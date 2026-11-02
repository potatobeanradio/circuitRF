# Brief MC-13 — The `model` verb and its MCP tool

**Series:** `brief-modelcomposer-0-overview.md` (D13, D15, D16) · **Tag:** `R-mc13-<m>` · **Depends on:** MC-11, MC-12
**Area:** a new `src/Cli/Model.cs`; `src/Cli/CliEntry.cs` (the verb switch); `src/Cli/Serve/ToolCatalog.cs` (the
MCP tool); `src/Cli/DocumentKinds.cs`; the `reference` topics; `docs/design/cli.md` (a new section);
`docs/user/src/reference/cli.md`
**Read first:** `docs/design/cli.md` (the five-step anatomy of a run verb, stdout/stderr, refusals) and the
CLAUDE.md rules for `new`/`import`/`check`/`history`: a verb owns no logic, it calls the function the GUI calls

---

## 1. Goal

Everything the window can do, an agent can do with no display — through the same functions — and get the same
bytes. After this phase the series is **agent-ready**.

## 2. Requirements

**R-mc13-1 — One verb with nouns** (`new` is a noun of `model`, never a separate verb):
- `model new <doc.cmc> --kind active|passive [--network <stock name | path.csch>] [--fingers N0 --unit-width W0]`;
- `model import <doc.cmc> <source…> --as <kind> [--pattern … | --map …] [--condition key=value …]`;
- `model deembed <doc.cmc>` — pads, network, intrinsic data, one summary line per data set;
- `model fit <doc.cmc> --step network|current|charge|thermal|traps|passive|all [--method …] [--seed n]`;
- `model check-charge <doc.cmc | design.cnl --device X1> [--threshold %]`;
- `model explain <doc.cmc>` — data sets and their planes, intrinsic ranges, `N0`/`W0`, temperature mode, each
  step's state (fresh, stale or absent), the network values with their identifiability, and the trap-setting
  levels each trap state was trained from;
- `model export <doc.cmc> -o <out.cmodel> [--symbol …]`, or, passive,
  `model export <doc.cmc> --cell <workspace> <name>`;
- `model validate <doc.cmc | model.cmodel> [--against <data set…>] [--tolerance …]`;
- `model report <doc.cmc> [--model <x.cmodel>] [-o <folder>] [--pdf <file.pdf>]` with every MC-11 R-mc11-6 option as a
  flag (`--sections`, `--classes B,J,F,FInverse`, `--freq`, `--bias vgs,vds`, `--source-2f0 re,im`, `--ropt`, `--disc
  radius,rings,spokes`, `--pin start,step,max`, `--compression`), defaulting to the options stored in the `.cmc`; the
  PDF through the same Data Display document and composer the window's report export uses (MC-15).

**R-mc13-2 — No logic in the verb.** Every noun parses arguments, calls the `src/Design/Modeling` function the
window's command calls, and reports. A comment-stripped source scan of `src/Ui/ModelComposer` and
`src/Cli/Model.cs` proves neither re-implements a flow — the `AuthoringCliVerbTests` pattern.

**R-mc13-3 — Output.**
- Results go to stdout as one line per item; `--json` gives the same content structured.
- Progress goes to stderr through `RunHost`'s `RunControl`, as `em` and `render` do.
- A cancelled run exits **130 and writes nothing**.
- Exit codes are D16's: 0, 1, 3 (charge conservation above the threshold, or a validation tolerance missed), 130.

**R-mc13-4 — Every default is the window's.** A choice the window would ask for is a refusal naming the flag that
answers it: an import whose bias cannot be inferred names `--pattern`; a fit without `N0` names `--fingers`.

**R-mc13-5 — `check` and `explain`.** `check` on a `.cmc` or a `.cmodel` reports their validators' findings (MC-1,
MC-3). `explain --ref` resolves a `ComposedFet`'s `File` exactly as elaboration does. Neither runs a fit.

**R-mc13-6 — MCP.**
- One tool, `model`, with the same nouns and arguments, registered in `ToolCatalog`.
- The `reference` tool gains `cmc` and `cmodel` topics, generated from the schemas, plus a `model-composer` recipe
  in the server instructions' style: new → import → deembed → fit → check-charge → export → validate.

**R-mc13-7 — Documents.** A `docs/design/cli.md` section for the verb; `docs/user/src/reference/cli.md` updated
(sources only — no DocGen).

## 3. Not in this phase
The window (MC-14, MC-15).

## 4. Gates (minimal tests, run only these classes)
- `ModelCliVerbTests` — the CLI as a **process** against the in-process functions, **byte for byte**, for
  `import`, `fit --step all --seed 1`, `export` and `check-charge` on `SyntheticFetData`; exit 3 on a planted
  non-conservative device; 130 on cancel, with nothing written.
- `ModelCliNoLogicTests` — the source scan of R-mc13-2.
- `ModelAgentEndToEndTests` — from raw synthetic files to a schematic using the exported model, simulated with
  `circuitrf hb`, with no display at any step.
- `ModelMcpToolTests` — the MCP tool's `export` equals the CLI's bytes; `reference cmodel` returns the schema.
