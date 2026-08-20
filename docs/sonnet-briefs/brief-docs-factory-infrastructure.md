# Sonnet Brief — Docs Factory (DF): vector UI capture and one-command doc generation

**Design:** `docs/design/user-docs-factory.md` (read it first — §2–§4 are *measured* findings, not
proposals). **Companion brief:** `brief-user-docs-content.md` writes the actual pages and depends on
everything here. Build this brief first; the content brief cannot start until DF1–DF3 land.

**Where findings go: `src/Ui/RESOLVED.md`.** **Do not write in any `CLAUDE.md`.**

---

## Gate command

```
dotnet test tests/Ui.Tests       --no-build
dotnet test tests/Firewall.Tests --no-build
```

Separate commands (`MSB1008`). `Ui.Tests` is ~27 s for 5,075 tests — a fast loop. **This brief adds
`tools/DocGen/`, adds files under `src/Ui/Diagnostics/`, and edits `src/Ui/Program.cs`'s CLI hook and
the docs CSS. If you find yourself editing `src/Core`, `src/Engine` or `src/RfCore`, stop and report.**

---

## 0. Read this first

### 0.1 What this builds

One command regenerates every user-doc figure and every user-doc page:

```
dotnet run --project tools/DocGen -- --out docs/user           # figures + fonts + HTML
dotnet run --project tools/DocGen -- --slides docs/slides      # landscape PDF decks
```

Figures are **vector SVG rendered from the live application** — real Avalonia controls, real dialogs,
real toolbars, and our own Skia canvases (schematic, layout, Smith chart, plots), in **light and dark**.
No bitmaps anywhere in the user docs. When the UI changes, re-run one command.

### 0.2 The four facts that make it work — all verified, do not re-litigate

1. **`Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync(SKCanvas, Visual)` is public** in Avalonia
   12.0.3 and renders a laid-out visual tree into any `SKCanvas`. Give it an `SKSvgCanvas` → vector SVG.
2. **`UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` is mandatory.**
   The default headless platform stubs drawing out; without the flag you get an empty document and no
   error.
3. **Our `ICustomDrawOperation` canvases render too.** `ISkiaSharpApiLease` hands the draw op the real
   `SKCanvas`, which here *is* the SVG recorder — so a captured window containing a Smith chart or a
   layout canvas is vector end to end. Verified with a probe that drew through the lease.
4. **`ScrollViewer` already behaves as required** — clipped to its box, scrolled to top, off-box
   children culled, thumb drawn at the top. Do not write special handling for it.

### 0.3 The trap that will otherwise ship silently

**Skia's SVG device omits `fill` when the colour is black — and drops `fill-opacity` with it.**

| Brush | Emitted |
|---|---|
| `#33000000` (black, 20 %) | `<rect …/>` — no `fill`, no `fill-opacity` → renders **opaque black** |
| `#33010101` (one bit off black, 20 %) | `<rect fill="#010101" fill-opacity="0.2" …/>` — correct |

Fluent's light-theme `ButtonBackground` **is** `SolidColorBrush #33000000`, so a naive capture renders
every Button as a black slab with its label invisible on top. §6 is the fix and the lint. **Ship the
lint in the same commit as the first capture** — this is precisely the class of defect that gets
noticed six months later in a screenshot nobody re-read.

### 0.4 Two decisions already made by the owner — do not re-open

- **Popup/menu figures: yes**, build the support (§3.4).
- **Synthetic window frames: yes** (§3.5).
- **The slide deck shares the doc content** — one source tree, two backends (§8).

---

## 1. Placement

| Piece | Where | Why |
|---|---|---|
| Figure catalog, fixtures, the render call | `src/Ui/Diagnostics/UiArtworkGenerator.cs` (+ `Fixtures/`) | Needs internal views, `SkiaFonts`, the Avalonia asset loader — same reasoning that put `SymbolArtworkGenerator` there |
| Headless bootstrap, Markdown pipeline, emit backends | **`tools/DocGen/`** | Keeps `Avalonia.Headless` off the shipping UI assembly |

`tools/DocGen` references `CircuitRF.Ui` + `Avalonia.Headless` + `SkiaSharp`. **It is NOT added to
`circuitrf.slnx`** — exactly like `tools/IconGen`, so a plain `dotnet build` neither builds it nor
restores its dependencies. It is a build tool, not one of the deliberately-independent programs in
`tools/`, so referencing `CircuitRF.Ui` is correct here.

Add `InternalsVisibleTo("CircuitRF.DocGen")` to `src/Ui` only if a fixture genuinely needs an internal
type; try public API first.

---

## 2. DF1 — the capture core

`src/Ui/Diagnostics/UiArtworkGenerator.cs`:

```csharp
public static string RenderVisual(Control content, int w, int h, ColorVariant variant, string path);
```

- Hosts `content` in a `Window`, shows it, pumps the dispatcher, measures/arranges at `w × h`, pumps
  again, renders to `SKSvgCanvas.Create(new SKRect(0,0,w,h), stream)`.
- **Both theme variants**: set `Application.RequestedThemeVariant` **and** circuitRF's own
  `ColorVariant` (the Skia canvases read the latter — see `color-themes.md`). Getting one and not the
  other gives a dark dialog with a light Smith chart.
- Emits `name.svg` / `name-dark.svg`, matching the convention `SymbolArtworkGenerator` and the docs CSS
  (`.sym-light` / `.sym-dark`) already use.
- **Bounding a low-level child** — a panel or a card captured without its parent — is `Width`/`Height`
  on the content (or a fixed `Border` wrapper) before `Arrange`. Every catalog row states an explicit
  capture size for this reason; there is no "natural size" fallback.

A capture that produces an SVG with **no drawing elements** is an error, not an empty figure. Throw.

---

## 3. DF2 — the figure catalog and its fixtures

### 3.1 The catalog

Mirror `SymbolArtworkGenerator.Catalog` — one row per figure, adding a figure is one row:

```csharp
(Id: "em-setup-editor", Build: Fixtures.EmSetup, Size: (980, 680), Chrome: WindowFrame.Titled("EM Setup"))
```

`Id` is the file stem **and** the `{{ui: …}}` placeholder key used by the content brief. Ids are a
contract: renaming one breaks a page.

### 3.2 Fixtures are the real work — budget for it

A capture of an empty editor teaches nothing. Each figure needs the UI in a **realistic state**: a real
`EmSetupDocument` with a stackup and ports, a real `DataSet` behind a trace card, a real schematic
behind the canvas.

**Build fixtures from the shipped example workspaces (`circuitRF_demo/`, the hero circuits) wherever
possible**, not by hand-constructing view-models. Hand-built fixtures rot silently as the model evolves;
a fixture that loads a real document fails loudly. One loaded workspace should serve many figures.

Where a figure needs simulated data (a HB spectrum, a loadpull contour, an EM S-parameter sweep), run
the real analysis in the fixture and cache the resulting `DataSet` to `testdata/` so regeneration is not
a multi-minute solve. State the cache path in the fixture's header comment.

### 3.3 Full-window captures

The content brief asks for whole-workspace figures (a workspace with a layout document open, the Data
Display with a plot). Capture the real `WorkspaceWindow` visual tree at a stated size. Docking layout
must be set up by the fixture, not left to whatever the last session saved.

### 3.4 Popups, menus and flyouts — **required**

An open dropdown, context menu or flyout lives in a **separate top-level** and is not in the window's
visual tree, so a naive capture silently omits it. Support it one of two ways (pick one, document which):

- force overlay-hosted popups so they land in the same tree and are captured with the window; or
- capture the popup host separately and composite it into the parent figure at its screen offset.

Either way there must be a test that a figure declaring a popup actually contains the popup's content —
"the menu silently didn't render" is otherwise indistinguishable from "the menu is closed".

### 3.5 Synthetic window frames — **required**

Native title bars are not in the visual tree. Draw a **synthetic frame** (title bar, title text, three
dots for the traffic lights/controls) around figures declaring `WindowFrame.Titled(...)`. This renders
identically on all three platforms, which is better than shipping whichever OS the author happened to
use. Keep it neutral — it must not read as an imitation of a specific OS.

---

## 4. DF3 — upgrade the symbol generator

`src/Ui/Diagnostics/SymbolArtworkGenerator.cs` exists and works; extend it, do not replace it.

### 4.1 Draw connection leads and pins

Today `DrawSymbol` draws primitives only — **pins are drawn by the schematic render loop's
`DrawPortMarkers`, not by `DrawSymbol`**, so the current symbol SVGs have no pin markers at all. Change
the generator to:

- include the **connection leads** (the stubs from the body to each pin location), and
- draw each pin with `DrawPortMarkers`' **unconnected** appearance (`theme.UnconnectedPin` — the
  `unconnPaint` argument, not `connPinPaint`).

The user must see what they will actually place on a schematic before anything is wired to it. Widen the
glyph box if leads now overflow it; the bbox fit is already computed from the primitives, so feed it the
lead endpoints too.

### 4.2 Add every missing component

Measured against `SymbolKind` today, the catalog is missing **fifteen real components**:

```
Diode  FetAngelov  FetCurtice  FetCurticeCubic  FetMaterka  FetStatz
Match  MBend  MCross  Mklopf  Mlin  Mtaper  MTee  VerilogA  WBond
```

(`Generic` and `Unknown` are not user-placeable — skip them.)

**Also:** `docs/user/assets/symbols/fet.svg` and `fet-dark.svg` exist but are produced by nothing —
hand-made and stale. Delete them once the five FET kinds generate properly, and fix any page that
referenced them.

### 4.3 The regeneration is expected to rewrite every existing symbol SVG

Adding leads and pins changes all of them. That is intended. Do not try to keep the old files stable.

---

## 5. DF4 — toolbar figures, generated

The content brief documents the toolbars of the **Schematic, Symbol, Layout, Data Display and wBond**
editors, button by button. These change over time, so **generate the images**, do not hand-draw them.

- Capture the real toolbar control out of each view (`SchematicView.axaml`, `SymbolEditorView.axaml`,
  `LayoutEditorView.axaml`, `DataDisplayView.axaml`, the wBond views), bounded to a plausible parent
  width per §2.
- Also emit, per toolbar, a **machine-readable manifest** (`toolbar-schematic.json`: ordered list of
  button id, tooltip, icon name, command name) so the content brief's per-button table is generated from
  the same source as the picture and cannot drift out of order with it. Read the tooltips/commands off
  the live control; do not re-type them.
- Emit an **indexed variant** (small numbered callouts beside each button) so prose can say "3 — Rotate"
  and stay correct when a button is inserted.

If a button has no tooltip, that is a UI bug worth reporting in `RESOLVED.md`, not a blank table cell.

---

## 6. SVG post-pass and lint — ship with the first capture

A post-pass over every emitted SVG:

1. **Dropped-paint lint (blocking).** Any `<path>`/`<rect>`/`<ellipse>` with neither `fill` nor `stroke`
   is a suspected dropped paint (§0.3). Fail generation and name the file and element.
2. **Black-alpha remap (the fix).** Merge a docs-only `ResourceDictionary` in the generator that
   re-points pure-black-with-alpha theme brushes (`#..000000`) to `#..010101`. Visually identical, one
   bit of red, survives Skia's serializer. `ButtonBackground` is the known one; find the rest by running
   the lint over a capture of every catalog figure.
3. **Size reduction.** The probe emitted ~23 KB for a 320 × 480 panel — **27 clip paths for six
   controls** (Avalonia emits a clip per control) plus full-precision coordinates. Drop no-op clips (a
   clip equal to its parent's), round coordinates to 2 dp, dedupe identical path data. Expect 3–5×.
   **This matters because `CircuitRF.Ui.csproj` copies `docs/user/**` into the app output — doc size is
   app size.** Report the before/after total in `RESOLVED.md`.

---

## 7. DF5 — fonts, and the Markdown → HTML pipeline

### 7.1 Fonts: use the app's own, do not substitute

`SKSvgCanvas` emits `<text>` with a `font-family` reference, not outlines. circuitRF already ships every
typeface it draws with, all redistributable:

| Where used | Family | Source |
|---|---|---|
| Avalonia chrome | **Inter** | embedded in `Avalonia.Fonts.Inter` (`.WithInterFont()`) |
| Our Skia canvases | **IBM Plex Sans** | `src/Ui/Assets/Fonts/IBM_Plex_Sans/` (OFL) |
| Plot/scientific text | **DejaVu Sans** | `src/Ui/Assets/Fonts/DejaVuSans*.ttf` |

**Do this:** the generator **extracts the TTFs through the asset loader** (`avares://Avalonia.Fonts.Inter/…`
and `avares://CircuitRF.Ui/Assets/Fonts/…`) and writes them to `docs/user/assets/fonts/` on every run,
copying `OFL.txt` and `DejaVu Fonts License.txt` alongside. The docs' webfonts are then literally the
same bytes the app renders with and cannot drift. Add the `@font-face` rules to
`assets/css/circuitrf-docs.css`.

**Inline the figure SVGs into the generated HTML** (an `<svg>` element, not `<img src>`), so the page's
`@font-face` applies. A standalone SVG referenced by `<img>` cannot load the page's fonts, and data-URI
fonts inside an SVG-as-image are unreliable in Safari — which is the default browser `DocLauncher` opens
on macOS. If a figure genuinely must be a standalone file, convert its text to outlines instead.

Mitigating detail if you see substituted text anywhere: Skia writes a **per-glyph `x` list**, so glyphs
stay pinned to their laid-out positions — substitution changes letterforms, not layout.

### 7.2 Content source and placeholders

Prose lives in **`docs/user/src/**.md`** with YAML front-matter (`title`, `kind`, `breadcrumb`, `slug`,
`anchors`). The generator expands typed placeholders:

| Placeholder | Expands to |
|---|---|
| `{{ui: em-setup-editor}}` | the light/dark figure pair, inline, framed, with caption |
| `{{symbol: resistor}}` | the generated symbol figure |
| `{{toolbar: layout}}` | the toolbar figure **and** its generated per-button table (§5) |
| `{{table: components/Resistor}}` | a parameter table read from the **live component registry** |
| `{{anchor: components#sdd}}` | a checked cross-link — unresolvable target fails generation |

Registry-driven tables are the second big drift kill after figures: component parameters, defaults and
units, the analysis list, plot types, measurement names and the expression function list are all facts
the code already knows and the prose currently re-types.

**Unknown placeholder ⇒ generation error.** A typo'd `{{ui: em-setup}}` must never render as literal
text on a shipped page.

### 7.3 HTML emit

Preserve today's look exactly — the existing `circuitrf-docs.css`, the `doc-header`/brand block, the
breadcrumb, the `prefers-color-scheme` dark handling, the figure frame. This brief changes **how** pages
are produced, not what they look like.

**The anchor contract is load-bearing:** `DocLauncher` deep-links to
`reference/components.html#<symbolkind-lowercase>`, `reference/simulations.html#<analysis>`,
`reference/plot-types.html#<type>`. Emit exactly those anchors and **test that every anchor
`DocLauncher` can produce exists in the emitted HTML** (§10.3).

### 7.4 Migration is incremental

Pages not yet ported to Markdown are **copied through untouched**. Port a page when its content is being
changed anyway — which, under the content brief, is most of them.

---

## 8. DF6 — slides as landscape PDF, from the same content

`SKDocument.CreatePdf(stream)` gives a canvas per page exactly as `SKSvgCanvas` gives one per figure —
no new dependency, and **Skia embeds the fonts**, so the PDF is correct everywhere with no `@font-face`
question.

- The deck **shares the doc content** (owner decision): a source file with `kind: slides` in its
  front-matter, one slide per `##` heading, rendered through the same placeholder expansion.
- Page size 13.33 × 7.5 in landscape (16:9). Vector throughout, including captured UI figures and any
  Smith chart.
- **Overflow is a generation error, not a silent clip.** There is no browser to reflow a slide; if
  content does not fit the template, say so and name the slide.

---

## 9. Invocation and the app's own hook

- Keep `dotnet run --project src/Ui -- --generate-symbols <dir>` working — it is referenced from
  `docs/user/reference/components.html` and from `SymbolArtworkGenerator`'s own header.
- `tools/DocGen` calls the same generator rather than reimplementing it.
- **Never wire generation into `dotnet build`.** It stays a deliberate command.
- Every generated file carries a banner comment naming the command that regenerates it.

---

## 10. Tests and gates (`tests/Ui.Tests`)

1. **Every catalog figure renders.** For each row, in both variants: the SVG exists, is non-empty, and
   contains ≥ 1 drawing element. This is the test that catches "a XAML refactor broke headless capture";
   without it the failure mode is a blank box on a page nobody re-reads.
2. **Dropped-paint lint** over every emitted file (§6.1).
3. **Anchor contract**: every deep-link `DocLauncher` can emit resolves in the generated HTML.
4. **Placeholder coverage**: no unexpanded `{{…}}` survives in any emitted page.
5. **Toolbar manifest ↔ table**: the generated per-button table has exactly one row per manifest entry,
   in order.
6. **Symbol catalog completeness**: every user-placeable `SymbolKind` has a catalog row. This is what
   stops the next new component from quietly missing its documentation — hard-code the two exclusions
   (`Generic`, `Unknown`) so adding a kind fails the test until someone decides.
7. Measure total generation wall-clock and the total emitted byte count; record both in `RESOLVED.md`.
   If generation exceeds ~60 s, say so plainly rather than letting it become normal.

CI: regenerate and `git diff --exit-code`, so a hand-edit of a generated page fails rather than being
silently reverted on the next run.

---

## 11. Do not

- Do not emit any **bitmap** into `docs/user/`. If a figure can only be produced as a raster, stop and
  report it rather than shipping a PNG.
- Do not hand-draw a toolbar, a symbol, or a dialog mock-up. If it is in the app, capture it.
- Do not put prose in C# string literals. The pipeline is C#; the words are Markdown.
- Do not chase pixel-exactness. Close approximation is the bar; §6's lint exists so "close" never means
  "visibly wrong".
- Do not add `Avalonia.Headless` to `src/Ui/CircuitRF.Ui.csproj`.

---

## 12. Deliverables

- [ ] `tools/DocGen/` — headless bootstrap, Markdown pipeline, HTML + PDF backends, not in the `.slnx`
- [ ] `src/Ui/Diagnostics/UiArtworkGenerator.cs` + fixtures, both theme variants, synthetic frames, popups
- [ ] `SymbolArtworkGenerator` upgraded: leads, unconnected pins, **15 missing components**, stale
      `fet.svg` removed
- [ ] Toolbar figures + manifests for Schematic, Symbol, Layout, Data Display, wBond
- [ ] Fonts extracted to `docs/user/assets/fonts/` + `@font-face` in the docs CSS + licences copied
- [ ] SVG post-pass: dropped-paint lint (blocking), black-alpha remap, size reduction with a
      before/after number
- [ ] Slides backend: shared content, 16:9 landscape PDF, overflow is an error
- [ ] Tests §10 (1)–(7) green; `Ui.Tests` + `Firewall.Tests` green
- [ ] `src/Ui/RESOLVED.md` updated with anything found — especially any further Skia-SVG paint
      omissions beyond the black-alpha case
