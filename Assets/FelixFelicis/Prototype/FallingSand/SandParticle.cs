using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace FelixFelicis.Prototype.ParticleRendering.Simulation
{
    /// <summary>
    /// Per-particle state for falling sand simulation.
    /// Stored in a pre-allocated NativeArray — no per-frame allocation.
    /// Verlet-style: velocity is derived from <c>pos - prevPos</c>.
    /// <para>
    /// 32 bytes (power-of-2) — exactly 2 structs per 64-byte cache line, no straddling.
    /// Sleep state (<c>isSleeping</c>, <c>sleepCounter</c>) stored in separate parallel
    /// NativeArrays for cache-friendly skip checks (64 bools per cache line).
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SandParticle
    {
        public float2 pos;
        public float2 prevPos;

        // Snapshot of position at the start of each FixedUpdate frame.
        // Used by sleep detection to measure total frame displacement
        // instead of per-substep micro-jitter from collision corrections.
        public float2 frameStartPos;

        public float radius;
        public uint packedColor;
    }
}
