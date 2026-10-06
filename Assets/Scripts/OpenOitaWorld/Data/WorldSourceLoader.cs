using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Data
{
    public sealed class WorldSourceLoader : IWorldSourceLoader
    {
        public const long DefaultMemoryBudgetBytes = 256L * 1024 * 1024;
        private readonly long _memoryBudgetBytes;
        public WorldSourceLoader(long memoryBudgetBytes = DefaultMemoryBudgetBytes)
        {
            if (memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes));
            _memoryBudgetBytes = memoryBudgetBytes;
        }
        public WorldLoadResult Load(WorldSources sources)
        {
            if (sources == null) return new WorldLoadResult(WorldResult.Failure(WorldErrorCode.InvalidArgument,
                new WorldDiagnostic("配置读取", "$", "WorldSources 不能为空。")));
            sources = new WorldSources(sources.MaterialsText, sources.WorldConfigText, sources.SceneText,
                string.IsNullOrWhiteSpace(sources.MaterialsFileName) ? "materials.json" : sources.MaterialsFileName,
                string.IsNullOrWhiteSpace(sources.WorldConfigFileName) ? "world_config.json" : sources.WorldConfigFileName,
                string.IsNullOrWhiteSpace(sources.SceneFileName) ? "scene.json" : sources.SceneFileName);
            try
            {
                long textLength = checked((long)(sources.MaterialsText?.Length ?? 0) + (sources.WorldConfigText?.Length ?? 0) + (sources.SceneText?.Length ?? 0));
                // 为文本、语法扫描、DOM 和布局构造预留保守上界，先于 DOM 分配执行。
                RequireMemory(checked(textLength * 64 + 131072), sources.SceneFileName, "$", "输入文本与解析工作集");
                JObject materials = StrictJson.Parse(sources.MaterialsText, sources.MaterialsFileName);
                JObject world = StrictJson.Parse(sources.WorldConfigText, sources.WorldConfigFileName);
                JObject scene = StrictJson.Parse(sources.SceneText, sources.SceneFileName);
                WorldConfig config = ParseWorld(world, sources.WorldConfigFileName);
                var rules = new RuleRegistry();
                List<MaterialRuntimeEntry> entries = ParseMaterials(materials, sources.MaterialsFileName, rules, out string materialSet);
                // 运行表在完整跨文件检查后创建；临时索引不公开。
                var byId = new Dictionary<ushort, MaterialRuntimeEntry>();
                foreach (MaterialRuntimeEntry entry in entries) byId.Add(entry.Id, entry);
                SceneInitialData initial = ParseScene(scene, sources.SceneFileName, config, materialSet, byId);
                var chunks = new HashSet<Vector2Int>();
                foreach (InitialCell cell in initial.Cells) chunks.Add(new Vector2Int(cell.Position.x / 128, cell.Position.y / 128));
                WorldLimits limits = config.Limits;
                long estimate = checked(textLength * 64 + 131072L + entries.Count * 256L +
                    chunks.Count * 128L * 128 * 64 + limits.MaxMaterialCells * 160L +
                    limits.MaxChangesPerTick * 96L + limits.MaxDynamicBodies * 256L + limits.MaxTotalShapes * 128L);
                RequireMemory(estimate, sources.WorldConfigFileName, "limits", "状态副本、初态区块、索引与准备缓冲");
                return new WorldLoadResult(WorldResult.Success(), config, initial, new MaterialRuntimeTable(materialSet, entries), rules);
            }
            catch (ConfigurationException ex) { return new WorldLoadResult(ex.Result); }
            catch (OverflowException)
            {
                return new WorldLoadResult(WorldResult.Failure(WorldErrorCode.CapacityExceeded,
                    new WorldDiagnostic("预算预检", "limits", "受检运算溢出。", sources.WorldConfigFileName)));
            }
        }

        private void RequireMemory(long bytes, string file, string path, string target)
        {
            if (bytes > _memoryBudgetBytes) throw new ConfigurationException(file, path,
                $"{target}估算 {bytes} 字节，超过加载预算 {_memoryBudgetBytes} 字节。", WorldErrorCode.CapacityExceeded, "预算预检");
        }

        private static WorldConfig ParseWorld(JObject root, string file)
        {
            StrictJson.Object(root, file, "schemaVersion", "width", "height", "chunkSize", "cellSize", "stepSeconds", "gravityY", "seed", "limits");
            StrictJson.Version(root["schemaVersion"], file);
            int width = (int)StrictJson.Integer(root["width"], file, 1, 4096);
            int height = (int)StrictJson.Integer(root["height"], file, 1, 4096);
            StrictJson.Integer(root["chunkSize"], file, 128, 128);
            JObject limits = StrictJson.Object(root["limits"], file, "maxMaterialCells", "maxDynamicBodies", "maxShapesPerBody", "maxTotalShapes",
                "maxChangesPerTick", "maxLinearSpeed", "maxAngularSpeedDegrees", "maxPhysicsSubsteps", "fluidDisplacementRadius");
            var parsedLimits = new WorldLimits(Count(limits, "maxMaterialCells", file), Count(limits, "maxDynamicBodies", file),
                Count(limits, "maxShapesPerBody", file), Count(limits, "maxTotalShapes", file), Count(limits, "maxChangesPerTick", file),
                StrictJson.Number(limits["maxLinearSpeed"], file, true), StrictJson.Number(limits["maxAngularSpeedDegrees"], file, true),
                Count(limits, "maxPhysicsSubsteps", file), (int)StrictJson.Integer(limits["fluidDisplacementRadius"], file, 1, 4096));
            // 上限是独立预算，perBody > total、maxCells > width*height 均可合法；不能误当必需分配量。
            long area = checked((long)width * height);
            long shapeProduct = checked((long)parsedLimits.MaxDynamicBodies * parsedLimits.MaxShapesPerBody);
            checked { long boundedShapes = Math.Min(shapeProduct, parsedLimits.MaxTotalShapes) + 4; }
            float cellSize = StrictJson.Number(root["cellSize"], file, true);
            float step = StrictJson.Number(root["stepSeconds"], file, true);
            float gravity = StrictJson.Number(root["gravityY"], file, false);
            if ((double)width * cellSize > float.MaxValue || (double)height * cellSize > float.MaxValue ||
                Math.Abs((double)gravity) * step * step > float.MaxValue || (double)parsedLimits.MaxLinearSpeed * step > float.MaxValue ||
                (double)parsedLimits.MaxAngularSpeedDegrees * step > float.MaxValue)
                StrictJson.Fail(root, file, "世界长度或步长/速度/重力组合超出运行时浮点范围。");
            return new WorldConfig(1, width, height, 128, cellSize, step, gravity,
                (uint)StrictJson.Integer(root["seed"], file, 0, uint.MaxValue), parsedLimits);
        }
        private static int Count(JObject root, string key, string file) => (int)StrictJson.Integer(root[key], file, 1, int.MaxValue);

        private static List<MaterialRuntimeEntry> ParseMaterials(JObject root, string file, RuleRegistry rules, out string materialSet)
        {
            StrictJson.Object(root, file, "schemaVersion", "materialSetId", "materials");
            StrictJson.Version(root["schemaVersion"], file);
            materialSet = StrictJson.String(root["materialSetId"], file);
            JArray array = StrictJson.Array(root["materials"], file, 1, 65535);
            var entries = new List<MaterialRuntimeEntry>();
            var ids = new HashSet<ushort>(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in array)
            {
                StrictJson.Object(token, file, "id", "name", "kind", "color", "massPerCell", "tags", "ruleParameters");
                ushort id = (ushort)StrictJson.Integer(token["id"], file, 1, ushort.MaxValue);
                string name = StrictJson.String(token["name"], file);
                if (!ids.Add(id)) StrictJson.Fail(token["id"], file, "重复材料 ID。");
                if (!names.Add(name)) StrictJson.Fail(token["name"], file, "重复材料名称。");
                string kindName = StrictJson.String(token["kind"], file);
                MaterialKind kind;
                switch (kindName)
                {
                    case "solid": kind = MaterialKind.Solid; break;
                    case "liquid": kind = MaterialKind.Liquid; break;
                    case "gas": kind = MaterialKind.Gas; break;
                    default: throw new ConfigurationException(file, StrictJson.Path(token["kind"]), "kind 必须是 solid/liquid/gas。");
                }
                string color = StrictJson.String(token["color"], file);
                if (color.Length != 7 || color[0] != '#') StrictJson.Fail(token["color"], file, "颜色必须是 #RRGGBB。");
                for (int i = 1; i < color.Length; i++) if (!Uri.IsHexDigit(color[i])) StrictJson.Fail(token["color"], file, "颜色含非法十六进制字符。");
                var parsedColor = new Color32(byte.Parse(color.Substring(1, 2), NumberStyles.HexNumber),
                    byte.Parse(color.Substring(3, 2), NumberStyles.HexNumber), byte.Parse(color.Substring(5, 2), NumberStyles.HexNumber), 255);
                float mass = StrictJson.Number(token["massPerCell"], file, true);
                if (!(token["ruleParameters"] is JObject parameters)) StrictJson.Fail(token["ruleParameters"], file, "参数必须是对象。");
                RuleMask mask = rules.Validate(StrictJson.Array(token["tags"], file, 1, 5), (JObject)token["ruleParameters"], kind, file, out RuleParameters parsed);
                entries.Add(new MaterialRuntimeEntry(id, 0, name, kind, parsedColor, mass, mask, parsed));
            }
            return entries;
        }

        private static SceneInitialData ParseScene(JObject root, string file, WorldConfig config, string materialSet,
            Dictionary<ushort, MaterialRuntimeEntry> byId)
        {
            StrictJson.Object(root, file, "schemaVersion", "materialSetId", "worldConfig", "materialsFile", "cells", "fixedCells", "initialBurning");
            StrictJson.Version(root["schemaVersion"], file);
            if (StrictJson.String(root["materialSetId"], file) != materialSet) StrictJson.Fail(root["materialSetId"], file, "材料集合不一致。");
            if (StrictJson.String(root["worldConfig"], file) != "world_config.json") StrictJson.Fail(root["worldConfig"], file, "只允许同目录 world_config.json。");
            if (StrictJson.String(root["materialsFile"], file) != "materials.json") StrictJson.Fail(root["materialsFile"], file, "只允许同目录 materials.json。");
            JArray array = StrictJson.Array(root["cells"], file, 0, 16777216);
            if (array.Count > config.Limits.MaxMaterialCells) StrictJson.Fail(array, file, "初态格数超过 maxMaterialCells。", WorldErrorCode.CapacityExceeded);
            if (array.Count > checked((long)config.Width * config.Height)) StrictJson.Fail(array, file, "初态格数超过世界面积。", WorldErrorCode.CapacityExceeded);
            var cells = new List<InitialCell>(); var occupied = new Dictionary<Vector2Int, MaterialRuntimeEntry>();
            Vector2Int previous = new Vector2Int(-1, -1);
            foreach (JToken token in array)
            {
                StrictJson.Object(token, file, "x", "y", "materialId");
                Vector2Int position = Position(token, file, config); CheckOrder(position, previous, token, file); previous = position;
                ushort id = (ushort)StrictJson.Integer(token["materialId"], file, 1, ushort.MaxValue);
                if (!byId.TryGetValue(id, out MaterialRuntimeEntry entry)) StrictJson.Fail(token["materialId"], file, "未定义材料 ID。", WorldErrorCode.UnknownMaterial);
                cells.Add(new InitialCell(position.x, position.y, id)); occupied.Add(position, entry);
            }
            var fixedCells = Markers(root["fixedCells"], file, config, occupied, RuleMask.Structure);
            var burning = Markers(root["initialBurning"], file, config, occupied, RuleMask.Burnable);
            return new SceneInitialData(1, materialSet, "materials.json", "world_config.json", cells, fixedCells, burning);
        }
        private static List<Vector2Int> Markers(JToken token, string file, WorldConfig config,
            Dictionary<Vector2Int, MaterialRuntimeEntry> occupied, RuleMask rule)
        {
            JArray array = StrictJson.Array(token, file, 0, 16777216);
            var list = new List<Vector2Int>(); Vector2Int previous = new Vector2Int(-1, -1);
            foreach (JToken item in array)
            {
                StrictJson.Object(item, file, "x", "y");
                Vector2Int position = Position(item, file, config); CheckOrder(position, previous, item, file); previous = position;
                if (!occupied.TryGetValue(position, out MaterialRuntimeEntry entry) || (entry.Rules & rule) == 0)
                    StrictJson.Fail(item, file, "标记必须指向具有所需规则的非空格。", WorldErrorCode.IncompatibleRule);
                list.Add(position);
            }
            return list;
        }
        private static Vector2Int Position(JToken token, string file, WorldConfig config)
        {
            int x = (int)StrictJson.Integer(token["x"], file, 0, 4095);
            int y = (int)StrictJson.Integer(token["y"], file, 0, 4095);
            if (x >= config.Width) StrictJson.Fail(token["x"], file, "坐标超过实际 width。", WorldErrorCode.OutOfBounds);
            if (y >= config.Height) StrictJson.Fail(token["y"], file, "坐标超过实际 height。", WorldErrorCode.OutOfBounds);
            return new Vector2Int(x, y);
        }
        private static void CheckOrder(Vector2Int current, Vector2Int previous, JToken token, string file)
        {
            if (current.y < previous.y || (current.y == previous.y && current.x <= previous.x))
                StrictJson.Fail(token, file, "列表必须按 y/x 严格升序且坐标唯一。");
        }
    }
}
