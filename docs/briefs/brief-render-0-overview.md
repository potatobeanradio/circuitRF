# Sonnet Brief — RND-0: rendering from the command line — the series

**Six briefs.** RND-1 puts the Skia renderers below the UI firewall; RND-2 adds the `render` verb;
RND-3 adds the three questions a caller has to be able to ask *before* it can render sensibly
(what cells exist, what layers exist, how big is it); RND-4 does the same for a `.cdd`, which is a
different problem with a different cost; RND-5 advertises all of it to the protocol adapter and
rewrites the user-docs chapter.

Read `docs/design/cli.md` first — §3 (the anatomy of a verb), §3.1 (the two channels), §3.2
(`--json`), §9 (adding a verb), §11 (`serve`). Read `docs/design/ui-architecture.md` §3 (the
firewall) and `docs/design/automation-architecture.md` (R-aut-9 and R-aut-13, which constrain how
much surface this series is allowed to add).

**Scope: turning a document into a picture, headlessly.** A `.csch`, a `.csym`, a `.clay` and a
`.cdd`, as `.svg`, `.pdf` or `.png`. No editing, no simulation, no new geometry — every pixel comes
from the renderer the GUI already draws with.

---

## 1. Why this is worth doing, and what it is not

circuitRF can already run, check, explain, convert and author headlessly. It cannot show anything.
A client — a script, a CI job, an AI agent — that has just authored a layout has no way to look at
what it made, and neither does the person reading its report. The picture is the one output the
command line does not have.

**R-rnd0-1. There is one renderer, and this series does not write a second one.** `SchematicRenderer`,
`SymbolEditorRenderer` and `LayoutRenderer` are ~7,000 lines of measured, tuned Skia that already
draw every frame the application shows and already produce the SVG and PDF on the clipboard. A CLI
that re-implemented any of it would drift, and the drift would be invisible: a picture that is
*plausible* is indistinguishable from a picture that is *right*. Everything in RND-1 exists to make
the CLI call that code rather than resemble it.

**R-rnd0-2. The gate for every renderer in this series is byte identity against the GUI's own
call** — the standard `EmCliVerbTests` and `ConvertCliVerbTests` already set. Not "looks the same",
not "same dimensions": the same bytes, with a named and justified exclusion where the format itself
carries a timestamp.

**Not in scope**: an interactive viewer, an animation, a schematic-to-image service, thumbnails in
the project tree, printing, or any new visual style. If the GUI cannot draw it today, this series
does not add it.

---

## 2. The four decisions that span the series

### R-rnd0-3. A new project, `src/Render`, not a fifth thing inside `src/Design`

The three renderers are already framework-free in everything but their neighbours: they use
SkiaSharp and the design model, and `SkiaSharp` is explicitly allowed across the firewall
(`tests/Firewall.Tests`). But `src/Design`'s own `.csproj` says, in as many words, that nothing in
it *draws*. Stuffing 11,000 lines of drawing code in there would make that comment false, and a
comment that is false is worse than no comment.

So: `src/Render/CircuitRF.Render.csproj`, namespace `CircuitRF.Render`, referencing `src/Design`,
`src/Core`, `src/Engine` and SkiaSharp, referenced by `src/Ui` and `src/Cli`, and gated by the same
firewall test row every other non-UI assembly has. The layer boundary this repo already enforces —
design model down, `DataSet` up — gains a third arrow: **pixels out, and nothing in.**

### R-rnd0-4. One verb and one tool, not one per view type

R-aut-9. `check` is one verb over every document kind and `new` is one verb with a noun; `render` is
one verb over every document kind, with the kind inferred from the path exactly as `check` infers it
(`src/Cli/DocumentKinds.cs`). There is no `render-schematic`.

The same discipline applies to the protocol surface. `serve` advertises seven tools today and
`cli.md` §11.3 says the count is the point — every description is a standing per-session cost paid
whether or not the tool is called. **This series adds exactly one tool: `render`.** The three
queries in RND-3 are options on `explain`, which already exists as a tool, because they are asking
what circuitRF resolved — which is what that verb is for.

### R-rnd0-5. A number without its unit is the bug this repo has already shipped

`sweep-unit-scale-and-mark`: a loadpull ran at 2 Hz because a mark was read without its scale, and
the run looked entirely normal. A viewport is exactly the same shape of argument. So:

- **Every layout coordinate a caller passes carries an SI unit** (`--window 0,0,500um,300um`). A
  bare number on a layout is a **refusal naming the units it would accept**, never a guess about
  whether it meant DBU, µm or mm — those differ by three orders of magnitude on identical text, and
  the picture that comes back from the wrong one is a plausible picture of the wrong thing.
- **Every extent this series reports comes back in base SI with its unit AND its scale named**, the
  rule `explain --analysis` already follows (R-aut4-9).
- Schematic and symbol coordinates are dimensionless design units. That is reported as what it is,
  not dressed up in metres.

### R-rnd0-6. Anything the GUI would have asked in a dialog is a refusal naming the flag

`brief-automation-3-authoring-verbs.md`'s rule, unchanged. A cell folder holding a schematic, a
symbol and a layout is a refusal that **lists the three and names `--view`**. An output path whose
extension is not one this verb writes is a refusal listing the formats. A layer name that the
resolved technology does not define is a refusal naming `explain --layers`, which is the verb that
answers it. Nothing here defaults its way past a question the caller has not answered.

---

## 3. What each brief owns

| Brief | Owns | Depends on |
|---|---|---|
| **RND-1** — the render layer below the firewall | `src/Render`, the file moves, the fonts, the theme resolution, the firewall row | nothing |
| **RND-2** — the `render` verb | `circuitrf render`, viewport, layers, detail, resolution, `.ccolor`, progress | RND-1 |
| **RND-3** — the query surface | `explain --cells` / `--layers` / `--extents` | RND-1 (for extents) |
| **RND-4** — `.cdd` | the Data Display half, and its own carve-out | RND-1, RND-2 |
| **RND-5** — the protocol surface and the docs | `ToolCatalog`, MCP resources, `docs/user/src/reference/cli.md` | RND-2, RND-3, RND-4 |

**RND-4 is separable on purpose.** The schematic/symbol/layout renderers are framework-free already;
the Data Display's models, view models and renderers are not — twenty-two files name an Avalonia
type (6 of 15 models, 13 view models, 3 of 8 renderers), though the types they name are mostly `Rect`,
`Point` and `Color`. That is tractable but it is a
different job with a different risk, and putting it in the same brief as RND-1 would let a hard
half hold an easy half hostage. **If RND-4 turns out to be more than its own brief can hold, that is
a finding to report — not a reason to hold up `render` for a schematic.**

---

## 4. What this series must report rather than absorb

Each brief has its own list. Series-wide:

1. **Anything that had to move below the firewall beyond what RND-1 predicts.** The prediction is
   made from a measurement (RND-1 §2); a closure that is bigger than measured is the single most
   likely way this series turns into a bad trade, and the instruction in that case is to stop and
   say so, exactly as R-aut4-3 instructed for the DRC engine.
2. **Every place the headless picture differs from the GUI's**, in words, with the reason. There is
   one such difference known before the work starts (fonts — RND-1 §3) and it has a fix. A second
   one that is discovered and quietly accepted is how "byte identity" becomes "close enough".
3. **The cost of a real board.** A 6-layer import measured 3,284 shapes and 764,110 vertices
   (`LayoutRenderDetail`'s own note). Report what `render` costs on one, in time and in output-file
   size, for each format and each `--detail` setting. An SVG that is 40 MB is a fact a caller needs
   before it asks for one, not after.
