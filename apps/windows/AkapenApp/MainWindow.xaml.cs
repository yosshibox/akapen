// Akapen main window (spec §7.4-4 / §9 M2 / §9 M3 — WinUI 3 shell; M2-A
// first-cut scaffold, M2-B1 real pointer kind/pressure + palm rejection,
// M2-D tool / color / size / undo-redo UI, M2-F next/prev frame navigation
// + save-failure data-loss guard, M2-E output-dir mode + suffix settings —
// spec §4.7 — via an independent top-level SettingsWindow and a
// SettingsStore-backed JSON at %LocalAppData%\Akapen\settings.json;
// M3-A: full spec §3 shortcut table routed through akapen_resolve_key so
// both shells share exactly one core key map — the interim per-button
// KeyboardAccelerators for P/E/[/]/Ctrl+Z/Y/Shift+Z/PageUp/PageDown/Ctrl+,
// have been removed and RootGrid.KeyDown is the single dispatch entry).
//
// Responsibilities kept in this file (mirrors apps/mac/Sources/AkapenApp/
// AppState.swift + SidePanelView.swift, folded into the window itself since
// we have no separate view-model layer at M2 scope):
//   - Own the AkapenEngine handle for the current image.
//   - Bring up the WinUI 3 SwapChainPanel as the wgpu render surface
//     (spec §7.4-6) via the ISwapChainPanelNative COM interop in
//     Interop/SwapChainPanelNativeInterop.cs.
//   - Feed Pointer{Pressed,Moved,Released,CaptureLost,Canceled} into akapen_pointer
//     with the real device kind + pressure (spec §5.1: WinUI's
//     PointerDeviceType / PointerPointProperties.Pressure — pen carries a
//     real 0.0-1.0 pressure signal, touch/mouse are pinned to 1.0), routed
//     through the core palm-rejection gate first (spec §5.2,
//     akapen_palm_route — the same state machine the mac shell's PalmGate
//     wraps). Wintab (the "Windows Ink off" fallback pen path, spec §5.1
//     second route) is M2-B2.
//   - Drive Open… (FileOpenPicker) and Save / Ctrl+S (akapen_export_to_dir).
//   - M2-F: step to the previous/next supported-image sibling in the current
//     folder (any of .png/.jpg/.jpeg/.webp/.bmp, matching mac's
//     supportedExts — same-extension token target takes priority via
//     SequenceStepper, natural-sort adjacency is the fallback). Prev/Next
//     toolbar buttons, PageUp/PageDown
//     accelerators — spec §4.5 連番次前, §3 主要ナビ行), auto-saving any
//     unsaved strokes before the step and aborting the step (keeping the
//     current frame) if that save fails — the same data-loss guard the mac
//     shell's AppState.step(forward:) applies before AppState.open(url:).
//     The same guard runs before Open… replaces the current image, and a
//     best-effort (non-blocking) save is attempted on window close, mirroring
//     apps/windows-probe's WM_CLOSE handling (Program.cs's `s_dirty` /
//     `TrySave("on-close")": save but never block exit on failure).
//   - Present one frame per DispatcherTimer tick via akapen_render_frame.
//   - M2-D: hold the current tool (Pen/Eraser) / color (10-swatch MS Paint
//     palette from PaletteColors.cs) / brush size, expose them via toolbar
//     + right-hand SidePanel controls, and push to the engine on every UI
//     change AND once per Open (deferred-apply pattern — mac's
//     AppState.applyToolState). Undo / Redo sit on the toolbar with
//     Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z accelerators. Single-key shortcuts
//     P / E / [ / ] (spec §3 主要行の先取り) are registered
//     programmatically on RootGrid because VirtualKey has no `Oem4`/`Oem6`
//     names for `[`/`]`, so XAML cannot spell them directly.
//
// Deliberately not here (kept for later M2/M3 chapters, called out in
// apps/windows/README.md so nobody accidentally starts adding them):
//   - Wintab (WACOM's native API, spec §5.1 second route, needed when a
//     driver has "Windows Ink" turned off) — M2-B2.
//   - Touch-driven canvas pinch remains a later gesture pass; Space+drag pan,
//     keyboard zoom/fit/rotation, and their inverse coordinate mapping are
//     part of the Windows MVP view state.
//   - Tools other than Pen/Eraser (Line / Arrow / Rect / Ellipse / Text
//     exist in the FFI; M3-A wires their shortcut keys through
//     akapen_resolve_key with a StatusText stub, and the real UI lands in
//     the M3-B/C/D chapters).
//   - Arbitrary-color picker (WinUI ColorPicker) — M3; M2-D only ships the
//     10-swatch fixed palette.
//   - SidePanel の hover-fade / フローティング化 — M3 の refine 候補
//     (README 参照)。M2-D は Grid の右列に固定配置。
//   - Settings のリセットボタン / プロジェクトごとの設定上書き — spec §4.7
//     の「将来検討」に相当。M2-E は「アプリローカルの単一 settings.json」まで。
//
// The engine handle is stored as IntPtr (mirrors apps/windows-probe's
// choice) rather than a raw AkapenEngine* field — C# raw-pointer fields on
// reference-type classes are legal but awkward, and the shell will grow a
// Swift-AkapenKit-shaped managed wrapper in M2-D anyway. Casts to
// AkapenEngine* happen at each call site under an `unsafe` block.
//
// Threading: every akapen_render_* call must stay on the UI thread that
// owns the SwapChainPanel (see crates/akapen-render/src/surface.rs's
// SurfaceKind::SwapChainPanel doc). DispatcherTimer ticks fire on the UI
// thread, and PointerEvents dispatch on the UI thread too, so single-thread
// affinity holds without extra plumbing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Akapen.Native;
using AkapenApp.Interop;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives; // RangeBaseValueChangedEventArgs
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System; // VirtualKey / VirtualKeyModifiers
using Windows.UI; // Color struct (WinUI 3 still uses Windows.UI.Color)
using Windows.UI.Core; // CoreVirtualKeyStates for InputKeyboardSource.GetKeyStateForCurrentThread
using Microsoft.UI; // Colors static class (WinUI 3's; Windows.UI.Colors is UWP-only)

namespace AkapenApp;

public sealed partial class MainWindow : Window
{
    // ── Engine + GPU surface state ─────────────────────────────────────────
    // Non-zero between Open… and the next Open… (or window close). Cast to
    // AkapenEngine* at each DllImport call site.
    private IntPtr _engine;

    // AddRef'd ISwapChainPanelNative pointer for the render surface, obtained
    // via QueryInterface (see SwapChainPanelNativeInterop). Zero when no
    // surface is attached. Released on Unloaded / Closed.
    private IntPtr _panelNativePtr;

    // True after akapen_render_attach returned 0. When false we skip
    // akapen_render_frame (which itself is a no-op then, but skipping avoids
    // a dead DllImport per tick).
    private bool _renderAttached;

    // Cached current file path for status text + save stem.
    private string? _currentPath;

    // Supported-image files in _currentPath's folder (any of
    // SupportedFrameExtensions = .png/.jpg/.jpeg/.webp/.bmp, mixed together —
    // matching mac's AppState.supportedExts), natural-sorted
    // (NaturalStringComparer), including _currentPath itself — the
    // frame-stepping sequence for Prev/Next (spec §4.5). SequenceStepper
    // then prefers the same-extension token neighbor and falls back to
    // natural-sort adjacency. Rebuilt on every successful LoadImage
    // (Open… and Prev/Next alike) since the folder's contents can have
    // changed since the last build. Empty while no image is loaded.
    private readonly List<string> _siblings = new();

    // Whether the current frame has drawing input that is not yet reflected
    // in a `_review/` export. Set the moment a sample reaches akapen_pointer
    // in PushPointerSample — any phase, any device kind (unlike the mac
    // shell's AppState.pointer, which only flips this on a completed stroke's
    // `.up`; setting it a beat earlier here is deliberately conservative for
    // the data-loss guard: marking dirty too early costs an extra harmless
    // auto-save, marking it too late risks losing a stroke). Cleared on a
    // successful TrySave() and on FreeEngine. Drives both the
    // auto-save-before-navigation guard (StepFrame / OnOpenClick / window
    // close) and the window-title dirty marker (UpdateDirtyIndicator).
    private bool _hasUnsavedStrokes;

    // Image pixel size of the currently loaded engine (from akapen_size at
    // Open time). Needed to invert the render surface's centered placement
    // back to image coordinates in PushPointerSample. Zero while no engine is
    // loaded.
    private uint _imgWidth;
    private uint _imgHeight;

    // Physical pixel size the GPU surface was last configured for, so a
    // SizeChanged tick can decide whether to call akapen_render_resize.
    private uint _surfaceWidth;
    private uint _surfaceHeight;

    // Per-tick DispatcherTimer that drives the present pump. Not a
    // CompositionTarget.Rendering hook because DispatcherTimer's cadence is
    // easier to reason about. Only running while _engine is non-zero
    // (LoadImage/FreeEngine Start/Stop it) so an idle window with no image
    // open does not tick at ~60Hz for nothing; a proper dirty-flag or
    // compositor-synced pump is left to M3 (see README).
    private DispatcherTimer? _presentTimer;

    // Pointer IDs currently captured for a potential stroke — a Down that
    // reached PushPointerSample and wasn't rejected as "outside the image".
    // This replaces M2-A's single `_mouseDown` bool with a per-PointerId set
    // because palm rejection's entire point is a pen stroke and a resting
    // palm touch being in contact *at the same time*: a shared boolean would
    // have the palm's Up wrongly end the still-in-progress pen stroke.
    // Moved/Released/CaptureLost consult this set to decide whether to keep
    // routing a given contact; PushPointerSample itself doesn't touch it — it
    // only knows about drawing, not capture bookkeeping. Still one active
    // *drawing* stroke assumption carries over from M2-A (no per-pointer
    // stroke/undo buffer) — genuine simultaneous multi-touch drawing is not
    // in scope here, only "pen draws while a palm rests nearby".
    private readonly HashSet<uint> _activePointerIds = new();

    // Last known panel-space (DIP) position each currently-tracked pointer
    // reported while still live, keyed by PointerId. Read back only when
    // PointerCanceled fires for that pointer, whose own PointerPoint.Position
    // is not reliable per WinRT docs (the contact can already be gone from
    // the digitizer) — see OnRenderSurfacePointerCanceled. Populated by
    // PushPointerSample on every in-bounds sample (Down/Move/Release/
    // Canceled alike) and removed once a pointer's stroke ends (Released/
    // CaptureLost/Canceled, or a Down that never became active), so it never
    // outlives the pointer it describes.
    private readonly Dictionary<uint, Windows.Foundation.Point> _lastPointerPosition = new();

    // Palm-rejection state (spec §5.2) — the same pure core state machine the
    // mac shell's `PalmGate` wraps (apps/mac/Sources/AkapenKit/AkapenEngine.swift).
    // Held as a plain struct field, not reset between images (the pen-
    // priority lock is about physical timing since the pen last lifted, not
    // which image is open), and passed by pointer into akapen_palm_route,
    // which reads and updates it in place. A freshly-constructed MainWindow
    // gets it default-initialized to all-zero (pen_down = 0, lock_active = 0,
    // lock_until_ms = 0), which is exactly AkapenPalmState's documented
    // "no pen seen yet" — no explicit constructor call needed.
    private AkapenPalmState _palmState;

    // Backing DPI scale factor as reported by the SwapChainPanel's XamlRoot
    // at attach time. For M2-A we don't remap on DPI changes (no PMv2
    // reconfigure), the shell logs the initial value.
    private float _scaleFactor = 1.0f;

    // View state shared by rendering and pointer inverse mapping. `_zoom` is
    // image-pixel scale before the backing DPI factor; pan is in physical
    // surface pixels so the render and input paths use identical math.
    private float _zoom = 1.0f;
    private float _rotationDeg;
    private float _panX;
    private float _panY;
    private uint _panPointerId;
    private bool _isPanning;
    private Windows.Foundation.Point _lastPanPoint;
    private const float MinZoom = 0.02f;
    private const float MaxZoom = 20.0f;

    // ── M2-E settings window (spec §4.7) ───────────────────────────────────
    // The independent top-level SettingsWindow the "Settings…" button /
    // Ctrl+, opens. Non-null while open; cleared to null in Closed so a
    // second Ctrl+, always re-opens a fresh window instead of trying to
    // Activate a disposed one. Not a modal — closing it does not touch the
    // MainWindow's engine/surface state, and TrySave always re-reads the
    // saved JSON via SettingsStore.Load() so a change made while the
    // SettingsWindow is open is picked up on the next save (no explicit
    // notification wiring needed).
    private SettingsWindow? _settingsWindow;

    // ── M2-D tool state (deferred-apply) ───────────────────────────────────
    // Shell-owned copies of the current tool / color / brush size. The UI
    // updates these fields on every change and, if an engine is loaded,
    // pushes them via akapen_set_tool/_color/_size. When no engine is loaded
    // (before the first Open, or after Close) the update is silently held
    // here and re-applied on the next Open — the "deferred-apply" pattern
    // that mirrors the mac shell's AppState.applyToolState (called both on
    // Open and on any tool/color/size change in AppState.swift). The
    // opening pose (Pen / MS Paint 赤 #ED1C24 / 10 px): the color matches
    // PaletteColors.Default. The brush-size *range* (1-50, see
    // MinBrushSize/MaxBrushSize below and the Slider's Minimum/Maximum in
    // MainWindow.xaml) matches the mac shell's SidePanel slider
    // (apps/mac/Sources/AkapenApp/SidePanelView.swift, 1...50 mapping) —
    // not its top ContentView slider, which spans 1...80
    // (apps/mac/Sources/AkapenApp/ContentView.swift). The *default value*
    // of 10 px is a Windows-shell-only starting pose, not a match with mac:
    // AppState.swift's `brushSize` opens at 14
    // (apps/mac/Sources/AkapenApp/AppState.swift:17), so the two shells do
    // not open at the same size today (Codex Ch.9 review, Low). Whether to
    // unify the two defaults is left to a later chapter. The XAML defaults
    // (PenToolButton IsChecked=True, Slider Value=10) mirror this field so
    // first paint shows the right chrome even before the shell finishes
    // wiring up event handlers.
    private int _currentTool = 0;                 // 0=Pen, 1=Eraser (akapen.h AKAPEN_TOOL_*)
    private uint _currentColorRgba = PaletteColors.DefaultRgba; // 0xED1C24FF
    private float _currentSize = 10.0f;           // Windows-shell default (mac opens at 14; see comment near the Slider max=50 for the range rationale)
    private string? _currentColorHex = PaletteColors.Default.Hex; // for swatch highlight

    // Backing collection for the color swatch buttons, so HighlightSelectedSwatch
    // can walk them without a live UIElement search each time. Populated once
    // in BuildColorSwatches at construction.
    private readonly List<Button> _swatchButtons = new();

    // Brush-size clamp (mirrors the Slider's Min/Max in MainWindow.xaml and the
    // mac shell's minSize/maxSize in SidePanelView.swift). Kept as constants
    // rather than reading Slider.Minimum/Maximum so the `[`/`]` accelerator
    // handler can clamp before touching the Slider (which itself would clamp,
    // but we also read _currentSize directly in ApplyToolStateToEngine).
    private const float MinBrushSize = 1.0f;
    private const float MaxBrushSize = 50.0f;

    public MainWindow()
    {
        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            // Keep startup failures diagnosable on an unpackaged double-click
            // launch, where an unhandled WinRT XamlParseException otherwise
            // only appears as 0xc000027b in the Application event log.
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Akapen",
                "startup-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, ex.ToString());
            throw;
        }
        this.Closed += OnWindowClosed;

        // Diagnostic logger routes wgpu-side attach failures to stderr — the
        // .34 SSH loop reads them there when a bring-up fails. Same call as
        // the probe (apps/windows-probe/AkapenProbe/Program.cs:53).
        NativeMethods.akapen_enable_diagnostic_logging();

        // Populate the right-hand color palette from PaletteColors. The spec
        // §3 shortcut table itself is handled by the M3-A OnRootKeyDown →
        // akapen_resolve_key path (registered in MainWindow.xaml via
        // KeyDown="OnRootKeyDown" on RootGrid), so there is no interim
        // per-key KeyboardAccelerator registration here anymore.
        BuildColorSwatches();
    }

    // ── SwapChainPanel lifecycle ───────────────────────────────────────────

    private void OnRenderSurfaceLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires when the panel is composited into the visual tree —
        // that is when its underlying WinRT IInspectable exists and can be
        // QI'd. Nothing before this point is safe.
        var panel = (SwapChainPanel)sender;
        _scaleFactor = (float)(panel.XamlRoot?.RasterizationScale ?? 1.0);

        _panelNativePtr = SwapChainPanelNativeInterop.QueryInterfacePointer(panel);
        if (_panelNativePtr == IntPtr.Zero)
        {
            SetStatus("Could not obtain ISwapChainPanelNative — CPU fallback only.");
            return;
        }

        // Re-entrant Loaded (an Unloaded/Loaded cycle, e.g. a parent
        // visibility change, re-QIs a fresh ISwapChainPanelNative*): if an
        // engine is already open from before the Unloaded, reattach its
        // surface now instead of waiting for the next Open… . No-op when
        // there is no engine yet, or a surface is already attached.
        AttachSurfaceIfNeeded();

        // Present pump. Only started when an engine is actually loaded —
        // idling at ~60 Hz with nothing to draw wastes CPU/battery for no
        // visible benefit (a zero-handle akapen_render_frame is a no-op, but
        // ticking the dispatcher at all is not free). LoadImage/FreeEngine
        // start/stop it as the engine comes and goes.
        _presentTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60 Hz upper bound
        };
        _presentTimer.Tick += OnPresentTick;
        if (_engine != IntPtr.Zero)
        {
            _presentTimer.Start();
        }
    }

    private void OnRenderSurfaceUnloaded(object sender, RoutedEventArgs e)
    {
        DetachSurface();
        _presentTimer?.Stop();
        _presentTimer = null;

        // Balance the AddRef QueryInterfacePointer returned in Loaded — an
        // Unloaded without this leaked one COM reference per Unloaded/Loaded
        // cycle, and left a stale pointer for the next Loaded to silently
        // overwrite.
        SwapChainPanelNativeInterop.Release(_panelNativePtr);
        _panelNativePtr = IntPtr.Zero;
    }

    private void OnRenderSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Physical pixels = DIPs × RasterizationScale. WinUI 3 gives us the
        // DIP-space ActualWidth/Height on SwapChainPanel; the render surface
        // configure step takes physical pixels.
        uint newW = (uint)Math.Max(1, Math.Round(e.NewSize.Width * _scaleFactor));
        uint newH = (uint)Math.Max(1, Math.Round(e.NewSize.Height * _scaleFactor));
        if (!_renderAttached || (newW == _surfaceWidth && newH == _surfaceHeight))
        {
            _surfaceWidth = newW;
            _surfaceHeight = newH;
            return;
        }
        _surfaceWidth = newW;
        _surfaceHeight = newH;
        unsafe
        {
            NativeMethods.akapen_render_resize(
                (AkapenEngine*)_engine, newW, newH, _scaleFactor);
        }
    }

    private void OnPresentTick(object? sender, object e)
    {
        if (!_renderAttached || _engine == IntPtr.Zero) return;

        // Keep rendering and pointer input on the same view transform. The
        // center is stored in physical pixels, matching the FFI contract.
        var view = new AkapenViewTransform
        {
            center_x = _surfaceWidth / 2.0f + _panX,
            center_y = _surfaceHeight / 2.0f + _panY,
            scale = _zoom * _scaleFactor,
            rotation_deg = _rotationDeg,
        };
        unsafe
        {
            NativeMethods.akapen_render_frame((AkapenEngine*)_engine, view);
        }
    }

    // ── Pointer input (M2-B1: pen + touch + mouse, palm-rejected) ──────────

    private void OnRenderSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Primary contact only (the mac shell's CanvasView only listens to
        // primary drags too). This one check already generalizes across
        // kinds: WinUI reports IsLeftButtonPressed == true for the primary
        // contact on pen (tip down) and touch alike, not just a mouse's left
        // button. Right/middle-button mouse stays for future context menus.
        var props = e.GetCurrentPoint((UIElement)sender).Properties;
        if (!props.IsLeftButtonPressed) return;

        // Capturing keeps subsequent moves/releases for *this* pointer
        // routing here even if it leaves the panel briefly. Captures are
        // scoped per-PointerId by WinUI, so a pen stroke and a concurrently
        // resting palm touch can each hold their own capture without
        // stepping on each other. CapturePointer can fail (WinRT docs: most
        // often because the pointer was already released by the time this
        // call runs) — its return value used to be ignored, which meant we'd
        // still track this pointer as active and might never see its Up if
        // it wandered off RenderSurface without a real capture backing it.
        uint id = e.Pointer.PointerId;
        bool captured = ((UIElement)sender).CapturePointer(e.Pointer);
        if (!captured)
        {
            // Capture failed (WinRT docs: most often because the pointer was
            // already released by the time this call runs). Skip the palm gate
            // entirely — if we sent Down but never got its Up, palm.rs would
            // hold pen_down forever and start rejecting every touch as palm.
            // The mac shell's send() has no "Down without Up" path either
            // (CanvasView.swift; touchesCancelled maps to phase=.up), so this
            // matches the mac contract: no capture → no stroke lifecycle at
            // all, palm state stays untouched, and a real concurrent pen
            // contact will still arm pen_down through its own successful Down.
            _lastPointerPosition.Remove(id);
            return;
        }

        _activePointerIds.Add(id);
        if (IsKeyDown(VirtualKey.Space) && e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            _isPanning = true;
            _panPointerId = id;
            _lastPanPoint = e.GetCurrentPoint((UIElement)sender).Position;
            return;
        }
        if (!PushPointerSample(sender, e, phase: 0 /*Down*/))
        {
            // Pressed outside the displayed image (see PushPointerSample):
            // don't start a stroke, release just this pointer's capture.
            _activePointerIds.Remove(id);
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        }
    }

    private void OnRenderSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_activePointerIds.Contains(e.Pointer.PointerId)) return;
        if (_isPanning && _panPointerId == e.Pointer.PointerId)
        {
            var current = e.GetCurrentPoint((UIElement)sender).Position;
            _panX += (float)((current.X - _lastPanPoint.X) * _scaleFactor);
            _panY += (float)((current.Y - _lastPanPoint.Y) * _scaleFactor);
            _lastPanPoint = current;
            return;
        }
        PushPointerSample(sender, e, phase: 1 /*Move*/);
    }

    private void OnRenderSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        uint id = e.Pointer.PointerId;
        if (!_activePointerIds.Remove(id)) return;
        if (_isPanning && _panPointerId == id)
        {
            _isPanning = false;
        }
        else
        {
            PushPointerSample(sender, e, phase: 2 /*Up*/);
        }
        _lastPointerPosition.Remove(id);
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void OnRenderSurfacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Alt-tab / window drag / another element steals capture: end that
        // pointer's stroke cleanly so the core's per-pointer buffer doesn't
        // stay open. Only affects the pointer that actually lost capture —
        // any other pointer still tracked in _activePointerIds is untouched.
        uint id = e.Pointer.PointerId;
        if (_activePointerIds.Remove(id))
        {
            if (_isPanning && _panPointerId == id)
            {
                _isPanning = false;
            }
            else
            {
                PushPointerSample(sender, e, phase: 2 /*Up*/);
            }
        }
        _lastPointerPosition.Remove(id);
    }

    private void OnRenderSurfacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        // PointerCanceled can fire in place of Released — WinRT docs call it
        // out as a substitute the system can raise instead (e.g. the contact
        // leaves the digitizer's sensing range, or the OS reclaims it for its
        // own gesture) — mirrors the mac shell's touchesCancelled ->
        // gateTouches(..., phase: .up) (CanvasView.swift:320). Treated
        // exactly like Released so the palm gate's pen_down/lock state and
        // this pointer's capture don't get left open.
        uint id = e.Pointer.PointerId;
        _lastPointerPosition.TryGetValue(id, out var lastPosition);
        bool wasActive = _activePointerIds.Remove(id);
        _lastPointerPosition.Remove(id);
        if (!wasActive) return;

        if (_isPanning && _panPointerId == id)
        {
            _isPanning = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            return;
        }

        // The canceled event's own PointerPoint.Position is not reliable
        // here (the contact can already be gone) — fall back to the last
        // position this pointer reported while it was still live so the
        // stroke's closing Up lands at a sane coordinate instead of
        // whatever (possibly (0,0)) WinUI reports for a canceled contact.
        PushPointerSample(sender, e, phase: 2 /*Up*/, positionOverride: lastPosition);
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    // Returns false only when phase is Down (0) and the press landed outside
    // the displayed image — callers use that to avoid starting a stroke and
    // to undo the pointer capture they just took. This bounds check now runs
    // BEFORE the palm gate (spec §5.2 fix): an out-of-image Down is rejected
    // outright and never reaches akapen_palm_route at all, so it can never
    // leave the core's pen_down/lock state stuck. (Previously the gate ran
    // first: a pen Down outside the image still set pen_down = true there,
    // and since the caller correctly never started a stroke for it, no
    // matching Up was ever delivered to clear that state — pen_down stayed
    // stuck true and every Touch afterward was misrouted as a palm until some
    // *other*, in-bounds pen stroke happened to complete and clear it.) A
    // Down that lands inside the image but the palm gate routes to Navigate
    // or Ignore still returns true (it is an accepted contact, just not a
    // drawing one) so the caller keeps tracking it and still delivers its
    // eventual Up. Move/Release/Canceled always return true (phase != 0
    // never returns false) so an in-progress stroke can be dragged past the
    // image edge and still deliver its Up sample, matching a canvas-edge
    // drag-out feel.
    //
    // positionOverride: used only by OnRenderSurfacePointerCanceled, whose
    // own PointerPoint.Position can be unreliable — the last known live
    // position for that pointer is supplied instead of reading
    // point.Position here.
    private bool PushPointerSample(
        object sender,
        PointerRoutedEventArgs e,
        int phase,
        Windows.Foundation.Point? positionOverride = null)
    {
        if (_engine == IntPtr.Zero) return true;

        var point = e.GetCurrentPoint((UIElement)sender);
        uint pointerId = e.Pointer.PointerId;

        // Pointer-kind classification (spec §5.1): WinUI's PointerDeviceType
        // tells pen/touch/mouse apart; only Pen carries a genuine pressure
        // signal (PointerPointProperties.Pressure, 0.0-1.0). The raw value is
        // passed straight through with no shell-side rounding/clamping, so
        // the core's pressure_stuck detector (§5.4) sees real driver
        // behavior rather than a shell-smoothed one.
        int kind;
        double pressure;
        switch (e.Pointer.PointerDeviceType)
        {
            case PointerDeviceType.Pen:
                kind = 0 /*Pen*/;
                double nativePressure = point.Properties.Pressure;
                // Windows Ink may expose a pen contact with an unusable zero
                // pressure on drivers that do not publish pressure. Keep the
                // MVP drawable with a safe fixed-width fallback; the core's
                // pressure-stuck detector still reports that the signal was
                // not varying, so this is never a silent capability loss.
                pressure = nativePressure > 0.0 && nativePressure <= 1.0
                    ? nativePressure
                    : 1.0;
                break;
            case PointerDeviceType.Touch:
                kind = 1 /*Touch*/;
                pressure = 1.0; // WinUI reports no meaningful touch pressure
                break;
            default:
                kind = 2 /*Mouse*/;
                pressure = 1.0;
                break;
        }

        // Invert the same transform used by OnPresentTick. Pointer positions
        // arrive in DIPs; the FFI view is in physical pixels.
        var panel = (FrameworkElement)sender;
        var pt = positionOverride ?? point.Position;
        double px = pt.X * _scaleFactor;
        double py = pt.Y * _scaleFactor;
        double dx = px - (_surfaceWidth / 2.0 + _panX);
        double dy = py - (_surfaceHeight / 2.0 + _panY);
        double rad = -_rotationDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double rx = dx * cos - dy * sin;
        double ry = dx * sin + dy * cos;
        double effectiveScale = Math.Abs(_zoom * _scaleFactor) < 1e-6
            ? 1.0
            : _zoom * _scaleFactor;
        double ex = _imgWidth / 2.0 + rx / effectiveScale;
        double ey = _imgHeight / 2.0 + ry / effectiveScale;
        bool inBounds = ex >= 0 && ex <= _imgWidth && ey >= 0 && ey <= _imgHeight;

        if (phase == 0 /*Down*/ && !inBounds)
        {
            return false; // pressed outside the image: caller skips the stroke
        }

        if (!positionOverride.HasValue)
        {
            _lastPointerPosition[pointerId] = pt;
        }

        // Palm-rejection gate (spec §5.2): every classified pointer event is
        // routed through the core state machine *before* it can reach
        // akapen_pointer — mirrors the mac shell's `palmGate.route(...)` call
        // just ahead of `send` in CanvasView.swift. `_palmState` is the
        // caller-owned AkapenPalmState the core reads/updates in place;
        // Environment.TickCount64 is a monotonic ms clock, matching what the
        // core's lock-expiry math (now_ms) expects.
        long nowMs = Environment.TickCount64;
        int route;
        unsafe
        {
            fixed (AkapenPalmState* statePtr = &_palmState)
            {
                route = NativeMethods.akapen_palm_route(statePtr, kind, phase, nowMs);
            }
        }
        if (route == 2 /*AKAPEN_ROUTE_IGNORE*/)
        {
            return true; // palm during pen contact/lock: silently dropped
        }
        if (route == 1 /*AKAPEN_ROUTE_NAVIGATE*/)
        {
            // A deliberate touch with no pen in play. Canvas pan/pinch is
            // M2-D scope; for now make it visible instead of silently eating
            // it, once per press (not on every Move — that would spam the
            // status bar across a whole drag).
            if (phase == 0 /*Down*/)
            {
                SetStatus("Touch detected — pan/zoom isn't wired up yet; draw with the pen or mouse.");
            }
            return true;
        }

        // route == 0 (AKAPEN_ROUTE_DRAW): existing M2-A coordinate mapping.
        // Only pen and mouse ever route here — touch always resolves to
        // Navigate or Ignore above and never draws (spec §5.2, palm.rs).
        unsafe
        {
            NativeMethods.akapen_pointer(
                (AkapenEngine*)_engine,
                ex, ey,
                pressure,
                kind,
                phase);
        }

        // M2-F data-loss guard (spec §4.5): the current frame now has
        // drawing input that isn't in a `_review/` export yet. See the
        // _hasUnsavedStrokes field doc for why this fires on every phase
        // rather than gating to Up like the mac shell.
        _hasUnsavedStrokes = true;
        UpdateDirtyIndicator();

        if (phase == 2 /*Up*/)
        {
            // Spec §5.4: surface (never silently swallow) a driver/tablet
            // that fed a constant pressure for the whole stroke. Checked
            // unconditionally on Up regardless of device kind, mirroring
            // AppState.pointer on mac (`pressureWarning = e.pressureStuck` on
            // every `.up`, not gated to kind == pen) — the core's own
            // stuck-detector (engine.rs::commit) is gated on the *drawing
            // tool* (Pen vs Eraser), not the input device, so a mouse-drawn
            // stroke and a pen-drawn stroke are judged by the exact same
            // rule here too.
            bool stuck;
            unsafe
            {
                stuck = NativeMethods.akapen_pressure_stuck((AkapenEngine*)_engine) != 0;
            }
            SetPressureWarning(stuck);
        }
        return true;
    }

    // ── Open (FileOpenPicker) ──────────────────────────────────────────────

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        // In an unpackaged WinUI 3 app, FileOpenPicker is a WinRT type whose
        // implementation needs a window handle so the pick dialog can parent
        // itself. WinRT.Interop.InitializeWithWindow.Initialize is the
        // required incantation; without it the picker throws NoWindow at
        // ShowAsync time. (Packaged apps get this wiring automatically via
        // the manifest's window identity, hence "known unpackaged quirk".)
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");

        StorageFile? file;
        try
        {
            file = await picker.PickSingleFileAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}");
            return;
        }
        if (file == null) return;

        // M2-F data-loss guard (spec §4.5): auto-save the current frame's
        // unsaved strokes before swapping in the new image. Unlike
        // StepFrame, a failed save here does not just skip the transition
        // silently — TrySave() already left a failure reason in StatusText,
        // so append a short note clarifying that Open… itself was aborted
        // and the previous image is still the one on screen.
        if (_hasUnsavedStrokes && !TrySave())
        {
            SetStatus(StatusText.Text + " Kept the current image open.");
            return;
        }

        LoadImage(file.Path);
    }

    private void LoadImage(string path)
    {
        // Open the new image FIRST, before touching the previous engine
        // (Codex Ch.10 review, Low1). The old ordering detached/freed the
        // previous engine up front, so a deleted/corrupt sibling (or any
        // akapen_open_image failure) left the shell with no engine at all —
        // "saved fine, but the current frame just vanished". Mirrors the mac
        // shell's `open(url:)` (AppState.swift), which only reassigns its
        // `engine` property after `AkapenEngine(imagePath:)` succeeds, so a
        // failed open never costs the still-good current frame.
        IntPtr engine;
        uint w = 0, h = 0;
        unsafe
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            AkapenEngine* raw;
            fixed (byte* p = utf8)
            {
                raw = NativeMethods.akapen_open_image(p);
            }
            if (raw == null)
            {
                SetStatus($"Could not open {Path.GetFileName(path)} — keeping current frame.");
                return;
            }
            engine = (IntPtr)raw;
            NativeMethods.akapen_size(raw, &w, &h);
        }

        // New engine is up: only now is it safe to detach/free the previous
        // one (still referenced by _engine at this point) so we don't leak
        // the wgpu surface's handle-lifetime chain (see the render-detach doc
        // in akapen.h).
        DetachSurface();
        FreeEngine();

        _engine = engine;
        _currentPath = path;
        _imgWidth = w;
        _imgHeight = h;
        _zoom = 1.0f;
        _rotationDeg = 0.0f;
        _panX = 0.0f;
        _panY = 0.0f;
        _isPanning = false;
        // Fresh image, fresh read: no unsaved strokes yet (mirrors
        // AppState.open resetting `hasUnsavedStrokes = false` on mac).
        _hasUnsavedStrokes = false;

        // Push the shell-held tool / color / size (M2-D deferred-apply pattern
        // — mirrors AppState.applyToolState on mac, called right after
        // AkapenEngine init in AppState.open). On the very first Open this is
        // PaletteColors.Default / 10 px / Pen; on subsequent Opens it's
        // whatever the user last chose, so tool state survives across images.
        ApplyToolStateToEngine();

        AttachSurfaceIfNeeded();
        // Now that there is something to draw, (re)start the present pump
        // (see the Loaded handler's Low2 note — idle-with-no-engine skips it).
        _presentTimer?.Start();
        SaveButton.IsEnabled = true;
        UndoButton.IsEnabled = true;
        RedoButton.IsEnabled = true;
        SetStatus($"{Path.GetFileName(path)} — {w}x{h}");
        // Fresh image, fresh read: a stuck-pressure warning from the previous
        // image shouldn't linger (mirrors AppState.open resetting
        // `pressureWarning = false` on mac).
        SetPressureWarning(false);

        // M2-F: rebuild the sibling sequence for this folder (spec §4.5) and
        // gate the Prev/Next buttons on the result — done last so it reflects
        // the just-loaded _currentPath.
        BuildSiblings(path);
        UpdateDirtyIndicator();
    }

    // ── M2-F: sibling detection + Prev/Next frame stepping ─────────────────

    // The frame-sequence's supported extensions (spec §4.5). Codex Ch.10
    // review (Low2): this shell's earlier BuildSiblings doc claimed jpg/jpeg
    // must not mix into the same sequence, but §4.5 only asks for the
    // supported images to be natural-sorted — it says nothing about keeping
    // extensions apart. The mac shell already mixes all five
    // (AppState.swift's `supportedExts`); matching that set here keeps both
    // shells' Prev/Next behavior identical instead of Windows silently
    // skipping, say, a `.jpg` sitting next to a folder full of `.png`s.
    private static readonly HashSet<string> SupportedFrameExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };

    /// <summary>
    /// Rescans <paramref name="path"/>'s parent folder for files whose
    /// extension is one of <see cref="SupportedFrameExtensions"/>
    /// (case-insensitive; jpg and jpeg mix into the same sequence — see that
    /// field's doc), natural-sorts them, and stores the result in
    /// <see cref="_siblings"/> (which always includes <paramref name="path"/>
    /// itself). Called from <see cref="LoadImage"/> on every successful
    /// Open… and Prev/Next step, since the folder's contents can change
    /// between one open and the next. Leaves <see cref="_siblings"/> empty
    /// (Prev/Next both disabled) if the folder can't be listed.
    /// </summary>
    private void BuildSiblings(string path)
    {
        _siblings.Clear();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            try
            {
                foreach (string candidate in Directory.GetFiles(dir))
                {
                    if (SupportedFrameExtensions.Contains(Path.GetExtension(candidate)))
                    {
                        _siblings.Add(candidate);
                    }
                }
                _siblings.Sort(NaturalStringComparer.Instance);
            }
            catch (IOException)
            {
                _siblings.Clear();
            }
            catch (UnauthorizedAccessException)
            {
                _siblings.Clear();
            }
        }
        UpdateStepButtonsEnabled();
    }

    /// <summary>
    /// Gates PrevButton/NextButton on whether an engine is loaded and
    /// <see cref="_currentPath"/> sits strictly between the first and last
    /// entries of <see cref="_siblings"/> (spec: first frame disables Prev,
    /// last frame disables Next; a lone file with no siblings disables both).
    /// </summary>
    private void UpdateStepButtonsEnabled()
    {
        bool hasEngine = _engine != IntPtr.Zero;
        int idx = _currentPath is not null ? _siblings.IndexOf(_currentPath) : -1;
        bool hasSiblings = idx >= 0 && _siblings.Count > 1;
        PrevButton.IsEnabled = hasEngine && hasSiblings && idx > 0;
        NextButton.IsEnabled = hasEngine && hasSiblings && idx < _siblings.Count - 1;
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => StepFrame(forward: false);

    private void OnNextClick(object sender, RoutedEventArgs e) => StepFrame(forward: true);

    /// <summary>
    /// Steps to the previous/next entry in <see cref="_siblings"/> (spec
    /// §4.5 連番次前) — preferring the sequence-token target (trailing digit
    /// run ± 1, zero-padding preserved) over plain natural-sort adjacency,
    /// via <see cref="SequenceStepper.Neighbor"/> (Codex Ch.10 review,
    /// Medium2; see that file's doc for why this isn't reached through FFI).
    /// If the current frame has unsaved strokes, auto-saves it first (no
    /// confirmation dialog, per §4.5) — and if that save fails, aborts the
    /// step entirely so the current frame is kept exactly as the data-loss
    /// guard requires (TrySave already left the failure reason in
    /// StatusText). Mirrors AppState.step(forward:) on mac, modulo the
    /// token-vs-adjacency preference mac doesn't implement either.
    /// </summary>
    private void StepFrame(bool forward)
    {
        if (_engine == IntPtr.Zero || _currentPath is null) return;
        // Unchanged pre-check: silently no-op (as before) if the current
        // path has fallen out of _siblings since the last rebuild, rather
        // than surfacing a misleading "already at the first/last frame".
        if (_siblings.IndexOf(_currentPath) < 0) return;

        string? target = SequenceStepper.Neighbor(_currentPath, _siblings, forward);
        if (target is null)
        {
            SetStatus(forward ? "Already at the last frame." : "Already at the first frame.");
            return;
        }

        if (_hasUnsavedStrokes && !TrySave())
        {
            return; // TrySave already set the failure status; frame not changed.
        }

        LoadImage(target);
    }

    // ── GPU surface attach / detach ────────────────────────────────────────

    private void AttachSurfaceIfNeeded()
    {
        if (_engine == IntPtr.Zero || _renderAttached || _panelNativePtr == IntPtr.Zero) return;

        _surfaceWidth = (uint)Math.Max(1, Math.Round(RenderSurface.ActualWidth * _scaleFactor));
        _surfaceHeight = (uint)Math.Max(1, Math.Round(RenderSurface.ActualHeight * _scaleFactor));

        int rc;
        string reason = "unknown";
        unsafe
        {
            var desc = new AkapenSurfaceDesc
            {
                // 2 = AKAPEN_SURFACE_SWAPCHAIN_PANEL (see akapen.h). The 0/1
                // constants are MetalLayer/Hwnd (mac / raw Win32), neither of
                // which applies here.
                kind = 2,
                handle = (void*)_panelNativePtr,
                display = null,
                width = _surfaceWidth,
                height = _surfaceHeight,
                scale_factor = _scaleFactor,
            };
            rc = NativeMethods.akapen_render_attach((AkapenEngine*)_engine, &desc);
            if (rc != 0)
            {
                // Read the real reason string for the status bar. Same size-
                // probe pattern the smoke test uses (Akapen.SmokeTest/Program.cs
                // and apps/windows-probe/AkapenProbe/Program.cs::ReadLastAttachError).
                nuint needed = NativeMethods.akapen_render_last_attach_error(
                    (AkapenEngine*)_engine, null, (nuint)0);
                if (needed > 0)
                {
                    byte[] buf = new byte[(int)needed];
                    fixed (byte* p = buf)
                    {
                        NativeMethods.akapen_render_last_attach_error(
                            (AkapenEngine*)_engine, p, (nuint)buf.Length);
                    }
                    reason = System.Text.Encoding.UTF8.GetString(buf, 0, buf.Length - 1);
                }
            }
        }

        if (rc != 0)
        {
            SetStatus($"GPU attach failed (rc={rc}: {reason}) — CPU fallback only.");
            return;
        }
        _renderAttached = true;
    }

    private void DetachSurface()
    {
        if (_renderAttached && _engine != IntPtr.Zero)
        {
            unsafe
            {
                NativeMethods.akapen_render_detach((AkapenEngine*)_engine);
            }
        }
        _renderAttached = false;
    }

    private void FreeEngine()
    {
        if (_engine != IntPtr.Zero)
        {
            unsafe
            {
                NativeMethods.akapen_free((AkapenEngine*)_engine);
            }
            _engine = IntPtr.Zero;
        }
        // Nothing left to present — stop the pump (Low2: no idle 16ms ticks
        // with no engine loaded).
        _presentTimer?.Stop();
        _currentPath = null;
        _imgWidth = 0;
        _imgHeight = 0;
        SaveButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        RedoButton.IsEnabled = false;
        // M2-F: no engine, no frame sequence, nothing unsaved.
        _hasUnsavedStrokes = false;
        _siblings.Clear();
        PrevButton.IsEnabled = false;
        NextButton.IsEnabled = false;
        UpdateDirtyIndicator();
    }

    // ── Save (Ctrl+S / Save button) ─────────────────────────────────────────

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        TrySave();
    }

    /// <summary>
    /// Writes the 3-file `_review/` export for the current frame (spec §4.3)
    /// and, on success, clears <see cref="_hasUnsavedStrokes"/>. Shared by
    /// the Save button/Ctrl+S, the Prev/Next auto-save-before-step guard
    /// (<see cref="StepFrame"/>), the Open… data-loss guard
    /// (<see cref="OnOpenClick"/>), and the best-effort save on window close
    /// (<see cref="OnWindowClosed"/>) — one save path, one status-text
    /// wording, one failure-code mapping (<see cref="DescribeExportRc"/>) for
    /// all four callers, mirroring how mac's AppState.save() is the single
    /// path AppState.step(forward:) also calls through.
    /// </summary>
    /// <returns>True on success; false if there was nothing to save or the
    /// export failed (StatusText already carries the reason either way).</returns>
    private bool TrySave()
    {
        if (_currentPath is null || _engine == IntPtr.Zero)
        {
            SetStatus("Nothing to save yet.");
            return false;
        }

        // M2-E (spec §4.7): 出力先とファイル名の接尾辞は毎回ディスクの
        // settings.json から読み直す。SettingsWindow は変更をその場で書くので、
        // 開いたままの SettingsWindow から別ウィンドウでも設定が反映される
        // (mac の AppState.save が UserDefaults.standard を毎回読むのと同型)。
        // loadWarning(JSON 破損・I/O 失敗で既定値にフォールバックした旨)は
        // Codex 指摘 Medium 1 対応 — 黙って既定値で export されると、ユーザー
        // は「設定した接尾辞が反映されない」原因を statusText からしか追えない
        // ため、ここでも他の警告と同じ経路に合流させる。
        var (settings, loadWarning) = SettingsStore.Load();
        var (dir, dirWarning) = SettingsStore.ResolveOutputDir(_currentPath, settings);
        var (flatSuffix, strokesSuffix, namingWarning) =
            SettingsStore.ResolveOutputNaming(settings);
        string stem = Path.GetFileNameWithoutExtension(_currentPath);

        // engine の naming を先に上書きしてから export を叩く。null(=既定に
        // 戻す)を渡す運用は SettingsStore 側で常に妥当な値を返しているため
        // 不要 — 有効な UTF-8 バイト列を素直に渡す(mac AppState.save の
        // engine.setOutputNaming(flatSuffix:strokesSuffix:) と同じ順序)。
        // Rust 側 sanitize_suffix が二段目の防衛として同じ規則で弾く。
        int rc;
        unsafe
        {
            byte[] flatUtf8 = System.Text.Encoding.UTF8.GetBytes(flatSuffix + "\0");
            byte[] strokesUtf8 = System.Text.Encoding.UTF8.GetBytes(strokesSuffix + "\0");
            byte[] dirUtf8 = System.Text.Encoding.UTF8.GetBytes(dir + "\0");
            byte[] stemUtf8 = System.Text.Encoding.UTF8.GetBytes(stem + "\0");
            fixed (byte* pFlat = flatUtf8)
            fixed (byte* pStrokes = strokesUtf8)
            fixed (byte* pDir = dirUtf8)
            fixed (byte* pStem = stemUtf8)
            {
                NativeMethods.akapen_set_output_naming(
                    (AkapenEngine*)_engine, pFlat, pStrokes);
                rc = NativeMethods.akapen_export_to_dir((AkapenEngine*)_engine, pDir, pStem);
            }
        }

        if (rc == 0)
        {
            // 成功メッセージに load/dir/naming の各フォールバック警告を合流
            // させる(mac AppState.save の "Saved review for X → /path/ (設定の
            // 固定パスが無効なので _review/ にフォールバック)" と同じ形式)。
            // 単独メッセージだと成功で警告が消えてしまうため、同一 statusText
            // に載せる Codex レビュー指摘への対応も踏襲(loadWarning も同列)。
            string message = $"Saved review for {Path.GetFileName(_currentPath)} → {dir}";
            string? combined = JoinWarnings(loadWarning, dirWarning, namingWarning);
            if (combined is not null)
            {
                message += $" ({combined})";
            }
            SetStatus(message);
            _hasUnsavedStrokes = false;
            UpdateDirtyIndicator();
            return true;
        }

        string failMessage = $"Save failed ({DescribeExportRc(rc)}). Frame not changed.";
        if (loadWarning is not null)
        {
            failMessage += $" ({loadWarning})";
        }
        SetStatus(failMessage);
        return false;
    }

    /// <summary>
    /// null をスキップして "; " で連結する。3件とも null なら null を返す
    /// (呼び出し側が「警告なし」と「空文字列の警告」を区別できるように)。
    /// </summary>
    private static string? JoinWarnings(params string?[] warnings)
    {
        List<string> present = new();
        foreach (string? w in warnings)
        {
            if (w is not null) present.Add(w);
        }
        return present.Count == 0 ? null : string.Join("; ", present);
    }

    private static string DescribeExportRc(int rc) => rc switch
    {
        // Codes mirror akapen_export_to_dir in crates/akapen-ffi/src/lib.rs
        // and AkapenExportError in AkapenKit.AkapenEngine (mac). Kept
        // literal here — a shared descriptor would depend on M5-scope
        // managed error types that don't exist yet.
        1 => "invalid save request",
        2 => "couldn't create _review folder",
        3 => "couldn't encode annotation data",
        4 => "couldn't write review files",
        _ => $"unknown error (code {rc})",
    };

    // ── Window teardown ────────────────────────────────────────────────────

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // M2-F data-loss guard: best-effort save of any unsaved strokes
        // before teardown. Unlike StepFrame/OnOpenClick, a failure here does
        // not block anything — the window is going away either way, mirroring
        // apps/windows-probe's WM_CLOSE handling ("save but never block exit
        // on failure"; see the class doc's Program.cs reference). Must run
        // before DetachSurface/FreeEngine, which invalidate _engine.
        if (_hasUnsavedStrokes)
        {
            TrySave();
        }
        DetachSurface();
        FreeEngine();
        SwapChainPanelNativeInterop.Release(_panelNativePtr);
        _panelNativePtr = IntPtr.Zero;

        // M2-E: SettingsWindow が開きっぱなしなら閉じる(MainWindow が閉じるのに
        // 独立トップレベル Window が残ると WinUI 3 はプロセスを終了しないため —
        // 独立 Window の Close は明示。子ではなくトップレベルなので、
        // MainWindow.Content.XamlRoot に紐付いていない点にも留意)。
        _settingsWindow?.Close();
        _settingsWindow = null;
    }

    // ── M2-E: Settings ウィンドウ起動 ──────────────────────────────────────

    private void OnSettingsClick(object sender, RoutedEventArgs e) => EnsureSettingsWindow();

    /// <summary>
    /// 既に開いていれば <see cref="Microsoft.UI.Xaml.Window.Activate"/> でフォ
    /// ーカスするだけ、無ければ新規に <see cref="SettingsWindow"/> を生成して
    /// Activate する。Closed で参照を null に戻して次回の Ctrl+, が fresh
    /// window を開けるようにしておく。
    /// </summary>
    private void EnsureSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    // Spec §5.4 pressure-stuck warning. Kept in its own TextBlock (see
    // MainWindow.xaml's status-bar comment) so it never fights with
    // save/open messages in StatusText for the same line.
    private void SetPressureWarning(bool warned)
    {
        PressureWarningText.Visibility = warned ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// M2-F dirty marker (spec: kept modest, matching how lightly the mac
    /// shell surfaces this — see its AppState.hasUnsavedStrokes doc, which
    /// has no dedicated UI marker at all). Puts a trailing "•" on the window
    /// title while <see cref="_hasUnsavedStrokes"/> is true, instead of
    /// touching StatusText (which OnSaveClick/StepFrame/OnOpenClick already
    /// use for open/save/failure messages — overloading it here would make
    /// those messages flicker on every completed stroke).
    /// </summary>
    private void UpdateDirtyIndicator()
    {
        string baseTitle = _currentPath is not null
            ? $"Akapen — {Path.GetFileName(_currentPath)}"
            : "Akapen";
        Title = _hasUnsavedStrokes ? baseTitle + " •" : baseTitle;
    }

    // ── M2-D: tool / color / size UI + undo/redo + shortcuts ───────────────
    //
    // Every UI change updates the shell-held _currentTool / _currentColorRgba /
    // _currentSize field first, then hands off to ApplyToolStateToEngine which
    // is a no-op while no engine is loaded (deferred apply — see the class doc
    // block for why this pattern mirrors the mac shell's AppState).

    /// <summary>
    /// Builds the 10-swatch MS Paint palette into <c>ColorSwatchRoot</c> in code
    /// so the XAML side owns only layout and PaletteColors.cs owns the color
    /// values. Two horizontal rows of five swatches each. The initially
    /// selected swatch (<see cref="_currentColorHex"/>) is visually highlighted.
    /// </summary>
    private void BuildColorSwatches()
    {
        const int perRow = 5;
        StackPanel? row = null;
        for (int i = 0; i < PaletteColors.Colors.Count; i++)
        {
            if (i % perRow == 0)
            {
                row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                };
                ColorSwatchRoot.Children.Add(row);
            }

            var pc = PaletteColors.Colors[i];
            // Fixed-size square Button: WinUI's default Button padding is too
            // large for a swatch, so zero the padding/min-size out and rely on
            // the explicit 26x26. `Tag` carries the PaletteColor struct so the
            // click handler doesn't need a per-swatch capture closure.
            var btn = new Button
            {
                Width = 26,
                Height = 26,
                MinWidth = 26,
                MinHeight = 26,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(pc.WinColor),
                BorderBrush = new SolidColorBrush(Colors.Black),
                BorderThickness = new Thickness(1),
                Tag = pc,
            };
            ToolTipService.SetToolTip(btn, pc.Name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, pc.Name);
            btn.Click += OnColorSwatchClick;

            row!.Children.Add(btn);
            _swatchButtons.Add(btn);
        }

        HighlightSelectedSwatch();
    }

    /// <summary>
    /// Walks every swatch button and re-paints its border to indicate whether
    /// it matches <see cref="_currentColorHex"/>. Called after every color
    /// change (including the initial build) so the highlight stays honest.
    /// </summary>
    private void HighlightSelectedSwatch()
    {
        foreach (var btn in _swatchButtons)
        {
            bool selected = btn.Tag is PaletteColor pc && pc.Hex == _currentColorHex;
            btn.BorderBrush = new SolidColorBrush(selected ? Colors.White : Colors.Black);
            btn.BorderThickness = new Thickness(selected ? 3 : 1);
        }
    }

    private void OnColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not PaletteColor pc) return;
        _currentColorRgba = pc.Rgba;
        _currentColorHex = pc.Hex;
        HighlightSelectedSwatch();
        ApplyToolStateToEngine();
    }

    /// <summary>
    /// Pen tool click. The XAML default IsChecked=True on PenToolButton and
    /// false on EraserToolButton means the very first paint shows Pen already
    /// pressed; this handler ensures repeated clicks on Pen keep Pen selected
    /// (ToggleButton's default "click-again toggles off" would otherwise leave
    /// no tool active).
    /// </summary>
    private void OnPenToolClick(object sender, RoutedEventArgs e) => SelectTool(0);

    private void OnEraserToolClick(object sender, RoutedEventArgs e) => SelectTool(1);

    private void SelectTool(int tool)
    {
        _currentTool = tool;
        // Force mutual exclusivity — no way for both to be checked, and the
        // active tool button always ends up checked even if the user clicked
        // the already-checked one (WinUI ToggleButton toggles first, so
        // re-setting IsChecked here overrides that).
        PenToolButton.IsChecked = (tool == 0);
        EraserToolButton.IsChecked = (tool == 1);
        ApplyToolStateToEngine();
    }

    private void OnSizeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        float size = (float)Math.Clamp(e.NewValue, MinBrushSize, MaxBrushSize);
        _currentSize = size;
        // SizeValueText can be null the very first time this fires during XAML
        // layout (Slider's default-Value triggers ValueChanged before the sibling
        // TextBlock has had its x:Name field wired up).
        if (SizeValueText != null)
        {
            SizeValueText.Text = $"{(int)Math.Round(size)} px";
        }
        ApplyToolStateToEngine();
    }

    /// <summary>
    /// Called by the `[` (–1) and `]` (+1) accelerators. Writes back through
    /// the Slider so its thumb + numeric readout stay in sync; the resulting
    /// <see cref="OnSizeSliderChanged"/> callback does the akapen_set_size push.
    /// </summary>
    private void NudgeSize(float delta)
    {
        float next = Math.Clamp(_currentSize + delta, MinBrushSize, MaxBrushSize);
        // Skip a no-op write (at the min/max endpoints, or when the current
        // value already matches after rounding) so we don't spam ValueChanged.
        if (Math.Abs(next - _currentSize) < 0.0001f) return;
        SizeSlider.Value = next;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    /// <summary>
    /// Shared undo path — called by the toolbar UndoButton click and by
    /// the M3-A OnRootKeyDown dispatch of AKAPEN_ACT_UNDO. Kept as a
    /// separate method so both entry points go through exactly the same
    /// dirty-marker arming logic (Codex Ch.10 review, Medium1: undo/redo
    /// changes the composited frame just as much as a drawn stroke does, so
    /// it must arm the same data-loss guard PushPointerSample does —
    /// otherwise "draw → save → undo/redo → Next/Open/close" silently drops
    /// the post-undo/redo state because _hasUnsavedStrokes never got set
    /// back to true).
    /// </summary>
    private void Undo()
    {
        if (_engine == IntPtr.Zero) return;
        unsafe { NativeMethods.akapen_undo((AkapenEngine*)_engine); }
        _hasUnsavedStrokes = true;
        UpdateDirtyIndicator();
    }

    /// <summary>
    /// Shared redo path — see <see cref="Undo"/>'s doc for the shared
    /// dirty-marker arming rationale.
    /// </summary>
    private void Redo()
    {
        if (_engine == IntPtr.Zero) return;
        unsafe { NativeMethods.akapen_redo((AkapenEngine*)_engine); }
        _hasUnsavedStrokes = true;
        UpdateDirtyIndicator();
    }

    /// <summary>
    /// The M2-D deferred-apply push. Silent no-op when no engine is loaded, so
    /// the UI can be adjusted freely before the first Open. Called on every
    /// tool / color / size change and once per <see cref="LoadImage"/> so a
    /// freshly-opened engine starts with the shell's current pose (not the
    /// core's factory defaults).
    /// </summary>
    private void ApplyToolStateToEngine()
    {
        if (_engine == IntPtr.Zero) return;
        unsafe
        {
            var eng = (AkapenEngine*)_engine;
            NativeMethods.akapen_set_tool(eng, _currentTool);
            NativeMethods.akapen_set_color(eng, _currentColorRgba);
            NativeMethods.akapen_set_size(eng, _currentSize);
        }
    }

    // ── M3-A: full spec §3 shortcut table via akapen_resolve_key ──────────
    //
    // Every key-down on RootGrid is threaded through the same pure core
    // key map both shells share (`crates/akapen-core/src/keymap.rs`), so the
    // shortcut table lives in exactly one place and both shells honor the
    // same JIS/US recovery rules, IME/text-editing guards, and primary-
    // accelerator semantics (mac Cmd == Windows Ctrl == core `primary`,
    // cross-platform rule 1). Mirrors apps/mac/Sources/AkapenApp/
    // CanvasView.swift's keyDown → resolveAction(for:) → dispatch(_) chain
    // from mac Ch.1.
    //
    // Wired from MainWindow.xaml via KeyDown="OnRootKeyDown" on RootGrid,
    // which reaches this handler as a routed event bubbling up from whichever
    // focused control the key-down originated on (SwapChainPanel, toolbar
    // button, or Slider — the last one is the reason the Slider's own
    // PageUp/PageDown "big step" behavior gets pre-empted by the core map's
    // NEXT_FRAME/PREV_FRAME action, which is the intended UX). Ctrl+S is
    // deliberately NOT threaded through here (it keeps its per-button
    // KeyboardAccelerator on SaveButton in MainWindow.xaml, since the core
    // key map returns AKAPEN_ACT_NONE for it anyway per spec §3's "primary+
    // letter combos other than Z/Y/=/-/0/Space are left to the OS/menu";
    // routing it through this handler would be a no-op) and Ctrl+, is
    // handled as a shell-side special case here (no matching AKAPEN_ACT_*
    // exists for "open Settings", matching how mac Cmd+, sits on SwiftUI's
    // Settings scene rather than in resolveAction).

    /// <summary>
    /// M3-A KeyDown pipeline: extract ch / physical / modifier flags from the
    /// WinUI 3 <see cref="KeyRoutedEventArgs"/>, hand them to the core key
    /// map (<c>akapen_resolve_key</c>), and dispatch the returned
    /// <c>AKAPEN_ACT_*</c> through <see cref="DispatchAction"/>.
    /// </summary>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;

        // Live modifier state. WinUI 3's KeyRoutedEventArgs exposes only the
        // pressed key itself, not the current Ctrl/Shift/Alt state — so we
        // consult Microsoft.UI.Input's per-thread keyboard-state helper (the
        // WinUI 3 replacement for UWP's Windows.UI.Core.CoreWindow.GetKeyState).
        bool ctrl = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        bool alt = IsKeyDown(VirtualKey.Menu); // VK_MENU == Alt

        bool textEditing = IsTextInputFocused();

        // Shell-side special case: Ctrl+, opens Settings. Deliberately not
        // routed through akapen_resolve_key (the core key map has no
        // matching AKAPEN_ACT_*, mirroring mac's Cmd+, being handled at the
        // SwiftUI Settings-scene level rather than in resolveAction). We
        // still respect the same text-editing guard the core key map applies
        // so a future Text tool's own Ctrl+, isn't hijacked mid-typing.
        if (ctrl && !shift && !alt && (uint)e.Key == 0xBC /*VK_OEM_COMMA*/)
        {
            if (!textEditing)
            {
                EnsureSettingsWindow();
                e.Handled = true;
            }
            return;
        }

        // Route everything else through the core key map. ch/physical are
        // extracted layout-agnostically (physical) plus a small VK→ASCII
        // table for the produced-character path (ch). Composing is
        // conservatively pinned to text_editing because WinUI 3 has no
        // cheap "is IME composing" probe today; that only matters if the
        // core key map ever cares about (composing && !text_editing), which
        // it does not (keymap.rs::resolve returns None the moment either is
        // set), so the pin never changes behavior.
        uint ch = ExtractCharacter(e.Key, shift);
        int physical = ExtractPhysical(e.Key);
        int primary = ctrl ? 1 : 0;
        int shiftFlag = shift ? 1 : 0;
        int altFlag = alt ? 1 : 0;
        int guardFlag = textEditing ? 1 : 0;

        int action = NativeMethods.akapen_resolve_key(
            ch, physical, primary, shiftFlag, altFlag, guardFlag, guardFlag);

        if (action == 0 /*AKAPEN_ACT_NONE*/) return;

        if (DispatchAction(action))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// True while the given key is physically down, per the WinUI 3 per-
    /// thread keyboard-state helper (the modern replacement for UWP's
    /// <c>CoreWindow.GetKeyState</c>). The <c>Down</c> bit of
    /// <see cref="CoreVirtualKeyStates"/> is the pressed-right-now flag;
    /// <c>Locked</c> is for CapsLock/NumLock/ScrollLock and is deliberately
    /// ignored here (a locked Shift would be spelled as "Shift is down"
    /// which is exactly what a keydown while CapsLock is on already looks
    /// like).
    /// </summary>
    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down)
            == CoreVirtualKeyStates.Down;

    /// <summary>
    /// Approximates macOS's <c>charactersIgnoringModifiers</c> for a WinUI 3
    /// <see cref="VirtualKey"/>. WinUI 3 has no direct equivalent —
    /// <see cref="KeyRoutedEventArgs.Key"/> is a raw VK code, not a produced
    /// character — so we hand-build the small VK→ASCII table for exactly the
    /// spec §3 shortcut inventory. A-Z always normalize to lowercase (the
    /// core keymap.rs::resolve_char upper-lower folds internally); the Shift
    /// state is only meaningful for the JIS/US rotate recovery on
    /// <c>VK_OEM_MINUS</c> (bare <c>-</c> → RotateLeft; Shift+<c>-</c> = <c>_</c>
    /// → RotateRight, spec §3.1 の "US 配列は Shift+`-` を代替併設").
    ///
    /// <para><c>VK_OEM_PLUS</c> is deliberately NOT mapped to <c>^</c> here:
    /// the JIS caret and the US `+`/`=` share a physical position but produce
    /// different characters, and misfiring RotateRight on a US `=` is the
    /// same class of bug mac Ch.1 fixed with its keyCode-24 defense
    /// (apps/mac/Sources/AkapenApp/CanvasView.swift). Real JIS `^` support
    /// waits for a proper layout probe (M3-B or later).</para>
    /// </summary>
    private static uint ExtractCharacter(VirtualKey key, bool shift)
    {
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            return (uint)('a' + (int)(key - VirtualKey.A));
        }
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            return (uint)('0' + (int)(key - VirtualKey.Number0));
        }
        if (key == VirtualKey.Space)
        {
            return (uint)' ';
        }
        // OEM keys — Windows.System.VirtualKey has no named members for
        // these, so match on the raw VK values.
        switch ((uint)key)
        {
            case 0xBD /*VK_OEM_MINUS*/:
                return shift ? (uint)'_' : (uint)'-';
            case 0xBC /*VK_OEM_COMMA*/:
                return (uint)',';
            case 0xDB /*VK_OEM_4*/:
                return (uint)'[';
            case 0xDD /*VK_OEM_6*/:
                return (uint)']';
        }
        return 0;
    }

    /// <summary>
    /// VK → AKAPEN_PK_* mapping (mirrors apps/mac/Sources/AkapenApp/
    /// CanvasView.swift's physicalCode(for:) mac Carbon keyCode table). The
    /// physical-key path is the array-independent recovery for keys whose
    /// produced character depends on layout or was swallowed by an IME
    /// (spec §3 cross-platform rule 5). <c>VK_OEM_PLUS</c> intentionally
    /// stays <c>AKAPEN_PK_OTHER</c> for the same US-misfire reason
    /// <see cref="ExtractCharacter"/> refuses to map it to <c>^</c>.
    /// </summary>
    private static int ExtractPhysical(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.P: return 1 /*AKAPEN_PK_P*/;
            case VirtualKey.E: return 2 /*AKAPEN_PK_E*/;
            case VirtualKey.U: return 3 /*AKAPEN_PK_U*/;
            case VirtualKey.A: return 4 /*AKAPEN_PK_A*/;
            case VirtualKey.R: return 5 /*AKAPEN_PK_R*/;
            case VirtualKey.O: return 6 /*AKAPEN_PK_O*/;
            case VirtualKey.T: return 7 /*AKAPEN_PK_T*/;
            case VirtualKey.I: return 8 /*AKAPEN_PK_I*/;
            case VirtualKey.X: return 9 /*AKAPEN_PK_X*/;
            case VirtualKey.C: return 10 /*AKAPEN_PK_C*/;
            case VirtualKey.Z: return 11 /*AKAPEN_PK_Z*/;
            case VirtualKey.Y: return 12 /*AKAPEN_PK_Y*/;
            case VirtualKey.Number0: return 13 /*AKAPEN_PK_DIGIT0*/;
            case VirtualKey.Space: return 14 /*AKAPEN_PK_SPACE*/;
            case VirtualKey.PageUp: return 19 /*AKAPEN_PK_PAGE_UP*/;
            case VirtualKey.PageDown: return 20 /*AKAPEN_PK_PAGE_DOWN*/;
        }
        switch ((uint)key)
        {
            case 0xDB /*VK_OEM_4*/: return 15 /*AKAPEN_PK_BRACKET_LEFT*/;
            case 0xDD /*VK_OEM_6*/: return 16 /*AKAPEN_PK_BRACKET_RIGHT*/;
            case 0xBD /*VK_OEM_MINUS*/: return 17 /*AKAPEN_PK_MINUS*/;
        }
        return 0 /*AKAPEN_PK_OTHER*/;
    }

    /// <summary>
    /// Dispatch table for the AKAPEN_ACT_* code returned by
    /// <c>akapen_resolve_key</c>. Actions the M2-D/M2-F UI already implements
    /// (Pen/Eraser tool switch, Undo/Redo, brush size, next/prev frame) fan
    /// out to those existing entry points so the KeyDown path and the toolbar
    /// buttons stay in lock-step (Codex Ch.10 review's dirty-marker rationale
    /// still holds for all of them via the shared <see cref="Undo"/>/
    /// <see cref="Redo"/>/<see cref="StepFrame"/>/<see cref="NudgeSize"/>
    /// paths). View actions are implemented in the Windows MVP view state;
    /// extra tools and color-model actions still surface a modest StatusText
    /// note until their later M3 UI lands.
    /// </summary>
    /// <returns>True when the action was consumed (KeyDown should mark
    /// <c>e.Handled = true</c>); false when the code fell through the switch
    /// (should not happen for any AKAPEN_ACT_* the core key map returns
    /// today, but leaves room for the enum growing in the future).</returns>
    private bool DispatchAction(int action)
    {
        switch (action)
        {
            // ── Wired to existing M2-D / M2-F entry points ────────────────
            case 1 /*AKAPEN_ACT_TOOL_PEN*/:
                SelectTool(0);
                return true;
            case 2 /*AKAPEN_ACT_TOOL_ERASER*/:
                SelectTool(1);
                return true;
            case 10 /*AKAPEN_ACT_UNDO*/:
                Undo();
                return true;
            case 11 /*AKAPEN_ACT_REDO*/:
                Redo();
                return true;
            case 18 /*AKAPEN_ACT_BRUSH_SMALLER*/:
                NudgeSize(-1.0f);
                return true;
            case 19 /*AKAPEN_ACT_BRUSH_LARGER*/:
                NudgeSize(+1.0f);
                return true;
            case 20 /*AKAPEN_ACT_NEXT_FRAME*/:
                StepFrame(forward: true);
                return true;
            case 21 /*AKAPEN_ACT_PREV_FRAME*/:
                StepFrame(forward: false);
                return true;

            // ── Remaining stubs — real UI lands in M3-B / M3-C / M3-D ───
            case 3 /*AKAPEN_ACT_TOOL_LINE*/:
                return AnnounceUnimplementedAction("Line tool (U)");
            case 4 /*AKAPEN_ACT_TOOL_ARROW*/:
                return AnnounceUnimplementedAction("Arrow tool (A)");
            case 5 /*AKAPEN_ACT_TOOL_RECT*/:
                return AnnounceUnimplementedAction("Rect tool (R)");
            case 6 /*AKAPEN_ACT_TOOL_ELLIPSE*/:
                return AnnounceUnimplementedAction("Ellipse tool (O)");
            case 7 /*AKAPEN_ACT_TOOL_TEXT*/:
                return AnnounceUnimplementedAction("Text tool (T)");
            case 12 /*AKAPEN_ACT_ZOOM_IN*/:
                ZoomBy(1.2f);
                return true;
            case 13 /*AKAPEN_ACT_ZOOM_OUT*/:
                ZoomBy(1.0f / 1.2f);
                return true;
            case 14 /*AKAPEN_ACT_FIT*/:
                FitToWindow();
                return true;
            case 15 /*AKAPEN_ACT_ACTUAL_SIZE*/:
                _zoom = 1.0f;
                _panX = 0.0f;
                _panY = 0.0f;
                SetStatus("View: 100%");
                return true;
            case 16 /*AKAPEN_ACT_ROTATE_LEFT*/:
                _rotationDeg -= 15.0f;
                SetStatus($"View: rotate {_rotationDeg:0}°");
                return true;
            case 17 /*AKAPEN_ACT_ROTATE_RIGHT*/:
                _rotationDeg += 15.0f;
                SetStatus($"View: rotate {_rotationDeg:0}°");
                return true;
            case 22 /*AKAPEN_ACT_SWAP_COLOR*/:
                return AnnounceUnimplementedAction("Swap primary/sub color (X)");
            case 23 /*AKAPEN_ACT_EYEDROPPER*/:
                return AnnounceUnimplementedAction("Eyedropper (I)");
            case 24 /*AKAPEN_ACT_TRANSPARENT_COLOR*/:
                return AnnounceUnimplementedAction("Transparent color (C)");

            default:
                return false;
        }
    }

    /// <summary>
    /// Put a modest StatusText note when a spec §3 shortcut resolves to an
    /// AKAPEN_ACT_* whose real UI is scheduled for M3-B/M3-C/M3-D. Consumes
    /// the key (returns true) so the KeyDown path
    /// marks it handled and it doesn't leak to a Button's auto-Click or the
    /// OS menu.
    /// </summary>
    private bool AnnounceUnimplementedAction(string label)
    {
        SetStatus($"{label}: not implemented yet — coming in a later M3 chapter.");
        return true;
    }

    private void ZoomBy(float factor)
    {
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        SetStatus($"View: {_zoom * 100.0f:0}%");
    }

    private void FitToWindow()
    {
        if (_imgWidth == 0 || _imgHeight == 0 || _surfaceWidth == 0 || _surfaceHeight == 0)
        {
            return;
        }

        float imageW = _imgWidth * _scaleFactor;
        float imageH = _imgHeight * _scaleFactor;
        _zoom = Math.Clamp(Math.Min(_surfaceWidth / imageW, _surfaceHeight / imageH), MinZoom, MaxZoom);
        _panX = 0.0f;
        _panY = 0.0f;
        SetStatus($"View: fit ({_zoom * 100.0f:0}%)");
    }

    /// <summary>
    /// Whether the currently-focused element is a text-editing control
    /// (Codex Ch.9 review, Medium). Fed straight into <c>akapen_resolve_key</c>'s
    /// <c>text_editing</c> parameter so the core key map returns
    /// AKAPEN_ACT_NONE while the user is typing — the same B15/B21 guard the
    /// mac shell's <c>textInputGuards()</c> supplies. AutoSuggestBox and
    /// editable ComboBox compose onto an inner TextBox, so focus already
    /// lands on the TextBox itself and is covered here without a separate
    /// case.
    /// </summary>
    private bool IsTextInputFocused()
    {
        var xamlRoot = this.Content?.XamlRoot;
        if (xamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(xamlRoot);
        return focused is TextBox or RichEditBox or PasswordBox;
    }
}
