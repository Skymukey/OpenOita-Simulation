using System;
using OpenOita.Contracts;

namespace OpenOita.Rules
{
    public sealed class WetContactPolicy
    {
        private readonly RuleBatchContext _input;
        private readonly RuleIntent _output;
        private readonly CellInstanceHandle[] _wet;
        private int _wetCount;
        public ReadOnlySpan<CellInstanceHandle> WetInstances => _wet.AsSpan(0, _wetCount);

        public WetContactPolicy(int cellCapacity, int contactCapacity = -1, int intentCapacity = -1)
        {
            _input = new RuleBatchContext(cellCapacity, contactCapacity < 0 ? cellCapacity : contactCapacity);
            _output = new RuleIntent(intentCapacity < 0 ? cellCapacity : intentCapacity);
            _wet = new CellInstanceHandle[cellCapacity];
        }

        // M02 在整批成功后 MarkWet；规则从不直接标湿。返回缓冲有效至下次 Collect。
        public WorldResult Collect(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context)
        {
            _wetCount = 0;
            if (context.Stage != TickStage.PreFlowContacts && context.Stage != TickStage.Extinguish && context.Stage != TickStage.Physics)
                return RuleBatchContext.Error(context.Stage, WorldErrorCode.InvalidArgument, default, "湿接触采集阶段无效。");
            WorldResult result = _input.Begin(snapshot, materials, instances, contacts, context);
            if (!result.IsSuccess) return result;
            for (int i = 0; i < _input.Count; i++)
            {
                if ((_input.Materials[i].Rules & RuleMask.Burnable) == 0) continue;
                result = _input.Neighbours(i, out ReadOnlySpan<int> neighbours);
                if (!result.IsSuccess) { _wetCount = 0; return result; }
                foreach (int target in neighbours)
                {
                    if ((_input.Materials[target].Rules & RuleMask.ExtinguishesFire) == 0) continue;
                    _wet[_wetCount++] = _input.Instances[i];
                    break;
                }
            }
            return WorldResult.Success();
        }

        public IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context)
        {
            _output.Begin(context.Stage);
            WorldResult result = context.Stage == TickStage.Extinguish || context.Stage == TickStage.Physics ?
                _input.Begin(snapshot, materials, instances, contacts, context) :
                RuleBatchContext.Error(context.Stage, WorldErrorCode.InvalidArgument, default, "灭火只在 Extinguish/Physics 阶段执行。");
            if (!result.IsSuccess) { _output.Fail(result); return _output; }
            for (int i = 0; i < _input.Count; i++)
            {
                if ((_input.Materials[i].Rules & RuleMask.Burnable) == 0 || !_input.States[i].IsBurning ||
                    !instances.IsWet(_input.Instances[i])) continue;
                var intent = new MutationIntent(MutationKind.WriteState, _input.Instances[i], _input.Keys[i], _input.Keys[i],
                    Extinguish(_input.States[i], _input.Materials[i].Parameters.SpreadIntervalTicks));
                if (!_output.Add(intent)) break;
            }
            return _output;
        }

        internal static CellSnapshot Extinguish(in CellSnapshot state, uint spreadInterval) => new CellSnapshot(
            state.MaterialId, (ushort)(state.Flags & ~1), state.FuelTicksRemaining, spreadInterval,
            state.LifetimeTicksRemaining, state.MoveCountdown, 0);

        public static bool CanIgnite(in MaterialRuntimeEntry material, in CellSnapshot state, bool wet) =>
            !wet && (material.Rules & RuleMask.Burnable) != 0 && state.FuelTicksRemaining > 0 && !state.IsBurning;

        // 供 M07A/M02 的 Ignite 意图使用；重复 Ignite 无变化，不补燃料或刷新周期。
        public static CellSnapshot Ignite(in CellSnapshot state, ulong workingTick) => new CellSnapshot(
            state.MaterialId, (ushort)(state.Flags | 1), state.FuelTicksRemaining, state.SpreadCountdown,
            state.LifetimeTicksRemaining, state.MoveCountdown, workingTick);
    }
}
