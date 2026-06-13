# Brief: hier-extract — hierarchical extraction in NetExtractor

**Goal.** Make `NetExtractor.Extract` emit **cell instances** and build a `Library` of `Cell`
definitions by recursively extracting referenced cells' schematics — instead of the current
`if (comp.CellRef is not null) continue;` skip. The Elaborator already flattens cell instances, so
extraction produces a *hierarchical* Design model and lets elaboration flatten it.

Authority: `docs/design/hierarchical-net-extraction.md` §3. This brief is **framework-free** and
unit-testable with a stub resolver; it does not touch Avalonia/registry/disk (that's brief-run-wire).

## Files

- `src/Ui/Schematic/ICellResolver.cs` — **new** (interface + record; framework-free).
- `src/Ui/Schematic/NetExtractor.cs` — recursion, cell emit, `ExtractionResult.Library`.
- Tests: `tests/Ui.Tests` — add to the existing NetExtractor test file (e.g. `NetExtractorTests.cs`)
  or create `NetExtractorHierarchyTests.cs`.

## Step 1 — the resolver seam (new file)

`NetExtractor` must stay framework-free, so cell→schematic resolution is injected. The resolver
resolves **one level**; `NetExtractor` drives the recursion.

```csharp
// src/Ui/Schematic/ICellResolver.cs
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Resolves a cell-instance component to its primary schematic + parameter interface.
/// Implemented by WorkspaceViewModel (registry-else-disk, WYSIWYG). Kept framework-free here.
/// </summary>
public interface ICellResolver
{
    /// <summary>
    /// Resolve <paramref name="cellInstance"/> (which lives inside <paramref name="containingModel"/>)
    /// to its primary schematic. Returns null when unresolvable (no primary schematic, scratch
    /// parent, missing cell) — the extractor then skips the instance with a conflict note.
    /// </summary>
    CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel);
}

/// <param name="CellName">Unique key + Cell.Name (the cell folder leaf name).</param>
/// <param name="Schematic">The cell's primary schematic — in-memory session if open, else disk.</param>
/// <param name="Parameters">The cell's declared parameter interface (from its .ccell).</param>
public sealed record CellResolution(
    string CellName,
    SchematicEditModel Schematic,
    IReadOnlyList<ParameterDeclaration> Parameters);
```

## Step 2 — `ExtractionResult.Library` + `Extract` signature

```csharp
public sealed record ExtractionResult(TestBench TestBench, IReadOnlyList<string> Conflicts)
{
    public IReadOnlyList<string> CellPorts { get; init; } = [];
    public Library Library { get; init; } = new("netlist");   // NEW — empty for flat schematics
}

public static ExtractionResult Extract(
    SchematicEditModel model, string testBenchName = "tb", ICellResolver? cells = null)
```

When `cells` is null (or a schematic has no cell instances) the Library stays empty and behaviour
is identical to today — keeps existing tests and any flat callers working.

## Step 3 — factor the per-model pipeline so it recurses

Today `Extract` runs the whole union-find/naming/emit pipeline inline for one model and returns a
`TestBench`. Sub-cells need the **same** pipeline. Factor the per-model work into a private helper
that returns the emitted instances + the model's CellPorts, threading the shared
Library/in-progress/conflicts through:

```csharp
public static ExtractionResult Extract(
    SchematicEditModel model, string testBenchName = "tb", ICellResolver? cells = null)
{
    var lib        = new Library("netlist");
    var conflicts  = new List<string>();
    var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var (instances, cellPorts) = ExtractModel(model, cells, lib, inProgress, conflicts);

    var tb = new TestBench(testBenchName);
    tb.Instances.AddRange(instances);

    // Analyses + measurements attach to the TOP testbench only (data-model §2.1 invariant).
    foreach (var analysis in model.Analyses)
        if (analysis.Enabled) tb.Analyses.Add(analysis);
    foreach (var measurement in model.Measurements)
        tb.Measurements.Add(measurement);

    return new ExtractionResult(tb, conflicts) { CellPorts = cellPorts, Library = lib };
}

// The CURRENT body of Extract (union-find seeding, T-junction/dot/short/label unions, pin infos,
// pinNetNameMap, AssignNetNames, BuildCellPorts, the emit loop) moves here UNCHANGED except the
// emit loop's cell-ref branch (Step 4). Does NOT touch analyses/measurements (top-only).
private static (List<Instance> Instances, IReadOnlyList<string> CellPorts) ExtractModel(
    SchematicEditModel model,
    ICellResolver?      cells,
    Library             lib,
    HashSet<string>     inProgress,
    List<string>        conflicts)
{
    // … existing union-find + naming setup …
    var instances = new List<Instance>();
    // … existing Term-Num uniqueness validation (append to `conflicts`) …

    foreach (var comp in model.Components)
    {
        if (comp.Disable is DisableState.Open or DisableState.Short) continue;
        if (comp.Symbol == SymbolKind.Ground) continue;
        if (comp.Symbol == SymbolKind.Pin)    continue;

        if (comp.CellRef is not null)
        {
            var ci = EmitCellInstance(comp, model, uf, QK, netNames, detachedKeys,
                                      cells, lib, inProgress, conflicts);
            if (ci is not null) instances.Add(ci);
            continue;
        }

        var inst = EmitInstance(comp, uf, QK, netNames, detachedKeys);
        if (inst is not null) instances.Add(inst);
    }

    var cellPorts = BuildCellPorts(pinInfos, conflicts);
    return (instances, cellPorts);
}
```

> Implementation note: the existing `Extract` body uses many locals (`uf`, `QK`, `netNames`,
> `detachedKeys`, `pinInfos`, etc.). Move them into `ExtractModel` verbatim. The only logic change
> is the cell-ref branch above. Keep `conflicts` as the shared list passed in.

## Step 4 — emit a cell instance (resolve, dedupe, guard, bind)

```csharp
private static Instance? EmitCellInstance(
    EditableComponent comp,
    SchematicEditModel model,
    UnionFind uf,
    Func<double, double, (long, long)> QK,
    Dictionary<(long, long), string> netNames,
    Dictionary<(string, int), (long, long)> detachedKeys,
    ICellResolver? cells,
    Library lib,
    HashSet<string> inProgress,
    List<string> conflicts)
{
    if (cells is null) return null;   // flat caller / no resolver — skip silently (back-compat)

    var res = cells.Resolve(comp, model);
    if (res is null)
    {
        conflicts.Add($"Cell instance '{comp.InstanceName}' (cell '{comp.CellRef}') has no " +
                      $"primary schematic; skipped.");
        return null;
    }

    var cellName = res.CellName;

    // Cycle guard: a cell currently being extracted up the stack instantiates itself.
    if (inProgress.Contains(cellName))
    {
        conflicts.Add($"Cell '{cellName}' instantiates itself (cycle); " +
                      $"instance '{comp.InstanceName}' skipped.");
        return null;
    }

    // Build the cell once (dedupe by name); children are added before parents → leaf-first lib.
    if (lib.Find(cellName) is null)
    {
        inProgress.Add(cellName);
        var (subInstances, subPorts) = ExtractModel(res.Schematic, cells, lib, inProgress, conflicts);
        var cell = new Cell(cellName);
        cell.Ports.AddRange(subPorts);
        cell.Instances.AddRange(subInstances);
        foreach (var p in res.Parameters) cell.Parameters.Add(p);
        lib.Cells.Add(cell);
        inProgress.Remove(cellName);
    }

    var cellDef  = lib.Find(cellName)!;
    var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);

    // BINDING CONTRACT GUARD: symbol port count must equal the cell's interface-pin count.
    // The instance's port index i binds positionally to Cell.Ports[i]; if they disagree the
    // netlist would mis-bind, so refuse rather than emit a wrong instance.
    if (cellDef.Ports.Count != portDefs.Length)
    {
        conflicts.Add($"Cell '{cellName}' instance '{comp.InstanceName}': symbol exposes " +
                      $"{portDefs.Length} port(s) but the cell defines {cellDef.Ports.Count} " +
                      $"interface pin(s); skipped.");
        return null;
    }

    // NetBindings = parent net at each symbol port, in symbol-port order (== Cell.Ports order).
    var nets = new List<string>(portDefs.Length);
    for (int pi = 0; pi < portDefs.Length; pi++)
    {
        var (px, py) = comp.GetPortWorldCoord(pi);
        nets.Add(NetForPort(comp, pi, px, py, uf, QK, netNames, detachedKeys));
    }

    var overrides = comp.Parameters
        .Select(p =>
        {
            var unit = UnitNormalizer.ToEngineUnit(p.Unit);
            return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
        })
        .ToList();

    return new Instance(comp.InstanceName, cellName, nets, overrides);
}
```

### `NetForPort` must be shared

Today `NetForPort` is a **local function inside `EmitInstance`**. Extract it to a private static
helper so both `EmitInstance` and `EmitCellInstance` use the identical detached/ground-safe net
lookup:

```csharp
private static string NetForPort(
    EditableComponent comp, int pi, double px, double py,
    UnionFind uf, Func<double, double, (long, long)> QK,
    Dictionary<(long, long), string> netNames,
    Dictionary<(string, int), (long, long)> detachedKeys)
{
    if (comp.IsPortDetached(pi) && detachedKeys.TryGetValue((comp.Id, pi), out var dk))
        return NetAtKey(uf, netNames, dk);
    return NetAt(uf, QK, netNames, px, py);
}
```

Update `EmitInstance` (primitives + ZPort) to call this shared helper instead of its local
function. No behavioural change for primitives.

### The binding contract (verify before relying on it)

Correctness hinges on: **the cell instance's symbol port index `i` is the same logical port as
`Cell.Ports[i]`** (the cell's symbol external-pin order matches its schematic `Pin` order, i.e.
`CellPorts`). Verify against `AutoSymbolGenerator` + the Pin-num→symbol-port mapping
(`brief-J-autogen-symbol`, `brief-G-pin-component-cell-mapping`): both derive from the cell's port
numbering (`.ccell` `NumPorts`; `Pin.Num` 1-based; symbol pins 0-based by PortIndex in Num order).
The `cellDef.Ports.Count != portDefs.Length` guard catches gross mismatch; the dedicated test
(below) pins the ordering. If verification shows the symbol's port order can differ from `CellPorts`
order in some case, raise it before shipping — do **not** silently reorder.

## Step 5 — tests (stub resolver)

A `StubCellResolver : ICellResolver` returns a hand-built `CellResolution` keyed by `comp.CellRef`.
Build minimal sub-`SchematicEditModel`s the same way the existing NetExtractor tests build models
(components + Pins). Cover:

1. **Flat regression:** schematic with only primitives ⇒ `result.Library.Cells` empty;
   instances unchanged from today.
2. **Single 2-port cell:** parent has one cell instance `X1` (2 ports) wired to nets `a`, `b`.
   Sub-cell schematic has two `Pin`s (Num 1,2) + an `R`. Assert: `Library` has cell with
   `Ports.Count == 2` and the `R` instance; top has one `Instance` with `Reference == cellName`,
   `NetBindings == ["a","b"]` (order!), overrides = the instance's parameter overrides.
3. **Reuse/dedupe:** two instances of the same cell ⇒ `Library` contains it **once**; two top
   instances, each with its own overrides.
4. **Nested:** cell A instantiates cell B ⇒ both in `Library`, **B before A** (leaf-first);
   B's instances live under A's `Cell.Instances`? No — B is its own `Cell`; A's `Instances`
   contains a `Cell:Inst` referencing B. Assert both cells present and A references B.
5. **Cycle:** resolver maps a cell to a schematic that instantiates itself ⇒ a conflict is added,
   the self-instance is skipped, extraction **terminates** (no stack overflow).
6. **Port mismatch:** resolved cell has 3 interface pins but the instance symbol has 2 ports ⇒
   conflict added, instance skipped.

## Acceptance

- Flat schematics: empty `Library`, byte-identical instance output to today.
- Cell instances produce `Cell:Inst`-style `Instance`s + a leaf-first `Library` of `Cell`s with
  correct Ports/Instances/Parameters.
- Dedupe by name; recursion terminates on cycles; port-count mismatch is refused with a conflict.
- `NetForPort` shared; primitive emission unchanged.

## Out of scope

- The resolver implementation (registry-else-disk, `.ccell` params) — brief-run-wire.
- `.cnl` emission — brief-cnl-cells.
- Cell-scoped `Variables`.
