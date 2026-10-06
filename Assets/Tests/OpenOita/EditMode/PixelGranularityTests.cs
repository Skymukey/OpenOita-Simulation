using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Rules;
using OpenOita.Simulation;
using OpenOita.Structure;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode
{
    // 验证真实状态/规则/结构/归属服务，不代表正式IWorld、显示或Unity物理验收。
    public sealed class PixelGranularityTests
    {
        private WorkingWorld _world;
        private WorldLoadResult _loaded;
        private readonly ConnectivityAnalyzer _structure = new ConnectivityAnalyzer(65536, 64);

        private static CellKey Grid(int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, x, y));
        private static CellKey Body(ulong id, int x, int y) => new CellKey(1, new CellPositionKey(OwnerKind.Body, id, x, y));
        private TransactionContext Context(TickStage stage = TickStage.Commands) =>
            new TransactionContext(_world.Published.Version, _world.WorkingTick, stage);

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
        }

        private void Create(PixelGranularityFixture fixture, bool prepareStructure)
        {
            WorldLoadResult baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Require(baseline.Result);
            _loaded = new WorldLoadResult(WorldResult.Success(), fixture.Config, fixture.Scene, baseline.Materials, baseline.Rules);
            Require(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world));
            Require(_world.BeginTick());
            var writes = fixture.AuthoredStates.Select(p => new CellWrite(Grid(p.Position.x, p.Position.y), p.State)).ToArray();
            IPreparedMutation edit = Prepare(writes, Context());
            if (prepareStructure) CompleteStructure(edit, Context());
            Commit(edit, Context());
        }

        private IPreparedMutation Prepare(CellWrite[] writes, TransactionContext context, MutationIntent[] moves = null)
        {
            var result = _world.Prepare(writes, context, moves: moves ?? Array.Empty<MutationIntent>());
            Require(result.Result);
            return result.Prepared;
        }

        private IPreparedMaterialMutation CompleteStructure(IPreparedMutation edit, TransactionContext context)
        {
            var structure = _world.PlanStructure(edit, _structure, context);
            Require(structure.Result);
            var material = _world.PrepareMaterial(edit, structure.Plan, new MaterialMutationPreparer(_loaded.Materials), context);
            Require(material.Result);
            Assert.That(material.Prepared, Is.SameAs(edit));
            return material.Prepared;
        }

        private void Commit(IPreparedMutation edit, TransactionContext context, Action afterApply = null)
        {
            using var coordinator = new TransactionCoordinator();
            coordinator.Own(edit);
            Require(coordinator.ValidateAndApply(context, _world.Config.Limits, ContractDefaults.CpuBudgetBytes));
            afterApply?.Invoke();
            Require(_world.PublishState());
            coordinator.MarkCommitted(_world.Published.Version);
        }

        private static void Require(WorldResult result)
        {
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Target + ": " + result.Diagnostic.Message);
        }

        private static void StateEquals(CellSnapshot expected, CellSnapshot actual, CellKey key)
        {
            Assert.That(actual.MaterialId, Is.EqualTo(expected.MaterialId), key + " 材料");
            Assert.That(actual.Flags, Is.EqualTo(expected.Flags), key + " 标记");
            Assert.That(actual.FuelTicksRemaining, Is.EqualTo(expected.FuelTicksRemaining), key + " 燃料");
            Assert.That(actual.SpreadCountdown, Is.EqualTo(expected.SpreadCountdown), key + " 传播倒计时");
            Assert.That(actual.LifetimeTicksRemaining, Is.EqualTo(expected.LifetimeTicksRemaining), key + " 寿命");
            Assert.That(actual.MoveCountdown, Is.EqualTo(expected.MoveCountdown), key + " 移动倒计时");
            Assert.That(actual.IgnitedTick, Is.EqualTo(expected.IgnitedTick), key + " 点燃Tick");
        }

        private CellSnapshot Read(CellKey key)
        {
            Require(_world.Published.Read(key, out CellSnapshot state));
            return state;
        }

        [TestCase(10)]
        [TestCase(124)]
        public void M00_07_M02_02_RemovingOneOf64PixelsPreservesEveryOtherCompleteState(int minimum)
        {
            PixelGranularityFixture fixture = PixelGranularityFixture.IndependentMaterials8x8(minimum);
            Assert.That(fixture.LogicalSize, Is.EqualTo(new Vector2Int(8, 8)));
            Assert.That(fixture.AuthoredStates.Count, Is.EqualTo(64));
            foreach (ushort id in new ushort[] { 101, 102, 103, 104 })
                Assert.That(fixture.AuthoredStates.Count(p => p.State.MaterialId == id), Is.EqualTo(16));
            Create(fixture, false);
            foreach (PixelState pixel in fixture.AuthoredStates)
                StateEquals(pixel.State, Read(Grid(pixel.Position.x, pixel.Position.y)), Grid(pixel.Position.x, pixel.Position.y));
            Assert.That(_world.ChunkCount, Is.EqualTo(minimum == 124 ? 4 : 1));
            Require(_world.BeginTick());
            var instances = new Dictionary<CellKey, CellInstanceHandle>();
            foreach (PixelState pixel in fixture.AuthoredStates)
            {
                CellKey key = Grid(pixel.Position.x, pixel.Position.y);
                Assert.That(_world.Instances.TryGetInstance(key, out CellInstanceHandle instance), Is.True);
                instances.Add(key, instance);
            }
            CellKey removed = Grid(minimum + 3, minimum + 3);
            var edit = Prepare(new[] { new CellWrite(removed, default) }, Context());
            Assert.That(edit.Budget.MaterialCells, Is.EqualTo(63));
            Assert.That(edit.CandidateWrites.ToArray(), Is.EqualTo(new[] { removed.Position }));
            StateEquals(fixture.AuthoredStates[27].State, Read(removed), removed);
            Commit(edit, Context(), () =>
            {
                foreach (var item in instances)
                {
                    bool exists = _world.Instances.TryResolve(item.Value, out CellKey current);
                    Assert.That(exists, Is.EqualTo(!item.Key.Equals(removed)));
                    if (exists) Assert.That(current, Is.EqualTo(item.Key));
                }
            });
            Assert.That(_world.MaterialCells, Is.EqualTo(63));
            Assert.That(_world.ChangedPositions, Is.EqualTo(1));
            Assert.That(_world.Published.OccupiedCells.Length, Is.EqualTo(63));
            foreach (PixelState pixel in fixture.AuthoredStates)
            {
                CellKey key = Grid(pixel.Position.x, pixel.Position.y);
                StateEquals(key.Equals(removed) ? default : pixel.State, Read(key), key);
            }
        }

        [TestCase(10)]
        [TestCase(127)]
        public void M00_07_M03_T9_01_AdjacentFuelIsIndependentWhenOnePixelBurnsOut(int x)
        {
            Create(PixelGranularityFixture.AdjacentFuel(x), true);
            Require(_world.BeginTick());
            CellKey exhausted = Grid(x, 10), survivor = Grid(x + 1, 10);
            Assert.That(_world.Instances.TryGetInstance(survivor, out CellInstanceHandle survivorInstance), Is.True);
            Assert.That(_world.Instances.TryGetInstance(exhausted, out CellInstanceHandle exhaustedInstance), Is.True);
            TransactionContext context = Context(TickStage.Burning);
            IRuleBatch batch = new BurnRule(64).Execute(_world, _loaded.Materials, _world.Instances, null, context);
            Require(batch.Result);
            MutationIntent[] intents = batch.Intents.ToArray();
            Assert.That(intents.Length, Is.EqualTo(2));
            Assert.That(intents.Single(i => i.Source.Equals(exhausted)).Kind, Is.EqualTo(MutationKind.Remove));
            Assert.That(intents.Single(i => i.Source.Equals(exhausted)).RemovalReason, Is.EqualTo(RemovalReason.BurnedOut));
            var expected = new CellSnapshot(104, 1, 8, 6);
            StateEquals(expected, intents.Single(i => i.Source.Equals(survivor)).State, survivor);
            var edit = Prepare(intents.Select(i => new CellWrite(i.Source, i.State)).ToArray(), context);
            var material = CompleteStructure(edit, context);
            CellKey target = Body(1, 0, 0);
            Assert.That(material.Cells.Length, Is.EqualTo(1));
            Assert.That(material.Cells[0].Source, Is.EqualTo(survivor));
            Assert.That(material.Cells[0].Target, Is.EqualTo(target));
            Assert.That(material.Cells[0].Instance, Is.EqualTo(survivorInstance));
            Commit(edit, context, () =>
            {
                Assert.That(_world.Instances.TryResolve(survivorInstance, out CellKey moved), Is.True);
                Assert.That(moved, Is.EqualTo(target));
                Assert.That(_world.Instances.TryResolve(exhaustedInstance, out _), Is.False);
            });
            StateEquals(default, Read(exhausted), exhausted);
            StateEquals(default, Read(survivor), survivor);
            StateEquals(expected, Read(target), target);
            Assert.That(_world.MaterialCells, Is.EqualTo(1));
            Assert.That(_world.Bodies.Length, Is.EqualTo(1));
            Assert.That(_world.IsFixed(exhausted), Is.False);
        }

        [TestCase(10)]
        [TestCase(127)]
        public void M00_07_M03_T6_AdjacentWaterActsIndependentlyAndConservesInstances(int x)
        {
            PixelGranularityFixture fixture = PixelGranularityFixture.AdjacentWater(x);
            Create(fixture, true);
            Require(_world.BeginTick());
            CellKey falling = Grid(x, 5), blocked = Grid(x + 1, 5), destination = Grid(x, 4);
            Assert.That(_world.Instances.TryGetInstance(falling, out CellInstanceHandle fallingInstance), Is.True);
            Assert.That(_world.Instances.TryGetInstance(blocked, out CellInstanceHandle blockedInstance), Is.True);
            TransactionContext context = Context(TickStage.Water);
            IRuleBatch batch = new LiquidFlowRule(64).Execute(_world, _loaded.Materials, _world.Instances, null, context);
            Require(batch.Result);
            IRuleBatch resolved = new MovementCandidateResolver(64).Resolve(batch);
            Require(resolved.Result);
            MutationIntent[] intents = resolved.Intents.ToArray();
            Assert.That(intents.Length, Is.EqualTo(2));
            MutationIntent move = intents.Single(i => i.Source.Equals(falling));
            MutationIntent wait = intents.Single(i => i.Source.Equals(blocked));
            Assert.That(move.Kind, Is.EqualTo(MutationKind.Move));
            Assert.That(move.Target, Is.EqualTo(destination));
            Assert.That(move.Instance, Is.EqualTo(fallingInstance));
            Assert.That(wait.Kind, Is.EqualTo(MutationKind.WriteState));
            Assert.That(wait.Target, Is.EqualTo(blocked));
            Assert.That(wait.Instance, Is.EqualTo(blockedInstance));
            var expected = new CellSnapshot(101, moveCountdown: 2);
            StateEquals(expected, move.State, destination);
            StateEquals(expected, wait.State, blocked);
            var writes = new[] { new CellWrite(falling, default), new CellWrite(destination, move.State), new CellWrite(blocked, wait.State) };
            var edit = Prepare(writes, context, new[] { move });
            CompleteStructure(edit, context);
            Commit(edit, context, () =>
            {
                Assert.That(_world.Instances.TryResolve(fallingInstance, out CellKey moved), Is.True);
                Assert.That(moved, Is.EqualTo(destination));
                Assert.That(_world.Instances.TryResolve(blockedInstance, out CellKey unchanged), Is.True);
                Assert.That(unchanged, Is.EqualTo(blocked));
            });
            Assert.That(_world.MaterialCells, Is.EqualTo(4));
            Assert.That(_world.Published.OccupiedCells.ToArray().Count(k => Read(k).MaterialId == 101), Is.EqualTo(2));
            StateEquals(default, Read(falling), falling);
            StateEquals(expected, Read(destination), destination);
            StateEquals(expected, Read(blocked), blocked);
            foreach (PixelState obstacle in fixture.AuthoredStates.Where(p => p.State.MaterialId == 102))
                StateEquals(obstacle.State, Read(Grid(obstacle.Position.x, obstacle.Position.y)), Grid(obstacle.Position.x, obstacle.Position.y));
            Assert.That(_world.ChangedPositions, Is.EqualTo(3));
        }

        [TestCase(10, false)]
        [TestCase(124, false)]
        [TestCase(10, true)]
        [TestCase(124, true)]
        public void M04_M05_MergedBodyPreservesPixelStatesAndSinglePixelHoleAfterRebuild(int minimum, bool initialHole)
        {
            PixelGranularityFixture fixture = PixelGranularityFixture.MixedSolid8x8(minimum, initialHole);
            WorldLoadResult baseline = new WorldSourceLoader().Load(BaselineSources.Read());
            Require(baseline.Result);
            _loaded = new WorldLoadResult(WorldResult.Success(), fixture.Config, fixture.Scene, baseline.Materials, baseline.Rules);
            Require(WorkingWorld.CreateInitial(_loaded, Vector2.zero, 1, out _world));
            Require(_world.BeginTick());
            var seed = Prepare(fixture.AuthoredStates.Select(p => new CellWrite(Grid(p.Position.x, p.Position.y), p.State)).ToArray(), Context());
            var material = CompleteStructure(seed, Context());
            Assert.That(material.Geometry.Count, Is.EqualTo(1));
            Assert.That(material.Geometry[0].Cells.Count, Is.EqualTo(initialHole ? 63 : 64));
            if (!initialHole) Assert.That(material.Geometry[0].Rectangles.Count, Is.EqualTo(1));
            var extractionInstances = new Dictionary<CellKey, CellInstanceHandle>();
            foreach (PixelState pixel in fixture.AuthoredStates)
            {
                CellKey source = Grid(pixel.Position.x, pixel.Position.y);
                CellKey target = Body(1, pixel.Position.x - minimum, pixel.Position.y - minimum);
                Assert.That(_world.Instances.TryGetInstance(source, out CellInstanceHandle instance), Is.True);
                PlannedCell transfer = material.Cells.ToArray().Single(c => c.Source.Equals(source));
                Assert.That(transfer.Target, Is.EqualTo(target));
                Assert.That(transfer.Instance, Is.EqualTo(instance));
                StateEquals(pixel.State, transfer.State, target);
                extractionInstances.Add(target, instance);
            }
            Assert.That(_world.Bodies.Length, Is.Zero);
            var holes = new HashSet<Vector2Int>();
            if (initialHole) holes.Add(new Vector2Int(3, 3));
            AssertCoverage(material.Geometry[0], holes);
            Commit(seed, Context(), () =>
            {
                foreach (var item in extractionInstances)
                {
                    Assert.That(_world.Instances.TryResolve(item.Value, out CellKey target), Is.True);
                    Assert.That(target, Is.EqualTo(item.Key));
                }
            });
            AssertBodyStates(fixture, holes);
            Assert.That(_world.Bodies.Length, Is.EqualTo(1));
            Assert.That(_world.Bodies[0].GeometryVersion, Is.EqualTo(1));

            // 初态无孔时删除中心；已有孔时删除孔旁一格。其余分量仍连通，身份应保留。
            Vector2Int removedPosition = initialHole ? new Vector2Int(4, 3) : new Vector2Int(3, 3);
            CellKey removed = Body(1, removedPosition.x, removedPosition.y);
            Require(_world.BeginTick());
            var survivingInstances = new Dictionary<CellKey, CellInstanceHandle>();
            foreach (CellKey key in _world.OccupiedCells)
            {
                Assert.That(_world.Instances.TryGetInstance(key, out CellInstanceHandle instance), Is.True);
                survivingInstances.Add(key, instance);
            }
            var edit = Prepare(new[] { new CellWrite(removed, default) }, Context());
            material = CompleteStructure(edit, Context());
            holes.Add(removedPosition);
            Assert.That(material.Geometry.Count, Is.EqualTo(1));
            Assert.That(material.Geometry[0].Owner.BodyId, Is.EqualTo(1));
            Assert.That(material.Geometry[0].Rectangles.Count, Is.GreaterThan(1));
            AssertCoverage(material.Geometry[0], holes);
            Assert.That(material.Budget.ChangedPositions, Is.EqualTo(1));
            Commit(edit, Context(), () =>
            {
                foreach (var item in survivingInstances)
                {
                    bool exists = _world.Instances.TryResolve(item.Value, out CellKey key);
                    Assert.That(exists, Is.EqualTo(!item.Key.Equals(removed)));
                    if (exists) Assert.That(key, Is.EqualTo(item.Key));
                }
            });
            AssertBodyStates(fixture, holes);
            Assert.That(_world.MaterialCells, Is.EqualTo(64 - holes.Count));
            Assert.That(_world.ChangedPositions, Is.EqualTo(1));
            Assert.That(_world.Bodies.Length, Is.EqualTo(1));
            Assert.That(_world.Bodies[0].BodyId, Is.EqualTo(1));
            Assert.That(_world.Bodies[0].GeometryVersion, Is.EqualTo(2));
            StateEquals(default, Read(Body(1, 3, 3)), Body(1, 3, 3));
        }

        private void AssertBodyStates(PixelGranularityFixture fixture, HashSet<Vector2Int> holes)
        {
            foreach (PixelState pixel in fixture.AuthoredStates)
            {
                Vector2Int local = pixel.Position - fixture.LogicalMinimum;
                CellKey key = Body(1, local.x, local.y);
                StateEquals(holes.Contains(local) ? default : pixel.State, Read(key), key);
                StateEquals(default, Read(Grid(pixel.Position.x, pixel.Position.y)), Grid(pixel.Position.x, pixel.Position.y));
            }
            foreach (Vector2Int hole in holes)
                StateEquals(default, Read(Body(1, hole.x, hole.y)), Body(1, hole.x, hole.y));
            Assert.That(_world.Published.OccupiedCells.Length, Is.EqualTo(64 - holes.Count));
        }

        private static void AssertCoverage(BodyGeometryPlan geometry, HashSet<Vector2Int> holes)
        {
            // 独立逐坐标覆盖表；不照抄矩形生成算法，也不从生产Cells反推预期。
            var counts = new int[8, 8];
            foreach (CellRectangle rectangle in geometry.Rectangles)
            {
                Assert.That(rectangle.Min.x, Is.InRange(0, 7));
                Assert.That(rectangle.Min.y, Is.InRange(0, 7));
                Assert.That(rectangle.MaxExclusive.x, Is.InRange(rectangle.Min.x + 1, 8));
                Assert.That(rectangle.MaxExclusive.y, Is.InRange(rectangle.Min.y + 1, 8));
                for (int y = rectangle.Min.y; y < rectangle.MaxExclusive.y; y++)
                    for (int x = rectangle.Min.x; x < rectangle.MaxExclusive.x; x++) counts[x, y]++;
            }
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    Assert.That(counts[x, y], Is.EqualTo(holes.Contains(new Vector2Int(x, y)) ? 0 : 1), $"矩形覆盖({x},{y})");
        }
    }
}
