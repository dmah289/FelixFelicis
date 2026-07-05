using UnityEngine.Rendering;
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

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("FluidParticles");
            
            drawer.Clear();

            // Đọc particles từ static provider (ghi bởi FluidSimDebug hoặc simulation thật)
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