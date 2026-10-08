using System;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace OpenOita.V2
{
    public struct RuleWork
    {
        public long Attempts, Moves, Sleeping, TileJobs;
    }

    // 同色核心相距64格。材料写集扩1格、唤醒写集扩2格，均不重叠。
    // 非核心页的共享位图用原子OR；核心页位图仅由自己的任务修改。
    public sealed unsafe class LocalMaterialRules : IDisposable
    {
        private readonly MaterialGrid _grid;
        private NativeList<int> _work;
        private NativeArray<RuleWork> _metrics;
        private NativeList<int> _waterReady, _gasReady;
        private NativeArray<byte> _waterRegistered, _gasRegistered;
        public RuleWork LastWork { get; private set; }
        public bool Parallel { get; set; } = true;

        public LocalMaterialRules(MaterialGrid grid)
        {
            _grid = grid; _work = new NativeList<int>(grid.Tiles.Length, Allocator.Persistent);
            _metrics = new NativeArray<RuleWork>(grid.Tiles.Length, Allocator.Persistent);
            _waterReady = new NativeList<int>(grid.Tiles.Length, Allocator.Persistent);
            _gasReady = new NativeList<int>(grid.Tiles.Length, Allocator.Persistent);
            _waterRegistered = new NativeArray<byte>(grid.Tiles.Length, Allocator.Persistent);
            _gasRegistered = new NativeArray<byte>(grid.Tiles.Length, Allocator.Persistent);
        }

        public void Execute(ulong tick, bool gas)
        {
            LastWork = default;
            // 页增长在调度前完成，任务不能改变页目录或分配内存。
            NativeArray<int> active = gas ? _grid.GasTiles : _grid.WaterTiles;
            NativeList<int> ready = gas ? _gasReady : _waterReady;
            NativeArray<byte> registered = gas ? _gasRegistered : _waterRegistered;
            NativeList<int> pending = gas ? _grid.PendingGasTiles : _grid.PendingWaterTiles;
            for (int i = 0; i < pending.Length; i++) Register(pending[i], active, ready, registered);
            pending.Clear();
            for (int i = 0; i < ready.Length; i++) _grid.ReserveHalo(ready[i]);
            for (int phase = 0; phase < 4; phase++)
            {
                int color = (int)((tick + (gas ? 1UL : 0UL) + (ulong)phase) & 3);
                _work.Clear();
                for (int i = 0; i < ready.Length; i++)
                {
                    int id = ready[i];
                    int tx = id % _grid.TileColumns, ty = id / _grid.TileColumns;
                    if (active[id] != 0 && ((tx & 1) | ((ty & 1) << 1)) == color) _work.Add(id);
                }
                if (_work.Length == 0) continue;
                var job = new MovementJob
                {
                    Tiles = _grid.Tiles, Definitions = _grid.Definitions, Work = _work.AsArray(),
                    Components = _grid.ColdStore.Records.AsArray(), Metrics = _metrics,
                    VisualTiles = _grid.VisualTiles, StateTiles = _grid.StateTiles,
                    CommitTiles = _grid.CommitTiles,
                    WaterTiles = _grid.WaterTiles, GasTiles = _grid.GasTiles,
                    Width = _grid.Width, Height = _grid.Height, Columns = _grid.TileColumns,
                    Tick = (uint)tick, GasPhase = gas ? 1 : 0
                };
                if (Parallel) job.Schedule(_work.Length, 8).Complete();
                else for (int i = 0; i < _work.Length; i++) job.Execute(i);
                RuleWork total = LastWork;
                for (int i = 0; i < _work.Length; i++)
                {
                    RuleWork value = _metrics[i]; total.Attempts += value.Attempts; total.Moves += value.Moves;
                    total.Sleeping += value.Sleeping; total.TileJobs += value.TileJobs;
                }
                LastWork = total;
                // 任务只可能唤醒核心相邻页，不扫描空白画布目录。
                for (int i = 0; i < _work.Length; i++)
                {
                    int id = _work[i], cx = id % _grid.TileColumns, cy = id / _grid.TileColumns;
                    for (int y = Math.Max(0, cy - 1); y <= Math.Min(_grid.TileRows - 1, cy + 1); y++)
                        for (int x = Math.Max(0, cx - 1); x <= Math.Min(_grid.TileColumns - 1, cx + 1); x++)
                        {
                            int neighbour = x + y * _grid.TileColumns;
                            Register(neighbour, active, ready, registered);
                            if (_grid.StateTiles[neighbour] == 1) _grid.RegisterStateTile(neighbour);
                            if (_grid.CommitTiles[neighbour] == 1) _grid.RegisterCommitTile(neighbour);
                        }
                }
            }
            for (int i = ready.Length - 1; i >= 0; i--)
                if (active[ready[i]] == 0) { registered[ready[i]] = 0; ready.RemoveAtSwapBack(i); }
        }

        private static void Register(int id, NativeArray<int> active, NativeList<int> ready, NativeArray<byte> registered)
        {
            if (active[id] == 0 || registered[id] != 0) return;
            registered[id] = 1; ready.Add(id);
        }

        [BurstCompile]
        private unsafe struct MovementJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<MaterialTile> Tiles;
            [ReadOnly] public NativeArray<MaterialDefinition> Definitions;
            [ReadOnly] public NativeArray<int> Work;
            [NativeDisableParallelForRestriction] public NativeArray<CellCold> Components;
            public NativeArray<RuleWork> Metrics;
            [NativeDisableParallelForRestriction] public NativeArray<int> VisualTiles, StateTiles, WaterTiles, GasTiles, CommitTiles;
            public int Width, Height, Columns, GasPhase;
            public uint Tick;

            private static void AtomicOr(uint* address, uint bits)
            {
                ref int location = ref UnsafeUtility.AsRef<int>(address);
                int before;
                do { before = location; } while (Interlocked.CompareExchange(ref location, before | (int)bits, before) != before);
            }
            private static void SetTileFlag(NativeArray<int> flags, int id) => Interlocked.Exchange(ref UnsafeUtility.ArrayElementAsRef<int>(flags.GetUnsafePtr(), id), 1);
            private bool Inside(int x, int y) => (uint)x < Width && (uint)y < Height;
            private int Id(int x, int y) => (x >> 5) + (y >> 5) * Columns;
            private int Position(int x, int y) => (x & 31) + (y & 31) * 32;
            private bool Empty(int x, int y)
            {
                if (!Inside(x, y)) return false;
                MaterialTile target = Tiles[Id(x, y)];
                return target.Material[Position(x, y)] == 0 && (target.Dynamic[y & 31] & (1u << (x & 31))) == 0;
            }
            private void Mark(int x, int y, int core)
            {
                int id = Id(x, y); MaterialTile tile = Tiles[id]; uint bit = 1u << (x & 31);
                if (id == core) { tile.State[y & 31] |= bit; tile.Visual[y & 31] |= bit; }
                else { AtomicOr(tile.State + (y & 31), bit); AtomicOr(tile.Visual + (y & 31), bit); }
                Interlocked.CompareExchange(ref UnsafeUtility.ArrayElementAsRef<int>(StateTiles.GetUnsafePtr(), id), 1, 0);
                Interlocked.CompareExchange(ref UnsafeUtility.ArrayElementAsRef<int>(CommitTiles.GetUnsafePtr(), id), 1, 0);
                SetTileFlag(VisualTiles, id);
            }
            private void Wake(int x, int y, int core)
            {
                if (!Inside(x, y)) return;
                int id = Id(x, y); MaterialTile tile = Tiles[id]; if (!tile.IsCreated) return;
                MaterialDefinition definition = Definitions[tile.Material[Position(x, y)]];
                uint bit = 1u << (x & 31);
                if (definition.IsWater)
                {
                    if (id == core) tile.Water[y & 31] |= bit; else AtomicOr(tile.Water + (y & 31), bit);
                    SetTileFlag(WaterTiles, id);
                }
                if (definition.IsGas)
                {
                    if (id == core) tile.Gas[y & 31] |= bit; else AtomicOr(tile.Gas + (y & 31), bit);
                    SetTileFlag(GasTiles, id);
                }
            }
            private void WakeAround(int x, int y, int core)
            {
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) Wake(x + dx, y + dy, core);
            }
            private void Move(MaterialTile source, int p, int x, int y, int nx, int ny, int direction, int core, MaterialDefinition definition)
            {
                MaterialTile target = Tiles[Id(nx, ny)]; int q = Position(nx, ny);
                target.Material[q] = source.Material[p]; target.Flags[q] = (byte)((source.Flags[p] & ~GridCell.LeftFlag) | (direction < 0 ? GridCell.LeftFlag : 0));
                target.Due[q] = Tick + definition.MoveInterval; target.Stamp[q] = Tick;
                int component = source.Component[p]; target.Component[q] = component;
                if (component != 0)
                {
                    CellCold state = Components[component - 1]; state.X = nx; state.Y = ny;
                    Components[component - 1] = state;
                }
                source.Material[p] = 0; source.Flags[p] = 0; source.Component[p] = 0; source.Due[p] = 0;
                source.Occupied[y & 31] &= ~(1u << (x & 31));
                if (Id(nx, ny) == core) target.Occupied[ny & 31] |= 1u << (nx & 31);
                else AtomicOr(target.Occupied + (ny & 31), 1u << (nx & 31));
                Mark(x, y, core); Mark(nx, ny, core); WakeAround(x, y, core); WakeAround(nx, ny, core);
            }

            public void Execute(int index)
            {
                int core = Work[index], ox = (core % Columns) * 32, oy = (core / Columns) * 32;
                MaterialTile tile = Tiles[core]; RuleWork work = new RuleWork { TileJobs = 1 };
                uint* activity = GasPhase == 0 ? tile.Water : tile.Gas;
                int vertical = GasPhase == 0 ? -1 : 1;
                bool rightFirst = ((Tick + (uint)core) & 1) != 0;
                for (int yi = 0; yi < 32; yi++)
                {
                    int row = GasPhase == 0 ? yi : 31 - yi;
                    uint considered = 0;
                    while ((activity[row] & ~considered) != 0)
                    {
                        uint remaining = activity[row] & ~considered;
                        int cx = rightFirst ? 31 - math.lzcnt(remaining) : math.tzcnt(remaining);
                        uint bit = 1u << cx; considered |= bit;
                        int p = cx + row * 32, x = ox + cx, y = oy + row;
                        MaterialDefinition definition = Definitions[tile.Material[p]];
                        if (GasPhase == 0 ? !definition.IsWater : !definition.IsGas) { activity[row] &= ~bit; continue; }
                        if (tile.Stamp[p] == Tick || (int)(Tick - tile.Due[p]) < 0) continue;
                        work.Attempts++; tile.Stamp[p] = Tick;
                        // 当前格被真实动态体覆盖时等待捕获阶段，不把它当可移动自由格。
                        if ((tile.Dynamic[row] & bit) != 0) { activity[row] &= ~bit; work.Sleeping++; continue; }
                        int direction = (tile.Flags[p] & GridCell.LeftFlag) != 0 ? -1 : 1;
                        int nx = x, ny = y + vertical;
                        bool found = Empty(nx, ny);
                        if (!found && Empty(x + direction, y) && Empty(x + direction, y + vertical)) { nx = x + direction; found = true; }
                        if (!found && Empty(x - direction, y) && Empty(x - direction, y + vertical)) { nx = x - direction; direction = -direction; found = true; }
                        if (!found && Empty(x + direction, y)) { nx = x + direction; ny = y; found = true; }
                        if (!found && Empty(x - direction, y)) { nx = x - direction; ny = y; direction = -direction; found = true; }
                        if (found)
                        {
                            activity[row] &= ~bit; Move(tile, p, x, y, nx, ny, direction, core, definition); work.Moves++;
                        }
                        else { tile.Due[p] = Tick + definition.MoveInterval; activity[row] &= ~bit; work.Sleeping++; }
                    }
                }
                uint any = 0; for (int row = 0; row < 32; row++) any |= activity[row];
                // 此核心没有并行邻域写入，清除此页标志不会丢失其他任务唤醒。
                if (any == 0) { if (GasPhase == 0) WaterTiles[core] = 0; else GasTiles[core] = 0; }
                Metrics[index] = work;
            }
        }

        public void Dispose()
        {
            if (_work.IsCreated) _work.Dispose(); if (_metrics.IsCreated) _metrics.Dispose();
            if (_waterReady.IsCreated) _waterReady.Dispose(); if (_gasReady.IsCreated) _gasReady.Dispose();
            if (_waterRegistered.IsCreated) _waterRegistered.Dispose(); if (_gasRegistered.IsCreated) _gasRegistered.Dispose();
        }
    }
}
