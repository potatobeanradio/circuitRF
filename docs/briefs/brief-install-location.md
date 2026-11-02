# Brief — choose where circuitRF installs on Windows, and say where it is

**Tag:** `R-inst-n` · **Phase:** packaging (field feedback)
**Area:** `packaging/windows/circuitRF.wxs`, `packaging/windows/build-windows.ps1` (only if needed),
`src/Ui/Views/Dialogs/AboutWindow.axaml(.cs)`, `tests/Ui.Tests/PackagingScriptTests.cs`, `BUILDING.md`
**Found by:** a first-time Windows user, 2026-10-15. They could not tell where circuitRF had
installed, could not find the example files by searching folders, and asked to choose the install
folder during installation.

---

## 0. What is true today

- **Per-machine** installs to `%ProgramFiles%\circuitRF`, a flat directory.
- **Per-user** installs to `%LOCALAPPDATA%\Programs\circuitRF\`. That folder holds the launcher stub,
  the `current` pointer, `staging\`, and the real files in `app-<version>\`. `%LOCALAPPDATA%` is under
  a hidden folder, so a search from Explorer does not find it.
- **The installer offers no choice of location.** The comment at `circuitRF.wxs:101` says *"License
  page, install-location page, and a feature tree"*, but that is not what ships. `WixUI_FeatureTree`
  has no install-directory page. Its only location control is the **Browse…** button on the
  feature-tree page, and that button is enabled only when the selected `Feature` declares
  `ConfigurableDirectory`. `MainFeature` (`:430`) does not declare it, so the button is always
  disabled.
- The examples problem is solved separately and is not part of this brief: the examples now live
  under **File ▸ New ▸ Examples**, which copies one to a folder the user picks.

## 1. `R-inst-1` — make the location choosable

### `R-inst-1a` — the page

**Decided (owner, 2026-10-15): a dedicated Destination Folder page.**

Build a custom UI: copy `WixUI_FeatureTree`'s `<UI>` definition into the `.wxs` and insert
`InstallDirDlg` between the licence page and the feature tree. The sequence becomes
**Welcome → Licence → Destination Folder → Feature tree → Ready**, with **Back** reversing it. The
feature tree keeps the optional desktop shortcut.

- The copied `<UI>` tracks the WiX version the build pins, so take it from **that** version's
  WixUI source, not from memory or an older release. Say in a comment which version it came from.
- `InstallDirDlg` needs `WIXUI_INSTALLDIR` (or the v4+ `InstallDirectory` attribute) pointing at
  `INSTALLFOLDER`, and its **Change…** button needs `BrowseDlg` wired into the sequence.
- Also add `ConfigurableDirectory="INSTALLFOLDER"` to `MainFeature`, so the feature tree's
  Browse… button agrees with the page rather than sitting there disabled.

The options that were considered and not chosen:

| Option | Why not |
|---|---|
| `WixUI_FeatureTree` + `ConfigurableDirectory` alone | The location is a small Browse… button on the feature-tree page, which is as easy to miss as the problem that was reported. |
| `WixUI_InstallDir` | It has no feature tree, so the optional desktop shortcut would go. |

Also **fix the `:101` comment** to describe what actually ships.

### `R-inst-1b` — per-user scope must stay writable

The per-user updater does not care which folder it is in. `UpdateInstallSite.Detect` works out the
layout from `AppContext.BaseDirectory` and **probes** writability (`UpdateInstallSite.cs`). A
per-user install in any folder the user can write keeps updating itself. One the user cannot write
becomes **notify-only**: it still checks for updates but writes nothing. It does not break. Nothing
in `src/Ui/Updates` should need to change, and the gate checks that (§3, test 3).

The risk is earlier than that. A per-user MSI does not elevate, so choosing `C:\Program Files\…` in
per-user scope makes the install **fail partway** with a Windows Installer privileges error. Pick one:

- validate on the destination page (a `DoAction` that probes writability and blocks **Next**), or
- add a note on the page saying that a per-user install needs a folder this account can write, and
  let the installer's own error stand.

The first is better and costs a custom action. If it is not cheap, take the second and say which in
the completion note.

### `R-inst-1c` — an upgrade goes back to the same folder

**The trap in this feature.** `MajorUpgrade` removes the old product and installs the new one, and
`INSTALLFOLDER` starts at its authored default each time. Without a fix, a user who chose
`D:\Tools\circuitRF` would get the next release installed into `%LOCALAPPDATA%` with no warning. The
shortcuts would move, and the old folder would be left empty.

- Write the chosen folder to the registry. The key already exists:
  `Software\circuitRF\circuitRF` (`:188`, `:203`, `:233`, `:384`, `:391`, `:424`). Use HKCU for
  per-user and HKLM for per-machine, which is the existing `ArpRoot` split.
- Seed `INSTALLFOLDER` from it with a `RegistrySearch` (an `AppSearch` runs before the old product is
  removed), so the upgrade's destination page opens on the folder already in use.
- This only applies to an **MSI** upgrade. The per-user auto-update never runs the MSI.

## 2. `R-inst-2` — say where it is installed

The second half of the report was not knowing where circuitRF lives. Add a line to the **About** box
showing the install folder (`AppContext.BaseDirectory`, or the `current` pointer's parent for a
per-user install, which is the folder the user picked). The path should be selectable, with
**Show in Explorer / Finder / file manager** beside it. Keep the readout to the value alone; there is
no need for explanatory text.

## 3. Gate

Add these to `tests/Ui.Tests/PackagingScriptTests.cs` (structural checks over the `.wxs`):

1. The installer's UI sequence contains `InstallDirDlg` between the licence page and the feature tree,
   in both directions (Next and Back), and `MainFeature` carries `ConfigurableDirectory="INSTALLFOLDER"`.
2. `INSTALLFOLDER` is seeded from a `RegistrySearch` on the same key and value the install writes, in
   both scopes.
3. **Nothing in `src/Ui/Updates` hard-codes `%LOCALAPPDATA%\Programs`.** This is a source scan, and it
   is what makes §1b's claim true and keeps it true.
4. The About box reports a folder that exists, as a headless view test.

**The real gate needs Windows, and only the owner can run it** (an ARM64 VM; x64 and x86 have to be
cross-built there). Per-user and per-machine:

- (a) install to a non-default folder; launch; check the shortcuts and the CLI on `PATH`
- (b) build a higher version and run its MSI; it must land in the same folder
- (c) per-user only: the auto-update stages into `<chosen>\app-<ver>`
- (d) the Apps-list uninstall removes the chosen folder

Record the results in `packaging/RESOLVED.md`.

## 4. Scope

- **Windows only.** A macOS `.dmg` is dragged wherever the user likes. A Linux `.tar.gz` unpacks
  anywhere, and a `.deb` is conventionally fixed.
- **No code signing** here (the "unknown publisher" warning is a separate decision).
- **`.ps1` files stay pure ASCII** (root `CLAUDE.md`, packaging rules).
