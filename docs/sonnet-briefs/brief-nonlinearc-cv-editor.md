# Brief #4: NonlinearC C–V data editor (Method 2) — HIG-compliant

Design refs: `docs/design/nonlinear-in-linear-engines.md` (DECISION 1 — edit-time fit, coefficients are the
canonical stored form), `docs/design/parameter-editor.md` (the param editor this hangs off), `ui-design.md`
§4.5. Depends on briefs #2 (`PolynomialFit.Fit`, `NonlinearCModel` reads `C0…Cn`) and #3 (`SymbolKind.NonlinearC`,
registry, `C0` default + `C{n}` "+" template).

**What this adds:** a second way to author a NonlinearC's capacitance. Method 1 (already works after brief #3):
the user types `C0, C1, …` directly in the normal parameter editor. Method 2 (this brief): a **C–V data editor
dialog** where the user enters measured/target (V, C) points and a fit order; on **Apply** it fits a polynomial
(`PolynomialFit.Fit`) and writes the resulting `C0…Cn` into the instance's parameters. The raw C–V table + order
persist in the `.csch` so the user can reopen and re-edit. **Coefficients remain the single source of truth the
engine reads** — the table is convenience input, not a second model.

**The template to clone:** `VarEditorDialog` + `VarEditorView` (+ `VarEditorViewModel` / `VarRowViewModel`) in
`src/Ui/Views/Dialogs/` and `src/Ui/ViewModels/`. Match their structure, styling tokens, commit pattern, and
undo/redo wiring exactly. This brief only calls out where the C–V editor **differs**.

Build **0W/0E** (nullable-on-properties → locals; no `<`/`>` in XML doc comments). Add the VM test. Report
count; newest-first changelog.

---

## New files (mirror the Var quartet)

- `src/Ui/Views/Dialogs/NonlinearCvEditorDialog.axaml` (+ `.axaml.cs`) — the `Window`.
- `src/Ui/Views/Dialogs/NonlinearCvEditorView.axaml` (+ `.axaml.cs`) — the `UserControl` body.
- `src/Ui/ViewModels/NonlinearCvEditorViewModel.cs` — construction/command-stack wiring mirrors
  `VarEditorViewModel` (same owning-schematic / `EditableComponent` / command-stack parameters — copy its ctor
  signature and the undo/redo command exposure).
- `CvRowViewModel` (in the same VM file or alongside, like `VarRowViewModel`) — one (V, C) row.

---

## 1. The dialog window (`NonlinearCvEditorDialog.axaml`)

Clone `VarEditorDialog.axaml`. Same `Width="400"`, `MinWidth`/`MinHeight`, `SizeToContent="Height"`,
`CanResize="True"`, `WindowStartupLocation="CenterOwner"`, `ShowInTaskbar="False"`, and the **same five
Ctrl/Meta Undo/Redo `KeyBindings`** (they act on the owning schematic's stack — coefficient/table writes are
undoable). Title: `Edit C–V Data — {instance name}` (bind to a `DialogTitle`; an ASCII `C-V` is fine if the en
dash is awkward). Body is `<d:NonlinearCvEditorView/>`.

---

## 2. The editor body (`NonlinearCvEditorView.axaml`) — layout top→bottom

Reuse Var's compact styles (11px text boxes, 8px outer margin, 6px footer spacing, the muted-hint and
warning-border idioms). `Grid RowDefinitions="Auto,Auto,Auto,*,Auto"`:

- **Row 0 — header:** panel title (`SemiBold` 13px) + instance name (muted 11px), like Var's header. No
  Text/Rows mode toggle (the C–V editor is rows-only).
- **Row 1 — fit order:** a small labelled control: `Fit order:` + a numeric stepper/`NumericUpDown` (or a
  narrow TextBox validated to an integer ≥ 0) bound to `FitOrder`. Default 3. Place it inline, right-aligned or
  next to the header.
- **Row 2 — validation summary:** the same warning `Border` as Var (`CrfWarningBrush`, icon-or-text, **never
  color alone** — the text carries the meaning), visible when `HasValidationErrors`. Drives the Apply enable
  (below). Messages: "Need at least {order+1} points for order {order}." / "Row {n}: V and C must be numbers."
- **Row 3 — the (V, C) table (scrollable):** an `ItemsControl` over `Rows`, `Grid.IsSharedSizeScope="True"`,
  each row `Grid ColumnDefinitions="*,*,Auto"`:
  - **V** TextBox ↔ `StagedV` (placeholder "V", `ToolTip.Tip="Terminal voltage (V)"`),
  - **C** TextBox ↔ `StagedC` (placeholder "C", `ToolTip.Tip="Capacitance (F)"`),
  - **×** remove `Button` (`Command="{Binding RemoveCommand}"`, transparent, `ToolTip.Tip="Remove point"`).
  Commit each cell on **LostFocus + Enter/Tab** via code-behind handlers calling `row.CommitV()` /
  `row.CommitC()` (copy Var's `OnRow*LostFocus`/`OnRow*KeyDown` handlers verbatim, renamed). Below the list, a
  left-aligned **`+ Add point`** button (`AddRowCommand`).
  - Units: V in volts, C in **farads (raw SI)** — the table is raw numeric (no unit ComboBox), matching how the
    coefficients are stored (brief #3: `C1+` raw; `C0` resolves through its unit). Keep the table raw F to avoid
    a unit-scaling layer; note "C in farads" in the column hint.
- **Row 4 — footer (HIG, see §4).**

### Optional (recommended, deferrable) — live preview
A small C–V plot (data points + the fitted curve) above or beside the table makes the fit legible and serves
the owner's "joy to use" bar. **Mark as a follow-on**, not v1-blocking: v1 ships table + order + Apply; the
preview is an additive layer (reuse the data-display plot machinery, or a minimal hand-drawn `Canvas`). Note it
in the changelog as deferred so it isn't lost.

---

## 3. Apply / Close semantics (the key difference from Var)

**Staging model:** the dialog holds the (V, C) rows + order **locally** (staged). Nothing touches the component
until **Apply**.

**Apply** (`ApplyCommand`):
1. Validate: all rows parse as numbers; `rowCount ≥ FitOrder + 1`. If invalid → set `HasValidationErrors`, do
   **not** write. (Disable the Apply button while invalid — bind `IsEnabled` to `!HasValidationErrors`.)
2. Fit: `var coeffs = PolynomialFit.Fit(vArray, cArray, FitOrder);` (lowest-power-first, per brief #2).
3. Write coefficients **and** persist the table in **one undoable step** — add a dedicated
   **`ApplyCvFitCommand`** (snapshot the instance's current coefficient params + `CvData` in the ctor; Execute
   sets `C0…Cn` from `coeffs` and `CvData` from the serialized table; Undo restores the snapshot). A single
   custom command (rather than a burst of `EditParameterCommand` + add/remove-param commands) makes one Apply =
   **one undo step** and keeps coefficient count and table atomically consistent. It must:
   - set `C0 … C{n}` (n = `coeffs.Length-1`) to the fitted values, **adding or removing** coefficient params so
     the instance has exactly the params the fit produced (reuse whatever add/remove-parameter primitive the
     param editor's `+`/`−` template uses; if none is cleanly reusable inside a command, have `ApplyCvFitCommand`
     mutate the `EditableComponent.Parameters` collection directly in Execute/Undo, since it owns the snapshot),
   - set the hidden `CvData` param (see §5),
   - call `EditModel.NotifyChanged()` in **both** Execute and Undo (so the schematic label + param editor
     refresh live), matching the standing command-pattern rule.
4. **Apply does not close the dialog** (the user can tweak points and re-apply). After a successful Apply, clear
   `HasValidationErrors`.

**Close** (`CloseCommand` / window close): **does NOT fit, does NOT apply.** It dismisses the dialog and
**discards** any staged edits since the last Apply. (This is the deliberate divergence from `VarEditorView`,
whose Close applies pending text — do **not** copy that here.) Rationale: the persisted table always mirrors the
last-applied coefficients, so reopening never shows a table that disagrees with the live `C0…Cn`. The owner's
rule: *the user is responsible for clicking Apply.*

(If the owner later wants "Close with unsaved edits" to prompt, that's an additive confirm — out of scope here.)

---

## 4. HIG conventions (the owner's explicit ask)

- **Default + cancel keys:** the primary button (**Apply**) is `IsDefault="True"` (Enter triggers it); **Close**
  is `IsCancel="True"` (Esc triggers it). This is the substantive keyboard win — wire both.
- **Per-cell Enter vs. dialog default:** the V/C row TextBoxes are single-line and commit on Enter (their
  KeyDown handler). To avoid ambiguity between "commit this cell" and "Apply the dialog", also bind an explicit
  **Cmd+Enter / Ctrl+Enter** accelerator to `ApplyCommand` (a `KeyBinding`), and keep Apply as a visible button.
  Per-cell Enter commits the cell and moves focus; the user clicks Apply (or Cmd+Enter) to fit.
- **Button order / placement:** right-aligned footer group. Put the **primary (Apply) at the trailing/right
  edge** with **Close to its left** — the macOS HIG ordering (default button trailing). *(This intentionally
  differs from `VarEditorView`'s Apply-then-Close order; HIG is the owner's stated priority here. Flag this to
  the owner so they can decide whether to realign Var too — don't silently diverge.)*
- **No destructive default:** the default action (Apply) is non-destructive; row removal (`×`) is per-row and
  never the default. Good as designed.
- **Spacing/typography:** use the `frontend-design` tokens already in `VarEditorView` (8px margins, 6px footer
  spacing, 11px compact fields) — don't invent new metrics.
- **Accessibility:** every icon-only control (the `×`) has a `ToolTip.Tip`; validation conveys meaning via
  **text** (+ the warning brush), never color alone — matches the Messages-region rule.
- **Resizable, sized to content:** keep `CanResize` + `SizeToContent="Height"` + `MinWidth/MinHeight` (a data
  table benefits from growing).
- **Title:** title-case, names the instance (`Edit C–V Data — C3`).

---

## 5. Persistence of the raw C–V table (`CvData`) + re-open

- Store the table + order in a **hidden instance param `CvData`** (engine-ignored). Serialize compactly, e.g.
  `"0,1e-12;1,1.1e-12;2,1.4e-12|order=3"`. The brief-#2 factory reads only `C0…Cn` (Real) and never looks at
  `CvData`, so it's inert to the engine.
- **Hide `CvData` from the normal parameter editor** — add its name to the same hidden-param set that already
  hides `NumPorts` (`parameter-editor.md` "Which params are editable"). The user edits the table through this
  dialog, not as a raw row. `C0…Cn` stay visible/editable (that's Method 1).
- **Re-open:** the VM loads the component's `CvData` (parse it) to repopulate `Rows` + `FitOrder`. If absent
  (e.g. coefficients were typed directly via Method 1), start with two blank rows and `FitOrder = 3` — and note
  in a muted hint that applying will overwrite the current `C0…Cn`.

**⚠ Verify (load-bearing, NOT confirmed on disk):** that a **String-valued instance param round-trips through
`.csch`** (`SchematicPersistence`) and back without the expression evaluator choking on it. Two options, pick
per what persistence actually supports:
  (a) store `CvData` as a **quoted string-literal expression** (`"…"`) so it parses to a `ValueKind.String`
      Value and survives elaboration (the factory ignores it); or
  (b) if String params don't round-trip cleanly, add a **dedicated optional `.csch` field** for the C–V table
      on the instance record (alpha no-back-compat allows adding a defaulted/nullable field with **no**
      `format_version` bump — `project-file-formats.md`).
Confirm which on disk before building; don't assume (a) works.

---

## 6. Integration — opening the dialog from the parameter editor

NonlinearC keeps the **normal** parameter editor (double-click → `ParameterEditorDialog`, with `C0…Cn` editable
= Method 1). Method 2 is reached by a button:

- Add an **`Edit C–V…`** button to `ParameterEditorView`'s footer (next to **Help**), **visible only when the
  bound component is NonlinearC**. Expose on `ParameterEditorViewModel`: `bool ShowCvEditorButton` (=
  `Symbol == SymbolKind.NonlinearC`) and `OpenCvEditorCommand`.
- `OpenCvEditorCommand` constructs `NonlinearCvEditorViewModel(component, commandStack, …)` (mirror
  `VarEditorViewModel`'s ctor args) and shows `NonlinearCvEditorDialog` **the same way `VarEditorDialog` is
  shown** — grep `new VarEditorDialog` to copy the exact owner-window resolution + `ShowDialog(owner)` call (and
  modal/non-modal choice). Reuse that wiring verbatim.

---

## 7. Test (VM-level, headless)

`NonlinearCvEditorViewModel` test (no UI):
- Seed rows from a known varactor table + `FitOrder=3`; call Apply; assert the component's `C0…Cn` now equal
  `PolynomialFit.Fit(v,c,3)` (same values brief #2's fit test uses) and that `CvData` round-trips (serialize →
  new VM → parse → same rows/order).
- **Close discards:** stage edits, Close, assert the component's coefficients are unchanged from before the
  dialog.
- **Validation gates Apply:** `FitOrder=3` with only 2 points → `HasValidationErrors` true, Apply writes
  nothing.
- **Undo:** after Apply, one undo restores the pre-Apply coefficients (and `CvData`) — proves the single-step
  `ApplyCvFitCommand`.

---

## Out of scope
- The live C–V preview plot (§2) — recommended follow-on, not v1.
- Unit-aware C entry (table is raw F) — defer.
- Re-fit-on-every-keystroke — Apply is explicit by design.
