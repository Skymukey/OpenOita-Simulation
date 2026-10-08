using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M02
{
    public sealed class StateCoreTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private static CellKey Key(int x, int y, ulong generation = 1) => new CellKey(generation, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private TransactionContext Context => new TransactionContext(_world.Published.Version, _world.WorkingTick, TickStage.Commands);

        [SetUp]
        public void SetUp() { _loaded = new WorldSourceLoader().Load(BaselineSources.Read()); Assert.That(_loaded.Result.IsSuccess, Is.True); }
        [TearDown]
        public void TearDown() { _world?.Dispose(); _world = null; }

        private void Create(int changes = 65536, int materialCells = 65536, int width = 256, int height = 256,
            bool initialCell = false, long memoryLimit = ContractDefaults.CpuBudgetBytes)
        {
            var limits = new WorldLimits(materialCells, 64, 256, 4096, changes, 5, 180, 8, 16);
            var config = new WorldConfig(1, width, height, 128, 0.1f, 0.02f, -9.81f, 1, limits);
            var scene = FixtureCatalog.Scene(initialCell ? new[] { new InitialCell(127, 10, 104) } : Array.Empty<InitialCell>(),
                initialCell ? new[] { new Vector2Int(127, 10) } : null,
                initialCell ? new[] { new Vector2Int(127, 10) } : null);
            var loaded = new WorldLoadResult(WorldResult.Success(), config, scene, _loaded.Materials, _loaded.Rules);
            Assert.That(WorkingWorld.CreateInitial(loaded, new Vector2(3, 4), 1, out _world, memoryLimit).IsSuccess, Is.True);
        }

        private void Apply(PreparationResult<IPreparedMutation> preparation)
        {
            Assert.That(preparation.Result.IsSuccess, Is.True, preparation.Result.Diagnostic.Message);
            using (IPreparedMutation prepared = preparation.Prepared)
            {
                Assert.That(prepared.Preflight(Context).IsSuccess, Is.True);
                Assert.That(prepared.Apply(Context).IsSuccess, Is.True);
            }
        }

        [Test]
        public void M02_01_MissingReadsNeverAllocateAndTailWritesAreBounded()
        {
            using var store = new ChunkStore(129, 130);
            for (int i = 0; i < 100; i++)
            {
                Assert.That(store.Read(128, 129, out CellState state).IsSuccess, Is.True);
                Assert.That(state.MaterialId, Is.Zero);
            }
            Assert.That(store.ChunkCount, Is.Zero);
            Assert.That(store.Write(128, 129, new CellState { MaterialId = 101 }).IsSuccess, Is.True);
            Assert.That(store.ChunkCount, Is.EqualTo(1));
            foreach (var point in new[] { new Vector2Int(-1, 0), new Vector2Int(129, 129), new Vector2Int(128, 130), new Vector2Int(int.MaxValue, 0) })
            {
                Assert.That(store.Read(point.x, point.y, out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
                Assert.That(store.Write(point.x, point.y, default).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            }
            Assert.That(store.ChunkCount, Is.EqualTo(1));
            Assert.That(store.Read(128, 129, out CellState last).IsSuccess, Is.True);
            Assert.That(last.MaterialId, Is.EqualTo(101));
        }

        [Test]
        public void M02_01_LargeCanvasFarCornerReadsStaySparseAndWritesAllocateOneChunk()
        {
            const int width = 9600;
            const int height = 6400;
            const int right = width - 1;
            const int top = height - 1;
            using var store = new ChunkStore(width, height);

            for (int i = 0; i < 100; i++)
            {
                Assert.That(store.Read(right, top, out CellState empty).IsSuccess, Is.True);
                Assert.That(empty.MaterialId, Is.Zero);
            }
            Assert.That(store.ChunkCount, Is.Zero);
            Assert.That(store.StorageBytes, Is.Zero);

            Assert.That(store.Write(right, top, new CellState { MaterialId = 101 }).IsSuccess, Is.True);
            Assert.That(store.ChunkCount, Is.EqualTo(1));
            Assert.That(store.StorageBytes, Is.EqualTo(WorldChunk.StorageBytes));
            Assert.That(store.Read(right, top, out CellState written).IsSuccess, Is.True);
            Assert.That(written.MaterialId, Is.EqualTo(101));

            foreach (var point in new[]
            {
                new Vector2Int(-1, 0), new Vector2Int(width, top), new Vector2Int(right, height),
                new Vector2Int(int.MaxValue, int.MaxValue)
            })
            {
                Assert.That(store.Read(point.x, point.y, out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
                Assert.That(store.Write(point.x, point.y, new CellState { MaterialId = 102 }).ErrorCode,
                    Is.EqualTo(WorldErrorCode.OutOfBounds));
            }
            Assert.That(store.ChunkCount, Is.EqualTo(1));
        }

        [Test]
        public void M02_01_LargeCanvasFarCornerCloneKeepsBranchesIsolated()
        {
            const int right = 9599;
            const int top = 6399;
            using var original = new ChunkStore(9600, 6400);
            Assert.That(original.Write(right, top, new CellState { MaterialId = 101 }).IsSuccess, Is.True);

            using var candidate = original.Clone();
            Assert.That(candidate.ChunkCount, Is.EqualTo(1));
            Assert.That(candidate.CopiedChunks, Is.Zero);
            Assert.That(candidate.Write(right, top, new CellState { MaterialId = 103 }).IsSuccess, Is.True);
            Assert.That(candidate.CopiedChunks, Is.EqualTo(1));
            Assert.That(original.Read(right, top, out CellState originalValue).IsSuccess, Is.True);
            Assert.That(originalValue.MaterialId, Is.EqualTo(101));
            Assert.That(candidate.Read(right, top, out CellState candidateValue).IsSuccess, Is.True);
            Assert.That(candidateValue.MaterialId, Is.EqualTo(103));

            Assert.That(original.Write(right, top, new CellState { MaterialId = 104 }).IsSuccess, Is.True);
            Assert.That(original.Read(right, top, out originalValue).IsSuccess, Is.True);
            Assert.That(originalValue.MaterialId, Is.EqualTo(104));
            Assert.That(candidate.Read(right, top, out candidateValue).IsSuccess, Is.True);
            Assert.That(candidateValue.MaterialId, Is.EqualTo(103));
        }

        [Test]
        public void M02_01_ChunkMemoryFailureAndEmptyWritesDoNotAllocate()
        {
            using var store = new ChunkStore(256, 256, 0);
            Assert.That(store.Write(0, 0, default).IsSuccess, Is.True);
            Assert.That(store.Write(0, 0, new CellState { MaterialId = 101 }).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(store.ChunkCount, Is.Zero);
            store.Dispose();
            Assert.That(store.Read(0, 0, out _).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
        }

        [Test]
        public void CopyOnWrite_OnlyWrittenChunksDetachAndBothBranchesStayIndependent()
        {
            using var original = new ChunkStore();
            foreach (var p in new[] { Vector2Int.zero, new Vector2Int(128, 0), new Vector2Int(0, 128), new Vector2Int(128, 128) })
                Assert.That(original.Write(p.x, p.y, new CellState { MaterialId = 101 }).IsSuccess, Is.True);
            using var candidate = original.Clone();
            Assert.That(candidate.CopiedChunks, Is.Zero);
            Assert.That(candidate.Write(0, 0, new CellState { MaterialId = 101 }).IsSuccess, Is.True);
            Assert.That(candidate.CopiedChunks, Is.Zero, "同字节写入不分离原生数组；实例替换语义由WorkingWorld独立处理。");
            Assert.That(candidate.Write(0, 0, new CellState { MaterialId = 103 }).IsSuccess, Is.True);
            Assert.That(candidate.Write(1, 0, new CellState { MaterialId = 104 }).IsSuccess, Is.True);
            Assert.That(candidate.CopiedChunks, Is.EqualTo(1));
            original.Read(0, 0, out CellState old); Assert.That(old.MaterialId, Is.EqualTo(101));
            original.Read(1, 0, out old); Assert.That(old.MaterialId, Is.Zero);
            Assert.That(original.Write(128, 128, new CellState { MaterialId = 102 }).IsSuccess, Is.True);
            Assert.That(original.CopiedChunks, Is.EqualTo(1));
            candidate.Read(128, 128, out CellState unchanged); Assert.That(unchanged.MaterialId, Is.EqualTo(101));
            Assert.That(candidate.StorageBytes, Is.EqualTo(original.StorageBytes), "预算仍保守报告完整逻辑存储。");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CopyOnWrite_ReleaseEitherOwnerAndNestedCloneKeepsNativeStorageAlive(bool releaseOriginalFirst)
        {
            using var original = new ChunkStore();
            original.Write(127, 127, new CellState { MaterialId = 101 });
            using var candidate = original.Clone();
            using var nested = candidate.Clone();
            if (releaseOriginalFirst) { original.Dispose(); candidate.Dispose(); }
            else { candidate.Dispose(); original.Dispose(); }
            Assert.That(nested.Read(127, 127, out CellState value).IsSuccess, Is.True);
            Assert.That(value.MaterialId, Is.EqualTo(101));
            Assert.That(nested.Write(127, 127, new CellState { MaterialId = 104 }).IsSuccess, Is.True);
            Assert.That(nested.CopiedChunks, Is.Zero, "只剩一个拥有者时应直接写入。");
            nested.Dispose(); nested.Dispose();
            Assert.That(nested.Read(127, 127, out _).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
        }

        [Test]
        public void CopyOnWrite_AbortedCandidateCannotChangeSourceOrConsumeItsChunkBudget()
        {
            using var original = new ChunkStore(256, 256, WorldChunk.StorageBytes);
            original.Write(0, 0, new CellState { MaterialId = 101 });
            using (var candidate = original.Clone())
            {
                candidate.Write(0, 0, new CellState { MaterialId = 103 });
                Assert.That(candidate.Write(128, 0, new CellState { MaterialId = 102 }).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            }
            original.Read(0, 0, out CellState value); Assert.That(value.MaterialId, Is.EqualTo(101));
            original.Write(0, 0, new CellState { MaterialId = 104 });
            Assert.That(original.CopiedChunks, Is.Zero);
        }

        [Test]
        public void InstanceCopyOnWrite_IdentityAndWetChangesStayIsolatedAndAdoptionTransfersOwnership()
        {
            var map = new TickInstanceMap(1, 1);
            CellInstanceHandle instance = map.Create(Key(10, 10));
            var candidate = map.Clone();
            try
            {
                candidate.MarkWet(instance);
                Assert.That(map.IsWet(instance), Is.False);
                Assert.That(candidate.IdentityCopies, Is.Zero, "湿标记不应复制身份目录。");
                candidate.Move(instance, Key(11, 10));
                Assert.That(candidate.IdentityCopies, Is.EqualTo(1));
                Assert.That(map.TryResolve(instance, out CellKey old), Is.True);
                Assert.That(old, Is.EqualTo(Key(10, 10)));
                map.Adopt(candidate); candidate.Close(); candidate.Close();
                Assert.That(map.TryResolve(instance, out CellKey moved), Is.True);
                Assert.That(moved, Is.EqualTo(Key(11, 10)));
                Assert.That(map.IsWet(instance), Is.True);
                var other = map.Clone();
                try
                {
                    map.Invalidate(instance);
                    Assert.That(other.TryResolve(instance, out _), Is.True);
                    Assert.That(other.IsWet(instance), Is.True);
                    var replacement = map.Create(Key(11, 10));
                    Assert.That(replacement, Is.Not.EqualTo(instance));
                    Assert.That(map.IsWet(replacement), Is.False);
                }
                finally { other.Close(); }
            }
            finally { candidate.Close(); map.Close(); }
        }

        [TestCase(101, 0u, 0u, 0u)]
        [TestCase(102, 0u, 0u, 0u)]
        [TestCase(103, 0u, 0u, 200u)]
        [TestCase(104, 250u, 10u, 0u)]
        public void M02_02_InitialFieldsReplaceAndRemove(int id, uint fuel, uint spread, uint lifetime)
        {
            Create();
            _world.BeginTick();
            Apply(_world.PrepareReplace(Key(10, 10), (ushort)id, Context));
            _world.Read(Key(10, 10), out CellSnapshot state);
            Assert.That(state.FuelTicksRemaining, Is.EqualTo(fuel));
            Assert.That(state.SpreadCountdown, Is.EqualTo(spread));
            Assert.That(state.LifetimeTicksRemaining, Is.EqualTo(lifetime));
            Assert.That(state.MoveCountdown, Is.Zero);
            Assert.That(state.Flags, Is.Zero);
            Assert.That(state.IgnitedTick, Is.Zero);
            Apply(_world.Prepare(new[] { new CellWrite(Key(10, 10), default) }, Context));
            _world.Read(Key(10, 10), out state);
            Assert.That(state, Is.EqualTo(default(CellSnapshot)));
        }

        [Test]
        public void M02_02_CellStateSizeAndRoundTripPreserveEveryField()
        {
            Assert.That(Marshal.SizeOf<CellState>(), Is.LessThanOrEqualTo(32));
            Assert.That(Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<CellState>(), Is.EqualTo(32));
            Assert.That(typeof(CellState).GetField("Velocity"), Is.Null);
            var state = new CellSnapshot(104, 1, 3, 2, 0, 0, ulong.MaxValue);
            Assert.That(CellState.FromSnapshot(state).Snapshot, Is.EqualTo(state));
        }

        [Test]
        public void M02_02_CrossChunkMoveKeepsFuelWetInstanceAndFixedBinding()
        {
            Create(initialCell: true);
            _world.BeginTick();
            var burnt = new CellSnapshot(104, 1, 3, 2);
            Apply(_world.Prepare(new[] { new CellWrite(Key(127, 10), burnt) }, Context));
            Assert.That(_world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle handle), Is.True);
            _world.Instances.MarkWet(handle);
            ITickInstanceMap tickMap = _world.Instances;
            Apply(_world.PrepareMove(Key(127, 10), Key(128, 10), Context));
            _world.Read(Key(128, 10), out CellSnapshot moved);
            Assert.That(moved, Is.EqualTo(burnt));
            Assert.That(_world.Instances.TryResolve(handle, out CellKey target), Is.True);
            Assert.That(target, Is.EqualTo(Key(128, 10)));
            Assert.That(_world.Instances.IsWet(handle), Is.True);
            Assert.That(tickMap, Is.SameAs(_world.Instances));
            Assert.That(tickMap.IsWet(handle), Is.True);
            Assert.That(_world.IsFixed(Key(127, 10)), Is.False);
            Assert.That(_world.IsFixed(Key(128, 10)), Is.True);
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            Assert.That(_world.ChunkCount, Is.EqualTo(2));
        }

        [Test]
        public void BatchedMovesKeepCrossChunkStateWetInstancesAndCommittedSnapshot()
        {
            Create(initialCell: true);
            _world.BeginTick();
            Apply(_world.PrepareReplace(Key(10, 10), 103, Context));
            _world.Read(Key(127, 10), out CellSnapshot wood);
            _world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle first);
            _world.Instances.TryGetInstance(Key(10, 10), out CellInstanceHandle second);
            _world.Instances.MarkWet(first);
            var steam = new CellSnapshot(103, lifetimeTicksRemaining: 199, moveCountdown: 3);
            var moves = new[]
            {
                new MutationIntent(MutationKind.Move, first, Key(127, 10), Key(128, 10), wood),
                new MutationIntent(MutationKind.Move, second, Key(10, 10), Key(10, 11), steam)
            };
            var writes = new[] { new CellWrite(Key(127, 10), default), new CellWrite(Key(128, 10), wood),
                new CellWrite(Key(10, 10), default), new CellWrite(Key(10, 11), steam) };
            var preparation = _world.Prepare(writes, Context, moves: moves);
            Assert.That(preparation.Result.IsSuccess, Is.True, preparation.Result.Diagnostic.Message);
            Assert.That(_world.Instances.TryResolve(first, out CellKey before), Is.True);
            Assert.That(before, Is.EqualTo(Key(127, 10)), "准备不能提前搬运工作实例。");
            Apply(preparation);
            Assert.That(_world.Instances.TryResolve(first, out CellKey target), Is.True);
            Assert.That(target, Is.EqualTo(Key(128, 10)));
            Assert.That(_world.Instances.IsWet(first), Is.True);
            Assert.That(_world.IsFixed(target), Is.True);
            Assert.That(_world.Instances.TryResolve(second, out target), Is.True);
            Assert.That(target, Is.EqualTo(Key(10, 11)));
            _world.Read(target, out CellSnapshot actual);
            Assert.That(actual, Is.EqualTo(steam));
            _world.Read(Key(127, 10), out actual);
            Assert.That(actual.MaterialId, Is.Zero);
            _world.Published.Read(Key(127, 10), out actual);
            Assert.That(actual, Is.EqualTo(wood), "发布前公开快照仍为旧版本。");
            Assert.That(_world.MaterialCells, Is.EqualTo(2));
            Assert.That(_world.ChangedPositions, Is.EqualTo(4));
        }

        [Test]
        public void BatchedMovesRejectConflictingTargetsBeforeChangingAnyState()
        {
            Create();
            _world.BeginTick();
            Apply(_world.PrepareReplace(Key(0, 0), 101, Context));
            Apply(_world.PrepareReplace(Key(1, 0), 101, Context));
            _world.Instances.TryGetInstance(Key(0, 0), out CellInstanceHandle first);
            _world.Instances.TryGetInstance(Key(1, 0), out CellInstanceHandle second);
            var water = new CellSnapshot(101);
            var moves = new[]
            {
                new MutationIntent(MutationKind.Move, first, Key(0, 0), Key(2, 0), water),
                new MutationIntent(MutationKind.Move, second, Key(1, 0), Key(2, 0), water)
            };
            var preparation = _world.Prepare(new[] { new CellWrite(Key(0, 0), default),
                new CellWrite(Key(1, 0), default), new CellWrite(Key(2, 0), water) }, Context, moves: moves);
            Assert.That(preparation.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(preparation.Prepared, Is.Null);
            Assert.That(_world.Instances.TryResolve(first, out CellKey unchanged), Is.True);
            Assert.That(unchanged, Is.EqualTo(Key(0, 0)));
            Assert.That(_world.MaterialCells, Is.EqualTo(2));
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            Apply(_world.PrepareMove(Key(0, 0), Key(2, 0), Context));
        }

        [Test]
        public void M02_02_SameIdReplaceCreatesNewInstanceAndClearsFixedAndBurning()
        {
            Create(initialCell: true);
            _world.BeginTick();
            _world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle old);
            _world.Instances.MarkWet(old);
            Apply(_world.PrepareReplace(Key(127, 10), 104, Context));
            Assert.That(_world.Instances.TryResolve(old, out _), Is.False);
            Assert.That(_world.Instances.TryGetInstance(Key(127, 10), out CellInstanceHandle replacement), Is.True);
            Assert.That(replacement, Is.Not.EqualTo(old));
            Assert.That(_world.Instances.IsWet(replacement), Is.False);
            Assert.That(_world.IsFixed(Key(127, 10)), Is.False);
            _world.Read(Key(127, 10), out CellSnapshot state);
            Assert.That(state.Flags, Is.Zero);
            Assert.That(state.FuelTicksRemaining, Is.EqualTo(250));
        }

        [Test]
        public void M02_03_WholeTickDeduplicationAndExternalOverflowAreAtomic()
        {
            Create(changes: 2);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            Assert.That(_world.BeginTick().ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            Apply(_world.PrepareReplace(Key(0, 0), 101, Context));
            Apply(_world.PrepareReplace(Key(0, 0), 101, Context));
            Assert.That(_world.ChangedPositions, Is.EqualTo(1));
            var failure = _world.Prepare(new[] { new CellWrite(Key(1, 0), new CellSnapshot(101), true), new CellWrite(Key(2, 0), new CellSnapshot(101), true) }, Context);
            Assert.That(failure.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(failure.Prepared, Is.Null);
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            _world.Read(Key(1, 0), out CellSnapshot unchanged);
            Assert.That(unchanged.MaterialId, Is.Zero);
            Apply(_world.PrepareReplace(Key(1, 0), 101, Context));
            Assert.That(_world.ChangedPositions, Is.EqualTo(2));
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            _world.BeginTick();
            Assert.That(_world.ChangedPositions, Is.Zero);
        }

        [Test]
        public void M02_03_StateOnlyChangesCountAndIdenticalWriteDoesNot()
        {
            Create(initialCell: true);
            _world.BeginTick();
            _world.Read(Key(127, 10), out CellSnapshot state);
            Apply(_world.Prepare(new[] { new CellWrite(Key(127, 10), state) }, Context));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Apply(_world.Prepare(new[] { new CellWrite(Key(127, 10), new CellSnapshot(104, 1, 249, 9)) }, Context));
            Assert.That(_world.ChangedPositions, Is.EqualTo(1));
            var counter = new TickChangeCounter(3);
            var oldPosition = new CellPositionKey(OwnerKind.Grid, 0, 1, 1);
            var newPosition = new CellPositionKey(OwnerKind.Body, 2, 0, 0);
            var keys = new[] { oldPosition, oldPosition, newPosition };
            Assert.That(counter.Preflight(keys, 3).IsSuccess, Is.True);
            Assert.That(counter.ProjectedCount, Is.EqualTo(2));
            counter.Record(keys);
            Assert.That(counter.ProjectedCount, Is.EqualTo(counter.Count));
            counter.Record(keys);
            Assert.That(counter.Count, Is.EqualTo(2));
            Assert.That(counter.Preflight(new[] { new CellPositionKey(OwnerKind.Grid, 0, 3, 3) }, 2).IsSuccess, Is.False);
            Assert.That(counter.ProjectedCount, Is.EqualTo(counter.Count));
        }

        [Test]
        public void M02_03_MaterialBudgetRejectsWholeBatch()
        {
            Create(materialCells: 1);
            _world.BeginTick();
            var result = _world.Prepare(new[] { new CellWrite(Key(0, 0), new CellSnapshot(101), true), new CellWrite(Key(1, 0), new CellSnapshot(101), true) }, Context);
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(_world.ChunkCount, Is.Zero);
            Assert.That(_world.MaterialCells, Is.Zero);
        }

        [Test]
        public void M02_05_PrepareAbortSnapshotPublishAndFaultVisibility()
        {
            Create(initialCell: true);
            CommittedWorldView previous = _world.Published;
            _world.BeginTick();
            var prepared = _world.PrepareReplace(Key(127, 10), 101, Context);
            Assert.That(prepared.Result.IsSuccess, Is.True);
            _world.Read(Key(127, 10), out CellSnapshot working);
            Assert.That(working.MaterialId, Is.EqualTo(104));
            prepared.Prepared.Abort();
            prepared.Prepared.Abort();
            prepared.Prepared.Dispose();
            Assert.That(_world.ChangedPositions, Is.Zero);
            Apply(_world.PrepareReplace(Key(127, 10), 101, Context));
            previous.Read(Key(127, 10), out CellSnapshot committed);
            Assert.That(committed.MaterialId, Is.EqualTo(104));
            Assert.That(_world.PublishState().IsSuccess, Is.True);
            Assert.That(previous.Read(Key(127, 10), out _).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
            _world.Published.Read(Key(127, 10), out committed);
            Assert.That(committed.MaterialId, Is.EqualTo(101));
            _world.BeginTick();
            Apply(_world.PrepareReplace(Key(127, 10), 102, Context));
            _world.Fault();
            Assert.That(_world.Read(Key(127, 10), out _).ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(_world.Published.Version.CommittedTick, Is.EqualTo(1));
            Assert.That(_world.Published.Read(Key(127, 10), out committed).IsSuccess, Is.True);
            Assert.That(committed.MaterialId, Is.EqualTo(101));
        }

        [Test]
        public void M02_05_IllegalStateStaleKeyWrongThreadAndBusyPreparationAreRejected()
        {
            Create();
            _world.BeginTick();
            Assert.That(_world.Read(Key(0, 0, 2), out _).ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
            Assert.That(Task.Run(() => _world.Read(Key(0, 0), out _)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            foreach (CellSnapshot invalid in new[] { new CellSnapshot(0, 1), new CellSnapshot(101, 1), new CellSnapshot(104, 2, 250, 10), new CellSnapshot(103, lifetimeTicksRemaining: 0) })
                Assert.That(_world.Prepare(new[] { new CellWrite(Key(0, 0), invalid) }, Context).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(_world.PrepareReplace(Key(0, 0), 65535, Context).Result.ErrorCode, Is.EqualTo(WorldErrorCode.UnknownMaterial));
            using var candidate = _world.PrepareReplace(Key(0, 0), 101, Context).Prepared;
            Assert.That(_world.PrepareReplace(Key(1, 0), 101, Context).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            Assert.That(_world.PublishState().ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
        }

        [Test]
        public void M02_04_ProductionCreateNeverReportsPartialReady()
        {
            var factory = new WorldSimulation(rendererFactory: () => null);
            var result = factory.Create(BaselineSources.Read(), Vector2.zero);
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(result.Result.Diagnostic.Stage, Is.EqualTo("Assembly"));
            Assert.That(result.World, Is.Null);
            Assert.That(factory.Create(BaselineSources.NegativeCopy(BaselineSources.Read(), "UnknownMaterial"), Vector2.zero).Result.ErrorCode,
                Is.EqualTo(WorldErrorCode.UnknownMaterial));
            Assert.That(factory.Create(BaselineSources.Read(), new Vector2(float.NaN, 0)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
        }

        [Test]
        public void M02_07_TwentyStateRebuildsRestoreInitialAndReleaseLeases()
        {
            for (ulong generation = 1; generation <= 20; generation++)
            {
                Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, generation, out WorkingWorld state).IsSuccess, Is.True);
                CommittedWorldView lease = state.Published;
                Assert.That(state.MaterialCells, Is.EqualTo(_loaded.Scene.Cells.Count));
                Assert.That(lease.Version.Generation, Is.EqualTo(generation));
                Assert.That(lease.Version.CommittedTick, Is.Zero);
                state.Dispose();
                state.Dispose();
                Assert.That(state.ChunkCount, Is.Zero);
                Assert.That(lease.Read(Key(0, 0, generation), out _).ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
            }
            Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out WorkingWorld failed, 1).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(failed, Is.Null);
        }

        [Test]
        public void M02_02_InstanceMapTracksOwnershipAndRejectsOldTick()
        {
            var map = new TickInstanceMap(1, 5);
            CellInstanceHandle handle = map.Create(Key(0, 0));
            map.MarkWet(handle);
            var body = new CellKey(1, new CellPositionKey(OwnerKind.Body, 3, 4, 5));
            map.Move(handle, body);
            Assert.That(map.TryResolve(handle, out CellKey resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(body));
            Assert.That(map.IsWet(handle), Is.True);
            var nextTick = new TickInstanceMap(1, 6);
            Assert.That(nextTick.TryResolve(handle, out _), Is.False);
            map.Invalidate(handle);
            Assert.That(map.TryResolve(handle, out _), Is.False);
        }
    }
}
