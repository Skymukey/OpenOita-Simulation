using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Render;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode.M07B
{
    public sealed class M07DisplayAcceptanceTests
    {
        private readonly List<IWorld> _worlds = new();
        private SimulationWorld Create(WorldSources sources, out CommittedWorldRenderer renderer, Vector2 origin = default)
        {
            CommittedWorldRenderer created = null;
            var result = new WorldSimulation(rendererFactory: () => created = new CommittedWorldRenderer()).Create(sources, origin);
            Success(result.Result); _worlds.Add(result.World); renderer = created; return (SimulationWorld)result.World;
        }
        [UnityTearDown] public IEnumerator Cleanup() { foreach (IWorld world in _worlds) world.Dispose(); _worlds.Clear(); yield return null; yield return null; }
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True, result.ErrorCode + " / " + result.Diagnostic.Stage + ": " + result.Diagnostic.Message);
        private static WorldRect Rect(int x, int y, int width = 1) => new WorldRect(new Vector2(x * 0.1f, y * 0.1f), new Vector2((x + width) * 0.1f, (y + 1) * 0.1f));
        private static CommandToken Send(IWorld world, MaterialOperation op, WorldRect rect, ushort material = 0)
        { var result = world.Enqueue(new MaterialCommand(op, rect, material, world.Version.Generation)); Success(result.Result); return result.Token; }
        private static WorldSources Small(WorldSources source, int width, int height, float size)
        {
            // 合法配置副本；不改生产默认值。
            string json = source.WorldConfigText.Replace("\"width\": 256", "\"width\": " + width).Replace("\"height\": 256", "\"height\": " + height)
                .Replace("\"cellSize\": 0.1", "\"cellSize\": " + size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return new WorldSources(source.MaterialsText, json, source.SceneText);
        }
        [Test] public void M07B_01_08_SparseIdOriginCrossChunkAndIndependentTexels()
        {
            var source = M06Sources.Create(new[] { new InitialCell(0, 0, 102), new InitialCell(127, 0, 102), new InitialCell(128, 0, 104), new InitialCell(4, 0, 101), new InitialCell(6, 0, 103) }, new[] { new Vector2Int(0, 0), new Vector2Int(127, 0) });
            source = new WorldSources(source.MaterialsText.Replace("\"id\": 102", "\"id\": 65535"), source.WorldConfigText, source.SceneText.Replace("\"materialId\": 102", "\"materialId\": 65535"));
            var origin = new Vector2(2, 3); IWorld world = Create(source, out var renderer, origin); renderer.FlushFrame();
            Assert.That(renderer.TileCount, Is.EqualTo(2));
            foreach (int x in new[] { 0, 127, 128 })
            {
                var hit = world.QueryPoint(origin + new Vector2((x + 0.5f) * 0.1f, 0.05f)); Success(hit.Result); Assert.That(hit.HasHit, Is.True);
                Assert.That(renderer.TryGetPixel(0, x, 0, out Color32 pixel), Is.True); Assert.That(pixel.a, Is.EqualTo(255));
            }
            Assert.That(world.QueryPoint(origin + new Vector2(0.05f, 0.05f)).Hit.MaterialId, Is.EqualTo(65535));
            renderer.TryGetPixel(0, 1, 0, out Color32 empty); Assert.That(empty.a, Is.Zero);
            Assert.That(renderer.TextureFor(0, 0, 0).width, Is.EqualTo(128)); Assert.That(renderer.TextureFor(0, 128, 0).filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
        }
        [Test] public void M07B_02_TwoStepsFlushOnlyFinalVersionAndTailClears()
        {
            IWorld world = Create(Small(M06Sources.Create(Array.Empty<InitialCell>()), 129, 1, 0.1f), out var renderer);
            var token = Send(world, MaterialOperation.Spawn, Rect(128, 0), 101); Success(world.Step().Result); Success(world.Retry(token).Result);
            Assert.That(renderer.UploadedVersion.CommittedTick, Is.Zero);
            Send(world, MaterialOperation.Remove, Rect(128, 0)); Success(world.Step().Result); renderer.FlushFrame();
            Assert.That(renderer.UploadedVersion, Is.EqualTo(new WorldVersion(1, 2))); Assert.That(renderer.TileCount, Is.Zero);
            Assert.That(world.QueryPoint(new Vector2(12.85f, 0.05f)).HasHit, Is.False);
            Send(world, MaterialOperation.Spawn, Rect(128, 0), 101); Success(world.Step().Result); renderer.FlushFrame();
            Color32[] tail = renderer.TextureFor(0, 128, 0).GetPixels32(); Assert.That(tail[0].a, Is.EqualTo(255));
            for (int i = 1; i < tail.Length; i++) Assert.That(tail[i].a, Is.Zero);
        }
        [TestCase(true)] [TestCase(false)] public void M07B_03_SplitAndRetainDisplaysMatchCommittedQueries(bool middle)
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(20, 20, 104), new InitialCell(21, 20, 104), new InitialCell(22, 20, 104) }), out var renderer);
            BodySnapshot old = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(old.BodyId, new BodyPose(old.Pose.Position, 0.3f), default, old.LocalCenterOfMass, old.GeometryVersion)));
            // 通过内部检查点观察结构前后格中心，公开事件只在完整Step后出现。
            int removeX = middle ? 1 : 2;
            Vector2 remove = Center(old.Pose.Position, 0.3f, removeX, 0);
            int notifications = 0; world.Committed += _ => notifications++;
            world.Runtime.BeforePhysicsForTest = () =>
            {
                Assert.That(notifications, Is.Zero); Assert.That(renderer.LogicalVersion.CommittedTick, Is.Zero);
                int remaining = 0;
                foreach (CellKey key in world.Runtime.State.OccupiedCells)
                {
                    BodySnapshot current = default; foreach (BodySnapshot candidate in world.Runtime.State.Bodies) if (candidate.BodyId == key.Position.BodyId) current = candidate;
                    Vector2 center = Center(current.Pose.Position, current.Pose.AngleRadians, key.Position.X, key.Position.Y);
                    float nearest = float.PositiveInfinity;
                    for (int x = 0; x < 3; x++) if (x != removeX) nearest = Mathf.Min(nearest, Vector2.Distance(center, Center(old.Pose.Position, 0.3f, x, 0)));
                    Assert.That(nearest, Is.LessThanOrEqualTo(1e-5f)); remaining++;
                }
                Assert.That(remaining, Is.EqualTo(2));
            };
            Send(world, MaterialOperation.Remove, new WorldRect(remove - Vector2.one * 0.01f, remove + Vector2.one * 0.01f));
            Success(world.Step().Result); renderer.FlushFrame(); Assert.That(notifications, Is.EqualTo(1));
            Assert.That(world.Runtime.State.Bodies.Length, Is.EqualTo(middle ? 2 : 1));
            foreach (CellKey key in world.Runtime.View.OccupiedCells)
            {
                BodySnapshot body = default; foreach (var candidate in world.Runtime.View.Bodies) if (candidate.BodyId == key.Position.BodyId) body = candidate;
                var query = world.QueryPoint(Center(body.Pose.Position, body.Pose.AngleRadians, key.Position.X, key.Position.Y));
                Assert.That(query.HasHit, Is.True); Assert.That(query.Hit.Position, Is.EqualTo(key.Position));
                Assert.That(renderer.TryGetPixel(key.Position.BodyId, key.Position.X, key.Position.Y, out Color32 pixel), Is.True); Assert.That(pixel.a, Is.EqualTo(255));
            }
            Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
        }
        [Test] public void M07B_04_ShortFuelSpreadBurnoutAndChangesHaveReasons()
        {
            var source = M06Sources.Create(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 104) }, new[] { new Vector2Int(10, 10) }, new[] { new Vector2Int(10, 10) });
            source = new WorldSources(BaselineSources.ShortBurningMaterials(source.MaterialsText), source.WorldConfigText, source.SceneText);
            var world = Create(source, out var renderer); bool burnout = false;
            world.Committed += changes => { foreach (CellChange change in changes.Changes.Cells) if (change.Kind == CellChangeKind.BurnedOut) burnout = true; };
            for (int tick = 1; tick <= 5; tick++)
            {
                Success(world.Step().Result); renderer.FlushFrame();
                if (tick == 1) Assert.That(renderer.BurningCount, Is.EqualTo(1));
                if (tick == 2) { Assert.That(renderer.BurningCount, Is.EqualTo(2)); Assert.That(world.QueryPoint(new Vector2(1.15f, 1.05f)).Hit.State.FuelTicksRemaining, Is.EqualTo(5)); }
                if (tick < 5) { renderer.TryGetPixel(0, 10, 10, out Color32 pixel); Assert.That(pixel.a, Is.EqualTo(255)); Assert.That(pixel.r, Is.LessThan(166)); }
                AssertFlames(world, renderer);
            }
            Assert.That(burnout, Is.True); Assert.That(world.QueryPoint(new Vector2(1.05f, 1.05f)).HasHit, Is.False);
        }
        [Test] public void M07B_05_RemoveReplaceIgniteResetAndWetCleanup()
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 104) }, new[] { new Vector2Int(10, 10) }), out var renderer);
            Send(world, MaterialOperation.Ignite, Rect(10, 10)); Success(world.Step().Result); renderer.FlushFrame(); AssertFlames(world, renderer);
            Success(world.Step().Result); renderer.FlushFrame(); var burned = world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State.FuelTicksRemaining;
            Send(world, MaterialOperation.Spawn, Rect(10, 11), 101); Success(world.Step().Result); renderer.FlushFrame(); AssertFlames(world, renderer);
            Assert.That(renderer.BurningCount, Is.Zero); Assert.That(world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State.FuelTicksRemaining, Is.EqualTo(burned));
            Send(world, MaterialOperation.Replace, Rect(10, 10), 104); Success(world.Step().Result); renderer.FlushFrame(); AssertFlames(world, renderer);
            Assert.That(world.QueryPoint(new Vector2(1.05f, 1.05f)).Hit.State.FuelTicksRemaining, Is.EqualTo(250));
            Send(world, MaterialOperation.Remove, Rect(10, 10)); Success(world.Step().Result); renderer.FlushFrame(); AssertFlames(world, renderer);
            Success(world.Reset()); Success(world.Step().Result); Assert.That(world.Version.Generation, Is.EqualTo(2));
        }
        [TestCase(FailurePoint.AfterDisplayTilePrepared)] [TestCase(FailurePoint.AfterPhysicsSubstep)] [TestCase(FailurePoint.AfterStructurePrepared)]
        public void M07B_06_FailedTickDoesNotAdoptPublishOrUpload(FailurePoint point)
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 104) }, burning: new[] { new Vector2Int(10, 10) }), out var renderer);
            renderer.FlushFrame(); WorldVersion before = world.Version;
            int flames = renderer.BurningCount; int notifications = 0; world.Committed += _ => notifications++;
            var token = Send(world, MaterialOperation.Remove, Rect(10, 10));
            if (point == FailurePoint.AfterDisplayTilePrepared)
            {
                // 确保新候选含一个显示块，触发实际准备检查点。
                Send(world, MaterialOperation.Spawn, Rect(30, 0), 102); renderer.Failures = new Failure(point);
            }
            else world.Runtime.Failures = new Failure(point);
            Assert.That(world.Step().Result.IsSuccess, Is.False); renderer.FlushFrame();
            Assert.That(world.Version, Is.EqualTo(before)); Assert.That(renderer.LogicalVersion, Is.EqualTo(before)); Assert.That(renderer.UploadedVersion, Is.EqualTo(before));
            Assert.That(renderer.BurningCount, Is.EqualTo(flames)); Assert.That(notifications, Is.Zero);
            Assert.That(world.Retry(token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.Faulted));
        }
        [UnityTest] public IEnumerator M07B_06_07_InitializationFailureAndTwentyLifecycleRoundsReleaseResources()
        {
            yield return null; yield return null;
            int textures = Count<Texture2D>("OpenOita材料纹素"), meshes = Count<Mesh>("OpenOita"), objects = Count<GameObject>("OpenOita显示 ");
            int materials = Count<Material>("OpenOita"), roots = Count<GameObject>("OpenOita正式显示");
            var source = M06Sources.Create(new[] { new InitialCell(10, 10, 104), new InitialCell(150, 0, 102) }, burning: new[] { new Vector2Int(10, 10) });
            var failed = new WorldSimulation(rendererFactory: () => new CommittedWorldRenderer { Failures = new Failure(FailurePoint.AfterDisplayTilePrepared) }).Create(source, Vector2.zero);
            Assert.That(failed.Result.IsSuccess, Is.False); Assert.That(failed.World, Is.Null);
            yield return null; yield return null;
            for (int i = 0; i < 20; i++)
            {
                IWorld world = Create(source, out var renderer); renderer.FlushFrame(); Success(world.Step().Result); renderer.FlushFrame();
                Success(world.Reset()); Success(world.Dispose()); Success(world.Dispose());
                yield return null; yield return null;
                Assert.That(Count<Material>("OpenOita"), Is.EqualTo(materials)); Assert.That(Count<GameObject>("OpenOita正式显示"), Is.EqualTo(roots));
                Assert.That(Count<Texture2D>("OpenOita材料纹素"), Is.EqualTo(textures)); Assert.That(Count<Mesh>("OpenOita"), Is.EqualTo(meshes)); Assert.That(Count<GameObject>("OpenOita显示 "), Is.EqualTo(objects));
            }
            TestContext.WriteLine($"20轮正式Create/Step/Reset/Dispose资源稳定：纹理{textures}、网格{meshes}、显示对象{objects}、材质{materials}、根对象{roots}。");
        }
        [UnityTest] public IEnumerator M07B_09_ActualGpuOutputOneAndThreeTimesCoverage()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("无GPU；实际输出未测。");
            var cells = new List<InitialCell>(); for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) cells.Add(new InitialCell(x, y, 102));
            cells.Add(new InitialCell(255, 0, 102)); cells.Add(new InitialCell(0, 255, 102)); cells.Add(new InitialCell(255, 255, 102));
            IWorld world = Create(M06Sources.Create(cells, new[] { Vector2Int.zero, new Vector2Int(255, 0), new Vector2Int(0, 255), new Vector2Int(255, 255) }), out var renderer); renderer.FlamesVisible = false; renderer.FlushFrame();
            var owner = new GameObject("M07正式GPU像素验收"); var camera = owner.AddComponent<Camera>();
            var target = new RenderTexture(768, 768, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1, filterMode = FilterMode.Point };
            target.Create(); camera.targetTexture = target; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            Texture2D capture = null;
            try
            {
                foreach (int scale in new[] { 1, 3 })
                {
                    PixelWorldViewport.Configure(camera, world.Config, Vector2.zero, scale);
                    yield return null; yield return new WaitForEndOfFrame();
                    RenderTexture previous = RenderTexture.active; RenderTexture.active = target;
                    capture = new Texture2D(768, 768, TextureFormat.RGBA32, false); capture.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); capture.Apply(); RenderTexture.active = previous;
                    Color32[] pixels = capture.GetPixels32(); Color32 color = pixels[0]; Assert.That(color.r, Is.GreaterThan(20));
                    int n = 8 * scale;
                    for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) Assert.That(pixels[y * 768 + x], Is.EqualTo(color), $"倍率{scale}，({x},{y})出现缝隙/混色。");
                    Assert.That(pixels[n], Is.Not.EqualTo(color)); Assert.That(pixels[n * 768], Is.Not.EqualTo(color));
                    int far = 255 * scale;
                    for (int y = far; y < 256 * scale; y++) for (int x = far; x < 256 * scale; x++) Assert.That(pixels[y * 768 + x], Is.EqualTo(color));
                    if (scale == 1) { Assert.That(pixels[256], Is.Not.EqualTo(color)); Assert.That(pixels[256 * 768], Is.Not.EqualTo(color)); }
                    Assert.That(world.QueryRegion(new WorldRect(Vector2.zero, new Vector2(25.6f, 25.6f)), new CellHit[67]).RequiredCount, Is.EqualTo(67));
                    string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "docs", "validation", "M07"); Directory.CreateDirectory(folder);
                    File.WriteAllBytes(Path.Combine(folder, "正式GPU-" + scale + "倍.png"), capture.EncodeToPNG());
                    TestContext.WriteLine($"正式IWorld，768×768原始GPU输出，倍率{scale}，8×8材料覆盖{n}×{n}像素，逻辑世界覆盖{256 * scale}×{256 * scale}；版本1/0。");
                    UnityEngine.Object.Destroy(capture); capture = null;
                }
            }
            finally { if (capture != null) UnityEngine.Object.Destroy(capture); camera.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(owner); }
        }
        [Test] public void M07B_10_Config129x65AndViewScaleNeverMutatesWorld()
        {
            IWorld world = Create(Small(M06Sources.Create(new[] { new InitialCell(128, 0, 102) }), 129, 65, 0.2f), out var renderer);
            Assert.That(world.Config.Width, Is.EqualTo(129)); Assert.That(world.Config.Height, Is.EqualTo(65)); Assert.That(world.Config.CellSize, Is.EqualTo(0.2f));
            var owner = new GameObject("M07视图配置"); var camera = owner.AddComponent<Camera>(); WorldVersion before = world.Version;
            try { foreach (int scale in new[] { 1, 3, 1 }) PixelWorldViewport.Configure(camera, world.Config, Vector2.zero, scale, new Vector2Int(3, 2)); }
            finally { UnityEngine.Object.Destroy(owner); }
            Assert.That(world.Version, Is.EqualTo(before)); Assert.That(world.QueryPoint(new Vector2(25.7f, 0.1f)).HasHit, Is.True); renderer.FlushFrame();
        }
        [Test] public void M07B_11_RotatingMovingRingPreservesEightTexelsAndHole()
        {
            var cells = new List<InitialCell>(); for (int y = 20; y < 23; y++) for (int x = 20; x < 23; x++) if (x != 21 || y != 21) cells.Add(new InitialCell(x, y, 104));
            var world = Create(M06Sources.Create(cells), out var renderer); var body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(body.Pose.Position, 0.23f), new BodyMotion(new Vector2(0.02f, 0), 0.1f), body.LocalCenterOfMass, body.GeometryVersion)));
            Success(world.Step().Result); renderer.FlushFrame(); body = world.Runtime.State.Bodies[0];
            Assert.That(world.QueryPoint(Center(body.Pose.Position, body.Pose.AngleRadians, 1, 1)).HasHit, Is.False);
            int count = 0; for (int y = 0; y < 3; y++) for (int x = 0; x < 3; x++) { renderer.TryGetPixel(body.BodyId, x, y, out Color32 pixel); if (pixel.a != 0) count++; }
            Assert.That(count, Is.EqualTo(8)); renderer.TryGetPixel(body.BodyId, 1, 1, out Color32 hole); Assert.That(hole.a, Is.Zero);
            Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
            var owner = new GameObject("M07旋转体倍率切换"); var camera = owner.AddComponent<Camera>(); WorldVersion before = world.Version;
            try { foreach (int scale in new[] { 1, 3, 1 }) PixelWorldViewport.Configure(camera, world.Config, Vector2.zero, scale); }
            finally { UnityEngine.Object.Destroy(owner); }
            Assert.That(world.Version, Is.EqualTo(before)); Assert.That(world.Runtime.State.Bodies[0].Equals(body), Is.True);
        }
        private static Vector2 Center(Vector2 origin, float angle, int x, int y)
        {
            Vector2 p = new Vector2((x + 0.5f) * 0.1f, (y + 0.5f) * 0.1f);
            return origin + new Vector2(Mathf.Cos(angle) * p.x - Mathf.Sin(angle) * p.y, Mathf.Sin(angle) * p.x + Mathf.Cos(angle) * p.y);
        }
        [UnityTest] public IEnumerator M07B_05_ActualFlamePixelsClearWithCommittedRemove()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("无GPU；火焰实际输出未测。");
            var world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 104) }, burning: new[] { new Vector2Int(10, 10) }), out var renderer);
            renderer.FlushFrame();
            var owner = new GameObject("M07正式火焰GPU读回"); var camera = owner.AddComponent<Camera>();
            var target = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 }; target.Create(); camera.targetTexture = target;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; PixelWorldViewport.Configure(camera, world.Config, Vector2.zero, 8);
            Texture2D capture = null;
            try
            {
                yield return null; yield return new WaitForEndOfFrame();
                capture = ReadTarget(target);
                bool flame = false; Color32[] pixels = capture.GetPixels32();
                for (int y = 88; y < 91; y++) for (int x = 80; x < 88; x++) if (pixels[y * 128 + x].r > 10) flame = true;
                Assert.That(flame, Is.True, "已提交燃烧格上方必须有真实火焰像素。");
                string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "docs", "validation", "M07"); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "正式GPU-火焰.png"), capture.EncodeToPNG()); UnityEngine.Object.Destroy(capture); capture = null;
                Send(world, MaterialOperation.Remove, Rect(10, 10)); Success(world.Step().Result); renderer.FlushFrame();
                yield return null; yield return new WaitForEndOfFrame(); capture = ReadTarget(target); pixels = capture.GetPixels32();
                for (int y = 80; y < 91; y++) for (int x = 80; x < 88; x++) Assert.That(pixels[y * 128 + x].r, Is.Zero);
                Assert.That(renderer.BurningCount, Is.Zero); Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
                TestContext.WriteLine("128×128正式GPU输出，8倍单格，火焰位于材料格上方；成功Remove后材料和火焰实际像素均清空。");
            }
            finally { if (capture != null) UnityEngine.Object.Destroy(capture); camera.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(owner); }
        }
        private static Texture2D ReadTarget(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active; RenderTexture.active = target;
            var capture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false); capture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); capture.Apply(); RenderTexture.active = previous;
            return capture;
        }
        [Test] public void M07B_05_BurningBodySplitRetiresOldFlameAssociation()
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(20, 20, 104), new InitialCell(21, 20, 104), new InitialCell(22, 20, 104) },
                burning: new[] { new Vector2Int(20, 20), new Vector2Int(21, 20), new Vector2Int(22, 20) }), out var renderer);
            renderer.FlushFrame(); ulong old = world.Runtime.State.Bodies[0].BodyId;
            Send(world, MaterialOperation.Remove, Rect(21, 20)); Success(world.Step().Result); renderer.FlushFrame(); AssertFlames(world, renderer);
            Assert.That(renderer.BurningCount, Is.EqualTo(2)); Assert.That(world.Runtime.State.Bodies.Length, Is.EqualTo(2));
            Assert.That(renderer.TryGetPixel(old, 0, 0, out _), Is.False);
            foreach (BodySnapshot body in world.Runtime.State.Bodies) Assert.That(body.BodyId, Is.Not.EqualTo(old));
        }
        [UnityTest] public IEnumerator M07B_07_09_DefaultHostCreatesReadyWorldAndFlushesFrame()
        {
            var owner = new GameObject("M07默认Host验收"); owner.SetActive(false);
            WorldHost host = owner.AddComponent<WorldHost>();
            try
            {
                owner.SetActive(true); Success(host.LastResult); Assert.That(host.World, Is.Not.Null); Assert.That(host.World.Lifecycle, Is.EqualTo(WorldLifecycle.Ready));
                yield return null; yield return new WaitForEndOfFrame();
                Assert.That(host.World.Version.CommittedTick, Is.GreaterThan(0));
                var query = host.World.QueryPoint(new Vector2(0.05f, 0.05f)); Success(query.Result); Assert.That(query.Version, Is.EqualTo(host.World.Version));
                Success(host.ResetWorld()); Assert.That(host.World.Version.Generation, Is.EqualTo(2));
                Success(host.CloseWorld()); Success(host.CloseWorld());
            }
            finally { UnityEngine.Object.Destroy(owner); }
        }
        [Test] public void M07B_05_RotatingFirstWaterContactUsesSameBurnDamageAndFlameVersion()
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 101) }, burning: new[] { new Vector2Int(10, 10) }), out var renderer);
            HoldFluids(world);
            var body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(0.998f, 1), 0), new BodyMotion(Vector2.zero, 2.5f), body.LocalCenterOfMass, body.GeometryVersion)));
            Success(world.Step().Result); renderer.FlushFrame(); AssertFlames(world, renderer);
            var query = world.QueryPoint(Center(world.Runtime.State.Bodies[0].Pose.Position, world.Runtime.State.Bodies[0].Pose.AngleRadians, 0, 0));
            Assert.That(query.Hit.State.FuelTicksRemaining, Is.EqualTo(249)); Assert.That(query.Hit.State.IsBurning, Is.False);
            renderer.TryGetPixel(body.BodyId, 0, 0, out Color32 burnt); Success(world.Step().Result); renderer.FlushFrame();
            renderer.TryGetPixel(body.BodyId, 0, 0, out Color32 later); Assert.That(later, Is.EqualTo(burnt));
        }
        [Test] public void M07B_06_NoImmediateTargetPublishesHiddenFluidDisplay()
        {
            var source = M06Sources.Create(new[] { new InitialCell(10, 10, 102), new InitialCell(20, 10, 102), new InitialCell(10, 9, 101), new InitialCell(20, 9, 101),
                new InitialCell(19, 9, 101), new InitialCell(21, 9, 101), new InitialCell(20, 8, 101) }, radius: 1);
            var world = Create(source, out var renderer); HoldFluids(world);
            foreach (BodySnapshot body in world.Runtime.State.Bodies.ToArray())
                Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, body.Pose, new BodyMotion(new Vector2(0, -2), 0), body.LocalCenterOfMass, body.GeometryVersion)));
            renderer.FlushFrame(); WorldVersion before = world.Version;
            Success(world.Step().Result); renderer.FlushFrame();
            Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
            Assert.That(world.Version.CommittedTick, Is.EqualTo(before.CommittedTick + 1));
            Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(2));
            foreach (int x in new[] { 10, 20 })
            {
                Assert.That(renderer.TryGetPixel(0, x, 9, out Color32 pixel), Is.True);
                Assert.That(pixel.a, Is.Zero);
            }
        }
        [Test] public void M07B_06_CommandCapacityRejectsOnlyOneCommandThenCanDisplayNextTick()
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 102), new InitialCell(11, 10, 102) }, new[] { new Vector2Int(10, 10) }, maxChanges: 1), out var renderer);
            var rejected = Send(world, MaterialOperation.Remove, Rect(10, 10, 2)); Success(world.Step().Result); renderer.FlushFrame();
            Assert.That(world.Retry(rejected).Result.ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded)); Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Ready));
            Assert.That(world.QueryPoint(new Vector2(1.15f, 1.05f)).HasHit, Is.True);
            var accepted = Send(world, MaterialOperation.Remove, Rect(11, 10)); Success(world.Step().Result); renderer.FlushFrame(); Success(world.Retry(accepted).Result);
            Assert.That(world.QueryPoint(new Vector2(1.15f, 1.05f)).HasHit, Is.False); Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
        }
        [Test] public void M07B_06_ResetFailureKeepsOldDisplayAndInvalidatesOldTokens()
        {
            var sources = M06Sources.Create(new[] { new InitialCell(10, 10, 104) });
            CommittedWorldRenderer renderer = null; int creates = 0;
            var result = new WorldSimulation(rendererFactory: () => ++creates == 1 ? renderer = new CommittedWorldRenderer() :
                new CommittedWorldRenderer { Failures = new Failure(FailurePoint.AfterDisplayTilePrepared) }).Create(sources, Vector2.zero);
            Success(result.Result); IWorld world = result.World; _worlds.Add(world); renderer.FlushFrame();
            var token = Send(world, MaterialOperation.Ignite, Rect(10, 10)); Success(world.Step().Result); renderer.FlushFrame();
            WorldVersion shown = renderer.UploadedVersion; Assert.That(world.Reset().IsSuccess, Is.False); renderer.FlushFrame();
            Assert.That(world.Lifecycle, Is.EqualTo(WorldLifecycle.Faulted)); Assert.That(renderer.UploadedVersion, Is.EqualTo(shown));
            Assert.That(world.Retry(token).Result.ErrorCode, Is.EqualTo(WorldErrorCode.StaleGeneration));
        }
        [Test] public void M07B_08_FuelTexelsAndLifetimeExpiredChangeAreIndependent()
        {
            var world = Create(M06Sources.Create(new[] { new InitialCell(10, 10, 104), new InitialCell(11, 10, 104), new InitialCell(30, 30, 103) }, new[] { new Vector2Int(10, 10) }), out var renderer);
            var a = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 10, 10)); var steam = new CellKey(1, new CellPositionKey(OwnerKind.Grid, 0, 30, 30));
            Success(world.Runtime.SetCellsForTest(new[] { new CellWrite(a, new CellSnapshot(104, fuelTicksRemaining: 125, spreadCountdown: 10)),
                new CellWrite(steam, new CellSnapshot(103, lifetimeTicksRemaining: 1)) }));
            bool expired = false; world.Committed += changes => { foreach (CellChange change in changes.Changes.Cells) if (change.Kind == CellChangeKind.LifetimeExpired) expired = true; };
            Success(world.Step().Result); renderer.FlushFrame();
            renderer.TryGetPixel(0, 10, 10, out Color32 damaged); renderer.TryGetPixel(0, 11, 10, out Color32 fresh);
            Assert.That(damaged.r, Is.LessThan(fresh.r)); Assert.That(damaged.a, Is.EqualTo(255)); Assert.That(expired, Is.True);
        }
        private static void HoldFluids(SimulationWorld world)
        {
            var writes = new List<CellWrite>();
            foreach (CellKey key in world.Runtime.State.OccupiedCells)
            {
                world.Runtime.State.Read(key, out CellSnapshot cell);
                if (cell.MaterialId == 101 || cell.MaterialId == 103)
                    writes.Add(new CellWrite(key, new CellSnapshot(cell.MaterialId, cell.Flags, cell.FuelTicksRemaining, cell.SpreadCountdown,
                        cell.LifetimeTicksRemaining, cell.MaterialId == 101 ? 2U : 3U, cell.IgnitedTick)));
            }
            Success(world.Runtime.SetCellsForTest(writes.ToArray()));
        }
        private static void AssertFlames(SimulationWorld world, CommittedWorldRenderer renderer)
        {
            int count = 0; foreach (CellKey key in world.Runtime.View.OccupiedCells) { world.Runtime.View.Read(key, out CellSnapshot state); if (state.IsBurning) count++; }
            Assert.That(renderer.BurningCount, Is.EqualTo(count)); Assert.That(renderer.UploadedVersion, Is.EqualTo(world.Version));
        }
        private static int Count<T>(string prefix) where T : UnityEngine.Object { int count = 0; foreach (T item in Resources.FindObjectsOfTypeAll<T>()) if (item.name.StartsWith(prefix, StringComparison.Ordinal)) count++; return count; }
        private sealed class Failure : IFailureInjector
        {
            private readonly FailurePoint _point;
            internal Failure(FailurePoint point) { _point = point; }
            public WorldResult Check(in TransactionContext context, FailurePoint point) => point == _point ? WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("DisplayAcceptance", "候选", "M07故障注入")) : WorldResult.Success();
        }
    }
}
