using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Rules
{
    public sealed class MovementCandidateResolver
    {
        private readonly int[] _candidates;
        private readonly int[] _writes;
        private readonly HashSet<CellKey> _moved;
        private readonly HashSet<CellKey> _reserved;
        private readonly HashSet<CellKey> _written;
        private readonly RuleIntent _output;

        public MovementCandidateResolver(int cellCapacity, int intentCapacity = -1)
        {
            if (cellCapacity < 1) throw new ArgumentOutOfRangeException(nameof(cellCapacity));
            _candidates = new int[checked(cellCapacity * 5)];
            _writes = new int[cellCapacity];
            _moved = new HashSet<CellKey>(cellCapacity);
            _reserved = new HashSet<CellKey>(cellCapacity);
            _written = new HashSet<CellKey>(cellCapacity);
            _output = new RuleIntent(intentCapacity < 0 ? cellCapacity : intentCapacity);
        }

        // 只裁决候选，不搬运世界状态。输出每源最多一个写入/移除/搬运。
        public IRuleBatch Resolve(IRuleBatch candidates)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            _output.Begin(candidates.Stage);
            _moved.Clear();
            _reserved.Clear();
            _written.Clear();
            if (!candidates.Result.IsSuccess) { _output.Fail(candidates.Result); return _output; }
            int moveCount = 0, writeCount = 0;
            ReadOnlySpan<MutationIntent> intents = candidates.Intents;
            for (int index = 0; index < intents.Length; index++)
            {
                MutationIntent intent = intents[index];
                if (intent.Kind == MutationKind.Move)
                {
                    if (moveCount == _candidates.Length || intent.Tier > MoveCandidateTier.OtherHorizontal)
                        return Fail(intent.Source, WorldErrorCode.CapacityExceeded, "移动候选容量或层级无效。");
                    _candidates[moveCount++] = index;
                }
                else if (intent.Kind == MutationKind.WriteState || intent.Kind == MutationKind.Remove)
                {
                    if (_written.Count == _writes.Length)
                        return Fail(intent.Source, WorldErrorCode.CapacityExceeded, "源状态缓冲不足。");
                    if (!_written.Add(intent.Source))
                        return Fail(intent.Source, WorldErrorCode.InvalidArgument, "同一源包含重复状态写入。");
                    _writes[writeCount++] = index;
                }
                else return Fail(intent.Source, WorldErrorCode.InvalidArgument, "移动批次只接受移动、状态和自然移除。");
            }
            SortIndices(_candidates, moveCount, intents, true);
            for (int i = 0; i < moveCount; i++)
            {
                MutationIntent intent = intents[_candidates[i]];
                if (_moved.Contains(intent.Source) || _reserved.Contains(intent.Target)) continue;
                if (_moved.Count == _writes.Length)
                    return Fail(intent.Source, WorldErrorCode.CapacityExceeded, "成功移动源数量超过容量。");
                _moved.Add(intent.Source);
                _reserved.Add(intent.Target);
                if (!_output.Add(intent)) return _output;
            }
            SortIndices(_writes, writeCount, intents, false);
            for (int i = 0; i < writeCount; i++)
            {
                MutationIntent intent = intents[_writes[i]];
                if (!_moved.Contains(intent.Source) && !_output.Add(intent)) return _output;
            }
            return _output;
        }

        private IRuleBatch Fail(CellKey key, WorldErrorCode code, string message)
        {
            _output.Fail(RuleBatchContext.Error(_output.Stage, code, key, message));
            return _output;
        }

        // 索引堆排序借用输入 Span，避免复制 5N 份完整 MutationIntent 或为比较器分配闭包。
        private static void SortIndices(int[] indices, int count, ReadOnlySpan<MutationIntent> intents, bool movement)
        {
            for (int root = count / 2 - 1; root >= 0; root--) Sift(indices, root, count, intents, movement);
            for (int end = count - 1; end > 0; end--)
            {
                int value = indices[0]; indices[0] = indices[end]; indices[end] = value;
                Sift(indices, 0, end, intents, movement);
            }
        }
        private static void Sift(int[] indices, int root, int count, ReadOnlySpan<MutationIntent> intents, bool movement)
        {
            while (root < count / 2)
            {
                int child = root * 2 + 1;
                if (child + 1 < count && Compare(intents[indices[child]], intents[indices[child + 1]], movement) < 0) child++;
                if (Compare(intents[indices[root]], intents[indices[child]], movement) >= 0) break;
                int value = indices[root]; indices[root] = indices[child]; indices[child] = value;
                root = child;
            }
        }
        private static int Compare(in MutationIntent a, in MutationIntent b, bool movement) => movement ?
            ContractDefaults.CompareMoveCandidates(a, b) : a.Source.Position.CompareTo(b.Source.Position);

        // 执行器内的五级候选生成与裁决。每层最多 N 个紧凑候选，输出最多 N 个完整意图。
        internal sealed class Phase
        {
            private readonly struct Candidate
            {
                internal readonly int Source;
                internal readonly CellKey Target;
                internal Candidate(int source, CellKey target) { Source = source; Target = target; }
            }
            private sealed class Comparer : IComparer<Candidate>
            {
                internal RuleBatchContext Input;
                public int Compare(Candidate a, Candidate b)
                {
                    int value = a.Target.Position.CompareTo(b.Target.Position);
                    return value == 0 ? Input.Keys[a.Source].Position.CompareTo(Input.Keys[b.Source].Position) : value;
                }
            }
            private readonly Candidate[] _candidates;
            private readonly CellSnapshot[] _states;
            private readonly bool[] _alive;
            private readonly bool[] _ready;
            private readonly bool[] _moved;
            private readonly HashSet<CellKey> _reserved;
            private readonly Comparer _comparer = new Comparer();
            private readonly LiquidSpreadPlanner _liquid;

            internal Phase(int capacity, bool liquid = false)
            {
                _candidates = new Candidate[capacity];
                _states = new CellSnapshot[capacity];
                _alive = new bool[capacity];
                _ready = new bool[capacity];
                _moved = new bool[capacity];
                _reserved = new HashSet<CellKey>(liquid ? checked(capacity * 2) : capacity);
                _liquid = liquid ? new LiquidSpreadPlanner(capacity) : null;
            }

            internal WorldResult Generate(RuleBatchContext input, RuleIntent output, RuleMask capability,
                int verticalDirection, IOccupancyView occupancy)
            {
                Array.Clear(_alive, 0, input.Count);
                Array.Clear(_ready, 0, input.Count);
                Array.Clear(_moved, 0, input.Count);
                _reserved.Clear();
                _comparer.Input = input;
                for (int i = 0; i < input.Count; i++)
                {
                    MaterialRuntimeEntry material = input.Materials[i];
                    if ((material.Rules & capability) == 0) continue;
                    CellKey source = input.Keys[i];
                    if (source.Position.OwnerKind != OwnerKind.Grid)
                        return RuleBatchContext.Error(output.Stage, WorldErrorCode.UnsupportedOperation, source, "网格流体不能归属动态材料体。");
                    CellSnapshot before = input.States[i];
                    uint lifetime = before.LifetimeTicksRemaining;
                    if (capability == RuleMask.GasDrift)
                    {
                        if (lifetime > 0) lifetime--;
                        if (lifetime == 0)
                        {
                            if (!output.Add(new MutationIntent(MutationKind.Remove, input.Instances[i], source, source,
                                default, removalReason: RemovalReason.LifetimeExpired))) return output.Result;
                            continue;
                        }
                    }
                    _alive[i] = true;
                    uint countdown = before.MoveCountdown;
                    if (countdown > 0) countdown--;
                    _ready[i] = countdown == 0;
                    if (_ready[i]) countdown = material.Parameters.MoveIntervalTicks;
                    _states[i] = new CellSnapshot(before.MaterialId, before.Flags, before.FuelTicksRemaining,
                        before.SpreadCountdown, lifetime, countdown, before.IgnitedTick);
                }
                if (capability == RuleMask.LiquidFlow)
                {
                    WorldResult chains = _liquid.GenerateChains(input, output, _states, _ready, _moved, _reserved, occupancy);
                    if (!chains.IsSuccess) return chains;
                }
                for (int tier = 0; tier < 5; tier++)
                {
                    if (tier == 3 && capability == RuleMask.LiquidFlow)
                    {
                        WorldResult horizontal = _liquid.PrepareHorizontal(input, _ready, _moved, occupancy);
                        if (!horizontal.IsSuccess) return horizontal;
                    }
                    int count = 0;
                    for (int i = 0; i < input.Count; i++)
                    {
                        if (!_ready[i] || _moved[i]) continue;
                        CellKey source = input.Keys[i];
                        int x = source.Position.X, y = source.Position.Y;
                        bool preferLeft = ContractDefaults.PreferLeft(input.Transaction.WorkingTick, x, y, input.Snapshot.Config.Seed);
                        int dx = tier == 0 ? 0 : ((tier == 1 || tier == 3) == preferLeft ? -1 : 1);
                        if (tier >= 3 && capability == RuleMask.LiquidFlow)
                        {
                            dx = _liquid.HorizontalDirection(i, tier);
                            if (dx == 0) continue;
                        }
                        int dy = tier < 3 ? verticalDirection : 0;
                        if (tier == 1 || tier == 2)
                        {
                            WorldResult side = input.Passable(x + dx, y, occupancy, out bool sideOpen);
                            if (!side.IsSuccess) return side;
                            if (!sideOpen) continue;
                        }
                        WorldResult result = input.Passable(x + dx, y + dy, occupancy, out bool open);
                        if (!result.IsSuccess) return result;
                        if (!open) continue;
                        var target = new CellKey(source.Generation, new CellPositionKey(OwnerKind.Grid, 0, x + dx, y + dy));
                        _candidates[count++] = new Candidate(i, target);
                    }
                    Array.Sort(_candidates, 0, count, _comparer);
                    for (int i = 0; i < count; i++)
                    {
                        Candidate candidate = _candidates[i];
                        if (!_reserved.Add(candidate.Target)) continue;
                        int source = candidate.Source;
                        _moved[source] = true;
                        if (!output.Add(new MutationIntent(MutationKind.Move, input.Instances[source], input.Keys[source],
                            candidate.Target, _states[source], (MoveCandidateTier)tier))) return output.Result;
                    }
                }
                for (int i = 0; i < input.Count; i++)
                    if (_alive[i] && !_moved[i] && !RuleBatchContext.SameState(input.States[i], _states[i]) &&
                        !output.Add(new MutationIntent(MutationKind.WriteState, input.Instances[i], input.Keys[i], input.Keys[i], _states[i])))
                        return output.Result;
                return WorldResult.Success();
            }
        }
    }
}
