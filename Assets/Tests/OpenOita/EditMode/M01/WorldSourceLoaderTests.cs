using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;

namespace OpenOita.Tests.EditMode.M01
{
    public sealed class WorldSourceLoaderTests
    {
        [Test]
        public void M01_01_BaselineAndTagPermutationHaveStableRuntimeSemantics()
        {
            WorldSources sources = BaselineSources.Read();
            WorldLoadResult loaded = new WorldSourceLoader().Load(sources);
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Assert.That(loaded.Materials.Count, Is.EqualTo(4));
            Assert.That(loaded.Scene.Cells.Count, Is.EqualTo(175));
            Assert.That(loaded.Scene.FixedCells.Count, Is.EqualTo(2));
            Assert.That(loaded.Scene.InitialBurning.Count, Is.EqualTo(1));
            Assert.That(loaded.Materials.TryGet(0, out var empty), Is.False);
            Assert.That(empty.Id, Is.Zero);
            Assert.That(loaded.Materials.GetByCompactIndex(0).Id, Is.Zero);
            JObject materials = JObject.Parse(sources.MaterialsText);
            foreach (JToken material in (JArray)materials["materials"])
                material["tags"] = new JArray(((JArray)material["tags"]).Reverse().Select(t => t.DeepClone()));
            materials["materials"] = new JArray(((JArray)materials["materials"]).Reverse().Select(t => t.DeepClone()));
            WorldLoadResult reordered = new WorldSourceLoader().Load(new WorldSources(materials.ToString(), sources.WorldConfigText, sources.SceneText));
            Assert.That(reordered.Result.IsSuccess, Is.True, reordered.Result.Diagnostic.Message);
            for (ushort id = 101; id <= 104; id++)
            {
                Assert.That(loaded.Materials.TryGet(id, out var before), Is.True);
                Assert.That(reordered.Materials.TryGet(id, out var after), Is.True);
                Assert.That(after, Is.EqualTo(before));
                Assert.That(loaded.Materials.GetByCompactIndex(before.CompactIndex), Is.EqualTo(before));
            }
            Assert.That(loaded.Rules.Count, Is.EqualTo(5));
            for (int i = 1; i <= 5; i++)
            {
                Assert.That(loaded.Rules.TryGet((RuleId)i, out var rule), Is.True);
                Assert.That(rule.Id, Is.EqualTo((RuleId)i));
            }
            Assert.That(loaded.Rules.TryGet((RuleId)0, out _), Is.False);
            Assert.That(loaded.Rules.TryGet((RuleId)6, out _), Is.False);
        }

        private static readonly string[] InvalidCases =
        {
            "重复JSON键", "嵌套重复键", "未知根字段", "字段大小写", "缺字段", "字符串数字", "NaN", "Infinity", "负Infinity", "浮点溢出", "整数溢出",
            "单引号", "注释", "尾逗号", "尾随内容", "数组根", "前导零", "null字段", "版本",
            "重复ID", "重复name", "未知标签", "重复标签", "标签大小写", "kind错配", "缺必需标签", "非法kind", "缺参数块", "多参数块", "灭火参数块",
            "未知参数", "时间零", "时间上限", "空名称", "空连接组", "非法颜色", "质量零", "质量负数", "材料零", "材料上限",
            "材料集", "未知材料", "重复坐标", "乱序格", "越界x", "越界y", "固定水", "固定空格", "初燃水", "初燃空格", "重复固定", "乱序固定", "乱序初燃",
            "引用路径", "世界引用", "世界版本", "场景版本", "宽零", "宽上限", "块长", "cellSize零", "cellSize下溢", "step零", "seed负数", "seed溢出",
            "限制零", "半径上限", "低格数", "极大预算", "极大形状", "世界长度溢出", "物理组合溢出"
        };
        public static IEnumerable<TestCaseData> NegativeCases()
        {
            foreach (string name in InvalidCases)
            {
                string id = Array.IndexOf(InvalidCases, name) < 19 ? "02" : Array.IndexOf(InvalidCases, name) < 40 ? "03" : Array.IndexOf(InvalidCases, name) < 57 ? "04" : "06";
                yield return new TestCaseData(name).SetName("M01_" + id + "_拒绝_" + name);
            }
        }
        [TestCaseSource(nameof(NegativeCases))]
        public void InvalidInputFailsWithoutPartialPublication(string name)
        {
            WorldSources original = BaselineSources.Read();
            var loader = new WorldSourceLoader();
            WorldLoadResult good = loader.Load(original);
            JObject materials = JObject.Parse(original.MaterialsText), world = JObject.Parse(original.WorldConfigText), scene = JObject.Parse(original.SceneText);
            JToken water = materials["materials"][0], wood = materials["materials"][3];
            string materialText = null, worldText = null;
            switch (name)
            {
                case "重复JSON键": materialText = original.MaterialsText.Insert(1, "\"schemaVersion\":1,"); break;
                case "嵌套重复键": materialText = original.MaterialsText.Replace("\"fuelTicks\": 250", "\"fuelTicks\":250,\"fuelTicks\":250"); break;
                case "未知根字段": materials["extra"] = 1; break;
                case "字段大小写": materials["SchemaVersion"] = 1; materials.Remove("schemaVersion"); break;
                case "缺字段": ((JObject)water).Remove("name"); break;
                case "字符串数字": water["id"] = "101"; break;
                case "NaN": materialText = original.MaterialsText.Replace("\"massPerCell\": 1", "\"massPerCell\": NaN"); break;
                case "Infinity": materialText = original.MaterialsText.Replace("\"massPerCell\": 1", "\"massPerCell\": Infinity"); break;
                case "负Infinity": materialText = original.MaterialsText.Replace("\"massPerCell\": 1", "\"massPerCell\": -Infinity"); break;
                case "浮点溢出": materialText = original.MaterialsText.Replace("\"massPerCell\": 1", "\"massPerCell\": 1e1000"); break;
                case "整数溢出": materialText = original.MaterialsText.Replace("\"id\": 101", "\"id\": 99999999999999999999999999999999999999"); break;
                case "单引号": materialText = original.MaterialsText.Replace("\"schemaVersion\"", "'schemaVersion'"); break;
                case "注释": materialText = original.MaterialsText.Insert(1, "/* 注释 */"); break;
                case "尾逗号": materialText = "{\"schemaVersion\":1,}"; break;
                case "尾随内容": materialText = original.MaterialsText + "{}"; break;
                case "数组根": materialText = "[]"; break;
                case "前导零": materialText = original.MaterialsText.Replace("\"id\": 101", "\"id\": 0101"); break;
                case "null字段": water["name"] = JValue.CreateNull(); break;
                case "版本": materials["schemaVersion"] = 2; break;
                case "重复ID": materials["materials"][1]["id"] = 101; break;
                case "重复name": materials["materials"][1]["name"] = "水"; break;
                case "未知标签": ((JArray)water["tags"]).Add("unknown"); break;
                case "重复标签": ((JArray)water["tags"]).Add("liquid_flow"); break;
                case "标签大小写": water["tags"][0] = "Liquid_flow"; break;
                case "kind错配": water["kind"] = "solid"; break;
                case "缺必需标签": water["tags"] = new JArray("extinguishes_fire"); water["ruleParameters"] = new JObject(); break;
                case "非法kind": water["kind"] = "Liquid"; break;
                case "缺参数块": water["ruleParameters"] = new JObject(); break;
                case "多参数块": water["ruleParameters"]["burnable"] = new JObject(); break;
                case "灭火参数块": water["ruleParameters"]["extinguishes_fire"] = new JObject(); break;
                case "未知参数": wood["ruleParameters"]["burnable"]["extra"] = 1; break;
                case "时间零": wood["ruleParameters"]["burnable"]["fuelTicks"] = 0; break;
                case "时间上限": wood["ruleParameters"]["burnable"]["fuelTicks"] = 1000001; break;
                case "空名称": water["name"] = "  "; break;
                case "空连接组": wood["ruleParameters"]["structure"]["connectionGroup"] = " "; break;
                case "非法颜色": water["color"] = "#FF00GG"; break;
                case "质量零": water["massPerCell"] = 0; break;
                case "质量负数": water["massPerCell"] = -1; break;
                case "材料零": water["id"] = 0; break;
                case "材料上限": water["id"] = 65536; break;
                case "材料集": scene["materialSetId"] = "other"; break;
                case "未知材料": scene["cells"][0]["materialId"] = 65535; break;
                case "重复坐标": scene["cells"][1] = scene["cells"][0].DeepClone(); scene["cells"][1]["materialId"] = 104; break;
                case "乱序格": Reverse(scene, "cells"); break;
                case "越界x": scene["cells"][0]["x"] = 256; break;
                case "越界y": scene["cells"][0]["y"] = 256; break;
                case "固定水": scene["fixedCells"] = new JArray(Coordinate((JArray)scene["cells"], 101)); break;
                case "固定空格": scene["fixedCells"] = new JArray(new JObject { ["x"] = 0, ["y"] = 255 }); break;
                case "初燃水": scene["initialBurning"] = new JArray(Coordinate((JArray)scene["cells"], 101)); break;
                case "初燃空格": scene["initialBurning"] = new JArray(new JObject { ["x"] = 0, ["y"] = 255 }); break;
                case "重复固定": ((JArray)scene["fixedCells"]).Add(scene["fixedCells"][0].DeepClone()); break;
                case "乱序固定": Reverse(scene, "fixedCells"); break;
                case "乱序初燃": scene["initialBurning"] = new JArray(((JArray)scene["cells"]).Where(t => (int)t["materialId"] == 104).Take(2).Reverse().Select(t => new JObject { ["x"] = t["x"].DeepClone(), ["y"] = t["y"].DeepClone() })); break;
                case "引用路径": scene["materialsFile"] = "../materials.json"; break;
                case "世界引用": scene["worldConfig"] = "World_config.json"; break;
                case "世界版本": world["schemaVersion"] = 2; break;
                case "场景版本": scene["schemaVersion"] = 2; break;
                case "宽零": world["width"] = 0; break;
                case "宽上限": world["width"] = ContractDefaults.MaxWorldDimension + 1; break;
                case "块长": world["chunkSize"] = 64; break;
                case "cellSize零": world["cellSize"] = 0; break;
                case "cellSize下溢": worldText = original.WorldConfigText.Replace("\"cellSize\": 0.1", "\"cellSize\": 1e-100"); break;
                case "step零": world["stepSeconds"] = 0; break;
                case "seed负数": world["seed"] = -1; break;
                case "seed溢出": world["seed"] = 4294967296L; break;
                case "限制零": world["limits"]["maxDynamicBodies"] = 0; break;
                case "半径上限": world["limits"]["fluidDisplacementRadius"] = 4097; break;
                case "低格数": world["limits"]["maxMaterialCells"] = 174; break;
                case "极大预算":
                    loader = new WorldSourceLoader(ContractDefaults.ConfiguredCpuBudgetBytes);
                    world["limits"]["maxMaterialCells"] = int.MaxValue;
                    break;
                case "极大形状":
                    loader = new WorldSourceLoader(ContractDefaults.ConfiguredCpuBudgetBytes);
                    world["limits"]["maxTotalShapes"] = int.MaxValue;
                    break;
                case "世界长度溢出": world["cellSize"] = 1e38; break;
                case "物理组合溢出": world["stepSeconds"] = 1e30; break;
                default: throw new ArgumentException(name);
            }
            WorldLoadResult failed = loader.Load(new WorldSources(materialText ?? materials.ToString(), worldText ?? world.ToString(), scene.ToString()));
            Assert.That(failed.Result.IsSuccess, Is.False, name);
            Assert.That(failed.Result.ErrorCode, Is.Not.EqualTo(WorldErrorCode.None));
            Assert.That(failed.Result.Diagnostic.FileName, Is.Not.Null.And.Not.Empty);
            Assert.That(failed.Result.Diagnostic.Target, Is.Not.Null.And.Not.Empty);
            Assert.That(failed.Config, Is.Null); Assert.That(failed.Scene, Is.Null);
            Assert.That(failed.Materials, Is.Null); Assert.That(failed.Rules, Is.Null);
            Assert.That(good.Materials.Count, Is.EqualTo(4)); Assert.That(good.Scene.Cells.Count, Is.EqualTo(175));
            Assert.That(loader.Load(original).Result.IsSuccess, Is.True);
        }
        [Test]
        public void DisabledDefaultMemoryBudgetAllowsReservationButExplicitBudgetStillRejectsIt()
        {
            WorldSources original = BaselineSources.Read();
            JObject config = JObject.Parse(original.WorldConfigText);
            config["limits"]["maxMaterialCells"] = 8000000;
            var sources = new WorldSources(original.MaterialsText, config.ToString(), original.SceneText);

            WorldLoadResult unrestricted = new WorldSourceLoader().Load(sources);
            Assert.That(unrestricted.Result.IsSuccess, Is.True);
            Assert.That(unrestricted.Scene.Cells.Count, Is.EqualTo(175));

            WorldLoadResult limited = new WorldSourceLoader(ContractDefaults.ConfiguredCpuBudgetBytes).Load(sources);
            Assert.That(limited.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(limited.Config, Is.Null);
            Assert.That(limited.Scene, Is.Null);
        }

        private static void Reverse(JObject scene, string field) => scene[field] = new JArray(((JArray)scene[field]).Reverse().Select(t => t.DeepClone()));
        private static JObject Coordinate(JArray cells, int id)
        {
            JToken cell = cells.First(t => (int)t["materialId"] == id);
            return new JObject { ["x"] = cell["x"].DeepClone(), ["y"] = cell["y"].DeepClone() };
        }

        [TestCase("MissingField", "materials.json", "materials[0].name", WorldErrorCode.InvalidConfig)]
        [TestCase("UnknownRule", "materials.json", "materials[0].tags[2]", WorldErrorCode.UnknownRule)]
        [TestCase("UnsupportedVersion", "world_config.json", "schemaVersion", WorldErrorCode.UnsupportedVersion)]
        [TestCase("UnknownMaterial", "scene.json", "cells[0].materialId", WorldErrorCode.UnknownMaterial)]
        [TestCase("OutOfBounds", "scene.json", "cells[0].x", WorldErrorCode.OutOfBounds)]
        public void M01_02_DiagnosticsCarryExactFilePathAndCode(string caseId, string file, string path, WorldErrorCode code)
        {
            var sources = BaselineSources.NegativeCopy(BaselineSources.Read(), caseId);
            var result = new WorldSourceLoader().Load(sources).Result;
            Assert.That(result.Diagnostic.FileName, Is.EqualTo(file));
            Assert.That(result.Diagnostic.Target, Is.EqualTo(path));
            Assert.That(result.ErrorCode, Is.EqualTo(code));
            TestContext.WriteLine($"{result.Diagnostic.FileName} | {result.Diagnostic.Target} | {result.ErrorCode} | {result.Diagnostic.Message}");
        }

        [Test]
        public void M01_02_NullTextStillCarriesCanonicalDiagnosticFileName()
        {
            var result = new WorldSourceLoader().Load(new WorldSources(null, "{}", "{}", null, null, null));
            Assert.That(result.Result.Diagnostic.FileName, Is.EqualTo("materials.json"));
            Assert.That(result.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidConfig));
            Assert.That(result.Materials, Is.Null);
            Assert.That(new WorldSourceLoader().Load(null).Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
        }

        [Test]
        public void M01_03_NamesUseOrdinalCaseSensitiveUniquenessAndMaximumIdIsValid()
        {
            WorldSources sources = BaselineSources.Read(); JObject materials = JObject.Parse(sources.MaterialsText);
            materials["materials"][0]["name"] = "Water"; materials["materials"][1]["name"] = "water";
            JObject extra = (JObject)materials["materials"][0].DeepClone(); extra["id"] = 65535; extra["name"] = "EXTRA";
            ((JArray)materials["materials"]).Add(extra);
            WorldLoadResult result = new WorldSourceLoader().Load(new WorldSources(materials.ToString(), sources.WorldConfigText, sources.SceneText));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            Assert.That(result.Materials.TryGet(65535, out var entry), Is.True);
            Assert.That(entry.CompactIndex, Is.EqualTo(5));
        }

        [Test]
        public void M01_01_NumericParsingAndSerializationAreIndependentOfOperatingSystemCulture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                WorldLoadResult loaded = new WorldSourceLoader().Load(BaselineSources.Read());
                Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
                Assert.That(loaded.Config.CellSize, Is.EqualTo(0.1f));
                Assert.That(ConfigurationSerializer.Serialize(loaded.Config, loaded.Scene, loaded.Materials, out var text).IsSuccess, Is.True);
                Assert.That(new WorldSourceLoader().Load(text).Result.IsSuccess, Is.True);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void M01_06_ExtremeDimensionsStaySparseAndLowMemoryFailsBeforePublication()
        {
            WorldSources sources = BaselineSources.Read(); JObject world = JObject.Parse(sources.WorldConfigText);
            world["width"] = 4096; world["height"] = 4096; world["seed"] = uint.MaxValue;
            // 独立上限无隐式乘积预分配，合法较大单体上限仍受全局形状上限约束。
            world["limits"]["maxShapesPerBody"] = int.MaxValue;
            var changed = new WorldSources(sources.MaterialsText, world.ToString(), sources.SceneText);
            Assert.That(new WorldSourceLoader().Load(changed).Result.IsSuccess, Is.True);
            WorldLoadResult low = new WorldSourceLoader(8 * 1024 * 1024).Load(changed);
            Assert.That(low.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(low.Result.Diagnostic.Target, Is.EqualTo("limits"));
            Assert.That(low.Materials, Is.Null);
            Assert.That(new WorldSourceLoader(1024).Load(changed).Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
        }

        [Test]
        public void M01_06_LargeCanvasRightTopCellLoadsAndRoundTripsThroughSerialization()
        {
            WorldSources baseline = BaselineSources.Read();
            JObject world = JObject.Parse(baseline.WorldConfigText);
            JObject scene = JObject.Parse(baseline.SceneText);
            world["width"] = 9600;
            world["height"] = 6400;
            ((JArray)scene["cells"]).Add(new JObject { ["x"] = 9599, ["y"] = 6399, ["materialId"] = 104 });
            ((JArray)scene["fixedCells"]).Add(new JObject { ["x"] = 9599, ["y"] = 6399 });
            ((JArray)scene["initialBurning"]).Add(new JObject { ["x"] = 9599, ["y"] = 6399 });

            WorldSources large = new WorldSources(baseline.MaterialsText, world.ToString(), scene.ToString());
            WorldLoadResult loaded = new WorldSourceLoader().Load(large);
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Assert.That(loaded.Config.Width, Is.EqualTo(9600));
            Assert.That(loaded.Config.Height, Is.EqualTo(6400));
            Assert.That(loaded.Scene.Cells.Count, Is.EqualTo(176));
            Assert.That(loaded.Scene.Cells[loaded.Scene.Cells.Count - 1].Position, Is.EqualTo(new UnityEngine.Vector2Int(9599, 6399)));

            Assert.That(ConfigurationSerializer.Serialize(loaded.Config, loaded.Scene, loaded.Materials, out WorldSources serialized).IsSuccess, Is.True);
            JObject savedScene = JObject.Parse(serialized.SceneText);
            JArray savedCells = (JArray)savedScene["cells"];
            JToken savedTail = savedCells[savedCells.Count - 1];
            Assert.That((int)savedTail["x"], Is.EqualTo(9599));
            Assert.That((int)savedTail["y"], Is.EqualTo(6399));

            WorldLoadResult roundTrip = new WorldSourceLoader().Load(serialized);
            Assert.That(roundTrip.Result.IsSuccess, Is.True, roundTrip.Result.Diagnostic.Message);
            Assert.That(roundTrip.Config.Width, Is.EqualTo(9600));
            Assert.That(roundTrip.Config.Height, Is.EqualTo(6400));
            Assert.That(roundTrip.Scene.Cells[roundTrip.Scene.Cells.Count - 1].Position, Is.EqualTo(new UnityEngine.Vector2Int(9599, 6399)));
            Assert.That(roundTrip.Scene.FixedCells[roundTrip.Scene.FixedCells.Count - 1], Is.EqualTo(new UnityEngine.Vector2Int(9599, 6399)));
            Assert.That(roundTrip.Scene.InitialBurning[roundTrip.Scene.InitialBurning.Count - 1], Is.EqualTo(new UnityEngine.Vector2Int(9599, 6399)));
        }

        [TestCase(16384, 16384, true)]
        [TestCase(16385, 16384, false)]
        [TestCase(16384, 16385, false)]
        [TestCase(0, 16384, false)]
        [TestCase(16384, 0, false)]
        public void M01_06_WorldDimensionContractAcceptsMaximumAndRejectsOutside(int width, int height, bool expectedSuccess)
        {
            WorldSources baseline = BaselineSources.Read();
            JObject world = JObject.Parse(baseline.WorldConfigText);
            world["width"] = width;
            world["height"] = height;

            WorldLoadResult result = new WorldSourceLoader().Load(new WorldSources(
                baseline.MaterialsText, world.ToString(), baseline.SceneText));
            Assert.That(result.Result.IsSuccess, Is.EqualTo(expectedSuccess), result.Result.Diagnostic.Message);
            if (expectedSuccess)
            {
                Assert.That(result.Config.Width, Is.EqualTo(ContractDefaults.MaxWorldDimension));
                Assert.That(result.Config.Height, Is.EqualTo(ContractDefaults.MaxWorldDimension));
            }
            else
            {
                Assert.That(result.Result.ErrorCode, Is.Not.EqualTo(WorldErrorCode.None));
                Assert.That(result.Config, Is.Null);
                Assert.That(result.Scene, Is.Null);
            }
        }

        [Test]
        public void M01_04_LoadedInitialStateIsImmutableAcrossTwentyIndependentLoads()
        {
            var loader = new WorldSourceLoader(); var sources = BaselineSources.Read(); var first = loader.Load(sources);
            for (int i = 0; i < 20; i++)
            {
                var next = loader.Load(sources); Assert.That(next.Materials, Is.Not.SameAs(first.Materials));
                Assert.That(next.Scene, Is.Not.SameAs(first.Scene));
            }
            Assert.Throws<NotSupportedException>(() => ((IList<InitialCell>)first.Scene.Cells).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<UnityEngine.Vector2Int>)first.Scene.FixedCells).Clear());
            Assert.That(first.Scene.Cells.Count, Is.EqualTo(175));
        }
    }
}
