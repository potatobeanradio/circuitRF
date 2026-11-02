# Brief — Loadpull UI 02: Source Tuner & Load Tuner variants

**Goal:** Add `SymbolKind.SourceTuner` and `SymbolKind.LoadTuner` — the **same engine component as the
general `Tuner`** (brief 01), with a different glyph and instance prefix (`SourceTuner1…`, `LoadTuner1…`).
Each has a **single pin**; the Source/Load distinction now also carries a small but real difference in
**net ordering** (see below), chosen to match the engine's role-dependent topology. The Source glyph borrows
the source-drive motif from the P1Tone symbol.

**Depends on:** brief 01 (general Tuner — these reuse its engine wiring, default params, and user-param
template, and slot into the same `EmitInstance` branch).

**Reads with:** `docs/skills/adding-a-library-component.md`, `docs/sonnet-briefs/palette-contributor-guide.md`,
`loadpull.md` §1.1 / §2.1 (role assignment), and `src/Engine/Loadpull/CLAUDE.md` (the role-dependent
topology).

## Design decisions for this brief (owner-confirmed 2026-06-23)

1. **One pin each = the DUT-facing net.**
   - **LoadTuner:** pin on the **left** (load sits to the right of the DUT; pin points left toward it).
   - **SourceTuner:** pin on the **right** (source sits to the left of the DUT; pin points right toward it).
   This pin-side convention is a schematic-layout convenience (source → DUT → load, left to right).
2. **Reference / second net is not a pin (DEFERRED).**
   - **LoadTuner:** hidden net hard-coded ground `"0"` (the reference) — like the general Tuner.
   - **SourceTuner:** the hidden net is the engine's internal RF **source** node (`n_outer`), where the
     embedded `V_1Tone` drives **against ground**. It therefore **cannot be ground** — it is an
     **auto-generated unique internal net**, not `"0"`.
3. **Wider, more illustrative glyphs than the compact general Tuner** (the task spec: "drawn wider than a
   general Tuner"). General Tuner is 300 wide; Source/Load are ~400 wide × 200 tall. Source shows the
   P1Tone drive motif; Load is passive.
4. **Exposing the reference/source net as a pin is deferred** (documented per brief 01).

## The equivalence statement (the §3 deliverable) — now with one nuance

All three tuner tiles are the **same engine component**: `EngineReference` = `"Tuner"`, identical default
parameters, identical `UserParamTemplate`. Differences:
- **glyph** (cosmetic intent cue),
- **instance prefix** (`Tuner` / `SourceTuner` / `LoadTuner`),
- **net ordering** at extraction, which now encodes the intended role:
  - **Tuner + LoadTuner (load-style):** pin → `Nodes[0]` (DUT-facing), `Nodes[1]` = `"0"` (ground).
  - **SourceTuner (source-style):** `Nodes[0]` = unique internal source net, `Nodes[1]` = pin (DUT-facing).

**Consequence to document:** because the net ordering matches a role, a **SourceTuner** symbol must be named
`SourceTuner=` in the Loadpull analysis, and a **Tuner/LoadTuner** symbol named `LoadTuner=`. (Mixing them —
e.g. naming a LoadTuner symbol as `SourceTuner=` — would mis-bind the nets.) State this in:
- XML-doc on `SymbolKind.SourceTuner`/`LoadTuner`,
- the `DefaultParameters` case comment,
- a sentence in `docs/design/loadpull.md` §1: *"In the GUI, the Source/Load/general Tuner tiles place the
  identical `Tuner` component (same EngineReference and parameters); they differ in glyph, instance prefix,
  and the single-pin net ordering (load-style: pin = DUT-facing, reference = implicit ground; source-style:
  pin = DUT-facing, internal source net auto-generated). Match the symbol to its analysis role
  (`LoadTuner=`/`SourceTuner=`). Exposing the reference/source net as a second pin is deferred."*

## Implementation

### 1 — Enum
`SchematicModel.cs`: add `SourceTuner`, `LoadTuner` near `Tuner`. XML-doc each:
```csharp
/// <summary>Source-side Tuner. Same engine component as <see cref="Tuner"/> (EngineReference "Tuner",
/// same parameters). Differs by glyph, instance prefix, and SOURCE-STYLE single-pin net ordering
/// (pin = DUT-facing = Nodes[1]; the internal source net = Nodes[0], auto-generated, NOT ground).
/// Pin on the RIGHT. Must be named SourceTuner= in the Loadpull analysis. Reference/source net as a
/// pin is deferred.</summary>
SourceTuner,
/// <summary>Load-side Tuner. Same engine component as <see cref="Tuner"/>; LOAD-STYLE ordering
/// (pin = DUT-facing = Nodes[0]; reference Nodes[1] hard-coded ground "0"). Pin on the LEFT. Must be
/// named LoadTuner= in the analysis. Reference pin deferred.</summary>
LoadTuner,
```

### 2 — Registry entries
```csharp
[SymbolKind.SourceTuner] = new("SourceTuner", "SourceTuner",
    Category: ComponentCategory.Sources,
    SearchTerms: ["SourceTuner", "source tuner", "tuner", "sourcepull", "drive", "loadpull"],
    IsCommon: true,
    ExtraCategories: [ComponentCategory.Terminals]),
[SymbolKind.LoadTuner]   = new("LoadTuner", "LoadTuner",
    Category: ComponentCategory.Terminals,
    SearchTerms: ["LoadTuner", "load tuner", "tuner", "loadpull", "termination"],
    IsCommon: true),
```
Prefixes give `SourceTuner1`, `LoadTuner1`.

### 3 — Ports (`SymbolPortDefs.For`) — one pin; Load left, Source right
The wider (400) box has edges at x=±200, so a pin at ±300 gives a clean 100-unit lead on grid.
```csharp
// LoadTuner: single DUT-facing pin on the LEFT (like the general Tuner). Reference = implicit ground.
case SymbolKind.LoadTuner:
    return [("1", -300f, 0f)];
// SourceTuner: single DUT-facing pin on the RIGHT. The internal source net is auto-generated at
// extraction (NOT a pin, NOT ground).
case SymbolKind.SourceTuner:
    return [("1", 300f, 0f)];
```

### 4 — Glyphs (`BuiltInSymbols.cs`) — wider than the general Tuner; borrow the P1Tone drive motif
Read `BuildP1Tone()` first — its source-drive **circle + 1-cycle sine** is the motif to borrow for the
SourceTuner (a source tuner owns the internal `V_1Tone`, `loadpull.md` §1.1). The LoadTuner is passive
(no drive circle). Both are ~400 wide × 200 tall (edges ±200/±100), wider than the 300-wide general Tuner.
```csharp
private static readonly Symbol _sourceTuner = BuildSourceTuner();
private static readonly Symbol _loadTuner   = BuildLoadTuner();

// ── Source Tuner — wider box + P1Tone-style source-drive circle; single RIGHT pin ──
// 400 × 200. The drive circle + 1-cycle sine (borrowed from P1Tone) marks that a source
// tuner OWNS its internal RF drive (loadpull.md §1.1). Pin "1" (DUT-facing) at (+300,0).
private static Symbol BuildSourceTuner() => Sym([
    L( 200,   0,  300,   0),           // right lead → DUT-facing pin
    RRect(0,  0,  400,  200,  20),     // wider body
    Circ(-90,  0,  48),                // source-drive circle (P1Tone motif)
    Sine(-90,  0,  20,   1,   90, SineAxis.Horizontal),
    Circ( 90,  0,  40),                // tunable-Γ mark
    L(90, 0, 118, -28),                // slug needle
], SymbolKind.SourceTuner);

// ── Load Tuner — wider box, passive (no drive circle); single LEFT pin ────
// 400 × 200. Passive termination → NO drive circle. Γ-tuning mark + a small termination
// zigzag to read as a passive load. Pin "1" (DUT-facing) at (−300,0).
private static Symbol BuildLoadTuner() => Sym([
    L(-300,   0, -200,   0),           // left lead → DUT-facing pin
    RRect(0,  0,  400,  200,  20),     // wider body
    Circ(-90,  0,  40),                // tunable-Γ mark
    L(-90, 0, -62, -28),               // slug needle
    PLine(90,-44, 90,-28, 110,-14, 70,12, 110,36, 90,50, 90,55),  // termination zigzag (passive load)
], SymbolKind.LoadTuner);
```
Dispatch:
```csharp
case SymbolKind.SourceTuner: return _sourceTuner;
case SymbolKind.LoadTuner:   return _loadTuner;
```
Coordinates are a starting point — adjust so the motifs sit cleanly in the 400×200 box. The requirements
are: wider than the general Tuner, single pin (Source right / Load left), Source shows a drive motif, Load
is passive. Only `SymbolColorRole.SymbolLine`.

### 5 — Default parameters — DELEGATE to the Tuner case
```csharp
case SymbolKind.Tuner:
case SymbolKind.SourceTuner:
case SymbolKind.LoadTuner:
    // Same engine component as the general Tuner — same params, same EngineReference "Tuner".
    // Only glyph + prefix + single-pin net ordering differ (loadpull.md §1, §9).
    return [ /* the Tuner default-param list from brief 01 */ ];
```

### 6 — Engine reference — all three return `"Tuner"`
```csharp
SymbolKind.Tuner       => "Tuner",
SymbolKind.SourceTuner => "Tuner",
SymbolKind.LoadTuner   => "Tuner",
```

### 7 — `UserParamTemplate` — all three share the Tuner template
```csharp
SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner => new IndexedParamGroup(
    NameFormats: ["Z[{0}]"], DefaultUnits: ["Ω"], ShowOnSchematic: [true],
    Dimensions: [UnitDimension.Resistance], FirstAddIndex: 2, SkipIndices: null),
```

### 8 — Code-parse
```csharp
case "SOURCETUNER":
case "SRCTUNER": kind = SymbolKind.SourceTuner; return true;
case "LOADTUNER":
case "LDTUNER":  kind = SymbolKind.LoadTuner;   return true;
```

### 9 — Extraction — extend the brief-01 `EmitInstance` Tuner branch
LoadTuner joins the load-style branch from brief 01 (already added there). **SourceTuner needs source-style
ordering** with a unique internal net for `Nodes[0]`:
```csharp
if (comp.Symbol is SymbolKind.Tuner or SymbolKind.LoadTuner)
{
    // load-style: [pinNet, "0"]   (from brief 01)
    var def = GetEffectivePortDefs(model, comp, cellRefResolutions)[0];
    var (px, py) = model.PortWorldOf(comp, def);
    string pinNet = NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys);
    return new Instance(comp.InstanceName, "Tuner", new List<string> { pinNet, "0" }, overrides2);
}
if (comp.Symbol == SymbolKind.SourceTuner)
{
    // source-style: [uniqueSourceNet, pinNet]. Nodes[0] is the internal RF source node where the
    // embedded V_1Tone drives against ground — it must be a UNIQUE, NON-GROUND net (and must not use
    // the reserved "__" prefix). Per-instance unique; guard against collision with a real net name.
    var def = GetEffectivePortDefs(model, comp, cellRefResolutions)[0];
    var (px, py) = model.PortWorldOf(comp, def);
    string pinNet    = NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys);
    string sourceNet = UniqueInternalNetName($"nsrc_{comp.InstanceName}", netNames);
    return new Instance(comp.InstanceName, "Tuner", new List<string> { sourceNet, pinNet }, overrides2);
}
```
`UniqueInternalNetName(seed, netNames)`: return `seed` if it is not already an assigned net name; otherwise
append `_2`, `_3`, … until unique. (Collect the set of names from `netNames.Values`; the instance name makes
`seed` almost always unique already.) **Alternative** (more robust, optional): route the source net through
the same synthetic-key + auto-name path the extractor already uses for detached ports (search
`detachedKeys` / `AddDetachedKey` in `NetExtractor`), which is guaranteed unique. Either is acceptable; the
synthesized-name approach is simpler — just guard uniqueness.

### 10 — Tests
- **Equivalence (Load == general Tuner):** a `LoadTuner` and a `Tuner` placed with identical values on the
  same node extract to identical `Instance`s except the instance name — both `Reference="Tuner"`, nets
  `[n_dut, "0"]`, identical params. This is the executable proof of equivalence; keep it.
- **Source ordering:** a `SourceTuner` with its pin on `n_gate` extracts to `Reference="Tuner"`, nets
  `[<unique-non-"0" net>, "n_gate"]` (source net first, pin/DUT second). Assert the first net is neither
  `"0"` nor a user net, and is unique per instance (place two SourceTuners → two distinct source nets).

## Verify
1. `dotnet build` zero warnings; `dotnet test` green incl. the equivalence + source-ordering tests.
2. Three tiles: **Tuner** (300-wide, pin left), **SourceTuner** (400-wide, drive circle, pin **right**),
   **LoadTuner** (400-wide, passive, pin **left**). Search "tuner" finds all three.
3. Place each; verify pin sides, glyphs, prefixes, and `Z[1]=50 Ω`.
4. Firewall passes.
