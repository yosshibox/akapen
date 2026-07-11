# apps/windows-probe

**Throwaway de-risk spike — not a product milestone.** This is *not* the
future WinUI 3 shell (that is `bindings/dotnet`, csbindgen-generated, M2 per
`bindings/README.md`). This is a hand-rolled, no-codegen C# console host whose
only job is to answer one question: *does a bare Win32 HWND actually reach a
working DX12 present through `akapen-ffi`'s C ABI, end to end?*

## Why C# P/Invoke (not a Rust bin)

Two ways to de-risk the Windows presentation path were on the table:

- **A: Rust bin** calling either the C ABI or akapen-render's internal Rust
  API directly.
- **B: C# P/Invoke host** calling the C ABI (`akapen.dll`) via `DllImport`.

**B was chosen.** A Rust bin could call `akapen-render`'s Rust types
directly, which would prove the *render crate* works on Windows but would
silently skip the actual marshaling boundary (`AkapenSurfaceDesc`,
`AkapenViewTransform`, `extern "C"` calling convention, struct layout,
`size_t`/`nuint` size-probe pattern) that a real external consumer — the
future WinUI 3 shell, or any other language binding — has to cross. Since the
product's own design puts a hand-written C ABI between the Rust core and
every shell (spec §7.2), and the mac shell already exercises that boundary
from Swift, the Windows-side de-risk is only representative if it exercises
the *same* boundary from a different language. C# P/Invoke against the built
`akapen.dll` does exactly that: it marshals a real HWND across the C ABI,
receives back the opaque `AkapenEngine*`, and drives
`akapen_render_attach`/`_resize`/`_frame`/`_detach` exactly as a shell would.

`AkapenProbe/` is a plain `net8.0` console app (`Program.cs`) with hand-written
`DllImport` declarations mirroring `crates/akapen-ffi/include/akapen.h` — no
csbindgen, no WinUI 3/Windows App SDK dependency, so it builds with only the
.NET 8 SDK already on the box.

## What it does

1. Registers a Win32 window class and creates a plain `WS_OVERLAPPEDWINDOW`
   HWND (raw `user32.dll` P/Invoke — no WinForms/WPF).
2. Calls `akapen_new` then `akapen_render_attach` with
   `AkapenSurfaceDesc{ kind = AKAPEN_SURFACE_HWND, handle = hwnd, ... }`.
   On failure, prints the code *and* `akapen_render_last_attach_error` (the
   real `RendererError` text) rather than just the opaque integer.
3. On success, prints `akapen_render_backend_info` (actual backend / present
   mode / max frame latency — see `crates/akapen-render/src/canvas.rs`).
4. Pumps Win32 messages and renders >=300 frames, periodically feeding a
   short pen stroke through `akapen_pointer` (so the frames actually exercise
   the wet-ink → bake → composite path, not just a static background), and
   resizes the window >=3 times mid-run via `SetWindowPos` +
   `akapen_render_resize`.
5. Reports a pass/fail summary (frames rendered, resizes performed, any
   exception) and exits 0 on a clean run, non-zero otherwise.

## Build & run (on the Windows dev box)

```
cargo build -p akapen-ffi --release        REM from the repo root: produces target\release\akapen.dll
cd apps\windows-probe\AkapenProbe
dotnet build -c Release
copy ..\..\..\target\release\akapen.dll bin\Release\net8.0\akapen.dll
dotnet bin\Release\net8.0\AkapenProbe.dll
```

No visual confirmation is expected or required from this probe (it may run
under a non-interactive SSH session with no attached desktop) — see the dev
journal for how the accept criteria (present succeeds, no crash, resize
tolerance, logged backend/present-mode/frame-latency) are read from its
stdout instead of a screenshot.
