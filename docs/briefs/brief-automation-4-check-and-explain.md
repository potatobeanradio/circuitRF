# Sonnet Brief — AUT-4: `check` and `explain` — closing the loop

**Read `brief-automation-0-overview.md` first**, and note that this brief **depends on AUT-1** (its
output is structured diagnostics) and **AUT-2** (it must validate a `.csch`). The architecture is
`docs/design/automation-architecture.md`; R-aut-5 and R-aut-10 are what this brief implements.

**Scope: telling a caller what is wrong with a design, and what a reference resolved to.** No
authoring, no simulation, no editing. These are the verbs that make a headless client's loop
converge: it writes a document, asks whether the document is sound, and asks what circuitRF made of
it — without paying for a run.

---

## 1. Why these two verbs and not more

A client that has just written a file has three questions, in order:

1. **Is this well formed?** — parse, schema, primacy, name validity.
2. **Does it resolve?** — do the cell references, technology references, layout references, SnP
   paths, kit parts and expression symbols all find something?
3. **Is it sound?** — DRC, technology validation, unconnected nets, an analysis chain that will not
   dispatch.

`check` answers all three. **`explain` answers a fourth that is not a failure at all**: *what did
circuitRF decide?* — which technology a layout resolved to and by which walk-up, which analysis chain
`SelectTop` would promote to, what an expression evaluates to, which cell a relative ref landed on.
That question is asked constantly during authoring and today the only way to answer it is to run
something and read stderr.

**R-aut4-1. Neither verb runs an analysis.** `check` on a large design must be fast enough to call
after every edit. If a check needs a solve to answer, it is out of scope and belongs in the run verbs'
own warnings.

---

## 2. `check`

```
circuitrf check <path> [--json] [--recursive] [--severity warning|error]
```

`<path>` is a `.cws`, a cell folder, or a single `.csch` / `.csym` / `.clay` / `.ctech` / `.cem` /
`.cnl`. A workspace or a folder checks everything under it.

**R-aut4-2. `check` calls the validators that already exist. It writes no new validation logic.**
The repo is full of them and they are scattered rather than missing:

| Validator | Where | Checks |
|---|---|---|
| `CellViewFileValidator.DescribeDefect` | `src/Ui/Schematic` (moves in AUT-2) | a view file's defects |
| `CellFolder.ResolvePrimary` | `src/Design/Cells` | primacy — `MissingNamedPrimary`, empty sub-folder, ambiguous |
| `NameValidator` | `src/Design/Cells` | cell and view names |
| `TechValidation.Validate` / `.Analyze` | `src/Design/Layout` | a `.ctech`'s problems, already typed as `TechProblem` |
| `TechnologyResolver` | `src/Design/Layout` | the technology walk-up |
| `EmSetupResolver.Resolve` | `src/Design/Layout/Em` | a `.cem`'s layout and technology, and its refusals |
| `CellSymbolResolver` / `HierarchyResolver` | `src/Ui/Schematic` (AUT-2) | cell references |
| `Elaborator.Elaborate` | `src/Core/Elaboration` | parameters, expressions, cycles, node numbering |
| `SelectTop` | `src/Cli/Program.cs:1252` | whether an analysis chain will dispatch at all |
| `DrcEngine` | `src/Ui/Layout/Drc` | layout design rules — see R-aut4-3 |

**Anything `check` finds that no existing validator produces is a finding to report**, not a new
check to invent here. A rule that exists only in `check` is a rule the GUI does not enforce.

**R-aut4-3. The DRC engine is framework-free and should move.** All 11 files in `src/Ui/Layout/Drc`
(`DrcEngine`, `DrcModel`, `DrcConnectivity`, `DrcRegions`, `DrcRunReport`, the predicate and region
evaluators, `DrcWireCheck`, `WBondClearance`) reference no UI framework — measured, not assumed. The
layer-expression *format* is already in `src/Design/Layout/Drc`; only the engine is above the wall,
and the repo-root `CLAUDE.md` records that split as a fact rather than a decision. Move the engine to
`src/Design/Layout/Drc` beside its format, on AUT-2's terms (R-aut2-3: whole files, no reshaping).

**If it turns out not to be free — if the closure pulls in the layout editor — stop and report it.**
Running DRC headlessly is worth having but it is not what this brief is for, and a `check` that omits
DRC and says so is far better than one that drags a canvas below the wall.

**R-aut4-4. Every finding is a `Diagnostic`** (AUT-1 R-aut1-6), with a stable id, the offending
file's path and whatever typed values the producing validator already has. `TechProblem` is already
typed; do not flatten it into a sentence on the way out.

**R-aut4-5. Exit code: 0 if nothing at or above `--severity` was found, 1 otherwise.** Default
severity is `error`. There is no `2` — nothing here converges. A check that found warnings and no
errors exits 0 and still reports them, because the alternative makes the exit code useless in CI.

**R-aut4-6. `check` never writes.** Not a repair, not a re-save, not a cache file. A caller must be
able to run it on a read-only tree, and on a workspace another process has open.

---

## 3. `explain`

```
circuitrf explain <path> [--json]
circuitrf explain <path> --expr "<expression>"
circuitrf explain <path> --analysis [<name>]
circuitrf explain <path> --ref <relative-ref>
```

**R-aut4-7. `explain` reports resolution, and shows the walk it performed.** The value is as much in
the path taken as in the answer:

- **A `.cem` or `.clay`** — the workspace found by walking up, the layout it resolved to, the
  technology and *which* workspace resolved it. The two walks start from different files and can land
  on different workspaces; that is deliberate (`cli.md` §8.1) and is exactly the thing a caller
  cannot otherwise see.
- **`--analysis`** — every runnable chain in the TestBench, which one `SelectTop` would dispatch, and
  whether a named inner analysis would be **promoted** to its wrapper. That promotion is silent-ish
  today (a stderr note) and it exists because naming the inner analysis produces a converged,
  plausible, complete-looking result for a run the caller thinks swept (`cli.md` §4). Making it
  inspectable without a run is most of this option's value.
- **`--expr`** — evaluate an expression in the design's own resolved scope and report its value and
  kind (Real / Complex / Bool). The expression engine is one engine serving globals, cell parameters,
  SDD equations and measurements (`docs/design/expressions.md`); this evaluates through it, never by
  substitution.
- **`--ref`** — what a relative cell reference resolves to from that document's directory, including
  when it leaves the workspace.

**R-aut4-8. `explain` never guesses and never falls back silently.** Where resolution fails, that is
the answer — reported as a `Diagnostic` naming what was looked for and where it was looked. A caller
uses this verb precisely when something did not resolve.

**R-aut4-9. Sweep units are reported with their scale.** A swept variable's unit carries both a mark
and a scale, and reading the mark without the scale has already produced a run at 2 Hz that looked
entirely normal (recorded in `sweep-unit-scale-and-mark`). `explain --analysis` prints the sweep's
resolved start, stop and step **in base SI with the unit named**, so that class of error is visible
without running anything.

---

## 4. Shape

**R-aut4-10. Both verbs follow `cli.md` §9**, and both are read-only. `--kits` applies (a kit part
that will not resolve is exactly what `check` should catch). `--json` per AUT-1, with `diagnostics`
carrying the findings and `result` carrying the resolution report.

**R-aut4-11. `check` is one verb over every document type, not one per type.** R-aut-9. The type is
inferred from the path — by extension, and for a directory by what it contains — exactly as `convert`
already infers formats.

---

## 5. Gates

1. **Every validator in R-aut4-2's table is reached by at least one test** with a deliberately broken
   fixture, asserting the specific `Diagnostic` id. Anonymize fixture paths to the *shape* of a path.
2. **A clean workspace checks clean**: the shipped example workspaces exit 0 with no error-severity
   diagnostics. If one does not, **that is a finding about the repo** — report it, do not adjust the
   check to pass.
3. **`check` writes nothing**: run it against a tree whose files are read-only and assert both a
   clean exit and unchanged mtimes.
4. **`explain --analysis` reports the promotion** for a netlist whose analysis is wrapped in a
   `parametric_sweep`, and its report agrees with what `hb`/`lp` actually dispatch for the same file.
   Assert against the run, not against a transcription of `SelectTop`.
5. **`explain` agrees with `em`**: for a `.cem` whose layout and technology resolve through two
   different workspaces, the reported paths are the ones `EmSetupResolver.Resolve` actually used.
6. **`explain --expr` agrees with elaboration**: an expression that a `--set` override would change is
   reported differently before and after the override, matching what the elaborated netlist holds.
7. **Speed** (R-aut4-1): `check` on the largest shipped example workspace completes in a time worth
   calling after every edit. **Measure it and report the number; do not add a timing test** — a
   wall-clock assertion measures the machine and flakes. If it is slow, say so and say why.

---

## 6. On completion

Write the findings to `src/Cli/RESOLVED.md` and, if the DRC engine moved, `src/Design/RESOLVED.md` —
**never to a `CLAUDE.md`**. Update `docs/design/cli.md` §2 and §9, and
`docs/design/automation-architecture.md` §4 (these are the verbs R-aut-5 predicted would matter most).

**Report, do not silently absorb:**
- Whether the DRC engine moved (R-aut4-3), and if not, exactly what blocked it.
- Any validation that `check` needed and no existing validator provided — that is a gap in what the
  GUI enforces, and it is the most valuable thing this brief can find.
- The §5.2 result on the shipped workspaces, in full.
- The §5.7 measurement.
