using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Spatial;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Spatial
{
    public sealed class FluidSuspensionTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private readonly WorldOccupancyIndex _index = new();
        private CellKey Grid(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private TransactionContext Context(TickStage stage = TickStage.Physics) => new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True, result.ErrorCode + " / " + result.Diagnostic.Message);

        private void Create(IEnumerable<InitialCell> cells, int radius = 1, int maxChanges = 65536)
        {
            _loaded = new WorldSourceLoader().Load(M06Sources.Create(cells, radius: radius, maxChanges: maxChanges));
            Success(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world));
            Success(_world.BeginTick());
        }
        [TearDown] public void Dispose() { _world?.Dispose(); _world = null; _index.Invalidate(); }
        [OneTimeTearDown] public void DisposeIndex() => _index.Dispose();

        private SuspendedFluidSnapshot Suspend(int x, int y, ulong id)
        {
            CellKey source = Grid(x, y);
            Success(_world.Read(source, out CellSnapshot state));
            Assert.That(_world.Instances.TryGetInstance(source, out CellInstanceHandle instance), Is.True);
            var record = new SuspendedFluidSnapshot(1, id, new Vector2Int(x, y), _world.WorkingTick, state);
            var move = new MutationIntent(MutationKind.Move, instance, source, record.Key, state);
            var prepared = _world.PrepareFluidMoves(new[] { move }, Context(), new[] { record });
            Success(prepared.Result);
            using (prepared.Prepared) Success(prepared.Prepared.Apply(Context()));
            Assert.That(_world.Instances.TryGetInstance(record.Key, out CellInstanceHandle transferred), Is.True);
            Assert.That(transferred, Is.EqualTo(instance));
            return record;
        }
        private void NextTick() { Success(_world.PublishState()); Success(_world.BeginTick()); }
        private void Write(CellKey key, CellSnapshot state)
        {
            var context = Context(TickStage.Commands);
            var prepared = _world.Prepare(new[] { new CellWrite(key, state, state.MaterialId != 0) }, context);
            Success(prepared.Result);
            using (prepared.Prepared) Success(prepared.Prepared.Apply(context));
        }
        private MutationIntent[] Plan(bool dynamicCover = false, int originX = 10, int originY = 10)
        {
            IWorkingWorldView view = dynamicCover ? new Overlay(_world, originX, originY) : _world;
            ITickInstanceMap instances = view is Overlay overlay ? overlay.Instances : _world.Instances;
            long revision = _world.Revision; ulong tick = _world.WorkingTick;
            Success(_index.Refresh(view, _loaded.Materials, instances, Context(), revision, () => _world.IsLeaseValid(revision, tick)));
            Success(new FluidDisplacementPlanner().PlanRestoration(_world, _world.Instances, _index, out MutationIntent[] moves));
            return moves;
        }

        [Test] public void F01_02_NewSuspensionWaitsOneTickThenRestoresOriginWithSameInstance()
        {
            Create(new[] { new InitialCell(10, 10, 101) });
            SuspendedFluidSnapshot record = Suspend(10, 10, 9);
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.OccupiedCells.Length, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            Assert.That(Plan(), Is.Empty);
            NextTick();
            MutationIntent[] moves = Plan();
            Assert.That(moves.Length, Is.EqualTo(1)); Assert.That(moves[0].Target, Is.EqualTo(Grid(10, 10)));
            Assert.That(moves[0].State, Is.EqualTo(record.State));
            var prepared = _world.PrepareFluidMoves(moves, Context()); Success(prepared.Result);
            using (prepared.Prepared) Success(prepared.Prepared.Apply(Context()));
            Assert.That(_world.SuspendedFluids.Length, Is.Zero);
            Assert.That(_world.Instances.TryGetInstance(Grid(10, 10), out CellInstanceHandle instance), Is.True);
            Assert.That(instance, Is.EqualTo(moves[0].Instance));
            Assert.That(_world.MaterialCells, Is.EqualTo(1)); Assert.That(_world.ChangedPositions, Is.EqualTo(2));
        }

        [Test] public void F01_03_EqualDistanceTargetsUseYThenXAndFinalDynamicOccupation()
        {
            Create(new[] { new InitialCell(10, 10, 101) }); Suspend(10, 10, 1); NextTick();
            MutationIntent[] moves = Plan(true);
            Assert.That(moves.Length, Is.EqualTo(1)); Assert.That(moves[0].Target, Is.EqualTo(Grid(10, 9)));
        }

        [Test] public void F01_03_MultipleRecordsCanShareAnchorAndRestoreOnlyAvailablePart()
        {
            var cells = new List<InitialCell> { new InitialCell(10, 10, 101), new InitialCell(9, 10, 102), new InitialCell(11, 10, 102), new InitialCell(10, 11, 102) };
            Create(cells);
            Suspend(10, 10, 7);
            Write(Grid(10, 10), new CellSnapshot(101));
            Suspend(10, 10, 2);
            NextTick();
            Assert.That(_world.SuspendedFluids[0].RecordId, Is.EqualTo(2UL));
            MutationIntent[] moves = Plan(true);
            Assert.That(moves.Length, Is.EqualTo(1)); Assert.That(moves[0].Source.Position.BodyId, Is.EqualTo(2UL));
            Assert.That(moves[0].Target, Is.EqualTo(Grid(10, 9)));
            var prepared = _world.PrepareFluidMoves(moves, Context()); Success(prepared.Result);
            using (prepared.Prepared) Success(prepared.Prepared.Apply(Context()));
            Assert.That(_world.SuspendedFluids.Length, Is.EqualTo(1)); Assert.That(_world.SuspendedFluids[0].RecordId, Is.EqualTo(7UL));
            Assert.That(_world.MaterialCells, Is.EqualTo(5));
        }

        [Test] public void F01_04_StaticOriginBlocksSearchEvenWhenNearbySpaceExists()
        {
            Create(new[] { new InitialCell(10, 10, 101) }, radius: 16); Suspend(10, 10, 1); NextTick();
            Write(Grid(10, 10), new CellSnapshot(102));
            Assert.That(Plan(), Is.Empty); Assert.That(_world.SuspendedFluids.Length, Is.EqualTo(1));
        }

        [Test] public void F01_04_CannotCrossWallsThenRecoversWhenFourNeighborPassageOpens()
        {
            Create(new[] { new InitialCell(10, 10, 101), new InitialCell(10, 9, 102), new InitialCell(9, 10, 102),
                new InitialCell(11, 10, 102), new InitialCell(10, 11, 102) }, radius: 16);
            Suspend(10, 10, 1); NextTick();
            Assert.That(Plan(true), Is.Empty);
            Write(Grid(10, 9), default);
            Assert.That(Plan(true)[0].Target, Is.EqualTo(Grid(10, 9)));
        }

        [Test] public void F01_04_RadiusLimitsPathAndDoesNotOverwriteActiveFluids()
        {
            Create(new[] { new InitialCell(10, 10, 101), new InitialCell(10, 9, 101), new InitialCell(9, 10, 101),
                new InitialCell(11, 10, 101), new InitialCell(10, 11, 101) });
            Suspend(10, 10, 1); NextTick();
            Assert.That(Plan(true), Is.Empty);
            Assert.That(_world.MaterialCells, Is.EqualTo(5));
            Write(Grid(10, 9), default);
            Assert.That(Plan(true)[0].Target, Is.EqualTo(Grid(10, 9)));
        }

        [Test] public void F01_04_WorldCornerSearchNeverLeavesBoundsOrMovesDiagonally()
        {
            Create(new[] { new InitialCell(0, 0, 101), new InitialCell(1, 0, 102), new InitialCell(0, 1, 102) }, radius: 16);
            Suspend(0, 0, 1); NextTick();
            Assert.That(Plan(true, 0, 0), Is.Empty);
        }

        [TestCase(1U)] [TestCase(2U)] public void F01_05_HiddenSteamLifetimeTicksOnceAndMoveCountdownPauses(uint lifetime)
        {
            Create(new[] { new InitialCell(10, 10, 103) });
            Write(Grid(10, 10), new CellSnapshot(103, lifetimeTicksRemaining: lifetime, moveCountdown: 2));
            SuspendedFluidSnapshot record = Suspend(10, 10, 1); NextTick();
            var rule = new GasDriftRule(1);
            IRuleBatch batch = rule.Execute(_world, _loaded.Materials, _world.Instances, null, Context(TickStage.Steam));
            Success(batch.Result); Assert.That(batch.Intents.Length, Is.EqualTo(1));
            MutationIntent intent = batch.Intents[0];
            Assert.That(intent.Kind, Is.EqualTo(lifetime == 1 ? MutationKind.Remove : MutationKind.WriteState));
            if (lifetime > 1) { Assert.That(intent.State.LifetimeTicksRemaining, Is.EqualTo(1)); Assert.That(intent.State.MoveCountdown, Is.EqualTo(2)); }
            var prepared = _world.Prepare(new[] { new CellWrite(intent.Source, intent.State) }, Context(TickStage.Steam)); Success(prepared.Result);
            using (prepared.Prepared) Success(prepared.Prepared.Apply(Context(TickStage.Steam)));
            Assert.That(_world.SuspendedFluids.Length, Is.EqualTo(lifetime == 1 ? 0 : 1));
            MutationIntent[] restored = Plan();
            Assert.That(restored.Length, Is.EqualTo(lifetime == 1 ? 0 : 1));
            if (restored.Length != 0) Assert.That(restored[0].State.LifetimeTicksRemaining, Is.EqualTo(1));
            Assert.That(record.State.MoveCountdown, Is.EqualTo(2));
        }

        [Test] public void F01_07_RestorationBudgetFailureDoesNotAdoptPartialTargetsOrLoseRecords()
        {
            Create(new[] { new InitialCell(10, 10, 101), new InitialCell(20, 10, 101) }, maxChanges: 3);
            Suspend(10, 10, 1); NextTick(); Suspend(20, 10, 2); NextTick();
            MutationIntent[] moves = Plan(); Assert.That(moves.Length, Is.EqualTo(2));
            var rejected = _world.PrepareFluidMoves(moves, Context());
            Assert.That(rejected.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(_world.SuspendedFluids.Length, Is.EqualTo(2)); Assert.That(_world.OccupiedCells.Length, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.Zero); Assert.That(_world.MaterialCells, Is.EqualTo(2));
        }

        [Test] public void F01_08_CommittedSnapshotCopiesHiddenStateAndCountsThenExpiresItsLease()
        {
            Create(new[] { new InitialCell(10, 10, 101), new InitialCell(20, 10, 103) });
            Suspend(10, 10, 1); Suspend(20, 10, 2); Success(_world.PublishState());
            var old = _world.Published;
            MaterialCountsResult saved = old.MaterialCounts;
            Assert.That(saved.TotalCells, Is.EqualTo(2)); Assert.That(saved.ActiveCells, Is.Zero);
            Assert.That(saved.SuspendedWaterCells, Is.EqualTo(1)); Assert.That(saved.SuspendedSteamCells, Is.EqualTo(1));
            Assert.That(saved.Counts.Length, Is.EqualTo(2));
            Success(_world.BeginTick());
            var prepared = _world.PrepareFluidMoves(Plan(), Context()); Success(prepared.Result);
            using (prepared.Prepared) Success(prepared.Prepared.Apply(Context()));
            Assert.That(old.SuspendedFluids.Length, Is.EqualTo(2));
            Success(_world.PublishState());
            Assert.Throws<ObjectDisposedException>(() => { _ = old.SuspendedFluids.Length; });
            Assert.That(saved.SuspendedCells, Is.EqualTo(2)); Assert.That(saved.TotalCells, Is.EqualTo(2));
            Assert.That(_world.Published.MaterialCounts.ActiveCells, Is.EqualTo(2));
        }

        [Test] public void F01_01_ExactSharedEdgeAndPureTouchDoNotCaptureFluid()
        {
            Create(new[] { new InitialCell(10, 10, 101) });
            var overlay = new Overlay(_world, 11, 10);
            Success(_index.Refresh(overlay, _loaded.Materials, overlay.Instances, Context(), _world.Revision, () => true));
            Success(new FluidDisplacementPlanner().CollectCovered(_world, _loaded.Materials, _index, out CellKey[] sources));
            Assert.That(sources, Is.Empty);
        }

        private sealed class Overlay : IWorkingWorldView
        {
            private readonly WorkingWorld _source;
            private readonly CellKey[] _keys;
            private readonly BodySnapshot[] _bodies;
            internal readonly TickInstanceMap Instances;
            internal Overlay(WorkingWorld source, int x, int y)
            {
                _source = source;
                var keys = new List<CellKey>(source.OccupiedCells.ToArray());
                var key = new CellKey(source.Generation, new CellPositionKey(OwnerKind.Body, 99, 0, 0));
                keys.Add(key); _keys = keys.ToArray();
                _bodies = new[] { new BodySnapshot(99, new BodyPose(new Vector2(x * source.Config.CellSize, y * source.Config.CellSize), 0), default, Vector2.zero, 1) };
                Instances = source.Instances.Clone(); Instances.Create(key);
            }
            public WorldConfig Config => _source.Config;
            public Vector2 Origin => _source.Origin;
            public ulong Generation => _source.Generation;
            public ulong WorkingTick => _source.WorkingTick;
            public ReadOnlySpan<CellKey> OccupiedCells => _keys;
            public ReadOnlySpan<BodySnapshot> Bodies => _bodies;
            public bool IsFixed(in CellKey key) => _source.IsFixed(key);
            public WorldResult Read(in CellKey key, out CellSnapshot cell)
            {
                if (key.Position.OwnerKind == OwnerKind.Body) { cell = new CellSnapshot(102); return WorldResult.Success(); }
                return _source.Read(key, out cell);
            }
        }
    }
}
