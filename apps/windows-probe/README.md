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

## Interactive mode

**Still the throwaway de-risk spike — not the M2 WinUI 3 shell** (that is
`bindings/dotnet`, csbindgen-generated). The same `AkapenProbe.exe` now has a
second mode that wires the existing HWND up to real WM_MOUSE / WM_KEYDOWN so a
developer can actually see it working end-to-end. Everything about how the C
ABI is crossed is unchanged (hand-written `DllImport`, raw `user32.dll`, no
WinForms/WPF/WinUI 3 dependency); only the message handling and a save path
were added on top.

What it does in interactive mode:

- Left-mouse drag draws a red pen stroke (mouse-kind, pressure 1.0). The
  window's client area is sized to match the loaded image (or 800×600 for the
  blank canvas), so cursor coordinates land in engine pixels 1:1 without a
  remap. Resizing the window mid-session is deliberately not remapped — the
  probe is not the M2 shell.
- **Ctrl+S** saves the 3-file `_review/` set (transparent strokes PNG, flat
  PNG, vector JSON) via `akapen_export_to_dir` and reports the outcome on
  stdout.
- **Closing the window auto-saves** (once) if anything was drawn, then exits.
  A save failure is logged but does not block the close.

Modes:

- **Default (no args):** the original scripted presentation smoke test (>=300
  frames, >=3 resizes, stdout-only pass/fail).
- **Interactive:** pass `--interactive` (or `-i`), or just pass an image path
  as the first argument. An optional second argument overrides the save
  target directory (default: `%TEMP%\akapen-review`).

### Build & run (on the Windows dev box)

From the repository root:

```
apps\windows-probe\run.bat                              REM blank canvas
apps\windows-probe\run.bat path\to\shot.png             REM open an image
apps\windows-probe\run.bat path\to\shot.png D:\reviews  REM custom save dir
```

`run.bat` builds `akapen.dll` (Rust, `cargo build -p akapen-ffi --release`),
builds `AkapenProbe.dll` (.NET, `dotnet build -c Release`), copies the native
DLL next to the managed one so the P/Invoke resolver finds it, and runs the
probe with any arguments forwarded via `%*`.

Then: left-drag to draw, `Ctrl+S` to save, close the window to auto-save and
exit. The default save location prints on startup (look for
`[probe] interactive mode; save target dir='...'`).

## Headless-export mode

`--headless-export <outdir>` runs a third, non-interactive path: no window,
no swapchain, no `akapen_render_attach`. The probe creates an engine, draws
a fixed signature stroke pattern (two thick diagonals forming an X, plus a
thin vertical baseline) with varying pressure through `akapen_pointer`, then
calls `akapen_export_to_dir` to write the 3-file `_review/` triplet:
`headless.review.png` (composited over the canvas), `headless.strokes.png`
(transparent overlay), and `headless.strokes.json` (vector, schema
`veda-annot-1`).

Purpose: off-LAN witness of the Windows core+FFI+export pipeline when
someone can't get to the box in an interactive session. Session 0 SSH cannot
present a DX12 swapchain (DXGI composition surfaces need Session 1), so the
window-based smoke and interactive modes are unavailable over SSH. This mode
sidesteps that: it exercises `raster::bake_stroke` (the CPU path used by
export) and the full stroke→bake→composite→PNG chain, and yields two PNGs
that show whether Windows rendered the strokes correctly. It is **not** a
substitute for the interactive test: `tessellate_stroke` and the GPU
wet-ink present path are still only witnessed by Mode 2 in Session 1.

Failure output goes to stderr with distinct exit codes: `2` for an
unhandled exception, `5` for engine bring-up returning null, `6` for
`akapen_export_to_dir` returning a non-zero rc (the Rust-side code maps to
`AkapenExportError` in the mac shell's Swift binding for context).

```
apps\windows-probe\run.bat --headless-export C:\tmp\out            REM blank canvas
apps\windows-probe\run.bat path\to\shot.png --headless-export C:\tmp\out  REM open an image
```

Or without `run.bat`, once `akapen.dll` is already sitting next to the
built managed DLL:

```
dotnet apps\windows-probe\AkapenProbe\bin\Release\net8.0\AkapenProbe.dll --headless-export C:\tmp\out
```
