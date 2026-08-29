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


---

# P1 — honest memory accounting for the planar solver (2026-08-29)

`docs/sonnet-briefs/brief-em-p1-honest-memory-accounting.md`. **This brief measured and re-worded.
It changed no arithmetic** — no fill, no factorisation, no Green's function, no s-parameter moved by
a bit, and no ceiling constant moved at all.

## What was wrong

`SurfaceMesher`, `PlanarSystem` and `PlanarFill` each carried their own copy of R17's refusal and
all three quoted `16·N²` — "381 MB at the ceiling". That is exactly the dense matrix and it is
silent about everything live beside it. §7 of `CLAUDE.md` already carried the open item "381 MB
quoted against ~607 MB real — owner's call". **607 was itself an underestimate.**

The same class of defect sat in `PlanarAimReport.ApproximateBytes`, which counted the accelerator's
own arrays and stopped before the sparse LU it builds from them.

## What was measured

Every number below is in `HISTORY.md` §P1 with its table.

- **One dense frequency point holds 3.52× the 16·N² that was quoted**, and the ratio is FLAT across
  N = 552 / 1,980 / 4,836 because every term is O(N²) with a fixed coefficient. The split is one
  matrix, **two** further full matrices for the LU factors, and the cached cores at just over half a
  matrix. At the ceiling that is **1,338 MB, not 381**.
- **`NumFlat.LuDecompositionComplex` is not a packed in-place LU.** It holds `L` and `U` as two
  separate full `Mat<Complex>` of stride n — confirmed by reflection over its fields AND by
  measurement — beside the matrix `PlanarSystem` keeps for the life of the point. That is the single
  largest term in the accounting and it is the one nobody had counted.
- **The transient m×m `P` is real and is not the peak.** The fill builds it, uses it and drops it
  before anything is factored; at m ≈ N/1.95 it is a quarter of a matrix against the factorisation's
  two.
- **The accelerated point's missing term was the sparse LU**, not the near field: at the accelerated
  ceiling its factors are comparable to everything else the operator holds put together.

## Three findings the brief did not anticipate

1. **`PreconditionerNonZeros` was never the factor's fill-in.** The brief describes it as the
   fill-in "reported but never added". It is `csc.NonZerosCount` — the near MATRIX's own non-zero
   count, which (every near pair being stored exactly once) is the near ENTRY count a second time,
   under a name that reads like the factor's. There was no fill-in number to add; `FactorNonZeros`
   (`SparseLU.NonZerosCount`, L and U together) is new.

2. **`CLAUDE.md` §8's "the accelerator's own working set stays under 200 MB even at that ceiling"
   was FALSE as shipped, and releasing `_nearExact` is what makes it true.** Counted honestly with
   the exact near entries still held — which is what shipped until this brief — the operator at
   N = 11,959 holds ~269 MB. Freeing them brings it to 196.2 MB. The sentence in the refusal and in
   §8 survives, but it survives *because of* this brief rather than in spite of it, and it is now
   tight rather than comfortable.

3. **The brief's own gate could not live in the routine tier as written.** "Within 20% of the
   measured resident delta" needs `GC.GetTotalMemory`, which is PROCESS-wide, and xUnit runs test
   classes concurrently in one process — another class's collection lands inside the measurement
   window. The first version of that test read **0.925 alone and −0.245 in a full-suite run**. It is
   not a flake to be re-run; it is the wrong instrument for a parallel suite. So the resident
   comparison is asserted in the `Category=Benchmark` measurement (which carries a "measure this one
   alone" note, following the precedent `HISTORY.md` already sets for the L8d cost table), and the
   ROUTINE gate counts the same quantity by walking the operator's own object graph and adding up
   every array it holds — load-immune, and independent of the report's arithmetic in the way that
   matters, since it counts the arrays that exist rather than re-deriving them from the report's own
   fields. **It agrees to 0.3%** (reported 9.7 MB against 9.7 MB walked at N = 552).

4. **Reading the exact entries back out of CSparse's CSC — the brief's own suggestion for keeping
   `NearExactAt` alive — is strictly worse than keeping the array.** The CSC stores a 4-byte row
   index per entry that the CSR's shared column index already provides, so holding the CSC to serve
   a diagnostic costs 4 B/entry MORE for the same numbers. `PlanarAimSettings.KeepNearExact`
   (default false) keeps the array instead, and `NearExactAt` throws by name when it was not kept —
   a silent zero there is indistinguishable from "this pair is not near", which is the question the
   caller is asking.

## A measurement trap worth the paragraph, because it produced a wrong table first

**The large object heap does not compact by default**, so a released n×n matrix leaves committed
space the next allocation lands in. A per-phase `GC.GetTotalMemory(true)` DELTA therefore reads the
matrix LOW (it lands in the transient `P`'s grave) and the factorisation HIGH, and a ladder that
measures several N in one process reads the second rung's cores as **negative** — which is what the
first version of this table printed. Cumulative live from ONE baseline, paired with
`GC.GetTotalAllocatedBytes`, are both exact and are what the tables use; the same effect made the
AIM ladder's second rung read 40% low until every operator was kept reachable for the whole test.

The brief's own opening scratch measurement is the same artifact: `.Lu()` "added 530 MB" to a 137 MB
matrix (3.87×) is the LIVE figure with the factorisation's released scratch still committed. What is
RETAINED is 2× the matrix; the extra ~0.6× is real, transient, and belongs in a note rather than in
a refusal.

`Process.PeakWorkingSet64` **reads 0 on macOS** — the platform does not track it — so the tables
report `WorkingSet64` and say so.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarSystem.cs` — new `CoreBytes(n, cellCount)`, `FactorBytes(n)`,
  `ResidentBytes(n, cellCount)` and `ResidentPhrase(n, cellCount)`. `GuardCeiling` gained an optional
  cell count. `PlanarSweepResult.ResidentBytes` added beside `MatrixBytes`.
- `src/Engine/Mom/PlanarFill.cs`, `src/Engine/Mom/SurfaceMesher.cs`, `src/Engine/Mom/PlanarSolve.cs`
  — the three refusals and the WARN note quote `ResidentPhrase`; the cell count is threaded to each
  from the mesh the caller already has.
- `src/Engine/Mom/PlanarAim.cs` — `ApproximateBytes` → `ResidentBytes` (renamed because it no longer
  approximates anything), plus `PeakBuildBytes`; `FactorNonZeros` and `NearExactRetained` added to
  the report; `PlanarAimSettings.KeepNearExact` added; `_nearExact` released after `FactorNear`.
- `tests/Engine.Tests/Mom/PlanarMemoryAccountingTests.cs` — new. Two `Category=Benchmark`
  measurements (the two tables, and the brief's own resident-delta assertion) and four routine
  COUNTER gates: the AIM report against every array the operator actually holds (and not vacuously
  so — without the sparse LU the same comparison misses by more than the band allows), the exact
  near entries released by default with the answer bit-identical either way, `CoreBytes` reproducing
  `PlanarFillCores.CoreBytes`, and all three refusals quoting the same sentence.
- `tests/Ui.Tests/Em/EmCeilingRefusalTests.cs` — the expected sentence updated, still asserted, and
  now also pinned against `PlanarSystem.ResidentPhrase` itself so the gate cannot drift from the
  code.
- `docs/design/mom-engine.md` §10.7 and `src/Engine/Mom/CLAUDE.md` §7/§8 — corrected in place.

## Out of scope by instruction, and left alone

`UnknownCeiling` and `AcceleratedUnknownCeiling` are untouched (P7 and P8 own those, once the memory
is what it will be). `PlanarSystem`'s factorisation is untouched (P7). **The 3.52× is a fact about
the code as it stands, and P2 and P7 are the briefs that move it.**


---

# P2 — four mechanical memory wins on the dense path (2026-08-29)

`docs/sonnet-briefs/brief-em-p2-cheap-memory-wins.md`. Four allocations removed. Three are
**bit-identical**, measured against a worktree of the pre-P2 tree rather than argued; the fourth
(M2) is a different rounding of the same system and moves a published s-parameter by ≤ 1 ulp.

## How the bit-identity claims were actually made, since the method matters

"Bit-identical" compares this build to the build before the change, and no single tree holds both.
So: `git worktree add … HEAD --detach`, drop the same fixture-driven digest test into both trees,
run, compare. The digests are then LITERALS in `PlanarP2MemoryWinsTests` — a test that recomputed
its own expected value from the code under test asserts nothing.

Running the pre-P2 worktree TWICE — once untouched, once with M2's six lines applied and nothing
else — separated the one milestone that moves arithmetic from the three that do not. That is what
lets `P2_6` assert "M1, M3 and M4 together moved not one bit of a published s-parameter" as a
measurement instead of a hope.

## Findings

**1. An O(N²) array held nothing but the outer product of an O(N) vector.** `BuildDirectionCores`
cached `mMom * nMom` per same-direction basis pair — the extracted constant term's vector core,
which factors. Its two neighbours in the same family (`∫∫ w w /R`, `∫∫ w w ln r`) genuinely do not
factor, which is why they are cached and this one need not have been. Removing it is **24% off the
cached cores at every N** and 3.5% off a whole dense frequency point, and it is exactly bit-identical
because the product formed at the point of use has the same two operands.

**2. `PlanarSolveResult.CoreFillCount` could not have caught the duplicate core build, because it
never counted builds.** It is `1 + standards` — derived from the number of standard MESHES. Over the
sweep it read 5 while the code was calling `BuildCores` 7 times, the extra two being
`StaticCapacitance` re-coring meshes whose contexts already held identical cores. A counter derived
from what SHOULD happen cannot notice what does. `PlanarCoreBuildCounter` counts the builds; the old
counter is unchanged, which is what the brief required.

**3. M4's saving is real but much narrower than the brief assumed, and the reason is structural.**
Through `PlanarSolve.Run` the calibration band IS the sweep's own frequency range, so over a full
sweep every separation is selected at some frequency and lazy coring saves nothing: 4 of 4 standards
cored on a 1–20 GHz sweep whether it has 5 points or 21. The saving appears only when the band is
wider than the frequencies actually stepped — a single-frequency check, an interrupted run, or a
direct `PlanarPortCalibrator` caller — where it is large: 2 of 4 standards cored, 50.5 kB of
240.8 kB, i.e. **79% of the standards' cores never built**. The unconditional part is that the
CONSTRUCTOR now cores 0 of 4 rather than 4 of 4.

**4. M3 and M4 pull against each other, by design, and the longest standard is the price.** D7's
`CapacitancePerMetre` differences `Standards[0]` and `Standards[^1]` — the two EXTREME lengths, not
the one this frequency solved. Handing it the contexts' cores (M3) therefore BUILDS the longest
standard's cores on every de-embedded run even when no frequency selected it. That is correct for
the static differencing and it is why the measurement above reads 2 cored rather than 1. P11 changes
that solve.

**5. The de-embedding ceiling's own refusal was undercounting, and M2 made the sentence false as
well.** It said "the two m×m complex matrices this solve holds at once (the potential-coefficient
matrix and its own copy)". M2 removed the copy; and P1 had already established that NumFlat's LU
holds `L` and `U` as two further full matrices, so "two" was low before M2 and wrong after it. It now
says three (P, L, U) and quotes 3·16·m².

**6. A geometry-only core is no longer empty, and the gate had to say what it actually meant.**
`AimAcceleratorTests.T1b` asserted `geom.CoreBytes == 0`. The accelerator's cores now carry the same
O(N) `VMoment` vector (8·N bytes, and `PlanarEntryFill` reads it instead of deriving a second copy of
the identical array). The assertion became `== 8·N`, which states the real claim — nothing QUADRATIC
— exactly rather than by proxy.

## Numbers worth keeping

| N | cells | cores before | cores after | resident before | resident after |
|---|---|---|---|---|---|
| 552 | 297 | 2.43 MB | 1.85 MB | 16.38 MB | 15.80 MB |
| 1,980 | 1,053 | 30.92 MB | 23.45 MB | 210.39 MB | 202.92 MB |
| 4,836 | 2,565 | 184.09 MB | 139.50 MB | 1,254.68 MB | 1,210.09 MB |

P1's flat resident-to-matrix ratio **3.52× → 3.39×**; the ceiling refusals quote **1,290 MB** at
N = 5,000 rather than 1,338. The ceiling CONSTANT is untouched — that is still P7's decision.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarFill.cs` — `VXArea`/`VYArea` removed; `PlanarFillCores.VMoment` (one
  `double[N]`) added, built by a shared `BasisMoments` that both core builders call.
  `AddDirectionBlock`, `HorizontalVectorEntry` and `PlanarEntryFill` form the product at use.
  `PlanarCoreBuildCounter` added; `PlanarFillSettings.CoreBuilds` carries one.
- `src/Engine/Mom/PlanarSystem.cs` — `CoreBytes` is `8·(2·sp + 2·vp + n)`.
- `src/Engine/Mom/PlanarDeembed.cs` — `StaticCapacitance` solves `P q = ε₀·1` and takes optional
  cores; `CapacitancePerMetre` takes two; the ceiling refusal names three m×m.
- `src/Engine/Mom/PlanarSolve.cs` — `PlanarSolveContext.Cores` is a thread-safe `Lazy` (the R17
  guard stays eager), plus `CoresBuilt`/`CoreBuildMs`; `PlanarPortCalibrator` gained
  `CoredMeshCount`/`CoreBuildMs` and hands its contexts' cores to `CapacitancePerMetre`; the run's
  reported `CoreBuildMs` is summed from the contexts rather than measured around a constructor.
- `tests/Engine.Tests/Mom/PlanarP2MemoryWinsTests.cs` — new, 7 routine tests.
- `tests/Engine.Tests/Mom/AimAcceleratorTests.cs` (T1b) and `EmDeembedCeilingTests.cs` (C2) — the two
  assertions whose subject genuinely changed.
- `docs/design/mom-engine.md` §10.7 and `src/Engine/Mom/CLAUDE.md` §7 — the 3.39× / 1,290 MB in place.

## Not done, on purpose

Storing `P` packed — the brief's own fifth candidate. It halves a transient 4N², and it touches every
`p[a, b]` reader in three fills, so it is a milestone with its own bit-identity gate rather than a
few lines. `PlanarAim.cs`, the LU and every quadrature untouched, as instructed.

---

# P3 — the multi-level fill scales like the single-level one (2026-08-29)

`docs/sonnet-briefs/brief-em-p3-multilevel-fill-scalability.md`. **Every number is in
`HISTORY.md` §P3; this is the narrative.**

## What was asked, and what turned out to be true

The brief read `FillMultiLevel` and saw per-entry work the single-level fill hoists — a locked
kernel-set lookup and a fresh `PlanarKernelTerms` per cell pair and per basis pair, three more locked
caches per entry, an array per horizontal entry — and predicted its parallel scaling "will be far
worse". Milestone 1 measured before anything was touched: **the multi-level fill already scaled
5.5–5.9× on ten cores, the same as the single-level fill's 5.4–5.9×.** The locks were uncontended
hits on small dictionaries, ~100 ns between ~200 µs of quadrature; the allocations were ~150 bytes a
pair against the same 200 µs. The hoist was still the right thing to do — it is bit-identical, it
takes ~8× the allocation out of every fill, and it is the shape the single-level fill already has —
but the speed it bought came from somewhere the brief did not look.

## Findings

**1. The via-bearing fill's cost was in an arm that is not O(N²).** `MixedEntry` — one entry per
via basis × horizontal basis — enumerated the horizontal cell's quadrature nodes through an
iterator, once per VIA node. On a near or intermediate pair that is ~1,500 enumerators per entry;
236 MB of them per fill on the N = 514 fixture, and ~30 s of its 58 s vector phase. Inlining the
enumeration (same nodes, weights and nesting order — bit-identical) is **19% off the serial fill
and 16% at cap 10** on that fixture, 16% / 22% on the FR-4 hero with three vias. The block is
O(N·N_z), but a via cell is large, so most horizontal cells count as near or intermediate to it and
take the full graded rule. Its remaining cost is a quadrature-order question and out of scope.

**2. The strided writes were not the fall-off, and the fall-off is the box.** Milestone 4 moved
every fill to the contiguous (lower) triangle with a column-wise mirror. The serial time on the
256 mm line is unchanged to 0.6% (70.9 → 71.3 s); 25 M cache-missing writes at 100 ns is 2.5 s of a
71 s fill, and could never have produced a 40% loss at ten cores. What does: the machine has
**4 performance + 6 efficiency cores**. Efficiency is 89–95% at cap 4 on every fixture, before and
after; the loss appears exactly when the cap admits efficiency cores, and every cap-10 speedup
measured (5.2–6.4×) solves `4 + 6f` to f ≈ 0.2–0.4 — an efficiency core's share of a performance
core. That is why `CLAUDE.md` §6's Amdahl fit gave three different serial fractions: there is no
serial fraction. "Hardware, not scheduling" stands; "memory bandwidth" is struck from it.

**3. Exactly the pairings the loops visit must be resolved, and no others.** `PlanarKernelSet.FitCount`
is asserted by three tests, and a hoist that resolved every (layer, layer) for every kernel would fit
pairings the loops never asked for — a layer carrying only x̂ rooftops and one carrying only ŷ never
pair in the vector block. The horizontal table is therefore built per direction from the layers
that carry it; the ẑẑ table is keyed on the ORDERED span pair the `i ≤ j` loop produces, because
`AveragedTerms(si, sj)` is not asked in canonical order and the digests were taken on what it was
asked; and the mixed table covers every (span, horizontal layer). FitCount is unchanged on every
fixture.

**4. The allocation that remains is the via z-integral's, not the fill's.** With a per-arm counter,
the horizontal and mixed arms now allocate 0 bytes across a whole serial fill. The 27 MB left beyond
the two matrices on the N = 514 fixture is 12.6 MB of per-pairing radial tables (which `Fill` also
builds per call) and 14.4 MB from three ẑẑ entries — `ViaZIntegral.PrismCore` allocates its node
arrays per entry, ~4.8 MB each. O(N_z²), the via rule's own, left alone.

## How the gates were made

The pre-P3 build lives in a git worktree at `HEAD` with P1 and P2's uncommitted diff applied and
`PlanarFill.cs` replaced by its pre-P3 copy; the same test file runs in both trees. Digests are
literals (seven of them — P2's own hero digest is re-asserted, since milestone 4 touches `Fill`).
The routine allocation gate computes its allowance from the settings — one `MaxTableSamples`-capped
table per (kernel, level, level), plus O(N) — and **the pre-P3 worktree fails it by 2.7×**, which is
what makes it a gate rather than a description. A first draft asserted a growth LAW (double the
line, ~4× the pairs, the extra allocation must not follow) and could not discriminate: on the
fixtures a routine test can afford, the pairs grow only 1.8× and the pre-P3 extra was dominated by
the O(N·N_z) mixed-arm iterators, so both trees passed. The allowance form replaced it.

Timing tests are `Category=Benchmark`, and were run ALONE and detached — a first attempt inside the
shell's ten-minute limit was killed with no output. The three multi-level fixtures at four caps plus
warm-ups are ~12 minutes; the 256 mm line ~5.

## What changed, for a reader who only wants the code

- `src/Engine/Mom/PlanarFill.cs` — `MultiLevelPairings` resolves everything per pairing before
  `ForRows`; `FillMultiLevel`'s loops read it. `HorizontalVectorEntry` (cached halves, no array),
  `MixedEntry`/`MixedHalf` (inline node enumeration for a whole-rectangle half),
  `CellPairPotential` (cached pulses), `SingularPrismPart` (asymptote passed in).
  `MirrorLowerToUpper` is the one mirror; every fill writes the lower triangle.
- `tests/Engine.Tests/Mom/PlanarP3MultiLevelFillTests.cs` — new: four routine gates, three
  Benchmark measurements.
- `src/Engine/Mom/CLAUDE.md` §6 — the "core heterogeneity or memory bandwidth" clause narrowed in
  place, dated. `docs/design/mom-engine.md` §10.7 — a `P3 is done` note.

## Not done, on purpose

No quadrature rule, table spacing or kernel evaluation was changed; `ForRows` is still the only
parallel loop. The mixed block's quadrature ORDER (the actual cost of a via-bearing fill at small N)
and `PrismCore`'s per-entry arrays are recorded above and left for a brief of their own.

# P4 — the four ramp combinations of a cell pair are one pass (2026-08-29)

`docs/sonnet-briefs/brief-em-p4-vector-block-moment-cache.md`. **Every number is in `HISTORY.md`
§P4; this is the narrative.**

## What was asked, and what turned out to be true

The brief's premise is right and is now built: a cell's two ramps are `w_A = (u − u₀)/A` and
`w_B = (u₁ − u)/A = Δ·p − w_A`, so every (half, half) integral over a cell pair is a linear map of
four primitives per kernel and per flow direction — seven per pair in all, one outer pass. The
core build's quadrature count fell 6.6–7.3× and the per-frequency vector remainder's 5.8–6.4×;
wall clock followed at 3.6–4.0× and 2.7–3.0× on the three series fixtures. Three things in the
brief did not survive contact with the code, and each changed the design.

## Findings

**1. The ŷ block integrates the same cell pair in both orientations, so the primitives are per
ORDERED pair over a band, not per unordered pair "with the same packed index as S0".** The outer
Gauss rule and the inner closed form are not interchangeable to 1e-12 — swapping which cell is
integrated numerically moves a touching pair by its own quadrature error, ~1e-6 — and the row
loops integrate the lower-indexed BASIS's cells as the outer domain. In the x̂ block that always
puts the lower-indexed cell outside. In the ŷ block a rooftop one row down and one column to the
LEFT has the lower basis index, so cell pair (c, c′) is integrated with c outside by one basis pair
and with c′ outside by another. An unordered cache, as the brief specified, would therefore have
changed the orientation of a fraction of the ŷ entries and failed the brief's own 1e-12 gate on
them — not a sign error, a quadrature-tolerance one, and exactly the kind of "smooth, plausible,
wrong" the brief warned about. The fix is structural: `RampTopology.MinInner[a]` is the smallest
inner cell any pair with `a` as outer can ask for (a suffix minimum over basis positions), the
pass runs `c ≥ MinInner[a]` — a band `n_x` wide below the diagonal — and orientation is preserved
exactly. The band costs ≈ 10–20% over the triangle on the series fixtures (0.56–0.60 m² against
0.5), and the 1e-12 gate holds at 8e-13.

**2. The brief's storage arithmetic was off by the basis-pair-to-cell-pair ratio, so the
primitives are transient rather than stored.** "7 × 2 doubles per cell pair against today's
2 + 3 + 3" compares a per-cell-pair count with per-BASIS-pair triangles, and there are ≈ 2
same-direction basis pairs per unordered cell pair. Holding the primitives would be 14 doubles per
cell pair against the ≈ 6 P2's layout holds — 2.3× the cached cores, ≈ +250 MB at the ceiling —
in a series whose first two briefs exist to take memory off. So the primitives are never
persisted: each outer cell's pass assembles them straight into the existing per-basis-pair
triangles. That needed one more idea, because a basis pair draws on two outer cells (its A cell
and its B cell) and a cell-parallel pass would have two threads adding into one entry. The A
cell's contributions go to the entry itself and the B cell's to a transient second triangle of
the same size, and a row pass adds them; every slot has exactly one writer, in a fixed order over
inner cells, so R-fil-11 holds and the build is deterministic. The resident cores are the P2
layout to the byte (`P1_5`, `P2_2` pass unchanged). The fill does the same with four transient
`Complex` triangles for the remainder sums, ≈ 195 MB at N = 4,933 — inside the fill phase, whose
high-water mark P1 showed is below the factorisation's.

**3. The scalar block came along for free, bit-identically.** The pulse×pulse primitive `Q00` is
the scalar block's own `S0`, so the one cell pass serves D4's triangle as well as both directions
of D5 — the brief's "≈ 0.5 m²" was the vector count alone and the total is now ≈ 0.6 m² for
everything. Accumulating `Q00` with the pulse path's own expressions in the pulse path's own
order makes `S0`/`SLog` bit-identical, which is asserted exactly and is what keeps every
capacitance and static-limit gate, and the ẑẑ block that shares `S0`, untouched.

**4. What "bit-identical" could and could not mean here, and what was re-pinned.** Four
combinations of seven primitives are different floating-point operations from four quadratures
summed, so the vector block's last bits move — the brief anticipated this and the gate is 1e-12 on
the assembled matrix, held against the pre-P4 arithmetic kept in the tree as
`BuildCoresByHalves`/`FillByHalves` (a runnable reference rather than a digest printed once
against a tree that no longer exists; the internals are not visible to the test project, which is
why it is a public pair of methods with "reference" in their documentation). Three things ARE
bit-identical and are asserted so: `S0`/`SLog`; every entry touching a cut basis, because those
pairs never left the four-call path (milestone 5's gate, read as the cut PAIRS rather than the
whole conformal matrix, which contains whole pairs too); and `PlanarEntryFill.At` against `Fill`,
because both assemble from the same primitives with the same `Combine` in the same order — A half's
two inner cells ascending, then B, then A + B — which is the order the dense pass's slots
accumulate in. The consequence is that every digest P2 and P3 pinned moved and was re-printed on
this tree, with a note at each literal naming the 1e-12 gate as the bridge. That is not a
loosening of a gate: the digests pinned "nothing moved" through P2's and P3's changes, and the
1e-12 comparison is the equivalent statement for a change that moves association by design.

**5. Why the seconds lag the counts.** A seven-primitive core pass evaluates six inner closed forms
per node against a ramp×ramp call's four, so it costs ~1.5 calls and 6.6× in passes is ~4.4× at
best in seconds (3.6–4.0× measured). A seven-sum remainder pass costs about one call — the `rem`
evaluation dominates — but the fill also carries the scalar block's own remainder over the OTHER
kernel, the radial tables and the row passes, none of which P4 touches, so 5.9× in vector passes
is 2.7–3.0× on the fill. The AIM near fill at N = 3,731 went 2.5× (23.7 → 9.3 s), less than the
dense fill because a near-field row visits each cell pair from fewer rooftops than a full
triangle does.

## How the gates were made

Before any code changed, the P3 tree's `Fill` was dumped to disk for the hero, the 60 mm taper
and the 16 mm conformal taper, and its core-build, fill and AIM near-fill times recorded alone on
the machine. The P4 tree was compared against those dumps entry by entry (max 8e-13 relative; zero
cross-direction entries moved; 1e-16 relative to the largest entry). The same three numbers came
back from the in-tree reference, which is what the permanent test holds. The pass counters are
asserted as ratios against the reference's own count so the mesher's cell count is not a hidden
parameter of the gate.

## What changed, for a reader who only wants the code

See `HISTORY.md` §P4's "What was changed in code". The one-sentence version: `BuildCores` and
`Fill` each run ONE cell-parallel pass over ordered cell pairs that serves the scalar block and
both flow directions, scattering through per-basis-pair slots with one writer each; the four-call
path survives for cut pairs and as the reference; `PlanarEntryFill` memoises the same primitives
per ordered pair.

## Not done, on purpose

The multi-level fill's horizontal remainder still takes four calls per pair per frequency (its
core build got P4's factor; its per-pairing remainder tables are the reason it was not widened
into here — the brief names `AddDirectionBlock` and `PlanarEntryFill`). No quadrature rule,
clustering or closed form changed; the ẑ blocks were not touched. The 256 mm line's LU was scaled
from P1's measurement, not re-timed.

# P5 — translation classes: every cell pair that is a translate of another is one integral (2026-08-29)

`docs/sonnet-briefs/brief-em-p5-translation-class-memo.md`. **Every number is in `HISTORY.md`
§P5; this is the narrative.**

## What was asked, and what turned out to be true

The brief's premise holds exactly: on a tensor-product grid with separation-only kernels, an ordered
cell pair's seven P4 primitives depend on six numbers and a rule, and the seven fixtures reuse each
class 2.5× to 30× — the brief's table reproduced to the pair under its own counting method, all
seven rows. The class table is now the production layout: one `int` per ordered band pair, the
seven primitives once per class, no per-basis-pair triangles at all. The core build fell 3.7–41× in
wall clock and the per-frequency fill 2.8–5.8× beyond P4; AIM's near fill on the 256 mm line 6.8×.
Four things in the brief did not survive contact with the code, and one of them changed the gate.

## Findings

**1. Exact `==` on the gridline differences does not hold, so the spacing classes are quantised at
1e-12 relative — as the brief allowed, and SAID here as it asked.** The mesher writes a bulk
gridline as `a + len·i/n` and the marcher rescales the graded runs, so the hero's x axis carries 15
exactly-distinct spacings for what are 6 classes at 1e-12; the in-class spread reaches 4.0e-13 on
the GaAs line and 3.7e-13 on the 60 mm taper. A class is represented by its smallest member. Nothing
else in the key is a double: each axis's spacing LIST between the two cells is hash-consed element by
element into an integer id, and a list read downward in index shares the id of the equal list read
upward, which is what makes a symmetric line's two graded ends one class.

**2. The brief's "1e-12 relative per entry" is not a property the reference itself has, and the
gate is on the diagonal scale instead.** Every value in P4's matrix is computed at the pair's
ABSOLUTE coordinates, and the corner-summed closed forms lose about 1e-16 · (x/w) of their own value
there — measured before any P5 code existed, on the P4 tree: the self core of a 0.5 mm cell moves
2e-13 relative when the same cell is placed at x = 1 m instead of at the origin, the far core of two
cells 0.25 m apart 5e-13. A class value is computed with the outer cell at the origin, so it is the
MORE stable of the two; the two disagree by ~1e-13 of the near cores. D4's assembly then takes
signed second differences of those cores, and for an aligned far pair of an x̂ and a ŷ rooftop the
four terms cancel to 1e-10 … 1e-14 of the diagonal, so a fixed 1e-13 · |Z_ii| disagreement reads as
1e-8 to 1e-1 RELATIVE on entries that are themselves cancellation residue: on the GaAs line 0.118 on
an entry that is 1.2e-14 of the largest, on the 256 mm line 2e-3 on one that is 6e-13 of it, and
millions of entries per matrix over 1e-12 by that measure. Measured on all seven fixtures against
P4's own arithmetic: max |Δ_ij| / √(|Z_ii||Z_jj|) between 6e-14 and 4.7e-13, max |Δ| / |Z_max| ≤
3e-13, and 7e-8 relative at worst on any entry at or above 1e-6 of the largest. The gate that ships
is |Δ_ij| ≤ 1e-12 · √(|Z_ii||Z_jj|) per entry — the scale on which the factorisation reads the
entry — and the solved currents of `Z x = e_k` on the coarse hero agree to ~1e-12 relative through
the LU. The per-entry relative figures are reported beside the gate rather than tuned away. The
only bit-identity P5 keeps is the one that stands alone: the scalar core of every pair with a cut
cell (its own row, the same conformal call in the same orientation). An entry touching a cut BASIS
is not bit-identical any more, because its scalar block also sums whole-cell pairs, which are
classed — P4's cut-pair bit-identity claim is therefore re-pointed at P4's own retained arithmetic
(`PlanarP4MomentCacheTests` runs on `BuildCoresByPairs`/`FillByPairs` now) rather than weakened.

**3. Orientation is preserved and the 180° rotation is folded, but the RULE had to go into the
key.** P4's lesson — the outer Gauss rule and the inner closed form are not interchangeable to
1e-12 — means a class never swaps outer and inner; what the brief's "(Δx, Δy) ≥ 0 lexicographically"
folds is the rotation about the outer cell, under which a rising ramp becomes a falling one, so a
rotated member reads the representative's (A, B) halves swapped: `Combine(outerB ^ rot,
innerB ^ rot, …)`, one xor, no arithmetic. The trap the brief did not name: two equal cells offset
by (4, 4) cells have τ = 4·√(w²+h²)/√(w²+h²) = 4 in exact arithmetic — exactly `FarRatio` — and
floating point decides per pair which side each lands on, so a class carrying its representative's
rule would move such members by the rule change (~1e-6). The τ band, computed from the member's own
floats exactly as `RuleFor` does, is the low two bits of the key, and the class counts came out
16–17% above the brief's unordered counts for that and for the band's 20% extra ordered pairs.

**4. The representative is synthetic — a pure function of the key — and that is what keeps AIM
bit-identical to the dense fill and R-fil-11 intact.** A first-visited member as representative
would make AIM's row-parallel near fill scheduler-dependent in the last bit, and would make the
dense build and the per-entry fill integrate different members of the same class. The class
primitives are integrated on outer cell [0, w_a] × [0, h_a] and inner cell at the class's signed
offset, every dimension the representative spacing of its class, the rule the class's band. `At`
against `Fill` stays bit-identical (`P4_3`, `AimAcceleratorTests.T1`) because both read the same
class through one `WholeVectorEntry`.

**5. The memory win has a break-even, and it is stated rather than assumed.** The class layout holds
4 bytes per ordered band pair (≈ 0.6 m²) plus 112 bytes per class (seven primitives × two kernels),
where P4 held ≈ 24 m² bytes of triangles; the two meet at a reuse of ≈ 3×. Above it the win is real
— the 256 mm line 83 → 25 MB, the tapers 4.5–5×; below it the layout is slightly LARGER (the hero
1.8 → 2.6 MB, the GaAs line 3.6 → 5.9 MB, both trivial in absolute terms), and a mesh with no
translation reuse at all would hold up to 2.9× P4's bytes. A hybrid that scattered the class table
into P4's triangles when the count came out unfavourable was considered and not built: it keeps a
second production layout alive in every reader for a case none of the seven fixtures approaches.
The a-priori figure the ceiling refusals quote (`PlanarSystem.CoreBytes`) stays P4's formula,
documented as a conservative quote rather than a reconstruction; `PlanarFillCores.CoreBytes` reports
what was allocated, classifier tables included.

**6. The GaAs line's 2.1× is a MESHER finding, recorded for a separate brief and not fixed here.**
Its x grid is asymmetric: the −x end carries the graded fan (2.16, 6.47, 19.4, 58.3, 175 µm into a
208 µm bulk of 8 cells) and the +x end carries NO fan at all — 32 cells of 2.16 µm, i.e. 69 µm of a
2 mm line meshed at the edge-cell pitch. The hero's x axis shows the same asymmetry in a milder
form (86.6, 174.8, 352.7 → 711.6 µm on the left; 538.9, 173.2, 86.6, 86.6 on the right), which is
the "end grading is not exactly mirror-symmetric" `CLAUDE.md` §3.5 already records as the reason a
plain line's two ports do not share a calibration. On a symmetric grid the two ends would be one
class family and both lines would reuse more.

## How the gates were made

Before any code changed, the P4 tree's `Fill` was dumped to disk for all seven fixtures, its core
build, fill and AIM near-fill times recorded alone on the machine, the brief's class counts
reproduced with the brief's own method, and the translation-sensitivity of the closed forms probed
on a hand-built row of cells at four offsets. The class-layout matrices were compared against those
dumps entry by entry, which is where finding 2 came from; the same comparison, against the retained
`BuildCoresByPairs`/`FillByPairs` in the tree, is what the permanent tests hold. Class counts are
literals in the routine test (`P5_1`, hero and 60 mm taper) and in the Benchmark one (all seven).

## What changed, for a reader who only wants the code

See `HISTORY.md` §P5's "What was changed in code". The one-sentence version: `PairClassifier`
(new file) keys every ordered band pair on grid indices; `BuildCores` classifies the band, sorts the
keys, integrates one synthetic representative per class and keeps rows for what no class serves;
`Fill` evaluates the remainder once per class and assembles every entry from the class table
through `WholeVectorEntry`, which `PlanarEntryFill.At` shares; P4's per-pair pass survives as
`BuildCoresByPairs`/`FillByPairs`, the reference.

## Not done, on purpose

The multi-level fill reads its cores through the class table (so its core build got the full
factor) but its per-frequency horizontal remainder is still four calls per pair, as after P4 — the
brief names `Fill` and `PlanarEntryFill`. No quadrature rule, panel or closed form changed. The
mesher's end asymmetry (finding 6) is recorded, not touched — the brief forbids changing the mesher
to increase reuse. The 256 mm line's LU was not re-timed; the per-point crossover is scaled from
P1's measurement and says so.

# P6 — AIM's frequency-independent state, built once per mesh (2026-08-29)

`docs/sonnet-briefs/brief-em-p6-aim-frequency-independent-state.md`. Measured tables in
`HISTORY.md` §P6; this is what was learned.

**1. The brief's premise was stale by two phases, and the measurement was taken before the code was
touched.** It quotes §12's "25 s per frequency at N = 3,731"; on the tree P6 started from that build
was 1.75 s, because P4 and P5 had already taken the near fill from 23.7 s to 1.07 s. What P6 could
remove per frequency was therefore the projection (0.14 s), the near set (in the 0.15 s "tables"
figure) and the singular-core share of the near fill — measured afterwards at 0.3 s of the 1.07 —
not twenty seconds. The structural claim stood (the dense path runs its singular quadrature once per
mesh, the accelerator ran it once per frequency), and the honest result is **1.2–1.4× per point**,
recorded as such rather than as the order of magnitude the premise implied.

**2. The mirror index the brief listed for the geometry was measured and left out.** At 4 B per near
entry it is 18.2 MB at N = 11,959 and took the resident set from 196.2 to 214 MB — past the "under
200 MB at the ceiling" sentence `CLAUDE.md` §8 stands on — to save a rebuild that is one binary
search per lower-triangle entry: 1 / 5 / 17 ms per frequency at the three N. The transpose position
is found inline instead. A memory series should not spend 18 MB to buy 17 ms.

**3. The near cores' store is a flat array, not dictionary nodes, because the memory gate would not
have seen them otherwise.** `P1_3` counts the arrays an operator holds by walking its object graph;
values held in `ConcurrentDictionary` nodes are invisible to it, and its element sizing put every
struct at 0 bytes. The store is chunked arrays of 168-byte `CellPairMoments` addressed through a
key-to-slot index, the walker now sizes a struct element with `Unsafe.SizeOf`, and the report's
208 B per class is checked rather than asserted. The count itself is the P5 compounding the brief
anticipated: ~9–10 thousand classes at N = 552 and at N = 11,959 alike, 2–2.5 MB, against the
~130 MB the brief's per-cell-pair arithmetic would give at the ceiling.

**4. Splitting the near fill's timer showed that the AIM correction, not the remainder quadrature,
is now the largest per-frequency term** — 43–46% of the build at N ≥ 3,731, ahead of the sparse LU.
`AimEntry` does `(M+1)⁴ = 256` complex multiply-adds per near pair per block; only the kernel values
depend on frequency, and two stencils share at most `(2M+1)² = 49` distinct grid offsets, so the sum
could be 256 real products into 49 offset weights and 49 complex multiplies. Recorded for P7/P8;
not built here.

**5. The time crossover is N ≈ 1,100, measured, not the 3,700 of §12 or a scaled estimate.** One
dense point (P5 fill + LU) against one accelerated point at N = 552 / 994 / 1,912: 0.24 / 0.54 / 2.03 s
against 0.37 / 0.71 / 0.96 s. Below the crossover the accelerator is 1.3–1.5× slower and the dense
path stays the shipped default.

## How the gates were made

Before any code changed, a throwaway test on the committed tree dumped the whole accelerated matrix,
every near exact entry, the solved current and the iteration count at three frequencies on the
coarse fixture; the same test on the finished tree produced a byte-identical file. That is what
licenses storing the stencils as `double` and moving the cores across the seam without a tolerance.
The permanent gates are bit-identity between the four-argument `Build` (still geometry-and-operator
from scratch) and an operator over a shared geometry at five frequencies, the same through
`PlanarSolveContext`, the once-per-sweep counters, and the report's accounting reconstructed term by
term. The timings are one Benchmark method, run twice per rung, both readings recorded.

## What changed, for a reader who only wants the code

`PlanarAimGeometry` (new) holds the grid, the stencils, the near set and a `PlanarEntryCores` (new,
the frequency-independent half of `PlanarEntryFill`) warmed over the near set; `PlanarAimOperator`
is built per frequency over it and holds the grid kernels, the near entries' remainders and assembly,
the correction and the sparse LU. `PlanarSolveContext.AimGeometry` is lazy beside `Cores`.
`PlanarAimOperator.Build(cores, …)` still works and builds both, so every existing caller and gate is
unchanged.

## Not done, on purpose

The mirror index (finding 2). The correction's restructuring (finding 4). The projection order,
pitch, near radius, tolerance and preconditioner are untouched, as the brief requires; the
multi-level refusal moved to `PlanarAimGeometry.Build` with its text unchanged. `AimAccuracyTests`
was not re-run: it reads only the four-argument `Build`, shown bit-identical, and the owner asked
that the Benchmark tier not be spent where a cheaper measurement answers.

---

# P7 — the dense factorisation (brief-em-p7-symmetric-inplace-factorisation.md, 2026-08-29)

## What was asked, and what turned out to be true

Replace NumFlat's general LU on the dense planar path with an unpivoted, in-place, blocked, parallel
complex-symmetric LDLᵀ; gate it on a residual rather than on faith; keep the LU reachable as the
oracle. All five milestones landed. **The factorisation is 11.5× faster at N = 4,836 in the shipped
configuration and holds a third of the memory**, and no fixture came within three and a half decades
of the brief's own "stop and report" residual, so no pivoted alternative was needed.

The full tables are in `HISTORY.md` §P7. What follows is what a reader would otherwise have to
rediscover.

## Findings

**1. `dotnet test` builds DEBUG, and that INVERTS this particular comparison.** Every other timing in
this area compares managed code against managed code, so the build configuration has always cancelled
and nobody has had to think about it. It does not cancel here: NumFlat's LU is native and moves 1%
between configurations, while `SymmetricFactorization` is ordinary C# and runs **11× slower**
unoptimised. In Debug the top rung reads 143.8 s at cap 1 against an LU of 40.7 s — "the replacement
is three and a half times slower" — and in Release the same code reads 13.1 s against 41.2 s. **Any
in-suite benchmark that compares a managed implementation against a native one is measuring the build
configuration**, and this is the first one in this area that does. `PlanarP7FactorCostTests` prints
its own configuration above its table and asserts only what holds in both.

**2. A synthetic decaying matrix is not a stand-in for Z when the thing being timed is the LU.** A
complex-symmetric fixture with a `1/(1+|i−j|)` off-diagonal decay timed NumFlat's LU at **97.0 s** at
N = 4,836, reproducibly, against **41.2 s** on a real filled Z of the same size — while
`SymmetricFactorization` read 13.1 s on both. The synthetic fixture is fine for the correctness
tests, which is where it stayed; the cost table is measured on real matrices.

**3. The brief's 1e-12 residual gate cannot be applied to its own worst-conditioned fixture, and the
reason is the fixture.** At 120 MHz — just inside `Dcim.CanFitAtFrequency`'s floor — the residual is
5.5e-12 for the LDLᵀ and **3.8e-12 for the general LU on the same matrix**. That is the MPIE's
low-frequency breakdown (the vector term vanishing like ω against a scalar term growing like 1/ω), a
property of Z that a pivoted factorisation misses by the same margin. Holding the unpivoted form to a
bound the pivoted reference also misses would be measuring the fixture — this area's most expensive
recurring mistake. The gate is therefore in two parts: the three series fixtures keep the brief's
1e-10 / 1e-12 verbatim, and **every** fixture is held to 1e-8 absolute *and* to within 10× of the
general LU's own residual on the same matrix, which is the question actually being asked. Measured
ratio: **1.4–2.0× on all five**.

**4. The live-heap trap P1 documented is about SCOPE, not about the counter.** A first version of the
memory table measured a whole frequency point cumulatively (cores → fill → factor) and read a
**negative 188 MB** at the largest rung, even with a blocking compacting collect at each end. Scoped
to one factorisation on a fresh copy, with the baseline taken immediately before that copy, the same
counter is exact to the last megabyte: 357.0 MB against 356.9 MB of matrix, and 1,070.6 against
3 × 356.9 for the LU. **`GC.GetTotalAllocatedBytes` is not the safe alternative it looks like** — it
is process-wide and counts every thread, so the LDLᵀ column picks up the trailing update's own
`Parallel.For` task objects: 0.2–0.9 MB, independent of N, i.e. noise.

**5. Only the LOWER triangle is ever read, and the fill computes exactly that.** `PlanarFill.Fill`
and `FillMultiLevel` both write the lower triangle and `MirrorLowerToUpper` copies it, so the
factorisation reads precisely the entries that were computed — the multi-level path included, whose
symmetry `CLAUDE.md` describes as "structural" rather than bit-for-bit. There is no
symmetrisation and no perturbation anywhere in this change. It also means that O(N²) mirror write is
now arguably dead on the dense path; establishing that is its own piece of work (`PlanarEntryFill`,
the AIM path and several tests still read `Z[i,j]` for arbitrary (i,j)).

**6. Packing the triangle would RAISE the peak, not lower it.** 8·N² is the steady state a packed
factor would hold, but the matrix arrives from the fill as a full N×N array and packing it needs a
transient 24·N² — higher than the 16·N² the in-place full-lower form never leaves. A refusal is about
the peak, so full-lower in place is the right choice unless the FILL is changed to build packed,
which the brief forbids.

**7. `PlanarSystem.Matrix` had to become an exception rather than a comment.** The factorisation
overwrites the lower triangle and leaves the UPPER one untouched, so a stale read of a symmetric
matrix returns entirely plausible numbers from the previous frequency. Six call sites in the tests
held their own reference to the matrix they had wrapped; all six now pass `matrix.Copy()`, in the
test, which is where a reader can see what the copy is for.

## How the gates were made

The oracle is the code being replaced: `PlanarFillSettings.UseSymmetricFactorization = false` selects
NumFlat's LU, and every accuracy gate solves the same Z both ways. Bit-identity is not available and
was not asked for — two different factorisations of one matrix do different arithmetic — so the gates
are the solved current vector to 1e-10 relative, the residual to 1e-12 on the series fixtures, and
the published de-embedded s-parameters to **1e-9 absolute** over a 5-point sweep on three fixtures
(worst measured 7.4e-13). Everything routine runs on the COARSE mesh, deliberately: a factorisation
cannot tell a coarse mesh's Z from a shipping mesh's, so a finer mesh is a bigger matrix rather than
a harder question. `Category=Benchmark` carries exactly one method, the cost measurement, which is
the one place a large N genuinely is the subject.

**The whole-sweep digest gate was strengthened rather than re-pinned.** `P2_6` hashes the published S
of a de-embedded sweep against a literal, and P4 and P5 each moved that literal when they moved the
last bits. P7 moves it too — but a bare re-pin accepts any change, including a real one, so the test
now runs the same sweep through BOTH factorisations and asserts that **NumFlat's path still
reproduces P5's literal bit for bit**. That is the load-bearing half: it says the factorisation moved
and nothing else did — not the fill, the cores, the calibration, the peel or the renormalisation.

Two structural properties are asserted rather than measured: caps 1 / 2 / 4 / unbounded and a shared
`PlanarParallelBudget` produce **bit-identical** factorisations (R-fil-11's rule — the trailing
update's parallelism is over destinations each written by one iteration), and the P-right-hand-side
substitution is bit-identical to P separate solves.

## What changed, for a reader who only wants the code

`SymmetricFactorization.cs` is new and self-contained. `PlanarSystem` holds its matrix privately,
throws from `Matrix` once factored, and gained `Factor()` (which replaces `_ = system.Lu` as the way
to force and time the factorisation) and `Factorization` / `LastResidual`. `IPlanarOperator` gained a
default `Solve(IReadOnlyList<Vec<Complex>>)` that `PlanarSystem` overrides, so `Y = BᵀZ⁻¹B` streams
the factor once for all P ports. `PlanarFillSettings` gained `UseSymmetricFactorization` (true) and
`TrackFactorizationResidual` (false). `PlanarSystem.FactorBytes` now means the shipped path;
`LuFactorBytes` is its old body under its own name; `SymmetricFactorBytes` is `16·N`.

## Not done, on purpose

No native BLAS (the root `CLAUDE.md` requires asking, and the brief forbids it). No pivoting —
Bunch–Kaufman stays the NAMED follow-up and `FactorPanel` refuses by name on an exactly zero pivot
rather than falling back quietly. No vectorisation of the trailing update: it is scalar managed C#
and NumFlat's native LU still beats it per flop, so a `Vector128`/`Vector256` complex multiply-add
and destination-column register blocking are an obvious further 2–4× — recorded, not built, because
the brief's target was met by 11.5×. `PlanarDeembed.StaticCapacitance` and `ChargeSolver` both factor
a symmetric matrix over CELLS and would take the same treatment; they are outside this brief's scope
and are now the only general LUs left on a planar run. **`UnknownCeiling` was re-asked and NOT
moved** — 1 GB now buys N = 6,968 against 4,454, so memory no longer binds at 5,000, but the fill's
time and a mesh's accuracy at that size have not been measured and the constant is the owner's.

# P8 — AIM's near radius had no physical floor (2026-08-29)

**Symptom.** `brief-em-aim-ceiling.md`'s A1b ladder — refine the mesh at a fixed 64 mm footprint —
climbed GMRES 21 → 143 → 372 iterations over cells/λ 80 → 120 and did not converge at all at 140
(N = 13,967, residual 9.1e-5 against a 1e-8 tolerance). The A1a ladder, growing the board's LENGTH at
the shipping resolution, was flat over the same N range. `AcceleratedUnknownCeiling = 12,000` was set
with margin under A1b's failure.

**Cause, in one sentence.** `PlanarAimSettings.NearRadiusFactor` measures the exact near field in
**largest basis supports**, and refining a mesh shrinks the largest support — so the near field
shrinks in METRES (8.93 h at cells/λ = 20, 1.28 h at 140, and the same at every board length to four
figures) while the coupling it has to span does not, because the grounded slab's image sits at a fixed
depth 2h. The scalar kernel is `1/ρ − 1/√(ρ² + 4h²)` plus smooth terms: past ρ ≈ 2h the residue falls
like `2h²/ρ³` rather than `1/ρ`, so **2h is where the coupling stops being long-ranged** and a near
field narrower than it is missing the dominant part.

**Fix.** `PlanarAimGeometry.NearRadiusM = max(NearRadiusFactor · maxSpan, NearRadiusMinM)`, where
`NearRadiusMinM` is null by default and derives `2·h`. The A1b ladder becomes 21 → 28 → 36 and the
failing rung converges; the same ladder at 16 mm is 2, 4, 6, 12, 14, 10, 10 against 2, 4, 6, 12, 46,
144, 273. `4h` was never needed — the brief's own stopping rule is "flat at 2h".

## Four things worth keeping

**1. It is not only the preconditioner — the brief's premise was half right.** The brief said "nothing
about the AIM projection's accuracy is at stake; the preconditioner is what degraded". But the near
radius is also the boundary between entries computed EXACTLY and entries the projection approximates,
so shrinking it degrades the OPERATOR as well. `|ΔI|` against the dense solve on the same mesh:
4.90e-7 at cells/λ = 20, **5.93e-4** at 140 — a 1,200× loss, of which the floor recovers 17×
(3.58e-5). Both measured at the same converged GMRES residual, so it is the operator and not the
stopping rule. A ladder that only watched iteration counts would have missed this.

**2. The obvious alternative explanation was checked and ruled out, cheaply.** A preconditioner that
silently failed to factor would leave GMRES unpreconditioned and produce exactly this iteration
climb. `PlanarAimReport.FactorNonZeros` (P1's field) is non-zero on every rung including the one that
fails, so the factorisation succeeds throughout — it is a good factorisation of a near field that is
too narrow. Printing that one existing column cost nothing and settled it before any code changed.

**3. The floor's real cost is the sparse LU's FILL-IN, not the near set, and it is
aspect-ratio-dependent.** Widening the radius grows the near matrix ~1.5× at the top rung; what it
does to the factor depends on how much of the board the radius spans. On a 64 mm line the fill-in
ratio is 1.13× and the floor is a NET WIN (build + solve 11.0 s → 7.1 s, because the solve collapses).
On a 16 mm line the 3.2 mm radius covers the whole 2.9 mm width and a fifth of the length, the band is
wide against a small N, fill-in is 2.93× and the same change is a net LOSS (12.1 s → 19.0 s). Both are
correct answers; the short board is the artefact. Memory grows either way — 303 → 426 MB at
N = 10,708.

**4. The slab height is a REQUIRED argument, on purpose.** `PlanarAimGeometry.Build` refuses a
non-positive `slabHeightM` by name rather than defaulting to "no floor", because the failure of a
forgotten plumb is silent: the geometry takes the pre-P8 radius and returns a complete, plausible
answer that merely takes 20× longer to reach, or does not converge. Making it required turned all six
call sites into compile errors. `PlanarSolveContext` takes it as a trailing optional — the DENSE path
has no near radius to floor, and 35 dense call sites should not have to carry it — and throws when
`Fill.Aim` is set without one.

**5. It changes no answer anyone currently gets.** The floor binds on no mesh either starter
technology produces at a shipped resolution — R/h is 2.68 to 8.92 on the FR-4 hero at cells/λ 20 and
40, and **4.17 to 50.03 on the GaAs starter**, where a 72 µm conductor on a 100 µm slab has cells that
are large against h by construction. Only the PCB case, whose conductor is nearly twice its slab
height, can be walked into the floor, and only by asking for several times the default resolution.
`HISTORY.md` §P8 §M3 carries the table.

## What the ceiling question became

`AcceleratedUnknownCeiling = 12,000` is **untouched** (the brief forbids moving it, and the decision is
the owner's) — but its basis has changed, and the recommendation is to leave it there for a NEW reason.
A1a, the healthy construction it was set from, is unaffected by the floor and stands exactly as
measured. A1b, the failing construction it took its margin from, no longer fails to converge. What
replaced the convergence risk is a BUILD one: at N = 10,708 the accelerator holds 426 MB on a refined
mesh against 188 MB at N = 12,894 on a coarse one, and one rung further — N = 13,967, the near matrix
8.9% dense — **CSparse's exact sparse LU had not returned after 8 minutes of CPU and 1.43 GB, and was
stopped there**, where the unfloored one factors in 5 s. So the old failure was a GMRES that did not converge and threw; the new one would
be a preconditioner that never finishes building. **N alone no longer bounds the accelerated path** —
near entries per row does. `HISTORY.md` §P8 §M5 carries the sentence that would move the constant,
written out and not applied, together with the density guard it would need beside it.

That is also why `AimCeilingTests.A1_LadderByResolution` is now pinned to the pre-P8 radius
(`NearRadiusMinM: 0`): it exists to record the "before", and on the shipped default its top rung would
never finish. `PlanarP8NearRadiusFloorTests` is where the shipped behaviour is measured.

## Not done

`NearRadiusMinM` is exposed on `PlanarAimSettings` and reachable from no UI — nothing persists it and
`EmRunService` only ever passes `PlanarAimSettings.Default`. That is deliberate: it is a derived
physical quantity, not a knob a user should be tuning, and its one non-default use in the tree is the
`0` that turns the floor off so a test can measure the pre-P8 behaviour.

---

# P9 — adaptive frequency sampling on by default: measured, and the recommendation (2026-08-29)

**A decision brief. Nothing was flipped, and one thing turned out already to have been.**

## What was asked, and what turned out to be true

The brief asks whether `PlanarAdaptiveSettings` should be on by default, on the premise that the
feature ships off. **That premise was two days stale in the half that matters.** The ENGINE default
is still `PlanarSolveSettings.Adaptive = null`, but the shipping USER default was flipped on
2026-08-26: `EmSetupModel.AdaptiveSampling = true`, translated by `EmRunService` into
`PlanarAdaptiveSettings.Default`. Every EM run a user starts today already samples adaptively. So
the live question is not "should it be turned on" but **"was turning it on right, and are 1e-3 and
5 the right numbers"**. The measurements answer both.

## The recommendation

**Leave it on. Keep `Tolerance = 1e-3` and `InitialPoints = 5`. Leave the ENGINE default null.**
Four reasons, each measured:

1. **It never costs accuracy that matters.** On the panel's own default grid the realised worst
   |ΔS| against a fully-solved sweep is at worst **1.42e-3** across five fixtures. The one deep
   feature in the set — a −7.2 dB notch — was recovered **exactly, in all 33 configurations run**,
   at every tolerance and every seed count.
2. **It costs about 1% when it saves nothing.** Two of five fixtures solve every point anyway and
   read 0.99× — the overhead is the probes' interpolant evaluations, and the calibration replays are
   cache hits by construction.
3. **`InitialPoints = 5` is right and raising it is a pure loss.** From 5 to 17 seeds the realised
   error is unchanged to two figures; from 33 up the solved count climbs and the error does not.
4. **`Tolerance = 1e-3` is the right point on the curve.** 1e-2 saves more but its realised error
   reaches 5.6e-3, which is visible on a −40 dB return-loss plot; 1e-4 gives back most of the saving
   (1.72× where 1e-3 gets 3.67×) for an error nobody is reading.

**The engine default stays `null`, deliberately.** It is not the user-facing default and never was;
it is what makes every measured number in L8c/L8d/L9d and P1–P8 reproducible at full precision, and
R-adf-1's bit-identity gate is written against it. Flipping it would change what every engine test
measures and buy a user nothing.

## What the owner should know before agreeing, because it is not what the design note promises

**§10.7's "typically cuts solve count by 5–10×" is not what the shipped default sweep gets. On
1–20 GHz at 101 points the saving is between 1.0× and 1.73×**, and on two of five fixtures it is
exactly nothing. The explanatory number is one column wide: **adjacent points of that grid already
differ by 0.13 to 0.63 in |ΔS| — 130× to 630× the tolerance.** Adaptive sampling can skip a point
only when the interpolant predicts it inside the tolerance, and a grid whose neighbours are that far
apart is not oversampled with respect to a 1e-3 criterion at all.

**The lever that actually delivers the 5–10× is a FINER grid, which inverts the usual advice.** On
the FR-4 hero, a **401-point** adaptive sweep costs **3.01 s** and solves 81 points; a **101-point**
non-adaptive sweep of the same band costs **4.25 s** and solves 101. Four times the frequency
resolution, in less time. With adaptive sampling on, the solved count is set by the STRUCTURE and
not by the grid, so **asking for more points is close to free and asking for fewer buys almost
nothing**. The user documentation currently advises the opposite ("the useful move when in doubt …
is to reduce the number of requested points"); that sentence is now measurably backwards.

## Four things worth keeping

**1. The narrow-resonance failure mode the brief names could not be constructed, and the reason is
structural — it is the same fact as the small saving.** For a distributed structure the transmission
phase rotates at `2π√ε_eff·L/c` per Hz. For a resonance of quality factor Q to be under-resolved the
grid step must exceed about `f₀/2Q`, and at that step the background's own adjacent-point difference
is `≈ π·(L/λ_g)/Q` — below a tolerance τ only when `L/λ_g < τ·Q/π`, i.e. shorter than λ/45 at
τ = 1e-3 and the measured Q ≈ 70. A structure that short cannot host a distributed resonance.
**Whenever a resonance is sharp enough to fall between two solved points, the background is already
forcing refinement to the grid floor, and the resonance is bracketed on the way down.** Read the
other way, that is exactly why so few points can be skipped. Both halves were measured across two
grids, three tolerances and six seed counts; the argument is supported by the measurements, not
proved by them.

**2. The real accuracy caveat is a magnitude one, not a missed-feature one: `Tolerance` is a LOCAL
stopping test, not a global error bound.** On a 1001-point grid at tol 1e-3 the realised worst |ΔS|
is **1.01e-2 — ten times what was asked for**. L9e's `T1_2` already gates this at `worst < 10 × tol`;
these numbers sit at that gate rather than inside it. Anyone quoting the tolerance to a user as an
error bar would be overstating it by up to an order of magnitude.

**3. Adaptive sampling cannot rescue a notch that falls between two REQUESTED frequencies, and the
user doc's own framing of "the problem it solves" says that it can.** R-adf-2 publishes exactly the
grid the user asked for and never inserts a point. The measured fixture makes this concrete: its
notch is 80 MHz wide (Q ≈ 70 — and sweeping tanδ over 1e-5…2e-2 and the coupling gap over
0.15…2.4 mm moves the depth but not the width, so 80 MHz is what this structure class produces), and
the panel's default grid steps 190 MHz. On that grid the notch is lost with the feature on or off;
what the published curve reports as its deepest point is a different feature 5.6 GHz away.

**4. The memory the adaptive branch retains is real but never the binding term.** It keeps every
solved point's kernel, raw matrix and per-port basis currents alive at once, because the calibration
is replayed from a fresh branch state after each insertion — `16·N·P` bytes per solved point, ~4 MB
at N = 1,278 and ~16 MB at N = 5,000. Measured peak managed heap on the taper was **217 MB adaptive
against 227 MB off**: the transient fill and factorisation dominate by two orders of magnitude, and
the difference is GC timing. It is worth knowing about only if the retention ever meets a mesh where
the matrix itself is not the peak.

## How the gates were made

**No new engine test.** The brief gates a flip, and nothing was flipped; `AdaptiveSweepTests` is
unchanged. Every number above came from a standalone Release harness run one fixture at a time —
`dotnet test` builds Debug, which would have inverted the timings, and the benchmark tier's own
warning is that these measurements read ~2× slow alongside each other.

**One UI gate was missing and is now there.** Milestone 3 requires that the panel show a point was
modelled rather than solved. `WorkspaceViewModel.EmRunSummary` already does — but its only test
covered the `adaptive: false` path, so the branch that reports the solved count was ungated.
`EmRunProgressTests.WithAdaptiveSamplingOn_TheFinishedRow_SaysHowMANYPointsWereSOLVED` now asserts
the requested count, the solved count and the word "modelled" all reach the row.

## What changed, for a reader who only wants the code

```
tests/Ui.Tests/EmRunProgressTests.cs   + the adaptive branch of the run-summary row
docs/design/mom-engine.md              §10.7's "rational" and "5-10x" corrected in place
docs/user/src/reference/mom-engine.md  the interpolant, what a notch between samples does, the advice
src/Engine/Mom/HISTORY.md              §P9 — every table
```

No engine source was touched.

## Not done, on purpose

**The default was not flipped in either direction** — the brief forbids it before the owner answers,
and the user-facing default is already where the recommendation says it should be.

**`PlanarAdaptiveSettings` is still reachable only as a whole.** `EmSetup.AdaptiveSampling` is a
bool; `Tolerance`, `Interpolant` and `InitialPoints` are not persisted and no panel exposes them.
Given that `InitialPoints` measurably buys nothing and `Interpolant` was decided by measurement at
L9e, exposing them would be offering knobs whose right settings are already known. If anything here
deserves a control it is the tolerance, and only alongside a plainer statement of what it is not.

# P10 — M2's fan-out is not starving the thread pool (2026-08-29)

`brief-em-p10-fanout-starvation.md` was written as a hypothesis to test, not a finding, and the test
refutes it at milestone 1. **Nothing in the solver's parallelism was changed.** Milestones 2 (one
shared row queue drained by `cap` long-lived workers) and 3 (re-measure the overlap) are conditional
on the instrument showing a stall — it does not — so neither was built. Every table lives in
`HISTORY.md` §P10.

## What was asked

`PlanarFill.ForRows` takes a permit from `PlanarParallelBudget` in `Parallel.For`'s `localInit`. When
`PlanarFanOut` runs K solves at one frequency, each inner loop asks for up to `cap` workers, so K·cap
pool threads are requested and (K−1)·cap of them should sit blocked in `Enter()`. The .NET pool
injects threads at ~1–2 per second once its minimum is exhausted, so the run should stall for seconds
at every frequency while the pool grows — and if it does, part of the 1.09–1.15× ceiling §6 attributes
to hardware is really scheduling.

## Findings

1. **The consequence does not happen, and the wall clock is the proof.** On the brief's own fixture
   (FR-4 80 mm line, N = 1,980, de-embedded — 5 solves, `cap` = `ProcessorCount` = 10) the fill
   reaches all 10 permits **within ~30 ms of starting** and holds them for the whole fill phase. Peak
   threads parked in `Enter()`: **3**, against the 40 the hypothesis predicts. Pool thread count
   11 → 12 over the point.

2. **The mechanism IS real — it is simply gated by the pool's size, and it costs nothing.** Force the
   pool large first (`SetMinThreads(64)`) and the predicted picture appears in full: 34 threads parked
   at once, 20 thread-seconds parked, 151% of the budget's own core-seconds. **The point still takes
   1.25–1.47 s, exactly as it does with the default pool (1.23–1.37 s), and utilisation goes UP
   (84.6% against 78.7%).** A parked thread burns no CPU and holds no permit; what it costs is a pool
   thread, not time. **This is the measurement that settles the phase** — if injection were the stall,
   pre-supplying the threads would have removed it.

3. **`Parallel.For`'s unmet demand queues; it does not block.** This is why the brief's
   multiplication never materialises on a healthy pool, and it is not obvious from reading `ForRows`.
   `Parallel.For` is a replicating task: a worker queues one replica when it starts and work remains,
   and a replica becomes a thread only when the pool has one to give. The trace shows this directly —
   `PendingWorkItemCount` sits at 3–5 through the entire fill phase while parked threads stay at 0–2.
   K concurrent loops produce K·cap **queued work items**, not K·cap parked threads.

4. **The cap can never outrun the pool's minimum, which is the structural reason it works.**
   `PlanarSolve` materialises a null cap as `Environment.ProcessorCount`, and the pool's minimum
   worker count is also `ProcessorCount`. The fill therefore needs no injected thread to reach its
   cap. **A cap BELOW `ProcessorCount` parks the most threads** (at cap 4: pool 12, eight surplus
   threads, 8 parked, 111% of the budget's core-seconds) and still costs no time.

5. **The pool does grow across a sweep, and it is still not the fan-out's doing.** Over five points
   the pool went 11 → 12 → **24** in one jump, with parked threads spiking to 13. The same sweep
   started with a 64-thread minimum ran **3.05 s** against **3.01 s**. Utilisation over the sweep is
   75%, and the shortfall is the sequential Green's-function fit (0.11 s per point, which the trace
   resolves exactly) and `SymmetricFactorization`'s serial panel steps between its parallel trailing
   updates — both visible in the trace as permits-held-zero with an empty queue.

6. **§6's M3 paragraph did not become false and was not touched.** Its fall-off is attributed to core
   heterogeneity on a 4 + 6 box, and P10 removes the one competing explanation that was still open.

## How the gates were made

**No new timing test.** These are machine measurements; a threshold on them would measure the box.
What is gated instead is the instrument itself, in `ParallelBudgetTests`, both routine and together
under half a second:

- `P10_TheBudgetsCounters_SeeTheFillTheyExistToMeasure` — the counters are wired to the **budgeted**
  branch of `ForRows`, so a future re-measurement is not measuring nothing. It asserts only that they
  MOVE: they are process-wide, another test's fill can touch them at any instant, and an equality
  against zero would be a race against the runner rather than a statement about this code.
- `P10_APermitAlwaysComesBack_SoOneBudgetSurvivesASecondFill` — `PlanarParallel.cs`'s header claims a
  permit always comes back, which is what makes the scheme deadlock-free, and nothing tested it. A
  leaked permit is invisible in one fill and fatal in the next, so the gate is a second fill through
  the same **cap-1** budget, on a background thread with a 60 s join, so a regression fails rather
  than hangs.

`REmp8_TheBudgetPath_IsBitIdenticalToTheOrdinaryCappedPath` and R-emp-13's two sweep tests are
unchanged and still pass — the brief requires bit-identity to survive, and since `ForRows` was not
touched it survives by construction rather than by measurement.

Everything else came from a standalone Release harness, one fixture at a time. `dotnet test` builds
Debug, which changes the cost of the arithmetic relative to the scheduling under study.

## What changed, for a reader who only wants the code

```
src/Engine/Mom/PlanarParallel.cs               PlanarParallelBudget: + WaitingThreads, HeldPermits,
                                               EnterCount, TotalWaitSeconds, ResetCounters
tests/Engine.Tests/Mom/ParallelBudgetTests.cs  + the two P10 gates above
src/Engine/Mom/HISTORY.md                      §P10 — the five tables
```

`ForRows`, `PlanarFanOut` and the budget's semantics are untouched. No answer moved and no
provenance hash changed.

## Not done, on purpose

**The shared row queue was not built.** It is milestone 2, conditional on a stall, and there is none
to remove: `cap` long-lived workers draining one queue would arrive at the same `cap` permits' worth
of arithmetic the present scheme already keeps busy from ~30 ms in. It would also have to re-earn
R-emp-13's bit-identity, which the current scheme gets for free because a row is still written once
by one worker.

**No second cap, and no attempt to size the pool.** Both were forbidden by the brief, and the
measurements say neither would buy anything: the run is invariant to the pool's size across a 5×
range of it.

**The parked threads were not eliminated for their own sake.** They are surplus pool threads holding
no permit, and they cost about a thread stack each. If that is ever worth reclaiming it is a memory
argument on a host that already runs a large pool, not a performance one, and this phase measured no
evidence for it.

---

# P11 — the calibration standards' static capacitance, accelerated (2026-08-29)

`brief-em-p11-accelerated-static-capacitance.md`. **The last step of a de-embedded run that was
always dense is not any more.**

D7 references every de-embedded s-parameter to the line's own `Z_c = γ/(jωC_pul)`, and `C_pul` comes
from differencing two calibration standards' static capacitances. Each of those solved `P q = ε₀·1`
over CELLS with a dense m×m complex LU. Turning the accelerator on moved the DUT's ceiling and left
that one where it was, so a wide-port run whose DUT solved comfortably could still be refused —
measured on the 2026-08-14 owner report, whose wide port's calibration standard meshed at N = 6,466
against a 5,000-unknown dense ceiling.

**`P` is exactly the scalar block M5 already projects.** The accelerated charge stencil is a ± pair of
cell pulses per basis; a cell-pulse operator is the same stencil with one cell and `sign = +1`. So
this is not a second accelerator — `PlanarStaticAim` is the same projection, the same near-set rule,
the same grid FFT and the same sparse-LU-preconditioned GMRES, over CELLS, with one grid kernel and
no ω.

## What was found

1. **The near RADIUS is the only knob that acts, and it must be read in M5's unit, not the natural
   one.** Sizing it from the CELL span — the obvious choice for a cell-pulse operator — halves M5's
   radius on the same mesh, because a basis spans two cells. Measured against the dense solve as the
   relative error in `C_pul` (FR-4 hero's 30.8/90.9 mm standards / GaAs hero's 1.14/3.02 mm), with
   `NearRadiusFactor` in BASIS supports: `3 → 2.69e-7 / 8.88e-7`, `4 → 3.69e-7 / 1.61e-7`,
   `5 → 1.98e-7 / 2.89e-8`, **`6 → 1.04e-7 / 2.02e-9`**, `8 → 3.39e-8 / 7.07e-15`, at 99 / 132 / 164 /
   196 / 256 near entries per row. The cell-span reading would have left GaAs at 8.88e-7 — inside the
   brief's 1e-6 gate by 12%, which is not a margin. So the pitch is sized from the largest CELL span
   (the source support, which is what a stencil has to enclose) and the radius from the largest BASIS
   support (the kernel's own range, which is what P8's 2h floor is about); the file header derives
   both.

2. **The projection ORDER is inert here.** M = 2 / 3 / 4 / 5 give `1.05e-7 / 1.04e-7 / 1.08e-7 /
   1.08e-7` on FR-4 and `2.15e-9 / 2.02e-9 / 2.00e-9 / 2.00e-9` on GaAs. Raising it is not a remedy
   for this operator; widening the near field is, and the non-convergence refusal names both in that
   order.

3. **The GMRES tolerance is not what limits this — the projection is.** The brief asked for the
   tolerance that delivers 1e-6 on the DIFFERENCED result to be measured and set from the
   measurement. It is: `1e-6 → 9.88e-8`, then `1e-8`, `1e-10`, `1e-12` and `1e-14` give `1.04e-7`
   **identically in every printed digit**. `StaticTolerance` ships at **1e-10**, two decades under
   where the answer stopped moving so a standard pair whose lengths are close (a larger
   `C/(C₂ − C₁)` than the 1.5-1.9 these fixtures measure) still has the solve's own contribution well
   under the projection's — and deliberately not tighter, because a tolerance GMRES cannot reach
   turns into a refusal. Cost at the shipped value: 3-4 iterations.

4. **A finer auxiliary grid buys nothing here, which is the opposite of M5's own N-ladder — and the
   two knobs are not independent.** At the shipped radius the pitch ladder is flat below 0.5 cell
   spans (`0.125 → 1.19e-7`, `0.25 → 1.11e-7`, `0.5 → 1.04e-7`) and degrades above it
   (`0.75 → 1.60e-7`, `1.0 → 3.32e-7`). At a radius of 3 supports the SAME ladder has the opposite
   sign (`0.5 → 2.69e-7`, `0.25 → 1.31e-6`, `0.125 → 2.45e-6`). What that says is that refining the
   grid cannot compensate for a near field too narrow to hold the coupling — P8's finding in another
   coordinate.

5. **The setup refusal this brief was told to re-word was UNREACHABLE, and no test had ever seen its
   message.** `brief-em-deembed-ceiling-closeout.md`'s C1 put a de-embedding-specific ceiling check in
   `PlanarSolve.Run` AFTER the calibrators were constructed — but `PlanarPortCalibrator`'s constructor
   builds one `PlanarSolveContext` per standard, and that constructor's own eager
   `SurfaceMesher.GuardCeiling` throws first, with a correct sentence about a mesh that names neither
   de-embedding, nor which port, nor a remedy. (`EmDeembedCeilingTests` gates C2's `StaticCapacitance`
   guard message, which is a different one; the brief's "EmDeembedCeilingTests asserts the sentence"
   was not the case.) The check now runs BEFORE the calibrator is built, on a standard set the
   calibrator is then handed rather than rebuilding, and two tests assert it —
   `EmDeembedCeilingTests.P11_ADenseDeembeddedRun_IsRefusedAtSetup_AndTheRefusalNamesTheAccelerator`
   and its accelerated counterpart.

6. **The dense refusal's first remedy is now the accelerator, not turning de-embedding off.** It used
   to have to say the accelerated solve "will NOT help here"; since the standards' static solve is
   accelerated too, it does help, and the sentence says so and quotes the accelerated ceiling.

## The taper, re-run (the brief's M4)

Reconstructed at the reported geometry (13.1 mm into 299 µm over 28.575 mm on 20 mil RO4350B,
cells/λ 5, edge 3, mesh frequency 500 MHz, 1-5 GHz), straight-flanked rather than Klopfenstein, which
lands at N = 4,239 rather than the reported 7,749 — the exact count was never the point, the width
RATIO is, and the failure mode reproduces exactly: **the WIDE port's calibration standards mesh at
N = 1,498 / 7,603 / 4,273**, one of them past the dense ceiling on its own.

| | before P11 | after |
|---|---|---|
| dense + de-embedded | refused at setup | refused at setup, and the sentence now names the accelerator |
| **accelerated + de-embedded** | **refused at setup** | **runs**: 3 points (1 / 3 / 5 GHz) in **107 s** |

The wide standard's static solve on its own (m = 3,864 cells, n = 7,603): **9.7 s and 222 MB**, at
607 near entries per row and 15.7% fill, against **683 MB** for the three m×m complex matrices the
dense route holds — which the ceiling refuses outright rather than allocating. 9.24 s of the 9.7 s is
the sparse LU of the near matrix; the GMRES solve itself is 72 ms at 4 iterations.

What the user now gets, at the published planes: `|S₁₁|` −1.24 / −2.13 / −1.23 dB and `|S₂₁|`
−6.10 / −4.22 / −6.28 dB at 1 / 3 / 5 GHz, `|S₁₁|² + |S₂₁|²` = 0.998 / 0.991 / 0.990 (passive), with
port 1's `Z_c` measured at 6.59 → 7.00 Ω against the 6.92 Ω the taper was drawn for and port 2's at
82 → 84 Ω against 100 Ω. The port-2 figure is not a P11 result: that port is 2 basis functions wide
and the run raises its own feed-clearance note about it.

## What the gates are

`PlanarP11StaticAimTests`, 12 tests, **0.93 s together** — all routine, none tagged. Deliberately on
the COARSE standards and small uniform plates: every claim is that the accelerated OPERATOR agrees
with the dense one on the same mesh, which a coarse mesh tests exactly as hard.

- the accelerated PRODUCT against `PlanarFill.ScalarPotentialMatrix`'s own `P x` (asked of the
  operator, not only of the solved capacitance, because a solve can absorb a product error into a
  charge vector whose SUM still looks right);
- `C_pul` and each standard's total against the dense solve, to 1e-6;
- the tolerance ladder, kept as a test rather than a note so the default's justification is re-run;
- the self-kernel sentinel moved ×0.1 and ×4 — the near-set completeness gate, since G(0) is
  arbitrary and only legitimately so if every overlapping-stencil pair is near;
- `PlanarStaticLimitTests`' plate-over-ground oracle and its quadrature-separability oracle, through
  the accelerated path, plus the sharper form of the first (accelerated vs dense at each spacing);
- `Aim = null` is today's code asserted as EXACT equality, not to a tolerance;
- an accelerated call with no slab height refuses (a silent dense fallback would reinstate the
  ceiling invisibly);
- the near field is O(m) in entries per row — a COUNTER, not a stopwatch.

`EmDeembedCeilingTests` gains the two setup-refusal gates in item 5 above, on the owner's own taper
shape. `EmCeilingRefusalTests.Gate1_…` is inverted from "refuses at setup" to "no longer refuses at
setup", proved with a **pre-cancelled `RunControl`**: the first cancellation checkpoint is one
Green's-function fit into the first frequency, so an `OperationCanceledException` is positive proof
that setup completed, at a fraction of a second where solving that taper de-embedded is ~40 s per
point.

## No second implementation

The shared parts were MOVED, not copied: `AimProjection` (the Vandermonde inverse, the moment match,
the moment quadrature, the near set) and `AimGridFft` (the circulant embedding and the cyclic
convolution) came out of `PlanarAim.cs` unchanged, and `PlanarAimGeometry`/`PlanarAimOperator` call
them. The near set's exact entries are `PlanarPulsePotential`, which is `PlanarEntryFill`'s own
`P(a, b)` carved out whole — same class-keyed memo, same singular cores — so the near field is
literally the dense path's arithmetic rather than a second formulation of it.

## What changed, for a reader who only wants the code

```
src/Engine/Mom/PlanarStaticAim.cs   NEW — PlanarStaticAim, PlanarStaticAimReport
src/Engine/Mom/PlanarAim.cs         + AimProjection, AimGridFft (moved out of the two AIM classes)
                                    + PlanarAimSettings.StaticTolerance (1e-10, measured)
src/Engine/Mom/PlanarFill.cs        + PlanarPulsePotential (carved out of PlanarEntryFill.P)
                                    + PlanarEntryCores.PrepareScalarPair
src/Engine/Mom/PlanarDeembed.cs     StaticCapacitance: + the accelerated route and a slabHeightM
                                    parameter; GuardCapacitanceCeiling is now public and takes the
                                    route's flag
src/Engine/Mom/PlanarSolve.cs       the standards' ceiling refusal moved BEFORE the calibrator is
                                    built (it was unreachable) and re-worded;
                                    PlanarPortCalibrator takes a pre-built standard set
```

## Not done, on purpose

**The static accelerator does not share the DUT's `PlanarAimGeometry`.** It builds its own
`PlanarEntryCores`, so a mesh that has both pays two class-core warm-ups. Sharing would force the
LONGEST standard's basis geometry to be built even when no frequency ever selects it — which is
exactly the lazy build M4 of the sweep-performance brief put in — and the class store is cheap.
Measured cost of the duplicate on the taper's wide standard: the near fill is 219 ms of a 9.7 s
solve.

**`AimEntry`'s `(M+1)⁴` lookups per near pair were not restructured.** §8's own P6 note records the
same hotspot on M5's operator; here it is 93-354 ms against a sparse LU of 1.6-9.2 s, so it is not
what to fix first on this path.
