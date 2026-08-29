using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FelixFelicis.Prototype.ParticleRendering
{
    /// <summary>
    /// URP render pass that bridges the engine's rendering pipeline to <see cref="IInstanceDrawer"/>.
    /// <para><b>RenderGraph path</b> (Unity 6+): uses <c>AddUnsafePass</c> because
    /// <c>DrawMeshInstancedIndirect</c> is a raw command buffer operation unsupported
    /// by <c>AddRasterPass</c>. Explicitly calls <c>SetRenderTarget</c> inside the
    /// render func — <c>AddUnsafePass</c> declares resource dependencies but does
    /// <b>not</b> auto-bind render targets (unlike <c>AddRasterPass</c>).</para>
    /// <para><b>Legacy path</b>: compatibility fallback via <c>Execute()</c>.</para>
    /// </summary>
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
            public TextureHandle colorTarget;
            public TextureHandle depthTarget;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            using var builder = renderGraph.AddUnsafePass<PassData>("ParticleRendering", out var passData);

            passData.drawer = drawer;
            passData.camera = cameraData.camera;
            passData.colorTarget = resourceData.activeColorTexture;
            passData.depthTarget = resourceData.activeDepthTexture;

            // Alpha blending reads current color to blend — requires ReadWrite access
            builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
            builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
            {
                // Explicitly bind render target — AddUnsafePass does NOT auto-bind.
                // Without this, draw commands render into nothing on Vulkan/mobile.
                context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);

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
