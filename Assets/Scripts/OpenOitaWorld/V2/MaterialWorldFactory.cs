using OpenOita.Contracts;
using OpenOita.Data;
using OpenOita.V2.Render;
using UnityEngine;

namespace OpenOita.V2
{
    public sealed class MaterialWorldFactory : IWorldFactory
    {
        private readonly bool _freezeRotation;
        private readonly int _layer;
        private readonly V2RenderingResources _resources;
        public MaterialWorldFactory(V2RenderingResources resources = null, int layer = 0, bool freezeRotation = false)
        { _resources = resources; _layer = layer; _freezeRotation = freezeRotation; }

        public WorldCreateResult Create(WorldSources sources, Vector2 origin)
        {
            WorldLoadResult loaded = new WorldSourceLoaderV2().Load(sources);
            if (!loaded.Result.IsSuccess) return new WorldCreateResult(loaded.Result);
            if (!Application.isPlaying) return new WorldCreateResult(WorldResult.Failure(WorldErrorCode.NotReady, new WorldDiagnostic("V2", "PlayMode", "正式世界须进入PlayMode。")));
            if (!ContractDefaults.IsFinite(origin)) return new WorldCreateResult(WorldResult.Failure(WorldErrorCode.InvalidArgument, new WorldDiagnostic("V2", "origin", "原点必须有限。")));
            V2RenderingResources resources = _resources != null ? _resources : Resources.Load<V2RenderingResources>("OpenOita/V2Rendering");
            if (resources == null || !resources.IsComplete) return new WorldCreateResult(WorldResult.Failure(WorldErrorCode.NotReady,
                new WorldDiagnostic("V2", "rendering", "请先执行OpenOita/V2/装配显示资源与Renderer2D。")));
            var world = new MaterialWorld(loaded, origin, true, _freezeRotation);
            WorldResult result = world.Initialize();
            if (result.IsSuccess)
            {
                try { world.AttachDisplay(resources, _layer); return new WorldCreateResult(result, world); }
                catch (System.Exception exception) { result = WorldResult.Failure(WorldErrorCode.Faulted, new WorldDiagnostic("V2", "display", exception.Message)); }
            }
            world.Dispose(); return new WorldCreateResult(result);
        }
    }
}
