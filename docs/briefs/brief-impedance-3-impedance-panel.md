# Brief 3 — the Impedance panel: review on the canvas, export when it is done

**Series:** [Impedance review](brief-impedance-0-overview.md) · **Tag:** `R-imp3-n` · **Phase:** UI
**Area:** `src/Ui/Views/Dialogs/TraceImpedanceAnalysisDialog.axaml(.cs)` (retired), new
`src/Ui/Views/Impedance/ImpedanceToolView.axaml(.cs)`, new `src/Ui/Layout/Impedance/` (tool + row view
models), `src/Ui/Layout/LayoutEditorViewModel.TraceImpedance.cs`, `src/Ui/Docking/DockLayoutSchema.cs`,
`src/Ui/Views/Layout/LayoutEditorView.axaml.cs` (`OnImpedanceAnalysis`),
`src/Render/Renderers/LayoutRenderer.*` + `src/Render/Layout/LayoutOverlay.cs` (a results overlay)
**Depends on:** 1, 2 · **Blocks:** 4, 5
**Rule:** the boards, their vendors and the reporter must not be named anywhere in the repo.

---

## 0. Why the dialog has to go

Today the toolbar's **Z₀** tile opens a MODAL dialog whose only way out is **Export…**: the run ends in
a PDF, and its only trace in the application is one Messages line. That rules out both of what the
field report asked for next — drawing a region on the board while choosing what to review (brief 4),
and accepting a finding after reading it (brief 5) — and it means a reviewer iterates by exporting a
PDF, reading it in another program, and running again.

The DRC panel (`DockLayoutSchema.Drc`, `DrcToolView`, `DrcRunReport`) already is the shape needed:
dockable, non-modal, a run button, rows, click-to-zoom, markers on the canvas. Follow it.

## 1. `R-imp3-1` — a dockable Impedance panel

- **`R-imp3-1a`** New dock tool `DockLayoutSchema.Impedance`, registered beside `Drc` and `Lvs` (in
  the tool list at `DockLayoutSchema.cs` ~420). The **Z₀** toolbar tile shows and focuses it instead of
  opening the dialog. Remember the dock-tool trap in project memory: a `Tool` must not be counted as a
  document pane.
- **`R-imp3-1b`** The panel holds, top to bottom: **Settings** (target, tolerance, warning, highest
  frequency — brief 1), **Layers**, **Traces** (brief 2's width table), a **Scope** line (brief 2's
  `ScopeText`, live as the scope changes), then **Run** / **Cancel**, a progress line, the **results**,
  and **Export PDF…**. It binds to the ACTIVE layout editor, as the DRC panel does; with no layout
  editor it says so and disables Run.
- **`R-imp3-1c`** Settings and scope load from and save to `LayoutView.ImpedanceReview` (brief 2) as
  they are edited — not on Run, not on close.
- **`R-imp3-1d`** Delete `TraceImpedanceAnalysisDialog`. Move whatever logic it holds (validation of the
  fields, the band sentence) into the panel's view model; nothing survives in code-behind.

## 2. `R-imp3-2` — results in the panel

- **`R-imp3-2a`** **Run** calls the same `TraceImpedanceAnalysis.Analyze` on the same flattened artwork
  `ExportTraceImpedanceAsync` gathers today (split that method: run → report; export → PDF of a report
  already held). A cancelled run keeps its finished layers, as today.
- **`R-imp3-2b`** A header with the verdict counts as tiles — Pass / Warning / Fail (and Out of scope) —
  and a filter: **All / Warnings and failures / Failures**. Default: Warnings and failures.
- **`R-imp3-2c`** One row per trace in scope: id, layer, verdict, width, Z0 min–max, % in band, type
  summary; expandable to its findings (with severity) and notes. The same strings the PDF prints —
  read from the report, never re-formatted in the view model.
- **`R-imp3-2d`** Selecting a trace row zooms the canvas to the trace; selecting a finding zooms to its
  stretch (`TraceIssue.X0..Y1`). Follow `ZoomToSelectedViolationCommand`.
- **`R-imp3-2e`** The report is held until the next Run or until the layout is edited; an edit marks
  the results **stale** (a banner: *"The layout has changed since this run"*), it does not clear them.

## 3. `R-imp3-3` — the results on the canvas

- **`R-imp3-3a`** A results overlay in `src/Render` (an overlay type like the DRC markers, filled by
  the editor — the renderer draws it, holds no view model): each in-scope trace's centre line coloured
  by Z0 on the report's own colour scale; failing findings as filled markers, warnings hollow (brief 1's
  rule), purple for return-path kinds. Out-of-scope traces are not drawn at all.
- **`R-imp3-3b`** The overlay is on while the panel has results and has a **Show on canvas** toggle.
  The selected row's trace is emphasised.
- **`R-imp3-3c`** It never draws into an export (render/GDS/Gerber): it is overlay state, not document
  content, as the DRC markers are.

## 4. `R-imp3-4` — Export PDF

- **`R-imp3-4a`** **Export PDF…** writes `TraceImpedanceReportDocument.Pdf` of the report the panel
  holds — no second run. Disabled with no results; allowed on stale results, with the PDF's summary
  page carrying the stale sentence so a stale PDF cannot pass as current.
- **`R-imp3-4b`** The Messages line stays (one line, pass / warning / fail, a link to the PDF), and is
  posted on Run, not only on Export.

## 5. Gates and tests (minimal)

1. `LayoutEditorViewModel`: run → report → export produces the same PDF bytes as today's one-shot
   path for the same options (the split lost nothing).
2. The panel VM: the filter's three settings show the expected rows for a report with one of each
   verdict.
3. Editing the layout after a run marks the results stale and does not clear them.
4. The overlay is absent from a `render` of the same layout (reuse a render test's harness).
5. Dock: opening and closing the panel does not change which document pane is active (the memory
   trap).

The GUI cannot be launched from an agent shell: verify by compiler and `Ui.Tests`, and say in the
completion note that pixels were not seen.

## 6. Scope

- No new analysis. No region drawing (brief 4), no accepting (brief 5).
- The CLI is unchanged by this brief.

## 7. On completion

Findings in `src/Ui/RESOLVED.md`. Rewrite `docs/user/src/reference/layout-editor.md` §Impedance
Analysis around the panel (it currently describes the dialog).
