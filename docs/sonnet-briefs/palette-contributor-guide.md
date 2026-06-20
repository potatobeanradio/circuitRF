# Adding a built-in component to the Library Palette

This guide walks through every edit required to make a new built-in primitive appear in the Library
Palette, place correctly on a schematic, render its symbol, carry default parameters, and extract to
the engine. The guide targets a developer adding a new `SymbolKind` to the compiled-in component set
— external model libraries are a v2 concern (see the "v1 contribution point" note in §5).

## 1. Overview

The single contribution point is `ComponentTypeRegistry` in
[`src/Ui/Schematic/ComponentTypeRegistry.cs`](../src/Ui/Schematic/ComponentTypeRegistry.cs). The
palette is generated entirely from it — you do not touch any palette view or item-list code. Four
things must be in place for a component to work end-to-end:

| Thing | Defined where |
|---|---|
| Symbol glyph (drawing primitives) | `BuiltInSymbols.Primitives(kind)` in `BuiltInSymbols.cs` |
| Port positions | `SymbolPortDefs.For(kind, portCount)` in `EditableSchematic.cs` |
| Default parameters | `ComponentTypeRegistry.DefaultParameters(kind, portCount)` |
| Engine reference string | `ComponentTypeRegistry.EngineReference(kind, portCount)` |

Data flow:

```
SymbolKind (enum)
  → ComponentTypeRegistry entry   →  LibraryCatalog.AllItems  →  PaletteTileVm  →  palette tile
  → SymbolPortDefs.For             →  port positions at placement / connectivity
  → BuiltInSymbols.Primitives      →  glyph rendered on canvas and in palette glyph tile
  → DefaultParameters              →  EditableParameter list on freshly-placed component
  → EngineReference                →  Instance.Reference in extracted netlist
```

Cross-links to related design notes:
- Palette architecture: [library-palette.md](design/library-palette.md)
- Symbol glyph conventions and geometry spec: [standard-library-symbols.md](design/standard-library-symbols.md)
- Symbol editor (for cell-reference / user-drawn symbols): [symbol-editor.md](design/symbol-editor.md)
- Parameter editor (per-instance values): [parameter-editor.md](design/parameter-editor.md)
- Netlist and engine model: [data-model.md](design/data-model.md)

---

## 2. Step-by-step recipe

### Step 1 — Add a `SymbolKind` enum value

`SymbolKind` is defined in
[`src/Ui/Schematic/SchematicModel.cs`](../src/Ui/Schematic/SchematicModel.cs).

```csharp
public enum SymbolKind
{
    Resistor,
    // … existing values …
    MyNewPart,   // add here
}
```

Place it near related kinds (e.g., a new lumped element next to R/L/C). The value is used as a
dictionary key internally; insertion position does not affect behavior.

---

### Step 2 — Add the `Registry` entry (`ComponentTypeInfo`)

In `ComponentTypeRegistry.cs`, add an entry to the `Registry` dictionary:

```csharp
[SymbolKind.MyNewPart] = new("XNP", "XNP",
    Category: ComponentCategory.Lumped,
    SearchTerms: ["XNP", "MyNewPart", "alias1"],
    IsCommon: false),
```

`ComponentTypeInfo` field meanings:

| Field | Type | Effect |
|---|---|---|
| `DisplayName` | `string` | Short label shown on the schematic (e.g. "R", "C"). Used by `DisplayName(kind, portCount)` for the type label and by the palette tile caption. |
| `InstancePrefix` | `string` | Prefix for auto-generated instance names (e.g. "R" → R1, R2). Used in `NextAvailableName`. |
| `DefaultShowTypeLabel` | `bool` | Whether the type label is shown by default on a freshly-placed component. `false` for Ground (self-identifying glyph). |
| `DefaultShowInstanceName` | `bool` | Whether the instance name is shown by default. `false` for Ground. |
| `Category` | `ComponentCategory` | Primary palette category. Controls sort order in All view and the on-tile tooltip. |
| `SearchTerms` | `IReadOnlyList<string>?` | Terms matched during palette search (case-insensitive substring). Include the display name, type code, and useful aliases. |
| `IsCommon` | `bool` | `true` puts the component in the curated Common virtual category. Reserve for the most-placed primitives. |
| `ExtraCategories` | `IReadOnlyList<ComponentCategory>?` | Additional categories. The component appears under `Category` AND each extra category in `ByCategory` filtering. `AllItems` still lists it once. |

The available categories are defined in the `ComponentCategory` enum (in the same file):
`Lumped`, `TransmissionLine`, `Microstrip`, `Sources`, `DataFiles`, `Terminals`, `Other`.

Once the entry is in `Registry`, `LibraryCatalog` picks it up automatically and the palette tile
appears — no view code changes are needed.

---

### Step 3 — Define ports in `SymbolPortDefs.For`

In `EditableSchematic.cs`, add a case to `SymbolPortDefs.For(SymbolKind kind, int portCount)`:

```csharp
case SymbolKind.MyNewPart:
    return [("1", 0f, -200f), ("2", 0f, 200f)];
```

Each tuple is `(Name, LocalX, LocalY)`.

**Coordinate convention.** Local coordinates use 100 units = 1 grid square. The component origin
is its geometric center. Two-terminal passive symbols are vertical: pin 1 at `(0, -200)` (top) and
pin 2 at `(0, +200)` (bottom). Box symbols (FET, ZPort, Sdd) are horizontal with ports at `x = ±200`.
See [standard-library-symbols.md](design/standard-library-symbols.md) for the full geometry spec.

**Connection-grid rule.** All pin positions must land on the connection grid P (multiples of
`GridSize = 100`). The `±200` values above satisfy this. Do not use fractional grid positions for
pins. This is the R7 invariant described in [grid-and-connectivity.md](design/grid-and-connectivity.md).

**Pin naming.** Use `"1"`, `"2"`, … for most signal pins. The ZPort `"ref"` pin is a special case
handled by `EmitInstance` in `NetExtractor.cs`: a pin named `"ref"` maps to `RefNetBinding` rather
than a signal net binding. Use `"ref"` only for a shared reference/ground pin on a multi-port network.

**Variadic types (`GeneratePorts`).** ZPort and Sdd delegate to the private `GeneratePorts(n)` helper,
which generates N+1 pins: N signal ports split left/right and one `"ref"` pin on the right. If your
new component also has a variable port count, follow the same `GeneratePorts` pattern and remember to
read `NumPorts` from `DefaultParameters` (see Step 5). All other types use a fixed pin list.

---

### Step 4 — Provide the symbol glyph in `BuiltInSymbols.Primitives`

In `BuiltInSymbols.cs`, add a static builder and cache field:

```csharp
private static readonly Symbol _myNewPart = BuildMyNewPart();

private static Symbol BuildMyNewPart() => Sym([
    L(  0, -200,   0,  -50),   // top lead
    // … drawing primitives (lines, arcs, curves) …
    L(  0,   50,   0,  200),   // bottom lead
], SymbolKind.MyNewPart);
```

Then add the dispatch in `Primitives(SymbolKind kind)`:

```csharp
SymbolKind.MyNewPart => _myNewPart,
```

The `_` fallback returns `_generic`, so an unregistered kind renders as a plain box — useful during
development but the full glyph must be provided before shipping.

**Primitive helpers.** The helpers in `BuiltInSymbols.cs` mirror the primitive types documented in
[symbol-editor.md](design/symbol-editor.md): `L` (line), `A` (arc), `Circ` (circle), `QC` (quadratic
curve), `RRect` (rounded rect), `Poly` (polygon), `PLine` (polyline), `Sine`. All use
`SymbolColorRole.SymbolLine`/`SymbolColorRole.SymbolPlus` — never literal colors.

**Glyph bounding box.** The glyph bounding box is computed automatically by `SymbolGeometry.ComputeBb`
from the primitives. Port lead endpoints (pin `LocalX/Y` values at `±200`) are included for variadic
types. No manual bounding-box code is needed.

**Cell-reference / user-drawn alternative.** If the glyph will be supplied by a `.csym` file rather
than hard-coded primitives, set `CellRef` on the placed `EditableComponent` and skip `BuiltInSymbols`
for that kind. The cell-ref resolution path is described in [symbol-editor.md](design/symbol-editor.md)
and [workspace-and-project-tree.md](design/workspace-and-project-tree.md).

---

### Step 5 — Default parameters in `DefaultParameters(kind, portCount)`

In `ComponentTypeRegistry.cs`, add a case to `DefaultParameters`:

```csharp
case SymbolKind.MyNewPart:
    return [new("R1", "50", "Ω", true, UnitDimension.Resistance),
            new("R2", "50", "Ω", true, UnitDimension.Resistance)];
```

`DefaultParam` field meanings:

| Field | Type | Meaning |
|---|---|---|
| `Name` | `string` | Parameter name — must match what the engine model reads at elaboration. |
| `Expression` | `string` | Default expression string (raw, not yet evaluated). |
| `Unit` | `string` | Unit string shown on the schematic and sent to the engine (e.g. `"Ω"`, `"nH"`, `"GHz"`). |
| `ShowOnSchematic` | `bool` | Whether this parameter is visible on the schematic label by default. |
| `Dimension` | `UnitDimension` | Physical dimension — drives the closed Unit ComboBox in the parameter editor. |

The `UnitDimension` enum values and their corresponding `UnitOptions` (the closed set of allowed unit
strings) are defined in `ComponentTypeRegistry.cs`:

```
None, Resistance, Inductance, Capacitance, Frequency, Voltage, Current, Power, Length, Angle
```

**Variadic port-count caveat.** For ZPort, the parameters include `NumPorts` (hidden, `ShowOnSchematic
= false`) followed by the Z[i,j] matrix entries. `DefaultParameters(SymbolKind.ZPort, n)` generates
1 + n×n params. The hidden `NumPorts` param is what `EditableComponent.PortCount` reads to know how
many signal pins to create via `SymbolPortDefs.For`. Always generate `NumPorts` as the first hidden
param for any variadic type.

Parameters whose `Name` or `Unit` contain Unicode glyphs (e.g. `Ω`, `µ`) are normalized to ASCII at
extraction time by `UnitNormalizer.ToEngineUnit` in `NetExtractor.EmitInstance` — the editor and the
engine do not need to agree on the glyph representation.

---

### Step 6 — Engine reference in `EngineReference(kind, portCount)`

Add a case to `EngineReference`:

```csharp
SymbolKind.MyNewPart => "MyNewPart",
```

This string becomes `Instance.Reference` in the extracted netlist and must resolve to a registered
engine model in the elaborator's component-model factory. The reference string may differ from
`DisplayName` — for example, `FetSdd` has `DisplayName = "FET"` but `EngineReference = "SDD"`. See
[data-model.md](design/data-model.md) §5 for the component-model factory contract.

If no case is added, the `_ => Get(kind).DisplayName` fallback emits the display name — acceptable
during development but the explicit case is required for production.

---

### Step 7 — Code-parse support in `TryParseCode`

Add cases to `TryParseCode` so the inline type-change field (e.g. typing "R" → "C" in the schematic)
recognises the new kind:

```csharp
case "XNP": kind = SymbolKind.MyNewPart; return true;
```

For variadic types with encoded port counts (like `Z{N}P` for ZPort), add a regex-style match in the
`default:` block following the ZPort and Sdd patterns. For most fixed-port types, one or more simple
`case` strings suffice.

`InstancePrefix(kind)` is automatically read from the registry entry; no separate registration is
needed.

---

### Step 8 — Verify

Build and exercise the new component:

1. `dotnet build` — zero errors or new warnings.
2. `dotnet test` — all existing tests pass; add a golden-reference test for the new type (see the
   `NetExtractorLayer*Tests` suite for examples).
3. Launch the app. Open the Library Palette. The new tile appears under the correct category and
   via keyword search using the `SearchTerms` you supplied.
4. Click the tile to arm placement (tile turns accent-color). The ghost appears on the schematic
   canvas with the correct glyph and pin squares.
5. Place the component (click). The instance name autoincrements using `InstancePrefix`.
6. Inspect labels: the type label shows `DisplayName` (not `SymbolKind.ToString()`); default
   parameter values render with the correct unit string.
7. File → Run (or Simulate → Run). The extracted netlist contains an `Instance` for the new
   component with the expected `Reference` string and no "unknown component" elaboration error.
8. Drag-and-drop from the palette tile also places correctly.

---

## 3. Field reference tables

### `ComponentTypeInfo` fields

| Field | Type | Default | Palette effect | Schematic effect |
|---|---|---|---|---|
| `DisplayName` | `string` | — | Tile caption; search match | Type label on placed component |
| `InstancePrefix` | `string` | — | — | Auto-naming prefix (R1, C3, X1) |
| `DefaultShowTypeLabel` | `bool` | `true` | — | Whether type label is on by default; `false` for Ground |
| `DefaultShowInstanceName` | `bool` | `true` | — | Whether instance name is on by default; `false` for Ground |
| `Category` | `ComponentCategory` | — | Primary category filter and sort order | — |
| `SearchTerms` | `IReadOnlyList<string>?` | `null` | Full-text search over all listed terms | — |
| `IsCommon` | `bool` | `false` | Appears in Common virtual category when `true` | — |
| `ExtraCategories` | `IReadOnlyList<ComponentCategory>?` | `null` | Component appears under each extra category in `ByCategory` filter; once in AllItems | — |

### `DefaultParam` fields

| Field | Type | Meaning |
|---|---|---|
| `Name` | `string` | Parameter name; must match engine model key |
| `Expression` | `string` | Default expression (unevaluated string) |
| `Unit` | `string` | Unit string shown on schematic; normalized to ASCII at extraction |
| `ShowOnSchematic` | `bool` | Visible on the schematic label by default |
| `Dimension` | `UnitDimension` | Drives closed Unit ComboBox options in the parameter editor |

---

## 4. Worked example — 2-port attenuator (`Atten2P`)

A symmetric resistive attenuator (`Pi` or `T` network) is not in the current registry. Here is every
edit needed to add it as `SymbolKind.Atten2P`.

**Verify it is absent.** Searching `SymbolKind` in `SchematicModel.cs` confirms no `Atten2P` value
exists. Searching `EngineReference` confirms no `"Atten"` or `"Attenuator"` string.

### 4a — Add the enum value

In `SchematicModel.cs`:

```csharp
public enum SymbolKind
{
    // … existing …
    Atten2P,
}
```

### 4b — Registry entry

In `ComponentTypeRegistry.cs`, inside the `Registry` dictionary:

```csharp
[SymbolKind.Atten2P] = new("ATT",  "AT",
    Category: ComponentCategory.Lumped,
    SearchTerms: ["ATT", "Atten2P", "attenuator", "pad"],
    IsCommon: false),
```

- `DisplayName = "ATT"` — shown as the type label on the schematic.
- `InstancePrefix = "AT"` — placed components are named AT1, AT2, etc.
- `Category = Lumped` — appears in the Lumped category and in All.
- `IsCommon = false` — omitted from the curated Common view.

### 4c — Port definitions

In `EditableSchematic.cs`, `SymbolPortDefs.For`:

```csharp
case SymbolKind.Atten2P:
    return [("1", 0f, -200f), ("2", 0f, 200f)];
```

Two-terminal, vertical orientation: pin 1 top, pin 2 bottom. Both on the connection grid.

### 4d — Symbol glyph

In `BuiltInSymbols.cs`:

```csharp
private static readonly Symbol _atten2p = BuildAtten2P();

// Two-port attenuator — box with diagonal attenuation bars.
private static Symbol BuildAtten2P() => Sym([
    L(  0, -200,   0,  -60),            // top lead
    L(-50,  -60,  50,  -60),            // box top
    L( 50,  -60,  50,   60),            // box right
    L( 50,   60, -50,   60),            // box bottom
    L(-50,   60, -50,  -60),            // box left
    L(-35,  -45,  35,   45),            // diagonal bar 1
    L( 35,  -45, -35,   45),            // diagonal bar 2
    L(  0,   60,   0,  200),            // bottom lead
], SymbolKind.Atten2P);
```

Add to the `Primitives` dispatch:

```csharp
SymbolKind.Atten2P => _atten2p,
```

### 4e — Default parameters

In `ComponentTypeRegistry.DefaultParameters`:

```csharp
case SymbolKind.Atten2P:
    return [new("Att",  "10",  "dB",  true, UnitDimension.None),
            new("Z0",   "50",  "Ω",   false, UnitDimension.Resistance)];
```

- `Att` — attenuation in dB, shown on the schematic.
- `Z0` — reference impedance, hidden from the schematic label by default.
- `UnitDimension.None` for `Att` (dB is not in the dimension table; the unit is a literal string).

### 4f — Engine reference

In `ComponentTypeRegistry.EngineReference`:

```csharp
SymbolKind.Atten2P => "Attenuator2P",
```

The engine model must be registered under `"Attenuator2P"` in the component-model factory.

### 4g — Code-parse support

In `ComponentTypeRegistry.TryParseCode`:

```csharp
case "ATT":
case "ATTEN2P": kind = SymbolKind.Atten2P; return true;
```

### 4h — Verify checklist

- [ ] `dotnet build` green.
- [ ] `dotnet test` green; new extraction test passes for Atten2P.
- [ ] "ATT" tile appears in the Lumped category and in search for "att", "attenuator", "pad".
- [ ] Tile click arms placement; ghost shows box-with-X glyph and two pin squares.
- [ ] Placed component: instance name = "AT1"; type label = "ATT"; `Att = 10 dB` visible; `Z0` hidden.
- [ ] `Run` extracts an `Instance { Reference = "Attenuator2P" }` without elaboration error.
- [ ] Drag-and-drop from the tile also places correctly.

---

## 5. Gotchas and conventions

**Variadic port count (N signal pins vs N+1 schematic pins).** For ZPort and Sdd, the schematic has
N+1 pins (N signal ports plus one `"ref"` pin), but the engine sees N ports. `EditableComponent.PortCount`
returns N (read from the `NumPorts` parameter). `SymbolPortDefs.For(kind, portCount)` returns N+1 entries.
`EmitInstance` in `NetExtractor.cs` handles the split — signal pins go into `NetBindings`, the `"ref"`
pin goes into `RefNetBinding`. Fixed-port types do not need a `NumPorts` parameter.

**Pins must land on the connection grid P.** Local coordinates for all pins must be multiples of 100.
`±200` is the standard lead length; `0` is the center. Any other value must still be a multiple of 100.
Fractional positions break the R7 on-grid invariant and cause connectivity failures at extraction.

**Use `DisplayName(kind, portCount)`, not `SymbolKind.ToString()`.** The type label on the schematic
reads `ComponentTypeRegistry.DisplayName(kind, portCount)`, which is port-count-aware for variadic types
(ZPort with portCount=2 → `"Z2P"`). Never call `kind.ToString()` for user-facing text.

**Ground-style label suppression.** `DefaultShowTypeLabel = false` / `DefaultShowInstanceName = false`
on a registry entry suppress the respective labels by default on all freshly-placed instances of that
kind. The suppression is seeded at placement from the registry and stored per-instance in
`EditableComponent.ShowTypeLabel` / `ShowInstanceName`. Use this for self-identifying glyphs like Ground.

**Where palette category/search/Common come from.** All three come exclusively from the `ComponentTypeInfo`
entry in `Registry`. `LibraryCatalog` and the palette views read the registry at startup; nothing in the
palette view layer hard-codes component lists.

**Registry is the v1 contribution point — compiled external model libraries are deferred (v2).** In v1,
every built-in component is registered by adding a `SymbolKind` value and a `Registry` entry. The v2
path (loading external `.clib` model libraries with their own type registrations) is out of scope and
must not be implemented without explicit design discussion (see `docs/PRD.md` for the scope boundary).

---

## Open questions

These gaps were observed while writing this guide; they are recorded here rather than resolved by
inventing an API.

1. **`EngineReference` for `Generic`.** The `_` fallback in `EngineReference` emits
   `Get(kind).DisplayName`, which for `SymbolKind.Generic` is `"X"`. Whether the elaborator treats
   `"X"` as a valid subcircuit reference or an error is not visible from the registry code alone.
2. **Unit dimension for dB / dBm.** `UnitDimension.Power` covers `dBm` in `UnitOptions`, but there
   is no `UnitDimension` value for dimensionless ratios like dB attenuation. The worked example
   uses `UnitDimension.None` with a literal `"dB"` unit string, which passes the unit to the engine
   as-is. Whether the engine accepts `"dB"` as a unit token for attenuation parameters needs
   confirmation against the engine model's parameter parser.
