# Mobile GPU Compatibility — ParticleRendering

> Phân tích khả năng tương thích của hệ thống ParticleRendering trên các họ GPU mobile phổ biến.
> Dựa trên Unity Issue Tracker, Unity Forums, Khronos specs, vendor docs, và dữ liệu thị trường 2024–2026.

Namespace: `FelixFelicis.ParticleRendering` · Ngày phân tích: 2026-07-06

---

## 1. Thiết bị đã test

**POCO M7 Pro 5G** — SoC **MediaTek Dimensity 7025-Ultra** (6nm), GPU **Imagination PowerVR IMG BXM-8-256** (950 MHz).

Hỗ trợ Vulkan 1.3, OpenGL ES 3.x, OpenCL 3.0 → đáp ứng mọi yêu cầu kỹ thuật.

PowerVR chiếm ~5–10% thị phần Android — **chưa cover 2 họ GPU lớn nhất**: ARM Mali (~40–45%) và Qualcomm Adreno (~26–30%).

> **Lưu ý:** IMG BXM-8-256 có [danh sách driver bug được báo cáo](https://forums.imgtec.com/t/bxm-8-256-long-list-of-driver-issues/3891) trên Imagination forums và [crash report trên Vulkan sau ~2 phút](https://github.com/hrydgard/ppsspp/issues/18681). Hệ thống chạy ổn có thể do workload nhẹ (200 particles mặc định).

---

## 2. Yêu cầu phần cứng

Hệ thống dùng `#pragma target 4.5` + `StructuredBuffer` + `DrawMeshInstancedIndirect` → **bắt buộc OpenGL ES 3.1+ hoặc Vulkan**.

| Yêu cầu | API tối thiểu | Ghi chú |
|----------|:-------------:|---------|
| `StructuredBuffer<T>` | ES 3.1 / Vulkan | SSBO (Shader Storage Buffer Object) |
| `DrawMeshInstancedIndirect` | ES 3.1 / Vulkan | Yêu cầu compute shader support |
| `SV_InstanceID` | ES 3.0 / Vulkan | Không phải bottleneck |
| `fwidth()` | ES 3.0 / Vulkan | Không phải bottleneck |
| `CBUFFER` explicit | ES 3.1 / Vulkan | Standard trên mọi GPU hỗ trợ ES 3.1 |
| `GraphicsBuffer.Target.Structured` | ES 3.1 / Vulkan | Stride phải là bội số 4, lý tưởng 16 |
| `GraphicsBuffer.Target.IndirectArguments` | ES 3.1 / Vulkan | args buffer 5 × uint |

**Ước tính coverage:** >90% thiết bị Android đang hoạt động (2024–2026) hỗ trợ ES 3.1+. Unity 6.6 sẽ [nâng minimum lên ES 3.1](https://discussions.unity.com/t/increasing-the-opengles-minimum-spec-in-unity-6-6/1719163).

---

## 3. Thị phần GPU mobile Android (2024–2025)

| Họ GPU | Vendor SoC | Thị phần ước tính | Xu hướng |
|--------|-----------|:-----------------:|----------|
| **ARM Mali / Immortalis** | MediaTek (cũ), Samsung Exynos, Unisoc | ~40–45% | Ổn định |
| **Qualcomm Adreno** | Qualcomm Snapdragon | ~26–30% | Ổn định |
| **IMG PowerVR** | MediaTek (mới, mid-range) | ~5–10% | Tăng nhẹ (MediaTek ký hợp đồng multi-year với Imagination, tháng 11/2024) |
| **Samsung Xclipse (AMD RDNA 2)** | Samsung Exynos 2200/2400 | <2% | Regional (flagship) |

Nguồn: Counterpoint Research Q4 2024, ARM Newsroom, TelecomLead.

---

## 4. GPU tối thiểu hỗ trợ (cutoff)

### 4.1 Lý thuyết vs thực tế

| Họ GPU | Tối thiểu lý thuyết | Tối thiểu thực tế (an toàn) | SoC ví dụ |
|--------|---------------------|:---------------------------:|-----------|
| **Adreno** | Adreno 420 (SD 805, 2014) | **Adreno 530+** (SD 820, 2016) | SD 820/821, SD 835, SD 660 |
| **Mali** | Mali-T760 (Midgard, 2014) | **Mali-G76+** (Bifrost, 2018) | Exynos 9820, Kirin 980/990 |
| **PowerVR** | Series6 (Rogue, 2013) | **GE8320+** (2017) | Helio P22/P35 |
| **Xclipse** | Xclipse 920 (tất cả) | **Tất cả** | Exynos 2200/2400/2500 |
| **Apple** | A8 (Apple2, 2014) | **A8+** (tất cả Metal) | iPhone 6+, iPad Air 2+ |

Tối thiểu thực tế khác lý thuyết vì GPU cũ có driver bug khiến compute/instancing không đáng tin cậy dù báo cáo đủ capability.

### 4.2 GPU KHÔNG chạy được (quá cũ)

| GPU | SoC ví dụ | Điện thoại ví dụ | Lý do |
|-----|-----------|-------------------|-------|
| Mali-400 | MT6580, MT6582 | Budget 2014–2016 | Chỉ ES 2.0 |
| Mali-T720 | MT6735 | Redmi 2, Galaxy J2 | ES 3.1 thiếu compute |
| Adreno 304/305/306 | Snapdragon 210/212 | Galaxy J1/J2 Core | Chỉ ES 3.0 |
| Adreno 308 | Snapdragon 425 | Redmi 5A, Galaxy J4 | ES 3.0, không compute |
| PowerVR GE8100 | MT6739 | Realme C1, Redmi 6A | ES 3.1 nhưng compute rất hạn chế |

Đều là budget 2016–2018, đa số đã hết vòng đời.

### 4.3 Chipset cũ fail hoàn toàn Vulkan compute

| Chipset | GPU | Vấn đề |
|---------|-----|--------|
| Exynos 8890 | Mali-T880 | Mọi Vulkan compute conformance test FAIL |
| Kirin 960 / Exynos 8895 | Mali-G71 | Mọi Vulkan compute conformance test FAIL |
| Snapdragon 625 | Adreno 506 | Compute pipeline creation fails |
| Snapdragon 636 (driver cũ) | Adreno 512 | Mọi compute test FAIL (fix ở driver API 1.0.61+) |

---

## 5. Phân tích rủi ro theo họ GPU

### 5.1 ARM Mali — 🔴 Rủi ro cao nhất

Mali chiếm ~40–45% thị trường Android và là họ GPU **có nhiều vấn đề nhất** với `DrawMeshInstancedIndirect` + `StructuredBuffer`.

#### Vấn đề đã xác nhận

| # | Vấn đề | GPU bị ảnh hưởng | Ảnh hưởng hệ thống? |
|---|--------|-------------------|:--------------------:|
| 1 | [DrawMeshInstancedIndirect ngừng render khi vượt ~3,500 instance](https://issuetracker.unity3d.com/issues/vulkan-android-mali-meshes-stop-being-rendered-using-drawmeshinstanceindirect-when-they-exceed-certain-amount-on-mali-gpus) trên Vulkan. Unity quyết định **Won't Fix** — "internal limitations of Mali GPUs". Giới hạn chính xác [không thể query qua Vulkan API](https://github.com/KhronosGroup/Vulkan-Docs/issues/2266). | Mali-G71, G72, G76 | ⚠️ Có thể nếu count >3,000 |
| 2 | [DrawMeshInstancedIndirect KHÔNG render gì cả trên OpenGL ES](https://discussions.unity.com/t/drawmeshinstancedindirect-not-working-on-specific-gpus-mali-2019-4-36f1/872993) — hoàn toàn 0 output. | G72, G78, T880 | ❌ Nếu dùng GLES |
| 3 | [Chỉ render 1 instance](https://discussions.unity.com/t/drawmeshinstancedindirect-not-working-on-specific-gpus-mali-2019-4-36f1/872993) trên GLES3 — position không translate. | Mali-G78 (Galaxy S21 Exynos) | ❌ Nếu dùng GLES |
| 4 | [StructuredBuffer null access → crash](https://issuetracker.unity3d.com/issues/android-vulkan-crashes-mali-device-when-accessing-null-item-in-structuredbuffer-in-shader) trên Vulkan. Adreno/Nvidia/Tegra không crash. | Mọi Mali + Vulkan | ✅ Code có guard `count == 0` |
| 5 | [Compute shader freeze](https://discussions.unity.com/t/rendering-stops-freezes-on-some-mobile-gpus-using-vulkan-compute-shaders/945225) — 2–3 frame cuối lặp vô hạn cho đến khi xoay màn hình hoặc app background. | Mali (nhiều đời) + Vulkan | ✅ Không dùng compute dispatch |
| 6 | [Flickering / black screen](https://issuetracker.unity3d.com/issues/android-vulkan-flickering-and-rendering-glitches-or-black-screen-with-mali-g72-gpu) trên Mali-G72 + Vulkan. | Mali-G72 | ⚠️ Driver-level |
| 7 | [GraphicsBuffer.CopyCount broken](https://issuetracker.unity3d.com/issues/vulkan-android-mali-meshes-stop-being-rendered-using-drawmeshinstanceindirect-when-they-exceed-certain-amount-on-mali-gpus) — trả kết quả sai. | G72, T760, G76 MC4 | ✅ Không dùng CopyCount |
| 8 | `LockBufferForWrite` cache coherence — GPU đọc stale data. | Mali Valhall (G610, G710, G720) | ✅ **Đã fix** — code dùng `SetData(NativeArray)` |
| 9 | Vertex Shader SSBO = 0 trên GLES — `StructuredBuffer` trong vertex shader **thất bại im lặng**. Unity staff xác nhận: có ít nhất 1 GPU vendor không implement vertex-stage SSBO trên OpenGL, chỉ Vulkan. | Mali (GLES) | ❌ Nếu dùng GLES |

#### Tóm tắt Mali

```
Mali + OpenGL ES  → ❌ Không render gì / chỉ 1 instance / vertex SSBO = 0
Mali + Vulkan     → ✅ Hoạt động, nhưng giới hạn instance ~3,000–3,500 trên G71/G72/G76
```

**→ Vulkan là BẮT BUỘC cho Mali. GLES không phải fallback hợp lệ cho hệ thống này.**

#### Thiết bị Mali phổ biến cần test

| Ưu tiên | GPU | Thiết bị ví dụ | Lý do |
|:-------:|-----|----------------|-------|
| 🔴 P0 | Mali-G57 | Samsung Galaxy A14/A15, Redmi Note 12 | Budget phổ biến nhất |
| 🔴 P0 | Mali-G610 | Samsung Galaxy A34, Dimensity 7200 | Mid-range phổ biến |
| 🟡 P1 | Mali-G710 | Samsung Galaxy S22 (Exynos) | Flagship |
| 🟡 P1 | Mali-G78 | Pixel 6 (Tensor G1) | Google Pixel; Unity deny Vulkan mặc định trên device này |

#### SoC → Mali GPU reference

| Mali GPU | Kiến trúc | SoC tiêu biểu |
|----------|-----------|----------------|
| T880 | Midgard | Exynos 8890, Kirin 950/960 |
| G52 | Bifrost | Helio G80/G85/G90, Kirin 810 |
| G72 | Bifrost | Exynos 9810, Kirin 970 |
| G76 | Bifrost | Exynos 9820, Kirin 980/990 |
| G57 | Valhall | Dimensity 700/800 |
| G68 | Valhall | Dimensity 900/1080, Exynos 1380 |
| G77 | Valhall | Exynos 990 |
| G78 | Valhall | Exynos 2100, Tensor G1 |
| G710 | Valhall | Dimensity 9000 |
| G720 / Immortalis | 5th Gen | Dimensity 9300, Exynos 2400 |

---

### 5.2 Qualcomm Adreno — 🟡 Rủi ro trung bình

Adreno **ổn định hơn Mali** với StructuredBuffer + instancing. Bugs chủ yếu ở driver level, không riêng hệ thống này.

#### Vấn đề đã xác nhận

| # | Vấn đề | GPU bị ảnh hưởng | Ảnh hưởng hệ thống? |
|---|--------|-------------------|:--------------------:|
| 1 | [Crash trên Adreno 619](https://discussions.unity.com/t/in-72861-app-crashes-on-some-android-devices-which-has-adreno-619-gpu/943646) (cả Vulkan & GLES). Xperia 10 III, AQUOS sense6/7, Galaxy A52. | Adreno 619 | ⚠️ Unity engine bug |
| 2 | [Shader render đen](https://discussions.unity.com/t/rendering-issues-on-adreno-610-and-612/821057) trên Adreno 610/612 (Linear color space + Vulkan/GLES3). | Adreno 610, 612 | ⚠️ Không riêng StructuredBuffer |
| 3 | [Memory leak + Vulkan](https://issuetracker.unity3d.com/issues/android-vulkan-memory-leak-on-some-adreno-devices-when-graphics-api-is-set-to-vulkan). Fix có trong Unity 6.3. | Một số Adreno | ⚠️ Engine-level |
| 4 | [Interlocked operations bị bỏ qua](https://issuetracker.unity3d.com/issues/vulkan-adreno-540-and-older-omits-interlocked-operations-when-computebuffer-is-longer-then-65520-star-sizeof-int) khi buffer >65,520 items (Vulkan). | Adreno 540 trở xuống | ✅ Không dùng interlocked |
| 5 | [GPU Instancing particle flickering](https://issuetracker.unity3d.com/issues/particle-systems-that-use-meshes-flicker-in-player-when-gpu-instancing-is-enabled-on-older-adreno-gpus) (2025). | Adreno cũ (không phải 640+) | ⚠️ Có thể |
| 6 | [Glitchy geometry](https://issuetracker.unity3d.com/issues/android-graphics-dot-drawmeshinstancedindirect-is-glitchy-on-devices-containing-adreno-or-nvidia-gpus) — sphere thành cube. | Adreno 540, 506 | ⚠️ GPU rất cũ |

#### Thiết bị Adreno phổ biến cần test

| Ưu tiên | GPU | Thiết bị ví dụ | Lý do |
|:-------:|-----|----------------|-------|
| 🔴 P0 | Adreno 619 | Redmi Note 12 5G, Galaxy A52 | Có crash bug đã xác nhận |
| 🟡 P1 | Adreno 610 | Redmi 9, Galaxy A21s | Shader đen? |
| 🟢 P2 | Adreno 730/740 | Galaxy S22/S23 (Snapdragon) | Performance baseline |

#### Thiết bị Adreno đã xác nhận hoạt động

| Thiết bị | GPU | Nguồn |
|----------|-----|-------|
| Galaxy Note10+ 5G | Adreno 640 | Unity Issue Tracker |
| Xiaomi MI 9 | Adreno 640 | Unity Issue Tracker |
| ROG Phone (SD 845) | Adreno 630 | Unity Issue Tracker |
| Galaxy S21 (Snapdragon) | Adreno 660 | Unity Issue Tracker |
| Pixel 3 | Adreno 630 | Unity Issue Tracker |
| Galaxy A70 | Adreno 612 | [MobileDrawMeshInstancedIndirectExample](https://github.com/ColinLeung-NiloCat/UnityURP-MobileDrawMeshInstancedIndirectExample) — 10M grass, 50–60 FPS |

#### SoC → Adreno GPU reference

| Snapdragon | Adreno | Năm | Ghi chú |
|------------|--------|:---:|---------|
| SD 625 | 506 | 2016 | Compute pipeline có thể fail |
| SD 660 | 512 | 2017 | OK với driver API 1.0.61+ |
| SD 845 | 630 | 2018 | ✅ Confirmed working |
| SD 855 | 640 | 2019 | ✅ Confirmed working |
| SD 865/870 | 650 | 2020 | ✅ |
| SD 888 | 660 | 2021 | ✅ |
| SD 8 Gen 1 | 730 | 2022 | ✅ |
| SD 8 Gen 2 | 740 | 2023 | ✅ |
| SD 8 Gen 3 | 750 | 2024 | ✅ |
| SD 8 Elite | 830 | 2025 | ✅ |

---

### 5.3 Imagination PowerVR — 🟡 Rủi ro trung bình

| # | Vấn đề | GPU bị ảnh hưởng | Ảnh hưởng hệ thống? |
|---|--------|-------------------|:--------------------:|
| 1 | [Danh sách driver bug dài](https://forums.imgtec.com/t/bxm-8-256-long-list-of-driver-issues/3891) — rendering corruption, missing elements | IMG BXM-8-256 | ⚠️ Driver-level |
| 2 | [Vulkan crash sau ~2 phút](https://github.com/hrydgard/ppsspp/issues/18681) | IMG BXM-8-256 (Moto G54) | ⚠️ Workload-dependent |
| 3 | [URP shadows + camera stack = black](https://discussions.unity.com/t/android-urp-powervr-ge8320-unity-2022-3-43f1-issue-on-camera-stack-behave-wrong/1512165) | PowerVR GE8320 | ✅ Không dùng camera stack |
| 4 | [Vulkan "incompatible driver"](https://discussions.unity.com/t/android-vulkan-powervr-gpu-incompatible-driver/875296) | PowerVR (một số device) | ⚠️ Driver-level |

API đủ capability nhưng driver chưa mature. Ít báo cáo liên quan trực tiếp đến StructuredBuffer + instancing.

---

### 5.4 Samsung Xclipse (AMD RDNA 2) — 🟢 Rủi ro thấp

Dựa trên kiến trúc AMD RDNA 2 — mạnh compute + StructuredBuffer. Ít báo cáo lỗi.

| Vấn đề duy nhất | Chi tiết |
|-----------------|----------|
| [Crash sau ~30 phút](https://discussions.unity.com/t/samsung-s22-and-s22-ultra-crashes/892750) trên S22+/S22 Ultra | Thermal throttling → GPU fault. Cần Samsung Adaptive Performance plugin. |

Chỉ có mặt trên Samsung flagship ở **một số thị trường** (còn lại dùng Snapdragon). Thị phần <2%.

---

## 6. Những gì code đã xử lý đúng

Hệ thống đã fix trước **6/6 vấn đề mobile phổ biến nhất** được document trong [ParticleRendering.md §2.10](ParticleRendering.md#210-android-apk-build--6-fixes):

| Mitigation | Chống | Status |
|-----------|-------|:------:|
| `SetData(NativeArray)` thay `LockBufferForWrite` | Mali Valhall cache coherence | ✅ |
| `HLSLPROGRAM` + `Core.hlsl` + URP tags | Shader stripping trên Vulkan | ✅ |
| `Resources.Load<Shader>()` thay `Shader.Find()` | Shader missing trên build | ✅ |
| CBUFFER explicit | Vulkan SPIR-V binding lỗi | ✅ |
| `cmd.SetGlobal*()` thay `material.Set*()` | Vulkan deferred execution race | ✅ |
| Explicit `SetRenderTarget` trong UnsafePass | Vulkan không auto-bind | ✅ |

Các mitigation bổ sung có sẵn trong code:

| Mitigation | Chống | Status |
|-----------|-------|:------:|
| Stride 16 bytes (power-of-2) | GPU cache alignment, layout mismatch | ✅ |
| Guard `count == 0` trước buffer access | Mali null StructuredBuffer crash | ✅ |
| Grow-only buffer | GPU resource re-allocation stutter | ✅ |
| Double buffering | CPU/GPU contention | ✅ |
| Pre-packed colors (`uint` thay `Color`) | Bandwidth, struct alignment | ✅ |

---

## 7. Rủi ro còn lại & vấn đề cần lưu ý

### 7.1 Vertex Shader SSBO trên GLES

**Vấn đề nghiêm trọng:** OpenGL ES 3.1 **không bắt buộc** SSBO trong vertex shader. Nhiều Mali device báo cáo `maxComputeBufferInputsVertex = 0` trên GLES — `StructuredBuffer<ParticleData>` trong vertex shader **thất bại im lặng**.

Unity staff xác nhận:

> *"There's at least one GPU manufacturer that didn't implement reading it from vertex shaders, but only on OpenGL. Their Vulkan driver supports it."*

**→ Vulkan PHẢI là API chính. GLES không phải fallback hợp lệ cho hệ thống này.**

### 7.2 Mali + GLES = không render

`DrawMeshInstancedIndirect` trên Mali + OpenGL ES hoàn toàn không render — không phải giới hạn count, mà là **0 output**. Chỉ Vulkan mới hoạt động.

### 7.3 Mali instance count limit (Vulkan)

Giới hạn ~3,000–3,500 instance trên Mali cũ (G71/G72/G76). Con số chính xác [không thể query qua Vulkan API](https://github.com/KhronosGroup/Vulkan-Docs/issues/2266) — là "undiscoverable limit". Vượt quá → `VK_ERROR_DEVICE_LOST` → toàn bộ rendering dừng.

### 7.4 Unity RenderGraph black screen trên Android

[Khi tắt Compatibility Mode](https://discussions.unity.com/t/unity-6-android-rendering-broken/1548228), nhiều báo cáo màn hình đen trên Android (Unity 6000.0.6f1 → 6000.0.27f1). Nếu Compatibility Mode bật → Unity gọi `Execute()` (legacy, ổn). Nếu tắt → `RecordRenderGraph()` → có thể black screen.

### 7.5 AddUnsafePass và TBDR

Tất cả mobile GPU (Mali, Adreno, PowerVR) dùng kiến trúc **Tile-Based Deferred Rendering (TBDR)**. [`AddUnsafePass` phá vỡ pass merging](https://docs.unity3d.com/6000.4/Documentation/Manual/urp/render-graph-unsafe-pass.html) — buộc GPU load/store tile memory ra main memory giữa các pass. Với 1 pass particle overlay thì ảnh hưởng nhỏ, nhưng cần lưu ý khi thêm nhiều effect.

### 7.6 Google Pixel 6 bị Unity chặn Vulkan

Unity's Vulkan Device Filtering deny Pixel 6 (Tensor G1 / Mali-G78) khỏi Vulkan mặc định.

### 7.7 `CommandBuffer.DrawMeshInstancedIndirect` vẫn supported

Chỉ `Graphics.DrawMeshInstancedIndirect` (static call) bị obsolete trong Unity 6. Code hiện tại dùng `cmd.DrawMeshInstancedIndirect` (CommandBuffer) — [vẫn fully supported](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.CommandBuffer.DrawMeshInstancedIndirect.html). Chưa có `CommandBuffer.RenderMeshIndirect` thay thế.

### 7.8 Unity 6.4+ bỏ Compatibility Mode

RenderGraph sẽ trở thành bắt buộc — legacy `Execute()` path sẽ bị xóa. Code `RecordRenderGraph()` hiện tại là hướng đi đúng.

---

## 8. Struct layout lưu ý

Struct `ParticleRenderData` hiện tại **đã tuân thủ** mọi yêu cầu cross-platform:

```
ParticleRenderData (16 bytes, power-of-2):
  float2 center      (8B)  ← 2 × float4-aligned component
  float  radius      (4B)
  uint   packedColor (4B)
  ─────────────────────────
  Total: 16 bytes ✓ (bội số 16)
```

| Yêu cầu | Status | Ghi chú |
|----------|:------:|---------|
| Stride bội số 4 | ✅ | 16 / 4 = 4 |
| Stride bội số 16 (khuyến nghị) | ✅ | 16 / 16 = 1 |
| Stride < 2048 | ✅ | 16 << 2048 |
| Không dùng `float3` | ✅ | DX tight-pack vs OpenGL std430 pad `float3` → `float4`. Struct dùng `float2` + `float` + `uint` — an toàn |
| `SequentialLayout` | ✅ | Ngăn C# compiler reorder |
| Byte-by-byte match HLSL | ✅ | Đã verify |

**Lưu ý CBUFFER:** Trên Adreno, tổng kích thước tất cả UBO (Uniform Buffer Object) per shader nên dưới **7,372 bytes** (90% của 8K). `ParticleUniforms` CBUFFER hiện tại chứa `float4x4` (64B) + `float2` (8B) + `int` (4B) + `uint` (4B) = **80 bytes** — rất nhỏ, an toàn.

---

## 9. Khuyến nghị hành động

### 9.1 Bắt buộc

| # | Hành động | Lý do |
|---|-----------|-------|
| **1** | **Vulkan là Graphics API chính** (Player Settings → Graphics APIs → Vulkan trước GLES) | Mali + GLES = không render. Vertex SSBO = 0 trên Mali GLES. |
| **2** | **Runtime capability check** khi khởi động (xem [§10](#10-runtime-capability-check)) | ~5–10% thiết bị không đáp ứng → cần disable effect |
| **3** | **Test trên 1 thiết bị Mali tầm trung** (Samsung Galaxy A14/A15 hoặc Redmi Note 12) | Mali = 40–45% thị trường, chưa test |

### 9.2 Nên làm

| # | Hành động | Lý do |
|---|-----------|-------|
| **4** | Giới hạn `maxParticleCount` mặc định ≤ **3,000** | Tránh undiscoverable instance limit trên Mali cũ |
| **5** | Bật **URP Compatibility Mode** nếu chưa bật | RenderGraph black screen bug trên nhiều thiết bị Android |
| **6** | Test trên 1 thiết bị **Adreno 619** (Redmi Note 12 5G, Galaxy A52) | Có crash bug đã xác nhận |

### 9.3 Theo dõi

| # | Hành động | Thời điểm |
|---|-----------|-----------|
| **7** | Migrate `DrawMeshInstancedIndirect` → `RenderMeshIndirect` khi có `CommandBuffer` equivalent | Unity cung cấp API mới |
| **8** | Loại bỏ legacy `Execute()` path | Unity 6.4+ bỏ Compatibility Mode |
| **9** | Cân nhắc [Google VkQuality plugin](https://developer.android.com/games/develop/vulkan/vkquality) | Khi cần selective Vulkan enable/disable per device |

---

## 10. Runtime capability check

```csharp
/// <summary>
/// Checks whether the current device supports the ParticleRendering pipeline.
/// Call once at startup; cache the result.
/// </summary>
public static class ParticleCapability
{
    public static bool IsSupported()
    {
        // Compute shader support (StructuredBuffer + IndirectDraw require this)
        if (!SystemInfo.supportsComputeShaders) return false;

        // Shader Model 4.5 (#pragma target 4.5)
        if (SystemInfo.graphicsShaderLevel < 45) return false;

        // Vertex-stage SSBO (Mali GLES reports 0 → StructuredBuffer in vert silent-fails)
        if (SystemInfo.maxComputeBufferInputsVertex == 0) return false;

        // GPU instancing
        if (!SystemInfo.supportsInstancing) return false;

        return true;
    }
}
```

---

## 11. Tổng kết rủi ro

| Mức độ | Đánh giá |
|--------|----------|
| **Đa số thiết bị phổ thông (>85%)** | ✅ Hoạt động tốt — ES 3.1+, Vulkan driver đủ tốt |
| **Mali cũ G71/G72 (5–8%)** | ⚠️ Có thể lỗi ở count cao (>3,000). GLES = không render |
| **Adreno 619 (3–5%)** | ⚠️ Crash khả năng có — Unity engine bug |
| **GPU quá cũ ES 3.0 only (<5%)** | ❌ Không hỗ trợ — cần runtime check |

```
Estimated coverage matrix:

                    Vulkan        GLES 3.1      GLES 3.0 only
                  ┌─────────┐   ┌──────────┐   ┌──────────────┐
  Adreno 530+     │  ✅ OK  │   │  ✅ OK   │   │              │
  Adreno cũ       │  ⚠️     │   │  ⚠️      │   │   ❌ No      │
  Mali G76+       │  ✅ OK  │   │  ❌ No*  │   │              │
  Mali cũ         │  ⚠️ ≤3K │   │  ❌ No   │   │   ❌ No      │
  PowerVR BXM+    │  ✅ OK  │   │  ⚠️      │   │              │
  Xclipse         │  ✅ OK  │   │  ─       │   │              │
                  └─────────┘   └──────────┘   └──────────────┘

  * Mali GLES: DrawMeshInstancedIndirect không render / vertex SSBO = 0
```

---

## 12. Project tham khảo

[ColinLeung-NiloCat/UnityURP-MobileDrawMeshInstancedIndirectExample](https://github.com/ColinLeung-NiloCat/UnityURP-MobileDrawMeshInstancedIndirectExample) — vẽ **10 triệu cỏ** trên mobile bằng `DrawMeshInstancedIndirect` + compute culling. Test kết quả:

| Thiết bị | GPU | Kết quả |
|----------|-----|---------|
| Lenovo S5 | Adreno 506 | 10M grass, 30 FPS |
| Galaxy A70 | Adreno 612 | 10M grass, 50–60 FPS |

Xác nhận kỹ thuật `DrawMeshInstancedIndirect` + `StructuredBuffer` hoạt động trên production mobile nếu triển khai đúng.

---

## 13. Nguồn tham khảo

- [Unity Issue Tracker — Mali instance limit](https://issuetracker.unity3d.com/issues/vulkan-android-mali-meshes-stop-being-rendered-using-drawmeshinstanceindirect-when-they-exceed-certain-amount-on-mali-gpus)
- [Unity Issue Tracker — Mali StructuredBuffer null crash](https://issuetracker.unity3d.com/issues/android-vulkan-crashes-mali-device-when-accessing-null-item-in-structuredbuffer-in-shader)
- [Unity Issue Tracker — Adreno 619 crash](https://discussions.unity.com/t/in-72861-app-crashes-on-some-android-devices-which-has-adreno-619-gpu/943646)
- [Unity Issue Tracker — Adreno 610/612 rendering issues](https://discussions.unity.com/t/rendering-issues-on-adreno-610-and-612/821057)
- [Unity Forum — Mali GLES DrawMeshInstancedIndirect failure](https://discussions.unity.com/t/drawmeshinstancedindirect-not-working-on-specific-gpus-mali-2019-4-36f1/872993)
- [Unity Forum — RenderGraph Android black screen](https://discussions.unity.com/t/unity-6-android-rendering-broken/1548228)
- [Unity Forum — Mali compute shader freeze](https://discussions.unity.com/t/rendering-stops-freezes-on-some-mobile-gpus-using-vulkan-compute-shaders/945225)
- [Unity Docs — RenderGraph unsafe pass optimization](https://docs.unity3d.com/6000.4/Documentation/Manual/urp/render-graph-unsafe-pass.html)
- [Unity Docs — CommandBuffer.DrawMeshInstancedIndirect](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.CommandBuffer.DrawMeshInstancedIndirect.html)
- [Unity Docs — ES 3.1 minimum in Unity 6.6](https://discussions.unity.com/t/increasing-the-opengles-minimum-spec-in-unity-6-6/1719163)
- [Khronos — Undiscoverable Mali instance limit](https://github.com/KhronosGroup/Vulkan-Docs/issues/2266)
- [Imagination Forums — BXM-8-256 driver bugs](https://forums.imgtec.com/t/bxm-8-256-long-list-of-driver-issues/3891)
- [GitHub — PPSSPP BXM-8-256 Vulkan crash](https://github.com/hrydgard/ppsspp/issues/18681)
- [GitHub — MobileDrawMeshInstancedIndirectExample](https://github.com/ColinLeung-NiloCat/UnityURP-MobileDrawMeshInstancedIndirectExample)
- [Android Distribution Dashboard](https://developer.android.com/about/dashboards)
- [Google VkQuality plugin](https://developer.android.com/games/develop/vulkan/vkquality)
- Counterpoint Research Q4 2024
