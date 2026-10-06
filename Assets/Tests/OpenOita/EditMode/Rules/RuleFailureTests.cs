using System;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using Unity.Profiling;
using UnityEngine;

namespace OpenOita.Tests.EditMode.Rules
{
    public sealed class RuleFailureTests
    {
        [TestCase(RuleId.LiquidFlow)]
        [TestCase(RuleId.GasDrift)]
        [TestCase(RuleId.Burnable)]
        public void M03_T7_01_IntentCapacityFailureExposesNoPartialBatch(RuleId id)
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(15, 15, 103); f.Put(20, 20, 104, true); f.Begin();
            IRuleExecutor executor = id == RuleId.LiquidFlow ? new LiquidFlowRule(8, intentCapacity: 0) :
                id == RuleId.GasDrift ? new GasDriftRule(8, intentCapacity: 0) : new BurnRule(8, intentCapacity: 0);
            TickStage stage = id == RuleId.LiquidFlow ? TickStage.Water : id == RuleId.GasDrift ? TickStage.Steam : TickStage.Burning;
            var before = f.Cells.ToArray();
            IRuleBatch batch = f.Run(executor, stage);
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(batch.Intents.Length, Is.Zero);
            Assert.That(batch.Result.Diagnostic.Stage, Is.EqualTo(stage.ToString()));
            Assert.That(batch.Result.Diagnostic.Target, Does.Contain("Grid/0/"));
            CollectionAssert.AreEquivalent(before, f.Cells);
        }

        [Test]
        public void M03_T7_01_ScanAndResolvedOutputCapacitiesRejectWholeBatch()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(15, 15, 101); f.Begin();
            IRuleBatch tooSmall = f.Run(new LiquidFlowRule(1), TickStage.Water);
            Assert.That(tooSmall.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(tooSmall.Intents.Length, Is.Zero);
            IRuleBatch batch = new MovementCandidateResolver(2, intentCapacity: 1).Resolve(f.Run(f.Rules.Water, TickStage.Water));
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(batch.Intents.Length, Is.Zero);
            Assert.That(f.Cells.Count, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void M03_T7_01_MissingOrClosedInstancesFailWithCellDiagnostic(bool close)
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Begin();
            f.Instances.TryGetInstance(RuleFixture.Grid(5, 5), out CellInstanceHandle instance);
            if (close) f.Instances.Close(); else f.Instances.Invalidate(instance);
            IRuleBatch batch = f.Run(f.Rules.Water, TickStage.Water);
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
            Assert.That(batch.Result.Diagnostic.Target, Is.EqualTo("Grid/0/5,5"));
            Assert.That(batch.Intents.Length, Is.Zero);
        }

        [Test]
        public void M03_T7_01_WrongTickGenerationAndStageRejectBeforeEmission()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Begin();
            foreach (TransactionContext context in new[]
            {
                new TransactionContext(new WorldVersion(2, 0), 1, TickStage.Water),
                new TransactionContext(new WorldVersion(1, 0), 2, TickStage.Water),
                new TransactionContext(new WorldVersion(1, ulong.MaxValue), 1, TickStage.Water),
                f.Context(TickStage.Steam)
            })
            {
                IRuleBatch batch = f.Rules.Water.Execute(f, f.Materials, f.Instances, null, context);
                Assert.That(batch.Result.IsSuccess, Is.False); Assert.That(batch.Intents.Length, Is.Zero);
            }
        }

        [Test]
        public void M03_T7_01_ContactBufferFailureClearsWetHandlesAndBurnIntents()
        {
            var f = new RuleFixture(); CellKey body = RuleFixture.Body(2, 0, 0), target = RuleFixture.Grid(10, 10);
            f.Put(body, 104, true); f.Put(target, 104); f.Put(11, 10, 101);
            f.Cells[body] = new CellSnapshot(104, 1, 5, 1);
            f.BodyItems = new[] { new BodySnapshot(2, default, default, default, 1) };
            var contacts = new ContactFixture(); contacts.Items.Add(new CellContact(body, target, ContactFeature.EdgeEdge, 0));
            contacts.Items.Add(new CellContact(body, RuleFixture.Grid(11, 10), ContactFeature.EdgeEdge, 0));
            f.Contacts = contacts; f.Begin();
            var policy = new WetContactPolicy(8, contactCapacity: 0);
            WorldResult wet = policy.Collect(f, f.Materials, f.Instances, contacts, f.Context(TickStage.PreFlowContacts));
            Assert.That(wet.ErrorCode, Is.EqualTo(WorldErrorCode.BufferTooSmall));
            Assert.That(policy.WetInstances.Length, Is.Zero);
            IRuleBatch batch = f.Run(new BurnRule(8, contactCapacity: 0), TickStage.Burning);
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.BufferTooSmall));
            Assert.That(batch.Intents.Length, Is.Zero);
            Assert.That(f.State(body).FuelTicksRemaining, Is.EqualTo(5));
        }

        [Test]
        public void M03_T7_01_OccupancyFailureAndStaleVersionCannotBecomeEmptyTargets()
        {
            var occupancy = new OccupancyFixture { Result = RuleFixture.Error(WorldErrorCode.Faulted) };
            var f = new RuleFixture(occupancy: occupancy); f.Put(5, 5, 101); f.Begin();
            Assert.That(f.Run(f.Rules.Water, TickStage.Water).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            occupancy.Result = WorldResult.Success(); occupancy.Version = new WorldVersion(1, 99);
            IRuleBatch batch = f.Run(f.Rules.Water, TickStage.Water);
            Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
            Assert.That(batch.Intents.Length, Is.Zero);
        }

        [Test]
        public void M03_A_RulesNeverMutateSnapshotOrInstanceMap()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(15, 15, 103); f.Put(20, 20, 104, true); f.Begin();
            var readOnly = new ReadOnlyInstances(f.Instances); var before = f.Cells.ToArray();
            foreach (var pair in new[]
            {
                (RuleId.LiquidFlow, TickStage.Water), (RuleId.GasDrift, TickStage.Steam), (RuleId.Burnable, TickStage.Burning)
            })
            {
                f.Rules.TryGetExecutor(pair.Item1, out IRuleExecutor executor);
                IRuleBatch batch = executor.Execute(f, f.Materials, readOnly, null, f.Context(pair.Item2));
                Assert.That(batch.Result.IsSuccess, Is.True);
                foreach (MutationIntent intent in batch.Intents)
                {
                    Assert.That(f.Instances.TryGetInstance(intent.Source, out CellInstanceHandle instance), Is.True);
                    Assert.That(intent.Instance, Is.EqualTo(instance));
                }
            }
            Assert.That(f.Rules.WetContacts.Collect(f, f.Materials, readOnly, null, f.Context(TickStage.PreFlowContacts)).IsSuccess, Is.True);
            Assert.That(f.Rules.WetContacts.Execute(f, f.Materials, readOnly, null, f.Context(TickStage.Extinguish)).Result.IsSuccess, Is.True);
            CollectionAssert.AreEquivalent(before, f.Cells);
        }

        [Test]
        public void M03_A_ExecutionTableUsesOneRegistryAndNoPerCellCapabilityExecutor()
        {
            var table = new RuleExecutionTable(16);
            Assert.That(table.Validate(new RuleRegistry()).IsSuccess, Is.True);
            foreach (RuleId id in new[] { RuleId.LiquidFlow, RuleId.GasDrift, RuleId.Burnable })
            {
                Assert.That(table.TryGetExecutor(id, out IRuleExecutor executor), Is.True);
                Assert.That(executor.RuleId, Is.EqualTo(id));
            }
            Assert.That(table.TryGetExecutor(RuleId.Structure, out _), Is.False);
            Assert.That(table.TryGetExecutor(RuleId.ExtinguishesFire, out _), Is.False);
            Assert.That(table.TryGetExecutor((RuleId)99, out _), Is.False);
            Assert.That(table.Validate(new BrokenRegistry()).ErrorCode, Is.EqualTo(WorldErrorCode.IncompatibleRule));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RuleExecutionTable(1000000));
        }

        [Test]
        public void M03_A_KindWithoutExplicitRulesDoesNotExecuteImplicitBehaviour()
        {
            var f = new RuleFixture(); f.Put(5, 5, 101); f.Put(15, 15, 103); f.Put(20, 20, 104, true); f.Begin();
            var materials = new NoRulesTable(f.Materials);
            foreach (var pair in new[]
            {
                (RuleId.LiquidFlow, TickStage.Water), (RuleId.GasDrift, TickStage.Steam), (RuleId.Burnable, TickStage.Burning)
            })
            {
                f.Rules.TryGetExecutor(pair.Item1, out IRuleExecutor executor);
                IRuleBatch batch = executor.Execute(f, materials, f.Instances, null, f.Context(pair.Item2));
                Assert.That(batch.Result.IsSuccess, Is.True); Assert.That(batch.Intents.Length, Is.Zero);
            }
        }

        [Test]
        public void M03_T7_01_RuleFailureDoesNotWriteM02StateAndManualFaultRetainsOldView()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(WorkingWorld.CreateInitial(loaded, Vector2.zero, 1, out WorkingWorld world).IsSuccess, Is.True);
            using (world)
            {
                world.BeginTick(); CellKey key = world.OccupiedCells[0];
                var context = new TransactionContext(world.Published.Version, world.WorkingTick, TickStage.Water);
                IRuleBatch batch = new LiquidFlowRule(1).Execute(world, loaded.Materials, world.Instances, null, context);
                Assert.That(batch.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
                Assert.That(world.ChangedPositions, Is.Zero); Assert.That(world.Published.Version.CommittedTick, Is.Zero);
                world.Fault();
                Assert.That(world.Read(key, out _).ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
                Assert.That(world.Published.Read(key, out _).IsSuccess, Is.True);
                Assert.That(world.Published.Version.CommittedTick, Is.Zero);
                TestContext.WriteLine("真实 M02 状态核心+手动 Fault 探针；未执行正式 Step/显示链路");
            }
        }

        [Test]
        public void M03_A_WarmedRuleAllocationRequiresWorkingPositiveControl()
        {
            var f = new RuleFixture(occupancy: new OccupancyFixture());
            f.Put(5, 5, 101); f.Put(15, 15, 103); f.Put(20, 20, 104, true); f.Put(21, 20, 104);
            f.Cells[RuleFixture.Grid(20, 20)] = new CellSnapshot(104, 1, 5, 1);
            CellKey body = RuleFixture.Body(2, 0, 0); f.Put(body, 104, true);
            f.Cells[body] = new CellSnapshot(104, 1, 5, 1);
            f.BodyItems = new[] { new BodySnapshot(2, default, default, default, 1) };
            var contacts = new ContactFixture(); contacts.Items.Add(new CellContact(body, RuleFixture.Grid(21, 20), ContactFeature.VertexEdge, 0));
            f.Contacts = contacts;
            f.Begin(); var water = f.Context(TickStage.Water); var steam = f.Context(TickStage.Steam);
            var burn = f.Context(TickStage.Burning); var wet = f.Context(TickStage.Extinguish);
            for (int i = 0; i < 100; i++) Execute();
            long controlBefore = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[8192];
            probe[0] = 1;
            long control = GC.GetAllocatedBytesForCurrentThread() - controlBefore;
            GC.KeepAlive(probe);
            if (control < 8192)
            {
                long profilerControl = 0;
                using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 32,
                    ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.CollectOnlyOnCurrentThread))
                {
                    var profilerProbe = new byte[8192];
                    profilerProbe[0] = 1;
                    GC.KeepAlive(profilerProbe);
                    recorder.Stop();
                    for (int i = 0; i < recorder.Count; i++) profilerControl += recorder.GetSample(i).Value;
                }
                Assert.Ignore($"未测：当前 Editor .NET 分配计数器正对照={control}字节；同步 GC.Alloc 正对照={profilerControl}字节；未获得可靠的逐调用分配证据。需在 Player/Profiler 按 M09 复测。");
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Execute();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.WriteLine($"1000轮固定5格阶段输入（含传播及跨归属接触夹具），规则/裁决分配={allocated}字节；不含夹具提交与完整 Step");
            Assert.That(allocated, Is.Zero);
            void Execute()
            {
                f.Rules.Movement.Resolve(f.Rules.Water.Execute(f, f.Materials, f.Instances, null, water));
                f.Rules.Movement.Resolve(f.Rules.Steam.Execute(f, f.Materials, f.Instances, null, steam));
                f.Rules.Burning.Execute(f, f.Materials, f.Instances, contacts, burn);
                f.Rules.WetContacts.Collect(f, f.Materials, f.Instances, contacts, wet);
                f.Rules.WetContacts.Execute(f, f.Materials, f.Instances, contacts, wet);
            }
        }

        private sealed class BrokenRegistry : IRuleRegistry
        {
            public int Count => 5;
            public bool TryGet(RuleId id, out RuleDescriptor descriptor) { descriptor = default; return false; }
        }
        private sealed class NoRulesTable : IMaterialRuntimeTable
        {
            private readonly IMaterialRuntimeTable _inner;
            public string MaterialSetId => _inner.MaterialSetId;
            public int Count => _inner.Count;
            internal NoRulesTable(IMaterialRuntimeTable inner) { _inner = inner; }
            public bool TryGet(ushort id, out MaterialRuntimeEntry entry)
            {
                bool found = _inner.TryGet(id, out MaterialRuntimeEntry original);
                entry = new MaterialRuntimeEntry(original.Id, original.CompactIndex, original.Name, original.Kind,
                    original.Color, original.MassPerCell, RuleMask.None, original.Parameters);
                return found;
            }
            public MaterialRuntimeEntry GetByCompactIndex(ushort index) { TryGet(_inner.GetByCompactIndex(index).Id, out MaterialRuntimeEntry value); return value; }
        }
    }
}
