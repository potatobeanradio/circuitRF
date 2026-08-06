# Sonnet Brief — Phase L9a: the general layered medium (N dielectric layers, arbitrary source and observer heights)

**Design:** `docs/design/layout-view.md` **§10.1** (2.5D MoM: *"laterally infinite, vertically
stratified"* — L9 is the phase where the second half of that phrase becomes true), **§10.2** (the
honest cost, and its two "where a schedule goes to die" markers, both of which L8 measured and neither
of which L9 gets to assume is settled), §10.3.4 (`EmCapabilities.LayeredWithVias` — declared at L6,
read by nothing, and L9's registration point exactly as `Planar` was L8e's), §10.7 (the size budget,
the R17 ceiling, and the measured per-frequency costs L9 will multiply), §11's phase table row **L9**.

**Read, in this order, before planning anything:** `src/Engine/Mom/CLAUDE.md` §L8a end to end — this
slice is its direct generalisation and every trap in it recurs — then §L8b's D8 and D1, then
`src/Ui/Layout/Em/CLAUDE.md` §"Who refuses what". `SpectralGreens.cs`'s own header is the derivation
you are extending; read the file, not a summary of it.

**First of L9's slices.** L9 is *not* one phase's worth of work and must not be attempted as one — see
§0.1 for the split and why the boundaries fall where they do. This brief specifies **L9a only**: the
spectral kernel for an arbitrary stratified medium, with source and observer at arbitrary heights, and
its oracle ladder. **No DCIM change, no mesher, no basis functions, no vias, no ports, nothing in
`src/Ui`.**

---

## Gate command — and it is NOT the full solution

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

L8a's precedent stands and the reasoning is unchanged: a slice that touches only `src/Engine/Mom/` has
a blast radius of `src/Engine/Mom/`, and the full-solution run belongs **once, at the end of L9, as
part of the last slice's gate**. Add `--no-build` after the first build.

**Tagging.** `Category=Benchmark` for anything whose measured wall clock crosses ~5 s, per the root
`CLAUDE.md` rule — and this slice will produce such tests, because a direct Sommerfeld integral is
"accurate everywhere and far too slow to fill a matrix with" and the oracle ladder is built out of
them. L8a tagged 17 that way; expect a similar shape. **The routine `Engine.Tests` tier must stay
under ~60 s**; report the number either way.

**There is no `Hero1BTests` risk in this slice** (it is `Engine.Tests`, and L8e measured it at 8 s
against its own 10 s gate under full-solution load — marginal, and not yours to widen).

---

## 0. Read this before planning anything

### 0.1 L9 is five slices, and this brief is the first

§11's L9 row reads *"DCIM, N dielectrics, vias and z-directed current, adaptive frequency sampling,
N-budget enforcement"*. That is **strictly more work than L8**, which needed five slices, and it
contains at least two items that are separately capable of consuming a quarter. Staging it is not a
preference; it is the same decision §10.2 already recorded for L8, for the same reason.

| slice | scope | why the boundary is here |
|---|---|---|
| **L9a** *(this brief)* | The N-layer spectral kernel; source and observer at arbitrary heights; surface-wave pole location; the oracle ladder | The Green's function is the thing everything else consumes and the thing most likely to be quietly wrong. It is testable with **no mesh, no basis, no solve** — exactly as L8a was |
| **L9b** | DCIM in **two** variables (a fit per source/observer height pair, per frequency), the branch-point behaviour re-derived for the general kernel, and what to do with more than one surface-wave pole | L8a's M4 finding — `1+Γ` vanishing at k_ρ = k₀ making `ΣA_i` a *theorem* rather than a fit — is a statement about **the grounded slab's** Γ. It does not survive generalisation unexamined, and the far field is where DCIM already failed once |
| **L9c** | Vertical current: the **G_A^zz and G_A^zx kernel components L8a never built**, via basis functions, junction continuity between levels, and the multi-level mesher | This is not "add a basis function". A z-directed current needs Green's-function components that do not exist in the repository. L8b's shared tensor grid was designed for it (`PlanarMesh`'s own note) and that part is genuinely free |
| **L9d** | Ports and references: coplanar ground, differential/second-conductor, meshed finite ground pours; narrowing every extractor refusal that points at L9 | `PlanarPortReference.CoplanarGround` and `.SecondConductor` already exist as enum members refused by name. R-gen-9: **narrow, never delete** |
| **L9e** | Adaptive frequency sampling, ACA compression, N-budget enforcement above R17, the refusal audit, and **L9's phase gate** | The cost work only becomes measurable once the thing that is expensive exists, and the gate is the deliverable |

**Write L9b–L9e's briefs after L9a's measurements land**, not now. Every L8 brief cited the previous
slice's numbers; there are no L9 numbers yet.

### 0.2 Three things that are true before you start

**1. Kernel A and kernel B both keep working, unchanged, and that is a hard constraint.** L8 shipped
and is gated. The grounded slab is a special case of what you are building, and the temptation is to
delete `GroundedSlab` and let the general medium serve it. **Do not, in this slice.** Build the general
medium alongside and gate it by *exact* agreement with the shipped slab kernel (D5). Collapsing the two
is a later, separate, measured decision — the L7b-b precedent, where the general modal decomposition
superseded L7b's fixed matrix only after the error against it was measured, and L7b's construction
survived as a test oracle.

**2. "The general layered stack" means N HORIZONTAL layers. It does not mean arbitrary dielectric
geometry.** A vertical or sloped dielectric boundary is outside the 2.5D premise entirely and is not
L9's — no slice of it. `QuasiStaticKernel`'s sloped-boundary refusal currently ends *"A general
dielectric stack arrives at L9"*, which is true as written and **will be read as a promise it does not
make**. Flag it; L9e's audit sharpens it. Do not sharpen it here — this slice changes no user-facing
string.

**3. The cost pressure is already real and L9 makes it worse.** L8c/L8d measured, on §10.7's own hero
(N = 552, FR-4, shipping mesh): 1.73 s per frequency for the DUT alone, **7.66 s per de-embedded
point** once the calibration standards are included (they are 78% of it), and L8e measured **48 s** end
to end through the product path for a single frequency. A 101-point sweep of one bend is ~80 minutes.
L9 multiplies N — multi-level metal and vias add unknowns to the same structure — and multiplies the
DCIM work per frequency by the number of height pairs. **The per-sample cost of your cascade is
therefore a first-class deliverable of this slice, not an afterthought** (§8.2).

---

## 1. Decisions taken

**D1 — The medium is a transmission-line cascade, derived, not transcribed.** Each layer is a section
of two equivalent transmission lines — TM (superscript `e`) with `Z^e = k_z/(ωε)`, TE (superscript `h`)
with `Z^h = ωµ/k_z` — and the spectral Green's function is the voltage `V_i(z|z′)` on those lines due
to a 1 A current source at `z′`. This is the same object L8a already built for one layer; the
generalisation is that the terminating impedances looking up and down from the source are now the input
impedances of a cascade rather than closed forms.

**Nothing is transcribed from a paper.** L8a's rule and its justification carry over verbatim: names in
the literature are *attribution, not provenance*, and what makes the result trustworthy is §4's ladder,
not a citation. Write the derivation out in the file header the way `SpectralGreens` does.

**D2 — Arbitrary termination top and bottom, because the cascade makes it a one-line change.** A
grounded stack terminates below in a short (Z = 0); an open stack terminates in the half-space
impedance. Both are *a terminating impedance* in the same formula, so covering both costs nothing here.

**But the consequence is not free and belongs in the report: an open-below stack has a SECOND branch
point.** L8a's kernel has exactly one, at `k_ρ = k₀`, and that fact is load-bearing (`tan(k_z1 h)/k_z1`
is even in `k_z1`, so `k₁` is not a branch point). A stack that is open below introduces the bottom
half-space's own `k_b`, and a two-branch-cut spectrum is a materially harder DCIM problem. **L9a builds
it and measures it; whether DCIM can fit it is L9b's question and must be measured, not assumed.**
`PlanarExtractor`'s ungrounded-stack refusal stays in place until L9b answers.

**D3 — Source and observer at arbitrary heights, from the start.** L8a's D2 collapsed the problem to
one variable by putting the single metal layer on the slab's top surface. That collapse is what L9
exists to undo, and `SpectralGreens.KernelAtHeights` already carries the general two-height form for
exactly this reason — *"written down once, correctly, for whoever lifts this kernel to two metal
levels"*. **It is the regression target, not a starting point to rewrite.**

**D4 — Locate and COUNT the surface-wave poles; do not assume how many there are.** A grounded slab has
a TM₀ mode with no cutoff (L8a's R-lgf-3). An N-layer stack supports more, and the count depends on
frequency and on the stack. The poles are the resonances of the equivalent TL network — zeros of the
denominator — and they must be found numerically with a stated search strategy and a stated confidence
that none was missed. **A missed pole does not produce an obvious failure; it produces a plausible
kernel that is wrong at large ρ**, which is precisely the failure mode L8a's M4 already burned a
milestone on.

**D5 — The 1-layer reduction is the gate on this slice, at machine precision, against the SHIPPED
kernel.** Not "close to". `SpectralGreens` for the two starter substrates is already validated to
≤ 6e-3 against direct Sommerfeld integration and is what L8's three phase-gate sentences rest on. The
general medium instantiated as one grounded slab must reproduce Γ^e, Γ^h, Γ^q, G̃_A and G̃_q from it to
~1e-13 relative across the whole sampled `k_ρ` range, at several frequencies, on both starters. If it
does not, the cascade is wrong — there is no tolerance to negotiate, because the two are the same
formula.

**D6 — No user-facing string changes, no refusal narrowed, no capability widened.** `EmCapabilities`
gains nothing; `PlanarKernel.CanSolve` still refuses more than one conductor level;
`LayeredMedium.CanHost` still refuses buried metal. The capability is not real until L9c, and R-gen-9's
rule is that a refusal is narrowed when the capability arrives, never in advance of it.

**D7 — No dependency.** L8a wrote its own Bessel functions rather than adding one (§8.3), and got
Y₀/Y₁ to 5.6e-11 via the Wronskian. A general layered medium needs no new special functions at all —
it is complex arithmetic and a loop. If you find yourself wanting a linear-algebra or special-function
package for this, the formulation has gone wrong.

**D8 — The stack used by the oracles is HAND-BUILT in the engine tests, not a new starter technology.**
Both starters today are single-substrate, and L9's own gate will eventually need a multilayer one — but
adding a starter technology is a `src/Ui` change with `.ctech` consequences, and it is L9d/L9e's.
Follow the `EmProblemBuilders` precedent: a couple of stacks defined in `tests/Engine.Tests/Mom/Support/`,
in SI units, with the layer parameters written out.

---

## 2. What already exists, and what genuinely does not

**Exists and is load-bearing — read it before writing anything:**

- `SpectralGreens.cs` — the one-layer kernel, the MPIE formulation-C derivation in its header, the
  `Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h)` algebra with its exact `k_ρ²` cancellation, and
  `KernelAtHeights`, which is the two-height form you are generalising.
- `LayeredMedium.cs` — `GroundedSlab` and `CanHost`, the refusal that names L9.
- `SommerfeldIntegral.cs` — the independent inversion, and the oracle for §4's spatial-domain rungs.
  It is *slow by design*; that is what makes it an oracle.
- `Bessel.cs` — written here, not imported. J₀/J₁/Y₀/Y₁ for complex argument.
- `Dcim.cs` — **do not touch it in this slice.** `Dcim.WithinValidatedRange` is a real decision
  (R-prt-13) and its validated range is a property of the one-layer kernel; it does not transfer.
- `tests/Engine.Tests/Mom/LayeredGreensFunctionTests.cs` — L8a's own ladder, Tier 0 through Tier 4.
  Your ladder extends it; it does not replace it, and none of its existing assertions may be loosened.

**Does not exist, and is not this slice's to build:**

- Any `G_A^zz` / `G_A^zx` component. A horizontal-dipole kernel is all there is. **L9c.**
- Any DCIM fit for more than one height pair. **L9b.**
- Any mesh, basis, port or Ui awareness of more than one conductor level. **L9c/L9d.**

---

## 3. The formulation, stated as requirements

**R-lyr-1 — The layer stack is a first-class type with an explicit termination at each end.** Layers
top-to-bottom or bottom-to-top, but say which, once, and never re-derive it at a call site. Each layer
carries thickness, complex ε and µ. The two ends carry a termination: PEC (short), PMC (open), or a
half-space of stated ε/µ. A `GroundedSlab` is one layer, PEC below, half-space above.

**R-lyr-2 — `k_zi = sqrt(k_i² − k_ρ²)` on ONE stated branch, chosen once.** L8a's branch convention is
in `SpectralGreens`; use the same one and say so. A sign flip here is invisible in the propagating
region and catastrophic in the evanescent one, which is most of the DCIM sampling path.

**R-lyr-3 — Write the cascade in terms of `tan(k_zi d_i)/k_zi`, never `tan` alone.** L8a measured why:
that combination is *even* in `k_zi`, which is what keeps every interior layer's `k_i` from becoming a
branch point, and it is finite as `k_zi → 0`, which is what keeps `k_ρ = k_i` from being a numerical
event. Dividing by `k_zi` somewhere turns an ordinary point into a 0/0. **This is the single most
important line in the formulation and it is easy to lose while generalising.**

**R-lyr-4 — The `k_ρ² → 0` cancellation in `G̃_q` must survive generalisation ALGEBRAICALLY.** In the
one-layer case `Γ^e − Γ^h` vanishes as `k_ρ²` against a `k₀²/k_ρ²` prefactor, and L8a arranged the
cancellation in closed form because the naive implementation **has lost every digit by
`k_ρ = 1e-8 k₀`** (measured: 0.82 absolute error) — which is exactly where the sampling path starts.
The same limit exists for a cascade and the same catastrophe is available.

Pin it the way `T0_4` does, **including an assertion that the naive form IS ruined there**, so the test
cannot quietly stop demonstrating why the cancellation matters.

**R-lyr-5 — Reciprocity in heights is STRUCTURAL, not measured.** `G(z, z′) = G(z′, z)` must hold to
machine precision because of how the expression is written, at kernel A's own standard. If it holds to
1e-8 rather than 1e-15, the expression is wrong even though the number looks fine.

**R-lyr-6 — The pole finder states its search domain and its confidence.** Report, for each stack and
frequency: how many poles were found, where, how close to the real axis, and what was searched. "None
found" is only an answer if the domain searched is stated.

**R-lyr-7 — Everything is per frequency and nothing is cached across frequencies.** `SpectralGreens`'s
header already says this and says why there is deliberately no fill counter implying otherwise. A
layered medium is *more* frequency-dependent, not less.

**R-lyr-8 — Refusals name the specific feature and where the capability arrives**, per R-mom-17. A stack
this kernel cannot represent (a layer with zero thickness, a termination that makes no sense, a
frequency where the search fails) is refused with the feature named — not returned as a NaN.

---

## 4. The oracle ladder — this IS the deliverable

L8a's phase gate was *a measurement*, reported before anything consuming it was built, and that is the
model. **Four of these five rungs need no external data at all**, which matters because §11's L9 gate
sentence asks for something the project's own rules do not permit (§8.4).

**Tier 0 — Structural, per sample, free.**
Reciprocity in heights (R-lyr-5). The `k_ρ → 0` limit and the naive-form-is-ruined counter-assertion
(R-lyr-4). `Γ^q(0) ≠ Γ^e(0)` — L8a asserts this precisely because if they coincided the `k₀²/k_ρ²`
term would be missing and *everything downstream would still look plausible*.

**Tier 1 — The 1-layer reduction, at machine precision (D5).** The strongest single check, and the
direct analogue of L8a's own Tier 1 εᵣ = 1 reduction and of kernel A's coax. Against the **shipped**
`SpectralGreens`, both starters, several frequencies, the whole `k_ρ` range.

**Tier 2 — Split-a-layer invariance. Cheap, exact, and it catches every cascade bookkeeping error.**
One layer of thickness `d` and permittivity `ε` must give a **bit-identical** kernel to two stacked
layers of thickness `d/2` with the same `ε`. Then three. Then an asymmetric split (`0.3 d` / `0.7 d`).
Interface bookkeeping, propagation-direction sign errors and impedance-transform slips all break this,
and none of them breaks Tier 1.

**Tier 3 — The static limit.** As `ω → 0` the scalar kernel must reproduce the multi-layer electrostatic
Green's function, which is an image series with a stated closed form. **L8a's own warning applies
directly: the static image series had to be COMPLEX, and getting that wrong looked exactly like a
kernel bug.** Read `src/Engine/Mom/CLAUDE.md` §L8a's "Two findings about the ORACLES themselves" before
writing this rung; that is the second of the two occasions the *oracle*, not the method, was wrong.

**Tier 4 — Direct Sommerfeld integration on a genuinely multilayer stack, and this rung is the
REPORTED MEASUREMENT.** The spatial-domain kernel over `ρ/λ` and over height pairs, against
`SommerfeldIntegral` driven through the general medium. Report the error as **a fraction of the
free-space kernel at the same ρ** — that is what a matrix fill actually experiences, and it is the
measure L8a's ≤ 6e-3 is quoted in — as well as strict relative error, and say where each stops being
trustworthy.

**Note what Tier 4 does and does not establish.** It shares the spectral kernel with the thing under
test, so it validates the **inversion**, not the kernel. Tiers 1–3 are what validate the kernel. Say
this in the test file; L8a's ladder says the equivalent thing about its own rungs and it is why the
ladder has five entries instead of one.

**A warning that has now cost this area three milestones: check the oracle before concluding the method
is wrong.** L8a records two occasions where the oracle was the thing at fault, L7b-b a third, and the
L8e phase gate a fourth (a fixture chamfering the wrong corner, which read as 0.98 reflection and
looked exactly like a solver defect). When Tier 3 or Tier 4 disagrees, **the first hypothesis is the
rung, not the cascade.**

---

## 5. What must NOT be built here

- **Any DCIM change.** Not a two-variable fit, not a re-derived branch-point theorem, not a new pole
  handling strategy. `Dcim.WithinValidatedRange`'s range is the one-layer kernel's and must not be
  silently widened by a kernel change underneath it. **L9b.**
- **`G_A^zz`, `G_A^zx`, via bases, junction continuity, any mesh change.** **L9c.**
- **Ports, references, finite ground pours, extractors, `.cem`, anything in `src/Ui`.** **L9d.**
- **Adaptive frequency sampling, ACA/MLFMM, N-budget changes.** **L9e.** Note that §10.7's *"build it
  only when the kernel that needs it exists"* was already superseded at L8c — the kernel exists — so
  L9e inherits a stronger case, not a weaker one. It is still not this slice's.
- **A losslessness check.** L8a wrote the warning, L8d and L8e honoured it, and it is *more* true with
  more layers: an open stratified structure radiates and launches surface waves, so |S₁₁|² + |S₂₁|² < 1
  legitimately. Reciprocity and passivity carry over; losslessness does not.
- **Any gate tighter than L8d's measured radiative floor** (|S₁₁| = 3.9e-4 at 2 GHz, 6.0e-3 at 10 GHz
  on 1.6 mm FR-4, scaling f²) on anything that becomes an s-parameter. Not directly this slice's
  concern, but it constrains what you may promise in the report.
- **Deleting or widening any refusal**, per D6 and R-gen-9.
- **A new starter technology**, per D8.

---

## 6. Milestones, each with its own gate

| | content | gate |
|---|---|---|
| **M1** | The stack type, the terminations, the cascade | **Tier 2** green — split-a-layer invariance, bit-identical, including the asymmetric split |
| **M2** | `G̃_A` and `G̃_q` at arbitrary heights | **Tier 1** green at ~1e-13 against the shipped kernel, and **Tier 0**, including the naive-form counter-assertion |
| **M3** | The pole finder | The slab's TM₀-with-no-cutoff and the first TE mode's cutoff reproduced; pole counts reported for both starter stacks and for the hand-built multilayer one |
| **M4** | Spatial domain | **Tier 3** and **Tier 4** — and Tier 4's error curve WRITTEN DOWN, over `ρ/λ` and over height pairs, before anything is claimed about it |
| **M5** | Cost | Per-sample cascade cost against the slab's closed form, and the projected DCIM sample budget (§8.2) |

**M1 and M2 are the ones with a wrong obvious answer** (R-lyr-3 and R-lyr-4). M4 is the one that takes
the time.

---

## 7. File map (indicative)

```
src/Engine/Mom/
  LayeredMedium.cs        + LayerStack / Termination / the cascade.  GroundedSlab STAYS and is
                            untouched — D5 gates the new path against it, and nothing switches over
                            in this slice
  SpectralGreens.cs        the general kernel alongside the one-layer one; KernelAtHeights is the
                            two-height form to generalise, not to replace
  SurfaceWavePoles.cs     + the pole finder (D4/R-lyr-6), with its search domain stated

tests/Engine.Tests/Mom/
  LayeredGreensFunctionTests.cs   L8a's ladder — EXTEND, loosen nothing
  GeneralLayeredMediumTests.cs  + Tiers 0-4 for the general medium
  Support/LayerStacks.cs        + the hand-built multilayer stacks (D8), SI units, parameters spelled out
```

Nothing outside `src/Engine/Mom/` and `tests/Engine.Tests/Mom/` should change. If something does, that
is a finding worth reporting, not a step to take quietly.

---

## 8. Four things to report back on, whatever else happens

1. **Tier 4's error curve as numbers** — as a fraction of the free-space kernel and as strict relative
   error, over `ρ/λ` and over source/observer height pairs, on a real multilayer stack, and **where it
   stops being trustworthy in each variable**. This is the L8a analogue and it is the deliverable: it
   is what L9b's DCIM work will be scheduled against, and it must exist before anything consumes it.

2. **The per-sample cost of the cascade against the slab's closed form, and what that projects to.**
   DCIM samples the spectrum thousands of times per fit, per frequency — and at L9b, per height pair.
   If the cascade is 20× a closed form, the fit budget changes shape and L9b's brief has to be written
   differently. Measure it; do not estimate it.

3. **How many surface-wave poles the stacks actually have, and whether any sits close enough to the
   real axis to matter.** Include the two starter substrates (where the answer is known and is a check
   on the finder) and the hand-built multilayer stack (where it is not). Say what domain was searched.

4. **Whether §11's L9 gate sentence can be satisfied under this project's own rules, and if not, what
   should replace it.** The row reads *"agreement with published reference structures"* — and the
   standing rule everywhere else in this area is that a gate is never built on numbers whose provenance
   cannot be checked (Tier C3; §10.9's *"prefer self-consistency checks that need no external tool"*;
   the L8e bend gate used Kirschning-Jansen-Koster only because its source was read directly and its
   inputs are verifiable; and `docs/design/layout-view.md` §10.9 requires golden data to be
   **owner-approved before it becomes a gate**). Published S-parameters for a multilayer structure
   usually arrive without a verifiable stackup, and a gate built on them measures the transcription.
   **Say plainly whether the sentence survives, and propose the replacement if it does not** — that is
   an owner decision, and the right moment to surface it is now, not in L9e when the gate is due.
