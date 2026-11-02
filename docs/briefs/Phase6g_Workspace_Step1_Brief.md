# Phase 6g — Workspace Step 1: `.ccell` model + cell folder layout + NameValidator (Claude Code / Sonnet)

The first, self-contained step of the workspace/project-tree work: the **`.ccell` file model + read/write**,
the **cell folder layout** helper (`schematic/`/`symbol/`/`layout/` subfolders + primacy resolution), and a
**`NameValidator`** for the cross-platform-safe character set. **This brief is ONLY step 1.** No Project Tree
UI, no cell reference model, no cell-parameter editor, no scanning UI — those are steps 2–7. Everything here is
**framework-free model + persistence + helpers**, unit-testable with no GUI. Read `workspace-and-project-tree.md`
§1/§2 first (the authority). Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `docs/design/workspace-and-project-tree.md` §1 (on-disk structure), §1.4 (naming charset), §2
> (`.ccell` contents + primacy resolution rules); `docs/design/project-file-formats.md` (refinement notes,
> serialization conventions). Context code to **mirror**: `src/Ui/Schematic/SymbolPersistence.cs` (the
> `.csym` System.Text.Json pattern — enum-as-string, format_version reject-on-mismatch, `Id` never persisted,
> `CurrentFormatVersion`, `_jsonOpts` — copy this structure exactly for `.ccell`), `src/Ui/Schematic/
> WorkspacePersistence.cs` + `CwsFile` (the workspace-file pattern), `src/Ui/Schematic/EditableSchematic.cs`
> (`EditableParameter` — Name/Expression/Unit/Dimension/ShowOnSchematic; `.ccell` parameters mirror this
> shape; `UnitDimension`). Design docs win on any conflict.

## The spine (do not violate)
- **Framework-free** — `.ccell` model, persistence, primacy resolution, and `NameValidator` are plain C#
  (no Avalonia, no Skia). They live in `src/Ui/Schematic/` alongside the other persistence (the schematic
  model layer is framework-free by the firewall rule). Unit-testable headless.
- **Mirror `SymbolPersistence.cs` conventions exactly:** System.Text.Json, `JsonStringEnumConverter`,
  `WhenWritingNull`, `format_version` reject-on-mismatch (alpha — no migration), **`Id` never persisted**.
- **The filesystem is truth** — these helpers *read* structure; they do not maintain a separate membership
  list. Primacy resolution reads the folder + `.ccell` per §2's rules.
- **Scope fence (step 1):** model + persistence + folder helper + name validator. NO tree UI, NO scanning of
  a whole workspace, NO cell reference model, NO editor. Just the cell-level building blocks.

---

## LAYER 1 — `NameValidator` (cross-platform-safe charset)

`src/Ui/Schematic/NameValidator.cs` (framework-free, static). Per `workspace-and-project-tree.md` §1.4:
- `bool IsValid(string name)` / `string? Validate(string name)` (returns null if valid, else a clear reason).
- **Reject:** the characters `< > : " / \ | ? *`, control chars (0x00–0x1F), empty/whitespace-only, names
  **ending in space or dot**, and the Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`,
  `COM1`–`COM9`, `LPT1`–`LPT9`, case-insensitive, with or without an extension).
- This validates a single path **component** (a workspace/library/cell/view name), not a full path — no
  slashes are allowed (they're in the reject set).
- Used later at create/rename in the tree; here it's just the validated helper + tests.

**Layer 1 gate:** `NameValidator` compiles; unit tests cover valid names, each disallowed-character class,
trailing space/dot, reserved names (case-insensitive), and empty. Report.

---

## LAYER 2 — `.ccell` model + persistence (mirror `SymbolPersistence`)

`src/Ui/Schematic/CellPersistence.cs` (+ the `CcellFile` model), copying `SymbolPersistence.cs`'s structure:
1. **`CcellFile`** (framework-free):
   - `int FormatVersion` (reject-on-mismatch).
   - `List<CcellParameter> Parameters` — each `CcellParameter { string Name; string DefaultExpression;
     string Unit; UnitDimension Dimension; bool ShowOnSchematic; }` (mirrors `EditableParameter`'s persisted
     shape, but holds the **default** expression — it's the cell's declared interface, not an instance value).
   - `string? PrimarySchematic`, `string? PrimarySymbol`, `string? PrimaryLayout` — each a **filename
     relative to the view sub-folder** (e.g. `"amp.csym"`), nullable (null = none chosen).
   - `bool IsTestBench`.
   - (`Id` never persisted, per policy.)
2. **`CellPersistence`** with `Serialize`/`Deserialize`/`SaveToFile`/`LoadFromFile`, `CurrentFormatVersion`,
   the same `_jsonOpts` (enum-as-string, `WhenWritingNull`, case-insensitive), and the same
   format_version-mismatch `InvalidDataException` with a clear "regenerate" message.
3. A round-trip unit test (parameters + all three primaries + IsTestBench) and a format_version-mismatch test.

**Layer 2 gate:** `.ccell` round-trips losslessly; format_version mismatch rejected with a clear error;
framework-free. Report.

---

## LAYER 3 — cell folder layout + primacy resolution helper

`src/Ui/Schematic/CellFolder.cs` (framework-free; pure path + filesystem-read logic, no GUI):
1. **Layout constants/helpers:** the three view sub-folder names (`schematic`, `symbol`, `layout`) and helpers
   to build their paths under a cell folder. A `CreateCellFolder(parentDir, cellName)` that makes the cell
   folder + the three (empty) sub-folders + an initial `.ccell` (validating `cellName` via `NameValidator`
   first; throw/return error on invalid). `layout/` is created empty (v1 unused).
2. **Primacy resolution** (`workspace-and-project-tree.md` §2) — given a cell folder, resolve the primary
   file for each view type per the exact rule order:
   - **Sole-file-implies-primary:** exactly one file in the sub-folder ⇒ that file is primary (ignore
     `.ccell`'s field).
   - **Named primary present:** `.ccell` names a primary that exists ⇒ it is primary.
   - **Named primary MISSING:** `.ccell` names a primary that is absent ⇒ return a **distinct "contradiction"
     result** (e.g. `PrimaryResolution { State = MissingNamedPrimary, MissingName = "..." }`) so the caller
     (the tree, later) can flag System.Warning with a reason. Do NOT silently treat as "no primary".
   - **No primary, multiple files:** multiple files, `.ccell` names none ⇒ `State = NoPrimary` (normal, not
     an error).
   - **Empty sub-folder:** `State = NoView` (normal — cell being authored).
   Return a small result type per view (`PrimaryResolution` with a `State` enum +
   the resolved filename when present + the missing name when contradictory). This is the single source of
   primacy truth the tree and the cell-reference model (step 5) will both call.
3. Tests for **every** branch of the resolution rules (sole-file, named-present, named-missing-contradiction,
   no-primary-multiple, empty), per view type independently.

**Layer 3 gate:** `CreateCellFolder` makes the correct structure with a valid `.ccell`; primacy resolution
returns the right state for all five branches per view type; `NameValidator` gates cell creation. Report.

---

## Acceptance (step 1)
1. `NameValidator` enforces the cross-platform-safe charset (§1.4), with tests for each reject class.
2. `.ccell` (`CcellFile` + `CellPersistence`) round-trips parameters + per-type primaries + IsTestBench,
   mirroring `SymbolPersistence` conventions (enum-as-string, format_version reject, `Id` not persisted).
3. `CellFolder` creates the cell folder + `schematic/`/`symbol/`/`layout/` subfolders + initial `.ccell`, and
   resolves primacy per §2's five-branch rule (including the distinct missing-named-primary contradiction
   state) for each view type, with tests for every branch.
4. `dotnet build`/`dotnet test` green; firewall green (all framework-free, in `src/Ui/Schematic/`); **no tree
   UI, no whole-workspace scan, no cell reference model, no editor** (steps 2–7); nothing else regresses.

## Guardrails
- **Framework-free** — model/persistence/helpers carry no Avalonia/Skia; headless-testable.
- **Mirror `SymbolPersistence.cs`** — same JSON conventions, format_version reject, `Id` not persisted; don't
  invent a different serialization style.
- **Primacy resolution is the single source** the tree + cell-reference model will reuse — implement the five
  branches exactly per §2, and keep the **missing-named-primary contradiction** a *distinct* state (it drives
  the System.Warning surfacing later; don't fold it into "no primary").
- **Scope fence:** cell-level building blocks only — no tree, no scan, no reference model, no editor.
- Sub-gate the three layers; report and stop between each; don't run the full suite into the output limit.
- Update `workspace-and-project-tree.md` §8 status (step 1 done) and note in `src/Ui/CLAUDE.md` that `.ccell`/
  `CellFolder`/`NameValidator` exist and that primacy resolution is centralized in `CellFolder`.

*Exit: the cell-level building blocks exist and are tested — `.ccell` read/write, the cell folder layout with
correct primacy resolution (including the contradiction state), and the cross-platform name validator — the
foundation the filesystem scan (step 2) and Project Tree (steps 3–4) build on, with no GUI yet.*
