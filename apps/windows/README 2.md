# apps/windows

Akapen Windows shell — the WinUI 3 + Windows App SDK (.NET 8) implementation
of the M2 milestone (spec §9 M2 "Windows 写像", §7.4-4 UI-framework choice,
§8.2 repository layout). Wraps the same `crates/akapen-ffi` C ABI the mac
SwiftUI shell (`apps/mac`) drives — everything to do with strokes, palm
rejection, key mapping, or export lives in the Rust core; this project is a
thin XAML shell around it.

## Not to be confused with `apps/windows-probe`

`apps/windows-probe` is a **throwaway de-risk spike** (see
`../windows-probe/README.md`). It hand-rolls a raw-Win32 HWND window with
`user32.dll` P/Invoke and hand-written `DllImport` declarations so it can
answer one question: *does an HWND actually reach a working DX12 present
through `akapen-ffi`, end to end?* It intentionally has zero WinUI 3 / Windows
App SDK dependency and zero csbindgen dependency. Do not extend it, do not
merge it with this project, do not port its code here. When the WinUI 3 path
is fully proved out, the probe can be deleted.

This project (`apps/windows`), by contrast, is the real M2 shell:

- WinUI 3 (`UseWinUI=true`) + Windows App SDK, unpackaged
  (`WindowsPackageType=None`) with framework-dependent deployment. The target
  machine must have the matching Windows App Runtime installed; this is the
  supported runtime path for the Windows MVP.
- Links the same csbindgen-generated `NativeMethods.g.cs` source used by
  `bindings/dotnet/Akapen.Native` — the shell avoids a plain `net8.0`
  `ProjectReference` because WinUI's XAML compiler cannot resolve that
  managed assembly as Windows metadata (WMC1006). The generated source still
  has one C ABI and one binding source of truth.
- Renders through a WinUI `SwapChainPanel` (spec §7.4-6): the panel's raw
  `ISwapChainPanelNative*` (obtained via `QueryInterface`) is passed as
  `AkapenSurfaceDesc.handle` with `kind = AKAPEN_SURFACE_SWAPCHAIN_PANEL`;
  wgpu's dx12 backend consumes it directly via
  `SurfaceTargetUnsafe::SwapChainPanel(*mut c_void)`. See
  `crates/akapen-render/src/surface.rs` for the Rust side of the contract.

## In scope this chapter (M2-A)

- `App.xaml`(.cs) + `MainWindow.xaml`(.cs) + `AkapenApp.csproj` scaffold.
- Open… button + FileOpenPicker (PNG / JPEG / WebP / BMP) →
  `akapen_open_image`.
- Save button + `Ctrl+S` keyboard accelerator →
  `akapen_export_to_dir(<inputDir>/_review/, <stem>)`. Default suffixes
  `review` / `strokes` (spec §4.3). Configurable output dir mode / suffixes
  (spec §4.7) is a later chapter.
- SwapChainPanel wired to `akapen_render_attach` / `_resize` / `_frame` /
  `_detach` via the raw `ISwapChainPanelNative*`. DispatcherTimer drives one
  present per tick.
- Mouse-only input: `PointerPressed/Moved/Released` → `akapen_pointer(kind=Mouse,
  pressure=1.0)`. Superseded by M2-B1 below (kept for history — the
  coordinate-mapping logic it introduced is unchanged, just generalized).
- Default pen settings on Open: red (`0xFF0000FF`) / 6.0 px, Pen tool. Matches
  the mac shell's opening pose (`PaletteColors.defaultColor`).
- Unpackaged FileOpenPicker requires `WinRT.Interop.InitializeWithWindow`
  with the window's HWND, otherwise `PickSingleFileAsync` throws `NoWindow`;
  that quirk is handled in `MainWindow.xaml.cs::OnOpenClick`.

## In scope this chapter (M2-B1)

- Real pointer-kind + pressure (spec §5.1): `PointerRoutedEventArgs.Pointer.
  PointerDeviceType` (`Microsoft.UI.Input.PointerDeviceType.Pen/Touch/Mouse`)
  classifies each event; `GetCurrentPoint(RenderSurface).Properties.Pressure`
  supplies the pen's real 0.0-1.0 pressure (touch/mouse are pinned to 1.0,
  same as before). The raw pressure value is passed through unrounded so the
  core's §5.4 pressure-stuck detector sees genuine driver behavior.
- Palm rejection (spec §5.2): every classified pointer event is routed
  through `akapen_palm_route` — the same pure core state machine the mac
  shell's `PalmGate` wraps — *before* it can reach `akapen_pointer`. Draw
  (pen or mouse — a pen is always the intended input, a mouse is never a
  palm) proceeds to drawing. Touch never draws: it always resolves to either
  Navigate (a deliberate touch with no pen in play — routed for future
  canvas pan/pinch, M2-D) or Ignore (a palm during pen contact or the
  post-pen lock window); neither is fed to the drawing engine.
  `MainWindow.xaml.cs` holds one caller-owned `AkapenPalmState` per window
  (`_palmState`), matching the mac shell's one-per-canvas `PalmGate`.
- `MainWindow.xaml.cs::PushMouseSample` is generalized to `PushPointerSample`
  (kind + pressure + palm gate, then the same M2-A coordinate inversion as
  before). The single `_mouseDown` bool becomes a `HashSet<uint>
  _activePointerIds` keyed on `Pointer.PointerId`, because palm rejection's
  whole point is a pen stroke and a resting palm touch being in contact *at
  the same time* — a shared boolean would have the palm's Up wrongly end the
  still-in-progress pen stroke. (Still a single in-progress *drawing* stroke
  assumption — genuine simultaneous multi-touch drawing remains out of
  scope.)
- Spec §5.4 pressure-stuck warning: after a completed stroke (`Released`)
  that reached the drawing engine, `akapen_pressure_stuck` is checked and
  surfaced in a dedicated `PressureWarningText` TextBlock (kept separate from
  `StatusText` so a save/open message can never silently clobber it, or vice
  versa — the failure mode the mac shell's save() comment calls out having
  hit once already).
- `SwapChainPanel.ManipulationMode="None"` is now explicit in
  `MainWindow.xaml`, so WinUI's gesture recognizer never intercepts a
  multi-contact drag (e.g. a pen stroke alongside a resting palm) before our
  pointer handlers + the palm gate see it — the WinUI analogue of the mac
  shell's `allowedTouchTypes = [.direct]`.

## In scope this chapter (M2-D)

- **Toolbar tool switcher** (`Pen` / `Eraser`): two `ToggleButton`s with
  code-behind mutual exclusion. Clicking the already-active tool re-selects
  it rather than leaving no tool active (WinUI's default toggle behavior).
  Extra tools (`Line` / `Arrow` / `Rect` / `Ellipse` / `Text`) exist in the
  FFI but are M3 UI scope.
- **Toolbar undo / redo** (`Undo`, `Redo`) with `Ctrl+Z` /
  `Ctrl+Y` + macOS-idiom `Ctrl+Shift+Z` KeyboardAccelerators. Both buttons
  gate on `_engine != IntPtr.Zero` (enabled when an image is loaded,
  disabled when none is), same shape as `SaveButton`.
- **Right-hand SidePanel** (192 DIP fixed width, `Grid.Column=1` next to
  the SwapChainPanel):
  - **Size slider** (`Minimum=1` / `Maximum=50` / `Value=10`,
    `StepFrequency=1`), with a live "N px" readout right of the label.
    Fires `akapen_set_size` on every `ValueChanged`. `[` / `]` shortcuts
    step ±1 via a code-behind handler that writes back through the Slider
    (so thumb + readout + engine stay in sync from one place).
  - **10-swatch MS Paint palette** (`PaletteColors.cs`; MS Paint 上段: 黒,
    グレー50%, 暗い赤, **赤 #ED1C24 (既定)**, オレンジ, 黄, 緑, ターコイズ,
    インディゴ, 紫). Rendered as two rows of five 26x26 buttons built in
    code from the `PaletteColors.Colors` list so a palette swap only
    touches one file. Each swatch carries `AutomationProperties.Name` +
    tooltip = the color's Japanese name. The selected swatch is
    highlighted with a thick white border.
- **Deferred-apply pattern**: `_currentTool` / `_currentColorRgba` /
  `_currentSize` fields hold the shell's current pose. Every UI change
  updates the field first and then calls `ApplyToolStateToEngine()`, which
  is a silent no-op while `_engine == IntPtr.Zero`. On `LoadImage` after
  `akapen_open_image` returns, `ApplyToolStateToEngine()` runs once to seed
  the fresh engine with the shell's current pose — so tool state survives
  across image opens, matching `AppState.applyToolState` on mac
  (`apps/mac/Sources/AkapenApp/AppState.swift`). The opening pose (Pen /
  MS Paint 赤 / 10 px) is set by the field initializers and mirrored in
  XAML defaults (PenToolButton `IsChecked=True`, Slider `Value=10`).
- **Single-key shortcuts** (spec §3 主要行の先取り): `P` = Pen, `E` = Eraser,
  `[` = size –1, `]` = size +1. Registered programmatically on `RootGrid`
  in `MainWindow.xaml.cs::RegisterGlobalAccelerators` — `[` / `]` map to
  VirtualKey `0xDB` / `0xDD` (VK_OEM_4 / VK_OEM_6), which
  `Windows.System.VirtualKey` has no named members for, so XAML can't
  spell them and code-behind registration is the only option. The full
  spec §3 table (routed through `akapen_resolve_key` so both shells share
  one map) is M3.

## In scope this chapter (M2-F)

- **Prev/Next frame stepping** (spec §4.5 連番次前, §3 主要ナビ行):
  `PrevButton`/`NextButton` on the toolbar (`◀ Prev` / `Next ▶`), each with a
  `PageUp`/`PageDown` `KeyboardAccelerator` guarded by the same
  `IsTextInputFocused` check as the M2-D accelerators
  (`OnStepAcceleratorInvoked`, same shape as `OnUndoRedoAcceleratorInvoked`).
- **Sibling detection**: `MainWindow.xaml.cs::BuildSiblings` rescans the
  current image's folder for **any supported image file** (case-insensitive
  match against `SupportedFrameExtensions` = `.png`/`.jpg`/`.jpeg`/`.webp`/
  `.bmp`, mixed together — same set as mac's `AppState.supportedExts`) and
  natural-sorts them (`frame_2.png` before `frame_10.png`) via
  `NaturalStringComparer.cs`, a P/Invoke wrapper around Shlwapi's
  `StrCmpLogicalW` — the same routine Explorer itself uses to order a
  folder listing. From that combined list, `SequenceStepper` still prefers
  the same-extension token neighbor (spec §4.5 の末尾連番トークン優先),
  falling back to natural-sort adjacency; the mixing only widens the pool
  the fallback can traverse. Rebuilt on every `LoadImage` (Open… and Prev/
  Next alike), since the folder's contents can change between one open and
  the next.
  `PrevButton`/`NextButton.IsEnabled` (`UpdateStepButtonsEnabled`) reflect
  whether the current image sits strictly inside that sequence: first frame
  disables Prev, last disables Next, a lone file with no siblings disables
  both.
- **Data-loss guard** (spec §4.5 "確認ダイアログではなく自動保存"): a new
  `_hasUnsavedStrokes` field flips true the moment a drawing sample reaches
  `akapen_pointer` in `PushPointerSample` and clears on a successful save.
  `StepFrame` auto-saves before switching frames and **aborts the step,
  keeping the current frame,** if that save fails — mirroring the mac
  shell's `AppState.step(forward:)` guard ahead of `AppState.open(url:)`.
  `OnOpenClick` runs the same guard before Open… replaces the image (on
  failure the picked file is not opened; the previous image stays up).
  `OnWindowClosed` attempts a best-effort save on close but, unlike the two
  above, never blocks the close on failure — mirroring
  `apps/windows-probe`'s `WM_CLOSE` handling (`Program.cs`'s `s_dirty` /
  `TrySave("on-close")`).
- **Shared save path**: the Save button/Ctrl+S, the Prev/Next step guard, the
  Open… guard, and the close-time best-effort save all go through one
  `TrySave()` method, so the `DescribeExportRc` failure-code mapping (rc 1-4
  + unknown) and the status-text wording are identical everywhere a save can
  fail — no separate "auto-save" message shape to keep in sync.
- **Dirty marker**: a trailing `•` on the window title while
  `_hasUnsavedStrokes` is true (`UpdateDirtyIndicator`), left deliberately
  modest — the mac shell has no dedicated dirty-marker UI either.
- Output directory / naming stay fixed at `<input's folder>/_review/` with
  the engine's default suffixes; configurable output dir mode + suffixes
  (spec §4.7) is handled by **M2-E** below.

## In scope this chapter (M2-E)

- **Independent Settings window** (`SettingsWindow.xaml` + `.xaml.cs`): a
  top-level WinUI 3 `Window` (not a modal child, closing it leaves the main
  window running). Opened by the toolbar `Settings…` button or the `Ctrl+,`
  KeyboardAccelerator (mac Cmd+, idiom写像; VK_OEM_COMMA = 0xBC, registered
  in `RegisterGlobalAccelerators` since `Windows.System.VirtualKey` has no
  named member for the comma — same shape as `[` / `]`). `MainWindow` holds
  the `_settingsWindow` field, `EnsureSettingsWindow()` activates an
  existing window or spins up a fresh one; the `Closed` handler nulls the
  field so the next Ctrl+, always opens a fresh copy.
- **Persistence — plain JSON at `%LocalAppData%\Akapen\settings.json`**
  (via `System.Text.Json`), *not* WinAppSDK's
  `Microsoft.Windows.Storage.ApplicationData.GetForUnpackagedAsync(...)`.
  Rationale:
  - the unpackaged `ApplicationData` bring-up needs a `publisher` argument
    whose semantics vary between packaged and unpackaged contexts and
    require an async initialization step that adds start-up complexity to
    a Settings window that would otherwise be synchronous;
  - the settings surface is 5 flat string keys with no nesting, so
    `Utf8JsonWriter` is smaller than any KVS wrapper and lets a user open
    `notepad` on the file to diff or hand-fix it;
  - the JSON path itself is discoverable (Explorer address bar) which
    matches the way `apps/mac`'s @AppStorage lives in a discoverable
    plist under `~/Library/Preferences/`.

  The JSON keys are 1-to-1 with the mac shell's
  `SettingsView.swift::AkapenSettingsKey` enum
  (`output.dirMode` / `output.subfolderName` / `output.fixedDir` /
  `output.flatSuffix` / `output.strokesSuffix`).
- **AppState-shaped resolution layer** (`SettingsStore.cs`): mirrors
  `AppState.swift`'s `resolveOutputDir(input:)` and `resolveOutputNaming()`.
  `TrySave()` calls `SettingsStore.Load()`, `ResolveOutputDir(...)`, then
  `ResolveOutputNaming(...)`; on success the fallback warning (if any) is
  merged into the same StatusText line as the "Saved review for ..."
  message so the success text can't silently clobber it (the same failure
  mode Codex flagged on the mac side during Ch.6 review).
- **UI validation with a two-tier defensive posture**: `SettingsWindow`
  paints each `TextBox`'s `BorderBrush` red (thickness 2) and drops a
  red helper text below when the current value is invalid, using the
  same rules as `SettingsStore.Resolve*`:
  - Subfolder name — non-empty, no `/`, no `\\`, not a bare `.` or `..`
    (after trimming `char.IsWhiteSpace` — Unicode `White_Space` — which
    lines up with Rust's `str::trim` and mac's
    `.whitespacesAndNewlines`).
  - Suffix — non-empty, no `/`, no `\\`, no `.` (same set the Rust
    `sanitize_suffix` rejects, applied here first so the UI shows the
    problem before it reaches the FFI).
  - Fixed directory — non-empty, `Path.IsPathFullyQualified` (handles
    Windows drive letters and UNC), and `Directory.Exists`.

  Values that fail validation *are still saved to disk* (matches mac's
  @AppStorage which never rejects a raw string); the `Resolve*` helpers
  fall back to defaults at save time, and the Rust `sanitize_suffix`
  (`crates/akapen-ffi/src/lib.rs`) is the third and final gate so no
  invalid value ever lands in a filename.
- **FolderPicker HWND initialization**: the `Browse…` button uses
  `Windows.Storage.Pickers.FolderPicker` with
  `WinRT.Interop.InitializeWithWindow.Initialize(...)` on the
  SettingsWindow's own HWND — the same unpackaged-quirk fix
  `MainWindow.OnOpenClick` uses for `FileOpenPicker`. Skipping this
  step throws `NoWindow` at `PickSingleFolderAsync` time.
- **FFI setter integration**: `TrySave` calls
  `NativeMethods.akapen_set_output_naming(engine, flatUtf8, strokesUtf8)`
  immediately before `akapen_export_to_dir`, so a settings change is
  guaranteed to reflect on the very next save (mirrors mac
  `AkapenEngine.setOutputNaming(flatSuffix:strokesSuffix:)` called from
  `AppState.save()` on every save).

## In scope this chapter (M3-A)

- **Full spec §3 shortcut table via `akapen_resolve_key`** (spec §9 M3
  step A): every key-down bubbling up to `RootGrid` is threaded through
  the pure core key map both shells share
  (`crates/akapen-core/src/keymap.rs`), so the shortcut inventory lives
  in exactly one place and both shells honor the same JIS/US recovery,
  IME / text-editing guards, and primary-accelerator semantics (mac Cmd
  == Windows Ctrl == core `primary`, cross-platform rule 1). Mirrors
  `apps/mac/Sources/AkapenApp/CanvasView.swift`'s `keyDown` →
  `resolveAction(for:)` → `dispatch(_)` chain from mac Ch.1.
- **Single dispatch entry** (`MainWindow.xaml.cs::OnRootKeyDown`):
  - Modifier state read via
    `Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread`
    (the WinUI 3 replacement for UWP's `CoreWindow.GetKeyState`).
  - `ch` is a small VK→ASCII table that covers exactly the spec §3
    shortcut inventory (A-Z folded to lowercase, `[` `]` `-` `_` `,`
    `Space`, top-row digits). `VK_OEM_PLUS` is deliberately NOT mapped
    to `^` — that is JIS-specific and would misfire on US layouts (the
    mac Ch.1 keyCode-24 lesson). JIS `^` support waits for a proper
    layout probe in a later chapter.
  - `physical` maps every named `VirtualKey` we care about
    (P/E/U/A/R/O/T/I/X/C/Z/Y, `Number0`, `Space`, `PageUp`, `PageDown`)
    plus the `VK_OEM_4` / `VK_OEM_6` / `VK_OEM_MINUS` OEM codes (no
    named `VirtualKey` members) to their `AKAPEN_PK_*` counterparts.
  - `text_editing` is `IsTextInputFocused()` (`FocusManager` walked
    against the current `XamlRoot`, matching every `TextBox` /
    `RichEditBox` / `PasswordBox`, including the inner `TextBox` an
    `AutoSuggestBox` / editable `ComboBox` composes onto). Pinned into
    `composing` too — WinUI 3 has no cheap "is IME composing" probe
    today and the core key map short-circuits the moment either guard
    is set.
- **Action dispatch table** (`DispatchAction(int action)`):
  - Fanned out to existing M2-D / M2-F entry points so the KeyDown path
    and the toolbar buttons stay in lock-step (including Codex Ch.10's
    dirty-marker rationale that flows through the shared `Undo` /
    `Redo` / `StepFrame` / `NudgeSize` methods): `AKAPEN_ACT_TOOL_PEN`,
    `TOOL_ERASER`, `UNDO`, `REDO`, `BRUSH_SMALLER`, `BRUSH_LARGER`,
    `NEXT_FRAME`, `PREV_FRAME`.
  - Stubs — surface a modest StatusText note (`AnnounceUnimplementedAction`)
    so an M3-A build makes them discoverable rather than silently
    swallowing; real UI lands in M3-B / M3-C / M3-D: `TOOL_LINE`,
    `TOOL_ARROW`, `TOOL_RECT`, `TOOL_ELLIPSE`, `TOOL_TEXT`, `ZOOM_IN`,
    `ZOOM_OUT`, `FIT`, `ACTUAL_SIZE`, `ROTATE_LEFT`, `ROTATE_RIGHT`,
    `SWAP_COLOR`, `EYEDROPPER`, `TRANSPARENT_COLOR`.
- **XAML pre-M3-A KeyboardAccelerators removed** so the KeyDown → core
  key map is the single dispatch path (no risk of double-invoke):
  Ctrl+Z on `UndoButton`, Ctrl+Y / Ctrl+Shift+Z on `RedoButton`,
  PageUp on `PrevButton`, PageDown on `NextButton`. The M2-D interim
  code-behind `RegisterGlobalAccelerators` shim
  (P / E / `[` / `]` / Ctrl+,) is likewise removed. Doc-comments and
  button tooltips still spell each shortcut for discoverability.
- **Shell-side special cases kept**:
  - Ctrl+S (Save) — the core key map returns `AKAPEN_ACT_NONE` for it
    (spec §3: "primary+letter combos other than Z/Y/=/-/0/Space are left
    to the OS/menu"), so routing it through `OnRootKeyDown` would be a
    no-op. `SaveButton.KeyboardAccelerators` in the XAML keeps its
    Ctrl+S accelerator; the tooltip / access-key surface stays natural.
  - Ctrl+, (Settings) — no `AKAPEN_ACT_*` exists for "open Settings"
    (matching mac Cmd+, sitting on SwiftUI's Settings scene rather than
    in `resolveAction`), so `OnRootKeyDown` catches Ctrl+, early with
    the same `IsTextInputFocused` guard and calls
    `EnsureSettingsWindow` directly.

## Not yet in scope (future M2 / M3 chapters)

- **M2-B2**: Wintab (WACOM's native API), the second pen path for drivers
  with "Windows Ink" turned off (spec §5.1). `akapen_palm_route` and the
  pointer-kind/pressure plumbing landed in M2-B1 above; only the Wintab
  fallback input source itself remains.
- **Touch-driven canvas pinch** (the palm gate's `Navigate` routing is wired
  up but touch gesture consumption remains a later refinement). Keyboard
  zoom/fit/actual-size, rotation, and Space+drag pan are part of the Windows
  MVP view state.
- **M3-B / M3-C / M3-D — real UI for the remaining shortcut stubs**:
  extra shape tools (U / A / R / O / T) and the color-model actions (swap
  primary/sub, eyedropper, transparent-color). Zoom / fit / actual-size,
  canvas rotate, and Space+drag pan are wired in the Windows MVP view state.
- **JIS `^` rotation shortcut**: intentionally deferred until a proper
  Windows keyboard-layout probe lands (mac Ch.1's keyCode-24 misfire
  defense applies symmetrically here — `VK_OEM_PLUS` on a US layout must
  never resolve to RotateRight). US `-` + Shift = `_` = RotateRight is
  already reachable via the `VK_OEM_MINUS` character path today.
- **Present-pump graduation**: from a fixed-cadence `DispatcherTimer`
  (started/stopped alongside the loaded engine, M2-A) to an engine-
  dirty-flag-gated or compositor-synced pump, once real profiling data
  says the fixed 16ms tick matters.
- **M3 refine**: SidePanel の hover-fade + フローティング化 (mac の
  `SidePanelView` は `.overlay(alignment: .trailing)` の半透明パネルで、
  ホバー時のみ opacity 0.4→0.97 に戻る)。M2-D は Grid の右列に固定配置
  したので canvas を圧迫するが、実測で邪魔なら refine 対象。
- **M3 color picker**: 任意色ピッカ (WinUI `ColorPicker`) — M2-D は
  10-swatch 固定パレットのみ、mac 側の `ColorPicker` 相当は M3。
- **Settings reset button + project-scoped overrides**: `SettingsWindow`
  ships no "Reset to defaults" affordance and reads/writes exactly one
  location (`%LocalAppData%\Akapen\settings.json`). Spec §4.7's "プロジェ
  クトごとの上書きは将来検討" is left for a later chapter; deleting the
  JSON file by hand is the interim reset path.
- **napi-rs setter exposure**: `akapen_set_output_naming` is not yet
  exposed to the Node binding surface (`bindings/node`); that lift is
  Ch.4 scope. Nothing outside the .NET / SwiftUI shells consumes the
  setter today.
- **MSIX packaging**: distributable installer + auto-update. `WindowsPackageType`
  currently stays `None`; the MVP distributes a framework-dependent publish
  directory/zip and relies on the installed Windows App Runtime.
- **4K perf gate**: spec §6 headline numbers on real hardware, first
  measurable now that M2-B1 lands the real pen path (mouse throughput was
  never the bottleneck).

## Retreat criterion (spec §7.4-4)

WinUI 3 is the first-candidate UI framework. If M2 implementation cost is
dominated by working around WinUI-specific brokenness (SwapChainPanel
lifecycle bugs, input routing, tooling regressions), spec §7.4-4 authorizes
retreat to **WPF (.NET 8) + HwndHost**. The core (Rust + C ABI) does not
change; only the `apps/windows/` shell is rewritten as a WPF app that hosts a
child HWND and uses `kind = AKAPEN_SURFACE_HWND` instead of
`SWAPCHAIN_PANEL`. As of this chapter's scaffold, no such retreat is
warranted — SwapChainPanel bring-up is straightforward and the
`ISwapChainPanelNative` COM contract is stable.

## Build / run

WinUI 3 shells **only build on Windows** — the WindowsAppSDK NuGet package
carries native tooling that is Windows-specific. On mac/Linux this shows up in
two layers, not one:

- A plain `dotnet build`/`dotnet restore` of this project fails immediately
  with `NETSDK1100` ("To build a project targeting Windows on this operating
  system, set the EnableWindowsTargeting property to true") — the .NET SDK
  refuses to even evaluate a `net8.0-windows10.0.19041.0`-targeted project on
  a non-Windows OS by default.
- Passing `-p:EnableWindowsTargeting=true` gets past that guard: restore and
  the generated binding source can be evaluated, but nothing about
  resolving the WindowsAppSDK NuGet packages themselves requires Windows.
  The build then fails for the real reason: WinUI 3's XAML compiler
  (`XamlCompiler.exe`, invoked via
  `Microsoft.UI.Xaml.Markup.Compiler.interop.targets`) is a Windows PE
  binary, so mac/Linux cannot execute it (`cannot execute binary file`) and
  `MainWindow.xaml` never gets compiled.

Either way this is expected; the CI job `apps-windows-shell`
(`.github/workflows/ci.yml`) runs on `windows-latest` and is what actually
proves the build.

### Prerequisite on a bare Windows dev box

The .NET 8 SDK alone is **not enough** to `dotnet build` this project. WinUI
3's MrtCore.PriGen.targets (pulled in unconditionally by the WindowsAppSDK
metapackage) invokes `Microsoft.Build.Packaging.Pri.Tasks.ExpandPriContent`,
whose assembly lives in Visual Studio's "MSIX packaging tools" component —
not in the WindowsAppSDK NuGet itself. On a machine with only the .NET SDK
you will get `MSB4062: could not load … Microsoft.Build.Packaging.Pri.Tasks.dll`
even though this project is unpackaged (`WindowsPackageType=None`) and has
no PRI content to expand.

Install one of:
- **Visual Studio 2022** with the "MSIX packaging tools" workload checked
  (Individual components → SDKs, libraries, and frameworks → *MSIX
  packaging tools*), or
- **Visual Studio Build Tools 2022** with the same component checked (the
  smaller, headless install; matches what CI does).

GitHub Actions `windows-latest` runners include VS 2022 with the necessary
workloads preinstalled, so the CI job "apps-windows-shell" is unaffected.

On the Windows dev box (`.34`, Session 1 required for real DX12 present), the
shortest path is:

```
apps\windows\run-mvp.bat
```

The script builds the Rust FFI, regenerates the shared C# binding, publishes
the framework-dependent x64 WinUI shell, copies `akapen.dll`, and starts the
app. The machine must have the matching Windows App Runtime installed. The
equivalent manual steps are:

```
REM 1. Build the native library and regenerate the C# P/Invoke surface.
cargo build -p akapen-ffi --release
cargo run -p akapen-dotnet-bindgen

REM 2. Build the x64 shell.
dotnet publish apps\windows\AkapenApp\AkapenApp.csproj -c Release -r win-x64 --self-contained false -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -o apps\windows\AkapenApp\publish-win-x64

REM 3. Copy the native DLL next to the shell exe (dotnet build does not do
REM    this for a DLL it did not produce itself; the .NET P/Invoke resolver
REM    finds akapen.dll via the exe's own output directory).
copy target\release\akapen.dll apps\windows\AkapenApp\publish-win-x64\akapen.dll

REM 4. Run (Session 1 required for real GPU present; Session 0 SSH cannot
REM    reach a DX12 swapchain, same limitation apps/windows-probe hits).
apps\windows\AkapenApp\publish-win-x64\AkapenApp.exe
```

Then: `Open…` an image, drag the left mouse button to draw a red stroke,
`Ctrl+S` (or the Save button) to write the 3-file `_review/` set. The status
bar reports open / save results and the reason for any `akapen_render_attach`
failure (via `akapen_render_last_attach_error`).

## Layout

```
AkapenApp/
├─ AkapenApp.csproj                # net8.0-windows10.0.19041.0, unpackaged
├─ App.xaml + .cs                  # WinUI Application entry, creates MainWindow
├─ MainWindow.xaml + .cs           # Toolbar + SwapChainPanel + SidePanel + status bar
├─ SettingsWindow.xaml + .cs       # M2-E: 出力先モード・接尾辞・固定パスの独立設定ウィンドウ
├─ SettingsStore.cs                # M2-E: JSON永続化 + Resolve* ヘルパ (mac AppState.resolve* に相当)
├─ PaletteColors.cs                # MS Paint 10色パレット定義(1箇所集約)
├─ NaturalStringComparer.cs        # StrCmpLogicalW-backed natural sort (M2-F sibling sequence)
├─ SequenceStepper.cs              # M2-F: 連番トークン優先の Prev/Next 隣接解決
├─ Interop/
│  └─ SwapChainPanelNativeInterop.cs  # ISwapChainPanelNative QueryInterface
└─ app.manifest                    # PerMonitorV2 DPI + Windows 10+ compat
```
