using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Rules;
using OpenOita.Simulation;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Rules
{
    // 独立几何配方及内存提交器，只验 M03 规则；不冒充正式 Step/Remove/Reset。
    internal sealed class LiquidLevelingFixture : IWorkingWorldView
    {
        private const int Side = 256;
        private readonly CellSnapshot[] _cells = new CellSnapshot[Side * Side];
        private readonly CellKey[] _keys = new CellKey[Side * Side];
        private readonly int[] _keySlots = new int[Side * Side];
        private readonly CellSnapshot[] _expectedStates = new CellSnapshot[4608];
        private readonly bool[] _expectedWater = new bool[4608];
        private readonly int[] _moveSlots = new int[4608];
        private readonly LiquidChainValidation _chains = new LiquidChainValidation(4608);
        private readonly HashSet<int> _solids = new HashSet<int>();
        private readonly HashSet<CellKey> _sources = new HashSet<CellKey>();
        private readonly HashSet<CellKey> _targets = new HashSet<CellKey>();
        private readonly RuleFixture _baseline;
        private readonly bool _reverse;
        private int _count;
        internal TickInstanceMap Instances { get; private set; }
        internal readonly string Name;
        internal readonly int Scale;
        internal readonly int WaterCount;
        internal readonly List<LiquidTickRecord> Records = new List<LiquidTickRecord>(2304);
        internal bool InnerWallRemoved;
        internal int BoundaryCrossings;
        internal int LastMoves;
        internal ulong LastBatchHash;
        internal int FirstGeometryTick = -1;
        public WorldConfig Config => _baseline.Config;
        public Vector2 Origin => Vector2.zero;
        public ulong Generation => 1;
        public ulong WorkingTick { get; private set; }
        public ReadOnlySpan<CellKey> OccupiedCells => _keys.AsSpan(0, _count);
        public ReadOnlySpan<BodySnapshot> Bodies => ReadOnlySpan<BodySnapshot>.Empty;
        internal IMaterialRuntimeTable Materials => _baseline.Materials;
        internal LiquidFlowRule Water => _baseline.Rules.Water;

        internal LiquidLevelingFixture(string name, int scale, uint seed, bool reverse = false)
        {
            Name = name;
            Scale = scale;
            WaterCount = name == "F3" ? 1 : 16 * scale * scale;
            _reverse = reverse;
            // 复用公共 JSON/材料加载；容量包含所有混凝土，不把墙从规则快照隐藏。
            _baseline = new RuleFixture(seed, capacity: 4608);
            LocalRectangle(0, 24, 0, 1, 102);
            LocalRectangle(0, 1, 1, 12, 102);
            LocalRectangle(23, 24, 1, 12, 102);
            if (name != "F3") LocalRectangle(3, 7, 15, 19, 101);
            if (name == "F2") LocalRectangle(3, 9, 1, 2, 102);
            if (name == "F3")
            {
                for (int x = 40; x < 216; x++) if (x != 180) Put(x, 80, 102);
                Put(60, 81, 101);
            }
            if (name == "F4")
                for (int y = 56; y < 256; y++) Put(128, y, 102);
            RefreshKeys();
            CheckTotals();
        }

        private void LocalRectangle(int x0, int x1, int y0, int y1, ushort material)
        {
            int left = 32 + Scale * x0, right = 32 + Scale * x1;
            int bottom = 48 + Scale * y0, top = 48 + Scale * y1;
            // 反序初态构造与区块枚举是独立输入变体，最终几何不变。
            for (int row = bottom; row < top; row++)
                for (int column = left; column < right; column++)
                    Put(_reverse ? right - 1 - (column - left) : column,
                        _reverse ? top - 1 - (row - bottom) : row, material);
        }

        private void Put(int x, int y, ushort material)
        {
            Materials.TryGet(material, out MaterialRuntimeEntry entry);
            _cells[Index(x, y)] = CellState.Create(entry).Snapshot;
            if (material == 102) _solids.Add(Index(x, y));
        }

        private static int Index(int x, int y) => y * Side + x;
        internal CellSnapshot State(int x, int y) => _cells[Index(x, y)];
        private CellSnapshot State(CellKey key) => State(key.Position.X, key.Position.Y);
        internal void Begin()
        {
            Instances?.Close();
            WorkingTick++;
            Instances = new TickInstanceMap(Generation, WorkingTick);
            foreach (CellKey key in OccupiedCells) Instances.Create(key);
        }

        internal TransactionContext Context => new TransactionContext(new WorldVersion(Generation, WorkingTick - 1), WorkingTick, TickStage.Water);
        internal IRuleBatch Generate(IRuleExecutor executor = null) => (executor ?? Water).Execute(this, Materials,
            new ReadOnlyInstances(Instances), null, Context);

        internal LiquidTickRecord Step()
        {
            Begin();
            IRuleBatch generated = Generate();
            Assert.That(generated.Result.IsSuccess, Is.True, $"{Name} Tick {WorkingTick}: {generated.Result.Diagnostic.Message}");
            IRuleBatch batch = _baseline.Rules.Movement.Resolve(generated);
            ApplyChecked(batch);
            LiquidGeometry geometry = Measure();
            var record = new LiquidTickRecord((int)WorkingTick, WaterCount, LastMoves, geometry, OccupancyHash(), FullStateHash(), LastBatchHash);
            Records.Add(record);
            return record;
        }

        internal void ApplyChecked(IRuleBatch batch)
        {
            if (!batch.Result.IsSuccess) Assert.Fail($"{Name} Tick {WorkingTick}: {batch.Result.Diagnostic.Message}");
            _sources.Clear();
            _targets.Clear();
            LastMoves = 0;
            LastBatchHash = 0;
            for (int slot = 0; slot < _count; slot++)
            {
                CellSnapshot before = State(_keys[slot]);
                _expectedWater[slot] = before.MaterialId == 101;
                if (!_expectedWater[slot]) continue;
                uint countdown = before.MoveCountdown == 0 ? 0 : before.MoveCountdown - 1;
                if (countdown == 0) countdown = 2;
                _expectedStates[slot] = new CellSnapshot(before.MaterialId, before.Flags,
                    before.FuelTicksRemaining, before.SpreadCountdown, before.LifetimeTicksRemaining,
                    countdown, before.IgnitedTick);
            }
            // 整批预检基于同一阶段快照；仅接受通过完整有向链校验的同材让位。
            _chains.Validate(this, Materials, batch);
            int intentIndex = 0;
            foreach (MutationIntent intent in batch.Intents)
            {
                _moveSlots[intentIndex++] = _keySlots[Index(intent.Source.Position.X, intent.Source.Position.Y)];
                LastBatchHash ^= HashState(intent.State, Index(intent.Source.Position.X, intent.Source.Position.Y)) ^
                    Mix((ulong)Index(intent.Target.Position.X, intent.Target.Position.Y) + 1 + ((ulong)intent.Kind << 32) + ((ulong)intent.Tier << 40));
                CellSnapshot before = State(intent.Source);
                Require(before.MaterialId == 101 && _sources.Add(intent.Source), "非水源或一源多次处理");
                Require(Instances.TryResolve(intent.Instance, out CellKey source) && source.Equals(intent.Source), "实例与源不一致");
                uint countdown = before.MoveCountdown == 0 ? 0 : before.MoveCountdown - 1;
                if (countdown == 0) countdown = 2;
                Require(SameState(intent.State, new CellSnapshot(before.MaterialId, before.Flags,
                    before.FuelTicksRemaining, before.SpreadCountdown, before.LifetimeTicksRemaining,
                    countdown, before.IgnitedTick)), "完整状态或倒计时未继承");
                if (intent.Kind == MutationKind.WriteState)
                {
                    Require(intent.Target.Equals(intent.Source), "倒计时写入改变坐标");
                    continue;
                }
                Require(intent.Kind == MutationKind.Move && _targets.Add(intent.Target), "非移动意图或一目标多源");
                int x = intent.Target.Position.X, y = intent.Target.Position.Y;
                Require(x >= 0 && x < Side && y >= 0 && y < Side, "越过世界边界");
                Require(!_solids.Contains(Index(x, y)), "目标穿墙");
                int dx = x - intent.Source.Position.X, dy = y - intent.Source.Position.Y;
                Require(Math.Abs(dx) <= 1 && (dy == -1 || dy == 0) && (dx != 0 || dy != 0), "瞬移、上移或零步长移动");
                Require(before.MoveCountdown <= 1, "移动间隔未到却搬运");
                if (dy == -1 && dx != 0)
                    Require(State(intent.Source.Position.X + dx, intent.Source.Position.Y).MaterialId == 0, "斜向封闭墙角穿透");
                if (intent.Source.Position.X / 128 != x / 128 || intent.Source.Position.Y / 128 != y / 128) BoundaryCrossings++;
                LastMoves++;
            }
            // 实例按链尾到链首迁移；先清所有源，再写所有目标，避免覆盖链内材料。
            foreach (int index in _chains.TailFirstOrder)
            {
                MutationIntent intent = batch.Intents[index];
                Instances.Move(intent.Instance, intent.Target);
            }
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind == MutationKind.Move)
                {
                    int sourceIndex = Index(intent.Source.Position.X, intent.Source.Position.Y);
                    _keySlots[sourceIndex] = -1;
                    _cells[sourceIndex] = default;
                }
            }
            intentIndex = 0;
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind == MutationKind.Move)
                {
                    int slot = _moveSlots[intentIndex];
                    _keys[slot] = intent.Target;
                    _keySlots[Index(intent.Target.Position.X, intent.Target.Position.Y)] = slot;
                }
                intentIndex++;
                _cells[Index(intent.Target.Position.X, intent.Target.Position.Y)] = intent.State;
                Require(Instances.TryGetInstance(intent.Target, out CellInstanceHandle after) && after.Equals(intent.Instance), "搬运后实例未保留");
            }
            for (int slot = 0; slot < _count; slot++)
                if (_expectedWater[slot]) Require(SameState(State(_keys[slot]), _expectedStates[slot]), "水阶段漏写倒计时或完整状态");
            CheckTotals();
        }

        private void RefreshKeys()
        {
            _count = 0;
            Array.Fill(_keySlots, -1);
            // 完整区块顺序反转与块内顺序反转，覆盖127/128的不同枚举顺序。
            for (int chunk = 0; chunk < 4; chunk++)
            {
                int selected = _reverse ? 3 - chunk : chunk;
                int left = (selected % 2) * 128, bottom = (selected / 2) * 128;
                for (int offset = 0; offset < 128 * 128; offset++)
                {
                    int cell = _reverse ? 128 * 128 - 1 - offset : offset;
                    int x = left + cell % 128, y = bottom + cell / 128;
                    if (State(x, y).MaterialId != 0)
                    {
                        _keySlots[Index(x, y)] = _count;
                        _keys[_count++] = RuleFixture.Grid(x, y);
                    }
                }
            }
        }

        private void CheckTotals()
        {
            int water = 0, concrete = 0;
            foreach (CellKey key in OccupiedCells)
            {
                ushort material = State(key).MaterialId;
                if (material == 101)
                {
                    water++;
                    if (Name == "F4" && !InnerWallRemoved) Require(key.Position.X < 128, "隔墙右侧出现水");
                }
                else if (material == 102)
                {
                    concrete++;
                    Require(_solids.Contains(Index(key.Position.X, key.Position.Y)), "固定墙发生移动");
                }
                else Require(false, "出现配方之外的材料");
            }
            Require(water == WaterCount, $"水量不守恒，实际={water}，预期={WaterCount}");
            Require(concrete == _solids.Count, "混凝土数量或墙占据改变");
        }

        internal int ConcreteCount => _solids.Count;

        internal LiquidGeometry Measure()
        {
            int left = 32 + Scale, right = 32 + 23 * Scale, bottom = 48 + Scale;
            if (Name == "F4" && !InnerWallRemoved) right = 128;
            var depths = new int[right - left];
            int holes = 0, outside = 0;
            foreach (CellKey key in OccupiedCells)
            {
                if (State(key).MaterialId != 101) continue;
                int x = key.Position.X, y = key.Position.Y;
                if (x < left || x >= right || y < bottom) { outside++; continue; }
                depths[x - left]++;
            }
            int minimum = int.MaxValue, maximum = 0;
            for (int x = left; x < right; x++)
            {
                int depth = depths[x - left];
                minimum = Math.Min(minimum, depth);
                maximum = Math.Max(maximum, depth);
                for (int y = bottom; y < bottom + depth; y++) if (State(x, y).MaterialId != 101) holes++;
            }
            bool balanced;
            if (Name == "F3")
            {
                balanced = false;
                foreach (CellKey key in OccupiedCells)
                    if (State(key).MaterialId == 101) balanced = key.Position.Y == 56;
            }
            else if (Name == "F2")
            {
                balanced = outside == 0;
                foreach (CellKey key in OccupiedCells)
                    if (State(key).MaterialId == 101 && key.Position.Y >= 48 + 2 * Scale) balanced = false;
                // 配方水量恰好等于低层非平台空位数，因此守恒+边界给出唯一逐格终态。
                for (int y = bottom; y < 48 + 2 * Scale; y++)
                    for (int x = left; x < right; x++)
                        if (!_solids.Contains(Index(x, y)) && State(x, y).MaterialId != 101) balanced = false;
            }
            else balanced = outside == 0 && holes == 0 && maximum - minimum <= 1;
            return new LiquidGeometry(balanced, maximum - minimum, holes, depths);
        }

        internal bool[] CaptureWaterOccupancy()
        {
            var occupied = new bool[Side * Side];
            foreach (CellKey key in OccupiedCells)
                if (State(key).MaterialId == 101) occupied[Index(key.Position.X, key.Position.Y)] = true;
            return occupied;
        }

        internal bool SameWaterOccupancy(bool[] occupied)
        {
            int count = 0;
            foreach (CellKey key in OccupiedCells)
                if (State(key).MaterialId == 101)
                {
                    if (!occupied[Index(key.Position.X, key.Position.Y)]) return false;
                    count++;
                }
            return count == WaterCount;
        }

        internal ulong OccupancyHash()
        {
            ulong hash = 0;
            foreach (CellKey key in OccupiedCells)
                if (State(key).MaterialId == 101)
                {
                    ulong value = (ulong)Index(key.Position.X, key.Position.Y) + 1;
                    unchecked { value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL; value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL; }
                    hash ^= value ^ (value >> 31);
                }
            return hash;
        }

        internal void AssertEquivalent(LiquidLevelingFixture other)
        {
            Require(WorkingTick == other.WorkingTick && _count == other._count && LastMoves == other.LastMoves && LastBatchHash == other.LastBatchHash, "重放Tick/数量/搬运结果不一致");
            foreach (CellKey key in OccupiedCells)
                Require(SameState(State(key), other.State(key)), $"重排/重放完整状态不同：({key.Position.X},{key.Position.Y})");
        }

        internal void RemoveInnerWall()
        {
            Require(Name == "F4" && !InnerWallRemoved, "内墙只能移除一次");
            for (int y = 56; y < 256; y++)
            {
                Require(State(128, y).MaterialId == 102, "内墙配方不完整");
                _cells[Index(128, y)] = default;
                _solids.Remove(Index(128, y));
            }
            InnerWallRemoved = true;
            FirstGeometryTick = -1;
            RefreshKeys();
            CheckTotals();
        }

        public WorldResult Read(in CellKey key, out CellSnapshot cell)
        {
            cell = default;
            if (key.Generation != Generation) return RuleFixture.Error(WorldErrorCode.StaleGeneration);
            if (key.Position.OwnerKind != OwnerKind.Grid || key.Position.X < 0 || key.Position.X >= Side || key.Position.Y < 0 || key.Position.Y >= Side)
                return RuleFixture.Error(WorldErrorCode.OutOfBounds);
            cell = State(key);
            return WorldResult.Success();
        }
        public bool IsFixed(in CellKey key) => _solids.Contains(Index(key.Position.X, key.Position.Y));
        private ulong FullStateHash()
        {
            ulong hash = 0;
            foreach (CellKey key in OccupiedCells) hash ^= HashState(State(key), Index(key.Position.X, key.Position.Y));
            return hash;
        }
        private static ulong HashState(in CellSnapshot state, int position)
        {
            ulong value = (ulong)position + 1;
            unchecked
            {
                value = Mix(value ^ ((ulong)state.MaterialId << 32) ^ ((ulong)state.Flags << 48));
                value = Mix(value ^ state.FuelTicksRemaining ^ ((ulong)state.SpreadCountdown << 32));
                value = Mix(value ^ state.LifetimeTicksRemaining ^ ((ulong)state.MoveCountdown << 32));
                return Mix(value ^ state.IgnitedTick);
            }
        }
        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
                value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
                return value ^ (value >> 31);
            }
        }
        internal static bool SameState(in CellSnapshot a, in CellSnapshot b) => a.MaterialId == b.MaterialId && a.Flags == b.Flags &&
            a.FuelTicksRemaining == b.FuelTicksRemaining && a.SpreadCountdown == b.SpreadCountdown &&
            a.LifetimeTicksRemaining == b.LifetimeTicksRemaining && a.MoveCountdown == b.MoveCountdown && a.IgnitedTick == b.IgnitedTick;
        private void Require(bool condition, string message)
        {
            if (!condition) Assert.Fail($"{Name} k={Scale} seed={Config.Seed} Tick={WorkingTick}：{message}");
        }
    }

    // 独立验证链的拓扑及提交边界；不复刻搜索半径、代价或候选选择策略。
    internal sealed class LiquidChainValidation
    {
        private readonly Dictionary<CellKey, int> _moves;
        private readonly HashSet<CellKey> _targets;
        private readonly bool[] _visited;
        private readonly int[] _path;
        private readonly int[] _order;
        private int _orderCount;
        internal ReadOnlySpan<int> TailFirstOrder => _order.AsSpan(0, _orderCount);
        internal LiquidChainValidation(int capacity)
        {
            _moves = new Dictionary<CellKey, int>(capacity);
            _targets = new HashSet<CellKey>();
            _visited = new bool[capacity];
            _path = new int[capacity];
            _order = new int[capacity];
        }

        internal void Validate(IWorkingWorldView world, IMaterialRuntimeTable materials, IRuleBatch batch)
        {
            _moves.Clear(); _targets.Clear(); _orderCount = 0;
            ReadOnlySpan<MutationIntent> intents = batch.Intents;
            Check(intents.Length <= _visited.Length, "验收夹具意图容量不足。");
            Array.Clear(_visited, 0, intents.Length);
            for (int index = 0; index < intents.Length; index++)
            {
                MutationIntent intent = intents[index];
                if (intent.Kind != MutationKind.Move) continue;
                Check(_moves.TryAdd(intent.Source, index) && _targets.Add(intent.Target), "原子链包含重复源或目标。");
                Check(!intent.Source.Equals(intent.Target), "原子链包含自环。");
            }
            foreach (var move in _moves)
            {
                if (_targets.Contains(move.Key)) continue;
                int pathCount = 0, current = move.Value;
                CellKey head = move.Key, terminal;
                while (true)
                {
                    Check(!_visited[current], "原子链包含环或交叉重复。");
                    _visited[current] = true;
                    _path[pathCount++] = current;
                    MutationIntent segment = intents[current];
                    Check(world.Read(segment.Target, out CellSnapshot target).IsSuccess, "原子链目标越界或过期。");
                    if (target.MaterialId == 0) { terminal = segment.Target; break; }
                    Check(batch.Stage == TickStage.Water && _moves.TryGetValue(segment.Target, out int next), "占据目标没有在本原子链释放。");
                    Check(world.Read(segment.Source, out CellSnapshot source).IsSuccess && source.MaterialId == target.MaterialId &&
                        materials.TryGet(source.MaterialId, out MaterialRuntimeEntry material) && (material.Rules & RuleMask.LiquidFlow) != 0,
                        "原子链经过异材或非液体。");
                    current = _moves[segment.Target];
                }
                if (pathCount > 1)
                {
                    Check(terminal.Position.Y < head.Position.Y, "原子链无净下降。");
                    var above = new CellKey(head.Generation, new CellPositionKey(OwnerKind.Grid, 0, head.Position.X, head.Position.Y + 1));
                    Check(above.Position.Y == world.Config.Height || (world.Read(above, out CellSnapshot upper).IsSuccess && upper.MaterialId == 0), "原子链首不是自由表面。");
                    for (int index = 0; index < pathCount; index++)
                    {
                        MutationIntent segment = intents[_path[index]];
                        int dx = segment.Target.Position.X - segment.Source.Position.X;
                        int dy = segment.Target.Position.Y - segment.Source.Position.Y;
                        Check((dy == -1 && dx == 0) || (dy == 0 && Math.Abs(dx) == 1), "原子链有上移、斜移或非相邻步。");
                        Check(world.Read(segment.Source, out CellSnapshot source).IsSuccess && source.MaterialId == intents[_path[0]].State.MaterialId &&
                            materials.TryGet(source.MaterialId, out MaterialRuntimeEntry material) && (material.Rules & RuleMask.LiquidFlow) != 0,
                            "原子链源材料不一致。");
                        Check(source.MoveCountdown <= 1, "原子链包含未到移动间隔的材料。");
                        Check(segment.Source.Position.OwnerKind == OwnerKind.Grid && segment.Target.Position.OwnerKind == OwnerKind.Grid,
                            "原子液体链只能位于主网格。");
                    }
                }
                for (int index = pathCount - 1; index >= 0; index--) _order[_orderCount++] = _path[index];
            }
            Check(_orderCount == _moves.Count, "原子链成环，没有可追踪的链首和原空尾。");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) Assert.Fail(message);
        }
    }

    internal readonly struct LiquidGeometry
    {
        internal readonly bool Balanced;
        internal readonly int DepthDifference;
        internal readonly int Holes;
        internal readonly int[] Depths;
        internal LiquidGeometry(bool balanced, int difference, int holes, int[] depths)
        { Balanced = balanced; DepthDifference = difference; Holes = holes; Depths = depths; }
    }

    internal readonly struct LiquidTickRecord
    {
        internal readonly int Tick;
        internal readonly int WaterCount;
        internal readonly int Moves;
        internal readonly LiquidGeometry Geometry;
        internal readonly ulong OccupancyHash;
        internal readonly ulong FullStateHash;
        internal readonly ulong BatchHash;
        internal LiquidTickRecord(int tick, int waterCount, int moves, LiquidGeometry geometry, ulong hash, ulong stateHash, ulong batchHash)
        { Tick = tick; WaterCount = waterCount; Moves = moves; Geometry = geometry; OccupancyHash = hash; FullStateHash = stateHash; BatchHash = batchHash; }
        public override string ToString() => $"Tick={Tick},水量={WaterCount},搬运={Moves},列深差={Geometry.DepthDifference},孔洞={Geometry.Holes},占据哈希={OccupancyHash:x16},全状态哈希={FullStateHash:x16},意图哈希={BatchHash:x16}";
    }
}
