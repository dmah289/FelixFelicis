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
    /// <para>
    /// All physics jobs use <c>FloatMode.Fast</c> — allows Burst to reorder float
    /// operations and use approximate intrinsics (e.g. <c>rsqrt</c>). Acceptable for
    /// game physics where exact IEEE 754 compliance is not required.
    /// </para>
    /// </summary>
    public static class SandPhysics
    {
        /// <summary>
        /// Minimum squared tangent velocity to apply friction correction.
        /// Below this threshold, the friction force is negligible and the <c>1/tLen</c>
        /// division approaches instability. Value: 1% of max friction force squared.
        /// </summary>
        private const float FrictionDeadZoneFraction = 0.01f;

        // ── BuildActiveIndicesJob ─────────────────────────────────────

        /// <summary>
        /// Scans <see cref="isSleeping"/> and writes indices of awake particles
        /// into <see cref="activeIndices"/>. Writes awake count to <see cref="awakeCount"/>.
        /// O(n) scan with excellent cache behavior (64 bools per cache line).
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        public struct BuildActiveIndicesJob : IJob
        {
            [ReadOnly] public NativeArray<bool> isSleeping;
            public int particleCount;
            [WriteOnly] public NativeArray<int> activeIndices;
            public NativeReference<int> awakeCount;

            public void Execute()
            {
                int awake = 0;
                for (int i = 0; i < particleCount; i++)
                {
                    if (!isSleeping[i])
                        activeIndices[awake++] = i;
                }
                awakeCount.Value = awake;
            }
        }

        // ── SnapshotFrameStartJob ─────────────────────────────────────

        [BurstCompile(FloatMode = FloatMode.Fast)]
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

        [BurstCompile(FloatMode = FloatMode.Fast)]
        public struct IntegrateJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;
            public float gravityDtSqX;
            public float gravityDtSqY;
            public float dragMultiplier;

            public void Execute()
            {
                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];

                    float vx = (p.pos.x - p.prevPos.x) * dragMultiplier;
                    float vy = (p.pos.y - p.prevPos.y) * dragMultiplier;

                    p.prevPos = p.pos;
                    p.pos.x += vx + gravityDtSqX;
                    p.pos.y += vy + gravityDtSqY;

                    particles[i] = p;
                }
            }
        }

        // ── SpatialHashBuildJob ───────────────────────────────────────

        /// <summary>
        /// 3-pass O(n) counting sort. Replaces <c>SpatialHash2D.Build()</c>.
        /// Pass 1 caches cell index per particle to avoid recomputing in pass 3.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        public struct SpatialHashBuildJob : IJob
        {
            [ReadOnly] public NativeArray<SandParticle> particles;
            public int particleCount;

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

                for (int i = 0; i < particleCount; i++)
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
                for (int i = 0; i < particleCount; i++)
                {
                    int cell = particleCells[i];
                    int newCount = cellCounts[cell] - 1;
                    sortedIndices[cellOffsets[cell] + newCount] = i;
                    cellCounts[cell] = newCount;
                }

                // Restore cellCounts from offsets
                for (int i = 0; i < cellCount - 1; i++)
                    cellCounts[i] = cellOffsets[i + 1] - cellOffsets[i];
                cellCounts[cellCount - 1] = particleCount - cellOffsets[cellCount - 1];
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
        [BurstCompile(FloatMode = FloatMode.Fast)]
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

                                    float deltaX = pj.pos.x - px;
                                    float deltaY = pj.pos.y - py;
                                    float distSqr = deltaX * deltaX + deltaY * deltaY;
                                    float minDist = ri + pj.radius;

                                    if (distSqr >= minDist * minDist || distSqr < 1e-10f)
                                        continue;

                                    // ── INLINE SolveContact ──────────────
                                    // Inlined because Burst IJob with ref struct params
                                    // has significant call overhead even with [AggressiveInlining].

                                    float invDist = math.rsqrt(distSqr);
                                    float dist = distSqr * invDist;
                                    float overlap = minDist - dist;
                                    float normalX = deltaX * invDist;
                                    float normalY = deltaY * invDist;

                                    // verticalness: 0 = horizontal contact, 1 = vertical contact.
                                    // Squared instead of abs — branchless, smooth derivative at 0.
                                    float verticalness = normalY * normalY;

                                    // ── Phase 0: Wake sleeping particle if hit hard enough ──
                                    bool jSleeping = isSleeping[j];
                                    if (jSleeping)
                                    {
                                        float wakeThreshold = (ri < pj.radius ? ri : pj.radius) * wakeOverlapFraction;
                                        float velIX = pi.pos.x - pi.prevPos.x;
                                        float velIY = pi.pos.y - pi.prevPos.y;

                                        if (overlap > wakeThreshold && velIX * velIX + velIY * velIY > wakeSpeedSqr)
                                        {
                                            jSleeping = false;
                                            isSleeping[j] = false;
                                            sleepCounters[j] = 0;
                                            pj.prevPos = pj.pos; // zero velocity on wake
                                            wakeOccurred.Value = true;
                                        }
                                    }

                                    // ── Phase 1: Position correction (resolve overlap) ──
                                    // Sleeping j is immovable — i absorbs full correction.
                                    // Both active — height-biased split for natural slope formation.
                                    if (jSleeping)
                                    {
                                        pi.pos.x -= normalX * overlap;
                                        pi.pos.y -= normalY * overlap;
                                    }
                                    else
                                    {
                                        float bias = pi.pos.y > pj.pos.y
                                            ? 0.5f + slopeBias    // i is above → receives more push
                                            : 0.5f - slopeBias;   // i is below → receives less push
                                        float pushI = overlap * bias;
                                        float pushJ = overlap - pushI;

                                        pi.pos.x -= normalX * pushI;
                                        pi.pos.y -= normalY * pushI;
                                        pj.pos.x += normalX * pushJ;
                                        pj.pos.y += normalY * pushJ;
                                    }

                                    // ── Phase 2: Coulomb friction (tangential) ──
                                    // Vertical contacts slide easier → natural angle of repose.
                                    float effFriction = frictionCoef * (1f - verticalness * slopeFrictionReduction);

                                    float relVelX = (pi.pos.x - pi.prevPos.x) - (pj.pos.x - pj.prevPos.x);
                                    float relVelY = (pi.pos.y - pi.prevPos.y) - (pj.pos.y - pj.prevPos.y);
                                    float relDotNormal = relVelX * normalX + relVelY * normalY;
                                    float tangentX = relVelX - relDotNormal * normalX;
                                    float tangentY = relVelY - relDotNormal * normalY;
                                    float tangentLenSqr = tangentX * tangentX + tangentY * tangentY;

                                    float maxFriction = effFriction * overlap;
                                    if (tangentLenSqr > maxFriction * maxFriction * FrictionDeadZoneFraction)
                                    {
                                        float tangentLen = math.sqrt(tangentLenSqr);
                                        float correction = tangentLen < maxFriction ? tangentLen : maxFriction;
                                        float invTangentLen = 1f / tangentLen;
                                        float tangentDirX = tangentX * invTangentLen;
                                        float tangentDirY = tangentY * invTangentLen;

                                        if (jSleeping)
                                        {
                                            pi.pos.x -= tangentDirX * correction;
                                            pi.pos.y -= tangentDirY * correction;
                                        }
                                        else
                                        {
                                            float halfCorrection = correction * 0.5f;
                                            pi.pos.x -= tangentDirX * halfCorrection;
                                            pi.pos.y -= tangentDirY * halfCorrection;
                                            pj.pos.x += tangentDirX * halfCorrection;
                                            pj.pos.y += tangentDirY * halfCorrection;
                                        }
                                    }

                                    // ── Phase 3: Contact damping (reduce bounce via prevPos) ──
                                    // Modifies prevPos so implicit velocity (pos - prevPos) decreases
                                    // along the contact normal. Only for active-active pairs.
                                    if (!jSleeping)
                                    {
                                        float dampingHalf = relDotNormal * contactDamping * 0.5f;
                                        pi.prevPos.x += normalX * dampingHalf;
                                        pi.prevPos.y += normalY * dampingHalf;
                                        pj.prevPos.x -= normalX * dampingHalf;
                                        pj.prevPos.y -= normalY * dampingHalf;
                                    }

                                    particles[j] = pj;

                                    // Re-cache pos for accurate distance checks on next neighbor
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

        // ── ResolveObstaclesJob ───────────────────────────────────────

        /// <summary>
        /// Resolves particle ↔ obstacle collisions for all active particles.
        /// <para>
        /// Two-phase per obstacle: <br/>
        /// 1. <b>Broadphase</b> — precomputed AABB (expanded by max particle radius at bake time).
        ///    Point-in-box test: 4 float comparisons, no per-particle radius math.<br/>
        /// 2. <b>Narrowphase</b> — per-shape signed distance (Circle, Box, Capsule).
        /// </para>
        /// <para>
        /// Collision response: position correction + configurable restitution + Coulomb friction.
        /// All math inlined — same rationale as <see cref="ResolveCollisionsJob"/>.
        /// </para>
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        public struct ResolveObstaclesJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;

            [ReadOnly] public NativeArray<ObstacleData> obstacles;
            public int obstacleCount;

            public void Execute()
            {
                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];
                    float px = p.pos.x;
                    float py = p.pos.y;

                    for (int o = 0; o < obstacleCount; o++)
                    {
                        var obs = obstacles[o];

                        // ── Broadphase: precomputed AABB (expanded by radiusMax at bake) ──
                        // Point-in-box: the AABB already accounts for max particle radius,
                        // so this is a zero-overhead rejection for distant obstacles.
                        if (px < obs.aabbMin.x || px > obs.aabbMax.x ||
                            py < obs.aabbMin.y || py > obs.aabbMax.y)
                            continue;

                        float penetration;
                        float normalX, normalY;

                        // ── Narrowphase dispatch (inlined) ──────────
                        switch (obs.shape)
                        {
                            case ObstacleShape.Circle:
                            {
                                float dx = px - obs.center.x;
                                float dy = py - obs.center.y;
                                float distSq = dx * dx + dy * dy;
                                float minDist = obs.halfExtents.x + p.radius;

                                if (distSq >= minDist * minDist || distSq < 1e-10f)
                                    continue;

                                float invDist = math.rsqrt(distSq);
                                float dist = distSq * invDist;
                                normalX = dx * invDist;
                                normalY = dy * invDist;
                                penetration = minDist - dist;
                                break;
                            }

                            case ObstacleShape.Box:
                            {
                                float localX = px - obs.center.x;
                                float localY = py - obs.center.y;
                                float hx = obs.halfExtents.x;
                                float hy = obs.halfExtents.y;

                                float clampedX = localX < -hx ? -hx : (localX > hx ? hx : localX);
                                float clampedY = localY < -hy ? -hy : (localY > hy ? hy : localY);

                                bool isInsideX = localX == clampedX;
                                bool isInsideY = localY == clampedY;

                                if (isInsideX && isInsideY)
                                {
                                    // Center inside box — push out along the axis
                                    // with the smallest penetration depth.
                                    float distToEdgeX = hx - math.abs(localX);
                                    float distToEdgeY = hy - math.abs(localY);

                                    if (distToEdgeX < distToEdgeY)
                                    {
                                        normalX = localX >= 0f ? 1f : -1f;
                                        normalY = 0f;
                                        penetration = distToEdgeX + p.radius;
                                    }
                                    else
                                    {
                                        normalX = 0f;
                                        normalY = localY >= 0f ? 1f : -1f;
                                        penetration = distToEdgeY + p.radius;
                                    }
                                }
                                else
                                {
                                    // Center outside box — closest point on box surface.
                                    float ddx = localX - clampedX;
                                    float ddy = localY - clampedY;
                                    float distSq = ddx * ddx + ddy * ddy;
                                    float rSq = p.radius * p.radius;

                                    if (distSq >= rSq || distSq < 1e-10f)
                                        continue;

                                    float invDist = math.rsqrt(distSq);
                                    float dist = distSq * invDist;
                                    normalX = ddx * invDist;
                                    normalY = ddy * invDist;
                                    penetration = p.radius - dist;
                                }
                                break;
                            }

                            case ObstacleShape.Capsule:
                            {
                                float dx = px - obs.center.x;
                                float dy = py - obs.center.y;

                                // Project onto capsule medial axis, clamp to segment
                                float t = dx * obs.axisDirection.x + dy * obs.axisDirection.y;
                                float halfH = obs.halfExtents.y;
                                if (t < -halfH) t = -halfH;
                                else if (t > halfH) t = halfH;

                                // Closest point on axis → reduce to circle test
                                float tpx = px - (obs.center.x + obs.axisDirection.x * t);
                                float tpy = py - (obs.center.y + obs.axisDirection.y * t);
                                float distSq = tpx * tpx + tpy * tpy;
                                float minDist = obs.halfExtents.x + p.radius;

                                if (distSq >= minDist * minDist || distSq < 1e-10f)
                                    continue;

                                float invDist = math.rsqrt(distSq);
                                float dist = distSq * invDist;
                                normalX = tpx * invDist;
                                normalY = tpy * invDist;
                                penetration = minDist - dist;
                                break;
                            }

                            default:
                                continue;
                        }

                        // ── Collision Response ──────────────────────

                        // Capture velocity BEFORE position correction
                        float velX = p.pos.x - p.prevPos.x;
                        float velY = p.pos.y - p.prevPos.y;
                        float normalVel = velX * normalX + velY * normalY;

                        // Position correction — push particle out of obstacle
                        p.pos.x += normalX * penetration;
                        p.pos.y += normalY * penetration;

                        // Restitution — reflect normal velocity via prevPos.
                        // Only for incoming velocity (normalVel < 0 = approaching surface).
                        // bounciness=0 → dead stop. bounciness=1 → full elastic reflect.
                        if (normalVel < 0f)
                        {
                            float restitutionCorrection = normalVel * (1f + obs.bounciness);
                            p.prevPos.x += normalX * restitutionCorrection;
                            p.prevPos.y += normalY * restitutionCorrection;
                        }

                        // Coulomb friction — clamp tangent velocity by friction × penetration.
                        float tangentVelX = velX - normalVel * normalX;
                        float tangentVelY = velY - normalVel * normalY;
                        float tangentLenSq = tangentVelX * tangentVelX + tangentVelY * tangentVelY;
                        float maxFriction = obs.friction * penetration;

                        if (tangentLenSq > maxFriction * maxFriction * FrictionDeadZoneFraction)
                        {
                            float tangentLen = math.sqrt(tangentLenSq);
                            float correction = tangentLen < maxFriction ? tangentLen : maxFriction;
                            float invTLen = 1f / tangentLen;
                            p.prevPos.x += tangentVelX * invTLen * correction;
                            p.prevPos.y += tangentVelY * invTLen * correction;
                        }

                        // Re-cache pos after correction for next obstacle test
                        px = p.pos.x;
                        py = p.pos.y;
                    }

                    particles[i] = p;
                }
            }
        }

        // ── ResolveFunnelJob ──────────────────────────────────────────

        /// <summary>
        /// Resolves particle ↔ funnel wall collisions.
        /// Replaces <c>ResolveBoundariesJob</c> — funnel edges are arbitrary 2D segments
        /// instead of axis-aligned walls.
        /// <para>
        /// Uses <b>signed distance</b> from the precomputed outward normal to detect
        /// both proximity collisions AND full tunneling (particle crossed to the wrong
        /// side of the wall in one substep). This is critical for small particles
        /// (radius 0.01) with high gravity — displacement per substep can exceed
        /// 2× particle diameter, causing standard overlap-only checks to miss.
        /// </para>
        /// <para>
        /// Funnel friction is intentionally low (~0.05) so sand slides fast
        /// down the inclined walls toward the narrow spout.
        /// </para>
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        public struct ResolveFunnelJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;

            [ReadOnly] public NativeArray<FunnelSegment> segments;
            public int segmentCount;
            public float friction;

            /// <summary>
            /// Broadphase expansion margin around each segment AABB.
            /// Must be large enough to catch tunneled particles — set to
            /// max expected displacement per substep (not just particle radius).
            /// Passed in by the orchestrator as <c>max(particleRadiusMax, maxDisplacementPerSubstep)</c>.
            /// </summary>
            public float broadphaseMargin;

            public void Execute()
            {
                float margin = broadphaseMargin;

                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];
                    float px = p.pos.x;
                    float py = p.pos.y;
                    float r = p.radius;

                    for (int s = 0; s < segmentCount; s++)
                    {
                        var seg = segments[s];

                        // ── Broadphase: inline AABB from segment endpoints ──
                        // Expanded by broadphaseMargin — must cover max tunneling distance,
                        // not just particle radius.
                        float segMinX = (seg.a.x < seg.b.x ? seg.a.x : seg.b.x) - margin;
                        float segMaxX = (seg.a.x > seg.b.x ? seg.a.x : seg.b.x) + margin;
                        float segMinY = (seg.a.y < seg.b.y ? seg.a.y : seg.b.y) - margin;
                        float segMaxY = (seg.a.y > seg.b.y ? seg.a.y : seg.b.y) + margin;

                        if (px < segMinX || px > segMaxX || py < segMinY || py > segMaxY)
                            continue;

                        // ── Closest point on segment ──
                        float abx = seg.b.x - seg.a.x;
                        float aby = seg.b.y - seg.a.y;
                        float apx = px - seg.a.x;
                        float apy = py - seg.a.y;

                        float t = (apx * abx + apy * aby) * seg.invLenSq;
                        if (t < 0f) t = 0f;
                        else if (t > 1f) t = 1f;

                        float closestX = seg.a.x + abx * t;
                        float closestY = seg.a.y + aby * t;

                        float dx = px - closestX;
                        float dy = py - closestY;
                        float distSq = dx * dx + dy * dy;

                        // ── Signed distance from wall surface ──
                        // signedDist > 0: particle is on the interior (correct) side
                        // signedDist < 0: particle has TUNNELED through the wall
                        // signedDist ∈ [0, r]: particle overlaps the wall surface
                        float signedDist = dx * seg.outNormal.x + dy * seg.outNormal.y;

                        // Skip if safely on the correct side beyond particle radius
                        if (signedDist > r)
                            continue;

                        // Skip if too far from the segment line (endpoint region, no collision)
                        // But only skip if we haven't tunneled (signedDist >= 0)
                        if (signedDist >= 0f && distSq >= r * r)
                            continue;

                        // ── Compute penetration + collision normal ──
                        float penetration;
                        float normalX, normalY;

                        if (signedDist < 0f)
                        {
                            // TUNNELED: particle crossed to the wrong side.
                            // Push back along outNormal by (radius - signedDist).
                            normalX = seg.outNormal.x;
                            normalY = seg.outNormal.y;
                            penetration = r - signedDist;
                        }
                        else if (distSq > 1e-10f)
                        {
                            // Normal overlap: push along closest-point → particle direction
                            float invDist = math.rsqrt(distSq);
                            normalX = dx * invDist;
                            normalY = dy * invDist;
                            penetration = r - distSq * invDist;
                        }
                        else
                        {
                            // Degenerate: particle exactly on the segment line
                            normalX = seg.outNormal.x;
                            normalY = seg.outNormal.y;
                            penetration = r;
                        }

                        if (penetration <= 0f)
                            continue;

                        // ── Collision response ──

                        float velX = p.pos.x - p.prevPos.x;
                        float velY = p.pos.y - p.prevPos.y;
                        float normalVel = velX * normalX + velY * normalY;

                        // Position correction — push out of wall
                        p.pos.x += normalX * penetration;
                        p.pos.y += normalY * penetration;

                        // Dead stop along normal (bounciness = 0 for funnel walls)
                        if (normalVel < 0f)
                        {
                            p.prevPos.x += normalX * normalVel;
                            p.prevPos.y += normalY * normalVel;
                        }

                        // Coulomb friction — low friction lets sand slide down inclined walls
                        float tangentVelX = velX - normalVel * normalX;
                        float tangentVelY = velY - normalVel * normalY;
                        float tangentLenSq = tangentVelX * tangentVelX + tangentVelY * tangentVelY;
                        float maxFrict = friction * penetration;

                        if (tangentLenSq > maxFrict * maxFrict * FrictionDeadZoneFraction)
                        {
                            float tangentLen = math.sqrt(tangentLenSq);
                            float correction = tangentLen < maxFrict ? tangentLen : maxFrict;
                            float invTLen = 1f / tangentLen;
                            p.prevPos.x += tangentVelX * invTLen * correction;
                            p.prevPos.y += tangentVelY * invTLen * correction;
                        }

                        // Re-cache pos for next segment
                        px = p.pos.x;
                        py = p.pos.y;
                    }

                    particles[i] = p;
                }
            }
        }

        // ── DespawnOutOfBoundsJob ─────────────────────────────────────

        /// <summary>
        /// Soft-kills particles that fell below the funnel spout.
        /// Teleports them far off-screen and marks them sleeping — zero ongoing cost.
        /// <para>
        /// Runs once per frame after all substeps (not per-substep).
        /// Particles below <see cref="despawnY"/> are moved to <c>(9999, 9999)</c>
        /// (outside spatial hash grid) and set to sleeping. Their slot remains
        /// allocated but dormant — no physics, no render (off-camera).
        /// </para>
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        public struct DespawnOutOfBoundsJob : IJob
        {
            public NativeArray<SandParticle> particles;
            [ReadOnly] public NativeArray<int> activeIndices;
            public int awakeCount;
            public NativeArray<bool> isSleeping;
            public float despawnY;

            public void Execute()
            {
                for (int a = 0; a < awakeCount; a++)
                {
                    int i = activeIndices[a];
                    var p = particles[i];

                    if (p.pos.y < despawnY)
                    {
                        p.pos = new float2(9999f, 9999f);
                        p.prevPos = p.pos;
                        p.frameStartPos = p.pos;
                        particles[i] = p;
                        isSleeping[i] = true;
                    }
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
                    // Saturate at 255 to prevent byte overflow when sleepFrames is large.
                    // sleepFrames is clamped to [1, 255] at the orchestrator level.
                    int raw = sleepCounters[i] + 1;
                    byte counter = raw < 255 ? (byte)raw : (byte)255;
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
