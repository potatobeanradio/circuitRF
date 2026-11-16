# Brief PAA-11 — fine pattern cuts from a fields analysis, and the example's displays drawn from them

**Series:** PAA-0 … PAA-11 · **Tag:** `R-paa11-<n>` · **Depends on:** PAA-2 (the local evaluation), PAA-6 / PAA-6b (fields from
harmonic-balance and envelope waves), brief-cosim-pattern-3 (a fields analysis's far field) · **Design note:** `docs/design/phased-array.md` §6.2
**Area:** `src/Design/Em3d/FieldCosimulation.Array.cs`, `src/Design/Em3d/FieldCosimulation.FarField.cs`, `src/Engine/Array/ArrayBeam.cs`,
`src/Engine/Array/ArrayPattern.cs`, `src/Render/DataDisplay/TraceResolve.cs` (polar), `examples/Phased Array/*.cdd`,
`docs/user/src/reference/phased-array.md`, `src/Ui/Diagnostics/FigureCatalog.cs` / `Fixtures/DocPhasedArrayFixtures.cs`.

## The gap

A fields analysis on a Phased Antenna Array publishes its pattern only on the EM record's grid: `farfield.U`, `Etheta`, `Ephi` over
the record's θ and φ. The shipped Phased Array example's record is on a 5° grid (θ 0…90°, 19 × 72 directions), and its Transmit array is
16 × 16 at 5.9 GHz, a beam about 7° wide. Its display draws the φ = 0 cut of `F2.farfield.U` for each θ0: the polar trace is a polygon
through 5° samples, one or two across the main lobe, so every lobe is a triangle and the nulls and sidelobes between samples are missing
or misplaced. The Brute Force and Envelope displays draw `F1.farfield.U` the same way, and so do the user page's figures of them
(`phased-array-transmit-display`, `phased-array-bruteforce-display`, each with its dark twin).

The numbers are not wrong: §6.2 forms every metric (beamwidth, sidelobe level, pointing error, scan loss) on its own fine cuts through
the peak, at a step no coarser than a tenth of the beamwidth, with the element pattern interpolated and the array factor exact
(`ArrayBeam`). But those cuts are not published, so nothing a display can plot is finer than the record, and the overview carries no
sign of the grid it was formed on, which §6.2 says it should.

## Requirements

**R-paa11-1 — Fine cuts published.** A fields analysis on an array publishes pattern cuts beside the overview: for each frequency, each
cut plane φc, over a signed angle t from −90° to +90° (t ≥ 0 is the direction (θ = t, φc), t < 0 is (θ = −t, φc + 180°)), so one cut is
a whole plane through boresight and a family over θ0 overlays on one polar plot. Cubes in the `farfield` group, for example
`farfield.CutU`, `CutEtheta`, `CutEphi` (W/sr and V), axes `freq`, `cut` (φc, deg), `angle` (t, deg), and the port axis the overview has.
Planes: a new `cuts=` key (degrees, comma-separated); its default the lattice's two principal planes, `0,90`. Step: no coarser than a
tenth of the beamwidth `ArrayBeam` estimates for the lattice, pitch and frequency, scanned to the cut's largest |t| (so a narrower
array gets a finer cut), and no coarser than the record's own step; the run says the step it took. The element pattern is interpolated
from the record as §6.2 does it and the array factor evaluated exactly; from harmonic-balance or envelope waves (PAA-6, PAA-6b) the
elements' waves are the weights, as the overview takes them.

**R-paa11-2 — The overview says its grid.** A fields analysis on an array whose overview step is coarser than a tenth of the
estimated beamwidth says so once, naming both steps and the cut cubes to plot instead (§6.2's "labelled with its grid").

**R-paa11-3 — The polar plot takes a cut.** A polar trace of a cut cube (an `angle` axis kept as X, `cut` pinned or iterated) draws the
whole plane from −90° to +90° with no mirroring (`PatternWholePlane` is for the overview's φ and φ + 180° halves); the trace card offers
the cut cubes as it offers the overview's.

**R-paa11-4 — The example draws the cuts.** `1 Transmit.cdd`, `5 Brute Force.cdd` and `6 Envelope.cdd` plot the cut at φc = 0 (each
θ0 a trace where the display iterates it) instead of the overview's φ = 0 slice; titles and the README's description follow.

**R-paa11-5 — Documentation.** `phased-array.md`'s Transmit-bench section and its list of a fields analysis's cubes name the cut cubes
and `cuts=`; the user page's figures `phased-array-transmit-display` and `phased-array-bruteforce-display` (and their dark twins) are
regenerated with `DocGen --figure` and looked at (rendered with Svg.Skia), and any Envelope figure the same.

**R-paa11-6 — Fast by construction.** A fields analysis is what Simulate and every point of a sweep wait on, so the cuts may not make
it noticeably slower:
- The step has a floor (0.05°), so a cut is at most 3,601 samples whatever the array; at 64 × 64 (a 1.6° beam) it is about 1,100.
- The element pattern is evaluated once per pattern class and direction (as `PropagationChannelModel.PatternEvaluations` counts for
  the channel), never per element; the position phases come from `ArrayPattern`'s row and column tables, O(samples × (rows + cols))
  trigonometry, leaving one complex multiply-add per element and sample (about 4.5 M a cut at 64 × 64).
- Under `FieldsOutput.OnDemand` (R-cp4-1) the cuts are formed only when something reads them, as the overview is.
- Budget: the cuts add no more than 20 ms or 10 % (whichever is larger) to one fields evaluation at 64 × 64, and no more than 5 %
  to the example's Transmit sweep; the result file grows by under 1 MB for the example. Measured once with a scratch harness (not a
  timing test) and recorded in `src/Engine/RESOLVED.md` with the 16 × 16 and brute-force figures; a requirement that misses its
  budget says why in the completion note rather than shipping slower silently.

**R-paa11-7 — Light to draw.** A cut trace is at most 3,601 points, so a polar plot of a θ0 family (the Transmit display's seven)
draws without decimation; the Data Display's redraw on a drag or resize stays as it is for the overview's traces.

## Gates

1. **On the record's grid.** At every cut sample that falls on the record's grid, the cut equals the overview there to 1e-12 relative,
   for the Transmit array at θ0 = 0 and 30°.
2. **Exact between.** A 16 × 16 uniform array of isotropic elements: every cut sample equals the closed-form array factor to 1e-12 of
   the peak, and the half-power width read off the cut is `HpbwDeg(F, "elevation")`'s to within the cut's step.
3. **Resolved.** The example's Transmit run: each θ0's cut has at least ten samples across its main lobe, and its peak is within one
   step of `PointingErrorDeg`'s peak.
4. **From waves.** The Brute Force bench's cut from harmonic-balance waves equals the linear cut at small drive to the brute-force
   gates' existing tolerance.
5. **Cost, by counting** (routine tier, no clock). One fields evaluation of a 64 × 64 array with two cuts: the element pattern is
   evaluated at most once per pattern class per cut direction, the position phases come from the row and column tables (a counter of
   table entries: samples × (rows + cols)), and no cut exceeds 3,601 samples. Evaluating the pattern or the phase per element turns
   it red; that is checked by breaking it once, then restoring it.
6. **Lazy.** An `OnDemand` fields evaluation that nothing reads forms no cut (the same counters read zero).
7. **Figures and drawing.** The regenerated figures show smooth lobes with their nulls, and the Transmit display rendered with
   `tools/UiSnapshot` is looked at; its render time beside the overview version is recorded with R-paa11-6's measurements, not
   asserted.

## Not here

A finer whole-sphere overview (the 3D view and the overview cubes keep the record's grid); smoothing or spline drawing of a coarse trace
in the viewer, which would draw a plausible curve the data does not hold.

## Completion note (2026-11-16)

**Built.**

- **Engine** (`src/Engine/Array/ArrayPattern.Cuts.cs`): `ArrayCutGrid`, `ArrayPattern.CutGrid` / `Cuts` / `CutBasis`, with the
  step rule of R-paa11-1 (the record's θ step over the smallest whole number that makes it a tenth of the estimated beamwidth or
  finer, floored at 0.05°) and per-thread counters `CutsFormed`, `CutPatternEvaluations`, `CutTableEntries`. The grid keeps its
  phase tables and each class's sampled pattern; each class's factor is one pass over the elements per sample, and a rank-one
  class takes the overview's separable sum, so a sample on the record's grid is the overview's own arithmetic.
  `ElementPattern.AtDegrees` reads a stored sample on a grid point instead of rebuilding it from magnitude and phase.
- **Fields analysis** (`src/Design/Em3d/FieldCosimulation.Cuts.cs`): `farfield.CutU`, `CutEtheta`, `CutEphi` over
  [freq, cut, angle, port] from the linear solve and from HB waves; an envelope's time-averaged `CutU` from the waves'
  covariance over `CutBasis`. `cuts=` (default `0,90`) through the `.cnl`, the schematic's analysis record, the schema, the
  analysis body and Duplicate. The run says the step it took and, when the overview is coarser than a tenth of the beam, says so
  and names `CutU`. None formed under `OnDemand`. A sweep keeps its first point's grid so the cubes stack; the last six grids are
  kept across evaluations.
- **Display**: a cube with `cut` and `angle` axes seeds `angle` as X, `cut` pinned and the innermost sweep a family; the
  whole-plane option on a cut trace refuses with a sentence saying the cut already is the whole plane. The polar plot needed
  nothing else: a `deg` axis is its compass, and a negative angle draws left of boresight.
- **Example and docs**: the three displays plot `F*.farfield.CutU` at φ = 0, one trace each; README and `phased-array.md`
  (cubes, `cuts=`, the Transmit step); design note §6.2; both figures and their dark twins regenerated and looked at.

**Gates** (`tests/Ui.Tests/PhasedArray/PatternCutTests.cs`, 8 tests, all pass): 1, 444 grid samples at θ0 = 0 and 30° equal to
0 relative; 2, the closed form to 7.7e-16 of the peak, half-power width 6.3463° against the metric's 6.3571° (step 0.625°);
3, 13 to 24 samples across each θ0's main lobe at 0.556°, F1's peak at 30.000° against PointingErrorDeg 0.085°; 4, the HB cut
equals the linear one to 8.0e-10 of the peak at −60 dBm; 5, 64 × 64 two cuts: 10,088 pattern evaluations (4 classes ×
2,522 directions), 322,816 table entries, 1,261 samples a cut, and red when broken either way (10.3 M evaluations; 0 entries);
6, OnDemand forms none; plus `cuts=` round trips and a polar trace drawing −90…+90°. Gate 7: the figures and a UiSnapshot of
the Transmit display show smooth lobes with their nulls.

**R-paa11-6** is recorded in `src/Engine/RESOLVED.md`. The cut pair costs 0.14 ms at 16 × 16 and 3.5 ms at 64 × 64, under 20 ms;
about 3 % of a Transmit sweep point by its own time, though an in-place A/B on the shared machine moved between 1 % and 14 %;
+0.245 MB on the result file.

**Found and fixed on the way: the example's PA loadpull failed at every load.** A recent HB change held two interface nodes with
ideal sources when Z_NN(0) was rank-deficient; the loadpull's tuner net is joined to the drain by an ideal probe, so the held
network was a loop of sources and all 48 loads stopped at "DC extraction singular" (the regenerated Brute Force figure had no
contours). The held form now falls back to holding through the inductance regularization R, and at RF a rank-deficient Z_NN is
inverted with R at the joined nodes, which also fixes three loads that had never converged. Details and gates in
`src/Engine/RESOLVED.md` (brief PCH-12's section).
