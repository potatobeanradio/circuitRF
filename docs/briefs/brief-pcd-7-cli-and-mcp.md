# Brief PCD-7 — `circuitrf pcell` and the MCP `pcell` tool

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd7-n`
**Area:** new `src/Cli/PCellVerb.cs`; `src/Cli/CliEntry.cs` (the `Dispatch` row and the help text),
`src/Cli/Serve/ToolCatalog.cs` (the `pcell` tool), `docs/design/cli.md` (the verb table and §47), `CLAUDE.md` (the verb
list line); one hook in `src/Cli/Render.cs` (D2)
**Depends on:** PCD-0, PCD-1's seams commit. `render` needs only seam 10 (`DesignedCellLayout.At`, real from the seams
commit); `new`, `check`, `export` and `import` need PCD-5's real `PCellDesign.Create`, `PCellDesignCheck.Run` and
`PCellPackage`.

---

## 0. What this adds

The PCell Designer with no window. ONE verb with nouns, as `new`, `model` and `yield` are; every noun calls the
`src/Design` function the window's command calls, so the verb and the window write the same bytes. `PCellVerb.cs` is
argument parsing, refusals and reporting, on `Authoring.cs`' terms.

## 1. Requirements

- **`R-pcd7-1` — `pcell new <workspace-or-dir> <name>`** → `PCellDesign.Create`. The name is `NameValidator`'s; an
  existing folder is a refusal. Prints the created paths, cell folder first (`--json` outputs `cell`, `schematic`,
  `symbol`).
- **`R-pcd7-2` — `pcell check <cell> [--severity warning|error]`** → `PCellDesignCheck.Run`. Each finding on stderr as
  `check` prints one, a summary line on stdout; exit as `check`: 1 on an error, or on a warning with
  `--severity warning`, else 0.
- **`R-pcd7-3` — `pcell render <cell> -o <out.svg|.pdf|.png> [--set name=value]…`** → `DesignedCellLayout.At` at the
  values, drawn by `render`'s own layout path with every one of `render`'s viewport, layer and theme flags. A value is
  an expression evaluated to base SI (`200um`, `0.2mm`, `90`); an unset parameter stays at its default. The planner's
  report lines are forwarded (a warning as a warning). Nothing is written but the picture and the planner's generated
  cells.
- **`R-pcd7-4` — `pcell export <cell> -o <file.cpcell>`** → `PCellPackage.Export`; **`pcell import <file.cpcell> --into
  <dir>`** → `PCellPackage.Import`. Findings forwarded at their severity; a refusal exits 1. stdout is the path written.
- **`R-pcd7-5` — the MCP `pcell` tool**, `what=` the noun, translated to the same argv by `ToolCatalog`.
- **`R-pcd7-6` — `--help`, `docs/design/cli.md`, the verb list.** The top-level help names the five nouns; `pcell
  --help` prints them with the `pcell options:` section.

### Exit codes

0 done · 1 refused, an argument that does not parse, or (check) a finding at the threshold · 130 cancelled (render).

### Diagnostic ids (the verb's own; PCD-5's `pcell.*` findings pass through unchanged)

`pcell.args.noun-required`, `pcell.args.unknown-noun`, `pcell.args.unknown-option`, `pcell.args.required`,
`pcell.args.extra-argument`, `pcell.args.set-malformed`, `pcell.args.unknown-severity`, `pcell.cell.not-found`,
`pcell.cell.not-designed`, `pcell.cell.invalid-name`, `pcell.cell.exists`, `pcell.cell.no-workspace`,
`pcell.cell.no-technology`, `pcell.new.refused`, `pcell.render.refused`, `pcell.render.plan`.

## 2. Decisions

- **D1 — A designed cell only.** `check`, `render` and `export` refuse a cell that is not designed
  (`PCellDesign.IsDesigned`) with `pcell.cell.not-designed`, naming `render` for an ordinary cell.
- **D2 — One render path.** `render` reads its layout from a file; a designed cell's layout at values exists only in
  memory. `Render.RunLayout(args, documentPath, view)` runs `render` unchanged on that view as the `.clay` at
  `documentPath` (the cell's primary `.clay`, else `layout/<Name>.clay`), so instance references, the technology walk
  and the generated cells resolve as they would for the saved layout. Writing a temporary `.clay` instead would put a
  second layout in the user's cell folder; copying `render`'s drawing would be a second renderer (R-rnd0-1). `Render.cs`
  has no owner in PCD-0 §6; the hook is four lines.
- **D3 — The technology** is the one `TechnologyResolver.ResolveForDocument` gives that `.clay`, as Update Layout from
  Schematic resolves it. No technology is a refusal: the planner needs one.
- **D4 — The tool list grows by one.** `ServeProtocolAdapterTests`' tool list and positional table gain `pcell` and its
  five modes: the two gates that exist to be reminded of a new tool.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd7PCellVerbTests.cs`, as `AuthoringCliVerbTests`: the CLI as a process against the
in-process call, byte for byte — `new` (the cell tree), `check` (the exit code and the ids), `render` (the picture
against `render` of the in-process `At` layout saved at the same path), `export` (the package bytes) and `import` (the
cell tree) — and a comment-stripped scan that `PCellVerb.cs` calls the five functions and holds none of their logic.
`ServeProtocolAdapterTests`' tool-list and argument gates cover R-pcd7-5.
