using Unity.Collections;

namespace FelixFelicis.Prototype.ParticleRendering
{
    /// <summary>
    /// Write-side contract for GPU instanced rendering.
    /// Simulation calls <see cref="BeginFrame"/> to get a NativeArray mapped to GPU staging memory,
    /// writes data into it, then calls <see cref="EndFrame"/> to finalize.
    /// </summary>
    public interface IInstanceWriter<T> where T : unmanaged
    {
        NativeArray<T> BeginFrame(int count);
        void EndFrame(int count);
    }
}
