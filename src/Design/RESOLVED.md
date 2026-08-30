# src/Design — resolved findings (detail, off the CLAUDE.md growth path)

## MIM-1 — region vias: drawn via artwork beyond the point `ViaShape` (2026-08-30)

`docs/sonnet-briefs/brief-em-mim-1-region-vias.md`, gap 1 of the MIM series. Extraction and
reporting only; `src/Engine` untouched, and every §7 via refusal still fires unchanged.

### What was wrong, and why it was silent rather than refused

`PlanarExtractor`'s classification loop recognised a via-bound drawing layer in exactly one place:
inside its `if (s is ViaShape)` branch. Every other shape fell through to `binding`, the layer→z-band
map — and `BuildStack` builds that map from **non-Via entries only**, because a via contributes no
thickness and has no z band of its own. So a rectangle or polygon drawn on a via layer missed the map
and landed in `ignoredOther`.

**The counter it landed in is what made the failure worse than a drop.** `ignoredOther`'s note says
the shape is *"not bound to a stackup conductor or via entry"* — which is exactly the wrong advice
for artwork on a layer that IS bound, and sends the user to the technology editor to redo something
already done. The same silence swallowed a drawn backside-via slot or bar.

This is the same map-vs-branch split that made `BuildVias` unreachable at L9's phase gate. It is
worth stating once more: the two bindings answer different questions (where a layer sits in z, versus
which two conductors a via joins), keeping them apart is right, and the cost of keeping them apart is
that every new shape kind has to be routed to the second one deliberately.

### What it does now

- **A filled region on a via-bound layer becomes a `PlanarVia` footprint**, through the conductor
  path's own shape→`PlanarPolygon` conversion — outer ring plus holes, the layout's own flatten
  tolerance, the same degenerate-ring floor. Reused rather than restated: a via footprint and a
  conductor footprint are resolved onto the same tensor grid, and two conversions that could drift
  apart would show up as a via meshing to a slightly different set of cells than the metal it lands
  on.
- **The footprint is NOT squared.** The equal-area square (side = 0.886 × drill) exists so a round
  barrel *nobody drew* does not contribute a hard gridline per facet. A drawn outline already is the
  footprint, so it goes to the mesher as it stands.
- **Span, conductivity and the ground rule come from the stackup entry**, identical to the point
  path, and a region via participates in the same `noSpan` / `unknownLevels` / `notAdjacent` /
  `toGround` / `wrongGround` accounting — counted in SHAPES, because a shape is what the user drew
  and can go and look at.
- **Nothing on a via-bound layer falls into `ignoredOther` any more.** A `PathShape` there gets its
  own sentence (a centreline encloses no area; draw the region), and so does a region that flattens
  to nothing.

### The one design decision worth the words: regions are GROUPED PER STACKUP ENTRY

Every region on one via entry becomes **one `PlanarVia` carrying several footprint polygons**, not
one `PlanarVia` each. The obvious reason is that the span, conductivity and ground rule all come from
the entry, so per-shape vias would be N identical records. The real reason is correctness:

`SurfaceMesher` scans every grid cell against a via's polygon list and **stops at the first polygon
that covers it**. Two overlapping footprints inside one `PlanarVia` therefore give a shared cell
**one** vertical basis. As separate `PlanarVia`s they would give it **one each**, silently doubling
the vertical current in the overlap — and a plate connection drawn as two overlapping rectangles is
an ordinary thing to draw, not a corner case. `TwoOverlappingRegions_GiveTheirSharedCellsOneVerticalBasisEach`
pins it as a counter that is independent of the mesh pitch: no cell index appears twice,
and the meshed footprint is the union (60 × 40 µm) rather than the sum (2 × 40 × 40 µm).

**The same hazard exists on the point path and was left alone**, deliberately — it is pre-existing
behaviour, changing it would move existing runs, and the brief forbids touching the point path. It
was *measured* while sizing the structural gate below: a 2 × 2 array of nominally touching point
vias overlaps by 0.37 nm (see the next section), and the meshed footprint comes out at
1600.0591 µm² against the true union — i.e. the overlap strip is counted twice, exactly as the
first-cover argument predicts.

### The structural gate, and why it compares AREA rather than a basis list

The brief asks that a region via covering the cells of an N×N array of touching point vias yield the
same vertical basis functions. **That cannot be a basis-list comparison, and asserting it would be
asserting something false.** L9c's own mesher finding is that a via footprint must contribute HARD
gridlines or the via vanishes silently — so N×N touching footprints put N−1 interior gridlines per
axis into the shared tensor grid that one large footprint does not. Those lines *subdivide* the
covered cells; they do not move the covered boundary. Measured: 943 unknowns (4 vertical) for the
single region against 943 (4 vertical) for the drawn 2 × 2 array on this fixture.

The grid-independent statement of the same claim is **the plan-view area the vertical bases cover**
— still a cell counter (one basis per covered cell, summed over the cells' own areas), never an
S-parameter. The gate is in two halves:

| | Fixture | Claim | Result |
|---|---|---|---|
| A | 2 × 2 drawn squares vs one drawn rectangle over their union | covered area equal, **to the bit**; the single footprint needs no more unknowns | 1600 µm² both, N = 943 both |
| B | 2 × 2 point vias vs the same region | covered area equal to the equal-area square's own DBU rounding | −3.6926 × 10⁻⁵ relative, **predicted exactly** |

**Half B's discrepancy is predicted rather than bounded**, which is the part worth keeping. A point
via's square is 0.886 × drill and a drill is an integer number of DBU, so the square that gets meshed
has side s′ ≠ the nominal s and the array covers n²s′² against the region's (ns)². On this fixture
s′ − s = +0.37 nm, so nominally touching point vias in fact *overlap*, and 1 − (s′/s)² reproduces the
measured area difference to 12 decimal places. If the two ever disagree by anything that rounding
does not account for, one of the two paths has a real defect. (The overlap also costs N: 1096
unknowns with 16 vertical bases, against the drawn array's 943 with 4 — a sub-nanometre sliver run,
and a good illustration of why the point path snaps nothing.)

### Milestone 4's assumed paths, checked rather than assumed

- **A Via stackup row binds a drawing layer and states its span** — real, `ShowsDrawingLayerPicker`
  is `Kind == Via` and a Conductor row deliberately does not show that control (it binds through the
  layer table). Verified against a live `TechEditorViewModel` over the MMIC starter, including that
  the picker's option list actually contains the layer the extractor keys on.
- **A rectangle drawn on that layer reaches the extractor** — real, and is now the main body of tests.
- **`EmDiagnostics`' via count includes region vias** — **this path does not exist.** `EmDiagnostics`
  is the EM run service's REFUSAL family (`em.run.cancelled`, `em.layout.not-found`, …); it has no
  via counter and no counter of any other extraction quantity. The via count a user actually sees is
  carried in the run's NOTES, which `EmRunService` concatenates from the extractor and the mesher.
  Nothing was built: the smallest version is a test that both note sources count a region via, which
  is what `TheRunsOwnViaCount_IncludesRegionVias` asserts. Growing a diagnostic for a *quantity*
  would be the first non-refusal member of that family and is a decision for whoever converts the
  next family, not a side effect of this brief.

### Tests

`tests/Ui.Tests/Em/RegionViaExtractionTests.cs`, 13 methods, all routine tier (~70 ms). Point-via
bit-identity is asserted with `BitConverter.DoubleToInt64Bits` against the documented rule restated
in the test, not read back from the object under test.

The terminal resolution (`SpanFrom`/`SpanTo` → a level pair or one of five counters) is now a single
local function both artwork kinds call. That is not tidiness either: "the artwork says WHERE, the
stackup says WHICH TWO CONDUCTORS" only holds if the answer cannot depend on how the via was drawn,
and a second copy of that block is exactly how it would stop holding.

## R-em-4's ground query returns null for TWO reasons, and the note claimed the wrong one (2026-08-30)

`PlanarExtractor` resolves the EM ground as **the highest ground-designated conductor BELOW the
lowest analysis level**. When that query comes back empty and `Stackup.Bottom == Ground`, it fell
back to the bottom of the stack and said:

> No conductor layer in technology 'X' is marked as a ground reference, so the ground plane was taken
> from Stackup.Bottom = Ground at the bottom of the stack.

**That sentence is only true for one of the two ways to reach it.** The query is scoped to
conductors *below the signal*, so it also returns null on a stackup that HAS a designated ground
sitting *above* — and there the message is flatly false, contradicted by a ticked checkbox on the
Stackup tab the user is looking at.

**It survived because no shipped technology could reach the false branch.** Every PCB starter was
2-layer and the MMIC's ground is its backside metal, so the only ground candidate was always the
bottom conductor: "none below the signal" and "none at all" were the same statement. The first
technology with an INNER ground plane (`pcb-4layer_FR-4_62mil_1oz`, added the same day) made them
different, and a trace on a lower layer was told its technology designates no ground at all. Worse
than the wording: the run **succeeded**, solving against a reference further away than the real one,
so there was no refusal to prompt anyone to look.

The fallback now asks which case it is and names the planes it did find, says why they cannot serve
(a port returns through a plane BENEATH the conductor it feeds), and states the cost — the reading
will be a higher impedance than the real structure. The original sentence is kept verbatim for the
genuinely-undesignated case.

### Two neighbouring messages were wrong in the same way — advice for a situation that was not this one

- **The zero-height slab refusal** said *"Check the stackup order in the technology editor."* The
  commoner way to arrive there is a correctly-ordered board whose BOTTOM conductor is being treated
  as the signal: it rests on the `Stackup.Bottom = Ground` boundary, so the slab has zero height and
  nothing is misordered at all. That case is now named, with the two things that actually help
  (mark it as a ground reference, or move the trace up a layer).
- **The no-signal-conductor refusal** said *"Draw the artwork on a conductor layer, or bind the layer
  it is on to a conductor entry."* When every shape is on a ground-designated conductor the layer IS
  bound — the advice sends the user to redo something already done. Reachable on any stackup with
  more than one plane (a 4-layer board whose only artwork so far is an inner pour), so it now says
  the plane is not meshed and points at the "Ground reference" tick.

**None of the three was found by reading the extractor.** They were found by running it on each
conductor of the new 4-layer technology in turn and printing the result — a scratch xunit probe, run
once and deleted. A message can only be checked against the state that reaches it.

Gated by `tests/Ui.Tests/Em/FourLayerGroundReferenceTests.cs`, which drives the extractor rather than
scanning source, and includes the negative: the 2-layer starter must reach neither new branch.

## A board outline refused the EM run, and the dielectric binding was the workaround (2026-08-30)

User proposal: remove the dielectric's "Drawing layer" control from the `.ctech` editor, since the
binding is never used except under the hood. **The premise was wrong and the conclusion was right,
for a reason neither of us had.**

### What the binding actually did

Nothing electrical: `PlanarExtractor.BuildMediumStack` reads only `Epsr`/`TanD`/`Mur`/`ThicknessDbu`
and every dielectric is a laterally infinite slab, so a dielectric bound to `(none)` is everywhere.
Every other consumer filters it out — `WBondClearance` reads `DrawingLayers` only after
`if (sl.Kind != StackupKind.Conductor) continue`, `PcbLayerNaming`/`DrcConnectivity`/`GerberExport`
take conductors and vias, `PcbWriter` writes dielectric thickness with no layer reference, and in
`PlanarExtractor` a dielectric-bound and an unbound shape reach the same `ignoredOther`.

Its ONE effect was in `CrossSectionExtractor.Classify`, and it was not subtle. Measured on the MMIC
starter with a Metal1 trace plus a die outline on `Substrate`: binding kept → `Ok=True` with a note;
binding removed → **hard refusal.** The field was the difference between the run working and failing.

### The defect underneath: the refusal fired on the normal case

Sweeping every layer of the shipped 2-layer PCB starter, one shape at a time beside a solvable trace:

| Layer | Result |
|---|---|
| Top Copper, Bottom Copper, Drill | Ok |
| Soldermask Top / Bottom, Silk Top / Bottom, **Outline** | **REFUSED** |

**Every PCB layout has a board outline**, so the failing case was the normal one — and the refusal's
advice was *"add this drawing layer to a conductor entry's DrawingLayers list"*, i.e. declare your
board outline to be copper. The dielectric-`DrawingLayers` binding was a narrow escape hatch from
this, applied only where the MMIC starter tripped over it.

### The discriminator was available and was not being asked for

A layer the technology **declares** but binds to no stackup entry is the technology stating the layer
is not metal. Silk, soldermask and outline are exactly that. A layer the technology **does not
declare at all** — a foreign import, a hand-edited file — is the case nobody has said anything about,
and there the original reasoning holds in full.

So the refusal is narrowed, not deleted: declared-but-unbound is ignored with a note that names every
distinct layer once and still offers the fix (*"If one of them IS metal, bind it to a conductor entry
on the Stackup tab"*); undeclared still refuses, now pointing at the Layers tab rather than telling
anyone to call it copper. Ignoring is REPORTED, never silent — a trace genuinely drawn on a forgotten
layer is still visible in the run's own output.

With the workaround unnecessary, the editor's dielectric picker is gone
(`StackupLayerRowViewModel.ShowsDrawingLayerPicker`, via only). **The model field stays**: shipped and
user `.ctech` files carrying a dielectric binding still parse, validate, round-trip through
`TechnologyMerge`, and take their original more-specific "substrate extent" note — removing a control
must not rewrite anyone's file. `IsSingleDrawingLayer` is deliberately left answering `true` for a
dielectric, because the CARDINALITY rule did not change.

Gated by `tests/Ui.Tests/Em/UnboundLayerArtworkTests.cs` (10 tests), including both halves that make
this safe rather than merely permissive: the MMIC die outline extracts with the binding removed, and
a file that still carries one behaves exactly as before.

**None of this was visible by reading the extractor.** It came from running it on each layer in turn
and printing the verdict — a scratch xunit probe, run once and deleted. The same method found the
ground-reference bug above. A refusal can only be checked against the state that reaches it.

## A laterally-finite dielectric cannot be drawn, because the kernel cannot represent one

Asked while the section above was being investigated: how does a user simulate a MIM cap built on a
GaAs substrate, if the dielectric is always everywhere? Surely the nitride must be drawn on a layer.

**It cannot be, and no binding would have helped** — this is a formulation limit, not a missing
feature. `BuildMediumStack` produces a `LayerStack` of `MediumLayer(thickness, material)`: a 1-D
stack of laterally infinite slabs, and the DCIM Green's function is derived from exactly that stack.
Unknowns live on conductor surfaces and via barrels only. A nitride island under a top plate needs
either volume-equivalent currents inside the dielectric (a VIE) or a surface-equivalence formulation
on its boundary, and neither exists in `src/Engine`'s planar kernel.

Drawn dielectric geometry is therefore ignored — before the change above it fell into
`PlanarExtractor`'s `ignoredOther`; it is now named in the declared-but-unbound note. Reported, but
inert either way. Note also that the MMIC starter's own `Cap Dielectric` and `Nitride` drawing layers
are bound to no stackup entry at all: they are artwork/DRC/GDS layers, and their presence must not be
read as EM support.

What actually works, best first: a **lumped C in the schematic** (C = ε₀εᵣA/d from the process's
capacitance density, with the EM run covering the interconnect around it — the normal MMIC flow); or
**stating the inter-metal dielectric as nitride** in the stackup and meshing both metal levels, which
gets the plate overlap right out of the solve but puts every airbridge and crossover in the same run
in nitride instead of air; or **splitting the run**, EM for the passive interconnect and lumped caps
combined in the schematic.
