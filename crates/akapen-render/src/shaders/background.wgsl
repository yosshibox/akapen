// Phase b: displays the background image texture through a view-transform
// uniform (see akapen_render::transform::uv_to_clip_matrix). A hard-coded
// unit UV quad (two triangles, no vertex buffer) is transformed entirely by
// `uniforms.transform`, which already folds in image_to_screen and the
// screen->clip viewport map — this shader has no view-transform math of its
// own, only the matrix multiply, so akapen_core::coord stays the single
// oracle (spec §6.2).

struct Uniforms {
    transform: mat4x4<f32>,
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;
@group(0) @binding(1) var background_texture: texture_2d<f32>;
@group(0) @binding(2) var background_sampler: sampler;

struct VertexOutput {
    @builtin(position) clip_position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vertex_index: u32) -> VertexOutput {
    var uvs = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 1.0),
    );
    let uv = uvs[vertex_index];

    var out: VertexOutput;
    out.clip_position = uniforms.transform * vec4<f32>(uv, 0.0, 1.0);
    out.uv = uv;
    return out;
}

@fragment
fn fs_main(in: VertexOutput) -> @location(0) vec4<f32> {
    return textureSample(background_texture, background_sampler, in.uv);
}
