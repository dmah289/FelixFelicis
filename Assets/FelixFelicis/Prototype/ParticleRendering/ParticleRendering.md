# Particle Rendering

> Vẽ hàng nghìn particle circles trên GPU trong 1 draw call.

Namespace: `FelixFelicis.ParticleRendering` · Assembly: `com.FelixFelicis`

---

## 1. Particle = 16 bytes

Mỗi particle trên GPU là 1 struct:

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

`StructLayout(Sequential)` ngăn C# compiler sắp xếp lại thứ tự trường. Thứ tự **phải khớp byte-by-byte** với HLSL struct — nếu lệch, GPU đọc sai offset → visual corruption.

16 bytes là power-of-2, nên GPU cache line (thường 64 hoặc 128 bytes) chứa đúng 4 hoặc 8 particles — không lãng phí byte nào.

### Color packing

`Color` của Unity = 4 float = **16 bytes**. Màu particle không cần float precision (256 mức mỗi kênh là đủ), nên pack `Color32` (4 × uint8) thành 1 `uint` = **4 bytes**:

```
C# pack:    (uint)(R | G<<8 | B<<16 | A<<24)

Bộ nhớ (little-endian):
byte:     [  R  ][  G  ][  B  ][  A  ]
bit:      0──7   8──15  16──23 24──31

HLSL unpack:
  R = (packed        & 0xFF) / 255.0
  G = ((packed >> 8)  & 0xFF) / 255.0
  B = ((packed >> 16) & 0xFF) / 255.0
  A = ((packed >> 24) & 0xFF) / 255.0
```

`Color32 c = color` — C# implicit conversion tự clamp float [0,1] → byte [0,255].

Struct giảm từ 28 bytes (8 + 4 + 16) xuống **16 bytes** (−43% bandwidth).

---

## 2. CPU → GPU: 1 memcpy mỗi frame

### 2.1 Vấn đề: data copies

Simulation (CPU) tạo data mỗi frame, GPU cần đọc để vẽ. Hệ thống ban đầu có **4 copies**:

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

Mỗi copy trung gian bị loại bằng cách:

| Bước tối ưu | Copy loại bỏ | Cách |
|-------------|-------------|------|
| Bỏ Provider `List<T>` | Copy #1 | Provider chỉ giữ drawer reference, không giữ data |
| Bỏ `AddParticle()` per-element loop | Copy #2 | Drawer không giữ List riêng, nhận data trực tiếp qua NativeArray |
| Bỏ `SetData(List<T>)` | Copy #3 (pin) | `NativeArray` unmanaged — không cần GC pin |

### 2.2 NativeArray — CPU staging buffer

`NativeArray<T>` là mảng nằm trên **unmanaged heap** — bộ nhớ do developer quản lý, nằm ngoài GC. So với `List<T>` (managed heap, GC tracked):

| | `List<T>` / `T[]` | `NativeArray<T>` |
|---|---|---|
| Bộ nhớ | Managed heap (GC scan) | Unmanaged heap (no GC) |
| `SetData()` | GC pin mảng → memcpy → unpin | memcpy trực tiếp |
| Allocation | GC quyết định khi nào thu hồi | Developer gọi `Dispose()` |

Hệ thống dùng `Allocator.Persistent` (sống qua nhiều frame) + `NativeArrayOptions.UninitializedMemory` (không zero-fill, vì sim sẽ ghi đè hết trước upload).

### 2.3 Ba cách upload, từ chậm đến nhanh

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

**Dùng Cách 2.** Cách 3 (zero-copy) nhanh nhất trên desktop, nhưng trên **mobile Vulkan** (đặc biệt Mali Valhall — G610, G710, G720), driver không flush CPU cache sau khi CPU ghi → GPU đọc **stale data** → particles invisible. Unity không expose memory barrier API để workaround. Cách 2 đi qua Unity's managed upload path — driver tự xử lý cache coherence. Chi phí 1 memcpy (10K × 16B = 160 KB ≈ 0.05ms) — negligible.

### 2.4 Double buffering

CPU và GPU chạy **không đồng bộ** — GPU pipeline xử lý lệnh từ frame trước trong khi CPU đã chuyển sang frame tiếp. Nếu chỉ có 1 buffer, CPU ghi đè data mà GPU đang đọc:

```
Single buffer:                     Double buffer:
Frame N:   CPU write → A           Frame N:   CPU write → A    GPU đọc B (data frame trước)
Frame N+1: CPU write → A ⚠️        Frame N+1: CPU write → B    GPU đọc A (data frame N)
           GPU vẫn đang đọc A!    Frame N+2: CPU write → A    GPU đọc B (data frame N+1)
           → race condition                   Không conflict ✓
```

Triển khai: 2 `GraphicsBuffer`, biến `writeIndex` swap mỗi frame đầu `BeginFrame`:

```
writeIndex = (writeIndex + 1) % 2
```

Trong cùng 1 frame: `EndFrame` upload vào `instanceBuffers[writeIndex]`, `Draw` bind cùng `instanceBuffers[writeIndex]`. Không mâu thuẫn — vì `EndFrame` (CPU) hoàn thành trước khi `Draw` record command, và GPU execute draw command sau khi upload xong. Buffer còn lại (`writeIndex` cũ) là buffer frame trước mà GPU pipeline có thể vẫn đang đọc — nhờ swap, CPU không đụng vào nó.

Chi phí: thêm 1 buffer (10K × 16B = 160 KB).

### 2.5 Grow-only buffer

Khi particle count giảm (10K → 5K), buffer **không shrink** — giữ capacity 10K. `args[1] = count` giới hạn GPU chỉ đọc 5K đầu tiên.

Tạo `GraphicsBuffer` mới tốn thời gian (GPU resource allocation). Nếu count dao động 5K↔10K mỗi frame, shrink sẽ tạo/hủy buffer liên tục → stutter. Áp dụng cho cả GPU buffers lẫn CPU staging `NativeArray`.

---

## 3. GPU vẽ circle: Instancing + SDF

### 3.1 GPU Instancing

Bottleneck khi render nhiều particles không nằm ở số polygon — mà ở **số draw calls**. Mỗi draw call, CPU phải chuẩn bị render state (shader, texture, uniforms) rồi gửi lệnh cho GPU. 10.000 particles = 10.000 draw calls → CPU-bound.

GPU Instancing giải quyết: vẽ cùng 1 mesh **N lần** trong **1 draw call**. Mỗi instance (bản sao) dùng `SV_InstanceID` (biến built-in do GPU cung cấp, tự tăng 0, 1, 2, ...) đọc data riêng từ `StructuredBuffer`:

```
1 quad mesh × 10.000 instances = 10.000 circles trong 1 draw call
```

| API | Giới hạn | Data | Lý do |
|-----|----------|------|-------|
| `DrawMeshInstanced` | 1.023 instances | Transform matrix only | ✗ Không đủ, data cứng nhắc |
| `DrawMeshInstancedIndirect` | Không giới hạn | Tùy ý qua StructuredBuffer | ✓ **Đang dùng** |

`argsBuffer` (5 × `uint`) cho GPU biết vẽ bao nhiêu, mapping tới hardware API `DrawIndexedInstancedIndirect`:

| [0] indexCount | [1] instanceCount | [2] startIndex | [3] baseVertex | [4] startInstance |
|:-:|:-:|:-:|:-:|:-:|
| 6 | N | 0 | 0 | 0 |

- `indexCount = 6`: 1 quad = 2 triangles × 3 vertices
- `instanceCount = N`: số particles cần vẽ
- Còn lại = 0: bắt đầu từ đầu buffer

`argsBuffer` chỉ re-upload khi `instanceCount` thay đổi (`lastArgsInstanceCount` tracking).

### 3.2 SDF (Signed Distance Field)

GPU chỉ biết vẽ tam giác. Cách truyền thống vẽ hình tròn: tạo polygon 64 cạnh → 192 vertices. SDF dùng 1 **quad** (4 vertices), fragment shader tính toán pixel nào thuộc circle:

```
SDF(p) = length(p) − radius

  < 0 → bên trong circle    = 0 → đúng biên    > 0 → bên ngoài
```

`length(p)` = khoảng cách Euclidean từ pixel đến tâm circle = `√(x² + y²)`. Trừ đi radius: nếu âm → pixel gần tâm hơn radius → bên trong.

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

| | Polygon (64 cạnh) | SDF |
|---|---|---|
| Zoom gần | Thấy cạnh | Luôn tròn hoàn hảo (tính per-pixel) |
| Vertices/instance | 192 | 4 |
| Anti-aliasing | Cần MSAA (4–8× fragment cost) | `fwidth()` (1 instruction, xem 3.3) |

### 3.3 Anti-aliasing bằng `fwidth` + `smoothstep`

**Vấn đề:** Nếu fragment shader chỉ check `sdf < 0 → opaque, sdf ≥ 0 → transparent`, biên circle bị **aliasing** (răng cưa) — pixel hoặc ON hoặc OFF, không có gradient chuyển tiếp.

**Giải pháp:** Tạo gradient 1-pixel tại biên. Cần biết "1 pixel rộng bao nhiêu đơn vị SDF":

```hlsl
float fw = fwidth(sdf);
```

`fwidth()` là **hardware derivative** — GPU tính screen-space partial derivatives bằng cách so sánh giá trị `sdf` giữa các pixel lân cận trong cùng 1 quad 2×2 (GPU luôn shade fragment theo block 2×2). Cụ thể:

```
fwidth(sdf) = |dFdx(sdf)| + |dFdy(sdf)|

dFdx(sdf) ≈ sdf(pixel bên phải) − sdf(pixel hiện tại)
dFdy(sdf) ≈ sdf(pixel bên dưới)  − sdf(pixel hiện tại)
```

Kết quả: `fw` ≈ "1 pixel cover bao nhiêu đơn vị SDF". Giá trị này **tự động đúng** cho mọi camera type (ortho/perspective) và mọi zoom level — vì nó đo trực tiếp trên screen space.

Tiếp theo, `smoothstep` tạo gradient:

```hlsl
float alpha = 1.0 − smoothstep(−0.5 * fw, 0.5 * fw, sdf);
```

`smoothstep(edge0, edge1, x)` trả về:
- `0` khi `x ≤ edge0`
- `1` khi `x ≥ edge1`
- Đường cong Hermite (chữ S) ở giữa: `t² × (3 − 2t)` với `t = (x − edge0)/(edge1 − edge0)`

Đạo hàm = 0 tại hai đầu → gradient bắt đầu và kết thúc mượt, không có "bước nhảy".

```
alpha:   1.0 ─────╮              ╭───── 0.0
                    ╲    smooth  ╱
                     ╰──────────╯
sdf:        −fw/2        0        +fw/2
             inside   ◄─ 1 pixel ─►  outside
```

**AA padding:** Quad phải **lớn hơn** circle để fragment shader có chỗ vẽ gradient biên. Vertex shader mở rộng quad thêm `aaPadding = radius × 0.1` (world-space) hoặc `2.0` pixel (screen-space).

### 3.4 fwidth() thay vì invTexelSize (approach SebVis)

| | SebVis: `saturate(0.5 - sdf * invTexelSize)` | Hiện tại: `fwidth()` + `smoothstep()` |
|---|---|---|
| Tính toán | Vertex shader tính `invTexelSize` từ `unity_OrthoParams` | Fragment shader gọi hardware `fwidth()` |
| Camera type | Chỉ orthographic | **Mọi camera** (ortho + perspective) |
| Zoom handling | Manual scale | Tự động |
| Gradient shape | Tuyến tính (`saturate`) | Chữ S (`smoothstep`) — mượt hơn, đạo hàm = 0 tại hai đầu |

`fwidth()` đắt hơn `invTexelSize` (~1 instruction vs precomputed), nhưng đúng cho mọi trường hợp mà không cần biết camera type.

---

## 4. Frame lifecycle

```
Update()                                   Render (per camera)
────────────────────────────────────       ────────────────────────

BeginFrame(count)                          Draw(cmd, cam)
  │  frameDataReady = false                  │  skip if !frameDataReady
  │  swap writeIndex                         │  cmd.SetGlobalBuffer (instance data)
  │  ensure GPU buffer capacity              │  cmd.SetGlobalVector (screen size)
  │  ensure CPU staging capacity             │  cmd.SetGlobalInt    (useScreenSpace=0)
  └─ return cpuStaging NativeArray           │  cmd.SetGlobalInt    (InstanceOffset=0)
                                             │  cmd.SetGlobalMatrix (VP matrix)
nativeArray[i] = { center, radius, color }   └─ DrawMeshInstancedIndirect
         ↑ sim ghi thẳng vào staging

EndFrame(count)
  │  SetData(nativeArray → GPU buffer)
  │  update argsBuffer (if count changed)
  └─ frameDataReady = true
```

`Draw` an toàn gọi nhiều lần/frame (multi-camera) — chỉ bind + draw, không upload lại data. Mỗi camera nhận VP matrix và ScreenSize riêng.

---

## 5. Shader

### 5.1 Uniforms — CBUFFER + StructuredBuffer

```hlsl
CBUFFER_START(ParticleUniforms)
    float4x4 WorldToClipSpace;   // VP matrix, per-camera
    float2   ScreenSize;         // viewport size, per-camera
    int      useScreenSpace;     // 0 = world-space, ≠0 = screen-space NDC
    uint     InstanceOffset;     // offset vào StructuredBuffer (luôn 0 hiện tại)
CBUFFER_END

StructuredBuffer<ParticleData> InstanceData;   // particle data, global SRV
```

**Tại sao CBUFFER explicit?** D3D11 tự gom loose globals (biến khai báo ngoài CBUFFER) vào `$Globals` constant buffer. Vulkan SPIR-V compiler không có cơ chế này — biến không nằm trong CBUFFER sẽ bị binding sai hoặc mất.

**Tại sao `cmd.SetGlobal*()` thay vì `material.Set*()`?**

`material.Set*()` là **immediate mode** — set property lên Material object ngay tại thời điểm gọi. Trong RenderGraph, GPU execute commands **deferred** (sau khi recording xong). Trên Vulkan, descriptor set caching có thể khiến property changes chưa visible cho GPU tại thời điểm execute. `cmd.SetGlobal*()` ghi trực tiếp vào **command stream** — GPU đọc đúng giá trị tại đúng thời điểm.

```
material.Set*() (immediate)              cmd.SetGlobal*() (deferred) ✓
┌──────────────────────┐                ┌──────────────────────┐
│ CPU: set property    │                │ Record: ghi vào cmd  │
│ GPU: đọc khi nào?    │ ← race        │ Execute: GPU đọc     │ ← deterministic
│      có thể stale    │                │          đúng lúc    │
└──────────────────────┘                └──────────────────────┘
```

Trên D3D11 Editor, driver tự sync material state trước mỗi draw call nên `material.Set*()` vẫn hoạt động — lỗi chỉ lộ trên Vulkan build.

| Uniform | Type | Cách set |
|---------|------|----------|
| `InstanceData` (buffer) | StructuredBuffer | `cmd.SetGlobalBuffer` — rebind sau double buffer swap |
| `WorldToClipSpace` | float4x4 | `cmd.SetGlobalMatrix` — per-camera |
| `ScreenSize` | float2 | `cmd.SetGlobalVector` — per-camera |
| `useScreenSpace` | int | `cmd.SetGlobalInt` — constant (= 0) |
| `InstanceOffset` | uint | `cmd.SetGlobalInt` — constant (= 0) |

### 5.2 Render state

| State | Giá trị | Tại sao |
|-------|---------|---------|
| ZWrite | Off | Circles không che nhau theo depth |
| ZTest | Always | Overlay trên mọi 3D objects |
| Cull | Off | Quad 2D, vẽ cả 2 mặt |
| Blend | SrcAlpha OneMinusSrcAlpha | `finalPixel = circle × α + background × (1−α)` |

### 5.3 Vertex shader

```hlsl
v2f vert(appdata v, uint instanceID : SV_InstanceID)
```

1. Đọc particle data: `InstanceData[instanceID + InstanceOffset]`
2. Tính quad size: `diameter = radius × 2`, thêm `aaPadding` cho gradient biên
3. Scale vertex: `localVert = v.vertex.xy × (diameter + aaPadding)`
4. Dịch về vị trí particle: `worldPos = float3(localVert + center, 0)`
5. Transform: `clipPos = WorldToClipPos(worldPos)`
6. Pass cho fragment: `localPos` (dùng cho SDF), `radius`, `color` (unpacked)

`WorldToClipPos` hỗ trợ 2 mode:
- **World-space** (`useScreenSpace = 0`): `mul(WorldToClipSpace, float4(worldPos, 1))` — nhân VP matrix
- **Screen-space** (`useScreenSpace ≠ 0`): `worldPos.xy / ScreenSize × 2 − 1` — map pixel coords → NDC

### 5.4 Fragment shader

```hlsl
half4 frag(v2f i) : SV_Target
{
    float sdf   = length(i.localPos) − i.radius;
    float fw    = fwidth(sdf);
    float alpha = 1.0 − smoothstep(−0.5 * fw, 0.5 * fw, sdf);
    return half4(i.color.rgb, i.color.a * alpha);
}
```

3 instruction làm mọi thứ: tính khoảng cách → đo pixel width → tạo gradient.

### 5.5 VP matrix

```csharp
Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)
             * cam.worldToCameraMatrix;
```

- `cam.worldToCameraMatrix` = **View matrix** (V) — chuyển world coords → camera-local coords
- `cam.projectionMatrix` = **Projection matrix** (P) — chuyển camera-local → clip space
- `GL.GetGPUProjectionMatrix(..., true)` — chuyển P từ Unity convention sang **GPU-native convention**: handle Y-flip (Vulkan Y ngược so với OpenGL) và clip space depth range (D3D [0,1] vs OpenGL [−1,1]). Tham số `true` = "đang render vào render texture" → áp dụng Y-flip nếu cần
- Kết quả: `VP = P × V` — nhân theo thứ tự này vì HLSL `mul(VP, position)` = `VP × position`

---

## 6. URP Integration

### 6.1 Tổng quan

```
ScriptableRendererFeature (composition root, sống qua domain reload)
  ├─ Create()          → tạo drawer + pass, đăng ký provider
  ├─ AddRenderPasses() → enqueue pass mỗi frame
  └─ Dispose()         → giải phóng GPU resources

ScriptableRenderPass (bridge URP ↔ IInstanceDrawer)
  ├─ RecordRenderGraph()  → Unity 6+ RenderGraph (AddUnsafePass)
  └─ Execute()            → Legacy Compatibility Mode
```

### 6.2 RenderGraph path

`AddUnsafePass` (không phải `AddRasterPass`) vì `DrawMeshInstancedIndirect` là raw command buffer operation — `AddRasterPass` chỉ hỗ trợ standard rasterization API.

```csharp
using var builder = renderGraph.AddUnsafePass<PassData>("ParticleRendering", out var passData);

builder.UseTexture(activeColorTexture, AccessFlags.ReadWrite);  // alpha blending đọc pixel hiện tại
builder.UseTexture(activeDepthTexture, AccessFlags.Read);       // depth buffer cần bound dù ZTest Always
builder.AllowPassCulling(false);  // RenderGraph không detect dependency của procedural draw
```

Render func — **static lambda** (zero closure capture → zero GC):

```csharp
builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
{
    context.cmd.SetRenderTarget(data.colorTarget, data.depthTarget);  // explicit bind
    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
    data.drawer.Draw(cmd, data.camera);
});
```

**Explicit `SetRenderTarget`:** `AddUnsafePass` khai báo texture dependencies (để RenderGraph sắp xếp execution order) nhưng **KHÔNG tự bind render targets** (khác `AddRasterPass` tự bind). Nếu thiếu, draw commands render vào "nothing" trên Vulkan/mobile. D3D11 Editor tự inherit render target từ pass trước nên lỗi chỉ lộ trên build.

### 6.3 PassData

```csharp
private class PassData
{
    public IInstanceDrawer drawer;
    public Camera camera;
    public TextureHandle colorTarget;
    public TextureHandle depthTarget;
}
```

**Class, không phải struct** — RenderGraph API yêu cầu: nó pool instances nội bộ, gán qua `out` parameter. Struct sẽ bị copy, mất reference đến pooled object.

RenderGraph pool `PassData` sau frame đầu tiên → **zero heap allocation** từ frame thứ 2.

### 6.4 Static bridge

`ScriptableRendererFeature` (ScriptableObject) không thể reference MonoBehaviour trực tiếp. 3 phương án:

| Phương án | Cách hoạt động | Trade-off |
|-----------|---------------|-----------|
| **Static field ✓** | Provider giữ static reference | Đơn giản nhất. Đủ cho single-drawer |
| Singleton | `ParticleDrawer.Instance` | Cần MonoBehaviour → phải sống trên GameObject |
| SO bridge | Cả 2 reference cùng 1 SO asset | Cần tạo asset, đúng cho Inspector config |

`ParticleProvider` là static class đóng vai bridge:

```
ParticleRendererFeature ──Register()──► ParticleProvider (static)
                                            ├── .Writer → IInstanceWriter<T> ◄── Simulation
                                            └── .Drawer → IInstanceDrawer    ◄── ParticleRenderPass
```

Simulation chỉ thấy `IInstanceWriter<T>`, render pass chỉ thấy `IInstanceDrawer`. Cả hai đều là interface của cùng 1 `ParticleDrawer` — nhưng consumer không biết nhau, không biết concrete type.

### 6.5 Lazy material

```csharp
Resources.Load<Shader>("ParticleDraw")   // ✓ load trực tiếp bằng path
Shader.Find("FelixFelicis/ParticleDraw") // ✗ chỉ tìm shader đã compiled + registered
```

Hệ thống tạo material **runtime** (`new Material(shader)`) — không có material asset nào reference shader. `Shader.Find()` chỉ tìm shader được reference bởi material asset hoặc nằm trong Always Included Shaders → trả `null` trên build. `Resources.Load<Shader>()` load trực tiếp từ `Resources/` folder → guaranteed included.

`EnsureMaterial()` retry mỗi frame. Khi shader sẵn sàng → tạo material → các frame sau chỉ check `material != null` → return true (1 branch).

---

## 7. GraphicsBuffer thay vì ComputeBuffer

| | `ComputeBuffer` | `GraphicsBuffer` |
|---|---|---|
| Unity 6 status | Legacy, vẫn hoạt động | **Recommended API** |
| API | Tách type theo `ComputeBufferType` | `GraphicsBuffer.Target` — nhất quán hơn |
| `SetData(NativeArray)` | Hỗ trợ | Hỗ trợ |
| Indirect args | `ComputeBufferType.IndirectArguments` | `GraphicsBuffer.Target.IndirectArguments` |
| Structured data | `ComputeBufferType.Structured` | `GraphicsBuffer.Target.Structured` |

---

## 8. Safety guards

| Guard | Vị trí | Chống |
|-------|--------|-------|
| `!EnsureMaterial()` → return default | BeginFrame | Shader chưa load (domain reload, Resources missing) |
| `count == 0` → return default / skip | BeginFrame, EndFrame | Sim gọi với 0 particles → không tạo buffer thừa |
| `frameDataReady = false` reset | BeginFrame đầu | Draw vẽ stale data sau sim destroy |
| `lastUploadFrame == Time.frameCount` | BeginFrame | BeginFrame gọi 2 lần cùng frame → swap writeIndex 2 lần = conflict |
| `cpuStaging` grow-only | EnsureStagingCapacity | Count dao động → liên tục alloc/dealloc |
| `!frameDataReady \|\| count == 0` | Draw | Không có data → skip draw call |
| `renderPass != null` | AddRenderPasses | Feature error → null pass |
| `ReleaseResources()` trong Create() | Create | URP gọi Create() nhiều lần → GPU resource leak |
| `buffer.IsValid()` | EnsureInstanceBuffer | Buffer invalidate (hot reload, device lost) |
| `instance == drawer` check | Unregister | Tránh unregister drawer sai instance |

---

## 9. Vulkan/Android — 6 vấn đề D3D11 Editor che giấu

Hệ thống ban đầu hoạt động trên Editor (D3D11) nhưng **invisible trên Android** (Vulkan). Nguyên nhân: D3D11 rất "tha thứ" — tự sync, tự bind, tự gom. Vulkan yêu cầu **explicit** mọi bước.

| # | Vấn đề | D3D11 tại sao hoạt động | Fix |
|---|--------|------------------------|-----|
| 1 | `CGPROGRAM` + `UnityCG.cginc` | Backward compatibility layer | `HLSLPROGRAM` + `Core.hlsl` + `"RenderPipeline"="UniversalPipeline"` tag |
| 2 | `Shader.Find()` trả null | Editor register tất cả shaders | `Resources.Load<Shader>()` |
| 3 | Loose shader globals | D3D11 tự gom vào `$Globals` CBUFFER | `CBUFFER_START(ParticleUniforms)` explicit |
| 4 | `material.Set*()` stale | D3D11 auto-sync material state | `cmd.SetGlobal*()` vào command stream |
| 5 | Không `SetRenderTarget` | Inherit từ previous pass | Explicit `context.cmd.SetRenderTarget(color, depth)` |
| 6 | `LockBufferForWrite` stale | Desktop flush correctly | `SetData(NativeArray)` — driver handle cache coherence |

---

## 10. Kiến trúc files

```
ParticleRendering/
├─ ParticleRendering.md                ← tài liệu này
├─ Resources/
│   ├─ ParticleDraw.shader             ← SDF circle + color unpack + fwidth AA
│   └─ SpaceTransformHelper.hlsl       ← CBUFFER uniforms + world/screen-space transform
└─ Scripts/
    ├─ IInstanceWriter.cs              ← interface: BeginFrame / EndFrame
    ├─ IInstanceDrawer.cs              ← interface: Draw
    ├─ ParticleRenderData.cs           ← 16B struct + PackColor()
    ├─ QuadMeshHelper.cs               ← quad mesh factory (4 vertices, UploadMeshData(true))
    ├─ ParticleDrawer.cs               ← core: double buffer + staging + lazy material + draw
    ├─ ParticleProvider.cs             ← static bridge (Writer/Drawer via interface)
    ├─ ParticleRendererFeature.cs      ← URP composition root
    ├─ ParticleRenderPass.cs           ← URP pass (RenderGraph + Legacy)
    └─ ParticleDebugSim.cs             ← debug sim (thay bằng sim thật)
```

### Dependency graph

```
ParticleRendererFeature (composition root)
  ├── creates ──► ParticleDrawer ◄── implements ── IInstanceWriter<T>
  │                    ▲                            IInstanceDrawer
  │                    │                            IDisposable
  ├── creates ──► ParticleRenderPass
  │                    └── depends on ──► IInstanceDrawer (interface only)
  │
  └── registers ──► ParticleProvider (static bridge)
                       ├── .Writer ──► IInstanceWriter<T> ◄── Simulation reads
                       └── .Drawer ──► IInstanceDrawer    ◄── ParticleRenderPass reads
```

### File roles

| File | Vai trò |
|------|---------|
| `ParticleDrawer` | Quản lý 2 GraphicsBuffer luân phiên, CPU staging NativeArray, lazy material, grow-only args buffer, phát draw call qua `cmd.SetGlobal*()`. Implement cả IInstanceWriter lẫn IInstanceDrawer |
| `ParticleProvider` | Static bridge — giữ concrete drawer nội bộ, expose Writer/Drawer qua interface. Writer và drawer phải share cùng buffer lifecycle nên phải là 1 object |
| `ParticleRendererFeature` | Composition root — tạo drawer + pass, đăng ký provider, release khi dispose. Gọi ReleaseResources() trong Create() vì URP gọi Create() nhiều lần |
| `ParticleRenderPass` | RenderGraph: AddUnsafePass + explicit SetRenderTarget + PassData pooled + static lambda. Legacy: CommandBufferPool. Chỉ gọi `drawer.Draw()` — không upload data |
| `ParticleDebugSim` | Pre-allocate arrays + pre-pack colors trong Start(). Update ghi vào CPU staging NativeArray, EndFrame upload qua SetData |
| `QuadMeshHelper` | Tạo 1×1 quad (4 vertices, 6 indices). `UploadMeshData(true)` giải phóng CPU copy — mesh chỉ sống trên GPU |

---

## 11. Debug sim

`ParticleDebugSim` — MonoBehaviour test rendering pipeline. Thiết kế:

- **Start():** pre-allocate tất cả arrays (`positions`, `velocities`, `radii`, `packedColors`). `PackColor()` gọi 1 lần ở đây (cold path), không ở Update (hot path)
- **Update():** di chuyển particles (bounce khi chạm biên), ghi thẳng vào `NativeArray` qua `ParticleProvider.Writer`
- Runtime Inspector thay đổi `particleCount` không có hiệu lực — cần restart Play
- Simulation thật nếu color thay đổi mỗi frame → pack trong Update vẫn rẻ hơn truyền `Color` 16 bytes

---

## 12. Kiểm thử

| # | Kiểm tra | Pass | Debug nếu fail |
|---|----------|------|-----------------|
| 1 | Play với ParticleDebugSim | Circles hiện | Camera, ZTest, shader error |
| 2 | Frame Debugger | 1 draw call "ParticleRendering" | Buffer/args sai |
| 3 | 10.000+ particles | > 60 FPS | Profiler → SetData |
| 4 | Zoom gần 1 circle | Biên mượt | AA padding (radius × 0.1) |
| 5 | Game + Scene view cùng lúc | Cả 2 đúng | VP matrix per-camera |
| 6 | Enter/Exit Play 3 lần | Không error, memory ổn | ReleaseResources leak |
| 7 | Profiler → GC Alloc | 0 trong render path | Managed allocation |
| 8 | Build APK → test trên device | Circles hiện | `adb logcat -s Unity` |

---

## 13. Mở rộng

| Ưu tiên | Tính năng | Thay đổi |
|---------|-----------|----------|
| A | Color gradient theo velocity | Thêm velocity vào struct, shader lerp màu |
| B | Depth occlusion | `ZTest LEqual` — particles bị geometry che |
| C | Compute shader simulation | GPU-only pipeline, bỏ CPU staging + SetData |
| D | Rectangular masking | maskMin/maskMax trong struct + shader clip |
| E | Multi-shape | Type field + SDF cho quad/line/triangle |

---

## 14. Tóm tắt hiệu năng

| Metric | Giá trị |
|--------|---------|
| Copies per particle per frame | **1** (NativeArray → SetData → GPU) |
| Bytes per particle | **16** (power-of-2, cache-aligned) |
| Draw calls | **1** per camera |
| Bandwidth (10K particles) | **160 KB/frame** |
| GC alloc in render path | **0** |
| Buffers | Double-buffered, grow-only |
| CPU staging | NativeArray (persistent, grow-only) |
| Uniform binding | `cmd.SetGlobal*()` (command stream) |
| AA | `fwidth()` + `smoothstep()` (1-pixel gradient) |
| Shader | HLSL + URP Core.hlsl + CBUFFER |
