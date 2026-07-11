//! OS-neutral surface description (spec §7.4-6: 「シェルが用意したネイティブ
//! ビュー…へコアが wgpu で直接描く」). All per-OS surface creation is closed
//! over by [`create`] so that writing the Windows mapping later only means
//! adding a [`SurfaceKind`] match arm — nothing above this module should ever
//! need to know about `raw_window_handle` directly.
//!
//! mac (CAMetalLayer) and Windows Win32 (Hwnd) are wired through wgpu's
//! standard raw-window/display-handle surface path. WinUI 3's
//! `SwapChainPanel` is **not** — see [`SurfaceKind::SwapChainPanel`].

use core::ffi::c_void;
use core::ptr::NonNull;

use raw_window_handle::{RawDisplayHandle, RawWindowHandle};

use crate::error::RendererError;

/// Which OS-native view kind a [`SurfaceDesc`] points at.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SurfaceKind {
    /// macOS/iOS: an `NSView`/`UIView` that either already hosts a
    /// `CAMetalLayer` or that wgpu will host one on.
    ///
    /// Named after the eventual backing layer (spec wording), but per
    /// `raw_window_handle` 0.6 (which has no direct `CAMetalLayer` handle
    /// variant) [`SurfaceDesc::handle`] must be the `NSView*`/`UIView*`
    /// pointer, not the layer itself — this is what wgpu's public
    /// `AppKitWindowHandle`/`UiKitWindowHandle` path actually consumes. See
    /// the "迷った点" note in the Phase a+b implementation report.
    MetalLayer,
    /// Windows (Win32 desktop / classic HWND-hosted windows): a window
    /// handle usable directly with `raw_window_handle::Win32WindowHandle`.
    Hwnd,
    /// WinUI 3 (Windows App SDK) `SwapChainPanel`. **Not** a standard wgpu
    /// `Surface` target: a `SwapChainPanel` has no HWND of its own and is
    /// attached via `ISwapChainPanelNative::SetSwapChain` on a DXGI swap
    /// chain created directly against the D3D12 device — a different
    /// attach mechanism than `raw_window_handle`'s window/display handles.
    /// [`create`] returns [`RendererError::UnsupportedSurfaceKind`] for this
    /// kind; wiring it is left to the Windows FFI milestone (Phase e), which
    /// will need to reach into `wgpu-hal`'s D3D12 device rather than go
    /// through `wgpu::Surface`.
    SwapChainPanel,
}

/// Describes an OS-native drawing surface in a platform-neutral shape, so
/// that [`create`] is the single choke point a Windows port has to extend
/// (spec instruction: "Windows写像でkind差し替えのみになるように").
#[derive(Debug, Clone, Copy)]
pub struct SurfaceDesc {
    pub kind: SurfaceKind,
    /// The native view/window handle. Meaning depends on `kind`; see
    /// [`SurfaceKind`] variant docs. Must be non-null and valid for the
    /// lifetime of the returned `Surface` (safety contract of [`create`]).
    pub handle: *mut c_void,
    /// The native display/connection handle, if the platform has one
    /// distinct from `handle` (e.g. an `NSApplication`/Win32 module handle
    /// slot). May be null — most desktop platforms don't need one.
    pub display: *mut c_void,
    pub width: u32,
    pub height: u32,
    pub scale_factor: f64,
}

// SAFETY rationale (spec §7.4-6): `SurfaceDesc` only carries raw pointers
// across the shell -> core boundary, exactly like the C ABI it is meant to
// sit behind (§7.2). It performs no dereference itself; `create` is the only
// consumer and is unsafe precisely because it does.
unsafe impl Send for SurfaceDesc {}

/// Creates a wgpu [`wgpu::Surface`] from an OS-neutral [`SurfaceDesc`].
///
/// # Safety
///
/// The caller must ensure `desc.handle` (and `desc.display`, if non-null) are
/// valid native handles for `desc.kind`, and that the thing they point to
/// outlives the returned `Surface`. This mirrors the safety contract of
/// `wgpu::Instance::create_surface_unsafe`, which this function wraps.
pub unsafe fn create(
    instance: &wgpu::Instance,
    desc: SurfaceDesc,
) -> Result<wgpu::Surface<'static>, RendererError> {
    let raw_window_handle = match desc.kind {
        SurfaceKind::MetalLayer => {
            let ns_view = NonNull::new(desc.handle)
                .ok_or(RendererError::UnsupportedSurfaceKind("null NSView handle"))?;
            RawWindowHandle::AppKit(raw_window_handle::AppKitWindowHandle::new(ns_view))
        }
        SurfaceKind::Hwnd => {
            let hwnd = core::num::NonZeroIsize::new(desc.handle as isize).ok_or(
                RendererError::UnsupportedSurfaceKind("null/zero HWND handle"),
            )?;
            RawWindowHandle::Win32(raw_window_handle::Win32WindowHandle::new(hwnd))
        }
        SurfaceKind::SwapChainPanel => {
            return Err(RendererError::UnsupportedSurfaceKind(
                "SwapChainPanel has no HWND; attach via ISwapChainPanelNative on a \
                 D3D12-native DXGI swap chain instead of wgpu::Surface (Phase e)",
            ));
        }
    };

    let raw_display_handle = match desc.kind {
        SurfaceKind::MetalLayer => {
            RawDisplayHandle::AppKit(raw_window_handle::AppKitDisplayHandle::new())
        }
        SurfaceKind::Hwnd => {
            RawDisplayHandle::Windows(raw_window_handle::WindowsDisplayHandle::new())
        }
        SurfaceKind::SwapChainPanel => unreachable!("returned above"),
    };

    // SAFETY: forwarded to the caller of this function (see doc comment).
    unsafe {
        instance
            .create_surface_unsafe(wgpu::SurfaceTargetUnsafe::RawHandle {
                raw_display_handle: Some(raw_display_handle),
                raw_window_handle,
            })
            .map_err(RendererError::CreateSurface)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn swap_chain_panel_is_reported_unsupported_not_silently_ignored() {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let desc = SurfaceDesc {
            kind: SurfaceKind::SwapChainPanel,
            handle: core::ptr::null_mut(),
            display: core::ptr::null_mut(),
            width: 100,
            height: 100,
            scale_factor: 1.0,
        };
        // SAFETY: handle is null and never dereferenced on this path (the
        // SwapChainPanel arm returns before touching it).
        let err = unsafe { create(&instance, desc) }.unwrap_err();
        assert!(matches!(err, RendererError::UnsupportedSurfaceKind(_)));
    }

    #[test]
    fn null_metal_layer_handle_is_a_clean_error_not_a_panic() {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let desc = SurfaceDesc {
            kind: SurfaceKind::MetalLayer,
            handle: core::ptr::null_mut(),
            display: core::ptr::null_mut(),
            width: 100,
            height: 100,
            scale_factor: 1.0,
        };
        // SAFETY: a null handle is rejected before any dereference.
        let err = unsafe { create(&instance, desc) }.unwrap_err();
        assert!(matches!(err, RendererError::UnsupportedSurfaceKind(_)));
    }
}
