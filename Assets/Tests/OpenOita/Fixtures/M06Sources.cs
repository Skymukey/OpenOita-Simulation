using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpenOita.Contracts;
using OpenOita.Data;
using UnityEngine;

namespace OpenOita.Tests.Fixtures
{
    public static class M06Sources
    {
        public static WorldSources Create(IEnumerable<InitialCell> cells, IEnumerable<Vector2Int> fixedCells = null,
            IEnumerable<Vector2Int> burning = null, float gravity = 0, int maxChanges = 65536, int radius = 16, int substeps = 8, bool probeMasses = false,
            int maxMaterialCells = 65536, uint steamLifetimeTicks = 200)
        {
            WorldSources baseline = BaselineSources.Read();
            JObject json = BaselineSources.Parse(baseline.MaterialsText);
            foreach (JObject item in (JArray)json["materials"])
                if ((int)item["id"] == 103) item["ruleParameters"]["gas_drift"]["lifetimeTicks"] = steamLifetimeTicks;
            if (probeMasses) foreach (JObject item in (JArray)json["materials"])
            {
                if ((int)item["id"] == 102) item["massPerCell"] = 1;
                if ((int)item["id"] == 104) item["massPerCell"] = 10;
            }
            var loader = new WorldSourceLoader();
            WorldLoadResult loaded = loader.Load(new WorldSources(json.ToString(), baseline.WorldConfigText, baseline.SceneText));
            if (!loaded.Result.IsSuccess) throw new InvalidOperationException(loaded.Result.Diagnostic.Message);
            var limits = new WorldLimits(maxMaterialCells, 64, 256, 4096, maxChanges, 5, 180, substeps, radius);
            var config = new WorldConfig(1, 256, 256, 128, 0.1f, 0.02f, gravity, 1, limits);
            var scene = new SceneInitialData(1, loaded.Materials.MaterialSetId, "materials.json", "world_config.json",
                cells, fixedCells ?? Array.Empty<Vector2Int>(), burning ?? Array.Empty<Vector2Int>());
            WorldResult result = ConfigurationSerializer.Serialize(config, scene, loaded.Materials, out WorldSources sources);
            if (!result.IsSuccess) throw new InvalidOperationException(result.Diagnostic.Message);
            return sources;
        }
        // 只观察正式发布视图；不创建体、不推进物理、不做M07B显示验收。
        public sealed class CommitObserver : IWorldRenderer
        {
            public WorldVersion LastVersion;
            public int Commits;
            public int Disposals;
            public WorldResult Prepare(ICommittedRenderView initialView) { LastVersion = initialView.Version; return WorldResult.Success(); }
            public void OnCommitted(ICommittedRenderView view, in ChangeSet changes) { LastVersion = view.Version; Commits++; }
            public void FlushFrame() { }
            public void Dispose() { Disposals++; }
        }
    }
}
