using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Rules;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Rules
{
    public sealed class MovementRuleTests
    {
        [Test]
        public void M03_T6_01_WaterMovesOnTicks1And3WithCompleteState()
        {
            var f = new RuleFixture();
            f.Put(5, 5, 101);
            for (int tick = 1; tick <= 3; tick++)
            {
                f.Begin(); f.Water();
                int y = tick == 3 ? 3 : 4;
                Assert.That(f.State(5, y).MaterialId, Is.EqualTo(101));
                Assert.That(f.State(5, y).MoveCountdown, Is.EqualTo(tick == 2 ? 1 : 2));
                Assert.That(f.Cells.Count, Is.EqualTo(1));
                TestContext.WriteLine($"Tick{tick}: 水=(5,{y}), MoveCountdown={f.State(5, y).MoveCountdown}");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void M03_T6_02_SteamExpiresOnTick200WhenOpenOrEnclosed(bool enclosed)
        {
            var f = new RuleFixture(height: 512);
            f.Put(5, 5, 103);
            if (enclosed)
                for (int y = 4; y <= 6; y++)
                    for (int x = 4; x <= 6; x++) if (x != 5 || y != 5) f.Put(x, y, 102);
            for (int tick = 1; tick <= 200; tick++)
            {
                f.Begin(); f.Steam();
                CellSnapshot[] steam = f.Cells.Values.Where(c => c.MaterialId == 103).ToArray();
                Assert.That(steam.Length, Is.EqualTo(tick == 200 ? 0 : 1), $"Tick{tick}");
                if (tick == 200) continue;
                Assert.That(steam[0].LifetimeTicksRemaining, Is.EqualTo(200 - tick));
                int expectedY = enclosed ? 5 : 6 + (tick - 1) / 3;
                Assert.That(f.State(5, expectedY).MaterialId, Is.EqualTo(103));
                if (tick <= 4 || tick == 199) TestContext.WriteLine($"Tick{tick}: 蒸汽=(5,{expectedY}), Lifetime={200 - tick}");
            }
            Assert.That(f.Expired, Is.EqualTo(1));
            Assert.That(f.BurnedOut, Is.Zero);
            TestContext.WriteLine("Tick200: 自然消散=1，燃尽=0");
        }

        [TestCase(false, 4)]
        [TestCase(true, 4)]
        [TestCase(false, 126)]
        [TestCase(true, 126)]
        public void M03_T6_03_T6_05_CompetitionSortsByTargetThenSourceAndLoserUsesFallback(bool reverse, int x)
        {
            CellKey a = RuleFixture.Grid(x, 5), b = RuleFixture.Grid(x + 2, 5), target = RuleFixture.Grid(x + 1, 4);
            var f = new RuleFixture(); f.Put(x, 5, 101); f.Put(x + 2, 5, 101); f.Begin();
            f.Instances.TryGetInstance(a, out CellInstanceHandle ia); f.Instances.TryGetInstance(b, out CellInstanceHandle ib);
            var items = new[]
            {
                new MutationIntent(MutationKind.Move, ib, b, target, f.State(b), MoveCandidateTier.PreferredDiagonal),
                new MutationIntent(MutationKind.Move, ia, a, target, f.State(a), MoveCandidateTier.PreferredDiagonal),
                new MutationIntent(MutationKind.Move, ib, b, RuleFixture.Grid(x + 3, 4), f.State(b), MoveCandidateTier.OtherDiagonal)
            };
            if (reverse) Array.Reverse(items);
            IRuleBatch batch = new MovementCandidateResolver(2).Resolve(new CandidateBatch(items));
            Assert.That(batch.Result.IsSuccess, Is.True);
            Assert.That(batch.Intents.Length, Is.EqualTo(2));
            Assert.That(batch.Intents[0].Source, Is.EqualTo(a));
            Assert.That(batch.Intents[0].Target, Is.EqualTo(target));
            Assert.That(batch.Intents[1].Source, Is.EqualTo(b));
            Assert.That(batch.Intents[1].Target, Is.EqualTo(RuleFixture.Grid(x + 3, 4)));
            TestContext.WriteLine($"首选斜向: ({x},5)胜出→({x + 1},4)；败者({x + 2},5)次选斜向→({x + 3},4)");
        }

        [Test]
        public void M03_T6_03_InputAndTagOrderDoNotChangeGeneratedPlan()
        {
            var a = new RuleFixture();
            var b = new RuleFixture(editMaterials: root =>
            {
                foreach (JObject entry in (JArray)root["materials"]) entry["tags"] = new JArray(((JArray)entry["tags"]).Reverse().ToArray());
            });
            foreach (RuleFixture f in new[] { a, b })
            {
                f.Put(4, 5, 101); f.Put(6, 5, 101); f.Put(4, 4, 102); f.Put(6, 4, 102);
            }
            a.Begin(); b.Begin(reverse: true);
            a.Water(); b.Water();
            CollectionAssert.AreEquivalent(a.Cells, b.Cells);
        }

        [Test]
        public void M03_T6_04_DiagonalCannotCrossBlockedSide()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101);
            f.Put(5, 4, 102); f.Put(4, 5, 102); f.Put(6, 5, 102);
            f.Begin(); f.Water();
            Assert.That(f.State(5, 5).MaterialId, Is.EqualTo(101));
            Assert.That(f.State(4, 4).MaterialId, Is.Zero);
            Assert.That(f.State(6, 4).MaterialId, Is.Zero);
            Assert.That(f.State(5, 5).MoveCountdown, Is.EqualTo(2));
        }

        [Test]
        public void M03_T6_04_IndependentMovesCannotUseSamePhaseVacancyOrSwapSteam()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(5, 4, 101);
            // 上方受混凝土封闭，不是自由表面，不能组成合法原子链。
            f.Put(5, 6, 102);
            for (int y = 4; y <= 5; y++) { f.Put(4, y, 102); f.Put(6, y, 102); }
            f.Begin(); f.Water();
            Assert.That(f.State(5, 3).MaterialId, Is.EqualTo(101));
            Assert.That(f.State(5, 5).MaterialId, Is.EqualTo(101));
            Assert.That(f.State(5, 4).MaterialId, Is.Zero);
            var other = new RuleFixture(); other.Put(5, 5, 101); other.Put(5, 4, 103);
            for (int y = 4; y <= 5; y++) { other.Put(4, y, 102); other.Put(6, y, 102); }
            other.Begin(); other.Water(); other.Steam();
            Assert.That(other.State(5, 5).MaterialId, Is.EqualTo(101));
            Assert.That(other.State(5, 4).MaterialId, Is.EqualTo(103));
        }

        [Test]
        public void M03_T6_04_ExplicitAtomicWaterChainMovesEachInstanceOneCellWithoutStateLoss()
        {
            var f = new RuleFixture();
            f.Put(5, 5, 101); f.Put(5, 4, 101);
            for (int y = 4; y <= 5; y++) { f.Put(4, y, 102); f.Put(6, y, 102); }
            f.Cells[RuleFixture.Grid(5, 5)] = new CellSnapshot(101, 8, 17, 16, 15, 0, 14);
            f.Cells[RuleFixture.Grid(5, 4)] = new CellSnapshot(101, 16, 27, 26, 25, 0, 24);
            f.Begin();
            f.Instances.TryGetInstance(RuleFixture.Grid(5, 5), out CellInstanceHandle upper);
            f.Instances.TryGetInstance(RuleFixture.Grid(5, 4), out CellInstanceHandle lower);
            f.Instances.MarkWet(upper);
            f.Water();
            Assert.That(f.State(5, 5).MaterialId, Is.Zero);
            Assert.That(f.State(5, 4), Is.EqualTo(new CellSnapshot(101, 8, 17, 16, 15, 2, 14)));
            Assert.That(f.State(5, 3), Is.EqualTo(new CellSnapshot(101, 16, 27, 26, 25, 2, 24)));
            Assert.That(f.Instances.TryResolve(upper, out CellKey upperTarget), Is.True);
            Assert.That(upperTarget, Is.EqualTo(RuleFixture.Grid(5, 4)));
            Assert.That(f.Instances.TryResolve(lower, out CellKey lowerTarget), Is.True);
            Assert.That(lowerTarget, Is.EqualTo(RuleFixture.Grid(5, 3)));
            Assert.That(f.Instances.IsWet(upper), Is.True);
            Assert.That(f.Instances.IsWet(lower), Is.False);
            Assert.That(f.Cells.Values.Count(c => c.MaterialId == 101), Is.EqualTo(2));
        }

        [Test]
        public void M03_T6_04_SteamReadsAlreadyAppliedWaterPhase()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(5, 4, 103);
            f.Put(4, 4, 102); f.Put(6, 4, 102);
            f.Begin(); f.Water();
            Assert.That(f.State(5, 5).MaterialId, Is.Zero);
            f.Steam();
            Assert.That(f.State(5, 5).MaterialId, Is.EqualTo(103));
        }

        [TestCase(5)]
        [TestCase(127)]
        public void M03_T6_05_FullStateMovesAcrossChunkBoundary(int x)
        {
            var f = new RuleFixture(seed: 2); f.Put(x, 5, 101); f.Put(x, 4, 102);
            var sentinel = new CellSnapshot(101, 1, 7, 6, 5, 0, 4);
            f.Cells[RuleFixture.Grid(x, 5)] = sentinel;
            f.Begin();
            IRuleBatch resolved = f.Rules.Movement.Resolve(f.Run(f.Rules.Water, TickStage.Water));
            MutationIntent move = resolved.Intents.ToArray().Single(i => i.Kind == MutationKind.Move);
            Assert.That(move.Target, Is.EqualTo(RuleFixture.Grid(x + 1, 4)));
            Assert.That(move.State, Is.EqualTo(new CellSnapshot(101, 1, 7, 6, 5, 2, 4)));
            f.Apply(resolved);
            Assert.That(f.State(x, 5).MaterialId, Is.Zero);
            Assert.That(f.State(x + 1, 4), Is.EqualTo(move.State));
            // 搬运字节探针故意使用非正式材料状态，单独验证不丢任一字段。
        }

        [TestCase(1U, -1)]
        [TestCase(2U, 1)]
        public void M03_T6_06_ParityAppliesToWaterDiagonalsAndSteamFallbacks(uint seed, int dx)
        {
            foreach (ushort id in new ushort[] { 101, 103 })
                foreach (bool horizontal in new[] { false, true })
                {
                    // L01水横移须有可达低处；原水平奇偶后备仅保留蒸汽。
                    if (id == 101 && horizontal) continue;
                    var f = new RuleFixture(seed); f.Put(5, 5, id);
                    int dy = id == 101 ? -1 : 1;
                    f.Put(5, 5 + dy, 102);
                    if (horizontal) { f.Put(4, 5 + dy, 102); f.Put(6, 5 + dy, 102); }
                    f.Begin(); if (id == 101) f.Water(); else f.Steam();
                    Assert.That(f.State(5 + dx, horizontal ? 5 : 5 + dy).MaterialId, Is.EqualTo(id));
                }
        }

        [Test]
        public void M03_T6_06_OverflowSafeParityAtMaximumTickAndSeed()
        {
            Assert.That(ContractDefaults.PreferLeft(ulong.MaxValue, int.MaxValue, int.MaxValue, uint.MaxValue), Is.True);
            Assert.That(ContractDefaults.PreferLeft(ulong.MaxValue, int.MaxValue, int.MaxValue, uint.MaxValue - 1), Is.False);
        }

        [Test]
        public void M03_T6_04_BoundaryAndDynamicSolidOccupancyBlockCandidates()
        {
            var occupancy = new OccupancyFixture();
            var f = new RuleFixture(occupancy: occupancy); f.Put(0, 0, 101);
            occupancy.Blocked.Add(RuleFixture.Grid(1, 0));
            f.Begin(); f.Water();
            Assert.That(f.State(0, 0).MaterialId, Is.EqualTo(101));
            var missing = new RuleFixture(); missing.Put(5, 5, 101);
            missing.BodyItems = new[] { new BodySnapshot(1, default, default, default, 1) };
            missing.Begin(); IRuleBatch batch = missing.Run(missing.Rules.Water, TickStage.Water);
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(batch.Intents.Length, Is.Zero);
        }

        [Test]
        public void M03_T6_02_NewSteamExpiresWithoutExtraTickAndNoUnsignedUnderflow()
        {
            var f = new RuleFixture(); f.Begin(); f.Put(5, 5, 103);
            CellKey key = RuleFixture.Grid(5, 5);
            f.Cells[key] = new CellSnapshot(103, lifetimeTicksRemaining: 1);
            f.Instances.Create(key); f.Refresh(); f.Steam();
            Assert.That(f.State(key).MaterialId, Is.Zero);
            Assert.That(f.Expired, Is.EqualTo(1));
        }

        private sealed class CandidateBatch : IRuleBatch
        {
            private readonly MutationIntent[] _items;
            public WorldResult Result => WorldResult.Success();
            public TickStage Stage => TickStage.Water;
            public ReadOnlySpan<MutationIntent> Intents => _items;
            internal CandidateBatch(MutationIntent[] items) { _items = items; }
        }
    }
}
