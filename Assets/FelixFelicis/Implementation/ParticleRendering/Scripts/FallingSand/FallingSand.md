# Falling Sand Simulation

> 10,000 hạt cát (default, configurable) · PBD + Verlet · funnel walls + obstacle collision · particle despawn · GPU instancing 1 draw call

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

### 1.4 Collision Response (particle ↔ particle)

```
delta = pos[j] - pos[i]
invDist = math.rsqrt(delta.x² + delta.y²)    ← SSE rsqrtss intrinsic
dist = distSqr × invDist
overlap = (r_i + r_j) - dist
normal = delta × invDist
```

**4 giai đoạn per contact:**

```
┌──────────────────────────────────────────────────────────────────────┐
│ 0. Wake check (sleeping j only)                                       │
│    if overlap > wakeThreshold AND speed_i > wakeSpeed → wake j        │
│                                                                        │
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

### 1.7 Funnel Walls (thay thế Boundary Box)

Thay boundary box 4 tường axis-aligned bằng **2 thành phễu cong** (EdgeCollider2D). Cát rơi từ miệng rộng phía trên, trượt dọc thành nghiêng, đổ ra miệng hẹp phía dưới.

#### 1.7.1 Data Layout — FunnelSegment (precomputed)

Mỗi cặp điểm liên tiếp trên `EdgeCollider2D.points` tạo 1 segment. Toàn bộ dữ liệu dẫn xuất được **bake 1 lần** — job không tính lại gì:

```
┌──────────────────────────────────────────────────────────────┐
│  aabbMin      float2   8B  ← precomputed AABB, expanded     │
│  aabbMax      float2   8B  ← by broadphaseMargin            │
│  a            float2   8B  ← segment start point (world XY) │
│  edge         float2   8B  ← precomputed b - a              │
│  outNormal    float2   8B  ← unit outward normal             │
│  invLenSq     float    4B  ← 1 / dot(edge, edge)            │
│  Total: 5×8 + 4 = 44 bytes                                  │
└──────────────────────────────────────────────────────────────┘
```

**Tại sao precompute tất cả?** Inner loop chạy `particles × segments × substeps` lần. Với 20K × 10 × 3 = 600K iterations/frame, mỗi phép tính inline tiết kiệm được = 600K ops saved.

| Giá trị | Nếu tính inline | Precomputed |
|---------|-----------------|-------------|
| AABB min/max | 8 min/max ops × 600K = 4.8M | **0** — 4 float comparisons trực tiếp |
| Edge direction `b - a` | 2 ops × 600K = 1.2M | **0** — đọc từ struct |
| `invLenSq` | 1 division × 600K = 600K | **0** — precomputed |

#### 1.7.2 Outward Normal Convention

```
Left wall (negative X side): points ordered top → bottom
  edge d = b - a ≈ (0, -1)
  outNormal = (-d.y, d.x) = (1, 0) → points RIGHT ✓ (into funnel)

Right wall (positive X side): points ordered top → bottom
  edge d = b - a ≈ (0, -1)
  outNormal = (d.y, -d.x) = (-1, 0) → points LEFT ✓ (into funnel)
```

**Bug đã fix (§8.3):** Ban đầu công thức bị đảo → normal hướng ra ngoài → particle bị đẩy xuyên thành thay vì vào trong. Chứng minh đúng: cho left wall edge going downward, `(-d.y, d.x)` cho vector hướng phải. Ký hiệu cụ thể:

```
d = (0.01, -2.55) → perp = (-(-2.55), 0.01) = (2.55, 0.01) → normalize → (+1, ~0) ✓
```

#### 1.7.3 Signed Distance — Anti-Tunneling

**Vấn đề:** Particle nhỏ (radius 0.01) với gravity cao (20) → displacement per substep ≈ 0.059 >> collision zone 0.02 → particle nhảy qua tường trong 1 substep.

**Giải pháp: Signed distance thay vì overlap-only.**

Standard overlap check chỉ phát hiện khi `dist(particle, segment) < radius`. Nếu particle bay qua hoàn toàn → dist lớn → miss.

Signed distance phát hiện particle ở **phía sai** của tường:

```
// Closest point trên segment
t = dot(p - a, edge) × invLenSq,  clamped [0, 1]
delta = (p - a) - edge × t        ← vector từ closest point đến particle

// Signed distance = projection lên outNormal
signedDist = dot(delta, outNormal)

signedDist > r     → safe, skip (phía đúng, xa tường)
signedDist ∈ [0,r] → overlap bình thường → push along delta direction
signedDist < 0     → TUNNELED! → push along outNormal by (r - signedDist)
```

**Tại sao tunneling case dùng `outNormal` thay `delta`?** Khi tunneled, `delta` có thể hướng ngược (từ phía sai) → push sai hướng. `outNormal` luôn hướng đúng (vào funnel interior).

#### 1.7.4 Broadphase Margin — Catch Tunneled Particles

AABB mở rộng phải đủ lớn để particle tunneled không bị reject bởi broadphase trước khi signed distance test chạy.

```
v_max = √(2 × |gravity| × spawnRange)           ← max velocity sau khi rơi full range
maxDisplacement = v_max × subDt + |g| × subDt²   ← max displacement per substep
broadphaseMargin = max(radiusMax, maxDisplacement)
```

Tính **1 lần ở Start()**, bake vào mỗi segment AABB — zero per-frame compute.

Ví dụ scene config (`gravity=20, spawnRange=2, substeps=3`):

```
v_max = √(2 × 20 × 2) = 8.94 units/s
subDt = 0.02 / 3 = 0.0067s
maxDisplacement = 8.94 × 0.0067 + 20 × 0.000044 = 0.06 units
broadphaseMargin = max(0.01, 0.06) = 0.06
```

#### 1.7.5 Collision Response — Dead Stop + Low Friction

Funnel walls dùng **zero restitution** (dead stop, giống tường cũ) + **low Coulomb friction** (~0.05) → cát trượt nhanh dọc thành:

```
1. Capture vel = pos - prevPos
2. pos += normal × penetration                     ← push ra khỏi tường
3. if normalVel < 0:
       prevPos += normal × normalVel               ← dead stop (restitution = 0)
4. tangentVel = vel - normalVel × normal
   correction = min(|tangentVel|, friction × pen)
   prevPos += normalize(tangentVel) × correction   ← Coulomb friction
```

**Vật lý trượt trên mặt nghiêng** — hoạt động tự nhiên, không cần code riêng:

```
Gravity (0, -20) kéo particle xuống
  → va thành phễu nghiêng → position correction đẩy theo normal
  → thành phần velocity dọc surface KHÔNG bị giết (friction 0.05 rất thấp)
  → particle trượt xuống tự do dọc surface

friction = 0.05, penetration ≈ 0.01:
  maxFriction = 0.05 × 0.01 = 0.0005
  tangentVel (dọc thành) ≈ 0.5
  → correction = min(0.5, 0.0005) = 0.0005 → gần như 0
  → cát trượt tự do ✓
```

Cát tích tụ ở cổ phễu do particle-particle collision (§1.4) + slope mechanics (§1.5). Không cần logic đặc biệt.

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

### 1.9 Obstacle Collision

Object 3D (Sphere, Box, Capsule) đặt trong scene → chiếu xuống 2D proxy shape → va chạm hạt cát. Static obstacles có thể **biến mất** runtime (user tương tác).

#### 1.9.1 Chiếu 3D → 2D

| Unity Collider | 2D Proxy | Phép chiếu |
|----------------|----------|------------|
| `SphereCollider` | Circle | `center = position.xy`, `R = collider.radius × max(scaleX, scaleY)` |
| `BoxCollider` | OBB | `center = position.xy`, `halfExtents = (size × scale).xy × 0.5`, `axisDirection = (cos θ, sin θ)` với θ = Z-rotation |
| `CapsuleCollider` (X/Y) | 2D Capsule | `center = position.xy`, project axis lên XY bằng Z-rotation, `R = radius × radialScale`, `halfH = height/2 × axisScale - R` |
| `CapsuleCollider` (Z) | Circle | Trục vuông góc XY → degenerate thành circle |

`collider.center` offset được rotate bởi Z-rotation trước khi cộng vào center — hỗ trợ collider offset trên GameObject xoay.

#### 1.9.2 Data Layout — ObstacleData (52 bytes)

```
┌──────────────────────────────────────────────────────────────┐
│  aabbMin      float2   8B  ← broadphase AABB, pre-expanded  │
│  aabbMax      float2   8B  ← by particleRadiusMax           │
│  center       float2   8B  ← narrowphase: 2D center         │
│  halfExtents  float2   8B  ← shape-dependent (§1.9.1)       │
│  axisDirection float2  8B  ← Box: (cos θ, sin θ)            │
│                                Capsule: medial axis vector   │
│                                Circle: unused (0,0)          │
│  friction     float    4B  ← per-obstacle Coulomb μ         │
│  bounciness   float    4B  ← restitution [0,1]              │
│  shape        byte     1B  ← enum dispatch tag               │
│  [3B padding]                                                │
└──────────────────────────────────────────────────────────────┘
```

Fat struct — tất cả shape parameters lưu chung, `shape` tag quyết định field nào active.

#### 1.9.3 Broadphase — Precomputed Expanded AABB

AABB tính **1 lần khi bake** (obstacle register), mở rộng sẵn bằng `particleRadiusMax`:

```
if (px < obs.aabbMin.x || px > obs.aabbMax.x ||
    py < obs.aabbMin.y || py > obs.aabbMax.y)
    continue;   ← 4 float comparisons, skip toàn bộ narrowphase
```

**Box OBB broadphase:** AABB = world-axis envelope của OBB xoay:

```
absCos = |cos θ|, absSin = |sin θ|
worldHalfX = absCos × hx + absSin × hy    ← tight AABB, not bounding circle
worldHalfY = absSin × hx + absCos × hy
aabbMin = center - worldHalf - particleRadiusMax
aabbMax = center + worldHalf + particleRadiusMax
```

#### 1.9.4 Narrowphase — Circle, OBB, Capsule

**Circle:** Giống particle-particle (§1.4) nhưng obstacle immovable.

**OBB (Box xoay):** Rotate particle vào box-local space, sau đó dùng AABB math:

```
// axisDirection = (cos θ, sin θ) baked tại register time

// ① Inverse rotate particle → local space (Rᵀ × world)
localX =  cos × worldDx + sin × worldDy
localY = -sin × worldDx + cos × worldDy

// ② AABB clamp + distance trong local space (giống AABB code cũ)
clampedX = clamp(localX, -hx, hx)
clampedY = clamp(localY, -hy, hy)
... (inside/outside branches → localNormal, penetration)

// ③ Forward rotate normal → world space (R × local)
normalX = cos × localNX - sin × localNY
normalY = sin × localNX + cos × localNY
```

Cost: 12 extra float ops per colliding particle (4 mul + 2 add inverse, 4 mul + 2 add forward). Broadphase AABB reject → zero cost cho non-colliding.

**Capsule:** Project lên medial axis → clamp → reduce to circle test (§1.4).

#### 1.9.5 Collision Response

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. Capture vel = pos - prevPos (TRƯỚC correction)                    │
│ 2. pos += normal × penetration (push ra)                             │
│ 3. Restitution (only if normalVel < 0):                              │
│      prevPos += normal × normalVel × (1 + bounciness)               │
│ 4. Coulomb friction: clamp tangent vel by μ × penetration            │
└──────────────────────────────────────────────────────────────────────┘
```

**Restitution trong Verlet** — sửa `prevPos` để thay đổi implicit velocity:

```
bounciness=0 → shift = normalVel       → reflected vel = 0 (dead stop)
bounciness=1 → shift = 2 × normalVel   → reflected vel = -normalVel (full bounce)
```

#### 1.9.6 Obstacle Removal — Wake với Gravity Nudge

Khi obstacle biến mất (user tương tác), particles sleeping trên đó phải rơi xuống. 3 vấn đề đã giải quyết:

**Vấn đề 1: Execution order race** — `SandObstacle.OnEnable()` gọi `Register()` trước `FallingSandSim.Start()` tạo registry → silent drop. **Fix:** `SetInstance()` retroactive scan bằng `FindObjectsByType()`.

**Vấn đề 2: Floating cluster** — Particles sleeping trên obstacle → obstacle xóa → particles wake zero velocity → collision correction giữ pile tại chỗ → re-sleep. **Fix:** Wake với **gravity nudge** — shift `prevPos` ngược hướng gravity tạo implicit velocity xuống:

```
nudgeMag = max(radiusMax × 10, sleepThreshold × 20)
nudge = -normalize(gravity) × nudgeMag     ← shift prevPos NGƯỢC gravity
prevPos = pos + nudge                        ← implicit vel = pos - prevPos = -nudge = THEO gravity
```

Nudge phải >> collision correction (~0.014/neighbor) để gravity thắng. Với `radiusMax=0.01`: nudge = 0.2 >> 0.014.

**Vấn đề 3: Wake 20K particles gây FPS drop** — Wake tất cả (kể cả despawned ở 9999,9999) → 20K active × 3 substeps × 4 iters = chết. **Fix:** Chỉ wake particles **trong simulation bounds** (`±spawnRange + 1`). Despawned particles ở (9999,9999) bị skip.

#### 1.9.7 Registration — `SandObstacleRegistry`

```
SandObstacle.OnEnable()  → Register(this)     ← no-op nếu registry chưa tồn tại
SandObstacle.OnDisable() → Unregister(this)   ← set ObstacleWasRemoved = true

FallingSandSim.Start()   → SetInstance(registry)
                            → FindObjectsByType() retroactive scan

FallingSandSim.FixedUpdate:
  if ObstacleWasRemoved → WakeNearbyParticles() (1× per removal)
  GetObstacleData() → dirty? rebuild NativeArray : return cached
```

NativeArray grow-only (giống `SpatialHash2D.EnsureCapacity`). Zero GC per frame khi obstacles không đổi.

---

### 1.10 Particle Despawn

Particles rơi qua miệng dưới phễu → **soft-kill**: teleport + sleep.

```
DespawnOutOfBoundsJob (1×/frame, sau substeps):
  if p.pos.y < despawnBelowY:
      p.pos = (9999, 9999)       ← ngoài spatial hash grid + camera
      p.prevPos = p.pos           ← zero velocity
      p.frameStartPos = p.pos
      isSleeping[i] = true        ← skip tất cả physics
```

**Tại sao teleport + sleep thay vì compact/recycle?**

- Compact NativeArray = O(n) copy, phá particle indices, spatial hash invalid
- Teleport + sleep = zero ongoing cost, slot "chết" nhưng vô hại (ngoài camera, sleeping)
- Phase 2 có thể thêm free-list recycle nếu slot exhaustion thành vấn đề

#### Stream Mode — Unlimited Spawn + Auto-Growing Arrays

Stream mode spawn liên tục không giới hạn. `maxParticles` chỉ là **initial capacity** — khi `spawnedCount` vượt capacity, tất cả parallel NativeArrays (`particles`, `isSleeping`, `sleepCounters`, `activeIndices`) grow bằng **doubling** (giống `SpatialHash2D.EnsureCapacity`):

```
EnsureParticleCapacity(spawnedCount + toSpawn):
  if requiredCount ≤ particleCapacity → return (zero cost)
  newCapacity = particleCapacity × 2 (repeat until ≥ requiredCount)
  particles      = GrowArray(old, spawnedCount, newCapacity)
  isSleeping     = GrowArray(...)
  sleepCounters  = GrowArray(...)
  activeIndices  = GrowArray(...)

GrowArray: alloc new → NativeArray.Copy(old, new, usedCount) → dispose old
```

**Tại sao doubling?** Amortize allocation: 500 particles/s → ~14 doublings từ 10K → 10M thay vì hàng nghìn exact-size resize. Mỗi grow = 1 alloc + 1 memcpy + 1 dispose — hiếm khi xảy ra.

**Bounded trong thực tế:** Despawn ở đáy phễu giữ live particle count ổn định. Grow chỉ xảy ra giai đoạn khởi động khi particles chưa đến miệng phễu. Sau đó spawn rate ≈ despawn rate → capacity không tăng nữa.

Burst mode **không grow** — allocate chính xác `maxParticles` 1 lần, `SpawnParticle()` early-return khi full.

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

**Vấn đề:** Physics loops iterate 10K particles, skip 9K sleeping.

**Giải pháp:** Đầu FixedUpdate scan `isSleeping[]` → build `activeIndices[]` → tất cả loops chỉ iterate awake:

```
for a in 0..awakeCount:
    i = activeIndices[a]      ← chỉ active particles
    // physics trên p[i]

90% sleeping → 1K iterations thay 10K → ~10× nhanh
100% sleeping → awakeCount = 0 → early exit, FixedUpdate ≈ 0
```

**Wake tracking:** `NativeReference<bool> wakeOccurred` → rebuild `activeIndices` trước bước tiếp.

---

### 2.3 Render Upload Skip

```
needsRenderUpload = false ──► LateUpdate skip ──► GPU reuse buffer cũ
needsRenderUpload = true  ──► awakeCount > 0 hoặc spawn mới ──► upload
```

Settled: 0 CPU cost + 0 bandwidth.

---

### 2.4 Inlined Collision + Fast Math

| Kỹ thuật | Lý do |
|----------|-------|
| Inline toàn bộ SolveContact (~150 dòng) | Burst IJob với ref struct params có significant call overhead kể cả `[AggressiveInlining]` |
| `math.rsqrt()` | SSE `rsqrtss` instruction, ~2× nhanh hơn `1/Mathf.Sqrt` |
| `math.sqrt(x) = x × rsqrt(x)` | Tận dụng rsqrt đã tính |

---

### 2.5 Substeps + Time Budget

```csharp
obstacleData = registry.GetObstacleData()     // ← 1×/frame
funnelSegments = funnel.GetSegments()          // ← 1×/frame (baked data)

for (int s = 0; s < substeps; s++)
{
    if (s > 0 && stopwatch.ElapsedTicks > budgetTicks) break;
    Integrate → BuildHash → ResolveCollisions → ResolveObstacles → ResolveFunnel
}
DespawnOutOfBounds                              // ← 1×/frame
UpdateSleep
```

Budget `maxPhysicsMs` → degrade gracefully (ít substeps) thay vì frame drop.

---

### 2.6 Funnel — Bake-Once Precompute

Tất cả dữ liệu dẫn xuất per-segment tính 1 lần ở `Start()`:

| Precomputed | Phép tính tiết kiệm | Per 600K iters/frame |
|-------------|---------------------|---------------------|
| AABB min/max | 8 min/max ops | 4.8M ops |
| Edge direction `b - a` | 2 subtract ops | 1.2M ops |
| `invLenSq = 1/dot(e,e)` | 1 dot + 1 div | 1.2M ops |
| outNormal | 1 perpendicular + normalize | 2.4M ops |
| **Total saved** | | **~9.6M float ops/frame** |

Broadphase margin cũng tính 1 lần ở `Start()` (2× Mathf.Sqrt) → bake vào segment AABB.

---

### 2.7 Obstacle — Broadphase AABB at Struct Top

`ObstacleData` đặt `aabbMin/aabbMax` ở **đầu struct** (offset 0–16). CPU prefetch loads 64B cache line → broadphase reject chỉ cần đọc 16B đầu → nếu reject, phần còn lại (36B) không cần load.

OBB broadphase tính tight AABB (không dùng bounding circle):

```
worldHalf = (|cos|×hx + |sin|×hy, |sin|×hx + |cos|×hy)
```

Tight hơn bounding circle `√(hx² + hy²)` → ít false positive → ít narrowphase.

---

### 2.8 Burst + NativeArray + IJob

| Kỹ thuật | Lý do |
|----------|-------|
| `NativeArray<T>` thay managed arrays | Burst bỏ bounds check, contiguous memory guaranteed |
| `[BurstCompile(FloatMode.Fast)]` cho 9 IJob (8 SandPhysics + 1 FallingSandSim) | Auto-SIMD, no GC, fast float reorder |
| `math.rsqrt()` | SSE `rsqrtss` intrinsic |
| Obstacle + Funnel data `[ReadOnly]` | Burst biết không cần safety check ghi → no memory fence |
| `NativeReference<int/bool>` | Output channels từ Jobs → managed, không cần callback |
| `UpdateSleep` giữ managed | 1 lần/frame, O(awake), cần kết quả ngay cho `needsRenderUpload` |

---

## 3. Thiết kế hệ thống

### 3.1 Dependency Graph

```
FallingSandSim (MonoBehaviour — orchestrator)
  ├── SandParticle[]       32B × maxParticles
  ├── bool[] isSleeping    ┐
  ├── byte[] sleepCounters ├ parallel sleep arrays
  ├── int[] activeIndices  ┘ rebuilt each FixedUpdate
  ├── SpatialHash2D          counting-sort (particle-particle)
  ├── SandObstacleRegistry   dirty-flag NativeArray<ObstacleData>
  │     └── SandObstacle[]   MonoBehaviour per scene obstacle
  ├── SandFunnel             baked NativeArray<FunnelSegment>
  │     ├── EdgeCollider2D   left wall
  │     └── EdgeCollider2D   right wall
  ├── SandPhysics            static stateless solver (8 Burst IJob)
  ├── SandColors             palette (spawn only)
  └──► ParticleProvider.Writer → ParticleDrawer → GPU → shader
```

### 3.2 Frame Lifecycle

```
Start()
  create SandObstacleRegistry + SetInstance (retroactive scan)
  alloc particles[], isSleeping[], sleepCounters[], activeIndices[]
  create SpatialHash2D
  bake funnel segments (precompute AABB + edge + normal + margin)
  if Burst: spawn all

FixedUpdate()                              LateUpdate()
─────────────────────────────────          ─────────────────────────────
  if Stream: StreamSpawn()                   if !needsRenderUpload: return
  if spawnedCount == 0: return                upload ALL to GPU (CopyToRenderDataJob)
  if ObstacleWasRemoved:
    WakeNearbyParticles(nudge)
  awakeCount = BuildActiveIndices()
  if awakeCount == 0: return  ◄─ early exit
  needsRenderUpload = true
  SnapshotFrameStart()
  obstacleData = registry.GetObstacleData()  ← 1×/frame
  funnelSegments = funnel.GetSegments()      ← 1×/frame
  for substep:
    if over budget: break
    Integrate()              O(awake)
    hash.Build()             O(n)
    ResolveCollisions()      O(awake × ~27)
      if wake → rebuild activeIndices
    ResolveObstacles()       O(awake × obstacleCount)
    ResolveFunnel()          O(awake × segmentCount)
  DespawnOutOfBounds()       O(awake), 1×/frame
  UpdateSleep()              O(awake)
```

### 3.3 Collision Pair Processing

```csharp
// Outer: activeIndices iteration (O(awake))
// Inner: 3×3 neighbor cells
int j = sorted[off + k];
if (j == i) continue;                          // self
if (!isSleeping[j] && j < i) continue;         // active pair đã xử lý
```

Position re-cache sau mỗi contact (`px = p[i].pos.x`) — distance chính xác hơn. Cell KHÔNG re-hash — correction nhỏ, iterations bù lại, re-hash O(n) quá đắt.

---

## 4. Parameters

| Parameter | Code Default | |
|-----------|:-------:|---|
| **Spawn** | | |
| mode | Burst | Burst: spawn tất cả khi Start (cố định maxParticles) · Stream: spawn liên tục, không giới hạn |
| maxParticles | 10,000 | Burst: tổng số hạt. Stream: initial NativeArray capacity (tự grow) |
| spawnRange | 10 | Spatial hash bounds |
| streamRate | 500 | Particles/s (Stream mode only) |
| streamSpawnWidth | 2 | Chiều rộng nguồn phát |
| **Size** | | |
| radiusMin / radiusMax | 0.08 / 0.12 | |
| **Physics** | | |
| gravity | (0, -20) | |
| airDrag | 0.01 | |
| frictionCoef | 0.3 | Coulomb μ (particle-particle) |
| contactDamping | 0.4 | 0=elastic, 1=fully damped |
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
| **Funnel** (SandFunnel) | | |
| friction | 0.05 | Low = cát trượt nhanh (glass/metal) |
| despawnBelowY | -12 | Dưới miệng phễu |
| **Obstacle** (per SandObstacle) | | |
| friction | 0.3 | Coulomb μ surface |
| bounciness | 0 | Restitution (0=dead stop, 1=elastic) |
| particleRadiusMax | 0.12 | AABB expand. Phải match FallingSandSim.radiusMax |

---

## 5. Files

```
Simulation/
  SandParticle.cs           32B struct: pos, prevPos, frameStartPos, radius, packedColor
  SpatialHash2D.cs          counting-sort, 3-pass O(n), cached particleCells[]
  SandPhysics.cs            static solver: 8 Burst IJob + managed UpdateSleep
                              BuildActiveIndices, SnapshotFrameStart, Integrate,
                              SpatialHashBuild, ResolveCollisions, ResolveObstacles,
                              ResolveFunnel, DespawnOutOfBounds
  SandColors.cs             5 earth tones + HSV jitter → packed uint
  FallingSandSim.cs         orchestrator: parallel arrays, activeIndices,
                              wake tracking, obstacle/funnel wiring, despawn, render skip.
                              Also owns CopyToRenderDataJob (private Burst IJob, 9th total)
  ObstacleData.cs           52B blittable struct + ObstacleShape enum (Circle/Box/Capsule)
  SandObstacle.cs           per-obstacle MonoBehaviour: 3D→2D projection (OBB rotation),
                              precomputed AABB, Inspector friction/bounciness
  SandObstacleRegistry.cs   managed collector: dirty-flag NativeArray, retroactive registration,
                              ObstacleWasRemoved flag for wake
  FunnelSegment.cs          44B precomputed struct: aabbMin/Max, a, edge, outNormal, invLenSq
  SandFunnel.cs             MonoBehaviour: 2 EdgeCollider2D → baked NativeArray<FunnelSegment>,
                              BakeWithMargin(margin), Friction, DespawnBelowY
```

---

## 6. Kiểm thử

| # | Kiểm tra | |
|---|----------|---|
| 1 | Burst 10K | Rơi, tạo đụn cát |
| 2 | Angle of repose | ~20–25° |
| 3 | Stream spawn | Dòng liên tục, cone tự nhiên |
| 4 | Avalanche | Cascade khi quá dốc |
| 5 | Frame Debugger | 1 draw call |
| 6 | Profiler settled | FixedUpdate < 0.1ms, LateUpdate ≈ 0 |
| 7 | Profiler active | Physics < maxPhysicsMs |
| 8 | GC Alloc | 0 trong simulation path |
| 9 | Enter/Exit Play ×3 | Không error/leak |
| 10 | Funnel V-shape | Cát trượt dọc thành, đổ ra miệng dưới |
| 11 | Funnel trượt nhanh | friction=0.05: cát không bám thành |
| 12 | Funnel tích tụ cổ | Cát pile up ở cổ hẹp, particles chèn → vài rơi qua |
| 13 | Funnel tunneling | radius=0.01 + gravity=20: cát KHÔNG xuyên qua thành |
| 14 | Despawn | Particles dưới despawnBelowY → sleeping + teleport |
| 15 | Obstacle Circle | Sphere: cát trượt 2 bên, tích tụ trên |
| 16 | Obstacle OBB | Box xoay 45°: cát trượt dọc cạnh nghiêng |
| 17 | Obstacle Capsule | Capsule xoay: cát trượt dọc axis |
| 18 | Obstacle bounciness | bounciness=0.5: cát nảy nhẹ |
| 19 | Obstacle removal | Tắt obstacle → cát rơi xuống, không lơ lửng, FPS ổn |
| 20 | 20+ obstacles | Particle không thoát, frame time OK |

---

## 7. Tóm tắt hiệu năng

| Metric | Giá trị |
|--------|---------|
| SandParticle | 32B — 2/cache line, no straddling |
| FunnelSegment | 44B, all precomputed (AABB + edge + normal + invLenSq). AABB at struct top for early reject |
| ObstacleData | 52B, broadphase AABB at struct top (16B prefetch → reject path) |
| Sleep state | Parallel NativeArray bool/byte — 64/cache line |
| Physics loops | O(awake) via activeIndices |
| Funnel broadphase | Precomputed AABB (baked at Start), 4 float comparisons per segment |
| Funnel narrowphase | Signed distance (catch tunneling), precomputed edge + invLenSq |
| Funnel precompute savings | ~9.6M float ops/frame eliminated (AABB + edge + invLenSq + normal) |
| Obstacle broadphase | Precomputed AABB (baked at register), OBB tight envelope |
| Obstacle narrowphase | Circle (rsqrt), OBB (12 ops rotate), Capsule (dot+clamp+rsqrt) |
| Data queries | 1×/frame hoisted outside substep loop (obstacles + funnel) |
| Burst compilation | 9 IJob total (8 in SandPhysics + 1 in FallingSandSim), FloatMode.Fast, auto-SIMD |
| Fully settled | FixedUpdate ≈ 0, LateUpdate ≈ 0 |
| GC in hot path | 0 |
| Render | 1 draw call, 1 memcpy (skipped when settled) |

---

## 8. Bug fixes — Phân tích & giải pháp

### 8.1 Execution Order Race — Obstacle không đăng ký

**Triệu chứng:** Obstacle trong scene không va chạm. `obstacleCount = 0` mãi mãi.

**Nguyên nhân:** Unity không đảm bảo thứ tự `OnEnable()`/`Start()` giữa MonoBehaviours. `SandObstacle.OnEnable()` gọi `Register()` khi registry chưa tồn tại (`instance == null`) → silent drop.

**Fix:** `SandObstacleRegistry.SetInstance()` thực hiện retroactive `FindObjectsByType<SandObstacle>()` scan. `Register()` check `Contains()` tránh duplicate. `SandObstacle.RebuildCachedData()` lazy-init `DetectCollider()` phòng `Awake()` chưa chạy.

### 8.2 Funnel Normal Đảo Ngược — Cát bị đẩy xuyên ra ngoài

**Triệu chứng:** ~50% cát xuyên qua thành phễu, phần còn lại bị "đẩy chéo đối xứng" với mặt nghiêng.

**Nguyên nhân:** Perpendicular formula bị đảo. Left wall `outNormal` hướng sang TRÁI (ra ngoài phễu) thay vì PHẢI (vào trong). Code nhận particle bên trong phễu là "tunneled" → đẩy ra ngoài.

**Chứng minh bằng data:**

```
Left wall: a=(-1.43, 1.70), b=(-1.42, -0.85), d = (0.01, -2.55)

SAI:  perp = (d.y, -d.x) = (-2.55, -0.01) → normalize → (-1, 0) ← hướng TRÁI ✗
ĐÚNG: perp = (-d.y, d.x) = (2.55, 0.01)   → normalize → (+1, 0) ← hướng PHẢI ✓
```

**Fix:** Swap formula: left = `(-d.y, d.x)`, right = `(d.y, -d.x)`. Kèm comment chứng minh.

### 8.3 Tunneling — Particle nhỏ xuyên qua tường

**Triệu chứng:** Cát radius 0.01 với gravity 20 xuyên qua thành phễu.

**Nguyên nhân:** Displacement per substep (0.059) >> collision zone (2 × 0.01 = 0.02). Particle nhảy từ bên này sang bên kia trong 1 substep.

**Root causes (2 tầng):**

1. **Overlap-only check:** `distSq >= r²` chỉ phát hiện overlap tại vị trí hiện tại. Particle đã ở phía sai hoàn toàn → `distSq` lớn → miss.
2. **Broadphase quá hẹp:** AABB expand bằng `particleRadiusMax` (0.12) nhưng tunneled particle có thể ở xa segment hơn radius.

**Fix (2 tầng):**

1. **Signed distance test:** `signedDist = dot(delta, outNormal)`. Nếu < 0 → particle ở phía sai → push back theo `outNormal` bất kể `distSq`.
2. **Dynamic broadphase margin:** `max(radiusMax, v_max × subDt + g × subDt²)` — tính 1 lần ở `Start()`, bake vào segment AABB.

### 8.4 Floating Cluster — Cát lơ lửng sau khi tắt obstacle

**Triệu chứng:** Tắt obstacle → phần lớn cát rơi nhưng vài hạt vẫn lơ lửng.

**Nguyên nhân (3 tầng tiến hóa):**

1. **Sleep system không biết obstacle biến mất:** Particles sleeping → không có gì đánh thức → nổi vĩnh viễn. → Fix: `ObstacleWasRemoved` flag + `WakeNearbyParticles()`.

2. **Wake 20K particles → FPS chết:** Wake tất cả kể cả despawned (9999,9999) → 20K active × 3 substeps × 4 iters. → Fix: Chỉ wake particles trong `±spawnRange + 1`.

3. **Pile equilibrium → instant re-sleep:** Particles wake zero velocity → collision correction giữ pile tại chỗ → displacement ≈ 0 → re-sleep. → Fix: Gravity nudge `max(radiusMax × 10, sleepThreshold × 20)` — đủ lớn để thắng collision correction → pile tan dần.

### 8.5 Box Không Tính Z-Rotation

**Triệu chứng:** Box xoay 45° nhưng cát va chạm như box axis-aligned.

**Nguyên nhân:** `BuildBox()` bỏ qua `transform.eulerAngles.z`. Narrowphase dùng AABB math không rotate.

**Fix:** `axisDirection = (cos θ, sin θ)` baked tại register. Narrowphase: inverse-rotate particle → local space, tính contact, forward-rotate normal → world. Broadphase: tight OBB envelope thay vì AABB.
