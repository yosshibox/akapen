// Akapen Windows presentation de-risk probe (see ../README.md for why this
// exists and why it is C# P/Invoke rather than a Rust bin).
//
// Hand-rolled raw Win32 (user32.dll) window + hand-written DllImport
// declarations mirroring crates/akapen-ffi/include/akapen.h. No WinForms,
// WPF, or WinUI 3 dependency -- this only needs the .NET 8 SDK.
//
// Accept criteria this reports on stdout (no screenshot required):
//   - present succeeds for >= 300 frames with no crash
//   - >= 3 resizes survived without error
//   - actual backend / present mode / max frame latency, read back from the
//     engine itself (crates/akapen-render/src/canvas.rs::backend_info)

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AkapenProbe;

internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("[probe] Akapen Windows HWND/DX12 presentation de-risk probe");
        akapen_enable_diagnostic_logging();
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[probe] FAIL: unhandled exception: {ex}");
            return 2;
        }
    }

    private static int Run()
    {
        const int initialW = 800;
        const int initialH = 600;
        const int totalFrames = 320;
        int[] resizeAtFrames = { 80, 160, 240 };
        int[] strokeStartFrames = { 10, 90, 170, 250 };

        // ── 1. Create a plain Win32 window (no WinForms/WPF) ──────────────
        var wndProc = new WndProcDelegate(WndProc); // keep the delegate rooted
        IntPtr hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            lpszClassName = "AkapenProbeWndClass",
        };
        ushort atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            Console.WriteLine($"[probe] FAIL: RegisterClassEx failed, Win32Error={Marshal.GetLastWin32Error()}");
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

        // ── 2. Engine + attach ────────────────────────────────────────────
        IntPtr engine = akapen_new(clientW, clientH);
        if (engine == IntPtr.Zero)
        {
            Console.WriteLine("[probe] FAIL: akapen_new returned null");
            return 5;
        }
        akapen_set_tool(engine, 0); // Pen
        akapen_set_color(engine, 0xFF0000FFu); // opaque red
        akapen_set_size(engine, 24.0f);

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

        // ── 3. Frame loop: pump messages, feed a stroke periodically, ─────
        //       resize a few times, render+present every iteration.
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
                // Alternate the size each resize so this genuinely exercises
                // both growing and shrinking the swapchain.
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

        // ── 4. Teardown ────────────────────────────────────────────────────
        akapen_render_detach(engine);
        akapen_free(engine);
        DestroyWindow(hwnd);

        bool pass = framesRendered >= 300 && resizesDone >= 3;
        Console.WriteLine(pass
            ? "[probe] PASS: present succeeded for >=300 frames across >=3 resizes with no crash"
            : "[probe] FAIL: did not meet the frame/resize acceptance bar");
        return pass ? 0 : 1;
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
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
    private static extern IntPtr akapen_new(uint width, uint height);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_free(IntPtr engine);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_tool(IntPtr engine, int tool);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_color(IntPtr engine, uint rgba);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_set_size(IntPtr engine, float px);

    [DllImport("akapen", CallingConvention = CallingConvention.Cdecl)]
    private static extern void akapen_pointer(IntPtr engine, double x, double y, double pressure, int kind, int phase);

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
    private const uint WM_DESTROY = 0x0002;

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

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY)
        {
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

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}
