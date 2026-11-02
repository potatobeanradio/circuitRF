# Brief — harmonicaRF Round 6C: the readout strip — a 2 × 4 chunk grid, two new chunks, and no more jitter

**Read first:** `src/Ui/Views/Harmonica/ReadoutStripView.axaml` (its comment block is the design
record for the current column layout) and `ReadoutStripView.axaml.cs` (`SetItems`,
`UpdateReadoutColumn`, `SetInputs`, `BuildGeneralRow`), `src/Ui/Harmonica/HarmonicaFrame.cs`
(`ReadoutColumn`, `HarmonicaReadout`), `src/Ui/Harmonica/HarmonicaSolver.cs` (`BuildReadouts`,
`AddMxColumn`), `src/Ui/Harmonica/HarmonicaInputs.cs`, and
`src/Ui/Harmonica/HarmonicaReadoutFormatting.cs`.

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only if something below is worth recording.

---

## 0. Why this brief is mostly about *width*

Two of the owner's four reports here are the same underlying complaint:

> "As data gets updated the results rendered in the data column changes widths depending on the data
> text width. This pushes other data columns around while the user drags marker and makes the data
> layout appear glitchy."

> "trailing '0' characters as a number goes from something like 10.01 to 10.1 … causes glitches at
> high frame rate and we want to get rid of it."

The strip's columns are `StackPanel`s inside a horizontal `StackPanel` — **a StackPanel sizes to its
widest child, so one value getting one character wider moves every column to its right.** At 60 fps
during a drag, that is continuous horizontal churn. R3C already hit a version of this and fixed it for
the inline editor by floating the editor in an overlay canvas so its `MinWidth` could not widen a
column (see `ReadoutStripView.axaml`'s own comment). The same class of problem is now coming from the
*values*.

§4 is the fix and it is the most important part of this brief. Do it **before** or **together with**
§1's re-layout — a 2 × 4 grid with unstable column widths is worse than what is there now, because a
width change in row 1 now also shoves row 2.

---

## 1. The 2 × 4 chunk grid

Today: six chunks in one horizontal row — Settings · Operating point · Source · Load · MXP · MXE.

The owner's layout, as **(row, column)**, 2 rows × 4 columns:

|  | col 1 | col 2 | col 3 | col 4 |
|---|---|---|---|---|
| **row 1** | **A** Settings (Vgs, Idq, Vds, Freq, Harmonic Order, Compression, Z0 …) | **B** P-3dB operating point | **E** MXP | **F** MXE |
| **row 2** | **D** Load | **C** Source | **G** Intrinsic VDS | **H** Intrinsic IDS |

Note carefully: **Load is above-left of Source** (D at (2,1), C at (2,2)) — that is what the owner
specified, and it is the reverse of today's Source-then-Load order. Do not "fix" it.

Implementation: replace the `Columns` horizontal `StackPanel` with a `Grid` of
`RowDefinitions="Auto,Auto"` / `ColumnDefinitions="Auto,Auto,Auto,Auto"` (widths per §4), each cell
holding one chunk's vertical `StackPanel`. Keep every `x:Name` that exists (`SettingsColumn`,
`OperatingPointColumn`, `SourceColumn`, `LoadColumn`, `MxpColumn`, `MxeColumn`) so
`UpdateReadoutColumn`'s build-once/update-in-place machinery and the existing tests keep working —
this is a re-parenting, not a rewrite of the row builders.

`HarmonicaR3cStripTests.ColumnsPanel_ListsTheSixColumns_InTheOwnersOrder` scans this XAML file's own
`x:Name` order (nothing here is unit-testable through a live control tree). **Update that test to
assert the new grid positions** — read `Grid.Row`/`Grid.Column` off the XAML rather than order, so it
pins what the owner actually specified.

The `General` run (`Items`) and the input half (`Inputs`) keep their current place above the grid.

---

## 2. Two new chunks — G (Intrinsic VDS) and H (Intrinsic IDS)

**Contents:** the Fourier components of the intrinsic drain voltage and drain current at the current
operating point. Header row: `Intrinsic VDS` / `Intrinsic IDS`. Under it, one row per harmonic,
**magnitude and angle only — no harmonic index, no frequency** (the owner is explicit). The list grows
with K.

**Where the data already is — do not recompute it.** `HarmonicaDataSet.Build` publishes
`V_intr` and `I_intr` as `[port, harmonic]` complex cubes (`src/Harmonica/HarmonicaDataSet.cs:~145`),
built from `IntrinsicPlane.Evaluate`, with the drain port index at
`ctx.IntrinsicPorts.DrainPort` and voltages already referenced to `SourcePort`. `HarmonicaSolver`
already has that published `DataSet` in hand where it builds the readouts (`published`, line ~256, fed
to `BuildReadouts`'s siblings) and already has a `ReadComplex(ds, cubeName, i, j)` helper for exactly
this shape — extend it or add a sibling for `[port, harmonic]` indexing. **Never recompute a spectrum
in a view model** (§0.3 item 1 of the harmonicaRF design note).

Rows: harmonic 0 (DC) through K. Include DC — it is the bias point and it is what makes the list
physically readable; if you exclude it, say why. Format as magnitude ∠ angle, in the same
`HarmonicaReadoutFormatting` path everything else uses, so the right-click format flyout works on
these rows too (they are `IsComplex: true`, `Editable: false`, with a `RawValue` — a consequence of
the solve, not something the user may type into, exactly like MXP/MXE's rows).

**When the intrinsic plane is not located** (`ctx.IntrinsicPorts.LoadAvailable == false`, the same
condition that publishes an empty loadline), these chunks show a single stated row — `not located` —
rather than zeros. "Empty" and "broken" look identical otherwise, which is the rule R-h8-3 already
established for this strip.

Add two members to `ReadoutColumn` (`src/Ui/Harmonica/HarmonicaFrame.cs:78`) — note that
`SetItems`'s switch currently uses `default:` for `Mxe`, which silently swallows any new enum member
into the MXE column. **Make that switch exhaustive** before adding anything to the enum.

---

## 3. Input label renames

In `HarmonicaInputs.Build` (`src/Ui/Harmonica/HarmonicaInputs.cs:~137-180`) — labels only, the `Key`
constants do NOT change (they are the persistence and apply keys):

| key | label today | label wanted |
|---|---|---|
| `settings.compression` | `compr` | `Compression:` |
| `settings.f0` | `f₀` | `Freq:` |
| `settings.k` | `K` | `Harmonic Order:` |

Keep the trailing colon exactly as written — the owner typed it, and the strip's other labels not
having one is a cosmetic inconsistency they can raise later if it bothers them. These are the widest
labels in the Settings chunk, so this change interacts with §4: recheck the chunk's reserved width
after making it.

---

## 4. Stable widths — the actual fix

Two independent sources of width change. Both must go.

### 4.1 Number formatting must be fixed-width, not "shortest"

`HarmonicaReadoutFormatting.FormatComplex` uses `0.###`, `HarmonicaSolver.BuildReadouts` uses `0.##`,
`0.#` and `0.###` for its scalars. **Every one of those drops trailing zeros**, so a value moving from
10.01 to 10.1 loses a character and the column reflows. `HarmonicaTitles.FormatTrim` does the same for
titles and is deliberate there — leave the titles alone; this is a strip change.

Replace with **fixed decimal places** per quantity — `0.00` where `0.##` was, `0.0` where `0.#` was,
`0.000` where `0.###` was. That alone fixes the trailing-zero case and costs nothing.

It does **not** fix the integer side: 9.99 → 10.01 still adds a character, and an impedance can
legitimately run from 0.5 Ω to 5000 Ω during a drag. So add one helper —
`HarmonicaReadoutFormatting.FixedWidth(value, decimals, budget)` — with a **stated character budget**
per quantity, which formats fixed-decimal inside the budget and switches to a fixed-width exponent
form outside it (`1.23e+04`). Document the budget as a constant per row type rather than a magic
number at each call site. The point is that the *string length* is a function of the row, not of the
value.

### 4.2 Column width must be reserved, not measured per frame

Even with fixed-width numbers, a *label* set changing (a marker added, K increased, §2's chunks
growing) changes a chunk's width — and that is fine, because it is rare. What must never happen is a
width change on a **value** update.

`UpdateReadoutColumn` already distinguishes exactly these two cases: it rebuilds a column only when its
**shape signature** changes and otherwise writes values into existing rows. Hang the width off that
same signature:

- when a chunk's shape changes, compute its reserved width from its own widest possible content
  (label column + §4.1's character budget for the value column) and set it on the grid column (or on
  the chunk's `Width`);
- on a value-only update, **never touch any width** — write text into the existing `TextBlock`s and
  nothing else.

A simpler variant is acceptable if you can show it holds: give each chunk's label and value
`TextBlock`s a fixed `Width` in the same place the rows are built. What is *not* acceptable is leaving
the width to `Auto` and hoping fixed-decimal formatting is enough — it is not, per the integer case
above.

### 4.3 The test that proves it

Headless, in `tests/Ui.Tests/Harmonica` — `Ui.Tests` cannot construct this control (no live Avalonia
application), so test the **formatting and width-budget layer**, not the control:

- for each row type, format a swept range of values (0.001 → 1e6, positive and negative, and the
  degenerate 9.999 → 10.001 crossing) and assert **every produced string has the same character
  length**;
- assert the shape signature does not change across a value-only update for a fixed marker set — this
  is the one that pins "no width recompute during a drag".

Then the owner drags L1 and confirms nothing shifts. That interactive check is the real gate; the tests
are what stop it regressing.

---

## 5. Per-chunk Copy

Right-click anywhere in a chunk → a fly menu with **`Copy`**, which puts *what the user is seeing in
that chunk* on the clipboard as **tab-delimited text**, one row per line, `label<TAB>value`, so it
pastes straight into Excel. Include the chunk's header row.

`HarmonicaClipboard.TextFor(vm)` already builds exactly this shape for the whole readout set
(`label \t value \n`) — factor its inner loop out so the per-chunk copy and the existing
`Edit ▸ Copy Readouts` share one formatter. Text-only: this is a text clipboard write
(`clipboard.SetTextAsync`), not `PlotExporter`.

**Mechanics:** each chunk's `StackPanel` gets one `ContextMenu` instance whose `Opening` handler
populates it (the pattern this file already uses for the per-row format menu at
`ReadoutStripView.axaml.cs:441` — attached lazily, populated on `Opening`, so `SetItems` does not pay
for a menu nobody opened). Do not attach a menu per row for this.

The existing per-row right-click **format** menu (real/imaginary ⇄ magnitude/angle) on complex rows must
keep working. A right-click on a complex row shows the format menu; a right-click on the chunk's
whitespace or on a non-complex row shows Copy. If that is awkward in practice, put `Copy` at the bottom
of the row menu too, after a separator — both menus reaching the same command is fine, silently losing
one is not.

Copy applies to **all** chunks, including the two new ones and the Settings chunk.

---

## 6. Gates

1. `dotnet test tests/Ui.Tests --no-build` and `dotnet test tests/Harmonica.Tests --no-build` green;
   `tests/Firewall.Tests` green.
2. `ReadoutStripView.LastSetItemsMs` **must not regress**. R5 brought the steady-state drag path down
   to "writing ~37 strings, not constructing ~100 controls" — §2 adds up to 2·(K+1) more rows, so the
   string count grows; the *control construction* count in a steady-state drag must still be zero.
   Report both numbers (before and after) from an interactive drag with the diagnostics overlay, or
   say plainly that you could not read them.
3. Owner check: the eight chunks sit where §1 says; the two intrinsic chunks grow when K increases;
   nothing moves horizontally while dragging L1; right-click ▸ Copy on any chunk pastes into a
   spreadsheet as columns.
