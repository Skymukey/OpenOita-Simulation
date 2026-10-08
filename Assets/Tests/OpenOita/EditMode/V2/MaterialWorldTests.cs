using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;
using OpenOita.V2;
using UnityEngine;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class MaterialWorldTests
    {
        [Test]
        public void ReinitializationAndWorkerDisposalDoNotReplaceOrReleaseLiveState()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) => SetScene(scene, new Cell(4, 4, 101)));
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                MaterialReadLease lease = world.AcquireReadLease();
                Assert.That(world.Initialize().ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                WorldResult workerResult = default;
                var worker = new System.Threading.Thread(() => workerResult = world.Dispose());
                worker.Start();
                Assert.That(worker.Join(5000), Is.True);
                Assert.That(workerResult.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                Assert.That(lease.Read(4, 4).MaterialId, Is.EqualTo((ushort)101));
                Assert.That(SuccessfulStep(world), Is.True);
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void PublishAllowsReadButRejectsReentrantMutation()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) => SetScene(scene, new Cell(4, 4, 101)));
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                int published = 0;
                world.Published += info =>
                {
                    published++;
                    Assert.That(world.AcquireReadLease().Version, Is.EqualTo(info.Version));
                    Assert.That(world.Step().Result.ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                    Assert.That(world.Reset().ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                    Assert.That(world.Dispose().ErrorCode, Is.EqualTo(WorldErrorCode.Busy));
                };
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(world.Version.CommittedTick, Is.EqualTo(1UL));
                Assert.That(published, Is.EqualTo(1));
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void GasUsesAbsoluteExpiryAndResetRebuildsTickZeroState()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) =>
            {
                SetGasLifetime(materials, 2);
                SetScene(scene, new Cell(4, 4, 103),
                    new Cell(3, 3, 102), new Cell(4, 3, 102), new Cell(5, 3, 102),
                    new Cell(3, 4, 102), new Cell(5, 4, 102),
                    new Cell(3, 5, 102), new Cell(4, 5, 102), new Cell(5, 5, 102));
                SetFixed(scene, new Cell(3, 3), new Cell(4, 3), new Cell(5, 3), new Cell(3, 4),
                    new Cell(5, 4), new Cell(3, 5), new Cell(4, 5), new Cell(5, 5));
            });
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                Assert.That(world.Grid.Read(4, 4).Cold.ExpiryTick, Is.EqualTo(2UL));
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(world.Grid.Read(4, 4).MaterialId, Is.EqualTo((ushort)103));
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(world.Grid.Read(4, 4).IsEmpty, Is.True);

                ulong generation = world.Version.Generation;
                Assert.That(world.Reset().IsSuccess, Is.True);
                Assert.That(world.Version.Generation, Is.EqualTo(generation + 1));
                Assert.That(world.Version.CommittedTick, Is.Zero);
                Assert.That(world.Grid.Read(4, 4).MaterialId, Is.EqualTo((ushort)103));
                Assert.That(world.Grid.Read(4, 4).Cold.ExpiryTick, Is.EqualTo(2UL));
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void BurningDelayRepeatIgniteAndWetExtinguishPreserveFuel()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) =>
            {
                SetBurningParameters(materials, 5, 50);
                SetScene(scene, new Cell(10, 10, 104), new Cell(10, 9, 101),
                    new Cell(9, 9, 102), new Cell(11, 9, 102), new Cell(10, 8, 102));
                SetFixed(scene, new Cell(9, 9), new Cell(11, 9), new Cell(10, 8));
                SetBurning(scene, new Cell(10, 10));
            });
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                Assert.That(SuccessfulStep(world), Is.True);
                GridCell extinguished = world.Grid.Read(10, 10);
                Assert.That(extinguished.IsBurning, Is.False);
                Assert.That(extinguished.Cold.FuelRemaining, Is.EqualTo(5u));

                Enqueue(world, MaterialOperation.Remove, 10, 9);
                Enqueue(world, MaterialOperation.Ignite, 10, 10);
                Assert.That(SuccessfulStep(world), Is.True);
                GridCell delayed = world.Grid.Read(10, 10);
                Assert.That(delayed.IsBurning, Is.True);
                Assert.That(delayed.Cold.FuelRemaining, Is.EqualTo(5u));

                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(Fuel(world, 10, 10), Is.EqualTo(4u));

                Enqueue(world, MaterialOperation.Ignite, 10, 10);
                Enqueue(world, MaterialOperation.Ignite, 10, 10);
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(Fuel(world, 10, 10), Is.EqualTo(3u));
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void WetCandidatesAcrossFourTilesExtinguishOnceWithoutLosingFuel()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) =>
            {
                config["width"] = 64;
                config["height"] = 64;
                SetBurningParameters(materials, 5, 50);
                SetScene(scene, new Cell(31, 31, 104), new Cell(30, 31, 101),
                    new Cell(32, 31, 101), new Cell(31, 30, 101), new Cell(31, 32, 101));
                SetBurning(scene, new Cell(31, 31));
            });
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                Assert.That(SuccessfulStep(world), Is.True);
                GridCell wood = world.Grid.Read(31, 31);
                Assert.That(wood.IsBurning, Is.False);
                Assert.That(wood.Cold.FuelRemaining, Is.EqualTo(5u));
                Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(5));
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(world.Grid.Read(31, 31).Cold.FuelRemaining, Is.EqualTo(5u));
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void BurningSpreadsAtTenTicksAndNewFireDoesNotSpreadRecursively()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) =>
            {
                SetBurningParameters(materials, 30, 10);
                SetScene(scene, new Cell(4, 4, 104), new Cell(5, 4, 104), new Cell(6, 4, 104));
                SetBurning(scene, new Cell(4, 4));
            });
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                for (int tick = 1; tick < 10; tick++)
                    Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(IsBurningAt(world, 5, 4), Is.False);
                Assert.That(IsBurningAt(world, 6, 4), Is.False);

                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(IsBurningAt(world, 5, 4), Is.True);
                Assert.That(IsBurningAt(world, 6, 4), Is.False,
                    "同一传播批次中新点燃的木头不能递归点燃下一格。");

                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(IsBurningAt(world, 6, 4), Is.False,
                    "新火的IgnitedTick应阻止下一Tick递归传播。");
                for (int tick = 12; tick < 20; tick++)
                    Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(IsBurningAt(world, 6, 4), Is.True);
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void BurnoutDeletesBeforeSameBatchIgnitionAndCannotBeRevived()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) =>
            {
                SetBurningParameters(materials, 3, 1);
                AddShortBurnableMaterial(materials, 105, 1, 1);
                SetScene(scene, new Cell(4, 4, 104), new Cell(5, 4, 105));
                SetBurning(scene, new Cell(4, 4), new Cell(5, 4));
            });
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(IsBurningAt(world, 4, 4), Is.True);
                Assert.That(PointAt(world, 5, 4).HasHit, Is.False,
                    "燃尽删除后的目标不能被同一Tick遗留的点火请求复活。");
                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(PointAt(world, 5, 4).HasHit, Is.False);
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void SteamMoveKeepsAbsoluteExpiryAndOldTimerCannotDeleteTwice()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) =>
            {
                SetGasLifetime(materials, 3);
                SetScene(scene, new Cell(4, 4, 103));
            });
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                PointQueryResult initial = FindMaterial(world, 103);
                Assert.That(initial.HasHit, Is.True);
                Assert.That(initial.Hit.State.LifetimeTicksRemaining, Is.EqualTo(3u));

                Assert.That(SuccessfulStep(world), Is.True);
                PointQueryResult first = FindMaterial(world, 103);
                Assert.That(first.HasHit, Is.True);
                Assert.That(first.Hit.State.LifetimeTicksRemaining, Is.EqualTo(2u));
                Assert.That(first.Hit.Position.X == 4 && first.Hit.Position.Y == 4, Is.False,
                    "蒸汽应移动，但移动不能重置绝对到期Tick。");

                Assert.That(SuccessfulStep(world), Is.True);
                PointQueryResult second = FindMaterial(world, 103);
                Assert.That(second.HasHit, Is.True);
                Assert.That(second.Hit.State.LifetimeTicksRemaining, Is.EqualTo(1u));

                Assert.That(SuccessfulStep(world), Is.True);
                Assert.That(FindMaterial(world, 103).HasHit, Is.False);
                Assert.That(SuccessfulStep(world), Is.True,
                    "旧坐标事件不应在后续Tick再次删除或使世界进入Faulted。");
                Assert.That(FindMaterial(world, 103).HasHit, Is.False);
            }
            finally { world.Dispose(); }
        }

        [Test]
        public void ReadLeaseExpiresAfterStepAndRejectedCommandDoesNotModifyState()
        {
            MaterialWorld world = CreateWorld((materials, config, scene) => SetScene(scene, new Cell(2, 2, 101)));
            try
            {
                Assert.That(world.Initialize().IsSuccess, Is.True);
                MaterialReadLease lease = world.AcquireReadLease();
                Assert.That(lease.Read(2, 2).MaterialId, Is.EqualTo((ushort)101));

                MaterialCommand invalidMaterial = new MaterialCommand(MaterialOperation.Spawn,
                    CellRegion(4, 4), 999, world.Version.Generation);
                EnqueueResult rejected = world.Enqueue(invalidMaterial);
                Assert.That(rejected.Result.ErrorCode, Is.EqualTo(WorldErrorCode.UnknownMaterial));
                Assert.That(world.Grid.Read(2, 2).MaterialId, Is.EqualTo((ushort)101));

                Assert.That(SuccessfulStep(world), Is.True);
                Assert.Throws<InvalidOperationException>(() => lease.Read(2, 2));
            }
            finally { world.Dispose(); }
        }

        private static MaterialWorld CreateWorld(Action<JObject, JObject, JObject> edit)
        {
            WorldSources baseline = BaselineSources.Read();
            JObject materials = JObject.Parse(baseline.MaterialsText);
            JObject config = JObject.Parse(baseline.WorldConfigText);
            JObject scene = JObject.Parse(baseline.SceneText);
            materials["schemaVersion"] = 2;
            config["schemaVersion"] = 2;
            config.Remove("chunkSize");
            scene["schemaVersion"] = 2;
            edit?.Invoke(materials, config, scene);
            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(new WorldSources(
                materials.ToString(), config.ToString(), scene.ToString()));
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            return new MaterialWorld(loaded, Vector2.zero, false);
        }

        private static void SetGasLifetime(JObject materials, int lifetime)
        {
            foreach (JObject material in (JArray)materials["materials"])
                if ((int)material["id"] == 103)
                {
                    material["ruleParameters"]["gas_drift"]["lifetimeTicks"] = lifetime;
                    material["ruleParameters"]["gas_drift"]["moveIntervalTicks"] = 1;
                }
        }

        private static void SetBurningParameters(JObject materials, int fuel, int spread)
        {
            foreach (JObject material in (JArray)materials["materials"])
                if ((int)material["id"] == 104)
                {
                    material["ruleParameters"]["burnable"]["fuelTicks"] = fuel;
                    material["ruleParameters"]["burnable"]["spreadIntervalTicks"] = spread;
                }
        }

        private static void AddShortBurnableMaterial(JObject materials, ushort id, int fuel, int spread)
        {
            foreach (JObject source in (JArray)materials["materials"])
                if ((int)source["id"] == 104)
                {
                    JObject copy = (JObject)source.DeepClone();
                    copy["id"] = id;
                    copy["name"] = "短燃木" + id;
                    copy["ruleParameters"]["burnable"]["fuelTicks"] = fuel;
                    copy["ruleParameters"]["burnable"]["spreadIntervalTicks"] = spread;
                    ((JArray)materials["materials"]).Add(copy);
                    return;
                }
            Assert.Fail("缺少可燃材料104。");
        }

        private static void SetScene(JObject scene, params Cell[] cells)
        {
            var array = new JArray();
            Array.Sort(cells, (first, second) => first.Y == second.Y ? first.X.CompareTo(second.X) : first.Y.CompareTo(second.Y));
            foreach (Cell cell in cells) array.Add(new JObject { ["x"] = cell.X, ["y"] = cell.Y, ["materialId"] = cell.MaterialId });
            scene["cells"] = array;
            scene["fixedCells"] = new JArray();
            scene["initialBurning"] = new JArray();
        }

        private static void SetFixed(JObject scene, params Cell[] cells)
        {
            var array = new JArray();
            Array.Sort(cells, (first, second) => first.Y == second.Y ? first.X.CompareTo(second.X) : first.Y.CompareTo(second.Y));
            foreach (Cell cell in cells) array.Add(new JObject { ["x"] = cell.X, ["y"] = cell.Y });
            scene["fixedCells"] = array;
        }

        private static void SetBurning(JObject scene, params Cell[] cells)
        {
            var array = new JArray();
            Array.Sort(cells, (first, second) => first.Y == second.Y ? first.X.CompareTo(second.X) : first.Y.CompareTo(second.Y));
            foreach (Cell cell in cells) array.Add(new JObject { ["x"] = cell.X, ["y"] = cell.Y });
            scene["initialBurning"] = array;
        }

        private static void Enqueue(MaterialWorld world, MaterialOperation operation, int x, int y, ushort material = 0)
        {
            EnqueueResult result = world.Enqueue(new MaterialCommand(operation, CellRegion(x, y), material, world.Version.Generation));
            Assert.That(result.Result.IsSuccess || result.Result.Status == ResultStatus.Pending, Is.True, result.Result.Diagnostic.Message);
        }

        private static WorldRect CellRegion(int x, int y) => new WorldRect(new Vector2(x * .1f, y * .1f), new Vector2((x + 1) * .1f, (y + 1) * .1f));

        private static uint Fuel(MaterialWorld world, int x, int y)
        {
            PointQueryResult result = world.QueryPoint(new Vector2((x + .5f) * .1f, (y + .5f) * .1f));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            Assert.That(result.HasHit, Is.True);
            return result.Hit.State.FuelTicksRemaining;
        }

        private static bool IsBurningAt(MaterialWorld world, int x, int y)
        {
            PointQueryResult result = PointAt(world, x, y);
            return result.HasHit && result.Hit.State.IsBurning;
        }

        private static PointQueryResult PointAt(MaterialWorld world, int x, int y)
        {
            PointQueryResult result = world.QueryPoint(new Vector2((x + .5f) * .1f, (y + .5f) * .1f));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            return result;
        }

        private static bool SuccessfulStep(MaterialWorld world)
        {
            WorldResult result = world.Step().Result;
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            return result.IsSuccess;
        }

        private static PointQueryResult FindMaterial(MaterialWorld world, ushort materialId)
        {
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    PointQueryResult result = PointAt(world, x, y);
                    if (result.HasHit && result.Hit.MaterialId == materialId) return result;
                }
            return default;
        }

        private readonly struct Cell
        {
            public readonly int X, Y;
            public readonly ushort MaterialId;
            public Cell(int x, int y, ushort materialId = 0) { X = x; Y = y; MaterialId = materialId; }
        }
    }
}
