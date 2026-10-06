using System;
using NUnit.Framework;
using OpenOita.Contracts;

namespace OpenOita.Tests.Fixtures
{
    // 必须传入 M02/M05/M06 的真实实现；M00 不用假成功世界调用这些集成断言。
    public static class IntegrationContractAssertions
    {
        public static void InstanceMigration(ITickInstanceMap map, CellInstanceHandle original, CellKey target, CellInstanceHandle replacement)
        {
            Assert.That(map.TryResolve(original, out CellKey key), Is.True);
            Assert.That(key, Is.EqualTo(target));
            Assert.That(map.IsWet(original), Is.True, "D01：湿标记随实例跨归属迁移。");
            Assert.That(map.IsWet(replacement), Is.False, "D01/D10：新实例不继承旧湿集合。");
        }
        public static void Writes(ITickChangeCounter counter, ReadOnlySpan<CellPositionKey> keys, int expected, int limit)
        {
            Assert.That(counter.Preflight(keys, limit).IsSuccess, Is.True);
            counter.Record(keys);
            Assert.That(counter.Count, Is.EqualTo(expected));
            counter.Record(keys);
            Assert.That(counter.Count, Is.EqualTo(expected), "D02：多写同键只计一次。");
        }
        public static void PreparedFailure(IPreparedMutation prepared, TransactionContext context)
        {
            Assert.That(prepared.State, Is.EqualTo(PreparationState.Prepared));
            Assert.That(prepared.Preflight(context).IsSuccess, Is.False);
            prepared.Abort(); prepared.Abort(); prepared.Dispose();
            Assert.That(prepared.State, Is.EqualTo(PreparationState.Aborted));
        }
        public static void SameIdReplacement(IWorkingWorldView world, ITickInstanceMap instances, CellKey key,
            CellInstanceHandle oldInstance, CellInstanceHandle newInstance, CellSnapshot expectedInitialState, CommandResult command)
        {
            Assert.That(command.AffectedCount, Is.EqualTo(1));
            Assert.That(world.IsFixed(key), Is.False);
            Assert.That(world.Read(key, out CellSnapshot state).IsSuccess, Is.True);
            Assert.That(state, Is.EqualTo(expectedInitialState));
            Assert.That(instances.TryResolve(oldInstance, out _), Is.False);
            Assert.That(instances.TryResolve(newInstance, out CellKey newKey), Is.True);
            Assert.That(newKey, Is.EqualTo(key));
        }
        public static void FaultedTick(IWorld world, WorldVersion lastPublished, ReadOnlySpan<CommandToken> takenOrPending,
            CommandToken earlierCommitted, CommandResult earlierResult, CommandToken expired)
        {
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Faulted));
            Assert.That(world.Version, Is.EqualTo(lastPublished));
            foreach (var token in takenOrPending) Assert.That(world.Retry(token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(world.Retry(earlierCommitted), Is.EqualTo(earlierResult));
            Assert.That(world.Retry(expired).Result.ErrorCode, Is.EqualTo(WorldErrorCode.ResultExpired));
            Assert.That(world.QueryPoint(default).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
        }
    }
}
