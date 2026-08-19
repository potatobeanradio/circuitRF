# Schematic — resolved issues (see also `CLAUDE.md`)

Per-topic notes that don't belong in the standing `CLAUDE.md` file. Newest first.

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
