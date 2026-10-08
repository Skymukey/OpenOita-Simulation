using System;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;

namespace OpenOita.Data
{
    public sealed class RuleRegistry : IRuleRegistry
    {
        private static readonly RuleDescriptor[] Descriptors =
        {
            default,
            new RuleDescriptor(RuleId.Structure, "structure", MaterialKind.Solid, TickStage.Structure, RuleTrigger.MaterialChanged),
            new RuleDescriptor(RuleId.LiquidFlow, "liquid_flow", MaterialKind.Liquid, TickStage.Water, RuleTrigger.EachMovementPhase),
            new RuleDescriptor(RuleId.GasDrift, "gas_drift", MaterialKind.Gas, TickStage.Steam, RuleTrigger.EachTick),
            new RuleDescriptor(RuleId.Burnable, "burnable", MaterialKind.Solid, TickStage.Burning, RuleTrigger.BurningCells),
            new RuleDescriptor(RuleId.ExtinguishesFire, "extinguishes_fire", MaterialKind.Liquid, TickStage.Extinguish, RuleTrigger.ContactCapability),
            new RuleDescriptor(RuleId.Corrosive, "corrosive", MaterialKind.Liquid, TickStage.PreFlowContacts, RuleTrigger.ContactCapability),
            new RuleDescriptor(RuleId.Corrodible, "corrodible", MaterialKind.Solid, TickStage.PreFlowContacts, RuleTrigger.ContactCapability)
        };
        private readonly bool _v2;
        private static readonly RuleDescriptor V2Burnable = new RuleDescriptor(RuleId.Burnable, "burnable",
            MaterialKind.Solid, TickStage.Burning, RuleTrigger.BurningCells, MaterialKind.Liquid);
        public RuleRegistry(bool v2 = false) { _v2 = v2; }
        public int Count => _v2 ? Descriptors.Length - 1 : 5;
        public bool TryGet(RuleId id, out RuleDescriptor descriptor)
        {
            int index = (int)id;
            descriptor = index > 0 && index <= Count ? Descriptors[index] : default;
            if (_v2 && id == RuleId.Burnable) descriptor = V2Burnable;
            return index > 0 && index <= Count;
        }

        internal RuleMask Validate(JArray tags, JObject parameters, MaterialKind kind, string file, out RuleParameters parsed)
        {
            RuleMask mask = RuleMask.None;
            foreach (JToken tag in tags)
            {
                string name = StrictJson.String(tag, file);
                int index = System.Array.FindIndex(Descriptors, d => d.Tag == name);
                if (index <= 0 || index > Count) StrictJson.Fail(tag, file, "未知规则标签。", WorldErrorCode.UnknownRule);
                RuleMask bit = (RuleMask)(1UL << (index - 1));
                if ((mask & bit) != 0) StrictJson.Fail(tag, file, "重复规则标签。");
                TryGet((RuleId)index, out RuleDescriptor descriptor);
                if (!descriptor.SupportsKind(kind)) StrictJson.Fail(tag, file, "规则与 kind 不兼容。", WorldErrorCode.IncompatibleRule);
                mask |= bit;
            }
            RuleMask required = kind == MaterialKind.Solid ? RuleMask.Structure : kind == MaterialKind.Liquid ? RuleMask.LiquidFlow : RuleMask.GasDrift;
            if ((mask & required) == 0) StrictJson.Fail(tags, file, "缺少 kind 要求的显式标签。", WorldErrorCode.IncompatibleRule);
            foreach (JProperty property in parameters.Properties())
            {
                int index = System.Array.FindIndex(Descriptors, d => d.Tag == property.Name);
                if (index <= 0 || index > Count || index == 5 || index == 7 || (mask & (RuleMask)(1UL << (index - 1))) == 0)
                    StrictJson.Fail(property, file, "未知、未启用或不允许参数的规则块。");
            }
            string group = null;
            uint move = 0, lifetime = 0, fuel = 0, spread = 0;
            ushort smoke = 0;
            uint smokeInterval = 0, corrosionInterval = 0;
            for (int index = 1; index <= Count; index++)
            {
                if (index == 5 || index == 7) continue;
                if ((mask & (RuleMask)(1UL << (index - 1))) == 0) continue;
                string name = Descriptors[index].Tag;
                JToken token = parameters[name];
                if (token == null) throw new ConfigurationException(file, StrictJson.Path(parameters) + "." + name, "缺少已启用规则的参数块。");
                switch (index)
                {
                    case 1:
                        StrictJson.Object(token, file, "connectionGroup"); group = StrictJson.String(token["connectionGroup"], file); break;
                    case 2:
                        StrictJson.Object(token, file, "moveIntervalTicks"); move = Ticks(token["moveIntervalTicks"], file); break;
                    case 3:
                        StrictJson.Object(token, file, "moveIntervalTicks", "lifetimeTicks");
                        move = Ticks(token["moveIntervalTicks"], file); lifetime = Ticks(token["lifetimeTicks"], file); break;
                    case 4:
                        if (_v2 && token is JObject smokeParameters &&
                            (smokeParameters["smokeMaterialId"] != null || smokeParameters["smokeIntervalTicks"] != null))
                        {
                            StrictJson.Object(token, file, "fuelTicks", "spreadIntervalTicks", "smokeMaterialId", "smokeIntervalTicks");
                            smoke = (ushort)StrictJson.Integer(token["smokeMaterialId"], file, 1, ushort.MaxValue);
                            smokeInterval = Ticks(token["smokeIntervalTicks"], file);
                        }
                        else StrictJson.Object(token, file, "fuelTicks", "spreadIntervalTicks");
                        fuel = Ticks(token["fuelTicks"], file); spread = Ticks(token["spreadIntervalTicks"], file); break;
                    case 6:
                        StrictJson.Object(token, file, "intervalTicks"); corrosionInterval = Ticks(token["intervalTicks"], file); break;
                }
            }
            parsed = new RuleParameters(group, move, lifetime, fuel, spread, smoke, smokeInterval, corrosionInterval);
            return mask;
        }
        private static uint Ticks(JToken token, string file) => (uint)StrictJson.Integer(token, file, 1, 1000000);
    }
}
