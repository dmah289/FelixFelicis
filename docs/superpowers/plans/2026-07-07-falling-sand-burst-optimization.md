# FallingSand Burst Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce FallingSand physics CPU cost ~5-10× by migrating to NativeArray + Burst + IJob and changing collision outer loop from O(n) to O(awake).

**Architecture:** Replace managed arrays with `NativeArray<T>` (Allocator.Persistent). Convert each `SandPhysics` method into a `[BurstCompile] IJob` struct scheduled sequentially on main thread. Change collision outer loop to iterate `activeIndices[]` instead of `0..count` with adjusted pair-processing rule. `UpdateSleep` stays managed (not hot path).

**Tech Stack:** Unity Burst 1.8.21, Unity Collections 2.5.4, Unity Mathematics 1.3.2, IJob (not IJobParallelFor)

## Global Constraints

- Branch: `develop/falling_sand`
- Namespace: `FelixFelicis.ParticleRendering.Simulation`
- Assembly: `com.FelixFelicis` (asmdef at `Assets/FelixFelicis/com.FelixFelicis.Runtime.asmdef`)
- `SandParticle` must remain exactly 32 bytes
- Physics behavior must be identical — same pair processing order, same collision response
- Zero GC in simulation hot path
- All `NativeArray`/`NativeReference` must be disposed in `OnDestroy`
- `ParticleRenderData.center` is `Vector2` — `float2` has implicit conversion, no change needed in rendering layer
- `SandColors.cs` — untouched (no reference to `Vector2` or `SandParticle`)
- Rendering pipeline (ParticleDrawer, ParticleRenderPass, shader) — untouched

---

### Task 1: Add Package Dependencies + Update asmdef

**Files:**
- Modify: `Packages/manifest.json:3` (add 3 entries to `dependencies`)
- Modify: `Assets/FelixFelicis/com.FelixFelicis.Runtime.asmdef:4-8` (add 2 refs to `references`)

**Interfaces:**
- Consumes: nothing
- Produces: `Unity.Burst`, `Unity.Mathematics`, `Unity.Collections` available in `com.FelixFelicis` assembly

- [ ] **Step 1: Add packages to manifest.json**

Open `Packages/manifest.json` and add these 3 entries inside `"dependencies"` (alphabetical order):

```json
"com.unity.burst": "1.8.21",
"com.unity.collections": "2.5.4",
"com.unity.mathematics": "1.3.2",
```

Insert after the existing `"com.unity.addressables"` line. The file already has `com.unity.collab-proxy` after addressables, so the new entries go between them.

- [ ] **Step 2: Add assembly references to asmdef**

Open `Assets/FelixFelicis/com.FelixFelicis.Runtime.asmdef` and add `Unity.Burst` and `Unity.Mathematics` to the `references` array:

```json
{
    "name": "com.FelixFelicis",
    "rootNamespace": "FelixFelicis",
    "references": [
        "Unity.RenderPipelines.Universal.Runtime",
        "com.horcrux.runtime",
        "Unity.RenderPipelines.Core.Runtime",
        "Unity.Collections",
        "Unity.Burst",
        "Unity.Mathematics"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 3: Verify Unity compiles without errors**

Open Unity Editor, wait for package import + domain reload. Console should show no errors.
If Burst package version conflicts, use the latest compatible version from Package Manager.

- [ ] **Step 4: Commit**

```
git add Packages/manifest.json Assets/FelixFelicis/com.FelixFelicis.Runtime.asmdef
git commit -m "feat(falling-sand): add Burst, Collections, Mathematics packages"
```

---

### Task 2: Migrate SandParticle to float2

**Files:**
- Modify: `Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/SandParticle.cs` (full rewrite)

**Interfaces:**
- Consumes: `Unity.Mathematics.float2`
- Produces: `struct SandParticle` with `float2 pos, prevPos, frameStartPos; float radius; uint packedColor;` — 32 bytes, `[StructLayout(Sequential)]`

- [ ] **Step 1: Rewrite SandParticle.cs**

Replace the entire file:

```csharp
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Per-particle state for falling sand simulation.
    /// Stored in a pre-allocated NativeArray — no per-frame allocation.
    /// Verlet-style: velocity is derived from <c>pos - prevPos</c>.
    /// <para>
    /// 32 bytes (power-of-2) — exactly 2 structs per 64-byte cache line, no straddling.
    /// Sleep state (<c>isSleeping</c>, <c>sleepCounter</c>) stored in separate parallel
    /// NativeArrays for cache-friendly skip checks (64 bools per cache line).
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SandParticle
    {
        public float2 pos;
        public float2 prevPos;

        // Snapshot of position at the start of each FixedUpdate frame.
        // Used by sleep detection to measure total frame displacement
        // instead of per-substep micro-jitter from collision corrections.
        public float2 frameStartPos;

        public float radius;
        public uint packedColor;
    }
}
```

- [ ] **Step 2: Verify compilation**

Unity should show errors in `SandPhysics.cs`, `SpatialHash2D.cs`, `FallingSandSim.cs` — these expect `Vector2` but now get `float2`. This is expected; they will be fixed in later tasks.

Verify `SandColors.cs` has NO errors (it doesn't reference `SandParticle` or `Vector2` in particle fields).

- [ ] **Step 3: Commit**

```
git add Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/SandParticle.cs
git commit -m "refactor(falling-sand): SandParticle Vector2 → float2"
```

---

### Task 3: Migrate SpatialHash2D to NativeArray

**Files:**
- Modify: `Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/SpatialHash2D.cs` (full rewrite)

**Interfaces:**
- Consumes: `SandParticle` (float2 pos from Task 2), `NativeArray<T>` from Unity.Collections
- Produces: `class SpatialHash2D : IDisposable` with:
  - `internal NativeArray<int> cellCounts, cellOffsets, sortedIndices` (read by collision job)
  - `internal readonly float invCellSize, originX, originY`
  - `internal readonly int gridWidth, gridHeight, cellCount`
  - `void Build(NativeArray<SandParticle> particles, int count)` — 3-pass O(n) counting sort
  - `void Dispose()` — disposes all NativeArrays
  - `NativeArray<int> particleCells` — internal, used by Build only

- [ ] **Step 1: Rewrite SpatialHash2D.cs**

Replace the entire file:

```csharp
using System;
using Unity.Collections;

namespace FelixFelicis.ParticleRendering.Simulation
{
    /// <summary>
    /// Zero-GC 2D spatial hash using counting-sort.
    /// Rebuilds every substep in O(n). All NativeArrays pre-allocated at construction.
    /// Fields are internal for direct access from Burst jobs — avoids
    /// method call overhead in the hot collision loop.
    /// </summary>
    public class SpatialHash2D : IDisposable
    {
        internal readonly float invCellSize;
        internal readonly int gridWidth;
        internal readonly int gridHeight;
        internal readonly int cellCount;
        internal readonly float originX;
        internal readonly float originY;

        internal NativeArray<int> cellCounts;
        internal NativeArray<int> cellOffsets;
        internal NativeArray<int> sortedIndices;
        internal NativeArray<int> particleCells;

        private int capacity;
        private bool disposed;

        public SpatialHash2D(float cellSize, float minX, float maxX, float minY, float maxY, int initialCapacity)
        {
            invCellSize = 1f / cellSize;
            originX = minX;
            originY = minY;

            gridWidth = (int)Math.Ceiling((maxX - minX) * invCellSize) + 1;
            gridHeight = (int)Math.Ceiling((maxY - minY) * invCellSize) + 1;
            cellCount = gridWidth * gridHeight;

            cellCounts = new NativeArray<int>(cellCount, Allocator.Persistent);
            cellOffsets = new NativeArray<int>(cellCount, Allocator.Persistent);
            capacity = initialCapacity;
            sortedIndices = new NativeArray<int>(capacity, Allocator.Persistent);
            particleCells = new NativeArray<int>(capacity, Allocator.Persistent);
        }

        /// <summary>
        /// Ensures particle-indexed arrays can hold <paramref name="count"/> entries.
        /// Disposes old arrays and reallocates if needed. Called before Build.
        /// </summary>
        public void EnsureCapacity(int count)
        {
            if (count <= capacity) return;

            capacity = count;
            sortedIndices.Dispose();
            particleCells.Dispose();
            sortedIndices = new NativeArray<int>(capacity, Allocator.Persistent);
            particleCells = new NativeArray<int>(capacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (cellCounts.IsCreated) cellCounts.Dispose();
            if (cellOffsets.IsCreated) cellOffsets.Dispose();
            if (sortedIndices.IsCreated) sortedIndices.Dispose();
            if (particleCells.IsCreated) particleCells.Dispose();
        }
    }
}
```

Key changes from original:
- `int[]` → `NativeArray<int>` with `Allocator.Persistent`
- `Build()` removed from class — will be a Burst Job in Task 4
- `EnsureCapacity()` extracted — called by orchestrator before scheduling build job
- `Dispose()` actually disposes NativeArrays, with `disposed` guard and `IsCreated` checks
- `Mathf.CeilToInt` → `(int)Math.Ceiling` (avoid UnityEngine dependency — Burst-friendlier)

- [ ] **Step 2: Commit**

```
git add Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/SpatialHash2D.cs
git commit -m "refactor(falling-sand): SpatialHash2D managed arrays → NativeArray"
```

---

### Task 4: Convert SandPhysics to Burst Jobs

**Files:**
- Modify: `Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/SandPhysics.cs` (full rewrite — static methods → 6 Job structs)

**Interfaces:**
- Consumes:
  - `SandParticle` (float2, Task 2)
  - `SpatialHash2D` fields: `invCellSize, originX, originY, gridWidth, gridHeight, cellCounts, cellOffsets, sortedIndices, particleCells` (NativeArray, Task 3)
  - `NativeArray<T>`, `NativeReference<T>` from Unity.Collections
  - `Unity.Burst.BurstCompile`, `Unity.Jobs.IJob`
  - `Unity.Mathematics.math.rsqrt`, `math.sqrt`
- Produces:
  - `BuildActiveIndicesJob : IJob` — fields: `NativeArray<bool> isSleeping`, `int count`, `NativeArray<int> activeIndices`, `NativeReference<int> awakeCount`
  - `SnapshotFrameStartJob : IJob` — fields: `NativeArray<SandParticle> particles`, `NativeArray<int> activeIndices`, `int awakeCount`
  - `IntegrateJob : IJob` — fields: `NativeArray<SandParticle> particles`, `NativeArray<int> activeIndices`, `int awakeCount`, `float gx, gy, dragMul`
  - `SpatialHashBuildJob : IJob` — fields: all SpatialHash2D NativeArrays + grid params + `NativeArray<SandParticle> particles`, `int count`
  - `ResolveCollisionsJob : IJob` — fields: particles, activeIndices, awakeCount, all hash arrays + params, sleep arrays, physics params, `NativeReference<bool> wakeOccurred`
  - `ResolveBoundariesJob : IJob` — fields: `NativeArray<SandParticle> particles`, `NativeArray<int> activeIndices`, `int awakeCount`, `float boundsX, boundsY, wallFriction`

- [ ] **Step 1: Write the complete SandPhysics.cs with 6 Job structs**

Replace the entire file:

```csharp
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
```

Key differences from original:
- 6 `[BurstCompile] IJob` structs instead of static methods
- All arrays are `NativeArray<T>` with `[ReadOnly]`/`[WriteOnly]` attributes where appropriate
- `FastInvSqrt` → `math.rsqrt`, `FastSqrt` → `math.sqrt`
- Collision outer loop: `for a in 0..awakeCount` using `activeIndices` (O(awake))
- Pair rule: `if (j == i) continue; if (!isSleeping[j] && j < i) continue;`
- NativeArray indexer returns copy → read struct into local `var pi`, modify, write back `particles[i] = pi`
- `UpdateSleep` remains a static managed method (not a job)

- [ ] **Step 2: Commit**

```
git add Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/SandPhysics.cs
git commit -m "feat(falling-sand): convert SandPhysics to Burst IJob structs + collision O(awake)"
```

---

### Task 5: Migrate FallingSandSim to NativeArray + Job Scheduling

**Files:**
- Modify: `Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/FallingSandSim.cs` (full rewrite)

**Interfaces:**
- Consumes:
  - `SandParticle` (float2, Task 2)
  - `SpatialHash2D` (NativeArray, Task 3)
  - All 6 Job structs from `SandPhysics` (Task 4)
  - `SandColors.GeneratePacked()` (unchanged)
  - `ParticleProvider.Writer` as `IInstanceWriter<ParticleRenderData>` (unchanged)
  - `ParticleRenderData` struct with `Vector2 center` (unchanged — `float2` implicit converts)
- Produces: `FallingSandSim : MonoBehaviour` — orchestrator with NativeArray state, Job schedule/complete pattern, managed UpdateSleep, proper Dispose

- [ ] **Step 1: Rewrite FallingSandSim.cs**

Replace the entire file:

```csharp
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

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
```

Key changes from original:
- All managed arrays → `NativeArray<T>` / `NativeReference<T>` with `Allocator.Persistent`
- Each physics step is a Job `Schedule().Complete()` call
- `Vector2` params (`gravity`) stay as `Vector2` in SerializeField (Inspector), converted to `float` at usage
- `float2` used in spawn methods (`SpawnParticle`, `RandomPositionInRange`)
- `LateUpdate` uses `float2 → Vector2` implicit conversion for `ParticleRenderData.center`
- `OnDestroy` disposes every NativeArray/NativeReference with `IsCreated` guard

- [ ] **Step 2: Verify full compilation in Unity**

Open Unity Editor, wait for domain reload. Console should show **zero errors**.

Check:
- `SandParticle.cs` — float2 ✓
- `SpatialHash2D.cs` — NativeArray ✓
- `SandPhysics.cs` — 6 Job structs ✓
- `FallingSandSim.cs` — NativeArray + Schedule/Complete ✓
- `SandColors.cs` — untouched, no errors ✓
- Rendering layer — untouched, no errors ✓

- [ ] **Step 3: Commit**

```
git add Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/FallingSandSim.cs
git commit -m "feat(falling-sand): FallingSandSim NativeArray containers + Job scheduling"
```

---

### Task 6: Verify in Unity — Behavior + Performance + Leak Check

**Files:**
- No file changes — verification only

**Interfaces:**
- Consumes: All changes from Tasks 1-5

- [ ] **Step 1: Enter Play Mode with Burst mode and 10K particles**

1. Open the scene containing `FallingSandSim`
2. Inspector: set `Mode = Burst`, `Max Particles = 10000`
3. Enter Play Mode
4. **Verify:** particles fall, form sand pile, angle of repose ~20-25°
5. **Verify:** stream mode works — switch to `Mode = Stream`, re-enter Play Mode, particles spawn from top

- [ ] **Step 2: Check Burst Inspector**

1. Menu: Jobs → Burst → Open Inspector
2. Find all 6 jobs under `FelixFelicis.ParticleRendering.Simulation.SandPhysics`:
   - `BuildActiveIndicesJob`
   - `SnapshotFrameStartJob`
   - `IntegrateJob`
   - `SpatialHashBuildJob`
   - `ResolveCollisionsJob`
   - `ResolveBoundariesJob`
3. **Verify:** all 6 show "Compiled" status (green check), no Burst compilation errors

- [ ] **Step 3: Profile with Unity Profiler**

1. Enter Play Mode with Burst 10K, wait for particles to mostly settle (~90% sleeping)
2. Open Profiler (Window → Analysis → Profiler)
3. **Verify FixedUpdate physics < 1ms** when most particles sleeping (was ~3-5ms on Mono)
4. **Verify FixedUpdate ≈ 0ms** when ALL particles sleeping
5. **Verify LateUpdate ≈ 0ms** when all settled (render upload skip)
6. **Verify GC Alloc = 0** in simulation path (Profiler → GC.Alloc column)

- [ ] **Step 4: NativeArray leak check**

1. Enter Play Mode, let simulation run for a few seconds
2. Exit Play Mode
3. **Verify:** Console shows NO "A Native Collection has not been disposed" warnings
4. Repeat Enter/Exit Play Mode 3 times — no errors, no leaks

- [ ] **Step 5: Wake cascade test**

1. Enter Play Mode with Stream mode
2. Wait for pile to fully settle (all sleeping)
3. Switch to Burst mode or trigger more particles from top
4. **Verify:** new particles falling onto pile wake up sleeping particles in contact
5. **Verify:** cascade propagates naturally, then settles again

- [ ] **Step 6: Update FallingSand.md documentation**

Open `Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/FallingSand.md` and update:

In Section 2 (Tối ưu hiệu năng), add a new subsection `### 2.7 Burst + NativeArray + IJob`:

```markdown
### 2.7 Burst + NativeArray + IJob

| Kỹ thuật | Lý do |
|----------|-------|
| `NativeArray<T>` thay managed arrays | Burst bỏ bounds check, contiguous memory guaranteed |
| `[BurstCompile] IJob` cho 6 physics methods | Auto-SIMD, inline, no GC, ~2-4× nhanh hơn Mono |
| `math.rsqrt()` thay FastInvSqrt | SSE `rsqrtss` instruction, nhanh hơn Quake trick trong Burst |
| Collision outer O(awake) thay O(n) | `activeIndices[]` iteration, pair rule `j==i ∥ (!sleeping[j] && j<i)` |
| `UpdateSleep` giữ managed | 1 lần/frame, O(awake), cần kết quả ngay cho `needsRenderUpload` |
| `NativeReference<int/bool>` | awakeCount + wakeOccurred output từ Jobs, không cần managed callback |
```

In Section 7 (Tóm tắt hiệu năng), update:

```markdown
| Burst compilation | 6 IJob structs, auto-SIMD |
| Collision outer loop | O(awake) via activeIndices |
| Math | math.rsqrt (SSE rsqrtss) |
```

- [ ] **Step 7: Commit documentation + final commit**

```
git add Assets/FelixFelicis/Implementation/ParticleRendering/Scripts/FallingSand/FallingSand.md
git commit -m "docs(falling-sand): update FallingSand.md with Burst optimization details"
```
