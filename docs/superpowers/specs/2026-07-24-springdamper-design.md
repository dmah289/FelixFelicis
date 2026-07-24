# SpringDamper — Thiết kế & nền toán học

> **Mục tiêu tài liệu:** dẫn giải **đầy đủ bản chất toán học** (mỗi biến đổi có "tại sao", kiểm mốc biên, không nhảy bước) + **code tham chiếu hoàn chỉnh** từng phần. Người đọc dùng để hiểu sâu rồi tự code lại.
>
> **Ngày:** 2026-07-24 · **Vị trí code:** `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/` · **Namespace:** `Horcrux.Runtime.Utilities.PhysXHelper`

---

## 0. Bối cảnh & vị trí trong roadmap

- **Tầng 1** trong `Pendings.md` — nguyên hàm vật lý, chỉ phụ thuộc toán Tầng 0 (đã có `Interpolator`, `HarmonicOscillator`, `Easer`).
- Là "động cơ" được tái dùng nhiều nhất: follow camera, drag UI, magnetic snap, recoil, elastic drag. Thay `Mathf.SmoothDamp` với kiểm soát tốt hơn.
- Kế thừa tinh thần **framerate-independent** đã dựng ở `Interpolator.ExpDecay`: ưu tiên nghiệm giải tích chính xác thay vì xấp xỉ rời rạc.

### Khác biệt cốt lõi so với các class đã có
Các class trước đều **static thuần, không state**. Lò xo **buộc mang state**: vận tốc phải lưu qua từng frame. → Giải quyết bằng `struct` state + static solver (zero-GC, value-type).

---

## 1. Kiến trúc & module

```
Spring/
├── SpringConfig.cs      # struct: 2 tham số trực quan + precompute hệ số + converter physical
├── SpringSolver.cs      # enum SpringMethod; LÕI toán scalar (analytic + euler) — viết 1 lần
├── FloatSpring.cs       # struct FloatSpringState + Solve()
├── Vector2Spring.cs     # struct Vector2SpringState + Solve() (per-axis)
└── Vector3Spring.cs     # struct Vector3SpringState + Solve() (per-axis)
```

| File | Trách nhiệm | Phụ thuộc |
|---|---|---|
| `SpringConfig` | Giữ `frequency` + `dampingRatio`; precompute phần **không phụ thuộc dt** (`ω₀`, `ζω₀`, `ω_d`, `mode`); converter ↔ `stiffness/damping` | — |
| `SpringSolver` | `enum SpringMethod {Analytic, SemiImplicit}`; lõi toán scalar dùng chung | `SpringConfig` |
| `FloatSpring` | `struct FloatSpringState {position, velocity}` + `Solve(ref, target, cfg, dt, method)` | `SpringSolver` |
| `Vector2/3Spring` | struct state vector + `Solve()` gọi lõi scalar **per-axis** | `SpringSolver` |

**Nguyên tắc bám theo (CLAUDE.md):**
- **S**: mỗi file 1 kiểu / 1 trách nhiệm.
- **Tái dùng**: toán lò xo chỉ viết **một lần** trong `SpringSolver`; Vector2/3 lặp per-axis, không chép công thức.
- **O**: thêm solver = thêm `enum` case + 1 nhánh, không đụng struct state.
- **Zero-GC**: toàn `struct`, `Solve` nhận `ref`/`in`, không alloc, không LINQ/closure.
- **Precompute**: hệ số nặng (`exp`, `sincos`) — phần không phụ thuộc dt tính 1 lần trong `SpringConfig`; phần phụ thuộc dt tính trong `Solve`. Cân bằng tốc độ ↔ hỗ trợ dt biến thiên.

---

## 2. Nền toán học (cốt lõi)

### 2.1. Dựng phương trình từ vật lý

Vật khối lượng `m` gắn lò xo + giảm chấn, bị kéo về `target`. Định luật II Newton:

$$m\ddot{x} = \underbrace{-k(x - target)}_{\text{lực lò xo (Hooke)}} \;\underbrace{-\,c\dot{x}}_{\text{lực cản nhớt}}$$

- **Lực lò xo:** tỉ lệ độ lệch `(x − target)`, luôn kéo **về** target → dấu `−`. `k` = độ cứng.
- **Lực cản:** tỉ lệ vận tốc `ẋ`, cản **ngược** chiều chuyển động → dấu `−`. `c` = hệ số giảm chấn.

### 2.2. Chuẩn hóa về 2 tham số trực quan

Chia 2 vế cho `m`, đổi biến `y = x − target` (target hằng → `ẏ = ẋ`, `ÿ = ẍ`):

$$\ddot{y} + \frac{c}{m}\dot{y} + \frac{k}{m}y = 0$$

Đặt 2 đại lượng thay cho bộ ba `m, k, c`:

| Ký hiệu | Định nghĩa | Ý nghĩa |
|---|---|---|
| `ω₀` (tần số tự nhiên) | `√(k/m)` | tốc độ dao động "tự nhiên". Designer nhập `f` (Hz) → `ω₀ = 2π·f` |
| `ζ` (damping ratio) | `c / (2√(km))` | độ giảm chấn **không thứ nguyên** — 1 con số quyết định "kiểu" chuyển động |

Suy ra `c/m = 2ζω₀` và `k/m = ω₀²`. Phương trình gọn lại thành **dạng chuẩn**:

$$\boxed{\;\ddot{y} + 2\zeta\omega_0\,\dot{y} + \omega_0^2\,y = 0\;}$$

→ Đây là lý do chọn tham số `frequency + dampingRatio`: `m,k,c` gộp còn đúng **2 số có ý nghĩa cảm nhận được**. Bộ chuyển đổi ngược (`f,ζ → k,c`) ở §3.1.

### 2.3. Giải ODE → phương trình đặc trưng

ODE tuyến tính hệ số hằng → thử nghiệm dạng `y = e^{rt}`. Thay vào (`ẏ = re^{rt}`, `ÿ = r²e^{rt}`), chia cho `e^{rt} ≠ 0`:

$$r^2 + 2\zeta\omega_0 r + \omega_0^2 = 0 \;\Rightarrow\; r = -\zeta\omega_0 \pm \omega_0\sqrt{\zeta^2 - 1}$$

Dấu của `ζ² − 1` (tức `ζ` so với 1) chẻ ra **3 chế độ chuyển động**:

| Chế độ | Điều kiện | Nghiệm `r` | Hành vi |
|---|---|---|---|
| **Under-damped** | `ζ < 1` | phức: `−ζω₀ ± i·ω_d` | nảy quanh target rồi tắt, bao hình `e^{−ζω₀t}` |
| **Critically damped** | `ζ = 1` | kép: `−ω₀` | tới đích **nhanh nhất mà KHÔNG nảy** |
| **Over-damped** | `ζ > 1` | 2 thực âm | bò về đích chậm, ì |

Định nghĩa tần số/tốc độ tắt (dùng ở nghiệm):
- Under: `ω_d = ω₀√(1 − ζ²)` — tần số dao động **thực tế** (chậm hơn `ω₀` do bị cản).
- Over: `s = ω₀√(ζ² − 1)` — "tốc độ" phân rã của thành phần hyperbolic.

### 2.4. Nghiệm đóng cho từng chế độ (dùng cho solver **Analytic**)

Mục tiêu: từ `(y₀, v₀)` tại đầu bước, tính `(y, v)` sau `Δt`. Ba chế độ có **cấu trúc thống nhất** (chỉ khác cos↔cosh và giới hạn):

#### Under-damped (`ζ < 1`)
$$y(t) = e^{-\zeta\omega_0 t}\!\left[y_0\cos(\omega_d t) + \frac{v_0 + \zeta\omega_0 y_0}{\omega_d}\sin(\omega_d t)\right]$$

Đặt `A = y₀`, `B = (v₀ + ζω₀y₀)/ω_d`, `E = e^{−ζω₀t}`, `C = cos(ω_d t)`, `S = sin(ω_d t)`:
$$y = E\,(A\,C + B\,S)$$
$$v = \dot{y} = E\big[(-\zeta\omega_0 A + B\,\omega_d)\,C - (\zeta\omega_0 B + A\,\omega_d)\,S\big]$$

**Kiểm mốc** `t=0`: `E=1, C=1, S=0` → `y = A = y₀` ✓; `v = −ζω₀A + Bω_d = −ζω₀y₀ + (v₀+ζω₀y₀) = v₀` ✓.
`t→∞`: `E→0` → `y→0, v→0` tức `x→target` ✓.

#### Critically damped (`ζ = 1`)
Nghiệm kép `r = −ω₀`, nghiệm có dạng `(hằng + hằng·t)·e^{−ω₀t}`:
$$y(t) = e^{-\omega_0 t}\big[y_0 + (v_0 + \omega_0 y_0)\,t\big]$$
$$v(t) = e^{-\omega_0 t}\big[v_0 - \omega_0(v_0 + \omega_0 y_0)\,t\big]$$

**Kiểm mốc** `t=0`: `y=y₀` ✓, `v=v₀` ✓.

#### Over-damped (`ζ > 1`)
Cùng khuôn under-damped nhưng thay lượng giác bằng hyperbolic (`cos→cosh, sin→sinh, ω_d→s`) — dạng này **ổn định số** hơn tổng hai mũ rời:
$$y(t) = e^{-\zeta\omega_0 t}\!\left[y_0\cosh(s\,t) + \frac{v_0 + \zeta\omega_0 y_0}{s}\sinh(s\,t)\right]$$

Đặt `B = (v₀ + ζω₀y₀)/s`, `E = e^{−ζω₀t}`, `Ch = cosh(st)`, `Sh = sinh(st)`:
$$y = E\,(y_0\,Ch + B\,Sh)$$
$$v = E\big[(-\zeta\omega_0 y_0 + B\,s)\,Ch + (y_0\,s - \zeta\omega_0 B)\,Sh\big]$$

**Kiểm mốc** `t=0`: `Ch=1, Sh=0` → `y=y₀` ✓; `v = −ζω₀y₀ + Bs = −ζω₀y₀ + v₀ + ζω₀y₀ = v₀` ✓.

> **Vì sao Analytic ổn định vô điều kiện:** mọi thành phần đều nhân bao hình `e^{−ζω₀t}` (giảm dần khi `ζ>0`). Không có phép lặp tích lũy sai số như Euler → không bao giờ "nổ" dù `Δt` lớn. Cùng bản chất đã chứng minh ở `Interpolator.ExpDecay` §Bước 4 (hàm mũ cộng số mũ → độc lập cách chia thời gian).

### 2.5. Solver **Semi-implicit Euler** (đối chiếu, rẻ)

Rời rạc hóa ODE gốc, thứ tự **cập nhật vận tốc trước, vị trí sau**:

$$a = -\omega_0^2\,(x - target) - 2\zeta\omega_0\,\dot{x}$$
$$\dot{x} \mathrel{+}= a\,\Delta t; \qquad x \mathrel{+}= \dot{x}\,\Delta t$$

- **Vì sao "velocity-first" (semi-implicit) ổn hơn explicit Euler:** vị trí mới dùng **vận tốc đã cập nhật** → thêm một chút hàm ẩn, giữ ổn định tốt hơn nhiều khi dao động.
- **Ngưỡng nổ:** vẫn phân kỳ khi `ω₀·Δt` đủ lớn (bước quá thô so với chu kỳ). Quy tắc thực dụng: an toàn khi `ω₀·Δt` nhỏ (vài phần mười trở xuống). Khi `dt` dao động (lag spike) → **ưu tiên Analytic**.

### 2.6. Mở rộng vector

ODE dạng chuẩn **tuyến tính, không ghép chéo các trục** (`x, y, z` không xuất hiện chung trong một số hạng). → Mỗi trục là một bài toán scalar **độc lập** với **cùng** `ω₀, ζ`. Vector2/3 = chạy lõi scalar per-axis. **Không có toán mới.**

---

## 3. API & code hoàn chỉnh

### 3.1. `SpringConfig.cs`

```csharp
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    public enum SpringMode { UnderDamped, CriticallyDamped, OverDamped }

    /// <summary>
    /// Tham số lò xo dạng chuẩn (frequency + dampingRatio) + hệ số precompute không phụ thuộc dt.
    /// Immutable: tạo qua factory, hệ số nặng tính 1 lần.
    /// </summary>
    /// <remarks>
    /// ODE chuẩn: ÿ + 2ζω₀·ẏ + ω₀²·y = 0, với y = x - target.
    /// ω₀ = 2π·f (tần số tự nhiên), ζ = damping ratio (không thứ nguyên).
    /// </remarks>
    public readonly struct SpringConfig
    {
        public readonly float Omega0;     // ω₀ = 2π·f
        public readonly float Zeta;       // ζ  (đã clamp ≥ 0)
        public readonly float ZetaOmega;  // ζω₀       (precompute)
        public readonly float OmegaD;     // under: ω₀√(1−ζ²) | over: ω₀√(ζ²−1) | critical: 0
        public readonly SpringMode Mode;
        public readonly bool IsActive;    // false khi frequency ≤ 0 → Solve thành no-op

        private const float CriticalEpsilon = 1e-4f; // dải coi như ζ = 1 (tránh chia 0 ở ω_d/s)

        private SpringConfig(float omega0, float zeta, float zetaOmega,
            float omegaD, SpringMode mode, bool isActive)
        {
            Omega0 = omega0; Zeta = zeta; ZetaOmega = zetaOmega;
            OmegaD = omegaD; Mode = mode; IsActive = isActive;
        }

        /// <summary>API chính (designer-friendly).</summary>
        /// <param name="frequency">Tần số f (Hz): cảm giác "độ cứng"/tốc độ dao động. ≤ 0 → lò xo tắt.</param>
        /// <param name="dampingRatio">ζ: &lt;1 nảy, =1 tới đích nhanh nhất không nảy, &gt;1 ì. Âm bị clamp về 0.</param>
        public static SpringConfig FromFrequency(float frequency, float dampingRatio)
        {
            if (frequency <= 0f)
                return new SpringConfig(0f, 0f, 0f, 0f, SpringMode.CriticallyDamped, false);

            float omega0 = 2f * Mathf.PI * frequency;
            float zeta = dampingRatio < 0f ? 0f : dampingRatio; // âm = bơm năng lượng → nổ
            float zetaOmega = zeta * omega0;

            SpringMode mode;
            float omegaD;
            if (zeta < 1f - CriticalEpsilon)
            {
                mode = SpringMode.UnderDamped;
                omegaD = omega0 * Mathf.Sqrt(1f - zeta * zeta);
            }
            else if (zeta > 1f + CriticalEpsilon)
            {
                mode = SpringMode.OverDamped;
                omegaD = omega0 * Mathf.Sqrt(zeta * zeta - 1f); // = s
            }
            else
            {
                mode = SpringMode.CriticallyDamped;
                omegaD = 0f;
            }
            return new SpringConfig(omega0, zeta, zetaOmega, omegaD, mode, true);
        }

        /// <summary>Converter: tham số vật lý thô (k, c, m) → dạng chuẩn.</summary>
        public static SpringConfig FromPhysical(float stiffness, float damping, float mass = 1f)
        {
            // ω₀ = √(k/m); ζ = c / (2√(km)); f = ω₀ / 2π
            float omega0 = Mathf.Sqrt(stiffness / mass);
            float frequency = omega0 / (2f * Mathf.PI);
            float denom = 2f * Mathf.Sqrt(stiffness * mass);
            float zeta = denom > 0f ? damping / denom : 0f;
            return FromFrequency(frequency, zeta);
        }

        /// <summary>Converter ngược: dạng chuẩn → (k, c) để tra cứu/so sánh.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public (float stiffness, float damping) ToPhysical(float mass = 1f)
        {
            float k = Omega0 * Omega0 * mass;   // k = ω₀²·m
            float c = 2f * Zeta * Omega0 * mass; // c = 2ζω₀·m
            return (k, c);
        }
    }
}
```

### 3.2. `SpringSolver.cs` — lõi toán scalar (viết 1 lần)

```csharp
using Unity.Mathematics;

namespace Horcrux.Runtime.Utilities.PhysXHelper
{
    public enum SpringMethod { Analytic, SemiImplicit }

    /// <summary>
    /// Lõi giải lò xo 1 chiều (scalar). Vector2/3 gọi vào đây theo từng trục.
    /// </summary>
    public static class SpringSolver
    {
        /// <summary>
        /// Tiến state lò xo một bước dt. Trả (position, velocity) mới.
        /// </summary>
        /// <remarks>Giải ÿ + 2ζω₀·ẏ + ω₀²·y = 0 với y = pos - target. Xem spec §2.4–2.5.</remarks>
        public static (float pos, float vel) Solve(
            float pos, float vel, float target,
            in SpringConfig cfg, SpringMethod method, float dt)
        {
            if (!cfg.IsActive || dt <= 0f) return (pos, vel); // guard: lò xo tắt / bước rỗng

            float y = pos - target; // đổi biến về khoảng cách còn lại
            float v = vel;

            (float ny, float nv) = method == SpringMethod.Analytic
                ? SolveAnalytic(y, v, cfg, dt)
                : SolveSemiImplicit(y, v, cfg.Omega0, cfg.Zeta, dt);

            return (ny + target, nv); // đổi biến ngược: x = y + target
        }

        /// <summary>Nghiệm giải tích — ổn định vô điều kiện với mọi dt (spec §2.4).</summary>
        internal static (float, float) SolveAnalytic(float y0, float v0, in SpringConfig cfg, float dt)
        {
            float zw = cfg.ZetaOmega;
            float e = math.exp(-zw * dt); // bao hình chung e^(−ζω₀·dt)

            switch (cfg.Mode)
            {
                case SpringMode.UnderDamped:
                {
                    float wd = cfg.OmegaD;
                    float c = math.cos(wd * dt);
                    float s = math.sin(wd * dt);
                    float b = (v0 + zw * y0) / wd;

                    float y = e * (y0 * c + b * s);
                    float v = e * ((-zw * y0 + b * wd) * c - (zw * b + y0 * wd) * s);
                    return (y, v);
                }
                case SpringMode.OverDamped:
                {
                    float sc = cfg.OmegaD; // = s = ω₀√(ζ²−1)
                    float ch = math.cosh(sc * dt);
                    float sh = math.sinh(sc * dt);
                    float b = (v0 + zw * y0) / sc;

                    float y = e * (y0 * ch + b * sh);
                    float v = e * ((-zw * y0 + b * sc) * ch + (y0 * sc - zw * b) * sh);
                    return (y, v);
                }
                default: // CriticallyDamped
                {
                    float w0 = cfg.Omega0;
                    float coeff = v0 + w0 * y0;
                    float y = e * (y0 + coeff * dt);
                    float v = e * (v0 - w0 * coeff * dt);
                    return (y, v);
                }
            }
        }

        /// <summary>Semi-implicit Euler — rẻ, có thể nổ khi ω₀·dt lớn (spec §2.5).</summary>
        internal static (float, float) SolveSemiImplicit(float y, float v, float omega0, float zeta, float dt)
        {
            float a = -(omega0 * omega0) * y - 2f * zeta * omega0 * v; // gia tốc
            v += a * dt;   // velocity trước
            y += v * dt;   // position sau (dùng v đã cập nhật)
            return (y, v);
        }
    }
}
```

### 3.3. `FloatSpring.cs`

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

**Cách dùng:**
```csharp
var scale = new FloatSpringState(1f);
var cfg = SpringConfig.FromFrequency(4f, 0.4f); // 1 lần
// mỗi frame:
FloatSpring.Solve(ref scale, targetScale, cfg, Time.deltaTime);
transform.localScale = Vector3.one * scale.Position;
```

### 3.4. `Vector2Spring.cs` / `Vector3Spring.cs` — per-axis

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
        /// <summary>Lò xo 3 trục độc lập, cùng config. Tái dùng lõi scalar — không lặp toán.</summary>
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
`Vector2Spring` tương tự với `Vector2` và 2 trục. **Không dòng toán lò xo nào lặp lại.**

### 3.5. Biên & an toàn (đã nhúng vào code trên)

| Tình huống | Xử lý | Vị trí |
|---|---|---|
| `dt ≤ 0` | no-op, giữ state | `SpringSolver.Solve` |
| `frequency ≤ 0` | `IsActive=false` → no-op | `SpringConfig.FromFrequency` |
| `dampingRatio < 0` | clamp về 0 (âm = bơm năng lượng → nổ) | `SpringConfig.FromFrequency` |
| `ζ ≈ 1` | rơi vào nhánh critical (tránh chia 0 ở `ω_d`/`s`) | `CriticalEpsilon` |
| Euler `ω₀·dt` lớn | có thể nổ — khuyến nghị Analytic; ghi rõ trong XML doc | — |

---

## 4. Tiêu chí đúng đắn (tự viết test khi code lại)

### 4.1. Kiểm mốc & bất biến

| # | Tiêu chí | Kỳ vọng |
|---|---|---|
| 1 | `dt=0` | trả `(pos, vel)` y nguyên |
| 2 | `t→∞` (nhiều bước) | `pos→target`, `vel→0` ở cả 3 mode |
| 3 | `ζ=1` critical | `pos` tiến đơn điệu, **không vượt** target |
| 4 | `ζ<1` under | `pos` vượt target ≥1 lần, biên độ nảy giảm dần |
| 5 | `ζ>1` over | về target chậm hơn critical, không nảy |
| 6 | `frequency≤0` / `dt≤0` | giữ state, không `NaN` |
| 7 | `ζ<0` | clamp 0, không phân kỳ |

### 4.2. Độc lập framerate (then chốt của Analytic)
Chạy Analytic từ cùng `(pos, vel)` tới cùng tổng `T`, chia khác nhau (1 bước `T` vs `N` bước `T/N`) → kết quả trùng trong sai số float (~1e-4). Euler thì **lệch** theo cách chia.

### 4.3. Analytic ≈ Euler khi dt nhỏ
`dt→0` → hai solver cho quỹ đạo gần trùng (xác nhận cùng giải một ODE). `dt` lớn + `ω₀` cao → Euler lệch/nổ, Analytic vẫn ổn.

### 4.4. Bảo toàn năng lượng (sanity)
`ζ=0` → dao động điều hòa thuần → Analytic giữ biên độ không đổi; Euler semi-implicit trôi biên độ nhẹ (đặc tính đã biết, ghi chú rõ).

### 4.5. Debug hints
- Rung giật → `ω₀·dt` quá lớn (Euler): giảm frequency hoặc chuyển Analytic.
- Ì, không tới → `ζ` quá cao hoặc `frequency` quá thấp.
- Nổ (`NaN`/văng xa) → damping âm, hoặc Euler vượt ngưỡng ổn định.
- So sánh nhanh: log `Position` mỗi frame, kiểm mốc #2 (phải tiệm cận target).

---

## 5. Ngoài phạm vi (v1)

- **Quaternion spring** (xoay) — cần xử lý shortest-path + chuẩn hóa, để v2.
- **Bộ tài liệu `.html`** — không làm; tập trung derivation + code.
- **Precompute toàn bộ theo dt cố định** — hiện chỉ precompute phần không phụ thuộc dt (hỗ trợ dt biến thiên). Nếu sau này cần fixed-timestep tối đa tốc độ, có thể thêm biến thể `SpringConfigFixed`.
