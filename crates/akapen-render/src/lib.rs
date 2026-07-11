//! Akapen GPU drawing crate (spec §7.4-6 「GPU 描画(wgpu: Metal/D3D12)+
//! オフスクリーン焼き込み」). This crate owns the wgpu device/surface layer
//! and the background-image display path (view transform → screen).
//!
//! Scope (Phase a+b only; see `docs/仕様書.md` §6/§7):
//! - Phase a: crate skeleton, wgpu device/adapter/queue bring-up, an
//!   OS-neutral surface descriptor (`surface` module), and a headless
//!   offscreen render path used for tests (no window needed).
//! - Phase b: uploading a background RGBA image as a GPU texture and
//!   displaying it through [`akapen_core::coord::ViewTransform`] (zoom /
//!   pan / rotation), matching the core's `image_to_screen` oracle.
//!
//! - Phase c: wet-ink stroke drawing ([`stroke`]) — tessellated triangle
//!   geometry drawn with pen (premultiplied source-over) or eraser
//!   (destination-out) blend state.
//! - Phase d: the offscreen committed-stroke bake texture and per-frame
//!   composite ([`bake`]) — background → `baked_tex` → wet ink, O(1) per
//!   frame in stroke count.
//!
//! `wgpu::Instance` construction is centralized in the internal `instance`
//! module (see its doc comment) rather than repeated at each call site,
//! because on Windows the platform default must differ from every other
//! platform to route around a crashing Vulkan ICD.

pub mod background;
pub mod bake;
pub mod canvas;
pub mod error;
pub(crate) mod instance;
pub mod renderer;
pub mod stroke;
pub mod surface;
#[cfg(test)]
pub(crate) mod test_support;
pub mod transform;

pub use background::BackgroundPipeline;
pub use bake::{apply_bake_delta, composite_frame, plan_bake, BakePlan, BakedTexture};
pub use canvas::{GpuCanvas, SurfaceRenderer};
pub use error::RendererError;
pub use renderer::{OffscreenTarget, Renderer, OFFSCREEN_FORMAT};
pub use stroke::StrokePipeline;
pub use surface::{SurfaceDesc, SurfaceKind};
