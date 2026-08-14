# Brief — harmonicaRF Round 3C: the strip's columns, the Smith titles, and the efficiency axis

**Read first:** `docs/design/harmonicarf.md` **§7.1**, **§7.2**, **§7.4**, **§7.5**, then
`src/Ui/Views/Harmonica/ReadoutStripView.axaml(.cs)` in full, `src/Ui/Harmonica/HarmonicaInputs.cs`,
`HarmonicaSolver.BuildReadouts` / `AddMxColumn` (`src/Ui/Harmonica/HarmonicaSolver.cs` ~514–695), and
`HarmonicaPanelRenderer`'s title band (~lines 40–200) and `DrawEfficiencyAxisOverlay` (~700–800).

Five changes, all owner-reported, all in `src/Ui`. Two of them (§3, §5) are one-line-ish once you know
where; §1 and §2 are a real rearrangement of the strip. **§5's cause is already found by reading —
verify it, do not re-derive it.**

---

## 1. The settings inputs become a column of inline-editable text

> **owner:** *"Vgs, Idq, Vds, f0, K, compr, and Z0 textedit elements on the display need to change to
> regular text UI elements wired to our inline text editor so that the user can change them by
> double-clicking. (We currently do this for other text elements in the UI.) Change the layout
> arrangement of these elements from a row to a column. The column should be placed hugging the left
> side of the window (with padding), to the left of the Source column of data."*

Today `ReadoutStripView.SetInputs` renders every `HarmonicaInput` as a live `TextBox` in a horizontal
`WrapPanel` named `Inputs`. The target is the **same shape the Source/Load columns already use**:
label + value `TextBlock`, double-click swaps in a `TextBox` in place, Return commits, Escape reverts,
LostFocus commits. `ReadoutStripView.BeginInlineEdit` is that editor and it already exists — reuse it,
do not write a second one.

**Layout, left to right, in the `Columns` `StackPanel`:**

```
[ Settings ] [ Operating point ] [ Source ] [ Load ] [ MXP ] [ MXE ]
```

Settings hugs the left edge with the strip's existing padding. §2 defines the Operating-point column.

### 1.1 — Which inputs move, and what happens to the rest

The owner named seven: **Vgs, Idq, Vds, f₀, K, compr, Z0** (`HarmonicaInputs.KeyVgs`, `KeyIdq`,
`KeyVds`, `KeyFrequency`, `KeyHarmonicCount`, `KeyCompression`, `KeyZ0`). `HarmonicaInputs.Build`
produces four more fixed inputs — **loadline pts, FFT×, charge, M** — plus every parameter the loaded
model declares (`DeclaredModelParameters`), which for an external model can be dozens.

**Move the seven named ones into the Settings column, in the owner's own order.** For the rest:

- **`charge` is a `CheckBox`**, not text — an inline text editor does not apply to it. Keep it a
  checkbox wherever it ends up.
- **Do not silently drop anything.** The remaining fixed inputs and the model parameters must still be
  reachable. Keeping them in the existing horizontal `Inputs` wrap run above the columns is the
  cheapest correct answer and is what this brief expects; if you put them in the Settings column
  instead, say so and show it.
- Model-declared parameters can be numerous and long — an SDD's are already excluded from the strip
  (R-h9c-5), but an external model's are not. Do not let them push the columns off screen.

**State your disposition for every input the owner did not name.** A list of what went where, in the
completion note.

### 1.2 — Two traps in this file that will bite you

1. **`SetInputs` deliberately does NOT rebuild when only values change.** Its `_inputSignature` guard
   plus `UpdateInPlace` exist because the strip is refreshed on **every published frame**, and a
   rebuild would destroy the `TextBox` the user is typing in. Read that comment before you touch it.
   Moving to double-click editing makes the *steady state* immune (there is no persistent `TextBox`),
   **but it does not make an OPEN inline editor safe** — `SetItems` clears and rebuilds all four
   columns on every call, so a refresh while an editor is open destroys it mid-edit.

   **Handle this explicitly.** Either keep the shape-signature/update-in-place discipline for the new
   column, or suppress the strip rebuild while an inline editor is open. Pick one, state it, and gate
   it: "an open editor survives a published frame" is a test you can write without a window if the
   decision lives in a pure helper.

2. **A structural input carries a `*` marker**, not a colour (§7.9.2 reserves red, and the strip has
   exactly one text role). `f₀` and `K` are structural; keep the marker and keep the "(structural —
   changing it rebuilds the context and resets the frame ladder)" tooltip suffix.

### 1.3 — Write-back is unchanged

`HarmonicaInputs.Apply(model, key, text, out error)` stays the one write-back path, error strings and
all, and a rejected edit still shows through `SetInputError` rather than throwing. **Do not move
validation into the view.**

---

## 2. The operating-point readouts become a column too

> **owner:** *"Change the results rendered on the display at the L1 marker (Pin, Pout, Gain, DE, PAE,
> Pdc) from a row arrangement to a column arrangement. Place the column to the right of the Vgs, Idq,
> Vds… etc. column in the display."*

These six are built in `HarmonicaSolver.BuildReadouts` under `ReadoutColumn.General` (lines ~556–562)
and rendered into the `Items` `WrapPanel`. Add a `ReadoutColumn` member for them — name it for what it
is (the figures at the operating point), not for its screen position — route those six rows to it, and
give it a `StackPanel` in `Columns` immediately after Settings.

**Give it a header row** in the same style MXP/MXE already use (label only, empty value and tooltip —
`BuildColumnRow`'s `isHeader` path). What it is headed with matters: these are the figures at the
compression point (or at the user-placed cursor, R-h6-11), which is exactly what `HarmonicaTitles`
already knows how to spell. Reuse it rather than composing a new string here.

**Whatever is left in `ReadoutColumn.General` after §3** — the `intrinsic: not located` row, which
only ever appears when the intrinsic plane could not be resolved — keeps the existing wrapping run.
Do not delete the General path.

---

## 3. Remove the duplicated f₀, Vds and Vgs

> **owner:** *"The following data display fields are duplicated — one of the duplicates should be
> removed: f0, Vds, Vgs."*

They are duplicated between the two halves of the strip:

| field | as an INPUT (`HarmonicaInputs.Build`) | as a READOUT (`BuildReadouts`) |
|---|---|---|
| f₀ | `KeyFrequency`, editable, GHz | `Add("f₀", $"{…/1e9:0.###} GHz", …)` |
| Vds | `KeyVds`, editable, V | `Add("Vds", $"{…:0.##} V", …)` |
| Vgs | `KeyVgs`, editable, V | `Add("Vgs", …, "(from Idq)" when solved)` |

**Remove the READOUT copies** (`HarmonicaSolver.BuildReadouts` lines ~535–538) and keep the editable
inputs — the input is strictly more capable and, after §1, it renders as text anyway.

**One thing is lost and you must not lose it silently.** The Vgs *readout* shows `"(from Idq)"` when
the bias is current-driven and Vgs was solved by the secant; the Vgs *input* shows an empty box in
that case. After this change there would be nowhere to see the solved Vgs. **Surface the solved value
on the Vgs input** — as the placeholder/greyed text of the row, or in its tooltip — and say which you
chose. Do not just delete the row and move on.

---

## 4. The Smith chart titles: 85% size, and closer to the chart

> **owner:** *"Make Smith chart title text size 85%. Also move the title down (both row 1 and row 2)
> so it renders closer to the Smith Chart. (I.e. the bottom of row 2 text should be above the Smith
> chart with some padding.)"*

Both rows are drawn by `HarmonicaPanelRenderer.DrawTitleRows` and their band is reserved by
`TitleBandHeight`, which is also folded into `GammaToCanvas`/`CanvasToGamma`. **That coupling is
load-bearing and is the fix for R1B's worst bug** — a non-empty `Plot.Title` used to shift the
rendered chart down from where the hit test placed it, so every marker and grid point was drawn in one
place and grabbed in another. Read `TitleBandHeight`'s and `DrawTitleRows`' own comments before
editing.

- **Size:** the current font is `Math.Max(7.0, m * TitleRowFontFraction * TitleFontShrink)` with
  `TitleRowFontFraction = 0.052` and `TitleFontShrink = 0.8`. 85% of *current* means one more factor
  of 0.85. Put it in a **named constant** with the owner's request recorded — the existing constants
  were made variables for exactly this reason (R-h9r2-21's own note in `ReadoutStripView`) and this is
  the second such tweak. Note the 7.0 pt floor: at small panel sizes the shrink is swallowed by the
  clamp, so check whether the floor should scale too and state what you decided.
- **Position:** the rows already measure **backward from the band's bottom** (`DrawTitleRows`' own
  note, R-h9r2-13). Keep that. What the owner is asking for is that the gap between row 2's baseline
  and the chart's top becomes a small explicit padding rather than whatever falls out — so name that
  padding as a constant too, and let `TitleBandHeight` be `rows + padding` rather than a magic
  fraction.
- **Never let render and hit-test derive the band separately.** Both must keep going through
  `TitleBandHeight`. A test that drives `GammaToCanvas` and `CanvasToGamma` round-trip at the new font
  size, and asserts a marker's drawn position equals its hit-test position, is the gate — that is the
  bug this coupling exists to prevent and it must stay pinned.

**Both Smith charts and any other panel that reserves the band must move together.** One formula, two
charts.

---

## 5. The efficiency axis draws a green line under the red one

> **owner:** *"The right y-axis of the power sweep plot currently renders a vertical red line over top
> of a green. Do not render the green line underneath it."*

**Found by reading.** Two paints draw the same segment:

`AxesRenderer.DrawBorder` (called from `PlotRenderer.Draw`) draws the right border when
`axes.ShowSecondary`, through:
```csharp
private static SKPaint StrokePaint(SKColor color, float width) => new SKPaint
{
    Color = color, StrokeWidth = width, Style = SKPaintStyle.Stroke,
    IsAntialias = true, StrokeCap = SKStrokeCap.Square
};
// width: 2f * (float)axes.GridThicknessFactor * lw, colour: theme.BorderColor
```

`HarmonicaPanelRenderer.DrawEfficiencyAxisOverlay` (R-h9b-9) then repaints the same two endpoints in
`theme.EfficiencyTrace` — same width, **but `IsAntialias = false` and the default butt cap**:
```csharp
using var linePaint = new SKPaint
{
    Style = SKPaintStyle.Stroke, IsAntialias = false,
    Color = theme.EfficiencyTrace, StrokeWidth = 2f * (float)axes.GridThicknessFactor * lw,
};
```

An antialiased stroke covers a wider footprint than a hard-edged one of the same nominal width, and a
`Square` cap extends half a stroke-width past each endpoint where a butt cap does not. **So the
underlying border shows as a fringe along both sides of the cover and past both ends.** That is the
green the owner sees. Confirm it (render at a large size and inspect, or reason it through from the
two paints) and state the confirmation.

**Two candidate fixes:**

- **(a) Do not draw the underlying segment at all** — literally what the owner asked. This needs
  `AxesRenderer.DrawBorder` to be able to omit one side, i.e. a shared-renderer change.
- **(b) Make the cover match the covered exactly** — `IsAntialias = true`, `StrokeCap = Square`, same
  width, same endpoints. Entirely inside `HarmonicaPanelRenderer`.

**Take (b) unless you can show it does not fully hide the line**, and say so plainly in the completion
note including that it is a cover rather than a suppression. The standing rule
(`brief-harmonicarf-r2a` §5, the `AnnulusHeadroom` precedent, and `DrawEfficiencyAxisOverlay`'s own
doc comment) is **never widen `PlotRenderer` / `AxesRenderer` for a harmonicaRF need.** If (b)
genuinely cannot work, (a) is allowed only as a **general** capability — a per-side border mask on
`Axes` that any plot could use, with its own Data Display test — never a harmonicaRF special case, and
you must say why (b) failed.

**Check the tick marks and the tick numbers for the same fringing** while you are in there — they are
drawn by the same two-pass pattern with the same antialias mismatch (`tickPaint`, `IsAntialias =
false`, against `AxesRenderer`'s antialiased ticks). Fix them the same way or state that they do not
show the artifact.

---

## 6. Scope guardrails

- **No menu work** (**R3A**), **no solver, frame-loop, drag or loadpull work** (**R3B**).
- **`HarmonicaReadoutFormatting` stays the ONE formatter**, and R-h9r2-25's render-time formatting from
  `RawValue` stays — a right-click format change must still repaint with no re-solve.
- **`HarmonicaTitles` stays the ONE formatter** for the title rows and the MX headers.
- **Keep `SelectableTextBlock` for non-editable rows and plain `TextBlock` for editable ones** —
  R-h9r2-15's finding is that `SelectableTextBlock` eats the double-tap before the inline editor sees
  it, and `HarmonicaInlineEditSelectabilityTests` pins both halves. §1 makes seven more rows editable,
  so this rule now covers them too.
- **§7.5's density constraints hold**: no section titles, no decoration, every element tooltipped, all
  non-editable text selectable.
- **No `.charm` `FormatVersion` bump** — nothing here is persisted state.
- `src/Core`, `src/Engine`, `RfCore`, `src/Harmonica` untouched. (`ReadoutColumn` lives in `src/Ui`;
  confirm before adding a member.)

---

## 7. Gates

1. **Build + `dotnet test` green** — `tests/Ui.Tests` and `tests/Firewall.Tests` while working, full
   solution at the end.
2. **The seven named inputs render as text and open the inline editor on double-click**, commit on
   Return and LostFocus, revert on Escape, and a rejected value shows through `SetInputError` without
   clearing what the user typed.
3. **An open inline editor survives a published frame refresh** (§1.2 trap 1), gated.
4. **Column order is Settings · Operating point · Source · Load · MXP · MXE**, gated on the panel
   composition rather than by eye.
5. **f₀, Vds and Vgs appear exactly once each** in the whole strip — gated by scanning the built
   readout + input lists for duplicate labels, so a future addition cannot quietly reintroduce one.
6. **The solved Vgs is visible when the bias is Idq-driven** (§3), gated.
7. **The Smith title rows are 85% of their previous size and the band is rows + a named padding**, with
   the constants named and the numbers quoted in the completion note.
8. **`GammaToCanvas`/`CanvasToGamma` still agree with what is drawn** at the new font size — the R1B
   render-vs-hit-test regression stays pinned.
9. **No green fringe on the right axis**, with the chosen route (a) or (b) named and justified.
10. **`HarmonicaInlineEditSelectabilityTests` still passes** and is extended to cover the new editable
    rows.

**Interactive verification is required** — no visual driver exists here. List the gestures in the
completion note under "please confirm on your end": double-click each of the seven settings and edit
it; type something invalid and confirm the error shows and the value is kept; resize the window and
confirm the columns and the Smith titles stay sane at small and large sizes; look at the right axis of
the power-sweep plot at 100% and zoomed.

---

## 8. Write-up — READ THIS BEFORE YOU FINISH

**Do NOT append a phase write-up to `src/Ui/CLAUDE.md`.** That file reached 21,417 lines that way and
had to be archived to `src/Ui/HISTORY.md`; its own maintenance rule says a completed phase's narrative
does not belong there.

Instead: **create (or extend) `src/Ui/RESOLVED.md`**, following the shape of the existing
`src/Ui/DataDisplay/RESOLVED.md` — a title, a short note about why the file exists, then one `##`
section per completed brief. Brief R3A creates the same file; if it has landed, add a section rather
than a second file.

**Use it sparingly — only truly important findings.** For this brief that is a short list, probably
just:

- **the antialias/cap mismatch behind the two-colour axis line (§5)** — a genuinely non-obvious
  rendering trap that will recur anywhere one renderer covers another's stroke;
- **the strip-rebuild-destroys-an-open-editor hazard (§1.2)** and how you closed it, because the
  existing `_inputSignature` guard exists for the same reason and the next person will meet it again;
- the title band's render/hit-test coupling, **only if you found something the existing comments do not
  already say** — they say most of it.

Everything else — which control moved where, the column order, what you renamed — belongs in the
completion note you hand back, not in a checked-in file. If `src/Ui/CLAUDE.md` needs anything at all,
it is at most one line pointing at `RESOLVED.md`.
