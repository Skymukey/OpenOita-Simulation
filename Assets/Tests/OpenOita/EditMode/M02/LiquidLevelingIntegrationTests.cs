using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M02
{
    [Category("LiquidLevelingIntegration")]
    public sealed class LiquidLevelingIntegrationTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _baseline;
        private static CellKey Key(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private TransactionContext Context(TickStage stage) => new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        [SetUp]
        public void SetUp()
        {
            _baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(_baseline.Result.IsSuccess, Is.True);
        }

        [TearDown]
        public void TearDown() => _world?.Dispose();

        private void Create(IEnumerable<InitialCell> cells, int maxChanges = 64, IEnumerable<Vector2Int> burning = null)
        {
            var loaded = new WorldLoadResult(WorldResult.Success(), FixtureCatalog.Config(maxChanges: maxChanges),
                FixtureCatalog.Scene(cells, burning: burning), _baseline.Materials, _baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out _world).IsSuccess, Is.True);
        }

        private void Seed(params CellWrite[] writes)
        {
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            TransactionContext context = Context(TickStage.Commands);
            PreparationResult<IPreparedMutation> candidate = _world.Prepare(writes, context);
            Assert.That(candidate.Result.IsSuccess, Is.True, candidate.Result.Diagnostic.Message);
            using var transaction = new TransactionCoordinator();
            transaction.Own(candidate.Prepared);
            Assert.That(transaction.ValidateAndApply(context, _world.Config.Limits, ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            transaction.MarkCommitted(_world.Published.Version);
        }

        private PreparationResult<IPreparedMutation> PrepareBatch(IRuleBatch batch)
        {
            Assert.That(batch.Result.IsSuccess, Is.True, batch.Result.Diagnostic.Message);
            var writes = new List<CellWrite>();
            var moves = new List<MutationIntent>();
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind == MutationKind.Move)
                {
                    moves.Add(intent);
                }
                else writes.Add(new CellWrite(intent.Source, intent.State));
            }
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Source, default));
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Target, move.State));
            return _world.Prepare(writes.ToArray(), Context(batch.Stage), moves: moves.ToArray());
        }

        [Test]
        public void L01_07_RealWaterHorizontalMoveKeepsCountdownWetIdentityAcrossChunkBoundary()
        {
            var cells = new List<InitialCell> { new InitialCell(127, 10, 101), new InitialCell(150, 10, 101) };
            // 唯一低处在右侧远处；首步只能水平越过127/128，不能把搜索落点当实际目标。
            for (int x = 120; x <= 132; x++) cells.Add(new InitialCell(x, 9, 102));
            Create(cells);
            Seed(new CellWrite(Key(127, 10), new CellSnapshot(101, moveCountdown: 1)),
                new CellWrite(Key(150, 10), new CellSnapshot(101, moveCountdown: 2)));
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle instance);
            _world.Instances.MarkWet(instance);
            ITickInstanceMap stableMap = _world.Instances;
            var rule = new LiquidFlowRule(64);
            IRuleBatch batch = new MovementCandidateResolver(64).Resolve(rule.Execute(_world, _baseline.Materials,
                stableMap, null, Context(TickStage.Water)));
            Assert.That(batch.Result.IsSuccess, Is.True, batch.Result.Diagnostic.Message);
            int moves = 0;
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind != MutationKind.Move) continue;
                moves++;
                Assert.That(intent.Source, Is.EqualTo(Key(127, 10)));
                Assert.That(intent.Target, Is.EqualTo(Key(128, 10)));
                Assert.That(intent.Tier, Is.EqualTo(MoveCandidateTier.PreferredHorizontal));
                Assert.That(intent.State, Is.EqualTo(new CellSnapshot(101, moveCountdown: 2)));
            }
            Assert.That(moves, Is.EqualTo(1));
            PreparationResult<IPreparedMutation> candidate = PrepareBatch(batch);
            Assert.That(candidate.Result.IsSuccess, Is.True, candidate.Result.Diagnostic.Message);
            Assert.That(candidate.Prepared.CandidateInstances.TryResolve(instance, out CellKey candidateKey), Is.True);
            Assert.That(candidateKey, Is.EqualTo(Key(128, 10)));
            Assert.That(candidate.Prepared.CandidateInstances.IsWet(instance), Is.True);
            Assert.That(stableMap.TryResolve(instance, out CellKey oldKey), Is.True);
            Assert.That(oldKey, Is.EqualTo(Key(127, 10)), "准备不得提前采用实例位置。");
            using var transaction = new TransactionCoordinator();
            transaction.Own(candidate.Prepared);
            Assert.That(transaction.ValidateAndApply(Context(TickStage.Water), _world.Config.Limits,
                ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            Assert.That(_world.Instances, Is.SameAs(stableMap));
            Assert.That(stableMap.TryResolve(instance, out CellKey moved), Is.True);
            Assert.That(moved, Is.EqualTo(Key(128, 10)));
            Assert.That(stableMap.IsWet(instance), Is.True);
            AssertCell(_world, Key(127, 10), default);
            AssertCell(_world, moved, new CellSnapshot(101, moveCountdown: 2));
            AssertCell(_world, Key(150, 10), new CellSnapshot(101, moveCountdown: 1));
            Assert.That(_world.ChangedPositions, Is.EqualTo(3), "源、目标和未移动水的倒计时均应计入。");
            AssertCell(_world.Published, Key(127, 10), new CellSnapshot(101, moveCountdown: 1));
            AssertCell(_world.Published, moved, default);
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            transaction.MarkCommitted(_world.Published.Version);
            Assert.That(stableMap.TryResolve(instance, out _), Is.False, "Tick结束后句柄不能继续使用。");
        }

        [Test]
        public void L01_07_HorizontalCompetitionPreservesFullLegalStatesAndDoesNotMixWetMarkers()
        {
            // 水没有燃料/寿命字段。用合法木头和蒸汽状态覆盖M02通用批次的所有状态字段。
            Create(new[] { new InitialCell(127, 10, 104), new InitialCell(129, 10, 104),
                new InitialCell(130, 12, 103), new InitialCell(140, 10, 101) },
                burning: new[] { new Vector2Int(127, 10), new Vector2Int(129, 10) });
            var firstBefore = new CellSnapshot(104, 1, 3, 2, ignitedTick: 1);
            var secondBefore = new CellSnapshot(104, 1, 4, 1, ignitedTick: 1);
            var steamBefore = new CellSnapshot(103, lifetimeTicksRemaining: 199, moveCountdown: 1);
            Seed(new CellWrite(Key(127, 10), firstBefore), new CellWrite(Key(129, 10), secondBefore),
                new CellWrite(Key(130, 12), steamBefore), new CellWrite(Key(140, 10), new CellSnapshot(101, moveCountdown: 2)));
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle first);
            _world.Instances.TryGetInstance(Key(129, 10), out CellInstanceHandle second);
            _world.Instances.TryGetInstance(Key(130, 12), out CellInstanceHandle steam);
            _world.Instances.TryGetInstance(Key(140, 10), out CellInstanceHandle waiting);
            _world.Instances.MarkWet(first);
            _world.Instances.MarkWet(steam);
            var firstAfter = new CellSnapshot(104, 1, 2, 1, ignitedTick: 1);
            var secondAfter = new CellSnapshot(104, 1, 3, 0, ignitedTick: 1);
            var steamAfter = new CellSnapshot(103, lifetimeTicksRemaining: 198, moveCountdown: 2);
            var candidates = new Batch(new[]
            {
                new MutationIntent(MutationKind.Move, second, Key(129, 10), Key(128, 10), secondAfter, MoveCandidateTier.PreferredHorizontal),
                new MutationIntent(MutationKind.Move, first, Key(127, 10), Key(128, 10), firstAfter, MoveCandidateTier.PreferredHorizontal),
                new MutationIntent(MutationKind.Move, steam, Key(130, 12), Key(131, 12), steamAfter, MoveCandidateTier.PreferredHorizontal),
                new MutationIntent(MutationKind.WriteState, first, Key(127, 10), Key(127, 10), firstAfter),
                new MutationIntent(MutationKind.WriteState, second, Key(129, 10), Key(129, 10), secondAfter),
                new MutationIntent(MutationKind.WriteState, waiting, Key(140, 10), Key(140, 10), new CellSnapshot(101, moveCountdown: 1))
            });
            IRuleBatch resolved = new MovementCandidateResolver(64).Resolve(candidates);
            Assert.That(resolved.Intents.Length, Is.EqualTo(4));
            PreparationResult<IPreparedMutation> candidate = PrepareBatch(resolved);
            Assert.That(candidate.Result.IsSuccess, Is.True, candidate.Result.Diagnostic.Message);
            Assert.That(candidate.Prepared.CandidateInstances.TryResolve(first, out CellKey winnerTarget), Is.True);
            Assert.That(winnerTarget, Is.EqualTo(Key(128, 10)));
            Assert.That(candidate.Prepared.CandidateInstances.TryResolve(second, out CellKey loserPosition), Is.True);
            Assert.That(loserPosition, Is.EqualTo(Key(129, 10)));
            using var transaction = new TransactionCoordinator();
            transaction.Own(candidate.Prepared);
            Assert.That(transaction.ValidateAndApply(Context(TickStage.Water), _world.Config.Limits,
                ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            AssertCell(_world, Key(127, 10), default);
            AssertCell(_world, Key(128, 10), firstAfter);
            AssertCell(_world, Key(129, 10), secondAfter);
            AssertCell(_world, Key(130, 12), default);
            AssertCell(_world, Key(131, 12), steamAfter);
            AssertCell(_world, Key(140, 10), new CellSnapshot(101, moveCountdown: 1));
            Assert.That(_world.Instances.TryGetInstance(Key(128, 10), out CellInstanceHandle adopted), Is.True);
            Assert.That(adopted, Is.EqualTo(first));
            Assert.That(_world.Instances.TryGetInstance(Key(129, 10), out adopted), Is.True);
            Assert.That(adopted, Is.EqualTo(second));
            Assert.That(_world.Instances.IsWet(first), Is.True);
            Assert.That(_world.Instances.IsWet(second), Is.False);
            Assert.That(_world.Instances.IsWet(steam), Is.True);
            Assert.That(_world.Instances.IsWet(waiting), Is.False);
            Assert.That(_world.MaterialCells, Is.EqualTo(4));
            Assert.That(_world.ChangedPositions, Is.EqualTo(6));
            AssertCell(_world.Published, Key(127, 10), firstBefore);
            AssertCell(_world.Published, Key(129, 10), secondBefore);
            AssertCell(_world.Published, Key(131, 12), default);
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            transaction.MarkCommitted(_world.Published.Version);
            AssertCell(_world.Published, Key(128, 10), firstAfter);
        }

        [TestCase("IntentBuffer")]
        [TestCase("ChangedPositions")]
        [TestCase("BeforeApply")]
        public void L01_07_WholeWaterBatchFailureKeepsEveryStateInstanceWetMarkerAndPublishedTick(string failure)
        {
            Create(new[] { new InitialCell(127, 10, 101), new InitialCell(129, 10, 101), new InitialCell(140, 10, 101) },
                maxChanges: failure == "ChangedPositions" ? 4 : 64);
            Seed(new CellWrite(Key(140, 10), new CellSnapshot(101, moveCountdown: 2)));
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle first);
            _world.Instances.TryGetInstance(Key(129, 10), out CellInstanceHandle second);
            _world.Instances.MarkWet(first);
            ITickInstanceMap instances = _world.Instances;
            var rule = new LiquidFlowRule(64, intentCapacity: failure == "IntentBuffer" ? 1 : 64);
            IRuleBatch batch = new MovementCandidateResolver(64).Resolve(rule.Execute(_world, _baseline.Materials,
                instances, null, Context(TickStage.Water)));
            if (failure == "IntentBuffer")
            {
                Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                Assert.That(batch.Intents.Length, Is.Zero, "意图缓冲失败不能泄漏半批结果。");
            }
            else
            {
                PreparationResult<IPreparedMutation> candidate = PrepareBatch(batch);
                if (failure == "ChangedPositions")
                {
                    Assert.That(candidate.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                    Assert.That(candidate.Prepared, Is.Null);
                }
                else
                {
                    Assert.That(candidate.Result.IsSuccess, Is.True, candidate.Result.Diagnostic.Message);
                    using var transaction = new TransactionCoordinator();
                    transaction.Own(candidate.Prepared);
                    Assert.That(transaction.ValidateAndApply(Context(TickStage.Water), _world.Config.Limits,
                        ContractDefaults.CpuBudgetBytes, new BeforeApplyFailure()).ErrorCode,
                        Is.EqualTo(WorldErrorCode.CapacityExceeded));
                    Assert.That(transaction.RequiresFault, Is.True, "自动水阶段失败须由调度器冻结世界。");
                }
            }
            Assert.That(_world.Instances, Is.SameAs(instances));
            Assert.That(instances.TryResolve(first, out CellKey unchanged), Is.True);
            Assert.That(unchanged, Is.EqualTo(Key(127, 10)));
            Assert.That(instances.TryResolve(second, out unchanged), Is.True);
            Assert.That(unchanged, Is.EqualTo(Key(129, 10)));
            Assert.That(instances.IsWet(first), Is.True);
            Assert.That(instances.IsWet(second), Is.False);
            AssertCell(_world, Key(127, 10), new CellSnapshot(101));
            AssertCell(_world, Key(129, 10), new CellSnapshot(101));
            AssertCell(_world, Key(140, 10), new CellSnapshot(101, moveCountdown: 2));
            AssertCell(_world, Key(127, 9), default);
            AssertCell(_world, Key(129, 9), default);
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Assert.That(_world.ChangedPositions, Is.Zero);
            _world.Fault();
            Assert.That(_world.Published.Version.CommittedTick, Is.EqualTo(1));
            AssertCell(_world.Published, Key(127, 10), new CellSnapshot(101));
            AssertCell(_world.Published, Key(129, 10), new CellSnapshot(101));
            AssertCell(_world.Published, Key(140, 10), new CellSnapshot(101, moveCountdown: 2));
        }

        private static void AssertCell(IWorkingWorldView world, CellKey key, CellSnapshot expected)
        {
            Assert.That(world.Read(key, out CellSnapshot actual).IsSuccess, Is.True);
            Assert.That(actual, Is.EqualTo(expected), $"状态应完整保持：{key.Position.X}/{key.Position.Y}");
        }

        [Test]
        public void L01_07_LaterStageFaultAfterWaterApplyKeepsLastPublishedWaterSnapshot()
        {
            Create(new[] { new InitialCell(127, 10, 101), new InitialCell(129, 10, 101) });
            ICommittedWorldView published = _world.Published;
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle instance);
            _world.Instances.MarkWet(instance);
            var rule = new LiquidFlowRule(64);
            IRuleBatch batch = new MovementCandidateResolver(64).Resolve(rule.Execute(_world, _baseline.Materials,
                _world.Instances, null, Context(TickStage.Water)));
            PreparationResult<IPreparedMutation> candidate = PrepareBatch(batch);
            Assert.That(candidate.Result.IsSuccess, Is.True, candidate.Result.Diagnostic.Message);
            using var transaction = new TransactionCoordinator();
            transaction.Own(candidate.Prepared);
            Assert.That(transaction.ValidateAndApply(Context(TickStage.Water), _world.Config.Limits,
                ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            AssertCell(_world, Key(127, 9), new CellSnapshot(101, moveCountdown: 2));
            AssertCell(_world, Key(129, 9), new CellSnapshot(101, moveCountdown: 2));
            Assert.That(_world.Instances.IsWet(instance), Is.True);
            Assert.That(_world.ChangedPositions, Is.EqualTo(4));
            // 模拟后续阶段失败；水已采用到工作副本仍不能提前发布或继续运行。
            _world.Fault();
            Assert.That(_world.PublishState().ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(_world.Published, Is.SameAs(published));
            Assert.That(published.Version.CommittedTick, Is.Zero);
            AssertCell(published, Key(127, 10), new CellSnapshot(101));
            AssertCell(published, Key(129, 10), new CellSnapshot(101));
            AssertCell(published, Key(127, 9), default);
            AssertCell(published, Key(129, 9), default);
            Assert.That(_world.Read(Key(127, 9), out _).ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(_world.Instances.TryResolve(instance, out _), Is.False);
        }

        private static void AssertCell(ICommittedWorldView world, CellKey key, CellSnapshot expected)
        {
            Assert.That(world.Read(key, out CellSnapshot actual).IsSuccess, Is.True);
            Assert.That(actual, Is.EqualTo(expected), $"状态应完整保持：{key.Position.X}/{key.Position.Y}");
        }

        private sealed class Batch : IRuleBatch
        {
            private readonly MutationIntent[] _intents;
            internal Batch(MutationIntent[] intents) { _intents = intents; }
            public WorldResult Result => WorldResult.Success();
            public TickStage Stage => TickStage.Water;
            public ReadOnlySpan<MutationIntent> Intents => _intents;
        }

        private sealed class BeforeApplyFailure : IFailureInjector
        {
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point == FailurePoint.BeforeApply ?
                WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Water", "L01-07", "测试注入整批应用前失败。")) :
                WorldResult.Success();
        }
    }
}
