# Brief 8 — docs, figures, and the one decision left open

**Series:** [stackup rendering](brief-stackup-render-0-overview.md) · **Tag:** `R-stk8-n`
**Area:** `docs/design/layout-view.md`, `docs/user/src/reference/stackup.md`, `src/Ui/Diagnostics/`
**Depends on:** briefs 1–7

---

## 0. What this brief delivers

The prose and the pictures catch up with the tab, in **one** reviewable change rather than eight — which
is why briefs 2–7 are each told not to regenerate a figure.

---

## 1. The design note says the opposite of what is now true

### R-stk8-1

`StackupLayerRowViewModel`'s own doc comment opens with:

> VM for one row in the .ctech editor's stackup list — **an ordered top-to-bottom plain list (no
> diagram; see docs/design/layout-view.md §10.4, which is L6)**.

`docs/design/layout-view.md` §10.4 is the source of that. Both are now false. Update §10.4 to describe
the drawing as part of the tab, and fix the row VM's comment to match — a comment that states a
constraint the code no longer has is worse than no comment, and this one will be read by whoever next
touches the stackup.

While there: §10.4 is also where the **cosmetic-only** status of brief 5's `DrawLaneFraction` belongs in
prose, next to the model fields it sits among.

---

## 2. The user documentation

### R-stk8-2 — `docs/user/src/reference/stackup.md`

"Anatomy of a stackup" currently explains the cross-section as a *figure in the documentation*. It is now
a thing in the application that the reader can click. Rewrite that section so it says so, and add the
interactions in the order a new user meets them: click to select and jump to the fields, double-click a
value to change it, drag to reorder, drag a via's ends, right-click for delete / add via / plated, `Esc`
to clear, right-click ▸ Copy.

### R-stk8-3 — the height rule is explained in the PROSE, because the picture says nothing

Brief 1 `R-stk1-3`/`R-stk1-4`: heights are proportional **within a kind** where the kind's own dynamic
range allows it, compressed and monotone where it does not, never compared across kinds, and the real
number is always printed on every band.

**The drawing deliberately carries no note about any of that** — `R-stk1-4` keeps it clean — so this
chapter is the only place it is written down, which makes it load-bearing rather than optional. Two
sentences: heights are relative within conductors and within dielectrics but never between them, and on
a process whose own range is too wide to draw they are compressed while staying in order. A reader who
has just measured a 500:1 dielectric range on a real MMIC process and finds nothing in the
documentation will conclude the drawing is wrong.

If `R-stk8-7` repoints the doc figures at the shared renderer, their captions also need to change: they
currently say "Thicknesses are printed rather than drawn to scale", and within a kind they would then
partly **be** to scale. A figure CAPTION is the right place for that qualification — it sits outside
the picture, which is exactly why `R-stk1-4` refuses to put it inside one.

### R-stk8-4 — `docs/user/src/reference/mom-engine.md`, "2. Set the stackup"

That step tells the reader how to enter a stackup. It now has a picture to point at inside the
application. One or two sentences; do not restructure the walkthrough.

---

## 3. The figures

### R-stk8-5 — `tech-editor-stackup` will change, and it is meant to

`FigureCatalog` line ~179 captures the Stackup tab through `DocTechEditorFixtures.StackupTab()` at
**980 × 2080**, and the catalog's own comment explains the height: it is the height at which all nine
cards and the summary row fit with nothing clipped, because a reader cannot scroll a picture.

Brief 2 changes the tab, so this figure changes. Two things to get right:

- **The height constant has to be re-derived, not nudged.** The cards are shorter (brief 2 `R-stk2-9`)
  and the drawing is above them. Re-measure and update the comment with the new reasoning.
- **The capture must show the drawing at a useful split.** A capture taken at the default split with a
  2080-pixel window gives the drawing a share the interactive default would never produce. Set the split
  explicitly in the fixture via `FigureScene.AfterLayout`, which exists for exactly this kind of
  "needs a real size to answer" adjustment.

Update the caption: it currently describes only the cards.

### R-stk8-6 — classify the churn before reporting it

A DocGen run that reports hundreds of changed `.svg`s is a red flag, not a result. **Before reporting,
separate real changes from id churn**: the generated id counter is **hexadecimal**, so a `\d+`-based
filter mis-classifies and will tell you a file changed when only an id moved.

There is also a known population of roughly 67 figures that drift by a pixel on **any** regeneration,
including at `HEAD` with no changes at all. Isolate this brief's own changes by regenerating in a
worktree at `HEAD` first and diffing the two regenerations, not by diffing against the committed files.

Expected real change: **`tech-editor-stackup` (light and dark)** — two files. Anything else is either
known drift or something this series did not intend, and the second case is worth stopping for.

---

## 4. The decision this brief has to make

### R-stk8-7 — do the doc figures adopt the new renderer?

`DocStackupFixtures` draws `stackup-mmic`, `stackup-mim` and `mom-bend-stackup` in **Avalonia controls
on a `Canvas`** — `Border`s and `TextBlock`s, ~250 lines. Brief 1's `StackupRenderer` draws the same
subject in Skia, in `src/Render`, from the same `Technology` objects.

Two drawings of one thing, which have to keep looking alike by hand. That is the situation this
repository has repeatedly decided against — it is the reason `src/Render` exists at all, and the reason
`circuitrf render` draws through the application's own renderers rather than a second copy.

**So attempt the repoint**: `DocStackupFixtures.CrossSection` becomes a thin wrapper that builds a
`StackupScene` and hands it to the renderer inside a Skia-backed control, keeping its three public entry
points and their signatures (`include:`, `height:`, `footer:`) unchanged.

**And stop and report if it does not come out at least as good.** The doc figures carry things the tab
does not need — a `footer:` line, an `include:` window for the MIM module, the "◄ ground reference:
every port's − terminal" and "◄ patterned with 'X'" annotations, and captions sized for a documentation
page rather than a dockable pane. Growing `StackupRenderer` a documentation mode to absorb all of that
may cost more than it saves.

Judge it on the rendered figures, side by side, at reading size:

- If the repointed figures are as legible and no worse, take it. One renderer, and brief 1's
  determinism gate now covers the documentation too.
- If they are worse, **leave `DocStackupFixtures` alone and record why in
  `src/Ui/Diagnostics/RESOLVED.md`** — naming the specific features that did not carry across, so the
  next person to notice the duplication does not re-litigate it from scratch.

Either outcome is a result. An unexamined duplication is not.

---

## 5. Series closeout

### R-stk8-8

- `src/Render/RESOLVED.md` — the scene/renderer findings (brief 1).
- `src/Ui/RESOLVED.md` — the hosting, selection, inline-edit, drag, menu and clipboard findings
  (briefs 2–7). Create sections; do not create a new file where one exists.
- `src/Design/RESOLVED.md` — `DrawLaneFraction` (brief 5).
- `src/Ui/Diagnostics/RESOLVED.md` — the `R-stk8-7` decision.

**Never write any of this into a CLAUDE.md.**

### R-stk8-9 — the vendor-name sweep

Before any commit, grep for commercial-vendor and PDK names across everything this series touched —
briefs, code, comments, test fixtures, documentation and figure captions. Remove what is found and say
what was removed. `.kicad_pcb` is the one permitted exception, and it has no business appearing in this
series at all.

### R-stk8-10 — the gate

```
dotnet test tests/Ui.Tests       --no-build --filter "FullyQualifiedName~Stackup"
dotnet test tests/Ui.Tests       --no-build --filter "FullyQualifiedName~TechEditor"
dotnet test tests/Firewall.Tests --no-build
```

One project path per invocation. **Do not run the full solution suite** — nothing in this series can
reach `src/Core`, `src/Engine` or `RfCore`. Read `tests/Ui.Tests/TestResults/last-run.trx` for failure
detail rather than re-running to find out what broke.
