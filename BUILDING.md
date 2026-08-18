# Packaging circuitRF

How to turn a source checkout into the installers users download: **`.msi`** for Windows,
**`.dmg`** for macOS, **`.deb`** for Linux. For plain building and running from source, see
[README ▸ Getting started](README.md#getting-started).

Every script here is run **from the repository root** and writes its installer to `dist/`.

> **Each platform's installer must be built on that platform.** Cross-publishing the binaries works,
> but WiX only runs on Windows, `hdiutil`/`codesign` only on macOS, and the Windows executable only
> gets its embedded icon when the publish itself happens on Windows.

---

## The version number

**`VERSION` at the repository root is the only place circuitRF's version is written** — one line,
e.g. `0.9.0-beta.1`, `1.0` or `1.0.1`:

```bash
echo "1.0.0" > VERSION       # that is the whole procedure
```

Everything else *reads* it — nothing is generated, so there is no version script to run and no
packaging file to update:

| Reader | What it gets |
|---|---|
| `Directory.Build.props` | `Version` / `InformationalVersion` on every assembly → the in-app **About** box |
| `packaging/version.sh` | the macOS bundle scripts (stamped into the `.app`'s `Info.plist`) and the `.dmg` / `.deb` names |
| `packaging/version.ps1` | the `.msi`'s ProductVersion and file name |

Two derived forms exist because some fields must be purely numeric: `0.9.0-beta.1` becomes **`0.9.0`**
for `AssemblyVersion`, `CFBundleVersion` and the MSI ProductVersion, and **`0.9.0~beta.1`** for the
Debian package (dpkg sorts `~beta` *before* the release, while a plain `-beta` sorts *after* it).

The version strings inside `src/Ui/Assets/macOS/*.plist` are placeholders — the bundle scripts
overwrite them at bundle time. For a one-off build without touching the file, set `CRF_VERSION=1.2.3`
in the environment (macOS/Linux) or pass `-Version 1.2.3` (Windows).

---

## App icons

Icons are generated from the committed brand SVGs in `src/Ui/Assets/artwork/` — the repository
tracks the artwork, never the icon binaries:

```bash
dotnet run --project tools/IconGen           # all three apps; or: -- circuitrf
```

It writes `src/Ui/Assets/circuitRFIcon.icns` (macOS), `…/circuitRFIcon.ico` (Windows) and
`packaging/linux/icons/circuitrf.png` (Linux). **The packaging scripts below run it for you** — you
only need this command directly after changing the artwork.

---

## Windows — `.msi` (x64, arm64, x86)

**One-time setup**, in PowerShell:

```powershell
dotnet tool install --global wix
wix extension add --global WixToolset.UI.wixext
```

**Build** (run each line for the architecture you want):

```powershell
.\packaging\windows\build-msi.ps1                    # → dist\circuitRF-0.9.0-beta.1-x64.msi
.\packaging\windows\build-msi.ps1 -Arch arm64        # → dist\circuitRF-0.9.0-beta.1-arm64.msi
.\packaging\windows\build-msi.ps1 -Arch x86          # → dist\circuitRF-0.9.0-beta.1-x86.msi
```

The installer offers a license page, a changeable install directory, a Start Menu entry and an
optional desktop shortcut, and registers `.crfw` / `.cws`, `.charm` and `.wBond` so double-clicking
one opens circuitRF.

The MSI is unsigned, so SmartScreen warns on first run. To sign it you need a code-signing
certificate:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 dist\circuitRF-0.9.0-beta.1-x64.msi
```

---

## macOS — `.dmg` (Apple Silicon)

No setup beyond the .NET SDK — everything else ships with macOS.

```bash
./packaging/macos/build-dmg.sh                # → dist/circuitRF-0.9.0-beta.1-arm64.dmg
```

The disk image contains `circuitRF.app` and a drop link to `/Applications`. The app is **ad-hoc
signed**, so the first launch needs right-click ▸ **Open** (or
`xattr -dr com.apple.quarantine /Applications/circuitRF.app`).

For public distribution, sign with a Developer ID certificate and notarise: replace the `"-"` in
`src/Ui/bundleForMacOS.sh`'s `codesign` line with your identity, then

```bash
xcrun notarytool submit dist/circuitRF-0.9.0-beta.1-arm64.dmg --apple-id you@example.com \
      --team-id TEAMID --password APP-SPECIFIC-PASSWORD --wait
xcrun stapler staple dist/circuitRF-0.9.0-beta.1-arm64.dmg
```

*Intel Macs:* change `RID` to `osx-x64` in `src/Ui/bundleForMacOS.sh`.

---

## Linux — `.deb` (x64, arm64)

**One-time setup** — [fpm](https://fpm.readthedocs.io) (the `dotnet-deb` tool targets .NET 9 and
does not work here):

```bash
sudo apt-get install ruby-dev build-essential
sudo gem install fpm
```

**Build:**

```bash
./packaging/linux/build-deb.sh x64            # → dist/circuitRF-0.9.0-beta.1-x64.deb
./packaging/linux/build-deb.sh arm64          # → dist/circuitRF-0.9.0-beta.1-arm64.deb
```

The package installs to `/opt/circuitrf/`, puts `circuitrf` on `PATH`, and registers the icon, the
application-menu entry and the `.crfw` / `.cws`, `.charm` and `.wBond` file types.

```bash
sudo apt install ./dist/circuitRF-0.9.0-beta.1-x64.deb
sudo apt remove circuitrf
```

Cross-building is fine — an `arm64` package builds on an x64 machine.

---

## harmonicaRF and wBond

They are the same assembly with a different entry point (`-p:CrfApp=harmonica` / `-p:CrfApp=wbond`),
and today they ship as macOS bundles only:

```bash
./packaging/macos/build-dmg.sh harmonica      # → dist/harmonicaRF-0.9.0-beta.1-arm64.dmg
./packaging/macos/build-dmg.sh wbond          # → dist/wBond-0.9.0-beta.1-arm64.dmg
```

For Windows or Linux they publish as plain self-contained binaries — no installer exists yet:

```bash
dotnet publish src/Ui/CircuitRF.Ui.csproj -c Release -r win-x64 --self-contained -p:CrfApp=wbond
```

---

## Release checklist

1. `dotnet test` is green (see the root `CLAUDE.md` for what the default test gate covers).
2. Bump the repo-root `VERSION` file — that is the only place, and it feeds the About box, all
   three installers and their file names.
3. Build all six installers on their respective platforms.
4. Install each one on a clean machine; confirm the icon appears in the file manager, **Help ▸ About**
   shows the version you set, and double-clicking a workspace (`.crfw` / `.cws`) opens it.
5. Attach them to the GitHub release and update the download table in `README.md`.

---

## Notes for contributors

- **There is no Makefile, and none is needed.** Each installer is built by the one tool its platform
  requires, on that platform; a Makefile would only wrap a single script per OS.
- `dist/`, `publish/`, the generated icons and `packaging/windows/Files.wxs` are all build products
  and are git-ignored. Nothing produced by this document should ever be committed.
- The Windows file list is **harvested** from the publish output at build time rather than listed by
  hand — circuitRF ships its user documentation as loose files beside the executable, and a
  hand-written list goes stale the moment that set changes.
