# Sonnet Brief — Add the IProbe (current probe / ammeter) standard-library component

## Context
The engine is already complete: `ComponentModelFactory._registry` maps `"IProbe"` → `new IProbeModel()`,
and DC packs an `I:<probe>` cube per IProbe (DcResultPacker). What's missing is the **schematic-UI side**
so the user can place an IProbe from the palette, wire it in series (e.g. a FET drain leg), and have it
serialize to a `.cnl` instance with `Reference = "IProbe"`. The palette auto-derives from
`SymbolKind` + `ComponentTypeRegistry` (see LibraryCatalog comment: "adding a SymbolKind + registry
entry makes the new type appear in AllItems automatically"), so no catalog edit is needed.

IProbe is a 2-terminal, **parameterless** device (a 0 V ammeter): current flows pin1 → pin2.

Build 0W/0E (`TreatWarningsAsErrors=true`). After editing, build and fix any non-exhaustive
`switch` over `SymbolKind` that the new enum value exposes (search the solution for `SymbolKind` switches
that lack a `default:` arm — add an `IProbe` case there mirroring the closest 2-terminal kind, e.g. R).

## Edit 1 — add the enum value
`src/Ui/Schematic/SchematicModel.cs`, in `public enum SymbolKind` — add `IProbe` (place it after `Pin`,
near the other terminal-ish kinds):
```csharp
    Pin,
    IProbe,
    FetSdd,
```

## Edit 2 — pin layout in SymbolPortDefs
Find the `SymbolPortDefs` class (it backs `SymbolPortDefs.For(SymbolKind)` — grep the solution; it's the
static class returning the per-kind pin list used by `BuiltInSymbols.Sym`). Add an `IProbe` entry with
**two pins at the bottom, 100 apart, both on-grid**, matching the symbol art in Edit 3:
- pin index 0 (pin1, current in / "np"): LocalX = 0,   LocalY = 100
- pin index 1 (pin2, current out / "nm"): LocalX = 100, LocalY = 100

Mirror exactly how an existing fixed 2-pin kind is registered there (e.g. how `Term` or `Pin` provides
its `For(...)` list). Pin names may be left null/empty (the symbol carries the visual arrow for
direction). PortCount falls out of pin count (2) — no S-parameter port concept here.

## Edit 3 — symbol art in BuiltInSymbols
`src/Ui/Schematic/BuiltInSymbols.cs`. Add a cache field, a switch case, and the builder.

(a) cache field alongside the others:
```csharp
    private static readonly Symbol _iprobe       = BuildIProbe();
```
(b) switch case in `Primitives(SymbolKind kind, int portCount)` (next to `case SymbolKind.Pin:`):
```csharp
            case SymbolKind.IProbe:     return _iprobe;
```
(c) the builder. Pins at the bottom (0,100)/(100,100); stems rise 100 to the connector at y=0; a
right-pointing filled arrow sits on the connector; an ammeter "window" (curved top + bottom, top wider,
angled sides) floats above; a rounded rect encloses window + arrow. Authored clean on the 100 grid:
```csharp
    // ── IProbe — current probe / ammeter ─────────────────────────────────────
    // 2-terminal series ammeter. Pins at the BOTTOM (0,100)/(100,100), 100 apart.
    // Stems rise to a horizontal connector at y=0 carrying a right-pointing current
    // arrow (pin1 left → pin2 right). Above the connector: an ammeter window
    // (curved top/bottom via quad curves, top edge wider → angled sides). A rounded
    // rect encloses the window + arrow; the connector leads exit it to the stems.
    private static Symbol BuildIProbe() => Sym([
        L(  0, 100,   0,   0),                 // left stem  (pin1 → connector)
        L(100, 100, 100,   0),                 // right stem (pin2 → connector)
        L(  0,   0, 100,   0),                 // horizontal connector
        Poly(true, 40, -10, 60, 0, 40, 10),    // current arrow (filled), points right
        QC(35, -22, 50, -16, 65, -22),         // window bottom edge (shorter, bows down)
        QC(25, -52, 50, -58, 75, -52),         // window top edge (longer, bows up)
        L(35, -22, 25, -52),                   // window left side (angled out toward top)
        L(65, -22, 75, -52),                   // window right side (angled out toward top)
        RRect(50, -24, 80, 84, 10),            // enclosing rounded rect (window + arrow)
    ], SymbolKind.IProbe);
```
> `L`, `QC`, `Poly`, `RRect`, `Sym` are the existing private helpers in this file — no new helpers needed.

## Edit 4 — ComponentTypeRegistry (4 spots)
`src/Ui/Schematic/ComponentTypeRegistry.cs`.

(a) `Registry` dictionary — add:
```csharp
        [SymbolKind.IProbe]        = new("IProbe", "IP",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["IProbe", "I", "ammeter", "current", "probe", "meter"],
            IsCommon: true),
```
> InstancePrefix `"IP"` → instances IP1, IP2, … so the DC current cube reads `I:IP1` (matches the
> `I:<instancePath>` convention and the family-of-curves brief's `IPd` example).

(b) `EngineReference(SymbolKind, int)` switch — add (so serialization writes the factory key):
```csharp
        SymbolKind.IProbe        => "IProbe",
```

(c) `DefaultParameters(SymbolKind, int)` — IProbe is parameterless; it falls into the `default: return [];`
arm, so **no edit needed** unless you prefer an explicit `case SymbolKind.IProbe: return [];` for clarity.

(d) `TryParseCode(...)` switch — add aliases:
```csharp
            case "IPROBE":
            case "IP":     kind = SymbolKind.IProbe;        return true;
```

## Gate / manual checks
1. Build 0W/0E. (Fix any exhaustive `SymbolKind` switch the new value breaks.)
2. Palette: "IProbe" appears (Terminals category and Common); search "ammeter"/"current"/"IP" finds it.
3. Place an IProbe, hover/zoom: pins at the two bottom points 100 apart; arrow points pin1→pin2;
   window sits above the connector inside a rounded rect; rotation/mirror behave like other symbols.
4. Wire an IProbe in series in a DC test (e.g. a resistor + Vdc loop, or a FET drain leg). Run a DC
   analysis. The result DataSet contains an `I:IP1` cube; in Data Display you can add a trace on it.
5. FET family of curves: nested `SW_Vgs ⊃ SW_Vds ⊃ DC1`, IProbe in the drain leg → `I:IPd` is a
   `[Vgs, Vds]` cube; plot with Vds = X, Vgs = Family (7.3b) for the I–V fan.
6. Round-trip: save the schematic, reopen — the IProbe persists and re-elaborates to an IProbeModel
   (Reference "IProbe" in the .cnl).

## Cleanup
The user's freehand reference at `<workspace>/probe_test.csym` was only a draft for this
geometry — it can be ignored/deleted; the built-in `BuildIProbe()` above supersedes it.
