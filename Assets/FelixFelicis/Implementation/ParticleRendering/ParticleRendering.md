# Particle Rendering

> Vẽ hàng nghìn particle circles trên GPU trong 1 draw call.
> GPU Instancing + SDF + zero-copy upload, tích hợp URP (Unity 6).

Namespace: `FelixFelicis.ParticleRendering` · Assembly: `com.FelixFelicis`

---

## 1. Nền tảng lý thuyết

### 1.1 GPU Instancing

Bottleneck khi render nhiều particles không nằm ở số polygon — mà ở **số draw calls**. Mỗi draw call CPU phải chuẩn bị shader, texture, uniforms rồi mới gửi lệnh cho GPU.

GPU Instancing giải quyết bằng cách vẽ cùng 1 mesh **N lần** trong **1 draw call**. Mỗi instance dùng `SV_InstanceID` đọc data riêng từ `StructuredBuffer`:

```
1 quad mesh × 10,000 instances = 10,000 circles trong 1 draw call
```

| API | Giới hạn | Data | Lý do chọn/bỏ |
|-----|----------|------|----------------|
| `DrawMeshInstanced` | 1,023 instances | Transform matrix only | ✗ Không đủ, data cứng nhắc |
| `DrawMeshInstancedIndirect` | Không giới hạn | Tùy ý qua StructuredBuffer | ✓ **Đang dùng** |

`argsBuffer` (5 × `uint`) cho GPU biết vẽ bao nhiêu:

| [0] indexCount | [1] instanceCount | [2] startIndex | [3] baseVertex | [4] startInstance |
|:-:|:-:|:-:|:-:|:-:|
| 6 | N | 0 | 0 | 0 |

---

### 1.2 CPU → GPU Data Transfer

Simulation (CPU) tạo data, GPU cần đọc để vẽ. 3 cách, từ chậm đến nhanh:

```
Cách 1: SetData(List<T>)           Cách 2: SetData(NativeArray<T>)     Cách 3: LockBufferForWrite() ✓
┌──────────────┐                   ┌──────────────┐                    ┌──────────────┐
│ List<T>      │ managed heap      │ NativeArray  │ unmanaged          │ NativeArray  │ GPU staging
│ (GC tracked) │                   │ (no GC)      │                    │ (mapped)     │
└──────┬───────┘                   └──────┬───────┘                    └──────┬───────┘
       │ pin + memcpy                     │ memcpy                            │ ← sim ghi thẳng
       ▼                                  ▼                                   │
┌──────────────┐                   ┌──────────────┐                           │
│ Staging buf  │                   │ Staging buf  │                           │
└──────┬───────┘                   └──────┬───────┘                           │
       │ DMA                              │ DMA                               │ (không copy)
       ▼                                  ▼                                   ▼
┌──────────────┐                   ┌──────────────┐                    ┌──────────────┐
│ GPU VRAM     │                   │ GPU VRAM     │                    │ GPU đọc      │
└──────────────┘                   └──────────────┘                    └──────────────┘
    2 copies                           1 copy                              0 copies
```

Hệ thống dùng **Cách 3** — `GraphicsBuffer` với `UsageFlags.LockBufferForWrite`. Buffer nằm trong GPU-visible CPU memory (D3D12 Upload Heap). CPU ghi nhanh, GPU đọc trực tiếp, không copy trung gian.

---

### 1.3 Double Buffering

GPU pipeline chạy **async** — frame N+1 CPU có thể ghi buffer mà GPU vẫn đang đọc từ frame N:

```
Single buffer:                     Double buffer:
Frame N:   CPU ──write──► A        Frame N:   CPU ──write──► A    GPU reads B
Frame N+1: CPU ──write──► A ⚠️     Frame N+1: CPU ──write──► B    GPU reads A
           GPU vẫn đọc A!          Frame N+2: CPU ──write──► A    GPU reads B
           → D3D11 slow path                   Không conflict ✓
```

Triển khai: 2 `GraphicsBuffer`, `writeIndex` swap mỗi frame bằng `(writeIndex + 1) % 2`. Chi phí thêm 1 buffer (10K × 16B = 160 KB).

---

### 1.4 SDF Circle

GPU chỉ biết vẽ tam giác. Vẽ hình tròn bằng cách gửi 1 **quad** (4 vertices), rồi fragment shader dùng SDF quyết định pixel nào thuộc circle:

```
SDF(p) = length(p) - radius

  < 0 → bên trong      = 0 → biên      > 0 → bên ngoài
```

```
     ┌─────────────────────┐
     │ quad                 │
     │    ╭─────────────╮   │
     │   ╱   SDF < 0     ╲  │   SDF > 0
     │  │   (opaque)      │ │  (transparent)
     │   ╲               ╱  │
     │    ╰─────────────╯   │
     │        SDF = 0       │
     └─────────────────────┘
```

| | Mesh polygon (64 cạnh) | SDF |
|---|---|---|
| Zoom gần | Thấy cạnh | Luôn tròn hoàn hảo |
| Vertices | 192 | 4 |
| AA | MSAA (4–8× cost) | `fwidth()` (1 instruction) |

---

### 1.5 Anti-aliasing

`fwidth(sdf)` = hardware derivative, trả về "1 pixel rộng bao nhiêu đơn vị SDF". Đúng cho mọi camera type + zoom level.

```hlsl
float fw    = fwidth(sdf);
float alpha = 1.0 - smoothstep(-fw * 0.5, fw * 0.5, sdf);
//                             ◄── 1 pixel ──►
//            inside (1.0)  ──  gradient  ──  outside (0.0)
```

Quad mở rộng thêm 10% radius (`aaPadding = radius * 0.1`) để fragment shader có chỗ vẽ gradient biên.

---

### 1.6 Color Packing

`Color` = 4 float = **16 bytes**. Particle colors không cần float precision → pack `Color32` (4 × uint8) thành 1 `uint` = **4 bytes**:

```
C#:     (uint)(R | G<<8 | B<<16 | A<<24)
Shader: float4((packed & 0xFF) / 255.0, ...)
```

Struct giảm 28 → **16 bytes** (power-of-2, GPU cache-aligned, −43% bandwidth).

---

### 1.7 Render State

| State | Giá trị | Tại sao |
|-------|---------|---------|
| ZWrite | Off | Circles không che nhau theo depth |
| ZTest | Always | Overlay trên mọi 3D objects |
| Cull | Off | Quad 2D, cả 2 mặt |
| Blend | SrcAlpha OneMinusSrcAlpha | `finalColor = circle × α + background × (1−α)` |

---

### 1.8 URP Integration

```
ScriptableRendererFeature (asset, sống qua domain reload)
  ├─ Create()          → tạo drawer + pass, đăng ký provider
  ├─ AddRenderPasses() → enqueue pass mỗi frame
  └─ Dispose()         → giải phóng GPU resources

ScriptableRenderPass (logic vẽ)
  ├─ RecordRenderGraph()  → Unity 6+ RenderGraph (AddUnsafePass)
  └─ Execute()            → Legacy Compatibility Mode
```

`AddUnsafePass` (không phải `AddRasterPass`) vì `DrawMeshInstancedIndirect` là raw command buffer operation.

`AllowPassCulling(false)` — RenderGraph không tự detect dependency của procedural draw.

`AccessFlags.ReadWrite` trên `activeColorTexture` — alpha blending đọc pixel hiện tại để trộn.

**SO ↔ MB bridge:** Feature (ScriptableObject) không reference được MonoBehaviour → static `ParticleProvider` làm cầu nối, expose qua interface.

---

## 2. Quyết định tối ưu — Tại sao kỹ thuật hiện tại

### 2.1 Loại bỏ data copies

Hệ thống ban đầu có **4 copies** mỗi particle mỗi frame:

```
Ban đầu (4 copies):
SoA arrays ──COPY 1──► Provider List<T> ──COPY 2──► Drawer List<T> ──COPY 3──► SetData ──► GPU
  positions[]              list.Add(struct)       destructure→       pin + memcpy
  radii[]                                         AddParticle()→
  colors[]                                        reassemble→Add()

Hiện tại (0 copies):
SoA arrays ──ghi thẳng──► NativeArray (GPU staging memory) ──► GPU đọc
                          LockBufferForWrite()                  không copy
```

| Bước tối ưu | Copy loại bỏ | Cách |
|-------------|-------------|------|
| Bỏ `AddParticle()` per-element loop | Copy #2 | Drawer không giữ List riêng, nhận data trực tiếp |
| Bỏ `SetData(List<T>)` | Copy #3 | `LockBufferForWrite()` → sim ghi thẳng GPU staging |
| Bỏ Provider `List<T>` | Copy #1 | Provider chỉ giữ drawer reference, không giữ data |

### 2.2 Grow-only buffer

Khi particle count giảm (10K → 5K), buffer **không shrink** — giữ nguyên capacity 10K. `args[1] = count` giới hạn GPU chỉ đọc 5K đầu tiên.

Tại sao: tạo `GraphicsBuffer` mới tốn thời gian (GPU resource allocation). Nếu count dao động 5K–10K mỗi frame, shrink sẽ tạo/hủy buffer liên tục → stutter.

### 2.3 Uniform caching

Mỗi `material.Set*()` có CPU overhead: hash lookup → validation → đánh dấu material dirty. Với 5 lệnh Set × 60 FPS × nhiều camera = hàng trăm lần gọi thừa mỗi giây.

| Uniform | Tần suất thay đổi | Chiến lược |
|---------|-------------------|------------|
| `UseScreenSpace = 0` | Không bao giờ | Set 1 lần trong `EnsureMaterial()` |
| `InstanceOffset = 0` | Không bao giờ | Set 1 lần trong `EnsureMaterial()` |
| `ScreenSize` | Khi resize window | Dirty flag (`cachedScreenWidth/Height`) |
| `InstanceData` (buffer) | Mỗi frame (double buffer swap) | Luôn rebind sau swap |
| `WorldToClipSpace` (VP matrix) | Mỗi camera | Luôn set — camera khác nhau |

### 2.4 GraphicsBuffer thay vì ComputeBuffer

| | `ComputeBuffer` | `GraphicsBuffer` |
|---|---|---|
| Zero-copy API | `BeginWrite` — cần `SubUpdates` mode, lịch sử chỉ ổn định trên Vulkan | `LockBufferForWrite` — native support, ổn định cross-platform |
| Memory placement | Default: GPU VRAM (cần staging copy) | Với `LockBufferForWrite`: GPU-visible CPU memory (D3D12 Upload Heap) — CPU ghi nhanh |
| Unity 6 status | Legacy, vẫn hoạt động | Recommended API |

`ComputeBuffer.BeginWrite` yêu cầu `ComputeBufferMode.SubUpdates` — mode này từng chỉ hoạt động đáng tin cậy trên Vulkan. `GraphicsBuffer.LockBufferForWrite` là API native của Unity 6, không cần mode đặc biệt.

### 2.5 Color packing tại Start() thay vì Update()

`PackColor()` chuyển `Color` → `uint` bằng bit-shift. Chi phí nhỏ nhưng nhân với N particles × 60 FPS = đáng kể.

Particle colors trong debug sim không đổi mỗi frame → pack 1 lần trong `Start()` vào `uint[] packedColors`, `Update()` chỉ copy `uint` đã sẵn. Di chuyển tính toán từ **hot path** (mỗi frame) sang **cold path** (1 lần).

Simulation thật nếu color thay đổi mỗi frame → pack trong Update vẫn rẻ hơn truyền `Color` 16 bytes.

### 2.6 Static bridge thay vì Singleton hoặc SO bridge

`ScriptableRendererFeature` (ScriptableObject) không thể reference `MonoBehaviour` trực tiếp. 3 phương án:

| Phương án | Cách hoạt động | Trade-off |
|-----------|---------------|-----------|
| **Static field** ✓ | Provider giữ static reference, cả 2 bên đọc/ghi | Đơn giản nhất. Không cần tạo asset. Đủ cho single-drawer |
| Singleton | `ParticleDrawer.Instance` | Cần MonoBehaviour → phải sống trên GameObject → phức tạp hơn cho class không cần MonoBehaviour |
| ScriptableObject bridge | Cả 2 reference cùng 1 SO asset | Cần tạo asset trong Editor, đúng cho cấu hình từ Inspector |

Static bridge phù hợp vì drawer không cần Inspector config, không cần MonoBehaviour lifecycle, và chỉ có 1 instance.

### 2.7 `fwidth()` thay vì `invTexelSize` (approach SebVis)

| | SebVis: `saturate(0.5 - sdf * invTexelSize)` | Hiện tại: `fwidth()` + `smoothstep()` |
|---|---|---|
| Tính toán | Vertex shader tính `invTexelSize` từ `unity_OrthoParams` | Fragment shader gọi hardware `fwidth()` |
| Camera type | Chỉ orthographic | **Mọi camera** (ortho + perspective) |
| Zoom handling | Manual scale | Tự động |
| Gradient shape | Tuyến tính (`saturate`) | Chữ S (`smoothstep`) — mượt hơn, đạo hàm = 0 tại hai đầu |

`fwidth()` đắt hơn `invTexelSize` (~1 instruction vs precomputed), nhưng đúng cho mọi trường hợp mà không cần biết camera type.

### 2.8 PassData là class, không phải struct

RenderGraph API (`AddUnsafePass<T>`) yêu cầu `T` là **class** — RenderGraph pool instances nội bộ, gán giá trị qua `out` parameter. Struct sẽ bị copy, mất reference.

PassData chứa `IInstanceDrawer drawer` + `Camera camera` — reference types. RenderGraph pool PassData sau frame đầu tiên → **zero heap allocation** từ frame thứ 2 trở đi.

Static lambda `static (PassData data, UnsafeGraphContext context) => { ... }` tránh closure capture → zero GC pressure.

### 2.9 Lazy material init

`Shader.Find()` có thể trả `null` nếu gọi trước khi Unity import shader xong. Xảy ra khi:
- Domain reload (Enter Play Mode)
- URP gọi `Feature.Create()` trước shader compilation

`EnsureMaterial()` retry mỗi frame. Khi shader sẵn sàng → tạo material + set constants 1 lần. Các frame tiếp chỉ check `material != null` → return true (1 branch, near-zero cost).

---

## 3. Thiết kế hệ thống

### 3.1 Dependency Graph

```
ParticleRendererFeature (composition root)
  ├── creates ──► ParticleDrawer ◄── implements ── IInstanceWriter<T>
  │                    ▲                            IInstanceDrawer
  │                    │                            IDisposable
  ├── creates ──► ParticleRenderPass
  │                    │
  │                    └── depends on ──► IInstanceDrawer (interface only)
  │
  └── registers ──► ParticleProvider (static bridge)
                       │
                       ├── .Writer ──► IInstanceWriter<T> ◄── ParticleDebugSim reads
                       └── .Drawer ──► IInstanceDrawer    ◄── ParticleRenderPass reads
```

Simulation không biết `ParticleDrawer` tồn tại — chỉ thấy `IInstanceWriter<T>`.
Render pass không biết `ParticleDrawer` tồn tại — chỉ thấy `IInstanceDrawer`.

### 3.2 Frame Lifecycle

```
Update()                                    Render (per camera)
────────────────────────────────────        ────────────────────────
                                            
BeginFrame(count)                           Draw(cmd, cam)
  │  frameDataReady = false                   │  skip if !frameDataReady
  │  recover orphaned lock                    │  set ScreenSize (if changed)
  │  swap writeIndex                          │  set VP matrix (per-camera)
  │  ensure buffer capacity                   └─ DrawMeshInstancedIndirect
  └─ LockBufferForWrite → NativeArray       
                                            
nativeArray[i] = { center, radius, color }  ← zero-copy, GPU staging memory
                                            
EndFrame(count)                             
  │  UnlockBufferAfterWrite                 
  │  update argsBuffer (if count changed)   
  └─ rebind buffer to material              
```

### 3.3 Struct Layout (C# ↔ Shader)

```
C# [StructLayout(Sequential)]          HLSL StructuredBuffer
┌───────────────────────────┐          ┌───────────────────────────┐
│ Vector2 center     (8B)   │ ═══════ │ float2 center      (8B)   │
│ float   radius     (4B)   │ ═══════ │ float  radius      (4B)   │
│ uint    packedColor (4B)  │ ═══════ │ uint   packedColor  (4B)  │
├───────────────────────────┤          ├───────────────────────────┤
│ Total: 16 bytes            │          │ Total: 16 bytes            │
└───────────────────────────┘          └───────────────────────────┘
```

Thứ tự trường **phải khớp byte-by-byte**. `StructLayout.Sequential` ngăn C# compiler sắp xếp lại. Stride sai → GPU đọc lệch → visual corruption.

### 3.4 Safety Guards

| Guard | Chống | Cơ chế |
|-------|-------|--------|
| `isBufferLocked` | Exception giữa Begin/EndFrame → buffer locked vĩnh viễn | BeginFrame tự unlock buffer cũ |
| `lockedBuffer.IsValid()` | Buffer invalidate (hot reload, device lost) | Check trước unlock |
| `!isBufferLocked` early return | EndFrame gọi mà không có BeginFrame | Skip unlock |
| `frameDataReady = false` | Simulation destroy → Draw vẽ stale data | Reset mỗi BeginFrame |
| `lastUploadFrame` | BeginFrame gọi 2 lần cùng frame | Guard Time.frameCount |
| Dispose unlock | Buffer locked khi feature bị destroy | Unlock trước Release |
| `renderPass != null` | Feature error → null pass | Guard trong AddRenderPasses |
| ReleaseResources in Create() | URP gọi Create() nhiều lần → leak | Release trước tạo mới |

---

## 4. File Reference

```
ParticleRendering/
├─ ParticleRendering.md                ← tài liệu này
├─ Resources/
│   ├─ ParticleDraw.shader             ← SDF circle + color unpack + fwidth AA
│   └─ SpaceTransformHelper.hlsl       ← world/screen-space coordinate transform
└─ Scripts/
    ├─ IInstanceWriter.cs              ← interface: BeginFrame / EndFrame
    ├─ IInstanceDrawer.cs              ← interface: Draw
    ├─ ParticleRenderData.cs           ← 16B struct (center + radius + packedColor)
    ├─ QuadMeshHelper.cs               ← quad mesh factory (4 vertices, 2 triangles)
    ├─ ParticleDrawer.cs               ← core: double-buffered GPU + material + draw
    ├─ ParticleProvider.cs             ← static bridge (Writer/Drawer via interface)
    ├─ ParticleRendererFeature.cs      ← URP feature (composition root)
    ├─ ParticleRenderPass.cs           ← URP pass (RenderGraph + Legacy)
    └─ ParticleDebugSim.cs             ← debug simulation (thay bằng sim thật)
```

| File | Vai trò |
|------|---------|
| `ParticleDrawer` | Quản lý 2 GraphicsBuffer luân phiên, lazy material, grow-only args buffer, phát draw call. Implement cả IInstanceWriter lẫn IInstanceDrawer |
| `ParticleProvider` | Static bridge — giữ concrete drawer nội bộ, expose Writer/Drawer qua interface. Writer và drawer phải share cùng buffer lifecycle nên phải là 1 object |
| `ParticleRendererFeature` | Composition root — tạo drawer + pass, đăng ký provider, release khi dispose. Gọi ReleaseResources() trong Create() vì URP gọi Create() nhiều lần |
| `ParticleRenderPass` | Hỗ trợ RenderGraph (AddUnsafePass, PassData pooled, static lambda) + Legacy (CommandBufferPool). Chỉ gọi `drawer.Draw()` — không upload data |
| `ParticleDebugSim` | Pre-allocate arrays + pre-pack colors trong Start(). Update ghi thẳng NativeArray từ LockBufferForWrite |

---

## 5. Kiểm thử

| # | Kiểm tra | Pass | Debug nếu fail |
|---|----------|------|-----------------|
| 1 | Play với ParticleDebugSim | Circles hiện | Camera, ZTest, shader error |
| 2 | Frame Debugger | 1 draw call "ParticleRendering" | Buffer/args sai |
| 3 | 10,000+ particles | > 60 FPS | Profiler → LockBufferForWrite |
| 4 | Zoom gần 1 circle | Biên mượt | AA padding (radius × 0.1) |
| 5 | Game + Scene view cùng lúc | Cả 2 đúng | VP matrix per-camera |
| 6 | Enter/Exit Play 3 lần | Không error, memory ổn | ReleaseResources leak |
| 7 | Profiler → GC Alloc | 0 trong render path | Managed allocation |
| 8 | Console | Không warning LockBufferForWrite | Double buffering |

---

## 6. Mở rộng

| Ưu tiên | Tính năng | Thay đổi |
|---------|-----------|----------|
| A | Color gradient theo velocity | Thêm velocity vào struct, shader lerp màu |
| B | Depth occlusion | `ZTest LEqual` — particles bị geometry che |
| C | Compute shader simulation | GPU-only pipeline, bỏ LockBufferForWrite |
| D | Rectangular masking | maskMin/maskMax trong struct + shader clip |
| E | Multi-shape | Type field + SDF cho quad/line/triangle |

---

## 7. Tóm tắt hiệu năng

| Metric | Giá trị |
|--------|---------|
| Copies per particle per frame | **0** |
| Bytes per particle | **16** (power-of-2) |
| Draw calls | **1** per camera |
| Bandwidth (10K particles) | **160 KB/frame** |
| GC alloc in render path | **0** |
| Buffers | Double-buffered, grow-only |
| AA | `fwidth()` + `smoothstep()` |
