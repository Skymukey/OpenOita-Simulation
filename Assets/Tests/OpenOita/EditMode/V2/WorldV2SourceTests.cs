using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.Tests.Fixtures;
using UnityEngine;

namespace OpenOita.Tests.EditMode.V2
{
    public sealed class WorldV2SourceTests
    {
        [Test]
        public void V2LoaderAcceptsV2SourcesAndUsesFixedRuntimeChunkSize()
        {
            WorldSources v2 = Convert(BaselineSources.Read());
            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(v2);

            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Assert.That(loaded.Config.SchemaVersion, Is.EqualTo(2));
            Assert.That(loaded.Config.ChunkSize, Is.EqualTo(32));
            Assert.That(loaded.Scene.SchemaVersion, Is.EqualTo(2));
            Assert.That(loaded.Materials.Count, Is.EqualTo(4));
            Assert.That(loaded.Scene.Cells.Count, Is.EqualTo(175));
        }

        [Test]
        public void V2LoaderDoesNotRejectLargeCapacityEstimate()
        {
            WorldSources v2 = Convert(BaselineSources.Read());
            JObject world = JObject.Parse(v2.WorldConfigText);
            world["limits"]["maxMaterialCells"] = 8000000;

            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(new WorldSources(
                v2.MaterialsText, world.ToString(), v2.SceneText));

            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Assert.That(loaded.Config.Limits.MaxMaterialCells, Is.EqualTo(8000000));
        }

        [Test]
        public void V2LoaderRejectsV1AndRejectsWorldChunkSize()
        {
            WorldSources v1 = BaselineSources.Read();
            WorldLoadResult oldVersion = new WorldSourceLoaderV2().Load(v1);
            Assert.That(oldVersion.Result.ErrorCode, Is.EqualTo(WorldErrorCode.UnsupportedVersion));
            Assert.That(oldVersion.Config, Is.Null);

            WorldSources v2 = Convert(v1);
            JObject world = JObject.Parse(v2.WorldConfigText);
            world["chunkSize"] = 128;
            WorldLoadResult chunked = new WorldSourceLoaderV2().Load(new WorldSources(
                v2.MaterialsText, world.ToString(), v2.SceneText));
            Assert.That(chunked.Result.IsSuccess, Is.False);
            Assert.That(chunked.Result.ErrorCode, Is.EqualTo(WorldErrorCode.InvalidConfig));
            Assert.That(chunked.Config, Is.Null);
        }

        [Test]
        public void ConverterChangesOnlyVersionsAndRemovesWorldChunkSize()
        {
            WorldSources original = BaselineSources.Read();
            WorldResult result = WorldV2SourceConverter.Convert(original, out WorldSources converted);

            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            JObject oldMaterials = JObject.Parse(original.MaterialsText);
            JObject oldWorld = JObject.Parse(original.WorldConfigText);
            JObject oldScene = JObject.Parse(original.SceneText);
            JObject newMaterials = JObject.Parse(converted.MaterialsText);
            JObject newWorld = JObject.Parse(converted.WorldConfigText);
            JObject newScene = JObject.Parse(converted.SceneText);
            Assert.That((int)newMaterials["schemaVersion"], Is.EqualTo(2));
            Assert.That((int)newScene["schemaVersion"], Is.EqualTo(2));
            Assert.That((int)newWorld["schemaVersion"], Is.EqualTo(2));
            Assert.That(newWorld["chunkSize"], Is.Null);
            Assert.That(JToken.DeepEquals(newWorld["width"], oldWorld["width"]), Is.True);
            Assert.That(JToken.DeepEquals(newWorld["height"], oldWorld["height"]), Is.True);
            Assert.That(JToken.DeepEquals(newMaterials["materials"], oldMaterials["materials"]), Is.True);
            Assert.That(JToken.DeepEquals(newScene["cells"], oldScene["cells"]), Is.True);
            Assert.That(JToken.DeepEquals(newScene["fixedCells"], oldScene["fixedCells"]), Is.True);
            Assert.That(JToken.DeepEquals(newScene["initialBurning"], oldScene["initialBurning"]), Is.True);
            Assert.That(new WorldSourceLoaderV2().Load(converted).Result.IsSuccess, Is.True);
        }

        [Test]
        public void V2EditorDataPreservesVersionAcrossEditExportAndReload()
        {
            SceneMaterialData.Load(Convert(BaselineSources.Read()), out SceneMaterialData data);
            Assert.That(data, Is.Not.Null);
            Assert.That(data.Apply(new[] { new Vector2Int(255, 255) }, SceneEditOperation.Paint, 102).IsSuccess, Is.True);

            WorldResult exported = data.Export(out WorldSources saved);
            Assert.That(exported.IsSuccess, Is.True, exported.Diagnostic.Message);
            JObject world = JObject.Parse(saved.WorldConfigText);
            JObject scene = JObject.Parse(saved.SceneText);
            Assert.That((int)world["schemaVersion"], Is.EqualTo(2));
            Assert.That(world["chunkSize"], Is.Null);
            Assert.That((int)scene["schemaVersion"], Is.EqualTo(2));

            WorldResult reloaded = SceneMaterialData.Load(saved, out SceneMaterialData restored);
            Assert.That(reloaded.IsSuccess, Is.True, reloaded.Diagnostic.Message);
            Assert.That(restored.Config.ChunkSize, Is.EqualTo(32));
            Assert.That(restored.MaterialAt(new Vector2Int(255, 255)), Is.EqualTo((ushort)102));
        }

        [Test]
        public void V2MapAssetReplaceAndBuildEditingDataRetainV2Sources()
        {
            var asset = ScriptableObject.CreateInstance<OpenOitaMapAsset>();
            try
            {
                WorldResult replaced = asset.ReplaceSources(Convert(BaselineSources.Read()));
                Assert.That(replaced.IsSuccess, Is.True, replaced.Diagnostic.Message);
                Assert.That((int)JObject.Parse(asset.Sources.WorldConfigText)["schemaVersion"], Is.EqualTo(2));
                Assert.That(JObject.Parse(asset.Sources.WorldConfigText)["chunkSize"], Is.Null);

                WorldResult loaded = asset.BuildEditingData(out SceneMaterialData data);
                Assert.That(loaded.IsSuccess, Is.True, loaded.Diagnostic.Message);
                Assert.That(data.Config.SchemaVersion, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void V2CreateEmptyDrawSaveAsSaveAndLoadDirectoryPreservesSchema2()
        {
            WorldSources template = Convert(BaselineSources.Read());
            var document = new SceneEditingDocument();
            Assert.That(document.CreateEmpty(template, 33, 17, 0.2f).IsSuccess, Is.True);

            Assert.That(document.Data.Apply(new[] { new Vector2Int(1, 1), new Vector2Int(31, 16) },
                SceneEditOperation.Paint, 104).IsSuccess, Is.True);

            // 留在Logs下供失败诊断；按测试约束不批量删除临时目录。
            string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "V2SceneEditing",
                Guid.NewGuid().ToString("N"));
            Assert.That(document.SaveNewDirectory(directory).IsSuccess, Is.True);
            AssertV2Files(directory);

            Assert.That(document.Data.Apply(new[] { new Vector2Int(2, 2) }, SceneEditOperation.Paint, 104).IsSuccess, Is.True);
            Assert.That(document.Save(directory).IsSuccess, Is.True);
            AssertV2Files(directory);

            WorldLoadResult loaded = new ConfigurationFileStore().LoadDirectory(directory);
            Assert.That(loaded.Result.IsSuccess, Is.True, loaded.Result.Diagnostic.Message);
            Assert.That(loaded.Config.SchemaVersion, Is.EqualTo(2));
            Assert.That(loaded.Config.ChunkSize, Is.EqualTo(32));
            Assert.That(loaded.Scene.Cells.Count, Is.EqualTo(3));

            var restored = new SceneEditingDocument();
            Assert.That(restored.LoadDirectory(directory).IsSuccess, Is.True);
            Assert.That(restored.Data.Config.SchemaVersion, Is.EqualTo(2));
            Assert.That(restored.Data.Config.ChunkSize, Is.EqualTo(32));
            Assert.That(restored.Data.Count, Is.EqualTo(3));
            Assert.That(restored.Data.MaterialAt(new Vector2Int(2, 2)), Is.EqualTo((ushort)104));
        }

        private static void AssertV2Files(string directory)
        {
            JObject materials = JObject.Parse(File.ReadAllText(Path.Combine(directory, "materials.json")));
            JObject world = JObject.Parse(File.ReadAllText(Path.Combine(directory, "world_config.json")));
            JObject scene = JObject.Parse(File.ReadAllText(Path.Combine(directory, "scene.json")));
            Assert.That((int)materials["schemaVersion"], Is.EqualTo(2));
            Assert.That((int)world["schemaVersion"], Is.EqualTo(2));
            Assert.That(world.Property("chunkSize"), Is.Null);
            Assert.That((int)scene["schemaVersion"], Is.EqualTo(2));
        }

        private static WorldSources Convert(WorldSources v1)
        {
            WorldResult result = WorldV2SourceConverter.Convert(v1, out WorldSources v2);
            Assert.That(result.IsSuccess, Is.True, result.Diagnostic.Message);
            return v2;
        }
    }
}
