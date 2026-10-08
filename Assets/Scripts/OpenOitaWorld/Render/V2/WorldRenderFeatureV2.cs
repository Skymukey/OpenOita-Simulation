using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace OpenOita.V2.Render
{
    /// <summary>
    /// URP 17 RenderGraph injection point for the process-wide V2 renderer.
    /// The host owns registration and lifetime; the feature never advances simulation.
    /// </summary>
    public sealed class WorldRenderFeatureV2 : ScriptableRendererFeature
    {
        // Kept as ScriptableObject so this renderer feature remains usable while the Unity
        // generated project is being refreshed; the host owns the typed V2RenderingResources.
        [SerializeField] private ScriptableObject _resources;

        public ScriptableObject Resources => _resources;

        private sealed class V2Pass : ScriptableRenderPass
        {
            internal IncrementalWorldRenderer Renderer;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                Renderer?.RecordRenderGraph(renderGraph, frameData);
            }
        }

        private static IncrementalWorldRenderer _renderer;
        private V2Pass _pass;

        public static IncrementalWorldRenderer Renderer => _renderer;

        public static void Register(IncrementalWorldRenderer renderer)
        {
            _renderer = renderer;
        }

        public static void Unregister(IncrementalWorldRenderer renderer)
        {
            if (ReferenceEquals(_renderer, renderer)) _renderer = null;
        }

        public override void Create()
        {
            _pass = new V2Pass { renderPassEvent = RenderPassEvent.AfterRenderingTransparents };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (_pass == null || _renderer == null) return;
            _pass.Renderer = _renderer;
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            if (_pass != null) _pass.Renderer = null;
            _pass = null;
        }
    }
}
