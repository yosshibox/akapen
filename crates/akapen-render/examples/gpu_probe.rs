//! Standalone, single-backend, stage-by-stage GPU bring-up probe.
//!
//! Purpose: de-risking a native STATUS_ACCESS_VIOLATION (0xc0000005) hit on
//! Windows (Intel HD 620) the moment `cargo test -p akapen-render` reaches
//! its first GPU test. The crate's own tests always build the `Instance`
//! with `Backends::default()` (== `Backends::all()`, since
//! `InstanceDescriptor::new_without_display_handle()` does *not* apply
//! `WGPU_BACKEND` — only `*_from_env()` variants do), so every backend gets
//! enumerated together regardless of the env var. This probe instead takes
//! the backend to test as an explicit CLI argument and builds
//! `Backends` for exactly one backend in code, so each run only ever
//! touches one driver stack.
//!
//! Every stage below prints (and flushes) a `[probe] stage=... ` line
//! *before* the risky call and an `..._ok` line *after* it returns, so if
//! the process dies with a native access violation, the last flushed line
//! pinpoints which stage never returned.
//!
//! Usage: `cargo run -p akapen-render --example gpu_probe -- <dx12|vulkan|gl|all>`

use std::io::Write as _;

fn flushed(msg: &str) {
    eprintln!("[probe] {msg}");
    let _ = std::io::stderr().flush();
    // Also mirror to stdout in case stderr is line-buffered differently
    // under whatever's capturing this process on the Windows side.
    println!("[probe] {msg}");
    let _ = std::io::stdout().flush();
}

fn backend_from_arg(s: &str) -> wgpu::Backends {
    match s {
        "dx12" => wgpu::Backends::DX12,
        "vulkan" => wgpu::Backends::VULKAN,
        "gl" => wgpu::Backends::GL,
        "all" => wgpu::Backends::all(),
        other => panic!("unknown backend arg '{other}' (expected dx12|vulkan|gl|all)"),
    }
}

fn main() {
    let arg = std::env::args().nth(1).unwrap_or_else(|| "all".to_string());
    let backends = backend_from_arg(&arg);
    flushed(&format!(
        "start backend_arg={arg} backends={backends:?} pid={}",
        std::process::id()
    ));

    pollster::block_on(run(backends));

    flushed("ALL_STAGES_OK — process did not crash");
}

async fn run(backends: wgpu::Backends) {
    flushed("stage=instance_new");
    let instance = wgpu::Instance::new(wgpu::InstanceDescriptor {
        backends,
        ..wgpu::InstanceDescriptor::new_without_display_handle()
    });
    flushed("stage=instance_new_ok");

    flushed("stage=enumerate_adapters");
    let adapters = instance.enumerate_adapters(backends).await;
    flushed(&format!(
        "stage=enumerate_adapters_ok count={}",
        adapters.len()
    ));
    for (i, a) in adapters.iter().enumerate() {
        let info = a.get_info();
        flushed(&format!("  adapter[{i}] = {info:?}"));
    }

    flushed("stage=request_adapter");
    let adapter = instance
        .request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::default(),
            force_fallback_adapter: false,
            compatible_surface: None,
            apply_limit_buckets: false,
        })
        .await
        .expect("request_adapter returned no adapter");
    flushed(&format!(
        "stage=request_adapter_ok info={:?}",
        adapter.get_info()
    ));

    flushed("stage=request_device");
    let (device, queue) = adapter
        .request_device(&wgpu::DeviceDescriptor {
            label: Some("gpu_probe device"),
            ..Default::default()
        })
        .await
        .expect("request_device failed");
    flushed("stage=request_device_ok");

    // ---- Stage group A: plain (no-texture) pipeline, matches
    // renderer.rs's `triangle_renders_to_offscreen_texture` test shape ----
    flushed("stage=triangle_shader_compile");
    let shader = device.create_shader_module(wgpu::include_wgsl!("../src/shaders/triangle.wgsl"));
    flushed("stage=triangle_shader_compile_ok");

    flushed("stage=triangle_pipeline_layout");
    let pipeline_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
        label: Some("gpu_probe triangle pipeline layout"),
        bind_group_layouts: &[],
        immediate_size: 0,
    });
    flushed("stage=triangle_pipeline_layout_ok");

    flushed("stage=triangle_render_pipeline");
    let pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
        label: Some("gpu_probe triangle pipeline"),
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
                format: akapen_render::OFFSCREEN_FORMAT,
                blend: None,
                write_mask: wgpu::ColorWrites::ALL,
            })],
        }),
        multiview_mask: None,
        cache: None,
    });
    flushed("stage=triangle_render_pipeline_ok");

    flushed("stage=triangle_offscreen_target_new");
    let target = akapen_render::OffscreenTarget::new(&device, 64, 64);
    flushed("stage=triangle_offscreen_target_new_ok");

    flushed("stage=triangle_render_pass");
    let mut encoder =
        device.create_command_encoder(&wgpu::CommandEncoderDescriptor { label: None });
    {
        let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: Some("gpu_probe triangle pass"),
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
    flushed("stage=triangle_render_pass_ok");

    flushed("stage=triangle_submit");
    queue.submit(Some(encoder.finish()));
    flushed("stage=triangle_submit_ok");

    flushed("stage=triangle_readback");
    let pixels = target.read_rgba(&device, &queue);
    flushed(&format!(
        "stage=triangle_readback_ok len={} center={:?}",
        pixels.len(),
        {
            let i = ((32 * target.width + 32) * 4) as usize;
            [pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]]
        }
    ));

    // ---- Stage group B: textured pipeline, matches background.rs's
    // failing `identity_view_transform_readback_matches_source_rgba` test ----
    flushed("stage=bg_pipeline_new");
    let bg_pipeline =
        akapen_render::BackgroundPipeline::new(&device, akapen_render::OFFSCREEN_FORMAT);
    flushed("stage=bg_pipeline_new_ok");

    let size = 32u32;
    let source = checkerboard(size, size);

    flushed("stage=bg_set_background_upload");
    let background = bg_pipeline.set_background(&device, &queue, &source, size, size);
    flushed("stage=bg_set_background_upload_ok");

    flushed("stage=bg_offscreen_target_new");
    let bg_target = akapen_render::OffscreenTarget::new(&device, size, size);
    flushed("stage=bg_offscreen_target_new_ok");

    let view = identity_view(size as f64);

    flushed("stage=bg_render");
    bg_pipeline.render(
        &device,
        &queue,
        &bg_target.view,
        size,
        size,
        &background,
        &view,
    );
    flushed("stage=bg_render_ok");

    flushed("stage=bg_readback");
    let readback = bg_target.read_rgba(&device, &queue);
    flushed(&format!("stage=bg_readback_ok len={}", readback.len()));

    const TOLERANCE: i32 = 4;
    let mut worst_diff = 0i32;
    for i in 0..source.len() {
        let diff = (source[i] as i32 - readback[i] as i32).abs();
        worst_diff = worst_diff.max(diff);
    }
    flushed(&format!(
        "stage=bg_parity_check worst_diff={worst_diff} tolerance={TOLERANCE} pass={}",
        worst_diff <= TOLERANCE
    ));
}

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

fn identity_view(size: f64) -> akapen_core::coord::ViewTransform {
    akapen_core::coord::ViewTransform {
        center_x: size / 2.0,
        center_y: size / 2.0,
        scale_x: 1.0,
        scale_y: 1.0,
        rotation_deg: 0.0,
        buffer_w: size,
        buffer_h: size,
    }
}
