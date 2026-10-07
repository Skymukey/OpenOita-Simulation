using System;
using OpenOita.Contracts;

namespace OpenOita.Rules
{
    public sealed class RuleExecutionTable
    {
        public LiquidFlowRule Water { get; }
        public GasDriftRule Steam { get; }
        public BurnRule Burning { get; }
        public WetContactPolicy WetContacts { get; }
        public MovementCandidateResolver Movement { get; }
        public int CellCapacity { get; }
        // 保守管理存储预留，供 M02 合并 CPU 预算；不是 Profiler 峰值证据。
        public long ReservedCpuBytes { get; }

        public RuleExecutionTable(int cellCapacity, IOccupancyView occupancy = null, int contactCapacity = -1)
        {
            if (cellCapacity < 1 || contactCapacity < -1) throw new ArgumentOutOfRangeException(nameof(cellCapacity));
            int contacts = contactCapacity < 0 ? cellCapacity : contactCapacity;
            ReservedCpuBytes = checked(cellCapacity * 3268L + contacts * 256L + 4096 + LiquidSpreadPlanner.ReservedBytes(cellCapacity));
            if (ReservedCpuBytes > ContractDefaults.CpuBudgetBytes)
                throw new ArgumentOutOfRangeException(nameof(cellCapacity), "规则缓冲预留超过模拟 CPU 总预算。");
            CellCapacity = cellCapacity;
            var input = new RuleBatchContext(cellCapacity, contacts);
            Water = new LiquidFlowRule(cellCapacity, occupancy, -1, input);
            Steam = new GasDriftRule(cellCapacity, occupancy, -1, input);
            Burning = new BurnRule(cellCapacity, contacts, -1, input);
            WetContacts = new WetContactPolicy(cellCapacity, contacts, -1, input);
            Movement = new MovementCandidateResolver(cellCapacity);
        }

        public bool TryGetExecutor(RuleId id, out IRuleExecutor executor)
        {
            executor = id == RuleId.LiquidFlow ? Water : id == RuleId.GasDrift ? Steam : id == RuleId.Burnable ? Burning : null;
            return executor != null;
        }

        // 核对唯一的 M01 描述表；structure 由 M04 服务执行，extinguishes_fire 只作邻格能力。
        public WorldResult Validate(IRuleRegistry registry)
        {
            if (registry == null || registry.Count != 5) return Invalid("规则描述表缺失或条目数不一致。");
            for (int value = 1; value <= 5; value++)
            {
                RuleId id = (RuleId)value;
                if (!registry.TryGet(id, out RuleDescriptor descriptor) || descriptor.Id != id) return Invalid("稳定 RuleId 描述缺失。");
                bool valid;
                switch (id)
                {
                    case RuleId.Structure: valid = descriptor.Tag == "structure" && descriptor.Kind == MaterialKind.Solid &&
                        descriptor.Stage == TickStage.Structure && descriptor.Trigger == RuleTrigger.MaterialChanged; break;
                    case RuleId.LiquidFlow: valid = descriptor.Tag == "liquid_flow" && descriptor.Kind == MaterialKind.Liquid &&
                        descriptor.Stage == TickStage.Water && descriptor.Trigger == RuleTrigger.EachMovementPhase; break;
                    case RuleId.GasDrift: valid = descriptor.Tag == "gas_drift" && descriptor.Kind == MaterialKind.Gas &&
                        descriptor.Stage == TickStage.Steam && descriptor.Trigger == RuleTrigger.EachTick; break;
                    case RuleId.Burnable: valid = descriptor.Tag == "burnable" && descriptor.Kind == MaterialKind.Solid &&
                        descriptor.Stage == TickStage.Burning && descriptor.Trigger == RuleTrigger.BurningCells; break;
                    default: valid = descriptor.Tag == "extinguishes_fire" && descriptor.Kind == MaterialKind.Liquid &&
                        descriptor.Stage == TickStage.Extinguish && descriptor.Trigger == RuleTrigger.ContactCapability; break;
                }
                if (!valid) return Invalid("规则描述与冻结阶段/触发方式不一致。");
                if (id >= RuleId.LiquidFlow && id <= RuleId.Burnable &&
                    (!TryGetExecutor(id, out IRuleExecutor executor) || executor.RuleId != id)) return Invalid("规则执行器缺失或 ID 不一致。");
            }
            return WorldResult.Success();
        }

        private static WorldResult Invalid(string message) => WorldResult.Failure(WorldErrorCode.IncompatibleRule,
            new WorldDiagnostic("Assembly", "RuleExecutionTable", message));
    }
}
