using Unity.Mathematics;
using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Marks a GameObject as a sand obstacle.
    /// Reads the attached 3D <see cref="Collider"/> and projects it to a 2D shape
    /// on the XY plane for the falling sand simulation.
    /// <para>
    /// Supported colliders: <see cref="SphereCollider"/> → Circle,
    /// <see cref="BoxCollider"/> → AABB, <see cref="CapsuleCollider"/> → 2D Capsule.
    /// </para>
    /// <para>
    /// Per-obstacle <see cref="friction"/> and <see cref="bounciness"/> are configurable
    /// in the Inspector and baked into <see cref="ObstacleData"/> at enable time.
    /// The broadphase AABB is precomputed and expanded by <see cref="particleRadiusMax"/>
    /// so the Burst job does a simple point-in-AABB test with zero per-particle math.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class SandObstacle : MonoBehaviour
    {
        [Header("Surface Properties")]
        [SerializeField, Range(0f, 2f)]
        private float friction = 0.3f;

        [SerializeField, Range(0f, 1f)]
        private float bounciness;

        [Header("Broadphase")]
        [Tooltip("Maximum particle radius in the simulation. " +
                 "Used to expand the broadphase AABB so point-in-box rejection works " +
                 "without per-particle radius math. Must match FallingSandSim.radiusMax.")]
        [SerializeField]
        private float particleRadiusMax = 0.12f;

        private ObstacleData cachedData;
        private ObstacleShape detectedShape;
        private Collider attachedCollider;
        private bool isDirty = true;

        // ── Unity Lifecycle ───────────────────────────────────────────

        private void Awake()
        {
            attachedCollider = DetectCollider();
        }

        private void OnEnable()
        {
            isDirty = true;
            SandObstacleRegistry.Register(this);
        }

        private void OnDisable() => SandObstacleRegistry.Unregister(this);

        private void OnValidate()
        {
            isDirty = true;
            SandObstacleRegistry.MarkDirty();
        }

        // ── Public API ────────────────────────────────────────────────

        /// <summary>
        /// Returns the cached <see cref="ObstacleData"/>, rebuilding if dirty.
        /// Called by <see cref="SandObstacleRegistry"/> when packing the NativeArray.
        /// </summary>
        public ObstacleData ToObstacleData()
        {
            if (isDirty) RebuildCachedData();
            return cachedData;
        }

        // ── Shape Detection ───────────────────────────────────────────

        /// <summary>
        /// Auto-detects shape from the first supported Collider on this GameObject.
        /// Priority: CapsuleCollider > SphereCollider > BoxCollider.
        /// CapsuleCollider checked first because a degenerate capsule (radius == halfHeight)
        /// could coexist with a SphereCollider.
        /// </summary>
        private Collider DetectCollider()
        {
            if (TryGetComponent<CapsuleCollider>(out var capsule))
            {
                detectedShape = ObstacleShape.Capsule;
                return capsule;
            }

            if (TryGetComponent<SphereCollider>(out var sphere))
            {
                detectedShape = ObstacleShape.Circle;
                return sphere;
            }

            if (TryGetComponent<BoxCollider>(out var box))
            {
                detectedShape = ObstacleShape.Box;
                return box;
            }

            Debug.LogError($"[SandObstacle] No supported Collider on '{name}'. " +
                           "Add a SphereCollider, BoxCollider, or CapsuleCollider.", this);
            detectedShape = ObstacleShape.Circle;
            return null;
        }

        // ── 3D → 2D Projection ───────────────────────────────────────

        private void RebuildCachedData()
        {
            isDirty = false;

            // Lazy init: Awake() may not have run yet when the registry
            // retroactively registers this obstacle during SetInstance.
            if (attachedCollider == null)
                attachedCollider = DetectCollider();

            if (attachedCollider == null)
            {
                cachedData = default;
                return;
            }

            var pos = transform.position;
            var scale = transform.lossyScale;
            var center = new float2(pos.x, pos.y);

            cachedData.friction = friction;
            cachedData.bounciness = bounciness;
            cachedData.shape = detectedShape;

            switch (detectedShape)
            {
                case ObstacleShape.Circle:
                    BuildCircle((SphereCollider)attachedCollider, center, scale);
                    break;
                case ObstacleShape.Box:
                    BuildBox((BoxCollider)attachedCollider, center, scale);
                    break;
                case ObstacleShape.Capsule:
                    BuildCapsule((CapsuleCollider)attachedCollider, center, scale);
                    break;
            }
        }

        /// <summary>
        /// Sphere → Circle. Radius = collider.radius × max(scaleX, scaleY).
        /// Z-axis scale is irrelevant for the 2D projection.
        /// </summary>
        private void BuildCircle(SphereCollider sphere, float2 center, Vector3 scale)
        {
            float maxScale2D = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
            float radius = sphere.radius * maxScale2D;

            var offset = sphere.center;
            center += new float2(offset.x * scale.x, offset.y * scale.y);

            cachedData.center = center;
            cachedData.halfExtents = new float2(radius, 0f);
            cachedData.axisDirection = float2.zero;

            float expand = radius + particleRadiusMax;
            cachedData.aabbMin = center - expand;
            cachedData.aabbMax = center + expand;
        }

        /// <summary>
        /// Box → AABB (axis-aligned, rotation ignored in Phase 1).
        /// halfExtents = (collider.size × scale).xy × 0.5.
        /// </summary>
        private void BuildBox(BoxCollider box, float2 center, Vector3 scale)
        {
            var size = box.size;
            var halfExtents = new float2(
                size.x * Mathf.Abs(scale.x) * 0.5f,
                size.y * Mathf.Abs(scale.y) * 0.5f);

            var offset = box.center;
            center += new float2(offset.x * scale.x, offset.y * scale.y);

            cachedData.center = center;
            cachedData.halfExtents = halfExtents;
            cachedData.axisDirection = float2.zero;

            cachedData.aabbMin = center - halfExtents - particleRadiusMax;
            cachedData.aabbMax = center + halfExtents + particleRadiusMax;
        }

        /// <summary>
        /// Capsule → 2D capsule (two semicircles + rectangle).
        /// Projects the capsule's <c>direction</c> axis (0=X, 1=Y, 2=Z) onto XY,
        /// rotated by <c>transform.eulerAngles.z</c>.
        /// <para>
        /// If direction == Z, the capsule axis is perpendicular to XY plane,
        /// so the 2D projection degenerates to a circle (halfSegmentLength = 0).
        /// </para>
        /// </summary>
        private void BuildCapsule(CapsuleCollider capsule, float2 center, Vector3 scale)
        {
            int dir = capsule.direction; // 0=X, 1=Y, 2=Z
            float height = capsule.height;
            float radius = capsule.radius;

            // Z-axis capsule → degenerate to circle in XY
            if (dir == 2)
            {
                float radialScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
                float scaledRadius = radius * radialScale;

                var offsetZ = capsule.center;
                center += new float2(offsetZ.x * scale.x, offsetZ.y * scale.y);

                cachedData.center = center;
                cachedData.halfExtents = new float2(scaledRadius, 0f);
                cachedData.axisDirection = new float2(0f, 1f);
                cachedData.shape = ObstacleShape.Circle;

                float expandZ = scaledRadius + particleRadiusMax;
                cachedData.aabbMin = center - expandZ;
                cachedData.aabbMax = center + expandZ;
                return;
            }

            float axisScale, radialScaleCap;
            float2 localAxis;

            if (dir == 0) // X-axis capsule
            {
                axisScale = Mathf.Abs(scale.x);
                radialScaleCap = Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                localAxis = new float2(1f, 0f);
            }
            else // Y-axis capsule (dir == 1)
            {
                axisScale = Mathf.Abs(scale.y);
                radialScaleCap = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                localAxis = new float2(0f, 1f);
            }

            float scaledR = radius * radialScaleCap;
            float halfSegment = Mathf.Max(0f, height * 0.5f * axisScale - scaledR);

            // Rotate localAxis by Z-rotation of the transform
            float zRad = transform.eulerAngles.z * Mathf.Deg2Rad;
            float cos = Mathf.Cos(zRad);
            float sin = Mathf.Sin(zRad);
            float2 rotatedAxis = math.normalizesafe(
                new float2(localAxis.x * cos - localAxis.y * sin,
                           localAxis.x * sin + localAxis.y * cos),
                new float2(0f, 1f));

            var offset = capsule.center;
            center += new float2(offset.x * scale.x, offset.y * scale.y);

            cachedData.center = center;
            cachedData.halfExtents = new float2(scaledR, halfSegment);
            cachedData.axisDirection = rotatedAxis;

            // AABB from two hemisphere centers ± (radius + particleRadius)
            float2 axisExtent = math.abs(rotatedAxis) * halfSegment;
            float expand = scaledR + particleRadiusMax;
            cachedData.aabbMin = center - axisExtent - expand;
            cachedData.aabbMax = center + axisExtent + expand;
        }
    }
}
