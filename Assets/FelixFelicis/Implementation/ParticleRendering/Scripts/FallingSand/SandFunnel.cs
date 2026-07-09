using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Bakes two <see cref="EdgeCollider2D"/> walls (left + right) into a
    /// <see cref="NativeArray{FunnelSegment}"/> for the Burst physics job.
    /// <para>
    /// The funnel is a static concave container: wide opening at the top,
    /// narrow spout at the bottom. Sand enters from above and exits through
    /// the spout, where it despawns.
    /// </para>
    /// <para>
    /// Each wall's <c>EdgeCollider2D.points</c> are converted to consecutive
    /// <see cref="FunnelSegment"/> entries with precomputed outward normals.
    /// Left wall normals point right (into funnel interior);
    /// right wall normals point left.
    /// </para>
    /// </summary>
    public class SandFunnel : MonoBehaviour, IDisposable
    {
        [Header("Walls")]
        [Tooltip("Left funnel wall. Points should be ordered top → bottom.")]
        [SerializeField] private EdgeCollider2D leftWall;

        [Tooltip("Right funnel wall. Points should be ordered top → bottom.")]
        [SerializeField] private EdgeCollider2D rightWall;

        [Header("Surface")]
        [Tooltip("Coulomb friction for funnel walls. Low values = sand slides fast (glass/metal).")]
        [SerializeField, Range(0f, 2f)]
        private float friction = 0.05f;

        [Header("Despawn")]
        [Tooltip("World Y below which particles are despawned (teleport + sleep). " +
                 "Set below the funnel spout opening.")]
        [SerializeField]
        private float despawnBelowY = -12f;

        [Header("Broadphase")]
        [Tooltip("Must match FallingSandSim.radiusMax for correct collision detection.")]
        [SerializeField]
        private float particleRadiusMax = 0.12f;

        private NativeArray<FunnelSegment> segments;
        private int segmentCount;
        private bool isBaked;

        // ── Public API ────────────────────────────────────────────────

        public float Friction => friction;
        public float DespawnBelowY => despawnBelowY;
        public float ParticleRadiusMax => particleRadiusMax;

        /// <summary>
        /// Returns the baked segment array and count.
        /// Bakes on first call if not already done.
        /// </summary>
        public (NativeArray<FunnelSegment> data, int count) GetSegments()
        {
            if (!isBaked) Bake();
            return (segments, segmentCount);
        }

        /// <summary>
        /// Converts <see cref="EdgeCollider2D.points"/> from both walls into
        /// a packed <see cref="NativeArray{FunnelSegment}"/>.
        /// Called once at startup — funnel is static.
        /// </summary>
        public void Bake()
        {
            if (isBaked) return;

            if (leftWall == null || rightWall == null)
            {
                Debug.LogError("[SandFunnel] Both leftWall and rightWall must be assigned.", this);
                return;
            }

            var leftPoints = leftWall.points;
            var rightPoints = rightWall.points;

            int leftSegCount = leftPoints.Length - 1;
            int rightSegCount = rightPoints.Length - 1;
            segmentCount = leftSegCount + rightSegCount;

            if (segmentCount <= 0)
            {
                Debug.LogError("[SandFunnel] EdgeCollider2D must have at least 2 points.", this);
                return;
            }

            segments = new NativeArray<FunnelSegment>(
                segmentCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

            int idx = 0;

            // Left wall: outNormal points RIGHT (into funnel interior)
            // For edge (a→b), right-hand normal = (dy, -dx) / |d|
            var leftTransform = leftWall.transform;
            for (int i = 0; i < leftSegCount; i++)
            {
                var a = (float2)((Vector2)leftTransform.TransformPoint(leftPoints[i]));
                var b = (float2)((Vector2)leftTransform.TransformPoint(leftPoints[i + 1]));
                segments[idx++] = BuildSegment(a, b, isLeftWall: true);
            }

            // Right wall: outNormal points LEFT (into funnel interior)
            // For edge (a→b), left-hand normal = (-dy, dx) / |d|
            var rightTransform = rightWall.transform;
            for (int i = 0; i < rightSegCount; i++)
            {
                var a = (float2)((Vector2)rightTransform.TransformPoint(rightPoints[i]));
                var b = (float2)((Vector2)rightTransform.TransformPoint(rightPoints[i + 1]));
                segments[idx++] = BuildSegment(a, b, isLeftWall: false);
            }

            isBaked = true;
        }

        // ── Segment Builder ───────────────────────────────────────────

        private static FunnelSegment BuildSegment(float2 a, float2 b, bool isLeftWall)
        {
            float2 d = b - a;
            float lenSq = math.dot(d, d);

            // Edge direction d = b - a (points are ordered top → bottom).
            // Left wall is on the left side (negative X), outNormal must point RIGHT (+X).
            // Right wall is on the right side (positive X), outNormal must point LEFT (-X).
            //
            // For left wall edge going downward: d ≈ (0, -1)
            //   (-d.y, d.x) = (1, 0) → points right ✓
            // For right wall edge going downward: d ≈ (0, -1)
            //   (d.y, -d.x) = (-1, 0) → points left ✓
            float2 perp = isLeftWall
                ? new float2(-d.y, d.x)
                : new float2(d.y, -d.x);

            float2 outNormal = math.normalizesafe(perp, new float2(0f, 1f));

            return new FunnelSegment
            {
                a = a,
                b = b,
                outNormal = outNormal,
                invLenSq = lenSq > 1e-10f ? 1f / lenSq : 0f,
            };
        }

        // ── Lifecycle ─────────────────────────────────────────────────

        private void OnDestroy() => Dispose();

        public void Dispose()
        {
            if (segments.IsCreated)
            {
                segments.Dispose();
                segments = default;
            }

            isBaked = false;
        }
    }
}
