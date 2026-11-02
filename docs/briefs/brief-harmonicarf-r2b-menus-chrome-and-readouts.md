# Brief — harmonicaRF Round 2B: the menu bar, the chrome, and the readouts

**Read first:** `docs/design/harmonicarf.md` (**§7.1**, **§7.4–7.6**, **§7.9**), then
`src/Ui/CLAUDE.md`'s **H7**, **H8**, **R1A**, **R1B** and **R1C** entries, and this file's own
"harmonicaRF — closing a torn-off window crashed the app" note (it is the same class of bug as §1.2).

**Round 2 is two briefs and they are independent.** **2A** is the frame loop, the drag and the markers.
This one is everything the user reads rather than drags: the macOS menu bar (docked, and a crash), the
Smith titles, the DCIV defaults, the inline editor, the power-sweep range, Set Z0, the readout strip's
size and contents, and the format flyout.

**Two of these are already root-caused by reading, and one of the two changes what "fix it" means —**
see §4.1 and §8.1. Do not re-derive them.

---

## 0. What already exists

| you need | it is here |
|---|---|
| both menu surfaces | `HarmonicaMenuView.axaml(.cs)` (`src/Ui/Views/Harmonica/`) — ONE `NativeMenu` instance + one in-window `Menu`, hand-mirrored |
| the menu commands | `HarmonicaMenuViewModel` (`src/Ui/Harmonica/`) |
| the docked-focus seam | `WorkspaceViewModel.UpdateHarmonicaDockedMenuFocus` → `HarmonicaDocument.NativeMenuDockedFocusChanged` → `HarmonicaMenuView.SetDockedFocus` |
| the Smith titles | `HarmonicaTitles` (`src/Harmonica/`) — the ONE formatter; `HarmonicaPanelRenderer.DrawTitleRows` / `TitleBandHeight` draw and reserve them |
| the DCIV defaults | `DcivFamily.DefaultKey` / `OverrideOf` / `ResolvedKey` / `IsValidOverride` (`src/Harmonica/`) |
| the Pin search | `PinSearch.Run` (`src/Harmonica/`) + `HarmonicaSettings.PinStartDbm` / `PinMaxDbm` — **read §5.1 before touching these** |
| the settings block | `HarmonicaSettings` (`src/Harmonica/CircuitModel.cs`) — persisted via `CharmIo`, absent field ⇒ default, no version bump |
| the input rows | `HarmonicaInputs` (`src/Ui/Harmonica/`) — `KeyZ0`, `KeyCompression`, … ; `HarmonicaViewModel.ApplyInput(key, text)` |
| the readout strip | `ReadoutStripView.axaml(.cs)` (`src/Ui/Views/Harmonica/`) — `SetItems`, `SetInputs`, `FontSizeFor` |
| the readout rows | `HarmonicaSolver.BuildReadouts` / `AddMxColumn` — `HarmonicaReadout(Label, Value, Tooltip, Column, …)` |
| readout formatting | `HarmonicaReadoutFormatting` (`src/Ui/Harmonica/`) — `FormatZ`, `FormatGamma`, `FormatComplex`, `TryParse` |
| the efficiency-axis overlay | `HarmonicaPanelRenderer.DrawEfficiencyAxisOverlay` (R-h9b-9) |
| the axis-label rects | `AxesRenderer.ComputeLabelHitRects(plot, size)` — a standalone, non-drawing accessor already used by the X-label context menu |

---

## 1. The macOS menu bar

### 1.1 — R-h9r2-11: the docked document shows no native menu

> **owner:** *"On macOS, the harmonicaRF native menu does not appear when the harmonicaRF document is
> docked. But it does correctly appear when undocked (torn off from the dock)."*

The machinery for this exists and is R-h9a-3's: `HarmonicaMenuView.RecomputeAttachment` attaches the
`NativeMenu` to the hosting window when `!isWorkspaceWindow` (torn off / standalone) **or** when
`_dockedHasFocus` is true; `_dockedHasFocus` is set by `SetDockedFocus`, wired in
`HarmonicaView.axaml.cs` (`_doc.ViewModel.NativeMenuDockedFocusChanged = Menus.SetDockedFocus`) and
driven by `WorkspaceViewModel.UpdateHarmonicaDockedMenuFocus`. **The torn-off case works, which proves
`RecomputeAttachment`'s attach itself is correct** — so the fault is upstream, in whether
`_dockedHasFocus` ever becomes true.

**Work down this list and report which it actually was** — the completion note must say what was found,
not what was assumed:

1. **Is `UpdateHarmonicaDockedMenuFocus` called at all, with the right argument?** It early-returns on
   `ReferenceEquals(_harmonicaDockedFocusDoc, nowActive)`; if the field is initialised to the document
   (or the call site passes null for a harmonicaRF document because the active-dockable branch orders
   `HarmonicaDocument` after some other type's arm), it will never fire. Check every caller.
2. **Is the wiring installed before the first activation?** `HarmonicaView.axaml.cs` sets
   `NativeMenuDockedFocusChanged` when the view binds. A document that is the active dockable at the
   moment its view is first built gets the activation notification BEFORE the delegate exists, and
   nothing re-fires it. (This is exactly the class of bug `TryWireWindowFocusTracking`'s own
   already-active check exists to close — see this file's own "a freshly torn-off window's macOS menu
   said Close Workspace" note.) The fix, if this is it, is the same shape: on wiring, ask whether this
   document is currently the active dockable and apply immediately rather than waiting for a future
   change.
3. **Is the target right?** Docked-with-focus attaches to the same `WorkspaceWindow` circuitRF's own
   menu is on, deliberately overwriting it. If some later code path re-attaches circuitRF's menu after
   ours (a theme change, a tool-panel activation, `RestoreCircuitRfMenuBar` firing on a transition it
   should not), ours is silently replaced.

**Gate it on the attachment target, not on a screenshot.** `HarmonicaMenuNativeAttachTests` already
exists and already source-scans this file for R-h9a-1's detach-before-attach discipline; add a test that
drives `SetDockedFocus(true)`/`(false)` and asserts which object `RecomputeAttachment` decided on. If the
real fault is in `WorkspaceViewModel` (which cannot be constructed headlessly), pin the call site by
source scan — the pattern this repo already uses for exactly this situation.

### 1.2 — R-h9r2-12: switching the system theme to dark crashes the app

> **owner:** *"App crashed when I change the System color theme to dark mode."*
> ```
> System.ArgumentException: The menu being updated does not match. (Parameter 'menu')
>    at Avalonia.Native.Interop.Impl.__MicroComIAvnMenuProxy.Update(IAvaloniaNativeFactory, NativeMenu)
>    at Avalonia.Native.AvaloniaNativeMenuExporter.DoLayoutReset(Boolean forceUpdate)
>    at Avalonia.Native.AvaloniaNativeMenuExporter.SetNativeMenu(NativeMenu menu)
>    at Avalonia.Controls.NativeMenu…SetMenu(AvaloniaObject o, NativeMenu menu)
>    at CircuitRF.Ui.Views.Harmonica.HarmonicaMenuView.RecomputeAttachment()
>    at CircuitRF.Ui.Views.Harmonica.HarmonicaMenuView.<.ctor>b__4_1(…VisualTreeAttachmentEventArgs)
> ```

**This is the same exception, from the same interop layer, as the torn-off-window-close crash this file
already records** — that one was fixed by wrapping the detach in `try`/`catch` because the window was
being destroyed regardless. The trace here names the ATTACH path instead: a system theme change tears
down and rebuilds the visual tree, `AttachedToVisualTree` fires, `RecomputeAttachment` calls
`NativeMenu.SetMenu(window, _ownMenu)`, and the platform exporter for that window refuses the update
because the menu it currently holds is not the one being updated.

**Three things to do, in this order:**

1. **Make the attach unable to take the application down.** Wrap the `SetMenu` calls in
   `RecomputeAttachment` in `try`/`catch (Exception)` with the same reasoning the detach already carries
   in its own comment: a failed menu-bar attach costs a menu bar, an unhandled exception costs the whole
   application, and the two are not close. **Leave `_attachedTo` consistent on the failure path** —
   pick whether a failed attach leaves the field at its old value or clears it, and say which and why.
2. **Detach defensively, not only when we think we are attached.** The current guard is
   `if (_attachedTo is { } current) NativeMenu.SetMenu(current, null);`. After a visual-tree rebuild our
   bookkeeping can disagree with what the exporter believes, which is precisely the mismatch. Clearing
   the DESIRED target's menu before setting ours costs nothing and removes one way for the two to
   disagree.
3. **Say whether a re-attach is even needed.** `AttachedToVisualTree` fires on a theme change; if the
   hosting window and the desired target are the same object as before, `RecomputeAttachment`'s
   `ReferenceEquals(_attachedTo, desiredTarget)` early-return should already skip the whole thing. If
   the crash happens anyway, the field went stale — which is finding (2) — and the fix is to detect that
   rather than to re-attach harder. **Investigate before adding code.**

**A `try`/`catch` alone is not a fix, it is a floor.** Report what actually made the exporter's state
diverge; if the honest answer is "an Avalonia.Native behaviour we cannot see into", say that plainly.

**Not headlessly reproducible** (no Avalonia platform in this suite, matching the earlier fix's own
note) — pin the guard by source scan and put the theme-switch gesture on the interactive list.

---

## 2. The Smith chart titles sit too high, and the two rows should match

> **owner:** *"Both Smith Charts' title text is rendered too high above the Smith Chart plot. Move it
> down so it looks better to the user. Also, make the row 1 text size of the Smith Charts be the same as
> row 2."*

`HarmonicaPanelRenderer` owns both halves: `TitleBandHeight(size)` reserves the band and
`DrawTitleRows` places the two baselines inside it (`y1 = row1Size * 1.05`,
`y2 = row1Size * 1.3 + row2Size * 1.05`), with sizes from `TitleRow1FontFraction` (0.052) and
`TitleRow2FontFraction` (0.82 × that), both scaled by R-h9b-5's `TitleFontShrink = 0.8`.

### R-h9r2-13 — the band and the baselines move together, or the chart moves

**`TitleBandHeight` is not decoration — `GammaToCanvas` / `CanvasToGamma` / `MarkerToCanvas` /
`CanvasToMarker` all subtract it, unconditionally, which is what makes render and hit-test agree
(R1B's own root-cause fix).** So:

- **Equalising the row sizes** is one constant (`TitleRow2FontFraction = TitleRow1FontFraction`) and it
  changes `TitleBandHeight`'s result — the chart below shifts and the hit test follows it for free,
  because both read the same function. Do not hardcode a band height to "keep the chart where it was".
- **Moving the text down within the band** is the two baseline expressions. Whether the band should also
  grow is your call: growing it pushes the chart down (less chart), leaving it pushes the text closer to
  the chart (less gap above the rim). **Say which you chose and what the resulting numbers are.**
- **R-h9b-5's 0.8× shrink stays.** The owner asked for the two rows to MATCH, not for either to grow.
- **Both charts read the same code path**, so they cannot disagree — that is already true and must stay
  true.

Gate it with the transform, not by eye: a pixel oracle that a marker at a known Γ renders where
`MarkerToCanvas` says it does, at more than one panel size, before and after the change.

---

## 3. The DCIV sweep defaults

> **owner:** *"Set the default DCIV sweep settings to VGS: -5 to 2.5 with 16 steps and VDS: 0 to 120 with
> 120 steps. This is a good setting for the SDD equation that is currently used."*

`DcivFamily.DefaultKey(model, vgsSteps = 9, vdsSteps = 200)` currently centres Vgs on the document's own
bias (`vgs ± 2.0`) and takes Vds from `0 … max(1, Vds × 1.4)`.

### R-h9r2-14 — a fixed default, and say what it costs

- Vgs −5 … 2.5 in **16** steps, Vds 0 … 120 in **120** steps. That is 1,920 curve points against the
  current 9 × 200 = 1,800 — the same order, so tier C's own "computed once and held" budget is
  unaffected. **Confirm that with `DcivComputeCount`, don't assume it.**
- **This makes `DefaultKey` independent of the document's bias**, which is a real change: a document
  biased far from this window will now draw a family that does not bracket its own operating point. That
  is what the owner asked for (the numbers are chosen for the shipped SDD), so do it — but **state the
  trade in the completion note**, since the bias-centred behaviour was deliberate.
- **The R-h9b-12 override is untouched.** `OverrideOf` still wins where set; `IsValidOverride`'s rules
  (min < max, steps ≥ 2, all finite) are unchanged; the DCIV Sweeps dialog still edits the same six
  numbers. Only the fallback moves.
- The `vgsSteps`/`vdsSteps` parameters exist for callers that want something else — keep them, with the
  new values as the defaults, rather than deleting the seam.

---

## 4. The inline text editor never engages

> **owner:** *"Inline text editor does not activate/engage. Perhaps because we're using selectable Text UI
> elements. Change all UI text that uses the inline text editor to be non-selectable. (It doesn't need to
> be selected anyway because the user can simply double click on text to activate the inline text editor
> and can then select text from there.) Make sure units is included in the inline text editor text field
> (but not initially selected — just like how the schematic editor does it)."*

### 4.1 — the owner's diagnosis is correct, and the mechanism is worth writing down

`ReadoutStripView.BuildColumnRow` puts the value in a **`SelectableTextBlock`** (R1C: "§7.5 — all text is
selectable") and hangs `DoubleTapped` on the **parent `StackPanel`**. `SelectableTextBlock` handles
pointer input for text selection — including the double-tap, which it consumes as select-a-word — so the
event never reaches the parent and `BeginInlineEdit` is never called. Selectability and double-click-to-
edit are competing for the same gesture, and selectability wins.

### R-h9r2-15 — editable rows are NOT selectable; the rest still are

- **Only the rows that carry the inline editor change.** `BuildColumnRow`'s `item.Editable` rows become a
  plain `TextBlock`. Every other readout — General rows, MXP/MXE's read-only figures, headers — keeps
  `SelectableTextBlock`, because §7.5's "a readout you cannot copy is one you retype by hand into a
  report" is still true for them and they have no competing gesture.
- The owner's own justification for the trade is the right one to record: an editable row's text is
  reachable for selection **inside** the editor, one double-click away.
- **Check the other editors in this strip too.** `SetInputs`' own rows and any other double-tap surface
  added since R1C are the same hazard; sweep them and say what you found.

### R-h9r2-16 — the editor shows the unit, and pre-selects only the value

`BeginInlineEdit` currently seeds the box with `item.Value` and calls `box.SelectAll()`.

- Seed the box with the value **and its unit** (`"80 + j10 Ω"`, not `"80 + j10"`), so what the user sees
  in the box is what the row showed.
- Pre-select the **value only**, not the unit — the schematic editor's own rule
  (`InlineEditSelLength = param.Expression.Length` when a unit is present, see this file's
  "Inline editor fixes" note). Typing a fresh number replaces the number and keeps the unit.
- **The parse must accept the unit back.** `HarmonicaReadoutFormatting.TryParse` is the one parser
  (`OnReadoutCommitEdit` calls it); it has to tolerate a trailing unit token rather than rejecting the
  text the editor itself put there. **If it already does, say so; if not, widen it there — never add a
  second strip-the-unit step at the call site.**
- Return/LostFocus commit and Escape revert are unchanged, `e.Handled = true` on Return included.

---

## 5. The power sweep is an EXPLICIT uniform sweep the user specifies exactly

> **owner:** *"The power sweep Pin Start, Stop and Step needs to be user controlled (and persisted in the
> .charm file). Set the default to -10 dBm to 50 dBm in 1 dB steps. Add a menu that allows the user to
> adjust it. Robust text entry validation."*

> **owner, asked directly:** *"I want an explicit Pin sweep. I don't want you to pick one. I want the user
> to exactly specify it. Default is −10 to 50 in 1 dB steps."*

**The requirement is not negotiable and there is nothing to choose between.** The power-sweep panel plots
the points the user asked for: `Start, Start+Step, Start+2·Step, …` up to and including `Stop`. Every one
of those points is a real HB solve at that available power. No resampling, no interpolation onto a ladder
the solver did not actually visit, no reinterpretation of Step as a bound or a stride cap.

### 5.1 — what exists today, and why the sweep is a NEW entry point rather than a change to the old one

`PinSearch.Run` is **not** a uniform sweep: it is a tickle, a doubling bracket, then a secant onto the
compression target. That shape is load-bearing and must survive untouched, for one reason:
**`ContourGrid.Build` calls `PinSearch.Run` once per Γ point.** H0–H3 measured 4.6 solves per point,
280 for a 61-point grid — the entire affordability of the contour grid rests on that search NOT being a
uniform ladder. A 61-point grid × a 61-point uniform sweep would be 3,721 solves, ~13× worse.

So:

- **`PinSearch.Run` stays exactly as it is**, and stays what the contour grid uses per Γ point.
- **A new sibling, `PinSearch.Sweep(ctx, terminations, start, stop, step)`, is what tier A uses** — the
  single drive-up at the markers' own terminations that feeds the power-sweep panel, the loadline and the
  operating-point readouts.
- **Both return `PinSearchResult`.** Same record, same `Steps`/`AtCompression`/`Reason`/`Solves`, same
  FOM extraction (`PinStepResult`'s own conventions — Pout, Gt, Gp, DE, PAE written exactly as it writes
  them). One result type, one FOM path, no duplicated physics. `HarmonicaSolver.BuildPowerSweep` and
  `BuildReadouts` then need no knowledge of which produced the result.
- **`Sweep` warm-starts each point from the previous point's converged spectrum**, exactly as the bracket
  already does. Without it the sweep is a cold solve per point and costs several times what it should.
- `BuildPowerSweep` already sorts by `PavlDbm` because the bracket's output is not monotone. A uniform
  sweep is already ordered; leave the sort alone — it is a no-op on ordered input and it is what keeps
  the panel correct for both producers.

### R-h9r2-17 — the ladder is exactly what the user typed

- **Inclusive at both ends.** `Stop` is always solved, even when `Stop − Start` is not an integer
  multiple of `Step` (the final interval is then short). The user named that number; the plot must reach
  it.
- Point count is `floor((Stop − Start)/Step) + 1`, plus the final `Stop` point when it is not already on
  the ladder. At the defaults that is **61 points**.
- **The ladder must run PAST the compression target**, because the compression point is interpolated
  from the pair that straddles it (R-h9r2-17a) and a bracket needs a point on each side. That is why
  Stop defaults to 50 dBm. Choosing a Stop below where the device compresses is the user's call and the
  panel already says what happened.
- **A sweep that never compresses is a normal outcome, not an error.** `ReachedCompression` goes false
  and the panel already draws its own "did not compress" note (`DrawDidNotCompressNote`). Unchanged.

### R-h9r2-17a — compression is INTERPOLATED from the ladder; the extra solve is an option, default OFF

> **owner:** *"We want the Pin sweep to go past the compression setting, then the compression Pin is
> interpolated from the sweep, and from there the Pout and Efficiency (or PAE) is interpolated at
> compression as well. For 1 dB Pin steps this technique for calculating the compression point has been
> proven to be very effective. Keep the 'exact' compression as an option in the preferences/settings for
> the user to turn on if they want it, but its default setting is off."*

**Default path: no extra HB solve at all.** Walk the sweep, find the first adjacent pair that straddles
the compression target, and interpolate.

- **First crossing, lowest Pin.** A real gain curve can cross the target more than once (that is exactly
  why `CompressionAt` tracks a running `gMax` rather than a fixed reference — its own comment says so);
  "the compression point" means the first one.
- **Linear, on the compression value:** `f = (target − cLo) / (cHi − cLo)`, then
  `PinAtComp = PinLo + f·(PinHi − PinLo)`. Every FOM is interpolated at that same `f`.
- **Name the domain per quantity and keep it**, or two implementations will diverge in the fourth digit:
  Pin and Gain in **dB/dBm**, Pout in **dBm** (the curve the panel actually plots), DE and PAE as
  **ratios**, Pdc in **watts**. At 1 dB steps the choice is small — that is the owner's own point — but
  it must be written down, not left to whoever touches it next.
- **Use the sweep's own `gMax`**, the running maximum the ladder established. Do not recompute a
  reference from the bracketing pair alone.
- The interpolated point's `Compression` is the target exactly, by construction.

**The structural constraint, found by reading, that decides the shape of this:** `PinStep` **cannot
carry an interpolated result.** It is `record PinStep(double PavlDbm, double Compression,
OperatingPoint Point)` whose `PoutW`, `De` and `Pae` are *derived* from `Foms`/`PdcW` — all of which come
from a real HB solve — and it carries the operating point's own spectrum. So the interpolated compression
point needs its own small record (Pin, Pout, Gain, DE, PAE, Pdc, plus the nearest solved `PinStep` and a
flag saying it was interpolated). **Do not fabricate a `PinStep`** by inventing an `OperatingPoint` for
it; that spectrum would be a lie that the loadline and the intrinsic glyphs would then draw.

**And the readouts must actually READ it.** `HarmonicaSolver` today resolves the operating point as
`cursor = IndexOfNearestPin(sweep.Steps, sweep.AtCompression.PavlDbm)` and then uses `sweep.Steps[cursor]`.
With the secant that is exact, because `AtCompression` *is* one of `Steps`. **With a 1 dB ladder,
nearest-step rounds the readouts to the nearest whole dB — which is precisely the error interpolation
exists to remove.** Pin/Pout/Gain/DE/PAE/Pdc at compression must come from the interpolated record, not
from `Steps[cursor]`.

**What still comes from a solved point, and why the split is unavoidable.** `HarmonicaDataSet.Build(ctx,
at.Point, terminations)` feeds the intrinsic Γ glyphs, Zin, AM/PM and the published cubes, and
`BuildLoadline(ctx, at, …)` feeds the loadline — all from the operating point's **spectrum**, which
cannot be interpolated meaningfully. With the option OFF those come from the **nearest solved ladder
point** (≤ ½ step away, ≤ 0.5 dB at the default). **State this in the completion note**: the scalar
readouts sit exactly at compression while the loadline and glyphs sit at the nearest solved dB, and at
1 dB steps that is the intended trade.

#### The loadline's Fourier coefficients — asked directly, answered from the code

> **owner:** *"What VDS/IDS Fourier coefficients will be used to calculate the time-domain loadline? Are
> they interpolated from the Pin found at compression? Or is it simply the nearest Fourier components to
> the compression?"*

**It is the NEAREST solved point's coefficients, not interpolated — today, and under this brief's
default.** Three facts from the code, worth writing down because the answer is less symmetric than the
question assumes:

- `HarmonicaSolver.BuildLoadline` passes **`at.Point.V`** — the converged HB **node-voltage** spectrum of
  the step at `cursor`, and `cursor` is `IndexOfNearestPin(sweep.Steps, …)`. So the loadline has always
  been drawn from a *solved* step. With today's secant that step IS the compression point, so the
  distinction never mattered; **with a 1 dB ladder it becomes the nearest whole-dB point**, which is why
  the owner is right to ask.
- **There is no Ids spectrum.** `IntrinsicPlane.Loadline` transforms only the voltage spectrum to the
  time grid (`ResampleSpectrum` per node) and then re-evaluates the device pointwise:
  `ids[t] = dut.Evaluate(new PortVoltages(pv)).I[drainPort]`. So the only Fourier coefficients involved
  are **V**, and Ids is the device's own nonlinear response to them. That is also why
  `LoadlineSamples` can be any count exactly (R-h9b-13) rather than being tied to the FFT grid.
- **Do not interpolate the spectra.** It is arithmetically possible — they are complex arrays, and
  `dut.Evaluate` would happily produce a curve from a blended V — but a linear blend of two converged HB
  solutions **is not itself a solution**: it satisfies the harmonic-balance residual at neither Pin
  level, so the resulting locus would not satisfy KCL at the device terminals. It would draw perfectly
  and describe a state the circuit never occupies, which is the exact failure class this codebase keeps
  catching. **`ExactCompressionSolve` is the supported way to get a loadline at compression** — one real
  solve at the interpolated Pin, yielding a genuine converged spectrum, with loadline, glyphs, Zin and
  AM/PM all then describing that one state.

**What the option buys, and its name.** `ExactCompressionSolve`, **default false**. When ON, one extra
HB solve runs at the interpolated Pin and *everything* — scalars and spectrum — comes from that one
solved state, which is R-h9b-16's own principle ("the FOMs at that state come from a SOLVE… every number
is then the same state, consistently") applied here. Cost: exactly **one** extra solve per drive-up.

- **The contour grid is NOT affected.** `GridPoint.Metric` reads `AtCompression`'s FOMs, produced by
  `PinSearch.Run`'s bracket-and-secant, and `SolveAtOptimum` (MXP/MXE) does the same. Both stay as they
  are — a secant is already cheap and already exact there, and a 61-point ladder per Γ point is what
  §5.1's guardrail forbids. The panel and the grid therefore find compression by different methods;
  they agree to within the interpolation error, which at 1 dB steps is small. **Say so rather than
  letting a reader discover it.**
- **This is a different toggle from `ReconvergeAtCompression`** (`HarmonicaViewModel`, R-h6-11, default
  off), which re-converges an *inverse drag* at compression. Two different things; do not merge them.
- **Lives in both places, same as the tickle** (R-h9r2-18a): the preference seeds a new `.charm`, the
  `.charm` carries what this document used, and it appears in the same **Display ▸ Power Sweep…** dialog
  and in **Edit ▸ Preferences…**.

### R-h9r2-18 — three settings, persisted, validated, and ONE power range for the document

- `HarmonicaSettings` carries **Start**, **Stop** and **Step**. Defaults **−10 dBm, 50 dBm, 1 dB**.
- **`PinStartDbm` IS Start** — it already seeds both tier A and the grid's per-point bracket, and one
  "where power sweeps begin" number for the document is right.
- **`PinMaxDbm` becomes Stop.** Today it is 30 dBm and is R-hrf-7's hard stop for the bracket. Keeping it
  as a separate ceiling would mean a document whose panel sweeps to 50 dBm while every grid point quietly
  gives up at 30 and reports a hole — two ceilings the user never asked for and cannot see. One number,
  used as the sweep's end AND as the bracket's hard stop.
  **State the consequence in the completion note:** raising the default ceiling from 30 to 50 dBm means
  grid points that previously reported `PinMax` (a hole) at 30 dBm now keep searching to 50, so some
  documents will show fewer holes and cost more solves. That is the correct behaviour for the new
  default, but it is a change and it must be visible.
- **Step is the uniform sweep's only.** The grid's bracket keeps its doubling strides — that is what
  makes it 4.6 solves per point, and nothing about Step should touch it.
- Persisted through `CharmIo`'s settings block — additive, absent ⇒ default, **no `FormatVersion` bump**,
  exactly like Z₀ and `LoadlineSamples` before them.
- **Is it structural?** Apply R-h9b-6's own test: probe `CircuitModel.StructuralKey` and say which way it
  went. A power range changes no circuit, so it should be a VALUE change — but check rather than assume,
  and if it does move the key, say why.
- **Menu: Display ▸ Power Sweep…**, opening a small dialog with the three fields, mirroring
  `HarmonicaDcivSweepsDialog`'s own shape — Return/Escape contract, live validation, and **validated
  before anything is written**, so an invalid entry cannot even transiently reach `Model` (R-h9b-12's own
  rule, and what makes "invalid input keeps the old sweep on screen" true by construction rather than by
  reverting after the fact). Show the resulting point count live, next to the fields.
- **Validation:** all three finite; `Start < Stop`; `Step > 0`; and a point-count ceiling —
  **`MaxSweepPoints = 1001`**, refused by name with the computed count in the message, never silently
  clamped (a clamp would mean the plot is not the sweep the user typed, which is the one thing this
  section exists to guarantee).
- A §7.5 input row is not a substitute for the menu; the owner asked for the menu. Adding rows as well is
  fine — **say so if you do.**

### R-h9r2-18a — the tickle tone stays, as a preference, on by default, at −50 dBm

> **owner:** *"Keep the Tickle tone Pin level as an option for the user to turn on/off as a harmonicaRF
> preference. Default is on and its value is −50 dBm."*

**Know what the tickle actually is before changing it — it is not a warm-up solve.** `PinSearch.Run`
solves one point at `PinStartDbm − TickleBelowStartDb` (the constant is 30 dB, so **−40 dBm** at
today's default Start), and that point's gain becomes `gss` → `PinSearchResult.SmallSignalGainDb` **and
seeds `gMax`, the running maximum every point's compression is measured against** (`CompressionAt`).
It is also the first convergence check: a tickle that fails makes the whole Γ point a hole. So the
tickle defines what "3 dB compression" means for the document.

- Two settings: **`TickleEnabled`** (default **true**) and **`TickleDbm`** (default **−50 dBm**).
- **`TickleDbm` is an ABSOLUTE available-power level**, replacing the relative
  `TickleBelowStartDb` offset. Two consequences to state: at the default Start the effective tickle
  moves from −40 to −50 dBm, so compression references shift slightly against today's behaviour; and
  the tickle no longer follows Start when Start moves — which is exactly what naming an absolute level
  means, and is the reason to name one.
- **Validate `TickleDbm < Start`.** A tickle at or above the sweep's first point is not a small-signal
  reference and the number it produces is meaningless.
- **What OFF means, stated rather than left to be discovered:** with no tickle, `gMax` seeds from the
  first solved sweep point and `SmallSignalGainDb` is **null** — never a fabricated value. Compression
  is then referenced to the gain at Start, so a device already slightly compressed there reads
  compression LATE. `gMax`'s running-maximum rule (already in the code, with its own comment saying
  why) recovers most of that as the sweep climbs, but not all. Say so in the completion note.
- **It applies to BOTH drive-ups.** `PinSearch.Run` (per Γ point) and `PinSearch.Sweep` must read the
  same setting, or the panel's compression cursor and the contour grid's compression criterion measure
  against two different references and MXP/MXE stop agreeing with the plot.
- **Cost, which is presumably why it is a toggle:** one HB solve per drive-up — 61 of a 61-point grid's
  ~280 (~22%), and 1 of the uniform sweep's ~62. **Report the measured saving with it off.**
- **Where it lives: BOTH, and the split has direct precedent in this codebase.** The owner asked for a
  preference; a colleague's `.charm` must still reproduce its own numbers. Follow the wBond defaults
  precedent recorded in `src/Ui/CLAUDE.md` (*"§6.4's defaults are preferences, not design state … per
  USER because they describe how one shop's bonder is set up — a `.wBond` arriving from someone else
  must not change what the next wire you draw looks like"*): **`AppPreferences` carries the tickle
  default that SEEDS a new `.charm`; `HarmonicaSettings` carries what THIS document was solved with**,
  persisted by `CharmIo` like every other solver knob. Reachable per-user from **Edit ▸ Preferences…**
  and per-document in the same **Display ▸ Power Sweep…** dialog, since it is part of how the sweep is
  defined.

### R-h9r2-19 — EVERY point, EVERY frame, drag included. No decimation, ever.

> **owner:** *"I want the power sweep plot to show all the points in the Pin sweep. This allows the user
> to see the Gain shape of the DUT (aka the AM/PM nonlinear response). I do not want some points in the
> sweep to be skipped during a drag. I want to see the Gain (AM/PM) profile shape in the Power sweep plot
> in real-time during a drag."*

**This is a hard requirement, and the reason is what the plot is FOR.** The user is not watching a
compression number during a drag — they are reading the *shape* of the gain curve as the termination
moves. A decimated ladder destroys exactly the feature being read: the gentle roll-off, the expansion
knee, the AM/PM signature. **No every-Nth, no LOD, no adaptive sampling, no "coarse during drag".** The
plotted ladder is the user's ladder, on every frame, in every state.

**The cost is real and this is what you have to work with.** H0–H3 measured the real 61-point grid at
0.804 s for ~280 solves — **≈ 2.9 ms per HB solve** on that fixture — so a naive 61-point sweep is
≈ 180 ms per frame against §6.8's 33 ms target. But that 2.9 ms is a grid point's *bracket* cost, where
each Γ point starts a fresh search; it is an upper bound here, not a prediction. **Measure before
concluding anything.**

**Two levers, in this order, both of which keep every point:**

1. **Warm-start the sweep FRAME TO FRAME, not just point to point.** Within a frame the ladder already
   seeds each point from the previous one. Across frames, a marker drag perturbs the termination
   *slightly* — so the previous frame's converged spectrum **at the same Pin level** is a far better seed
   than its neighbour one step down. Keep the previous frame's per-level spectra and seed from them.
   This is the same technique the codebase already relies on twice (H6's Broyden warm start, and
   `ContourGrid`'s Γ-nearest converged-neighbour seed), and it is the single biggest lever here: a
   well-seeded HB point converges in a fraction of the Newton iterations a cold one needs.
2. **Then parallelise the ladder, which lever 1 is what unlocks.** Point-to-point seeding forces a serial
   chain; once every point has a good seed from the previous frame, the ladder is embarrassingly parallel
   and can go to the existing `SolvePool`. **Respect this project's own rule:** a `HarmonicaContext` is
   not thread-safe and belongs to a worker (`src/Harmonica/CLAUDE.md`'s "No static mutable state — H5
   gives each worker its own context"), so this means N contexts, exactly as the grid already does.

**If it is still over budget after both, the frame rate drops and the curve stays complete.** That is the
owner's explicit trade and it is not yours to re-decide. The scheduler keeps measuring, and it may still
degrade what 2A already lets it degrade (the contours are frozen during a drag anyway) — but **it must
never drop a power-sweep point**, and no new status message is added for this (2A removed the
contour-quality messages precisely because they were noise).

**Report the measured numbers**: solves and wall-clock per tier-A frame at the default 61-point range,
before and after lever 1, and again after lever 2 — plus the achieved drag frame rate.
`HarmonicaViewModel.LastSolveCount` already counts everything. If lever 1 alone gets there, say so and do
not build lever 2.

---

## 6. Set Z₀ from a menu

> **owner:** *"Currently no way for the user to set Z0 of the Smith Charts. Add a Set Z0 to menu."*

**Check first: Z₀ IS already user-settable.** R-h9b-6 added it as a §7.5 input row —
`HarmonicaInputs.KeyZ0 = "settings.z0"`, label `Z0`, unit `Ω`, committed through
`HarmonicaViewModel.ApplyInput`. The owner did not find it, which is a discoverability report, not an
absence.

### R-h9r2-20 — a second surface onto the SAME write

- Add **Display ▸ Set Z0…** opening a one-field dialog. It must call `ApplyInput(HarmonicaInputs.KeyZ0,
  text)` — the same path, the same parse, the same non-structural classification, the same re-solve.
  **Never a second write to `Model.Settings.Z0`**, or the strip row and the menu will drift on
  validation, on undo, and on what counts as structural.
- **The strip row stays.** Two surfaces onto one value is the point (the menu is discoverable, the row is
  fast).
- Show the current value in the dialog, pre-selected, and state R-h9b-6's own rule in it: changing Z₀
  moves no termination — every impedance is unchanged and every marker moves on the chart. That is the
  one thing about this setting a user is likely to get wrong.
- **Re-check R-h9b-6's invariant after wiring the menu**: set a marker to 80 + j10 Ω, change Z₀ from the
  MENU, assert `Terminations.Z(...)` is bit-identical and `marker.Gamma` changed.

---

## 7. The readout strip: size, colour, and one row too many

### R-h9r2-21 — the display text is too small, and the size is a named constant

> **owner:** *"Display text (bottom left) is rendered too small. Increase text font size by +25%. We may
> have to tweak this for visual appeal, so make this text size a variable in the code."*

`ReadoutStripView.FontSizeFor(w, h) = clamp(min(w,h) × FontSizeFraction, MinFontSize, MaxFontSize)`
with `FontSizeFraction = 0.03`, `MinFontSize = 8`, `MaxFontSize = 16`.

- **+25% means the fraction AND the clamps**, or the increase evaporates the moment the strip is small
  enough (or large enough) to be clamped — which at the default §7.1 layout it very nearly is at the top
  end. `0.03 → 0.0375`, `8 → 10`, `16 → 20`. **Say what you moved.**
- "Make this text size a variable" — the three constants already are. Keep them `public const` on
  `ReadoutStripView` (they are, and `FontSizeFor` is deliberately a pure function so it is testable
  without a live control), and make sure nothing else hardcodes a readout font size. **Sweep for stray
  `fontSize = 10` defaults** — `SetItems`/`SetInputs` both carry one as a parameter default.

### R-h9r2-22 — and it must actually track the window

> **owner:** *"Display text (bottom left) rendered size does not change with harmonicaRF window size."*

The plumbing for this exists and looks right: `HarmonicaView.ReadoutFontSize()` computes from the
strip's own PLACED pixel size (`Layout.PlacementOf(ReadoutStrip)` × `PanelHost.Bounds`), `Refresh()`
passes it to `SetItems`/`SetInputs`, and `PanelHost.SizeChanged` calls `Refresh()`. **So this is a
diagnosis job, not a design job.** Candidates, cheapest first:

1. **The clamp is binding.** At the default layout and an ordinary window, `min(W,H)` for the strip is
   dominated by its HEIGHT, which is a small fraction of the panel host — so the computed size may be
   pinned at `MinFontSize` across the whole realistic window-size range, and resizing genuinely changes
   nothing. **Check this first; if it is the whole story, R-h9r2-21's clamp change may fix it outright**
   and the honest completion note says so.
2. **`SetInputs` skips the rebuild.** It compares an `_inputSignature` and, when the shape is unchanged,
   updates values in place via `UpdateInPlace` — **which may not re-apply the font size.** That would
   leave the input row frozen at its first size while the item rows scale. Check `UpdateInPlace`.
3. **`SizeChanged` is not firing for the case that matters.** `PanelHost.SizeChanged` covers a window
   resize; an Edit Display drag of the strip's own panel changes the PLACEMENT without changing
   `PanelHost.Bounds`. Confirm both paths call `Refresh()`.

**Report which it was.** Gate it on `FontSizeFor` directly (a pure function, no control needed) plus
whichever call-site fix the diagnosis turns up.

### R-h9r2-23 — "Efficiency (%)" in the efficiency colour

> **owner:** *"The 'Efficiency (%)' text label needs to be rendered in Harmonica.EfficiencyTrace color."*

R-h9b-9 already redraws the right axis's **line, ticks and tick numbers** in `theme.EfficiencyTrace`
(`DrawEfficiencyAxisOverlay`), over what `PlotRenderer` drew in the shared theme colour. It does **not**
redraw the axis LABEL, which `AxesRenderer.DrawTitleAndAxisLabels` draws in the ordinary text colour.

- Extend the overlay to the secondary Y label, using `AxesRenderer.ComputeLabelHitRects(plot, size)` —
  the standalone non-drawing accessor R-h9b-10 already uses for the X label — so the redraw lands
  **exactly** on top of the original rather than beside it. A label drawn twice at slightly different
  positions reads as a rendering bug, not a colour.
- **If `ComputeLabelHitRects` does not expose the secondary label's rect, add it there.** That is
  widening a non-drawing geometry accessor, which is a different (and permitted) thing from adding a
  per-axis COLOUR capability to `AxesRenderer` — which stays forbidden, per `AnnulusHeadroom`'s own
  precedent and R-h9b-9's own reasoning.
- The label follows `EfficiencyMetric`, so it reads "Efficiency (%)" or "PAE (%)"; both get the colour.
- **Re-run the reserved-red test** — `Harmonica.EfficiencyTrace` is reserved red in both variants and
  there is an existing test asserting red belongs to it and the loadline. This extends that reservation
  consistently; it is not a violation, but the test must still pass.

### R-h9r2-24 — the intrinsic Γ row goes, and the concern goes in the note

> **owner:** *"Remove: there is redundant display text S1 S2 L1 L2 L3 displayed on the same row as Pin
> Pout Gain DE PAE. It is redundant because there is a Load column of data showing the same thing
> below."*

The rows on that line are `BuildReadouts`' loop emitting `$"{m.Name} Γᵢ"` — the marker's **INTRINSIC**
Γ (§4.5), one per marker, in the General column.

**They are not the same quantity as the Source/Load columns.** Those show the **extrinsic** termination
as a Z/Γ pair; the General rows show the intrinsic reflection at the device plane, which is exactly the
number §4.5's whole intrinsic-plane machinery exists to produce, and which can legitimately have
|Γ| > 1. Removing them removes the only readout of it.

**Raise that in one sentence and then do as asked** — the owner has looked at the screen and judged the
line too crowded, which is a call about their own tool. Two ways to comply:

- **Remove them outright**, per the literal ask. Simplest; the intrinsic Γ is then visible only as the
  glyph on the chart.
- **Move them into the Source/Load columns** as a third row per marker (`Zx` / `Γx` / `Γᵢx`), which
  clears the crowded General line — the owner's actual complaint — without losing the quantity.

**Pick one, state which and why.** If you remove them, keep the `intrinsic: not located` row (R-h8-3):
that one reports a FAILURE to locate the plane and is not redundant with anything.

---

## 8. The format flyout does not repaint the value

> **owner:** *"The MenuFlyout used to set the format of the termination readout does not change the
> rendering of the text to the new format that is selected by the user."*

### 8.1 — root-caused: the format is baked in at SOLVE time

`OnReadoutFormatChanged` does the right thing — it writes `CharmAppearance.ReadoutFormats` and calls
`Refresh()`. But `Refresh()` re-renders `h.Frame.Readouts`, and a `HarmonicaReadout.Value` is a **string
built by `HarmonicaSolver.BuildReadouts` at solve time**, using the format that was current *then*
(`format($"{sideLetter}{m.Band}.Z")` etc.). Re-rendering a cached frame therefore re-renders the old
string. The new format only takes effect on the next solve — which, for a document sitting idle, is
never.

### R-h9r2-25 — format at RENDER time, not at solve time

- Carry the **raw value** on `HarmonicaReadout` (a nullable `Complex`, alongside the pre-formatted
  `Value` for rows that have no raw form — headers, `no optimum`, scalar figures) and let
  `ReadoutStripView` format it through `HarmonicaReadoutFormatting` using the CURRENT format at the
  moment it builds the row.
- **`HarmonicaReadoutFormatting` stays the one formatter.** The solver keeps choosing which formatter
  (Z vs Γ) a row uses; only the real/imaginary-vs-magnitude/angle choice moves to render time. Do not
  split the formatting logic across two files.
- **The inline editor and the `Set…` dialog already read `ReadoutFormatFor` live** and will now agree
  with what is on screen at all times — check that they still do, since `OnReadoutCommitEdit` parses in
  "the row's OWN current format" and that is now unambiguous.
- **Forcing a re-solve on a format change is not the fix.** It is display-only state (R-h9c-7 says so
  explicitly), it would be slow, and it would still be wrong for a row whose frame is frozen.
- Gate it without a live control: build a frame, change the format, rebuild the row list, assert the
  string changed. `ReadoutStripView`'s own pure helpers are the seam.

---

## 9. Scope guardrails

- **No frame-loop work** — the contour freeze, the drag rules, marker defaults, z-order and the marker
  context menu are **2A**. If a change here needs the frame to re-solve, route it through the existing
  `ApplyInput`/`RequestScheduledFrame` path and change nothing about the ladder.
- **Never widen `PlotRenderer` / `AxesRenderer` for a harmonicaRF colour or font need.** Widening a
  non-drawing geometry accessor (§7's label rect) is the one permitted exception and is different in
  kind.
- **`HarmonicaTitles` stays the ONE title formatter** and `TitleBandHeight` stays the ONE band
  reservation, read by the renderer and by all four transform functions.
- **One write path per setting** — Z₀ through `ApplyInput`, terminations through
  `SetMarkerGamma`/`SetMarkerImpedance`, DCIV through `ApplyDcivOverride`. A menu is a second SURFACE,
  never a second WRITE.
- **The uniform Pin sweep is TIER A's alone.** `ContourGrid` keeps calling `PinSearch.Run`'s
  bracket-and-secant once per Γ point; a uniform ladder there is ~13× the solves and would make the
  contour grid unaffordable. `PinSearch.Run` and `PinSearch.Sweep` share one result type and one FOM
  extraction, and nothing else.
- **Every power-sweep point is a real solve at a level the user named, on every frame, drag included** —
  no resampling onto a ladder the solver did not visit, no stride cap standing in for Step, and **no
  decimation in any state** (R-h9r2-19: the gain curve's shape is what the plot is for). **The
  compression point BETWEEN two ladder points is interpolated by design** (R-h9r2-17a) — that is a
  different thing and is the default. **The loadline's spectra are never interpolated** — nearest solved
  point, or one real solve when `ExactCompressionSolve` is on.
- **No `.charm` `FormatVersion` bump** — the Pin range is additive-with-a-default like every setting
  before it.
- **`HarmonicaPanelId` strings and `ColorRole` names are file-format keys.** Renaming one silently drops
  that panel's placement or that colour for every existing `.charm`.
- `src/Core`, `src/Engine`, `RfCore` untouched.

---

## 10. Gates

1. **Build + `dotnet test` green** — `tests/Ui.Tests` and `tests/Harmonica.Tests` while working, full
   solution at the end.
2. **A DOCKED harmonicaRF document owns the macOS menu bar while it is the active tab**, and gives it
   back on blur — with the actual cause of the failure named in the completion note.
3. **A system light/dark theme switch does not crash**, and the attach path can no longer take the
   application down — with what made the exporter's state diverge reported, or stated as unknown.
4. **Both Smith titles use one font size for both rows** and sit lower relative to the chart, with the
   chosen band/baseline numbers stated — and a marker at a known Γ still renders exactly where
   `MarkerToCanvas` puts it, at more than one panel size.
5. **A new document's DCIV family is Vgs −5…2.5 × 16 and Vds 0…120 × 120**, computed once per distinct
   key (`DcivComputeCount`), with the bias-independence trade stated. The R-h9b-12 override still wins.
6. **Double-clicking an editable readout opens the inline editor** — every time — while non-editable
   readouts stay selectable. The editor shows the unit and pre-selects only the value; committing
   round-trips through `TryParse`.
7. **The power sweep solves exactly the ladder the user typed.** With the defaults, a released frame's
   `PinSearchResult.Steps` is 61 points at −10, −9, …, 49, 50 dBm — every one a real solve, both
   endpoints present — and a non-integer range (e.g. −10 → 50 in 7 dB) still includes `Stop` exactly.
   Start/Stop/Step are validated, persisted, and reachable from **Display ▸ Power Sweep…**; a range over
   `MaxSweepPoints` is refused by name with the computed count.
8. **Compression is interpolated from the ladder, with NO extra solve by default** — the first straddling
   pair, linear in the compression value, with Pin/Pout/Gain/DE/PAE at compression all interpolated and
   **read by the readouts** rather than rounded to the nearest whole-dB step. On a fixture whose
   compression falls between two ladder points, the reported Pin is strictly between them and is not
   equal to either. `ExactCompressionSolve` is off by default and, when on, costs exactly one extra
   solve and makes every quantity come from that one solved state. A range that never compresses draws
   the existing "did not compress" note.
9. **The contour grid still uses `PinSearch.Run`'s bracket-and-secant per Γ point** — counter-gated:
   a 61-point grid is still ~280 solves, not ~3,700. Raising the default ceiling to 50 dBm is reported
   with its effect on hole counts and solve counts.
10. **Every sweep point is solved and plotted on EVERY frame, drag included** — a dragging frame's
    `Steps.Count` equals a released frame's, and the gain curve's shape is continuously readable while
    a marker is dragged. No decimation path exists anywhere. The measured per-frame solves, wall-clock
    and achieved drag frame rate are reported, before and after frame-to-frame warm starting.
11. **The tickle is on by default at −50 dBm, toggleable, and read by BOTH drive-ups.** With it on, the
    grid's per-point solve count is unchanged (~4.6); with it off, both the sweep and every grid point
    drop exactly one solve each, and `SmallSignalGainDb` is null rather than fabricated. A tickle level
    at or above Start is refused. The preference seeds a new `.charm`; an existing `.charm` reproduces
    its own numbers regardless of the preference.
12. **Display ▸ Set Z0… writes through the same `ApplyInput` the strip row does**, and changing Z₀ from
    the menu leaves every termination impedance bit-identical while every marker's Γ moves.
13. **The readout strip is visibly larger** and its size tracks the window and an Edit Display resize —
    with the reason it did not before named.
14. **The right axis's "Efficiency (%)" / "PAE (%)" label is drawn in `Harmonica.EfficiencyTrace`**,
    exactly once, on top of where the shared renderer put it.
15. **The General row no longer carries five intrinsic-Γ entries**, with the chosen disposition (removed
    vs. moved into the columns) stated.
16. **Changing a row's format from its right-click menu re-renders that row immediately**, with no
    re-solve, and the inline editor parses in the newly-chosen format.

**Interactive verification is required** for the macOS menu bar (both the docked case and the theme
switch), the title placement, the inline editor and the new dialogs — no visual driver here, matching
every prior harmonicaRF phase. List the exact gestures in the completion note under "please confirm on
your end".
