using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    // 只用于验收夹具和工具校验，不能接入正式 Create。M01 必须实现独立严格加载器。
    public static class BaselineSources
    {
#if UNITY_EDITOR
        public static WorldSources Read()
        {
            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "docs", "examples");
            return new WorldSources(File.ReadAllText(Path.Combine(folder, "materials.json")),
                File.ReadAllText(Path.Combine(folder, "world_config.json")), File.ReadAllText(Path.Combine(folder, "scene.json")));
        }
#endif
        public static JObject Parse(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonReaderException("不接受尾随 JSON 内容。");
                return value;
            }
        }
        public static SceneInitialData InitialScene(WorldSources sources)
        {
            JObject scene = Parse(sources.SceneText);
            var cells = new System.Collections.Generic.List<InitialCell>();
            var fixedCells = new System.Collections.Generic.List<Vector2Int>();
            var burning = new System.Collections.Generic.List<Vector2Int>();
            foreach (JObject cell in (JArray)scene["cells"])
                cells.Add(new InitialCell((int)cell["x"], (int)cell["y"], (ushort)cell["materialId"]));
            foreach (JObject cell in (JArray)scene["fixedCells"]) fixedCells.Add(new Vector2Int((int)cell["x"], (int)cell["y"]));
            foreach (JObject cell in (JArray)scene["initialBurning"]) burning.Add(new Vector2Int((int)cell["x"], (int)cell["y"]));
            return FixtureCatalog.Scene(cells, fixedCells, burning);
        }
        public static string ShortBurningMaterials(string original)
        {
            JObject copy = Parse(original);
            foreach (JObject material in (JArray)copy["materials"])
            {
                if ((int)material["id"] != 104) continue;
                material["ruleParameters"]["burnable"]["fuelTicks"] = 5;
                material["ruleParameters"]["burnable"]["spreadIntervalTicks"] = 2;
            }
            return copy.ToString(Formatting.Indented);
        }
        public static WorldSources NegativeCopy(WorldSources original, string caseId)
        {
            JObject materials = Parse(original.MaterialsText);
            JObject world = Parse(original.WorldConfigText);
            JObject scene = Parse(original.SceneText);
            JArray entries = (JArray)materials["materials"];
            string materialText = null;
            switch (caseId)
            {
                case "MissingField": entries[0]["name"].Parent.Remove(); break;
                case "DuplicateId": entries[1]["id"] = entries[0]["id"].DeepClone(); break;
                case "DuplicateName": entries[1]["name"] = entries[0]["name"].DeepClone(); break;
                case "DuplicateJsonKey": materialText = original.MaterialsText.Insert(original.MaterialsText.IndexOf('{') + 1, "\"schemaVersion\":1,"); break;
                case "UnknownRule": ((JArray)entries[0]["tags"]).Add("unknown"); break;
                case "WrongKind": entries[0]["kind"] = "solid"; break;
                case "InvalidParameter": entries[3]["ruleParameters"]["burnable"]["fuelTicks"] = 0; break;
                case "WrongSet": scene["materialSetId"] = "wrong-set"; break;
                case "OutOfBounds": scene["cells"][0]["x"] = (int)world["width"]; break;
                case "UnknownField": world["extra"] = true; break;
                case "UnsupportedVersion": world["schemaVersion"] = 2; break;
                case "UnknownMaterial": scene["cells"][0]["materialId"] = 65535; break;
                default: throw new ArgumentException("未知反例。", nameof(caseId));
            }
            return new WorldSources(materialText ?? materials.ToString(), world.ToString(), scene.ToString());
        }
        public static readonly string[] NegativeCaseIds =
        {
            "MissingField", "DuplicateId", "DuplicateName", "DuplicateJsonKey", "UnknownRule", "WrongKind",
            "InvalidParameter", "WrongSet", "OutOfBounds", "UnknownField", "UnsupportedVersion", "UnknownMaterial"
        };
    }
}
