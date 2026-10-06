using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M01
{
    // 真实配置到状态核心的 C01 回归；不代替完整 Create、查询或显示集成验收。
    public sealed class PixelConfigurationTests
    {
        private static readonly Vector2 Origin = new Vector2(3, -4);

        [Test]
        public void C01_NonDefaultDimensionsAndCellSizeReachWorkingAndCommittedStateUnchanged()
        {
            WorldLoadResult loaded = Load(Sources(0.2f, new JArray(
                Cell(0, 0, 101), Cell(127, 32, 104), Cell(128, 32, 102), Cell(128, 64, 103))));
            using WorkingWorld world = Create(loaded);

            AssertConfig(loaded.Config, 0.2f);
            AssertConfig(world.Config, 0.2f);
            AssertConfig(world.Published.Config, 0.2f);
            Assert.That(world.Origin, Is.EqualTo(Origin));
            Assert.That(world.Published.Origin, Is.EqualTo(Origin));
            Assert.That(world.MaterialCells, Is.EqualTo(4));
            Assert.That(world.OccupiedCells.Length, Is.EqualTo(4));
            Assert.That(world.Published.OccupiedCells.Length, Is.EqualTo(4));
            Assert.That(Read(world, 128, 64).MaterialId, Is.EqualTo(103));
            Assert.That(Read(world.Published, 128, 64).MaterialId, Is.EqualTo(103));
            Assert.That(world.Read(Key(129, 64), out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(world.Read(Key(128, 65), out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(world.Published.Read(Key(129, 64), out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(world.Published.Read(Key(128, 65), out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
        }

        [Test]
        public void C01_OneSceneCellInitializesExactlyOneLogicalPixelWithoutEightByEightExpansion()
        {
            WorldLoadResult loaded = Load(Sources(0.2f, new JArray(Cell(7, 9, 104)),
                new JArray(Point(7, 9)), new JArray(Point(7, 9))));
            Assert.That(loaded.Scene.Cells.Count, Is.EqualTo(1));
            using WorkingWorld world = Create(loaded);
            Assert.That(world.Initial.Cells.Count, Is.EqualTo(1));
            Assert.That(world.MaterialCells, Is.EqualTo(1));
            Assert.That(world.OccupiedCells.Length, Is.EqualTo(1));
            Assert.That(world.Published.OccupiedCells.Length, Is.EqualTo(1));
            int chunksBeforeReads = world.ChunkCount;

            // 检查全部有效坐标，包含标记周围的 8×8 和远处空白，防止隐藏展开。
            for (int y = 0; y < 65; y++)
            {
                for (int x = 0; x < 129; x++)
                {
                    bool occupied = x == 7 && y == 9;
                    CellSnapshot state = Read(world, x, y);
                    Assert.That(state.MaterialId, Is.EqualTo(occupied ? 104 : 0), $"工作状态 ({x},{y})");
                    Assert.That(Read(world.Published, x, y), Is.EqualTo(state), $"提交状态 ({x},{y})");
                    Assert.That(world.IsFixed(Key(x, y)), Is.EqualTo(occupied), $"固定标记 ({x},{y})");
                    Assert.That(world.Instances.TryGetInstance(Key(x, y), out _), Is.EqualTo(occupied), $"实例 ({x},{y})");
                    if (!occupied)
                        Assert.That(state, Is.EqualTo(default(CellSnapshot)), $"空像素不得有规则状态 ({x},{y})");
                }
            }
            Assert.That(Read(world, 7, 9).IsBurning, Is.True);
            Assert.That(world.ChunkCount, Is.EqualTo(chunksBeforeReads), "读取空像素不能分配缺块。");
        }

        [Test]
        public void C01_AdjacentPixelsAcrossChunkBoundaryKeepIndependentMaterialFuelAndMarkers()
        {
            WorldLoadResult loaded = Load(Sources(0.2f, new JArray(Cell(127, 32, 104), Cell(128, 32, 104)),
                new JArray(Point(127, 32)), new JArray(Point(127, 32))));
            using WorkingWorld world = Create(loaded);
            CellSnapshot leftBefore = Read(world, 127, 32);
            CellSnapshot rightBefore = Read(world, 128, 32);
            Assert.That(leftBefore.IsBurning, Is.True);
            Assert.That(rightBefore.IsBurning, Is.False);
            Assert.That(world.IsFixed(Key(127, 32)), Is.True);
            Assert.That(world.IsFixed(Key(128, 32)), Is.False);
            Assert.That(world.Instances.TryGetInstance(Key(127, 32), out CellInstanceHandle leftInstance), Is.True);
            Assert.That(world.Instances.TryGetInstance(Key(128, 32), out CellInstanceHandle rightInstance), Is.True);
            Assert.That(leftInstance, Is.Not.EqualTo(rightInstance));
            Assert.That(world.BeginTick().IsSuccess, Is.True);
            var context = new TransactionContext(world.Published.Version, world.WorkingTick, TickStage.Commands);
            var burnedLeft = new CellSnapshot(104, leftBefore.Flags, 17, 3,
                leftBefore.LifetimeTicksRemaining, leftBefore.MoveCountdown, leftBefore.IgnitedTick);

            Apply(world.Prepare(new[] { new CellWrite(Key(127, 32), burnedLeft) }, context), context);
            Assert.That(Read(world, 127, 32), Is.EqualTo(burnedLeft));
            Assert.That(Read(world, 128, 32), Is.EqualTo(rightBefore), "修改左像素的燃料不能改变右像素。");
            Apply(world.PrepareReplace(Key(128, 32), 103, context), context);
            Assert.That(Read(world, 128, 32).MaterialId, Is.EqualTo(103));
            Assert.That(Read(world, 127, 32), Is.EqualTo(burnedLeft), "替换右像素不能改变左像素。");
            Assert.That(world.IsFixed(Key(127, 32)), Is.True);
            Apply(world.Prepare(new[] { new CellWrite(Key(128, 32), default) }, context), context);
            Assert.That(Read(world, 128, 32), Is.EqualTo(default(CellSnapshot)));
            Assert.That(Read(world, 127, 32), Is.EqualTo(burnedLeft));
            Assert.That(world.MaterialCells, Is.EqualTo(1));
            Assert.That(world.ChangedPositions, Is.EqualTo(2));

            Assert.That(Read(world.Published, 127, 32), Is.EqualTo(leftBefore));
            Assert.That(Read(world.Published, 128, 32), Is.EqualTo(rightBefore));
            Assert.That(world.PublishState().IsSuccess, Is.True);
            Assert.That(Read(world.Published, 127, 32), Is.EqualTo(burnedLeft));
            Assert.That(Read(world.Published, 128, 32), Is.EqualTo(default(CellSnapshot)));
        }

        [Test]
        public void C01_ChangingCellSizePreservesEveryLogicalPixelStateAndCount()
        {
            var cells = new JArray(Cell(0, 0, 101), Cell(127, 32, 104), Cell(128, 32, 104), Cell(128, 64, 103));
            var fixedCells = new JArray(Point(127, 32));
            var burning = new JArray(Point(128, 32));
            using WorkingWorld small = Create(Load(Sources(0.1f, cells, fixedCells, burning)));
            using WorkingWorld large = Create(Load(Sources(0.2f, cells, fixedCells, burning)));
            AssertConfig(small.Config, 0.1f);
            AssertConfig(large.Config, 0.2f);
            AssertConfig(small.Published.Config, 0.1f);
            AssertConfig(large.Published.Config, 0.2f);
            Assert.That(large.MaterialCells, Is.EqualTo(small.MaterialCells).And.EqualTo(4));
            Assert.That(large.OccupiedCells.ToArray(), Is.EqualTo(small.OccupiedCells.ToArray()));
            Assert.That(large.ChunkCount, Is.EqualTo(small.ChunkCount));

            for (int y = 0; y < 65; y++)
            {
                for (int x = 0; x < 129; x++)
                {
                    Assert.That(Read(large, x, y), Is.EqualTo(Read(small, x, y)), $"cellSize 不得改变状态 ({x},{y})");
                    Assert.That(large.IsFixed(Key(x, y)), Is.EqualTo(small.IsFixed(Key(x, y))));
                    Assert.That(Read(large.Published, x, y), Is.EqualTo(Read(small.Published, x, y)));
                }
            }
        }

        [Test]
        public void C01_SerializationRoundTripPreservesExactPixelDimensionsCellSizeAndLayout()
        {
            WorldLoadResult loaded = Load(Sources(0.2f, new JArray(
                Cell(0, 0, 101), Cell(127, 32, 104), Cell(128, 32, 102), Cell(128, 64, 103)),
                new JArray(Point(128, 32)), new JArray(Point(127, 32))));
            WorldResult serialized = ConfigurationSerializer.Serialize(loaded.Config, loaded.Scene, loaded.Materials, out WorldSources sources);
            Assert.That(serialized.IsSuccess, Is.True, serialized.Diagnostic.Message);
            JObject savedWorld = JObject.Parse(sources.WorldConfigText);
            Assert.That((int)savedWorld["width"], Is.EqualTo(129));
            Assert.That((int)savedWorld["height"], Is.EqualTo(65));
            Assert.That((float)savedWorld["cellSize"], Is.EqualTo(0.2f));
            WorldLoadResult restored = Load(sources);
            AssertConfig(restored.Config, 0.2f);
            Assert.That(restored.Scene.Cells, Is.EqualTo(loaded.Scene.Cells));
            Assert.That(restored.Scene.FixedCells, Is.EqualTo(loaded.Scene.FixedCells));
            Assert.That(restored.Scene.InitialBurning, Is.EqualTo(loaded.Scene.InitialBurning));
            using WorkingWorld before = Create(loaded);
            using WorkingWorld after = Create(restored);
            Assert.That(after.OccupiedCells.ToArray(), Is.EqualTo(before.OccupiedCells.ToArray()));
            foreach (CellKey key in before.OccupiedCells)
            {
                Assert.That(before.Read(key, out CellSnapshot expected).IsSuccess, Is.True);
                Assert.That(after.Read(key, out CellSnapshot actual).IsSuccess, Is.True);
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(after.IsFixed(key), Is.EqualTo(before.IsFixed(key)));
            }
        }

        private static WorldSources Sources(float cellSize, JArray cells, JArray fixedCells = null, JArray burning = null)
        {
            WorldSources baseline = BaselineSources.Read();
            JObject config = JObject.Parse(baseline.WorldConfigText);
            config["width"] = 129;
            config["height"] = 65;
            config["cellSize"] = cellSize;
            JObject scene = JObject.Parse(baseline.SceneText);
            scene["cells"] = cells.DeepClone();
            scene["fixedCells"] = fixedCells?.DeepClone() ?? new JArray();
            scene["initialBurning"] = burning?.DeepClone() ?? new JArray();
            return new WorldSources(baseline.MaterialsText, config.ToString(), scene.ToString());
        }

        private static JObject Cell(int x, int y, ushort materialId) => new JObject
        {
            ["x"] = x, ["y"] = y, ["materialId"] = materialId
        };

        private static JObject Point(int x, int y) => new JObject { ["x"] = x, ["y"] = y };

        private static CellKey Key(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));

        private static WorldLoadResult Load(WorldSources sources)
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(sources);
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            return loaded;
        }

        private static WorkingWorld Create(WorldLoadResult loaded)
        {
            WorldResult result = WorkingWorld.CreateInitial(loaded, Origin, 1, out WorkingWorld world);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            return world;
        }

        private static void AssertConfig(WorldConfig config, float cellSize)
        {
            Assert.That(config.Width, Is.EqualTo(129));
            Assert.That(config.Height, Is.EqualTo(65));
            Assert.That(config.ChunkSize, Is.EqualTo(128));
            Assert.That(config.CellSize, Is.EqualTo(cellSize));
        }

        private static CellSnapshot Read(IWorkingWorldView world, int x, int y)
        {
            WorldResult result = world.Read(Key(x, y), out CellSnapshot state);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            return state;
        }

        private static CellSnapshot Read(ICommittedWorldView world, int x, int y)
        {
            WorldResult result = world.Read(Key(x, y), out CellSnapshot state);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            return state;
        }

        private static void Apply(PreparationResult<IPreparedMutation> preparation, TransactionContext context)
        {
            Assert.That(preparation.Result.IsSuccess, Is.True, preparation.Result.Diagnostic.Message);
            using IPreparedMutation prepared = preparation.Prepared;
            Assert.That(prepared.Preflight(context).IsSuccess, Is.True);
            Assert.That(prepared.Apply(context).IsSuccess, Is.True);
        }
    }
}
