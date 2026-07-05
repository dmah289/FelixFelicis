using UnityEngine.Rendering.Universal;

namespace FelixFelicis.ParticleRendering
{
    public class ParticleRendererFeature : ScriptableRendererFeature
    {
        private ParticleDrawer drawer;
        private ParticleRenderPass renderPass;

        // URP calls Create() multiple times (domain reload, settings change, enter/exit play).
        // Must release old resources before creating new ones to avoid GPU resource leaks.
        public override void Create()
        {
            ReleaseResources();
            drawer = new ParticleDrawer();
            renderPass = new ParticleRenderPass(drawer);
            ParticleProvider.Register(drawer);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderPass != null)
                renderer.EnqueuePass(renderPass);
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            if (drawer != null)
            {
                ParticleProvider.Unregister(drawer);
                drawer.Dispose();
                drawer = null;
            }
            renderPass = null;
        }
    }
}
