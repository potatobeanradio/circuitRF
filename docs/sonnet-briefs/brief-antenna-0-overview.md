# Brief — antennas in the planar kernel: the series

**Owner request, 2026-09-10.** Can circuitRF simulate a patch antenna, and if so what would it take
to report the things antenna work is actually about — far-field patterns, gain, directivity,
efficiency, beamwidth, front-to-back, surface currents and polarization? Plus: the default mesh on a
real imported patch produces an unusable cell count, and patch antennas may want a mesh of their own.

This file is the **overview only**. It is not implementable. Each numbered brief beside it is.

---

## 0. The short answer

**The kernel is right for this and most of the hard physics is already built.** Kernel B is a 2.5-D
MPIE surface MoM in a laterally-infinite stratified medium with an open top half-space. Three things
already in the tree are exactly what an antenna needs, and none of them were put there for antennas:

- **The radiation condition is exact** — no airbox, no PML, no box walls (`mom-engine.md` §10).
- **Losslessness is deliberately not a gate**, because "an open planar structure radiates and launches
  surface waves, so |S₁₁|² + |S₂₁|² < 1 legitimately" (`src/Engine/Mom/CLAUDE.md` §1). The power that
  leaves the port is already physically real, not numerical leakage.
- **Surface-wave poles are already found**, with polarization, index and residue machinery
  (`SurfaceWavePoles.Find`). That is the loss term that separates a credible patch answer from a
  plausible one.

What does not exist is anything that looks **away** from the metal. Everything in ANT-4 onward is
that, and it is cheaper than it looks — see §3.

**But two things must be fixed before any of it is worth building**, and they are ANT-1 and ANT-2/3.

---

## 1. What was measured, 2026-09-10

On an imported four-layer FR-4 patch board supplied for this investigation: a 41.3 × 49.4 mm
inset-fed patch on top copper, a 349.3 µm microstrip feed, a coax-connector footprint with via lands,
and a 70 × 70 mm plane on the first inner layer 203.2 µm below the patch (εᵣ 4.4, tanδ 0.02). Every
number below is one frequency point through `Cli em`, on scratch copies.

### 1a. The board was simulating without its feed line

`PlanarExtractor` drops **every** `PathShape`, on any layer, on the grounds that "a Path is a
centreline… it encloses no area" (`src/Design/Layout/Em/PlanarExtractor.cs:220-226`). That is true of
a zero-width polyline. It is false of a width-bearing stroke — and the Gerber reader represents every
track as exactly that (`GerberReader.Regions.cs:276`: "a stroke stays a `PathShape`"), with `Width`
and `End`.

| what was solved | Zin near 1.7 GHz | power leaving the port |
|---|---|---|
| as imported, N = 3,796 | 0.70 − j56.5 Ω | 2.3 % |
| as imported, N = 7,442, conformal cells, accelerated | 0.70 − j56.5 Ω | 2.3 % |
| **feed replaced by an equivalent `Rect`, N = 4,854** | **13.6 − j89.4 Ω** | **22.6 %** |

The first two are a **pure 1.6 pF capacitor** — flat and monotonic from 1.55 to 1.90 GHz, no
resonance anywhere. Doubling the mesh and switching on conformal cells moved neither digit, which
rules out mesh coarseness as the cause. With the feed present as a filled rectangle the structure
resonates at **1.740 GHz** (S11 −3.2 dB; Zin sweeping 62 − j323 → 30 − j71 → 4 − j50 across the
band). The cavity-model estimate for a 41.3 mm patch on εᵣ 4.4 is **1.734 GHz**, so the "with feed"
answer is independently corroborated and the "as imported" one is not an answer at all.

There is no warning. The run says `4 shape(s) were ignored — a Path is a centreline`, one line among
twenty, and never that the feed is gone. **This is ANT-1, and it is first because until it lands
every Gerber- or KiCad-imported board is EM-simulated with its traces missing.**

### 1b. The mesh, measured across nine configurations

| configuration | unknowns |
|---|---|
| as imported, **default mesh** (Auto, 20/λ, 4 across, edge on) | **704,482** — refused |
| as imported, transmission-line mesh on, otherwise default | 23,416 — refused |
| as imported, TL on + edge mesh off | 15,251 — refused |
| as imported, TL on + edge off + 2 across, accelerated | 7,442 — solves |
| as imported, TL on + edge off + 1 across | 3,796 — solves, ~18 s/point |
| as imported, owner's own settings (cells/λ = 5) | 1,909 |
| connector via-lands deleted, default mesh | 51,031 — refused |
| connector via-lands deleted, TL on, 4 across, **edge on** | 12,596 — refused |
| connector via-lands deleted, TL on, 4 across, **edge off** | **4,854** — solves |

Three separate causes, only one of which is antenna-specific:

1. **Sub-wavelength import artefacts set the global pitch.** The narrowest metal is reported as
   **310 µm** — not the feed, but the connector's via lands and the aperture-rounded corners a Gerber
   import brings in. Deleting only those moved the default mesh 704,482 → 51,031, a **14× swing**
   driven by geometry that is λ_g/830 in size and ~9 mm from anything electrically interesting.
2. **The edge fan is the mesh, and on a patch it is 61 % of it** (12,596 → 4,854 when the edge mesh
   goes off). The engine already knows this shape: `SurfaceMesher`'s `LocalConductorWidth` doc
   comment records the same ratio from a connector cutout on 2026-09-09 (513 cells, 442 of them fan).
   Attractors gave each fan its own c₀, but that comment is explicit that two things did not change —
   the grading rate is "still derived from the global narrowest conductor", and a fan "does not, and
   cannot, stop crossing the part", because an x-attractor refines a column over the full grid height.
   The awkward part is that **edge refinement matters MORE on an antenna, not less**: the radiating
   edges set the effective length, hence the resonant frequency, hence everything. Switching it off
   is not the answer. **ANT-2.**
3. **A wide sheet has no current direction, which is precisely the case the existing field declines.**
   `TransmissionLineMesh` picks its direction from the port vector or the artwork's principal axis and
   **declines** when they disagree by more than 15°. A patch is the ambiguous case by definition. But
   the machinery underneath — `PlanarMeshPitchField`, its Lipschitz smoothing, its aspect cap — is
   right; it is only the direction question that does not apply. **ANT-3.**

### 1c. The adaptive sweep did not converge on the resonance, and said so

The 51-point run with the feed present solved **44** of them and still reported worst disagreement
**|ΔS| = 0.02 against a 0.001 tolerance**. A patch resonance is narrower than a 10 MHz grid, and the
sampler's own documented property is that "it never adds a frequency you did not ask for", so it
cannot rescue a feature between requested points. **ANT-9.**

### 1d. The surface-current map already ships

`PlanarCurrentDensity` → `PlanarCurrentDensityMap` → `EmSetupEditorViewModel.AdoptCurrentDensity` →
`LayoutRenderer.DrawPlanarMeshOverlay(cellScalar)`. The run printed it: *"Surface current density
|J|, 0 … 1.077 A/m (normalised to this map's own peak), port 1 driven at 1 V, 1.45 GHz."* Per-cell
|J| in A/m with Jx and Jy kept complex, one driven port at one frequency, drawn in plan view over the
artwork, with the reduction documented once in the engine next to the basis it reduces.

**The physics is done. What is missing is presentation only** — a frequency picker, a dB scale, and a
phase mode. **ANT-8**, and it is the cheapest item in the series relative to how much it looks like a
feature.

---

## 2. The four limits that bound every claim in this series

Stated once here so no brief has to re-argue them, and so none of them can be discovered by a user
getting a wrong answer.

- **The ground plane is laterally infinite by construction**, and the extractor says so in its own
  note. The measured board's is 70 × 70 mm = **0.40 λ₀** at 1.74 GHz. The real antenna will have
  substantial back radiation and a tilted, rippled pattern this model cannot see. Directivity will
  read optimistic.
- **Every DIELECTRIC LAYER is laterally infinite too, and this is the limit most likely to be
  mis-remembered.** circuitRF does model *drawn* dielectric artwork — MIM-7's patterned film, tied to
  its plate conductor — but that mechanism decides **whether** a layer is in the run, never **where it
  stops**. `PatternedDielectric.Deactivate` says so in its own words: *"A thin-film capacitor's
  dielectric is patterned: it exists under the plates and nowhere else. The 2.5D premise cannot
  express that laterally — inside a run every dielectric is laterally infinite — but it does not force
  the film to be present in EVERY run."* If the plate is one of the analysis levels the film is in the
  stack, everywhere, to infinity; if not, the band becomes air and the stack is rebuilt. A vertical
  dielectric boundary is outside the 2.5-D premise entirely and no amount of layering reaches it
  (`QuasiStaticKernel`'s sloped-boundary refusal). **There is no board edge**, which is why ANT-5 books
  surface-wave power as loss permanently, and why ANT-11's §1 says what it now says.
- **Apertures in the ground plane are not representable at all.** That rules out slot antennas,
  CPW-fed slots and aperture-coupled patches — which is the good way to feed a patch. Edge-fed,
  inset-fed and probe-fed are all fine. Probe-fed is a particularly clean fit: the existing internal
  port type is defined as *between the metal and the ground plane at the point you put the label*,
  which is a coaxial probe feed exactly, with no feed line to de-embed.
- **Kernel B's metal is a perfect conductor.** `SigmaSm` is carried through the whole pipeline and
  never read by the fill. Copper loss is missing, which lands directly on radiation efficiency —
  see ANT-5, which must report it rather than absorb it.

**The stackup on the measured board is intended** (owner, 2026-09-10): the patch references the first
inner plane across 203.2 µm of prepreg, not the full board thickness. h/λ₀ = 0.0012. That is a poor
patch substrate — small radiation resistance, high Q, dielectric loss dominant — and it makes ANT-9
(finding the resonance) and ANT-5's loss itemisation *more* important, not less. It also argues that
the shipped validation example in ANT-12 should be a better-proportioned antenna, with this board kept
as the import regression case it already is.

---

## 3. Why the far field is cheaper than the near field was

Worth stating once, because the instinct from L8a/L9a is that anything involving the layered
Green's function is a schedule risk. **It is not, here, and the reason is structural.**

The research-grade part of this engine — inverting the Sommerfeld integral to reach the *spatial*
domain, DCIM and all of it — exists because the **matrix fill** needs near-field interactions. The
far field needs the **spectral** Green's function at exactly one point per direction,
k_ρ = k₀ sin θ, by stationary phase. That is already sitting there: `SpectralGreens.ReflectionTe/Tm`,
`Kz0`, and `LayeredSpectralGreens` for a general stack. **No DCIM, no Sommerfeld quadrature, no
validated-range refusal.**

The current side is closed form too. `PlanarPortSolution.Currents` is one complex coefficient vector
per driven port, and the 2-D Fourier transform of a rooftop on a rectangular cell is analytic. So the
far field is an exact sum over N basis coefficients with **no quadrature error of its own**, at O(N)
per direction — N ≈ 4,000 over a 1° × 1° hemisphere is seconds.

That is ANT-4, and it is the pivot of the series: ANT-5, 6, 7, 10 and 11 are all post-processes of it.

---

## 4. The series, and the order

Each is a separate file and each is implementable alone. The order is a dependency order, not a
priority order, except where noted.

| | brief | depends on | why here |
|---|---|---|---|
| **ANT-1** | A width-bearing Path is metal | — | Blocking. Every imported board is wrong until it lands. Small. |
| **ANT-2** | Mesh: the detail floor and the bounded edge fan | — | Nothing downstream is usable at 704k unknowns. Fixes every import, not just antennas. |
| **ANT-3** | Mesh: the sheet intent | ANT-2 | The second analysis intent — "this is a radiator", not "this is a line". |
| **ANT-4** | The far field | ANT-1 | The physics. Also **stages the front-to-back refusal.** |
| **ANT-5** | The metrics: directivity, gain, efficiency, beamwidth | ANT-4 | Where the conventions get named once. Owns the metric registry and the staged F/B entry. |
| **ANT-6** | Polarization: Ludwig-3, axial ratio, sense | ANT-4 | Small, and it covers circularly-polarized patches. |
| **ANT-7** | Pattern plots: the dB polar mode and the cuts | ANT-5 | Presentation of ANT-4/5. Rect cuts already work; the polar dB radial axis does not. |
| **ANT-8** | The surface-current map, made usable | — | Independent of everything. Can be taken at any time. |
| **ANT-9** | Finding a high-Q resonance | — | Independent. Engine-only. |
| **ANT-10** | The 3D pattern viewer | ANT-4 | Its own brief on owner instruction. The only item adding a new interaction model. |
| **ANT-11** | Finite ground: the UTD estimate, and **activating** front-to-back | ANT-4, ANT-5 | The honest F/B answer. Research-shaped; last on purpose. |
| **ANT-12** | User docs and a shipped example antenna | most | The user page's Can/Cannot list, and an example that is a good antenna. |

**Staging the front-to-back refusal is the house pattern, not a workaround.** With an analytically
infinite ground plane the field at θ > 90° is identically zero, so F/B is infinite and printing any
number for it would be a lie. `LayeredMedium.CanHost` already states the rule this follows: *deleting
a refusal instead of narrowing it is how a kernel starts silently answering questions it cannot
answer.* So:

- **ANT-4** establishes the physical fact and the consequence for the data model: the θ axis spans
  **0…90° only**, with the reason named. A cube half full of structural zeros invites a plot that
  looks like a measurement.
- **ANT-5** creates the metric registry with `FrontToBackDb` **present and refused**, carrying its own
  sentence and naming the phase that supplies it. Present-and-refused rather than absent, so the Data
  Display picker, the CLI and the exporter are all plumbed on day one.
- **ANT-11** extends θ to 180° and **narrows** that refusal to the case with no ground outline. One
  predicate flips; nothing downstream is re-plumbed.

---

## 5. Standing rules for the whole series

1. **No new result type.** R-res-6 holds throughout: a far-field run adds a `"farfield"` group of
   `DataCube`s to the same `DataSet`, exactly as kernel B added `"planar"`. `DataCube` is already
   N-rank with named unit-bearing axes and slicing, so `[freq, theta, phi, port]` needs nothing new.
2. **Keep the port axis from the first commit**, even though every fixture here is a one-port. It is
   free at construction and it is what makes array pattern synthesis possible later; retro-fitting an
   axis to a shipped cube is not free.
3. **The measurement decides, and a negative result is a result.** Every accuracy claim in ANT-4/5/6
   goes to an independent oracle — see each brief's own gate list — never to a second circuitRF path.
4. **Take expensive measurements in a scratch harness and report the number; do not make it a test.**
   A de-embedded planar point is tens of seconds. Gate the structural property.
5. **Mesher work is gated by mesher tests only** — `SurfaceMesher.Mesh` on a hand-built
   `PlanarProblem` is milliseconds. ANT-2 and ANT-3 need no EM solve at all.
6. **Every phase writes up in the relevant `RESOLVED.md`, never in `CLAUDE.md`.** A `CLAUDE.md` gains
   only what is still true tomorrow — a new invariant, a moved default, a new refusal, a named trap.
7. **Say the limit out loud and refuse by name.** R-mom-17 applies to everything added here. A
   metric that cannot be computed is refused with its reason and its phase, never approximated
   quietly.
8. **The user-facing page and the design note must not contradict each other.** Anything that changes
   `docs/design/mom-engine.md` changes `docs/user/src/reference/mom-engine.md` in the same phase, and
   the Cannot list is part of the deliverable.

## 6. Reading order before starting anywhere in this series

`docs/design/mom-engine.md` §10.1-10.3 (what the kernel is), §10.6 (ports and de-embedding) ·
`src/Engine/Mom/CLAUDE.md` §0-1 (the two kernels, the invariants, and why losslessness is not a
gate) · `docs/user/src/reference/mom-engine.md` §"What can and cannot be simulated" ·
`src/Engine/Mom/SpectralGreens.cs` (`ReflectionTe`/`ReflectionTm`/`Kz0` — the far field's whole
kernel) · `src/Engine/Mom/PlanarExcitation.cs` (`PlanarPortSolution`, the currents everything
downstream reads) · `src/Design/Layout/Em/PlanarExtractor.cs` (what becomes metal, and what does not).
