# bindings/dotnet

.NET face of the tri-face build discipline (spec §9). This is a **CI-only
scaffold at M1** — the WinUI 3 / .NET 8 shell itself lands in M2 (spec §9 M2);
here we only prove that the C ABI in `crates/akapen-ffi` can be P/Invoke-bound
from managed code on every push.

Layout:

```
Akapen.Native/
├─ Akapen.Native.csproj           # net8.0 class library (internal-only)
├─ AssemblyInfo.cs                # hand-written placeholder + InternalsVisibleTo
└─ Generated/
   └─ NativeMethods.g.cs          # emitted by csbindgen (gitignored)

Akapen.SmokeTest/
├─ Akapen.SmokeTest.csproj        # net8.0 console app, ProjectReference → Akapen.Native
└─ Program.cs                    # calls akapen_new/akapen_size/akapen_palm_route/akapen_free
```

`Akapen.Native`'s own build only proves the C# side compiles against the
csbindgen output — it never calls into `akapen.dll`. `Akapen.SmokeTest` closes
that gap: it is a real P/Invoke caller, so a wrong DLL name, a missing native
dependency, or a struct layout mismatch fails at run time in CI instead of
only showing up later when a real shell links against this surface.

## Regenerate + build locally

Prerequisites: Rust toolchain (workspace root), .NET 8 SDK (or newer that can
build net8.0 targets — the SDK just needs to include the netcoreapp8.0
targeting pack).

```
# 1. Emit the P/Invoke surface from crates/akapen-ffi/src/lib.rs
cargo run -p akapen-dotnet-bindgen

# 2. Also build the native library (the .dll the P/Invoke resolves to)
cargo build -p akapen-ffi --release

# 3. Compile the class library and the smoke test
dotnet build bindings/dotnet/Akapen.Native/Akapen.Native.csproj -c Release
dotnet build bindings/dotnet/Akapen.SmokeTest/Akapen.SmokeTest.csproj -c Release

# 4. Windows only: copy the DLL next to the smoke test binary and run it
copy target\release\akapen.dll bindings\dotnet\Akapen.SmokeTest\bin\Release\net8.0\
dotnet bindings\dotnet\Akapen.SmokeTest\bin\Release\net8.0\Akapen.SmokeTest.dll
```

Step 4 only works on Windows (the P/Invoke surface targets `akapen.dll`); on
mac/Linux steps 1-3 still prove the C# side compiles, they just cannot load
the native library. CI runs step 4 on `windows-latest`.

`Generated/NativeMethods.g.cs` is not committed — a fresh checkout compiles
because `Akapen.Native.csproj` only includes the file when it exists, and CI
always regenerates before `dotnet build`. Any drift between the Rust FFI and
the C# side surfaces as a build failure on the same push.

## Not yet in scope (spec §9 M5)

- Public managed API surface (this crate exposes P/Invoke methods as
  `internal`).
- NuGet packaging.
- VEDA integration.
