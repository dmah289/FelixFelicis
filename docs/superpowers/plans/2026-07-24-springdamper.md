# SpringDamper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Lưu ý về plan này:** đây là **artifact học tập**. Người dùng chủ động code lại để rèn tư duy và đã chọn **không viết code test** (spec §4). Vì vậy mỗi task thay bước "write failing test / run test" bằng **checklist tiêu chí kiểm chứng** (kiểm mốc toán học, chạy tay). Code trong mỗi task là **hoàn chỉnh, dán được** — trích từ spec.

**Goal:** Xây `SpringDamper` — bộ giải lò xo-giảm chấn framerate-independent (analytic + semi-implicit Euler) cho float/Vector2/Vector3, dạng struct state + static solver zero-GC.

**Architecture:** 5 file trong `Spring/`. Lõi toán scalar viết **một lần** trong `SpringSolver`; `FloatSpring` bọc state 1 chiều; `Vector2/3Spring` gọi lõi scalar **per-axis** (không lặp toán). Tham số hóa `frequency + dampingRatio` + converter physical, precompute hệ số không-phụ-thuộc-dt trong `SpringConfig`.

**Tech Stack:** C# (Unity), `Unity.Mathematics` (`math.exp/sin/cos/cosh/sinh`), `UnityEngine.Mathf` cho setup. Không Addressables/UniTask (thuần toán).

## Global Constraints

- Namespace: `Horcrux.Runtime.Utilities.PhysXHelper` (mọi file).
- Zero-GC hot path: toàn `struct`, `Solve` nhận `ref`/`in`, không `new` reference type, không LINQ/closure/string.
- SOLID: 1 file = 1 trách nhiệm; toán lò xo chỉ viết 1 lần (SpringSolver); mở rộng qua enum, không sửa struct.
- Self-documenting naming; XML doc kèm công thức + "tại sao" ở method public.
- Precompute hệ số nặng (`exp`, `sincos`) phần không phụ thuộc dt trong `SpringConfig`.
- ODE chuẩn: `ÿ + 2ζω₀·ẏ + ω₀²·y = 0`, với `y = x − target`, `ω₀ = 2π·f`.
- Guard biên: `dt ≤ 0` → no-op; `frequency ≤ 0` → lò xo tắt; `dampingRatio < 0` → clamp 0; `ζ ≈ 1` (|ζ−1| ≤ 1e-4) → nhánh critical.
- Spec nguồn: `docs/superpowers/specs/2026-07-24-springdamper-design.md`.

---

### Task 1: `SpringConfig` — tham số + precompute + converter

**Files:**
- Create: `Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/SpringConfig.cs`

**Interfaces:**
- Consumes: —
- Produces:
  - `enum SpringMode { UnderDamped, CriticallyDamped, OverDamped }`
  - `readonly struct SpringConfig` với field: `float Omega0, Zeta, ZetaOmega, OmegaD; SpringMode Mode; bool IsActive`
  - `static SpringConfig FromFrequency(float frequency, float dampingRatio)`
  - `static SpringConfig FromPhysical(float stiffness, float damping, float mass = 1f)`
  - `(float stiffness, float damping) ToPhysical(float mass = 1f)`

- [ ] **Step 1: Tạo file với đầy đủ code**

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
            float k = Omega0 * Omega0 * mass;    // k = ω₀²·m
            float c = 2f * Zeta * Omega0 * mass; // c = 2ζω₀·m
            return (k, c);
        }
    }
}
```

- [ ] **Step 2: Kiểm chứng (chạy tay / nhẩm, không code test)**

  - Unity biên dịch không lỗi (đợi domain reload, kiểm Console sạch).
  - `FromFrequency(2f, 0.5f)` → `Mode == UnderDamped`, `Omega0 ≈ 12.566`, `OmegaD ≈ 10.88` (`= ω₀·√0.75`).
  - `FromFrequency(2f, 1f)` → `Mode == CriticallyDamped`, `OmegaD == 0`.
  - `FromFrequency(2f, 2f)` → `Mode == OverDamped`, `OmegaD ≈ ω₀·√3 ≈ 21.77`.
  - `FromFrequency(0f, 0.5f).IsActive == false`.
  - `FromFrequency(2f, -1f).Zeta == 0`.
  - Round-trip: `FromPhysical(k, c).ToPhysical()` trả `(k, c)` gần đúng với `k=ω₀²`, `c=2ζω₀` (mass=1).

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
- Consumes: `SpringConfig` (field `IsActive, Omega0, Zeta, ZetaOmega, OmegaD, Mode`) từ Task 1.
- Produces:
  - `enum SpringMethod { Analytic, SemiImplicit }`
  - `static (float pos, float vel) SpringSolver.Solve(float pos, float vel, float target, in SpringConfig cfg, SpringMethod method, float dt)`
  - `internal static (float, float) SolveAnalytic(float y0, float v0, in SpringConfig cfg, float dt)`
  - `internal static (float, float) SolveSemiImplicit(float y, float v, float omega0, float zeta, float dt)`

- [ ] **Step 1: Tạo file với đầy đủ code**

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

- [ ] **Step 2: Kiểm chứng — kiểm mốc t=0 (bất biến quan trọng nhất)**

  Với mọi mode, gọi `SolveAnalytic(y0, v0, cfg, dt)` khi `dt → 0` phải trả `≈ (y0, v0)`:
  - Under: `e=1, c=1, s=0` → `y=y0`, `v = −zw·y0 + b·wd = −zw·y0 + (v0+zw·y0) = v0` ✓
  - Over: `ch=1, sh=0` → `y=y0`, `v = −zw·y0 + b·sc = v0` ✓
  - Critical: `e=1` → `y=y0`, `v=v0` ✓

- [ ] **Step 3: Kiểm chứng — hội tụ & hành vi 3 mode**

  Vòng lặp `for` ~5s với `dt=1/60`, `target=10`, `pos0=0`:
  - Cả 3 mode: `pos → 10`, `vel → 0` (tiêu chí #2).
  - `ζ=1` (critical): `pos` **không vượt** 10 (đơn điệu tăng) — tiêu chí #3.
  - `ζ=0.3` (under): `pos` **vượt** 10 ít nhất 1 lần, biên độ nảy giảm dần — tiêu chí #4.
  - `ζ=2` (over): về 10 chậm hơn critical, không vượt — tiêu chí #5.

- [ ] **Step 4: Kiểm chứng — độc lập framerate (then chốt của Analytic)**

  Từ cùng `(pos0, vel0)`, chạy Analytic tới cùng `T=1s`:
  - Cách A: 1 bước `dt=1`. Cách B: 100 bước `dt=0.01`.
  - Hai kết quả `pos` trùng trong ~1e-4. (Euler thì lệch rõ → xác nhận đúng đặc tính.)

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
- Consumes: `SpringSolver.Solve(float, float, float, in SpringConfig, SpringMethod, float)` (Task 2); `SpringConfig`, `SpringMethod`.
- Produces:
  - `struct FloatSpringState { float Position; float Velocity; ctor(float position, float velocity = 0f) }`
  - `static void FloatSpring.Solve(ref FloatSpringState s, float target, in SpringConfig cfg, float dt, SpringMethod method = SpringMethod.Analytic)`

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
  // Kỳ vọng: s.Position ≈ 5, s.Velocity ≈ 0 (state cập nhật in-place qua ref).
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
- Consumes: `SpringSolver.Solve(float, float, float, in SpringConfig, SpringMethod, float)` (Task 2); `SpringConfig`, `SpringMethod`.
- Produces:
  - `struct Vector2SpringState { Vector2 Position; Vector2 Velocity; ctor(Vector2, Vector2 = default) }`
  - `static void Vector2Spring.Solve(ref Vector2SpringState, Vector2 target, in SpringConfig, float dt, SpringMethod = Analytic)`
  - `struct Vector3SpringState { Vector3 Position; Vector3 Velocity; ctor(Vector3, Vector3 = default) }`
  - `static void Vector3Spring.Solve(ref Vector3SpringState, Vector3 target, in SpringConfig, float dt, SpringMethod = Analytic)`

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
        /// <summary>Lò xo 2 trục độc lập, cùng config. Tái dùng lõi scalar — không lặp toán.</summary>
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

  - `Vector3` với target `(5, -3, 10)`, chạy ~5s: mỗi trục hội tụ đúng thành phần target, `Velocity → 0`.
  - Chạy 1 trục của Vector3 với cùng `(pos, vel, target, cfg, dt)` như `SpringSolver.Solve` scalar → **trùng khít** (chứng minh không có toán riêng, chỉ per-axis).
  - `Vector2` tương tự với `(5, -3)`.

- [ ] **Step 4: Commit**

```bash
git add Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/Vector2Spring.cs Assets/Horcrux/Runtime/Utilities/PhysXHelper/Spring/Vector3Spring.cs
git commit -m "feat(spring): Vector2/Vector3 spring - per-axis tai dung loi"
```

---

## Ghi chú thực thi

- **Thứ tự bắt buộc:** Task 1 → 2 → 3/4 (2 phụ thuộc 1; 3 và 4 đều phụ thuộc 2, độc lập nhau).
- **File `.meta`:** Unity tự sinh khi import — commit kèm nếu bạn muốn giữ GUID ổn định (tùy quy ước repo).
- **Kiểm chứng:** tất cả bước "Kiểm chứng" chạy tay qua một scene/script tạm hoặc nhẩm theo kiểm mốc — **không** tạo file test (theo lựa chọn của bạn ở spec §4). Xóa script tạm trước khi commit.
- **Nếu sau này muốn test tự động:** dựng NUnit EditMode test theo bảng tiêu chí spec §4; không nằm trong plan này.
