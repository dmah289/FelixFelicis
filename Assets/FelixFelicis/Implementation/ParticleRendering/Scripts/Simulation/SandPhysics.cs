using System.Runtime.CompilerServices;
using UnityEngine;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Stateless physics solver for falling sand.
    /// PBD with Verlet integration. All hot-path code uses inline float arithmetic
    /// and direct field access to avoid method call / struct copy overhead on Mono.
    /// </summary>
    public static class SandPhysics
    {
        /// <summary>
        /// Snapshot current positions before substeps begin.
        /// Sleep detection compares against this — not per-substep prevPos which
        /// includes micro-corrections from collision resolution.
        /// </summary>
        public static void SnapshotFrameStart(SandParticle[] p, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (p[i].isSleeping) continue;
                p[i].frameStartPos = p[i].pos;
            }
        }

        public static void Integrate(
            SandParticle[] p, int count,
            float gx, float gy, float dragMul)
        {
            for (int i = 0; i < count; i++)
            {
                if (p[i].isSleeping) continue;

                float vx = (p[i].pos.x - p[i].prevPos.x) * dragMul;
                float vy = (p[i].pos.y - p[i].prevPos.y) * dragMul;

                p[i].prevPos.x = p[i].pos.x;
                p[i].prevPos.y = p[i].pos.y;
                p[i].pos.x += vx + gx;
                p[i].pos.y += vy + gy;
            }
        }

        /// <summary>
        /// Resolve particle-particle collisions. All spatial hash fields accessed
        /// directly (internal) to avoid method call overhead in the innermost loop.
        /// </summary>
        public static void ResolveCollisions(
            SandParticle[] p, int count,
            SpatialHash2D hash,
            float frictionCoef, float contactDamping, float wakeOverlapFraction,
            float wakeSpeedSqr, int iterations)
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
                    if (p[i].isSleeping) continue;

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
                                if (j <= i && !p[j].isSleeping) continue;

                                float ddx = p[j].pos.x - px;
                                float ddy = p[j].pos.y - py;
                                float distSqr = ddx * ddx + ddy * ddy;
                                float minDist = ri + p[j].radius;

                                if (distSqr >= minDist * minDist || distSqr < 1e-10f)
                                    continue;

                                SolveContact(ref p[i], ref p[j],
                                    frictionCoef, contactDamping,
                                    wakeOverlapFraction, wakeSpeedSqr);
                            }
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SolveContact(
            ref SandParticle a, ref SandParticle b,
            float frictionCoef, float contactDamping,
            float wakeOverlapFraction, float wakeSpeedSqr)
        {
            float ddx = b.pos.x - a.pos.x;
            float ddy = b.pos.y - a.pos.y;
            float distSqr = ddx * ddx + ddy * ddy;
            float minDist = a.radius + b.radius;

            if (distSqr >= minDist * minDist || distSqr < 1e-10f) return;

            // Fast inverse sqrt: 1/sqrt via reciprocal
            float invDist = InvSqrt(distSqr);
            float dist = distSqr * invDist;
            float overlap = minDist - dist;
            float nnx = ddx * invDist;
            float nny = ddy * invDist;

            // Wake sleeping particle only if active particle has significant speed
            if (b.isSleeping)
            {
                float wakeThreshold = (a.radius < b.radius ? a.radius : b.radius) * wakeOverlapFraction;
                float avx = a.pos.x - a.prevPos.x;
                float avy = a.pos.y - a.prevPos.y;
                float aSpeedSqr = avx * avx + avy * avy;

                if (overlap > wakeThreshold && aSpeedSqr > wakeSpeedSqr)
                {
                    b.isSleeping = false;
                    b.sleepCounter = 0;
                    b.prevPos = b.pos;
                }
            }

            // Position correction
            if (b.isSleeping)
            {
                a.pos.x -= nnx * overlap;
                a.pos.y -= nny * overlap;
            }
            else
            {
                float half = overlap * 0.5f;
                a.pos.x -= nnx * half;
                a.pos.y -= nny * half;
                b.pos.x += nnx * half;
                b.pos.y += nny * half;
            }

            // Coulomb friction
            float relVx = (a.pos.x - a.prevPos.x) - (b.pos.x - b.prevPos.x);
            float relVy = (a.pos.y - a.prevPos.y) - (b.pos.y - b.prevPos.y);
            float relDotN = relVx * nnx + relVy * nny;
            float tx = relVx - relDotN * nnx;
            float ty = relVy - relDotN * nny;
            float tLenSqr = tx * tx + ty * ty;

            if (tLenSqr > 1e-12f)
            {
                float tLen = Mathf.Sqrt(tLenSqr);
                float maxF = frictionCoef * overlap;
                float corr = tLen < maxF ? tLen : maxF;
                float invT = 1f / tLen;
                float tdx = tx * invT;
                float tdy = ty * invT;

                if (b.isSleeping)
                {
                    a.pos.x -= tdx * corr;
                    a.pos.y -= tdy * corr;
                }
                else
                {
                    float hc = corr * 0.5f;
                    a.pos.x -= tdx * hc;
                    a.pos.y -= tdy * hc;
                    b.pos.x += tdx * hc;
                    b.pos.y += tdy * hc;
                }
            }

            // Contact damping
            if (!b.isSleeping)
            {
                float dh = relDotN * contactDamping * 0.5f;
                a.prevPos.x += nnx * dh;
                a.prevPos.y += nny * dh;
                b.prevPos.x -= nnx * dh;
                b.prevPos.y -= nny * dh;
            }
        }

        public static void ResolveBoundaries(
            SandParticle[] p, int count,
            float boundsX, float boundsY, float wallFriction)
        {
            for (int i = 0; i < count; i++)
            {
                if (p[i].isSleeping) continue;

                float r = p[i].radius;

                // Floor
                float floorY = -boundsY + r;
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
                float leftX = -boundsX + r;
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

        /// <summary>
        /// Sleep detection based on total frame displacement (frameStartPos → pos)
        /// instead of per-substep velocity (prevPos → pos). This prevents
        /// micro-corrections from collision resolution from keeping particles awake.
        /// </summary>
        public static void UpdateSleep(
            SandParticle[] p, int count,
            float sleepThresholdSqr, int sleepFrames)
        {
            for (int i = 0; i < count; i++)
            {
                if (p[i].isSleeping) continue;

                // Total displacement this FixedUpdate frame
                float dx = p[i].pos.x - p[i].frameStartPos.x;
                float dy = p[i].pos.y - p[i].frameStartPos.y;
                float dispSqr = dx * dx + dy * dy;

                if (dispSqr < sleepThresholdSqr)
                {
                    p[i].sleepCounter++;
                    if (p[i].sleepCounter >= sleepFrames)
                    {
                        p[i].isSleeping = true;
                        p[i].prevPos = p[i].pos;
                    }
                }
                else
                {
                    p[i].sleepCounter = 0;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float InvSqrt(float x)
        {
            return 1f / Mathf.Sqrt(x);
        }
    }
}
