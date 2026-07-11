// Phase c+d: draws a tessellated stroke (akapen_core::tessellate::Vertex, a
// flat TriangleList in image-pixel space) as a solid premultiplied-alpha
// color, transformed by akapen_render::transform::image_to_clip_matrix (the
// same view-transform math background.wgsl uses via uv_to_clip_matrix — one
// oracle, spec §6.2). No per-fragment antialiasing: the tessellated geometry
// is a hard-edged triangle mesh (see akapen_core::tessellate module doc), so
// every covered fragment is full coverage; `uniforms.color` already carries
// the constant premultiplied-alpha output for the whole draw (computed once
// on the CPU side from the stroke's color/opacity, mirroring
// akapen_core::raster::bake_stroke's `base_alpha`).
//
// Pen vs eraser is *not* a shader branch: it is two different
// akapen_render::stroke::StrokePipeline pipelines with different
// (fixed-function) blend states over the identical shader output — pen uses
// premultiplied source-over, eraser uses destination-out (see
// StrokePipeline::new doc comment).

struct Uniforms {
    transform: mat4x4<f32>,
    color: vec4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

@vertex
fn vs_main(@location(0) pos: vec2<f32>) -> @builtin(position) vec4<f32> {
    return uniforms.transform * vec4<f32>(pos, 0.0, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return uniforms.color;
}
