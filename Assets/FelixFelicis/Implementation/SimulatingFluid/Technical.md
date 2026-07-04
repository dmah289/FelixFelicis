# Vẽ hình tròn trên GPU — Từ lý thuyết đến triển khai

> Tài liệu này giải thích cách vẽ hàng nghìn hình tròn hiệu quả trên GPU,
> dựa trên phân tích thư viện SebVis (`Smoke-Simulation/Assets/Seb/SebVis/`),
> và hướng dẫn triển khai lại từ đầu trong FelixFelicis.

---

## Mục 1 — Kỹ thuật vẽ hình tròn trên GPU

### 1.1 Ý tưởng chính

Cách thông thường vẽ hình tròn trong game là tạo mesh đa giác (32–64 cạnh) rồi render.
Mesh là tập hợp các đỉnh (vertices) nối thành tam giác — GPU chỉ biết vẽ tam giác.
Cách này có 3 nhược điểm:

- **Zoom gần** thấy rõ các cạnh thẳng (polygon artifacts).
- **Nhiều vertices** — 64 cạnh = 192 vertices, trong khi hình vuông chỉ cần 4.
- **Mỗi hình tròn = 1 draw call** riêng → vẽ 10,000 hạt = 10,000 draw calls → rất chậm.

SebVis dùng cách khác: gửi lên GPU một **hình vuông** (quad, 4 vertices), rồi dùng **toán học trong shader** để quyết định pixel nào thuộc hình tròn. Kết hợp với **GPU Instancing**, hàng nghìn hình tròn được vẽ trong **1 draw call duy nhất**.

```
    Thu thập data         Upload lên GPU          Vẽ trên GPU
  ┌──────────────┐    ┌──────────────────┐    ┌─────────────────────┐
  │  Mỗi circle  │    │  ComputeBuffer   │    │ Vertex: scale quad  │
  │  → struct    │ →  │  chứa tất cả     │ →  │ Fragment: SDF→alpha │
  │  → thêm vào  │    │  structs         │    │ → tròn hoàn hảo     │
  │    List<>    │    │  1 draw call     │    │   + anti-aliasing   │
  └──────────────┘    └──────────────────┘    └─────────────────────┘
```
```
Vertex shader (chạy 4 lần)          Fragment shader (chạy N lần, N = số pixel)
┌───────────────────────────┐      ┌─────────────────────────────────────┐
│ Đọc data → Scale quad     │      │ Tính SDF → pixel thuộc circle?     │
│ → Xác định 4 góc trên màn │  →   │ → chọn màu + alpha                 │
└───────────────────────────┘      │ GPU chạy song song cho mọi pixel   │
                                   └─────────────────────────────────────┘
```

Các phần tiếp theo đi theo thứ tự pipeline:
GPU Instancing (1.2) → Quad + Vertex Shader (1.3) → SDF (1.4) → Anti-aliasing (1.5) → Render State (1.6).

---

### 1.2 GPU Instancing — Vẽ N lần trong 1 lệnh

#### Vấn đề: Draw call bottleneck

Mỗi lần CPU ra lệnh cho GPU vẽ gì đó, nó phải chuẩn bị nhiều thứ: chọn shader, gắn texture, set các tham số (gọi là **uniforms** — giá trị mà CPU truyền cho shader, giữ nguyên cho mọi pixel trong cùng 1 draw call). Quá trình này gọi là **draw call**.

Chi phí của 1 draw call gần như **không phụ thuộc** vào số polygon — vẽ 1 quad hay 1000 quads tốn gần bằng nhau nếu cùng 1 draw call. Nhưng 1000 draw calls riêng biệt thì rất chậm.

→ Bottleneck nằm ở **số draw calls**, không phải tổng số triangles.

#### Giải pháp: GPU Instancing

GPU Instancing cho phép vẽ cùng 1 mesh **N lần** trong **1 draw call**. Mỗi bản sao (instance) có số thứ tự riêng — `SV_InstanceID` — để đọc data riêng từ buffer.

```
1 quad mesh  ×  10,000 instances  =  10,000 quads  trong  1 draw call
Mỗi instance đọc vị trí, kích thước, màu riêng từ StructuredBuffer
```

Unity cung cấp 2 API cho GPU Instancing:

- `DrawMeshInstanced(mesh, material, matrices[])` — truyền mảng transform matrices, **giới hạn 1023** instances mỗi lần gọi.
- `DrawMeshInstancedIndirect(mesh, material, argsBuffer)` — số lượng nằm trong GPU buffer, **không giới hạn**. Data per-instance đọc từ `StructuredBuffer` cho phép truyền data tùy ý (position + radius + color) thay vì chỉ transform matrix.

Ta dùng `Indirect` vì cần vẽ hàng nghìn particles với data tùy chỉnh:

```csharp
cmd.DrawMeshInstancedIndirect(mesh, 0, material, 0, argsBuffer);
```

`argsBuffer` chứa 5 số uint cho GPU biết vẽ bao nhiêu:

| Vị trí | Ý nghĩa | Giá trị cho quad |
|--------|---------|-------------------|
| [0] | Số index của mesh | 6 |
| [1] | Số instances | N (số circles) |
| [2] | Index bắt đầu | 0 |
| [3] | Vertex gốc | 0 |
| [4] | Instance offset | 0 |

**Index vs Vertex:** Quad có 4 đỉnh nhưng GPU vẽ bằng tam giác. 1 quad = 2 tam giác. Mỗi tam giác cần 3 đỉnh → tổng 6. Nhờ **index buffer**, 2 tam giác chia sẻ 2 đỉnh chung (`[0,1,2]` và `[2,1,3]`) thay vì khai báo 6 đỉnh riêng.

#### StructuredBuffer — Gửi per-instance data lên GPU

Mỗi instance cần biết: vẽ ở đâu, to bao nhiêu, màu gì. Dữ liệu được đóng gói vào **struct** phía C#, upload lên GPU qua **ComputeBuffer**, và shader đọc qua **StructuredBuffer**:

```
C# struct                          Shader struct
┌────────────────────┐             ┌────────────────────┐
│ Vector2 centre (8B)│  ─────────► │ float2 centre      │
│ float radius   (4B)│             │ float  radius      │
│ Color col     (16B)│             │ float4 col         │
└────────────────────┘             └────────────────────┘
         28 bytes                         28 bytes
         phải khớp byte-by-byte
```

```csharp
// C#: upload danh sách struct lên GPU
ComputeBuffer buffer = new ComputeBuffer(count, stride);
buffer.SetData(listOfStructs);
material.SetBuffer("InstanceData", buffer);
```

```hlsl
// Shader: đọc data theo instance ID
StructuredBuffer<ParticleData> InstanceData;
ParticleData p = InstanceData[instanceID];
```

#### CommandBuffer — Xếp hàng lệnh vẽ

`CommandBuffer` là danh sách các lệnh vẽ. Thay vì gọi GPU ngay, ta ghi lệnh vào buffer rồi gắn nó vào camera pipeline — camera sẽ thực thi tất cả lệnh cùng lúc tại thời điểm phù hợp trong frame.

Cách gắn CommandBuffer khác nhau tùy render pipeline:

```csharp
// Built-in RP (SebVis dùng cách này):
camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, cmd);

// URP (FelixFelicis dùng cách này):
// Ghi lệnh trong ScriptableRenderPass.Execute(), URP tự quản lý CommandBuffer.
// Chi tiết ở Mục 2, Bước 5.
```

---

### 1.3 Quad Mesh — Hình vuông mà GPU vẽ

Mỗi circle bắt đầu từ 1 hình vuông (quad) gồm 4 đỉnh:

```
(-0.5, +0.5)─────(+0.5, +0.5)
     │                  │
     │     tâm (0,0)    │
     │                  │
(-0.5, -0.5)─────(+0.5, -0.5)
```

Vertex shader làm 3 việc cho mỗi instance:

1. Đọc data (vị trí, radius, màu) từ StructuredBuffer.
2. **Scale** quad thành đúng kích thước hình tròn, lưu `posLocal` để truyền sang fragment shader.
3. Chuyển tọa độ world → **clip space** để GPU biết vẽ ở đâu trên màn hình.

```hlsl
// Bước 2: scale quad
float2 diameter = radius * 2;
float2 vertLocal = v.vertex.xy * (diameter + aaPadding);
//     [-0.5, +0.5]   ×  kích thước thực  =  vị trí thực
```

`aaPadding` là thêm vài pixel mỗi bên cho anti-aliasing (giải thích ở 1.5).

**Clip space** là hệ tọa độ mà GPU dùng để xác định pixel nào trên màn hình. Trục X và Y chạy từ -1 đến +1 (góc dưới-trái = (-1,-1), góc trên-phải = (+1,+1)):

```hlsl
// World-space: dùng View-Projection (VP) matrix từ camera
// VP matrix kết hợp "camera ở đâu, nhìn hướng nào" (View)
// với "góc nhìn rộng bao nhiêu" (Projection)
// → chuyển vị trí 3D thành vị trí trên màn hình
o.posClip = mul(WorldToClipSpace, float4(worldPos, 1.0));
// mul() = phép nhân ma trận × vector
// float4(worldPos, 1.0) = thêm 1.0 vào cuối → cần thiết cho phép nhân ma trận 4×4

// Screen-space: chuyển pixel → clip space trực tiếp (bypass camera)
// Ví dụ: pixel (960, 540) trên màn 1920×1080 → (0, 0) = chính giữa
float2 uv = worldPos.xy / ScreenSize;   // pixel → [0, 1]
o.posClip = float4(uv * 2 - 1, 0, 1);  // [0, 1] → [-1, +1]
```

---

### 1.4 SDF — Xác định pixel nào thuộc hình tròn

#### Signed Distance Field là gì?

SDF là hàm toán học nhận vào **1 điểm** và trả về **khoảng cách có dấu** đến biên hình:

```
SDF < 0  →  điểm nằm TRONG hình
SDF = 0  →  điểm nằm TRÊN biên
SDF > 0  →  điểm nằm NGOÀI hình
```

Với hình tròn bán kính `r`, tâm tại gốc:

```
SDF(p) = √(px² + py²) - r
       = length(p) - r
```

Minh họa trên mặt cắt ngang đi qua tâm hình tròn (bán kính = 3):

```
SDF
 +3 ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ·                   · ─ ─  ngoài xa
 +2                              ·                       ·
 +1                           ·                             ·
  0 ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ●─────────────────────────────────●── biên
 -1                       ░ ·                             · ░
 -2                     ░░░  ·                           · ░░░
 -3 ─ ─ ─ ─ ─ ─ ─ ─ ░░░░░░  · ─ ─ ─ tâm ─ ─ ─ ·  ░░░░░░  ─── sâu bên trong
                     ◄──────── bán kính = 3 ────────►
```

Giá trị SDF thay đổi **liên tục** — đây là điểm mấu chốt cho anti-aliasing (xem 1.5).

#### Tại sao dùng SDF thay vì mesh polygon?

| | Mesh polygon | SDF |
|---|---|---|
| Chất lượng | Zoom gần thấy cạnh | Luôn tròn hoàn hảo |
| Vertices | 64 cạnh = 192 vertices | 4 vertices (1 quad) |
| Anti-aliasing | Cần MSAA (4–8× sampling) | 1 phép tính |
| Kết hợp hình | Khó | Union, intersect, blend dễ dàng |

#### Triển khai trong shader

Fragment shader chạy **trên từng pixel** trong quad. Mỗi pixel nhận `posLocal` (offset từ tâm) do GPU **nội suy** từ vertex shader.

**Nội suy (interpolation)** nghĩa là gì? Vertex shader chỉ tính giá trị tại 4 đỉnh. Fragment shader cần giá trị tại mỗi pixel — có thể hàng nghìn pixel. GPU tự tính giá trị trung gian:

```
Đỉnh trái: posLocal.x = -5     Pixel giữa: posLocal.x = 0     Đỉnh phải: posLocal.x = +5
      ●──────────────────────────────·──────────────────────────────●
      GPU tự nội suy tuyến tính      ↑ đúng tâm hình tròn
```

Nhờ nội suy, mỗi pixel biết nó cách tâm bao xa → SDF hoạt động:

```hlsl
float4 frag(v2f i) : SV_Target
{
    // 1. Tính SDF: khoảng cách từ pixel này đến biên hình tròn
    float sdf = length(i.posLocal) - i.radius;

    // 2. Chuyển SDF thành alpha (độ trong suốt)
    //    sdf < 0 → bên trong → alpha = 1 (opaque)
    //    sdf > 0 → bên ngoài → alpha = 0 (transparent)
    //    Vùng biên → gradient mượt (anti-aliasing, xem chi tiết ở 1.5)
    float fw = fwidth(sdf);
    float alpha = 1.0 - smoothstep(-fw * 0.5, fw * 0.5, sdf);

    return float4(i.col.rgb, alpha * i.col.a);
}
```

---

### 1.5 Anti-aliasing — Làm mượt biên hình tròn

Nếu chỉ dùng `alpha = sdf < 0 ? 1 : 0`, biên hình tròn sẽ bị răng cưa (jagged) vì mỗi pixel hoặc hoàn toàn đen hoặc hoàn toàn trắng — không có trung gian. Cần gradient mượt từ opaque → transparent trong vùng biên, rộng đúng 1 pixel.

#### Approach SebVis: `saturate(0.5 - sdf)`

SebVis tính trước "1 pixel = bao nhiêu đơn vị SDF" ở vertex shader, rồi chuyển SDF sang đơn vị pixel:

```hlsl
float sdf_texel = sdf * invTexelSize;    // chuyển SDF sang đơn vị pixel
float alpha = saturate(0.5 - sdf_texel); // gradient trong 1 pixel
// saturate(x): nếu x < 0 → trả 0, x > 1 → trả 1, còn lại giữ nguyên
```

| sdf (pixels) | 0.5 - sdf | saturate → alpha |
|---|---|---|
| ≤ −0.5 | ≥ 1.0 | **1.0** — bên trong |
| 0.0 | 0.5 | **0.5** — trên biên |
| ≥ +0.5 | ≤ 0.0 | **0.0** — bên ngoài |

Nhanh nhưng phụ thuộc `unity_OrthoParams` — chỉ đúng cho orthographic camera.

#### Approach FelixFelicis: `fwidth()` + `smoothstep()`

`fwidth(sdf)` là lệnh phần cứng GPU. Nó trả về **SDF thay đổi bao nhiêu giữa pixel này và pixel kế bên** — nói cách khác: "1 pixel trên màn hình rộng bao nhiêu đơn vị SDF?"

Tại sao cần biết điều này? Vì vùng gradient phải rộng đúng **1 pixel trên màn hình** — không hơn (blur), không kém (jagged). `fwidth()` tự động đúng cho mọi loại camera và mọi mức zoom.

```hlsl
float fw = fwidth(sdf);
// fw ≈ "1 pixel rộng bao nhiêu" tính theo đơn vị SDF

float alpha = 1.0 - smoothstep(-fw * 0.5, fw * 0.5, sdf);
// smoothstep(a, b, x):
//   x ≤ a → 0
//   x ≥ b → 1
//   a < x < b → chuyển mượt 0→1 theo đường cong chữ S (mượt hơn tuyến tính)
//
// Kết quả:
//   sdf < -fw/2 → alpha = 1 (bên trong)
//   sdf > +fw/2 → alpha = 0 (bên ngoài)
//   giữa → gradient mượt, rộng đúng 1 pixel
```

#### AA Padding — Mở rộng quad cho vùng gradient

Fragment shader chỉ chạy trên pixel **bên trong quad**. Nếu quad vừa khít hình tròn, pixel ở biên ngoài bị cắt mất → không có vùng gradient → biên bị cắt phẳng.

Giải pháp: mở rộng quad thêm vài pixel mỗi bên.

```hlsl
// Screenspace: +2 pixel cố định
// World-space: +10% radius (tỷ lệ, vì radius thay đổi lớn: 0.01 → 100)
float2 aaPadding = useScreenSpace ? 2.0 : p.radius * 0.1;
float2 vertLocal = v.vertex.xy * (diameter + aaPadding);
```

---

### 1.6 Render State — Cấu hình GPU

```hlsl
ZWrite Off                        // Không ghi depth buffer (bộ đệm ghi nhớ "pixel nào gần camera hơn")
                                  // → circles không che nhau theo chiều sâu
ZTest Always                      // Luôn vẽ bất kể depth → overlay trên mọi 3D objects
Cull Off                          // Vẽ cả 2 mặt quad (không quan trọng cho 2D)
Blend SrcAlpha OneMinusSrcAlpha   // Alpha blending: pixel mới trộn với pixel cũ theo alpha
```

`Blend SrcAlpha OneMinusSrcAlpha` nghĩa là:
```
finalColor = circleColor × alpha + backgroundColor × (1 - alpha)
```
Khi `alpha = 0.5` (biên): trộn 50/50 → biên mượt.

---

### 1.7 SebVis — Các lớp bổ sung

SebVis là thư viện UI đầy đủ, có thêm nhiều lớp mà fluid sim không cần. Tham khảo nếu muốn mở rộng sau:

| Tính năng | Mô tả | Cần cho fluid? |
|---|---|---|
| **UI Space** (100 đơn vị) | Hệ tọa độ ảo, scale khi render | Không — dùng world-space |
| **Anchor system** | 9 loại neo (TopLeft, Centre...) | Không |
| **Layer system** | Z-ordering shapes vs text | Không — 1 layer đủ |
| **Object Pooling** | Tái dùng Material, Scope objects | Không ở giai đoạn đầu |
| **Two-pass layout** | Chạy draw functions không render để đo bounds | Không |
| **Rectangular masking** | Clip shapes theo vùng chữ nhật | Có thể, sau |
| **Multi-shape struct** | 1 struct 56 bytes cho mọi loại hình | Không — chỉ cần circle |

---

### 1.8 Call chain tóm tắt

```
DrawCircle(pos, radius, col, anchor)                      C# — SebVis UI layer
│
├─ CalculateCentre(pos, size, anchor)                     tính tâm từ anchor
├─ UIToScreenSpace(centre, size)                          UI units → pixels
├─ Draw.Point(pixelCentre, pixelRadius, col)
│   ├─ ShapeData.CreatePoint(...)                         pack thành struct 56 bytes
│   └─ shapeDrawer.AddToLayer(data)                       thêm vào List<ShapeData>
│
│   ─── cuối frame ───
│
│   Camera.onPreRender → DispatchAll()                    C# — SebVis render layer
│   └─ InstancedDrawer.DrawLayer(cmd, ...)
│       ├─ ComputeBuffer.SetData(allShapes)               upload lên GPU
│       └─ cmd.DrawMeshInstancedIndirect(...)              1 draw call cho N shapes
│
│   ─── GPU ───
│
│   Vertex shader                                         scale quad theo radius
│   └─ WorldToClipPos(worldPos)                           pixel → clip space
│
│   Fragment shader
│   ├─ sdf = length(posLocal) - radius                    SDF circle
│   ├─ alpha = saturate(0.5 - sdf)                        anti-aliasing
│   └─ Blend SrcAlpha OneMinusSrcAlpha                    alpha compositing
│
├─ OnFinishedDrawingUIElement(centre, size)                layout tracking (UI-specific)
```

---

## Mục 2 — Triển khai lại trong FelixFelicis

### Tổng quan

| | SebVis (Smoke-Simulation) | FelixFelicis (horcrux_algo) |
|---|---|---|
| Render Pipeline | Built-in RP | **URP** |
| Camera hook | `Camera.onPreRender` | **`ScriptableRenderPass`** |
| Không gian vẽ | Screen-space (pixel) | **World-space (3D scene)** |
| Y-flip | Shader tự flip | **C# xử lý qua `GL.GetGPUProjectionMatrix`** |
| AA approach | `invTexelSize` (orthographic only) | **`fwidth()` (mọi camera type)** |
| Shape types | Circle, quad, line, triangle, diamond | **Chỉ circle** |
| Struct size | 56 bytes (đa năng) | **28 bytes (chuyên biệt)** |

Namespace: `FelixFelicis.Runtime`. Assembly: `com.FelixFelicis.Runtime`.

---

### Bước 0 — Thêm URP reference vào assembly

Code URP (`ScriptableRenderPass`, `ScriptableRendererFeature`) nằm trong assembly riêng. Assembly `com.FelixFelicis.Runtime` hiện chưa reference nó → **mọi code URP sẽ không compile**.

Sửa `com.FelixFelicis.Runtime.asmdef`:

```json
{
    "name": "com.FelixFelicis.Runtime",
    "references": [
        "Unity.RenderPipelines.Universal.Runtime"
    ]
}
```

---

### Bước 1 — Shader

Tạo 2 file trong `Rendering/Resources/`.

**Tại sao `Resources/`?** C# dùng `Shader.Find("FelixFelicis/FluidDraw")` để tìm shader theo tên. Unity chỉ include shader vào build nếu nó nằm trong `Resources/` hoặc được gắn trực tiếp vào Material trong scene. `Resources/` đảm bảo shader luôn có sẵn.

**`FluidDrawCommon.hlsl`** — Hàm chuyển tọa độ:

```hlsl
float4x4 WorldToClipSpace;   // VP matrix — set từ C# mỗi frame
float2 ScreenSize;
int useScreenSpace;

float4 WorldToClipPos(float3 worldPos)
{
    if (useScreenSpace)
    {
        float2 uv = worldPos.xy / ScreenSize;
        return float4(uv * 2 - 1, 0, 1);
    }
    // GL.GetGPUProjectionMatrix() phía C# đã xử lý Y-flip cho platform hiện tại
    // → KHÔNG thêm _ProjectionParams.x hay UNITY_UV_STARTS_AT_TOP ở đây
    return mul(WorldToClipSpace, float4(worldPos, 1.0));
}
```

**`FluidDraw.shader`:**

```hlsl
Shader "FelixFelicis/FluidDraw"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            // CGPROGRAM (thay vì HLSLPROGRAM) vì nó tự include UnityCG.cginc
            // → cung cấp sẵn các biến Unity built-in (_ScreenParams, unity_OrthoParams, v.v.)
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "FluidDrawCommon.hlsl"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 posClip   : SV_POSITION;
                float2 posLocal  : TEXCOORD0;
                float  radius    : TEXCOORD1;
                float4 col       : TEXCOORD2;
            };

            // Phải khớp byte-by-byte với C# ParticleRenderData
            struct ParticleData
            {
                float2 centre;
                float  radius;
                float4 col;
            };

            StructuredBuffer<ParticleData> InstanceData;
            uint InstanceOffset;

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                ParticleData p = InstanceData[instanceID + InstanceOffset];
                v2f o;
                o.col = p.col;

                // Mở rộng quad cho vùng anti-aliasing gradient
                float2 aaPadding = useScreenSpace ? 2.0 : p.radius * 0.1;

                float2 diameter = p.radius * 2;
                float2 vertLocal = v.vertex.xy * (diameter + aaPadding);
                float3 worldPos = float3(p.centre + vertLocal, 0);

                o.posLocal = vertLocal;
                o.radius = p.radius;
                o.posClip = WorldToClipPos(worldPos);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float sdf = length(i.posLocal) - i.radius;

                float fw = fwidth(sdf);
                float alpha = 1.0 - smoothstep(-fw * 0.5, fw * 0.5, sdf);

                return float4(i.col.rgb, alpha * i.col.a);
            }
            ENDCG
        }
    }
}
```

---

### Bước 2 — Quad Mesh

```csharp
using UnityEngine;

namespace FelixFelicis.Runtime
{
    public static class QuadMeshHelper
    {
        public static Mesh CreateQuadMesh()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new Vector3[]
            {
                new(-0.5f,  0.5f, 0),
                new( 0.5f,  0.5f, 0),
                new(-0.5f, -0.5f, 0),
                new( 0.5f, -0.5f, 0),
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 3 }, 0, true);
            return mesh;
        }
    }
}
```

---

### Bước 3 — Particle Data Struct

```csharp
using System.Runtime.InteropServices;
using UnityEngine;

namespace FelixFelicis.Runtime
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ParticleRenderData
    {
        public Vector2 centre;   // 8 bytes  — khớp float2 trong shader
        public float radius;     // 4 bytes  — khớp float
        public Color col;        // 16 bytes — khớp float4
        // Total: 28 bytes
    }
}
```

**Lưu ý quan trọng:**

- **Thứ tự trường phải khớp** với struct `ParticleData` trong shader. `StructLayout.Sequential` đảm bảo C# compiler không sắp xếp lại.
- **Verify stride:** thêm `Debug.Assert(Marshal.SizeOf<ParticleRenderData>() == 28)` trong code init. **Stride** là kích thước 1 struct tính bằng bytes — GPU dùng stride để biết "nhảy bao xa" khi đọc phần tử tiếp theo trong buffer. Nếu stride C# ≠ stride shader → GPU đọc lệch → hình sai hoặc crash.
- Nếu gặp lỗi stride trên một số platform, thêm `float _padding` (4 bytes) để đạt 32.

---

### Bước 4 — Instanced Drawer

Class trung tâm: nhận data từ simulation, upload lên GPU, phát draw call.

```csharp
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FelixFelicis.Runtime
{
    public class ParticleDrawer
    {
        // Cache shader property IDs — tránh string lookup mỗi frame
        static readonly int InstanceDataID = Shader.PropertyToID("InstanceData");
        static readonly int InstanceOffsetID = Shader.PropertyToID("InstanceOffset");
        static readonly int ScreenSizeID = Shader.PropertyToID("ScreenSize");
        static readonly int UseScreenSpaceID = Shader.PropertyToID("useScreenSpace");
        static readonly int WorldToClipID = Shader.PropertyToID("WorldToClipSpace");

        readonly Mesh quadMesh;
        readonly Material material;
        readonly List<ParticleRenderData> frameData = new();
        readonly int stride = Marshal.SizeOf<ParticleRenderData>();

        ComputeBuffer instanceBuffer;
        ComputeBuffer argsBuffer;
        readonly uint[] args = new uint[5];

        public ParticleDrawer()
        {
            quadMesh = QuadMeshHelper.CreateQuadMesh();
            var shader = Shader.Find("FelixFelicis/FluidDraw");
            Debug.Assert(shader != null, "Shader 'FelixFelicis/FluidDraw' not found.");
            material = new Material(shader);
        }

        public void Clear() => frameData.Clear();

        public void AddParticle(Vector2 centre, float radius, Color col)
        {
            frameData.Add(new ParticleRenderData
            {
                centre = centre,
                radius = radius,
                col = col
            });
        }

        public void Dispatch(CommandBuffer cmd, Camera cam)
        {
            int count = frameData.Count;
            if (count == 0) return;

            // Instance buffer: chỉ tạo mới khi cần mở rộng, tái sử dụng khi đủ chỗ
            if (instanceBuffer == null || !instanceBuffer.IsValid() || instanceBuffer.count < count)
            {
                instanceBuffer?.Release();
                instanceBuffer = new ComputeBuffer(count, stride);
            }
            instanceBuffer.SetData(frameData);

            // Args buffer: 5 uint cho DrawMeshInstancedIndirect
            if (argsBuffer == null || !argsBuffer.IsValid())
            {
                argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5,
                    ComputeBufferType.IndirectArguments);
            }
            args[0] = quadMesh.GetIndexCount(0);  // 6
            args[1] = (uint)count;                 // số circles
            args[2] = quadMesh.GetIndexStart(0);   // 0
            args[3] = quadMesh.GetBaseVertex(0);   // 0
            args[4] = 0;
            argsBuffer.SetData(args);

            // Set uniforms cho shader
            material.SetBuffer(InstanceDataID, instanceBuffer);
            material.SetInt(InstanceOffsetID, 0);
            material.SetVector(ScreenSizeID,
                new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0));
            material.SetInt(UseScreenSpaceID, 0);  // world-space

            // VP matrix — GL.GetGPUProjectionMatrix xử lý Y-flip cho platform hiện tại
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true)
                         * cam.worldToCameraMatrix;
            material.SetMatrix(WorldToClipID, vp);

            cmd.DrawMeshInstancedIndirect(quadMesh, 0, material, 0, argsBuffer);
        }

        public void Release()
        {
            instanceBuffer?.Release();
            instanceBuffer = null;
            argsBuffer?.Release();
            argsBuffer = null;
            if (material != null)
            {
                if (Application.isPlaying) Object.Destroy(material);
                else Object.DestroyImmediate(material);
            }
        }
    }
}
```

**Tại sao buffer chỉ grow, không shrink?**
Frame trước 10,000 particles, frame này 5,000 → tái sử dụng buffer 10,000.
`args[1] = count` đảm bảo GPU chỉ đọc 5,000 đầu tiên.
Tạo buffer mới tốn thời gian → tránh tạo lại mỗi frame.

---

### Bước 5 — URP Render Feature

SebVis (Built-in RP) gắn CommandBuffer vào camera bằng `camera.AddCommandBuffer()` — API này **không tồn tại** trên URP. URP dùng 2 class thay thế:

- **`ScriptableRenderPass`** — chứa logic vẽ (tương đương `Camera.onPreRender`).
- **`ScriptableRendererFeature`** — đăng ký pass vào URP pipeline.

**5a. `FluidRenderPass.cs`:**

```csharp
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FelixFelicis.Runtime
{
    public class FluidRenderPass : ScriptableRenderPass
    {
        readonly ParticleDrawer drawer;

        public FluidRenderPass(ParticleDrawer drawer)
        {
            this.drawer = drawer;
            // Thời điểm vẽ trong URP pipeline:
            // BeforeRenderingTransparents = cùng lúc transparent objects
            // AfterRenderingPostProcessing = overlay trên mọi thứ
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        }

        public override void Execute(
            ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // URP có sẵn pool CommandBuffer — không cần tạo/release thủ công
            CommandBuffer cmd = CommandBufferPool.Get("FluidParticles");

            drawer.Clear();
            // Thu thập particles từ simulation — xem 5c bên dưới
            // foreach (var p in ...) drawer.AddParticle(p.pos, p.radius, p.color);

            drawer.Dispatch(cmd, renderingData.cameraData.camera);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }
}
```

**5b. `FluidRendererFeature.cs`:**

```csharp
using UnityEngine.Rendering.Universal;

namespace FelixFelicis.Runtime
{
    public class FluidRendererFeature : ScriptableRendererFeature
    {
        ParticleDrawer drawer;
        FluidRenderPass renderPass;

        public override void Create()
        {
            drawer = new ParticleDrawer();
            renderPass = new FluidRenderPass(drawer);
        }

        public override void AddRenderPasses(
            ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(renderPass);
        }

        protected override void Dispose(bool disposing)
        {
            drawer?.Release();
        }
    }
}
```

**5c. Kết nối simulation data → renderer:**

`ScriptableRendererFeature` kế thừa từ `ScriptableObject` — nó là asset, không sống trên GameObject, nên không thể reference MonoBehaviour trực tiếp. Cách giải quyết:

| Phương án | Cách hoạt động | Khi nào dùng |
|---|---|---|
| **A. Static field** | Simulation ghi vào `static List<ParticleRenderData>`, RenderPass đọc ra | Prototype |
| **B. Singleton** | `FluidSimulation.Instance.particles` | Production |
| **C. ScriptableObject bridge** | Cả hai reference cùng 1 SO asset | Cấu hình từ Editor |

**5d. Thêm vào URP trong Unity Editor:**

```
1. Mở Assets/Settings/PC_Renderer.asset
2. Inspector → Add Renderer Feature → FluidRendererFeature
3. Play → particles xuất hiện
```

---

### Bước 6 — Kiểm thử

Mỗi bước xác nhận đúng 1 thứ. Cột "Debug" cho biết kiểm tra gì nếu fail.

| Bước | Làm gì | Pass khi | Debug nếu fail |
|------|--------|----------|-----------------|
| **6a** | Mở Unity | Không có shader error | Shader Model 4.5+, cú pháp HLSL |
| **6b** | Hardcode `AddParticle(Vector2.zero, 1f, Color.red)` | 1 hình tròn đỏ tại origin | Không thấy → camera, ZTest, RenderPassEvent. Thấy vuông → SDF sai |
| **6c** | 100 circles ngẫu nhiên | Frame Debugger: 1 draw call | > 1 draw call → buffer/args sai |
| **6d** | Di chuyển circles, chạy 60 giây | Memory ổn định | Tăng liên tục → buffer không tái sử dụng |
| **6e** | Zoom rất gần 1 circle | Biên mượt | Jagged → AA padding nhỏ. Blur → padding lớn |
| **6f** | 10,000+ circles | > 60 FPS | Chậm → kiểm tra SetData, buffer resize |
| **6g** | Kết nối fluid simulation | Particles đúng vị trí | Sai → đơn vị world-space không khớp |

---

### Bước 7 — Mở rộng sau khi core hoạt động

Sắp theo ưu tiên cho fluid simulation:

| Ưu tiên | Tính năng | Mô tả |
|---------|-----------|-------|
| **A** | Color gradient | Thêm velocity vào struct, shader lerp màu theo tốc độ |
| **B** | Depth occlusion | `ZTest Always` → `LEqual`, particles bị geometry che |
| **C** | Compute shader | Simulation trên GPU → bỏ `SetData()`, truyền buffer trực tiếp |
| D | Masking | Thêm maskMin/maskMax vào struct + shader |
| E | Multi-shape | Thêm type field, SDF cho quad/line |
| F | Layer system | Nhiều layer, z-ordering |

---

### Cấu trúc file

```
Assets/FelixFelicis/Runtime/Implementation/SimulatingFluid/
├─ Technical.md                     ← tài liệu này
├─ Rendering/
│   ├─ ParticleRenderData.cs        ← Bước 3
│   ├─ QuadMeshHelper.cs            ← Bước 2
│   ├─ ParticleDrawer.cs            ← Bước 4
│   ├─ FluidRenderPass.cs           ← Bước 5a
│   ├─ FluidRendererFeature.cs      ← Bước 5b
│   └─ Resources/
│       ├─ FluidDraw.shader         ← Bước 1
│       └─ FluidDrawCommon.hlsl     ← Bước 1
```

### Thứ tự triển khai

```
Bước 0 ──────────────────── Assembly reference (30 giây)
                             Nếu quên → code URP không compile

Bước 1 + 2 + 3 ─────────── Shader + Quad + Struct (song song)

Bước 4 ──────────────────── ParticleDrawer (cần 1, 2, 3)

Bước 5 ──────────────────── URP RendererFeature (cần 4)
                             Nhớ thêm vào PC_Renderer.asset

Bước 6 ──────────────────── Test tuần tự 6a → 6g

Bước 7 ──────────────────── Mở rộng (sau khi 6g pass)
```
