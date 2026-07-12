//! Node native addon wrapping the Akapen C ABI (spec §9 tri-face build).
//!
//! This is a **CI-only probe at M1**: the JavaScript surface here is
//! intentionally minimal (a handful of wrappers around `akapen-ffi`) — just
//! enough to prove napi-rs can bind against the same C ABI the mac and .NET
//! faces consume. The real Node package (`@akapen/core-node`, spec §9 M5) will
//! land when VEDA re-imports the core; do not treat this file as a product
//! API.

#![deny(clippy::all)]

use napi_derive::napi;
use std::ffi::CString;

/// Returns a stable version tag for the Akapen C ABI, useful as a smoke test
/// from JavaScript (`require('./index.node').version()`). Not exported by the
/// C ABI itself — the tag lives in the workspace `Cargo.toml` and is baked in
/// at compile time.
#[napi]
pub fn version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}

/// Creates and immediately frees an engine over a blank canvas, returning the
/// canvas size as `[width, height]`. Exercised only to prove the akapen-ffi
/// static/rlib symbols are actually linked in the produced `.node` binary
/// (missing symbols would fail at load, not build; this catches the latter).
#[napi]
pub fn ping_new_engine(width: u32, height: u32) -> Vec<u32> {
    // SAFETY: akapen_new returns an owned handle or null; we call akapen_size
    // only if it is non-null and then release with akapen_free exactly once.
    unsafe {
        let engine = akapen::akapen_new(width, height);
        if engine.is_null() {
            return vec![0, 0];
        }
        let mut w: u32 = 0;
        let mut h: u32 = 0;
        akapen::akapen_size(engine, &mut w as *mut u32, &mut h as *mut u32);
        akapen::akapen_free(engine);
        vec![w, h]
    }
}

/// Convenience wrapper: routes a single classified pointer event through the
/// palm-rejection state machine and returns the route code as a string
/// (`"draw" | "navigate" | "ignore"`).
///
/// Kept minimal so the CI probe stays a probe: a real Node binding would carry
/// a proper `PalmState` object across calls; here we run it stateless per call
/// (equivalent to zero-initializing `AkapenPalmState`).
#[napi]
pub fn palm_route(kind: i32, phase: i32, now_ms: i64) -> String {
    let mut state = akapen::AkapenPalmState {
        pen_down: 0,
        lock_active: 0,
        lock_until_ms: 0,
    };
    // SAFETY: akapen_palm_route only reads/writes through `state` (a stack
    // local we own), never dereferences a null; kind/phase/now_ms are plain
    // integers.
    let code = unsafe { akapen::akapen_palm_route(&mut state as *mut _, kind, phase, now_ms) };
    match code {
        0 => "draw".to_string(),
        1 => "navigate".to_string(),
        _ => "ignore".to_string(),
    }
}

/// Passes a path through the C ABI's `*const c_char` string boundary by
/// actually calling `akapen_open_image` with it, then reports whether the
/// call returned null. Meant to be called with a path that does not exist
/// (see the CI smoke step), so the expected — and asserted — outcome is
/// `true`: `akapen_open_image` returns null for a missing file, but only
/// after the string has crossed the FFI marshalling layer end to end. That
/// crossing (not the null result itself) is what this proves; a prior
/// version of this probe round-tripped a `CString` locally in Rust without
/// ever calling into `akapen-ffi`, which touched no C ABI at all.
#[napi]
pub fn open_bogus_returns_null(bogus_path: String) -> bool {
    let Ok(c_path) = CString::new(bogus_path) else {
        return false;
    };
    // SAFETY: `c_path` is a valid NUL-terminated UTF-8 C string kept alive for
    // the duration of the call. `akapen_open_image` returns either null or an
    // owned handle; on the non-null branch we release it immediately with
    // `akapen_free` (exactly once) so the probe leaks nothing.
    unsafe {
        let handle = akapen::akapen_open_image(c_path.as_ptr());
        if handle.is_null() {
            true
        } else {
            akapen::akapen_free(handle);
            false
        }
    }
}
