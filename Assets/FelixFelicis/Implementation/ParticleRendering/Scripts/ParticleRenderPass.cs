using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FelixFelicis.ParticleRendering
{
    public class ParticleRenderPass : ScriptableRenderPass
    {
        private readonly IInstanceDrawer drawer;

        public ParticleRenderPass(IInstanceDrawer drawer)
        {
            this.drawer = drawer;
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        // ── RenderGraph path (Unity 6+ / URP 17+) ──────────────────────────

        // RenderGraph pools PassData instances — no per-frame heap allocation after warmup.
        private class PassData
        {
            public IInstanceDrawer drawer;
            public Camera camera;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            using var builder = renderGraph.AddUnsafePass<PassData>("ParticleRendering", out var passData);

            passData.drawer = drawer;
            passData.camera = cameraData.camera;

            // Alpha blending reads current color to blend — requires ReadWrite access
            builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
            {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                data.drawer.Draw(cmd, data.camera);
            });
        }

        // ── Legacy path (Compatibility Mode fallback) ───────────────────────

#pragma warning disable CS0672 // Overrides obsolete member
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
#pragma warning restore CS0672
        {
            var cmd = CommandBufferPool.Get("ParticleRendering");

            drawer.Draw(cmd, renderingData.cameraData.camera);
            context.ExecuteCommandBuffer(cmd);

            CommandBufferPool.Release(cmd);
        }
    }
}
