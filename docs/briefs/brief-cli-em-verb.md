# Sonnet Brief — `circuitrf em <setup.cem>`: an EM run from the command line

**Date:** 2026-08-26 · **Status:** specified, not started · **Depends on:** nothing
**Related:** `docs/design/cli.md` §8, `docs/design/mom-engine.md`, `src/Ui/Layout/Em/CLAUDE.md`

## 0. What is being asked for

```
circuitrf em Amp.cem                     # → results/Amp.s2p (+ .npy), summary on stdout
circuitrf em Amp.cem -o /tmp/amp.s2p     # explicit Touchstone destination
```

The GUI already does this. `EmRunService.Run` is **headless by construction** — "everything the
Simulate button does that is not dispatcher work, so it is testable without a document, a canvas or a
workspace" — and it already writes the `.sNp` and the `.npy` itself, at a predictable path
(`ResolveSnpPath`), for the explicit reason that a schematic's SnP reference must survive a re-run.

**So this brief is not about writing an EM driver. It is about moving one to the other side of the UI
firewall.** Read §2 before designing anything: the interesting question is where the new project's
boundary goes, and the answer is not "move `src/Ui/Layout/Em/`".

## 1. Why there is no `em` verb today

`src/Cli` references `src/Core` and `src/Engine` only. `tests/Firewall.Tests` fails the build if
anything on that path references Avalonia. `EmRunService` lives in the `CircuitRF.Ui` assembly, and
referencing that assembly pulls Avalonia across the firewall.

**The engines are not the problem.** `src/Engine/Mom` — the planar MoM kernel, the layered Green's
function, the mesher, de-embedding — is already on the CLI's side of the line. Only the half that
turns a `.cem` plus a `.clay` into an `EmProblem` is on the wrong one.

**And that half is already framework-free.** R-em-1 has held since L6/L7: nothing under
`src/Ui/Layout/Em/` references Avalonia or SkiaSharp, and the four files that mention either do so
only in a comment saying they must not. Verified 2026-08-26 by compiling the folder outside the Ui
assembly (below). **Nothing has to be rewritten. The work is a project split.**

## 2. The measured closure — the actual size of this change

Measured 2026-08-26 by building the candidate file set as a standalone project against
`Core` + `Engine` + `RfCore` only, and following the compiler until it stopped finding new names.

**48 files, ~11,200 lines**, in five clusters:

| Cluster | What | Notes |
|---|---|---|
| `Layout/Em/` (13 files, ~4,200 lines) | `.cem` model + persistence, `EmGeometry`, `EmPortExtraction`, `CrossSectionExtractor`, `PlanarExtractor`, `EmRunService`, `EmSnpProvenance`, `EmSolveCores`, `EmExtractionResult/Settings`, `EmLengthFormat` | the target |
| layout model (~15 files) | `LayoutModel`, `LayoutPersistence`, `LayoutGeometry`, `LayoutUnits`, `LayoutAngle`, `LayoutFlatten`, `LayoutFlattener`, `LayoutClipper`, `LayoutBooleans`, `LayoutCoordinateWalk`, `LayoutInstanceTransform`, `LayoutRotationPromotion`, `LayoutSpatialIndex`, `LayoutChangeInfo`, `CellLayoutResolver` | a `.cem` names a `.clay`; something has to read and flatten it |
| technology (5 files) | `TechModel`, `TechPersistence`, `TechnologyResolver`, `TechnologyCache`, `TechValidation` | the stackup is what makes the geometry an EM problem |
| small model types | `PCells/PCellValue` + `PCellUnits` + `PCellContract` + its JSON converter, `Assembly/WasmModel`, `Theming/Rgba`, `Messages/*`, `Schematic/AtomicFile`, `Schematic/CellFolder` | see R-emcli-3 |
| package | `Clipper2` | already a Ui dependency |

**Two results worth knowing before planning:**

- **The PCell GENERATORS are not in the closure.** Only the PCell *model* types are (`PCellValue` is
  how an instance carries its parameters). All seven generators, the handle solver, the geometry
  cache, `GeneratedCellStore`, `SubstrateResolver` — 7,000 of the 7,666 lines under `PCells/` — stay
  in `src/Ui`. Flattening resolves a placed generated cell by **reading the `.clay` the generator
  already wrote**, so a headless EM run needs no generator and no Python.
- **Only three couplings need inverting or splitting rather than moving**, and each is small:
  `LayoutModel`/`LayoutPersistence` want `Drc.DrcWaiver` (one class, out of `DrcModel.cs`, which
  otherwise drags DRC and wBond in); `EmRunService` wants `RunResultsWriter.SanitizeFileNameComponent`
  and `WriteRun`; `EmSolveCores` reads a **GUI preference** for its core cap.

## 3. Requirements

**R-emcli-1. A new non-UI project holds the EM setup pipeline, and `src/Ui` references it rather than
holding its own copy.** No duplication: two copies of the layout reader is two readers that disagree
about a `.clay` the day someone fixes one of them. `src/Cli` references it too.

Name and location are yours to pick; the boundary is the requirement. What the project holds is "the
design-layer artifacts an EM problem is built from and the code that turns them into one" — the
layout model, the technology model, the `.cem`, and the extractors. What it must NOT hold is anything
that draws, docks, undoes, or observes a canvas.

**R-emcli-2. `tests/Firewall.Tests` gets the new assembly.** It must assert the same thing it asserts
of `Core`/`Engine`/`RfCore`: no Avalonia reference, transitively. A project that starts clean and is
not gated will not stay clean — that is the whole reason the firewall test exists.

**R-emcli-3. Split or invert the three couplings; do not drag their subsystems across.**

- `DrcWaiver` moves to its own file. DRC itself stays in `src/Ui`.
- `EmSolveCores`' core cap becomes an **argument** with a sensible default, not a preference read.
  Headless, there is no preferences file to read and no user to have set it. The GUI keeps passing
  its preference in.
- `RunResultsWriter`: extract what `EmRunService` actually uses. `SanitizeFileNameComponent` is a
  pure string function. `WriteRun` is a results-folder convention shared with schematic runs — decide
  whether the convention moves or the EM caller takes the folder as a parameter, and **say which in
  the file's own header**, because the next person will ask.
- `CellFolder`/`CellPersistence`/`NameValidator` close the cell-resolution tail. If moving them pulls
  the schematic cell subsystem in, invert instead: `CellLayoutResolver` takes a resolver delegate.
  **Stop and report if this tail turns out to be larger than the rest of the closure put together** —
  that would mean the boundary is in the wrong place and this brief should be re-cut before more code
  moves.

**R-emcli-4. Namespaces change with the project.** Keeping `CircuitRF.Ui.Layout` as a namespace inside
a non-UI assembly costs no churn on day one and lies about the architecture forever, which is the kind
of thing that gets a file moved back. Rename, and take the mechanical `using` churn in `src/Ui`.

**R-emcli-5. The `em` verb takes a `.cem` and needs no other arguments.** Both paths it must resolve
already have a defined rule, and the rule is a WALK-UP, not a flag:

- The layout: `EmSetup.LayoutRef` is relative to the **workspace root** — the nearest ancestor `.cws`
  walking up from the file — and absolute when it names something outside. `WorkspaceViewModel`
  already falls back to the `.cem`'s own directory when there is no workspace, so a loose `.cem`
  beside its `.clay` is already specified behaviour, not a new case.
- The technology: `ResolveTechFor` resolves against **the layout's own parent workspace**, found by
  walking up from the `.clay` (brief-foreign-documents R-fgn-3), never against "the current
  workspace". Headless there is no current workspace, so the rule applies unchanged.

An explicit `--workspace <path.cws>` override is optional and easy; do not make it required.

**R-emcli-6. The verb follows `docs/design/cli.md` §3.** Results and the summary to stdout; progress,
warnings and the engine's own notes to stderr. `EmRunResult` already separates `Notes` / `Warnings` /
`Errors` by what the reader is expected to DO about each — print all three, keeping that distinction,
rather than flattening them into one list. Flattening them is the exact defect the three-list split
was introduced to fix.

**R-emcli-7. `-o <path>` names the Touchstone; with none, the run writes where the GUI writes.**
`EmRunService.ResolveSnpPath` is a predictable path by design, so a schematic's SnP reference stays
valid across re-runs. **A headless run must not mint a different filename** — the whole point is that
`circuitrf em` and Simulate produce the same file. `-o` overrides the destination only.

**R-emcli-8. Refusals stay refusals.** `EmRunStatus` distinguishes `Refused` / `NoLayout` /
`EngineError` / `Cancelled`, and each carries a written explanation. Print the explanation and exit
non-zero; do not collapse them into "EM failed". Suggested mapping: `Ok` → 0, `Refused`/`NoLayout` →
1, `EngineError` → 1, `Cancelled` → 130.

## 4. Gates

1. `dotnet build` clean; `dotnet test tests/Firewall.Tests` green **with the new assembly asserted**.
2. `dotnet test` green at the repo root. The layout/EM tests in `tests/Ui.Tests` must pass unchanged —
   they are the evidence that the move was mechanical. **A test that needed editing to keep passing is
   a finding**: say what changed and why, in `src/Ui/Layout/Em/RESOLVED.md`.
3. `circuitrf em` on an existing `.cem` from a real workspace produces a `.sNp` **byte-identical** to
   the one the GUI's Simulate writes for the same setup. This is the acceptance test and it is worth
   more than any unit test in this brief: it proves the move changed no physics.
4. `docs/design/cli.md` §8 replaced with the verb's actual behaviour, and its §2 table gains a row.

## 5. Explicitly out of scope

- **Back-annotation.** `EmBackAnnotation` writes an SnP component into a schematic; that is an editor
  operation and stays in `src/Ui`.
- **The `.cem` editor.** `EmSetupEditorViewModel` (1,966 lines) and `EmSetupDocument` are UI and stay.
  `EmLayoutSource` is a four-field record that happens to be declared in that file — move the record,
  not the file.
- **Making the `em` verb create or edit a `.cem`.** It runs one. A setup with no ports, no technology
  or no signal conductor is REFUSED with the sentence the run service already writes; teaching the CLI
  to fix it is a different brief.
- **Anything under `src/Engine/Mom`.** It is already where it needs to be. If this change requires
  editing the MoM engine, something has gone wrong — stop and report.

## 6. How to verify the closure claim yourself

The measurement above is reproducible in about two minutes and is worth re-running before starting,
since `src/Ui` moves: build a throwaway project referencing `Core` + `Engine` + `RfCore` with
`EnableDefaultCompileItems=false`, `<Compile Include>` the `Layout/Em/*.cs` files (excluding the four
UI ones named in §5), and add files as the compiler names them. It terminates. **Do not add whole
folders when it names one type** — that is what turned a 4-file gap into a spurious wBond-and-DRC
dependency the first time this was measured.
