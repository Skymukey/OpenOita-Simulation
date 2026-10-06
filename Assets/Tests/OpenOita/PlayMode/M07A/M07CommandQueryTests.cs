using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Commands;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.PlayMode.M07A
{
    public sealed class M07CommandQueryTests
    {
        private readonly List<IWorld> _worlds = new();
        private SimulationWorld Create(params InitialCell[] cells)
        {
            var result = new WorldSimulation(rendererFactory: () => new M06Sources.CommitObserver()).Create(M06Sources.Create(cells), Vector2.zero);
            Success(result.Result); _worlds.Add(result.World); return (SimulationWorld)result.World;
        }
        [TearDown] public void Dispose() { foreach (IWorld world in _worlds) world.Dispose(); _worlds.Clear(); }
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True, result.ErrorCode + ": " + result.Diagnostic.Message);
        private static MaterialCommand Command(IWorld world, MaterialOperation operation, int x, int y, ushort id = 0, int width = 1) =>
            new MaterialCommand(operation, new WorldRect(new Vector2(x * 0.1f, y * 0.1f), new Vector2((x + width) * 0.1f, (y + 1) * 0.1f)), id, world.Version.Generation);

        [Test] public void M07A_01_EnqueueAndRetryNeverExecuteAndResultLeaseExpires()
        {
            IWorld world = Create(new InitialCell(10, 10, 104));
            var token = world.Enqueue(Command(world, MaterialOperation.Ignite, 10, 10)); Success(token.Result);
            Assert.That(world.Version.CommittedTick, Is.Zero);
            Assert.That(world.Retry(token.Token).Result.Status, Is.EqualTo(ResultStatus.Pending));
            Assert.That(world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State.IsBurning, Is.False);
            StepResult step = world.Step(); Success(step.Result);
            Assert.That(world.Retry(token.Token).AffectedCount, Is.EqualTo(1));
            Assert.That(world.Retry(token.Token).Version, Is.EqualTo(step.Version));
            Assert.That(world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State.FuelTicksRemaining, Is.EqualTo(250));
            var copy = new CommandResult[step.Commands.Count]; step.Commands.CopyTo(copy);
            Success(world.Step().Result);
            Assert.Throws<InvalidOperationException>(() => { _ = step.Commands.Count; });
            Assert.That(copy[0].AffectedCount, Is.EqualTo(1));
            Assert.That(world.Retry(token.Token).AffectedCount, Is.EqualTo(1));
        }
        [Test] public void M07A_02_QueueAndCache4096TokenIdentityAndOverflow()
        {
            IWorld world = Create(); var command = Command(world, MaterialOperation.Ignite, 1, 1);
            CommandToken first = default, last = default;
            for (int i = 0; i < 4096; i++) { var queued = world.Enqueue(command); Success(queued.Result); if (i == 0) first = queued.Token; last = queued.Token; }
            Assert.That(last.Sequence, Is.EqualTo(4096));
            Assert.That(world.Enqueue(command).Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Success(world.Step().Result); Success(world.Retry(first).Result);
            var next = world.Enqueue(command); Assert.That(next.Token.Sequence, Is.EqualTo(4097)); Success(world.Step().Result);
            Assert.That(world.Retry(first).Result.ErrorCode, Is.EqualTo(WorldErrorCode.ResultExpired));
            Assert.That(world.Retry(default).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
            Assert.That(world.Retry(new CommandToken(world, 1, 9000)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
            IWorld other = Create(); Assert.That(other.Retry(last).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
            Success(world.Reset()); Assert.That(world.Retry(last).Result.ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
            // 同一生产队列的溢出边界，使用反射设置已耗尽值，不循环分配2^64个令牌。
            var queue = new CommandQueue(new object());
            typeof(CommandQueue).GetField("<Highest>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(queue, ulong.MaxValue);
            Assert.That(queue.Enqueue(command).Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
        }
        [Test] public void M07A_03_CrossChunkSpawnAtomicAndSameIdReplaceRemovesFixedMark()
        {
            IWorld world = Create(new InitialCell(127, 0, 102));
            var spawn = world.Enqueue(Command(world, MaterialOperation.Spawn, 127, 0, 102, 2)); Success(spawn.Result); Success(world.Step().Result);
            Assert.That(world.Retry(spawn.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Occupied));
            Assert.That(world.QueryPoint(new Vector2(12.85f, 0.05f)).HasHit, Is.False);
            var replace = world.Enqueue(Command(world, MaterialOperation.Replace, 127, 0, 104, 2)); Success(world.Step().Result);
            Assert.That(world.Retry(replace.Token).AffectedCount, Is.EqualTo(1));
        }
        [Test] public void M07A_04_RotatedRingUsesRealFacesAndRegionMatchesErase()
        {
            var cells = new List<InitialCell>(); for (int y = 20; y < 23; y++) for (int x = 20; x < 23; x++) if (x != 21 || y != 21) cells.Add(new InitialCell(x, y, 102));
            SimulationWorld world = Create(cells.ToArray()); BodySnapshot old = world.Runtime.State.Bodies[0];
            var pose = new BodyPose(old.Pose.Position, 0.37f);
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(old.BodyId, pose, default, old.LocalCenterOfMass, old.GeometryVersion)));
            Vector2 hole = pose.Position + Rotate(new Vector2(0.15f, 0.15f), pose.AngleRadians);
            Assert.That(world.QueryPoint(hole).HasHit, Is.False);
            var region = new WorldRect(hole - Vector2.one * 0.02f, hole + Vector2.one * 0.02f);
            Assert.That(world.QueryRegion(region, new CellHit[8]).RequiredCount, Is.Zero);
            Vector2 center = pose.Position + Rotate(new Vector2(0.05f, 0.05f), pose.AngleRadians);
            Assert.That(world.QueryPoint(center).HasHit, Is.True);
            region = new WorldRect(center - Vector2.one * 0.01f, center + Vector2.one * 0.01f);
            Assert.That(world.QueryRegion(region, new CellHit[8]).RequiredCount, Is.EqualTo(1));
            var erase = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, region, 0, world.Version.Generation)); Success(world.Step().Result);
            Assert.That(world.Retry(erase.Token).AffectedCount, Is.EqualTo(1));
        }
        [Test] public void M07A_05_DynamicFluidReplaceRejectsWholeMixedSelection()
        {
            IWorld world = Create(new InitialCell(10, 0, 102), new InitialCell(10, 2, 104));
            var command = new MaterialCommand(MaterialOperation.Replace, new WorldRect(new Vector2(1, 0), new Vector2(1.1f, 0.3f)), 101, 1);
            var token = world.Enqueue(command); Success(world.Step().Result);
            Assert.That(world.Retry(token.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.UnsupportedOperation));
            Assert.That(world.QueryPoint(new Vector2(1.05f, 0.05f)).Hit.MaterialId, Is.EqualTo(102));
            Assert.That(world.QueryPoint(new Vector2(1.05f, 0.25f)).Hit.MaterialId, Is.EqualTo(104));
        }
        [Test] public void M07A_06_BoundariesSortingBufferAndNoChunkAllocation()
        {
            SimulationWorld world = Create(new InitialCell(1, 0, 102), new InitialCell(0, 0, 102));
            int chunks = world.Runtime.State.ChunkCount;
            Assert.That(world.QueryPoint(new Vector2(25.6f, 0)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(world.QueryPoint(new Vector2(float.NaN, 0)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            var sent = new CellHit(new WorldVersion(999, 999), new CellPositionKey(OwnerKind.Grid, 0, 99, 99), default);
            var small = new[] { sent };
            var query = world.QuerySegment(Vector2.zero, new Vector2(0.2f, 0), small);
            Assert.That(query.Result.ErrorCode, Is.EqualTo(WorldErrorCode.BufferTooSmall)); Assert.That(query.RequiredCount, Is.EqualTo(2)); Assert.That(small[0].Position, Is.EqualTo(sent.Position));
            var hits = new CellHit[8]; query = world.QuerySegment(Vector2.zero, new Vector2(0.2f, 0), hits); Success(query.Result);
            Assert.That(hits[0].Position.X, Is.Zero); Assert.That(hits[1].Position.X, Is.EqualTo(1));
            Assert.That(world.QuerySegment(new Vector2(0.1f, 0.05f), new Vector2(0.1f, 0.05f), hits).RequiredCount, Is.EqualTo(1));
            Assert.That(world.QueryRegion(new WorldRect(Vector2.zero, new Vector2(25.6f, 25.6f)), hits).RequiredCount, Is.EqualTo(2));
            Assert.That(world.QueryRegion(new WorldRect(Vector2.zero, new Vector2(25.7f, 1)), hits).Result.ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(world.Runtime.State.ChunkCount, Is.EqualTo(chunks));
        }
        [TestCase(false)] [TestCase(true)] public void M07A_07_FailureInvalidatesAllCurrentTokensButPreservesHistory(bool commandFailure)
        {
            SimulationWorld world = Create(new InitialCell(10, 10, 104), new InitialCell(20, 20, 102));
            var prior = world.Enqueue(Command(world, MaterialOperation.Ignite, 10, 10)); Success(world.Step().Result);
            var first = world.Enqueue(Command(world, MaterialOperation.Remove, 10, 10));
            var second = world.Enqueue(Command(world, MaterialOperation.Spawn, 20, 20, 102));
            var third = world.Enqueue(Command(world, MaterialOperation.Ignite, 1, 1));
            world.Runtime.Failures = new Failure(commandFailure ? FailurePoint.DuringApply : FailurePoint.AfterPhysicsSubstep);
            WorldVersion before = world.Version; Assert.That(world.Step().Result.IsSuccess, Is.False); Assert.That(world.Version, Is.EqualTo(before));
            foreach (CommandToken token in new[] { first.Token, second.Token, third.Token }) Assert.That(world.Retry(token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Success(world.Retry(prior.Token).Result); Assert.That(world.QueryPoint(Vector2.zero).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Success(world.Reset()); Assert.That(world.Retry(first.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
        }
        [Test] public void M07A_08_IgniteNoOpAndSameMaterialReplacementCreatesFreshState()
        {
            IWorld world = Create(new InitialCell(10, 10, 104), new InitialCell(12, 10, 102));
            var a = world.Enqueue(Command(world, MaterialOperation.Ignite, 10, 10)); var no = world.Enqueue(Command(world, MaterialOperation.Ignite, 12, 10)); Success(world.Step().Result);
            Assert.That(world.Retry(a.Token).AffectedCount, Is.EqualTo(1)); Assert.That(world.Retry(no.Token).AffectedCount, Is.Zero);
            var repeat = world.Enqueue(Command(world, MaterialOperation.Ignite, 10, 10)); Success(world.Step().Result); Assert.That(world.Retry(repeat.Token).AffectedCount, Is.Zero);
            Assert.That(world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State.FuelTicksRemaining, Is.EqualTo(249));
            var replace = world.Enqueue(Command(world, MaterialOperation.Replace, 10, 10, 104)); Success(world.Step().Result);
            var state = world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State;
            Assert.That(state.IsBurning, Is.False); Assert.That(state.FuelTicksRemaining, Is.EqualTo(250)); Assert.That(world.Retry(replace.Token).AffectedCount, Is.EqualTo(1));
        }
        [Test] public void M07A_09_PublicLifecycleNotificationAllowsReadAndEnqueueButRejectsReentry()
        {
            IWorld world = Create(); CommandToken queued = default; IChangeSetView borrowed = null;
            world.Committed += changes =>
            {
                Assert.That(changes.Version, Is.EqualTo(world.QueryPoint(Vector2.zero).Version));
                Assert.That(world.Step().Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                Assert.That(world.Reset().ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                Assert.That(world.Dispose().ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                queued = world.Enqueue(Command(world, MaterialOperation.Ignite, 1, 1)).Token; borrowed = changes.Changes;
            };
            Success(world.Step().Result); Assert.That(world.Retry(queued).Result.Status, Is.EqualTo(ResultStatus.Pending));
            Assert.Throws<InvalidOperationException>(() => { _ = borrowed.Cells.Length; });
            Success(world.Reset()); Success(world.Dispose()); Success(world.Dispose());
            Assert.That(world.QueryPoint(Vector2.zero).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
        }
        private static Vector2 Rotate(Vector2 p, float angle) => new Vector2(Mathf.Cos(angle) * p.x - Mathf.Sin(angle) * p.y, Mathf.Sin(angle) * p.x + Mathf.Cos(angle) * p.y);
        [Test] public void M07A_06_MicroOverlapPointPriorityZeroSegmentAndEqualDistanceSort()
        {
            var sources = M06Sources.Create(new[] { new InitialCell(1, 1, 102), new InitialCell(5, 5, 104) }, new[] { new Vector2Int(1, 1) });
            var created = new WorldSimulation(rendererFactory: () => new M06Sources.CommitObserver()).Create(sources, Vector2.zero); Success(created.Result); _worlds.Add(created.World);
            var world = (SimulationWorld)created.World; var body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(0.10001f, 0.1f), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
            Vector2 point = new Vector2(0.15f, 0.15f); var hit = world.QueryPoint(point); Assert.That(hit.Hit.Position.OwnerKind, Is.EqualTo(OwnerKind.Grid));
            var hits = new CellHit[4]; var query = world.QuerySegment(point, point, hits); Success(query.Result); Assert.That(query.RequiredCount, Is.EqualTo(2));
            Assert.That(hits[0].Position.OwnerKind, Is.EqualTo(OwnerKind.Grid)); Assert.That(hits[1].Position.BodyId, Is.EqualTo(body.BodyId));
            query = world.QuerySegment(point, point + new Vector2(0.01f, 0), hits); Success(query.Result); Assert.That(hits[0].Position.OwnerKind, Is.EqualTo(OwnerKind.Grid));
            Assert.That(world.QueryPoint(new Vector2(0.2f, 0.15f)).Hit.Position.OwnerKind, Is.EqualTo(OwnerKind.Body));
        }
        [Test] public void M07A_03_08_SameIdReplaceResetsFixedMarkAndChangesDescribeNewInstanceOwnership()
        {
            var sources = M06Sources.Create(new[] { new InitialCell(127, 10, 104) }, new[] { new Vector2Int(127, 10) });
            var result = new WorldSimulation(rendererFactory: () => new M06Sources.CommitObserver()).Create(sources, Vector2.zero); Success(result.Result); _worlds.Add(result.World);
            IWorld world = result.World; CellChange[] copied = null; BodyIdMapping[] mappings = null;
            world.Committed += changes => { copied = changes.Changes.Cells.ToArray(); mappings = changes.Changes.BodyMappings.ToArray(); };
            var token = world.Enqueue(Command(world, MaterialOperation.Replace, 127, 10, 104)); Success(world.Step().Result);
            Assert.That(world.Retry(token.Token).AffectedCount, Is.EqualTo(1));
            var hit = world.QueryPoint(new Vector2(12.75f, 1.05f)); Assert.That(hit.Hit.Position.OwnerKind, Is.EqualTo(OwnerKind.Body));
            Assert.That(copied.Length, Is.EqualTo(1)); Assert.That(copied[0].Kind, Is.EqualTo(CellChangeKind.Replaced));
            Assert.That(copied[0].BeforeKey.Position.OwnerKind, Is.EqualTo(OwnerKind.Grid)); Assert.That(copied[0].AfterKey.Position, Is.EqualTo(hit.Hit.Position));
            Assert.That(mappings.Length, Is.EqualTo(1)); Assert.That(mappings[0].OldBodyId, Is.Zero); Assert.That(mappings[0].NewBodyId, Is.EqualTo(hit.Hit.Position.BodyId));
        }
        private sealed class Failure : IFailureInjector
        {
            private readonly FailurePoint _point;
            internal Failure(FailurePoint point) { _point = point; }
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point == _point ? WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic(context.Stage.ToString(), "M07A注入", "验收故障")) : WorldResult.Success();
        }
    }
}

