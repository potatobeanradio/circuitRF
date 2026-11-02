# Brief MC-28 — Verilog-A export: the Export step, the CLI and MCP, the page and the acceptance

**Series:** `brief-modelcomposer-0-overview.md` (D15, D17, D18) · **Tag:** `R-mc28-<m>` · **Depends on:** MC-26, MC-27
**Area:** `src/Ui/ModelComposer/ModelComposerViewModel.Steps.cs` (the Export step), `src/Ui/Views/ModelComposer/`;
`src/Cli/Model.cs`, `src/Cli/Serve/McpServer.cs` (the `model` tool); `examples/Model Composer/synthetic-gan/`;
`docs/user/src/reference/model-composer.md`, `veriloga.md`, `cli.md` (the user reference for the verb);
`docs/design/model-composer.md`, `docs/design/cli.md`
**Read first:** MC-26 and MC-27; MC-13 (the `model` verb and its MCP tool); MC-15 R-mc15-8 (the Export step);
`tests/Ui.Tests/ModelComposer/ModelMcpToolTests.cs`; `ModelComposerExampleShippedTests`

---

## 1. Goal

MC-26 and MC-27 make `CmodelVerilogA` able to write any table-free composed FET as Verilog-A. This brief puts that
function in front of the user in three places that all call it, as D15 requires: a button on the Export step, the
`model export` verb, and the MCP `model` tool. It then documents it and proves it on the shipped example, end to end:
the example's `.cmodel` and the `.va` written from it, placed on one bench, giving the same DC, S-parameters and
harmonic balance.

## 2. Requirements

**R-mc28-1 — The Export step.** Add **Export Verilog-A…** beside **Export**. It opens a save dialog with
`<name>.va` beside the `.cmodel` path, and writes through `CmodelExport.Assemble` → `CmodelVerilogA.Write` off the UI
thread with a `RunControl`, as Export does.
- It is blocked by exactly what blocks Export (the stale-step line), plus a table constituent. A table blocks it with
  one line naming the constituents — `Id and Qg are tables; Verilog-A export needs ANN or equation constituents.` —
  and **Export** itself is unaffected.
- When it finishes, one line under the buttons names the file and its module, like Export's result line. Nothing else:
  no explanatory paragraph (owner rule — minimal on-screen text).

**R-mc28-2 — The CLI.** `model export <path> -o <out>` picks the format **from `-o`'s extension**, the way `render`
does: `.cmodel` as today, `.va` (or `.vams`) for Verilog-A. Any other extension is refused, listing the three.
- `<path>` may be a `.cmc` (assembled as Export does) or **a `.cmodel`**, which needs no document. That is the case of a
  colleague who was sent a `.cmodel` and wants it as Verilog-A. A `.cmodel` with `-o x.cmodel` is refused, since there
  is nothing to do.
- New flag `--module <name>`, for `export` only. `--created` keeps its meaning for a `.cmodel` and is refused for a `.va`,
  which records the model's own `Created`.
- A refusal exits 1 with the writer's sentence, and every refusal is listed. Cancelled exits 130. `--json` reports the
  written path, the module name, the parameter count and the switches that change nothing for this model.
- The `model` verb's help table and `docs/design/cli.md`'s `model` section gain the rows.

**R-mc28-3 — The MCP tool.** The `model` tool's `export` noun takes the same `output` (extension picks the format),
`module` and a `.cmodel` `path`. Its description gains one sentence saying a `.va` output writes Verilog-A source for
other simulators and refuses table constituents. `ModelMcpToolTests` gets the case.

**R-mc28-4 — The example.** Write `examples/Model Composer/synthetic-gan/gan_4x125.va` from the shipped `.cmodel` and
commit it. Add one testbench beside the example's benches that places the `ComposedFET` and the `VerilogA` (with
`File=gan_4x125.va`) on identical bias networks, with a DC sweep, an S-parameter analysis and a short HB power sweep.
It must be a **check-only** citizen of the shipped-example tests: `ExampleWorkspaceLoadsTests` and
`ModelComposerExampleShippedTests` check it but **never need a compiler** to pass. A machine without one must stay green.
- `ModelComposerExampleShippedTests` gains: the committed `.va` equals `CmodelVerilogA.Write` of the committed
  `.cmodel`, byte for byte. That is what keeps them in step when either is regenerated.

**R-mc28-5 — The user page** (`docs/user/src/reference/model-composer.md`, sources only; the owner runs DocGen). Add a
new section, **Verilog-A export** `{#veriloga}`, linked from the Export step section and from `veriloga.md`'s
introduction:
- what it is for: the same model in another simulator, and the comparison circuitRF can run itself (the example bench);
- what is exported, and the one refusal: **table constituents**, and what to do instead (refit that constituent as an
  ANN or an equation);
- the module's terminals: three, or four with the thermal pin. An unconnected pin still self-heats, unlike the general
  warning on `veriloga.md` about leaving a thermal terminal unwired, and the page says why this module is different;
- the parameter table and the switch table, in user terms. `m` is the host simulator's multiplier;
- **the bias point**: `VgsQ`, `VdsQ`, `TjQ` are **intrinsic** and are needed only when a linear-charge switch is on.
  Explain how to get them from a circuitRF DC run (the intrinsic node voltages of the placed `ComposedFET` and its
  thermal node) or from the other simulator's own operating point, and that leaving them unset with such a switch on
  stops the run with a message;
- what differs from the `ComposedFET`: circuitRF finds the bias point itself, and the `.va` takes it as given. Nothing
  else differs. Do not list implementation detail.
Owner rules: no change history; no vendor or simulator product names (say "another simulator"); never "the design's
own"; no verbose notes.

**R-mc28-6 — Design notes.** `docs/design/model-composer.md`: the Verilog-A section gains the three entry points, the
example bench and the acceptance numbers. Update the status line to MC-28. `docs/design/cli.md`: the `model export`
format rule.

## 3. Not in this phase
A Verilog-A import back into the Model Composer. Exporting a passive model as Verilog-A (it exports as a cell). Any
table export (D17).

## 4. Gates
- `ModelExportVerilogACliTests`: the CLI run as a process writes the same bytes as an in-process
  `CmodelVerilogA.Export`, from a `.cmc` and from a `.cmodel`. Also each refusal: an unknown extension, `.cmodel` to
  `.cmodel`, `--created` with `.va`, a table model listing every table constituent, an illegal `--module`. The `--json`
  shape is checked too.
- `ModelMcpToolTests`: the `.va` case and the table refusal.
- The Export step, through the headless view harness: the button is present, blocked by a table with the one line,
  unblocked otherwise, and writes the file.
- `ModelComposerExampleShippedTests`: byte identity of the committed `.va`, and the new bench checks clean.
- **Acceptance (`CompilerFact`)**: run the example bench. The `ComposedFET` and the `VerilogA` agree within MC-26's
  tolerances on DC, S and the HB sweep, with the switches at their defaults and again with `GateLag = 0`,
  `DrainLag = 0`, `SelfHeating = 0` and with `LinearC = 1` at circuitRF's frozen point. Print the HB curves' largest
  difference to the test output.
- Run only these classes, MC-26's and MC-27's, the existing `ModelComposer` classes, `ModelComposerDocumentedPageTests`
  and `Firewall.Tests`.
