//! Error types for the render crate. Kept small and explicit rather than
//! reaching for an external error-handling crate (spec §4.7 "外部npm依存を
//! 勝手に追加しない" 相当の方針をRust側にも適用: no `thiserror`/`anyhow` here
//! since the workspace has not approved them).

use core::fmt;

/// Failures that can occur while bringing up the GPU device/queue, or while
/// creating an OS surface (see [`crate::surface::create`]).
#[derive(Debug)]
pub enum RendererError {
    /// No adapter matched the requested options. Common in CI/headless
    /// environments with no GPU and no software (e.g. lavapipe/warp)
    /// rasterizer installed.
    NoAdapter,
    /// The adapter was found but a logical device could not be created from
    /// it (e.g. unsupported required features/limits).
    RequestDevice(wgpu::RequestDeviceError),
    /// Creating an OS surface from a [`crate::surface::SurfaceDesc`] failed.
    CreateSurface(wgpu::CreateSurfaceError),
    /// The requested [`crate::surface::SurfaceKind`] is not backed by a
    /// standard wgpu `Surface` (raw window/display handle) attach path.
    /// See [`crate::surface::SurfaceKind::SwapChainPanel`] doc comment.
    UnsupportedSurfaceKind(&'static str),
    /// `wgpu::Surface::configure` reported a validation error through the
    /// device's uncaptured-error handler (see
    /// [`crate::canvas::SurfaceRenderer::new`]). Note this is *not* the same
    /// thing as [`Self::CreateSurface`]: `Surface::configure`'s public API
    /// returns `()`, not a `Result` — without an installed
    /// `on_uncaptured_error` handler, this failure mode panics the process
    /// instead of surfacing here (discovered during the Windows HWND
    /// presentation de-risk: a real target HWND with no attached
    /// desktop/compositor session fails `configure` with "Invalid surface").
    SurfaceConfigure(String),
}

impl fmt::Display for RendererError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            RendererError::NoAdapter => write!(f, "no wgpu adapter available"),
            RendererError::RequestDevice(e) => write!(f, "failed to request device: {e}"),
            RendererError::CreateSurface(e) => write!(f, "failed to create surface: {e}"),
            RendererError::UnsupportedSurfaceKind(reason) => {
                write!(f, "unsupported surface kind: {reason}")
            }
            RendererError::SurfaceConfigure(msg) => {
                write!(f, "surface configure failed: {msg}")
            }
        }
    }
}

impl std::error::Error for RendererError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            RendererError::RequestDevice(e) => Some(e),
            RendererError::CreateSurface(e) => Some(e),
            _ => None,
        }
    }
}
