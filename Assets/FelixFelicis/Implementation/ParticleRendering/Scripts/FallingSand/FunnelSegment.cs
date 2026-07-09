using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// One edge segment of a funnel wall, precomputed at bake time.
    /// Used by <see cref="SandPhysics.ResolveFunnelJob"/> for particle ↔ wall collision.
    /// <para>
    /// 32 bytes — exactly 2 structs per 64-byte cache line, no straddling.
    /// Sequential scan of 100 segments fits in ~50 cache lines.
    /// </para>
    /// <para>
    /// <c>outNormal</c> points outward from the funnel interior.
    /// Convention: particles with <c>dot(pos - a, outNormal) &gt; radius</c>
    /// are on the safe (outside) side — no collision needed.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FunnelSegment
    {
        /// <summary>Segment start point in world XY.</summary>
        public float2 a;

        /// <summary>Segment end point in world XY.</summary>
        public float2 b;

        /// <summary>
        /// Unit outward normal (perpendicular to edge, pointing away from funnel interior).
        /// Precomputed at bake time to avoid per-particle normalize in the hot loop.
        /// </summary>
        public float2 outNormal;

        /// <summary>
        /// Precomputed <c>1 / lengthSq(b - a)</c>.
        /// Used for segment projection: <c>t = dot(p - a, b - a) * invLenSq</c>
        /// avoids a division per particle in the inner loop.
        /// </summary>
        public float invLenSq;

        // 4B padding to reach 32B (power-of-2, 2/cache line)
    }
}
