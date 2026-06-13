# Brief: run-wire — implement ICellResolver + pass Library through WriteNetlist

**Goal.** Tie the hierarchy pieces into the live app: `WorkspaceViewModel` implements
`ICellResolver` (WYSIWYG memory-else-disk resolution + `.ccell` parameters), and `WriteNetlist`
passes the resolver to `NetExtractor.Extract` and the resulting `Library` to `CnlWriter.Write`.
`SchematicRunService` needs **no change** — it already reads the `Library` from the `.cnl` and
hands it to the Elaborator.

Authority: `docs/design/hierarchical-net-extraction.md` §3.2 + §5. Depends on **brief-cnl-cells**
(writer overload) and **brief-hier-extract** (`ICellResolver`, `ExtractionResult.Library`). Land
last.

## File

- `src/Ui/ViewModels/WorkspaceViewModel.cs` — implement `ICellResolver`; update `WriteNetlist`.

## Step 1 — implement `ICellResolver`

Add `ICellResolver` to the class's interface list:

```csharp
public partial class WorkspaceViewModel
    : ViewModelBase, ITreeActions, IHierarchyHost, ICellResolver
```

Add the resolver method (near the netlist/hierarchy helpers). It mirrors Push-In resolution
exactly (`schematic-hierarchy-navigation.md` §4): in-memory session if the cell is open, else load
from disk; cell name + parameters from the cell folder / `.ccell`.

```csharp
/// <summary>
/// ICellResolver — resolves a cell instance to its primary schematic (WYSIWYG: the shared
/// in-memory session if the cell is open anywhere, else the primary .csch from disk) plus the
/// cell's declared parameter interface. Returns null when unresolvable (scratch parent with no
/// directory, missing cell, or no primary schematic) — the extractor skips the instance.
/// </summary>
public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
{
    var primaryPath = HierarchyResolver.ResolvePrimaryPath(cellInstance, containingModel);
    if (primaryPath is null) return null;

    // Memory-else-disk. GetOrCreateSession returns the shared session VM (registry) or loads the
    // .csch from disk and wires it up (SchematicDirectory set) exactly as Open/Push-In do — this
    // is what makes nested cell resolution work and keeps unsaved edits visible.
    var schematic = GetOrCreateSession(primaryPath).EditModel;

    // primaryPath = …/<cell>/schematic/<file>.csch → cell dir is two levels up.
    var cellDir  = Path.GetDirectoryName(Path.GetDirectoryName(primaryPath))!;
    var cellName = Path.GetFileName(cellDir);

    IReadOnlyList<ParameterDeclaration> parameters = [];
    var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
    if (File.Exists(ccellPath))
    {
        try
        {
            parameters = CellPersistence.LoadFromFile(ccellPath).Parameters
                .Select(p => new ParameterDeclaration(
                    p.Name,
                    p.DefaultExpression,
                    string.IsNullOrEmpty(p.Unit) ? null : p.Unit,
                    hidden: !p.ShowOnSchematic))
                .ToList();
        }
        catch { /* malformed .ccell → no declared params; instance overrides still apply */ }
    }

    return new CellResolution(cellName, schematic, parameters);
}
```

Notes:
- `HierarchyResolver` is `internal static` in the same assembly — directly callable.
- `GetOrCreateSession(path)` returns the shared `SchematicViewModel`; `.EditModel` is the model.
  It already backs `OpenOrActivateSchematic` and Push-In, so the returned model has its
  `SchematicDirectory` set — required so a **nested** cell instance inside this sub-schematic
  resolves against the correct directory.
- **Side effect:** `GetOrCreateSession` registers the session in `_registry` if it wasn't already
  there. That's benign — a freshly-loaded clean session is retireable and reused if later opened.
  (If you'd rather not populate the registry during a pure netlist read, gate it as
  `_registry.TryGet(primaryPath, out var vm) && vm is not null ? vm.EditModel :
  SchematicPersistence.LoadFromFile(primaryPath)` — **only** if you confirm `LoadFromFile` sets
  `SchematicDirectory` the same way; otherwise keep `GetOrCreateSession`.)
- `CcellParameter` → `ParameterDeclaration(Name, DefaultExpression, Unit?, hidden: !ShowOnSchematic)`.
- Add `using CircuitRF.Core.Design;` if not already present (for `ParameterDeclaration`/`Library`).

## Step 2 — pass resolver + Library through `WriteNetlist`

Two-line change in `WriteNetlist`:

```csharp
var result = NetExtractor.Extract(model, testBenchName, cells: this);   // was: Extract(model, testBenchName)
var header = $"netlist.cnl — generated from TestBench \"{testBenchName}\"" +
             $" at {DateTime.UtcNow:O}";
var text   = CnlWriter.Write(result.TestBench, result.Library, header); // was: Write(result.TestBench, header)
```

Everything else in `WriteNetlist` (destination resolution, atomic temp+rename, returning
`(path, conflicts)`) is unchanged. Both `RunAnalysis` and `GenerateNetlist` call `WriteNetlist`, so
both automatically produce hierarchical netlists.

## Step 3 — run path (no change, confirm)

`SchematicRunService.RunNetlist` already does `CnlReader.ReadFile → new Elaborator(lib).Elaborate(tb)`
and passes the parsed `Library` to the Elaborator. With hierarchical `.cnl` now on disk, cell
instances elaborate (flatten) and simulate. Confirm no change is needed.

## Acceptance / end-to-end

- Place a cell instance in a **saved** schematic (the cell has a primary schematic + symbol).
  **Simulate → Generate Netlist** → `netlist.cnl` contains a `define <cell> ( … ) … end` block and a
  `<cell>:<Inst>` top-level line with the parent nets bound in port order.
- **Simulate → Run** on that schematic → it elaborates and runs (the Elaborator flattens the cell;
  internal nets are uniquified by instance path). Compare against a hand-written flat netlist of the
  same circuit to confirm equivalence.
- Two instances of the same cell ⇒ one `define` block, two instance lines.
- Nested cell (cell-in-cell) ⇒ both `define` blocks present (leaf-first), runs correctly.
- A cell instance whose parent schematic is an **unsaved scratch** doc (no directory) ⇒
  `ResolvePrimaryPath` returns null ⇒ the instance is skipped with a conflict warning
  (`"… has no primary schematic; skipped."`). Saving the schematic resolves it. (Acceptable v1
  behaviour — note it in the run output.)
- Flat schematics: identical `.cnl` to before (empty Library ⇒ no `define` blocks).

## Out of scope

- `NetExtractor` recursion/guards (brief-hier-extract) and writer emission (brief-cnl-cells).
- Cross-library cell-name collision handling (deferred — surface a conflict if two distinct cell
  dirs resolve to the same `CellName`).
