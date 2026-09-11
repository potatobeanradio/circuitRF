---
title: Antennas
slug: reference/antennas.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Antennas
lede: Patch antennas in the planar solver — the pattern, the numbers that go with it, and the limits that bound both.
keywords: antenna, patch, microstrip antenna, radiation pattern, far field, directivity, gain, realized gain, radiation efficiency, beamwidth, polarization, axial ratio, cross-pol, Ludwig-3, surface wave, resonance, inset feed, probe feed, slot
---

The planar kernel radiates by construction: the top of the stack is an open half-space and the
radiation condition is exact, so there is no airbox, no absorbing boundary and nothing to size. An
antenna is an ordinary [EM run](mom-engine.html) with one checkbox added.

**Everything on this page follows from one fact: the ground plane and every dielectric layer are
laterally infinite.** That is what makes the radiation condition exact, and it is also what the
[limits](#cannot) are.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#on">Turning it on, and what you get</a></li>
<li><a href="#feeds">Which feeds work</a></li>
<li><a href="#setup">Setting a patch up</a></li>
<li><a href="#resonance">Finding the resonance</a></li>
<li><a href="#pattern">Reading the pattern</a></li>
<li><a href="#numbers">Reading the numbers</a></li>
<li><a href="#cannot">What this will not tell you</a></li>
<li><a href="#example">Worked example: a 5.8 GHz inset-fed patch</a></li>
</ol>
</nav>

## Turning it on, and what you get {#on}

**EM Setup ▸ Radiation pattern ▸ Compute the radiation pattern.** Off by default, because it is only
meaningful on a radiator. It changes no s-parameter and no mesh cell — the pattern is a post-process of
currents the solve has already paid for — so switching it on does not invalidate anything.

{{ui: antenna-radiation-pattern}}

Headless there is nothing to add: the flag lives in the `.cem`, so `circuitrf em patch.cem` produces the
same results.

The results land in the `farfield` group of the same result the s-parameters do.

| Output | Axes | Unit |
|---|---|---|
| `U` | freq, θ, φ, port | W/sr — radiation intensity |
| `Etheta`, `Ephi` | freq, θ, φ, port | V, r-normalised (r·E with e<sup>−jk₀r</sup> removed) |
| `DirectivityDbi`, `GainDbi` | freq, port | dBi |
| `DirectivityPeakThetaDeg`, `…PhiDeg` | freq, port | deg — where the peak is |
| `RadiationEfficiency` | freq, port | fraction, not dB |
| `PowerAccepted`, `PowerRadiated`, `PowerSurfaceWave`, `PowerDielectric`, `PowerConductor` | freq, port | W |
| `BeamwidthDeg` | freq, **cut**, port | deg — the `cut` axis carries each plane's φ |
| `AxialRatioDb`, `PolarizationSense` | freq, θ, φ, port | dB, and signed Stokes *V* |
| `CoPolLudwig3Db`, `CrossPolLudwig3Db` | freq, θ, φ, port | dB |

Two are **present and refused**, each with its own sentence in the run's notes: `FrontToBackDb`
(there is no field behind an infinite plane) and `RealizedGainDbi` (see [the numbers](#numbers)).

## Which feeds work {#feeds}

| Feed | Pattern? | Why |
|---|---|---|
| **Inset-fed / edge-fed microstrip** | **yes** | the current stays in the plane |
| **Proximity- / gap-coupled patch** | **yes** | same reason |
| Coaxial probe (an `Internal` port) | no — refused by name | the probe's current is z-directed, and this kernel does not radiate vertical current yet. The s-parameters are still right. |
| Slot, CPW-fed slot, aperture-coupled patch | no | an aperture in the ground plane is not representable at all |

So: **feed a patch from the edge.** A probe feed still gives you Z<sub>in</sub> and the resonance —
it is refused for the *pattern* only, and the run says so.

## Setting a patch up {#setup}

{{ui: antenna-patch-layout}}

1. **Draw the patch and its feed as ordinary metal** on one conductor level. Do not draw the ground
   plane for the solver's benefit — the stackup's ground reference already *is* the plane. Drawing the
   pour is still worth doing, because it is the only way the run can report how big your real plane is
   (see [limits](#cannot)).
2. **Put the port on the feed's end face**, pointing in. An edge port is de-embedded, so your
   reference plane ends up at the end of the drawn feed and the solver grows whatever uniform lead the
   calibration needs.
3. **EM Setup ▸ Surface mesh ▸ This metal is → Radiating sheet.** A patch is not a line: the current
   varies on the scale of a wavelength in *both* directions and there is no "along". With
   *Transmission line* selected instead, the mesher looks for a current direction, fails to find one on
   a wide sheet, and declines — leaving the cell size set by the narrowest metal on the board.
4. **Boundary cells must be Staircase.** The far field does not transform a conformal (cut) cell — a
   cut cell's metal is not its rectangle — so a conformal mesh refuses the pattern by name. The
   checkbox disables itself and says so.

{{ui: antenna-patch-em-setup}}

<div class="callout note">
<span class="label">Edge refinement matters more here, not less</span>
<p>The radiating edges set the effective length, which sets the resonant frequency, which sets
everything. Leave <b>Edge mesh</b> on. If the cell count is the problem, the <b>Detail floor</b> is the
control that fixes it — it stops sub-wavelength import artefacts from sizing the whole grid.</p>
</div>

## Finding the resonance {#resonance}

A patch resonance is narrower than any sweep you would draw across a band, and **adaptive sampling
cannot rescue it**: adaptive sampling bisects the grid you *gave* it and never adds a frequency you did
not ask for. Ten points across a decade will miss a 1 % bandwidth completely, and the run will tell you
it did not converge without telling you where the feature is.

Two things to do, in order:

- **Sweep the band you expect, not the decade.** Start from the cavity-model estimate:
  f ≈ c / (2(L + 2ΔL)√ε<sub>eff</sub>).
- **Turn on EM Setup ▸ Frequency ▸ Resonance search.** It is the one setting in circuitRF that lets a
  sweep publish a frequency you did not ask for: it looks for Im(Z<sub>in</sub>) crossing zero, brackets
  each crossing by bisection, and reports f₀, Q and the bandwidth. Every added point is flagged, and
  every point of your own grid is published exactly as it was. It needs adaptive sampling on, because it
  seeds itself from the model refinement builds.

## Reading the pattern {#pattern}

**θ spans 0…90° only, and the axis stops there rather than being padded.** With an infinite ground plane
the field below the plane is not small — it is identically zero — so half a sphere of structural zeros
would read as a measured front-to-back ratio.

In the Data Display:

- **A cut** is one φ, swept over θ. Plot `farfield.U` on a **Polar** plot and set the radial axis to
  **dB**: the outer ring is the reference and each ring is a step down. The plot states which reference
  it is using, so a normalised pattern cannot be mistaken for an absolute one.
- **Normalised** means the peak of *this* trace is the outer ring — right for comparing shapes, useless
  for comparing two antennas. **Absolute** pins the outer ring to a dB value you choose, which is what
  you want when the levels are the point.
- **Two cuts on one polar plot** is the comparison worth making: the **E-plane** (the plane containing
  the current axis) against the **H-plane**. They are not the same width, and a beamwidth quoted without
  its plane is not a number.
- **The 3D surface** is for seeing that a pattern is not the shape you assumed, and for the picture that
  goes in a report. The principal-plane cuts say more.

## Reading the numbers {#numbers}

### Which gain

| | Includes loss | Includes mismatch |
|---|---|---|
| `DirectivityDbi` | no | no |
| `GainDbi` | **yes** | no |
| Realized gain | yes | **yes** |

Neither published result is called just "gain", because the usual failure is comparing one against a
datasheet that quotes the other.

**Realized gain is refused, and the substitute is exact.** The mismatch factor needs the reflection a
source would see at the port; what the analysis has where the pattern is taken is the raw admittance of
the port's delta-gap excitation, which at a de-embedded edge port is the gap's own parasitic rather than
the antenna's input. So compute it yourself from the two published results:

```
realized gain (dBi) = GainDbi + 10·log10(1 − |S11|²)
```

### The loss itemisation, and what to change for each term

The run prints a power budget at every pattern point: accepted = radiated + surface wave + dielectric +
conductor.

| Term | What to change |
|---|---|
| **Radiated** | this is the output, not a loss |
| **Dielectric** | lower tanδ, or a thicker substrate (the same current radiates more of its power) |
| **Surface wave** | thinner substrate, or lower ε<sub>r</sub>. Booked as a **permanent loss** here, because an infinite substrate never gives it back — on a real board it reaches the edge and radiates, usually badly |
| **Conductor** | always exactly zero: the full-wave kernel's metal is a perfect conductor |

**The two corrections do not cancel, so read both.** The surface-wave term makes the reported efficiency
a **lower bound** on what a finite board does; the missing copper loss makes it read **high**. The
pattern is also missing the edge-diffracted contribution entirely.

### Beamwidth, polarization, cross-pol

- **Beamwidth is per named cut**, and the cut is either named by you or derived from the plane
  containing the peak and the dominant current axis. The φ that was used is on the result's own `cut`
  axis. With an infinite ground plane the **E-plane reads much broader than a real board measures**: the
  pattern only reaches zero at exact grazing, so the half-power point sits near 78° where a finite board
  puts it nearer 40°. It is refused outright when the cut has no half-power crossing inside 0…90°.
- **Co- and cross-pol are Ludwig-3**, about a reference azimuth φ₀ that is derived from the dominant
  current axis unless you name one. It is the same axis the derived beamwidth cut uses.
- **`PolarizationSense` is IEEE**, as the signed, normalised Stokes *V*: its sign is the sense and its
  magnitude is how circular the direction is, so a nearly linear direction reads near zero rather than
  being assigned a handedness it does not have.

<div class="callout warn">
<span class="label">A principal-plane cross-pol of −45 dB may be −45 dB of mesh</span>
<p>Cross-polarization is generated by asymmetry. On a nominally symmetric patch the physical
principal-plane cross-pol is very low, so what is reported there is dominated by the <b>mesh's</b>
asymmetry — a staircased boundary is not symmetric, and neither is a grid whose lines were placed by an
edge attractor at one rim. Read it as a ceiling on what the analysis can resolve: refine the mesh and
watch whether the number moves. Cross-pol on the <b>diagonal</b> planes is a different thing — it is
physical, and it is ideally non-zero.</p>
</div>

## What this will not tell you {#cannot}

<div class="callout warn">
<span class="label">Read this before you trust a number</span>
<p>A user who discovers a limit by getting a wrong answer has been failed by the documentation.</p>
</div>

- **No back radiation, and no front-to-back ratio.** The ground plane is laterally infinite, so the
  field behind it is identically zero. `FrontToBackDb` is present and refused rather than printed as a
  large number. **Directivity therefore reads optimistic** against a real board, whose finite plane puts
  substantial power behind it and tilts and ripples the pattern.
- **Drawing the pour does not make the plane finite**, but it does make its size *reportable*: a run
  that finds artwork on the return plane prints the plane's bounding box, equal-area diameter and — the
  electrically meaningful one — how far the plane reaches **beyond** the radiating metal, all in
  wavelengths at both ends of the sweep. There is deliberately no "your ground plane is big enough"
  verdict, because no measurement here yields a threshold.
- **No apertures in the ground plane.** Slot antennas, CPW-fed slots and aperture-coupled patches are
  out entirely. [Feed from the edge](#feeds) instead.
- **No vertical current in the pattern.** A probe feed, a monopole, an IFA, a via-fenced patch: the
  s-parameters are computed, the pattern is refused by name. Edge- and inset-fed structures are in.
- **The metal is a perfect conductor**, so radiation efficiency reads high by the copper's share.
- **Surface-wave power is a permanent loss**, so efficiency is a lower bound and the pattern is missing
  what a real board's edge re-radiates.
- **Every dielectric layer is laterally infinite — including one you drew.** circuitRF *does* model
  patterned dielectric artwork (a thin-film capacitor's film, tied to its plate), and it is reasonable
  to assume a superstrate drawn over the patch alone is modelled where it is drawn. **It is not.** That
  mechanism decides *whether* a layer is in the run, never *where it stops*. A radome, a conformal
  coating or a gain-raising superstrate is modelled as covering the whole run, to infinity — which moves
  resonance, gain and surface-wave launch by enough to matter.
  **A uniform cover layer is therefore a fair model and is supported**; see the
  [measured example](#example) below. A patterned one is not the thing you drew.

## Worked example: a 5.8 GHz inset-fed patch {#example}

The workspace is `testdata/antenna/` in the circuitRF source tree: one inset-fed patch on the shipped
**PCB 2-Layer RO4350B (30 mil, 1 oz)** technology — ε<sub>r</sub> 3.66, tanδ 0.0037, 762 µm to the
ground plane. It is a test as well as an example, so every number below is re-derivable.

```
circuitrf em testdata/antenna/patch/em/patch-5p8GHz.cem
```

{{ui: antenna-patch-feed}}

| | |
|---|---|
| Patch | 16.94 × 13.30 mm — W from λ₀/2 · √(2/(ε<sub>r</sub>+1)), L from the cavity model |
| Feed | 1.68 mm microstrip (50 Ω), inset 4.0 mm into a notch with 1.0 mm gaps |
| Ground pour | 40 × 40 mm, drawn on the bottom copper so the run can report its size |
| Port | one edge port on the feed's end face, 50 Ω, de-embedded |
| Mesh | radiating sheet, staircase cells, shipped defaults → **N = 1,611** |
| Sweep | 5.3–6.3 GHz, 21 points, adaptive sampling + resonance search |
| Run | **4.9 minutes** on 10 cores, with a pattern at every one of the 21 points |

### What it finds

**The resonance search reports f₀ = 5.8131 GHz**, series, Q = 35.1 at R = 38.9 Ω — |S₁₁| = −18.1 dB
there, with a −10 dB bandwidth of **91 MHz (1.6 %)**. It also finds the parallel resonance at
5.9425 GHz, where |S₁₁| only reaches −3.7 dB: *the resonance is real; it is the match that is not
there.* Both are points the 50 MHz sweep grid could not have shown.

**The cavity model puts it at 5.7968 GHz** — f = c / (2(L + 2ΔL)√ε<sub>eff</sub>) with
ε<sub>eff</sub> = 3.402 and ΔL = 0.361 mm — so the solver and an independent analytic reference agree
to **+0.28 %**. That is the comparison to judge the tool by; nothing in it is a circuitRF number
checked against another circuitRF number.

At 5.85 GHz, the requested grid point nearest resonance:

| | |
|---|---|
| S₁₁ | −14.3 dB, Z<sub>in</sub> = 58.6 + j19.4 Ω |
| Directivity | 6.70 dBi, peak at θ = 0° (broadside) |
| Gain | 4.66 dBi — efficiency only |
| Realized gain | 4.50 dBi — `GainDbi` + 10·log₁₀(1 − \|S₁₁\|²), done by hand per [above](#numbers) |
| Radiation efficiency | 62.6 % |
| E-plane 3 dB beamwidth | 146° — and see the [infinite-ground caveat](#numbers) before quoting it |
| Ground plane | 0.71 λ₀ across at 5.3 GHz rising to 0.84 λ₀ at 6.3, with 0.15–0.18 λ₀ beyond the metal |

The power budget at that point, in the run's own words: **27.50 µW accepted = 17.22 µW radiated
(62.6 %) + 1.93 µW surface wave (7.0 %) + 8.35 µW dielectric (30.4 %) + 0 conductor.** Dielectric loss
is the term to attack, and the explicit zero is the perfect metal.

### The mesh is not what limits this example — the frequency grid is

The example runs on the **shipped default mesh**, not a cut-down one. Re-meshed at 30 cells/λ
(N = 1,983 against 1,611) the radiation efficiency moves 0.2 pp, Z<sub>in</sub> moves 2 %, and f₀
moves about 2.5 MHz — 0.04 %. So one convergence check is worth doing and the default passes it.

**What is coarse is the sweep.** 21 points across 1 GHz is a 50 MHz step against a 91 MHz bandwidth,
which is why the example leans on the resonance search. For design work, narrow the band once you know
where f₀ is — 5.70–5.95 GHz at 5 MHz is 51 points and resolves the notch from the grid alone, at about
four times the solve time.

### A cover layer over the patch does nothing, and the run says so

Adding a uniform 0.5 mm, ε<sub>r</sub> 3.0 radome to this technology's stackup produces s-parameters
**bit-identical** to the uncovered run at every frequency. The medium is built from the ground plane up
to the topmost analysis level and terminated in air there, so a dielectric above the metal is not in
the solve. That is measured, not inferred, and the run now warns by name — but read the numbers as the
bare board's.
