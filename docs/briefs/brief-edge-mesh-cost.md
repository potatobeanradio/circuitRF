# Brief EFAN — the edge fan's finest cell is unrelated to the mesh the user asked for, so every control that coarsens the mesh makes the edge mesh relatively more expensive

**Tags:** `R-efan-n` · **Area:** `src/Engine/Mom`

**Read first**, end to end, before planning anything: `src/Engine/Mom/RESOLVED.md` §"Why the edge
mesh is unaffordable on a MMIC spiral" (2026-09-15) — this brief is that investigation's action half
and every number below is measured there; `PlanarEdgeReference`'s own three-value doc comment in
`SurfaceMesher.cs`, because the 2026-09-09 `LocalConductorWidth` change is the closest prior art and
its **one-way coarsening invariant is the pattern this brief must copy exactly**; `GradedAttractor`'s
doc comment, which is ANT-2's per-attractor growth rate and the other half of the same size field;
`PlanarMeshSettings`' header on `MinCellsAcrossConductor` (the owner instruction that made it a
control, and the non-monotonicity that turned out to be a marcher defect) and on `CurrentModel` (the
"a note about a dead control is not a working control" paragraph — this brief is the same complaint
about a different control); and
`tests/Engine.Tests/Mom/SurfaceMesherEdgeCellsTests.RaisingEdgeCells_DoesNotSharpenTheFinestCell_ItWidensTheGradedBand`,
which **already gates the behaviour described below as intended** and must be read before anything is
called a bug.

---

## The problem

**On a part whose metal is narrow relative to its own extent, the edge mesh costs 3.6× the unknowns
at the default and 45× at the coarsest setting — and the only user control over it is off.**

Owner report, 2026-09-15, on the `KIT_SPIRAL` cell in the PDK PCells example: the default mesh has
far too many cells; edge mesh has to be switched off to get the count down; and the extra cells
appear "in seemingly random locations". Measured in a scratch harness on the shipped coil — 3 turns,
10 µm metal, 8 µm space, 120 µm opening, 270 × 250 µm envelope, Metal1 + Metal2 over 100 µm GaAs,
20 GHz, the artwork exactly as it now ships:

| `MinCellsAcrossConductor` | edge mesh ON | edge mesh OFF | ratio | bulk ÷ c₀ |
|---|---|---|---|---|
| 4 (the default) | **23,195** | 6,456 | 3.59× | 8.5× |
| 3 | 17,001 | 3,456 | 4.92× | 11.3× |
| 2 | 12,160 | 1,380 | **8.81×** | 17× |
| 1 | 10,392 | 228 | **45.6×** | 34.6× |

The dense ceiling is 5,000 unknowns, and this problem is two conductor levels plus a via
(`RequiresGeneralKernel`), so **the accelerator's 12,000 is not available and the 5,000 does not
move.** The default is 4.6× past it.

### Read the table down the right-hand column, not the left

**The edge mesh's cost is not a constant overhead — it grows as the user coarsens the mesh.** That
is the defect, and the mechanism is in one line:

```
c0 = PlanarMeshSettings.EdgeFractionOfReference * edgeRef      // 3% of the LOCAL conductor width
h  = min(λ_g / CellsPerWavelength, W / MinCellsAcrossConductor) // the bulk pitch
```

`c₀` is a fraction of the METAL. `h` is a fraction of the metal **divided by a control**. So the
climb the fan has to make, `h / c₀ = 1 / (0.03 · MinCellsAcrossConductor)` on any geometry-limited
mesh, is **8.3 / 11.1 / 16.7 / 33.3** at 4 / 3 / 2 / 1 across — measured as 8.5 / 11.3 / 17 / 34.6,
which is that formula. A longer climb is more graded cells, and a tensor product turns every one of
them into a gridline across the whole part.

**So `MinCellsAcrossConductor` barely works while the edge mesh is on.** Taking it 4 → 1 falls
23,195 → 10,392 with the edge mesh on, a factor of 2.2; with it off the same change falls
6,456 → 228, a factor of **28**. The control the owner deliberately opened up in 2026-09-09 —
*"the user is responsible for mesh density, and a tool that will not give them a bad mesh when they
ask for one is deciding on their behalf"* — is 13× weaker than it reads, and nothing says so.

The mesher already knows and already reports the outcome, honestly, in its own notes:

```
Narrowest conductor dimension 10 µm, meshed 9 cell(s) across (target 4).
```

**Nine, where four were asked for.** At `MinCellsAcrossConductor = 2` it is seven, where two were
asked for — 3.5×. **This is the whole defect in one line of the run's own output**, and it has been
there the whole time.

### The reporting is not the problem. It is already very good

Do not plan any work on the messages before reading what the run actually says. On this part the
refusal names the mechanism exactly, names the λ knobs as inert, and names four remedies including
the two that work:

> The cell size is set by the NARROWEST metal, not by wavelength: the narrowest conductor run is
> 10 µm, and meshing it 4 cells across forces a 2.5 µm pitch over all 270 µm × 250 µm of the artwork
> — the grid is one tensor product over the whole layout, so the narrow end is paid for everywhere.
> … LOWERING CELLS PER WAVELENGTH OR MESH FREQUENCY WILL NOT REDUCE THIS COUNT. … What acts on the
> count here: narrow the range of widths in the analysed region, coarsen the Detail floor — a SMALLER
> divisor — …, turn the edge mesh off or analyse a smaller region.

and the notes are equally straight, including about a control that did nothing:

> Detail floor 5 µm — λ_g/200 would be 20.87 µm, but this artwork is only 250 µm across, so the
> floor is held at 2% of that: nothing on this artwork is narrower than that, so it changed nothing
> here.
>
> Longest edge fan: 3 cell(s) from its finest cell to the bulk pitch — each one is a gridline across
> the whole part, so this is what the edge mesh costs.

**The detail floor is correctly inert here and correctly says so** — `CapMinFractionOfExtent` holds
it at 2 % of the smallest extent, 5 µm on a 250 µm coil, under the 10 µm trace. There is no import
detail on a drawn spiral to floor. **Nothing about the detail floor is in scope for this brief**, and
a reader arriving from the RESOLVED.md note should take "the floor is dead on MMIC parts" as a
correct behaviour that is correctly explained, not as a second defect.

The one gap in the refusal is M3, and it is one sentence: it *describes* "meshing it 4 cells across"
and never says **that number is a setting, and lowering it is the single largest lever on this part**.
Memory of `em-refusal-must-name-a-binding-remedy` applies — a remedy list that omits the binding
constraint sends the user round the inert ones.

### `EdgeCells` 1 and 2 are the same mesh, and that is NOT the bug

Measured: 23,195 / 18,861 / 18,861 / 6,456 unknowns at `EdgeCells` 3 / 2 / 1 / 0. **Only 0 does
anything, and 0 is `EdgeMesh = false` spelt differently — 6,456 either way, bit for bit.**

The reason is `GrowthRatioFor`'s clamp: `r = clamp((h/c₀)^(1/n), MinGrowthRatio, MaxGrowthRatio)`
with `MaxGrowthRatio = 3`. A climb of 8.5 cannot be made in one cell (needs r = 8.5) or in two
(needs 2.92, just under the clamp but realised the same after the partition rescales), so both take
two graded cells. **`EdgeCells` is a FLOOR on the fan's length, not a cap on it**, and
`SurfaceMesherEdgeCellsTests` already gates exactly this, and `AnUnhonourableEdgeCellCount_IsReportedInTheMeshNotes`
already reports it. **Do not "fix" it by raising `MaxGrowthRatio`** — see Must NOT. It is in this
brief only because flooring c₀ shortens the climb and therefore changes what `EdgeCells` can
express, which has to be re-measured and re-worded rather than left to drift.

### The "random locations" are the tensor product, and they are out of scope

One grid is shared by every layer (D8), so an x-attractor refines a column over the part's full
height. Measured:

```
edge ON : 188 x-lines × 160 y-lines = 29,733 rectangles, 12,294 on metal, N = 23,195
edge OFF: 113 × 105                 = 11,648 rectangles,  3,696 on metal, N =  6,456
x lines: -155 -154.7 -154.1 -152.88 -150.41 -147.93 -145.46 -142.99 -140.51 -138.04 -136.46 -135.68 -135.3 -135 …
```

A spiral puts metal edges at ~14 distinct x and ~14 distinct y. The fan belonging to a VERTICAL
turn's edge lands in the middle of every HORIZONTAL run, refining it along the axis where nothing
varies — that is what the user sees as fine cells where there is no edge. `PlanarEdgeReference`'s own
doc already states this and states why it cannot be localised: *"an x-attractor refines a COLUMN over
the full height of the grid — that is what a tensor product is … localising it in the other axis
would need T-junctions, which the rooftop basis and `RectangleIntegrals`' closed forms do not
admit."* **This brief makes each fan SHORTER. It does not make it narrower, and nothing in it should
try.**

---

## Milestones

### M1 — what the edge fan actually buys, as a function of `h / c₀`. MEASUREMENT ONLY, and it decides M2's constant.

Everything below hangs on one number that has never been measured: **how fine does the outermost edge
cell have to be, relative to the bulk pitch, before further refinement stops changing the answer.**
3 % of the conductor width is §10.5's own figure for the edge cell and it is not in dispute *as a
fraction of a width*; what is in dispute is that it is stated with no reference to the mesh it sits
in, so on a trace meshed 4 cells across it asks for a cell 8.5× finer than the bulk and on one meshed
1 across it asks for 34.6× finer.

**The instrument already exists — copy `MeshFrequencyAccuracyTests`' shape**, which solves a reference
setting and reports worst |ΔS| per band against it, on the FR-4 and GaAs heroes both. Here the swept
quantity is `h / c₀` rather than the mesh frequency.

Sweep the ratio over, at least, `off · 34 · 17 · 11 · 8.5 · 6 · 4 · 3 · 2 · 1 (= no fan)` and report,
per fixture: unknowns, wall time, and worst |ΔS| against the finest rung. Fixtures:

1. **A uniform line, where the oracle does not come from this kernel at all.** The edge singularity is
   what sets Z_c, and kernel A's cross-section extraction answers Z_c and ε_eff for the same line
   exactly. Report both against it, not only |ΔS| against the finest mesh — a self-comparison can
   only show convergence, not correctness, and the whole question is whether the fine rungs are
   buying accuracy or buying agreement with themselves.
2. **The FR-4 hero and the GaAs hero**, at `MinCellsAcrossConductor` 4 and 2, because the ratio's
   dependence on that control is the defect and a constant chosen at one value proves nothing.
3. **The reported coil**, at whatever settings get it under the ceiling, so the brief's own motivating
   case is in the table.

Then answer, in this order:

1. **Where does the curve flatten?** That number, rounded down, is M2's `MaxEdgeRefinement`.
2. **Is it the same on both stacks and at both `MinCellsAcross` values?** If it is not, the floor must
   be expressed in whatever variable does make it constant, and M2's shape changes.
3. **Does 8.5 — today's value at the default — already sit on the flat part?** If it does, M2 is
   nearly a no-op at the default and bites only where the mesh is coarse, which is the best possible
   outcome and should be stated as such.

**Stop and report if M1 shows the answer still moving at 34×.** That would mean the edge fan is
under-resolved rather than over-resolved on this class of part, the table in §The problem is the
price of a correct answer, and every milestone below is the wrong shape — the brief would become one
about the ceiling, not about the fan.

### M2 — floor `c₀` against the bulk pitch, on `LocalConductorWidth`'s own invariant

```
c0_i = max( EdgeFractionOfReference * edgeRef_i ,  bulkAt(i) / MaxEdgeRefinement )
```

per attractor, where `bulkAt(i)` is **the same bulk the attractor's own growth rate already climbs
to** — ANT-2's `GradedAttractor` note says which number that is and warns against using the pitch
field's value at the attractor's coordinate, and that warning applies here unchanged.

**The invariant is not optional and is the reason this is safe: it may only COARSEN.** `c₀` is raised
and never lowered, `h(x) = min_i [c₀_i + g·|x − a_i|]` is monotone non-decreasing in `c₀`, so the size
field is pointwise ≥ today's and the cell count is bounded above by today's. That is word for word
the argument `PlanarEdgeReference.LocalConductorWidth` and `PlanarMeshPitchField` already carry, and
`LocalEdgeReferenceTests.TheLocalReference_NeverProducesAFinerCell_NorMoreOfThem_OnAnyFixture` is the
test to extend rather than the test to copy.

Three decisions the implementer must not re-open on their own:

- **`MaxEdgeRefinement` is a measured constant, not a tenth user control.** D3's test is that a
  control earns its place by being a modelling or responsibility decision; this is a resolution, and
  resolutions are derived. `MinCellsAcrossConductor` is already the control for "how finely do I want
  this metal meshed", and the whole point of M2 is that the edge fan should OBEY it rather than
  ignore it. Put the number beside `EdgeFractionOfReference` and `EdgeGrowthRatio` as a `const` with
  M1's table quoted at it, exactly as `DefaultDetailFloorDivisor` carries its own.
- **It survives `Auto`** — or rather, it is not a setting at all, so the taxonomy question does not
  arise. Do not add it to `PlanarMeshSettings`.
- **`PlanarEdgeReference.CellSize` is NOT this, and must not be revived as this.** It sets
  `c₀ = 3 % of the cell`, which on this part is 0.075 µm — four times FINER than today. It is a
  measured-and-rejected alternative reference, not a floor, and the two do opposite things.

Report, against today, for every fixture in `DetailFloorTests.Fixtures()` and `SheetMeshTests`' own:
unknowns before and after, and how many are bit-identical. **A fixture whose count RISES is a failed
invariant, not a tuning question.**

### M3 — the refusal offers the control it already describes

One sentence, and it must ship whatever M1 and M2 conclude.

The refusal already says *"meshing it 4 cells across forces a 2.5 µm pitch"*. It must also say that
**Cells across a conductor is a setting**, what lowering it would give, and — separately — that the
edge fan is currently delivering more cells across the metal than were asked for. Both numbers are
already computed: the note `Narrowest conductor dimension 10 µm, meshed 9 cell(s) across (target 4)`
has them. **No second mesh, no estimate.**

The wording must survive M2: after the floor lands, "9 across where 4 were asked" becomes a smaller
number, and a sentence that only makes sense when the gap is large is a sentence that will read as
noise later.

### M4 — the reported part, end to end

`KIT_SPIRAL` at its shipped defaults on the GaAs starter technology, 20 GHz, two levels. Report the
unknown count at the default mesh, at `MinCellsAcrossConductor` 3 and 2, and with the
transmission-line mesh on, **before and after M2**. State plainly whether the default is under the
ceiling. **If it is not, that is a reportable result and not a failure** — the honest deliverable is
that the controls now do what they say, not that every part fits.

Also report what the `Sheet` and `TransmissionLine` modes compose to: both act on the ALONG pitch and
M2 acts on the ACROSS fan, so they should multiply. Measured today: TL + edge off + across 2 is 846
unknowns against 23,195 at the default, so the composition is already worth ~27× and M2 should make
the middle of that range reachable without turning the edge mesh off at all.

---

## Gates

- **`LocalEdgeReferenceTests`' monotonicity gate, extended to the floor**: on every fixture, at every
  `MinCellsAcrossConductor` in 1…8, the new field produces no finer cell and no more of them than
  today's. This is the primary gate and it is structural, not statistical.
- **`MinCellsAcrossConductor` must become monotone in its EFFECT with the edge mesh ON.** Today
  4 → 1 buys 2.2× with the fan on and 28× with it off; after M2 the two must be within a stated
  factor of each other. `MeshGradingTests.LoweringCellsAcrossTheConductor_NeverRaisesTheCellCount`
  is the existing shape — this adds the strength of the effect, not only its sign.
- **The L8/L9 acceptance set is unchanged in ANSWER, and any change in COUNT is reported per
  fixture.** Bit-identity is the goal at the default and is not a requirement: M1 decides whether the
  default sits on the flat part. Every fixture whose mesh moves gets its |ΔS| reported against the
  pre-change mesh, and anything past `PlanarFillAccuracy`'s own 5.0e-6 yardstick is argued for
  explicitly or is a failure.
- **`SurfaceMesherEdgeCellsTests` passes, with `RaisingEdgeCells_…` re-measured rather than deleted.**
  Its 0.9–1.1 band is a statement about today's climb; after M2 the climb is shorter and the band may
  legitimately move. A changed assertion with a recorded reason is fine; a removed one is not.
- **`DetailFloorTests`, `SheetMeshTests`, `TransmissionLineMeshTests`, `MeshGradingTests`,
  `EdgeAttractorCapTests`, `SurfaceMesherConformalTests` all pass unchanged.**
- No new `Category=Benchmark` test unless a measured one crosses ~5 s. M1's sweep is a measurement
  taken once in a scratch harness and reported in `RESOLVED.md`; it does not become a test.
  (Memory: *no new timing benchmark tests* — assert the structural property, which here is the
  monotonicity gate above, not a wall clock.)

## Must NOT

- **Do not try to localise a fan to the metal it belongs to.** It is the obvious move and it is
  blocked by the rooftop basis, not by effort: a pair of basis functions shares a cell EDGE, and a
  T-junction has no such edge. `PlanarEdgeReference`'s own doc says so. Anyone reaching for it has
  rediscovered the tensor product.
- **Do not raise `MaxGrowthRatio` to make `EdgeCells` 1 reachable.** The clamp exists because the
  size field has to stay Lipschitz — `BuildGridLines`' own note records that a discontinuous field
  let the same rectangle moved 3.7 mm change the mesh by 33 %. `EdgeCells` is a floor on the fan,
  it is already reported as one, and the fix is wording (M3 and its tooltip), not arithmetic.
- **Do not change `EdgeFractionOfReference` from 3 %.** It is §10.5's figure for what an edge cell
  is as a fraction of a WIDTH and this brief does not dispute it; the floor is a second constraint
  beside it, so on a fine mesh the 3 % still governs and nothing that works today changes.
- **Do not turn the edge mesh off by default**, or weaken it on a fixture to make a number. The
  1/√d edge singularity is real physics and ANT-3's own note about a patch rim — *"the temptation to
  skip it is the trap"* — is about exactly this reflex.
- **Do not touch the detail floor or `CapMinFractionOfExtent`.** Correct behaviour, correctly
  explained, out of scope. Say so in `RESOLVED.md` so the next person does not re-derive it.
- **Do not add a tenth `PlanarMeshSettings` control.** If M1's constant turns out not to be constant,
  come back with the measurement rather than exposing the knob.
- Do not touch `src/Core`, `src/Design`, `src/Render`, `src/Ui`, `src/Cli` or `RfCore`. This is
  `src/Engine/Mom` and its tests only. The `.cem` format does not change and no panel gains a field.
- Anonymise anything derived from a user's file before it is committed — no workspace names, no
  paths. The coil in §The problem is the shipped example and carries none.

## Cost

M1 is most of the value and is measurement only; if it finds the default already on the flat part,
M2 is a two-line change guarded by an invariant that is already written down twice in this directory.
M3 is one sentence built from numbers already in hand and **must ship even if M1 and M2 conclude that
nothing should change** — a user who coarsens the mesh and gets 2.2× where the control promises 28×
is entitled to be told which setting is eating it. M4 is a report, not a target.

The sequencing matters: **do not start M2 before M1 has said what the constant is.** The floor is
trivial to write and impossible to defend without the number, and a constant chosen by eye here would
be a silent accuracy change across every planar result in the repository.

## On completion

- `src/Engine/Mom/RESOLVED.md` gains a `§EFAN` section: M1's full sweep table on all three fixtures
  with the kernel-A comparison, the chosen `MaxEdgeRefinement` and why, M2's before/after counts per
  fixture including the ones that did not move, M4's end-to-end numbers on the coil, and every trap
  found.
- The existing §"Why the edge mesh is unaffordable on a MMIC spiral" section is **updated, not left
  standing** — its closing "If this is picked up" paragraph proposes exactly this floor and must
  either be replaced by what was measured or marked as superseded.
- `PlanarEdgeReference`'s and `PlanarMeshSettings`' doc comments carry the new constant and M1's
  number, at the constant itself, the way `DefaultDetailFloorDivisor` carries its convergence table.
- The Mesh settings tooltip for **Cells across a conductor** says what it now genuinely controls, and
  the **Edge cells** tooltip says that it is a floor on the fan's length rather than a control over
  its fineness — which is true today and undocumented today.
- **Do not write to any `CLAUDE.md`.** Report stale lines to the owner by file and line instead — in
  particular `src/Engine/Mom/CLAUDE.md`'s mesh-control list if it states the edge cell's reference.
