# `src/Engine/Mom` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Ui/DataDisplay/RESOLVED.md` and `src/Ui/Layout/Em/RESOLVED.md`.

## The internal port — a port from the metal to the ground plane (2026-08-25)

The third of §10.6's port types, and the one the design note had written down as *not* built with a
costing attached. That costing was wrong in the encouraging direction, and the reasons are worth
keeping.

**`PlanarPortKind.Internal`.** The port is placed on the metal and means "here, referenced to
ground"; its + terminal is the metal, its − terminal is the stackup's ground plane. It drives the
ground-attachment bases of the via under it — every cell of that footprint, since they are one
conductor at one potential.

**Findings.**

1. **The excitation is D1's after all, and the design note's stated reason for doubting that was
   about the wrong integral.** §10.6 recorded that an attachment basis "has a different
   normalisation… `NetCharge` is exactly −1, and L9c's `∫∇·f dS = 0` explicitly does not hold for
   it. The incidence matrix and the reaction integral both need their own derivation." The charge
   facts are true and are properties of the FILL, which was already built and gated; a delta gap's
   reaction is an integral against the CURRENT, and the attachment basis carries unit total current
   across the connection it spans exactly as a rooftop does across its shared edge. So
   `⟨f_m, E^imp⟩ = v` holds exactly, with no footprint area and no quadrature in it, and the port is
   one more ±1 row of the same incidence matrix. **No new basis, no new excitation, no new algebra.**

2. **THE SIGN WAS DERIVED WRONG AND THE MEASUREMENT CAUGHT IT.** The first implementation took
   `IncidenceSign = −1`, from a written derivation of which lip of the gap is the + terminal (the
   impressed field points from + to −, so the + lip is the one on the −f side, so a downward-flowing
   positive current puts + on the metal…). It produced a complete, plausible s-matrix with |S₁₃|
   right to two figures and **every term through the port turned by π**. The convention is `+1` —
   the same "current flows into the structure" rule every other port here uses.
   **A port's sign is unobservable through any termination**: every reduction carries `S_i3·S_3j`
   and both factors flip together, so no amount of loading the port could have shown it. What shows
   it is a structure whose answer is known independently — a short line with a via to ground at its
   centre is three 50 Ω ports meeting at one node above the plane, S_ii = −1/3 and S_ij = **+2/3**.
   That gate exists (`InternalPortTests.ASmallStructureAtLowFrequency…`) and it is the reason the
   released sign is right.

3. **A centred internal port measures S₁₃ = +S₂₃**, against the centred delta gap's S₁₃ = −S₂₃.
   The two gates sit beside each other on purpose: it is the measurement that distinguishes a
   ground-returning port from a series one, and a magnitude plot never would.

4. **Shorting the port reproduces the plain board**, to < 1e-9 — reducing the 3-port with Γ₃ = −1
   against an ordinary 2-port solve of the same artwork. A port is a gap with a source in it, so
   terminating that gap in a short is the structure with no port on it: an end-to-end oracle for
   everything between the incidence row and the published matrix, needing no external data.

**The via is the SOLVER's to build (owner, same day).** Requiring the user to draw a via first was
the original build and was wrong for the workflow: the port is placed on the metal, and whether the
drawing happens to have a via there is a question about the drawing. `PlanarGroundPath` runs before
meshing, beside R-fed-1's feed extension and by the same three rules — a drawn via wins, a missing
one is built, and what was built is reported by port and by size. The problem reaches the mesher **by
reference** when nothing was added, so a board that draws its own vias is bit-identical.

**Its size is a PROCESS dimension, not a mesh cell**, and that choice is load-bearing: the built
via's inductance is part of the port's answer, so sizing it from the mesh would make the answer a
function of *Cells per wavelength* — refining the mesh, the one thing a user does to converge a
result, would move it for a reason unrelated to convergence. The order is the technology's default
via drill, then its default pad, then (Ui-side, reported as the rule of thumb it is) a quarter of the
substrate height. The stackup's own `Via` entries carry a fill, a wall and a span but **never a
diameter**, so "this technology declares no via" is not a reason to refuse the port.

**Cost of the four physics gates: they are `Category=Benchmark`** (5.5–11.5 s each, measured). Not
because they measure time — they assert physics — but because a via-bearing fill costs seconds per
frequency whatever the mesh: the DCIM fit count for a problem carrying vertical current is a fixed
per-frequency cost, so shrinking the board does not shrink it. The default gate keeps the resolution,
the refusals and a solve-free incidence-row wiring test, which is where a change to this area breaks
first.

**`src/Engine/Mom/CLAUDE.md` §3.4 still says "`PlanarPortKind` — TWO port types" and lists the
kinds; that sentence is superseded by this entry.** It was left alone deliberately (this repository's
standing instruction is that findings go here, never into a `CLAUDE.md`).

### What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarPort.cs` — the third enum value, `Direction`/`IncidenceSign` for it,
  `PlanarPortResolution.FootprintAreaM2`, `TryResolveInternal` (footprint walk by 4-connectivity,
  two refusals), and its own `Describe`.
- `src/Engine/Mom/PlanarGroundPath.cs` — new: the path the port grows for itself.
- `src/Engine/Mom/PlanarKernel.cs` — one call, before the feed extension, on both mesh paths.
- `src/Engine/Mom/PlanarFeedExtension.cs` — gated on `Kind != Edge` rather than on the gap alone.
- `src/Engine/Mom/PlanarSolve.cs` — the not-de-embedded note now lists the two internal kinds apart.
- Ui: `EmPortExtraction` (kind, the built path and its width, notes and refusals),
  `EmSetupModel.DeclaresInternalPort`, `EmRunService.InternalPortNeedsFullWave`, the panel row and
  its type list, `PlanarPortKindNameConverter`, and the layout mark
  (`LayoutRenderer.DrawInternalPortMarker` — a ring with a ground symbol; the mark channel now
  carries the port KIND rather than only an anchor).
- Tests: `tests/Engine.Tests/Mom/InternalPortTests.cs`, `tests/Ui.Tests/Em/InternalPortUiTests.cs`.
- Docs: `docs/design/mom-engine.md` §10.6, the user chapters `mom-engine.md` / `em-setup.md`, and a
  new one — `docs/user/src/reference/stackup.md`, which is where "what is my port's negative
  terminal" is now answered, with a cross-section figure generated from the shipped technology.

## The internal delta-gap port (2026-08-24)

The second of §10.6's two v1 port types, listed as "later" in the design note from the first draft
and as deliberately out of scope in this directory's own `CLAUDE.md` §7 until now. Both statements
were removed rather than softened; the design note's own §10.6 carries the full write-up.

**It cost almost nothing in the fill or the excitation, and that is the point.** D1 already defines a
port as a delta gap across the shared edge of two adjacent cells driving the rooftop row that spans
it — nothing in that says the cells have to be the two outermost ones. `PlanarPortKind` picks WHICH
shared edge: `Edge` marches in from the named side as before, `InternalDeltaGap` scans the run's
interior gridlines and takes the one nearest the placed point that has metal (and a rooftop) on both
sides. Everything downstream — `PlanarExcitation`, `Y = BᵀZ⁻¹B`, the s-matrix — is untouched.

**Three findings worth keeping.**

**1. A delta gap is a SERIES source, and the first gate asserted the wrong identity.** A gap at the
exact centre of a uniform line is mirror-symmetric about its own cut, so the obvious oracle is
"it couples equally to both ends", S₁₃ = S₂₃. The solve returned S₁₃ = **−**S₂₃, equal and opposite
**to sixteen digits**. That is correct and the oracle was wrong: a series gap pushes current one way
along the conductor, into the line on one side of the cut and out of it on the other, so the two
halves are driven in ANTIPHASE. A SHUNT port — current injected against the ground plane — would be
symmetric. The difference is a hard π, invisible in a magnitude plot, and the test now asserts the
antisymmetry at 1e-6 (it holds far tighter than that) precisely because a later change to the
incidence sign would otherwise pass. `CLAUDE.md`'s low-frequency-floor note already said the port is
"necessarily a **series** delta-gap"; this is the same fact arriving from the other direction.

**2. `IndexOf` CLAMPS, so "is the port on the metal?" needs the grid extent as well.** The transverse
index lookup returns the nearest cell for an out-of-range coordinate rather than a miss — right for an
edge port, whose label may legitimately sit just off the end face it names and whose longitudinal
coordinate is not read at all. For an internal port it is wrong in the worst way: a point metres from
the artwork clamps onto the outermost row, finds metal there, and cuts a gap the user never asked
for — a complete, plausible s-matrix for a structure nobody drew. The internal branch now tests both
axes against the grid's own extent before asking about metal. *(The pre-existing "outside the meshed
region entirely" refusal on the edge path is, for the same reason, effectively unreachable. Left
alone: the Ui-side extractor refuses an off-metal label before the engine sees it, and widening the
edge path's behaviour is a change to shipped port resolution that wants its own measurement.)*

**3. De-embedding stays ONE code path, via an identity error box.** An internal port takes
`PlanarSolve.IdentityBox` (a₁₁ = 0, a₂₂ = 0, a₂₁ = 1) and `Z_c = Z₀`, which makes
`PlanarDeembed.Apply` and `Renormalise` the identity on its row and column. This is not "de-embedding
an internal port": it is arithmetic that provably changes nothing, and it was chosen over partitioning
the matrix because a unit a₂₁ also leaves a de-embedded NEIGHBOUR's mixed terms untouched — the
partitioned alternative would have needed its own proof of exactly that. Gated by solving the same
problem with `Deembed` on and off and requiring < 1e-12; tolerant rather than an equality only because
the ON path still passes through an LU of the identity matrix, so asserting bit-identity would be
asserting a property of NumFlat's LU.

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
