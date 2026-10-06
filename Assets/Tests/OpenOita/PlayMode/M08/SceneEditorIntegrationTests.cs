#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Editor;
using OpenOita.Simulation;
using OpenOita.Tests.Fixtures;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpenOita.Tests.PlayMode.M08
{
    public sealed class SceneEditorIntegrationTests
    {
        private SceneEditorSession _session;
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True,
            result.ErrorCode + " / " + result.Diagnostic.Stage + " / " + result.Diagnostic.Message);
        private static SceneMaterialData Load(WorldSources source) { Success(SceneMaterialData.Load(source, out var data)); return data; }
        private static string Text(SceneMaterialData data) { Success(data.Export(out var sources)); return sources.SceneText; }
        private static string DirectoryFor(WorldSources source)
        {
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "M08", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "materials.json"), source.MaterialsText);
            File.WriteAllText(Path.Combine(directory, "world_config.json"), source.WorldConfigText);
            File.WriteAllText(Path.Combine(directory, "scene.json"), source.SceneText);
            return directory;
        }
        [UnityTearDown] public IEnumerator Cleanup() { _session?.Dispose(); _session = null; yield return null; yield return null; }

        [Test] public void F01_09_TrialWithHiddenWaterExitAndResetKeepEditableInitialData()
        {
            var cells = new System.Collections.Generic.List<InitialCell> { new InitialCell(11, 11, 101) };
            for (int y = 40; y <= 42; y++) for (int x = 40; x <= 42; x++) cells.Add(new InitialCell(x, y, 102));
            SceneMaterialData data = Load(M06Sources.Create(cells, radius: 1)); string before = Text(data);
            _session = new SceneEditorSession(); Success(_session.BeginTrial(data, Vector2.zero, false));
            var world = (SimulationWorld)_session.Host.World;
            BodySnapshot body = world.Runtime.State.Bodies[0];
            Success(world.Runtime.SetBodyForTest(new BodySnapshot(body.BodyId, new BodyPose(new Vector2(1, 1), 0), default, body.LocalCenterOfMass, body.GeometryVersion)));
            Success(_session.Host.Step().Result); Assert.That(world.QueryMaterialCounts().SuspendedWaterCells, Is.EqualTo(1));
            var old = world.Runtime.State.Published;
            Success(_session.Host.ResetWorld()); Assert.That(world.QueryMaterialCounts().SuspendedCells, Is.Zero);
            Assert.That(Text(data), Is.EqualTo(before)); Assert.That(world.Version.CommittedTick, Is.Zero);
            Success(_session.EndTrial()); Assert.That(_session.Host, Is.Null); Assert.That(_session.IsTrial, Is.False);
            Assert.That(world.QueryMaterialCounts().Result.ErrorCode, Is.EqualTo(WorldErrorCode.Disposed));
            Assert.Throws<ObjectDisposedException>(() => { _ = old.SuspendedFluids.Length; });
            Assert.That(Text(data), Is.EqualTo(before));
        }

        [Test] public void M08_06_OneHundredFormalTicksSaveExitAndResetDoNotPolluteInitialData()
        {
            WorldSources sources = BaselineSources.Read(); string directory = DirectoryFor(sources);
            var document = new SceneEditingDocument(); Success(document.LoadDirectory(directory)); string before = Text(document.Data);
            Success(document.Save(directory)); string disk = File.ReadAllText(Path.Combine(directory, "scene.json"));
            _session = new SceneEditorSession(); Success(_session.BeginTrial(document.Data, new Vector2(2, 3), false));
            var world = _session.Host.World;
            for (int tick = 0; tick < 100; tick++) Success(_session.Host.Step().Result);
            Assert.That(world.Version.CommittedTick, Is.EqualTo(100));
            Assert.That(Text(document.Data), Is.EqualTo(before)); Success(document.Save(directory));
            Assert.That(File.ReadAllText(Path.Combine(directory, "scene.json")), Is.EqualTo(disk));
            Success(_session.EndTrial()); Assert.That(Text(document.Data), Is.EqualTo(before));
            Success(_session.BeginTrial(document.Data, new Vector2(2, 3), false));
            world = _session.Host.World; var runtime = ((SimulationWorld)world).Runtime;
            Assert.That(runtime.State.Initial.FixedCells, Is.EqualTo(document.Data.Snapshot().FixedCells));
            for (int tick = 0; tick < 10; tick++) Success(_session.Host.Step().Result);
            Success(_session.Host.ResetWorld());
            Assert.That(world.Version.Generation, Is.EqualTo(2)); Assert.That(world.Version.CommittedTick, Is.Zero);
            foreach (InitialCell initial in document.Data.Snapshot().Cells)
            {
                var query = world.QueryPoint(GridSelection.Center(document.Data.Config, new Vector2(2, 3), initial.Position));
                Success(query.Result); Assert.That(query.HasHit, Is.True); Assert.That(query.Hit.MaterialId, Is.EqualTo(initial.MaterialId));
                if (document.Data.IsInitiallyBurning(initial.Position))
                {
                    document.Data.Materials.TryGet(initial.MaterialId, out var material);
                    Assert.That(query.Hit.State.IsBurning, Is.True); Assert.That(query.Hit.State.IgnitedTick, Is.Zero);
                    Assert.That(query.Hit.State.FuelTicksRemaining, Is.EqualTo(material.Parameters.FuelTicks));
                }
            }
            Assert.That(Text(document.Data), Is.EqualTo(before));
        }

        [Test] public void M08_07_SingleUnfixedPixelExtractsAndActuallyMoves()
        {
            var data = Load(M06Sources.Create(Array.Empty<InitialCell>(), gravity: -1));
            Success(data.Apply(new[] { new Vector2Int(20, 20) }, SceneEditOperation.Paint, 104)); string before = Text(data);
            _session = new SceneEditorSession(); Success(_session.BeginTrial(data, Vector2.zero, false));
            var world = _session.Host.World; var point = world.QueryPoint(new Vector2(2.05f, 2.05f));
            Success(point.Result); Assert.That(point.Hit.Position.OwnerKind, Is.EqualTo(OwnerKind.Body));
            var runtime = ((SimulationWorld)world).Runtime; BodySnapshot original = runtime.State.Bodies[0];
            for (int tick = 0; tick < 10; tick++) Success(_session.Host.Step().Result);
            var moved = runtime.State.Bodies[0]; Assert.That(moved.Pose.Position.y, Is.LessThan(original.Pose.Position.y));
            var hit = world.QueryPoint(moved.Pose.Position + Vector2.one * 0.05f); Success(hit.Result);
            Assert.That(hit.Hit.Position.BodyId, Is.EqualTo(original.BodyId));
            Success(_session.EndTrial()); Assert.That(Text(data), Is.EqualTo(before)); Assert.That(data.IsFixed(new Vector2Int(20, 20)), Is.False);
        }

        [Test] public void M08_07_FourArmsUseFormalCommandStructurePhysicsAndQuery()
        {
            var fixture = FixtureCatalog.Cross();
            var source = M06Sources.Create(fixture.Scene.Cells, fixture.Scene.FixedCells, gravity: -1);
            var data = Load(source); string before = Text(data);
            _session = new SceneEditorSession(); Success(_session.BeginTrial(data, Vector2.zero, false)); var world = _session.Host.World;
            var queued = world.Enqueue(new MaterialCommand(MaterialOperation.Remove, FixtureCatalog.CellRegion(4, 4), 0, world.Version.Generation)); Success(queued.Result);
            Success(_session.Host.Step().Result); Success(world.Retry(queued.Token).Result);
            var runtime = ((SimulationWorld)world).Runtime; Assert.That(runtime.State.Bodies.Length, Is.EqualTo(4));
            BodySnapshot[] bodies = runtime.State.Bodies.ToArray();
            for (int tick = 0; tick < 4; tick++) Success(_session.Host.Step().Result);
            Assert.That(runtime.State.Bodies[0].Pose.Position.y, Is.LessThan(bodies[0].Pose.Position.y));
            Assert.That(world.QueryRegion(GridSelection.Bounds(world.Config, Vector2.zero), new CellHit[8]).RequiredCount, Is.EqualTo(8));
            foreach (var key in runtime.View.OccupiedCells)
            {
                Assert.That(key.Position.OwnerKind, Is.EqualTo(OwnerKind.Body));
                var body = runtime.View.Bodies.ToArray().First(b => b.BodyId == key.Position.BodyId);
                Vector2 local = new Vector2((key.Position.X + 0.5f) * 0.1f, (key.Position.Y + 0.5f) * 0.1f);
                float c = Mathf.Cos(body.Pose.AngleRadians), s = Mathf.Sin(body.Pose.AngleRadians);
                var hit = world.QueryPoint(body.Pose.Position + new Vector2(c * local.x - s * local.y, s * local.x + c * local.y));
                Success(hit.Result); Assert.That(hit.Hit.Position, Is.EqualTo(key.Position));
            }
            Success(_session.EndTrial()); Assert.That(Text(data), Is.EqualTo(before));
        }

        [UnityTest] public IEnumerator M08_08_TwentyTrialCyclesReleaseWorldDisplayCameraAndPhysics()
        {
            var data = Load(M06Sources.Create(new[] { new InitialCell(20, 20, 104) }));
            int physicsBefore = PhysicsScenes(); int textures = Count<Texture2D>("OpenOita材料纹素");
            int materials = Count<Material>("OpenOita材料批次") + Count<Material>("OpenOita共享火焰");
            int meshes = Count<Mesh>("OpenOita共享块四边形") + Count<Mesh>("OpenOita火焰批次");
            int roots = Count<GameObject>("OpenOita正式显示");
            for (int round = 0; round < 20; round++)
            {
                _session = new SceneEditorSession(); Success(_session.Render(data, Vector2.zero, 128, 128, 1, default));
                Success(_session.BeginTrial(data, Vector2.zero, false)); Success(_session.Host.Step().Result);
                Success(_session.Render(data, Vector2.zero, 384, 384, 3, default));
                Success(_session.Host.ResetWorld()); Success(_session.EndTrial()); _session.Dispose(); _session = null;
                yield return null; yield return null;
                Assert.That(PhysicsScenes(), Is.EqualTo(physicsBefore));
                Assert.That(Count<GameObject>("OpenOita编辑器正式试玩"), Is.Zero); Assert.That(Count<GameObject>("OpenOita编辑像素摄像机"), Is.Zero);
                Assert.That(Count<RenderTexture>("OpenOita编辑像素输出"), Is.Zero);
                Assert.That(Count<Texture2D>("OpenOita材料纹素"), Is.EqualTo(textures));
                Assert.That(Count<Material>("OpenOita材料批次") + Count<Material>("OpenOita共享火焰"), Is.EqualTo(materials));
                Assert.That(Count<Mesh>("OpenOita共享块四边形") + Count<Mesh>("OpenOita火焰批次"), Is.EqualTo(meshes));
                Assert.That(Count<GameObject>("OpenOita正式显示"), Is.EqualTo(roots));
            }
        }

        [Test] public void M08_03_HostRejectsRotationAndScaleWithoutWorld()
        {
            var go = new GameObject("M08非法Host变换"); go.SetActive(false); var host = go.AddComponent<WorldHost>();
            try
            {
                go.transform.rotation = Quaternion.Euler(0, 0, 1);
                Assert.That(host.CreateWorld(new WorldSimulation(), BaselineSources.Read(), Vector2.zero, false).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                go.transform.rotation = Quaternion.identity; go.transform.localScale = new Vector3(2, 1, 1);
                Assert.That(host.CreateWorld(new WorldSimulation(), BaselineSources.Read(), Vector2.zero, false).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
                Assert.That(host.World, Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test] public void M08_09_Custom129x65SizeSurvivesTrialAndFileRoundTrip()
        {
            WorldSources original = M06Sources.Create(new[] { new InitialCell(128, 64, 104) }, new[] { new Vector2Int(128, 64) });
            var load = new WorldSourceLoader().Load(original);
            var config = new WorldConfig(1, 129, 65, 128, 0.2f, load.Config.StepSeconds, 0, load.Config.Seed, load.Config.Limits);
            Success(ConfigurationSerializer.Serialize(config, load.Scene, load.Materials, out var source));
            string directory = DirectoryFor(source); var doc = new SceneEditingDocument(); Success(doc.LoadDirectory(directory)); string before = Text(doc.Data);
            _session = new SceneEditorSession();
            foreach (int scale in new[] { 1, 3, 1 }) Success(_session.Render(doc.Data, new Vector2(2, 3), 768, 768, scale, default));
            Success(_session.BeginTrial(doc.Data, new Vector2(2, 3), false));
            Assert.That(_session.Host.World.Config.Width, Is.EqualTo(129)); Assert.That(_session.Host.World.Config.Height, Is.EqualTo(65));
            Assert.That(_session.Host.World.Config.CellSize, Is.EqualTo(0.2f));
            Success(_session.Host.Step().Result); Success(doc.Save(directory)); Success(doc.LoadDirectory(directory));
            Assert.That(Text(doc.Data), Is.EqualTo(before)); Assert.That(doc.Data.Config.CellSize, Is.EqualTo(0.2f));
        }

        [UnityTest] public IEnumerator M08_09_ActualEditorPreviewAndFormalTrialGpuPixels()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                Assert.Ignore("需要可渲染Editor真实GPU输出。");
            var cells = Enumerable.Range(0, 64).Select(i => new InitialCell(i % 8, i / 8, 104)).ToArray();
            var data = Load(M06Sources.Create(cells, new[] { Vector2Int.zero }));
            _session = new SceneEditorSession();
            foreach (bool trial in new[] { false, true })
            {
                if (trial) Success(_session.BeginTrial(data, Vector2.zero, false));
                foreach (int scale in new[] { 1, 3 })
                {
                    Success(_session.Render(data, Vector2.zero, 768, 768, scale, default)); yield return null;
                    Success(_session.Render(data, Vector2.zero, 768, 768, scale, default));
                    RenderTexture previous = RenderTexture.active; Texture2D capture = null;
                    try
                    {
                        RenderTexture.active = _session.Output;
                        capture = new Texture2D(768, 768, TextureFormat.RGBA32, false); capture.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); capture.Apply();
                        Color32[] pixels = capture.GetPixels32(); Color32 color = pixels[0]; Assert.That(color.r, Is.GreaterThan(40));
                        for (int y = 0; y < 8 * scale; y++) for (int x = 0; x < 8 * scale; x++) Assert.That(pixels[y * 768 + x], Is.EqualTo(color));
                        Assert.That(pixels[8 * scale], Is.Not.EqualTo(color)); Assert.That(pixels[8 * scale * 768], Is.Not.EqualTo(color));
                        string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "docs", "validation", "M08"); Directory.CreateDirectory(folder);
                        File.WriteAllBytes(Path.Combine(folder, (trial ? "正式试玩" : "编辑初态") + "-" + scale + "倍.png"), capture.EncodeToPNG());
                        TestContext.WriteLine($"{(trial ? "正式试玩" : "编辑初态")} 原始输出768×768，{scale}倍，64状态覆盖{8 * scale}×{8 * scale}实际像素。");
                    }
                    finally { RenderTexture.active = previous; if (capture != null) UnityEngine.Object.DestroyImmediate(capture); }
                }
            }
        }

        private static int Count<T>(string name) where T : UnityEngine.Object => Resources.FindObjectsOfTypeAll<T>().Count(o => o.name == name);
        private static int PhysicsScenes()
        { int count = 0; for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).name.StartsWith("OpenOitaPhysics-", StringComparison.Ordinal)) count++; return count; }
    }
}
#endif
