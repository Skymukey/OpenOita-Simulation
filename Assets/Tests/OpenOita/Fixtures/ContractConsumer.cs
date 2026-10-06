using System;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    // 编译探针；正式启动路径中没有桩。所有外部签名在同一个消费者中引用。
    public static class ContractConsumer
    {
        public static WorldResult Exercise(IWorldFactory factory, WorldSources sources)
        {
            WorldCreateResult created = factory.Create(sources, Vector2.zero);
            if (!created.Result.IsSuccess) return created.Result;
            IWorld world = created.World;
            var command = new MaterialCommand(MaterialOperation.Spawn, new WorldRect(Vector2.zero, Vector2.one), 101, world.Version.Generation);
            var queued = world.Enqueue(in command);
            var token = queued.Token;
            world.Retry(in token);
            var step = world.Step();
            if (step.Commands != null)
            {
                var results = new CommandResult[step.Commands.Count];
                step.Commands.CopyTo(results.AsSpan());
            }
            world.QueryPoint(Vector2.zero);
            var buffer = new CellHit[1];
            var region = new WorldRect(Vector2.zero, Vector2.one);
            world.QueryRegion(in region, buffer.AsSpan());
            world.QuerySegment(Vector2.zero, Vector2.one, buffer.AsSpan());
            world.Reset();
            return world.Dispose();
        }
    }
    public sealed class RejectingWorldFactory : IWorldFactory
    {
        public WorldCreateResult Create(WorldSources sources, Vector2 origin) => new WorldCreateResult(
            WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("Create", "test-consumer", "测试拒绝桩，没有生产世界。")));
    }
}
