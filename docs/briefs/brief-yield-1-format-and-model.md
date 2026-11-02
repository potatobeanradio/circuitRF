# Brief YA-1 — The model and the file format: tolerances, correlation, statistics settings, corners, goal use

**Series:** `brief-yield-0-overview.md` (read it first; decisions D1–D14 are binding) · **Tag:** `R-ya1-<m>`
**Depends on:** nothing (the tuning series is complete) · **Unblocks:** every other phase
**Area:** `src/Core/Design/TuningSetup.cs` (`TunableEntry`, `OptimizationGoal`, `TuningSetup`),
`src/Core/Netlist/CnlReader.cs` / `CnlWriter.cs` / `AnalysisDirectiveSchema.cs` (`TuningDirectives`),
`src/Design/Schematic/SchematicPersistence.cs`, `src/Design/Schematic/NetExtractor.cs`,
`src/Design/Optimization/TuningValidator.cs`, a new `src/Design/Statistics/`, `src/Cli/Check.cs`,
`src/Cli/ExplainTunables.cs`, `src/Cli/ReferenceTuning.cs` (or a new `ReferenceStatistics.cs`),
`docs/design/yield.md` (new), `docs/design/project-file-formats.md`

---

## 1. Goal

Everything later phases store or exchange exists as data, round-trips through `.csch` and `.cnl`, validates in
`check`, and is explained by `explain` and `reference`. **No sampling, no runs, no UI in this phase.** An agent can
already write a complete, `check`-clean statistical setup at the end of it.

## 2. Requirements

**R-ya1-1 — The model.** Extend, don't fork (D1):
- `TunableEntry` gains `Stat` (bool), `Distribution` (`None|Gauss|Unif|LogNorm|Discrete`), and a spread record
  holding whichever of `sd`, `tol`, `sigmas`, `lo`, `hi`, `by`, `trunc` were written, each with its **form**
  (percent or absolute-with-unit) so the writer reproduces it. The keys previously parked in `Extra` by a
  hand-written file move to the typed fields; `Extra` keeps working for anything else.
- `OptimizationGoal` gains `Use` (`Opt|Yield|Both`, default `Both`).
- `StatisticsSettings` — `trials`, `seed` (default 1), `sampling` (`Random|Lhs|Sobol`), `target` (percent, optional),
  `confidence` (default 95 %), `autostop`, `nonconverged` (`Fail|Warn`, default `Fail`), `save`
  (`Auto|Scalars|All|<n>`), `process`, `mismatch`, `sigmascale`, `parallel`, `analyses` (`Goals|All`), and a
  corner scope (`none|all|<names>`). Defaults are the overview's; a default is never written.
- `Correlation` — two keys and ρ.
- `CornerDefinition` — name, enabled, kit axis selections (`.csch` only, keyed as `WorkspaceCornerAxis.Key` is),
  `temp`, global VAR values, tunable values, or a **statistical** origin (`trial`, plus the seed/sampling/trials that
  identify it — D10).
- `TuningSetup` holds them all. There is **one** block, not a second "statistics" block beside `tuning`: the
  variables are the same entries.

**R-ya1-2 — `.cnl` directives.** On the TestBench, extending `AnalysisDirectiveSchema.TuningDirectives`:
```
tune R1.R   min=10 Ohm max=200 Ohm opt=1 dist=gauss sd=2%
tune C1.C   dist=unif tol=0.1 pF
tune L1.L   dist=gauss tol=5% sigmas=3 trunc=3
tune mag(ZL) dist=unif lo=45 Ohm hi=55 Ohm stat=0
tune X1.Nf  dist=discrete lo=4 hi=8 by=2
correlate R1.R R2.R rho=0.9
statistics trials=500 seed=7 sampling=lhs target=95% confidence=95% nonconverged=fail save=auto
goal S21 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge 14 use=yield
corner SS_hot temp=85 Vdd=3.0 V R1.R=47 Ohm
corner Worst_S21 trial=417 seed=7 sampling=lhs trials=500
```
The spelling rules are the tuning grammar's (`tuning-optimization.md` §4): units after a space, unknown keys a
warning and kept, a malformed line refused with a `cnl.statistics.*` / `cnl.corner.*` diagnostic and its line number,
booleans `1/0/true/false/yes/no`, defaults omitted. `%` attaches to its number (`2%`). **Byte stability** as for the
tuning lines: `CnlWriter → CnlReader → CnlWriter` and `.csch → .cnl → .csch → .cnl` reproduce every line exactly.

**R-ya1-3 — `.csch`.** The existing `tuning` object carries the new fields with the `.csch` conventions
(`project-file-formats.md`): a schematic with no statistical content writes **identical bytes** to today. A
`.csch` corner records kit axis selections by axis key and section; `NetExtractor` resolves them through
`WorkspaceCorners.BindingsFor` — the function Simulate already uses for `CornerSelections` — and writes the
**resolved bindings** into the `.cnl` corner line (D10). A selection that no longer resolves is reported exactly as a
stale `CornerSelections` entry is. Decide whether a `format_version` bump is needed by the file's own rule and record it.

**R-ya1-4 — `check`.** Errors (`yield.*` codes, through `TuningValidator`):
a distribution on an entry that is not tunable; a missing or extra spread key for the distribution (`gauss` with
`lo`); `sd`/`tol` ≤ 0; `lo ≥ hi`; `by` ≤ 0 or not dividing into the range to within rounding; `lognorm` on a value
that is not positive; `gauss`/`lognorm` on an integer entry; tolerances on a mixed pair of complex parts, or on more
than two parts of one value; `correlate` naming a non-stat entry, the same key twice, or ρ outside (−1, 1);
`sampling=lhs` with `autostop=1`; `autostop=1` with no `target`; a corner naming an unknown tunable key or
binding `temp` to a non-number; `trial=` without its seed/sampling/trials. Warnings: a distribution with a
non-physical draw probability above 1e-9 (D2 — the sentence names `trunc=` and `lognorm`); a `stat=0`
distribution; a `use=yield` goal while no entry has `stat=1` and no kit statistics are selected; a correlation
matrix that will need repair (report the largest change — the repair itself is YA-2's, called here). **The same
functions run at run time**, so a setup `check` passes never refuses later for a reason `check` could have seen.

**R-ya1-5 — `explain`.** `explain --tunables` (existing) shows each entry's statistical part in words and numbers
(`R1.R  50 Ohm  gauss σ = 1 Ohm (2 %)`, JSON: a `stat` object with the spread in **base SI and the written form**).
`explain --analysis` reports the statistics settings, the yield goals (by `use`), the corners, the effective
correlation matrix and — useful to an agent before it spends a run — the **expected half-width of the yield
interval** at the configured trial count for a yield of 90 %, and the trial count that would bring it under ±2 %.

**R-ya1-6 — `reference statistics`.** A new topic, generated from the schema (as `reference tuning` is): every key
of the four directives with its default; each distribution with one example line and what its spread means; the
percent-vs-absolute rule; truncation; correlation; corners and statistical corners; goal `use=`; how pass/fail is
decided (D4) and how yield and its interval are reported (D7, D8). `reference goals` gains `use=`.

**R-ya1-7 — The design note.** `docs/design/yield.md`: this phase's model and grammar as built, then a section per
later phase, appended as each lands (the tuning series' pattern). Link it from `tuning-optimization.md` §19, and
mark each §19 seam as taken by the phase that takes it.

## 3. Not in this phase
Sampling, runs, kit distribution functions (YA-3 — a hand-written `agauss(...)` in a VAR still reads as today),
UI, the CLI run verb.

## 4. Gates (minimal tests, run only these classes)
- `StatisticsSetupRoundTripTests` — `.csch` → `.cnl` → `.csch` → `.cnl` byte-stable for a setup that uses every
  distribution form, a correlation, the settings line, a value corner and a statistical corner, and a `use=yield`
  goal; a schematic with no statistical content writes identical bytes before and after this change.
- `StatisticsCheckTests` — one case per error and warning in R-ya1-4.
- `CornerExtractionTests` — a `.csch` corner with a kit axis selection extracts to resolved bindings identical to
  what `CornerSelections` produces for the same selection; a stale selection is reported, not dropped.
- `StatisticsExplainTests` — the expected-interval figure for N = 500 at 90 % equals the Clopper–Pearson half-width
  computed independently in the test.
