//! Phase b: background image display (spec §7.4-6 / §7.2 `set_background`).
//! Uploads an RGBA image as a GPU texture and draws it as a full-image quad
//! positioned by [`akapen_core::coord::ViewTransform`] via
//! [`crate::transform::uv_to_clip_matrix`].
//!
//! Compositing policy (spec instruction): premultiplied alpha. This module
//! only ever draws the background as the bottom-most layer into a freshly
//! cleared target (`blend: None` — there is nothing under it to blend
//! against yet), so premultiplication doesn't change today's output; it
//! matters once Phase c adds a wet-ink layer on top and needs to blend
//! against this background, which is why the background texture itself must
//! already be stored premultiplied (the caller's responsibility — see
//! [`BackgroundPipeline::set_background`] doc comment) rather than deferring
//! the decision to that later pass.

use akapen_core::coord::ViewTransform;
use bytemuck::{Pod, Zeroable};

use crate::transform::uv_to_clip_matrix;

#[repr(C)]
#[derive(Debug, Clone, Copy, Pod, Zeroable)]
struct Uniforms {
    transform: [[f32; 4]; 4],
}

/// A background image uploaded to the GPU. Opaque handle returned by
/// [`BackgroundPipeline::set_background`]; hang on to it and pass it back
/// into [`BackgroundPipeline::render`] every frame instead of re-uploading.
pub struct BackgroundTexture {
    // Never read directly (only `view` is), but must be kept alive: dropping
    // the `wgpu::Texture` while `view` is still in use is a use-after-free
    // at the GPU level, so this field exists purely for its `Drop` impl.
    #[allow(dead_code)]
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    width: u32,
    height: u32,
}

impl BackgroundTexture {
    pub fn width(&self) -> u32 {
        self.width
    }
    pub fn height(&self) -> u32 {
        self.height
    }
    /// Read-only accessor for [`crate::bake::composite_frame`]'s single-pass
    /// path, which needs to pass this texture's view into
    /// [`BackgroundPipeline::prepare_texture_draw`] directly instead of
    /// going through [`BackgroundPipeline::render`].
    pub fn view(&self) -> &wgpu::TextureView {
        &self.view
    }
}

/// GPU pipeline for drawing a [`BackgroundTexture`] through a
/// [`ViewTransform`]. One instance per device is enough; it holds no
/// per-image state (that lives in [`BackgroundTexture`]).
pub struct BackgroundPipeline {
    pipeline: wgpu::RenderPipeline,
    bind_group_layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
}

impl BackgroundPipeline {
    pub fn new(device: &wgpu::Device, output_format: wgpu::TextureFormat) -> Self {
        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("akapen-render background sampler"),
            address_mode_u: wgpu::AddressMode::ClampToEdge,
            address_mode_v: wgpu::AddressMode::ClampToEdge,
            address_mode_w: wgpu::AddressMode::ClampToEdge,
            // Nearest, not Linear: keeps the identity-transform readback
            // test an exact byte match against the source image instead of
            // a blend of neighboring texels (spec Phase b AC: "オフスクリー
            // ン読み戻しが背景RGBAと一致(許容誤差±数階調)"). Revisit once a
            // real "fit"/arbitrary-zoom UI needs smoother minification.
            mag_filter: wgpu::FilterMode::Nearest,
            min_filter: wgpu::FilterMode::Nearest,
            ..Default::default()
        });

        let bind_group_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("akapen-render background bind group layout"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::VERTEX,
                    ty: wgpu::BindingType::Buffer {
                        ty: wgpu::BufferBindingType::Uniform,
                        has_dynamic_offset: false,
                        min_binding_size: None,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 2,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::NonFiltering),
                    count: None,
                },
            ],
        });

        let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("akapen-render background pipeline layout"),
            bind_group_layouts: &[Some(&bind_group_layout)],
            immediate_size: 0,
        });

        let shader = device.create_shader_module(wgpu::include_wgsl!("shaders/background.wgsl"));
        let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("akapen-render background pipeline"),
            layout: Some(&pipeline_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_main"),
                compilation_options: wgpu::PipelineCompilationOptions::default(),
                buffers: &[],
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
                    // Premultiplied-alpha source-over, not `None` (replace):
                    // this pipeline draws the bottom-most background layer
                    // via `render` (blending is a no-op there — the target
                    // was just cleared to transparent, so `dst == 0` and
                    // `src + 0*(1-a) == src` regardless of blend mode) *and*
                    // Phase d's `baked_tex` via `render_texture` with
                    // `LoadOp::Load` (crate::bake), where `baked_tex` has
                    // genuinely transparent regions that must let the
                    // background already in `target_view` show through
                    // rather than being overwritten with (0,0,0,0). `None`
                    // (replace) would be correct for the first case but
                    // silently wrong for the second, so this pipeline always
                    // blends — see the module doc's premultiplied-alpha
                    // policy note.
                    blend: Some(wgpu::BlendState {
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
                    }),
                    write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None,
            cache: None,
        });

        Self {
            pipeline,
            bind_group_layout,
            sampler,
        }
    }

    /// Uploads `rgba` (`w * h * 4` bytes, row-major top-to-bottom) as a GPU
    /// texture. Per this module's premultiplied-alpha policy, `rgba` must
    /// already be premultiplied if it carries partial alpha; fully-opaque
    /// image data (alpha = 255 everywhere) is unaffected either way, which
    /// covers every case this Phase b implementation is tested against.
    ///
    /// Panics if `rgba.len() != w as usize * h as usize * 4` (programmer
    /// error at the call site, not untrusted input — the FFI boundary that
    /// will validate real caller input is Phase e).
    pub fn set_background(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        rgba: &[u8],
        w: u32,
        h: u32,
    ) -> BackgroundTexture {
        assert_eq!(
            rgba.len(),
            w as usize * h as usize * 4,
            "set_background: rgba length must be w*h*4"
        );

        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("akapen-render background texture"),
            size: wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: crate::renderer::OFFSCREEN_FORMAT,
            usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
            view_formats: &[],
        });

        queue.write_texture(
            wgpu::TexelCopyTextureInfo {
                texture: &texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            rgba,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(w * 4),
                rows_per_image: Some(h),
            },
            wgpu::Extent3d {
                width: w,
                height: h,
                depth_or_array_layers: 1,
            },
        );

        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        BackgroundTexture {
            texture,
            view,
            width: w,
            height: h,
        }
    }

    /// Draws `background` into `target_view` (an `output_w x output_h`
    /// render target, any format this pipeline was built for) positioned by
    /// `view`. Clears the target to transparent black first — this is the
    /// bottom-most layer, so there is nothing to preserve underneath yet
    /// (see module doc comment re: Phase c).
    #[allow(clippy::too_many_arguments)]
    pub fn render(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_view: &wgpu::TextureView,
        output_w: u32,
        output_h: u32,
        background: &BackgroundTexture,
        view: &ViewTransform,
    ) {
        self.render_texture(
            device,
            queue,
            target_view,
            output_w,
            output_h,
            &background.view,
            view,
            wgpu::LoadOp::Clear(wgpu::Color::TRANSPARENT),
        );
    }

    /// Generalization of [`Self::render`]: draws an arbitrary
    /// premultiplied-alpha RGBA texture (`texture_view`, same format this
    /// pipeline was built for) into `target_view`, positioned by `view`,
    /// with a caller-supplied `load` op instead of always clearing.
    ///
    /// This is what lets Phase d's `baked_tex` (spec §6.2 point 3, see
    /// [`crate::bake`]) reuse this same view-transform textured-quad
    /// pipeline as the background layer, drawn *over* whatever is already in
    /// `target_view` (`wgpu::LoadOp::Load`) rather than clearing it away —
    /// the per-frame composite order is background (`Clear` via
    /// [`Self::render`]) → `baked_tex` (`Load`, this method) → wet ink
    /// (`Load`, [`crate::stroke::StrokePipeline::draw`]).
    #[allow(clippy::too_many_arguments)]
    pub fn render_texture(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        target_view: &wgpu::TextureView,
        output_w: u32,
        output_h: u32,
        texture_view: &wgpu::TextureView,
        view: &ViewTransform,
        load: wgpu::LoadOp<wgpu::Color>,
    ) {
        let bind_group =
            self.prepare_texture_draw(device, queue, output_w, output_h, texture_view, view);

        let mut encoder =
            device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("akapen-render background pass"),
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
            self.record_texture_draw(&mut pass, &bind_group);
        }
        queue.submit(Some(encoder.finish()));
    }

    /// The uniform-buffer + bind-group half of [`Self::render_texture`],
    /// split out so a caller that wants to draw several textured quads
    /// (background, `baked_tex`, ...) into the *same* render pass — one
    /// encoder, one `queue.submit` — can prepare every draw's resources up
    /// front and then record them all before ending the pass (used by
    /// [`crate::bake::composite_frame`], review perf finding: the per-frame
    /// composite used to be 3 submits with a fresh uniform buffer each,
    /// where 1 encoder / 1 submit suffices).
    ///
    /// The returned [`wgpu::BindGroup`] owns the uniform buffer it was built
    /// from (via `as_entire_binding`), so the caller only needs to keep the
    /// bind group alive (not a separate buffer handle) until the pass that
    /// consumes it (via [`Self::record_texture_draw`]) has ended.
    pub fn prepare_texture_draw(
        &self,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        output_w: u32,
        output_h: u32,
        texture_view: &wgpu::TextureView,
        view: &ViewTransform,
    ) -> wgpu::BindGroup {
        let uniforms = Uniforms {
            transform: uv_to_clip_matrix(view, output_w, output_h),
        };
        let uniform_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("akapen-render background uniform buffer"),
            size: core::mem::size_of::<Uniforms>() as u64,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.write_buffer(&uniform_buffer, 0, bytemuck::bytes_of(&uniforms));

        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("akapen-render background bind group"),
            layout: &self.bind_group_layout,
            entries: &[
                wgpu::BindGroupEntry {
                    binding: 0,
                    resource: uniform_buffer.as_entire_binding(),
                },
                wgpu::BindGroupEntry {
                    binding: 1,
                    resource: wgpu::BindingResource::TextureView(texture_view),
                },
                wgpu::BindGroupEntry {
                    binding: 2,
                    resource: wgpu::BindingResource::Sampler(&self.sampler),
                },
            ],
        })
    }

    /// Records one [`Self::prepare_texture_draw`]-prepared textured-quad
    /// draw into an already-open render pass. Does not clear/load or submit
    /// — that is the open pass's/its caller's responsibility, so several
    /// calls (e.g. background then `baked_tex`) can share one pass.
    pub fn record_texture_draw<'pass>(
        &'pass self,
        pass: &mut wgpu::RenderPass<'pass>,
        bind_group: &'pass wgpu::BindGroup,
    ) {
        pass.set_pipeline(&self.pipeline);
        pass.set_bind_group(0, bind_group, &[]);
        pass.draw(0..6, 0..1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::renderer::{OffscreenTarget, OFFSCREEN_FORMAT};

    /// Deterministic test image: every pixel's color is a function of its
    /// own (x, y), so any mislocated/rotated/mirrored sample shows up as a
    /// wrong color rather than accidentally matching a neighbor.
    fn checkerboard(w: u32, h: u32) -> Vec<u8> {
        let mut out = Vec::with_capacity((w * h * 4) as usize);
        for y in 0..h {
            for x in 0..w {
                out.push((x * 7 % 256) as u8);
                out.push((y * 11 % 256) as u8);
                out.push(((x + y) * 13 % 256) as u8);
                out.push(255);
            }
        }
        out
    }

    fn identity_view(size: f64) -> ViewTransform {
        ViewTransform {
            center_x: size / 2.0,
            center_y: size / 2.0,
            scale_x: 1.0,
            scale_y: 1.0,
            rotation_deg: 0.0,
            buffer_w: size,
            buffer_h: size,
        }
    }

    #[test]
    fn identity_view_transform_readback_matches_source_rgba() {
        let Some(renderer) = crate::test_support::try_headless_renderer(
            "identity_view_transform_readback_matches_source_rgba",
        ) else {
            return;
        };

        let size = 32u32;
        let source = checkerboard(size, size);

        let bg_pipeline = BackgroundPipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let background =
            bg_pipeline.set_background(&renderer.device, &renderer.queue, &source, size, size);
        let target = OffscreenTarget::new(&renderer.device, size, size);

        let view = identity_view(size as f64);
        bg_pipeline.render(
            &renderer.device,
            &renderer.queue,
            &target.view,
            size,
            size,
            &background,
            &view,
        );

        let readback = target.read_rgba(&renderer.device, &renderer.queue);
        assert_eq!(readback.len(), source.len());

        // Allow a few tonal steps of slack (spec AC: "許容誤差±数階調")
        // even though Nearest sampling + a pixel-exact identity transform
        // should make this an exact match in practice; the tolerance guards
        // against backend-specific rounding rather than masking a real bug.
        const TOLERANCE: i32 = 4;
        for i in 0..source.len() {
            let diff = (source[i] as i32 - readback[i] as i32).abs();
            assert!(
                diff <= TOLERANCE,
                "byte {i} differs by {diff} (expected {}, got {})",
                source[i],
                readback[i]
            );
        }
    }

    #[test]
    fn zoomed_and_panned_view_places_background_at_expected_screen_points() {
        let Some(renderer) = crate::test_support::try_headless_renderer(
            "zoomed_and_panned_view_places_background_at_expected_screen_points",
        ) else {
            return;
        };

        let buffer_size = 32u32;
        let source = checkerboard(buffer_size, buffer_size);
        let output_size = 64u32; // output != buffer size, so zoom/pan is meaningful.

        let bg_pipeline = BackgroundPipeline::new(&renderer.device, OFFSCREEN_FORMAT);
        let background = bg_pipeline.set_background(
            &renderer.device,
            &renderer.queue,
            &source,
            buffer_size,
            buffer_size,
        );
        let target = OffscreenTarget::new(&renderer.device, output_size, output_size);

        // 2x zoom, panned so the buffer's top-left corner (image pixel
        // (0,0)) lands at output screen point (8, 8) instead of wherever an
        // untranslated 2x-zoomed-and-centered view would put it. Solved from
        // `screen = center - scale * buffer/2` (image_to_screen at the
        // origin, rotation=0): `center = (8,8) + scale * buffer/2`.
        let center = 8.0 + 2.0 * (buffer_size as f64 / 2.0);
        let view = ViewTransform {
            center_x: center,
            center_y: center,
            scale_x: 2.0,
            scale_y: 2.0,
            rotation_deg: 0.0,
            buffer_w: buffer_size as f64,
            buffer_h: buffer_size as f64,
        };

        bg_pipeline.render(
            &renderer.device,
            &renderer.queue,
            &target.view,
            output_size,
            output_size,
            &background,
            &view,
        );

        let readback = target.read_rgba(&renderer.device, &renderer.queue);
        let pixel_at = |x: u32, y: u32| -> [u8; 4] {
            let i = ((y * output_size + x) * 4) as usize;
            [
                readback[i],
                readback[i + 1],
                readback[i + 2],
                readback[i + 3],
            ]
        };
        let source_pixel_at = |x: u32, y: u32| -> [u8; 4] {
            let i = ((y * buffer_size + x) * 4) as usize;
            [source[i], source[i + 1], source[i + 2], source[i + 3]]
        };

        // Cross-check a handful of representative image points against the
        // akapen_core oracle (image_to_screen), rather than re-deriving the
        // expected screen position by hand (spec AC: "代表点で読み戻し検証").
        for &(ix, iy) in &[(0u32, 0u32), (16, 16), (31, 0), (0, 31)] {
            let screen = view.image_to_screen(akapen_core::coord::Point2 {
                x: ix as f64 + 0.5, // sample the pixel *center*, not its corner
                y: iy as f64 + 0.5,
            });
            let sx = screen.x.floor() as i64;
            let sy = screen.y.floor() as i64;
            if sx < 0 || sy < 0 || sx >= output_size as i64 || sy >= output_size as i64 {
                continue; // this representative point landed outside the output; skip it.
            }
            let got = pixel_at(sx as u32, sy as u32);
            let expected = source_pixel_at(ix, iy);
            const TOLERANCE: i32 = 4;
            for c in 0..4 {
                let diff = (got[c] as i32 - expected[c] as i32).abs();
                assert!(
                    diff <= TOLERANCE,
                    "image point ({ix},{iy}) -> screen ({sx},{sy}): channel {c} expected {}, got {} (diff {diff})",
                    expected[c],
                    got[c]
                );
            }
        }
    }
}
