# `src/Engine/Mom` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Ui/DataDisplay/RESOLVED.md` and `src/Ui/Layout/Em/RESOLVED.md`.

## `brief-em-deembed-ceiling-closeout.md` — the de-embedded path's OWN dense ceiling (2026-08-14)

Closes `brief-em-aim-ceiling.md`'s own §13 closing subsection ("The limitation this surfaced"): the
accelerated ceiling (`SurfaceMesher.AcceleratedUnknownCeiling = 12,000`) moved for the DUT's own
N×N basis system, but `PlanarDeembed.StaticCapacitance` — the m×m CELL system D7's reference
impedance needs, computed once per calibration standard — is a separate, always-dense computation
that was never wired to refuse honestly. On the owner's own reported taper (`brief-em-aim-ceiling.md`
§0; Z1 = 6.92 Ω, Z2 = 100 Ω, L = 28.575 mm), turning Accelerated solve on as the panel's own refusal
instructed produced a mesh that passed (N = 6,581) and a DUT Z-matrix that actually solved, then a
throw twenty REAL MINUTES later out of `PlanarDeembed.CapacitancePerMetre`, once the calibration
standard's own static-capacitance solve (N = 6,466 — 98% of the DUT's own N, because D4 makes a
standard reproduce the DUT's transverse gridlines verbatim) finally ran into
`PlanarFill.BuildCores`'s dense guard. R17's own contract has never been "there is a ceiling" — it
is "surface the predicted N before solving, and refuse politely above it", and this run predicted,
passed, and failed late — the one thing that contract exists to prevent.

**No physics changed.** D6/D7's algebra, `SpectralGreens.cs`, `Dcim.cs`, `SingularExtraction.cs` and
`PlanarAim.cs` are all untouched. This is a refusal-timing fix, a message-accuracy fix, and one
measurement that came back negative.

### C1 — refuse at setup, not twenty minutes in

`PlanarSolve.cs`'s `if (st.Deembed)` block already assembled every calibration standard's own
`Mesh.Bases.Count` into a `sizes` list and reported the ratio against the DUT's own N — the
prediction R17 wants was present, correct, and simply unenforced. A loop added right after that
list is built (before any calibrator's raw solve, before any dense fill) now checks each standard's
own N against `SurfaceMesher.UnknownCeiling` and throws `InvalidOperationException` naming:

- **why the accelerator does not help** — D7's static ω → 0 capacitance solve is a structurally
  different m×m system over cells, not the N×N frequency-domain basis system
  `PlanarFillSettings.Aim` covers, so turning it on (which the panel just told this same user to do)
  does not move THIS ceiling;
- **the actionable remedy, with what it costs** — turn de-embedding off and read the raw solve; those
  s-parameters include the port discontinuity rather than being the structure's own response
  (`docs/design/layout-view.md` §10.6: "A raw port excitation includes the port discontinuity;
  reporting those s-parameters as the structure's response is simply wrong"), so this is named as a
  diagnostic-only fallback, not a second success case;
- **both N's** — the DUT's own mesh count and every calibration standard's own count, not just one.

No mesh remedies (`Lower Cells per wavelength`, `turn the edge mesh off`, …) are offered — the
parent brief's own §0 already measured them inert on this class of geometry (width-ratio-driven N
growth), and naming an inert remedy is the exact defect `brief-em-aim-ceiling.md` closed once
already for the dense mesh refusal.

**Gate 1**, `EmCeilingRefusalTests.Gate1_TheOwnersReportedTaper_DeembedOn_AcceleratedOn_RefusesAtSetup_NotAfterADenseFill`:
the owner's exact reported taper, de-embedding ON, accelerated ON, through the real entry point
(`PlanarSolve.Run`) — now refuses in **88 ms** (measured), naming the standard's N, the reason the
accelerator does not help, and the de-embed-off remedy with what it costs. Before this brief the
identical call sequence appeared to succeed and only failed twenty real minutes in.

### C2 — the guard at `StaticCapacitance`'s own call site was measuring the wrong allocation

`PlanarFill.BuildCores`'s shared `GuardCeiling(n)` quotes an **n×n dense complex matrix** because
that is what its OTHER callers (`PlanarFill.Fill`, `PlanarSystem.Build`) go on to allocate.
`PlanarDeembed.StaticCapacitance` never builds one: `PlanarFill.ScalarPotentialMatrix` returns an
**m×m** `Mat<Complex>` over CELLS (D4's potential-coefficient matrix P), and `StaticCapacitance`
then builds a *second* m×m `Mat<Complex>` from it and factors that — so the megabytes the shared
guard's own message would quote are not the cost of what is actually about to be allocated at this
call site. This is §7's own "quotes 381 MB against a real run cost of ~607 MB" defect in a second
location.

Measured on a real calibration standard (`EmDeembedCeilingTests.C2_MVsN_OnARealCalibrationStandard_IsMeasuredNotAssumed`,
a 6 mm FR-4 line's own port, coarse mesh): **m = 32 cells, n = 52 bases, m/n = 0.615** — a standard
is long and thin (few cells across the port width, many along its length), which is why the ratio
sits above the square-grid asymptote of 0.5 rather than at it; either way m is always strictly under
n. On a synthetic mesh sized to straddle the dense ceiling the OLD way but not the new one
(`EmDeembedCeilingTests.C2_TheGuardAtThisCallSite_QuotesCellsAndTheRealMegabytes`, m = 4,500 cells,
n = 8,855 bases): the shared guard's own n×n formula would quote **1,196.5 MB**; what this call site
actually holds (two m×m complex matrices) is **618.0 MB** — corroborated by a real
`GC.GetAllocatedBytesForCurrentThread()` measurement on an actual solve
(`C2_TheFormula_IsCorroboratedByAMeasuredAllocation`), not left as arithmetic alone.

**Fix**: `PlanarDeembed.GuardCapacitanceCeiling(mesh)`, called at the top of `StaticCapacitance`
before `PlanarFill.BuildCores` runs. **The threshold is UNCHANGED** — it still asks
`n > SurfaceMesher.UnknownCeiling`, exactly what `BuildCores`'s own shared guard asks, per the
brief's own instruction not to tighten or loosen the shared question for every caller. Only the
MESSAGE differs: it names the cell count, the two-m×m-matrix accounting, and says explicitly that
this solve never builds the n×n matrix the shared guard's own wording describes.

### C3 — is the ω → 0 static system real? Measured, and the answer is no

The premise this milestone existed to test: if `PlanarKernelTerms.StaticScalar`'s output is
genuinely real (as its own doc comment's "there is no logarithm and no linear term" language might
suggest), `StaticCapacitance` could move from `Mat<Complex>` + a general LU to `Mat<double>` + a
symmetric factorisation — roughly 4× less memory, i.e. 2× the reachable linear dimension, which
could be the difference between refusing the owner's own 6,466-cell standard and running it
comfortably.

**Measured, not assumed, and it fails.** `StaticScalar`'s image ratio is
`k = (1 − εᵣ*)/(1 + εᵣ*)`, and `εᵣ* = εᵣ(1 − j·tanδ)` is complex for any lossy substrate — which is
every starter this repository ships (`Fr4Starter`: εᵣ = 4.4, tanδ = 0.02; `GaAsStarter`: εᵣ = 12.9,
tanδ = 0.002). `EmDeembedCeilingTests.C3_TheStaticScalarKernel_HasAMateriallyNonzeroImaginaryPart_OnALossySlab`:

| slab | tanδ | `Inverse.Imaginary / Inverse.Real` |
|---|---|---|
| FR-4 | 0.02 | **1.63e-2** |
| GaAs | 0.002 | **1.86e-3** |

Neither is floating-point noise (machine epsilon would read ~1e-16) — both scale with the
substrate's own tanδ, because they come directly from it. Carried through an actual solve on a real
calibration standard (`C3_TheDiscardedImaginaryPart_IsMaterial_OnARealCalibrationStandard`): the
total `StaticCapacitance` sums before its own `.Real` truncation has `|Im/Re| = 1.76e-2` — consistent
with the term-level ratio, confirming the discarded part is not merely an input-level artifact that
cancels during the solve.

**Why this closes the door on the representation win, not just narrows it**: the per-cell-pair
remainder term (`PlanarKernelTerms.Remainder`, the convergent image series) mixes powers of the
SAME complex `k` with REAL, ρ-dependent weights that differ per cell pair — so the resulting matrix
is not of the form `A_real·(1 + jα)` for one global loss factor α (which is the one shape where
`Re(A⁻¹b) = Re(A)⁻¹b` would hold). The matrix's complex structure is genuinely entry-dependent.
Converting to `Mat<double>` from `Re(A)` would therefore change the published C_pul, not merely
its representation — failing R-dcl-5's bit-comparison requirement outright, not by a measurable-but-
small margin. **No representation change was made.** `_cPerMetre`'s existing once-per-calibrator
cache (`PlanarPortCalibrator`'s own `if (double.IsNaN(_cPerMetre))`) already holds this cost to one
solve per standard per sweep and needed no change.

### C4 — the decision

**The de-embedded path's effective ceiling is `SurfaceMesher.UnknownCeiling` (5,000), asked of every
calibration standard's own N, exactly as it is asked of the DUT's dense path** — the accelerated
ceiling never applies to this step and, per C3, cannot be widened by a cheaper representation
either. For the class of geometry that motivated the accelerator (width-ratio-driven N growth), a
standard reproduces the DUT's own transverse gridlines verbatim (D4), so **a wide-port DUT's
calibration standard is typically comparable in size to the DUT itself** — on the owner's own file,
98%. This is not a separate, narrower "wide port" concept: it is the same N the DUT's own mesh
report already predicts, applied a second time to the standard.

**What this brief delivers**: the run now tells the user this honestly, in seconds, with a remedy —
turning the accelerated solve on genuinely helps the DUT's own Z-matrix and is offered as it should
be; de-embedding a wide-port part past the dense ceiling is not currently possible, and the run says
so instead of appearing to succeed and failing twenty minutes later.

**If `PlanarDeembed.StaticCapacitance` is ever accelerated, it is its own brief, not built here.**
It would have to compress an **m×m system over mesh CELLS**, under a **static (ω → 0), genuinely
complex kernel** (C3: not reducible to a real one for any lossy substrate) — not the **N×N system
over rooftop BASES** under a **frequency-dependent DCIM-fitted kernel** that
`PlanarFillSettings.Aim` already accelerates. AIM's own near/far split and auxiliary-grid
interpolation are built around the basis geometry and the DCIM fit's height dependence; neither
transfers directly to a cell-indexed static Green's function whose singular structure is the
`(1 + k)/(4πρ)` term plus a convergent image series, not DCIM's exponential fit. Whether a
hierarchical/low-rank compression that stays genuinely complex is worth its own engineering cost
is undecided and unstarted.

### What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarSolve.cs` — a setup-time refusal loop in the `if (st.Deembed)` block, right
  after the existing `sizes`/`totalN` computation.
- `src/Engine/Mom/PlanarDeembed.cs` — `StaticCapacitance` calls a new private
  `GuardCapacitanceCeiling(mesh)` before `PlanarFill.BuildCores`; same threshold, corrected message.
- `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` — new gate 1 test; the existing
  `…ACTUALLYSOLVES…` benchmark test's doc comment now states explicitly (§10.6) why the DUT's raw
  solve it gates is not this user's published success case.
- `tests/Engine.Tests/Mom/EmDeembedCeilingTests.cs` — new: the C2 m-vs-n measurement and guard-message
  gates, and the C3 negative-result measurements.
- Nothing in `PlanarDeembed.cs`'s D6/D7 algebra, `SpectralGreens.cs`, `Dcim.cs`,
  `SingularExtraction.cs` or `PlanarAim.cs` changed.
