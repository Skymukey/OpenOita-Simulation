using System;
using System.Diagnostics;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Rules;

namespace OpenOita.Tests.EditMode.Rules
{
    [Category("LiquidLeveling")]
    public sealed class LiquidLevelingTests
    {
        [TestCase(1, 1U)]
        [TestCase(1, 2U)]
        [TestCase(8, 1U)]
        [TestCase(8, 2U)]
        public void M03_L01_01_F1_AllColumnsLevelWithinDeadlineAndStopFor128Ticks(int scale, uint seed)
        {
            var fixture = new LiquidLevelingFixture("F1", scale, seed);
            Assert.That(fixture.ConcreteCount, Is.EqualTo(46 * scale * scale));
            RunUntilStable(fixture, scale == 1 ? 256 : 1024);
            AssertDepthHistogram(fixture, scale == 1 ? 0 : 5, scale == 1 ? 6 : 32,
                scale == 1 ? 1 : 6, scale == 1 ? 16 : 144);
        }

        [TestCase(1, 1U)]
        [TestCase(1, 2U)]
        [TestCase(8, 1U)]
        [TestCase(8, 2U)]
        public void M03_L01_02_F2_PlatformDrainsToExactLowerCellsAndStopsFor128Ticks(int scale, uint seed)
        {
            var fixture = new LiquidLevelingFixture("F2", scale, seed);
            Assert.That(fixture.ConcreteCount, Is.EqualTo(52 * scale * scale));
            RunUntilStable(fixture, scale == 1 ? 256 : 1024);
            int left = 32 + scale, right = 32 + 23 * scale, bottom = 48 + scale;
            for (int y = bottom; y < 48 + 2 * scale; y++)
                for (int x = left; x < right; x++)
                    Assert.That(fixture.State(x, y).MaterialId, Is.EqualTo(x >= 32 + 3 * scale && x < 32 + 9 * scale ? 102 : 101), $"({x},{y})");
            foreach (CellKey key in fixture.OccupiedCells)
                if (fixture.State(key.Position.X, key.Position.Y).MaterialId == 101)
                    Assert.That(key.Position.Y, Is.LessThan(48 + 2 * scale), "平台上方不得滞留水堆。");
        }

        [TestCase(1U)]
        [TestCase(2U)]
        public void M03_L01_03_F3_120CellChannelCrosses128AndFallsWithin512Ticks(uint seed)
        {
            var fixture = new LiquidLevelingFixture("F3", 8, seed);
            Assert.That(fixture.ConcreteCount, Is.EqualTo(3119));
            RunUntilStable(fixture, 512);
            Assert.That(fixture.BoundaryCrossings, Is.GreaterThanOrEqualTo(1));
            Assert.That(fixture.FirstGeometryTick, Is.GreaterThanOrEqualTo(120), "逐格路径不能瞬移。");
        }

        [TestCase(1U)]
        [TestCase(2U)]
        public void M03_L01_04_05_F4_WallIsImpermeableThenGeometryRemovalRestartsLeveling(uint seed)
        {
            var fixture = new LiquidLevelingFixture("F4", 8, seed);
            Assert.That(fixture.ConcreteCount, Is.EqualTo(3144));
            RunUntilStable(fixture, 1024);
            AssertDepthHistogram(fixture, 11, 32, 12, 56);
            int changeTick = (int)fixture.WorkingTick;
            fixture.RemoveInnerWall();
            Assert.That(fixture.ConcreteCount, Is.EqualTo(2944));
            RunUntilStable(fixture, 1024);
            Assert.That(fixture.FirstGeometryTick - changeTick, Is.LessThanOrEqualTo(1024));
            AssertDepthHistogram(fixture, 5, 32, 6, 144);
            TestContext.WriteLine("本用例只移除独立夹具的完整内墙；正式一条区域Remove命令及事务提交仍待命令系统集成后联验。");
        }

        [TestCase("F1", 1U)]
        [TestCase("F1", 2U)]
        [TestCase("F2", 1U)]
        [TestCase("F2", 2U)]
        [TestCase("F3", 1U)]
        [TestCase("F3", 2U)]
        public void M03_L01_06_ReversedInsertionChunksAndReconstructedReplayMatchEveryTick(string name, uint seed)
        {
            var normal = new LiquidLevelingFixture(name, 8, seed);
            var reversed = new LiquidLevelingFixture(name, 8, seed, reverse: true);
            int deadline = name == "F3" ? 512 : 1024;
            RunUntilStable(normal, deadline, reversed);
            Assert.That(normal.BoundaryCrossings, Is.GreaterThan(0));
            // 同步重排用例逐格精确比对；重建重放逐Tick核对包含全部字段的状态/意图摘要。
            var replay = new LiquidLevelingFixture(name, 8, seed);
            int totalTicks = (int)normal.WorkingTick;
            for (int tick = 0; tick < totalTicks; tick++)
            {
                replay.Step();
                LiquidTickRecord recorded = normal.Records[tick];
                Assert.That(replay.Records[tick].OccupancyHash, Is.EqualTo(recorded.OccupancyHash), $"重放Tick={tick + 1}");
                Assert.That(replay.Records[tick].FullStateHash, Is.EqualTo(recorded.FullStateHash), $"重放完整状态Tick={tick + 1}");
                Assert.That(replay.Records[tick].BatchHash, Is.EqualTo(recorded.BatchHash), $"重放完整意图Tick={tick + 1}");
                Assert.That(replay.LastMoves, Is.EqualTo(recorded.Moves));
            }
            normal.AssertEquivalent(replay);
            TestContext.WriteLine("本用例验证纯规则重建重放；正式世界Reset代次/辅助状态生命周期另由M02联验。");
        }

        [TestCase(5)]
        [TestCase(127)]
        public void M03_L01_07_HorizontalMovementPreservesAllStateInstanceAndWetAssociation(int x)
        {
            var fixture = new RuleFixture(seed: 2);
            fixture.Put(x, 5, 101);
            for (int column = x - 1; column <= x + 1; column++) fixture.Put(column, 4, 102);
            fixture.Put(x - 1, 5, 102);
            var before = new CellSnapshot(101, 8, 7, 6, 5, 0, 4);
            fixture.Cells[RuleFixture.Grid(x, 5)] = before;
            fixture.Begin();
            fixture.Instances.TryGetInstance(RuleFixture.Grid(x, 5), out CellInstanceHandle instance);
            fixture.Instances.MarkWet(instance);
            IRuleBatch batch = fixture.Rules.Movement.Resolve(fixture.Run(fixture.Rules.Water, TickStage.Water));
            MutationIntent move = batch.Intents.ToArray().Single(i => i.Kind == MutationKind.Move);
            Assert.That(move.Target, Is.EqualTo(RuleFixture.Grid(x + 1, 5)), "通道右端x+2下方开放；本步只能水平推进一格。");
            Assert.That(move.State, Is.EqualTo(new CellSnapshot(101, 8, 7, 6, 5, 2, 4)));
            fixture.Apply(batch);
            Assert.That(fixture.Instances.TryResolve(instance, out CellKey target), Is.True);
            Assert.That(target, Is.EqualTo(move.Target));
            Assert.That(fixture.Instances.IsWet(instance), Is.True);
            Assert.That(fixture.Instances.TryGetInstance(target, out CellInstanceHandle after), Is.True);
            Assert.That(after, Is.EqualTo(instance));
        }

        [Test]
        public void M03_L01_07_HorizontalCompetitionIsUniqueAndWetStateDoesNotLeakToLoser()
        {
            var fixture = new RuleFixture();
            CellKey a = RuleFixture.Grid(126, 5), b = RuleFixture.Grid(128, 5), target = RuleFixture.Grid(127, 5);
            fixture.Put(126, 5, 101); fixture.Put(128, 5, 101);
            fixture.Cells[a] = new CellSnapshot(101, 8, 17, 16, 15, 2, 14);
            fixture.Cells[b] = new CellSnapshot(101, 16, 27, 26, 25, 2, 24);
            fixture.Begin();
            fixture.Instances.TryGetInstance(a, out CellInstanceHandle ia);
            fixture.Instances.TryGetInstance(b, out CellInstanceHandle ib);
            fixture.Instances.MarkWet(ib);
            var input = new FixedBatch(new[]
            {
                new MutationIntent(MutationKind.Move, ib, b, target, fixture.State(b), MoveCandidateTier.PreferredHorizontal),
                new MutationIntent(MutationKind.Move, ia, a, target, fixture.State(a), MoveCandidateTier.PreferredHorizontal),
                new MutationIntent(MutationKind.WriteState, ib, b, b, fixture.State(b))
            });
            IRuleBatch resolved = fixture.Rules.Movement.Resolve(input);
            Assert.That(resolved.Intents.ToArray().Count(i => i.Kind == MutationKind.Move), Is.EqualTo(1));
            fixture.Apply(resolved);
            Assert.That(fixture.State(target).FuelTicksRemaining, Is.EqualTo(17));
            Assert.That(fixture.State(b).FuelTicksRemaining, Is.EqualTo(27));
            Assert.That(fixture.Instances.IsWet(ia), Is.False);
            Assert.That(fixture.Instances.IsWet(ib), Is.True);
            Assert.That(fixture.Instances.TryResolve(ib, out CellKey loser), Is.True);
            Assert.That(loser, Is.EqualTo(b));
        }

        [Test]
        public void M03_L01_07_InsufficientHorizontalIntentCapacityReturnsEmptyAndLeavesStateUntouched()
        {
            var fixture = new RuleFixture();
            fixture.Put(127, 5, 101);
            for (int x = 126; x <= 128; x++) fixture.Put(x, 4, 102);
            fixture.Put(126, 5, 102);
            fixture.Begin();
            var before = fixture.Cells.ToArray();
            fixture.Instances.TryGetInstance(RuleFixture.Grid(127, 5), out CellInstanceHandle instance);
            fixture.Instances.MarkWet(instance);
            IRuleBatch batch = fixture.Run(new LiquidFlowRule(32, intentCapacity: 0), TickStage.Water);
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(batch.Intents.Length, Is.Zero);
            CollectionAssert.AreEquivalent(before, fixture.Cells);
            Assert.That(fixture.Instances.TryResolve(instance, out CellKey source), Is.True);
            Assert.That(source, Is.EqualTo(RuleFixture.Grid(127, 5)));
            Assert.That(fixture.Instances.IsWet(instance), Is.True);
        }

        [TestCase("Cycle")]
        [TestCase("NoNetDescent")]
        [TestCase("BlockedHead")]
        [TestCase("OccupiedTerminal")]
        [TestCase("DifferentMaterial")]
        [TestCase("UnreadySource")]
        [TestCase("LongStep")]
        [TestCase("DiagonalSegment")]
        public void M03_L01_07_InvalidAtomicChainsCannotPassIndependentPreflight(string defect)
        {
            var fixture = new RuleFixture();
            CellKey head = RuleFixture.Grid(5, 5);
            CellKey next = defect == "Cycle" || defect == "NoNetDescent" ? RuleFixture.Grid(6, 5) :
                defect == "DiagonalSegment" ? RuleFixture.Grid(6, 4) : RuleFixture.Grid(5, 4);
            CellKey terminal = defect == "Cycle" ? head : defect == "NoNetDescent" ? RuleFixture.Grid(7, 5) :
                defect == "LongStep" ? RuleFixture.Grid(5, 2) : defect == "DiagonalSegment" ? RuleFixture.Grid(6, 3) : RuleFixture.Grid(5, 3);
            fixture.Put(head, 101);
            fixture.Put(next, defect == "DifferentMaterial" ? (ushort)103 : (ushort)101);
            if (defect == "BlockedHead") fixture.Put(5, 6, 102);
            if (defect == "UnreadySource") fixture.Cells[head] = new CellSnapshot(101, moveCountdown: 2);
            fixture.Begin();
            fixture.Instances.TryGetInstance(head, out CellInstanceHandle first);
            fixture.Instances.TryGetInstance(next, out CellInstanceHandle second);
            fixture.Instances.MarkWet(first);
            var intents = new[]
            {
                new MutationIntent(MutationKind.Move, first, head, next, new CellSnapshot(101, moveCountdown: 2)),
                new MutationIntent(MutationKind.Move, second, next, terminal, new CellSnapshot(fixture.State(next).MaterialId, moveCountdown: 2))
            };
            var before = fixture.Cells.ToArray();
            var batch = new FixedBatch(defect == "OccupiedTerminal" ? new[] { intents[0] } : intents);
            Assert.Throws<AssertionException>(() => fixture.Apply(batch));
            CollectionAssert.AreEquivalent(before, fixture.Cells, "链未通过预检时不得先写回部分状态。");
            Assert.That(fixture.Instances.TryResolve(first, out CellKey firstSource), Is.True);
            Assert.That(firstSource, Is.EqualTo(head));
            Assert.That(fixture.Instances.TryResolve(second, out CellKey secondSource), Is.True);
            Assert.That(secondSource, Is.EqualTo(next));
            Assert.That(fixture.Instances.IsWet(first), Is.True);
            TestContext.WriteLine("本用例只证明独立验收夹具拒绝非法链；正式事务预检由M02用例验证。");
        }

        [TestCase(5, 6, 3)]
        [TestCase(5, 4, 3)]
        [TestCase(5, 3, 4)]
        public void M03_L01_07_AtomicChainRespectsSolidOccupancyAtHeadInteriorAndTail(int blockedX, int blockedY, int expectedLowerY)
        {
            var occupancy = new OccupancyFixture();
            var fixture = new RuleFixture(occupancy: occupancy);
            fixture.Put(5, 5, 101); fixture.Put(5, 4, 101);
            for (int y = 4; y <= 5; y++) { fixture.Put(4, y, 102); fixture.Put(6, y, 102); }
            occupancy.Blocked.Add(RuleFixture.Grid(blockedX, blockedY));
            fixture.Begin(); fixture.Water();
            Assert.That(fixture.State(5, 5).MaterialId, Is.EqualTo(101), "真实固体遮挡使原子链失效，上水不得借下水移出的坐标。");
            Assert.That(fixture.State(5, expectedLowerY).MaterialId, Is.EqualTo(101));
            Assert.That(fixture.Cells.Values.Count(c => c.MaterialId == 101), Is.EqualTo(2));
            TestContext.WriteLine("本用例注入占据适配夹具；旋转体的真实格几何仍由M06集成验证。");
        }

        [TestCase(1U)]
        [TestCase(2U)]
        public void M03_L01_StableSingleWaterOnClosedFlatFloorNeverOscillates(uint seed)
        {
            var fixture = new RuleFixture(seed);
            for (int x = 0; x < 256; x++) fixture.Put(x, 4, 102);
            fixture.Put(127, 5, 101);
            for (int tick = 1; tick <= 128; tick++)
            {
                fixture.Begin();
                IRuleBatch batch = fixture.Rules.Movement.Resolve(fixture.Run(fixture.Rules.Water, TickStage.Water));
                Assert.That(batch.Intents.ToArray().Count(i => i.Kind == MutationKind.Move), Is.Zero, $"Tick={tick}，水平空格不能作为降低水位依据。");
                fixture.Apply(batch);
                Assert.That(fixture.State(127, 5).MaterialId, Is.EqualTo(101));
            }
        }

        [TestCase(1U, -1)]
        [TestCase(2U, 1)]
        public void M03_L01_EquivalentHorizontalPlansUseSeedThenKeepAdvancing(uint seed, int direction)
        {
            var fixture = new RuleFixture(seed);
            for (int x = 2; x <= 8; x++) fixture.Put(x, 4, 102);
            fixture.Put(5, 5, 101);
            // 两端落口等距；Tick3的坐标/Tick奇偶会反转，仍应向已选低处推进。
            for (int tick = 1; tick <= 3; tick++)
            {
                fixture.Begin(); fixture.Water();
                int distance = tick < 3 ? 1 : 2;
                Assert.That(fixture.State(5 + direction * distance, 5).MaterialId, Is.EqualTo(101), $"Tick={tick}");
            }
        }

        private static void RunUntilStable(LiquidLevelingFixture fixture, int deadline, LiquidLevelingFixture equivalent = null)
        {
            var watch = Stopwatch.StartNew();
            int startTick = (int)fixture.WorkingTick;
            bool[] stableOccupancy = null;
            int observed = 0;
            try
            {
                for (int elapsed = 1; elapsed <= deadline + 128; elapsed++)
                {
                    LiquidTickRecord record = fixture.Step();
                    if (equivalent != null) { equivalent.Step(); fixture.AssertEquivalent(equivalent); }
                    if (elapsed % 128 == 0) TestContext.WriteLine(record);
                    if (stableOccupancy != null)
                    {
                        Assert.That(record.Moves, Is.Zero, $"首次几何达标后的128Tick必须零搬运：{record}");
                        Assert.That(fixture.SameWaterOccupancy(stableOccupancy), Is.True, $"稳定观察占据发生变化：{record}");
                        Assert.That(record.Geometry.Balanced, Is.True, $"稳定观察几何失衡：{record}");
                        if (++observed == 128) return;
                    }
                    else if (record.Geometry.Balanced)
                    {
                        Assert.That(elapsed, Is.LessThanOrEqualTo(deadline), $"首次满足几何条件超过{deadline}Tick：{record}");
                        fixture.FirstGeometryTick = record.Tick;
                        stableOccupancy = fixture.CaptureWaterOccupancy();
                        TestContext.WriteLine($"首次达标：{record}；自地形/初态变化起{elapsed}Tick。");
                    }
                    else if (elapsed == deadline)
                        Assert.Fail($"{fixture.Name} k={fixture.Scale} seed={fixture.Config.Seed} 未在{deadline}Tick达标：{record}");
                }
                Assert.Fail("未完成128Tick稳定观察。");
            }
            finally
            {
                watch.Stop();
                TestContext.WriteLine($"{fixture.Name} k={fixture.Scale} seed={fixture.Config.Seed}，初始Tick={startTick}，已执行={fixture.WorkingTick - (ulong)startTick}，首次几何Tick={fixture.FirstGeometryTick}，夹具墙钟={watch.Elapsed.TotalSeconds:F3}s；纯规则结果不覆盖正式Step性能。");
                if (fixture.Records.Count > 0) TestContext.WriteLine($"末步：{fixture.Records[fixture.Records.Count - 1]}");
            }
        }

        private static void AssertDepthHistogram(LiquidLevelingFixture fixture, int low, int lowCount, int high, int highCount)
        {
            int[] depths = fixture.Measure().Depths;
            Assert.That(depths.Length, Is.EqualTo(lowCount + highCount));
            Assert.That(depths.Count(d => d == low), Is.EqualTo(lowCount));
            Assert.That(depths.Count(d => d == high), Is.EqualTo(highCount));
        }

        private sealed class FixedBatch : IRuleBatch
        {
            private readonly MutationIntent[] _items;
            internal FixedBatch(MutationIntent[] items) { _items = items; }
            public WorldResult Result => WorldResult.Success();
            public TickStage Stage => TickStage.Water;
            public ReadOnlySpan<MutationIntent> Intents => _items;
        }
    }
}
