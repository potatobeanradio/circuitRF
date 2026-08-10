# Brief — edge mesh on curved geometry

**A FOLLOW-UP to L8b**, in `src/Engine/Mom/SurfaceMesher.cs`. It is not a slice of L9 and touches no
Green's function, no fill and no solve.

Read `src/Engine/Mom/CLAUDE.md`'s own **L8b** section first — the tensor-grid decision (D8), the
edge-grading rule (R-msh-5) and the staircasing measurements are all there and are not repeated here.

---

## §0 — the finding, measured

**`EdgeCells` does nothing at all on an outline with no axis-parallel edges.** Meshed through the
production path (`SurfaceMesher.Mesh`), FR-4 starter, 10 GHz, `Auto = false`:

| shape | EdgeCells=0 | 3 | 10 | 20 | min cell |
|---|---|---|---|---|---|
| straight 20 × 2.9 mm rect | 247 | 552 | 1145 | 1286 | 580 → 84.5 µm |
| trapezoid (1 oblique side) | 707 | 1000 | 1672 | 1913 | 264 → 32.1 µm |
| **96-point disc, r = 1.45 mm** | **248** | **248** | **248** | **248** | **223 µm, flat** |

N is **identical** at every `EdgeCells` for the disc. The mesh is the plain λ_g-driven marcher and
nothing else; there is no graded fan anywhere on the rim.

### The mechanism, and it is deliberate

`SurfaceMesher.CollectBoundaryLines` (≈ line 631) classifies every ring edge as vertical, horizontal
or oblique. Vertical and horizontal edges contribute a **hard gridline** and — if long enough —
an **edge attractor**, which is what drives the grading. An oblique edge contributes **neither**:

> "A non-axis-parallel edge contributes NEITHER — which is D9's guarantee that a 96-point smooth
> outline cannot inflate the grid."

That guarantee is real and must be preserved: a `MKlopfPCell` outline is tessellated at 96 points, and
one attractor per vertex would be 96 graded fans on one part. **The gap is that the rule throws out
the physics along with the cost.** The graded fan exists to resolve the 1/√d current crowding at a
metal rim (R-msh-5); a curved rim has that crowding just as a straight one does, and currently gets
nothing.

A taper responds only on its axis-parallel sides — its two end caps and its one horizontal flank —
so the 32.1 µm minimum cell above comes from the narrow **end cap**, not from the slanted rim that
is the whole point of the part.

### The separate, already-measured half: staircasing

L8b measured the outline quantisation itself on the real PCells: **17–24% worst local width error and
5.5–11% RMS**, against a global area error of 0.47–0.59%. A Klopfenstein taper's entire value is a
controlled equiripple |Γ| of 0.05, so the local number is the one that matters and the global one
flatters it.

**These are two different defects and they want two different answers.** Unresolved rim current is an
edge-grading problem; a rim in the wrong place is a cell-shape problem. Do not conflate them.

---

## §0.1 — two traps, both already paid for once

**1. `PlanarMeshSettings.Resolved` discards everything when `Auto` is true.**

```csharp
public PlanarMeshSettings Resolved => Auto ? new PlanarMeshSettings(Auto: false) : this with { … };
```

`PlanarMeshSettings.Default` has `Auto = true`, so `Default with { EdgeCells = 10 }` meshes at
`EdgeCells = 3`. The first probe written for this brief did exactly that and reported "EdgeCells is
inert" for a **straight line** — which is false, and would have sent the whole investigation at the
wrong target. **Every fixture must set `Auto = false` explicitly.**

**The production path is correct and was checked** — `EmSetupEditorViewModel` lines 651/653/671 all
write `pm with { Auto = false, … }`. There is no bug here; only the test fixture is easy to get wrong.

**2. A non-monotonic N was observed on a hand-built 45° bend and is NOT yet a finding.** A crude
L-shape with a 45° chamfer measured N = 23,891 / 11,438 / 20,146 at EdgeCells 3 / 10 / 20 — both
non-monotonic and, at the default, **4.8× over R17's 5,000 ceiling**. L8b measures the *real*
`MBendPCell` at 550 on the same technology, so the fixture is probably unrepresentative. **Reproduce
it against real PCell geometry before drawing any conclusion from it** — if it survives, it is its own
bug (the growth-ratio knife edge L8b's own notes describe) and outranks the rest of this brief.

---

## §1 — M0: measure the shipping parts first

**Do this before choosing an approach.** The disc above proves the mechanism; it does not say how much
it costs on geometry anyone ships.

`tests/Ui.Tests/Em/PlanarMeshPCellTests.cs` already meshes `MTaperPCell`, `MKlopfPCell` and
`MBendPCell` (they live in `src/Ui`, so an `Engine.Tests` fixture cannot reach them). Extend it:

- For each part × both starter technologies × `EdgeCells` ∈ {0, 3, 10, 20}: report N, cell count,
  minimum cell, and **the minimum cell within one bulk-cell distance of the oblique rim** — the last
  is the quantity this brief is actually about, and total N does not measure it.
- Report whether N responds to `EdgeCells` **at all** for each part.

Expected from the mechanism: MLIN responds fully; MTaper and MBend respond on their axis-parallel
edges only; MKlopf's smooth flanks get nothing.

---

## §2 — M1: does a graded fan on a staircased rim even help?

**This is the question that decides whether the work is worth doing, and it must be answered with a
converged physical quantity rather than with a cell count.**

The 1/√d crowding is at the **true** rim. A staircased rim is in the wrong place by up to a cell, so
refining toward a *tread's* own edge may resolve the quantisation artifact rather than the physics —
and would then cost unknowns for nothing, or worse, converge confidently onto the staircase.

**The measurement.** Take a quantity with a refinement limit, on geometry with a genuinely curved rim:

- static `C_pul` via `PlanarFill.ScalarPotentialMatrix` at ω → 0 (L8c's own Tier 5 harness — cheap, no
  frequency sweep, no calibration), or
- `Z_c`/ε_eff through the full path if a solve is affordable.

Compare three ladders, each refined along its own axis:
1. today's mesh (no rim attractors),
2. today's mesh + rim attractors (M2 below),
3. a uniformly refined mesh, as the reference limit.

**If (2) reaches the limit at materially fewer unknowns than (1), build it. If it does not, stop and
report that** — the answer is then that curved geometry needs conformal cells (§4), and a fan on a
staircase is a cost with no payoff. **A negative result here is a legitimate deliverable**; this area
has three already on the record (L7b-b's Route B, L9c's amplitude cap, L9e's ACA).

---

## §3 — M2: rim attractors, decimated by RUN rather than by vertex

The cheap approach, and the one that preserves D9 by construction.

An oblique **run** is a maximal chain of consecutive oblique edges between two axis-parallel ones (or
around the whole ring, for a closed curve). A 96-point disc has exactly **one** run; a taper has one
per flank; a mitred bend has one. Emit attractors from the RUN, not from its vertices:

- **one attractor per run per axis**, at the run's own extreme coordinate in that axis (its extent is
  what the grading has to reach across), or
- a small fixed number (2–3) spread along the run, if M1 shows one is too coarse for a long flank.

D9 then holds numerically rather than by exclusion: 96 vertices still yield O(1) attractors, and the
existing "long enough to crowd" filter (`≥ 0.2 ×` the polygon's own extent, line 654) already keeps a
staircase tread from qualifying — it is the same test, asked of a run.

**Keep it behind the existing `EdgeMesh` flag.** No new user control: D3 permits exactly three and
this is not a fourth.

**What must not change:** a Manhattan polygon's mesh must be **bit-identical**. That is the R-msh-1
tiling guarantee and every L8b/L8c/L8d/L9 number in the repo rests on it. Gate it as an equality on
`SurfaceMesherTests`' own hero fixtures — §10.7's FR-4 hero must still measure **N = 552** exactly.

---

## §4 — out of scope, and named so it is not attempted by accident

- **Conformal / cut boundary cells, and triangles (RWG).** These address *staircasing*, not edge
  grading. They are a different and much larger phase: a cut cell breaks the rooftop pairing L8c's
  fill and L9c's via basis both assume, and RWG replaces the basis outright. If §2 comes back
  negative, that is the phase to scope — with its own brief.
- **Any change to the fill, the kernel, ports, de-embedding or the solve.**
- **Any change to `EdgeFractionOfReference` (0.03) or `EdgeGrowthRatio` (1.7)**, or to
  `SurfaceMesher.EdgeReferenceLength`'s conductor-width choice — L8c closed R-fil-12 on that and it
  measured 0.18% from the consensus limit. Do not reopen it here.
- **A fourth user control.**

---

## §5 — one wording fix, worth doing regardless of the outcome

`SurfaceMesher`'s own note reads *"Edge mesh on: N graded cell(s) at **every axis-parallel conductor
edge**…"*. That is accurate and nobody reads the qualifier. On an all-curved part it reports an edge
mesh that does not exist anywhere on the part.

**When the meshed geometry contributes no attractors on a given axis, say so** — e.g. *"…no
axis-parallel edge on this artwork qualifies, so no edge grading was applied; a curved outline is
approximated by a staircase (see below)."* This is the same class as the `EffectiveEdgeCells` clamp
note added 2026-08-09: a control that silently does nothing is worse than one that says why.

---

## §6 — gates

1. **M0's table** — the shipping PCells' response to `EdgeCells`, per part, per technology.
2. **M1's convergence comparison** — a converged physical quantity, three ladders, reported as a
   table. **A negative result closes the brief.**
3. Manhattan meshes **bit-identical**; §10.7's hero still N = 552.
4. A 96-point disc's N **responds** to `EdgeCells` (the direct regression for §0).
5. D9 preserved: a 96-point outline contributes O(1) attractors, asserted on the count, not on N.
6. `dotnet test` green; the routine `Engine.Tests` tier stays inside its budget (it is already at
   ~65 s against a ~60 s ceiling — see L9e's own note, and do not add a routine-tier sweep).
7. The §5 note fires on an all-curved part and does not fire on a Manhattan one.

## §7 — if something here turns out to be wrong

Report it. The §0 table was itself produced by a probe that was wrong the first time, and the two
observations in §0.1 are explicitly flagged as unverified. Measuring first is the standing rule in
this area and it has paid for itself repeatedly.

---

# RESULTS (2026-08-09) — §2 came back NEGATIVE, and the brief is closed on that

**M0 and M1 are done. M2 was built only as a MEASUREMENT SEAM and is not the default.** §5's wording
fix shipped regardless of the outcome, as it said it should. Full detail, every table, and the file
map are in `src/Engine/Mom/CLAUDE.md`'s own "Edge mesh on CURVED geometry" section; the Ui-side half
is in `src/Ui/Layout/Em/CLAUDE.md`. Gate: `Ui.Tests` 6,134 green, `Firewall.Tests` 6/6,
`Engine.Tests` +4 routine (~0.2 s) and +2 `Category=Benchmark` (4 m 52 s together).

## §0 reproduces exactly — and M0 says the defect is NARROWER than "smooth parts get nothing"

The disc is confirmed: **N = 248 at `EdgeCells` 0, 3, 10 and 20 alike**, min cell flat at 223 µm.

**But §1's own suggested metrics — total N, and the minimum cell near the rim — BOTH LIE, and a
reader of this brief needs that before reading its table.** Every shipping PCell responds to
`EdgeCells` in N, and every one shows a minimum cell collapsing ~8× (176 µm → 21 µm on PCB). That
reads as "the rim responded" and is false: the fans come from the **axis-parallel end caps**, whose
attractors refine whole grid columns, and a taper's rim passes within one bulk cell of its own caps.
A minimum taken over the whole rim therefore reports the cap's fan.

The quantity that is actually about this brief is the **transverse grid spacing at the rim point
FARTHEST from any axis-parallel edge**:

| part (PCB 2-Layer, 10 GHz) | N at EdgeCells 0 → 20 | MID-RIM spacing at 0 → 20 |
|---|---|---|
| MLIN straight (control) | 247 → 1,286 | — (no oblique edge) |
| MBend mitred | 196 → 1,736 | 580 → 694 → 245 → 213 µm |
| MTaper 2.9 → 1.0 mm | 657 → 851 | **263.64 µm, FLAT** |
| MKlopf on-axis | 605 → 1,115 | **176.36 µm, FLAT** |
| MKlopf Offset | 497 → 986 | **176.21 µm, FLAT** |

MMIC GaAs is the same shape (MTaper flat at 6.59 µm, MKlopf flat at 1.55 µm). **MBend is not a
counter-example**: its one oblique edge is the mitre, 1.2 mm from two long axis-parallel edges whose
fans reach it — and even there `EdgeCells = 3` (694 µm) is *worse* than `EdgeCells = 0` (580 µm),
which is gridlines being moved about rather than anything being resolved.

`E0` / `E0b` in `tests/Ui.Tests/Em/PlanarMeshPCellTests.cs`. §0.1 item 1's trap is real and the
fixtures set `Auto = false` explicitly; a straight MLIN control is included precisely so a flat row
cannot be read as "EdgeCells is inert".

## §0.1 item 2 — the non-monotone 45° bend was an UNREPRESENTATIVE FIXTURE

Asked of the **real** `MBendPCell` on the same technology, N is monotone at every angle and inside
R17's ceiling:

| MBend angle | EdgeCells 0 / 3 / 10 / 20 |
|---|---|
| 45° | 1,190 / 1,799 / 2,840 / 3,616 |
| 90° | 196 / 550 / 1,484 / 1,736 |
| 135° | 2,363 / 2,856 / 3,879 / 4,176 (Warn) |

It is **asserted rather than reported**, because §0.1 is right that if a shipping bend ever does go
non-monotone that is its own bug and outranks the rest of this brief.

## §2 — THE ANSWER IS NO, and it is the staircase that says so

Quantity: static capacitance via `PlanarFill.ScalarPotentialMatrix` at ω → 0, εᵣ = 1 so the kernel is
closed form and only the mesh can be wrong. Geometry: the 96-point disc, r = 1.45 mm, over the FR-4
starter's ground plane. `E3` in `tests/Engine.Tests/Mom/PlanarRimGradingTests.cs`.

| ladder | N ≈ 250 | ≈ 330 | ≈ 670 | ≈ 1,300 | **best** |
|---|---|---|---|---|---|
| (1) today's mesh | 1.494% | 0.335% | 0.265% | 0.413% | **0.265%** |
| (2) `PerRun` rim attractors | 0.331% | 0.447% | 0.588% | 0.478% | 0.331% |
| (2') `PerRunSampled` | 0.745% | 0.511% | 0.768% | 0.501% | 0.501% |

**(2) does not reach the limit at fewer unknowns than (1). It does not reach it at all better, and
the sampled variant is worse.** §2's own instruction is then "stop and report that".

**The reason, and it is measured rather than argued: ladder (3) does not converge monotonically.** A
uniformly refined disc reads 137.48 / 138.31 / 138.51 / 137.59 / 137.94 fF at N = 324 … 3,972 — a
**non-monotone band of 0.669%, wider than every difference being compared** — and the staircase's own
area error, reported beside each rung, wanders over the same range (0.16% … 1.13%) in step with it.
That is L8b's already-recorded mitre finding seen on a smooth outline: the quantisation error depends
on how the grid happens to *align* with an oblique edge, not only on how fine it is. §2's worry was
exactly right — refining toward a tread's edge resolves the artifact, and the artifact is an order of
magnitude larger than anything the fan changes.

**A CONTROL WAS ADDED THAT §2 DID NOT ASK FOR, and it is what makes the negative believable** (`E3a`).
Without it, "the rim ladders improved nothing" is indistinguishable from "this harness cannot see edge
grading at all", and this area has been burned nine times by an oracle being the broken part. Same
harness, same quantity, on a Manhattan square where the fan lands on a rim that is where the metal
actually is:

| | N = 40/144 | 180/264 | 760/840 | 1,624 | 3,280 |
|---|---|---|---|---|---|
| uniform (edge mesh off) | **4.437%** | 2.224% | 0.970% | 0.556% | 0.279% |
| edge-graded (EdgeCells 3) | **0.431%** | 0.501% | 0.455% | 0.453% | 0.279% |

**At the shipping mesh grading is 10× better, and the ungraded ladder needs ~20× the unknowns to
catch it.** So `EdgeCells` is doing real work on Manhattan artwork and on the axis-parallel *parts* of
a taper or bend; it simply has nothing to grip on a smooth flank. (The graded ladder's flatness at
~0.45% is R-fil-12's own already-recorded mechanism, not a defect: the conductor-width edge cell does
not shrink with cells/λ, so its sequence is flat because it is already at its own limit.)

**Verdict, stated the way the brief asks: edge meshing on curved geometry improves neither accuracy
nor cost-at-fixed-accuracy. §4 is the answer — conformal / cut boundary cells — and it wants its own
brief.** Three precedents on the record for a measured deferral: L7b-b's Route B, L9c's amplitude cap,
L9e's ACA.

## §3 — M2 exists, as a SEAM, and D9 is preserved NUMERICALLY

`PlanarRimGrading` (`None` / `PerRun` / `PerRunSampled`), default `None`, a trailing parameter on
`SurfaceMesher.Mesh` — a measurement seam of exactly `PlanarEdgeReference`'s kind, **not a fourth user
control**; `PlanarMeshSettings` is unchanged. Attractors come from the RUN at each end of its own
coordinate range per axis, filtered by the SAME "long enough to crowd" test (a fifth of the polygon's
own extent) asked of the run. `PerRunSampled` adds three interior samples spread along the run's **arc
length**, each transverse to the local tangent — **spreading by coordinate is wrong for a closed
curve**, since the midpoint of a disc's y-range is the disc's centre and not on the rim at all.

- **D9 asserted on the COUNT, not on N** (`E1c`, via the new public `SurfaceMesher.EdgeAttractors`):
  a 24-, 96- and 384-point disc each give **0 shipped / 4 `PerRun` / 7 `PerRunSampled`**, unchanged as
  the tessellation is quadrupled.
- **Manhattan meshes are BIT-IDENTICAL with the seam on** (`E2`): gridlines, cells and bases compared
  as equalities at EdgeCells 0/3/10 in both modes, and §10.7's FR-4 hero is still exactly **N = 552**.
  It cannot move — a Manhattan polygon has no oblique edge and therefore no run.
- **The negative result is ASSERTED**, not merely written down: `E3` fails if rim grading ever beats
  the shipped mesh by 2×, so a later change that made it genuinely pay turns red rather than quietly
  contradicting the note in `CLAUDE.md`.

**One fixture trap, and it cost real time.** Building the disc with its vertices offset by half a step
— the obvious way to keep vertices off the axes — makes the four edges that STRADDLE each axis exactly
axis-parallel to the mesher's own 1e-12 tolerance. The ring splits into four runs and the fixture hands
itself the very gridlines it exists not to have: **16 attractors instead of 4**. Put the vertices ON
the axes; a vertex is geometry to be covered, never a gridline.

## §5 — done, and it is worded for the drawn-staircase case too

When an axis collects no attractor the report adds *"…but NO edge grading was actually applied…"*,
naming the axis and saying whether raising Edge cells can change the mesh at all. It is worded on "no
conductor edge is both axis-parallel and long enough" rather than on curvature, so it is also accurate
for a user-drawn staircase (all edges axis-parallel, none long enough). `E1b` asserts it fires on the
disc and does **not** fire on the Manhattan hero.

## §7 — things in this brief that turned out to be wrong or incomplete

1. **§1's metrics.** Total N and "the minimum cell within one bulk-cell distance of the oblique rim"
   both report the END CAPS' fans and read as a rim that responded. Superseded by the mid-rim measure
   above. This is the single most important correction here — the brief's own expectation ("MKlopf's
   smooth flanks get nothing") is right, and §1's metric would not have shown it.
2. **§0.1 item 2's fixture** was unrepresentative, as it suspected. No bug behind it.
3. **§2's three ladders collapse to two on a disc.** Ladder (1) IS ladder (3) there — a disc has no
   axis-parallel edge, so "today's mesh" and "a uniformly refined mesh" are the same mesh. The
   reference had to be that ladder pushed much further, and the Manhattan control had to be added to
   make the comparison mean anything.
4. **§2's implicit assumption that a refinement LIMIT exists to compare against** is the thing that
   actually fails. On a staircased rim the sequence is non-monotone at 0.669%, so there is no
   converged value at the level where grading would matter — which is itself the answer.

## Not done, on purpose

- **Conformal / cut boundary cells and RWG** (§4). The measurement says staircasing is the binding
  term, so this is now the phase to scope — with its own brief.
- **Making `PerRun` the default.** §2 says no.
- **Any change to the fill, kernel, ports, de-embedding, solve, `EdgeFractionOfReference`,
  `EdgeGrowthRatio`, `EdgeReferenceLength`, or `PlanarMeshSettings`.** None was made.
