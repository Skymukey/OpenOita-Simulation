using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Editor;
using OpenOita.Tests.Fixtures;
using UnityEditor;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M08
{
    public sealed class SceneMaterialDataTests
    {
        private static void Success(WorldResult result) => Assert.That(result.IsSuccess, Is.True,
            result.ErrorCode + " / " + result.Diagnostic.Stage + " / " + result.Diagnostic.Message);
        private static SceneMaterialData Load(WorldSources sources)
        { Success(SceneMaterialData.Load(sources, out var data)); return data; }
        private static string Text(SceneMaterialData data) { Success(data.Export(out var sources)); return sources.SceneText; }
        private static string DirectoryFor(WorldSources sources, bool scene = true)
        {
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "M08", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "materials.json"), sources.MaterialsText);
            File.WriteAllText(Path.Combine(directory, "world_config.json"), sources.WorldConfigText);
            if (scene) File.WriteAllText(Path.Combine(directory, "scene.json"), sources.SceneText);
            return directory;
        }

        [Test] public void M08_01_FileRoundTripPreserves175CellsAndReferences()
        {
            WorldSources input = BaselineSources.Read(); string directory = DirectoryFor(input);
            var doc = new SceneEditingDocument(); Success(doc.LoadDirectory(directory));
            var before = doc.Data.Snapshot(); Assert.That(before.Cells.Count, Is.EqualTo(175));
            string materialBytes = File.ReadAllText(Path.Combine(directory, "materials.json"));
            string configBytes = File.ReadAllText(Path.Combine(directory, "world_config.json"));
            Success(doc.Save(directory)); Success(doc.LoadDirectory(directory));
            Assert.That(doc.Data.Snapshot().Cells, Is.EqualTo(before.Cells));
            Assert.That(doc.Data.Snapshot().FixedCells, Is.EqualTo(before.FixedCells));
            Assert.That(doc.Data.Snapshot().InitialBurning, Is.EqualTo(before.InitialBurning));
            Assert.That(File.ReadAllText(Path.Combine(directory, "materials.json")), Is.EqualTo(materialBytes));
            Assert.That(File.ReadAllText(Path.Combine(directory, "world_config.json")), Is.EqualTo(configBytes));
            string text = Text(doc.Data);
            Assert.That(text, Does.Not.Contain("BodyId").And.Not.Contain("fuelTicks").And.Not.Contain("velocity"));
            Assert.That(JToken.DeepEquals(JObject.Parse(input.SceneText), JObject.Parse(text)), Is.True);
        }

        [TestCase((ushort)101)] [TestCase((ushort)102)] [TestCase((ushort)103)] [TestCase((ushort)104)]
        public void M08_02_AllValidatedMaterialsSingleRectangleAndErase(ushort id)
        {
            var data = Load(M06Sources.Create(Array.Empty<InitialCell>())); var p = new Vector2Int(127, 1);
            Success(data.Apply(new[] { p }, SceneEditOperation.Paint, id));
            Assert.That(data.MaterialAt(p), Is.EqualTo(id));
            Success(GridSelection.Region(data.Config, Vector2.zero, new WorldRect(new Vector2(12.7f, 0.1f), new Vector2(12.9f, 0.3f)), out var cells));
            Assert.That(cells.Length, Is.EqualTo(4)); Success(data.Apply(cells, SceneEditOperation.Paint, id));
            Assert.That(data.Count, Is.EqualTo(4)); Success(data.Apply(cells, SceneEditOperation.Erase)); Assert.That(data.Count, Is.Zero);
        }

        [Test] public void M08_02_OverwriteAndEraseClearOldMarkersEvenSameId()
        {
            var p = new Vector2Int(5, 5); var data = Load(M06Sources.Create(new[] { new InitialCell(5, 5, 104) }, new[] { p }, new[] { p }));
            var frozen = data.Snapshot();
            Success(data.Apply(new[] { p }, SceneEditOperation.Paint, 104));
            Assert.That(data.IsFixed(p), Is.False); Assert.That(data.IsInitiallyBurning(p), Is.False);
            Success(data.Apply(new[] { p }, SceneEditOperation.Fixed)); Success(data.Apply(new[] { p }, SceneEditOperation.InitialBurning));
            Success(data.Apply(new[] { p }, SceneEditOperation.Fixed, mark: false));
            Success(data.Apply(new[] { p }, SceneEditOperation.InitialBurning, mark: false));
            Success(data.Apply(new[] { p }, SceneEditOperation.Erase)); Success(data.Apply(new[] { p }, SceneEditOperation.Paint, 104));
            Assert.That(data.IsFixed(p), Is.False); Assert.That(data.IsInitiallyBurning(p), Is.False);
            Success(data.Apply(new[] { p }, SceneEditOperation.Fixed)); Success(data.Apply(new[] { p }, SceneEditOperation.InitialBurning));
            Success(data.Apply(new[] { p }, SceneEditOperation.Paint, 102));
            Assert.That(data.IsFixed(p), Is.False); Assert.That(data.IsInitiallyBurning(p), Is.False);
            Assert.That(frozen.FixedCells, Is.EqualTo(new[] { p })); Assert.That(frozen.InitialBurning, Is.EqualTo(new[] { p }));
        }

        [Test] public void M08_02_IllegalMarksAndCapacityAreAtomic()
        {
            var data = Load(M06Sources.Create(new[] { new InitialCell(0, 0, 104), new InitialCell(1, 0, 101), new InitialCell(2, 0, 102) }));
            string before = Text(data);
            foreach (SceneEditOperation op in new[] { SceneEditOperation.Fixed, SceneEditOperation.InitialBurning })
                Assert.That(data.Apply(new[] { Vector2Int.zero, Vector2Int.right }, op).ErrorCode, Is.EqualTo(WorldErrorCode.IncompatibleRule));
            Assert.That(data.Apply(new[] { new Vector2Int(2, 0) }, SceneEditOperation.InitialBurning).IsSuccess, Is.False);
            Assert.That(data.Apply(new[] { Vector2Int.zero, new Vector2Int(256, 0) }, SceneEditOperation.Erase).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Assert.That(Text(data), Is.EqualTo(before));
            var sources = M06Sources.Create(Array.Empty<InitialCell>());
            var json = JObject.Parse(sources.WorldConfigText); json["limits"]["maxMaterialCells"] = 1;
            data = Load(new WorldSources(sources.MaterialsText, json.ToString(), sources.SceneText));
            Assert.That(data.Apply(new[] { Vector2Int.zero, Vector2Int.right }, SceneEditOperation.Paint, 104).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(data.Count, Is.Zero);
        }

        [Test] public void M08_03_OriginBoundariesCrossChunkTailAndSharedSelection()
        {
            var source = M06Sources.Create(Array.Empty<InitialCell>()); var json = JObject.Parse(source.WorldConfigText); json["width"] = 129;
            var data = Load(new WorldSources(source.MaterialsText, json.ToString(), source.SceneText)); var origin = new Vector2(2, 3);
            foreach (int x in new[] { 0, 1, 127, 128 })
            {
                var cell = new Vector2Int(x, 0);
                Success(GridSelection.Point(data.Config, origin, GridSelection.Center(data.Config, origin, cell), out var center)); Assert.That(center, Is.EqualTo(cell));
                Success(GridSelection.Point(data.Config, origin, origin + new Vector2(x * 0.1f, 0), out var edge)); Assert.That(edge, Is.EqualTo(cell));
            }
            Assert.That(GridSelection.Point(data.Config, origin, GridSelection.Bounds(data.Config, origin).Max, out _).ErrorCode, Is.EqualTo(WorldErrorCode.OutOfBounds));
            Success(GridSelection.Region(data.Config, origin, new WorldRect(origin + new Vector2(12.7f, 0), origin + new Vector2(12.9f, 0.1f)), out var selection));
            Assert.That(selection, Is.EqualTo(new[] { new Vector2Int(127, 0), new Vector2Int(128, 0) }));
            Success(data.Apply(selection, SceneEditOperation.Paint, 104)); Assert.That(data.Count, Is.EqualTo(2));
            Success(data.Apply(selection, SceneEditOperation.Erase)); Assert.That(data.Count, Is.Zero); Assert.That(data.MaterialAt(Vector2Int.zero), Is.Zero);
            Assert.That(GridSelection.Point(data.Config, origin, new Vector2(float.NaN, 3), out _).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidArgument));
        }

        [TestCase("MissingField")] [TestCase("DuplicateJsonKey")] [TestCase("UnknownMaterial")] [TestCase("WrongSet")]
        [TestCase("OutOfBounds")] [TestCase("DuplicateCell")] [TestCase("FixedWater")] [TestCase("BurningConcrete")] [TestCase("ArbitraryReference")]
        public void M08_04_FailedImportKeepsModelAndDisk(string invalid)
        {
            var original = BaselineSources.Read(); WorldSources bad;
            if (new[] { "DuplicateCell", "FixedWater", "BurningConcrete", "ArbitraryReference" }.Contains(invalid))
            {
                var scene = JObject.Parse(original.SceneText);
                if (invalid == "DuplicateCell") ((JArray)scene["cells"]).Add(scene["cells"][0].DeepClone());
                if (invalid == "ArbitraryReference") scene["materialsFile"] = "../materials.json";
                if (invalid == "FixedWater")
                { var water = ((JArray)scene["cells"]).First(c => (int)c["materialId"] == 101); ((JArray)scene["fixedCells"]).Add(new JObject { ["x"] = water["x"], ["y"] = water["y"] }); }
                if (invalid == "BurningConcrete")
                { var concrete = ((JArray)scene["cells"]).First(c => (int)c["materialId"] == 102); ((JArray)scene["initialBurning"]).Add(new JObject { ["x"] = concrete["x"], ["y"] = concrete["y"] }); }
                bad = new WorldSources(original.MaterialsText, original.WorldConfigText, scene.ToString());
            }
            else bad = BaselineSources.NegativeCopy(original, invalid);
            string directory = DirectoryFor(bad), disk = File.ReadAllText(Path.Combine(directory, "scene.json"));
            var doc = new SceneEditingDocument(); Success(doc.Load(original)); string before = Text(doc.Data); var model = doc.Data;
            WorldResult failed = doc.LoadDirectory(directory);
            Assert.That(failed.IsSuccess, Is.False); Assert.That(failed.Diagnostic.FileName, Is.Not.Null.And.Not.Empty);
            Assert.That(failed.Diagnostic.Target, Is.Not.Null.And.Not.Empty); Assert.That(failed.Diagnostic.Message, Is.Not.Null.And.Not.Empty);
            Assert.That(doc.Data, Is.SameAs(model)); Assert.That(Text(doc.Data), Is.EqualTo(before));
            Assert.That(File.ReadAllText(Path.Combine(directory, "scene.json")), Is.EqualTo(disk));
        }

        [TestCase("写入", false)] [TestCase("刷新关闭", false)] [TestCase("替换", false)]
        [TestCase("写入", true)] [TestCase("刷新关闭", true)] [TestCase("替换", true)]
        public void M08_05_AllSaveFaultsPreserveOldBytesAndFirstSave(string stage, bool first)
        {
            var source = BaselineSources.Read(); string directory = DirectoryFor(source, !first);
            string target = Path.Combine(directory, "scene.json"), temporary = null;
            byte[] before = first ? null : File.ReadAllBytes(target);
            var store = new ConfigurationFileStore(WorldSourceLoader.DefaultMemoryBudgetBytes,
                (path, text) =>
                {
                    temporary = path;
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    {
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(stage == "写入" ? "部分" : text);
                        stream.Write(bytes, 0, bytes.Length);
                        if (stage == "写入") throw new IOException("注入写入失败");
                        stream.Flush(true);
                        if (stage == "刷新关闭") throw new IOException("注入刷新/关闭失败");
                    }
                }, (tmp, path) => { Assert.That(stage, Is.EqualTo("替换")); throw new IOException("注入替换失败"); });
            var doc = new SceneEditingDocument(store); Success(doc.Load(source));
            WorldResult failed = doc.Save(directory); Assert.That(failed.IsSuccess, Is.False);
            Assert.That(failed.Diagnostic.Stage, Is.EqualTo(stage == "替换" ? "目标替换" : "临时写入"));
            Assert.That(failed.Diagnostic.Message, Does.Contain(temporary));
            if (first) Assert.That(File.Exists(target), Is.False); else Assert.That(File.ReadAllBytes(target), Is.EqualTo(before));
            Assert.That(File.Exists(temporary), Is.False);
        }

        [Test] public void M08_05_FirstSaveAndChangedReferencePrecheck()
        {
            var source = BaselineSources.Read(); string directory = DirectoryFor(source, false);
            var doc = new SceneEditingDocument(); Success(doc.Load(source)); Success(doc.Save(directory));
            string target = Path.Combine(directory, "scene.json"), before = File.ReadAllText(target);
            var config = JObject.Parse(source.WorldConfigText); config["cellSize"] = 0.2;
            File.WriteAllText(Path.Combine(directory, "world_config.json"), config.ToString());
            Assert.That(doc.Save(directory).ErrorCode, Is.EqualTo(WorldErrorCode.InvalidConfig));
            Assert.That(File.ReadAllText(target), Is.EqualTo(before));
        }

        [Test] public void M08_08_TwentyWindowAndPreviewResourceLifecycles()
        {
            var data = Load(M06Sources.Create(new[] { new InitialCell(5, 5, 104) }));
            int scenes = UnityEngine.SceneManagement.SceneManager.sceneCount;
            int[] texturesBefore = Resources.FindObjectsOfTypeAll<RenderTexture>().Where(t => t.name == "OpenOita编辑像素输出").Select(t => t.GetInstanceID()).ToArray();
            int[] objectsBefore = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "OpenOita编辑像素摄像机" || g.name == "OpenOita正式显示").Select(g => g.GetInstanceID()).ToArray();
            for (int round = 0; round < 20; round++)
            {
                var window = ScriptableObject.CreateInstance<SceneMaterialEditorWindow>(); window.Show(); window.Close();
                using (var session = new SceneEditorSession())
                {
                    Success(session.Render(data, Vector2.zero, 128, 128, 1, default));
                    Success(session.Render(data, Vector2.zero, 384, 384, 3, default));
                }
                Assert.That(Resources.FindObjectsOfTypeAll<RenderTexture>().Where(t => t.name == "OpenOita编辑像素输出").Select(t => t.GetInstanceID()), Is.EquivalentTo(texturesBefore));
                Assert.That(Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "OpenOita编辑像素摄像机" || g.name == "OpenOita正式显示").Select(g => g.GetInstanceID()), Is.EquivalentTo(objectsBefore));
                Assert.That(UnityEngine.SceneManagement.SceneManager.sceneCount, Is.EqualTo(scenes));
            }
        }

        [Test] public void M08_09_SinglePixelAndExplicit64PixelRectangleAreIndependent()
        {
            var data = Load(M06Sources.Create(Array.Empty<InitialCell>()));
            Success(GridSelection.Region(data.Config, default, new WorldRect(Vector2.zero, new Vector2(0.8f, 0.8f)), out var square));
            Assert.That(square.Length, Is.EqualTo(64)); Success(data.Apply(square, SceneEditOperation.Paint, 104));
            foreach (int scale in new[] { 1, 3 })
            {
                var p = new Vector2Int(2, 3);
                string before = Text(data);
                using (var session = new SceneEditorSession()) Success(session.Render(data, default, 256 * scale, 256 * scale, scale, default));
                Assert.That(Text(data), Is.EqualTo(before));
                Success(data.Apply(new[] { p }, SceneEditOperation.Fixed)); Success(data.Apply(new[] { p }, SceneEditOperation.InitialBurning));
                Assert.That(data.Snapshot().FixedCells.Count, Is.EqualTo(1)); Assert.That(data.Snapshot().InitialBurning.Count, Is.EqualTo(1));
                Success(data.Apply(new[] { p }, SceneEditOperation.Erase)); Assert.That(data.Count, Is.EqualTo(63));
                foreach (var cell in square) if (cell != p) Assert.That(data.MaterialAt(cell), Is.EqualTo(104));
                Success(data.Apply(new[] { p }, SceneEditOperation.Paint, 104));
                Assert.That(data.Count, Is.EqualTo(64)); Assert.That(data.Snapshot().FixedCells.Count, Is.Zero);
            }
        }

        [TestCase(1, 1f)] [TestCase(3, 1f)] [TestCase(1, 1.5f)] [TestCase(3, 2f)]
        public void M08_09_ActualScreenCoordinatesSelectOneCellAtIntegerScaleAndDpi(int scale, float dpi)
        {
            int pixels = 768; var rect = new Rect(10, 20, pixels / dpi, pixels / dpi); var pan = new Vector2Int(127, 3);
            Vector2 point = rect.position + new Vector2((2 * scale + 0.5f) / dpi, (pixels - 4 * scale - 0.5f) / dpi);
            Assert.That(PixelPreviewCoordinates.TryPoint(rect, point, dpi, pixels, scale, pan, out var cell), Is.True);
            Assert.That(cell, Is.EqualTo(new Vector2Int(129, 7)));
            Assert.That(PixelPreviewCoordinates.TryPoint(rect, rect.position, dpi, pixels, scale, default, out var top), Is.True);
            Assert.That(top, Is.EqualTo(new Vector2Int(0, (pixels - 1) / scale)));
            Assert.That(PixelPreviewCoordinates.TryPoint(rect, rect.max, dpi, pixels, scale, default, out _), Is.False);
        }
    }
}
