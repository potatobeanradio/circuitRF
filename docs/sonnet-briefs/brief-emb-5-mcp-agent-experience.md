# Brief EMB-5 — The MCP server under long work: jobs, estimates first, and nothing blocks the rest

**Series:** EMB-0 … EMB-5 · **Tag:** `R-emb5-<n>`
**Area:** `src/Cli/Serve/McpServer.cs` (`Enqueue`, `WorkerLoop`, `Cancel`), `src/Cli/Serve/ToolCatalog.cs`,
`src/Cli/Serve/PathRoot.cs`, `src/Cli/CliDiagnostics.cs` (`serve.path.outside-root`), `src/Cli/RunHost.cs`,
`docs/design/cli.md`
**Depends on:** EMB-4 (the estimates R-emb5-2 surfaces).

---

## 0. The gap

An agent driving board EM through the MCP server today:

- **One worker thread serves every tool** (`McpServer` starts one `circuitrf-serve` thread running
  `WorkerLoop`). An EM `check` that took 16+ minutes blocked every later call, including a sub-second
  `check` of a `.cnl`. The agent fell back to the shell for the rest of the session.
- **Cancelling did not stop the work.** `notifications/cancelled` cancels the call's token, but `check`
  passed no `RunControl` to `EmRunService.Preflight`, so the worker kept meshing. (Fixed as F2, uncommitted;
  this brief makes cancellation a property every long verb is tested for.)
- **No estimate before committing.** `explain` already computes the planar factor time per point and
  Palace's element count and memory, but nothing puts them in front of the agent at the moment it decides
  to call `run`. Twice an agent committed to a run whose real cost was 4× or more the number it could have
  read.
- **The `create` tool documented three views; the CLI takes four** (`3d`). Fixed as F4, uncommitted.
- **Paths outside the server root are refused** (`serve.path.outside-root`) — correct — but the refusal does
  not say where the agent can write.

## 1. Requirements

**R-emb5-1 — Read-only verbs run concurrently; long verbs run as jobs.** Tools that write nothing
(`find`, `check`, `explain`, `read`, `reference`, `solver`, `render`, `plot`) run on a small pool, not behind
the single worker. `run` (and `check` of an EM setup) may be started as a **job**: `run … async=true` returns
at once with a job id; `job action=status|cancel|result id=…` reports progress (stage, percent, elapsed,
estimate remaining), cancels, and returns the same result document the synchronous call would. Without
`async`, behaviour is unchanged. **Why:** an agent can keep reading and checking while a 20-minute EM run
proceeds, and can stop it.

**R-emb5-2 — An estimate before the run, from the same functions `explain` uses.** `run analysis=em
dryRun=true` (and `circuitrf em --dry-run`) returns, without meshing: the kernel, unknowns, memory, time per
point and total (planar: `SurfaceMesher.EstimatedFactorTime`; Palace: `EstimatePalace` after EMB-4's
correction), the verdict against this machine's memory, and every refusal `check` would raise. A real run's
first progress notification repeats the estimate, so a client that did not ask still sees it. Each estimate
carries a confidence note when the geometry is imported (EMB-4 R-emb4-1).

**R-emb5-3 — Cancellation is tested per verb.** Every verb the server runs that can take more than a few
seconds (`check` of a `.cem`/`.c3d`, `explain` of a 3D setup, every `run` analysis) observes
`RunHost.Control`; a test cancels each mid-stage and asserts the worker is free within one stage boundary.

**R-emb5-4 — Stage-level progress names what is slow.** Progress notifications carry the stage name and its
own size (*"Gmsh: high-order optimisation, 330,000 nodes"*, *"near-field factor, 41 % of 23,734 columns"*),
from EMB-2 R-emb2-6 and EMB-4 R-emb4-5, so an agent can cancel and change approach rather than wait.

**R-emb5-5 — The root refusal says where to write.** `serve.path.outside-root` adds the server root and,
when one exists, a writable scratch folder under it the server will accept (e.g. `<root>/.circuitrf-scratch`
created on demand), so an agent with its own temp directory knows where to put a render or a result.

**R-emb5-6 — `create` and the CLI agree, held by a test.** A test compares each MCP tool option's documented
value list with the CLI parser's accepted values for the same flag (views, technologies, analysis kinds), so
F4's kind of drift fails a build.

**R-emb5-7 — A duration is read the way its own refusal describes it.** `run analysis=optimize time=10min`
is refused with "expects seconds, or a number and s, ms, min or h, got '10min'" — the spelling the sentence
offers. `src/Cli/Optimize.cs` (~line 125) turns a bare number into "N s" and passes anything else to
`TuningValidator.TimeLimitSeconds`, which evidently wants a space before the unit. Accept `10min`, `10 min`,
`90s`, `1.5h` alike everywhere a wall-clock limit is taken (the optimize line's `timelimit=`, the CLI's
`--time`, the MCP `time` argument), through one parser, and quote an accepted example in the refusal.

**R-emb5-8 — Every long result has a small form.** `result=summary` is refused for `optimize`
("'result' does not apply to optimize", `serve.args.not-for-mode`), so an agent asking a fit for its best
values receives every iteration's detail. Give `optimize`, `yield`, `montecarlo`, `corners`, `center` and `doe`
the same `summary` the S-parameter runs have: the outcome, the best values as text to write back, each goal's
margin and where it binds — and nothing per iteration unless `showIterations` asks.

**R-emb5-9 — A refusal never floods the caller.** `recognize` on a picture it could not calibrate (an
instrument screenshot whose grid carries no per-line labels, only start and stop beneath it, with a noisy
trace) refused correctly but returned ~150,000 characters: 206 trace fragments, each with its own record.
Collapse the trace list in any refused or unassigned reading to a count plus the few longest traces, with the
full list behind an explicit argument, and lead the result with the refusal's sentence and the flag that
answers it (`xAxis`/`yAxis`, or `manualBox` + `xRange` + `yRange`). Apply the same bound to every tool whose
result can grow with its input.

**R-emb5-10 — A followed curve is a trace, not a gridline.** `recognize follow=<name>=<px>,<py>` on a click a
few pixels from a coloured trace locked onto the black 0 dB gridline beneath it and returned a flat line
(`src/Design/Imaging/Plots/ManualCurves.cs`, the follow path). Prefer a coloured, non-grid run near the click
over the plot's own grid and frame colours; report the colour followed and, when another candidate was
within reach, name it, so the caller can click again knowingly rather than discover it in the overlay.

**R-emb5-11 — A curve read by hand can be written as S-parameters.** In manual mode `trace` is refused
("--trace belongs to the automatic reading; a plot read by hand takes --curve …,is=S21:db") and `follow`
accepts no `is=`, so a followed S11 can only come out as CSV. Let `follow` take the same `,is=<quantity>` (and
`,axis=`) suffix `curve` does, so a hand-followed curve reaches a Touchstone like a hand-drawn one.

## 2. Gates

- `ServeProtocolAdapterTests`: a slow fake verb on the worker does not delay a concurrent `find`; a job's
  status/cancel/result round trip; `result` equals the synchronous call's document.
- R-emb5-3's per-verb cancellation test on small fixtures (a planar setup large enough to mesh for a second;
  a 3D setup cancelled during lowering).
- `dryRun` on a planar and a 3D fixture returns the same numbers `explain` prints.
- The root-refusal sentence names the root and the scratch folder.
- R-emb5-6's catalog-vs-parser test.
- R-emb5-7: `10min`, `10 min`, `600`, `600s` and `0.5h` all set the same limit on the CLI, the MCP and a
  `timelimit=` line; a refusal quotes an accepted spelling.
- R-emb5-8: `optimize result=summary` returns the best values and goal margins and no per-iteration record.
- R-emb5-9: a refused plot reading of a fixture with hundreds of fragments returns under a fixed size, with the
  refusal first.
- R-emb5-10: on a fixture with a coloured trace a few pixels from a black gridline, a click between them follows
  the trace, and the result names the gridline as the other candidate.
- R-emb5-11: `follow=S11=<px>,<py>,is=S11:db` with `output=x.s1p` writes a one-port Touchstone.

## 3. Not in scope

- Remote/multi-client servers, authentication, or persisting jobs across a server restart.
- Changing what any verb computes; this brief changes only how the server schedules, reports and stops it.
