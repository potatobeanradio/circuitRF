# Brief IM-14 — S-parameter plots to Touchstone

**Series:** `brief-img-0-overview.md` (D1, D2, D4, D6, D15, D16, D17; the owner's answers in §6) · **Tag:** `R-im14-<m>`
**Depends on:** IM-1, IM-2 (raster core, picture source), IM-9 (the text reader), IM-5 (the dialog shell), IM-11
(the `recognize` picture input). Independent of IM-3, IM-4, IM-7, IM-8, IM-10.
**Area:** new `src/Design/Imaging/Plots/` (`PlotKind.cs`, `PlotFrame.cs`, `RectAxes.cs`, `SmithFrame.cs`,
`PolarFrame.cs`, `PlotTraces.cs`, `PlotMarkers.cs`, `PlotQuantity.cs`, `PlotReading.cs`, `TouchstoneAssembly.cs`,
`MinimumPhase.cs`), `src/Design/Imaging/ImageKind.cs` (the *Plot* kind), `src/Ui/Recognition/` (the plot mode of
the dialog), `src/Ui/Views/WorkspaceWindow.axaml` (entry rows), `src/Cli/Recognize.cs`, `src/Cli/Serve/ToolCatalog.cs`,
`docs/design/image-to-circuit.md` (new section), `docs/design/cli.md`, `docs/user/src`

---

## 0. What the owner asked for (paraphrased)

Read a picture of an S-parameter plot — a rectangular plot against frequency, or a Smith chart — and turn it into a
Touchstone file. The typical source is a datasheet that shows S-parameter plots but ships no S-parameter file.

## 1. Feasibility, and the two things a picture of a plot does not contain

Reading curves from a plot picture is the most tractable kind of picture reading there is. Axes are straight, grids are
periodic, tick labels are numbers, and traces are usually distinct colours. Accuracy is set by the picture: one pixel
is typically 0.05–0.2 dB on a magnitude plot and 0.3–1 % of the radius on a Smith chart. The report states it for every
trace.

Two things are usually **missing**, and this brief treats both the way the series treats scale (D6). The value is never
a silent guess. It is a stated default, one click from being changed, and it is recorded in the file it produced:
1. **Phase**, when only a magnitude plot exists (a `dB(S21)` plot with no phase plot beside it).
2. **Frequency along a Smith-chart trace.** A Smith chart has no frequency axis.

A Touchstone file also needs **every entry** of its matrix, and a datasheet rarely plots all four of a two-port's
entries. R-im14-9 handles that.

---

## 2. Requirements

**R-im14-1 — The *Plot* kind.** `ImageKind.Classify` gains **Plot**, with the subtype *Rectangular*, *Smith* or *Polar*:
- *Rectangular*: two long perpendicular lines meeting at a corner (or a closed frame), with periodic ticks or grid
  lines along both, and number-shaped words beside the ticks.
- *Smith*: an outer circle (R-im1-8 fit) holding circles that are all tangent at one point of it (the constant-r
  family), plus arcs through that point (constant-x). An impedance chart is tangent on the right and an admittance chart
  on the left; both are read, and an overlaid Z-Y chart is read as Z.
- *Polar*: concentric circles about the centre of the outer circle, plus radial lines.
Plot is checked before Schematic: a plot's grid would otherwise read as wires.

**R-im14-2 — One picture, several plots.** A datasheet page often holds a grid of plots. `PlotReading.FindPlots`
returns each plot's frame separately. The dialog shows them as numbered boxes and every plot is read. A plot can be
excluded with a click.

**R-im14-3 — Rectangular axes.** `RectAxes.Calibrate(plot)`:
- **Ticks and gridlines** come from the frame's straight runs and their periodicity.
- **Tick labels** are read by the IM-9 reader under a **numeric grammar**: signed decimals, exponents (`1e9`, `10^3`, a
  superscript read as an exponent when it sits raised), multiplier suffixes (`2G`, `500M`), and a unit word on the
  label or in the axis title (`Frequency (GHz)`, `[MHz]`).
- **Fit:** label values are paired with their tick pixels, and a **linear** and a **logarithmic** mapping are fitted
  by least squares with outliers rejected (a misread label must not bend the axis). The better residual wins, and the
  residual is reported in pixels.
- **Fallback:** fewer than two labels read on an axis means the axis waits for the user. Two clicks on that axis and
  two typed values with units (IM-5's in-place box) calibrate it, and a log axis is one toggle.
- **Axis titles** are read for R-im14-6.

**R-im14-4 — Smith and polar frames.** `SmithFrame`: centre, radius and orientation (the real axis through the
tangency point), checked against at least two constant-r circles (r = 1 must pass through the centre). Any pixel then
maps to Γ directly; the chart needs no other calibration. The reference impedance comes from a `Z0 =` word on the chart
when one is read, else **50 Ω** (stated, one click to change). An admittance chart is mirrored into Γ. `PolarFrame`:
centre, the radius of the outer ring, and the ring labels for the radial scale (linear magnitude or dB); the angle
labels give the zero direction and the sense of rotation.

**R-im14-5 — Traces.**
- **Separation by colour:** the frame's colour clusters (R-im1-2) minus background, grid (thin, periodic, light),
  frame, text, the legend box (R-im14-7) and marker glyphs. Each remaining cluster is a trace candidate.
- **Rectangular traces** are functions of x. Per pixel column, the centre of the cluster's coverage gives y with
  sub-pixel accuracy.
- **Gaps** (a trace hidden under another, a marker, a label) of up to 2 % of the axis are bridged by interpolation and
  counted. Longer gaps are left as gaps and reported, and the Touchstone omits nothing silently (R-im14-8).
- **Same-colour traces** (black-and-white plots) are separated by line style (solid, dashed or dotted, from the
  run-length pattern along the skeleton) and, where two cross, by **slope continuity**: the continuation with the
  smaller change of direction wins. A crossing that stays ambiguous is reported and drawn amber in the overlay.
- **Smith and polar traces** are parametric curves, followed along the skeleton from end to end. The result is an
  ordered list of Γ values.
- **Click to follow:** a click on a curve the automatic pass missed makes it a trace and follows it from that point.
  A dragged rectangle excludes a region (a legend or annotation the automatic pass took for curve).

**R-im14-6 — What each trace is.** `PlotQuantity` assigns each trace an **S-parameter entry** (i, j) and a
**quantity**: dB magnitude, linear magnitude, phase (wrapped or unwrapped, in degrees or radians), real part,
imaginary part, complex Γ (Smith or polar), VSWR, return loss, or insertion loss.
- **Sign conventions:** return loss and insertion loss are positive numbers by definition; −dB is applied. VSWR maps
  to |Γ| only.
- **Evidence, strongest first:**
  1. the legend entry in the trace's colour or line style (R-im14-7);
  2. a label written along the trace;
  3. the y-axis title (`dB(S(2,1))`, `S21 (dB)`, `|S11|`, `Phase S21 (deg)`, `Return Loss (dB)`, `VSWR`);
  4. the plot's title.
- **Unassigned traces:** the assignment is a combo per trace. A trace no evidence names starts as *unassigned* and the
  Touchstone waits for it. Digitising the curves alone (CSV, R-im14-11) never waits.
- **Grammar:** IM-9's line-set grammar is widened to these names and their spellings (`S(2,1)`, `S21`, `s21`,
  `S_21`, `|S21|`, `∠S21`, `Mag`, `Ang`, `Gain`, `Isolation`, `RL`, `IL`). An unread title is unread, never guessed.

**R-im14-7 — Legends and markers.**
- **Legends:** a legend is a box of short line samples, each beside a word. Each sample's colour and style are matched
  to a trace, and the word goes through R-im14-6's grammar.
- **Markers:** a **marker readout** (`m1 freq=2.400GHz dB(S(2,1))=-0.512`, or a two-line table near a marker glyph) is
  read under a marker grammar. Where it reads, it is an **exact data point** on its trace. It replaces the pixel
  reading within one pixel of it, and its disagreement with the calibrated curve is reported as a check on the
  calibration.
- **Markers on Smith traces** are also the strongest frequency evidence (R-im14-8).

**R-im14-8 — Frequency on a Smith or polar trace.** Evidence, strongest first:
1. markers on the trace with frequency readouts (two or more);
2. a rectangular trace of the **same entry** in the same session (`dB(S11)` against frequency beside the S11 Smith
   chart): frequency is taken by matching |Γ| along both curves, monotone in order, wherever |Γ| varies enough to match;
3. a start and stop frequency **the user types**, with frequency spread along the trace **in proportion to the angle
   swept about the chart centre**, which is exact for a uniform line and a stated approximation for anything else.
- **Combining evidence:** markers and the stated range combine. Between two known frequencies the sweep is
  interpolated by swept angle.
- **Direction:** frequency increases **clockwise** (Foster's theorem for a lossless one-port; nearly always true for a
  passive reflection) unless markers or a matched rectangular trace say otherwise. The report states which rule set the
  direction.
- **No evidence:** with neither (1) nor (2), the trace **waits** for (3). The start and stop boxes are the highlighted
  control, never pre-filled.

**R-im14-9 — Assembling the file.** `TouchstoneAssembly.Build(session) → SNP`:
- **Ports:** the largest index of any assigned entry (S11 only → `.s1p`; S21 → `.s2p`), up to four.
- **Grid:** from the overlap of every trace's frequency range, at the densest trace's pixel resolution, rounded to a
  tidy step (1-2-5). Editable as start/stop/points, linear or log. A grid point outside a trace's own range is not
  extrapolated: the range shrinks, and the report says which trace bounded it.
- **Resampling** per quantity: dB linearly in dB, phase **unwrapped** then linear, Γ in the complex plane, and log
  frequency axes in log f.
- **Combining traces:** magnitude and phase traces of one entry combine. Two traces of the same entry and quantity (a
  Smith S11 and a dB S11) are cross-checked, and the larger difference is reported. The *Prefer* combo per entry picks
  which is written (default: Smith or polar for reflection entries, because they carry phase).
- **Missing phase** (a magnitude with no phase) — per entry, one combo:
  - *Minimum phase (estimate)* (default; R-im14-10);
  - *Zero phase*;
  - *Linear phase from a delay* the user types.
- **Missing entries**, for two ports and above — per entry, one combo, each default stated:
  - S12 = S21 (*reciprocal*) when |S21| ≤ 0 dB throughout, else zero (*an amplifier's isolation is not plotted*);
  - S22 = S11 (*symmetric*); *zero (matched)* and *unassigned* are the alternatives.
  - Three- and four-port matrices get *zero* for every unplotted entry, as their default.
  - Every default is **one click from being changed**, the owner's ruling on D6 applied here.
- **Refusal:** the file is refused only while some trace is unassigned, some Smith trace has no frequency, or an
  axis is uncalibrated. Each case names the control that answers it.

**R-im14-10 — Minimum phase.** `MinimumPhase.FromMagnitude(f, |S|)`:
- **Method:** the phase is the Hilbert transform of ln|S| (cepstral method), computed on a uniform grid with the
  repo's FFT (`HbFft`, or an in-house radix-2 if that one does not fit). The band is extended to DC and beyond the top
  frequency by holding the end values, then tapered.
- **Error report:** the error grows towards the band edges. The report states the band where the estimate is
  trustworthy, the middle 80 % by default.
- **Health check:** `TouchstoneHealth`'s causality check runs on the result, and its verdict is in the report.
- **Labelling:** a minimum-phase entry is labelled an estimate wherever its value is shown (*estimated*, the panel
  readouts' normal word).

**R-im14-11 — Outputs.**
- **The Touchstone** is written by `TouchstoneIO.Write` (version 1.1, `DB` format by default since most sources are
  dB plots; `MA`/`RI` selectable), with **header comments** recording each source picture's file name and hash, every
  trace's assignment and evidence, each calibration's residual, each rule applied to missing phase and entries, and
  the per-trace resolution.
- **A CSV of the raw digitised traces** (`frequency or x, value` per trace, in the plot's own units, no resampling) is
  always available. It works for **any** plot, S-parameter or not: a plot of temperature against time still
  digitises, it just makes no Touchstone.
- **The source pictures are kept beside the output** as `<name>.source-<n>.<ext>` (D4), unless turned off.
- **After Create:** the Touchstone opens in the Data Display with the same plot types the pictures showed, so the
  result can be compared with the source at a glance. `TouchstoneHealth`'s report (passivity, reciprocity, causality)
  goes to Messages.

**R-im14-12 — The dialog's plot mode.** In the IM-5 dialog, *It is* gains **Plot** and *Make* gains **Touchstone** and
**CSV**.
- **Several pictures make one file.** The source pane is a **list**: *Add picture…*, drop, and paste each add one.
  Every picture's plots are read into one session.
- **Canvas overlay:** each plot's frame and axes (tick labels it read, in green; unread ones amber), each trace drawn
  over its curve in its own colour and labelled with its assignment (`S21 dB`), marker points, bridged gaps dashed,
  ambiguous crossings amber.
- **Right column: the traces table.** Swatch, plot, entry (combo), quantity (combo), range, points, resolution, and
  the evidence as the confidence dot. Below it, the matrix grid: an n × n grid of entries, each showing where its
  data came from (*plot 2*, *= S21*, *zero*, *min phase*), each cell a combo. This is the one place every missing-data
  rule is seen together.
- **Frequency row** for Smith and polar traces (R-im14-8), and the output grid row (R-im14-9).
- **Create** asks for the file name and location (default: the workspace folder, named after the first picture).

**R-im14-13 — Ways in.** These sit beside the IM-6 rows:
- **Edit ▸ Paste Image as Touchstone…**, with the same clipboard enablement rule as IM-6.
- A Project Tree image row **Create Touchstone from Plot…**.
- **Tools ▸ Digitise Plot…**, which opens the dialog empty in plot mode.
- A picture dropped on the dialog while in plot mode is **added** to the session, not swapped in.

**R-im14-14 — CLI and MCP (D15: no new verb).** `recognize` reads plots, and several pictures make one session:
`recognize s11.png s21.png -o part.s2p`.
- **Flags:**
  - `--image-kind plot`;
  - `--plot <n>` to choose plots within a picture;
  - `--x-axis "<px>=<value>,<px>=<value>"` and `--y-axis …` (per picture, `@<n>` to name the plot), plus
    `--x-log`/`--y-log`;
  - `--trace "<#rrggbb|style>=S21:db"` (assignment);
  - `--smith-z0 50Ohm`;
  - `--freq <trace>=<start>..<stop>`;
  - `--phase S21=min|zero|delay:<t>`;
  - `--fill S12=S21,S22=zero`;
  - `--grid <start>..<stop>/<n>[log]`;
  - `--format db|ma|ri`;
  - `--csv out.csv`;
  - `--overlay out.png`.
- **Refusals:** each R-im14-9 refusal exits 1 and prints the flag that answers it. An unassigned trace prints the
  trace's colour, its plot, and the evidence it lacked.
- **`--json`:** `result.recognize.plots` (frames, calibrations with residuals, traces with assignments, evidence and
  resolution, the matrix map).
- **MCP:** the `recognize` tool takes the same parameters, takes several picture paths, and returns the overlay as
  image content. An agent can check every reading by eye and answer an unassigned trace with `trace`.

**R-im14-15 — No second route.** The dialog, the CLI and the MCP tool call `PlotReading.Read` and
`TouchstoneAssembly.Build`. AS-7's source-scan rule covers the new namespace.

**R-im14-16 — Docs.** The design note gains *Plots to Touchstone*. `cli.md` §26 gains plot input. The user page
*Create from Image* gains a *Plots* section covering:
- what reads well (a rectangular plot with labelled ticks; a Smith chart with markers);
- what is assumed and where to see it (the matrix grid);
- the minimum-phase estimate and its band edges;
- the advice to digitise the phase plot too whenever the datasheet has one.
Edit sources only; **do not run DocGen**.

## 3. Not in this phase
- Group delay, stability-factor or noise-figure plots as Touchstone input. Noise parameters would need a
  noise-parameter block, which is a later addition.
- Mixed-mode (differential) S-parameters.
- 3D or contour plots.
- Load-pull contour pictures, which would be their own brief.
- Vector sources (a PDF datasheet's plots as vectors would be exact; that is D18's other series).

## 4. Gates (minimal tests, run only these classes)

**Round trip through circuitRF's own plotter.** A band-pass filter's `.s2p` (the test simulates a small LC filter with the sparam
engine and writes it, since `testdata/`'s passive two-ports are flat pads), `testdata/ndf/potentially_unstable_amp.s2p`
for the above-0 dB defaults, and a one-port reflection are drawn by the **`plot` verb** to PNG: `dB(S21)` and `phase(S21)` rectangular, a log-frequency
rectangular, and S11 on a Smith chart with three frequency markers. Each is then read back.
- `PlotRoundTripTests`:
  - a rectangular `dB(S21)` comes back within 1.5 × the stated per-pixel resolution at every grid point;
  - the Smith S11 comes back within 1 % of the radius in |ΔΓ| with frequency from its markers;
  - the four pictures together give a `.s2p` whose |ΔS| against the original is within the stated resolutions, the
    owner's |ΔS| measure, not dB, for small entries.

**Pictures drawn another way.** These are committed PNGs from the IM-12 generator, using shipped fonts only:
- `PlotForeignDrawingTests`, one case per claim:
  - dark background;
  - log frequency axis with `1e9`-style labels;
  - two black traces, one solid and one dashed, that cross;
  - a legend inside the plot;
  - an admittance Smith chart;
  - a JPEG at quality 60.

**The rest:**
- `RectAxesTests`:
  - one deliberately misread tick label does not move the fit (outlier rejection);
  - one readable label waits for two clicks.
- `SmithFrameTests`:
  - an impedance and an admittance chart give the same Γ for the same drawn point;
  - a chart with no `Z0 =` word reads 50 Ω and says so.
- `PlotFrequencyTests`:
  - two markers interpolate by swept angle;
  - no markers and no matching rectangular trace waits for a typed range;
  - direction defaults to clockwise and is overridden by markers.
- `TouchstoneAssemblyTests`:
  - S11 and S21 only, with passive |S21|, gives S12 = S21 and S22 = S11, each recorded in the header;
  - an S21 above 0 dB defaults S12 to zero;
  - an unassigned trace refuses naming it.
- `MinimumPhaseTests`: a minimum-phase two-pole low-pass's |S21| gives back its phase within 2° across the middle
  80 % of the band.
- `PlotCliVerbTests`:
  - two pictures → `-o x.s2p` is byte-identical to the in-process assembly (bar the header's time line, as `em`'s gate
    allows);
  - a Smith trace without frequency exits 1 naming `--freq`.
