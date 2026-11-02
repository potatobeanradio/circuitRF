# Brief CL1 — the surface-impedance term in the full-wave fill

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl1-n` · **Area:** `src/Engine/Mom`

**Read first**, end to end, before planning anything: `src/Engine/Mom/CLAUDE.md` §3.3 (the fill),
§3.5 (kernel B traps), §5 (validated ranges) and §11 (the AIM decision gate); `PlanarFill.cs`'s
header and `Fill` / `FillMultiLevel` / `PlanarEntryFill`; `PlanarDcSolve.cs`'s header in full;
`RlgcExtractor.cs:53-140` (the oracle you will be measured against); the series overview §1 and §3.

---

## The problem

`PlanarConductorLayer.SigmaSm` and `ThicknessM` reach `PlanarProblem` and are never read by the
fill. The metal is a PEC. On the shipped MMIC technology that omits 92-99% of the line's loss
(overview §0).

## The physics, stated once

A surface-impedance boundary condition turns the EFIE into

```
Z_s(ω)·J(r) + jωA(r) + ∇Φ(r) = E_inc(r)
```

which adds exactly one Galerkin term:

```
Z[m,n] += Z_s(ω, layer of m) · ⟨f_m , f_n⟩          when m and n share a layer
```

**`⟨f_m, f_n⟩` is a real, frequency-independent, sparse Gram matrix over the rooftop basis** —
nonzero only where two rooftops share a cell, so O(N) entries with a small constant. Build it once
beside the cores; scale it by a scalar per frequency. It is symmetric, so R-fil-2's `m ≤ n`-and-mirror
construction and P7's complex-symmetric factorisation are both untouched.

**The scalar, for a conductor of thickness t carrying current on both faces:**

```
Z_s(ω) = (η_c/2)·coth(γ_c·t/2),     η_c = (1+j)/(σδ),  γ_c = (1+j)/δ,  δ = √(2/ωµ₀σ)
```

Two limits, and **both are load-bearing rather than decorative**:

- `t ≫ δ` → `(1+j)/(2σδ)`, two surfaces in parallel.
- `t ≪ δ` → `1/(σt)`, which is **exactly `PlanarDcSolve.SheetResistance`** (`PlanarDcSolve.cs:418-421`).
  So the AC term walks continuously into the DC point the kernel already computes, and LF2's
  conduction-substitution band below the DCIM fit floor stops being a discontinuity in α. R-cl1-6
  is that gate.

**What this form cannot carry, said here rather than discovered later.** It is exact for symmetric
excitation and is the standard single-unknown reduction otherwise. A microstrip's substrate-side
face carries far more current than its air-side face; one unknown per location cannot hold two
independent face currents, and the asymmetry is precisely the information a single sheet loses.
Overview §3 has the measured size of the related thickness-blindness. **R-cl1-9 is how much it
costs in practice.**

## Milestones

1. **`PlanarSurfaceImpedance`** — a new small file. `Zs(sigmaSm, thicknessM, omega)` returning
   `Complex`, plus `SkinDepthM`. **σ ≤ 0 or t ≤ 0 returns `Complex.Zero`** (PEC), which is what
   every other part of the kernel already means by those values (`PlanarDcSolve.cs:66-81` states the
   same convention) and is what makes R-cl1-4's PEC reproduction exact rather than approximate.
   Write the derivation and both limits in the file header, as L8c did.
   **Gate (R-cl1-1):** the two asymptotes to 1e-12 relative, and a `coth` that is stable at large
   `|γt/2|`. The naive `(e^{2z}+1)/(e^{2z}-1)` does not overflow until `t/δ > 710` and **no shipped
   stackup reaches that** — 105 µm copper at 40 GHz is 318 — so this is a precision question, not a
   crash: past `t/δ ≈ 40` the ratio is 1 to every bit that matters and the subtraction in the
   denominator is pure cancellation noise. Return the large-argument branch there rather than
   computing it.

2. **The Gram matrix**, built in `PlanarFill.BuildCores` beside the existing cores and carried on
   `PlanarFillCores`. Closed form on rectangles. **Cut cells** (`PlanarCellRegion != null`) go
   through `PolygonIntegrals` — `Area` and `AreaMoment` exist; the ramp×ramp self-term needs a
   second moment, which is the same Green's-theorem contour and is the only new closed form in this
   brief. **Cross-layer pairs contribute nothing** and same-layer pairs on different levels use that
   level's own `Z_s`.
   **Gate (R-cl1-2):** `⟨f_m, f_m⟩` against a dense 2-D quadrature of the rooftop on a rectangle and
   on a cut cell, 1e-12 relative. Report the nonzero count as a multiple of N on the hero and on the
   60 mm taper — if it is not O(N) with a constant in the single digits, something is wrong with the
   pairing, not with the integral.

3. **The two fill seams.** `PlanarFill.Fill` (`PlanarFill.cs:1911`) and `FillMultiLevel` (`:2463`)
   add the term after their direction blocks and **before** `MirrorLowerToUpper`.
   `PlanarEntryFill.At` (`:3900`) adds the same expression, **written once and called from both** —
   `PlanarPairClasses.cs:42` requires those two to be bit-identical and that requirement does not
   relax here.
   **In the AIM path the term goes into `nearExact` only** (`PlanarAim.cs:927`), never into
   `AimEntry`: `Z_s` is not a Green's-function interaction and the grid product must not claim it.
   The correction at `:940` then carries it, and `FactorNear` at `:968` puts it in the
   preconditioner for free. **Every overlapping-support pair is already in the near set** — the near
   set is the union of a radius and stencil overlap (`PlanarAim.cs:53-57`), and two rooftops sharing
   a cell overlap — so nothing is lost off the end of the sparse pattern. **Assert that**, do not
   assume it: R-cl1-3 is a test that every Gram nonzero has a CSR slot.

4. **Via bases.** A z-directed basis is a barrel, not a sheet: its series impedance is
   `ℓ·(1+j)/(σ·δ·P)·coth(...)` over the barrel perimeter P, not `Z_s` over a plan-view area. **The
   σ lookup already exists** — `PlanarDcSolve.ViaSigma` (`:399-416`) resolves per-basis via
   conductivity including `PlanarGroundPath`'s synthesised cells and the ground-attachment case.
   Reuse it; do not write a second one. A ground-attachment basis's length is
   `PlanarLevels.AttachmentLengthOf`, already used at `PlanarDcSolve.cs:390`.
   **Gate (R-cl1-5):** a single through-via's DC resistance from the AC path at the lowest swept
   frequency matches `PlanarDcSolve`'s to 1%.

5. **The flag.** `PlanarFillSettings.ConductorLoss`, **default `false` for this brief**, so every
   existing golden is untouched and CL1 lands green without a re-bless. It stays in the codebase
   afterwards as the PEC oracle, exactly as `UseSymmetricFactorization = false` and
   `UseRadialTable = false` are kept. **It does not reach `EmSetupPersistence` and there is no
   `.cem` key** — overview §5.

## Gates

- **R-cl1-4 — PEC reproduction, bit for bit.** With `ConductorLoss = true` and every layer at
  σ = ∞ (or t = 0), the filled matrix is **bit-identical** to today's on the hero, the 60 mm taper
  and a multi-level MIM fixture. Not 1e-12: identical. `Complex.Zero` added to an entry changes no
  bits, and if it does, the term was inserted in the wrong place.
- **R-cl1-6 — the DC limit.** On the FR-4 and GaAs starters, `Re(Z_s)` at the sweep's lowest
  fittable frequency is within 1% of `PlanarDcSolve`'s `1/(σt)` for that layer, and the solved α
  crosses LF2's substitution boundary without a step. **This is the gate that catches a factor of
  two in the coth**, which nothing else here would notice.
- **R-cl1-7 — against kernel A, the independent oracle.** A uniform 50 Ω microstrip on both
  starters, ground held PEC in BOTH kernels (kernel A via `EmGroundPlane(0, ∞)`), α_c from kernel B
  against `RlgcModel.RMatrix`'s strip-only term. **State the agreement; do not tune to it.** The
  series overview's measured strip-only R at 10 GHz is 8.19 Ω/m (FR-4, w = 3020.28 µm) and
  382.56 Ω/m (GaAs, w = 70.72 µm) — those are the numbers to land beside.
- **R-cl1-8 — nothing else moved.** With the flag off, the entire `Engine.Tests` planar suite is
  unchanged. With it on and σ = ∞, R-cl1-4.
- **R-cl1-9 — THE EDGE CONVERGENCE GATE, and the one that decides the series' future.**
  Sweep `EdgeCells` 0 / 2 / 3 / 5 / 8 at fixed `CellsPerWavelength` on both starters and tabulate
  α_c. A zero-thickness sheet's `∫R_s|J|²` is log-divergent in edge refinement (overview §1); the
  loaded operator should converge, and the question is how fast and to what.
  **Report the table, the limit if there is one, and the deviation from kernel A at each rung.**
  **Do not tune `EdgeCells`, `Z_s` or the edge cell width to close a gap.** If α_c is still moving
  by more than a few per cent per refinement rung at `EdgeCells = 8`, **say so plainly and stop** —
  that is the measured argument for thick metal, it is a legitimate outcome of this brief, and
  inventing an edge correction to hide it would destroy the only evidence that decides the question.

## Must NOT

- Change any quadrature rule, singular extraction, radial table, panel clustering or closed form.
- Add `Z_s` to `AimEntry`, to the far-field grid product, or to `PlanarStaticAim` — the static
  capacitance solve is a DIFFERENT matrix at ω → 0 and has no `Z_s` in it.
- Touch `PlanarDeembed`'s algebra. The calibration lines get the term through the same `Fill` they
  already call, so γ gains its α_c and the two-line peel is unchanged. **If a de-embedded result
  moves more than the overview predicts, that is a finding to report, not a thing to correct here.**
  > **SHIPPED 2026-09-14, and this bullet's second sentence has since become conditional.** QSC
  > landed the same day and supplies γ from the standard's ELECTROSTATICS below a per-stack
  > crossover, where there is no `Fill` to get the term through — so "γ gains its α_c" holds only
  > above it, which on the MMIC starter is above 26.07 GHz. It changed nothing for CL1, whose flag
  > is off and whose own gates read γ from the two-line extraction directly. It is CL3's to answer:
  > `brief-conductor-loss-3-default-and-gate.md` §0.
- Touch the ground termination. That is CL4 and it is a Green's-function change.
- Add a `.cem` key, a UI control, or a `PlanarSolveSettings` member.
- Write a second σ lookup, a second sheet-resistance formula, or a second `coth`.

## Cost

Per frequency the term is O(N) complex multiply-adds against an O(N²) fill and an O(N³)
factorisation, over a matrix already allocated. **It should be unmeasurable.** Report fill and
factor wall clock before/after at the hero's N in `HISTORY.md` anyway — if it is measurable,
the Gram matrix is being rebuilt per frequency, which it must not be.

**No new `Category=Benchmark` test.** R-cl1-9's sweep is five meshes on two starters and belongs in
the routine gate if it lands under ~5 s; measure it and tag it only if it does not.

## On completion

- `src/Engine/Mom/RESOLVED.md` gains a `§CL1` section: the derivation, the two limits, R-cl1-9's
  table **in full**, the kernel-A comparison, the nonzero-count multiple, and every trap found.
- `docs/design/mom-engine.md` §10.9's conductor-loss blockquote gains a `> Built at CL1` note with
  the measured agreement. That file is a design note, not a `CLAUDE.md`, and is edited normally.
- **Do not write to any `CLAUDE.md`.** `src/Engine/Mom/CLAUDE.md` §5's conductor-loss bullet and
  §7's entry will have gone stale — **report them to the owner by file and line** in the completion
  message instead.
- The user-facing page is CL3's, not this brief's.
