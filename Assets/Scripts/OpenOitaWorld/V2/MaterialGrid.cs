using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace OpenOita.V2
{
    public unsafe struct MaterialTile
    {
        [NativeDisableUnsafePtrRestriction] public ushort* Material;
        [NativeDisableUnsafePtrRestriction] public byte* Flags;
        [NativeDisableUnsafePtrRestriction] public uint* Due;
        [NativeDisableUnsafePtrRestriction] public uint* Stamp;
        [NativeDisableUnsafePtrRestriction] public int* Component;
        [NativeDisableUnsafePtrRestriction] public uint* Water;
        [NativeDisableUnsafePtrRestriction] public uint* Gas;
        [NativeDisableUnsafePtrRestriction] public uint* Visual;
        [NativeDisableUnsafePtrRestriction] public uint* State;
        [NativeDisableUnsafePtrRestriction] public uint* Topology;
        [NativeDisableUnsafePtrRestriction] public uint* Dynamic;
        [NativeDisableUnsafePtrRestriction] public uint* Ignition;
        [NativeDisableUnsafePtrRestriction] public uint* Occupied;
        public bool IsCreated => Material != null;
    }

    // 32格页不移动。整个画布只分配小型页目录，不分配画布材料数组。
    public sealed unsafe class MaterialGrid : IDisposable
    {
        public const int Side = 32, CellsPerTile = 1024;
        private readonly bool _ownsCold;
        private readonly bool _ownsPagePool;
        private readonly MaterialPagePool _pagePool;
        private bool _disposed;
        public readonly int Width, Height, TileColumns, TileRows;
        public readonly NativeArray<MaterialDefinition> Definitions;
        public readonly TimedComponentStore ColdStore;
        public NativeArray<MaterialTile> Tiles;
        public NativeArray<int> StateTiles, VisualTiles, TopologyTiles, WaterTiles, GasTiles, IgnitionTiles;
        public NativeArray<int> CommitTiles;
        public NativeArray<int> Counts;
        private NativeList<int> _allocated;
        public NativeList<int> PendingWaterTiles, PendingGasTiles, PendingStateTiles, PendingIgnitionTiles;
        public NativeList<int> ChangedTiles;
        private NativeList<int> _pendingTopology;
        public int GridHandle { get; set; }
        public int AllocatedTileCount => _allocated.Length;
        public int CellCount { get; private set; }
        public int BurningCount { get; private set; }
        public ulong Tick { get; set; }
        public uint Seed { get; set; }

        public MaterialGrid(int width, int height, NativeArray<MaterialDefinition> definitions,
            TimedComponentStore cold = null, MaterialPagePool pagePool = null)
        {
            Width = width; Height = height; TileColumns = (width + 31) / 32; TileRows = (height + 31) / 32;
            Definitions = definitions; ColdStore = cold ?? new TimedComponentStore(); _ownsCold = cold == null;
            _pagePool = pagePool ?? new MaterialPagePool(); _ownsPagePool = pagePool == null;
            int size = TileColumns * TileRows;
            Tiles = new NativeArray<MaterialTile>(size, Allocator.Persistent);
            StateTiles = new NativeArray<int>(size, Allocator.Persistent);
            VisualTiles = new NativeArray<int>(size, Allocator.Persistent);
            TopologyTiles = new NativeArray<int>(size, Allocator.Persistent);
            WaterTiles = new NativeArray<int>(size, Allocator.Persistent);
            GasTiles = new NativeArray<int>(size, Allocator.Persistent);
            IgnitionTiles = new NativeArray<int>(size, Allocator.Persistent);
            CommitTiles = new NativeArray<int>(size, Allocator.Persistent);
            Counts = new NativeArray<int>(65536, Allocator.Persistent);
            _allocated = new NativeList<int>(size, Allocator.Persistent);
            PendingWaterTiles = new NativeList<int>(size, Allocator.Persistent);
            PendingGasTiles = new NativeList<int>(size, Allocator.Persistent);
            PendingStateTiles = new NativeList<int>(size, Allocator.Persistent);
            PendingIgnitionTiles = new NativeList<int>(size, Allocator.Persistent);
            _pendingTopology = new NativeList<int>(size, Allocator.Persistent);
            ChangedTiles = new NativeList<int>(size, Allocator.Persistent);
        }

        public MaterialGrid(WorldConfig config, NativeArray<MaterialDefinition> definitions,
            TimedComponentStore cold = null, MaterialPagePool pagePool = null)
            : this(config.Width, config.Height, definitions, cold, pagePool) { Seed = config.Seed; }

        public MaterialPagePool PagePool => _pagePool;

        public int GetAllocatedTileId(int index) => _allocated[index];
        public bool Inside(int x, int y) => (uint)x < Width && (uint)y < Height;
        public int TileId(int x, int y) => (x >> 5) + (y >> 5) * TileColumns;
        public static int Offset(int x, int y) => (x & 31) + (y & 31) * 32;
        public RectInt TileBounds(int id) => new RectInt((id % TileColumns) * 32, (id / TileColumns) * 32, 32, 32);

        public MaterialTile EnsureTile(int id)
        {
            MaterialTile tile = Tiles[id]; if (tile.IsCreated) return tile;
            byte* start = (byte*)_pagePool.Rent();
            tile.Material = (ushort*)start; start += 2048;
            tile.Flags = start; start += 1024;
            tile.Due = (uint*)start; start += 4096;
            tile.Stamp = (uint*)start; start += 4096;
            tile.Component = (int*)start; start += 4096;
            tile.Water = (uint*)start; start += 128;
            tile.Gas = (uint*)start; start += 128;
            tile.Visual = (uint*)start; start += 128;
            tile.State = (uint*)start; start += 128;
            tile.Topology = (uint*)start; start += 128;
            tile.Dynamic = (uint*)start; start += 128;
            tile.Ignition = (uint*)start; start += 128;
            tile.Occupied = (uint*)start;
            Tiles[id] = tile; _allocated.Add(id); return tile;
        }

        public void ReserveHalo(int id)
        {
            int tx = id % TileColumns, ty = id / TileColumns;
            for (int y = Math.Max(0, ty - 1); y <= Math.Min(TileRows - 1, ty + 1); y++)
                for (int x = Math.Max(0, tx - 1); x <= Math.Min(TileColumns - 1, tx + 1); x++) EnsureTile(x + y * TileColumns);
        }

        public GridCell Read(int x, int y)
        {
            if (!Inside(x, y)) return default;
            MaterialTile tile = Tiles[TileId(x, y)]; if (!tile.IsCreated) return default;
            int p = Offset(x, y), component = tile.Component[p];
            return new GridCell { MaterialId = tile.Material[p], Flags = tile.Flags[p], NextMoveTick = tile.Due[p],
                ProcessedTick = tile.Stamp[p], ComponentHandle = component, Cold = ColdStore.Read(component) };
        }

        public GridCell CreateCell(ushort material, bool fixedCell = false, bool burning = false)
        {
            MaterialDefinition definition = Definitions[material];
            if (definition.Id == 0) throw new ArgumentException("材料ID未注册。");
            byte flags = (byte)((fixedCell ? GridCell.FixedFlag : 0) | (burning ? GridCell.BurningFlag : 0));
            return new GridCell { MaterialId = material, Flags = flags, NextMoveTick = (uint)Tick,
                Cold = new CellCold { FuelRemaining = definition.Fuel, ExpiryTick = definition.IsGas ? Tick + definition.Lifetime : 0,
                    BurnEndTick = burning ? Tick + definition.Fuel : 0, IgnitedTick = Tick,
                    NextSpreadTick = burning ? Tick + definition.SpreadInterval : 0,
                    NextVisualTick = burning ? Tick + Math.Max(1, definition.Fuel / 255) : 0 } };
        }

        public void Write(int x, int y, in GridCell input) => Set(x, y, input, false);
        private void Set(int x, int y, GridCell cell, bool transfer)
        {
            if (!Inside(x, y)) throw new ArgumentOutOfRangeException("坐标越界。");
            int id = TileId(x, y); MaterialTile tile = Tiles[id];
            if (!tile.IsCreated && cell.IsEmpty) return;
            tile = EnsureTile(id); int p = Offset(x, y); ushort old = tile.Material[p]; byte oldFlags = tile.Flags[p];
            int oldComponent = tile.Component[p];
            if (!transfer && oldComponent != 0 && oldComponent != cell.ComponentHandle) ColdStore.Delete(oldComponent);
            if (cell.MaterialId != 0)
            {
                MaterialDefinition d = Definitions[cell.MaterialId];
                if (cell.ComponentHandle == 0 && (d.IsGas || cell.IsBurning || (d.IsBurnable && cell.Cold.FuelRemaining != 0 && cell.Cold.FuelRemaining != d.Fuel)))
                {
                    CellCold state = cell.Cold; state.MaterialId = cell.MaterialId; state.GridHandle = GridHandle; state.X = x; state.Y = y;
                    cell.ComponentHandle = ColdStore.Create(state);
                    ulong due = d.IsGas ? state.ExpiryTick : Math.Min(state.BurnEndTick, Math.Min(state.NextSpreadTick, state.NextVisualTick));
                    if (d.IsGas || cell.IsBurning) ColdStore.Schedule(cell.ComponentHandle, due);
                }
                else if (cell.ComponentHandle != 0)
                {
                    CellCold state = cell.Cold; state.MaterialId = cell.MaterialId; state.GridHandle = GridHandle; state.X = x; state.Y = y;
                    ColdStore.Write(cell.ComponentHandle, state);
                }
            }
            tile.Material[p] = cell.MaterialId; tile.Flags[p] = cell.Flags; tile.Due[p] = cell.NextMoveTick;
            if (old != 0 && (oldFlags & GridCell.BurningFlag) != 0) BurningCount--;
            if (cell.MaterialId != 0 && cell.IsBurning) BurningCount++;
            tile.Stamp[p] = cell.ProcessedTick; tile.Component[p] = cell.ComponentHandle;
            if (old != cell.MaterialId)
            {
                if (old != 0) { Counts[old]--; CellCount--; }
                if (cell.MaterialId != 0) { Counts[cell.MaterialId]++; CellCount++; }
            }
            uint bit = 1u << (x & 31); int row = y & 31;
            if (cell.MaterialId != 0) tile.Occupied[row] |= bit; else tile.Occupied[row] &= ~bit;
            tile.State[row] |= bit; RegisterStateTile(id); RegisterCommitTile(id);
            if (old != cell.MaterialId || oldFlags != cell.Flags || cell.IsBurning)
            { tile.Visual[row] |= bit; VisualTiles[id] = 1; }
            if (old != cell.MaterialId && (Definitions[old].IsStructure || Definitions[cell.MaterialId].IsStructure) || ((oldFlags ^ cell.Flags) & GridCell.FixedFlag) != 0)
            {
                tile.Topology[row] |= bit;
                if (TopologyTiles[id] == 0) _pendingTopology.Add(id);
                TopologyTiles[id] = 1;
            }
            tile.Water[row] &= ~bit; tile.Gas[row] &= ~bit;
            if (Definitions[cell.MaterialId].IsWater) { tile.Water[row] |= bit; Ready(id, false); }
            if (Definitions[cell.MaterialId].IsGas) { tile.Gas[row] |= bit; Ready(id, true); }
            WakeNeighborhood(x, y);
        }

        // 归属迁移不撤销组件。目标Write完成后句柄继续指向同一份冷状态。
        public GridCell Take(int x, int y)
        {
            GridCell value = Read(x, y); Set(x, y, default, true); return value;
        }

        public void WakeNeighborhood(int x, int y)
        {
            for (int py = y - 1; py <= y + 1; py++) for (int px = x - 1; px <= x + 1; px++) Wake(px, py);
        }

        public void Wake(int x, int y)
        {
            if (!Inside(x, y)) return;
            int id = TileId(x, y); MaterialTile tile = Tiles[id]; if (!tile.IsCreated) return;
            MaterialDefinition definition = Definitions[tile.Material[Offset(x, y)]];
            uint bit = 1u << (x & 31);
            if (definition.IsWater) { tile.Water[y & 31] |= bit; Ready(id, false); }
            if (definition.IsGas) { tile.Gas[y & 31] |= bit; Ready(id, true); }
        }

        private void Ready(int id, bool gas)
        {
            if (gas) { if (GasTiles[id] == 0) PendingGasTiles.Add(id); GasTiles[id] = 1; }
            else { if (WaterTiles[id] == 0) PendingWaterTiles.Add(id); WaterTiles[id] = 1; }
        }

        public bool Passable(int x, int y)
        {
            if (!Inside(x, y)) return false;
            MaterialTile tile = Tiles[TileId(x, y)];
            return !tile.IsCreated || tile.Material[Offset(x, y)] == 0 && (tile.Dynamic[y & 31] & (1u << (x & 31))) == 0;
        }

        public void SetDynamic(int x, int y, bool occupied)
        {
            if (!Inside(x, y)) return;
            MaterialTile tile = EnsureTile(TileId(x, y)); uint bit = 1u << (x & 31); int row = y & 31;
            bool old = (tile.Dynamic[row] & bit) != 0;
            if (occupied) tile.Dynamic[row] |= bit; else tile.Dynamic[row] &= ~bit;
            if (old != occupied) WakeNeighborhood(x, y);
        }

        public bool IsDynamic(int x, int y)
        {
            if (!Inside(x, y)) return false;
            MaterialTile tile = Tiles[TileId(x, y)]; return tile.IsCreated && (tile.Dynamic[y & 31] & (1u << (x & 31))) != 0;
        }

        public void TakeTopologyDirtyTiles(NativeList<int> destination)
        {
            destination.Clear();
            for (int i = 0; i < _pendingTopology.Length; i++)
            {
                int id = _pendingTopology[i];
                destination.Add(id); TopologyTiles[id] = 0;
                UnsafeUtility.MemClear(Tiles[id].Topology, 128);
            }
            _pendingTopology.Clear();
        }

        // Jobs置1，屏障后置2并登记；不扫描静止页，也不在任务内共享追加队列。
        public void RegisterStateTile(int id)
        {
            if (StateTiles[id] == 2) return;
            StateTiles[id] = 2; PendingStateTiles.Add(id);
        }

        public void RequestIgnition(int x, int y)
        {
            int id = TileId(x, y); MaterialTile tile = Tiles[id];
            if (!tile.IsCreated) return;
            tile.Ignition[y & 31] |= 1u << (x & 31);
            if (IgnitionTiles[id] == 0) PendingIgnitionTiles.Add(id);
            IgnitionTiles[id] = 1;
        }

        public void RegisterCommitTile(int id)
        {
            if (CommitTiles[id] == 2) return;
            CommitTiles[id] = 2; ChangedTiles.Add(id);
        }

        public void BeginCommit()
        {
            for (int i = 0; i < ChangedTiles.Length; i++) CommitTiles[ChangedTiles[i]] = 0;
            ChangedTiles.Clear();
        }

        public void MarkVisual(int x, int y)
        {
            int id = TileId(x, y); MaterialTile tile = Tiles[id];
            tile.Visual[y & 31] |= 1u << (x & 31); VisualTiles[id] = 1;
            RegisterCommitTile(id);
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            for (int i = 0; i < _allocated.Length; i++)
            {
                MaterialTile tile = Tiles[_allocated[i]];
                if (tile.IsCreated) _pagePool.Return((IntPtr)tile.Material);
            }
            Tiles.Dispose(); StateTiles.Dispose(); VisualTiles.Dispose(); TopologyTiles.Dispose();
            WaterTiles.Dispose(); GasTiles.Dispose(); IgnitionTiles.Dispose(); Counts.Dispose(); _allocated.Dispose();
            CommitTiles.Dispose(); ChangedTiles.Dispose();
            PendingWaterTiles.Dispose(); PendingGasTiles.Dispose();
            PendingStateTiles.Dispose(); PendingIgnitionTiles.Dispose(); _pendingTopology.Dispose();
            if (_ownsCold) ColdStore.Dispose();
            if (_ownsPagePool) _pagePool.Dispose();
        }
    }
}
