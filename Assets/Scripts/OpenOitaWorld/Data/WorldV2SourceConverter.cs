using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;

namespace OpenOita.Data
{
    // 只做明确的 v1 -> v2 迁移；转换前先走 v1 严格语义校验，转换后再走 V2 严格校验。
    public static class WorldV2SourceConverter
    {
        public static WorldResult Convert(WorldSources v1Sources, out WorldSources v2Sources)
        {
            v2Sources = null;
            if (v1Sources == null)
                return WorldResult.Failure(WorldErrorCode.InvalidArgument,
                    new WorldDiagnostic("V2迁移", "$", "WorldSources 不能为空。"));
            try
            {
                JObject materials = StrictJson.Parse(v1Sources.MaterialsText, "materials.json");
                JObject world = StrictJson.Parse(v1Sources.WorldConfigText, "world_config.json");
                JObject scene = StrictJson.Parse(v1Sources.SceneText, "scene.json");
                Version1(materials["schemaVersion"], "materials.json");
                Version1(world["schemaVersion"], "world_config.json");
                Version1(scene["schemaVersion"], "scene.json");

                // 以极大值仅关闭旧加载器的保守预算拒绝；所有字段、跨文件关系和规则仍由旧严格加载器校验。
                WorldLoadResult validated = new WorldSourceLoader(long.MaxValue).Load(new WorldSources(
                    v1Sources.MaterialsText, v1Sources.WorldConfigText, v1Sources.SceneText));
                if (!validated.Result.IsSuccess) return validated.Result;

                materials["schemaVersion"] = 2;
                world["schemaVersion"] = 2;
                world.Remove("chunkSize");
                scene["schemaVersion"] = 2;
                v2Sources = new WorldSources(
                    materials.ToString(Formatting.None), world.ToString(Formatting.None), scene.ToString(Formatting.None));
                WorldLoadResult converted = new WorldSourceLoaderV2().Load(v2Sources);
                if (!converted.Result.IsSuccess) { v2Sources = null; return converted.Result; }
                return WorldResult.Success();
            }
            catch (ConfigurationException ex) { return ex.Result; }
            catch (OverflowException)
            {
                return WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("V2迁移", "limits", "受检运算溢出。", "world_config.json"));
            }
        }

        private static void Version1(JToken token, string file)
        {
            if (token == null) StrictJson.Fail(token, file, "缺少必需字段 schemaVersion。");
            long version = StrictJson.Integer(token, file, 0, int.MaxValue);
            if (version != 1) StrictJson.Fail(token, file, "迁移输入必须是 schemaVersion=1。", WorldErrorCode.UnsupportedVersion);
        }
    }
}
