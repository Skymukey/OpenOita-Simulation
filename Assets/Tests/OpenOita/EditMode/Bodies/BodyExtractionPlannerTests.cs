using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Simulation;
using OpenOita.Structure;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Bodies
{
    public sealed class BodyExtractionPlannerTests
    {
        [TestCase(10)] [TestCase(126)]
        public void M05_T3_01_RemoveBridgeKeepsFixedGridAndExtractsEverySingleCell(int x)
        {
            var world = new BodyFixture { Origin = new Vector2(1.5f, -2) };
            world.Put(BodyFixture.Grid(x, 10), 102, true);
            world.Put(BodyFixture.Grid(x + 1, 10));
            world.Put(BodyFixture.Grid(x + 2, 10));
            world.Remove(BodyFixture.Grid(x + 1, 10));
            using BodyExtractionPlan plan = BodyFixture.Success(world, new ulong[] { 1 });
            Assert.That(plan.Bodies.Count, Is.EqualTo(1));
            Assert.That(plan.Bodies[0].CellCount, Is.EqualTo(1));
            Assert.That(plan.Geometry.Count, Is.EqualTo(2));
            PlannedCell fixedCell = plan.Cells[0], extracted = plan.Cells[1];
            Assert.That(fixedCell.Target, Is.EqualTo(fixedCell.Source));
            Assert.That(extracted.Target.Position.BodyId, Is.EqualTo(1));
            Assert.That(extracted.Target.Position.X, Is.Zero);
            Assert.That(world.IsFixed(fixedCell.Source), Is.True);
            Vector2 actual = MassPropertiesCalculator.Transform(plan.Bodies[0].Snapshot.Pose, new Vector2(0.05f, 0.05f));
            Vector2 expected = world.Origin + new Vector2((x + 2.5f) * 0.1f, 10.5f * 0.1f);
            Assert.That(Vector2.Distance(actual, expected), Is.LessThanOrEqualTo(1e-5));
            Assert.That(plan.Writes.Length, Is.EqualTo(2));
            Assert.That(world.Changes.Count, Is.Zero);
            Assert.That(world.Cells.Count, Is.EqualTo(2));
        }

        private static BodyFixture Arms(int maxBodies = 64)
        {
            var world = new BodyFixture(maxBodies);
            foreach (Vector2Int p in new[] { new Vector2Int(2, 4), new Vector2Int(3, 4), new Vector2Int(5, 4),
                new Vector2Int(6, 4), new Vector2Int(4, 2), new Vector2Int(4, 3), new Vector2Int(4, 5), new Vector2Int(4, 6) })
                world.Put(BodyFixture.Grid(p.x, p.y));
            return world;
        }

        [Test]
        public void M05_T4_01_AllFourArmsBecomeTwoCellBodiesInOriginalMinimumOrder()
        {
            var world = Arms();
            using BodyExtractionPlan plan = BodyFixture.Success(world, new ulong[] { 1, 2, 3, 4 });
            Assert.That(plan.Bodies.Select(b => b.CellCount), Is.EqualTo(new[] { 2, 2, 2, 2 }));
            Assert.That(plan.Cells.Length, Is.EqualTo(8));
            Assert.That(plan.Cells.ToArray().Select(c => c.Source).Distinct().Count(), Is.EqualTo(8));
            Assert.That(plan.Bodies.Select(b => b.Geometry.Cells[0].Source.Position), Is.EqualTo(new[] {
                BodyFixture.Grid(4, 2).Position, BodyFixture.Grid(2, 4).Position,
                BodyFixture.Grid(5, 4).Position, BodyFixture.Grid(4, 5).Position }));
            Assert.That(plan.Bodies.Select(b => b.Snapshot.BodyId), Is.EqualTo(new ulong[] { 1, 2, 3, 4 }));
            Assert.That(plan.Budget.DynamicBodies, Is.EqualTo(4));
            Assert.That(plan.Writes.Length, Is.EqualTo(16));
        }

        [Test]
        public void M05_T4_02_AllFourResultsAreRejectedWhenCapacityIsThree()
        {
            var world = Arms(3);
            StructurePlan structure = world.Analyze();
            Assert.That(structure.Components.Count, Is.EqualTo(4));
            Assert.That(world.Build(out BodyExtractionPlan plan, new ulong[] { 1, 2, 3, 4 }, structure: structure).ErrorCode,
                Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(plan, Is.Null);
            Assert.That(world.Cells.Count, Is.EqualTo(8));
            Assert.That(world.Changes.Count, Is.Zero);
        }

        private static void CheckWorldPositionsAndVelocity(BodySnapshot old, BodyExtractionPlan plan)
        {
            foreach (var body in plan.Bodies)
            {
                BodyGeometryPlan geometry = body.Geometry;
                foreach (PlannedCell cell in geometry.Cells)
                {
                    Vector2 sourceCenter = new Vector2((cell.Source.Position.X + 0.5f) * 0.1f, (cell.Source.Position.Y + 0.5f) * 0.1f);
                    Vector2 targetCenter = new Vector2((cell.Target.Position.X + 0.5f) * 0.1f, (cell.Target.Position.Y + 0.5f) * 0.1f);
                    Vector2 before = MassPropertiesCalculator.Transform(old.Pose, sourceCenter);
                    Vector2 after = MassPropertiesCalculator.Transform(geometry.Pose, targetCenter);
                    Assert.That(Vector2.Distance(before, after), Is.LessThanOrEqualTo(1e-5));
                }
                double cosine = Math.Cos(old.Pose.AngleRadians), sine = Math.Sin(old.Pose.AngleRadians);
                double oldCx = old.Pose.Position.x + cosine * old.LocalCenterOfMass.x - sine * old.LocalCenterOfMass.y;
                double oldCy = old.Pose.Position.y + sine * old.LocalCenterOfMass.x + cosine * old.LocalCenterOfMass.y;
                double newCx = geometry.Pose.Position.x + Math.Cos(geometry.Pose.AngleRadians) * geometry.LocalCenterOfMass.x -
                    Math.Sin(geometry.Pose.AngleRadians) * geometry.LocalCenterOfMass.y;
                double newCy = geometry.Pose.Position.y + Math.Sin(geometry.Pose.AngleRadians) * geometry.LocalCenterOfMass.x +
                    Math.Cos(geometry.Pose.AngleRadians) * geometry.LocalCenterOfMass.y;
                Assert.That(geometry.Motion.LinearVelocity.x, Is.EqualTo(old.Motion.LinearVelocity.x - 0.5 * (newCy - oldCy)).Within(1e-4));
                Assert.That(geometry.Motion.LinearVelocity.y, Is.EqualTo(old.Motion.LinearVelocity.y + 0.5 * (newCx - oldCx)).Within(1e-4));
                Assert.That(geometry.Motion.AngularVelocityRadians, Is.EqualTo(0.5).Within(1e-4));
            }
        }

        [Test]
        public void M05_T5_01_RotatedStripSplitPreservesCentersAndVelocityField()
        {
            var world = new BodyFixture(); world.AddStrip();
            BodySnapshot old = world.BodyItems[0];
            world.Remove(BodyFixture.Body(7, 1, 0));
            using BodyExtractionPlan plan = BodyFixture.Success(world, new ulong[] { 8, 9 });
            Assert.That(plan.RetiredBodyIds.ToArray(), Is.EqualTo(new ulong[] { 7 }));
            Assert.That(plan.BodyMappings.ToArray().Select(m => m.OldBodyId), Is.EqualTo(new ulong[] { 7, 7 }));
            Assert.That(plan.BodyMappings.ToArray().Select(m => m.NewBodyId), Is.EqualTo(new ulong[] { 8, 9 }));
            Assert.That(plan.Bodies.All(b => b.CellCount == 1), Is.True);
            CheckWorldPositionsAndVelocity(old, plan);
        }

        [TestCase(0)] [TestCase(2)]
        public void M05_T5_02_RemoveEitherEndRetainsIdOriginAndRecomputesMass(int x)
        {
            var world = new BodyFixture(); world.AddStrip();
            BodySnapshot old = world.BodyItems[0];
            world.Remove(BodyFixture.Body(7, x, 0));
            using BodyExtractionPlan plan = BodyFixture.Success(world);
            Assert.That(plan.Bodies.Single().Snapshot.BodyId, Is.EqualTo(7));
            Assert.That(plan.Bodies.Single().Snapshot.GeometryVersion, Is.EqualTo(6));
            Assert.That(plan.RetiredBodyIds.Length, Is.Zero);
            Assert.That(plan.Bodies.Single().Snapshot.Pose.Position, Is.EqualTo(old.Pose.Position));
            Assert.That(plan.Bodies.Single().Geometry.Mass, Is.EqualTo(1.2).Within(1.2e-4));
            Assert.That(plan.Bodies.Single().Geometry.Inertia, Is.EqualTo(1.2 * 0.01 / 6 + 1.2 * 0.05 * 0.05).Within(5e-7));
            Assert.That(plan.Writes.Length, Is.Zero, "同ID同坐标的派生几何更新不是归属写入");
            CheckWorldPositionsAndVelocity(old, plan);
        }

        [Test]
        public void M05_T5_02_ReplaceWithDifferentMassRetainsPoseAndAppliesSameVelocityField()
        {
            var world = new BodyFixture(); world.AddStrip();
            BodySnapshot old = world.BodyItems[0];
            world.Put(BodyFixture.Body(7, 2, 0), 102);
            using BodyExtractionPlan plan = BodyFixture.Success(world);
            BodyGeometryPlan geometry = plan.Bodies.Single().Geometry;
            double expectedCenter = (0.6 * 0.05 + 0.6 * 0.15 + 2.4 * 0.25) / 3.6;
            double expectedInertia = 3.6 * 0.01 / 6 + 0.6 * Math.Pow(0.05 - expectedCenter, 2) +
                0.6 * Math.Pow(0.15 - expectedCenter, 2) + 2.4 * Math.Pow(0.25 - expectedCenter, 2);
            Assert.That(geometry.Mass, Is.EqualTo(3.6).Within(3.6e-4));
            Assert.That(geometry.LocalCenterOfMass.x, Is.EqualTo(expectedCenter).Within(1e-5));
            Assert.That(geometry.Inertia, Is.EqualTo(expectedInertia).Within(expectedInertia * 1e-4));
            CheckWorldPositionsAndVelocity(old, plan);
        }

        [Test]
        public void M05_T9_01_BurningSurvivorsKeepAllStateAndInstanceHandles()
        {
            var world = new BodyFixture(); world.AddStrip();
            var first = new CellSnapshot(104, 1, 3, 2, 7, 9, 23);
            var last = new CellSnapshot(104, 1, 1, 1, 5, 8, 17);
            world.Put(BodyFixture.Body(7, 0, 0), state: first);
            world.Put(BodyFixture.Body(7, 2, 0), state: last);
            world.Instances.TryGetInstance(BodyFixture.Body(7, 0, 0), out CellInstanceHandle wet);
            world.Instances.MarkWet(wet);
            world.Remove(BodyFixture.Body(7, 1, 0));
            using BodyExtractionPlan plan = BodyFixture.Success(world, new ulong[] { 8, 9 });
            BodyFixture.StateEquals(first, plan.Cells[0].State);
            BodyFixture.StateEquals(last, plan.Cells[1].State);
            Assert.That(plan.Cells[0].Instance, Is.EqualTo(wet));
            Assert.That(world.Instances.IsWet(wet), Is.True);
            Assert.That(world.Instances.TryResolve(wet, out CellKey source), Is.True);
            Assert.That(source, Is.EqualTo(plan.Cells[0].Source));
            Assert.That(plan.Bodies[0].TryRead(plan.Cells[0].Target.Position, out CellSnapshot stored), Is.True);
            BodyFixture.StateEquals(first, stored);
            Assert.That(plan.Bodies[0].Geometry.Mass, Is.EqualTo(0.6).Within(1e-6));
        }

        [Test]
        public void EmptyBodyIsRetiredWithoutFakeComponentOrGeometry()
        {
            var world = new BodyFixture(); world.AddStrip();
            for (int x = 0; x < 3; x++) world.Remove(BodyFixture.Body(7, x, 0));
            using BodyExtractionPlan plan = BodyFixture.Success(world);
            Assert.That(plan.Bodies.Count, Is.Zero); Assert.That(plan.Geometry.Count, Is.Zero);
            Assert.That(plan.Cells.Length, Is.Zero); Assert.That(plan.RetiredBodyIds.ToArray(), Is.EqualTo(new ulong[] { 7 }));
            Assert.That(plan.BodyMappings[0].OldBodyId, Is.EqualTo(7)); Assert.That(plan.BodyMappings[0].NewBodyId, Is.Zero);
        }

        [Test]
        public void D03_MixedGridAndBodyOrderIsIndependentOfEnumeration()
        {
            var world = new BodyFixture(); world.AddStrip(20); world.Remove(BodyFixture.Body(20, 1, 0));
            world.Put(BodyFixture.Grid(5, 5)); world.Put(BodyFixture.Grid(2, 2));
            world.Keys = world.Keys.Reverse().ToArray();
            using BodyExtractionPlan first = BodyFixture.Success(world, new ulong[] { 21, 22, 23, 24 });
            world.Keys = world.Keys.Reverse().ToArray();
            using BodyExtractionPlan second = BodyFixture.Success(world, new ulong[] { 21, 22, 23, 24 });
            Assert.That(first.Cells.ToArray().Select(c => c.Source.Position), Is.EqualTo(second.Cells.ToArray().Select(c => c.Source.Position)));
            Assert.That(first.Cells.ToArray().Select(c => c.Target.Position.BodyId), Is.EqualTo(new ulong[] { 21, 22, 23, 24 }));
        }

        private sealed class FinalFailure : IFailureInjector
        {
            internal int Calls;
            public WorldResult Check(in TransactionContext context, FailurePoint point)
            {
                if (point != FailurePoint.AfterMaterialPrepared) return WorldResult.Success();
                Calls++;
                Assert.That(point, Is.EqualTo(FailurePoint.AfterMaterialPrepared));
                return WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("M05.AfterMaterialPrepared", "lastBody", "末体完成后注入失败。"));
            }
        }

        [Test]
        public void M05_T7_01_FailureAfterAllBodiesDoesNotChangeCandidateAndRetryHasNoResidualPlan()
        {
            var world = Arms(); var planner = new BodyExtractionPlanner(); var failure = new FinalFailure();
            var keys = world.Keys.ToArray();
            WorldResult result = world.Build(out BodyExtractionPlan failed, new ulong[] { 1, 2, 3, 4 }, failure, planner: planner);
            Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(result.Diagnostic.Stage, Is.EqualTo("M05.AfterMaterialPrepared"));
            Assert.That(failed, Is.Null); Assert.That(failure.Calls, Is.EqualTo(1));
            Assert.That(world.Keys, Is.EqualTo(keys)); Assert.That(world.Changes.Count, Is.Zero);
            Assert.That(world.Build(out BodyExtractionPlan successful, new ulong[] { 5, 6, 7, 8 }, planner: planner).IsSuccess, Is.True);
            using (successful) Assert.That(successful.Bodies.Select(b => b.Snapshot.BodyId), Is.EqualTo(new ulong[] { 5, 6, 7, 8 }));
        }

        [Test]
        public void D03_ExistingUnifiedSequenceDoesNotReuseIdsAfterPreparationFailure()
        {
            var world = Arms(); var ids = new IdentitySequence(); var first = new ulong[4]; var second = new ulong[4];
            for (int i = 0; i < 4; i++) Assert.That(ids.Reserve(out first[i]).IsSuccess, Is.True);
            Assert.That(world.Build(out _, first, new FinalFailure()).IsSuccess, Is.False);
            for (int i = 0; i < 4; i++) Assert.That(ids.Reserve(out second[i]).IsSuccess, Is.True);
            using BodyExtractionPlan plan = BodyFixture.Success(world, second);
            Assert.That(plan.Bodies.Select(b => b.Snapshot.BodyId), Is.EqualTo(new ulong[] { 5, 6, 7, 8 }));
        }

        [TestCase(1, 4096)] [TestCase(256, 3)]
        public void M05_T7_02_RealRingGeometryRejectsShapeLimits(int bodyLimit, int totalLimit)
        {
            var world = new BodyFixture(maxBodyShapes: bodyLimit, maxShapes: totalLimit);
            world.BodyItems = new[] { new BodySnapshot(7, default, default, new Vector2(0.15f, 0.15f), 1) };
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) if (x != 1 || y != 1) world.Put(BodyFixture.Body(7, x, y));
            Assert.That(world.Build(out BodyExtractionPlan plan).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(plan, Is.Null); Assert.That(world.Cells.Count, Is.EqualTo(8)); Assert.That(world.Changes.Count, Is.Zero);
        }

        [Test]
        public void D07_StaticShapesUseOnlyGlobalLimitAndDoNotCountFourBoundaries()
        {
            var world = new BodyFixture(maxBodyShapes: 1, maxShapes: 4);
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) if (x != 1 || y != 1) world.Put(BodyFixture.Grid(x, y), fixedCell: x == 0 && y == 0);
            using BodyExtractionPlan plan = BodyFixture.Success(world);
            Assert.That(plan.Budget.StaticShapes, Is.EqualTo(4)); Assert.That(plan.Budget.DynamicShapes, Is.Zero);
            Assert.That(plan.Bodies.Count, Is.Zero);
        }

        [Test]
        public void D02_TickCounterDeduplicatesPriorWritesAndFullTransferPreflightDoesNotRecord()
        {
            var world = new BodyFixture(maxChanges: 2); var source = BodyFixture.Grid(10, 10);
            world.Put(source); world.Changes.Record(new[] { source.Position });
            Assert.That(world.Build(out BodyExtractionPlan plan, new ulong[] { 1 }, candidateWrites: new[] { source.Position, source.Position }).IsSuccess, Is.True);
            using (plan)
            {
                Assert.That(plan.Writes.Length, Is.EqualTo(2)); Assert.That(plan.Budget.ChangedPositions, Is.EqualTo(2));
                Assert.That(world.Changes.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void D02_SourceAndTargetBothCountAndDoNotResetEarlierTransactions()
        {
            var world = new BodyFixture(maxChanges: 2); world.Put(BodyFixture.Grid(10, 10));
            world.Changes.Record(new[] { BodyFixture.Grid(20, 20).Position });
            Assert.That(world.Build(out BodyExtractionPlan plan, new ulong[] { 1 }).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(plan, Is.Null); Assert.That(world.Changes.Count, Is.EqualTo(1));
        }

        [TestCase(101)] [TestCase(103)]
        public void DynamicNonStructureReplaceIsRejectedBeforeProducingAnyBody(int material)
        {
            var world = new BodyFixture(); world.AddStrip(); StructurePlan oldPlan = world.Analyze();
            world.Put(BodyFixture.Body(7, 1, 0), (ushort)material);
            Assert.That(world.Build(out BodyExtractionPlan plan, structure: oldPlan).ErrorCode, Is.EqualTo(WorldErrorCode.UnsupportedOperation));
            Assert.That(plan, Is.Null); Assert.That(world.BodyItems[0].BodyId, Is.EqualTo(7));
        }

        [Test]
        public void MissingInstancesAndIncompletePlanAreRejected()
        {
            var world = new BodyFixture(); world.Put(BodyFixture.Grid(10, 10));
            StructurePlan structure = world.Analyze();
            world.Instances.Close();
            Assert.That(world.Build(out BodyExtractionPlan plan, new ulong[] { 1 }, structure: structure).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(plan, Is.Null);
            var other = new BodyFixture(); other.Put(BodyFixture.Grid(10, 10));
            Assert.That(other.Build(out plan, structure: new StructurePlan(Array.Empty<StructureComponent>())).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(plan, Is.Null);
        }

        [TestCase(0)] [TestCase(7)] [TestCase(6)]
        public void InvalidReservedIdIsRejectedWithoutAllocatorSideEffects(int id)
        {
            var world = new BodyFixture(); world.AddStrip(); world.Remove(BodyFixture.Body(7, 1, 0));
            Assert.That(world.Build(out BodyExtractionPlan plan, new ulong[] { (ulong)id, 8 }).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(plan, Is.Null); Assert.That(world.Changes.Count, Is.Zero);
        }

        [Test]
        public void GeometryVersionOverflowIsRejectedAtomically()
        {
            var world = new BodyFixture(); world.AddStrip(); BodySnapshot body = world.BodyItems[0];
            world.BodyItems[0] = new BodySnapshot(body.BodyId, body.Pose, body.Motion, body.LocalCenterOfMass, ulong.MaxValue);
            Assert.That(world.Build(out BodyExtractionPlan plan).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(plan, Is.Null); Assert.That(world.BodyItems[0].GeometryVersion, Is.EqualTo(ulong.MaxValue));
        }

        [Test]
        public void ExpiredLeaseAndWrongGenerationFailWithSpecificCodes()
        {
            var world = new BodyFixture(); world.Put(BodyFixture.Grid(10, 10)); StructurePlan structure = world.Analyze();
            world.Closed = true;
            Assert.That(world.Build(out BodyExtractionPlan plan, new ulong[] { 1 }, structure: structure).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(plan, Is.Null);
            world.Closed = false;
            Assert.That(new BodyExtractionPlanner().Build(world, structure, world.Materials, new ulong[] { 1 }, world.Instances, world.Changes,
                ReadOnlySpan<CellPositionKey>.Empty, new TransactionContext(new WorldVersion(2, 0), 1, TickStage.Commands), out plan).ErrorCode,
                Is.EqualTo(WorldErrorCode.StaleGeneration));
        }

        [Test]
        public void CrossThreadDoesNotReadOrMutateWorld()
        {
            var world = new BodyFixture(); world.Put(BodyFixture.Grid(10, 10)); StructurePlan structure = world.Analyze();
            var planner = new BodyExtractionPlanner(); WorldResult result = default;
            var thread = new Thread(() => result = world.Build(out _, new ulong[] { 1 }, planner: planner, structure: structure));
            thread.Start(); thread.Join();
            Assert.That(result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument)); Assert.That(world.Changes.Count, Is.Zero);
        }

        [Test]
        public void M05_T7_03_TwentyCandidateFailureAndDisposeRoundsReleaseOwnedStorage()
        {
            var world = Arms(); var planner = new BodyExtractionPlanner();
            for (int round = 0; round < 20; round++)
            {
                ulong first = (ulong)(round * 8 + 1);
                Assert.That(world.Build(out _, new[] { first, first + 1, first + 2, first + 3 }, new FinalFailure(), planner: planner).IsSuccess, Is.False);
                Assert.That(world.Build(out BodyExtractionPlan plan, new[] { first + 4, first + 5, first + 6, first + 7 }, planner: planner).IsSuccess, Is.True);
                var bodies = plan.Bodies.ToArray();
                plan.Dispose(); plan.Dispose();
                Assert.That(plan.IsDisposed, Is.True);
                Assert.That(bodies.All(body => body.IsDisposed && body.CellCount == 0), Is.True);
                Assert.That(bodies[0].TryRead(new CellPositionKey(OwnerKind.Body, first + 4, 0, 0), out CellSnapshot state), Is.False);
                BodyFixture.StateEquals(default, state);
                Assert.Throws<ObjectDisposedException>(() => { _ = plan.Geometry; });
                Assert.Throws<ObjectDisposedException>(() => { _ = bodies[0].Snapshot; });
            }
            Assert.That(world.Cells.Count, Is.EqualTo(8)); Assert.That(world.Changes.Count, Is.Zero);
            TestContext.WriteLine("20轮M05局部候选失败/成功/重复释放通过；完整Create/Reset/Dispose与真实资源峰值未测。");
        }
    }
}
