# Sonnet Brief — Table/trace-card cube UX cluster (#3 triangle gap, #4 inline trace editor, #5 S-only matrix/Z0, #6 harmonic in freq units)

Four independent trace-card / Table-renderer refinements for cube (HB-sweep) data. All touch
`TraceRowViewModel` / `PlotInspectorView(.axaml)` / `TableRenderer`. Do them as one pass; each section is
self-contained. (A fifth issue — the missing `Vout2` node — is engine work in a separate brief.)

---
## #3 — Extra space before the X-axis sort triangle (Table)
In `TableRenderer.DrawHeaderRow`, the freq/X-axis column draws the header text then a sort-direction triangle.
The triangle currently abuts the text. Add ~**1–2 space characters' worth** of gap before the triangle.

Where: the `triCx` computation in `DrawHeaderRow`:
```csharp
float triCx = layout.ColX[0] + layout.ScaledPaddingX + measuredW + triSize * 0.5f + 2f;
```
Increase the trailing constant from `+ 2f` to `+ fs * 0.6f` (≈ one space at the current font size) — or
`+ measuredSpaceWidth * 1.5f` where `measuredSpaceWidth = boldFont.MeasureText(" ")`. Use the measured-space
form so it scales with font/zoom:
```csharp
float spaceW = boldFont.MeasureText(" ");
float triCx  = layout.ColX[0] + layout.ScaledPaddingX + measuredW + spaceW * 1.5f + triSize * 0.5f;
```
Also bump `CalcFitWidth`'s freq-column triangle reservation to match (it adds `fs * 0.6f + TextCellPaddingX`
today) so the wider gap doesn't clip. Small, visual-only.

---
## #6 — Harmonic axis shown in the plot's Freq units (label + combo entries)
The HB `harmonic` axis stores values `{0, f0, 2f0, …}` in **Hz** (unit `"Hz"`). Today the trace card / Table
show these raw (Hz). They should display in the plot's `FreqUnits` (e.g. GHz), in **both** the axis-role pin
combo (`AxisRoleRowViewModel` pin options) and the Table column-0 cells/header when the kept axis is `harmonic`.

This is a **display-only** scaling — the cube keeps Hz. Apply when an axis's unit is a frequency unit (`"Hz"`):
- **Axis-role pin combo** (`TraceRowViewModel.RebuildAxisRoles`, the `opts` list): when `axis.Unit` is `"Hz"`,
  format each value as `(value * freqScale)` with the plot's `FreqUnits`, and label the option with the unit,
  e.g. `"2 GHz"` instead of `2000000000`. Pull the plot's `FreqUnit` from `_parent.FreqUnit` (add a getter if
  not exposed). The DC bin (`0`) shows `"DC"` (nice-to-have) or `"0 GHz"`.
- **Table** (`TableRenderer`, cube mode from the prior Table-cube brief): when the kept X axis is a frequency
  axis (`CubeXUnit == "Hz"` or a freq-unit), scale column-0 values by `plot.FreqUnits.Scale()` and label the
  header `harmonic (GHz)` (axis name + plot freq unit) instead of raw Hz. **Caution:** the prior brief said "do
  not apply FreqUnits.Scale() in cube mode" for a generic Pin axis — that's still right for non-frequency axes
  (Pin/dBm, Vbias/V). Scale **only** when the cube X axis is itself a frequency axis. Detect via
  `CubeXUnit` equalling a known frequency unit ("Hz"/"kHz"/"MHz"/"GHz"); otherwise leave unscaled.
- Helper: add `bool IsFreqUnit(string? unit)` (mirror the CnlReader `_freqUnits` set) where needed.

Add `IReadOnlyList<string>` value-formatting that respects this in one place (e.g. a small
`FormatAxisValue(double v, string? unit, FreqUnit plotUnit)` helper used by both the combo and the table) so the
two stay consistent.

---
## #5 — Matrix type (S/Z/Y) and Z0 controls only for S-parameter sources
The trace card always shows the MatrixType (S/Z/Y) combo and the Z0 box. These only make sense for
**S-parameter (network/SNP) sources**, not for HB cube data (V/INl over node×harmonic). Gate them:
- Add `public bool IsSParamSource => !_trace.IsCubeBound && _trace.Data is { } d && !d.IsEmpty;`
  (a network-bound trace is S-param by construction — the SNP path. Cube-bound traces are never S-param.)
  More precisely: the MatrixType/Z0 controls already partly key off `IsScatteringTrace`/`ShowZ0Control`; extend
  the **MatrixType combo** visibility to a new `ShowMatrixTypeCombo => IsSParamSource` and bind the combo's
  `IsVisible` to it in the XAML. The Z0 control already has `ShowZ0Control` (scattering only) — confirm cube
  traces yield `ShowZ0Control == false` (they do: `!_trace.IsCubeBound` guards it). So the Z0 part may already be
  correct; **verify** and only add the MatrixType gate.
- In `PlotInspectorView.axaml` (the trace-card template), bind the MatrixType combo (and its label)
  `IsVisible="{Binding ShowMatrixTypeCombo}"`. Cube traces show the cube transform combo instead (already
  gated by `IsCubeBoundTrace`), so the row won't look empty.
- Raise `OnPropertyChanged(nameof(ShowMatrixTypeCombo))` wherever `ShowZ0Control` etc. are already raised
  (`OnSelectedSignalChanged`, `RefreshDescription`, `RebuildSignals`).

---
## #4 — Inline text editor for Table traces (type the trace you want)
Let the user type a trace specification directly in the Table (and trace card), reusing the inline text-edit
control from the Schematic/Symbol editor. On invalid input, keep the user's text with a ` <invalid>` suffix, render
that column's cells blank, and show a subtle, gentle, **selectable** hint in the Plot Properties trace card
explaining why it's invalid.

### Syntax to parse
The DataCube shorthand the Table already renders (from the Table-cube brief): `Cube[slice, …]`, e.g.
`V["Vout", 1, :]` — cube name, then per-axis tokens (quoted node label or index for pinned axes, `:` for the
kept/X axis), with an optional leading transform (`dB20 V[...]`). Parser maps a typed string → `(CubeName,
AxisSlice[], CubeTransform)` against the source DataSet's cube + axis metadata:
- cube name must exist in the source DataSet (`ds.Contains(name)`);
- token count must equal the cube rank;
- exactly one `:` (the kept axis);
- a quoted token (`"Vout"`) matches an axis Label; a bare integer matches an index in range; out-of-range or
  unknown label → invalid;
- optional leading transform token parses to `CubeTransform` (case-insensitive: `db20`,`db10`,`mag`,`phase`,
  `real`,`imag`,`conj`,`none`).
Put this in a **pure, testable** static: `CubeTraceSpecParser.TryParse(string text, DataSet ds, out CubeName,
out AxisSlice[], out CubeTransform, out string error)`. (Serialize side already exists as `Trace.CubeShorthand`
from the Table-cube brief — make the parser its inverse so round-trip holds.)

### Editor control
Reuse the Schematic/Symbol inline text editor. Find it (search `src/Ui` for the inline-edit control used in the
schematic canvas / symbol editor — likely an `InlineTextEditor`/`EditableTextBlock` or the param-editor's
TextBox commit pattern). Wire it so:
- **In the Table:** double-clicking a trace **column header** (the `TableHitKind.TraceHeader` hit — already
  detected in `HitTest`) opens the inline editor seeded with `trace.CubeShorthand`. On commit (Enter/blur),
  parse; on success apply `(CubeName, Slice, Transform)` to the trace and `RebuildAndNotify`; on failure store the
  raw text + invalid flag (below). (If wiring header-double-click into the Skia table is heavy, the trace-card
  textbox in #4b is the required path; the in-table editor is the nice-to-have — implement the card path first,
  then the header path if cheap.)
- **In the trace card (required):** add a single-line editable text field showing `trace.CubeShorthand`,
  editable, with the same parse-on-commit behavior. This is the primary affordance.

### Invalid handling
- On parse failure, **keep the user's exact string** and set trace state: `Trace.InvalidSpecText` (string?) and
  the displayed shorthand becomes `"{userText} <invalid>"`. The column renders **blank** cells (not NaN — blank;
  in `TableRenderer` cube-cell path, when the trace has an invalid spec, emit `""`).
- The trace card shows a **subtle, gentle, selectable** hint with the reason. Use a `SelectableTextBlock`
  (Avalonia) with low opacity / secondary color (≈0.6), small font, e.g. *"Couldn't read 'V[\"Voutx\", 1, :]':
  no node 'Voutx' in V. Axes: node, harmonic."* Expose `TraceRowViewModel.SpecError` (string, empty when valid)
  bound to the hint's text and `IsVisible="{Binding HasSpecError}"`. Selectable so the user can copy the axis
  list. No orange icon — gentle grey, matching the §2.8 secondary-label idiom (use a Brush resource, not a
  `*Color` key on `Foreground`).
- When the user fixes the text and it parses, clear `InvalidSpecText`/`SpecError`, restore normal rendering.

### Model
Add to `Trace`: `public string? InvalidSpecText { get; set; }` (null = valid). When non-null, `CubeShorthand`
(or the header label) returns `$"{InvalidSpecText} <invalid>"`, `BuildCubePath`/`FormatCubeCell` produce no
points / blank cells. Keep it additive (nullable; round-trips harmlessly in `.cdd`).

### Tests (headless)
- **Parser_RoundTrip:** `CubeShorthand` of a valid cube trace re-parses to the same `(CubeName, Slice,
  Transform)`.
- **Parser_BadNode_Invalid:** `V["Voutx", 1, :]` on a cube without node `Voutx` → invalid, error mentions the
  bad token + lists axes.
- **Parser_RankMismatch / TwoColons / OutOfRange:** each → invalid with a clear error.
- **Parser_Transform:** `dB20 V["Vout", 1, :]` → Transform=dB20.
- **InvalidState_BlankCells:** a trace with `InvalidSpecText` set → `FormatCubeCell` returns "" and
  `HasSpecError` true.

---
## Gate
Build 0W/0E; tests green. Manual on `HB1.npy`: (#3) clear gap before the sort triangle; (#6) the harmonic
axis/combo reads `2 GHz` not `2000000000`, Pin axis still reads dBm unscaled; (#5) a cube trace shows no S/Z/Y
combo and no Z0 box (transform combo instead); a Touchstone trace still shows S/Z/Y + Z0; (#4) typing
`V["Vout", 1, :]` in the trace-card spec field selects that trace; typing a bad node keeps the text with
` <invalid>`, blanks the column, and shows a gentle selectable reason.

## On completion
Note in `src/Ui/CLAUDE.md`: trace card supports typing a DataCube-shorthand spec (`CubeTraceSpecParser`,
inverse of `Trace.CubeShorthand`); invalid specs are kept verbatim with ` <invalid>`, blank cells, and a subtle
selectable reason. MatrixType/Z0 controls show only for S-parameter (network) sources. The harmonic axis renders
in the plot's frequency units (display-only); non-frequency cube axes (Pin/dBm, bias/V) are unscaled. Sort-arrow
gap widened.
