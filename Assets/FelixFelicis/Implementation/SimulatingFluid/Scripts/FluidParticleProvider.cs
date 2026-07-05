using System.Collections.Generic;
using UnityEngine;

namespace FelixFelicis.SimulatingFluid
{
    /// <summary>
    /// Static bridge giữa simulation (MonoBehaviour) và renderer (ScriptableRenderPass).
    /// ScriptableRendererFeature là ScriptableObject — không reference được MonoBehaviour trực tiếp.
    /// Class này giữ 1 static list mà cả hai bên đều đọc/ghi được.
    /// </summary>
    public static class FluidParticleProvider
    {
        // RenderPass đọc list này mỗi frame
        public static readonly List<ParticleRenderData> Particles = new();
    }
}
