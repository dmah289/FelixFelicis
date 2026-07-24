# SpringDamper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Plan này là artifact học tập + triển khai, tự chứa.** Người dùng tự code lại để rèn tư duy → §0 dẫn giải **đầy đủ toán học** (biến đổi + lý do dùng công thức), các task chứa **code hoàn chỉnh** kèm chú thích self-doc & lý do tối ưu. Không code test (theo lựa chọn ở spec §4) → mỗi task có **checklist kiểm chứng** (kiểm mốc toán học).

**Goal:** `SpringDamper` — bộ giải lò xo-giảm chấn framerate-independent (analytic + semi-implicit Euler) cho float/Vector2/Vector3, dạng struct state + static solver zero-GC.

**Architecture:** 5 file trong `Spring/`. Lõi toán scalar viết **một lần** trong `SpringSolver`; `FloatSpring` bọc state 1 chiều; `Vector2/3Spring` gọi lõi scalar **per-axis** (không lặp toán). Tham số hóa `frequency + dampingRatio` + converter physical; precompute hệ số không-phụ-thuộc-dt trong `SpringConfig`.

**Tech Stack:** C# (Unity), `Unity.Mathematics` (`math.exp/sin/cos/cosh/sinh`), `UnityEngine.Mathf` cho setup. Thuần toán — không Addressables/UniTask.

## Global Constraints

| Ràng buộc | Giá trị |
|---|---|
| Namespace | `Horcrux.Runtime.Utilities.PhysXHelper` (mọi file) |
| Zero-GC hot path | toàn `struct`; `Solve` nhận `ref`/`in`; không `new` reference-type, LINQ, closure, string |
| SOLID | 1 file = 1 trách nhiệm; toán viết 1 lần (SpringSolver); mở rộng qua `enum`, không sửa struct |
| Self-doc | tên nói rõ mục đích; XML doc kèm công thức + "tại sao" ở API public |
| Precompute | hệ số nặng phần không phụ thuộc dt (`ω₀, ζω₀, ω_d, mode`) tính 1 lần trong `SpringConfig` |
| ODE chuẩn | `ÿ + 2ζω₀·ẏ + ω₀²·y = 0`, `y = x − target`, `ω₀ = 2π·f` |
| Guard biên | `dt ≤ 0` → no-op; `f ≤ 0` → tắt; `ζ < 0` → clamp 0; `|ζ−1| ≤ 1e-4` → critical |
| Spec nguồn | `docs/superpowers/specs/2026-07-24-springdamper-design.md` |

---

## §0. Nền toán học (đọc trước khi code)

### 0.1. Vật lý → ODE

Vật khối lượng `m` gắn lò xo + giảm chấn, kéo về `target`. Định luật II Newton `mẍ = ΣF`:

| Lực | Biểu thức | Dấu — vì sao |
|---|---|---|
| Lò xo (Hooke) | `−k(x − target)` | tỉ lệ độ lệch, luôn kéo **về** target → `−` |
| Cản nhớt (damping) | `−c·ẋ` | tỉ lệ vận tốc, cản **ngược** hướng đi → `−` |

$$m\ddot{x} = -k(x - target) - c\dot{x}$$

### 0.2. Chuẩn hóa: 3 tham số vật lý → 2 tham số trực quan

Chia `m`, đổi biến `y = x − target` (target hằng ⇒ `ẏ = ẋ`, `ÿ = ẍ`):

$$\ddot{y} + \tfrac{c}{m}\dot{y} + \tfrac{k}{m}y = 0$$

Đặt 2 đại lượng gộp `m,k,c` thành 2 số **có ý nghĩa cảm nhận**:

| Ký hiệu | Định nghĩa | Ý nghĩa | Suy ra |
|---|---|---|---|
| `ω₀` | `√(k/m)` | tần số tự nhiên; designer nhập `f` (Hz) → `ω₀ = 2π·f` | `k/m = ω₀²` |
| `ζ` | `c / (2√(km))` | damping ratio (không thứ nguyên) — quyết định "kiểu" chuyển động | `c/m = 2ζω₀` |

$$\boxed{\;\ddot{y} + 2\zeta\omega_0\,\dot{y} + \omega_0^2\,y = 0\;}\qquad\text{(dạng chuẩn)}$$

→ **Lý do chọn `frequency + dampingRatio`:** tune 2 số ý nghĩa thay vì 3 số vật lý thô. Converter đảo ngược ở Task 1.

### 0.3. Giải ODE → phương trình đặc trưng → 3 chế độ

Thử `y = e^{rt}` (`ẏ = re^{rt}`, `ÿ = r²e^{rt}`), chia `e^{rt} ≠ 0`:

$$r^2 + 2\zeta\omega_0 r + \omega_0^2 = 0 \;\Rightarrow\; r = -\zeta\omega_0 \pm \omega_0\sqrt{\zeta^2 - 1}$$

Dấu `ζ² − 1` (tức `ζ` so 1) chẻ ra 3 chế độ:

| Chế độ | ĐK | Nghiệm `r` | Đại lượng phụ | Hành vi |
|---|---|---|---|---|
| **Under-damped** | `ζ<1` | phức `−ζω₀ ± iω_d` | `ω_d = ω₀√(1−ζ²)` | nảy quanh target, tắt dần |
| **Critically** | `ζ=1` | kép `−ω₀` | — | tới đích **nhanh nhất, KHÔNG nảy** |
| **Over-damped** | `ζ>1` | 2 thực âm | `s = ω₀√(ζ²−1)` | bò về đích, ì |

### 0.4. Nghiệm đóng từng chế độ (lõi solver Analytic)

**Mục tiêu:** từ `(y₀, v₀)` đầu bước → `(y, v)` sau `Δt`. Quy trình mỗi chế độ: **nghiệm tổng quát → ghim `A,B` bằng ĐK đầu → đạo hàm ra `v` → kiểm mốc.**

#### ● Under-damped (`ζ<1`)

Nghiệm phức → bao hình phân rã × dao động:
$$y = e^{-\zeta\omega_0 t}\big[A\cos(\omega_d t) + B\sin(\omega_d t)\big]$$

Ghim hằng số bằng `(y₀, v₀)`:

| Điều kiện | Cho ra |
|---|---|
| `t=0`: `y=y₀` | `A = y₀` |
| `v = ẏ` tại `t=0`: `v₀ = −ζω₀A + ω_d B` | `B = (v₀ + ζω₀y₀)/ω_d` |

Đạo hàm `ẏ` (quy tắc tích: `(e^{-ζω₀t}f)' = e^{-ζω₀t}(f' − ζω₀f)`):

$$\boxed{\;y = E\,(y_0 C + B S)\;}\qquad \boxed{\;v = E\big[(-\zeta\omega_0 y_0 + B\omega_d)C - (\zeta\omega_0 B + y_0\omega_d)S\big]\;}$$
với `E = e^{−ζω₀Δt}`, `C = cos(ω_dΔt)`, `S = sin(ω_dΔt)`, `B = (v₀+ζω₀y₀)/ω_d`.

Kiểm mốc: `Δt=0` → `E=C=1, S=0` → `y=y₀`, `v=−ζω₀y₀+Bω_d=v₀` ✓ · `Δt→∞` → `E→0` → `(0,0)` tức `x→target` ✓

#### ● Over-damped (`ζ>1`)

Cùng khuôn under nhưng **cos→cosh, sin→sinh, ω_d→s** (nghiệm thực). Dùng dạng hyperbolic thay vì tổng 2 mũ rời để **ổn định số** hơn:
$$y = e^{-\zeta\omega_0 t}\big[y_0\cosh(st) + B\sinh(st)\big],\quad B = \tfrac{v_0+\zeta\omega_0 y_0}{s}$$

Đạo hàm (`cosh'=s·sinh`, `sinh'=s·cosh`):
$$\boxed{\;y = E\,(y_0 Ch + B\,Sh)\;}\qquad \boxed{\;v = E\big[(-\zeta\omega_0 y_0 + Bs)Ch + (y_0 s - \zeta\omega_0 B)Sh\big]\;}$$
với `E = e^{−ζω₀Δt}`, `Ch = cosh(sΔt)`, `Sh = sinh(sΔt)`.

Kiểm mốc `Δt=0`: `Ch=1, Sh=0` → `y=y₀`, `v=−ζω₀y₀+Bs=v₀` ✓
> **Vì sao không nổ dù `cosh/sinh` tăng theo `e^{sΔt}`:** luôn `ζω₀ > s` (do `ζ > √(ζ²−1)`), nên bao hình `E=e^{−ζω₀Δt}` **thắng** → tích vẫn phân rã. ✓

#### ● Critically damped (`ζ=1`)

Nghiệm kép `r=−ω₀` → dạng `(A + Bt)e^{−ω₀t}`:

| Điều kiện | Cho ra |
|---|---|
| `t=0`: `y=y₀` | `A = y₀` |
| `v₀ = B − ω₀A` | `B = v₀ + ω₀y₀` |

$$\boxed{\;y = E\,[\,y_0 + (v_0+\omega_0 y_0)\,\Delta t\,]\;}\qquad \boxed{\;v = E\,[\,v_0 - \omega_0(v_0+\omega_0 y_0)\,\Delta t\,]\;}$$
với `E = e^{−ω₀Δt}`. Kiểm mốc `Δt=0`: `y=y₀, v=v₀` ✓

> **Vì sao Analytic ổn định vô điều kiện:** mọi số hạng nhân bao hình `e^{−ζω₀Δt}` (giảm khi `ζ>0`), **không có phép lặp tích lũy sai số** → không bao giờ nổ dù `Δt` lớn. Cùng bản chất `Interpolator.ExpDecay` (mũ cộng số mũ → độc lập cách chia thời gian).

### 0.5. Semi-implicit Euler (đối chiếu, rẻ)

Rời rạc ODE gốc, **velocity trước, position sau**:
$$a = -\omega_0^2\,y - 2\zeta\omega_0\,v \;\to\; v \mathrel{+}= a\,\Delta t \;\to\; y \mathrel{+}= v\,\Delta t$$

| Điểm | Nội dung |
|---|---|
| Vì sao "velocity-first" | `y` mới dùng `v` **đã cập nhật** → thêm tính ẩn → ổn định hơn explicit Euler nhiều |
| Ngưỡng nổ | phân kỳ khi `ω₀·Δt` lớn (bước thô so chu kỳ). An toàn khi `ω₀·Δt` ≲ vài phần mười |
| Khi nào tránh | `dt` dao động mạnh (lag spike) → dùng Analytic |

### 0.6. Mở rộng vector

ODE chuẩn **tuyến tính, không ghép chéo trục** (`x,y,z` không xuất hiện chung số hạng) → mỗi trục là bài scalar **độc lập**, cùng `ω₀,ζ`. Vector = chạy lõi scalar per-axis. **Không toán mới.**

---

## Bản đồ triển khai

```
Spring/
├── SpringConfig.cs   Task 1  tham số + precompute + converter        (0.2)
├── SpringSolver.cs   Task 2  lõi toán scalar analytic + euler        (0.4, 0.5)
├── FloatSpring.cs    Task 3  state 1 chiều + API Solve
├── Vector2Spring.cs  Task 4  per-axis                                 (0.6)
└── Vector3Spring.cs  Task 4  per-axis                                 (0.6)
```
Thứ tự: **1 → 2 → 3/4** (2 cần 1; 3 và 4 đều cần 2, độc lập nhau).

---

### Task 1: `SpringConfig` — tham số + precompute + converter

**Files:**
- Create: `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/SpringConfig.cs`

**Interfaces:**
- Consumes: —
- Produces:
  - `enum SpringMode { UnderDamped, CriticallyDamped, OverDamped }`
  - `readonly struct SpringConfig` — field `float Omega0, Zeta, ZetaOmega, OmegaD; SpringMode Mode; bool IsActive`
  - `static SpringConfig FromFrequency(float frequency, float dampingRatio)`
  - `static SpringConfig FromPhysical(float stiffness, float damping, float mass = 1f)`
  - `(float stiffness, float damping) ToPhysical(float mass = 1f)`

**Bản đồ toán → code:** §0.2 (chuẩn hóa `k/m=ω₀²`, `c/m=2ζω₀`) · §0.3 (chọn mode + `ω_d`/`s`).

**Self-doc & tối ưu:**

| Quyết định | Lý do |
|---|---|
| `readonly struct` | immutable, zero-GC, không đổi sau khi tạo |
| precompute `ZetaOmega, OmegaD, Mode` trong factory | `Sqrt`/nhân tính **1 lần**, không lặp mỗi frame trong Solve |
| `IsActive` cờ sẵn | Solve chỉ check `bool`, không so `frequency` lại |
| `Mode` enum sẵn | Solve `switch` thẳng, không so `zeta` mỗi frame |
| `CriticalEpsilon` | tránh chia 0 ở `ω_d`/`s` khi `ζ≈1` |

- [ ] **Step 1: Tạo file với đầy đủ code**

```csharp
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    public enum SpringMode { UnderDamped, CriticallyDamped, OverDamped }

    /// <summary>
    /// Tham số lò xo dạng chuẩn (frequency + dampingRatio) + hệ số precompute không phụ thuộc dt.
    /// Immutable: tạo qua factory, hệ số nặng (Sqrt) tính 1 lần.
    /// </summary>
    /// <remarks>
    /// ODE chuẩn: ÿ + 2ζω₀·ẏ + ω₀²·y = 0, với y = x − target.
    /// ω₀ = 2π·f (tần số tự nhiên), ζ = damping ratio (không thứ nguyên).
    /// </remarks>
    public readonly struct SpringConfig
    {
        public readonly float Omega0;     // ω₀ = 2π·f
        public readonly float Zeta;       // ζ  (đã clamp ≥ 0)
        public readonly float ZetaOmega;  // ζω₀  — precompute cho bao hình e^(−ζω₀·dt)
        public readonly float OmegaD;     // under: ω₀√(1−ζ²) | over: s=ω₀√(ζ²−1) | critical: 0
        public readonly SpringMode Mode;
        public readonly bool IsActive;    // false khi frequency ≤ 0 → Solve thành no-op

        private const float CriticalEpsilon = 1e-4f; // dải coi ζ = 1 (tránh chia 0 ở ω_d/s)

        private SpringConfig(float omega0, float zeta, float zetaOmega,
            float omegaD, SpringMode mode, bool isActive)
        {
            Omega0 = omega0; Zeta = zeta; ZetaOmega = zetaOmega;
            OmegaD = omegaD; Mode = mode; IsActive = isActive;
        }

        /// <summary>API chính (designer-friendly).</summary>
        /// <param name="frequency">Tần số f (Hz): cảm giác "độ cứng"/tốc độ dao động. ≤ 0 → lò xo tắt.</param>
        /// <param name="dampingRatio">ζ: &lt;1 nảy, =1 tới đích nhanh nhất không nảy, &gt;1 ì. Âm → clamp 0.</param>
        public static SpringConfig FromFrequency(float frequency, float dampingRatio)
        {
            if (frequency <= 0f) // lò xo tắt: không hội tụ, đánh dấu no-op
                return new SpringConfig(0f, 0f, 0f, 0f, SpringMode.CriticallyDamped, false);

            float omega0 = 2f * Mathf.PI * frequency;        // ω₀ = 2π·f  (§0.2)
            float zeta = dampingRatio < 0f ? 0f : dampingRatio; // ζ<0 = bơm năng lượng → nổ → clamp
            float zetaOmega = zeta * omega0;

            SpringMode mode;
            float omegaD;
            if (zeta < 1f - CriticalEpsilon)                 // under-damped (§0.3)
            {
                mode = SpringMode.UnderDamped;
                omegaD = omega0 * Mathf.Sqrt(1f - zeta * zeta);   // ω_d = ω₀√(1−ζ²)
            }
            else if (zeta > 1f + CriticalEpsilon)            // over-damped
            {
                mode = SpringMode.OverDamped;
                omegaD = omega0 * Mathf.Sqrt(zeta * zeta - 1f);   // s = ω₀√(ζ²−1)
            }
            else                                             // critical (ζ ≈ 1)
            {
                mode = SpringMode.CriticallyDamped;
                omegaD = 0f;
            }
            return new SpringConfig(omega0, zeta, zetaOmega, omegaD, mode, true);
        }

        /// <summary>Converter: tham số vật lý thô (k, c, m) → dạng chuẩn (§0.2 đảo ngược).</summary>
        public static SpringConfig FromPhysical(float stiffness, float damping, float mass = 1f)
        {
            // ω₀ = √(k/m); ζ = c / (2√(km)); f = ω₀ / 2π
            float omega0 = Mathf.Sqrt(stiffness / mass);
            float frequency = omega0 / (2f * Mathf.PI);
            float denom = 2f * Mathf.Sqrt(stiffness * mass);
            float zeta = denom > 0f ? damping / denom : 0f;
            return FromFrequency(frequency, zeta);
        }

        /// <summary>Converter ngược: dạng chuẩn → (k, c) để tra cứu/so sánh. k=ω₀²m, c=2ζω₀m.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (float stiffness, float damping) ToPhysical(float mass = 1f)
        {
            float k = Omega0 * Omega0 * mass;
            float c = 2f * Zeta * Omega0 * mass;
            return (k, c);
        }
    }
}
```

- [ ] **Step 2: Kiểm chứng (chạy tay / nhẩm)**

| Input | Kỳ vọng |
|---|---|
| `FromFrequency(2, 0.5)` | `Mode=UnderDamped`, `Omega0≈12.566`, `OmegaD≈10.88` (`=ω₀√0.75`) |
| `FromFrequency(2, 1)` | `Mode=CriticallyDamped`, `OmegaD=0` |
| `FromFrequency(2, 2)` | `Mode=OverDamped`, `OmegaD≈21.77` (`=ω₀√3`) |
| `FromFrequency(0, 0.5)` | `IsActive=false` |
| `FromFrequency(2, -1)` | `Zeta=0` |
| `FromPhysical(k,c).ToPhysical()` | round-trip ≈ `(k,c)` (mass=1) |

  Unity biên dịch sạch (Console không lỗi).

- [ ] **Step 3: Commit**

```bash
git add Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/SpringConfig.cs
git commit -m "feat(spring): SpringConfig - tham so + precompute + converter"
```

---

### Task 2: `SpringSolver` — lõi toán scalar (analytic + euler)

**Files:**
- Create: `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/SpringSolver.cs`

**Interfaces:**
- Consumes: `SpringConfig` (field `IsActive, Omega0, Zeta, ZetaOmega, OmegaD, Mode`) — Task 1.
- Produces:
  - `enum SpringMethod { Analytic, SemiImplicit }`
  - `static (float pos, float vel) Solve(float pos, float vel, float target, in SpringConfig cfg, SpringMethod method, float dt)`
  - `internal static (float, float) SolveAnalytic(float y0, float v0, in SpringConfig cfg, float dt)`
  - `internal static (float, float) SolveSemiImplicit(float y, float v, float omega0, float zeta, float dt)`

**Bản đồ toán → code:** `SolveAnalytic` = 3 hộp công thức §0.4 · `SolveSemiImplicit` = §0.5 · đổi biến `y=pos−target` rồi ngược lại = §0.2.

**Self-doc & tối ưu:**

| Quyết định | Lý do |
|---|---|
| `in SpringConfig` | readonly by-ref, **không copy** struct mỗi call |
| trả tuple `(float,float)` | value-type trên stack, zero-GC, không `out` rườm rà |
| tách `SolveAnalytic`/`SolveSemiImplicit` `internal` | test/đọc từng nhánh; đặt tên nói rõ phương pháp |
| guard `!IsActive \|\| dt≤0` ngay đầu | thoát sớm, không tính `exp` vô ích |
| `math.exp/sin/cos/...` (Unity.Mathematics) | thuần `float`, Burst-friendly, không cast `double` |
| tính `E` (bao hình) 1 lần, dùng chung | không gọi `exp` lặp trong nhánh |

- [ ] **Step 1: Tạo file với đầy đủ code**

```csharp
using Unity.Mathematics;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    public enum SpringMethod { Analytic, SemiImplicit }

    /// <summary>
    /// Lõi giải lò xo 1 chiều (scalar). Vector2/3 gọi vào đây theo từng trục (§0.6).
    /// </summary>
    public static class SpringSolver
    {
        /// <summary>Tiến state lò xo một bước dt. Trả (position, velocity) mới.</summary>
        /// <remarks>Giải ÿ + 2ζω₀·ẏ + ω₀²·y = 0 với y = pos − target. Xem §0.4–0.5.</remarks>
        public static (float pos, float vel) Solve(
            float pos, float vel, float target,
            in SpringConfig cfg, SpringMethod method, float dt)
        {
            if (!cfg.IsActive || dt <= 0f) return (pos, vel); // guard: lò xo tắt / bước rỗng

            float y = pos - target; // đổi biến về khoảng cách còn lại (§0.2)

            (float ny, float nv) = method == SpringMethod.Analytic
                ? SolveAnalytic(y, vel, cfg, dt)
                : SolveSemiImplicit(y, vel, cfg.Omega0, cfg.Zeta, dt);

            return (ny + target, nv); // đổi biến ngược: x = y + target
        }

        /// <summary>Nghiệm giải tích — ổn định vô điều kiện với mọi dt (§0.4).</summary>
        internal static (float, float) SolveAnalytic(float y0, float v0, in SpringConfig cfg, float dt)
        {
            float zw = cfg.ZetaOmega;
            float e = math.exp(-zw * dt); // bao hình chung E = e^(−ζω₀·dt)

            switch (cfg.Mode)
            {
                case SpringMode.UnderDamped: // §0.4 ●under
                {
                    float wd = cfg.OmegaD;                 // ω_d
                    float c = math.cos(wd * dt);
                    float s = math.sin(wd * dt);
                    float b = (v0 + zw * y0) / wd;         // B = (v₀+ζω₀y₀)/ω_d

                    float y = e * (y0 * c + b * s);
                    float v = e * ((-zw * y0 + b * wd) * c - (zw * b + y0 * wd) * s);
                    return (y, v);
                }
                case SpringMode.OverDamped: // §0.4 ●over
                {
                    float sc = cfg.OmegaD;                 // s = ω₀√(ζ²−1)
                    float ch = math.cosh(sc * dt);
                    float sh = math.sinh(sc * dt);
                    float b = (v0 + zw * y0) / sc;         // B = (v₀+ζω₀y₀)/s

                    float y = e * (y0 * ch + b * sh);
                    float v = e * ((-zw * y0 + b * sc) * ch + (y0 * sc - zw * b) * sh);
                    return (y, v);
                }
                default: // CriticallyDamped — §0.4 ●critical
                {
                    float w0 = cfg.Omega0;
                    float coeff = v0 + w0 * y0;            // B = v₀+ω₀y₀
                    float y = e * (y0 + coeff * dt);
                    float v = e * (v0 - w0 * coeff * dt);
                    return (y, v);
                }
            }
        }

        /// <summary>Semi-implicit Euler — rẻ, có thể nổ khi ω₀·dt lớn (§0.5).</summary>
        internal static (float, float) SolveSemiImplicit(float y, float v, float omega0, float zeta, float dt)
        {
            float a = -(omega0 * omega0) * y - 2f * zeta * omega0 * v; // gia tốc a = −ω₀²y − 2ζω₀v
            v += a * dt;   // velocity trước
            y += v * dt;   // position sau (dùng v đã cập nhật)
            return (y, v);
        }
    }
}
```

- [ ] **Step 2: Kiểm chứng — kiểm mốc `dt→0` (bất biến quan trọng nhất)**

  Mọi mode, `SolveAnalytic(y0, v0, cfg, dt→0)` phải trả `≈ (y0, v0)`:

| Mode | Tại `dt=0` | Kết quả |
|---|---|---|
| Under | `e=1,c=1,s=0` | `y=y0`; `v=−zw·y0+b·wd = −zw·y0+(v0+zw·y0)=v0` ✓ |
| Over | `ch=1,sh=0` | `y=y0`; `v=−zw·y0+b·sc=v0` ✓ |
| Critical | `e=1` | `y=y0`, `v=v0` ✓ |

- [ ] **Step 3: Kiểm chứng — hội tụ & hành vi 3 mode**

  Vòng `for` ~5s, `dt=1/60`, `target=10`, `pos0=0`, `vel0=0`:

| ζ | Kỳ vọng | Tiêu chí |
|---|---|---|
| bất kỳ | `pos→10`, `vel→0` | #2 hội tụ |
| `1` | `pos` **không vượt** 10 (đơn điệu) | #3 critical |
| `0.3` | `pos` **vượt** 10 ≥1 lần, nảy giảm dần | #4 under |
| `2` | về 10 chậm hơn critical, không vượt | #5 over |

- [ ] **Step 4: Kiểm chứng — độc lập framerate (then chốt của Analytic)**

  Cùng `(pos0, vel0)`, chạy Analytic tới cùng `T=1s`:
  - A: 1 bước `dt=1` · B: 100 bước `dt=0.01`
  - Hai `pos` **trùng ~1e-4**. (Euler thì lệch rõ → đúng đặc tính.)

- [ ] **Step 5: Commit**

```bash
git add Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/SpringSolver.cs
git commit -m "feat(spring): SpringSolver - loi toan scalar analytic + euler"
```

---

### Task 3: `FloatSpring` — state 1 chiều + API người dùng

**Files:**
- Create: `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/FloatSpring.cs`

**Interfaces:**
- Consumes: `SpringSolver.Solve(float, float, float, in SpringConfig, SpringMethod, float)` — Task 2; `SpringConfig`, `SpringMethod`.
- Produces:
  - `struct FloatSpringState { float Position; float Velocity; ctor(float position, float velocity = 0f) }`
  - `static void Solve(ref FloatSpringState s, float target, in SpringConfig cfg, float dt, SpringMethod method = SpringMethod.Analytic)`

**Self-doc & tối ưu:**

| Quyết định | Lý do |
|---|---|
| `struct` state | zero-GC, copy value an toàn, dễ pool/nhét vào array |
| `Solve(ref ...)` | cập nhật **in-place**, không trả struct mới (khỏi copy) |
| `method = Analytic` mặc định | chất lượng trước; muốn rẻ thì opt-in `SemiImplicit` |
| `AggressiveInlining` | wrapper mỏng → nội tuyến khỏi phí gọi hàm |

- [ ] **Step 1: Tạo file với đầy đủ code**

```csharp
using System.Runtime.CompilerServices;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    /// <summary>State lò xo 1 chiều. Value-type: zero-GC, copy an toàn, dễ pool.</summary>
    public struct FloatSpringState
    {
        public float Position;
        public float Velocity;

        public FloatSpringState(float position, float velocity = 0f)
        {
            Position = position;
            Velocity = velocity;
        }
    }

    public static class FloatSpring
    {
        /// <summary>Tiến lò xo về target một bước dt (in-place). Mặc định Analytic (chất lượng).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Solve(ref FloatSpringState s, float target,
            in SpringConfig cfg, float dt, SpringMethod method = SpringMethod.Analytic)
        {
            (s.Position, s.Velocity) = SpringSolver.Solve(s.Position, s.Velocity, target, cfg, method, dt);
        }
    }
}
```

- [ ] **Step 2: Kiểm chứng — smoke test ref-update**

```csharp
var s = new FloatSpringState(0f);
var cfg = SpringConfig.FromFrequency(3f, 0.5f);
for (int i = 0; i < 300; i++) FloatSpring.Solve(ref s, 5f, cfg, 1f / 60f);
// Kỳ vọng: s.Position ≈ 5, s.Velocity ≈ 0 (cập nhật in-place qua ref).
```
  - `dt=0` → state không đổi (guard xuyên suốt từ SpringSolver).

- [ ] **Step 3: Commit**

```bash
git add Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/FloatSpring.cs
git commit -m "feat(spring): FloatSpring - state 1 chieu + API Solve"
```

---

### Task 4: `Vector2Spring` + `Vector3Spring` — per-axis, tái dùng lõi

**Files:**
- Create: `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/Vector2Spring.cs`
- Create: `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/Vector3Spring.cs`

**Interfaces:**
- Consumes: `SpringSolver.Solve(float, float, float, in SpringConfig, SpringMethod, float)` — Task 2; `SpringConfig`, `SpringMethod`.
- Produces:
  - `struct Vector2SpringState { Vector2 Position; Vector2 Velocity; ctor(Vector2, Vector2 = default) }`
  - `static void Vector2Spring.Solve(ref Vector2SpringState, Vector2 target, in SpringConfig, float dt, SpringMethod = Analytic)`
  - `struct Vector3SpringState { Vector3 Position; Vector3 Velocity; ctor(Vector3, Vector3 = default) }`
  - `static void Vector3Spring.Solve(ref Vector3SpringState, Vector3 target, in SpringConfig, float dt, SpringMethod = Analytic)`

**Bản đồ toán → code:** §0.6 — mỗi trục độc lập, cùng `cfg`. **Không toán mới, chỉ gọi lõi scalar per-axis.**

**Self-doc & tối ưu:**

| Quyết định | Lý do |
|---|---|
| gọi `SpringSolver.Solve` mỗi trục | toán viết 1 lần (DRY); sửa lõi → mọi kiểu hưởng |
| `new Vector3(...)` cuối | Vector3 là **struct** → nằm stack, không GC |
| `Velocity = default` ctor | mặc định đứng yên, gọn |

- [ ] **Step 1: Tạo `Vector3Spring.cs`**

```csharp
using UnityEngine;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    public struct Vector3SpringState
    {
        public Vector3 Position;
        public Vector3 Velocity;

        public Vector3SpringState(Vector3 position, Vector3 velocity = default)
        {
            Position = position;
            Velocity = velocity;
        }
    }

    public static class Vector3Spring
    {
        /// <summary>Lò xo 3 trục độc lập, cùng config. Tái dùng lõi scalar — không lặp toán (§0.6).</summary>
        public static void Solve(ref Vector3SpringState s, Vector3 target,
            in SpringConfig cfg, float dt, SpringMethod method = SpringMethod.Analytic)
        {
            var (px, vx) = SpringSolver.Solve(s.Position.x, s.Velocity.x, target.x, cfg, method, dt);
            var (py, vy) = SpringSolver.Solve(s.Position.y, s.Velocity.y, target.y, cfg, method, dt);
            var (pz, vz) = SpringSolver.Solve(s.Position.z, s.Velocity.z, target.z, cfg, method, dt);
            s.Position = new Vector3(px, py, pz);
            s.Velocity = new Vector3(vx, vy, vz);
        }
    }
}
```

- [ ] **Step 2: Tạo `Vector2Spring.cs`**

```csharp
using UnityEngine;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    public struct Vector2SpringState
    {
        public Vector2 Position;
        public Vector2 Velocity;

        public Vector2SpringState(Vector2 position, Vector2 velocity = default)
        {
            Position = position;
            Velocity = velocity;
        }
    }

    public static class Vector2Spring
    {
        /// <summary>Lò xo 2 trục độc lập, cùng config. Tái dùng lõi scalar — không lặp toán (§0.6).</summary>
        public static void Solve(ref Vector2SpringState s, Vector2 target,
            in SpringConfig cfg, float dt, SpringMethod method = SpringMethod.Analytic)
        {
            var (px, vx) = SpringSolver.Solve(s.Position.x, s.Velocity.x, target.x, cfg, method, dt);
            var (py, vy) = SpringSolver.Solve(s.Position.y, s.Velocity.y, target.y, cfg, method, dt);
            s.Position = new Vector2(px, py);
            s.Velocity = new Vector2(vx, vy);
        }
    }
}
```

- [ ] **Step 3: Kiểm chứng — độc lập trục & khớp scalar**

  - `Vector3` target `(5, −3, 10)`, ~5s: mỗi trục hội tụ đúng thành phần, `Velocity→0`.
  - Chạy 1 trục Vector3 với cùng `(pos, vel, target, cfg, dt)` như `SpringSolver.Solve` scalar → **trùng khít** (chứng minh chỉ per-axis, không toán riêng).
  - `Vector2` tương tự `(5, −3)`.

- [ ] **Step 4: Commit**

```bash
git add Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/Vector2Spring.cs Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/Vector3Spring.cs
git commit -m "feat(spring): Vector2/Vector3 spring - per-axis tai dung loi"
```

---

## Ghi chú thực thi

- **Thứ tự:** 1 → 2 → 3/4 (3 và 4 độc lập, làm song song được sau khi 2 xong).
- **File `.meta`:** Unity tự sinh khi import — commit kèm nếu repo giữ GUID ổn định.
- **Kiểm chứng:** chạy tay qua script tạm hoặc nhẩm theo kiểm mốc — **không** tạo file test (spec §4). Xóa script tạm trước khi commit.
- **Muốn test tự động sau này:** dựng NUnit EditMode theo bảng tiêu chí §4 của spec — ngoài phạm vi plan này.
```
