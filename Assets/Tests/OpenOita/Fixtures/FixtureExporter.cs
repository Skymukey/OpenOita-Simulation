using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    // 导出测试数据；不包含生产运行存档，也不新增公开速度 API。
    public static class FixtureExporter
    {
        private static JObject Point(Vector2Int point) => new JObject { ["x"] = point.x, ["y"] = point.y };
        private static JObject Vector(Vector2 point) => new JObject { ["x"] = point.x, ["y"] = point.y };
        private static JObject Config(WorldConfig config)
        {
            WorldLimits limits = config.Limits;
            return new JObject
            {
                ["schemaVersion"] = config.SchemaVersion, ["width"] = config.Width, ["height"] = config.Height,
                ["chunkSize"] = config.ChunkSize, ["cellSize"] = config.CellSize, ["stepSeconds"] = config.StepSeconds,
                ["gravityY"] = config.GravityY, ["seed"] = config.Seed,
                ["limits"] = new JObject
                {
                    ["maxMaterialCells"] = limits.MaxMaterialCells, ["maxDynamicBodies"] = limits.MaxDynamicBodies,
                    ["maxShapesPerBody"] = limits.MaxShapesPerBody, ["maxTotalShapes"] = limits.MaxTotalShapes,
                    ["maxChangesPerTick"] = limits.MaxChangesPerTick, ["maxLinearSpeed"] = limits.MaxLinearSpeed,
                    ["maxAngularSpeedDegrees"] = limits.MaxAngularSpeedDegrees, ["maxPhysicsSubsteps"] = limits.MaxPhysicsSubsteps,
                    ["fluidDisplacementRadius"] = limits.FluidDisplacementRadius
                }
            };
        }
        public static string Export(WorldSources baseline)
        {
            var fixtures = new List<ScenarioFixture>
            {
                FixtureCatalog.DrawingSave(baseline), FixtureCatalog.ApiLifecycle(),
                FixtureCatalog.Connection(), FixtureCatalog.Connection(true), FixtureCatalog.Connection(diagonal: true),
                FixtureCatalog.Cross(), FixtureCatalog.Cross(true), FixtureCatalog.RotatingStrip(), FixtureCatalog.RotatingStrip(true), FixtureCatalog.Ring(),
                FixtureCatalog.CollisionProbe("Flat"), FixtureCatalog.CollisionProbe("Corner"), FixtureCatalog.CollisionProbe("HighSpeed"),
                FixtureCatalog.Flow(), FixtureCatalog.Flow(true), FixtureCatalog.Flow(blocked: true), FixtureCatalog.Flow(true, true),
                FixtureCatalog.Transactions(), FixtureCatalog.Burning(), FixtureCatalog.Burning("Tank"), FixtureCatalog.Burning("PreFlow"),
                FixtureCatalog.Burning("Partial"), FixtureCatalog.Burning("RemoveThenIgnite"), FixtureCatalog.RotatedContact(),
                FixtureCatalog.RotatedContact(true), FixtureCatalog.RotatedContact(duringPhysics: true), FixtureCatalog.BurningSplit(),
                FixtureCatalog.DisplacementFailure(), FixtureCatalog.P2(), FixtureCatalog.P3(), FixtureCatalog.P4()
            };
            var scenarios = new JArray();
            foreach (ScenarioFixture fixture in fixtures)
            {
                var cells = new JArray(); var fixedCells = new JArray(); var burning = new JArray();
                foreach (InitialCell cell in fixture.Scene.Cells) cells.Add(new JObject { ["x"] = cell.Position.x, ["y"] = cell.Position.y, ["materialId"] = cell.MaterialId });
                foreach (Vector2Int cell in fixture.Scene.FixedCells) fixedCells.Add(Point(cell));
                foreach (Vector2Int cell in fixture.Scene.InitialBurning) burning.Add(Point(cell));
                var commands = new JArray();
                foreach (TimedCommand timed in fixture.Commands)
                {
                    MaterialCommand command = timed.Command;
                    commands.Add(new JObject { ["tick"] = timed.Tick, ["operation"] = command.Operation.ToString(), ["min"] = Vector(command.Region.Min),
                        ["max"] = Vector(command.Region.Max), ["materialId"] = command.MaterialId, ["generation"] = command.Generation });
                }
                var bodies = new JArray();
                foreach (BodyProbeState body in fixture.BodiesAfterCreate) bodies.Add(new JObject
                {
                    ["originalMinimum"] = Point(body.OriginalMinimum), ["position"] = Vector(body.Pose.Position),
                    ["angleRadians"] = body.Pose.AngleRadians, ["linearVelocity"] = Vector(body.Motion.LinearVelocity),
                    ["angularVelocityRadians"] = body.Motion.AngularVelocityRadians
                });
                var overrides = new JArray();
                foreach (CellStateProbe cell in fixture.CellsAfterCreate) overrides.Add(new JObject
                {
                    ["originalMinimum"] = Point(cell.OriginalBodyMinimum), ["localCell"] = Point(cell.LocalCell), ["state"] = JObject.FromObject(cell.State)
                });
                var observations = new JArray();
                foreach (FixtureObservation observation in fixture.Observations) observations.Add(new JObject
                {
                    ["tick"] = observation.Tick, ["stage"] = observation.Stage.ToString(), ["expected"] = observation.Expected
                });
                scenarios.Add(new JObject
                {
                    ["id"] = fixture.Id, ["config"] = Config(fixture.Config),
                    ["scene"] = new JObject { ["schemaVersion"] = 1, ["materialSetId"] = fixture.Scene.MaterialSetId, ["materialsFile"] = fixture.Scene.MaterialsFile,
                        ["worldConfig"] = fixture.Scene.WorldConfigFile, ["cells"] = cells, ["fixedCells"] = fixedCells, ["initialBurning"] = burning },
                    ["commands"] = commands, ["bodiesAfterCreate"] = bodies, ["cellsAfterCreate"] = overrides,
                    ["observations"] = observations, ["resetEveryTicks"] = fixture.ResetEveryTicks
                });
            }
            return new JObject
            {
                ["夹具版本"] = "M00-1", ["用途"] = "测试输入及预期；不是已通过的模拟结果。",
                ["长度单位"] = "Unity世界单位", ["角度单位"] = "弧度", ["材料基线"] = "docs/examples/materials.json",
                ["T2反例"] = new JArray(BaselineSources.NegativeCaseIds), ["T9材料副本"] = BaselineSources.Parse(BaselineSources.ShortBurningMaterials(baseline.MaterialsText)),
                ["P1"] = new JObject { ["width"] = 256, ["height"] = 256, ["分段规则"] = "index/16384+101", ["每材料格数"] = 16384, ["禁用行为"] = true },
                ["生命周期轮次"] = FixtureCatalog.LifecycleRounds, ["预热Tick"] = FixtureCatalog.PerformanceWarmupTicks,
                ["采样Tick"] = FixtureCatalog.PerformanceSampleTicks, ["P4重复次数"] = FixtureCatalog.P4Repetitions,
                ["scenarios"] = scenarios
            }.ToString(Formatting.Indented);
        }
    }
}
