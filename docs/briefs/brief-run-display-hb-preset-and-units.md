# Brief — a schematic's display after its run changes shape, an HB plots preset, and marker readouts with units

**Tag:** `R-rdh-<n>` · **Area:** `src/Ui/ViewModels/WorkspaceViewModel.cs` (the post-run block that calls
`AutoOpenOrCreateDataDisplayAsync`, `PointDisplayAtRunAsync`, `AddTransientToRunDisplayAsync`),
`src/Ui/DataDisplay/ViewModels/DataDisplayViewModel.cs` (`AddTransientAsync`, `AddPresetGroupAsync`, `PresetSourceRef`),
`src/Render/DataDisplay/TransientPreset.cs` (the model for the new preset), `src/Render/DataDisplay/TraceResolve.cs`
(`InvalidSpecText`), `src/Render/DataDisplay/Models/Trace.cs` (`BuildMarkerBoxLines`, `GetStemValString`,
`GetMarkerValString`, `CubeValueUnit`), `src/Core/Expressions/Evaluator.TimeDomain.cs` (the new HB time-domain function),
`src/Ui/RESOLVED.md`.

## The gap

A field test ran small rectifier benches (a diode and load driven by a `VTone` or `P1Tone` at 50 Hz, then a four-diode
bridge with a reservoir capacitor), first in transient and then in harmonic balance, with the plots in each schematic's
own Data Display. Three gaps remain after the fixes listed below.

1. **A display that no longer matches its schematic's run shows an empty plot and says nothing.** Each run reopens the
   schematic's own display (`results/<schematic>.cdd`, or the authored one beside the bench). That display was built for
   whatever the first run produced. When the run's analysis changes (the bench's `TRAN1` disabled and an `HB1` enabled, so
   the results file now holds only `HB1`), or a net its traces pin is renamed (`n1` to `Vin`), every trace resolves to
   `<invalid>`: the run succeeds and the plot is empty. Nothing points at the new results.
2. **Harmonic balance has no plots preset.** A transient run gets waveforms, the output's spectrum and the measures in one
   action (`TransientPreset`). An HB run gets one trace on its first cube, and its time waveform cannot be drawn at all:
   no expression function turns an HB spectrum into a waveform. The two analyses of the same bench look unrelated in the
   Data Display, although `fft` of a transient's last period is already shaped like an HB spectrum
   (`TransientMeasurementCliTests.Gate3` holds that).
3. **A marker readout carries no unit.** On the Transient preset's spectrum (`dB(fft(TRAN1.V[:, 1], 50))`, y axis
   labelled "Voltage (dBV)"), the DC stem's marker reads `V(Vout_rect)=19.67`. That is 19.67 dBV, 9.63 V, exactly the
   waveform's level, but beside a waveform plot in volts it reads as 19.67 V, twice the waveform. Every cube and expression
   marker on a stem or Rect plot prints a bare number.

**Already fixed, do not redo** (uncommitted when this brief was written; see `src/Ui/RESOLVED.md` once it lands):

- The Transient, Noise circles and Signal quality "Open … plots" actions selected their source before the display knew the
  results folder, so the `.cdd` saved an absolute path and a copied workspace kept reading the original's results file.
  They now wire the library first.
- Every preset added from `DataDisplayViewModel` (Transient, Signal quality, Noise circles, Statistics table) binds its
  traces to the display's selected source (`PresetSourceRef = DataSourceRef.Selected`), as Add Trace does, not to the file
  selected when it was added.
- After a run, `PointDisplayAtRunAsync` points the schematic's own display at the file the run just wrote when it was
  showing another `.npy` result, and posts a note saying so.
- A new display created by a transient run holds the Transient plots; on later runs the message offers **Add Transient
  plots**, which adds them to that display rather than a new untitled one.
- Test: `tests/Ui.Tests/Transient/TransientPresetFollowsSourceTests.cs`.

## Requirements

**R-rdh-1 — Say when the display shows nothing of this run, and offer the plots that would.** After a successful run,
once the schematic's display is open and pointed at the run (`PointDisplayAtRunAsync`), check its traces. When it holds
traces and **none** of them resolve (every trace reading the run has `InvalidSpecText` set, and no trace reads another
source that still resolves), post one Messages line naming the display and the run's analyses, with a one-click action
that adds the plots for that run to that display:

- the run holds a transient group: the Transient plots (`AddTransientAsync`);
- the run holds an HB group: the HB plots (R-rdh-2);
- otherwise: one plot with the default trace Add Trace would seed, as `AutoOpenOrCreateDataDisplayAsync` does for a new
  display.

When a run holds several analysis groups, the action adds the plots for each group that has a preset. The existing traces
are not edited or deleted: a pinned net that was renamed is the user's to re-point, and the `<invalid>` label already says
which. Nothing is added without the click. A display where at least one trace still resolves gets no message: this is
about a display showing nothing, not about every stale trace.

The transient-only **Add Transient plots** message stays as it is when R-rdh-1 does not fire; when R-rdh-1 fires, its line
replaces that message rather than adding a second one for the same run.

**R-rdh-2 — An HB plots preset.** `HbPreset` in `src/Render/DataDisplay`, shaped like `TransientPreset`: `GroupOf`,
`Available`, `Build(ds, sourceRef)`, laid out from (0, 0), every trace an ordinary trace on `DataSourceRef.Selected` that
the user can re-point. Three plots:

- **Spectrum** of the output, as stems, at the analysis's own spectral axis (`harmonic` for one tone, `mixIndex` for two
  or more): the power through the IProbe whose name says "out" in dBm when there is one, else the output net's voltage in
  dBV. "Output" is chosen by `TransientPreset.OutputIndex`'s rule, so the two presets pick the same net on the same bench.
- **Waveforms**: the labelled nets' voltages against time over one period, at most `TransientPreset.MaxWaveforms`, through
  the function of R-rdh-3. Titled and labelled as the Transient preset's waveform plot is, so the two read alike.
- **Measures**: the bench's one-number measures as a table, exactly as `TransientPreset` builds it.

Wire it as the Transient preset is wired: a toolbar button and context-menu entry beside **Add Transient** (with its own
icon, enabled when `HbPreset.Available`), a `DataDisplayViewModel.AddHbAsync`, and the new display that an HB run creates
gets the HB plots instead of the single seeded trace (the branch in `AutoOpenOrCreateDataDisplayAsync` the transient case
already takes). A run holding both groups gets both presets, transient first.

**R-rdh-3 — An HB spectrum as a waveform.** An expression function that turns a spectral cube into a real waveform on a
`time` axis: `x(t) = X₀ + Σ Re{Xₖ e^{j2πfₖt}}`, the inverse of `fft`'s convention in `Evaluator.TimeDomain.cs` (`Dft`'s doc
comment), so `fft(waveform(HB1.V["out"]), f0)` gives back `HB1.V["out"]` at every harmonic. Name it in the existing
time-domain set's style (for example `waveform(x)` or `ts(x)`; check `Evaluator`'s reserved names and the `at`/`freq`
collisions the TRN-5 note records). Its result is a real cube with a `time` axis, so every time-domain function (`rms`,
`tmax`, `rise`, …) reads it unchanged.

- **One tone:** one period of the fundamental, a sample count that resolves the highest harmonic (at least 8 samples per
  period of it; a default the brief's implementer measures and records).
- **Several tones:** one period of the tones' common fundamental (the greatest common divisor of `ToneFreqs`, within a
  relative tolerance that is stated). When there is none, or its period would need more samples than a stated budget
  (two-tone 1 GHz and 1.001 GHz is 1,000 periods of the carrier), the function is refused with a sentence naming the tones
  and the period it would need. A silent truncated waveform is the wrong answer. The preset then leaves its waveform plot
  out rather than holding nothing but an error, as `TransientPreset` leaves its spectrum out when the record is shorter
  than a period.
- Every other axis (a sweep's) is kept, as the time-domain functions keep it.

**R-rdh-4 — A marker readout names its unit.** Every marker row that prints a trace's value (`GetStemValString`,
`GetMarkerValString`, `BuildCubeMarkerBoxLines`'s value rows, the multi-marker rows) appends the value's unit when it is
known: `V(Vout_rect)=19.67 dBV`. Known means one of:

- the cube's own `CubeValueUnit`;
- a dB transform or function applied to a quantity whose unit is known: `dBm(...)` is dBm; `dB(...)` or a `dB20`
  transform of a voltage is dBV, of a current dBA; of a ratio (an S-parameter, a gain) plain dB;
- the quantity itself: a transient or HB `V` cube is V, `I` is A, `P`/`Pprobe_W` is W, `time` axis values are s.

Where the unit is not known, the row stays as it is: a guessed unit is worse than none. Put the inference in one place the
marker rows and the trace card both call, and do not add a second copy beside `Plot.XLabelFor`.

**R-rdh-5 — Docs.** `docs/user/src/reference/data-display.md` (the HB preset, the message of R-rdh-1, units in marker
readouts), `transient.md` (the HB counterpart, one sentence), the HB section of `simulations.md` (the preset and the
waveform function), `measurements.md` (the new function, in the time-domain list). Regenerate each edited page with
`tools/DocGen --page`; figures wait for the end of the series. One example that runs HB on a rectifier or a PA shows the
HB plots: name it in the completion note.

## Gates

Minimal tests, one per claim, run by class:

1. **R-rdh-1:** a display holding only `TRAN1.V` traces, pointed at a run holding only `HB1`, is reported as showing
   nothing of the run, and the action adds the HB plots and edits no existing trace. A display with one resolving trace
   is not reported. The check that decides this is a function testable without `WorkspaceViewModel`.
2. **R-rdh-2:** `HbPreset.Build` on a one-tone HB result gives spectrum, waveforms and measures; on a two-tone result with
   a common fundamental gives all three; on an incommensurate two-tone result gives no waveform plot. The spectrum picks
   the same output as `TransientPreset` on the same bench.
3. **R-rdh-3:** `fft(waveform(x), f0)` returns `x` within 1e-9 relative at every harmonic of a one-tone HB result; a
   two-tone result with gcd 50 Hz (tones 50 Hz and 1 kHz) round-trips the same way; an incommensurate pair is refused
   with the sentence. `rms(waveform(HB1.V["out"]))` of a one-tone result matches `sqrt(X₀² + Σ|Xₖ|²/2)`.
4. **R-rdh-4:** the stem marker on `dB(fft(TRAN1.V[:, 1], 50))` reads `… dBV`; on `dBm(...)` reads `… dBm`; on
   `dB(S21)`-shaped data reads `… dB`; on an expression whose unit is not known reads the bare number, unchanged.
5. `TransientPresetFollowsSourceTests` and `TransientMeasurementCliTests` still pass.

UI: render the HB preset and the R-rdh-1 message headlessly with `tools/UiSnapshot` and look at them before reporting.

## Not here

- **The stem baseline.** Stems start at y = 0 and point away from it, on every spectrum: a value below 0 dBV hangs
  downward. That convention is the harmonic-stem brief's and stays.
- Rewriting pinned net labels when a net is renamed in the schematic.
- Deleting or replacing a display's traces automatically.
- A per-analysis results file (each run still writes one `.npy` holding the analyses it ran).

## Completion note (2026-11-17)

Built as above. Findings in `src/Ui/RESOLVED.md` (2026-11-17 entry).

- **R-rdh-1:** `DisplayWindowViewModel.ShowsNothingOfItsSource` reads every trace's `InvalidSpecText` across every tab,
  ignoring annotations and owner data. After a run that reopens an existing display, the post-run code posts one warning
  naming the display and the run's analyses, with **Add plots of this run** (`DataDisplayViewModel.AddRunPlotsAsync`:
  the run's presets, transient first, else one Rect plot with Add Trace's seed). It replaces the Transient message for
  that run. A display closed before the click is reopened through `AutoOpenOrCreateDataDisplayAsync` with the run's own
  base directory and schematic key.
- **R-rdh-2:** `src/Render/DataDisplay/HbPreset.cs`. It shares `TransientPreset.OutputIndex`, `Tokens`, `IndexOf`, `Hz`
  and the extracted `MeasuresTable`. A toolbar button (Waveform icon) and **Open Harmonic Balance Plots** on a result's
  menu. A new display of a run gets `AddRunPresetsAsync` (both presets, the table once). On a `mixIndex` axis the spectrum
  plot's frequency unit follows its highest product. Checked by rendering, with `tools/UiSnapshot`, displays built from
  the field test's own results and the *Harmonic Balance* example's PowerAmplifier run (drive sweep, read at its first
  point; its measures are swept, so it has no table). The field test's bridge bench (tones 50 Hz and 869 MHz) gets the
  spectrum only, as R-rdh-3 intends.
- **R-rdh-3:** `waveform(x[, f0])`, `src/Core/Expressions/Evaluator.Waveform.cs`, in `AxisFunctions`. 16 samples per
  cycle of the highest product, at least 128, at most 65,536 per period. The period tolerance is absolute in cycles
  (1e-6); a relative one accepted a rational approximation of √2.
- **R-rdh-4:** `Trace.ValueUnit` (`Trace.ValueUnit.cs`), appended by the marker value, stem and multi-marker rows; delta
  rows are left bare (a difference of dBV values is dB). Network traces: an S-parameter in dB (and Max Gain) reads dB.
- **R-rdh-5:** data-display, measurements, transient and simulations pages, each regenerated with `--page`. The CLI's
  reference text names `waveform`. The example is *Harmonic Balance*: it ships no display, so its first run opens on
  these plots; its README is unchanged. `examples/pulse-response/index.html` waits for the end-of-series regeneration
  (`--page` refuses an example README).
- **Gates:** `Core.Tests/Expressions/WaveformFunctionTests` (4), `Ui.Tests/DataDisplay/RunDisplayHbPresetTests` (11),
  `Ui.Tests/Transient/TransientPresetFollowsSourceTests` (1). Marker, preset and HB-spectrum classes (441) and
  `Firewall.Tests` (81) pass unchanged.
