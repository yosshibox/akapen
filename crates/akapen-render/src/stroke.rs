//! Phase c: wet-ink stroke drawing (spec §7.4-6 / §6.2 point 3 "ウェット
//! インクの分離"). Draws a [`Vertex`] triangle list — from
//! [`akapen_core::tessellate::tessellate_stroke`] — as a solid color, with
//! the composite mode (pen vs eraser) chosen by fixed-function blend state
//! rather than a shader branch, matching the two composite modes
//! [`akapen_core::raster::bake_stroke`] implements on the CPU:
//!
//! - **Pen**: premultiplied-alpha source-over (`out = src + dst*(1-src.a)`).
//! - **Eraser**: destination-out (`out = dst*(1-src.a)`), draining only
//!   alpha (and, since it's premultiplied, color proportionally) — never
//!   painting new color, matching `bake_stroke`'s `keep = 1.0 - cov` branch.
//!
//! [`StrokePipeline::draw`] is also the primitive Phase d's `baked_tex`
//! append/rebuild path ([`crate::bake`]) is built on: it is agnostic to
//! *which* texture it draws into (`target_view`) or what `view`/`load` op is
//! used, so the same pipeline draws wet ink onto the on-screen target and
//! committed strokes onto `baked_tex`.
//!
//! No per-fragment antialiasing is attempted here (see the module doc on
//! `akapen_core::tessellate`): the tessellated mesh has hard edges, so this
//! module is not a pixel-exact GPU port of `raster.rs`'s coverage-based AA —
//! only their *interior* pixels are expected to agree (see the readback
//! tests in this module and in [`crate::bake`]).
//!
//! **GPU 表示経路は opacity=1 前提(既知の乖離、方針: M1 は明記+回帰テストのみ)。**
//! `tessellate_stroke` は1本のストロークをキャップ/ジョインの円ファンと区間
//! ごとの帯(クアッド)に分解し、それらを**同一 draw call 内で重ねて**描画す
//! る(自己交差する経路や、キャップ/ジョインの円が隣接する帯と重なる領域が
//! 必ず生じる)。このパイプラインの blend state は premultiplied source-over
//! なので、重なった領域は複数回ソースオーバー合成され、alpha が累積して濃く
//! なる。CPU オラクル([`akapen_core::raster`]、単一ストロークをカバレッジ
//! マスクへ**最大値**で合成してから1回だけ焼き込む)は同じ重なりを均一な
//! coverage=1として扱うため濃くならない。opacity=1 のときはどちらの経路も
//! 「1回塗った」のと結果が同じになり退化的に一致するが、opacity<1 では
//! GPU のみ濃くなり CPU オラクルと乖離する。M1 時点ではパレットに不透明色
//! しかなく opacity<1 を UI から提供していないため、恒久修正(マスク折り:
//! ストロークをオフスクリーンへ1回だけ合成してからカバレッジを1回に畳む)
//! はせず、**GPU 表示は opacity=1 前提**と明記した上で回帰テストを
//! `#[ignore]` 付きで残し、将来 opacity<1 を公開する際の地雷を可視化する
//! (下記テストモジュールの `..._diverges_from_cpu_oracle_...` 系)。

use akapen_core::coord::ViewTransform;
use akapen_core::raster::unpack_rgba;
use akapen_core::stroke::{Stroke, Tool};
use akapen_core::tessellate::Vertex;
use bytemuck::{Pod, Zeroable};

use crate::transform::image_to_clip_matrix;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct Uniforms {
    transform: [[f32; 4]; 4],
    color: [f32; 4],
}

/// Whether `stroke` composites in erase (destination-out) mode. Mirrors
/// `akapen_core::raster::bake_stroke`'s `erase` flag exactly (`Tool::Eraser`
/// *or* the `erase` field — the latter lets a non-eraser-tool stroke still
/// erase, which `bake_stroke` also honors).
pub fn is_erase(stroke: &Stroke) -> bool {
    stroke.tool == Tool::Eraser || stroke.erase
}

/// Packs a stroke's color/opacity into the constant premultiplied RGBA this
/// module's fragment shader outputs for every covered fragment (no
/// per-fragment coverage — see module doc). For an erasing stroke, color is
/// irrelevant (the eraser pipeline's blend state has a `Zero` source color
/// factor) and alpha is always `1.0`: `bake_stroke`'s destination-out branch
/// erases by raw geometry coverage alone, never scaled by the eraser
/// stroke's own opacity/color-alpha.
fn premultiplied_color(color: u32, opacity: f64, erase: bool) -> [f32; 4] {
    if erase {
        return [0.0, 0.0, 0.0, 1.0];
    }
    let (r, g, b, a) = unpack_rgba(color);
    let base_alpha = ((a as f64 / 255.0) * opacity.clamp(0.0, 1.0)) as f32;
    [
        (r as f32 / 255.0) * base_alpha,
        (g as f32 / 255.0) * base_alpha,
        (b as f32 / 255.0) * base_alpha,
        base_alpha,
    ]
}

/// Packs `verts` into a tightly-packed little-endian `[f32;2]` byte buffer
/// for upload. `akapen_core::tessellate::Vertex` isn't `bytemuck::Pod` (it's
/// a foreign type this crate doesn't own), so this hand-packs instead of
/// casting.
fn vertices_to_bytes(verts: &[Vertex]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(verts.len() * 8);
    for v in verts {
        bytes.extend_from_slice(&v.pos[0].to_le_bytes());
        bytes.extend_from_slice(&v.pos[1].to_le_bytes());
    }
    bytes
}

/// GPU pipeline pair (pen / eraser blend state) for drawing tessellated
/// strokes. One instance per device/output-format is enough; holds no
/// per-stroke state.
pub struct StrokePipeline {
    pen: wgpu::RenderPipeline,
    eraser: wgpu::RenderPipeline,
    bind_group_layout: wgpu::BindGroupLayout,
}

impl StrokePipeline {
    pub fn new(device: &wgpu::Device, output_format: wgpu::TextureFormat) -> Self {
        let bind_group_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("akapen-render stroke bind group layout"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: false,
                    min_binding_size: None,
                },
                count: None,
            }],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("akapen-render stroke pipeline layout"),
            bind_group_layouts: &[Some(&bind_group_layout)],
            immediate_size: 0,
        });

        let shader = device.create_shader_module(wgpu::include_wgsl!("shaders/stroke.wgsl"));

        let vertex_buffers = [Some(wgpu::VertexBufferLayout {
            array_stride: 8, // 2 x f32
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &wgpu::vertex_attr_array![0 => Float32x2],
        })];

        let make_pipeline = |label: &str, blend: wgpu::BlendState| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(&pipeline_layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_main"),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    buffers: &vertex_buffers,
                },
                primitive: wgpu::PrimitiveState {
                    topology: wgpu::PrimitiveTopology::TriangleList,
                    strip_index_format: None,
                    front_face: wgpu::FrontFace::Ccw,
                    cull_mode: None,
                    unclipped_depth: false,
                    polygon_mode: wgpu::PolygonMode::Fill,
                    conservative: false,
                },
                depth_stencil: None,
                multisample: wgpu::MultisampleState::default(),
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some("fs_main"),
                    compilation_options: wgpu::PipelineCompilationOptions::default(),
                    targets: &[Some(wgpu::ColorTargetState {
                        format: output_format,
                        blend: Some(blend),
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            })
        };

        // Premultiplied-alpha source-over: out = src + dst*(1 - src.a).
        let pen_blend = wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::One,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
        };
        // Destination-out: out = dst*(1 - src.a); src color never contributes.
        let eraser_blend = wgpu::BlendState {
            color: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::Zero,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
            alpha: wgpu::BlendComponent {
                src_factor: wgpu::BlendFactor::Zero,
                dst_factor: wgpu::BlendFactor::OneMinusSrcAlpha,
                operation: wgpu::BlendOperation::Add,
            },
        };

        Self {
            pen: make_pipeline("akapen-render stroke pipeline (pen)", pen_blend),
            eraser: make_pipeline("akapen-render stroke pipeline (eraser)", eraser_blend),
            bind_group_layout,
        }
    }

    /// Draws `verts` (a flat `TriangleList`, image-pixel space — see
    /// [`akapen_core::tessellate::tessellate_stroke`]) into `target_view`,
    /// positioned by `view` (via [`image_to_clip_matrix`]) and blended
    /// according to `erase`.
    ///
    /// `load` controls whether existing `target_view` contents are cleared
    /// first (`wgpu::LoadOp::Clear`, e.g. rebuilding `baked_tex` from
    /// scratch) or preserved (`wgpu::LoadOp::Load`, e.g. drawing wet ink over
    /// an already-composited frame, or appending one stroke to `baked_tex`).
    /// The render pass — and thus the `load` op — always runs, even for an
    /// empty `verts` (a defensive no-op stroke): a caller that needs a clear
    /// with nothing to draw still gets it.
    #[allow(clippy::too_many_arguments)]
    pub fn draw(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_view: &wgpu::TextureView,
        output_w: u32,
        output_h: u32,
        verts: &[Vertex],
        color: u32,
        opacity: f64,
        erase: bool,
        view: &ViewTransform,
        load: wgpu::LoadOp<wgpu::Color>,
    ) {
        let prepared = self.prepare_draw(
            device, queue, output_w, output_h, verts, color, opacity, erase, view,
        );

        let mut encoder =
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("akapen-render stroke pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: target_view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load,
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            self.record_draw(&mut pass, &prepared);
        }
        queue.submit(Some(encoder.finish()));
    }

    /// The buffer/bind-group-building half of [`Self::draw`], split out so a
    /// caller that wants to draw several primitives into the *same* render
    /// pass — one encoder, one `queue.submit` — can prepare every draw's
    /// resources up front and then record them all before ending the pass
    /// (used by [`crate::bake::composite_frame`]; see
    /// [`crate::background::BackgroundPipeline::prepare_texture_draw`] for
    /// the analogous split on the background/`baked_tex` pipeline).
    ///
    /// Unlike the uniform-only [`crate::background::BackgroundPipeline`]
    /// split, this one has to return the vertex buffer too (not just the
    /// bind group): [`Self::record_draw`] borrows it via `set_vertex_buffer`,
    /// which ties it to the render pass's own lifetime, so the caller must
    /// keep the returned [`PreparedDraw`] alive for at least as long as the
    /// pass that records it (mirrors the borrow-checker note [`Self::draw`]
    /// used to carry directly on its own local `vertex_buffer`).
    #[allow(clippy::too_many_arguments)]
    pub fn prepare_draw(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        output_w: u32,
        output_h: u32,
        verts: &[Vertex],
        color: u32,
        opacity: f64,
        erase: bool,
        view: &ViewTransform,
    ) -> PreparedDraw {
        let uniforms = Uniforms {
            transform: image_to_clip_matrix(view, output_w, output_h),
            color: premultiplied_color(color, opacity, erase),
        };
        let uniform_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("akapen-render stroke uniform buffer"),
            size: core::mem::size_of::<Uniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&uniform_buffer, 0, bytemuck::bytes_of(&uniforms));

        let bind_group = device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("akapen-render stroke bind group"),
            layout: &self.bind_group_layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: uniform_buffer.as_entire_binding(),
            }],
        });

        let vertex_buffer = if verts.is_empty() {
            None
        } else {
            let bytes = vertices_to_bytes(verts);
            let buf = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("akapen-render stroke vertex buffer"),
                size: bytes.len() as u64,
                usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
                mapped_at_creation: false,
            });
            queue.write_buffer(&buf, 0, &bytes);
            Some(buf)
        };

        PreparedDraw {
            bind_group,
            vertex_buffer,
            vertex_count: verts.len() as u32,
            erase,
        }
    }

    /// Records a previously [`Self::prepare_draw`]-prepared draw into an
    /// already-open render pass. Does not clear/load or submit — that is the
    /// open pass's/its caller's responsibility, so several calls (e.g.
    /// background, `baked_tex`, then wet ink) can share one pass. A no-op
    /// (same as [`Self::draw`]'s own behavior) when the prepared draw came
    /// from an empty vertex list.
    pub fn record_draw<'pass>(
        &'pass self,
        pass: &mut wgpu::RenderPass<'pass>,
        prepared: &'pass PreparedDraw,
    ) {
        let Some(buf) = &prepared.vertex_buffer else {
            return;
        };
        pass.set_pipeline(if prepared.erase {
            &self.eraser
        } else {
            &self.pen
        });
        pass.set_bind_group(0, &prepared.bind_group, &[]);
        pass.set_vertex_buffer(0, buf.slice(..));
        pass.draw(0..prepared.vertex_count, 0..1);
    }
}

/// The GPU resources one [`StrokePipeline::draw`] call needs, built ahead of
/// opening a render pass (see [`StrokePipeline::prepare_draw`]).
pub struct PreparedDraw {
    bind_group: wgpu::BindGroup,
    vertex_buffer: Option<wgpu::Buffer>,
    vertex_count: u32,
    erase: bool,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::renderer::{OffscreenTarget, OFFSCREEN_FORMAT};
    use crate::test_support::try_headless_renderer;
    use crate::transform::identity_view;
    use akapen_core::brush::Brush;
    use akapen_core::raster::{bake_stroke, pixel_at as raster_pixel_at, RgbaBuffer};
    use akapen_core::smoothing::Smoothing;
    use akapen_core::stroke::Point;
    use akapen_core::tessellate::tessellate_stroke;

    fn pen_stroke(size: f64, pts: &[(f64, f64, f64)]) -> Stroke {
        let mut s = Stroke::new(Tool::Pen, size, 0xFF0000FF); // opaque red
        s.points = pts.iter().map(|&(x, y, p)| Point::new(x, y, p)).collect();
        s
    }

    fn pixel_at(pixels: &[u8], w: u32, x: u32, y: u32) -> [u8; 4] {
        let i = ((y * w + x) * 4) as usize;
        [pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]]
    }

    #[test]
    fn is_erase_matches_bake_stroke_semantics() {
        let mut pen = pen_stroke(10.0, &[(0.0, 0.0, 1.0)]);
        assert!(!is_erase(&pen));
        pen.erase = true; // non-eraser tool, `erase` field set — still erases.
        assert!(is_erase(&pen));

        let mut eraser = Stroke::new(Tool::Eraser, 10.0, 0);
        eraser.points.push(Point::new(0.0, 0.0, 1.0));
        assert!(is_erase(&eraser));
    }

    /// Phase c AC: a wet pen stroke drawn over a transparent target puts ink
    /// on its path, at an interior point (not an AA-fringe edge pixel, which
    /// this hard-edged-triangle pipeline never claimed to match `raster.rs`
    /// pixel-for-pixel on — see module doc).
    #[test]
    fn wet_pen_stroke_paints_ink_along_its_path() {
        let Some(renderer) = try_headless_renderer("wet_pen_stroke_paints_ink_along_its_path")
        else {
            return;
        };
        let size = 64u32;
        let brush = Brush::from_size(16.0);
        let stroke = pen_stroke(16.0, &[(10.0, 32.0, 1.0), (54.0, 32.0, 1.0)]);
        let verts = tessellate_stroke(&stroke, &brush, Smoothing::Off);

        let pipeline = StrokePipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let target = OffscreenTarget::new(&renderer.device, size, size);
        let view = identity_view(size as f64, size as f64);

        pipeline.draw(
            &renderer.device,
            &renderer.queue,
            &target.view,
            size,
            size,
            &verts,
            stroke.color,
            stroke.opacity,
            is_erase(&stroke),
            &view,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
        );

        let pixels = target.read_rgba(&renderer.device, &renderer.queue);
        // Interior of the stroke's centerline.
        let inked = pixel_at(&pixels, size, 32, 32);
        assert_eq!(
            inked,
            [255, 0, 0, 255],
            "stroke interior should be opaque red"
        );

        // Far outside the stroke's path (near the top corner) stays transparent.
        let untouched = pixel_at(&pixels, size, 2, 2);
        assert_eq!(
            untouched,
            [0, 0, 0, 0],
            "far outside the stroke must stay transparent"
        );
    }

    /// Phase c AC: low pressure yields a narrower painted band than high
    /// pressure, for the same stroke path/brush — the tessellate-side
    /// pressure→width behavior must survive onto the GPU output, not just
    /// the CPU bounding-box check `tessellate.rs` already covers.
    #[test]
    fn lower_pressure_paints_a_narrower_band_than_higher_pressure() {
        let Some(renderer) =
            try_headless_renderer("lower_pressure_paints_a_narrower_band_than_higher_pressure")
        else {
            return;
        };
        let size = 64u32;
        let brush = Brush::from_size(20.0);
        let pipeline = StrokePipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let view = identity_view(size as f64, size as f64);

        let painted_row_count = |pressure: f64| -> usize {
            let stroke = pen_stroke(20.0, &[(5.0, 32.0, pressure), (59.0, 32.0, pressure)]);
            let verts = tessellate_stroke(&stroke, &brush, Smoothing::Off);
            let target = OffscreenTarget::new(&renderer.device, size, size);
            pipeline.draw(
                &renderer.device,
                &renderer.queue,
                &target.view,
                size,
                size,
                &verts,
                stroke.color,
                stroke.opacity,
                false,
                &view,
                wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
            );
            let pixels = target.read_rgba(&renderer.device, &renderer.queue);
            (0..size)
                .filter(|&y| pixel_at(&pixels, size, 32, y)[3] > 0)
                .count()
        };

        let thin = painted_row_count(0.05);
        let thick = painted_row_count(1.0);
        assert!(
            thick > thin,
            "high pressure must paint a wider band: thin={thin}, thick={thick}"
        );
    }

    /// Phase c AC (eraser blend): drawing a pen stroke then an eraser stroke
    /// over the same interior point clears it back to transparent, matching
    /// `bake_stroke`'s destination-out semantics.
    #[test]
    fn eraser_blend_clears_previously_painted_ink() {
        let Some(renderer) = try_headless_renderer("eraser_blend_clears_previously_painted_ink")
        else {
            return;
        };
        let size = 64u32;
        let pipeline = StrokePipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let target = OffscreenTarget::new(&renderer.device, size, size);
        let view = identity_view(size as f64, size as f64);
        let brush = Brush::from_size(20.0);

        let pen = pen_stroke(20.0, &[(10.0, 32.0, 1.0), (54.0, 32.0, 1.0)]);
        let pen_verts = tessellate_stroke(&pen, &brush, Smoothing::Off);
        pipeline.draw(
            &renderer.device,
            &renderer.queue,
            &target.view,
            size,
            size,
            &pen_verts,
            pen.color,
            pen.opacity,
            false,
            &view,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
        );
        let after_pen = target.read_rgba(&renderer.device, &renderer.queue);
        assert_eq!(
            pixel_at(&after_pen, size, 32, 32)[3],
            255,
            "pen must have painted first"
        );

        let mut eraser = Stroke::new(Tool::Eraser, 24.0, 0);
        eraser.points = vec![Point::new(32.0, 32.0, 1.0)];
        let eraser_brush = Brush::from_size(24.0);
        let eraser_verts = tessellate_stroke(&eraser, &eraser_brush, Smoothing::Off);
        pipeline.draw(
            &renderer.device,
            &renderer.queue,
            &target.view,
            size,
            size,
            &eraser_verts,
            eraser.color,
            eraser.opacity,
            is_erase(&eraser),
            &view,
            wgpu::LoadOp::Load,
        );
        let after_erase = target.read_rgba(&renderer.device, &renderer.queue);
        assert_eq!(
            pixel_at(&after_erase, size, 32, 32)[3],
            0,
            "eraser must clear alpha back to transparent"
        );
    }

    // ── opacity<1 self-overlap divergence (review 所見1) ──
    //
    // Both tests below draw one translucent (opacity 0.5) stroke through
    // this module's real GPU pipeline and compare the alpha channel against
    // the CPU oracle (`akapen_core::raster::bake_stroke`, single max-coverage
    // pass) at a pixel known to be doubly covered by this stroke's own
    // tessellated geometry (see the module doc's opacity=1-premise note).
    // Both are `#[ignore]`d: they currently fail (GPU alpha ends up higher
    // than the CPU oracle's), which is the documented, accepted M1 gap, not
    // a regression to fix here — the point is to keep the divergence visible
    // in the suite instead of silently un-covered.

    /// A self-intersecting path (out and back along the same line): the two
    /// overlapping passes' tessellated quads/joins fully overlap along the
    /// shared segment.
    #[test]
    #[ignore = "既知の乖離: opacity<1 で GPU が自己重なり alpha を累積(開発日誌の所見1)。\
                マスク折り実装時に ignore を外す"]
    fn self_intersecting_translucent_stroke_diverges_from_cpu_oracle_at_the_crossing() {
        let Some(renderer) = try_headless_renderer(
            "self_intersecting_translucent_stroke_diverges_from_cpu_oracle_at_the_crossing",
        ) else {
            return;
        };
        let size = 64u32;
        let brush = Brush::from_size(12.0);
        // Straight down, then straight back up the same line: entirely
        // self-overlapping, not just at caps/joins.
        let mut stroke = pen_stroke(
            12.0,
            &[(32.0, 5.0, 1.0), (32.0, 55.0, 1.0), (32.0, 5.0, 1.0)],
        );
        stroke.opacity = 0.5;

        // CPU oracle: bake_stroke onto a transparent layer (same semantics
        // `raster.rs`'s own self-overlap test — `overlapping_stamps_in_one_
        // stroke_do_not_darken` — relies on: max coverage per pixel, so
        // doubling back does not raise alpha above a single pass).
        let mut cpu_layer = RgbaBuffer::transparent(size, size);
        bake_stroke(&mut cpu_layer, &stroke, &brush, Smoothing::Off);
        let expected_alpha = raster_pixel_at(&cpu_layer, 32, 30).3;

        let verts = tessellate_stroke(&stroke, &brush, Smoothing::Off);
        let pipeline = StrokePipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let target = OffscreenTarget::new(&renderer.device, size, size);
        let view = identity_view(size as f64, size as f64);
        pipeline.draw(
            &renderer.device,
            &renderer.queue,
            &target.view,
            size,
            size,
            &verts,
            stroke.color,
            stroke.opacity,
            false,
            &view,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
        );
        let pixels = target.read_rgba(&renderer.device, &renderer.queue);
        let got_alpha = pixel_at(&pixels, size, 32, 30)[3];

        const TOLERANCE: i32 = 4;
        assert!(
            (got_alpha as i32 - expected_alpha as i32).abs() <= TOLERANCE,
            "GPU alpha {got_alpha} should match the CPU oracle's {expected_alpha} at the \
             self-intersection, but the GPU premultiplied blend accumulates alpha across the \
             two overlapping passes instead of capping at single coverage"
        );
    }

    /// A single straight two-point stroke: its own round end-cap fan
    /// overlaps the segment's tapered quad near the endpoint (every real
    /// stroke has this overlap by construction — see `tessellate_stroke`'s
    /// module doc), so this is the "ordinary" case, not a contrived one.
    #[test]
    #[ignore = "既知の乖離: opacity<1 で GPU がキャップ端の重なりで alpha を累積(開発日誌の\
                所見1)。マスク折り実装時に ignore を外す"]
    fn translucent_stroke_diverges_from_cpu_oracle_where_the_cap_overlaps_the_segment() {
        let Some(renderer) = try_headless_renderer(
            "translucent_stroke_diverges_from_cpu_oracle_where_the_cap_overlaps_the_segment",
        ) else {
            return;
        };
        let size = 64u32;
        let brush = Brush::from_size(16.0);
        let mut stroke = pen_stroke(16.0, &[(10.0, 32.0, 1.0), (54.0, 32.0, 1.0)]);
        stroke.opacity = 0.5;

        let mut cpu_layer = RgbaBuffer::transparent(size, size);
        bake_stroke(&mut cpu_layer, &stroke, &brush, Smoothing::Off);
        // Near the start endpoint: inside both the round cap's fan and the
        // segment quad's own coverage of that same area.
        let expected_alpha = raster_pixel_at(&cpu_layer, 12, 32).3;

        let verts = tessellate_stroke(&stroke, &brush, Smoothing::Off);
        let pipeline = StrokePipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let target = OffscreenTarget::new(&renderer.device, size, size);
        let view = identity_view(size as f64, size as f64);
        pipeline.draw(
            &renderer.device,
            &renderer.queue,
            &target.view,
            size,
            size,
            &verts,
            stroke.color,
            stroke.opacity,
            false,
            &view,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
        );
        let pixels = target.read_rgba(&renderer.device, &renderer.queue);
        let got_alpha = pixel_at(&pixels, size, 12, 32)[3];

        const TOLERANCE: i32 = 4;
        assert!(
            (got_alpha as i32 - expected_alpha as i32).abs() <= TOLERANCE,
            "GPU alpha {got_alpha} should match the CPU oracle's {expected_alpha} where the cap \
             fan overlaps the segment quad, but the GPU premultiplied blend accumulates alpha \
             across the two overlapping primitives instead of capping at single coverage"
        );
    }
}
