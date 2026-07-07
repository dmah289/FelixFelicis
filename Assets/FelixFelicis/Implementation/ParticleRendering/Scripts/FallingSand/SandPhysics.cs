using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Burst-compiled physics jobs for falling sand.
    /// PBD with Verlet integration. All jobs implement IJob (sequential, not parallel).
    /// <para>
    /// Slope behavior: height-biased position correction + slope-dependent friction
    /// produce natural gentle sand dunes (~20-25° angle of repose).
    /// </para>
    /// </summary>
    public static class SandPhysics
    {
        // ── BuildActiveIndicesJob ─────────────────────────────────────

        /// <summary>
        /// Scans <see cref="isSleeping"/> and writes indices of awake particles
        /// into <see cref="activeIndices"/>. Writes awake count to <see cref="awakeCount"/>.
        /// O(n) scan with excellent cache behavior (64 bools per cache line).
        /// </summary>
        [BurstCompile]
        public struct BuildActiveIndicesJob : IJob
        {
            [ReadOnly] public NativeArray<bool> isSleeping;
            public int count;
            [WriteOnly] public NativeArray<int> activeIndices;
            public NativeReference<int> awakeCount;

            public void Execute()
            {
                int awake = 0;
                for (int i = 0; i < count; i++)
                {
                    if (!isSleeping[i])
                        activeIndices[awake++] = i;
                }
                awakeCount.Value = awake;
            }
        }

        // ── SnapshotFrameStartJob ─────────────────────────────────────

        [BurstCompile]
        public struct SnapshotFrameStartJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;

            public void Execute()
            {
                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];
                    p.frameStartPos = p.pos;
                    particles[i] = p;
                }
            }
        }

        // ── IntegrateJob ──────────────────────────────────────────────

        [BurstCompile]
        public struct IntegrateJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;
            public float gx;
            public float gy;
            public float dragMul;

            public void Execute()
            {
                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];

                    float vx = (p.pos.x - p.prevPos.x) * dragMul;
                    float vy = (p.pos.y - p.prevPos.y) * dragMul;

                    p.prevPos = p.pos;
                    p.pos.x += vx + gx;
                    p.pos.y += vy + gy;

                    particles[i] = p;
                }
            }
        }

        // ── SpatialHashBuildJob ───────────────────────────────────────

        /// <summary>
        /// 3-pass O(n) counting sort. Replaces <c>SpatialHash2D.Build()</c>.
        /// Pass 1 caches cell index per particle to avoid recomputing in pass 3.
        /// </summary>
        [BurstCompile]
        public struct SpatialHashBuildJob : IJob
        {
            [ReadOnly] public NativeArray<SandParticle> particles;
            public int count;

            // Grid parameters (copied from SpatialHash2D)
            public float invCellSize;
            public float originX;
            public float originY;
            public int gridWidth;
            public int gridHeight;
            public int cellCount;

            // Output arrays (from SpatialHash2D)
            public NativeArray<int> cellCounts;
            public NativeArray<int> cellOffsets;
            public NativeArray<int> sortedIndices;
            public NativeArray<int> particleCells;

            public void Execute()
            {
                int gw = gridWidth;
                int gwM1 = gw - 1;
                int ghM1 = gridHeight - 1;
                float inv = invCellSize;
                float ox = originX;
                float oy = originY;

                // Pass 1: compute cell index + count
                for (int i = 0; i < cellCount; i++)
                    cellCounts[i] = 0;

                for (int i = 0; i < count; i++)
                {
                    int cx = (int)((particles[i].pos.x - ox) * inv);
                    if (cx < 0) cx = 0; else if (cx > gwM1) cx = gwM1;
                    int cy = (int)((particles[i].pos.y - oy) * inv);
                    if (cy < 0) cy = 0; else if (cy > ghM1) cy = ghM1;
                    int cell = cy * gw + cx;
                    particleCells[i] = cell;
                    cellCounts[cell] = cellCounts[cell] + 1;
                }

                // Pass 2: prefix-sum → offsets
                cellOffsets[0] = 0;
                for (int i = 1; i < cellCount; i++)
                    cellOffsets[i] = cellOffsets[i - 1] + cellCounts[i - 1];

                // Pass 3: scatter using cached cell indices
                for (int i = 0; i < count; i++)
                {
                    int cell = particleCells[i];
                    int newCount = cellCounts[cell] - 1;
                    sortedIndices[cellOffsets[cell] + newCount] = i;
                    cellCounts[cell] = newCount;
                }

                // Restore cellCounts from offsets
                for (int i = 0; i < cellCount - 1; i++)
                    cellCounts[i] = cellOffsets[i + 1] - cellOffsets[i];
                cellCounts[cellCount - 1] = count - cellOffsets[cellCount - 1];
            }
        }

        // ── ResolveCollisionsJob ──────────────────────────────────────

        /// <summary>
        /// Fully inlined collision resolution with O(awake) outer loop.
        /// Iterates <see cref="activeIndices"/> instead of 0..count.
        /// <para>
        /// Pair rule: <c>if (j == i) continue; if (!isSleeping[j] &amp;&amp; j &lt; i) continue;</c>
        /// — equivalent to the original <c>j &lt;= i &amp;&amp; !isSleeping[j]</c> but works
        /// with activeIndices iteration.
        /// </para>
        /// </summary>
        [BurstCompile]
        public struct ResolveCollisionsJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;

            // Spatial hash data
            [ReadOnly] public NativeArray<int> sortedIndices;
            [ReadOnly] public NativeArray<int> cellOffsets;
            [ReadOnly] public NativeArray<int> cellCounts;
            public float invCellSize;
            public float originX;
            public float originY;
            public int gridWidth;
            public int gridHeight;

            // Sleep arrays (read + write for wake)
            public NativeArray<bool> isSleeping;
            public NativeArray<byte> sleepCounters;

            // Physics parameters
            public float frictionCoef;
            public float contactDamping;
            public float wakeOverlapFraction;
            public float wakeSpeedSqr;
            public float slopeBias;
            public float slopeFrictionReduction;
            public int iterations;

            // Output: did any sleeping particle wake up?
            public NativeReference<bool> wakeOccurred;

            public void Execute()
            {
                int gwM1 = gridWidth - 1;
                int ghM1 = gridHeight - 1;
                int gw = gridWidth;
                float inv = invCellSize;
                float ox = originX;
                float oy = originY;

                for (int iter = 0; iter < iterations; iter++)
                {
                    for (int a = 0; a < awakeCount; a++)
                    {
                        int i = activeIndices[a];
                        var pi = particles[i];

                        float px = pi.pos.x;
                        float py = pi.pos.y;
                        float ri = pi.radius;

                        int cx = (int)((px - ox) * inv);
                        if (cx < 0) cx = 0; else if (cx > gwM1) cx = gwM1;
                        int cy = (int)((py - oy) * inv);
                        if (cy < 0) cy = 0; else if (cy > ghM1) cy = ghM1;

                        int cyMin = cy > 0 ? cy - 1 : 0;
                        int cyMax = cy < ghM1 ? cy + 1 : ghM1;
                        int cxMin = cx > 0 ? cx - 1 : 0;
                        int cxMax = cx < gwM1 ? cx + 1 : gwM1;

                        for (int ny = cyMin; ny <= cyMax; ny++)
                        {
                            int rowBase = ny * gw;
                            for (int nx = cxMin; nx <= cxMax; nx++)
                            {
                                int cell = rowBase + nx;
                                int off = cellOffsets[cell];
                                int cnt = cellCounts[cell];

                                for (int k = 0; k < cnt; k++)
                                {
                                    int j = sortedIndices[off + k];
                                    if (j == i) continue;
                                    if (!isSleeping[j] && j < i) continue;

                                    var pj = particles[j];

                                    float ddx = pj.pos.x - px;
                                    float ddy = pj.pos.y - py;
                                    float distSqr = ddx * ddx + ddy * ddy;
                                    float minDist = ri + pj.radius;

                                    if (distSqr >= minDist * minDist || distSqr < 1e-10f)
                                        continue;

                                    // ── INLINE SolveContact ──────────────

                                    float invDist = math.rsqrt(distSqr);
                                    float dist = distSqr * invDist;
                                    float overlap = minDist - dist;
                                    float nnx = ddx * invDist;
                                    float nny = ddy * invDist;

                                    float verticalness = nny * nny;

                                    // Wake check
                                    bool jSleeping = isSleeping[j];
                                    if (jSleeping)
                                    {
                                        float wt = (ri < pj.radius ? ri : pj.radius) * wakeOverlapFraction;
                                        float avx = pi.pos.x - pi.prevPos.x;
                                        float avy = pi.pos.y - pi.prevPos.y;

                                        if (overlap > wt && avx * avx + avy * avy > wakeSpeedSqr)
                                        {
                                            jSleeping = false;
                                            isSleeping[j] = false;
                                            sleepCounters[j] = 0;
                                            pj.prevPos = pj.pos;
                                            wakeOccurred.Value = true;
                                        }
                                    }

                                    // Position correction
                                    if (jSleeping)
                                    {
                                        pi.pos.x -= nnx * overlap;
                                        pi.pos.y -= nny * overlap;
                                    }
                                    else
                                    {
                                        float bias = pi.pos.y > pj.pos.y
                                            ? 0.5f + slopeBias
                                            : 0.5f - slopeBias;
                                        float pushI = overlap * bias;
                                        float pushJ = overlap - pushI;

                                        pi.pos.x -= nnx * pushI;
                                        pi.pos.y -= nny * pushI;
                                        pj.pos.x += nnx * pushJ;
                                        pj.pos.y += nny * pushJ;
                                    }

                                    // Coulomb friction
                                    float effFriction = frictionCoef * (1f - verticalness * slopeFrictionReduction);

                                    float relVx = (pi.pos.x - pi.prevPos.x) - (pj.pos.x - pj.prevPos.x);
                                    float relVy = (pi.pos.y - pi.prevPos.y) - (pj.pos.y - pj.prevPos.y);
                                    float relDotN = relVx * nnx + relVy * nny;
                                    float ttx = relVx - relDotN * nnx;
                                    float tty = relVy - relDotN * nny;
                                    float tLenSqr = ttx * ttx + tty * tty;

                                    float maxF = effFriction * overlap;
                                    if (tLenSqr > maxF * maxF * 0.01f)
                                    {
                                        float tLen = math.sqrt(tLenSqr);
                                        float corr = tLen < maxF ? tLen : maxF;
                                        float invT = 1f / tLen;
                                        float tdx = ttx * invT;
                                        float tdy = tty * invT;

                                        if (jSleeping)
                                        {
                                            pi.pos.x -= tdx * corr;
                                            pi.pos.y -= tdy * corr;
                                        }
                                        else
                                        {
                                            float hc = corr * 0.5f;
                                            pi.pos.x -= tdx * hc;
                                            pi.pos.y -= tdy * hc;
                                            pj.pos.x += tdx * hc;
                                            pj.pos.y += tdy * hc;
                                        }
                                    }

                                    // Contact damping
                                    if (!jSleeping)
                                    {
                                        float dh = relDotN * contactDamping * 0.5f;
                                        pi.prevPos.x += nnx * dh;
                                        pi.prevPos.y += nny * dh;
                                        pj.prevPos.x -= nnx * dh;
                                        pj.prevPos.y -= nny * dh;
                                    }

                                    // Write back j
                                    particles[j] = pj;

                                    // Re-cache pos after correction
                                    px = pi.pos.x;
                                    py = pi.pos.y;

                                    // ── END INLINE ───────────────────────
                                }
                            }
                        }

                        // Write back i after all neighbor checks
                        particles[i] = pi;
                    }
                }
            }
        }

        // ── ResolveBoundariesJob ──────────────────────────────────────

        [BurstCompile]
        public struct ResolveBoundariesJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;
            public float boundsX;
            public float boundsY;
            public float wallFriction;

            public void Execute()
            {
                float negBX = -boundsX;
                float negBY = -boundsY;

                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];
                    float r = p.radius;

                    // Floor
                    float floorY = negBY + r;
                    if (p.pos.y < floorY)
                    {
                        float pen = floorY - p.pos.y;
                        float hVel = p.pos.x - p.prevPos.x;
                        float maxF = wallFriction * pen;
                        if (hVel > maxF) p.prevPos.x += maxF;
                        else if (hVel < -maxF) p.prevPos.x -= maxF;
                        else p.prevPos.x += hVel;

                        p.pos.y = floorY;
                        p.prevPos.y = floorY;
                    }

                    // Ceiling
                    float ceilY = boundsY - r;
                    if (p.pos.y > ceilY)
                    {
                        p.pos.y = ceilY;
                        p.prevPos.y = ceilY;
                    }

                    // Left wall
                    float leftX = negBX + r;
                    if (p.pos.x < leftX)
                    {
                        float pen = leftX - p.pos.x;
                        float vVel = p.pos.y - p.prevPos.y;
                        float maxF = wallFriction * pen;
                        if (vVel > maxF) p.prevPos.y += maxF;
                        else if (vVel < -maxF) p.prevPos.y -= maxF;
                        else p.prevPos.y += vVel;

                        p.pos.x = leftX;
                        p.prevPos.x = leftX;
                    }

                    // Right wall
                    float rightX = boundsX - r;
                    if (p.pos.x > rightX)
                    {
                        float pen = p.pos.x - rightX;
                        float vVel = p.pos.y - p.prevPos.y;
                        float maxF = wallFriction * pen;
                        if (vVel > maxF) p.prevPos.y += maxF;
                        else if (vVel < -maxF) p.prevPos.y -= maxF;
                        else p.prevPos.y += vVel;

                        p.pos.x = rightX;
                        p.prevPos.x = rightX;
                    }

                    particles[i] = p;
                }
            }
        }

        // ── UpdateSleep (managed — not a job) ─────────────────────────

        /// <summary>
        /// Managed sleep update — runs once per frame after all substeps.
        /// Not a Burst job: O(awake), not hot path, needs immediate result
        /// for <c>needsRenderUpload</c> decision.
        /// </summary>
        public static void UpdateSleep(
            NativeArray<SandParticle> particles,
            NativeArray<int> activeIndices, int awakeCount,
            NativeArray<bool> isSleeping, NativeArray<byte> sleepCounters,
            float sleepThresholdSqr, int sleepFrames)
        {
            for (int a = 0; a < awakeCount; a++)
            {
                int i = activeIndices[a];
                var p = particles[i];

                float dx = p.pos.x - p.frameStartPos.x;
                float dy = p.pos.y - p.frameStartPos.y;

                if (dx * dx + dy * dy < sleepThresholdSqr)
                {
                    byte counter = (byte)(sleepCounters[i] + 1);
                    sleepCounters[i] = counter;
                    if (counter >= sleepFrames)
                    {
                        isSleeping[i] = true;
                        p.prevPos = p.pos;
                        particles[i] = p;
                    }
                }
                else
                {
                    sleepCounters[i] = 0;
                }
            }
        }
    }
}
