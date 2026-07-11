# CLAUDE.md

## Dependencies

- **Horcrux SDK** — git submodule `Assets/Horcrux`, design guide: `Assets/Horcrux/SKILL.md`
- **Init(args)** — DI via `[Service]` + `MonoBehaviour<TDep>`. Runtime assemblies only
- **UniTask** — async/await, not coroutines. Always propagate `CancellationToken`
- **Addressables** — load via `AssetReference`, track handles for `Release()`

## Design Principles

Áp dụng cho **tất cả** code: runtime, editor, utilities. Không có ngoại lệ trừ khi ghi rõ.

### SOLID — tuyệt đối tuân thủ

- **S**: 1 class = 1 responsibility. Tách khi class có >1 lý do thay đổi
- **O**: Extend, don't modify. Thiết kế cho mở rộng (interface, abstract, strategy)
- **L**: Subtypes thay thế được base mà không break behavior
- **I**: Interface nhỏ, tách theo consumer. Không ép client phụ thuộc method không dùng
- **D**: Depend on abstractions. **Runtime**: dùng InitArgs (`Sisus.Init`) cho DI. **Editor**: không bắt buộc InitArgs, constructor injection hoặc static factory OK

### Hiệu năng — tuyệt đối tối ưu

**Zero GC in hot paths:**
- Pre-allocate collections, reuse buffers. `struct`/`ref`/`Span<T>` over `class` khi hợp lý
- Cache `GUIContent`/`GUIStyle`/delegates: `static readonly` hoặc lazy-init (`EnsureStyles()`)
- Pool objects thay vì Instantiate/Destroy. Grow-only buffers khi size dao động

**Tối ưu tính toán:**
- Cache over recompute — dirty flags, event-driven rebuilds, pre-built lookup dictionaries
- Heavy work trong event handlers, **tuyệt đối không** trong `Update`/polling loops
- Tách static vs dynamic — phần không đổi tính 1 lần, phần thay đổi tính incremental

**Tối ưu bộ nhớ:**
- NativeArray/NativeList (unmanaged) cho data lớn cần truyền GPU hoặc Job System
- `StructLayout(Sequential)` khi struct phải khớp layout với GPU/native
- Không allocate trong hot path: không `new`, không LINQ, không string concat, không closure capture

### Naming — self-documenting code

- Methods convey purpose: `EnsureMaterial()`, `SwapWriteBuffer()`. Không `Process`/`Handle`/`DoWork`
- Booleans read as questions: `IsPickable`, `HasCars`, `frameDataReady`
- Code tự giải thích → comment chỉ khi giải thích **tại sao**, không giải thích **cái gì**

### Async

- UniTask only. Always propagate `CancellationToken`
- Addressables via `AssetReference`, not string keys
