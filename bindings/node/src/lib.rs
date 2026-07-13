//! Node native addon wrapping the Akapen C ABI (spec §9 tri-face build).
//!
//! napi-rs VEDA adapter preview over the shared Akapen C ABI.

#![deny(clippy::all)]

use napi::{Error, Result};
use napi_derive::napi;
use std::ffi::CString;

fn invalid(message: impl Into<String>) -> Error {
    Error::from_reason(message.into())
}

fn handle_or_error(handle: *mut akapen::AkapenEngine, what: &str) -> Result<usize> {
    if handle.is_null() {
        Err(invalid(format!("{what} failed")))
    } else {
        Ok(handle as usize)
    }
}

/// A single-thread-owned drawing session.
///
/// The native handle never crosses the JavaScript boundary. Rust owns it and
/// releases it exactly once from `Drop`, including when a napi method fails.
#[napi]
pub struct AkapenSession {
    handle: usize,
}

impl AkapenSession {
    fn ptr(&self) -> *mut akapen::AkapenEngine {
        self.handle as *mut akapen::AkapenEngine
    }
}

impl Drop for AkapenSession {
    fn drop(&mut self) {
        if self.handle != 0 {
            // SAFETY: handle was returned by akapen_new/open_image and this
            // Drop implementation is the sole owner of it.
            unsafe { akapen::akapen_free(self.ptr()) };
            self.handle = 0;
        }
    }
}

#[napi]
impl AkapenSession {
    #[napi(constructor)]
    pub fn new(width: u32, height: u32) -> Result<Self> {
        let handle = akapen::akapen_new(width, height);
        Ok(Self {
            handle: handle_or_error(handle, "creating blank session")?,
        })
    }

    #[napi(factory)]
    pub fn from_image(path: String) -> Result<Self> {
        let c_path = CString::new(path).map_err(|_| invalid("image path contains NUL"))?;
        // SAFETY: CString is valid for the duration of this call.
        let handle = unsafe { akapen::akapen_open_image(c_path.as_ptr()) };
        Ok(Self {
            handle: handle_or_error(handle, "opening image")?,
        })
    }

    #[napi(getter)]
    pub fn width(&self) -> u32 {
        self.size().0
    }

    #[napi(getter)]
    pub fn height(&self) -> u32 {
        self.size().1
    }

    fn size(&self) -> (u32, u32) {
        let mut width = 0;
        let mut height = 0;
        // SAFETY: pointer is owned and valid for the lifetime of self.
        unsafe { akapen::akapen_size(self.ptr(), &mut width, &mut height) };
        (width, height)
    }

    #[napi(getter, js_name = "canUndo")]
    pub fn can_undo(&self) -> bool {
        // SAFETY: pointer is owned and valid for the lifetime of self.
        unsafe { akapen::akapen_can_undo(self.ptr()) != 0 }
    }

    #[napi(getter, js_name = "canRedo")]
    pub fn can_redo(&self) -> bool {
        // SAFETY: pointer is owned and valid for the lifetime of self.
        unsafe { akapen::akapen_can_redo(self.ptr()) != 0 }
    }

    #[napi]
    pub fn pointer(&mut self, x: f64, y: f64, pressure: f64, kind: i32, phase: i32) -> Result<()> {
        if !x.is_finite()
            || !y.is_finite()
            || !pressure.is_finite()
            || !(0.0..=1.0).contains(&pressure)
        {
            return Err(invalid(
                "pointer coordinates must be finite and pressure must be in 0..=1",
            ));
        }
        if !(0..=2).contains(&kind) || !(0..=2).contains(&phase) {
            return Err(invalid("kind and phase must be 0, 1, or 2"));
        }
        // SAFETY: validated scalar inputs and owned valid handle.
        unsafe { akapen::akapen_pointer(self.ptr(), x, y, pressure, kind, phase) };
        Ok(())
    }

    #[napi]
    pub fn undo(&mut self) -> bool {
        let available = self.can_undo();
        if available {
            // SAFETY: pointer is owned and valid for the lifetime of self.
            unsafe { akapen::akapen_undo(self.ptr()) };
        }
        available
    }

    #[napi]
    pub fn redo(&mut self) -> bool {
        let available = self.can_redo();
        if available {
            // SAFETY: pointer is owned and valid for the lifetime of self.
            unsafe { akapen::akapen_redo(self.ptr()) };
        }
        available
    }

    #[napi(js_name = "setTool")]
    pub fn set_tool(&mut self, tool: i32) -> Result<()> {
        if !(0..=6).contains(&tool) {
            return Err(invalid("tool must be 0..=6"));
        }
        unsafe { akapen::akapen_set_tool(self.ptr(), tool) };
        Ok(())
    }

    #[napi(js_name = "setColor")]
    pub fn set_color(&mut self, rgba: u32) {
        unsafe { akapen::akapen_set_color(self.ptr(), rgba) };
    }

    #[napi(js_name = "setSize")]
    pub fn set_size(&mut self, px: f64) -> Result<()> {
        if !px.is_finite() || px <= 0.0 {
            return Err(invalid("size must be finite and greater than zero"));
        }
        if px > f32::MAX as f64 {
            return Err(invalid("size is too large"));
        }
        unsafe { akapen::akapen_set_size(self.ptr(), px as f32) };
        Ok(())
    }

    #[napi(js_name = "exportToDir")]
    pub fn export_to_dir(&self, dir: String, stem: String) -> Result<()> {
        let c_dir = CString::new(dir).map_err(|_| invalid("directory contains NUL"))?;
        let c_stem = CString::new(stem).map_err(|_| invalid("stem contains NUL"))?;
        if c_dir.as_bytes().is_empty() || c_stem.as_bytes().is_empty() {
            return Err(invalid("directory and stem must not be empty"));
        }
        // SAFETY: both CStrings live through the call; export is implemented
        // by the shared ABI and writes the canonical three-file contract.
        let status =
            unsafe { akapen::akapen_export_to_dir(self.ptr(), c_dir.as_ptr(), c_stem.as_ptr()) };
        if status == 0 {
            Ok(())
        } else {
            Err(invalid(format!("export failed (status {status})")))
        }
    }
}

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

#[cfg(test)]
mod tests {
    use super::*;
    use serde::Deserialize;
    use serde_json::Value;
    use std::fs;

    #[derive(Deserialize)]
    struct GoldenFixture {
        canvas: GoldenCanvas,
        commands: Vec<GoldenCommand>,
        expected: GoldenExpected,
    }
    #[derive(Deserialize)]
    struct GoldenCanvas {
        width: u32,
        height: u32,
    }
    #[derive(Deserialize)]
    struct GoldenCommand {
        x: f64,
        y: f64,
        pressure: f64,
        kind: String,
        phase: String,
    }
    #[derive(Deserialize)]
    struct GoldenExpected {
        schema: String,
        natural_w: u32,
        natural_h: u32,
        stroke_count: usize,
        artifacts: Vec<String>,
    }

    #[test]
    fn session_runs_shared_golden_commands_history_and_export_contract() {
        let fixture: GoldenFixture =
            serde_json::from_str(include_str!("../../../testdata/golden-export-v1.json"))
                .expect("golden fixture must be valid JSON");
        let mut session = AkapenSession::new(fixture.canvas.width, fixture.canvas.height)
            .expect("blank AkapenSession");

        for command in fixture.commands {
            assert_eq!(command.kind, "pen");
            let kind = 0;
            let phase = match command.phase.as_str() {
                "down" => 0,
                "move" => 1,
                "up" => 2,
                other => panic!("unknown golden phase: {other}"),
            };
            session
                .pointer(command.x, command.y, command.pressure, kind, phase)
                .expect("portable pointer command");
        }

        assert!(session.can_undo());
        assert!(!session.can_redo());
        assert!(session.undo());
        assert!(session.can_redo());
        assert!(session.redo());
        assert!(!session.can_redo());

        let dir = std::env::temp_dir().join(format!("akapen-node-golden-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        session
            .export_to_dir(dir.to_str().unwrap().to_string(), "golden".to_string())
            .expect("Rust export through AkapenSession");
        for artifact in &fixture.expected.artifacts {
            assert!(dir.join(artifact).is_file(), "missing {artifact}");
        }
        let json: Value =
            serde_json::from_str(&fs::read_to_string(dir.join("golden.strokes.json")).unwrap())
                .unwrap();
        assert_eq!(json["schema"], fixture.expected.schema);
        assert_eq!(json["natural_w"], fixture.expected.natural_w);
        assert_eq!(json["natural_h"], fixture.expected.natural_h);
        assert_eq!(
            json["strokes"].as_array().unwrap().len(),
            fixture.expected.stroke_count
        );
        for stroke in json["strokes"].as_array().unwrap() {
            assert_eq!(stroke["kind"], "pen");
            assert!(stroke["points"]
                .as_array()
                .unwrap()
                .iter()
                .all(|point| point["p"].is_number()));
        }
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn session_drop_owns_and_releases_blank_handle() {
        let session = AkapenSession::new(8, 6).expect("blank session");
        assert_eq!((session.width(), session.height()), (8, 6));
        drop(session);
    }

    #[test]
    fn invalid_image_path_is_an_error_without_a_handle() {
        assert!(AkapenSession::from_image("/definitely/missing/akapen.png".into()).is_err());
        assert!(AkapenSession::from_image("bad\0path".into()).is_err());
    }
}
