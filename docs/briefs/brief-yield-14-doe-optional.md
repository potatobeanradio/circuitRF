# Brief YA-14 (OPTIONAL) — Design of experiments: screening, effects and a response model

**Series:** `brief-yield-0-overview.md` · **Tag:** `R-ya14-<m>` · **Status:** **optional** — not on the series'
critical path; build only when the owner asks for it.
**Depends on:** YA-2 (batch door), YA-4 (regression), YA-5 (the verb), YA-8/YA-9 (bars, Plot Versus, families),
YA-10 (the panel), YA-12 (the central-composite points and the quadratic fit, shared with the surrogate)
**Area:** `src/Engine/Statistics/` (design generators, effects, Lenth's PSE), `src/Design/Statistics/`
(`DoeRun`), `src/Core/Design/TuningSetup.cs` + the `.cnl`/`.csch` serializers (a `doe` line), `src/Cli/Yield.cs`,
`src/Cli/Serve/ToolCatalog.cs`, `src/Ui/Yield/`, `docs/design/yield.md`, `docs/user/src/reference/yield.md`

---

## 1. Why it is optional (owner, paraphrased, 2026-10-07)

Most of what design of experiments answers is already answered elsewhere in the series: which tolerances drive a spec
(YA-4 contributions), which design variables matter near the current point (TO-8 sensitivity), a response model to
search cheaply (YA-12's surrogate), and confirming a model's answer (YA-11's verification). What DOE adds is
**main-effect and interaction views over the designable ranges**, and **screening many variables in few runs** when
each simulation is slow. That is worth having, but not at the cost of the critical path.

## 2. Goal

Pick factors, run a structured design through the one evaluation service, and read which factors matter, how they
interact, and where a fitted model says the best point is — then confirm that point by real simulation.

## 3. Requirements

**R-ya14-1 — Factors and levels.** `factors=opt` (default): the `opt=1` entries, at the low and high ends of their
own ranges (tuning D4; centre = the range's midpoint on the entry's own scale, so a log-scaled entry's centre is
geometric). `factors=stat`: the `stat=1` entries at nominal ± k σ (`levels=sigma:<k>`, default 1), for a tolerance
study in a handful of runs instead of a Monte Carlo. Integer and discrete entries use their nearest allowed values
and the report says so. A complex value's parts are factors like any entry (tuning D18), and a mixed pair is refused
as in YA-1.

**R-ya14-2 — Designs.** `design=full2` (2-level full factorial; refused above 10 factors, naming the run count),
`frac` (2^(k−p) fractional factorial at `resolution=4|5`, generators from a published minimum-aberration table
embedded as data with its citation), `pb` (Plackett–Burman: 12, 20, 24 … runs, for screening many factors), `ccf`
(face-centred central composite — the same point construction YA-12's surrogate uses, factored out so both call one
function). Each design optionally adds centre points (`centre=<n>`, default 1) to detect curvature. Simulation is
deterministic, so there is **no replication and no randomized run order**; the design note says why in one sentence.

**R-ya14-3 — The `doe` line.** `doe [design=full2|frac|pb|ccf] [resolution=4|5] [factors=opt|stat]
[levels=range|sigma:<k>] [centre=<n>] [responses=goals|all] [parallel=<n>]` — YA-1's conventions: defaults omitted,
round-trip byte-stable through `.cnl` and `.csch`, a malformed line refused with a `cnl.doe.*` diagnostic, `check`
refusing what the run would refuse (no factors, too many for `full2`, a resolution the table does not hold for k).

**R-ya14-4 — Responses and effects.** Responses: each goal's `worst` value and margin (YA-4 R-ya4-4) and every scalar
`measure`; `responses=all` adds every goal regardless of `use`. For each response: main effects and two-factor
interactions (linear + 2FI regression on coded −1/+1 factors; quadratic terms too for `ccf`), with **Lenth's
pseudo-standard-error** margin to separate active effects from noise-sized ones (there is no pure error in a
deterministic simulation; the centre points give a curvature check instead). Fractional and Plackett–Burman designs
report their **alias structure** next to every effect they confound — an effect is never shown as if it were clean.

**R-ya14-5 — Results.** `<schematic>.doe.npy`: an outer `run` axis with the coded and actual factor values, every
response per run, an `effects` group (effect, coefficient, Lenth margin, active flag, alias set) per response, and the
fitted model's coefficients and R².

**R-ya14-6 — Plots** (YA-8/YA-9 machinery, nothing new to draw): **effects Pareto** (bars of |effect|, sorted, with
the Lenth margin as a line); **main-effects plot** (response mean at each level, one small line per factor);
**interaction plot** (response vs factor A, one line per level of factor B, for a chosen pair or the strongest
active pair). The panel's mode offers them as a one-click display, as YA-10 R-ya10-8 does for yield.

**R-ya14-7 — Using the model.** **Model optimum**: run the optimizer's algorithms (the registry, unit box) on the
fitted model against the goals — microseconds per evaluation — inside the factor ranges; then a **confirmation
run** by real simulation at that point, reported as predicted vs simulated for every goal. **Send to Tuning** loads
the point into the sliders; **Send to Optimizer** sets it as the start point. The model is never the answer — the
confirmation is (the YA-12 rule).

**R-ya14-8 — Headless.** `circuitRF yield doe <path>` (a noun on the existing verb rather than a new verb: it runs the
same service and shares its flags, output conventions and exit codes 0/1/2/130; there is no target, so no 3), the
`doe` keys as flags, `--json` with the effects tables and alias sets, `--optimum` to add R-ya14-7's model optimum and
confirmation. MCP `run analysis=doe`. `reference statistics` gains the DOE section with an example and the
one-line guidance: screening with `pb` first, then `ccf` on the few active factors.

**R-ya14-9 — Panel.** A **DOE** mode in the Yield panel: factors (the opt or stat rows, chosen by a toggle), design
and its run count shown before running, Run/Stop, the effects table per response with active effects marked, the
one-click DOE display, Model optimum → confirmation → Send to Tuning / Optimizer.

## 4. Gates (minimal; run only these classes)
- `DoeDesignTests` — `full2` for k = 3 is the 8 corners; a `frac` 2^(5−1) resolution-V design matches the published
  generator and reports no main-effect aliasing; `pb` 12 is the published matrix and is orthogonal.
- `DoeEffectsTests` — on a closed-form response y = 3a + 2b + ab (no simulation, a variables-only goal), the main
  effects and the interaction are recovered exactly and an inert factor falls under the Lenth margin.
- `DoeRunTests` — an RC low-pass with R, C and an inert resistor elsewhere: R and C are active, the inert one is not;
  the `doe` line round-trips byte-stable.
- `DoeModelOptimumTests` — on a `ccf` of a response that is exactly quadratic, the model optimum equals the analytic
  optimum and the confirmation run matches the prediction to rounding.
- `DoeCliVerbTests` — `yield doe` as a process prints the effects table and exits 0; `--json` carries the alias sets.
