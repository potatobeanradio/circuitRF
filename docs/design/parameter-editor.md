# circuitRF — Parameter Editor (ParameterEditorView) Design

**Status:** Draft (rev 1) for review · **Date:** 2026-06-08 · **Phase:** 6 (schematic editor / 6g polish)

Specifies the **ParameterEditorView** — the full per-component parameter editor that realizes `ui-design.md`
§4.5 ("double-click a component → a parameter dialog listing *all* parameters, editable, with a Help button")
**and** serves as the Properties-region inspector for the selected component. One view, two host contexts.
Companions: `ui-design.md` §4.5 (parameters), §2.2 (Properties region), §5B (component-type registry),
`src/Ui/CLAUDE.md` (command-pattern undo, design-quality bar), the `frontend-design` skill (styling).

## Two decisions deferred to hands-on experimentation (owner)

The owner will decide these by trying the GUI, not on paper. Both are **presentation/host choices that do not
change the view, the view-model, or the data flow** — so the build can proceed and these get chosen by a flag
late, not designed in early. The build brief must keep both **easily swappable** rather than baking one in:

1. **Palette ↔ param-editor coexistence in the Properties region** — content-switch (show the editor when a
   single non-Ground component is selected, else the palette) **vs.** stack (palette above, editor below).
   This is purely how the Properties ToolDock arranges its two child views; the `ParameterEditorView` itself
   is identical either way. Build the editor as a self-contained `UserControl` so the host region can switch
   or stack it without the editor knowing which.
2. **Dialog modal vs. non-modal** (the double-click-component host) — again, the hosted `ParameterEditorView`
   is identical; only the window's modality flag differs. Make it a one-line choice at the dialog open call,
   not a structural assumption inside the view.

Because the editor is one reusable `UserControl` driven by one view-model (below), neither decision touches
its internals — they are host-chrome flags. Defer both; pick after experimenting.

## Purpose & the two host contexts

The ParameterEditorView shows **all** parameters of one selected component and lets the user edit each
parameter's value/unit and its **Display-on-Schematic** flag, plus the component's instance name and the two
label-visibility flags. It is a single reusable `UserControl` bound to one component, hosted in **two** places:

1. **Embedded in the Properties region** (`ui-design.md` §2.2 / §2.0 ToolDock) — shows the parameters of the
   **currently selected** component, live. When selection changes, the view rebinds to the new component (or
   shows the empty state when nothing/multiple/Ground is selected). This is the always-visible inspector.
2. **As a modal/non-modal dialog** popped on **double-clicking a component** (`ui-design.md` §4.5) — the same
   view in a dialog window with the Help button and a close affordance. The double-click-component gesture
   (already wired: `ComponentDoubleTapped`) opens this instead of today's inline-first-param fallback.

The **same `ParameterEditorView` + its view-model** drives both; only the host chrome differs (a panel header
vs. a dialog window with title bar + Help/Close). Build the view once; host it twice.

## The Ground special case (critical — handle first)

**Ground (`SymbolKind.Ground`) has no user-editable parameters and no meaningful instance name, so the
ParameterEditorView treats a Ground target EXACTLY as if nothing were selected** — it shows the **empty
state** (below), never Ground's parameters, never its component/instance name, never its label flags. This
applies in **both** host contexts:
- Properties region: selecting a Ground shows the empty state, same as an empty-canvas selection.
- Dialog: double-clicking a Ground does **not** open the dialog at all (no-op), since there is nothing to edit.

Implement this as a single guard at the top of the view-model's "bind to component" path: **if the component
is null OR its `Symbol == SymbolKind.Ground` OR the selection is empty/multiple → enter the empty state.** One
check, both contexts. (Don't scatter `if Ground` through the rows — gate once at binding.)

## Empty state

When there is no single non-Ground component to edit (nothing selected, multiple selected, or Ground
selected), the embedded Properties view shows a quiet, centered placeholder: a muted line like
**"Select a component to edit its parameters."** (Match the existing placeholder styling already in
`PropertiesView.axaml` — muted foreground, centered, ~11px.) The dialog simply doesn't open in these cases.

## Layout (top to bottom)

A **scrollable** vertical layout (the owner's "some kind of scrollable view"), because a component may have
many parameters (a ZNP has N×N `Z[i,j]` entries). Structure:

1. **Header (fixed, not scrolled):** the component **type** (registry `DisplayName(symbol, portCount)`, e.g.
   `Z2P`, `R`) shown prominently, and the editable **Instance Name** field (a text box, e.g. `R3`). Renaming
   here routes through the same `RenameComponentCommand` the inline editor uses (undoable). Validate the name
   is non-empty and unique-enough (don't hard-block, but don't allow blank).
2. **Label-visibility row (fixed):** two checkboxes — **"Show type label"** (`ShowTypeLabel`) and **"Show
   instance name"** (`ShowInstanceName`) — the per-instance flags from §5B. Toggling routes through the
   existing `SetLabelVisibilityCommand` (undoable).
3. **Parameters list (scrollable):** the body. **All** parameters in `Parameters` order, one **row** each.
   Hidden-by-convention params (e.g. `NumPorts` on variadic types) — see "Which params are editable" below.

### A parameter row

Each row is a horizontal line with, left → right:
- **Name** — read-only label (e.g. `L`, `Z[1,1]`, `Freq`). The parameter name is fixed (not user-edited here,
  matching the inline editor where the name stays fixed).
- **Value (Expression)** — an editable text box bound to `Expression` (the raw expression string, e.g.
  `2.5`, `1/(w0^2*C)`).
- **Unit** — a **ComboBox** (closed list) of the units valid for this parameter's physical **dimension**
  (see "Units" below): an inductance param offers `None / pH / nH / µH / mH / H`, a resistance param
  `None / mΩ / Ω / kΩ / MΩ / GΩ`, etc. `SelectedItem` binds to `Unit`. Dimensionless params (e.g. `NumPorts`,
  which is hidden anyway) get no unit cell. Narrow column.
- **Display on Schematic** — a checkbox bound to `ShowOnSchematic`.
- **Value preview** (FOLLOW-ON layer, not v1 — see "Value preview" below) — a subtle grey `≈ <evaluated>`
  shown after the value when the expression evaluates to a single real number at the schematic's current
  state. Non-interactive output, but text-selectable for copy. Absent for v1; the column/space is added when
  the preview layer is built.

Use a consistent column grid so the Name / Value / Unit / Display columns align down the list (a
`Grid` with shared column widths, or an `ItemsControl` whose item template uses a shared-size column group).
Rows are compact but tappable; align to the `frontend-design` spacing tokens.

### Footer (fixed)

- **Help** button (always present per §4.5) → opens the component's local HTML doc (placeholder HTML for now;
  real docs later — same behavior the §4.5 dialog specifies).
- In the **dialog** host: also a **Close** button (and standard window close). In the **embedded** host: no
  Close (the panel is persistent); the Help button still shows.

## Units — closed ComboBox keyed by physical dimension

The Unit cell is a **closed ComboBox** (pick-from-list, not free text) whose options come from the
parameter's physical **dimension**, not its component type. This is deliberately keyed by **dimension** rather
than `SymbolKind`, because the same unit list serves every parameter of that dimension across all component
types — a resistor's `R`, a ZPort's `Z[i,j]`, and a future transmission-line impedance all share the
resistance list. Keying by component would duplicate the list and let copies drift (the exact bug class the
registry §5B exists to prevent); keying by dimension keeps one list per dimension, referenced everywhere.

### The dimension enum (in the registry, Avalonia-free)
Add to `ComponentTypeRegistry` (or alongside it, same framework-free layer):
```
public enum UnitDimension { None, Resistance, Inductance, Capacitance, Frequency, Voltage, Current, Power, Length, Angle }
```

### Per-dimension unit options (single source of truth)
A `Dictionary<UnitDimension, string[]>` in the registry. **"None" is element [0] of every list** (the
consistent "clear / no unit" choice). Canonical strings use proper glyphs (`µ`, `Ω`) — the bundled DejaVu/IBM
Plex fonts cover them, and because the list is closed the user never types `u` vs `µ` or `ohm` vs `Ω`, so the
unit strings are standardized for the eventual expression/unit parser. Lock these canonical strings now:

| Dimension | Options (element [0] = "None") |
|---|---|
| `None` | `None` |
| `Resistance` | `None`, `mΩ`, `Ω`, `kΩ`, `MΩ`, `GΩ` |
| `Inductance` | `None`, `pH`, `nH`, `µH`, `mH`, `H` |
| `Capacitance` | `None`, `fF`, `pF`, `nF`, `µF`, `mF`, `F` |
| `Frequency` | `None`, `Hz`, `kHz`, `MHz`, `GHz`, `THz` |
| `Voltage` | `None`, `nV`, `µV`, `mV`, `V`, `kV` |
| `Current` | `None`, `nA`, `µA`, `mA`, `A` |
| `Power` | `None`, `fW`, `pW`, `nW`, `µW`, `mW`, `W`, `dBm` |
| `Length` | `None`, `nm`, `µm`, `mm`, `cm`, `metre`, `mil` |
| `Angle` | `None`, `deg`, `rad` |

(`dBm` is intentionally in the Power list — RF work uses it as a power unit; it's a closed-list option, not a
separate dimension. Extend any list later by editing the one table — no other code changes.)

**Length reads `metre`, not `m`, and that is load-bearing rather than a style choice.** In the expression
engine's unit table (`expressions.md` §8) **`m` is the SI prefix MILLI** — it has to be, because a
hand-authored `C=1m` means one millifarad in every netlist dialect there is. Offering `m` here for a
*length* would therefore hand the user a value a thousand times too small, silently: that is exactly the
bug brief-core-length-units.md closed on 2026-08-07 (`nm` and `cm` were worse — both evaluated to a
multiplier of exactly 1, so `L = 1 cm` resolved to 1.0). Every option in this row must be a symbol
`Units.Scale` actually carries; `metre` is the engine's own scale-1 length symbol, so the two agree
everywhere rather than meaning different things in two places.

**`in`/`inch` are deliberately NOT offered here**, although the expression engine accepts both (2.54e-2)
and `LayoutUnits` always has. Adding them needs an inch row in `MicrostripSubstrateInjection`'s
`ConvertMmTo`/`RoundStepFor`/`NiceLengthFor` tables first — each currently falls through to a *wrong* mm
value for an unrecognised unit. A hand-written `.cnl` may use them today; this dropdown does not.

### Tagging each parameter with its dimension
Add a `UnitDimension` field to the `DefaultParam` record so the registry's default-parameter template carries
the dimension per parameter:
```
public readonly record struct DefaultParam(string Name, string Expression, string Unit, bool ShowOnSchematic, UnitDimension Dimension);
```
Then tag each entry in `DefaultParameters(kind, portCount)`: `R`→`Resistance`, `L`→`Inductance`,
`C`→`Capacitance`, `Freq`→`Frequency`, `Vac`/`V`→`Voltage`, ZPort `Z[i,j]`→`Resistance`, `NumPorts`→`None`,
SDD's params→`None` (user-authored). The default `Unit` string for each param must be a member of its
dimension's list (e.g. `L`'s default `nH` is in the Inductance list) so the ComboBox shows it pre-selected.

### Row view-model & the control
- The parameter-row VM exposes `UnitOptions` (the `string[]` for its dimension, via
  `ComponentTypeRegistry.UnitOptions(dimension)`) and binds the Unit ComboBox `ItemsSource` to it, with
  `SelectedItem` ↔ `Unit`. Selecting a unit commits via the same `EditParameterCommand` (undoable).
- **Dimension source:** the row VM needs each live parameter's dimension. Since `EditableParameter` is
  seeded from the registry template, the simplest path is to **carry the dimension on `EditableParameter`**
  (add a `UnitDimension Dimension` field, seeded from `DefaultParam.Dimension` at placement and persisted in
  `.csch`); the row VM reads `param.Dimension`. (Alternative — look the dimension up by `(symbol, paramName)`
  via a registry helper each time — avoids persisting it but re-derives per row; carrying it on the param is
  simpler and survives a param whose name doesn't match a template entry. Recommend carrying it on
  `EditableParameter`.)
- **Unknown / dimensionless:** a param with `UnitDimension.None` → the Unit cell shows a disabled ComboBox
  containing only `["None"]`, or is omitted entirely (for hidden params like `NumPorts` it's omitted with the
  row). **Do NOT mix control types per row** (some Unit cells ComboBox, some TextBox) — it reads as
  inconsistent and complicates the column template. One control type (ComboBox) for every visible row.
- **Freeform units are deferred.** If a genuinely custom unit string is ever needed, add a
  `UnitDimension.Freeform` rendering an *editable* ComboBox (`IsEditable=true`) — not in v1; the closed list
  covers RF needs and keeps strings typo-proof.

### Effort note
Low — the registry already centralizes per-type knowledge, so this is one enum + one `Dictionary` + one field
on `DefaultParam` (and one on `EditableParameter`) (~30–40 lines in the registry/model), ~10 lines in the row
VM, and a one-control swap (TextBox→ComboBox) in the AXAML. No new architecture; it extends the existing
single-source-of-truth registry pattern.

## Value preview (FOLLOW-ON layer — build after the v1 editor)

**STATUS: IMPLEMENTED (2026-06-08).** Built directly on the landed v1 editor. Files: `DesignScope.cs`
(scope builder), preview logic in `ParameterRowViewModel.cs` (`ValuePreview`/`HasValuePreview` +
`RecomputePreview`), and a `SelectableTextBlock` preview cell in `ParameterEditorView.axaml`. Two reality
adjustments made during implementation (both noted inline below): there is **no Var layer yet** (the §7.2 Var
tool isn't built — `SymbolKind` has no `Var`), so the scope collects named component parameters only and is
Var-ready for later; and **units are NOT passed into the engine** because the engine's `Units` table is
ASCII-keyed (`Ohm`, `uH`, `uF`) while the editor's ComboBox uses glyphs (`Ω`, `µH`, `µF`) — a mismatch that
would throw — so the preview shows the raw evaluated value (display-unit scaling stays deferred, as planned).

A quiet **"evaluates-to" preview**: when a parameter's value is an *expression* (not a bare number), show its
evaluated result in subtle grey — e.g. a row whose value is `1/(w0^2*C)` shows `≈ 1.27 pF` beside it. This
lets the user see roughly what a parameter resolves to **before** running a simulation. **It is a follow-on
enhancement to the v1 editor, not part of the v1 build** — listed here so it's captured against the engine
API; build it as a separate layer once the editor works.

### It evaluates against the schematic's current state, via the expression engine
- The engine entry point is `Evaluator.Eval(string expression, Scope scope, string? unit = null)` (in
  `src/Core/Expressions/Evaluator.cs`) — it parses and evaluates a bare expression string and returns a
  `Value`. No `MeasurementContext` is needed (that's only for post-run accessors like `HB1.V(...)`); a plain
  `new Evaluator()` suffices. The engine is framework-free, so a `src/Ui` VM may call it directly (it already
  references nothing below the firewall that this would violate — the engine is Core, which UI may consume).
- **The work is building the `Scope`, not the eval call.** `1/(w0^2*C)` can't resolve without `w0` and `C`.
  Add a small **`SchematicEditModel → Scope` helper** that collects the schematic's current named values into
  scope bindings (`Scope.Bind(name, expression, unit)`): every **Var** component (the `name = expr`
  definitions, §7.2 Var tool) and the component's own + other components' **named parameters** that an
  expression might reference. This is a lightweight, design-time mirror of what the real Elaborator builds at
  run time — so it's a useful, reusable step toward extraction/elaboration, not throwaway. (Keep it simple
  for v1-of-preview: Vars + the editing component's sibling params; expand the resolvable set later if
  needed.)

### What is previewed — and what is NOT (the gates fall out of the engine)
- **Preview only when the result is a single real number.** `Eval` returns a `Value` whose `Kind` says what
  it is. **Show the preview only when `Kind == Real`** (optionally `Complex`, formatted as `a + bj`).
  Anything else → no preview. This is the clean gate for the owner's exclusions:
  - **Arrays / sweeps** → come back as `ValueKind.Cube` (or otherwise non-scalar) → no preview. You don't
    predict array-ness; you evaluate and check the kind that came back.
  - **SDD expressions** → **do not evaluate at all.** Gate by component type: skip the preview for
    `SymbolKind.Sdd`/`FetSdd` (their equations are user-authored device expressions evaluated in the SDD's
    own context via `SddEvaluator`, not the scalar scope). Don't even call `Eval` for SDD params.
  - **Unresolvable / parse error / cycle** → `Eval` throws (`UnresolvedNameException`, `ParseException`,
    `CycleException`). **Catch and show nothing** — a preview that can't resolve simply doesn't appear; never
    surface an error to the user from a preview.
- **Only when the value is actually an expression.** If the value is already a bare number (e.g. `2.5`), don't
  show `≈ 2.5` — it's noise. Show the preview only when evaluating adds information (the parsed expression
  isn't a single `NumberExpr`, or more simply: the result differs from the literal text / the text isn't
  purely numeric).

### Presentation (HIG — it must whisper, not compete)
- **Placement:** inline, trailing the value (after Value/Unit) in the same row — **subtle grey** using the
  theme's muted-text role (the color-theme work supplies it). Prefixed with **`≈`** to mark it *approximate /
  pre-simulation*. It appears only for expression-valued, real-valued, resolvable, non-SDD params; absent
  otherwise (progressive disclosure — never clutters simple literal params, never an empty column staring
  back).
- **Non-interactive and non-editable — but text-selectable for copy.** The preview is **output, not a field**:
  it takes no focus, has no caret, cannot be edited, and is skipped by tab navigation. **However the user can
  still select the preview text and copy it to the clipboard** (so they can grab the evaluated number). Use a
  read-only selectable text presentation (e.g. a `SelectableTextBlock`, or a read-only/`IsHitTestVisible`-for-
  selection text control) — selectable + copyable, but never an input the user could type into or that
  participates in editing/commit.
- **The `≈` and grey are load-bearing for the mental model:** the preview is "what this evaluates to *right
  now, at the schematic's current values*," NOT "what the simulation will use." They can differ (the run may
  sweep a Var, apply an instance override, or resolve in a richer context). The approximate-marker + muted
  styling keep it advisory so no one mistakes it for the authored value or the simulated result.
- **Debounce:** recompute on a short debounce (e.g. after the user pauses typing / on commit), not every
  keystroke — evaluation is cheap but per-keystroke scope-rebuilds are wasteful and could flicker.

### Effort & sequencing
Moderate — the `Eval` call is trivial; the `SchematicEditModel → Scope` helper is the real piece (a small,
reusable design-time variable-resolution pass). Build it as its **own layer after the v1 ParameterEditor is
working and confirmed**, so the editor ships first and the preview is added without entangling the two. Unit
of the preview: show the engine's evaluated number; rendering it in the param's *display* unit is a unit-
conversion step (engine `Units.cs` may help) that can be deferred — v1-of-preview may show the raw evaluated
value with the engine's own unit handling.

## Which parameters are editable / shown

- **Every** parameter in `Parameters` is **viewable** (the owner: "all parameters … should at least be
  viewable"). Show them all as rows.
- **Editable where appropriate:** the `Expression`, `Unit` (via the dimension ComboBox above), and
  `ShowOnSchematic` of normal parameters are editable. **Structural / hidden params need care:**
  - **`NumPorts`** (variadic ZPort/Sdd) is the **port count** — editing it as free text would desync the
    symbol's pin geometry from the parameter. **Do NOT expose `NumPorts` as a normal editable value row.**
    Either (a) hide it from the list entirely (recommended for v1 — port count is changed via the inline type
    editor `Z{N}P`/`SDD{N}`, which correctly regenerates ports), or (b) show it **read-only**. Pick (a) hide
    for v1; note the choice. (The registry/`PortCount` is the source of truth for ports; the editor must not
    create a second way to set it that bypasses port regeneration.)
  - A parameter with an empty `Name` (shouldn't happen for registry-seeded components, but be defensive) →
    skip or show read-only; don't crash.
- The list reflects the component's **actual** `Parameters` (registry-seeded at placement), so a ZNP shows
  its full `Z[i,j]` matrix as rows, a tone source shows `V`/`Freq`, etc. — no per-type special-casing in the
  view beyond the `NumPorts` hide.

## Editing semantics — all through the command stack

**Every edit is an undoable command** (the standing rule, `src/Ui/CLAUDE.md` §10). Reuse the existing
commands so there's one mutation path:
- Value/unit change on a parameter → **`EditParameterCommand`** (the one that now notifies in Execute+Undo).
- `ShowOnSchematic` toggle → a small command (add **`SetParameterVisibilityCommand`** if none exists, mirroring
  `SetLabelVisibilityCommand`: set the bool in Execute/Undo, call `EditModel.NotifyChanged()` in **both** so
  the schematic re-renders the label appearing/disappearing).
- Instance-name change → **`RenameComponentCommand`**.
- Show-type-label / show-instance-name → **`SetLabelVisibilityCommand`**.

**Commit timing:** commit a text-box edit on **Enter** and on **focus-loss** (same as a normal property grid).
Apply the **same idempotency discipline** the inline editor uses — don't double-commit on Enter-then-blur;
capture the value, and a no-change edit pushes no command. (If it's simplest, route value edits through the
*same* commit helper the inline editor uses so the two share one safe path.) A checkbox commits immediately on
toggle.

**Live reflection:** because every command calls `EditModel.NotifyChanged()` (Execute and Undo), the schematic
re-renders as the user edits — a value change updates the on-schematic label, toggling Display-on-Schematic
makes the label appear/disappear live, all undoably. The embedded Properties view and the schematic stay in
sync through the model, not through view-to-view wiring.

## View-model & data flow

- A **`ParameterEditorViewModel`** holds the bound `EditableComponent` (or null/empty state) and exposes:
  the type display name, instance name, the two label flags, and an observable collection of
  **parameter-row view-models** (each wrapping one `EditableParameter` with Name / Expression / Unit /
  ShowOnSchematic and the commit commands).
- **Binding source:** the embedded host binds the VM's target to the schematic's **current selection** (when
  exactly one non-Ground component is selected). Subscribe to `Selection.Changed`; on change, resolve "single
  selected component?" and set the target (or empty state per the Ground/none/multiple guard). The dialog host
  sets the target once from the double-clicked component.
- The row VMs read/write through the commands above against the live `EditableComponent` — no copy/apply
  buffer; edits are immediate and undoable (consistent with the inline editor and the rest of the editor).
- **Firewall:** the VM is `src/Ui` (it references `EditableComponent`/commands, which are UI-layer). Fine —
  this is view-model code, above the firewall line. No engine/Core dependency.

## Wiring into the app

- **Properties region:** replace the `PropertiesView.axaml` placeholder body (or add a second region/section)
  with the embedded `ParameterEditorView`. Per §2.2 the Properties region *first* hosts the palette; the
  parameter editor is an additional inspector role the region "is expected to grow." For v1, host the
  ParameterEditorView in the Properties region as the **selected-component inspector** (palette coexistence:
  either stack palette-above / params-below, or switch the region's content based on whether a component is
  selected — recommend: show the parameter editor when a single non-Ground component is selected, else show
  the palette/empty state. Note the chosen approach in `src/Ui/CLAUDE.md`.)
- **Dialog:** change the `ComponentDoubleTapped` handler in `SchematicView.axaml.cs` — instead of the current
  inline-first-param fallback, open a dialog hosting `ParameterEditorView` bound to the double-clicked
  component. (Keep the inline single-param editing on the schematic for the click-a-label gesture; the
  double-click-the-component-body gesture opens the full dialog.) Ground double-click → no dialog (the guard).

## Acceptance

- Selecting a single non-Ground component shows all its parameters in the Properties region; editing a value/
  unit updates the schematic label live and is undoable; toggling Display-on-Schematic shows/hides that label
  live and is undoable; editing the instance name and the two label-visibility flags works and is undoable.
- Double-clicking a (non-Ground) component opens the same editor as a dialog with a Help button.
- **Ground:** selecting Ground shows the empty state (no params, no name); double-clicking Ground opens
  nothing. Multiple-selection and empty-selection also show the empty state.
- A ZNP shows its full `Z[i,j]` matrix as rows; `NumPorts` is **not** an editable value row (hidden for v1);
  changing port count is still done via the inline `Z{N}P` type editor (unchanged).
- The Unit cell is a **closed ComboBox** whose options match the parameter's dimension (inductance param →
  `None/pH/nH/µH/mH/H`, resistance param → `None/mΩ/Ω/kΩ/MΩ/GΩ`, etc.); the param's existing unit shows
  pre-selected; selecting a different unit is undoable; dimensionless/hidden params show no unit cell.
- All edits route through the command stack (no direct model mutation); the list is scrollable; columns align;
  styling matches the `frontend-design` tokens. Firewall green.

## Out of scope (note, don't build)

- Real component HTML docs (Help opens placeholder HTML — §4.5).
- Editing parameter **names** or adding/removing parameters (the set is registry-seeded; name edits and
  add/remove are a later feature if needed).
- Multi-component batch editing (edit-all-selected) — v1 is single-component; empty state for multi-select.
- A separate ParameterEditor for canvas objects/text (those have their own context-menu properties, §3.1).
- **Freeform / editable unit ComboBox** (`UnitDimension.Freeform`) — deferred; v1 units are a closed list per
  dimension. Unit-aware value *conversion* (changing nH→µH rescaling the expression) is also out of scope —
  the ComboBox only sets the unit string; it does not convert the value.
- **Value preview** (the `≈ <evaluated>` grey expression preview) — designed above as a **follow-on layer**,
  NOT part of the v1 editor build. The v1 ParameterEditor ships without it; the preview is a separate layer
  added afterward (it needs the `SchematicEditModel → Scope` helper). Captured here so it's not lost.
