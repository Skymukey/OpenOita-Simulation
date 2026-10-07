using System;
using System.IO;
using OpenOita.Contracts;
using UnityEngine;

namespace OpenOita.Data
{
    // M08 文件协调层；加载、校验、序列化及原子发布均交给M01。
    public sealed class SceneEditingDocument
    {
        private readonly ConfigurationFileStore _store;
        public SceneMaterialData Data { get; private set; }
        public SceneEditingDocument(ConfigurationFileStore store = null) { _store = store ?? new ConfigurationFileStore(); }
        public void Clear() { Data = null; }

        public WorldResult LoadDirectory(string directory)
        {
            WorldResult read = _store.ReadSources(directory, out WorldSources sources);
            return read.IsSuccess ? Load(sources) : read;
        }

        public WorldResult Load(WorldSources sources)
        {
            WorldResult result = SceneMaterialData.Load(sources, out SceneMaterialData candidate);
            if (result.IsSuccess) Data = candidate;
            return result;
        }

        public WorldResult CreateEmpty(WorldSources template, int width, int height, float cellSize)
        {
            WorldLoadResult loaded = new WorldSourceLoader().Load(template);
            if (!loaded.Result.IsSuccess) return loaded.Result;
            WorldConfig old = loaded.Config;
            var config = new WorldConfig(old.SchemaVersion, width, height, old.ChunkSize, cellSize,
                old.StepSeconds, old.GravityY, old.Seed, old.Limits);
            var scene = new SceneInitialData(1, loaded.Materials.MaterialSetId, "materials.json", "world_config.json",
                Array.Empty<InitialCell>(), Array.Empty<Vector2Int>(), Array.Empty<Vector2Int>());
            WorldResult result = ConfigurationSerializer.Serialize(config, scene, loaded.Materials, out WorldSources sources);
            return result.IsSuccess ? Load(sources) : result;
        }

        // 新建/另存显式创建独立目录；scene最后发布。失败保留可诊断文件，不批量清理。
        public WorldResult SaveNewDirectory(string directory)
        {
            if (Data == null) return WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("另存", "scene.json", "请先创建或加载场景。"));
            WorldResult exported = Data.Export(out WorldSources sources);
            if (!exported.IsSuccess) return exported;
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("请选择新场景目录。");
                string fullPath = Path.GetFullPath(directory);
                if (Directory.Exists(fullPath) || File.Exists(fullPath))
                    return WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("另存", "directory", "目录已存在，请使用新的场景名称；已有场景请加载后保存。", fullPath));
                Directory.CreateDirectory(fullPath);
                WorldResult result = _store.WriteAtomically(fullPath, "materials.json", sources.MaterialsText);
                if (result.IsSuccess) result = _store.WriteAtomically(fullPath, "world_config.json", sources.WorldConfigText);
                if (result.IsSuccess) result = _store.WriteAtomically(fullPath, "scene.json", sources.SceneText);
                return result;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return WorldResult.Failure(WorldErrorCode.InvalidConfig, new WorldDiagnostic("另存", "directory", ex.Message, directory));
            }
        }

        public WorldResult Save(string directory)
        {
            if (Data == null) return WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("保存", "scene.json", "请先加载有效初态。"));
            WorldResult exported = Data.Export(out WorldSources sources);
            if (!exported.IsSuccess) return exported;
            // 首次保存也校验目标目录的两个引用文件；不读旧scene、不改材料或世界参数。
            WorldResult read = _store.ReadSourcesWithScene(directory, sources.SceneText, out WorldSources target);
            if (!read.IsSuccess) return read;
            WorldLoadResult loaded = new WorldSourceLoader().Load(target);
            if (!loaded.Result.IsSuccess) return loaded.Result;
            WorldResult normalized = ConfigurationSerializer.Serialize(loaded.Config, loaded.Scene, loaded.Materials, out WorldSources canonical);
            if (!normalized.IsSuccess) return normalized;
            if (canonical.MaterialsText != sources.MaterialsText || canonical.WorldConfigText != sources.WorldConfigText)
                return WorldResult.Failure(WorldErrorCode.InvalidConfig, new WorldDiagnostic("保存预检", "materials.json/world_config.json", "目标目录材料或世界参数与编辑初态不一致，请使用匹配的引用文件。", directory));
            return _store.SaveScene(directory, Data.Config, Data.Snapshot(), Data.Materials);
        }
    }
}
