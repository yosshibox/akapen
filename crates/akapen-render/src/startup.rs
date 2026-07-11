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
pub async fn run_headless_cold_start(trace: &mut StartupTrace) -> Result<Vec<u8>, RendererError> {
    trace
        .begin(SPAN_INSTANCE_CREATE)
        .expect("cold start: duplicate span");
    let instance = crate::instance::create_instance();
    trace
        .end(SPAN_INSTANCE_CREATE)
        .expect("cold start: end without begin");

    trace
        .begin(SPAN_ADAPTER_REQUEST)
        .expect("cold start: duplicate span");
    let adapter = instance
        .request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::default(),
            force_fallback_adapter: false,
            compatible_surface: None,
            apply_limit_buckets: false,
        })
        .await
        .map_err(|_| RendererError::NoAdapter)?;
    trace
        .end(SPAN_ADAPTER_REQUEST)
        .expect("cold start: end without begin");

    trace
        .begin(SPAN_DEVICE_REQUEST)
        .expect("cold start: duplicate span");
    let (device, queue) = adapter
        .request_device(&wgpu::DeviceDescriptor {
            label: Some("akapen-render cold-start headless device"),
            ..Default::default()
        })
        .await
        .map_err(RendererError::RequestDevice)?;
    trace
        .end(SPAN_DEVICE_REQUEST)
        .expect("cold start: end without begin");

    let renderer = Renderer {
        instance,
        adapter,
        device,
        queue,
    };

    // Shader compilation: one span per pipeline, mirroring the two
    // pipelines the real cold-start path builds before any drawing can
    // happen (background quad + wet-ink/bake stroke pipeline). Built for
    // OFFSCREEN_FORMAT since this is the headless path (no swapchain).
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

    // A tiny 1x1 opaque-white background stands in for "an image was
    // opened" — this path measures pipeline/bake/first-frame cost, not
    // image-decode cost (out of scope, see module doc).
    let background_rgba = [255u8, 255, 255, 255];
    let background = background_pipeline.set_background(
        &renderer.device,
        &renderer.queue,
        &background_rgba,
        1,
        1,
    );

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
) -> Result<Vec<u8>, RendererError> {
    pollster::block_on(run_headless_cold_start(trace))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Session 0 regression (spec 4b): runs the real cold headless path
    /// once and fixes that every span in [`COLD_START_SPANS_IN_ORDER`] gets
    /// recorded, in that exact order — no absolute-duration threshold, only
    /// the *shape* of the trace. Skips cleanly (or hard-fails under
    /// `AKAPEN_REQUIRE_GPU=1`) with no GPU adapter, per this crate's
    /// established convention.
    #[test]
    fn cold_start_records_every_span_in_order_on_a_real_headless_gpu_path() {
        if crate::test_support::try_headless_renderer(
            "cold_start_records_every_span_in_order_on_a_real_headless_gpu_path",
        )
        .is_none()
        {
            return;
        }

        let mut trace = StartupTrace::new_with_system_clock();
        let result = run_headless_cold_start_blocking(&mut trace);
        let pixels = result.expect(
            "run_headless_cold_start failed even though a headless renderer was just \
             confirmed available",
        );
        assert_eq!(
            pixels.len(),
            4,
            "1x1 RGBA8 readback must be exactly 4 bytes"
        );

        let spans = trace.finish().expect("cold start left an unclosed span");
        let names: Vec<&str> = spans.iter().map(|(n, _)| *n).collect();
        assert_eq!(
            names, COLD_START_SPANS_IN_ORDER,
            "cold-start span order/coverage regressed"
        );
    }
}
