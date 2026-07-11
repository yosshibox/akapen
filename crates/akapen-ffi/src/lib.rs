//! Akapen C ABI (spec §7.2).
//!
//! This is the minimal-common-denominator surface the mac SwiftUI shell (and
//! later the .NET / Node bindings) wrap. It exposes an opaque `Engine` handle
//! and thin `extern "C"` functions over it. The hand-written header
//! `include/akapen.h` mirrors this surface (spec §7.4 permits a hand-written
//! extern "C" ABI; keeping the header checked in keeps the boundary reviewable
//! and avoids a build-time codegen dependency at M1).
//!
//! Ownership rules for callers:
//! - `akapen_open_image` / `akapen_new` return a handle you must release with
//!   `akapen_free` exactly once.
//! - Strings are borrowed for the duration of the call (UTF-8, NUL-terminated).
//! - `akapen_composite_rgba` writes into a caller-owned buffer.
//!
//! The per-function safety contract (non-null, valid handle from
//! `akapen_open_image`/`akapen_new`, UTF-8 NUL-terminated strings, writable
//! output buffer) is uniform across the surface and documented here and in
//! `include/akapen.h`, so the per-fn `# Safety` lint is allowed crate-wide.
#![allow(clippy::missing_safety_doc)]

use akapen_core::engine::{Phase, PointerSample};
use akapen_core::stroke::PointerKind;
use akapen_core::{Engine, PressureCurve, Tool};
use akapen_io::decode::decode_rgba;
use akapen_io::output::{resolve_target, OutputNaming};
use std::ffi::CStr;
use std::os::raw::{c_char, c_int};
use std::path::Path;

/// Opaque engine handle.
pub struct AkapenEngine {
    inner: Engine,
}

#[inline]
unsafe fn as_engine<'a>(ptr: *mut AkapenEngine) -> Option<&'a mut AkapenEngine> {
    if ptr.is_null() {
        None
    } else {
        Some(&mut *ptr)
    }
}

unsafe fn cstr<'a>(ptr: *const c_char) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    CStr::from_ptr(ptr).to_str().ok()
}

/// Opens an image file and creates an engine over it. Returns null on failure.
///
/// # Safety
/// `path` must be a valid NUL-terminated UTF-8 C string.
#[no_mangle]
pub unsafe extern "C" fn akapen_open_image(path: *const c_char) -> *mut AkapenEngine {
    let Some(path) = cstr(path) else {
        return std::ptr::null_mut();
    };
    match decode_rgba(path) {
        Ok(img) => {
            let engine = Engine::from_rgba(img.rgba, img.width, img.height);
            Box::into_raw(Box::new(AkapenEngine { inner: engine }))
        }
        Err(_) => std::ptr::null_mut(),
    }
}

/// Creates an engine over a blank white background of the given size.
#[no_mangle]
pub extern "C" fn akapen_new(width: u32, height: u32) -> *mut AkapenEngine {
    if width == 0 || height == 0 {
        return std::ptr::null_mut();
    }
    Box::into_raw(Box::new(AkapenEngine {
        inner: Engine::new(width, height),
    }))
}

/// Releases an engine handle.
///
/// # Safety
/// `engine` must have come from `akapen_open_image` / `akapen_new` and not be
/// freed already.
#[no_mangle]
pub unsafe extern "C" fn akapen_free(engine: *mut AkapenEngine) {
    if !engine.is_null() {
        drop(Box::from_raw(engine));
    }
}

/// Writes the natural (image) size into `w`/`h`.
///
/// # Safety
/// Pointers must be valid and non-null.
#[no_mangle]
pub unsafe extern "C" fn akapen_size(engine: *mut AkapenEngine, w: *mut u32, h: *mut u32) {
    if let Some(e) = as_engine(engine) {
        let (nw, nh) = e.inner.natural_size();
        if !w.is_null() {
            *w = nw;
        }
        if !h.is_null() {
            *h = nh;
        }
    }
}

/// Sets the tool: 0=Pen, 1=Eraser (M1 tools; other values map to Pen).
#[no_mangle]
pub unsafe extern "C" fn akapen_set_tool(engine: *mut AkapenEngine, tool: c_int) {
    if let Some(e) = as_engine(engine) {
        let t = match tool {
            1 => Tool::Eraser,
            2 => Tool::Line,
            3 => Tool::Arrow,
            4 => Tool::Rect,
            5 => Tool::Ellipse,
            6 => Tool::Text,
            _ => Tool::Pen,
        };
        e.inner.set_tool(t);
    }
}

/// Sets the packed `0xRRGGBBAA` color.
#[no_mangle]
pub unsafe extern "C" fn akapen_set_color(engine: *mut AkapenEngine, rgba: u32) {
    if let Some(e) = as_engine(engine) {
        e.inner.set_color(rgba);
    }
}

/// Sets the nominal brush size in px.
#[no_mangle]
pub unsafe extern "C" fn akapen_set_size(engine: *mut AkapenEngine, px: f32) {
    if let Some(e) = as_engine(engine) {
        e.inner.set_size(px);
    }
}

/// Sets the pressure curve: 0=Normal, 1=Soft, 2=Hard.
#[no_mangle]
pub unsafe extern "C" fn akapen_set_pressure_curve(engine: *mut AkapenEngine, curve: c_int) {
    if let Some(e) = as_engine(engine) {
        let c = match curve {
            1 => PressureCurve::Soft,
            2 => PressureCurve::Hard,
            _ => PressureCurve::Normal,
        };
        e.inner.set_pressure_curve(c);
    }
}

/// Feeds one normalized pointer sample.
///
/// `kind`: 0=Pen, 1=Touch, 2=Mouse. `phase`: 0=Down, 1=Move, 2=Up.
/// `pressure` is `0..=1` (mouse passes 1.0).
#[no_mangle]
pub unsafe extern "C" fn akapen_pointer(
    engine: *mut AkapenEngine,
    x: f64,
    y: f64,
    pressure: f64,
    kind: c_int,
    phase: c_int,
) {
    if let Some(e) = as_engine(engine) {
        let kind = match kind {
            1 => PointerKind::Touch,
            2 => PointerKind::Mouse,
            _ => PointerKind::Pen,
        };
        let phase = match phase {
            0 => Phase::Down,
            2 => Phase::Up,
            _ => Phase::Move,
        };
        e.inner.push_pointer(PointerSample {
            x,
            y,
            pressure,
            kind,
            phase,
        });
    }
}

/// Undo / redo one stroke.
#[no_mangle]
pub unsafe extern "C" fn akapen_undo(engine: *mut AkapenEngine) {
    if let Some(e) = as_engine(engine) {
        e.inner.undo();
    }
}

#[no_mangle]
pub unsafe extern "C" fn akapen_redo(engine: *mut AkapenEngine) {
    if let Some(e) = as_engine(engine) {
        e.inner.redo();
    }
}

/// Returns 1 if the last committed pen stroke had no pressure variation (the
/// spec §5.4 "pressure not detected" guard); 0 otherwise.
#[no_mangle]
pub unsafe extern "C" fn akapen_pressure_stuck(engine: *mut AkapenEngine) -> c_int {
    match as_engine(engine) {
        Some(e) if e.inner.pressure_stuck_warning => 1,
        _ => 0,
    }
}

/// Composites the display image (background + strokes + in-progress stroke)
/// into `out` (straight RGBA8, `width*height*4` bytes). Returns the number of
/// bytes needed; if `out_len` is smaller, nothing is written.
///
/// # Safety
/// `out` must point to at least `out_len` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn akapen_composite_rgba(
    engine: *mut AkapenEngine,
    out: *mut u8,
    out_len: usize,
) -> usize {
    let Some(e) = as_engine(engine) else {
        return 0;
    };
    let buf = e.inner.composite_for_display();
    let needed = buf.data.len();
    if out.is_null() || out_len < needed {
        return needed;
    }
    std::ptr::copy_nonoverlapping(buf.data.as_ptr(), out, needed);
    needed
}

/// Writes the 3-file export (transparent strokes PNG / flat PNG / vector JSON)
/// into `dir`, using `stem` as the base filename with collision-free naming.
/// Returns 0 on success, non-zero on failure.
///
/// # Safety
/// `dir` and `stem` must be valid NUL-terminated UTF-8 C strings.
#[no_mangle]
pub unsafe extern "C" fn akapen_export_to_dir(
    engine: *mut AkapenEngine,
    dir: *const c_char,
    stem: *const c_char,
) -> c_int {
    let (Some(e), Some(dir), Some(stem)) = (as_engine(engine), cstr(dir), cstr(stem)) else {
        return 1;
    };
    if std::fs::create_dir_all(dir).is_err() {
        return 2;
    }
    let naming = OutputNaming::default();
    let pseudo_input = Path::new(dir).join(format!("{stem}.png"));
    let target = resolve_target(&pseudo_input, dir, &naming, |p| p.exists());
    let out = e.inner.export();
    let vector_json = match out.vector.to_json() {
        Ok(j) => j,
        Err(_) => return 3,
    };
    if std::fs::write(&target.strokes_png, &out.strokes_png).is_err()
        || std::fs::write(&target.flat_png, &out.flat_png).is_err()
        || std::fs::write(&target.vector_json, vector_json).is_err()
    {
        return 4;
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CString;

    #[test]
    fn ffi_draw_and_export_roundtrip() {
        let e = akapen_new(64, 64);
        assert!(!e.is_null());
        unsafe {
            akapen_set_size(e, 20.0);
            akapen_set_color(e, 0xFF0000FF);
            // A pressure-varying diagonal stroke.
            akapen_pointer(e, 8.0, 8.0, 0.2, 0, 0);
            akapen_pointer(e, 32.0, 32.0, 0.6, 0, 1);
            akapen_pointer(e, 56.0, 56.0, 1.0, 0, 2);

            let mut w = 0;
            let mut h = 0;
            akapen_size(e, &mut w, &mut h);
            assert_eq!((w, h), (64, 64));

            let need = akapen_composite_rgba(e, std::ptr::null_mut(), 0);
            assert_eq!(need, 64 * 64 * 4);
            let mut buf = vec![0u8; need];
            let got = akapen_composite_rgba(e, buf.as_mut_ptr(), buf.len());
            assert_eq!(got, need);

            let dir = std::env::temp_dir().join(format!("akapen-ffi-{}", std::process::id()));
            let cdir = CString::new(dir.to_str().unwrap()).unwrap();
            let cstem = CString::new("c001").unwrap();
            let rc = akapen_export_to_dir(e, cdir.as_ptr(), cstem.as_ptr());
            assert_eq!(rc, 0);
            assert!(dir.join("c001.review.png").exists());
            assert!(dir.join("c001.strokes.png").exists());
            assert!(dir.join("c001.strokes.json").exists());
            let _ = std::fs::remove_dir_all(&dir);

            akapen_free(e);
        }
    }

    #[test]
    fn null_handle_is_safe() {
        unsafe {
            akapen_free(std::ptr::null_mut());
            akapen_undo(std::ptr::null_mut());
            assert_eq!(akapen_pressure_stuck(std::ptr::null_mut()), 0);
        }
    }
}
