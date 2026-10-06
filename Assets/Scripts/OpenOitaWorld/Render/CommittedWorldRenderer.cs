using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;
using UnityEngine.Rendering;

namespace OpenOita.Render
{
    // 正式M07B。每归属/128块一份逐像素纹理和一个火焰网格；不使用材料ID作切片。
    public sealed class CommittedWorldRenderer : ITransactionalWorldRenderer
    {
        private const int Side = 128;
        private readonly Shader _shader, _flameShader;
        private Dictionary<(ulong id, int x, int y), Tile> _resources = new();
        private Dictionary<(ulong id, int x, int y), Tile> _pending = new();
        private GameObject _root;
        private Mesh _quad;
        private Material _flameMaterial;
        private bool _disposed, _dirty;
        private readonly List<(ulong id, int x, int y)> _retired = new();
        public WorldVersion LogicalVersion { get; private set; }
        public WorldVersion UploadedVersion { get; private set; }
        public long LastUploadBytes { get; private set; }
        public double LastFlushMilliseconds { get; private set; }
        public long ReservedCpuBytes
        {
            get { long bytes = 4096; foreach (Tile tile in _resources.Values) bytes += tile.CpuBytes; return bytes; }
        }
        public int TileCount => _pending.Count;
        public int BurningCount { get; private set; }
        public bool FlamesVisible { get; set; } = true;
        // M07B：编辑预览/正式试玩摄像机隔离；必须在Prepare前设定。
        public int DisplayLayer { get; set; }
        // 测试沿公共故障接口注入，生产默认无注入器。
        internal IFailureInjector Failures;

        public CommittedWorldRenderer(Shader shader = null, Shader flameShader = null)
        {
            _shader = shader;
            _flameShader = flameShader;
        }
        public WorldResult Prepare(ICommittedRenderView initialView)
        {
            if (_disposed) return Error(WorldErrorCode.Disposed, "renderer", "显示已释放。");
            try
            {
                Shader shader = _shader != null ? _shader : Shader.Find("OpenOita/ChunkDisplay");
                Shader flame = _flameShader != null ? _flameShader : Shader.Find("OpenOita/CommittedFlame");
                if (shader == null || flame == null || !shader.isSupported || !flame.isSupported)
                    return Error(WorldErrorCode.NotReady, "shader", "正式材料/火焰Shader缺失或不受支持。");
                _root = new GameObject("OpenOita正式显示") { hideFlags = HideFlags.DontSave };
                _root.layer = DisplayLayer;
                _root.AddComponent<WorldDisplayDriver>().Renderer = this;
                _quad = new Mesh { name = "OpenOita共享块四边形", hideFlags = HideFlags.DontSave };
                _quad.vertices = new[] { Vector3.zero, Vector3.right, Vector3.one - Vector3.forward, Vector3.up };
                _quad.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                _quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                _quad.RecalculateBounds();
                _flameMaterial = new Material(flame) { name = "OpenOita共享火焰", hideFlags = HideFlags.DontSave };
                MaterialShader = shader;
                using IPreparedWorldDisplay prepared = PrepareCommit(initialView, default);
                if (!prepared.Result.IsSuccess) { Dispose(); return prepared.Result; }
                prepared.Adopt();
                return WorldResult.Success();
            }
            catch (Exception exception) { Dispose(); return Error(WorldErrorCode.Faulted, "initialize", exception.Message); }
        }
        private Shader MaterialShader { get; set; }
        private static int Block(int coordinate) => (int)Math.Floor(coordinate / (double)Side);

        public IPreparedWorldDisplay PrepareCommit(ICommittedRenderView view, in ChangeSet changes)
        {
            var candidate = new Prepared(this, view.Version);
            try
            {
                if (_disposed || _root == null) throw new InvalidOperationException("显示未初始化或已释放。");
                var poses = new Dictionary<ulong, BodyPose>();
                poses.Add(0, new BodyPose(view.Origin, 0));
                foreach (BodySnapshot body in view.Bodies) poses.Add(body.BodyId, body.Pose);
                foreach (CellKey key in view.OccupiedCells)
                {
                    var tileKey = (key.Position.BodyId, Block(key.Position.X), Block(key.Position.Y));
                    if (!candidate.Tiles.TryGetValue(tileKey, out Tile tile))
                    {
                        if (!_resources.TryGetValue(tileKey, out tile))
                        {
                            if (ReservedCpuBytes + (candidate.NewTiles.Count + 1) * (Side * Side * 8L + 1024) > ContractDefaults.CpuBudgetBytes)
                                throw new InvalidOperationException("显示块候选容量超过CPU预算。");
                            tile = new Tile(this, tileKey);
                            candidate.NewTiles.Add(tile);
                        }
                        tile.ClearWork();
                        tile.WorkPose = poses[key.Position.BodyId];
                        tile.WorkSize = view.Config.CellSize;
                        candidate.Tiles.Add(tileKey, tile);
                        var context = new TransactionContext(LogicalVersion, view.Version.CommittedTick, TickStage.Publish);
                        WorldResult injected = Failures?.Check(context, FailurePoint.AfterDisplayTilePrepared) ?? WorldResult.Success();
                        if (!injected.IsSuccess) { candidate.Result = injected; return candidate; }
                    }
                    WorldResult read = view.Read(key, out CellSnapshot cell);
                    if (!read.IsSuccess || !view.Materials.TryGet(cell.MaterialId, out MaterialRuntimeEntry material))
                        throw new InvalidOperationException("提交显示材料或状态不可读取。");
                    int x = key.Position.X - tileKey.Item2 * Side, y = key.Position.Y - tileKey.Item3 * Side;
                    tile.WorkPixels[y * Side + x] = MaterialPixel(cell, material);
                    if (cell.IsBurning) { tile.AddFlame(x, y); candidate.Burning++; }
                }
                candidate.AllResources = new Dictionary<(ulong id, int x, int y), Tile>(_resources);
                foreach (Tile tile in candidate.NewTiles) candidate.AllResources.Add(tile.Key, tile);
                candidate.Result = WorldResult.Success();
            }
            catch (Exception exception) { candidate.Result = Error(WorldErrorCode.Faulted, "candidate", exception.Message); }
            return candidate;
        }

        // M02已采用准备结果，通知仅核对版本；绝不在回调中读工作世界。
        public void OnCommitted(ICommittedRenderView view, in ChangeSet changes)
        {
            if (!LogicalVersion.Equals(view.Version)) throw new InvalidOperationException("显示候选未由M02采用。");
        }

        public void FlushFrame()
        {
            if (_disposed || !_dirty) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            LastUploadBytes = 0;
            _retired.Clear();
            foreach (var pair in _resources) if (!_pending.ContainsKey(pair.Key)) _retired.Add(pair.Key);
            foreach (var key in _retired) { _resources[key].Dispose(); _resources.Remove(key); }
            foreach (Tile tile in _pending.Values)
            {
                LastUploadBytes += tile.Upload(FlamesVisible);
            }
            UploadedVersion = LogicalVersion; _dirty = false;
            LastFlushMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
        public void RefreshOverlays()
        {
            foreach (Tile tile in _pending.Values) tile.Flames.SetActive(FlamesVisible && tile.FrontVertices.Count != 0);
        }
        public bool TryGetPixel(ulong bodyId, int x, int y, out Color32 pixel)
        {
            pixel = default;
            if (!_pending.TryGetValue((bodyId, Block(x), Block(y)), out Tile tile)) return false;
            pixel = tile.FrontPixels[(y - Block(y) * Side) * Side + x - Block(x) * Side]; return true;
        }
        internal Texture2D TextureFor(ulong bodyId, int x, int y) => _pending[(bodyId, Block(x), Block(y))].Texture;
        public static Color32 MaterialPixel(in CellSnapshot cell, in MaterialRuntimeEntry material)
        {
            float ratio = (material.Rules & RuleMask.Burnable) == 0 ? 1 : (float)cell.FuelTicksRemaining / material.Parameters.FuelTicks;
            float brightness = 0.35f + 0.65f * Mathf.Clamp01(ratio);
            Color32 c = material.Color;
            return new Color32((byte)Mathf.RoundToInt(c.r * brightness), (byte)Mathf.RoundToInt(c.g * brightness), (byte)Mathf.RoundToInt(c.b * brightness), c.a);
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            if (_root != null) _root.SetActive(false);
            foreach (Tile tile in _resources.Values) tile.Dispose();
            _resources.Clear(); _pending.Clear();
            Release(_quad); Release(_flameMaterial); Release(_root);
            _root = null; _quad = null; _flameMaterial = null;
        }
        private static WorldResult Error(WorldErrorCode code, string target, string message) =>
            WorldResult.Failure(code, new WorldDiagnostic("DisplayPrepare", target, message));
        internal static void Release(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }

        private sealed class Prepared : IPreparedWorldDisplay
        {
            private readonly CommittedWorldRenderer _owner;
            private readonly WorldVersion _version;
            private bool _adopted;
            internal readonly Dictionary<(ulong id, int x, int y), Tile> Tiles = new();
            internal readonly List<Tile> NewTiles = new();
            internal Dictionary<(ulong id, int x, int y), Tile> AllResources;
            internal int Burning;
            public WorldResult Result { get; internal set; }
            public long CpuBytes
            {
                get { long bytes = _owner.ReservedCpuBytes; foreach (Tile tile in NewTiles) bytes += tile.CpuBytes; return bytes; }
            }
            internal Prepared(CommittedWorldRenderer owner, WorldVersion version) { _owner = owner; _version = version; }
            public void Adopt()
            {
                if (_adopted || !Result.IsSuccess) throw new InvalidOperationException("无效显示候选不能采用。");
                // 字典及其容量也在Prepare中完成；这里不分配、不上传。
                _owner._resources = AllResources;
                foreach (Tile tile in Tiles.Values) tile.Adopt();
                _owner._pending = Tiles; _owner.LogicalVersion = _version; _owner.BurningCount = Burning;
                _owner._dirty = true; _adopted = true;
            }
            public void Dispose() { if (!_adopted) foreach (Tile tile in NewTiles) tile.Dispose(); }
        }
        private sealed class Tile : IDisposable
        {
            internal readonly (ulong id, int x, int y) Key;
            internal readonly GameObject Object, Flames;
            internal readonly Texture2D Texture;
            private readonly Material _material;
            private readonly Mesh _flames;
            internal Color32[] FrontPixels = new Color32[Side * Side], WorkPixels = new Color32[Side * Side];
            internal List<Vector3> FrontVertices = new(), WorkVertices = new();
            internal List<Vector2> FrontUv = new(), WorkUv = new();
            internal List<int> FrontTriangles = new(), WorkTriangles = new();
            internal BodyPose WorkPose;
            internal float WorkSize;
            internal long CpuBytes => Side * Side * 8L + 1024 +
                (FrontVertices.Capacity + WorkVertices.Capacity) * 12L + (FrontUv.Capacity + WorkUv.Capacity) * 8L +
                (FrontTriangles.Capacity + WorkTriangles.Capacity) * 4L;
            private BodyPose _pose;
            private float _size;
            private bool _pixelsDirty, _flamesDirty, _poseDirty, _uploaded;
            internal Tile(CommittedWorldRenderer owner, (ulong id, int x, int y) key)
            {
                Key = key;
                try
                {
                    Object = new GameObject($"OpenOita显示 {key}") { hideFlags = HideFlags.DontSave };
                    Object.layer = owner.DisplayLayer;
                    Object.SetActive(false); Object.transform.SetParent(owner._root.transform, false);
                    Texture = new Texture2D(Side, Side, TextureFormat.RGBA32, false) { name = "OpenOita材料纹素", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
                    _material = new Material(owner.MaterialShader) { name = "OpenOita材料批次", hideFlags = HideFlags.DontSave };
                    _material.mainTexture = Texture;
                    Object.AddComponent<MeshFilter>().sharedMesh = owner._quad;
                    Object.AddComponent<MeshRenderer>().sharedMaterial = _material;
                    Flames = new GameObject("OpenOita批量火焰") { hideFlags = HideFlags.DontSave };
                    Flames.layer = owner.DisplayLayer;
                    Flames.transform.SetParent(Object.transform, false);
                    _flames = new Mesh { name = "OpenOita火焰批次", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
                    Flames.AddComponent<MeshFilter>().sharedMesh = _flames;
                    Flames.AddComponent<MeshRenderer>().sharedMaterial = owner._flameMaterial;
                }
                catch { Dispose(); throw; }
            }
            internal void ClearWork() { Array.Clear(WorkPixels, 0, WorkPixels.Length); WorkVertices.Clear(); WorkUv.Clear(); WorkTriangles.Clear(); }
            internal void AddFlame(int x, int y)
            {
                int n = WorkVertices.Count;
                float left = x / (float)Side, bottom = y / (float)Side, right = (x + 1f) / Side, top = (y + 1.35f) / Side;
                WorkVertices.Add(new Vector3(left, bottom, -0.001f)); WorkVertices.Add(new Vector3(right, bottom, -0.001f));
                WorkVertices.Add(new Vector3(right, top, -0.001f)); WorkVertices.Add(new Vector3(left, top, -0.001f));
                WorkUv.Add(Vector2.zero); WorkUv.Add(Vector2.right); WorkUv.Add(Vector2.one); WorkUv.Add(Vector2.up);
                WorkTriangles.Add(n); WorkTriangles.Add(n + 1); WorkTriangles.Add(n + 2); WorkTriangles.Add(n); WorkTriangles.Add(n + 2); WorkTriangles.Add(n + 3);
            }
            internal void Adopt()
            {
                bool pixelChanged = !_uploaded;
                for (int i = 0; !pixelChanged && i < WorkPixels.Length; i++)
                {
                    Color32 a = FrontPixels[i], b = WorkPixels[i];
                    pixelChanged = a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a;
                }
                bool flamesChanged = !_uploaded || FrontVertices.Count != WorkVertices.Count;
                for (int i = 0; !flamesChanged && i < WorkVertices.Count; i++) flamesChanged = FrontVertices[i] != WorkVertices[i];
                _pixelsDirty |= pixelChanged; _flamesDirty |= flamesChanged;
                _poseDirty |= !_uploaded || !_pose.Equals(WorkPose) || _size != WorkSize;
                (FrontPixels, WorkPixels) = (WorkPixels, FrontPixels);
                (FrontVertices, WorkVertices) = (WorkVertices, FrontVertices); (FrontUv, WorkUv) = (WorkUv, FrontUv);
                (FrontTriangles, WorkTriangles) = (WorkTriangles, FrontTriangles);
                _pose = WorkPose; _size = WorkSize;
            }
            internal long Upload(bool flamesVisible)
            {
                long bytes = 0;
                if (_pixelsDirty) { Texture.SetPixels32(FrontPixels); Texture.Apply(false, false); bytes += Side * Side * 4L; }
                if (_poseDirty)
                {
                    Quaternion rotation = Quaternion.Euler(0, 0, _pose.AngleRadians * Mathf.Rad2Deg);
                    Vector3 offset = rotation * new Vector3(Key.x * Side * _size, Key.y * Side * _size, 0);
                    Object.transform.SetPositionAndRotation((Vector3)_pose.Position + offset, rotation);
                    Object.transform.localScale = new Vector3(Side * _size, Side * _size, 1);
                }
                if (_flamesDirty)
                {
                    _flames.Clear(); _flames.SetVertices(FrontVertices); _flames.SetUVs(0, FrontUv); _flames.SetTriangles(FrontTriangles, 0); _flames.RecalculateBounds();
                    bytes += FrontVertices.Count * 20L + FrontTriangles.Count * 4L;
                }
                Flames.SetActive(flamesVisible && FrontVertices.Count != 0); Object.SetActive(true);
                _pixelsDirty = _flamesDirty = _poseDirty = false; _uploaded = true;
                return bytes;
            }
            public void Dispose()
            {
                if (Object != null) Object.SetActive(false);
                Release(Texture); Release(_material); Release(_flames); Release(Object);
            }
        }
    }
}
