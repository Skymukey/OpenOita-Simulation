using OpenOita.Contracts;

namespace OpenOita.Rules
{
    public sealed class LiquidFlowRule : IRuleExecutor
    {
        private readonly RuleBatchContext _input;
        private readonly RuleIntent _output;
        private readonly IOccupancyView _occupancy;
        private readonly MovementCandidateResolver.Phase _movement;
        public RuleId RuleId => RuleId.LiquidFlow;

        public LiquidFlowRule(int cellCapacity, IOccupancyView occupancy = null, int intentCapacity = -1)
        {
            _input = new RuleBatchContext(cellCapacity, 0);
            _output = new RuleIntent(intentCapacity < 0 ? cellCapacity : intentCapacity);
            _occupancy = occupancy;
            _movement = new MovementCandidateResolver.Phase(cellCapacity, true);
        }

        public IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context)
        {
            _output.Begin(context.Stage);
            WorldResult result = context.Stage == TickStage.Water ? _input.Begin(snapshot, materials, instances, contacts, context) :
                RuleBatchContext.Error(context.Stage, WorldErrorCode.InvalidArgument, default, "水规则只能在 Water 阶段执行。");
            if (result.IsSuccess) result = _movement.Generate(_input, _output, RuleMask.LiquidFlow, -1, _occupancy);
            if (!result.IsSuccess) _output.Fail(result);
            return _output;
        }
    }
}
