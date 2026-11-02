# Sonnet Brief — Cell-first creation, filename defaults, S-parameter card units, FPS removal

Five owner items. Four are contained; the units bug (§4) has a root cause worth fixing properly rather than
patching.

**Sequencing:** §2 changes a keyboard accelerator, which `brief-file-menu-restructure.md` touches. That brief
says to *preserve* existing accelerators; **this brief supersedes it for `Cmd+Shift+N` specifically**. If the
menu restructure has not landed, apply this after it.

Gate command is plain `dotnet test`.

---

## 1. New Cell creates and opens a default schematic

Today `File ▸ New ▸ New Cell…` creates the cell folder and its `.ccell` and stops, leaving an empty cell.

**R-cc-1. Creating a cell also creates `<CellName>.csch` as that cell's primary schematic, and opens it in a
new tab.** The user lands on a blank schematic ready to draw, which is what they wanted the cell for.

- **Reuse the existing creation paths** — `CellFolder.CreateCellFolder` for the cell, and whatever
  `New Schematic` already calls for the view. Do not write a second schematic-creation path; if the two
  diverge, one of them will stop setting some field correctly.
- The schematic is **written to disk and opened clean**, not as a scratch document. The cell now has a real
  primary, which is what `ResolvePrimary` and every reference-resolution path expect.
- **If the schematic cannot be created, the cell still exists** — report the failure and leave the cell in
  place rather than rolling back. A cell with no view is recoverable; a half-deleted cell is not.

## 2. `Cmd+Shift+N` moves from New Schematic to New Cell

**R-cc-2. Rebind `Cmd+Shift+N` to `New Cell…`.** The intent is to steer users toward cells rather than
orphaned schematic documents, and the accelerator is the strongest steer available.

**Leave `New Schematic` unbound.** Do not invent a replacement accelerator for it — a new binding would
undo the steer this change exists to create. Both menu surfaces (in-window and the macOS native menu) must
agree.

## 3. Project-tree context menu suggests a filename

`New Schematic`, `New Symbol` and `New Layout` in the tree currently open a naming prompt with no useful
default.

**R-cc-3. Default the name to the cell's name; if that is taken for this view type, append the next free
numeric suffix.** So a cell `Amp` suggests `Amp`, then `Amp2`, then `Amp3`.

- **Scan every existing file of that view type in the cell, not just the primary.** With `Amp.csch` and
  `Amp2.csch` present, the next suggestion is `Amp3` — checking only the primary would suggest `Amp2` and
  collide.
- **Pick one suffix convention and state it** — suggest bare numerals (`Amp2`), matching whatever the
  existing duplicate-naming does if there is a precedent. Do not mix `Amp2` and `Amp_2`.
- **Pre-select the suggested text** in the box so typing replaces it. A default the user must delete first
  is worse than no default.
- Handle a cell whose name already ends in digits (`Amp2` → `Amp2_2` or `Amp3`, whichever the chosen
  convention gives) without producing something ambiguous.

**Note the interaction with §1:** once every new cell ships with `<Cell>.csch`, the tree's `New Schematic`
will *always* take the suffix path. That makes this the common case rather than an edge case, so it needs to
be right rather than merely present.

## 4. Bug: the S-parameter card shows Hz instead of the chosen unit

A 1–10 GHz sweep displays as "1 Hz–10 Hz".

### 4.1 Cause

`AnalysisRowViewModel.FormatFreq` parses the sweep's **expression string** as a raw double and assumes the
result is already in hertz:

```csharp
if (double.TryParse(expr, …, out double hz))
{
    if      (hz >= 1e9)  return $"{hz / 1e9:G3} GHz";
    …
    else                 return $"{hz:G3} Hz";
}
return expr;
```

But a frequency field is authored as **a coefficient plus a unit from a dropdown** —
`AnalysisPreviewHelper.ComputeFreqPreview(coeff, fieldUnit, model)` says so in its own signature and doc
comment. `StartExpr` holds `"1"`; the `GHz` lives in a separate unit field that `FormatFreq` never reads. It
parses 1.0, finds it below 1e3, and prints `1 Hz`.

### 4.2 Fix — and the trap that makes the obvious fix wrong

**R-cc-4. Delete `FormatFreq` and format through `AnalysisPreviewHelper.ComputeFreqPreview`.** It is a
*second*, private frequency formatter that disagrees with the one the editor already uses — which is why the
card and the editor show different values for the same analysis. Two formatters is the defect; adding the
unit to this one would leave two formatters that merely agree today.

**The obvious fix — "read the unit dropdown and multiply the coefficient by it" — is wrong, and there is prior
art saying so.** `ComputeFreqPreview`'s own doc comment records the rule:

> the dropdown is the **site** unit; the evaluator's **var-unit-wins** rule lets a unit-bearing reference
> (e.g. a GHz var) apply its *own* unit instead, so a mixed-unit compound (`RFfreq + Voff`) resolves exactly
> rather than via a single shared multiplier.

**R-cc-4a. Preserve var-unit-wins.** A field whose dropdown reads `GHz` and whose expression is `RFfreq`,
where `RFfreq` is itself declared in GHz, must **not** be scaled by 1e9 twice. `ComputeFreqPreview` gets this
right because it passes the site unit to the evaluator as `unit:` and lets the evaluator decide per
reference; a formatter that multiplies the resolved coefficient by a dropdown factor cannot.

**R-cc-4b. Mixed-unit compounds must resolve per-term.** `RFfreq + Voff`, with the two carrying different
units, is the case a single shared multiplier gets wrong — and it is named explicitly in the helper's comment
because it was the motivating example. It belongs in the gate.

### 4.3 Do not touch the parametric-sweep path

There are **two** resolution paths in `AnalysisPreviewHelper` and they are deliberately different:

| | Scope | Units |
|---|---|---|
| `ComputeFreqPreview` | `DesignScope.BuildResolved` | **bound** — site unit passed as `unit:`, var-unit-wins applies |
| `TryResolveCoefficient` | `DesignScope.Build` | **stripped** — "used by the parametric-sweep row, which applies its own unit scaling on top of the coefficient" |

**R-cc-4c. The parametric-sweep row keeps `TryResolveCoefficient` and its own scaling. Do not route it
through `ComputeFreqPreview`.** The sweep applies units itself, downstream; binding them again in the
resolver would multiply twice. `FormatSweepSummary` reads `psa.SweepValues`, which are already past that
scaling — it is not part of this bug and must not be "unified" with the frequency path.

If the sweep summary reads awkwardly for lacking a unit label, that is a **separate** question to raise with
the owner, not something to fix by reusing the frequency formatter.

**R-cc-4d. A frequency that references a swept variable can only show its nominal value.**
`ComputeFreqPreview` resolves against the design scope, so a card whose sweep endpoint is `RFfreq` shows
`RFfreq`'s nominal resolution — not the range the parametric sweep will drive it over. That is correct and
honest, and it should stay. **Report what the card does here in the completion note** rather than changing it;
whether to indicate the dependency is the owner's call.

**Surface unresolved names.** `ComputeFreqPreview` returns `unknown: <name>` for an `UnresolvedNameException`.
The card should show that rather than silently falling back to raw expression text, which is what
`FormatFreq` does today.

**Check the other summaries while there.** `FormatHbSummary` calls the same `FormatFreq` for the harmonic-
balance tone, so it has the identical bug. Audit every caller rather than fixing the S-parameter path alone.

## 5. Remove the FPS readouts from the schematic editor

Two sites, as reported:

| | Where |
|---|---|
| **Canvas overlay** | `SchematicRenderer.DrawFpsOverlay` (~line 1230), called at ~90 and ~356 |
| **Toolbar readout** | `SchematicView.axaml` ~353 — `<TextBlock x:Name="FpsText">`, plus whatever updates it in the code-behind |

The plumbing that feeds them: `SchematicCanvas.ShowFpsProperty` / `_showFps` / `ShowFps` (~143–153), passed
through the draw operation (~307, 898–923), and `ShowFps="True"` in `SchematicView.axaml` ~424.

**R-cc-6. Remove the plumbing, not just the drawing.** `showFps` parameters, the `ShowFps` property, the
XAML attribute and the `FpsText` element all go. A rendering path that still threads a now-unused flag is
the residue that makes the next reader think the feature exists.

**Two things to check rather than assume:**

- **Does `previousFrameTicks` / the renderer's `Stopwatch` serve anything besides the overlay?** If not, it
  goes too. If it does, keep it and say what else uses it.
- **Is `SchematicRenderer` shared with the symbol editor?** The owner asked for the *schematic* editor. If the
  renderer is shared and the symbol editor also shows an FPS readout, **report it and ask** rather than
  removing it silently — it may be wanted there, or may be equally unwanted, but that is his call.

## 6. Guardrails

- Do not change what `New Cell…` does beyond adding the schematic (§1), or add view creation for symbol or
  layout — schematic only.
- Do not add a replacement accelerator for `New Schematic` (§2).
- Do not patch `FormatFreq` in place; remove it (§4).
- Do not remove FPS from the layout editor or anywhere else unless §5's check says the code is shared and
  the owner has agreed.
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 7. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **New Cell (R-cc-1)** — creates the folder, the `.ccell`, and `<CellName>.csch`; the schematic is the
   cell's resolved **primary**; it opens in a new tab as a clean document; the tree shows both. Assert it
   routes through the existing creation paths, not a copy.
3. **Failure is partial, not fatal** — with schematic creation forced to fail, the cell still exists and the
   failure is reported.
4. **Accelerator (R-cc-2)** — `Cmd+Shift+N` invokes New Cell in **both** menu surfaces; New Schematic has no
   accelerator.
5. **Name suggestion (R-cc-3)** — cell `Amp` with no views suggests `Amp`; with `Amp.csch` suggests `Amp2`;
   with `Amp.csch` and `Amp2.csch` suggests `Amp3`. The suggestion is pre-selected. Same behaviour for symbol
   and layout, each scanning only its own view type.
6. **Units (R-cc-4)** — a 1–10 GHz S-parameter sweep displays as `1 GHz–10 GHz`; MHz and kHz sweeps display
   correctly; **the card and the analysis editor show the same text for the same analysis**.
6a. **Var-unit-wins (R-cc-4a)** — a field with dropdown `GHz` whose expression is a variable **also declared
   in GHz** resolves once, not twice. A formatter that multiplies by the dropdown factor fails this, which is
   the point of the test.
6b. **Mixed-unit compound (R-cc-4b)** — `RFfreq + Voff` with differing units resolves per-term, matching the
   editor's preview exactly.
6c. **Parametric sweep untouched (R-cc-4c)** — sweep values are identical before and after this change;
   `TryResolveCoefficient` still uses the unit-stripped scope; a swept frequency is not double-scaled.
   Assert the sweep row's numbers, not just that it compiles.
6d. **Unresolved name** — a sweep referencing an undefined variable shows `unknown: <name>`, not raw text.
7. **Expressions** — a sweep authored as a named variable renders with its resolved value and unit, not as
   bare expression text.
8. **Harmonic balance** — the tone summary shows the correct unit (same root cause).
9. **FPS gone (R-cc-6)** — no FPS text renders on the schematic canvas or beside the toolbar; grep confirms
   no `ShowFps`, `showFps` or `FpsText` remains in the schematic path.

## 8. On completion

Record in `src/Ui/CLAUDE.md`: that **New Cell now creates and opens a primary schematic**, and that it reuses
the existing creation paths; the `Cmd+Shift+N` rebind and that it **supersedes the menu-restructure brief's
accelerator-preservation rule** for that one binding; the filename-suffix convention chosen; **that
`FormatFreq` was a second frequency formatter disagreeing with `AnalysisPreviewHelper`**, which is why the
card and editor differed, and that it was removed rather than patched; and the answers to §5's two checks
(the stopwatch, and whether the renderer is shared with the symbol editor).
