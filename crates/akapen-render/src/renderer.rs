//! wgpu device/adapter/queue bring-up and the headless offscreen render
//! target used by tests (spec §7.4-6 "GPU 描画(wgpu: Metal/D3D12)").
//!
//! Low-latency swapchain settings (spec §6.2 item 2: `desired_maximum_frame_
//! latency = 1`相当、present_mode既定Fifo) are centralized in
//! [`surface_configuration`] as constants/arguments rather than inlined at
//! each call site, so a later Mailbox/Immediate comparison run is a one-line
//! change (spec instruction: "後でMailbox/Immediate比較できるよう定数/引数
//! 化").

use crate::error::RendererError;

/// Texture format used for both the on-screen swapchain (once wired) and
/// every offscreen render target/texture in this crate. Fixed to a
/// non-sRGB, 8-bit-per-channel format so CPU-side test expectations compare
/// 1:1 against GPU readback bytes without an sRGB transfer-function step
/// (spec instruction: "sRGB差でハマらないよう非sRGBで統一").
pub const OFFSCREEN_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Rgba8Unorm;

/// Default present mode. `Fifo` is the only mode wgpu guarantees every
/// backend supports; pass a different [`wgpu::PresentMode`] to
/// [`surface_configuration`] to compare against `Mailbox`/`Immediate`.
pub const DEFAULT_PRESENT_MODE: wgpu::PresentMode = wgpu::PresentMode::Fifo;

/// Default `desired_maximum_frame_latency`: minimizes latency at the cost of
/// CPU/GPU parallelism (spec §6.2 item 2), matching the DXGI
/// `frame latency 1` / waitable-swapchain discipline the spec calls for on
/// Windows.
pub const DEFAULT_MAX_FRAME_LATENCY: u32 = 1;

/// Builds a [`wgpu::SurfaceConfiguration`] using this crate's low-latency
/// defaults, parameterized on `present_mode` so callers can swap in
/// `Mailbox`/`Immediate` for comparison runs (spec §6.2) without duplicating
/// the rest of the configuration.
pub fn surface_configuration(
    format: wgpu::TextureFormat,
    width: u32,
    height: u32,
    present_mode: wgpu::PresentMode,
) -> wgpu::SurfaceConfiguration {
    wgpu::SurfaceConfiguration {
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
        format,
        color_space: wgpu::SurfaceColorSpace::Auto,
        width,
        height,
        present_mode,
        desired_maximum_frame_latency: DEFAULT_MAX_FRAME_LATENCY,
        alpha_mode: wgpu::CompositeAlphaMode::Auto,
        view_formats: vec![],
    }
}

/// Owns the wgpu instance/adapter/device/queue. One per app window (or one
/// for headless tests); cheap enough to not bother pooling.
pub struct Renderer {
    pub instance: wgpu::Instance,
    pub adapter: wgpu::Adapter,
    pub device: wgpu::Device,
    pub queue: wgpu::Queue,
}

impl Renderer {
    /// Creates a `Renderer` with no compatible surface requirement — usable
    /// for offscreen/headless rendering (tests, and the future bake-texture
    /// path which never presents to a window). Real window-attached
    /// renderers should instead create the `Instance` first, build a
    /// `Surface` via [`crate::surface::create`], and request an adapter
    /// compatible with it; that entry point lands with the Windows/mac FFI
    /// wiring (Phase e) once there is a real window handle to test against.
    pub async fn new_headless() -> Result<Self, RendererError> {
        let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_without_display_handle());
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::default(),
                force_fallback_adapter: false,
                compatible_surface: None,
                apply_limit_buckets: false,
            })
            .await
            .map_err(|_| RendererError::NoAdapter)?;
        let (device, queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("akapen-render headless device"),
                ..Default::default()
            })
            .await
            .map_err(RendererError::RequestDevice)?;
        Ok(Self {
            instance,
            adapter,
            device,
            queue,
        })
    }

    /// Blocking wrapper over [`Self::new_headless`] for non-async call sites
    /// (tests, and CLI-style tools that don't otherwise need an executor).
    pub fn new_headless_blocking() -> Result<Self, RendererError> {
        pollster::block_on(Self::new_headless())
    }
}

/// An offscreen `Rgba8Unorm` render target plus the readback machinery
/// tests need (spec Phase a AC: "オフスクリーンテクスチャへ描画→
/// copy_texture_to_bufferで読み戻し→期待画素").
pub struct OffscreenTarget {
    pub texture: wgpu::Texture,
    pub view: wgpu::TextureView,
    pub width: u32,
    pub height: u32,
}

impl OffscreenTarget {
    pub fn new(device: &wgpu::Device, width: u32, height: u32) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some("akapen-render offscreen target"),
            size: wgpu::Extent3d {
                width,
                height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: OFFSCREEN_FORMAT,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        Self {
            texture,
            view,
            width,
            height,
        }
    }

    /// Reads the target back to a tightly-packed (no row padding) RGBA8
    /// buffer, `width * height * 4` bytes, row-major top-to-bottom —
    /// matching the layout tests compare against source image bytes with.
    ///
    /// Blocks the calling thread until the GPU work + map completes
    /// (`PollType::wait_indefinitely`); fine for tests, not for a render
    /// loop.
    pub fn read_rgba(&self, device: &wgpu::Device, queue: &wgpu::Queue) -> Vec<u8> {
        read_rgba_from_texture(device, queue, &self.texture, self.width, self.height)
    }
}

/// Reads a `width x height` [`OFFSCREEN_FORMAT`]-format texture back to a
/// tightly-packed (no row padding) RGBA8 buffer, row-major top-to-bottom.
/// Shared readback machinery behind [`OffscreenTarget::read_rgba`] and
/// [`crate::bake::BakedTexture::read_rgba`] (Phase d) — both are
/// `Rgba8Unorm` render-attachment textures that need the same padded-copy
/// dance, just with a different owning struct.
///
/// Blocks the calling thread until the GPU work + map completes; fine for
/// tests, not for a render loop.
pub fn read_rgba_from_texture(
    device: &wgpu::Device,
    queue: &wgpu::Queue,
    texture: &wgpu::Texture,
    width: u32,
    height: u32,
) -> Vec<u8> {
    let bytes_per_pixel = 4u32;
    let unpadded_bytes_per_row = width * bytes_per_pixel;
    let align = wgpu::COPY_BYTES_PER_ROW_ALIGNMENT;
    let padded_bytes_per_row = unpadded_bytes_per_row.div_ceil(align) * align;

    let buffer_size = (padded_bytes_per_row as u64) * (height as u64);
    let readback_buffer = device.create_buffer(&wgpu::BufferDescriptor {
        label: Some("akapen-render readback buffer"),
        size: buffer_size,
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });

    let mut encoder =
        device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
    encoder.copy_texture_to_buffer(
        wgpu::TexelCopyTextureInfo {
            texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::TexelCopyBufferInfo {
            buffer: &readback_buffer,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(padded_bytes_per_row),
                rows_per_image: Some(height),
            },
        },
        wgpu::Extent3d {
            width,
            height,
            depth_or_array_layers: 1,
        },
    );
    queue.submit(Some(encoder.finish()));

    let slice = readback_buffer.slice(..);
    let (tx, rx) = std::sync::mpsc::channel();
    slice.map_async(wgpu::MapMode::Read, move |result| {
        let _ = tx.send(result);
    });
    device
        .poll(wgpu::PollType::wait_indefinitely())
        .expect("device poll failed while waiting for readback map");
    rx.recv()
        .expect("map_async callback never fired")
        .expect("failed to map readback buffer");

    let padded: Vec<u8> = slice
        .get_mapped_range()
        .expect("buffer was just confirmed mapped")
        .to_vec();
    readback_buffer.unmap();

    // Strip row padding.
    let mut out = Vec::with_capacity((unpadded_bytes_per_row as usize) * height as usize);
    for row in 0..height as usize {
        let start = row * padded_bytes_per_row as usize;
        let end = start + unpadded_bytes_per_row as usize;
        out.extend_from_slice(&padded[start..end]);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// GPU-touching test. Per spec instruction for Phase a: skip cleanly
    /// (not a failure) with a printed reason when no adapter is available —
    /// e.g. CI without a GPU or software rasterizer — and run the real
    /// assertions when one is. This is *not* the static `#[ignore]`
    /// attribute (Rust has no way to apply that conditionally at runtime);
    /// see the Phase a+b report for why this reading was chosen. Renders a
    /// solid-color triangle over a cleared background and checks a pixel
    /// inside it against one clearly outside it.
    #[test]
    fn triangle_renders_to_offscreen_texture() {
        let Some(renderer) =
            crate::test_support::try_headless_renderer("triangle_renders_to_offscreen_texture")
        else {
            return;
        };

        let target = OffscreenTarget::new(&renderer.device, 64, 64);

        let shader = renderer
            .device
            .create_shader_module(wgpu::include_wgsl!("shaders/triangle.wgsl"));
        let pipeline_layout =
            renderer
                .device
                .create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
                    label: Some("triangle pipeline layout"),
                    bind_group_layouts: &[],
                    immediate_size: 0,
                });
        let pipeline = renderer
            .device
            .create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some("triangle pipeline"),
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
                        format: OFFSCREEN_FORMAT,
                        blend: None,
                        write_mask: wgpu::ColorWrites::ALL,
                    })],
                }),
                multiview_mask: None,
                cache: None,
            });

        let mut encoder = renderer
            .device
            .create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("triangle pass"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &target.view,
                    depth_slice: None,
                    resolve_target: None,
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color {
                            r: 0.0,
                            g: 0.0,
                            b: 0.0,
                            a: 1.0,
                        }),
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: None,
                timestamp_writes: None,
                occlusion_query_set: None,
                multiview_mask: None,
            });
            pass.set_pipeline(&pipeline);
            pass.draw(0..3, 0..1);
        }
        renderer.queue.submit(Some(encoder.finish()));

        let pixels = target.read_rgba(&renderer.device, &renderer.queue);
        let pixel_at = |x: u32, y: u32| -> [u8; 4] {
            let i = ((y * target.width + x) * 4) as usize;
            [pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]]
        };

        // Center of the 64x64 target is inside the triangle (see
        // shaders/triangle.wgsl: apex at clip y=0.6, base at y=-0.6,
        // comfortably straddling the origin) -> solid red.
        let center = pixel_at(32, 32);
        assert_eq!(
            center,
            [255, 0, 0, 255],
            "center pixel should be the triangle's red"
        );

        // Top-left corner is well outside the triangle -> the clear color.
        let corner = pixel_at(1, 1);
        assert_eq!(
            corner,
            [0, 0, 0, 255],
            "corner pixel should be the clear color"
        );
    }
}
