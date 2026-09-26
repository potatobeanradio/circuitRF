// brief-em3d-28 R-em3d28-1c — THE one shader source for every 3D-view backend. tools/ShaderGen
// cross-compiles it offline to scene.metal (Metal), scene.hlsl (D3D11) and scene.spv (Vulkan); each
// generated file records this file's SHA-256, and Viewer3DFrameGateTests.Gate1b fails when they disagree.
// Edit this file, then regenerate:  tools/ShaderGen  ->  shadergen src/Ui/Viewer3D/Shaders/scene.wgsl src/Ui/Viewer3D/Shaders
//
// The uniform block is Scene3DFramePlan's: vp, eye, clip, hover, selection, flags, then brief 29's field
// block (FieldUniforms: phase, range, mode, dB, the colour map's stops) — 400 bytes.
// A vertex is Scene3DVertex: position, object id, RGBA8 colour (20 bytes); a FIELD vertex is FieldVertex:
// position, the value's real part, its imaginary part (36 bytes).

struct U {
    vp: mat4x4f,
    eye: vec4f,
    clip: vec4f,
    hover: u32,
    sel: u32,
    flags: u32,
    pad: u32,
    // x cos φ, y sin φ, z range lo, w range hi
    fphase: vec4f,
    // x mode (0 |v| of a vector, 1 |Re{v e^jφ}|, 2 a real scalar, 3 Re{v e^jφ} of a scalar, 4 |v| of a
    // scalar), y dB, z the colour map's stop count
    fmode: vec4f,
    // (t, r, g, b) per stop
    stops: array<vec4f, 16>,
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

// ── brief-em3d-29: the field pass ──────────────────────────────────────────────────────────────────
// The value is computed HERE from the vertex's real and imaginary parts and the phase uniform, so an
// animated frame changes one uniform and uploads nothing. Unshaded: the colour is the datum. A value
// outside the range is drawn in the end colour (the clamp), never transparent.

struct FVI {
    @location(0) p: vec3f,
    @location(1) re: vec3f,
    @location(2) im: vec3f,
};

struct FVO {
    @builtin(position) pos: vec4f,
    @location(0) world: vec3f,
    @location(1) re: vec3f,
    @location(2) im: vec3f,
};

@vertex fn vs_field(v: FVI) -> FVO {
    var o: FVO;
    o.pos = u.vp * vec4f(v.p, 1.0);
    o.world = v.p;
    o.re = v.re;
    o.im = v.im;
    return o;
}

fn field_value(re: vec3f, im: vec3f) -> f32 {
    let c = u.fphase.x;
    let s = u.fphase.y;
    let mode = u32(u.fmode.x + 0.5);
    if (mode == 0u) { return sqrt(dot(re, re) + dot(im, im)); }
    if (mode == 1u) { return length(re * c - im * s); }
    if (mode == 2u) { return re.x; }
    if (mode == 3u) { return re.x * c - im.x * s; }
    return sqrt(re.x * re.x + im.x * im.x);
}

fn colour_map(t: f32) -> vec3f {
    let n = u32(u.fmode.z + 0.5);
    var rgb = u.stops[0].yzw;
    for (var k = 1u; k < n; k = k + 1u) {
        let a = u.stops[k - 1u];
        let b = u.stops[k];
        if (t <= b.x || k == n - 1u) {
            let w = clamp((t - a.x) / max(b.x - a.x, 1e-6), 0.0, 1.0);
            rgb = mix(a.yzw, b.yzw, w);
            break;
        }
    }
    return rgb;
}

@fragment fn fs_field(i: FVO) -> @location(0) vec4f {
    if (clipped(i.world)) { discard; }
    var v = field_value(i.re, i.im);
    if (u.fmode.y > 0.5) { v = 20.0 * 0.30102999566 * log2(max(abs(v), 1e-30)); }
    let t = clamp((v - u.fphase.z) / max(u.fphase.w - u.fphase.z, 1e-30), 0.0, 1.0);
    return vec4f(colour_map(t), 1.0);
}
