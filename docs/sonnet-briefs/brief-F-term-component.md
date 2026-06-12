# Brief F — Term component (S-parameter excitation port)

**Scope:** rename the bare `Port` component to **`Term`** and finish it so it can actually drive an
S-parameter port: `Num` + `Z` parameters and a real `−` terminal (for differential). Engine and
netlist substrate already exist — this is mostly the schematic/registry/symbol layer plus a
round-trip check. **Read `docs/design/ports-pins-and-terms.md` first — it is the spec.**

**Firewall:** `SymbolKind`, `ComponentTypeRegistry`, `BuiltInSymbols`, `SymbolPortDefs` are in
`src/Ui/Schematic` but are **framework-free** (no Avalonia/Skia) — keep them so. `PortModel`/
`TermModel`, `CnlWriter/Reader`, `Elaborator` are in `src/Core` — no Avalonia.

---

## Read first (real names)

- `docs/design/ports-pins-and-terms.md` — the locked design. Term = `+`/`−`, `Num` + `Z`,
  netlists as `Port:` object, testbench-only.
- `src/Ui/Schematic/ComponentTypeRegistry.cs` — `SymbolKind.Port` entry (`DisplayName "Port"`,
  `InstancePrefix "P"`, `Category Terminals`, `SearchTerms`), `EngineReference(SymbolKind.Port) =>
  "Port"`, `DefaultParameters` (currently returns `[]` for Port — the gap), `TryParseCode`
  (`"PORT"`/`"P"` → `SymbolKind.Port`). `UnitDimension` has no impedance entry that's distinct from
  `Resistance` — use `Resistance` (Ω) for `Z`, or add a dimension if you prefer (state it).
- `src/Ui/Schematic/BuiltInSymbols.cs` — `BuildPort()` ("Port / Term — resistor-in-box, single
  signal pin"). Currently **one** pin at `(0,-200)`; the box bottom is an *implicit* reference.
  `Sym(prims, kind, portCount)` builds pins from `SymbolPortDefs.For(kind)`.
- **`SymbolPortDefs`** — grep for `SymbolPortDefs` (defines `(LocalX, LocalY, Name)` pins per
  `SymbolKind`). `SymbolKind.Port` currently yields ONE pin. You will make `Term` yield **two**:
  `+` (signal) and `−` (reference).
- **`SymbolKind`** — grep `enum SymbolKind` (likely in `SymbolGeometry.cs` or a sibling). Rename the
  `Port` member to `Term`.
- `src/Core/Devices/PortModel.cs` — defines BOTH `PortModel` and `TermModel` (identical: 0 V source
  between `Nodes[0]`(+) and `Nodes[1]`(−); S-param engine drives it). Leave the models as-is.
- `src/Core/Devices/ComponentModelFactory.cs` — grep: maps an engine `Reference` string →
  model. Confirm `"Port"` (and/or `"Term"`) → a Port/Term model, and `IsPrimitive("Port")` is true.
- `src/Engine/SParameterEngine.cs` — `CollectPortsAndBranchLabels` accepts `PortModel` OR
  `TermModel`; `GetPortNum` **throws if `Num` is missing**; `GetZ0` defaults `Z` to 50. (Don't edit;
  this is why `Num`/`Z` defaults are required.)
- `src/Core/Netlist/CnlWriter.cs` — `FormatStandardInstance` emits `Reference:Name  nets…  param=val`
  → a Term with `Reference="Port"`, two nets, `Num`/`Z` overrides produces exactly the `hero1.cnl`
  line. `src/Core/Netlist/CnlReader.cs` — the inverse; confirm it parses `Port:… Num= Z=`.
- `src/Ui/Schematic/NetExtractor.cs` — turns a schematic into `Instance`s (sets `Reference` =
  `EngineReference`, `InstanceName`, `NetBindings` in pin order). Confirm a 2-pin Term emits two net
  bindings (`+` then `−`).
- `testdata/Hero1/hero1.cnl` — `Port:Term1  p1 0  Num=1 Z=50 Ohm` (the exact target round-trip).

---

## Spine (do-not-violate)

1. **The engine `Reference` stays `"Port"`** so `.cnl` keeps emitting `Port:` objects (compat with
   `hero1.cnl`, `CnlReader`, `ComponentModelFactory`, `SParameterEngine`). Only the *schematic*
   component's identity becomes `Term`.
2. A Term has **two** terminals: `+` = `Nodes[0]` (signal), `−` = `Nodes[1]` (reference). Pin order
   into `NetBindings` must be `+` then `−` (the engine relies on `Nodes[0]`/`Nodes[1]`).
3. `Num` is **required** and unique within a schematic; `Z` defaults to `50 Ω`. Auto-assign the next
   free `Num` on placement.
4. Don't touch `PortModel`/`TermModel` stamping here (that's Brief H). This brief only makes the
   schematic Term well-formed and round-tripping.

---

## Layer 1 — Rename `SymbolKind.Port` → `SymbolKind.Term`

Global, mechanical, alpha (no back-compat). Touch every reference:
- `SymbolKind` enum: `Port` → `Term`.
- `ComponentTypeRegistry`: `Registry[SymbolKind.Term] = new("Term", "Term", Category: Terminals,
  SearchTerms: ["Term", "T", "port", "sparam", "termination"], IsCommon: true)`. `EngineReference`
  case → `SymbolKind.Term => "Port"` (UNCHANGED string). `TryParseCode`: `"TERM"`/`"T"` →
  `SymbolKind.Term`; **drop `"PORT"`/`"P"`** (retired) — or keep them mapping to `Term` only if a
  cheap alias helps typed entry (state choice; default: drop, since `Port` is retired).
- `BuiltInSymbols`: `_port` field + `BuildPort()` → `_term`/`BuildTerm()`; switch arm
  `SymbolKind.Term => _term`.
- `SymbolPortDefs.For`: the `SymbolKind.Port` arm → `SymbolKind.Term`.
- `NetExtractor`, `SchematicRenderer`/`SymbolEditorRenderer`, hit-test, palette, any `switch` on
  `SymbolKind` — grep `SymbolKind.Port` and update all.
- **Persistence caution:** if `.csch` serializes `SymbolKind` as the string `"Port"`, the rename
  changes the token to `"Term"`. Per alpha policy that's fine (reject-on-mismatch, no migrate), but
  **any existing `.csch`/testdata using `Port` must be regenerated** — list affected files. The
  `.cnl` is unaffected (it uses the engine `Reference "Port"`, not `SymbolKind`).

**Gate 1:** Project builds; placing a "Term" from the palette works; existing schematics that used
the old Port either load (if not persisted by enum name) or are regenerated (listed).

---

## Layer 2 — Term parameters: `Num` + `Z`

In `ComponentTypeRegistry.DefaultParameters`, add a `Term` case:
```
case SymbolKind.Term:
    return [ new("Num", "<next>", "",  true,  UnitDimension.None),        // integer port index
             new("Z",   "50",     "Ω", true,  UnitDimension.Resistance) ]; // reference impedance
```
- `Num` must be an **integer** and **unique within the schematic**. Default expression = the next
  free integer among existing Terms (compute at placement time — see Layer 4 — since `DefaultParameters`
  is static, you may emit a placeholder like `"1"` here and have the placement path overwrite `Num`
  with the next-free value; state how you wired it).
- `Z` is the reference (renorm) impedance; real or complex; default `50 Ω`. The engine reads it via
  `GetZ0` (complex-aware). Showing it on-schematic is desirable (RF users tune per-port Z).

**Gate 2:** A freshly placed Term shows `Num` and `Z=50 Ω`. The S-param engine no longer throws
"Port/Term is missing the Num parameter" for a schematic containing Terms.

---

## Layer 3 — Two terminals (`+`/`−`) in the symbol

Today `BuildPort`/`SymbolPortDefs` expose ONE pin (signal), reference implicit. Give `Term` a real
`−` pin so it can be wired differentially (and explicitly to ground when single-ended):
- `SymbolPortDefs.For(SymbolKind.Term)` returns TWO pins, in this order: index 0 = `+` (signal,
  e.g. `(0,-200)`, name `"+"`), index 1 = `−` (reference, e.g. `(0,+200)`, name `"−"`). The order
  IS the `NetBindings`/`Nodes` order the engine uses.
- `BuildTerm()` art: keep the resistor-in-box glyph but make the bottom a real lead to the `−` pin
  at `(0,+200)` (instead of an implicit reference). Single-ended use = user wires `−` to a Ground.
- Confirm `NetExtractor` now emits two net bindings for a Term (`+` net, then `−` net), and that a
  Term with `−` tied to a Ground net resolves `Nodes[1]` to node 0.

**Gate 3:** A Term has two connectable pins. Wiring `+`→signal and `−`→GND gives `Nodes=[signal,0]`.
Wiring `+`/`−` across two nets gives a differential pair. The S-param result for a simple 50 Ω
through-line testbench matches the previous single-pin behavior when `−` is grounded.

---

## Layer 4 — Auto-`Num` + validation on placement

- On placing a Term, assign `Num` = `1 + max(existing Term Nums in this schematic)` (or lowest free).
- Validate uniqueness within the schematic: two Terms with the same `Num` is an error → surface a
  warning (reuse the schematic's existing validation/》warning channel; grep how duplicate names are
  flagged today) and/or block the duplicate. 1-based numbering (S-param convention).

**Gate 4:** Placing three Terms yields `Num` 1,2,3. Editing one to a duplicate `Num` is flagged.

---

## Layer 5 — Round-trip check (no new code expected)

- Build a 2-port testbench (through-line + two Terms, `Num`=1/2, `Z`=50). Write `.cnl` via
  `CnlWriter`; confirm it emits `Port:<name>  <+net> <−net>  Num=1 Z=50 …` (matching `hero1.cnl`),
  and `CnlReader` reads it back to an equivalent TestBench. Run `SParameterEngine` → sane S-params.
- If `CnlWriter`/`Reader` need any tweak for the 2-net Term, make the minimal change and note it;
  the standard-instance path should already cover it.

**Gate 5:** `.cnl` round-trips a Term; S-param sweep over the testbench runs and returns a 2×2 S.

---

## Acceptance
- The schematic component is **Term** (`Num` + `Z`, `+`/`−`); engine `Reference` is still `"Port"`. ✅
- A Term drives an S-param port with arbitrary `Z`; differential wiring works. ✅
- `.cnl` emits/reads `Port:… Num= Z=` (matches `hero1.cnl`). ✅
- No bare "Port" component remains; placement offers Term. ✅

## Guardrails
- Engine `Reference "Port"` is sacred — do not rename it. Don't edit `PortModel`/`TermModel`.
- Keep `SymbolKind`/registry/`BuiltInSymbols`/`SymbolPortDefs` framework-free.
- Pin order `+` then `−` — the engine depends on it.
- Minimal diff; list every file touched and every regenerated testdata file.

## Scope fence (NOT here)
- No Pin/interface component, no cell mapping (Brief G).
- No scoping rule / engine stamping changes / linter (Brief H).

## Exit / report
State: where `SymbolKind` is defined; the full rename file list; how `Num` auto-assign is wired
(since `DefaultParameters` is static); the `−`-pin coordinates/names; any `CnlWriter/Reader` tweak;
the regenerated testdata; and the 2-port round-trip result. Confirm all 5 gates run mentally.
