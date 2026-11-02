# Brief PTU-5 — One row per colour, and pieces of one curve joined

**Series:** `brief-ptu-0-overview.md` · **Tag:** `R-ptu5-<m>` · **Depends on:** PTU-3 (selection) is nice to have, not needed
**Area:** `src/Ui/Recognition/PlotSessionViewModel.cs`, `src/Ui/Recognition/PlotTracesPanel.axaml`, one new pure file
`src/Design/Imaging/Plots/TraceJoin.cs`, the call site that hands traces to `TouchstoneAssembly`

---

## 1. What is wrong

The reader returns a drawn curve in pieces wherever it crosses a grid line, a marker or another curve: a four-trace
plot came back as **57 rows**, every one to be assigned by hand, and T37, T39, T42, T50 are plainly one curve. And
assigning them all would not even help: `TouchstoneAssembly.Entry` uses **only the first** magnitude trace of an
entry (`mag[0]`), so the other pieces of S21 are silently dropped.

Fixing the fragmentation in the reader is the later series (overview §5). This brief makes it **cheap to live with**:
group the rows by colour so there are a handful to assign, and join every piece assigned to the same entry.

## 2. Requirements

**R-ptu5-1 — Pieces assigned alike are joined.** New `TraceJoin.Join(IReadOnlyList<PlotTrace> pieces)` → one trace: the
pieces' points concatenated in x order; where two pieces overlap in x, the longer piece's points are kept over the
overlap; a gap between pieces is left for the assembly's existing gap rule. Pure, no reader state, no image access.
Before assembly, **every set of traces on the same plot with the same entry and the same quantity is joined into
one**, in the dialog AND the CLI's plot path (one call site if they share one; if they do not, the CLI keeps today's
behaviour and the report says so — do not refactor the CLI for this). The report line names the pieces joined
(`S21 dB: T37 + T39 + T42 + T50`). This is the only `src/Design` change in the series, and it is a new file — the
reader's files are untouched.

**R-ptu5-2 — Rows by colour (Simple, the default).** The traces table groups the reading's traces into **colour
groups**: same plot, curve colours within a small ΔE of each other (reuse whatever colour-distance helper the reader
or `ImageLayerRowViewModel` already uses — do not write a new colour space), and greedy by length: each piece joins
the first same-colour group whose x coverage it overlaps by less than ~5 % of its own span; otherwise it starts a new
group. So two black curves (measured S11 and measured S21) stay two rows, and the pieces of one red curve are one.
A group row shows its swatch, `Red · 4 pieces · 2.3–2.6 GHz`, and the same Entry and Quantity combos; choosing in
them **assigns every member**. Rows are ordered by total length, longest first, and a group whose total length is
under the reader's `MinTrackShare` of the axis is listed last, dimmed, already unassigned (it is legend samples and
debris).

**R-ptu5-3 — Detailed, for the advanced user.** A two-state toggle at the right of the Traces heading:
`Simple | Detailed`. Detailed is today's table, one row per piece. Switching keeps every assignment (an assignment is
per piece underneath; a group row shows `mixed` when its members differ). The choice is remembered per user the way
`AdvancedExpanded` is. This answers the owner's "a setting for how many segments" without a numeric threshold to
explain — the threshold is the reader's and stays out of the UI.

**R-ptu5-4 — Selection works on groups.** If PTU-3 is in: clicking any piece on the canvas selects its group row in
Simple mode, and selecting a group row highlights all its pieces on the canvas.

## 3. Gates

- `TraceJoinTests` (new, `tests/Ui.Tests/PlotRecognition/` beside the others): three disjoint pieces join in x order;
  an overlap keeps the longer piece's points; a single piece is returned unchanged.
- `PlotColourGroupTests` (new): pieces of one colour with disjoint x form one group; two same-colour curves spanning
  the same x form two groups; assigning a group assigns its members; Detailed ↔ Simple keeps assignments; the
  assembled Touchstone's S21 covers the union of the pieces' x ranges (not just the first piece's).
- One headless render of a multi-trace plot in Simple mode. Report the row count before and after for the one
  picture used — one number, not a corpus.

## 4. Out of scope

Fewer pieces from the reader; reading the legend to pre-assign groups (overview §5).
