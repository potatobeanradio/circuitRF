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
| `RealizedGainDbi` | freq, port | dBi — gain including mismatch |
| `RadiationEfficiency` | freq, port | **%** — a percentage, not a fraction |
| `RadiationEfficiencyDb` | freq, port | dB — the same number, 10·log10(η) |
| `TrpDbm`, `PeakEirpDbm` | freq, port | dBm — see [TRP and EIRP](#trp) |
| `ReferenceInputPowerDbm` | freq, port | dBm — what those two are referenced to |
| `PowerAccepted`, `PowerRadiated`, `PowerSurfaceWave`, `PowerDielectricAndGround`, `PowerConductor` | freq, port | W |
| `BeamwidthDeg` | freq, **cut**, port | deg — the `cut` axis carries each plane's φ |
| `AxialRatioDb`, `PolarizationSense` | freq, θ, φ, port | dB, and signed Stokes *V* |
| `CoPolLudwig3Db`, `CrossPolLudwig3Db` | freq, θ, φ, port | dB |

One is **present and refused**, with its own sentence in the run's notes: `FrontToBackDb`. This model
has **no lower half-space at all** — the ground plane is laterally infinite and enters as a boundary
condition, so no field below it is ever computed. On a stackup whose ground layer has no conductivity
that plane is perfect and the true ratio really is infinite; on one that carries conductivity the
plane is a real conductor and does leak, so your structure's front-to-back is finite — but what is
missing from the model is a *region*, not a small number, and either way a printed value would be a
fiction. The run's note says which of the two it is in.

> **`PowerDielectricAndGround` was called `PowerDielectric`** before the ground plane became a real
> conductor. It is the same residual with one more mechanism in it, and it is renamed rather than
> split because the laterally infinite plane has no basis function to integrate over — what it
> absorbs can only arrive as a remainder. **A Data Display or script that names the old cube will
> find nothing**; point it at the new name.

## From a 3D solver {#3d}

The same checkbox works on a **driven** 3D setup — **FDTD 3D (openEMS)**, **FEM 3D (Palace)** or **FEM &
FDTD - Compare** — and produces the same `farfield` cubes, so the Data Display, the metrics and `circuitrf em`
read them exactly as they read a planar run's. It is disabled on a static or eigenmode problem, which drives
no port at a frequency. What is different:

- **The domain follows the air box.** With an absorber on every face the pattern is the **whole sphere**
  (θ 0…180°) and `FrontToBackDb` is computed. When the box's floor is a conducting plane — circuitRF makes
  an undrawn ground plane the floor — the pattern is the **upper hemisphere**, as the planar kernel's is,
  and front-to-back is refused for the same reason.
- **A pattern at every frequency of the sweep**, each port's from the run that drove that port, normalised to
  **1 V across the driven port** with every other port terminated in its own Z₀.
- **Three power terms are refused by name**: `PowerSurfaceWave`, `PowerDielectricAndGround` and
  `PowerConductor`. A 3D model has no infinite substrate to lose a guided wave into and neither solver
  itemises its loss; `PowerAccepted − PowerRadiated` is every loss together, and both are published.
- **FDTD 3D (openEMS)** transforms the tangential fields on a box that sits **3 cells inside the absorber and
  1 cell clear of every conductor**, by circuitRF's own surface integral. A setup whose air box leaves no
  room for that is refused, naming the face and the padding to raise; with the pattern on, the default
  padding is a quarter of the longest wavelength rather than an eighth for this reason. If the substrate runs
  through that box, what it guides is counted as radiation — the run says so, and on an unbounded substrate
  the directivity reads lower than the planar kernel's by the planar kernel's own surface-wave share.
- **FEM 3D (Palace)** uses Palace's own far-field integral, which needs **every air-box face absorbing and
  every port lumped**. Otherwise the S-parameters are still computed and the run says why there is no
  pattern; on *Compare* the openEMS half still produces one. Palace's absorbing faces are first order, so
  keep the box well away from the radiator. Its far-field file is about 16 MB per frequency per port.

**Measured** (circuitRF against closed forms and against itself, 2026-09-26): a short dipole reads
1.78 dBi through openEMS and 1.77–1.79 dBi through Palace, against 1.76; a half-wave dipole through openEMS
follows the sinusoidal-current directivity across the band at one electrical length, with radiation
efficiency 98.7–99.0 % for lossless metal. The shipped patch with its ground made the box's floor agrees with
the planar solve at 5.85 GHz to 0.3° in the H-plane beamwidth and 0.09 dB in directivity once the planar
kernel's surface-wave power is counted as radiated; drawn with its real 40 × 40 mm ground it reads 7.19 dBi
with an 82.6° E-plane — the finite plane's own effect.

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

### Stopping without losing the run

The resonance search keeps adding **solved** points after the resonance is on screen, and each one is a
full-wave frequency point. There is no way to see from outside how many more it intends to take, so the
EM run's progress bar offers a **Stop** above its Cancel:

| | |
|---|---|
| **Stop** | finish at the next work boundary and **keep everything solved**. The results are packaged and written exactly as a completed run's are. |
| **Cancel** | abandon the run and write nothing. |

Right-click either of the EM run's two progress rows. A stopped run is a complete, ordinary result —
same cubes, same `.snp` — which is why it always carries a note saying so, and saying what is therefore
not in it:

- with adaptive sampling on, **the full requested grid is still published**, modelled from the points
  that were solved (which is what adaptive sampling does at every budget); the note gives the
  disagreement actually reached rather than the tolerance you asked for;
- on a fixed grid, the sweep is **the prefix that was solved** and the note names the frequencies that
  are not in it. Nothing is interpolated and nothing is approximate.
- if a radiation pattern was asked for, **the patterns already taken are kept and no more are
  started**. A pattern costs about as much as the full-wave point it rides on, so at one per solved
  point the pattern block is the longest part of the run and a Stop has to reach into it. They are
  taken in ascending frequency, so what a stop leaves out is the top of the band — on a resonant
  structure, quite possibly the resonance — and the note says how many were taken and over what
  span. Every pattern in the result is a whole one, and the s-parameters are untouched: they are
  finished before the first pattern begins.

## Reading the pattern {#pattern}

**θ spans 0…90° only, and the axis stops there rather than being padded.** With an infinite ground plane
the field below the plane is not small — it is identically zero — so half a sphere of structural zeros
would read as a measured front-to-back ratio.

In the Data Display:

- **A cut** is one plane, swept over θ. Plot `farfield.U` on a **Polar** plot and set the radial axis
  to **dB**: the outer ring is the reference and each ring is a step down. The plot states which
  reference it is using, so a normalised pattern cannot be mistaken for an absolute one.
- **A cut is a plane, so it crosses the disc.** It runs from −θ<sub>max</sub> through broadside to
  +θ<sub>max</sub>, and the negative half is the φ + 180° branch — which is also what the beamwidth
  metric measures, so the picture and the number agree. There are **two ways to draw it, and the
  simple one is the default choice**:
  - **One trace.** Tick **Whole plane in one trace** on the trace's card and it fetches the φ + 180°
    half itself. An E-plane and an H-plane plot is then two traces rather than four, with one
    colour, one label and one marker set each. Headless: `--whole-plane`.
  - **Two traces**, which is what every `.cdd` written before 2026-09 carries and still the one to
    reach for when the halves want telling apart — a different colour per half, or the back half
    hidden. Add a trace pinned at φ + 180° and tick **Back half of the cut** on its card; headless,
    `--trace cube=farfield.U,cut=0,…` adds that second branch for you.

  Either way the trace's label names both azimuths (`phi=0/180 deg`), so a half-disc can never be
  mistaken for a whole one.
- **Bearings around the rim** — tick **Angles** beside the dB-radial switch (headless:
  `--angle-labels`) to print the angle every 30° outside the disc, with a spoke to each, the way an
  antenna-range plot is drawn. The disc shrinks to make room rather than the numbers landing on the
  outer ring, which on a normalised pattern is exactly where the peak is.
- **Normalised** means the peak of *this* trace is the outer ring — right for comparing shapes, useless
  for comparing two antennas. **Absolute** pins the outer ring to a dB value you choose, which is what
  you want when the levels are the point.
- **Two cuts on one polar plot** is the comparison worth making: the **E-plane** (the plane containing
  the current axis) against the **H-plane**. They are not the same width, and a beamwidth quoted without
  its plane is not a number.
- **The 3D surface** is for seeing that a pattern is not the shape you assumed, and for the picture that
  goes in a report. The principal-plane cuts say more.

Both, on the [worked example](#example) below, at 5.85 GHz:

{{ui: antenna-pattern-cuts}}

The two planes are 3 dB down at θ = 40° (H-plane) and θ = 75° (E-plane). That difference is the
infinite ground plane, and the [beamwidth caveat](#numbers) below is about this pair of curves.

{{ui: antenna-pattern-3d}}

The same data at the same frequency, on a 20 dB scale rather than the cuts' 40 dB: one broadside
lobe, no sidelobe, and no null until exact grazing. The surface shows that the shape holds in every
azimuth; the cuts are what you read a level off.

## Reading the numbers {#numbers}

### Which gain

| | Includes loss | Includes mismatch |
|---|---|---|
| `DirectivityDbi` | no | no |
| `GainDbi` | **yes** | no |
| `RealizedGainDbi` | yes | **yes** |

Neither is called just "gain", because the usual failure is comparing one against a datasheet that
quotes the other.

### Efficiency, twice

`RadiationEfficiency` is P<sub>radiated</sub> / P<sub>accepted</sub> **in percent**, and
`RadiationEfficiencyDb` is the same number in decibels — 0 dB lossless, −3 dB for half the accepted
power gone. Both are published; neither is derived from the other on the plot, because the Data
Display's dB transforms apply to a cube's own numbers and 10·log10 of a percentage is not a loss.

The denominator is the power **accepted** at the port, never the incident power: mismatch is already in
the port admittance, and counting it twice is the classic double count. Total efficiency — the one that
does include mismatch — is `RadiationEfficiencyDb + 10·log10(1 − |S11|²)`, which is exactly what
`TrpDbm` is at the default 0 dBm reference.

{{ui: antenna-efficiency-sweep}}

Radiation efficiency varies strongly across the band: 22.6 % at 5.3 GHz, 69.3 % at its maximum, and
62.6 % at the 5.85 GHz the [worked example](#example) quotes. **Quote it with the frequency it was
read at.**

The maximum is at **5.94 GHz — the parallel resonance, not the 5.81 GHz series resonance the feed is
matched at.** The two are 130 MHz apart on this patch and only the lower one is matched, so the
best-radiating frequency and the best-matched frequency are not the same. The three gains split along
that line: `GainDbi` follows the efficiency and peaks at 5.94 GHz (5.14 dBi), while `RealizedGainDbi`
carries the mismatch and peaks at 5.85 GHz (4.50 dBi). Which one you quote decides which of the two
frequencies looks best.

**The mismatch factor is 1 − |S<sub>11</sub>|², read from the same published, de-embedded
s-parameter the S cube carries** — so the two gains differ by exactly that and by nothing else:

```
RealizedGainDbi = GainDbi + 10·log10(1 − |S11|²)
```

It is *not* read from the raw admittance of the port's delta-gap excitation, which at a de-embedded
edge port is the gap's own parasitic rather than the antenna's input — that reads a matched antenna as
badly mismatched.

### TRP and peak EIRP {#trp}

These are the two numbers an **over-the-air report** leads with, and they are the only *absolute*
quantities here — everything else on this page is a ratio.

| | |
|---|---|
| `TrpDbm` | total radiated power: what the antenna radiates in **every** direction |
| `PeakEirpDbm` | equivalent isotropically radiated power in the pattern's strongest direction |

A ratio needs no excitation to be absolute against; a watt does. This analysis drives a 1 V delta gap,
which means nothing in watts, so **you supply the reference** — in either of two places, and the second
one is the one you will normally touch:

| | |
|---|---|
| *EM Setup ▸ Radiation pattern ▸ Reference input power* | what the **run records**: the value baked into the `.npy` and reported by `circuitrf em`. |
| *Trace card ▸ Reference input power* | reads the **same solved data** against any other reference — **no re-run**. Headless: `ref=<dBm>` on a `--trace`. |

**Changing the reference never needs a re-run.** A level is linear in its reference, so re-referencing
is a subtraction and an addition, both exact. The run publishes its own reference as
`ReferenceInputPowerDbm` beside the two levels — a dBm whose reference is not in the file cannot be
reproduced from it, and it is also what makes the trace-card version exact rather than a guess. The
trace's label **always** states the reference it is drawn at (`@ 20 dBm in`), because a picture carries
no file and there would otherwise be no way to tell one reference from another.

```
TrpDbm      = ReferenceInputPowerDbm + RadiationEfficiencyDb + 10·log10(1 − |S11|²)
PeakEirpDbm = ReferenceInputPowerDbm + RealizedGainDbi
            = TrpDbm + DirectivityDbi
```

**The default is 0 dBm**, and at 0 dBm the two read as quantities you already have: peak EIRP in dBm
*is* the realized gain in dBi, and TRP in dBm *is* the total efficiency in dB. Set it to a radio's own
conducted power and both become directly comparable against that radio's measured report. It changes
nothing else — a directivity, a gain and an efficiency are ratios and do not move.

**Read TRP as a lower bound.** Full-sphere is what TRP means, and here the sphere and the upper
hemisphere are the same integral because the ground plane is infinite. A real board puts power behind
the antenna that this model cannot see, and the surface-wave term — which a finite board radiates from
its edges and this one books as loss permanently — pushes the same way.

### The loss itemisation, and what to change for each term

The run prints a power budget at every pattern point: accepted = radiated + surface wave +
dielectric and ground plane + conductor.

| Term | What to change |
|---|---|
| **Radiated** | this is the output, not a loss |
| **Dielectric + ground plane** | lower tanδ, or a thicker substrate (the same current radiates more of its power); and a lower-resistivity ground plane. **The two are reported together because they cannot be separated here**: the plane is laterally infinite and is not meshed, so it has no basis function to integrate its loss over the way the drawn metal does, and what it absorbs can only arrive in this residual. On a low-tanδ substrate it is most of what this line reads — measured at **14% of it** on the MMIC starter at 30 GHz and 0.4% on FR-4 at 6 GHz, where the dielectric swamps it. A ground layer with no σ is a perfect plane and this term is dielectric loss alone |
| **Surface wave** | thinner substrate, or lower ε<sub>r</sub>. Booked as a **permanent loss** here, because an infinite substrate never gives it back — on a real board it reaches the edge and radiates, usually badly |
| **Conductor** | lower-resistivity metal, or thicker metal — it is the **drawn** metal's own ohmic loss, taken from the stackup's σ and t. **Exactly zero only when the metal really is a perfect conductor**, and the budget says which of the two zeros it is printing. The ground plane's share is real and is modelled, but it is in the line above rather than this one |

**The surface-wave term makes the reported efficiency a lower bound** on what a finite board does —
an infinite substrate never gives that power back, where a real board's edge radiates some of it. The
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

- **No back radiation, and no front-to-back ratio.** The ground plane is laterally infinite and enters
  as a boundary condition on the underside of the stack, so the model computes no field below it at
  all — not even the leakage a real conductor has. `FrontToBackDb` is present and refused rather than
  printed as a large number. **Directivity therefore reads optimistic** against a real board, whose
  finite plane puts substantial power behind it and tilts and ripples the pattern.
- **Drawing the pour does not make the plane finite**, but it does make its size *reportable*: a run
  that finds artwork on the return plane prints the plane's bounding box, equal-area diameter and — the
  electrically meaningful one — how far the plane reaches **beyond** the radiating metal, all in
  wavelengths at both ends of the sweep. There is deliberately no "your ground plane is big enough"
  verdict, because no measurement here yields a threshold.
- **No apertures in the ground plane.** Slot antennas, CPW-fed slots and aperture-coupled patches are
  out entirely. [Feed from the edge](#feeds) instead.
- **No vertical current in the pattern.** A probe feed, a monopole, an IFA, a via-fenced patch: the
  s-parameters are computed, the pattern is refused by name. Edge- and inset-fed structures are in.
- **The signal metal's loss is modelled and the GROUND PLANE's is not**, so radiation efficiency reads
  high by the plane's share of the conductor term — 21% of it on FR-4, about 11% on the MMIC
  technology. The signal metal's own term is a sheet and under-reads a thick strip's crowding by a
  further measured amount; [the MoM reference](mom-engine.html#can-cannot) carries both numbers. A
  measured size: a half-wave patch loses 0.28 points of efficiency to 35 µm copper on FR-4 at
  2.4 GHz, and 4.12 points to 3 µm gold on GaAs at 60 GHz.
- **Surface-wave power is a permanent loss**, so efficiency is a lower bound and the pattern is missing
  what a real board's edge re-radiates.
- **Every dielectric layer is laterally infinite — including one you drew.** circuitRF *does* model
  patterned dielectric artwork (a thin-film capacitor's film, tied to its plate), and it is reasonable
  to assume a superstrate drawn over the patch alone is modelled where it is drawn. **It is not.** That
  mechanism decides *whether* a layer is in the run, never *where it stops*. A radome, a conformal
  coating or a gain-raising superstrate is modelled as covering the whole run, to infinity — which moves
  resonance, gain and surface-wave launch by enough to matter.
  **And a uniform cover layer is not in the solve at all today** — a dielectric declared ABOVE the
  topmost analysis level is discarded, the run warns by name, and the answer is the bare board's. That
  is measured, not inferred; see the [measured example](#example) below.

## Worked example: a 5.8 GHz inset-fed patch {#example}

**Tools ▸ Examples ▸ Patch Antenna** installs this workspace wherever you choose it, ready to open —
one inset-fed patch on the shipped **PCB 2-Layer RO4350B (30 mil, 1 oz)** technology, ε<sub>r</sub>
3.66, tanδ 0.0037, 762 µm to the ground plane. Open `patch/em/patch-5p8GHz.cem` and press
**Simulate**; it arrives with the radiation pattern, the radiating-sheet mesh and the resonance search
already on, which is what makes every number below re-derivable. It is a test as well as an example —
the same workspace is `testdata/antenna/` in the circuitRF source tree, where the suite runs it.

<div class="callout note">
<span class="label">Open the setup that is in the cell, not a new one</span>
<p>This example keeps its EM setup at <code>patch/em/patch-5p8GHz.cem</code> — inside the cell folder,
beside the layout's own — because it is <i>this patch's</i> setup. Open it from the project tree.
The layout editor's <b>EM</b> button finds it too; before 2026-09-15 it did not, and made a second,
default setup instead — if you have one of those from an earlier build, it is the one at
<code>em/patch.cem</code> and it is not the run this page describes.</p>
</div>

Headless, on either copy:

```
circuitrf em <workspace>/patch/em/patch-5p8GHz.cem
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
| Realized gain | 4.50 dBi — `GainDbi` + 10·log₁₀(1 − \|S₁₁\|²), which is what `RealizedGainDbi` publishes |
| Radiation efficiency | 62.6 % |
| E-plane 3 dB beamwidth | 146° — and see the [infinite-ground caveat](#numbers) before quoting it |
| Ground plane | 0.71 λ₀ across at 5.3 GHz rising to 0.84 λ₀ at 6.3, with 0.15–0.18 λ₀ beyond the metal |

The power budget at that point, in the run's own words: **27.50 µW accepted = 17.22 µW radiated
(62.6 %) + 1.93 µW surface wave (7.0 %) + 8.35 µW dielectric + ground plane (30.4 %) + 0 conductor.**
Dielectric loss is the term to attack, and the explicit zero is the perfect metal.

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
