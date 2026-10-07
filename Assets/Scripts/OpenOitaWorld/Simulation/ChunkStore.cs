using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Simulation;

public sealed class ChunkStore : IDisposable
{
    // 目录独立，未写入的区块共享；只有首次写入共享块才复制其材料数组。
    private sealed class SharedChunk
    {
        internal readonly WorldChunk Chunk;
        internal int References = 1;
        internal SharedChunk(WorldChunk chunk) { Chunk = chunk; }
        internal void Release() { if (--References == 0) Chunk.Dispose(); }
    }
    private readonly Dictionary<ChunkCoord, SharedChunk> _chunks = new();
    private readonly long _memoryLimit;
    private bool _disposed;
    public int Width { get; }
    public int Height { get; }
    public int ChunkCount => _chunks.Count;
    public long StorageBytes => _chunks.Count * WorldChunk.StorageBytes;
    internal int CopiedChunks { get; private set; }

    public ChunkStore(int width = 256, int height = 256, long memoryLimit = ContractDefaults.CpuBudgetBytes)
    {
        if (width < 1 || width > 4096 || height < 1 || height > 4096 || memoryLimit < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _memoryLimit = memoryLimit;
    }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public WorldResult Read(int x, int y, out CellState cell)
    {
        cell = default;
        if (_disposed) return Error(WorldErrorCode.Disposed, "存储已释放。");
        if (!Contains(x, y)) return Error(WorldErrorCode.OutOfBounds, "坐标超出实际宽高。");
        if (_chunks.TryGetValue(ChunkCoord.FromCell(x, y), out SharedChunk chunk))
            cell = chunk.Chunk.Read(ChunkCoord.CellIndex(x % 128, y % 128));
        return WorldResult.Success();
    }

    public bool TryGetCell(int worldX, int worldY, out CellState cell) => Read(worldX, worldY, out cell).IsSuccess;
    internal WorldResult Write(int x, int y, in CellState state)
    {
        WorldResult read = Read(x, y, out CellState before);
        if (!read.IsSuccess) return read;
        if (before.MaterialId == state.MaterialId && before.Flags == state.Flags &&
            before.FuelTicksRemaining == state.FuelTicksRemaining && before.SpreadCountdown == state.SpreadCountdown &&
            before.LifetimeTicksRemaining == state.LifetimeTicksRemaining && before.MoveCountdown == state.MoveCountdown &&
            before.IgnitedTick == state.IgnitedTick) return WorldResult.Success();
        ChunkCoord coord = ChunkCoord.FromCell(x, y);
        if (!_chunks.TryGetValue(coord, out SharedChunk shared))
        {
            if (state.MaterialId == 0) return WorldResult.Success();
            if (StorageBytes + WorldChunk.StorageBytes > _memoryLimit)
                return Error(WorldErrorCode.CapacityExceeded, "区块分配超过 CPU 预算。");
            var chunk = new WorldChunk(coord);
            try
            {
                chunk.Allocate();
                shared = new SharedChunk(chunk);
                _chunks.Add(coord, shared);
            }
            catch
            {
                chunk.Dispose();
                throw;
            }
        }
        else if (shared.References > 1)
        {
            // 复制成功前不改变目录或引用数，失败候选不能破坏旧世界。
            WorldChunk detached = shared.Chunk.Clone();
            SharedChunk replacement;
            try { replacement = new SharedChunk(detached); }
            catch { detached.Dispose(); throw; }
            _chunks[coord] = replacement;
            shared.Release();
            shared = replacement;
            CopiedChunks++;
        }
        shared.Chunk.Write(ChunkCoord.CellIndex(x % 128, y % 128), state);
        return WorldResult.Success();
    }

    internal bool HasChunk(ChunkCoord coord) => _chunks.ContainsKey(coord);
    internal ChunkStore Clone()
    {
        using var timing = WorldStepMetrics.Measure(WorldStepMetrics.Timing.Copy);
        if (_disposed) throw new ObjectDisposedException(nameof(ChunkStore));
        var copy = new ChunkStore(Width, Height, _memoryLimit);
        try
        {
            foreach (KeyValuePair<ChunkCoord, SharedChunk> pair in _chunks)
            {
                if (pair.Value.References == int.MaxValue) throw new InvalidOperationException("区块共享引用数超限。");
                copy._chunks.Add(pair.Key, pair.Value);
                pair.Value.References++;
            }
            WorldStepMetrics.Add(WorldStepMetrics.Work.DirectoryCopies);
            WorldStepMetrics.Add(WorldStepMetrics.Work.DirectoryEntries, _chunks.Count);
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
        if (_disposed) return;
        foreach (SharedChunk chunk in _chunks.Values) chunk.Release();
        _chunks.Clear();
        _disposed = true;
    }

    private static WorldResult Error(WorldErrorCode code, string message) => WorldResult.Failure(code,
        new WorldDiagnostic("ChunkStore", "cell", message));
}
