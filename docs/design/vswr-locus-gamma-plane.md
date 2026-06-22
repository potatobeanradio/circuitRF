# Constant-VSWR locus in the Γ-plane — closed-form circle

**Status:** Implemented (RfCore `LoadpullSurface.VswrCircleGamma`) · **Date:** 2026-06-22
**Reads with:** `RfCore/src/Loadpull/LoadpullSurface.cs` (the `VswrLocus` / `VswrCircleZ` / `VswrCircleGamma` methods), `RfCore/src/RfHelpers.cs` (`G2Z`, `Z2G`).
**Reference:** K. Kurokawa, "Power Waves and the Scattering Matrix," *IEEE Trans. MTT*, vol. 13, no. 2, pp. 194–202, 1965.

---

## Problem

A constant-VSWR locus is naturally a circle in the **impedance (Z) plane**: the existing
`VswrCircleZ` parametrizes it as

```
Z(θ) = (Zc + ρ·e^{jθ}·conj(Zc)) / (1 − ρ·e^{jθ}),   ρ = (V−1)/(V+1)
```

about a center impedance `Zc`. The old Γ-plane locus was obtained by sampling this Z-circle
uniformly in θ and mapping each point to Γ via `Γ = Z2G(Z/Z0)`. That mapping is **non-conformal
in spacing**: equal steps in the Z/θ parameter become very unequal steps in Γ, so for large VSWR
(|V| ≳ 50) the rendered Γ ring had segments whose length varied by **>12×** around the loop — the
"jaggy" appearance, with long chords on one side and bunched points on the other.

We want a locus that is smooth in the Γ-plane at any VSWR with a **fixed** point count.

## Key fact: the locus is a true circle in Γ

The constant-VSWR contour is the set `|s| = ρ` of the **Kurokawa power-wave reflection
coefficient** referenced to the (generally complex) `Z0`:

```
s = (Z − Z0) / (Z + conj(Z0))        (Kurokawa 1965, eq. for the power-wave Γ)
```

The conjugate in the denominator is exactly why `VswrCircleZ` carries `conj(Zc)` — the Z-plane
locus is the `|s|=ρ` circle expressed about its center. The **ordinary** voltage reflection
coefficient (what a Smith chart plots) is

```
Γ = (Z − Z0) / (Z + Z0)
```

Eliminating Z between these two gives a **Möbius (bilinear) transform** between Γ and s. Möbius
transforms map circles to circles, so the image of `|s| = ρ` in the Γ-plane is **again a circle**.
(Verified numerically: points produced by the old Z→Γ path lie on a single circle to ~1e-15.)

So we never needed to sample the Z-plane and map across — we can compute the Γ-plane circle's
center and radius directly and sample *it* uniformly.

## Derivation

The locus is built about a **center impedance** `Zc = G2Z(Γ_center)·Z0` (ohms; `G2Z(g)=(1+g)/(1−g)`
returns normalized Z, multiplied by the full complex `Z0`). The Z-plane locus about `Zc` is the
power-wave circle `|s_c| = ρ` with `s_c = (Z − Zc)/(Z + conj(Zc))`, i.e.

```
Z = (Zc + s_c·conj(Zc)) / (1 − s_c)
```

Compose this with `Γ = (Z − Z0)/(Z + Z0)`. Writing `Z = (N0 + N1·s_c)/(D0 + D1·s_c)` with
`N0 = Zc, N1 = conj(Zc), D0 = 1, D1 = −1`:

```
Γ = ( (N0 − Z0·D0) + (N1 − Z0·D1)·s_c ) / ( (N0 + Z0·D0) + (N1 + Z0·D1)·s_c )
  = (a·s_c + b) / (c·s_c + d)
```

with

```
a = conj(Zc) + Z0
b = Zc − Z0
c = conj(Zc) − Z0
d = Zc + Z0
```

The image of the circle `|s_c| = ρ` under `Γ = (a·s + b)/(c·s + d)` is the circle

```
denom  = |d|² − |c|²·ρ²
center = (b·conj(d) − a·conj(c)·ρ²) / denom
radius = |a·d − b·c|·|ρ| / |denom|
```

We then emit `center + radius·e^{jθ}` for `θ` uniform on `[0, 2π)`.

### Real-Z0 sanity check

For real `Z0 = R0` the power-wave coefficient collapses to the ordinary one (`s = Γ`), so the
locus is `|Γ| = ρ` — a circle centered at the matched point with radius ρ. The closed-form above
reproduces this. (Confirmed numerically.)

### Degenerate case

`denom = |d|² − |c|²ρ²` vanishes when the image circle passes through `Γ = ∞`. This is not
reachable for physical passive cases inside the unit disk, but to stay robust the implementation
falls back to directly sampling the Möbius map `Γ = (a·s + b)/(c·s + d)` at uniform `θ` when
`|denom| < 1e-30`.

## Validation

Tested against the original `Z2G(VswrCircleZ(...))` path across real and complex `Z0`
(`50`, `50+20j`, `30−15j`, `75`), several centers (including Γ=0 and high-|Γ| points), and
`V ∈ {1.5, 2, 10, 50, 60, 100, 150, 500}`:

- **Geometric agreement:** all points of both methods lie on the same circle to machine epsilon
  (~1e-15 max radial deviation).
- **Uniformity:** with the closed-form sampler the ratio of longest-to-shortest segment around the
  ring is **1.000×** at every VSWR; the old Z→Γ sampler was **11.6×** at V=60 and **12.4×** at V=150.

A fixed `VswrNPoints = 100` now renders smoothly at any VSWR, so no adaptive point-count is needed.

## Implementation

`LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma, z0ref, n)` calls
`VswrCircleGamma`, which implements the closed form above. The Z-plane branch
(`SurfacePlane.Z`) is unchanged and still uses `VswrCircleZ`. `VswrBoundingBox` reuses
`VswrLocus`, so the loadpull auto-view-box behavior is unchanged (the Γ point set is identical;
only its parametrization changed).
