//! Centralized `wgpu::Instance` construction.
//!
//! # 落とし穴: Windows(Intel HD 620)で Vulkan ICD がクラッシュする
//!
//! Windows実機(Intel HD 620)で `cargo test -p akapen-render` の最初のGPU
//! テストが `STATUS_ACCESS_VIOLATION`(0xc0000005)でネイティブクラッシュして
//! いた。Windowsイベントログの faulting module から犯人を特定: **Intel純正
//! Vulkan ICD `igvk64.dll`** が `request_device` 直後にアクセス違反を起こす。
//! wgpu/Rust側のバグではない。
//!
//! 単一バックエンドのstandalone probe(`examples/gpu_probe.rs`)で検証した
//! ところ、**DX12単体は完全健全**(instance→adapter→device→triangle→
//! readbackまで完走)で、他バックエンドとの**パリティも完全一致
//! (worst_diff=0)**。一方 Vulkan単体は `request_device` で確実に落ちる。
//!
//! この crate はこれまで全箇所で
//! `wgpu::InstanceDescriptor::new_without_display_handle()` を使っていたが、
//! これは `backends: Backends::default()`(== `Backends::all()`)を返すため
//! `request_adapter` が Vulkan アダプタを優先して選んでしまい、Windows で
//! 全滅していた。また `WGPU_BACKEND` 環境変数は `new_without_display_handle()`
//! では反映されない(`with_env()`/`from_env_or_default()` を呼んだ場合のみ
//! 効く、wgpu 30 の `InstanceDescriptor::with_env` 実装より)。
//!
//! # 対策
//!
//! この関数を全エントリポイント([`crate::renderer`]・[`crate::surface`]・
//! [`crate::canvas`])から共有で呼び、backendの既定値を一箇所に集約する:
//! - Windows既定: `Backends::DX12`(壊れたVulkanを除外。上記の通り実測で健全)
//! - それ以外: 従来通り `Backends::all()`(mac=Metal・Linux=Vulkan/GL等、
//!   非回帰)
//! - その上で `WGPU_BACKEND` 環境変数(`Backends::from_env()`)があれば
//!   それを優先し、デバッグ用の手動オーバーライドを可能にする。

/// Builds the `wgpu::Instance` shared by every GPU entry point in this
/// crate ([`crate::renderer::Renderer`], [`crate::canvas::SurfaceRenderer`],
/// [`crate::surface::create`]'s callers). See the module doc for why the
/// backend selection here matters (Windows Vulkan ICD crash) and must not
/// be duplicated ad-hoc at each call site.
pub fn create_instance() -> wgpu::Instance {
    wgpu::Instance::new(instance_descriptor())
}

/// The `InstanceDescriptor` used by [`create_instance`]. Split out so tests
/// can inspect `backends` without standing up a full `wgpu::Instance`.
fn instance_descriptor() -> wgpu::InstanceDescriptor {
    // `Backends::from_env()` reads `WGPU_BACKEND`; only consulted here
    // (rather than via `with_env()`/`*_from_env()`) so it overrides our
    // platform default but everything else about the descriptor stays
    // identical to `new_without_display_handle()`.
    let backends = wgpu::Backends::from_env().unwrap_or_else(default_backends);
    wgpu::InstanceDescriptor {
        backends,
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    }
}

/// Windows default: DX12 only, excluding the Vulkan ICD that crashes on at
/// least Intel HD 620 (see module doc). DX12 alone was verified healthy and
/// pixel-parity-identical (worst_diff=0) via `examples/gpu_probe.rs`.
#[cfg(target_os = "windows")]
fn default_backends() -> wgpu::Backends {
    wgpu::Backends::DX12
}

/// Non-Windows default: unchanged from this crate's previous behavior
/// (`new_without_display_handle()`'s own default, i.e. `Backends::all()`).
#[cfg(not(target_os = "windows"))]
fn default_backends() -> wgpu::Backends {
    wgpu::Backends::all()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn respects_wgpu_backend_env_override() {
        // SAFETY: this test does not run concurrently with anything else
        // that reads/writes `WGPU_BACKEND` — akapen-render's test suite has
        // no other env-var-dependent instance-selection tests today. If
        // that changes, this needs `--test-threads=1` (already how the
        // Windows GPU lane runs, see task docs) or a lock.
        unsafe {
            std::env::set_var("WGPU_BACKEND", "gl");
        }
        let desc = instance_descriptor();
        unsafe {
            std::env::remove_var("WGPU_BACKEND");
        }
        assert_eq!(desc.backends, wgpu::Backends::GL);
    }

    /// Regression guard for the Windows Vulkan-ICD crash (see module doc):
    /// the *actual* default construction path this crate's Renderer/
    /// SurfaceRenderer/GpuCanvas all go through must not hand back a Vulkan
    /// adapter on Windows. Requires a real GPU adapter, so it follows this
    /// crate's existing `AKAPEN_REQUIRE_GPU` skip/hard-fail convention
    /// (`crate::test_support`) rather than asserting on the descriptor
    /// alone — a descriptor-only check couldn't catch e.g. a future
    /// wgpu upgrade changing what `Backends::all()`-compatible
    /// `request_adapter` actually resolves to.
    #[cfg(target_os = "windows")]
    #[test]
    fn windows_default_instance_does_not_select_the_crashing_vulkan_backend() {
        let Some(renderer) = crate::test_support::try_headless_renderer(
            "windows_default_instance_does_not_select_the_crashing_vulkan_backend",
        ) else {
            return;
        };
        let backend = renderer.adapter.get_info().backend;
        assert_ne!(
            backend,
            wgpu::Backend::Vulkan,
            "Windows既定のInstanceがVulkanを選択している(backend={backend:?})。\
             Intel HD 620のigvk64.dllがrequest_deviceでAVるクラッシュ\
             (0xc0000005)を回避できていない。instance::default_backends()\
             がDX12を返しているか、WGPU_BACKENDが誤って設定されていないか確認せよ。"
        );
    }
}
