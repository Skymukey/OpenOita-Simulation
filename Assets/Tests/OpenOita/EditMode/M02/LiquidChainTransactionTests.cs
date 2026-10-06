using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M02
{
    [Category("LiquidLevelingIntegration")]
    public sealed class LiquidChainTransactionTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _baseline;
        private static CellKey Key(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private TransactionContext Context(TickStage stage = TickStage.Water) =>
            new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        [SetUp]
        public void SetUp()
        {
            _baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(_baseline.Result.IsSuccess, Is.True);
        }

        [TearDown]
        public void TearDown() => _world?.Dispose();

        private void Create(IEnumerable<InitialCell> cells, int maxChanges = 64)
        {
            var loaded = new WorldLoadResult(WorldResult.Success(), FixtureCatalog.Config(maxChanges: maxChanges),
                FixtureCatalog.Scene(cells), _baseline.Materials, _baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out _world).IsSuccess, Is.True);
        }

        private MutationIntent Move(CellKey source, CellKey target, uint countdown = 2)
        {
            Assert.That(_world.Instances.TryGetInstance(source, out CellInstanceHandle instance), Is.True);
            Assert.That(_world.Read(source, out CellSnapshot before).IsSuccess, Is.True);
            var state = new CellSnapshot(before.MaterialId, before.Flags, before.FuelTicksRemaining,
                before.SpreadCountdown, before.LifetimeTicksRemaining, countdown, before.IgnitedTick);
            return new MutationIntent(MutationKind.Move, instance, source, target, state);
        }

        private PreparationResult<IPreparedMutation> Prepare(MutationIntent[] moves, TickStage stage = TickStage.Water)
        {
            var writes = new List<CellWrite>();
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Source, default));
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Target, move.State));
            return _world.Prepare(writes.ToArray(), Context(stage), moves: moves);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void L01_07_AtomicChainPreservesEveryIdentityWetMarkerAndCountsSameByteInterior(bool reverse)
        {
            Create(new[] { new InitialCell(127, 11, 101), new InitialCell(127, 10, 101), new InitialCell(128, 10, 101) });
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            // 状态事务独立于规则计时裁决；此处刻意令搬运前后字节相同，单独验证身份变更计数。
            var sameBytes = new CellSnapshot(101, moveCountdown: 2);
            using (IPreparedMutation seed = _world.Prepare(new[] { new CellWrite(Key(127, 11), sameBytes),
                new CellWrite(Key(127, 10), sameBytes), new CellWrite(Key(128, 10), sameBytes) }, Context(TickStage.Commands)).Prepared)
            {
                Assert.That(seed, Is.Not.Null);
                Assert.That(seed.Apply(Context(TickStage.Commands)).IsSuccess, Is.True);
            }
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            ITickInstanceMap instances = _world.Instances;
            var moves = new[] { Move(Key(127, 11), Key(127, 10)), Move(Key(127, 10), Key(128, 10)), Move(Key(128, 10), Key(128, 9)) };
            CellInstanceHandle root = moves[0].Instance, middle = moves[1].Instance, tail = moves[2].Instance;
            instances.MarkWet(middle);
            if (reverse) Array.Reverse(moves);
            PreparationResult<IPreparedMutation> preparation = Prepare(moves);
            Assert.That(preparation.Result.IsSuccess, Is.True, preparation.Result.Diagnostic.Message);
            IPreparedMutation prepared = preparation.Prepared;
            Assert.That(prepared.CandidateWrites.Length, Is.EqualTo(4));
            Assert.That(prepared.Budget.ChangedPositions, Is.EqualTo(4), "两个中间格字节相同，实例身份变化仍应计数。");
            AssertInstance(prepared.CandidateInstances, root, Key(127, 10), false);
            AssertInstance(prepared.CandidateInstances, middle, Key(128, 10), true);
            AssertInstance(prepared.CandidateInstances, tail, Key(128, 9), false);
            AssertInstance(instances, root, Key(127, 11), false);
            AssertInstance(instances, middle, Key(127, 10), true);
            AssertInstance(instances, tail, Key(128, 10), false);
            using var transaction = new TransactionCoordinator();
            transaction.Own(prepared);
            Assert.That(transaction.ValidateAndApply(Context(), _world.Config.Limits, ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            Assert.That(_world.Instances, Is.SameAs(instances));
            AssertInstance(instances, root, Key(127, 10), false);
            AssertInstance(instances, middle, Key(128, 10), true);
            AssertInstance(instances, tail, Key(128, 9), false);
            Assert.That(instances.TryGetInstance(Key(127, 11), out _), Is.False);
            Assert.That(_world.Read(Key(127, 11), out CellSnapshot empty).IsSuccess, Is.True);
            Assert.That(empty, Is.EqualTo(default(CellSnapshot)));
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Assert.That(_world.ChangedPositions, Is.EqualTo(4));
            Assert.That(_world.Published.Read(Key(128, 9), out empty).IsSuccess, Is.True);
            Assert.That(empty.MaterialId, Is.Zero);
            Assert.That(_world.Published.Version.CommittedTick, Is.EqualTo(1));
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            transaction.MarkCommitted(_world.Published.Version);
            Assert.That(_world.Published.Read(Key(128, 9), out CellSnapshot moved).IsSuccess, Is.True);
            Assert.That(moved, Is.EqualTo(sameBytes));
            Assert.That(prepared.CandidateInstances.TryResolve(middle, out _), Is.False);
        }

        [TestCase("Cycle")]
        [TestCase("Horizontal")]
        [TestCase("Upward")]
        [TestCase("Diagonal")]
        [TestCase("LongStep")]
        [TestCase("UnreleasedTarget")]
        [TestCase("DifferentMaterial")]
        [TestCase("WrongStage")]
        [TestCase("DuplicateSource")]
        [TestCase("DuplicateTarget")]
        public void L01_07_InvalidAtomicChainIsRejectedBeforeStateOrInstanceChanges(string invalid)
        {
            var cells = new[] { new InitialCell(127, 11, 101), new InitialCell(127, 10, 101),
                new InitialCell(128, 10, (ushort)(invalid == "DifferentMaterial" ? 103 : 101)),
                new InitialCell(128, 11, 101), new InitialCell(127, 12, 101) };
            Create(cells);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Key(127, 11), out CellInstanceHandle original);
            _world.Instances.MarkWet(original);
            MutationIntent[] moves;
            TickStage stage = TickStage.Water;
            switch (invalid)
            {
                case "Cycle": moves = new[] { Move(Key(127, 11), Key(127, 10)), Move(Key(127, 10), Key(127, 11)) }; break;
                case "Horizontal": moves = new[] { Move(Key(127, 10), Key(128, 10)), Move(Key(128, 10), Key(129, 10)) }; break;
                case "Upward": moves = new[] { Move(Key(127, 10), Key(127, 11)), Move(Key(127, 11), Key(127, 12)), Move(Key(127, 12), Key(127, 13)) }; break;
                case "Diagonal": moves = new[] { Move(Key(127, 11), Key(128, 10)), Move(Key(128, 10), Key(128, 9)) }; break;
                case "LongStep": moves = new[] { Move(Key(127, 12), Key(127, 10)), Move(Key(127, 10), Key(128, 10)), Move(Key(128, 10), Key(128, 9)) }; break;
                case "UnreleasedTarget": moves = new[] { Move(Key(127, 11), Key(127, 10)) }; break;
                case "DuplicateSource": moves = new[] { Move(Key(127, 11), Key(127, 10)), Move(Key(127, 11), Key(128, 11)) }; break;
                case "DuplicateTarget": moves = new[] { Move(Key(127, 11), Key(127, 10)), Move(Key(128, 10), Key(127, 10)) }; break;
                default:
                    moves = new[] { Move(Key(127, 11), Key(127, 10)), Move(Key(127, 10), Key(128, 10)), Move(Key(128, 10), Key(128, 9)) };
                    if (invalid == "WrongStage") stage = TickStage.Commands;
                    break;
            }
            PreparationResult<IPreparedMutation> preparation = Prepare(moves, stage);
            Assert.That(preparation.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(preparation.Prepared, Is.Null);
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.MaterialCells, Is.EqualTo(cells.Length));
            AssertInstance(_world.Instances, original, Key(127, 11), true);
            foreach (InitialCell cell in cells)
            {
                CellKey key = Key(cell.Position.x, cell.Position.y);
                Assert.That(_world.Read(key, out CellSnapshot actual).IsSuccess, Is.True);
                Assert.That(actual.MaterialId, Is.EqualTo(cell.MaterialId));
                Assert.That(actual.MoveCountdown, Is.Zero);
            }
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }

        [Test]
        public void L01_07_AtomicChainBudgetFailureDoesNotAdoptAnyInternalNode()
        {
            Create(new[] { new InitialCell(127, 11, 101), new InitialCell(127, 10, 101), new InitialCell(128, 10, 101) }, maxChanges: 3);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            var moves = new[] { Move(Key(127, 11), Key(127, 10)), Move(Key(127, 10), Key(128, 10)), Move(Key(128, 10), Key(128, 9)) };
            _world.Instances.MarkWet(moves[1].Instance);
            PreparationResult<IPreparedMutation> preparation = Prepare(moves);
            Assert.That(preparation.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(preparation.Prepared, Is.Null);
            foreach (MutationIntent move in moves)
                AssertInstance(_world.Instances, move.Instance, move.Source, move.Instance.Equals(moves[1].Instance));
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Read(Key(128, 9), out CellSnapshot empty).IsSuccess, Is.True);
            Assert.That(empty, Is.EqualTo(default(CellSnapshot)));
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }

        private static void AssertInstance(ITickInstanceMap map, CellInstanceHandle instance, CellKey target, bool wet)
        {
            Assert.That(map.TryResolve(instance, out CellKey actual), Is.True);
            Assert.That(actual, Is.EqualTo(target));
            Assert.That(map.TryGetInstance(target, out CellInstanceHandle inverse), Is.True);
            Assert.That(inverse, Is.EqualTo(instance));
            Assert.That(map.IsWet(instance), Is.EqualTo(wet));
        }
    }
}
