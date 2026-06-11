# circuitRF — App Settings: tabs, launch prefs, global Copy policy, Color Theme UX + WireRouting

Sub-gated into independent layers; do them in order, gate each, report+STOP between. Firewall: the color model
(ColorTheme/ColorRole/Rgba) stays framework-free; Avalonia/Skia only in views + renderer projections.
`dotnet build`/`dotnet test` green each layer.

## Read first (verified on disk)
- Theming/AppPreferences.cs — AppPreferences + AppPreferencesIo.Load/Save. **Save clobbers fields — Layer 0.**
- Theming/ColorRole.cs — role string constants + All list (new role here).
- Theming/ColorTheme.cs — ColorTheme.BuiltIn light+dark role maps (new role defaults here).
- Renderers/SchematicRenderTheme.cs — L2 roles→SKColor; has un-themed green `WirePreview` token = the
  in-progress wire color.
- Renderers/SchematicRenderer.cs — Draw(...) takes `SchematicRenderTheme`, `useTransparentBackground`,
  `excludeGrid`; the in-progress/rubber wire draw is where WirePreview is used.
- Clipboard/SchematicClipboard.cs — `CopyAsync(...)`; the three render helpers (TryRenderToPdf/Svg/
  AvaloniaImage) ALL hardcode `SchematicRenderTheme.Light` and take `useTransparentBackground`/`excludeGrid`
  call-site defaults. This is the only copy path in circuitRF today; symbol-glyph + splotRF-plot copy are
  future.
- Views/Dialogs/SettingsView.axaml + .axaml.cs — the dialog (single-panel today; becomes tabbed).
- App.axaml.cs — OnFrameworkInitializationCompleted (launch). Reads as garbled via the text tool (encoding) —
  open in the editor directly.

---

## LAYER 0 — fix AppPreferencesIo.Save clobber (PREREQUISITE, do FIRST)
`AppPreferencesIo.Save(prefs)` serializes a WHOLE `AppPreferences` and overwrites the file. Callers already do
`Save(new AppPreferences { ActiveThemeName = name })` — which ERASES `RecentWorkspaces` and `RecentlyPlaced`.
Adding more fields multiplies this. Add a load-merge-save helper:
`AppPreferencesIo.Update(Action<AppPreferences> mutate)` = Load → mutate → Save. Convert ALL partial-save call
sites (SettingsView OnSaveThemeClick/OnCloseClick, recent-workspaces + recently-placed writers) to `Update(...)`.
**Gate:** set a theme, confirm recent_workspaces/recently_placed survive in preferences.json. Report.

---

## LAYER 1 — New color role: WireRouting (self-contained; lands first for quick win)
Add a themable `Schematic.WireRouting` color used by the schematic renderer while the user is ACTIVELY
drawing/routing a wire (the in-progress wire), seeded to the current Wire color so the default look is
unchanged. No Settings-dialog dependency — pure color-model + renderer.
1. **ColorRole.cs:** add `public const string SchematicWireRouting = "Schematic.WireRouting";` and include it in
   `All` IMMEDIATELY AFTER `SchematicWire` (so UI lists it under Wire).
2. **ColorTheme.BuiltIn:** add `[ColorRole.SchematicWireRouting]` to BOTH light and dark maps = SAME value as
   `SchematicWire` (light `(164,63,129)`, dark `(214,122,178)`). Seed explicitly so it appears in the editor and
   saved themes.
3. **SchematicRenderTheme.cs:** add a `WireRouting` SKColor token; in `FromTheme`, `WireRouting =
   SK(ColorRole.SchematicWireRouting)`; add it to the `WithAccent` copy. The existing un-themed green
   `WirePreview` token IS the in-progress wire color — REPLACE its use with `WireRouting`; if `WirePreview`
   becomes unused, remove it.
4. **SchematicRenderer.cs:** the in-progress/rubber wire being drawn must render with `WireRouting` (not the
   committed `Wire`, not hardcoded green). Repoint that draw call.
5. **SettingsView RoleLabels:** add `[ColorRole.SchematicWireRouting] = "Wire Routing"` so the role list shows a
   friendly name directly under "Wire" (via step 1's ordering).
6. **Default.ccolor asset:** if a `Default.ccolor` ships in /Assets/Color, add WireRouting there too (= Wire);
   bump its format_version if it has one (alpha: reject-on-mismatch, no migration).
**Gate:** role list shows "Wire Routing" directly under "Wire", defaulting to the Wire color; while drawing a
wire the in-progress wire uses WireRouting; changing WireRouting recolors the in-progress wire ONLY (committed
wires still use Wire). Report.

---

## LAYER 2 — Global Clipboard Render Policy (ONE setting, ALL copy commands)
[USER DECISION] This is NOT a per-call schematic param. It is ONE app-wide policy that EVERY copy-to-clipboard
path reads — schematic today, symbol glyphs and splotRF plots in the future. Quick-access, single source of
truth.
1. **Define the policy (framework-free, in Theming or a small Clipboard/ClipboardRenderPolicy.cs):**
   - `enum CopyColorMode { FollowSystem, ForceLight, ForceDark }` (default FollowSystem).
   - `bool TransparentBackground` (default true).
   - Persist both on `AppPreferences` (`CopyColorMode`, `CopyTransparentBackground`) via the Layer-0 `Update`
     helper.
   - Provide a single resolver, e.g. `ClipboardRenderPolicy.Current` (reads prefs) exposing
     `ColorVariant ResolveVariant()` (FollowSystem → the app's current resolved variant via ThemeService;
     Force* → that variant) and `bool TransparentBackground`.
2. **Wire EVERY copy path through it.** `SchematicClipboard`'s three render helpers currently hardcode
   `SchematicRenderTheme.Light` and take call-site `useTransparentBackground`/`excludeGrid` — change them to
   resolve from the policy: pick the `ColorVariant` (→ `SchematicRenderTheme.FromTheme(ThemeService.Active,
   variant)` or the Light/Dark static) and pass `useTransparentBackground = policy.TransparentBackground`.
   Applies to PDF + SVG + PNG uniformly. (Leave `excludeGrid` as-is for now unless you want it in the policy too
   — keep its current default; do NOT add a UI for it this pass.)
3. **Future copy paths** (symbol-glyph copy, splotRF-plot copy) must read the SAME `ClipboardRenderPolicy` — note
   this in the policy's XML doc and in color-themes.md so they're wired consistently when built. (splotRF lives
   in a sibling repo; only document the contract here — do not edit splotRF.)
4. **Quick access:** beyond the Settings UI (Layer 4), expose the two policy controls where a user copies — e.g.
   a small affordance in the relevant context/menu — OR at minimum ensure the Settings control is the canonical
   editor. v1: the Settings General-tab control (Layer 4) is the required surface; a toolbar/menu quick-toggle
   is nice-to-have — implement if cheap, else note as a follow-up.
**Gate:** Force Dark + Transparent → copy a schematic selection → paste into an external app shows a dark,
transparent render; Force Light + opaque → light with solid bg; FollowSystem tracks the app theme. The render
path reads the policy, not a hardcoded Light. Report.

---

## LAYER 3 — Settings dialog becomes tabbed
TabControl: **Tab 1 "General"** (new, first; Layers 4–6 fill it) and **Tab 2 "Color Theme"** (move the ENTIRE
existing theme editor in — combo row + role list + RGBA editor + Save Theme — behavior unchanged). Shared
Cancel/Close/Revert footer OUTSIDE the TabControl so it applies globally.
**Gate:** Settings opens with two tabs; Color Theme tab works exactly as before (edit/save/revert/live preview).
Report.

---

## LAYER 4 — General tab: launch prefs + Copy-policy controls
Three controls on the General tab, all persisted via the Layer-0 `Update` helper.
1. **On launch (action):** `enum LaunchAction { NewSchematic(default), NewWorkspace, OpenWorkspace,
   NewDataDisplay, NewSymbol }` → ComboBox. Startup (App.axaml.cs), AFTER the main window is up, IF no
   command-line file args: NewSchematic → new scratch schematic (no other tabs); NewWorkspace → New Workspace
   dialog; OpenWorkspace → open folder/.cws picker; NewDataDisplay → new data-display doc (fall back to
   NewSchematic + note if the doc type doesn't exist yet); NewSymbol → New Symbol flow. If launched WITH file
   args → process those and SKIP the launch-action entirely.
2. **On launch (focus pane):** `enum LaunchPane { ProjectTree, Palette(default) }` → ComboBox. Startup: after the
   layout is up, set the left projectTree ToolDock's ActiveDockable — Palette → PaletteTool, ProjectTree →
   ProjectTreeTool (CircuitRfDockFactory exposes both + the dock).
3. **Copy / Export policy (from Layer 2):** "Copy color:" ComboBox {Follow System, Force Light, Force Dark};
   "Transparent background" checkbox. Both bound to the persisted `CopyColorMode`/`CopyTransparentBackground`.
   Label the group "Copy & Export" (applies to all copy commands).
**Gate:** each launch option relaunches correctly (no stray tabs; file-arg launch skips the action); default
launch shows the Palette tab active, ProjectTree option shows the tree tab; the two copy controls drive the
Layer-2 policy. Report (note any launch option that fell back, e.g. data-display if absent).

---

## LAYER 5 — Color Theme tab: RGBA value as integer edit box linked to slider
Replace the read-only `LabelR/G/B/A` TextBlocks with integer-only edit boxes (~40px, no spinner per HIG) two-way
linked to each slider (0–255). Editing the box moves the slider + live preview; moving the slider updates the
box. Reuse the existing `_updating` guard against recursion. Invalid/out-of-range input reverts to the last
valid value. Keep the Hex box in sync (it already is via ApplyCurrentSliders).
**Gate:** type 200 into R → slider + preview jump to 200; drag G slider → G box tracks; invalid text reverts.
Report.

---

## LAYER 6 — Color Theme tab: "Custom" shows on appear when colors differ from preset [BUG]
`PopulateThemeCombo` selects the combo by `ThemeService.Active.Name`, so a modified-but-still-named theme shows
the preset name, not "Custom". Add `bool DiffersFromPreset(ColorTheme active)` that resolves the named preset via
ThemeResolver and compares ALL `ColorRole.All` for BOTH variants; any mismatch → select "Custom" (insert into the
combo if absent). Call it in OnLoaded/PopulateThemeCombo.
**Gate:** edit a color, close+reopen Settings (without saving a named theme) → combo shows "Custom" on appear.
Report.

---

## LAYER 7 — Color Theme tab: tighter RGBA rows
Reduce vertical rhythm of the R/G/B/A grid: RowSpacing 7 → ~3–4, trim per-row margins, optionally a slightly
shorter preview swatch. Keep legible/aligned; don't crowd the slider thumbs. Pure layout.
**Gate:** rows visibly tighter, still legible and aligned. Report.

---

## LAYER 8 — Color Theme tab: double-click role name/swatch → color picker
Double-tap a role's name or color square in the RoleList → open the color picker seeded with the current color →
on OK apply via the existing `ApplyRgbaToActiveRole` path (forks to Custom + live preview + updates swatch/
sliders). Avalonia 12: VERIFY the available control before coding — use `ColorPicker` (Avalonia.Controls
.ColorPicker) if present, else host a `ColorView` in a small modal dialog seeded with the current color. Report
which you used.
**Gate:** double-click a role's name or swatch → picker opens at the current color → pick a new color → role
updates live and combo forks to Custom. Report which control was used.

---

## Acceptance
AppPreferences no longer clobbers fields; WireRouting role (seeded to Wire) themes the in-progress wire; a single
global ClipboardRenderPolicy (copy color mode + transparent bg) drives ALL copy paths (schematic now; documented
contract for symbol/plot copy later); Settings is tabbed (General + Color Theme); General tab carries
launch-action (with command-line-arg override), launch-pane (default Palette), and the copy-policy controls;
Color Theme tab gains linked integer boxes, correct Custom-on-appear, tighter rows, and double-click color
picking. Firewall green; build/test green; no regression to theme save/revert/live-preview or clipboard copy.

## Guardrails
- **Layer 0 first** — fix the AppPreferences clobber before adding fields, or new prefs erase each other.
- Color model framework-free (ColorTheme/ColorRole/Rgba — no Avalonia/Skia); views + render projection hold the
  framework types. ClipboardRenderPolicy is framework-free data + a resolver.
- WireRouting default = Wire's value (light + dark) so the out-of-box look is unchanged; themes ONLY the
  in-progress wire.
- The copy policy is ONE global setting that every copy path reads — never per-call duplicated literals; the
  render path must read the policy, not a hardcoded Light. Document the contract for future symbol/plot copy.
- Launch-action is SKIPPED when file args are passed on the command line.
- Verify the Avalonia 12 color-picker control before coding Layer 8; report which.
- Sub-gate; report+STOP between layers; don't batch.
- Update docs: docs/design/color-themes.md (WireRouting role + the global ClipboardRenderPolicy contract + the
  launch prefs) and src/Ui/CLAUDE.md (AppPreferences.Update merge pattern; ClipboardRenderPolicy single-source;
  launch-action/pane wiring).

*Exit: AppPreferences saves merge (no clobber); a themable WireRouting color drives the in-progress wire; one
global clipboard render policy governs every copy command; Settings is tabbed with launch + copy-policy controls
on a new General tab; and the Color Theme tab gains linked integer boxes, Custom-on-appear, tighter rows, and
double-click color picking.*
