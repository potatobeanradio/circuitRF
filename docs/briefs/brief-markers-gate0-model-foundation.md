# Brief — Markers Gate 0: Marker model foundation (additive, no behavior change)

**Status:** Ready to implement
**Scope:** Add new fields to the marker model and thread them through copy + persistence. **No rendering or interaction change.** Existing markers must behave and round-trip exactly as today.
**Design ref:** `/docs/design/trace-markers-design.md` §12 "Gate 0". Read that section first.

This is the first of seven gates that bring markers to contour plots (and tidy the other marker types). Gate 0 only lays the model groundwork later gates read; it deliberately changes no visible behavior.

---

## Context (already verified — do not re-investigate)

- The marker model is `src/Ui/DataDisplay/Models/Marker.cs` (class `Marker`).
- Marker persistence is `MarkerConfig` in `src/Ui/DataDisplay/Models/DataDisplayConfig.cs`.
- The **`Marker ↔ MarkerConfig` mapping (both directions)** lives in `src/Ui/DataDisplay/ViewModels/DataDisplayViewModel.cs`. Grep that file for `MarkerConfig` and `PositionStaticX` to find the two map sites (config→marker on load, marker→config on save). There may be a helper method for each direction; edit whichever methods build a `Marker` from a `MarkerConfig` and a `MarkerConfig` from a `Marker`.
- `Marker` already has `PositionStatic` (`System.Numerics.Vector2`), already serialized as `MarkerConfig.PositionStaticX/Y`, already in the `Marker` copy constructor. **Reuse it** for contour free-roaming position in a later gate — do NOT add a new position field now.
- Alpha no-back-compat rule: adding **defaulted** fields to `MarkerConfig` is safe; no migration shim, no `format_version` bump for this.

## UI/Core build gate

The Ui and Core csproj have `TreatWarningsAsErrors=true`. Watch for:
- An unused `private` field/method warns → don't add unused privates.
- A nullable-property-into-non-null warning → capture into a local first.
- An unused `using` warns.
None of these should arise here if you only add public auto-properties and map them.

---

## Task 1 — Add fields to `Marker` (`Marker.cs`)

Add a marker-kind enum and the new state, all as public auto-properties with the given defaults. Put the enum near the top of the file beside `MarkerStyle`.

```csharp
// Beside MarkerStyle:
public enum MarkerKind { Polyline, Spectrum, StabilityCircle, Table, Contour }
```

Add these properties to the `Marker` class (group them with the existing "Core state" properties):

```csharp
public MarkerKind MarkerKind     { get; set; } = MarkerKind.Polyline;
public bool       ShowInfoBox    { get; set; } = true;
public bool       ContourSnapped { get; set; }            // false ⇒ Mode 1 (free/interpolated)
public bool       VswrEnabled    { get; set; }
public double     VswrValue      { get; set; } = 2.0;
```

Extend the **copy constructor** `Marker(Marker src)` to copy all five new fields:

```csharp
MarkerKind     = src.MarkerKind;
ShowInfoBox    = src.ShowInfoBox;
ContourSnapped = src.ContourSnapped;
VswrEnabled    = src.VswrEnabled;
VswrValue      = src.VswrValue;
```

Do **not** change the primary constructor's signature. (Later gates will set `MarkerKind` from the host trace at creation; Gate 0 leaves it defaulting to `Polyline`, which is correct for the overwhelmingly common case and harmless elsewhere because nothing reads it yet.)

## Task 2 — Add fields to `MarkerConfig` (`DataDisplayConfig.cs`)

Add matching defaulted properties to `MarkerConfig`. Use the `JsonStringEnumConverter` attribute on the enum, mirroring the existing `MatrixFormat`/`Style` properties in that class:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public MarkerKind MarkerKind { get; set; } = MarkerKind.Polyline;

public bool   ShowInfoBox    { get; set; } = true;
public bool   ContourSnapped { get; set; }
public bool   VswrEnabled    { get; set; }
public double VswrValue      { get; set; } = 2.0;
```

## Task 3 — Thread through the map in `DataDisplayViewModel.cs`

Find the two map sites (grep `MarkerConfig`, `PositionStaticX`).

- **marker → config** (save): set the five new `MarkerConfig` fields from the `Marker`.
- **config → marker** (load): set the five new `Marker` fields from the `MarkerConfig`.

Mirror exactly how the existing fields (`Style`, `MaximumFractionDigits`, `PositionStaticX/Y`, etc.) are copied at each site. Add nothing else.

---

## Out of scope (do NOT do in Gate 0)

- No glyph changes (`MarkerRenderer` untouched).
- No hit-test / add / drag changes.
- No InfoBox content changes (`BuildMarkerBoxLines` untouched).
- No editor UI, no context-menu items.
- No setting `MarkerKind` from the host trace yet.
- No VSWR rendering or interaction.

## Acceptance / verification

1. **Build green** for the whole solution (UI + Core, warnings-as-errors).
2. Open an existing saved layout that contains markers → markers load and render **identically** to before (still triangles, same positions, same InfoBoxes).
3. Save it back, reopen → markers round-trip unchanged. New JSON keys (`MarkerKind`, `ShowInfoBox`, `ContourSnapped`, `VswrEnabled`, `VswrValue`) appear in the saved file with their default values.
4. Copy/paste a plot containing a marker (clipboard path) → marker pastes unchanged.
5. No visual or behavioral difference anywhere.

## Report back

- Confirm the two map-site method names you edited in `DataDisplayViewModel.cs`.
- Confirm build is green and the round-trip test passed.
- Paste the marker JSON block from a saved file so we can eyeball the new defaulted keys.
