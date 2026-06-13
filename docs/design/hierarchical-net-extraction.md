# Hierarchical Net Extraction + Generate Netlist

Status: design (Phase 6e follow-up). Authority for the deferred work in
`schematic-hierarchy-navigation.md` §4 (resolution rule) and §8 (deferred extraction).

## 1. Goal

Make a schematic that contains **cell instances** extract into a genuinely hierarchical
netlist that the engine can elaborate and run, and add a **Generate Netlist** command to the
Simulate menu so the result can be inspected by hand.

Two user-visible outcomes:

1. Placing a cell instance in a schematic and running (or generating the netlist) produces a
   `.cnl` with `define … end` cell blocks plus `Cell:Inst` instance lines — and simulates
   correctly via the existing flatten-on-elaborate path.
2. A **Generate Netlist** command (Simulate menu) writes `netlist.cnl` for the active
   schematic — no analysis — and opens it in the OS default editor.

## 2. Key finding — hierarchy is already built everywhere except the emit side

Reading the Core + run path verbatim shows the hierarchical machinery is **already present and
wired**:

- **Design model** (`src/Core/Design`): `Cell` (Name, Ports, Parameters, Variables, Instances),
  `Library` (Cells + `Find`), `Instance` (Reference = primitive type *or* cell name; NetBindings;
  Overrides; RefNetBinding). `TestBench` holds top-level instances; cell definitions live in a
  `Library`.
- **Elaborator** (`Elaborator(params Library[])`): flattens depth-first. For a non-primitive
  `Instance` it `FindCell(Reference)` across libraries, builds `subPortMap[cell.Ports[i] =
  ResolveNet(inst.NetBindings[i])]` **positionally**, builds a cell scope (defaults + overrides
  evaluated in the parent scope), and recurses. Nets are uniquified by instance path; `Pin`
  instances are skipped (connectivity markers); a `Term`/`Port` inside an instantiated cell is
  warned + treated inert.
- **CnlReader** already parses `define CellName ( P1 P2 … )`, `parameters name=expr [unit] …`,
  `Cell:Inst` instance lines, and `end [CellName]` into a `Library`; `ReadFile` returns
  `(Library, TestBench)`.
- **SchematicRunService.RunNetlist** already does `CnlReader.ReadFile → new
  Elaborator(lib).Elaborate(tb) → engines`. It already passes the parsed `Library` to the
  Elaborator.

So hierarchical **simulation** works the moment a hierarchical `.cnl` exists on disk. The only
gaps are on the **emit side**:

- **Gap A — `CnlWriter`** emits only globals + top-level instances + analyses + measurements. It
  does **not** emit `define … end` cell blocks. (The reader already understands them.)
- **Gap B — `NetExtractor`** skips cell instances:
  `if (comp.CellRef is not null) continue; // hierarchical extraction deferred to step 2`, and
  builds no `Library`.

This design closes A and B and adds the Generate Netlist command.

## 3. Extraction design (Gap B)

### 3.1 What the extractor must produce

A hierarchical Design model: a top `TestBench` (top-level primitive + cell instances) **plus** a
`Library` containing one `Cell` per distinct referenced cell (transitively). Extraction does
**not** flatten — the Elaborator already flattens. This matches the existing separation
(`NetExtractor` builds Design; `Elaborator` flattens).

`ExtractionResult` gains a `Library`:

```csharp
public sealed record ExtractionResult(TestBench TestBench, IReadOnlyList<string> Conflicts)
{
    public IReadOnlyList<string> CellPorts { get; init; } = [];
    public Library Library { get; init; } = new("netlist");   // NEW — empty for flat schematics
}
```

Flat schematics (no cell instances) yield an empty `Library` — fully backward compatible with
today's behaviour and the existing flat round-trip.

### 3.2 Resolving a cell instance to its schematic (the §4 rule)

`NetExtractor` is framework-free and must stay that way (no Avalonia, no registry, no disk
knowledge). It takes an injected resolver. The resolver resolves **one level**; `NetExtractor`
drives the recursion.

```csharp
public interface ICellResolver
{
    /// Resolve the cell instance <paramref name="cellInstance"/> that lives inside
    /// <paramref name="containingModel"/> to its primary schematic + parameter interface.
    /// Returns null when unresolvable (no primary schematic, scratch parent, missing cell).
    CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel);
}

public sealed record CellResolution(
    string CellName,                                   // unique key + Cell.Name
    SchematicEditModel Schematic,                      // WYSIWYG: in-memory session if open, else disk
    IReadOnlyList<ParameterDeclaration> Parameters);   // from the cell's .ccell
```

`WorkspaceViewModel` implements `ICellResolver` (it has `_registry`, `GetOrCreateSession`, and
the cell directory). Resolution mirrors Push-In exactly (`schematic-hierarchy-navigation.md` §4):

1. `HierarchyResolver.ResolvePrimaryPath(cellInstance, containingModel)` → primary `.csch` abs
   path (null ⇒ unresolvable).
2. `GetOrCreateSession(path).EditModel` — returns the shared in-memory session if the cell is
   open anywhere (a tab or pushed-in frame), else loads the primary `.csch` from disk. This is
   the WYSIWYG guarantee: unsaved edits in an open cell are reflected without saving.
3. `CellName` = the cell folder name (`comp.CellRef`'s leaf), used as the `Cell.Name`/dedupe key.
4. `Parameters` = the cell's `.ccell` parameter declarations (via `CellPersistence`), so the
   `Cell` carries its parameter defaults for the Elaborator's cell scope.

`Extract` gains an optional resolver; when null, cell instances are skipped exactly as today
(keeps existing unit tests and any flat callers working):

```csharp
public static ExtractionResult Extract(
    SchematicEditModel model, string testBenchName = "tb", ICellResolver? cells = null)
```

### 3.3 Recursion, dedupe, cycle guard

`NetExtractor` keeps a `Dictionary<string,Cell> cellsByName` (becomes the `Library`) and a
`HashSet<string> inProgress` (cells currently on the extraction stack). On a cell instance with
resolved `CellName C`:

- **C ∈ inProgress** → cycle. Add a conflict (`"Cell 'C' instantiates itself (cycle: a→b→C)"`),
  **skip** emitting the instance (do not recurse) so extraction terminates.
- **C ∈ cellsByName** → already extracted; just emit the instance.
- **otherwise** → mark `inProgress[C]`; recursively extract `resolution.Schematic` into a new
  `Cell` (see §3.4); add to `cellsByName`; clear `inProgress[C]`; then emit the instance.

Because a child cell is added to the Library *after* its own children, the Library ends up in
leaf-first order — convenient for `define`-before-use emission.

If the resolver returns null (unresolvable cell), add a conflict
(`"Cell instance 'X1' (CellRef 'amp') has no primary schematic; skipped."`) and skip the
instance — never abort the whole extraction.

### 3.4 Building a `Cell` from a sub-schematic

Refactor the current per-schematic emission so it can target either `TestBench.Instances` or
`Cell.Instances`. Extracting a sub-schematic reuses the **entire existing algorithm** (union-find
connectivity, net naming, Pin→port names, `EmitInstance`) — the only addition is the same cell
handling, applied recursively.

For a resolved cell:
- `cell.Ports` = the sub-extraction's `CellPorts` (already computed from the sub-schematic's
  `Pin` components — ordered by Num, differential pairs expanded to `base+`/`base-`).
- `cell.Instances` = the sub-schematic's emitted instances (primitives + nested cell instances).
- `cell.Parameters` = `resolution.Parameters` (from `.ccell`).
- `cell.Variables` = none for v1 (cell-scoped variables are a later feature; leave empty).

### 3.5 Emitting a cell instance (the binding contract — CRUX)

A cell instance is emitted exactly like a primitive but with `Reference = CellName` and no
`RefNetBinding`:

```csharp
// nets[pi] = parent net at the instance's port pi, in symbol-port order
var nets = new List<string>(portDefs.Length);
for (int pi = 0; pi < portDefs.Length; pi++) {
    var (px, py) = comp.GetPortWorldCoord(pi);
    nets.Add(NetForPort(pi, px, py));        // reuse existing detached/ground-safe logic
}
var overrides = comp.Parameters.Select(p => new ParameterAssignment(
    p.Name, p.Expression, UnitNormalizer.ToEngineUnit(p.Unit) is { Length: >0 } u ? u : null));
tb.Instances.Add(new Instance(comp.InstanceName, cellName, nets, overrides));
```

**Correctness rests on one invariant the brief MUST verify and guard:**

> The cell instance's symbol port index `i` (`comp.GetPortWorldCoord(i)`, `SymbolPortDefs.For`)
> corresponds to the same logical port as `Cell.Ports[i]` — i.e. the cell's **symbol** external
> pin order matches the cell's **schematic** `Pin` order (`CellPorts`).

Both derive from the cell's port numbering (the `.ccell` `NumPorts`; `Pin.Num` 1-based; symbol
pins 0-based by PortIndex; auto-symbol generator assigns PortIndex in Num order — see
`brief-J-autogen-symbol` / `brief-G-pin-component-cell-mapping`). The brief verifies this against
`AutoSymbolGenerator` + the Pin-num→symbol-port mapping, and adds a guard: if
`comp.PortCount != CellPorts.Count` for the resolved cell, add a conflict
(`"Cell 'C' instance 'X1': symbol has N ports but schematic defines M interface pins"`) and skip
the instance rather than emit a mis-bound netlist. This is the single highest-risk point; it gets
a dedicated unit test (a 2-port cell instance, nets bound in order, asserted against
`Cell.Ports`).

### 3.6 Net naming note

Cell-instance ports participate in the parent schematic's union-find exactly like any component
pin (they already seed P-cells today — only the *emit* was skipped). So `NetForPort` returns the
correct parent net (named net-label, Pin port name, ground `0`, detached synthetic, or auto
`nK`). No change to net-name assignment is required; only the emit loop's `continue` is replaced.

## 4. `.cnl` hierarchy emission (Gap A)

`CnlWriter` learns to emit the `Library` as `define … end` blocks **before** the top-level
content. The reader already parses these, so this is writer-only + round-trip tests.

Signature — add an overload (avoid breaking existing `Write(tb, header)` callers/tests):

```csharp
public static string Write(TestBench tb, string? header = null);                 // existing
public static string Write(TestBench tb, Library? library, string? header = null); // NEW
```

Emission order (leaf-first, matching the Library order the extractor produced):

```
; <header>

define <CellName> ( <port1> <port2> … )
  parameters <name>=<defaultExpr> [unit] <name2>=<expr2> [unit2] …     ; only if Parameters non-empty
  <instance lines>                                                     ; reuse FormatInstance
end <CellName>

… (more cells) …

<global variables>
<top-level instance lines>
<analyses>
<measurements>
```

Details:
- Instance lines inside a `define` reuse the existing `FormatInstance` (a cell instance is just
  `CellName:Inst nets params…` via `FormatStandardInstance`; the reader's general instance path
  reconstructs `Instance(Reference = CellName)`).
- `parameters` line: `parameters {Name}={DefaultExpression} [{Unit}] …` from
  `Cell.Parameters` (`ParameterDeclaration`). Omit the line when the cell has no parameters.
- Cells with no ports emit `define CellName ()` (reader tolerates an empty port list).
- Analyses/measures are never emitted inside `define` (the reader rejects them there; the
  extractor never puts them in a `Cell`).

Round-trip test: build a `TestBench` + `Library` with a 2-port cell that has a parameter and two
primitives, `Write` → `CnlReader.Read` → assert the `Library`/`TestBench` match (cell name, ports,
parameter, instances, bindings).

## 5. Run + Generate wiring

- `WorkspaceViewModel` implements `ICellResolver` (§3.2).
- `WriteNetlist(model, name)` passes the resolver to `Extract` and the resulting `Library` to
  `CnlWriter.Write`:
  ```csharp
  var result = NetExtractor.Extract(model, testBenchName, cells: this);
  var text   = CnlWriter.Write(result.TestBench, result.Library, header);
  ```
- `SchematicRunService.RunNetlist` is **unchanged** — it already reads the `Library` from the
  `.cnl` and passes it to the Elaborator. Hierarchical run works the moment WriteNetlist emits
  hierarchical `.cnl`.
- `RunAnalysis` continues to call `WriteNetlist`; it automatically becomes hierarchical.

## 6. Generate Netlist command (Simulate menu)

A new `GenerateNetlistCommand` on `WorkspaceViewModel`:

- **Behaviour:** extract + write `netlist.cnl` for the active schematic (reusing `WriteNetlist`),
  surface extraction conflicts as warnings, then open the file in the OS default editor. **No**
  analysis is run.
- **Target = active view:** use `activeDoc.ActiveViewModel.EditModel` (the cell you're currently
  looking at, including a pushed-in sub-cell) with `testBenchName = activeDoc.ActiveViewModel`'s
  cell name (fallback `activeDoc.Id`). This is WYSIWYG and lets you generate a netlist for a
  sub-cell directly. (Contrast: `RunAnalysis` uses the base `.ViewModel.EditModel`; Generate is an
  inspection aid, so "what you see" is the least surprising.)
- **Works for empty schematics:** `NetExtractor.Extract` on an empty model already yields an
  empty `TestBench`; `CnlWriter` writes the header with no body. No special-casing needed.
- **Not undoable:** it is a `RelayCommand` that only reads the model and writes a file — it never
  touches any `UndoRedoStack`.
- **Enablement (greyed out of context):** unlike `RunAnalysis` (which has no CanExecute and just
  warns), Generate Netlist uses an explicit CanExecute so the menu item greys out when the active
  document is not a schematic:
  ```csharp
  [RelayCommand(CanExecute = nameof(CanGenerateNetlist))]
  private void GenerateNetlist() { … }
  private bool CanGenerateNetlist()
      => _factory.DocumentDock?.ActiveDockable is SchematicDocument;
  ```
  Refresh it on every active-document change by adding
  `GenerateNetlistCommand.NotifyCanExecuteChanged();` to `OnDocumentDockPropertyChanged` (the
  central hook that already retargets Properties/Analyses/undo). Disabled for the Symbol Editor,
  cell parameter editor, Welcome stub, and when no document is active.
- **Opening externally:** factor the existing `OpenExternal(ProjectTreeNodeViewModel)` into a
  path-based `OpenPathExternal(string path)` (macOS `open`, Windows shell `explorer`/`ShellExecute`,
  Linux `xdg-open`) and call it with the written netlist path. `OpenExternal(node)` delegates to
  it.
- **Menu placement:** add to both the macOS `NativeMenu` and the in-window `Menu` Simulate
  sections, between "Setup Analyses…" and the Run/Stop group:
  ```
  Setup Analyses…
  Generate Netlist          ← new
  ──────────
  Run
  Stop
  ```
  No toolbar button and no key gesture (menu-only) unless you want one later.

## 7. Work breakdown (briefs) + sequencing

1. **brief-gennet — Generate Netlist command** *(independent; land first as the test harness).*
   New `GenerateNetlistCommand` + CanExecute + `NotifyCanExecuteChanged` in
   `OnDocumentDockPropertyChanged`; `OpenPathExternal` helper; Simulate-menu items (native +
   in-window). Uses today's flat `WriteNetlist`; becomes hierarchical automatically once 2–4 land.
2. **brief-cnl-cells — CnlWriter cell emission.** `Write(TestBench, Library?, header?)` overload
   emitting `define/parameters/…/end`; round-trip tests. Reader already supports it. Format-only.
3. **brief-hier-extract — NetExtractor hierarchical extraction.** `ICellResolver` +
   `CellResolution`; recursive `Cell`/`Library` build; emit cell instances; dedupe + cycle guard;
   port-binding contract + `PortCount != CellPorts.Count` guard; `ExtractionResult.Library`.
   Framework-free; unit-tested with a stub resolver.
4. **brief-run-wire — wire it together.** `WorkspaceViewModel : ICellResolver` (registry-else-disk
   via `GetOrCreateSession` + `ResolvePrimaryPath` + `.ccell` params); `WriteNetlist` passes the
   resolver + Library through. `SchematicRunService` unchanged.

Each brief is independently buildable and testable. 1 gives immediate manual-test value; 2 is
pure format; 3 is pure extraction (stub resolver); 4 ties 2+3 into the live app.

## 8. Test plan

- **Extractor (unit, stub resolver):** flat schematic ⇒ empty Library (regression). One cell
  instance, 2 ports ⇒ Library has the cell with correct Ports/Instances; top has a `Cell:Inst`
  with NetBindings in port order. Two instances of one cell ⇒ Library has it once, two instances
  with their own overrides. Nested cell (cell-in-cell) ⇒ both cells in Library, leaf-first.
  Self-referential cell ⇒ conflict + instance skipped, terminates. `PortCount != CellPorts.Count`
  ⇒ conflict + skipped.
- **Writer round-trip:** `Write(tb, lib)` → `CnlReader.Read` → equal Library/TestBench.
- **End-to-end (manual via Generate Netlist):** place a cell instance, Generate Netlist, confirm
  the `.cnl` has the `define` block + `Cell:Inst` line; Run and confirm it elaborates/simulates
  (Elaborator flattens). Empty schematic ⇒ header-only `.cnl`, opens externally.
- **Enablement:** Generate Netlist greys out on a Symbol Editor / cell editor / Welcome tab,
  enables on a schematic; toggles as you switch tabs.

## 9. Risks / open questions / deferred

- **Port-order binding (§3.5)** is the one correctness-critical assumption. Guarded + tested.
- **`.ccell` parameters as `Cell.Parameters`:** confirm `CellPersistence` exposes parameter
  declarations in `(name, defaultExpr, unit)` form; map to `ParameterDeclaration`. If the
  parameter model differs, the resolver adapts (brief-run-wire).
- **CellName uniqueness:** two different cells with the same folder leaf name in different
  libraries would collide in one flat `Library`. v1 uses the folder leaf as the key (matches the
  current single-workspace model); cross-library name collisions are deferred (surface a conflict
  if two distinct cell dirs resolve to the same name).
- **Generate target = active view** (§6) is a deliberate choice; revisit if you'd rather Generate
  always target the base testbench.
- **Deferred:** cell-scoped `Variables` emission; per-instance Library scoping; layout views.
