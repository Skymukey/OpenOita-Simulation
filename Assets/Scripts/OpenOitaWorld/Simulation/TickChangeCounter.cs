using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Simulation
{
    internal sealed class TickChangeCounter : ITickChangeCounter
    {
        private HashSet<CellPositionKey> _writes = new();
        private readonly HashSet<CellPositionKey> _candidates = new();
        private readonly int _capacity;
        public int Count => _writes.Count;
        internal IEnumerable<CellPositionKey> Writes => _writes;
        public int ProjectedCount => Count + _candidates.Count;
        internal TickChangeCounter(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public WorldResult Preflight(ReadOnlySpan<CellPositionKey> writes, int maxChangesPerTick)
        {
            _candidates.Clear();
            int limit = Math.Min(_capacity, maxChangesPerTick);
            if (limit < Count) return Failure();
            foreach (CellPositionKey key in writes)
            {
                if (!_writes.Contains(key)) _candidates.Add(key);
                if ((long)Count + _candidates.Count > limit) return Failure();
            }
            return WorldResult.Success();
        }

        public void Record(ReadOnlySpan<CellPositionKey> writes)
        {
            if (!Preflight(writes, _capacity).IsSuccess)
                throw new InvalidOperationException("必须在写入前通过整 Tick 变更容量检查。");
            foreach (CellPositionKey key in writes) _writes.Add(key);
            _candidates.Clear();
        }

        // 分配在准备/预检阶段完成，Apply 只采用集合，避免逐项 Record 中途失败。
        internal TickChangeCounter PrepareRecord(ReadOnlySpan<CellPositionKey> writes)
        {
            var copy = new TickChangeCounter(_capacity) { _writes = new HashSet<CellPositionKey>(_writes) };
            copy.Record(writes);
            return copy;
        }

        internal void Adopt(TickChangeCounter candidate)
        {
            _writes = candidate._writes;
            _candidates.Clear();
        }

        private WorldResult Failure()
        {
            _candidates.Clear();
            return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                new WorldDiagnostic("Preflight", "maxChangesPerTick", "整个 Tick 的不同权威位置数超限。"));
        }
    }
}
