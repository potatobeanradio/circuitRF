# Brief PAA-5 — the dynamic symbol

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa5-<n>` · **Depends on:** PAA-3, PAA-4 · **Design note:** §4.6
**Area:** `src/Design/Schematic/BuiltInSymbols.cs` (beside `BuildCombiner`), `ComponentTypeRegistry.cs`,
`src/Ui/Controls/PaletteGlyphControl.cs`, `src/Ui/Diagnostics/SymbolArtworkGenerator.cs`, `src/Render/RESOLVED.md`.

## The gap

Until this brief the component draws a plain box. An array placed in a schematic should say what it is at a glance: how
many elements on what lattice, which are switched off, where its beam points, and — in Elements mode — which pin is
which element. The Combiner already draws a symbol that changes with N and is cached per N; the array's symbol changes
with more than one parameter.

## Requirements

**R-paa5-1 — Feed mode.** A rounded body holding a thumbnail of the aperture: one small square per element up to 8×8,
the `tri` lattice drawn offset; above 8 in either direction, an 8×8 corner of it with an ellipsis and `Rows × Cols`
written beneath. One pin `F` on the left at the Combiner's lead length. A beam lobe leaving the aperture's front face,
tilted by the resolved θ₀ when the weights are a `steer()` or `steerDelay()` (found from the parsed `Phase`/`Delay`,
R-paa4-3), with `θ0 30°  φ0 0°` beside it; boresight and no text when they are not.

**R-paa5-2 — Elements mode.** The thumbnail on the right with the four corner elements numbered (`1`, `Cols`,
`N−Cols+1`, `N`) so the numbering of D6 is visible, and one pin per element down the left at a 100-unit pitch,
numbered `1…N`, as the Combiner's paths are. The body grows with N exactly as the Combiner's does.

**R-paa5-3 — Off elements.** Drawn hollow in the thumbnail, in both modes.

**R-paa5-4 — Variant and cache.** The symbol is built from the instance's resolved `Mode`, `Rows`, `Cols`, `Lattice`,
`Off` and θ₀/φ₀ (rounded to 1°) at the nominal point, cached per that key as `_combinerCache` is per N, and redrawn when
any of them changes. A value that does not resolve (a VAR not yet defined) draws the default variant: 4×4, boresight, no
text. The palette tile and any caller with no instance draw the default variant, the contract the other dynamic System
symbols keep.

**R-paa5-5 — Pin hover.** In Elements mode a hover on pin n names `element n — row r, col c — x, y` in the unit of `dx`.

**R-paa5-6 — The same everywhere.** The schematic canvas, the palette, `render` of a `.csch` and `netlist --to-schematic`
draw the same symbol, because they all go through `BuiltInSymbols`.

## Gates

1. UiSnapshot PNGs, looked at, of: Feed mode 4×4 boresight, 32×32 steered to 30°, a `tri` 6×6 with two elements off;
   Elements mode 2×2 and 1×8. Committed to the completion note, not to the repo.
2. `render` of a schematic holding a Feed-mode and an Elements-mode instance produces the same SVG run as a process and
   in process (the existing render gate's pattern).
3. Changing a placed Elements-mode instance from 4×4 to 6×4 (rows only, so D6 keeps every existing element's number)
   adds pins 17…24 and keeps the wires on pins 1…16 attached to the same pins.
4. The cache holds one symbol per key: placing ten identical instances builds one.

## Not here

A symbol that shows the pattern itself; that is the tool's (PAA-9).

## Completion note (2026-11-14)

**Built.** The symbol is drawn from a variant, `PhasedArrayGlyph` (src/Design/Schematic): mode, Rows, Cols, rect/tri, the elements
switched off and θ0/φ0 rounded to 1°, resolved at the nominal point against the schematic's own VARs (`SchematicNominal`, set on the
component by the render and by the extraction, so `Rows=nr` binds the pins it draws). `BuiltInSymbols.PrimitivesForPhasedArray`
caches one symbol per variant. **Feed mode:** the aperture seen from the front in a rounded body (a square per element up to 8 × 8;
a larger array its top-left 8 × 8 with drawn ellipses and `Rows × Cols` beneath; a tri lattice's odd rows offset), pin `F` at
(-300, 0) with the Combiner's lead, and a beam lobe (r = L·cos⁶α) leaving the right face, tilted by θ0 with `θ0 30°  φ0 0°` beside it
when Phase (or Delay) holds a `steer()`/`steerDelay()` found in its parsed expression; boresight and no text otherwise. **Elements
mode:** pins 1…N down the left at a 100-unit pitch, the body ±80 beyond the outer pins as the Combiner's, the aperture on the right
with its corner elements numbered (1, Cols, N − Cols + 1, N). Off elements are hollow in both. A lattice that does not resolve draws
4 × 4, a steering that does not, boresight with no text; the palette tile and every no-instance caller draw Feed 4 × 4 at boresight,
and the palette now places Feed mode at 4 × 4 so the part dragged out is the tile. A hover on an Elements-mode pin names
`element n — row r, col c — x, y` in dx's unit (`EditableComponent.PhasedArrayPinHover`, shown by the canvas as a tooltip). The
canvas, the palette, `render` and `netlist --to-schematic` all draw through `BuiltInSymbols`.

**Gates.**
1. Pictures, looked at, drawn by `netlist --to-schematic` then `render` (the renderer the canvas uses), in the session scratchpad
   (`paa5/`, not committed): `feed44.png` Feed 4 × 4 at boresight; `feed32.png` 32 × 32 with Rows a VAR, steered to 30° by `steer(theta0,phi0,f0)`
   (the corner, the ellipses, `32 × 32`, the lobe tilted up and `θ0 30°  φ0 0°`); `tri66.png` tri 6 × 6 with elements 8 and 23 off
   (offset rows, two hollow squares); `el22.png` and `el18.png` Elements 2 × 2 and 1 × 8 (element 3 off), corners numbered, pins from
   pin 1 down.
2. `RenderCliVerbTests.RenderingPhasedArraysAsAProcess_WritesTheBytesTheRendererWrites`: a Feed-mode instance steered by a VAR and an
   Elements-mode tri instance render as a process to the in-process renderer's SVG, the steering text present.
3. `PhasedArraySymbolTests.AddingRows_AddsPinsBelow_AndKeepsEveryWireOnItsPin`: 4 × 4 → 6 × 4 gives 24 pins, pins 1…16 at the same
   coordinates, their wires still connected, 17…24 unconnected.
4. `TenIdenticalInstances_BuildOneSymbol`: one build for ten instances, one shared symbol. Plus the variant's resolution and fallback,
   and the pin hover.

**What changed from the brief, and why.**
- **Elements-mode pins are anchored at pin 1, not centred** as the Combiner's are. Gate 3 asks that adding rows keep every wire on its
  pin; centred, every pin moves by half the added height. N = 1 and Feed mode keep the pin where PAA-3 put it; an Elements-mode
  schematic saved under PAA-3 with N > 1 will find its pins moved once.
- **The palette's default Mode is now feed (4 × 4)**, so the tile, which R-paa5-4 asks to draw the default variant, shows what is placed.
- **The ellipses are drawn dots**: the symbol font has no vertical ellipsis (it rendered as a box).
- The hover is verified on its text (`PhasedArrayPinHover`); the tooltip itself is an Avalonia tooltip on the canvas, which the headless
  harness does not open.
