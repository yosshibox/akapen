// Akapen Windows MVP: raw Win32 presentation shell over the stable Rust C ABI.
//
// Hand-rolled raw Win32 (user32.dll) window + hand-written DllImport
// declarations mirroring crates/akapen-ffi/include/akapen.h. No WinForms,
// WPF, or WinUI 3 dependency -- this only needs the .NET 8 SDK.
//
// Explicit execution modes share the same host for compatibility:
//   1. Product mode (default): blank raw Win32 shell; no scripted input.
//   2. Presentation smoke (--presentation-smoke): drive a scripted stroke
//      through akapen_pointer for >=300 frames across >=3 resizes with no
//      crash, print backend / present mode / max frame latency, exit 0/1 on
//      pass/fail. This is what the Session 0 -friendly SSH-only check runs.
//   3. Interactive mode (--interactive / image path):
//      open, draw with mouse/pen, pan/zoom/rotate, undo/redo, navigate sibling
//      images, and export the 3-file _review/ set. It deliberately has no
//      Windows App SDK dependency, so a self-contained publish is portable.
//
// Accept criteria for mode 1 (unchanged, reported on stdout):
//   - present succeeds for >= 300 frames with no crash
//   - >= 3 resizes survived without error
//   - actual backend / present mode / max frame latency, read back from the
//     engine itself (crates/akapen-render/src/canvas.rs::backend_info)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AkapenProbe.Ui;

namespace AkapenProbe;

internal static class Program
{
    // ── Shared state for WndProc ──────────────────────────────────────────
    //
    // WndProc has to be a static method (its function pointer is handed to
    // RegisterClassEx), so the pieces the message handlers need -- the engine
    // handle, the pen defaults, the "user is dragging" flag, the save target
    // -- live as static fields rather than being closed over. Set once from
    // Main before ShowWindow, cleared before akapen_free so a stray late
    // WM_MOUSEMOVE cannot dereference a freed engine.
    private static IntPtr s_engine = IntPtr.Zero;
    private static string s_outDir = "";
    private static string s_transientOutDir = "";
    private static string? s_customOutDir;
    private static string s_stem = "probe";
    private static readonly HashSet<string> s_transientArtifacts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_transientDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static bool s_leftDown;
    private static bool s_dirty;
    private static bool s_isSaving;
    private static bool s_isLoading;
    private static UiCommandId s_activeTool = UiCommandId.Pen;
    private static IntPtr s_mainWindow = IntPtr.Zero;
    private static IntPtr s_canvasWindow = IntPtr.Zero;
    private static string s_imagePath = "";
    private static readonly List<string> s_imageSiblings = new();
    private static int s_imageSiblingIndex = -1;
    private static readonly object s_preloadLock = new();
    private readonly record struct CachedImage(IntPtr Engine, long EstimatedBytes);
    private static readonly Dictionary<string, CachedImage> s_preloadedImages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_sessionDocuments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_dirtySessionDocuments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_preloadInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> s_preloadOrder = new();
    private static readonly SemaphoreSlim s_preloadSlots = new(4, 4);
    private static readonly long s_preloadBudgetBytes = Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 3, 1L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024);
    private static long s_preloadedBytes;
    private static bool s_lastNavigationForward = true;
    private static bool s_shuttingDown;
    private static uint s_imageW = 800, s_imageH = 600;
    private static float s_zoom = 1.0f, s_rotationDeg;
    private static float s_brushSize = 14.0f;
    private static uint s_color = 0xED1C24FFu;
    private static PressureCurve s_pressureCurve = PressureCurve.Normal;
    private static float s_panX, s_panY;
    private static bool s_panDown;
    private static int s_lastPanX, s_lastPanY;
    private static DateTime s_lastPenInputUtc = DateTime.MinValue;
    private static bool s_pressureFallbackWarned;
    private static bool s_automatedSmoke;
    private static DockSide s_dockSide = DockSide.Right;
    private static string s_settingsPath = UiSettingsStore.DefaultPath();
    private static CanvasBackdrop s_canvasBackdrop = CanvasBackdrop.White;
    private static bool s_autoSaveOnNavigate = true;
    private static SaveLocationMode s_saveLocationMode = SaveLocationMode.SiblingSubfolder;
    private static string s_outputFolderName = "_review";
    private static string s_customOutputPath = "";
    private static uint s_touchPointerId;
    private static bool s_hasInterpolatedPoint;
    private static double s_lastInterpolatedX, s_lastInterpolatedY, s_lastInterpolatedPressure;
    private static bool s_touchPanDown;
    private static int s_lastTouchX, s_lastTouchY;
    private static readonly Dictionary<uint, POINT> s_touchPoints = new();
    private static double s_lastPinchDistance;
    private static readonly HashSet<uint> s_activePenPointers = new();
    private static IntPtr s_tooltip = IntPtr.Zero;
    private static IntPtr s_brushCursor = IntPtr.Zero;
    private static int s_brushCursorDiameter;
    private static uint s_brushCursorColor;
    private static IntPtr s_settingsDialog = IntPtr.Zero;
    private static IntPtr s_settingsOwner = IntPtr.Zero;
    private static bool s_productMode;
    private static IntPtr s_productMenu = IntPtr.Zero;
    private static UiCommandId? s_hoverCommand;
    private static bool s_draggingBrushFader;
    private static bool s_brushFaderFocused;
    private static KeymapPresetKind s_keymapPreset = KeymapPresetKind.Photoshop;
    private static bool s_pressureEnabled = true;
    // Navigator (V1.1): cached thumbnail (top-down BGRA rows for GDI+) and the
    // click/drag-to-pan state.
    private static byte[]? s_navThumbBgra;
    private static int s_navThumbW, s_navThumbH;
    private static bool s_draggingNavigator;
    private static uint s_lastRenderWidth, s_lastRenderHeight;
    private static string LogPrefix => s_productMode ? "[akapen]" : "[probe]";
    private static readonly WndProcDelegate s_settingsWndProc = SettingsWndProc;
    private static readonly WndProcDelegate s_canvasWndProc = CanvasWndProc;

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2
        s_productMode = LaunchMode.IsProduct(args);
        Console.WriteLine(s_productMode ? "[akapen] Akapen Windows product" : "[probe] Akapen Windows HWND/DX12 presentation de-risk probe");
        akapen_enable_diagnostic_logging();

        bool headlessExport = false;
        bool presentationSmoke = false;
        string? imagePath = null;
        string? cliOutDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--interactive" || a == "-i")
            {
            }
            else if (a == "--presentation-smoke")
            {
                presentationSmoke = true;
            }
            else if (a == "--interactive-smoke")
            {
                s_automatedSmoke = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) cliOutDir = args[++i];
            }
            else if (a == "--headless-export")
            {
                headlessExport = true;
                // next positional (if any) is the output directory
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    cliOutDir = args[++i];
                }
            }
            else if (imagePath is null)
            {
                imagePath = a;
            }
            else if (cliOutDir is null)
            {
                cliOutDir = a;
            }
        }

        try
        {
            // Explorer may launch Akapen with a folder as the first argument.
            // Resolve it before the native image decoder sees a directory path.
            if (!headlessExport && !presentationSmoke &&
                !string.IsNullOrWhiteSpace(imagePath) && Directory.Exists(imagePath))
            {
                ImageFolderSelectionResult selection = ImageFolderSelection.FindFirstSupportedImage(
                    imagePath,
                    Comparer<string>.Create(StrCmpLogicalW));
                if (selection.ErrorMessage != null)
                    AppDiagnostics.Write("explorer-launch", $"{imagePath}: {selection.ErrorMessage}");
                imagePath = selection.ImagePath;
            }
            if (headlessExport) return RunHeadlessExport(imagePath, cliOutDir);
            if (presentationSmoke) return RunSmokeTest();
            // Product startup is intentionally a quiet interactive shell. Smoke
            // and export remain explicit developer modes and are unreachable
            // from the normal no-argument product path.
            return RunInteractive(imagePath, cliOutDir);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("unhandled", ex);
            Console.Error.WriteLine($"{LogPrefix} FAIL: unhandled exception: {ex}");
            return 2;
        }
    }

    // ── Mode 3: headless-export ───────────────────────────────────────────
    //
    // No window, no swapchain, no render_attach. Just: create engine, draw a
    // recognizable stroke pattern via akapen_pointer, then akapen_export_to_dir
    // to write the 3-file _review/ triplet. Purpose: prove the akapen.dll +
    // C ABI + core stroke → CPU raster bake → composite → PNG-encode path works
    // on Windows from a Session 0 SSH shell (where the interactive DX12 window
    // path cannot reach the desktop compositor). Note: this exercises
    // `raster::bake_stroke` (the CPU path used by export), NOT the GPU-side
    // `tessellate_stroke` — the on-screen wet-ink pipeline still needs Mode 2
    // in Session 1 to be witnessed. Failure messages go to stderr; exit codes:
    // 2=unhandled exception, 5=engine bring-up null, 6=export non-zero rc.
    private static int RunHeadlessExport(string? imagePath, string? cliOutDir)
    {
        string outDir = cliOutDir ?? Path.Combine(Path.GetTempPath(), "akapen-review");
        string stem = !string.IsNullOrEmpty(imagePath)
            ? SanitizeStem(Path.GetFileNameWithoutExtension(imagePath))
            : "headless";
        Console.WriteLine($"[probe] headless-export mode; out='{outDir}' stem='{stem}'");

        uint w = 800, h = 600;
        IntPtr engine;
        if (!string.IsNullOrEmpty(imagePath))
        {
            engine = akapen_open_image(imagePath);
            if (engine == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[probe] akapen_open_image failed for '{imagePath}'; falling back to blank {w}x{h}");
                engine = akapen_new(w, h);
            }
            else
            {
                akapen_size(engine, out w, out h);
                Console.WriteLine($"[probe] opened image {imagePath} ({w}x{h})");
            }
        }
        else
        {
            engine = akapen_new(w, h);
        }
        if (engine == IntPtr.Zero)
        {
            Console.Error.WriteLine("[probe] FAIL: engine bring-up returned null");
            return 5;
        }

        akapen_set_tool(engine, 0);            // Pen
        akapen_set_color(engine, 0xFF0000FFu); // opaque red
        akapen_set_size(engine, 16.0f);

        // Draw a signature: a diagonal (top-left → bottom-right) crossed by a
        // second diagonal (bottom-left → top-right). Both taper (pressure
        // varies) so the pressure→width path is exercised too. The pattern
        // makes it obvious the render actually ran (as opposed to an
        // accidental blank).
        double W = w, H = h;
        DrawStroke(engine,
            (0.10 * W, 0.15 * H, 0.4),
            (0.50 * W, 0.50 * H, 0.9),
            (0.90 * W, 0.85 * H, 0.6));
        DrawStroke(engine,
            (0.10 * W, 0.85 * H, 0.4),
            (0.50 * W, 0.50 * H, 0.9),
            (0.90 * W, 0.15 * H, 0.6));
        // Vertical baseline through center, thin.
        s_brushSize = 6.0f;
        akapen_set_size(engine, s_brushSize);
        DrawStroke(engine,
            (0.50 * W, 0.10 * H, 0.6),
            (0.50 * W, 0.50 * H, 1.0),
            (0.50 * W, 0.90 * H, 0.6));

        int rc = akapen_export_to_dir(engine, outDir, stem);
        akapen_free(engine);
        if (rc != 0)
        {
            Console.Error.WriteLine($"[probe] FAIL: akapen_export_to_dir returned {rc}");
            return 6;
        }
        Console.WriteLine($"[probe] PASS: saved 3 files to '{outDir}' with stem '{stem}'");
        Console.WriteLine($"[probe]   {stem}.review.png / {stem}.strokes.png / {stem}.strokes.json");
        return 0;
    }

    private static void DrawStroke(IntPtr engine,
        (double x, double y, double p) a,
        (double x, double y, double p) b,
        (double x, double y, double p) c)
    {
        akapen_pointer(engine, a.x, a.y, a.p, /*kind=Mouse*/ 2, /*phase=Down*/ 0);
        akapen_pointer(engine, b.x, b.y, b.p, 2, /*Move*/ 1);
        akapen_pointer(engine, c.x, c.y, c.p, 2, /*Move*/ 1);
        akapen_pointer(engine, c.x, c.y, c.p, 2, /*Up*/ 2);
    }

    // ── Explicit mode: scripted presentation smoke test ──────────────────
    //
    // Unchanged in intent from the pre-interactive probe: create a window,
    // attach the engine, feed a scripted 4-point stroke every ~80 frames,
    // resize the swapchain three times mid-run, and report pass/fail on
    // stdout. Kept as the default so the existing SSH-only accept-criteria
    // check is now opt-in via --presentation-smoke and never runs on product startup.
    private static int RunSmokeTest()
    {
        const int initialW = 800;
        const int initialH = 600;
        const int totalFrames = 320;
        int[] resizeAtFrames = { 80, 160, 240 };
        int[] strokeStartFrames = { 10, 90, 170, 250 };

        var wndProc = new WndProcDelegate(WndProc); // keep the delegate rooted
        IntPtr hInstance = GetModuleHandle(null);
        if (!RegisterProbeWindowClass(wndProc, hInstance, "AkapenProbeWndClass"))
        {
            return 3;
        }

        IntPtr hwnd = CreateWindowEx(
            0,
            "AkapenProbeWndClass",
            "Akapen Windows Probe",
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT,
            initialW, initialH,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[probe] FAIL: CreateWindowEx failed, Win32Error={Marshal.GetLastWin32Error()}");
            return 4;
        }
        Console.WriteLine($"[probe] created HWND=0x{hwnd.ToInt64():X}");

        ShowWindow(hwnd, SW_SHOWNORMAL);
        UpdateWindow(hwnd);

        GetClientRect(hwnd, out RECT rect);
        uint clientW = (uint)Math.Max(1, rect.right - rect.left);
        uint clientH = (uint)Math.Max(1, rect.bottom - rect.top);
        Console.WriteLine($"[probe] initial client size = {clientW}x{clientH}");

        IntPtr engine = akapen_new(clientW, clientH);
        if (engine == IntPtr.Zero)
        {
            Console.WriteLine("[probe] FAIL: akapen_new returned null");
            return 5;
        }
        akapen_set_tool(engine, 0);            // Pen
        akapen_set_color(engine, 0xFF0000FFu); // opaque red
        akapen_set_size(engine, 24.0f);        // wide stroke so the scripted
                                               // wet-ink path is visible in
                                               // the smoke test.

        var desc = new AkapenSurfaceDesc
        {
            kind = AKAPEN_SURFACE_HWND,
            handle = hwnd,
            display = IntPtr.Zero,
            width = clientW,
            height = clientH,
            scale_factor = 1.0f,
        };
        int attachRc = akapen_render_attach(engine, ref desc);
        if (attachRc != 0)
        {
            string reason = ReadLastAttachError(engine);
            Console.WriteLine($"[probe] FAIL: akapen_render_attach returned {attachRc} ({DescribeAttachCode(attachRc)}); reason: {reason}");
            akapen_free(engine);
            DestroyWindow(hwnd);
            return 6;
        }
        Console.WriteLine($"[probe] akapen_render_attach OK; {ReadBackendInfo(engine)}");

        int framesRendered = 0;
        int resizesDone = 0;
        uint curW = clientW, curH = clientH;

        for (int f = 0; f < totalFrames; f++)
        {
            PumpMessages();

            if (Array.IndexOf(strokeStartFrames, f) >= 0)
            {
                akapen_pointer(engine, curW * 0.25, curH * 0.25, 0.3, /*kind=Mouse*/ 2, /*phase=Down*/ 0);
            }
            else if (Array.Exists(strokeStartFrames, s => s + 1 == f))
            {
                akapen_pointer(engine, curW * 0.5, curH * 0.5, 0.6, 2, /*Move*/ 1);
            }
            else if (Array.Exists(strokeStartFrames, s => s + 2 == f))
            {
                akapen_pointer(engine, curW * 0.75, curH * 0.75, 1.0, 2, /*Move*/ 1);
            }
            else if (Array.Exists(strokeStartFrames, s => s + 3 == f))
            {
                akapen_pointer(engine, curW * 0.75, curH * 0.75, 1.0, 2, /*Up*/ 2);
            }

            if (Array.IndexOf(resizeAtFrames, f) >= 0)
            {
                (curW, curH) = resizesDone % 2 == 0 ? ((uint)500, (uint)400) : ((uint)900, (uint)700);
                bool moved = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)curW, (int)curH, SWP_NOZORDER | SWP_NOMOVE);
                if (!moved)
                {
                    Console.WriteLine($"[probe] WARN: SetWindowPos failed at frame {f}, Win32Error={Marshal.GetLastWin32Error()}");
                }
                GetClientRect(hwnd, out RECT r2);
                uint newClientW = (uint)Math.Max(1, r2.right - r2.left);
                uint newClientH = (uint)Math.Max(1, r2.bottom - r2.top);
                akapen_render_resize(engine, newClientW, newClientH, 1.0f);
                resizesDone++;
                Console.WriteLine($"[probe] resize #{resizesDone} at frame {f}: requested {curW}x{curH}, client now {newClientW}x{newClientH}");
                curW = newClientW;
                curH = newClientH;
            }

            var view = new AkapenViewTransform
            {
                center_x = curW / 2.0f,
                center_y = curH / 2.0f,
                scale = 1.0f,
                rotation_deg = 0.0f,
            };
            akapen_render_frame(engine, view);
            framesRendered++;
        }

        Console.WriteLine($"[probe] rendered {framesRendered} frames, {resizesDone} resizes, akapen_render_available={akapen_render_available(engine)}");
        Console.WriteLine($"[probe] final {ReadBackendInfo(engine)}");

        akapen_render_detach(engine);
        akapen_free(engine);
        DestroyWindow(hwnd);
        GC.KeepAlive(wndProc);

        bool pass = framesRendered >= 300 && resizesDone >= 3;
        Console.WriteLine(pass
            ? "[probe] PASS: present succeeded for >=300 frames across >=3 resizes with no crash"
            : "[probe] FAIL: did not meet the frame/resize acceptance bar");
        return pass ? 0 : 1;
    }

    // ── Mode 2: interactive draw + Ctrl+S export ──────────────────────────
    //
    // Windows MVP interactive shell. The drawing coordinate system is kept in
    // image pixels and mapped through a small, UI-independent view transform.
    private static int RunInteractive(string? imagePath, string? cliOutDir)
    {
        UiSettings settings = UiSettingsStore.Load(s_settingsPath);
        s_dockSide = UiSettingsStore.ParseDockSide(settings.DockSide);
        s_canvasBackdrop = UiSettingsStore.ParseBackdrop(settings.CanvasBackdrop);
        s_autoSaveOnNavigate = settings.AutoSaveOnNavigate;
        s_saveLocationMode = UiSettingsStore.ParseSaveLocation(settings.SaveLocationMode);
        s_outputFolderName = SanitizeFolderName(settings.OutputFolderName);
        s_customOutputPath = settings.CustomOutputPath ?? "";
        s_keymapPreset = UiSettingsStore.ParseKeymapPreset(settings.KeymapPreset);
        s_pressureEnabled = settings.PressureEnabled;
        // 1. Resolve save target. Images save beside the source in _review;
        //    blank canvases use a temp folder unless the caller specifies one.
        s_imagePath = imagePath ?? "";
        s_customOutDir = cliOutDir;
        SetOutputDirectory(cliOutDir ?? (!string.IsNullOrEmpty(imagePath)
            ? ResolveOutputDirectory(imagePath)
            : Path.Combine(Path.GetTempPath(), "akapen-review")));
        s_stem = !string.IsNullOrEmpty(imagePath)
            ? SanitizeStem(Path.GetFileNameWithoutExtension(imagePath))
            : (s_productMode ? "akapen" : "probe");
        string logPrefix = s_productMode ? "[akapen]" : "[probe]";
        string windowClass = s_productMode ? "AkapenWndClass" : "AkapenProbeInteractiveWndClass";
        string windowTitle = s_productMode
            ? "Akapen"
            : "Akapen Probe (interactive) - Ctrl+S to save, close to auto-save";
        Console.WriteLine($"{logPrefix} {(s_productMode ? "product mode" : "interactive mode")}; save target dir='{s_outDir}' stem='{s_stem}'");

        // 2. Bring up the engine (image if given, blank white otherwise). We
        //    do this BEFORE creating the window so the window's client size
        //    can match the image's natural size and pointer coords land in
        //    engine pixels without a coordinate remap. akapen_size is only
        //    valid on a live engine.
        uint imgW = 800, imgH = 600;
        IntPtr engine;
        if (!string.IsNullOrEmpty(imagePath))
        {
            engine = akapen_open_image(imagePath);
            if (engine == IntPtr.Zero)
            {
                Console.WriteLine($"{LogPrefix} akapen_open_image failed for '{imagePath}'; falling back to blank 800x600");
                engine = akapen_new(imgW, imgH);
            }
            else
            {
                akapen_size(engine, out imgW, out imgH);
                Console.WriteLine($"{LogPrefix} opened image {imagePath} ({imgW}x{imgH})");
            }
        }
        else if (!s_productMode)
        {
            engine = akapen_new(imgW, imgH);
        }
        else
        {
            engine = IntPtr.Zero;
        }
        if (engine == IntPtr.Zero && !s_productMode)
        {
            Console.Error.WriteLine("[akapen] WARN: engine bring-up returned null; starting an empty canvas shell");
        }
        s_engine = engine;
        s_mainWindow = IntPtr.Zero;

        // Defaults match the mac shell's red-ink pen (spec §5.1).
        if (engine != IntPtr.Zero)
        {
            ApplyStyle(engine);
        }

        s_imageW = imgW; s_imageH = imgH; s_zoom = 1.0f; s_rotationDeg = 0; s_panX = 0; s_panY = 0;

        // 3. Register class + create window sized to the image's natural size.
        var wndProc = new WndProcDelegate(WndProc);
        IntPtr hInstance = GetModuleHandle(null);
        if (!RegisterWindowClass(wndProc, hInstance, windowClass))
        {
            if (engine != IntPtr.Zero) akapen_free(engine);
            s_engine = IntPtr.Zero;
            return 3;
        }

        int initialClientW = s_productMode ? Math.Max(720, (int)imgW) : (int)imgW;
        int initialClientH = s_productMode ? Math.Max(480, (int)imgH + DockLayout.StatusHeight) : (int)imgH;
        var rc = new RECT { left = 0, top = 0, right = initialClientW, bottom = initialClientH };
        uint mainWindowStyle = WS_OVERLAPPEDWINDOW | (s_productMode ? WS_CLIPCHILDREN : 0);
        AdjustWindowRect(ref rc, mainWindowStyle, s_productMode);
        int winW = rc.right - rc.left;
        int winH = rc.bottom - rc.top;

        s_productMenu = s_productMode ? CreateProductMenu() : IntPtr.Zero;
        IntPtr hwnd = CreateWindowEx(
            0,
            windowClass,
            windowTitle,
            mainWindowStyle,
            CW_USEDEFAULT, CW_USEDEFAULT,
            winW, winH,
            IntPtr.Zero, s_productMenu, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"{LogPrefix} FAIL: CreateWindowEx failed, Win32Error={Marshal.GetLastWin32Error()}");
            if (engine != IntPtr.Zero) akapen_free(engine);
            s_engine = IntPtr.Zero;
            return 4;
        }
        s_mainWindow = hwnd;
        if (s_productMode && engine != IntPtr.Zero)
        {
            if (!CreateProductCanvas(hwnd, hInstance))
            {
                Console.Error.WriteLine("[akapen] FAIL: could not create the canvas child window");
                DestroyWindow(hwnd);
                if (engine != IntPtr.Zero) akapen_free(engine);
                s_engine = IntPtr.Zero;
                return 5;
            }
        }
        UpdateProductMenu(hwnd);
        if (s_productMode) DragAcceptFiles(hwnd, true);
        Console.WriteLine($"{LogPrefix} created HWND=0x{hwnd.ToInt64():X}");

        ShowWindow(hwnd, SW_SHOWNORMAL);
        UpdateWindow(hwnd);
        CreateTooltips(hwnd);

        GetClientRect(hwnd, out RECT client);
        uint clientW = (uint)Math.Max(1, client.right - client.left);
        uint clientH = (uint)Math.Max(1, client.bottom - client.top);
        Console.WriteLine($"{LogPrefix} initial client size = {clientW}x{clientH}");

        // 4. Attach the GPU surface. Same handshake as the smoke test.
        IntPtr renderWindow = RenderWindow(hwnd);
        int attachRc = 0;
        if (engine != IntPtr.Zero)
        {
            GetClientRect(renderWindow, out RECT renderClient);
            uint renderW = (uint)Math.Max(1, renderClient.right - renderClient.left);
            uint renderH = (uint)Math.Max(1, renderClient.bottom - renderClient.top);
            var desc = new AkapenSurfaceDesc
            {
                kind = AKAPEN_SURFACE_HWND,
                handle = renderWindow,
                display = IntPtr.Zero,
                width = renderW,
                height = renderH,
                scale_factor = 1.0f,
            };
            attachRc = akapen_render_attach(engine, ref desc);
            if (attachRc == 0)
            {
                s_lastRenderWidth = desc.width;
                s_lastRenderHeight = desc.height;
            }
        }
        if (engine != IntPtr.Zero && attachRc != 0)
        {
            string reason = ReadLastAttachError(engine);
            Console.Error.WriteLine($"{LogPrefix} FAIL: akapen_render_attach returned {attachRc} ({DescribeAttachCode(attachRc)}); reason: {reason}");
            // Null the shared static BEFORE freeing so any stray late WndProc
            // dispatch (WM_MOUSEMOVE / WM_PAINT queued before DestroyWindow
            // completes) sees a null handle instead of a dangling pointer.
            // Same ordering as the normal-exit teardown at the bottom of this
            // function.
            s_engine = IntPtr.Zero;
            akapen_free(engine);
            DestroyWindow(hwnd);
            return 6;
        }
        if (engine != IntPtr.Zero) Console.WriteLine($"[akapen] akapen_render_attach OK; {ReadBackendInfo(engine)}");
        Console.WriteLine("[akapen] draw with the left mouse button; press Ctrl+S to save; close the window to auto-save + exit.");
        if (s_automatedSmoke) Console.WriteLine("[probe] interactive-smoke: exercising HWND mouse messages and auto-save");

        // 5. Message pump + per-frame render. PeekMessage keeps the pump non-
        //    blocking; akapen_render_frame with the DX12 backend's Fifo present
        //    mode paces us to vsync, so this is not a busy loop.
        int framesRendered = 0;
        while (true)
        {
            if (s_automatedSmoke)
            {
                if (framesRendered == 20) PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, MakeLParam(120, 120));
                if (framesRendered == 21) PostMessage(hwnd, WM_MOUSEMOVE, (IntPtr)1, MakeLParam(180, 160));
                if (framesRendered == 22) PostMessage(hwnd, WM_MOUSEMOVE, (IntPtr)1, MakeLParam(260, 220));
                if (framesRendered == 23) PostMessage(hwnd, WM_MOUSEMOVE, (IntPtr)1, MakeLParam(340, 280));
                if (framesRendered == 24) PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(340, 280));
                if (framesRendered == 80) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            bool quit = false;
            while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT)
                {
                    quit = true;
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (quit) break;

            renderWindow = RenderWindow(hwnd);
            GetClientRect(renderWindow, out RECT cur);
            uint curW = (uint)Math.Max(1, cur.right - cur.left);
            uint curH = (uint)Math.Max(1, cur.bottom - cur.top);
            var view = new AkapenViewTransform
            {
                center_x = curW / 2.0f + s_panX,
                center_y = curH / 2.0f + s_panY,
                scale = s_zoom,
                rotation_deg = s_rotationDeg,
            };
            if (s_engine != IntPtr.Zero) akapen_render_frame(s_engine, view);
            else WaitMessage();
            framesRendered++;
        }

        Console.WriteLine($"[akapen] product session ended; frames={framesRendered}");
        if (s_engine != IntPtr.Zero) Console.WriteLine($"[akapen] final {ReadBackendInfo(s_engine)}");

        IntPtr finalEngine = s_engine;
        if (finalEngine != IntPtr.Zero) akapen_render_detach(finalEngine);
        s_engine = IntPtr.Zero;  // block any stray late WndProc access before free
        if (finalEngine != IntPtr.Zero) akapen_free(finalEngine);
        s_shuttingDown = true;
        CleanupTransientArtifacts();
        ClearPreloadedImages();
        if (s_brushCursor != IntPtr.Zero) { DestroyIcon(s_brushCursor); s_brushCursor = IntPtr.Zero; }
        if (s_canvasWindow != IntPtr.Zero) { DestroyWindow(s_canvasWindow); s_canvasWindow = IntPtr.Zero; }
        DestroyTooltip();
        GC.KeepAlive(wndProc);
        return 0;
    }

    // Registers the plain WS_OVERLAPPEDWINDOW class both modes share. Kept as
    // a helper so the two entry points don't diverge on class flags.
    // The app icon embedded in Akapen.exe (csproj ApplicationIcon), used for
    // the title bar / Alt-Tab / taskbar. Extracted once; leaked deliberately
    // (lives as long as the process, like the window classes).
    private static IntPtr s_appIconLarge = IntPtr.Zero;
    private static IntPtr s_appIconSmall = IntPtr.Zero;
    private static bool s_appIconLoaded;

    private static void EnsureAppIcons()
    {
        if (s_appIconLoaded) return;
        s_appIconLoaded = true;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx(exe, 0, large, small, 1) > 0)
        {
            s_appIconLarge = large[0];
            s_appIconSmall = small[0];
        }
    }

    private static bool RegisterProbeWindowClass(WndProcDelegate wndProc, IntPtr hInstance, string className)
    {
        EnsureAppIcons();
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            hIcon = s_appIconLarge,
            hIconSm = s_appIconSmall,
            // A null class cursor leaves whatever cursor Windows displayed
            // while launching the process in place.  In product mode that was
            // the blue busy cursor, which made an otherwise responsive window
            // look permanently hung.
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            lpszClassName = className,
        };
        ushort atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine($"[probe] FAIL: RegisterClassEx failed, Win32Error={Marshal.GetLastWin32Error()}");
            return false;
        }
        return true;
    }

    private static bool RegisterWindowClass(WndProcDelegate wndProc, IntPtr hInstance, string className) =>
        RegisterProbeWindowClass(wndProc, hInstance, className);

    private static IntPtr RenderWindow(IntPtr mainWindow) =>
        s_productMode && s_canvasWindow != IntPtr.Zero ? s_canvasWindow : mainWindow;

    private static float UiScale(IntPtr window)
    {
        uint dpi = window == IntPtr.Zero ? 96u : GetDpiForWindow(window);
        return dpi == 0 ? 1.0f : dpi / 96.0f;
    }

    private static IntPtr BrushCursor(IntPtr canvas)
    {
        int diameter = Math.Clamp((int)MathF.Round(s_brushSize * s_zoom), 3, 256);
        uint color = 0x000000FFu;
        if (s_brushCursor != IntPtr.Zero && diameter == s_brushCursorDiameter && color == s_brushCursorColor)
            return s_brushCursor;

        if (s_brushCursor != IntPtr.Zero) { DestroyIcon(s_brushCursor); s_brushCursor = IntPtr.Zero; }
        int size = diameter + 6;
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size,
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            }
        };
        IntPtr colorBitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr maskBitmap = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        if (colorBitmap == IntPtr.Zero || maskBitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (colorBitmap != IntPtr.Zero) DeleteObject(colorBitmap);
            if (maskBitmap != IntPtr.Zero) DeleteObject(maskBitmap);
            return LoadCursor(IntPtr.Zero, IDC_ARROW);
        }
        var pixels = new int[size * size];
        double center = (size - 1) / 2.0, radius = diameter / 2.0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
            if (Math.Abs(distance - radius) <= 0.55)
                pixels[y * size + x] = unchecked((int)0xFF000000u);
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var icon = new ICONINFO { fIcon = false, xHotspot = (uint)(size / 2), yHotspot = (uint)(size / 2), hbmMask = maskBitmap, hbmColor = colorBitmap };
        s_brushCursor = CreateIconIndirect(ref icon);
        DeleteObject(colorBitmap); DeleteObject(maskBitmap);
        s_brushCursorDiameter = diameter; s_brushCursorColor = color;
        return s_brushCursor != IntPtr.Zero ? s_brushCursor : LoadCursor(IntPtr.Zero, IDC_ARROW);
    }

    private static bool CreateProductCanvas(IntPtr owner, IntPtr hInstance)
    {
        if (!RegisterWindowClass(s_canvasWndProc, hInstance, "AkapenCanvasWndClass")) return false;
        s_canvasWindow = CreateWindowEx(
            0, "AkapenCanvasWndClass", "Akapen Canvas",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, owner, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (s_canvasWindow == IntPtr.Zero) return false;
        LayoutProductChildren(owner);
        return true;
    }

    private static UiRect ProductCanvasBounds(IntPtr owner, int clientWidth, int clientHeight)
        => DockLayout.Workspace(clientWidth, clientHeight, s_dockSide, UiScale(owner)).Canvas;

    private static void LayoutProductChildren(IntPtr owner)
    {
        if (!s_productMode || !GetClientRect(owner, out RECT client)) return;
        if (s_canvasWindow == IntPtr.Zero)
        {
            InvalidateRect(owner, IntPtr.Zero, false);
            return;
        }
        int width = Math.Max(1, client.right - client.left);
        int height = Math.Max(1, client.bottom - client.top);
        UiRect bounds = ProductCanvasBounds(owner, width, height);
        SetWindowPos(s_canvasWindow, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ResizeRenderSurface();
        InvalidateRect(owner, IntPtr.Zero, false);
    }

    private static void ResizeRenderSurface()
    {
        if (s_engine == IntPtr.Zero) return;
        IntPtr canvas = RenderWindow(s_mainWindow);
        if (canvas == IntPtr.Zero || !GetClientRect(canvas, out RECT client)) return;
        uint width = (uint)Math.Max(1, client.right - client.left);
        uint height = (uint)Math.Max(1, client.bottom - client.top);
        if (width == s_lastRenderWidth && height == s_lastRenderHeight) return;
        akapen_render_resize(s_engine, width, height, 1.0f);
        s_lastRenderWidth = width;
        s_lastRenderHeight = height;
    }

    private static void RenderCurrentFrame()
    {
        if (s_engine == IntPtr.Zero) return;
        IntPtr canvas = RenderWindow(s_mainWindow);
        if (canvas == IntPtr.Zero || !GetClientRect(canvas, out RECT client)) return;
        uint width = (uint)Math.Max(1, client.right - client.left);
        uint height = (uint)Math.Max(1, client.bottom - client.top);
        var view = new AkapenViewTransform
        {
            center_x = width / 2.0f + s_panX,
            center_y = height / 2.0f + s_panY,
            scale = s_zoom,
            rotation_deg = s_rotationDeg,
        };
        akapen_render_frame(s_engine, view);
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // Writes the flat PNG into the review directory and keeps the JSON plus
    // transparent stroke PNG in a per-output transient directory. The latter
    // is removed when the product session ends.
    private static bool TrySave(string reasonTag)
    {
        if (s_engine == IntPtr.Zero)
        {
            Console.WriteLine($"{LogPrefix} {reasonTag} save skipped: no engine");
            return false;
        }
        s_isSaving = true;
        UpdateProductMenu(s_mainWindow);
        string[] before = Directory.Exists(s_transientOutDir)
            ? Directory.GetFiles(s_transientOutDir)
            : Array.Empty<string>();
        Directory.CreateDirectory(s_transientOutDir);
        s_transientDirectories.Add(s_transientOutDir);
        string? movedFlat = null;
        string[] created = Array.Empty<string>();
        try
        {
            int rc = akapen_export_to_dir(s_engine, s_transientOutDir, s_stem);
            if (rc != 0)
            {
                Console.WriteLine($"{LogPrefix} {reasonTag} save failed rc={rc} (dir='{s_transientOutDir}' stem='{s_stem}')");
                return false;
            }

            var beforeSet = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
            created = Directory.GetFiles(s_transientOutDir)
                .Where(path => !beforeSet.Contains(path))
                .ToArray();
            string? flat = created.FirstOrDefault(path =>
                ReviewOutputLayout.IsArtifactForStem(path, s_stem, "review", ".png"));
            string[] strokes = created.Where(path =>
                ReviewOutputLayout.IsArtifactForStem(path, s_stem, "strokes", ".png") ||
                ReviewOutputLayout.IsArtifactForStem(path, s_stem, "strokes", ".json")).ToArray();
            if (flat == null || strokes.Length != 2)
                throw new IOException("The export did not produce the expected review artifacts.");

            Directory.CreateDirectory(s_outDir);
            string flatDestination = ReviewOutputLayout.ResolveCollisionFreeFlatPath(s_outDir, Path.GetFileName(flat));
            File.Move(flat, flatDestination);
            movedFlat = flatDestination;
            foreach (string artifact in strokes) s_transientArtifacts.Add(artifact);
            Console.WriteLine($"{LogPrefix} {reasonTag} saved flat PNG to {s_outDir} and transient stroke data to {s_transientOutDir} (stem={s_stem})");
            s_dirty = false;
            if (!string.IsNullOrEmpty(s_imagePath)) s_dirtySessionDocuments.Remove(s_imagePath);
            return true;
        }
        catch (Exception ex)
        {
            if (movedFlat != null) TryDeleteFile(movedFlat);
            foreach (string path in created) TryDeleteFile(path);
            Console.WriteLine($"{LogPrefix} {reasonTag} save failed: {ex.Message}");
            return false;
        }
        finally
        {
            s_isSaving = false;
            UpdateProductMenu(s_mainWindow);
            InvalidateAllWindows();
        }
    }

    private static UiState CurrentUiState() => new(
        ActiveTool: s_activeTool,
        HasDocument: s_engine != IntPtr.Zero,
        IsDirty: s_dirty,
        CanUndo: s_engine != IntPtr.Zero && akapen_can_undo(s_engine) != 0,
        CanRedo: s_engine != IntPtr.Zero && akapen_can_redo(s_engine) != 0,
        IsSaving: s_isSaving,
        IsLoading: s_isLoading,
        HasPrevious: HasSibling(false),
        HasNext: HasSibling(true),
        HasWarning: s_pressureFallbackWarned,
        Zoom: s_zoom,
        BrushSize: s_brushSize,
        Color: s_color,
        Pressure: s_pressureCurve);

    private static bool IsCommandEnabled(UiCommandId command) =>
        !UiCommandStateResolver.Resolve(command, CurrentUiState()).IsDisabled;

    private static void InvalidateAllWindows()
    {
        if (s_mainWindow != IntPtr.Zero) InvalidateRect(s_mainWindow, IntPtr.Zero, false);
    }

    private static void SetOutputDirectory(string outputDirectory)
    {
        s_outDir = outputDirectory;
        s_transientOutDir = ReviewOutputLayout.ResolveTransientDirectory(outputDirectory);
        s_transientDirectories.Add(s_transientOutDir);
    }

    private static void CleanupTransientArtifacts()
    {
        ReviewOutputLayout.DeleteTrackedArtifacts(s_transientArtifacts, s_transientDirectories);
        s_transientArtifacts.Clear();
        s_transientDirectories.Clear();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Trim characters that would trip resolve_target's collision-naming or
    // land somewhere unexpected on Windows. Keeps the stem printable.
    private static string SanitizeStem(string s)
    {
        if (string.IsNullOrEmpty(s)) return "probe";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else sb.Append('_');
        }
        return sb.Length == 0 ? "probe" : sb.ToString();
    }

    private static string SanitizeFolderName(string? value)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? "_review" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) candidate = candidate.Replace(invalid, '_');
        return candidate is "." or ".." or "" ? "_review" : candidate;
    }

    private static string ResolveOutputDirectory(string imagePath)
    {
        string source = Path.GetDirectoryName(Path.GetFullPath(imagePath))!;
        return s_saveLocationMode switch
        {
            SaveLocationMode.SourceFolder => source,
            SaveLocationMode.CustomFolder when !string.IsNullOrWhiteSpace(s_customOutputPath) => s_customOutputPath,
            _ => ReviewOutputLayout.ResolveSiblingReviewDirectory(imagePath, s_outputFolderName),
        };
    }

    private static string DescribeAttachCode(int rc) => rc switch
    {
        1 => "null engine",
        2 => "null desc",
        3 => "unknown surface kind",
        4 => "surface/adapter/device bring-up failed",
        _ => "unknown code",
    };

    private static string ReadBackendInfo(IntPtr engine)
    {
        UIntPtr needed = akapen_render_backend_info(engine, null!, UIntPtr.Zero);
        if (needed == UIntPtr.Zero) return "backend_info unavailable (no surface attached)";
        var buf = new byte[(int)needed];
        akapen_render_backend_info(engine, buf, (UIntPtr)buf.Length);
        return Encoding.ASCII.GetString(buf, 0, buf.Length - 1); // drop NUL
    }

    private static string ReadLastAttachError(IntPtr engine)
    {
        UIntPtr needed = akapen_render_last_attach_error(engine, null!, UIntPtr.Zero);
        if (needed == UIntPtr.Zero) return "(no error text recorded)";
        var buf = new byte[(int)needed];
        akapen_render_last_attach_error(engine, buf, (UIntPtr)buf.Length);
        return Encoding.ASCII.GetString(buf, 0, buf.Length - 1);
    }

    // ── akapen C ABI (crates/akapen-ffi/include/akapen.h) ──────────────────

    private const int AKAPEN_SURFACE_HWND = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct AkapenSurfaceDesc
    {
        public int kind;
        public IntPtr handle;
        public IntPtr display;
        public uint width;
        public uint height;
        public float scale_factor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AkapenViewTransform
    {
        public float center_x;
        public float center_y;
        public float scale;
        public float rotation_deg;
    }

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_enable_diagnostic_logging();

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr akapen_open_image([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr akapen_new(uint width, uint height);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_free(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_size(IntPtr engine, out uint w, out uint h);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_tool(IntPtr engine, int tool);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_color(IntPtr engine, uint rgba);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_size(IntPtr engine, float px);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_pressure_curve(IntPtr engine, int curve);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_undo(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_redo(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_can_undo(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_can_redo(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_pressure_stuck(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_pointer(IntPtr engine, double x, double y, double pressure, int kind, int phase);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_export_to_dir(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stem);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_render_attach(IntPtr engine, ref AkapenSurfaceDesc desc);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_replace_document(IntPtr active, IntPtr replacement);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_swap_document(IntPtr active, IntPtr standby);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_resolve_key(uint ch, int physical, int primary, int shift, int alt, int composing, int textEditing);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_resolve_key_preset(int preset, uint ch, int physical, int primary, int shift, int alt, int composing, int textEditing);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint akapen_thumbnail_rgba(IntPtr engine, uint maxW, uint maxH, IntPtr outBuf, nuint outLen, out uint outW, out uint outH);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_render_resize(IntPtr engine, uint width, uint height, float scale);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_canvas_dark(IntPtr engine, int dark);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_render_frame(IntPtr engine, AkapenViewTransform view);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_render_detach(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_render_available(IntPtr engine);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr akapen_render_backend_info(IntPtr engine, byte[] outBuf, UIntPtr outLen);

    [DllImport("akapen_native", CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr akapen_render_last_attach_error(IntPtr engine, byte[] outBuf, UIntPtr outLen);

    // ── raw Win32 (user32.dll / kernel32.dll) ───────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_OVERLAPPED = 0x00000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000,
        WS_BORDER = 0x00800000, WS_TABSTOP = 0x00010000;
    private const uint WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_DLGMODALFRAME = 0x00000001;
    private const uint BS_AUTORADIOBUTTON = 0x00000009, BS_AUTOCHECKBOX = 0x00000003,
        BS_GROUP = 0x00000200, BS_GROUPBOX = 0x00000007, BS_DEFPUSHBUTTON = 0x00000001, ES_AUTOHSCROLL = 0x00000080;
    private const uint BM_SETCHECK = 0x00F1, BST_CHECKED = 1;
    private const uint TTS_ALWAYSTIP = 0x00000001, TTF_SUBCLASS = 0x00000010;
    private const uint TTM_ADDTOOLW = 0x0432;
    private const int SettingsOkId = 1003, SettingsCancelId = 1004,
        SettingsDarkCanvasId = 1010, SettingsAutoSaveId = 1011,
        SettingsSiblingFolderId = 1012, SettingsSourceFolderId = 1013,
        SettingsCustomFolderId = 1014, SettingsFolderNameId = 1015,
        SettingsCustomPathId = 1016,
        SettingsKeymapPhotoshopId = 1017, SettingsKeymapClipStudioId = 1018,
        SettingsPressureId = 1019;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_HIDE = 0, SW_SHOWNORMAL = 1;
    private static readonly IntPtr IDC_ARROW = (IntPtr)32512;
    private const int DEFAULT_GUI_FONT = 17;
    private const uint PM_REMOVE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    // Window / input messages we handle directly (everything else falls
    // through to DefWindowProc).
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CREATE = 0x0001, WM_COMMAND = 0x0111, WM_SETFONT = 0x0030;
    private const uint WM_CTLCOLORBTN = 0x0135, WM_CTLCOLORSTATIC = 0x0138;
    private const int WHITE_BRUSH = 0;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
    private const uint WM_CLOSE = 0x0010, WM_KILLFOCUS = 0x0008;
    private const uint WM_SIZE = 0x0005, WM_DPICHANGED = 0x02E0;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012, WM_DROPFILES = 0x0233;
    private const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private const int PT_TOUCH = 2, PT_PEN = 3;

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SPACE = 0x20, VK_S = 0x53, VK_Z = 0x5A, VK_Y = 0x59, VK_0 = 0x30;
    private const int VK_A = 0x41, VK_O = 0x4F, VK_P = 0x50, VK_E = 0x45, VK_F = 0x46, VK_R = 0x52, VK_ESCAPE = 0x1B;
    private const int VK_ADD = 0x6B, VK_SUBTRACT = 0x6D, VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD, VK_OEM_4 = 0xDB, VK_OEM_6 = 0xDD;
    private const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint displayName, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem item, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount, inputData, dwKeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags, penMask, pressure, rotation;
        public int tiltX, tiltY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TOOLINFO
    {
        public uint cbSize, uFlags;
        public IntPtr hwnd;
        public UIntPtr uId;
        public RECT rect;
        public IntPtr hinst;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszText;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize; public IntPtr hWndOwner; public IntPtr hInstance;
        public string lpstrFilter; public string? lpstrCustomFilter; public uint nMaxCustFilter, nFilterIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public StringBuilder lpstrFile; public uint nMaxFile;
        public StringBuilder? lpstrFileTitle; public uint nMaxFileTitle; public string? lpstrInitialDir;
        public string? lpstrTitle; public uint Flags; public ushort nFileOffset, nFileExtension;
        public string? lpstrDefExt; public IntPtr lCustData, lpfnHook; public string? lpTemplateName;
        public IntPtr pvReserved; public uint dwReserved, FlagsEx;
    }
    private const uint OFN_FILEMUSTEXIST = 0x1000, OFN_PATHMUSTEXIST = 0x0800, MB_ICONERROR = 0x10;
    private const uint FOS_PICKFOLDERS = 0x00000020, FOS_FORCEFILESYSTEM = 0x00000040, FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint SRCCOPY = 0x00CC0020;

    // WM_LBUTTONDOWN/UP/MOUSEMOVE pack the client-area x/y into lParam as two
    // signed 16-bit ints (LOWORD=x, HIWORD=y). Sign-extend explicitly so a
    // drag off the top/left edge of the client area still gets a well-formed
    // (negative) coordinate.
    private static int GetLParamX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int GetLParamY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);
    private static IntPtr MakeLParam(int x, int y) => (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static (double x, double y) ClientToImage(int x, int y, IntPtr hWnd)
    {
        GetClientRect(hWnd, out RECT r);
        double cx = (r.right - r.left) / 2.0 + s_panX;
        double cy = (r.bottom - r.top) / 2.0 + s_panY;
        double a = -s_rotationDeg * Math.PI / 180.0;
        double dx = (x - cx) / Math.Max(0.01f, s_zoom);
        double dy = (y - cy) / Math.Max(0.01f, s_zoom);
        return (dx * Math.Cos(a) - dy * Math.Sin(a) + s_imageW / 2.0,
                dx * Math.Sin(a) + dy * Math.Cos(a) + s_imageH / 2.0);
    }

    private static void FeedPointer(IntPtr hWnd, int x, int y, double pressure, int kind, int phase)
    {
        if (s_engine == IntPtr.Zero) return;
        var p = ClientToImage(x, y, hWnd);
        double normalizedPressure = Math.Clamp(pressure, 0.05, 1.0);
        if (phase == 0 || !s_hasInterpolatedPoint)
        {
            akapen_pointer(s_engine, p.x, p.y, normalizedPressure, kind, phase == 2 ? 0 : phase);
            s_hasInterpolatedPoint = phase != 2;
        }
        else
        {
            double dx = p.x - s_lastInterpolatedX, dy = p.y - s_lastInterpolatedY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double spacing = Math.Max(0.75, s_brushSize * 0.18);
            int steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                akapen_pointer(s_engine,
                    s_lastInterpolatedX + dx * t,
                    s_lastInterpolatedY + dy * t,
                    s_lastInterpolatedPressure + (normalizedPressure - s_lastInterpolatedPressure) * t,
                    kind,
                    phase == 2 && i == steps ? 2 : 1);
            }
            if (phase == 2) s_hasInterpolatedPoint = false;
        }
        s_lastInterpolatedX = p.x; s_lastInterpolatedY = p.y; s_lastInterpolatedPressure = normalizedPressure;
        if (phase != 2) s_dirty = true;
        else
        {
            RefreshNavigatorThumbnail();
            InvalidateAllWindows();
        }
        UpdateProductMenu(hWnd);
    }

    private static void SetZoom(float factor)
    {
        s_zoom = Math.Clamp(s_zoom * factor, 0.05f, 32.0f);
        Console.WriteLine($"{LogPrefix} zoom={s_zoom:0.00}x");
        InvalidateAllWindows();
    }

    private static void SetBrushSize(float delta)
    {
        s_brushSize = BrushFader.Clamp(s_brushSize + delta);
        ApplyStyle(s_engine);
        Console.WriteLine($"{LogPrefix} brush size={s_brushSize:0.0}px");
    }

    private static bool TryMapBrushFaderKey(int virtualKey, out BrushFaderKey key)
    {
        key = virtualKey switch
        {
            VK_UP => BrushFaderKey.Up,
            VK_DOWN => BrushFaderKey.Down,
            VK_PRIOR => BrushFaderKey.PageUp,
            VK_NEXT => BrushFaderKey.PageDown,
            VK_HOME => BrushFaderKey.Home,
            VK_END => BrushFaderKey.End,
            _ => default,
        };
        return virtualKey is VK_UP or VK_DOWN or VK_PRIOR or VK_NEXT or VK_HOME or VK_END;
    }

    private static int ResolvePhysicalKey(int vk) => vk switch
    {
        0x50 => 1, 0x45 => 2, 0x55 => 3, 0x41 => 4, 0x52 => 5,
        0x4F => 6, 0x54 => 7, 0x49 => 8, 0x58 => 9, 0x43 => 10,
        0x5A => 11, 0x59 => 12, 0x30 => 13, 0x20 => 14,
        0xDB => 15, 0xDD => 16, 0xBD => 17, 0xDE => 18,
        0x21 => 19, 0x22 => 20,
        // V1.1: Photoshop-preset keys and the arrow cluster (AKAPEN_PK_*).
        0x42 => 21 /*B*/, 0x31 => 22 /*1*/,
        VK_LEFT => 23, VK_RIGHT => 24, VK_UP => 25, VK_DOWN => 26,
        _ => 0,
    };

    /// <summary>
    /// View rotation step (V1.1): 15° per press/click instead of the V1.0
    /// 90°, matching Photoshop-like fine rotation for review work.
    /// </summary>
    private static void RotateView(float degrees)
    {
        s_rotationDeg = (s_rotationDeg + degrees % 360 + 360) % 360;
        Console.WriteLine($"{LogPrefix} rotation={s_rotationDeg:0}°");
    }

    private static void ExecuteResolvedShortcut(IntPtr owner, int action)
    {
        switch (action)
        {
            case 1: HandleDockCommand(owner, UiCommandId.Pen); break;
            case 2: HandleDockCommand(owner, UiCommandId.Eraser); break;
            case 4: HandleDockCommand(owner, UiCommandId.Arrow); break;
            case 10: HandleDockCommand(owner, UiCommandId.Undo); break;
            case 11: HandleDockCommand(owner, UiCommandId.Redo); break;
            case 12: SetZoom(1.15f); break;
            case 13: SetZoom(1.0f / 1.15f); break;
            case 14: FitView(RenderWindow(owner)); break;
            case 15: s_zoom = 1; s_panX = s_panY = 0; break;
            case 16: RotateView(-15); break;
            case 17: RotateView(+15); break;
            case 18: SetBrushSize(-1); break;
            case 19: SetBrushSize(1); break;
            case 20: StepSibling(owner, true); break;
            case 21: StepSibling(owner, false); break;
        }
        InvalidateAllWindows();
    }

    private static void ApplyStyle(IntPtr engine)
    {
        if (engine == IntPtr.Zero) return;
        akapen_set_color(engine, AkapenPalette.NormalizeRgba(s_color));
        akapen_set_size(engine, BrushFader.Clamp(s_brushSize));
        akapen_set_pressure_curve(engine, (int)s_pressureCurve);
    }

    private static void FitView(IntPtr hWnd)
    {
        GetClientRect(hWnd, out RECT r);
        s_zoom = Math.Clamp(Math.Min((r.right - r.left) / (float)s_imageW,
                                     (r.bottom - r.top) / (float)s_imageH) * 0.94f, 0.05f, 32.0f);
        s_panX = s_panY = 0;
    }

    private static void OpenImageDialog(IntPtr hWnd)
    {
        var file = new StringBuilder(4096);
        var ofn = new OPENFILENAME
        {
            lStructSize = Marshal.SizeOf<OPENFILENAME>(), hWndOwner = hWnd,
            lpstrFilter = "Images\0*.png;*.jpg;*.jpeg;*.bmp;*.webp\0All files\0*.*\0\0",
            lpstrFile = file, nMaxFile = (uint)file.Capacity,
            Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST,
            lpstrTitle = "Open image"
        };
        if (GetOpenFileName(ref ofn)) OpenImageFile(hWnd, file.ToString());
    }

    private static void OpenImageFolderDialog(IntPtr hWnd)
    {
        IFileDialog? dialog = null;
        IShellItem? selected = null;
        try
        {
            Type dialogType = Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), throwOnError: true)!;
            dialog = (IFileDialog)Activator.CreateInstance(dialogType)!;
            dialog.GetOptions(out uint existingOptions);
            dialog.SetOptions(existingOptions | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            dialog.SetTitle("画像を開くフォルダを選択");
            dialog.SetOkButtonLabel("選択");
            int result = dialog.Show(hWnd);
            if (result != 0) return; // Includes user cancellation (HRESULT_FROM_WIN32(ERROR_CANCELLED)).
            dialog.GetResult(out selected);
            selected.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pathPtr);
            try
            {
                string? path = Marshal.PtrToStringUni(pathPtr);
                if (!string.IsNullOrWhiteSpace(path)) OpenFirstImageInFolder(hWnd, path);
            }
            finally { Marshal.FreeCoTaskMem(pathPtr); }
        }
        catch (COMException ex)
        {
            AppDiagnostics.Write("folder-dialog", ex);
            MessageBox(hWnd, $"フォルダ選択を開始できませんでした。\n0x{ex.HResult:X8}", "Akapen", MB_ICONERROR);
        }
        catch (Exception ex)
        {
            // A filesystem/access failure after the dialog must never escape
            // Main and silently terminate the entire application.
            AppDiagnostics.Write("folder-dialog", ex);
            MessageBox(hWnd, $"フォルダを開けませんでした。\n{ex.Message}", "Akapen", MB_ICONERROR);
        }
        finally
        {
            if (selected != null) Marshal.ReleaseComObject(selected);
            if (dialog != null) Marshal.ReleaseComObject(dialog);
        }
    }

    private static void OpenFirstImageInFolder(IntPtr hWnd, string folder)
    {
        if (!Directory.Exists(folder)) return;
        ImageFolderSelectionResult selection = ImageFolderSelection.FindFirstSupportedImage(
            folder,
            Comparer<string>.Create(StrCmpLogicalW));
        if (selection.ErrorMessage != null)
        {
            AppDiagnostics.Write("folder-enumeration", $"{folder}: {selection.ErrorMessage}");
            MessageBox(hWnd, $"フォルダを読み込めませんでした。\n{selection.ErrorMessage}", "Akapen", MB_ICONERROR);
            return;
        }
        string? first = selection.ImagePath;
        if (first == null)
        {
            MessageBox(hWnd, "このフォルダに対応画像がありません。", "Akapen", MB_ICONERROR);
            return;
        }
        OpenImageFile(hWnd, first);
    }

    private static bool IsSupportedImage(string path) => ImageFolderSelection.IsSupportedImage(path);

    private static void OpenDroppedPath(IntPtr hWnd, string path)
    {
        try
        {
            if (Directory.Exists(path)) OpenFirstImageInFolder(hWnd, path);
            else if (File.Exists(path) && IsSupportedImage(path)) OpenImageFile(hWnd, path);
            else MessageBox(hWnd, "対応画像または画像フォルダをドロップしてください。", "Akapen", MB_ICONERROR);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("explorer-drop", ex);
            MessageBox(hWnd, $"Explorer からの読込に失敗しました。\n{ex.Message}", "Akapen", MB_ICONERROR);
        }
    }

    private static void OpenImageFile(IntPtr hWnd, string path, bool saveCurrent = true)
    {
        if (!IsCommandEnabled(UiCommandId.Open)) return;
        if (saveCurrent && s_dirty && !TrySave("before-open")) return;
        s_isLoading = true;
        UpdateProductMenu(s_mainWindow);
        IntPtr next = TakePreloadedImage(path);
        if (next == IntPtr.Zero) next = akapen_open_image(path);
        s_isLoading = false;
        if (next == IntPtr.Zero)
        {
            UpdateProductMenu(s_mainWindow);
            InvalidateAllWindows();
            MessageBox(hWnd, "Could not open this image.", "Akapen", MB_ICONERROR);
            return;
        }
        if (s_productMode && s_canvasWindow == IntPtr.Zero && !CreateProductCanvas(s_mainWindow, GetModuleHandle(null)))
        {
            akapen_free(next);
            MessageBox(hWnd, "Could not create the image canvas.", "Akapen", MB_ICONERROR);
            InvalidateAllWindows();
            return;
        }
        bool needsAttach = s_engine == IntPtr.Zero;
        string previousPath = s_imagePath;
        akapen_size(next, out s_imageW, out s_imageH);
        if (needsAttach)
        {
            s_engine = next;
        }
        else
        {
            int replaceRc = akapen_swap_document(s_engine, next);
            if (replaceRc != 0)
            {
                akapen_free(next);
                s_isLoading = false;
                MessageBox(hWnd, "Could not switch to this image.", "Akapen", MB_ICONERROR);
                return;
            }
            if (!string.IsNullOrEmpty(previousPath))
            {
                if (s_dirty) s_dirtySessionDocuments.Add(previousPath);
                else s_dirtySessionDocuments.Remove(previousPath);
                StoreCachedDocument(previousPath, next);
            }
            else akapen_free(next);
        }
        s_imagePath = path;
        RebuildImageSiblings(path);
        SetOutputDirectory(s_customOutDir ?? ResolveOutputDirectory(path));
        s_stem = SanitizeStem(Path.GetFileNameWithoutExtension(path));
        s_dirty = s_dirtySessionDocuments.Contains(path);
        s_activeTool = UiCommandId.Pen;
        akapen_set_tool(s_engine, 0); ApplyStyle(s_engine);
        s_panX = s_panY = 0; s_rotationDeg = 0;
        IntPtr renderWindow = RenderWindow(hWnd);
        GetClientRect(renderWindow, out RECT renderClient);
        FitView(renderWindow);
        var desc = new AkapenSurfaceDesc { kind = AKAPEN_SURFACE_HWND, handle = renderWindow,
            width = (uint)Math.Max(1, renderClient.right - renderClient.left), height = (uint)Math.Max(1, renderClient.bottom - renderClient.top), scale_factor = 1 };
        int rc = needsAttach ? akapen_render_attach(s_engine, ref desc) : 0;
        akapen_set_canvas_dark(s_engine, s_canvasBackdrop == CanvasBackdrop.Black ? 1 : 0);
        if (rc != 0) MessageBox(hWnd, "Could not attach the image surface.", "Akapen", MB_ICONERROR);
        s_lastRenderWidth = desc.width;
        s_lastRenderHeight = desc.height;
        ShowWindow(s_canvasWindow, SW_SHOWNORMAL);
        LayoutProductChildren(s_mainWindow);
        if (s_canvasWindow != IntPtr.Zero) SetFocus(s_canvasWindow);
        else SetFocus(s_mainWindow);
        SetWindowText(s_mainWindow, "Akapen");
        UpdateProductMenu(hWnd);
        CreateTooltips(s_mainWindow);
        RefreshNavigatorThumbnail();
        PreloadAdjacentImages();
    }

    private static void StepSibling(IntPtr hWnd, bool forward)
    {
        var command = forward ? UiCommandId.Next : UiCommandId.Previous;
        if (!IsCommandEnabled(command)) return;
        if (s_imageSiblingIndex < 0 || s_imageSiblings.Count < 2) return;
        if (s_dirty && s_autoSaveOnNavigate && !TrySave(forward ? "before-next" : "before-previous")) return;
        int nextIndex = (s_imageSiblingIndex + (forward ? 1 : -1) + s_imageSiblings.Count) % s_imageSiblings.Count;
        s_lastNavigationForward = forward;
        OpenImageFile(hWnd, s_imageSiblings[nextIndex], saveCurrent: false);
    }

    private static void RebuildImageSiblings(string currentPath)
    {
        s_imageSiblings.Clear();
        s_imageSiblingIndex = -1;
        string? directory = Path.GetDirectoryName(currentPath);
        if (directory == null) return;
        try
        {
            s_imageSiblings.AddRange(Directory.EnumerateFiles(directory).Where(IsSupportedImage));
            s_imageSiblings.Sort(StrCmpLogicalW);
            string fullCurrentPath = Path.GetFullPath(currentPath);
            s_imageSiblingIndex = s_imageSiblings.FindIndex(candidate =>
                string.Equals(Path.GetFullPath(candidate), fullCurrentPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException) { s_imageSiblings.Clear(); }
        catch (UnauthorizedAccessException) { s_imageSiblings.Clear(); }
    }

    private static bool HasSibling(bool forward) => s_imageSiblingIndex >= 0 && s_imageSiblings.Count > 1;

    private static IntPtr TakePreloadedImage(string path)
    {
        lock (s_preloadLock)
        {
            if (!s_preloadedImages.Remove(path, out CachedImage cached)) return IntPtr.Zero;
            s_preloadOrder.Remove(path);
            s_preloadedBytes -= cached.EstimatedBytes;
            return cached.Engine;
        }
    }

    private static void StoreCachedDocument(string path, IntPtr engine)
    {
        if (engine == IntPtr.Zero) return;
        akapen_size(engine, out uint width, out uint height);
        long estimatedBytes = Math.Max(1, (long)width * height * 5);
        IntPtr staleEngine = IntPtr.Zero;
        lock (s_preloadLock)
        {
            if (s_preloadedImages.Remove(path, out CachedImage stale))
            {
                s_preloadedBytes -= stale.EstimatedBytes;
                s_preloadOrder.Remove(path);
                staleEngine = stale.Engine;
            }
            s_preloadedImages[path] = new CachedImage(engine, estimatedBytes);
            s_sessionDocuments.Add(path);
            s_preloadedBytes += estimatedBytes;
            s_preloadOrder.AddLast(path);
        }
        if (staleEngine != IntPtr.Zero) akapen_free(staleEngine);
    }

    private static void PreloadAdjacentImages()
    {
        foreach (string path in ImagePreloadPlan.Window(s_imageSiblings, s_imageSiblingIndex, s_lastNavigationForward))
        {
            lock (s_preloadLock)
            {
                if (s_shuttingDown || s_preloadedImages.ContainsKey(path) || !s_preloadInFlight.Add(path)) continue;
            }
            _ = Task.Run(() =>
            {
                s_preloadSlots.Wait();
                IntPtr engine;
                try { engine = akapen_open_image(path); }
                finally { s_preloadSlots.Release(); }
                var evicted = new List<IntPtr>();
                lock (s_preloadLock)
                {
                    s_preloadInFlight.Remove(path);
                    if (!s_shuttingDown && engine != IntPtr.Zero && !s_preloadedImages.ContainsKey(path))
                    {
                        akapen_size(engine, out uint width, out uint height);
                        long estimatedBytes = Math.Max(1, (long)width * height * 5);
                        s_preloadedImages[path] = new CachedImage(engine, estimatedBytes);
                        s_preloadedBytes += estimatedBytes;
                        s_preloadOrder.AddLast(path);
                        while (s_preloadedBytes > s_preloadBudgetBytes && s_preloadOrder.Count > 1)
                        {
                            LinkedListNode<string>? candidate = s_preloadOrder.First;
                            while (candidate != null && s_sessionDocuments.Contains(candidate.Value)) candidate = candidate.Next;
                            if (candidate == null) break;
                            string oldest = candidate.Value;
                            s_preloadOrder.Remove(candidate);
                            if (s_preloadedImages.Remove(oldest, out CachedImage stale))
                            {
                                s_preloadedBytes -= stale.EstimatedBytes;
                                evicted.Add(stale.Engine);
                            }
                        }
                    }
                    else if (engine != IntPtr.Zero) evicted.Add(engine);
                }
                foreach (IntPtr stale in evicted) akapen_free(stale);
            });
        }
    }

    private static void ClearPreloadedImages()
    {
        List<IntPtr> engines;
        lock (s_preloadLock)
        {
            engines = s_preloadedImages.Values.Select(item => item.Engine).ToList();
            s_preloadedImages.Clear();
            s_sessionDocuments.Clear();
            s_dirtySessionDocuments.Clear();
            s_preloadOrder.Clear();
            s_preloadedBytes = 0;
        }
        foreach (IntPtr engine in engines) akapen_free(engine);
    }

    /// <summary>
    /// Re-reads the navigator thumbnail from the core (V1.1). The core
    /// downscales its display composite (background + strokes) to ≤256 px on
    /// the long side and this converts RGBA → BGRA once for GDI+. Called on
    /// document load, stroke end, undo/redo and settings changes — not per
    /// frame.
    /// </summary>
    private static void RefreshNavigatorThumbnail()
    {
        if (s_engine == IntPtr.Zero)
        {
            s_navThumbBgra = null;
            s_navThumbW = s_navThumbH = 0;
            return;
        }
        uint w = 0, h = 0;
        nuint needed = akapen_thumbnail_rgba(s_engine, 256, 256, IntPtr.Zero, 0, out w, out h);
        if (needed == 0 || w == 0 || h == 0) return;
        var pixels = new byte[(int)needed];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                if (akapen_thumbnail_rgba(s_engine, 256, 256, (IntPtr)p, (nuint)pixels.Length, out w, out h) != needed)
                    return;
            }
        }
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); // RGBA → BGRA
        s_navThumbBgra = pixels;
        s_navThumbW = (int)w;
        s_navThumbH = (int)h;
        InvalidateAllWindows();
    }

    private static NavigatorState? CurrentNavigatorState()
    {
        if (s_engine == IntPtr.Zero) return null;
        IntPtr canvas = RenderWindow(s_mainWindow);
        int canvasW = 0, canvasH = 0;
        if (canvas != IntPtr.Zero && GetClientRect(canvas, out RECT rc))
        {
            canvasW = Math.Max(0, rc.right - rc.left);
            canvasH = Math.Max(0, rc.bottom - rc.top);
        }
        return new NavigatorState(s_navThumbBgra, s_navThumbW, s_navThumbH,
            s_imageW, s_imageH, canvasW, canvasH, s_zoom, s_panX, s_panY, s_rotationDeg);
    }

    private static void PaintDock(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out RECT client)) return;
        IntPtr target = BeginPaint(hWnd, out PAINTSTRUCT paint);
        if (target == IntPtr.Zero) return;
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            int width = client.right - client.left, height = client.bottom - client.top;
            memory = CreateCompatibleDC(target);
            if (memory == IntPtr.Zero) return;
            bitmap = CreateCompatibleBitmap(target, Math.Max(1, width), Math.Max(1, height));
            if (bitmap == IntPtr.Zero) return;
            previous = SelectObject(memory, bitmap);
            string statusText = s_engine == IntPtr.Zero
                ? "画像フォルダを選択するか、画像をドロップしてください"
                : $"{s_stem}　{s_zoom * 100:0}%　ペン先 {s_brushSize:0} px{(s_dirty ? "　未保存" : "")}";
            using var renderer = new NativeUiRenderer(memory);
            renderer.Paint(width, height, s_dockSide, CurrentUiState(), s_hoverCommand, statusText,
                s_brushFaderFocused, UiScale(hWnd), CurrentNavigatorState());
            BitBlt(target, 0, 0, width, height, memory, 0, 0, SRCCOPY);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            EndPaint(hWnd, ref paint);
        }
    }

    private const int ProductMenuBaseId = 0x5000;
    private const int ProductOpenFolderId = 0x5100;
    // V1.1.1: explicit pressure submenu (the old single "筆圧" item silently
    // cycled Normal→Soft→Hard with no visible state) and Help → About.
    private const int ProductPressureEnabledId = 0x5201;
    private const int ProductPressureNormalId = 0x5202;
    private const int ProductPressureSoftId = 0x5203;
    private const int ProductPressureHardId = 0x5204;
    private const int ProductAboutId = 0x5210;
    private const int ProductHelpManualId = 0x5211;

    private static IntPtr CreateProductMenu()
    {
        IntPtr menu = CreateMenu();
        if (menu == IntPtr.Zero) return IntPtr.Zero;

        IntPtr file = CreatePopupMenu();
        AppendMenu(file, MF_STRING, MenuId(UiCommandId.Open), "開く");
        AppendMenu(file, MF_STRING, (UIntPtr)ProductOpenFolderId, "画像フォルダを選択");
        AppendMenu(file, MF_STRING, MenuId(UiCommandId.Save), "保存");
        AppendMenu(menu, MF_POPUP, (UIntPtr)file, "ファイル");

        IntPtr edit = CreatePopupMenu();
        AppendMenu(edit, MF_STRING, MenuId(UiCommandId.Undo), "元に戻す");
        AppendMenu(edit, MF_STRING, MenuId(UiCommandId.Redo), "やり直す");
        AppendMenu(menu, MF_POPUP, (UIntPtr)edit, "編集");

        IntPtr view = CreatePopupMenu();
        AppendMenu(view, MF_STRING, MenuId(UiCommandId.Previous), "前の画像");
        AppendMenu(view, MF_STRING, MenuId(UiCommandId.Next), "次の画像");
        AppendMenu(view, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(view, MF_STRING, MenuId(UiCommandId.ViewFit), "ウインドウに合わせる");
        AppendMenu(view, MF_STRING, MenuId(UiCommandId.Actual), "実寸（100%）");
        AppendMenu(menu, MF_POPUP, (UIntPtr)view, "表示");

        IntPtr tools = CreatePopupMenu();
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Pen), "ペン");
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Eraser), "消しゴム");
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Pan), "パン");
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Zoom), "ズーム");
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Rotate), "回転");
        AppendMenu(tools, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.BrushSize), "ブラシサイズ");
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Color), "色");
        IntPtr pressure = CreatePopupMenu();
        AppendMenu(pressure, MF_STRING, (UIntPtr)ProductPressureEnabledId, "筆圧を使う");
        AppendMenu(pressure, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(pressure, MF_STRING, (UIntPtr)ProductPressureNormalId, "筆圧カーブ: 標準");
        AppendMenu(pressure, MF_STRING, (UIntPtr)ProductPressureSoftId, "筆圧カーブ: やわらかめ");
        AppendMenu(pressure, MF_STRING, (UIntPtr)ProductPressureHardId, "筆圧カーブ: かため");
        AppendMenu(tools, MF_POPUP, (UIntPtr)pressure, "筆圧");
        AppendMenu(tools, MF_STRING, MenuId(UiCommandId.Opacity), "不透明度");
        AppendMenu(menu, MF_POPUP, (UIntPtr)tools, "ツール");

        IntPtr settings = CreatePopupMenu();
        AppendMenu(settings, MF_STRING, MenuId(UiCommandId.Settings), "設定");
        AppendMenu(menu, MF_POPUP, (UIntPtr)settings, "設定");

        IntPtr help = CreatePopupMenu();
        AppendMenu(help, MF_STRING, (UIntPtr)ProductHelpManualId, "Akapen ヘルプ");
        AppendMenu(help, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(help, MF_STRING, (UIntPtr)ProductAboutId, "バージョン情報");
        AppendMenu(menu, MF_POPUP, (UIntPtr)help, "ヘルプ");
        return menu;
    }

    private static void UpdateProductMenu(IntPtr hWnd)
    {
        if (!s_productMode || s_productMenu == IntPtr.Zero) return;

        IntPtr file = GetSubMenu(s_productMenu, 0);
        IntPtr edit = GetSubMenu(s_productMenu, 1);
        IntPtr view = GetSubMenu(s_productMenu, 2);
        IntPtr tools = GetSubMenu(s_productMenu, 3);
        IntPtr settings = GetSubMenu(s_productMenu, 4);
        foreach (var (menu, command) in new[]
        {
            (file, UiCommandId.Open), (file, UiCommandId.Save),
            (edit, UiCommandId.Undo), (edit, UiCommandId.Redo),
            (view, UiCommandId.Previous), (view, UiCommandId.Next),
            (view, UiCommandId.ViewFit), (view, UiCommandId.Actual),
            (tools, UiCommandId.Pen), (tools, UiCommandId.Eraser),
            (tools, UiCommandId.Pan), (tools, UiCommandId.Zoom),
            (tools, UiCommandId.Rotate), (tools, UiCommandId.BrushSize),
            (tools, UiCommandId.Color),
            (tools, UiCommandId.Opacity), (settings, UiCommandId.Settings)
        })
        {
            var state = UiCommandStateResolver.Resolve(command, CurrentUiState());
            EnableMenuItem(menu, (uint)MenuId(command), state.IsDisabled ? MF_GRAYED : MF_ENABLED);
        }

        // Pressure submenu state (V1.1.1): check mark on the on/off toggle,
        // radio mark on the active curve; curve entries gray out while
        // pressure is off.
        CheckMenuItem(tools, ProductPressureEnabledId,
            MF_BYCOMMAND | (s_pressureEnabled ? MF_CHECKED : MF_UNCHECKED));
        CheckMenuRadioItem(tools, ProductPressureNormalId, ProductPressureHardId,
            (uint)(s_pressureCurve switch
            {
                PressureCurve.Soft => ProductPressureSoftId,
                PressureCurve.Hard => ProductPressureHardId,
                _ => ProductPressureNormalId,
            }), MF_BYCOMMAND);
        foreach (int id in new[] { ProductPressureNormalId, ProductPressureSoftId, ProductPressureHardId })
            EnableMenuItem(tools, (uint)id, s_pressureEnabled ? MF_ENABLED : MF_GRAYED);

        var activeTool = CurrentUiState().ActiveTool;
        CheckMenuRadioItem(tools, (uint)MenuId(UiCommandId.Pen), (uint)MenuId(UiCommandId.Rotate),
            (uint)MenuId(activeTool), MF_BYCOMMAND);
        DrawMenuBar(hWnd);
    }

    /// <summary>Writes the current in-memory settings to settings.json (used
    /// by the settings dialog's OK and the menu-level pressure toggle).</summary>
    private static void PersistSettings()
    {
        UiSettingsStore.Save(s_settingsPath, new UiSettings
        {
            DockSide = s_dockSide == DockSide.Left ? "left" : "right",
            CanvasBackdrop = s_canvasBackdrop == CanvasBackdrop.Black ? "black" : "white",
            AutoSaveOnNavigate = s_autoSaveOnNavigate,
            SaveLocationMode = s_saveLocationMode switch
            {
                SaveLocationMode.SourceFolder => "sourceFolder",
                SaveLocationMode.CustomFolder => "customFolder",
                _ => "siblingSubfolder",
            },
            OutputFolderName = s_outputFolderName,
            CustomOutputPath = s_customOutputPath,
            KeymapPreset = UiSettingsStore.KeymapPresetName(s_keymapPreset),
            PressureEnabled = s_pressureEnabled,
        });
    }

    /// <summary>
    /// Opens the bundled HTML manual in the default browser (ヘルプ → Akapen
    /// ヘルプ). The file ships beside the exe under manual\; falls back to the
    /// repository copy's URL when missing (e.g. running from a dev tree).
    /// </summary>
    private static void OpenManual(IntPtr owner)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "manual", "akapen-manual-ja.html");
        string target = File.Exists(local)
            ? local
            : "https://github.com/yosshibox/akapen/blob/main/docs/manual/akapen-manual-ja.md";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("help-manual", ex);
            MessageBox(owner, $"マニュアルを開けませんでした。\n{target}", "Akapen", MB_ICONERROR);
        }
    }

    private static string AboutText()
    {
        Version v = typeof(Program).Assembly.GetName().Version ?? new Version(1, 1, 1);
        return $"Akapen Version {v.Major}.{v.Minor}.{v.Build} Windows\n(C) 2026 Yoshino Yoshikawa";
    }

    private static UIntPtr MenuId(UiCommandId command) => (UIntPtr)(ProductMenuBaseId + (int)command);

    private static bool TryGetMenuCommand(IntPtr wParam, out UiCommandId command)
    {
        int id = (int)(wParam.ToInt64() & 0xFFFF);
        int value = id - ProductMenuBaseId;
        if (value >= 0 && value < Enum.GetValues<UiCommandId>().Length)
        {
            command = (UiCommandId)value;
            return true;
        }
        command = default;
        return false;
    }

    private static void CreateTooltips(IntPtr hWnd)
    {
        DestroyTooltip();
        if (s_engine == IntPtr.Zero) return;
        s_tooltip = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "tooltips_class32", "",
            TTS_ALWAYSTIP, 0, 0, 0, 0, hWnd, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (s_tooltip == IntPtr.Zero) return;
        if (!GetClientRect(hWnd, out RECT client)) return;
        int width = client.right - client.left, height = client.bottom - client.top;
        float scale = UiScale(hWnd);
        foreach (var button in DockLayout.Buttons(width, height, s_dockSide, scale))
        {
            var rect = ToNativeRect(button.Bounds);
            var info = new TOOLINFO
            {
                cbSize = (uint)Marshal.SizeOf<TOOLINFO>(),
                uFlags = TTF_SUBCLASS,
                hwnd = hWnd,
                uId = (UIntPtr)((int)button.Command + 1),
                rect = rect,
                lpszText = UiCommand.Tooltip(button.Command)
            };
            SendMessage(s_tooltip, TTM_ADDTOOLW, IntPtr.Zero, ref info);
        }
        foreach (var swatch in DockLayout.PaletteSwatches(width, height, s_dockSide, scale))
        {
            var info = new TOOLINFO
            {
                cbSize = (uint)Marshal.SizeOf<TOOLINFO>(), uFlags = TTF_SUBCLASS, hwnd = hWnd,
                uId = (UIntPtr)(100 + swatch.Index), rect = ToNativeRect(swatch.Bounds),
                lpszText = $"{AkapenPalette.Colors[swatch.Index].Name}（クリックで描画色に設定）"
            };
            SendMessage(s_tooltip, TTM_ADDTOOLW, IntPtr.Zero, ref info);
        }
        var faderInfo = new TOOLINFO
        {
            cbSize = (uint)Marshal.SizeOf<TOOLINFO>(), uFlags = TTF_SUBCLASS, hwnd = hWnd, uId = (UIntPtr)200,
            rect = ToNativeRect(DockLayout.BrushFaderBounds(width, height, s_dockSide, scale)),
            lpszText = "ペン先サイズ 1–50 px（クリック／ドラッグ、矢印キー、Page Up／Page Down、Home／End）"
        };
        SendMessage(s_tooltip, TTM_ADDTOOLW, IntPtr.Zero, ref faderInfo);
    }

    private static void DestroyTooltip()
    {
        if (s_tooltip != IntPtr.Zero) { DestroyWindow(s_tooltip); s_tooltip = IntPtr.Zero; }
    }

    private static RECT ToNativeRect(UiRect r) => new() { left = r.X, top = r.Y, right = r.X + r.Width, bottom = r.Y + r.Height };

    /// <summary>
    /// Centers the view on the image point under a navigator-thumbnail
    /// click/drag (V1.1). The pan math lives in <see cref="NavigatorMath"/>.
    /// </summary>
    private static void NavigatorPanTo(NavigatorLayout navigator, int x, int y, int clientW, int clientH, float scale)
    {
        UiRect placement = NavigatorMath.ImagePlacement(navigator.Thumbnail, s_imageW, s_imageH);
        (double ix, double iy) = NavigatorMath.ThumbToImage(placement, s_imageW, s_imageH, x, y);
        (s_panX, s_panY) = NavigatorMath.PanToCenterOn(ix, iy, s_imageW, s_imageH, s_zoom, s_rotationDeg);
        InvalidateAllWindows();
    }

    private static void HandleDockCommand(IntPtr hWnd, UiCommandId command)
    {
        if (!IsCommandEnabled(command)) return;
        switch (command)
        {
            case UiCommandId.Arrow: s_activeTool = UiCommandId.Arrow; break;
            case UiCommandId.Open: OpenImageDialog(hWnd); break;
            case UiCommandId.Save: TrySave("dock"); break;
            case UiCommandId.Undo: if (s_engine != IntPtr.Zero) akapen_undo(s_engine); s_dirty = true; RefreshNavigatorThumbnail(); break;
            case UiCommandId.Redo: if (s_engine != IntPtr.Zero) akapen_redo(s_engine); s_dirty = true; RefreshNavigatorThumbnail(); break;
            case UiCommandId.Pen: s_activeTool = UiCommandId.Pen; if (s_engine != IntPtr.Zero) akapen_set_tool(s_engine, 0); break;
            case UiCommandId.Eraser: s_activeTool = UiCommandId.Eraser; if (s_engine != IntPtr.Zero) akapen_set_tool(s_engine, 1); break;
            case UiCommandId.Pan: s_activeTool = UiCommandId.Pan; break;
            case UiCommandId.Zoom: s_activeTool = UiCommandId.Zoom; SetZoom(1.15f); break;
            case UiCommandId.Rotate: s_activeTool = UiCommandId.Rotate; RotateView(+15); break;
            case UiCommandId.BrushSize: s_brushFaderFocused = true; break;
            case UiCommandId.Color: break;
            case UiCommandId.Pressure:
                s_pressureCurve = s_pressureCurve switch { PressureCurve.Normal => PressureCurve.Soft, PressureCurve.Soft => PressureCurve.Hard, _ => PressureCurve.Normal };
                ApplyStyle(s_engine); break;
            case UiCommandId.Opacity: break;
            case UiCommandId.ViewFit: FitView(RenderWindow(hWnd)); break;
            case UiCommandId.Actual: s_zoom = 1; s_panX = s_panY = 0; break;
            case UiCommandId.Previous: StepSibling(hWnd, false); break;
            case UiCommandId.Next: StepSibling(hWnd, true); break;
            case UiCommandId.Settings:
                ShowSettingsDialog(hWnd);
                break;
        }
        UpdateProductMenu(hWnd);
        InvalidateRect(hWnd, IntPtr.Zero, false);
    }

    private static void ShowSettingsDialog(IntPtr owner)
    {
        if (s_settingsDialog != IntPtr.Zero) return;
        s_settingsOwner = owner;
        EnableWindow(owner, false);
        IntPtr hInstance = GetModuleHandle(null);
        RegisterWindowClass(s_settingsWndProc, hInstance, "AkapenSettingsWndClass");
        GetWindowRect(owner, out RECT ownerRect);
        int x = ownerRect.left + 80, y = ownerRect.top + 80;
        float scale = UiScale(owner);
        s_settingsDialog = CreateWindowEx(WS_EX_DLGMODALFRAME, "AkapenSettingsWndClass", "Akapen 設定",
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE, x, y, (int)(600 * scale), (int)(672 * scale),
            owner, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (s_settingsDialog == IntPtr.Zero) { EnableWindow(owner, true); s_settingsOwner = IntPtr.Zero; return; }
        MSG msg = default;
        while (s_settingsDialog != IntPtr.Zero && GetMessage(out msg, IntPtr.Zero, 0, 0) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // V1.1 settings-window look: Segoe UI text on a white surface with flat
    // bold section headers (no classic GROUPBOX frames), themed common
    // controls (app.manifest opts into comctl32 v6), and a footer button row.
    private static IntPtr s_settingsFont = IntPtr.Zero;
    private static IntPtr s_settingsHeaderFont = IntPtr.Zero;
    private static IntPtr s_settingsNoteFont = IntPtr.Zero;
    private static readonly IntPtr s_settingsSurfaceBrush = GetStockObject(WHITE_BRUSH);

    private static IntPtr SettingsWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
            {
                float scale = UiScale(hWnd);
                s_settingsFont = CreateSettingsFont(scale, 15, bold: false);
                s_settingsHeaderFont = CreateSettingsFont(scale, 16, bold: true);
                s_settingsNoteFont = CreateSettingsFont(scale, 13, bold: false);
                const int L = 28, W = 528, IndentW = 504;

                Header(hWnd, "ショートカット", L, 20);
                CreateSettingsControl(hWnd, "BUTTON", "Photoshop 準拠（既定）", BS_AUTORADIOBUTTON | BS_GROUP | WS_TABSTOP, L + 4, 48, IndentW, 24, SettingsKeymapPhotoshopId);
                Note(hWnd, "B=ブラシ、Ctrl+Shift+Z=やり直し、Ctrl+1=100%、R / Shift+R=回転（15°）", L + 24, 72);
                CreateSettingsControl(hWnd, "BUTTON", "CLIP STUDIO PAINT 準拠", BS_AUTORADIOBUTTON | WS_TABSTOP, L + 4, 96, IndentW, 24, SettingsKeymapClipStudioId);
                Note(hWnd, "P=ペン、Ctrl+Y=やり直し、- / ^=回転（15°）", L + 24, 120);
                Separator(hWnd, L, 150, W);

                Header(hWnd, "ペン", L, 162);
                CreateSettingsControl(hWnd, "BUTTON", "筆圧を使う（線の太さに反映する）", BS_AUTOCHECKBOX | WS_TABSTOP, L + 4, 190, IndentW, 24, SettingsPressureId);
                Note(hWnd, "オフのときは筆圧を無視し、一定の太さで描きます", L + 24, 214);
                Separator(hWnd, L, 240, W);

                Header(hWnd, "キャンバス", L, 252);
                CreateSettingsControl(hWnd, "BUTTON", "画像の範囲外を黒にする", BS_AUTOCHECKBOX | WS_TABSTOP, L + 4, 280, IndentW, 24, SettingsDarkCanvasId);
                Separator(hWnd, L, 316, W);

                Header(hWnd, "画像切り替え", L, 328);
                CreateSettingsControl(hWnd, "BUTTON", "左右矢印キーで切り替える前に自動保存する", BS_AUTOCHECKBOX | WS_TABSTOP, L + 4, 356, IndentW, 24, SettingsAutoSaveId);
                Separator(hWnd, L, 392, W);

                Header(hWnd, "保存先", L, 404);
                CreateSettingsControl(hWnd, "BUTTON", "画像と同じ階層に新規フォルダを作る", BS_AUTORADIOBUTTON | BS_GROUP | WS_TABSTOP, L + 4, 432, IndentW, 24, SettingsSiblingFolderId);
                CreateSettingsControl(hWnd, "STATIC", "フォルダ名", 0, L + 28, 462, 82, 22, 0);
                CreateSettingsControl(hWnd, "EDIT", s_outputFolderName, WS_BORDER | ES_AUTOHSCROLL | WS_TABSTOP, L + 116, 460, 384, 26, SettingsFolderNameId);
                CreateSettingsControl(hWnd, "BUTTON", "画像と同じフォルダに保存する", BS_AUTORADIOBUTTON | WS_TABSTOP, L + 4, 494, IndentW, 24, SettingsSourceFolderId);
                CreateSettingsControl(hWnd, "BUTTON", "指定フォルダに保存する", BS_AUTORADIOBUTTON | WS_TABSTOP, L + 4, 524, IndentW, 24, SettingsCustomFolderId);
                CreateSettingsControl(hWnd, "EDIT", s_customOutputPath, WS_BORDER | ES_AUTOHSCROLL | WS_TABSTOP, L + 28, 552, 472, 26, SettingsCustomPathId);

                CreateSettingsControl(hWnd, "BUTTON", "OK", BS_DEFPUSHBUTTON | WS_TABSTOP, 366, 594, 92, 32, SettingsOkId);
                CreateSettingsControl(hWnd, "BUTTON", "キャンセル", WS_TABSTOP, 466, 594, 92, 32, SettingsCancelId);
                SetButtonChecked(hWnd, SettingsPressureId, s_pressureEnabled);

                CheckRadioButton(hWnd, SettingsKeymapPhotoshopId, SettingsKeymapClipStudioId,
                    s_keymapPreset == KeymapPresetKind.ClipStudio ? SettingsKeymapClipStudioId : SettingsKeymapPhotoshopId);
                SetButtonChecked(hWnd, SettingsDarkCanvasId, s_canvasBackdrop == CanvasBackdrop.Black);
                SetButtonChecked(hWnd, SettingsAutoSaveId, s_autoSaveOnNavigate);
                CheckRadioButton(hWnd, SettingsSiblingFolderId, SettingsCustomFolderId, s_saveLocationMode switch
                {
                    SaveLocationMode.SourceFolder => SettingsSourceFolderId,
                    SaveLocationMode.CustomFolder => SettingsCustomFolderId,
                    _ => SettingsSiblingFolderId,
                });
                return IntPtr.Zero;
            }
            // A white surface with transparent label/checkbox backgrounds is
            // most of the difference between the old battleship-gray dialog
            // and a current-Windows settings page.
            case WM_CTLCOLORSTATIC:
            case WM_CTLCOLORBTN:
                SetBkMode(wParam, 1 /*TRANSPARENT*/);
                return s_settingsSurfaceBrush;
            case WM_ERASEBKGND:
            {
                GetClientRect(hWnd, out RECT rc);
                FillRect(wParam, ref rc, s_settingsSurfaceBrush);
                return (IntPtr)1;
            }
            case WM_COMMAND:
                switch ((int)(wParam.ToInt64() & 0xFFFF))
                {
                    case SettingsOkId:
                        s_keymapPreset = IsButtonChecked(hWnd, SettingsKeymapClipStudioId)
                            ? KeymapPresetKind.ClipStudio : KeymapPresetKind.Photoshop;
                        s_pressureEnabled = IsButtonChecked(hWnd, SettingsPressureId);
                        s_canvasBackdrop = IsButtonChecked(hWnd, SettingsDarkCanvasId) ? CanvasBackdrop.Black : CanvasBackdrop.White;
                        s_autoSaveOnNavigate = IsButtonChecked(hWnd, SettingsAutoSaveId);
                        s_saveLocationMode = IsButtonChecked(hWnd, SettingsSourceFolderId) ? SaveLocationMode.SourceFolder
                            : IsButtonChecked(hWnd, SettingsCustomFolderId) ? SaveLocationMode.CustomFolder
                            : SaveLocationMode.SiblingSubfolder;
                        s_outputFolderName = SanitizeFolderName(ReadControlText(hWnd, SettingsFolderNameId));
                        s_customOutputPath = ReadControlText(hWnd, SettingsCustomPathId).Trim();
                        PersistSettings();
                        if (!string.IsNullOrEmpty(s_imagePath))
                            SetOutputDirectory(s_customOutDir ?? ResolveOutputDirectory(s_imagePath));
                        if (s_engine != IntPtr.Zero)
                            akapen_set_canvas_dark(s_engine, s_canvasBackdrop == CanvasBackdrop.Black ? 1 : 0);
                        RenderCurrentFrame();
                        UpdateProductMenu(s_settingsOwner);
                        CloseSettingsDialog(hWnd);
                        return IntPtr.Zero;
                    case SettingsCancelId: CloseSettingsDialog(hWnd); return IntPtr.Zero;
                }
                break;
            case WM_CLOSE: CloseSettingsDialog(hWnd); return IntPtr.Zero;
            case WM_DESTROY:
                s_settingsDialog = IntPtr.Zero;
                foreach (IntPtr font in new[] { s_settingsFont, s_settingsHeaderFont, s_settingsNoteFont })
                    if (font != IntPtr.Zero) DeleteObject(font);
                s_settingsFont = s_settingsHeaderFont = s_settingsNoteFont = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr CreateSettingsFont(float scale, int pixelHeight, bool bold) =>
        CreateFont(-(int)MathF.Round(pixelHeight * scale), 0, 0, 0, bold ? 700 : 400, 0, 0, 0,
            1 /*DEFAULT_CHARSET*/, 0, 0, 5 /*CLEARTYPE_QUALITY*/, 0, "Segoe UI");

    private static void Header(IntPtr parent, string text, int x, int y) =>
        CreateSettingsControl(parent, "STATIC", text, 0, x, y, 300, 22, 0, s_settingsHeaderFont);

    private static void Note(IntPtr parent, string text, int x, int y) =>
        CreateSettingsControl(parent, "STATIC", text, 0, x, y, 500, 20, 0, s_settingsNoteFont);

    private static void Separator(IntPtr parent, int x, int y, int width) =>
        CreateSettingsControl(parent, "STATIC", "", 0x10 /*SS_ETCHEDHORZ*/, x, y, width, 1, 0);

    private static IntPtr CreateSettingsControl(IntPtr parent, string className, string text, uint style,
        int x, int y, int width, int height, int id, IntPtr font = default)
    {
        float scale = UiScale(parent);
        IntPtr control = CreateWindowEx(0, className, text, WS_CHILD | WS_VISIBLE | style,
            (int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale),
            parent, id == 0 ? IntPtr.Zero : (IntPtr)id, GetModuleHandle(null), IntPtr.Zero);
        if (control != IntPtr.Zero)
            SendMessage(control, WM_SETFONT, font != IntPtr.Zero ? font : s_settingsFont, (IntPtr)1);
        return control;
    }

    private static void SetButtonChecked(IntPtr dialog, int id, bool value) =>
        SendMessage(GetDlgItem(dialog, id), BM_SETCHECK, (IntPtr)(value ? BST_CHECKED : 0), IntPtr.Zero);

    private static bool IsButtonChecked(IntPtr dialog, int id) =>
        SendMessage(GetDlgItem(dialog, id), 0x00F0, IntPtr.Zero, IntPtr.Zero) == (IntPtr)BST_CHECKED;

    private static string ReadControlText(IntPtr dialog, int id)
    {
        IntPtr control = GetDlgItem(dialog, id);
        int length = GetWindowTextLength(control);
        var text = new StringBuilder(length + 1);
        GetWindowText(control, text, text.Capacity);
        return text.ToString();
    }

    private static void CloseSettingsDialog(IntPtr dialog)
    {
        IntPtr owner = s_settingsOwner;
        s_settingsOwner = IntPtr.Zero;
        DestroyWindow(dialog);
        if (owner != IntPtr.Zero)
        {
            EnableWindow(owner, true);
            SetForegroundWindow(owner);
            SetFocus(s_canvasWindow != IntPtr.Zero ? s_canvasWindow : owner);
            UpdateWindow(owner);
        }
    }

    private static IntPtr CanvasWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_POINTERDOWN:
            case WM_POINTERUPDATE:
            case WM_POINTERUP:
            case WM_LBUTTONDOWN:
            case WM_MOUSEMOVE:
            case WM_LBUTTONUP:
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_SETCURSOR:
                return WndProc(hWnd, msg, wParam, lParam);
            case WM_ERASEBKGND:
                return (IntPtr)1;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                if (hWnd == s_canvasWindow && (s_activeTool is UiCommandId.Pen or UiCommandId.Eraser))
                {
                    SetCursor(BrushCursor(hWnd));
                    return (IntPtr)1;
                }
                break;
            case WM_COMMAND:
                if (s_productMode && (int)(wParam.ToInt64() & 0xFFFF) == ProductOpenFolderId)
                {
                    OpenImageFolderDialog(hWnd);
                    return IntPtr.Zero;
                }
                if (s_productMode)
                {
                    switch ((int)(wParam.ToInt64() & 0xFFFF))
                    {
                        case ProductPressureEnabledId:
                            s_pressureEnabled = !s_pressureEnabled;
                            PersistSettings();
                            Console.WriteLine($"{LogPrefix} pressure {(s_pressureEnabled ? "enabled" : "disabled (fixed width)")}");
                            UpdateProductMenu(hWnd);
                            return IntPtr.Zero;
                        case ProductPressureNormalId:
                        case ProductPressureSoftId:
                        case ProductPressureHardId:
                            s_pressureCurve = (int)(wParam.ToInt64() & 0xFFFF) switch
                            {
                                ProductPressureSoftId => PressureCurve.Soft,
                                ProductPressureHardId => PressureCurve.Hard,
                                _ => PressureCurve.Normal,
                            };
                            ApplyStyle(s_engine);
                            Console.WriteLine($"{LogPrefix} pressure curve={s_pressureCurve}");
                            UpdateProductMenu(hWnd);
                            return IntPtr.Zero;
                        case ProductAboutId:
                            MessageBox(hWnd, AboutText(), "Akapen について", 0x40 /*MB_ICONINFORMATION*/);
                            return IntPtr.Zero;
                        case ProductHelpManualId:
                            OpenManual(hWnd);
                            return IntPtr.Zero;
                    }
                }
                if (s_productMode && TryGetMenuCommand(wParam, out UiCommandId menuCommand))
                {
                    HandleDockCommand(hWnd, menuCommand);
                    return IntPtr.Zero;
                }
                break;
            case WM_PAINT:
                PaintDock(hWnd);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return (IntPtr)1;
            case WM_POINTERDOWN:
            case WM_POINTERUPDATE:
            case WM_POINTERUP:
            {
                if (hWnd == s_canvasWindow && msg == WM_POINTERDOWN) SetFocus(hWnd);
                if (s_activeTool == UiCommandId.Arrow) return IntPtr.Zero;
                uint id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (!GetPointerInfo(id, out POINTER_INFO info)) return IntPtr.Zero;
                POINT pt = info.ptPixelLocation;
                ScreenToClient(hWnd, ref pt);
                if (info.pointerType == PT_PEN)
                {
                    s_lastPenInputUtc = DateTime.UtcNow;
                    GetPointerPenInfo(id, out POINTER_PEN_INFO pen);
                    // V1.1.1: pressure comes from the Windows-standard pointer
                    // path and is ON by default; the settings toggle flattens
                    // it to a fixed 1.0 (constant line width) when off.
                    double pressure = s_pressureEnabled ? pen.pressure / 1024.0 : 1.0;
                    int phase = msg == WM_POINTERDOWN ? 0 : (msg == WM_POINTERUP ? 2 : 1);
                    if (phase == 0) s_activePenPointers.Add(id);
                    FeedPointer(hWnd, pt.x, pt.y, pressure, 0, phase);
                    if (phase == 2 && s_pressureEnabled && s_engine != IntPtr.Zero && akapen_pressure_stuck(s_engine) != 0)
                        Console.WriteLine($"{LogPrefix} WARN: pen pressure was unavailable or constant; using the reported fallback pressure");
                    if (phase == 2) s_activePenPointers.Remove(id);
                }
                else if (info.pointerType == PT_TOUCH && s_activePenPointers.Count == 0)
                {
                    int phase = msg == WM_POINTERDOWN ? 0 : (msg == WM_POINTERUP ? 2 : 1);
                    HandleTouchNavigation(id, pt, phase);
                }
                // Touch is navigation input and is ignored while a pen is
                // down: this is the MVP palm-rejection policy.
                return IntPtr.Zero;
            }
            case WM_LBUTTONDOWN:
                if (hWnd == s_canvasWindow) { s_brushFaderFocused = false; SetFocus(hWnd); InvalidateAllWindows(); }
                if ((!s_productMode || hWnd == s_mainWindow) && GetClientRect(hWnd, out RECT dockRect))
                {
                    int x = GetLParamX(lParam), y = GetLParamY(lParam);
                    float scale = UiScale(hWnd);
                    if (s_productMode && s_engine == IntPtr.Zero && !s_isLoading)
                    {
                        EmptyStateLayout empty = DockLayout.EmptyState(dockRect.right, dockRect.bottom, scale);
                        if (empty.FolderButton.Contains(x, y)) OpenImageFolderDialog(hWnd);
                        return IntPtr.Zero;
                    }
                    // Navigator (V1.1): click/drag the thumbnail recenters the
                    // view; the −/+ buttons zoom.
                    if (s_engine != IntPtr.Zero)
                    {
                        NavigatorLayout navigator = DockLayout.Navigator(dockRect.right, dockRect.bottom, s_dockSide, scale);
                        if (navigator.ZoomOut.Contains(x, y)) { SetZoom(1.0f / 1.15f); return IntPtr.Zero; }
                        if (navigator.ZoomIn.Contains(x, y)) { SetZoom(1.15f); return IntPtr.Zero; }
                        if (navigator.Thumbnail.Contains(x, y))
                        {
                            NavigatorPanTo(navigator, x, y, dockRect.right, dockRect.bottom, scale);
                            s_draggingNavigator = true;
                            SetCapture(hWnd);
                            return IntPtr.Zero;
                        }
                    }
                    int? paletteIndex = DockLayout.HitTestPalette(x, y, dockRect.right, dockRect.bottom, s_dockSide, scale);
                    if (paletteIndex.HasValue)
                    {
                        s_color = AkapenPalette.Colors[paletteIndex.Value].Rgba;
                        ApplyStyle(s_engine); s_brushFaderFocused = false; InvalidateAllWindows();
                        return IntPtr.Zero;
                    }
                    UiRect fader = DockLayout.BrushFaderBounds(dockRect.right, dockRect.bottom, s_dockSide, scale);
                    UiRect faderTrack = BrushFader.TrackBounds(fader, scale);
                    if (faderTrack.Contains(x, y))
                    {
                        s_brushSize = BrushFader.ValueFromY(y, faderTrack); ApplyStyle(s_engine);
                        s_draggingBrushFader = true; s_brushFaderFocused = true; SetFocus(hWnd); SetCapture(hWnd);
                        InvalidateAllWindows(); return IntPtr.Zero;
                    }
                    var command = DockLayout.HitTest(x, y, dockRect.right, dockRect.bottom, s_dockSide, scale);
                    if (command.HasValue) { s_brushFaderFocused = false; HandleDockCommand(hWnd, command.Value); return IntPtr.Zero; }
                }
                if (s_activePenPointers.Count == 0 && (DateTime.UtcNow - s_lastPenInputUtc).TotalMilliseconds > 100 && s_engine != IntPtr.Zero)
                {
                    if (s_activeTool == UiCommandId.Arrow) return IntPtr.Zero;
                    SetCapture(hWnd); s_leftDown = true;
                    if ((GetKeyState(VK_SPACE) & 0x8000) != 0)
                    { s_panDown = true; s_lastPanX = GetLParamX(lParam); s_lastPanY = GetLParamY(lParam); }
                    else { if (!s_pressureFallbackWarned) { Console.WriteLine($"{LogPrefix} WARN: mouse input has no pressure; using fixed pressure 1.0"); s_pressureFallbackWarned = true; } FeedPointer(hWnd, GetLParamX(lParam), GetLParamY(lParam), 1.0, 2, 0); }
                }
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                if (s_productMode && hWnd == s_mainWindow && GetClientRect(hWnd, out RECT hoverRect))
                {
                    if (s_engine == IntPtr.Zero) return IntPtr.Zero;
                    if (s_draggingNavigator)
                    {
                        float navScale = UiScale(hWnd);
                        NavigatorLayout navigator = DockLayout.Navigator(hoverRect.right, hoverRect.bottom, s_dockSide, navScale);
                        NavigatorPanTo(navigator, GetLParamX(lParam), GetLParamY(lParam), hoverRect.right, hoverRect.bottom, navScale);
                        return IntPtr.Zero;
                    }
                    if (s_draggingBrushFader)
                    {
                        float scale = UiScale(hWnd);
                        UiRect control = DockLayout.BrushFaderBounds(hoverRect.right, hoverRect.bottom, s_dockSide, scale);
                        s_brushSize = BrushFader.ValueFromY(GetLParamY(lParam), BrushFader.TrackBounds(control, scale));
                        ApplyStyle(s_engine); InvalidateAllWindows(); return IntPtr.Zero;
                    }
                    UiCommandId? hovered = DockLayout.HitTest(GetLParamX(lParam), GetLParamY(lParam), hoverRect.right, hoverRect.bottom, s_dockSide, UiScale(hWnd));
                    if (hovered != s_hoverCommand) { s_hoverCommand = hovered; InvalidateRect(hWnd, IntPtr.Zero, false); }
                }
                if (s_leftDown && s_engine != IntPtr.Zero)
                {
                    int x = GetLParamX(lParam), y = GetLParamY(lParam);
                    if (s_panDown) { s_panX += x - s_lastPanX; s_panY += y - s_lastPanY; s_lastPanX = x; s_lastPanY = y; }
                    else FeedPointer(hWnd, x, y, 1.0, 2, 1);
                }
                return IntPtr.Zero;
            case WM_DROPFILES:
                if (s_productMode && !s_isLoading)
                {
                    uint length = DragQueryFile(wParam, 0, null, 0);
                    var dropped = new StringBuilder((int)length + 1);
                    DragQueryFile(wParam, 0, dropped, (uint)dropped.Capacity);
                    DragFinish(wParam);
                    OpenDroppedPath(hWnd, dropped.ToString());
                }
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (s_draggingNavigator)
                {
                    s_draggingNavigator = false; ReleaseCapture(); return IntPtr.Zero;
                }
                if (s_draggingBrushFader)
                {
                    s_draggingBrushFader = false; ReleaseCapture(); InvalidateAllWindows(); return IntPtr.Zero;
                }
                if (s_leftDown && s_engine != IntPtr.Zero)
                {
                    if (!s_panDown) FeedPointer(hWnd, GetLParamX(lParam), GetLParamY(lParam), 1.0, 2, 2);
                    s_leftDown = false; s_panDown = false; ReleaseCapture();
                }
                return IntPtr.Zero;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            {
                int vk = (int)(wParam.ToInt64() & 0xFFFF);
                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
                IntPtr owner = s_mainWindow != IntPtr.Zero ? s_mainWindow : hWnd;
                if (s_brushFaderFocused && TryMapBrushFaderKey(vk, out BrushFaderKey faderKey))
                {
                    s_brushSize = BrushFader.Adjust(s_brushSize, faderKey); ApplyStyle(s_engine); InvalidateAllWindows();
                }
                else if (ctrl && vk == VK_S) TrySave("Ctrl+S");
                else if (ctrl && vk == VK_O) OpenImageDialog(owner);
                // Shared Rust keymap with the user-selected preset (V1.1:
                // Photoshop by default, CLIP STUDIO via settings). Arrows,
                // rotate (R / Shift+R or -/^), PageUp/Down all resolve here.
                else if (ResolvePhysicalKey(vk) is int physical && physical != 0 &&
                         akapen_resolve_key_preset((int)s_keymapPreset, 0, physical, ctrl ? 1 : 0, shift ? 1 : 0, alt ? 1 : 0, 0, 0) is int action && action != 0)
                    ExecuteResolvedShortcut(owner, action);
                else if (vk == VK_ADD || vk == VK_OEM_PLUS) SetZoom(1.15f);
                else if (vk == VK_SUBTRACT || vk == VK_OEM_MINUS) SetZoom(1.0f / 1.15f);
                else if (vk == VK_OEM_4) SetBrushSize(-1.0f);
                else if (vk == VK_OEM_6) SetBrushSize(1.0f);
                else if (vk == VK_F) FitView(RenderWindow(hWnd));
                UpdateProductMenu(owner);
                return IntPtr.Zero;
            }
            case WM_SIZE:
                if (s_productMode && hWnd == s_mainWindow)
                {
                    LayoutProductChildren(hWnd);
                    if (s_engine != IntPtr.Zero) FitView(RenderWindow(hWnd));
                    RenderCurrentFrame();
                    CreateTooltips(hWnd);
                    UpdateProductMenu(hWnd);
                }
                else
                {
                    if (s_engine != IntPtr.Zero)
                    { long lp = lParam.ToInt64(); uint w = (uint)(lp & 0xFFFF), h = (uint)((lp >> 16) & 0xFFFF); if (w > 0 && h > 0) { akapen_render_resize(s_engine, w, h, 1.0f); FitView(hWnd); RenderCurrentFrame(); } }
                    CreateTooltips(hWnd);
                    UpdateProductMenu(hWnd);
                }
                return IntPtr.Zero;
            case WM_DPICHANGED:
                if (s_productMode && hWnd == s_mainWindow && lParam != IntPtr.Zero)
                {
                    RECT suggested = Marshal.PtrToStructure<RECT>(lParam);
                    SetWindowPos(hWnd, IntPtr.Zero, suggested.left, suggested.top,
                        suggested.right - suggested.left, suggested.bottom - suggested.top, SWP_NOZORDER | SWP_NOACTIVATE);
                    LayoutProductChildren(hWnd); CreateTooltips(hWnd);
                }
                return IntPtr.Zero;
            case WM_CLOSE:
                if (s_dirty && !TrySave("on-close")) return IntPtr.Zero;
                DestroyWindow(hWnd); return IntPtr.Zero;
            case WM_DESTROY:
                DestroyTooltip();
                if (s_productMenu != IntPtr.Zero) { DestroyMenu(s_productMenu); s_productMenu = IntPtr.Zero; }
                PostQuitMessage(0); return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void HandleTouchNavigation(uint id, POINT point, int phase)
    {
        if (phase == 2)
        {
            s_touchPoints.Remove(id);
            s_lastPinchDistance = 0;
            if (s_touchPoints.Count == 1)
            {
                POINT remaining = s_touchPoints.Values.First();
                s_lastTouchX = remaining.x; s_lastTouchY = remaining.y;
            }
            s_touchPanDown = s_touchPoints.Count > 0;
            return;
        }

        s_touchPoints[id] = point;
        if (s_touchPoints.Count >= 2)
        {
            POINT[] touches = s_touchPoints.Values.Take(2).ToArray();
            double dx = touches[1].x - touches[0].x, dy = touches[1].y - touches[0].y;
            double distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            int centerX = (touches[0].x + touches[1].x) / 2;
            int centerY = (touches[0].y + touches[1].y) / 2;
            if (s_lastPinchDistance > 0)
                s_zoom = Math.Clamp(s_zoom * (float)(distance / s_lastPinchDistance), 0.05f, 32.0f);
            if (s_touchPanDown) { s_panX += centerX - s_lastTouchX; s_panY += centerY - s_lastTouchY; }
            s_lastPinchDistance = distance;
            s_lastTouchX = centerX; s_lastTouchY = centerY; s_touchPanDown = true;
        }
        else
        {
            if (s_touchPanDown) { s_panX += point.x - s_lastTouchX; s_panY += point.y - s_lastTouchY; }
            s_lastTouchX = point.x; s_lastTouchY = point.y; s_touchPanDown = true;
            s_touchPointerId = id;
        }
        InvalidateAllWindows();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CheckMenuRadioItem(IntPtr hMenu, uint idFirst, uint idLast, uint idCheck, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool ValidateRect(IntPtr hWnd, IntPtr rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int objectId);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rasterOperation);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr cursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    // Explicitly the W export: the default ANSI resolution (DefWindowProcA)
    // reinterprets the UTF-16 caption set by CreateWindowExW / SetWindowTextW
    // as ANSI, truncating the title at the first interleaved NUL byte — the
    // V1.1 title bar showed just "A" instead of "Akapen".
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool enable);

    [DllImport("user32.dll")]
    private static extern bool CheckRadioButton(IntPtr hDlg, int first, int last, int check);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr hDlg, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool WaitMessage();

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetPointerInfo(uint pointerId, out POINTER_INFO pointerInfo);

    [DllImport("user32.dll")]
    private static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint file, StringBuilder? path, uint pathLength);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr drop);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int iconIndex, IntPtr[] largeIcons, IntPtr[] smallIcons, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, ref TOOLINFO lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800, MF_POPUP = 0x0010;
    private const uint MF_BYCOMMAND = 0x0000, MF_ENABLED = 0x0000, MF_GRAYED = 0x0001;
    private const uint MF_CHECKED = 0x0008, MF_UNCHECKED = 0x0000;

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr hMenu, uint idCheckItem, uint uCheck);
}
