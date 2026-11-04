# Brief EM-TFR — a resistive film on a metal level, in the planar EM solve, verified in 3D

## The gap

A thin-film resistor cannot be simulated in the planar (2.5D) MoM, and nothing says so.

- **The film is a property of a DRAWING layer, not of the stackup.** `LayerDef.SheetResistanceOhmPerSq`
  (`src/Design/Layout/TechModel.cs:142`) is what the `TFR` circuit model, the `TFR` PCell
  (`TfrPCell`, `src/Design/Layout/PCells/MmicPCells.cs`), `MmicStack` and LVS device recognition all
  read. Its own comment states why it is not a stackup entry: a stackup conductor would add a whole
  new level to every planar run on the technology.
- **The planar extractor therefore never sees it.** `PlanarExtractor` classifies shapes against the
  stackup's `DrawingLayers` bindings; a shape on the resistor layer binds to nothing and is counted
  in `ignoredOther`, whose note talks about zero-width paths and unbound layers. A TFR drawn in a
  layout reaches the kernel as **two Metal1 contacts with nothing between them** — a converged,
  smooth, meaningless open circuit.
- **The one place that noticed refuses rather than models.** `ComponentEmExtraction.cs:72` refuses
  to EM-extract a `TFR` for exactly this reason.
- **Two conductor materials at one elevation cannot be stated at all.** `PlanarExtractor.BuildStack`
  lays the non-via entries out strictly in sequence, one z band each, and one conductor entry's
  drawing layers all share that entry's σ and thickness.
- **There is no editor for the number either.** No view in `src/Ui` reads or writes
  `SheetResistanceOhmPerSq`; the starter GaAs technology sets it in code
  (`src/Ui/Layout/StarterTechnologies.cs:174`). A technology authored in the Technology editor has
  no way to declare a resistor film without hand-editing the `.ctech`.

GitHub discussion #41 asks for this shape on a GaAs process: a 1 µm Metal1 and a 50 nm, 50 Ω/sq
film sharing Metal1's upper surface.

## Why this is one brief and not a new kind of solver

**The kernel already carries the physics.** CL1's surface impedance (`PlanarSurfaceImpedance.cs`)
is `Z_s = (η_c/2)·coth(γ_c t/2)`, and its thin limit is exactly `1/(σt)` — a sheet resistance. A
50 nm film is far thinner than a skin depth at any frequency this kernel runs, so the film IS a
`Z_s` of 50 Ω (real, frequency-flat) on whatever cells it covers. Nothing about the Green's function,
the medium or the levels changes.

**The film's own height does not matter in a sheet model.** Metal1 is already one zero-thickness
sheet at one z (`SheetAt` picks which face); its 1 µm enters only through `Z_s`. A film on the same
level, at the same z, is the correct reduction of "same upper surface" — the 0.95 µm difference
between the film's bottom and Metal1's bottom is not a quantity this formulation has.

**So the change is that `Z_s` becomes a property of a CELL rather than of a LEVEL.** Everything
below is what that costs.

## What to build

### 1. The technology: a film is a drawing layer bound to a conductor level

**A drawing layer that carries `SheetResistanceOhmPerSq` AND appears in a Conductor entry's
`DrawingLayers` is a resistive film ON that level.** No new field. A resistor layer bound to nothing
keeps exactly today's meaning (a mask the circuit model reads), so every existing technology is
unchanged.

- `TechValidation`: a film layer bound to **more than one** conductor entry is an error naming both;
  a film layer bound to a **Via** entry is an error; a film bound to a conductor that is
  `IsGroundReference` is an error (the return plane is a termination, not a member of the fill —
  there is no cell for the film to sit on).
- **Overlap: metal wins.** Where a film shape and an ordinary shape on the same level overlap, the
  cell is metal. Physically the current in an overlap runs in the metal (`Z_s,metal ≪ R_s`), and it
  is what makes the `TFR` PCell come out right: its contacts overlap the film by half a minimum
  feature and their edges are exactly `L` apart, so the film left over is the `L×W` the model counts.
- **The Technology editor gets the field.** The Layers tab gains a *Sheet resistance* column
  (Ω/sq, blank = not a film); the Stackup tab's drawing-layer chips mark a bound film
  (e.g. `Resistor · 50 Ω/sq`). Without this the feature exists only for technologies written in code.
- **A behaviour change to say once:** a technology that today binds a resistor layer into a metal
  entry's `DrawingLayers` gets gold there now and 50 Ω/sq after this brief. That was a shorted
  resistor and is a fix, but a `.snp` stamped before it describes a different structure — see §6.

### 2. The extractor: carry the film as tagged regions on its level

`PlanarExtractor` collects, per conductor level, the film polygons separately from the metal ones
and hands both down. `PlanarConductorLayer` gains a list of **film regions**, each with its sheet
resistance (`PlanarFilmRegion(Polygons, SheetResistanceOhmPerSq)` or similar — a neutral engine
type, R-mom-1). Metal polygons are unchanged; the level's conductor outline for meshing is the union
of the two.

- **An UNBOUND resistor layer drawn in an EM layout gets its own note**, out of `ignoredOther`:
  *"N shape(s) on 'Resistor' (50 Ω/sq) are not in this run: the layer is a resistor film but is not
  bound to a conductor level, so a resistor drawn on it is an OPEN between its contacts. Bind it to
  the level it is deposited on (Technology ▸ Stackup) to simulate it."* This is worth shipping even
  if nothing else in this brief lands, because today's silence is the expensive part.
- `ComponentEmExtraction`'s `TFR` refusal is lifted **when the film layer is bound**, and keeps its
  current wording when it is not.

### 3. The mesher: the film boundary is a hard gridline, and cells carry a class

- **The film/metal boundary contributes hard gridlines** — the mechanism via footprints already use
  (§3.2 of `src/Engine/Mom/CLAUDE.md`: via footprints emit hard gridlines and get no rim grading).
  The same applies here, for the same reason: an INTERNAL film/metal boundary is not a conductor
  edge, carries no charge singularity, and must not earn a graded fan. The film's OUTER edges — where
  it borders no metal — ARE conductor edges and are graded like any other.
- **Each cell gets a sheet class** (index into a per-mesh table of `(level, Z_s source)`): the
  level's metal, or one of its films. Assigned from the cell's own region: a whole rectangle takes
  the class at its centroid; a conformally cut cell whose metal straddles a film boundary falls back
  to the staircase for that cell, the same per-cell fallback §7 already lists for three other
  configurations, and is counted in the mesh report.
- **Rooftop merging must not merge across a class boundary.** "Mesh as Merged Shapes" is ON by
  default (PM-2/PM-3); `PlanarRooftopMerge`'s classification gains one more unconditional rule —
  cells of different class are never merged. Without it a merged cell half on film and half on gold
  takes one `Z_s` and the resistor's length changes with the merge decision.
- **The film sets pitch like metal does**: its width counts toward the narrowest-conductor and local-
  width rules (`MinCellsAcrossConductor`). Its LENGTH needs nothing special — the DC network is exact
  for any cell count on a rectangle (§4), and at AC a resistor is electrically short.

### 4. The fill: `Z_s` per cell, in every place that reads it

`PlanarGram` stores one `Layer` per slot and `PlanarFill.SheetTerm` is `zs[gram.Layer[slot]] *
gram.Value[slot]`. **A rooftop's two cells can now be different classes**, so one class per slot is
not enough. A pair of same-direction bases shares at most two cells (a basis with itself shares both;
neighbours share one), so the slot becomes **two (class, value) contributions**, the second empty on
every slot whose cells agree:

    term(slot) = zs[classA[slot]]·valueA[slot] + zs[classB[slot]]·valueB[slot]

`SheetTable` returns one `Z_s` per CLASS rather than per level; a film class's entry is the real
constant `R_s`. **`SheetTerm` is already the single spelling** that the dense fill, `PlanarEntryFill`
(AIM's near field) and `PlanarFillMerged` all call, and `PlanarCalibration` resolves through
`SheetTable` by name — change it there, once. A mesh with no film produces exactly today's Gram and
today's table, and every number in `HISTORY.md` must stay **bit-identical** on it.

**The film is not "conductor loss" and must not hide behind its switch.** Two places gate the term
on the loss model today:

- `PlanarFill.AddSurfaceImpedance` returns immediately when `Settings.ConductorLoss` is null.
- `PlanarFillSettings.PerfectConductor` (the PEC oracle) makes the METAL perfect.

A film is a circuit element, not a loss correction. Under either condition the film's term is
**still applied** and the metal's is not — otherwise the PEC oracle silently shorts every resistor
in the layout, which is precisely the kind of plausible wrong answer that oracle exists to rule out.
Say so in the run's notes when it happens.

### 5. DC: each half-cell's own sheet resistance

`PlanarDcSolve.Resistance` computes one rooftop's resistance as
`rs × (HalfSquares(ca) + HalfSquares(cb))` with `rs` read from **cell A's level only**. With films,
each half takes its own: `rs(ca)·HalfSquares(ca) + rs(cb)·HalfSquares(cb)`. Same reduction on every
existing mesh. The 0 Hz point is where this feature is easiest to verify — see Gate 1.

### 6. Ports, power budget, provenance

- **A port may not sit on film.** An edge port's cut cells, an internal port's cell and a delta
  gap's cut must be metal; on film the refusal names the port and the film layer. A calibration
  standard is built from the port's own cross-section (`BuildLine`), so with ports on metal no
  standard ever contains film and QSC/PCAL are untouched.
- **The power budget reports the film separately.** `PlanarConductorPower` evaluates
  `½∫Re(Z_s)|J|²` against the same Gram; restricted by class, it yields the film's dissipation as
  its own figure — a `PlanarMetric.PowerFilm` beside `PowerConductor` in `PlanarMetrics`' registry,
  same unit and axis, and the matching line in `PlanarPowerBudget`.
  Folding a resistor's dissipation into `PowerConductor` would make a terminated antenna's
  "conductor loss" read as most of its budget. `PowerDielectricAndGround` is a residual and must
  subtract it.
- **Provenance.** `EmSnpProvenance`'s geometry/mesh hashes include the film regions and their
  `R_s`, so a `.snp` stamped before a film was bound (or whose `R_s` changed) reads as stale. Omitted
  from the hash when a run has no film, so every existing `.snp` stays current.

### 7. Docs and the shipped technology

- `docs/user/src/reference/stackup.md` — a section on resistor films: what binds one, metal-wins,
  the sheet model's height argument in one paragraph, and the unbound-film note.
- `docs/user/src/reference/mom-engine.md` — the per-cell `Z_s`, the port-on-film refusal, the
  `PowerFilm` line.
- The starter GaAs technology (`StarterTechnologies.cs`, `src/Design/resources/technologies/
  mmic-GaAs_2LM_100um.ctech`, `examples/LVS/tech/mmic-GaAs_2LM_100um.ctech` — all three, together)
  binds `Resistor` to `Metal1`. Check first that nothing in the starter's own tests or the LVS
  example depends on the resistor layer being unbound.

### 8. The 3D generator emits the film, and a 3D setup can keep metal thick

The verification below needs the SAME layout solved by Palace, EMerge and openEMS, and today the
layout-to-3D generator drops the film exactly as the planar extractor does. Every backend already
has the boundary a film needs: Palace's `Conductivity` boundary with a `Thickness`
(`PalaceConfigWriter`), openEMS's `ConductingSheet` with `Conductivity` and `Thickness`
(`CsxcadWriter`), and EMerge's thin-conductor boundary (`EmergeLoweringRules`). So this is emission,
not new solver support.

- **A bound film becomes an `Em3dSheet` on its level** (`Em3dLayoutSolids`), clipped where metal
  covers it (metal wins, as in §1). Its material is a generated conductor of σ = 1/(R_s·t). For a
  sheet only the product σ·t is physical, and the notes say so.
- **The film's thickness and face are optional fields on the layer**, used by 3D only:
  `FilmThicknessDbu` (the discussion's 50 nm) and `FilmAt` (`Top`/`Bottom` of the level's band,
  defaulting to the level's own `SheetAt`). The planar solve reads neither, because its sheet is at
  one z regardless. When no thickness is stated, the generator picks one well under a skin depth,
  derives σ from it, and states both values.
- **EMerge's surface-impedance floor is already a refusal** (`SurfaceImpedanceLimitSm` = 1e4 S/m):
  a sheet at or below it carries no boundary at all. 50 Ω/sq at 50 nm is 4e5 S/m, comfortably above.
  A very high-R_s film with a stated thickness can fall below the floor; that case keeps the existing
  refusal, whose sentence should name the film.
- **A 3D setup gains `Conductors: Auto | Sheets | Solids`** (a `.cem` key, omitted at `Auto`).
  Today each band's piece is a sheet or a solid by `SheetRule` (under 3 skin depths AND under 0.1 of
  its width), with no override. `Sheets` forces every conductor to a sheet, which is the planar
  model's own idealisation. `Solids` gives every conductor band its real thickness. **A film stays a
  sheet in all three modes**: a 50 nm volume inside a 100 µm substrate is a mesh nobody should be
  asked to pay for, and the run's note says so.
- **In `Solids` mode the film's sheet sits on the face `FilmAt` names**, at that face's z, and joins
  the clipped metal along the contact edges. That is the discussion's geometry (film top = Metal1
  top) in a model that can see the step.

## Verification against Palace, EMerge and openEMS

This campaign is **run once, and its results are committed as reference data**. That follows the
repo's validation rule: references are generated independently and then committed as fixed data.
None of the three solvers runs in CI. The routine gates read what this campaign wrote.

### The structure

One `.clay` on the starter GaAs technology (100 µm GaAs, εᵣ 12.9; Metal1 3 µm gold, `SheetAt: Top`;
`Resistor` 50 Ω/sq bound to Metal1 by §7):

    50 Ω Metal1 line ── step to contact ── ZIG-ZAG FILM ── contact ── step back ── 50 Ω Metal1 line

- **Feeds:** 50 Ω microstrip (≈ 73 µm on 100 µm GaAs), each at least 3 substrate heights long.
- **Contacts:** narrower Metal1 pads overlapping the film by half a minimum feature (the `TfrPCell`
  rule), so where the metal edges sit decides the film's length.
- **Zig-zag:** three legs joined by four 90° bends, film width ≥ 4 cells at the shipped mesh, about
  **5–8 squares end to end (250–400 Ω)**. That keeps |S₂₁| near −12 to −16 dB, where |ΔS| is still a
  meaningful comparison. **The bends are the point**: a straight strip has a closed form, and a
  corner's current crowding does not. Only a solver that resolves the field gets the corner squares
  right, and the circuit `TFR` model cannot represent them at all.
- **A 2×-thru beside it:** the two feeds joined end to end with no film. Each solver's DUT is
  de-embedded by **that solver's own** thru. The comparison is then of the film section, not of five
  different port models.

### Two routes into 3D, and why both

**Generated from 2D** is the route a user takes: the `.clay` above, through §8, to a `.c3d`. It is
what has to work. It is also code this brief writes, so it can't be the reference that judges the
brief.

**Hand-built** is the route that judges it. It is the same structure written directly as a `.c3d`,
the format's own boxes and sheets (`reference 3d-view`). It has no link to the `.clay` and goes
through no generator. The film is a `C3dSheet` of a named material, σ = 4e5 S/m, `ThicknessUm`
0.05. Metal1 is a sheet in one variant and a 3 µm gold box in the other. A user can build this today
with no change to circuitRF, which is what makes it independent. It shares only the
`.c3d`→solver lowering with the generated route, and that lowering already has its own goldens.

### The runs

| Run | Solver | Route | Conductors | Film | What it isolates |
|---|---|---|---|---|---|
| P | planar MoM (this brief) | `.clay` | sheets (by construction) | per-cell `Z_s` | the thing under test |
| P-DC | `PlanarDcSolve` | `.clay` | — | half-cell `R_s` | DC, exact on the rooftop network |
| **HA** | Palace | **hand-built** | sheets | sheet | **the reference**: P's idealisation, sharing no code with P or §8 |
| **HB** | Palace | **hand-built** | 3 µm gold boxes | sheet on Metal1's top face | **the thickness effect**: what P leaves out |
| HF | Palace | hand-built | 3 µm gold boxes | **a 50 nm box** | the film's own sheet idealisation, *only if its dry run is affordable* |
| PA | Palace | generated (`Conductors: Sheets`) | sheets | sheet | **§8 against HA**: should match to mesh noise |
| PB | Palace | generated (`Conductors: Solids`) | 3 µm gold | sheet at `FilmAt` = Top | **§8 against HB** |
| E | EMerge | generated (`Sheets`) | sheets | thin-conductor sheet | a second FEM, a different mesher |
| O | openEMS | generated (`Sheets`) | sheets | `ConductingSheet` | a time-domain solver with no shared algebra |
| 3D-DC | the 3D DC conduction solve (`Em3dDcLowering`) | hand-built (HA) | — | σ·t | an independent DC with bends |

- **Frequencies:** DC, 1, 5, 10, 20 and 40 GHz. Few points on purpose. Use the product's own
  Compare (one `.cem` naming the three 3D solvers, written as `<key>.compare_em.npy`) rather than a
  second harness.
- **Mesh:** shipped defaults everywhere, plus one refined HA run, so that HA's own convergence is on
  the record before anything is judged against it.
- **Cost discipline:** run `run analysis=em dryRun=true` first for every 3D leg. One MPI solve at a
  time. Use the memory watchdog on Palace and EMerge. The solid-metal legs (HB, PB) can grow, and HF
  more so. Stop and report if a dry run estimates more than a few minutes per point. Shrink the feeds
  before coarsening the mesh. HF is optional, and skipping it is a reported outcome, not a failure.

### What is compared, and what is expected

1. **DC.** P-DC against 3D-DC, both against a hand count of the zig-zag's squares. They should agree
   within ~1 %. The hand count disagrees with both at the bends by about half a square per corner;
   record that as a measurement, not a gate.
2. **P against HA**: |ΔS| per frequency on the de-embedded film section, and `Re`/`Im` of its series
   impedance. Same idealisation, independent code, so disagreement beyond HA's own refinement spread
   is a defect in this brief's kernel work.
3. **PA against HA, PB against HB**: the 2D-to-3D route against the hand-built one. Both go through
   the same lowering, so they should agree to mesh noise. Any real difference is a defect in §8's
   emission (clipping, the face, σ·t), and an `explain`/`render` of the two `.c3d`s side by side
   should show where.
4. **HB against HA**: the size of the thickness effect, per frequency. This answers the question
   discussion #41 actually asks: whether putting the film and a 3 µm metal at one sheet height
   matters. Reported, never gated. **HF against HB**, if run, does the same for the film's own
   thickness.
5. **E and O against PA**: three independent solvers on one idealisation. Their spread is the
   uncertainty P is judged within.

### What is committed

- `testdata/em-tfr/zigzag/`: the `.clay`, `.ctech` and `.cem`; the hand-built `.c3d`s (HA, HB,
  and HF if run); each run's de-embedded `.s2p`; and a
  `README.md` with each solver's version, CPU or GPU, mesh size, wall clock and date. The README
  states no tolerance the measurement did not produce.
- **Lowering goldens**, which run in CI because they solve nothing. A bound film lowers to Palace's
  `Conductivity` boundary, openEMS's `ConductingSheet` and EMerge's thin-conductor boundary, each with
  σ = 1/(R_s·t) and the stated thickness, beside `testdata/em3d/{palace,openems,emerge}-goldens/`.
- **Three routine gates** reading the committed data: P against HA's `.s2p`; P-DC against 3D-DC's
  resistance; and the generated `.c3d` for the fixture against the hand-built HA, compared as
  geometry (sheet outlines, z, σ·t) rather than as a solve. Each takes the tolerance its item above
  measured. If the P run of this fixture
  takes over ~5 s, it is an accuracy test in the `Benchmark` tier. It is not a timing test.

## Gates

One test per claim; none needs a full-wave sweep longer than a few seconds.

1. **DC closed form.** A straight film strip `L×W` between two Metal1 contacts, at 0 Hz:
   `R = R_s·L/W` to round-off on a Manhattan mesh (the DC network IS the rooftop basis). Vary `L/W`
   across 0.5…10 squares.
2. **Low-frequency continuity.** The same strip de-embedded at a handful of points up to a few GHz:
   `Re(Z₂₁)` flat at `R_s·L/W` within the kernel's own de-embedding residual, and continuous into
   the DC point (LF1/LF2's continuity property, now with a film in the path).
3. **Against the circuit model.** `TFR` PCell artwork EM-solved versus the `TFR` circuit model
   (π: `R` plus half the film capacitance each end) cascaded with the measured contact feeds — `S`
   within a stated tolerance at low GHz, judged by `|ΔS|` on the small entries.
4. **Metal wins.** A film rectangle drawn entirely under a Metal1 rectangle gives a mesh, a matrix
   and an answer **bit-identical** to the Metal1 rectangle alone.
5. **The oracle does not short the resistor.** Gate 1's strip with `PerfectConductor = true` still
   reads `R_s·L/W` (the metal is perfect; the film is not).
6. **No film, no change.** A mesh with no film produces a Gram, a `SheetTable`, a fill and a `.snp`
   hash bit-identical to today's — asserted on one single-level and one multi-level fixture.
7. **Merging respects the class.** With "Mesh as Merged Shapes" on, no merged cell carries two
   classes, and Gate 1's `R` is unchanged by turning merging on or off.
8. **Refusals and notes.** Port on film refused by name; an unbound film layer drawn in a run produces
   the open-circuit note; each `TechValidation` error in §1 fires.
9. **3D emission.** A bound film produces one clipped `Em3dSheet` per region with σ·t = 1/R_s.
   `Conductors: Sheets` and `Solids` each produce the conductor shape they name and keep the film a
   sheet. `Auto` is bit-identical to today's generator output on a layout with no film.
10. **The campaign's gates** — the lowering goldens and the two committed-reference comparisons in
    *Verification* above.

Scope the test runs to `tests/Engine.Tests --filter` on the new classes and `tests/Ui.Tests --filter`
on the extractor/technology/editor classes, plus `tests/Firewall.Tests`.

## Stop and report if

- The two-contribution Gram slot (§4) turns out not to cover a shape the mesher produces — e.g. a
  merged or cut support sharing more than two cells — before generalising it.
- The DC gate (1) passes and the AC gate (2) does not converge to it at the low end: that is a fill
  or `SheetTable` resolution defect, not a tolerance to relax.
- Binding `Resistor` to `Metal1` in the starter technology moves any existing starter EM result —
  that means some shipped layout already draws on the resistor layer in an EM run, and it needs a
  look before the default changes.
- PA, E and O disagree with each other by more than they disagree with P. Then the shared idealisation
  (the sheet boundary, or the port de-embedding) is the question, not this kernel, and needs a look
  before any gate is set from it.
- PB's dry run is large. See *Cost discipline* above.

## Order of work

1. **The hand-built references first** (HA, HB, 3D-DC, and HF if affordable). They need no code from
   this brief, so they exist before anything is written that they will judge, and their numbers
   can't be shaped by it. Commit them with their README, then report.
2. **§1–§7** with their gates, then P and P-DC against the committed references.
3. **§8**, then PA, PB, E and O.

Report at each boundary.

## Explicitly NOT in this brief

- **The stackup cross-section's interaction with a film** (drawing it on its band's face, selecting
  it, dragging it to another level or face, double-click to edit R_s). That belongs to the
  stackup-render series' gesture rules and is its own brief:
  [stackup-render 9](brief-stackup-render-9-resistor-films.md). This brief adds only the Layers-tab
  column and the conductor card's chip, which is enough to author a film.
- **A film as a solid of its own thickness in 3D.** It stays a sheet in every mode (§8).
- **Two conductor materials at one elevation in general** (two metals, not a metal and a film). The
  same per-cell class carries it, but a second metal raises the thickness-asymmetry question CL1's
  header already declines; nothing has asked for it.
- **A frequency-dependent film.** `R_s` is a constant; a film thick enough for skin effect to matter
  is a metal, and belongs on its own stackup entry.
- **Film on a ground-designated conductor**, refused in §1 rather than modelled.
