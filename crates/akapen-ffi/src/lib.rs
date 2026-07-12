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

use akapen_core::coord::ViewTransform;
use akapen_core::engine::{BakeDelta, Phase, PointerSample};
use akapen_core::palm::{PalmState, Routing};
use akapen_core::stroke::PointerKind;
use akapen_core::{Engine, PressureCurve, Tool};
use akapen_io::decode::decode_rgba;
use akapen_io::output::{resolve_target, OutputNaming};
use akapen_render::{GpuCanvas, SurfaceDesc, SurfaceKind};
use std::ffi::{c_void, CStr};
use std::os::raw::{c_char, c_int};
use std::path::Path;

/// Opaque engine handle.
///
/// `inner` is the UI-agnostic drawing engine (unchanged since M1). Phase e
/// adds two GPU-path fields that are inert until [`akapen_render_attach`] is
/// called:
/// - `gpu`: the attached [`GpuCanvas`], or `None` on the CPU-only path.
/// - `pending`: committed-stroke changes ([`BakeDelta`]) reported by
///   `push_pointer`/`undo`/`redo` but not yet baked into the GPU's offscreen
///   texture; consumed (and reset) by each [`akapen_render_frame`]. Inert on
///   the CPU path — the CPU composite (`akapen_composite_rgba`) never reads it.
pub struct AkapenEngine {
    inner: Engine,
    gpu: Option<GpuCanvas>,
    pending: BakeDelta,
    /// The `Display` text of the most recent `akapen_render_attach` failure,
    /// if any (cleared on a successful attach). `akapen_render_attach` only
    /// returns an opaque code (1-4) across the C ABI — code 4 alone collapses
    /// four distinct [`akapen_render::RendererError`] variants (no adapter /
    /// device request failed / surface creation failed / unsupported kind).
    /// This field lets a caller doing platform bring-up (e.g. the Windows
    /// HWND de-risk probe) retrieve the real reason via
    /// [`akapen_render_last_attach_error`] instead of guessing from the code
    /// alone.
    last_attach_error: Option<String>,
}

/// Folds a newly-reported [`BakeDelta`] into the accumulated one held between
/// GPU frames. `None` is the identity (an ignored sample changes nothing); the
/// first real change is kept verbatim so a single commit between frames stays
/// a cheap `Append`; any *second* real change before the next frame escalates
/// to `Rebuild`, which safely redraws the whole committed history rather than
/// risk dropping a stroke a lone `Append` (which only bakes the last committed
/// stroke) would miss. `Rebuild` therefore always wins — it is a correct
/// superset of any pending change.
fn accumulate_bake_delta(acc: BakeDelta, new: BakeDelta) -> BakeDelta {
    match (acc, new) {
        (acc, BakeDelta::None) => acc,
        (BakeDelta::None, new) => new,
        // Two real changes coalesced before a frame: redraw everything.
        _ => BakeDelta::Rebuild,
    }
}

/// Maps the C ABI `kind` code to the render crate's [`SurfaceKind`]. Mirrors
/// the numbering documented in `include/akapen.h`
/// (`MetalLayer=0, Hwnd=1, SwapChainPanel=2`).
fn surface_kind_from_code(kind: i32) -> Option<SurfaceKind> {
    match kind {
        0 => Some(SurfaceKind::MetalLayer),
        1 => Some(SurfaceKind::Hwnd),
        2 => Some(SurfaceKind::SwapChainPanel),
        _ => None,
    }
}

/// A native drawing surface, described in the OS-neutral shape the render
/// crate's [`SurfaceDesc`] expects (spec §7.4-6). `kind` selects how `handle`
/// is interpreted: `0 = MetalLayer` (mac: `handle` is the `NSView*`, not the
/// layer), `1 = Hwnd` (Windows), `2 = SwapChainPanel` (WinUI 3; not yet
/// wired). `scale_factor` is the backing-store scale (e.g. 2.0 on Retina).
#[repr(C)]
pub struct AkapenSurfaceDesc {
    pub kind: i32,
    pub handle: *mut c_void,
    pub display: *mut c_void,
    pub width: u32,
    pub height: u32,
    pub scale_factor: f32,
}

/// The on-screen view transform (zoom/pan/rotation) for a rendered frame, in
/// **surface physical pixels**. A single uniform `scale` (the shell's zoom ×
/// backing scale) is expanded to the core's per-axis `scale_x`/`scale_y`;
/// `center_x`/`center_y` are the displayed image center in physical pixels;
/// `rotation_deg` is clockwise degrees. The image's own size (`buffer_w/h`)
/// is filled in from the attached canvas, not carried here.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct AkapenViewTransform {
    pub center_x: f32,
    pub center_y: f32,
    pub scale: f32,
    pub rotation_deg: f32,
}

/// Expands an [`AkapenViewTransform`] (single `scale`, physical pixels) plus
/// the attached image's natural size into the core's [`ViewTransform`]
/// (per-axis scale, explicit `buffer_w/h`). Pure — unit-tested without a GPU.
fn to_view_transform(vt: AkapenViewTransform, buffer_w: u32, buffer_h: u32) -> ViewTransform {
    ViewTransform {
        center_x: vt.center_x as f64,
        center_y: vt.center_y as f64,
        scale_x: vt.scale as f64,
        scale_y: vt.scale as f64,
        rotation_deg: vt.rotation_deg as f64,
        buffer_w: buffer_w as f64,
        buffer_h: buffer_h as f64,
    }
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
            Box::into_raw(Box::new(AkapenEngine {
                inner: engine,
                gpu: None,
                pending: BakeDelta::None,
                last_attach_error: None,
            }))
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
        gpu: None,
        pending: BakeDelta::None,
        last_attach_error: None,
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
        let delta = e.inner.push_pointer(PointerSample {
            x,
            y,
            pressure,
            kind,
            phase,
        });
        // Record the committed-stroke change for the next GPU frame to bake.
        // Purely additive: the CPU composite path ignores `pending`, so this
        // does not change any observable behavior of `akapen_pointer` itself.
        e.pending = accumulate_bake_delta(e.pending, delta);
    }
}

/// Undo / redo one stroke.
#[no_mangle]
pub unsafe extern "C" fn akapen_undo(engine: *mut AkapenEngine) {
    if let Some(e) = as_engine(engine) {
        let delta = e.inner.undo();
        e.pending = accumulate_bake_delta(e.pending, delta);
    }
}

#[no_mangle]
pub unsafe extern "C" fn akapen_redo(engine: *mut AkapenEngine) {
    if let Some(e) = as_engine(engine) {
        let delta = e.inner.redo();
        e.pending = accumulate_bake_delta(e.pending, delta);
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

/// Minimal stderr [`log::Log`] implementation: no external logging crate
/// (`env_logger` etc. are not approved dependencies), just the already-used
/// `log` facade printing straight to stderr. Off by default — `log`'s
/// records are silently dropped everywhere in this codebase until a caller
/// opts in via [`akapen_enable_diagnostic_logging`]. This matters for GPU
/// surface diagnosis in particular: wgpu-core logs the *specific* reason
/// behind some validation failures (e.g. the underlying HRESULT/message
/// behind a DX12 swapchain creation failure) via `log::error!` before
/// collapsing them to a generic error variant the `Result`/`RendererError`
/// path never sees otherwise (discovered during the Windows HWND
/// presentation de-risk).
struct StderrLogger;

impl log::Log for StderrLogger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::Level::Warn
    }
    fn log(&self, record: &log::Record) {
        if self.enabled(record.metadata()) {
            eprintln!(
                "[akapen:{}] {}: {}",
                record.level(),
                record.target(),
                record.args()
            );
        }
    }
    fn flush(&self) {}
}

static STDERR_LOGGER: StderrLogger = StderrLogger;

/// Enables the minimal stderr diagnostic logger (warn level and above) for
/// this process. Call once, early — before [`akapen_render_attach`] if
/// diagnosing a surface bring-up failure. Idempotent: safe to call more than
/// once (a logger already being registered, from an earlier call or from
/// the embedding host, is not treated as an error).
#[no_mangle]
pub extern "C" fn akapen_enable_diagnostic_logging() {
    if log::set_logger(&STDERR_LOGGER).is_ok() {
        log::set_max_level(log::LevelFilter::Warn);
    }
}

// ── Phase e: GPU surface path (spec §7.4-6) ──────────────────────────────
//
// These are additive and independent of the CPU path: `akapen_composite_rgba`
// / `akapen_export_to_dir` keep working identically whether or not a GPU
// surface is attached. All wgpu / CAMetalLayer work must be driven from a
// single thread (the shell's main thread) — an `AkapenEngine` handle is not
// thread-safe (see `include/akapen.h`).

/// Attaches a GPU render surface to the engine, seeding it with the current
/// background and committed strokes. Returns 0 on success, non-zero on
/// failure (the caller must then fall back to the CPU composite path):
/// - `1`: null engine handle.
/// - `2`: null `desc` pointer.
/// - `3`: unknown `desc->kind` value.
/// - `4`: surface / adapter / device bring-up failed (e.g. no GPU, or an
///   unsupported surface kind such as `SwapChainPanel`).
///
/// # Safety
/// `desc` must be a valid pointer to an `AkapenSurfaceDesc` whose `handle`
/// (and `display`, if non-null) are valid native handles for `kind`, live for
/// as long as the surface stays attached, and are used only from this thread.
#[no_mangle]
pub unsafe extern "C" fn akapen_render_attach(
    engine: *mut AkapenEngine,
    desc: *const AkapenSurfaceDesc,
) -> c_int {
    let Some(e) = as_engine(engine) else {
        return 1;
    };
    if desc.is_null() {
        return 2;
    }
    let desc = &*desc;
    let Some(kind) = surface_kind_from_code(desc.kind) else {
        return 3;
    };

    let render_desc = SurfaceDesc {
        kind,
        handle: desc.handle,
        display: desc.display,
        width: desc.width,
        height: desc.height,
        // The render crate's SurfaceDesc carries scale_factor as f64.
        scale_factor: desc.scale_factor as f64,
    };

    let bg = e.inner.background();
    let (bw, bh) = (bg.width, bg.height);
    // SAFETY: the caller's handle contract is forwarded to GpuCanvas::attach
    // (which forwards it to surface::create).
    let result = GpuCanvas::attach(render_desc, &bg.data, bw, bh, e.inner.committed_strokes());
    match result {
        Ok(canvas) => {
            e.gpu = Some(canvas);
            // The freshly-attached canvas already baked the whole committed
            // history, so there is nothing pending to replay on the first frame.
            e.pending = BakeDelta::None;
            e.last_attach_error = None;
            0
        }
        Err(err) => {
            e.last_attach_error = Some(err.to_string());
            4
        }
    }
}

/// Writes a short NUL-terminated ASCII message describing why the most
/// recent [`akapen_render_attach`] call failed (e.g.
/// `"failed to create surface: ..."`), so a caller bringing up a new
/// platform surface (a bare code-4 return is otherwise opaque across four
/// distinct underlying failures — no adapter / device request failed /
/// surface creation failed / unsupported kind) can log the real reason.
/// Same size-probe convention as [`akapen_composite_rgba`]: returns bytes
/// needed (incl. NUL); call once with `out=NULL`/`out_len=0` to size the
/// buffer. Returns 0 if the last attach succeeded or none was attempted.
///
/// # Safety
/// `out` must point to at least `out_len` writable bytes, or be null.
#[no_mangle]
pub unsafe extern "C" fn akapen_render_last_attach_error(
    engine: *mut AkapenEngine,
    out: *mut c_char,
    out_len: usize,
) -> usize {
    let Some(e) = as_engine(engine) else {
        return 0;
    };
    let Some(msg) = e.last_attach_error.as_ref() else {
        return 0;
    };
    let needed = msg.len() + 1; // + NUL
    if out.is_null() || out_len < needed {
        return needed;
    }
    std::ptr::copy_nonoverlapping(msg.as_ptr(), out as *mut u8, msg.len());
    *out.add(msg.len()) = 0;
    needed
}

/// Re-configures the attached surface for a new pixel size (physical pixels)
/// and backing scale. No-op if no surface is attached.
///
/// # Safety
/// `engine` must be a valid handle (or null, which is ignored).
#[no_mangle]
pub unsafe extern "C" fn akapen_render_resize(
    engine: *mut AkapenEngine,
    width: u32,
    height: u32,
    scale: f32,
) {
    // `scale` is accepted for symmetry with attach and future use; the
    // swapchain is configured purely from the physical pixel size here, and
    // the per-frame view transform already carries the effective scale.
    let _ = scale;
    if let Some(e) = as_engine(engine) {
        if let Some(gpu) = e.gpu.as_mut() {
            gpu.resize(width, height);
        }
    }
}

/// Draws one on-screen frame through the given view transform. Bakes any
/// committed-stroke changes accumulated since the last frame, composites
/// (background → baked strokes → in-progress "wet" stroke), and presents.
/// No-op if no surface is attached.
///
/// # Safety
/// `engine` must be a valid handle (or null, which is ignored). Must be
/// called on the same thread that attached the surface.
#[no_mangle]
pub unsafe extern "C" fn akapen_render_frame(engine: *mut AkapenEngine, view: AkapenViewTransform) {
    let Some(e) = as_engine(engine) else {
        return;
    };
    if e.gpu.is_none() {
        return;
    }
    // Consume the accumulated bake delta (reset to None) before drawing, so a
    // dropped/failed frame doesn't replay it forever.
    let delta = std::mem::replace(&mut e.pending, BakeDelta::None);
    let gpu = e.gpu.as_mut().expect("gpu present (checked above)");
    let (bw, bh) = gpu.buffer_size();
    let vt = to_view_transform(view, bw, bh);
    // `e.gpu` and `e.inner` are disjoint fields, so these borrows don't alias.
    gpu.apply_bake(delta, e.inner.committed_strokes());
    gpu.render(e.inner.current_stroke(), &vt);
}

/// Detaches and tears down the GPU surface (releasing the swapchain and its
/// CAMetalLayer retain). Safe to call when nothing is attached. The engine
/// keeps working on the CPU path afterward.
///
/// # Safety
/// `engine` must be a valid handle (or null, which is ignored).
#[no_mangle]
pub unsafe extern "C" fn akapen_render_detach(engine: *mut AkapenEngine) {
    if let Some(e) = as_engine(engine) {
        e.gpu = None;
    }
}

/// Returns 1 if a GPU surface is currently attached (the GPU path is active),
/// 0 otherwise (CPU-only, including a null handle).
///
/// # Safety
/// `engine` must be a valid handle (or null, which returns 0).
#[no_mangle]
pub unsafe extern "C" fn akapen_render_available(engine: *mut AkapenEngine) -> c_int {
    match as_engine(engine) {
        Some(e) if e.gpu.is_some() => 1,
        _ => 0,
    }
}

/// Writes a short NUL-terminated ASCII diagnostic line identifying the
/// attached surface's actual backend / present mode / max frame latency
/// (e.g. `"backend=Dx12 present_mode=Fifo max_frame_latency=1"`) into `out`.
/// Same size-probe convention as [`akapen_composite_rgba`]: returns the
/// number of bytes needed (including the NUL terminator); call once with
/// `out=NULL`/`out_len=0` to size the buffer. Returns 0 (and writes nothing)
/// if no surface is attached.
///
/// # Safety
/// `out` must point to at least `out_len` writable bytes, or be null.
#[no_mangle]
pub unsafe extern "C" fn akapen_render_backend_info(
    engine: *mut AkapenEngine,
    out: *mut c_char,
    out_len: usize,
) -> usize {
    let Some(e) = as_engine(engine) else {
        return 0;
    };
    let Some(gpu) = e.gpu.as_ref() else {
        return 0;
    };
    let info = gpu.backend_info();
    let needed = info.len() + 1; // + NUL
    if out.is_null() || out_len < needed {
        return needed;
    }
    std::ptr::copy_nonoverlapping(info.as_ptr(), out as *mut u8, info.len());
    *out.add(info.len()) = 0;
    needed
}

// ── Key mapping (spec §3) ────────────────────────────────────────────────
//
// A pure, engine-independent bridge to `akapen_core::keymap`. The shell hands
// over a described key event (produced character, physical key code, modifier
// flags, and the IME / text-editing guards) and gets back a stable integer
// action code. Keeping the mapping in the core means mac / Windows / Node share
// exactly one shortcut table (spec §3).

/// Stable C ABI action codes returned by [`akapen_resolve_key`]. `0` = no action
/// (leave the key alone). Mirrored in `include/akapen.h`.
mod action_code {
    pub const NONE: i32 = 0;
    // Tools 1..=7 match the `akapen_set_tool` numbering.
    pub const TOOL_PEN: i32 = 1;
    pub const TOOL_ERASER: i32 = 2;
    pub const TOOL_LINE: i32 = 3;
    pub const TOOL_ARROW: i32 = 4;
    pub const TOOL_RECT: i32 = 5;
    pub const TOOL_ELLIPSE: i32 = 6;
    pub const TOOL_TEXT: i32 = 7;
    pub const UNDO: i32 = 10;
    pub const REDO: i32 = 11;
    pub const ZOOM_IN: i32 = 12;
    pub const ZOOM_OUT: i32 = 13;
    pub const FIT: i32 = 14;
    pub const ACTUAL_SIZE: i32 = 15;
    pub const ROTATE_LEFT: i32 = 16;
    pub const ROTATE_RIGHT: i32 = 17;
    pub const BRUSH_SMALLER: i32 = 18;
    pub const BRUSH_LARGER: i32 = 19;
    pub const NEXT_FRAME: i32 = 20;
    pub const PREV_FRAME: i32 = 21;
    pub const SWAP_COLOR: i32 = 22;
    pub const EYEDROPPER: i32 = 23;
    pub const TRANSPARENT_COLOR: i32 = 24;
}

/// Stable C ABI physical-key codes accepted by [`akapen_resolve_key`]. `0` =
/// unknown/other. Mirrored in `include/akapen.h`; the shell maps its native
/// keyCode/scancode to these.
fn physical_key_from_code(code: i32) -> akapen_core::PhysicalKey {
    use akapen_core::PhysicalKey as K;
    match code {
        1 => K::KeyP,
        2 => K::KeyE,
        3 => K::KeyU,
        4 => K::KeyA,
        5 => K::KeyR,
        6 => K::KeyO,
        7 => K::KeyT,
        8 => K::KeyI,
        9 => K::KeyX,
        10 => K::KeyC,
        11 => K::KeyZ,
        12 => K::KeyY,
        13 => K::Digit0,
        14 => K::Space,
        15 => K::BracketLeft,
        16 => K::BracketRight,
        17 => K::Minus,
        18 => K::Caret,
        19 => K::PageUp,
        20 => K::PageDown,
        _ => K::Other,
    }
}

fn action_to_code(action: akapen_core::Action) -> i32 {
    use action_code as C;
    use akapen_core::Action as A;
    use akapen_core::Tool;
    match action {
        A::SelectTool(Tool::Pen) => C::TOOL_PEN,
        A::SelectTool(Tool::Eraser) => C::TOOL_ERASER,
        A::SelectTool(Tool::Line) => C::TOOL_LINE,
        A::SelectTool(Tool::Arrow) => C::TOOL_ARROW,
        A::SelectTool(Tool::Rect) => C::TOOL_RECT,
        A::SelectTool(Tool::Ellipse) => C::TOOL_ELLIPSE,
        A::SelectTool(Tool::Text) => C::TOOL_TEXT,
        A::Undo => C::UNDO,
        A::Redo => C::REDO,
        A::ZoomIn => C::ZOOM_IN,
        A::ZoomOut => C::ZOOM_OUT,
        A::FitToWindow => C::FIT,
        A::ActualSize => C::ACTUAL_SIZE,
        A::RotateLeft => C::ROTATE_LEFT,
        A::RotateRight => C::ROTATE_RIGHT,
        A::BrushSmaller => C::BRUSH_SMALLER,
        A::BrushLarger => C::BRUSH_LARGER,
        A::NextFrame => C::NEXT_FRAME,
        A::PrevFrame => C::PREV_FRAME,
        A::SwapColor => C::SWAP_COLOR,
        A::Eyedropper => C::EYEDROPPER,
        A::TransparentColor => C::TRANSPARENT_COLOR,
    }
}

/// Maps a described key-down event to an editor action code (spec §3). Pure and
/// engine-independent — no handle is needed.
///
/// - `ch`: the Unicode scalar the key produced *ignoring* the primary/alt
///   modifiers (mac `charactersIgnoringModifiers`), or `0` for none.
/// - `physical`: the physical-key code (see `physical_key_from_code` /
///   `akapen.h`), `0` for unknown.
/// - `primary`/`shift`/`alt`: modifier flags (non-zero = held). `primary` is
///   Cmd on macOS, Ctrl on Windows/Linux.
/// - `composing`: non-zero while an IME composition is active (B21 guard).
/// - `text_editing`: non-zero while focus is in a text control (B15 guard).
///
/// Returns a stable action code (`action_code`), or `0` to leave the key alone.
#[no_mangle]
pub extern "C" fn akapen_resolve_key(
    ch: u32,
    physical: i32,
    primary: c_int,
    shift: c_int,
    alt: c_int,
    composing: c_int,
    text_editing: c_int,
) -> i32 {
    let input = akapen_core::KeyInput {
        ch: char::from_u32(ch).filter(|c| *c != '\0'),
        physical: physical_key_from_code(physical),
        mods: akapen_core::Modifiers {
            primary: primary != 0,
            shift: shift != 0,
            alt: alt != 0,
        },
        composing: composing != 0,
        text_editing: text_editing != 0,
    };
    match akapen_core::resolve_key(input) {
        Some(action) => action_to_code(action),
        None => action_code::NONE,
    }
}

// ── Palm rejection (spec §5.2) ───────────────────────────────────────────
//
// A pure, engine-independent bridge to `akapen_core::palm` — the same shape as
// `akapen_resolve_key`, but carrying a tiny caller-owned state so the
// pen-priority lock can span calls. The shell classifies each raw event as
// Pen/Touch/Mouse and asks where it should go; touches during pen contact or
// within the lock are rejected as palm. Keeping it in the core means Windows M2
// (WM_POINTER / Wintab) reuses the identical judgment.

/// Stable C ABI routing codes returned by [`akapen_palm_route`]. Mirrored in
/// `include/akapen.h`.
mod route_code {
    /// Feed to the drawing engine (pen / mouse).
    pub const DRAW: i32 = 0;
    /// Use for canvas pan/pinch, not drawing (a deliberate touch, no pen).
    pub const NAVIGATE: i32 = 1;
    /// Drop entirely — a palm touch during pen contact or the pen lock.
    pub const IGNORE: i32 = 2;
}

/// Caller-owned palm-rejection state, persisted between [`akapen_palm_route`]
/// calls (the shell holds one per canvas). Zero-initialized (`pen_down = 0`,
/// `lock_active = 0`) is "no pen seen yet". `#[repr(C)]`, so the shell can hold
/// it by value and pass a pointer in; `akapen_palm_route` reads and updates it
/// in place.
#[repr(C)]
pub struct AkapenPalmState {
    /// Non-zero while a pen is in contact (between its Down and Up).
    pub pen_down: i32,
    /// Non-zero while the post-pen lock is armed (then `lock_until_ms` is live).
    pub lock_active: i32,
    /// Absolute time (ms) the lock expires at; only meaningful when
    /// `lock_active` is non-zero.
    pub lock_until_ms: i64,
}

/// Routes one classified pointer event through the core palm-rejection state
/// machine (spec §5.2), updating `state` in place. Pure apart from that state:
/// time is the injected `now_ms` (monotonic milliseconds), never read here.
///
/// - `kind`: 0=Pen, 1=Touch, 2=Mouse (matches [`akapen_pointer`]).
/// - `phase`: 0=Down, 1=Move, 2=Up.
/// - `now_ms`: a monotonic timestamp in milliseconds.
///
/// Returns a [`route_code`]: `0`=Draw, `1`=Navigate, `2`=Ignore.
///
/// # Safety
/// `state` must be null or a valid, writable pointer to an `AkapenPalmState`.
#[no_mangle]
pub unsafe extern "C" fn akapen_palm_route(
    state: *mut AkapenPalmState,
    kind: c_int,
    phase: c_int,
    now_ms: i64,
) -> i32 {
    let kind = match kind {
        1 => PointerKind::Touch,
        2 => PointerKind::Mouse,
        _ => PointerKind::Pen,
    };
    if state.is_null() {
        // No state to consult (and none to update): fail *closed* for touch so a
        // missing state can never leak a palm into drawing, and open for
        // pen/mouse so real input still works.
        return match kind {
            PointerKind::Touch => route_code::IGNORE,
            _ => route_code::DRAW,
        };
    }
    let st = &mut *state;
    let mut core = PalmState::from_parts(
        st.pen_down != 0,
        if st.lock_active != 0 {
            Some(st.lock_until_ms)
        } else {
            None
        },
    );
    let phase = match phase {
        0 => Phase::Down,
        2 => Phase::Up,
        _ => Phase::Move,
    };
    let routing = akapen_core::palm_route(&mut core, kind, phase, now_ms);
    let (pen_down, lock_until) = core.into_parts();
    st.pen_down = pen_down as i32;
    match lock_until {
        Some(until) => {
            st.lock_active = 1;
            st.lock_until_ms = until;
        }
        None => {
            st.lock_active = 0;
            st.lock_until_ms = 0;
        }
    }
    match routing {
        Routing::Draw => route_code::DRAW,
        Routing::Navigate => route_code::NAVIGATE,
        Routing::Ignore => route_code::IGNORE,
    }
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
    fn enable_diagnostic_logging_is_idempotent_and_raises_max_level() {
        // Calling it more than once (e.g. once from a probe host, once from
        // a future caller) must not panic -- log::set_logger only succeeds
        // the first time process-wide, and this function is documented as
        // tolerating that.
        akapen_enable_diagnostic_logging();
        akapen_enable_diagnostic_logging();
        assert!(log::max_level() >= log::LevelFilter::Warn);
    }

    #[test]
    fn resolve_key_bridges_core_mapping() {
        // 'p' bare -> pen tool (code 1).
        assert_eq!(
            akapen_resolve_key('p' as u32, 1, 0, 0, 0, 0, 0),
            action_code::TOOL_PEN
        );
        // Cmd+Z -> undo (code 10).
        assert_eq!(
            akapen_resolve_key('z' as u32, 11, 1, 0, 0, 0, 0),
            action_code::UNDO
        );
        // Cmd+Shift+Z -> redo (code 11).
        assert_eq!(
            akapen_resolve_key('z' as u32, 11, 1, 1, 0, 0, 0),
            action_code::REDO
        );
        // Physical-only fallback: no char, physical Minus + shift -> right rotate.
        assert_eq!(
            akapen_resolve_key(0, 17, 0, 1, 0, 0, 0),
            action_code::ROTATE_RIGHT
        );
        // PageDown (physical 20, no char) -> next frame.
        assert_eq!(
            akapen_resolve_key(0, 20, 0, 0, 0, 0, 0),
            action_code::NEXT_FRAME
        );
        // IME guard: composing swallows everything.
        assert_eq!(
            akapen_resolve_key('p' as u32, 1, 0, 0, 0, 1, 0),
            action_code::NONE
        );
        // Cmd+O is left to the menu (not stolen).
        assert_eq!(
            akapen_resolve_key('o' as u32, 6, 1, 0, 0, 0, 0),
            action_code::NONE
        );
    }

    #[test]
    fn null_handle_is_safe() {
        unsafe {
            akapen_free(std::ptr::null_mut());
            akapen_undo(std::ptr::null_mut());
            assert_eq!(akapen_pressure_stuck(std::ptr::null_mut()), 0);
        }
    }

    // ── Palm rejection bridge (spec §5.2) ──

    #[test]
    fn palm_route_bridges_pen_lock_and_touch_gating() {
        // Codes mirror akapen_pointer: kind 0=Pen,1=Touch,2=Mouse; phase
        // 0=Down,1=Move,2=Up. Routing 0=Draw,1=Navigate,2=Ignore.
        let mut st = AkapenPalmState {
            pen_down: 0,
            lock_active: 0,
            lock_until_ms: 0,
        };
        unsafe {
            // Touch alone → navigate (pan), never draw.
            assert_eq!(
                akapen_palm_route(&mut st, 1, 0, 0),
                route_code::NAVIGATE,
                "touch alone pans"
            );
            // Pen down draws and marks pen_down.
            assert_eq!(akapen_palm_route(&mut st, 0, 0, 10), route_code::DRAW);
            assert_eq!(st.pen_down, 1);
            // Touch while the pen is down → ignored as palm.
            assert_eq!(
                akapen_palm_route(&mut st, 1, 0, 11),
                route_code::IGNORE,
                "palm during pen contact"
            );
            // Pen up draws and arms the lock (default 500 ms → until 510).
            assert_eq!(akapen_palm_route(&mut st, 0, 2, 10), route_code::DRAW);
            assert_eq!(st.pen_down, 0);
            assert_eq!(st.lock_active, 1);
            assert_eq!(st.lock_until_ms, 510);
            // Touch inside the lock window → still ignored.
            assert_eq!(akapen_palm_route(&mut st, 1, 0, 400), route_code::IGNORE);
            // Touch past the lock → navigates again.
            assert_eq!(akapen_palm_route(&mut st, 1, 0, 510), route_code::NAVIGATE);
            // Mouse always draws (never a palm).
            assert_eq!(akapen_palm_route(&mut st, 2, 0, 600), route_code::DRAW);
        }
    }

    #[test]
    fn palm_route_null_state_fails_closed_for_touch() {
        unsafe {
            // No state: pen/mouse still work, but a touch is rejected rather than
            // risk leaking a palm into drawing.
            assert_eq!(
                akapen_palm_route(std::ptr::null_mut(), 0, 0, 0),
                route_code::DRAW
            );
            assert_eq!(
                akapen_palm_route(std::ptr::null_mut(), 2, 0, 0),
                route_code::DRAW
            );
            assert_eq!(
                akapen_palm_route(std::ptr::null_mut(), 1, 0, 0),
                route_code::IGNORE
            );
        }
    }

    // ── Phase e: pure-logic unit tests (no GPU) ──

    #[test]
    fn accumulate_bake_delta_none_is_identity() {
        use BakeDelta::*;
        assert_eq!(accumulate_bake_delta(None, None), None);
        assert_eq!(accumulate_bake_delta(Append, None), Append);
        assert_eq!(accumulate_bake_delta(Rebuild, None), Rebuild);
        assert_eq!(accumulate_bake_delta(None, Append), Append);
        assert_eq!(accumulate_bake_delta(None, Rebuild), Rebuild);
    }

    #[test]
    fn accumulate_bake_delta_first_change_is_kept_verbatim() {
        // A lone commit between frames stays a cheap Append (not escalated).
        assert_eq!(
            accumulate_bake_delta(BakeDelta::None, BakeDelta::Append),
            BakeDelta::Append
        );
    }

    #[test]
    fn accumulate_bake_delta_second_change_escalates_to_rebuild() {
        use BakeDelta::*;
        // Two commits coalesced before a frame: a lone Append would only bake
        // the last committed stroke, so we escalate to a full Rebuild.
        assert_eq!(accumulate_bake_delta(Append, Append), Rebuild);
        // Rebuild always wins, in either order.
        assert_eq!(accumulate_bake_delta(Append, Rebuild), Rebuild);
        assert_eq!(accumulate_bake_delta(Rebuild, Append), Rebuild);
        assert_eq!(accumulate_bake_delta(Rebuild, Rebuild), Rebuild);
    }

    #[test]
    fn accumulate_bake_delta_ordered_sequence_none_append_then_rebuild_wins() {
        // Explicitly the case from the Phase e brief: None -> Append -> Rebuild
        // must end at Rebuild.
        let mut acc = BakeDelta::None;
        acc = accumulate_bake_delta(acc, BakeDelta::None); // ignored sample
        acc = accumulate_bake_delta(acc, BakeDelta::Append); // a commit
        assert_eq!(acc, BakeDelta::Append);
        acc = accumulate_bake_delta(acc, BakeDelta::Rebuild); // an undo
        assert_eq!(acc, BakeDelta::Rebuild);
    }

    #[test]
    fn view_transform_conversion_expands_scale_and_fills_buffer_size() {
        let vt = AkapenViewTransform {
            center_x: 320.5,
            center_y: 180.25,
            scale: 1.5,
            rotation_deg: 12.0,
        };
        let out = to_view_transform(vt, 640, 360);
        assert!((out.center_x - 320.5).abs() < 1e-4);
        assert!((out.center_y - 180.25).abs() < 1e-4);
        // Single uniform scale expands to both axes.
        assert!((out.scale_x - 1.5).abs() < 1e-6);
        assert!((out.scale_y - 1.5).abs() < 1e-6);
        assert!((out.scale_x - out.scale_y).abs() < 1e-12);
        assert!((out.rotation_deg - 12.0).abs() < 1e-4);
        // Buffer size comes from the attached image, not the struct.
        assert_eq!(out.buffer_w, 640.0);
        assert_eq!(out.buffer_h, 360.0);
    }

    #[test]
    fn render_functions_are_safe_with_null_handle() {
        let view = AkapenViewTransform {
            center_x: 0.0,
            center_y: 0.0,
            scale: 1.0,
            rotation_deg: 0.0,
        };
        unsafe {
            assert_eq!(
                akapen_render_attach(std::ptr::null_mut(), std::ptr::null()),
                1
            );
            akapen_render_resize(std::ptr::null_mut(), 10, 10, 2.0);
            akapen_render_frame(std::ptr::null_mut(), view);
            akapen_render_detach(std::ptr::null_mut());
            assert_eq!(akapen_render_available(std::ptr::null_mut()), 0);
            assert_eq!(
                akapen_render_backend_info(std::ptr::null_mut(), std::ptr::null_mut(), 0),
                0
            );
        }
    }

    #[test]
    fn last_attach_error_captures_the_real_reason_behind_a_code_4_failure() {
        // A known-kind, null-handle desc reaches GpuCanvas::attach and fails
        // there (code 4), unlike the null-desc/unknown-kind cases above
        // (codes 2/3) which never call it. This is platform-independent: on
        // every OS `MetalLayer` with a null handle is rejected by
        // `surface::create` before any GPU work starts.
        let e = akapen_new(16, 16);
        assert!(!e.is_null());
        unsafe {
            let desc = AkapenSurfaceDesc {
                kind: 0, // MetalLayer
                handle: std::ptr::null_mut(),
                display: std::ptr::null_mut(),
                width: 16,
                height: 16,
                scale_factor: 1.0,
            };
            assert_eq!(akapen_render_attach(e, &desc), 4);

            let need = akapen_render_last_attach_error(e, std::ptr::null_mut(), 0);
            assert!(
                need > 1,
                "expected a non-empty error message, got len {need}"
            );
            let mut buf: Vec<c_char> = vec![0; need];
            let got = akapen_render_last_attach_error(e, buf.as_mut_ptr(), buf.len());
            assert_eq!(got, need);
            let msg = CStr::from_ptr(buf.as_ptr()).to_str().unwrap();
            assert!(
                msg.contains("NSView"),
                "expected the underlying RendererError text, got: {msg}"
            );

            akapen_free(e);
        }
    }

    #[test]
    fn backend_info_is_zero_when_no_surface_attached() {
        let e = akapen_new(16, 16);
        assert!(!e.is_null());
        unsafe {
            // No GPU surface attached (headless/CI or CPU-only path): the
            // probe must report "nothing to show" rather than crash.
            assert_eq!(akapen_render_backend_info(e, std::ptr::null_mut(), 0), 0);
            akapen_free(e);
        }
    }

    #[test]
    fn attach_with_null_desc_returns_error_and_stays_on_cpu() {
        let e = akapen_new(32, 32);
        assert!(!e.is_null());
        unsafe {
            // Null desc: clean error, no crash, GPU not marked available.
            assert_eq!(akapen_render_attach(e, std::ptr::null()), 2);
            assert_eq!(akapen_render_available(e), 0);

            // Unknown kind: also a clean error (code 3), still CPU-only.
            let bad = AkapenSurfaceDesc {
                kind: 99,
                handle: std::ptr::null_mut(),
                display: std::ptr::null_mut(),
                width: 32,
                height: 32,
                scale_factor: 1.0,
            };
            assert_eq!(akapen_render_attach(e, &bad), 3);
            assert_eq!(akapen_render_available(e), 0);
            // Codes 2/3 are returned before GpuCanvas::attach ever runs, so
            // they never touch last_attach_error (that field is reserved for
            // code-4 GpuCanvas/RendererError failures specifically).
            assert_eq!(
                akapen_render_last_attach_error(e, std::ptr::null_mut(), 0),
                0
            );

            // The CPU composite path must still work while GPU is unattached.
            let need = akapen_composite_rgba(e, std::ptr::null_mut(), 0);
            assert_eq!(need, 32 * 32 * 4);

            // A frame call with no surface attached is a harmless no-op.
            let view = AkapenViewTransform {
                center_x: 16.0,
                center_y: 16.0,
                scale: 1.0,
                rotation_deg: 0.0,
            };
            akapen_render_frame(e, view);

            akapen_free(e);
        }
    }
}
