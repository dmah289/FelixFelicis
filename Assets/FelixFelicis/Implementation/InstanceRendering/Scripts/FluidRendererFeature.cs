using UnityEngine.Rendering.Universal;

namespace FelixFelicis.SimulatingFluid
{
    public class FluidRendererFeature : ScriptableRendererFeature
    {
        private ParticleDrawer drawer;
        private FLuidRendererPass rendererPass;

        // URP gọi Create() nhiều lần (domain reload, settings change, enter/exit play).
        // Phải release tài nguyên cũ trước khi tạo mới, nếu không mỗi lần gọi
        // đều leak ComputeBuffer + Material + Mesh của drawer trước đó.
        public override void Create()
        {
            ReleaseResources();
            drawer = new ParticleDrawer();
            rendererPass = new FLuidRendererPass(drawer);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(rendererPass);
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            drawer?.Release();
            drawer = null;
            rendererPass = null;
        }
    }
}