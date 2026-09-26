// language: metal1.0
#include <metal_stdlib>
#include <simd/simd.h>

using metal::uint;

struct type_3 {
    metal::float4 inner[32];
};
struct U {
    metal::float4x4 vp;
    metal::float4 eye;
    uint hover;
    uint sel;
    uint per;
    uint pad;
    type_3 colors;
};
struct VO {
    metal::float4 pos;
    metal::packed_float3 world;
    uint id;
};

struct vsInput {
    metal::float3 p [[attribute(0)]];
    uint id [[attribute(1)]];
};
struct vsOutput {
    metal::float4 pos [[position]];
    metal::float3 world [[user(loc0), center_perspective]];
    uint id [[user(loc1), flat]];
};
vertex vsOutput vs(
  vsInput varyings [[stage_in]]
, constant U& u [[buffer(1)]]
) {
    const auto p = varyings.p;
    const auto id = varyings.id;
    VO o = {};
    metal::float4x4 _e6 = u.vp;
    o.pos = _e6 * metal::float4(p, 1.0);
    o.world = p;
    o.id = id;
    VO _e12 = o;
    const auto _tmp = _e12;
    return vsOutput { _tmp.pos, _tmp.world, _tmp.id };
}

uint naga_mod(uint lhs, uint rhs) {
    return lhs % metal::select(rhs, 1u, rhs == 0u);
}


struct fs_colorInput {
    metal::float3 world [[user(loc0), center_perspective]];
    uint id_1 [[user(loc1), flat]];
};
struct fs_colorOutput {
    metal::float4 member_1 [[color(0)]];
};
fragment fs_colorOutput fs_color(
  fs_colorInput varyings_1 [[stage_in]]
, metal::float4 pos [[position]]
, constant U& u [[buffer(1)]]
) {
    const VO i = { pos, varyings_1.world, varyings_1.id_1 };
    metal::float3 rgb = {};
    metal::float3 _e2 = metal::dfdx(i.world);
    metal::float3 _e4 = metal::dfdy(i.world);
    metal::float3 n = metal::normalize(metal::cross(_e2, _e4));
    metal::float4 _e9 = u.eye;
    float d = metal::abs(metal::dot(n, metal::normalize(_e9.xyz - i.world)));
    uint _e23 = u.per;
    metal::float4 c = u.colors.inner[naga_mod(i.id - 1u, _e23)];
    rgb = c.xyz * (0.25 + (0.75 * d));
    uint _e37 = u.hover;
    if (i.id == _e37) {
        metal::float3 _e39 = rgb;
        rgb = metal::mix(_e39, metal::float3(0.2, 0.9, 1.0), 0.6);
    }
    uint _e49 = u.sel;
    if (i.id == _e49) {
        metal::float3 _e51 = rgb;
        rgb = metal::mix(_e51, metal::float3(1.0, 0.3, 1.0), 0.6);
    }
    metal::float3 _e58 = rgb;
    return fs_colorOutput { metal::float4(_e58, c.w) };
}


struct fs_pickInput {
    metal::float3 world [[user(loc0), center_perspective]];
    uint id_1 [[user(loc1), flat]];
};
struct fs_pickOutput {
    uint member_2 [[color(0)]];
};
fragment fs_pickOutput fs_pick(
  fs_pickInput varyings_2 [[stage_in]]
, metal::float4 pos_1 [[position]]
) {
    const VO i_1 = { pos_1, varyings_2.world, varyings_2.id_1 };
    return fs_pickOutput { i_1.id };
}
