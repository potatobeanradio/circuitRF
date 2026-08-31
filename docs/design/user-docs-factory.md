# circuitRF — User-Docs Factory: vector UI capture + one-command doc generation

**Status:** BUILT — rev 1 · **Date:** 2026-08-20 · **Phase:** cross-cutting (docs tooling)

> **Implemented 2026-08-20** as `tools/DocGen` plus `src/Ui/Diagnostics/`
> (`UiArtworkGenerator`, `FigureCatalog`, `Fixtures/`, `ToolbarCatalog`, `SvgLint`, `SvgPostPass`,
> `DocsPaintRemap`, `DocAnchors`, `DocTables`), per
> `docs/sonnet-briefs/brief-docs-factory-infrastructure.md`. **What this note got wrong, and the
> traps it did not anticipate, are recorded in `src/Ui/RESOLVED.md` § User-Docs Factory** — read that
> before changing any of it. In particular §3.1 here understates the black-alpha problem (it is not
> only alpha, and not only brushes), §3.4's 3–5× size estimate came out at 2.12×, and §5.2's advice
> to build fixtures from `circuitRF_demo/` cannot be followed because that directory is git-ignored.
> The five §9 decisions were all taken: inline SVG, popup figures yes, synthetic frames yes, Markdown
> is the prose source, and the deck shares the doc content.

This document answers three questions the owner raised together, because they are one system:

1. Can we render **live Avalonia UI** (a dialog, the EM Setup editor, a panel) to **SVG**, so user-doc
   figures regenerate from the app instead of being screenshotted by hand?
2. Can the fonts be **right** — the app's own typefaces, not a browser substitution?
3. Is a **"user-docs factory"** — one program that regenerates every image *and* every page — the right
   shape for docs that must track a moving app, including a second output format (landscape PDF slides)?

Short answers: **yes**, **yes**, and **yes with one structural amendment** — the factory should own the
*pipeline*, not the *prose*. Details below. Companions: `ui-architecture.md` (the firewall),
`color-themes.md` (light/dark variants), `standard-library-symbols.md` (the existing symbol generator,
which is the precedent this generalises).

Everything in §3–§5 was **measured** in a throwaway probe (headless Avalonia 12.0.3 + SkiaSharp 3.119.4,
macOS, .NET 10), not inferred from documentation. The probe is disposable scratch work and is not in the
repo; the findings are reproducible from the recipe in §3.1.

---

## 1. What exists today

- **`SymbolArtworkGenerator`** (`src/Ui/Diagnostics/`) already does this for *component symbols*:
  `dotnet run --project src/Ui -- --generate-symbols docs/user/assets/symbols` renders every
  `SymbolKind` to a light and a dark SVG straight from `SchematicRenderer.DrawSymbol`. It works because
  our schematic renderer draws into an `SKCanvas`, and `SKSvgCanvas` *is* an `SKCanvas` — the renderer
  never knew it was writing vectors.
- **`docs/user/`** — 18 hand-written HTML pages (~3,300 lines), one shared stylesheet, a consistent
  header/breadcrumb/figure structure, light/dark via `prefers-color-scheme` plus paired
  `.sym-light`/`.sym-dark` images.
- **`DocLauncher`** serves those pages over loopback HTTP to the system browser and deep-links via a
  stable anchor contract (`reference/components.html#<symbolkind-lowercase>`). The docs are copied into
  the app output by `CircuitRF.Ui.csproj`, so **doc size is app size.**

The gap: symbols are generated, everything else is hand-made. `docs/images/` holds four **PNG**
screenshots, which is exactly the thing the owner does not want in the docs.

---

## 2. The core enabler

`Avalonia.Skia` exposes a **public** entry point that renders any laid-out visual into an `SKCanvas`:

```csharp
Avalonia.Skia.Helpers.DrawingContextHelper.RenderAsync(SKCanvas canvas, Visual visual);
```

Point it at an `SKSvgCanvas` and the entire visual tree — theme chrome, text, borders, clips — comes out
as vector SVG. Verified against Avalonia 12.0.3: compiles, runs, produces correct output.

### 2.1 The recipe

```csharp
AppBuilder.Configure<DocsApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })  // ← real Skia
    .WithInterFont()
    .SetupWithoutStarting();

var window = new Window { Width = w, Height = h, Content = BuildFixture() };
window.Show();                              // needed: templates/styles apply via the visual root
Dispatcher.UIThread.RunJobs();
window.Measure(new Size(w, h));
window.Arrange(new Rect(0, 0, w, h));
Dispatcher.UIThread.RunJobs();

using var stream = new SKFileWStream(path);
using var canvas = SKSvgCanvas.Create(new SKRect(0, 0, w, h), stream);
DrawingContextHelper.RenderAsync(canvas, window);
```

**`UseHeadlessDrawing = false` is load-bearing.** The default headless platform stubs out drawing
entirely; without this flag `RenderAsync` produces an empty document.

### 2.2 What the probe confirmed works

| Concern | Result |
|---|---|
| Fluent-themed controls (TextBox, ComboBox, CheckBox, Button, ScrollBar) | Render as vectors — rounded-rect paths, chevron/tick glyph outlines, correct theme colours |
| **ScrollViewer** | Clipped to its own box, **scrolled to top**, off-box children culled, thumb drawn at top — exactly the semantics the owner asked for, with no special handling |
| Bounding a low-level child to a plausible parent size | Set `Width`/`Height` (or wrap in a fixed `Border`) before `Arrange` — same mechanism |
| Light **and** dark | Set `Application.RequestedThemeVariant` and re-render; pairs with the existing `-dark.svg` convention |
| **Our own Skia canvases** (schematic, layout, Smith chart, wBond profile — all `ICustomDrawOperation` + `ISkiaSharpApiLease`) | **Render into the SVG as vectors.** The lease hands the custom draw op the real `SKCanvas`, which here is the SVG recorder. A full window capture that *includes* a schematic or a Smith chart is vector end to end. |
| Gradient brushes | Emitted correctly as `<defs><linearGradient>` |
| Box shadows, corner radii, opacity | Emitted correctly |

That last row is the one that makes this worth doing: capturing the EM Setup window, the Data Display,
or the layout editor gives a **single vector figure** containing both the Avalonia chrome and our own
rendered content, at any zoom, in both themes.

---

## 3. Fidelity limits found, and what to do about them

None of these block the approach; all have cheap fixes. They are written down because each fails
*silently* — a wrong figure, not an error.

### 3.1 Pure-black-with-alpha loses its opacity (renders as an opaque black slab)

**The finding.** Skia's SVG device omits `fill` when the colour is black, treating it as the SVG default
— and drops `fill-opacity` with it. Measured directly:

| Brush | Emitted |
|---|---|
| `#33000000` (black, 20 %) | `<rect …/>` — **no `fill`, no `fill-opacity`** → renders opaque black |
| `#33010101` (one bit off black, 20 %) | `<rect fill="#010101" fill-opacity="0.2" …/>` — correct |

This is not academic: **Fluent's `ButtonBackground` in the light theme is `SolidColorBrush #33000000`**,
so every Button in a naive capture is a black slab with its label invisible on top. Setting any
non-black background made the same Button render correctly.

**Fix.** A docs-only `ResourceDictionary`, merged by the generator only, that re-points the handful of
pure-black-with-alpha theme brushes to their `#010101` equivalent (visually identical, one bit of red).
Plus a **lint on the emitted SVG**: any `<path>`/`<rect>` with neither `fill` nor `stroke` is a
suspected dropped paint and fails generation. The lint is what keeps this from silently regressing when
Avalonia's theme changes.

### 3.2 Popups, flyouts, menus and ComboBox dropdowns are separate top-levels

An open dropdown or context menu is not in the window's visual tree, so it is not captured. If a figure
needs one, either force overlay-hosted popups (so they land in the same tree) or capture the popup host
separately and composite. Worth deciding before promising "here is the File menu" figures.

### 3.3 Window chrome is the OS's, not ours

Native title bars are not in the visual tree. A figure that should look like a window needs a synthetic
frame drawn by the generator (which is arguably better — it renders identically on all three platforms
instead of showing whichever OS the author was using).

### 3.4 File size

The probe emitted ~23 KB for a 320×480 panel with eight controls — dominated by **27 clip paths for six
controls** (Avalonia emits a clip per control) and by unrounded coordinates. A 1200×800 window will be a
few hundred KB, and **these files ship inside the app bundle**. A small post-pass — drop no-op clips
(clip equal to the parent's), round coordinates to 2 dp, dedupe identical paths — should take 3–5× off.
Budget it in from the start rather than discovering it at packaging time.

---

## 4. Fonts — yes, exactly the app's own

This has a clean answer because **circuitRF already ships every typeface it renders with**, all
redistributable:

| Where | Family | Source | Licence |
|---|---|---|---|
| Avalonia control chrome | **Inter** | embedded in `Avalonia.Fonts.Inter` (`.WithInterFont()`) | SIL OFL |
| Our Skia canvases (plots, Smith, layout) | **IBM Plex Sans** | `src/Ui/Assets/Fonts/IBM_Plex_Sans/` | SIL OFL (`OFL.txt` present) |
| Plot/scientific text | **DejaVu Sans** | `src/Ui/Assets/Fonts/DejaVuSans*.ttf` | DejaVu licence (`DejaVu Fonts License.txt` present) |

**The problem to solve.** `SKSvgCanvas` emits `<text>` with a `font-family` reference, *not* outlines —
the existing symbol SVGs already do this (`font-family="IBM Plex Sans SemiBold, IBM Plex Sans"`). With
one letter per symbol nobody noticed; a UI capture has hundreds of words, and a reader without those
fonts installed gets substituted letterforms. (Mitigating detail, measured: Skia writes a **per-glyph
`x` list**, so every glyph stays pinned to its laid-out position — substitution changes letter *shapes*,
not the layout. It looks wrong, it does not collapse.)

**Three fixes, ranked.**

1. **Inline the SVG into the generated HTML page and serve the fonts via `@font-face`** — recommended.
   The pages are generated anyway, so inlining is free; the page's CSS supplies the exact family from
   `docs/user/assets/fonts/`, and the fonts are cached once for the whole doc set (Inter Regular is
   ~218 KB as TTF, less as WOFF2 — a subset would be far smaller). Fidelity is exact. It also unlocks
   per-figure CSS (a theme swap without shipping two copies of large captures). Cost: no `<img>` reuse
   of the same figure across pages, and larger HTML.
2. **Self-contained SVG with a base64 font subset in `<defs><style>`** — keeps `<img>` usage and works
   standalone. Costs KB per file unless the subset is tight, and data-URI fonts inside an SVG loaded as
   an *image* are honoured by Chrome/Firefox but have been unreliable in Safari — which matters here,
   because `DocLauncher` opens the **system default browser** (Safari on macOS). Verify before adopting.
3. **Convert text to outlines** in a post-pass (SkiaSharp can produce glyph paths; the emitted `<text>`
   carries family, size and per-glyph positions, so re-shaping is deterministic for the Latin/`µΩΓ`
   range the UI uses). Universally correct with no font dependency, but files grow and the text stops
   being selectable or searchable. Keep as the fallback for figures that must stand alone.

**The fonts should be copied by the generator, not by hand.** It can pull the Inter TTFs out of
`avares://Avalonia.Fonts.Inter/…` and the Plex/DejaVu TTFs out of `avares://CircuitRF.Ui/Assets/Fonts/…`
and write them to `docs/user/assets/fonts/` on every run — so the docs' webfonts are literally the same
bytes the app renders with, and cannot drift. Ship the two licence files alongside them.

**The PDF path needs none of this**: Skia's PDF backend embeds the fonts it draws with (§6.2).

---

## 5. The factory — shape, and one amendment

The owner's instinct is right: **one program, one command, every artefact.** The amendment is about
scope.

> **Generate the pipeline and the machine-known facts. Do not generate the prose.**

A generator that owns prose means editing C# string literals to fix a sentence — worse than editing the
HTML we have now, and it locks the docs to whoever can build the solution. But a generator that owns
*layout, chrome, figures, tables, cross-links and formats* removes exactly the drift that hand-editing
cannot prevent.

### 5.1 Three layers

**A. Capture** (`src/Ui`, needs internals + the asset loader)
A catalog of figures, in the same spirit as `SymbolArtworkGenerator.Catalog`:

```csharp
(Id: "em-setup-editor",  Build: Fixtures.EmSetup,   Size: 980x680, Variants: LightDark)
(Id: "trace-card",       Build: Fixtures.TraceCard, Size: 320x420, Variants: LightDark)
```

Each row is *(id, fixture factory, capture size, variants)*. Adding a figure is one row — the property
that has kept the symbol generator alive.

**B. Content** (`docs/user/src/*.md`, hand-written)
Markdown with front-matter (title, kind, breadcrumb, anchors) and **typed placeholders** the generator
expands:

| Placeholder | Expands to |
|---|---|
| `{{ui: em-setup-editor}}` | the light/dark figure pair (inline SVG + caption + frame) |
| `{{symbol: resistor}}` | the existing generated symbol figure |
| `{{table: components/Resistor}}` | a parameter table read from the **live component registry** |
| `{{anchor: components#sdd}}` | a checked cross-link |

The registry-driven tables are the second big drift kill after the figures: component parameters,
defaults and units, the analysis list, plot types, measurement names and the expression function list
are all facts the code already knows and the prose currently re-types by hand.

**C. Emit** — one content tree, several backends:
- **HTML** into `docs/user/` with today's header/breadcrumb/CSS chrome, preserving the
  `components.html#<symbolkind>` anchor contract that `DocLauncher` depends on.
- **Slides → landscape PDF** (§6.2).
- Anything later (a single-page printable manual, in-app help) is another backend, not another pipeline.

### 5.2 The real cost is fixtures, not rendering

The renderer is a few hundred lines and a weekend. **The work is putting the UI into a photogenic
state**: a capture of an empty EM Setup editor teaches nothing, so each figure needs a fixture — a real
`EmSetupDocument` with layers and ports, a real `DataSet` behind a trace card, a real schematic behind
the canvas. That is where the effort goes, it is ongoing, and it is the part that gets under-estimated.

Mitigation: fixtures should be **built from the shipped example workspaces** wherever possible
(`circuitRF_demo/`, the hero circuits) rather than hand-constructed, so they stay valid as the model
evolves and one fixture serves many figures.

---

## 6. Output formats

### 6.1 HTML

Unchanged from today's look — the existing stylesheet, header, breadcrumb and `prefers-color-scheme`
dark handling are good and should be preserved, not redesigned. Inline figures per §4.

### 6.1a Search — a generated index and a hand-written reader

Added 2026-08-31. Every page carries a search box at the right of its header, and the landing page
carries a wide one between the three guide cards and the prose.

**The index is emitted as a SCRIPT, not as JSON.** The docs are read three ways — over the loopback
server `DocLauncher` starts for the Help menu, from a web host, and by opening a page straight off
disk — and only a classic `<script src>` works in all three. A `fetch()` of a sibling `.json` is
blocked by every browser's `file://` origin rules, so a search built that way would work everywhere
except the offline case §6.1's stylesheet header promises.

**A section, not a page, is the unit.** Every `h2`/`h3` that carries an id becomes one record with
its own text, so a result deep-links to the anchor the reader wants rather than to the top of a
30-screen Reference page. The extraction runs over the RENDERED body, which is what makes generated
content searchable: a parameter table, a per-button table and a figure caption are all produced by a
placeholder and appear nowhere in the Markdown source.

Two things are deliberately removed before indexing — inlined `<svg>` figures (hundreds of kilobytes
of path data each, and not one readable word) and the generated site contents (`{{toc: site}}`, which
is every other page's blurb; left in, the two contents pages answer almost any query).

| Half | File | Owned by |
|---|---|---|
| Index | `docs/user/assets/js/search-index.js` | generated — `tools/DocGen/Pipeline/SearchIndex.cs` |
| Reader | `docs/user/assets/js/docs-search.js` | hand-written, no dependency, no build step |
| Markup | both boxes | `HtmlEmitter.SearchBox`, so they cannot drift |
| Style | `[SEARCH]` block in `circuitrf-docs.css` | hand-written |

Ranking is: every query word must appear somewhere in a section, worth most in the page title, less
in the heading, least in the body, with a bonus for the whole query as a phrase — then multiplied by a
15% prior that decays along the **reading order**, so a genuine tie ("Hierarchy" heads a section in
both editors) is settled by the order the documentation itself puts them in rather than by the order
the file system enumerated them. Measured at 0.14 ms per query over 458 sections.

### 6.2 Slides as landscape PDF — same engine, no new dependency

`SKDocument.CreatePdf(stream)` gives a canvas per page, the same way `SKSvgCanvas` gives one per figure.
A slide is a fixed-size page — 1920×1080 pt, or 13.33×7.5 in — which is exactly what that API wants. So:

- Author a slide deck as the same Markdown content with a `kind: slides` front-matter and one slide per
  `##` heading, or as an Avalonia slide template rendered through the identical `RenderAsync` path.
- Render each slide to an `SKDocument` PDF page; **Skia embeds the fonts**, so the PDF is correct
  everywhere with no `@font-face` question at all.
- Vector throughout, including any captured UI figure and any Smith chart on a slide.

The alternative — HTML slides printed by headless Chrome — adds a browser to the build for no gain.
Reject it.

Caveat: text *flow* on a slide is on us (no browser to reflow). Keep slide templates simple and fixed —
title, bullets, one figure — and let overflow be a generation error rather than a silent clip.

> **Extended 2026-08-24.** Four decks now exist, each selectable and each rendered in both colour
> variants; overflow is still a generation error, but an auto-fit ladder of body sizes runs in front
> of it. What that cost, and the four traps in it, are in `src/Ui/RESOLVED.md` § *Four slide decks,
> light and dark, off the same content tree*. The decks and their markup:
>
> | `--deck` | Source | Audience |
> |---|---|---|
> | `overview` | `docs/user/src/slides/circuitrf-overview.md` | Deciding whether to adopt circuitRF at all |
> | `new-user` | `…/circuitrf-new-user.md` | New to circuit simulation, from first principles |
> | `quick-start` | `…/circuitrf-quick-start.md` | Already uses simulators; wants the differences |
> | `reference` | `…/circuitrf-reference.md` | The Reference Guide in outline, chapter by chapter |
>
> A slides source declares its id in front-matter (`deck: overview`) rather than having it derived
> from the file name: the file name is the PDF a reader sees, the id is what a script types, and the
> two want to change independently. The light/dark pair is named by the same
> `UiArtworkGenerator.FileStem` convention the figures use — `circuitrf-overview.pdf` and
> `circuitrf-overview-dark.pdf`.
>
> Beyond `##` per slide, the backend understands `#` (a full-bleed section divider), `###` (a
> sub-head), indented `-` (a sub-bullet), `> **Label** text` (a callout band), a ``` fence (a command
> band), `{{ui: id}}` / `{{ui: id | full}}`, `{{caption: …}}` and `{{stats: 4::analyses | …}}`, plus
> inline `**bold**` and `` `code` ``. `SlideEmitter`'s own summary is the reference.

---

## 7. Where the code lives, and how it is gated

**Placement.** The capture layer needs internal views, the `SkiaFonts` asset path and a live Avalonia
app host, so it belongs with `src/Ui` — the same reasoning that put `SymbolArtworkGenerator` there. But
it also needs `Avalonia.Headless`, which should **not** become a dependency of the shipping UI assembly.
Recommended split:

- `src/Ui/Diagnostics/UiArtworkGenerator.cs` — the catalog, the fixtures and the render call (no
  headless reference; it takes an already-initialised app).
- `tools/DocGen/` — the headless bootstrap, the Markdown pipeline and the emit backends; references
  `CircuitRF.Ui` and `Avalonia.Headless`; **not** in `circuitrf.slnx`, exactly like `tools/IconGen`, so
  a plain `dotnet build` neither builds it nor restores its dependencies.

Note this is a *build tool*, like `IconGen` — not one of the deliberately-independent programs in
`tools/` that exist to be tested against, so referencing `CircuitRF.Ui` is correct here.

**Invocation.** One command, mirroring the existing one:

```
dotnet run --project tools/DocGen -- --out docs/user            # HTML + figures + fonts
dotnet run --project tools/DocGen -- --slides docs/slides       # every deck, light and dark
dotnet run --project tools/DocGen -- --slides docs/slides --deck overview            # one deck
dotnet run --project tools/DocGen -- --slides docs/slides --deck overview --theme dark
```

**Gates.** Generated output must be regenerable and checked, or it drifts the other way — someone
hand-edits a generated page and the next run silently reverts it.

1. A `Ui.Tests` test that the figure catalog renders **every** entry to a non-empty SVG containing at
   least one drawing element. This is what catches "a XAML refactor broke headless capture" — otherwise
   the failure mode is a blank box in the docs that nobody looks at.
2. The dropped-paint lint of §3.1, run over every emitted file.
3. A cross-link check: every anchor `DocLauncher` deep-links to exists in the emitted HTML.
4. CI: regenerate and `git diff --exit-code`.
5. A banner comment in every generated file saying which command regenerates it.
   **The output is byte-reproducible as of 2026-08-31** — verified by three consecutive full runs —
   so `git diff --exit-code` is a usable gate rather than a permanently red one. What made it not
   reproducible was the animation clock: Avalonia advances it from the RENDER TIMER, a headless
   process never enters a render loop, and an Expander's chevron was therefore captured at whatever
   angle wall-clock reached. `UiArtworkGenerator.SettleAnimations` now ticks the timer past every
   animation's end through the `AdvanceFrames` seam, which `tools/DocGen/HeadlessHost` fills with
   `AvaloniaHeadlessPlatform.ForceRenderTimerTick` (this assembly split is why it is a seam and not
   a call — see the placement rule above).
6. Search gates (`DocsFactoryTests`): every indexed section resolves to an anchor that exists in the
   shipped HTML; the index covers every page; it carries prose and not figure geometry; every page
   wires both scripts at the right relative depth; and the hand-written `docs-search.js` still exists
   — nothing else in this pipeline would notice it being deleted, and the symptom is a search box
   that accepts typing and does nothing.

Runtime is the open cost: a headless render per figure per variant is fast (tens of ms), but fixture
construction may not be. If total generation crosses a few seconds it stays a manual command, not a
build step — and it must never be wired into `dotnet build`.

---

## 8. Migration — incremental, never big-bang

Rewriting 3,300 lines of good HTML into Markdown up front is the way this stalls. Instead:

1. **Pipeline first, no content change.** `tools/DocGen` renders figures and copies today's HTML through
   untouched. Immediate win: vector UI figures replace `docs/images/*.png`.
2. **Port one page** — `reference/components.html` is the right first target, because its parameter
   tables are the most drift-prone content in the set and it already carries a comment pleading with
   editors to keep the anchor scheme.
3. **Port the rest opportunistically**, whenever a page is being edited anyway. Un-ported pages keep
   working because the generator copies them through.
4. **Slides last**, once the content model has settled under real use.

---

## 9. Decisions the owner needs to make

1. **Inline SVG vs standalone `<img>`** (§4). Inline is the recommendation; it costs `<img>` reuse.
2. **Do we need popup/menu figures?** (§3.2) — cheap to support if decided up front, awkward to retrofit.
3. **Synthetic window frames?** (§3.3) — yes/no changes what a "window" figure looks like everywhere.
4. **Is Markdown the prose source, or do we keep hand-written HTML forever** and generate only figures
   and tables? The second is a legitimate, much smaller project, and it still kills most of the drift.
5. **Does the slide deck share the doc content, or is it a separate authored artefact?** Sharing is
   elegant but constrains both; separate decks off the same figure catalog may be the honest answer.

---

## 10. Non-goals

- **Pixel-exact reproduction.** Close approximation is the stated bar; §3's fixes are aimed at "no
  visibly wrong figure", not at matching a screenshot bit for bit.
- **Capturing interaction** (hover, drag, animation). Static figures only.
- **A general SVG export of arbitrary Avalonia apps.** This serves circuitRF's docs; it does not need to
  handle everything Avalonia can draw.
- **Replacing the symbol generator.** It works, it is proven, and `--generate-symbols` stays — the
  factory calls the same code rather than reimplementing it.

---

## 11. Capturing the WORKSPACE — the whole window, not one view

Added 2026-08-20, after the rest of the factory was already running. Everything in §1-§9 captures one
view: a dialog, a panel, an editor. This section is about capturing the **shell those views live
in** — `WorkspaceWindow` with its menu bar, toolbar, dock tree and open documents. It is a different
problem only in what it has to arrange beforehand; the render call is the same one.

**It works, and it is worth stating why that was not obvious.** The workspace is the one figure that
depends on Dock.Avalonia's `DockControl`, on a `WorkspaceViewModel` (11,000 lines, a constructor with
real side effects), and on a workspace existing on disk. All three turned out to be fine.

### 11.1 What the capture needs, and why each is load-bearing

Each of these fails **silently** — a wrong figure, not an error — which is why each is written down.

1. **The DataContext must be set on the CONTENT, not on the `Window`.** The generator renders the
   window's content and draws its own frame (§3.3), so the fixture detaches the content — and a
   detached control loses the DataContext it inherited. The whole dock tree binds through it, so the
   first capture was a correctly-rendered toolbar above an empty grey rectangle. `DockControl`'s
   `Layout` had bound to nothing and reported nothing.

2. **`DocsApp` needs the application's DataTemplates, not just the `ViewLocator`.** The ViewLocator
   maps `XViewModel` → `XView` by name. Every Dock tool and document view-model is named nothing like
   its view (`ProjectTreeTool` → `ProjectTreeView`, `SchematicDocument` → `SchematicView`) and is
   mapped by an explicit `DataTemplate` in `App.axaml`. Without them each dock panel rendered as the
   **literal text of its view-model's type name**. `DocsApp` now copies them off a real `App`
   instance rather than restating nineteen templates: constructing `App` does not start the
   application (`Initialize` is only `AvaloniaXamlLoader.Load(this)`; it is
   `OnFrameworkInitializationCompleted` — never called — that opens windows and loads PDKs).

3. **The in-window menu bar has to be forced visible.** It carries
   `IsVisible="{OnPlatform True, macOS=False}"` because macOS puts those menus in the system menu
   bar, which is not in any visual tree. Left alone, a figure generated on macOS is missing a menu
   bar that Windows and Linux readers have on screen — and differs from one generated on Linux. The
   fixture turns it on, and the page says where macOS puts it.

### 11.2 A figure must not depend on whose machine generated it

This is the finding that generalises beyond the workspace. `WorkspaceViewModel`'s constructor reads
the real `preferences.json` and restores the PDKs installed from it, so the capture carried the
generating developer's **launch window layout** (visibly — the Library panel changed columns), their
colour scheme, and their installed kits in the palette. With `check-docs-current.sh` regenerating and
diffing, that is not cosmetic: the output would never be reproducible.

The fix is one lever. `CircuitRF.Ui.AppDataRoot` is now the single definition of the per-user state
directory — `AppPreferencesIo` and `RecoveryManager` computed `LocalApplicationData/circuitRF`
independently before — and `tools/DocGen` redirects it to a throwaway directory before it starts, so
every run sees a **first-launch installation**.

The environment cannot do this job, and this was measured rather than assumed: on macOS .NET resolves
`SpecialFolder.LocalApplicationData` to `~/Library/Application Support` from the platform, so setting
`XDG_DATA_HOME` or `HOME` in-process changes nothing.

Two smaller sources of churn are handled in the fixture itself. Message timestamps are switched to
the application's own **Hidden** mode for the capture (a real setting, not a docs hook), and the
message log — which correctly names the absolute path of the temporary workspace that was just
opened — is cleared and restated without it.

### 11.3 The workspace the figure opens

There is no example workspace tracked in this repository (`circuitRF_demo/` is git-ignored, which
§5.2 did not anticipate). The fixture therefore **writes one** into a temporary directory it deletes
afterwards: two cells — one carrying the shipped FET S-parameter schematic template, one carrying a
layout — plus the starter PCB technology, all through the real `CellPersistence` /
`SchematicPersistence` / `LayoutPersistence` / `WorkspacePersistence` writers. Only the folder
scaffolding is synthesised, which is exactly what **File ▸ New Cell** writes; the content is shipped,
tracked content, and a format change breaks the run rather than quietly producing a stale picture.

It is then driven the way a user drives it: the cell is found in the **Project panel's own tree** and
opened through `OpenCellSchematic` / `OpenCellLayout`, so primacy resolution, tab de-duplication and
active-tab bookkeeping all run as they do for a double-click.

### 11.4 Indexed figures whose numbers are REGIONS

`workspace-regions` is the numbered version, and it generalises the toolbar's indexed figure from
buttons to arbitrary parts of a window:

- `WorkspaceRegions.Catalog` is one row per numbered region — number, title, one sentence, **a
  locator, and which corner of that region the number sits in**.
- **The locator finds a real control** (`ByType<ProjectTreeView>()`, the toolbar `StackPanel` by its
  style class, `DocumentControl` for the tab strip). Never a coordinate: a dot placed at "about
  x=180" is a screenshot with extra steps — right until a panel changes width, then wrong without
  saying so. A region that cannot be found, or that arranged too small to carry its number, fails the
  run and names itself.
- The legend beside the figure is generated from the same list (`{{regions: workspace}}` →
  `DocTables.WorkspaceRegionLegend`), so the number in the picture and the number in the table cannot
  disagree.
- `CalloutDot` is the numbered dot both indexed figure kinds draw, defined once.

### 11.5 Cost

Two figures, four files (light and dark), ~280 KB each after the post-pass, and about a second of the
run. The whole regeneration is 14 s for 438 files, so the workspace capture does not change the shape
of the command: it stays a deliberate one, never a build step.
