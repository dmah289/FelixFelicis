using UnityEngine.Rendering.Universal;

namespace FelixFelicis.SimulatingFluid
{
    public class FluidRendererFeature : ScriptableRendererFeature
    {
        private ParticleDrawer drawer;
        private FLuidRendererPass rendererPass;
        
        public override void Create()
        {
            drawer = new ParticleDrawer();
            rendererPass = new FLuidRendererPass(drawer);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(rendererPass);
        }

        protected override void Dispose(bool disposing)
        {
            drawer?.Release();
        }
    }
}