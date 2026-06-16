# Sonnet Brief — Parameter editor: user-addable instance parameters for "advanced" components

**Goal.** Let users **add and remove parameters** on an instance, in the Parameter-Properties editor, for
component types that need open-ended parameter sets — and **lead the user** by pre-populating the right parameter
**names** for the type. Targets:
- **V_nTone**: each `+` adds the next tone group **`Freq[n]` + `V[n]` + `Phase[n]`** (next index n). `−` removes
  the highest group. So `+` from a fresh V_nTone → `Freq[2]/V[2]/Phase[2]`, another `+` → `Freq[3]/V[3]/Phase[3]`.
- **P1Tone**: each `+` adds the next harmonic termination `Z[k]`, in the sequence **`Z[0]`, then `Z[2]`, `Z[3]`,
  …** (`Z[1]` is the always-present fundamental default; `Z[0]` is DC). `−` removes the highest declared band.
- **Tuner** (`Z[k]`/`G[k]`), **Z_Port** (`Z[i,j]`), **SDD** (`I[p,w]`): also user-extensible (single `+` adds the
  next sensible indexed slot for the type).

On **commit (Enter / close)**, the indexed parameters are **re-sorted into canonical order** (`Z[0], Z[1],
Z[2], …`; `Freq[1]/V[1]/Phase[1]`, `Freq[2]/V[2]/Phase[2]`, …) so the list and the on-schematic rendering read
cleanly. Today the editor has **no "Add Parameter" button** and the Name column is read-only — most components
have a fixed set, so it was never needed. Gate the capability so ordinary components (R/L/C/V/…) are unaffected.

This is its **own brief** (not folded into P1Tone): a reusable schematic-editor capability shared by several
types, and the undoable add/remove/rename commands **already exist** from the VAR work.

## What already exists (reuse — don't reinvent)
- `src/Ui/Commands/Schematic/VarParamCommands.cs`: `AddVarParameterCommand`, `RemoveVarParameterCommand`,
  `SetVarParamNameCommand`, `SetVarParametersCommand` — generic over `EditableComponent.Parameters`
  (`EditableParameter` has `Clone()` + mutable `Name`), call `_model.NotifyChanged()`. Mechanically generic; only
  names say "VAR".
- `src/Ui/ViewModels/ParameterEditorViewModel.cs`: builds `Rows` from `comp.Parameters` (skips `NumPorts`/blank);
  edits go through `_schematicVm.Execute(command)` (schematic undo). No add/remove.
- `src/Ui/Views/ParameterEditor/ParameterEditorView.axaml`: Row 2 = `ItemsControl` of rows (Name = read-only
  `TextBlock`); Row 3 footer = Help/Close (home for the Add button).

## Design

### 1. Generalize the VAR param commands (rename, keep VAR working)
Rename to neutral names; update VAR call sites:
- `AddVarParameterCommand` → `AddParameterCommand`, `RemoveVarParameterCommand` → `RemoveParameterCommand`,
  `SetVarParamNameCommand` → `SetParameterNameCommand`, `SetVarParametersCommand` → `SetParametersCommand`
  (the atomic replace-all — used for VAR multi-line **and** for the canonical re-sort below).
- Neutral `Description` strings. Behavior identical (clone-based undo, `NotifyChanged`).

### 2. Per-type **indexed-parameter template** (the new core)
Add a small declarative descriptor — `ComponentTypeRegistry.UserParamTemplate(SymbolKind)` returning null
(not extensible) or an `IndexedParamTemplate` describing how `+`/`−` and re-sort work for that type:

```csharp
public sealed record IndexedParamGroup(
    string[] NameFormats,   // e.g. ["Freq[{0}]","V[{0}]","Phase[{0}]"]  or  ["Z[{0}]"]
    string[] Units,         // per-format default unit, e.g. ["GHz","V","deg"] or ["Ω"]
    bool[]   ShowOnSchematic,// per-format default visibility
    int      FirstAddIndex, // first index a '+' creates that isn't already a fixed default
    int[]    SkipIndices);  // indices the '+' sequence skips (e.g. P1Tone skips 1: Z[1] is the fixed default)
```

Templates:
- **V_nTone (`ToneSource`):** group `["Freq[{0}]","V[{0}]","Phase[{0}]"]`, units `["GHz","V","deg"]`, show
  `[true,true,false]`, `FirstAddIndex = 2` (Freq[1]/V[1] are the default single-tone params), skip none. NOTE:
  V_nTone also has a `NumFreqs` param the factory reads — see §6.
- **P1Tone:** group `["Z[{0}]"]`, unit `["Ω"]`, show `[true]`, `FirstAddIndex = 0` with sequence ordering
  `0,2,3,4,…` (i.e. add `Z[0]` first, then skip `1`, then `2,3,…`) — encode as `FirstAddIndex=0, SkipIndices=[1]`
  and define the "next index" as: lowest non-negative index not already present and not in `SkipIndices`.
- **Tuner:** group `["Z[{0}]"]`, unit `["Ω"]`, `FirstAddIndex=2`, skip none (Z[1] is the declared fundamental).
- **Z_Port:** group `["Z[{0}]"]` is 2-D — special-case: next slot is the next `Z[i,j]` not present in row-major
  order up to the port count (keep it simple; if 2-D indexing is awkward, scope Z_Port to "add a blank named row"
  and flag — the headline asks are V_nTone + P1Tone).
- **SDD:** group `["I[{0},0]"]` (next port's current equation); `FirstAddIndex=1`.

"Next index" helper: `nextIndex = smallest i ≥ FirstAddIndex (P1Tone: smallest i ≥ 0) such that the group's
NameFormats[0].Format(i) is not already a parameter and i ∉ SkipIndices`.

### 3. ParameterEditorViewModel — add / remove-group / re-sort
- `public bool AllowsAddParameter => _target is not null &&
  ComponentTypeRegistry.UserParamTemplate(_target.Symbol) is not null;`
- **`AddParameterCommand`** (IRelayCommand): look up the template, compute `nextIndex`, build **one
  `EditableParameter` per `NameFormats` entry** (name = `fmt.Format(nextIndex)`, unit = template unit, blank
  expression, `ShowOnSchematic` per template), and add them as a **single undoable op** (use
  `SetParametersCommand` with `existing + newGroup`, or push the multiple `AddParameterCommand`s inside one
  undo-group if the stack supports grouping — simplest: `SetParametersCommand` replacing the whole list with the
  appended group). After commit, **re-sort** (below) is not needed on add (indices are appended in order), but a
  re-sort is harmless. Focus the first new row's expression box.
- **`RemoveTopGroupCommand`** (the `−` button): remove the **highest-index** group for the template (all
  `NameFormats` entries at that index) in one undoable op. (Per-row trash also allowed — see view — but `−`
  removes the whole top group so `Freq[3]/V[3]/Phase[3]` go together.)
- **Canonical re-sort on commit:** when the user commits a value (Enter) or closes the editor, reorder
  `comp.Parameters` into canonical order so the schematic renders nicely:
  1. fixed/leading params in their existing order (everything not matching the template's indexed pattern, e.g.
     `Pavl, Z, Freq, Phase` for P1Tone; `NumFreqs, Freq[1], V[1], Phase[1]` for V_nTone),
  2. then the indexed groups sorted by index ascending, and within an index by the template's `NameFormats`
     order (so `…, Z[0], Z[1], Z[2]` and `Freq[2],V[2],Phase[2], Freq[3],V[3],Phase[3]`).
  Implement as a pure `IReadOnlyList<EditableParameter> CanonicalSort(template, parameters)` (testable), applied
  via one `SetParametersCommand` **only when order actually changes** (avoid spurious undo entries). Trigger it
  from the existing Enter/commit path and on `Dispose`/close.
- **Editable Name** (for extensible types): allow free renaming too (commit via `SetParameterNameCommand`,
  reject empty/dup with inline `CrfWarningBrush`). The pre-populated names mean most users never need to type a
  name, but renaming stays available. Add `bool NameEditable` + `StagedName` to `ParameterRowViewModel`
  (mirror `StagedExpression`).

### 4. View (`ParameterEditorView.axaml`)
- **Footer (Row 3):** a `+` button and a `−` button (left of Help), both `IsVisible="{Binding
  AllowsAddParameter}"`. `+` → `AddParameterCommand`; `−` → `RemoveTopGroupCommand` (disable/hide `−` when there
  are no removable groups). Label them `+`/`−` with tooltips ("Add parameter", "Remove last") — compact, matching
  the footer idiom. (Keep an optional per-row trash for one-off removal, `IsVisible="{Binding NameEditable}"`.)
- **Name cell:** `TextBox` bound to `StagedName` with `IsReadOnly="{Binding !NameEditable}"` (ordinary components
  render exactly as today via the read-only path). LostFocus/Enter commit like the expression box.
- Don't disturb SharedSize groups / preview / display-checkbox for ordinary components.

### 5. Per-type semantics unchanged downstream
The factory/elaborator already parse `Z[k]`/`Freq[i]`/`V[i]`/`Phase[i]`/`Z[i,j]`/`I[p,w]` by name. A
user-added `Z[2]` (P1Tone) or `Freq[2]/V[2]` (V_nTone) flows through the existing
`ResolveP1ToneParameters`/`ResolveToneSourceParameters` exactly as a netlist-authored one. **Authoring only** —
no model-interpretation change.

### 6. V_nTone `NumFreqs` consistency (important)
`ToneSourceModel`/`ResolveToneSourceParameters` use `NumFreqs` (or `NumFreqsExpr`) to know how many tones to read
(`Freq[1..N]`). When the user adds/removes a tone group, **keep `NumFreqs` in sync** = the count of present
`Freq[i]` groups (or highest index). Update `NumFreqs` in the same undoable op as the add/remove (it's a hidden
param like `NumPorts`). Likewise confirm whether a single-tone `ToneSource` uses scalar `Freq` vs `Freq[1]` —
when the user adds `Freq[2]`, migrate the scalar `Freq`/`V` to `Freq[1]`/`V[1]` if that's what the multi-tone
factory path expects (check `ResolveToneSourceParameters`/`CreateToneSourceModel`; the registry note already
flags V vs Vac naming — verify and keep the factory keys exact). Flag if the single↔multi migration is more than
trivial.

## Tests
- **`CanonicalSort`** (pure): P1Tone params `[Pavl, Z, Z[2], Z[0]]` → `[Pavl, Z, Z[0], Z[2]]`; V_nTone
  `[…, Freq[3],V[3], Freq[2],V[2]]` → groups ordered 2 then 3.
- **`nextIndex`**: P1Tone with `Z[0]` present → next `+` gives `Z[2]` (skips 1); with none → `Z[0]`. V_nTone with
  `Freq[1]/V[1]` → next `+` → `Freq[2]/V[2]/Phase[2]`.
- **Add/Remove undo:** `+` adds a full group (one undo entry); `−` removes the top group; undo restores; `NumFreqs`
  tracked for V_nTone.
- **VM gate:** `AllowsAddParameter` true for P1Tone/ToneSource/Tuner/ZPort/SDD/Var, false for R/L/C/V/Term/Pin/
  Ground.
- Manual covers the view + on-schematic ordering.

## Gate
Build 0W/0E; tests green. Manual: select a V_nTone → `+` adds `Freq[2]/V[2]/Phase[2]` with names pre-filled;
another `+` → index 3; `−` removes index 3; enter values, close → params render in `Freq[1],V[1],…,Freq[2],V[2]`
order on the schematic; two-tone HB sees both tones. Select a P1Tone → `+` adds `Z[0]`, `+` → `Z[2]`, `+` →
`Z[3]`; close → `Z[0],Z[1],Z[2],Z[3]` order; harmonic terminations take effect. A resistor looks exactly as
before. Undo reverses an add.

## On completion
Note in `src/Ui/CLAUDE.md`: the Parameter editor offers type-aware `+`/`−` that add/remove **indexed parameter
groups** with pre-populated names (V_nTone → `Freq[n]/V[n]/Phase[n]`; P1Tone → `Z[k]` in `0,2,3,…` order; Tuner/
Z_Port/SDD analogous), driven by `ComponentTypeRegistry.UserParamTemplate`; params are canonically re-sorted on
commit for clean list + schematic rendering; `NumFreqs` stays in sync for V_nTone. Backed by the generalized
`AddParameterCommand`/`RemoveParameterCommand`/`SetParametersCommand`/`SetParameterNameCommand`. Authoring only —
per-type parameter semantics unchanged.
