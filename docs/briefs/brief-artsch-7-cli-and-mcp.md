# Brief AS-7 — `circuitrf recognize` and the MCP `recognize` tool

**Series:** `brief-artsch-0-overview.md` (D2, D4, D10, D11, D16) · **Tag:** `R-as7-<m>`
**Depends on:** AS-6
**Area:** new `src/Cli/Recognize.cs`; `src/Cli/CliEntry.cs` (verb dispatch; `IsVerb` comes from `Run`'s switch);
`src/Cli/Serve/ToolCatalog.cs`, `src/Cli/Serve/McpServer.cs`; `src/Cli/DocumentKinds.cs` (read-only use);
`docs/design/cli.md` (a new chapter, in the shape of the `lvs` and `yield` chapters); `CLAUDE.md` is **not** edited
— the verb list there is the owner's to update

---

## 1. Goal

The whole feature headless, so an agent (or a build script) can turn a board into a simulatable circuit, review
and edit the parts table as a file, and write the schematic — with exactly the result the GUI command produces.
**Agent-ready here.**

## 2. Requirements

**R-as7-1 — The verb owns no recognition.** `src/Cli/Recognize.cs` is argument parsing, refusals and reporting,
on `Authoring.cs`' terms. Every decision is `ArtworkRecognition`'s (AS-3…AS-6). A comment-stripped source scan
(the `Authoring`/`history` gates' pattern) holds it: no reference from `src/Cli` to recognition internals other
than the entry point, the options, the result and `PartsTableCsv`.

**R-as7-2 — Input.** `circuitrf recognize <path>`: a `.clay`, a cell folder (its layout view; more than one
layout view is a refusal listing them, `render`'s rule), or a workspace with `--cell <name>`. The kind is inferred
through `DocumentKinds.Classify` as `check` and `render` infer it. A path with no layout is a refusal **by kind**.

**R-as7-3 — Read-only by default.** With neither `-o` nor `--into`, the verb **writes nothing**: it prints the
report (one line per class) and the parts table to stdout, and exits 0. That is the first call an agent makes.

**R-as7-4 — Outputs.**
- `-o <file.cnl>` — writes the circuit (R-as6-2) and nothing else.
- `--into new:<name>` — a new cell; `--into artwork` — the artwork cell (refused when it has a schematic view, with
  the sentence naming `new:`); a target holding an `ArtworkSource` schematic needs **`--replace`**, else a refusal
  naming the flag (the GUI asks; a build machine cannot be asked). A hand-drawn schematic is refused whatever the
  flags.
- `--parts-out <file.csv>` — writes the parts table (R-as4-8); allowed alone or with any output.
- `-o` and `--into` together are allowed (the same recognition, both written).
- Paths created are the result: on stdout and in `--json`.

**R-as7-5 — Options**, each the dialog's default when absent:
| Flag | Meaning |
|---|---|
| `--parts <file.csv>` | the edited parts table (R-as4-8) |
| `--bom <file>` / `--placement <file>` | companion files; `--placement-origin symbol\|body\|pin1`, `--placement-unit mm\|mil\|in`; an unstated origin the file does not declare is a **refusal naming `--placement-origin`** (the readers' rule) |
| `--region x0,y0,x1,y1` | scope; **every coordinate carries an SI unit; a bare number is a refusal** (`render --window`'s spelling and rule) |
| `--ground <net>` / `--ground-at x,y` | ground override (units required on the point) |
| `--vias model\|ground` | D7's policy |
| `--coplanar auto\|microstrip\|gcpw`, `--coplanar-factor <k>` | R-as5-2 |
| `--start <f> --stop <f> --npts <n>` | the analysis range (D15), with units |
| `--json` | the result document (paths, report classes with counts and anchors, the parts table) on stdout |

**R-as7-6 — Exit codes.** **0** recognised (and written, when asked); **1** refusal (nothing written);
**130** cancelled via `RunHost`'s `RunControl` (writes nothing — the targets are written last, after everything else
succeeded). Never 2: nothing here solves a circuit.

**R-as7-7 — MCP.** A `recognize` tool in `ToolCatalog` with the same options as named fields, the same read-only
default, the same refusals and the JSON result. Its description says, in one line each: read-only unless `into` or
`output` is given; review `parts` before writing; the result is a schematic to simulate with `run`.

**R-as7-8 — `check` and `explain`.** `check` on a recognised schematic reports nothing new (its components are
ordinary). `explain` on one reports the `ArtworkSource` block in plain lines and, with `--ref`, resolves the source
`.clay`.

**R-as7-9 — Docs.** `docs/design/cli.md` gains a `recognize` chapter: anatomy, the read-only default, the parts
round trip, the refusals, exit codes, the gate. `docs/user/src` gains the verb's reference row.

## 3. Not in this phase
The GUI (AS-8).

## 4. Gates (minimal tests, run only these classes)
`tests/Ui.Tests/Recognition/RecognizeCliVerbTests.cs`:
- the verb as a **process** against `ArtworkRecognition.Run` in-process on the same synthetic board: the `.csch`
  **byte for byte**, except the provenance time; the `.cnl` byte for byte with no exception;
- the read-only default writes nothing (the directory tree's file list and timestamps unchanged);
- `--parts-out` → edit one value in the CSV → `--parts` → the written circuit has that value and no variable for it;
- `--region 0,0,10,5` (bare numbers) is the refusal; `--into artwork` on a cell with a schematic is the refusal;
  `--into new:x` twice without `--replace` is the refusal naming it, with `--replace` it writes;
- the end to end: `recognize --into new:m` then `run analysis=sparam` on the result produces a DataSet with no
  display at any step;
- the source scan of R-as7-1.
`tests/Ui.Tests/Cli/McpToolCatalogTests.cs` (existing, if present; else the class that gates the catalog) — one
case: `recognize` is listed, and its read-only default writes nothing.
