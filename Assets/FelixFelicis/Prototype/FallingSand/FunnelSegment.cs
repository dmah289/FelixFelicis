using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// One edge segment of a funnel wall, fully precomputed at bake time.
    /// Used by <see cref="SandPhysics.ResolveFunnelJob"/> for particle ↔ wall collision.
    /// <para>
    /// All derived quantities (edge direction, AABB, inverse length²) are baked once
    /// to eliminate redundant per-particle math in the hot loop.
    /// </para>
    /// <para>
    /// <c>outNormal</c> points away from the funnel interior toward the outside.
    /// Particles on the interior side satisfy <c>dot(pos - a, outNormal) &lt; radius</c>.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FunnelSegment
    {
        // ── Broadphase (checked first for early rejection) ────────

        /// <summary>Precomputed AABB min corner, expanded by broadphase margin at bake time.</summary>
        public float2 aabbMin;

        /// <summary>Precomputed AABB max corner, expanded by broadphase margin at bake time.</summary>
        public float2 aabbMax;

        // ── Narrowphase geometry ──────────────────────────────────

        /// <summary>Segment start point in world XY.</summary>
        public float2 a;

        /// <summary>Precomputed edge direction <c>b - a</c>. Avoids recomputing per particle.</summary>
        public float2 edge;

        /// <summary>
        /// Unit outward normal (perpendicular to edge, pointing away from funnel interior).
        /// Precomputed at bake time to avoid per-particle normalize.
        /// </summary>
        public float2 outNormal;

        /// <summary>
        /// Precomputed <c>1 / dot(edge, edge)</c>.
        /// Used for segment projection: <c>t = dot(p - a, edge) * invLenSq</c>
        /// avoids a division per particle in the inner loop.
        /// </summary>
        public float invLenSq;

        // Total: 5×8 + 4 = 44 bytes.
        // Broadphase AABB fields at struct top → reject path only needs
        // first 16B (within same cache line prefetch).
    }
}
