# Brief YA-16 — Review fixes: the yield verbs, MCP and the Data Display

**Series:** `brief-yield-0-overview.md` (follow-up after YA-13/YA-14) · **Tag:** `R-ya16-<m>` · **Depends on:** YA-15
(only for R-ya16-3's planned-trials scalar, if both briefs touch `StatisticalDataSet`)
**Area:** `src/Cli/Yield.cs`, `Yield.Corners.cs`, `Yield.Doe.cs`, `src/Cli/PlotVerb.cs`, the MCP server's `run`
paths for yield, corners and doe, `src/Render/DataDisplay/` (`TrialRenderer`, `RenderTheme`, `TrialResolve`,
`SpecLineResolve`, `StatisticsTablePreset`, `StatisticsRenderer`, `ContributionParetoPreset`, `YieldDisplayPreset`),
`src/Design/Statistics/` (`StatisticalDataSet`, `ResultContributions`), `src/Ui/Yield/` (share bars only),
`docs/design/cli.md` §25, `docs/user/src/reference/yield.md` and `cli.md` (sources only)

---

## 1. Goal

A post-series review of the headless surfaces and the Data Display found the defects below. Most were confirmed by
running the CLI against a copy of `examples/Yield`. Fix each one, with one small gate per claim, and **confirm each
before changing it**. If a finding turns out to be wrong, say so in the completion note and leave the code alone.

The review checked these and found them sound; **do not touch them**: the histogram and CDF binning and their NaN
handling, the percentile and σ envelopes, Cpk and σ-to-limit, the spec-line positions and frequency scaling, the DOE
aliasing and strongest-pair map, the centering exit codes and JSON, and culture handling in this brief's files.

**The CLI rule this brief enforces:** a flag the verb cannot honour is **refused**, through the existing
`YieldCornerFlag` diagnostic (or its sibling for DOE). It is never accepted and silently dropped. A run that
succeeded while ignoring part of what it was asked is the failure that cannot be noticed.

## 2. Owner decision D-a (decided 2026-10-08)

**Contributions with correlated entries.** The share is β·r, from the regression. With correlated entries it is not
bounded to 0–1. On the shipped Bandpass example, PassbandSpec ranks `C1.C` at 122.8 % and `C3.C` at −43 %.

**Decided:**
- β·r stays unchanged in JSON and in the result file, because it is the honest number.
- Every DISPLAY shows the share clamped to 0–100 %: the Pareto bars, the cumulative line, the Yield panel's share
  bars and their tooltips, and the CLI's text `--contributions` table.
- The cumulative line is computed from the clamped values, renormalised to 100 %.
- The ranking does not change.

The Yield example workspaces have not been released, so nothing shipped depends on today's numbers or on the saved
`.cdd`. Change them freely where a fix calls for it.

## 3. Requirements

**R-ya16-1 — `--save-corner` saves exactly the trial it names (most severe; confirmed).**
- **The defect:** `src/Cli/Yield.cs` ≈ 556–564 records only seed, sampling and trials. It drops what narrowed or
  changed the draws: `--vars`, and very likely `--sigma-scale`, `--process`/`--mismatch` and `--set`.
- **Failure:**
  1. `yield mc BandpassYield.csch --trial 5 --vars C1.C,C3.C --save-corner VC` prints two drawn values.
  2. `yield corners --corners VC` then replays all six parts drawn, with no note.
- **Fix:** store what the corner format can carry. Refuse `--save-corner` together with any flag it cannot carry,
  naming that flag. Check each flag in the list rather than assuming.
- **Gate:** the review's two commands. The replayed values must equal trial 5's under `--vars`, or the save must be
  refused by name.

**R-ya16-2 — Pass and fail trials must be told apart (confirmed, rendered).**
- **The defect:**
  - In `TrialRenderer` (≈ 81–82), a pass is drawn in the trace colour at 35 % alpha and a fail in the limit colour.
  - In `RenderTheme` (≈ 57–60), the first trace colour is pure red (255,0,0) and the limit colour is (200,40,40).
- **Failure:**
  - Every family in the shipped `BandpassYield.yield.cdd` shows 432 passes and 68 fails as one red band.
  - Any first `plot --trace … colorby=pass` does the same.
  - A scatter is worse, because its passes are drawn at full opacity.
- **Fix:** `colorby=pass` paints passes in a fixed neutral colour, theme-aware for light and dark, and fails in the
  limit colour, whatever the trace colour is.
- **Gate:** a renderer-level assertion that the pass and fail paints differ by a clear margin, in both themes and for
  the first trace colour.

**R-ya16-3 — A result run at every corner is readable by every display, or refused by name (confirmed).**
- **The defect:** `yield corners --mc`, or mc/estimate with `statistics corners=`, stacks `<design>.yield.npy` under a
  `corner` axis. `yield.goal:<g>:spec` is then rank 2, and `SpecLineResolve.GoalsOf` (≈ line 49) requires rank 1.
- **Failure:**
  - `plot AmplifierCorners.yield.npy --trace cube=trials.goal:Gain:worst,stat=histogram --spec-lines` refuses with a
    false "records none".
  - Without the flag, the histogram silently becomes `histogram(trials.goal:Gain:worst[0, :], 9)`, the nominal corner
    only.
  - The yield display (`YieldDisplayPreset`) builds no families and no histograms.
  - The statistics table (`StatisticsTablePreset` ≈ line 41) is skipped.
  - `ResultContributions` fails its check that the z cube's first axis matches the trial count, so nothing is ranked.
- **Fix:**
  - `GoalsOf` reads the first slice of a stacked label: the spec is the same at every corner.
  - A histogram or statistic over a corner-stacked cube either takes `corner=<name>` (defaulting to every corner as
    separate traces) or is refused naming the corner axis. **Never** silently slice to index 0.
  - The presets and contributions, shown a corner-stacked result, refuse by name, or build per corner if that is
    cheap. Say which you chose.
- **Gate:** the plot command above produces one histogram per corner (or the refusal), and the yield display opened
  on that file either renders or says why.

**R-ya16-4 — A cancelled `yield doe --optimum` writes nothing (high, from reading).**
- **The defect:** `Yield.Doe.cs` ≈ 126–137 writes `.doe.npy`, prints "Wrote" and adds the file to the outputs before
  `ModelOptimum` runs. Cancelling during the optimum returns 130 and leaves the file.
- **Fix:** run the optimum before reporting the write, or delete the file on the 130 path.
- **Gate:** cancel during the optimum; the file is absent.

**R-ya16-5 — `--optimum` with `--factors stat` is refused before anything runs (confirmed).**
- **Failure:** `yield doe --factors stat --design pb --optimum` runs 13 simulations, writes the file, then exits 1.
- **Fix:** add the check to `DoeFlagProblem` (or immediately after `DoeRun.Create`).
- **Gate:** exit 1, zero simulations, no file.

**R-ya16-6 — `--generate` returns the definitions in JSON and MCP for a `.csch` too (confirmed).**
- **The defect:** `Yield.Corners.cs` ≈ 79–80 lists generated corners by name only, while the text output prints the
  full corner JSON and a `.cnl` gets full lines. An MCP caller cannot see a generated corner's temperature or values.
- **Fix:** include each generated definition in the JSON, in the same structure for `.csch` and `.cnl`.
- **Gate:** the JSON of a `.csch` `--generate` carries each corner's temperature and values.

**R-ya16-7 — No `yield` flag is silently ignored (confirmed, all exit 0 today).**
- **Refuse:**
  - on `doe`: `--confidence`, `--nonconverged`, `--save`, `--process`, `--mismatch`, `--sigma-scale` and `--analyses`;
    and `--resolution` together with `--design pb`;
  - on `corners` without `--mc`: `--target`, `--trials`, `--seed`, `--autostop` and `--contributions`;
  - `mc --trial n --corners X`. Either honour the corners or refuse;
  - `--contributions` on every corner path, unless it is implemented there.
- **Mechanism:** extend the existing `Yield.Flags` table rather than scattering the checks. `reference statistics`
  reads that table, so it must stay the one place that says which noun takes which flag.
- **Gate:** one table-driven test of every refused pair, plus a check that the table and `reference statistics`
  agree.

**R-ya16-8 — `plot` refuses trial and statistic options that do nothing (confirmed).**
- **Refuse:**
  - `envelope=` on `--type smith`, through the existing `TrialViews.EnvelopeRefusal`, which nothing calls;
  - `envelope=` and `colorby=` on a trace with no trial axis;
  - `fit=normal` with `stat=cdf`, unless the CDF can carry the fit line;
  - `percent=` and `over=` with `stat=yieldsens`.
- **Also:** the usage text's own family example `cube=SP1.S,i=2,j=1,y=db` (`PlotVerb.cs` ≈ 365) is refused as an
  ambiguous dB. Change it to `y=db20`, and grep the user page and `cli.md` for the same spelling.
- **Gate:** a table-driven test of each refusal, plus a test that the usage example runs.

**R-ya16-9 — A measurement family draws its nominal curve (confirmed).**
- **The defect:** `TrialResolve` (≈ line 169) looks up `nominal.<CubeName>`. For a measurement family the cube name
  is bare (`S21dB`, as `YieldDisplayPreset` ≈ 133 also spells it), but the nominal is stored as
  `nominal.measurements.S21dB`.
- **Failure:** a vector `measure S21dB = dB(SP1.S(2,1))` plotted with `curves=0` gives an empty plot. The same plot
  of `SP1.S` shows the nominal.
- **Fix:** qualify a bare measurement name with `measurements.` before adding the `nominal.` prefix, and check that
  this does not break a cube that genuinely is top-level.
- **Gate:** that plot shows the nominal curve.

**R-ya16-10 — Contribution shares displayed within 0–100 % (per D-a).**
- **Where:** `ContributionParetoPreset` ≈ 44–62, the Yield panel's share bars and tooltips
  (`YieldVariableRowViewModel.ShareText`, `YieldPanelViewModel.Run.cs` ≈ 582), and the CLI's text `--contributions`
  output.
- **Fix:** as D-a decides: clamp for display, renormalise the cumulative line, and keep the raw β·r in JSON and in
  the file.
- **Gate:** on the Bandpass example's PassbandSpec, every displayed share is within 0–100 % and the cumulative line
  never decreases.

**R-ya16-11 — `--vars` works on a design with a `correlate` line (confirmed).**
- **The defect:** `Yield.cs` ≈ 481–489 (`NarrowVariables`) turns off the other entries but keeps the correlations
  that name them.
- **Failure:** `yield mc BandpassYield.csch --vars C1.C` is refused with "correlate C1.C C3.C: C3.C is not a
  statistical entry".
- **Fix:** drop every correlation that touches an excluded entry. Keep the ones wholly inside `--vars`.
- **Gate:** `--vars C1.C` runs, and `--vars C1.C,C3.C` keeps ρ = 0.9 (check the drawn pair's sample correlation
  loosely).

**R-ya16-12 — A histogram's spec line is always visible (confirmed).**
- **The defect:** `StatisticsRenderer.DrawSpecLines` draws the limit, but the autoscale ignores it.
- **Failure:** in the shipped yield display, the StopHighSpec histogram (data around −31 dB, limit −22 dB) shows no
  spec line.
- **Fix:** widen the X autoscale to include every vertical spec limit, but only on an autoscaled axis. A user's fixed
  range stays as set, and an off-scale limit then gets an edge marker.
- **Gate:** that histogram's X range contains −22.

**R-ya16-13 — Per-trial goal values carry their unit (confirmed).**
- **The defect:** `StatisticalDataSet` ≈ 102–103 writes `goal:<g>:worst` and `:margin` with unit "".
- **Failure:**
  - The histogram's X axis reads a bare "bin".
  - A frequency goal shows a raw 2.4e9.
  - The yield-sensitivity companion trace reads "bin" where its main trace reads "bin (F)".
- **Fix:** write the goal expression's unit on both cubes. The margin carries the same unit.
- **Gate:** the cubes' units after a Bandpass run read `dB`, and a frequency goal's axis is scaled.

**R-ya16-14 — `yield trial` runs in the mode its design asks for (plausible; confirm first).**
- **The defect:** `Yield.cs` ≈ 227 runs `trial` in `StatisticalMode.Yield`. `StatisticalRun` (≈ 78) therefore refuses
  it on a Monte Carlo design with no `use=yield` goal, and it may score a different set of goals than
  `yield mc --trial n` does for the same trial.
- **Fix:** if confirmed, `trial` follows the same mode rule as `mc --trial`, so that the two agree trial for trial.
- **Gate:** `yield trial 5` and `yield mc --trial 5` report the same values and goals on a Monte Carlo design.

## 4. Gates (minimal; run only these classes)

- New `tests/Ui.Tests/Statistics/ReviewCliDisplayFixesTests.cs`: one test per requirement above, table-driven where
  it lists flags.
- Re-run the existing classes whose code you touched: `YieldCliTests`, `CornerTests`, `DoeTests`,
  `DisplayStatisticsTests`, `DisplayTrialsTests`, `YieldPanelTests`, `YieldExampleTests`, `CliStructuredOutputTests`,
  `ServeProtocolAdapterTests`. **Run targeted classes only**, never a whole project, and rebuild each test project
  before `--no-build`.
- Run the CLI against a **copy** of `examples/Yield` in a scratch directory, never the repo's own, so no `.npy` lands
  in `examples/`.

## 5. Out of scope

- The numeric-core findings. Those are YA-15.
- New plot types or new flags beyond what a refusal needs.
- DocGen: edit page sources only.
- **The shipped `BandpassYield.yield.cdd` is not edited by hand.** R-ya16-2 and R-ya16-12 fix it through the renderer.
  If the preset itself must change, regenerate the file from `YieldDisplayPreset` and say so. The example is unreleased
  (D-a), so there is no compatibility to keep. Update `examples/Yield/README.md` and `YieldExampleTests` if any number
  they quote moves.

## 6. Completion note

Add a "review fixes" entry to `src/Cli/RESOLVED.md` (and to `src/Render/RESOLVED.md` for the display items). Update
`docs/design/cli.md` §25 for every new refusal. Record D-a in `docs/design/yield.md`. Do not write to any
`CLAUDE.md`, and do not commit.
