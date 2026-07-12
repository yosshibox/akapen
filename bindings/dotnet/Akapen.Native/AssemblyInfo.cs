// Placeholder hand-written source so `dotnet build` succeeds even if the
// csbindgen output has not yet been generated. Real bindings live in
// `Generated/NativeMethods.g.cs` (emitted by
// `cargo run -p akapen-dotnet-bindgen`; regenerated in CI on every push).
//
// Do NOT add real API here — the M5 public surface (spec §9) will land in a
// separate managed wrapper. `Akapen.Native` is a CI-only face until then.

// NativeMethods and the generated structs are `internal` on purpose (spec §7.2
// keeps the C ABI reviewable but not the product API). The smoke-test console
// app (../Akapen.SmokeTest) needs to call them directly to prove the P/Invoke
// boundary actually loads and runs akapen.dll, so it is declared a friend
// assembly here rather than making the surface public. The Windows shell
// (apps/windows/AkapenApp, spec §9 M2 first-cut) is granted the same friend
// access for the same reason — it drives the C ABI directly rather than
// waiting on the M5 public managed API surface.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Akapen.SmokeTest")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AkapenApp")]

namespace Akapen.Native;

internal static class AssemblyInfo
{
    /// <summary>
    /// Build-time marker so the class library has at least one type to compile
    /// against on a fresh checkout with no generated file. Regenerating the
    /// bindings (see README) is what actually makes the P/Invoke surface real.
    /// </summary>
    internal const string GeneratedFile = "Generated/NativeMethods.g.cs";
}
