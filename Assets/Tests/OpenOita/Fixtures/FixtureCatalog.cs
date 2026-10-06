using System;
using System.Collections.Generic;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    public static class FixtureCatalog
    {
        public const int LifecycleRounds = 20;
        public const int PerformanceWarmupTicks = 1000;
        public const int PerformanceSampleTicks = 10000;
        public const int P4Repetitions = 200;
        public static ScenarioFixture DrawingSave(WorldSources baseline)
        {
            return new ScenarioFixture("T1", Config(), BaselineSources.InitialScene(baseline), Array.Empty<TimedCommand>(), Array.Empty<BodyProbeState>(), new[]
            {
                new FixtureObservation(0, TickStage.Publish, "175格/2固定/1初燃保存重开三列表完全一致。"),
                new FixtureObservation(100, TickStage.Publish, "试玩副本不回写编辑态；Reset重建generation新代次Tick0。")
            });
        }
        public static ScenarioFixture ApiLifecycle()
        {
            return Make("T8", Config(), Scene(Array.Empty<InitialCell>()), "真实Create→Enqueue→Step→Query→Reset→Dispose；API探针只证签名，下游实际接入另验。",
                new[] { Command(1, MaterialOperation.Spawn, 5, 5, 101), Command(2, MaterialOperation.Replace, 5, 4, 103), Command(3, MaterialOperation.Remove, 5, 5) });
        }
        public static WorldConfig Config(int width = 256, int height = 256, int maxBodies = 64, float gravityY = -9.81f, int maxChanges = 65536)
        {
            return new WorldConfig(1, width, height, 128, 0.1f, 0.02f, gravityY, 1,
                new WorldLimits(65536, maxBodies, 256, 4096, maxChanges, 5, 180, 8, 16));
        }
        public static SceneInitialData Scene(IEnumerable<InitialCell> cells, IEnumerable<Vector2Int> fixedCells = null, IEnumerable<Vector2Int> burning = null)
        {
            return new SceneInitialData(1, "openoita-core-v1", "materials.json", "world_config.json", cells,
                fixedCells ?? Array.Empty<Vector2Int>(), burning ?? Array.Empty<Vector2Int>());
        }
        public static WorldRect CellRegion(int x, int y, float size = 0.1f) => new WorldRect(new Vector2(x * size, y * size), new Vector2((x + 1) * size, (y + 1) * size));
        public static TimedCommand Command(ulong tick, MaterialOperation operation, int x, int y, ushort material = 0) =>
            new TimedCommand(tick, new MaterialCommand(operation, CellRegion(x, y), material, 1));
        private static ScenarioFixture Make(string id, WorldConfig config, SceneInitialData scene, string expected,
            IEnumerable<TimedCommand> commands = null, IEnumerable<BodyProbeState> bodies = null, int resetEveryTicks = 0)
        {
            return new ScenarioFixture(id, config, scene, commands ?? Array.Empty<TimedCommand>(), bodies ?? Array.Empty<BodyProbeState>(),
                new[] { new FixtureObservation(1, TickStage.Publish, expected) }, resetEveryTicks);
        }
        public static ScenarioFixture Connection(bool crossChunk = false, bool diagonal = false)
        {
            int x = crossChunk ? 127 : 10;
            var cells = new[] { new InitialCell(x, 10, 102), new InitialCell(x + 1, 10, 104), new InitialCell(x + 2, diagonal ? 11 : 10, 104) };
            return Make(crossChunk ? "T3-CrossChunk" : diagonal ? "T3-Diagonal" : "T3", Config(), Scene(cells, new[] { new Vector2Int(x, 10) }),
                diagonal ? "对角末格不与固定结构连接。" : "Tick0 三格固定；删连接格后末格成为单格体。", new[] { Command(1, MaterialOperation.Remove, x + 1, 10) });
        }
        public static ScenarioFixture Cross(bool capacityFailure = false)
        {
            var cells = new List<InitialCell> { new InitialCell(4, 4, 104) };
            for (int distance = 1; distance <= 2; distance++)
            {
                cells.Add(new InitialCell(4 - distance, 4, 104)); cells.Add(new InitialCell(4 + distance, 4, 104));
                cells.Add(new InitialCell(4, 4 - distance, 104)); cells.Add(new InitialCell(4, 4 + distance, 104));
            }
            return Make(capacityFailure ? "T4-Capacity3" : "T4", Config(16, 16, capacityFailure ? 3 : 64), Scene(cells, new[] { new Vector2Int(4, 4) }),
                capacityFailure ? "命令整拒，原9格及固定点不变。" : "全部生成4个两格动态体。", new[] { Command(1, MaterialOperation.Remove, 4, 4) });
        }
        public static Vector2 LocalCenter(BodyPose pose, int x, int y, float size = 0.1f)
        {
            double cos = Math.Cos(pose.AngleRadians), sin = Math.Sin(pose.AngleRadians);
            double localX = (x + 0.5) * size, localY = (y + 0.5) * size;
            return pose.Position + new Vector2((float)(cos * localX - sin * localY), (float)(sin * localX + cos * localY));
        }
        public static ScenarioFixture RotatingStrip(bool removeEnd = false)
        {
            var pose = new BodyPose(new Vector2(1, 1), (float)(Math.PI / 6));
            var body = new BodyProbeState(new Vector2Int(10, 10), pose, new BodyMotion(new Vector2(0.25f, -0.1f), 0.5f));
            Vector2 center = LocalCenter(pose, removeEnd ? 2 : 1, 0);
            var region = new WorldRect(center - Vector2.one * 0.01f, center + Vector2.one * 0.01f);
            var command = new TimedCommand(1, new MaterialCommand(MaterialOperation.Remove, region, 0, 1));
            return new ScenarioFixture(removeEnd ? "T5-End" : "T5-Split", Config(),
                Scene(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 104), new InitialCell(12, 10, 104) }),
                new[] { command }, new[] { body }, new[]
                {
                    new FixtureObservation(1, TickStage.Structure, "在结构应用后、物理前检查世界中心误差≤1e-5，线/角速度误差≤1e-4，质量惯量相对误差≤1e-4；删中间产生两单格，删端保留旧ID。"),
                    new FixtureObservation(1, TickStage.Publish, "物理后微穿透≤0.01世界单位。")
                });
        }
        public static ScenarioFixture Ring()
        {
            var cells = new List<InitialCell>();
            for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++)
                if (x != 1 || y != 1) cells.Add(new InitialCell(20 + x, 20 + y, 104));
            return Make("T5-Ring", Config(), Scene(cells), "中心孔无材料/碰撞/查询命中。", bodies: new[]
            {
                new BodyProbeState(new Vector2Int(20, 20), new BodyPose(new Vector2(2, 2), (float)(Math.PI / 6)), new BodyMotion(Vector2.zero, 0))
            });
        }
        public static ScenarioFixture CollisionProbe(string variant)
        {
            var cells = new List<InitialCell>(); var fixedCells = new List<Vector2Int>();
            for (int x = 0; x < 32; x++) { cells.Add(new InitialCell(x, 0, 102)); fixedCells.Add(new Vector2Int(x, 0)); }
            cells.Add(new InitialCell(20, 20, 104));
            float angle = 0;
            if (variant == "Corner")
            {
                cells.Add(new InitialCell(9, 1, 102)); fixedCells.Add(new Vector2Int(9, 1)); angle = (float)(Math.PI / 6);
            }
            else if (variant != "Flat" && variant != "HighSpeed") throw new ArgumentException("未知碰撞探针。", nameof(variant));
            return new ScenarioFixture("T5-" + variant, Config(), Scene(cells, fixedCells), Array.Empty<TimedCommand>(), new[]
            {
                new BodyProbeState(new Vector2Int(20, 20), new BodyPose(new Vector2(1.05f, variant == "HighSpeed" ? 0.2f : 0.3f), angle),
                    new BodyMotion(new Vector2(0, variant == "HighSpeed" ? -5 : -0.25f), 0))
            }, new[] { new FixtureObservation(10, TickStage.Publish, "逐子步完整格/边界微穿透≤0.01；超速/超子步即Faulted，不裁剪。") });
        }
        public static ScenarioFixture BurningSplit()
        {
            ScenarioFixture strip = RotatingStrip();
            var overrides = new List<CellStateProbe>();
            for (int x = 0; x < 3; x++) overrides.Add(new CellStateProbe(new Vector2Int(10, 10), new Vector2Int(x, 0), new CellSnapshot(104, 1, 3, 1)));
            return new ScenarioFixture("T9-Split", strip.Config, strip.Scene, strip.Commands, strip.BodiesAfterCreate,
                new[]
                {
                    new FixtureObservation(1, TickStage.Commands, "命令拆分应用后每个剩余格余量3，燃烧状态随实例搬运。"),
                    new FixtureObservation(1, TickStage.Publish, "燃烧阶段后每个剩余格余量2；旧ID火焰关联撤销。")
                }, cellsAfterCreate: overrides);
        }
        public static ScenarioFixture DisplacementFailure()
        {
            var cells = new List<InitialCell> { new InitialCell(5, 5, 101), new InitialCell(10, 10, 104) };
            var fixedCells = new List<Vector2Int>();
            for (int y = 4; y <= 6; y++) for (int x = 4; x <= 6; x++)
            {
                if (x == 5 && y == 5) continue;
                cells.Add(new InitialCell(x, y, 102)); fixedCells.Add(new Vector2Int(x, y));
            }
            return Make("T7-T9-DisplacementFailure", Config(), Scene(cells, fixedCells),
                "Tick1物理子步候选通过测试探针置单格体原点(0.5,0.5)，与密闭水格重叠；排开无目标，水不丢、Faulted、版本停Tick0。",
                bodies: new[] { new BodyProbeState(new Vector2Int(10, 10), new BodyPose(new Vector2(1, 1), 0), new BodyMotion(Vector2.zero, 0)) });
        }
        public static ScenarioFixture Flow(bool steam = false, bool blocked = false)
        {
            var cells = new List<InitialCell> { new InitialCell(5, 5, (ushort)(steam ? 103 : 101)) };
            var fixedCells = new List<Vector2Int>();
            if (blocked)
            {
                int direction = steam ? 1 : -1;
                for (int x = 4; x <= 6; x++) { cells.Add(new InitialCell(x, 5 + direction, 102)); fixedCells.Add(new Vector2Int(x, 5 + direction)); }
                cells.Add(new InitialCell(4, 5, 102)); cells.Add(new InitialCell(6, 5, 102));
                fixedCells.Add(new Vector2Int(4, 5)); fixedCells.Add(new Vector2Int(6, 5));
            }
            return new ScenarioFixture(blocked ? steam ? "T6-SteamBlocked" : "T6-WaterBlocked" : steam ? "T6-Steam" : "T6-Water", Config(16, 256), Scene(cells, fixedCells),
                Array.Empty<TimedCommand>(), Array.Empty<BodyProbeState>(), new[]
                {
                    new FixtureObservation(1, TickStage.Publish, blocked ? "水汽保持(5,5)，不穿墙角。" : steam ? "蒸汽到(5,6)。" : "水到(5,4)。"),
                    new FixtureObservation(steam ? 4UL : 3UL, TickStage.Publish, blocked ? "无穿透；只更新倒计时/寿命。" : steam ? "蒸汽到(5,7)。" : "水到(5,3)，Tick2不动。"),
                    new FixtureObservation(200, TickStage.Publish, steam ? "默认蒸汽消散；减少记LifetimeExpired。" : "除显式增减外水数守恒。")
                });
        }
        public static ScenarioFixture Transactions()
        {
            var scene = Scene(new[] { new InitialCell(128, 10, 104) }, new[] { new Vector2Int(128, 10) });
            var region = new WorldRect(new Vector2(12.7f, 1), new Vector2(12.9f, 1.1f));
            return Make("T7", Config(), scene, "跨块Spawn整拒；同ID Replace跳空且撤固定/重置状态；注入失败不发布工作版本；令牌分类及20轮生命周期见合同断言。", new[]
            {
                new TimedCommand(1, new MaterialCommand(MaterialOperation.Spawn, region, 104, 1)),
                new TimedCommand(2, new MaterialCommand(MaterialOperation.Replace, region, 104, 1))
            });
        }
        public static ScenarioFixture Burning(string variant = "Spread")
        {
            var cells = new List<InitialCell> { new InitialCell(10, 10, 104) };
            var fixedCells = new List<Vector2Int> { new Vector2Int(10, 10) };
            var commands = new List<TimedCommand>();
            var observations = new List<FixtureObservation>();
            switch (variant)
            {
                case "Spread":
                    observations.Add(new FixtureObservation(1, TickStage.Publish, "A余量4，B未燃。"));
                    observations.Add(new FixtureObservation(2, TickStage.Publish, "A余量3；B刚点燃且余量5。"));
                    observations.Add(new FixtureObservation(3, TickStage.Publish, "B余量4。"));
                    observations.Add(new FixtureObservation(5, TickStage.Publish, "A燃尽；质量/固定/碰撞/火焰同步撤销。"));
                    break;
                case "Tank": observations.Add(new FixtureObservation(1, TickStage.Publish, "A熄灭且燃料5；水留(10,9)且不消耗。")); break;
                case "PreFlow": observations.Add(new FixtureObservation(1, TickStage.Publish, "水落(10,8)，A仍熄灭且余量5，验证流动前湿集合。")); break;
                case "Partial":
                    observations.Add(new FixtureObservation(2, TickStage.Publish, "A已燃烧两步余量3。"));
                    observations.Add(new FixtureObservation(3, TickStage.Publish, "水流落(10,8)；A熄灭且余量仍3。"));
                    observations.Add(new FixtureObservation(4, TickStage.Publish, "先Remove水再两次Ignite，A余量仍3。"));
                    observations.Add(new FixtureObservation(5, TickStage.Publish, "A燃料2。"));
                    break;
                case "RemoveThenIgnite":
                    observations.Add(new FixtureObservation(1, TickStage.Publish, "先Remove水再Ignite，命令新点燃余量5。"));
                    observations.Add(new FixtureObservation(2, TickStage.Publish, "A正常扣至4。"));
                    break;
                default: throw new ArgumentException("未知燃烧夹具。", nameof(variant));
            }
            if (variant == "Spread") { cells.Add(new InitialCell(11, 10, 104)); fixedCells.Add(new Vector2Int(11, 10)); }
            if (variant == "Tank" || variant == "PreFlow") cells.Add(new InitialCell(10, 9, 101));
            if (variant == "Tank") foreach (Vector2Int p in new[] { new Vector2Int(9, 8), new Vector2Int(10, 8), new Vector2Int(11, 8), new Vector2Int(9, 9), new Vector2Int(11, 9) })
            {
                cells.Add(new InitialCell(p.x, p.y, 102)); fixedCells.Add(p);
            }
            if (variant == "Partial")
            {
                commands.Add(Command(3, MaterialOperation.Spawn, 10, 9, 101));
                commands.Add(Command(4, MaterialOperation.Remove, 10, 8));
                commands.Add(Command(4, MaterialOperation.Ignite, 10, 10));
                commands.Add(Command(4, MaterialOperation.Ignite, 10, 10));
            }
            if (variant == "RemoveThenIgnite")
            {
                cells.Add(new InitialCell(10, 9, 101));
                commands.Add(Command(1, MaterialOperation.Remove, 10, 9)); commands.Add(Command(1, MaterialOperation.Ignite, 10, 10));
            }
            return new ScenarioFixture("T9-" + variant, Config(), Scene(cells, fixedCells,
                variant == "RemoveThenIgnite" ? Array.Empty<Vector2Int>() : new[] { new Vector2Int(10, 10) }), commands, Array.Empty<BodyProbeState>(), observations);
        }
        public static ScenarioFixture RotatedContact(bool vertexOnly = false, bool duringPhysics = false)
        {
            // 45度单格的最低顶点为局部原点；水格上边y=1。x=1.05触边，x=1.1仅触顶点。
            var pose = new BodyPose(new Vector2(vertexOnly ? 1.1f : 1.05f, duringPhysics ? 1.01f : 1), (float)(Math.PI / 4));
            var motion = new BodyMotion(duringPhysics ? new Vector2(0, -0.5f) : Vector2.zero, 0);
            var cells = new List<InitialCell> { new InitialCell(20, 20, 104), new InitialCell(10, 9, 101) };
            var fixedCells = new List<Vector2Int>();
            foreach (Vector2Int p in new[] { new Vector2Int(9, 8), new Vector2Int(10, 8), new Vector2Int(11, 8), new Vector2Int(9, 9), new Vector2Int(11, 9) })
            {
                cells.Add(new InitialCell(p.x, p.y, 102)); fixedCells.Add(p);
            }
            return Make(vertexOnly ? "T9-VertexOnly" : duringPhysics ? "T9-PhysicsContact" : "T9-VertexEdge", Config(gravityY: 0),
                Scene(cells, fixedCells, new[] { new Vector2Int(20, 20) }),
                vertexOnly ? "仅顶点接触不灭火/传播。" : duringPhysics ? "物理新接触当Tick灭火，不回退此前燃料消耗。" : "边-顶点接触熄灭，燃料保持5。",
                bodies: new[] { new BodyProbeState(new Vector2Int(20, 20), pose, motion) });
        }
        public static ushort[] P1()
        {
            var ids = new ushort[256 * 256];
            for (int index = 0; index < ids.Length; index++) ids[index] = (ushort)(101 + index / 16384);
            return ids;
        }
        private static void Ground(List<InitialCell> cells, List<Vector2Int> fixedCells)
        {
            for (int y = 0; y < 4; y++) for (int x = 0; x < 256; x++)
            {
                cells.Add(new InitialCell(x, y, 102)); fixedCells.Add(new Vector2Int(x, y));
            }
        }
        public static ScenarioFixture P2()
        {
            var cells = new List<InitialCell>(); var fixedCells = new List<Vector2Int>(); var burning = new List<Vector2Int>();
            Ground(cells, fixedCells);
            for (int y = 4; y <= 67; y++) for (int x = 0; x <= 127; x++) cells.Add(new InitialCell(x, y, 101));
            for (int y = 4; y <= 35; y++) for (int x = 128; x <= 191; x++) cells.Add(new InitialCell(x, y, 103));
            for (int x = 0; x < 256; x++)
            {
                cells.Add(new InitialCell(x, 68, 104)); fixedCells.Add(new Vector2Int(x, 68));
                if ((x & 1) == 0) burning.Add(new Vector2Int(x, 68));
            }
            return Make("P2", Config(), Scene(cells, fixedCells, burning), "1024固定混凝土+8192水+2048蒸汽+256固定木头/128初燃；活动与全窗口分别采样。", resetEveryTicks: 200);
        }
        public static ScenarioFixture P3()
        {
            var cells = new List<InitialCell>(); var fixedCells = new List<Vector2Int>(); var bodies = new List<BodyProbeState>();
            Ground(cells, fixedCells);
            for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++)
            {
                int x = 8 + 28 * i, y = 32 + 24 * j;
                for (int dy = 0; dy < 4; dy++) for (int dx = 0; dx < 4; dx++) cells.Add(new InitialCell(x + dx, y + dy, 104));
                bodies.Add(new BodyProbeState(new Vector2Int(x, y), new BodyPose(new Vector2(x * 0.1f, y * 0.1f), 0),
                    new BodyMotion(new Vector2((i & 1) == 0 ? 0.25f : -0.25f, 0), (float)(((j & 1) == 0 ? 30 : -30) * Math.PI / 180))));
            }
            return Make("P3", Config(gravityY: 0), Scene(cells, fixedCells), "初始化自动提取64个4×4木体；速度仅通过测试适配器设置，每200Tick重设。", bodies: bodies, resetEveryTicks: 200);
        }
        public static ScenarioFixture P4()
        {
            var cells = new List<InitialCell>(); var commands = new List<TimedCommand>();
            for (int x = 16; x <= 143; x++)
            {
                cells.Add(new InitialCell(x, 100, 104));
                if ((x & 1) != 0) commands.Add(Command(1, MaterialOperation.Remove, x, 100));
            }
            return Make("P4", Config(), Scene(cells, new[] { new Vector2Int(17, 100) }), "按x升序64条独立Remove；留下64个一格分量。重建200次，只计破坏Step。", commands);
        }
    }
}
