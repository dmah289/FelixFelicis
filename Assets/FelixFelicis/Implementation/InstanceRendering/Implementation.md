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
