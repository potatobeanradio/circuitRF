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
