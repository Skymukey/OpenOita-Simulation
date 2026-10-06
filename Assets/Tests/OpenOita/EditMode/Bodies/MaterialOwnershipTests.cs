using System;
using System.Collections.Generic;
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
    public sealed class MaterialOwnershipTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private ConnectivityAnalyzer _structure;
        private static CellKey Grid(int x, int y = 10) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private static CellKey Body(ulong id, int x = 0, int y = 0) => new CellKey(1, new CellPositionKey(OwnerKind.Body, id, x, y));
        private TransactionContext Context(TickStage stage = TickStage.Commands) => new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        [TearDown]
        public void TearDown() { _world?.Dispose(); }

        private void Create(InitialCell[] cells = null, Vector2Int[] fixedCells = null, int maxChanges = 65536, int maxBodies = 64)
        {
            _world?.Dispose();
            WorldLoadResult baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(baseline.Result.IsSuccess, Is.True);
            var config = new WorldConfig(1, 256, 256, 128, 0.1f, 0.02f, -9.81f, 1,
                new WorldLimits(65536, maxBodies, 256, 4096, maxChanges, 5, 180, 8, 16));
            _loaded = new WorldLoadResult(WorldResult.Success(), config, FixtureCatalog.Scene(cells ?? Array.Empty<InitialCell>(), fixedCells), baseline.Materials, baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world).IsSuccess, Is.True);
            _structure = new ConnectivityAnalyzer(65536, 64);
        }

        private IPreparedMaterialMutation Full(IPreparedMutation edit, TransactionContext context, IMaterialMutationPreparer preparer = null)
        {
            var structure = _world.PlanStructure(edit, _structure, context);
            Assert.That(structure.Result.IsSuccess, Is.True, structure.Result.Diagnostic.Message);
            var result = _world.PrepareMaterial(edit, structure.Plan, preparer ?? new MaterialMutationPreparer(_loaded.Materials), context);
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            Assert.That(result.Prepared, Is.SameAs(edit));
            return result.Prepared;
        }

        private void ApplyAndPublish(IPreparedMaterialMutation prepared, TransactionContext context)
        {
            using var coordinator = new TransactionCoordinator();
            coordinator.Own(prepared);
            Assert.That(coordinator.ValidateAndApply(context, _world.Config.Limits, ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            coordinator.MarkCommitted(_world.Published.Version);
        }

        private void InitializeBodies(IMaterialMutationPreparer preparer = null)
        {
            var result = _world.PrepareInitial();
            Assert.That(result.Result.IsSuccess, Is.True);
            var context = Context(TickStage.Structure);
            ApplyAndPublish(Full(result.Prepared, context, preparer), context);
        }

        [Test]
        public void SpawnUsesCandidateInstanceAndAtomicTransferCountsBothOwners()
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            var edit = _world.PrepareReplace(Grid(5), 104, Context()).Prepared;
            ITickInstanceMap lease = edit.CandidateInstances;
            Assert.That(lease.TryGetInstance(Grid(5), out CellInstanceHandle spawned), Is.True);
            Assert.That(_world.Instances.TryGetInstance(Grid(5), out _), Is.False);
            Assert.That(edit.CandidateWrites.ToArray(), Is.EqualTo(new[] { Grid(5).Position }));
            IPreparedMaterialMutation combined = Full(edit, Context());
            PlannedCell cell = combined.Cells[0];
            Assert.That(cell.Instance, Is.EqualTo(spawned));
            Assert.That(combined.CandidateInstances.TryResolve(spawned, out CellKey target), Is.True);
            Assert.That(target, Is.EqualTo(cell.Target));
            Assert.That(combined.CandidateWorld.Bodies.Length, Is.EqualTo(1));
            Assert.That(combined.Budget.MaterialCells, Is.EqualTo(1));
            Assert.That(combined.Budget.ChangedPositions, Is.EqualTo(2));
            Assert.That(_world.MaterialCells, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Bodies.Length, Is.Zero);
            ApplyAndPublish(combined, Context());
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            Assert.That(_world.Read(Grid(5), out CellSnapshot grid).IsSuccess, Is.True);
            Assert.That(grid.MaterialId, Is.Zero);
            Assert.That(_world.Read(target, out CellSnapshot body).IsSuccess, Is.True);
            Assert.That(body.MaterialId, Is.EqualTo(104));
            Assert.That(_world.Published.Read(target, out CellSnapshot published).IsSuccess, Is.True);
            BodyFixture.StateEquals(body, published);
            Assert.That(lease.TryResolve(spawned, out CellKey expired), Is.False);
            Assert.That(expired, Is.EqualTo(default(CellKey)));
        }

        [TestCase(101)]
        [TestCase(104)]
        public void MoveCandidateAndOwnershipKeepOriginalWetInstance(int materialId)
        {
            Create(new[] { new InitialCell(5, 10, (ushort)materialId) });
            _world.BeginTick();
            TickInstanceMap original = _world.Instances;
            original.TryGetInstance(Grid(5), out CellInstanceHandle instance);
            original.MarkWet(instance);
            var edit = _world.PrepareMove(Grid(5), Grid(6), Context()).Prepared;
            Assert.That(edit.CandidateInstances.TryGetInstance(Grid(5), out _), Is.False);
            Assert.That(edit.CandidateInstances.TryGetInstance(Grid(6), out CellInstanceHandle moved), Is.True);
            Assert.That(moved, Is.EqualTo(instance));
            Assert.That(edit.CandidateInstances.IsWet(moved), Is.True);
            Assert.That(edit.CandidateWrites.ToArray(), Is.EqualTo(new[] { Grid(5).Position, Grid(6).Position }));
            var combined = Full(edit, Context());
            CellKey target = materialId == 104 ? combined.Cells[0].Target : Grid(6);
            Assert.That(combined.CandidateInstances.TryResolve(instance, out CellKey candidateTarget), Is.True);
            Assert.That(candidateTarget, Is.EqualTo(target));
            Assert.That(original.TryResolve(instance, out CellKey source), Is.True);
            Assert.That(source, Is.EqualTo(Grid(5)));
            Assert.That(combined.Apply(Context()).IsSuccess, Is.True);
            Assert.That(_world.Instances, Is.SameAs(original));
            Assert.That(original.TryResolve(instance, out CellKey applied), Is.True);
            Assert.That(applied, Is.EqualTo(target));
            Assert.That(original.IsWet(instance), Is.True);
            Assert.That(_world.ChangedPositions, Is.EqualTo(materialId == 104 ? 3 : 2));
            Assert.That(_world.Published.Read(Grid(5), out CellSnapshot old).IsSuccess, Is.True);
            Assert.That(old.MaterialId, Is.EqualTo(materialId));
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            combined.MarkCommitted(_world.Published.Version);
            combined.Dispose();
        }

        [Test]
        public void SameIdReplaceUsesNewDryInstanceRevokesFixedAndDeduplicatesWholeTick()
        {
            Create(new[] { new InitialCell(5, 10, 104) }, new[] { new Vector2Int(5, 10) });
            _world.BeginTick();
            _world.Instances.TryGetInstance(Grid(5), out CellInstanceHandle original);
            _world.Instances.MarkWet(original);
            var edit = _world.PrepareReplace(Grid(5), 104, Context()).Prepared;
            Assert.That(edit.CandidateInstances.TryGetInstance(Grid(5), out CellInstanceHandle replacement), Is.True);
            Assert.That(replacement, Is.Not.EqualTo(original));
            Assert.That(edit.CandidateInstances.IsWet(replacement), Is.False);
            Assert.That(edit.CandidateWorld.IsFixed(Grid(5)), Is.False);
            var combined = Full(edit, Context());
            CellKey target = combined.Cells[0].Target;
            Assert.That(combined.Cells[0].Instance, Is.EqualTo(replacement));
            Assert.That(combined.Apply(Context()).IsSuccess, Is.True);
            Assert.That(_world.Instances.TryResolve(original, out _), Is.False);
            Assert.That(_world.Instances.TryGetInstance(target, out CellInstanceHandle adopted), Is.True);
            Assert.That(adopted, Is.EqualTo(replacement));
            Assert.That(_world.IsFixed(Grid(5)), Is.False);
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            _world.Instances.MarkWet(adopted);
            var secondEdit = _world.PrepareReplace(target, 104, Context()).Prepared;
            var second = Full(secondEdit, Context());
            Assert.That(second.Budget.ChangedPositions, Is.EqualTo(2));
            Assert.That(second.Apply(Context()).IsSuccess, Is.True);
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            Assert.That(_world.Instances.TryGetInstance(target, out CellInstanceHandle latest), Is.True);
            Assert.That(latest, Is.Not.EqualTo(adopted));
            Assert.That(_world.Instances.IsWet(latest), Is.False);
            Assert.That(_world.Bodies[0].GeometryVersion, Is.EqualTo(2));
            combined.Dispose();
            second.Dispose();
        }

        [TestCase("Abort")]
        [TestCase("Apply")]
        [TestCase("Fault")]
        [TestCase("Dispose")]
        public void CandidateLeaseIsReadOnlyAndExpiresAtEveryBoundary(string boundary)
        {
            Create(new[] { new InitialCell(5, 10, 101) });
            _world.BeginTick();
            var edit = _world.PrepareMove(Grid(5), Grid(6), Context()).Prepared;
            ITickInstanceMap lease = edit.CandidateInstances;
            lease.TryGetInstance(Grid(6), out CellInstanceHandle instance);
            Assert.Throws<InvalidOperationException>(() => lease.Create(Grid(7)));
            Assert.Throws<InvalidOperationException>(() => lease.Move(instance, Grid(7)));
            Assert.Throws<InvalidOperationException>(() => lease.Invalidate(instance));
            Assert.Throws<InvalidOperationException>(() => lease.MarkWet(instance));
            Assert.That(lease.TryGetInstance(new CellKey(2, Grid(6).Position), out CellInstanceHandle wrong), Is.False);
            Assert.That(wrong, Is.EqualTo(default(CellInstanceHandle)));
            bool crossThread = true;
            var thread = new Thread(() => crossThread = lease.TryGetInstance(Grid(6), out _));
            thread.Start(); thread.Join();
            Assert.That(crossThread, Is.False);
            if (boundary == "Abort") edit.Abort();
            if (boundary == "Apply") Assert.That(edit.Apply(Context()).IsSuccess, Is.True);
            if (boundary == "Fault") _world.Fault();
            if (boundary == "Dispose") _world.Dispose();
            Assert.That(lease.TryResolve(instance, out CellKey expired), Is.False);
            Assert.That(expired, Is.EqualTo(default(CellKey)));
            Assert.That(lease.TryGetInstance(Grid(6), out _), Is.False);
            Assert.That(lease.IsWet(instance), Is.False);
            Assert.Throws<InvalidOperationException>(() => { _ = edit.CandidateWrites.Length; });
            edit.Dispose();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ActualBodySevenEditSplitsOrRetainsWithAllStorageAndInstancesTogether(bool split)
        {
            Create(new[] { new InitialCell(5, 10, 104), new InitialCell(6, 10, 104), new InitialCell(7, 10, 104) });
            var discarded = new ulong[6];
            _world.ReserveBodyIds(6, discarded);
            InitializeBodies();
            Assert.That(_world.Bodies[0].BodyId, Is.EqualTo(7));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Published.Bodies[0].BodyId, Is.EqualTo(7));
            _world.BeginTick();
            _world.Instances.TryGetInstance(Body(7), out CellInstanceHandle wet);
            _world.Instances.MarkWet(wet);
            _world.Instances.TryGetInstance(Body(7, split ? 1 : 2), out CellInstanceHandle removed);
            var edit = _world.Prepare(new[] { new CellWrite(Body(7, split ? 1 : 2), default) }, Context()).Prepared;
            Assert.That(edit.CandidateWorld.Bodies[0].BodyId, Is.EqualTo(7));
            var combined = Full(edit, Context());
            BodyIdMapping[] mappings = combined.BodyMappings.ToArray();
            Assert.That(mappings.Select(mapping => mapping.NewBodyId), Is.EqualTo(split ? new ulong[] { 8, 9 } : new ulong[] { 7 }));
            Assert.That(_world.Bodies[0].BodyId, Is.EqualTo(7));
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Assert.That(_world.ChangedPositions, Is.Zero);
            var expected = split ? Body(8) : Body(7);
            Assert.That(combined.CandidateInstances.IsWet(wet), Is.True);
            Assert.That(combined.CandidateInstances.TryResolve(wet, out CellKey candidate), Is.True);
            Assert.That(candidate, Is.EqualTo(expected));
            Assert.That(combined.Apply(Context()).IsSuccess, Is.True);
            Assert.That(_world.Bodies.ToArray().Select(body => body.BodyId), Is.EqualTo(split ? new ulong[] { 8, 9 } : new ulong[] { 7 }));
            Assert.That(_world.MaterialCells, Is.EqualTo(2));
            Assert.That(_world.ChangedPositions, Is.EqualTo(split ? 5 : 1));
            Assert.That(_world.Instances.TryResolve(removed, out _), Is.False);
            Assert.That(_world.Instances.TryResolve(wet, out CellKey adopted), Is.True);
            Assert.That(adopted, Is.EqualTo(expected));
            Assert.That(_world.Instances.IsWet(wet), Is.True);
            Assert.That(_world.Published.Bodies[0].BodyId, Is.EqualTo(7));
            Assert.That(_world.Published.Read(Body(7, 1), out CellSnapshot previous).IsSuccess, Is.True);
            Assert.That(previous.MaterialId, Is.EqualTo(104));
            _world.PublishState();
            combined.MarkCommitted(_world.Published.Version);
            combined.Dispose();
            Assert.That(_world.Published.Bodies.Length, Is.EqualTo(split ? 2 : 1));
        }

        [Test]
        public void EmptyBodyRetiresAndNewGridBodyDoesNotReuseId()
        {
            Create(new[] { new InitialCell(5, 10, 104) });
            InitializeBodies();
            _world.BeginTick();
            var edit = _world.Prepare(new[] { new CellWrite(Body(1), default) }, Context()).Prepared;
            Assert.That(edit.CandidateWorld.Bodies.Length, Is.EqualTo(1));
            var removed = Full(edit, Context());
            Assert.That(removed.BodyMappings[0].OldBodyId, Is.EqualTo(1));
            Assert.That(removed.BodyMappings[0].NewBodyId, Is.Zero);
            Assert.That(removed.Apply(Context()).IsSuccess, Is.True);
            Assert.That(_world.Bodies.Length, Is.Zero);
            Assert.That(_world.MaterialCells, Is.Zero);
            var spawn = Full(_world.PrepareReplace(Grid(10), 104, Context()).Prepared, Context());
            Assert.That(spawn.Cells[0].Target.Position.BodyId, Is.EqualTo(2));
            Assert.That(spawn.Apply(Context()).IsSuccess, Is.True);
            Assert.That(_world.Bodies[0].BodyId, Is.EqualTo(2));
            Assert.That(_world.ChangedPositions, Is.EqualTo(3));
            removed.Dispose(); spawn.Dispose();
        }

        [Test]
        public void CompletedOwnershipCannotBePreparedOrAnalyzedAgainAndPublishesIndependentCopy()
        {
            Create(new[] { new InitialCell(5, 10, 104) });
            _world.BeginTick();
            var edit = _world.Prepare(ReadOnlySpan<CellWrite>.Empty, Context()).Prepared;
            var structure = _world.PlanStructure(edit, _structure, Context());
            var service = new MaterialMutationPreparer(_loaded.Materials);
            var result = _world.PrepareMaterial(edit, structure.Plan, service, Context());
            Assert.That(result.Result.IsSuccess, Is.True);
            Assert.That(_world.PrepareMaterial(edit, structure.Plan, service, Context()).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            Assert.That(_world.PlanStructure(edit, _structure, Context()).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            Assert.That(edit.State, Is.EqualTo(PreparationState.Prepared));
            ApplyAndPublish(result.Prepared, Context());
            _world.BeginTick();
            CommittedWorldView published = _world.Published;
            var replacement = Full(_world.PrepareReplace(Body(1), 102, Context()).Prepared, Context());
            Assert.That(replacement.Apply(Context()).IsSuccess, Is.True);
            _world.Read(Body(1), out CellSnapshot current);
            published.Read(Body(1), out CellSnapshot previous);
            Assert.That(current.MaterialId, Is.EqualTo(102));
            Assert.That(previous.MaterialId, Is.EqualTo(104));
            Assert.That(published.Bodies[0].GeometryVersion, Is.EqualTo(1));
            Assert.That(_world.Bodies[0].GeometryVersion, Is.EqualTo(2));
            replacement.Dispose();
        }

        private sealed class CapturingPreparer : IMaterialMutationPreparer
        {
            private readonly MaterialMutationPreparer _inner;
            private MaterialOwnershipPreparation _next;
            internal IMaterialBodyStorage[] Bodies;
            internal string Corruption;
            internal bool ReadOnlyCounterConfirmed;
            internal CapturingPreparer(IMaterialRuntimeTable materials) { _inner = new MaterialMutationPreparer(materials); }
            public PreparationResult<IPreparedMaterialMutation> Prepare(IPreparedMutation candidate, StructurePlan structure,
                BodyIdReservation reserveBodyIds, ITickChangeCounter changes, MaterialOwnershipPreparation prepareOwnership,
                in TransactionContext context, IFailureInjector failures)
            {
                try { changes.Record(ReadOnlySpan<CellPositionKey>.Empty); }
                catch (InvalidOperationException) { ReadOnlyCounterConfirmed = true; }
                _next = prepareOwnership;
                return _inner.Prepare(candidate, structure, reserveBodyIds, changes, Capture, context, failures);
            }
            private PreparationResult<IPreparedMaterialMutation> Capture(ReadOnlySpan<PlannedCell> cells, ReadOnlySpan<BodyIdMapping> mappings,
                IReadOnlyList<BodyGeometryPlan> geometry, IReadOnlyList<IMaterialBodyStorage> bodies,
                ReadOnlySpan<ulong> retired, in TransactionContext context)
            {
                Bodies = bodies.ToArray();
                PlannedCell[] changed = cells.ToArray();
                BodyIdMapping[] changedMappings = mappings.ToArray();
                if (Corruption == "Instance") changed[0] = new PlannedCell(changed[0].Source, changed[0].Target, default, changed[0].State);
                if (Corruption == "Coverage") changed = Array.Empty<PlannedCell>();
                if (Corruption == "Mapping") changedMappings[0] = new BodyIdMapping(777, changedMappings[0].NewBodyId);
                if (Corruption == "Retirement") retired = new ulong[] { 777 };
                return _next(changed, changedMappings, geometry, bodies, retired, context);
            }
        }

        private sealed class Failure : IFailureInjector
        {
            private readonly FailurePoint _point;
            internal Failure(FailurePoint point) { _point = point; }
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point != _point ? WorldResult.Success() :
                WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Injected", point.ToString(), "受控失败。"));
        }

        [Test]
        public void AllParticipantsPreflightBeforeApplyAndAbortReleasesOnlyCandidateBodies()
        {
            Create(new[] { new InitialCell(5, 10, 104) });
            _world.BeginTick();
            var capture = new CapturingPreparer(_loaded.Materials);
            var combined = Full(_world.Prepare(ReadOnlySpan<CellWrite>.Empty, Context()).Prepared, Context(), capture);
            Assert.That(capture.ReadOnlyCounterConfirmed, Is.True);
            Assert.That(capture.Bodies[0].IsDisposed, Is.False);
            using (var coordinator = new TransactionCoordinator())
            {
                coordinator.Own(combined);
                WorldResult rejected = coordinator.ValidateAndApply(Context(), _world.Config.Limits, ContractDefaults.CpuBudgetBytes,
                    new Failure(FailurePoint.BeforeApply));
                Assert.That(rejected.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                Assert.That(coordinator.RequiresFault, Is.False);
            }
            Assert.That(capture.Bodies[0].IsDisposed, Is.True);
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.Bodies.Length, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.Zero);
            var retry = Full(_world.Prepare(ReadOnlySpan<CellWrite>.Empty, Context()).Prepared, Context());
            Assert.That(retry.Cells[0].Target.Position.BodyId, Is.EqualTo(2));
            Assert.That(retry.Apply(Context()).IsSuccess, Is.True);
            retry.Dispose();
            Assert.That(_world.Bodies[0].BodyId, Is.EqualTo(2));
        }

        [TestCase("Instance")]
        [TestCase("Coverage")]
        [TestCase("Mapping")]
        [TestCase("Retirement")]
        public void InvalidOwnershipPlanRejectsWithoutChangingEditedOrWorkingState(string corruption)
        {
            Create(new[] { new InitialCell(5, 10, 104) });
            _world.BeginTick();
            var edit = _world.PrepareReplace(Grid(5), 104, Context()).Prepared;
            var structure = _world.PlanStructure(edit, _structure, Context());
            var capture = new CapturingPreparer(_loaded.Materials) { Corruption = corruption };
            var rejected = _world.PrepareMaterial(edit, structure.Plan, capture, Context());
            Assert.That(rejected.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(rejected.Prepared, Is.Null);
            Assert.That(capture.Bodies.All(body => body.IsDisposed), Is.True);
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Bodies.Length, Is.Zero);
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }

        [Test]
        public void JointTransferBudgetRejectsBeforeAnyWriteAndConsumesReservedIds()
        {
            Create(new[] { new InitialCell(5, 10, 104) }, maxChanges: 1);
            _world.BeginTick();
            var edit = _world.PrepareReplace(Grid(5), 104, Context()).Prepared;
            var structure = _world.PlanStructure(edit, _structure, Context());
            var rejected = _world.PrepareMaterial(edit, structure.Plan, new MaterialMutationPreparer(_loaded.Materials), Context());
            Assert.That(rejected.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Bodies.Length, Is.Zero);
            var next = new ulong[1];
            Assert.That(_world.ReserveBodyIds(1, next).IsSuccess, Is.True);
            Assert.That(next[0], Is.EqualTo(2));
        }

        [TestCase(TickStage.Commands, false)]
        [TestCase(TickStage.Burning, true)]
        public void MaterialPrepareFailureRejectsCommandOrFreezesRuleWithOldPublishedView(TickStage stage, bool faulted)
        {
            Create(new[] { new InitialCell(5, 10, 104) });
            _world.BeginTick();
            var context = Context(stage);
            var edit = _world.PrepareReplace(Grid(5), 104, context).Prepared;
            var structure = _world.PlanStructure(edit, _structure, context);
            var result = _world.PrepareMaterial(edit, structure.Plan, new MaterialMutationPreparer(_loaded.Materials), context,
                new Failure(FailurePoint.AfterMaterialPrepared));
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(_world.Read(Grid(5), out _).ErrorCode, Is.EqualTo(faulted ? WorldErrorCode.Faulted : WorldErrorCode.None));
            Assert.That(_world.Published.Read(Grid(5), out CellSnapshot previous).IsSuccess, Is.True);
            Assert.That(previous.MaterialId, Is.EqualTo(104));
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }

        [Test]
        public void PrematureStorageDisposeFailsPreflightAndWorldDisposeOwnsAdoptedStorage()
        {
            Create(new[] { new InitialCell(5, 10, 104) });
            _world.BeginTick();
            var capture = new CapturingPreparer(_loaded.Materials);
            var combined = Full(_world.Prepare(ReadOnlySpan<CellWrite>.Empty, Context()).Prepared, Context(), capture);
            capture.Bodies[0].Dispose();
            Assert.That(combined.Preflight(Context()).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(_world.Bodies.Length, Is.Zero);
            combined.Abort();
            combined = Full(_world.Prepare(ReadOnlySpan<CellWrite>.Empty, Context()).Prepared, Context(), capture);
            Assert.That(combined.Apply(Context()).IsSuccess, Is.True);
            combined.Dispose();
            Assert.That(capture.Bodies[0].IsDisposed, Is.False);
            _world.Dispose(); _world.Dispose();
            Assert.That(capture.Bodies[0].IsDisposed, Is.True);
        }

        [Test]
        public void TwentyInitialOwnershipPublishAndDisposeRoundsReleaseAdoptedStorage()
        {
            for (int round = 0; round < 20; round++)
            {
                Create(new[] { new InitialCell(5, 10, 104), new InitialCell(10, 10, 102) });
                var capture = new CapturingPreparer(_loaded.Materials);
                InitializeBodies(capture);
                Assert.That(_world.MaterialCells, Is.EqualTo(2));
                Assert.That(_world.ChangedPositions, Is.Zero);
                Assert.That(capture.Bodies.All(body => !body.IsDisposed), Is.True);
                _world.Dispose(); _world.Dispose();
                Assert.That(capture.Bodies.All(body => body.IsDisposed), Is.True);
            }
            TestContext.WriteLine("20轮真实M02/M05存储采用、Tick0发布及释放通过；未运行完整IWorld/物理/GPU生命周期。");
        }
    }
}
