//! Emits C# P/Invoke bindings from `crates/akapen-ffi/src/lib.rs` into
//! `bindings/dotnet/Akapen.Native/Generated/NativeMethods.g.cs`.
//!
//! This is the .NET half of the tri-face build discipline (spec §9). It runs in
//! CI (Windows) before `dotnet build` so a change to the C ABI (adding a
//! function, changing a `#[repr(C)]` layout) breaks the Windows job on the same
//! push that lands in `akapen-ffi` — the whole point of keeping .NET in the
//! build during M1 even though no .NET shell ships yet.
//!
//! Intentionally *not* a build script on `akapen-ffi`: keeping csbindgen out of
//! the core's dependency graph means the mac face (which does not want a .NET
//! codegen dep) still builds with just `cargo build -p akapen-ffi`.
//!
//! DLL name: the emitted `[DllImport]` uses `akapen`, matching
//! `crates/akapen-ffi/Cargo.toml` `[lib] name = "akapen"` — the cdylib is
//! `akapen.dll` on Windows.

use std::path::PathBuf;

fn main() {
    // Resolve the workspace root from CARGO_MANIFEST_DIR so `cargo run -p
    // akapen-dotnet-bindgen` works regardless of the caller's cwd (CI runs it
    // from the repo root; a developer might run it from anywhere).
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let workspace_root = manifest_dir
        .parent()
        .and_then(|p| p.parent())
        .expect("crates/akapen-dotnet-bindgen must live two levels under the workspace root");

    let ffi_source = workspace_root.join("crates/akapen-ffi/src/lib.rs");
    let out_file =
        workspace_root.join("bindings/dotnet/Akapen.Native/Generated/NativeMethods.g.cs");

    if let Some(parent) = out_file.parent() {
        std::fs::create_dir_all(parent)
            .unwrap_or_else(|e| panic!("create {}: {e}", parent.display()));
    }

    csbindgen::Builder::default()
        .input_extern_file(&ffi_source)
        // The runtime DLL: crates/akapen-ffi produces `akapen.dll` (cdylib) on
        // Windows. Consumers set DllImportResolver / native lib path to point
        // at wherever cargo dropped it.
        .csharp_dll_name("akapen")
        .csharp_namespace("Akapen.Native")
        .csharp_class_name("NativeMethods")
        // `Akapen.Native` is a .NET Standard-compatible class library; leave the
        // accessibility at `internal` so applications wrap the P/Invoke surface
        // in an idiomatic managed API rather than calling it directly (spec
        // §7.2 keeps the C ABI reviewable but not the product API).
        .csharp_class_accessibility("internal")
        .generate_csharp_file(&out_file)
        .unwrap_or_else(|e| panic!("csbindgen failed for {}: {e}", ffi_source.display()));

    println!("wrote {}", out_file.display());
}
