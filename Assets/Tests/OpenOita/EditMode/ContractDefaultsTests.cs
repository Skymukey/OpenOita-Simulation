using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode
{
    public sealed class ContractDefaultsTests
    {
        [Test]
        public void M00_02_ResultDefaultsAreNotSuccessAndErrorSetIsComplete()
        {
            Assert.That(default(WorldResult).IsSuccess, Is.False);
            Assert.That(Enum.GetValues(typeof(WorldErrorCode)).Length, Is.EqualTo(19));
            Assert.That(Marshal.SizeOf<CellSnapshot>(), Is.LessThanOrEqualTo(32));
            Assert.That(typeof(CellSnapshot).GetFields().All(f => f.IsInitOnly), Is.True);
            Assert.Throws<ArgumentException>(() => WorldResult.Failure(WorldErrorCode.None, default));
            Assert.Throws<ArgumentException>(() => new WorldCreateResult(WorldResult.Success()));
            Assert.Throws<ArgumentException>(() => new WorldLoadResult(WorldResult.Success()));
            Assert.Throws<ArgumentException>(() => new StructurePlanResult(WorldResult.Success()));
            Assert.That((ulong)RuleMask.Burnable, Is.EqualTo(1UL << ((int)RuleId.Burnable - 1)));
        }
        [Test]
        public void M00_05_D01_InstanceHandlesCarryTickAndGenerationAndReplacementIdentity()
        {
            var old = new CellInstanceHandle(1, 3, 1);
            var replacement = new CellInstanceHandle(1, 3, 2);
            Assert.That(old.Equals(replacement), Is.False);
            Assert.That(old.Equals(new CellInstanceHandle(1, 4, 1)), Is.False);
            Assert.That(old.Equals(new CellInstanceHandle(2, 3, 1)), Is.False);
            Assert.That(typeof(CellSnapshot).GetFields().Any(f => f.FieldType == typeof(CellInstanceHandle)), Is.False);
        }
        [Test]
        public void M00_05_D02_PositionKeysDeduplicateWritesAndDistinguishOwnership()
        {
            var grid = new CellPositionKey(OwnerKind.Grid, 0, 10, 10);
            var body = new CellPositionKey(OwnerKind.Body, 1, 10, 10);
            var writes = new HashSet<CellPositionKey> { grid, grid, body };
            Assert.That(writes.Count, Is.EqualTo(2));
            Assert.That(new CellKey(1, grid).Equals(new CellKey(2, grid)), Is.False);
            Assert.Throws<ArgumentException>(() => new CellPositionKey(OwnerKind.Grid, 1, 0, 0));
            Assert.Throws<ArgumentException>(() => new CellPositionKey(OwnerKind.Body, 0, 0, 0));
        }
        [Test]
        public void M00_05_D03_OriginalOwnershipThenYXDeterminesBodyOrderAndIdsDoNotWrap()
        {
            var keys = new[]
            {
                new CellPositionKey(OwnerKind.Body, 2, 0, 0), new CellPositionKey(OwnerKind.Grid, 0, 4, 5),
                new CellPositionKey(OwnerKind.Body, 1, 9, 1), new CellPositionKey(OwnerKind.Grid, 0, 4, 3),
                new CellPositionKey(OwnerKind.Grid, 0, 3, 3)
            };
            Array.Sort(keys);
            Assert.That(keys.Select(k => k.BodyId), Is.EqualTo(new ulong[] { 0, 0, 0, 1, 2 }));
            Assert.That(keys.Take(3).Select(k => new Vector2Int(k.X, k.Y)), Is.EqualTo(new[] { new Vector2Int(3, 3), new Vector2Int(4, 3), new Vector2Int(4, 5) }));
            Assert.That(ContractDefaults.RetainBodyId(OwnerKind.Grid, 1), Is.False);
            Assert.That(ContractDefaults.RetainBodyId(OwnerKind.Body, 1), Is.True);
            Assert.That(ContractDefaults.RetainBodyId(OwnerKind.Body, 2), Is.False);
            var sequence = new IdentitySequence(ulong.MaxValue - 1);
            Assert.That(sequence.Reserve(out ulong last).IsSuccess, Is.True);
            Assert.That(last, Is.EqualTo(ulong.MaxValue));
            Assert.That(sequence.Reserve(out ulong exhausted).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(exhausted, Is.Zero);
            Assert.That(ContractDefaults.Advance(ulong.MaxValue, out ulong next).IsSuccess, Is.False);
            Assert.That(next, Is.EqualTo(ulong.MaxValue));
        }
        [Test]
        public void M00_05_D04_ParityDoesNotOverflowAndFiveTiersStaySeparate()
        {
            for (ulong tick = 0; tick < 4; tick++) for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++)
                Assert.That(ContractDefaults.PreferLeft(tick, x, y, 1), Is.EqualTo(((tick + (ulong)x + (ulong)y + 1) & 1) == 0));
            Assert.That(ContractDefaults.PreferLeft(ulong.MaxValue, int.MaxValue, int.MaxValue, uint.MaxValue), Is.True);
            Assert.That(ContractDefaults.PreferLeft(1, 5, 5, 1), Is.Not.EqualTo(ContractDefaults.PreferLeft(1, 5, 5, 2)));
            Assert.That(Enum.GetValues(typeof(MoveCandidateTier)).Cast<MoveCandidateTier>().Select(t => (int)t), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
            var source = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 5, 5));
            var preferred = new MutationIntent(MutationKind.Move, default, source,
                new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 6, 4)), default, MoveCandidateTier.PreferredDiagonal);
            var other = new MutationIntent(MutationKind.Move, default, source,
                new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 4, 4)), default, MoveCandidateTier.OtherDiagonal);
            Assert.That(ContractDefaults.CompareMoveCandidates(preferred, other), Is.LessThan(0), "层级优先于目标x，保留seed左右偏好。");
        }
        [Test]
        public void M00_05_D05_QueryBoundariesAndNonFiniteInputs()
        {
            var world = new WorldRect(Vector2.zero, Vector2.one);
            Assert.That(ContractDefaults.ValidatePoint(Vector2.zero, world).IsSuccess, Is.True);
            Assert.That(ContractDefaults.ValidatePoint(Vector2.one, world).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(ContractDefaults.ValidatePoint(new Vector2(float.NaN, 0), world).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(ContractDefaults.ValidateSegment(Vector2.one, Vector2.one, world).IsSuccess, Is.True);
            Assert.That(ContractDefaults.ValidateSegment(new Vector2(-0.01f, 0), Vector2.one, world).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(ContractDefaults.ValidateSegment(new Vector2(float.PositiveInfinity, 0), Vector2.zero, world).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(ContractDefaults.ValidateRegion(world, world).IsSuccess, Is.True);
            var zero = new WorldRect(Vector2.zero, Vector2.zero);
            Assert.That(ContractDefaults.ValidateRegion(zero, world).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            var outside = new WorldRect(new Vector2(-0.01f, 0), Vector2.one);
            Assert.That(ContractDefaults.ValidateRegion(outside, world).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            var small = WorldResult.Failure(WorldErrorCode.BufferTooSmall, default);
            var result = new QueryResult(small, new WorldVersion(1, 0), 2, 0);
            Assert.That(result.RequiredCount, Is.EqualTo(2));
            Assert.Throws<ArgumentException>(() => new QueryResult(small, default, 2, 1));
        }
        [Test]
        public void M00_05_D06_SegmentSortUsesFirstTThenStableIdentity()
        {
            var grid = new CellHit(new WorldVersion(1, 1), new CellPositionKey(OwnerKind.Grid, 0, 9, 9), new CellSnapshot(101));
            var body = new CellHit(new WorldVersion(1, 1), new CellPositionKey(OwnerKind.Body, 1, 0, 0), new CellSnapshot(104));
            Assert.That(ContractDefaults.CompareSegmentHits(new SegmentCellHit(grid, 0.5), new SegmentCellHit(body, 0.5)), Is.LessThan(0));
            Assert.That(ContractDefaults.CompareSegmentHits(new SegmentCellHit(body, 0.1), new SegmentCellHit(grid, 0.5)), Is.LessThan(0));
        }
        [Test]
        public void M00_05_D07_StaticShapesAreNotLimitedPerBodyButAllMaterialShapesCount()
        {
            var limits = new WorldLimits(100, 10, 2, 10, 100, 5, 180, 8, 16);
            Assert.That(ContractDefaults.CheckShapes(8, new[] { 2 }, limits).IsSuccess, Is.True);
            Assert.That(ContractDefaults.CheckShapes(9, new[] { 2 }, limits).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(ContractDefaults.CheckShapes(0, new[] { 3 }, limits).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(ContractDefaults.BoundaryShapes, Is.EqualTo(4));
        }
        [Test]
        public void M00_05_D08_SubstepsIncludeGravityAndAngularEdgeSpeedAndRejectOverBudget()
        {
            var config = FixtureCatalog.Config();
            Assert.That(ContractDefaults.EstimateSubsteps(Vector2.zero, 0, 0, config, out int idle).IsSuccess, Is.True);
            Assert.That(idle, Is.EqualTo(1));
            Assert.That(ContractDefaults.EstimateSubsteps(new Vector2(5, 0), 0, 0, config, out int linear).IsSuccess, Is.True);
            Assert.That(linear, Is.EqualTo(3));
            Assert.That(ContractDefaults.EstimateSubsteps(Vector2.zero, 1, 5, config, out int angular).IsSuccess, Is.True);
            Assert.That(angular, Is.EqualTo(3));
            Assert.That(ContractDefaults.EstimateSubsteps(Vector2.zero, 1, 100, config, out int tooMany).ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(tooMany, Is.Zero);
            Assert.That(ContractDefaults.EstimateSubsteps(new Vector2(6, 0), 0, 0, config, out _).ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(ContractDefaults.EstimateSubsteps(Vector2.zero, (float)Math.PI, 0.1f, config, out _).IsSuccess, Is.True);
        }
        [Test]
        public void M00_05_D09_TokensCannotCrossWorldAndOldGenerationsOrFabricatedSequencesFail()
        {
            object owner = new object(), otherWorld = new object();
            var token = new CommandToken(owner, 1, 1);
            Assert.That(CommandTokenValidation.Validate(owner, 1, 1, token).IsSuccess, Is.True);
            Assert.That(CommandTokenValidation.Validate(otherWorld, 1, 1, token).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
            Assert.That(CommandTokenValidation.Validate(owner, 2, 1, token).ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
            Assert.That(CommandTokenValidation.Validate(owner, 1, 0, token).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
            Assert.That(CommandTokenValidation.Validate(owner, 1, 1, default).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
        }
        [Test]
        public void M00_05_D10_SameIdReplacementHasDistinctInstanceAndChangeKind()
        {
            var before = new CellInstanceHandle(1, 1, 1);
            var after = new CellInstanceHandle(1, 1, 2);
            Assert.That(before.Equals(after), Is.False);
            Assert.That(Enum.IsDefined(typeof(CellChangeKind), CellChangeKind.Replaced), Is.True);
            var fixture = FixtureCatalog.Transactions();
            Assert.That(fixture.Commands[1].Command.Operation, Is.EqualTo(MaterialOperation.Replace));
            Assert.That(fixture.Commands[1].Command.MaterialId, Is.EqualTo(fixture.Scene.Cells[0].MaterialId));
        }
        [Test]
        public void M00_05_FailureInjectionCarriesStageAndFiresOnlyOnce()
        {
            var injector = new FailOnceInjector(FailurePoint.BeforePublish);
            var context = new TransactionContext(new WorldVersion(1, 0), 1, TickStage.Publish);
            Assert.That(injector.Check(context, FailurePoint.BeforeApply).IsSuccess, Is.True);
            var failed = injector.Check(context, FailurePoint.BeforePublish);
            Assert.That(failed.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(failed.Diagnostic.Stage, Is.EqualTo("Publish"));
            Assert.That(injector.Check(context, FailurePoint.BeforePublish).IsSuccess, Is.True);
        }
    }
}
