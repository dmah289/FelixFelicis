using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Falling sand simulation orchestrator.
    /// Runs Burst-compiled physics jobs in <see cref="FixedUpdate"/>, uploads to
    /// <see cref="ParticleProvider.Writer"/> in <see cref="LateUpdate"/>.
    /// <para>
    /// All simulation state in NativeArrays (Allocator.Persistent).
    /// Jobs scheduled sequentially (Schedule + Complete) — no parallel execution.
    /// Sleep update is managed (not a job) — runs once per frame, O(awake).
    /// </para>
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

        // Simulation state — all NativeArray with Allocator.Persistent
        private NativeArray<SandParticle> particles;
        private NativeArray<bool> isSleeping;
        private NativeArray<byte> sleepCounters;
        private NativeArray<int> activeIndices;
        private NativeReference<int> awakeCountRef;
        private NativeReference<bool> wakeOccurredRef;

        private int activeCount;
        private int awakeCount;
        private float streamAccumulator;
        private SpatialHash2D spatialHash;
        private readonly System.Diagnostics.Stopwatch stopwatch = new();

        private float sleepThresholdSqr;
        private float wakeSpeedSqr;

        // Skip render upload when nothing moved (all sleeping, no spawns).
        private bool needsRenderUpload;

        private void Start()
        {
            particles = new NativeArray<SandParticle>(maxParticles, Allocator.Persistent);
            isSleeping = new NativeArray<bool>(maxParticles, Allocator.Persistent);
            sleepCounters = new NativeArray<byte>(maxParticles, Allocator.Persistent);
            activeIndices = new NativeArray<int>(maxParticles, Allocator.Persistent);
            awakeCountRef = new NativeReference<int>(Allocator.Persistent);
            wakeOccurredRef = new NativeReference<bool>(Allocator.Persistent);
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

            // Build active indices (Burst job)
            new SandPhysics.BuildActiveIndicesJob
            {
                isSleeping = isSleeping,
                count = activeCount,
                activeIndices = activeIndices,
                awakeCount = awakeCountRef,
            }.Schedule().Complete();

            awakeCount = awakeCountRef.Value;
            if (awakeCount == 0) return;

            needsRenderUpload = true;
            stopwatch.Restart();

            float subDt = Time.fixedDeltaTime / substeps;
            float dtSqr = subDt * subDt;
            float dragMul = 1f - airDrag;
            float gx = gravity.x * dtSqr;
            float gy = gravity.y * dtSqr;
            long budgetTicks = (long)(maxPhysicsMs * System.Diagnostics.Stopwatch.Frequency / 1000);

            // Snapshot frame start positions (Burst job)
            new SandPhysics.SnapshotFrameStartJob
            {
                particles = particles,
                activeIndices = activeIndices,
                awakeCount = awakeCount,
            }.Schedule().Complete();

            for (int s = 0; s < substeps; s++)
            {
                if (s > 0 && stopwatch.ElapsedTicks > budgetTicks)
                    break;

                // Integrate (Burst job)
                new SandPhysics.IntegrateJob
                {
                    particles = particles,
                    activeIndices = activeIndices,
                    awakeCount = awakeCount,
                    gx = gx,
                    gy = gy,
                    dragMul = dragMul,
                }.Schedule().Complete();

                // Spatial hash build (Burst job)
                spatialHash.EnsureCapacity(activeCount);
                new SandPhysics.SpatialHashBuildJob
                {
                    particles = particles,
                    count = activeCount,
                    invCellSize = spatialHash.invCellSize,
                    originX = spatialHash.originX,
                    originY = spatialHash.originY,
                    gridWidth = spatialHash.gridWidth,
                    gridHeight = spatialHash.gridHeight,
                    cellCount = spatialHash.cellCount,
                    cellCounts = spatialHash.cellCounts,
                    cellOffsets = spatialHash.cellOffsets,
                    sortedIndices = spatialHash.sortedIndices,
                    particleCells = spatialHash.particleCells,
                }.Schedule().Complete();

                // Resolve collisions (Burst job)
                wakeOccurredRef.Value = false;
                new SandPhysics.ResolveCollisionsJob
                {
                    particles = particles,
                    activeIndices = activeIndices,
                    awakeCount = awakeCount,
                    sortedIndices = spatialHash.sortedIndices,
                    cellOffsets = spatialHash.cellOffsets,
                    cellCounts = spatialHash.cellCounts,
                    invCellSize = spatialHash.invCellSize,
                    originX = spatialHash.originX,
                    originY = spatialHash.originY,
                    gridWidth = spatialHash.gridWidth,
                    gridHeight = spatialHash.gridHeight,
                    isSleeping = isSleeping,
                    sleepCounters = sleepCounters,
                    frictionCoef = frictionCoef,
                    contactDamping = contactDamping,
                    wakeOverlapFraction = wakeOverlapFraction,
                    wakeSpeedSqr = wakeSpeedSqr,
                    slopeBias = slopeBias,
                    slopeFrictionReduction = slopeFrictionReduction,
                    iterations = collisionIterations,
                    wakeOccurred = wakeOccurredRef,
                }.Schedule().Complete();

                if (wakeOccurredRef.Value)
                {
                    new SandPhysics.BuildActiveIndicesJob
                    {
                        isSleeping = isSleeping,
                        count = activeCount,
                        activeIndices = activeIndices,
                        awakeCount = awakeCountRef,
                    }.Schedule().Complete();
                    awakeCount = awakeCountRef.Value;
                }

                // Resolve boundaries (Burst job)
                new SandPhysics.ResolveBoundariesJob
                {
                    particles = particles,
                    activeIndices = activeIndices,
                    awakeCount = awakeCount,
                    boundsX = spawnRange,
                    boundsY = spawnRange,
                    wallFriction = wallFriction,
                }.Schedule().Complete();
            }

            // Sleep update — managed, not a job
            SandPhysics.UpdateSleep(
                particles, activeIndices, awakeCount,
                isSleeping, sleepCounters,
                sleepThresholdSqr, sleepFrames);
        }

        private void LateUpdate()
        {
            if (activeCount == 0 || !needsRenderUpload) return;
            needsRenderUpload = false;

            var writer = ParticleProvider.Writer;
            if (writer == null) return;

            var buffer = writer.BeginFrame(activeCount);
            if (!buffer.IsCreated) return;

            for (int i = 0; i < activeCount; i++)
            {
                var p = particles[i];
                buffer[i] = new ParticleRenderData
                {
                    center = p.pos, // float2 → Vector2 implicit conversion
                    radius = p.radius,
                    packedColor = p.packedColor,
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
                SpawnParticle(new float2(x, topY));
            }
        }

        private void SpawnParticle(float2 position)
        {
            if (activeCount >= maxParticles) return;

            particles[activeCount] = new SandParticle
            {
                pos = position,
                prevPos = position,
                frameStartPos = position,
                radius = Random.Range(radiusMin, radiusMax),
                packedColor = SandColors.GeneratePacked(),
            };
            isSleeping[activeCount] = false;
            sleepCounters[activeCount] = 0;
            activeCount++;

            needsRenderUpload = true;
        }

        private float2 RandomPositionInRange()
        {
            return new float2(
                Random.Range(-spawnRange + radiusMax, spawnRange - radiusMax),
                Random.Range(-spawnRange + radiusMax, spawnRange - radiusMax));
        }

        private void OnDestroy()
        {
            if (particles.IsCreated) particles.Dispose();
            if (isSleeping.IsCreated) isSleeping.Dispose();
            if (sleepCounters.IsCreated) sleepCounters.Dispose();
            if (activeIndices.IsCreated) activeIndices.Dispose();
            if (awakeCountRef.IsCreated) awakeCountRef.Dispose();
            if (wakeOccurredRef.IsCreated) wakeOccurredRef.Dispose();
            spatialHash?.Dispose();
        }
    }
}
