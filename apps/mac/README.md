# apps/mac

SwiftUI shell for macOS — **implemented (M1).**

A thin layer over the Rust core (spec §7.1); no business logic here.

- `Package.swift` — SwiftPM package. Links the Rust C ABI static lib from
  `../../target/debug/libakapen.a` (run `cargo build -p akapen-ffi` first).
- `Sources/CAkapen` — the hand-written C ABI header (mirrors `crates/akapen-ffi`).
- `Sources/AkapenKit` — idiomatic Swift wrapper (`AkapenEngine`).
- `Sources/AkapenApp` — the SwiftUI editor: open (⌘O / drag-drop), draw with
  NSEvent tablet pressure, erase, zoom/pan/rotate, undo/redo, save the 3-file
  set to `<input folder>/_review/` (⌘S), step ◀ / ▶ through the sequence.
- No-argument startup shows only a folder chooser and image drop target.
  Canvas/Metal and the lightweight bottom dock appear only after an image is
  loaded. The dock contains Pen/Eraser, ten circular colors in one row, and a
  compact 1–50 px size knob; document/history/zoom actions stay in native
  menus, shortcuts, and gestures.
- `Sources/AkapenUIContract` and its tests define the shared
  empty/loading/loaded, palette, size-control, and no-transition contract that
  the raw Win32 shell mirrors.
- Rendering uses the CPU composite for correctness, with an optional Metal/wgpu
  GPU surface for on-screen rendering when attached successfully.
- `Sources/akapen-harness` — headless proof: drives the FFI to draw
  pressure-varying strokes and write the 3-file export (used by CI and for
  verifying the boundary without a GUI).

## Build & run

```bash
cargo build -p akapen-ffi     # from the repo root: build libakapen.a
swift build                   # here: build the app + harness
swift run AkapenApp           # launch the review window
swift run akapen-harness /tmp/out
```

## Pressure (spec §5.0 / §5.6)

Pen pressure is a hard release gate. The shell reads `NSEvent.pressure` and only
treats `.tabletPoint` subtype events as pen pressure (so a trackpad Force Touch
is not mistaken for a pen). When the core detects a constant-pressure stroke it
raises a visible warning (spec §5.4) instead of silently drawing a flat line.
The interactive acceptance test on real WACOM hardware is a separate manual gate
each release.

## Not yet (deferred past M1)

- Full CSP shortcut table, size presets, main/sub/transparent color, eyedropper
  (spec M3).
