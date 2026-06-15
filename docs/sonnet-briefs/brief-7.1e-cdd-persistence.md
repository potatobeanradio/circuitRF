# Sonnet Brief — Phase 7.1e: `.cdd` layout persistence (Save / Open Display)

**Design:** `docs/design/data-display.md` §1/§3 (7.1e) · `docs/design/project-file-formats.md` (`.cdd`). **Scope:
wire the Save Display / Open Display file dialogs for the `.cdd` format, add a `format_version`, register the
format, and verify the viewport (tab selection + zoom + pan) round-trips.** Do **not** rebuild the
serialization — it is already ported and working.

## What already exists (don't re-implement — reuse)
The persistence engine is fully ported and handles the owner's key requirement already:
- `DisplayWindowViewModel.SaveAllAsync(path, l,t,w,h)` — serializes every tab via
  `DataDisplay.BuildTabConfig(...)`, writes `ActiveTabIndex` + window geometry, `File.WriteAllTextAsync`.
- `DisplayWindowViewModel.LoadAllAsync(path, jsonStream=null)` — deserializes, rebuilds tabs via
  `DataDisplay.LoadFromTabConfigAsync(...)`, restores `ActiveTabIndex` (clamped).
- `DataDisplayViewModel.LoadFromTabConfigAsync` — restores `_zoomLevel`/`_viewOffsetX`/`_viewOffsetY`
  (+ `PropagateViewProperties()`), and per plot `plot.RestoreAxesFromConfig(...)` when `pc.Axes` is present
  (only `Autoscale()` when it's null). **No FitAll/autoscale runs on load** — the restored viewport stands.
- The `SaveDataDisplay` / `SaveDataDisplayAs` / `OpenDataDisplay` commands exist on `DisplayWindowViewModel`
  but invoke **injected actions that were never wired** (`_saveDataDisplayAsAction`, `_openDataDisplayAction`,
  `_getWindowGeometryAction`) — that's the main gap. Setters already exist:
  `SetSaveDataDisplayAsAction`, `SetOpenDataDisplayAction`, `SetGetWindowGeometryAction`.

So tab selection, canvas zoom/pan, and per-plot axes zoom already round-trip. 7.1e just exposes it + versions it.

## 1. Add `format_version` (the one model change)
`DataDisplayConfig` (`src/Ui/DataDisplay/Models/DataDisplayConfig.cs`) currently has **no version field** —
the alpha policy + `project-file-formats.md` require one (written, reject-on-mismatch, no migration).
- Add a field, e.g. `public int FormatVersion { get; set; } = 1;` with the **on-disk key matching the other
  circuitRF formats** — check what `WorkspacePersistence` (`src/Ui/Schematic/`) uses for its `.cws` version and
  mirror it (likely `[JsonPropertyName("format_version")]`). Add a `const int CurrentFormatVersion = 1;`.
- `SaveAllAsync`: set `config.FormatVersion = DataDisplayConfig.CurrentFormatVersion` before serializing.
- `LoadAllAsync`: after deserialize, if `config.FormatVersion != CurrentFormatVersion`, **reject** — do not
  load, surface a clear user-facing error (e.g. throw `InvalidDataException("Unsupported .cdd version …")` and
  have the view code-behind catch + show a message dialog, or route to the Messages panel). No partial load,
  no migration.
- Leave the clipboard path (`TryParseDataDisplayConfig` / `PlotExporter` v1) untouched — same-session, same
  version; the default `FormatVersion = 1` keeps it valid.

## 2. Wire the `.cdd` Save / Open dialogs (mirror the existing Load-Touchstone wiring)
In `src/Ui/Views/DataDisplay/DataDisplayView.axaml.cs` `OnLoaded` (which already injects
`SetOpenFileAction`/`SetGetCanvasSizeAction`), add three injections following the exact `DoOpenFileAsync`
pattern (`TopLevel.GetTopLevel(this)?.StorageProvider`):
- **`win.SetSaveDataDisplayAsAction(DoSaveDisplayAsAsync)`** — `sp.SaveFilePickerAsync` with a
  `FilePickerFileType("circuitRF Data Display") { Patterns = ["*.cdd"] }` and `DefaultExtension="cdd"`; on a
  chosen file call `await win.SaveAllAsync(file.Path.LocalPath, 0,0,0,0)`.
- **`win.SetOpenDataDisplayAction(DoOpenDisplayAsync)`** — `sp.OpenFilePickerAsync` (`*.cdd`, single); then
  `await using var s = await file.OpenReadAsync(); await win.LoadAllAsync(file.Path.LocalPath, s);` (pass the
  **stream** so macOS security-scoped access works, per `LoadAllAsync`'s `jsonStream` param). Optionally, if
  `win.HasUnsavedChanges()`, prompt before replacing the current display (nice-to-have).
- **`win.SetGetWindowGeometryAction(() => (0,0,0,0))`** — the Data Display is an embedded **Dock document**,
  not a floating OS window, so geometry isn't persisted; zeros mean "not saved" and `LoadAllAsync` skips the
  `WindowGeometryLoaded` reposition. (If/when tear-off floating windows persist geometry, revisit.)

Wrap both dialog handlers in try/catch and surface load/version errors to the user (message dialog or Messages
panel) rather than failing silently.

## 3. Toolbar affordances (document-scoped)
In `src/Ui/Views/DataDisplay/DataDisplayView.axaml`, add **Save Display** and **Open Display** buttons to the
existing in-document toolbar (next to Load Touchstone / Fit All), using the toolbar icon idiom
(`SystemBaseMediumColor` icons): `ContentSaveOutline` → `SaveDataDisplayCommand`, `FolderOpenOutline` →
`OpenDataDisplayCommand` (and optionally a Save-As affordance → `SaveDataDisplayAsCommand`). Tooltips
"Save Display (.cdd)" / "Open Display (.cdd)".
- **Do NOT** bind global `Ctrl/Cmd+S` / `Ctrl/Cmd+O` — those belong to the workspace (`.cws`). If you add
  KeyBindings, scope them to the Data Display document and use non-conflicting gestures (e.g.
  `Ctrl/Cmd+Shift+S` / `Ctrl/Cmd+Shift+O`); verify in-app they don't steal the workspace shortcuts. Toolbar
  buttons are the required affordance; KeyBindings are optional.

## 4. Register the format
- `project-file-formats.md` "Open items": mark `.cdd` (Phase 7) **settled** — note it round-trips placed
  plots/traces/markers + **tab selection, canvas zoom/pan, and per-plot axes** via `DataDisplayConfig`
  (System.Text.Json, `JsonStringEnumConverter`, references-not-data, `format_version` reject-on-mismatch).
- **Out of 7.1e scope (note as a later item, don't implement):** the `.cws` auto-reopening open Data Displays
  on workspace load (Dock-document restore). 7.1e is the explicit Save/Open Display round-trip the owner asked
  for ("reload a `.cdd` file").

## 5. Viewport-restore verification (the owner's requirement — the real gate)
The engine already restores it; **prove it end-to-end at runtime** and guard against regressions:
- Create a display with **≥2 tabs**; on one tab, **pan + zoom the canvas** to a non-default view and **zoom a
  plot's axes** (e.g. zoom into a Smith/Rect plot); make a **non-first tab active**. Save Display → `.cdd`.
- Reset (New Display), then **Open Display** the file. Confirm **exactly** restored: the same **active tab**,
  the same **canvas zoom + pan** (plots right where they were), and each plot's **axes zoom**. Nothing
  re-centers or re-autoscales.
- Confirm **Open Display does not call `FitAll`/`Autoscale`** anywhere on the open path (it currently doesn't —
  keep it that way; the restored `_zoomLevel`/offsets + `RestoreAxesFromConfig` must stand).

## Guardrails
- Reuse `SaveAllAsync`/`LoadAllAsync`/`BuildTabConfig`/`LoadFromTabConfigAsync` as-is (only the
  `format_version` write/reject is new logic in them). The only model change is the version field.
- **Clipboard Cut/Copy/Paste of plots stays OUT of scope** — `PerformCopy` remains the `// TODO 7.x` stub.
- No `PlotControl`, inspector, Properties dock (7.1d-2), or MarkerEditor changes.

## Gate (acceptance)
1. Builds green. Save Display writes a `.cdd` (indented JSON, enum-as-name, `format_version` present); Open
   Display reads it back.
2. **Round-trip restores active tab + canvas zoom/pan + per-plot axes zoom exactly** (§5) — plots right where
   the user left them; no auto-fit on open.
3. A `.cdd` with a wrong `format_version` is **rejected with a clear error**, not silently mis-loaded.
4. Toolbar Save/Open Display buttons work; global workspace `Ctrl/Cmd+S`/`O` are not clobbered.

## On completion
Tick the 7.1e bullet in `docs/design/data-display.md`, note "Phase 7.1e — COMPLETE" in `src/Ui/CLAUDE.md`, and
report build + a short clip/screenshots of the save→reopen viewport restore. Next: **7.1d-2** (Properties-dock
surface) per the plan order, unless the owner reprioritizes.
