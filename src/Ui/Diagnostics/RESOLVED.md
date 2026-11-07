# Diagnostics — resolved findings (detail, off the CLAUDE.md growth path)

Same pattern as the other `RESOLVED.md` files in this repo: a completed investigation's detail lands
here, and `CLAUDE.md` stays for durable, still-true conventions only.

## Docs churn: 41 of 54 "drifted" files were not drift, and the workaround was the mechanism (2026-09-21)

The owner's question was why a one-file change still prints a hundred-file docs diff, weeks after it
was raised. Answered by measurement, not inspection: two full runs at a clean HEAD with **nothing
changed**.

| | files |
|---|---|
| differ from HEAD after one run | 54 (a second run: 56) |
| differ between run 1 and run 2 — genuinely nondeterministic | **15** |
| stable across runs but differ from HEAD — **stale committed output** | **41** |
| pure Skia id-renumbering churn | **0** — `SvgPostPass.ScopeIds` works |

**The 41 are the finding.** They are what HEAD's code draws, reverted by hand on earlier commits
under the "classify the churn and put the rest back" workflow that the old `CLAUDE.md` note
prescribed. Each revert makes that figure permanently stale, so it returns on every later run: the
workaround does not reduce the diff, it *is* the diff, and it grows. HEAD's own commit `358e1322`
says so in as many words — it added a Footprint button to the layout toolbar, committed
`toolbar-layout*.svg` and `reference/layout-editor.html` with the new button, and put back
`layout-editor.svg`, `layout-rulers.svg`, `wbond-layout.svg`, `wbond-editor.svg` and
`drc-violations.svg`, the five window figures that draw that same toolbar.

Worse than churn: the committed `reference/layout-editor.html` **inlines a newer copy of the
layout-editor figure than the standalone `assets/figures/layout-editor.svg` beside it** — verified
by extracting the inlined region and matching the new icon's path data (present in the page, absent
in the file). Both are published to Pages, so the chapter shipped a screenshot that disagreed with
the button table printed next to it.

### The 15 that really were nondeterministic — three causes

**ND-1, the Expander chevron** (`antenna-patch-em-setup{,-dark}`, `em-setup-loaded{,-dark}` and the
two pages inlining them). `translate(495 821)` on one run, `matrix(0.9949 0.1011 …)` on the next.
The fix that was already in the tree **cannot work**, and the reason is worth keeping:
`UiArtworkGenerator.SettleAnimations` called `AvaloniaHeadlessPlatform.ForceRenderTimerTick(600)`
believing it advanced 600 frames of animation time. Decompiled `Avalonia.Headless` 12.0.3:
`ForceRenderTimerTick(n)` loops `ForceTick()` n times and `ForceTick` raises `tick(st.Elapsed)`,
where `st` is a real `Stopwatch` started when the timer started. Six hundred ticks in a tight loop
all report the same instant, so the animation clock advances by nothing. Whether the chevron was
settled came down to how much wall time happened to have passed since the Expander was realised.
The file's own remarks listed three earlier failed attempts including `Thread.Sleep(450)` — sleeping
failed because nothing ticked, ticking fails because no time passes, and **nobody combined them**.
Fixed by ticking once, sleeping 400 ms, then ticking again. `Avalonia.Animation.Clock`/`ClockBase`
would be the clean way to control the time directly; both are `internal`, so this is the seam there
is.

**ND-2, a live solve count.** `harmonica-instrument{,-dark}` drew `40 HB solves` one run and
`813 HB solves` the next — how much of the background solve had finished when the tree was read.

**ND-3, a live elapsed time (new, never catalogued).** The railRF status strip drew `414.9 ms` then
`400.3 ms`, in `railrf-window`, `railrf-impedance` and `railrf-classification`, both themes, plus
`railrf.html`.

ND-2 and ND-3 are one bug class — a figure stating a number that measures the machine — and neither
had any fix at all. Both are now suppressed while `UiArtworkGenerator.HeadlessCapture` is set; the
application keeps showing them, because on screen they are the point.

The two nondeterministic families that did **not** reproduce: no `/var/folders` "Opened" row in the
workspace figures, and no one-pixel drift anywhere.

### What was changed

1. **`SvgLint.Measurements` + `ExplainMeasurements`** — a blocking lint, beside the dropped-paint
   one, over four patterns: a solve count, a frame rate, and an elapsed time in `ms` or `µs`.
   **Validated before it was turned on**: those patterns match the eight offending figures and
   nothing else in 766 committed files, so there is no allow-list to argue about. The next figure
   that prints a measurement fails the run by name instead of arriving as unattributable churn.
2. **`SettleAnimations`** — tick, sleep past the longest animation, tick. Costs 400 ms per captured
   scene; the whole run measured **4 min 09 s** afterwards, at 25 % CPU.
3. **The two measurements suppressed** at `HarmonicaView.axaml.cs`'s idle summary and
   `RailRfViewModel.StatusLine`.
4. **`check-docs-current.sh` rewritten** — it now also refuses a dirty tree, fails on anything
   written OUTSIDE `docs/user` (the generator has rewritten `examples/**/*.csch` on the way out
   before), fails on untracked output, and says in its failure text not to revert.
5. **`.github/workflows/docs-current.yml`** — the check had never run anywhere.
   `user-docs.yml` only *publishes* `docs/user`. It runs on **macOS on purpose**: the figures carry
   real text and the substitution report names Lucida Grande, Apple Symbols, Zapf Dingbats and
   Helvetica, so a Linux runner would redraw every label and report the whole set as changed. If a
   runner-image bump ever turns it red purely on text metrics, pin the image or retire the job —
   do not go back to reverting figures.
6. **The 41 stale files regenerated and committed**, which is what makes the diff zero rather than
   merely smaller.

## Smith Chart figures with no wires — not a figure bug, and not a stale page either (2026-09-20)

The second "something is missing from a figure" report in three days, with the answer in a third
place. The `.svg` files held their wire paths and every page's inline copy matched its file byte for
byte; WebKit was painting the stroked wire paths with the preceding grid line's paint, so they drew
at the right place in `#EFEFEF`. Detail, the probe table and the fix are in `src/Render/RESOLVED.md`
under the same date.

**The order to check, from the pair:** the figure file, then the page's inline copy, then the PIXELS
— a figure can be complete, correctly inlined, and still not reach the screen. And when a
correlation partitions the evidence perfectly, confirm the mechanism before fixing it: a real
sub-pixel measurement here split exactly four-bad-to-two-good and was a coincidence.

## Two Smith charts in the Data Display chapter had no grid, and the figures were innocent (2026-09-17)

Owner: some Smith charts in the user docs render with no axis grid lines.

**The `.svg` files were correct. The page's INLINED copies were stale.** `reference/data-display.html`
carried `data-display`, `data-display-dark`, `plot-loadpull-contours` and
`plot-loadpull-contours-dark` as they stood BEFORE `e670f8c9`, which is the commit whose own message
says what the missing thing is: *Skia's SVG device drops everything drawn inside a SaveLayer, which
took the whole Smith grid out of every export*, fixed by drawing the arc family as one path filled
once. That commit rewrote the four figure files and did not rewrite the page, so the fix reached
`assets/figures/` and never reached the chapter anybody reads.

**It presents as a figure bug and is not one, so the first four hours can go into the wrong file.**
Rendering every figure — all 98 light, 42 dark — shows every Smith chart with its full grid, because
every figure genuinely has one. `{{ui: …}}` figures are **inlined as `<svg>`, not referenced with
`<img>`** (`Placeholders.ReadInline`), so a page and its figures are two copies of one picture and
only the figure half is regenerated when a capture is re-run.

### The check that finds it in one pass, and it is cheap

For each inline `<svg>…</svg>` block, take the id prefix (`<name>_cl_0`), read
`assets/figures/<name>.svg`, strip the XML declaration and comments exactly as `ReadInline` does, and
compare. A stale block is not subtle once you look: the four here were **~17 KB short each**, the
size of the one omitted grid path. Run over every page it also named `antennas.html`
(`antenna-patch-em-setup`, ±2.2 KB) and `mom-engine.html` (`mom-bend-mesh-settings`, ±40 bytes) as
stale for unrelated reasons — left alone here, because the ask was the Smith charts.

**Element counts are enough to spot it; lengths are not, on their own.** `<ellipse>` is what a POLAR
grid's rings are; a SMITH grid is one `<path fill="#A0A0A0" fill-opacity="0.498">` holding every arc
as a stroke-to-fill outline, which is why a gridless Smith chart still has its unit circle (that one
IS an `<ellipse>`) and its real axis, and reads as "the chart drew, the grid did not".

**The fix was to re-inline those four blocks from their own files, byte for byte as `ReadInline`
would**, rather than re-running DocGen — a full run rewrites every figure and every page, and this
repo already treats that churn as a red flag. Four lines changed in one file.

## R-stk8-7 — the stackup doc figures adopt `StackupRenderer`. Taken, and here is what it cost (2026-09-13)

`brief-stackup-render-8-docs-and-figures.md` left one decision open: `DocStackupFixtures` drew
`stackup-mmic`, `stackup-mim` and `mom-bend-stackup` in Avalonia `Border`s and `TextBlock`s on a
`Canvas`, ~250 lines, while brief 1's `StackupRenderer` drew the same subject in Skia from the same
`Technology` objects. Two drawings of one thing, kept looking alike by hand. **Repointed.**

### It was judged on the pictures, not on the principle

The brief's instruction was to attempt it and **stop and report if it did not come out at least as
good**, so all six figures were rendered and read at 2× before anything was decided. Doing that needs
a rasteriser, and the obvious one lies:

> **`qlmanage -t` is not a reliable way to look at one of these figures.** It rendered an 862-px SVG
> at some other scale and clipped it, which read exactly like a figure whose right-hand label ran off
> the canvas — and an early draft of this note and of `DocStackupFixtures`' own header said the
> hand-built figures shipped with the MIM tie annotation cut off mid-word. **They did not.** The
> SVG's own clip rect for that label is `x=503 width=320` and the glyph advances end at 315: it fits.
> `qlmanage` is fine for "did anything draw at all" (which is what `src/Ui/Diagnostics/RESOLVED.md`
> already recommends it for) and not fine for judging a layout. A five-line console referencing
> `Svg.Skia` that loads the picture and writes `CullRect × 2` to a PNG is exact, takes two minutes to
> write, and is what the comparison was actually done with.

### What the repointed figures gain

- **A via says what it connects.** `MIM Metal → Metal2  solid`, `Metal1 → Backside Metal  plated =
  3 µm`. The hand-built figure drew three identical grey rods and named them; the *span* — which is
  the whole of what a via entry is, and what the chapter's Via section is about — appeared nowhere.
- **Plated, solid and unplated are visibly different**, and a plated barrel is drawn with its bore.
- **Conductors scale against each other.** The old fixture gave every conductor a flat 26 px, so the
  0.25 µm MIM plate drew the same thickness as a 3 µm interconnect metal — in the figure whose
  subject is that plate. The scene draws it at 16 px against their 36.
- **The label column is measured and cannot overlap or overflow** (R-stk1-9), where the old one
  placed text at computed offsets and trusted it.
- One renderer, so brief 1's determinism gate now covers the documentation, and a reader comparing
  the page with the application sees the same picture.

### What they lose, and where it went

- **The two sentences.** The old figure wrote `◄ ground reference: every port's − terminal` and
  `◄ patterned with 'MIM Metal' — only in runs that analyse it` into the picture. The renderer writes
  `gnd` and `patterned: MIM Metal`, which is right for a pane someone is editing in and thin for a
  page someone is reading. That is R-stk1-4 holding rather than a defect, and **both sentences moved
  into the figure captions**, which is where a qualification belongs — held by
  `StackupDocFigureTests.TheCaptions_CarryWhatTheDrawingAbbreviates`.
- **A very thin band gives up its own name.** `MIM Metal` at 16 px cannot hold a padded 19 px name,
  so the scene moves it into the label column in bold, leading its group. Designed behaviour, and the
  column is in stack order, but it is a real difference on the one figure that is about that band.
- **`mom-bend-stackup` now reads in mil**, because that is the technology's own display unit and the
  scene prints what the tab prints: `62.9921 mil`, not the `1600 µm` the hand-built fixture hardcoded.
  The chapter quoted 1.6 mm, so the figure and the prose beside it disagreed. **Fixed in the prose and
  the caption, not in the drawing** — the drawing agrees with the application, which is the point of
  the repoint. Both now say 62.99 mil *is* the 1.6 mm.

### Three things the repoint needed, none of them a "documentation mode"

1. **`StackupSceneOptions.ShowBoundaryConditions`.** The MIM figure is a WINDOW on five of seven
   bands, and a slice of a sandwich has no terminations of its own — printing the stack's would say
   something the picture does not show. It is a scene option rather than a renderer flag because it
   changes the LAYOUT: the notes occupy space above and below the bands and everything else is
   measured from them.
2. **The window is a filtered `Technology`, built by the fixture**, not a feature of the scene. The
   layer table is shared rather than copied — the scene reads it only to resolve a conductor's colour.
3. **The `footer:` line is an Avalonia `TextBlock` UNDER the control**, not a scene label. It is
   documentation prose about what the window leaves out, and R-stk1-4's rule that the drawing carries
   no commentary applies to the figures too.

**`height:` did not survive**, and the brief's "keep the signatures unchanged" is the one place this
deviates from it. The scene computes its own intrinsic height, so a figure height typed beside the
fixture could only be ignored or, worse, silently clip the bottom band the day a layer is added. The
catalog rows are derived from `StackupScene.Height` instead, and `StackupDocFigureTests` asserts the
frame is neither smaller than the drawing nor more than 24 px larger.

### One thing the repoint SURFACED and did not cause

In the dark variant, a dielectric's on-band name is dark ink on a dark fill and reads faintly.
`ColorRole.StackupOnBandInk` is fixed dark in both variants deliberately — a theme-coloured name would
vanish into copper — and `StackupRenderTheme.DielectricFill` is themed, so the two meet on a
dielectric. **This is the tab's behaviour, not the figure's**, and it arrived with brief 1; the old
doc figure hid it by hardcoding a light grey dielectric fill in both variants. Left alone here — brief
8 is documentation, and the fix is a renderer decision that changes the application.

---

## R-stk8-5 — `tech-editor-stackup` is 2224 px, and the number is measured (2026-09-13)

Two separate problems, and the first is the one that would have gone unnoticed.

**The split.** The Stackup tab opens with the drawing starred and the card pane at a stated height,
which is right in a window of ordinary size. The figure is captured in a window ~2,000 px tall,
because a reader cannot scroll a picture — and at that height the starred row handed the drawing
**about 1,800 px, nearly all of it empty**, while the cards kept their 140 and eight of the nine
stayed below the fold. The capture showed a split the interactive default would never produce and a
card list that was mostly missing. `TechEditorView.ApplyStackupSplitForCapture`, called from
`FigureScene.AfterLayout`, swaps the roles: the drawing takes the height it actually measured (the
scene's own, so the whole cross-section with nothing to scroll) and the CARDS take the star.

**The height.** 2224 = title bar 34 + header 57 + cross-section 338 + splitter 6 + the filter row and
all nine cards, ending at 2214 measured off a deliberately over-tall capture. It is **not** the old
2080 nudged, and the two changes do not cancel: the drawing is new above the cards (+344) and each
conductor card lost the ~52 px R-stk2-9 took out of its drawing-layer picker (−208).

---

## The churn, classified (R-stk8-6, 2026-09-13)

Regenerated in a `git worktree` at `HEAD` and again in place, then diffed the two regenerations — the
procedure this file already prescribes, and the only one that separates a change from the ~59-file
stale drift. **21 files differed, of which 3 were `.DS_Store`.**

- **13 are this brief's**: the four cross-section figures in both variants (`stackup-mmic`,
  `stackup-mim`, `mom-bend-stackup`, `tech-editor-stackup`), `reference/stackup.html`,
  `reference/mom-engine.html`, their two `.md` sources, and `search-index.js`. The brief predicted
  two; taking R-stk8-7 is what makes it eight.
- **5 are a nondeterministic family**, and now it has a name: `analysis-editor-hb-dark`,
  `em-setup-loaded`, `em-setup-loaded-dark` and the two pages that inline them differ in **one
  chevron's rotation matrix** — `matrix(-0.9212 0.389 …)` against `matrix(-0.9203 0.3912 …)` on a
  7×7 `M0 0L7 7L14 0` glyph, which is an expander arrow caught mid-transition. `SettleAnimations`
  does not pin it. Nothing else in those files differs.

The other ~52 changed files were the stale drift and were `git checkout`-ed. **The hex id counter
never came into it** — a run-against-run diff cancels id churn as well as drift, which is why it is
worth the four minutes rather than filtering a diff against the committed files.

## The crash report says how the process EXECUTES code, not only what it runs on (2026-09-03)

Four header lines were added for one specific dead end. A field report that has survived six rounds
(`src/RfCore/RESOLVED.md`) is a managed exception whose own arithmetic says it was unreachable: an
in-range array index that faulted anyway, on an immutable object, with the read's own replay
succeeding. Once the application's state is exhausted as an explanation — and it now is, positively
rather than by failure to reproduce — what remains is the layer underneath. None of it appeared in any
report:

```
debugger    : no
gc          : workstation, Interactive
profiler    : none
codegen env : all default
modules     : 214 loaded, from elsewhere: SomeEndpointHook.dll
```

- **`profiler`** covers `CORECLR_ENABLE_PROFILING`/`CORECLR_PROFILER` and the legacy `COR_*` spelling,
  because injectors set either. A CLR profiler can rewrite IL and force rejits, so its presence
  changes what "the same code" means — and endpoint-protection and APM agents inject them routinely
  on managed corporate desktops, which is exactly the environment this report comes from.
- **`codegen env`** lists only knobs that change codegen (`DOTNET_TieredCompilation`,
  `DOTNET_TieredPGO`, `DOTNET_TC_QuickJitForLoops`, `DOTNET_ReadyToRun`, `DOTNET_JitMinOpts`,
  `DOTNET_ZapDisable`). Unset is the shipping configuration; saying "all default" explicitly is what
  makes a set one stand out.
- **`modules`** lists native modules loaded from neither the OS nor the application directory — i.e.
  injected. Classified by path, reported by **name only**: which DLLs are in the process is
  diagnostic, where they live on the reporter's disk is not ours to collect.

**The negative answer is the point.** "no / none / all default / nothing injected" narrows the field
as usefully as a named module does, which is why all five lines are written unconditionally rather
than only when something is set.

**Platform caveat, not a bug:** `Process.Modules` enumerates the full loaded-module list on Windows
and reports only the main module on macOS. The line therefore reads `1 loaded` on a Mac. Windows is
where the report comes from and where the enumeration works, so this was not worth a second
mechanism — but do not read a Mac report's `modules` line as evidence of anything.

### `Dispatcher.UIThread.CheckAccess()` is not a free read

Every trail note now carries its thread (`[07:34:56.246 t1]`, and `t7!ui` off the UI thread), because
a burst of identical notes is a different event depending on whether one thread produced it or
several did.

The obvious implementation is wrong. **Reading `Dispatcher.UIThread` has a side effect: the first
access CREATES the dispatcher, bound to whichever thread asked.** A diagnostic that consulted it could
bind the UI thread to a worker merely by noting something early in startup — a diagnostic that changes
the thing it observes, in the one direction that would be hardest to notice.

`CrashReporter.MarkUiThread()` captures the managed thread id once from
`App.OnFrameworkInitializationCompleted`, which runs on the UI thread by definition, and `Note`
compares integers. Before it has run there is no UI thread to be off, so an early note is unannotated
rather than wrongly flagged. It also keeps `CrashReporter` free of any Avalonia reference. Held by
`CrashReporterTests.EveryNote_CarriesItsThread_AndMarksTheOnesThatAreNotTheUiThread` and
`TheHeader_RecordsHowTheProcessIsExecutingCode_NotOnlyWhatItRunsOn`.

---

## Adding a docs page in a NEW section: two traps, both silent (2026-09-12, AN-01)

Found while adding `docs/user/src/app-notes/` — the first section outside `reference/`,
`quick-start/` and `new-user-guide/`. Neither trap produces an error; both produce a page that
generates cleanly and is wrong.

**1. The middle breadcrumb segment IS the directory name, slugified.**
`HtmlEmitter.Breadcrumb` links every segment except the last, and builds the href by
`Slugify(segment) + "/index.html"` where `Slugify` is `ToLowerInvariant().Replace(' ', '-')`. So a
page in `app-notes/` whose front matter says `breadcrumb: Docs > Application Notes > …` emits a link
to `application-notes/index.html`, which does not exist. **Nothing checks it** — the orphan check is
over `_nav.txt`, not over hrefs. The fix is to spell the middle segment so it slugifies to the real
directory (`Docs > App Notes > …`). The existing sections hide this because their section word
already equals their directory (`Reference` → `reference/`). A section INDEX page is unaffected: its
section name is the last segment and last segments are not linked, which is why
`reference/index.md` can say `Docs > Reference Guide` and be fine.

A cheap guard, worth running after any docs change, is a site-wide href existence sweep over
`docs/user/**/*.html`; it takes under a second and would have caught this.

**2. `DocLayoutFixtures.Framed` does not centre correctly at extreme aspect ratios.**
Its arithmetic (zoom = min(w/worldW, h/worldH), then an x0 that centres) is right, and it is right on
screen for the existing figures — all of which are between about 1.3:1 and 3:1. On a 5:1 part in a
4.6:1 frame the rendered result came out with the content scaled ~1.15× about the top-left rather
than centred, so it overran the right edge and cropped the outermost port while leaving a large left
margin. Root cause not chased; the practical rule is:

- **Keep the figure WIDTH-limited**, i.e. choose `height` so `height/worldH > width/worldW`. That is
  the regime every correctly-framed existing figure is in (`ports-edge` frames exactly as computed).
- **Then leave real slack** — `marginX: 0.32` put the four-port coupled-pair figure comfortably
  inside its frame at 880×190, where 0.12 and 0.22 both cropped it.
- **Verify by rendering, do not trust the arithmetic.** `qlmanage -t -s 1200 -o <dir> fig.svg`
  produces a PNG that can be read directly. Note that qlmanage does not draw the port bar/arrow
  markers — the reference figure `ports-edge.svg` behaves the same way, so a missing marker in a
  qlmanage render is the renderer, not the figure.

**3. Unrelated: the committed `docs/user` output is STALE against the current build.** A regeneration
with no source change rewrites ~65 figures and pages — a toolbar icon (`SineWave` → `none`), a
Settings string, a ~2 px shift in the wBond profile figure, and others. These are real content
drift, not the hex-id churn and not the three known nondeterministic families: they are stable across
two consecutive runs and differ from `HEAD`. So `tools/DocGen/check-docs-current.sh` fails on a clean
tree for reasons that predate any current change, and anyone adding one page must revert the other 65
rather than commit them.

---

## AN-01's measured data was stale against the port-calibration series (2026-09-13)

The app note was written while a port whose feed had a driven neighbour inside the clearance
threshold could not be calibrated at all, so **every number in it was of a run that no longer
happens**. PCAL3, PCAL4 and PCAL5 changed the outcome, not the wording: a portless neighbour goes
into the standard, two ports at one reference plane are one calibration group with a modal error
box, and a group member that grew a feed lead is peeled with the mode's own γ. On
`testdata/portcal/coupled-pair` the note's headline figures went from S(2,1) = −34.2 dB at 1 GHz and
non-passive everywhere to **−0.14 dB and max |ΔS| 0.0454 against the two kernels' own 0.0521
agreement floor** — so the table the note argued from was not merely imprecise, it was of the
opposite result.

**The lesson is about app notes specifically.** A reference page describes a control and ages slowly;
an app note is an argument built on a measurement, and when the engine's answer changes the argument
inverts silently. Nothing in the build checks a note's numbers against a run — the figures are
regenerated, the prose is not. Two cheap habits follow:

- **Name the fixture in the note** and have the note's numbers come from it, so re-measuring is one
  command rather than an archaeology exercise. AN-01 now names
  `testdata/portcal/coupled-pair` and quotes the `em` verb that runs it.
- **Write what the tool does, never what it used to do.** The old note's structure was a narrative of
  a failure and its diagnosis, which is exactly the shape that goes stale: half of it existed only to
  describe behaviour that no longer exists (owner, 2026-09-13 — a reader new to circuitRF is asking
  how to do the thing, not what used to go wrong).

**A result figure that costs a full-wave sweep goes in the catalog as `Static`.** AN-01's two plots
(`an01-coupled-pair-return-loss`, `an01-coupled-pair-through-coupled`) are drawn by
`DocCoupledLineFixtures`, which resolves the committed `.cem` through `EmSetupResolver` and runs
`EmRunService.Run` at 31 points into `DocRunData.ResultsRoot` — never into `testdata/`. That is ~30 s
built Release and several times that in the Debug build a plain `dotnet run` produces, which does not
belong in an ordinary regeneration; `--rebuild-static` redraws them, exactly as the antenna patterns
work.

**The two-port figure went with the argument.** `an01-coupled-pair-2port` existed only so the note
could reason from the difference between four ports and two. With that section gone it is worse than
unused: a picture of a coupled pair carrying ports on one line only, under a page about coupled
lines, reads as the recommended arrangement to anyone scrolling (owner, 2026-09-13). Its catalog row
and `DocLayoutFixtures.CoupledPairTwoPorts` are deleted along with the two committed SVGs — nothing
in the build forbids an unreferenced catalog row, which is exactly why one has to be removed by hand
when the page that cited it stops citing it.

Two mechanical traps met on the way:

- **The data-source picker enumerates `results/*.npy` only.** Handing `DocDataDisplayFixtures.Sourced`
  the `.sNp` beside it still loads and still plots, so nothing fails — but the toolbar's source combo
  renders **empty**, which no other Data Display figure does. Use `EmRunResult.NpyPath`.
- **Two coincident curves are indistinguishable from one.** S(1,1) and S(2,2) of a uniform line agree
  to 0.0008 dB, so the return-loss figure drew as a single trace with two Y-axis label strips beside
  it. `DocDataDisplayFixtures.Dashed` sets the second trace's line style through the trace card's own
  picker; the coincidence is then the thing the reader can see.

---

## Illustrating the MoM chapter's worked example (2026-09-13)

Eight figures were added to `reference/mom-engine.html`'s worked example — the artwork, the stackup,
the ports, the mesh settings, the mesh, and three plots of the solved bend — plus two links to AN-01.
`DocMomBendFixtures` builds all of them. A ninth, `DocConformalMeshFixtures`, illustrates the
Conformal boundary cells section; it is described at the end.

**The artwork is BUILT in the fixture, which is the deliberate exception to the rule
`DocAntennaFixtures` states.** The antenna page is written *about* `testdata/antenna/`, so a second
copy under `src/Ui` would drift from the design the page quotes. This page is an *instruction*: it
tells a reader to draw two rectangles at four stated coordinates, and the figures have to be of
exactly those coordinates. There is no file for them to be a copy of, so the constants live once, in
the fixture, beside a comment saying the prose quotes them.

### The figures falsified two paragraphs of the prose, which is the point of adding them

Putting the panel's own mesh report next to hand-written arithmetic is what caught it:

- The page computed the mesh from **λ_g ≈ 16.5 mm and a 20 mm run** — neither of which is this
  structure. The real numbers are λ_g = 563 mil at 10 GHz, 400 mil arms, 351 cells and 654 unknowns.
- A sentence claiming the conductor width sets the cell size here was wrong by 2 %: λ_g/20 is
  28.1 mil and the 114 mil width over 4 cells is 28.5, so the **wavelength cap** wins, barely. Both
  numbers are printed in the figure beside it now.

Same shape as AN-01's own lesson one file up: prose is not regenerated, so the cheapest guard is to
put the tool's own report in the picture next to the sentence that paraphrases it.

### A second data source on one plot is a LOAD, not a refresh

`DataSourceLibraryViewModel.RefreshAvailableDataSources` only enumerates `results/*.npy` into
`AvailableDataSources`; `Entries` — the loaded library — grows only through `LoadFileAsync`. And
`TraceRowViewModel.SourceSelectorVisible` is gated on **two loaded entries**. So a figure that merely
wrote a second results file beside the first gets no per-trace Source combo and every trace silently
comes from the selected one. `DocDataDisplayFixtures.PlotFor` takes an `also` list for this, and
`PickSource` refuses by name rather than falling back.

### Every trace-card edit needs the card as a FUNCTION, not a reference

`TraceRowViewModel.ApplySelectedTransform` ends in `RebuildAndNotify`, which replaces the inspector's
rows — so the rule `DocAntennaFixtures` learned on cube-axis edits applies to the transform combo and
the left/right axis toggle too. `Card`, `SetTransform`, `PickSource` and `UseRightAxis` now live in
`DocDataDisplayFixtures` so there is one copy of it.

### Cropping to the mesh settings

`FigureCrop.Around` needs something NAMED to crop to, so `EmSetupEditorView.axaml`'s surface-mesh
`Border` is now `Name="SurfaceMeshGroup"`. Two things about the row size: the group includes the
mesh **summary and notes**, which are worth keeping (they carry the unknown count, the return plane,
and what the edge mesh cost), and a frame shorter than the group trims from the **bottom**, so 430 px
cut a sentence mid-word. 560 fits it.

### Two small traps met on the way

- **The `.sNp` an EM run writes is `RI`, not `MA`.** Reading columns 2 and 3 as magnitude and angle
  gives a smooth, plausible, completely wrong table — which is what the first hand-check of the bend
  produced.
- **`dotnet run --project src/Cli --nologo -- …` swallows the verb** and prints the usage banner with
  exit code 0. Drop the `--nologo`.

### The committed `docs/user` drift, re-measured

The stale-output problem recorded above is unchanged and now numbers **59 files**: 47 figures, one
toolbar manifest, and 11 pages (pages drift because figures are inlined into them). Measured the way
the memory note prescribes — `git worktree add` at HEAD, regenerate there, and diff — which takes
about four minutes and is the only way to separate your own figures from the drift. Generating into a
**scratch directory instead of `docs/user` does not work for pages**: every emitted page carries a
`Source: <path>` comment built from the out directory, so all 40-odd of them differ for that reason
alone. Regenerate in place and `git checkout` the drift list afterwards.

### The conformal-against-staircase comparison is ONE figure, not two

`DocConformalMeshFixtures.StaircaseVersusConformal` puts two `LayoutCanvas`es in one picture, so a
reader cannot be looking at one of them under a caption about the other. `DocLayoutFixtures.Framed`
was split for it: `FramedCanvas` hands back the control and the after-layout viewport write, and
`Framed` is now a two-line wrapper. The viewport arithmetic stays in one place because that is the
part with the cropping trap in it.

Three things the figure needs that are not obvious:

- **The mesh has to be COARSE and the edge mesh OFF.** At the shipping settings the cells at a
  conductor rim are a few per cent of the conductor width, so the boundary treatment — the only thing
  the figure is of — is drawn smaller than a reader can see, and the graded edge rows are a second
  striking pattern along exactly the edge they are meant to be looking at. `CellsPerWavelength: 10`,
  `EdgeMesh: false`, `MinCellsAcrossConductor: 3` at a 3 GHz mesh frequency puts the flank across
  about one cell every two or three columns, which is the regime in which a staircase looks like one.
- **The mesh frequency is the LOW end of the band, not the top.** The cell cap goes down with
  frequency, and small cells are exactly what hides a staircase.
- **Every settings field is stated rather than defaulted.** The two panels must differ in exactly one
  field; taking the rest from `PlanarMeshSettings.Default` would let a changed default break that
  silently.

The overlay draws conformal cut cells only above a zoom floor (`WouldDecimatePlanarMesh`, and its own
header records the owner report behind it), so a panel too small would have drawn the conformal side
as staircase rectangles and the figure would have shown two identical meshes. At 430 x 330 on this
taper it clears the floor comfortably.

**Checking the figure: `qlmanage` is not a reliable ruler for this one.** It renders a wide, short SVG
at roughly 1.16x whatever `-s` asks for and crops the overflow, so the right-hand panel looked cut off
when it was not. The SVG's own clip rects settle it — the two canvases are at x = 1 and x = 451, both
430 x 330, inside a 900-wide frame — and a temporary copy with a shifted `viewBox` renders the far
panel on its own.

### The figures found two renderer bugs that no test could have (2026-09-13)

Both reported against the new port and Smith figures, both fixed in `src/Render` and written up in
that project's own `RESOLVED.md`: `SaveLayer` content never reaches an SVG (the Smith grid), and
`SKClipOperation.Difference` reaches it inverted (the port glyph, clipped to the inside of its own
numeral). **Neither is a docs bug**, and the same regeneration redrew every other figure carrying a
Smith chart or a port — `plot-smith-data`, `plot-loadpull-contours`, `data-display`,
`harmonica-instrument`, `ports-edge`, `ports-internal`, `ports-internal-gap`,
`ports-gap-mesh-width`, the three AN-01 coupled-pair figures and both antenna patch figures.

**The lesson for this factory is that it is the only vector-export consumer anyone looks at.** The
application draws these charts correctly on screen every day and had done for as long as the layer
existed; what surfaced the defect was a person reading a committed `.svg`. A figure is therefore a
cheap, permanent smoke test for the export path — worth remembering the next time one "looks wrong"
and the obvious explanation is the fixture.

**And the port label's size is a proportion judgement, not the fix.** A port's name is centred on
the same anchor its arrow points at, and the renderer knocks the name out of the marker so the text
reads on top; a numeral much larger than the arrowhead takes the head with it. On the 114 mil bend
the barb is at most 0.22 of the width — 25 mil — so `DocMomBendFixtures.PortLabelHeight` is 22 mil,
a little over one barb.

### The footprint figures: a shared scale has to be constructed, and silkscreen is invisible in a light figure (2026-09-21)

Two figures for the new Footprints chapter (`footprint-case-sizes`, `footprint-densities`), both
drawn by `ChipLandPatternGenerator` itself against the PCB starter technology. Three things came out
of building them.

**A row of canvases does not compare sizes — it hides the comparison.** `LayoutCanvas` fits its own
content on the first layout pass, so four land patterns in four canvases all come out the same size
on the page, and the first figure's entire subject is that an 0402 and a 1206 differ by a factor of
three. The scale is therefore *constructed* rather than fitted: `LayoutViewport.ZoomToFit` takes
`min(W/worldW, H/worldH) * 0.8`, so a panel whose pixel width is its own world width times a shared
constant — and whose height is the TALLEST pattern's, so height never binds — lands at exactly that
constant. Measured out of the emitted SVG afterwards: every panel at 42.0 px/mm, to within the
0.3 % that rounding a panel width to whole pixels costs. The first attempt drew all four patterns
into one `LayoutView` at offsets instead, which also shares a scale but needs a shape-translating
helper and puts the captions inside the artwork — see below for why that was worse.

**A `LabelShape` on the silkscreen role is invisible in the light variant of a figure.** Silk Top is
`#F2F2F2` at 0.349 opacity and a light figure's canvas background is `#F6F6F4`: the case codes and
the two silk body lines are both there and neither can be seen. Every figure is emitted in both
variants from one scene, so there is no per-variant escape — the case code is an ordinary
`TextBlock` caption under each panel now, which the docs' own paint handling renders off-black on
light and white on dark. Worth knowing beyond this figure: **the application has the same property**
on the light theme, since a layer's colour comes from the technology and not from the theme.

**No shipped technology declares a courtyard layer**, so both figures show copper, mask openings and
silk and no courtyard, which is what a user gets. `LandPatternLayers` says so in the run's
diagnostics rather than relocating the outline — the chapter states it as a limit instead of the
figure quietly implying courtyards do not exist.

**The layout toolbar figure had never included the `Footprint…` button.** Regenerating produced a
34th button capture (`toolbar-layout-btn-34`) and a new row in the Layout Editor chapter's own
button table: the button shipped with brief-footprint-3 and the docs were never re-run. Nothing
reports that — the manifest table is generated from the live toolbar, so it is only ever as current
as the last regeneration.

**And the live "n HB solves" counter drifted again**, exactly as the 2026-08-31 entry above records
(40 → 889 → 40 across runs here). Two runs at HEAD in a worktree read 40 both times, which is what
made it look at first like this change had caused it; a third run in the working tree read 40 again.
The figure is not deterministic and is not evidence of anything — classify it before reporting a
regeneration, and put the file back if it moved.

## A slides-only DocGen run emptied the site search index (2026-09-21)

Found while regenerating documentation for `brief-lvs-15-docs-and-example.md`, and fixed in
`tools/DocGen/Pipeline/DocGenRun.cs`.

```
dotnet run -c Release --project tools/DocGen -- --slides docs/slides --deck overview
```

That command **rewrote `docs/user/assets/js/search-index.js` to
`window.CRF_DOCS_SEARCH = {"v":1,"p":[],"s":[]};`** — an index of nothing. Exit 0, and the run's own
report lists the two PDFs it produced and does not mention the file at all.

**The code already said it could not happen.** The comment above the write read *"A slides-only run
never reaches here."* It does: the page loop `continue`s past each page's HTML emission on
`slidesOnly`, then falls out of the loop to the write with the `SearchIndex` still empty. The fix is
the early `return` the comment was describing.

**Why it went unnoticed, and why it is worth a file of its own.** `docs/slides/` is **git-ignored**,
so a deck run is supposed to touch no tracked file whatsoever — which means nobody looks at
`git status` afterwards. This one file was the single exception, so the damage was invisible at the
moment it happened and would have arrived in a later commit as one modified line inside a minified
`.js`, in the same diff as whatever else that commit was about.

**And the symptom is not an error.** Search on every page of the published documentation simply
returns nothing, forever, with the box still there and still accepting input. `check-docs-current.sh`
would have caught it *if* it were run after a deck regeneration, but the whole point of a deck
regeneration is that it is not a docs change.

Two things generalise:

- **A comment asserting that a branch is unreachable is a claim, and an unenforced claim rots.** It
  is now the guard rather than the description of one.
- **"This mode writes nothing tracked" is a property worth holding, not assuming.** The exception is
  what made the bug silent, not what made it small.

## A framed dialog figure lost its content margin (2026-10-08)

`WindowFrame.Wrap` sets `Width`/`Height` on the control it is handed. A dialog's root `Grid` that
carries its own `Margin="12"` then measures as 12 px wider and taller than the frame, and the capture
shows the content running to the frame's edges, which the dialog never does. Nothing errors. Hand the
frame a `Panel` wrapping the lifted content (`DocRecognitionFixtures` does), so the margin is inside
the size the frame sets. An older dialog fixture that lifts `Window.Content` directly may still be
drawing its content without the margin.

Found while adding `artwork-to-schematic-dialog` to inspect that dialog's layout. The fixes that
capture prompted were in the dialog itself:

- In a horizontal `WrapPanel` or `StackPanel`, every child stretches to the row's height by default.
  A `TextBox` then grows to match its tallest neighbour, while a `ComboBox`'s Fluent theme pins it to
  the top. The dialog's `.row` style centres every child of a row.
- A collapsed Fluent `Expander` is 48 px tall, and putting one in a row of 32 px controls sets the
  height of the whole row.

## Yield figures, and a search-index check that read a CLI table as geometry (2026-10-08)

The Yield page's figures (`yield-family`, `-envelope`, `-histograms`, `-sensitivity`, `-statistics-table`,
`-trace-card`) are `YieldDisplayPreset`'s own plots over the shipped BandpassYield example, pasted into a document
as a `.cdd` opens (`DocDataDisplayFixtures.Yield.cs`). The family and envelope are zoomed to the passband: with ±2 %
parts the 500 curves are a fraction of a dB wide, and over the whole sweep the failures' red covered every pass. The
statistics table is split into two stacked tables for the figure — fifteen columns at a readable size do not fit a
page. `DocsFactoryTests.TheSearchIndexCarriesProseAndNotFigureGeometry` failed once the CLI page's yield section was
first indexed: its quoted statistics row is six two-decimal numbers in a row, which the geometry heuristic matched.
The run length is twelve now; leaked geometry runs to hundreds.

## AN-02's figures: an import and a recognition run on a copy of an example (2026-10-09)

`DocAppNoteGerberFixtures` copies `examples/Artwork to Schematic` to a temporary folder per figure and runs the real
import, the real Create and real S-parameter runs into it; `Cleanup` deletes the copy. Things worth knowing:

- **The import lands in `Board_2`**, because the example already holds the same import as `Board` and
  `ImportFolder.UniqueName` suffixes. That is what a reader sees too, so the note says so rather than the fixture
  importing somewhere else.
- **A layout opened with its file path shows the "From workspace" banner** when no workspace is open, which in a
  capture is always. The layout figures pass no path; nothing in them needs one, since the technology is set
  explicitly.
- **A recognised schematic's netlist names its technology relative to the WORKSPACE ROOT**
  (`technology "Board/Board.ctech"`), so run from `DocRunData`'s scratch folder it fails to resolve.
  `DocRunData.RunNetlistFile` runs a netlist where it is written; the fixture writes it at the copy's root.
- **The Layer Mapping dialog's answer is rebuilt, not clicked.** Its Continue handler is private and a dialog that is
  never shown returns nothing, so `Import` builds the same `GerberMappingAnswer` it builds: the choice's rows
  (`request.Rows`, or `Repropose`) and `GerberTechnologyChoices.Commit`.
- **Monospace text is drawn in DejaVu Sans at monospace advances.** The dialog's From column is
  `FontFamily="Courier New,Consolas,monospace"`; headless, the SVG names `DejaVu Sans` but keeps 7.2 px per glyph,
  so `Board.GTL` reads as "Boar d. GTL" in a browser too. Pre-existing — `artwork-to-schematic-dialog` and the
  harmonica readout strip show the same — and not fixed here.
- **The second default trace colour is pure blue**, barely legible as a legend label on the dark plot; `an02-s11`
  sets that trace to Green through the card's own colour picker.
- **Two size-to-content dialogs and a panel were measured**, not chosen: rendered tall, the last row of ink found,
  the dialog's 16 px bottom margin added (scope prompt 162, drill prompt 394, Tuning panel 88).
- Two separate capture processes produced byte-identical SVGs for all fourteen rows, both variants.

## DocGen: figures are opt-in and gated on the page's source; the image-dialog hang (2026-10-11)

**A docs run captures no figures unless asked.** `--out docs/user` alone expands every page against
the SVGs already on disk — 1.4 s Release. `--figures` recaptures only what a CHANGED page cites
(`{{ui:}}`, `{{toolbar:}}`, `{{symbol:}}`, `{{snapglyph:}}`, read from the Markdown, not the
expansion), plus any cited figure missing on disk; "changed" is against
`docs/user/src/_figure-stamps.txt`, a SHA-256 per page (CRLF normalised) written only at the END of
a successful capturing run, so a run that fails part-way leaves the old stamps. `--figure <id>` names
figures explicitly; `--all-figures` is the old full capture and is what `check-docs-current.sh` runs,
because it is the only mode that sees a UI change moving a figure whose page did not change. The
first `--figures` run measured 81 s for eight changed pages (276 files, 216 of them symbols).
The stamps file was seeded from `2a1b890c`'s sources, the last full regeneration; it is excluded
from the app bundle in `CircuitRF.Ui.csproj` like the `.md` sources.

**The full run hung, forever, on `image-dialog-empty`'s DARK capture** (100% CPU, no output, 20+ min;
`dotnet-stack` showed `CreateSchematicFromArtworkDialog..ctor` → `set_DataContext` →
`RadioButtonGroupManager.OnCheckedChanged` → `TargetNewCell`/`TargetOwn` on every sample). The two
Target radios in `RecognitionTargetRow` carried `GroupName="target"`. A named group is scoped to the
window, and a control not yet in one — every radio during a dialog's constructor — joins a
process-wide group of that name, so the second dialog's radios shared a group with the first's, each
bound to a different view model, and they unchecked each other without end. The light capture (the
first instance) was fine. Fixed by dropping the GroupName: the radios are siblings, so Avalonia
groups them by their panel. **Any `GroupName` on a control that is constructed more than once per
process can do this**; sibling radios should not carry one.

**Still open, found on the same run:** `settings-3d-em` races the gmsh probe — one run captured
"Checking…" in the light variant and the found path in the dark — and the committed figure bakes in
the generating machine's gmsh path. `workspace-overview`'s light and dark variants draw different
documents (the dark one has the layout editor in front; 179 vs 137 text runs), as committed.

## Model Composer figures; a literal `{{` in a code block stops the run (2026-10-20)

**DocGen has no escape for a literal double brace.** The placeholder pass only expands `{{kind: arg}}`,
then fails the page if any `{{…}}` survives into the HTML — and the check runs on the rendered page, so
a fenced code block is no shelter. `cmodel.md`'s Octave example wrote `struct('Inputs', {{'vgs', 'vds'}})`
and stopped every page run. The example now names the cell array (`vv = {'vgs', 'vds'}`) and passes
`{vv}`, which `jsonencode` turns into the same JSON (checked in Octave). Write any such code the same
way rather than adding an escape to the pipeline.

**A Model Composer panel lifted out of its window must take the window's styles with it.** The panes,
cards, tool buttons and lit lamps are `ModelComposerWindow`'s `Window.Styles`, not the panels' own;
`DocModelComposerFixtures.Styled` moves them from a throwaway window onto the panel's host. **The
example opens from a copy of the WHOLE workspace**, not its folder: `synthetic-gan`'s network is a
schematic in the sibling `GaNTwoShell` cell, and with only the folder copied the document opened with
no network and an empty Values table. The lasso is captured mid-drag (`DragBegin` and `DragTo`, never
`DragEnd`), since its loop is drawn only while the drag is held.

## Open, parked until it recurs: a slow window, then a "killed" report after a close (2026-11-07)

A designer on 1.7.3, Windows x64, reported the application slow, with "Not responding" in the title bar while dragging schematic parts, and little memory or CPU use showing in Task Manager. They closed it, as they describe it normally, and the next launch promoted a report saying the session had been killed without a managed exception. A reboot followed before any stack was taken, so **the cause is unknown** and nothing was changed.

- **The trail is the display a finished run opens.** `addPlot — Smith (now 1)`, then a `.palace_em.npy` load, then `addTrace` on a Rect plot 40 ms later: `AutoOpenOrCreateDataDisplayAsync` switches the plot's type in code, which leaves no note. The last line does not date the death, since nothing notes after it, and EM runs write no breadcrumbs, so a long quiet stretch before these lines is not evidence of anything.
- **Not reproduced on that path.** The same flow through a real `PlotRenderer.Draw`, on Palace results and on NaN, Inf, zero, DC-row, 40-port and 20,000-point S cubes, finished without a hang or a throw on macOS, 159 ms at worst. A NaN frequency and an empty sweep were not tested.
- **Unexplained: 18 s between the display's creation and the load's note.** Opening the document and `RefreshAvailableDataSources` run on the UI thread there; reading the file is on a pool thread. Not measured on that machine. It does not explain a slow part drag, because it does not run during one.
- **A normal close cannot leave this report.** `ProcessExit` deletes the session file, so the process ended some other way. Windows' prompt to close a program that is not responding would do it. If a report follows a close with no such prompt, that is a shutdown bug.
- **Low CPU does not clear the UI thread:** one pegged thread on 16 logical processors reads about 6%.
- **Leads, neither shown to apply:** one heavy canvas slowing the whole window through the single compositor (`src/Ui/RESOLVED.md`, "Why one slow canvas stops the whole window"), and `PGHook.dll`, also named in `src/RfCore/RESOLVED.md`.

**If it comes back,** take a stack before anything is restarted: `dotnet-stack report -p <pid>` two or three times during the slowness. First note whether a solver process is still running, whether closing other documents helps, and whether a small example workspace is slow too. An always-on UI-stall note in the trail was considered and not added, since it would run on every user's machine for a question a one-off stack answers better.
