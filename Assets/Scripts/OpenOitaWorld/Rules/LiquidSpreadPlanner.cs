using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using OpenOita.Simulation;

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
        private readonly int[] _links;
        private readonly byte[] _solid;
        private readonly int[] _landingKeys;
        private readonly int[] _landingY;
        private readonly SurfaceComparer _surfaceComparer = new SurfaceComparer();
        private int _stamp;

        // 原搜索/预留缓冲、三邻接、固体标记与有界列落点缓存的保守上界。
        internal static long ReservedBytes(int capacity) => checked(capacity * 168L + 256L);

        internal LiquidSpreadPlanner(int capacity)
        {
            _queue = new int[capacity];
            _parents = new int[capacity];
            _seen = new int[capacity];
            _surface = new int[capacity];
            _preferred = new int[capacity];
            _other = new int[capacity];
            _links = new int[checked(capacity * 3)];
            _solid = new byte[capacity];
            int landingCapacity = 1;
            while (landingCapacity < checked(capacity * 4)) landingCapacity = checked(landingCapacity * 2);
            _landingKeys = new int[landingCapacity];
            _landingY = new int[landingCapacity];
        }

        internal WorldResult GenerateChains(RuleBatchContext input, RuleIntent output, CellSnapshot[] states,
            bool[] ready, bool[] moved, HashSet<CellKey> reserved, IOccupancyView occupancy)
        {
            Array.Clear(_preferred, 0, input.Count);
            Array.Clear(_other, 0, input.Count);
            Array.Fill(_links, -3, 0, checked(input.Count * 3));
            Array.Clear(_solid, 0, input.Count);
            Array.Fill(_landingKeys, -1);
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
            // 热循环先累加局部整数，每次搜索结束才合入世界诊断。
            int visitedNodes = 0, checkedEdges = 0, linkHits = 0;
            try
            {
                last = -1;
                WorldStepMetrics.Add(WorldStepMetrics.Work.LiquidSearches);
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
                    visitedNodes++;
                    CellPositionKey position = input.Keys[current].Position;
                    for (int direction = 0; direction < 3; direction++)
                    {
                        checkedEdges++;
                        int dx = direction == 0 ? 0 : ((direction == 1) == left ? -1 : 1);
                        int x = position.X + dx;
                        int y = position.Y + (direction == 0 ? -1 : 0);
                        if (x < 0 || y < 0 || x >= input.Snapshot.Config.Width || y >= input.Snapshot.Config.Height) continue;
                        int link = current * 3 + (dx == 0 ? 0 : dx < 0 ? 1 : 2);
                        int next = _links[link];
                        if (next == -3)
                            _links[link] = next = input.TryGetGridIndex(x, y, out int neighbour) ? neighbour : -4;
                        else linkHits++;
                        if (next >= 0)
                        {
                            // 链选择期间，所有已预留的原占据节点都已标记moved。
                            // 原空末端另查reserved；不能把此等价关系用于后续普通移动。
                            if (_seen[next] == _stamp || moved[next] || !ready[next] ||
                                input.States[next].MaterialId != input.States[source].MaterialId ||
                                (input.Materials[next].Rules & RuleMask.LiquidFlow) == 0) continue;
                            if (_solid[next] == 0)
                            {
                                WorldResult solid = input.ClearOfSolids(x, y, occupancy, out bool clear);
                                if (!solid.IsSuccess) return solid;
                                _solid[next] = clear ? (byte)1 : (byte)2;
                            }
                            if (_solid[next] == 2) continue;
                            _seen[next] = _stamp;
                            _parents[next] = current;
                            _queue[count++] = next;
                        }
                        else if (y < terminalY)
                        {
                            var key = new CellKey(input.Snapshot.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
                            if (reserved.Contains(key)) continue;
                            if (next == -4)
                            {
                                WorldResult open = input.Passable(x, y, occupancy, out bool clear);
                                if (!open.IsSuccess) return open;
                                _links[link] = next = clear ? -1 : -2;
                            }
                            if (next == -2) continue;
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
            finally
            {
                WorldStepMetrics.Add(WorldStepMetrics.Work.LiquidNodes, visitedNodes);
                WorldStepMetrics.Add(WorldStepMetrics.Work.LiquidEdges, checkedEdges);
                WorldStepMetrics.Add(WorldStepMetrics.Work.LiquidLinkHits, linkHits);
            }
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

        private WorldResult FindOutlet(RuleBatchContext input, int x, int y, int direction,
            IOccupancyView occupancy, out int landingY, out int distance)
        {
            landingY = y;
            distance = 0;
            for (int nextX = x + direction; nextX >= 0 && nextX < input.Snapshot.Config.Width; nextX += direction)
            {
                WorldStepMetrics.Add(WorldStepMetrics.Work.OutletProbes);
                WorldResult result = input.Passable(nextX, y, occupancy, out bool open);
                if (!result.IsSuccess) return result;
                if (!open) break;
                result = Landing(input, nextX, y, occupancy, out int lowerY);
                if (!result.IsSuccess) return result;
                int path = Math.Abs(nextX - x);
                if (lowerY < landingY || (lowerY == landingY && lowerY < y && path < distance))
                {
                    landingY = lowerY;
                    distance = path;
                    // 已到世界最低行；后续更远的列不可能更低，也不可能赢得距离平局。
                    if (landingY == 0) break;
                }
            }
            return WorldResult.Success();
        }

        private WorldResult Landing(RuleBatchContext input, int x, int y, IOccupancyView occupancy, out int lowerY)
        {
            int key = y * input.Snapshot.Config.Width + x;
            int slot = (int)((uint)key * 2654435761u) & (_landingKeys.Length - 1);
            if (_landingKeys[slot] == key)
            {
                WorldStepMetrics.Add(WorldStepMetrics.Work.LandingCacheHits);
                lowerY = _landingY[slot];
                return WorldResult.Success();
            }
            lowerY = y;
            while (lowerY > 0)
            {
                WorldStepMetrics.Add(WorldStepMetrics.Work.OutletProbes);
                WorldResult result = input.Passable(x, lowerY - 1, occupancy, out bool open);
                if (!result.IsSuccess) return result;
                if (!open) break;
                lowerY--;
            }
            _landingKeys[slot] = key;
            _landingY[slot] = lowerY;
            return WorldResult.Success();
        }
    }
}
