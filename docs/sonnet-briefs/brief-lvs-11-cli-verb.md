# Brief 11 — `circuitrf lvs`

**Series:** [LVS](brief-lvs-0-overview.md) · **Tag:** `R-lvs11-n` · **Design note:** [`lvs.md`](../design/lvs.md) §8.3 · **Reads with:** [`cli.md`](../design/cli.md) §9
**Area:** `src/Cli/Lvs.cs` (new), `src/Cli/CliEntry.cs`, `src/Cli/DocumentKinds.cs`,
`src/Cli/Serve/`, `docs/design/cli.md`
**Depends on:** 8 · **Blocks:** 12, 15

---

## 0. What this brief delivers

```
circuitrf lvs <path> [--flat] [--flatten-cell <name>] [--testbench] [--no-reduce]
                     [--set var=expr] [--severity warning|error] [--json] [-o report.txt]
```

and, with no extra work, the MCP surface: an agent that authors a layout can ask whether it
matches the schematic.

---

## 1. `R-lvs11-1` — the verb holds no comparison logic

**`R-lvs11-1a`** `src/Cli/Lvs.cs` is argument parsing, refusals, reporting and **one call** into
`LvsRun.Run`. `Authoring.cs`' terms, and the rule the whole CLI already follows: an operation that
lives only in a verb is not a capability, and a verb that re-implements one diverges from it
silently.

**`R-lvs11-1b`** Held by a **comment-stripped source scan** over `src/Cli/` for any second
comparison, extraction, partition or terminal derivation — the same gate `AuthoringCliVerbTests`
already applies to the authoring verbs.

**`R-lvs11-1c` It is its own verb and is NOT folded into `check`** (note R-lvs-54). R-aut4-1 is
explicit that `check` must be cheap enough to call after every edit and stops at elaboration; an
LVS on a real board is seconds, not milliseconds, and a `check` that had become slow is a `check`
people stop running. What `check` **does** gain from this series is brief 1's terminal-map
validation, which is cheap, static, and exactly the kind of rule R-aut4-2 says must live where the
GUI enforces it too.

**`R-lvs11-1d`** The GUI panel (brief 12) calls the **identical** function with the identical
arguments. A test asserts the two results are equal object for object, which is what makes
*"a design that passes headlessly passes when opened"* true rather than hoped for.

---

## 2. `R-lvs11-2` — one verb over the document kinds it can answer for

**`R-lvs11-2a`** The kind comes from the path, through `DocumentKinds.Classify`, exactly as
`check` and `render` infer it.

| Path | Meaning |
|---|---|
| a **cell folder** | its primary schematic against its primary layout — **the default unit** (note R-lvs-29) |
| a **workspace** | every cell that has both views; a cell with only one is reported at info and skipped |
| a **`.clay`** | its sibling schematic in the same cell folder |
| a **`.csch`** | its sibling layout |

**`R-lvs11-2b`** A cell with only one of the two views is **not** an error. It is the ordinary
mid-design state and a verb that failed on it would be a verb nobody runs mid-design.

**`R-lvs11-2c`** Any other document kind is a **refusal by kind**, naming what it is — `render`'s
own rule. A foreign extension goes through `convert`'s classifier before being called unknown.

**`R-lvs11-2d`** `--set var=expr` overrides a global **before elaboration**, exactly as every run
verb spells it. It is here because a design whose topology depends on a swept variable has more
than one correct layout, and a caller must be able to say which.

---

## 3. `R-lvs11-3` — output, and the stdout/stderr split

**`R-lvs11-3a`** **stdout is the result**; diagnostics and progress go to stderr. The CLI's
standing rule.

**`R-lvs11-3b`** The default is a human report: the summary line, then findings grouped by
severity, each naming its objects, with the technology, the reduction mode and the counts on the
face of it.

**`R-lvs11-3c`** `--json` projects `LvsRunResult` and **adds nothing**. Every finding is its
`Diagnostic` — stable `lvs.` id plus typed arguments — so a caller reads the layer, the
coordinate or the two values **without parsing the sentence back apart**. The id is the contract.

**`R-lvs11-3d`** `-o report.txt` writes the human report. **It is the only thing this verb ever
writes**, and with no `-o` it writes nothing at all — LVS is read-only on `check`'s terms
(R-aut4-6), so it runs on a read-only tree and on a workspace another process has open.

**`R-lvs11-3e` The CLI stays English, permanently** (§7A). The `Diagnostic` template renders
invariant; a localized CLI error breaks every user's grep, log scraper and CI job.

---

## 4. `R-lvs11-4` — exit codes

**`R-lvs11-4a`** **0** when nothing at or above `--severity` was found; **1** otherwise. Default
severity is `error`.

**`R-lvs11-4b`** A run that found warnings and no errors **exits 0 and still reports them** —
`check`'s rule, for its reason: the alternative makes the exit code useless in CI.

**`R-lvs11-4c`** A **refusal** — unreadable document, wrong kind, elaboration failure, over the
device ceiling — exits 1 with the producing component's own sentence. A refusal is not a finding
and must not be reported as a clean run with a note.

**`R-lvs11-4d`** **130** on cancellation, and nothing written. `em` and `render`'s rule.

**`R-lvs11-4e`** There is no 2. Nothing here converges.

---

## 5. `R-lvs11-5` — the MCP surface

**`R-lvs11-5a`** `serve` gains `lvs` from the verb with no second implementation, which is the
whole point of the automation architecture.

**`R-lvs11-5b`** Its result is the `--json` projection. An agent that wrote a `.clay` asks whether
it matches the `.csch` and gets typed findings it can act on — which is the LVS case that matters
most for an out-of-process author, because an agent cannot look at the screen.

**`R-lvs11-5c`** Documented in `cli.md` as a new §19, on §13-§18's pattern, and in the MCP
reference. The CLI design doc is where a verb's contract lives; this brief updates it rather than
leaving the note as the only description.

---

## 6. Gate

`tests/Ui.Tests/Lvs/LvsCliVerbTests.cs`.

1. **The verb run as a PROCESS equals the in-process call.** `LvsRun.Run` directly vs
   `circuitrf lvs` with `--json`: same findings, same ids, same objects, same order
   (`R-lvs11-1d`). This is the gate the whole brief exists to pass.
2. **The correct board exits 0 with no findings above info**; the six-fault board exits 1 and
   names all six (brief 5).
3. **Warnings-only exits 0 and still prints them** (`R-lvs11-4b`).
4. **`--severity warning` flips that same run to 1** and changes nothing else about the output.
5. **Each document kind resolves**: cell folder, workspace, bare `.clay`, bare `.csch`
   (`R-lvs11-2a`).
6. **A cell with only one view is info and skipped**, and a workspace of such cells exits 0
   (`R-lvs11-2b`).
7. **A `.cem`, a GDSII file and a directory that is not a workspace are refusals BY KIND**
   (`R-lvs11-2c`).
8. **Nothing is written with no `-o`.** Run on a read-only copy; assert no mtime changed
   (`R-lvs11-3d`).
9. **Cancellation exits 130 and writes nothing** (`R-lvs11-4d`).
10. **`--json` carries typed arguments**, asserted by reading a layer and a coordinate out of the
    JSON without touching the sentence (`R-lvs11-3c`).
11. **`--no-reduce` and `--flat` change the counts and not the verdict** on the correct board, and
    the mode is on the face of both outputs (brief 6 `R-lvs6-5c`, brief 9 `R-lvs9-4b`).
12. **`--set` reaches elaboration** — a design whose topology depends on a global compares
    differently under two values (`R-lvs11-2d`).
13. **No second comparison in `src/Cli`** — the source scan (`R-lvs11-1b`).
14. **`serve` answers `lvs`** with the same payload as `--json` (`R-lvs11-5b`).

## 7. Scope

- **No GUI.** Brief 12.
- **No waiver authoring from the CLI.** A waiver is a deliberate, reasoned act with a sentence
  attached; it is written in the editor beside the thing being waived. The CLI **honours** waivers
  and reports them as waived — it does not create them.
- **No new output format.** The human report and the `--json` projection of an existing record.
- **No analysis.** LVS runs no solve, ever.
