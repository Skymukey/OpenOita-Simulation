using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace OpenOita.V2.Validation
{
    /// <summary>
    /// Benchmark-only URP RenderGraph timestamp boundaries. This feature is installed
    /// into the isolated benchmark renderer by Editor code; the production Renderer2D
    /// does not reference it and the pass is inert when no timer is active. In the
    /// URP 2D renderer the end event is the last custom 2D hook. The benchmark's
    /// normal mode queues the end event after SingleCameraRequest instead, which
    /// also covers PixelPerfect, FinalPost, FinalBlit and overlay/debug tails.
    /// </summary>
    public sealed class NativeGpuTimingFeature : ScriptableRendererFeature
    {
        private sealed class TimingPass : ScriptableRenderPass
        {
            private readonly bool _begin;

            internal TimingPass(bool begin, RenderPassEvent passEvent)
            {
                _begin = begin;
                renderPassEvent = passEvent;
            }

            private sealed class PassData
            {
                internal bool Begin;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                // The feature is only enqueued for the target camera. Keep this
                // second guard so a stale pass cannot open a scope on another camera.
                if (!NativeGpuCameraTimer.HasActiveTimer) return;

                using (var builder = renderGraph.AddUnsafePass<PassData>(
                    _begin ? "OpenOita V2 Native GPU Timestamp Begin" :
                        "OpenOita V2 Native GPU Timestamp End", out var passData))
                {
                    passData.Begin = _begin;
                    // Plugin events alter global/native GPU state and have no graph
                    // resource dependency. Both flags are required to retain them.
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                    {
                        CommandBuffer commandBuffer =
                            CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        if (data.Begin)
                            NativeGpuCameraTimer.IssueBegin(commandBuffer);
                        else
                            NativeGpuCameraTimer.IssueEnd(commandBuffer);
                    });
                }
            }
        }

        private TimingPass _beginPass;
        private TimingPass _endPass;

        public override void Create()
        {
            _beginPass = new TimingPass(true, RenderPassEvent.BeforeRendering);
            _endPass = new TimingPass(false, RenderPassEvent.AfterRenderingPostProcessing);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (_beginPass == null || _endPass == null || !NativeGpuCameraTimer.HasActiveTimer ||
                camera != NativeGpuCameraTimer.ActiveTargetCamera)
                return;
            renderer.EnqueuePass(_beginPass);
            if (!NativeGpuCameraTimer.ActiveUsesCameraTail)
                renderer.EnqueuePass(_endPass);
        }

        protected override void Dispose(bool disposing)
        {
            _beginPass = null;
            _endPass = null;
        }
    }
}
