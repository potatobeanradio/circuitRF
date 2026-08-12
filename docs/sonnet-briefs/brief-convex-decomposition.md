# Brief — convex decomposition (the conformal mesher's reflex-vertex fallback)

**A FOLLOW-UP to `brief-conformal-boundary-cells.md`. It is the M7 that brief's §6 named**, and it is
the one thing standing between "exact on most parts" and "exact". It touches **no Green's function, no
DCIM, no kernel set, no port model, no de-embedding algebra and no solve** — and, if §1 is right, no
closed-form integral either.

**The title presupposes the answer, and §1 is about why that may be wrong.** §6 of the conformal brief
named convex decomposition as the concrete reopener because `PlanarCellRegion` already holds a LIST of
convex pieces. That is true and it is not the whole story: reading the code that consumes those pieces
says the *convexity* test may simply be the wrong predicate, in which case the fix is a smaller change
than decomposing anything. **M0 is a measurement that decides between those, and it comes first.**

Read first, in this order:

- `src/Engine/Mom/CLAUDE.md` §**"Conformal (cut) boundary cells"** — in particular **"THE LIMITATION
  THAT MATTERS MOST"** (the saturation ladder and the reflex-vertex count) and **"The default"** (why
  the phase ships off and what flips it).
- `src/Engine/Mom/RooftopSupport.cs`'s own header, then `Build` and `Extent`. **This is the file that
  decides the phase** and §1 is about nothing else. Its finding — that `ξ/Area` is exactly the wrong
  weight on a cut cell — is what makes the support a list of STRIPS, and the strips are where the real
  constraint lives.
- `src/Engine/Mom/PolygonIntegrals.cs`'s derivation header, specifically the sentence beginning *"It is
  EXACT for any simple polygon…"*.
- `src/Engine/Mom/SurfaceMesher.cs` **lines ~693–726** — the three per-cell refusals, and the
  `IsConvex` test at ~717 that this brief exists to interrogate.

Nothing in this brief is repeated from those.

---

## §0 — the prize, and it is already measured

| | measured | source |
|---|---|---|
| MKlopf on-axis, **PCB**, area error: staircase → conformal | 0.593% → **0.766% — WORSE** | conformal §"THE LIMITATION" |
| MKlopf Offset, PCB | 0.540% → **0.750% — worse** | ditto |
| MKlopf on-axis / Offset, MMIC | 0.278% → 0.189%, 0.334% → 0.084% (better, **not exact**) | ditto |
| MBend and MTaper, both starters | 0.10–0.47% → **1e-15 (exact)** | ditto |
| MKlopf fallback cells at cells/λ 20 / 40 / 80 / 160 / 320 | 52 / 78 / **126 / 126 / 126** | `G1b` |
| MKlopf's outline: vertices, of which REFLEX | 194, **126** | `G1c` |

**Two facts, and the second is what forces the phase.** The first is that one shipping library part
comes out *worse* than the staircase it replaced, on the starter a user is most likely to be on. The
second is that **refining cannot fix it**: the fallback count is a count of features of the ARTWORK,
not of cells, so once each reflex vertex owns a cell the count sticks at exactly 126 and R-cut-1's
tiling guarantee never arrives on that part.

**The prize is the default.** The conformal phase's own note says it ships OFF for two reasons and
that *"the second is the real one"* — MKlopf's regression. Bookkeeping aside, **this brief closing is
what makes flipping `PlanarBoundaryCells.Conformal` to the default a defensible act**, and that is the
whole of what it is worth. Everything else about the conformal phase already passes: the fill reaches
1.35e-6 / 2.34e-6 against L8c's own 5.0e-6, tiling is exact at every rung of the disc ladder, and N is
essentially unchanged (745 → 743 on MKlopf, i.e. a conformal mesh is not more expensive).

**And the counter-measurement, so the prize is not overstated.** A Klopfenstein taper's whole value is
a controlled equiripple |Γ| of 0.05 and L8b measured the staircase's LOCAL width error there at
**20.9% worst, 11.2% RMS** — but nobody has yet measured what the conformal mesh does to *that*
quantity, only to the area. **If M0 or M3 finds the local width error already fixed by the 74 cells
that DO cut, the residual prize is smaller than the area table suggests, and saying so is a legitimate
outcome.** Measure it; do not assume the area error is a proxy for it.

---

## §1 — THE PREMISE, AND WHY IT IS PROBABLY WRONG

Three facts, each read directly out of the shipping code rather than inferred.

**1. The fill does not need convexity, and says so.** `PolygonIntegrals`' derivation reduces the area
integral to a sum over EDGES via the signed fan from the observation point, and its own header states
the scope: *"It is EXACT for any simple polygon: the signed fan from the observation point covers the
interior once with sign +1 and everything outside an even number of times with cancelling signs, so it
needs neither convexity nor the observation point being inside."* All six closed forms are already
general. **M3 of the conformal phase is not a constraint on this one.**

**2. A non-convex region ALREADY ships, and already works.** R-cut-3's sliver merge absorbs a sliver
into the neighbour it shares its largest face with, producing *"one L-shaped or trapezoidal cell"* — an
L-shape is not convex. `RooftopSupport.Build` iterates `cell.Region.Pieces` (plural) and `Extent`
unions across them. So the question is **not** "can the machinery carry a non-convex cell": it
demonstrably can, and does, in every mesh that merges a sliver.

**3. The actual constraint is in `Extent`, and it is not convexity.** `Extent(region, alongX, t, tol)`
returns the min and max of *every* crossing of the line at transverse coordinate `t` — i.e. the outer
hull of the crossing set. `Build` then makes one trapezoid strip spanning `lo`…`hi`. **If the region
meets that line in more than one interval, the strip spans the gap and integrates source over metal
that is not there** — which is precisely the sin route (c) was refused for in the conformal brief's §3.

So the property the fill actually requires is:

> **FLOW-SIMPLICITY. At every transverse coordinate, the region's intersection with that line is a
> single interval.**

**Convexity implies flow-simplicity in both directions; flow-simplicity is much weaker, and it is
per-direction.** A merged L-shaped cell is flow-simple in both (its two pieces share a face, so the
union at any line is connected) — which is exactly why merging works. And a region can perfectly well
be x-simple and not y-simple.

**Therefore `IsConvex` at `SurfaceMesher.cs:717` is a SUFFICIENT test being used as a necessary one,
and it is applied to the whole CELL for both directions at once.** Meanwhile the per-direction,
per-basis refusal machinery **already exists** — `RooftopSupport.Anchored` and `SharedFaceLength` are
computed per direction and R-cut-4 already refuses individual bases on them, in the mesher, with the
count asserted.

**So there are two routes, and this brief's whole job is to measure which one the artwork needs:**

- **Route A — replace the predicate.** Keep the clipped region as ONE piece, non-convex and all, and
  refuse per direction on flow-simplicity instead of refusing the cell on convexity. If MKlopf's 126
  are flow-simple, this is a predicate swap and a per-direction refusal, and **no decomposition is
  built at all**.
- **Route B — decompose.** For a region that is genuinely not flow-simple in a direction, split it into
  pieces that are. Note this is **not** "decompose into convex pieces" either: the target property is
  flow-simplicity, and a decomposition into monotone (y-monotone / trapezoidal) pieces is the standard
  and cheaper one.

**D3's standing rule applies: MEASURE, then choose, then record the measurement.** Do not build Route B
because this brief's title names it.

---

## §2 — M0: CLASSIFY THE 126, and build nothing until it reports

**This is the milestone that decides the phase and it is a measurement, not an implementation.**
Instrument the existing refusal site to classify every cell it currently rejects, and report the table.
No production behaviour changes in M0.

For each cell that fails `IsConvex` today, record:

- **is it x-simple?** (every horizontal line meets the region in one interval)
- **is it y-simple?**
- **how many reflex vertices does the clipped region have?** (1 is the case §1 expects; more is a
  different animal)
- **what is its area as a fraction of its grid rectangle?** — a fallback that is nearly the whole
  rectangle costs almost nothing today, and one that is a thin wedge costs everything.

**Report it for all three shipping PCells on both starters, at cells/λ 20 / 80 / 320**, plus the
96-point disc as the control (which should produce **zero** non-convex fallbacks at any density — its
rim is convex everywhere, and if it does not, that is a finding that outranks the rest of this brief).

**The three outcomes, and each has a different phase attached:**

| M0 says | then |
|---|---|
| the 126 are flow-simple in **both** directions | Route A alone, and it is a predicate swap. **Most likely, and §1 says why** |
| flow-simple in **one** direction | Route A plus a per-direction basis refusal. The cell contributes an X basis and not a Y one — which the machinery already expresses |
| **not** flow-simple in either | Route B is genuinely needed. Scope it then, on the measured count |

**A prediction, recorded so it can be wrong.** A taper's rim is a graph over its own axis, so a cell it
crosses should meet every transverse line once, and the reflex vertex should come from the rim's
curvature rather than from the metal doubling back. **That predicts outcome 1 for most of the 126.** It
is a guess, it is flagged as one, and M0 exists to refute it. **If it is refuted the phase is bigger
than this brief assumes and the honest thing is to stop and re-scope**, not to press on into Route B
with a brief written for Route A.

---

## §3 — M1: the predicate (Route A)

Only if M0 says so.

**R-cvx-1 — the test is flow-simplicity, computed where the strips are.** The natural place is
`RooftopSupport.Build` itself: it already walks the breakpoints and already calls `Extent` at each. Have
`Extent` return whether the crossing set at that coordinate was a single interval, and surface the
conjunction as a new required member beside `Anchored`. **Do not write a separate geometric predicate
over the ring** — a second implementation of "is this region simple in x" that must agree with what
`Extent` actually does is exactly the two-code-paths-that-must-agree trap L7b-b's D1 records.

**R-cvx-2 — the refusal moves from the CELL to the BASIS, and the counts must distinguish them.**
`FallbackNonConvex` currently means "this cell is staircased". After M1 a cell may be cut and still
refuse one direction's basis. Those are different events and a user reading the notes needs them
separate:
- cells staircased because no direction is usable (the old meaning, and it should shrink toward zero);
- bases refused on flow-simplicity in one direction, with the cell still cut.

Report both. **A mesher that silently re-shapes cells is worse than one that says it did**, and the
same is true of one that silently drops a basis.

**R-cvx-3 — R-cut-1's tiling gate is what proves the change, and it is unforgiving.** The area error on
MKlopf must go from 0.766% to round-off on the PCB starter. It is measured against the DRAWN artwork,
not against an ideal curve — the conformal phase's own trap, where measuring against a true disc
reported a flat 7.138e-4 that was the 96-gon's own deficit. **Measure against the drawn outline.**

**R-cvx-4 — Manhattan stays BIT-IDENTICAL, and it is asserted on gridlines, cells and bases as
equalities.** §10.7's FR-4 hero must still measure **N = 552** exactly. A Manhattan polygon has no
oblique edge, so nothing here can reach it — assert it anyway, as R-cut-2 does.

**R-cvx-5 — the sliver merge must survive the predicate change.** A merged cell is non-convex today and
passes because it is never asked; after M1 it will be asked. **It must answer "flow-simple", and if the
new predicate refuses a merged cell the predicate is wrong**, because those cells demonstrably fill
correctly today. This is the strongest available regression check on the new test and it costs nothing
— `ConformalSliverTests` already builds meshes full of merged cells.

---

## §4 — M2: decomposition (Route B), and only for what M0 measured

**Do not build this unless M0 says the residue is non-empty.** If it is:

**R-cvx-6 — the target is monotone pieces, not convex ones.** §1's requirement is flow-simplicity, and
monotone (trapezoidal) decomposition is the standard, cheaper and more directly-aimed construction.
Convex decomposition is a stronger property than needed, and the stronger property costs more pieces.
**If convex pieces are built anyway, say why in the code** — "the representation is called
`PlanarCellRegion.Pieces` and the field is documented as convex" is a legitimate reason, and it should
be written down rather than assumed.

**R-cvx-7 — the pieces are pieces of ONE cell, and N must not move.** This is the property that makes
the route affordable and it is the one to assert: a decomposed cell remains **one cell** with one basis
pairing, so decomposition multiplies work inside the quadrature loop and **not** the matrix dimension.
Gate N as an equality against the count before decomposition. If N moves, the change has become a
subdivision — which grows the unknown count and breaks the rooftop pairing L8c's fill and L9c's via
basis both assume — and that is a different, much larger phase.

**R-cvx-8 — decomposition must not lose or double-count area.** `Σ pieces' areas == the clipped
region's area` as an equality, and R-cut-1's tiling gate downstream of it. `G5b` already asserts the
analogous property for the merge ("merging never loses area at any threshold") and is the model.

**R-cvx-9 — a decomposition that produces a SLIVER piece has re-created R-cut-3 one level down.** The
merge absorbs sliver *cells*; nothing absorbs sliver *pieces*. A piece with negligible area contributes
a strip whose trapezoid is degenerate. Decide it — drop pieces below a measured area fraction, or
refuse — and **measure the threshold the way `ConformalSliverTests` measured the cell one**, rather
than picking a number. Note that finding: the cell-level sweep found the conditioning harm *far*
smaller than R-cut-3's rationale claimed, so do not inherit the rationale, only the method.

---

## §5 — what else is keyed to convexity

Enumerated rather than left to discovery. Each is small; each is a silent-wrong-answer if missed.

- **`PlanarCellRegion.IsConvex`** — keep it (it is a correct predicate, well tested) but audit every
  caller. After M1 it should have none in the mesher's hot path.
- **`PlanarCellRegion.Contains`** — check whether its implementation assumes convexity. If it does, it
  is on the `Merged` path today and is therefore already reachable with a non-convex region; either way
  the answer belongs in one place with a test.
- **`PlanarCellRegion.CentroidX/Y` and `Area`** — signed-area formulas are exact for any simple
  polygon, and the file already translates to a local origin to kill cancellation. Assert on a
  non-convex ring; do not re-derive.
- **`RooftopSupport.MetalTiles`** — the divergence pulse's domain, built from the same strips with a
  unit weight. It inherits whatever `Build` does and needs no separate change, but its tiling **is**
  what `∫∇·f dS = ±1` is integrated over, so it inherits the gate too.
- **`PlanarCurrentDensity`** — the transverse extent on a cut cell is `Area / Width`, not `Height`. A
  decomposed or newly-admitted cell must still report a sane extent; the heat map is wrong exactly on
  the rim otherwise, which is the part anyone looks at.
- **`LayoutRenderer.PlanarMesh.cs`** — draws the region as an `SKPath`. A non-convex or multi-piece
  region must draw as what it is. **The overlay is the only place a user can SEE that the mesh followed
  the metal**, so this is the feature's own evidence, not cosmetics.
- **The mesh NOTES** — R-cvx-2's two counts, and `StaircasedPolygons` must stop claiming a staircase
  for a part that no longer has one.

---

## §6 — deliberately deferred, with what would reopen each

- **The other two per-cell refusals** — `FallbackMultiPolygon` (two drawn shapes touching one cell) and
  `FallbackHole`. **Both are genuinely different problems**: they need more than one basis per cell,
  which is a much larger change. They also **do** clear under refinement, which is exactly why they
  matter less than the reflex-vertex case that does not. Reopener: a part where they saturate too.
- **Turning `PlanarRimGrading` back on.** Still open, still measured as a negative *because the rim was
  in the wrong place*, and the conformal brief's §6 already says to re-take it after the rim is right.
  It is not this brief's, and doing it here would confound two changes in one measurement.
- **RWG / triangles.** §1 of the conformal brief, unchanged. Note that RooftopSupport's own header
  already identifies the one thing that needs it: pointwise continuity across a shared face does not
  survive a cut and cannot, since making it exact needs a basis whose current is not purely x̂.
- **Flipping the default to `Conformal`.** It is what this brief unlocks and it is **not** part of it.
  It moves every number a user has previously recorded, so it gets its own deliberate act and its own
  line in the CLAUDE.md — the conformal phase's note is explicit about that.

---

## §7 — gates

1. **M0's table** — the 126 classified by x-simplicity, y-simplicity, reflex count and area fraction,
   for three PCells × two starters × three densities, with the disc as a zero-fallback control.
   **This is the deliverable even if the answer stops the phase.**
2. **MKlopf's area error at round-off** on both starters, replacing 0.766% / 0.750% (PCB) and 0.189% /
   0.084% (MMIC) — measured against the DRAWN artwork.
3. **The fallback count goes to zero on MKlopf**, and the saturation ladder (cells/λ 20 … 320) is
   re-run to show it — the plateau at 126 is the signature this phase exists to remove, so its absence
   is the proof.
4. **Manhattan bit-identical** — gridlines, cells, bases as equalities; §10.7's hero still N = 552;
   L8c's fill, L8d's de-embedded S and L9's via answers all unmoved.
5. **N unchanged on every part** (R-cvx-7), as an equality against today's conformal counts:
   547 / 704 / 745 / 579 on the PCB starter.
6. **The rooftop's three properties still hold as EQUALITIES** on a newly-admitted cell —
   `∇·f = ±1/Area`, `∫∇·f dS = 0` to machine precision, `f = 0` on the whole outer boundary including
   the rim — and the shared-face current is still exactly 1 A.
7. **The fill against the independent 4-D quadrature oracle on a mesh containing the newly-admitted
   cells**, reported beside the conformal phase's own 1.35e-6 / 2.34e-6. **Do not gate tighter than
   5e-6** — that is L8c's own benchmark, and the oracle's own residual on the pulse self term is
   3.3e-7, so a 1e-6 gate asks it for a decision it cannot make. That mistake was made once already.
8. **The sliver merge still passes the new predicate** (R-cvx-5).
9. **`dotnet test` green with the routine `Engine.Tests` tier not growing a sweep.** It is at 1,119
   tests / 4 m 46 s taken alone and is already well over its ~60 s ceiling; every new ladder is
   `Category=Benchmark`. For scale, the conformal phase's own routine share is 37 tests in 2 s, and its
   opt-in share is ~25 min — **this phase should be far smaller on both counts, and if it is not, that
   is worth reporting.**

---

## §8 — if something here turns out to be wrong

Report it. **Two things in this brief are guesses rather than measurements and are flagged as such**:
§2's prediction that most of MKlopf's 126 are flow-simple in both directions, and §0's implication that
the area error is a proxy for the local width error that actually matters on a Klopfenstein taper.
Either could be wrong and each has a different consequence — the first re-scopes the phase, the second
shrinks the prize.

**A measured deferral is a legitimate deliverable in this area and there are five on the record**
(L7b-b's Route B, L9c's amplitude cap, L9e's ACA, the edge-mesh brief, and the ground-via chain that
was measured and not built). **If M0 says the residue needs a real decomposition and the measured cost
does not earn it, that result plus its number is worth more than a shipped default nobody can
justify** — and MKlopf staying a partial case, stated plainly, is what the conformal phase already
does today.
