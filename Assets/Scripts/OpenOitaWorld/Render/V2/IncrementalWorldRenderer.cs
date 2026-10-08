using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenOita.Contracts;
using OpenOita.V2;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OpenOita.V2.Render
{
    /// <summary>
    /// Incremental page renderer for the committed world.
    /// A tile handle is a stable slot; slot 0..127 share one 128-layer page.
    /// The renderer never reads a working simulation view.
    /// </summary>
    public sealed class IncrementalWorldRenderer : IDisposable
    {
        public const int TileSide = 128;
        public const int LayersPerPage = 128;
        public const int InitialTileCapacity = 512;
        public const int PaletteSize = 65536;
        public const int PagePixelCapacity = TileSide * TileSide * LayersPerPage;
        public const int InitialUpdateCapacity = 65536;
        public const int FireVerticesPerTile = 6;
        private const int BatchOperationCapacity = LayersPerPage * 16;

        private static readonly int UpdatesId = Shader.PropertyToID("_PixelUpdates");
        private static readonly int UpdateCountId = Shader.PropertyToID("_UpdateCount");
        private static readonly int PagePixelsId = Shader.PropertyToID("_PagePixels");
        private static readonly int ClearLayerId = Shader.PropertyToID("_ClearLayer");
        private static readonly int TileInstancesId = Shader.PropertyToID("_TileInstances");
        private static readonly int BodyPosesId = Shader.PropertyToID("_BodyPoses");
        private static readonly int PaletteId = Shader.PropertyToID("_Palette");
        private static readonly int PageIndexId = Shader.PropertyToID("_PageIndex");
        private static readonly int WorldRectId = Shader.PropertyToID("_WorldRect");
        private static readonly int WorldCullId = Shader.PropertyToID("_EnableWorldCull");

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct RenderPixelUpdate
        {
            public readonly uint Layer;
            public readonly uint PackedXY;
            public readonly uint PackedPixel;
            public readonly uint Reserved;

            public RenderPixelUpdate(uint layer, int x, int y, uint packedPixel)
            {
                Layer = layer;
                PackedXY = (uint)(x & 0xffff) | ((uint)(y & 0xffff) << 16);
                PackedPixel = packedPixel;
                Reserved = 0;
            }

            public int X => (int)(PackedXY & 0xffff);
            public int Y => (int)(PackedXY >> 16);
        }

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct RenderTileInstance
        {
            public readonly Vector4 PositionSize;
            public readonly Vector4 Rotation;
            public readonly uint Layer;
            public readonly uint Active;
            public readonly uint BodyHandle;
            public readonly uint HasBurning;

            public RenderTileInstance(Vector2 origin, float size, float angleRadians, uint layer, bool active,
                uint bodyHandle, bool hasBurning = false)
            {
                PositionSize = new Vector4(origin.x, origin.y, size, 0);
                Rotation = new Vector4(Mathf.Sin(angleRadians), Mathf.Cos(angleRadians), 0, 0);
                Layer = layer;
                Active = active ? 1u : 0u;
                BodyHandle = bodyHandle;
                HasBurning = hasBurning ? 1u : 0u;
            }
        }

        public readonly struct RenderBodyPose
        {
            public readonly int BodyHandle;
            public readonly BodyPose Pose;

            public RenderBodyPose(int bodyHandle, BodyPose pose)
            {
                BodyHandle = bodyHandle;
                Pose = pose;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct GpuBodyPose
        {
            public readonly Vector4 PositionRotation;

            public GpuBodyPose(BodyPose pose)
            {
                PositionRotation = new Vector4(pose.Position.x, pose.Position.y,
                    Mathf.Sin(pose.AngleRadians), Mathf.Cos(pose.AngleRadians));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct RenderPixelBatchOperation
        {
            public MaterialTile Tile;
            public int TileHandle;
            public int BaseX;
            public int BaseY;
            public int GridTileId;
            public int ColdLength;
            public int DefinitionLength;
            public int VisualTileLength;
            public byte Fresh;
            public byte Reserved0;
            public ushort Reserved1;
            public ulong CommittedTick;
            [NativeDisableUnsafePtrRestriction] public CellCold* ColdRecords;
            [NativeDisableUnsafePtrRestriction] public MaterialDefinition* Definitions;
            [NativeDisableUnsafePtrRestriction] public int* VisualTiles;
        }

        private struct TileState
        {
            public int BodyHandle;
            public Vector2 LocalOrigin;
            public float CellSize;
            public bool Active;
        }

        private sealed class Page : IDisposable
        {
            internal readonly int Index;
            internal readonly RenderTexture Pixels;
            internal readonly RTHandle PixelsHandle;
            internal GraphicsBuffer Updates;
            internal readonly GraphicsBuffer Instances;
            internal NativeList<RenderPixelUpdate> PendingUpdates;
            internal NativeList<int> FreeUpdateSlots;
            internal NativeArray<uint> PackedPixels;
            internal NativeArray<int> DirtySlots;
            internal NativeArray<int> LayerPendingCounts;
            internal NativeArray<int> LayerBurningCounts;
            internal NativeArray<RenderTileInstance> InstanceData;
            internal NativeArray<byte> ClearLayers;
            internal NativeArray<byte> BurningBits;
            internal NativeList<RenderPixelBatchOperation> BatchOperations;
            internal NativeArray<byte> BatchBurningChanged;
            internal NativeArray<int> BatchError;
            internal NativeArray<int> BatchBurningTotal;
            internal readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            internal int PendingUpdateCount;
            internal bool InstanceDirty;
            internal bool Queued;
            internal bool HasActiveTiles;
            internal bool HasBurning;
            internal int BurningCount;

            internal Page(int index, int updateCapacity)
            {
                Index = index;
                Pixels = new RenderTexture(TileSide, TileSide, 0, GraphicsFormat.R32_UInt)
                {
                    name = "OpenOita V2 像素页 " + index,
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = LayersPerPage,
                    enableRandomWrite = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                    autoGenerateMips = false,
                    hideFlags = HideFlags.DontSave
                };
                Pixels.Create();
                PixelsHandle = RTHandles.Alloc(Pixels);
                Updates = new GraphicsBuffer(GraphicsBuffer.Target.Structured, updateCapacity,
                    Marshal.SizeOf<RenderPixelUpdate>());
                Instances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, LayersPerPage,
                    Marshal.SizeOf<RenderTileInstance>());
                PendingUpdates = new NativeList<RenderPixelUpdate>(updateCapacity, Allocator.Persistent);
                FreeUpdateSlots = new NativeList<int>(updateCapacity, Allocator.Persistent);
                PackedPixels = new NativeArray<uint>(PagePixelCapacity, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                DirtySlots = new NativeArray<int>(PagePixelCapacity, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                LayerPendingCounts = new NativeArray<int>(LayersPerPage, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                LayerBurningCounts = new NativeArray<int>(LayersPerPage, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                InstanceData = new NativeArray<RenderTileInstance>(LayersPerPage, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                ClearLayers = new NativeArray<byte>(LayersPerPage, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                BurningBits = new NativeArray<byte>((TileSide * TileSide * LayersPerPage + 7) / 8,
                    Allocator.Persistent, NativeArrayOptions.ClearMemory);
                BatchOperations = new NativeList<RenderPixelBatchOperation>(BatchOperationCapacity, Allocator.Persistent);
                BatchBurningChanged = new NativeArray<byte>(LayersPerPage, Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);
                BatchError = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                BatchBurningTotal = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            }

            internal void ClearBatchOperations()
            {
                BatchOperations.Clear();
                for (int i = 0; i < BatchBurningChanged.Length; i++) BatchBurningChanged[i] = 0;
                BatchError[0] = 0;
            }

            internal void ClearPending()
            {
                for (int i = 0; i < PendingUpdates.Length; i++)
                {
                    RenderPixelUpdate update = PendingUpdates[i];
                    if (update.Layer == uint.MaxValue) continue;
                    DirtySlots[PixelIndex(update.Layer, update.X, update.Y)] = 0;
                }
                PendingUpdates.Clear();
                FreeUpdateSlots.Clear();
                for (int i = 0; i < LayerPendingCounts.Length; i++) LayerPendingCounts[i] = 0;
                PendingUpdateCount = 0;
                Queued = false;
                InstanceDirty = false;
                for (int i = 0; i < ClearLayers.Length; i++) ClearLayers[i] = 0;
            }

            internal bool SetBurning(int layer, int x, int y, bool burning)
            {
                int bit = (layer * TileSide + y) * TileSide + x;
                int index = bit >> 3;
                byte mask = (byte)(1 << (bit & 7));
                bool wasBurning = (BurningBits[index] & mask) != 0;
                if (wasBurning == burning) return false;
                if (burning)
                {
                    BurningBits[index] |= mask;
                    BurningCount++;
                    LayerBurningCounts[layer]++;
                }
                else
                {
                    BurningBits[index] &= (byte)~mask;
                    BurningCount--;
                    LayerBurningCounts[layer]--;
                }
                HasBurning = BurningCount > 0;
                return LayerBurningCounts[layer] == 1 || LayerBurningCounts[layer] == 0;
            }

            internal void WritePixel(int layer, int x, int y, uint packed)
            {
                int pixelIndex = PixelIndex((uint)layer, x, y);
                PackedPixels[pixelIndex] = packed;
                int slot = DirtySlots[pixelIndex] - 1;
                RenderPixelUpdate update = new RenderPixelUpdate((uint)layer, x, y, packed);
                if (slot >= 0)
                {
                    PendingUpdates[slot] = update;
                    return;
                }
                if (FreeUpdateSlots.Length > 0)
                {
                    slot = FreeUpdateSlots[FreeUpdateSlots.Length - 1];
                    FreeUpdateSlots.RemoveAt(FreeUpdateSlots.Length - 1);
                    PendingUpdates[slot] = update;
                }
                else
                {
                    if (PendingUpdates.Length >= PendingUpdates.Capacity)
                        throw new InvalidOperationException("V2像素更新超出稳定NativeList容量，请在帧屏障前扩容。");
                    slot = PendingUpdates.Length;
                    PendingUpdates.Add(update);
                }
                DirtySlots[pixelIndex] = slot + 1;
                LayerPendingCounts[layer]++;
            }

            internal int ClearPixelLayer(int layer)
            {
                if (LayerPendingCounts[layer] == 0)
                {
                    int emptyFirst = layer * TileSide * TileSide;
                    for (int i = 0; i < TileSide * TileSide; i++) PackedPixels[emptyFirst + i] = 0;
                    return 0;
                }

                int first = layer * TileSide * TileSide;
                for (int i = 0; i < TileSide * TileSide; i++)
                {
                    int pixelIndex = first + i;
                    int slot = DirtySlots[pixelIndex] - 1;
                    if (slot < 0) continue;
                    DirtySlots[pixelIndex] = 0;
                    PendingUpdates[slot] = EmptyUpdate;
                    FreeUpdateSlots.Add(slot);
                }
                LayerPendingCounts[layer] = 0;

                for (int i = 0; i < TileSide * TileSide; i++) PackedPixels[first + i] = 0;
                return TileSide * TileSide;
            }

            internal void CompactPendingUpdates()
            {
                if (FreeUpdateSlots.Length == 0) return;
                int write = 0;
                for (int read = 0; read < PendingUpdates.Length; read++)
                {
                    RenderPixelUpdate update = PendingUpdates[read];
                    if (update.Layer == uint.MaxValue) continue;
                    if (write != read)
                    {
                        PendingUpdates[write] = update;
                    }
                    int pixelIndex = PixelIndex(update.Layer, update.X, update.Y);
                    DirtySlots[pixelIndex] = write + 1;
                    write++;
                }
                PendingUpdates.ResizeUninitialized(write);
                FreeUpdateSlots.Clear();
            }

            private static readonly RenderPixelUpdate EmptyUpdate =
                new RenderPixelUpdate(uint.MaxValue, 0, 0, 0);

            private static int PixelIndex(uint layer, int x, int y) =>
                (int)layer * TileSide * TileSide + y * TileSide + x;

            internal void ReserveUpdateCapacity(int capacity)
            {
                if (capacity > PagePixelCapacity)
                    throw new ArgumentOutOfRangeException(nameof(capacity), "V2单页更新容量不能超过实际像素数。");
                if (capacity <= PendingUpdates.Capacity) return;
                PendingUpdates.Capacity = capacity;
                FreeUpdateSlots.Capacity = capacity;
                GraphicsBuffer replacement = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity,
                    Marshal.SizeOf<RenderPixelUpdate>());
                Updates.Dispose();
                Updates = replacement;
            }

            internal bool ClearBurningLayer(int layer)
            {
                int firstBit = layer * TileSide * TileSide;
                int firstByte = firstBit >> 3;
                int byteCount = TileSide * TileSide / 8;
                for (int i = 0; i < byteCount; i++)
                {
                    byte value = BurningBits[firstByte + i];
                    if (value == 0) continue;
                    BurningCount -= CountBits(value);
                    BurningBits[firstByte + i] = 0;
                }
                bool changed = LayerBurningCounts[layer] != 0;
                LayerBurningCounts[layer] = 0;
                HasBurning = BurningCount > 0;
                return changed;
            }

            private static int CountBits(byte value)
            {
                int count = 0;
                while (value != 0)
                {
                    value &= (byte)(value - 1);
                    count++;
                }
                return count;
            }

            public void Dispose()
            {
                if (PendingUpdates.IsCreated) PendingUpdates.Dispose();
                if (FreeUpdateSlots.IsCreated) FreeUpdateSlots.Dispose();
                if (PackedPixels.IsCreated) PackedPixels.Dispose();
                if (DirtySlots.IsCreated) DirtySlots.Dispose();
                if (LayerPendingCounts.IsCreated) LayerPendingCounts.Dispose();
                if (InstanceData.IsCreated) InstanceData.Dispose();
                if (ClearLayers.IsCreated) ClearLayers.Dispose();
                if (BurningBits.IsCreated) BurningBits.Dispose();
                if (LayerBurningCounts.IsCreated) LayerBurningCounts.Dispose();
                if (BatchOperations.IsCreated) BatchOperations.Dispose();
                if (BatchBurningChanged.IsCreated) BatchBurningChanged.Dispose();
                if (BatchError.IsCreated) BatchError.Dispose();
                if (BatchBurningTotal.IsCreated) BatchBurningTotal.Dispose();
                Updates?.Dispose();
                Instances?.Dispose();
                PixelsHandle?.Release();
                if (Pixels != null)
                {
                    Pixels.Release();
                    DestroyUnityObject(Pixels);
                }
            }
        }

        // A page is processed by one serial Burst job. The operation list is deliberately
        // grouped by page so the job can update the page's existing dedupe/free-slot tables
        // without atomics or a second managed per-pixel pass.
        [BurstCompile]
        private unsafe struct PixelBatchJob : IJob
        {
            [ReadOnly] public NativeArray<RenderPixelBatchOperation> Operations;
            public NativeList<RenderPixelUpdate> PendingUpdates;
            public NativeList<int> FreeUpdateSlots;
            public NativeArray<uint> PackedPixels;
            public NativeArray<int> DirtySlots;
            public NativeArray<int> LayerPendingCounts;
            public NativeArray<int> LayerBurningCounts;
            public NativeArray<byte> BurningBits;
            public NativeArray<byte> BurningChanged;
            public NativeArray<int> Error;
            public NativeArray<int> BurningTotal;
            public bool ValidateOnly;

            public void Execute()
            {
                int potentialUpdates = 0;
                for (int i = 0; i < Operations.Length; i++)
                {
                    RenderPixelBatchOperation operation = Operations[i];
                    for (int row = 0; row < 32; row++)
                    {
                        int count = math.countbits(operation.Fresh != 0
                            ? operation.Tile.Occupied[row] : operation.Tile.Visual[row]);
                        if (potentialUpdates > int.MaxValue - count)
                        {
                            Error[0] = 1;
                            return;
                        }
                        potentialUpdates += count;
                    }
                }
                int availableUpdates = PendingUpdates.Capacity - PendingUpdates.Length + FreeUpdateSlots.Length;
                if (potentialUpdates > availableUpdates)
                {
                    Error[0] = 1;
                    return;
                }
                if (ValidateOnly) return;

                for (int i = 0; i < Operations.Length; i++)
                {
                    RenderPixelBatchOperation operation = Operations[i];
                    int layer = operation.TileHandle % LayersPerPage;
                    for (int row = 0; row < 32; row++)
                    {
                        uint bits = operation.Fresh != 0 ? operation.Tile.Occupied[row] : operation.Tile.Visual[row];
                        while (bits != 0)
                        {
                            int column = (int)math.tzcnt(bits);
                            bits &= bits - 1;
                            int position = row * 32 + column;
                            ushort material = operation.Tile.Material[position];
                            bool burning = material != 0 &&
                                (operation.Tile.Flags[position] & GridCell.BurningFlag) != 0;
                            byte fuel = 255;
                            if (burning)
                            {
                                fuel = 0;
                                uint totalFuel = 0;
                                if ((uint)material < (uint)operation.DefinitionLength)
                                    totalFuel = operation.Definitions[material].Fuel;
                                CellCold cold = default;
                                int component = operation.Tile.Component[position];
                                if (component > 0 && component <= operation.ColdLength)
                                {
                                    cold = operation.ColdRecords[component - 1];
                                    if (cold.MaterialId == 0) cold = default;
                                }
                                ulong remaining = cold.BurnEndTick > operation.CommittedTick
                                    ? cold.BurnEndTick - operation.CommittedTick : 0;
                                if (totalFuel != 0)
                                    fuel = (byte)math.min((ulong)255, remaining * 255UL / totalFuel);
                            }

                            int x = operation.BaseX + column;
                            int y = operation.BaseY + row;
                            uint packed = material == 0 ? 0u :
                                material | (burning ? 1u << 16 : 0) | ((uint)fuel << 17);
                            WritePixel(layer, x, y, packed);
                            SetBurning(layer, x, y, burning);
                        }
                        // Match the former Paint barrier: once a material tile was visited,
                        // every visual row is consumed, including rows that had no cells.
                        operation.Tile.Visual[row] = 0;
                    }
                    if ((uint)operation.GridTileId < (uint)operation.VisualTileLength)
                        operation.VisualTiles[operation.GridTileId] = 0;
                }
            }

            private void WritePixel(int layer, int x, int y, uint packed)
            {
                int pixelIndex = layer * TileSide * TileSide + y * TileSide + x;
                PackedPixels[pixelIndex] = packed;
                int slot = DirtySlots[pixelIndex] - 1;
                RenderPixelUpdate update = new RenderPixelUpdate((uint)layer, x, y, packed);
                if (slot >= 0)
                {
                    PendingUpdates[slot] = update;
                    return;
                }
                if (FreeUpdateSlots.Length > 0)
                {
                    int freeIndex = FreeUpdateSlots.Length - 1;
                    slot = FreeUpdateSlots[freeIndex];
                    FreeUpdateSlots.RemoveAt(freeIndex);
                    PendingUpdates[slot] = update;
                }
                else
                {
                    if (PendingUpdates.Length >= PendingUpdates.Capacity)
                    {
                        Error[0] = 1;
                        return;
                    }
                    slot = PendingUpdates.Length;
                    PendingUpdates.AddNoResize(update);
                }
                DirtySlots[pixelIndex] = slot + 1;
                LayerPendingCounts[layer]++;
            }

            private void SetBurning(int layer, int x, int y, bool burning)
            {
                int bit = (layer * TileSide + y) * TileSide + x;
                int index = bit >> 3;
                byte mask = (byte)(1 << (bit & 7));
                bool wasBurning = (BurningBits[index] & mask) != 0;
                if (wasBurning == burning) return;
                if (burning)
                {
                    BurningBits[index] |= mask;
                    BurningTotal[0]++;
                    LayerBurningCounts[layer]++;
                }
                else
                {
                    BurningBits[index] &= (byte)~mask;
                    BurningTotal[0]--;
                    LayerBurningCounts[layer]--;
                }
                if (LayerBurningCounts[layer] == 1 || LayerBurningCounts[layer] == 0)
                    BurningChanged[layer] = 1;
            }
        }

        private readonly ComputeShader _updateShader;
        private readonly Shader _tileShader;
        private readonly Shader _fireShader;
        private readonly List<Page> _pages = new List<Page>(InitialTileCapacity / LayersPerPage);
        private NativeArray<GpuBodyPose> _bodyPoseData;
        private NativeArray<byte> _bodyPoseDirty;
        private GraphicsBuffer _bodyPoseBuffer;
        private int _bodyPoseDirtyMin;
        private int _bodyPoseDirtyMax;
        private int _bodyPoseDirtyCount;
        private TileState[] _tiles = new TileState[InitialTileCapacity];
        private int _tileCount;
        private Texture2D _palette;
        private Material _tileMaterial;
        private Material _fireMaterial;
        private int _applyKernel = -1;
        private int _clearKernel = -1;
        private bool _initialized;
        private bool _frameOpen;
        private bool _disposed;
        private bool _hasQueuedFrame;
        private ulong _frameVersion;
        private int _updateCapacity = InitialUpdateCapacity;
        private int _activeTileCount;
        private long _clearPixelLayerProbeCount;

        public ulong QueuedVersion { get; private set; }
        public ulong UploadedVersion { get; private set; }
        /// <summary>最后一个已记录到当前相机RenderGraph的成功Tick。</summary>
        public ulong VisibleVersion => UploadedVersion;
        public int PageCount => _pages.Count;
        public int Capacity => _tiles.Length;
        public int ActiveTileCount => _activeTileCount;
        public int BodyPoseCapacity => _bodyPoseData.IsCreated ? _bodyPoseData.Length : 0;
        public Texture2D PaletteTexture => _palette;
        public int PendingPixelUpdateCount { get; private set; }
        public int LastFramePixelUpdates { get; private set; }
        public int LastFramePoseUpdates { get; private set; }
        public int PendingBodyPoseUpdateCount { get; private set; }
        public double LastDrawSubmissionMs { get; private set; }
        /// <summary>提交的火焰绘制顶点数；每个有火页为6顶点×128实例。</summary>
        public int EstimatedFireVertexCount
        {
            get
            {
                int pages = 0;
                foreach (Page page in _pages) if (page.HasBurning) pages++;
                return pages * FireVerticesPerTile * LayersPerPage;
            }
        }
        /// <summary>累计清层访问的像素槽数量，用于验证清层不会扫描整页更新列表。</summary>
        public long ClearPixelLayerProbeCount => _clearPixelLayerProbeCount;
        /// <summary>是否已有一帧等待相机把RenderGraph记录到GPU。</summary>
        public bool HasQueuedFrame => _hasQueuedFrame || _frameOpen;
        public int DisplayLayer { get; set; }
        public event Action<Camera> PrepareCamera;

        public IncrementalWorldRenderer(ComputeShader updateShader = null, Shader tileShader = null,
            Shader fireShader = null)
        {
            _updateShader = updateShader;
            _tileShader = tileShader;
            _fireShader = fireShader;
        }

        public void Initialize(Color32[] palette)
        {
            RequireNotDisposed();
            if (_initialized) throw new InvalidOperationException("V2显示器已初始化。");
            if (_updateShader == null)
                throw new InvalidOperationException("V2 ComputeShader资源缺失，禁止静默跳过像素上传。");
            if (palette == null || palette.Length == 0 || palette.Length > PaletteSize)
                throw new ArgumentException("材质调色板必须为非空且不超过65536色。", nameof(palette));

            Color32[] fullPalette = new Color32[PaletteSize];
            Array.Copy(palette, fullPalette, palette.Length);
            _palette = new Texture2D(256, 256, TextureFormat.RGBA32, false, true)
            {
                name = "OpenOita V2 材质调色板",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            _palette.SetPixels32(fullPalette);
            _palette.Apply(false, false);

            Shader tileShader = _tileShader != null ? _tileShader : Shader.Find("OpenOita/V2/WorldPage");
            Shader fireShader = _fireShader != null ? _fireShader : Shader.Find("OpenOita/V2/WorldFire");
            if (tileShader == null) throw new InvalidOperationException("V2材料Shader缺失。");
            _tileMaterial = new Material(tileShader) { name = "OpenOita V2 材料", hideFlags = HideFlags.DontSave };
            _tileMaterial.enableInstancing = true;
            _fireMaterial = fireShader == null ? null :
                new Material(fireShader) { name = "OpenOita V2 火焰", hideFlags = HideFlags.DontSave };
            if (_fireMaterial != null) _fireMaterial.enableInstancing = true;
            _applyKernel = _updateShader.FindKernel("ApplyUpdates");
            _clearKernel = _updateShader.FindKernel("ClearSlice");
            for (int i = 0; i < InitialTileCapacity / LayersPerPage; i++)
                _pages.Add(new Page(i, _updateCapacity));
            _bodyPoseData = new NativeArray<GpuBodyPose>(InitialTileCapacity, Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _bodyPoseDirty = new NativeArray<byte>(InitialTileCapacity, Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            _bodyPoseBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, InitialTileCapacity,
                Marshal.SizeOf<GpuBodyPose>());
            _bodyPoseBuffer.SetData(_bodyPoseData);
            _bodyPoseDirtyMin = InitialTileCapacity;
            _bodyPoseDirtyMax = -1;
            _bodyPoseDirtyCount = 0;
            _initialized = true;
        }

        public static uint PackPixel(ushort materialId, bool burning, byte fuelBin)
        {
            return materialId | (burning ? 1u << 16 : 0) | ((uint)fuelBin << 17);
        }

        public static uint PackPixelMaterial(ushort materialId, bool burning, byte fuelBin) =>
            PackPixel(materialId, burning, fuelBin);

        public static ushort UnpackMaterialId(uint packed) => (ushort)(packed & 0xffff);
        public static bool UnpackBurning(uint packed) => (packed & (1u << 16)) != 0;
        public static byte UnpackFuelBin(uint packed) => (byte)((packed >> 17) & 0xff);

        public void BeginFrame(ulong version)
        {
            RequireReady();
            if (_frameOpen) throw new InvalidOperationException("V2显示帧已开启。");
            _frameVersion = version;
            foreach (Page page in _pages) page.ClearBatchOperations();
            // A hidden GameView/editor camera may not record the previous graph. Keep that
            // unsubmitted batch and append the next committed Tick to it; only RecordGraph
            // clears the batch after the final version has been scheduled for the GPU.
            if (!_hasQueuedFrame)
                foreach (Page page in _pages) page.ClearPending();
            _frameOpen = true;
        }

        /// <summary>
        /// Reserves all steady-frame storage at a frame barrier. This must be called after
        /// Initialize and before BeginFrame; no NativeList/GraphicsBuffer growth occurs in a
        /// normal upload path. pixelCount is the maximum dirty updates for one page in a frame.
        /// </summary>
        public void ReserveFrameCapacity(int pixelCount, int tileCapacity, int bodyCapacity)
        {
            RequireReady();
            if (_frameOpen)
                throw new InvalidOperationException("V2容量只能在显示帧屏障扩容。");
            if (pixelCount < 0 || tileCapacity < 0 || bodyCapacity < 0)
                throw new ArgumentOutOfRangeException();
            if (pixelCount > PagePixelCapacity)
                throw new ArgumentOutOfRangeException(nameof(pixelCount), "V2单页像素更新不能超过128×128×128。");

            int requestedUpdates = Mathf.Max(InitialUpdateCapacity, Mathf.Max(pixelCount, 1));
            int updateCapacity = Mathf.NextPowerOfTwo(requestedUpdates);
            if (updateCapacity > _updateCapacity)
            {
                _updateCapacity = updateCapacity;
                foreach (Page page in _pages) page.ReserveUpdateCapacity(_updateCapacity);
            }

            if (tileCapacity > _tiles.Length) EnsureTileCapacity(tileCapacity);
            int requiredPages = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(tileCapacity, InitialTileCapacity) / (float)LayersPerPage));
            while (_pages.Count < requiredPages) _pages.Add(new Page(_pages.Count, _updateCapacity));

            int requestedBodies = Mathf.Max(InitialTileCapacity, Mathf.Max(bodyCapacity, 1));
            if (requestedBodies > _bodyPoseData.Length)
                ResizeBodyPoseCapacity(Mathf.NextPowerOfTwo(requestedBodies));
        }

        /// <param name="localOrigin">Tile lower-left in body-local world units.</param>
        /// <param name="cellSize">One material pixel in world units.</param>
        public void SetTile(int tileHandle, int bodyHandle, Vector2 localOrigin, float cellSize)
        {
            RequireFrame();
            if (tileHandle < 0 || bodyHandle < 0 || !ContractDefaults.IsFinite(cellSize) || cellSize <= 0)
                throw new ArgumentOutOfRangeException();
            if ((uint)bodyHandle >= (uint)_bodyPoseData.Length)
                throw new ArgumentOutOfRangeException(nameof(bodyHandle), "V2身体句柄超出初始化Pose缓冲。");
            if (tileHandle >= _tiles.Length)
                throw new InvalidOperationException("V2 Tile容量不足，请在BeginFrame前调用ReserveFrameCapacity。");
            Page page = PageFor(tileHandle);
            int layer = tileHandle % LayersPerPage;
            bool wasActive = _tiles[tileHandle].Active;
            _tiles[tileHandle] = new TileState { Active = true, BodyHandle = bodyHandle, LocalOrigin = localOrigin, CellSize = cellSize };
            page.ClearLayers[layer] = 1;
            _clearPixelLayerProbeCount += page.ClearPixelLayer(layer);
            page.ClearBurningLayer(layer);
            page.InstanceData[layer] = BuildInstance(tileHandle);
            page.InstanceDirty = true;
            page.HasActiveTiles = true;
            if (!wasActive)
            {
                _tileCount = Mathf.Max(_tileCount, tileHandle + 1);
                _activeTileCount++;
            }
        }

        /// <summary>
        /// Geometry-edit entry point for a newly extracted dynamic body tile. The caller keeps the
        /// returned handle and uses WritePixel or AddPixelBatchOperation for subsequent visual
        /// deltas; ordinary simulation ticks should keep using an existing handle through SetTile.
        /// </summary>
        public int AddBodyTile(int bodyHandle, Vector2 localOrigin, float cellSize)
        {
            RequireFrame();
            int handle = FindFreeTileHandle();
            SetTile(handle, bodyHandle, localOrigin, cellSize);
            return handle;
        }

        public void WritePixel(int tileHandle, int x, int y, uint packed)
        {
            RequireFrame();
            if (tileHandle < 0 || tileHandle >= _tiles.Length || !_tiles[tileHandle].Active)
                throw new InvalidOperationException("像素写入目标Tile未注册。");
            if ((uint)x >= TileSide || (uint)y >= TileSide)
                throw new ArgumentOutOfRangeException();
            Page page = PageFor(tileHandle);
            // NativeList is deliberately never allowed to grow during a steady upload. A host
            // that needs more capacity must reserve it at its frame barrier before retrying.
            int layer = tileHandle % LayersPerPage;
            page.WritePixel(layer, x, y, packed);
            if (page.SetBurning(layer, x, y, UnpackBurning(packed)))
            {
                page.InstanceData[layer] = BuildInstance(tileHandle, page.LayerBurningCounts[layer] != 0);
                page.InstanceDirty = true;
            }
            page.HasActiveTiles = true;
        }

        /// <summary>
        /// Queues one 32×32 material tile for the page-local Burst display pass. The operation
        /// owns only unmanaged pointers into the caller's persistent grid storage; the job runs
        /// synchronously at FlushFrame before the world can mutate those arrays.
        /// </summary>
        public unsafe void AddPixelBatchOperation(int tileHandle, int baseX, int baseY, bool fresh,
            MaterialTile tile, int gridTileId, NativeArray<MaterialDefinition> definitions,
            NativeArray<CellCold> coldRecords, NativeArray<int> visualTiles, ulong committedTick)
        {
            RequireFrame();
            if (tileHandle < 0 || tileHandle >= _tiles.Length || !_tiles[tileHandle].Active)
                throw new InvalidOperationException("像素批处理目标Tile未注册。");
            if ((uint)baseX >= TileSide || (uint)baseY >= TileSide ||
                (baseX & 31) != 0 || (baseY & 31) != 0)
                throw new ArgumentOutOfRangeException(nameof(baseX), "像素批处理局部原点必须是32格对齐坐标。");
            if (gridTileId < 0 || gridTileId >= visualTiles.Length)
                throw new ArgumentOutOfRangeException(nameof(gridTileId));
            Page page = PageFor(tileHandle);
            if (page.BatchOperations.Length >= page.BatchOperations.Capacity)
                throw new InvalidOperationException("V2页批处理操作超出稳定容量，请在帧屏障前扩容。");
            RenderPixelBatchOperation operation = new RenderPixelBatchOperation
            {
                Tile = tile,
                TileHandle = tileHandle,
                BaseX = baseX,
                BaseY = baseY,
                GridTileId = gridTileId,
                ColdLength = coldRecords.Length,
                DefinitionLength = definitions.Length,
                VisualTileLength = visualTiles.Length,
                Fresh = fresh ? (byte)1 : (byte)0,
                CommittedTick = committedTick,
                ColdRecords = (CellCold*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(coldRecords),
                Definitions = (MaterialDefinition*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(definitions),
                VisualTiles = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(visualTiles)
            };
            page.BatchOperations.AddNoResize(operation);
            page.HasActiveTiles = true;
        }

        public bool TryReadCachedPixel(int tileHandle, int x, int y, out uint packed)
        {
            RequireReady();
            packed = 0;
            if (tileHandle < 0 || tileHandle >= _tiles.Length || !_tiles[tileHandle].Active ||
                (uint)x >= TileSide || (uint)y >= TileSide)
                return false;
            Page page = PageFor(tileHandle);
            int index = (tileHandle % LayersPerPage) * TileSide * TileSide + y * TileSide + x;
            packed = page.PackedPixels[index];
            return true;
        }

        public void SetBodyPose(int bodyHandle, BodyPose pose)
        {
            RequireFrame();
            if (bodyHandle < 0 || !ContractDefaults.IsFinite(pose.Position) || !ContractDefaults.IsFinite(pose.AngleRadians))
                throw new ArgumentOutOfRangeException(nameof(bodyHandle));
            if ((uint)bodyHandle >= (uint)_bodyPoseData.Length)
                throw new ArgumentOutOfRangeException(nameof(bodyHandle), "V2身体句柄超出初始化Pose缓冲。");
            GpuBodyPose next = new GpuBodyPose(pose);
            if (PoseEquals(_bodyPoseData[bodyHandle], next)) return;
            _bodyPoseData[bodyHandle] = next;
            if (_bodyPoseDirty[bodyHandle] == 0)
            {
                _bodyPoseDirty[bodyHandle] = 1;
                _bodyPoseDirtyCount++;
            }
            _bodyPoseDirtyMin = Mathf.Min(_bodyPoseDirtyMin, bodyHandle);
            _bodyPoseDirtyMax = Mathf.Max(_bodyPoseDirtyMax, bodyHandle);
        }

        public void RemoveTile(int tileHandle)
        {
            RequireFrame();
            if (tileHandle < 0 || tileHandle >= _tiles.Length) return;
            if (!_tiles[tileHandle].Active) return;
            _tiles[tileHandle].Active = false;
            _activeTileCount--;
            Page page = PageFor(tileHandle);
            page.InstanceData[tileHandle % LayersPerPage] = BuildInstance(tileHandle);
            page.InstanceDirty = true;
            page.ClearLayers[tileHandle % LayersPerPage] = 1;
            _clearPixelLayerProbeCount += page.ClearPixelLayer(tileHandle % LayersPerPage);
            page.ClearBurningLayer(tileHandle % LayersPerPage);
            page.HasActiveTiles = HasActiveTiles(page);
        }

        public void FlushFrame()
        {
            RequireFrame();
            try
            {
                // Validate every page before any page mutates its cache or consumes Visual bits.
                foreach (Page page in _pages)
                    if (page.BatchOperations.Length != 0) ProcessPixelBatch(page, true);

                int pendingTotal = 0;
                foreach (Page page in _pages)
                {
                    ProcessPixelBatch(page, false);
                    page.CompactPendingUpdates();
                    int count = page.PendingUpdates.Length;
                    bool hasClear = false;
                    for (int i = 0; i < page.ClearLayers.Length; i++) hasClear |= page.ClearLayers[i] != 0;
                    if (count == 0 && !page.InstanceDirty && !hasClear) continue;
                    if (count > page.Updates.count)
                        throw new InvalidOperationException("V2像素更新超出稳定GraphicsBuffer容量，请在帧屏障前扩容。");
                    if (count > 0) page.Updates.SetData(page.PendingUpdates.AsArray(), 0, 0, count);
                    if (page.InstanceDirty) page.Instances.SetData(page.InstanceData);
                    page.PendingUpdateCount = count;
                    page.Queued = true;
                    pendingTotal += count;
                }
                if (_bodyPoseDirtyMax >= _bodyPoseDirtyMin)
                    _bodyPoseBuffer.SetData(_bodyPoseData, _bodyPoseDirtyMin, _bodyPoseDirtyMin,
                        _bodyPoseDirtyMax - _bodyPoseDirtyMin + 1);
                _frameOpen = false;
                _hasQueuedFrame = true;
                QueuedVersion = _frameVersion;
                PendingPixelUpdateCount = pendingTotal;
                LastFramePixelUpdates = pendingTotal;
                LastFramePoseUpdates = _bodyPoseDirtyCount;
                PendingBodyPoseUpdateCount = _bodyPoseDirtyCount;
            }
            catch
            {
                // A failed preflight leaves all Visual/cache state untouched and closes the
                // frame so the caller can reserve more capacity and re-enter the barrier.
                _frameOpen = false;
                throw;
            }
        }

        private void ProcessPixelBatch(Page page, bool validateOnly)
        {
            if (page.BatchOperations.Length == 0) return;
            page.BatchError[0] = 0;
            page.BatchBurningTotal[0] = page.BurningCount;
            PixelBatchJob job = new PixelBatchJob
            {
                Operations = page.BatchOperations.AsArray(),
                PendingUpdates = page.PendingUpdates,
                FreeUpdateSlots = page.FreeUpdateSlots,
                PackedPixels = page.PackedPixels,
                DirtySlots = page.DirtySlots,
                LayerPendingCounts = page.LayerPendingCounts,
                LayerBurningCounts = page.LayerBurningCounts,
                BurningBits = page.BurningBits,
                BurningChanged = page.BatchBurningChanged,
                Error = page.BatchError,
                BurningTotal = page.BatchBurningTotal,
                ValidateOnly = validateOnly
            };
            job.Run();
            if (page.BatchError[0] != 0)
                throw new InvalidOperationException("V2像素批处理超出稳定NativeList容量，请在帧屏障前扩容。");
            if (validateOnly) return;

            page.BurningCount = page.BatchBurningTotal[0];
            page.HasBurning = page.BurningCount > 0;
            for (int layer = 0; layer < page.BatchBurningChanged.Length; layer++)
            {
                if (page.BatchBurningChanged[layer] == 0) continue;
                int tileHandle = page.Index * LayersPerPage + layer;
                if ((uint)tileHandle < (uint)_tiles.Length && _tiles[tileHandle].Active)
                {
                    page.InstanceData[layer] = BuildInstance(tileHandle, page.LayerBurningCounts[layer] != 0);
                    page.InstanceDirty = true;
                }
            }
            page.ClearBatchOperations();
        }

        /// <summary>Called by WorldRenderFeatureV2 while recording the camera graph.</summary>
        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (!_initialized || _disposed) return;
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;
            if (camera != null && DisplayLayer >= 0 && DisplayLayer < 32 &&
                (camera.cullingMask & (1 << DisplayLayer)) == 0)
                return;
            if (camera != null) PrepareCamera?.Invoke(camera);
            // PrepareCamera may synchronously ResetDisplay and dispose this instance when the
            // world generation changed. Do not touch disposed pages or buffers afterwards.
            if (_disposed) return;

            bool hasQueuedFrame = _hasQueuedFrame;
            if (hasQueuedFrame)
            {
                foreach (Page page in _pages)
                {
                    if (!page.Queued) continue;
                    for (int layer = 0; layer < page.ClearLayers.Length; layer++)
                    {
                        if (page.ClearLayers[layer] == 0) continue;
                        AddClearPass(renderGraph, page, layer);
                    }
                    if (page.PendingUpdateCount > 0) AddUpdatePass(renderGraph, page);
                }
            }
            AddDrawPass(renderGraph, frameData);
            if (hasQueuedFrame)
            {
                CompleteQueuedFrameState();
            }
        }

        // The production RenderGraph path calls this after successful pass recording. Tests use
        // the same completion barrier after PrepareForCamera to model that successful record.
        internal void CompleteFrameAfterRenderGraphRecording() => CompleteQueuedFrameState();

        private void CompleteQueuedFrameState()
        {
            if (!_hasQueuedFrame) return;
            foreach (Page page in _pages) if (page.Queued) page.ClearPending();
            for (int i = _bodyPoseDirtyMin; i <= _bodyPoseDirtyMax; i++) _bodyPoseDirty[i] = 0;
            _bodyPoseDirtyMin = _bodyPoseData.Length;
            _bodyPoseDirtyMax = -1;
            _bodyPoseDirtyCount = 0;
            PendingBodyPoseUpdateCount = 0;
            PendingPixelUpdateCount = 0;
            _hasQueuedFrame = false;
            UploadedVersion = QueuedVersion;
        }

        private void AddClearPass(RenderGraph renderGraph, Page page, int layer)
        {
            using var builder = renderGraph.AddUnsafePass<ComputePassData>("OpenOita V2 ClearTile", out var data);
            data.Renderer = this; data.Page = page; data.Layer = layer; data.Update = false;
            data.Pixels = renderGraph.ImportTexture(page.PixelsHandle);
            builder.UseTexture(data.Pixels, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (ComputePassData pass, UnsafeGraphContext context) => ExecuteCompute(pass, context));
        }

        private void AddUpdatePass(RenderGraph renderGraph, Page page)
        {
            using var builder = renderGraph.AddUnsafePass<ComputePassData>("OpenOita V2 ApplyPixels", out var data);
            data.Renderer = this; data.Page = page; data.Layer = page.PendingUpdateCount; data.Update = true;
            data.Pixels = renderGraph.ImportTexture(page.PixelsHandle);
            data.Updates = renderGraph.ImportBuffer(page.Updates);
            builder.UseTexture(data.Pixels, AccessFlags.ReadWrite);
            builder.UseBuffer(data.Updates, AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.SetRenderFunc(static (ComputePassData pass, UnsafeGraphContext context) => ExecuteCompute(pass, context));
        }

        private sealed class ComputePassData
        {
            internal IncrementalWorldRenderer Renderer;
            internal Page Page;
            internal int Layer;
            internal bool Update;
            internal TextureHandle Pixels;
            internal BufferHandle Updates;
        }

        private static void ExecuteCompute(ComputePassData data, UnsafeGraphContext context)
        {
            CommandBuffer command = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
            int kernel = data.Update ? data.Renderer._applyKernel : data.Renderer._clearKernel;
            command.SetComputeTextureParam(data.Renderer._updateShader, kernel, PagePixelsId, data.Pixels);
            if (data.Update)
            {
                command.SetComputeBufferParam(data.Renderer._updateShader, kernel, UpdatesId, data.Updates);
                command.SetComputeIntParam(data.Renderer._updateShader, UpdateCountId, data.Layer);
            }
            else command.SetComputeIntParam(data.Renderer._updateShader, ClearLayerId, data.Layer);
            command.DispatchCompute(data.Renderer._updateShader, kernel,
                data.Update ? Mathf.CeilToInt(data.Layer / 64f) : 16, data.Update ? 1 : 16, 1);
        }

        private void AddDrawPass(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            using var builder = renderGraph.AddUnsafePass<DrawPassData>("OpenOita V2 DrawPages", out var data);
            data.Renderer = this;
            data.Color = resourceData.activeColorTexture;
            data.WorldRect = ResolveCameraWorldRect(frameData, out data.CullToCamera);
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            // Unsafe RenderGraph passes do not promise that the camera built-ins are
            // still bound after an intervening 2D pass or a render-request target
            // switch. Capture the exact URP camera matrices for this graph here and
            // bind them immediately before the procedural draws below.
            data.ViewMatrix = cameraData.GetViewMatrix();
            data.ProjectionMatrix = cameraData.GetProjectionMatrix();
            builder.UseTexture(data.Color, AccessFlags.WriteAll);
            foreach (Page page in _pages)
            {
                if (!page.HasActiveTiles) continue;
                builder.UseTexture(renderGraph.ImportTexture(page.PixelsHandle), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(page.Instances), AccessFlags.Read);
            }
            builder.UseBuffer(renderGraph.ImportBuffer(_bodyPoseBuffer), AccessFlags.Read);
            builder.AllowPassCulling(false);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (DrawPassData pass, UnsafeGraphContext context) => ExecuteDraw(pass, context));
        }

        private sealed class DrawPassData
        {
            internal IncrementalWorldRenderer Renderer;
            internal TextureHandle Color;
            internal Vector4 WorldRect;
            internal bool CullToCamera;
            internal Matrix4x4 ViewMatrix;
            internal Matrix4x4 ProjectionMatrix;
        }

        private static Vector4 ResolveCameraWorldRect(ContextContainer frameData, out bool valid)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;
            if (camera == null || !camera.orthographic)
            {
                valid = false;
                return Vector4.zero;
            }

            float aspect = camera.aspect;
            if (!ContractDefaults.IsFinite(aspect) || aspect <= 0)
            {
                aspect = cameraData.cameraTargetDescriptor.height > 0
                    ? cameraData.cameraTargetDescriptor.width / (float)cameraData.cameraTargetDescriptor.height
                    : 1;
            }
            float halfHeight = camera.orthographicSize;
            float halfWidth = halfHeight * aspect;
            Vector3 center = camera.transform.position;
            valid = ContractDefaults.IsFinite(halfWidth) && ContractDefaults.IsFinite(halfHeight) &&
                halfWidth > 0 && halfHeight > 0;
            return new Vector4(center.x - halfWidth, center.y - halfHeight,
                center.x + halfWidth, center.y + halfHeight);
        }

        private static void ExecuteDraw(DrawPassData data, UnsafeGraphContext context)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                context.cmd.SetRenderTarget(data.Color);
                // SetViewProjectionMatrices updates UNITY_MATRIX_V/P/VP used by
                // TransformWorldToHClip in WorldPage and WorldFire. Do this after
                // binding the active camera color target so SingleCameraRequest and
                // ordinary camera rendering use the same world-to-screen transform.
                context.cmd.SetViewProjectionMatrices(data.ViewMatrix, data.ProjectionMatrix);
                foreach (Page page in data.Renderer._pages)
                {
                    if (!page.HasActiveTiles) continue;
                    page.Properties.Clear();
                    page.Properties.SetTexture(PagePixelsId, page.Pixels);
                    page.Properties.SetBuffer(TileInstancesId, page.Instances);
                    page.Properties.SetBuffer(BodyPosesId, data.Renderer._bodyPoseBuffer);
                    page.Properties.SetTexture(PaletteId, data.Renderer._palette);
                    page.Properties.SetInt(PageIndexId, page.Index);
                    page.Properties.SetVector(WorldRectId, data.WorldRect);
                    page.Properties.SetFloat(WorldCullId, data.CullToCamera ? 1 : 0);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Renderer._tileMaterial, 0,
                        MeshTopology.Triangles, 6, LayersPerPage, page.Properties);
                    if (data.Renderer._fireMaterial == null || !page.HasBurning) continue;
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Renderer._fireMaterial, 0,
                        MeshTopology.Triangles, FireVerticesPerTile, LayersPerPage, page.Properties);
                }
            }
            finally
            {
                data.Renderer.LastDrawSubmissionMs =
                    (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
        }

        private RenderTileInstance BuildInstance(int tileHandle, bool hasBurning = false)
        {
            TileState state = _tiles[tileHandle];
            float s = TileSide * state.CellSize;
            return new RenderTileInstance(state.LocalOrigin, s, 0, (uint)(tileHandle % LayersPerPage), state.Active,
                (uint)state.BodyHandle, hasBurning);
        }

        private Page PageFor(int tileHandle)
        {
            int pageIndex = tileHandle / LayersPerPage;
            if (pageIndex >= _pages.Count)
                throw new InvalidOperationException("V2页缓存不足，请在BeginFrame前调用ReserveFrameCapacity。");
            return _pages[pageIndex];
        }

        private void ResizeBodyPoseCapacity(int capacity)
        {
            NativeArray<GpuBodyPose> replacementData = new NativeArray<GpuBodyPose>(capacity,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            NativeArray<byte> replacementDirty = new NativeArray<byte>(capacity,
                Allocator.Persistent, NativeArrayOptions.ClearMemory);
            NativeArray<GpuBodyPose>.Copy(_bodyPoseData, replacementData, _bodyPoseData.Length);
            NativeArray<byte>.Copy(_bodyPoseDirty, replacementDirty, _bodyPoseDirty.Length);
            GraphicsBuffer replacementBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity,
                Marshal.SizeOf<GpuBodyPose>());
            replacementBuffer.SetData(replacementData);
            _bodyPoseData.Dispose();
            _bodyPoseDirty.Dispose();
            _bodyPoseBuffer.Dispose();
            _bodyPoseData = replacementData;
            _bodyPoseDirty = replacementDirty;
            _bodyPoseBuffer = replacementBuffer;
            _bodyPoseDirtyMin = capacity;
            _bodyPoseDirtyMax = -1;
            _bodyPoseDirtyCount = 0;
            for (int i = 0; i < replacementDirty.Length; i++)
            {
                if (replacementDirty[i] == 0) continue;
                _bodyPoseDirtyCount++;
                _bodyPoseDirtyMin = Mathf.Min(_bodyPoseDirtyMin, i);
                _bodyPoseDirtyMax = Mathf.Max(_bodyPoseDirtyMax, i);
            }
        }

        private int FindFreeTileHandle()
        {
            for (int i = 0; i < _tileCount; i++)
                if (!_tiles[i].Active) return i;
            return _tileCount;
        }

        private void EnsureTileCapacity(int required)
        {
            if (required <= _tiles.Length) return;
            int capacity = Mathf.NextPowerOfTwo(required);
            Array.Resize(ref _tiles, capacity);
        }

        private bool HasActiveTiles(Page page)
        {
            int start = page.Index * LayersPerPage;
            int end = Mathf.Min(start + LayersPerPage, _tileCount);
            for (int i = start; i < end; i++) if (_tiles[i].Active) return true;
            return false;
        }

        private void RequireFrame()
        {
            RequireReady();
            if (!_frameOpen) throw new InvalidOperationException("V2显示操作必须在BeginFrame/FlushFrame之间执行。");
        }

        private void RequireReady()
        {
            RequireNotDisposed();
            if (!_initialized) throw new InvalidOperationException("V2显示器尚未初始化。");
        }

        private void RequireNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IncrementalWorldRenderer));
        }

        private static bool PoseEquals(in GpuBodyPose left, in GpuBodyPose right)
        {
            return left.PositionRotation.x == right.PositionRotation.x &&
                left.PositionRotation.y == right.PositionRotation.y &&
                left.PositionRotation.z == right.PositionRotation.z &&
                left.PositionRotation.w == right.PositionRotation.w;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Page page in _pages) page.Dispose();
            _pages.Clear();
            if (_bodyPoseData.IsCreated) _bodyPoseData.Dispose();
            if (_bodyPoseDirty.IsCreated) _bodyPoseDirty.Dispose();
            _bodyPoseBuffer?.Dispose();
            PrepareCamera = null;
            if (_palette != null) DestroyUnityObject(_palette);
            if (_tileMaterial != null) DestroyUnityObject(_tileMaterial);
            if (_fireMaterial != null) DestroyUnityObject(_fireMaterial);
        }

        private static void DestroyUnityObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
