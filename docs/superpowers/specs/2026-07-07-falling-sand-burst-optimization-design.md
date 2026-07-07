# FallingSand Burst Optimization

> 10K particles, giữ nguyên behavior, giảm CPU cost ~5-10× qua NativeArray + Burst + IJob + collision O(awake)

Namespace: `FelixFelicis.ParticleRendering.Simulation` · Branch: `develop/falling_sand`

---

## 1. Mục tiêu

- **Giữ 10K particles**, giảm ms/frame cho physics
- **NativeArray + Burst + IJob** (không IJobParallelFor)
- **Collision outer loop O(n) → O(awake)** — thay đổi thuật toán lớn nhất
- **Behavior identical** — cùng pair order, cùng physics output
- Spatial hash vẫn rebuild O(n) mỗi substep (incremental hash là phase 2 tương lai)

---

## 2. Package Dependencies

Thêm vào `Packages/manifest.json`:

```json
"com.unity.burst": "1.8.21",
"com.unity.collections": "2.5.4",
"com.unity.mathematics": "1.3.2"
```

- **Burst** — compiler, bỏ bounds check, auto-SIMD
- **Collections** — `NativeArray<T>`, `NativeReference<T>` (Burst dependency)
- **Mathematics** — `float2`, `math.rsqrt()`, `math.sqrt()` (Burst-native math)

---

## 3. Data Layout

### 3.1 SandParticle — Vector2 → float2

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct SandParticle  // vẫn 32B, layout byte-by-byte identical
{
    public float2 pos, prevPos, frameStartPos;  // Vector2 → float2
    public float radius;
    public uint packedColor;
}
```

Size giữ nguyên 32B. `float2` = 2 × float = 8B, same as `Vector2`.

### 3.2 Containers trong FallingSandSim

| Hiện tại | Sau |
|---|---|
| `SandParticle[]` | `NativeArray<SandParticle>(maxParticles, Persistent)` |
| `bool[] isSleeping` | `NativeArray<bool>(maxParticles, Persistent)` |
| `byte[] sleepCounters` | `NativeArray<byte>(maxParticles, Persistent)` |
| `int[] activeIndices` | `NativeArray<int>(maxParticles, Persistent)` |
| — | `NativeReference<int> awakeCountRef` (Persistent) |
| — | `NativeReference<bool> wakeOccurredRef` (Persistent) |

### 3.3 SpatialHash2D internal arrays

Tất cả `int[]` → `NativeArray<int>(Persistent)`. `Dispose()` thật sự dispose thay vì no-op.

---

## 4. Collision Outer Loop O(n) → O(awake)

### 4.1 Vấn đề

Hiện tại outer loop `for i=0..count`, skip sleeping bằng `continue`. 90% sleeping = 9K wasted iterations chỉ để check 1 bool.

### 4.2 Giải pháp

Iterate `activeIndices[]` thay vì `0..count`:

```csharp
for (int a = 0; a < awakeCount; a++)
{
    int i = activeIndices[a];
    // ... 3×3 neighbor, pair processing
}
```

### 4.3 Pair rule điều chỉnh

```csharp
int j = sorted[off + k];
if (j == i) continue;                       // self
if (!isSleeping[j] && j < i) continue;      // active pair đã xử lý
```

Equivalence proof:

| j vs i | j state | Cũ: `j <= i && !isSleeping[j]` | Mới: `j==i` then `!isSleeping[j] && j<i` |
|---|---|---|---|
| j < i | active | skip ✓ | skip ✓ |
| j = i | — | skip ✓ | skip ✓ |
| j < i | sleeping | **process** ✓ | **process** ✓ |
| j > i | any | **process** ✓ | **process** ✓ |

### 4.4 Wake handling

Khi j bị wake mid-loop: j nằm trong spatial hash, active particles tiếp theo collide với nó. `wakeOccurredRef` flag trigger rebuild `activeIndices` cho `ResolveBoundaries` + substep tiếp.

---

## 5. Job Structs

### 5.1 Danh sách 6 Jobs

| Job | Input | Output | Scope |
|---|---|---|---|
| `BuildActiveIndicesJob` | isSleeping, count | activeIndices, awakeCountRef | O(n) scan |
| `SnapshotFrameStartJob` | particles, activeIndices, awakeCount | particles (frameStartPos) | O(awake) |
| `IntegrateJob` | particles, activeIndices, awakeCount, gravity, drag | particles (pos, prevPos) | O(awake) |
| `SpatialHashBuildJob` | particles, count, hash arrays | cellCounts, cellOffsets, sortedIndices | O(n) |
| `ResolveCollisionsJob` | particles, activeIndices, awakeCount, hash, sleep arrays, params | particles (pos, prevPos), sleep arrays, wakeOccurredRef | O(awake) outer |
| `ResolveBoundariesJob` | particles, activeIndices, awakeCount, bounds | particles (pos, prevPos) | O(awake) |

Tất cả `[BurstCompile]`, implement `IJob`.

### 5.2 UpdateSleep giữ managed

Chạy 1 lần/frame sau tất cả substeps, O(awake). Không phải hot path. Giữ managed vì:
- Cần đọc kết quả ngay để set `needsRenderUpload`
- Overhead schedule 1 job > benefit Burst cho ~1K iterations 1 lần/frame

### 5.3 FastInvSqrt → math.rsqrt

Trong Burst context, `math.rsqrt()` map sang SSE `rsqrtss` — nhanh hơn Quake trick. `math.sqrt()` thay `FastSqrt()`. Xóa 2 method `FastInvSqrt`/`FastSqrt`.

---

## 6. FallingSandSim Lifecycle

### 6.1 FixedUpdate schedule pattern

```
BuildActiveIndicesJob.Schedule().Complete()
awakeCount = awakeCountRef.Value
if awakeCount == 0: return

SnapshotFrameStartJob.Schedule().Complete()

for substep:
    if s > 0 && over budget: break
    IntegrateJob.Schedule().Complete()
    SpatialHashBuildJob.Schedule().Complete()
    ResolveCollisionsJob.Schedule().Complete()
    if wakeOccurredRef.Value:
        BuildActiveIndicesJob.Schedule().Complete()
        awakeCount = awakeCountRef.Value
    ResolveBoundariesJob.Schedule().Complete()

UpdateSleepManaged()  // inline, managed
```

Tuần tự Schedule+Complete — không chain dependency. Đơn giản, dễ debug.

### 6.2 OnDestroy — dispose tất cả

```
particles.Dispose()
isSleeping.Dispose()
sleepCounters.Dispose()
activeIndices.Dispose()
awakeCountRef.Dispose()
wakeOccurredRef.Dispose()
spatialHash.Dispose()  // dispose internal NativeArrays
```

### 6.3 SpawnParticle

```csharp
var particle = new SandParticle { pos = position, ... };
particles[activeCount] = particle;      // NativeArray whole-struct assign OK
isSleeping[activeCount] = false;
sleepCounters[activeCount] = 0;
```

---

## 7. Unchanged

- **Rendering pipeline** — ParticleDrawer, ParticleRenderPass, shader: untouched
- **ParticleProvider** static bridge: untouched
- **SandColors** — managed, gọi 1 lần khi spawn, không reference Vector2/SandParticle → không cần đổi
- **Namespace, file structure** — giữ nguyên
- **Sleep system logic** — identical
- **Time budgeting** — identical
- **Render upload skip** — identical
- **Spatial hash rebuild** — vẫn O(n) mỗi substep (incremental = phase 2 tương lai)

---

## 8. Files thay đổi

| File | Thay đổi |
|---|---|
| `SandParticle.cs` | `Vector2` → `float2`, thêm `using Unity.Mathematics` |
| `SpatialHash2D.cs` | `int[]` → `NativeArray<int>`, `Dispose()` thật sự dispose |
| `SandPhysics.cs` | Tách thành 6 `[BurstCompile] IJob` structs, collision O(awake), `math.rsqrt` |
| `SandColors.cs` | Không đổi — không reference Vector2 hay SandParticle |
| `FallingSandSim.cs` | NativeArray containers, Schedule/Complete, Dispose, managed UpdateSleep |
| `manifest.json` | Thêm `burst`, `collections`, `mathematics` |

---

## 9. Estimated Impact

| Phase | Hiện tại | Sau | Speedup |
|---|---|---|---|
| Collision outer loop | O(10K), Mono | O(1K), Burst | ~15-30× |
| Integrate/Snapshot/Boundaries | O(1K), Mono | O(1K), Burst | ~2-4× |
| SpatialHash Build | O(10K), Mono | O(10K), Burst | ~2-3× |
| UpdateSleep | O(1K), Mono | O(1K), Mono | 1× |
| FastInvSqrt | Quake trick | `math.rsqrt` SSE | ~1.5-2× |

Collision = ~70-80% physics time → tổng FixedUpdate giảm ước **~5-10×**.

---

## 10. Testing

| # | Test | Pass criteria |
|---|---|---|
| 1 | Burst 10K | Rơi, tạo đụn cát — behavior identical với trước |
| 2 | Angle of repose | ~20-25° — unchanged |
| 3 | Stream 500/s | Dòng liên tục, cone tự nhiên |
| 4 | Profiler settled | FixedUpdate < 0.1ms, LateUpdate ≈ 0 |
| 5 | Profiler active (1K awake / 10K total) | Physics < 1ms (giảm từ ~3-5ms) |
| 6 | GC Alloc | 0 trong simulation path |
| 7 | Burst Inspector | Tất cả 6 Jobs compiled thành công |
| 8 | NativeArray leak check | Không leak warning khi exit Play mode |
| 9 | Wake cascade | Particle mới đánh thức pile — identical behavior |
| 10 | Enter/Exit Play ×3 | Không error, không leak |
