# Brief — harmonicaRF R7C: units move into the labels, the jitter ends, and γ joins the readout

**Read first, in this order:**
`src/Ui/Harmonica/HarmonicaReadoutFormatting.cs` (all 282 lines — every comment in it is a record of a
previous attempt at this problem), `src/Ui/Views/Harmonica/ReadoutStripView.axaml` (its comment block
is the design record), `src/Ui/Views/Harmonica/ReadoutStripView.axaml.cs`
(`BuildColumnRowShell` ~430, `UpdateColumnRow` ~498, `DisplayValue` ~575, `BeginInlineEdit` ~660,
`CalcInlineEditWidth` ~760, `BuildSettingsColumnRow` ~1071, `SettingsValueWidth` ~1109,
`SettingsUnitWidth` ~1130, `UpdateSettingsColumnRow` ~1150, `BuildGeneralRow` ~303),
`src/Ui/Harmonica/HarmonicaFrame.cs` (`HarmonicaReadout` ~111 and its `FormatKey` ~123),
`src/Ui/Harmonica/HarmonicaSolver.cs` (`BuildReadouts` ~591, `AddMxColumn` ~774,
`AddIntrinsicColumn` ~740, `ReadComplex` ~832), `src/Harmonica/HarmonicaDataSet.cs` (~line 125–150,
where `V_intr` is published), `src/Harmonica/IntrinsicPortMap.cs`.

**Do NOT update any `CLAUDE.md`.** `src/Ui/RESOLVED.md` only if you find something genuinely worth
recording — and §1 of this brief probably will produce one.

Tag new comments `R7C §n`.

---

## 0. Read this first — why this brief exists and what "done" means

> "The units in the display readout are still not horizontally aligned with the units on the row
> above them. I have asked Sonnet 5 times to fix this but he can't get it right. Values and units
> cannot be jittery while user drags marker. Investigate. … New solution idea: Merge the units to be
> with the metric name. So instead of separate 'Pout' ⟨val⟩ 'dBm', it's 'Pout (dBm): ' ⟨val⟩. In this
> way there are less columns that have to be aligned. I think this is the right approach given all
> the failed attempts at making this work."

**Five attempts have failed. Do not attempt a sixth variation of the same idea.** The history, from
the code's own comments:

| attempt | what it did | why it did not hold |
|---|---|---|
| pre-R-hui-4 | `ReservedValueChars` — one fixed-width value box | left a visible gap between a short value and its label |
| R-hui-4 | split the unit into its own third column; `Grid` + `SharedSizeGroup` per chunk, all `Auto` | `Auto` measures the *live* text, so a dragged value re-measured its shared column every frame |
| R-hui-5 | pinned the VALUE control's `Width` to `ValueChars(item) * fontSize * 0.55` | fixed the value column; units still drifted |
| R-hui-7 | pinned the UNIT control's `Width` the same way, added `UnitChars` and `SettingsUnitWidth` | still wrong — this brief |

Every one of those changed **how wide a box is**. None of them changed the two things that actually
decide where a glyph lands: `0.55` is a **guess at the font's character advance**, and a third column
is a third thing to align. This brief removes both.

**The acceptance test is a screenshot, not a unit test.** `tests/Ui.Tests` is forbidden from calling
any Avalonia runtime API (`tests/Ui.Tests/CircuitRF.Ui.Tests.csproj`, its own comment) — it cannot
instantiate a control, cannot measure text, and therefore *cannot have caught any of the five
failures*. Do not report this as done on the strength of green tests. §4 is the gate.

---

## 1. The layout fix

### 1.1 What ships: two columns per row, unit inside the label

Every row in every chunk becomes exactly:

```
┌─────────────────────┬──────────────────┐
│ Pout (dBm):         │ 35.20            │
│ Eff (%):            │ 62.4             │
│ Pdc (W):            │ 12.345           │
│ Zin (Ω):            │ 12.500-j3.200    │
│ γ:                  │ 0.312∠-47.5°     │
└─────────────────────┴──────────────────┘
   col 0: SharedSizeGroup    col 1: pinned width
```

- The row `Grid` goes from `ColumnDefinitions="Auto,Auto,Auto"` to `"Auto,Auto"`.
- `SharedSizeGroup` `"ColUnit"` is **deleted**, along with the whole third `TextBlock`.
- `HarmonicaReadoutFormatting.UnitChars` is **deleted**. `ReadoutStripView.SettingsUnitWidth` is
  **deleted**.
- The label cell's text is `$"{Label} ({Unit}):"` when the row has a unit, `$"{Label}:"` when it does
  not. Header rows (`Value.Length == 0 && Tooltip.Length == 0`) keep their bare text and keep their
  `Grid.SetColumnSpan(label, 2)`.
- This applies to **all three** row builders: `BuildColumnRowShell` (Operating point / Terminations /
  MXP / MXE / Intrinsic), `BuildSettingsColumnRow` (Vgs … Z0), and `BuildGeneralRow` (the `Items`
  run). Three builders with two different conventions is how this drifts back.

### 1.2 The unit must come from the row's KIND, never from its current text

This is the single most important sentence in this brief.

`HarmonicaReadoutFormatting.SplitUnit` (line 102) recovers the unit by matching the *rendered string*
against a suffix whitelist. If you compose the label from `SplitUnit(...).Unit`, then the instant a
value is `NaN` — `FormatDbm` returns `"—"`, no suffix — the label loses its `(dBm)` and **the shared
label column changes width**. You will have re-created the exact bug you are fixing, and it will only
appear when a solve fails mid-drag, which is precisely when nobody is looking carefully. The same
happens for `"no optimum"` and `"not located"`.

So:

1. Add a `Unit` member to `HarmonicaReadout` (`src/Ui/Harmonica/HarmonicaFrame.cs:111`) —
   `string Unit = ""` as the last positional parameter, so every existing construction site keeps
   compiling.
2. `HarmonicaSolver.BuildReadouts` sets it explicitly at each site, from what it *knows the row is*:
   `"dBm"` for Pout, `"dB"` for Gain/Gp, `"%"` for Eff/PAE, `"W"` for Pdc, `"Ω"` for Zin and for
   every `Z{marker}` termination row, `""` for AM/PM (the `°` is attached to the number with no
   space — see `FormatDegrees` — and stays part of the value), `""` for a Γ row, `""` for the
   intrinsic VDS/IDS rows (their unit is stated once in the chunk header, `"Intrinsic VDS (V)"`), and
   `""` for every header and status row.
3. `ReadoutStripView` renders the value cell as `SplitUnit(DisplayValue(item, formatFor)).Value` —
   `SplitUnit` survives, but only ever as *strip the suffix*, never as *discover the unit*. Its
   whitelist already covers every suffix the formatters emit; leave it alone.
4. `HarmonicaInput` already carries a separate `Unit` field, so the Settings column needs no new
   state — but the same rule applies: compose the label from `input.Unit`, and an input whose unit is
   empty (`Harmonic Order`) simply gets `"Harmonic Order:"`.

**Do not change the `Format*` functions to stop appending units.** They are the one formatter and
other callers depend on the suffix — `HarmonicaSetTerminationDialog` renders `FormatZ` into its own
text boxes and `HarmonicaReadoutFormatting.TryParse` strips a trailing `Ω` on the way back. Strip at
the strip.

### 1.3 The value cell's width must be MEASURED, not multiplied by 0.55

`0.55` appears at four places today (`ReadoutStripView.axaml.cs` lines ~534, ~541, ~762, ~1171). It
is an assumed character advance for a **proportional** UI font. It is wrong by a different amount for
digits, for `−`, for `+j`, for `∠`, for `°`, for `Ω` and for `—`, and it is wrong by a *different*
different amount at each font size, because the strip's font size is computed from the panel's pixel
size (`ReadoutStripView.FontSizeFor`) and is not an integer.

When the reserved width comes out narrower than the text actually needs, the value's glyphs run past
the cell — into the next column, or clipped, depending on the theme's `ClipToBounds`. Either way what
the user sees moves with the text, which is exactly the reported symptom. When it comes out wider,
the value floats away from its label.

Replace all four with a measurement of the actual typeface:

```csharp
/// <summary>R7C §1.3 — the pixel width of the widest string this row KIND can ever render, measured
/// with the typeface and size the row is actually drawn with. Cached per (typeface, size, kind):
/// the strip refreshes on every published frame and must never lay out text to decide a width.</summary>
private static double ReservedValueWidth(Typeface typeface, double fontSize, string worstCase)
```

Implementation notes:

- Use `Avalonia.Media.FormattedText` (or `TextLayout`) with the same `Typeface`, `FontSize`,
  `FlowDirection` and default text-shaping the `TextBlock` will use. Read the typeface off the
  control (`TextElement.GetFontFamily(control)` / `FontWeight` / `FontStyle`) rather than assuming
  the default — the value cells are `FontWeight.SemiBold` and the labels are not, and SemiBold digits
  are wider.
- Cache in a `static Dictionary<(string family, double size, FontWeight weight, string worstCase), double>`.
  Measuring ~15 short strings once per font-size change is free; measuring per row per frame is not.
- The **worst case string is a literal per row kind**, not a character count times a width. Replace
  `ValueChars`'s `int` return with a `string` — this is the change that makes the whole thing exact
  and self-documenting:

  | row kind | worst case |
  |---|---|
  | complex, rect | `-0000.000-j0000.000` |
  | complex, polar | `0000.000∠-000.0°` |
  | Pin / Pout | `-0000.00` |
  | Gain / Gp | `-000.00` |
  | Eff / PAE | `-0000.0` |
  | Pdc | `-000000.000` |
  | AM/PM | `-000.0°` |
  | γ (§2) | `0000.000∠-000.0°` |

  A complex row must reserve the **max** of its rect and polar worst cases, because a right-click can
  flip the format without a re-solve (`DisplayValue`, R-h9r2-25) — that is what `MaxComplexChars`
  exists for today; keep the idea, change the units from characters to pixels.
- Keep these budgets tied to `FixedWidth`'s own decimal/budget constants
  (`ComplexPartDecimals`/`Budget`, `DbmDecimals`/`Budget`, …) so a change to one cannot silently
  outgrow the other. A helper that builds the worst-case string from `(decimals, budget)` is better
  than eight hand-typed literals.
- `CalcInlineEditWidth` (line ~762) uses the same `0.55`; give it the same measurement. The inline
  editor floats in `EditorOverlay` so it cannot shove a column, but a box narrower than its own text
  is still wrong.

**Nothing about a row's reserved width may depend on the row's current value.** That rule is already
written in the code (R-hui-5's comment at line 528) and it is still correct — this brief only makes
the width itself correct.

### 1.4 The other jitter source, which no width fix can reach

Width is not the only thing that moves during a drag. **The MXP and MXE chunks change their ROW COUNT
mid-drag.**

`HarmonicaSolver.Solve` calls `SolveAtOptimum` only when `opt.Quality == FrameQuality.Full`
(`HarmonicaSolver.cs:334`). On a degraded ladder rung — i.e. while dragging — `Optimum.Solved` and
`Optimum.Published` are null, so `AddMxColumn` (`HarmonicaSolver.cs:781`) emits **one** row,
`"no optimum"`, instead of the nine rows (header, Pout, Eff, PAE, Gain, Gp, Zin, AM/PM, Pdc) it emits
on a full frame. `UpdateReadoutColumn`'s shape signature changes, the column is torn down and rebuilt
with one row, and the whole 2 × 4 `Columns` grid re-measures: row heights collapse, column widths
change, and everything to the right of MXP moves. A `SkipContours` frame *carries the previous
optimum forward* (`CarryForwardContourLayer`, line ~393) and so keeps its nine rows — so as the frame
ladder alternates between rung kinds, the chunk flips between 1 row and 9 rows repeatedly. That is
structural churn at frame rate and it is not fixable by pinning any width.

**Fix:** when the optimum is unavailable, keep the chunk's row SHAPE and render every value as `"—"`.
Concretely, `AddMxColumn` always emits its nine rows; when `optimum is not { Solved: …, Published: … }`
it emits them with `"—"` values, and states the situation once — in the header row's own text
(e.g. `MXP 1f0 Load — no optimum`) and in every row's tooltip.

This preserves R-h8-3's rule ("empty and broken look identical on a panel, so the strip SAYS which it
is") — the statement is still there, it just no longer costs a re-shape. Check the same reasoning
against `AddIntrinsicColumn`'s `"not located"` row (`HarmonicaSolver.cs:750`): that one changes only
when the DUT changes, not per frame, so it may stay as it is — but say in your report that you
checked.

### 1.5 Also verify, and report on

- **Does `Grid.IsSharedSizeScope` on a `StackPanel` actually work in Avalonia 12.0.3?** The whole
  label-alignment scheme rests on it (`ReadoutStripView.axaml.cs:64–71`). If shared sizing is a
  no-op here, every row's label column is independently `Auto` and *nothing* has ever been aligned —
  which would explain five failed attempts better than any width bug. **Test it deliberately**: two
  rows with labels of very different length in one chunk; their values must start at the same x.
  If it does not work, drop `SharedSizeGroup` entirely and pin the label column to a measured width
  per chunk (the max over that chunk's own label strings, measured once per font size, exactly as
  §1.3 does for values). Report which of the two you found.
- `Grid` `ColumnSpacing="4"` stays; the label already ends in `":"`, so 4 px is right.
- After the unit column is gone, re-read `BeginInlineEdit`'s long doc comment: it says the editor is
  seeded "unit included" and that `TryParse` tolerates a trailing `Ω`. With the unit in the label
  the seed no longer carries one, so the "pre-select only the value" rule becomes a plain
  `SelectAll`. **Update that comment** — a stale comment describing the opposite of the code is worse
  than none.
- `RowText` (line ~107), which builds the clipboard text, joins the label with every subsequent
  child. With two columns it now yields `"Pout (dBm)"` + `"35.20"` — check the tab-delimited output
  still pastes into Excel as two sensible columns, and that `HarmonicaClipboard.RowsText` is happy.

---

## 2. The new metric — input nonlinearity factor γ

### 2.1 Definition (owner's, verbatim in substance)

γ is complex, and is defined on the **intrinsic control voltage** — the intrinsic gate-source
voltage, not a terminal quantity:

- `|γ| = |V₂| / |V₁|` — magnitude of the 2nd-harmonic intrinsic control voltage over the magnitude of
  the fundamental.
- `∠γ = φ₂ − 2·φ₁` — the 2nd harmonic's phase **minus twice** the fundamental's.

Note the `2·`. It is not `arg(V₂/V₁)`. In closed form:

```
γ = V₂ · conj(V₁)²  /  |V₁|³
```

(check: with `V₁ = |V₁|e^{jφ₁}`, `V₂ = |V₂|e^{jφ₂}`, the numerator is `|V₂||V₁|²e^{j(φ₂−2φ₁)}`.)
Implement it that way — one expression, no separate magnitude/angle assembly that can disagree with
itself. Guard `|V₁| == 0` (and NaN) by returning `Complex(NaN, NaN)`, which every formatter here
already renders as `"—"`.

Backing reference, for the tooltip and the doc comment:
S. K. Dhar, T. Sharma, N. Zhu, D. Holmes, R. Darraji, F. M. Ghannouchi, "Comprehensive Analysis of
Input Waveform Shaping for Efficiency Enhancement in Class B Power Amplifiers," *2019 IEEE MTT-S
International Microwave Symposium (IMS)*, Boston, MA, USA, 2019, pp. 1164–1167.

### 2.2 Where the data already is — do not recompute a spectrum

`HarmonicaDataSet.Build` publishes `V_intr` as a `[port, harmonic]` complex cube
(`src/Harmonica/HarmonicaDataSet.cs:145`), built by `IntrinsicPlane`. The **gate** port index is
`ctx.IntrinsicPorts.GatePort` (`IntrinsicPortMap`; `TwoPort` = gate 0, drain 1). Harmonic index 1 is
f₀ and 2 is 2f₀ — the same indexing `AddIntrinsicColumn` already uses for the drain.

`HarmonicaSolver` already has a `ReadComplex(ds, cubeName, portIndex, harmonic)` helper
(`HarmonicaSolver.cs:832`) that returns `NaN` rather than a substituted zero when the cube or the
index is absent. Use it. §0.3 item 1 of the harmonicaRF design note — *never recompute a spectrum in
a view model* — is the standing rule and it has been violated in this codebase before.

### 2.3 Three chunks, three γ

> "Add this metric to the harmonicaRF readout, under the Pdc readout for P-3dB, MXP and MXE. (Note
> that γ must be calculated 3 times - one for each readout)."

Each chunk has its own `DataSet` and they are **different operating points** — this is the whole
reason it is computed three times, and reusing one for all three would be silently wrong:

| chunk | DataSet in scope | where |
|---|---|---|
| P-3dB / operating point | `published` | `BuildReadouts`, the `if (at is not null)` block |
| MXP | `ds` (from `optimum.Published`) | `AddMxColumn` |
| MXE | `ds` (from `optimum.Published`) | `AddMxColumn` |

Row position: **immediately after `Pdc`**, which is the last row of all three chunks today.

### 2.4 Formatting — magnitude ∠ angle, always

> Owner, follow-up: "γ (the input nonlinearity) readout should only ever [be] displayed as mag/angle.
> (Real/Imag format does not make sense because of the way it is defined)."

That constraint is best enforced **structurally, not by a default**: build the γ row with
`IsComplex: false` and a `Value` string already formatted as magnitude ∠ angle at solve time.

Why that specific choice:

- `BuildColumnRowShell` attaches the real/imaginary ⇄ magnitude/angle flyout **only** when
  `item.IsComplex` (line 480). With `IsComplex: false` there is no menu, so no user action can put γ
  into a format that does not mean anything. A "default of MagnitudeAngle" would leave the menu right
  there.
- `HarmonicaReadout.FormatKey` (`HarmonicaFrame.cs:123`) resolves *any* complex row in the
  OperatingPoint column to `"OP.Zin"` and any in Mxp/Mxe to `"MXP.Zin"`/`"MXE.Zin"`. A γ row marked
  `IsComplex: true` would therefore **share Zin's saved format state**, and `DisplayValue` would run
  it through `FormatZ` and append a `" Ω"` to a dimensionless number. Both are silent wrong answers.
  Marking it non-complex sidesteps the whole collision; if you instead extend `FormatKey`, you must
  handle both of those and you have gained nothing.

Add one formatter beside the others in `HarmonicaReadoutFormatting`:

```csharp
/// <summary>R7C §2 — the input nonlinearity factor, ALWAYS magnitude ∠ angle …</summary>
public static string FormatGammaFactor(Complex g)
    => double.IsNaN(g.Real) || double.IsNaN(g.Imaginary) ? "—"
     : FormatComplex(g, ReadoutFormat.MagnitudeAngle);
```

Row label `γ`, `Unit = ""` (dimensionless; the angle's `°` is attached to the number, like AM/PM).
Give `ValueChars`/§1.3's worst-case table its own entry keyed on the label.

Tooltip, in full — this row is the one thing on the strip a user will not recognise:

> Input nonlinearity factor. |γ| is the magnitude of the 2nd-harmonic intrinsic control voltage over
> the fundamental's; ∠γ is φ₂ − 2·φ₁. Read from the intrinsic gate port's own V_intr spectrum at this
> operating point — used for input waveform shaping in high-efficiency PA design (Dhar et al., IMS
> 2019).

### 2.5 When it cannot be computed

Show `"—"` (which `FormatGammaFactor` already produces from a NaN) when any of these hold, and keep
the row present so the chunk's shape never changes (§1.4's rule):

- `ctx.IntrinsicPorts.GatePort < 0`, or the intrinsic plane is not located
- the DataSet is null, or carries no `V_intr`
- `Settings.HarmonicCount < 2` — there is no 2nd harmonic to read
- `|V₁| == 0`

The tooltip should say which of those it is when it can — `IntrinsicPortMap.Reason` already carries
the text for the "not located" case, and `"K = 1: no second harmonic is solved"` covers the other one
the user can act on.

### 2.6 Gate for §2

`tests/Ui.Tests/Harmonica/HarmonicaGammaFactorTests.cs`, pure (no Avalonia):

1. **The definition, against a hand-computed oracle.** `V₁ = 2∠30°`, `V₂ = 0.5∠100°` →
   `|γ| = 0.25`, `∠γ = 100 − 60 = 40°`. Assert both to 1e-12. Add a case where `φ₂ − 2φ₁` wraps past
   ±180° and assert the wrapped principal value that `Complex.Phase` returns, so the wrap is pinned
   rather than discovered later.
2. **Not `arg(V₂/V₁)`** — the same inputs give `arg(V₂/V₁) = 70°`. Assert the row does **not** show
   70°. This is the mistake the implementation will make if the closed form is "simplified".
3. Solve a real frame (`HarmonicaReadoutColumnsTests.NewSolvedVm()` is the existing pattern) and
   assert a `γ` row exists in each of the OperatingPoint, Mxp and Mxe columns, immediately after that
   column's `Pdc` row.
4. Assert the three γ values are read from three different DataSets — at a minimum, that MXP's γ and
   MXE's γ are not bit-identical to the operating point's on a fixture where the optima differ.
5. Assert `IsComplex == false` on the γ row (this is what §2.4 relies on; a later refactor that
   flips it must fail here).
6. `K = 1`: the row is present and reads `"—"`.

---

## 3. Out of scope

- Do not re-lay-out the 2 × 4 chunk grid; R-hui-1 settled it.
- Do not change which rows exist, other than adding γ.
- Do not change `FixedWidth`'s decimals/budgets — the owner accepted digit-count changes as legitimate
  (`HarmonicaReadoutFormatting.cs:16–29`); it is the *cell* that must not move, not the string.
- Do not touch the engine, the solver's numerics, or `IntrinsicPlane`.

---

## 4. Gates

1. `dotnet test tests/Ui.Tests --no-build` and `dotnet test tests/Firewall.Tests --no-build` green
   (separate invocations). Existing strip tests — `HarmonicaR6cStripTests`,
   `HarmonicaR3cStripTests`, `HarmonicaReadoutColumnsTests`,
   `HarmonicaInlineEditSelectabilityTests`, `HarmonicaR5StripRebuildTests` — will need updating for
   the two-column shape and the label text; update them to assert the **new** contract, do not delete
   assertions.
2. The new tests in §2.6.
3. **Run the app** (`/run`, or `dotnet run --project src/Ui`) and produce these screenshots. They are
   the gate; nothing else in this repo can see any of it:
   - the whole readout strip at rest, with a ruler or a straight edge drawn over it (or simply
     cropped tightly) showing every value in a chunk starting at the same x;
   - **a drag in progress**: grab a marker and hold it while the numbers are changing, and capture
     two frames a few hundred ms apart. Overlay or diff them. Nothing except the digits themselves may
     move — no column, no chunk, no row height. This is the specific claim that failed five times;
     prove it with two frames, not with an assertion that it looks fine.
   - the MXP/MXE chunks **during** that drag, showing nine rows of `"—"` rather than a collapsed
     single row (§1.4);
   - the γ row under Pdc in all three chunks.
4. Resize the harmonicaRF window from small to large and back. The font size is a function of the
   panel's pixel size (`FontSizeFor`), so this exercises the measurement cache from §1.3 across many
   sizes. Nothing may overflow its cell at any size.
5. Report, explicitly:
   - the answer to §1.5's `IsSharedSizeScope` question — does Avalonia 12.0.3 honour it on a
     `StackPanel` host? This is worth writing to `src/Ui/RESOLVED.md` whichever way it goes;
   - the measured pixel width of the widest complex value at the strip's typical font size, next to
     what `22 * fontSize * 0.55` would have reserved — i.e. how wrong the old constant was;
   - whether §1.4's row-count churn was in fact happening (it should be visible as a flicker before
     your change).
