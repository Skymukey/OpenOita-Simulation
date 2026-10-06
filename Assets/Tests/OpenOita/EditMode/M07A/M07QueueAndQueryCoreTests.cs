using System;
using NUnit.Framework;
using OpenOita.Commands;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Queries;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M07A
{
    public sealed class M07QueueAndQueryCoreTests
    {
        [Test] public void M07A_02_Core4096QueueCacheAndNoSequenceConsumptionWhenFull()
        {
            object owner = new object(); var queue = new CommandQueue(owner);
            var command = new MaterialCommand(MaterialOperation.Ignite, new WorldRect(Vector2.zero, Vector2.one), 0, 1);
            CommandToken first = default;
            for (int i = 0; i < 4096; i++) { var result = queue.Enqueue(command); Assert.That(result.Result.IsSuccess, Is.True); if (i == 0) first = result.Token; }
            Assert.That(queue.Enqueue(command).Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded)); Assert.That(queue.Highest, Is.EqualTo(4096));
            var version = new WorldVersion(1, 1);
            for (int i = 0; i < 4096; i++) { var item = queue.Dequeue(); queue.Cache(new CommandResult(WorldResult.Success(), item.token, 0, version)); }
            Assert.That(queue.Retry(first, version, false).Result.IsSuccess, Is.True);
            var next = queue.Enqueue(command); Assert.That(next.Token.Sequence, Is.EqualTo(4097));
            var final = queue.Dequeue(); queue.Cache(new CommandResult(WorldResult.Success(), final.token, 0, version));
            Assert.That(queue.Retry(first, version, false).Result.ErrorCode, Is.EqualTo(WorldErrorCode.ResultExpired));
            Assert.That(queue.Retry(new CommandToken(new object(), 1, 1), version, false).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidToken));
            Assert.That(queue.Retry(next.Token, new WorldVersion(2, 0), false).Result.ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
        }
        [Test] public void M07A_06_CoreBoundedQueryHasStableSortAndNoPartialWrites()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(M06Sources.Create(new[] { new InitialCell(0, 0, 102), new InitialCell(1, 0, 104) }));
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                var service = new QueryService(loaded.Config.Limits.MaxMaterialCells);
                int chunks = world.ChunkCount;
                var marker = new CellHit(new WorldVersion(99, 1), new CellPositionKey(OwnerKind.Grid, 0, 99, 99), default);
                var small = new[] { marker };
                var result = service.Collect(world.Published, default, Vector2.zero, new Vector2(0.2f, 0), true, small);
                Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.BufferTooSmall)); Assert.That(result.RequiredCount, Is.EqualTo(2)); Assert.That(small[0].Position, Is.EqualTo(marker.Position));
                var hits = new CellHit[2]; result = service.Collect(world.Published, default, Vector2.zero, new Vector2(0.2f, 0), true, hits);
                Assert.That(result.Result.IsSuccess, Is.True); Assert.That(hits[0].Position.X, Is.Zero); Assert.That(hits[1].Position.X, Is.EqualTo(1));
                Assert.That(service.Collect(world.Published, default, new Vector2(0.1f, 0.05f), new Vector2(0.1f, 0.05f), true, hits).RequiredCount, Is.EqualTo(1));
                Assert.That(world.ChunkCount, Is.EqualTo(chunks));
                service.Dispose(); service.Dispose(); Assert.That(service.ReservedBytes, Is.EqualTo(128));
            }
        }
    }
}
