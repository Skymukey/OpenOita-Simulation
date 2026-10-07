using System;
using OpenOita.Contracts;

namespace OpenOita.Rules
{
    public sealed class BurnRule : IRuleExecutor
    {
        private readonly RuleBatchContext _input;
        private readonly RuleIntent _output;
        private readonly bool[] _spreading;
        private readonly bool[] _removed;
        private readonly bool[] _ignited;
        public RuleId RuleId => RuleId.Burnable;

        public BurnRule(int cellCapacity, int contactCapacity = -1, int intentCapacity = -1) : this(cellCapacity, contactCapacity, intentCapacity, null)
        {
        }

        internal BurnRule(int cellCapacity, int contactCapacity, int intentCapacity, RuleBatchContext input)
        {
            _input = input ?? new RuleBatchContext(cellCapacity, contactCapacity < 0 ? cellCapacity : contactCapacity);
            _output = new RuleIntent(intentCapacity < 0 ? cellCapacity : intentCapacity);
            _spreading = new bool[cellCapacity];
            _removed = new bool[cellCapacity];
            _ignited = new bool[cellCapacity];
        }

        public IRuleBatch Execute(IWorkingWorldView snapshot, IMaterialRuntimeTable materials,
            ITickInstanceMap instances, IContactQuery contacts, in TransactionContext context)
        {
            _output.Begin(context.Stage);
            WorldResult result = context.Stage == TickStage.Burning ? _input.Begin(snapshot, materials, instances, contacts, context) :
                RuleBatchContext.Error(context.Stage, WorldErrorCode.InvalidArgument, default, "燃烧规则只能在 Burning 阶段执行。");
            if (!result.IsSuccess) { _output.Fail(result); return _output; }
            Array.Clear(_spreading, 0, _input.Count);
            Array.Clear(_removed, 0, _input.Count);
            Array.Clear(_ignited, 0, _input.Count);
            foreach (int i in _input.Participants(RuleMask.Burnable, true))
            {
                CellSnapshot before = _input.States[i];
                MaterialRuntimeEntry material = _input.Materials[i];
                if ((material.Rules & RuleMask.Burnable) == 0 || !before.IsBurning) continue;
                CellKey key = _input.Keys[i];
                if (instances.IsWet(_input.Instances[i]))
                {
                    if (!_output.Add(new MutationIntent(MutationKind.WriteState, _input.Instances[i], key, key,
                        WetContactPolicy.Extinguish(before, material.Parameters.SpreadIntervalTicks)))) return _output;
                    continue;
                }
                if (before.IgnitedTick >= context.WorkingTick) continue;
                uint fuel = before.FuelTicksRemaining;
                if (fuel > 0) fuel--;
                if (fuel == 0)
                {
                    _removed[i] = true;
                    if (!_output.Add(new MutationIntent(MutationKind.Remove, _input.Instances[i], key, key, default,
                        removalReason: RemovalReason.BurnedOut))) return _output;
                    continue;
                }
                uint countdown = before.SpreadCountdown;
                if (countdown > 0) countdown--;
                _spreading[i] = countdown == 0;
                if (countdown == 0) countdown = material.Parameters.SpreadIntervalTicks;
                var after = new CellSnapshot(before.MaterialId, before.Flags, fuel, countdown,
                    before.LifetimeTicksRemaining, before.MoveCountdown, before.IgnitedTick);
                if (!_output.Add(new MutationIntent(MutationKind.WriteState, _input.Instances[i], key, key, after))) return _output;
            }
            // 第二遍只读阶段初态，不递归推进新火；先确定燃尽，防止同批复活。
            foreach (int i in _input.Participants(RuleMask.Burnable, true))
            {
                if (!_spreading[i]) continue;
                result = _input.Neighbours(i, out ReadOnlySpan<int> neighbours);
                if (!result.IsSuccess) { _output.Fail(result); return _output; }
                foreach (int target in neighbours)
                {
                    if (_removed[target] || _ignited[target] || !WetContactPolicy.CanIgnite(_input.Materials[target],
                        _input.States[target], instances.IsWet(_input.Instances[target]))) continue;
                    _ignited[target] = true;
                }
            }
            // 目标按稳定身份输出；多个点燃源只写一次。
            foreach (int i in _input.Participants(RuleMask.Burnable))
                if (_ignited[i] && !_output.Add(new MutationIntent(MutationKind.Ignite, _input.Instances[i], _input.Keys[i],
                    _input.Keys[i], WetContactPolicy.Ignite(_input.States[i], context.WorkingTick)))) return _output;
            return _output;
        }
    }
}
