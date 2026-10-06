using System;
using System.Collections.Generic;
using OpenOita.Contracts;

namespace OpenOita.Rules
{
    // 每阶段重建。只保存本批索引，不持有跨 Tick 实例或材料状态。
    internal sealed class LiquidSpreadPlanner
    {
        private sealed class SurfaceComparer : IComparer<int>
        {
            internal RuleBatchContext Input;
            public int Compare(int first, int second)
            {
                CellPositionKey a = Input.Keys[first].Position;
                CellPositionKey b = Input.Keys[second].Position;
                int value = b.Y.CompareTo(a.Y);
                return value == 0 ? a.X.CompareTo(b.X) : value;
            }
        }

        private readonly int[] _queue;
        private readonly int[] _parents;
        private readonly int[] _seen;
        private readonly int[] _surface;
        private readonly int[] _preferred;
        private readonly int[] _other;
        private readonly SurfaceComparer _surfaceComparer = new SurfaceComparer();
        private int _stamp;

        // 六个 int[N] 与水阶段新增的 N 个预留集合槽位（含哈希表容量余量）。
        internal static long ReservedBytes(int capacity) => checked(capacity * 88L + 256L);

        internal LiquidSpreadPlanner(int capacity)
        {
            _queue = new int[capacity];
            _parents = new int[capacity];
            _seen = new int[capacity];
            _surface = new int[capacity];
            _preferred = new int[capacity];
            _other = new int[capacity];
        }

        internal WorldResult GenerateChains(RuleBatchContext input, RuleIntent output, CellSnapshot[] states,
            bool[] ready, bool[] moved, HashSet<CellKey> reserved, IOccupancyView occupancy)
        {
            Array.Clear(_preferred, 0, input.Count);
            Array.Clear(_other, 0, input.Count);
            _surfaceComparer.Input = input;
            int count = 0;
            for (int i = 0; i < input.Count; i++)
            {
                if (!ready[i]) continue;
                CellPositionKey position = input.Keys[i].Position;
                WorldResult result = input.Passable(position.X, position.Y + 1, occupancy, out bool open);
                if (!result.IsSuccess) return result;
                if (open) _surface[count++] = i;
            }
            Array.Sort(_surface, 0, count, _surfaceComparer);
            for (int i = 0; i < count; i++)
            {
                int source = _surface[i];
                if (moved[source] || _preferred[source] == 2) continue;
                WorldResult result = FindChain(input, source, ready, moved, reserved, occupancy,
                    out int last, out CellKey terminal);
                if (!result.IsSuccess) return result;
                if (last < 0) continue;
                CellKey target = terminal;
                for (int current = last; current >= 0; current = _parents[current])
                {
                    CellKey key = input.Keys[current];
                    MoveCandidateTier tier = target.Position.Y < key.Position.Y ?
                        MoveCandidateTier.Vertical : MoveCandidateTier.PreferredHorizontal;
                    if (!output.Add(new MutationIntent(MutationKind.Move, input.Instances[current], key,
                        target, states[current], tier))) return output.Result;
                    moved[current] = true;
                    reserved.Add(key);
                    reserved.Add(target);
                    target = key;
                }
            }
            return WorldResult.Success();
        }

        private WorldResult FindChain(RuleBatchContext input, int source, bool[] ready, bool[] moved,
            HashSet<CellKey> reserved, IOccupancyView occupancy, out int last, out CellKey terminal)
        {
            last = -1;
            terminal = default;
            if (_stamp == int.MaxValue)
            {
                Array.Clear(_seen, 0, _seen.Length);
                _stamp = 0;
            }
            _stamp++;
            _seen[source] = _stamp;
            _parents[source] = -1;
            _queue[0] = source;
            int count = 1;
            CellPositionKey origin = input.Keys[source].Position;
            bool left = ContractDefaults.PreferLeft(input.Transaction.WorkingTick,
                origin.X, origin.Y, input.Snapshot.Config.Seed);
            int terminalY = origin.Y;
            for (int head = 0; head < count; head++)
            {
                int current = _queue[head];
                CellPositionKey position = input.Keys[current].Position;
                for (int direction = 0; direction < 3; direction++)
                {
                    int dx = direction == 0 ? 0 : ((direction == 1) == left ? -1 : 1);
                    int x = position.X + dx;
                    int y = position.Y + (direction == 0 ? -1 : 0);
                    if (x < 0 || y < 0 || x >= input.Snapshot.Config.Width || y >= input.Snapshot.Config.Height) continue;
                    var key = new CellKey(input.Snapshot.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
                    if (reserved.Contains(key)) continue;
                    if (input.TryGetGridIndex(x, y, out int next))
                    {
                        if (_seen[next] == _stamp || moved[next] || !ready[next] ||
                            input.States[next].MaterialId != input.States[source].MaterialId ||
                            (input.Materials[next].Rules & RuleMask.LiquidFlow) == 0) continue;
                        WorldResult solid = input.ClearOfSolids(x, y, occupancy, out bool clear);
                        if (!solid.IsSuccess) return solid;
                        if (!clear) continue;
                        _seen[next] = _stamp;
                        _parents[next] = current;
                        _queue[count++] = next;
                    }
                    else if (y < terminalY)
                    {
                        WorldResult open = input.Passable(x, y, occupancy, out bool clear);
                        if (!open.IsSuccess) return open;
                        if (!clear) continue;
                        terminalY = y;
                        terminal = key;
                        last = current;
                    }
                }
            }
            if (last < 0)
            {
                // 搜索只向下/横向；无低于根的出口也意味着所有可达节点无更低出口。
                // 本阶段预留只会减少可达集合，因此此失败结论可在阶段内复用。
                for (int i = 0; i < count; i++) _preferred[_queue[i]] = 2;
            }
            return WorldResult.Success();
        }

        internal WorldResult PrepareHorizontal(RuleBatchContext input, bool[] ready, bool[] moved,
            IOccupancyView occupancy)
        {
            Array.Clear(_preferred, 0, input.Count);
            for (int i = 0; i < input.Count; i++)
            {
                if (!ready[i] || moved[i]) continue;
                CellPositionKey source = input.Keys[i].Position;
                WorldResult result = FindOutlet(input, source.X, source.Y, -1, occupancy,
                    out int leftY, out int leftDistance);
                if (!result.IsSuccess) return result;
                result = FindOutlet(input, source.X, source.Y, 1, occupancy, out int rightY, out int rightDistance);
                if (!result.IsSuccess) return result;
                bool hasLeft = leftDistance != 0;
                bool hasRight = rightDistance != 0;
                if (!hasLeft && !hasRight) continue;
                bool preferLeft = leftY < rightY || (leftY == rightY && (leftDistance < rightDistance ||
                    (leftDistance == rightDistance && ContractDefaults.PreferLeft(input.Transaction.WorkingTick,
                        source.X, source.Y, input.Snapshot.Config.Seed))));
                if (!hasRight) preferLeft = true;
                if (!hasLeft) preferLeft = false;
                _preferred[i] = preferLeft ? -1 : 1;
                _other[i] = hasLeft && hasRight ? -_preferred[i] : 0;
            }
            return WorldResult.Success();
        }

        internal int HorizontalDirection(int source, int tier) => tier == 3 ? _preferred[source] : _other[source];

        private static WorldResult FindOutlet(RuleBatchContext input, int x, int y, int direction,
            IOccupancyView occupancy, out int landingY, out int distance)
        {
            landingY = y;
            distance = 0;
            for (int nextX = x + direction; nextX >= 0 && nextX < input.Snapshot.Config.Width; nextX += direction)
            {
                WorldResult result = input.Passable(nextX, y, occupancy, out bool open);
                if (!result.IsSuccess) return result;
                if (!open) break;
                int lowerY = y;
                while (lowerY > 0)
                {
                    result = input.Passable(nextX, lowerY - 1, occupancy, out open);
                    if (!result.IsSuccess) return result;
                    if (!open) break;
                    lowerY--;
                }
                int path = Math.Abs(nextX - x);
                if (lowerY < landingY || (lowerY == landingY && lowerY < y && path < distance))
                {
                    landingY = lowerY;
                    distance = path;
                }
            }
            return WorldResult.Success();
        }
    }
}
