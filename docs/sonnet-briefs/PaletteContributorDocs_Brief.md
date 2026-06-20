# circuitRF — Palette contributor docs (author a doc; Claude Code / Sonnet)

Write **one new doc** — `docs/palette-contributor-guide.md` — a recipe a developer follows to add a new
built-in component to the Library Palette. This is DOCS-ONLY (no code changes). The single contribution point
is `ComponentTypeRegistry`; the guide must teach the full set of edits needed for a new component to appear in
the palette, place correctly, render its symbol, carry default parameters, and extract to the engine.

## Read first (ground every step in the real code/types — quote real names)
- `src/Ui/Schematic/ComponentTypeRegistry.cs` — `ComponentCategory` enum; `ComponentTypeInfo` record
  (`DisplayName`, `InstancePrefix`, `DefaultShowTypeLabel`, `DefaultShowInstanceName`, `Category`,
  `SearchTerms`, `IsCommon`, `ExtraCategories`); the `Registry` dictionary; `DefaultParam` record +
  `DefaultParameters(kind, portCount)`; `EngineReference(kind, portCount)`; `InstancePrefix`, `DisplayName`,
  `TryParseCode`, `UnitOptions`/`UnitDimension`.
- `src/Ui/Schematic/EditableSchematic.cs` — `SymbolKind` enum (where a new kind is added) and
  `SymbolPortDefs.For(kind, portCount)` (port positions/names; variadic `GeneratePorts`).
- `src/Ui/Schematic/BuiltInSymbols.cs` — `Primitives(kind)` (the glyph: the symbol drawing).
- `src/Ui/Schematic/PaletteDragPayload.cs` + `ViewModels/Dock/PaletteTool.cs` — how the palette lists items
  from the registry and carries Kind/PortCount through DnD (so the doc can state "registry entry ⇒ palette
  tile, automatically").
- `NetExtractor.cs` `EmitInstance` + `ComponentTypeRegistry.EngineReference` — how a placed component maps to
  an engine `Instance`/reference (so contributors know the engine-reference string must resolve).
- Existing design notes to cross-link (do NOT duplicate): `docs/design/library-palette.md`,
  `standard-library-symbols.md`, `symbol-editor.md`, `parameter-editor.md`, `data-model.md`. Match their
  tone/format.

## The doc to write — `docs/palette-contributor-guide.md`
Audience: a dev adding a new built-in primitive. Keep it a concrete, ordered recipe with a complete worked
example. Required sections:

1. **Overview / where things live.** One paragraph: the registry is the single contribution point; the
   palette is generated from it; the four things a component needs (symbol glyph, ports, default params,
   engine reference) and where each is defined. A small diagram-in-prose of the data flow:
   `SymbolKind → ComponentTypeRegistry entry → palette tile → placement (SymbolPortDefs + DefaultParameters) →
   render (BuiltInSymbols) → extraction (EngineReference)`.

2. **Step-by-step recipe.** Numbered, each step naming the real file + symbol:
   1. Add a `SymbolKind` enum value.
   2. Add the `Registry` entry (`ComponentTypeInfo`): `DisplayName`, `InstancePrefix`, `Category`,
      `SearchTerms`, `IsCommon`, optional `ExtraCategories`, label-default flags. Explain each field and how
      it affects the palette (category grouping, search, Common filter, on-tile tooltip) and the schematic
      (type label, instance-name prefix/auto-naming).
   3. Define ports in `SymbolPortDefs.For` (local coords; the 100-units-per-grid convention; fixed vs variadic
      `GeneratePorts`; pin naming, esp. the ZPort `ref` convention). State the connection-grid rule (pins land
      on P).
   4. Provide the symbol glyph via `BuiltInSymbols.Primitives(kind)` (or note the cell-ref/.csym path and
      cross-link `symbol-editor.md` + `standard-library-symbols.md`). Note glyph BB / port-lead expectations.
   5. Default parameters via `DefaultParameters(kind, portCount)` returning `DefaultParam`s: `Name`,
      `Expression`, `Unit`, `ShowOnSchematic`, `Dimension` (drives the closed Unit ComboBox via `UnitOptions`).
      Note the N-vs-N+1 port-count caveat for variadic types.
   6. Engine reference via `EngineReference(kind, portCount)` — the string the extractor emits; it must match
      an engine model the elaborator resolves. Cross-link the netlist/extraction path.
   7. Code-parse support: `TryParseCode` (so inline-edit type change "R"→"C" works) and the instance prefix.
   3. **Verify:** build; the new tile appears in the palette under its category and via search; place it
      (click-arm + DnD); labels/params render; `Run` extracts it without an unknown-reference error.

3. **Field reference table** for `ComponentTypeInfo` and `DefaultParam` (each field: meaning, example,
   effect). Use a table (this is reference material — a table is appropriate here even though prose is the
   default elsewhere).

4. **Worked example.** Pick a realistic not-yet-present primitive (e.g. a mutual-inductor / a 2-port
   attenuator / a simple transmission line if not already in the registry — verify against the current
   `Registry` and `SymbolKind` before choosing) and show every edit end-to-end (enum value, registry entry,
   ports, glyph note, default params, engine reference, TryParseCode), then the verify checklist. Keep the
   example's values illustrative but plausible.

5. **Gotchas / conventions.** Variadic port count (N signal vs N+1 pins; the `ref` pin), pins must land on the
   connection grid P, `DisplayName(kind, portCount)` not `SymbolKind.ToString()`, Ground-style label
   suppression, where the palette category/search/Common come from, and "registry is the v1 contribution
   point — compiled external model libraries are deferred (v2)". Cross-link rather than restate the connectivity
   and symbol-editor designs.

## Acceptance
- `docs/palette-contributor-guide.md` exists; a dev can follow it to add a component end-to-end.
- Every file/type/method named matches the current code (no invented APIs); the worked example uses a
  component NOT already in the registry (verified).
- Cross-links to `library-palette.md`, `standard-library-symbols.md`, `symbol-editor.md`,
  `parameter-editor.md`; does not duplicate them.
- Markdown matches the house style of `docs/design/*` (prose-first; a table only for the field reference).

## Guardrails
- **Docs only — no code changes.** If, while reading, you spot a real gap (a step with no supporting API),
  note it in a short "Open questions" section at the doc's end rather than inventing an API.
- Ground every step in real symbols read from the source; quote actual field/method names.
- Be accurate about the variadic/port-count and engine-reference details — these are the easy things to get
  wrong.
- Keep it a recipe, not an essay; the worked example carries the weight.

## Separately (NOT this doc — note for the session): GUI New-Cell/Symbol fix already applied
Opus already fixed the dead New Cell / New Symbol / New Schematic path: the four `ITreeActions` creation
methods in `WorkspaceViewModel` were calling `GetMainWindow()` (which returns `desktop.MainWindow` — always
null because `App.axaml.cs` only `Show()`s the window, never assigns `desktop.MainWindow`), so the name dialog
never opened and the action silently bailed. They now use `ResolveOwner(null)` (window-by-DataContext lookup,
same as the working File commands). **Cleanup for you to fold in (small):** set `desktop.MainWindow =
firstWindow` in `App.axaml.cs.OnFrameworkInitializationCompleted` (and the macOS `ShowFirstWindowIfNeeded`
path) so no future dialog hits the same null trap, then delete the now-unused `GetMainWindow()` helper.
Verify New Cell (tree + File menu), New Symbol / New Schematic (cell right-click) all open their dialog and a
new .csym opens in the Symbol Editor. `dotnet build`/`dotnet test` green; firewall green.

*Exit: a contributor doc that takes a dev from "I want a new palette component" to a placed, rendered,
extractable part; plus the small App.axaml.cs MainWindow cleanup so the New-Cell/Symbol fix is robust.*
