# CLAUDE.md

## Dependencies

- **Horcrux SDK** — git submodule `Assets/Horcrux`, design guide: `Assets/Horcrux/SKILL.md`
- **Init(args)** — DI via `[Service]` + `MonoBehaviour<TDep>`
- **UniTask** — async/await, not coroutines; always pass `CancellationToken`
- **Addressables** — load via `AssetReference`; track handles for `Release()`

## Design Principles

Apply to all code: runtime, editor, utilities.

**SOLID** — S: one responsibility per class. O: extend, don't modify. L: subtypes substitutable. I: small interfaces. D: depend on abstractions; use InitArgs (`Sisus.Init`) for DI in runtime assemblies.

**Zero GC in hot paths** — pre-allocate collections, reuse buffers, prefer `struct`/`ref`/`Span<T>`. Cache `GUIContent`/`GUIStyle`/delegates as `static readonly` or lazy-init (`EnsureStyles()`). Pool objects instead of Instantiate/Destroy.

**Cache over recompute** — dirty flags, event-driven rebuilds, pre-built lookup dictionaries. Heavy work in event handlers, never in `Update`/polling loops.

**Naming** — methods convey purpose without comments. No `Process`/`Handle`/`DoWork`. Booleans read as questions: `IsPickable`, `HasCars`.

**Async** — UniTask only. Always propagate `CancellationToken`. Addressables via `AssetReference`, not string keys.
