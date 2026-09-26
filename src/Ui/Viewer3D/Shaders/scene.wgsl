// brief-em3d-28 R-em3d28-1c — THE one shader source for every 3D-view backend. tools/ShaderGen
// cross-compiles it offline to scene.metal (Metal), scene.hlsl (D3D11) and scene.spv (Vulkan); each
// generated file records this file's SHA-256, and Viewer3DShaderTests fails when they disagree.
// Edit this file, then regenerate:  tools/ShaderGen  ->  shadergen src/Ui/Viewer3D/Shaders/scene.wgsl src/Ui/Viewer3D/Shaders
//
// The uniform block is Scene3DFramePlan's: vp, eye, clip, hover, selection, flags (112 bytes).
// A vertex is Scene3DVertex: position, object id, RGBA8 colour (20 bytes).

struct U {
    vp: mat4x4f,
    eye: vec4f,
    clip: vec4f,
    hover: u32,
    sel: u32,
    flags: u32,
    pad: u32,
};
@group(0) @binding(0) var<uniform> u: U;

struct VI {
    @location(0) p: vec3f,
    @location(1) id: u32,
    @location(2) col: vec4f,
};

struct VO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
    @location(1) @interpolate(flat) id: u32,
    @location(2) col: vec4f,
};

@vertex fn vs(v: VI) -> VO {
    var o: VO;
    o.pos = u.vp * vec4f(v.p, 1.0);
    o.world = v.p;
    o.id = v.id;
    o.col = v.col;
    return o;
}

// Flag bits: 1 = clip on, 2 = draw back faces flat (the cut solid's cap).
fn clipped(w: vec3f) -> bool {
    return (u.flags & 1u) != 0u && dot(u.clip.xyz, w) + u.clip.w > 0.0;
}

fn highlight(rgb: vec3f, id: u32) -> vec3f {
    var c = rgb;
    if (id != 0u && id == u.hover) { c = mix(c, vec3f(0.2, 0.9, 1.0), 0.45); }
    if (id != 0u && id == u.sel) { c = mix(c, vec3f(1.0, 0.35, 1.0), 0.55); }
    return c;
}

@fragment fn fs_color(i: VO, @builtin(front_facing) front: bool) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    let n = normalize(cross(dpdx(i.world), dpdy(i.world)));
    let d = abs(dot(n, normalize(u.eye.xyz - i.world)));
    var rgb = i.col.rgb * (0.3 + 0.7 * d);
    if (!front && (u.flags & 2u) != 0u) { rgb = i.col.rgb * 0.8; }
    return vec4f(highlight(rgb, i.id), i.col.a);
}

@fragment fn fs_line(i: VO) -> @location(0) vec4f {
    return vec4f(i.col.rgb, 1.0);
}

struct PickOut {
    @location(0) id: u32,
    @location(1) world: vec4f,
};

@fragment fn fs_pick(i: VO) -> PickOut {
    if (clipped(i.world)) { discard; }
    var o: PickOut;
    o.id = i.id;
    o.world = vec4f(i.world, 1.0);
    return o;
}
