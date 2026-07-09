using Unity.Burst;
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
        [SerializeField, Range(1, 255)] private int sleepFrames = 10;
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

        /// <summary>Total particles spawned (including sleeping). Only increases.</summary>
        private int spawnedCount;
        private int awakeCount;
        private float streamAccumulator;
        private SpatialHash2D spatialHash;
        private readonly System.Diagnostics.Stopwatch stopwatch = new();

        private float sleepThresholdSqr;
        private float wakeSpeedSqr;

        private SandObstacleRegistry obstacleRegistry;

        // Skip render upload when nothing moved (all sleeping, no spawns).
        private bool needsRenderUpload;

        private void Start()
        {
            obstacleRegistry = new SandObstacleRegistry();
            SandObstacleRegistry.SetInstance(obstacleRegistry);

            particles = new NativeArray<SandParticle>(maxParticles, Allocator.Persistent);
            isSleeping = new NativeArray<bool>(maxParticles, Allocator.Persistent);
            sleepCounters = new NativeArray<byte>(maxParticles, Allocator.Persistent);
            activeIndices = new NativeArray<int>(maxParticles, Allocator.Persistent);
            awakeCountRef = new NativeReference<int>(Allocator.Persistent);
            wakeOccurredRef = new NativeReference<bool>(Allocator.Persistent);
            spawnedCount = 0;

            float cellSize = radiusMax * 2.5f;
            float margin = cellSize;
            spatialHash = new SpatialHash2D(
                cellSize,
                -spawnRange - margin, spawnRange + margin,
                -spawnRange - margin, spawnRange + margin,
                maxParticles);

            // Runtime clamp — Inspector Range only constrains UI, not code/SO writes.
            substeps = Mathf.Clamp(substeps, 1, 4);
            sleepFrames = Mathf.Clamp(sleepFrames, 1, 255);

            sleepThresholdSqr = sleepVelocityThreshold * sleepVelocityThreshold;
            wakeSpeedSqr = wakeSpeed * wakeSpeed;

            if (mode == SpawnMode.Burst)
                SpawnBurst();
        }

        private void FixedUpdate()
        {
            if (mode == SpawnMode.Stream)
                StreamSpawn();

            if (spawnedCount == 0) return;

            // Build active indices (Burst job)
            new SandPhysics.BuildActiveIndicesJob
            {
                isSleeping = isSleeping,
                particleCount = spawnedCount,
                activeIndices = activeIndices,
                awakeCount = awakeCountRef,
            }.Schedule().Complete();

            awakeCount = awakeCountRef.Value;
            if (awakeCount == 0) return;

            needsRenderUpload = true;
            stopwatch.Restart();

            float subDt = Time.fixedDeltaTime / substeps;
            float dtSqr = subDt * subDt;
            float dragMultiplier = 1f - airDrag;
            float gravityDtSqX = gravity.x * dtSqr;
            float gravityDtSqY = gravity.y * dtSqr;
            long budgetTicks = (long)(maxPhysicsMs * System.Diagnostics.Stopwatch.Frequency / 1000);

            // Snapshot frame start positions (Burst job)
            new SandPhysics.SnapshotFrameStartJob
            {
                particles = particles,
                activeIndices = activeIndices,
                awakeCount = awakeCount,
            }.Schedule().Complete();

            // Query obstacle data once per frame — static obstacles don't change
            // between substeps. Avoids repeated dirty-check + managed call overhead.
            var (obstacleData, obstacleCount) = obstacleRegistry.GetObstacleData();

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
                    gravityDtSqX = gravityDtSqX,
                    gravityDtSqY = gravityDtSqY,
                    dragMultiplier = dragMultiplier,
                }.Schedule().Complete();

                // Spatial hash build (Burst job) — hashes ALL spawned particles
                // (including sleeping) so awake particles can collide with them.
                spatialHash.EnsureCapacity(spawnedCount);
                new SandPhysics.SpatialHashBuildJob
                {
                    particles = particles,
                    particleCount = spawnedCount,
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
                        particleCount = spawnedCount,
                        activeIndices = activeIndices,
                        awakeCount = awakeCountRef,
                    }.Schedule().Complete();
                    awakeCount = awakeCountRef.Value;
                }

                // Resolve obstacles (Burst job) — after particle collisions, before boundary clamp.
                if (obstacleCount > 0)
                {
                    new SandPhysics.ResolveObstaclesJob
                    {
                        particles = particles,
                        activeIndices = activeIndices,
                        awakeCount = awakeCount,
                        obstacles = obstacleData,
                        obstacleCount = obstacleCount,
                    }.Schedule().Complete();
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

            // Sleep update — managed, not a job.
            // Kept managed because O(awake), runs once per frame, and
            // the result (needsRenderUpload) is needed immediately.
            SandPhysics.UpdateSleep(
                particles, activeIndices, awakeCount,
                isSleeping, sleepCounters,
                sleepThresholdSqr, sleepFrames);
        }

        private void LateUpdate()
        {
            if (spawnedCount == 0 || !needsRenderUpload) return;
            needsRenderUpload = false;

            var writer = ParticleProvider.Writer;
            if (writer == null) return;

            var buffer = writer.BeginFrame(spawnedCount);
            if (!buffer.IsCreated) return;

            // Burst job: copy SandParticle → ParticleRenderData (pos, radius, packedColor).
            // Uploads ALL spawned particles including sleeping — they must still render.
            new CopyToRenderDataJob
            {
                particles = particles,
                renderData = buffer,
                count = spawnedCount,
            }.Schedule().Complete();

            writer.EndFrame(spawnedCount);
        }

        // ── Render upload job ─────────────────────────────────────────

        /// <summary>
        /// Burst-compiled copy from <see cref="SandParticle"/> (32B) to
        /// <see cref="ParticleRenderData"/> (16B). Extracts only the fields
        /// needed for GPU rendering: position, radius, and packed color.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        private struct CopyToRenderDataJob : IJob
        {
            [ReadOnly] public NativeArray<SandParticle> particles;
            [WriteOnly] public NativeArray<ParticleRenderData> renderData;
            public int count;

            public void Execute()
            {
                for (int i = 0; i < count; i++)
                {
                    var p = particles[i];
                    renderData[i] = new ParticleRenderData
                    {
                        center = p.pos,
                        radius = p.radius,
                        packedColor = p.packedColor,
                    };
                }
            }
        }

        // ── Spawning ──────────────────────────────────────────────────

        private void SpawnBurst()
        {
            for (int i = 0; i < maxParticles; i++)
                SpawnParticle(RandomPositionInRange());
        }

        private void StreamSpawn()
        {
            if (spawnedCount >= maxParticles) return;

            streamAccumulator += streamRate * Time.fixedDeltaTime;
            int toSpawn = Mathf.Min((int)streamAccumulator, maxParticles - spawnedCount);

            // Subtract integer part only — prevents float precision drift
            // from accumulating over long play sessions (hours on mobile).
            streamAccumulator -= toSpawn;
            if (streamAccumulator > streamRate) streamAccumulator = 0f;

            float topY = spawnRange - radiusMax;

            for (int i = 0; i < toSpawn; i++)
            {
                float x = Random.Range(-streamSpawnWidth * 0.5f, streamSpawnWidth * 0.5f);
                SpawnParticle(new float2(x, topY));
            }
        }

        private void SpawnParticle(float2 position)
        {
            if (spawnedCount >= maxParticles) return;

            particles[spawnedCount] = new SandParticle
            {
                pos = position,
                prevPos = position,
                frameStartPos = position,
                radius = Random.Range(radiusMin, radiusMax),
                packedColor = SandColors.GeneratePacked(),
            };
            isSleeping[spawnedCount] = false;
            sleepCounters[spawnedCount] = 0;
            spawnedCount++;

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
            if (obstacleRegistry != null)
            {
                SandObstacleRegistry.ClearInstance(obstacleRegistry);
                obstacleRegistry.Dispose();
                obstacleRegistry = null;
            }

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
