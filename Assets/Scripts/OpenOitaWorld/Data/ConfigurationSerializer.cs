using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;

namespace OpenOita.Data
{
    public static class ConfigurationSerializer
    {
        // 输入也可能是编辑器自行构造的合同值；序列化后复用同一严格加载器校验整个初态。
        public static WorldResult Serialize(WorldConfig config, SceneInitialData scene, IMaterialRuntimeTable materials, out WorldSources sources)
        {
            sources = null;
            if (config == null || scene == null || materials == null)
                return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("配置序列化", "$", "配置、初态和材料表不能为空。"));
            if (config.SchemaVersion != 1 && config.SchemaVersion != 2)
                return WorldResult.Failure(WorldErrorCode.UnsupportedVersion,
                    new WorldDiagnostic("配置序列化", "schemaVersion", "仅支持 schemaVersion=1 或 2。"));
            if (scene.SchemaVersion != config.SchemaVersion)
                return WorldResult.Failure(WorldErrorCode.InvalidConfig,
                    new WorldDiagnostic("配置序列化", "scene.schemaVersion", "场景与世界配置版本必须一致。"));
            try
            {
                var candidate = new WorldSources(Materials(materials, config.SchemaVersion).ToString(Formatting.Indented),
                    World(config).ToString(Formatting.Indented), Scene(scene).ToString(Formatting.Indented));
                WorldLoadResult verified = config.SchemaVersion == 2
                    ? new WorldSourceLoaderV2().Load(candidate)
                    : new WorldSourceLoader().Load(candidate);
                if (!verified.Result.IsSuccess) return verified.Result;
                sources = candidate;
                return WorldResult.Success();
            }
            catch (ArgumentException ex)
            {
                return WorldResult.Failure(WorldErrorCode.InvalidConfig, new WorldDiagnostic("配置序列化", "$", ex.Message));
            }
        }

        private static JObject Materials(IMaterialRuntimeTable table, int schemaVersion)
        {
            var array = new JArray();
            var registry = new RuleRegistry();
            // ID 顺序规范化，不依赖传入表的紧凑索引排列；65535 合法，避免 ushort 循环溢出。
            for (int id = 1; id <= ushort.MaxValue; id++)
            {
                if (!table.TryGet((ushort)id, out MaterialRuntimeEntry entry)) continue;
                var tags = new JArray(); var parameters = new JObject();
                for (int index = 1; index <= registry.Count; index++)
                {
                    if ((entry.Rules & (RuleMask)(1UL << (index - 1))) == 0) continue;
                    registry.TryGet((RuleId)index, out RuleDescriptor rule); tags.Add(rule.Tag);
                    RuleParameters p = entry.Parameters;
                    switch (rule.Id)
                    {
                        case RuleId.Structure: parameters[rule.Tag] = new JObject { ["connectionGroup"] = p.ConnectionGroup }; break;
                        case RuleId.LiquidFlow: parameters[rule.Tag] = new JObject { ["moveIntervalTicks"] = p.MoveIntervalTicks }; break;
                        case RuleId.GasDrift: parameters[rule.Tag] = new JObject { ["moveIntervalTicks"] = p.MoveIntervalTicks, ["lifetimeTicks"] = p.LifetimeTicks }; break;
                        case RuleId.Burnable: parameters[rule.Tag] = new JObject { ["fuelTicks"] = p.FuelTicks, ["spreadIntervalTicks"] = p.SpreadIntervalTicks }; break;
                    }
                }
                if ((entry.Rules & ~(RuleMask)31) != 0) throw new ArgumentException("材料表含未注册规则位。");
                string kind = entry.Kind == MaterialKind.Solid ? "solid" : entry.Kind == MaterialKind.Liquid ? "liquid" : entry.Kind == MaterialKind.Gas ? "gas" : "invalid";
                array.Add(new JObject
                {
                    ["id"] = entry.Id, ["name"] = entry.Name, ["kind"] = kind,
                    ["color"] = $"#{entry.Color.r:X2}{entry.Color.g:X2}{entry.Color.b:X2}",
                    ["massPerCell"] = entry.MassPerCell, ["tags"] = tags, ["ruleParameters"] = parameters
                });
            }
            return new JObject { ["schemaVersion"] = schemaVersion, ["materialSetId"] = table.MaterialSetId, ["materials"] = array };
        }
        private static JObject World(WorldConfig config)
        {
            WorldLimits l = config.Limits;
            var world = new JObject
            {
                ["schemaVersion"] = config.SchemaVersion, ["width"] = config.Width, ["height"] = config.Height
            };
            if (config.SchemaVersion == 1) world["chunkSize"] = config.ChunkSize;
            world["cellSize"] = config.CellSize;
            world["stepSeconds"] = config.StepSeconds;
            world["gravityY"] = config.GravityY;
            world["seed"] = config.Seed;
            world["limits"] = new JObject
            {
                ["maxMaterialCells"] = l.MaxMaterialCells, ["maxDynamicBodies"] = l.MaxDynamicBodies,
                ["maxShapesPerBody"] = l.MaxShapesPerBody, ["maxTotalShapes"] = l.MaxTotalShapes,
                ["maxChangesPerTick"] = l.MaxChangesPerTick, ["maxLinearSpeed"] = l.MaxLinearSpeed,
                ["maxAngularSpeedDegrees"] = l.MaxAngularSpeedDegrees, ["maxPhysicsSubsteps"] = l.MaxPhysicsSubsteps,
                ["fluidDisplacementRadius"] = l.FluidDisplacementRadius
            };
            return world;
        }
        private static JObject Scene(SceneInitialData scene)
        {
            var cells = new JArray(); var fixedCells = new JArray(); var burning = new JArray();
            foreach (InitialCell cell in scene.Cells) cells.Add(new JObject { ["x"] = cell.Position.x, ["y"] = cell.Position.y, ["materialId"] = cell.MaterialId });
            foreach (var position in scene.FixedCells) fixedCells.Add(new JObject { ["x"] = position.x, ["y"] = position.y });
            foreach (var position in scene.InitialBurning) burning.Add(new JObject { ["x"] = position.x, ["y"] = position.y });
            return new JObject
            {
                ["schemaVersion"] = scene.SchemaVersion, ["materialSetId"] = scene.MaterialSetId,
                ["worldConfig"] = scene.WorldConfigFile, ["materialsFile"] = scene.MaterialsFile,
                ["cells"] = cells, ["fixedCells"] = fixedCells, ["initialBurning"] = burning
            };
        }
    }
}
