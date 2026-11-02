# Brief TO-2 — One evaluation service below the firewall

**Series:** `brief-tuneopt-0-overview.md` · **Tag:** `R-to2-<m>` · **Depends on:** TO-1
**Area:** `src/Ui/Schematic/SchematicRunService.cs` (source of the extraction), `src/Design/Circuit/` (destination;
`HbCircuitRun.cs` and `ChainSelection.cs` live there already), `src/Cli/RunHost.cs`, `src/Engine/RunControl.cs`,
`src/Engine/MeasurementEvaluator.cs`

---

## 1. Why

Tuning and the optimizer must run "this design, with these values, these analyses" thousands of times, from the GUI,
the CLI and MCP. Today the GUI's whole run — dispatch every analysis, assemble the grouped `DataSet`, evaluate
measurements into the `measurements` group — lives in `SchematicRunService` in `src/Ui`, out of reach of `src/Cli`.
The CLI verbs reach the same result by other code (`RunHost`, `HbCircuitRun`). A third copy for the optimizer would be
the divergence this repo's CLI rules forbid (`CLAUDE.md`: an operation that lives only in a view model is not a
capability; a verb that re-implements one diverges from it silently).

## 2. Requirements

**R-to2-1 — Extract.** Move the body of `SchematicRunService.Prepare`/`Execute` that does not touch Avalonia into
`src/Design/Circuit/CircuitEvaluation.cs` (name is the implementer's). `SchematicRunService` becomes a thin caller.
Simulate's behavior and its `run.npy` are **unchanged** — that is the gate.

**R-to2-2 — The request.** One call takes: the netlist text or design (what `CircuitSource.CnlTextOf` produces), a
base directory, `--set` overrides, **tunable values** (TO-1 `TunableOverrides`), an **analysis subset** (names; null =
every enabled analysis), whether to evaluate **measurements**, an optional list of **extra expressions** to evaluate in
the measurement scope (the goals, TO-6), and a `RunControl`. It returns the grouped `DataSet`, run notes, warnings, the
extra expressions' values, and per-analysis convergence status.

**R-to2-3 — No file writes.** The service never writes `run.npy`. Writing is the caller's (`RunResultsWriter`), so a
tune step or an optimizer evaluation costs no disk I/O.

**R-to2-4 — Reuse of elaboration work.** When only tunable values change between calls, re-elaborate (values move
the netlist) but do not re-read, re-parse or re-resolve the cell library from disk. Hold a prepared design object the
caller can reuse; assert with a **counter** (files read, cells resolved) that a second evaluation reads nothing.

**R-to2-5 — Concurrency audit.** Several evaluations of the same prepared design must be able to run concurrently on
different threads (TO-6 parallel evaluation; yield later). Audit the engine and model paths for shared mutable state
(static caches, `TechnologyCache`'s shared instances — `CLAUDE.md` records one such trap — Verilog-A / external device
workers, HB continuation state). Fix what is cheap; for what is not, the service must **report itself not reentrant
for that design** (e.g. a design with an external device worker), and callers fall back to one-at-a-time. Record the
audit in `src/Design/RESOLVED.md`.

**R-to2-6 — Cancellation and progress.** `RunControl` cancellation stops at the next work boundary and returns
"cancelled" with no partial `DataSet`. Progress is per analysis.

**R-to2-7 — CLI convergence.** The `sparam`, `dc`, `hb` verbs call the service where they currently assemble the
same thing by hand. Do not change their output bytes; where a verb's current output differs from the GUI's for the
same netlist, stop and report the difference to the owner rather than choosing.

**R-to2-8 — Complex values (overview D18; amended 2026-10-07).** Nothing new in the service: a tunable value it is
handed may be a whole complex value under its own key (`ZL` → `30+52j Ohm`), and `TunableOverrides.Apply` turns it
into the netlist text exactly as typing it would. Part keys are folded by `Apply`, never by the service. The reuse
counter (R-to2-4) must hold for a complex value moving too — it is re-elaboration only, like any other value.

## 3. Gates (minimal; run only these classes)
- `CircuitEvaluationParityTests` — for the S-parameter, HB and loadpull examples, `run.npy` from the old GUI path
  (captured once as a fixture before the extraction) equals the new path's byte for byte.
- `CircuitEvaluationReuseTests` — the second evaluation with different tunable values reads no file (counter).
- `CircuitEvaluationConcurrencyTests` — four concurrent evaluations of an S-parameter design with four value sets
  equal four sequential ones; a design the audit marks non-reentrant reports so.
- A comment-stripped source scan: `src/Ui` and `src/Cli` contain no second measurement-group assembly.
