# Analysis Authoring — Step 5: copy/paste + templates + 2 HIG fixes (Claude Code / Sonnet)

The reuse layer (analysis setups are long & painful): **copy/paste** analyses between schematics (clipboard)
and named **multi-analysis templates** (`.canl`) with a first-class **Save as Template** UX — all backed by
the **step-2 shared serializer** (same bytes). Plus **two HIG fixes** the owner flagged: **(a)** TextBox
content vertically centered (global), **(b)** double-clicking an analysis row opens its **edit dialog**. Read
`analysis-authoring.md` §5 first. Sub-gated; **report and stop between every layer.** Firewall green.

> Read first: `docs/design/analysis-authoring.md` §5 (§5.1 dangling-reference rule, §5.2 copy/paste, §5.3
> templates + Save-as-Template UX, §5.4 one serialization). Context code: **the step-2 shared analysis
> serializer** (the single encoder for `.csch`/clipboard/`.canl` — reuse it; do NOT write a second),
> `src/Ui/ViewModels/AnalysesListViewModel.cs` + `AnalysisRowViewModel.cs` (the list — add copy/paste/
> template commands + double-click-edit), `src/Ui/Views/Dialogs/AnalysisEditorDialog.axaml(.cs)` (the
> step-4 edit dialog double-click reopens), `src/Ui/Views/Dialogs/SetupAnalysesDialog.axaml` +
> `InputNameDialog.axaml`/`NewWorkspaceDialog.axaml` (HIG dialog patterns for the Save-as-Template dialog),
> `src/Ui/Theming/AppPreferences.cs` (user-data dir family for the templates dir; the `.ccolor` theme
> resolution chain to mirror), `src/Ui/ViewModels/ParameterRowViewModel.cs` (the "≈"/unresolved-hint pattern
> for §5.1 surfacing), the app-wide TextBox style location (Styles/Themes axaml). Design docs win on conflict.

## The spine
- **One serialization (§5.4)** — copy/paste payload AND `.canl` template AND `.csch` are the **same** step-2
  encoder bytes. Reuse it; never a second encoder.
- **Faithful + surfaced (§5.1)** — paste/insert appends verbatim, then surfaces unresolved VAR/instance refs
  via the existing "≈ unknown" hint; never auto-rewrite or auto-drop. Name-collision resolution on append.
- **Multi-select / whole-setup** — copy selected OR all; templates are multi-analysis bundles.
- **Save-as-Template is first-class UX** (§5.3) — a button + a proper dialog (name + description + preview
  list + collision guard).
- **Scope fence (step 5):** the 2 HIG fixes + copy/paste + templates. NO extraction/run wiring (step 6), NO
  measurements builder.

---

## LAYER 1 — two HIG fixes

1. **TextBox vertical centering (global):** add/adjust an app-wide `TextBox` style so content is
   **vertically centered** within the box (`VerticalContentAlignment="Center"`) — the HIG-standard. Apply at
   the theme/Styles level so it covers all TextBoxes (analysis fields, dialogs, parameter editor, name
   dialogs), not per-control. Verify a few dense forms (AnalysisEditorDialog, NewWorkspaceDialog) look right.
2. **Double-click an analysis row → edit:** in the Analyses list, double-clicking an `AnalysisRowViewModel`
   opens the **step-4 `AnalysisEditorDialog`** for that analysis (the same path as the Edit/pencil action).
   Wire it in both hosts (dock panel + `SetupAnalysesDialog` modal).

**Layer 1 gate:** TextBox content is vertically centered app-wide (spot-checked in 2–3 dialogs); double-
clicking an analysis row opens its edit dialog (dock + modal). Report.

---

## LAYER 2 — copy / paste (clipboard)

1. **Copy (⌘C/Ctrl+C)** in the Analyses list → serialize the **selected** analyses (multi-select) via the
   step-2 shared serializer to the clipboard (a circuitRF-analyses clipboard format/text). A **"Copy All"**
   command copies the whole schematic's analyses.
2. **Paste (⌘V/Ctrl+V)** into another schematic's list → deserialize via the same serializer → **append** to
   the model's analyses, with **name-collision resolution** (`SP1` → `SP1 copy`/next free), as **one undoable
   action**; then run the **§5.1 unresolved-reference surfacing** (evaluate against the destination's VARs;
   unresolved → the quiet "≈ unknown: f0" hint, no auto-fix).
3. Works in dock + modal hosts (same VM); clipboard guards against non-analysis payloads (paste of unrelated
   clipboard content is a no-op/clear message).

**Layer 2 gate:** copy selected (and Copy All) → switch schematic → paste appends with collision-resolved
names; a pasted analysis referencing a VAR absent in the destination shows the unresolved hint (not auto-
fixed, not dropped); paste of non-analysis clipboard content is a safe no-op. Report.

---

## LAYER 3 — templates (`.canl`): Save as Template + Insert from Template

1. **`.canl` format:** a named multi-analysis bundle = `{ name, description?, analyses+measurements }` using
   the **step-2 serializer** for the analyses payload. Stored in the **user templates dir** (AppPreferences
   user-data family); resolution chain workspace → user → bundled (mirror `.ccolor` themes).
2. **Save as Template (the UX, §5.3):** a **"Save as Template…"** button in the Analyses panel (and the
   `SetupAnalysesDialog`). It targets the **current selection**, or **all** when nothing's selected (label
   reflects which). Opens a **Save-as-Template dialog** (mirror `InputNameDialog` + a description field):
   - **Template name** (validated via `NameValidator`; file = `<name>.canl`),
   - optional **Description** (shown in the picker),
   - a **preview list** of exactly which analyses will be saved,
   - collision guard (overwrite-or-rename if `<name>.canl` exists; don't clobber),
   - **Save** default (centered) / **Cancel** (Esc).
   On save: **atomic write** (temp+rename) to the templates dir; report the path via Messages (clickable).
3. **Insert from Template:** an **"Insert from Template…"** item in the Add (＋) menu → a **picker** (name +
   description, from the resolution chain) → selecting one **appends** its bundle (name-collision resolution +
   §5.1 surfacing, same as paste). Picker offers minimal **Manage** (rename/delete a `.canl`).

**Layer 3 gate:** Save-as-Template (selected vs all) opens the dialog with the preview list, validates the
name, guards collision, writes `<name>.canl` atomically, reports the path; Insert-from-Template lists
templates and appends a bundle (collision-resolved + unresolved-ref surfaced); a template round-trips its
analyses (same serializer as `.csch`). Report.

## Acceptance (step 5)
1. **HIG:** TextBox content vertically centered app-wide; double-click an analysis row opens its edit dialog.
2. **Copy/paste:** multi-select + Copy All; paste appends (collision-resolved, undoable) with §5.1 unresolved-
   reference surfacing; non-analysis paste is safe.
3. **Templates:** `.canl` multi-analysis bundles via the step-2 serializer in the user templates dir;
   Save-as-Template button + dialog (name/description/preview/collision/atomic/report); Insert-from-Template
   picker (+ minimal Manage), append with surfacing.
4. All three reuse the **one** step-2 serializer (§5.4 — no second encoder).
5. `dotnet build`/`dotnet test` green; firewall green; **no extraction/run wiring (step 6), no measurements
   builder**; nothing else regresses.

## Guardrails
- **One serializer (§5.4)** — clipboard + `.canl` + `.csch` share the step-2 encoder; never a second.
- **Faithful + surfaced (§5.1)** — append verbatim + name-collision resolution; surface unresolved VAR/
   instance refs via the existing hint; no auto-rewrite/drop.
- **Save-as-Template UX** — selection-or-all, name+description+preview+collision-guard+atomic+report.
- **Templates dir + resolution chain** mirror `.ccolor` themes.
- **HIG TextBox centering global** (theme-level, not per-control).
- **Scope fence:** HIG fixes + copy/paste + templates only — no run wiring, no measurements builder.
- Sub-gate the three layers; report and stop between each.
- Update `analysis-authoring.md` §7 status (step 5 done) and `src/Ui/CLAUDE.md` (copy/paste + `.canl`
   templates reuse the one analysis serializer; TextBox vertical-center global; double-click-edit).

*Exit: analysis setups are reusable — copy/paste between schematics and named `.canl` template bundles (Save
as Template / Insert from Template), all on the one shared serialization, faithful with unresolved refs
surfaced — plus the two HIG fixes; only extraction/run wiring (step 6) remains to make authored analyses
actually simulate.*
