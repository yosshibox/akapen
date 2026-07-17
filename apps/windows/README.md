# Akapen Windows MVP

`AkapenProbe/` is the Windows-first MVP shell. The name is historical; it is
now the supported raw Win32 host over `akapen-ffi`'s C ABI. It uses a plain
`HWND` and hand-written C# P/Invoke, with no WinUI 3, Windows App SDK, WPF, or
WinForms dependency. Publish it self-contained and the target PC does not need
to install .NET or Windows App Runtime.

## MVP interactions

- Start in the folder/drop empty state or pass an image path. `Ctrl+O` opens
  the native file picker; the empty-state button selects an image folder.
- Mouse or pen draws; `WM_POINTER` pen pressure is passed to the Rust core.
  Touch is ignored while a pen is active as the initial palm-rejection policy.
- Shortcuts resolve through the shared Rust keymap with a user-selectable
  preset (V1.1). The default is the **Photoshop** preset: `B`/`P` pen, `E`
  eraser, `Ctrl+Z` undo, `Ctrl+Shift+Z` redo (`Ctrl+Alt+Z` also steps
  backward; `Ctrl+Y` is deliberately unmapped, as in Photoshop), `Ctrl+0`
  fit, `Ctrl+1` / `Ctrl+Alt+0` 100%, `R` / `Shift+R` rotate the view by
  15 degrees. The **CLIP STUDIO** preset (settings) restores the spec §3
  table (`Ctrl+Y` redo, `-`/`^` rotate 15 degrees, `R` = rect tool key).
- `+`/`-` zoom, `F` fit, Space+drag pan.
- Arrow keys: `←`/`→` previous/next image, `↑`/`↓` zoom in/out (V1.1). While
  the brush fader is focused, `↑`/`↓`/`PageUp`/`PageDown`/`Home`/`End` adjust
  the brush size instead.
- `[`/`]` changes brush size.
- A single touch drag pans when no pen is active; touch is rejected while a
  pen is down as the MVP palm-rejection policy.
- `PageUp`/`PageDown` move through neighboring image files.
- A Photoshop-style navigator sits at the top of the right dock (V1.1): a
  live thumbnail with the current viewport rectangle; click/drag it to
  recenter the view, and use the `−`/`+` buttons or the percentage readout
  for zoom.
- `Ctrl+S` exports the non-destructive review set. For an image under `hoge`,
  the default folder is `hoge_review` beside the source: the flat PNG is kept
  directly there, while the transparent stroke PNG and JSON are placed under
  its `strokes` subfolder. The stroke PNG and JSON are deleted when Akapen
  closes; the flat PNG and source image remain.

## Build and run on Windows

From the repository root:

```bat
apps\windows\run.bat
apps\windows\run.bat C:\path\to\image.png
```

The script builds the Rust DLL, publishes a self-contained `win-x64` host,
copies the native `akapen_native.dll` beside the executable, and launches it.
The published
executable is under
`AkapenProbe\bin\Release\net8.0\win-x64\publish\Akapen.exe`.

The no-argument mode is the normal Akapen product shell. It shows the folder
chooser and image drop target without creating a canvas. After an image loads,
the canvas and dedicated right-side tool dock appear without overlap. Use `--presentation-smoke` for the scripted presentation check and
`--headless-export <directory>` for an SSH/session-0 export check without a
desktop compositor.

For maintainers, `--interactive-smoke <directory>` runs in an interactive
desktop session, sends real `HWND` mouse messages through the window procedure,
and closes after auto-saving the flat artifact plus transient stroke artifacts.
It also verifies the transient cleanup path. It is a regression check, not a
user-facing workflow.

## Product GUI contract

The canvas and product UI are separate HWND regions. The loaded workspace uses
a 96-DIP bottom dock and 24-DIP status row; neither overlaps the renderer's
child canvas.

- Only Pen and Eraser are permanent tool buttons. File/history/view/sequence
  actions remain available from native menus, shortcuts, and gestures.
- The fixed ten-color MS Paint-style palette is always visible. A swatch takes
  effect with one click; ten circular swatches remain on one row and a
  three-pixel accent ring shows the selection.
- Brush size is a compact 48×72-DIP vertical knob from 1 to 50 px, with a
  downward-triangle cap, five small ticks, and the current px value. Click jumps to a
  value, drag adjusts continuously, arrows change by 1, Page Up/Down by 5, and
  Home/End select the minimum/maximum. A large rail is intentionally absent.
- `assets/icons/akapen-ui-icons.svg` is the canonical 24×24 icon source
  (`stroke-width=1.75`, round cap/join, `currentColor`). Windows parses the
  embedded path data and rasterizes it at the current monitor DPI.
- Product labels and status text no longer use GDI `TextOut`. The isolated
  native UI renderer uses antialiased GDI+ paths and ClearType Japanese text
  today; its boundary is intentionally replaceable by Direct2D/DirectWrite
  without changing portable commands, layout state, canvas, or save paths.
- The main HWND suppresses `WM_ERASEBKGND` and paints one complete memory-DC
  frame per `WM_PAINT` before a single `BitBlt`, preventing resize/hover/style
  flicker across the parent/canvas child boundary.

## Deliberate MVP boundaries

Pressure is best-effort: native `WM_POINTER` pressure is used when reported,
and mouse input uses pressure 1.0. Advanced palm rejection, annotation tools
beyond pen/eraser, and Rapture-level polish are later tune-up work. The Rust
core and C ABI remain UI-independent so the same engine can be reused by the
Mac shell and VEDA integration.
