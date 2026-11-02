# Brief PDG-2 — Plot digitiser: fit a curve to a trace (linear, exponential, polynomial, spline)

**Series:** PDG-1 … PDG-3 (plot digitiser improvements) · **Tag:** `R-pdg2-<n>` · **Written:** 2026-10-18
**Follows:** `brief-img-14-sparameter-plots-to-touchstone.md`, `brief-ptu-5-one-row-per-colour.md` (`TraceJoin`)
**After:** PDG-1. A fit is in the units of the trace's own axis, which PDG-1 makes per trace. It is independent of PDG-3.

---

## 1. What the owner asked for

A digitised trace is a pixel reading, with a step of one pixel's worth of value and the jitter of a pen's edge. A curve
fit smooths that away and gives the curve a formula. **The owner chose both halves** (2026-10-18):

- **the fitted curve replaces the raw reading in the output** (Touchstone, and alongside it in the CSV), and
- **the fit's equation and coefficients are shown**, with its residual against the raw reading, and can be copied.

Models: **linear**, **exponential**, **polynomial** (chosen degree) and **spline**.

## 2. What to build

### R-pdg2-1 — The fits, as a pure file: `src/Design/Imaging/Plots/TraceFit.cs`

```csharp
public enum TraceFitModel { None, Linear, Exponential, Polynomial, Spline }
public sealed record TraceFitSpec(TraceFitModel Model, int Degree = 3, double? Smoothing = null, bool LogX = false);
public sealed record TraceFitResult(TraceFitSpec Spec, IReadOnlyList<double> Coefficients, string? Expression,
                                    string XVariable, string XUnit, double RmsResidual, double MaxResidual,
                                    double RmsResidualPx, string? Refusal);
public static class TraceFit
{
    public static TraceFitResult Fit(IReadOnlyList<PlotSample> samples, TraceFitSpec spec, AxisCalibration y, ...);
    public static double Evaluate(TraceFitResult fit, double x);
}
```

The names are a suggestion; the shape is what matters. Requirements:

- **Fitted in the trace's own plotted units.** That means dB for a dB trace, degrees for phase (after
  `PlotReading`'s unwrap, when `Unwrapped`), and the axis's value for anything else. It is the quantity the user sees
  the curve drawn in, and what "the curve looks like a straight line" refers to. **Rectangular traces only.** A chart
  trace (Γ on a Smith chart) offers only `None`. Say so in the control's tooltip rather than hiding it.
- **x is the axis's value**: hertz on a frequency axis, with the expression written in the axis's DISPLAY unit
  (`RectCalibration.XUnit`) so its coefficients are readable. On a log x axis, `LogX` defaults on, so the fit is in
  `log10(x)`: a straight line on a log plot is what the user is looking at.
- **Linear:** y = a + b·x, by least squares.
- **Polynomial, degree 1…8:** least squares on x centred and scaled to [−1, 1], solved by QR or an equivalent
  well-conditioned method, never the raw normal equations. The coefficients are then expanded back to raw x for
  display. Refuse a degree ≥ the number of distinct x values.
- **Exponential:** y = a·e^(b·x) + c. Start from a log-linear fit of y − c₀ (c₀ just beyond the data's extreme, on the
  side its curvature points to), then refine all three by Levenberg–Marquardt on the **y residuals**, so the
  log-domain fit does not over-weight small values. Refuse with a sentence when it does not converge. Never return a
  silently poor fit.
- **Spline:** a cubic smoothing spline (Reinsch or equivalent). **The default smoothing is the reading's own
  resolution:** choose the parameter so the RMS residual equals the trace's `Resolution`, the value of one pixel.
  That removes pixel noise and nothing the picture actually resolves. A typed `Smoothing` (an RMS target in the
  trace's units) overrides it. A spline has no compact equation: `Expression` is null and the readout says
  `spline · N knots`.
- **Never extrapolates.** `Evaluate` outside the trace's own x range returns NaN. The Touchstone grid is already the
  overlap of the traces' ranges, and that stays true.
- **Exact samples stay exact.** Marker readouts (`PlotSample.Exact`) are weighted heavily (×100) in every
  least-squares fit, and the spline passes through them.
- Residuals are reported in the trace's units AND in pixels (`RmsResidualPx`, via the axis's `PerPixel`). The pixel
  figure is the one that says whether the model fits: under ~0.5 px is "fits the drawing"; several px is the wrong
  model.

### R-pdg2-2 — The equation is circuitRF expression text

`Expression` is written in the expression language (`docs/design/expressions.md`): `+ - * / ^`, `exp`, `log10`,
with the variable named after the axis (`f` on a frequency axis, else `x`). Examples:
`-0.512 + 0.0123*f - 4.1e-4*f^2` with `f` in GHz, and `3.2*exp(-0.81*f) + 0.05`. Coefficients use round-trip
precision (`"R"`/`G17`), so the copied text reproduces the fit. The unit of the variable is stated beside the
expression, never inside it: `f in GHz`. Mind `-2^2 == -4`: emit `- 4.1e-4*f^2`, never `+ -4.1e-4*f^2` written as
`(-4.1e-4)f^2`.

### R-pdg2-3 — Where the fit applies

- A fit belongs to the curve the output uses. For pieces that PTU-5's `TraceJoin` joins (same entry and quantity),
  the fit is computed **once, on the joined curve**. In the Simple view the fit control sits on the colour row; in
  the Detailed view each piece shows its group's fit. A trace that is not joined is fitted on its own.
- **Touchstone:** where a fit is set, `TouchstoneAssembly` evaluates the fit at each grid frequency **in place of**
  resampling the samples (`TouchstoneAssembly.cs` ~line 384-390). Per-quantity handling is unchanged after that point:
  a dB fit still converts to magnitude, and a phase fit still converts to radians. Where no fit is set, nothing
  changes.
- **CSV:** append a `y_fit` column at the END of `Columns` (after PDG-1's `y_axis`), holding the fit at each
  sample's x, empty where no fit is set. The `y` column stays what was read; the CSV's existing promise is "exactly
  as read", and that promise stays true of `y`.
- **Header and report:** each fitted trace's model, coefficients or expression, residual in units and in pixels, and
  any refusal. A refused fit leaves the trace unfitted and is a finding, never a refusal of the whole file.

### R-pdg2-4 — The dialog

- In the Traces table, a **Fit** combo per row: `None · Linear · Exponential · Polynomial · Spline`. A degree box
  (1–8, default 3) appears for Polynomial, and a smoothing box (watermark = the resolution default) for Spline.
  Undoable through the existing assignment undo.
- Under a fitted row: the expression with its variable's unit (or `spline · N knots`), `RMS 0.012 dB (0.3 px)`, and a
  **Copy** button that copies the expression text (disabled for a spline). It is selectable text, as PTU-1 made the
  output text.
- **On the picture:** the fitted curve is drawn over its trace, thin and dashed in a contrasting colour, mapped back
  through `PixelAt`. That is how a user sees at a glance that a fit has missed. The PTU-3 rule still holds: never hide
  the trace's own colour.

### R-pdg2-5 — CLI and MCP

- `--fit <selector>=<model>[:<degree or smoothing>]`, repeatable, using the existing trace selector grammar (`T3`,
  `#rrggbb`, `dashed`, `@n`) plus `all`: `--fit T3=poly:3`, `--fit all=linear`, `--fit #c03030=spline`,
  `--fit T2=spline:0.05`, `--fit T4=exp`.
- JSON: each trace carries `fit { model, degree, expression, xUnit, coefficients, rms, rmsPx, refusal }`. MCP
  `PlotOptions` gains `fit`.
- `cli.md` §26.9.

## 3. Gates — one test per claim, `tests/Ui.Tests/PlotRecognition/TraceFitTests.cs`

Pure tests on synthetic samples, no pictures except the last:

1. Linear, a cubic, and an exponential are each recovered from exact samples to 1e-9 relative.
2. A degree-6 polynomial on x in hertz (1e9…1e10) is recovered, which proves the scaling does its job.
3. Spline default: on a smooth curve plus ±½-resolution quantisation, the RMS residual is the resolution within 5 %.
4. Exponential refuses, with a sentence, on data it cannot fit (a sine), and does not return a poor fit.
5. `Evaluate` outside the range is NaN.
6. **The copied expression evaluates, through Core's expression engine with `f` bound in the stated unit, to the
   fit within 1e-9 relative at five points.** This is the test that holds R-pdg2-2.
7. An exact (marker) sample is passed within 1e-6 by the spline.
8. End to end: a circuitRF-plotted smooth dB(S21) (`PlotPictureSet`), fitted with a degree-4 polynomial, written as
   `.s2p`: |ΔS21| against the source is ≤ the unfitted reading's on the same picture.
9. CLI: `--fit T1=poly:2` puts a `fit` in the JSON and a `y_fit` column in the CSV. One `PlotCliVerbTests` case.

Run **only** `--filter "FullyQualifiedName~TraceFitTests"` and that one CLI case; finish with
`TouchstoneAssemblyTests` once, since nothing unfitted may change. Never the full suite. Read failures from the TRX.

## 4. Rules

- No change to the reader (`PlotTraces`, `RectAxes`, frame detection). A fit runs **after** reading, on samples.
- No new native or package dependency. If a dense solve is wanted, NumFlat is already in the stack, but check
  `src/Design`'s references first: adding one to `src/Design` needs the Firewall gate green, and a small QR written
  inline is acceptable for ≤ 9 unknowns.
- At most two rendered dialog pictures, via `brief-ptu-0-overview.md` §4.
- Docs: `docs/design/image-to-circuit.md` (new §15.x "Fitting a trace"), `create-from-image.md` `#plots`, `cli.md`
  §26.9. No DocGen. Findings go in `src/Design/RESOLVED.md` / `src/Ui/RESOLVED.md`. No commit unless the owner asks.
