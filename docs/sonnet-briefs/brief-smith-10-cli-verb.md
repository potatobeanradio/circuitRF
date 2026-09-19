# Brief 10 — `circuitrf smith`, and why it is wiring rather than a refactor

**Series:** [Smith Chart](brief-smith-0-overview.md) · **Tag:** `R-smith10-n` · **Phase:** P3
**Area:** `src/Cli/` · **Depends on:** 2 · **Blocks:** nothing
**Design note:** [`smith-chart.md`](../design/smith-chart.md) §9 · **Read with:** [`docs/design/cli.md`](../design/cli.md)

---

## 0. What this brief delivers

One verb. It evaluates a `.csmith` and writes the load Γ, or draws the chart. **It depends on brief 2 and
not on the window**, which is the whole point of having put the arithmetic below the firewall from day one.

### `R-smith10-1` — the standing rule for a verb in this repository

> `src/Cli/Smith.cs` is argument parsing, refusals and reporting. **It holds no logic of its own.**

`src/Cli/Authoring.cs` already states it and a comment-stripped source scan is what holds it: every answer
comes from the same `src/Design/Smith` function the window calls. A rule living only in the verb is a rule
the application does not enforce, so a document would evaluate one way headlessly and another when someone
opened it.

**Read `docs/design/cli.md` before writing this** — the five-step anatomy of a run verb and the
stdout/stderr split are settled there and this verb does not get to differ.

---

## 1. `R-smith10-2` — the shape

```
circuitrf smith <path.csmith> [options]

  --at <freq>            evaluate at this frequency instead of the document's design frequency
  --sweep                force the document's swept band on
  --set var=expr         override a global before evaluation, exactly as every run verb does
  -o out.s1p             write the load Γ as Touchstone
  -o out.{svg,pdf,png}   draw the chart
  --json                 the result as structured output
```

- **The path is inferred by kind through `DocumentKinds.Classify`**, exactly as `check` infers one. Any
  other document kind is **a refusal BY KIND**, naming what it found.
- **`-o`'s extension picks the format.** `.s1p` writes data; `.svg`/`.pdf`/`.png` writes a picture.
- **There is no picture on stdout.** stdout is the result and `--json` has to co-exist with the write —
  `render`'s own rule.
- With no `-o`, the verb **reports**: the design frequency, the load impedance and Γ, VSWR, the
  conjugate-match mismatch, and the per-node impedances as a table. That last one is what makes the verb
  useful for a build machine rather than merely possible.

### `R-smith10-3` — the picture goes through `render`, not through a second renderer

A `.csmith` chart is drawn by building the same `Plot` the window builds and handing it to the same
composer `circuitrf render --data` uses (`PlotComposer`). **No second drawing path**, and the fonts and
theme resolve through `src/Render`'s own embedded resources — which is exactly why `SkiaFonts` and
`ThemeResolver` were moved there: both used to fall back **silently** with no app host, producing a
different picture reported as a success.

The constant-Q arcs, the conjugate targets and the load-point labels are all in that picture, because they
are all in the `Plot` and its overlay description — which brief 9 and brief 5 kept framework-free for this
reason.

**Any per-render narrowing is done on a clone.** A verb that hides an overlay or selects a subset by
mutating the resolved theme in place is `TechnologyCache`'s defect — a shared instance narrowed once stays
narrowed for every later call in the same process, which is invisible until the second one. `render
--layers` already had to fix exactly this, through `TechnologyLayerSelection`.

### `R-smith10-4` — refusals and exit codes

The house spelling, unchanged: a refusal exits **1** with the refusal's own sentence on stderr; a
cancellation exits **130** and **writes nothing**. Every refusal brief 1's `Refusal()` and brief 2's
evaluator already produce is surfaced verbatim — the verb adds no sentences of its own beyond argument
parsing.

Progress and cancellation ride `RunHost`'s `RunControl`, as `em` and `render` do.

---

## 2. The gate

`tests/Ui.Tests/Smith/SmithCliVerbTests.cs` — the gate every verb in this repository has:

1. **Byte identity against the in-process call.** Run the verb **as a process**; run the same
   `src/Design/Smith` evaluation and `PlotComposer` call in process; compare the written `.s1p` and the
   written `.svg` **byte for byte**, excluding only a provenance write-timestamp the file carries by
   design. This is what proves `R-smith10-1`.
2. **A comment-stripped source scan over `src/Cli/Smith.cs`** proving it holds no second evaluator — no
   arithmetic on impedances, no second `Plot` construction.
3. **A refusal by kind**: a `.csch`, a `.clay` and a `.crail` each exit 1 naming what was found.
4. **`--at` outside the generator table's span refuses** with the span in the sentence (brief 2
   `R-smith2-5`), and a one-row table accepts any frequency.
5. **Exit 130 writes nothing.**
6. **`--json` and `-o` co-exist**: the picture is written and stdout is still parseable JSON.

---

## 3. What this brief must NOT do

- **No per-primitive edit options.** There is no `--add-element` and no `--set-value`. Once a document
  exists, the way to change it is to **write** it, because the format is the contract. This is the
  standing rule `new` and `history` already follow.
- **No second renderer, no second evaluator, no second Touchstone writer.**
- **No new refusal sentences.** Surface the ones the model already produces.
- **No `smith` subcommand nouns.** One verb, options only.

---

## 4. On completion

Findings to `src/Cli/RESOLVED.md`, and add the verb to `docs/design/cli.md`'s verb list and to the CLI
reference page. **Never a `CLAUDE.md`.**
