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
    /// `Surface` target reachable through `raw_window_handle`: a
    /// `SwapChainPanel` has no HWND of its own. Unlike the original Phase e
    /// assumption (see the superseded wording below, kept for history),
    /// wgpu 30 exposes this directly as a public, `cfg(dx12)`-gated
    /// `wgpu::SurfaceTargetUnsafe::SwapChainPanel(*mut c_void)` variant
    /// (confirmed against the vendored `wgpu-30.0.0`/`wgpu-hal-30.0.0`
    /// source and the C 節 reconnaissance in `docs/開発日誌.md`): passing it
    /// to `Instance::create_surface_unsafe` internally does
    /// `IDXGIFactory2::CreateSwapChainForComposition` followed by
    /// `ISwapChainPanelNative::SetSwapChain`. No new dependency on
    /// `wgpu-hal`/`wgpu-core` (or reaching for a raw `ID3D12Device`) is
    /// needed. [`create`] therefore forwards `desc.handle` — which the
    /// caller must have already `QueryInterface`'d from the XAML
    /// `SwapChainPanel` object to `ISwapChainPanelNative`
    /// (`microsoft.ui.xaml.media.dxinterop.h`) — straight to that variant on
    /// Windows; on non-Windows targets (where `cfg(dx12)` never holds) this
    /// arm still returns [`RendererError::UnsupportedSurfaceKind`], keeping
    /// mac/Linux behavior byte-for-byte unchanged.
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
    // SwapChainPanel does not go through `raw_window_handle` at all (it has
    // no HWND) -- it uses wgpu's own `SurfaceTargetUnsafe::SwapChainPanel`
    // variant directly, so it is handled up front and returns early.
    if desc.kind == SurfaceKind::SwapChainPanel {
        return create_swap_chain_panel(instance, desc);
    }

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
        SurfaceKind::SwapChainPanel => unreachable!("returned above"),
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

/// Windows-only path for [`SurfaceKind::SwapChainPanel`] (see its doc
/// comment for the wgpu API this rests on). Split out of [`create`] so the
/// `#[cfg(...)]` gate on the actual `wgpu::SurfaceTargetUnsafe::SwapChainPanel`
/// call stays localized to one small function.
///
/// # Safety
/// Same contract as [`create`]: `desc.handle` must be a valid, non-null
/// `ISwapChainPanelNative*` for the lifetime of the returned `Surface`.
#[cfg(target_os = "windows")]
unsafe fn create_swap_chain_panel(
    instance: &wgpu::Instance,
    desc: SurfaceDesc,
) -> Result<wgpu::Surface<'static>, RendererError> {
    if desc.handle.is_null() {
        return Err(RendererError::UnsupportedSurfaceKind(
            "null ISwapChainPanelNative handle",
        ));
    }
    // SAFETY: forwarded to the caller of `create` (see its doc comment); the
    // non-null check above only rules out the one precondition this function
    // can check itself.
    unsafe {
        instance
            .create_surface_unsafe(wgpu::SurfaceTargetUnsafe::SwapChainPanel(desc.handle))
            .map_err(RendererError::CreateSurface)
    }
}

/// Non-Windows (or dx12-feature-disabled) fallback: `wgpu`'s
/// `SwapChainPanel` variant only exists under `cfg(dx12)`
/// (`all(target_os = "windows", feature = "dx12")` per wgpu 30's own
/// build script), so on every other target this keeps returning the same
/// explicit, non-silent error `create` always returned for this kind —
/// mac/Linux behavior is byte-for-byte unchanged.
#[cfg(not(target_os = "windows"))]
unsafe fn create_swap_chain_panel(
    _instance: &wgpu::Instance,
    _desc: SurfaceDesc,
) -> Result<wgpu::Surface<'static>, RendererError> {
    Err(RendererError::UnsupportedSurfaceKind(
        "SwapChainPanel requires Windows + the dx12 wgpu feature (both compiled out here)",
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn swap_chain_panel_is_reported_unsupported_not_silently_ignored() {
        let instance = crate::instance::create_instance();
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

    /// Windows-only: pins that a `SwapChainPanel` request now *reaches* the
    /// DX12 `create_swap_chain_panel` branch (rather than the platform-
    /// generic "not implemented" message the non-Windows fallback still
    /// returns) — i.e. the wgpu-API wiring is live, not just a stub. Actually
    /// presenting through a real `ISwapChainPanelNative` needs a WinUI/XAML
    /// host (Session 1 GUI de-risk, tracked in `docs/開発日誌.md`), so this
    /// only exercises the reachable-but-null-handle guard.
    #[cfg(target_os = "windows")]
    #[test]
    fn swap_chain_panel_null_handle_is_rejected_by_the_dx12_branch_specifically() {
        let instance = crate::instance::create_instance();
        let desc = SurfaceDesc {
            kind: SurfaceKind::SwapChainPanel,
            handle: core::ptr::null_mut(),
            display: core::ptr::null_mut(),
            width: 100,
            height: 100,
            scale_factor: 1.0,
        };
        // SAFETY: handle is null and never dereferenced (rejected up front).
        let err = unsafe { create(&instance, desc) }.unwrap_err();
        match err {
            RendererError::UnsupportedSurfaceKind(msg) => {
                assert!(
                    msg.contains("ISwapChainPanelNative"),
                    "expected the DX12-branch-specific null-handle message, got: {msg}"
                );
            }
            other => panic!("expected UnsupportedSurfaceKind, got {other:?}"),
        }
    }

    #[test]
    fn null_metal_layer_handle_is_a_clean_error_not_a_panic() {
        let instance = crate::instance::create_instance();
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
