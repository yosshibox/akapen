// Akapen Windows presentation de-risk probe (see ../README.md for why this
// exists and why it is C# P/Invoke rather than a Rust bin).
//
// Hand-rolled raw Win32 (user32.dll) window + hand-written DllImport
// declarations mirroring crates/akapen-ffi/include/akapen.h. No WinForms,
// WPF, or WinUI 3 dependency -- this only needs the .NET 8 SDK.
//
// Two modes share the same executable:
//   1. Original "presentation smoke test" (default): drive a scripted stroke
//      through akapen_pointer for >=300 frames across >=3 resizes with no
//      crash, print backend / present mode / max frame latency, exit 0/1 on
//      pass/fail. This is what the Session 0 -friendly SSH-only check runs.
//   2. Interactive mode (--interactive, or when an image path is passed):
//      show the window, let the mouse actually draw, and export the 3-file
//      _review/ set with Ctrl+S or on close. This is the minimum-viable
//      "developer can see it working" surface -- not the future WinUI 3
//      shell (that is bindings/dotnet, M2), just the existing de-risk spike
//      wired up to real WM_MOUSE / WM_KEYDOWN messages.
//
// Accept criteria for mode 1 (unchanged, reported on stdout):
//   - present succeeds for >= 300 frames with no crash
//   - >= 3 resizes survived without error
//   - actual backend / present mode / max frame latency, read back from the
//     engine itself (crates/akapen-render/src/canvas.rs::backend_info)

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
    private static string s_stem = "probe";
    private static bool s_leftDown;
    private static bool s_dirty;
    private static bool s_saveOnCloseAttempted;

    private static int Main(string[] args)
    {
        Console.WriteLine("[probe] Akapen Windows HWND/DX12 presentation de-risk probe");
        akapen_enable_diagnostic_logging();

        bool interactive = false;
        bool headlessExport = false;
        string? imagePath = null;
        string? cliOutDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--interactive" || a == "-i")
            {
                interactive = true;
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
                if (!headlessExport) interactive = true; // an image path implies interactive unless headless
            }
            else if (cliOutDir is null)
            {
                cliOutDir = a;
            }
        }

        try
        {
            if (headlessExport) return RunHeadlessExport(imagePath, cliOutDir);
            return interactive ? RunInteractive(imagePath, cliOutDir) : RunSmokeTest();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe] FAIL: unhandled exception: {ex}");
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
        akapen_set_size(engine, 6.0f);
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

    // ── Mode 1: the original scripted presentation smoke test ─────────────
    //
    // Unchanged in intent from the pre-interactive probe: create a window,
    // attach the engine, feed a scripted 4-point stroke every ~80 frames,
    // resize the swapchain three times mid-run, and report pass/fail on
    // stdout. Kept as the default so the existing SSH-only accept-criteria
    // check keeps working without an --interactive flag.
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
    // Minimum-viable interactive surface on top of the same raw user32 host:
    //   * WM_LBUTTONDOWN/MOVE/UP -> akapen_pointer (mouse-kind, pressure 1.0)
    //   * Ctrl+S                 -> akapen_export_to_dir into s_outDir
    //   * WM_CLOSE               -> auto-save if dirty, then destroy
    // Every mismatched thing is out of scope on purpose (pen/pressure via
    // WM_POINTER, palm rejection UI, undo/redo UI, coordinate remap on
    // window resize, GUI save dialog): this is the de-risk spike, not M2.
    private static int RunInteractive(string? imagePath, string? cliOutDir)
    {
        // 1. Resolve save target. Default: %TEMP%\akapen-review with a stable
        //    stem so repeated saves collision-name inside the same folder.
        s_outDir = cliOutDir ?? Path.Combine(Path.GetTempPath(), "akapen-review");
        s_stem = !string.IsNullOrEmpty(imagePath)
            ? SanitizeStem(Path.GetFileNameWithoutExtension(imagePath))
            : "probe";
        Console.WriteLine($"[probe] interactive mode; save target dir='{s_outDir}' stem='{s_stem}'");

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
                Console.WriteLine($"[probe] akapen_open_image failed for '{imagePath}'; falling back to blank 800x600");
                engine = akapen_new(imgW, imgH);
            }
            else
            {
                akapen_size(engine, out imgW, out imgH);
                Console.WriteLine($"[probe] opened image {imagePath} ({imgW}x{imgH})");
            }
        }
        else
        {
            engine = akapen_new(imgW, imgH);
        }
        if (engine == IntPtr.Zero)
        {
            Console.WriteLine("[probe] FAIL: akapen_new returned null");
            return 5;
        }
        s_engine = engine;

        // Defaults match the mac shell's red-ink pen (spec §5.1).
        akapen_set_tool(engine, 0);            // Pen
        akapen_set_color(engine, 0xFF0000FFu); // opaque red
        akapen_set_size(engine, 6.0f);

        // 3. Register class + create window sized so its client area is the
        //    engine buffer. That keeps mouse coords and engine coords aligned
        //    without a remap for the initial pose. A user resize breaks the
        //    alignment (known limitation for the probe -- M2 does the remap).
        var wndProc = new WndProcDelegate(WndProc);
        IntPtr hInstance = GetModuleHandle(null);
        if (!RegisterProbeWindowClass(wndProc, hInstance, "AkapenProbeInteractiveWndClass"))
        {
            akapen_free(engine);
            s_engine = IntPtr.Zero;
            return 3;
        }

        var rc = new RECT { left = 0, top = 0, right = (int)imgW, bottom = (int)imgH };
        AdjustWindowRect(ref rc, WS_OVERLAPPEDWINDOW, false);
        int winW = rc.right - rc.left;
        int winH = rc.bottom - rc.top;

        IntPtr hwnd = CreateWindowEx(
            0,
            "AkapenProbeInteractiveWndClass",
            "Akapen Probe (interactive) - Ctrl+S to save, close to auto-save",
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT,
            winW, winH,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"[probe] FAIL: CreateWindowEx failed, Win32Error={Marshal.GetLastWin32Error()}");
            akapen_free(engine);
            s_engine = IntPtr.Zero;
            return 4;
        }
        Console.WriteLine($"[probe] created HWND=0x{hwnd.ToInt64():X}");

        ShowWindow(hwnd, SW_SHOWNORMAL);
        UpdateWindow(hwnd);

        GetClientRect(hwnd, out RECT client);
        uint clientW = (uint)Math.Max(1, client.right - client.left);
        uint clientH = (uint)Math.Max(1, client.bottom - client.top);
        Console.WriteLine($"[probe] initial client size = {clientW}x{clientH}");

        // 4. Attach the GPU surface. Same handshake as the smoke test.
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
            Console.Error.WriteLine($"[probe] FAIL: akapen_render_attach returned {attachRc} ({DescribeAttachCode(attachRc)}); reason: {reason}");
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
        Console.WriteLine($"[probe] akapen_render_attach OK; {ReadBackendInfo(engine)}");
        Console.WriteLine("[probe] draw with the left mouse button; press Ctrl+S to save; close the window to auto-save + exit.");

        // 5. Message pump + per-frame render. PeekMessage keeps the pump non-
        //    blocking; akapen_render_frame with the DX12 backend's Fifo present
        //    mode paces us to vsync, so this is not a busy loop.
        int framesRendered = 0;
        while (true)
        {
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

            GetClientRect(hwnd, out RECT cur);
            uint curW = (uint)Math.Max(1, cur.right - cur.left);
            uint curH = (uint)Math.Max(1, cur.bottom - cur.top);
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

        Console.WriteLine($"[probe] interactive session ended; frames={framesRendered}");
        Console.WriteLine($"[probe] final {ReadBackendInfo(engine)}");

        akapen_render_detach(engine);
        s_engine = IntPtr.Zero;  // block any stray late WndProc access before free
        akapen_free(engine);
        GC.KeepAlive(wndProc);
        return 0;
    }

    // Registers the plain WS_OVERLAPPEDWINDOW class both modes share. Kept as
    // a helper so the two entry points don't diverge on class flags.
    private static bool RegisterProbeWindowClass(WndProcDelegate wndProc, IntPtr hInstance, string className)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
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

    private static void PumpMessages()
    {
        while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    // Runs the 3-file export and reports the outcome to stdout. Callers own
    // the "why we're saving" narration (explicit vs. auto-on-close).
    private static void TrySave(string reasonTag)
    {
        if (s_engine == IntPtr.Zero)
        {
            Console.WriteLine($"[probe] {reasonTag} save skipped: no engine");
            return;
        }
        int rc = akapen_export_to_dir(s_engine, s_outDir, s_stem);
        if (rc == 0)
        {
            Console.WriteLine($"[probe] {reasonTag} saved 3 files to {s_outDir} (stem={s_stem})");
            s_dirty = false;
        }
        else
        {
            Console.WriteLine($"[probe] {reasonTag} save failed rc={rc} (dir='{s_outDir}' stem='{s_stem}')");
        }
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

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_enable_diagnostic_logging();

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr akapen_open_image([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr akapen_new(uint width, uint height);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_free(IntPtr engine);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_size(IntPtr engine, out uint w, out uint h);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_tool(IntPtr engine, int tool);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_color(IntPtr engine, uint rgba);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_size(IntPtr engine, float px);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_pointer(IntPtr engine, double x, double y, double pressure, int kind, int phase);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_export_to_dir(
        IntPtr engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dir,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stem);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_render_attach(IntPtr engine, ref AkapenSurfaceDesc desc);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_render_resize(IntPtr engine, uint width, uint height, float scale);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_render_frame(IntPtr engine, AkapenViewTransform view);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_render_detach(IntPtr engine);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern int akapen_render_available(IntPtr engine);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr akapen_render_backend_info(IntPtr engine, byte[] outBuf, UIntPtr outLen);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr akapen_render_last_attach_error(IntPtr engine, byte[] outBuf, UIntPtr outLen);

    // ── raw Win32 (user32.dll / kernel32.dll) ───────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SW_SHOWNORMAL = 1;
    private const uint PM_REMOVE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;

    // Window / input messages we handle directly (everything else falls
    // through to DefWindowProc).
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;

    private const int VK_CONTROL = 0x11;
    private const int VK_S = 0x53;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

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

    // WM_LBUTTONDOWN/UP/MOUSEMOVE pack the client-area x/y into lParam as two
    // signed 16-bit ints (LOWORD=x, HIWORD=y). Sign-extend explicitly so a
    // drag off the top/left edge of the client area still gets a well-formed
    // (negative) coordinate.
    private static int GetLParamX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int GetLParamY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                if (s_engine != IntPtr.Zero)
                {
                    double x = GetLParamX(lParam);
                    double y = GetLParamY(lParam);
                    SetCapture(hWnd);
                    s_leftDown = true;
                    s_dirty = true;
                    akapen_pointer(s_engine, x, y, 1.0, /*Mouse*/ 2, /*Down*/ 0);
                }
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (s_leftDown && s_engine != IntPtr.Zero)
                {
                    double x = GetLParamX(lParam);
                    double y = GetLParamY(lParam);
                    akapen_pointer(s_engine, x, y, 1.0, 2, /*Move*/ 1);
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (s_leftDown && s_engine != IntPtr.Zero)
                {
                    double x = GetLParamX(lParam);
                    double y = GetLParamY(lParam);
                    akapen_pointer(s_engine, x, y, 1.0, 2, /*Up*/ 2);
                    s_leftDown = false;
                    ReleaseCapture();
                }
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            {
                int vk = (int)(wParam.ToInt64() & 0xFFFF);
                // Ctrl+S: manual save. GetKeyState's high bit is set while
                // the key is currently down (works cross-platform under user32).
                if (vk == VK_S && (GetKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    TrySave("Ctrl+S");
                }
                return IntPtr.Zero;
            }

            case WM_SIZE:
                // Reconfigure the swapchain to the new client size. Buffer
                // (engine image) coords do NOT track the window size on the
                // probe: a user resize deliberately decouples cursor coords
                // from ink coords (documented limitation; M2 does the remap).
                if (s_engine != IntPtr.Zero)
                {
                    long lp = lParam.ToInt64();
                    uint w = (uint)(lp & 0xFFFF);
                    uint h = (uint)((lp >> 16) & 0xFFFF);
                    if (w > 0 && h > 0)
                    {
                        akapen_render_resize(s_engine, w, h, 1.0f);
                    }
                }
                return IntPtr.Zero;

            case WM_CLOSE:
                // Auto-save on close if the user drew anything. If the save
                // fails we still let the window close (logged; the probe
                // deliberately does not block exit on save errors).
                if (s_dirty && !s_saveOnCloseAttempted)
                {
                    s_saveOnCloseAttempted = true;
                    TrySave("on-close");
                }
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
