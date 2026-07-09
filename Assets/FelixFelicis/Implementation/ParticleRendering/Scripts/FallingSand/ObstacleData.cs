using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// 2D shape type projected from a 3D collider.
    /// <see cref="SandObstacle"/> auto-detects from the attached Unity Collider.
    /// </summary>
    public enum ObstacleShape : byte
    {
        Circle,
        Box,
        Capsule,
    }

    /// <summary>
    /// Blittable obstacle descriptor for Burst jobs.
    /// All shape parameters stored in a fixed-size "fat struct" layout —
    /// the <see cref="shape"/> field determines which parameters are active.
    /// <para>
    /// <c>halfExtents</c> is overloaded per shape:<br/>
    /// Circle  → (radius, unused).<br/>
    /// Box     → (halfWidth, halfHeight).<br/>
    /// Capsule → (radius, halfSegmentLength).
    /// </para>
    /// <para>
    /// <c>aabbMin</c>/<c>aabbMax</c> are precomputed by <see cref="SandObstacle"/>
    /// at registration time — the Burst job uses them for O(1) broadphase rejection
    /// before the per-shape narrowphase math.
    /// </para>
    /// <para>
    /// Layout: 48 bytes — all float/float2 fields first (natural 4-byte alignment),
    /// <see cref="shape"/> byte last. 3 bytes implicit padding to reach 4-byte stride.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ObstacleData
    {
        // ── Broadphase (precomputed, expanded by max particle radius) ──

        /// <summary>AABB lower-left corner, expanded by particle radius at bake time.</summary>
        public float2 aabbMin;

        /// <summary>AABB upper-right corner, expanded by particle radius at bake time.</summary>
        public float2 aabbMax;

        // ── Narrowphase shape parameters ──────────────────────────────

        /// <summary>2D center projected from <c>Transform.position.xy</c>.</summary>
        public float2 center;

        /// <summary>Shape-dependent half-extents. See struct summary for per-shape meaning.</summary>
        public float2 halfExtents;

        /// <summary>Capsule medial axis unit vector. Unused for Circle/Box.</summary>
        public float2 axisDirection;

        // ── Surface properties ────────────────────────────────────────

        /// <summary>Coulomb friction coefficient (0 = frictionless, higher = stickier).</summary>
        public float friction;

        /// <summary>Restitution (0 = dead stop, 1 = perfect elastic bounce).</summary>
        public float bounciness;

        /// <summary>Which narrowphase math to use.</summary>
        public ObstacleShape shape;

        // 3 bytes implicit padding (byte → next 4-byte boundary)
        // Total: 5×8 + 2×4 + 1 + 3pad = 52 bytes
    }
}
