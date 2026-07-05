using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FelixFelicis.SimulatingFluid
{
    public class FLuidRendererPass : ScriptableRenderPass
    {
        private readonly ParticleDrawer drawer;

        public FLuidRendererPass(ParticleDrawer drawer)
        {
            this.drawer = drawer;
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        // ── RenderGraph path (Unity 6+ / URP 17+) ──────────────────────────

        private class PassData
        {
            public ParticleDrawer drawer;
            public Camera camera;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            var resourceData = frameData.Get<UniversalResourceData>();

            using var builder = renderGraph.AddUnsafePass<PassData>("FluidParticles", out var passData);

            passData.drawer = drawer;
            passData.camera = cameraData.camera;

            // Alpha blending đọc color hiện tại để trộn → cần ReadWrite
            builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
            {
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                data.drawer.Clear();

                var particles = FluidParticleProvider.Particles;
                for (int i = 0; i < particles.Count; i++)
                {
                    var p = particles[i];
                    data.drawer.AddParticle(p.center, p.radius, p.color);
                }

                data.drawer.Dispatch(cmd, data.camera);
            });
        }

        // ── Legacy path (Compatibility Mode fallback) ───────────────────────

#pragma warning disable CS0672 // Overrides obsolete member
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
#pragma warning restore CS0672
        {
            var cmd = CommandBufferPool.Get("FluidParticles");

            drawer.Clear();

            var particles = FluidParticleProvider.Particles;
            for (int i = 0; i < particles.Count; i++)
            {
                var p = particles[i];
                drawer.AddParticle(p.center, p.radius, p.color);
            }

            drawer.Dispatch(cmd, renderingData.cameraData.camera);
            context.ExecuteCommandBuffer(cmd);

            CommandBufferPool.Release(cmd);
        }
    }
}