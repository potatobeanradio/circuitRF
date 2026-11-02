# Brief — conformal (cut) boundary cells

**A FOLLOW-UP to L8b/L8c. It is NOT a slice of L9 and it touches no Green's function.** It is the
phase `brief-edge-mesh-on-curved-geometry.md` named in its §4 and its RESULTS section pointed at:
staircasing, not edge grading, is what limits a full-wave answer on a curved part, and closing it
needs the cell to follow the metal rather than the grid.

Read first, in this order:

- `src/Engine/Mom/CLAUDE.md` §**L8b** (D8's tensor grid, R-msh-1's tiling guarantee, D2's staircasing
  measurements) and §**"Edge mesh on CURVED geometry"** (the negative result that scheduled this).
- `src/Engine/Mom/CLAUDE.md` §**L8c** — the six closed forms, D4's per-cell potential matrix, D5's
  block-diagonality, the quadrature rule. **This is the file that decides whether the phase is
  tractable**, and §3 below is about nothing else.
- `src/Engine/Mom/PlanarBasisFunctions.cs`'s own header. The rooftop's three properties are asserted
  as EQUALITIES, not tolerances, and they have to survive.

Nothing in this brief is repeated from those.

---

## §0 — the prize, and it is already measured

| | measured, on the shipping mesh | source |
|---|---|---|
| MTaper 2.9 → 1.0 mm, local width error | **23.6% worst, 10.4% RMS** (global area error 0.47%) | L8b D2 |
| MKlopf on-axis | **20.9% worst, 11.2% RMS** (0.59%) | L8b D2 |
| MKlopf Offset 5 mm | **17.2% worst, 5.5% RMS** (0.54%) | L8b D2 |
| MBend mitre, cut-area error vs cells/λ 10/20/40/80 | 13.6% / 2.8% / 0.7% / **6.7%** | L8b D2 |
| 96-point disc, static C, uniform refinement N = 324 … 3,972 | **non-monotone, 0.669% band** | edge-mesh RESULTS |
| the same disc's staircase AREA error over that ladder | 0.16% … 1.13%, wandering in step with C | ditto |

**Two separate facts, and the second is the one that actually forces this phase.** The first is that
the error is large where it matters — a Klopfenstein taper's whole value is a controlled equiripple
|Γ| of 0.05, and a 21% local width error is enormous against that while the 0.5% area error says
nothing about it. The second is that **refining does not reliably fix it**: the error depends on how
the grid happens to ALIGN with an oblique edge, not only on how fine it is, so a user cannot buy
accuracy with unknowns on a curved part. There is no converged value to aim at.

**And the counter-measurement, so the prize is not overstated.** The edge-mesh brief's own control
found that on a Manhattan rim, grading is worth 10× accuracy at the shipping mesh; L8d found that
what limits a DE-EMBEDDED answer is **radiation**, at a few 1e-3 at 2 GHz and 1e-2 at 10 GHz on
1.6 mm FR-4, which no mesh change touches. **On a straight line this phase should change essentially
nothing.** If M0 finds it changing a uniform line's s-parameters by more than the radiative floor,
that is a bug in this phase, not a benefit.

---

## §1 — scope: CUT CELLS, not RWG, and the reason is a cost the repo has already written down

**RWG is out of scope and is not a milestone here.** It replaces the basis outright: a triangulator,
new singular-integral machinery over triangles, a new port operator, a new via attachment, and a
re-derivation of D4/D5/D6 and everything L9c/L9d built on them. L8c's own note already says what
would be lost — *"the classic near-singular difficulty comes from doing BOTH integrals numerically,
and here only one of them is"* — and RWG gives that back.

**A cut cell keeps all of it and changes one thing.** L8b's `PlanarCell` already anticipates this in
its own doc comment:

> *"a future conformal or diagonal boundary cell — one straight cut through an otherwise rectangular
> cell, which is a far smaller commitment than a triangulator and addresses the mitre directly — can
> be added as an extra field here without reshaping the type or the report. It is explicitly NOT
> built in L8b."*

Take that literally. **One straight cut per cell, and no more.** The interior of the mesh stays
rectangular, the tensor grid is untouched, R-msh-2's `(LayerIndex, IY, IX)` ordering is untouched,
and every cell that is not on the rim is bit-identical to what ships today.

**What is explicitly out of scope, named so it is not attempted by accident:**

- RWG / triangles / a Delaunay triangulator.
- More than one cut per cell (a cell straddling a sharp corner, or two rims of a narrow gap). §2's
  own gate is what decides whether that is a refusal or a mesh-refinement instruction — it must be
  one of the two, never a silently-wrong cell.
- Any change to the tensor grid itself, to `PlanarEdgeReference`, to `EdgeFractionOfReference` (0.03)
  or `EdgeGrowthRatio` (1.7). R-fil-12 closed those at 0.18% from the consensus limit.
- Any change to the Green's function, DCIM, the kernel set, ports' *electrical* model, de-embedding's
  algebra, or the solve.
- `PlanarRimGrading`. The edge-mesh brief measured it as a negative and it stays `None`. **Whether it
  becomes positive once the rim is in the right place is a legitimate question and it belongs to
  §6**, not to the start of this phase — do not turn it on speculatively.

---

## §2 — M1: the cut cell as GEOMETRY, before any physics touches it

**Do this first and gate it on tiling alone.** A cut cell that does not tile its polygon is a solver
that solves a slightly different structure and reports a smooth, plausible, wrong s-parameter —
R-msh-1's own words, and the whole reason that rule exists.

`PlanarCell` gains an optional cut. The suggested shape, because it keeps every existing member
meaningful: a half-plane `(nx, ny, d)` with the cell's kept region being `nx·x + ny·y ≤ d`
intersected with the rectangle, plus the derived `Area` and `Centroid`. Null means "whole rectangle"
and every existing consumer of `Width`/`Height`/`Area`/`XMin…YMax` keeps working unchanged — which is
what makes the blast radius auditable rather than a rewrite (**18 files in `src/Engine/Mom/` read
those members; `PlanarFill` alone has 21 sites**).

**R-cut-1 — R-msh-1 is EXTENDED, not weakened: the union of the cells is the input polygon to the
last DBU for NON-Manhattan artwork too.** Today it holds for Manhattan and the deviation is measured
rather than hidden (`PlanarMeshReport.StaircasedPolygons`). The gate is the area error going from
L8b's measured 0.47–0.59% to **round-off**, on all three shipping PCells and on the 96-point disc.

**R-cut-2 — a Manhattan mesh is BIT-IDENTICAL, and it is asserted on the gridlines, the cells and the
bases as equalities.** Every L8b/L8c/L8d/L9 number in the repository rests on it; §10.7's FR-4 hero
must still measure **N = 552** exactly. A Manhattan polygon has no oblique edge, so no cell can be
cut — the property should hold by construction, and be asserted anyway.

**R-cut-3 — THE SLIVER, and this is the hazard that will actually bite.** A cut that leaves 1e-9 of a
cell's area is the classic cut-cell failure: the basis normalisation is 1/Area, so a sliver puts an
enormous row in the matrix and destroys the conditioning — and it does so *silently*, because the
matrix is still symmetric and still factors. **The remedy is cell MERGING** (absorb a sliver into the
neighbour it shares its largest face with, giving one L-shaped or trapezoidal cell rather than two).
It is not optional, and the threshold is a MEASUREMENT: sweep the area fraction below which merging
fires and report the conditioning and the answer either side of it. Report the merge count in the
mesh notes — a mesher that silently re-shapes cells is worse than one that says it did.

**R-cut-4 — a shared EDGE can be cut to zero length.** The rooftop's normalisation is "unit total
current across the shared edge", i.e. 1/L. Two cells that are adjacent on the grid but whose shared
edge is entirely outside the metal are **not** a basis, and one whose shared edge is a sliver is the
R-cut-3 problem again in the other axis. Decide it in the MESHER, where the basis set is built, and
assert the resulting count — not in the fill, where it would be a guard on a division.

**M1's deliverable is a table, not a pass:** cut cells / merged cells / total cells, and the area
error, for the three shipping PCells on both starters and the disc. Plus N against R17's ceiling —
**a conformal mesh may need MORE unknowns at a given cells/λ (cut cells still pair into rooftops) or
FEWER for a given accuracy, and which one it is on real parts is measured rather than assumed.**

---

## §3 — M2/M3: the basis and the fill. THIS IS WHERE THE SCHEDULE DIES

§10.2 names the singular integrals as the second place a schedule goes to die, and L8c's answer was
to make the inner integral closed form over a rectangle. **A cut cell is not a rectangle**, and
pretending the difficulty is elsewhere is how this phase fails.

### M2 — the rooftop over a cut pair (`PlanarBasisFunctions`)

The three properties are asserted as EQUALITIES today and must stay equalities:

- `∇·f = ±1/Area` exactly — **this one is free if `Area` means the CUT area**, which is the argument
  for putting the cut on the cell rather than beside it.
- continuity across the shared edge — needs the ramp measured from each cell's own outer edge, which
  a cut can move.
- zero on the pair's two outer edges, so no charge lands on the rim — **this is the one to check
  hardest**, because the rim is exactly what a cut cell now has.

**State whether the ramp `ξ/Area` is still the right weight on a cut cell, or whether it has to
become the cut cell's own linear profile — and gate it on `∫∇·f dS = 0` and on the current across
the shared edge being exactly 1 A.** If the answer is that a cut pair needs a different weight, that
is a finding and it changes M3's integrals; find it in M2, not in M3.

### M3 — the inner integral over a cut cell

`RectangleIntegrals` carries **six** closed forms (`∫∫dS/R`, `∫∫ln r dS`, `∫∫r dS` and their three
first moments), derived from antiderivatives via a corner primitive, checked to 1e-12 against
adaptive quadrature at five placements. **Three routes, and D3's standing rule applies — MEASURE,
then choose, then record the measurement:**

- **(a) Derive the same six over a TRIANGLE**, and express a cut cell as rectangle-minus-triangle or
  rectangle-plus-triangle. The corner primitive generalises: the classic route is to reduce the
  surface integral to a sum over the polygon's EDGES. **Derive it, do not transcribe it** — the same
  rule L8a followed for the Bessel functions and L8c for these six, and for the same licensing and
  accuracy reasons. Cost: real derivation work, and six forms not one.
- **(b) Numerical inner integral for CUT cells only**, keeping the closed form for the ~70% of cells
  that are whole. Cheap to write and it is where the difficulty L8c removed comes back — a cut cell
  is *always* on the rim, so it is always a near-neighbour of another cut cell, which is precisely
  the configuration L8c measured as the limiting case (*"the limiting case is not the self term but a
  face-TOUCHING neighbour"*).
- **(c) Keep the closed form and correct only the WEIGHT** — integrate over the whole rectangle and
  scale by the cut area fraction. **Almost certainly wrong and it is named so nobody reaches for it
  quietly**: it puts source where there is no metal and gets the singular self term wrong by exactly
  the amount the phase exists to fix. If it is tried, it must be measured against (a) or (b) and
  reported, not shipped on plausibility.

**The measurement that chooses between them is L8c's own Tier 2/3 ladder, re-run:** entries against
`PlanarPairOracle`'s cross-correlation form (which evaluates no closed form of any kind and therefore
survives this change unmodified — extend it to a cut support), and the whole fill at εᵣ = 1 against
it. L8c reached **5.0e-6** there; this phase must say what it reaches and whether the fill is still
three decades more accurate than the kernel it fills from. **If it is not, the honest deliverable is
that number and a refusal, not a shipped default.**

**D6's frequency-independent core must survive.** `PlanarSweepResult.CoreFillCount` is asserted at
exactly 1 for a 3-point and a 101-point sweep, and the cores are purely GEOMETRIC — a cut is geometry,
so this should hold unchanged. Assert it; do not assume it.

**D5's block-diagonality is the one that could quietly break.** An x-rooftop and a y-rooftop are
pointwise orthogonal *because f is purely x̂ or purely ŷ*. A cut does not change the direction of f,
so D5 should survive — but `T4_2` asserts it as a formulation fact with the mixed pair's SCALAR block
asserted non-zero so it cannot pass for the wrong reason. Re-run it on a cut mesh.

---

## §4 — M4: everything else that is keyed to a rectangular cell

Small, but each is a silent-wrong-answer if missed. Enumerated rather than left to discovery:

- **`PlanarPort`** — the reference plane is the shared edge of the two OUTERMOST cells (L8d D2). A
  port sits on a drawn feed, which is Manhattan, so the port cells should be uncut. **Assert that**,
  and refuse a port whose resolved cells are cut, by name — a port on a cut cell is a different port
  and R-prt's whole error-box argument does not cover it.
- **`PlanarCalibration.BuildLine`** — a standard is a uniform rectangle built from the DUT's own
  gridlines, so it has no cut cells and needs no change. **Assert it** (L9d's `M3_3` already asserts
  the analogous property for levels), because a standard that acquired cut cells would be calibrating
  out something the DUT does not have.
- **Via footprints** — Manhattan by construction (L9c), so uncut. Assert; and confirm the footprint's
  hard gridlines still tile it exactly, since L9c measured that a via VANISHES silently without them.
- **`PlanarCurrentDensity`** — `J = I / (the cell's TRANSVERSE extent)`. On a cut cell that extent is
  not `Width`/`Height`. Get it right or the heat map is wrong exactly on the rim, which is the part
  anyone looks at.
- **`PlanarMeshReport`** — `MinCellEdgeM`, `MaxCellEdgeM`, `CellsAcrossNarrowestConductor` and R17's
  verdict all read cell extents. Decide what they mean for a cut cell and say so in one place.
- **`StaircasedPolygons` and its note** must stop claiming a staircase when the cells are conformal.
  The edge-mesh brief's §5 note ("…but NO edge grading was actually applied…") likewise needs
  revisiting: on a conformal mesh an oblique rim MAY now deserve a fan, which is §6.
- **`LayoutRenderer.PlanarMesh.cs`** draws `SKRect` per cell (line ~122) and derives the gridline
  overlay from `XMin`/`XMax` (line ~210). A cut cell needs an `SKPath`. **The overlay is the only
  place a user can SEE that the mesh followed the metal**, so this is not cosmetic — it is the
  feature's own evidence.

---

## §5 — M5: THE UI. A fourth control, deliberately, and D3 is reopened here

**D3 says `PlanarMeshSettings` carries "exactly three user controls, and no more", and this brief
adds a fourth on the owner's explicit instruction.** Record the reversal rather than slipping it in;
D3's reasoning still stands for everything else and is not being generally relaxed.

**Why this one earns it, and why it is not the same kind of control as the three.** Cells per
wavelength and Edge cells change how FINELY the same structure is discretised. Conformal cells change
**which structure is discretised at all** — a staircased disc and a conformal disc are different
geometry, not two resolutions of one. That is a modelling decision, and modelling decisions belong to
the user. It also needs an off switch on evidence rather than on taste: **every L8/L9 measurement in
this repository was taken on the staircase**, and a user reproducing one of them must be able to.

### The wiring, end to end, with the sites

1. **`src/Engine/Mom/PlanarMeshSettings.cs`** — add the control. Prefer a two-member enum
   (`PlanarBoundaryCells { Staircase, Conformal }`) over a `bool`: it names both states in the code
   and in the `.cem`, and a third member (per-polygon, or a refusal mode) does not then break the
   file format. **Add it as a TRAILING parameter of the positional record**, and check the way the
   `DirectVerticalKernel` insertion was checked — grep for `new PlanarMeshSettings(` and confirm
   every site is `Default` plus a name-based `with { }` before deciding.
   **`Resolved` must handle it**: `Auto = true` currently throws the whole record away
   (`new PlanarMeshSettings(Auto: false)`), which is §0.1's own already-paid-for trap. Decide
   explicitly whether Auto implies Staircase or Conformal and gate it — a fixture that sets the new
   control and leaves `Auto` on must not silently mesh the other way.
2. **`src/Ui/Layout/Em/EmSetupPersistence.cs`** — `CemPlanarMesh` gains the field with its default,
   and `ToFileModel`/`FromFileModel` carry it. **Follow the established omit-at-default pattern**
   (`DirectVerticalKernel = s.DirectVerticalKernel ? true : null`, line ~188) so a `.cem` written
   before this phase **round-trips byte-identically**. That is an existing, asserted property; gate it
   the same way.
3. **`src/Ui/Layout/Em/EmSetupEditorViewModel.cs`** — an `[ObservableProperty]` plus an
   `OnPlanarBoundaryCellsChanged` partial, modelled on `OnPlanarEdgeMeshChanged` (line ~665):
   suppress-check, no-op check, `SnapshotJson()` → `CommitEdit` → `InvalidateMesh()` → `Refresh()`.
   **Do NOT route it through `CommitMeshField`** — that committer is for staged TEXT fields, and its
   own comment says why the three share it.
4. **`src/Ui/Views/Layout/EmSetupEditorView.axaml`** — a fourth row in the **"Surface mesh"** group
   (line ~498, `IsVisible="{Binding ViewModel.IsPlanarAnalysis}"`), beside "Cells per wavelength",
   "Edge mesh" and "Edge cells". Grow the `RowDefinitions`. Label it for what it does — *"Boundary
   cells"* with *"Cut cells to follow curved and oblique edges"* — not for its implementation.
5. **`src/Ui/Layout/Em/EmSnpProvenance.cs` `MeshHash` (line ~161)** — **must include it.** It hashes
   `Auto|CellsPerWavelength|EdgeMesh|EdgeCells` today; leaving the new control out means a `.snp`
   produced with one boundary model is silently reported as current for the other. That is the exact
   staleness failure R-em-20 exists to prevent, and it is one line and easy to forget.
6. **The mesh NOTES** — say which boundary model produced this mesh, how many cells were cut, and how
   many were merged (R-cut-3). The notes are what the user reads after pressing the button, and this
   phase's whole visible effect lives there and in the overlay.

### The default, and when it flips

**Ship it OFF (`Staircase`) and flip the default to `Conformal` only when §3's own gate passes** — the
fill's accuracy against the correlation oracle, the tiling gate, the sliver gate, and a measured N
against R17. Flipping the default is a separate, deliberate act with its own line in the CLAUDE.md,
because it moves every number a user has previously recorded. **If the phase ends with the default
still off, that is a legitimate outcome and the note must say so plainly.**

### UI gates

- A `.cem` at the default round-trips **byte-identically**; one with the control set round-trips and
  reloads to the same value.
- Toggling it **changes `EmSnpProvenance.MeshHash`** and marks an existing `.snp` stale.
- It is **one undo entry**, and `InvalidateMesh()` fires (the panel must not show a stale N).
- The overlay draws the cut edges, and a Manhattan layout's overlay is unchanged.
- The control is not offered for the cross-section kernel (`IsVisible` already handles this — assert
  it, because kernel A has its own six mesh controls and they are a different group).

---

## §6 — deliberately deferred, with what would reopen each

- **Turning `PlanarRimGrading` back on.** The edge-mesh brief measured it as a negative *because the
  rim was in the wrong place*. Once it is in the right place the question is genuinely open, and the
  measurement is already written (`PlanarRimGradingTests.E3`, and its Manhattan control `E3a` says
  what a positive result would look like: ~10× at the shipping mesh). **Re-take it after M3, not
  before.** It is the natural M6 and it may be free.
- **RWG.** §1.
- **More than one cut per cell.** §1, and §2's gate decides whether it is a refusal. **DECIDED, and
  the gate's own answer is the phase's biggest limitation** — see RESULTS. It is a refusal today, and
  on MKlopf that refusal is PERMANENT rather than a refinement instruction: its outline carries 126
  reflex vertices, so once the mesh is fine enough for each to have a cell of its own the fallback
  count saturates at exactly 126 and the tiling guarantee never arrives. **The concrete reopener is
  CONVEX DECOMPOSITION, not a second cut**: `PlanarCellRegion` already holds a LIST of convex pieces,
  so a non-convex clip is representable — the mesher declines to build it rather than being unable
  to. That is the natural M7 and it is the difference between "exact on most parts" and "exact".
- **The tensor grid's own cost** — "a fine row anywhere is a fine row everywhere" — is untouched by
  this phase and is a different problem (L8b D8 chose it deliberately and measured the alternative's
  cost as landing on L8c).

---

## §7 — gates

1. **M1's table** — cut / merged / total cells, area error, and N against R17, for the three shipping
   PCells on both starters and the 96-point disc. Area error at **round-off**, not 0.5%.
2. **Manhattan bit-identical** — gridlines, cells and bases as equalities; §10.7's hero still N = 552;
   L8c's fill, L8d's de-embedded S and L9's via answers all unmoved.
3. **The rooftop's three properties still hold as EQUALITIES on a cut pair**, and `∫∇·f dS = 0` to
   machine precision rather than to a tolerance.
4. **The fill against the correlation oracle on a cut mesh**, reported as a number beside L8c's own
   5.0e-6. D6's `CoreFillCount` still exactly 1; D5's block-diagonality still exact.
5. **The sliver threshold, measured** — conditioning and answer either side of it, with the merge
   count reported in the notes.
6. **A CURVED part's answer converges MONOTONICALLY**, which is the thing the staircase makes
   impossible. Re-run the edge-mesh brief's own disc ladder: the 0.669% non-monotone band must
   collapse. **This is the single most important gate in the phase** — it is the user-visible promise,
   and it is the one a plausible-but-wrong implementation would fail while every tiling gate passed.
7. **A uniform line is unmoved** beyond L8d's radiative floor (§0). If it moves, that is a bug here.
8. The §5 UI gates, and `dotnet test` green with the routine `Engine.Tests` tier not growing a sweep
   (it is already over its ~60 s ceiling; new ladders are `Category=Benchmark`).

## §8 — if something here turns out to be wrong

Report it. Two things in this brief are guesses rather than measurements and are flagged as such: the
suggested half-plane representation in §2, and the ~70%-of-cells-are-whole figure implied in §3(b).
**A measured deferral is a legitimate deliverable in this area and there are four on the record**
(L7b-b's Route B, L9c's amplitude cap, L9e's ACA, and the edge-mesh brief this one follows) — if §3's
oracle says a cut cell's fill cannot reach L8c's accuracy without RWG, that result plus its number is
worth more than a shipped default nobody can justify.

---

## RESULTS — what this actually buys, in plain terms

*Written for someone deciding whether to switch the control on, not for someone maintaining the
mesher. Every number below is measured, and where something did not work it says so.*

### The problem, without the jargon

The solver covers your artwork with a grid of little rectangles and solves for the current in each
one. That is fine while your artwork is made of horizontal and vertical edges — the rectangles land
exactly on it. **The moment you draw a curve, a taper or a 45° bend, the rectangles cannot follow
it**, so the solver builds a staircase approximation of your shape and simulates *that* instead.

Two consequences, and the second is the one that actually hurts:

1. **The simulator is not analysing the part you drew.** On the shipping microstrip tapers the
   staircase gets the *local trace width* wrong by up to **17–24%** in places, even though the total
   metal area is only about 0.5% off. For something like a Klopfenstein taper — whose entire value
   is a carefully controlled reflection ripple of 0.05 — a 21% width error in the middle of it is
   enormous.

2. **Making the mesh finer does not reliably fix it.** This is the important one. With a staircase,
   how wrong the answer is depends on *where the grid lines happen to fall across your curve*, not
   just on how fine the grid is. Refine, and the answer can get worse before it gets better. So the
   usual engineering move — "I'm not sure this is converged, let me spend more compute" — **does not
   work on a curved part**. There is no settled answer to converge toward.

**Conformal ("cut") boundary cells fix both.** The rectangles in the middle of the metal are left
alone; only the ones on the rim are trimmed so their edge follows your artwork exactly.

### What was measured

A 96-sided disc, refined from 316 unknowns to 3,964, solving for its static capacitance at each step:

| | staircase (today's default) | conformal |
|---|---|---|
| **Does the simulated shape match what you drew?** | No — between 0.2% and 0.8% of the area is wrong, and *the amount changes every time you refine* | **Yes, exactly** — to the last decimal place the computer can hold, at every refinement |
| **Does refining converge on an answer?** | **No** — the value wanders up and down | **Yes** — it steps steadily toward a limit |
| **Spread of the answer over the last three refinements** | 0.669% | **0.279%** |

The first row is the real result. "Exactly" is not a figure of speech here: the trimmed cells tile
your drawn outline to round-off, so refining the mesh no longer quietly changes *which shape* is
being simulated. That is what makes the second row possible.

*One caveat, spelled out below rather than buried: "exactly" holds for artwork whose edge curves
consistently one way where the grid cuts it — which covers bends, tapers and the disc above. Artwork
whose edge bends back on itself is a partial case; see "Which parts it works on".*

### What it costs you

**Essentially nothing.** At matched settings the conformal mesh was *slightly smaller* than the
staircased one (316 unknowns vs 324 at the coarse end, 3,964 vs 3,972 at the fine end) — solve time
is set by the unknown count, so there is no meaningful runtime penalty. Building the mesh does a
little more geometry work, but meshing is milliseconds against a solve measured in seconds to
minutes.

### When it changes nothing at all

**If your layout is all horizontal and vertical edges, this control does nothing whatsoever** — not
"nearly nothing", literally nothing: the mesh is bit-for-bit identical to the one you get today, so
every number you have previously recorded on such a part is unaffected. It only ever acts where the
artwork is genuinely oblique or curved. The panel's own notes tell you which case you are in, and
report how many cells were trimmed.

### Which parts it works on, and the one it does not

This was measured on the actual library parts, on both the PCB and MMIC starter technologies, rather
than on test shapes. The result splits cleanly, and **the exception is the part you would most want
it for**, so it is stated up front:

| part | shape error today | with conformal cells |
|---|---|---|
| **Mitred bend** | 0.10% | **exact** |
| **Linear taper** | 0.47% | **exact** |
| **Klopfenstein taper (PCB)** | 0.59% | 0.77% — *worse* |
| Klopfenstein taper (MMIC) | 0.28% | 0.19% — better, but not exact |

"Exact" here means the simulated outline matches the drawn one to the last digit the computer holds.

**Why the Klopfenstein taper misses out.** The trimming only handles a rim cell that the metal
crosses *once*, leaving a simple wedge. A Klopfenstein taper's edge is not a simple curve — it has
gentle S-bends (inflections), and the end-blending option adds more. Where the outline bends *back*
inside a single cell, that cell cannot be described by one straight cut, so it falls back to the old
staircase behaviour for that cell alone.

**And refining does not fix it.** Normally "use a finer mesh" is the answer to this kind of thing.
Here it is not: the number of fallback cells rises and then *sticks* at 126 no matter how fine the
mesh gets — because that outline has exactly 126 inflection points, and once the mesh is fine enough
each one has a cell to itself. It is one permanently-imperfect cell per inflection, not a resolution
problem.

The practical upshot for a Klopfenstein taper: conformal cells still beat the staircase at fine
meshes (and at every mesh density on MMIC), but you do not get the exactness the other parts get, and
at coarse PCB settings you are slightly worse off. **This is fixable** — the internal representation
already supports splitting an awkward cell into several simple pieces; the mesher currently declines
to rather than being unable to. It was out of scope for this round and is the obvious next step.

### Does trimming a cell make the *maths* less accurate?

No — this was the thing most likely to go wrong, so it was checked hardest. Trimming changes the
shape the solver integrates over, and integrals over an odd-shaped cell are harder than over a neat
rectangle. Checked against a completely independent calculation, written from scratch for the purpose
and sharing no code with the solver, the trimmed-cell numbers agree to:

- **around one part in a hundred billion** for the interaction between two *different* cells — which
  is the overwhelming majority of the work; and
- **one to two parts per million** for a cell's interaction with itself, which is the genuinely hard
  case.

Two comparisons put that in scale. The existing solver manages about **five parts per million** on
ordinary rectangular cells, so trimmed cells are if anything *better*. And the underlying
electromagnetic model — the physics the whole thing is built on — is itself good to about **six parts
per thousand**, so the arithmetic here is roughly two and a half thousand times finer than the model
it is feeding. **Trimming costs nothing in numerical quality.**

(Worth knowing: the independent check is itself only good to about *three parts in ten million* on
that hardest case, so the true agreement may well be better than the numbers above. At that point the
yardstick is the limitation, not the thing being measured.)

### Where it will refuse, and why that is deliberate

- **A port must sit on a straight, axis-aligned feed.** If you put one on a chamfered or curved end,
  the run stops and says so by name — naming both remedies — rather than quietly analysing a
  differently-shaped port. Ports are for feeding the structure; that is what a feed line is for.
- **A cell your artwork crosses twice** falls back to a staircase *for that one cell*, and the count
  is reported in the panel. For most parts, refining makes that count go to zero; for the
  Klopfenstein taper it does not, for the reason above.

### Honest limitations

- **The improvement in the spread was 2.4×, not the 10× this brief originally asked for.** That
  target was not met and is not claimed. The claims that *are* made — the shape is now exact on most
  parts, and refining now converges — are the ones the measurements support, and they are the ones
  that matter for deciding whether to spend more compute.
- **This does not make curved parts *accurate*, it makes them *convergent*.** Other error sources are
  untouched: radiation loss in particular sits at a few parts in a thousand at 2 GHz and around a
  percent at 10 GHz on 1.6 mm FR-4, and no mesh change of any kind affects it.
- **The Klopfenstein taper is a partial case**, per the section above — and it is the one part where
  a coarse PCB mesh comes out slightly *worse* than today.
- **A sliver-cell safeguard exists and turned out to matter less than expected.** Where a grid line
  clips a corner off a cell, a very thin remnant can be left; those are absorbed into a neighbour.
  Measuring it showed the numerical harm from *not* doing so is much smaller than the design note
  assumed (about 1% worse conditioning over a 245× size ratio, and a 0.004% change in the answer) —
  so the safeguard is cheap insurance rather than a fix for a real cliff. That reversal is recorded
  rather than glossed.

### The default

**It ships OFF**, for two reasons.

The first is bookkeeping: **every accuracy figure recorded in this project so far was taken with the
staircase**, and anyone reproducing one has to be able to.

The second is the real one: **the Klopfenstein taper comes out slightly worse at coarse PCB
settings**, per the section above. Making something worse by default is not defensible even when it
is better in most cases, so it stays opt-in until the inflection handling is fixed — at which point
turning it on by default becomes the obvious call.

**Switch it on today** when you are simulating a bend, a linear taper, or any curved outline, and you
want an answer you can refine toward with confidence. For a Klopfenstein taper, switch it on and use a
fine mesh, or leave it off — the coarse setting is the one case that goes backwards.
