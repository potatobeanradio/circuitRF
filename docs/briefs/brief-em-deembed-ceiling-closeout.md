# Brief — closing `brief-em-aim-ceiling`: the accelerated ceiling is unreachable through the de-embedded path

**This CLOSES `brief-em-aim-ceiling.md`.** That brief's decision — (b), a second ceiling at
`SurfaceMesher.AcceleratedUnknownCeiling = 12_000`, single-level only — is sound, measured, and
stands. Nothing here reopens it. What it left behind is a **reporting defect and one measurement**,
both of which its own §4 correctly put out of scope, and neither of which should be discovered later
by a user.

**It adds no physics.** No Green's function, no DCIM fit, no basis function, no de-embedding algebra.
D6's error-box derivation and D7's Z_c route are not being touched — only how one m×m system is
stored, factored and guarded, and what the user is told before it runs. If you are editing
`SpectralGreens.cs`, `Dcim.cs`, `SingularExtraction.cs`, `PlanarAim.cs`, or the D6/D7 algebra in
`PlanarDeembed.cs`, you are in the wrong brief.

**Its outcome may legitimately be "C3 does not move it far enough."** That is a result, not a
failure, and it is recorded as one with the measurement that decided it — same as M3's
cross-frequency parallelism and same as this brief's parent.

Read first, in this order:

- `src/Engine/Mom/HISTORY.md` **§13**, and in particular its closing subsection *"The limitation this
  surfaced"* — the finding this brief exists to close, in the words of the agent that found it.
- `src/Engine/Mom/PlanarDeembed.cs` — the **file header's D7 section** (four bullets, and the fourth
  one matters most: see §3 below), then `StaticCapacitance` (~line 213) and `CapacitancePerMetre`
  (~line 240).
- `src/Engine/Mom/PlanarFill.cs` — `BuildCores` (~line 437), the `GuardCeiling(n)` it calls
  (~line 1618), and `ScalarPotentialMatrix` (~line 619).
- `src/Engine/Mom/PlanarSolve.cs` — the `if (st.Deembed)` block (~lines 796–870), specifically the
  buried-level port refusal it already throws and the `sizes` list it already assembles.
- `src/Engine/Mom/SurfaceMesher.cs` — `GuardCeiling(int n, bool accelerated)` (~line 154) and
  `BuildRefusal` (~line 761).
- `docs/design/layout-view.md` §10.6 — *"De-embedding is mandatory, not optional"*, and why.

Nothing below is repeated from those.

---

## §0 — the defect: the panel's own first-named remedy now leads to a twenty-minute failure

§13 moved the ceiling and rewrote `BuildRefusal` so that when a mesh would fit accelerated,
**"turn on the accelerated solve" is named FIRST**, ahead of every mesh remedy. That is the right
ordering and it is exactly what §0 of the parent brief asked for.

Follow it, on the file it was written for:

1. Owner's taper, dense: refused at mesh time, in **seconds**, with remedies.
2. User turns on Accelerated solve, as the refusal instructs.
3. Mesh passes (N = 6,581, under 12,000). The DUT's own Z-matrix solves. This is real and it is what
   §13 delivered.
4. `PlanarDeembed.CapacitancePerMetre` → `StaticCapacitance` → `PlanarFill.BuildCores` →
   `GuardCeiling` throws, on the standard's own N = 6,466, **twenty real minutes in**.

**Before this brief's parent landed, that user got an instant honest refusal. Now they get a
twenty-minute wait and a throw.** No code got worse; the ceiling moved for one path and the guard
that actually binds sits on another. But R17's contract has never been "there is a ceiling" — it is
*surface the predicted N before solving, and refuse politely above it*. This run predicts, passes,
and fails late, which is the one behaviour R17 was written to prevent.

**And it is not a corner case.** D4 makes a calibration standard reproduce the DUT's own transverse
gridlines **verbatim**, so on the owner's file the standard measured **N = 6,466 against the DUT's
6,581 — 98%**. For the class of geometry that motivated the accelerator in the first place
(width-ratio-driven N growth, §0 of the parent), *any DUT past the dense ceiling has a standard past
it too*.

> **State this plainly, because it is the finding:** as shipped today, the 12,000 ceiling is
> reachable **only with de-embedding turned off**. §10.6 makes de-embedding mandatory rather than
> optional — a raw S includes the port discontinuity and reporting it as the structure's response is
> simply wrong. So M6's headroom currently exists on a path the design doc says is not the published
> answer.

That sentence is what decides how much of this brief is worth doing, and it should appear in the
completion note whatever the outcome.

---

## §1 — the question, stated precisely

**Not** "how do we accelerate `StaticCapacitance`." That is a second accelerator against a
structurally different system and it is a later brief if it is anything (see C4). The question here
is narrower and answerable now:

> **What does the user find out, and when — and is there a ceiling move available on this path that
> costs no physics at all?**

Two things are owed before the parent brief can close:

- the refusal must be honest **at setup time** about the whole run, calibration standards included;
- the cheap **representation** win must be measured, because it may move this path's own ceiling far
  enough that the owner's file simply runs, and it touches no algebra.

---

## §2 — milestones

### C1 — refuse at setup, in the place the numbers already exist. **Gating.**

`PlanarSolve.cs`'s `if (st.Deembed)` block **already assembles every standard's
`Mesh.Bases.Count` into `sizes` before any fill runs**, already sums them, and already emits a note
giving the ratio against the DUT's N. Nothing checks them against anything. The prediction R17 wants
is not missing — it is present, correct, and unenforced.

There is also already a precedent for a setup-time refusal in that same block, forty lines above: the
buried-level port refusal, whose own comment says **"Refused by name rather than reported."** Same
place, same shape, same reasoning.

- **R-dcl-1.** A de-embedded run is refused **at setup**, in the block that already computes the
  standards' sizes, before any fill. Not inside `CalibrateAt`, which is lazy and runs at the first
  frequency's calibration — that laziness is exactly what turns this into a twenty-minute failure.
- **R-dcl-2 — the guard asks the DENSE question regardless of `AcceleratedSolve`.**
  `StaticCapacitance` is not on the accelerated path and this brief does not put it there, so the
  question is `SurfaceMesher.GuardCeiling(nStandard, accelerated: false)`. **The message must say why
  the accelerator does not help here**, or the user reads the panel as contradicting itself thirty
  seconds after telling them to turn the accelerator on. Name the step: de-embedding's reference
  impedance needs a static capacitance solve on the standard, that solve is dense, and the
  accelerator covers the DUT's own frequency-domain system and not this one.
- **R-dcl-3 — the remedy must be one a user can act on.** Gate 5's rule from the parent, unchanged.
  If the honest answer is "turn de-embedding off and read the raw solve," say exactly that **and say
  what is lost** — §10.6's own sentence, that the raw S includes the port discontinuity. Do not name
  mesh remedies; §0 of the parent already measured them inert on this geometry.
- **R-dcl-4.** The standards' own sizes must be in the refusal, not just the DUT's. A user told their
  6,581-unknown mesh is fine and then refused has no way to guess a second mesh of 6,466 exists
  unless the message names it.

### C2 — the guard is measuring the wrong quantity at this call site. **Gating, cheap.**

`PlanarFill.BuildCores` guards on `mesh.Bases.Count` and its message quotes `n × n × 16` bytes. But
`StaticCapacitance` allocates over **`mesh.Cells.Count`**: `ScalarPotentialMatrix` returns an m×m
`Mat<Complex>`, `StaticCapacitance` then builds a *second* m×m `Mat<Complex>` from it, and factors
that. So the number the refusal quotes is not the cost of the thing about to be allocated, and there
are two m×m allocations live at once plus the LU's working set.

- Measure **m against n** on a real standard mesh and report the ratio. If they differ materially,
  the guard at this call site needs the right quantity and the message needs the right megabytes.
- This is §7's own *"quotes 381 MB against a real run cost of ~607 MB"* defect in a second location.
  Same class, so fix it the same way: quote what a machine will actually see, measured.
- **Do not tighten `BuildCores`'s guard for every caller.** Every other caller is a basis-count fill
  and `n` is the right question there. If this call site needs a different question, it asks a
  different one — it does not change the shared one.

### C3 — the representation, measured before any accelerator is contemplated. **Gating, and this is where the answer may simply be.**

`StaticCapacitance` solves at **ω → 0** through `PlanarKernelTerms.StaticScalar(slab)`, sums the
solution and returns `total.Real`, discarding the imaginary part outright. `ScalarPotentialMatrix`'s
own doc comment says the matrix is **"Symmetric by construction"** (computed on `a ≤ b` and
mirrored). If the static terms are genuinely real, then this is a **real symmetric system being
carried as `Mat<Complex>` and factored with a general LU.**

- **Measure it, do not assume it.** Is the imaginary part identically zero on a real standard mesh —
  bit-zero, or only zero to tolerance? Report which. If it is only near-zero, say what it is and
  where it comes from before changing anything.
- If it is real: `Mat<double>` halves the storage; a symmetric factorisation (Cholesky or LDLᵀ)
  halves it again and roughly halves the flops. **4× on memory is 2× on the linear dimension** — the
  difference between refusing a 6,466-unknown standard and running it comfortably.
- **R-dcl-5 — the answer must not move.** C_pul is what every published Z_c is referenced to, and
  L8d already measured de-embedding amplifying a raw-S error ~22× at 2 GHz. So a representation
  change is gated by a **bit-comparison** of C_pul against today's value on a mesh both
  representations can run — not a tolerance. Two orderings of the same sum agree to 1e-12 whether or
  not they are the same computation; §9's own note on that applies here verbatim.
- Already correct, do not "fix" it: `_cPerMetre` is computed **once per calibrator for the whole
  sweep** (`if (double.IsNaN(_cPerMetre))` in `CalibrateAt`). That is R-mom-11's discipline holding
  on this path. It is worth a sentence in the completion note so a later reader does not add a cache
  that is already there.

### C4 — the decision, and the number that replaces the word "wide". **Gating.**

- **"A wide-port de-embedded run can hit the old ceiling" is not actionable.** *Wide* must be a
  number. §0's own measurement gives the shape: for a single-level uniform-cross-section port the
  standard is approximately the DUT's own N (98% on the owner's file), so state it as what it is —
  **the de-embedded path's effective ceiling IS this path's ceiling**, and it applies to the DUT's N
  as drawn, not to some separate notion of port width.
- Record the outcome in `src/Engine/Mom/CLAUDE.md` **§4's table and §8**, with the measurement. If
  C3 does not move it far enough to cover the owner's file, that goes in **§6** with its numbers, in
  M3's format.
- If C3 is insufficient, **accelerating `StaticCapacitance` becomes its own brief.** Write its scope
  sentence here — one paragraph naming what it would have to be, given that it is an m×m system over
  *cells* with a static kernel rather than an N×N system over *bases* with a frequency-dependent one
  — and stop. Do not start it.

---

## §3 — what this brief must NOT do

- **Do not read C_pul or Z_c from `QuasiStaticKernel` / `RlgcExtractor`.** This is the obvious idea —
  kernel A already computes [C] per unit length from a boundary mesh of a few hundred segments,
  frequency-independent, once per sweep — and `PlanarDeembed.cs`'s own file header **already rules it
  out by name**, with the reason: it would make the phase table's *"A and B agree on a uniform line"*
  gate a tautology and would import A's discretisation error into B's answer. Kernel A is the
  **oracle** for Z_c, never an input. Recorded here so nobody re-proposes it a third time.
- **Do not put `StaticCapacitance` on AIM.** Different system, different basis, different kernel — a
  second accelerator, not a widening. C4 may recommend it; this brief does not build it.
- **Do not touch D6 or D7's algebra.** The two-standard differencing exists so both end effects
  cancel *exactly* within one discretisation. Anything that changes what is differenced changes the
  published answer.
- **Do not coarsen the calibration standards' meshes to make them fit.** D4's verbatim transverse
  gridline match is precisely what makes the error boxes cancel; a standard meshed differently from
  the DUT would produce a smooth, plausible, wrong s-parameter — the failure mode this directory
  keeps finding.
- **Do not raise the dense ceiling**, and **do not make the accelerator the default.** Both carried
  forward unchanged from the parent's §4.
- **Do not lift AIM's multi-level/via refusal.**

---

## §4 — gates

1. **The owner's `NZ_20260814` taper, de-embedding ON, accelerated ON.** Either it **refuses at
   setup, in seconds, naming the standards' N and a remedy a user can act on**, or it runs. Either is
   a pass. A twenty-minute throw is the fail, and it is the only thing this brief exists to remove.
2. **C2's m-vs-n measurement** on a real standard mesh, with the megabytes the refusal quotes matched
   against a measured peak working set.
3. **C3's bit-comparison** of C_pul before and after, on a mesh both representations can run, plus
   memory and wall clock both ways.
4. **`EmCeilingRefusalTests` updated rather than loosened.** In particular
   `…TheDutsOwnZMatrix_ACTUALLYSOLVES_WithTheAcceleratedSolveOn` gates the DUT's **raw** solve; it
   stays, and it gains a stated note saying so and saying why raw is not the user's success case
   (§10.6). A gate whose name reads like the feature works, on a path the design doc calls
   incomplete, is the thing a future reader will trust wrongly.
5. **The limitation text in CLAUDE.md §4 carries a number**, not the word "wide" — and §0's sentence
   about the accelerated ceiling being reachable only with de-embedding off appears somewhere a
   reader will find it.
