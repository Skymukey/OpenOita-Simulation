using System;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode
{
    public sealed class RuleInstanceContractTests
    {
        private static CellKey GridKey(int x, int y, ulong generation = 1)
        {
            return new CellKey(generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        }

        [Test]
        public void M02_I01_LookupIsReadOnlyAndMissingOrStaleKeysReturnDefault()
        {
            var implementation = new TickInstanceMap(1, 5);
            ITickInstanceMap instances = implementation;
            CellKey firstKey = GridKey(1, 2);
            CellInstanceHandle first = instances.Create(firstKey);
            for (int i = 0; i < 100; i++)
            {
                Assert.That(instances.TryGetInstance(firstKey, out CellInstanceHandle found), Is.True);
                Assert.That(found, Is.EqualTo(first));
                Assert.That(instances.TryGetInstance(GridKey(99, 99), out CellInstanceHandle missing), Is.False);
                Assert.That(missing, Is.EqualTo(default(CellInstanceHandle)));
                Assert.That(instances.TryGetInstance(GridKey(1, 2, 2), out CellInstanceHandle stale), Is.False);
                Assert.That(stale, Is.EqualTo(default(CellInstanceHandle)));
            }
            CellInstanceHandle second = instances.Create(GridKey(3, 4));
            Assert.That(second.Sequence, Is.EqualTo(first.Sequence + 1), "只读查询不能分配实例序号。");
            Assert.That(instances.IsWet(first), Is.False);
            implementation.Close();
            Assert.That(instances.TryGetInstance(firstKey, out CellInstanceHandle closed), Is.False);
            Assert.That(closed, Is.EqualTo(default(CellInstanceHandle)));
        }

        [Test]
        public void M02_I01_LookupFollowsOwnershipTransferRemoveAndReplacement()
        {
            ITickInstanceMap instances = new TickInstanceMap(1, 5);
            CellKey grid = GridKey(127, 10);
            var body = new CellKey(1, new CellPositionKey(OwnerKind.Body, 9, 2, 3));
            CellInstanceHandle original = instances.Create(grid);
            instances.MarkWet(original);
            instances.Move(original, body);
            Assert.That(instances.TryGetInstance(grid, out CellInstanceHandle oldPosition), Is.False);
            Assert.That(oldPosition, Is.EqualTo(default(CellInstanceHandle)));
            Assert.That(instances.TryGetInstance(body, out CellInstanceHandle moved), Is.True);
            Assert.That(moved, Is.EqualTo(original));
            Assert.That(instances.IsWet(moved), Is.True);
            instances.Invalidate(moved);
            Assert.That(instances.TryGetInstance(body, out CellInstanceHandle removed), Is.False);
            Assert.That(removed, Is.EqualTo(default(CellInstanceHandle)));
            CellInstanceHandle replacement = instances.Create(body);
            Assert.That(instances.TryGetInstance(body, out CellInstanceHandle current), Is.True);
            Assert.That(current, Is.EqualTo(replacement));
            Assert.That(current, Is.Not.EqualTo(original));
            Assert.That(instances.IsWet(current), Is.False);
            Assert.That(instances.TryResolve(original, out _), Is.False);
        }

        [Test]
        public void M02_I01_PreparedCandidateDoesNotLeakAndSameTickMapStaysStable()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                world.BeginTick();
                ITickInstanceMap instances = world.Instances;
                CellKey key = world.OccupiedCells[0];
                Assert.That(instances.TryGetInstance(key, out CellInstanceHandle original), Is.True);
                instances.MarkWet(original);
                var context = new TransactionContext(world.Published.Version, world.WorkingTick, TickStage.Commands);
                var rejected = world.PrepareReplace(key, 101, context);
                Assert.That(rejected.Result.IsSuccess, Is.True);
                using (rejected.Prepared)
                {
                    Assert.That(instances.TryGetInstance(key, out CellInstanceHandle duringPrepare), Is.True);
                    Assert.That(duringPrepare, Is.EqualTo(original));
                    rejected.Prepared.Abort();
                }
                Assert.That(instances.IsWet(original), Is.True);
                var accepted = world.PrepareReplace(key, 101, context);
                Assert.That(accepted.Result.IsSuccess, Is.True);
                using (accepted.Prepared)
                {
                    Assert.That(accepted.Prepared.Apply(context).IsSuccess, Is.True);
                    Assert.That(instances, Is.SameAs(world.Instances));
                    Assert.That(instances.TryGetInstance(key, out CellInstanceHandle replacement), Is.True);
                    Assert.That(replacement, Is.Not.EqualTo(original));
                    Assert.That(instances.IsWet(replacement), Is.False);
                }
                Assert.That(world.PublishState().IsSuccess, Is.True);
                Assert.That(instances.TryGetInstance(key, out _), Is.False);
                world.BeginTick();
                Assert.That(world.Instances.TryGetInstance(key, out CellInstanceHandle nextTick), Is.True);
                Assert.That(nextTick.WorkingTick, Is.EqualTo(2));
                Assert.That(instances.TryResolve(nextTick, out _), Is.False);
            }
        }

        [Test]
        public void M00_02_M02_I01_RuleConsumerUsesOnlySharedSignaturesAndPreservesInstance()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                world.BeginTick();
                IWorkingWorldView snapshot = world;
                ITickInstanceMap instances = world.Instances;
                CellKey key = snapshot.OccupiedCells[0];
                Assert.That(instances.TryGetInstance(key, out CellInstanceHandle original), Is.True);
                instances.MarkWet(original);
                IRuleExecutor executor = new RuleConsumerProbe();
                var context = new TransactionContext(world.Published.Version, snapshot.WorkingTick, TickStage.Water);
                IRuleBatch batch = executor.Execute(snapshot, loaded.Materials, instances, null, context);
                Assert.That(batch.Result.IsSuccess, Is.True);
                Assert.That(batch.Stage, Is.EqualTo(TickStage.Water));
                Assert.That(batch.Intents.Length, Is.EqualTo(1));
                Assert.That(batch.Intents[0].Instance, Is.EqualTo(original));
                Assert.That(batch.Intents[0].Source, Is.EqualTo(key));
                Assert.That(batch.Intents[0].State, Is.EqualTo(Read(snapshot, key)));
                Assert.That(instances.IsWet(original), Is.True);
                Assert.That(world.ChangedPositions, Is.Zero);
                Assert.That(world.Published.Version.CommittedTick, Is.Zero);
            }
        }

        private static CellSnapshot Read(IWorkingWorldView view, CellKey key)
        {
            Assert.That(view.Read(key, out CellSnapshot state).IsSuccess, Is.True);
            return state;
        }

        // 仅为公共签名消费者探针，不实现材料规则，也不进入正式装配。
        private sealed class RuleConsumerProbe : IRuleExecutor, IRuleBatch
        {
            private readonly MutationIntent[] _intents = new MutationIntent[1];
            public RuleId RuleId => RuleId.LiquidFlow;
            public WorldResult Result { get; private set; }
            public TickStage Stage { get; private set; }
            public ReadOnlySpan<MutationIntent> Intents => _intents;

            public IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
                ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context)
            {
                CellKey key = snapshot.OccupiedCells[0];
                if (!instances.TryGetInstance(key, out CellInstanceHandle instance) ||
                    instance.Generation != snapshot.Generation || instance.WorkingTick != snapshot.WorkingTick ||
                    context.WorkingTick != snapshot.WorkingTick || context.PublishedVersion.Generation != snapshot.Generation)
                    throw new InvalidOperationException("规则输入必须共享当前 Tick 的材料实例。");
                snapshot.Read(key, out CellSnapshot state);
                _intents[0] = new MutationIntent(MutationKind.WriteState, instance, key, key, state);
                Result = WorldResult.Success();
                Stage = context.Stage;
                return this;
            }
        }
    }
}
