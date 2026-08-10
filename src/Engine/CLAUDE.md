# Engine — local conventions

## `RunControl` — cancellation and progress, at a POINT BOUNDARY (2026-08-09)

`src/Engine/RunControl.cs` is the one object an engine takes for both concerns, so a caller wires
them once instead of threading two parameters through every signature. Every entry point that gained
it takes it as a trailing optional argument defaulting to null, so **a null control reproduces the
pre-cancellation behaviour exactly** and no existing caller changed.

**Where each engine checks, and why there is no finer granularity.** `ParametricSweepEngine` per
sweep point, `SParameterEngine` per frequency (both the wave and legacy paths),
`LoadpullEngine` per grid termination, `LoadpullPursuitEngine` per cache-miss query. **None of them
checks inside a factorization, a back-substitution or a Newton loop** — that is precisely where this
engine cannot afford a check, and it is what keeps cancellation cheap enough to be always on. The
cost is that Stop is answered within one point rather than instantly: a 20,301-point sweep stops in
the time one point takes, while a lone HB solve runs to completion. `HbEngine` and
`NonlinearDcEngine` therefore take no control at all — a single solve has no boundary to offer.

**Cancelling abandons the run; it does not produce a partial result.** The per-point DataSets are
stacked along an axis of known length, so there is no shape a half-finished sweep could be published
in. Engines throw `OperationCanceledException`; the caller catches it and publishes nothing.

**Progress counts LEAF work units, and only the innermost countable loop counts them.** A sweep hands
a non-sweep inner analysis a `Child()` — same token, no progress sink — so an inner s-parameter's own
frequency loop cannot also tick and double the numerator; a nested PARAMETRIC sweep is handed the
full control so the innermost sweep is the one counting. Reports are throttled (default ~25/s, the
final tick of a known total always delivered), because every delivered observation is a post onto the
caller's UI thread and unthrottled reporting costs more than the arithmetic it reports on.


## Batched external-device evaluation in the HB inner loop (brief-harmonicarf-h0-h3 M1, 2026-08-06)

`HbNewton` now gathers a device's whole time grid and asks for it in ONE call when the model says an
evaluation costs a round trip. **Built-in models take literally the statements they took before** —
`ComponentModel.PrefersBatchEvaluate` is false for every one of them, and the engine branches on it,
so a built-in device's result is bit-identical by construction rather than by tolerance.

- **Measured, on this machine, taken alone, Release, warm-started across a 1 dB Pin step at K = 5
  (32 grid samples, 3 Newton iterations):** external UNBATCHED **1.37 ms/solve**, external BATCHED
  **0.48 ms/solve** (**2.8×**), built-in Hero-2 SDD **1.643 → 1.644 ms** — unmoved, as the code path
  requires. The batched external figure is *faster than the built-in SDD*, because an amortised
  round trip costs less than that SDD's expression-tree AD over 32 samples.
- **The seam is `ComponentModel.EvaluateBatch(double[][])`** with a scalar-loop default, plus
  `PrefersBatchEvaluate`. `ElaboratedComponent` carries both so the device multiplier stays applied
  in exactly one place. `NonlinearDcEngine` and `HbNewton2D` can adopt it with no second design.
- **The control-current form is always scalar.** `_c_ref(t)` is per-sample by construction and only
  an SDD has one, which is never an external device.
- **`ComputeDevicePortCurrents` batches too** — it is another whole grid per solve.
- Gate: `tests/Harmonica.Tests/ExternalDeviceBatchingTests.cs`. Batched and unbatched HB results are
  **bit-identical**, and "unbatched" is not a flag in the engine — it is a provider whose instances
  expose only the scalar `Evaluate` and inherit `IExternalDeviceInstance.EvaluateBatch`'s default
  loop, which is exactly what a provider that never implemented batching gets. **The fixture had to
  be built**: `tools/fake-osdi-model`'s two devices are both LINEAR, so a Newton loop converges in
  one or two iterations and understates any per-evaluation cost in exactly the ratio being measured.
  A third device, `crf_fet` — a square-law FET with smooth pinch-off, a smooth triode→saturation
  knee, Cgs/Cgd charge and three terminals — was added for it, with its closed form in the library's
  own comment.
- **Every hero golden is unmoved**: `Engine.Tests` 1,003 passed / 1 skipped, the only failure being
  `Hero1BTests`' 10 s wall-clock budget under full-suite load, which `src/Engine/CLAUDE.md`'s L8a
  entry already records as marginal on this machine independently of any phase, and which passes
  alone in 2 s.

## `LoadpullEngine.ComputeFoms` is PUBLIC (same brief, M5/M6)

`FomResult` and `ComputeFoms` were private. harmonicaRF drives its own Pin search and must not
re-derive a single FOM (§0.3 item 5), so the one definition of Pout / Pin_delivered / Gt / Gp is now
shared rather than copied. It is a pure function of its arguments, so no existing caller's result
moves — and it is what makes Tier 3's equivalence run a comparison of two SOLVES rather than of two
formulas. `LoadpullEngine`'s own behaviour, its uniform Pin ladder and every Hero 3/3B golden are
untouched.

## `HbLinearExtractor.ExtractImpedance` — the open-port intermediate (same brief, M3)

Additive, and nothing else in the repository calls it. It returns the interface IMPEDANCE matrix and
the open-circuit voltages, i.e. the same extraction stopped one step before `Y = Z⁻¹`.

**Why the intermediate is worth exposing.** `Y` is the right form for the Newton loop, whose
interface is always TERMINATED and therefore well conditioned. It is the wrong form for a network
whose ports are deliberately left OPEN — harmonicaRF's pre-terminated extraction — because an open
port's driving-point impedance runs to the ideal bias choke's ~10 GΩ while a terminated one sits at
tens of ohms, so `Z` spans eight or nine decades and inverting it spends them. Measured on
harmonicaRF's fixture: closing the terminations after the inversion agreed with direct extraction to
1e-4…1e-7; closing them in the impedance domain agrees to 1e-13. `Extract` and `ExtractDC` still
compute exactly what they did.

The constructor also gained an optional `extraInterfaceNodes`. Null — the default — is the shipped
behaviour exactly: the interface is the nonlinear-facing nodes and nothing else.

## `Mom/` — edge mesh on CURVED geometry: a NEGATIVE result (brief-edge-mesh-on-curved-geometry, 2026-08-09) — **M0 + M1 done; M2 built as a SEAM only**

**A FOLLOW-UP to L8b**, in `src/Engine/Mom/SurfaceMesher.cs`. No Green's function, no fill, no solve,
no user control. **Read `src/Engine/Mom/CLAUDE.md`'s own "edge mesh on CURVED geometry" section before
touching any of it**; every table lives there.

The four things worth knowing from out here:

- **A GRADED FAN ON A STAIRCASED RIM BUYS NOTHING, and that is the deliverable.** On a 96-point disc,
  measured on a converged static capacitance (L8c's Tier 5 harness at εᵣ = 1), rim attractors reach
  **0.331%** of the limit against the shipped mesh's own **0.265%** — no better, and the sampled
  variant is worse at 0.501%. The reason is that a uniformly refined staircased disc does **not**
  converge monotonically: its band across the last three rungs is **0.669%**, wider than every
  difference being compared, and the staircase's own area error wanders over the same range in step
  with it. Refining toward a *tread's* edge resolves the quantisation artifact, not the physics. **The
  answer is that curved geometry needs conformal cells — its own phase, with its own brief.**
- **The CONTROL is what makes that believable, and it is not optional.** On a Manhattan square, the
  same harness and the same quantity, edge grading is **4.437% → 0.431%** at the shipping mesh and the
  uniform ladder needs ~20× the unknowns to catch it. The harness sees grading perfectly well; the
  staircase is what it cannot see through.
- **M0 found that TOTAL N AND MINIMUM CELL BOTH LIE.** Every shipping PCell responds to `EdgeCells` in
  N and shows its min cell collapse ~8×, which reads as "the rim responded" and is false — the fans
  come from the axis-parallel END CAPS, and a taper's rim passes within a bulk cell of its own caps.
  The honest quantity is the transverse grid spacing at the rim point farthest from any axis-parallel
  edge, and it is **dead flat in `EdgeCells`** for MTaper and both MKlopf variants on both starters.
- **§0.1's non-monotone 45° bend was an unrepresentative FIXTURE**, measured against the real
  `MBendPCell` at 45/90/135°: monotone at every angle and inside R17's ceiling. It is asserted rather
  than reported, because a shipping bend that did go non-monotone would outrank the whole brief.

`PlanarRimGrading` ships as a measurement seam with `None` as the default — `PlanarEdgeReference`'s
precedent — and **a Manhattan mesh is BIT-IDENTICAL with it on** (gridlines, cells and bases as
equalities; §10.7's hero still exactly N = 552). One user-visible change: when an axis collects no
attractor the mesh report now says **"…but NO edge grading was actually applied…"** instead of
claiming a fan that exists nowhere on the artwork.

Gate: **`tests/Engine.Tests` +4 routine (~0.2 s) and +2 `Category=Benchmark` (4 m 52 s together);
`tests/Ui.Tests` +2 routine.** Nothing outside `src/Engine/Mom/SurfaceMesher.cs` and the two test
files was touched.

## `Mom/` — G_A^zz's ceiling: M1 is a NEGATIVE result, M2 is the direct path (brief-gazz-accuracy-ceiling, 2026-08-06) — **M0+M1+M2+M4; M3 not started**

Continues the M0 entry below. **Read `src/Engine/Mom/CLAUDE.md`'s own G_A^zz sections before touching
any of it.** Four things from out here:

- **M1 IS A NEGATIVE RESULT, and three of the five knob groups the brief names cannot reach the failing
  component at all.** `Dcim.FitAtHeights` never reads `BranchPointOrders`, `BranchSamples` or
  `BranchExtent` — L9c made the interior sum rule a theorem by inspection, so there is no branch-point
  sampling to configure — and `FitTolerance` is inert too. Asserted as bit-identity over 30
  configurations rather than argued. The knobs that DO reach it give **10.4× at best (14 → 1.35),
  still 71× outside the ≤ 1.9e-2 envelope, while making the error 23× WORSE inside ρ/λ ≤ 0.1** where
  the kernel is used today. **No per-component setting is simply better**, so R-zz-2's plumbing was
  not built.
- **M2 ships `PlanarFillSettings.DirectVerticalKernel` (default off)** — the ẑẑ block alone takes its
  kernel from `SommerfeldIntegral.EvaluateInterior` instead of the fit, reachable exactly like
  `UseRadialTable = false`. It converges in the table's sample count (**8.3e-5 at 128, 2.9e-6 at 256**)
  and costs **19–43% of a de-embedded point per via span per frequency**. The one trap worth knowing:
  **tabulate the REMAINDER, not the kernel** — the kernel still diverges as 1/ρ after the static
  asymptotes come out, and a linear table would be worst exactly at the self and touching pairs.
- **THE FINDING is (b), and it is not what the brief expected: on §10.7's own FR-4 hero with two vias
  18 mm apart — the one layout M0 left refused — the FITTED block is 4.53e-7 from the direct one.**
  L9c measured the fit POINTWISE; the refusal is asked of a MESH; nobody had asked the second question,
  and essentially none of the pointwise error survives into the assembled block there. **Do not widen
  the constant on that** — it is one layout on one stack, and the GaAs cross-check (where L9c's
  pointwise error is 130× larger) **was NOT RUN**: the fixture was mis-sized and did not finish in
  35 minutes. A small-mesh, high-frequency GaAs fixture is the most valuable next measurement here.
- **M4: the constant did NOT move.** `Dcim.ValidatedRhoOverLambdaAtHeights` is byte-identical, because
  M1 showed nothing supports widening it. `PlanarSolve.VerticalRangeVerdict` now takes the fill
  settings and skips the refusal — with a note — when the direct kernel is on, since that limit is a
  property of the FIT and not of the integrator it was measured against.

Gate: **`tests/Engine.Tests` 1,002 passed + 1 pre-existing skip — the routine tier is UNCHANGED in
size**, because both new methods are `Category=Benchmark`. `tests/Ui.Tests` **4,741** and
`tests/Firewall.Tests` **4/4**, both green; nothing outside `src/Engine/Mom/` and
`tests/Engine.Tests/Mom/` was touched.

## `Mom/` — G_A^zz's accuracy ceiling: M0 (brief-gazz-accuracy-ceiling, 2026-08-06) — **M0 ONLY; M1–M3 are measured deferrals**

**A FOLLOW-UP to L9**, independent of the other two. It closes the single limit that stopped a
via-bearing full-wave run on ordinary board geometry. **Read `src/Engine/Mom/CLAUDE.md`'s own
G_A^zz section (at the end of that file) before touching any of it.**

The four things worth knowing from out here:

- **§10.7's FR-4 HERO WITH A VIA NOW RUNS AT 10 GHz**, and no constant moved.
  `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` is untouched — what changed is *which ρ it is asked
  about*. `PlanarSolve`'s own comment already said the limit "binds ONLY the ẑẑ block" and the code
  two lines later asked the MESH DIAGONAL. `G_A^zz` has exactly two consumers anywhere, both between
  two VERTICAL bases, so the right quantity is the extent of the **via footprints**. On the hero
  that is 0.024 λ against the mesh's 0.674 λ: REFUSED → **PASS** for one via, and for two vias 1 mm
  apart.
- **Two vias genuinely far apart still refuse, and that is correct.** At 18 mm the fit really is
  asked about 0.617 λ, the regime L9c measured at 14×. Narrowing the question is not widening the
  answer — and the refusal now names the separation as being **between vias**, quotes the mesh
  diagonal beside it, and says outright that shrinking the surrounding metal does not act on it.
  "Move the vias closer" and "make the board smaller" are different instructions.
- **NARROWING EXPOSED THAT THREE COMPONENTS WERE GOVERNED BY NOTHING, and that is a finding rather
  than a gap.** Scoping `G_A^zz` to the via footprints leaves `G_A^xx`, `G_q` and the MIXED
  component's interior pairings unchecked — and the mixed block couples a via to *every* horizontal
  basis, so its ρ genuinely spans the mesh. `Dcim.ValidatedRhoOverLambdaInteriorHorizontal = 1.0`
  records what L9c actually measured (≤ 1.9e-2 out to ρ/λ = 1), and every general-kernel run now says
  whether it is inside that or PAST it. **A note, not a refusal** — reporting "unmeasured" is honest;
  refusing on it would invent a limit and would refuse structures accepted today.
- **TIER 1 IS INSTRUMENTED, NOT ARGUED**, because that is the whole soundness case. A new optional
  `PlanarFillDiagnostics` records the widest separation the ẑẑ arm actually reaches: 56.57 µm against
  the 56.57 µm the refusal checked — **equal, not merely bounded** — on a mesh 412 µm across. And the
  fill is **bit-identical with and without it attached**, since an instrument that perturbed what it
  measures would be worse than none.

**M1 (the DCIM knob sweep), M2 (direct integration instead of a fit) and M3 (a depth search) are not
started**, and the residual they would close is the two-vias-far row above. The brief's own §7 names
M0 as the natural fault line and possibly the only milestone needed. **Do not reach for an
amplitude-conditioning cap** — L9c measured it worse (14 → 39).

Gate: **1,002 routine tests in `tests/Engine.Tests`** (+4), no new Benchmark methods. `tests/Ui.Tests`
**4,741 and green**; `tests/Firewall.Tests` 4/4.

## `Mom/` — the GROUND VIA: the attachment basis (brief-ground-vias-and-interior-electrostatics, 2026-08-06) — **PART A, M1 + M3 (M2 deliberately not built; M4 partial; Part B not started)**

**A FOLLOW-UP to L9, like the via z-integral before it.** It closes gap A — the thing L9's own phase
gate found and named as the most valuable remaining work here: **a backside via was not representable
by this kernel at all.** **Read `src/Engine/Mom/CLAUDE.md`'s own ground-via section (at the end of
that file) before touching any of it**; every measured table lives there.

The five things worth knowing from out here:

- **A BACKSIDE VIA NOW WORKS, and it is right to 0.081%.** L9c's via basis spans two adjacent MESHED
  levels; a via to ground joins a signal level to the laterally infinite PEC the Green's function
  handles analytically, which is never one. The new ground-ATTACHMENT (half) basis is gated against a
  closed form — a bar of length ℓ plus its **equal-direction** image (L9e's T2_1 earned that sign) —
  **worst 0.081% over ℓ/w ∈ [0.01, 5] and a 16× range of w**, the same span T3_1 uses for an interior
  via. Through the product path a drawn backside via on the MMIC starter now extracts, meshes and
  produces 4 vertical unknowns where before it was dropped with a note.
- **THE CHAIN WAS MEASURED AND NOT BUILT, and the brief's own premise is what the measurement
  refuted.** §0.2 item 3 argues a chain of z-segments is mandatory because a 100 µm GaAs backside via
  at 30 GHz is k·ℓ = 0.23, 4.5× over `MaxElectricalLength`. Measured on an ATTACHED via — the only
  kind that exists in a real structure — subdividing into 8 moves the answer **0.077% at k·ℓ = 0.23
  and 0.141% at k·ℓ = 1.0**, with the current only 1.5–2.0% non-uniform. A FLOATING rod does move
  (10.2%), but that movement is **98% static** — identical at k·ℓ = 0.01 and 0.23 — so it is the
  floating end condition, not electrical length. Meanwhile the chain costs **14.2% of a de-embedded
  point at n = 8** and grows ~4× per doubling. **`MaxElectricalLength` is 0.05 → 0.30 instead**, from
  the measurement rather than from an O((kℓ)²) argument. Widening it unlocks nothing on its own:
  `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` is the limit that actually binds and is untouched.
- **TWO STRUCTURAL INVARIANTS GENUINELY BREAK, and both are re-gated rather than exempted.** L9c's
  D5 (`∫∇·f dS = 0`) does not survive a one-pulse basis — its net charge is −1, balanced by the
  plane's IMAGE, and adding a compensating pulse would double-count the image the Green's function
  already carries. And L8c's `s_A + s_B = 0` fails, so the extracted CONSTANT stops cancelling in
  exactly one row with nothing noticing. **That one does not bite, and it is measured rather than
  reasoned**: the answer is invariant under `PlanarExtractionOrder` to 1.09e-15 / 3.28e-9 on a row
  where the cancellation is provably gone.
- **THE SIGN CONVENTION IS DELIBERATELY NOT THE BRIEF'S, for reciprocity.** D4 calls the net charge
  "+1"; this keeps EVERY vertical basis's current flowing +z instead, so the ẑẑ block needs no
  per-basis direction factor and reciprocity stays structural with an attachment and an interior via
  in the same mesh — which is exactly the MMIC starter. The gate asserts the attachment↔via cross
  block is non-zero, because that block is precisely what a direction slip would invert while leaving
  `Z` symmetric and wrong.
- **L9c's TWO SILENT MESHER FAILURES DO NOT TRANSFER FOR FREE** and are re-asserted for the ground-via
  path specifically: the footprint must contribute hard GRIDLINES (or the via vanishes with no error)
  and must NOT get the edge grading a conductor rim gets (adding it grows N 1.08×, not 5.8×).

**Not built, on purpose or on cost:** the z-segment chain (measured, above); M4's shunt λ_g/4 SHORTED
stub through the product path (its three-claim gate is the strongest end-to-end statement available
and is the first thing a continuation should write); R-gv-8's ω → 0 static-limit rung; and **all of
Part B** — the interior electrostatic Green's function, `C_pul` at a buried level, and L9c's still
un-run Tier 4, which is now the most valuable remaining work in this area.

Gate: **997 routine tests in `tests/Engine.Tests` in ~2 min** (+4 over the z-integral's 993), plus
**+2 methods tagged `Category=Benchmark`** in this area (`T4_1` 11 s, `M1_2` 13 min — M1 is a
measurement, and its cost is the four chain fills it takes alone). `tests/Ui.Tests` **4,741 and
green** with **two gate tests UPDATED rather than loosened** (a backside via now extracts instead of
being dropped; the electrical refusal now fires at 0.30 with the measurement in its wording);
`tests/Firewall.Tests` unchanged (4/4).

## `Mom/` — the via's z-integral: removing the midpoint rule (brief-via-z-integral, 2026-08-06) — **COMPLETE (M1–M6)**

**A FOLLOW-UP to L9, not a sixth slice of it** — it fixes the one defect L9e found and deliberately
bounded rather than repaired. **Read `src/Engine/Mom/CLAUDE.md`'s own follow-up section (at the end of
that file) before touching any of it**; the split, the cost table and both wrong oracles live there.

The six things worth knowing from out here:

- **THE VIA IS PHYSICALLY RIGHT NOW.** L9e measured a via's terminal inductance high by
  **≈ 0.673·(ℓ/w)** — 4.9% on §10.7's own 3 µm-over-40 µm MMIC post, 220% at ℓ/w = 5. Re-measured
  against the FILL over three footprint widths spanning 16×, the same sweep is **flat to 0.124%**
  across ℓ/w ∈ [0.01, 5]. **And n = 1 now equals n = 8**: L9e's split-via chain
  (55.3% → 1.14% at ℓ/w = 1) is reproduced by a SINGLE via at every rung, so subdivision is an
  INVARIANCE rather than a convergence.
- **A PLAIN GAUSS RULE IN z DOES NOT WORK, and that is the whole design.** At Δ = |z−z′| > 0 the
  kernel's ρ-structure lives on the scale Δ, which the fill's 8-node cell quadrature and its
  2%-of-a-cell radial table cannot resolve when Δ ≪ cell — and Δ ≤ ℓ is exactly the regime the defect
  lives in. Worse, a discrete rule keeps a `Σ_a w_a²·C/ρ` the true integral does not have. So the
  integral is SPLIT: the two extracted asymptotes (whose coefficients are **measured at exactly 0
  drift** with the heights, and whose depths are exactly Δ and Σ_b) are integrated over the two prisms
  in CLOSED FORM in z; everything else — bounded, smooth in z — takes an ordinary rule applied to the
  TERMS rather than to the entry, so the fill's O(N²) work is untouched.
- **L9c's COST PREMISE was false, and M1 is what says so.** It declined the z-integral because "it
  would need a fit per z-quadrature node rather than one per pairing". Measured: a fit is 89.5 ms, the
  count is a property of the PAIRING SET rather than of N (every via of one drawn layer spans the same
  two levels), and n_z = 4 adds 15 pairings — **1.58 s per frequency, 1.05% of a 149.9 s de-embedded
  point**. End to end a de-embedded point reads **65.5 s taken ALONE**, against L9d's own 71.9 s.
- **`MaxLengthOverWidth` is RETIRED and `MaxElectricalLength` is now about the BASIS.** The geometric
  bound has nothing left to refuse. The electrical one stays because a via basis is ONE z-rooftop per
  gap, so its current is uniform along the whole length — exact for a short via, wrong for a resonant
  one however well the kernel is integrated, and no quadrature removes it. **Retiring the geometric
  bound WIDENS NOTHING**: `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` already restricts every
  via-bearing run to electrically small structures and is untouched.
- **A NEGATIVE RESULT, kept.** L9c's four-family span does NOT make a fifth height pair a
  constant-coefficient combination of four fitted ones — derivably, because the four basis functions
  are themselves functions of k_ρ so the recombination does not survive the inverse transform.
  Measured at **8.8e-4**, which is *inside* the interior fit's own envelope and therefore does not
  contradict it either; refuted by derivation, not by measurement, and recorded as such.
- **TWO ORACLES WERE WRONG FIRST — the eighth and ninth times in this area.** A uniformly panelled
  quadrature read 9.5e-5 from the new lifted-rectangle closed form at c = 1e-3 while agreeing to 2e-15
  at c ≥ 0.05 — the CHECK failing to resolve a spike 50× narrower than its own panel, not the closed
  form. Grading the panels closed it to 1e-13. And `T2_2` had to be re-pointed from
  `MidpointInductance` to `ExactInductance`, because reproducing the midpoint value is now the failure.

Gate: **993 routine tests in `tests/Engine.Tests` in 70 s** (L9e's baseline: 992 in 65 s), plus
9 methods tagged `Category=Benchmark` in this area (~20 min). `tests/Ui.Tests` **4,740 and green** with
one gate test UPDATED rather than deleted (`L9PhaseGateTests`' ℓ/w refusal now asserts that the
geometry it used to refuse is ACCEPTED); `tests/Firewall.Tests` unchanged (4/4).

## `Mom/` — L9e: adaptive sampling, the N budget, the refusal audit (brief-L9e-adaptive-sampling-budget-and-the-phase-gate, 2026-08-05) — **M1-M4 COMPLETE; M5's PHASE GATE IS NOT**

**Last of L9's five slices, and the only one that is mostly engineering rather than physics — except
that the physics check nobody had built turned out to be the finding.** **Read
`src/Engine/Mom/CLAUDE.md`'s own L9e section before touching any of it**; the measured curves, the
gate proposal and every deferral's number live there.

The seven things worth knowing from out here:

- **THE VIA IS NOT PHYSICALLY RIGHT, and the kernel and the fill are both innocent.** *(FIXED by the
  z-integral follow-up above; the bound named at the end of this bullet is retired. The measurement
  stands and is what the fix is gated against.)* §0.2 item 1
  asked for the check that had never existed. `G_A^zz` at εᵣ = 1 over a PEC is free space plus a
  POSITIVE image to ≤ 3.0e-4 (against 21 for a negative one, which is what earns the sign); the ẑẑ
  entry reproduces an independently-integrated closed form to ≤ 5.1e-5 across ℓ/w ∈ [0.075, 10].
  **What is wrong is L9c's MIDPOINT RULE, and it is wrong about a quantity nothing bounded.** Its
  stated cost, O((kℓ)²), is about the wave factor alone; the same substitution also freezes 1/R over
  the via's length, which is a purely GEOMETRIC condition — measured, the via's inductance is high by
  **≈ 0.67·(ℓ/w)**: 4.9% on §10.7's own 3 µm-over-40 µm MMIC spacer, and R-via-6's electrical bound
  admits ℓ/w ≈ 12 (≈ 5× too large) at 10 GHz on GaAs. A second, GEOMETRIC bound now ships
  (`PlanarLevels.MaxLengthOverWidth`), carrying the measured slope and naming the remedy — **splitting
  the via across intermediate levels converges** (55% → 1.1% for n = 1 → 8 at ℓ/w = 1), which is the
  refusal's own advice, now measured.
- **The adaptive-sampling collision was resolved by separating the EXPENSIVE from the ORDERED.** The
  calibrator must be stepped in increasing frequency order; refinement inserts points mid-band. The
  solve depends only on the frequency, the branch continuation only on the order — so the standards'
  raw matrices are cached per frequency and the whole sorted set is replayed after every insertion at
  **zero extra solves** (`PlanarPortCalibrator.SolveCount` is the counter), reproducing the sequential
  branch decisions **bit for bit**. The alternative (predicting βΔℓ from the pre-solve ε_eff) was
  rejected on L8d's own measurement that the estimate runs 15-20% low.
- **Tier 1's dense reduction passes EXACTLY**: with the tolerance at zero, the adaptive path
  reproduces the non-adaptive sweep bit for bit. Off, the path is L8d's own — pinned by
  `MultiLevelPortTests.M1_1`'s hand reconstruction, unchanged.
- **R17 polices a MESH; the user experiences a RUN.** Measured exactly (16·N², plus `CoreBytes`): a
  de-embedded run at N = 2,932 holds **209 MB** live, projecting to **~607 MB at the ceiling against
  the 381 MB the refusal's own message quotes**. The constant is defensible; its message is not.
  Changing it is the owner's call.
- **The near-DC hole L8e recorded is CLOSED**, because M1 is what makes it reachable — an adaptive
  scheme chooses its own frequencies. `Dcim.CanFitAtFrequency` refuses k₀H < 1e-6 (the 6 Hz point that
  spent 50 s and ended in a raw framework exception) and refuses L9b's `PathExtent·k₀H < 1` band with
  its measured numbers and the extent the user would need. **D8's decision, stated so neither option
  is left open:** a frequency-aware path extent is the right fix and is not a one-line change — the
  sample budget must rise with the extent, and `Samples` is what L8a's whole accuracy table is
  calibrated against.
- **ACA is DEFERRED with the number.** Far-field blocks of a real N = 790 fill need **53-62% of
  min(m,n)** in rank at 1e-3, even with a full pivot that overstates a practical scheme. The blocks
  reachable under R17's ceiling are simply not many wavelengths apart. Two precedents for a measured
  deferral: L7b-b's Route B and L9c's amplitude cap.
- **The refusal audit's sweep now catches ANY phase letter, and immediately caught one more** —
  `ModalDecomposition` still promised "arrives at L7b-b", which L7b-b delivered. Two further offenders
  were found by hand outside the sweep's phrasing, which is the argument for the sweep being a floor
  rather than the audit.

**§11's L9 gate sentence: a PROPOSAL is on the record** (in the Mom note) for the owner to rule on —
strike *"agreement with published reference structures"*, replace with three external-data-free
self-consistency checks. ~~**L9's phase gate itself is NOT built**~~ — **it WAS built**, in a later
pass (`tests/Ui.Tests/Em/L9PhaseGateTests.cs`, three gates, two of them `Category=Benchmark`), and it
found two things before it passed anything: the Ui-side via extraction was dead code, and a BACKSIDE
via is not representable by this kernel at all.

Gate: **992 routine tests in `tests/Engine.Tests` in 65 s** (+19 over L9d's 973, and just over the
~60 s ceiling — see the Mom note for why and what the trade is), plus **7 methods tagged
`Category=Benchmark`, ~10 min**. `tests/Ui.Tests` **4,737 and green** with two tests UPDATED rather
than loosened; `tests/Firewall.Tests` unchanged (4/4).

## `Mom/` — L9d: ports on more than one level, references, de-embedding (brief-L9d-multilevel-ports-and-references, 2026-08-05) — **COMPLETE (M1–M5)**

**Fourth of L9's five slices, and the first one that turns a two-level `Z` into an s-parameter.** L9c
left a matrix nothing excited. **Read `src/Engine/Mom/CLAUDE.md`'s own L9d section before touching any
of it**; the Ui half is in `src/Ui/Layout/Em/CLAUDE.md`. It is also the first L9 slice whose blast
radius is not `src/Engine/Mom/` — the extractor, the port inference and the provenance stamp all moved.

The six things worth knowing from out here:

- **THE COST, measured rather than projected: 71.9 s per de-embedded point, ~73 minutes for a
  101-point sweep, at N = 514 with two levels and one via.** Against L8d's own 7.66 s at N = 552 that
  is **9.4× at essentially the same N** — so what moved is not the unknown count but the **per-entry
  cost of the general kernel**. L9c's note projected ~4.3× from N alone and was measuring the wrong
  quantity. This is what makes L9e's adaptive frequency sampling non-optional.
- **A real performance bug came out of measuring it.** `PlanarFill.FillMultiLevel` re-integrated D6's
  frequency-independent geometric cores four times per matrix entry instead of looking them up — and
  because **a calibration standard is always single-level**, every one of its entries took that branch.
  Invisible at L9c (one fill, one small fixture); crippling the moment de-embedding exists.
- **The kernel is a DISCRIMINATED WRAPPER, and the failure widening it would have caused was in the
  CACHE.** `PlanarKernelSet.For()` copied its fit cache per view, so a de-embedded solve would have
  refit every pairing per MESH — L9c's measured 9 fits per frequency becoming 9 per mesh, with no
  answer anywhere looking wrong. The `DcimModel` fit is now shared across views; only the cheap
  per-view terms are derived. **R-mlp-1's bit-identity is pinned by RECONSTRUCTING L8d's own call
  sequence**, not by a tolerance.
- **A port ON A VIA is refused, and the refusal is EARNED by showing it is a different OBJECT.** A
  vertical basis has no end in the layout plane, its unit current already crosses its footprint, and
  there is no cell beyond the cut to reference against. Driving the horizontal rooftops at the same
  (x, y) is a perfectly good port — a *different* one. Measured structurally: every port row lies in
  R-via-5's horizontal prefix.
- **De-embedding refuses a port on a BURIED level, and the reason is electrostatics rather than
  levels.** C_pul is differenced from two standards' static capacitances, and the only static Green's
  function here is an image series over a **grounded slab**. The de-embedded S is *referenced* to the
  Z_c that produces, so a wrong C_pul renormalises every published s-parameter. Points at L9c's
  **un-run Tier 4** — the single most valuable thing anyone could build next for this area.
- **G_A^zz's ρ/λ ≤ 0.1 limit is now a REFUSAL, scoped to meshes that actually carry vertical current**
  (the same structure at 100 GHz is refused *with* a via and solves *without* one). It binds real
  answers: §10.7's FR-4 hero is ~0.67 λ across at 10 GHz, so a via-bearing DUT that size is refused
  outright; an MMIC at a few hundred µm is ~0.01 λ and passes comfortably.

Gate: **973 routine tests in `tests/Engine.Tests` in 51 s** (L9c's baseline: 965 in 51 s — the +8
routine tests cost nothing measurable), plus **9 methods tagged `Category=Benchmark`, ~6 min**: every
de-embedded point and every multi-level fill. `tests/Ui.Tests` **4,737 and green**; `tests/Firewall.Tests`
unchanged (4/4).

## `Mom/` — L9c: z-directed current, vias and the multi-level problem (brief-L9c-z-directed-current-and-vias, 2026-08-05) — **COMPLETE (M1–M5)**

**Third of L9's five slices.** **Read `src/Engine/Mom/CLAUDE.md`'s own L9c section before touching any
of it** — the derivation, the measured ladder, the Tier 5 table and three findings live there.

The eight things worth knowing from out here:

- **There are FOUR kernel components, and "the horizontal one with the TE line swapped" is wrong
  TWICE.** A z-directed current enters the TM line as a **series voltage source**, so `G_A^zz` and the
  mixed component are built from two transmission-line Green's functions this repository did not have
  (`I_v`, `I_i`) — obtained from one cascade traversal by running the ladder on the DUAL line. And
  **`G_A^zz` carries a TE term even though a vertical dipole radiates no TE field**: it is not the
  field, it is what is left after subtracting −∇φ, and φ uses a TM−TE `G_q`.
- **THE IMAGE SIGN FLIPS because the CURRENT reflection does.** A PEC is a short: the voltage
  reflection is −1 and the current reflection is +1, and the vertical component is built from a
  current. Measured at **4.1e-15 / 9.6e-15**, mixed component identically zero to 2.1e-15.
- **THE FAR-FIELD SUM RULE IS A THEOREM FOR THE INTERIOR FIT TOO — for a different reason, and the
  first version of the fit asserted its ABSENCE and was wrong.** L8a's rule holds because `1 + Γ`
  cancels a pole; an interior source's kernel is simply FINITE at its own branch point, so
  `M(k_zm) = 2j k_zm·K` vanishes by inspection. Measured as O(k_zm) on all 24 cases before it was
  imposed. **Asserting the absence of a theorem needs the same evidence as asserting one.**
- **THE CROSS-REGION PAIRING IS NOT WORSE THAN THE SAME-REGION ONE**, which is the answer to the
  question §10.2's warning was about — and it is the **opposite** of what the branch point this slice
  located suggested (−3.45 j k₀, 0.137 of the DCIM far path on GaAs, closer than the 0.178 that cost
  L9b 59×). Both are true and both are reported: **locating a cut is not measuring its cost.**
  Inside `Dcim.ValidatedRhoOverLambdaAtHeights = 0.1` every component on every grounded stack is
  **≤ 2.8e-3** — inside L9b's envelope. Above it `G_A^zz` reaches 14× on GaAs, from a diagnosed
  rank-deficient depth pair (Σ|A| = 1.1e9), and the refusal is worded on that.
- **THE VIA IS A ROOFTOP ONE DIMENSION OVER.** A footprint that is one cell of L8b's shared grid makes
  a vertical basis a cell pair *in z*, with the same ±1/Area divergence pulses — so charge
  conservation and junction continuity are exact **by construction** (asserted as equalities, D5) and
  L8d's single factorisation is untouched. D2's construction (b), a separate basis plus a constraint
  row, was never reached for.
- **TWO MESHER FINDINGS, both silent failures.** A via footprint must contribute GRIDLINES or the via
  **vanishes with no error** (measured: a 40 µm footprint sat between cell centres at 169.6 and
  269.3 µm and produced zero unknowns). And it must **not** get the edge grading a conductor rim gets
  — grading it measured 2,448 unknowns against 424 on the same fixture.
- **N for §10.7's hero: 552 one level, 1,104 two levels, 1,140 with a via — 2.07×, well under R17's
  5,000.** The brief's worry that two levels plus vias would cross the ceiling does not materialise.
- **THE FILL EXISTS AND ONLY ONE OF ITS THREE NEW BLOCKS NEEDED NEW MACHINERY.** The scalar block
  generalises for free (∇·f is the same pulse, and the geometric cores are in-plane so the height pair
  enters only through coefficients); the ẑẑ block IS the scalar block's cell-pair integral with
  `G_A^zz` and a factor ℓ_mℓ_n; only the ẑx̂ block is new, because its dyadic entry is a **∂/∂x**
  rather than a value. It reproduces L8c's own fill on a one-level mesh to **6.8e-7** through two
  independent fits and two independent extractions, `Z` is symmetric bit-identically with a non-zero
  mixed block, and **D7's projected ~12 fits per frequency measured 9**. The via is treated as
  electrically short (kℓ = 2.3e-3 on a 3 µm spacer) with the long-via case refused by name.

Gate: **963 routine tests in `tests/Engine.Tests/Mom/`; the whole routine `Engine.Tests` tier is 963
tests in 45 s**, inside the ~60 s ceiling. Plus 3 methods opt-in via
`--settings circuitrf.benchmark.runsettings` (~3 min). `tests/Firewall.Tests` unchanged (4/4) and
**`tests/Ui.Tests` unchanged and green (4,732)** — which is D6's "make the new members optional so the
extractor keeps compiling" decision gated rather than argued. Nothing outside `src/Engine/Mom/` and
`tests/Engine.Tests/Mom/` was touched.

## `Mom/` — L9b: DCIM for the general layered medium (brief-L9b-dcim-for-the-general-medium, 2026-08-05) — COMPLETE

**Second of L9's five slices.** L9a built the spectral kernel for an arbitrary stratified medium; this
makes DCIM work for it. **No mesher, no basis functions, no `G_A^zz`, no vias, no ports, no
`IEmKernel`/`EmCapabilities` change, nothing in `src/Ui`.** **Read `src/Engine/Mom/CLAUDE.md`'s own L9b
section before touching any of it** — the derivations, the measured curve and the two new limits live
there rather than being repeated here.

The six things worth knowing from out here:

- **THE FINDING: the second branch point is a STRUCTURAL obstruction, not an accuracy problem.** An
  open-below stack makes Γ depend on `k_zb = √(k_b² − k_top² + k_z0²)`, which carries a second branch
  cut at `k_z0 = ±j·k₀√(εᵣµᵣ − 1)` — on the imaginary axis, in the half-plane the sampling path runs
  into. DCIM fits Γ as a sum of exponentials in k_z0, which is **entire** and cannot carry a cut. On a
  4 µm oxide over silicon the error reaches **59× the free-space kernel on G_q and 2.3e+4× on G_A**,
  against ≤ 1.6e-2 everywhere admitted, and no `BranchPointOrders` setting touches it. Refused by name.
  **`LayerStacks.OpenBelow` is degenerate for this question** (alumina in air, k_b = k₀ exactly, the two
  branch points coincide) and must not be the fixture anyone concludes from — so `PlanarExtractor`'s
  ungrounded refusal can be narrowed for an equal-density bottom and never for a denser one.
- **A fit per source/observer height pair is simply WRONG for the top half-space.** The height pair is an
  exact shift of every fitted image — same amplitudes, depth `b_i + Σ`, poles scaled by the real decay
  `e^{−αΣ}` — so the sum rule and the far-field theorem survive it untouched and `DcimModel.Evaluate(ρ, z,
  z′)` needs no refit. The interior case is **also** an exact shift, of FOUR families in the source
  region's own `k_zm` (measured: four coefficients from four height pairs predict a fifth to **9.8e-15**),
  and it is handed to L9c as a scoped job with its oracle requirements enumerated rather than half-built.
- **L9a's cost projection was against the wrong quantity, by 15–35×.** A general-medium fit is
  **11–102 ms per frequency per kernel — the same as the shipped one-layer closed-form fit (96 ms)** — not
  1.4–3.4 s. The fit samples the top-interface reflection ladder, not `KernelAtHeights`, and most of its
  wall clock is Prony and the amplitude solve, which the stack does not touch. D7's cascade cache is also
  **2.5–3.7×**, not the ~2× L9a named. **L9c/L9d are scheduled against ~0.2 s per frequency at any height
  pair**, so a 101-point sweep of a two-level structure is minutes, not hours.
- **Two limits L8a did not have, both now in the refusal.** A **near-field floor** at
  `ρ/λ = 1/(2π·PathExtent) = 5.3e-4` — derived, stack-independent, invisible on a single 1.6 mm substrate
  and dominant once a 3 µm layer is in the stack (1.8e-1 below it, 1.6e-2 above). And an **electrically
  thin UNGROUNDED stack**, measured and bracketed (k₀H = 0.021 fails at 1.7e-1, 0.105 passes at 2.1e-2)
  — with the mechanism **not** isolated and the refusal saying so, because the obvious candidate was
  tested and ruled out.
- **The far-field sum rule survives generalisation and is still a theorem** (`|1 + Γ|` = 2.2e-16 on every
  stack), and the `BranchPointOrders` table re-run on the multilayer stacks says **order 1 stays the
  default** — best or within 1.6× of best everywhere except GaAs's G_q, where order 0 was already better
  at L8a and reproduces here through a completely different kernel.
- **The shipped one-layer fit is BIT-IDENTICAL**, pinned by exact equality on twelve dumped configurations
  after the shared internals were refactored. But **the general path's fit is NOT bit-identical to it and
  cannot be**: the samples agree to 2.7e-11 while Prony's order choice and the three-candidate scoring are
  discrete decisions taken on them, so the image count differs while both fits are equally good against
  the oracle. The honest gate is Tier 4's, not an image-by-image comparison.

Gate: **400 routine tests in `tests/Engine.Tests/Mom/` (+22); the whole routine `Engine.Tests` tier is 942
tests in 44 s**, inside the ~60 s ceiling. Plus 5 test methods (22 cases) opt-in via
`--settings circuitrf.benchmark.runsettings`, **~11 min** — of which 6 m 40 s is the ORACLE self-check on
the two open-below stacks, run before a single number below it was believed. `tests/Ui.Tests` and `tests/Firewall.Tests` unchanged. Nothing outside
`src/Engine/Mom/` and `tests/Engine.Tests/Mom/` was touched; `Dcim.ValidatedRhoOverLambda` and its refusal
string are byte-identical.

## `Mom/` — L9a: the general layered medium (brief-L9a-general-layered-medium, 2026-08-05) — COMPLETE

**First of L9's FIVE slices, and L9 must not be attempted as one** — §11's L9 row is strictly more work
than L8, which needed five. This slice is the spectral kernel for an arbitrary stratified medium with
source and observer at arbitrary heights, plus its oracle ladder. **No DCIM change, no mesher, no basis
functions, no vias, no ports, nothing in `src/Ui`.** **Read `src/Engine/Mom/CLAUDE.md`'s own L9a section
before touching any of it** — the conventions, the branch-rule finding, the measured ladder and the cost
table live there rather than being repeated here.

The six things worth knowing from out here:

- **The shipped one-layer kernel is UNTOUCHED and is the gate.** `GroundedSlab`, `SpectralGreens`'s
  closed forms and `StaticGreens` all still work exactly as L8 shipped them; the general medium is built
  alongside and gated by *exact* agreement with them — **Γ^e, Γ^h and Γ^q to 6.2e-14 / 7.1e-14** across
  both starters, five frequencies and the whole k_ρ range. Collapsing the two is a later, separate,
  measured decision (the L7b-b precedent).
- **THE FINDING: the proper-sheet branch rule is not an analytic function, and R-lyr-4 needs one.**
  `ProperRoot` negates its own result whenever `Im(w) < 0`, so it flips sign on half of any contour in
  w = k_ρ² — which silently broke the small-k_ρ Taylor extraction that replaces L8a's exact 0/0
  cancellation. **The reflection coefficients stayed perfect to 2e-16 while Γ^q's k_ρ → 0 limit came out
  5% wrong.** Nothing but the direct comparison against L8a's closed form would have caught it.
- **Split-a-layer invariance is 1e-13, not bit-identical, and the reason is reported rather than
  tolerated**: `exp(a)exp(b) ≠ exp(a+b)`. The internal interface itself IS exactly transparent — 7 of 16
  samples are bit-identical and the vector kernel's worst deviation is 9.8e-15.
- **The pole finder had to change TWICE for one silent wrong answer.** A uniform scan cannot see a thin
  slab's TM₀ (it sits 1e-12 of the range above cutoff), and a "safe" 1e-9 endpoint guard band excluded it
  outright. Counts now match the slab's own cutoff conditions exactly; **the ungrounded stack carries a
  TE mode at every frequency measured, where a grounded slab has none until 25 GHz.**
- **Cost: the cascade is 6.8× the closed form at one layer and 17× at three** (0.202 → 1.37 → 3.42
  µs/sample). At L9b that multiplies by the number of height pairs, so a DCIM fit at L8a's own sample
  budget lands at 1.4–3.4 s per frequency per height pair. The one-off costs are not the problem (Taylor
  extraction 98 µs, pole search 5.8 ms); the per-sample cascade is, and caching the TM/TE ladder pair is a
  named ~2× that L9b should not have to rediscover.
- **§11's L9 gate sentence does not survive this project's own rules** — see the L9a section for the
  proposed replacement. Nothing was built on unverifiable published data.

Gate: **378 routine tests in `tests/Engine.Tests/Mom/` (+28); the whole routine `Engine.Tests` tier is 920
tests in 43 s**, inside the ~60 s ceiling. Plus 4 opt-in via `--settings circuitrf.benchmark.runsettings`
(~8 s). `tests/Firewall.Tests` unchanged. Nothing outside `src/Engine/Mom/` and `tests/Engine.Tests/Mom/`
was touched, and no user-facing string, refusal or capability changed.

## `Mom/` — L8e: the kernel registry, the planar `DataSet`, and current density (brief-L8e-results-registry-and-the-phase-gate, 2026-08-05) — COMPLETE

**Last of L8's five slices.** The engine half is small on purpose: the registry §10.3.4 has been
deferring since L6, kernel B's own entry point, its diagnostics group, and the current-density
reduction. **Read `src/Engine/Mom/CLAUDE.md`'s own L8e section before touching any of it.**

The four things worth knowing from out here:

- **`EmKernelRegistry` unifies the OUTPUT contract, not the input.** `IEmKernel` is still exactly
  kernel A's — it consumes an `EmProblem`. Kernel B consumes a `PlanarProblem`, which L8b's D1 makes a
  SIBLING of `EmProblem` rather than a subtype, so a shared input interface could only be `object` or a
  union. What is genuinely shared is `EmKernelOutcome`: kind, kernel name, `DataSet`, `EmSuitability`,
  notes. `EmCapabilities.Planar`, declared at L6 and read by nothing since, finally has a consumer.
- **Auto-selection takes extractor VERDICTS, not geometry, because it has to.** Both extractors are in
  `src/Ui/Layout/Em/`, behind the firewall; the registry is here. The good side effect is that D2's
  rule is testable in `Engine.Tests` with no layout document — 16 tests, milliseconds. Auto prefers A
  whenever A accepts (about a thousand times cheaper, and exact for a uniform cross-section), falls to
  B when A refuses, and when neither accepts it refuses quoting BOTH. Explicit stays explicit in both
  directions.
- **The planar diagnostics group is `"planar"`, deliberately NOT `"tline"`.** Same `DataSet` shape as
  kernel A — `S`, per-port `Z0`, one diagnostics group — but a per-unit-length quantity from a 2-D
  quasi-static solve and one back-solved from a de-embedded full-wave S-matrix are different claims.
  They agree on a uniform line (that agreement is L8's phase gate) and diverge with frequency, which is
  dispersion and is a *result*.
- **The current-density reduction lives in the engine, and its two surprising consequences are
  documented rather than smoothed:** an outermost cell carries HALF what its neighbour does and that is
  correct (a rooftop spans two cells); and the exact identity is against the two adjacent EDGE
  currents' mean, not against the port current. One excitation, one frequency, no superposition — and
  the solve captures the column during the sweep the panel already pays for, so the map costs no second
  factorisation.

Nothing outside this slice's own files changed: `SurfaceMesher`, `PlanarMesh`, the cell/basis ordering,
the fill quadrature and L8d's calibration settings are all untouched. `PlanarSolve` gained three
*optional* settings and three result fields for the captured column; its arithmetic did not change.

Gate: see `src/Ui/CLAUDE.md` — L8's phase gate runs through the product path, not through the kernel.

## `Mom/` — L8d: ports and de-embedding (brief-L8d-ports-and-de-embedding, 2026-08-05) — COMPLETE

**Fourth of L8's five slices, and the first that produces a number a user would recognise.** L8c left
a matrix nobody excited; this adds the right-hand side, the per-frequency solve, and the two-line
calibration §10.6 calls mandatory and R-mom-15 calls *"real work at L8"*. **Read `src/Engine/Mom/
CLAUDE.md`'s own L8d section before touching any of it.** Scope is the engine only: no `DataSet`, no
`.snp`, no kernel registry, no `IEmKernel` change, nothing in `src/Ui` — all L8e.

The six things worth knowing from out here:

- **A port is an INCIDENCE MATRIX, and that is what makes the whole slice small.** L8c normalised the
  rooftop to unit current across its shared edge, so a delta-gap of *v* volts reacts with it as exactly
  *v* — no gap width, no quadrature. With `B` carrying ±1 on each port's row, `Y = BᵀZ⁻¹B`: one
  factorisation, P back-substitutions, and reciprocity structural for the third time in this area.
- **THE FINDING: what limits de-embedding accuracy is RADIATION, not the algebra.** The de-embedded S
  of a uniform section is exact at the two lengths the calibration was solved from (|S₁₁| = 8.5e-16)
  and drifts away from them — **not monotonically in the standard's length**, and scaling with
  frequency as **f²**. Both are the signature of direct radiative and surface-wave coupling between the
  ports, which decays only algebraically and has no term in a "box + matched line + box" model.
  Measured on 1.6 mm FR-4: a section that should be matched reads 3.9e-4 at 2 GHz and 6.0e-3 at 10 GHz.
  **A longer feed does not fix it**, which is why R-prt-4's "minimum feed length" came back as a
  negative result rather than a number.
- **A and B agree on a uniform line — the phase table's own gate — to 0.01% at 1 GHz**, then diverge
  upward by dispersion, tracking the Kirschning-Jansen closed form to 0.9% out to 10 GHz. **That
  divergence is a RESULT, not an error**; it is one of the two things kernel B exists to compute.
- **Three branch decisions, and one of them was a real bug.** The obvious rule for γ's sign — negate if
  `Re γ < 0`, since a passive line has α ≥ 0 — flips β too, and α is two orders of magnitude smaller and
  its extracted sign is noise. On FR-4 at 20 GHz it turned a correct β = 804 into 1492. β selects the
  branch; α is only a tiebreak.
- **Two calibration standards do NOT cover a 2–20 GHz sweep.** One line separation covers 8:1 (TRL's
  own [20°, 160°]); 2–20 GHz is 10:1. The count is derived from the band, designed to 4:1 for margin,
  and aimed at 60° rather than 90° because the pre-solve ε_eff estimate runs 15–20% low.
- **De-embedding costs 4.4× the bare fill, and the standards are 78% of it** — they are 2.58× the DUT's
  own unknowns on §10.7's hero (7.66 s per frequency against L8c's 1.73 s, so ~780 s for a 101-point
  sweep). The cheapest saving is not in the fill: making L8b's edge grading exactly mirror-symmetric
  would let the two ports of a plain microstrip share one calibration, which they currently do not.

## `Mom/` — L8c: the fill and the singular integrals (brief-L8c-fill-and-singular-integrals, 2026-08-05) — COMPLETE

**Third of L8's five slices, and §10.2's SECOND named place a schedule goes to die** — item 4, the
singular self- and near-term integrals. **Read `src/Engine/Mom/CLAUDE.md`'s own L8c section before
touching any of it**; the six derived closed forms, the quadrature rule and every measurement live
there rather than being repeated here. Scope is the basis, the fill and the dense factorisation only:
no ports, no excitation, no s-parameters, no `IEmKernel` change (L8d/L8e).

The six things worth knowing from out here:

- **There are THREE singular pieces in this kernel and the second is the one that gets missed.**
  Besides the obvious `1/ρ`, every surface-wave term carries a real `ln ρ` — `H₀⁽²⁾ = J₀ − jY₀` and
  `Y₀ → (2/π)(ln(z/2)+γ)` — and a grounded slab always has at least one surface wave. Both are
  extracted and integrated with an ANALYTIC inner integral; only a smooth remainder goes through
  quadrature. That is the whole payoff of the rectangular mesh: the classic near-singular difficulty
  comes from doing *both* integrals numerically, and here only one of them is.
- **R-fil-8's condition FAILS on the FR-4 starter, and that was the finding of the slice.** "The
  fitted images are smooth" holds only while no image depth is small against a cell — measured,
  `min|b|/cell` is **0.165 at 10 GHz and 0.079 at 20 GHz** on FR-4 (against 6–13 on GaAs). The
  remainder therefore is not smooth on the mesh's own scale, and a 3-point rule was **5% wrong** on
  the self entry while converging so gently (n^-2.2) that it looked converged at every step. The
  remainder rule is 8 points near because of that measurement, not by taste.
- **The fill is three decades more accurate than the kernel it fills from, and the report says so.**
  Against the εᵣ = 1 reduction — where the kernel is exact and only the quadrature can be wrong — the
  whole matrix is right to **5.0e-6**. Against direct Sommerfeld integration with the real DCIM
  kernel, worst over both starters and 2/10/20 GHz, it is **5.4e-3**, i.e. L8a's own ≤ 6e-3 kernel
  error. Chasing the quadrature further is wasted work.
- **The ORACLE was wrong first, for the third time in this area.** The Sommerfeld comparison initially
  read 2.4e-2 on FR-4 at 20 GHz; the cause was the oracle's own radial table clamping its
  interpolation stencil at the top of its range (2.1e-3 there, against a DCIM error of 4.2e-6 at the
  same ρ). Refining the oracle is what separated the two, and `T2_4` now pins the oracle's own
  convergence at 3.7e-6 so a future disagreement starts from a known-good reference.
- **R-msh-5's deferred half is CLOSED and the conductor-width default survives.** Measured on a
  converged static capacitance of §10.7's hero: the default lands **0.18% from the consensus limit at
  N = 552**, the cell-size alternative 0.11% at N = 787. The mechanism is on the record too — the
  conductor-width edge cell does not shrink with cells/λ, so its flat refinement sequence means "already
  at its own limit" rather than "converging to the truth", and it sits ~0.35% low. That is inside any
  EM tolerance and is what keeps an ordinary GaAs line under R17 (L8b measured N = 7,562 for the
  alternative there).
- **The cost is a FINDING, not a pass.** A 101-point sweep of §10.7's own 552-unknown hero takes
  **~3 minutes**, and what dominates is the FILL rather than the LU — right up to R17's ceiling
  (114× the LU at N = 552, still 1.8× at N = 4,933). D6's reused frequency-independent core is 62% of
  a single-frequency solve at the hero size, so it earned itself; its cached arrays also add **51% on
  top of §10.7's own 400 MB memory line**, which is worth knowing before anyone believes 5,000 is
  comfortable. The two ways out — per-cell-pair moment caching for the vector remainder (~4×) and
  §10.7's own adaptive frequency sampling — are named in the Mom note rather than left for L8d to
  discover.

Gate: **297 routine tests in `tests/Engine.Tests/Mom/` (+81), ~11 s**, plus 17 opt-in via
`--settings circuitrf.benchmark.runsettings` (~6 min: the oracle sweep, the convergence studies,
R-fil-12's measurement and all of Tier 8). `tests/Ui.Tests` and `tests/Firewall.Tests` unchanged.

## `Mom/` — L8b: the surface mesher and the N report (brief-L8b-planar-mesher-and-overlay, 2026-08-05) — COMPLETE

**Second of L8's five slices, and it adds NO PHYSICS** — it turns drawn geometry into cells, counts
them, and hands the count back before anything is solved. **Read `src/Engine/Mom/CLAUDE.md`'s own L8b
section before touching any of it**; the grid-model decision, the reference-length measurement and
the staircasing numbers live there rather than being repeated here.

The five things worth knowing from out here:

- **N for §10.7's own worked example is 552, and every non-Manhattan shipping PCell is under R17's
  5,000 ceiling.** The design note predicts "a few hundred" for the FR-4 hero and that is what comes
  out. MBend/MTaper/MKlopf land at 536-2,055 across both starter technologies, so **no one-click
  library part exceeds the budget** — which is the answer D8 was scheduled against, measured rather
  than assumed.
- **The grid model is TENSOR-PRODUCT, and the thing that makes it affordable is per-AXIS spacing.**
  Each axis's pitch comes from the narrowest run measured along that axis, so a taper gets a fine
  transverse pitch and a coarse axial one. Isotropic spacing on the §10.7 taper would have been
  ~15,000 unknowns; per-axis makes it 714.
- **N is BASIS FUNCTIONS, not cells, and R17 is about N.** A rooftop spans a pair of adjacent cells,
  so N is the number of shared internal edges — about 2× the cell count. Reporting cells while
  budgeting basis functions is a factor-of-two error in the one number this slice exists to produce.
- **Kernel A's edge-grading CODE was reused; its FINDING was not, and the difference is measured.**
  R-mom-8's cell-size field composes over any geometry and is called directly. But its "the reference
  length is the metal THICKNESS" conclusion does not carry over — a planar sheet has no thickness —
  and the cell-size alternative measures at **N = 7,562 on an ordinary GaAs line, over the ceiling**,
  against 705 for the conductor-width reference. The convergence half of that measurement needs a
  solver and is named as L8c's rather than faked.
- **The staircased mitre SURVIVES (2.8% cut-area error, 18 cells, N 550 vs 586 unmitred), and the
  smooth tapers are the real finding.** Local width error on MTaper/MKlopf is 17-24% worst and
  5.5-11% RMS while the global AREA error is 0.5% — and a Klopfenstein's whole value is a controlled
  equiripple |Γ| of 0.05, so the local number is the one that matters. A recommendation for L8c is in
  the Ui-side note.

Gate: **22 routine tests in `tests/Engine.Tests/Mom/SurfaceMesherTests.cs` (~0.3 s), none tagged
`Benchmark`** — this slice's sweeps are milliseconds, not the CPU-heavy kind L8a had to make opt-in.
Tier 6 and the PCell half of Tier 7 are in `tests/Ui.Tests/Em/PlanarMeshPCellTests.cs`, because the
PCell generators are in `src/Ui` and the reference graph is `Ui → Engine`.

## `Mom/` — L8a: the layered Green's function (brief-L8a-layered-greens-function, 2026-08-05) — COMPLETE

**Kernel B's foundation, and a DIFFERENT KERNEL rather than an increment on A.** Kernel A's whole
design was an escape from Sommerfeld integrals; that escape is not available for a full-wave planar
solver on a grounded slab, and nothing in L8a shares code with kernel A. **Read
`src/Engine/Mom/CLAUDE.md`'s own L8a section before touching any of it** — the formulation's
derivation, three algebraic facts that silently destroy it, the measured accuracy range, and two
findings about the ORACLES are recorded there rather than repeated here. Scope is the Green's
function and its oracle ladder only: no mesher, no fill, no solve, no ports, no `EmProblem` or
`IEmKernel` change, no kernel registry (L8b–L8e).

The six things worth knowing from out here:

- **The deliverable is a MEASURED RANGE, not "DCIM works" (R-lgf-4).** Against direct Sommerfeld
  integration over ρ/λ ∈ [1e-4, 10], both starter substrates, 2/10/20 GHz: error as a fraction of
  the free-space kernel — what a matrix fill actually experiences — is **≤ 6e-3 across the entire
  span**, and that is the number L8c should be scheduled against. Strict relative error is ≤ 1e-2
  out to **ρ/λ ≈ 1** and degrades to 0.25–0.57 at ρ/λ = 10. `Dcim.WithinValidatedRange` is the
  R-mom-17 refusal that words it.
- **The far-field defect had an exact cause and the fix is a theorem.** `1 + Γ` vanishes identically
  at the branch point k_ρ = k₀, so the coefficient of the 1/ρ far field is exactly
  `(1 + Γ(∞)) + Σ A_i` — and the physical far field has no 1/ρ term. The sampling path never visits
  k_z0 = 0, so an unconstrained fit extrapolates that cancellation and leaves the error behind as an
  uncancelled 1/ρ: **187% at ρ/λ = 10 on GaAs.** Imposing `Σ A_i = −(1 + Γ(∞))` exactly fixes it.
  Higher Taylor orders at the same point are also exact statements and make it *worse* — measured,
  tabulated, and the reason the default is order 1.
- **`FitResidual` does NOT predict the spatial error — the same finding as L7b-b's
  `ModeCouplingResidual`, for the same reason.** GaAs's best spectral fit belongs to a configuration
  whose far-field error is one of the worst. It is the honest measure of what the fit did; only the
  oracle says what the answer is worth.
- **Both ORACLES were checked before anything was concluded from them, and one was wrong.** The
  static image series had to carry a COMPLEX K; written with a real εᵣ it sat a
  frequency-*independent* 1.1e-6 from the kernel's ω → 0 limit, which reads exactly like a
  convergence floor. Refining the integrator 100× moved the answer by 7e-11 while the discrepancy
  did not move — that is what ruled it out. Fixed, convergence is exactly quadratic.
- **Bessel functions were WRITTEN, not added as a dependency**, because the root `CLAUDE.md`
  reserves that to the owner — defining series only, with the asymptotic coefficients computed from
  their product rather than transcribed. Measured to 2.9e-13 against an integral representation and
  5.6e-11 against the Wronskian. Likewise no general complex eigensolver: Prony plus Durand-Kerner
  reaches GPOF's poles, consistent with L7b-b's decision not to write one.
- **5 tests are tagged `Category=Benchmark` for another test's budget, not their own runtime** (the
  heaviest is ~3 s). `Hero1BTests` gates on a 10 s wall-clock budget and is **marginal on this
  machine independently of this phase** — measured at 4.2–9.6 s with these tests excluded from the
  full-solution run — so its budget was left alone and this phase's reporting sweeps were made
  opt-in instead.

Gate: **194 routine tests in `tests/Engine.Tests/Mom/` (+25), ~4 s**, plus 5 opt-in via
`--settings circuitrf.benchmark.runsettings` (~5 s).

## `Mom/` — L7b-b: the general modal decomposition (brief-L7b-b-general-modal-decomposition, 2026-08-05) — COMPLETE

Any N conductors, symmetric or not, through **one** path. **Read `src/Engine/Mom/CLAUDE.md`'s own
L7b-b section before touching any of it** — the Route A derivation, the `Ti` normalisation that puts
the reported `Zc` in ohms, and four findings that changed the phase are recorded there rather than
repeated here. It **partly supersedes the L7b section in that same file**, which says so at the top.

The five things worth knowing from out here:

- **Route B was NOT built, because D2 said the measurement decides and the measurement said no.**
  Route A (`Gevd(Re[C], [L]⁻¹)` on the lossless problem, loss carried perturbatively) is wrong by
  **4.9e-4 in |S|** on a realistic asymmetric copper pair swept 100 kHz–20 GHz at tanδ up to 0.2, and
  by **1.7e-2** in a fixture built to break it (100 mm of 1 MS/m metal, 10:1 widths, four decades
  below its own Wheeler crossover). Two orders of magnitude below the `[C]` solve's own
  discretisation error. A hand-written Hessenberg+shifted-QR complex eigensolver is a real
  numerical-methods commitment; this does not earn it.
- **A symmetric pair cannot measure Route A's error — it is EXACT there**, because `[1 1; 1 −1]`
  diagonalises any `[a b; b a]` and every one of [R], [L], [G], [C] has that form for a mirror-
  symmetric pair. The brief's own G1 fixture therefore had to be replaced: the measurement is taken
  on an ASYMMETRIC pair against a closed-form 2×2 complex eigen-decomposition (the quadratic formula,
  no eigensolver library) that shares the block construction with production, so the comparison
  isolates exactly the approximation being measured.
- **`ModeCouplingResidual` does NOT predict the terminal error** — the two are anti-correlated in
  frequency, and at 20 GHz the error exceeds the residual, so it is not even a bound. G1 said to
  report that rather than loosen a tolerance. It is still the right diagnostic (it is the honest
  measure of what was discarded, and it is loud where the error is worst); it is not a predictor.
- **On a discretised mesh the general path is ~3 orders of magnitude CLOSER to exact than L7b's
  forced modal matrix** (8e-6 against 8.9e-3). L7b forced the modal matrix a *perfectly* symmetric
  pair would have, but the solved matrices carry the mesher's own diagonal asymmetry — so forcing
  `[1 1; 1 −1]` was itself the larger approximation. It survives as a test oracle only; nothing
  should be reinstated on the grounds that it was the better answer.
- **Reciprocity stays STRUCTURAL at exactly L7b's strength.** Every block is `Tv·diag(x/e)·Tvᵀ`,
  symmetric for any Tv, assembled so `[i,j]` and `[j,i]` are bit-identical; *S* then inherits that
  through `RFNetwork.ZToS` to solver tolerance, precisely as it did before.

Gate: 169 tests in `tests/Engine.Tests/Mom/` (+30), ~3 s, none tagged `Benchmark`. Three L7b tests
were **updated, not loosened** — they asserted refusals that pointed at L7b-b, which is what L7b-b
delivers.

## `Mom/` — L7b: the symmetric coupled pair (brief-L7b-coupled-lines-and-cosim, 2026-08-05) — COMPLETE

Coupled-line s-parameters as a 4-port, plus `.snp` back-annotation into the schematic. **Read
`src/Engine/Mom/CLAUDE.md`'s own L7b section before touching any of it** — the modal algebra, the D3
port map, and two findings that changed the design are recorded there rather than repeated here.

The four things worth knowing from out here:

- **No eigensolver is involved, and that is load-bearing.** A symmetric pair decouples with the fixed
  matrix `[1 1; 1 −1]/√2` by symmetry alone. NumFlat's complex EVD is Hermitian-only and returns REAL
  eigenvalues, while a lossy line's γ² is genuinely complex — verified against NumFlat 1.3.0 directly.
  Asymmetric pairs and N > 2 need that eigensolver and are refused by name (L7b-b).
- **The five `[0,0]` collapses in `RlgcExtractor` are opened** — `∂L/∂n` is a per-surface N×N matrix
  (each conductor receded ALONE), `R_dc` is diagonal, `R`/`G` have matrix forms. `MatrixFillCount` is
  still exactly 4 for a single line, so R-mom-11's counter gate is unchanged.
- **The single-line answer is bit-identical**, verified by reconstructing the pre-L7b extractor and
  comparing at full precision — two arithmetic re-associations that moved `R` by one ulp were found
  and reverted. The Tier 3 oracles carry tolerances and could not have caught that.
- **R-cpl-8 is asked of the GEOMETRY, not the solved `[C]`.** Testing `C₁₁ ≈ C₂₂` is the obvious
  implementation and it wrongly refuses a mirror-symmetric pair whose mesh is merely under-resolved
  (measured: 6.8% on a 1 µm strip, converging to 0.99% under refinement). The matrix version survives
  as a "refine the mesh" warning.

Gate: 139 tests in `tests/Engine.Tests/Mom/` (+25), ~2 s, none tagged `Benchmark`. **Tier C3's
published even/odd fit was NOT obtainable and is reported as such** — substituted by a merged-strip
limiting case against an independently computed geometry, converging to 0.075%.

## `Mom/` — the quasi-static MoM kernel, kernel A (brief-L6-L7-mom-kernel-a, 2026-08-04) — COMPLETE

`src/Engine/Mom/` is the 2D quasi-static per-unit-length EM kernel for phases L6/L7 (engine half only
— no UI, no `.clay`, no `.snp`). **It has its own `CLAUDE.md`: read `src/Engine/Mom/CLAUDE.md` before
touching anything in there.** Every sign convention of the formulation, the two deliberate deviations
from `layout-view.md` §10.5's meshing wording, and the two findings about the closed-form oracles
themselves are recorded there rather than repeated here.

The four things worth knowing from out here:

- **R-mom-1 — the kernel consumes a neutral `EmProblem`, not Ui types.** `layout-view.md` §10.3.4's
  original `Solve(LayoutFragment, Stackup, …)` signature is not simultaneously satisfiable with §10.7's
  "lives in `src/Engine/Mom/`", because those types are in `src/Ui/Layout/` and the reference graph is
  `Ui → Engine → Core → RfCore`. `EmProblem` is the SI-unit cross-section model the Ui-side extractor
  produces; the design note has been corrected. `tests/Firewall.Tests` still passes unchanged.
- **`ChargeSolver` takes an `EmMesh`, not an `EmProblem`** — the physics is stated over segments, and
  that is what lets the exact *cylindrical*-interface oracles be tested at all.
- **R-mom-11 is enforced by a counter, not a comment.** `RlgcModel.MatrixFillCount` is asserted at
  exactly 4 for both a 3-point and a 1001-point sweep, because "[C], [C₀] and ∂L/∂n are
  frequency-independent" is the whole performance story and is easy to lose in a refactor.
- **Results follow the house convention exactly** — `SNP` → `DataSetBuilder.FromSnp` → per-port `Z0`
  cube, plus a `"tline"` group. No new result type.

Gate: 114 tests in `tests/Engine.Tests/Mom/`, ~2 s, none tagged `Benchmark`. Tier 3 (microstrip vs
Hammerstad-Jensen, 0.1 ≤ W/h ≤ 10, FR-4 and GaAs) lands at ≤ 1.3% on ε_eff and ≤ 0.6% on Z₀ against a
±2% requirement.

HB spectrum stage 2 — harmonic axis carries integer orders (brief-hb-spectrum-2-order-axis, 2026-06-23) — COMPLETE: `HbEngine.BuildSingleToneDataSet` now stores integer **orders** `[0,1,…,K]` (unit `""`) on the harmonic axis instead of frozen `k*f0` Hz values. Physical frequency is reconstructed `order × f0(slice)` everywhere it is needed (geometry, markers, stems, X label) via `HbSpectrum.HarmonicFreqHz`. The per-slice fundamental is resolved from `ToneFreqs[sweep…,tone=0]` by `PlotInspectorViewModel` and injected into the Trace via `SetSpectrumFundamentals(f0ByX)` before each `SetCubeData`/`SetFamilyData` call. Export (`.npy`) emits integer orders + the `ToneFreqs` cube; consumers reconstruct `order × f0`. **Follow-ups:** (a) two-tone `mixIndex` → integer indices with `ToneFreqs`-based mix-frequency reconstruction; (b) optional physical-frequency column in the Table. 6 gate tests (1 Engine, 5 Ui). Build 0W/0E; 429+376+1409+4 total tests pass.

HB spectrum stage 1 — ToneFreqs metadata (brief-hb-spectrum-1-tone-metadata, 2026-06-23) — COMPLETE: Every HB run now emits a stacking `ToneFreqs` cube carrying the per-operating-point fundamental(s): single-tone `ToneFreqs[tone(1)]=[f0]` added in `HbEngine.BuildSingleToneDataSet`; two-tone already emitted `ToneFreqs[tone(2)]=[f1,f2]`. Both are non-`__`, so `DataSet.StackSweepAxis` prepends the sweep axis giving `ToneFreqs[sweep,tone]` with per-point tone frequencies. `HbSpectrum` static (`src/Core/Expressions/HbSpectrum.cs`) centralizes the index/order→frequency rule (`HarmonicFreqHz`, `MixFreqHz`). `ToneFreqs`/`MetaMixOrder` hidden from the signal picker. 5 gate tests. Build 0W/0E.

Var-unit-wins sweep override (brief-var-unit-wins-consistency Part A, 2026-06-23) — COMPLETE: `ParametricSweepEngine.Run` now computes `effUnit = sweep.Spec?.Unit ?? origVar?.Unit ?? ""` and `baseUnit = Units.BaseUnit(effUnit)`, then injects each swept-point override as `new Variable(name, value, baseUnit)` (scale-1, value unchanged). The non-empty `baseUnit` causes `Elaborator.BuildGlobalScope` to call `MarkGlobalHasUnit`, putting the swept variable into `GlobalsWithExplicitUnit`. `FreqUnit.ResolveHz` then fires var-unit-wins → ToneUnit is not re-applied → the HB tone frequency is the correct base-unit value (e.g. `1e9` Hz, not `1e18`). The swept axis is tagged with the same `baseUnit` (supersedes the prior `origVar?.Unit ?? ""` source, so `Unit=GHz` on a unit-less VAR now also tags the axis correctly). When `effUnit` is empty (no sweep unit, no VAR unit), `baseUnit=""` → override stays unit-less (unmarked), unchanged. 3 gate tests in `tests/Engine.Tests/Parametric/SweepFreqVarDoubleUnitTests.cs` (T4: NoDoubleApply; T5: Override_Marked; T6: Equals_NoSweep). Build 0W/0E; 375+425+1398+4 total tests pass.

Parametric-sweep axis unit tagging (brief-sweep-axis-marker-units Part A, 2026-06-22) — COMPLETE: `ParametricSweepEngine.Run` now tags the swept axis with `Units.BaseUnit(origVar?.Unit ?? "")` so that marker readouts and X-axis labels display per-swept-variable units (e.g. `freq=2 GHz` instead of `RFfreq=2000000000`). SweepValues are already in base SI — the unit tag is for display only and activates the existing frequency/unit rendering machinery in `Trace`. Gate tests: `ParametricSweepAxisUnitTests.cs` (T1 GHz→Hz, T2 no-unit→empty, T3 pF→F). `Units.BaseUnit` added in Core (Part B). Marker family else-branch appends unit in `Trace.BuildCubeMarkerBoxLines` (Part C). Build 0W/0E; 1397+422+370+4 total tests pass.

`MeasurementEvaluator` resilience (brief-cube-broadcast-measurements Part B, 2026-06-22) — COMPLETE: `EvaluateInto` and private `Evaluate` now return `IReadOnlyList<string>` instead of `void`. Each measurement is wrapped in an individual try/catch with `continue` on failure; errors are collected into `List<string>` and returned. Successful cubes are always emitted to the DataSet regardless of how many other measurements fail. `SchematicRunService` captures the returned errors and surfaces them via the existing `errors` list. Gate tests: T7 `Measurement_NestedSweep_BroadcastsSweptVar` and T8 `Measurement_Resilient_OneBadDoesNotNukeRest` in `tests/Engine.Tests/Measurements/BroadcastMeasurementTests.cs`.

SDD control-current referenceable set += V_1Tone/V_nTone (brief-sdd-control-current-tonesource, 2026-06-19) — COMPLETE: `ToneSourceModel` (both `V_1Tone`/`V_nTone` spellings — one model) is now referenceable as an SDD control current `C[n]=<toneSrc>`, alongside {Vdc, IProbe, L, SnP, ZnP}. A tone source is a Group-2 branch-current element structurally identical to `VdcModel` (its `Stamp` allocates a branch and pins Va−Vb=E(ω)), so its branch current is a first-class unknown. **One real gap:** `ToneSourceModel` now exposes `public int LastBranchIndex` (set in `Stamp`, mirrors `VdcModel`). **Three resolver sites** each gained a `ToneSourceModel` case validating it as two-terminal (`Cport` absent/1), kind label `"V_1Tone/V_nTone"`: `NonlinearDcEngine.GetControlBranchIndex`, `HbEngine.GetControlBranchIndexHb`, `SParameterEngine.ResolveSParamBranchIndex` (allowed-kinds strings updated in all three). No factory change (it stores raw instance names; kind validation is the resolvers' job). In S-param a tone source is not an `IsSParamPort`, so `StampAll` stamps it normally (E=0 off its tone → a quiet 0 V branch) in both wave/legacy paths → branch exists, referenceable. **P1Tone deliberately excluded** (3-node, two HB branches behind an internal impedance, ambiguous "current" — own brief if needed). 5 gate tests in `tests/Engine.Tests/Nonlinear/SddControlCurrentToneSourceTests.cs` (DC read-through, HB mirror via series IProbe, S-param non-singular, V_nTone shared path, Cport rejection). Build 0W/0E.

SDD control-current (`_cn`) S-parameter column (brief-sdd-control-current-sparam, 2026-06-19) — COMPLETE: completes the control-current arc across all three analyses (DC, HB, S-param). **New MNA primitive** `IMnaContext.AddNodeBranchCoupling(node, branch, coeff)` (impl in `MnaSystem`: `int n=Col(node); if(n>=0) Accum(n, branch, coeff)`) — the `(node-row, branch-col)` transpose-position of `AddConstraint`, which is exactly what a node-KCL dependence on a branch current needs. **`SddModel.StampLinearized` override** (base stays control-free) adds, after the `Y[p,q]` block, the column `col[p,n] = DControl·H[0] + DControlCharge·H[1] + Σ_{w≥2} JacCtrl_w·H[w]`, stamped `+col` at the port-+ row and `−col` at the port-− row — matching `NonlinearDcEngine`'s branch-column sign so DC and S-param agree at ω→0. **Two architecture wrinkles solved:** (1) the new primitive; (2) `ControlBranchIndices` is **per-run** — branch numbering differs between the DC/HB and S-param assemblies (the wave path skips ports), so `SParameterEngine.ResolveSParamControlBranches` re-resolves each referenced device's branch index against a throwaway `StampAll` pass that replicates the real solve assembly, then writes it into `ControlBranchIndices` before the frequency loop (topology-invariant across ω). The DC pre-pass seeds `SddModel.ControlBias` (via `NonlinearDcEngine.CaptureControlBias`) so the small-signal sensitivities are evaluated at the DC operating point (exact for linear-in-`_cn`, where the sensitivity is seed-independent; the bias matters only for a nonlinear `_cn` dependence). DC-nonconverged fallback zeros `ControlBias`. Referenced device that allocates no branch → clear error. 11 gate tests in `tests/Engine.Tests/Linear/SddControlCurrentSParamTests.cs` (equivalence-to-built-in, sign/DC-agreement at ω→0, reactive charge column, all five kinds non-singular incl. SnP+ZnP, resolver errors, control-free regression). Build 0W/0E.

Nonlinear-device small-signal seam + DC-biased S-parameter (brief-nonlinear-engine-seam, 2026-06-19) — COMPLETE: `ComponentModel.StampLinearized` added (Core); `SParameterEngine` gains a DC pre-pass that runs `NonlinearDcEngine` once when `Kind==Nonlinear` devices are present, threads `dcNodeVoltages` into both wave and legacy paths, and routes nonlinear devices through `StampLinearized` in `StampAll` instead of `Stamp`. Purely-linear S-param runs are byte-identical (no DC pre-pass). Two helper methods: `BuildBias` (device port-voltage vector from DC node solution) and `NodeV` (1-based index safe). Fallback policy: zero-bias note when DC solves to ≈0 V (`sparam-zero-bias`); warn-and-continue on non-convergence (`sparam-dc-nonconverged`). 4 gate tests in `tests/Engine.Tests/Linear/NonlinearSParamTests.cs` (T1: linear regression guard; T2: resistive SDD at 0 V matches linear R; T3: bias-dependent G(V₀); T4: DC non-convergence fallback). Build 0W/0E; 1891 total tests pass.

Standing instructions for `src/Engine` (the numeric layer: MNA assembly, linear analyses, and the
HB sub-engine in `HarmonicBalance/`). Read with the root `CLAUDE.md`. Design notes:
`docs/design/linear-engine.md` and `docs/design/harmonic-balance.md`.

## Node-picker filter fix — StackSweepAxis passes `__`-prefixed metadata cubes unstacked (brief-node-picker-filter-fix, 2026-06-16)

`DataSet.StackSweepAxis` (src/RfCore/Data/DataSet.cs) now passes `__`-prefixed cubes through from the first dataset verbatim instead of calling `DataCube.PrependAxis` on them. Before the fix, stacking prepended the sweep axis onto `__LabeledNodes`, making it rank-2 (`[sweep, label]` instead of `[label]`); `RebuildAxisRolesCore` read `Axes[0].Labels` which was then the numeric sweep axis (Labels == null) → empty labeled set → filter broke for swept runs. The fix also updates `TraceRowViewModel.RebuildAxisRolesCore` to find the label axis by `Name == "label"` instead of by position, so it is resilient to any future shape change. Gate: `Stack_PreservesLabeledNodesShape`, `Stack_MetaCubeNotSwept` (Engine.Tests); `Picker_FiltersAfterSweep` (Ui.Tests); `Table_TraceHeader_HitTest_ReturnsTraceHeaderKind` (Ui.Tests). 4 new tests + 2 existing fixes; 1483 total tests pass.

## CNL provenance round-trip — `labelednets` directive (brief-cnl-labelednets-provenance, 2026-06-16)

**Root cause fixed:** `CnlWriter` never emitted `TestBench.LabeledNets`, so after the GUI wrote a `.cnl` file and `CnlReader` read it back, `tb.LabeledNets` was always empty → `HbEngine` skipped `__LabeledNodes` → picker showed all nodes.

**Fix:** `CnlWriter.Write` appends `labelednets n1 n2 …` (sorted, top-level) when `tb.LabeledNets.Count > 0`. `CnlReader.TryParseLine` parses it back into `tb.LabeledNets`. The directive is only valid at top level (inside `define … end` throws).

**T7 test** (`HbLabeledNodesCubeTests.T7_EndToEnd_SchematicCnl_EmitsLabeledNodesCube`): populates LabeledNets in-memory → `CnlWriter.Write` → `CnlReader.Read` → `Elaborator` → `HbEngine` → asserts `__LabeledNodes` present with correct labels. This is the regression guard for the full GUI run path (T4/T6 only covered the in-memory injection path).

## Node-picker labeled filter — `__LabeledNodes` side cube (brief-node-picker-labeled-filter, 2026-06-16)

`HbEngine.BuildSingleToneDataSet` (and `BuildTwoToneDataSet`) emit a `__LabeledNodes` metadata cube when `_netlist.Nodes.LabeledNames` is non-empty. The cube has one axis `label` with `Labels` = the labeled node names that actually appear in the `node` axis; values are all-zeros (unused). The `__` prefix marks it as metadata: the signal list and signal picker skip all `__`-prefixed cubes. Round-trips automatically via the generic DataSet `.npy` exporter.

- Absent `__LabeledNodes` (hand-written CNL, no schematic labels) → picker UI defaults to show-all.
- Present-but-empty (schematic ran, user tagged nothing) → picker shows nothing (filter ON, empty set).
- Present-and-non-empty → picker shows only the labeled nodes by default (`ShowAllNodes=false`).

Provenance thread: `NetExtractor.AssignNetNames` → `TestBench.LabeledNets` → `Elaborator` → `NodeMap.LabeledNames` → `HbEngine` → `__LabeledNodes` cube. Gate tests: `HbLabeledNodesCubeTests.cs` (T4, T6).

## Z_Port per-port references — 2N nets, ± pairs (brief-zport-per-port-refs, 2026-06-16)

**Z_Port now uses 2N nets as differential ± pairs with per-port references** (`V_p = V(net[2p]) − V(net[2p+1])`),
parallel to the SDD — **NOT** the N-or-(N+1) shared-reference convention. That single-shared-reference
convention still applies to **SnP/TLIN/user freq-models** (unchanged).

- `ZPortModel` no longer reads `ElaboratedComponent.ReferenceNode` (stays at default 0; stamp ignores it).
- Arity validated in `Elaborator.ResolveZPortParameters`: odd net count → error; netCount ≠ 2·portCount → error.
- Schematic: ZPort reuses the SDD 2N-pin ± port generator (`GenerateSddPorts` / `GenerateSddVariadicPorts`).
  `PortCount` = N (signal ports); pin count = 2N; `FromRenderModel` derives ZPort N = pins/2.
- `linear-engine.md` §4.1/§4.4 note: Z_Port is the exception to the N-or-(N+1) shared-reference rule.
- CNL format: `Z_Port:Name  n1+ n1−  n2+ n2−  …  Z[i,j]=expr` — 2N nets, no trailing refnet.
- 9 gate tests: `ZPortArityTests` (Core.Tests), `ZPortPerPortRefTests` (Engine.Tests),
  `ZPortSymbol_2Port_Has4Pins` + `ZPort_NetExtraction_4Nets` (Ui.Tests).

## HB V cube — full user-node axis (brief hb-linear-nodes-in-cube, 2026-06-16)

The HB `V` cube's `node` axis now includes **all non-ground user-facing nodes** (interface + linear-only), not only the nonlinear-device interface nodes.

- **Interface nodes** (nonlinear-device port nodes): use the converged Newton solution directly.
- **Linear-only nodes** (connected only to R/L/C/sources, no nonlinear port): recovered via `HbLinearBackSolver.GetNodeVoltage(c, k, 0)`.
- **`INl`** at linear-only nodes is 0 at all harmonics (no nonlinear device current there). The `V` and `INl` cubes keep the same `node` axis.
- **`__`-prefixed internal mint nodes** (e.g. `__p1tone_*_drv`, `__tuner_*_block/bias`) are **excluded** to reduce clutter; only user-named nets appear.
- **Stable order**: nodes emitted in ascending circuit-node-index order (topology-invariant across sweep points — required for `ParametricSweepEngine` axis stacking).
- **Two-tone** linear-node + IProbe recovery is **DONE** (2026-06-25): `RunTwoTone` back-solves the linear network
  per mixing product (`SolveMixFull`; negative-ω reps solved at `|ω|` with conjugated excitation + result, reusing
  `ExtractMix`'s cached LU), expands the V cube to all user nodes, and fills IProbe currents. See
  [[twotone-result-completeness-and-spectrum]].
- **`ParametricSweepEngine`** is unaffected: each per-point DataSet already carries the full node axis; stacking works unchanged.
- 5 gate tests: `HbLinearNodeTests` T1–T5 (`tests/Engine.Tests/HarmonicBalance/HbLinearNodeTests.cs`).
- `Hero2Tests.ExtractVMatrix` updated to filter to interface nodes (using `HbLinearExtractor`) for `RunJacobianDiagnostic` (which needs Newton unknowns only).

## P1ToneModel — single-tone RF power source (brief-sweep-5, 2026-06-16)

`P1ToneModel` (`src/Core/Devices/P1ToneModel.cs`) is the power-domain RF source: available power
`Pavl` (dBm) behind internal impedance `Z` (Ω, default 50), with optional per-harmonic-band
terminations `Z[k]`/`G[k]` (same as Tuner).

**Key design points:**
- Node layout: `[0]` = DUT-facing, `[1]` = reference (ground), `[2]` = minted `__p1tone_<inst>_drv`.
- Band-assignment rule: `n = roundHalfUp(|f|/f_c)` = `(int)Math.Floor(|f|/f_c + 0.5)`.
- `f_c` (band-center) set by `SetToneContext(fc, driveFreqHz)` — called by `HbEngine.Run()` /
  `RunTwoTone()` before extraction. Single-tone: `fc=f0`; two-tone: `fc=(f1+f2)/2`.
- `|Vs| = sqrt(8·Re(Z_at_fundamental)·Pavl_W)` (matched-load; recomputed in `SetToneContext`).
- S-param mode (`_fc≤0`): stamps `Z_Port(nExt, nRef, Z[1])` only — no drive branch.
- HB mode: drive branch `V=Vs@driveFreqHz` at `nDrv→nRef`; `Z_Port(nExt, nDrv, GetZ(ω))`.
- `HbEngine.CheckCommensurability` and `CheckCommensurabilityMultiTone` check `P1ToneModel.FreqHz`.
- Factory: `"P1Tone"` added to `_parameterizedTypes`; `CreateP1ToneModel` uses same `RxTunerZ`/`RxTunerG`
  regex + Γ→Z conversion. `Z` serves as both `Zdefault` and `Z0` for conversion.
- Elaborator: mints `__p1tone_{childPath}_drv`; dispatches `ResolveP1ToneParameters`.
- 7 gate tests in `tests/Engine.Tests/HarmonicBalance/P1ToneTests.cs`.

## HB sweep architecture (Sweep-3 migration, 2026-06-16)

All swept HB results — single-tone and two-tone — come from `ParametricSweepEngine`. The swept
axis is **prepended** (first) and named after the sweep variable (e.g. `Pavl_dbm`, `Pin`, `Vgg`).
HB-internal sweeping is fully retired: `HbEngine.Run` is always single-point, producing
`V[node, harmonic]` or `V[node, mixIndex]` plus scalar `Converged`/`Residual`.

- **Axis layout after ParametricSweepEngine:** `V[sweep…, node, harmonic]` (single-tone),
  `V[sweep…, node, mixIndex]` (two-tone); branch `I:*[sweep…, harmonic/mixIndex]`.
- **Tests and golden generators** were migrated to the parametric path; golden CSV numbers
  are unchanged.
- **Exported linear-network payload / back-solver** (`LinearPayload`, `ILinearBackSolver`) is
  single-point per HB run; a sweep-aware exported payload is a known follow-up.
- `TwoToneMeasurements` now finds node/mixIndex axes by **name** (not positional), so it works
  regardless of how many sweep axes are prepended.
- The `MeasurementEvaluator` V/INl accessor likewise finds the node axis by `Name=="node"`;
  the I branch accessor treats the last axis as harmonic/mixIndex with sweep axes prepended.

## ParametricSweepEngine inner-analysis dispatch (Sweep Fix 2, 2026-06-15)

`ParametricSweepEngine.RunInner` now dispatches `SParameterAnalysis` and `DcAnalysis` in addition
to the original `HarmonicBalanceAnalysis` and `ParametricSweepAnalysis`, so any of these can be
wrapped in nested parametric sweeps (e.g. S-params vs a bias variable, a DC curve-tracer Vds×Vgs).

- **`SParameterAnalysis`:** calls `spa.Expand(netlist.ResolvedGlobals)` to get the flat frequency
  array, then delegates to `SParameterEngine.Run(netlist, freqs, settings)` and returns its DataSet.
  The S/Z0 cubes stack cleanly under a prepended sweep axis via `DataSet.StackSweepAxis`.
- **`DcAnalysis`:** calls `NonlinearDcEngine.Run(netlist, settings)` → `DcResult`; delegates all
  packing to the shared **`DcResultPacker.Pack(result, netlist)`** (same packer used by the standalone
  `SchematicRunService` path). The packer emits: `V[node]` cube (node-name labels), scalar
  `Converged`/`Residual` cubes, scalar **`I:<probe>`** cubes for each `IProbeModel` instance
  (sign: np→nm, matching `AddBranchCurrent`), and `__LabeledNodes` metadata when present.
  `IProbe` branch currents live in `DcResult.ProbeCurrents` (keyed by instance path, set by
  `ExtractProbeCurrents` from `x[probe.LastBranchIndex]` after each Newton solve).
  A FET I–V family-of-curves = two nested parametric sweeps (Vgs outer, Vds inner) wrapping DC +
  IProbe in the drain — `I:IPd` scalar cubes stack into a `[Vgs, Vds]` cube after `StackSweepAxis`.
  Gate tests: `tests/Engine.Tests/Parametric/ParametricSweepDcSParamTests.cs` (5 tests) +
  `tests/Engine.Tests/Nonlinear/IProbeCurrentTests.cs` (3 tests).
- **Loadpull and other engine-owning analyses** remain unsupported in the generic sweep;
  `default:` still throws `NotSupportedException` with a diagnostic message.

## HB swept-axis naming (Sweep Fix 1, 2026-06-15)

The HB result's swept axis is named after `HbAnalysisParams.SweepVarName` with **unit = ""** (empty
string). The legacy hardcoded `"Pin"/"dBm"` sentinel has been removed from both `BuildSingleToneDataSet`
and `BuildTwoToneDataSet`. If `SweepVarName` is null (no-sweep path, which never creates a sweep
axis anyway), the fallback name `"sweep"` is unreachable in practice.

HB-internal sweep-axis ownership is slated for removal in the parametric-sweep consolidation
(Briefs 3–4); this fix is the interim de-sentinel so existing HB-internal sweeps stop lying about
their axis name.

## S-parameter port formulation — wave path (2026-06-15)

`SParameterEngine` uses a **Z0-terminated power-wave (Norton / Kurokawa) formulation** when all
port Z0 references have `Re(Z0) > 1e-12` (the common case).

- **Per port:** stamp conductance `1/Z0` between its nodes via `AddAdmittance` (no branch unknown).
- **Excitation:** for driven port `j` with unit incident wave `a_j = 1`, inject Norton current
  `I_j = 2·√(Re Z0_j) / Z0_j` at the port's nodes.
- **S extraction (Kurokawa):** after solving for port voltages `V_k`:
  `I_k = (k==j ? I_j : 0) − V_k / Z0_k`, then `b_k = (V_k − conj(Z0_k)·I_k) / (2·√(Re Z0_k))`,
  `S[k, j] = b_k`. No Y→S inversion step.
- **Singularity class eliminated:** parallel ports / port-across-short topologies are non-singular
  by construction — each port contributes a real positive conductance to its node, so the matrix is
  well-conditioned even when ports share the same node pair.
- **Regularization** is now a genuine last resort (floating internal nodes, exact admittance
  cancellation). The `sparam-regularization` warning no longer fires for trivial circuits.
- **Legacy path** (any port has `Re(Z0) ≤ 0`, e.g. reactive reference impedance): unchanged
  ideal-0 V-source branch stamping + unit-voltage solve + `RFNetwork.YToS`. HB/DC are unaffected
  (they already treat Port/Term as inert). `PortEntry` carries both `BranchIndex` (legacy) and
  `Node0/Node1` (wave).

`MnaSystem.Factorize` wraps `AMD.Generate` to convert `ArgumentNullException` (empty matrix from
exact conductance cancellation) to `SingularMatrixException`, so the IfNecessary retry path fires.

## What lives here
- The **`MnaSystem`** and the stamping API (`AddAdmittance`, `AddBlockAdmittance`, `AddBranch`,
  `AddBranchCurrent`, `AddConstraint`, `AddBranchConstraint`, `AddCurrentInjection`,
  `AddSourceValue`).
- The **linear engine**: DC analysis, S-parameter analysis, and the linear characterization the
  HB engine consumes.
- The **harmonic-balance engine** (`HarmonicBalance/`, see its own CLAUDE.md).
- The sparse solve (CSparse.NET) and the AMD fill-reducing ordering.

The engine sees only the **elaborated netlist** (fully-resolved kinded values, numbered nodes) and
returns a **`DataSet`**. No design-layer types, no UI, no expression strings reach here.

## Fixed conventions — record once, never silently change
A sign or direction flip here is the most expensive class of bug because results still look
plausible. Fix these in code as named constants/comments and do not change them without a
documented reason:
- **Ground is node 0.**
- **Branch-current direction:** a branch current flows from the element's **first** node to its
  **second**.
- **Current-source direction:** a current source `J` **injects into its first node** (and out of
  its second).
- **Time↔frequency sign convention and harmonic ordering** (DC, +k, −k): chosen and documented in
  `HarmonicBalance/CLAUDE.md`; every FFT round-trip uses the same one.

## Engine owns the matrix; models contribute stamps
The engine owns `MnaSystem` and orchestrates assembly and the sweep. A `ComponentModel` never sees
the raw matrix or global indices — it is handed resolved node indices and accumulates contributions
through the stamping API. This is what makes adding a component type local (root `CLAUDE.md` → "How
to add a component type"). Do not let a model reach around the API.

## One MNA assembly, three uses — keep them distinct
The same assembly/stamping serves three callers that differ in **frequency set, excitation, and
output** (`linear-engine.md` §2.1). Do not conflate them:
- **DC analysis** — single ω = 0; independent sources **on**; no ports; output = one operating
  point (node V + branch I).
- **S-parameter analysis** — swept frequency grid; independent sources **off** (zeroed: V-source →
  short, I-source → open); ports = user `Port`/`Term`; output = `S` cube.
- **HB linear partition** — per harmonic; linear partition **only** (nonlinear devices removed);
  sources **on**; "ports" = the **nonlinear-facing nodes**; output = the interface N-port **and**
  the source-excitation vector at that interface.

The DC (k = 0) member of the HB harmonic set uses the **same DC formulation** as the standalone DC
analysis — there is one DC formulation, not two.

## Element grouping (MNA)
- **Group 1 (admittance):** resistor, capacitor, current source, and any frequency-domain N-port
  **natively given as a finite `Y(ω)`**.
- **Group 2 (branch-current unknown):** inductor, voltage sources, current probe, mutual coupling,
  and **frequency-domain N-ports stamped as `Z(ω)`** (the default for Touchstone/SNP, impedance
  block, TLIN). `Z`-expansion is the robust default (every passive net has a finite `Z`); the
  native-`Y` admittance stamp is the lighter opportunistic path.

## DC correctness — no value fudges
DC is the **exact ω → 0** case: inductor → short (Group-2 constraint `Va = Vb`), capacitor → open
(admittance `jωC = 0`), floating nodes handled by a single documented **`gmin`** to ground. Never
reintroduce the prototype's large/small element-value clamps.

## Performance structure
- Sparse throughout (CSparse.NET); never a dense `n×n` solve for the full netlist.
- **Symbolic-once / numeric-per-frequency:** the nonzero pattern is fixed by topology — compute the
  AMD ordering + symbolic factorization once per topology, refactor numerically per frequency.
- **Factor-once / multi-RHS** for port extraction (one factorization, back-substitute per port).
- Native KLU/SuiteSparse stays a profiled, optional future optimization — never a v1 dependency.

## Output
Every analysis returns a **`DataSet`** of named single-kind `DataCube`s (→ `src/Core/Data/CLAUDE.md`).
S-parameter → `S {freq, i, j}` (Complex). DC → node V + branch I at ω = 0. HB → `V`, `I`
spectra (see `HarmonicBalance/CLAUDE.md`). Measurements are added to the DataSet as named cubes;
the engine does not invent its own result type.

## Engine diagnostics channel — firewall-safe, once per run
Engines surface run-time warnings (S-param regularization, HB non-convergence) via
`ElaboratedNetlist.AddWarning(message)` and `AddWarningOnce(key, message)` (Core-level;
`AddWarningOnce` deduplicates by key using a `HashSet`). **The engine never touches
`IMessageSink` directly** — that is a UI concept, and the UI firewall forbids any UI reference
in `src/Engine`.

- **`SParameterEngine`** calls `netlist.AddWarningOnce("sparam-regularization", ...)` once per
  run when the IfNecessary path fires (singular matrix retry). The message includes the
  `SingularMatrixException` detail, which names the floating node(s).
- **`HbEngine.Run`** (and `RunTwoTone`) accumulate `ncCount` / `worstRes` / `totalPoints`
  across ALL sweep points (no-sweep runs use `Enumerable.Repeat(0.0, 1)` so `totalPoints=1`),
  then emit **one** summary via `AddWarning(...)` after the loop if `ncCount > 0`.

`SchematicRunService` drains `nl.Warnings` after the run (even on `EngineError`) into
`RunResult.Warnings`; `WorkspaceViewModel.RunAnalysis` posts them to the Messages pane at
Warning level. Gated by `EngineDiagnosticsChannelTests` (T1: floating node; T2: HB MaxIter=1)
and by `SchematicRunServiceTests` (L1e/L1f: warnings non-empty / empty).

## Phase 2 Step 1 deliverable — COMPLETE (2026-05-31)

### `MnaSystem` — v1 backing store
`MnaSystem` (in `src/Engine/MnaSystem.cs`) implements `IMnaContext` (defined in `src/Core/`).
Backing store is `Dictionary<(int Row, int Col), Complex>` — simple for Step 1 stamp inspection.
**Step 2 replaces this with CSparse.NET triplets** and adds the LU solve, AMD ordering, and the
symbolic-once/numeric-per-frequency pattern.

### Matrix index convention
- Node k (k ≥ 1) → internal index k − 1 (method `Col(node) = node - 1`).
- Ground (node 0) → index −1, all entries silently dropped.
- Branch b (from `AddBranch()`) → internal index returned directly (= `_nodeCount + sequential counter`).
- Matrix row/col layout: `[0 .. nodeCount−1]` = voltage unknowns; `[nodeCount ..]` = branch unknowns.

## Phase 2 Step 2 deliverable — GATE PASSED (2026-05-31)

Hero 1: 4-port RLC + embedded 2-port SnP. max|S_sim − S_ref| < 1e-6 across all 16 S-params,
1–3 GHz, from the CLI. 117/117 tests pass.

### Implementation notes (reality vs. design)
- **Sign in Y-matrix extraction:** branch current flows FROM signal TO ref (AddBranchCurrent
  convention), so the port current (INTO the + terminal) = **−branch_current**. Y_kj = -x[br_k].
- **Fixture bug found and fixed:** hero1.cnl had `C3 = 0.5 pF`; the external reference used 1.5 pF.
  Also changed `InterpMode` to `"linear"` to match the external reference generation.
- **AMD perm caching:** computed on first `Factorize()` call (first frequency), reused for all
  subsequent frequencies. Both the Dictionary clearing and branch-count reset in `Reset()` are
  required to make the symbolic-once / numeric-per-frequency pattern work.
- **Gmin loop:** `for (int n = 1; n <= nonGroundNodes; n++) AddAdmittance(n, 0, gmin)` —
  uses the circuit node indices (1-based), NOT the internal 0-based matrix indices.
- **Port collection:** a preliminary stamp pass (omega=1.0) captures `PortModel.LastBranchIndex`
  before the analysis loop. Indices are deterministic (same component order each pass), so the
  captured values remain valid throughout the sweep.
- **S-matrix Z0 metadata:** the SNP returned by `SParameterEngine.Run` stores `refZ0 = z0PerPort[0]`
  as the SNP's Z0 field (for Touchstone write metadata). The actual per-port renormalization was
  already applied via `YToS(yMat, z0PerPort)`.

## Phase 2 Step 3 — Hero 1B Singular-Matrix Diagnosis (2026-06-01)

### Diagnostics added (permanent product features)
- **`MnaSystem.FindZeroRows(nodeNamer, branchNamer)`** — pre-solve structural check: finds all-zero
  rows in the assembled MNA; names voltage nodes (with touching component list) and branch rows.
- **`MnaSystem.FindZeroCols(nodeNamer, branchNamer)`** — finds all-zero columns (a degree-of-freedom
  singularity dual to zero rows).
- **`MnaSystem.Factorize(tol, nodeNamer, branchNamer)`** — runs the structural check before
  factorization; on failure (zero row/col or CSparse "no pivot") throws `SingularMatrixException`
  with a diagnostic message naming the problematic row/branch and its touching components.
- **`SingularMatrixException`** — new exception type in `src/Engine/`.
- **`SParameterEngine`** — builds node/branch namers from the elaborated netlist (node names +
  touching-component list; branch→component map from the preliminary stamp pass); passes them to
  `Factorize()`. Preliminary pass updated to two-phase ordering (non-mutual first, then mutual).
- **`MutualInductanceModel.Stamp`** — over-coupling check: rejects k ≥ 1 (M² ≥ L1·L2) with a
  clear error naming the Mutual instance and its computed k. `_l1`/`_l2` stored in `Resolve()`.

### Step 3 audit results
- **gmin in AC path**: confirmed present in S-parameter path (not DC-only). Not a bug.
- **Short stamp**: audited and correct. Unit test `Short_AsInternalWire_SameAsDirectConnection`
  confirms identical S-params with and without an internal Short wire.
- **Mutual stamp**: stamp sign convention correct per linear-engine §7. Pairwise k-check added.
  Unit tests: `Mutual_ValidCoupling_SolvesAndIsReciprocal` and `Mutual_OverCoupling_ThrowsWithDiagnosticMessage`.

### Hero 1B diagnosis (Step 5) — root cause identified
The singularity was frequency-dependent (solved at 1 Hz, failed at 1 GHz), pointing at the jωM
terms. Root cause: `InductorModel.Stamp` silently dropped the `R=` parameter on `L:` lines — the
first circuit to have lossy inductors. With R = 0.0026 Ω per inductor omitted, the coupled
inductance block had a zero eigenvalue at AC.

## Phase 2 Step 3 — Hero 1B Gate: PASSED (2026-06-01)

### Fix 1: Inductor series-R stamping (correctness bug)
**`InductorModel.Stamp`** now reads the optional `R=` parameter (default 0) and stamps it together
with jωL on the same branch diagonal: constraint becomes `Va − Vb − (R + jωL)·i = 0`.
- At DC with R=0: exact short (unchanged behaviour).
- At DC with R>0: `Va − Vb − R·i = 0` — acts as resistor, not a short.
- The R term is independent of ω, so it is always added if non-zero.
- Unit test: `InductorWithSeriesR_ImpedanceMatchesAnalytic` verifies Z(ω) = R + jωL end-to-end.

### Fix 2: Mixed-sign mutual inductance support (already correct, verified)
Negative M values are physically valid (anti-phase coupling — geometry dependent) and must NOT be
rejected, warned on, or negated. The stamp correctly applies M with its sign intact (−jωM term).
- Over-coupling check (`k ≥ 1`) uses `m*m >= _l1*_l2` — tests the magnitude, not the sign.
- Unit test: `ThreeInductors_MixedSignMutual_SolvesCorrectly` confirms a physically-realizable
  mixed-sign inductance matrix solves, is reciprocal, and is passive.

### Feature: Two tri-state regularization settings (`AnalysisSettings`)
**`AnalysisSettings`** (new, `src/Engine/AnalysisSettings.cs`) exposes two independent `RegularizationMode` settings:
- **`ConductanceRegularization`**: controls gmin (1e-12 S, node→ground). Default: `IfNecessary`.
- **`InductanceRegularization`**: controls a small series-R (1 nΩ) added to each inductor branch
  diagonal. Cures a rank-deficient coupled-inductance block. Default: `IfNecessary`.

`RegularizationMode` tri-state:
- **`IfNecessary`**: first attempt without regularization; if `SingularMatrixException`, retry with
  all non-`Never` regs applied (both, for simplicity) and warn on stderr. Clean circuits pay zero.
- **`Always`**: apply before the first factorization (skip speculative failed solve).
- **`Never`**: no regularization; `SingularMatrixException` propagates immediately (debug mode).

`SParameterEngine.Run` signature changed: `gmin = DefaultGmin` parameter replaced by
`AnalysisSettings? settings = null`; uses `AnalysisSettings.Default` when null.

Hero 1 (lossless inductors, no R=): first solve succeeds (no regularization needed) — result is
identical to before (1e-6 gate still passes).

Hero 1B (lossy inductors, R=0.0026 Ω): first solve succeeds after the inductor-R fix — the series
resistance regularises the inductance block naturally. Regularization retry never fires.

Tests added: `InductorWithSeriesR_ImpedanceMatchesAnalytic`, `ThreeInductors_MixedSignMutual_SolvesCorrectly`,
`AnalysisSettings_IfNecessary_RescuesSingularOnRetry`, and `SParameterEngine_IsolatedShort_BothNodesGround_ThrowsSingular`
(updated to use `RegularizationMode.Never` so the diagnostic propagates as designed).

**Total tests: 130 pass, 0 fail.**

## Phase 2 Component Robustness (2026-06-01)

### Design philosophy: warn-and-continue
circuitRF is a research tool. Non-physical-but-mathematically-handleable inputs emit a warning to
`Console.Error` and proceed; they do NOT hard-error. Warnings fire once per component instance
(not once per frequency point) using an instance-level `_warned` flag. Hard errors are reserved
for genuinely unresolvable conditions (missing required parameters, elaboration failures).

### Change 1: ResistorModel — negative R and R=0
- **R < 0**: stamps `G = 1/R` with its sign (negative conductance — models active/negative-resistance
  elements). Emits one warning per instance: `"R:{path}: R={r} Ω < 0 — non-physical/active"`.
- **R = 0**: stamps `Gmax = 1e12 S` (near-short). Emits one warning per instance naming Gmax.
  `Gmax` is a `const double DefaultGmax` on `ResistorModel` matching `AnalysisSettings.Default.Gmax`.
- `AnalysisSettings.Gmax` (default 1e12 S) exposes the conductance ceiling. Currently used by
  `ResistorModel.DefaultGmax`; future: wire through `IMnaContext` for per-run customization.

### Change 2: InductorModel — optional series R and C (series-RLC branch)
An `L:` line may carry `R=` (series resistance) and/or `C=` (series capacitance), both optional.
The inductor's single Group-2 branch is a series-RLC element:
- Constraint: `Va − Vb − (R + jωL + 1/(jωC))·i = 0`
- `R=` absent → no resistance term (lossless). `C=` absent → no capacitive term.
- **DC with C present**: series capacitor is an open at DC (1/(jωC) → ∞ as ω→0). Stamped as
  force-i=0: constraint row has only `−i = 0` (diagonal = -1, no voltage coefficients). KCL column
  still stamped so the branch column is non-zero. Equivalent to the standalone capacitor's DC-open.
- **DC without C**: `diag = −R` (resistor if R>0, exact short if R=0 — unchanged from prior).
- Tests: `InductorWithSeriesR_ImpedanceMatchesAnalytic` (RL), `InductorRLC_AcImpedanceMatchesAnalytic`
  (RLC AC), `InductorWithC_DcOpen_BranchCurrentIsZero` (DC-open via MnaSystem inspection).

### Change 3: MutualInductanceModel — k≥1 downgraded from error to warning
- `k ≥ 1` (M² ≥ L1·L2) is non-physical but allowed at the user's peril. Warning: once per instance
  via `_warnedOverCoupling` flag. Stamping proceeds; if the inductance matrix becomes singular,
  `InductanceRegularization` (IfNecessary default) rescues the solve.
- **Negative M (mixed-sign couplings) is fully physical — no warning, no special handling.**
- Test: `Mutual_OverCoupling_WarnsAndProducesResult` verifies warning fires + result returned.

**Total tests: 134 pass, 0 fail.**

## Phase 3 deliverable — COMPLETE (2026-06-01)
Nonlinear-DC Newton solver, validated by the hero GaN HEMT operating point.

### NonlinearDcEngine (`src/Engine/NonlinearDcEngine.cs`)
Unified real sparse Newton solver (nonlinear-dc §4):

**State vector** x = [V₁…Vₙ | I_branches]: voltage unknowns + all MNA branch-current unknowns
(voltage sources, inductors — from MnaSystem at ω=0). The full augmented system is built once from
`MnaSystem` at ω=0, extracting the real parts of all entries and the source RHS.

**Residual** F(x) = G_aug·x + I_nl(x) − b_source·sourceFrac  
**Jacobian** J = G_aug + dg(x): linear matrix (constant per source-stepping fraction) + dg from
`Evaluate`, stamped at each nonlinear device's port nodes using the 4-way port-pair formula.

**Port voltage convention** (from elaborated node layout):  
SDD nodes are in 2N pairs: `[n1+, n1−, n2+, n2−, …]`. Port voltage p = V(nodes[2p]) − V(nodes[2p+1]).
dg[p,q] stamps into the (np, nq) block with ±dgPQ signs from the 4 node-pair combinations.

**gmin continuity**: shunt DefaultGmin (1e-12 S) added to every voltage row diagonal (nodes only, not
branch rows). Controlled by `AnalysisSettings.ConductanceRegularization`.

**Source-stepping** (§4.3): sources walked from 0 to 1 in DefaultMaxSteps (20) equal steps; step-halving
backoff on Newton max-iter failure (up to 10 halvings). AMD permutation cached after first iteration.

**Convergence**: ‖F‖₂ < 1e-6 (DefaultAbsTol) or ‖Δx‖₂ < 1e-9 (DefaultVTol).

### Hero gate (2026-06-01)
`tests/Engine.Tests/Nonlinear/NonlinearDcTests.cs`: Hero GaN HEMT + 20 Ω series Rd, gate −3.05 V, drain 48 V.
Converges in 68 iterations to **vds = 47.0176 V, i2 = 49.122 mA** (golden: 47.018 V, 49.12 mA).
Residual = 6.2e-11 (well below 1e-6 tolerance). All Phase 1–2 tests still pass.

## Phase 3 Follow-up — DcBiasStepping, SDD whitespace, convergence settings (2026-06-02)

### DcBiasStepping tri-state (`AnalysisSettings.DcBiasStepping`)
New `DcBiasSteppingMode` enum (same tri-state pattern as `RegularizationMode`):

- **`IfNecessary`** (default): direct cold-start Newton at frac=1.0; fall back to ramp only if it
  fails. Hero converges in **4 iterations, 1 step** — no ramp needed.
- **`Always`**: always ramp DC supplies 0→1 in `DcBiasRampSteps` (default 20) equal steps.
  Reproduces the Phase-3 behavior (68 iters across 20 steps).
- **`Never`**: direct solve only; throws `NonlinearDcNotConvergedException` on failure. For
  validation/debugging.

`DcBiasStepping` ramps DC *bias supplies* — distinct from Phase-4's reserved `DriveStepping`
(which will ramp RF *drive power*). Do not conflate the two.

`DcBiasRampSteps` (default 20): ramp step count, only relevant when `Always` or fallback fires.

`NonlinearDcNotConvergedException` — new exception, thrown only by `Never` mode.

### Convergence trace (permanent feature)
`DcResult.Trace` holds a `ConvergenceTrace` with `StepRecord` (per continuation step: source
fraction, iteration count, convergence, per-iteration `IterationRecord`) and `DampingPolicy`.
The hero final step converges super-quadratically: ‖F‖ goes 2.4 → 9.9e-4 → 6.2e-11 in 3 iters.

### Solver architecture (post-refactor)
`NonlinearDcEngine.Solve()` dispatches to:
- `SolveDirect(throwOnFailure)` — single Newton attempt at full bias (frac=1.0)
- `SolveRamped()` — source-stepping loop (the former `Solve()` body)
- `SolveIfNecessary()` — calls `SolveDirect(false)`, then `SolveRamped()` if needed

**Total tests: 199 pass, 0 fail.**

## Phase 4b-1 deliverable — COMPLETE (2026-06-03)
Core loadpull engine and the `Tuner` component, validated on Hero 3.

### `TunerModel` (`src/Core/Devices/TunerModel.cs`)
New `ComponentModel` (Kind=Linear) wrapping Z_Port + bias-tee (L, C, V_supply) + optional
V_1Tone drive (SourceTuner role). Four-node layout: two declared nets + two internal nodes
`__tuner_<inst>_block` / `__tuner_<inst>_bias` minted by the Elaborator at elaboration time.
- Role (Load / Source) assigned by `LoadpullEngine` before HB runs.
- `ChokeBranchIndex` and `BiasSupplyBranchIndex` set each Stamp() pass.
- `SetHarmonicOverride(k, Z)` overrides one harmonic for the loadpull grid sweep.
- `SetSourceDrive(f0, Pavl)` updates the SourceTuner's V_1Tone amplitude each Pin step.
- In S-param mode (no tone set), presents Z[1] flat over all frequencies.

### `HbEngine.RunSinglePoint`
New method on `HbEngine` that runs the Newton solve at a single operating point (no sweep
loop). Accepts an optional warm-start `Complex[,]` seed. Used by `LoadpullEngine` for each
grid×Pin step. Settings override (InductanceRegularization=Always) passed per call.

### `HbLinearExtractor` changes
- `IsVoltageOrToneSource` now includes `TunerModel` — the TunerModel is stamped via
  `ZeroDriveMna` in the zeroDrive=true (Y_NN extraction) path, zeroing its V_1Tone and
  bias supply values while keeping the impedance topology active.
- `ApplyInductanceReg` now also regularizes `TunerModel.ChokeBranchIndex` (the internal
  choke, not an InductorModel, needs explicit regularization when mode=Always).

### `GamReader` (`src/Engine/Loadpull/GamReader.cs`)
Parses `.gam` grid files: mag_ang / re_im / re+j*imag formats; gamma or impedance form;
optional header; comment/blank skipping; Γ↔Z roundtrip via analytic formula.

### `LoadpullEngine` (`src/Engine/Loadpull/LoadpullEngine.cs`)
2-D sweep: outer Γ/Z grid × inner adaptive Pin drive-up. InductanceRegularization=Always
forced for all inner HB solves. VSWR-nearest Γ-grid warm-start. Compression stop at
P-xdB + one overshoot step. Stop reasons: Compression / PinMax / NonConvergence.

### Hero 3 gate — PASSED (2026-06-03)
20-point Γ grid, all converged. Gt 4.5..16.6 dB (varies with load — correct PA behavior).
Pout 14.5..26.6 dBm. Stop = PinMax for all (FET does not reach 3 dB compression at
PinMax=10 dBm from 25Ω source, which is physically correct for this bias point). Golden
frozen in `testdata/Hero3/`. SELF-GENERATED — NOT INDEPENDENTLY VALIDATED.

### `HarmonicBalanceAnalysis` / `LoadpullAnalysis` changes
- Both now carry `MaxIterExpr` (default "100") — user-settable max Newton iterations.
- `AnalysisSettings.HbMaxIter` default changed from 50 to 100.

**Total tests: 225 pass, 0 fail.**

## Phase 4b-2 deliverable — COMPLETE (2026-06-03)
Loadpull pursuit (MXP/MXE search + auto-Zsource), validated on Hero 3B.
Details in `src/Engine/Loadpull/CLAUDE.md`.

New files: `PursuitEngine`, `LoadpullPursuitEngine`, `GamWriter` (all in `Loadpull/`).
New analysis type: `LoadpullPursuitAnalysis` (Core/Design) + CnlReader dispatch.
Modified: `LoadpullEngine` (extracted `PrepareContext`/`RunOneTermination`);
           `LoadpullResult.PinStepResult` (added `PdcW`, `De`, `Pae`).

**Total tests: 245 pass, 0 fail.**

## Phase 4b-2 enhancement — IteratedQuadratic (2026-06-05)
Second, more robust search method added to `PursuitEngine` alongside the existing `SteepestAscent`.

- **`SearchMethod` enum** (`PursuitEngine.cs`): `{ SteepestAscent, IteratedQuadratic }` — extensible.
- **`PursuitEngine.Method`** init property (default `SteepestAscent`): dispatches `Run` to either
  `RunSteepestAscent` (unchanged existing path) or `RunIteratedQuadratic` (new).
- **`SearchMethod` directive key** in `loadpull_pursuit` (default `SteepestAscent`): parsed from
  `LoadpullPursuitAnalysis.SearchMethodExpr` by `CnlReader`, threaded into `PursuitParams`.
- **`FitAxis1D`** new private static helper in `PursuitEngine`: decoupled 1-D quadratic fit per axis,
  avoiding the singular AtA matrix that arises when axis-aligned cardinals feed the full 5-parameter
  `FitQuadraticSurface` (the ΔxΔy column is identically zero → `Solve5x5` returns all-zeros).
- **Debug cleanup**: removed leftover `Console.WriteLine` calls in `ExtractCriterion`.
- **Hero 3B IQ results**: MXP=77.6 Ω (brute-force VSWR=1.031 < 1.20), query ratio IQ/SA=1.86× ≤ 2×.

**Total tests: 257 pass, 0 fail (158 Core + 99 Engine).**
## DC non-convergence must say WHERE, not only how big (2026-07-31)

`DcResult.ResidualPerUnknown` carries the residual for every unknown at the point the solve stopped,
and `SParameterEngine`'s non-convergence warning now appends the worst three by name:
`Worst-unsettled: X1.thermal = 2.28e+08 (residual 4.55); …`.

**Why.** The message could previously only say `residual 35.6` — a number with no address. It names
neither the part of the circuit that will not settle nor how far off it is, so on a real design
(hundreds of unknowns, a vendor kit inside a package subcircuit) the only way forward is to bisect
the schematic. In practice one row is enormously worse than the rest. Found while chasing a real
kit's operating point, where the offender was a thermal node at 10^8 because nothing bounded it.

- **One residual build, not two.** `BuildResidualAndJacobian` evaluates every nonlinear device, so
  asking for the norm and the vector separately doubles the cost of finishing a solve. Caught by
  `Hero1BTests`' wall-clock budget under full-suite load — it passed in isolation, which is the usual
  shape here (see the memory note on verifying timing under load).
- **Past the node count an unknown is a branch current** and has no node name to give; it is reported
  as `branch unknown #k` rather than mislabelled.
- Node names come from `netlist.Nodes.NameOf`, so a node reads as the user's own net name.

Gate: `NonlinearSParamTests.T5`.

## A floating nonlinear port was solved wrong by HB, and right by DC (2026-08-02)

`HbNewton.EvaluateNonlinear` and `HbNewton2D.EvaluateNonlinear2D` accumulated each device port's
current at the port's **+** net only. A port spans two nets; the current that enters at + leaves at
−, so injecting it at one end and never removing it at the other violates KCL at the − net. The
Jacobian had the matching gap — `dg[p,q]` stamped at `(iPlus, jPlus)` instead of the four corners
`(+,−,−,+)`.

**Why nothing caught it.** Every nonlinear circuit in the suite references its device ports to
ground (`SDD:M1 n_gate 0 n_drain 0`), so `portMinusIdx` is −1 and the two formulations coincide. It
took a diode floating across two live nets — the ring quad of a passive mixer, a bridge, any
series-connected device — to separate them. `NonlinearDcEngine.BuildResidualAndJacobian` had
**always** done both signs and the 4-way `StampDg`, so the two engines in this repo disagreed with
each other about the same circuit.

The failure mode is the expensive one: it converges cleanly to a wrong answer. On a real mixer it
gave a 4e-10 residual and 128 dB of conversion loss — no mixing at all.

- Fixed via shared `PortAdd`/`PortAdd4` helpers in both assemblers, plus `SensAddPort` for the SDD
  control-sensitivity path, which had the same one-sided accumulation.
- Gate: `tests/Engine.Tests/HarmonicBalance/FloatingPortHbTests.cs`. The oracle is the closed-form
  series solution `Vs = I·(R1+R2) + N·Vt·ln(I/Is + 1)`, **not another circuitRF path**, so it cannot
  be satisfied by two wrong implementations agreeing. Before: `V(na)=0.6039, V(nb)=0`. After and
  closed-form: `1.293148 / 0.706852`. T5 checks the analytic Jacobian against the FD oracle,
  because a current-only fix leaves the derivative wrong — visible as slow convergence, not a wrong
  answer.
- Every hero HB golden is byte-identical (all grounded-port circuits). Suite: 6,036 pass, 0 fail.
- **Practical note for diode rings:** a passive ring with `Cj0 = 0` is stiff, and Newton diverges at
  full step above moderate drive. `Lambda = 0.5` converges where `Lambda = 1` gives ‖F‖ ~1e11.
  `DriveStepping` does not help — there is no DC bias to ramp; the drive *is* the bias.
