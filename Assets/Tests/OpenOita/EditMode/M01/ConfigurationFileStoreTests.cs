using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.M01
{
    public sealed class ConfigurationFileStoreTests
    {
        // 保留忽略目录内的证据，不执行目录清理或批量删除。
        private static string NewDirectory()
        {
            string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "M01", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); return folder;
        }

        [Test]
        public void M01_05_ThreeFileSerializationIsDeterministicAndPreservesInitialSemantics()
        {
            WorldSources input = BaselineSources.Read(); WorldLoadResult loaded = new WorldSourceLoader().Load(input);
            var reversed = new SceneInitialData(loaded.Scene.SchemaVersion, loaded.Scene.MaterialSetId, loaded.Scene.MaterialsFile,
                loaded.Scene.WorldConfigFile, loaded.Scene.Cells.Reverse(), loaded.Scene.FixedCells.Reverse(), loaded.Scene.InitialBurning.Reverse());
            Assert.That(ConfigurationSerializer.Serialize(loaded.Config, reversed, loaded.Materials, out WorldSources serialized).IsSuccess, Is.True);
            WorldLoadResult restored = new WorldSourceLoader().Load(serialized);
            Assert.That(restored.Result.IsSuccess, Is.True, restored.Result.Diagnostic.Message);
            Assert.That(restored.Scene.Cells, Is.EqualTo(loaded.Scene.Cells));
            Assert.That(restored.Scene.FixedCells, Is.EqualTo(loaded.Scene.FixedCells));
            Assert.That(restored.Scene.InitialBurning, Is.EqualTo(loaded.Scene.InitialBurning));
            Assert.That(JToken.DeepEquals(JObject.Parse(input.SceneText), JObject.Parse(serialized.SceneText)), Is.True);
            for (ushort id = 101; id <= 104; id++)
            {
                loaded.Materials.TryGet(id, out var before); restored.Materials.TryGet(id, out var after);
                Assert.That(after, Is.EqualTo(before));
            }
            Assert.That(ConfigurationSerializer.Serialize(restored.Config, restored.Scene, restored.Materials, out var twice).IsSuccess, Is.True);
            Assert.That(twice.MaterialsText, Is.EqualTo(serialized.MaterialsText));
            Assert.That(twice.WorldConfigText, Is.EqualTo(serialized.WorldConfigText));
            Assert.That(twice.SceneText, Is.EqualTo(serialized.SceneText));
            Assert.That(serialized.SceneText, Does.Not.Contain("BodyId").And.Not.Contain("fuelTicks").And.Not.Contain("velocity"));
        }

        [Test]
        public void M01_05_SerializationRejectsInvalidEditorInitialStateBeforeWriting()
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(BaselineSources.Read());
            InitialCell first = loaded.Scene.Cells[0];
            var duplicate = new SceneInitialData(1, loaded.Scene.MaterialSetId, "materials.json", "world_config.json",
                new[] { first, new InitialCell(first.Position.x, first.Position.y, 104) }, Array.Empty<Vector2Int>(), Array.Empty<Vector2Int>());
            string directory = NewDirectory(), path = Path.Combine(directory, "scene.json"); File.WriteAllText(path, "原文件");
            Assert.That(new ConfigurationFileStore().SaveScene(directory, loaded.Config, duplicate, loaded.Materials).IsSuccess, Is.False);
            Assert.That(File.ReadAllText(path), Is.EqualTo("原文件"));
            Assert.That(ConfigurationSerializer.Serialize(loaded.Config, duplicate, loaded.Materials, out var output).IsSuccess, Is.False);
            Assert.That(output, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void M01_05_TemporaryWriteAndReplacementFailuresKeepOriginalTarget(bool replacementFailure)
        {
            string directory = NewDirectory(), target = Path.Combine(directory, "scene.json");
            File.WriteAllText(target, "原文件完整内容"); string observedTemporary = null;
            var store = new ConfigurationFileStore(WorldSourceLoader.DefaultMemoryBudgetBytes,
                (path, text) =>
                {
                    observedTemporary = path; File.WriteAllText(path, replacementFailure ? text : "部分写入");
                    if (!replacementFailure) throw new IOException("注入临时写入失败");
                },
                (temporary, destination) => { throw new IOException("注入目标替换失败"); });
            WorldResult result = store.WriteAtomically(directory, "scene.json", "新文件");
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Diagnostic.Stage, Is.EqualTo(replacementFailure ? "目标替换" : "临时写入"));
            Assert.That(result.Diagnostic.FileName, Is.EqualTo(target));
            Assert.That(Path.GetDirectoryName(observedTemporary), Is.EqualTo(directory));
            Assert.That(File.ReadAllText(target), Is.EqualTo("原文件完整内容"));
            Assert.That(File.Exists(observedTemporary), Is.False);
        }

        [Test]
        public void M01_05_ExplicitDirectoryReadAndAtomicCreateReplaceUseSameLoader()
        {
            string directory = NewDirectory(); var store = new ConfigurationFileStore(); var input = BaselineSources.Read();
            Assert.That(store.WriteAtomically(directory, "materials.json", input.MaterialsText).IsSuccess, Is.True);
            Assert.That(store.WriteAtomically(directory, "world_config.json", input.WorldConfigText).IsSuccess, Is.True);
            Assert.That(store.WriteAtomically(directory, "scene.json", "原目标").IsSuccess, Is.True);
            WorldLoadResult loaded = new WorldSourceLoader().Load(input);
            Assert.That(store.SaveScene(directory, loaded.Config, loaded.Scene, loaded.Materials).IsSuccess, Is.True);
            WorldLoadResult fromDisk = store.LoadDirectory(directory);
            Assert.That(fromDisk.Result.IsSuccess, Is.True, fromDisk.Result.Diagnostic.Message);
            Assert.That(fromDisk.Scene.Cells.Count, Is.EqualTo(175));
            Assert.That(store.ReadSources(directory, out var read).IsSuccess, Is.True);
            Assert.That(read.MaterialsFileName, Is.EqualTo(Path.Combine(directory, "materials.json")));
            Assert.That(store.WriteAtomically(directory, "../scene.json", "{}").ErrorCode, Is.EqualTo(WorldErrorCode.InvalidConfig));
            File.WriteAllText(Path.Combine(directory, "world_config.json"), "坏输入");
            WorldLoadResult bad = store.LoadDirectory(directory);
            Assert.That(bad.Result.IsSuccess, Is.False); Assert.That(bad.Materials, Is.Null);
            Assert.That(bad.Result.Diagnostic.FileName, Is.EqualTo(Path.Combine(directory, "world_config.json")));
        }

        [Test]
        public void M01_06_ReadFailureAndLowInputBudgetDoNotPublishPartialSources()
        {
            string directory = NewDirectory(); var store = new ConfigurationFileStore(); var input = BaselineSources.Read();
            File.WriteAllText(Path.Combine(directory, "materials.json"), input.MaterialsText);
            Assert.That(store.ReadSources(directory, out var absent).IsSuccess, Is.False); Assert.That(absent, Is.Null);
            Assert.That(new ConfigurationFileStore(1024).ReadSources(directory, out var low).ErrorCode, Is.EqualTo(WorldErrorCode.CapacityExceeded));
            Assert.That(low, Is.Null);
        }

        [Test]
        public void M01_01_StreamingAssetsDeploymentMatchesBaseline()
        {
            WorldLoadResult result = new ConfigurationFileStore().LoadDirectory(Path.Combine(Application.streamingAssetsPath, "OpenOita"));
            Assert.That(result.Result.IsSuccess, Is.True, result.Result.Diagnostic.Message);
            Assert.That(result.Scene.Cells.Count, Is.EqualTo(175));
            Assert.That(result.Scene.FixedCells.Count, Is.EqualTo(2));
            Assert.That(result.Scene.InitialBurning.Count, Is.EqualTo(1));
        }
    }
}
