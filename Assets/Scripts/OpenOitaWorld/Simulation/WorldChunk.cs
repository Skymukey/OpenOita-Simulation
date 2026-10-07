using System;
using Unity.Collections;
using OpenOita.Simulation;

public sealed class WorldChunk : IDisposable
{
    private NativeArray<CellState> _cells;
    private NativeArray<ushort> _materialIds;
    private bool _disposed;
    public ChunkCoord Coord { get; }
    public ChunkAllocationState State => _cells.IsCreated ? ChunkAllocationState.Allocated : ChunkAllocationState.Empty;
    public bool IsEmpty => !_cells.IsCreated;
    public bool HasCells => _cells.IsCreated;
    public bool Dirty { get; internal set; }
    public NativeArray<CellState>.ReadOnly Cells => _cells.AsReadOnly();

    // 兼容旧显示消费者；每次从唯一权威 Cells 派生，回写缓存不会改变材料状态。
    public NativeArray<ushort> MaterialIds
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WorldChunk));
            if (_cells.IsCreated)
            {
                for (int i = 0; i < _cells.Length; i++) _materialIds[i] = _cells[i].MaterialId;
            }
            return _materialIds;
        }
    }

    internal const long StorageBytes = (32L + sizeof(ushort)) * WorldConstants.CellsPerChunk;
    internal WorldChunk(ChunkCoord coord) { Coord = coord; }

    internal void Allocate()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorldChunk));
        if (_cells.IsCreated) return;
        try
        {
            _cells = new NativeArray<CellState>(WorldConstants.CellsPerChunk, Allocator.Persistent);
            _materialIds = new NativeArray<ushort>(WorldConstants.CellsPerChunk, Allocator.Persistent);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal CellState Read(int index) => _cells.IsCreated ? _cells[index] : default;
    internal void Write(int index, in CellState state)
    {
        _cells[index] = state;
        Dirty = true;
    }

    internal WorldChunk Clone()
    {
        using var timing = WorldStepMetrics.Measure(WorldStepMetrics.Timing.Copy);
        var copy = new WorldChunk(Coord);
        try
        {
            if (_cells.IsCreated)
            {
                copy.Allocate();
                NativeArray<CellState>.Copy(_cells, copy._cells);
                WorldStepMetrics.Add(WorldStepMetrics.Work.ChunkCopies);
                WorldStepMetrics.Add(WorldStepMetrics.Work.CopiedItems, _cells.Length);
                WorldStepMetrics.Add(WorldStepMetrics.Work.CopiedPayloadBytes, (long)_cells.Length * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CellState>());
            }
            copy.Dirty = Dirty;
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_cells.IsCreated) _cells.Dispose();
        if (_materialIds.IsCreated) _materialIds.Dispose();
        _disposed = true;
    }
}
