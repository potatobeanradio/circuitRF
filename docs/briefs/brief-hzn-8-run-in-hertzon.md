# HZN-8 — run a `.hzn` design in Hertzon

Series: `brief-hzn-0-overview.md`. Needs HZN-3.

## Why

Hertzon solves its own designs with its own adaptive meshing, sweeps and convergence rules. `hertzon-cli` is
documented for exactly this use: another program sets variables, solves a design and reads the Touchstone file back.
Offering it gives circuitRF users a second, independently supported EM path.

## What to build

### R-hzn8-1 — discovery

`hertzon-cli` is found the way circuitRF finds its solvers (`SolverDiscovery`): a configured path in Settings, then
`PATH`, then the documented install locations for each platform.

- Its `version --json` is probed once per session. The probe reports `hertzon` and `cli_schema`.
- **`cli_schema` 1 is the contract this brief is written against.** Any other value disables Run in Hertzon with a
  sentence naming both versions.
- `solver` (CLI and MCP) lists Hertzon beside the other solvers, with its version and schema.

### R-hzn8-2 — run

Run in Hertzon on a design (tree menu, and the opened design's Simulate menu as a second choice) calls:

```
hertzon-cli solve <file.hzn> --design D [--setup S] [--sweep W] [--set var=value …]
  -o <out.sNp> --progress json --json
```

- **The `.hzn` must be saved.** An opened design with unsaved edits asks to save first (brief 5) and refuses on the
  CLI. Hertzon solves the file, not circuitRF's memory.
- `--set` carries a variable override the user chose in the run dialog. Values come from `hertzon-cli describe`'s
  `variables`.
- Progress events (`progress`, `pass`, `log`, `done`, `error`) feed the existing run panel: percent, adaptive pass,
  tetrahedra and ΔS. Cancel stops the process, which exits 130.
- **Exit codes map to sentences:**

  | Code | Sentence |
  |---|---|
  | 1 | solver failed; `solver.log` named |
  | 3 | bad input; Hertzon's message quoted |
  | 4 | model check failed |
  | 5 | not converged; the Touchstone is still read and the run is marked not converged |

- **The results are Hertzon's.** Hertzon keeps its solution in `<project>.hznresults/` beside the file. circuitRF reads
  the Touchstone that `-o` names, into the same results view a circuitRF run uses, labelled "Hertzon <version>".
  circuitRF writes nothing into `.hznresults`.
- **Open in Hertzon** (brief 3's menu) starts the Hertzon application on the file when it can be found. Otherwise it is
  disabled with the reason.

### R-hzn8-3 — headless

`em <file.hzn> --design D --solver hertzon [...]` runs the same call. It reports Hertzon's `--json` summary fields
(converged, passes, tetrahedra, seconds) in circuitRF's own `--json`.

### R-hzn8-4 — later: a native `.c3d` in Hertzon (owner question Q2)

Not in this brief unless the owner says yes. The shape: brief 4 exports to a scratch `.hzn`, then R-hzn8-2 runs it.

## Gates

- `HertzonCliTests` against a **stub `hertzon-cli`**: a small script in `tests/` that prints recorded `version`,
  `describe` and `solve` outputs and exit codes. It covers discovery, the schema gate, every progress event, every exit
  code, cancel and reading the Touchstone file.
- One real run of a fixture design with an installed Hertzon. Skipped without it, and kept to well under a minute.
- UiSnapshot of the run dialog with variables, the run panel mid-run, and a schema-mismatch refusal.
