# Falling Sand Simulation

> Mô phỏng 10,000 hạt cát rơi bằng vật lý liên tục (Position-Based Dynamics).
> Tích hợp hệ thống ParticleRendering — GPU instancing, 1 draw call.

Namespace: `FelixFelicis.ParticleRendering.Simulation` · Assembly: `com.FelixFelicis`

---

## 1. Nền tảng lý thuyết

### 1.1 Position-Based Dynamics (PBD)

PBD giải quyết collision bằng **position correction** thay vì force accumulation. Ưu điểm:

- **Ổn định vô điều kiện** — không bao giờ explode dù timestep lớn
- **Velocity derived** — `vel = pos - prevPos` (Verlet integration) → friction/damping tự nhiên
- **Substeps** kiểm soát chất lượng — 3 substeps × 4 iterations ở 60fps đủ cho 10K particles

```
Verlet integration (mỗi substep):
  vel = pos - prevPos        ← implicit velocity
  vel *= (1 - airDrag)       ← drag
  prevPos = pos
  pos += vel + gravity × dt²  ← Störmer-Verlet
```

So sánh với các approach khác:

| Approach | Ưu | Nhược | Chọn? |
|----------|------|-------|:-----:|
| **PBD** (Verlet + constraints) | Ổn định, friction tự nhiên, dễ tune | Cần nhiều iterations | ✓ |
| Force-based (F = ma) | Chính xác vật lý | Dễ explode, stiff contacts → timestep nhỏ | ✗ |
| Grid cellular automaton | O(n), stacking hoàn hảo | Snap vào grid, không mượt | ✗ |
| Impulse-based | Tốt cho rigid body | Micro-jitter khi nhiều contact | ✗ |

---

### 1.2 Spatial Hashing — Counting Sort

Collision detection O(n²) không chấp nhận được với 10K particles. Spatial hash chia không gian thành ô lưới, chỉ kiểm tra 3×3 neighborhood:

```
Complexity: O(n × k) với k = trung bình particles/cell ≈ 2–4
```

**Counting-sort approach** (zero per-cell limit, O(n)):

```
Pass 1: Đếm particles mỗi cell         → cellCounts[cellCount]
Pass 2: Prefix-sum → vị trí bắt đầu     → cellOffsets[cellCount]
Pass 3: Scatter particle index vào mảng  → sortedIndices[particleCount]

Lookup: QueryCell(cx, cy) → (offset, count) trong sortedIndices
```

```
Ví dụ 8 particles, grid 3×3:

Cell:      0    1    2    3    4    5    6    7    8
Count:     1    0    2    0    1    3    0    0    1
Offset:    0    1    1    3    3    4    7    7    7

sortedIndices: [4 | _ | 2,7 | _ | 0 | 1,5,6 | _ | _ | 3]
                ↑       ↑         ↑    ↑               ↑
              cell0   cell2    cell4  cell5          cell8
```

Tại sao counting-sort thay vì hash table:

| | Hash table (`Dictionary`) | Counting-sort |
|---|---|---|
| GC | Resize → GC spike | Pre-allocated, zero GC |
| Cache | Random access, cache miss | Sequential scan, cache-friendly |
| Rebuild | Clear + re-insert O(n) amortized | 3-pass O(n) guaranteed |
| Memory | ~40 bytes/entry (managed) | ~4 bytes/entry (int index) |

Cell size = `2.2 × radiusMax`. Hệ số 2.2 (thay vì 2.0) thêm margin nhỏ cho numerical precision — tránh particle rơi đúng biên cell bị miss.

---

### 1.3 Collision Response

```
Hai particle i, j overlap:

  delta = pos[j] - pos[i]
  dist = length(delta)
  overlap = (r_i + r_j) - dist
  normal = delta / dist

  ┌─────────────────────────────────────────────────┐
  │  Position correction (tách ra):                  │
  │    if j sleeping:                                │
  │      pos[i] -= normal × overlap    (chỉ i di)   │
  │    else:                                         │
  │      pos[i] -= normal × overlap × 0.5           │
  │      pos[j] += normal × overlap × 0.5           │
  │                                                  │
  │  Coulomb friction (tangential):                  │
  │    tangent = relVel - dot(relVel, normal) × n    │
  │    correction = min(|tangent|, μ × overlap)      │
  │    apply correction along tangent direction      │
  │                                                  │
  │  Contact damping (energy loss):                  │
  │    relNormalVel = dot(relVel, normal) × normal   │
  │    prevPos[i] += relNormalVel × damping × 0.5    │
  │    prevPos[j] -= relNormalVel × damping × 0.5    │
  └─────────────────────────────────────────────────┘
```

**Friction → Angle of repose**: hệ số ma sát Coulomb `μ = 0.6` → góc nghỉ ≈ `arctan(0.6)` ≈ 31°. Cát khô tự nhiên: 25–35°. Đụn cát hình thành tự nhiên khi tangential friction ngăn particle trượt xuống quá nhanh.

---

### 1.4 Sleep System

10,000 particles active mỗi frame = 10K × 9 cells × avg 3 particles/cell × 4 iterations = ~1M collision checks. Sau khi settle, >90% particles nằm yên → sleep system cắt cost xuống <100K checks.

```
                ┌─────────────┐
                │   Active    │◄─── collision overlap > wakeThreshold
                │  (physics)  │
                └─────┬───────┘
                      │ speed < threshold
                      │ for sleepFrames consecutive
                      ▼
                ┌─────────────┐
                │  Sleeping   │ skip integrate
                │ (obstacle)  │ skip initiate collision
                └─────────────┘ stay in spatial hash
```

**Sleeping particles luôn nằm trong spatial hash** — chúng hoạt động như vật cản cố định. Active particles va vào sleeping particle → sleeping particle chỉ di chuyển khi overlap vượt `wakeThreshold` (30% radius).

---

### 1.5 Boundary Handling

Tường 2 bên + sàn + trần:

```
│wall                              wall│
│                                      │
│        ░░                            │
│       ░░░░                           │   particles clamp + kill
│      ░░░░░░       ░░                 │   velocity component
│    ░░░░░░░░░░░░░░░░░░░░              │   normal to wall
│░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
└──────────────────────────────────────┘
  floor (-spawnRange): pos.y = max(pos.y, -spawnRange + radius)
  wall friction: giảm velocity dọc tường khi tiếp xúc
```

Wall friction cùng cơ chế Coulomb — `correction = clamp(tangentVel, ±μ × penetration)`. Cát trượt dọc tường chậm dần, tạo friction mark tự nhiên.

---

### 1.6 Color Palette

5 base tones (earth palette) với HSV jitter mỗi particle:

```
#E8D5A3  ████  light tan       ─┐
#D4B896  ████  wheat            │  Random 1 trong 5
#C4A882  ████  sand             │  + ±5° hue
#B09070  ████  dark sand        │  + ±10% saturation
#9A7B5B  ████  brown           ─┘  + ±8% value
```

Pack thành `uint` (RGBA8) 1 lần khi spawn, lưu trong `SandParticle.packedColor`. Zero per-frame cost — `LateUpdate` chỉ copy uint sang render buffer.

---

## 2. Quyết định tối ưu

### 2.1 Verlet thay vì Euler

| | Euler | Verlet (Störmer) |
|---|---|---|
| State | pos + vel (2 × Vector2) | pos + prevPos (2 × Vector2) |
| Integration | `pos += vel × dt` | `pos += (pos - prevPos) + accel × dt²` |
| Constraint | Sửa pos → vel lệch → jitter | Sửa pos → vel tự cập nhật |
| Friction | Phải tính explicit | Implicit từ position delta |

PBD sửa position trực tiếp khi resolve collision. Verlet tự động "hiểu" velocity mới từ delta — không cần recalculate. Euler phải track velocity riêng, sửa velocity khi constraint → phức tạp hơn, dễ jitter.

### 2.2 FixedUpdate + LateUpdate split

```
FixedUpdate (physics @ fixed timestep):
  ├─ Integrate
  ├─ Build spatial hash
  ├─ Resolve collisions
  ├─ Resolve boundaries
  └─ Update sleep

LateUpdate (render @ display framerate):
  └─ Upload to GPU via ParticleProvider.Writer
```

Physics chạy ở fixed timestep (default 50Hz) → deterministic, framerate-independent. Upload chạy ở display framerate → GPU nhận data mới nhất trước render. Nếu physics skip 1 render frame (lag), particles vẫn render vị trí cuối — không flicker.

### 2.3 Pre-allocated arrays thay vì List/Dictionary

| | `List<T>` / `Dictionary` | Pre-allocated `T[]` |
|---|---|---|
| Grow | Amortized O(1) nhưng GC spike khi resize | Alloc 1 lần, capacity = maxParticles |
| GC | Managed heap, GC pause risk | Struct array trên managed heap, no boxing |
| Access | Bounds check + virtual dispatch | Direct indexed access |

`SandParticle[]` cố định kích thước `maxParticles`. `activeCount` track bao nhiêu đang sống. Stream mode thêm particles bằng cách tăng `activeCount`.

### 2.4 Substeps thay vì small timestep

3 substeps × 4 collision iterations = 12 constraint passes per `FixedUpdate`. Thay vì giảm `Time.fixedDeltaTime` (ảnh hưởng toàn bộ game), substeps tự chia nhỏ bên trong simulation:

```csharp
float subDt = Time.fixedDeltaTime / substeps;
for (int s = 0; s < substeps; s++)
{
    Integrate(subDt);
    BuildHash();
    ResolveCollisions(iterations);
    ResolveBoundaries();
    UpdateSleep();
}
```

Mỗi substep dùng `dt = fixedDeltaTime / substeps` → gravity chính xác, không tunneling qua sàn.

---

## 3. Thiết kế hệ thống

### 3.1 Dependency Graph

```
FallingSandSim (MonoBehaviour orchestrator)
  ├── owns ──► SandParticle[] (pre-allocated)
  ├── owns ──► SpatialHash2D (counting-sort)
  ├── calls ──► SandPhysics (static solver)
  ├── calls ──► SandColors.GeneratePacked() (at spawn)
  └── writes ──► ParticleProvider.Writer (IInstanceWriter<ParticleRenderData>)
                    └── ParticleDrawer → GPU buffer → ParticleRenderPass → shader
```

### 3.2 Frame Lifecycle

```
Start()
  ├─ Allocate particles[maxParticles]
  ├─ Create SpatialHash2D(cellSize, bounds, maxParticles)
  └─ if Burst: SpawnBurst() → 10K particles, random positions

FixedUpdate()                              LateUpdate()
──────────────────────────────────         ─────────────────────────────
  if Stream: StreamSpawn(rate)              writer.BeginFrame(activeCount)
  for substep in 0..substeps:               for i in 0..activeCount:
    Integrate(gravity, drag, subDt)           buffer[i] = { pos, radius, color }
    hash.Build(particles, count)            writer.EndFrame(activeCount)
    ResolveCollisions(hash, μ, damp)
    ResolveBoundaries(walls, floor)
    UpdateSleep(threshold, frames)
```

### 3.3 Parameters (Inspector)

| Parameter | Default | Purpose |
|-----------|---------|---------|
| gravity | (0, -20) | Rơi nhanh hơn thực tế cho dramatic effect |
| airDrag | 0.01 | Terminal velocity |
| frictionCoef | 0.6 | Angle of repose ≈ 31° |
| contactDamping | 0.3 | Energy loss per contact |
| wallFriction | 0.5 | Friction dọc tường/sàn |
| substeps | 3 | Physics quality |
| collisionIterations | 4 | Constraint convergence |
| sleepVelocityThreshold | 0.01 | Ngưỡng ngủ |
| sleepFrames | 30 | Frames liên tiếp dưới ngưỡng |
| wakeOverlapFraction | 0.3 | % radius overlap để đánh thức |
| radiusMin / radiusMax | 0.08 / 0.12 | Kích thước particle |

---

## 4. File Reference

```
ParticleRendering/
├─ FallingSand.md                           ← tài liệu này
└─ Scripts/
    └─ Simulation/
        ├─ SandParticle.cs                  ← struct: pos, prevPos, radius, color, sleep
        ├─ SpatialHash2D.cs                 ← counting-sort spatial hash, O(n) rebuild
        ├─ SandPhysics.cs                   ← static solver: integrate, collide, boundary, sleep
        ├─ SandColors.cs                    ← natural sand palette (5 tones + HSV jitter)
        └─ FallingSandSim.cs                ← MonoBehaviour orchestrator (replaces ParticleDebugSim)
```

| File | Vai trò |
|------|---------|
| `SandParticle` | Data struct — pos + prevPos (Verlet), radius, packedColor, sleepCounter, isSleeping. Property `Velocity` = pos - prevPos. `SpeedSqr` avoids sqrt |
| `SpatialHash2D` | Counting-sort spatial hash. 3-pass build, pre-allocated int arrays. `QueryCell(cx,cy) → (offset, count)` vào `sortedIndices`. cellSize = 2.2 × radiusMax |
| `SandPhysics` | Static methods — pure functions trên particle array + hash. `Integrate`, `ResolveCollisions`, `ResolveBoundaries`, `UpdateSleep`. Không giữ state |
| `SandColors` | 5 base tones + HSV jitter → `uint` packed. Gọi 1 lần khi spawn |
| `FallingSandSim` | Orchestrator — Inspector config, spawn logic (Burst/Stream), `FixedUpdate` physics loop, `LateUpdate` render upload |

---

## 5. Kiểm thử

| # | Kiểm tra | Pass | Debug nếu fail |
|---|----------|------|-----------------|
| 1 | Burst 10K particles | Rơi xuống, tạo đụn cát | Gravity, collision params |
| 2 | Angle of repose | Độ dốc ≈ 30° | frictionCoef (0.6 → arctan ≈ 31°) |
| 3 | Stream 500/s | Dòng cát liên tục, cone tự nhiên | streamRate, streamSpawnWidth |
| 4 | Avalanche | Đụn quá dốc → cascade | Collision iterations, friction |
| 5 | Frame Debugger | 1 draw call | ParticleRendering pipeline |
| 6 | Profiler > 60 FPS (settled) | Physics < 2ms khi 90% sleep | Sleep system |
| 7 | GC Alloc | 0 trong simulation path | Profiler deep profile |
| 8 | Boundaries | Không thoát tường/sàn | ResolveBoundaries params |
| 9 | Enter/Exit Play ×3 | Không error, không leak | OnDestroy cleanup |
| 10 | Cát trượt trên nhau | Mượt, không jitter | contactDamping, iterations |

---

## 6. Mở rộng

| Ưu tiên | Tính năng | Thay đổi |
|---------|-----------|----------|
| A | Mouse interaction | Raycast → push/attract particles trong radius |
| B | Multi-material | Thêm type field: sand, water, stone — mỗi loại params khác |
| C | Compute shader simulation | GPU-only: integrate + hash + collide trên compute shader |
| D | Particle pooling | Stream mode recycle particles rơi ra biên |
| E | Terrain collision | SDF terrain → particles settle theo địa hình |

---

## 7. Tóm tắt hiệu năng

| Metric | Giá trị |
|--------|---------|
| Particles | 10,000 |
| Physics timestep | FixedUpdate (50Hz default) |
| Substeps | 3 per FixedUpdate |
| Collision iterations | 4 per substep |
| Spatial hash rebuild | O(n), 3-pass counting-sort |
| Collision checks (active) | ~O(n × k × 9), k ≈ 2–4 |
| Sleep ratio (settled) | >90% → physics cost near-zero |
| GC allocation | 0 in hot path |
| Render draw calls | 1 (via ParticleRendering) |
| Upload | 1 memcpy (NativeArray → SetData) |
