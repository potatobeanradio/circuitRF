# circuitRF — App Settings follow-up: launch action + Welcome + color-picker theme + hex field (Claude Code / Sonnet)

Four issues from the App Settings work. Two are real bugs that the prior pass reported as done but don't work
(launch action not respected; color picker shows no control), one is a new feature (Welcome launch action +
make it the default), one is a small correctness fix on the hex field. Sub-gated; report+STOP between layers.
Firewall green; `dotnet build`/`dotnet test` green each layer.

## Read first (verified on disk)
- Theming/AppPreferences.cs — `enum LaunchAction { NewSchematic, NewWorkspace, OpenWorkspace, NewDataDisplay,
  NewSymbol }`. Add `Welcome`; make it the default.
- ViewModels/WorkspaceViewModel.cs — constructor calls `NewScratchSchematic()` UNCONDITIONALLY (last line);
  `ExecuteLaunchActionAsync(LaunchAction)` only handles NewWorkspace/OpenWorkspace; `ApplyLaunchPane`.
- ViewModels/Dock/CircuitRfDockFactory.cs — `CreateLayout()` always builds a `StubDocument("Welcome",
  StubKind.Welcome)` as the initial document; `RemoveWelcomeStub()` exists.
- App.axaml.cs — `ApplyLaunchSettings(vm)` calls ApplyLaunchPane + ExecuteLaunchActionAsync (deferred,
  Background priority, after the window shows).
- App.axaml — `Application.Styles` includes FluentTheme + DockFluentTheme; **NO ColorPicker theme include**
  (the bug).
- Views/Dialogs/ColorPickerDialog.axaml(.cs) — hosts `cp:ColorView` from assembly
  Avalonia.Controls.ColorPicker; package IS referenced (12.0.3) but its theme isn't loaded.
- Views/Dialogs/SettingsView.axaml(.cs) — `HexBox` is an editable input; `OnHexKeyDown` parses on Return but
  does NOT set `e.Handled`, so Return bubbles to the default button and closes Settings.

---

## LAYER 1 — Launch action: actually respected; Welcome added as default
**Root cause:** the constructor unconditionally opens a scratch schematic (`NewScratchSchematic()` last line),
AND the layout always starts with a Welcome stub, AND `ExecuteLaunchActionAsync` only implements
NewWorkspace/OpenWorkspace (NewSchematic is a no-op "already open"; NewDataDisplay/NewSymbol are MISSING from
the switch). So the saved setting is masked by the constructor and three of five actions do nothing.

Restructure so the launch action OWNS what appears, and Welcome is shown ONLY for the Welcome action:
1. **AppPreferences.cs:** add `Welcome` to `LaunchAction` (put it FIRST:
   `enum LaunchAction { Welcome, NewSchematic, NewWorkspace, OpenWorkspace, NewDataDisplay, NewSymbol }`).
   Change every default fallback from `LaunchAction.NewSchematic` to `LaunchAction.Welcome` (App.axaml.cs
   `ApplyLaunchSettings`, SettingsView `LoadGeneralPrefs`).
2. **WorkspaceViewModel constructor:** REMOVE the unconditional `NewScratchSchematic();` call (and its
   `Messages.Info("circuitRF ready.")` can stay or move). The layout already contains the Welcome stub from
   `CreateLayout()`, so the app still lands on Welcome by default — now the launch action decides what to do
   with it.
3. **`ExecuteLaunchActionAsync`:** handle ALL actions; remove the Welcome stub for every action EXCEPT Welcome
   (so e.g. NewSchematic does not leave a stray Welcome tab):
   ```
   public async Task ExecuteLaunchActionAsync(LaunchAction action)
   {
       switch (action)
       {
           case LaunchAction.Welcome:
               // Leave the Welcome stub showing; add nothing.
               break;
           case LaunchAction.NewSchematic:
               _factory.RemoveWelcomeStub();
               NewScratchSchematic();
               break;
           case LaunchAction.NewWorkspace:
               _factory.RemoveWelcomeStub();
               await NewWorkspace(null);
               break;
           case LaunchAction.OpenWorkspace:
               _factory.RemoveWelcomeStub();
               await OpenWorkspace(null);
               break;
           case LaunchAction.NewSymbol:
               _factory.RemoveWelcomeStub();
               // Open a new blank editable symbol editor (no file yet) — mirror NewSymbolAsync's
               // editor-open path but without requiring a cell. If a no-cell New Symbol isn't
               // supported, fall back to Welcome and Messages.Info a note.
               OpenBlankSymbolEditor();   // add a small helper, or document the fallback
               break;
           case LaunchAction.NewDataDisplay:
               _factory.RemoveWelcomeStub();
               // If a data-display document type exists, open one; else fall back to NewSchematic
               // (or Welcome) and Messages.Info that data-display isn't available yet.
               OpenNewDataDisplayOrFallback();
               break;
       }
   }
   ```
   - If NewWorkspace/OpenWorkspace are CANCELLED by the user (dialog dismissed), the DocumentDock is now empty
     (Welcome was removed). Re-add a Welcome stub in that case so the app isn't blank — either re-insert the
     stub or skip the removal until the dialog returns success. Cleanest: only `RemoveWelcomeStub()` AFTER a
     successful NewWorkspace/OpenWorkspace; if cancelled, leave Welcome showing.
4. **Command-line file args still override** (already gated in App.axaml.cs — `startupPaths.Length > 0` skips
   ApplyLaunchSettings). Keep that.
**Gate:** set each launch action, relaunch (no file args): Welcome → only the Welcome screen; NewSchematic →
one scratch schematic, NO Welcome tab; NewWorkspace → New Workspace dialog, no stray tabs (Welcome remains if
cancelled); OpenWorkspace → folder picker; NewSymbol → blank symbol editor (or documented fallback);
NewDataDisplay → data-display (or documented fallback). Launch with a file arg → action skipped. Report each.

---

## LAYER 2 — Welcome in the Settings combo
SettingsView General tab must list Welcome and round-trip it.
- `LoadGeneralPrefs`: prepend "Welcome" to the LaunchActionCombo items so indices match the enum order
  (`Welcome, New Schematic, New Workspace, Open Workspace, New Data Display, New Symbol`).
  `SelectedIndex = (int)(prefs.LaunchAction ?? LaunchAction.Welcome)`.
- `OnLaunchSettingChanged` already casts SelectedIndex → LaunchAction; with Welcome first, the cast stays
  correct.
**Gate:** open Settings → On-launch shows "Welcome" selected by default; pick each option, reopen Settings →
the choice persists; relaunch honors it (ties to Layer 1). Report.

---

## LAYER 3 — Color picker shows no control [BUG: missing theme include]
**Root cause:** `Avalonia.Controls.ColorPicker` (12.0.3) IS referenced, and ColorPickerDialog hosts
`cp:ColorView` correctly — but App.axaml's `Application.Styles` never includes the ColorPicker control theme,
so `ColorView` instantiates with no template and renders blank. (Same class as the Dock HostWindow styling
issue.)
- Add the ColorPicker Fluent theme include to App.axaml `Application.Styles` (after FluentTheme, before/after
  DockFluentTheme is fine):
  `<StyleInclude Source="avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.axaml"/>`
- **VERIFY the exact resource path against the installed 12.0.3 package before building** (it may be
  `.../Themes/Fluent/Fluent.axaml` or a single `ColorPickerFluentTheme` entry point). If the path differs,
  use the one the package actually ships. Report which path worked.
**Gate:** double-click a role name/swatch in Color Theme → the dialog opens AND shows a full color picker
(spectrum + sliders + alpha) seeded to the current color → OK applies it (live preview + fork to Custom);
Cancel discards. Report the working StyleInclude path.

---

## LAYER 4 — Hex field: stop Return from closing Settings (+ your input/output call)
`HexBox` is currently an editable INPUT (type a hex code, Enter to apply). Bug: `OnHexKeyDown` parses on Return
but does NOT set `e.Handled = true`, so Return ALSO triggers the window default button → Settings closes.
**Decision (yours):** hex is a useful editable input (standard in color pickers), so KEEP it editable and fix
the Return bug — UNLESS you'd rather it be output-only.
- **Keep as input (recommended):** in `OnHexKeyDown`, set `e.Handled = true` after handling Return (mirror the
  RGBA boxes' `OnRgbaBoxKeyDown`, which already does this). Also handle Escape → revert + `e.Handled = true`.
- **OR make it output-only (if you prefer):** replace `HexBox` (TextBox) with a `SelectableTextBlock` showing
  the hex; drop `OnHexKeyDown`/`OnHexLostFocus`/`ParseAndApplyHex`; keep updating its `.Text` in
  `SetSlidersFromRgba`/`ApplyCurrentSliders`. (Selectable so users can copy the value.)
- Default to the first (keep editable + handle Return) unless told otherwise.
**Gate:** focus the hex box, type a value, press Return → the color applies and Settings STAYS OPEN; Escape
reverts and stays open. (If output-only chosen: hex shows current color, is selectable/copyable, no edit, no
close-on-Return.) Report which option was implemented.

---

## Acceptance
Launch action is respected for all six options (Welcome default); Welcome shows ONLY for the Welcome action (no
stray Welcome tab for NewSchematic etc.); command-line file args still override; the color picker dialog shows a
working picker; the hex field no longer closes Settings on Return. Firewall green; build/test green; no
regression to theme editing/live-preview/save or the General-tab copy/launch-pane controls.

## Guardrails
- The launch action OWNS the initial document; the constructor no longer force-opens a scratch schematic.
- Welcome stub is removed for every action except Welcome; if NewWorkspace/OpenWorkspace is cancelled, leave
  Welcome showing (don't leave the dock empty).
- VERIFY the ColorPicker theme StyleInclude path against the installed 12.0.3 package; report it.
- Hex: handle Return (e.Handled = true) so it never reaches the default button — same pattern the RGBA boxes
  already use.
- Sub-gate; report+STOP between layers.
- Update docs/design/color-themes.md (ColorPicker theme include; hex field behavior) and the launch-action note
  in src/Ui/CLAUDE.md (Welcome default; action owns the initial document).

*Exit: launch action works for all six options with Welcome as the default shown only for Welcome; the color
picker renders; the hex field applies on Return without closing Settings.*
