# Brief EMI-1 — The 3D solver seam: one adapter per solver, Palace and openEMS moved onto it unchanged

**Tag:** `R-emi1-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** nothing
**Area:** `src/Design/Layout/Em/EmSetupModel.cs` (`Em3dSolver`), `src/Design/Em3d/` (`Em3dRunService`,
`SolverDiscovery`, `Em3dFidelityReport`, the Palace and openEMS files), `src/Design/ThreeD/C3dSolveStatus.cs`,
`src/Engine/Em3d/Em3dSolverGuidance.cs`, `src/Cli` (`em`, `solver`, `explain`), the MCP `run`/`solver` tools, and the
UI sites that branch on the solver. Findings in `src/Design/RESOLVED.md`.
**Out of scope:** EMerge itself (EMI-2…), Compare's new behaviour (EMI-6). **This brief changes no result.**

---

## 0. Why

`Em3dSolver { None, Palace, OpenEms, Both }` is matched in 96 places across 23 files. Most matches answer one of a few
questions — *does this setup run Palace?*, *can this solver do eigenmodes?*, *what is its token in a result path?*,
*what is its display name?* — by listing enum values. A third solver would mean editing every list. The owner wants
each solver integration to be a contained piece of work, so the questions get one owner each.

## 1. `R-emi1-1` — what a solver is

**R-emi1-1a — an identity.** `Em3dSolverId`: a small value type (or a closed enum without `Both`), with `Palace`,
`OpenEms`, and room for `EMerge` (added in EMI-2). The **setup's** choice becomes `Em3dSolverChoice`: either one
solver or `Compare(IReadOnlyList<Em3dSolverId> solvers, Em3dSolverId reference)` (EMI-6 fills Compare in; here it only
has to represent today's `Both` as `Compare([Palace, OpenEms], Palace)`). `None` stays representable (a planar
setup).

**R-emi1-1b — a descriptor per solver**, one object, in `src/Design/Em3d/Solvers/<Name>/`:

| Member | Today it is |
|---|---|
| `Id`, `DisplayName`, `ResultToken` (`palace`, `openems`) | `Em3dRunService.SolverToken`, `Name`, string literals in UI |
| `Method` (`Fem`, `Fdtd`) | `Em3dSolverFit`, `C3dSolveStatus.SolverKind` |
| `Capabilities`: problem types, port kinds, boundary kinds, primitives it can lower, whether it reports fields / a radiation pattern / SAR | `StaticSolverRefusal`, `PalaceOnlyRefusal`, `TerminalPortRefusal`, `OpenEmsWavePortRefusal`, the SAR checks in `WorkspaceViewModel` and `C3dEditorViewModel.PhoneSar` |
| `Tools` it needs found (Palace: Palace, Gmsh, MPI launcher; openEMS: openEMS) | `SolverTool` and the readiness code in `SolverDiscovery` |
| `Refusal(setup, problem)` → the solver's own sentence or null | the refusals above |
| `Prepare(problem, setup, readiness, …)` → a plan or a stop | `PreparePalace`, `PrepareOpenEms` |
| `Execute(plan, …)` → a `Leg` | `ExecutePalace`, `ExecuteOpenEms` |
| `ComparisonFacts(leg)` → what makes a difference expected (EMI-6) | `Em3dComparisonFacts`, openEMS-only today |
| `MemoryVerdict(...)` (optional) | `PalaceMemoryVerdict` |

Exact shape is the implementer's. Prefer plain records + static functions over an interface hierarchy if it reads
better beside the surrounding code. **The test is the questions in §0 each have one owner.**

**R-emi1-1c — a registry** listing the descriptors in a fixed order (Palace, openEMS, then later ones). It is the only
list of solvers in the code. UI combo boxes, the CLI's `--solver` parser, `solver list` and the MCP `solver` tool
enumerate it.

## 2. `R-emi1-2` — move the call sites

**R-emi1-2a.** Replace each `Em3dSolver.X or Em3dSolver.Both` match with the question it is asking: `choice.Runs(id)`,
`choice.Solvers`, `descriptor.Capabilities.Eigenmode`, `descriptor.ResultToken`, … Grep for `Em3dSolver\.` and leave
none outside the format reader/writer (R-emi1-3) and the registry.

**R-emi1-2b — `Em3dRunService.Run` dispatches through the registry.** `RunBoth` becomes "run each chosen solver in
order, then compare" with the list being `[Palace, OpenEms]` for now; its notes, its stage names ("Palace, the first
of two solvers") and its failure sentences must come out **identical** for that list. EMI-6 generalises the wording.

**R-emi1-2c — capability refusals keep their sentences.** `StaticSolverRefusal`, `PalaceOnlyRefusal` and friends
become the descriptors' `Refusal`, worded exactly as today. Their tests are the gate.

**R-emi1-2d — guidance.** `Em3dSolverGuidance` favours a *method* (FEM/FDTD), not a program. Leave its rows as they
are; where a sentence names "Palace" as the FEM answer, it should name the FEM solvers that are installed (EMI-2 makes
that more than one). Not before EMI-2 — here it stays byte-identical.

## 3. `R-emi1-3` — the file format does not change

The `.cem`/`.c3d` key `Solver3D` keeps its spellings (`Palace`, `OpenEms`, `Both`). The reader maps them to
`Em3dSolverChoice`; the writer writes them back **unchanged** (a file opened and saved with no edit is byte-identical).
EMI-6 adds the Compare spelling.

## 4. Tests (targeted classes only)

1. **The gate is the existing suite for the touched classes, unchanged:** every Palace and openEMS writer golden, the
   refusal sentences, `EmCliVerbTests`' byte identity, the Both-run tests. Name the classes run in the completion note.
2. One source-scan test: no `Em3dSolver.Palace`/`OpenEms`/`Both` (or the new ids) matched outside the registry, the
   descriptors and the format reader/writer. Comment-stripped, as `AuthoringCliVerbTests` does.
3. One test: the registry enumerates Palace then openEMS, and every descriptor's `ResultToken` is unique.
4. Format round trip: `Palace`, `OpenEms`, `Both` read and written back byte-identically.
5. `tests/Firewall.Tests`.

## 5. Completion

Record in `src/Design/RESOLVED.md`: the descriptor's members, where each moved from, and **how to add a solver** (the
folder, the registry line, the recipe, the docs page section) and **how to remove an integration** (delete the folder
and its recipes; a setup naming that solver reads as an unknown solver and is refused with the list of known ones).
That section is the point of the brief: EMI-2 follows it, and so does any solver integrated after EMerge.
