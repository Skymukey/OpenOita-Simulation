using System;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Bodies
{
    public sealed class BodyCandidateStateCoreTests
    {
        private static WorldLoadResult Load(InitialCell[] cells, Vector2Int[] fixedCells, int maxBodies = 64)
        {
            var baseline = new OpenOita.Data.WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(baseline.Result.IsSuccess, Is.True);
            var limits = new WorldLimits(65536, maxBodies, 256, 4096, 65536, 5, 180, 8, 16);
            var config = new WorldConfig(1, 256, 256, 128, 0.1f, 0.02f, -9.81f, 1, limits);
            return new WorldLoadResult(WorldResult.Success(), config, FixtureCatalog.Scene(cells, fixedCells), baseline.Materials, baseline.Rules);
        }

        [Test]
        public void M05_T3_01_RealM02CandidateFeedsM04AndM05BeforeAnyGridApply()
        {
            WorldLoadResult loaded = Load(new[] { new InitialCell(126, 10, 102), new InitialCell(127, 10, 104), new InitialCell(128, 10, 104) },
                new[] { new Vector2Int(126, 10) });
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                Assert.That(world.BeginTick().IsSuccess, Is.True);
                var context = new TransactionContext(world.Published.Version, world.WorkingTick, TickStage.Commands);
                CellKey bridge = BodyFixture.Grid(127, 10);
                var prepared = world.Prepare(new[] { new CellWrite(bridge, default) }, context);
                Assert.That(prepared.Result.IsSuccess, Is.True);
                using (prepared.Prepared)
                {
                    StructurePlanResult structure = world.PlanStructure(prepared.Prepared, new ConnectivityAnalyzer(128, 64), context);
                    Assert.That(structure.Result.IsSuccess, Is.True);
                    // 使用原准备对象的只读候选实例及写入集；本用例只检查计算，不Apply。
                    var counterProbe = new TickChangeCounter(65536);
                    var ids = new ulong[1];
                    Assert.That(world.ReserveBodyIds(ids.Length, ids).IsSuccess, Is.True);
                    Assert.That(new BodyExtractionPlanner().Build(prepared.Prepared.CandidateWorld, structure.Plan, loaded.Materials,
                        ids, prepared.Prepared.CandidateInstances, counterProbe, prepared.Prepared.CandidateWrites, context, out BodyExtractionPlan plan).IsSuccess, Is.True);
                    using (plan)
                    {
                        Assert.That(plan.Bodies.Count, Is.EqualTo(1)); Assert.That(plan.Bodies[0].CellCount, Is.EqualTo(1));
                        Assert.That(plan.Writes.Length, Is.EqualTo(3));
                        Assert.That(world.MaterialCells, Is.EqualTo(3)); Assert.That(world.ChangedPositions, Is.Zero);
                        Assert.That(world.IsFixed(BodyFixture.Grid(126, 10)), Is.True);
                        world.Read(bridge, out CellSnapshot old); Assert.That(old.MaterialId, Is.EqualTo(104));
                        Assert.That(world.Published.Version.CommittedTick, Is.Zero);
                    }
                }
                Assert.That(world.MaterialCells, Is.EqualTo(3));
                TestContext.WriteLine("真实M02候选→M04→M05计算通过，未Apply；正式提取提交、体目录、物理未测。计数探针未代替M02整Tick服务。");
            }
        }

        [Test]
        public void M05_T4_02_RealRemoveCandidateCapacityRejectLeavesOriginalNineCellsAndConstraint()
        {
            var arms = new[] { new InitialCell(4, 4, 104), new InitialCell(2, 4, 104), new InitialCell(3, 4, 104),
                new InitialCell(5, 4, 104), new InitialCell(6, 4, 104), new InitialCell(4, 2, 104), new InitialCell(4, 3, 104),
                new InitialCell(4, 5, 104), new InitialCell(4, 6, 104) };
            WorldLoadResult loaded = Load(arms, new[] { new Vector2Int(4, 4) }, 3);
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                Assert.That(world.BeginTick().IsSuccess, Is.True);
                var context = new TransactionContext(world.Published.Version, 1, TickStage.Commands);
                var result = world.Prepare(new[] { new CellWrite(BodyFixture.Grid(4, 4), default) }, context);
                Assert.That(result.Result.IsSuccess, Is.True);
                using (result.Prepared)
                {
                    var structure = world.PlanStructure(result.Prepared, new ConnectivityAnalyzer(128, 64), context);
                    Assert.That(structure.Result.IsSuccess, Is.True); Assert.That(structure.Plan.Components.Count, Is.EqualTo(4));
                    var ids = new ulong[4];
                    Assert.That(world.ReserveBodyIds(ids.Length, ids).IsSuccess, Is.True);
                    WorldResult rejected = new BodyExtractionPlanner().Build(result.Prepared.CandidateWorld, structure.Plan, loaded.Materials,
                        ids, result.Prepared.CandidateInstances, new TickChangeCounter(65536),
                        result.Prepared.CandidateWrites, context, out BodyExtractionPlan plan);
                    Assert.That(rejected.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded)); Assert.That(plan, Is.Null);
                    Assert.That(world.MaterialCells, Is.EqualTo(9)); Assert.That(world.ChangedPositions, Is.Zero);
                    Assert.That(world.IsFixed(BodyFixture.Grid(4, 4)), Is.True); Assert.That(world.Published.Version.CommittedTick, Is.Zero);
                }
            }
        }

        [Test]
        public void InitialRealAllFreeComponentsComputeAtTickZeroWithoutCountingInitializationWrites()
        {
            WorldLoadResult loaded = Load(new[] { new InitialCell(1, 1, 104), new InitialCell(10, 10, 102) }, Array.Empty<Vector2Int>());
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                using IPreparedMutation initial = world.PrepareInitial().Prepared;
                var context = new TransactionContext(world.Published.Version, 0, TickStage.Structure);
                var structure = world.PlanStructure(initial, new ConnectivityAnalyzer(128, 64), context);
                Assert.That(structure.Result.IsSuccess, Is.True);
                var changes = new TickChangeCounter(65536);
                var ids = new ulong[2];
                Assert.That(world.ReserveBodyIds(ids.Length, ids).IsSuccess, Is.True);
                Assert.That(new BodyExtractionPlanner().Build(initial.CandidateWorld, structure.Plan, loaded.Materials, ids, initial.CandidateInstances,
                    changes, initial.CandidateWrites, context, out BodyExtractionPlan plan).IsSuccess, Is.True);
                using (plan)
                {
                    Assert.That(plan.Bodies.Count, Is.EqualTo(2));
                    Assert.That(plan.Bodies.Select(b => b.CellCount), Is.EqualTo(new[] { 1, 1 }));
                    Assert.That(changes.Count, Is.Zero);
                    Assert.That(plan.Writes.Length, Is.Zero);
                    Assert.That(plan.Budget.ChangedPositions, Is.Zero);
                }
            }
        }
    }
}
