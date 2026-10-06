using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M02
{
    public sealed class StructurePreparationTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private static CellKey Key(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private TransactionContext Context(TickStage stage = TickStage.Commands) =>
            new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        private void Create(bool cross = false)
        {
            WorldLoadResult baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Assert.That(baseline.Result.IsSuccess, Is.True);
            var cells = new List<InitialCell>();
            if (cross)
            {
                for (int n = 2; n <= 6; n++)
                {
                    cells.Add(new InitialCell(n, 4, 104));
                    if (n != 4) cells.Add(new InitialCell(4, n, 104));
                }
            }
            else for (int x = 126; x <= 128; x++) cells.Add(new InitialCell(x, 10, 104));
            var limits = new WorldLimits(128, cross ? 3 : 64, 256, 4096, 128, 5, 180, 8, 16);
            var config = new WorldConfig(1, 129, 130, 128, 0.1f, 0.02f, -9.81f, 1, limits);
            var scene = FixtureCatalog.Scene(cells, new[] { cross ? new Vector2Int(4, 4) : new Vector2Int(126, 10) });
            _loaded = new WorldLoadResult(WorldResult.Success(), config, scene, baseline.Materials, baseline.Rules);
            Assert.That(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world).IsSuccess, Is.True);
        }

        [TearDown]
        public void TearDown() { _world?.Dispose(); }

        [Test]
        public void M04_I01_I02_PublicPlanCopiesAndNormalizesRetirementsAndExplicitDisposition()
        {
            var members = new List<CellKey> { Key(1, 1) };
            var component = new StructureComponent(Key(1, 1).Position, "building", false,
                StructureDisposition.ExtractFreeGrid, members);
            var components = new List<StructureComponent> { component };
            var retired = new List<ulong> { 9, 2, 9, ulong.MaxValue };
            var plan = new StructurePlan(components, retired);
            members.Clear(); components.Clear(); retired.Clear();
            Assert.That(plan.Components.Single().Members.Single(), Is.EqualTo(Key(1, 1)));
            Assert.That(plan.Components.Single().Disposition, Is.EqualTo(StructureDisposition.ExtractFreeGrid));
            Assert.That(plan.RetiredBodyIds, Is.EqualTo(new[] { 2UL, 9UL, ulong.MaxValue }));
            Assert.Throws<NotSupportedException>(() => ((IList<ulong>)plan.RetiredBodyIds).Add(4));
            Assert.Throws<ArgumentException>(() => new StructurePlan(Array.Empty<StructureComponent>(), new[] { 0UL }));
        }

        [Test]
        public void M04_I03_WriteStateRetainsFixedAndCandidateKeyValidation()
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            _world.Read(Key(126, 10), out CellSnapshot old);
            var state = new CellSnapshot(old.MaterialId, old.Flags, old.FuelTicksRemaining - 1, old.SpreadCountdown);
            using IPreparedMutation prepared = _world.Prepare(new[] { new CellWrite(Key(126, 10), state) }, Context()).Prepared;
            Assert.That(prepared.CandidateWorld.IsFixed(Key(126, 10)), Is.True);
            Assert.That(_world.PlanStructure(prepared, new ConnectivityAnalyzer(128, 64), Context()).Plan.Components.Single().Disposition,
                Is.EqualTo(StructureDisposition.RetainFixedGrid));
            Assert.That(prepared.CandidateWorld.Read(Key(129, 0), out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            var stale = new CellKey(2, Key(126, 10).Position);
            Assert.That(prepared.CandidateWorld.Read(stale, out _).ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
            _world.Read(Key(126, 10), out CellSnapshot unchanged);
            Assert.That(unchanged, Is.EqualTo(old));
        }

        [Test]
        public void M04_I03_InitialAndRemoveArePlannedBeforeMaterialApply()
        {
            Create();
            var planner = new ConnectivityAnalyzer(128, 64);
            StructurePlanResult initial = _world.PlanInitialStructure(planner);
            Assert.That(initial.Result.IsSuccess, Is.True);
            Assert.That(initial.Plan.Components.Single().Disposition, Is.EqualTo(StructureDisposition.RetainFixedGrid));
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            using IPreparedMutation prepared = _world.Prepare(new[] { new CellWrite(Key(127, 10), default) }, Context()).Prepared;
            IWorkingWorldView candidate = prepared.CandidateWorld;
            Assert.That(candidate.Read(Key(127, 10), out CellSnapshot removed).IsSuccess, Is.True);
            Assert.That(removed.MaterialId, Is.Zero);
            Assert.That(candidate.OccupiedCells.Length, Is.EqualTo(2));
            Assert.That(candidate.Origin, Is.EqualTo(_world.Origin));
            Assert.That(candidate.WorkingTick, Is.EqualTo(_world.WorkingTick));
            Assert.That(candidate.Bodies.Length, Is.Zero);
            StructurePlanResult result = _world.PlanStructure(prepared, planner, Context());
            Assert.That(result.Result.IsSuccess, Is.True);
            Assert.That(result.Plan.Components.Select(item => item.Disposition), Is.EqualTo(new[] {
                StructureDisposition.RetainFixedGrid, StructureDisposition.ExtractFreeGrid }));
            _world.Read(Key(127, 10), out CellSnapshot old);
            Assert.That(old.MaterialId, Is.EqualTo(104));
            Assert.That(_world.ChangedPositions, Is.Zero);
            using var transaction = new TransactionCoordinator();
            transaction.Own(prepared);
            Assert.That(transaction.ValidateAndApply(Context(), _world.Config.Limits, ContractDefaults.CpuBudgetBytes).IsSuccess, Is.True);
            Assert.That(candidate.Read(Key(127, 10), out _).ErrorCode, Is.EqualTo(WorldErrorCode.NotReady));
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
            _world.Published.Read(Key(127, 10), out CellSnapshot published);
            Assert.That(published.MaterialId, Is.EqualTo(104));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void M04_I03_SameIdReplaceAndSpawnClearCandidateFixedWithoutChangingOldInstance(bool spawn)
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            CellKey key = spawn ? Key(10, 10) : Key(126, 10);
            _world.Instances.TryGetInstance(Key(126, 10), out CellInstanceHandle old);
            _world.Instances.MarkWet(old);
            using IPreparedMutation prepared = _world.PrepareReplace(key, 104, Context()).Prepared;
            Assert.That(prepared.CandidateWorld.IsFixed(key), Is.False);
            StructurePlanResult result = _world.PlanStructure(prepared, new ConnectivityAnalyzer(128, 64), Context());
            Assert.That(result.Result.IsSuccess, Is.True);
            Assert.That(result.Plan.Components[0].Disposition, Is.EqualTo(StructureDisposition.ExtractFreeGrid));
            Assert.That(_world.IsFixed(Key(126, 10)), Is.True);
            Assert.That(_world.Instances.TryResolve(old, out _), Is.True);
            Assert.That(_world.Instances.IsWet(old), Is.True);
            prepared.Abort();
            Assert.That(_world.IsFixed(Key(126, 10)), Is.True);
            Assert.That(_world.Instances.TryResolve(old, out _), Is.True);
        }

        [Test]
        public void M04_I03_RealBurnedOutBatchPlansCandidateAndPreservesSurvivingFuel()
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            var context = Context(TickStage.Burning);
            using (IPreparedMutation seed = _world.Prepare(new[] {
                new CellWrite(Key(127, 10), new CellSnapshot(104, 1, 1, 2)),
                new CellWrite(Key(128, 10), new CellSnapshot(104, 1, 3, 2)) }, context).Prepared)
                Assert.That(seed.Apply(context).IsSuccess, Is.True);
            _world.Instances.TryGetInstance(Key(128, 10), out CellInstanceHandle survivor);
            IRuleBatch batch = new BurnRule(128).Execute(_world, _loaded.Materials, _world.Instances, null, context);
            Assert.That(batch.Result.IsSuccess, Is.True);
            var writes = new List<CellWrite>();
            bool burnedOut = false;
            foreach (MutationIntent intent in batch.Intents)
            {
                writes.Add(new CellWrite(intent.Source, intent.State));
                burnedOut |= intent.Kind == MutationKind.Remove && intent.RemovalReason == RemovalReason.BurnedOut;
            }
            Assert.That(burnedOut, Is.True);
            using IPreparedMutation prepared = _world.Prepare(writes.ToArray(), context).Prepared;
            StructurePlanResult result = _world.PlanStructure(prepared, new ConnectivityAnalyzer(128, 64), context);
            Assert.That(result.Result.IsSuccess, Is.True);
            Assert.That(result.Plan.Components.Count, Is.EqualTo(2));
            prepared.CandidateWorld.Read(Key(128, 10), out CellSnapshot state);
            Assert.That(state, Is.EqualTo(new CellSnapshot(104, 1, 2, 1)));
            _world.Read(Key(127, 10), out CellSnapshot bridge);
            Assert.That(bridge.FuelTicksRemaining, Is.EqualTo(1));
            Assert.That(_world.Instances.TryGetInstance(Key(128, 10), out CellInstanceHandle current), Is.True);
            Assert.That(current, Is.EqualTo(survivor));
        }

        [Test]
        public void M04_T4_02_FullFourArmPlanIsRejectedAtomicallyAtBodyLimitThree()
        {
            Create(true);
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            CellKey center = Key(4, 4);
            _world.Instances.TryGetInstance(center, out CellInstanceHandle old);
            using IPreparedMutation prepared = _world.Prepare(new[] { new CellWrite(center, default) }, Context()).Prepared;
            StructurePlanResult result = _world.PlanStructure(prepared, new ConnectivityAnalyzer(128, 3), Context());
            Assert.That(result.Result.IsSuccess, Is.True);
            Assert.That(result.Plan.Components.Count, Is.EqualTo(4));
            Assert.That(prepared.Budget.DynamicBodies, Is.EqualTo(4));
            using var transaction = new TransactionCoordinator();
            transaction.Own(prepared);
            Assert.That(transaction.ValidateAndApply(Context(), _world.Config.Limits, ContractDefaults.CpuBudgetBytes).ErrorCode,
                Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(transaction.RequiresFault, Is.False);
            Assert.That(prepared.State, Is.EqualTo(PreparationState.Aborted));
            Assert.That(_world.IsFixed(center), Is.True);
            Assert.That(_world.MaterialCells, Is.EqualTo(9));
            Assert.That(_world.ChangedPositions, Is.Zero);
            Assert.That(_world.Instances.TryResolve(old, out _), Is.True);
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
            // 没有 BodyId 分配器；此测试只证明 M02/M04 预算边界，不声称验证 M05 预留号段。
        }

        private sealed class InjectFailure : IFailureInjector
        {
            public WorldResult Check(in TransactionContext context, FailurePoint point) =>
                point == FailurePoint.AfterStructurePrepared ? WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("Structure", "injection", "结构准备后故障注入")) : WorldResult.Success();
        }

        [TestCase(TickStage.Commands, false)]
        [TestCase(TickStage.Burning, false)]
        [TestCase(TickStage.Commands, true)]
        [TestCase(TickStage.Burning, true)]
        public void M02_05_StructureFailureAbortsCommandOrFaultsRuleBeforeApply(TickStage stage, bool inject)
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            var context = Context(stage);
            _world.Instances.TryGetInstance(Key(126, 10), out CellInstanceHandle old);
            using IPreparedMutation prepared = _world.PrepareReplace(Key(126, 10), 104, context).Prepared;
            var planner = new ConnectivityAnalyzer(inject ? 128 : 2, 64);
            StructurePlanResult result = _world.PlanStructure(prepared, planner, context, inject ? new InjectFailure() : null);
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(result.Plan, Is.Null);
            Assert.That(prepared.State, Is.EqualTo(PreparationState.Aborted));
            Assert.That(_world.Published.Version.CommittedTick, Is.Zero);
            _world.Published.Read(Key(126, 10), out CellSnapshot published);
            Assert.That(published.MaterialId, Is.EqualTo(104));
            Assert.That(_world.Read(Key(126, 10), out _).ErrorCode,
                Is.EqualTo(stage == TickStage.Commands ? WorldErrorCode.None : WorldErrorCode.Faulted));
            if (stage == TickStage.Commands)
            {
                Assert.That(_world.IsFixed(Key(126, 10)), Is.True);
                Assert.That(_world.Instances.TryResolve(old, out _), Is.True);
                using IPreparedMutation retry = _world.PrepareReplace(Key(126, 10), 104, context).Prepared;
                Assert.That(_world.PlanStructure(retry, new ConnectivityAnalyzer(128, 64), context).Result.IsSuccess, Is.True);
            }
        }

        [TestCase("abort", WorldErrorCode.NotReady)]
        [TestCase("dispose", WorldErrorCode.Disposed)]
        [TestCase("fault", WorldErrorCode.Faulted)]
        public void M04_I03_CandidateLeaseClosesWithItsOwner(string action, WorldErrorCode code)
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            using IPreparedMutation prepared = _world.PrepareReplace(Key(126, 10), 104, Context()).Prepared;
            IWorkingWorldView candidate = prepared.CandidateWorld;
            if (action == "abort") prepared.Abort();
            else if (action == "dispose") _world.Dispose();
            else _world.Fault();
            Assert.That(candidate.Read(Key(126, 10), out _).ErrorCode, Is.EqualTo(code));
            Assert.Throws<InvalidOperationException>(() => { int count = candidate.OccupiedCells.Length; });
            Assert.That(new ConnectivityAnalyzer(128, 64).Plan(candidate, _loaded.Materials, Array.Empty<CellKey>()).Plan, Is.Null);
        }

        [Test]
        public void M04_I03_CandidateRejectsWrongThreadAndTransactionContext()
        {
            Create();
            Assert.That(_world.BeginTick().IsSuccess, Is.True);
            using IPreparedMutation prepared = _world.PrepareReplace(Key(126, 10), 104, Context()).Prepared;
            Assert.That(Task.Run(() => prepared.CandidateWorld.Read(Key(126, 10), out _).ErrorCode).GetAwaiter().GetResult(),
                Is.EqualTo(WorldErrorCode.InvalidArgument));
            StructurePlanResult wrong = _world.PlanStructure(prepared, new ConnectivityAnalyzer(128, 64), Context(TickStage.Burning));
            Assert.That(wrong.Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
            Assert.That(wrong.Plan, Is.Null);
            Assert.That(prepared.State, Is.EqualTo(PreparationState.Prepared));
        }
    }
}
