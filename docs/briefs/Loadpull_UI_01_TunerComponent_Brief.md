# Brief — Loadpull UI 01: the general `Tuner` library component

**Goal:** Add a new built-in component, `SymbolKind.Tuner`, so a user can draw the programmable RF
termination (`loadpull.md` §1) on a schematic. This is the *general* tuner; the Source and Load variants
are brief 02. **Engine is done** — the `Tuner` engine model (`TunerModel`) and its factory wiring
(`ComponentModelFactory`, reference string `"Tuner"`) already exist and are tested. This brief is the
UI/palette/extraction layer only.

**Reads with:** `docs/skills/adding-a-library-component.md` (the device-component recipe; this Tuner is a
**device** archetype, not annotation) and `docs/sonnet-briefs/palette-contributor-guide.md` (the full
step-by-step). Follow those; this brief gives the Tuner-specific values and the key decisions.

## Design decisions for this brief (owner-confirmed 2026-06-23)

1. **One pin, on the LEFT.** The general Tuner has a **single** schematic pin = the **DUT-facing net**,
   placed on the left side of the glyph. (For LoadTuner the pin is also left; for SourceTuner it is right —
   brief 02.) Lab tuners connect a single DUT terminal; the reference is implicit.
2. **Reference net hard-coded to ground `"0"`.** The engine's `TunerModel` needs **two** nets
   (`Nodes[0]`, `Nodes[1]`). For the general Tuner (load-style ordering, see below) the pin supplies
   `Nodes[0]` (DUT-facing) and the extractor hard-codes `Nodes[1] = "0"` (the reference, ground). The
   reference is **not** exposed as a pin.
3. **Exposing the reference net as a pin is DEFERRED.** Document (code comment + a note in
   `docs/design/loadpull.md`) that the reference net is currently implicit-ground and a second pin can be
   added later if users need to wire a non-ground reference (e.g. differential terminations). This brief
   does NOT implement a second pin.
4. **Compact glyph, 300 × 200.** The general Tuner is for advanced users who want a small footprint, not a
   detailed pictorial reminder of what a tuner does. Keep the glyph minimal: a rounded rect plus a small
   tuning mark. (Source/Load get richer, slightly wider glyphs in brief 02.)

## The engine contract you must honor

Read `src/Core/Devices/TunerModel.cs` and `src/Engine/Loadpull/CLAUDE.md`. The `TunerModel` declares
`PortCount => 1` but its `Stamp` reads `c.Nodes[0]` and `c.Nodes[1]`, interpreted **by role** (the role is
assigned by the Loadpull analysis at run time, not by the symbol):

- **Load role:** `Nodes[0]` = DUT-facing net, `Nodes[1]` = reference (ground).
- **Source role:** `Nodes[0]` = internal RF source node (the embedded `V_1Tone` drives it **against
  ground** — so it cannot be ground), `Nodes[1]` = DUT-facing net.

The internal `__tuner_<inst>_block` / `__tuner_<inst>_bias` nodes (`Nodes[2]`, `Nodes[3]`) are minted by the
**elaborator** — they are not symbol pins and not your concern. The instance-name param `TunerName` is also
injected by the elaborator from the instance name (mirrors P1Tone's `P1ToneName`); you do not emit it.

**The general Tuner uses LOAD-STYLE net ordering:** pin → `Nodes[0]` (DUT-facing), and `Nodes[1]` hard-coded
`"0"`. This means the general Tuner is electrically identical to a LoadTuner and, like it, must be named
`LoadTuner=` in the Loadpull analysis (or run in a non-loadpull S-param sim, where the role defaults to
Load). The **SourceTuner uses source-style ordering** (pin → `Nodes[1]`, a unique internal net →
`Nodes[0]`) — that is brief 02. Because the engine reference is already `"Tuner"`
(`ComponentModelFactory._parameterizedTypes` contains `"Tuner"` and `CreateTunerModel(...)` is implemented),
`EngineReference(SymbolKind.Tuner)` must return exactly `"Tuner"`. The factory reads these parameter keys:
per-harmonic `Z[k]`/`G[k]` (`Z[1]`/`G[1]` **required**), `Zdefault` (default `1e-6`), `Z0` (default `50`),
`BiasTee` (string `"on"`), `Vbias` (real).

## Step-by-step (device archetype; grep `SymbolKind.Term` and `SymbolKind.Tline` for touchpoints)

### 1 — Enum
`src/Ui/Schematic/SchematicModel.cs`: add `Tuner` to `SymbolKind` (near `Term`/`P1Tone`).

### 2 — Registry entry
`src/Ui/Schematic/ComponentTypeRegistry.cs`, in `Registry`:
```csharp
[SymbolKind.Tuner] = new("Tuner", "Tuner",
    Category: ComponentCategory.Terminals,
    SearchTerms: ["Tuner", "tuner", "loadpull", "load pull", "sourcepull", "termination", "Z", "gamma"],
    IsCommon: true,
    ExtraCategories: [ComponentCategory.Sources]),
```
`DisplayName = "Tuner"`, `InstancePrefix = "Tuner"` → auto-names `Tuner1`, `Tuner2`, …

### 3 — Ports (`SymbolPortDefs.For`) — ONE pin, on the left
`src/Ui/Schematic/EditableSchematic.cs`, add a case:
```csharp
// Tuner: 1-port termination, single DUT-facing pin on the LEFT. The reference net is
// hard-coded to ground "0" at extraction (NOT a pin) — exposing it as a pin is DEFERRED
// (loadpull.md §1; can add a 2nd pin later if users need a non-ground reference).
case SymbolKind.Tuner:
    return [("1", -300f, 0f)];   // single pin, left; on grid (multiple of 100)
```

### 4 — Glyph (`BuiltInSymbols.cs`) — compact 300 × 200, minimal
Add a cache field + builder + dispatch case. The box is **300 wide × 200 tall** (edges at x=±150, y=±100),
deliberately small. Keep the interior mark minimal.
```csharp
private static readonly Symbol _tuner = BuildTuner();

// ── Tuner — compact rounded-rect termination, single left pin ─────────────
// 300 × 200 box (edges ±150 / ±100). Advanced users want a small footprint, not a
// detailed pictorial; keep the interior mark minimal. Pin "1" (DUT-facing) at (−300,0);
// the reference net is implicit ground (hard-coded "0" at extraction — deferred to expose).
private static Symbol BuildTuner() => Sym([
    L(-300,   0, -150,   0),          // left lead to box edge (DUT-facing pin)
    RRect(0,  0,  300,  200,  20),    // compact body, 300 × 200
    Circ(40,  0,   34),               // small tuning mark (a Smith-ish circle)
    L(40, 0, 64, -24),                // short "slug" needle
], SymbolKind.Tuner);
```
Dispatch in `Primitives(SymbolKind kind, int portCount)`:
```csharp
case SymbolKind.Tuner:  return _tuner;
```
Only `SymbolColorRole.SymbolLine`. Glyph BB is auto-computed. Treat the interior mark as tunable — the
requirement is "compact 300×200, minimal," not these exact coordinates.

### 5 — Default parameters (`DefaultParameters`)
Names must match what `CreateTunerModel` reads. `Z[1]` required; expose bias-tee controls.
```csharp
// Tuner: programmable termination (loadpull.md §1). Z[1] REQUIRED (fundamental); Zdefault is the
// catch-all (engine default 1e-6); Z0 sets Γ-normalisation for any G[k] form (default 50). BiasTee=
// "on"/"off" toggles the internal bias-tee + supply; Vbias is the DC bias at the DUT-facing port.
// The Loadpull analysis decides the tuned harmonic (TuneHarm) and the role (Load/Source), NOT this
// component. Add Z[2], Z[3], … via the parameter-editor "+".
case SymbolKind.Tuner:
    return [new("Z[1]",     "50",   "Ω", true,  UnitDimension.Resistance),
            new("Zdefault", "1e-6", "Ω", false, UnitDimension.Resistance),
            new("Z0",       "50",   "Ω", false, UnitDimension.Resistance),
            new("BiasTee",  "off",  "",  false, UnitDimension.None),
            new("Vbias",    "0",    "V", false, UnitDimension.Voltage)];
```
`Z[1]` shows on the schematic; the rest hidden. `Z[1]` accepts complex literals (e.g. `50+j*10`). Note in
the comment that `BiasTee=on` is required by the `Loadpull` directive (`loadpull.md` §1.1) — `off` is fine
for a standalone tuner.

### 6 — Engine reference
```csharp
SymbolKind.Tuner => "Tuner",
```

### 7 — `UserParamTemplate` (the "+" adds Z[2], Z[3], …)
The fundamental is the `Z[1]` row itself, so the first addable index is **2**, none skipped:
```csharp
SymbolKind.Tuner => new IndexedParamGroup(
    NameFormats:     ["Z[{0}]"],
    DefaultUnits:    ["Ω"],
    ShowOnSchematic: [true],
    Dimensions:      [UnitDimension.Resistance],
    FirstAddIndex:   2,
    SkipIndices:     null),
```

### 8 — Code-parse
```csharp
case "TUNER": kind = SymbolKind.Tuner; return true;
```

### 9 — Extraction: emit TWO nets (pin + hard-coded ground) — special-case in `EmitInstance`
The symbol has 1 pin but the engine needs 2 declared nets. Read `NetExtractor.EmitInstance`; its default
path emits one net per port def. Add a branch for the Tuner that emits the pin's net as `Nodes[0]` and a
literal `"0"` as `Nodes[1]`:
```csharp
// Tuner family: 1 symbol pin (DUT-facing) but the engine TunerModel needs 2 declared nets.
// General Tuner + LoadTuner use LOAD-STYLE ordering: pin → Nodes[0] (DUT-facing),
// Nodes[1] = "0" (reference, hard-coded ground; exposing it as a pin is DEFERRED).
// (SourceTuner uses source-style ordering — see brief 02 — handled in the same branch.)
if (comp.Symbol is SymbolKind.Tuner or SymbolKind.LoadTuner /* or SourceTuner in brief 02 */)
{
    var def = GetEffectivePortDefs(model, comp, cellRefResolutions)[0];
    var (px, py) = model.PortWorldOf(comp, def);
    string pinNet = NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys);
    var tunerNets = new List<string> { pinNet, "0" };   // [Nodes0 = DUT-facing, Nodes1 = ground]
    return new Instance(comp.InstanceName, "Tuner", tunerNets, overrides2);
}
```
Place this before the generic terminal-emission loop. `overrides2` is the filtered parameter list (same as
the generic path; in brief 03 the display-only `ShowBias` param is added to the dropped set). Brief 02
extends this branch for `SourceTuner` (different ordering); leave a clear TODO/marker so brief 02 slots in.

### 10 — Extraction test
Mirror the P1Tone extraction test. Assert a placed `Tuner` with `Z[1]=50`, its pin on net `n_dut`, extracts
to `Instance { Reference = "Tuner" }` with **exactly two** `NetBindings` `["n_dut", "0"]` (in that order),
and parameters `Z[1]`/`Zdefault`/`Z0`/`BiasTee`/`Vbias` as `ParameterAssignment`s (unit-normalized). Add a
`Z[2]` via the user-param path and assert it round-trips. Optionally assert the instance elaborates without
an "unknown component" error.

## Out of scope
- Source/Load variants → brief 02. Bias-supply rendering + the `ShowBias` filter + polish → brief 03.
  Analysis authoring → briefs 04–07. A second (reference) pin → deferred.

## Verify
1. `dotnet build` zero new warnings; `dotnet test` green incl. the extraction test.
2. Launch: the **Tuner** tile appears (Terminals + Sources) and via search. Placing shows the compact
   300×200 box with a **single pin on the left**; auto-names `Tuner1`; `Z[1] = 50 Ω` visible, rest hidden;
   "+" adds `Z[2]`.
3. Firewall passes.
