using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.PhysicsAdapter;
using OpenOita.Render;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode.Physics
{
    public sealed class FluidSuspensionAcceptanceTests
    {
        private readonly List<IWorld> _worlds = new();
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True,
            result.ErrorCode + " / " + result.Diagnostic.Stage + " / " + result.Diagnostic.Target + ": " + result.Diagnostic.Message);
        private SimulationWorld Create(WorldSources source, IWorldRenderer renderer = null, bool frozen = false)
        {
            var created = new WorldSimulation(rendererFactory: () => renderer ?? new M06Sources.CommitObserver(), freezeBodyRotation: frozen).Create(source, Vector2.zero);
            Success(created.Result); _worlds.Add(created.World); return (SimulationWorld)created.World;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            foreach (IWorld world in _worlds) Success(world.Dispose()); _worlds.Clear();
            yield return null; yield return null;
        }
        private static WorldRect All(IWorld world) => new WorldRect(Vector2.zero, new Vector2(world.Config.Width * world.Config.CellSize, world.Config.Height * world.Config.CellSize));
        private static int Count(IWorld world, ushort material)
        {
            MaterialCountsResult result = world.QueryMaterialCounts(); Success(result.Result);
            foreach (MaterialCount count in result.Counts) if (count.MaterialId == material) return count.TotalCells;
            return 0;
        }
        private static WorldSources BlockAndFluid(ushort fluid = 101, int maxCells = 65536, uint lifetime = 200)
        {
            var cells = new List<InitialCell> { new InitialCell(11, 11, fluid) };
            for (int y = 40; y <= 42; y++) for (int x = 40; x <= 42; x++) cells.Add(new InitialCell(x, y, 102));
            return M06Sources.Create(cells, radius: 1, maxMaterialCells: maxCells, steamLifetimeTicks: lifetime);
        }
        private static void PlaceCover(SimulationWorld world)
        {
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(1, 1), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
        }
        private static void MoveCoverAway(SimulationWorld world)
        {
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(4, 4), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
        }

        [Test] public void F01_01_02_06_08_RealCaptureWaitRestorePreservesInstanceAndChangeReasons()
        {
            SimulationWorld world = Create(BlockAndFluid()); PlaceCover(world);
            CellChange[] changes = null;
            world.Committed += change => changes = change.Changes.Cells.ToArray();
            Success(world.Step().Result);
            MaterialCountsResult hidden = world.QueryMaterialCounts();
            Assert.That(hidden.SuspendedWaterCells, Is.EqualTo(1)); Assert.That(hidden.ActiveCells, Is.EqualTo(9)); Assert.That(hidden.TotalCells, Is.EqualTo(10));
            Assert.That(Array.Exists(changes, c => c.Kind == CellChangeKind.Suspended && c.Before.MaterialId == 101 && c.AfterKey.Position.OwnerKind == OwnerKind.Suspended), Is.True);
            Assert.That(Array.Exists(changes, c => c.Kind == CellChangeKind.Removed && c.Before.MaterialId == 101), Is.False);
            var suspended = ((IFluidSuspensionView)world.Runtime.View).SuspendedFluids[0];
            Success(world.Step().Result);
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1));
            Assert.That(((IFluidSuspensionView)world.Runtime.View).SuspendedFluids[0].State, Is.EqualTo(suspended.State));
            var hits = new CellHit[16]; Success(world.QueryRegion(All(world), hits).Result);
            Assert.That(world.QueryRegion(All(world), hits).WrittenCount, Is.EqualTo(9));
            var point = world.QueryPoint(new Vector2(1.15f, 1.15f)); Success(point.Result); Assert.That(point.Hit.MaterialId, Is.EqualTo(102));
            var remove = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, All(world), 0, world.Version.Generation)); Success(remove.Result);
            Success(world.Step().Result);
            Assert.That(world.Retry(remove.Token).AffectedCount, Is.EqualTo(9));
            Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(1)); Assert.That(world.QueryMaterialCounts().SuspendedCells, Is.Zero);
            point = world.QueryPoint(new Vector2(1.15f, 1.15f)); Success(point.Result);
            Assert.That(point.Hit.MaterialId, Is.EqualTo(101)); Assert.That(point.Hit.State, Is.EqualTo(suspended.State));
            Assert.That(Array.Exists(changes, c => c.Kind == CellChangeKind.Restored && c.BeforeKey.Position.BodyId == suspended.RecordId && c.AfterKey.Position.X == 11), Is.True);
        }

        [TestCase(MaterialOperation.Remove)] [TestCase(MaterialOperation.Replace)] [TestCase(MaterialOperation.Ignite)]
        public void F01_08_CommandsDoNotSelectHiddenRecordsAtUnoccupiedAnchor(MaterialOperation operation)
        {
            SimulationWorld world = Create(BlockAndFluid()); PlaceCover(world); Success(world.Step().Result); MoveCoverAway(world);
            var region = new WorldRect(new Vector2(1.1f, 1.1f), new Vector2(1.2f, 1.2f));
            var query = world.QueryPoint(new Vector2(1.15f, 1.15f)); Success(query.Result); Assert.That(query.HasHit, Is.False);
            Assert.That(world.QuerySegment(new Vector2(1.11f, 1.11f), new Vector2(1.19f, 1.19f), new CellHit[1]).RequiredCount, Is.Zero);
            ushort id = operation == MaterialOperation.Replace ? (ushort)104 : (ushort)0;
            var command = world.Enqueue(new MaterialCommand(operation, region, id, world.Version.Generation)); Success(command.Result);
            Success(world.Step().Result); Success(world.Retry(command.Token).Result);
            Assert.That(world.Retry(command.Token).AffectedCount, Is.Zero); Assert.That(Count(world, 101), Is.EqualTo(1));
            Assert.That(world.QueryPoint(new Vector2(1.15f, 1.15f)).Hit.MaterialId, Is.EqualTo(101));
        }

        [Test] public void F01_07_SpawnCapacityIncludesHiddenWaterAndWorldStaysReadyAfterRejection()
        {
            SimulationWorld world = Create(BlockAndFluid(maxCells: 10)); PlaceCover(world); Success(world.Step().Result);
            var spawn = world.Enqueue(new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(5, 5), new Vector2(5.1f, 5.1f)), 101, world.Version.Generation)); Success(spawn.Result);
            Success(world.Step().Result);
            Assert.That(world.Retry(spawn.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Ready)); Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(10));
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1));
        }

        [Test] public void F01_08_ReplacedFluidCaptureDoesNotTreatRecordIdAsBodyMapping()
        {
            // 位姿探针令格面部分重叠，但命令按真实中心选格，只命中网格水；正式子步暂存新实例。
            SimulationWorld world = Create(M06Sources.Create(new[] { new InitialCell(11, 11, 101), new InitialCell(11, 12, 102),
                new InitialCell(10, 10, 102), new InitialCell(11, 10, 102), new InitialCell(12, 10, 102),
                new InitialCell(10, 11, 102), new InitialCell(12, 11, 102) }, new[] { new Vector2Int(10, 10) }));
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(1.1f, 1.135f), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
            BodyIdMapping[] mappings = null; CellChange[] changes = null;
            world.Committed += change => { mappings = change.Changes.BodyMappings.ToArray(); changes = change.Changes.Cells.ToArray(); };
            var replace = world.Enqueue(new MaterialCommand(MaterialOperation.Replace, new WorldRect(new Vector2(1.145f, 1.145f), new Vector2(1.155f, 1.155f)), 101, world.Version.Generation)); Success(replace.Result);
            Success(world.Step().Result); Success(world.Retry(replace.Token).Result);
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1)); Assert.That(mappings, Is.Empty);
            Assert.That(Array.Exists(changes, c => c.Kind == CellChangeKind.Replaced && c.AfterKey.Position.OwnerKind == OwnerKind.Suspended), Is.True);
        }

        [Test] public void F01_08_SpawnAtAnchorIsNotOverwrittenByRestoration()
        {
            SimulationWorld world = Create(BlockAndFluid()); PlaceCover(world); Success(world.Step().Result); MoveCoverAway(world);
            var spawn = world.Enqueue(new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(1.1f, 1.1f), new Vector2(1.2f, 1.2f)), 104, world.Version.Generation)); Success(spawn.Result);
            Success(world.Step().Result); Success(world.Retry(spawn.Token).Result);
            Assert.That(world.QueryPoint(new Vector2(1.15f, 1.15f)).Hit.MaterialId, Is.EqualTo(104));
            Assert.That(Count(world, 101), Is.EqualTo(1)); Assert.That(Count(world, 104), Is.EqualTo(1)); Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(11));
        }

        [Test] public void F01_05_DefaultHiddenSteamExpiresAfterTwoHundredFullSteps()
        {
            SimulationWorld world = Create(BlockAndFluid(103)); PlaceCover(world);
            for (int step = 1; step <= 200; step++)
            {
                Success(world.Step().Result);
                Assert.That(Count(world, 103), Is.EqualTo(step == 200 ? 0 : 1));
                if (step < 200) Assert.That(((IFluidSuspensionView)world.Runtime.View).SuspendedFluids[0].State.LifetimeTicksRemaining, Is.EqualTo(200 - step));
            }
            Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(9));
        }

        [TestCase(FailurePoint.BeforeFluidCapture)] [TestCase(FailurePoint.BeforeFluidRestoration)]
        public void F01_07_RealResourceFailureKeepsLastVersionAndFaultsCurrentToken(FailurePoint point)
        {
            var renderer = new CommittedWorldRenderer { DisplayLayer = 29 };
            SimulationWorld world = Create(BlockAndFluid(), renderer); PlaceCover(world);
            if (point == FailurePoint.BeforeFluidRestoration) { Success(world.Step().Result); MoveCoverAway(world); }
            // SetBodyForTest只发布状态探针；先显式同步正式显示，建立同版本故障基线。
            using (IPreparedWorldDisplay baseline = renderer.PrepareCommit(world.Runtime.View, new ChangeSet(world.Version, null)))
            { Success(baseline.Result); baseline.Adopt(); }
            renderer.FlushFrame(); WorldVersion shown = world.Version; int quantity = world.QueryMaterialCounts().TotalCells;
            world.Runtime.Failures = new FluidFailure(point);
            var command = world.Enqueue(new MaterialCommand(MaterialOperation.Ignite, new WorldRect(new Vector2(5, 5), new Vector2(5.1f, 5.1f)), 0, world.Version.Generation)); Success(command.Result);
            StepResult failed = world.Step(); Assert.That(failed.Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Faulted)); Assert.That(world.Version, Is.EqualTo(shown));
            renderer.FlushFrame(); Assert.That(renderer.UploadedVersion, Is.EqualTo(shown)); Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(quantity));
            Assert.That(world.QueryPoint(new Vector2(1.15f, 1.15f)).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Assert.That(world.Retry(command.Token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
            Success(world.Reset()); Assert.That(world.QueryMaterialCounts().SuspendedCells, Is.Zero);
        }

        [Test] public void F01_06_RestoredWaterExtinguishesFirstContactWithoutUndoingFuel()
        {
            SimulationWorld world = Create(BlockAndFluid()); PlaceCover(world); Success(world.Step().Result); MoveCoverAway(world);
            // 新木块继续覆盖锚点及四邻格；隐藏水不产生接触，木格可以实际烧损。
            var spawn = world.Enqueue(new MaterialCommand(MaterialOperation.Spawn, new WorldRect(new Vector2(1, 1), new Vector2(1.3f, 1.3f)), 104, world.Version.Generation)); Success(spawn.Result);
            var ignite = world.Enqueue(new MaterialCommand(MaterialOperation.Ignite, new WorldRect(new Vector2(1.2f, 1.1f), new Vector2(1.3f, 1.2f)), 0, world.Version.Generation)); Success(ignite.Result);
            Success(world.Step().Result);
            Success(world.Retry(spawn.Token).Result); Success(world.Retry(ignite.Token).Result);
            Success(world.Step().Result);
            var wood = world.QueryPoint(new Vector2(1.25f, 1.15f)); Success(wood.Result);
            Assert.That(wood.Hit.State.IsBurning, Is.True); Assert.That(wood.Hit.State.FuelTicksRemaining, Is.EqualTo(249));
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1));
            // 删除中心格给原位恢复腾出孔，燃烧阶段先扣到248，恢复接触随后灭火。
            var remove = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, new WorldRect(new Vector2(1.1f, 1.1f), new Vector2(1.2f, 1.2f)), 0, world.Version.Generation)); Success(remove.Result);
            Success(world.Step().Result); Success(world.Retry(remove.Token).Result);
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.Zero);
            wood = world.QueryPoint(new Vector2(1.25f, 1.15f)); Success(wood.Result);
            Assert.That(wood.Hit.State.IsBurning, Is.False); Assert.That(wood.Hit.State.FuelTicksRemaining, Is.EqualTo(248));
            Assert.That(Count(world, 101), Is.EqualTo(1));
            Success(world.Step().Result); Assert.That(world.QueryPoint(new Vector2(1.25f, 1.15f)).Hit.State.FuelTicksRemaining, Is.EqualTo(248));
        }

        [Test] public void F01_09_TwentyHiddenWorldResetDisposeRoundsClearRecordsAndPhysics()
        {
            int objects = ActivePhysicsObjects();
            for (int round = 0; round < 20; round++)
            {
                SimulationWorld world = Create(BlockAndFluid()); PlaceCover(world); Success(world.Step().Result);
                var old = world.Runtime.State.Published; ulong generation = world.Version.Generation;
                Success(world.Reset()); Assert.That(world.Version.Generation, Is.EqualTo(generation + 1));
                Assert.That(world.Version.CommittedTick, Is.Zero); Assert.That(world.QueryMaterialCounts().SuspendedCells, Is.Zero);
                Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(10));
                Assert.Throws<ObjectDisposedException>(() => { _ = old.SuspendedFluids.Length; });
                Success(world.Dispose()); Assert.That(world.QueryMaterialCounts().Result.ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
            }
            Assert.That(ActivePhysicsObjects(), Is.EqualTo(objects));
        }

        [TestCase(false)] [TestCase(true)] public void F01_10_Original175CellScenePassesTick264AndConservesWater(bool frozen)
        {
            SimulationWorld world = Create(BaselineSources.Read(), frozen: frozen);
            Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(175));
            int water = Count(world, 101), maximumHidden = 0;
            for (int tick = 1; tick <= 320; tick++)
            {
                Success(world.Step().Result); Assert.That(Count(world, 101), Is.EqualTo(water), "Tick=" + tick);
                maximumHidden = Math.Max(maximumHidden, world.QueryMaterialCounts().SuspendedWaterCells);
            }
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Ready));
            // 历史Tick264覆盖发生在允许旋转的模式；禁转模式核对推进和水量，不要求产生覆盖。
            if (!frozen) Assert.That(maximumHidden, Is.GreaterThan(0));
            TestContext.WriteLine($"175格初态，禁转={frozen}，Tick320 Ready，水总量={water}，最大暂存水={maximumHidden}。");
        }

        [UnityTest] public IEnumerator F01_10_NoEmptyTargetWaitsTenThousandTicksWithoutLosingWater()
        {
            SimulationWorld world = Create(BlockAndFluid()); PlaceCover(world);
            for (int tick = 1; tick <= 10000; tick++)
            {
                Success(world.Step().Result);
                Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1)); Assert.That(world.QueryMaterialCounts().TotalCells, Is.EqualTo(10));
                if (tick % 250 == 0) yield return null;
            }
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Ready));
        }

        [UnityTest] public IEnumerator F01_08_ActualGpuPixelsHideAndRestoreAtOneAndThreeTimes()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("无可渲染GPU，实际像素输出未测。");
            foreach (int scale in new[] { 1, 3 })
            {
                var renderer = new CommittedWorldRenderer { DisplayLayer = 29 };
                SimulationWorld world = Create(BlockAndFluid(), renderer); PlaceCover(world); Success(world.Step().Result); renderer.FlushFrame();
                var owner = new GameObject("F01水汽GPU验收"); var camera = owner.AddComponent<Camera>();
                var target = new RenderTexture(32 * scale, 32 * scale, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1, filterMode = FilterMode.Point };
                target.Create(); camera.targetTexture = target; camera.cullingMask = 1 << 29; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                PixelWorldViewport.Configure(camera, world.Config, Vector2.zero, scale);
                try
                {
                    yield return null; yield return new WaitForEndOfFrame();
                    Texture2D hidden = Read(target);
                    try { AssertBluePixels(hidden.GetPixels32(), 0); }
                    finally { UnityEngine.Object.Destroy(hidden); }
                    var remove = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, All(world), 0, world.Version.Generation)); Success(remove.Result); Success(world.Step().Result); renderer.FlushFrame();
                    yield return null; yield return new WaitForEndOfFrame();
                    Texture2D restored = Read(target);
                    try
                    {
                        AssertBluePixels(restored.GetPixels32(), scale * scale);
                        string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "docs/validation/F01"); Directory.CreateDirectory(folder);
                        File.WriteAllBytes(Path.Combine(folder, "恢复GPU-" + scale + "倍.png"), restored.EncodeToPNG());
                    }
                    finally { UnityEngine.Object.Destroy(restored); }
                    Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
                }
                finally { camera.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(owner); Success(world.Dispose()); }
            }
        }
        private static Texture2D Read(RenderTexture target)
        {
            RenderTexture prior = RenderTexture.active; RenderTexture.active = target;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try { image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply(); return image; }
            finally { RenderTexture.active = prior; }
        }
        private static int ActivePhysicsObjects()
        {
            int count = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (go.scene.IsValid() && go.scene.name.StartsWith("OpenOitaPhysics-", StringComparison.Ordinal) && go.activeSelf) count++;
            return count;
        }
        private static void AssertBluePixels(Color32[] pixels, int expected)
        {
            int blue = 0; foreach (Color32 pixel in pixels) if (pixel.b > pixel.r + 30 && pixel.b > pixel.g) blue++;
            Assert.That(blue, Is.EqualTo(expected), "真实GPU中的水像素数量与活动材料不一致。");
        }
        private sealed class FluidFailure : IFailureInjector
        {
            private readonly FailurePoint _point;
            internal FluidFailure(FailurePoint point) { _point = point; }
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point == _point ?
                WorldResult.Failure(WorldErrorCode.CapacityExceeded, new WorldDiagnostic("Physics", "fluidCpuBytes", "水汽工作集故障注入")) : WorldResult.Success();
        }
    }
}
