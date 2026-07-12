// Akapen main window (spec §7.4-4 / §9 M2 — WinUI 3 shell; M2-A first-cut
// scaffold, M2-B1 real pointer kind/pressure + palm rejection).
//
// Responsibilities kept in this file (mirrors apps/mac/Sources/AkapenApp/
// AppState.swift's role, folded into the window itself since M2-A has no
// side panel yet):
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
//   - Present one frame per DispatcherTimer tick via akapen_render_frame.
//
// Deliberately not here (kept for later M2 chapters, called out in
// apps/windows/README.md so nobody accidentally starts adding them):
//   - Wintab (WACOM's native API, spec §5.1 second route, needed when a
//     driver has "Windows Ink" turned off) — M2-B2.
//   - Touch-driven canvas pan/pinch: the palm gate's Navigate routing is
//     wired up and reachable (a deliberate touch with no pen in play), but
//     nothing consumes it yet beyond a status-bar note — M2-D.
//   - Tool switcher / color picker / size slider / undo-redo UI (M2-D).
//   - Settings pane (spec §4.7) covering output dir mode + suffixes.
//   - Zoom / pan / rotate remap (view scale != 1, non-zero rotation).
//     PushPointerSample inverts the *centered, scale-1* placement
//     OnPresentTick renders (panel DIP size <-> image pixel size), which
//     holds across ordinary window resizes since both sides read the
//     panel's live ActualWidth/Height; it does not attempt zoom/pan, which
//     is M2-D scope.
//   - Frame stepping across sibling images.
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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;

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

    public MainWindow()
    {
        this.InitializeComponent();
        this.Closed += OnWindowClosed;

        // Diagnostic logger routes wgpu-side attach failures to stderr — the
        // .34 SSH loop reads them there when a bring-up fails. Same call as
        // the probe (apps/windows-probe/AkapenProbe/Program.cs:53).
        NativeMethods.akapen_enable_diagnostic_logging();
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

        // Center-in-viewport, 1:1 zoom for M2-A. Zoom / pan / rotate UI is
        // M2-D; the transform shape is what the mac shell also passes on
        // its opening frame (apps/mac/Sources/AkapenApp/CanvasView.swift).
        var view = new AkapenViewTransform
        {
            center_x = _surfaceWidth / 2.0f,
            center_y = _surfaceHeight / 2.0f,
            scale = _scaleFactor,
            rotation_deg = 0.0f,
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
        PushPointerSample(sender, e, phase: 1 /*Move*/);
    }

    private void OnRenderSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        uint id = e.Pointer.PointerId;
        if (!_activePointerIds.Remove(id)) return;
        PushPointerSample(sender, e, phase: 2 /*Up*/);
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
            PushPointerSample(sender, e, phase: 2 /*Up*/);
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
                pressure = point.Properties.Pressure;
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

        // Coordinate mapping (unchanged M2-A math, just computed ahead of the
        // palm gate now — see the bounds-check note below): invert the
        // render surface's centered placement (OnPresentTick's
        // AkapenViewTransform: center = physical surface size / 2, scale =
        // _scaleFactor). Converting that forward mapping from physical
        // pixels back to the DIPs PointerRoutedEventArgs reports cancels the
        // scale factor algebraically, leaving exactly the 1:1-in-DIP inverse
        // below (panel DIP size in, image pixel size out). Reading
        // panel.ActualWidth/Height live (rather than a cached field) keeps
        // this correct across ordinary window resizes too. What is still not
        // handled here is zoom/pan/rotate (view scale != 1 or non-zero
        // rotation) — that remap is M2-D scope (see class doc).
        var panel = (FrameworkElement)sender;
        var pt = positionOverride ?? point.Position;
        double panelW = panel.ActualWidth;
        double panelH = panel.ActualHeight;
        double ex = _imgWidth / 2.0 + (pt.X - panelW / 2.0);
        double ey = _imgHeight / 2.0 + (pt.Y - panelH / 2.0);
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

        LoadImage(file.Path);
    }

    private void LoadImage(string path)
    {
        // First: detach and free any previous engine so we don't leak the
        // wgpu surface's handle-lifetime chain (see the render-detach doc in
        // akapen.h).
        DetachSurface();
        FreeEngine();

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
                SetStatus($"Could not open {Path.GetFileName(path)}.");
                return;
            }
            engine = (IntPtr)raw;
            NativeMethods.akapen_size(raw, &w, &h);

            // Default pose = red pen, 6 px. Mirrors the mac shell's opening
            // pose (apps/mac/Sources/AkapenApp/AppState.swift's applyToolState
            // + PaletteColors.defaultColor).
            NativeMethods.akapen_set_tool(raw, tool: 0 /*Pen*/);
            NativeMethods.akapen_set_color(raw, 0xFF0000FFu);
            NativeMethods.akapen_set_size(raw, 6.0f);
        }

        _engine = engine;
        _currentPath = path;
        _imgWidth = w;
        _imgHeight = h;
        AttachSurfaceIfNeeded();
        // Now that there is something to draw, (re)start the present pump
        // (see the Loaded handler's Low2 note — idle-with-no-engine skips it).
        _presentTimer?.Start();
        SaveButton.IsEnabled = true;
        SetStatus($"{Path.GetFileName(path)} — {w}x{h}");
        // Fresh image, fresh read: a stuck-pressure warning from the previous
        // image shouldn't linger (mirrors AppState.open resetting
        // `pressureWarning = false` on mac).
        SetPressureWarning(false);
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
    }

    // ── Save (Ctrl+S / Save button) ────────────────────────────────────────

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveCurrent();
    }

    private void SaveCurrent()
    {
        if (_currentPath is null || _engine == IntPtr.Zero)
        {
            SetStatus("Nothing to save yet.");
            return;
        }

        // Mirrors the mac shell's default output dir: `<input's folder>/_review/`
        // (spec §4.3). Suffix configuration (§4.7) is a later chapter; here
        // we take the engine's built-in defaults (review / strokes).
        string dir = Path.Combine(Path.GetDirectoryName(_currentPath) ?? ".", "_review");
        string stem = Path.GetFileNameWithoutExtension(_currentPath);

        int rc;
        unsafe
        {
            byte[] dirUtf8 = System.Text.Encoding.UTF8.GetBytes(dir + "\0");
            byte[] stemUtf8 = System.Text.Encoding.UTF8.GetBytes(stem + "\0");
            fixed (byte* pDir = dirUtf8)
            fixed (byte* pStem = stemUtf8)
            {
                rc = NativeMethods.akapen_export_to_dir((AkapenEngine*)_engine, pDir, pStem);
            }
        }

        if (rc == 0)
        {
            SetStatus($"Saved review for {Path.GetFileName(_currentPath)} → {dir}");
        }
        else
        {
            SetStatus($"Save failed ({DescribeExportRc(rc)}). Frame not changed.");
        }
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
        DetachSurface();
        FreeEngine();
        SwapChainPanelNativeInterop.Release(_panelNativePtr);
        _panelNativePtr = IntPtr.Zero;
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
}
