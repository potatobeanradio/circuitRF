# Brief YA-3 — Kit statistics: distribution functions live in the expression engine; process and mismatch

**Series:** `brief-yield-0-overview.md` (D5, D6) · **Tag:** `R-ya3-<m>` · **Depends on:** YA-2
**Area:** `src/Core/Netlist/Spice/SpiceExpression.cs` (`ReplaceStatistical`, `Statistical`),
`SpiceNetlistResult.Statistics` / `SpiceStatisticalUse`, `src/Core/Expressions/Evaluator.cs` (function dispatch) and
`Scope.cs`, `src/Core/Elaboration/Elaborator.cs`, `src/Core/Pdk/PdkCorners.cs`, `src/Ui/Schematic/WorkspaceCorners.cs`,
`src/Design/Schematic/NetExtractor.cs` (its SPICE read at ~line 746), `src/Design/Schematic/SpiceCellImport.cs`,
`src/Design/Statistics/`, `docs/design/spice-models.md` §8.4 and §8.11, `docs/design/expressions.md`,
`src/Core/CLAUDE.md` is NOT edited — findings go to `src/Core/RESOLVED.md`

---

## 1. Goal

A kit's statistical models — the variants its corner files point to from their statistical and mismatch sections —
vary in a Monte Carlo trial exactly as the kit intends, and every nominal result in the product stays
**byte-identical** to today.

## 2. Background (read before designing)

- The reader already reads one `.lib` section at a time (`PdkCorners.BindingsFor`), and a kit's later sections
  point at the statistical and mismatch variants of the same model library (`PdkImporter.CanonicalLibraries`). A
  user selects one in the existing corner picker (`AnalysesListViewModel`, `SetCornerSelectionCommand`).
- What breaks Monte Carlo today is one line of behaviour: `SpiceExpression.ReplaceStatistical` rewrites
  `agauss(nom, dev, k)`, `gauss(nom, rel, k)`, `aunif(nom, dev)`, `unif(nom, rel)` and two-argument `limit(nom, dev)`
  to `nom`, recording a `SpiceStatisticalUse(Function, Nominal)` — **the spread is discarded**. The three-argument
  `limit` is a clamp and is already rewritten to `min(max(…))` by arity (`spice-models.md` §8.4); leave that alone.

## 3. Requirements

**R-ya3-1 — The functions.** circuitRF's expression engine gains `agauss`, `gauss`, `aunif`, `unif` and the
two-argument `limit`, with the dialect's argument meaning (absolute deviation at k σ; relative deviation at k σ;
absolute half-width; relative half-width; nominal ± deviation chosen with equal probability). Each has a **nominal
evaluation** (returns its first argument, with that argument's kind and unit) and a **trial evaluation** (YA-2's
inverse CDF on the stream's z). Which one runs is a property of the evaluation context the elaborator passes down —
never a global. Document them in `expressions.md`; `reference functions` lists them.

**R-ya3-2 — Stop discarding the spread.** `ReplaceStatistical` rewrites to circuitRF's spelling of the same call
instead of to the nominal; `SpiceStatisticalUse` still records each one (it is the "this design has kit statistics"
signal the run report and `explain` use). Find **every path that persists translated text** — the extraction's
`.cnl`, `SpiceCellImport`, the kit importer's cell writes — and decide per path whether the live call or the
nominal is written. The rule: a file that only circuitRF regenerates (`netlist.cnl`) may carry the call; a file a
user keeps must still open in a version of circuitRF that predates this phase, or its format version must say why
it does not. Record the decision per path in `spice-models.md`.

**R-ya3-3 — Process vs mismatch (D6).** A draw in an expression evaluated in the **testbench (global) scope** is
**process**: stream = the variable's name, one draw per trial. A draw in an expression evaluated in a **cell scope**
(a subcircuit parameter default or body, once per instance) is **mismatch**: stream = the instance's full path +
the parameter name (+ an ordinal when one expression holds several calls), one draw per instance per trial. The
elaborator's top-down, per-instance evaluation already gives each instance its own scope; the rule is a property
of the scope kind, not of the function name. `statistics process=0` evaluates process draws nominally; `mismatch=0`
the mismatch ones; `sigmascale` multiplies every spread (YA-2 R-ya2-5).

**R-ya3-4 — The same functions in a design.** A VAR whose expression calls one (`Rsh = agauss(50, 2.5, 1)`) is a
process draw; one inside a cell's VAR or parameter default is a mismatch draw. A VAR holding a distribution call is an
expression and is therefore **not tunable** (tuning D1) — `explain --tunables` says why when asked about it. Its
nominal is what Simulate, Tuning and the optimizer see.

**R-ya3-5 — Discovery and reporting.** A design whose elaborated netlist holds kit or VAR statistical calls reports
them grouped as process/mismatch with counts and the sections that brought them, in `explain --analysis` and in the
run report. A Monte Carlo run with `use=yield` goals on a design with **no** stat entries and **no** statistical
calls is a refusal: nothing would vary. A design whose kit has a statistical section that is **not** selected gets an
Info note naming the section and the corner axis it belongs to — the user is one click away from it.

**R-ya3-6 — Nominal byte-identity.** For every example workspace and every kit fixture available on the machine,
Simulate's `run.npy` and the extracted `netlist.cnl` (modulo R-ya3-2's decided spelling change) are identical before
and after this phase. The kit-fixture half is a `FixtureFact` (skipped with a reason on a fresh clone, as
`RfCore.Tests`' proprietary fixtures are) — never commit kit data.

## 4. Not in this phase
The run loop (YA-4). The other simulator dialect's `statistics { process {…} mismatch {…} }` blocks (owner: later,
if a kit needs it — leave a named seam in `spice-models.md`).

## 5. Gates (minimal tests, run only these classes)
- `StatisticalFunctionTests` — nominal evaluation of each function returns its first argument with its unit; trial
  evaluation of `agauss(1, 0.3, 3)` over a fixed-seed 20,000 draws has σ = 0.1 within 4 SE; `limit(5, 1)` draws only
  4 and 6, with equal frequency within 4 SE; three-argument `limit` is still a clamp.
- `ProcessMismatchScopeTests` — a two-instance subcircuit whose parameter default calls `agauss`: both instances
  share a global draw and differ in their mismatch draw; `mismatch=0` makes them equal; `process=0` makes the
  global nominal.
- `KitStatisticsNominalIdentityTests` — R-ya3-6 on the examples (routine) and a kit fixture (`FixtureFact`).
- `KitStatisticsDiscoveryTests` — the refusal and the Info note of R-ya3-5.
