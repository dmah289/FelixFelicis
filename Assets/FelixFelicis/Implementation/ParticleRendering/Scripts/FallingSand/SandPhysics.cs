using System.Runtime.CompilerServices;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Stateless physics solver for falling sand.
    /// PBD with Verlet integration. All hot-path code uses inline float arithmetic
    /// and direct field access to avoid method call / struct copy overhead on Mono.
    /// <para>
    /// Slope behavior: height-biased position correction + slope-dependent friction
    /// produce natural gentle sand dunes (~20-25° angle of repose).
    /// </para>
    /// <para>
    /// Sleep state stored in parallel arrays (<c>isSleeping[]</c>, <c>sleepCounters[]</c>)
    /// for cache-friendly skip checks. Active-index iteration skips sleeping particles
    /// in O(awake) instead of O(n) for non-collision loops.
    /// </para>
    /// </summary>
    public static class SandPhysics
    {
        /// <summary>
        /// Scans <paramref name="isSleeping"/> and writes indices of awake particles
        /// into <paramref name="activeIndices"/>. Returns the awake count.
        /// O(n) scan with excellent cache behavior (64 bools per cache line).
        /// </summary>
        public static int BuildActiveIndices(bool[] isSleeping, int count, int[] activeIndices)
        {
            int awake = 0;
            for (int i = 0; i < count; i++)
            {
                if (!isSleeping[i])
                    activeIndices[awake++] = i;
            }
            return awake;
        }

        public static void SnapshotFrameStart(
            SandParticle[] p, int[] activeIndices, int awakeCount)
        {
            for (int a = 0; a < awakeCount; a++)
            {
                int i = activeIndices[a];
                p[i].frameStartPos = p[i].pos;
            }
        }

        public static void Integrate(
            SandParticle[] p, int[] activeIndices, int awakeCount,
            float gx, float gy, float dragMul)
        {
            for (int a = 0; a < awakeCount; a++)
            {
                int i = activeIndices[a];

                float vx = (p[i].pos.x - p[i].prevPos.x) * dragMul;
                float vy = (p[i].pos.y - p[i].prevPos.y) * dragMul;

                p[i].prevPos.x = p[i].pos.x;
                p[i].prevPos.y = p[i].pos.y;
                p[i].pos.x += vx + gx;
                p[i].pos.y += vy + gy;
            }
        }

        /// <summary>
        /// Fully inlined collision resolution — no method call for SolveContact.
        /// On Mono (Unity Editor + Android IL2CPP debug), method calls with ref struct
        /// params have significant overhead even with AggressiveInlining.
        /// <para>
        /// Slope mechanics: height-biased correction ratio pushes upper particles more,
        /// and slope-dependent friction lets them slide off vertical stacks easily.
        /// </para>
        /// <para>
        /// Outer loop preserves <c>for i=0..count</c> order for exact pair processing.
        /// Sleep checks use compact <c>isSleeping[]</c> array (1 byte per check vs 36-byte
        /// struct load). Wake events tracked via <paramref name="wakeOccurred"/> so caller
        /// can rebuild active indices.
        /// </para>
        /// </summary>
        public static void ResolveCollisions(
            SandParticle[] p, int count,
            SpatialHash2D hash,
            bool[] isSleeping, byte[] sleepCounters,
            float frictionCoef, float contactDamping,
            float wakeOverlapFraction, float wakeSpeedSqr,
            float slopeBias, float slopeFrictionReduction,
            int iterations,
            ref bool wakeOccurred)
        {
            int[] sorted = hash.sortedIndices;
            int[] offsets = hash.cellOffsets;
            int[] counts = hash.cellCounts;
            float inv = hash.invCellSize;
            float ox = hash.originX;
            float oy = hash.originY;
            int gw = hash.gridWidth;
            int gwM1 = gw - 1;
            int ghM1 = hash.gridHeight - 1;

            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < count; i++)
                {
                    if (isSleeping[i]) continue;

                    float px = p[i].pos.x;
                    float py = p[i].pos.y;
                    float ri = p[i].radius;

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
                            int off = offsets[cell];
                            int cnt = counts[cell];

                            for (int k = 0; k < cnt; k++)
                            {
                                int j = sorted[off + k];
                                if (j <= i && !isSleeping[j]) continue;

                                float ddx = p[j].pos.x - px;
                                float ddy = p[j].pos.y - py;
                                float distSqr = ddx * ddx + ddy * ddy;
                                float minDist = ri + p[j].radius;

                                if (distSqr >= minDist * minDist || distSqr < 1e-10f)
                                    continue;

                                // ── INLINE SolveContact ──────────────────────

                                float invDist = FastInvSqrt(distSqr);
                                float dist = distSqr * invDist;
                                float overlap = minDist - dist;
                                float nnx = ddx * invDist;
                                float nny = ddy * invDist;

                                // How vertical is this contact (0 = horizontal, 1 = vertical)
                                float verticalness = nny * nny; // squared abs

                                // Wake: only if active particle has significant speed
                                if (isSleeping[j])
                                {
                                    float wt = (ri < p[j].radius ? ri : p[j].radius) * wakeOverlapFraction;
                                    float avx = p[i].pos.x - p[i].prevPos.x;
                                    float avy = p[i].pos.y - p[i].prevPos.y;

                                    if (overlap > wt && avx * avx + avy * avy > wakeSpeedSqr)
                                    {
                                        isSleeping[j] = false;
                                        sleepCounters[j] = 0;
                                        p[j].prevPos = p[j].pos;
                                        wakeOccurred = true;
                                    }
                                }

                                // Position correction with height-biased ratio:
                                // Upper particle gets pushed more → slides off → gentle slopes
                                if (isSleeping[j])
                                {
                                    p[i].pos.x -= nnx * overlap;
                                    p[i].pos.y -= nny * overlap;
                                }
                                else
                                {
                                    // i is above j → ratioI > 0.5 → i gets pushed more
                                    float bias = p[i].pos.y > p[j].pos.y
                                        ? 0.5f + slopeBias
                                        : 0.5f - slopeBias;
                                    float pushI = overlap * bias;
                                    float pushJ = overlap - pushI;

                                    p[i].pos.x -= nnx * pushI;
                                    p[i].pos.y -= nny * pushI;
                                    p[j].pos.x += nnx * pushJ;
                                    p[j].pos.y += nny * pushJ;
                                }

                                // Coulomb friction — reduced for vertical contacts
                                // (particles stacked on top slide easier than side-by-side)
                                float effFriction = frictionCoef * (1f - verticalness * slopeFrictionReduction);

                                float relVx = (p[i].pos.x - p[i].prevPos.x) - (p[j].pos.x - p[j].prevPos.x);
                                float relVy = (p[i].pos.y - p[i].prevPos.y) - (p[j].pos.y - p[j].prevPos.y);
                                float relDotN = relVx * nnx + relVy * nny;
                                float ttx = relVx - relDotN * nnx;
                                float tty = relVy - relDotN * nny;
                                float tLenSqr = ttx * ttx + tty * tty;

                                float maxF = effFriction * overlap;
                                if (tLenSqr > maxF * maxF * 0.01f) // skip if negligible
                                {
                                    float tLen = FastSqrt(tLenSqr);
                                    float corr = tLen < maxF ? tLen : maxF;
                                    float invT = 1f / tLen;
                                    float tdx = ttx * invT;
                                    float tdy = tty * invT;

                                    if (isSleeping[j])
                                    {
                                        p[i].pos.x -= tdx * corr;
                                        p[i].pos.y -= tdy * corr;
                                    }
                                    else
                                    {
                                        float hc = corr * 0.5f;
                                        p[i].pos.x -= tdx * hc;
                                        p[i].pos.y -= tdy * hc;
                                        p[j].pos.x += tdx * hc;
                                        p[j].pos.y += tdy * hc;
                                    }
                                }

                                // Contact damping
                                if (!isSleeping[j])
                                {
                                    float dh = relDotN * contactDamping * 0.5f;
                                    p[i].prevPos.x += nnx * dh;
                                    p[i].prevPos.y += nny * dh;
                                    p[j].prevPos.x -= nnx * dh;
                                    p[j].prevPos.y -= nny * dh;
                                }

                                // Re-cache pos after correction (used by next neighbor check)
                                px = p[i].pos.x;
                                py = p[i].pos.y;

                                // ── END INLINE ──────────────────────────────
                            }
                        }
                    }
                }
            }
        }

        public static void ResolveBoundaries(
            SandParticle[] p, int[] activeIndices, int awakeCount,
            float boundsX, float boundsY, float wallFriction)
        {
            float negBX = -boundsX;
            float negBY = -boundsY;

            for (int a = 0; a < awakeCount; a++)
            {
                int i = activeIndices[a];
                float r = p[i].radius;

                // Floor
                float floorY = negBY + r;
                if (p[i].pos.y < floorY)
                {
                    float pen = floorY - p[i].pos.y;
                    float hVel = p[i].pos.x - p[i].prevPos.x;
                    float maxF = wallFriction * pen;
                    if (hVel > maxF) p[i].prevPos.x += maxF;
                    else if (hVel < -maxF) p[i].prevPos.x -= maxF;
                    else p[i].prevPos.x += hVel;

                    p[i].pos.y = floorY;
                    p[i].prevPos.y = floorY;
                }

                // Ceiling
                float ceilY = boundsY - r;
                if (p[i].pos.y > ceilY)
                {
                    p[i].pos.y = ceilY;
                    p[i].prevPos.y = ceilY;
                }

                // Left wall
                float leftX = negBX + r;
                if (p[i].pos.x < leftX)
                {
                    float pen = leftX - p[i].pos.x;
                    float vVel = p[i].pos.y - p[i].prevPos.y;
                    float maxF = wallFriction * pen;
                    if (vVel > maxF) p[i].prevPos.y += maxF;
                    else if (vVel < -maxF) p[i].prevPos.y -= maxF;
                    else p[i].prevPos.y += vVel;

                    p[i].pos.x = leftX;
                    p[i].prevPos.x = leftX;
                }

                // Right wall
                float rightX = boundsX - r;
                if (p[i].pos.x > rightX)
                {
                    float pen = p[i].pos.x - rightX;
                    float vVel = p[i].pos.y - p[i].prevPos.y;
                    float maxF = wallFriction * pen;
                    if (vVel > maxF) p[i].prevPos.y += maxF;
                    else if (vVel < -maxF) p[i].prevPos.y -= maxF;
                    else p[i].prevPos.y += vVel;

                    p[i].pos.x = rightX;
                    p[i].prevPos.x = rightX;
                }
            }
        }

        public static void UpdateSleep(
            SandParticle[] p, int[] activeIndices, int awakeCount,
            bool[] isSleeping, byte[] sleepCounters,
            float sleepThresholdSqr, int sleepFrames)
        {
            for (int a = 0; a < awakeCount; a++)
            {
                int i = activeIndices[a];

                float dx = p[i].pos.x - p[i].frameStartPos.x;
                float dy = p[i].pos.y - p[i].frameStartPos.y;

                if (dx * dx + dy * dy < sleepThresholdSqr)
                {
                    sleepCounters[i]++;
                    if (sleepCounters[i] >= sleepFrames)
                    {
                        isSleeping[i] = true;
                        p[i].prevPos = p[i].pos;
                    }
                }
                else
                {
                    sleepCounters[i] = 0;
                }
            }
        }

        // ── Fast math (avoid Mathf wrapper overhead on Mono) ────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastInvSqrt(float x)
        {
            // Quake-style fast inverse sqrt with one Newton-Raphson iteration.
            // ~2x faster than 1f/Mathf.Sqrt(x) on Mono.
            float half = 0.5f * x;
            int bits = System.BitConverter.SingleToInt32Bits(x);
            bits = 0x5F3759DF - (bits >> 1);
            float y = System.BitConverter.Int32BitsToSingle(bits);
            y *= 1.5f - half * y * y; // one Newton-Raphson iteration
            return y;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float FastSqrt(float x)
        {
            return x * FastInvSqrt(x);
        }
    }
}
