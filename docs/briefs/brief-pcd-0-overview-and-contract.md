# Brief PCD-0 — PCell Designer: series overview and contract

**Series:** PCell Designer, PCD-0 to PCD-8 · **Tag:** `R-pcd0-n`
**Origin:** the schematic-driven layout series (SDL-0 to SDL-10) made a schematic of primitives draw a joined layout and
be solved and optimised by EM. This series makes such a schematic a **parameterised cell**: designed in one window, every
position a function of its parameters, exported as a PCell that any workspace places like a kit's.
**Area:** new `src/Design/PCellDesigner/`, `src/Design/Layout/SchematicSync/` (offset placement), new drawn-metal primitive
`MSHAPE`, `.ccell`/`.csch` additive fields, new `.cpcell` package, new `src/Ui/PCellDesigner/`, new `circuitrf pcell` verb.
**Depends on:** SDL-0 to SDL-10 (merged). Every later PCD brief depends on this one.

---

## 0. What the series adds

| Lane | Adds |
|---|---|
| **PCD-1** | The seams commit (§5, first, before any other lane edits); `.ccell` metal declaration honoured by EM; the EM length step floor for placed cells |
| **PCD-2** | Offset placement for parts not joined by a net; the plan-at-values entry point |
| **PCD-3** | The drawn-metal primitive `MSHAPE`; Stretch by parameter |
| **PCD-4** | Merge of `.cnl` text back into a schematic by designator; layout-first wiring (drop a part on a pin: part plus net) |
| **PCD-5** | The designed-cell generator and its resolver; the `.cpcell` package (export, import); the design check; Update Schematic from Layout for it |
| **PCD-6** | The PCell Designer window and its entry points |
| **PCD-7** | `circuitrf pcell` with the nouns `new`, `check`, `render`, `export`, `import`, and the MCP `pcell` tool |
| **PCD-8** | Example workspace, user page, design note, screenshots, regenerated user HTML, the acceptance check |

Scopes are the ones given for the series, with one change: PCD-1 also writes the seams commit (§5), because every
cross-lane seam must exist and compile before the lanes start.

## 1. Series rules (every PCD brief holds these)

- **S1 — Everything is a function of the parameters.** A designed cell's copper is what its parameters say, at every
  value, with no hand-placed coordinate anywhere:
  - parts joined by a net are placed pin to pin by the planner, exactly as SDL-1 to SDL-3 place them;
  - a part not joined by a net carries an **offset placement** (§4.2): an anchor pin of another part plus `Dx`, `Dy` and
    `Angle` expressions; its whole chain moves with it;
  - a drawn-metal outline and its pins are expressions (§4.3);
  - in the designer a drag writes an expression, never a loose coordinate; the saved `.clay` is the planner's output at
    the defaults and is never hand-edited.
  SDL's S1 (never distort to close) and S2 (no auto-routing) stand: a joint or anchor that cannot be made is reported.
- **S2 — One model.** The cell's schematic of primitives is its circuit model: its closed-form models, extracted as a
  `define` like any hierarchical cell (`SchematicCircuit.CnlTextOf`, `src/Design/Schematic/SchematicCircuit.cs:125`).
  No second model is fitted, packaged or stored. `MSHAPE` has no circuit model unless it declares one (`Model=open`).
- **S3 — The same interface as any PCell.** A designed cell is placed through `IPCellGeneratorResolver`
  (`src/Design/Layout/PCells/Wire/PCellWorkerResolver.cs:4`) and the `.ccell` `LayoutGenerator` path the planner already
  has (`SchematicLayoutPlanner.cs:1602`), so the Library tile, parameter dialog, Properties, handles, regeneration,
  LVS identity (`Lvs/DeviceType.cs:275`) and EM need no designer-specific branch. The PCell contract
  (`PCellContract.cs:295` `PCellResult`, `:436` `PCellGenerator`) is not changed.
- **S4 — Existing files read and save unchanged.** Every new field is additive and omitted when unset
  (`WhenWritingNull`, or `WhenWritingDefault` for a bool), with no `FormatVersion` bump: the `.ccell` and `.csch`
  conventions (`CellPersistence.cs:462`, `SchematicPersistence.cs:402`).
- **S5 — Deterministic.** The same cell, values and technology give the same layout, the same generated cell and the
  same `.cpcell` bytes (sorted zip entries, fixed timestamps). The generator's content key carries everything its output
  depends on (§4.4).
- **S6 — Minimal tests.** One scoped test per R-tag behaviour, in `tests/Ui.Tests/PCellDesigner/Pcd<N>*.cs`. The
  end-to-end acceptance (§8) is the main check. No full suites, no benchmark or Firewall sweeps, no review passes.
- **S7 — circuitRF's own naming.** PCell Designer, designed cell, offset placement, drawn metal (`MSHAPE`), Stretch by
  Parameter, PCell package (`.cpcell`), generator prefix `cpcell:`. No other tool's names, wording or file layout.
- **S8 — Decisions taken for the series:**
  - **D-cell:** a designed cell is a PCell in its own workspace from the moment it is designed: its `.ccell` carries
    `LayoutGenerator = "cpcell:<cell folder name>"` and `EmCopper = true`. Export only packages the folder; import only
    unpacks it. Placed anywhere, source or imported, it goes through one resolver (PCD-5). Its own `.clay` stays its own
    document, opened and EM-run on its own (the `LayoutGenerator` rule, `CellPersistence.cs:259-271`).
  - **D-drawing:** a designed cell's schematic drawing follows its netlist. Text edits and pin drops that change
    connectivity redraw the wires (`NetlistSchematic.Build`, `NetlistSchematic.cs:252`); every component keeping its
    designator keeps its position, orientation and every non-netlist field (§4.2's two fields among them).
  - **D-numbers:** a designed cell's parameters are numbers (lengths, angles, counts). `Choices` on a designed cell is a
    check error: `EmEvaluation.Apply` applies real values only.

## 2. Agents in one tree

All lanes work at once in the **one** worktree `crf-pcd`, branch `feature/pcell-designer`, with one build output. The
rules file (`brief-pcd.md`, handed to every lane) gives the exact commands; in short:

- **Order.** PCD-1's seams commit (§5) lands first. Until `git log --oneline -- src/Design/PCellDesigner/PCellDesign.cs`
  shows it, every other lane reads and plans but edits nothing.
- **Ownership is per file and exclusive** (§6). Edit only your files and new files in your folders. To change another
  lane's file, add a hook in yours or name the change in your report. A seam's signature (§5) is frozen; a needed
  change is reported, not made.
- **Build and test only under the build lock** (`flock …/.home/pcd-build.lock`), in one command, with your own
  `TMPDIR`; build only the projects you touch and their test project. A build broken in another lane's file is not
  yours: wait and retry, and report it after ten minutes.
- **Commit only your own paths, under the git lock** (`flock …/.home/pcd-git.lock … git add -- <paths> && git commit
  -- <paths>`). Never `add -A`, `commit -a`, `stash`, `checkout`, `reset`, `rebase`, `merge` or a whole-solution format.
- **Keep every file compiling** between steps: write whole edits; a body not ready yet stays the seam's stub.

## 3. `R-pcd0-1` — the product, exactly

| Entry point | What it does | Lane |
|---|---|---|
| **Tools ▸ PCell Designer…** | Opens the designer on a new designed cell (`PCellDesign.Create`) or on the active schematic's cell when it is designed | 6 |
| **Edit in PCell Designer** (schematic and layout context menu) | On a placed component whose cell `PCellDesign.IsDesigned`: opens the designer on that cell; one window per cell folder | 6 |
| **File ▸ Import ▸ PCell Package…** | `PCellPackage.Import` into the workspace or a library folder it references | 6 |
| `circuitrf pcell new`, `check`, `render`, `export`, `import` | §7, every noun calling the `src/Design` function the window calls | 7 |
| MCP `pcell` tool, `what=` the noun | Translates to the same argv, as every MCP tool does (`ToolCatalog.cs:945`) | 7 |

The window (pattern: `src/Ui/Filters/`, the LC Filter Designer's debounced design loop
`LcFilterDesignerViewModel.cs:753`): a **canvas** (the planned layout at the current values), a **parameter table**, the
**text view** (the cell's `.cnl`, live; edits merged by designator), a **live circuit response**, **Run EM** (Planar,
overlaid), **Optimise** (the existing Optimizer on the cell's schematic, `em=correct`/`em=direct` as
`docs/user/src/reference/em-cells.md#optimise` describes) and **Export…**.

Once imported, a package's cell is an ordinary library cell: its symbol is the Library tile, its `.ccell` parameters
are the parameter dialog and Properties, its stretch bindings are drag handles, its generator regenerates it, Update
Schematic from Layout links it, LVS takes `cpcell:<Name>` as its identity and pairs pins through `Terminals`, and EM
solves it as copper.

## 4. `R-pcd0-2` — the data contract

### 4.1 `.ccell` (`CircuitRF.Design.Cells`, `src/Design/Cells/CellPersistence.cs`)

| Field | C# | JSON | Omitted when | Meaning |
|---|---|---|---|---|
| `CcellFile.PCellDesign` | `CcellPCellDesign?` | `"PCellDesign": { … }` | null | The designed marker. Presence is the marker |
| `CcellPCellDesign.Sweep` | `string?` | `"Sweep": "start=1 stop=20 npts=201 Unit=GHz"` | null | The live response's sweep, in the `.cnl` analysis spelling; null is the designer's default |
| `CcellFile.EmCopper` | `bool`, `[JsonIgnore(WhenWritingDefault)]` | `"EmCopper": true` | false | The layout its `LayoutGenerator` draws is metal: EM solves it as copper (PCD-1) |
| `CcellParameter.Minimum` | `double?` | `"Minimum": 0.0001` | null | Lowest accepted value, base SI (m, deg) |
| `CcellParameter.Maximum` | `double?` | `"Maximum": 0.002` | null | Highest accepted value, base SI |

Reused unchanged: `Parameters` (`Name`, `DefaultExpression` + `Unit` — the SI default is that expression evaluated —
`Dimension` is the kind: `Length`, `Angle` or `None`, `Description`), `LayoutGenerator` (`"cpcell:<Name>"`),
`Terminals` (one per Port: `{ "Port": k, "Name": "<port name>", "LayoutPin": "k" }`), `NumPorts`, `PrimarySchematic`,
`PrimarySymbol`. `CcellParameter.Clone()` copies the two new fields.

### 4.2 `.csch` component fields (`CircuitRF.Design.Schematic`, new `src/Design/Schematic/PCellDesignPlacement.cs`)

```csharp
/// brief-pcd-0 §4.2: where a part not joined by a net goes, relative to another part's pin.
public sealed record OffsetPlacement
{
    public string Anchor { get; init; } = "";  // "<InstanceName>.<pin>", the pin a 1-based port number: "TL1.2"
    public int    Pin    { get; init; } = 1;   // this part's pin that is placed at the offset point
    public string Dx     { get; init; } = "0"; // along the anchor pin's outward direction
    public string Dy     { get; init; } = "0"; // 90° counter-clockwise from it
    public string Unit   { get; init; } = "";  // the unit Dx and Dy are in, as EditableParameter.Unit; "" = metres
    public string Angle  { get; init; } = "0"; // degrees; 0 = this pin faces the anchor pin, as at a joint
}

/// brief-pcd-0 §4.3: an MSHAPE parameter that moves some of its vertices. The expressions are already written into
/// X[i]/Y[i]; this records what made them, for the drag handle.
public sealed record StretchBinding
{
    public string Parameter { get; init; } = "";   // a parameter of the designed cell
    public int    Anchor    { get; init; } = 1;    // the vertex (X[i]/Y[i]) that stays put
    public int[]  Moving    { get; init; } = [];   // the vertices that travel
    public double AxisDeg   { get; init; }         // which way they travel as Parameter grows, in the part's own frame
}
```

Geometry of an offset placement: with the anchor pin at `P` facing `θ` (outward), this part's pin `Pin` is placed at
`P + Dx·(cos θ, sin θ) + Dy·(−sin θ, cos θ)`, facing `θ + 180° + Angle`. All zero is exactly a joint. `Dx`, `Dy`,
`Angle` evaluate in the cell's scope (its parameters and VARs), as a part parameter does.

| Field | Model (`EditableComponent`, `EditableSchematic.cs:804`) | File (`CschComponent`, `SchematicPersistence.cs:126`) | JSON |
|---|---|---|---|
| Offset | `OffsetPlacement? Offset` | `OffsetPlacement? Offset` | `"Offset": {"Anchor":"TL1.2","Pin":1,"Dx":"g","Dy":"0","Unit":"um","Angle":"0"}` |
| Stretch | `IReadOnlyList<StretchBinding>? Stretch` | `List<StretchBinding>? Stretch` | `"Stretch": [{"Parameter":"Lpad","Anchor":1,"Moving":[2,3],"AxisDeg":0}]` |

Both are copied by `EditableComponent.Clone()` (records: shared, replaced and never mutated in place) and mapped in
`SchematicPersistence`'s two component loops (`:608`, `:712`). An empty `Stretch` is written as null.

### 4.3 `MSHAPE`, the drawn-metal primitive (PCD-3)

`SymbolKind.MShape`; netlist type, `PCellRegistry` key and LVS generator `MSHAPE`; designator prefix `SH`. A built-in
PCell, so `EmPartKinds` already calls it copper (`EmPartKinds.cs:51`). Its geometry is its own parameters, in the
part's own frame (the origin is where the planner puts the part):

| Parameter | Dimension | Placed default | Meaning |
|---|---|---|---|
| `Form` | – | `polygon` | `polygon` (closed outline) or `path` (centreline of width `W`) |
| `W` | Length | `0` | Path width; ignored for a polygon |
| `X[i]`, `Y[i]`, i = 1…N | Length | a 100 µm square (4 vertices) | Vertex i |
| `PinX[k]`, `PinY[k]`, `PinW[k]` | Length | pin 1 at the left edge's middle, the edge's width | Pin k: position, width |
| `PinDir[k]` | Angle | `180` | Pin k's outward direction, degrees |
| `Model` | – | `none` | `none`: no circuit model, EM only; `open`: its pins are left open in a circuit simulation |
| `SignalLayer`, `GroundReference` | – | empty | As MLIN: empty is the technology's top conductor and nearest ground |

The pin count is the number of `PinX[k]`; pin k is port k. Indexed groups use `UserParamTemplate`
(`ComponentTypeRegistry.cs:5273`) with the `[k]` spelling. `Model=none` makes the cell **have no circuit model**:
`EmModelSwitch.WithoutCircuitModel` (`EmModelSwitch.cs:140`) names it, so a run by circuit models is refused
(`CircuitEvaluation.cs:198`) and an optimisation selects `em=direct` (`OptimizationRun.Em.cs:298`). `Model=open` stamps
nothing and counts as a model.

### 4.4 The PCell package `.cpcell` (PCD-5)

A zip (`System.IO.Compression`, deflate, entries sorted ordinally, every timestamp 1980-01-01 00:00), holding the
cell folder itself:

```
<Name>.cpcell
├── manifest.json
└── <Name>/
    ├── <Name>.ccell              PCellDesign, LayoutGenerator = "cpcell:<Name>", EmCopper = true, Terminals
    ├── schematic/<Name>.csch     the definition and the circuit model (S2)
    └── symbol/<Name>.csym        the Library tile and the schematic symbol (AutoSymbolGenerator when the cell has none)
```

No layout, 3D, EM setup or results: the generator draws the layout wherever the cell is placed.

`manifest.json` (`PCellPackageManifest`):

```json
{
  "Format": "circuitrf.pcell",
  "FormatVersion": 1,
  "Name": "PadTee",
  "GeneratorId": "cpcell:PadTee",
  "ContentKey": "sha256:<hex>",
  "Generators": ["MBEND", "MLIN", "MSHAPE", "MTEE"],
  "Layers": [],
  "WrittenBy": "circuitRF <version>"
}
```

`ContentKey` is SHA-256 over the primary `.csch` bytes, the `.ccell`'s `Parameters` as serialised, and the sorted
`PCellRegistry.GeneratorContentKey` of every generator the schematic places; it is also what
`DesignedPCellResolver.ContentKeyFor` returns. `Layers` names every layer a part states explicitly (`SignalLayer`,
`GroundReference`); import refuses a technology without one, as `import part` reports missing layers.

**Import** writes `<libraryDir>/<Name>/`. `libraryDir` is the workspace root, a folder in its `LibraryRefs`, or an
editable referenced workspace (a cell library shipped as a workspace is the "kit" a package goes into; a Python kit
cannot hold one). An existing `<Name>` with the same `ContentKey` is "already imported" (no write); with another key it
is a refusal, never an overwrite.

## 5. `R-pcd0-3` — the seams (PCD-1 writes them, first, in one commit)

Every cross-lane interface below exists from the seams commit on, compiling, with the signature fixed. A stub body is
`throw new NotImplementedException("PCD-<owner>")` unless the table says otherwise; its owner replaces the body and
never the signature. Each new file starts with a comment naming its owning lane and this section.

| # | Seam | File (owner after the seams commit) | Stub body |
|---|---|---|---|
| 1 | §4.1 fields | `src/Design/Cells/CellPersistence.cs` (PCD-1) | real |
| 2 | §4.2 records and fields | new `src/Design/Schematic/PCellDesignPlacement.cs` (PCD-1); `EditableSchematic.cs` (PCD-3); `SchematicPersistence.cs` (PCD-1) | real |
| 3 | `EmEvaluation.Apply` → `internal` (`EmEvaluation.cs:323`); with seam 12's line, the file's only PCD edits | `EmEvaluation.cs` (frozen) | — |
| 4 | `PCellDesign` | new `src/Design/PCellDesigner/PCellDesign.cs` (PCD-5) | constants and `IsDesigned` real |
| 5 | `DesignedPCellResolver` | new `src/Design/PCellDesigner/DesignedPCellResolver.cs` (PCD-5) | `Register` returns a no-op `IDisposable`; `CellDirOf` null |
| 6 | `PCellPackage` | new `src/Design/PCellDesigner/PCellPackage.cs` (PCD-5) | throws |
| 7 | `PCellDesignCheck` | new `src/Design/PCellDesigner/PCellDesignCheck.cs` (PCD-5) | returns `[]` |
| 8 | `CellNetlistMerge`, `PinDrop` | new `src/Design/PCellDesigner/CellNetlistMerge.cs`, `PinDrop.cs` (PCD-4) | throws |
| 9 | `OffsetPlacer` | new `src/Design/Layout/SchematicSync/OffsetPlacer.cs` (PCD-2) | throws |
| 10 | `DesignedCellLayout.At` | new `src/Design/Layout/SchematicSync/DesignedCellLayout.cs` (PCD-2) | **real**: the planner on an empty layout at the values, as `EmEvaluation.SchematicDisagreements` already does (`EmEvaluation.cs:184-200`) |
| 11 | `DrawnMetal` | new `src/Design/Layout/PCells/DrawnMetal.cs` (PCD-3) | constants real; `Is` returns false; `Stretch` throws |
| 12 | Resolver registration | `WorkspaceViewModel.cs` `ResetPCellGenerators` (`:744-782`, beside the kit resolver: dispose the previous, register the new); `GeneratedCellsRun.cs` `EnsureKitResolver` (`:284-297`, disposed in `Dispose` `:349`); `LayoutFromSchematic.cs` before `Plan` (`:81`); `EmEvaluation.Geometry` before `Plan` (`:134`) | `using`/held `DesignedPCellResolver.Register(workspaceRoot)` |
| 13 | Round-trip test | new `tests/Ui.Tests/PCellDesigner/Pcd1SeamsTests.cs` (PCD-1) | a `.ccell` and a `.csch` with every new field round-trip; one existing fixture of each re-serialises byte for byte |

The signatures (namespaces as the folders: `CircuitRF.Design.PCellDesigner`, `CircuitRF.Design.Layout.SchematicSync`,
`CircuitRF.Design.Layout.PCells`):

```csharp
// 4 — PCD-5
public static class PCellDesign
{
    public const string GeneratorPrefix      = "cpcell:";
    public const string PackageExtension     = ".cpcell";
    public const string ManifestFileName     = "manifest.json";
    public const string PackageFormat        = "circuitrf.pcell";
    public const int    PackageFormatVersion = 1;
    public static string GeneratorIdFor(string cellName) => GeneratorPrefix + cellName;
    public static bool IsDesigned(string? cellDir) => CellFolder.ReadCcell(cellDir)?.PCellDesign is not null;
    /// A new designed cell under parentDir: CellCreate.Create with schematic and symbol views, then the .ccell's
    /// PCellDesign, LayoutGenerator = GeneratorIdFor(cellName) and EmCopper = true. What Tools ▸ PCell Designer… and
    /// `pcell new` both call.
    public static CellCreateResult Create(string parentDir, string cellName);
}

// 5 — PCD-5
public sealed class DesignedPCellResolver : CircuitRF.Design.Layout.PCells.Wire.IPCellGeneratorResolver
{
    /// Makes every designed cell under workspaceRootDir (and the folders its .cws references: LibraryRefs,
    /// ReferencedWorkspaces) resolvable as "cpcell:<Name>" until disposed. Reference-counted per root: nested calls are
    /// free. Registration starts nothing; the folders are read on the first Resolve.
    public static IDisposable Register(string workspaceRootDir);
    /// The cell folder a registered "cpcell:<Name>" id names, or null.
    public static string? CellDirOf(string generatorId);
    // IPCellGeneratorResolver: Resolve, KnownGeneratorIds, Describe, DeclaredDefaults, DeclaredParameters, ContentKeyFor
}

// 6 — PCD-5
public sealed record PCellPackageManifest
{
    public string Format { get; init; } = PCellDesign.PackageFormat;
    public int    FormatVersion { get; init; } = PCellDesign.PackageFormatVersion;
    public string Name { get; init; } = "";
    public string GeneratorId { get; init; } = "";
    public string ContentKey { get; init; } = "";
    public IReadOnlyList<string> Generators { get; init; } = [];
    public IReadOnlyList<string> Layers { get; init; } = [];
    public string WrittenBy { get; init; } = "";
}
/// Path: the package written (Export) or the cell folder imported (Import); null when refused, the refusal an Error in
/// Findings.
public sealed record PCellPackageOutcome(string? Path, PCellPackageManifest? Manifest,
                                         IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Findings);
public static class PCellPackage
{
    public static PCellPackageOutcome Export(string cellDir, string packagePath);   // runs PCellDesignCheck first
    public static PCellPackageOutcome Import(string packagePath, string libraryDir);
    public static PCellPackageManifest? ReadManifest(string packagePath, out string? refusal);
}

// 7 — PCD-5
public static class PCellDesignCheck
{
    /// Every finding about cellDir as a designed cell, ids "pcell.*": S1 (every part joined or offset-placed, every
    /// anchor resolves, every expression evaluates at the defaults and at each parameter's Minimum and Maximum),
    /// D-numbers, a parameter that is also a VAR, MSHAPE pins and Model, and what Export needs. schematic: the
    /// designer's in-memory one; null reads the cell's primary.
    public static IReadOnlyList<CircuitRF.Diagnostics.Diagnostic> Run(string cellDir, SchematicEditModel? schematic = null);
}

// 8 — PCD-4
public sealed record CellNetlistMergeResult(SchematicEditModel? Schematic, IReadOnlyList<string> Refusals,
                                            IReadOnlyList<string> Notes);
public static class CellNetlistMerge
{
    /// cnlText merged into a copy of current by designator (D-drawing). Never mutates current. Schematic null when refused.
    public static CellNetlistMergeResult Merge(SchematicEditModel current, string cnlText);
}
public sealed record PinDropResult(SchematicEditModel? Schematic, string? InstanceName, string? Refusal);
public static class PinDrop
{
    /// part, as the Library palette makes it for placement, added with its pin partPin on targetInstance's pin
    /// targetPin's net; a new designator; through Merge. Never mutates current.
    public static PinDropResult Apply(SchematicEditModel current, EditableComponent part, int partPin,
                                      string targetInstance, int targetPin);
}

// 9 — PCD-2
public readonly record struct PinPose(long X, long Y, double OutwardDeg);   // DBU, degrees
public static class OffsetPlacer
{
    /// Where the offset part's pin goes and which way it faces, given its anchor pin as placed. Null with refusal
    /// when an expression does not evaluate.
    public static PinPose? Pose(OffsetPlacement offset, PinPose anchor, Scope scope, Evaluator evaluator,
                                int dbuPerMicron, out string? refusal);
    /// The offset after dragging the part's pin from now to (toX, toY): Dx and Dy keep their parameter terms and
    /// gain the change as a literal in offset.Unit (S1: an expression, never a coordinate).
    public static OffsetPlacement Drag(OffsetPlacement offset, PinPose anchor, PinPose now, long toX, long toY,
                                       int dbuPerMicron);
}

// 10 — PCD-2 (real body in the seams commit)
public sealed record DesignedCellLayoutResult(LayoutView? Layout,
                                              IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines, string? Refusal);
public static class DesignedCellLayout
{
    /// The cell's layout at values (base SI, by parameter or VAR name): a copy of the schematic (or of `schematic`,
    /// the designer's in-memory one), EmEvaluation.Apply(copy, values), SchematicLayoutPlanner.Plan onto a new empty
    /// LayoutView with connectParts: true and the cell's layout folder as base, ApplyTo; Lines = plan.Lines plus
    /// NoLayoutWarnings as warnings. Registers DesignedPCellResolver for workspaceRootDir while it plans.
    public static DesignedCellLayoutResult At(string cellDir, IReadOnlyDictionary<string, Value> values,
                                              string workspaceRootDir, Technology technology, string? techIdentity,
                                              SchematicEditModel? schematic = null);
}

// 11 — PCD-3
public static class DrawnMetal
{
    public const string GeneratorId = "MSHAPE";
    public const string Form = "Form", FormPolygon = "polygon", FormPath = "path", Width = "W";
    public const string Model = "Model", ModelNone = "none", ModelOpen = "open";
    public const string SignalLayer = "SignalLayer", GroundReference = "GroundReference";
    public static string X(int i)      => $"X[{i}]";
    public static string Y(int i)      => $"Y[{i}]";
    public static string PinX(int k)   => $"PinX[{k}]";
    public static string PinY(int k)   => $"PinY[{k}]";
    public static string PinW(int k)   => $"PinW[{k}]";
    public static string PinDir(int k) => $"PinDir[{k}]";
    public static bool Is(EditableComponent c);   // stub: false; PCD-3: c.Symbol == SymbolKind.MShape
    /// A copy of shape whose Moving vertices follow binding.Parameter along binding.AxisDeg from binding.Anchor:
    /// unchanged at the current values, moved by δ when the parameter grows by δ; binding appended to Stretch.
    public static EditableComponent Stretch(EditableComponent shape, StretchBinding binding, Scope scope, Evaluator evaluator);
}
```

Lane-internal entry points that need no stub, named so callers know them: `PCellDesignResponse.Run` (PCD-6, new
`src/Design/PCellDesigner/PCellDesignResponse.cs`: `SchematicCircuit.CnlTextOf` → `PreparedCircuit.FromText`
(`PreparedCircuit.cs:118`) → `CircuitEvaluation.Evaluate` (`CircuitEvaluation.cs:94`) with the values as `Sets` and the
sweep appended when the schematic has no S-parameter analysis); `MShapePCell.Generate` (PCD-3); `PCellVerb.Run` (PCD-7).

## 6. `R-pcd0-4` — file ownership (exclusive, after the seams commit)

| File or folder | Owner | Note |
|---|---|---|
| `src/Design/Cells/CellPersistence.cs`, `src/Design/Schematic/SchematicPersistence.cs`, `src/Design/Schematic/PCellDesignPlacement.cs` | PCD-1 | Fixes to the seam fields only |
| `src/Design/Layout/Em/EmPartKinds.cs`, `src/Design/Optimization/OptimizationRun.Em.cs`, `src/Design/Optimization/OptimizationDiagnostics.cs`, `src/Design/Layout/Em/ParametricEm/ParametricEmProvider.cs` | PCD-1 | |
| `src/Design/Layout/Em/ParametricEm/EmEvaluation.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs`, `src/Design/Layout/PCells/GeneratedCellsRun.cs` | frozen | The seams commit is their only PCD edit. PCD-6's commands go in a new partial `WorkspaceViewModel.PCellDesigner.cs` |
| `src/Design/Layout/SchematicSync/**` (planner, `ChainPlacer`, `PortPlacement`, `LayoutFromSchematic`, `OffsetPlacer`, `DesignedCellLayout`), `testdata/schematic-to-layout/**` | PCD-2 | |
| `src/Design/Schematic/SchematicModel.cs` (`SymbolKind`), `ComponentTypeRegistry.cs`, `BuiltInSymbols.cs`, `EditableSchematic.cs`, `MicrostripSubstrateInjection.cs`; `src/Design/Layout/PCells/PCellRegistry.cs`, `GroundPourPlanner.cs`, `DrawnMetal.cs`, new `MShapePCell.cs`; `src/Design/Layout/Lvs/DeviceType.cs`; `src/Design/Layout/Em/ParametricEm/EmModelSwitch.cs`; `src/Core/Devices/ComponentModelFactory.cs`, `src/Core/Netlist/InstanceNetContract.cs`; `src/Ui/Diagnostics/SymbolArtworkGenerator.cs`; `docs/user/assets/symbols/mshape*.svg`; the existing tests a new kind touches (`LibraryCatalogTests.cs`, `Lvs/ComparisonTests.cs`, `PCellGeneratorVersioningTests.cs`) | PCD-3 | The list the MRADIAL commit touched (`427453af3`) |
| `src/Design/Schematic/NetlistSchematic.cs`, `src/Design/PCellDesigner/CellNetlistMerge.cs`, `PinDrop.cs` | PCD-4 | |
| `src/Design/PCellDesigner/PCellDesign.cs`, `DesignedPCellResolver.cs`, `PCellPackage.cs`, `PCellDesignCheck.cs`; `src/Ui/Layout/LayoutToSchematicGenerator.cs`; `src/Ui/Layout/PCells/**` | PCD-5 | |
| new `src/Ui/PCellDesigner/**`, `src/Ui/Views/PCellDesigner/**`, `src/Ui/ViewModels/WorkspaceViewModel.PCellDesigner.cs`, `src/Design/PCellDesigner/PCellDesignResponse.cs`; `src/Ui/Views/WorkspaceWindow.axaml`, `src/Ui/Views/Content/SchematicView.axaml(.cs)`, `src/Ui/Views/Layout/LayoutEditorView.axaml(.cs)` | PCD-6 | Both Tools menus (`WorkspaceWindow.axaml:561`, `:1724`), the File ▸ Import entry, both context menus |
| `src/Cli/CliEntry.cs` (the `Dispatch` table, `:237`), new `src/Cli/PCellVerb.cs`, `src/Cli/Serve/ToolCatalog.cs`, `src/Cli/Reference.cs`, `docs/design/cli.md`, `CLAUDE.md` (the verb list line only) | PCD-7 | |
| `docs/**` (but `docs/design/cli.md` and this brief), `examples/**`, every `RESOLVED.md` | PCD-8 | Other lanes put findings in their reports; PCD-8 writes them |
| `tests/Ui.Tests/PCellDesigner/Pcd<N>*.cs`, `testdata/pcell-designer/pcd<N>/` | lane N | |

A lane that finds it must change a file it does not own stops at the seam and reports the exact change: the owner
makes it. Two known hooks are already placed: resolver registration (seam 12) and `EmEvaluation.Apply` (seam 3).

## 7. `R-pcd0-5` — lanes, their R-tags, and the order

**Order.** PCD-1's seams commit first (§2); then every lane at once. No merges: each lane needs another's REAL body
only where this table says, and builds against the stub until then.

| Lane | Needs the real body of | For |
|---|---|---|
| PCD-5 | `DesignedCellLayout.At` (real from the seams commit) | the generator from day one |
| PCD-5 | PCD-3's `MSHAPE` | the handles test (R-pcd5-2) |
| PCD-4 | PCD-3's `MSHAPE` in the `.cnl` | its one `MSHAPE` merge case |
| PCD-6 | PCD-4 `Merge`/`PinDrop`, PCD-2 `Drag`, PCD-3 `Stretch`, PCD-5 `Create`/`Export`/`Import` | its gesture tests; the window builds against the stubs before |
| PCD-7 | PCD-5 `Create`, `PCellDesignCheck`, `Export`, `Import` | its byte-identity tests; `render` needs only seam 10 |
| PCD-8 | all | the example, the screenshots and §8, last |

**PCD-1** — `R-pcd1-1` the seams commit (§5). `R-pcd1-2` `EmPartKinds.Shallow` (`EmPartKinds.cs:54-56`): a `LayoutGenerator`
cell whose `.ccell` has `EmCopper`, and a `pdk://` part whose in-memory `.ccell` (`PdkKitRegistry.Find`) has it, is
`Copper`; everything else as today. `R-pcd1-3` the EM length step floor (`OptimizationRun.Em.cs:422`) applies when
`start.PathA` is null (placed EM cells, SDL-10), the database unit taken from the placed cells' layouts (the coarsest),
and to every length coordinate, a placed cell's own parameter (`X1.Lpad`) included. `R-pcd1-4` the no-circuit-model
sentences (`OptimizationDiagnostics.cs:199` and the next) stop saying the cause is a 3D-parametric cell.

**PCD-2** — `R-pcd2-1` the planner places a part with an `Offset` by `OffsetPlacer.Pose` from its anchor's placed pin,
after the chains are joined and before the rows; its chain moves with it as one rigid body; it never enters the rows.
An unresolvable anchor, an expression that does not evaluate, or an `Offset` on a part its chain already places is a
report line and nothing is moved to hide it. `R-pcd2-2` `OffsetPlacer.Pose` and `Drag`. `R-pcd2-3` `DesignedCellLayout.At`
is deterministic (same `.clay` bytes twice) and states its root: the chain the planner anchors first. `R-pcd2-4` designs
`O1-offset-gap` (two lines, the second offset by `Dx = g`) and `O2-offset-turned` (`Angle = 90`) in
`testdata/schematic-to-layout/`, tagged `pcd2` in `designs.txt`.

**PCD-3** — `R-pcd3-1` `MSHAPE` in the schematic (§4.3): kind, registry row, symbol with its pin count, the `.cnl` name,
a Core model that stamps nothing, `WithoutCircuitModel` naming a `Model=none` instance. `R-pcd3-2` `MShapePCell.Generate`:
polygon or path on the stated layers, pins as declared, registered in `PCellRegistry`. `R-pcd3-3` `DrawnMetal.Stretch`.
`R-pcd3-4` its LVS kind.

**PCD-4** — `R-pcd4-1` `CellNetlistMerge.Merge`: parameters from the text by designator; a designator added or removed
adds or removes the part; a designator whose type changes is a refusal (rename it instead); when the text's nets equal
the schematic's, the wires are kept, else redrawn (D-drawing) with every kept component's position, orientation and
non-netlist fields (`Offset`, `Stretch`, label offsets and visibility, `CellRef`, footprint, `CellInterfaceHash`)
carried over. `R-pcd4-2` `PinDrop.Apply`: the next free designator for the part's prefix; the dropped pin joins the
target pin's net (a new net when the target pin is unjoined); a target pin already joined to another part's pin is a
refusal naming it (S2 of SDL: no tee is invented).

**PCD-5** — `R-pcd5-1` `PCellDesign.Create` (D-cell). `R-pcd5-2` `DesignedPCellResolver`: `Register`, discovery,
`Resolve("cpcell:<Name>")` → a generator that calls `DesignedCellLayout.At` at the instance's values and returns every
placed instance's shapes flattened (a ground pour excluded), one `PCellPin` per Port named by its `Num` (from the port
labels `PortPlacement` writes), and one `PCellHandle` per `StretchBinding` (`Quantity = Length`); `DeclaredParameters`
from the `.ccell`; `ContentKeyFor` as §4.4; one id found in two folders is unresolvable and `Describe` names both; a cell
that places itself, directly or not, is refused (no recursion). `R-pcd5-3` `PCellPackage` (§4.4). `R-pcd5-4`
`Terminals` written so LVS pairs ports with the generator's pins. `R-pcd5-5` Update Schematic from Layout
(`LayoutToSchematicGenerator.cs:109`): an unlinked `cpcell:` instance creates a component with `CellRef` to
`CellDirOf(id)` and its parameters. `R-pcd5-6` the Library tile in schematic and layout. `R-pcd5-7` `PCellDesignCheck`.

**PCD-6** — `R-pcd6-1` the entry points of §3, one window per cell folder. `R-pcd6-2` the canvas: `DesignedCellLayout.At`
at the current values, drawn with the layout renderer `render` uses; gestures: a Library part dropped on a pin →
`PinDrop`; an offset part dragged → `OffsetPlacer.Drag`; `MSHAPE` vertices selected → **Stretch by Parameter…** →
`DrawnMetal.Stretch`; a vertex drag writes its expression; each gesture is one undo step (a snapshot of the schematic).
`R-pcd6-3` the parameter table (name, default with unit, Minimum, Maximum, description) and a values column (not saved)
that drives the canvas and the response. `R-pcd6-4` the text view, through `CellNetlistMerge` on Apply. `R-pcd6-5` the
live response (`PCellDesignResponse.Run`, debounced as `DesignAsync`). `R-pcd6-6` Run EM: the cell's own layout at the
current values through `EmCapability.Resolve` and `EmEvaluation.Evaluate`, its `.cem` made by `EmSetupFromSchematic`
when missing, overlaid on the response. `R-pcd6-7` Optimise opens the existing Optimizer on the cell's schematic.
`R-pcd6-8` Export… and Import PCell Package…. `R-pcd6-9` Save writes the `.csch`, the `.ccell` and the cell's own `.clay`
as `DesignedCellLayout.At` at the defaults (S1).

**PCD-7** — `R-pcd7-1` `pcell new <workspace> <name>` → `PCellDesign.Create`. `R-pcd7-2` `pcell check <cell>
[--severity]` → `PCellDesignCheck.Run`, exit as `check`. `R-pcd7-3` `pcell render <cell> -o <svg|pdf|png> [--set
name=value]…` → `DesignedCellLayout.At` and the layout renderer `render` calls, `render`'s viewport flags. `R-pcd7-4`
`pcell export <cell> -o <file.cpcell>`, `pcell import <file.cpcell> --into <dir>`. `R-pcd7-5` the MCP `pcell` tool.
`R-pcd7-6` `--help`, `cli.md`, the verb list. Gate as `AuthoringCliVerbTests`: the process against the in-process call,
byte for byte, and a comment-stripped scan that `PCellVerb.cs` holds no logic.

**PCD-8** — `R-pcd8-1` `examples/PCell Designer/` (the §8 cell and bench, a `.cdd` rendered by circuitrf, a short README;
no committed results) and its `examples.json` row. `R-pcd8-2` `docs/user/src/reference/pcell-designer.md` (and the site's
navigation), `docs/design/pcell-designer.md` (S1-S8, each lane's decisions from its report, Possible enhancements),
the `.cpcell` entry in `file-formats.md`, `EmCopper` in `em-cells.md`, the verb in the user `cli.md`. `R-pcd8-3`
screenshots (`tools/UiSnapshot`) and the regenerated user HTML (`tools/DocGen`). `R-pcd8-4` §8. `R-pcd8-5` the
lanes' findings into the `RESOLVED.md` files.

## 8. `R-pcd0-6` — the acceptance check that closes the series

`tests/Ui.Tests/PCellDesigner/Pcd8AcceptanceTests.cs`, one test, every step through the functions the window and the
verb call:

1. **Design.** `PCellDesign.Create` makes `PadTee`: Port 1 – MLIN `TL1` – MBEND `B1` – MTEE `T1` – MLIN `TL3` – Port 2,
   the tee's branch MLIN `TL2` ending in an `MSHAPE` pad `SH1` (`Model=open`, a `StretchBinding` on `Lpad`).
   Parameters `W`, `L1`, `Lstub`, `Lpad`, each with a range. `PCellDesignCheck.Run`: no error.
2. **Export and import.** `PCellPackage.Export` → `PadTee.cpcell`; a fresh workspace on the same technology;
   `PCellPackage.Import` into it. Exporting twice gives the same bytes.
3. **Place twice.** A bench schematic places `X1` (`Lpad = 200 um`) and `X2` (`Lpad = 400 um`) in a 50 Ω line with two
   Ports and an S-parameter analysis; Update Layout from Schematic places two generated cells that differ, each with
   an `Lpad` handle.
4. **LVS clean.** `LvsRun.Run` on the bench: zero errors.
5. **Circuit simulation runs.** `CircuitEvaluation.Evaluate` on the bench: `Ok`, and `X1`'s S21 differs from a bench
   with `X1.Lpad = X2.Lpad`.
6. **One `em=correct` optimisation on Planar.** The bench's own layout by EM (`.cem` from `EmSetupFromSchematic`), one
   goal on S11, `tune X1.Lpad`: `OptimizationRun` with `em=correct` runs and the geometry handed to the solver holds
   both pads' copper (PCD-1). In the test the solver is `EmEvaluation.SolverStandIn`; the real Planar run is made once on
   the example with `circuitrf opt` and its result reported, not committed.

`Model=open` on the pad is what makes step 6 `em=correct`: with `Model=none` the cell has no circuit model and the run
is `em=direct` by S2 (§4.3).

## 9. Possible enhancements

- **Repetition and conditional shapes** (finger arrays, an optional stub). A Python PCell placed inside the designed
  cell covers them today. Built in, they would make the part count a function of a parameter, so the netlist, the
  port count and LVS's terminal map would change with a value: one schematic would no longer be one model (S2).
- **A constraint solver for placement.** S1 already makes every position an explicit expression. A solver adds
  configurations with several or no solutions, an order-dependence S5 forbids, and a second placement engine beside
  the planner.
- **Models fitted from EM.** `em=correct` already corrects the primitives' models by EM during optimisation. A fitted
  parametric model needs a sampling plan and a model format of its own: the Model Composer's territory, not the
  designer's.
- **Offset and Stretch in the `.cnl`.** The text view does not carry them (they are placement, not circuit), so a cell
  authored as text alone needs the `.csch` written for its offsets. Carrying them needs a Core grammar change.
- **Choice parameters on a designed cell.** `EmEvaluation.Apply` applies real values only (D-numbers); a choice needs
  string values through the planner's scope.
- **A declared circuit model for `MSHAPE` beyond `open`** (a pad capacitance, a Touchstone file), so more drawn shapes
  keep `em=correct`.
- **Nets from Update Schematic from Layout outside the designer**, for pins that meet in any layout: the designer's
  `PinDrop` is the seam it would call.

## 10. Gate

Each lane: `dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~PCellDesigner.Pcd<N>"` under the build
lock, plus the existing tests its R-tags name (PCD-2: `FullyQualifiedName~SchematicSync`; PCD-3: the three in §6). The
series closes on §8. Not the full suite.
