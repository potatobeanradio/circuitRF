# Schematic — resolved issues (see also `CLAUDE.md`)

Per-topic notes that don't belong in the standing `CLAUDE.md` file. Newest first.

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
