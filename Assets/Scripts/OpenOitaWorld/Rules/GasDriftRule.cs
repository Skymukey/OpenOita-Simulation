using OpenOita.Contracts;

namespace OpenOita.Rules
{
    public sealed class GasDriftRule : IRuleExecutor
    {
        private readonly RuleBatchContext _input;
        private readonly RuleIntent _output;
        private readonly IOccupancyView _occupancy;
        private readonly MovementCandidateResolver.Phase _movement;
        public RuleId RuleId => RuleId.GasDrift;

        public GasDriftRule(int cellCapacity, IOccupancyView occupancy = null, int intentCapacity = -1) : this(cellCapacity, occupancy, intentCapacity, null)
        {
        }

        internal GasDriftRule(int cellCapacity, IOccupancyView occupancy, int intentCapacity, RuleBatchContext input)
        {
            _input = input ?? new RuleBatchContext(cellCapacity, 0);
            _output = new RuleIntent(intentCapacity < 0 ? cellCapacity : intentCapacity);
            _occupancy = occupancy;
            _movement = new MovementCandidateResolver.Phase(cellCapacity);
        }

        public IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context)
        {
            _output.Begin(context.Stage);
            WorldResult result = context.Stage == TickStage.Steam ? _input.Begin(snapshot, materials, instances, contacts, context) :
                RuleBatchContext.Error(context.Stage, WorldErrorCode.InvalidArgument, default, "蒸汽规则只能在 Steam 阶段执行。");
            if (result.IsSuccess) result = _movement.Generate(_input, _output, RuleMask.GasDrift, 1, _occupancy);
            if (result.IsSuccess && snapshot is IFluidSuspensionView fluids)
            {
                foreach (SuspendedFluidSnapshot record in fluids.SuspendedFluids)
                {
                    CellSnapshot state = record.State;
                    if (!materials.TryGet(state.MaterialId, out MaterialRuntimeEntry material) || (material.Rules & RuleMask.GasDrift) == 0) continue;
                    if (!instances.TryGetInstance(record.Key, out CellInstanceHandle instance))
                    {
                        result = RuleBatchContext.Error(context.Stage, WorldErrorCode.NotReady, record.Key, "暂存蒸汽实例租约失效。");
                        break;
                    }
                    bool expired = state.LifetimeTicksRemaining <= 1;
                    CellSnapshot next = expired ? default : new CellSnapshot(state.MaterialId, state.Flags, state.FuelTicksRemaining,
                        state.SpreadCountdown, state.LifetimeTicksRemaining - 1, state.MoveCountdown, state.IgnitedTick);
                    if (!_output.Add(new MutationIntent(expired ? MutationKind.Remove : MutationKind.WriteState, instance, record.Key, default, next))) break;
                }
            }
            if (!result.IsSuccess) _output.Fail(result);
            return _output;
        }
    }
}
