# Brief TO-1 — The model and the file format: tunables, presets, goals, optimizer settings

**Series:** `brief-tuneopt-0-overview.md` (read it first; decisions D1–D18 are binding) · **Tag:** `R-to1-<m>`
**Depends on:** nothing · **Unblocks:** every other phase
**Area:** `src/Design/Schematic/SchematicModel.cs` (the `.csch` model), `src/Design/Schematic/NetExtractor.cs`,
`src/Core/Netlist/CnlReader.cs` / `CnlWriter.cs` / `AnalysisDirectiveSchema.cs`, `src/Core/Elaboration/`, a new
`src/Design/Optimization/`, `src/Cli/Check.cs`, `src/Cli/Explain*.cs`, `src/Cli/Reference.cs`,
`docs/design/tuning-optimization.md` (new), `docs/design/project-file-formats.md`

---

## 1. Goal

Everything later phases store or exchange exists as data, round-trips through `.csch` and `.cnl`, validates in
`check`, and can be applied to a design in memory. **No UI and no algorithm in this phase.**

## 2. Requirements

**R-to1-1 — The model.** Plain serializable classes in `src/Design/Optimization/` (no Avalonia):
- `TunableEntry` — key (D3), `Tune`, `Opt`, `Min`, `Max`, `Scale` (`Auto|Lin|Log`), `Step?`, `Discrete`
  (`None|Integer|Preferred`), and room for unknown keys (overview §5).
- `TuningPreset` — name, created (UTC), `IsLastTuned`, and an ordered key → value-text map. Values are stored as the
  **text the schematic would hold** (`47 pF`), so a pushed preset is byte-identical to a typed value.
- `OptimizationGoal` — name, expression, analysis name, optional axis range (axis name, lo, hi, each with unit),
  type (`Le|Ge|Eq|In|Out`), limit(s), optional sloped limits (limit at lo / at hi), weight, enabled.
- `OptimizerSettings` — algorithm id (a stable string, D13 order), an algorithm-options bag, max iterations, max
  evaluations, time limit, cost form (`LeastSquares|Minimax`), analysis scope (`GoalAnalyses|All`), seed, parallelism.
- `TuningSetup` — the four above together, which is what a schematic holds.

**R-to1-2 — `.csch`.** `SchematicModel` gains an optional `tuning` object holding a `TuningSetup`, following the
`.csch` serialization conventions (`project-file-formats.md`: System.Text.Json, enums as names, nullable defaults, omit
when empty). A schematic with no tuning setup writes **no** `tuning` key — existing files and their bytes are unchanged.
Whether this needs a `format_version` bump: decide by the file's own rule and record it.

**R-to1-3 — `.cnl` directives.** On the TestBench, beside `analysis` and `measure`:
```
tune R1.R          min=10 Ohm max=200 Ohm scale=log tune=1 opt=1
tune DUT:Wline     min=50 um max=400 um opt=1 discrete=none
preset "wide band" R1.R=47 Ohm DUT:Wline=212 um
goal G1 = dB(SP1.S(2,1)) analysis=SP1 over=freq:1 GHz..2 GHz ge -0.5 weight=1
goal Stab = mu(SP1.S) analysis=SP1 over=freq:0.1 GHz..10 GHz ge 1.05
goal Ripple = dB(SP1.S(2,1)) analysis=SP1 over=freq:1 GHz..2 GHz in -0.6 -0.2
optimize algorithm=lm maxiter=200 cost=lsq analyses=goals seed=1
```
The exact grammar is this phase's to settle (key spelling, quoting, unit placement, how sloped limits are spelled) —
**constraints:** it reads like the existing `analysis`/`measure` lines; `CnlWriter` → `CnlReader` → `CnlWriter` is
byte-stable; units are written the way `measure` writes them; a key the reader does not know is a **warning naming the
key**, not a skipped line in silence. Record the grammar in the design note and in `AnalysisDirectiveSchema` so the
reference topic (R-to1-8) is generated from it, not hand-copied.

**R-to1-4 — Extraction.** `NetExtractor` emits the directives from the `.csch`'s `tuning` block, and the `.csch` reader
for a `.cnl` → `.csch` conversion (`netlist --toSchematic`) carries them back. `CircuitSource.CnlTextOf` therefore
hands every run verb the tuning setup with no extra code.

**R-to1-5 — The tunable catalog.** `TunableCatalog.Discover(schematic, workspace)` returns every tunable of a
schematic per D1/D2: key, display location (`top` or `DUT · ×2`), the instance/variable and parameter it names, current
value text, unit, integer-ness, read-only-ness with a reason, and the D4 default range. It walks the hierarchy with the
resolver Simulate uses (no second resolver). **A sub-circuit instance's declared cell parameters are offered per
instance** (overview D2), whether the instance overrides them or inherits the default. An inherited one reports
`IsDefault = true` and the default as its current value, so Push knows to add an override rather than edit one. **It must not offer** anything D1 excludes; an expression-valued
parameter is simply absent. It reports keys in the setup that resolve to nothing (D3) as a separate list.

**R-to1-6 — Applying values in memory.** `TunableOverrides.Apply(design, IReadOnlyDictionary<key, value>)` produces
the design to elaborate with the given values substituted — top-level parameters, VAR rows, and sub-cell definitions
(D2: every instance of that cell). **Nothing is written to disk.** A key that does not resolve is returned as a note.
Elaborating the result must equal elaborating a design in which the same values had been typed (that is the gate).
Interaction with `--set`: `--set` applies first, then tuned values; a key naming a variable that `--set` also sets is
a refusal naming both.

**R-to1-7 — `check` and `explain`.** `check` validates the setup with the same model code: a range with min ≥ max, log
scale with min ≤ 0, a goal whose analysis is not declared, a goal range holding no grid point, an expression that does
not parse, a preset value whose key resolves to nothing (**warning**, D3), a tunable that D1 would not offer (error —
an expression-valued key in a hand-written `.cnl`). `explain --tunables` lists the catalog (R-to1-5) as text and in
`--json`.

**R-to1-8 — Reference topics.** `reference topic=tuning` and `reference topic=goals` print the directive grammar from
`AnalysisDirectiveSchema` with one worked example each. TO-11 extends them; this phase makes them exist.

**R-to1-9 — Design note.** Write `docs/design/tuning-optimization.md` from the overview (decisions, model, grammar,
identity rules) and add the `tuning` block to `project-file-formats.md`.

**R-to1-10 — Complex values (overview D18; amended 2026-10-07, built).**
- `ComplexPart { Real, Imag, Mag, Phase }` and `TunableKey.Part`: a key may be `real(K)`, `imag(K)`, `mag(K)` or
  `phase(K)`. The `tune` and `preset` grammar takes them unchanged (the tokenizer already keeps `( )` in one token).
- `ComplexValue.TryParse` is the one literal test: numbers, `j`, `+ - *`, or one `complex(a,b)` / `polar(m,deg)` of
  numbers, with an optional unit; `4+j*X` is refused. It returns the form (`Rect`/`Call`/`Polar`) so a value is
  written back as it was written.
- The catalog offers each complex literal as **four** part tunables (`Part`, `WholeKey`, `Whole`, `WholeUnit`,
  `Form` on `Tunable`), never the whole: `Find("ZL")` is null and `WhyNotOffered("ZL")` names the four part keys;
  `WhyNotOffered("real(R1.R)")` says the value is not complex. `PartsOf(K)` and `FindValue(K)` reach the whole.
  Part default ranges follow D18 (phase ±90°, linear; others D4 to six significant figures).
- `TunableOverrides.Apply` takes a whole complex value under `K`, and folds part keys into the whole with
  `ComplexValue.Compose`, written in the design's own form; an impossible combination is a note, not a throw.
- `check`: a whole complex key in a `tune` line is an error naming the parts; a phase range wider than 360° is an
  error; ranges of one value's parts that leave no complex value inside all of them are an error
  (`tuning.range.complex-disjoint`, decided exactly by `ComplexRegion.IsEmpty`). A preset value under `K` resolves.
- `explain --tunables` lists the parts, each flagged `part of <whole>` (JSON: `whole`). `reference tuning` documents
  the part keys and the worked example tunes `mag(Zsrc)`/`phase(Zsrc)` and presets `Zsrc` whole.

## 3. Not in this phase
UI, live runs, algorithms, measurement functions (`mu` in the example above lands in TO-9 — the parser must accept an
unknown function name in a goal expression the way it does in `measure`, and `check` reports it the same way).

## 4. Gates (minimal tests, run only these classes)
- `TuningSetupRoundTripTests` — `.csch` → `.cnl` → `.csch` → `.cnl` byte-stable for a setup using every field once;
  a schematic without a setup writes identical bytes before and after this change.
- `TunableCatalogTests` — offers a literal R, a VAR literal, an integer parameter, a sub-cell parameter (with ×2
  instance count), an instance's overridden cell parameter, and an instance's **inherited** cell parameter
  (`IsDefault`, at the cell default), each instance separately; does **not** offer an expression parameter, a string, or a cell-parameter
  reference; flags a read-only cell's parameter.
- `TunableOverridesTests` — elaborated netlist with overrides == elaborated netlist of the hand-edited design (top,
  VAR, sub-cell); an unresolved key is a note, not a throw.
- `TuningCheckTests` — one test per `check` rule in R-to1-7.
- `ComplexTunableTests` — the three literal forms read and write back in their own form; `4+j*X` and a plain number
  are not literals; the catalog offers four parts and not the whole; region emptiness and the stop-at-the-edge move;
  a mixed-pair compose and an impossible one; overrides by whole value and by part key keep the design's form.
- `TuningCheckTests` gains one case each: a whole complex key, a phase span over 360°, disjoint part ranges.
