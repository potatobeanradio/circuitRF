# Brief YA-5 — The `yield` verb and MCP: an agent sets up and runs Monte Carlo and yield on its own

**Series:** `brief-yield-0-overview.md` (D12, D13) · **Tag:** `R-ya5-<m>` · **Depends on:** YA-4
(YA-6 and YA-11 add nouns to this verb without changing its shape)
**Area:** `src/Cli/` (a new `Yield.cs`, `CliEntry.cs`'s verb switch, `ReferenceStatistics.cs` from YA-1,
`DocumentSchema.cs`, `ReadBack.cs`), `src/Cli/Serve/ToolCatalog.cs` and `McpServer.cs` (progress tokens and
cancellation exist), `docs/design/cli.md` (new section; §7 already has exit 3 from `opt`),
`docs/user/src/reference/cli.md`, `docs/user/src/reference/ai-agents.md`

---

## 1. Goal

An agent with nothing but the MCP tools can discover how to write tolerances and yield specs, write them, `check`
them, run a Monte Carlo or yield analysis, follow its progress, read the answer, and re-run a single trial — and get
exactly the result the GUI would get from the same file. This phase lands **before** any UI on purpose.

## 2. Requirements

**R-ya5-1 — The verb.** ONE verb with nouns (the `new`/`history` rule): `circuitRF yield mc|estimate|trial <path>`,
`<path>` a `.csch` or `.cnl`, with the five-step anatomy of a run verb (`cli.md`). YA-6 adds `corners`, YA-11
`center`. Flags override the file's `statistics` line for this run only: `--trials n`, `--seed n`,
`--sampling random|lhs|sobol`, `--target p%`, `--confidence p%`, `--autostop`, `--nonconverged fail|warn`,
`--save scalars|all|<n>|auto`, `--process 0|1`, `--mismatch 0|1`, `--sigma-scale k`, `--parallel n`,
`--analyses goals|all`, `--set var=expr` (every run verb's), `--vars k,k` / `--goals g,g` to narrow to a subset of
the enabled ones, `-o out.npy` (the DataSet; default is `<schematic>.yield.npy`, D9), `--json`, `-q`.
`yield trial <path> --trial n [-o out.npy]` re-runs exactly one trial (YA-4 R-ya4-7) and prints its sample values.
`--save-preset <name> --trial n` and `--save-corner <name> --trial n` are the opt-in writes (D12; `.csch` only,
after a history checkpoint as `opt --save-preset` takes one).

**R-ya5-2 — Output.** stdout (text): the mode and settings line (seed, sampling, trials run / cap, stop reason); the
yield with its interval and the target verdict; a per-goal table (yield, interval, worst margin, which trial); the
did-not-evaluate count with reasons; a statistics table per goal margin and per `measure` scalar (mean, σ, min,
max, median, Cpk where a goal gives limits); the worst trials (YA-4 R-ya4-10); the kit statistics in use (process /
mismatch counts, sections). `--json`: the same as one document, plus contributions when `--contributions` is given
(never computed unasked). stderr: a progress line per batch (suppressed by `-q`). Every number with its unit; a
yield as a percent with one decimal.

**R-ya5-3 — Exit codes (D13).** 0 · 3 yield below `--target` · 1 refusal · 2 no trial evaluated · 130 cancelled,
writing nothing. `mc` has no target and exits 0 unless it could not run.

**R-ya5-4 — Parity.** The verb runs `src/Design/Statistics` and nothing else. Gate: the verb's DataSet is
**byte-identical** to `StatisticalRun` run in-process with the same file and seed (the `opt`/`em` parity pattern).

**R-ya5-5 — MCP `run`.** `run analysis=montecarlo|yield` with the verb's options as fields (`trials`, `seed`,
`sampling`, `target`, `autostop`, `nonconverged`, `save`, `output`, `contributions`, `trial` for a single-trial
re-run, `savePreset`, `saveCorner`), returning the `--json` object. A progress notification per batch (trials,
yield, interval, did-not-evaluate) through the existing token; cancellation through the existing path; the writer
lock is not held between notifications (`JsonRpc.cs` serializes writes — check the progress path stays
non-blocking, as TO-11 did).

**R-ya5-6 — `read` and `plot` on a yield result.** `read path=<x>.yield.npy` prints the `yield` summary group first,
then the cubes as today, and `only=`/`at=` work on the `trial` axis (`at=trial:417`). `plot` accepts the YA-4
statistics functions in a trace expression (the drawing styles they want arrive in YA-8; until then a histogram plots
as a line over its `bin` axis — acceptable, and YA-8's gate changes it).

**R-ya5-7 — Discoverability.** `reference statistics` (YA-1) gains the run options, the result layout (D9 names), the
exit codes, and one worked example per mode. The MCP server instructions gain a **short "yield" walk-through** beside
the existing end-to-end and "optimize" ones:
`explain --tunables` → write `tune … dist=gauss sd=2%` lines and a `goal … use=yield` → `check` → `explain
--analysis` (the expected interval width) → `run analysis=yield trials=500` → `read` the summary → `run
analysis=yield trial=<worst>`. The `ai-agents.md` user page gets the same walk-through.

**R-ya5-8 — `check` and `explain`.** `check` refuses a setup the run would refuse (no stat entries and no kit
statistics; a `yield` run with no `use=yield|both` goal) in the run's own words — `check` asks
`StatisticalRun.Create`, as it asks `OptimizationRun.Create` today. `explain --analysis` reports which analyses a
yield run would execute under `goals` vs `all`, and the trial cost estimate (nominal evaluation time × trials ÷
parallelism, labelled an estimate).

## 3. Gates (minimal; run only these classes)
- `YieldCliVerbTests` — the verb as a process on a divider `.cnl`: `estimate` exits 0 at a reachable target and 3 at an
  unreachable one, naming the goal; `mc` with no goals exits 0; `trial --trial 7` prints trial 7's values equal to
  the full run's; `--save-corner` on a `.csch` adds exactly one `corner` line and changes no other byte.
- `YieldParityTests` — R-ya5-4.
- `YieldMcpTests` — `run analysis=yield` returns the `--json` object; progress notifications arrive; cancel stops it
  and writes nothing.
- `YieldReferenceTests` — the topic is generated from the schema (a new key appears without editing topic text).
- `YieldAgentWalkthroughTests` — the walk-through's five MCP calls, in order, on a fresh workspace, end with a yield
  and no error (the agent-usability harness's shape, MCP only).
