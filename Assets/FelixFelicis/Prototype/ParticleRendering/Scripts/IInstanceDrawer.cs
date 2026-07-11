using UnityEngine;
using UnityEngine.Rendering;

namespace FelixFelicis.ParticleRendering
{
    /// <summary>
    /// Draw-side contract for GPU instanced rendering.
    /// Render pass calls <see cref="Draw"/> per camera to issue draw commands.
    /// Safe to call multiple times per frame (multi-camera) — no data re-upload.
    /// </summary>
    public interface IInstanceDrawer
    {
        void Draw(CommandBuffer cmd, Camera cam);
    }
}
