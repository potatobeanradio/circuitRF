# Skill — Adding a Library component to circuitRF

A procedure for adding a new built-in component (a new `SymbolKind`) to circuitRF so it appears in the
Library Palette, places and renders on a schematic, carries parameters, and reaches the engine. Use
this whenever the task is "add an X component / part / element to the palette."

The single registration hub is `ComponentTypeRegistry` (`src/Ui/Schematic/ComponentTypeRegistry.cs`);
the palette is generated from it — you never edit palette view code.

## Step 0 — Pick the archetype

Two kinds of built-in component exist, and they touch different files. Decide which you are building
before writing anything.

**Device component** — has ports, stamps/evaluates in the engine, and its `EngineReference` resolves to
a real component-model in the elaborator's factory (R, L, C, Vdc, SnP, ZPort, SDD, …). This is the
common case and is fully documented step-by-step in `docs/sonnet-briefs/palette-contributor-guide.md`. Follow that
guide for the **UI/palette** edits (enum, registry, ports, glyph, default params, code-parse). Two
corrections to that guide, verified against current code:
- Its variadic-port section describes ZPort/Sdd as "N+1 pins with a `ref` pin → `RefNetBinding`." The
  code actually generates **2N pins as differential ± pairs** (`SymbolPortDefs.GenerateSddPorts`); there
  is no `ref` pin for those types. Trust the code, not that paragraph.
- The design-note cross-links it lists (`design/library-palette.md`, etc.) may not all exist; grep
  before relying on them.

The palette guide assumes the **engine model already exists**. When the component is *not yet
implemented in the engine* (you must write the MNA stamp) or has ports that need a node convention
decision, the palette guide is **not enough** — see "Device-component engine implementation" below,
with TLIN (ideal transmission line) as the worked example.

**Annotation component** — has *no ports*, contributes *no instance* to the netlist, and instead carries
rows of `name = expression` text that are routed into a `TestBench` collection. The existing examples are
**VAR** (rows → `GlobalVariables`) and the planned **MEAS** (rows → `Measurements`); `Pin` is a related
no-electrical-model sentinel. The palette-contributor-guide does **not** cover this archetype — it is
documented below, with VAR as the reference implementation and MEAS as the worked example.

## The universal technique: grep an existing analog

Switch statements over `SymbolKind` are scattered (registry, ports, glyph, extraction) and some are
non-exhaustive (a missing case silently falls through to a generic default rather than failing the
build). So **do not trust the compiler to find every site.** Instead, pick the closest existing kind and
grep for it:

- Device component → grep `SymbolKind.Resistor` (or the nearest analog).
- Annotation component → grep `SymbolKind.Var`.

Every file the analog appears in is a file your new kind probably needs a case in. This enumerates the
touchpoints far more reliably than reasoning from scratch.

## Annotation-component recipe (VAR / MEAS archetype)

Touchpoints, all discoverable by grepping `SymbolKind.Var`:

1. **Enum** — add the value to `SymbolKind` in `src/Ui/Schematic/SchematicModel.cs`.
2. **Registry entry** — `ComponentTypeRegistry.Registry`: `DisplayName`, `InstancePrefix`,
   `Category` (usually `Other`), `SearchTerms`, `IsCommon`. The palette tile then appears automatically.
3. **Engine reference (sentinel)** — `ComponentTypeRegistry.EngineReference`: return a sentinel string
   (VAR returns `"VAR"`). It is never emitted as an `Instance`, so it does not need a factory model — but
   the value must exist so the `_ => DisplayName` fallback doesn't mislead. The *real* mechanism that
   keeps it out of the netlist is the extraction skip (step 7), not this string.
4. **Default parameters** — `ComponentTypeRegistry.DefaultParameters`: usually `[]` (a freshly placed
   annotation has no rows; the user adds them). VAR returns `[]`.
5. **User-param template** — `ComponentTypeRegistry.UserParamTemplate`: the `+`-button row template, e.g.
   VAR's `["Var{0}"]`. Provide an analogous placeholder-name format so the parameter editor can add rows.
6. **Code-parse** — `ComponentTypeRegistry.TryParseCode`: add a short code case (e.g. `"VAR"`).
7. **No ports** — `SymbolPortDefs.For` in `src/Ui/Schematic/EditableSchematic.cs`: return `[]`.
8. **Glyph** — `BuiltInSymbols.Primitives` + a glyph builder: a label-style glyph (mirror VAR's). No
   leads, since there are no ports.
9. **Extraction routing** — `src/Ui/Schematic/NetExtractor.cs`, in `ExtractModel`:
   - In the instance-emission loop, **skip** the kind: `if (comp.Symbol == SymbolKind.X) continue;`
     (alongside the existing `SymbolKind.Var` skip).
   - Add (or extend) a **collection pass** that gathers the kind's parameter rows into the right model
     collection — VAR builds `frameVars` (→ `GlobalVariables` at top, `cell.Variables` in a sub-cell).
     Route your rows to the correct `TestBench`/`Cell` collection, with the same duplicate-name guard.
   - **Scope caveat for analyses/measurements:** unlike variables, analyses *and measurements* attach to
     the **top testbench only** (data-model §2.1; `CnlReader` rejects `measure`/`analysis` inside a
     `define` block). So a MEAS-style component must collect rows **only from the top model**, not from
     sub-cells — warn (or ignore) if placed inside a cell, rather than emitting into `cell.Measurements`.
10. **Editor** — annotation rows are edited through a multi-line `name = expression` text editor, not the
    per-row parameter grid. VAR's editor is the template: `VarTextParser` (parse/serialize, framework-free
    and unit-tested) plus the VAR editor view-model/view and `VarParamCommands` for the undoable edits.
    Reuse or generalize these rather than writing a new editor; the text format is shared.
11. **Persistence** — none needed beyond the above: the rows are ordinary `EditableParameter`s, so
    `SchematicPersistence` round-trips them automatically. Confirm by saving and reloading.

## Worked example — the MEAS (measurement-equation) component

MEAS is the annotation archetype with rows routed to `TestBench.Measurements`. It is the authoring path
for the measurement system (`docs/design/measurements.md`); the engine evaluation and run wiring already
exist, so MEAS is the last missing piece to make measurements work end to end.

Concretely, following the recipe above:
- `SymbolKind.Meas`; registry entry `("MEAS", "MEAS", Category: Other, SearchTerms: ["MEAS","measure",
  "equation","meas"], IsCommon: true)`; `EngineReference` sentinel `"MEAS"`; `DefaultParameters` `[]`;
  `UserParamTemplate` `["Meas{0}"]`; `TryParseCode` `"MEAS"`; `SymbolPortDefs.For` `[]`; a label glyph.
- `NetExtractor`: skip `SymbolKind.Meas` from instance emission; add a top-level-only collection pass
  that turns each row into a `Measurement(name, expression, unit)` and appends to `tb.Measurements`
  (engine-normalized unit via `UnitNormalizer.ToEngineUnit`, duplicate-name guard, declaration order).
  Do **not** collect MEAS rows in sub-cell extraction.
- Editor: reuse the VAR multi-line editor; a measurement row is the same `name = expression` shape.

The difference from VAR is only the destination collection and the top-level-only scope; everything else
is the same archetype.

## Device-component engine implementation (writing the MNA stamp + factory wiring)

The palette-contributor-guide gets a device tile placing and netlisting, but it stops at
`EngineReference` — it assumes a model already answers to that reference string. When you are adding a
component **not yet implemented in the engine**, you also write the engine model and register it in the
factory. Worked example: **TLIN**, an ideal (lossless) transmission line — a 2-port device with three
parameters (`Z`, `E`, `F`).

### A. Decide the node convention BEFORE writing the stamp

A "2-port" can mean two different node layouts, and the choice drives both the symbol ports and the
stamp:
- **Ground-referenced ports (TLIN, and most lumped 2-ports).** Each port is one signal net referenced
  to the global ground ("0"). The symbol has **2 pins**; `c.Nodes[0]`/`c.Nodes[1]` are the signal nets;
  ground is the implicit common return and is **never a terminal**, so the netlister emits only the two
  signal nets (no reference net is written). This is the `default` 2-pin case in `SymbolPortDefs.For`
  — but make it explicit (give the kind its own `case`) when the symbol is horizontal
  (`(−200,0)`/`(+200,0)`) rather than the vertical default.
- **Differential 2N nets (ZPort/Sdd).** Each port is a ± pair with its own independent reference;
  `SymbolPortDefs.GenerateSddPorts` emits 2N pins. Use this **only** when ports genuinely float
  (per-port references). Do **not** use it for a ground-referenced line — it is wrong physics and
  doubles the pin count.

For TLIN: ground-referenced, 2 pins, horizontal. The reference-net-is-implicit behavior is automatic —
`NetExtractor.EmitInstance` emits one net per **pin**, and ground is not a pin, so nothing extra is
needed to "suppress" the reference net.

### B. Write the engine model (`src/Core/Devices/<Name>Model.cs`)

Subclass `ComponentModel`. The contract (read an existing analog — `InductorModel` for a Group-2
branch-current element, `ResistorModel` for a pure nodal-admittance element, `ZPortModel` for an N-port):
- `public override int PortCount => N;` and `public override ModelKind Kind => ModelKind.Linear;`
  (or `Nonlinear`).
- Constructor takes the **resolved parameter values** (doubles/Complex), not the raw param dict.
- `public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)` does the MNA stamp.

**Stamp primitives** (`IMnaContext`, see `src/Core/IMnaContext.cs`):
- Pure admittance between nodes → `AddAdmittance(a, b, y)` (adds the ± pattern to ground automatically).
- One raw nodal-matrix entry → `AddBlockAdmittance(rowNode, colNode, y)` (places `y` at `(row,col)`;
  ground node 0 rows/cols are **auto-dropped**, which is exactly how a ground-referenced port works).
- Branch-current unknown (voltage sources, inductors, Z-ports) → `AddBranch()` +
  `AddBranchCurrent`/`AddConstraint`/`AddBranchConstraint`. Expose `LastBranchIndex` if another model
  must reference this branch (e.g. Mutual, SDD control currents).

**Frequency.** `omega` is the angular frequency at the stamping point; `freqHz = omega/(2π)`. For a
frequency-dependent device, compute the response at `freqHz` inside `Stamp` (it is called once per
frequency point).

**Parameter units — the one real gotcha.** The elaborator applies the unit string to numeric params
before the model sees them, so a `Frequency`-dimensioned param arrives in **Hz**, a `Resistance` one in
**Ω**, etc. **Angles are the exception**: a `deg`/`UnitDimension.Angle` param arrives as **degrees, not
radians** (verified: `CreateP1ToneModel` passes `Phase` straight through as `phaseDeg`). Convert
deg→rad inside the model. TLIN reads `E` (degrees), `Z` (Ω), `F` (Hz) and converts `E` internally.

**Degeneracy/clamping philosophy.** circuitRF is a research tool: warn-and-continue rather than throw
on non-physical-but-recoverable inputs (mirror `MutualInductanceModel`'s over-coupling warning and
TLIN's `sin θ → 0` resonance clamp). Warn **once per instance** (a `bool _warned` field), not once per
frequency point. The engine's regularization rescues a singular matrix downstream.

**TreatWarningsAsErrors.** Core builds clean too — an unused private field (`CS0169`) fails the build.
If the constructor takes a param you don't use yet (e.g. an instance name reserved for diagnostics),
discard it (`_ = name;`) rather than storing it in an unread field.

### C. Register in the factory (`src/Core/Devices/ComponentModelFactory.cs`)

The factory maps the `EngineReference` string → a model instance. Two kinds of registration:
- **Parameterless** primitives (R/L/C/…) are entries in the `_registry` dict (`() => new XModel()`).
- **Parameterized** primitives (anything reading resolved params — TLIN, SnP, SDD, …) need: (1) add the
  reference string to the `_parameterizedTypes` set, (2) a dispatch line in the param-taking
  `TryCreate(typeName, parameters)` (`if (typeName.Equals("TLIN", …)) return CreateTLineModel(parameters);`),
  and (3) a `CreateXModel(parameters)` that pulls the resolved values out and constructs the model. Use
  the `GetReal(parameters, "Key", fallback)` helper (already in the factory) for numeric params; match
  the fallback to the registry default. `IsPrimitive` then returns true automatically (it checks both
  sets), so the elaborator treats it as a primitive rather than a missing sub-cell.

The `EngineReference` string, the `_parameterizedTypes` entry, and the `TryCreate` dispatch must all use
the **same** spelling (TLIN here). A mismatch shows up as an "unknown component" elaboration error at
run, not a build error.

### D. Persistence is automatic

`.csch` serializes `SymbolKind` by name (`JsonStringEnumConverter`) and parameters as ordinary rows, so
a new device round-trips with **no persistence edit** — confirm by save/reload, but don't go looking for
a persistence switch to update.

### TLIN touchpoint summary (every file, for grep-confirmation)

Engine: `src/Core/Devices/TLineModel.cs` (new, the stamp); `ComponentModelFactory.cs`
(`_parameterizedTypes` += "TLIN", `TryCreate` dispatch, `CreateTLineModel`). UI: `SchematicModel.cs`
(`SymbolKind.Tline`); `ComponentTypeRegistry.cs` (Registry entry, `EngineReference`, `DefaultParameters`,
`TryParseCode`); `EditableSchematic.cs` (`SymbolPortDefs.For` — horizontal 2-pin case);
`BuiltInSymbols.cs` (cache field, `Primitives` dispatch, `BuildTline`). The stamp math for the lossless
line: `θ = E·(π/180)·(f/F)`; `Y11=Y22=−j·cotθ/Z`, `Y12=Y21=+j/(Z·sinθ)`, stamped as the 2×2 nodal
block with ground as the common return.

## Verify

1. `dotnet build` — zero new warnings (`TreatWarningsAsErrors=true`; capture nullable properties into
   locals before passing to non-null parameters).
2. Palette: the tile appears under its category and via `SearchTerms`; click arms placement; the ghost
   shows the glyph (no pin squares for an annotation component); placing auto-names with the prefix.
3. Edit rows through the editor; save and reload the schematic — rows round-trip.
4. Run: for an annotation component, confirm the rows reach the right `TestBench` collection (for MEAS,
   that a `measurements.npy` appears under `results/<schematicKey>/` and its cubes plot). For a device
   component, confirm an `Instance` with the expected `Reference` and no elaboration error.
5. Add a headless extraction test mirroring the `NetExtractor` test suite for the new routing.

## See also

- `docs/sonnet-briefs/palette-contributor-guide.md` — the full device-component **UI/palette** step-by-step (fields,
  glyph helpers, worked attenuator example). Does **not** cover the engine model — see
  "Device-component engine implementation" above (TLIN) for the stamp + factory wiring.
- `src/Core/Devices/TLineModel.cs` + `ComponentModelFactory.CreateTLineModel` — reference
  implementation of a from-scratch device engine model (lossless 2-port, nodal-admittance stamp).
- `docs/design/measurements.md` — the measurement system MEAS feeds.
- `src/Ui/Schematic/NetExtractor.cs` — the VAR routing to copy for an annotation component.
