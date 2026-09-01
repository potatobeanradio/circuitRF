# Schematic — resolved issues (see also `CLAUDE.md`)

Per-topic notes that don't belong in the standing `CLAUDE.md` file. Newest first.

## SRLC and PRLC: the pin contract is the whole design constraint (2026-08-31)

Two new lumped tiles, `SymbolKind.Srlc` and `SymbolKind.Prlc`, over the engine components `SRLC` and
`PRLC`. Owner-approved glyphs.

**The brief's real requirement was not "draw a smaller R, L and C" — it was "put the pins where R, L
and C already put theirs".** Small was the means: three borrowed glyphs have to fit inside one
400-unit span so the leads can still reach (0,∓200). Everything about the geometry follows from
that — the resistor drops to 4 zigs, the inductor to 3 coils at r=20, the capacitor's plates to 60
wide — and none of it is a free aesthetic choice. Both kinds fall through `SymbolPortDefs.For`'s
DEFAULT arm, which already returns exactly R/L/C's two pins, so there is no second copy of the
coordinates to drift.

**That contract needed a test, because breaking it is invisible.** The glyph lives in
`BuiltInSymbols.cs` and the pin table in `EditableSchematic.cs`; a redraw that nudged a pin would
leave a part that still places, still saves, still simulates — while every schematic it had been
dropped into came apart at the wires. `RlcPaletteWiringTests.R2` asserts the pins against R, L and
C's OWN values, read live, rather than against copied literals that would move together with the
mistake — and it first checks that R, L and C still agree with each other, since otherwise it is
measuring the wrong thing. `R2b` adds the complementary claim from the primitive geometry: nothing
drawn crosses y = ±200, and the leads reach both pins exactly.

**PRLC's ±110 symmetry is deliberate.** The three branches sit at x = −80 / 0 / +80; the resistor
keeps its full ±30 zig amplitude (there is room sideways) and the capacitor's plates are 60 wide, so
the extreme left and right land at exactly ∓110. An asymmetric glyph would sit visibly off-centre
against its own wire.

**One docs-generation trap, unrelated to this work but found by it.**
`docs/user/assets/figures/analysis-editor-hb-dark.svg` is NOT deterministic: it contains a `use`
whose transform is a rotation matrix that changes on every DocGen run (measured 7.46° then 10.75°,
same tree, same command). Anyone running `tools/DocGen/check-docs-current.sh` will see that one file
dirty no matter what they changed. It is a capture-time artefact, not a drift — leave it out of an
otherwise-clean change set rather than committing a random phase.

---

## A file inside the workspace was listed twice: in place, and again under Known Files (2026-08-30)

Owner report. A Known File is a **bookmark to a file the tree cannot otherwise show**; once the file
lives inside the workspace the ordinary scan already renders it where it sits, so the Known Files
group was showing a second copy the user has to learn to ignore. `Import Data` and a file drop onto
the tree both land here — `AddKnownFile` records any picked path, and R-stb-10/11 explicitly allows
an in-workspace reference (stored workspace-relative).

**Fixed as a rendering filter, not a list filter** (`WorkspaceScanner.Scan`). A `.cws` entry whose
resolved path is already an `AbsolutePath` somewhere in the tree just built is skipped, and the group
node is omitted entirely when nothing survives.

- **The test is "already in the tree", NOT "inside the workspace root".** Those are different
  questions and the difference is load-bearing: `IsHiddenTreeFile` deliberately hides `.DS_Store` and
  `*.source` from the ordinary scan, so naming one as a Known File is the only way to see it, and
  that opt-in has its own test. A "was it inside the root" test would have broken it. Same for a
  broken in-workspace reference — nothing on disk to render, so the warning node is still the only
  way the user learns the reference is dead.
- **The `.cws` list itself is untouched, deliberately.** `GetKnownTouchstoneFiles` /
  `GetKnownLoadpullFiles` feed the Data Display's data-source library from `KnownFiles`, **not from
  the tree** — `DataSourceLibraryViewModel` otherwise only enumerates `results/*.npy`. Dropping an
  in-workspace `.sNp`/`.spl` from the list at write time (the tempting "don't record it at all" fix)
  would silently remove an imported measurement from every trace picker.
- Comparison is on the **resolved, fully-qualified, trailing-separator-trimmed** path
  (`WorkspaceScanner.PathKey`), because the stored form is relative for an in-workspace reference and
  absolute for an outside one, and `ResolveRef` returns a rooted ref unnormalized.

**Second, latent bug found on the way: `RemoveKnownFile` could never remove a relative entry.** It
compared `node.AbsolutePath` against the raw stored string, so for the workspace-relative form the
`RemoveAll` matched nothing, the `.cws` was rewritten unchanged, and the user still got
"Reference removed (file not deleted)". It now matches the resolved path as well. This is reachable
after the fix above — a hidden file opted in by name is stored relative and is still shown.


## Drag-follow redrew the whole wire, and mid-span taps left the net (2026-08-30)

Owner testing turned up seven drag defects on three real sheets. **Two were disconnects** — the
serious kind, because the schematic still looks wired and simulates as something else — and five were
shape damage. **All seven are one line of code:** both component-drag follow paths, the live tick
(`UpdateConnectedWireEndpointsLive`) and the commit (`CommitDragAsCommand`), threw the followed wire's
whole polyline away and redrew it as `WireGeometry.OrthogonalRoute`'s bare L between the two
endpoints.

**Why that loses connections, not just tidiness.** A wire's mid-span T-taps are geometric: a pin on a
segment interior IS on that net (`ComputeConnectivityGeometry`). The bare L is a *different wire* — it
does not pass where the original's interior was — so every tap on it is dropped, silently. Two
capacitors tapping the middle of a horizontal wire between an inductor and another capacitor left the
net when the inductor was nudged **one grid step**, and the resulting netlist is a different circuit
that still runs.

The five "annoying" reports are the same L seen from the other side: a vertical run comes back
horizontal, a horizontal run moves off its row, and in one case the new leg landed exactly on top of
an unrelated vertical wire — where a reader cannot tell one net from two — and ran through a
transistor's symbol on the way.

**The rule now: a moved endpoint deforms its own wire as little as the geometry allows**
(`WireGeometry.FollowEndpoints`). An orthogonal polyline alternates H and V legs, so the delta at a
moved end splits into a part ALONG that end's leg — absorbed by lengthening it, changing nothing else
— and a part ACROSS it, handed to the **one** neighbouring vertex, where the next leg (perpendicular
by construction) absorbs it as its own length. **Propagation stops there**: nothing past the second
vertex ever moves, so bends, rows and columns survive, and so does every tap not on the two legs that
changed. When the neighbour is the far ENDPOINT it is held by whatever is at the other end and can
absorb nothing — a plain two-point wire is exactly this case — so an elbow is inserted AT THE MOVED
END, leaving the original leg (and its taps) untouched. That elbow is the vertical jog a user expects
to see appear under a part they nudged off its row, and it is what fixes both disconnects.

**A tap that leaves its wire now grows a stub; the wire is not bent to chase it.** The old
`RouteBodyFollow` re-routed the tapped wire through the moved pin — but that branch is only reached
when NEITHER of the wire's endpoints moved, i.e. when both ends are anchored by something staying put,
so it was dragging a run the user placed (and everything else tapping it) on behalf of a part with no
claim on either end. `BuildTapStubs` creates a `PlaceWireCommand` stub instead, chained into the same
undoable composite as the move, exactly as the segment-drag path already did for the same situation
(`BuildInteriorPortStubs`). **The stub leaves the wire at a right angle**, from the foot of the
perpendicular dropped onto the nearest segment, so it never runs ALONG the wire it joins.

**A gap closed while there:** the stub is built from the wire's POST-follow geometry, so a tap also
survives when the tapped wire is itself following a moved endpoint. Dragging the inductor and one of
the tapping capacitors *together* used to lose the other capacitor, and the old code could not have
caught it — it `continue`d past the body-follow branch whenever an endpoint had moved.

**Not attempted, and it should not be assumed:** general obstacle-aware routing. Wire-over-wire and
wire-over-symbol are listed as out of scope in
`docs/design/placement-connectivity-and-drag-follow.md`, and both reported instances of them were
*produced by* the whole-wire redraw, so preserving shape removes them at the cause rather than by
avoidance. A drag that genuinely needs a detour still will not get one.

Gated by `tests/Ui.Tests/Schematic/DragRoutePreservesShapeTests.cs` (19 cases): net-level extraction
oracles for both disconnects, the exact expected polyline for each of the five shape reports (so a
future tidy-up cannot quietly go back to the L), the stub's geometry and its undo, the group-drag
case above, and a wire-over-wire overlap check on the sheet where the old route produced one.

## An added parameter group rendered no label, whatever "show on schematic" said (2026-08-29)

Owner report: adding a second tone to a VTone (and to the new ITone) with **View on schematic** ticked
put no label on the instance.

**The checkbox was being honoured; the value was missing.** `EditableSchematic.BuildRenderModel` skips
any label parameter whose `Expression` is empty — right for a label, since "Freq[2] = " is noise — and
`ParameterEditorViewModel.AddGroup` created every member of a new group with `Expression = ""`. So the
one moment a user ticks that box is the one moment it appears to do nothing.

**Fixed at the cause, not at the render rule.** `IndexedParamGroup` now carries `DefaultExpressions`,
and every group whose members are `ShowOnSchematic` states real ones — the tone families (VTone, ITone,
PnTone) and the impedance ones (P1Tone's `Z[k]`, ZPort's `Z[n]`, both 50 Ω). SDD equation slots and VAR
rows deliberately keep no default: blank genuinely is the right start there, and an invented value is a
guess the user then has to notice and undo. `EveryShownMemberOfAnAddedGroup_HasANonBlankDefault` is the
gate, and it is expressed as the RULE (shown ⟹ non-blank) rather than as a list, so a future group
cannot be added blank without failing it.

Second-order: a blank shown parameter also made `SchematicViewModel`'s `LabelCount` (`2 +
LabelParameters().Count()`, unfiltered) disagree with the renderer's own filtered list, so per-label
drag offsets would index the wrong row. With no group ever added blank, that condition no longer
arises from this path; the underlying mismatch is untouched and is worth its own look if a blank shown
parameter can be reached another way.

## ITone and the VCCS: the arrow is the whole of the direction cue (2026-08-29)

`SymbolKind.CurrentToneSource` ("ITone", `I_1Tone`) and `SymbolKind.Vccs` ("VCCS").

**Both reuse the BJT's arrowhead** (owner request) — a filled three-point `Poly` lying ON the lead, at
the BJT's own size — because that glyph is already the thing a reader looks for when they want to know
which way something flows.

**They point OPPOSITE ways, and that is correct.** ITone's points UP, at pin 1, where an independent
source delivers its current (`src/Engine/CLAUDE.md` → "Current-source direction"). The VCCS's points
DOWN, at `out−`: a controlled transconductance SINKS its current from `out+`, which is how a
small-signal gm source is drawn in every device model. `Vccs_Glyph_…` asserts both arrows in one test,
against each other, so a later "make these consistent" pass cannot flip one and leave the schematic
lying about a direction.

**ITone is VTone's body with the polarity marks swapped for the arrow.** Same circle, same sine, same
two pins in the same places — so the two read as one family — and deliberately NOT the textbook
circle-with-an-arrow-inside: the body is 120 across and already carries the sine, so an arrowhead
inside it would either collide with the sine or shrink to nothing at palette size. On the lead it is
legible at every zoom and cannot be mistaken for the AC mark.

**The VCCS's control leads stop short of the diamond, and the gap IS the drawing.** They end at
x = −170; the diamond's left vertex is at x = −90. A lead touching the body would draw a connection the
device does not have — the control pair senses voltage and carries no current at all. A glyph test
asserts the gap rather than trusting the coordinates to stay put.

**Pin ORDER is the engine contract, in the 2N ± pair form** `VccsModel` reads:
`[out+, out−, ctrl+, ctrl−]`. Swapping either pair reverses the source's sign and still solves, so the
order is asserted by test, not left to the geometry.

## The bipolar transistor: two kinds, one law — and what that costs elsewhere (2026-08-29)

`SymbolKind.BjtNpn` / `BjtPnp`, engine references `BJT_NPN` / `BJT_PNP`. Both place the SAME model
(`BjtModel`) with the SAME parameter list; only a sign differs. That is the inverse of the FET family
sitting beside them in the palette, where five kinds denote five different drain-current laws, and the
inversion is worth stating because it makes two rules in this directory read the wrong way round.

**Why not one kind with a polarity parameter.** Because the two DRAW differently, and the emitter
arrow is the entire cue a reader has. A parameter would leave the drawing and the netlist free to
disagree — an n-p-n on screen, a p-n-p in the run — with nothing reporting it. `EngineReference` puts
the polarity in the NETLIST for the same reason. This is also why they are the one place in
`BuiltInSymbols` where two kinds of one family do NOT share a glyph, and `DevicePaletteWiringTests`
now asserts that they don't, so a later "same topology, share the glyph" tidy-up fails loudly.

**Both polarity names are search terms on BOTH tiles.** Somebody typing "PNP" is looking for the pair,
not for one of them.

**The two are not interchangeable at a bias point, which broke the palette-wiring test's own probe.**
`DevicePaletteWiringTests.P4` perturbs every registry parameter and requires the device's behaviour to
move. Applied to a p-n-p with the n-p-n's bias grid it reports `Tf` — and anything else that only
lives in forward conduction — as an unwired parameter, because at those voltages the p-n-p is
reverse-active. The grid is therefore mirrored by polarity (`BjtBiases`), which is what
`BjtModel.IsNpn` exists for. The same trap is waiting for any future probe over this family.

**The saturation row of that grid is load-bearing.** `Br`, `Nr`, `Ikr`, `Isc` and `Nc` do essentially
nothing with the collector junction reverse-biased — their contribution is 1e-20-ish and lands under
the test's own change threshold. Without a bias with BOTH junctions forward, five real parameters
read as unwired. (And the shipped `Vtf` default cannot be probed at any bias at all — see
`src/Core/RESOLVED.md`'s own note for why, and why the test's activation value differs from it.)


## Four PDK defects: two kits' models crossing, an empty part-to-cell map, and a symbol that vanished when zoomed out (2026-08-19)

Four owner reports, three unrelated root causes and one shared lesson: **each failure looked like the
feature simply not working, because in every case the wrong answer was indistinguishable from a
legitimate one.**

### 1. An imported kit's parts all reported "no layout artwork", and only reopening the workspace fixed it

Owner report: import an open-process kit, place one of its components, and updating the layout from
the schematic reports every placed part as having no artwork — including parts whose layout cells the
kit plainly ships. Devices that used to render stopped rendering.

**Root cause: the part-to-cell map is filled by a background reading that exactly one path started.**
Which of a kit's parametric cells is a given schematic part's layout view is settled once, by the
palette (`KitPaletteMerge`), and published for everything else to read (`KitLayoutGenerators`). That
reading has to START a kit's Python interpreter, so `WorkspaceViewModel.RefreshPCellPaletteItems`
runs it off the UI thread — and it was reachable from one place only, the workspace PATH changing.

A kit imported into an already-open workspace declares its parametric-cell library **during that
import** (`DeclareKitPCellLibrary`), which sets `_pcellDeclarationsAdded` and calls
`ReloadPCellGenerators` — and that method rescans, invalidates and regenerates, but never re-read the
palette. So the map stayed empty for the whole session. The same hole swallowed the consent path:
a kit refused permission cannot be listed, so the reading taken while it was `Unknown` found nothing,
and granting permission afterwards never took it again.

**Verified rather than inferred.** Driving `PdkImporter` → `PdkPartInstaller` → `PCellWorkerResolver`
→ `KitPaletteMerge` against a kit pairs all 34 of its cells correctly — including the several
whose schematic part and layout cell are named nothing alike, which the model rule settles — and
generating one produces real geometry. So the matching rules were never the problem, and neither was
the artwork. Only the publishing was.

**Fix, in two parts, because one of them is only a narrowing of the window:**

- Every path that can change what the resolver would answer now refreshes: `ReloadPCellGenerators`
  and the consent-granted branch of `RequestPCellConsent`, alongside the workspace open. The reading
  itself is split into `CollectPCellGeneratorInfo` (no UI work) and `ApplyPCellGeneratorInfo`, and
  carries a generation counter so a slow earlier pass cannot land on top of a later one.
- `KitLayoutGenerators.SetRefresher` — a lookup that MISSES may ask, once, for the reading to be
  taken now. This is what makes the answer independent of timing rather than merely likelier to be
  right: a part placed in the seconds between a kit being declared and its interpreter answering
  would otherwise still get the wrong answer. It costs nothing once the map is populated (the hook
  returns immediately), it is asked at most once per lookup, it cannot re-enter itself, and a hook
  that throws is treated as a miss. Starting an interpreter there is no more than the
  `PCellRegistry.TryGet` on the very next line already does.

**The message for a part that genuinely has no layout cell is now one short clause.** It used to
carry three, telling the user to go and drop the cell from the palette themselves — written for a
period when the pairing was routinely failing outright. Once the pairing works, what reaches this
line is a model-only part (a parasitic capacitance, a technology include) with no artwork to place,
and a paragraph of recovery advice per placed part is noise.

### 2. Importing one kit picked up a NEIGHBOURING kit's compiled models — and broke its simulation

Owner report: importing a kit found "a whole bunch of `.osdi` models"; separately, a kit that used to
simulate now fails elaboration with the provider exposing a device type that belongs to a completely
different kit.

**These are one bug.** `PdkPartInstaller.FindCompiledModels` searched the kit root **and two ancestor
levels**. Unpacked kits routinely live side by side under one folder, so importing a kit whose
devices come from a compiled model LIBRARY found the compiled-Verilog-A artefacts of an unrelated
kit two levels up — seven of them, in the reported case — took the compiled-Verilog-A branch of
`SynthesiseProviderSettings` on the strength of them, and wrote settings naming the other kit's
worker and artefact. Everything imported cleanly. The failure surfaced only at Run.

**Reproduced exactly** by pointing `DeviceWorkerManifest.ToolsDirectory` at a build that ships the
OSDI worker (a test host does not, which is why no existing test could see this) and importing the
kit: the derived settings named the neighbour's artefact, and the same import with the ancestor walk
removed derives the correct compiled-library settings instead.

**Why the rule differs from the library search's, which DOES walk up.** A model library is recognised
by the entry points circuitRF's own worker will call, so finding one beside a kit is evidence about
that kit. An `.osdi` file carries nothing of the sort — it is one compiled module, and a folder of
kits therefore answers every one of them with the first kit's models. An ancestor is a coincidence;
the kit's own tree, and the folders the workspace was TOLD hold model libraries, are statements.

**Fix:** the search starts at the kit root (still recursive, so a kit's own artefact is found however
deep it sits) and adds the declared library roots. Nothing else.

**And the search fix alone would not have repaired anyone's workspace.** Derived settings are
RECORDED in `.cws` and win outright on every open — that is the whole point of recording them. So
`GeneratedFormat` is bumped 4 → 5, which is the mechanism `KeepIfStillCurrent` already carries for
exactly this: circuitRF's own earlier working-out is redone, while a kit's own settings and a user's
edits are untouched.

### 3. A kit's schematic symbol disappeared when zoomed out

Owner report: one PDK symbol does not render when zoomed out; at normal zoom it is fine.

**Root cause: the level-of-detail stand-in was sized from a nominal built-in symbol.**
`SchematicRenderer` decides to substitute a filled rectangle when `zoom * 300 < 6` — 300 world units
being a built-in's nominal width — and then drew that rectangle at `300 x 100` world units scaled.
Both numbers are an order of magnitude wrong for an imported kit's symbol. The reported part measures
**3,275 x 3,375 world units** (measured, not estimated: its terminals and artwork through
`KitTemplateSymbol`), so at the zoom where the substitution switches on it is still ~65 px across —
and was being replaced by a **4 x 1.4 px** speck. Nothing errored; the part simply looked absent
while every built-in around it stayed legible.

**Fix:** both the decision and the rectangle come from the component's own `GlyphBb`. A symbol whose
artwork is genuinely too small to read is still stood in for (that is what the substitution is for,
and the built-in case is unchanged); one still large on screen is drawn. The rectangle is centred on
the GLYPH, not on the component origin — a kit symbol's artwork is often nowhere near its origin, so
the two are not interchangeable.

### Gates

- `tests/Ui.Tests/KitLayoutGeneratorRefreshTests.cs` — the fallback hook (asked once, not re-entered,
  a throwing hook is a miss, `Clear` keeps the hook) plus source-level wiring checks that the reload
  and consent paths refresh, since `WorkspaceViewModel` cannot be constructed in a test.
- `tests/Ui.Tests/PdkNeighbouringKitIsolationTests.cs` — 5 tests over two kits side by side. Verified
  to fail with the ancestor walk restored, and the format-bump test verified to fail at
  `GeneratedFormat = 4`.
- `tests/Ui.Tests/SchematicLodGlyphSizeTests.cs` — 6 tests, oracle is the painted-pixel extent off a
  real Skia render rather than the renderer's own arithmetic. Verified to fail against the previous
  renderer.
- `KitPartToLayoutTests.L1` updated to the new skip wording, and now also holds the line SHORT.
- `KitLayoutArtworkTests` joined `PdkToolsDirectoryCollection`: `KitLayoutGenerators` is process-wide
  and now carries an installable hook, so classes that publish into it must not run alongside.

End-to-end against the reported workspace: the previously-skipped part is added to the layout with
its correct artwork, and the only remaining skip line is the model-only part that genuinely has no
layout cell.

## Wire hitbox is too thin when zoomed out (2026-08-19)

Owner report: schematic wires are hard to click and drag when the view is zoomed out.

**Root cause: the pick band is a WORLD constant, and the wire's stroke is a PIXEL one.**
`SchematicHitTest` used a flat `WireHitTol = 8` world units (and `EndpointHitTol = 12`) regardless of
view scale, while `SchematicRenderer` draws a wire at `max(1 px, zoom * 4)`. The two therefore move
in opposite directions as the user zooms out: at zoom 0.1 the wire is still drawn 1 px wide, but its
clickable band has collapsed to 0.8 px either side of the centreline — **narrower than the stroke the
user is aiming at**. Nothing about the wire looks unclickable, which is why it reads as a hitbox bug
rather than a zoom bug.

**Fix:** `Test` and `TestStack` take an optional `zoom` (default 1.0, so every existing call and test
keeps its old meaning) and derive the band from `WireTolFor`/`EndpointTolFor`:
`max(world constant, min(pixel floor / zoom, 45 % of GridSize))`. The floors are 7 px for a segment
and 10 px for an endpoint. Both sit *below* the old world constants at 1:1 (8 and 12), so **the feel
at 1:1 and at any zoom-in is bit-identical** — the new term only ever binds on zoom-out. The four
call sites pass the live scale: `SchematicCanvas` its `_zoom`, `SchematicViewModel` its `CanvasZoom`
(already maintained for the canvas-object gripper, and already synced at every zoom mutation).

**Two traps the obvious version falls into:**

- **The spatial-index window must grow with the band.** A wire's bounding box is a zero-thickness
  line along its run, so querying the old `hitRadius`-sized window returns no candidate at all for a
  click 30 world units off a horizontal wire — the widened band would have been dead code. `half` is
  now `max(hitRadius, wireTol, endTol)`.
- **A grown endpoint zone must not swallow its own segment.** At zoom 0.05 a 10 px endpoint radius is
  200 world units; on a 200-unit wire the two endpoint zones cover the whole thing and the segment —
  the thing the owner wants to drag — becomes unreachable. `CapEndpointTol` caps the radius at 40 %
  of the adjacent segment, floored at the original `EndpointHitTol` so short wires at 1:1 are
  untouched.

**Why the band is capped at 45 % of the connection grid rather than growing without bound.** The
picker returns the *topmost* candidate, not the *nearest*. Two parallel wires one grid pitch apart
are 10 px apart on screen at zoom 0.1; an uncapped 7 px band would overlap its neighbour's and hand
back a wire the user was not pointing at. 45 % of a 100-unit grid leaves a 10-unit gap between
adjacent bands, so the answer stays unambiguous. The cost is that below zoom ~0.156 the band is
grid-limited rather than 7 px (4.5 px at zoom 0.1 — still 5.6x the old 0.8 px). Making the picker
nearest-wins instead would lift that cap, but it changes the Z-order semantics that click-through
cycling and the overlapping-wire tests depend on, so it was not done.

**Not changed, deliberately:** the drag *threshold* (`5.0` world units in `HandleSelectDrag`) has the
same world-vs-pixel confusion but fails in the harmless direction on zoom-out (a drag starts too
easily, not too late). The wire-drawing snap tolerances (`NearestWireEndpoint`,
`NearestPointOnWireSegment`, `NearestWireCrossing`, all 15) are left in world units on purpose:
they decide *electrical connectivity*, and connectivity must not depend on how far the user happened
to be zoomed out when they drew the wire.

Tests: `HitTestTests` — `WireSegment_ZoomedOut_IsPickableSevenScreenPixelsOff` (3 zooms, each also
asserting the same click misses under the old fixed band), `..._StaysPickableThroughTestStack` (the
path the select tool actually presses through), `WireBand_FarZoomOut_IsCappedByTheGrid_NotTheStroke`,
`WirePick_AtUnityZoom_MatchesLegacyBand`, and
`WireEndpoint_GrabZone_NeverSwallowsItsOwnSegment`.

## Library Palette: explicit "All" order, "All - Alphabetical", "Nonlinear" filter (2026-08-16)

Owner report: the "All" filter's order looked "random" — it was never random, it was
`LibraryCatalog.BuildAllItems()`'s category-rank-then-DisplayName sort (`CategorySortKey`), which
reads as arbitrary unless you know the category priority order. Three owner-requested changes:

- **`LibraryCatalog.AllItemsPinnedOrder()`** — the "All" filter now shows an explicit 22-row pin
  list first (`AllFilterPinnedOrder`, keyed by `(SymbolKind, PortCount)` because Snp/ZPort/Sdd share
  one Kind across several port-count entry points), then every remaining built-in in `AllItems`'s
  own order. `PaletteTool.ComputeRawItems` calls this instead of `LibraryCatalog.AllItems` for the
  `All` category; PDK parts are still appended after, unsorted, unchanged from before.
- **`LibraryCatalog.AllItemsAlphabetical()`** + `PaletteTool.WithPdkAlphabeticalByKit` — the new
  "All - Alphabetical" filter (`PaletteCategoryKind.AllAlphabetical`, listed directly under "All" in
  `BuildCategories`). Built-ins pure-alphabetical by DisplayName, then PDK parts grouped by kit (kit
  groups alphabetical, matching the kit list's own ordering elsewhere), alphabetical within each kit,
  never interleaved across kits.
- **`ComponentCategory.Nonlinear`** — a new Real-category filter. Deliberately an
  `ExtraCategories` membership on nine registry entries (NonlinearC, VerilogA, Diode, the 5 FETs, and
  the shared `Sdd` entry, which covers all of SDD/SDD1/SDD2/SDD3), never anyone's *primary* Category
  — so it changes nothing about where those items sort in `AllItems`/the pinned "All" order, it only
  adds one more filter that finds them.

**"VnTone" resolved to `ToneSource`.** The owner's pin list paired `PnTone` with a `VnTone` that
does not exist anywhere in the codebase — the actual single-tone voltage source is `SymbolKind.ToneSource`,
`DisplayName` "VTone" (no "n"; `EngineReference` is `V_1Tone`, which is likely where the "V1Tone"
naming came from). Confirmed with the owner directly — pinned row 14 is `ToneSource`.
