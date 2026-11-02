# Phase 6 — Parameter Editor (ParameterEditorView) + Unit ComboBox (Claude Code / Sonnet)

Build the per-component parameter editor. The design is fully specified in `docs/design/parameter-editor.md`
— **read it first and implement to it**; this brief is the build plan, the doc is the authority. Sub-gated in
layers; report and stop between them. Firewall green throughout; every edit undoable.

> Read first: `docs/design/parameter-editor.md` (authoritative — the view, the Ground special case, the units
> ComboBox, the command-stack editing, the two deferred host decisions). Also: `docs/design/ui-design.md`
> §4.5 (parameters), §2.2 (Properties region), §5B (component-type registry); `src/Ui/CLAUDE.md` (command
> pattern, design bar); the `frontend-design` skill (styling tokens). Context code:
> `src/Ui/Schematic/ComponentTypeRegistry.cs` (DefaultParam, DefaultParameters, the metadata),
> `src/Ui/Schematic/EditableSchematic.cs` (EditableParameter, EditableComponent — Symbol/PortCount/Parameters/
> ShowTypeLabel/ShowInstanceName), `src/Ui/Commands/Schematic/EditParameterCommand.cs` (EditParameterCommand,
> RenameComponentCommand — now self-notifying), `src/Ui/Commands/Schematic/SetLabelVisibilityCommand.cs`,
> `src/Ui/Views/Properties/PropertiesView.axaml(.cs)` + `ViewModels/Dock/PropertiesTool.cs` (the region to
> host in), `src/Ui/Views/Content/SchematicView.axaml.cs` (the `ComponentDoubleTapped` handler to repoint),
> `src/Ui/ViewModels/SchematicViewModel.cs` (Selection, EditModel, Execute, the inline-edit commit helper to
> share). Design doc wins on any conflict.

## The spine (do not violate)
- **One reusable `ParameterEditorView` (`UserControl`) + one `ParameterEditorViewModel`**, hosted two ways
  (Properties-region inspector, and double-click dialog). Build the view once; host it twice. Neither host
  decision (coexistence layout, dialog modality) touches the view's internals — see "Deferred" below.
- **Every edit is an undoable command through the existing stack** — reuse `EditParameterCommand`,
  `RenameComponentCommand`, `SetLabelVisibilityCommand`; add `SetParameterVisibilityCommand` (new, mirrors
  the label one). All mutation commands notify `EditModel.NotifyChanged()` in **both** Execute and Undo (the
  standing rule — the others already do; the new one must too).
- **Ground special case is a single guard at binding** — Ground / null / empty-selection / multi-selection
  all → empty state. Do not scatter `if Ground` through rows.
- **Firewall:** VM + view live in `src/Ui` (they reference `EditableComponent`/commands, which are UI-layer)
  — fine, above the line. No engine/Core dependency.

---

## LAYER 1 — Units in the registry (dimension enum + tables + tagging)

Do this first; the editor's Unit ComboBox depends on it. Spec: `parameter-editor.md` "Units" section.

1. **Add `UnitDimension` enum** (framework-free, in/next to `ComponentTypeRegistry`):
   `None, Resistance, Inductance, Capacitance, Frequency, Voltage, Current, Power, Length, Angle`.
2. **Add the per-dimension options table** — a `Dictionary<UnitDimension, string[]>` (or equivalent) with the
   exact lists from the doc's table, **"None" as element [0] of every list**, proper glyphs (`µ`, `Ω`):
   - Resistance: `None, mΩ, Ω, kΩ, MΩ, GΩ` · Inductance: `None, pH, nH, µH, mH, H`
   - Capacitance: `None, fF, pF, nF, µF, mF, F` · Frequency: `None, Hz, kHz, MHz, GHz, THz`
   - Voltage: `None, nV, µV, mV, V, kV` · Current: `None, nA, µA, mA, A`
   - Power: `None, fW, pW, nW, µW, mW, W, dBm` · Length: `None, nm, µm, mm, cm, m, mil`
   - Angle: `None, deg, rad` · None: `None`
   Expose `ComponentTypeRegistry.UnitOptions(UnitDimension) → string[]`.
3. **Add `UnitDimension Dimension` to the `DefaultParam` record**, and tag every entry in
   `DefaultParameters(kind, portCount)`: `R`→Resistance, `L`→Inductance, `C`→Capacitance, `Freq`→Frequency,
   `Vac`/`V`→Voltage, ZPort `Z[i,j]`→Resistance, `NumPorts`→None, SDD params→None. Each param's **default
   `Unit` string must be a member of its dimension's list** (e.g. `L`'s `nH` ∈ Inductance) so the ComboBox
   shows it pre-selected. (Frequency default unit is currently `GHz` for V/VTone — `GHz` ∈ Frequency, good.)
4. **Carry the dimension onto `EditableParameter`**: add `UnitDimension Dimension { get; set; } = None;`, seed
   it from `DefaultParam.Dimension` everywhere a param is created from the template (placement in
   `HandlePlacePress`, type-change in `CommitInlineEdit`, and `FromRenderModel`), include it in
   `EditableParameter.Clone()`, and **persist it in `.csch`** (add to the param serialization; alpha policy =
   fresh write, no migration). The row VM reads `param.Dimension`.
5. **Tests:** `UnitOptions` returns the right list per dimension with `None` first; a freshly-placed resistor/
   inductor/capacitor/tone-source has each param tagged with the correct dimension and a default unit that is
   a member of that dimension's list; `.csch` round-trips `Dimension`.

**Layer 1 gate:** registry units (enum + tables + `UnitOptions`), `DefaultParam.Dimension` tagged,
`EditableParameter.Dimension` seeded/cloned/persisted, tests. Framework-free (firewall green). Report.

---

## LAYER 2 — `SetParameterVisibilityCommand` + verify the command set

1. **Add `SetParameterVisibilityCommand`** (in `Commands/Schematic/`, alongside `SetLabelVisibilityCommand`):
   holds `SchematicEditModel`, the `EditableParameter`, and the new bool; `Execute()` sets
   `param.ShowOnSchematic = newValue` then `_model.NotifyChanged()`; `Undo()` restores the old value then
   `_model.NotifyChanged()`. (Mirror `SetLabelVisibilityCommand` exactly — both directions notify.)
2. **Confirm** `EditParameterCommand`, `RenameComponentCommand`, `SetLabelVisibilityCommand` all notify in
   both Execute and Undo (they should after the recent fix — just verify; if any doesn't, fix it).

**Layer 2 gate:** new command added and notifying in both directions; existing ones confirmed. Report.

---

## LAYER 3 — `ParameterEditorViewModel` (the reusable VM)

Spec: `parameter-editor.md` "View-model & data flow", "Which parameters are editable / shown".

1. **`ParameterEditorViewModel`** (in `ViewModels/`), constructed with the `SchematicViewModel` (for
   `EditModel`, `Execute`, `Selection`) or the pieces it needs. Exposes:
   - `IsEmptyState` (bool) + the empty-state message string.
   - `TypeDisplayName` (`ComponentTypeRegistry.DisplayName(symbol, portCount)`), editable `InstanceName`,
     `ShowTypeLabel`, `ShowInstanceName`.
   - `ObservableCollection<ParameterRowViewModel> Rows`.
   - A `SetTarget(EditableComponent?)` method (and/or a "bind to current selection" mode for the embedded
     host — see Layer 5).
2. **The Ground/empty guard (single point):** `SetTarget` enters empty state when the component is
   **null OR `Symbol == SymbolKind.Ground`**. (The embedded host additionally maps empty/multi-selection to
   `SetTarget(null)`.) When empty: `IsEmptyState = true`, `Rows` cleared, header hidden.
3. **`ParameterRowViewModel`** — wraps one `EditableParameter`:
   - `Name` (read-only), `Expression` (editable), `Unit` (editable via ComboBox), `ShowOnSchematic` (bool),
     `UnitOptions` (= `ComponentTypeRegistry.UnitOptions(param.Dimension)`).
   - Commit: `Expression`/`Unit` change → `Execute(new EditParameterCommand(EditModel, param, expr, unit))`;
     `ShowOnSchematic` toggle → `Execute(new SetParameterVisibilityCommand(EditModel, param, value))`. Use the
     VM's `Execute` (the `SchematicViewModel.Execute` that wraps in `DotRevalidationCommand`) so it's one path.
   - **No-change guard + idempotent commit:** a commit whose value equals the current value pushes **no**
     command (mirrors the inline editor). If practical, share the inline editor's commit helper /
     parse-expression-unit logic rather than duplicating; at minimum match its discipline (capture value,
     commit once, no-change = no-op).
4. **Hidden/structural params:** **omit `NumPorts`** from `Rows` entirely (v1 — port count is set via the
   inline `Z{N}P` type editor, which regenerates ports; the editor must not create a second path). A param
   with empty `Name` → skip defensively. Every other param in `Parameters` order becomes a row.
5. **Instance name + label flags** route through `RenameComponentCommand` / `SetLabelVisibilityCommand`
   (undoable). Blank instance name is rejected (don't commit an empty name).

**Layer 3 gate:** VM + row VM with the Ground/empty guard, NumPorts omission, command-stack commits, no-change
guard. Unit options come from the param's dimension. Firewall green. Report.

---

## LAYER 4 — `ParameterEditorView` (the reusable UserControl)

Spec: `parameter-editor.md` "Layout". Use the `frontend-design` skill for styling tokens (spacing, type,
colors) — read it before writing AXAML.

1. **Self-contained `UserControl`** bound to `ParameterEditorViewModel`. Structure:
   - **Empty state:** when `IsEmptyState`, show only a quiet centered muted message (match the existing
     `PropertiesView.axaml` placeholder styling). Everything else hidden.
   - **Header (fixed):** `TypeDisplayName` shown prominently + an editable Instance Name `TextBox`.
   - **Label-visibility row (fixed):** two checkboxes — "Show type label" / "Show instance name".
   - **Parameters list (scrollable):** a `ScrollViewer` over an `ItemsControl` bound to `Rows`. Each row uses
     a shared-width column layout (shared-size `Grid` columns) so Name / Value / Unit / Display align down the
     list: **Name** (read-only `TextBlock`) · **Value** (`TextBox` ↔ `Expression`) · **Unit** (**`ComboBox`**,
     `ItemsSource`=`UnitOptions`, `SelectedItem`↔`Unit`, closed/non-editable) · **Display on Schematic**
     (`CheckBox` ↔ `ShowOnSchematic`).
   - **Footer (fixed):** a **Help** button (opens placeholder local HTML for the component — same behavior
     §4.5 specifies; wire the click to open an HTML file/URL stub, real docs later). A **Close** button slot
     that the dialog host shows and the embedded host hides (bind its visibility to a `ShowClose` flag on the
     VM or a control property).
2. **Commit triggers:** `TextBox` commits on Enter + focus-loss (LostFocus); `ComboBox` and `CheckBox` commit
   on selection/toggle. Apply the idempotency discipline (no double-commit on Enter-then-blur) — the no-change
   guard in the row VM is the backstop, but don't fire two commits for one Enter.
3. **One control type per row** — every visible row's Unit cell is a ComboBox (a `None`-dimension param shows
   a disabled `["None"]` combo; `NumPorts` doesn't appear at all). Do **not** mix TextBox/ComboBox per row.

**Layer 4 gate:** the view renders the header/flags/scrollable rows/footer, empty state works, Unit is a
closed ComboBox from the dimension, columns align, styling matches `frontend-design`. Report.

---

## LAYER 5 — Wire into the two hosts (keep both deferred decisions swappable)

Spec: `parameter-editor.md` "Two decisions deferred" + "Wiring into the app". **Both host decisions
(coexistence layout, dialog modality) are owner-experiment choices — build them swappable, do not bake one
in.**

1. **Embedded (Properties region):** host `ParameterEditorView` in the Properties region (`PropertiesView` /
   `PropertiesTool`). The embedded VM **binds to the schematic's current selection**: subscribe to
   `Selection.Changed`; on change, if exactly one component is selected resolve it and `SetTarget(comp)` (the
   Ground guard inside handles Ground → empty), else `SetTarget(null)`. (The Properties region needs access to
   the active schematic's `SchematicViewModel` — wire via `PropertiesTool` / the workspace's active-document
   notion; if there's no current mechanism for "active schematic," add a minimal one and flag it.)
   - **Coexistence with the palette = a swappable choice.** Implement so palette vs. param-editor is a single
     switch point (e.g. a content-switch driven by "single non-Ground component selected?", OR a stack) that
     can be flipped without touching `ParameterEditorView`. **Default to content-switch** (editor when one
     non-Ground component selected, else the palette/placeholder) but structure it so stack is a small change.
     Note the chosen default in `src/Ui/CLAUDE.md`.
2. **Dialog (double-click component):** repoint the `ComponentDoubleTapped` handler in
   `SchematicView.axaml.cs` — instead of the current inline-first-param fallback, open a dialog window hosting
   `ParameterEditorView` bound to the double-clicked component (`ShowClose = true`). **Ground double-click →
   do not open** (the guard; the handler checks `Symbol == Ground` and returns). Keep the **click-a-label**
   inline single-param editing on the schematic unchanged — only the double-click-the-component-body gesture
   opens the dialog.
   - **Modality = a swappable choice.** Make modal-vs-non-modal a single flag at the dialog-open call (e.g.
     `ShowDialog` vs `Show`), not a structural assumption. **Default to non-modal** (lets the user see the
     schematic update live as they edit) but structure so modal is a one-line flip. Note the default in
     `src/Ui/CLAUDE.md`.
3. **Live sync:** because commits call `NotifyChanged()`, editing in either host re-renders the schematic; the
   embedded editor and the canvas stay in sync through the model (no view-to-view wiring). Verify a value
   edited in the dialog updates the on-schematic label live, and (non-modal) the embedded inspector too.

**Layer 5 gate:** both hosts work; coexistence + modality are single swappable choices with the defaults
above; Ground guarded in both; live sync confirmed. Report.

---

## Acceptance (whole feature) — from `parameter-editor.md` "Acceptance"
1. Selecting a single non-Ground component shows all its params in the Properties region; editing value/unit
   updates the schematic label live and is undoable; toggling Display-on-Schematic shows/hides that label live
   and is undoable; instance name + the two label-visibility flags edit and undo.
2. Double-clicking a non-Ground component opens the same editor as a dialog with a Help button.
3. **Ground:** selecting Ground → empty state; double-clicking Ground → opens nothing. Empty/multi selection →
   empty state.
4. ZNP shows its full `Z[i,j]` matrix as rows; `NumPorts` is **not** a row; port count still changes only via
   the inline `Z{N}P` type editor.
5. Unit is a **closed ComboBox** matching the param's dimension (`L` → `None/pH/nH/µH/mH/H`, etc.), existing
   unit pre-selected, change undoable; dimensionless/hidden params show no unit cell.
6. All edits via the command stack (no direct model mutation); list scrollable; columns align; `frontend-design`
   styling. Coexistence + modality swappable (defaults: content-switch, non-modal). Firewall green;
   `dotnet build`/`dotnet test` green; nothing in prior phases regresses.

## Guardrails
- **One reusable view + VM, two hosts** — neither host decision touches the view internals; both are flags.
- **Every edit undoable through the existing stack**; the new `SetParameterVisibilityCommand` notifies in both
  Execute and Undo, like every other mutation command (the standing rule).
- **Ground guard once at binding** (null/Ground/empty/multi → empty state), not scattered through rows.
- **Units keyed by dimension, closed list** — never per-`SymbolKind`, never free text (v1); one control type
  per row (ComboBox); `NumPorts` omitted, never a second port-count path.
- **No copy/apply buffer** — edits are immediate and undoable against the live `EditableComponent`, like the
  inline editor; share its commit helper / no-change discipline rather than duplicating.
- Sub-gate the five layers; report and stop between them; don't run the full suite into the output limit.
- Update `src/Ui/CLAUDE.md` (the one-view-two-hosts pattern; the chosen coexistence + modality defaults as
  swappable; units-by-dimension) and confirm `parameter-editor.md` matches what was built (note any deviation).

*Exit: a reusable ParameterEditorView shows/edits all of a component's parameters (value, unit via a
dimension-aware closed ComboBox, Display-on-Schematic) plus instance name and label visibility — hosted as the
Properties-region inspector and the double-click dialog, every edit undoable, Ground treated as no-selection,
with palette-coexistence and dialog-modality left as swappable choices for the owner to settle by experiment.*
