# Sonnet Brief — Phase L0a: layout model, units, technology, and persistence

**Design:** `docs/design/layout-view.md` §1 (units/DBU), §2 (layers + `.ctech`), §3 (geometry model),
§4 (`.clay`). Read those four sections first — this brief assumes them and does not restate the rationale.

**L0 is split in two.** This brief is **L0a: the framework-free model + persistence layer, with zero UI.**
`L0b` (the layout document + editor shell, the `.ctech` editor document, the `tech/` folder and project-tree
node) is briefed separately once this lands. The split is deliberate — the §12 risk table calls the unit/DBU
model the thing that "gets it wrong and everything inherits the mistake," so it is built first, alone, and
tested headlessly before any pixel depends on it.

## Goal

A complete, framework-free layout data model that round-trips through `.clay` and `.ctech`, with exact
integer unit arithmetic, both starter technologies, and a full headless test suite. Nothing renders,
nothing is editable, no document exists yet.

## Verified substrate (consume — already exists)

- `CellFolder` (`src/Ui/Schematic/CellFolder.cs`) already has `ViewType.Layout`, `LayoutSubFolder = "layout"`,
  `ViewExtension(Layout) => ".clay"`, and `PrimaryLayout` in `.ccell`. **The cell-folder work is done — do
  not touch it.**
- `SymbolPersistence.cs` / `SymbolModel.cs` (`src/Ui/Schematic/`) are the **format template**. Copy their
  conventions exactly:
  - `System.Text.Json`, `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`,
    `PropertyNameCaseInsensitive = true`, `Converters = { new JsonStringEnumConverter() }`.
  - **PascalCase property names, no naming policy.** (`docs/design/layout-view.md` §4's example sketch uses
    snake_case — that is illustrative shorthand and is *wrong* for this codebase. Follow `.csym`, and see
    "On completion" below: update the doc's example to match what you actually build.)
  - `FormatVersion` const + reject-on-mismatch → `InvalidDataException`. `Id` never persisted.
  - `[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]` + `[JsonDerivedType(...)]` on the shape base,
    exactly as `SymbolPrimitive` does it.
- `AtomicFile.WriteAllText` (`src/Ui/Schematic/AtomicFile.cs`) — all writes go through it.
- `tests/Ui.Tests` is the test project; see `src/Ui/CLAUDE.md` §"Testing without the Avalonia runtime".
  **Every test in this brief must run with no Avalonia runtime.**

## Code changes

### 0. New folder `src/Ui/Layout/` (namespace `CircuitRF.Ui.Layout`)

Its own area, mirroring how `src/Ui/DataDisplay/` was carved out — not under `Schematic/`.
**Every file in this brief is framework-free: no Avalonia types, no SkiaSharp types.** Layout does *not*
reference `SymbolRotation`, `SymbolColorRole`, or any other `Schematic` type — it borrows patterns, not types.

### 1. `LayoutUnits.cs` — exact unit arithmetic

```csharp
public enum LayoutUnit { Nm, Um, Mm, Mil, Inch }
```

**R2 is the whole point of this file: conversions must be exact, not merely close.**

- Each unit has an **exact** size in nanometres as an integer: `Nm=1`, `Um=1_000`, `Mm=1_000_000`,
  `Mil=25_400`, `Inch=25_400_000`.
- `long ToDbu(decimal value, LayoutUnit unit, int dbuPerMicron)` — compute in `decimal`, never `double`:
  `value * NmPerUnit(unit) * dbuPerMicron / 1000m`, then round away-from-zero to `long`.
  Doing this in `double` puts 1 mil at 25399.999999999998 and the exactness guarantee evaporates.
- `decimal FromDbu(long dbu, LayoutUnit unit, int dbuPerMicron)` — the inverse.
- `bool TryParse(string text, LayoutUnit fallbackUnit, int dbuPerMicron, out long dbu)` — accepts a bare
  number (interpreted in `fallbackUnit`) or a number with a suffix: `nm`, `u`/`um`/`µm`, `mm`, `mil`,
  `in`/`inch`. Case-insensitive, whitespace tolerated (`"2.9 mm"`), leading `+`/`-` accepted, `InvariantCulture`.
  This is the parser behind §1 R6's numeric-entry fields.
- `string Format(long dbu, LayoutUnit unit, int dbuPerMicron, int maxDecimals = 4)` — trailing zeros trimmed,
  `InvariantCulture`.

`DbuPerMicron` default is **1000** (1 DBU = 1 nm). Put that in one `public const int DefaultDbuPerMicron = 1000;`.

### 2. `LayoutModel.cs` — shapes and the layout container

```csharp
public readonly record struct LayerKey(int Layer, int Datatype);

public enum LayoutRotation { R0, R90, R180, R270 }
public enum PathEndStyle   { Flush, Round, Square, Extended }
public enum AngleMode      { Manhattan, Deg45, AnyAngle }
public enum EdgeKind       { Line, Arc, Cubic }
```

**Edge lists (§3.2 R9a).** One vocabulary serves `Curve` and `Path`:

```csharp
public sealed class LayoutEdge
{
    public EdgeKind Kind  { get; set; } = EdgeKind.Line;
    public double   Bulge { get; set; }          // Arc only: tan(sweep/4), signed; 0 = straight
    public long     C1X   { get; set; }          // Cubic only
    public long     C1Y   { get; set; }
    public long     C2X   { get; set; }
    public long     C2Y   { get; set; }
}
```

An `Edges` list is **parallel to the vertex list**: `Edges[i]` describes the edge leaving vertex `i`.
`Edges == null` means every edge is a straight line — so a plain polygon stores no edge data at all, and
`WhenWritingNull` keeps it out of the file entirely. A shorter-than-expected `Edges` list is padded with
`Line` on load (graceful within-version load, per the alpha policy).

Shapes, all deriving from `LayoutShape`, all carrying `LayerKey Layer` and `string? Net` (§3.4 R10a — the
net field lands **now**, even though nothing populates it until L5):

| Type | `$type` | Fields |
|---|---|---|
| `RectShape` | `"Rect"` | `X1 Y1 X2 Y2` (normalized so X1<X2, Y1<Y2) |
| `PolygonShape` | `"Poly"` | `long[] Xy` — flat, implicitly closed |
| `RoundedRectShape` | `"RRect"` | `X1 Y1 X2 Y2`, `CornerRadius` |
| `CircleShape` | `"Circle"` | `Cx Cy R` |
| `CurveShape` | `"Curve"` | `long[] Xy`, `List<LayoutEdge>? Edges`, `long? FlattenTolDbu` — closed |
| `PathShape` | `"Path"` | `long[] Xy`, `List<LayoutEdge>? Edges`, `long Width`, `PathEndStyle End`, `long? FlattenTolDbu` — open |
| `ViaShape` | `"Via"` | `X Y`, `PadSize`, `DrillSize`, `LayerKey? LandingLayer` |
| `LabelShape` | `"Label"` | `X Y`, `string Text`, `long Height`, `LayoutRotation Rotation`, `bool IsPort` |

`FlattenTolDbu` is nullable: `null` means "inherit the technology default" (§3.2 R9b). Resolve it at the
point of use, never by writing the default into the shape.

Instances are a separate list, not a shape:

```csharp
public sealed class LayoutInstance
{
    public string         CellRef  { get; set; } = "";   // relative path to the cell folder
    public long           X, Y     { get; set; }
    public LayoutRotation Rot      { get; set; }
    public bool           MirrorX  { get; set; }
    public double         Mag      { get; set; } = 1.0;
    public int            Rows     { get; set; } = 1;    // AREF; 1×1 = a plain instance
    public int            Cols     { get; set; } = 1;
    public long           PitchX, PitchY { get; set; }
    public string?        SchematicId { get; set; }      // §9 R16 idempotency; unused until L5
}
```

And the container:

```csharp
public sealed class LayoutView
{
    public int            DbuPerMicron { get; set; } = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit     DisplayUnit  { get; set; } = LayoutUnit.Um;
    public long           SnapDbu      { get; set; }
    public AngleMode      AngleMode    { get; set; } = AngleMode.AnyAngle;
    public string?        TechRef      { get; set; }     // relative path to a .ctech
    public List<LayoutShape>    Shapes    { get; } = [];
    public List<LayoutInstance> Instances { get; } = [];
}
```

### 3. `LayoutGeometry.cs` — bounding boxes and arc math

Framework-free, mirroring `SymbolGeometry.cs`'s role. **Bounding boxes only in L0a — the flattener and
`ToClipperPaths` belong to L1** (they are coupled to Clipper2 and the tolerance policy).

- `readonly record struct Bbox(long MinX, long MinY, long MaxX, long MaxY)` with `Union`, `IsEmpty`,
  `Contains`, `Intersects`.
- `Bbox BboxOf(LayoutShape shape)` — **exact** for `Rect`, `Poly`, `RRect`, `Circle`; for edge-list shapes:
  - `Arc` edges contribute their true extremes (the arc's extreme points, not just the chord endpoints —
    a semicircular edge whose chord is horizontal bulges outside the chord's bbox, and getting this wrong
    silently truncates spatial-index queries later);
  - `Cubic` edges contribute the **convex hull of their control points** — conservative, which is the
    correct bias for an index;
  - `Path` grows its result by `Width/2` (plus the end-style extension for `Extended`/`Square`).
- `LayoutArc` static helper: bulge ↔ (center, radius, start angle, sweep), plus `ArcExtremes(...)`.
  L1's flattener will reuse this — write it as a proper, separately-tested unit, not as a private helper.

### 4. `TechModel.cs` — the technology

```csharp
public sealed class LayerDef {
    public LayerKey Key;  public string Name;  public Rgba Color;
    public double FillOpacity = 0.35;  public int ZOrder;
    public bool Visible = true;  public bool Selectable = true;  public string? Purpose;
}
```

`Rgba` here is the **existing framework-free** `CircuitRF.Ui.Theming.Rgba` record struct — reuse it; it is
already serializable and already framework-free. (This is the one deliberate cross-reference; §2.2's point is
that layer colors are literal RGBA rather than `ColorRole`, and `Rgba` is exactly that type.)

```csharp
public enum StackupKind      { Dielectric, Conductor, Via }
public enum BoundaryCondition { Open, Ground }

public sealed class StackupLayer {
    public StackupKind Kind;  public string Name;  public long ThicknessDbu;
    public double Epsr = 1.0, TanD, Mur = 1.0;       // Dielectric
    public double SigmaSm;                            // Conductor: S/m
    public List<LayerKey> DrawingLayers = [];         // which drawing layers map here
}

public sealed class Stackup {
    public BoundaryCondition Top = BoundaryCondition.Open;
    public BoundaryCondition Bottom = BoundaryCondition.Ground;
    public List<StackupLayer> Layers = [];            // ordered TOP → BOTTOM
}

public enum DrcRuleKind { MinWidth, MinSpacing }
public enum DrcSeverity { Error, Warning }
public sealed class DrcRule {
    public string Name;  public DrcRuleKind Kind;  public LayerKey Layer;
    public long ValueDbu;  public DrcSeverity Severity = DrcSeverity.Error;
}

public sealed class Technology {
    public string Name;
    public LayoutUnit DefaultDisplayUnit;
    public long DefaultSnapDbu;
    public long DefaultFlattenTolDbu;
    public List<LayerDef> Layers = [];
    public Stackup Stackup = new();
    public List<DrcRule> DrcRules = [];
}
```

The stackup and DRC rules are **carried and round-tripped now, consumed later** (L5b and L6). Model them
properly rather than as a `Dictionary<string,object>` — a half-modelled stackup is a migration waiting to
happen. Interchange mappings (§2.4) are **deferred to L4**; do not add a placeholder field for them.

### 5. `StarterTechnologies.cs` — the two shipped techs

Two `static Technology` builders, matching §2.4's table exactly:

- **`Pcb2Layer()`** — display unit `Mil`, snap 1 mil, flatten tolerance 1 µm.
  Layers: Top Copper `(1,0)`, Bottom Copper `(2,0)`, Soldermask Top/Bottom `(3,0)/(4,0)`,
  Silk Top/Bottom `(5,0)/(6,0)`, Drill `(7,0)`, Outline `(8,0)`.
  Stackup: 1 oz copper (35 µm, σ = 5.8e7) / 1.6 mm FR-4 (εr 4.4, tanδ 0.02) / 1 oz copper; bottom `Ground`.
  DRC: min width 6 mil and min spacing 6 mil on both copper layers.
- **`MmicGaAs()`** — display unit `Um`, snap 5 nm, flatten tolerance 10 nm.
  Layers: Metal1 `(1,0)`, Metal2 `(2,0)`, Via `(3,0)`, Resistor `(4,0)`, Cap Dielectric `(5,0)`,
  Nitride `(6,0)`, Substrate `(7,0)`, Backside Via `(8,0)`.
  Stackup: 3 µm plated gold (σ = 4.1e7) / 100 µm GaAs (εr 12.9, tanδ 0.0006) / backside ground.
  DRC: min width 4 µm and min spacing 4 µm on Metal1/Metal2.

Pick distinguishable colors; exact hues are not load-bearing and the owner will tune them in L0b.
Both must satisfy the §7 validation below.

### 6. `LayoutPersistence.cs` and `TechPersistence.cs`

Two static classes, each with `Serialize` / `Deserialize` / `SaveToFile` / `LoadFromFile`, both cloning
`SymbolPersistence`'s shape and options block. `CurrentFormatVersion = 1` for both.

**One thing the template does not have — do it anyway (§4):** `LoadFromFile` must **sniff the two gzip magic
bytes (`0x1F 0x8B`)** and transparently decompress if present. The writer only ever writes plain JSON in v1.
This is three lines now and is what makes the future gzip switch a writer-side change with no format bump.

### 7. `TechValidation.cs` — validate, don't crash

`IReadOnlyList<string> Validate(Technology tech)` returning human-readable problems:
duplicate `LayerKey`; a `StackupLayer.DrawingLayers` entry naming a layer that is not in the table; a
conductor with `SigmaSm <= 0`; a dielectric with `Epsr < 1`; a `DrcRule` on an unknown layer;
non-positive thickness. Returns empty for both starter techs. **Never throws** — L0b surfaces these through
`IMessageSink`, and §2.4's rule is that a bad tech warns and still lets you edit.

### 8. `LayoutScaling.cs` — the DBU resolution migration (§1.4 R4)

```csharp
public static bool TryChangeResolution(
    LayoutView view, int newDbuPerMicron, out IReadOnlyList<string> offenders);
```

- New value must be an exact integer multiple **or** integer divisor of the current one; anything else
  returns `false` with an explanatory offender entry.
- **Refinement** (multiply): always succeeds, mutates every coordinate, `offenders` empty.
- **Coarsening** (divide): **pre-scan without mutating.** If any coordinate is not divisible by the ratio,
  return `false` with a bounded list (cap at ~20 entries plus a count) naming the shapes. Mutate only when
  the scan is clean. Partial mutation on failure is the one unacceptable outcome here.
- Scale every coordinate: shape geometry, path widths, radii, corner radii, `FlattenTolDbu`, instance
  positions and pitches, and `SnapDbu`. **Cubic control points too** — they are easy to miss.

## Scope guardrails (do NOT do in L0a)

- No rendering, no `SKPath`, no spatial index, no hit-testing, no LOD, no benchmark harness (L2).
- No document, no dock integration, no view, no view-model, no tools, no commands, no undo (L0b/L1).
- No project-tree node, no `tech/` folder scanning, no `.cws` default-tech field (L0b).
- No flattener, no `ToClipperPaths`, no Clipper2 dependency, no booleans, no offsets (L1).
- No GDSII/DXF/Gerber (L4), no DRC execution (L5b), no mesh or EM anything (L6+).
- Don't touch `src/Core`, `src/Engine`, `RfCore`, or any existing `Schematic/` file.

## Gate (acceptance)

1. Builds green (`TreatWarningsAsErrors=true`); `dotnet test` green; **all new tests run headless** with no
   Avalonia runtime.
2. **Unit exactness.** `1 mil → 25_400 DBU`, `1 µm → 1_000`, `1 mm → 1_000_000`, `1 inch → 25_400_000`, all
   exactly, at `DbuPerMicron = 1000`. `TryParse` round-trips `"2.9mm"`, `"115 mil"`, `"50u"`, `"1e3nm"`,
   `"-0.5mm"`, and rejects `"2.9 furlongs"`.
3. **Display-unit change is a no-op.** Serialize a layout; set `DisplayUnit` from `Um` to `Mil`; serialize
   again; assert **every shape's serialized geometry is byte-identical** and only the `DisplayUnit` token
   differs. This is §1.3 R3 as an executable assertion — it is the headline gate of this phase.
4. **`.clay` round-trips byte-identically**: serialize → deserialize → serialize produces identical bytes,
   over a fixture containing at least one of every shape type, an arc-bearing `Curve`, an arc-bearing `Path`,
   a shape with a `Net`, a 1×1 instance, and a 4×4 array instance.
5. **`.ctech` round-trips byte-identically** for both starter technologies, and `Validate` returns empty for
   both.
6. **Format version** — a file claiming a newer `FormatVersion` throws `InvalidDataException` on load, for
   both formats.
7. **Gzip sniff** — a gzipped `.clay` loads and equals the plain-text load of the same content.
8. **Resolution migration** — ×10 refinement is lossless and reversible on a fixture whose coordinates are
   all multiples of 10; coarsening a fixture with a coordinate of `1_234_567` returns `false`, names the
   offending shape, and leaves the layout **unmutated**.
9. **Bbox correctness** — exact expected values for rect, polygon, circle, rounded rect, a path with each end
   style, and a semicircular arc edge whose true bbox **exceeds** its chord's bbox (the case that catches a
   naive implementation).

## On completion

1. Add a "Phase L0a — COMPLETE" entry at the top of `src/Ui/CLAUDE.md`, in the established one-paragraph
   style: the new `src/Ui/Layout/` files, the DBU/unit rules, the `.clay`/`.ctech` format versions, the gzip
   sniff, and the test file names.
2. **Correct `docs/design/layout-view.md` §4.** Its JSON example uses snake_case keys and a shorthand edge
   encoding (`"e": ["L", {"A": 0.4142}, "L"]`); replace it with a real serializer output snippet so the
   design note and the code agree. Note the change in the doc's status line.
3. Report back before L0b (layout document + editor shell + `.ctech` editor + project tree) is briefed.
