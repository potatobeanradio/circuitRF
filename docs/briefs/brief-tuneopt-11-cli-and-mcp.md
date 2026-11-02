# Brief TO-11 — The `opt` verb and the MCP optimizer

**Series:** `brief-tuneopt-0-overview.md` (D14, D15) · **Tag:** `R-to11-<m>` · **Depends on:** TO-6, TO-9
(TO-7/TO-8 add algorithms without touching this phase's code)
**Area:** `src/Cli/` (a new `Optimize.cs`, `CliEntry.cs` verb switch, `Reference.cs`, `DocumentSchema.cs`),
`src/Cli/Serve/ToolCatalog.cs` and `McpServer.cs` (progress tokens and cancellation are already there),
`docs/design/cli.md` (new section + §7 exit code 3)

---

## 1. Goal

An agent with nothing but the MCP tools can discover how to write goals, write them, run the optimizer, follow its
progress, and read the answer — and get the result the GUI would get from the same file.

## 2. Requirements

**R-to11-1 — The verb.** `circuitRF opt <path.csch|path.cnl>` with the five-step anatomy of a run verb
(`cli.md`). Flags: `--algorithm <id>`, `--max-iter`, `--max-evals`, `--time <s>`, `--cost lsq|minimax`,
`--analyses goals|all`, `--parallel n`, `--seed n`, `--set var=expr` (as every run verb), `--vars key,key` / `--goals
name,name` to narrow to a subset of the file's enabled ones, `--snap` (TO-8 snap and polish at the end), `-o out.npy`
(the best point's full results plus the `opt` history group), `--history out.npy` (history only), `--json`,
`--save-preset <name>` (D14; `.csch` only — a `.cnl` input is a refusal saying to add a `preset` line). **Flags override
the file's `optimize` settings for this run only.** Nothing is written to the design's values.

**R-to11-2 — Output.** stdout (text): finish reason; best cost; a table of variables (key, start, best, min, max,
railed); a table of goals (name, met, worst value, where on the axis, margin). `--json`: the same, plus the per-iteration
best-cost trajectory and the sensitivity when computed. stderr: per-iteration progress lines (suppressible with `-q`).
Values are printed as the value text a schematic would hold.

**R-to11-3 — Exit codes (D15).** 0 all goals met · 3 finished with goals unmet (add to `cli.md` §7 with the reason:
a script must be able to tell "ran and failed to meet spec" from "could not run") · 1 refusal · 2 no evaluation
converged · 130 cancelled, writing nothing.

**R-to11-4 — Parity.** The verb runs the same `src/Design/Optimization` code as the panel. Gate: on the TO-12
example, the verb's best values and cost equal the panel's headless view model's run with the same seed, exactly.

**R-to11-5 — MCP `run`.** `run analysis=optimize path=… [algorithm, maxIter, seed, …, savePreset]` — the same
options as the verb, the same result object as `--json`. Progress notifications per iteration (best cost, goals met
n/m) through the existing progress token; cancellation through the existing path. Long runs must not hold the
server's write lock between notifications (`JsonRpc.cs` serializes writes — check the progress path stays
non-blocking).

**R-to11-6 — Discoverability.** Extend TO-1's reference topics:
- `reference topic=goals` — the `goal` grammar; each type with an example; the axis-range form; sloped limits; the
  normalization rule (TO-6 R-to6-3) in two sentences; the goal-function list (TO-9) with one example each (S in dB,
  phase, μ, group delay, HB efficiency, a WSProbe metric).
- `reference topic=optimizers` — generated from the algorithm registry (R-to7-7): id, label, use-when, options with
  defaults, accepted cost forms.
- `reference topic=tuning` — the `tune` and `preset` directives, the key spelling (D3), discrete options.
- The MCP server instructions gain a five-line "optimize" walk-through beside the existing end-to-end example: find
  tunables with `explain --tunables`, write `tune` + `goal` + `optimize` lines, `check`, `run analysis=optimize`,
  `read` the result.

**R-to11-7 — `check` and `explain`.** `check` refuses an `optimize` setup that would refuse at run time (no opt
variables, no enabled goals, an algorithm id that does not exist, a cost form the algorithm rejects) with the same
sentences. `explain --analysis` reports which analyses an optimization would run under `goals` vs `all`.

**R-to11-8 — Complex values (overview D18; amended 2026-10-07).**
- `--vars` takes part keys (`--vars "mag(ZL),phase(ZL)"`); a whole complex key there is a refusal naming the four
  parts. The variables table prints one row per opt-enabled part (start, best, min, max, railed) and the whole value
  it composes to (`ZL  40+15j Ohm → 33.1+21.4j Ohm`); `--json` adds `whole` beside each part, as `explain --tunables`
  does. `--save-preset` stores the whole value under its own key.
- `check` refuses (R-to11-7) more than two opt-enabled parts of one value, with TO-6's sentence.
- `reference topic=tuning` already documents the part keys and the always-held ranges (TO-1 R-to1-10); the MCP
  "optimize" walk-through names one complex example line (`tune mag(ZL) … opt=1`). Infeasible points are a field
  of the progress notification and the result.

## 3. Gates (minimal; run only these classes)
- `OptCliVerbTests` — the verb as a process on the L-section `.cnl`: exit 0 and values within tolerance; the
  infeasible goal exits 3 and names the goal; `--save-preset` on a `.csch` adds exactly one preset and changes nothing
  else (byte comparison of the rest).
- `OptParityTests` — verb vs headless panel view model, same seed, identical best values.
- `OptMcpTests` — `run analysis=optimize` returns the `--json` object; progress notifications arrive; cancel stops it.
- `OptReferenceTests` — the three topics are generated from the schema/registry (a new algorithm appears without
  editing the topic text).
- `OptCliVerbTests` gains: a `mag`/`phase` optimization of a complex load prints the whole best value; `--vars ZL`
  refuses naming the parts.
