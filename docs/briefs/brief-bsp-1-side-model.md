# Brief BSP-1 — The side model: `Side`, surface roles, side-aware resolution, readable text

**Series:** bottom-side placement — read `brief-bsp-0-overview.md` first (D1–D6, and §4's answers). · **Tag:** `R-bsp1-<k>`
**Area:** `src/Design/Layout/LayoutModel.cs` (`LayoutInstance` ~759, `LabelShape` ~494),
`src/Design/Layout/Footprints/LandPatternLayers.cs` (the role resolution this brief generalises),
`src/Design/Layout/LayoutPersistence.cs`, `src/Design/Layout/CellLayoutResolver.cs`,
`src/Design/Layout/LayoutInstanceTransform.cs` (`ComposeInstances` ~150), `src/Design/Layout/LayoutFlatten.cs`,
`src/Design/Layout/LayoutDesignFlatten.cs`, `src/Design/Layout/LayoutTextOutline.cs`, `src/Render/Renderers/LayoutRenderer*`
(labels only), every `CellLayoutResolver.Resolve(inst.CellRef, …)` call site, `src/Design/resources/technologies/pcb-*.ctech`;
`docs/design/layout-view.md`, `src/Design/RESOLVED.md`
**Tests:** new `tests/Ui.Tests/Layout/BottomSideModelTests.cs`; the layout flatten/render/hit-test classes named in §5
**Depends on:** nothing.

---

## 1. Goal

Make "this placement is on the bottom face" a fact every reader of a layout sees the same way, with no editor UI yet
(BSP-2) and no exporter change beyond what falls out of flattening (BSP-3). After this brief a `.clay` written by hand
with `"Side": "Bottom"` on an instance draws, hit-tests, snaps, flattens, extracts and LVS-reads as a part on the bottom
face.

**A board has two surfaces, and a footprint draws on them only.** Flipping a part does not pair every layer of the
technology: it moves what the part draws on the top SURFACE to the bottom surface, role by role, and leaves everything
else — inner copper, drill, outline, any drawing layer that is not a surface role — exactly where it is.

## 2. Requirements

### 2.1 The model

**R-bsp1-1 — `LayoutInstance.Side`.** A new `enum PlacementSide { Top, Bottom }` and `LayoutInstance.Side`, default
`Top`. Every place that copies an instance field by field copies it too — `LayoutFlatten.FlattenOneLevel`'s array
expansion (~48), `ComposeInstances`, the clipboard, group/ungroup, undo snapshots. Grep for `MirrorX = ` assignments
that build a `new LayoutInstance` and add `Side` beside each; a test holds the array case.

**R-bsp1-2 — Sides compose (D5).** `ComposeInstances` sets `Side = outer.Side == inner.Side ? Top : Bottom` — the same
XOR it already applies to `MirrorX`. The flattened child of a bottom cell's bottom placement is on the top face.

### 2.2 Surface roles (D2)

**R-bsp1-3 — `SurfaceRoles` (new, `src/Design/Layout/Footprints/SurfaceRoles.cs`).** `SurfaceRoles.For(Technology)`
returns, for each surface role — **Copper, Soldermask, Paste, Silkscreen, Courtyard, Assembly** — the layer that plays it on
the TOP surface and the layer that plays it on the BOTTOM surface, either of which may be null. Built once per
technology instance (cache on a `ConditionalWeakTable` keyed on the `Technology`; never mutate it — technologies are
shared through `TechnologyCache`, and the layout editor draws on a clone).

Each role is resolved the way `LandPatternLayers` already resolves its front roles, and **by the same code**: generalise
its private `ResolveOptional`/`ResolveCopper` to take the side rather than writing a second resolver beside it.
- **Top:** the board-format alias (`F.Cu`, `F.Mask`, `F.Paste`, `F.SilkS`, `F.CrtYd`, `F.Fab`), then `Purpose`, then a
  layer name carrying a front side word — today's rules, unchanged. Copper falls back to the first stackup conductor on
  a laminate (`ResolveCopper`'s existing test).
- **Bottom:** the same with `B.` aliases, back side words (`bottom`, `bot`, `btm`, `back`, matched as whole words) and,
  for copper, the LAST stackup conductor's drawing layer.
`LandPatternLayers.Resolve` becomes `SurfaceRoles.For(tech).Top` plus its diagnostics; its behaviour and its
`LandPatternRoleSeverityTests` do not change.

**R-bsp1-4 — The swap.** `SurfaceRoles.Swap(LayerKey)` maps a layer that is a role's top layer to that role's bottom
layer and back; every other layer maps to itself. Two outcomes are not a swap:
- **The role has no layer on the other surface** (a technology with Silk Top and no Silk Bottom): the shape is
  **omitted**, exactly as `LandPatternLayers` omits a role the technology lacks (R-fp1-3a — never relocated onto another
  layer), and the omission is reported once per placement and role.
- **No bottom copper at all** (a single-sided board): a bottom placement is a refusal — there is nothing to solder the
  part to. This is the only refusal in the series.

**R-bsp1-5 — Shipped technologies complete (D6).** Each shipped board `.ctech` gains `Courtyard Bottom` (`B.CrtYd`),
coloured and z-ordered as its top twin, so a flipped generated land pattern omits nothing. Check each shipped board
technology for any other role present on one surface only and add its twin in the same change. The MMIC technology is
not a board and gets nothing. A test asserts every role a shipped board technology has on the top it also has on the
bottom.

### 2.3 Side-aware resolution (D3)

**R-bsp1-6 — `CellLayoutResolver.Resolve(LayoutInstance inst, string baseDir, Technology? tech)`.** For `Side == Top`
it is exactly today's `Resolve(inst.CellRef, baseDir)`. For `Bottom` it returns a resolution whose `View` is a clone of
the resolved view with every shape's layer (and every `LayoutPin`'s layer) passed through `SurfaceRoles.Swap`, shapes on a
role with no bottom layer left out, and every child instance's `Side` flipped per R-bsp1-2's rule — the child instances
themselves are not swapped here; they are resolved in their turn. Cache the clone keyed on (the resolved `LayoutView`
instance, the `Technology` instance) in a `ConditionalWeakTable`, so it is invalidated exactly when the resolver's own
cache is, and the renderer's compile cache (keyed on the view instance — `LayoutRenderer.Instances.cs` header) gives the
bottom view its own entry for free. **Geometry is not touched by the clone** — the mirror is the instance's `MirrorX`,
applied by the transform as today. The resolution carries the omitted roles (R-bsp1-4) so callers can report them, and a
refusal when the technology has no bottom copper.

**R-bsp1-7 — `tech` is the technology the PARENT is drawn in.** Each call site already has it, or has the layout editor's
`DrawTechnology`, or a flatten's resolved technology. Where a site has none (a pure-geometry helper), it passes `null`,
and a bottom placement resolves unswapped with a note — drawn where it is and reported, never guessed. List every such
site in `RESOLVED.md`.

**R-bsp1-8 — Migrate every call site.** Every `CellLayoutResolver.Resolve(<expr>.CellRef, …)` where `<expr>` is a
`LayoutInstance` becomes `Resolve(<expr>, …, tech)`. Calls that resolve a bare cell reference string (browsing, placing a
new part, Update Layout's lookups of a cell not yet placed) stay as they are. The ~60 sites are in `src/Design/Layout`
(flatten, design flatten, cell hierarchy, placed pins, LVS layout read, footprint labels, generated cells run, ground
artwork), `src/Render/Layout` (snap query, conductor lookup) and `src/Ui` (editor partials, properties, hierarchy
walk/resolver, WBond reference geometry and embedding, Update Layout). **Gate:** a comment-stripped source scan over
`src/` finds no `CellLayoutResolver.Resolve(` whose first argument ends in `.CellRef`.

### 2.4 Text on the bottom face (D4)

**R-bsp1-9 — Text keeps its reading direction.** A label in a mirrored placement (bottom-side or not) is drawn with its
glyphs NOT reflected: it reads left to right from above, on screen and in every exporter. `LabelShape` has no horizontal
justification — its anchor is the left end of the text — so carrying only the anchor through the mirror, which is what
`LayoutFlatten.FlattenOneLevel` does today, puts the text a text-width away from where the mirror carries it. Instead the
label's CENTRE (its outline's centre, `LayoutTextOutline`) is carried through the transform and the anchor recomputed
from it, so the text sits over the same spot of the mirrored part. Its angle is the cell's angle composed with the
instance's rotation, folded into (−90°, 90°] so it is never upside down. Apply this in the flatten, the renderer's instance
pass and hit test alike (one helper, e.g. `LayoutInstanceTransform.TransformLabel`), not three times. No field is added to
`LabelShape`.

### 2.5 What the readers do with it

**R-bsp1-10 — Flatten.** `LayoutFlatten.FlattenOneLevel` and `LayoutDesignFlatten` resolve through R-bsp1-6, so the
flattened shapes of a bottom placement are already on bottom layers. An omitted role becomes one note per placement
("R12 is on the bottom, but 'PCB …' has no bottom silkscreen layer; its silkscreen was omitted"); a missing bottom copper
is the refusal.

**R-bsp1-11 — Renderer, hit test, snap, conductor lookup, placed pins, LVS layout read.** These resolve per instance
already; after R-bsp1-8 they see the bottom view. Verify, don't rewrite: a bottom placement's lands are drawn in Bottom
Copper's colour and z-order, are picked by a press on them, snap to bottom-copper geometry, and its `PlacedPins` report
Bottom Copper.

### 2.6 Persistence

**R-bsp1-12 — `.clay`.** `Side` is written only when `Bottom` — match how `MirrorX` and `Mag` are written in
`LayoutPersistence.cs`. **No `FormatVersion` bump** (owner, O1): a file with no `Side` loads with every part on the top
face, and nothing migrates it. A round-trip test writes and reads one bottom instance and one top instance, byte-stable
for the top one against a file written before this change.

## 3. Out of scope

The editor gesture (BSP-2), exporters' structure-level handling (BSP-3), imports (BSP-4). There is no view from
below (owner, O3).
Nothing here adds a field to `LayerDef` or a column to the Technology editor: the roles are found, not declared. Do not
change `MirrorSelection`'s behaviour in this brief.

## 4. Things that will bite

- **Do not swap layers in the transform.** `LayoutInstanceTransform` and `LayoutCoordinateWalk` are geometry; putting a
  layer map into them reaches every coordinate walk in the repo, schematic side included.
- **Generated land patterns are ordinary views by the time they are resolved.** `smt:0402@N` resolves to a generated cell
  whose shapes are on the top roles; the clone swaps them like any other. Do not add a side to `FootprintRef` or to the
  generator.
- **A through-hole pad on both copper layers** swaps both ways and lands on both again: correct, and a test should say so.
- **Inner copper is not a surface.** A footprint that draws on an inner layer (a thermal relief on a plane) keeps it on the
  same inner layer when flipped; do not mirror the stack.

## 5. Tests (one per claim)

`BottomSideModelTests`: each role's top and bottom layer found on a shipped two-layer and four-layer technology, and on
a technology named only by side words (R-bsp1-3); a role with no bottom layer is omitted and reported, a technology with no
bottom copper refuses (R-bsp1-4); every shipped board technology has every role on both surfaces (R-bsp1-5); a bottom chip
land pattern flattens to Bottom Copper / Soldermask Bottom / Silk Bottom / Courtyard Bottom with its X mirrored, and an
inner-layer shape stays put (R-bsp1-6/10); sides compose through a two-level hierarchy (R-bsp1-2); an array of bottom parts
expands to bottom parts (R-bsp1-1); a label in a bottom placement reads left to right, centred over the spot the mirror carries its centre to, and is
never upside down (R-bsp1-9);
persistence round trip (R-bsp1-12); the source scan (R-bsp1-8); a bottom part is picked and its placed pin reports Bottom
Copper (R-bsp1-11). Then `LandPatternRoleSeverityTests` and the existing layout flatten, hit-test, snap and LVS classes by
name — not all of `Ui.Tests`.
