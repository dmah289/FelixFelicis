# Falling Sand Simulation

> 10,000 hạt cát · PBD + Verlet · GPU instancing 1 draw call

Namespace: `FelixFelicis.ParticleRendering.Simulation` · Assembly: `com.FelixFelicis`

---

## 1. Nền tảng lý thuyết

### 1.1 Position-Based Dynamics (PBD)

Collision bằng **position correction** thay vì force accumulation — ổn định vô điều kiện, không explode dù timestep lớn.

| Approach | Ưu | Nhược | |
|----------|------|-------|:--:|
| **PBD** (Verlet + constraints) | Ổn định, friction tự nhiên, dễ tune | Cần iterations | ✓ |
| Force-based (F = ma) | Chính xác | Dễ explode, stiff contacts | ✗ |
| Grid cellular automaton | O(n), stacking hoàn hảo | Snap vào grid, không mượt | ✗ |
| Impulse-based | Tốt cho rigid body | Micro-jitter khi nhiều contact | ✗ |

---

### 1.2 Verlet Integration (Störmer-Verlet)

Lưu `pos` + `prevPos`, velocity suy từ delta — không lưu riêng:

```
vel = pos - prevPos             ← implicit velocity
vel *= (1 - airDrag)            ← drag
prevPos = pos
pos += vel + gravity × dt²      ← Störmer-Verlet
```

**Tại sao `× dt²`?** Störmer-Verlet: `x(t+dt) = x(t) + vel + a·dt²`. Gravity là gia tốc (m/s²) × dt² (s²) = displacement (m). Nhân `dt` cho velocity — sai đơn vị.

| | Euler | Verlet |
|---|---|---|
| State | pos + vel | pos + prevPos |
| Constraint | Sửa pos → vel lệch → jitter | Sửa pos → vel tự cập nhật |
| Friction | Tính explicit | Implicit từ position delta |

PBD sửa position trực tiếp → Verlet tự "hiểu" velocity mới. Euler phải sync velocity riêng → jitter.

---

### 1.3 Spatial Hashing — Counting Sort

O(n²) → **O(n × k)**, k ≈ 2–4 particles/cell. Counting-sort 3-pass O(n), zero GC:

```
Pass 1: cell index + count    → cellCounts[], particleCells[] (cached)
Pass 2: prefix-sum            → cellOffsets[]
Pass 3: scatter (cached cell) → sortedIndices[]
```

```
8 particles, grid 3×3:

Cell:      0    1    2    3    4    5    6    7    8
Count:     1    0    2    0    1    3    0    0    1
Offset:    0    1    1    3    3    4    7    7    7

sortedIndices: [4 | _ | 2,7 | _ | 0 | 1,5,6 | _ | _ | 3]
```

| | `Dictionary` | Counting-sort |
|---|---|---|
| GC | Resize → spike | Pre-allocated, zero GC |
| Cache | Random access | Sequential scan |
| Rebuild | O(n) amortized | O(n) guaranteed |
| Memory | ~40 B/entry | ~4 B/entry |

Cell size = `2.5 × radiusMax` — margin cho precision khi correction đẩy particle sang cell lân cận.

---

### 1.4 Collision Response

```
delta = pos[j] - pos[i]
invDist = FastInvSqrt(delta.x² + delta.y²)     ← Quake-style, ~2× Mathf.Sqrt
dist = distSqr × invDist
overlap = (r_i + r_j) - dist
normal = delta × invDist
```

**3 giai đoạn per contact:**

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. Position correction                                                │
│    j sleeping → pos[i] -= n × overlap                                 │
│    both active → height-biased split (§1.5)                           │
│                                                                        │
│ 2. Coulomb friction (tangential)                                      │
│    effFriction = μ × (1 - verticalness × slopeFrictionReduction)      │
│    correction = min(|tangentVel|, effFriction × overlap)              │
│                                                                        │
│ 3. Contact damping → sửa prevPos (giảm implicit velocity)            │
│    prevPos[i] += n × dot(relVel, n) × damping × 0.5                  │
│    prevPos[j] -= n × dot(relVel, n) × damping × 0.5                  │
└──────────────────────────────────────────────────────────────────────┘
```

Damping sửa `prevPos` — implicit velocity `(pos - prevPos)` giảm mà không ảnh hưởng constraint satisfaction.

---

### 1.5 Slope Mechanics

Hai cơ chế kết hợp tạo đụn cát tự nhiên (~20–25° angle of repose):

**Height-biased position correction** — particle trên nhận nhiều correction hơn:

```
i above j → bias = 0.5 + slopeBias = 0.7 (i nhận 70%)
i below j → bias = 0.5 - slopeBias = 0.3 (i nhận 30%)

slopeBias = 0 → pile thẳng đứng (50/50 symmetric)
slopeBias > 0 → particle trên trượt ra → tạo slope
```

**Slope-dependent friction reduction** — contact dọc trượt dễ hơn:

```
verticalness = normal.y²        (0 = ngang, 1 = dọc)

effFriction = μ × (1 - verticalness × slopeFrictionReduction)
│                                │
│  Contact dọc (nny² ≈ 1):      │  μ × 0.3  → trượt dễ
│  Contact ngang (nny² ≈ 0):    │  μ × 1.0  → giữ chặt
```

`nny²` thay vì `|nny|` — nhanh hơn (không abs/branch), smooth hơn (đạo hàm liên tục tại 0), cùng mapping 0→0, ±1→1.

---

### 1.6 Sleep System

```
            ┌─────────────┐
            │   Active    │◄── overlap > threshold AND speed > wakeSpeed
            │  (physics)  │    (cần CẢ HAI)
            └─────┬───────┘
                  │ frame displacement < sleepThreshold
                  │ × sleepFrames consecutive frames
                  ▼
            ┌─────────────┐
            │  Sleeping   │ skip integrate + skip initiate collision
            │ (obstacle)  │ stay in spatial hash
            └─────────────┘
```

| Quyết định | Lý do |
|------------|-------|
| Đo displacement **per-frame** (không per-substep) | Collision correction gây micro-jitter mỗi substep rồi kéo lại → false wake. `frameStartPos` snapshot 1 lần đầu frame |
| Wake cần overlap **VÀ** speed | Settled particles có tiny overlap residual. Chỉ particle bay vào nhanh mới đánh thức |
| `wakeThreshold = fraction × min(ri, rj)` | Dùng bán kính nhỏ hơn → nhạy hơn, particle nhỏ không bị "nuốt" |
| Wake: `prevPos = pos` | Zero velocity — tránh phantom velocity từ vị trí trước khi ngủ |

---

### 1.7 Boundary Handling

Bounding box `[-spawnRange, spawnRange]²` — 4 walls + Coulomb wall friction:

```
│wall                              wall│
│      ░░░                             │  clamp position
│    ░░░░░░░        ░░                 │  + kill normal velocity (prevPos = pos)
│  ░░░░░░░░░░░░░░░░░░░░░░             │  + friction tangent to wall
│░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
└──────────────────────────────────────┘

friction = clamp(tangentVel, ±wallFriction × penetration)
```

Penetration depth ∝ normal force → va mạnh vào tường = friction lớn hơn.

---

### 1.8 Color Palette

```
#E8D5A3  ████  light tan  ─┐
#D4B896  ████  wheat       │ random 1/5 + ±5° hue + ±10% sat + ±8% val
#C4A882  ████  sand        │ → pack uint (RGBA8) 1 lần khi spawn
#B09070  ████  dark sand   │   zero per-frame cost
#9A7B5B  ████  brown      ─┘
```

---

## 2. Tối ưu hiệu năng

### 2.1 SandParticle 36B → 32B

```
Trước (36B — 1.78/cache line, straddling):     Sau (32B — 2/cache line, exact fit):

  pos            8B                                pos            8B
  prevPos        8B                                prevPos        8B
  radius         4B                                frameStartPos  8B
  packedColor    4B                                radius         4B
  sleepCounter   1B  ┐                             packedColor    4B
  isSleeping     1B  ├ tách ra parallel arrays
  [2B padding]       ┘                             Parallel arrays:
  frameStartPos  8B                                  bool[] isSleeping     64 bools/cache line
                                                     byte[] sleepCounters  64 bytes/cache line
```

| Metric | Trước | Sau |
|--------|-------|-----|
| Struct size | 36B (non-power-of-2) | **32B** (power-of-2) |
| Structs/cache line | 1.78 (straddling) | **2** (exact) |
| Sleep skip check | Load 36B struct cho 1 bool | **1B** từ compact array |
| Cache pressure (skip) | 1 check / 36B | **64 checks / 64B** |

`[StructLayout(LayoutKind.Sequential)]` ngăn runtime reorder. Max alignment = 4, 32 % 4 = 0 → zero padding.

---

### 2.2 Active-Index Iteration — O(awake) thay O(n)

**Vấn đề:** 4 loop (`SnapshotFrameStart`, `Integrate`, `ResolveBoundaries`, `UpdateSleep`) iterate 10K, skip 9K sleeping.

**Giải pháp:** Đầu FixedUpdate scan `isSleeping[]` → build `activeIndices[]` → 4 loop chỉ iterate awake:

```
for a in 0..awakeCount:
    i = activeIndices[a]      ← chỉ active particles
    // physics trên p[i]

90% sleeping → 1K iterations thay 10K → ~10× nhanh
100% sleeping → awakeCount = 0 → early exit, FixedUpdate ≈ 0
```

**Collision loop giữ `for i=0..count`** — không dùng activeIndices:

| Lý do | Chi tiết |
|-------|----------|
| Pair order | `j <= i && !isSleeping[j]` phụ thuộc index tuyệt đối |
| Mid-loop wake | j wake tại outer i → outer loop xử lý j khi đến index j |
| Skip cost đã rẻ | `isSleeping[i]` = 1B compact array, bottleneck là inner loop |

**Wake tracking:** `ref bool wakeOccurred` → rebuild `activeIndices` trước `ResolveBoundaries` + substep tiếp.

---

### 2.3 Render Upload Skip

```
needsRenderUpload = false ──► LateUpdate skip ──► GPU reuse buffer cũ
                                                   (data đúng, particles không di chuyển)
                                                   (BeginFrame không gọi → không swap double buffer)

needsRenderUpload = true  ──► awakeCount > 0 hoặc spawn mới
                          ──► LateUpdate upload bình thường
```

Settled: 0 CPU cost + 0 bandwidth (160KB/frame → 0).

---

### 2.4 Inlined Collision + Fast Math

| Kỹ thuật | Lý do |
|----------|-------|
| Inline toàn bộ SolveContact (~150 dòng) | Mono có significant overhead cho method call với ref struct params, kể cả `[AggressiveInlining]` |
| `FastInvSqrt` (Quake 0x5F3759DF + Newton-Raphson) | ~2× nhanh hơn `1/Mathf.Sqrt` trên Mono. IEEE 754 bit trick cho initial guess (~3% error), 1 Newton-Raphson → <0.2% error |
| `FastSqrt(x) = x × FastInvSqrt(x)` | Tận dụng invSqrt đã tính |

---

### 2.5 Substeps + Time Budget

```csharp
for (int s = 0; s < substeps; s++)
{
    if (s > 0 && stopwatch.ElapsedTicks > budgetTicks) break;  // ≥1 substep guaranteed
    Integrate → BuildHash → ResolveCollisions → ResolveBoundaries
}
UpdateSleep   ← ngoài substep loop: đo net frame displacement, không per-substep jitter
```

Substeps tự chia nhỏ `fixedDeltaTime` trong simulation — không ảnh hưởng `Time.fixedDeltaTime` toàn game.

Budget `maxPhysicsMs = 6ms` → degrade gracefully (ít substeps) thay vì frame drop.

---

### 2.6 Tổng hợp impact (10K particles, 90% sleeping)

| Phase | Trước | Sau | Speedup |
|-------|-------|-----|---------|
| SnapshotFrameStart | 10K iters | 1K | ~10× |
| Integrate (×substeps) | 10K × S | 1K × S | ~10× |
| ResolveBoundaries (×substeps) | 10K × S | 1K × S | ~10× |
| UpdateSleep | 10K iters | 1K | ~10× |
| Collision outer skip | 9K × 36B loads | 9K × 1B loads | ~36× less cache |
| LateUpdate (settled) | 10K writes + SetData | skip | ∞ |
| FixedUpdate (all sleeping) | full physics | return | ∞ |

---

## 3. Thiết kế hệ thống

### 3.1 Dependency Graph

```
FallingSandSim (MonoBehaviour)
  ├── SandParticle[]     32B × maxParticles
  ├── bool[] isSleeping  ┐
  ├── byte[] sleepCounters├ parallel sleep arrays
  ├── int[] activeIndices ┘ rebuilt each FixedUpdate
  ├── SpatialHash2D        counting-sort
  ├── SandPhysics          static stateless solver
  ├── SandColors           palette (spawn only)
  └──► ParticleProvider.Writer → ParticleDrawer → GPU → shader
```

### 3.2 Frame Lifecycle

```
Start()
  alloc particles[], isSleeping[], sleepCounters[], activeIndices[]
  create SpatialHash2D
  if Burst: spawn all

FixedUpdate()                              LateUpdate()
─────────────────────────────────          ─────────────────────────────
  if Stream: StreamSpawn()                   if !needsRenderUpload: return
  if activeCount == 0: return                upload ALL particles to GPU
  awakeCount = BuildActiveIndices()            (sleeping still visible)
  if awakeCount == 0: return    ◄─ early exit
  needsRenderUpload = true
  SnapshotFrameStart()
  for substep:
    if over budget: break
    Integrate()              O(awake)
    hash.Build()             O(n) — all particles
    ResolveCollisions()      O(n) outer, compact sleep skip
      if wake → rebuild activeIndices
    ResolveBoundaries()      O(awake)
  UpdateSleep()              O(awake)
```

### 3.3 Collision Pair Processing

```csharp
// Outer: for i=0..count (preserves pair order)
// Inner:
int j = sorted[off + k];
if (j <= i && !isSleeping[j]) continue;
```

| j vs i | j state | Action | Why |
|--------|---------|--------|-----|
| j < i | active | Skip | Cặp đã xử lý khi outer ở j |
| j = i | — | Skip | Self |
| j < i | sleeping | **Process** | j không ở outer loop → cặp chưa xử lý |
| j > i | any | **Process** | Cặp mới |

Position re-cache sau mỗi contact (`px = p[i].pos.x`) — distance chính xác hơn. Cell KHÔNG re-hash — correction nhỏ, iterations bù lại, re-hash O(n) quá đắt.

---

## 4. Parameters

| Parameter | Default | |
|-----------|:-------:|---|
| **Spawn** | | |
| mode | Burst | Burst: tất cả khi Start · Stream: spawn dần |
| maxParticles | 10,000 | |
| spawnRange | 10 | Bounding box half-size |
| streamRate | 500 | Particles/s |
| streamSpawnWidth | 2 | Chiều rộng nguồn phát |
| **Size** | | |
| radiusMin / radiusMax | 0.08 / 0.12 | |
| **Physics** | | |
| gravity | (0, -20) | |
| airDrag | 0.01 | |
| frictionCoef | 0.3 | Coulomb μ |
| contactDamping | 0.4 | 0=elastic, 1=fully damped |
| wallFriction | 0.3 | |
| substeps | 1 | Per FixedUpdate |
| collisionIterations | 1 | Per substep |
| **Slope** | | |
| slopeBias | 0.2 | 0=equal, 0.45=upper gets 95% |
| slopeFrictionReduction | 0.7 | Vertical contact friction ×(1-0.7) |
| **Sleep** | | |
| sleepVelocityThreshold | 0.02 | Squared internally |
| sleepFrames | 10 | Consecutive frames |
| wakeOverlapFraction | 0.4 | × min(ri, rj) |
| wakeSpeed | 0.8 | Active particle min speed |
| **Budget** | | |
| maxPhysicsMs | 6 | ms per FixedUpdate |

---

## 5. Files

```
Simulation/
  SandParticle.cs       32B struct: pos, prevPos, frameStartPos, radius, packedColor
  SpatialHash2D.cs      counting-sort, 3-pass O(n), cached particleCells[]
  SandPhysics.cs        static solver + BuildActiveIndices + FastInvSqrt
  SandColors.cs         5 earth tones + HSV jitter → packed uint
  FallingSandSim.cs     orchestrator: parallel arrays, activeIndices, wake tracking, render skip
```

---

## 6. Kiểm thử

| # | Kiểm tra | Pass |
|---|----------|------|
| 1 | Burst 10K | Rơi, tạo đụn cát |
| 2 | Angle of repose | ~20–25° |
| 3 | Stream 500/s | Dòng liên tục, cone tự nhiên |
| 4 | Avalanche | Cascade khi quá dốc |
| 5 | Frame Debugger | 1 draw call |
| 6 | Profiler settled | FixedUpdate < 0.1ms, LateUpdate ≈ 0 |
| 7 | Profiler active | Physics < maxPhysicsMs |
| 8 | GC Alloc | 0 trong simulation path |
| 9 | Boundaries | Không thoát |
| 10 | Enter/Exit Play ×3 | Không error/leak |
| 11 | Wake | Particle mới đánh thức pile |

---

## 7. Tóm tắt hiệu năng

| Metric | Giá trị |
|--------|---------|
| Struct size | 32B — 2/cache line, no straddling |
| Sleep state | Parallel bool[]/byte[] — 64/cache line |
| Non-collision loops | O(awake) via activeIndices |
| Collision outer skip | Compact bool[] — 1B per check |
| Fully settled | FixedUpdate ≈ 0, LateUpdate ≈ 0 |
| GC in hot path | 0 |
| Render | 1 draw call, 1 memcpy (skipped when settled) |
