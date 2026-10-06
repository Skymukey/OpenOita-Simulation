using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Simulation.Bodies;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Bodies
{
    // 真实材料状态事务验收；物理参与者仅在明确的失败测试中使用桩。
    public sealed class M05StateTransactionAcceptanceTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private FixturePreparer _preparer;
        private readonly ConnectivityAnalyzer _structure = new ConnectivityAnalyzer(65536, 64);
        private static CellKey Grid(int x, int y = 10) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private static CellKey Body(ulong id, int x = 0, int y = 0) => new CellKey(1, new CellPositionKey(OwnerKind.Body, id, x, y));
        private TransactionContext Context(TickStage stage = TickStage.Commands) => new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        [TearDown]
        public void TearDown() => _world?.Dispose();

        private void Create(InitialCell[] cells, Vector2Int[] fixedCells = null, int maxBodies = 64,
            int maxBodyShapes = 256, int maxTotalShapes = 4096, bool rotated = false)
        {
            var baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(baseline.Result.IsSuccess, Is.True);
            var limits = new WorldLimits(65536, maxBodies, maxBodyShapes, maxTotalShapes, 65536, 5, 180, 8, 16);
            var config = new WorldConfig(1, 256, 256, 128, 0.1f, 0.02f, -9.81f, 1, limits);
            _loaded = new WorldLoadResult(WorldResult.Success(), config, FixtureCatalog.Scene(cells, fixedCells), baseline.Materials, baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world).IsSuccess, Is.True);
            _preparer = new FixturePreparer(_loaded.Materials, rotated);
            var initial = _world.PrepareInitial();
            Assert.That(initial.Result.IsSuccess, Is.True);
            Commit(Full(initial.Prepared, Context(TickStage.Structure)), Context(TickStage.Structure));
            Assert.That(_world.ChangedPositions, Is.Zero);
        }

        private IPreparedMaterialMutation Full(IPreparedMutation edit, TransactionContext context)
        {
            var structure = _world.PlanStructure(edit, _structure, context);
            Assert.That(structure.Result.IsSuccess, Is.True, structure.Result.Diagnostic.Message);
            var prepared = _world.PrepareMaterial(edit, structure.Plan, _preparer, context);
            Assert.That(prepared.Result.IsSuccess, Is.True, prepared.Result.Diagnostic.Target + ": " + prepared.Result.Diagnostic.Message);
            Assert.That(prepared.Prepared, Is.SameAs(edit));
            return prepared.Prepared;
        }

        private void Commit(IPreparedMaterialMutation material, TransactionContext context, Action afterApply = null)
        {
            using var coordinator = new TransactionCoordinator();
            coordinator.Own(material);
            Assert.That(coordinator.ValidateAndApply(context, _world.Config.Limits, ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            afterApply?.Invoke();
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            coordinator.MarkCommitted(_world.Published.Version);
        }

        private IPreparedMutation Edit(CellWrite[] writes, TickStage stage = TickStage.Commands)
        {
            var result = _world.Prepare(writes, Context(stage));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            return result.Prepared;
        }

        [TestCase(10)] [TestCase(126)]
        public void M05_T3_01_RealBridgeRemovalTransfersSingleCellAndPublishesOneVersion(int x)
        {
            Create(new[] { new InitialCell(x, 10, 102), new InitialCell(x + 1, 10, 104), new InitialCell(x + 2, 10, 104) },
                new[] { new Vector2Int(x, 10) });
            CommittedWorldView old = _world.Published;
            Assert.That(_world.Bodies.Length, Is.Zero);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Grid(x + 2), out CellInstanceHandle survivor);
            var combined = Full(Edit(new[] { new CellWrite(Grid(x + 1), default) }), Context());
            CellKey target = combined.Cells.ToArray().Single(c => c.Source.Equals(Grid(x + 2))).Target;
            Assert.That(combined.Budget.ChangedPositions, Is.EqualTo(3));
            Assert.That(_world.MaterialCells, Is.EqualTo(3)); Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(old.Version.CommittedTick, Is.Zero);
            Assert.That(old.Read(Grid(x + 1), out CellSnapshot oldBridge).IsSuccess, Is.True); Assert.That(oldBridge.MaterialId, Is.EqualTo(104));
            Commit(combined, Context(), () =>
            {
                Assert.That(_world.Instances.TryResolve(survivor, out CellKey resolved), Is.True); Assert.That(resolved, Is.EqualTo(target));
            });
            Assert.That(_world.MaterialCells, Is.EqualTo(2)); Assert.That(_world.ChangedPositions, Is.EqualTo(3));
            Assert.That(_world.Bodies.Length, Is.EqualTo(1)); Assert.That(_world.IsFixed(Grid(x)), Is.True);
            Assert.That(_world.Read(Grid(x + 2), out CellSnapshot vacated).IsSuccess, Is.True); Assert.That(vacated.MaterialId, Is.Zero);
            Assert.That(_world.Published.Read(target, out CellSnapshot moved).IsSuccess, Is.True); Assert.That(moved.MaterialId, Is.EqualTo(104));
            Assert.That(_world.Instances.TryResolve(survivor, out _), Is.False);
            Assert.That(_world.Published.Version.CommittedTick, Is.EqualTo(1));
            Assert.That(old.Read(Grid(x + 1), out _).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
        }

        private static InitialCell[] Arms() => new[] { new InitialCell(4, 4, 104), new InitialCell(2, 4, 104), new InitialCell(3, 4, 104),
            new InitialCell(5, 4, 104), new InitialCell(6, 4, 104), new InitialCell(4, 2, 104), new InitialCell(4, 3, 104),
            new InitialCell(4, 5, 104), new InitialCell(4, 6, 104) };

        [TestCase(3)] [TestCase(64)]
        public void M05_T4_01_T4_02_RealFourArmTransactionRejectsWholeOrPublishesEveryBody(int limit)
        {
            Create(Arms(), new[] { new Vector2Int(4, 4) }, maxBodies: limit);
            _world.BeginTick();
            var edit = Edit(new[] { new CellWrite(Grid(4, 4), default) });
            var structure = _world.PlanStructure(edit, _structure, Context());
            Assert.That(structure.Result.IsSuccess, Is.True); Assert.That(structure.Plan.Components.Count, Is.EqualTo(4));
            var prepared = _world.PrepareMaterial(edit, structure.Plan, _preparer, Context());
            if (limit == 3)
            {
                Assert.That(prepared.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded)); Assert.That(prepared.Prepared, Is.Null);
                Assert.That(edit.State, Is.EqualTo(PreparationState.Aborted));
                Assert.That(_world.MaterialCells, Is.EqualTo(9)); Assert.That(_world.IsFixed(Grid(4, 4)), Is.True);
                Assert.That(_world.Bodies.Length, Is.Zero); Assert.That(_world.ChangedPositions, Is.Zero);
                Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
                Assert.That(_world.Read(Grid(4, 4), out CellSnapshot retained).IsSuccess, Is.True); Assert.That(retained.MaterialId, Is.EqualTo(104));
                edit.Dispose();
            }
            else
            {
                Assert.That(prepared.Result.IsSuccess, Is.True);
                Assert.That(prepared.Prepared.Budget.ChangedPositions, Is.EqualTo(17));
                Assert.That(prepared.Prepared.BodyMappings.ToArray().Select(m => m.NewBodyId), Is.EqualTo(new ulong[] { 1, 2, 3, 4 }));
                Commit(prepared.Prepared, Context());
                Assert.That(_world.MaterialCells, Is.EqualTo(8)); Assert.That(_world.Bodies.Length, Is.EqualTo(4));
                Assert.That(_preparer.Storages.Select(b => b.OccupiedPositions.Length), Is.EqualTo(new[] { 2, 2, 2, 2 }));
                Assert.That(_world.ChangedPositions, Is.EqualTo(17)); Assert.That(_world.Published.OccupiedCells.Length, Is.EqualTo(8));
                Assert.That(_world.IsFixed(Grid(4, 4)), Is.False);
            }
        }

        [TestCase("Middle")] [TestCase("End")] [TestCase("Replace")]
        public void M05_T5_01_T5_02_RotatedRealStorageKeepsGeometryAndVelocityField(string operation)
        {
            Create(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 104), new InitialCell(12, 10, 104) }, rotated: true);
            IMaterialBodyStorage oldStorage = _preparer.Storages.Single();
            BodySnapshot old = _world.Bodies[0];
            Assert.That(old.Pose.AngleRadians, Is.EqualTo(Math.PI / 6).Within(1e-6));
            Assert.That(old.Motion.AngularVelocityRadians, Is.EqualTo(0.5f));
            _world.BeginTick();
            IPreparedMutation edit = operation == "Replace" ? _world.PrepareReplace(Body(1, 2), 102, Context()).Prepared :
                Edit(new[] { new CellWrite(Body(1, operation == "Middle" ? 1 : 2), default) });
            Assert.That(edit, Is.Not.Null);
            var combined = Full(edit, Context());
            double centerError = 0, velocityError = 0;
            foreach (BodyGeometryPlan geometry in combined.Geometry)
            {
                foreach (PlannedCell cell in geometry.Cells)
                {
                    Vector2 originalCenter = ToWorld(old.Pose, (cell.Source.Position.X + 0.5) * 0.1, (cell.Source.Position.Y + 0.5) * 0.1);
                    Vector2 newCenter = ToWorld(geometry.Pose, (cell.Target.Position.X + 0.5) * 0.1, (cell.Target.Position.Y + 0.5) * 0.1);
                    centerError = Math.Max(centerError, Vector2.Distance(originalCenter, newCenter));
                }
                Vector2 oldCenter = ToWorld(old.Pose, old.LocalCenterOfMass.x, old.LocalCenterOfMass.y);
                Vector2 newCenterOfMass = ToWorld(geometry.Pose, geometry.LocalCenterOfMass.x, geometry.LocalCenterOfMass.y);
                double vx = old.Motion.LinearVelocity.x - 0.5 * (newCenterOfMass.y - oldCenter.y);
                double vy = old.Motion.LinearVelocity.y + 0.5 * (newCenterOfMass.x - oldCenter.x);
                velocityError = Math.Max(velocityError, Math.Max(Math.Abs(vx - geometry.Motion.LinearVelocity.x), Math.Abs(vy - geometry.Motion.LinearVelocity.y)));
                Assert.That(geometry.Motion.AngularVelocityRadians, Is.EqualTo(0.5).Within(1e-4));
                if (operation == "Middle") Assert.That(geometry.Mass, Is.EqualTo(0.6).Within(0.6e-4));
                if (operation == "End") Assert.That(geometry.Mass, Is.EqualTo(1.2).Within(1.2e-4));
                if (operation == "Replace")
                {
                    double mass = 3.6, cx = (0.6 * 0.05 + 0.6 * 0.15 + 2.4 * 0.25) / mass;
                    double inertia = mass * 0.01 / 6 + 0.6 * Math.Pow(0.05 - cx, 2) + 0.6 * Math.Pow(0.15 - cx, 2) + 2.4 * Math.Pow(0.25 - cx, 2);
                    Assert.That(geometry.Mass, Is.EqualTo(mass).Within(mass * 1e-4));
                    Assert.That(geometry.Inertia, Is.EqualTo(inertia).Within(inertia * 1e-4));
                }
                else
                {
                    double inertia = operation == "Middle" ? 0.001 : 0.005;
                    Assert.That(geometry.Inertia, Is.EqualTo(inertia).Within(inertia * 1e-4));
                }
            }
            Assert.That(centerError, Is.LessThanOrEqualTo(1e-5)); Assert.That(velocityError, Is.LessThanOrEqualTo(1e-4));
            Assert.That(oldStorage.IsDisposed, Is.False);
            Commit(combined, Context());
            Assert.That(oldStorage.IsDisposed, Is.True);
            Assert.That(_world.Bodies.ToArray().Select(b => b.BodyId), Is.EqualTo(operation == "Middle" ? new ulong[] { 2, 3 } : new ulong[] { 1 }));
            Assert.That(_world.ChangedPositions, Is.EqualTo(operation == "Middle" ? 5 : 1));
            Assert.That(_world.Published.Version.CommittedTick, Is.EqualTo(1));
            TestContext.WriteLine($"操作={operation}；最大格中心误差={centerError:R}；最大速度分量误差={velocityError:R}；真实材料状态提交，未推进物理。");
        }

        private static Vector2 ToWorld(BodyPose pose, double x, double y) => new Vector2(
            (float)(pose.Position.x + Math.Cos(pose.AngleRadians) * x - Math.Sin(pose.AngleRadians) * y),
            (float)(pose.Position.y + Math.Sin(pose.AngleRadians) * x + Math.Cos(pose.AngleRadians) * y));

        [Test]
        public void M05_T5_03_RingAdoptionAndCommittedStoragePreserveEmptyHole()
        {
            var cells = new List<InitialCell>();
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) if (x != 1 || y != 1) cells.Add(new InitialCell(10 + x, 10 + y, 104));
            Create(cells.ToArray());
            IMaterialBodyStorage body = _preparer.Storages.Single();
            Assert.That(body.Geometry.Rectangles.Count, Is.EqualTo(4));
            Assert.That(body.OccupiedPositions.Length, Is.EqualTo(8));
            Assert.That(body.TryRead(Body(1, 1, 1).Position, out CellSnapshot hole), Is.False); Assert.That(hole.MaterialId, Is.Zero);
            Assert.That(_world.Published.Read(Body(1, 1, 1), out CellSnapshot published).IsSuccess, Is.True); Assert.That(published.MaterialId, Is.Zero);
            Assert.That(_world.Published.OccupiedCells.Length, Is.EqualTo(8));
        }

        [TestCase(false)] [TestCase(true)]
        public void M05_T9_01_RealBurnedOutBridgePreservesSurvivorStateThroughOwnership(bool dynamic)
        {
            Create(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 104), new InitialCell(12, 10, 104) },
                dynamic ? null : new[] { new Vector2Int(10, 10) });
            _world.BeginTick();
            CellKey first = dynamic ? Body(1, 0) : Grid(10), bridge = dynamic ? Body(1, 1) : Grid(11), tip = dynamic ? Body(1, 2) : Grid(12);
            var seed = Full(Edit(new[] { new CellWrite(first, new CellSnapshot(104, 1, 3, 2, 0, 0, 0)),
                new CellWrite(bridge, new CellSnapshot(104, 1, 1, 2, 0, 0, 0)), new CellWrite(tip, new CellSnapshot(104, 1, 3, 2, 0, 0, 0)) }), Context());
            Assert.That(seed.Apply(Context()).IsSuccess, Is.True); seed.Dispose();
            _world.Instances.TryGetInstance(tip, out CellInstanceHandle survivingInstance);
            var context = Context(TickStage.Burning);
            IRuleBatch batch = new BurnRule(128).Execute(_world, _loaded.Materials, _world.Instances, null, context);
            Assert.That(batch.Result.IsSuccess, Is.True, batch.Result.Diagnostic.Message);
            Assert.That(batch.Intents.ToArray().Single(i => i.Source.Equals(bridge)).RemovalReason, Is.EqualTo(RemovalReason.BurnedOut));
            var prepared = Full(Edit(batch.Intents.ToArray().Select(i => new CellWrite(i.Source, i.State)).ToArray(), TickStage.Burning), context);
            PlannedCell tipTransfer = prepared.Cells.ToArray().Single(c => c.Source.Equals(tip));
            var expected = new CellSnapshot(104, 1, 2, 1, 0, 0, 0);
            BodyFixture.StateEquals(expected, tipTransfer.State);
            Assert.That(tipTransfer.Instance, Is.EqualTo(survivingInstance));
            Assert.That(prepared.Budget.MaterialCells, Is.EqualTo(2));
            Assert.That(prepared.Budget.ChangedPositions, Is.EqualTo(dynamic ? 5 : 4));
            Assert.That(_world.MaterialCells, Is.EqualTo(3));
            Commit(prepared, context, () =>
            {
                Assert.That(_world.Instances.TryResolve(survivingInstance, out CellKey moved), Is.True); Assert.That(moved, Is.EqualTo(tipTransfer.Target));
            });
            Assert.That(_world.MaterialCells, Is.EqualTo(2));
            Assert.That(_world.Published.Read(tipTransfer.Target, out CellSnapshot final).IsSuccess, Is.True); BodyFixture.StateEquals(expected, final);
            Assert.That(_world.Instances.TryResolve(survivingInstance, out _), Is.False);
            Assert.That(_preparer.Storages.All(s => Math.Abs(s.Geometry.Mass - 0.6) < 1e-6 && s.Geometry.Rectangles.Count == 1), Is.True);
            Assert.That(_world.Bodies.Length, Is.EqualTo(dynamic ? 2 : 1));
            if (!dynamic) Assert.That(_world.IsFixed(first), Is.True);
            TestContext.WriteLine($"真实BurnRule燃尽桥→M02候选→M04→M05→状态提交；动态={dynamic}；幸存余量2，传播倒计时1，实例保持。碰撞/火焰未测。");
        }

        [TestCase(TickStage.Commands)] [TestCase(TickStage.Burning)]
        public void M05_T7_02_CenterRemovalShapeOverflowRejectsCommandOrFreezesRealBurnBatch(TickStage stage)
        {
            var cells = new List<InitialCell>();
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) cells.Add(new InitialCell(10 + x, 10 + y, (ushort)(x == 1 && y == 1 ? 104 : 102)));
            Create(cells.ToArray(), new[] { new Vector2Int(11, 11) }, maxBodyShapes: 1);
            _world.BeginTick();
            CellKey center = Grid(11, 11);
            if (stage == TickStage.Burning)
            {
                var seeded = Full(Edit(new[] { new CellWrite(center, new CellSnapshot(104, 1, 1, 2)) }), Context());
                Assert.That(seeded.Apply(Context()).IsSuccess, Is.True); seeded.Dispose();
            }
            int priorCount = _world.ChangedPositions;
            CellWrite[] writes;
            if (stage == TickStage.Burning)
            {
                var batch = new BurnRule(128).Execute(_world, _loaded.Materials, _world.Instances, null, Context(stage));
                Assert.That(batch.Result.IsSuccess, Is.True);
                Assert.That(batch.Intents[0].RemovalReason, Is.EqualTo(RemovalReason.BurnedOut));
                writes = batch.Intents.ToArray().Select(i => new CellWrite(i.Source, i.State)).ToArray();
            }
            else writes = new[] { new CellWrite(center, default) };
            var edit = Edit(writes, stage);
            var plan = _world.PlanStructure(edit, _structure, Context(stage));
            Assert.That(plan.Result.IsSuccess, Is.True);
            var result = _world.PrepareMaterial(edit, plan.Plan, _preparer, Context(stage));
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded)); Assert.That(result.Prepared, Is.Null);
            Assert.That(_world.MaterialCells, Is.EqualTo(9)); Assert.That(_world.Published.Bodies.Length, Is.Zero);
            Assert.That(_world.ChangedPositions, Is.EqualTo(priorCount)); Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
            Assert.That(_world.Published.OccupiedCells.Length, Is.EqualTo(9));
            Assert.That(_world.Read(center, out _).ErrorCode, Is.EqualTo(stage == TickStage.Burning ? WorldErrorCode.Faulted : WorldErrorCode.None));
            if (stage == TickStage.Burning) Assert.That(_world.PublishState().ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            else Assert.That(_world.IsFixed(center), Is.True);
            edit.Dispose();
        }

        [Test]
        public void M05_T7_01_DownstreamPhysicsPreparationProbeRejectsBeforeAnyMaterialApply()
        {
            Create(new[] { new InitialCell(10, 10, 104) });
            IMaterialBodyStorage old = _preparer.Storages.Single();
            _world.BeginTick();
            var candidate = Full(_world.PrepareReplace(Body(1), 102, Context()).Prepared, Context());
            IMaterialBodyStorage replacement = _preparer.Storages.Single();
            using var coordinator = new TransactionCoordinator();
            var physics = new RejectedPhysicsProbe(candidate.Budget);
            coordinator.Own(candidate); coordinator.Own(physics);
            var result = coordinator.ValidateAndApply(Context(), _world.Config.Limits, ContractDefaults.CpuBudgetBytes);
            Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded)); Assert.That(result.Diagnostic.Stage, Is.EqualTo("M06Probe.Preflight"));
            Assert.That(coordinator.RequiresFault, Is.False); Assert.That(physics.Applied, Is.False); Assert.That(physics.Released, Is.True);
            Assert.That(replacement.IsDisposed, Is.True); Assert.That(old.IsDisposed, Is.False);
            Assert.That(_world.Read(Body(1), out CellSnapshot retained).IsSuccess, Is.True); Assert.That(retained.MaterialId, Is.EqualTo(104));
            Assert.That(_world.ChangedPositions, Is.Zero); Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
        }

        private sealed class RejectedPhysicsProbe : IPreparedPhysicsMutation
        {
            public PreparationState State { get; private set; } = PreparationState.Prepared;
            public ResourceBudget Budget { get; }
            public IWorkingWorldView CandidateWorld => null;
            public ITickInstanceMap CandidateInstances => null;
            public ReadOnlySpan<CellPositionKey> CandidateWrites => ReadOnlySpan<CellPositionKey>.Empty;
            internal bool Applied, Released;
            internal RejectedPhysicsProbe(ResourceBudget budget) { Budget = new ResourceBudget(budget.MaterialCells, budget.DynamicBodies, budget.StaticShapes, budget.DynamicShapes, budget.ChangedPositions, 0); }
            public WorldResult Preflight(in TransactionContext context) => WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("M06Probe.Preflight", "physics", "物理准备桩拒绝。"));
            public WorldResult Apply(in TransactionContext context) { Applied = true; State = PreparationState.Applied; return WorldResult.Success(); }
            public void MarkCommitted(WorldVersion version) => State = PreparationState.Committed;
            public void Abort() { State = PreparationState.Aborted; }
            public void Dispose() { Released = true; }
        }

        private sealed class FixturePreparer : IMaterialMutationPreparer
        {
            private readonly MaterialMutationPreparer _inner;
            private readonly bool _rotated;
            private MaterialOwnershipPreparation _next;
            internal IMaterialBodyStorage[] Storages;
            internal FixturePreparer(IMaterialRuntimeTable materials, bool rotated) { _inner = new MaterialMutationPreparer(materials); _rotated = rotated; }
            public PreparationResult<IPreparedMaterialMutation> Prepare(IPreparedMutation candidate, StructurePlan structure,
                BodyIdReservation reserveBodyIds, ITickChangeCounter changes, MaterialOwnershipPreparation prepareOwnership,
                in TransactionContext context, IFailureInjector failures)
            {
                _next = prepareOwnership;
                return _inner.Prepare(candidate, structure, reserveBodyIds, changes, Capture, context, failures);
            }
            private PreparationResult<IPreparedMaterialMutation> Capture(ReadOnlySpan<PlannedCell> cells, ReadOnlySpan<BodyIdMapping> mappings,
                IReadOnlyList<BodyGeometryPlan> geometry, IReadOnlyList<IMaterialBodyStorage> bodies, ReadOnlySpan<ulong> retired,
                in TransactionContext context)
            {
                if (!_rotated || context.WorkingTick != 0)
                {
                    Storages = bodies.ToArray();
                    return _next(cells, mappings, geometry, bodies, retired, context);
                }
                // 专用初始位姿/速度夹具，不新增公开冲量或运行写入API。
                var changedGeometry = new List<BodyGeometryPlan>();
                var changedBodies = new List<IMaterialBodyStorage>();
                bool transferred = false;
                try
                {
                    foreach (BodyGeometryPlan item in geometry)
                    {
                        var pose = new BodyPose(new Vector2(2, 3), (float)(Math.PI / 6));
                        var motion = new BodyMotion(new Vector2(0.25f, 0.1f), 0.5f);
                        var replacement = new BodyGeometryPlan(item.Owner, pose, motion, item.LocalCenterOfMass,
                            item.Mass, item.Inertia, item.BoundingRadius, item.GeometryVersion, item.Cells, item.Rectangles);
                        changedGeometry.Add(replacement);
                        changedBodies.Add(new MaterialBody(replacement, 1));
                    }
                    Storages = changedBodies.ToArray();
                    var result = _next(cells, mappings, changedGeometry, changedBodies, retired, context);
                    transferred = result.Result.IsSuccess;
                    return result;
                }
                finally
                {
                    foreach (IMaterialBodyStorage original in bodies) original.Dispose();
                    if (!transferred) foreach (IMaterialBodyStorage replacement in changedBodies) replacement.Dispose();
                }
            }
        }
    }
}
