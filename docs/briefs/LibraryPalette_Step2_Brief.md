# Library Palette — Step 2: the glyph-only tile (Claude Code / Sonnet)

Render one Palette item as a **square button showing its symbol glyph only** (no pins, no parameter text, no
instance name) with the **type label underneath** — reusing `SchematicRenderer.DrawSymbol`. **This brief is
ONLY step 2** — the tile control + its glyph render, shown in a simple non-interactive list. **No grid layout,
no header/filter/search, no placement, no drag-and-drop, no armed-state** — those are steps 3+. Read
`library-palette.md` §3 first. Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `docs/design/library-palette.md` §3 (the tile — glyph-only, type label, square button, armed
> state [armed is step 4, not now]). Context code: `src/Ui/Schematic/LibraryCatalog.cs` (step 1 —
> `PaletteItem` {Kind, PortCount, DisplayName, Category, …}; `AllItems` for a quick inert list),
> `src/Ui/Renderers/SchematicRenderer.cs` (`DrawSymbol(canvas, primitives, compX, compY, rotation, mirrorX,
> panX, panY, zoom, theme)` — **the glyph render to reuse**; it draws a symbol's primitives generically),
> `src/Ui/Renderers/SymbolEditorRenderer.cs` (the **pattern**: it calls `SchematicRenderer.DrawSymbol(...,
> compX:0, compY:0, R0, mirrorX:false, panX, panY, zoom, theme)` to render a symbol standalone — the tile does
> the same, glyph-only, auto-scaled to fit), `src/Ui/Schematic/BuiltInSymbols.cs` (`Primitives(SymbolKind)` —
> the geometry for a kind; the tile renders these), `src/Ui/Schematic/SymbolGeometry.cs` (`BboxOf`/`ComputeBb`
> — to compute the glyph bounds for auto-scale/centering), the existing Skia canvas control pattern (how
> `SchematicCanvas`/`SymbolEditorCanvas` host an `ICustomDrawOperation` + `ISkiaSharpApiLease`). Design docs
> win on any conflict.

## The spine (do not violate)
- **Glyph-only** (§3) — render just the symbol primitives. **No pins, no parameter text, no instance name** —
  a tile is an icon, not a schematic preview. (`DrawSymbol` draws primitives; don't add the pin/label passes.)
- **Reuse `SchematicRenderer.DrawSymbol`** — the same generic primitive render the canvas + symbol editor use;
  do NOT write a second symbol renderer. Auto-scale + center the glyph to fit the tile with padding (compute
  the glyph bbox via `SymbolGeometry`, derive a zoom + pan that fits it in the tile).
- **Type label underneath** — the `PaletteItem.DisplayName` (port-count-aware via the registry where relevant)
  as the tile caption; honors the theme.
- **Square button** — the tile looks like a square button (themed); **armed/depressed state is step 4** — do
  NOT wire placement or arming now (a tile may expose an `IsArmed` bool for later, but nothing drives it yet).
- **Theme-driven colors** — symbol-line role from `SchematicRenderTheme`; no literal colors.
- **Scope fence (step 2):** the tile control + glyph render + a simple inert list to view tiles. NO grid
  column logic, NO header, NO filter/search UI, NO placement, NO drag-and-drop, NO MRU.

---

## LAYER 1 — the glyph render into a tile-sized surface

1. A **tile glyph renderer**: given a `SymbolKind` (+ PortCount) and a tile pixel size, render
   `BuiltInSymbols.Primitives(kind)` **glyph-only** centered + auto-scaled into that square, via
   `SchematicRenderer.DrawSymbol` (compute the symbol bbox with `SymbolGeometry.BboxOf`/`ComputeBb`, then pick
   `zoom`/`panX`/`panY` so the glyph fits with a margin). Transparent or themed-button background.
2. Host it in a small Skia draw control (mirror `SymbolEditorCanvas`'s `ICustomDrawOperation` +
   `ISkiaSharpApiLease` setup) sized to the tile.
3. Verify a few kinds render recognizably (R, L, C, a source, Ground) — centered, scaled, glyph-only.

**Layer 1 gate:** a tile-sized control renders a given `SymbolKind`'s glyph centered + auto-scaled, glyph-only
(no pins/text), theme-colored, via `DrawSymbol`. Report (screenshot description for R/L/C). 

---

## LAYER 2 — the tile control (glyph + caption + square button) and an inert list

1. A **`PaletteTile`** control/usercontrol bound to a `PaletteItem`: the glyph render (Layer 1) in a **square
   button** with the **DisplayName caption** underneath; tooltip = display name + category. An `IsArmed`
   visual state may exist (depressed look) but is **not driven** yet (step 4).
2. A minimal **inert host** to view tiles — e.g. a simple `ItemsControl`/`WrapPanel` bound to
   `LibraryCatalog.AllItems` rendering a `PaletteTile` each. (Just to see them; the real grid + column logic +
   header are step 3. No interaction.)
3. Tiles are uniform square size; captions elide if long.

**Layer 2 gate:** an inert list shows a `PaletteTile` for each `LibraryCatalog.AllItems` entry — glyph + label,
square buttons, recognizable symbols, theme-colored; no interaction yet. Report (screenshot description).

## Acceptance (step 2)
1. A glyph-only tile renderer reuses `SchematicRenderer.DrawSymbol` to draw a `SymbolKind`'s primitives,
   auto-scaled + centered in a square, no pins/text, theme-colored.
2. A `PaletteTile` control binds a `PaletteItem` → square button + glyph + DisplayName caption + tooltip;
   an inert list renders all `AllItems`.
3. `dotnet build`/`dotnet test` green; firewall green (SkiaSharp allowed below src/Ui; no SKColor in
   framework-free models); **no grid logic, header, filter/search, placement, drag-and-drop, or MRU**
   (steps 3+); nothing else regresses.

## Guardrails
- **Glyph-only** — primitives only; no pin/label passes; reuse `DrawSymbol` (no second renderer).
- **Auto-scale + center** via `SymbolGeometry` bbox; padding/margin in the tile.
- **Type label underneath** (`DisplayName`); theme-driven colors (symbol-line role); no literal colors.
- **Armed state is step 4** — `IsArmed` may exist but nothing drives it; no placement/arming now.
- **Scope fence:** tile + glyph render + inert list only.
- Sub-gate the two layers; report and stop between each.
- Update `library-palette.md` §10 status (step 2 done) and `src/Ui/CLAUDE.md` (the Palette tile reuses
  `DrawSymbol`, glyph-only + caption).

*Exit: a glyph-only Palette tile (square button + symbol + label), reusing the shared symbol renderer, shown in
an inert list — the visual unit the grid + header (step 3) and the placement state machine (step 4) build on.*
