# Particle Rendering

> Vẽ hàng nghìn particle circles trên GPU trong 1 draw call.
> GPU Instancing + SDF + NativeArray upload, tích hợp URP RenderGraph (Unity 6).

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
Cách 1: SetData(List<T>)           Cách 2: SetData(NativeArray<T>) ✓    Cách 3: LockBufferForWrite()
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

Hệ thống dùng **Cách 2** — `NativeArray<T>` (persistent, grow-only) làm CPU staging buffer, upload mỗi frame qua `GraphicsBuffer.SetData(NativeArray, 0, 0, count)`. Tổng **1 memcpy** per frame.

> **Tại sao không dùng Cách 3 (zero-copy)?** `LockBufferForWrite()` map buffer vào GPU-visible CPU memory. Trên **desktop D3D11/D3D12** hoạt động tốt. Trên **mobile Vulkan** (đặc biệt Mali Valhall — G610, G710, G720), driver không flush CPU cache sau khi CPU ghi → GPU đọc **stale data** → particles invisible. Unity không expose memory barrier API để workaround. `SetData(NativeArray)` đi qua Unity's managed upload path — driver tự xử lý cache coherence. Trade-off 1 memcpy (10K × 16B = 160 KB) ≈ 0.05ms — negligible.

---

### 1.3 Double Buffering

GPU pipeline chạy **async** — frame N+1 CPU có thể ghi buffer mà GPU vẫn đang đọc từ frame N:

```
Single buffer:                     Double buffer:
Frame N:   CPU ──write──► A        Frame N:   CPU ──write──► A    GPU reads B
Frame N+1: CPU ──write──► A ⚠️     Frame N+1: CPU ──write──► B    GPU reads A
           GPU vẫn đọc A!          Frame N+2: CPU ──write──► A    GPU reads B
           → GPU slow path                    Không conflict ✓
```

Triển khai: 2 `GraphicsBuffer`, `writeIndex` swap mỗi frame bằng `(writeIndex + 1) % 2`. Sim ghi vào CPU staging `NativeArray`, rồi `SetData` upload vào buffer ở `writeIndex`. Chi phí thêm 1 buffer (10K × 16B = 160 KB).

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
ScriptableRendererFeature (composition root, sống qua domain reload)
  ├─ Create()          → tạo drawer + pass, đăng ký provider
  ├─ AddRenderPasses() → enqueue pass mỗi frame
  └─ Dispose()         → giải phóng GPU resources

ScriptableRenderPass (bridge URP ↔ IInstanceDrawer)
  ├─ RecordRenderGraph()  → Unity 6+ RenderGraph (AddUnsafePass)
  └─ Execute()            → Legacy Compatibility Mode (deprecated từ 6.3)
```

**RenderGraph path (primary):**

`AddUnsafePass` (không phải `AddRasterPass`) vì `DrawMeshInstancedIndirect` là raw command buffer operation — `AddRasterPass` chỉ hỗ trợ standard rasterization API.

`AllowPassCulling(false)` — RenderGraph không tự detect dependency của procedural draw.

`UseTexture(activeColorTexture, ReadWrite)` — alpha blending đọc pixel hiện tại để trộn.
`UseTexture(activeDepthTexture, Read)` — depth buffer cần được bound dù `ZTest Always`.

**Explicit `SetRenderTarget`** — `AddUnsafePass` khai báo texture dependencies nhưng **KHÔNG tự bind render targets** (khác `AddRasterPass` tự bind). Nếu không gọi `context.cmd.SetRenderTarget(color, depth)` trong render func, draw commands render vào "nothing" trên Vulkan/mobile. D3D11 Editor auto-bind từ previous pass nên lỗi chỉ lộ trên build.

**`cmd.SetGlobal*()`** — Tất cả uniforms (buffer, matrix, vector, int) được set qua CommandBuffer thay vì `material.Set*()`. Chi tiết tại [2.3](#23-commandbuffer-uniforms-thay-vì-materialset).

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

Hiện tại (1 copy):
SoA arrays ──ghi thẳng──► NativeArray (CPU staging) ──SetData──► GPU
                          persistent, grow-only          memcpy
```

| Bước tối ưu | Copy loại bỏ | Cách |
|-------------|-------------|------|
| Bỏ `AddParticle()` per-element loop | Copy #2 | Drawer không giữ List riêng, nhận data trực tiếp |
| Bỏ `SetData(List<T>)` | Copy #3 (pin) | `NativeArray` unmanaged — không cần GC pin |
| Bỏ Provider `List<T>` | Copy #1 | Provider chỉ giữ drawer reference, không giữ data |

Copy còn lại: 1 memcpy trong `SetData(NativeArray, 0, 0, count)` — đáng tin cậy trên mọi GPU driver bao gồm mobile Vulkan (xem [1.2](#12-cpu--gpu-data-transfer)).

### 2.2 Grow-only buffer

Khi particle count giảm (10K → 5K), buffer **không shrink** — giữ nguyên capacity 10K. `args[1] = count` giới hạn GPU chỉ đọc 5K đầu tiên.

Tại sao: tạo `GraphicsBuffer` mới tốn thời gian (GPU resource allocation). Nếu count dao động 5K–10K mỗi frame, shrink sẽ tạo/hủy buffer liên tục → stutter.

Áp dụng cho cả GPU instance buffers lẫn CPU staging `NativeArray`.

### 2.3 CommandBuffer uniforms thay vì `material.Set*()`

`material.Set*()` là **immediate mode** — set property trên Material object trên CPU ngay lập tức. Trong **RenderGraph**, command buffer execution là **deferred** — GPU đọc uniforms tại thời điểm execute, không phải lúc record.

Trên **D3D11** (Editor), driver tự sync material state trước mỗi draw call → hoạt động.

Trên **Vulkan** (Android build), descriptor set caching có thể khiến material property changes **chưa visible** cho GPU tại thời điểm execute. `cmd.SetGlobal*()` ghi trực tiếp vào command stream — GPU đọc đúng giá trị tại đúng thời điểm.

```
material.Set*() (immediate)              cmd.SetGlobal*() (deferred) ✓
┌──────────────────────┐                ┌──────────────────────┐
│ CPU: set property    │                │ Record: ghi vào cmd  │
│ GPU: đọc khi nào?    │ ← race        │ Execute: GPU đọc     │ ← deterministic
│      có thể stale    │                │          đúng lúc    │
└──────────────────────┘                └──────────────────────┘
```

| Uniform | Cách set |
|---------|----------|
| `InstanceData` (buffer) | `cmd.SetGlobalBuffer` — rebind sau double buffer swap |
| `WorldToClipSpace` (VP matrix) | `cmd.SetGlobalMatrix` — per-camera |
| `ScreenSize` | `cmd.SetGlobalVector` — per-camera |
| `useScreenSpace = 0` | `cmd.SetGlobalInt` — constant, vẫn set mỗi draw vì cost negligible |
| `InstanceOffset = 0` | `cmd.SetGlobalInt` — constant |

### 2.4 GraphicsBuffer thay vì ComputeBuffer

| | `ComputeBuffer` | `GraphicsBuffer` |
|---|---|---|
| Unity 6 status | Legacy, vẫn hoạt động | **Recommended API** |
| `SetData(NativeArray)` | Hỗ trợ | Hỗ trợ |
| Indirect args | Cần `ComputeBufferType.IndirectArguments` | `GraphicsBuffer.Target.IndirectArguments` |
| Structured data | `ComputeBufferType.Structured` | `GraphicsBuffer.Target.Structured` |

`GraphicsBuffer` là API hiện tại của Unity 6, hỗ trợ tất cả các use case của `ComputeBuffer` với API nhất quán hơn.

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

PassData chứa `IInstanceDrawer drawer` + `Camera camera` + `TextureHandle colorTarget` + `TextureHandle depthTarget`. RenderGraph pool PassData sau frame đầu tiên → **zero heap allocation** từ frame thứ 2 trở đi.

Static lambda `static (PassData data, UnsafeGraphContext context) => { ... }` tránh closure capture → zero GC pressure.

### 2.9 Lazy material init

`Resources.Load<Shader>()` thay vì `Shader.Find()`. Shader nằm trong `Resources/` folder → Unity include nó trong build thông qua Resources system.

> **Tại sao không dùng `Shader.Find()`?** `Shader.Find()` chỉ tìm shader đã compiled **và included** trong build. Shader phải được reference bởi material asset hoặc nằm trong Always Included Shaders. Hệ thống này tạo material **runtime** (`new Material(shader)`) — không có material asset nào reference shader → `Shader.Find()` trả `null` trên build, `EnsureMaterial()` fail im lặng, particles invisible. `Resources.Load<Shader>("ParticleDraw")` load trực tiếp bằng path → guaranteed bởi Resources inclusion.

`EnsureMaterial()` retry mỗi frame. Khi shader sẵn sàng → tạo material. Các frame tiếp chỉ check `material != null` → return true (1 branch, near-zero cost).

### 2.10 Android APK Build — 6 fixes

Hệ thống ban đầu hoạt động trên Editor (D3D11) nhưng **không vẽ gì** trên Android APK (Vulkan + RenderGraph). Nguyên nhân: 6 vấn đề kết hợp, mỗi cái đều bị D3D11 Editor che giấu.

| # | Vấn đề | Tại sao lỗi trên Vulkan/Android | Fix |
|---|--------|--------------------------------|-----|
| 1 | **`CGPROGRAM` + `UnityCG.cginc`** — Built-in RP shader syntax | URP shader stripper (`m_StripUnusedVariants: 1`) strip shader không thuộc URP. D3D11 Editor có backward compatibility layer. | `HLSLPROGRAM` + `Core.hlsl` + `"RenderPipeline"="UniversalPipeline"` tag |
| 2 | **`Shader.Find()` trả null** — shader không referenced bởi material asset | Resources folder include asset nhưng `Shader.Find` cần shader compiled + registered. Build có thể không register. | `Resources.Load<Shader>()` — load trực tiếp bằng path |
| 3 | **Loose shader globals** — uniforms không nằm trong CBUFFER | D3D11 tự gom loose globals vào `$Globals` CBUFFER. Vulkan SPIR-V compiler không có cơ chế tương đương → binding sai. | `CBUFFER_START(ParticleUniforms)` explicit |
| 4 | **`material.Set*()`** — immediate mode trong RenderGraph context | D3D11 auto-sync material state. Vulkan descriptor set caching → material changes chưa visible khi CommandBuffer execute. | `cmd.SetGlobal*()` — ghi vào command stream |
| 5 | **Không `SetRenderTarget`** — `AddUnsafePass` không auto-bind | D3D11 Editor inherit render target từ previous pass. `AddUnsafePass` trên Vulkan bắt đầu với unbound targets. | Explicit `context.cmd.SetRenderTarget(color, depth)` |
| 6 | **`LockBufferForWrite`** — Mali cache coherence | D3D12 Upload Heap trên desktop flush correctly. Mali Valhall driver không flush CPU cache → GPU đọc stale data. | `SetData(NativeArray)` — driver xử lý cache coherence |

**Bài học chung:** D3D11 trên Editor rất "tha thứ" — tự sync, tự bind, tự gom. Vulkan trên mobile yêu cầu **explicit** ở mọi bước. Test trên device thật là bắt buộc.

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
  │  swap writeIndex                          │  cmd.SetGlobalBuffer (instance data)
  │  ensure GPU buffer capacity               │  cmd.SetGlobalVector (screen size)
  │  ensure CPU staging capacity              │  cmd.SetGlobalMatrix (VP matrix)
  └─ return cpuStaging NativeArray            │  cmd.SetGlobalInt (constants)
                                              └─ DrawMeshInstancedIndirect
nativeArray[i] = { center, radius, color }
                  ← CPU staging, persistent

EndFrame(count)
  │  SetData(nativeArray → GPU buffer)
  │  update argsBuffer (if count changed)
  └─ frameDataReady = true
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
| `!EnsureMaterial()` early return | Shader chưa load (domain reload, Resources missing) | BeginFrame trả `default` NativeArray → sim skip |
| `count == 0` in BeginFrame | Sim gọi BeginFrame với 0 particles | Trả `default` → không tạo buffer thừa |
| `frameDataReady = false` | Simulation destroy → Draw vẽ stale data | Reset mỗi BeginFrame |
| `lastUploadFrame` | BeginFrame gọi 2 lần cùng frame | Guard `Time.frameCount` |
| `cpuStaging` grow-only | Count dao động → liên tục alloc/dealloc | Chỉ grow, không shrink |
| `count == 0` in EndFrame | EndFrame gọi với 0 particles | Skip SetData |
| `!frameDataReady \|\| count == 0` in Draw | Không có data hoặc 0 particles | Skip draw call |
| `renderPass != null` | Feature error → null pass | Guard trong AddRenderPasses |
| ReleaseResources in Create() | URP gọi Create() nhiều lần → leak | Release trước tạo mới |
| `buffer.IsValid()` | Buffer invalidate (hot reload, device lost) | Check trước Release/Recreate |

---

## 4. File Reference

```
ParticleRendering/
├─ ParticleRendering.md                ← tài liệu này
├─ Resources/
│   ├─ ParticleDraw.shader             ← HLSL: SDF circle + color unpack + fwidth AA
│   └─ SpaceTransformHelper.hlsl       ← CBUFFER uniforms + world/screen-space transform
└─ Scripts/
    ├─ IInstanceWriter.cs              ← interface: BeginFrame / EndFrame
    ├─ IInstanceDrawer.cs              ← interface: Draw
    ├─ ParticleRenderData.cs           ← 16B struct (center + radius + packedColor)
    ├─ QuadMeshHelper.cs               ← quad mesh factory (4 vertices, GPU-only)
    ├─ ParticleDrawer.cs               ← core: double-buffered GPU + lazy material + draw
    ├─ ParticleProvider.cs             ← static bridge (Writer/Drawer via interface)
    ├─ ParticleRendererFeature.cs      ← URP feature (composition root)
    ├─ ParticleRenderPass.cs           ← URP pass (RenderGraph + Legacy)
    └─ ParticleDebugSim.cs             ← debug simulation (thay bằng sim thật)
```

| File | Vai trò |
|------|---------|
| `ParticleDrawer` | Quản lý 2 GraphicsBuffer luân phiên, CPU staging NativeArray, lazy material, grow-only args buffer, phát draw call qua `cmd.SetGlobal*()`. Implement cả IInstanceWriter lẫn IInstanceDrawer |
| `ParticleProvider` | Static bridge — giữ concrete drawer nội bộ, expose Writer/Drawer qua interface. Writer và drawer phải share cùng buffer lifecycle nên phải là 1 object |
| `ParticleRendererFeature` | Composition root — tạo drawer + pass, đăng ký provider, release khi dispose. Gọi ReleaseResources() trong Create() vì URP gọi Create() nhiều lần |
| `ParticleRenderPass` | RenderGraph: AddUnsafePass + explicit SetRenderTarget + PassData pooled + static lambda. Legacy: CommandBufferPool. Chỉ gọi `drawer.Draw()` — không upload data |
| `ParticleDebugSim` | Pre-allocate arrays + pre-pack colors trong Start(). Update ghi vào CPU staging NativeArray, EndFrame upload qua SetData |

---

## 5. Kiểm thử

| # | Kiểm tra | Pass | Debug nếu fail |
|---|----------|------|-----------------|
| 1 | Play với ParticleDebugSim | Circles hiện | Camera, ZTest, shader error |
| 2 | Frame Debugger | 1 draw call "ParticleRendering" | Buffer/args sai |
| 3 | 10,000+ particles | > 60 FPS | Profiler → SetData |
| 4 | Zoom gần 1 circle | Biên mượt | AA padding (radius × 0.1) |
| 5 | Game + Scene view cùng lúc | Cả 2 đúng | VP matrix per-camera |
| 6 | Enter/Exit Play 3 lần | Không error, memory ổn | ReleaseResources leak |
| 7 | Profiler → GC Alloc | 0 trong render path | Managed allocation |
| 8 | **Build APK → test trên device** | Circles hiện | adb logcat -s Unity |

---

## 6. Mở rộng

| Ưu tiên | Tính năng | Thay đổi |
|---------|-----------|----------|
| A | Color gradient theo velocity | Thêm velocity vào struct, shader lerp màu |
| B | Depth occlusion | `ZTest LEqual` — particles bị geometry che |
| C | Compute shader simulation | GPU-only pipeline, bỏ CPU staging + SetData |
| D | Rectangular masking | maskMin/maskMax trong struct + shader clip |
| E | Multi-shape | Type field + SDF cho quad/line/triangle |

---

## 7. Tóm tắt hiệu năng

| Metric | Giá trị |
|--------|---------|
| Copies per particle per frame | **1** (NativeArray → SetData → GPU) |
| Bytes per particle | **16** (power-of-2) |
| Draw calls | **1** per camera |
| Bandwidth (10K particles) | **160 KB/frame** |
| GC alloc in render path | **0** |
| Buffers | Double-buffered, grow-only |
| CPU staging | NativeArray (persistent, grow-only) |
| Uniform binding | `cmd.SetGlobal*()` (command stream) |
| AA | `fwidth()` + `smoothstep()` |
| Shader | HLSL + URP Core.hlsl + CBUFFER |
