using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace FelixFelicis.Prototype.ParticleRendering.Simulation
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

        [Tooltip("Burst mode: total particle count spawned at Start.\n" +
                 "Stream mode: initial NativeArray capacity (grows automatically as needed).")]
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

        [Header("Funnel")]
        [Tooltip("Drag the SandFunnel MonoBehaviour that holds the two EdgeCollider2D walls.")]
        [SerializeField] private SandFunnel funnel;

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

        /// <summary>Total particles spawned (including sleeping/despawned). Only increases.</summary>
        private int spawnedCount;

        /// <summary>Current NativeArray capacity. Grows by doubling in Stream mode.</summary>
        private int particleCapacity;

        private int awakeCount;
        private float streamAccumulator;
        private SpatialHash2D spatialHash;
        private readonly System.Diagnostics.Stopwatch stopwatch = new();

        private float sleepThresholdSqr;
        private float wakeSpeedSqr;

        private SandObstacleRegistry obstacleRegistry;

        // Precomputed gravity nudge for WakeNearbyParticles — avoids
        // recomputing sqrt + normalize on every obstacle removal event.
        private float2 cachedWakeNudge;

        // Skip render upload when nothing moved (all sleeping, no spawns).
        private bool needsRenderUpload;

        private void Start()
        {
            Application.targetFrameRate = 60;
            obstacleRegistry = new SandObstacleRegistry();
            SandObstacleRegistry.SetInstance(obstacleRegistry);

            particleCapacity = maxParticles;
            particles = new NativeArray<SandParticle>(particleCapacity, Allocator.Persistent);
            isSleeping = new NativeArray<bool>(particleCapacity, Allocator.Persistent);
            sleepCounters = new NativeArray<byte>(particleCapacity, Allocator.Persistent);
            activeIndices = new NativeArray<int>(particleCapacity, Allocator.Persistent);
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

            // Precompute gravity nudge for obstacle removal wake.
            // Direction = opposite gravity (so implicit velocity points WITH gravity).
            // Magnitude must exceed collision correction from pile neighbors.
            float nudgeMag = Mathf.Max(radiusMax * 10f, sleepVelocityThreshold * 20f);
            float gravLen = Mathf.Sqrt(gravity.x * gravity.x + gravity.y * gravity.y);
            cachedWakeNudge = gravLen > 1e-6f
                ? new float2(-gravity.x / gravLen * nudgeMag, -gravity.y / gravLen * nudgeMag)
                : new float2(0f, nudgeMag);

            // Bake funnel segments once with precomputed broadphase margin.
            // Margin must cover max displacement per substep to catch tunneling.
            if (funnel != null)
            {
                float subDt = Time.fixedDeltaTime / substeps;
                float gravMag = Mathf.Sqrt(gravity.x * gravity.x + gravity.y * gravity.y);
                float vMax = Mathf.Sqrt(2f * gravMag * spawnRange);
                float maxDisplacement = vMax * subDt + gravMag * subDt * subDt;
                float funnelMargin = Mathf.Max(radiusMax, maxDisplacement);
                funnel.BakeWithMargin(funnelMargin);
            }

            if (mode == SpawnMode.Burst)
                SpawnBurst();
        }

        private void FixedUpdate()
        {
            if (mode == SpawnMode.Stream)
                StreamSpawn();

            if (spawnedCount == 0) return;

            // When an obstacle is removed, wake ALL sleeping particles so they
            // can fall under gravity instead of floating mid-air. This is a
            // brute-force wake — O(n) once per removal event, not per frame.
            if (obstacleRegistry.ObstacleWasRemoved)
            {
                obstacleRegistry.ObstacleWasRemoved = false;
                WakeNearbyParticles();
            }

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

            // Query obstacle + funnel data once per frame — both are static between substeps.
            var (obstacleData, obstacleCount) = obstacleRegistry.GetObstacleData();

            // Funnel data is baked once at Start — cache locals to avoid
            // property accessor calls inside the substep loop.
            var funnelSegments = default(NativeArray<FunnelSegment>);
            int funnelSegmentCount = 0;
            float funnelFriction = 0f;
            float despawnY = float.NegativeInfinity;
            bool hasFunnel = funnel != null && funnel.IsBaked;
            if (hasFunnel)
            {
                funnelSegments = funnel.Segments;
                funnelSegmentCount = funnel.SegmentCount;
                funnelFriction = funnel.Friction;
                despawnY = funnel.DespawnBelowY;
            }

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

                // Resolve obstacles (Burst job) — after particle collisions, before funnel walls.
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

                // Resolve funnel walls (Burst job) — final safety clamp per substep.
                // Also resets sleepCounters for particles near walls (no-sleep zone)
                // to prevent sand from accumulating on inclined surfaces.
                if (funnelSegmentCount > 0)
                {
                    new SandPhysics.ResolveFunnelJob
                    {
                        particles = particles,
                        activeIndices = activeIndices,
                        awakeCount = awakeCount,
                        segments = funnelSegments,
                        segmentCount = funnelSegmentCount,
                        friction = funnelFriction,
                        noSleepDistance = radiusMax * 5f,
                        sleepCounters = sleepCounters,
                    }.Schedule().Complete();
                }
            }

            // Despawn particles below funnel spout (Burst job) — once per frame,
            // after all substeps. Teleport + sleep = zero ongoing cost.
            if (hasFunnel)
            {
                new SandPhysics.DespawnOutOfBoundsJob
                {
                    particles = particles,
                    activeIndices = activeIndices,
                    awakeCount = awakeCount,
                    isSleeping = isSleeping,
                    despawnY = despawnY,
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

        /// <summary>
        /// Stream mode: spawns particles continuously each FixedUpdate.
        /// No upper cap — NativeArrays grow automatically as needed.
        /// Despawn at funnel spout keeps the live count bounded in practice.
        /// </summary>
        private void StreamSpawn()
        {
            streamAccumulator += streamRate * Time.fixedDeltaTime;
            int toSpawn = (int)streamAccumulator;

            // Subtract integer part only — prevents float precision drift
            // from accumulating over long play sessions (hours on mobile).
            streamAccumulator -= toSpawn;
            if (streamAccumulator > streamRate) streamAccumulator = 0f;

            if (toSpawn <= 0) return;

            // Grow all parallel arrays if needed before spawning batch
            EnsureParticleCapacity(spawnedCount + toSpawn);

            float topY = spawnRange - radiusMax;

            for (int i = 0; i < toSpawn; i++)
            {
                float x = Random.Range(-streamSpawnWidth * 0.5f, streamSpawnWidth * 0.5f);
                SpawnParticleUnchecked(new float2(x, topY));
            }
        }

        /// <summary>
        /// Spawns a particle with capacity check. Used by Burst mode (fixed count).
        /// </summary>
        private void SpawnParticle(float2 position)
        {
            if (spawnedCount >= particleCapacity) return;
            SpawnParticleUnchecked(position);
        }

        /// <summary>
        /// Spawns a particle without capacity check — caller must ensure
        /// <see cref="EnsureParticleCapacity"/> was called first.
        /// Used by Stream mode after pre-growing arrays for the entire batch.
        /// </summary>
        private void SpawnParticleUnchecked(float2 position)
        {
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

        /// <summary>
        /// Wakes sleeping particles that were near the removed obstacle.
        /// Only wakes particles within the simulation bounds — despawned particles
        /// (teleported to 9999,9999) are skipped to avoid waking the entire pool.
        /// <para>
        /// Applies a small downward nudge (<c>prevPos</c> shifted opposite to gravity)
        /// to break equilibrium — without this, tightly packed particles wake with
        /// zero velocity, collision-correct each other in place, and immediately
        /// re-sleep as a floating cluster.
        /// </para>
        /// </summary>
        /// <summary>
        /// Wakes sleeping particles within simulation bounds on obstacle removal.
        /// Uses <see cref="cachedWakeNudge"/> (precomputed at Start) to break pile
        /// equilibrium — no per-call sqrt/normalize.
        /// </summary>
        private void WakeNearbyParticles()
        {
            float bound = spawnRange + 1f;
            float2 nudge = cachedWakeNudge;

            for (int i = 0; i < spawnedCount; i++)
            {
                if (!isSleeping[i]) continue;

                var p = particles[i];

                // Skip despawned particles (teleported far away)
                if (p.pos.x > bound || p.pos.x < -bound ||
                    p.pos.y > bound || p.pos.y < -bound)
                    continue;

                isSleeping[i] = false;
                sleepCounters[i] = 0;
                p.prevPos = p.pos + nudge;
                particles[i] = p;
            }

            needsRenderUpload = true;
        }

        /// <summary>
        /// Grows all parallel particle arrays if <paramref name="requiredCount"/> exceeds
        /// current capacity. Doubles capacity to amortize allocation cost — same pattern
        /// as <see cref="SpatialHash2D.EnsureCapacity"/>.
        /// <para>
        /// Copies existing data to the new arrays and disposes the old ones.
        /// Only called in Stream mode — Burst mode allocates exactly <c>maxParticles</c> once.
        /// </para>
        /// </summary>
        private void EnsureParticleCapacity(int requiredCount)
        {
            if (requiredCount <= particleCapacity) return;

            int newCapacity = particleCapacity;
            while (newCapacity < requiredCount)
                newCapacity *= 2;

            particles = GrowArray(particles, spawnedCount, newCapacity);
            isSleeping = GrowArray(isSleeping, spawnedCount, newCapacity);
            sleepCounters = GrowArray(sleepCounters, spawnedCount, newCapacity);
            activeIndices = GrowArray(activeIndices, spawnedCount, newCapacity);

            particleCapacity = newCapacity;
        }

        /// <summary>
        /// Allocates a new <see cref="NativeArray{T}"/> with <paramref name="newCapacity"/>,
        /// copies the first <paramref name="usedCount"/> elements, and disposes the old array.
        /// </summary>
        private static NativeArray<T> GrowArray<T>(NativeArray<T> old, int usedCount, int newCapacity)
            where T : struct
        {
            var grown = new NativeArray<T>(newCapacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            if (old.IsCreated && usedCount > 0)
                NativeArray<T>.Copy(old, grown, usedCount);
            if (old.IsCreated)
                old.Dispose();
            return grown;
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
