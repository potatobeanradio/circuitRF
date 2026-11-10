# PSV-2 — passivation openings: a dielectric that exists everywhere except where a layer is drawn

Series: `brief-psv-0-overview.md`. The 3D half is independent; the planar note needs brief 1.

## Why

A semiconductor process covers its top metal with passivation and opens it over the bond pads. The
opening is drawn on its own mask layer, whose name varies by process. It is not a via:
it connects no two conductors. It is a hole in a dielectric. The technology has no way to describe it.
`PresentWithLayer` (MIM-11) ties a dielectric to a drawing layer, but only one way round: the film
exists only where the layer is drawn. A passivation needs the opposite.

The 3D generator already has every part needed. `Em3dLayoutSolids` builds a dielectric's lateral outline
from drawn shapes for `PresentWithLayer` (~line 627). `Extrude` carries hole rings into the solid.
Overlap is settled by precedence (metal over dielectric, then construction order: `Em3dProblem.cs`
~297). One builder serves the layout's 3D view, a 3D `.cem`, a layout placed in a `.c3d`, and the
thermal solve. A feature added there reaches all four routes.

## What to build

### R-psv2-1 — the field

Add `StackupLayer.OpenWithLayer` (`string?`), on the `PresentWithLayer` pattern: additive, nullable,
no `.ctech` `FormatVersion` bump, ignored on a non-dielectric entry, written only when set so every
existing `.ctech` round-trips unchanged.

`TechValidation`:
- The name must resolve to a **drawing layer** of the technology. A conductor entry's name is an error
  that says an opening is drawn on a mask layer. MIM-11's conductor-first namespace is right for a film
  tied to its plate and wrong here.
- `OpenWithLayer` and `PresentWithLayer` on one entry is an error naming both.
- On a non-dielectric entry, it is a warning that it is ignored, matching what `PresentWithLayer` does
  there today.

Carry it through `TechnologyMerge`, `StarterTechnologies` (no starter sets it, D4) and the technology
importers' round-trip, wherever `PresentWithLayer` is carried today. `grep -rn PresentWithLayer src`
lists the sites.

### R-psv2-2 — 3D: the opening is a hole

In `Em3dLayoutSolids`, beside the `PresentWithLayer` block:
- Merge the opening layer's shapes (`PlanarExtractor.MergeOverlapping` → `ToPolygons`, as the film
  block does).
- The slab's lateral outline is what it would be without the field (the board outline, the bounded
  extent, or the full air-box extent), **minus** the merged openings. Use the layout boolean code
  already in `src/Design/Layout`, not a new clipper. The result is a polygon list with hole rings, and
  an opening that crosses the slab's edge becomes a notch.
- **Not an air solid with a higher construction order.** A subtraction keeps the slab one honest solid.
  It adds no air object to the 3D view, the body list or the thermal problem. It does not depend on
  which order a backend resolves overlaps in.
- Notes: one line per run giving the number of openings cut and their total area. A note when
  `OpenWithLayer` names a layer with nothing drawn on it, so the slab is whole. A note when an opening
  lies over no conductor (an opening over bare substrate is legal but usually a drawing mistake; a
  note, not a refusal).
- A bond wire landing in an opening needs nothing new: wires are built last. Confirm with a wire in the
  test fixture.

### R-psv2-3 — planar: the passivation stays, and the run says how much is open

The planar medium is laterally infinite (D3). With brief 1 built, the passivation is in the medium
everywhere. Add one INFO note, worded on the pattern of MIM-11's coverage note in `PlanarExtractor`:
the layer is opened by the named mask over N % of the analysed area, and the planar solve keeps it
everywhere. Before brief 1 is built, the cover is not in the planar solve at all, and the existing
warning already says so. Do not add a second note.

### R-psv2-4 — the stackup editor and the stackup drawing

- `StackupLayerRowViewModel`: an **"Opened by layer"** picker on a dielectric row, beside "Present with
  layer". It lists drawing layers only, with a "None" row. Choosing one clears the other picker's value,
  so the two cannot be set together from the UI. Reserve the picker's space; it must not shift other
  controls (no inline `IsVisible` in the row).
- `StackupScene` (src/Render): draw an opened dielectric with a visible gap or notch, the way it marks a
  `PresentWithLayer` film today (~lines 956 and 1308), so the stackup picture shows the slab is opened.
- UiSnapshot: the stackup editor with the picker set, and a `--clay` 3D render of the brief-4 fixture
  showing the pad exposed through the opening. Look at both.

## Gates

1. **Round trip.** A `.ctech` with `OpenWithLayer` reads and writes back byte-identical. A `.ctech`
   without it is unchanged.
2. **Validation.** Three tests: a conductor name, both ties on one entry, a missing layer. Each gives
   its sentence.
3. **The hole.** A layout with a 100 × 100 µm pad on the top metal and a 90 × 90 µm opening over it,
   built into a 3D problem from a 3D `.cem` and again as an instance in a `.c3d`: the passivation solid
   has exactly one hole ring, matching the opening to DBU rounding, at the same coordinates in both
   routes.
4. **Exposed metal wins.** In that problem, nothing but the pad's own metal and air occupies the
   opening's volume above the pad (inspect the solids; no solve).
5. **Byte identity.** A technology without the field builds an identical 3D problem (the solid list and
   the provenance hash).
6. **Planar note** (once brief 1 is in): the fixture's planar run carries the coverage note with the
   right percentage.

## Out of scope

- A sloped or conformal opening (D5).
- A planar solve with a laterally finite dielectric. That is the 2.5D formulation's limit.
- A DRC rule for opening-to-pad enclosure. Possibly worth having, but it is a separate brief.
