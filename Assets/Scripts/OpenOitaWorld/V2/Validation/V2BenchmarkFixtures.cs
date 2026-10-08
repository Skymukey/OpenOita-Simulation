using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.V2.Validation
{
    internal enum V2BenchmarkLayout : byte
    {
        Current40K,
        Concentrated,
        Dispersed,
        Bodies,
        Fracture
    }

    internal enum V2BenchmarkMaterial : byte
    {
        Water,
        Steam,
        BurningWood,
        Mixed
    }

    internal sealed class V2BenchmarkScenario
    {
        internal string Name;
        internal int Width, Height, CellCount;
        internal int ActiveCount;
        internal V2BenchmarkLayout Layout;
        internal V2BenchmarkMaterial Material;
        internal bool WithPhysics, ActivityFixture;
        internal bool NaturalActivity, RotatingBodies, SleepBodies;
        internal bool UseBenchmarkCurrentAsset;
        internal bool RepeatResetBeforeTick;
        internal int FractureSupportX, FractureSupportY;
        internal int WakeLimit = 100000;
        internal int MinimumRuleAttempts;
        internal int ActiveFlowCount;
        internal readonly List<Vector2Int> WakeCells = new List<Vector2Int>();
        internal WorldSources Sources;

        internal bool RequiresRuleActivity => ActivityFixture && Material != V2BenchmarkMaterial.BurningWood;
        internal bool RequiresBurningActivity => ActivityFixture && Material == V2BenchmarkMaterial.BurningWood;
        internal bool IsFracture => Layout == V2BenchmarkLayout.Fracture;
        internal bool HasBodyFixture => RotatingBodies || SleepBodies;

        private const int BodyFixtureCount = 64;
        private const int BodyCellsPerFixture = 128;
        private const int BodyFixtureCellCount = BodyFixtureCount * BodyCellsPerFixture;

        internal WorldSources Build(WorldSources baseline)
        {
            if (UseBenchmarkCurrentAsset)
                throw new InvalidOperationException("current40k 必须从 Resources/OpenOita/BenchmarkCurrent.asset 读取。");
            JObject materials = JObject.Parse(baseline.MaterialsText);
            JObject config = JObject.Parse(baseline.WorldConfigText);
            materials["schemaVersion"] = 2;
            config["schemaVersion"] = 2;
            config.Remove("chunkSize");
            config["width"] = Width;
            config["height"] = Height;
            if (HasBodyFixture) config["gravityY"] = 0;
            JObject limits = (JObject)config["limits"];
            limits["maxMaterialCells"] = Math.Max(CellCount + 1024, 2000000);
            if (WithPhysics)
            {
                limits["maxDynamicBodies"] = 64;
                // One-million-cell fixed terrain is intentionally part of the full
                // physics benchmark. Keep shape capacity explicit so this fixture is
                // rejected only for a real solver limit, never for the old 16k demo cap.
                limits["maxShapesPerBody"] = 16384;
                limits["maxTotalShapes"] = 1000000;
            }
            if (ActivityFixture) ConfigureLongLivedMaterials(materials);
            string scene = BuildScene((string)materials["materialSetId"]);
            Sources = new WorldSources(materials.ToString(Newtonsoft.Json.Formatting.None),
                config.ToString(Newtonsoft.Json.Formatting.None), scene);
            return Sources;
        }

        private string BuildScene(string materialSetId)
        {
            var cells = new StringBuilder(Math.Max(256, CellCount * 32));
            var fixedCells = new StringBuilder(Math.Max(256, CellCount * 32));
            var burning = new StringBuilder(Math.Max(128, CellCount / 8));
            cells.Append('[');
            fixedCells.Append('[');
            burning.Append('[');
            bool firstCell = true, firstFixed = true, firstBurning = true;
            switch (Layout)
            {
                case V2BenchmarkLayout.Current40K:
                    throw new InvalidOperationException("current40k 不允许合成历史尺寸 fixture。");
                case V2BenchmarkLayout.Bodies:
                    AppendBodies(cells, fixedCells, burning, ref firstCell, ref firstFixed, ref firstBurning);
                    break;
                case V2BenchmarkLayout.Fracture:
                    AppendFracture(cells, fixedCells, ref firstCell, ref firstFixed);
                    break;
                default:
                    int ordinal = 0;
                    AppendField(cells, fixedCells, burning, ref firstCell, ref firstFixed, ref firstBurning,
                        ref ordinal, HasBodyFixture ? BodyFixtureCellCount : 0);
                    if (HasBodyFixture)
                        AppendBodies(cells, fixedCells, burning, ref firstCell, ref firstFixed, ref firstBurning, 1500);
                    break;
            }
            cells.Append(']');
            fixedCells.Append(']');
            burning.Append(']');
            return "{\"schemaVersion\":2,\"materialSetId\":\"" + Escape(materialSetId) +
                "\",\"worldConfig\":\"world_config.json\",\"materialsFile\":\"materials.json\",\"cells\":" +
                cells + ",\"fixedCells\":" + fixedCells + ",\"initialBurning\":" + burning + "}";
        }

        private void AppendFracture(StringBuilder cells, StringBuilder fixedCells,
            ref bool firstCell, ref bool firstFixed)
        {
            // Two dense 128x32 blocks share exactly one fixed keystone. Removing
            // that cell in the measured tick turns the concentrated structure into
            // two unfixed components, so the real extraction/split path is measured.
            const int leftX = 64, rightX = 193, baseY = 112, blockWidth = 128, blockHeight = 32;
            for (int y = baseY; y < baseY + blockHeight; y++)
            {
                for (int x = leftX; x < leftX + blockWidth; x++) AppendCell(cells, ref firstCell, x, y, 102);
                if (y == FractureSupportY) AppendCell(cells, ref firstCell, FractureSupportX, y, 102);
                for (int x = rightX; x < rightX + blockWidth; x++) AppendCell(cells, ref firstCell, x, y, 102);
            }
            AppendCoordinate(fixedCells, ref firstFixed, FractureSupportX, FractureSupportY);
        }

        private void AppendBodies(StringBuilder cells, StringBuilder fixedCells, StringBuilder burning,
            ref bool firstCell, ref bool firstFixed, ref bool firstBurning, int originY = 16)
        {
            // 64 disconnected 128-cell asymmetric islands.  The lower arm is narrower
            // than the upper arm, so the rotating variant receives a real contact torque
            // when gravity brings it to the solver boundary; the sleep variant is run as
            // a separate scenario and is allowed to settle.
            for (int y = 0; y < 8 * 40; y++)
                for (int bodyX = 0; bodyX < 8; bodyX++)
                {
                    int localY = y % 40;
                    if (localY >= 20) continue;
                    int width = localY < 10 ? 5 : localY < 19 ? 8 : 6;
                    for (int dx = 0; dx < width; dx++)
                        AppendCell(cells, ref firstCell, 16 + bodyX * 48 + dx,
                            originY + (y / 40) * 48 + localY, 104);
                }
        }

        private void AppendField(StringBuilder cells, StringBuilder fixedCells, StringBuilder burning,
            ref bool firstCell, ref bool firstFixed, ref bool firstBurning, ref int ordinal,
            int reservedBodyCells = 0)
        {
            // Every field has exactly ActiveCount two-cell horizontal cavities.  The
            // cavity has concrete at its top/bottom and both side walls; the fluid/gas/
            // burning cell alternates with an empty cell and therefore moves back and
            // forth without a per-tick Wake scan.  The remaining cells are fixed
            // concrete, so the benchmark still measures a one-million-cell world.
            int activeCount = ActiveCount <= 0 ? 100000 : ActiveCount;
            int strideX = Layout == V2BenchmarkLayout.Concentrated ? 4 : 6;
            int strideY = Layout == V2BenchmarkLayout.Concentrated ? 3 : 4;
            int columns = (Width - 4) / strideX + 1;
            if (columns <= 0) throw new InvalidOperationException("活动腔体布局宽度不足。");
            int rows = (activeCount + columns - 1) / columns;
            if ((rows - 1) * strideY + 3 > Height) throw new InvalidOperationException("活动腔体布局高度不足。");

            for (int row = 0; row < rows; row++)
            {
                int rowStart = row * columns;
                int rowCount = Math.Min(columns, activeCount - rowStart);
                int baseY = row * strideY;
                for (int index = 0; index < rowCount; index++)
                {
                    int x = index * strideX;
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x + 1, baseY);
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x + 2, baseY);
                }
                for (int index = 0; index < rowCount; index++)
                {
                    int x = index * strideX;
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x, baseY + 1);
                    ushort material = MaterialId(rowStart + index, x + 1, baseY + 1);
                    if (material == 101 || material == 103) ActiveFlowCount++;
                    AppendCell(cells, ref firstCell, x + 1, baseY + 1, material);
                    if (material == 104 && (Material == V2BenchmarkMaterial.BurningWood || Material == V2BenchmarkMaterial.Mixed))
                        AppendCoordinate(burning, ref firstBurning, x + 1, baseY + 1);
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x + 3, baseY + 1);
                }
                for (int index = 0; index < rowCount; index++)
                {
                    int x = index * strideX;
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x + 1, baseY + 2);
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x + 2, baseY + 2);
                }
            }

            int cavityFixedCount = activeCount * 6;
            int fillerCount = CellCount - reservedBodyCells - activeCount - cavityFixedCount;
            if (fillerCount < 0) throw new InvalidOperationException("固定材料与活动材料数量超过fixture总量。");
            int fillerStart = rows * strideY;
            int written = 0;
            for (int y = fillerStart; written < fillerCount; y++)
                for (int x = 0; x < Width && written < fillerCount; x++, written++)
                    AppendFixed(cells, fixedCells, ref firstCell, ref firstFixed, x, y);
            ordinal = activeCount;
        }

        private static void AppendFixed(StringBuilder cells, StringBuilder fixedCells,
            ref bool firstCell, ref bool firstFixed, int x, int y)
        {
            AppendCell(cells, ref firstCell, x, y, 102);
            AppendCoordinate(fixedCells, ref firstFixed, x, y);
        }

        private ushort MaterialId(int ordinal, int x, int y)
        {
            switch (Material)
            {
                case V2BenchmarkMaterial.Water: return 101;
                case V2BenchmarkMaterial.Steam: return 103;
                case V2BenchmarkMaterial.BurningWood: return 104;
                default:
                    // Keep nearly all mixed cells in the flow rules so a 100k fixture
                    // still has approximately 100k rule attempts while retaining a
                    // small, long-lived burning population for the event phase.
                    // Coordinates and ordinal are correlated in regular layouts.
                    // Use ordinal alone so every layout contains exactly 2% burning wood.
                    int mix = ordinal % 100;
                    if (mix < 49) return 101;
                    if (mix < 98) return 103;
                    return 104;
            }
        }

        internal void PrepareWakeCells(WorldSources sources)
        {
            WakeCells.Clear();
            if (!ActivityFixture || sources == null) return;
            if (NaturalActivity)
            {
                MinimumRuleAttempts = RequiresRuleActivity ? ActiveFlowCount : 0;
                return;
            }
            JObject scene = JObject.Parse(sources.SceneText);
            if (!(scene["cells"] is JArray cells)) return;
            foreach (JObject cell in cells)
            {
                ushort material = (ushort)cell["materialId"];
                if (material != 101 && material != 103) continue;
                WakeCells.Add(new Vector2Int((int)cell["x"], (int)cell["y"]));
                if (WakeCells.Count == WakeLimit) break;
            }
            MinimumRuleAttempts = RequiresRuleActivity ? Math.Min(WakeLimit, WakeCells.Count) : 0;
        }

        private static void ConfigureLongLivedMaterials(JObject materials)
        {
            foreach (JObject material in (JArray)materials["materials"])
            {
                int id = (int)material["id"];
                if (id == 101) material["ruleParameters"]["liquid_flow"]["moveIntervalTicks"] = 1;
                else if (id == 103)
                {
                    material["ruleParameters"]["gas_drift"]["moveIntervalTicks"] = 1;
                    material["ruleParameters"]["gas_drift"]["lifetimeTicks"] = 1000000;
                }
                else if (id == 104)
                {
                    material["ruleParameters"]["burnable"]["fuelTicks"] = 1000000;
                    material["ruleParameters"]["burnable"]["spreadIntervalTicks"] = 10;
                }
            }
        }

        private static void AppendCell(StringBuilder target, ref bool first, int x, int y, ushort material)
        {
            if (!first) target.Append(',');
            first = false;
            target.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y)
                .Append(",\"materialId\":").Append(material).Append('}');
        }

        private static void AppendCoordinate(StringBuilder target, ref bool first, int x, int y)
        {
            if (!first) target.Append(',');
            first = false;
            target.Append("{\"x\":").Append(x).Append(",\"y\":").Append(y).Append('}');
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    internal static class V2BenchmarkScenarioCatalog
    {
        internal static bool TryCreate(string name, out V2BenchmarkScenario scenario)
        {
            scenario = null;
            string key = (name ?? "current40k").Trim().ToLowerInvariant();
            if (key == "current40k" || key == "current")
                scenario = new V2BenchmarkScenario { Name = "current40k", Width = 960, Height = 640, CellCount = 33803,
                    Layout = V2BenchmarkLayout.Current40K, Material = V2BenchmarkMaterial.Mixed,
                    ActivityFixture = false, ActiveCount = 33803, WithPhysics = true,
                    UseBenchmarkCurrentAsset = true };
            else if (key == "water1m-concentrated")
                scenario = Field("water1m-concentrated", 2048, 2048, 100000, V2BenchmarkLayout.Concentrated, V2BenchmarkMaterial.Water);
            else if (key == "water100k-dispersed")
                scenario = Field("water100k-dispersed", 2048, 2048, 100000, V2BenchmarkLayout.Dispersed, V2BenchmarkMaterial.Water);
            else if (key == "steam1m-concentrated")
                scenario = Field("steam1m-concentrated", 2048, 2048, 100000, V2BenchmarkLayout.Concentrated, V2BenchmarkMaterial.Steam);
            else if (key == "steam100k-dispersed")
                scenario = Field("steam100k-dispersed", 2048, 2048, 100000, V2BenchmarkLayout.Dispersed, V2BenchmarkMaterial.Steam);
            else if (key == "burning1m-concentrated")
                scenario = Field("burning1m-concentrated", 2048, 2048, 100000, V2BenchmarkLayout.Concentrated, V2BenchmarkMaterial.BurningWood);
            else if (key == "burning100k-dispersed")
                scenario = Field("burning100k-dispersed", 2048, 2048, 100000, V2BenchmarkLayout.Dispersed, V2BenchmarkMaterial.BurningWood);
            else if (key == "mixed1m-concentrated")
                scenario = Field("mixed1m-concentrated", 2048, 2048, 100000, V2BenchmarkLayout.Concentrated, V2BenchmarkMaterial.Mixed);
            else if (key == "mixed100k-dispersed")
                scenario = Field("mixed100k-dispersed", 2048, 2048, 100000, V2BenchmarkLayout.Dispersed, V2BenchmarkMaterial.Mixed);
            else if (key == "mixed1m-bodies64-rotating")
                scenario = FieldWithBodies("mixed1m-bodies64-rotating", true, false);
            else if (key == "mixed1m-bodies64-sleeping")
                scenario = FieldWithBodies("mixed1m-bodies64-sleeping", false, true);
            else if (key == "bodies64-8192" || key == "bodies64-8192-rotating")
                scenario = new V2BenchmarkScenario { Name = "bodies64-8192-rotating", Width = 512, Height = 512, CellCount = 8192,
                    Layout = V2BenchmarkLayout.Bodies, Material = V2BenchmarkMaterial.BurningWood,
                    WithPhysics = true, RotatingBodies = true };
            else if (key == "bodies64-8192-sleep" || key == "bodies64-8192-sleeping")
                scenario = new V2BenchmarkScenario { Name = "bodies64-8192-sleeping", Width = 512, Height = 512, CellCount = 8192,
                    Layout = V2BenchmarkLayout.Bodies, Material = V2BenchmarkMaterial.BurningWood,
                    WithPhysics = true, SleepBodies = true };
            else if (key == "fracture8193-concentrated" || key == "fracture" || key == "structure-fracture")
                scenario = new V2BenchmarkScenario { Name = "fracture8193-concentrated", Width = 512, Height = 512, CellCount = 8193,
                    Layout = V2BenchmarkLayout.Fracture, Material = V2BenchmarkMaterial.Mixed,
                    WithPhysics = true, RepeatResetBeforeTick = true,
                    FractureSupportX = 192, FractureSupportY = 128 };
            return scenario != null;
        }

        private static V2BenchmarkScenario Field(string name, int width, int height, int activeCount,
            V2BenchmarkLayout layout, V2BenchmarkMaterial material) => new V2BenchmarkScenario
            {
                Name = name, Width = width, Height = height, CellCount = 1000000, ActiveCount = activeCount,
                Layout = layout, Material = material, WithPhysics = true, ActivityFixture = true,
                NaturalActivity = true
            };

        private static V2BenchmarkScenario FieldWithBodies(string name, bool rotating, bool sleeping) =>
            new V2BenchmarkScenario
            {
                Name = name, Width = 2048, Height = 2048, CellCount = 1000000, ActiveCount = 100000,
                Layout = V2BenchmarkLayout.Concentrated, Material = V2BenchmarkMaterial.Mixed,
                WithPhysics = true, ActivityFixture = true, NaturalActivity = true,
                RotatingBodies = rotating, SleepBodies = sleeping
            };
    }
}
