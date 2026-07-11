// Phase a smoke-test shader: a single hard-coded, solid-red triangle with no
// vertex buffer (positions come straight from vertex_index — the "hello
// triangle" this crate's headless offscreen test renders and reads back).
// Not used past Phase a; the real geometry path is
// akapen_core::tessellate::tessellate_stroke (Phase c+).

@vertex
fn vs_main(@builtin(vertex_index) vertex_index: u32) -> @builtin(position) vec4<f32> {
    var positions = array<vec2<f32>, 3>(
        vec2<f32>(0.0, 0.6),
        vec2<f32>(-0.6, -0.6),
        vec2<f32>(0.6, -0.6),
    );
    return vec4<f32>(positions[vertex_index], 0.0, 1.0);
}

@fragment
fn fs_main() -> @location(0) vec4<f32> {
    return vec4<f32>(1.0, 0.0, 0.0, 1.0);
}
