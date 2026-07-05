namespace FelixFelicis.ParticleRendering
{
    /// <summary>
    /// Static bridge between simulation (MonoBehaviour) and renderer (ScriptableRenderPass).
    /// Exposes two interface surfaces: <see cref="Writer"/> for simulation, <see cref="Drawer"/> for render pass.
    /// <see cref="ParticleRendererFeature"/> registers/unregisters via <see cref="Register"/>/<see cref="Unregister"/>.
    /// </summary>
    /// <remarks>
    /// Holds concrete ParticleDrawer internally because writer and drawer must share
    /// the same lifecycle-managed GPU buffer. Properties expose only interfaces.
    /// </remarks>
    public static class ParticleProvider
    {
        private static ParticleDrawer instance;

        internal static IInstanceWriter<ParticleRenderData> Writer => instance;
        internal static IInstanceDrawer Drawer => instance;

        internal static void Register(ParticleDrawer drawer) => instance = drawer;

        internal static void Unregister(ParticleDrawer drawer)
        {
            if (instance == drawer) instance = null;
        }
    }
}
