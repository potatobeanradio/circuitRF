# Brief: Schematic housecleaning — paste-Num, P1Tone S-param port, save-as title, toolbar glyphs, ohm units, SNP label

Stack/rules: .NET 10, Avalonia 12, `TreatWarningsAsErrors=true` (capture nullable-property reads into
locals). Core/Engine reference **no** Avalonia (firewall). Build must end **0W/0E**; add gate tests;
report total test count. After landing, add a newest-first changelog entry to the relevant `CLAUDE.md`
(`src/Ui/CLAUDE.md` for UI items, `src/Engine/CLAUDE.md` / `src/Core/Data/CLAUDE.md` for engine/units).

Six independent items — land incrementally. Root causes below are verified on disk except where noted
"confirm".

---

## Item 1 — Paste does not renumber Term / P1Tone `Num=` on conflict

**Symptom.** Pasting Term/P1Tone components into a schematic keeps their `Num=` parameter even when it
collides with an existing Term/P1Tone `Num`. Instance *names* are deduped on paste; the `Num` parameter
is not.

**Fix.** In the schematic paste path (locate it — likely `EditableSchematic` paste/clipboard-deserialize
or `SchematicViewModel.Paste`; the workspace Edit→Paste routes to the active `SchematicViewModel`), after
the pasted components are added and instance-name-deduped, resolve `Num` conflicts for the S-param port
family:
- Build the set of `Num` values already used by **top-level** `Term`, `Port`, and `P1Tone` components
  *not* in the pasted set (read each component's `Num` `EditableParameter`; ignore components inside
  sub-cells — only top-level instances carry S-param port numbers).
- For each pasted `Term`/`P1Tone` (and `Port`) whose `Num` is in that set, reassign to the lowest
  positive integer not yet used (updating the live set as you go so two pasted ports don't collide with
  each other). Mutate via the same parameter-edit path the editor uses (so it's undoable in one paste
  transaction).
- Mirror the existing instance-name dedup logic (`NameValidator` / `GenerateUniqueName`) for structure.

**Test.** Schematic with `Term Num=1`; paste a copied `Term Num=1` → pasted one becomes `Num=2`. Same
for P1Tone, and for a Term colliding with an existing P1Tone Num (shared numbering space).

---

## Item 2 — P1Tone used as a Term in an S-param sim: "port Num=1 missing" + spurious singular warning

**Finding.** `SParameterEngine` (`src/Engine/SParameterEngine.cs`) **already** treats P1Tone as an
S-param port: `IsSParamPort(m) => m is PortModel or TermModel or P1ToneModel`, and
`CollectPortsAndBranchLabels` adds P1Tone as a `PortEntry` (wave-path conductance + legacy-path branch via
`P1ToneModel.StampAsSParamPort`). So the engine is not the source of the "port Num=1 is missing; Terms are
numbered 2" message — that comes from a **separate S-param port-numbering validation that counts only
`Term` components**, so with `P1Tone:P1 Num=1` + `Port:Term2 Num=2` it sees only Term2 and reports port 1
missing.

**Fix.**
1. Locate the validation (search the message text `"is missing"` / `"are numbered"`, likely in
   `NetExtractor.cs` or a port-check it calls). Extend its port set to include `P1Tone` and `Port` (same
   family as `IsSParamPort` in the engine), so a P1Tone with `Num=1` satisfies port 1 and the numbering is
   validated across Term + Port + P1Tone together.
2. Reproduce the offending netlist (the one in the request: `Port:Term2 n1 0 Num=2 Z=50 Ohm` +
   `P1Tone:P1 n1 0 Num=1 … Z=50 Ohm`, `analysis SP1 type=sparam …`) and confirm it runs **without** the
   "matrix singular — regularization" warning. Per the engine code both ports stamp `1/Z0` conductance to
   `n1` (well-conditioned), so the singular warning is almost certainly a downstream symptom of the
   validation/extraction not presenting P1Tone as a port; fixing (1) should clear both messages. If the
   singular warning persists after (1), debug whether the P1Tone port conductance is actually stamped in
   `RunWavePath`/`StampPortConductances` for this case (it should be — `CollectPorts` includes it).

**Test.** Engine/extraction test: the two-component netlist above → S-param run produces a 2×2 matrix,
no "port missing" validation message, no singular warning. (Use the headless `SchematicRunService` or the
engine directly with an elaborated netlist containing a Term and a P1Tone at top level.)

---

## Item 3 — "Save Schematic As" doesn't update the Content-pane tab title

**Root cause.** `SchematicDocument.Materialize(filePath, cellName)` updates `_baseTitle`/`Id`/`Title`/
`FilePath`, but its contract is **one-way scratch→materialized** ("Must only be called once"). A
"Save Schematic As" on an already-materialized document (saving to a new name/path) does not go through a
title-updating path, so the tab keeps the old name.

**Fix.**
- Add a rename-safe method on `SchematicDocument`, e.g. `internal void OnSavedAs(string filePath, string
  cellName)` that sets `FilePath = filePath`, `_baseTitle = cellName`, `Id = cellName`, and calls
  `UpdateTitle()` (works repeatedly, unlike `Materialize`). (Or relax `Materialize` to be re-callable and
  use it.)
- Call it from the "Save Schematic As" command after the `.csch` is written — locate
  `SaveLooseSchematicCommand` (the File→"Save Schematic As…" binding) in `WorkspaceViewModel`; the
  `cellName` is the new file's base name (`Path.GetFileNameWithoutExtension`).
- Update workspace path bookkeeping so the renamed doc is keyed by the new path: session registry
  (`SchematicSessionRegistry` / `_registry`), `_openDocsByPath`, and the project tree — mirror the
  scratch-materialize bookkeeping already done elsewhere (e.g. how `Materialize` callers update the
  registry/tree). Do not leave a stale entry under the old path.

**Test.** Materialized schematic doc → Save As to `Foo.csch` → `SchematicDocument.Title` == `Foo`
(bullet-aware) and `FilePath` updated. (Pure `SchematicDocument.OnSavedAs` is unit-testable without the
Avalonia host; the workspace bookkeeping uses the "simulate" pattern.)

---

## Item 4 — Schematic toolbar glyphs (Wire / Ground / Term) + new Pin button, centered

All edits in `src/Ui/Views/Content/SchematicView.axaml` (+ `.axaml.cs` for the new handler). The toolbar
buttons are click-handler `<Button x:Name="…" Click="…" Padding="6,3">` with a 16×16 glyph. `xmlns:ctrl=
"using:CircuitRF.Ui.Controls"` is already declared.

**Wire** (`WireToolBtn`, currently `<mi:MaterialIcon Kind="VectorPolyline"/>`) → reuse the Symbol
Editor's exact Line glyph (from `SymbolEditorView.axaml`, the `ConverterParameter=Line` button):
```xml
<Path Width="16" Height="16" VerticalAlignment="Center"
      Stroke="{DynamicResource SystemControlForegroundBaseHighBrush}"
      StrokeThickness="1.5" Fill="Transparent" StrokeLineCap="Round"
      Data="M 1,6 L 15,10"/>
```

**Ground** (`PlaceGroundBtn`, currently `ArrowCollapseDown`) → render the actual GND library symbol via
`PaletteGlyphControl` (the Skia control that draws `BuiltInSymbols.Primitives(kind)` auto-fit + centered —
same one the Library Palette uses):
```xml
<ctrl:PaletteGlyphControl Kind="Ground" Width="16" Height="16"
                          VerticalAlignment="Center" HorizontalAlignment="Center"/>
```
**Term** (`PlacePortBtn`, currently `SquareCircle`) → `<ctrl:PaletteGlyphControl Kind="Term" Width="16"
Height="16" VerticalAlignment="Center" HorizontalAlignment="Center"/>`.

**New Pin button** — add after the Term button (before the rotate separator):
```xml
<Button x:Name="PlacePinBtn" Click="OnPlacePin" ToolTip.Tip="Place Pin" Padding="6,3">
    <ctrl:PaletteGlyphControl Kind="Pin" Width="16" Height="16"
                              VerticalAlignment="Center" HorizontalAlignment="Center"/>
</Button>
```
Add `OnPlacePin` in `SchematicView.axaml.cs` mirroring `OnPlaceTerm`/`OnPlaceGround` but arming
`SymbolKind.Pin` placement (read those two handlers and copy the placement-arm call with the Pin kind/port
count). Confirm `SymbolKind.Pin` is placeable in a schematic the same way Ground/Term are.

**Centering.** The Symbol Editor centers perfectly via `Button Padding="6,3"` + fixed `Width="16"
Height="16" VerticalAlignment="Center"` content — apply the same to all four glyphs above. Verify
`PaletteGlyphControl` renders the symbol centered and scaled-to-fit at 16 px (it computes a 12%-padding
fit, so it should; confirm visually). `PaletteGlyphControl.Kind` is a `SymbolKind` styled property;
`Ground`/`Term`/`Pin` all have `BuiltInSymbols` geometry.

**Test.** Smoke: the four buttons construct (PaletteGlyphControl needs the Skia/Avalonia host, so a render
test isn't framework-free) — at minimum a code-behind test that `OnPlacePin` arms `SymbolKind.Pin`
placement (via the placement service / VM), analogous to any existing place-Term test.

---

## Item 5 — Accept `Ω` / `ohm` / `ohms` / `Ohm` / `Ohms` for resistance (engine + inline editor)

**Root cause.** `src/Core/Expressions/Units.cs` `_scales` (an `Ordinal`, case-sensitive map) recognizes
`Ohm`, `Ohms`, `kOhm`, `MOhm`, `GOhm`, and `UnitNormalizer` maps `Ω→Ohm` — but **not lowercase `ohm` /
`ohms`**. So `Z=50 ohms` → `ohms` is not a recognized unit → the netlist tokenizer consumes it as a
separate net token → floating node named `ohms` → singular MNA (matches the reported error
"voltage node: ohms"). `Ω` and `Ohm`/`Ohms` already work.

**Fix.**
1. `src/Core/Expressions/Units.cs` — add to `_scales`:
   ```csharp
   { "ohm",  1.0 },
   { "ohms", 1.0 },
   ```
   (Optional, for parity: `kohm`/`Mohm`/`Gohm` lowercase-prefixed — not required by the request.) This
   flows automatically to `IsKnown`, `IsRecognizedUnit`, and `Scale`, so the netlist tokenizer's
   unit-position gate stops swallowing `ohms` as a net, and the value scales correctly (×1.0).
2. Inline editor validation — the schematic inline edit must validate `50ohms` → `50 ohms` exactly like
   `50Ω` → `50 Ω`. Locate `ParseExpressionUnit(raw, param)` (internal/testable, in the schematic
   inline-edit path — see the "Inline editor fixes" changelog entry; likely `SchematicView.axaml.cs` or a
   helper). If it gates on `Core` `Units.IsRecognizedUnit`, step 1 already fixes it; if it has its own
   UI-side unit list, add `ohm`/`ohms` there too. Ensure the no-space split (`"50ohms"` → `"50"` + `"ohms"`)
   recognizes `ohm`/`ohms` as a unit run.
3. **Respect the user's spelling** — do not force-normalize the displayed unit. The param's `Unit` stores
   what the user typed (`ohm`/`ohms`/`Ω`); the renderer shows it verbatim. The engine boundary normalizes
   via `UnitNormalizer.ToEngineUnit` (`Ω→Ohm`; `ohm`/`ohms` pass through unchanged) + `Units.Scale`
   (now → 1.0). No change needed in `UnitNormalizer` (optionally canonicalize `ohm`/`ohms`→`Ohm` there,
   but not necessary once `Units` recognizes them).

**Test.** `Units.Scale("ohm") == 1.0`, `Units.Scale("ohms") == 1.0`. A netlist with `R=50 ohm` and
`Z=50 ohms` elaborates with the right values and an S-param run shows **no** floating-node/singular
warning. Inline-editor unit test: `ParseExpressionUnit("50ohms", rParam)` splits to value `50` + unit
`ohms` (valid), like `"50Ω"`.

---

## Item 6 — SNP component label position & hitbox wrong for n ≥ 4 (regression)

**Root cause.** `SchematicComponent.LabelBaseYFor(symbol, portCount)`
(`src/Ui/Schematic/SchematicModel.cs`) has an SNP branch that computes the body half-height with a
**hardcoded** config:
```csharp
if (symbol is SymbolKind.Snp)
{
    var (_, halfH) = SymbolPortDefs.SnpBodyRect(portCount, SnpPinConfig.Standard, SnpPitch.Loose);
    return Math.Max(LabelBaseY, halfH + LabelWorldStep);
}
```
But the actual rendered SNP symbol comes from the component's own `SchematicComponent.SnpSymbol`, built
from its real `RefNode`/`PinConfig`/`Pitch`. When those differ from `Standard`/`Loose` (or a recent edit
changed the SNP body geometry/defaults), the assumed body half-height diverges from the real symbol for
n ≥ 4, so the label base-Y — and the hitbox, since the hit-test and renderer both go through
`LabelRowGeometry` → `LabelBaseYFor` — no longer sits just below the actual symbol.

**Fix.** Derive the SNP (and ideally all variadic-body) label base-Y from the component's **actual** glyph
geometry rather than a re-derived `SnpBodyRect` with assumed config:
- The `SchematicComponent` already carries the real glyph extent (`GlyphBbMinY/MaxY`, computed from
  `SnpSymbol`). The label's first-row base-Y (below center) should track the real glyph bottom:
  `Math.Max(LabelBaseY, (GlyphBbMaxY − Y) + LabelWorldStep)`.
- `LabelBaseYFor`/`LabelRowGeometry` are `static (symbol, portCount, …)`; thread the component's **actual**
  body half-height (or its real `SnpPinConfig`/`SnpPitch`) in as a parameter, and pass it identically from
  **all three** callsites so they stay in lock-step: the renderer (`DrawLabels`), the hit-test
  (`TestComponentLabels` in `SchematicHitTest.cs`), and the FullBb builder (`SchematicModelBuilder`). Each
  has the `SchematicComponent`, so each can pass `component.GlyphBbMaxY`/the real config.
- Keep the SDD/ZPort branch behavior (it works); the SNP branch is the one to fix. If you generalize to
  "use the actual glyph bottom for any variadic symbol," verify SDD/ZPort still match their current
  positions.

Confirm on disk: `SymbolPortDefs.SnpBodyRect` signature + the `SnpPinConfig`/`SnpPitch` enums + where the
component's real config is stored (likely on the SnP `EditableComponent` / `SnpSymbol` build inputs), and
that `GlyphBbMaxY` for an SNP reflects the real `SnpSymbol` extent (per `SchematicComponent` it does).

**Test.** Build an SNP component with n ≥ 4 ports; assert the label first-row base-Y (and the
`LabelRowGeometry` hit band) sits below the actual symbol body bottom (within `LabelWorldStep`), matching
the rendered `SnpSymbol` extent — and that it tracks port count (n=2 vs n=8 differ). A regression guard
comparing label base-Y to the real glyph bottom for several port counts.

---

## Notes
- Items are independent; land + test each, then one combined changelog entry (or per-item).
- For Items 1–4 and 6 (UI), keep edits within `src/Ui`; Items 2 and 5 touch `src/Engine`/`src/Core`
  (firewall: no Avalonia there).
- Capture nullable-property reads into locals (TreatWarningsAsErrors).
- Where a message/handler location is "locate" (Items 1, 2, 3, 5), grep the quoted string / symbol name to
  find it precisely before editing.
