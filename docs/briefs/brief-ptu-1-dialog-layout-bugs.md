# Brief PTU-1 — The plot column overlapped, Replace… that adds, and text that cannot be copied

**Series:** `brief-ptu-0-overview.md` · **Tag:** `R-ptu1-<m>` · **Depends on:** nothing
**Area:** `src/Ui/Recognition/ImageReadingPanel.axaml`, `src/Ui/Recognition/ImageSourceViewModel.cs`,
`src/Ui/Recognition/PlotTracesPanel.axaml`, the other `src/Ui/Recognition/*.axaml` panels

---

## 1. What is wrong (each confirmed by reading the code and one headless render)

**(a) The Plots heading is drawn over by "Advanced".** In `ImageReadingPanel.axaml` the right column is a
`Grid RowDefinitions="Auto,Auto,Auto,*"`. The plot panel's `ContentControl` spans `Grid.Row="0" Grid.RowSpan="4"`,
but the `Expander Header="Advanced"` in row 1 is visible whenever `HasPicture` — plot mode included. So the Advanced
header sits on top of the plot panel's "Plots" heading and its first checkbox ("Plot 1 · rectangular · picture 1"),
and as the plot panel's own `ScrollViewer` scrolls, its content passes under/over that expander.

**(b) Replace… adds.** `ImageSourcePane.axaml`'s Replace… is `BrowseCommand` → `LoadFile` → `Take`, and `Take` in plot
mode does `Plots.Add(source)` (R-im14-13: a dropped or pasted picture joins the session). So Replace… behaves exactly
like Add picture…, and the Pictures row shows two pictures.

**(c) Output text cannot be copied.** Every read-out — the calibration line (`x linear, 5 labels · y uncalibrated`),
a trace's range/points line, its frequency evidence, the matrix `Source`, the refusal, the report lines, the status
line, the parts table's read-outs — is a `TextBlock`.

## 2. Requirements

**R-ptu1-1 — Advanced is not shown in plot mode.** Its contents are all layout/schematic options (the only shared one,
"Keep the source picture under the result", has no meaning for a Touchstone; plot mode already has "Keep the source
pictures beside the file"). Bind its `IsVisible` to `!IsPlotPicture` (add an `IsDrawingPicture`-style property if a
negation converter is not already used here). The plot `ContentControl` keeps the whole column. Confirm in the render
that the Plots heading and its checkbox are clear and that a scrolled panel draws nothing over anything outside its
own bounds (`ClipToBounds="True"` on the plot `ContentControl` if the render shows any bleed).

**R-ptu1-2 — Replace… replaces.** Split the paths: `BrowseCommand` (Replace…, and the empty drop zone's Browse…) calls
a `Replace(source)` that, in plot mode, **clears the session and reads the new picture as picture 1** (the same state
as opening the dialog on it: `Plots` emptied, then `ReadPlot`); outside plot mode it is today's `Accept`. **Drop and
Paste keep today's join-the-session behaviour** in plot mode — that is R-im14-13 and is what "Add picture…" is for.
The Replace… tooltip says which: "Read another picture instead of this one." Add the session-clearing method to
`PlotSessionViewModel` if none exists (`Clear()`), firing the same notifications `Add` does.

**R-ptu1-3 — Every read-out is selectable.** In the `src/Ui/Recognition/*.axaml` panels, a `TextBlock` whose `Text` is
**output** (a binding to something the reader or the session computed — calibration, ranges, evidence, source, rule
text, status, refusal, report lines, sizes, the picture's file name) becomes a `SelectableTextBlock`. **Labels stay
`TextBlock`** — headings ("Plots", "Traces"), field captions ("x axis", "Start", "It is"), and text inside a button,
checkbox or combo. The application-wide `SelectableTextBlock` style already fixes size and alignment
(`CircuitRfStyles.axaml`, "AND THE SELECTABLE ONE"); do not restyle locally. A `TextBlock` with `<Run>`s converts the
same way. The report lines are an `ItemsControl` of strings — each item becomes a `SelectableTextBlock`.

## 3. Gates

- `ImageSourceReplaceTests` (new, `tests/Ui.Tests/PlotRecognition/`): in plot mode, Replace with picture B after
  picture A leaves exactly one picture, B; Add picture leaves two; a drop (`LoadFile`) in plot mode still joins.
  Use small pictures drawn in memory (the existing PlotRecognition fixtures draw them) — no file fixtures.
- A source-scan test in the same class: no `<TextBlock` in `src/Ui/Recognition/*.axaml` binds `Text` to one of the
  output properties named in R-ptu1-3 (list them; the test reads the axaml as text). Keep the list short and exact.
- One before/after headless render (overview §4) showing (a) fixed. Not a test; say in the report that it was looked at.

## 4. Out of scope

The order or look of the buttons (PTU-2). Anything the reader produces.
