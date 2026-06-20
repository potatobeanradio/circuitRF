# Brief: NonlinearC UX revisions (amends briefs #3 + #4, both landed)

Six UX changes from the owner against the landed NonlinearC symbol, registry, parameter-editor button, and CV
editor dialog. Each is self-contained; locate the current code (briefs #3/#4 built it) and apply. Build
**0W/0E**; update/extend the existing tests where noted; newest-first changelog.

---

## 1. Fix the symbol glyph's three nonlinear lines (`BuiltInSymbols.cs` → `BuildNonlinearC`)

The capacitor part is correct — **keep it**. Replace the three diagonal slashes with the geometry below.
Coordinate reminder: in symbol space **−y is up**, the top plate is the horizontal line at **y = −12**
spanning **x ∈ [−50, +50]** (left side x = −50, right side x = +50).

- **Line 1** — vertical, at the **left** plate edge (x = −50). Starts ~20 units above the top plate
  (y = −12 − 20 = −32) and draws **upward** 35 units (to y = −67): `L(-50, -32, -50, -67)`.
- **Line 2** — the point-symmetric partner on the **right** edge (x = +50), drawn **downward** 35 units,
  starting 20 below the plate (y = +8) to y = +43: `L(50, 8, 50, 43)`.
- **Line 3** — joins the **closest endpoints** of Lines 1 and 2 — i.e. Line 1's lower end (−50, −32) to
  Line 2's upper end (50, 8) — a diagonal: `L(-50, -32, 50, 8)`.

Full corrected method:
```csharp
private static Symbol BuildNonlinearC() => Sym([
    L(   0, -200,   0,  -12),            // top lead
    L( -50,  -12,  50,  -12),            // flat top plate
    QC( -50,   22,   0,    2,  50,  22), // curved bottom plate
    L(   0,   12,   0,  200),            // bottom lead
    // nonlinear annotation: two end-ticks joined by a diagonal (−y is up)
    L( -50, -32, -50, -67),              // Line 1: left tick, above plate, upward
    L(  50,   8,  50,  43),              // Line 2: right tick, below plate, downward
    L( -50, -32,  50,   8),              // Line 3: diagonal joining the closest ends
], SymbolKind.NonlinearC);
```
(The result is a diagonal stroke with a perpendicular end-tick at each end — the conventional
nonlinear/variable-element annotation across the capacitor.) Glyph-bbox/ghost/palette all read `Primitives`,
so no other change. Eyeball the palette tile + a placed instance.

---

## 2. Type name "NLC" → "NonlinearC" (`ComponentTypeRegistry.cs`)

- In the `Registry` entry for `SymbolKind.NonlinearC`, change the **DisplayName** from `"NLC"` to
  `"NonlinearC"` (first ctor arg). Leave `InstancePrefix` = `"C"`.
- In `TryParseCode`, change the case so the canonical typed code is `"NONLINEARC"` → `SymbolKind.NonlinearC`.
  Keep `"NLC"` as an additional alias in the same case (convenient to type; harmless) unless the owner wants it
  gone. `SearchTerms` already include both — leave them.

(The on-schematic label is now "NonlinearC" — longer than "NLC" but in line with "P1Tone"/"IProbe".)

---

## 3. Parameter-editor button label "Edit C-V…" → "Edit CV…" (`ParameterEditorView.axaml`)

Change the NonlinearC-only footer button's `Content` from `Edit C-V…` to **`Edit CV…`**. No behavior change.

---

## 4. CV editor dialog revisions (`NonlinearCvEditor*` — dialog/view/VM)

### 4a. Title — remove the hyphen
`Edit C-V Data — {name}` → **`Edit CV Data — {name}`** (the `DialogTitle` binding source).

### 4b. Capacitance-unit ComboBox (one unit for the whole table)
Add a **closed Capacitance-unit ComboBox** to the header, **default `pF`**, options = the
`UnitDimension.Capacitance` list (`None, fF, pF, nF, µF, mF, F` — reuse `ComponentTypeRegistry.UnitOptions`).
It is a **single unit applied to every C value in the dialog** (both rows and text mode). Bind to
`CapacitanceUnit` on the VM.

**The fit must honor the chosen unit.** Voltages are volts (raw). Convert each entered C from the chosen unit
to **SI farads** before fitting, so the stored coefficients stay SI (as today):
```
cSI[i] = enteredC[i] * UnitScale(CapacitanceUnit)
coeffs = PolynomialFit.Fit(vArray, cSI, FitOrder)   // → SI C0..Cn, written as before
```
Use a small local scale map (do **not** route through the engine `Units` table — it's ASCII-keyed and would
mismatch the glyph strings, per `parameter-editor.md`):
`fF=1e-15, pF=1e-12, nF=1e-9, µF=1e-6, mF=1e-3, F=1, None=1`.
Persist the **chosen unit** alongside the order + the **as-entered** (display-unit) values in `CvData`, so
reopening shows exactly what the user typed in the unit they typed it in. (Coefficients remain SI; only the
table round-trips in display units.)

### 4c. Cap dialog growth at ~10 rows (scroll after)
The window is `SizeToContent="Height"`; cap the rows `ScrollViewer` with a **`MaxHeight`** sized to ~10 rows
(≈ 280 px — tune to row height) so the dialog stops growing and the list scrolls beyond 10 entries. (Text mode
already has a fixed-height multiline box.)

### 4d. Text / Rows mode toggle (mirror the VAR / MEAS editor)
Add a segmented **`Text` | `Rows`** toggle (copy `VarEditorView`'s `mode-btn` styles + `SetTextModeCommand` /
`SetRowsModeCommand` / `IsTextMode` / `IsRowsMode`). Rows mode = the existing (V, C) table. **Text mode** = a
multiline `TextBox` (`AcceptsReturn`, monospace/IBM Plex) the user pastes Excel data into.

**Parser (keep simple):** one point per line.
- Strip a trailing comment beginning at the first `;` or `//` on the line.
- Trim; skip blank lines.
- Split on **tab** (Excel paste = tab-delimited); first column = **V**, second = **C**. (Light fallback:
  if a line has no tab, split on any whitespace, so hand-typed `0.5  1.2` also parses.)
- Parse both as doubles in the current culture-invariant form; a malformed non-blank line → a validation
  error naming the line (reuse the validation-summary border).

Sync text ↔ rows the way VAR does: parse Text → staged rows on **Apply** and when switching **Text→Rows**;
serialize rows → Text on **Rows→Text**. Apply/Close semantics are unchanged from brief #4 (Apply fits +
writes; **Close discards**, does not apply).

### 4e. Live SkiaSharp "shape preview" glyph in the header
Add a tiny live C–V shape preview to the **header, immediately right of the Fit-order box**.

- **Size:** ~**45 × 25 px (W × H)** — I read "voltage on X" as wanting the long axis horizontal, and 25 tall
  fits the header row. *(The owner wrote "25 × 45"; if you meant portrait, swap W/H.)*
- **What it draws:** an always-present **border rectangle** (a muted theme border brush). Inside, the current
  (V, C) data as a **single thin polyline** — points sorted by V, autoscaled to the box (V→X, higher C→higher
  = smaller y), **no axis/labels**. **Stroke ≈ 1 px**; **line color = `ColorRole.SchematicParameterNameText`**
  (`"Schematic.ParameterNameText"`). Updates **live** as the user edits rows/text/unit.
- **Invalid/degenerate data** (fewer than 2 valid points, or a zero-width V range) → draw **only the border
  box**, no line.

**Implementation — mirror `PaletteGlyphControl`** (`src/Ui/Controls/`): a new sealed
`CvShapePreview : Control` that overrides `Render(DrawingContext)` → `context.Custom(new …DrawOperation(…))`
implementing `ICustomDrawOperation`, leasing the canvas via `ISkiaSharpApiLeaseFeature` (`using var lease =
leaseFeature.Lease(); …lease.SkCanvas`). Reuse `PaletteGlyphControl`'s theme plumbing
(`OnAttached/DetachedFromVisualTree`, `ThemeService.ThemeChanged`, `ActualThemeVariant`); resolve the
`Schematic.ParameterNameText` role color through the same theme path the renderer uses
(`SchematicRenderTheme.FromTheme(...)` / `ThemeResolver` → `Rgba` → `SKColor`). Expose a
`StyledProperty<IReadOnlyList<CvPoint>?> Points` (with `AffectsRender`); the VM publishes the currently-parsed,
unit-agnostic points (display-unit values are fine — the preview only needs shape) and the view binds
`Points` to it so it repaints live. Keep the Skia drawing self-contained in the draw operation (firewall:
rendering separable from hosting). Transparent background; the bordered box is drawn by the operation.

---

## Tests
- Symbol: if there's a primitive-count/geometry assertion for NonlinearC, update it to the new 7-primitive
  list.
- CV unit handling (VM-level): entering `C` in `pF` with a known table yields the **same SI `C0..Cn`** as the
  raw-SI fit test (i.e. `1 pF` row ⇒ coefficient `1e-12`); changing the unit to `nF` rescales accordingly.
- Text parse (VM-level): tab-delimited paste with a `;`/`//` trailing comment and a blank line parses to the
  expected staged rows; a malformed line flags a validation error and blocks Apply.
- Preview: a headless check that `CvShapePreview` produces points only for ≥2 valid distinct-V points (the
  draw op need not be pixel-tested; gate the polyline on the same validity the VM exposes).

## Unchanged
Engine path, coefficient storage (SI), Apply/Close semantics, persistence approach (CvData hidden param /
fallback field per brief #4), undo wiring.
