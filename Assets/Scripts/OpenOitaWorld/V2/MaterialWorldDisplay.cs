using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.V2.Render;
using OpenOita.V2.Diagnostics;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace OpenOita.V2
{
    // 显示只在相机真正绘制时借用完整Tick的数据；隐藏窗口不创建逐Tick上传队列。
    public sealed unsafe class MaterialWorldDisplay : IDisposable
    {
        private readonly MaterialWorld _world;
        private readonly V2RenderingResources _resources;
        private readonly int _layer;
        private readonly Dictionary<TileKey, Entry> _tiles = new Dictionary<TileKey, Entry>(512);
        private readonly Stack<int> _free = new Stack<int>(512);
        private readonly HashSet<int> _activeGridHandles = new HashSet<int>();
        private readonly List<TileKey> _staleKeys = new List<TileKey>(512);
        private IncrementalWorldRenderer _renderer;
        private int _nextHandle, _capacity = 512;
        private int _prepareSequence;
        private ulong _generation;
        private bool _disposed;
        private bool _resetting;
        private readonly Color32[] _palette = new Color32[65536];
        public double LastPreparationMs { get; private set; }
        public long LastPreparationAllocated { get; private set; }
        public bool LastPreparationAllocationSupported { get; private set; }
        private readonly struct TileKey : IEquatable<TileKey>
        {
            public readonly int Grid, X, Y;
            public TileKey(int grid, int x, int y) { Grid = grid; X = x; Y = y; }
            public bool Equals(TileKey other) => Grid == other.Grid && X == other.X && Y == other.Y;
            public override bool Equals(object obj) => obj is TileKey other && Equals(other);
            public override int GetHashCode() => unchecked((Grid * 397 ^ X) * 397 ^ Y);
        }
        private sealed class Entry { public int Handle, SeenFrame; }
        public IncrementalWorldRenderer Renderer => _renderer;

        public MaterialWorldDisplay(MaterialWorld world, V2RenderingResources resources, int layer)
        {
            _world = world; _resources = resources; _layer = layer;
            resources.Validate();
            for (int id = 1; id < 65536; id++)
            {
                uint color = world.Grid.Definitions[id].Color;
                _palette[id] = new Color32((byte)color, (byte)(color >> 8), (byte)(color >> 16), (byte)(color >> 24));
            }
            CreateRenderer();
        }

        private void CreateRenderer()
        {
            _renderer = new IncrementalWorldRenderer(_resources.UpdateShader, _resources.TileShader, _resources.FireShader) { DisplayLayer = _layer };
            _renderer.Initialize(_palette);
            _renderer.ReserveFrameCapacity(IncrementalWorldRenderer.InitialUpdateCapacity, _capacity,
                Math.Max(512, _world.Config.Limits.MaxDynamicBodies * 4 + 2));
            _renderer.PrepareCamera += PrepareForCamera;
            WorldRenderFeatureV2.Register(_renderer); _generation = _world.Version.Generation;
        }

        public void ResetDisplay()
        {
            if (_disposed || _resetting) return;
            _resetting = true;
            try
            {
                IncrementalWorldRenderer old = _renderer;
                _renderer = null;
                if (old != null)
                {
                    WorldRenderFeatureV2.Unregister(old);
                    old.PrepareCamera -= PrepareForCamera;
                    old.Dispose();
                }
                _tiles.Clear(); _free.Clear(); _activeGridHandles.Clear(); _staleKeys.Clear();
                _nextHandle = 0; _capacity = 512; _prepareSequence = 0;
                CreateRenderer();
            }
            finally { _resetting = false; }
        }

        private static Rect View(Camera camera)
        {
            float height = camera.orthographic ? camera.orthographicSize : 10000;
            float width = height * camera.aspect; Vector3 p = camera.transform.position;
            return Rect.MinMaxRect(p.x - width, p.y - height, p.x + width, p.y + height);
        }

        /// <summary>Runs the same camera display barrier used by Renderer.PrepareCamera.</summary>
        public void PrepareForCamera(Camera camera)
        {
            long start = Stopwatch.GetTimestamp();
            bool measureAllocations = ScopedAllocationProbe.IsSupported && !ScopedAllocationProbe.IsActive;
            if (measureAllocations) ScopedAllocationProbe.Begin();
            try { Prepare(camera); }
            finally
            {
                LastPreparationMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                AllocationProbeMeasurement allocation = measureAllocations ? ScopedAllocationProbe.End() : default;
                LastPreparationAllocationSupported = allocation.Usable;
                LastPreparationAllocated = allocation.Usable ? allocation.Bytes : -1;
            }
        }

        private void Prepare(Camera camera)
        {
            if (_disposed || _resetting || _world.Lifecycle != WorldLifecycle.Ready || camera == null) return;
            if (_generation != _world.Version.Generation) { ResetDisplay(); return; }
            _prepareSequence = _prepareSequence == int.MaxValue ? 1 : _prepareSequence + 1;
            _activeGridHandles.Clear();
            _activeGridHandles.Add(_world.Grid.GridHandle);
            int bodies = _world.Physics?.BodyCount ?? 0;
            int maxHandle = 1;
            for (int i = 0; i < bodies; i++) maxHandle = Math.Max(maxHandle, _world.Physics.GetBody(i).Grid.GridHandle);
            if (_world.Physics != null)
                for (int i = 0; i < bodies; i++) _activeGridHandles.Add(_world.Physics.GetBody(i).Grid.GridHandle);
            Rect view = View(camera);
            // 当前可见请求可能超过512，增长只在显示屏障发生。
            int requested = EstimateVisible(_world.Grid, new BodyPose(_world.Origin, 0), view);
            for (int i = 0; i < bodies; i++)
            { BodyV2 body = _world.Physics.GetBody(i); requested += EstimateVisible(body.Grid, body.Pose, view); }
            int visiblePixelCapacity = EstimateVisiblePixelCapacity(_world.Grid, new BodyPose(_world.Origin, 0), view);
            for (int i = 0; i < bodies; i++)
            { BodyV2 body = _world.Physics.GetBody(i); visiblePixelCapacity = SaturatingPixelAdd(visiblePixelCapacity, EstimateVisiblePixelCapacity(body.Grid, body.Pose, view)); }
            visiblePixelCapacity = Math.Min(IncrementalWorldRenderer.PagePixelCapacity,
                SaturatingPixelAdd(visiblePixelCapacity, _renderer.PendingPixelUpdateCount));
            int nextCapacity = _capacity < requested ? math.ceilpow2(requested) : _capacity;
            if (nextCapacity > _capacity || maxHandle + 1 > _renderer.BodyPoseCapacity)
            {
                _renderer.ReserveFrameCapacity(visiblePixelCapacity, nextCapacity, maxHandle + 1);
                _capacity = nextCapacity;
            }
            else
            {
                _renderer.ReserveFrameCapacity(visiblePixelCapacity, _capacity, maxHandle + 1);
            }
            ulong committedTick = _world.Version.CommittedTick;
            _renderer.BeginFrame(committedTick);
            _renderer.SetBodyPose(_world.Grid.GridHandle, new BodyPose(_world.Origin, 0));
            Paint(_world.Grid, new BodyPose(_world.Origin, 0), view, committedTick);
            for (int i = 0; i < bodies; i++)
            { BodyV2 body = _world.Physics.GetBody(i); _renderer.SetBodyPose(body.Grid.GridHandle, body.Pose); Paint(body.Grid, body.Pose, view, committedTick); }
            RemoveRetiredGridTiles();
            _renderer.FlushFrame();
        }

        private static void LocalBounds(MaterialGrid grid, BodyPose pose, float size, Rect view, out int x0, out int y0, out int x1, out int y1)
        {
            float cosine = Mathf.Cos(pose.AngleRadians), sine = Mathf.Sin(pose.AngleRadians);
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int corner = 0; corner < 4; corner++)
            {
                float x = (corner & 1) == 0 ? view.xMin : view.xMax, y = (corner & 2) == 0 ? view.yMin : view.yMax;
                x -= pose.Position.x; y -= pose.Position.y;
                float lx = (cosine * x + sine * y) / size, ly = (-sine * x + cosine * y) / size;
                minX = Math.Min(minX, lx); maxX = Math.Max(maxX, lx); minY = Math.Min(minY, ly); maxY = Math.Max(maxY, ly);
            }
            x0 = Math.Max(0, Mathf.FloorToInt(minX / 128)); y0 = Math.Max(0, Mathf.FloorToInt(minY / 128));
            x1 = Math.Min((grid.Width + 127) / 128, Mathf.CeilToInt(maxX / 128)); y1 = Math.Min((grid.Height + 127) / 128, Mathf.CeilToInt(maxY / 128));
        }

        private int EstimateVisible(MaterialGrid grid, BodyPose pose, Rect view)
        {
            LocalBounds(grid, pose, _world.Config.CellSize, view, out int x0, out int y0, out int x1, out int y1);
            return Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        }

        private int EstimateVisiblePixelCapacity(MaterialGrid grid, BodyPose pose, Rect view)
        {
            LocalBounds(grid, pose, _world.Config.CellSize, view, out int x0, out int y0, out int x1, out int y1);
            long total = 0;
            for (int ry = y0; ry < y1; ry++) for (int rx = x0; rx < x1; rx++)
            {
                TileKey key = new TileKey(grid.GridHandle, rx, ry);
                bool cached = _tiles.ContainsKey(key), occupied = false, dirty = false;
                int occupiedPixels = 0, visualPixels = 0;
                for (int sy = ry * 4; sy < Math.Min(grid.TileRows, ry * 4 + 4); sy++)
                    for (int sx = rx * 4; sx < Math.Min(grid.TileColumns, rx * 4 + 4); sx++)
                    {
                        int id = sx + sy * grid.TileColumns; MaterialTile tile = grid.Tiles[id]; if (!tile.IsCreated) continue;
                        dirty |= grid.VisualTiles[id] != 0;
                        if (cached && !dirty) continue;
                        for (int row = 0; row < 32; row++)
                        {
                            if (cached) visualPixels += math.countbits(tile.Visual[row]);
                            else
                            {
                                occupiedPixels += math.countbits(tile.Occupied[row]);
                                occupied |= tile.Occupied[row] != 0;
                            }
                        }
                    }
                if (!cached && !occupied) continue;
                // Fresh tiles upload occupied cells; cached dirty tiles upload only visual bits.
                // This is the exact steady-frame upper bound and avoids scanning clean tiles.
                total += cached ? visualPixels : occupiedPixels;
                if (total >= IncrementalWorldRenderer.PagePixelCapacity) return IncrementalWorldRenderer.PagePixelCapacity;
            }
            return (int)total;
        }

        private static int SaturatingPixelAdd(int left, int right)
        {
            long total = (long)left + right;
            return total >= IncrementalWorldRenderer.PagePixelCapacity
                ? IncrementalWorldRenderer.PagePixelCapacity : (int)total;
        }

        private Entry GetEntry(TileKey key, out bool fresh)
        {
            if (_tiles.TryGetValue(key, out Entry value)) { value.SeenFrame = _prepareSequence; fresh = false; return value; }
            int handle;
            if (_free.Count != 0) handle = _free.Pop();
            else if (_nextHandle < _capacity) handle = _nextHandle++;
            else
            {
                TileKey oldestKey = default; Entry oldest = null;
                foreach (var pair in _tiles)
                    if (pair.Value.SeenFrame != _prepareSequence && (oldest == null || pair.Value.SeenFrame < oldest.SeenFrame))
                    { oldest = pair.Value; oldestKey = pair.Key; }
                if (oldest == null) throw new InvalidOperationException("显示缓存容量不足。");
                handle = oldest.Handle; _renderer.RemoveTile(handle); _tiles.Remove(oldestKey);
            }
            value = new Entry { Handle = handle, SeenFrame = _prepareSequence }; _tiles.Add(key, value); fresh = true; return value;
        }

        private void Paint(MaterialGrid grid, BodyPose pose, Rect view, ulong committedTick)
        {
            LocalBounds(grid, pose, _world.Config.CellSize, view, out int x0, out int y0, out int x1, out int y1);
            for (int ry = y0; ry < y1; ry++) for (int rx = x0; rx < x1; rx++)
            {
                var key = new TileKey(grid.GridHandle, rx, ry);
                bool occupied = false, dirty = false;
                for (int sy = ry * 4; sy < Math.Min(grid.TileRows, ry * 4 + 4); sy++)
                    for (int sx = rx * 4; sx < Math.Min(grid.TileColumns, rx * 4 + 4); sx++)
                    {
                        int id = sx + sy * grid.TileColumns; MaterialTile tile = grid.Tiles[id]; if (!tile.IsCreated) continue;
                        dirty |= grid.VisualTiles[id] != 0;
                        for (int row = 0; row < 32 && !occupied; row++) occupied |= tile.Occupied[row] != 0;
                    }
                if (!occupied && !_tiles.ContainsKey(key)) continue;
                Entry entry = GetEntry(key, out bool fresh);
                if (fresh) _renderer.SetTile(entry.Handle, grid.GridHandle, new Vector2(rx * 128 * _world.Config.CellSize, ry * 128 * _world.Config.CellSize), _world.Config.CellSize);
                if (!fresh && !dirty) continue;
                for (int sy = ry * 4; sy < Math.Min(grid.TileRows, ry * 4 + 4); sy++)
                    for (int sx = rx * 4; sx < Math.Min(grid.TileColumns, rx * 4 + 4); sx++)
                    {
                        int id = sx + sy * grid.TileColumns; MaterialTile tile = grid.Tiles[id]; if (!tile.IsCreated) continue;
                        if (!fresh && grid.VisualTiles[id] == 0) continue;
                        _renderer.AddPixelBatchOperation(entry.Handle, ((sx & 3) * 32), ((sy & 3) * 32),
                            fresh, tile, id, grid.Definitions, grid.ColdStore.Records.AsArray(), grid.VisualTiles,
                            committedTick);
                    }
            }
        }

        private void RemoveRetiredGridTiles()
        {
            _staleKeys.Clear();
            foreach (var pair in _tiles)
                if (!_activeGridHandles.Contains(pair.Key.Grid)) _staleKeys.Add(pair.Key);
            for (int i = 0; i < _staleKeys.Count; i++)
            {
                TileKey key = _staleKeys[i];
                if (!_tiles.TryGetValue(key, out Entry entry)) continue;
                _renderer.RemoveTile(entry.Handle);
                _tiles.Remove(key);
                _free.Push(entry.Handle);
            }
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            IncrementalWorldRenderer old = _renderer; _renderer = null;
            if (old != null) { WorldRenderFeatureV2.Unregister(old); old.PrepareCamera -= PrepareForCamera; old.Dispose(); }
            _tiles.Clear(); _free.Clear(); _activeGridHandles.Clear(); _staleKeys.Clear();
        }
    }
}
