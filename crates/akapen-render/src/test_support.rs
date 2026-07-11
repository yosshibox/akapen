//! Shared test-only helper (review finding item 3): every GPU-touching test
//! in this crate (`renderer.rs`/`stroke.rs`/`bake.rs`/`background.rs`, and
//! `canvas.rs` if it ever grows its own) needs to skip cleanly when no GPU
//! adapter is available — headless CI without a GPU or software rasterizer
//! is expected to hit this. Left as a silent dynamic skip, though, a whole
//! GPU test suite can stay "green" on a machine that never actually
//! exercised a single GPU draw call, which is indistinguishable from a
//! suite that genuinely passed. Setting `AKAPEN_REQUIRE_GPU=1` promotes that
//! skip to a hard test failure, so a developer or CI lane that has a GPU and
//! wants a real signal can assert on it instead of trusting the log output.
//!
//! Not a `#[cfg(test)]` item at the crate root — this module itself is
//! `#[cfg(test)]`-gated in `lib.rs`, so it only exists in test builds, but is
//! still reachable from every other module's own `#[cfg(test)] mod tests`
//! within this crate via `crate::test_support::...`.

use crate::renderer::Renderer;

/// Brings up a headless [`Renderer`], or returns `None` so the caller can
/// `return` early and skip the test — unless `AKAPEN_REQUIRE_GPU=1` is set
/// in the environment, in which case a missing adapter panics (fails the
/// test) instead of skipping. `test_name` is only used for the printed
/// skip/panic message.
pub fn try_headless_renderer(test_name: &str) -> Option<Renderer> {
    match Renderer::new_headless_blocking() {
        Ok(r) => Some(r),
        Err(e) => {
            if std::env::var_os("AKAPEN_REQUIRE_GPU").as_deref() == Some(std::ffi::OsStr::new("1"))
            {
                panic!(
                    "AKAPEN_REQUIRE_GPU=1 が設定されているのに {test_name} 用の GPU アダプタ\
                     が取得できなかった({e})。このレーンでは動的スキップを許さず、GPU 描画が\
                     実機で検証されないまま緑になることを防ぐ"
                );
            }
            eprintln!(
                "skipping {test_name}: no GPU adapter available in this \
                 environment ({e}); expected on headless CI without a GPU \
                 or software rasterizer. Set AKAPEN_REQUIRE_GPU=1 to make \
                 this a hard failure instead of a skip."
            );
            None
        }
    }
}
