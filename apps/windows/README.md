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
  (`WindowsPackageType=None`) so `dotnet build` / `dotnet run` produce a
  runnable exe without MSIX plumbing.
- Consumes `bindings/dotnet/Akapen.Native` via `ProjectReference` — that is
  the csbindgen-generated internal `NativeMethods` surface every managed
  consumer of `akapen.dll` should share (one C ABI, one binding face).
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
  pressure=1.0)`. `MainWindow.xaml.cs::PushMouseSample` inverts the render
  surface's centered, scale-1 placement back to image pixels (panel DIP size
  <-> image pixel size), so this holds across ordinary window resizes; a
  press outside the displayed image is skipped rather than starting a stroke.
  Zoom / pan / rotate remap is still a later chapter (M2-D).
- Default pen settings on Open: red (`0xFF0000FF`) / 6.0 px, Pen tool. Matches
  the mac shell's opening pose (`PaletteColors.defaultColor`).
- Unpackaged FileOpenPicker requires `WinRT.Interop.InitializeWithWindow`
  with the window's HWND, otherwise `PickSingleFileAsync` throws `NoWindow`;
  that quirk is handled in `MainWindow.xaml.cs::OnOpenClick`.

## Not yet in scope (future M2 chapters)

- **M2-B**: WM_POINTER + Wintab pen path. Replaces the mouse-only stub with
  real pressure / tilt / device-kind.
- **M2-C**: Palm rejection UI (spec §5.2), driving `akapen_palm_route`
  identically to the mac shell's `PalmGate`.
- **M2-D**: Tool switcher (Pen/Eraser), color palette, size slider, undo/
  redo buttons — the equivalent of `SidePanelView` on the mac shell.
- **M3**: Full shortcut table (spec §3), keyed off `akapen_resolve_key` so
  the shortcut map stays in the core (mac and Windows share one table).
  Also where the present pump graduates from a fixed-cadence
  `DispatcherTimer` (started/stopped alongside the loaded engine, M2-A) to
  an engine-dirty-flag-gated or compositor-synced pump, once real profiling
  data says the fixed 16ms tick matters.
- **Settings**: Output-dir mode + suffix configuration (spec §4.7),
  equivalent to `SettingsView.swift` on mac.
- **MSIX packaging**: distributable artifact + auto-update. `WindowsPackageType`
  currently stays `None` so the dev loop is `dotnet build && dotnet run`.
- **4K perf gate**: spec §6 headline numbers on real hardware, first
  measurable once M2-B lands the real pen path (mouse throughput is not the
  bottleneck).

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
  the `Akapen.Native` project reference both succeed — nothing about
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

On the Windows dev box (`.34`, Session 1 required for real DX12 present):

```
REM 1. Build the native library and regenerate the C# P/Invoke surface.
cargo build -p akapen-ffi --release
cargo run -p akapen-dotnet-bindgen

REM 2. Build the shell.
dotnet build apps\windows\AkapenApp\AkapenApp.csproj -c Release

REM 3. Copy the native DLL next to the shell exe (dotnet build does not do
REM    this for a DLL it did not produce itself; the .NET P/Invoke resolver
REM    finds akapen.dll via the exe's own output directory).
copy target\release\akapen.dll apps\windows\AkapenApp\bin\Release\net8.0-windows10.0.19041.0\win-x64\akapen.dll

REM 4. Run (Session 1 required for real GPU present; Session 0 SSH cannot
REM    reach a DX12 swapchain, same limitation apps/windows-probe hits).
dotnet apps\windows\AkapenApp\bin\Release\net8.0-windows10.0.19041.0\win-x64\AkapenApp.dll
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
├─ MainWindow.xaml + .cs           # Toolbar + SwapChainPanel + status bar
├─ Interop/
│  └─ SwapChainPanelNativeInterop.cs  # ISwapChainPanelNative QueryInterface
└─ app.manifest                    # PerMonitorV2 DPI + Windows 10+ compat
```
