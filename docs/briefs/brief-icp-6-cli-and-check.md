# Brief ICP-6 — `em`, `explain`, `check`, MCP and the format reference

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp6-<n>` · **Depends on:** ICP-1, ICP-2, ICP-5
**Area:** `src/Cli/ExplainComponentPorts.cs`, `Explain.cs`, `Check.cs`, `EmDryRun.cs`, `CliEntry.cs` (`--component-ports`),
`CliDiagnostics.cs`, `DocumentSchema.cs`, `src/Cli/Serve/McpServer.cs`, `docs/design/cli.md`.

## The gap

`--component-ports` takes refdes and `"*"`. `explain` walks one line per footprint part (`component port C1: … verified`) and
`check` reports `check.cem.component-port` from LCP's builder. None of them can name `X1/Q3` or a cell, and none says what
was left out of the problem.

## Requirements

**R-icp6-1 — Spellings.** `--component-ports` takes a comma list of `*`, a refdes or path (`Q1`, `X1/Q3`, `X1/*`) and
`cell:<name>` (the `Cell` key). `--port-at pin|cell` applies to every entry the flag names. They replace the setup's
`ComponentPorts` for the run, as the flag does today. Parsing is in the CLI; selection is ICP-1's.

**R-icp6-2 — `explain` reports the walk.** Per instance: its path and cell, how it was selected (`*`, by name, by cell),
what was left out (its shapes, or the box with its size), and per pin the face (point, width, direction, layer), the port
number and name, its reference, or why it was left out — read from ICP-2's build, never recomputed. A copper instance that
`*` passed over is listed once as such. `--json` carries the same, in the existing component-port step shape.

**R-icp6-3 — `check` reports it.** Each verdict as `check.cem.component-port` (info when built, warning when an instance or
pin was left out), and a `.c3d`'s as `check.c3d.component-port`; the refusal of the whole list as an error, as today.

**R-icp6-4 — `em --dry-run`** counts the cut ports in its unknowns and names them, as it names gap ports.

**R-icp6-5 — MCP.** The `run`, `explain` and `check` tools take the same `componentPorts` spellings; `reference em-setup`
documents the new keys (`Cell`, `PortAt`, `KeepOutUm`, `Construction`) and the path grammar, from the one schema
`DocumentSchema` already serves.

**R-icp6-6 — The issue's two commands.** On the shipped examples:
- `explain "PCB Component Ports/Filter Board/em/Filter Board Planar.cem" --component-ports "*"` prints what it printed.
- `explain "PDK PCells/SpiralResonator/em/SpiralResonator.cem" --component-ports "*"` says no device is placed and names
  `@0 (KIT_SPIRAL)` and `@1 (KIT_MIMCAP)`; with `--component-ports "@1"` it reports the MIM cap cut out with a port per
  pin, or the reason a pin was left out. (The kit's series MIM cap brings both plates out to pins facing opposite ways on
  one layer, so each pin face meets its own line: no gap between facing pads is involved.)

## Gates

The CLI as a process, as the other verb gates do. One test per claim:
1. Each spelling parses into the entries ICP-1 reads, and a bad one is refused naming the grammar.
2. `explain --json` on a cut matches ICP-2's build field for field.
3. `check` exit codes: built → 0; an instance left out → 0 with a warning; the list refused → 1.
4. R-icp6-6's two commands, the board one byte-identical to before.

## Not here

UI (ICP-7).
