using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Structure.Connectivity
{
    // 消费真实 M01/M02/M03 核心，在 Apply 前分析候选；尚未装配正式命令、体或物理。
    public sealed class ConnectivityStateCoreTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private ConnectivityAnalyzer _analyzer;
        private static CellKey Key(int x, int y) => ConnectivityFixture.Grid(x, y);
        private TransactionContext Context(TickStage stage) => new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        [SetUp]
        public void SetUp()
        {
            WorldLoadResult baseline = ConnectivityFixture.Load();
            var config = new WorldConfig(1, 129, 130, 128, 0.1f, 0.02f, -9.81f, 1, baseline.Config.Limits);
            var scene = FixtureCatalog.Scene(new[] { new InitialCell(126, 10, 104), new InitialCell(127, 10, 104),
                new InitialCell(128, 10, 104) }, new[] { new Vector2Int(126, 10) });
            _loaded = new WorldLoadResult(WorldResult.Success(), config, scene, baseline.Materials, baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world).IsSuccess, Is.True);
            _analyzer = new ConnectivityAnalyzer(128, 64);
        }

        [TearDown]
        public void TearDown() { _world?.Dispose(); }

        private StructurePlan Analyze(IPreparedMutation prepared = null, TickStage stage = TickStage.Commands)
        {
            int cells = _world.MaterialCells;
            int chunks = _world.ChunkCount;
            StructurePlanResult result = prepared == null ? _world.PlanInitialStructure(_analyzer)
                : _world.PlanStructure(prepared, _analyzer, Context(stage));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            Assert.That(_world.MaterialCells, Is.EqualTo(cells));
            Assert.That(_world.ChunkCount, Is.EqualTo(chunks));
            TestContext.WriteLine(ConnectivityAnalyzerTests.Describe(result.Plan));
            return result.Plan;
        }

        private static IPreparedMutation Prepared(PreparationResult<IPreparedMutation> result)
        {
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            return result.Prepared;
        }

        private void Apply(IPreparedMutation prepared, TickStage stage)
        {
            Assert.That(prepared.Preflight(Context(stage)).IsSuccess, Is.True);
            Assert.That(prepared.Apply(Context(stage)).IsSuccess, Is.True);
        }

        [Test]
        public void M04_T3_01_T3_03_RealInitialStateAndRemoveProduceCrossChunkPlanWithoutPublishing()
        {
            StructurePlan initial = Analyze();
            Assert.That(initial.Components.Count, Is.EqualTo(1));
            Assert.That(initial.Components[0].Members.Count, Is.EqualTo(3));
            Assert.That(initial.Components[0].IsFixed, Is.True);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            using IPreparedMutation prepared = Prepared(_world.Prepare(new[] { new CellWrite(Key(127, 10), default) }, Context(TickStage.Commands)));
            IWorkingWorldView candidate = prepared.CandidateWorld;
            StructurePlan removed = Analyze(prepared);
            Assert.That(removed.Components.Select(component => component.IsFixed), Is.EqualTo(new[] { true, false }));
            Assert.That(removed.Components.Select(component => component.Disposition), Is.EqualTo(new[] {
                StructureDisposition.RetainFixedGrid, StructureDisposition.ExtractFreeGrid }));
            _world.Read(Key(127, 10), out CellSnapshot original);
            Assert.That(original.MaterialId, Is.EqualTo(104));
            candidate.Read(Key(127, 10), out CellSnapshot candidateRemoved);
            Assert.That(candidateRemoved.MaterialId, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.Zero);
            string description = ConnectivityAnalyzerTests.Describe(removed);
            Apply(prepared, TickStage.Commands);
            Assert.That(candidate.Read(Key(127, 10), out _).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(ConnectivityAnalyzerTests.Describe(removed), Is.EqualTo(description));
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
            _world.Published.Read(Key(127, 10), out CellSnapshot published);
            Assert.That(published.MaterialId, Is.EqualTo(104));
            TestContext.WriteLine("真实M02候选在Apply前：桥已移除，工作三格/ChangedPositions0不变；Apply后候选失效、计划副本保留，提交版仍Tick0。正式Remove与提取未测。");
        }

        [Test]
        public void M04_T3_05_RealSameIdReplaceInvalidatesFixedInstance()
        {
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            Assert.That(_world.Instances.TryGetInstance(Key(126, 10), out CellInstanceHandle original), Is.True);
            using IPreparedMutation prepared = Prepared(_world.PrepareReplace(Key(126, 10), 104, Context(TickStage.Commands)));
            Assert.That(prepared.CandidateWorld.IsFixed(Key(126, 10)), Is.False);
            StructurePlan plan = Analyze(prepared);
            Assert.That(_world.IsFixed(Key(126, 10)), Is.True);
            Assert.That(_world.Instances.TryResolve(original, out _), Is.True);
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(plan.Components.Single().Disposition, Is.EqualTo(StructureDisposition.ExtractFreeGrid));
            Apply(prepared, TickStage.Commands);
            Assert.That(_world.Instances.TryResolve(original, out _), Is.False);
            Assert.That(_world.Instances.TryGetInstance(Key(126, 10), out CellInstanceHandle replacement), Is.True);
            Assert.That(replacement, Is.Not.EqualTo(original));
            Assert.That(_world.IsFixed(Key(126, 10)), Is.False);
            Assert.That(plan.Components.Count, Is.EqualTo(1));
            Assert.That(plan.Components[0].IsFixed, Is.False);
            Assert.That(plan.Components[0].Members.Count, Is.EqualTo(3));
            TestContext.WriteLine("Apply前候选同ID Replace已撤销固定，旧工作实例/固定点不变；Apply后三格待提取，原句柄失效。正式Replace接线未测。");
        }

        [Test]
        public void M04_T3_06_RealSpawnStateHasNoNewFixedPoint()
        {
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            using IPreparedMutation prepared = Prepared(_world.PrepareReplace(Key(10, 10), 102, Context(TickStage.Commands)));
            StructurePlan plan = Analyze(prepared);
            _world.Read(Key(10, 10), out CellSnapshot empty);
            Assert.That(empty.MaterialId, Is.Zero);
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(prepared.CandidateWorld.IsFixed(Key(10, 10)), Is.False);
            Assert.That(plan.Components.Count, Is.EqualTo(2));
            Assert.That(plan.Components[0].IsFixed, Is.False);
            Assert.That(plan.Components[0].Members.Count, Is.EqualTo(1));
            Assert.That(plan.Components[1].IsFixed, Is.True);
            Assert.That(plan.Components[0].Disposition, Is.EqualTo(StructureDisposition.ExtractFreeGrid));
            Apply(prepared, TickStage.Commands);
            TestContext.WriteLine("M02新格候选在Apply前进入单格提取计划，旧工作三格和0计数不变，没有新固定点；正式Spawn参数/占据/令牌未测。");
        }

        [Test]
        public void M04_T9_01_RealBurnRuleBridgeRemovalKeepsSurvivingStateAndInstance()
        {
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            var bridge = new CellSnapshot(104, 1, 1, 2);
            var tip = new CellSnapshot(104, 1, 3, 2);
            using (IPreparedMutation seed = Prepared(_world.Prepare(new[] { new CellWrite(Key(127, 10), bridge), new CellWrite(Key(128, 10), tip) },
                Context(TickStage.Burning)))) Apply(seed, TickStage.Burning);
            _world.Instances.TryGetInstance(Key(128, 10), out CellInstanceHandle original);
            var rule = new BurnRule(128);
            IRuleBatch batch = rule.Execute(_world, _loaded.Materials, _world.Instances, null, Context(TickStage.Burning));
            Assert.That(batch.Result.IsSuccess, Is.True, batch.Result.Diagnostic.Message);
            var writes = new List<CellWrite>();
            foreach (MutationIntent intent in batch.Intents)
            {
                writes.Add(new CellWrite(intent.Source, intent.State));
                if (intent.Source.Equals(Key(127, 10)))
                {
                    Assert.That(intent.Kind, Is.EqualTo(MutationKind.Remove));
                    Assert.That(intent.RemovalReason, Is.EqualTo(RemovalReason.BurnedOut));
                }
            }
            using IPreparedMutation prepared = Prepared(_world.Prepare(writes.ToArray(), Context(TickStage.Burning)));
            int changesBeforeAnalysis = _world.ChangedPositions;
            StructurePlan plan = Analyze(prepared, TickStage.Burning);
            Assert.That(plan.Components.Select(component => component.IsFixed), Is.EqualTo(new[] { true, false }));
            CellKey surviving = plan.Components[1].Members.Single();
            Assert.That(surviving, Is.EqualTo(Key(128, 10)));
            prepared.CandidateWorld.Read(surviving, out CellSnapshot after);
            Assert.That(after, Is.EqualTo(new CellSnapshot(104, 1, 2, 1)));
            _world.Read(Key(127, 10), out CellSnapshot oldBridge);
            _world.Read(surviving, out CellSnapshot oldTip);
            Assert.That(oldBridge, Is.EqualTo(bridge));
            Assert.That(oldTip, Is.EqualTo(tip));
            Assert.That(_world.ChangedPositions, Is.EqualTo(changesBeforeAnalysis));
            Assert.That(_world.Instances.TryGetInstance(surviving, out CellInstanceHandle current), Is.True);
            Assert.That(current, Is.EqualTo(original));
            Assert.That(plan.Components[1].Disposition, Is.EqualTo(StructureDisposition.ExtractFreeGrid));
            Apply(prepared, TickStage.Burning);
            _world.Read(surviving, out CellSnapshot applied);
            Assert.That(applied, Is.EqualTo(after));
            _world.Instances.TryGetInstance(surviving, out current);
            Assert.That(current, Is.EqualTo(original));
            TestContext.WriteLine("真实M03→M02候选→M04应用前链：桥燃尽、端格Burning=1/Fuel=2/Spread=1，旧工作桥/端状态和实例未变；Apply后幸存句柄保留。体提取未测。");
        }

        [Test]
        public void M04_T7_01_InsufficientBufferLeavesRealWorldAndPublishedVersionUnchanged()
        {
            _analyzer = new ConnectivityAnalyzer(2, 64);
            int chunks = _world.ChunkCount;
            StructurePlanResult result = _analyzer.Plan(_world, _loaded.Materials, Array.Empty<CellKey>());
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(result.Plan, Is.Null);
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Assert.That(_world.ChunkCount, Is.EqualTo(chunks));
            Assert.That(_world.IsFixed(Key(126, 10)), Is.True);
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }
    }
}
