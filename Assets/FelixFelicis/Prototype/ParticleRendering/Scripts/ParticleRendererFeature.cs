using UnityEngine.Rendering.Universal;

namespace FelixFelicis.Prototype.ParticleRendering
{
    /// <summary>
    /// URP composition root for the particle rendering pipeline.
    /// Creates and wires <see cref="ParticleDrawer"/> (GPU buffer owner) and
    /// <see cref="ParticleRenderPass"/> (URP bridge), then registers the drawer
    /// with <see cref="ParticleProvider"/> so simulations can discover it.
    /// <para>
    /// URP calls <see cref="Create"/> multiple times (domain reload, settings change,
    /// enter/exit play) — <see cref="ReleaseResources"/> runs first to prevent GPU leaks.
    /// </para>
    /// </summary>
    public class ParticleRendererFeature : ScriptableRendererFeature
    {
        private ParticleDrawer drawer;
        private ParticleRenderPass renderPass;

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
