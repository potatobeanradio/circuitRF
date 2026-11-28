# Brief PCD-13 — Geometric operations on a designed cell

**Series:** [PCell Designer](brief-pcd-0-overview-and-contract.md) · **Tag:** `R-pcd13-n`
**Area:** new `src/Design/PCellDesigner/Operations/` (`PCellOperation`, `PCellOperations`, `PCellOperationsEngine`);
one hook each in `src/Design/Layout/SchematicSync/DesignedCellLayout.cs` (the engine after the planner),
`src/Design/Schematic/SchematicPersistence.cs` and `EditableSchematic.cs` (one additive field, D1),
`src/Design/Layout/PCells/DrawnMetal.cs` (the `Role` word), `src/Design/Layout/Em/ParametricEm/EmModelSwitch.cs`
(R-pcd13-4), `src/Design/PCellDesigner/PCellDimensions.cs` (R-pcd13-6); `src/Design/Layout/Lvs/LvsRun.cs` (one hook) and new
`LvsOperatedCell.cs` (D8); `src/Cli/PCellVerb.cs`, `src/Cli/CliEntry.cs`, `src/Cli/Serve/ToolCatalog.cs` (the `pcell ops`
noun and MCP mode) and `tests/Ui.Tests/PCellDesigner/Pcd7PCellVerbTests.cs` (its gate); `docs/design/pcell-designer.md`
**Depends on:** PCD-0, PCD-2 (`DesignedCellLayout.At`), PCD-3 (`MSHAPE`), PCD-5 (the generator), PCD-7 (the verb), PCD-10
(`PCellDimensions`). **PCD-14** (the history panel in the designer window) codes against §2.

---

## 0. What this adds

Until now a designed cell's copper was exactly what its parts draw. This lane adds an ordered **operation history** to
the cell: Merge, Subtract, Intersect and Exclusive over parts, layers and **tool** shapes; Grow/Shrink onto the same or
another layer; Repeat in rows and columns. Every operand, amount, count and pitch is an expression in the cell's scope,
so the result is still a function of the parameters (S1). The history is applied after the planner, inside
`DesignedCellLayout.At`, so what the canvas shows, what Save writes, what the generator returns to a parent and what EM
solves are one result.

## 1. Requirements

- **`R-pcd13-1` — the history.** An ordered list of `PCellOperation` on the cell's schematic (D1), each with a stable
  `Id`, a kind, its operands and expressions, `Suppressed` and `KeepModel`. Add, edit, reorder, suppress and delete go
  through `PCellOperations` (§2), each returning a new schematic and never changing the one it is given, so each is one
  undo step in the designer (PCD-6 D2).
- **`R-pcd13-2` — the kinds.**
  - **Merge** (union), **Subtract** (A − B), **Intersect**, **Exclusive** (xor). An operand is a part by designator
    (its copper on the operation's layer), `layer:<name>` (all copper on that layer as the history has it so far), or
    an `MSHAPE` whose `Role` is `"tool"`: a cutter whose outline is never copper.
  - **Grow** by `Amount` (negative shrinks), onto the same layer or onto `Onto`, another layer: a derived layer
    (a solder-mask opening, a keep-out, a clearance).
  - **Repeat**: `Rows` × `Columns` copies of A at `PitchX`, `PitchY`; geometry only (D4).
- **`R-pcd13-3` — the engine.** `DesignedCellLayout.At` calls `PCellOperationsEngine.Apply` on the planned layout
  (one line). The result is drawn as D5 says. Pins stay where the parts put them. An operation that removes the copper
  under a part's pin, or changes which ports the copper joins, is **not applied** and says so (D6).
- **`R-pcd13-4` — model honesty.** An applied operation that changes copper on a conductor layer makes the cell
  EM-only unless it carries `KeepModel`: `EmModelSwitch.WithoutCircuitModel` names it (`PadTee:op2 (Subtract)`), so a
  run by circuit models is refused and an optimisation selects `em=direct`. An operation only on non-conductor layers
  (mask, silk, keep-out) keeps the model; `KeepModel` accepts the circuit model as an approximation (D7).
- **`R-pcd13-5` — LVS.** A placed designed cell stays a leaf (`cpcell:<Name>` and its `Terminals`, PCD-5). A cell
  with operations is compared by its ports' connectivity on the final copper, which the engine enforces (D8).
- **`R-pcd13-6` — dimensions.** A Grow amount or a Repeat pitch that names a cell parameter is a dimension line on the
  canvas through `PCellDimensions.Of` (D9).
- **`R-pcd13-7` — where the history is seen.** Operations are geometry, so they are not in the `.cnl` text view. They
  are seen and edited in the designer's history panel (PCD-14) and listed by `circuitrf pcell ops <cell>`.

## 2. The contract PCD-14 codes against

Namespace `CircuitRF.Design.PCellDesigner.Operations`, file `src/Design/PCellDesigner/Operations/`.

```csharp
public enum PCellOperationKind { Merge, Subtract, Intersect, Exclusive, Grow, Repeat }

/// One step of a designed cell's history. Expressions evaluate in the cell's scope (its parameters and VARs, at the
/// values the layout is planned at); a length is in Unit when it carries none of its own, as OffsetPlacement.Unit.
public sealed record PCellOperation
{
    public string Id { get; init; } = "";                 // "op1", unique in the cell; what refusals and the model switch name
    public PCellOperationKind Kind { get; init; }
    public List<string> A { get; init; } = [];            // operands: "TL1", "layer:Mask", a tool MSHAPE "SH2"
    public List<string>? B { get; init; }                 // Merge/Subtract/Intersect/Exclusive: the second operand set
    public string? Layer { get; init; }                   // the layer worked on, by technology name; null = the top conductor
    public string? Amount { get; init; }                  // Grow: an expression, negative shrinks
    public string? Onto { get; init; }                    // Grow: the layer the result goes to; null = Layer
    public string? Rows { get; init; }                    // Repeat: counts (expressions, rounded) and pitches
    public string? Columns { get; init; }
    public string? PitchX { get; init; }
    public string? PitchY { get; init; }
    public string Unit { get; init; } = "";               // the unit Amount and the pitches are in; "" = metres
    public bool Suppressed { get; init; }                 // kept in the history, not applied
    public bool KeepModel { get; init; }                  // R-pcd13-4: the circuit model stands as an approximation
}

/// A history edit: the new schematic, or null with the sentence saying why not.
public sealed record PCellOperationEdit(SchematicEditModel? Schematic, string? Refusal);

/// What one operation did when the layout was planned: applied or not (and why not), and the shapes it made, in the
/// cell's frame. Spans are its parameter dimensions (D9).
public sealed record PCellOperationStep(string Id, bool Applied, string? Refusal, IReadOnlyList<LayoutShape> Result);
public sealed record PCellOperationSpan(string Expression, string Unit, long FromX, long FromY, long ToX, long ToY);
public sealed record PCellOperationTrace(IReadOnlyList<PCellOperationStep> Steps, IReadOnlyList<PCellOperationSpan> Spans);

/// The layout with the history applied up to and including one operation, and that operation's step.
public sealed record PCellOperationPreview(LayoutView? Layout, PCellOperationStep? Step,
                                           IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines, string? Refusal);

public static class PCellOperations
{
    public const string LayerPrefix = "layer:";          // an operand naming a layer
    public const string GeneratedTag = "pcell-ops";      // LayoutShape.Generated on every shape the engine draws

    public static IReadOnlyList<PCellOperation> Of(SchematicEditModel schematic);          // the history, in order
    public static string NextId(SchematicEditModel schematic);                             // "op<k>", the next free

    // Each returns a copy of current with the change, or a refusal; current is never changed.
    public static PCellOperationEdit Add(SchematicEditModel current, PCellOperation operation, int index = -1); // -1 = last; Id "" = NextId
    public static PCellOperationEdit Edit(SchematicEditModel current, string id, PCellOperation operation);    // replaces it, Id kept
    public static PCellOperationEdit Move(SchematicEditModel current, string id, int index);                   // reorder
    public static PCellOperationEdit Suppress(SchematicEditModel current, string id, bool suppressed);
    public static PCellOperationEdit Delete(SchematicEditModel current, string id);

    public static string Describe(PCellOperation operation);   // one line: "op2  Subtract  TL2 − SH2  on top conductor"
    public static IReadOnlyList<string> Listing(string cellDir);   // the primary schematic's history as Describe lines
    public static bool IsTool(EditableComponent component);        // an MSHAPE with Role = "tool"

    /// R-pcd13-4 for the designer: the operations that make this cell EM-only ("op2 (Subtract)"), in order.
    public static IReadOnlyList<string> WithoutCircuitModel(SchematicEditModel schematic, Technology? technology);

    /// What the engine did on a layout DesignedCellLayout.At returned; null for a cell with no history.
    public static PCellOperationTrace? TraceOf(LayoutView layout);

    /// DesignedCellLayout.At on schematic with every operation after operationId left out, and that operation's step.
    public static PCellOperationPreview Preview(string cellDir, SchematicEditModel schematic, string operationId,
                                                IReadOnlyDictionary<string, Value> values, string workspaceRootDir,
                                                Technology technology, string? techIdentity);
}
```

**What the UI calls.** The history panel lists `Of(schematic)` with `Describe` per row and a check box for
`Suppressed`; Add, Edit, Move (drag a row), Suppress and Delete call the five edits and push the returned schematic as
one undo step; the canvas re-plans with `DesignedCellLayout.At` as for every other edit. A row whose step in
`TraceOf(layout)` is not `Applied` shows its `Refusal`. Selecting a row may draw `Preview(…).Step.Result` over the
canvas. **Make tool** on an `MSHAPE` sets its `Role` row to `"tool"` (and `Model` to `"open"`). The response pane shows
`WithoutCircuitModel(schematic, technology)` in the no-circuit-model line `PCellDesignResponse` already draws.

## 3. Decisions

- **D1 — The history lives in the cell's `.csch`,** `CschFile.PCellOperations` / `SchematicEditModel.PCellOperations`
  (`List<PCellOperation>?`, null when empty, omitted by the file's `WhenWritingNull`; `Suppressed` and `KeepModel` are
  `WhenWritingDefault`). Not in the `.ccell`'s `PCellDesign`: the designer hands `At` its in-memory schematic (seam 10),
  so an unsaved history previews and undoes with no side channel; the generator's content key already hashes the
  `.csch` bytes (PCD-0 §4.4), so an edited history regenerates every placement; and the package carries it unchanged.
- **D2 — Tools.** A tool is an `MSHAPE` with `Role="tool"`, a word written quoted as `Form` and `Model` are (PCD-3 D1),
  and `Model="open"`. It is placed like any `MSHAPE` (an offset from a pin, its own pin 1 the reference), so its outline
  is a function of the parameters; its copper is never drawn and it never makes the cell EM-only by itself. The planner's overlap warnings for a tool are dropped:
  overlapping the copper it cuts is what a tool is for.
- **D3 — One layer per operation.** `Layer` names the layer an operation reads and writes (null: the technology's top
  conductor, as `MSHAPE` draws). A part operand is the part's copper on that layer; a tool operand is its outline,
  whatever layer it is drawn on.
- **D4 — The result replaces A.** On the layer, `F ← (F − A) ∪ op(A, B)`: Merge adds B (a tool's area becomes copper),
  Subtract cuts B out of A, Intersect keeps A inside B, Exclusive keeps where exactly one is. B is never consumed:
  cutting with a part leaves that part whole. Grow onto the same layer replaces A by A grown; onto another layer it adds
  to that layer and leaves A. Repeat adds the copies (i, j) ≠ (0, 0) at (i·PitchX, j·PitchY); the copies have no pins
  and no circuit model (a finger array whose count changes the netlist stays a Python PCell's, PCD-0 §9).
- **D5 — How the result is drawn.** A part whose copper an operation changes stays placed as itself (its instance,
  `SchematicId`, transform, pins and `PCellOrigin`) on a copy of its generated cell holding no shapes, and its remaining
  copper is drawn in the cell's own layout as polygons with `Generated = "pcell-ops"` and `Component = <designator>`.
  Copper an operation makes (a grow, a copy, a tool's area merged in) is drawn the same way with no `Component`. An
  untouched part is untouched; a tool's instance keeps no shapes. Why: pins, handles, the designer's gestures, the
  generator's port pins and LVS identity all hang off the instance (PCD-5, PCD-6 D6), and the copy keeps them all. The
  ground pour is not an operand and is never changed.
- **D6 — Refusals.** An operation is not applied, with a Warning line `op2 (Subtract) was not applied: …` and nothing
  else changed, when an operand is unknown, an expression does not evaluate, it removes the copper under a part's pin
  (probed just inside the pin), or the ports its copper joins change (D8). The design check already turns a planner
  warning into an error at that point (PCD-5 R-pcd5-7), so a refused operation blocks Export.
- **D7 — Model honesty.** A conductor layer is one a `Conductor` stackup entry draws. An operation changes conductor
  copper when it is applied, not suppressed, and its written layer (`Onto` for a Grow, else `Layer`) is a conductor,
  except a Merge whose operands are all copper (it adds nothing). Such an operation, without `KeepModel`, is named by
  `PCellOperations.WithoutCircuitModel`. `EmModelSwitch.WithoutCircuitModel` appends, for the bench and for every
  library cell that is a registered designed cell (`DesignedPCellResolver.CellDirOf`), its operations as
  `<Cell>:op<k> (<Kind>)`. A designed cell is resolvable wherever a workspace is open or a layout or EM run is made
  (seam 12); a bare circuit run outside one sees no operation and runs the model.
- **D8 — LVS.** A placed designed cell is a leaf: `cpcell:<Name>`, its generator's pins and its `Terminals`, operations
  or none, so a bench compares it exactly as before. The cell itself, once an operation changes its copper (or it places
  a tool), is compared by its **ports' connectivity on the final copper**: `LvsRun` hands such a cell to
  `LvsOperatedCell`, which reads which ports each piece of copper joins (`CopperPieces`) on the saved layout and on the
  cell planned without its history (the planner drew that one from the schematic), and reports `lvs.net.short` or
  `lvs.net.open` when they differ, with the Info `lvs.scope.pcell-operations` saying how it was compared. Merged or cut
  copper is no longer a part's body, so a part-by-part reading would call it a short. The engine refuses an operation
  that would change the joins in the first place (D6), so a cell planned by `At` is clean.
- **D9 — Dimensions.** The engine records a span for a Grow whose `Amount` names a cell parameter (from A's outline
  outward by the amount, at the right of its bounds) and for a Repeat pitch that names one (from the first copy to the
  next). `PCellDimensions.Of` draws them with its own labels (one hook line reading `TraceOf(layout).Spans`). No
  generator handle: a grow has no drag direction a parent placement could hold.
- **D11 — Values as circuitRF spells them.** An amount or a pitch is read as every value box reads one
  (`TunableValue.Split`): `20 um`, `20um` and `Lmid/30` all evaluate, a unit written in the text winning over `Unit`.
- **D10 — The CLI.** `circuitrf pcell ops <cell>` prints `PCellOperations.Listing(cellDir)`, one line per operation
  and exits 0; a cell that is not designed is `pcell.cell.not-designed`. The verb holds
  no logic (PCD-7's gate gains the call). The MCP `pcell` tool has the mode `what=ops`.

## 4. Tests

`tests/Ui.Tests/PCellDesigner/Pcd13*.cs` on `Pcd5Workspace`'s designed through line: Merge (a tool's area becomes
copper), Subtract with a tool `MSHAPE`, Intersect, Grow onto a mask layer, Repeat; the model-honesty rule (conductor
Subtract named, mask Grow and `KeepModel` not); the pin-removal refusal; LVS of cells with a Subtract and a Merge on their own
layouts and of a bench placing one; a step that joins or severs the ports refused; an amount written `100 um`. Plus PCD-7's gate and help for `pcell ops`.

## 5. Possible enhancements

- **Part-by-part LVS inside a cell with operations.** D8 compares such a cell by its ports; reading each changed part's
  remaining copper as its body would also check the parts between the ports, at the cost of drawing it in the part's
  own frame.
- **An EM-only mark carried in the netlist.** D7 finds a placed cell's operations through the registered resolver; a
  key on the cell's `define` would let a bare circuit run outside a workspace see them too, at the cost of a Core
  grammar change.
- **Fillet and chamfer.** Round or cut corners by a radius expression are a Grow out and back in with round joins; worth
  adding once a cell needs smooth current paths at high frequency.
- **Operations on several layers at once.** One layer per operation keeps every step's meaning plain; a via stack cut
  in one step would take a layer list.
- **A generator handle for a Grow.** A parent could drag a clearance if the handle had an axis; D9 gives the designer a
  dimension only.
