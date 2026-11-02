# Brief 4 — symbols from a kit's own glyphs (OPTIONAL)

**Series:** [gdsfactory kits](brief-gdf-0-overview.md) · **Tag:** `R-gdf4-n` · **Phase:** schematic side
**Area:** `src/Design/Schematic/PCellKitSchematicParts.cs`, new `src/Design/Schematic/KitGlyphSymbol.cs`,
`tools/pcell-python/gdsfactory_bridge/bridge.py` (glyph discovery), `src/Ui/Schematic/KitPaletteMerge.cs`
**Depends on:** 2 · **Blocks:** —
**Status:** **Not to be built until the owner says so** (overview D9).
**Rule:** overview §0.

---

## 0. The question this answers

`PCellKitSchematicParts` says circuitRF **draws nothing on a kit's behalf**, because a generated box
with the right pin count reads as a part nobody recognises. That rule stands.

Some gdsfactory kits ship **their own drawing** of each cell. Kit B ships an SVG glyph for 38 of its
cells: 100 × 100 viewBox, lead lines running to the box edge, no pin markup. Building a `.csym` from
the kit's drawing is not circuitRF drawing on the kit's behalf, so it is within the rule. What
circuitRF adds is only **where the pins go**, and that comes from the cell's ports.

What such a symbol buys: the cell can be placed on a schematic, and Update Layout from Schematic and
LVS can see it. What it does **not** buy is a simulation model. A kit B resonator placed on a
schematic still has no circuit behaviour until an EM run produces an SnP for it (the existing
EM-to-SnP flow). The completion note must say this to the owner plainly.

## 1. `R-gdf4-1` — find the glyph

- `register_pdk` reports, per generator, a glyph path when the kit's package holds an SVG whose stem
  equals the cell name. Look in a `symbols/` package directory or beside the cell module.
- The match is by **file stem against the cell name**, which is the declaration-by-name rule
  `PCellKitSchematicParts` already uses for `.csym`. There is no fuzzy matching.
- A kit-supplied `.csym` with the same stem wins over a glyph.

## 2. `R-gdf4-2` — build the symbol

- **`R-gdf4-2a`** Convert the SVG's paths, rects, circles and text into `.csym` graphics. Use the
  symbol editor's own primitives. Unsupported SVG constructs are dropped **and counted** in the import
  report.
  - Colours are not carried. The symbol takes the schematic theme's symbol colour, as every built-in
    symbol does.
  - The kit's dark-mode CSS is ignored.
- **`R-gdf4-2b`** **Pins.** For each port (brief 2 §4c: `placement` excluded):
  - Choose the box edge from the port's outward orientation: 180° → left, 0° → right, 90° → top,
    270° → bottom.
  - Order pins along that edge by the port's coordinate across it.
  - Spread them evenly on the schematic grid.
  - Where a glyph lead line ends on that edge, snap the pin to the nearest lead end.
  - **If a cell has more ports on one edge than the glyph has lead ends there, place the pins on the
    grid anyway and say so in the report.** Never drop a pin.
- **`R-gdf4-2c`** The symbol carries **no parameters** (the existing rule: parameters come from the
  generator).

## 3. Tests (synthetic glyphs, tier A)

**UI is rendered headlessly and inspected** (overview §6): each symbol built from a kit glyph, placed on a schematic canvas and as its palette tile, with its pins where the cell's ports are. Render each with `tools/UiSnapshot`
(add a mode where none hosts it), look at the PNG, fix what it shows, and list the pictures in the completion
note.


1. A two-lead horizontal glyph with ports at 180°/0° gives pins on the lead ends.
2. Four ports on one edge with two lead ends gives four pins, and the report says two are not on a
   lead.
3. An SVG `<filter>` is dropped and counted, and the rest of the glyph converts.
4. A kit `.csym` with the same stem wins over the glyph.

## 4. Scope

- No symbol for a cell with no glyph.
- No generated box, ever.
- No circuit model.

---

## Completion note (2026-10-30)

Built on the owner's go-ahead.

**What a symbol buys, plainly.** A cell with a glyph can now be placed on a schematic, and Update Layout
from Schematic and LVS see it. **It still has no circuit behaviour.** A kit B resonator placed on a
schematic simulates as nothing until an EM run produces an SnP for it. The import report and
`circuitrf import pdk` both say this next to the symbol list.

**How it is built.**
- `register_pdk` (R-gdf4-1) reports `symbols` (cell → kit `.csym`), `glyphs` (cell → `.svg`) and, for each
  glyph cell, `ports` (non-`placement` ports at the default build). It looks in the package's `symbols/`,
  the cell module's `symbols/`, and beside the cell module. Names are matched by exact stem against the
  directory listing, so a case-insensitive disk cannot match a different spelling. A `.csym` wins.
- Symbols are **installed at import as files**: `KitGlyphSymbol.Install` writes
  `<module>-pcells/symbols/<cell>.csym`, which is where `PCellKitSchematicParts.FindSymbolFile` already
  looks. So the mount path, `PdkKitRegistry` and `KitPaletteMerge` did not change: the existing same-kit,
  same-id rule merges each tile with its generator. This also means the symbols work on a machine whose
  user has not picked an interpreter yet. An existing `.csym` is never overwritten, and the report says
  it was kept.
- R-gdf4-2a: lines, polylines, polygons, rects, circles, ellipses, paths and text are converted, including
  through `g` transforms. Curves stay curves; arcs and the curves of a filled outline are flattened. All
  other constructs are dropped and counted (`<defs>` children are counted by element name). Colours are not
  carried, and `@media` rules (the dark-mode CSS) are not read, as the brief says.
- R-gdf4-2b: the edge comes from the orientation, and port order is kept across the edge. Pins are spread
  evenly on the grid, and a lead end takes a pin. The symbol size is the smallest even number of grid
  squares that fits every edge; among a few larger sizes, the one that puts the glyph's edges and lead
  ends nearest the grid is chosen. When lead ends and pins differ in number, every order-keeping
  assignment is tried. The one that moves pins least from the even spread wins, and ties go to the most
  centred. No pin is ever dropped; pins not on a lead are named in the report as a warning. R-gdf4-2c:
  the symbol has no parameters.

**Two decisions beyond the brief, both about what is filled.**
1. **The glyph's plain CSS is read.** "Ignore the kit's CSS" could not be followed literally. Kit B's
   glyphs state `fill:none` ONLY through a class (`.s{fill:none;stroke:…}`). With all CSS ignored, every
   outline in 37 symbols became a solid block hiding the drawing (seen in the first contact sheet). Now
   simple class and element rules are read for fill, stroke, opacity and font size. `@media` blocks (the
   dark-mode rules) are not read, and no colour is carried, so the brief's intent holds.
2. **A fill is drawn solid only when it is at least half opaque and not near-white.** Kit B tints its pads
   with `fill-opacity:.25`; as solid fills they covered the junction drawn over them. A fainter fill
   becomes a **tinted** shape (see "Follow-on: tinted shapes" below). A near-white fill
   with no stroke counts as the glyph's background and is not drawn: the synthetic glyph's white backing
   rectangle had drawn as a heavy frame. A shape stating neither fill nor stroke is drawn filled, which
   matches SVG's default.

**Against the real kit B** (scratch environment, nothing committed): `circuitrf import pdk --module …
--allow-code` built **37 symbols from its 38 glyphs**. The 38th glyph's cell shares its name with a
circuitRF built-in generator, so it is not offered (the existing shadowing rule) and gets no symbol. Nothing was dropped. Three symbols
have one pin not on a lead: in each, the cell has a port on an edge where the glyph draws no lead.
Two cells have only `placement` ports, so their symbols have no pins, and the report warns. I looked at
a contact sheet of all 37 (`circuitrf render` of each `.csym`): each reads as its glyph.

**Firewall text gate.** The converter returns its two user-facing problems (not SVG; root not `<svg>`) as
text through `TryBuild`, and its tokenizer's internal exceptions carry no message. The gate passes with
no allowlist lines added.

**Tests (tier A, run: 5/5 pass).** `tests/Ui.Tests/Gdsfactory/KitGlyphSymbolTests.cs`: one per §3 claim,
plus one for decision 1 above.
Test 4 runs the bridge's own lookup in plain Python, with no gdsfactory needed, and skips without Python.
Also run because I touched their paths: `Firewall.Tests` 79/79; `ImportPdkCliVerbTests` 4/4 and
`GdsfactoryAdapterTests` 7/7 (with `CRF_GDSFACTORY_PYTHON`); `PdkPCellExampleTests.TheKitShipsASchematicSymbol…`
4/4; `tools/pcell-python/verify.py` all pass. The synthetic kit now ships `symbols/` (three glyphs, plus
`rect_pad` with both a glyph and a `.csym`).

**Pictures (UiSnapshot, looked at):**
- `--gdsfactory symbols` (new step): the kit's palette heading, with one tile per glyph showing the glyph,
  beside a schematic with one of each placed. Checked: `ported` pins on its two lead ends; `turned` pins
  on the bottom and right edges where the ports point; `rect_pad` drawn from its `.csym`, not its glyph;
  `kinds` with the filter dropped and the white background no longer framing it; pin names inside the
  edge, not overlapping. Fixed what the first picture showed: the background frame, and the palette
  opening on built-ins instead of the kit's heading.
- `--gdsfactory report-open`: the new "Schematic symbols from the kit's drawings (4)" section shows the
  no-model note, each cell's source, and the `kinds` filter line. Nothing is clipped.

**Not done.** There is no committed test against kit B: its glyphs must not enter the repo. Brief 5's
acceptance matrix is where to keep that check.

### Follow-on: fill strength (2026-10-30, owner asked for it after the brief)

The symbol format had no way to shade a region, so kit B's 25 % pads became plain outlines. First built as one
fixed tint level with a checkbox. The owner then asked for any level, set with a slider plus a validated box,
which replaced it the same day. As built:
- **Format.** `Filled` stays exactly as it was and means 100 %. A strength strictly between 0 and 1 is
  `Tint` on rect, rounded rect, circle, ellipse, polygon and taper, written only when set. Existing `.csym`
  files serialise byte for byte, the format version is unchanged, and an older build draws a partly filled
  shape as its outline. `SymbolFill` (in `SymbolModel.cs`) is the one place that reads (`Of`), writes (`Set`,
  rounding to 0.1 % and never setting both fields) and parses (`TryParsePercent`) a fill. A file with both
  fields set by hand is solid.
- **Drawing.** `SchematicRenderer.DrawSymbol` draws a partial fill in two passes: the line colour at that
  alpha, then the outline. Solid is the one fill pass it always was. Everything that draws a symbol goes
  through this method; `circuitrf render`'s SVG carries `fill-opacity`. The placement ghost draws outlines.
- **Selection.** Any fill above 0 makes the inside clickable. `Clone` carries `Tint`.
- **Editor.** The two checkboxes are replaced by one **Fill** row: a 0–100 slider and a percent box.
  - The slider previews as it moves and becomes ONE undo step on release (pointer release, lost capture or
    key up), the 3D Properties transparency row's pattern.
  - The box applies on Enter or on leaving it; Escape reverts.
  - Accepted: 0–100, optional `%`, point or comma decimal.
  - Refused, with a one-line reason under the box and the value left as it was: empty, `NaN`/`Infinity`
    (which .NET would otherwise parse), exponents, thousands separators, a doubled `%`, out of range.
  - The line under the box is always reserved, so a refusal never moves the rows below.
- **Glyph converter.** The kit's opacity is carried as the shape's fill strength (kit B's 0.25 → 25 %; a
  kit's 0.6 would stay 60 %), no longer mapped to on/off. Kit B: **13 shapes in 9 of its 37 symbols** are
  partly filled, all at 25 %.

**Tests (run, all pass):** new `tests/Ui.Tests/SymbolFillTests.cs` (23):
- storage: none, partial and solid; both fields set by hand means solid;
- drawn alpha at 0, 25, 60 and 100 %, with a full-strength outline;
- any fill makes the inside clickable;
- 8 accepted and 11 refused typed inputs;
- in the inspector, a 4-step drag is one undo step, and a refused entry leaves the value and the text.

`KitGlyphSymbolTests` asserts the kit's 0.25 arrives as 25 % (5/5). Also run: the wider symbol
model/persistence/inspector/drawing set, 165/165, and `Firewall.Tests`, 79/79.

**Pictures (looked at), UiSnapshot `--csym` (new; prints the box, the slider and the stored fill after
every step) and `--dark`:**
- One of kit B's symbols with its 25 % shape selected.
- After a real drag on the slider to 82 %, drawn live, with the editor's unsaved-change dot after release.
- Typed `abc` and `150`, in light and dark: one-line reasons, and the Stroke row stays where it was. The
  first shot wrapped the reason onto two lines and moved the row; fixed by shorter reasons on a
  fixed-height line.
- Typed `12,5 %`: applied as 12.5 %, and the slider follows without rounding it (the slider's own
  snap-to-tick was removed for that).
- Typed `100`: solid, identical to the old Filled.
- The synthetic kit's palette tile and placed symbol at 25 %.

User doc: `reference/symbol-editor.md` describes the Fill row; that one page was regenerated with
`DocGen --page`, without figures. The symbol-editor figure still shows the old inspector, and the
end-of-series DocGen run will update it.
