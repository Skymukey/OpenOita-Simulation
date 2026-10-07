using System;
using System.Collections.Generic;
using System.Threading;
using OpenOita.Contracts;
using OpenOita.Rules;
using OpenOita.PhysicsAdapter;
using OpenOita.Spatial;
using OpenOita.Structure;
using UnityEngine;

namespace OpenOita.Simulation
{
    // 正式 M02 阶段调度；由 WorldSimulation 装配，不属于 Preview。
    internal sealed class WorldRuntime : IDisposable
    {
        private readonly WorldLoadResult _loaded;
        private readonly Vector2 _origin;
        private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
        private readonly List<TransactionCoordinator> _transactions = new();
        private readonly FluidDisplacementPlanner _displacement = new();
        private ConnectivityAnalyzer _structure;
        private MaterialMutationPreparer _material;
        private RuleExecutionTable _rules;
        private bool _disposed;
        internal WorkingWorld State { get; private set; }
        internal WorldPhysicsAdapter Physics { get; private set; }
        internal WorldOccupancyIndex Spatial { get; private set; }
        private WorldOccupancyIndex _after;
        internal IFailureInjector Failures;
        internal ITransactionalWorldRenderer Renderer;
        internal long ExternalCpuBytes;
        internal TickChangeSet LastChanges { get; private set; }
        private readonly Dictionary<CellKey, CellInstanceHandle> _originalInstances = new();
        private readonly Dictionary<CellInstanceHandle, CellChangeKind> _removals = new();
        private readonly Dictionary<CellKey, CellInstanceHandle> _replacements = new();
        private readonly Dictionary<CellInstanceHandle, CellKey> _originOfInstance = new();
        // 仅测试使用固定N；正式入口始终执行D08规划。
        internal int FixedSubsteps;
        internal Action BeforePhysicsForTest;
        internal readonly WorldStepMetrics Metrics = new();
        internal Action<IPhysicsStepView> AfterPhysicsCandidateForTest;
        internal long CpuBudgetBytes = ContractDefaults.CpuBudgetBytes;
        internal int LastSubsteps { get; private set; }
        internal double SimulatedSeconds { get; private set; }
        internal bool Faulted { get; private set; }
        internal WorldResult LastResult { get; private set; } = WorldResult.Success();
        internal ICommittedRenderView View => State.Published;
        internal long ReservedCpuBytes => State.EstimatedCpuBytes + Physics.ReservedCpuBytes + Spatial.ReservedCpuBytes + _after.ReservedCpuBytes +
            ExternalCpuBytes + (_rules?.ReservedCpuBytes ?? 0) + State.Config.Limits.MaxMaterialCells * 256L + FluidDisplacementPlanner.EstimateCpuBytes(State.Config, State.MaterialCells) + 8192L;

        private WorldRuntime(WorldLoadResult loaded, Vector2 origin) { _loaded = loaded; _origin = origin; }
        internal static WorldResult Create(WorldLoadResult loaded, Vector2 origin, ulong generation, out WorldRuntime runtime, IFailureInjector failures = null, bool freezeBodyRotation = false)
        {
            runtime = null;
            var candidate = new WorldRuntime(loaded, origin) { Failures = failures };
            try
            {
                Require(WorkingWorld.CreateInitial(loaded, origin, generation, out WorkingWorld state));
                candidate.State = state;
                candidate.Spatial = new WorldOccupancyIndex(); candidate._after = new WorldOccupancyIndex();
                candidate._structure = new ConnectivityAnalyzer(loaded.Config.Limits.MaxMaterialCells, loaded.Config.Limits.MaxDynamicBodies);
                candidate._material = new MaterialMutationPreparer(loaded.Materials);
                candidate.Physics = new WorldPhysicsAdapter(loaded.Config, origin, generation, freezeBodyRotation);
                candidate.Apply(state.PrepareInitial(), candidate.Context(TickStage.Structure), true);
                candidate.EnsureRules();
                candidate.Refresh(candidate.Spatial, TickStage.Structure);
                WorldResult injected = failures?.Check(candidate.Context(TickStage.Structure), FailurePoint.AfterSpatialPrepared) ?? WorldResult.Success();
                Require(injected);
                Require(PhysicsGeometryValidator.Validate(state.Config, origin, candidate.Spatial, candidate.Spatial));
                // 初态也不能留固体覆盖水汽的未处理结果。
                Require(candidate._displacement.CollectCovered(state, loaded.Materials, candidate.Spatial, out CellKey[] initialCovered));
                if (initialCovered.Length != 0) throw new RuntimeFailure(WorldResult.Failure(WorldErrorCode.Occupied, new WorldDiagnostic("Create", "fluids", "初态固体覆盖水汽。")));
                candidate.Publish();
                runtime = candidate;
                return WorldResult.Success();
            }
            catch (RuntimeFailure exception) { return exception.Result; }
            catch (Exception exception) { return WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("Create", "M06", exception.Message)); }
            finally { if (runtime == null) candidate.Dispose(); }
        }

        internal TransactionContext Context(TickStage stage, CommandToken token = default) => new TransactionContext(View.Version, State.WorkingTick, stage, token);
        private static void Require(WorldResult result) { if (!result.IsSuccess) throw new RuntimeFailure(result); }
        private sealed class RuntimeFailure : Exception
        {
            internal readonly WorldResult Result;
            internal RuntimeFailure(WorldResult result) : base(result.Diagnostic.Message) { Result = result; }
        }
        private void EnsureRules()
        {
            int capacity = Math.Max(1, State.MaterialCells);
            if (_rules != null && _rules.CellCapacity >= capacity) return;
            // 按当前实际材料容量预留，增长前合并预算；不把MaxMaterialCells当已分配规则缓冲。
            long ruleBytes = capacity * 3268L + capacity * 256L + 4096 + LiquidSpreadPlanner.ReservedBytes(capacity);
            long withoutRules = ReservedCpuBytes;
            if (withoutRules + ruleBytes > CpuBudgetBytes)
                throw new RuntimeFailure(WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Preflight", "ruleCpuBytes", "规则、状态、空间及物理保留工作集超过统一CPU预算。")));
            _rules = new RuleExecutionTable(capacity, Spatial);
            Require(_rules.Validate(_loaded.Rules));
        }
        internal void Refresh(WorldOccupancyIndex index, TickStage stage, ReadOnlySpan<BodySnapshot> poses = default)
        {
            long revision = State.Revision; ulong tick = State.WorkingTick;
            var context = Context(stage);
            bool reuse = index.CanRebind(State, _loaded.Materials, State.Instances, context, revision, poses);
            if (ReservedCpuBytes + (reuse ? 0 : WorldOccupancyIndex.EstimateCpuBytes(State.MaterialCells)) > CpuBudgetBytes)
                throw new RuntimeFailure(WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Preflight", "spatialCpuBytes", "空间索引重建前的保留及候选工作集超过统一CPU预算。")));
            Require(index.Refresh(State, _loaded.Materials, State.Instances, context, revision, () => State.IsLeaseValid(revision, tick), poses));
        }

        private void Apply(PreparationResult<IPreparedMutation> prepared, in TransactionContext context, bool structural)
        {
            Require(prepared.Result);
            var transaction = new TransactionCoordinator();
            try
            {
                transaction.Own(prepared.Prepared);
                if (structural)
                {
                    StructurePlanResult plan = State.PlanStructure(prepared.Prepared, _structure, context, Failures);
                    Require(plan.Result);
                    var material = State.PrepareMaterial(prepared.Prepared, plan.Plan, _material, context, Failures);
                    Require(material.Result);
                    Require(Failures?.Check(context, FailurePoint.AfterMaterialPrepared) ?? WorldResult.Success());
                    var physics = Physics.Prepare(material.Prepared.Geometry, material.Prepared.BodyMappings, material.Prepared.Budget, context, Failures);
                    Require(physics.Result);
                    transaction.Own(physics.Prepared);
                }
                else if (prepared.Prepared.CandidateWorld.Bodies.Length != 0)
                {
                    Require(State.PrepareStateBodies(prepared.Prepared, State.Bodies).Result);
                }
                long otherBytes = ExternalCpuBytes + (_rules?.ReservedCpuBytes ?? 0) + Spatial.ReservedCpuBytes + _after.ReservedCpuBytes + State.Config.Limits.MaxMaterialCells * 256L + FluidDisplacementPlanner.EstimateCpuBytes(State.Config, State.MaterialCells);
                if (!structural) otherBytes += Physics.ReservedCpuBytes;
                Require(transaction.ValidateAndApply(context, State.Config.Limits, CpuBudgetBytes - otherBytes, Failures));
                _transactions.Add(transaction);
            }
            catch
            {
                bool fault = transaction.RequiresFault;
                transaction.Dispose();
                if (fault) throw new RuntimeFailure(WorldResult.Failure(WorldErrorCode.Faulted,
                    new WorldDiagnostic(context.Stage.ToString(), "apply", "事务进入应用后失败，不能保证一致性。")));
                throw;
            }
        }

        internal WorldResult BeginTick()
        {
            if (_disposed) return LastResult = WorldResult.Failure(WorldErrorCode.Disposed, new WorldDiagnostic("Step", "world", "世界已释放。"));
            if (Faulted) return LastResult = WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("Step", "world", "世界已冻结。"));
            if (Thread.CurrentThread.ManagedThreadId != _thread) return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("Step", "thread", "操作必须在创建线程执行。"));
            Metrics.Begin();
            using var activation = Metrics.Activate();
            using var timing = WorldStepMetrics.Measure(WorldStepMetrics.Timing.BeginTick);
            LastResult = State.BeginTick();
            _originalInstances.Clear(); _removals.Clear(); _replacements.Clear(); _originOfInstance.Clear();
            if (LastResult.IsSuccess) foreach (CellKey key in State.OccupiedCells)
                if (State.Instances.TryGetInstance(key, out CellInstanceHandle instance))
                { _originalInstances.Add(key, instance); _originOfInstance.Add(instance, key); }
            if (LastResult.IsSuccess) foreach (SuspendedFluidSnapshot record in State.SuspendedFluids)
                if (State.Instances.TryGetInstance(record.Key, out CellInstanceHandle instance))
                { _originalInstances.Add(record.Key, instance); _originOfInstance.Add(instance, record.Key); }
            return LastResult;
        }

        internal WorldResult CompleteTick()
        {
            using var activation = Metrics.Activate();
            try
            {
                EnsureRules();
                Wet(TickStage.PreFlowContacts, false);
                Run(_rules.Water, TickStage.Water);
                Run(_rules.Steam, TickStage.Steam);
                Wet(TickStage.Extinguish, true);
                Run(_rules.Burning, TickStage.Burning);
                using (WorldStepMetrics.Measure(WorldStepMetrics.Timing.Physics))
                {
                    Refresh(Spatial, TickStage.Physics);
                    Wet(TickStage.Physics, true);
                    BeforePhysicsForTest?.Invoke();
                    Require(PhysicsSubstepPlanner.Plan(State.Config, State.Bodies, Physics.Geometry, out int count));
                    if (FixedSubsteps != 0) count = FixedSubsteps;
                    LastSubsteps = count;
                    for (int i = 0; i < count; i++)
                    {
                        Refresh(Spatial, TickStage.Physics);
                        long revision = State.Revision; ulong tick = State.WorkingTick;
                        Physics.Bind(new SpatialLease(State.Generation, tick, TickStage.Physics, revision), () => State.IsLeaseValid(revision, tick));
                        long nonPhysicsBytes = ExternalCpuBytes + State.EstimatedCpuBytes + (_rules?.ReservedCpuBytes ?? 0) + Spatial.ReservedCpuBytes + _after.ReservedCpuBytes + State.Config.Limits.MaxMaterialCells * 256L + FluidDisplacementPlanner.EstimateCpuBytes(State.Config, State.MaterialCells);
                        PhysicsStepResult step = Physics.StepSubstep(State, State.Config.StepSeconds / count, i, CpuBudgetBytes - nonPhysicsBytes, Failures);
                        Require(step.Result);
                        AfterPhysicsCandidateForTest?.Invoke(step.Candidates);
                        SimulatedSeconds += (double)State.Config.StepSeconds / count;
                        Refresh(_after, TickStage.Physics, step.Candidates.CandidateBodies);
                        Require(PhysicsGeometryValidator.Validate(State.Config, _origin, Spatial, _after));
                        // 暂存前按候选连续位姿采湿，保留本Tick已消耗燃料。
                        Collect(_after, TickStage.Physics);
                        Require(_displacement.CollectCovered(State, _loaded.Materials, _after, out CellKey[] covered));
                        if (covered.Length != 0) Require(Failures?.Check(Context(TickStage.Physics), FailurePoint.BeforeFluidCapture) ?? WorldResult.Success());
                        var prepared = State.PreparePhysics(step.Candidates, covered, Context(TickStage.Physics));
                        Require(prepared.Result);
                        var transaction = new TransactionCoordinator();
                        try
                        {
                            transaction.Own(prepared.Prepared);
                            long others = ExternalCpuBytes + (_rules?.ReservedCpuBytes ?? 0) + Physics.ReservedCpuBytes + Spatial.ReservedCpuBytes + _after.ReservedCpuBytes + State.Config.Limits.MaxMaterialCells * 256L + FluidDisplacementPlanner.EstimateCpuBytes(State.Config, State.MaterialCells);
                            Require(transaction.ValidateAndApply(Context(TickStage.Physics), State.Config.Limits, CpuBudgetBytes - others, Failures));
                            _transactions.Add(transaction);
                        }
                        catch { transaction.Dispose(); throw; }
                        Wet(TickStage.Physics, true);
                    }
                    Refresh(Spatial, TickStage.Physics);
                    Require(_displacement.PlanRestoration(State, State.Instances, Spatial, out MutationIntent[] restored));
                    if (restored.Length != 0)
                    {
                        Require(Failures?.Check(Context(TickStage.Physics), FailurePoint.BeforeFluidRestoration) ?? WorldResult.Success());
                        Apply(State.PrepareFluidMoves(restored, Context(TickStage.Physics)), Context(TickStage.Physics), false);
                        Wet(TickStage.Physics, true);
                    }
                }
                Require(Failures?.Check(Context(TickStage.Publish), FailurePoint.BeforePublish) ?? WorldResult.Success());
                Publish();
                return LastResult = WorldResult.Success();
            }
            catch (RuntimeFailure exception) { return Freeze(exception.Result); }
            catch (Exception exception) { return Freeze(WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("Step", "M06", exception.Message))); }
            finally { Metrics.End(); }
        }
        internal WorldResult Step() { WorldResult begin = BeginTick(); return begin.IsSuccess ? CompleteTick() : begin; }
        internal WorldResult Freeze(WorldResult result)
        {
            Faulted = true; LastResult = result; State.Fault(); Spatial.Invalidate(); _after.Invalidate();
            foreach (TransactionCoordinator transaction in _transactions) transaction.Dispose();
            _transactions.Clear(); return result;
        }

        private void Run(IRuleExecutor rule, TickStage stage)
        {
            using var timing = WorldStepMetrics.Measure(StageTiming(stage));
            bool present = stage == TickStage.Water ? State.RuleSources.Count(RuleMask.LiquidFlow) != 0 :
                stage == TickStage.Steam ? State.RuleSources.Count(RuleMask.GasDrift) != 0 || State.RuleSources.SuspendedGasCount != 0 :
                State.RuleSources.Count(RuleMask.Burnable, true) != 0;
            if (!present) { WorldStepMetrics.Add(WorldStepMetrics.Work.SkippedRuleStages); return; }
            Refresh(Spatial, stage);
            IRuleBatch batch = rule.Execute(State, _loaded.Materials, State.Instances, Spatial, Context(stage));
            ApplyBatch(batch);
        }
        private void Collect(IContactQuery contacts, TickStage stage)
        {
            if (State.RuleSources.Count(RuleMask.Burnable) == 0 || State.RuleSources.Count(RuleMask.ExtinguishesFire) == 0)
            { WorldStepMetrics.Add(WorldStepMetrics.Work.SkippedRuleStages); return; }
            Require(_rules.WetContacts.Collect(State, _loaded.Materials, State.Instances, contacts, Context(stage)));
            foreach (CellInstanceHandle instance in _rules.WetContacts.WetInstances) State.Instances.MarkWet(instance);
        }
        private void Wet(TickStage stage, bool extinguish)
        {
            using var timing = WorldStepMetrics.Measure(stage == TickStage.Physics ? WorldStepMetrics.Timing.PhysicsContacts : StageTiming(stage));
            Refresh(Spatial, stage); Collect(Spatial, stage);
            if (extinguish && State.RuleSources.Count(RuleMask.Burnable, true) != 0)
                ApplyBatch(_rules.WetContacts.Execute(State, _loaded.Materials, State.Instances, Spatial, Context(stage)));
        }
        private void ApplyBatch(IRuleBatch batch)
        {
            Require(batch.Result);
            if (batch.Intents.Length == 0) return;
            var writes = new List<CellWrite>(); var moves = new List<MutationIntent>(); bool structural = false;
            foreach (MutationIntent intent in batch.Intents)
            {
                if (intent.Kind == MutationKind.Move) moves.Add(intent);
                else
                {
                    writes.Add(new CellWrite(intent.Source, intent.State));
                    if (intent.Kind == MutationKind.Remove && State.Instances.TryGetInstance(intent.Source, out CellInstanceHandle removed))
                        _removals[removed] = batch.Stage == TickStage.Burning ? CellChangeKind.BurnedOut :
                            batch.Stage == TickStage.Steam ? CellChangeKind.LifetimeExpired : CellChangeKind.Removed;
                    if (intent.Kind == MutationKind.Remove)
                    {
                        State.Read(intent.Source, out CellSnapshot old);
                        if (_loaded.Materials.TryGet(old.MaterialId, out MaterialRuntimeEntry entry) && (entry.Rules & RuleMask.Structure) != 0) structural = true;
                    }
                }
            }
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Source, default));
            foreach (MutationIntent move in moves) writes.Add(new CellWrite(move.Target, move.State));
            Apply(State.Prepare(writes.ToArray(), Context(batch.Stage), moves: moves.ToArray()), Context(batch.Stage), structural);
        }
        private void Publish()
        {
            using var timing = WorldStepMetrics.Measure(WorldStepMetrics.Timing.Publish);
            CommittedWorldView candidate = null;
            IPreparedWorldDisplay display = null;
            TickChangeSet changes = null;
            try
            {
                candidate = new CommittedWorldView(State, _loaded.Materials, new WorldVersion(State.Generation, State.WorkingTick));
                using (WorldStepMetrics.Measure(WorldStepMetrics.Timing.ChangeSet))
                    changes = TickChangeSet.Build(View, candidate, _originalInstances, State.Instances, State.TickWrites, _removals, _replacements);
                if (Renderer != null)
                {
                    changes.Open();
                    try
                    {
                        using var displayTiming = WorldStepMetrics.Measure(WorldStepMetrics.Timing.DisplayPrepare);
                        display = Renderer.PrepareCommit(candidate, new ChangeSet(candidate.Version, changes));
                    }
                    finally { changes.Close(); }
                    if (display == null) throw new RuntimeFailure(WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("DisplayPrepare", "candidate", "显示没有返回完整候选。")));
                    Require(display.Result);
                    if (ReservedCpuBytes + candidate.StorageBytes + display.CpuBytes > CpuBudgetBytes)
                        throw new RuntimeFailure(WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("DisplayPrepare", "cpuBytes", "显示候选与模拟保留资源合并超限。")));
                }
                Require(State.PublishState(candidate)); candidate = null;
                display?.Adopt();
                LastChanges = changes;
            }
            finally { display?.Dispose(); candidate?.Dispose(); }
            foreach (TransactionCoordinator transaction in _transactions) { transaction.MarkCommitted(View.Version); transaction.Dispose(); }
            // PublishState已关闭当前实例租约；几何桶保留至下Tick受控重新绑定。
            _transactions.Clear();
        }

        private static WorldStepMetrics.Timing StageTiming(TickStage stage) => stage switch
        {
            TickStage.Water => WorldStepMetrics.Timing.Water,
            TickStage.Steam => WorldStepMetrics.Timing.Steam,
            TickStage.Burning => WorldStepMetrics.Timing.Burning,
            TickStage.PreFlowContacts => WorldStepMetrics.Timing.PreFlowContacts,
            _ => WorldStepMetrics.Timing.Extinguish
        };

        internal WorldResult Edit(ReadOnlySpan<CellWrite> writes, in TransactionContext context, bool structural)
        {
            try
            {
                var replaced = new List<(CellKey original, CellInstanceHandle oldInstance, CellInstanceHandle next)>();
                var candidate = State.Prepare(writes, context); Require(candidate.Result);
                foreach (CellWrite write in writes)
                    if (write.NewInstance && State.Instances.TryGetInstance(write.Key, out CellInstanceHandle oldInstance) &&
                        _originOfInstance.TryGetValue(oldInstance, out CellKey original) &&
                        candidate.Prepared.CandidateInstances.TryGetInstance(write.Key, out CellInstanceHandle next))
                        replaced.Add((original, oldInstance, next));
                Apply(candidate, context, structural);
                foreach (var item in replaced)
                {
                    _replacements[item.original] = item.next;
                    _originOfInstance.Remove(item.oldInstance); _originOfInstance[item.next] = item.original;
                }
                return WorldResult.Success();
            }
            catch (RuntimeFailure exception)
            {
                if (exception.Result.ErrorCode == WorldErrorCode.Faulted) return Freeze(exception.Result);
                return exception.Result;
            }
            catch (Exception exception) { return Freeze(WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("Commands", "apply", exception.Message))); }
        }
        internal WorldResult SetBodyForTest(BodySnapshot snapshot)
        {
            if (Physics.FreezeBodyRotation && snapshot.Motion.AngularVelocityRadians != 0)
                return WorldResult.Failure(WorldErrorCode.InvalidArgument,
                    new WorldDiagnostic("Test", "angularVelocity", "禁转世界不能注入非零角速度。"));
            Require(BeginTick());
            var context = Context(TickStage.Commands);
            var poses = State.Bodies.ToArray();
            int index = Array.FindIndex(poses, b => b.BodyId == snapshot.BodyId); if (index < 0) return WorldResult.Failure(WorldErrorCode.InvalidArgument, default);
            poses[index] = snapshot;
            var candidate = State.Prepare(ReadOnlySpan<CellWrite>.Empty, context); Require(candidate.Result);
            Require(State.PrepareStateBodies(candidate.Prepared, poses).Result);
            using var transaction = new TransactionCoordinator(); transaction.Own(candidate.Prepared);
            Require(transaction.ValidateAndApply(context, State.Config.Limits, ContractDefaults.CpuBudgetBytes));
            Physics.Synchronize(snapshot); Require(State.PublishState()); transaction.MarkCommitted(View.Version);
            return WorldResult.Success();
        }
        internal WorldResult SetCellsForTest(ReadOnlySpan<CellWrite> writes)
        {
            Require(BeginTick());
            Require(Edit(writes, Context(TickStage.Commands), false));
            Publish();
            return WorldResult.Success();
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            foreach (TransactionCoordinator transaction in _transactions) transaction.Dispose(); _transactions.Clear();
            _after?.Dispose(); Spatial?.Dispose(); Physics?.Dispose(); State?.Dispose(); _rules = null;
            _structure = null; _material = null;
        }
    }
}
