using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Bodies
{
    public sealed class BodyIdReservationTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;

        [SetUp]
        public void SetUp()
        {
            WorldLoadResult baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(baseline.Result.IsSuccess, Is.True);
            _loaded = new WorldLoadResult(WorldResult.Success(), baseline.Config,
                FixtureCatalog.Scene(Array.Empty<InitialCell>()), baseline.Materials, baseline.Rules);
            CreateWorld(1);
        }

        [TearDown]
        public void TearDown() => _world?.Dispose();

        private void CreateWorld(ulong generation)
        {
            Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, generation, out _world).IsSuccess, Is.True);
        }

        private ulong[] Reserve(int count)
        {
            var ids = new ulong[count];
            Assert.That(_world.ReserveBodyIds(count, ids).IsSuccess, Is.True);
            return ids;
        }

        [Test]
        public void BatchStartsAtOneAndOnlyWritesRequestedPrefix()
        {
            var buffer = new ulong[] { 700, 700, 700, 700 };
            Assert.That(_world.ReserveBodyIds(3, buffer).IsSuccess, Is.True);
            Assert.That(buffer, Is.EqualTo(new ulong[] { 1, 2, 3, 700 }));
            Assert.That(Reserve(2), Is.EqualTo(new ulong[] { 4, 5 }));
            Assert.That(_world.MaterialCells, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Bodies.Length, Is.Zero);
            Assert.That(_world.Published.Version, Is.EqualTo(new WorldVersion(1, 0)));
            Assert.That(new CellPositionKey(OwnerKind.Grid, 0, 0, 0).BodyId, Is.Zero);
            Assert.Throws<ArgumentException>(() => new CellPositionKey(OwnerKind.Body, 0, 0, 0));
        }

        [TestCase(-1, 2, WorldErrorCode.InvalidArgument)]
        [TestCase(3, 2, WorldErrorCode.BufferTooSmall)]
        [TestCase(1, 0, WorldErrorCode.BufferTooSmall)]
        [TestCase(int.MaxValue, 2, WorldErrorCode.BufferTooSmall)]
        public void InvalidBatchLeavesBufferAndSequenceUnchanged(int count, int length, WorldErrorCode expected)
        {
            var buffer = Enumerable.Repeat(700UL, length).ToArray();
            WorldResult result = _world.ReserveBodyIds(count, buffer);
            Assert.That(result.ErrorCode, Is.EqualTo(expected));
            Assert.That(buffer, Is.EqualTo(Enumerable.Repeat(700UL, length)));
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 1 }));
        }

        [TestCase(0)]
        [TestCase(2)]
        public void ZeroCountIsSuccessfulNoOp(int length)
        {
            var buffer = Enumerable.Repeat(700UL, length).ToArray();
            Assert.That(_world.ReserveBodyIds(0, buffer).IsSuccess, Is.True);
            Assert.That(buffer, Is.EqualTo(Enumerable.Repeat(700UL, length)));
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 1 }));
        }

        [Test]
        public void ExhaustedBatchDoesNotPartiallyReserveAndNeverWrapsToZero()
        {
            var sequence = new IdentitySequence(ulong.MaxValue - 2);
            var buffer = new ulong[] { 700, 700, 700 };
            WorldResult result = sequence.Reserve(3, buffer);
            Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(buffer, Is.EqualTo(new ulong[] { 700, 700, 700 }));
            Assert.That(sequence.Reserve(2, buffer).IsSuccess, Is.True);
            Assert.That(buffer, Is.EqualTo(new ulong[] { ulong.MaxValue - 1, ulong.MaxValue, 700 }));
            Assert.That(sequence.Reserve(1, buffer).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(buffer[0], Is.EqualTo(ulong.MaxValue - 1));
            Assert.That(sequence.Reserve(out ulong exhausted).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(exhausted, Is.Zero);
            Assert.That(sequence.Reserve(0, Span<ulong>.Empty).IsSuccess, Is.True);
        }

        [Test]
        public void CandidateAbortAndTickPublishDoNotResetGenerationSequence()
        {
            Assert.That(Reserve(3), Is.EqualTo(new ulong[] { 1, 2, 3 }));
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            Assert.That(Reserve(2), Is.EqualTo(new ulong[] { 4, 5 }));
            var context = new TransactionContext(_world.Published.Version, _world.WorkingTick, TickStage.Commands);
            var preparation = _world.Prepare(ReadOnlySpan<CellWrite>.Empty, context);
            Assert.That(preparation.Result.IsSuccess, Is.True);
            using (preparation.Prepared)
            {
                Assert.That(Reserve(2), Is.EqualTo(new ulong[] { 6, 7 }));
                preparation.Prepared.Abort();
                preparation.Prepared.Abort();
            }
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 8 }));
            Assert.That(_world.MaterialCells, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.Zero);
        }

        [Test]
        public void RecreatedGenerationOwnsNewSequenceAndOldCallbackCannotReserve()
        {
            BodyIdReservation oldReservation = _world.ReserveBodyIds;
            Reserve(7);
            _world.Dispose();
            CreateWorld(2);
            var buffer = new ulong[] { 700 };
            Assert.That(oldReservation(1, buffer).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
            Assert.That(buffer[0], Is.EqualTo(700));
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 1 }));
            Assert.That(_world.Generation, Is.EqualTo(2));
        }

        [TestCase(false, WorldErrorCode.Faulted)]
        [TestCase(true, WorldErrorCode.Disposed)]
        public void ClosedWorldCannotReserveEvenZeroCount(bool dispose, WorldErrorCode expected)
        {
            if (dispose) _world.Dispose();
            else _world.Fault();
            var buffer = new ulong[] { 700 };
            Assert.That(_world.ReserveBodyIds(1, buffer).ErrorCode, Is.EqualTo(expected));
            Assert.That(_world.ReserveBodyIds(0, Span<ulong>.Empty).ErrorCode, Is.EqualTo(expected));
            Assert.That(buffer[0], Is.EqualTo(700));
        }

        [Test]
        public void WrongThreadCannotConsumeOrWriteReservation()
        {
            var buffer = new ulong[] { 700 };
            WorldResult result = default;
            var thread = new Thread(() => result = _world.ReserveBodyIds(1, buffer));
            thread.Start();
            thread.Join();
            Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(buffer[0], Is.EqualTo(700));
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 1 }));
        }

        private sealed class PreparationProbe : IMaterialMutationPreparer
        {
            internal readonly ulong[] Ids = new ulong[2];
            public PreparationResult<IPreparedMaterialMutation> Prepare(IPreparedMutation candidate, StructurePlan structure,
                BodyIdReservation reserveBodyIds, ITickChangeCounter changes, MaterialOwnershipPreparation prepareOwnership,
                in TransactionContext context, IFailureInjector failures)
            {
                WorldResult reserved = reserveBodyIds(Ids.Length, Ids);
                return new PreparationResult<IPreparedMaterialMutation>(reserved.IsSuccess
                    ? WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("Probe", "prepare", "测试消费者只验证预留输入，不返回正式准备结果。"))
                    : reserved);
            }
        }

        [Test]
        public void ExistingMaterialPrepareContractReceivesM02ReservationCallback()
        {
            var probe = new PreparationProbe();
            IMaterialMutationPreparer consumer = probe;
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            var context = new TransactionContext(_world.Published.Version, 1, TickStage.Commands);
            var edit = _world.Prepare(ReadOnlySpan<CellWrite>.Empty, context);
            Assert.That(edit.Result.IsSuccess, Is.True);
            var structure = _world.PlanStructure(edit.Prepared, new ConnectivityAnalyzer(128, 64), context);
            Assert.That(structure.Result.IsSuccess, Is.True);
            var result = _world.PrepareMaterial(edit.Prepared, structure.Plan, consumer, context);
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(result.Prepared, Is.Null);
            Assert.That(probe.Ids, Is.EqualTo(new ulong[] { 1, 2 }));
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 3 }));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void BodySevenSplitsToEightAndNineOrRetainsSeven(bool split)
        {
            // 原体由离线候选提供，先通过同一 M02 序列预留到7；不冒充体目录已接入。
            Reserve(7);
            var candidate = new BodyFixture();
            candidate.AddStrip(7);
            candidate.Remove(BodyFixture.Body(7, split ? 1 : 2, 0));
            var ids = Reserve(split ? 2 : 0);
            using BodyExtractionPlan plan = BodyFixture.Success(candidate, ids);
            Assert.That(plan.Bodies.Select(body => body.Snapshot.BodyId),
                Is.EqualTo(split ? new ulong[] { 8, 9 } : new ulong[] { 7 }));
            Assert.That(plan.RetiredBodyIds.ToArray(), Is.EqualTo(split ? new ulong[] { 7 } : Array.Empty<ulong>()));
            Assert.That(plan.BodyMappings.ToArray().Select(mapping => mapping.OldBodyId),
                Is.EqualTo(split ? new ulong[] { 7, 7 } : new ulong[] { 7 }));
            Assert.That(plan.BodyMappings.ToArray().Select(mapping => mapping.NewBodyId),
                Is.EqualTo(split ? new ulong[] { 8, 9 } : new ulong[] { 7 }));
            Assert.That(Reserve(1), Is.EqualTo(split ? new ulong[] { 10 } : new ulong[] { 8 }));
        }

        [Test]
        public void EmptyBodyRetirementDoesNotRecycleIdForNewGridBody()
        {
            Reserve(7);
            var candidate = new BodyFixture();
            candidate.AddStrip(7);
            for (int x = 0; x < 3; x++) candidate.Remove(BodyFixture.Body(7, x, 0));
            using (BodyExtractionPlan retired = BodyFixture.Success(candidate))
            {
                Assert.That(retired.RetiredBodyIds.ToArray(), Is.EqualTo(new ulong[] { 7 }));
                Assert.That(retired.BodyMappings[0].NewBodyId, Is.Zero);
            }
            candidate.Put(BodyFixture.Grid(10, 10));
            using BodyExtractionPlan created = BodyFixture.Success(candidate, Reserve(1));
            Assert.That(created.Bodies.Single().Snapshot.BodyId, Is.EqualTo(8));
            Assert.That(created.BodyMappings.ToArray().Any(mapping => mapping.OldBodyId == 0 && mapping.NewBodyId == 8), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AllNewIdsFollowOriginalOwnerThenMinimumYX(bool reverse)
        {
            Reserve(20);
            var candidate = new BodyFixture();
            candidate.AddStrip(7);
            BodySnapshot seven = candidate.BodyItems[0];
            candidate.AddStrip(20);
            BodySnapshot twenty = candidate.BodyItems[0];
            candidate.BodyItems = reverse ? new[] { twenty, seven } : new[] { seven, twenty };
            candidate.Remove(BodyFixture.Body(7, 1, 0));
            candidate.Remove(BodyFixture.Body(20, 1, 0));
            candidate.Put(BodyFixture.Grid(10, 3));
            candidate.Put(BodyFixture.Grid(3, 3));
            candidate.Put(BodyFixture.Grid(1, 8));
            if (reverse) candidate.Keys = candidate.Keys.Reverse().ToArray();
            using BodyExtractionPlan plan = BodyFixture.Success(candidate, Reserve(7));
            Assert.That(plan.Cells.ToArray().Select(cell => cell.Source.Position), Is.EqualTo(new[]
            {
                BodyFixture.Grid(3, 3).Position, BodyFixture.Grid(10, 3).Position, BodyFixture.Grid(1, 8).Position,
                BodyFixture.Body(7, 0, 0).Position, BodyFixture.Body(7, 2, 0).Position,
                BodyFixture.Body(20, 0, 0).Position, BodyFixture.Body(20, 2, 0).Position
            }));
            Assert.That(plan.Cells.ToArray().Select(cell => cell.Target.Position.BodyId),
                Is.EqualTo(new ulong[] { 21, 22, 23, 24, 25, 26, 27 }));
            Assert.That(plan.RetiredBodyIds.ToArray(), Is.EqualTo(new ulong[] { 7, 20 }));
        }

        private sealed class FinalBodyFailure : IFailureInjector
        {
            public WorldResult Check(in TransactionContext context, FailurePoint point) =>
                point == FailurePoint.AfterMaterialPrepared
                    ? WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                        new WorldDiagnostic("M05.AfterMaterialPrepared", "lastBody", "末体准备后测试失败。"))
                    : WorldResult.Success();
        }

        [Test]
        public void FinalBodyFailureCapacityRejectionAndPlanDisposeNeverReturnReservedIds()
        {
            var candidate = new BodyFixture();
            candidate.Put(BodyFixture.Grid(1, 1));
            candidate.Put(BodyFixture.Grid(10, 10));
            WorldResult failed = candidate.Build(out BodyExtractionPlan failedPlan, Reserve(2), new FinalBodyFailure());
            Assert.That(failed.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(failed.Diagnostic.Stage, Is.EqualTo("M05.AfterMaterialPrepared"));
            Assert.That(failedPlan, Is.Null);
            using (BodyExtractionPlan success = BodyFixture.Success(candidate, Reserve(2)))
                Assert.That(success.Bodies.Select(body => body.Snapshot.BodyId), Is.EqualTo(new ulong[] { 3, 4 }));
            var limited = new BodyFixture(maxBodies: 1);
            limited.Put(BodyFixture.Grid(1, 1));
            limited.Put(BodyFixture.Grid(10, 10));
            Assert.That(limited.Build(out BodyExtractionPlan rejected, Reserve(2)).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(rejected, Is.Null);
            Assert.That(Reserve(1), Is.EqualTo(new ulong[] { 7 }));
            Assert.That(candidate.Cells.Count, Is.EqualTo(2));
            Assert.That(candidate.Changes.Count, Is.Zero);
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }
    }
}
