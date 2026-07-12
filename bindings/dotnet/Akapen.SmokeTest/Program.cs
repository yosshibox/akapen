// End-to-end P/Invoke smoke test for the .NET face of the tri-face build
// discipline (spec §9). Akapen.Native's own build (see ../Akapen.Native) only
// proves the C# side *compiles* against the csbindgen-generated
// NativeMethods — it never calls into akapen.dll, so a bad DllImport (wrong
// DLL name, missing native dependency, a struct layout that does not match
// the Rust `#[repr(C)]` side) would only surface at product build time, not
// here in CI.
//
// This console app closes that gap by actually loading akapen.dll and
// driving three real P/Invoke calls: create an engine, read its size back,
// and route one pointer event through the palm-rejection state machine. No
// GUI/HWND/DX12 surface is touched — CI runs this on windows-latest under
// Session 0, which has no desktop to attach a real render surface to; the
// CPU-only handle lifecycle exercised here needs none of that.
//
// Usage: dotnet bin/Release/net8.0/Akapen.SmokeTest.dll
// (akapen.dll must already be next to it — the CI step that runs this copies
// target/release/akapen.dll into the output directory first.)
// Exit 0 on success; exit 2 (with a stderr message) on the first assertion
// that fails.

using Akapen.Native;

namespace Akapen.SmokeTest;

internal static class Program
{
    private static unsafe int Main()
    {
        var engine = NativeMethods.akapen_new(100, 100);
        if (engine == null)
        {
            Console.Error.WriteLine("akapen_new(100, 100) returned null");
            return 2;
        }

        uint width = 0;
        uint height = 0;
        NativeMethods.akapen_size(engine, &width, &height);
        if (width != 100 || height != 100)
        {
            Console.Error.WriteLine($"akapen_size returned {width}x{height}, expected 100x100");
            NativeMethods.akapen_free(engine);
            return 2;
        }

        // Pen (kind=0) going Down (phase=0) at now_ms=1000 with fresh state
        // (no pen seen yet, no lock armed) must route to Draw (route_code 0).
        // Passing a real AkapenPalmState (rather than null) also exercises the
        // struct's #[repr(C)] layout across the boundary, not just the scalar
        // arguments.
        var palmState = new AkapenPalmState
        {
            pen_down = 0,
            lock_active = 0,
            lock_until_ms = 0,
        };
        var route = NativeMethods.akapen_palm_route(&palmState, kind: 0, phase: 0, now_ms: 1000);
        if (route != 0)
        {
            Console.Error.WriteLine($"akapen_palm_route returned {route}, expected 0 (Draw)");
            NativeMethods.akapen_free(engine);
            return 2;
        }

        NativeMethods.akapen_free(engine);

        Console.WriteLine(
            $"[smoke] akapen_new/akapen_size/akapen_palm_route/akapen_free OK "
            + $"(size={width}x{height}, route=Draw)");
        return 0;
    }
}
