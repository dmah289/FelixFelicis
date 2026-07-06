using Unity.Collections;
using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Falling sand simulation orchestrator.
    /// Runs PBD physics in <see cref="FixedUpdate"/>, uploads to
    /// <see cref="ParticleProvider.Writer"/> in <see cref="LateUpdate"/>.
    /// </summary>
    public class FallingSandSim : MonoBehaviour
    {
        public enum SpawnMode { Burst, Stream }

        [Header("Spawn")]
        [SerializeField] private SpawnMode mode = SpawnMode.Burst;
        [SerializeField] private int maxParticles = 10000;
        [SerializeField] private float spawnRange = 10f;

        [Header("Stream Mode")]
        [SerializeField] private int streamRate = 500;
        [SerializeField] private float streamSpawnWidth = 2f;

        [Header("Particle Size")]
        [SerializeField] private float radiusMin = 0.08f;
        [SerializeField] private float radiusMax = 0.12f;

        [Header("Physics")]
        [SerializeField] private Vector2 gravity = new(0f, -20f);
        [SerializeField] private float airDrag = 0.01f;
        [SerializeField] private float frictionCoef = 0.3f;
        [SerializeField] private float contactDamping = 0.4f;
        [SerializeField] private float wallFriction = 0.3f;
        [SerializeField, Range(1, 4)] private int substeps = 1;
        [SerializeField, Range(1, 6)] private int collisionIterations = 1;

        [Header("Slope")]
        [Tooltip("How much more the upper particle is pushed in a vertical contact (0=equal, 0.3=upper gets 80%)")]
        [SerializeField, Range(0f, 0.45f)] private float slopeBias = 0.2f;
        [Tooltip("Friction reduction for vertical contacts (0=no reduction, 1=frictionless stacking)")]
        [SerializeField, Range(0f, 1f)] private float slopeFrictionReduction = 0.7f;

        [Header("Sleep")]
        [SerializeField] private float sleepVelocityThreshold = 0.02f;
        [SerializeField] private int sleepFrames = 10;
        [SerializeField] private float wakeOverlapFraction = 0.4f;
        [SerializeField] private float wakeSpeed = 0.8f;

        [Header("Performance")]
        [Tooltip("Max milliseconds per FixedUpdate before skipping remaining substeps")]
        [SerializeField] private float maxPhysicsMs = 6f;

        private SandParticle[] particles;
        private int activeCount;
        private float streamAccumulator;
        private SpatialHash2D spatialHash;
        private readonly System.Diagnostics.Stopwatch stopwatch = new();

        private float sleepThresholdSqr;
        private float wakeSpeedSqr;

        private void Start()
        {
            particles = new SandParticle[maxParticles];
            activeCount = 0;

            float cellSize = radiusMax * 2.5f;
            float margin = cellSize;
            spatialHash = new SpatialHash2D(
                cellSize,
                -spawnRange - margin, spawnRange + margin,
                -spawnRange - margin, spawnRange + margin,
                maxParticles);

            sleepThresholdSqr = sleepVelocityThreshold * sleepVelocityThreshold;
            wakeSpeedSqr = wakeSpeed * wakeSpeed;

            if (mode == SpawnMode.Burst)
                SpawnBurst();
        }

        private void FixedUpdate()
        {
            if (mode == SpawnMode.Stream)
                StreamSpawn();

            if (activeCount == 0) return;

            stopwatch.Restart();

            float subDt = Time.fixedDeltaTime / substeps;
            float dtSqr = subDt * subDt;
            float dragMul = 1f - airDrag;
            float gx = gravity.x * dtSqr;
            float gy = gravity.y * dtSqr;
            long budgetTicks = (long)(maxPhysicsMs * System.Diagnostics.Stopwatch.Frequency / 1000);

            SandPhysics.SnapshotFrameStart(particles, activeCount);

            for (int s = 0; s < substeps; s++)
            {
                if (s > 0 && stopwatch.ElapsedTicks > budgetTicks)
                    break;

                SandPhysics.Integrate(particles, activeCount, gx, gy, dragMul);

                spatialHash.Build(particles, activeCount);

                SandPhysics.ResolveCollisions(
                    particles, activeCount, spatialHash,
                    frictionCoef, contactDamping,
                    wakeOverlapFraction, wakeSpeedSqr,
                    slopeBias, slopeFrictionReduction,
                    collisionIterations);

                SandPhysics.ResolveBoundaries(
                    particles, activeCount, spawnRange, spawnRange, wallFriction);
            }

            SandPhysics.UpdateSleep(particles, activeCount, sleepThresholdSqr, sleepFrames);
        }

        private void LateUpdate()
        {
            if (activeCount == 0) return;

            var writer = ParticleProvider.Writer;
            if (writer == null) return;

            NativeArray<ParticleRenderData> buffer = writer.BeginFrame(activeCount);
            if (!buffer.IsCreated) return;

            for (int i = 0; i < activeCount; i++)
            {
                buffer[i] = new ParticleRenderData
                {
                    center = particles[i].pos,
                    radius = particles[i].radius,
                    packedColor = particles[i].packedColor,
                };
            }

            writer.EndFrame(activeCount);
        }

        // ── Spawning ──────────────────────────────────────────────────

        private void SpawnBurst()
        {
            for (int i = 0; i < maxParticles; i++)
                SpawnParticle(RandomPositionInRange());
        }

        private void StreamSpawn()
        {
            if (activeCount >= maxParticles) return;

            streamAccumulator += streamRate * Time.fixedDeltaTime;
            int toSpawn = Mathf.Min((int)streamAccumulator, maxParticles - activeCount);
            streamAccumulator -= toSpawn;

            float topY = spawnRange - radiusMax;

            for (int i = 0; i < toSpawn; i++)
            {
                float x = Random.Range(-streamSpawnWidth * 0.5f, streamSpawnWidth * 0.5f);
                SpawnParticle(new Vector2(x, topY));
            }
        }

        private void SpawnParticle(Vector2 position)
        {
            if (activeCount >= maxParticles) return;

            particles[activeCount] = new SandParticle
            {
                pos = position,
                prevPos = position,
                frameStartPos = position,
                radius = Random.Range(radiusMin, radiusMax),
                packedColor = SandColors.GeneratePacked(),
                sleepCounter = 0,
                isSleeping = false,
            };
            activeCount++;
        }

        private Vector2 RandomPositionInRange()
        {
            return new Vector2(
                Random.Range(-spawnRange + radiusMax, spawnRange - radiusMax),
                Random.Range(-spawnRange + radiusMax, spawnRange - radiusMax));
        }

        private void OnDestroy()
        {
            spatialHash?.Dispose();
        }
    }
}
