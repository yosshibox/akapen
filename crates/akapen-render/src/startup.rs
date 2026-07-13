//! コールドスタート計測(「開く→一筆目」)の headless 経路。
//!
//! [`akapen_core::StartupTrace`] を使い、実際のコールドスタート経路 —
//! instance生成 → adapter/device取得 → 各パイプライン(background/stroke)の
//! シェダコンパイル → 初回bake → 初回offscreenフレーム(readback完了まで)
//! — の各段を記録する。**present/surface系(スワップチェーン取得・
//! present)はここでは配線しない**: 本番のウィンドウ添付経路
//! ([`crate::canvas::GpuCanvas`])はまだ Session 1 の実機検証待ちであり、
//! この計測はそれより前段、`Renderer::new_headless`/`OffscreenTarget` と
//! 同じヘッドレス経路 — 既存のテスト/`gpu_probe`が使っているのと同じ道 —
//! に沿って段を差し込む(スコープ厳守: 最適化はしない、計測のみ)。
//!
//! [`run_headless_cold_start`] は最適化ターゲットではなく計測点そのもの:
//! 呼び出し順序が「実際にコールドスタート時に起きる順序」と一致している
//! ことが本モジュールの回帰テストの対象で、**絶対時間の閾値は入れない**。

use akapen_core::coord::ViewTransform;
use akapen_core::stroke::{Point, Stroke, Tool};
use akapen_core::tessellate::tessellate_stroke;
use akapen_core::StartupTrace;

use crate::background::BackgroundPipeline;
use crate::error::RendererError;
use crate::renderer::{OffscreenTarget, Renderer};
use crate::stroke::StrokePipeline;
use crate::transform::identity_view;

/// Span names this module records, in the order [`run_headless_cold_start`]
/// records them. Exposed as constants so a caller (or a test) can match on
/// them without repeating string literals.
pub const SPAN_INSTANCE_CREATE: &str = "instance_create";
pub const SPAN_ADAPTER_REQUEST: &str = "adapter_request";
pub const SPAN_DEVICE_REQUEST: &str = "device_request";
pub const SPAN_SHADER_COMPILE_BACKGROUND: &str = "shader_compile_background";
pub const SPAN_SHADER_COMPILE_STROKE: &str = "shader_compile_stroke";
pub const SPAN_BACKGROUND_UPLOAD: &str = "background_upload";
pub const SPAN_FIRST_BAKE: &str = "first_bake";
pub const SPAN_FIRST_OFFSCREEN_FRAME: &str = "first_offscreen_frame";

/// Every span [`run_headless_cold_start`] records, in the order it records
/// them — the fixed shape this module's regression test asserts on.
pub const COLD_START_SPANS_IN_ORDER: &[&str] = &[
    SPAN_INSTANCE_CREATE,
    SPAN_ADAPTER_REQUEST,
    SPAN_DEVICE_REQUEST,
    SPAN_SHADER_COMPILE_BACKGROUND,
    SPAN_SHADER_COMPILE_STROKE,
    SPAN_BACKGROUND_UPLOAD,
    SPAN_FIRST_BAKE,
    SPAN_FIRST_OFFSCREEN_FRAME,
];

/// Runs the headless cold-start path once, recording a span (spec: "open →
/// first stroke") for each of [`COLD_START_SPANS_IN_ORDER`] into `trace`.
///
/// This intentionally mirrors [`Renderer::new_headless`] broken into its
/// constituent steps (rather than calling it as one opaque unit) so each
/// step gets its own span, plus the pipeline/bake/first-frame steps that
/// happen once per opened image in the real app. Present/surface bring-up
/// ([`crate::canvas::SurfaceRenderer`]) is out of scope — see module doc.
///
/// Returns `Err` if no GPU adapter is available (same
/// [`RendererError::NoAdapter`]/[`RendererError::RequestDevice`] the
/// existing headless path returns); callers follow this crate's
/// established `AKAPEN_REQUIRE_GPU` skip/hard-fail convention
/// ([`crate::test_support`]) at the call site.
///
/// `background_size` is the `(width, height)` used for the
/// [`SPAN_BACKGROUND_UPLOAD`] span: it stands in for "an image was opened"
/// and lets a caller pass a representative size (e.g. a real canvas
/// resolution) instead of a fixed 1x1 stub, so the recorded span reflects
/// an actual texture allocation/upload cost rather than a near-zero one.
pub async fn run_headless_cold_start(
    trace: &mut StartupTrace,
    background_size: (u32, u32),
) -> Result<Vec<u8>, RendererError> {
    trace
        .begin(SPAN_INSTANCE_CREATE)
        .expect("cold start: duplicate span");
    let instance = crate::instance::create_instance();
    trace
        .end(SPAN_INSTANCE_CREATE)
        .expect("cold start: end without begin");

    // Each fallible await's result is captured into a local *before* `end`
    // is called, and only mapped to an error / propagated with `?`
    // afterwards. This keeps the span panic/error-safe: an early `?` return
    // can no longer skip `end` and leave the span open (review finding:
    // early-return via `?` used to bypass `end` and surface as
    // `UnclosedSpans` instead of the real adapter/device error).
    trace
        .begin(SPAN_ADAPTER_REQUEST)
        .expect("cold start: duplicate span");
    let adapter_result = instance
        .request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::default(),
            force_fallback_adapter: false,
            compatible_surface: None,
            apply_limit_buckets: false,
        })
        .await;
    trace
        .end(SPAN_ADAPTER_REQUEST)
        .expect("cold start: end without begin");
    let adapter = adapter_result.map_err(|_| RendererError::NoAdapter)?;

    trace
        .begin(SPAN_DEVICE_REQUEST)
        .expect("cold start: duplicate span");
    let device_result = adapter
        .request_device(&wgpu::DeviceDescriptor {
            label: Some("akapen-render cold-start headless device"),
            ..Default::default()
        })
        .await;
    trace
        .end(SPAN_DEVICE_REQUEST)
        .expect("cold start: end without begin");
    let (device, queue) = device_result.map_err(RendererError::RequestDevice)?;

    let renderer = Renderer {
        instance,
        adapter,
        device,
        queue,
    };

    // Shader compilation: one span per pipeline, mirroring the two
    // pipelines the real cold-start path builds before any drawing can
    // happen (background quad + wet-ink/bake stroke pipeline). Built for
    // OFFSCREEN_FORMAT since this is the headless path (no swapchain) — the
    // *screen* path ([`crate::canvas::GpuCanvas::attach`]) creates its own,
    // separate `BackgroundPipeline`/`StrokePipeline` instances against the
    // swapchain's surface format for on-screen draws, plus a second
    // `StrokePipeline` for its own bake target; the two spans below measure
    // only the headless pipelines built here, not those screen/bake ones.
    trace
        .begin(SPAN_SHADER_COMPILE_BACKGROUND)
        .expect("cold start: duplicate span");
    let background_pipeline =
        BackgroundPipeline::new(&renderer.device, crate::renderer::OFFSCREEN_FORMAT);
    trace
        .end(SPAN_SHADER_COMPILE_BACKGROUND)
        .expect("cold start: end without begin");

    trace
        .begin(SPAN_SHADER_COMPILE_STROKE)
        .expect("cold start: duplicate span");
    let stroke_pipeline = StrokePipeline::new(&renderer.device, crate::renderer::OFFSCREEN_FORMAT);
    trace
        .end(SPAN_SHADER_COMPILE_STROKE)
        .expect("cold start: end without begin");

    // Background upload: the real cost of "an image was opened" — texture
    // allocation + the RGBA transfer into it. `background_size` lets a
    // caller pass a representative size (real canvas resolution) instead of
    // a fixed 1x1 stub, so this span measures an actual upload rather than
    // a near-zero one. Content is opaque white; only the size affects cost.
    let (background_width, background_height) = background_size;
    let background_rgba = vec![255u8; background_width as usize * background_height as usize * 4];
    trace
        .begin(SPAN_BACKGROUND_UPLOAD)
        .expect("cold start: duplicate span");
    let background = background_pipeline.set_background(
        &renderer.device,
        &renderer.queue,
        &background_rgba,
        background_width,
        background_height,
    );
    trace
        .end(SPAN_BACKGROUND_UPLOAD)
        .expect("cold start: end without begin");

    // First bake: one committed stroke gets baked into a fresh baked_tex —
    // the same "commit -> Rebuild" path `crate::canvas::GpuCanvas::attach`
    // takes for a freshly-opened image with existing history, collapsed
    // here to a single pen stroke so the cold path has something to bake
    // and draw for "first stroke" to mean something concrete.
    let brush = akapen_core::brush::Brush::from_size(4.0);
    let smoothing = akapen_core::smoothing::Smoothing::Off;
    let mut stroke = Stroke::new(Tool::Pen, 4.0, 0x000000ff);
    stroke.points.push(Point::new(0.0, 0.0, 1.0));
    stroke.points.push(Point::new(1.0, 1.0, 1.0));

    trace
        .begin(SPAN_FIRST_BAKE)
        .expect("cold start: duplicate span");
    let baked = crate::bake::BakedTexture::new(&renderer.device, &renderer.queue, 1, 1);
    let verts = tessellate_stroke(&stroke, &brush, smoothing);
    let bake_view = identity_view(1.0, 1.0);
    stroke_pipeline.draw(
        &renderer.device,
        &renderer.queue,
        baked.view(),
        baked.width(),
        baked.height(),
        &verts,
        stroke.color,
        stroke.opacity,
        false,
        &bake_view,
        wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
    );
    // `draw` above only records+submits the GPU commands; without waiting
    // for that submit to actually finish, the real bake cost would leak
    // into (and be double-counted, or misattributed to) the next span
    // instead of being attributed here (review finding: bake completion
    // wasn't awaited before `end`).
    renderer
        .device
        .poll(wgpu::PollType::wait_indefinitely())
        .expect("device poll failed while waiting for first_bake to complete");
    trace
        .end(SPAN_FIRST_BAKE)
        .expect("cold start: end without begin");

    // First offscreen frame: composite background + baked_tex through the
    // same identity view, then read back — the readback's blocking
    // `device.poll` is what makes this span include "GPU actually finished
    // drawing", not just "commands were recorded".
    trace
        .begin(SPAN_FIRST_OFFSCREEN_FRAME)
        .expect("cold start: duplicate span");
    let target = OffscreenTarget::new(&renderer.device, 1, 1);
    let view: ViewTransform = identity_view(1.0, 1.0);
    crate::bake::composite_frame(
        &renderer.device,
        &renderer.queue,
        &target.view,
        1,
        1,
        &background_pipeline,
        &background,
        &baked,
        &stroke_pipeline,
        None,
        &view,
        wgpu::Color::WHITE,
    );
    let pixels = target.read_rgba(&renderer.device, &renderer.queue);
    trace
        .end(SPAN_FIRST_OFFSCREEN_FRAME)
        .expect("cold start: end without begin");

    Ok(pixels)
}

/// Blocking wrapper over [`run_headless_cold_start`] for non-async call
/// sites (tests, and any future CLI-style measurement tool).
pub fn run_headless_cold_start_blocking(
    trace: &mut StartupTrace,
    background_size: (u32, u32),
) -> Result<Vec<u8>, RendererError> {
    pollster::block_on(run_headless_cold_start(trace, background_size))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Session 0 regression (spec 4b): runs the real cold headless path
    /// once and fixes that every span in [`COLD_START_SPANS_IN_ORDER`] gets
    /// recorded, in that exact order — no absolute-duration threshold, only
    /// the *shape* of the trace.
    ///
    /// Deliberately does **not** pre-warm via
    /// [`crate::test_support::try_headless_renderer`] first (review finding:
    /// bringing up a throwaway `Renderer` just to probe adapter availability
    /// warms driver/shader caches before the "cold" run, undermining the
    /// point of measuring a cold path). Instead this calls
    /// [`run_headless_cold_start_blocking`] directly and interprets its own
    /// `NoAdapter`/`RequestDevice` error as the skip/hard-fail signal,
    /// following this crate's `AKAPEN_REQUIRE_GPU` convention
    /// ([`crate::test_support`]) without instantiating a prior renderer.
    #[test]
    fn cold_start_records_every_span_in_order_on_a_real_headless_gpu_path() {
        let mut trace = StartupTrace::new_with_system_clock();
        let result = run_headless_cold_start_blocking(&mut trace, (64, 64));
        let pixels = match result {
            Ok(pixels) => pixels,
            Err(e) => {
                if std::env::var_os("AKAPEN_REQUIRE_GPU").as_deref()
                    == Some(std::ffi::OsStr::new("1"))
                {
                    panic!(
                        "AKAPEN_REQUIRE_GPU=1 が設定されているのに \
                         cold_start_records_every_span_in_order_on_a_real_headless_gpu_path \
                         用の GPU アダプタ/デバイスが取得できなかった({e})。このレーンでは動的\
                         スキップを許さず、GPU描画が実機で検証されないまま緑になることを防ぐ"
                    );
                }
                eprintln!(
                    "skipping cold_start_records_every_span_in_order_on_a_real_headless_gpu_path: \
                     no GPU adapter/device available in this environment ({e}); expected on \
                     headless CI without a GPU or software rasterizer. Set AKAPEN_REQUIRE_GPU=1 \
                     to make this a hard failure instead of a skip."
                );
                return;
            }
        };
        assert_eq!(
            pixels.len(),
            4,
            "1x1 RGBA8 readback must be exactly 4 bytes (background_size only affects the \
             background_upload span, not the offscreen target's own 1x1 size)"
        );

        let spans = trace.finish().expect("cold start left an unclosed span");
        let names: Vec<&str> = spans.iter().map(|(n, _)| *n).collect();
        assert_eq!(
            names, COLD_START_SPANS_IN_ORDER,
            "cold-start span order/coverage regressed"
        );
    }
}
