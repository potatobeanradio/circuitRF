# Sonnet Brief — AUT-2: The schematic and symbol model below the UI firewall

**Read `brief-automation-0-overview.md` first.** It records the findings this brief is built on —
in particular §2.2, the measurement that makes this a project move rather than a decoupling — and
fixes R-aut0-1 … R-aut0-7, which it does not repeat. The architecture is
`docs/design/automation-architecture.md`; R-aut-3 and R-aut-4 are what this brief implements.

**Scope: moving code, and nothing else.** No behaviour change, no refactor, no new capability, no new
verb. Every editor, canvas, session and view model stays exactly where it is. This is the enabling
brief for AUT-3 and AUT-4, and it is worth doing on its own merits regardless of what is built on
top of it.

**The precedent is exact:** `brief-cli-em-verb.md` R-emcli-1/R-emcli-4 carved `src/Design` out of
`CircuitRF.Ui` in 2026-08 for the same reason, and the machinery that absorbed that move's `using`
churn — `src/Ui/GlobalUsings.cs`, which documents itself as the map — already exists.

---

## 1. What this brief makes true

After it, this compiles and runs in a project that references no UI framework:

```
read a .csch  →  SchematicEditModel  →  NetExtractor.Extract  →  TestBench + Library
              →  CnlWriter.Write     →  a .cnl the existing verbs already run
```

Today the first three steps are in `CircuitRF.Ui` and the last is in `CircuitRF.Core`, so the chain
cannot be assembled without Avalonia on the path. `src/Ui/Diagnostics/Fixtures/DocRunData.cs:126`
already performs exactly this chain — inside `src/Ui`, which is why the documentation factory needs
the GUI assembly to produce a netlist.

**No user-visible behaviour changes.** A schematic that opened, extracted and simulated before must
do all three identically after.

---

## 2. What moves, and how the set is decided

**R-aut2-1. The set is the transitive closure of the chain in §1, and it is computed, not guessed.**
Start from `SchematicPersistence.LoadFromFile`, `SchematicEditModel`
(`EditableSchematic.cs:1182`), `NetExtractor.Extract` (`NetExtractor.cs:75`) and
`SymbolPersistence`, and take everything they need. Let the compiler decide the boundary: move the
closure, build, and move what the build then asks for. A hand-picked list will be both too large and
incomplete.

**R-aut2-2. What must NOT move**, regardless of what the closure suggests:

| Stays in `src/Ui` | Because |
|---|---|
| `PlacementService.cs` | An `ObservableObject`; the only file in the folder that references a UI-framework package at all (overview §2.2) |
| Anything with a canvas, viewport, hit-test-for-input, drag, undo stack or edit session | R-aut-4: editors stay in `src/Ui` |
| `SchematicSessionRegistry`, `RecoveryManager`, `TemplateManager`, the `*Document` view-model types, the `*DragPayload` types | Editor and shell state |
| `VerilogACompilerInstaller`, `PdkPartInstaller` | Installation UX |

If the closure pulls one of these in, **that is the finding** — report the exact dependency rather
than moving it or writing a shim to dodge it. It means a document-model type is reaching into editor
state, which is a real problem worth naming.

**R-aut2-3. Whole files move; nothing is split, renamed or reshaped.** R-aut0-4. A move that also
improves something is a move nobody can review. Improvements go in a follow-up commit, after the
move is green.

**R-aut2-4. Where they land.** A sibling of the existing design-layer artifact folders:

```
src/Design/Schematic/      the .csch document model, its persistence, geometry, net extraction
src/Design/Symbol/         the .csym model, its persistence, its geometry
```

`src/Design` is already described in the repo-root `CLAUDE.md` as the design-layer DOCUMENT artifacts
project — layout model, technology model, cell folder format, `.cem`. The schematic and symbol
documents are the same kind of thing and have simply been on the wrong side of the wall.

**R-aut2-5. The namespace changes with the project.** `CircuitRF.Design.Schematic`,
`CircuitRF.Design.Symbol`. R-emcli-4 settled this for the layout move and the reasoning carries
verbatim: `CircuitRF.Ui.Schematic` inside a non-UI assembly would lie about the architecture forever.

**R-aut2-6. Absorb the `using` churn in `GlobalUsings.cs`, not across the tree.** Add the two new
namespaces there, with a comment in the style of the existing entries. That file already states the
rule this follows — one line per namespace, said once — and warns that adding to it is not free, so
add exactly two.

---

## 3. The coupling to fix, measured

Across all 105 files in `src/Ui/Schematic` there are **20 `using` lines reaching other
`CircuitRF.Ui.*` sub-namespaces, in 14 files**:

```
CellParameterEditorDocument   CellUsageScanner       MicrostripSubstrateInjection   MoveRefRegistry
NetExtractor                  RunResultsWriter       SchematicDocument              SchematicSessionRegistry
SymbolEditorDocument          TreeDropIntent         VerilogACompilerInstaller      WBondPlacement
WBondSymbolGenerator          WorkspaceCopy
```

by target: `ViewModels` (4), `Commands` (4), `WBond` (3), `Layout` (3), `Messages` (2), and one each
of `Theming`, `Layout.PCells`, `DataDisplay`, `Archive`.

**R-aut2-7. Each of these is resolved by one of exactly three moves, and the choice is recorded per
file:**

1. **The file stays in `src/Ui`** — it is editor or shell code and was never part of the closure.
2. **The dependency also moves**, because it is itself a design-layer artifact
   (`CircuitRF.Ui.Layout` types are largely already `CircuitRF.Design.Layout` and reached through
   `GlobalUsings`; check before assuming a real dependency).
3. **The dependency inverts** — the moved type takes an interface or a callback and `src/Ui` supplies
   the implementation. `ICellResolver` is the existing precedent in this very folder, and
   `LayoutTextOutline.TypefaceSource` (`src/Design`, set by a module initializer in `src/Ui`) is the
   precedent for a settable hook across the wall.

**Prefer 1 and 2. Reach for 3 only when the type genuinely belongs below the wall and genuinely needs
something above it**, and say so explicitly in the write-up. Three inversions is a design; thirteen is
a signal that the boundary was drawn in the wrong place.

`NetExtractor` is the one to look at first — it is 2,274 lines, it is the centre of the chain in §1,
and it currently reaches another `CircuitRF.Ui` namespace.

---

## 4. Two specific traps

**R-aut2-8. `ShippedTechnologies` resources are embedded in the `CircuitRF.Ui` assembly.** If the
closure reaches `src/Ui/Layout/ShippedTechnologies.cs` (102 lines), note that the class is
framework-free by explicit design — it uses plain .NET `EmbeddedResource` rather than Avalonia's
`AssetLoader`, because `AssetLoader.Open` throws with no live Avalonia platform — but **the embedded
resource items live in `src/Ui/CircuitRF.Ui.csproj`**. Moving the class without moving the
`EmbeddedResource` entries produces a class that compiles, loads no technology, and reports nothing.
AUT-3 needs this class; if it is not in this brief's closure, leave it and let AUT-3 move it.

**R-aut2-9. `src/Ui` has three entry points.** The main app plus the two standalone binaries
(harmonicaRF and wBond) are all `src/Ui` with a different `Main` (repo `README.md`, and the firewall
test's own comments on `CircuitRF.Harmonica`/`CircuitRF.WBond`). A module initializer, a static
constructor or a resource lookup that runs on one path but not the others is the failure mode this
has already produced once — `UiTypefaceInstaller` exists as a module initializer for precisely that
reason. Anything installed from `src/Ui` into a moved type must be installed on all three paths.

---

## 5. Gates

1. **`tests/Firewall.Tests` passes with `CircuitRF.Design` still in `NonUiAssemblies`.** This is the
   real gate and it is already written — the move either keeps `CircuitRF.Design.dll` free of
   `Avalonia*` references or it does not.
2. **A new test that assembles the §1 chain in a project that does not reference `src/Ui`**: read a
   fixture `.csch`, extract, write a `.cnl`, and assert the `.cnl` is **byte-identical** to what
   `DocRunData.cs:126` produces for the same input today. That byte identity is the whole point —
   `EmCliVerbTests` set the standard (R-aut0-6) and this is the schematic-side equivalent.
3. **`dotnet test` full solution, green.** This is a phase boundary, so the full run is warranted
   (~4 min at the repo root; `Engine.Tests` is ~3.5 min of it). Run it **once** and read
   `tests/*/TestResults/last-run.trx` for any failure — the console tail only shows the last project
   to finish.
4. **`Ui.Tests` unchanged in count and outcome.** A moved file whose tests moved with it is fine; a
   test that disappeared is not.
5. **All three binaries still launch**: `dotnet run --project src/Ui`, and the harmonicaRF and wBond
   standalone entry points (R-aut2-9).

---

## 6. On completion

Write the findings to `src/Design/RESOLVED.md` and `src/Ui/RESOLVED.md` — **never to a `CLAUDE.md`**.
Update `docs/design/ui-architecture.md` with the moved boundary and
`docs/design/automation-architecture.md` §3's status table (the last two rows change to "yes").
`docs/design/schematic-hierarchy-navigation.md`, `net-extraction-and-run.md` and `symbol-editor.md`
name types that are moving — update their references.

**Report, do not silently absorb:**
- The final file list, and for each of the 14 coupled files which of R-aut2-7's three resolutions was
  used.
- Anything R-aut2-2 forbade moving that the closure nonetheless required, with the exact dependency.
- Any dependency inversion introduced, and why the type could not simply stay above the wall.
