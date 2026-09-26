struct U { vp: mat4x4f, eye: vec4f, hover: u32, sel: u32, per: u32, pad: u32, colors: array<vec4f, 32> };
@group(0) @binding(0) var<uniform> u: U;
struct VO { @builtin(position) pos: vec4f, @location(0) world: vec3f, @location(1) @interpolate(flat) id: u32 };
@vertex fn vs(@location(0) p: vec3f, @location(1) id: u32) -> VO {
    var o: VO; o.pos = u.vp * vec4f(p, 1.0); o.world = p; o.id = id; return o;
}
@fragment fn fs_color(i: VO) -> @location(0) vec4f {
    let n = normalize(cross(dpdx(i.world), dpdy(i.world)));
    let d = abs(dot(n, normalize(u.eye.xyz - i.world)));
    let c = u.colors[(i.id - 1u) % u.per];
    var rgb = c.rgb * (0.25 + 0.75 * d);
    if (i.id == u.hover) { rgb = mix(rgb, vec3f(0.2, 0.9, 1.0), 0.6); }
    if (i.id == u.sel) { rgb = mix(rgb, vec3f(1.0, 0.3, 1.0), 0.6); }
    return vec4f(rgb, c.a);
}
@fragment fn fs_pick(i: VO) -> @location(0) u32 { return i.id; }
