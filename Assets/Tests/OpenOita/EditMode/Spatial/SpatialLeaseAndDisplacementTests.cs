using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Spatial;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Spatial
{
    public sealed class SpatialLeaseAndDisplacementTests
    {
        [Test]
        public void M06_Lease_RevisionAndTickEndInvalidateCurrentStageProvider()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(10, 10, 101) }));
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld state).IsSuccess, Is.True);
            using (state) using (var index = new WorldOccupancyIndex())
            {
                Assert.That(state.BeginTick().IsSuccess, Is.True);
                var context = new TransactionContext(state.Published.Version, state.WorkingTick, TickStage.Water);
                long revision = state.Revision; ulong tick = state.WorkingTick;
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, context, revision, () => state.IsLeaseValid(revision, tick)).IsSuccess, Is.True);
                var source = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 10, 10));
                var target = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 10, 9));
                Assert.That(index.QueryContacts(source, Array.Empty<CellContact>()).Result.IsSuccess, Is.True);
                long reserved = index.ReservedCpuBytes;
                index.Invalidate();
                Assert.That(index.ReservedCpuBytes, Is.EqualTo(reserved), "失效不能把字典保留容量从预算中抹掉。");
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, context, revision, () => state.IsLeaseValid(revision, tick)).IsSuccess, Is.True);
                var prepared = state.PrepareMove(source, target, context); Assert.That(prepared.Result.IsSuccess, Is.True);
                using (prepared.Prepared) Assert.That(prepared.Prepared.Apply(context).IsSuccess, Is.True);
                Assert.That(index.QueryContacts(source, Array.Empty<CellContact>()).Result.ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
                revision = state.Revision;
                long bound = revision;
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, context, bound, () => state.IsLeaseValid(bound, tick)).IsSuccess, Is.True);
                Assert.That(state.PublishState().IsSuccess, Is.True);
                Assert.That(index.Check(1).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            }
        }

        [Test]
        public void SpatialCache_RebindsStageButRebuildsAfterMutationAndInvalidation()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(10, 10, 101) }));
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld state).IsSuccess, Is.True);
            using (state) using (var index = new WorldOccupancyIndex())
            {
                state.BeginTick();
                ulong tick = state.WorkingTick; long revision = state.Revision;
                var water = new TransactionContext(state.Published.Version, tick, TickStage.Water);
                var steam = new TransactionContext(state.Published.Version, tick, TickStage.Steam);
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, water, revision, () => state.IsLeaseValid(revision, tick)).IsSuccess, Is.True);
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, steam, revision, () => state.IsLeaseValid(revision, tick)).IsSuccess, Is.True);
                Assert.That(index.RebuildCount, Is.EqualTo(1));
                Assert.That(index.Lease.Stage, Is.EqualTo(TickStage.Steam));
                var source = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 10, 10));
                var target = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 10, 9));
                var mutation = state.PrepareMove(source, target, water);
                Assert.That(mutation.Result.IsSuccess, Is.True);
                using (mutation.Prepared) Assert.That(mutation.Prepared.Apply(water).IsSuccess, Is.True);
                Assert.That(index.Check(1).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
                long nextRevision = state.Revision;
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, steam, nextRevision, () => state.IsLeaseValid(nextRevision, tick)).IsSuccess, Is.True);
                Assert.That(index.RebuildCount, Is.EqualTo(2));
                Assert.That(index.TryGet(source.Position, out _), Is.False);
                Assert.That(index.TryGet(target.Position, out _), Is.True);
                index.Invalidate();
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, steam, nextRevision, () => state.IsLeaseValid(nextRevision, tick)).IsSuccess, Is.True);
                Assert.That(index.RebuildCount, Is.EqualTo(3));
            }
        }

        [Test]
        public void SpatialCache_InvalidInputClearsPreviouslyValidLease()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(10, 10, 101) }));
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld state).IsSuccess, Is.True);
            using (state) using (var index = new WorldOccupancyIndex())
            {
                state.BeginTick();
                var context = new TransactionContext(state.Published.Version, state.WorkingTick, TickStage.Water);
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, context, state.Revision, () => true).IsSuccess, Is.True);
                Assert.That(index.Refresh(state, loaded.Materials, state.Instances, context, state.Revision, null).IsSuccess, Is.False);
                Assert.That(index.Check(1).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            }
        }

        [Test]
        public void M06_F01_01_CoveredSourcesAreCollectedInStableOrderWithoutMovingState()
        {
            // 纯规划隔离夹具：网格固体也是起止占据，源允许被候选固体覆盖。
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(10, 10, 101), new InitialCell(11, 10, 103) }));
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld state).IsSuccess, Is.True);
            using (state) using (var start = new WorldOccupancyIndex()) using (var end = new WorldOccupancyIndex())
            {
                Assert.That(state.BeginTick().IsSuccess, Is.True);
                var context = new TransactionContext(state.Published.Version, state.WorkingTick, TickStage.Physics);
                Assert.That(start.Refresh(state, loaded.Materials, state.Instances, context, state.Revision, () => true).IsSuccess, Is.True);
                // 候选视图只为几何规划测试，不进入正式Create。
                var candidate = new CoveredFluids(state, loaded.Materials);
                Assert.That(end.Refresh(candidate, loaded.Materials, candidate.Instances, context, state.Revision, () => true).IsSuccess, Is.True);
                var planner = new FluidDisplacementPlanner();
                Assert.That(planner.CollectCovered(state, loaded.Materials, end, out CellKey[] sources).IsSuccess, Is.True);
                Assert.That(sources.Length, Is.EqualTo(2));
                for (int i = 0; i < 2; i++)
                {
                    Assert.That(sources[i].Position.X, Is.EqualTo(10 + i)); Assert.That(sources[i].Position.Y, Is.EqualTo(10));
                    Assert.That(state.Instances.TryGetInstance(sources[i], out _), Is.True);
                    Assert.That(state.Read(sources[i], out CellSnapshot original).IsSuccess, Is.True);
                    Assert.That(original.MaterialId, Is.EqualTo(i == 0 ? 101 : 103));
                }
            }
        }

        [Test]
        public void M06_F01_01_CaptureDoesNotRequireImmediateEmptyTargets()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(10, 10, 101), new InitialCell(11, 10, 103),
                new InitialCell(10, 9, 102), new InitialCell(11, 9, 102) }));
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld state).IsSuccess, Is.True);
            using (state) using (var start = new WorldOccupancyIndex()) using (var end = new WorldOccupancyIndex())
            {
                Assert.That(state.BeginTick().IsSuccess, Is.True);
                var context = new TransactionContext(state.Published.Version, state.WorkingTick, TickStage.Physics);
                Assert.That(start.Refresh(state, loaded.Materials, state.Instances, context, state.Revision, () => true).IsSuccess, Is.True);
                var candidate = new CoveredFluids(state, loaded.Materials);
                Assert.That(end.Refresh(candidate, loaded.Materials, candidate.Instances, context, state.Revision, () => true).IsSuccess, Is.True);
                Assert.That(new FluidDisplacementPlanner().CollectCovered(state, loaded.Materials, end, out CellKey[] sources).IsSuccess, Is.True);
                Assert.That(sources.Length, Is.EqualTo(2));
                Assert.That(state.MaterialCells, Is.EqualTo(4));
            }
        }

        private sealed class CoveredFluids : IWorkingWorldView
        {
            private readonly WorkingWorld _source;
            private readonly CellKey[] _keys;
            private readonly BodySnapshot[] _bodies;
            internal TickInstanceMap Instances;
            internal CoveredFluids(WorkingWorld source, IMaterialRuntimeTable materials)
            {
                _source = source;
                _keys = source.OccupiedCells.ToArray();
                _bodies = new[] { new BodySnapshot(1, new BodyPose(new Vector2(1, 1), 0), default, new Vector2(0.1f, 0.05f), 1) };
                var keys = new System.Collections.Generic.List<CellKey>(_keys);
                keys.Add(new CellKey(1, new CellPositionKey(OwnerKind.Body, 1, 0, 0))); keys.Add(new CellKey(1, new CellPositionKey(OwnerKind.Body, 1, 1, 0))); _keys = keys.ToArray();
                Instances = new TickInstanceMap(1, source.WorkingTick); foreach (CellKey key in _keys) Instances.Create(key);
            }
            public WorldConfig Config => _source.Config;
            public Vector2 Origin => _source.Origin;
            public ulong Generation => _source.Generation;
            public ulong WorkingTick => _source.WorkingTick;
            public WorldResult Read(in CellKey key, out CellSnapshot state)
            {
                if (key.Position.OwnerKind == OwnerKind.Body) { state = new CellSnapshot(102); return WorldResult.Success(); }
                return _source.Read(key, out state);
            }
            public bool IsFixed(in CellKey key) => false;
            public ReadOnlySpan<CellKey> OccupiedCells => _keys;
            public ReadOnlySpan<BodySnapshot> Bodies => _bodies;
        }
    }
}
