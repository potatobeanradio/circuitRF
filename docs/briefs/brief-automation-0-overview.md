# Sonnet Brief — Automation surface: overview, findings and decisions

**Read this first; it is the map for `brief-automation-1-structured-output.md`,
`-2-schematic-below-the-firewall.md`, `-3-authoring-verbs.md`, `-4-check-and-explain.md` and
`-5-protocol-adapter.md`. It contains no work of its own.** It records what the tree says today and
fixes the decisions the five implementation briefs depend on. The architecture these serve is
`docs/design/automation-architecture.md` (R-aut-1 … R-aut-13); this brief does not restate it.

Every claim below was read out of the tree. File and line references are given so a later reader can
check them rather than trust them.

---

## 1. The goal, stated so it can be tested

circuitRF should be **fully drivable without a display** — author, validate, run, read back — by a
shell script, by CI, by the documentation factory, or by an automated design agent. The measure is
not "has an API"; it is:

> Every operation a person can perform in the GUI that changes a document or produces a result has a
> headless equivalent that produces the **same bytes**.

`tests/Ui.Tests/Em/EmCliVerbTests.cs` already holds exactly that standard for one verb — it compares
the CLI's `.sNp` byte for byte against `EmRunService.Run`'s, exempting only the provenance
write-timestamp. That test is the pattern every brief in this series is measured against.

---

## 2. What already works, and what actually blocks the rest

### 2.1 The capability layer is most of the way there

Every document format circuitRF owns is plain, readable, culture-invariant JSON
(`docs/design/project-file-formats.md`, `FormatCultureInvarianceTests`), and the readers and writers
for nearly all of them are already below the UI firewall:

| Format | Persistence | Project | Headless today |
|---|---|---|---|
| `.cws` | `WorkspacePersistence` (`Serialize`/`SaveToFile`/`SaveToFileAtomic`/`LoadFromFile`) | `src/Design/Workspace` | yes |
| `.ccell` | `CellPersistence`; folder creation via `CellFolder.CreateCellFolder` (`CellFolder.cs:103`) | `src/Design/Cells` | yes |
| `.clay` | `LayoutPersistence` | `src/Design/Layout` | yes |
| `.ctech` | `TechPersistence` | `src/Design/Layout` | yes |
| `.cem` | `EmSetupResolver` + `EmRunService` | `src/Design/Layout/Em` | yes |
| `.cnl` | `CnlReader` / `CnlWriter` | `src/Core/Netlist` | yes |
| GDSII/DXF/Gerber/Excellon/`.kicad_pcb` | `src/Design/Layout/Interchange` | `src/Design` | yes |
| Touchstone/`.npy`/`.mat`/`.spl`/`.lpcwave` | `src/RfCore/Export`, `src/RfCore/Loadpull` | `src/RfCore` | yes |
| **`.csch`** | `SchematicPersistence` (`src/Ui/Schematic/SchematicPersistence.cs:277`) | **`src/Ui`** | **no** |
| **`.csym`** | `SymbolPersistence` (`src/Ui/Schematic/SymbolPersistence.cs:67`) | **`src/Ui`** | **no** |

### 2.2 The schematic gap is a packaging accident, not a coupling

This is the finding that reshapes the whole series, and it was measured rather than assumed.

`src/Ui/Schematic` holds **105 `.cs` files**; 104 declare `namespace CircuitRF.Ui.Schematic`.
Grepping the whole folder for a UI-framework reference (`^using Avalonia`, `Avalonia.`, `SkiaSharp`,
`CommunityToolkit`, `ObservableObject`) returns **two files**, and on inspection:

- `AutoSymbolGenerator.cs` — matches only its own header comment, which says it is framework-free.
- `PlacementService.cs:1` — `using CommunityToolkit.Mvvm.ComponentModel;`, and it is an
  `ObservableObject`. A genuine view-model service; it stays in `src/Ui`.

**Nothing in the folder references Avalonia at all.** `SchematicPersistence.cs`,
`SymbolPersistence.cs`, `SchematicDocument.cs`, `SymbolModel.cs`, `SymbolGeometry.cs` and
`NetExtractor.cs` (2,274 lines) each carry a header stating they are framework-free, and the greps
agree.

The coupling that does exist runs the other way and is small: across all 105 files there are **20
`using` lines reaching other `CircuitRF.Ui.*` sub-namespaces** — `ViewModels` (4), `Commands` (4),
`WBond` (3), `Layout` (3), `Messages` (2), and one each of `Theming`, `Layout.PCells`, `DataDisplay`,
`Archive`.

**So AUT-2 is a project move, not a decoupling** — the same operation `src/Design` performed for the
layout side in 2026-08 (`brief-cli-em-verb.md` R-emcli-1/R-emcli-4), and the `GlobalUsings.cs`
mechanism that absorbed that move's `using` churn already exists and is documented as the map.

### 2.3 The firewall bans exactly one thing

`tests/Firewall.Tests/UiFirewallTests.cs:73` — `IsUiFramework` is
`assemblyName.StartsWith("Avalonia")`. SkiaSharp is explicitly allowed (`:70`), and
`CommunityToolkit.Mvvm` is **not** caught by the test. That is not a licence to move MVVM types
below the wall: R-aut-4 draws the line at document model, serializer and pure functions, and
`PlacementService.cs` is on the far side of it regardless of what the gate would tolerate.

### 2.4 The output side is prose, and the structure to fix it already exists

- `src/Cli/Program.cs` is 1,740 lines with hand-rolled switch-based argument parsing and
  `Console.WriteLine` printers (`PrintHbDataSet:1452`, `PrintCube:1489`, `PrintLoadpullGrid:856`,
  `PrintPursuitOptima:811`). There is **no structured output form of any result**.
- `src/Diagnostics` is a firewall-gated project of its own whose `Diagnostic` record already carries
  a stable dotted id, typed arguments and an English default template — its header lists filtering,
  grouping, deduplication and robust assertion as the motivating reasons, all four of which an
  automated client needs.
- **Adoption is thin: 6 construction sites in 2 files** (`src/Design/Layout/Em/EmDiagnostics.cs`,
  `src/Diagnostics/FileAccessDiagnostics.cs`). Everywhere else, a failure crosses the wall as an
  already-finished English sentence, and the CLI calls `Render()` and discards even what structure
  there was.

### 2.5 Workspace creation is the one authoring path still trapped in a view model

`WorkspaceViewModel.NewWorkspace` (`:1292`) does four things after its dialog returns: create the
directory, build a `CwsFile`, optionally copy a shipped technology's raw bytes into `<ws>/tech/` and
point `DefaultTechRef` at it, then `WorkspacePersistence.SaveToFileAtomic`. Three of the four are
already below the wall. The fourth, `ShippedTechnologies` (`src/Ui/Layout/ShippedTechnologies.cs`,
102 lines), is framework-free by explicit design — it uses plain .NET `EmbeddedResource` rather than
Avalonia's `AssetLoader` precisely because `AssetLoader.Open` throws with no live platform — but its
resources are embedded in the `CircuitRF.Ui` assembly.

Cell-folder creation, by contrast, is already headless: `CellFolder.CreateCellFolder` is in
`src/Design`, and both `ComponentImport.cs:174` and `SubcircuitCellBuilder.cs:790` call it.

---

## 3. Decisions the five briefs depend on

**R-aut0-1. Order is: AUT-1, AUT-2, AUT-3, AUT-4, AUT-5.** AUT-1 is independent of the rest and can
land alone. AUT-3 and AUT-4 depend on AUT-2. AUT-5 depends on all four and must add no capability of
its own.

**R-aut0-2. No brief in this series changes numeric behaviour.** Nothing under `src/Engine`,
`src/RfCore` or the analyses is touched. A `.cnl` that produced a number before must produce the same
number after, byte for byte through the existing exporters.

**R-aut0-3. Structured output is additive, never a replacement.** The existing human tables, the
stdout/stderr split (`cli.md` §3.1), the English-forever rule (§7A) and the per-verb exit-code rules
(§7) all survive unchanged. A script parsing stderr today must not notice any of this landing.

**R-aut0-4. The `src/Ui/Schematic` move keeps the file set intact.** AUT-2 moves whole files and
renames namespaces; it does not refactor, re-shape or split types. A move that also improves
something is a move nobody can review.

**R-aut0-5. Authoring verbs are scaffolding, not an editing API.** Per R-aut-5, AUT-3 adds verbs that
produce a correct *initial* document — a workspace skeleton, a cell folder, an imported part. It adds
no per-primitive edit verb, and the answer to "how do I add an instance" stays "write the document".

**R-aut0-6. Every new verb is gated by a byte-identity test against the GUI's own path**, following
`EmCliVerbTests`. Where the GUI path cannot be invoked headlessly at all, that is itself the finding
to report, not a reason to weaken the gate to a tolerance or a spot check.

**R-aut0-7. `--json` is the spelling.** One flag name across every verb, one schema shape, one
document per invocation on stdout. Not `--format json`, not per-verb variants.

---

## 4. What this series does not do

- **No remote control of the running GUI** (R-aut-11). No command channel, no socket into a live
  `WorkspaceWindow`.
- **No embedded scripting language** (R-aut-12).
- **No per-primitive edit verbs** (R-aut0-5).
- **No change to the GUI's own project-tree refresh model.** It re-scans on window focus and on an
  explicit Refresh, deliberately with no `FileSystemWatcher` (`ProjectTreeTool.cs:16`, `:431`).
  Whether that should tighten is a project-tree question and is out of scope here.
- **No new file format.** Every capability reads and writes the formats that already exist.

---

## 5. On completion of the series

Write the findings to the relevant `RESOLVED.md` — `src/Cli` has none today, so create one there;
`src/Ui/RESOLVED.md` and `src/Design/RESOLVED.md` already exist. **Never write findings to a
`CLAUDE.md`.** Update `docs/design/cli.md` (§2's verb table, §9) and
`docs/design/automation-architecture.md` (§3's status table) as each brief lands. The verb list in
the repo-root `CLAUDE.md` is standing project memory — flag the one-line addition for the owner
rather than editing it as part of a findings write-up.
