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
common case and is fully documented step-by-step in `docs/palette-contributor-guide.md`. Follow that
guide; this skill does not duplicate it. Two corrections to that guide, verified against current code:
- Its variadic-port section describes ZPort/Sdd as "N+1 pins with a `ref` pin → `RefNetBinding`." The
  code actually generates **2N pins as differential ± pairs** (`SymbolPortDefs.GenerateSddPorts`); there
  is no `ref` pin for those types. Trust the code, not that paragraph.
- The design-note cross-links it lists (`design/library-palette.md`, etc.) may not all exist; grep
  before relying on them.

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

- `docs/palette-contributor-guide.md` — the full device-component step-by-step (fields, glyph helpers,
  worked attenuator example).
- `docs/design/measurements.md` — the measurement system MEAS feeds.
- `src/Ui/Schematic/NetExtractor.cs` — the VAR routing to copy for an annotation component.
