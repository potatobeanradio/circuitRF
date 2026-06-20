# Brief #3: NonlinearC symbol + palette + type registry

Design ref: `docs/design/nonlinear-in-linear-engines.md` §4.3. Depends on briefs #1 (engine seam) and #2
(`NonlinearCModel` + factory `"NonlinearC"`). This brief makes NonlinearC a placeable built-in: a new
`SymbolKind`, a registry entry (label/params/engine-reference), the symbol glyph (linear C + three
nonlinear slashes), and the palette entry (automatic once registered). Ends with the full-pipeline
regression anchor: a constant-C NonlinearC must give the same S-parameters as a linear C.

UI/Schematic layer, framework-free where noted. Build **0W/0E**; add the test; report count; newest-first
changelog. No persistence/format changes (a placed NonlinearC is just an instance with `C0…` params).

Files: `SchematicModel.cs` (SymbolKind enum), `ComponentTypeRegistry.cs`, `BuiltInSymbols.cs`, the
`SymbolPortDefs.For` source (locate — referenced by `BuiltInSymbols.Sym`), and a new engine/UI integration
test. The palette (`LibraryCatalog`/`PaletteTool`) and renderers need **no** changes — they enumerate
`SymbolKind` and read the registry + `BuiltInSymbols.Primitives` automatically.

---

## 1. `SymbolKind` enum (`SchematicModel.cs`)

Add `NonlinearC` to the `SymbolKind` enum (alongside `Capacitor`). Append at the end to avoid disturbing
any ordinal-sensitive code (the enum is iterated by `LibraryCatalog.BuildAllItems`, which sorts by
category, so position doesn't affect palette order).

---

## 2. `ComponentTypeRegistry.cs`

Five edits, mirroring `Capacitor`:

**(a) `Registry` dict entry:**
```csharp
[SymbolKind.NonlinearC] = new("NLC", "C",
    Category: ComponentCategory.Lumped,
    SearchTerms: ["NLC", "NonlinearC", "nonlinear capacitor", "nonlinear", "varactor", "varicap", "CV", "C(V)"],
    IsCommon: false),   // flip to true if the owner wants it in the curated Common list
```
(DisplayName "NLC" is the on-schematic type label; prefix "C" so instances auto-name C1, C2, …. Rename
"NLC" if the owner prefers another short label.)

**(b) `EngineReference` switch** — must return the factory type name from brief #2:
```csharp
SymbolKind.NonlinearC => "NonlinearC",
```

**(c) `DefaultParameters` switch** — a freshly-placed NonlinearC shows `C0` (the constant term); higher
orders are added via the "+" template in (d). `C0` carries the Capacitance dimension so the common
near-constant case enters as "1 pF":
```csharp
case SymbolKind.NonlinearC:
    return [new("C0", "1", "pF", true, UnitDimension.Capacitance)];
```
Coefficient semantics: `C0` resolves through the Capacitance unit (1 pF → 1e-12 F). `C1, C2, …` (added by
the user, or written by the CV editor in brief #4) are **raw SI** values (units F/V, F/V², … have no clean
ComboBox dimension) → unit `None`. The brief-#2 factory reads `C0, C1, …` as resolved Real values, so this
flows through unchanged. (The CV editor in brief #4 writes `C0` back as a raw value with unit `F` and
`C1+` raw with unit `None` — both resolve correctly.)

**(d) `UserParamTemplate` switch** — let the user add `C1, C2, …` via the "+" button:
```csharp
SymbolKind.NonlinearC => new IndexedParamGroup(
    NameFormats:     ["C{0}"],
    DefaultUnits:    [""],
    ShowOnSchematic: [false],
    Dimensions:      [UnitDimension.None],
    FirstAddIndex:   1,
    SkipIndices:     null),
```

**(e) `TryParseCode`** — add a code so quick-place / text entry works (e.g. "NLC"):
```csharp
case "NLC": kind = SymbolKind.NonlinearC; return true;
```

---

## 3. `BuiltInSymbols.cs` — the glyph (linear C + three nonlinear slashes)

Add a cache field, a `Primitives` case, and `BuildNonlinearC()`. The body is the exact capacitor geometry
(`BuildCapacitor`) plus three short parallel diagonal strokes — the conventional "this element is
nonlinear" annotation drawn across the symbol.

```csharp
private static readonly Symbol _nonlinearC = BuildNonlinearC();
```
```csharp
// in Primitives(SymbolKind kind, int portCount) switch, next to Capacitor:
case SymbolKind.NonlinearC: return _nonlinearC;
```
```csharp
// ── NonlinearC — capacitor glyph + three diagonal "nonlinear" slashes ─────
// Identical plates/leads to the linear capacitor; the three parallel diagonal
// strokes are the standard nonlinear-element annotation. Pins: (0,-200)/(0,+200).
private static Symbol BuildNonlinearC() => Sym([
    L(   0, -200,   0,  -12),            // top lead
    L( -50,  -12,  50,  -12),            // flat top plate
    QC( -50,   22,   0,    2,  50,  22), // curved bottom plate
    L(   0,   12,   0,  200),            // bottom lead
    // three parallel diagonal slashes (lower-left → upper-right) across the plates:
    L( -42,  34,  -6,  -14),
    L( -24,  46,  12,   -2),
    L(  -6,  58,  30,   10),
], SymbolKind.NonlinearC);
```
The slash coordinates are a starting point — **tune visually** so the three strokes read cleanly against
the plates and stay within the glyph bbox (so the palette tile, ghost preview, and schematic render all
look right). Keep them `SymbolColorRole.SymbolLine` (same as the plates) unless the owner wants them dimmed.

---

## 4. `SymbolPortDefs.For` (locate the source — referenced by `BuiltInSymbols.Sym`)

`Sym(prims, SymbolKind.NonlinearC)` calls `SymbolPortDefs.For(SymbolKind.NonlinearC)` to get the pins.
Add a `NonlinearC` case returning the **same two vertical pins as the capacitor**: top `(0, -200)` and
bottom `(0, +200)` (names matching the capacitor's, e.g. "1"/"2" or "+"/"−" — mirror exactly what
`Capacitor` returns). If `For` has a `Capacitor` case, clone it; if 2-terminal verticals share a default,
make `NonlinearC` use that path. Two pins ⇒ two nets ⇒ `ElaboratedComponent.Nodes=[n0,n1]`, which the
brief-#2 model (`PortCount=1`) reads as its single differential port. Confirm `NonlinearC` is treated as a
2-terminal vertical everywhere `Capacitor` is special-cased (port defs, glyph-bbox, ghost) — grep
`SymbolKind.Capacitor` and give `NonlinearC` the same treatment where it concerns 2-terminal geometry.

---

## 5. Wire-through verification (should be automatic; confirm, don't rebuild)

- **Placement → params:** placing a NonlinearC seeds `C0=1pF` from `DefaultParameters`; users add `C1…`
  via "+". Auto-naming uses prefix "C".
- **Netlist:** `EngineReference(NonlinearC)="NonlinearC"` → `CnlWriter` emits `Reference="NonlinearC"` →
  `CnlReader` round-trips it. Confirm the writer/reader use `EngineReference` (they do for other kinds).
- **Elaboration → model:** the elaborator resolves the instance's numeric params (`C0, C1, …`) to `Value`s
  and calls `ComponentModelFactory.TryCreate("NonlinearC", params)` → `CreateNonlinearCModel` (brief #2).
  `IsPrimitive("NonlinearC")` is true (brief #2 added it to `_parameterizedTypes`). Confirm the elaborator
  passes `C0…Cn` through (no SDD-style special resolution is needed — they're ordinary scalar params, like
  R/L/C). If the elaborator has a per-type param whitelist, add `C0…Cn` (or confirm it passes all numeric
  params generically).
- **Renderer/palette/ghost:** all read `BuiltInSymbols.Primitives` + the registry, so the new kind appears
  with no further changes. Sanity-check the palette tile and a placed instance render the slashes.

---

## 6. Full-pipeline regression test (the brief-#1/#2 anchor, now placeable)

Engine/integration test (wherever S-param integration tests live):

**Constant-C NonlinearC ≡ linear C.** Build a 2-port testbench (Term Num=1 — Term Num=2) with a single
shunt (or series) NonlinearC whose only coefficient is `C0` (= 1 pF; no `C1…`). Run S-parameters over a
frequency grid with no DC source. Assert the resulting S-matrix equals, within tight tolerance at every
frequency, the S-matrix of the identical testbench with a **linear** `C` of 1 pF in the same position.
This exercises: placement defaults → netlist `Reference="NonlinearC"` → elaboration → DC pre-pass
(0 V bias, `sparam-zero-bias` note) → `StampLinearized` stamping jω·C(0)=jω·C0. It's the end-to-end proof
that the nonlinear path reduces exactly to the linear path at constant C.

(If building this through the full UI netlist path is heavy in a unit test, construct the
`ElaboratedNetlist` directly with a `NonlinearCModel([1e-12])` instance vs a `CapacitorModel` with C=1e-12
and compare `SParameterEngine.Run` outputs — same assertion, less plumbing. Prefer whichever matches the
existing S-param test style.)

---

## Out of scope: the CV-data editor (Method 2 entry, Apply/Close, `.csch` CV persistence) — brief #4.
