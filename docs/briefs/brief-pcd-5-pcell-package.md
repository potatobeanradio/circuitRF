# Brief PCD-5 — The designed cell as a PCell: generator, resolver, package, check

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd5-n`
**Area:** `src/Design/PCellDesigner/` (`PCellDesign`, `DesignedPCellResolver`, `PCellPackage`, `PCellDesignCheck`),
`src/Ui/Layout/LayoutToSchematicGenerator.cs`
**Depends on:** PCD-0, PCD-1's seams commit, PCD-2's `DesignedCellLayout.At` (real from the seams commit), PCD-3's
`MSHAPE` for the handles. PCD-6 and PCD-7 call `Create`, `Export`, `Import` and `PCellDesignCheck.Run`.

---

## 0. What this adds

A designed cell is placed through the same `IPCellGeneratorResolver` seam as a Python or gdsfactory kit's cell
(PCD-0 S3), so the layout palette, the parameter dialog and Properties, the handles, regeneration, Update Schematic
from Layout, LVS and EM reach it with no designer branch. Its generator is the planner at the instance's values
(`DesignedCellLayout.At`), flattened. The `.cpcell` package carries the cell folder to another workspace.

## 1. Requirements

- **`R-pcd5-1` — `PCellDesign.Create`:** `CellCreate.Create` with schematic and symbol views, then the `.ccell`'s
  `PCellDesign`, `LayoutGenerator = "cpcell:<Name>"` and `EmCopper = true` (D-cell).
- **`R-pcd5-2` — `DesignedPCellResolver`:** `Register` (reference-counted per root), discovery, `Resolve` to a generator
  that plans the cell at the instance's values and returns every placed instance's copper flattened (a ground pour
  excluded), one pin per Port named by its `Num`, and one Length handle per `StretchBinding`; `DeclaredParameters` from
  the `.ccell`; `ContentKeyFor` as PCD-0 §4.4. A duplicate id is unresolvable and `Describe` names both folders; a cell
  that places itself, directly or not, is refused.
- **`R-pcd5-3` — `PCellPackage`:** `Export` (after the check), `Import`, `ReadManifest`, the layout of PCD-0 §4.4.
- **`R-pcd5-4` — Terminals:** the packaged `.ccell` maps each Port `k` to the generator's pin `"k"`, so LVS pairs them.
- **`R-pcd5-5` — Update Schematic from Layout:** an unlinked `cpcell:` instance creates a component with `CellRef` to
  `CellDirOf(id)` and the instance's parameters; a linked one has its parameters pushed back, as a kit part's are.
- **`R-pcd5-6` — the Library tile:** every discovered designed cell is a placeable generator in the layout palette
  (`KnownGeneratorIds`, `DeclaredDefaults`) and places in a schematic by its symbol.
- **`R-pcd5-7` — `PCellDesignCheck.Run`:** the findings PCD-0 §5 seam 7 lists, ids `pcell.*`.

## 2. Decisions

- **D1 — The package is the folder, renamed to its cell.** Entries: `manifest.json`, `<Name>/.ccell`,
  `<Name>/schematic/<Name>.csch` (the primary's bytes), `<Name>/symbol/<Name>.csym` (the primary's bytes, or
  `AutoSymbolGenerator` when the cell has none with pins). Sorted ordinally, deflate, every timestamp 1980-01-01. The
  packaged `.ccell` names no primary (each view is the sole file) and carries `Terminals` and `NumPorts`.
- **D2 — The content key is one function of the folder.** `sha256:` over the primary `.csch` bytes, the `.ccell`'s
  `Parameters` as serialised, and the sorted `GeneratorContentKey` of every generator the schematic places. The
  manifest, `ContentKeyFor` and import's duplicate test all call it, so a package and its imported folder agree.
  Import compares the existing folder against the package as unpacked, not against the manifest's text, so two builds
  that version a primitive differently still recognise their own import.
- **D3 — Duplicate ids.** Discovery reads the root, its `LibraryRefs` and its referenced workspaces, four levels
  deep, skipping hidden and generated-cell folders. A name found in two folders resolves to nothing; `Describe` and
  the planner's "not registered" line name both. A miss or a stale hit re-reads the folders.
- **D4 — The recursion guard is per thread.** The generator keeps the cells it is drawing on a thread-local stack;
  meeting one again throws "places itself", and every frame above rethrows it, so the outermost placement is the one
  refused. The content key walks the same way and stops at a cycle.
- **D5 — Nested runs.** The generator runs the planner from inside a planner run. Nothing on that path holds a lock
  while a generator runs: `PCellRegistry` holds its gate only to resolve, and `Register` adds its resolver outside its
  own gate, so the two gates are never taken in opposite orders. Nested cells are keyed on the technology's content
  (`cpcell-tech/<hash>`), since a generator is handed a `Technology` and not its file.

## 3. Tests

`tests/Ui.Tests/PCellDesigner/Pcd5*.cs`, one per requirement, on a scratch workspace with the
`microstrip-0508.ctech` board.
