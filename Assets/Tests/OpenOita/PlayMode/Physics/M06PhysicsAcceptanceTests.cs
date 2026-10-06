using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.PhysicsAdapter;
using OpenOita.Simulation;
using OpenOita.Spatial;
using OpenOita.Tests.Fixtures;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode.Physics
{
    public sealed class M06PhysicsAcceptanceTests
    {
        private readonly List<IWorld> _owned = new();
        private SimulationWorld Create(WorldSources sources, out M06Sources.CommitObserver observer)
        {
            observer = new M06Sources.CommitObserver(); var renderer = observer;
            WorldCreateResult result = new WorldSimulation(rendererFactory: () => renderer).Create(sources, Vector2.zero);
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.ErrorCode + ": " + result.Result.Diagnostic.Message);
            _owned.Add(result.World); return (SimulationWorld)result.World;
        }
        private SimulationWorld Create(WorldSources sources) => Create(sources, out _);
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (IWorld world in _owned) Assert.That(world.Dispose().IsSuccess, Is.True);
            _owned.Clear(); yield return null; yield return null;
        }
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True, result.ErrorCode + " / " + result.Diagnostic.Stage + " / " + result.Diagnostic.Target + ": " + result.Diagnostic.Message);
        private static void HoldFluidsOneTick(SimulationWorld world)
        {
            var writes = new List<CellWrite>();
            foreach (CellKey key in world.Runtime.State.OccupiedCells)
            {
                world.Runtime.State.Read(key, out CellSnapshot state);
                if (state.MaterialId != 101 && state.MaterialId != 103) continue;
                writes.Add(new CellWrite(key, new CellSnapshot(state.MaterialId, state.Flags, state.FuelTicksRemaining, state.SpreadCountdown,
                    state.LifetimeTicksRemaining, state.MaterialId == 101 ? 2U : 3U, state.IgnitedTick)));
            }
            Success(world.Runtime.SetCellsForTest(writes.ToArray()));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(4)] [TestCase(8)]
        public void M06_T8_01_AutomaticExtractionFreeFallMassesAndIndependentScene(int n)
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(10, 20, 102), new InitialCell(30, 20, 104) }, gravity: -9.81f, probeMasses: true));
            BodySnapshot a = world.Runtime.State.Bodies[0], b = world.Runtime.State.Bodies[1];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(a.BodyId, new BodyPose(new Vector2(0.95f, 1.95f), 0), default, a.LocalCenterOfMass, a.GeometryVersion)));
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(b.BodyId, new BodyPose(new Vector2(2.95f, 1.95f), 0), default, b.LocalCenterOfMass, b.GeometryVersion)));
            Vector2 globalGravity = Physics2D.gravity; float globalStep = Time.fixedDeltaTime;
            var control = new GameObject("M06DefaultSceneControl"); var rb = control.AddComponent<Rigidbody2D>(); rb.gravityScale = 0; rb.position = new Vector2(50, 50); rb.linearVelocity = Vector2.one;
            try
            {
                world.Runtime.FixedSubsteps = n;
                for (int tick = 0; tick < 10; tick++) Success(world.Step().Result);
                Assert.That(world.Runtime.SimulatedSeconds, Is.EqualTo(0.2).Within(1e-7));
                a = world.Runtime.State.Bodies[0]; b = world.Runtime.State.Bodies[1];
                foreach (BodySnapshot body in world.Runtime.State.Bodies)
                {
                    Assert.That(body.Motion.LinearVelocity.y, Is.EqualTo(-1.962f).Within(1e-4));
                    float y = body.Pose.Position.y + body.LocalCenterOfMass.y;
                    Assert.That(Math.Abs(y - (2 - 0.1962)), Is.LessThanOrEqualTo(0.01962 / n + 1e-4));
                    TestContext.WriteLine($"N={n}, mass={world.Runtime.Physics.InspectBody(body.BodyId).mass}, vy={body.Motion.LinearVelocity.y:R}, y={y:R}, error={y - (2 - 0.1962):R}");
                    Assert.That(world.Runtime.Physics.InspectBody(body.BodyId).gravityScale, Is.Zero);
                }
                Assert.That(a.Motion.LinearVelocity.y, Is.EqualTo(b.Motion.LinearVelocity.y).Within(1e-4));
                Assert.That(rb.position, Is.EqualTo(new Vector2(50, 50)));
                Assert.That(Physics2D.gravity, Is.EqualTo(globalGravity)); Assert.That(Time.fixedDeltaTime, Is.EqualTo(globalStep));
            }
            finally { UnityEngine.Object.Destroy(control); }
        }
        [Test]
        public void M06_T8_01_ZeroGravityAndAutomaticSubstepSelection()
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(20, 20, 102) }));
            BodySnapshot before = world.Runtime.State.Bodies[0];
            for (int i = 0; i < 10; i++) Success(world.Step().Result);
            Assert.That(world.Runtime.State.Bodies[0].Pose.Position, Is.EqualTo(before.Pose.Position));
            Assert.That(world.Runtime.State.Bodies[0].Motion.LinearVelocity, Is.EqualTo(Vector2.zero));
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(before.BodyId, before.Pose, new BodyMotion(new Vector2(4, 0), 0), before.LocalCenterOfMass, before.GeometryVersion)));
            Success(world.Step().Result); Assert.That(world.Runtime.LastSubsteps, Is.EqualTo(2));
        }
        [TestCase(6f, 8)] [TestCase(4f, 1)]
        public void M06_T5_03_SpeedOrSubstepsFaultWithoutPublishing(float speed, int maximum)
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(20, 20, 102) }, substeps: maximum), out var observer);
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, body.Pose, new BodyMotion(new Vector2(speed, 0), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            WorldVersion version = world.Version; WorldVersion shown = observer.LastVersion;
            StepResult result = world.Step(); Assert.That(result.Result.IsSuccess, Is.False); Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Faulted));
            Assert.That(world.Version.Equals(version), Is.True); Assert.That(observer.LastVersion.Equals(shown), Is.True);
            Assert.That(world.QueryPoint(new Vector2(2.05f, 2.05f)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Success(world.Reset()); Assert.That(world.Version.CommittedTick, Is.Zero); Assert.That(world.Runtime.State.Bodies[0].Motion.LinearVelocity, Is.EqualTo(Vector2.zero));
        }
        [TestCase(FailurePoint.AfterRectanglesPrepared)] [TestCase(FailurePoint.AfterMassPrepared)]
        [TestCase(FailurePoint.AfterBodyPrepared)] [TestCase(FailurePoint.AfterPhysicsPrepared)] [TestCase(FailurePoint.AfterSpatialPrepared)]
        public void M06_T7_04_InitializationFailureCleansPreparedResources(FailurePoint point)
        {
            WorldSources sources = M06Sources.Create(new[] { new InitialCell(20, 20, 102), new InitialCell(22, 20, 104) });
            WorldLoadResult loaded = new WorldSourceLoader().Load(sources);
            int before = CountPhysicsObjects();
            WorldResult result = WorldRuntime.Create(loaded, Vector2.zero, 1, out WorldRuntime runtime, new FailOnceInjector(point));
            Assert.That(result.IsSuccess, Is.False); Assert.That(runtime, Is.Null);
            Assert.That(CountActivePhysicsObjects(), Is.Zero); TestContext.WriteLine($"{point}, retainedPendingDestroy={CountPhysicsObjects() - before}");
        }
        [UnityTest]
        public IEnumerator M06_T7_04_TwentyFormalCreateResetDisposeRoundsReleaseScenesAndObjects()
        {
            int scenes = SceneManager.sceneCount, objects = CountPhysicsObjects();
            WorldSources sources = M06Sources.Create(new[] { new InitialCell(20, 20, 102) });
            for (int i = 0; i < 20; i++)
            {
                SimulationWorld world = Create(sources); Success(world.Step().Result);
                world.Runtime.Failures = new FailOnceInjector(FailurePoint.AfterPhysicsSubstep);
                Assert.That(world.Step().Result.IsSuccess, Is.False); Success(world.Reset());
                Assert.That(world.Version.Generation, Is.EqualTo(2)); Assert.That(world.Version.CommittedTick, Is.Zero);
                var physics = world.Runtime.Physics;
                var spatial = world.Runtime.Spatial;
                Success(world.Dispose()); Success(world.Dispose());
                Assert.That(physics.Geometry.Count, Is.Zero);
                Assert.That(physics.OwnedObjects, Is.Zero);
                Assert.That(spatial.ReservedCpuBytes, Is.EqualTo(4096));
                Assert.That(spatial.Check(2).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
                yield return null; yield return null;
                Assert.That(CountPhysicsObjects(), Is.EqualTo(objects)); Assert.That(SceneManager.sceneCount, Is.EqualTo(scenes));
            }
            TestContext.WriteLine($"20轮稳定基线：scenes={scenes}, physicsObjects={objects}");
        }
        [Test]
        public void M06_T7_03_FormalSpawnRejectsMicroOverlapButAllowsHole()
        {
            var cells = new List<InitialCell>();
            for (int y = 20; y <= 22; y++) for (int x = 20; x <= 22; x++) if (x != 21 || y != 21) cells.Add(new InitialCell(x, y, 102));
            SimulationWorld world = Create(M06Sources.Create(cells));
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(body.Pose.Position + new Vector2(-0.0001f, 0), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
            EnqueueResult spawn = world.Enqueue(new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(1.9f, 2), new Vector2(2, 2.1f)), 101, world.Version.Generation));
            Success(spawn.Result); Success(world.Step().Result); Assert.That(world.Retry(spawn.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Occupied));
            Assert.That(world.QueryPoint(new Vector2(2.15f, 2.15f)).HasHit, Is.False);
            var hits = new CellHit[16]; Success(world.QuerySegment(new Vector2(2.14f, 2.14f), new Vector2(2.16f, 2.16f), hits).Result);
            Assert.That(world.QueryRegion(new WorldRect(new Vector2(2.11f, 2.11f), new Vector2(2.19f, 2.19f)), hits).RequiredCount, Is.Zero);
            Success(world.Reset());
            var hole = world.Enqueue(new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(2.1f, 2.1f), new Vector2(2.2f, 2.2f)), 101, world.Version.Generation));
            Success(hole.Result); Success(world.Step().Result); Success(world.Retry(hole.Token).Result);
            Assert.That(world.Retry(hole.Token).AffectedCount, Is.EqualTo(1)); Assert.That(world.QueryPoint(new Vector2(2.15f, 2.15f)).Hit.MaterialId, Is.EqualTo(101));
        }
        [TestCase(true)] [TestCase(false)]
        public void M06_T5_01_RotatingStripSplitAndRetainPreserveCentersMotionAndMass(bool middle)
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(20, 20, 104), new InitialCell(21, 20, 104), new InitialCell(22, 20, 104) }));
            BodySnapshot original = world.Runtime.State.Bodies[0];
            var parent = new BodySnapshot(original.BodyId, new BodyPose(new Vector2(2, 3), Mathf.PI / 6),
                new BodyMotion(new Vector2(0.25f, 0.1f), 0.5f), original.LocalCenterOfMass, original.GeometryVersion);
            Success(world.Runtime.SetBodyForTest(parent));
            var exact = new ExactCellGeometry();
            Vector2 Center(int x) => exact.Center(new CellGeometry(new CellKey(world.Version.Generation,
                new CellPositionKey(OwnerKind.Body, parent.BodyId, x, 0)), parent.Pose, new Vector2Int(x, 0), 0.1f));
            Vector2 parentCom = ExactCellGeometry.Transform(new CellGeometry(default, parent.Pose, default, 0.1f), parent.LocalCenterOfMass.x, parent.LocalCenterOfMass.y);
            Vector2 removed = Center(middle ? 1 : 2);
            world.Runtime.BeforePhysicsForTest = () =>
            {
                Assert.That(world.Runtime.State.Bodies.Length, Is.EqualTo(middle ? 2 : 1));
                var actual = new List<Vector2>();
                foreach (BodySnapshot body in world.Runtime.State.Bodies)
                {
                    Assert.That(body.BodyId == parent.BodyId, Is.EqualTo(!middle));
                    Assert.That(body.GeometryVersion, Is.EqualTo(middle ? 1UL : parent.GeometryVersion + 1));
                    Vector2 com = ExactCellGeometry.Transform(new CellGeometry(default, body.Pose, default, 0.1f), body.LocalCenterOfMass.x, body.LocalCenterOfMass.y);
                    Vector2 r = com - parentCom;
                    Vector2 expected = parent.Motion.LinearVelocity + parent.Motion.AngularVelocityRadians * new Vector2(-r.y, r.x);
                    Assert.That(Vector2.Distance(body.Motion.LinearVelocity, expected), Is.LessThanOrEqualTo(1e-4));
                    Assert.That(body.Motion.AngularVelocityRadians, Is.EqualTo(0.5f).Within(1e-4));
                    Rigidbody2D rb = world.Runtime.Physics.InspectBody(body.BodyId);
                    BodyGeometryPlan plan = null; foreach (BodyGeometryPlan item in world.Runtime.Physics.Geometry) if (item.Owner.BodyId == body.BodyId) plan = item;
                    Assert.That(Math.Abs(rb.mass - plan.Mass) / plan.Mass, Is.LessThanOrEqualTo(1e-4));
                    Assert.That(Math.Abs(rb.inertia - plan.Inertia) / plan.Inertia, Is.LessThanOrEqualTo(1e-4));
                    Assert.That(Vector2.Distance(rb.centerOfMass, plan.LocalCenterOfMass), Is.LessThanOrEqualTo(1e-5));
                    Assert.That(Vector2.Distance(rb.linearVelocity, body.Motion.LinearVelocity), Is.LessThanOrEqualTo(1e-4));
                    Assert.That(rb.angularVelocity * Mathf.Deg2Rad, Is.EqualTo(body.Motion.AngularVelocityRadians).Within(1e-4));
                }
                foreach (CellKey key in world.Runtime.State.OccupiedCells)
                {
                    BodySnapshot body = default; foreach (BodySnapshot item in world.Runtime.State.Bodies) if (item.BodyId == key.Position.BodyId) body = item;
                    actual.Add(exact.Center(new CellGeometry(key, body.Pose, new Vector2Int(key.Position.X, key.Position.Y), 0.1f)));
                }
                foreach (int x in middle ? new[] { 0, 2 } : new[] { 0, 1 })
                {
                    Vector2 expected = Center(x); float distance = float.PositiveInfinity;
                    foreach (Vector2 center in actual) distance = Math.Min(distance, Vector2.Distance(center, expected));
                    Assert.That(distance, Is.LessThanOrEqualTo(1e-5));
                }
            };
            var command = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, new WorldRect(removed - Vector2.one * 0.01f, removed + Vector2.one * 0.01f), 0, world.Version.Generation));
            Success(command.Result); Success(world.Step().Result); Assert.That(world.Retry(command.Token).AffectedCount, Is.EqualTo(1));
            Assert.That(world.Runtime.State.MaterialCells, Is.EqualTo(2));
        }

        [Test]
        public void M06_T9_02_FirstRotatingWaterContactExtinguishesAfterFuelConsumption()
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 101) }, burning: new[] { new Vector2Int(10, 10) }));
            HoldFluidsOneTick(world);
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(0.998f, 1), 0), new BodyMotion(Vector2.zero, 2.5f), body.LocalCenterOfMass, body.GeometryVersion)));
            CellKey wood = new CellKey(world.Version.Generation, new CellPositionKey(OwnerKind.Body, body.BodyId, 0, 0));
            world.Runtime.State.Read(wood, out CellSnapshot before);
            IPhysicsStepView savedCandidate = null;
            world.Runtime.AfterPhysicsCandidateForTest = candidate =>
            {
                savedCandidate = candidate;
                Assert.That(candidate.Lease.WorkingTick, Is.EqualTo(world.Runtime.State.WorkingTick));
                Assert.That(candidate.Contacts.Length, Is.GreaterThanOrEqualTo(1));
            };
            world.Runtime.BeforePhysicsForTest = () =>
            {
                Success(world.Runtime.State.Read(wood, out CellSnapshot burning));
                Assert.That(burning.IsBurning, Is.True); Assert.That(burning.FuelTicksRemaining, Is.EqualTo(before.FuelTicksRemaining - 1));
            };
            Success(world.Step().Result); world.Runtime.BeforePhysicsForTest = null;
            world.Runtime.AfterPhysicsCandidateForTest = null;
            Assert.Throws<InvalidOperationException>(() => { _ = savedCandidate.CandidateBodies.Length; });
            Success(world.Runtime.View.Read(wood, out CellSnapshot after));
            Assert.That(after.IsBurning, Is.False); Assert.That(after.IgnitedTick, Is.Zero);
            Assert.That(after.FuelTicksRemaining, Is.EqualTo(before.FuelTicksRemaining - 1));
            int waters = 0; foreach (CellKey key in world.Runtime.View.OccupiedCells) { world.Runtime.View.Read(key, out CellSnapshot state); if (state.MaterialId == 101) waters++; }
            Assert.That(waters + world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1)); Success(world.Step().Result);
            Success(world.Runtime.View.Read(wood, out CellSnapshot next)); Assert.That(next.FuelTicksRemaining, Is.EqualTo(after.FuelTicksRemaining));
        }

        [Test]
        public void M06_T6_01_RealSubstepSuspendsWaterAndSteamAtomicallyAndConservesState()
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 102), new InitialCell(11, 10, 102), new InitialCell(10, 9, 101), new InitialCell(11, 9, 103) }));
            HoldFluidsOneTick(world);
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, body.Pose, new BodyMotion(new Vector2(0, -2), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            Success(world.Step().Result);
            int water = 0, steam = 0, solids = 0;
            foreach (CellKey key in world.Runtime.View.OccupiedCells)
            {
                Success(world.Runtime.View.Read(key, out CellSnapshot state));
                if (state.MaterialId == 102) solids++;
            }
            foreach (SuspendedFluidSnapshot record in ((IFluidSuspensionView)world.Runtime.View).SuspendedFluids)
            {
                Assert.That(record.OriginalPosition.y, Is.EqualTo(9));
                if (record.State.MaterialId == 101) { water++; Assert.That(record.State.MoveCountdown, Is.EqualTo(1)); }
                if (record.State.MaterialId == 103) { steam++; Assert.That(record.State.LifetimeTicksRemaining, Is.EqualTo(199)); Assert.That(record.State.MoveCountdown, Is.EqualTo(2)); }
            }
            Assert.That(water, Is.EqualTo(1)); Assert.That(steam, Is.EqualTo(1)); Assert.That(solids, Is.EqualTo(2));
            Assert.That(world.Runtime.State.ChangedPositions, Is.EqualTo(4));
        }

        [Test]
        public void M06_T7_01_NoImmediateTargetSuspendsBothSourcesAndPublishes()
        {
            var cells = new[] { new InitialCell(10, 10, 102), new InitialCell(20, 10, 102), new InitialCell(10, 9, 101), new InitialCell(20, 9, 101),
                new InitialCell(19, 9, 101), new InitialCell(21, 9, 101), new InitialCell(20, 8, 101) };
            SimulationWorld world = Create(M06Sources.Create(cells, radius: 1), out var observer);
            HoldFluidsOneTick(world);
            BodySnapshot[] bodies = world.Runtime.State.Bodies.ToArray();
            foreach (BodySnapshot body in bodies) Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, body.Pose, new BodyMotion(new Vector2(0, -2), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            WorldVersion before = world.Version;
            Success(world.Step().Result);
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Ready));
            Assert.That(world.Version.CommittedTick, Is.EqualTo(before.CommittedTick + 1));
            Assert.That(observer.LastVersion, Is.EqualTo(world.Version));
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(2));
            Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(cells.Length));
            foreach (int x in new[] { 10, 20 })
            {
                Success(world.Runtime.View.Read(new CellKey(world.Version.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, 9)), out CellSnapshot state));
                Assert.That(state.MaterialId, Is.Zero);
                Success(world.Runtime.View.Read(new CellKey(world.Version.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, 8)), out CellSnapshot target));
                if (x == 10) Assert.That(target.MaterialId, Is.Zero);
            }
            Success(world.Reset()); Assert.That(world.Runtime.State.MaterialCells, Is.EqualTo(cells.Length));
        }

        [Test]
        public void M06_T7_02_CumulativeTickBudgetRejectsWholeDisplacementAndFaultsTokens()
        {
            // 两个源先写倒计时2个位置；排开再加入2个空目标，总计4，限额3整拒。
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 102), new InitialCell(11, 10, 102), new InitialCell(10, 9, 101), new InitialCell(11, 9, 101) }, maxChanges: 3));
            HoldFluidsOneTick(world);
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, body.Pose, new BodyMotion(new Vector2(0, -2), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            EnqueueResult command = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, new WorldRect(new Vector2(3, 3), new Vector2(3.1f, 3.1f)), 0, world.Version.Generation));
            Success(command.Result); WorldVersion before = world.Version;
            StepResult result = world.Step(); Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(result.Result.Diagnostic.Target, Is.EqualTo("maxChangesPerTick")); Assert.That(world.Version.Equals(before), Is.True);
            Assert.That(world.Retry(command.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Success(world.Reset()); Assert.That(world.Retry(command.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
            Assert.That(world.Runtime.State.MaterialCells, Is.EqualTo(4));
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void M06_T5_02_WorldFourSidesAreRealBoundaryCollisions(int side)
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(100, 100, 102) }));
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Vector2 pose = side == 0 ? new Vector2(0.12f, 3) : side == 1 ? new Vector2(25.38f, 3) : side == 2 ? new Vector2(3, 0.12f) : new Vector2(3, 25.38f);
            Vector2 velocity = side == 0 ? Vector2.left : side == 1 ? Vector2.right : side == 2 ? Vector2.down : Vector2.up;
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(pose, 0), new BodyMotion(velocity * 4, 0), body.LocalCenterOfMass, body.GeometryVersion)));
            for (int i = 0; i < 8; i++) Success(world.Step().Result);
            BodySnapshot after = world.Runtime.State.Bodies[0];
            Assert.That(after.Pose.Position.x, Is.InRange(-0.01f, 25.51f)); Assert.That(after.Pose.Position.y, Is.InRange(-0.01f, 25.51f));
            Assert.That(world.Runtime.Physics.BoundaryShapes, Is.EqualTo(4)); Assert.That(world.Runtime.View.OccupiedCells.Length, Is.EqualTo(1));
        }

        [Test]
        public void M06_T7_02_TemporaryCpuCapacityRejectsDisplacementBeforeAdoption()
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 102), new InitialCell(11, 10, 102), new InitialCell(10, 9, 101), new InitialCell(11, 9, 103) }));
            HoldFluidsOneTick(world);
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, body.Pose, new BodyMotion(new Vector2(0, -2), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            world.Runtime.BeforePhysicsForTest = () => world.Runtime.CpuBudgetBytes = world.Runtime.ReservedCpuBytes + 1;
            WorldVersion before = world.Version;
            StepResult result = world.Step(); Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(world.Version.Equals(before), Is.True); Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Faulted));
            foreach (int x in new[] { 10, 11 })
            {
                Success(world.Runtime.View.Read(new CellKey(world.Version.Generation, new CellPositionKey(OwnerKind.Grid, 0, x, 9)), out CellSnapshot state));
                Assert.That(state.MaterialId, Is.Not.Zero);
            }
            Success(world.Reset()); Assert.That(world.Runtime.CpuBudgetBytes, Is.EqualTo(ContractDefaults.CpuBudgetBytes));
        }

        [Test]
        public void M06_T5_02_FixedFloorAndCornerUseRealMaterialRectangles()
        {
            var cells = new List<InitialCell>();
            for (int x = 10; x <= 30; x++) cells.Add(new InitialCell(x, 20, 102));
            for (int y = 21; y <= 25; y++) cells.Add(new InitialCell(30, y, 102));
            cells.Add(new InitialCell(20, 30, 104));
            SimulationWorld world = Create(M06Sources.Create(cells, new[] { new Vector2Int(10, 20) }, gravity: -9.81f));
            Assert.That(world.Runtime.State.Bodies.Length, Is.EqualTo(1));
            for (int tick = 0; tick < 35; tick++) Success(world.Step().Result);
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Assert.That(body.Pose.Position.y, Is.GreaterThanOrEqualTo(2.09f)); Assert.That(Math.Abs(body.Motion.LinearVelocity.y), Is.LessThanOrEqualTo(1e-4));
            // 角点夹具：测试钩子设置运动，不手工创建第二套刚体。
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(2.79f, 2.25f), 0), new BodyMotion(new Vector2(2.5f, -2.5f), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            WorldVersion last = world.Version;
            for (int tick = 0; tick < 12; tick++)
            {
                StepResult result = world.Step();
                if (!result.Result.IsSuccess)
                {
                    Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Faulted)); Assert.That(world.Version.Equals(last), Is.True);
                    Assert.That(new[] { "speed", "solidOverlap", "boundary", "edgeDisplacement" }, Does.Contain(result.Result.Diagnostic.Target));
                    TestContext.WriteLine("角点按合同冻结：" + result.Result.Diagnostic.Target); return;
                }
                last = world.Version;
            }
            body = world.Runtime.State.Bodies[0];
            Assert.That(body.Pose.Position.x + 0.1f, Is.LessThanOrEqualTo(3.01f)); Assert.That(body.Pose.Position.y, Is.GreaterThanOrEqualTo(2.09f));
        }

        [Test]
        public void M06_T7_04_SecondBodyFailureReleasesAlreadyPreparedMaterialBodies()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(20, 20, 102), new InitialCell(22, 20, 104) }));
            WorldResult result = WorldRuntime.Create(loaded, Vector2.zero, 1, out WorldRuntime runtime, new SecondBodyFailure());
            Assert.That(result.IsSuccess, Is.False); Assert.That(runtime, Is.Null); Assert.That(CountActivePhysicsObjects(), Is.Zero);
        }
        private sealed class SecondBodyFailure : IFailureInjector
        {
            private int _count;
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point == FailurePoint.AfterBodyPrepared && ++_count == 2
                ? WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic(context.Stage.ToString(), "第二个材料体", "验收故障注入")) : WorldResult.Success();
        }

        [Test]
        public void M06_T7_03_WorldEdgeSpawnRejectsAnyRepresentablePositiveOverlap()
        {
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 102) }));
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(0.0999f, 1), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
            EnqueueResult command = world.Enqueue(new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(0, 1), new Vector2(0.1f, 1.1f)), 101, world.Version.Generation));
            Success(command.Result); Success(world.Step().Result); Assert.That(world.Retry(command.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Occupied));
            Assert.That(world.Runtime.State.MaterialCells, Is.EqualTo(1)); Assert.That(world.QueryPoint(new Vector2(0.05f, 1.05f)).HasHit, Is.False);
        }

        private static int CountPhysicsObjects()
        {
            int count = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>()) if (go.scene.IsValid() && go.scene.name.StartsWith("OpenOitaPhysics-", StringComparison.Ordinal)) count++;
            return count;
        }
        private static int CountActivePhysicsObjects()
        {
            int count = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>()) if (go.scene.IsValid() && go.scene.name.StartsWith("OpenOitaPhysics-", StringComparison.Ordinal) && go.activeSelf) count++;
            return count;
        }
    }
}
