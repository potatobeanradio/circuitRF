# Packaging circuitRF

How to turn a source checkout into the installers users download: **`.msi`** for Windows,
**`.dmg`** for macOS, **`.deb`** for Linux. For plain building and running from source, see
[README ▸ Getting started](README.md#getting-started).

Every script here is run **from the repository root** and writes its installer to `dist/`.

> **Each platform's installer must be built on that platform.** Cross-publishing the binaries works,
> but WiX only runs on Windows, `hdiutil`/`codesign` only on macOS, and the Windows executable only
> gets its embedded icon when the publish itself happens on Windows.
>
> **Architectures are a different matter, and all three platforms cross-build across them.** On
> macOS that includes the native helper programs and the Linux VM image, so one run of
> `build-dmg.sh` produces both the Apple Silicon and the Intel disk image from either kind of Mac —
> see *macOS ▸ `.dmg`* below for how, and for what is checked before either one is written.

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

## Helper programs — the one prerequisite beyond the .NET SDK

<a id="helper-programs"></a>

circuitRF ships a few small programs beside its assemblies. It never loads a vendor's compiled
device model itself: it starts a **device worker** that does, in its own process. On macOS that
worker is a Linux binary run inside the small **VM host** circuitRF ships, with its own kernel and
initramfs. All of them are **build products** — nothing binary is committed — built by
`tools/senior-worker/`, `tools/osdi-worker/` and `tools/macos-vmhost/`, which `dotnet build` runs
for you.

**Those build steps only ever warn.** Nobody should be unable to build circuitRF for want of a C
compiler, so a missing toolchain prints a line and the build succeeds. That is right for a build and
wrong for a release, so **the packaging scripts refuse instead** — an installer missing the worker
produces an application that reads a kit, describes it correctly, and refuses at Run naming a
program the user never installed and had no way to install.

So a C compiler must be on PATH when you package. **zig** is the cheapest — one archive, no
installer, no daemon, and it cross-compiles the x86-64 Windows worker from an ARM machine:

```powershell
winget install zig.zig                      # Windows  (or: scoop install zig)
```
```bash
brew install zig                            # macOS
sudo snap install zig --classic --beta      # Linux    (or your package manager, or ziglang.org/download)
```

Then `dotnet build` picks it up by itself. To build the helpers without a full build:

```bash
tools/senior-worker/ensure-built.sh                       # macOS / Linux
tools/macos-vmhost/ensure-built.sh --with-image           # macOS only — the ~330 MB, once
tools/macos-vmhost/ensure-built.sh --with-image --arch x86_64   # ...and the other architecture
```
```powershell
tools\senior-worker\ensure-built.cmd                      # Windows
```

zig is not the only route:

| Platform | Alternatives to zig | Also needed |
|---|---|---|
| Windows | x86-64 MSYS2/MinGW `gcc`, or Docker/Podman plus a `bash` | |
| macOS | Docker/Podman | Xcode command line tools, and a network for the VM image |
| Linux | host `gcc` (x86-64), or Docker/Podman | |

Two things worth knowing before they surprise you:

- **The Windows worker is built for x86-64 even on an ARM machine, deliberately.** It exists to load
  vendor model libraries, those ship as x64, and a process holds exactly one instruction set —
  Windows runs the pair under its own translation. A native ARM worker would start and then fail to
  load a single model, so a `gcc` that builds for arm64 is refused rather than used.
- **There is no 64-bit ARM Linux build of the worker.** `build-deb.sh arm64` says so and packages
  without it; everything else in that package is unaffected.
- **The macOS VM image is per-architecture and costs ~330 MB the first time for each.** Packaging
  both disk images therefore downloads both guest images once — Alpine aarch64 and Alpine x86-64 —
  plus a small x86-64 glibc runtime that **both** need: the guest userland is Alpine, which is musl,
  while the worker and every vendor model library it loads are glibc-linked and name
  `/lib64/ld-linux-x86-64.so.2` as their ELF interpreter. All of it is pinned by exact version and
  SHA-256 in `tools/macos-vmimage/sources.lock`, and cached in `tools/macos-vmimage/.work/`
  afterwards.

To package deliberately without them, set `CRF_ALLOW_NO_DEVICE_WORKER=1`.

---

## Windows — `.msi` (x64, arm64, x86)

**One-time setup**, in PowerShell:

```powershell
dotnet tool install --global wix
```

That is the whole WiX setup (see *Helper programs* above for the C toolchain). **Do not add the `WixToolset.UI.wixext` extension by hand** — the build
script installs it, pinned to the `wix` actually on your PATH. The WiX extension cache is keyed by
wix version, so an extension added once and a `dotnet tool update --global wix` later stop seeing
each other, and the symptom is

```
wix.exe : error WIX0144: The extension 'WixToolset.UI.wixext' could not be found.
```

which reads like a missing install and is really a version mismatch.

**Build** (run each line for the architecture you want):

```powershell
.\packaging\windows\build-msi.ps1                    # → dist\circuitRF-0.9.0-beta.1-x64.msi
.\packaging\windows\build-msi.ps1 -Arch arm64        # → dist\circuitRF-0.9.0-beta.1-arm64.msi
.\packaging\windows\build-msi.ps1 -Arch x86          # → dist\circuitRF-0.9.0-beta.1-x86.msi
```

The installer offers a license page, a changeable install directory, a Start Menu entry and an
optional desktop shortcut, and registers `.crfw` / `.cws`, `.charm` and `.wBond` so double-clicking
one opens circuitRF. The installed program is **`circuitRF.exe`** — see *The executable name* below.

> **PowerShell:** either `powershell.exe` (5.1, the Windows default) or `pwsh` (7.x) works, from the
> repository root. If you edit a script under `packaging/`, keep it **pure ASCII** — the reason is in
> the header of `build-msi.ps1`, and `tests/Ui.Tests/PackagingScriptTests.cs` enforces it.

The MSI is unsigned, so SmartScreen warns on first run. To sign it you need a code-signing
certificate:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 dist\circuitRF-0.9.0-beta.1-x64.msi
```

---

## macOS — `.dmg` (Apple Silicon and Intel)

No setup beyond the .NET SDK and the toolchain in *Helper programs* above — everything else ships
with macOS.

```bash
./packaging/macos/build-dmg.sh                # BOTH: dist/circuitRF-0.9.0-beta.1-{arm64,x64}.dmg
./packaging/macos/build-dmg.sh circuitrf arm64   # Apple Silicon only
./packaging/macos/build-dmg.sh circuitrf x64     # Intel only
```

**Both architectures build from whichever Mac you are on**, and the default with no architecture
argument is both — a release needs the pair, and the failure mode of a one-at-a-time default is
silent: you ship whichever architecture you were sitting at and the other one simply does not exist.
Name one explicitly when you are iterating and do not want to pay for the other.

This is the one place macOS is *easier* than the rule at the top of this file suggests. Every piece
of the bundle cross-builds:

| Piece | How |
|---|---|
| the .NET application | `dotnet publish -r osx-arm64` / `-r osx-x64`, either way round |
| `crf-vmhost` | `swift build --arch` — Apple's toolchain targets both slices, and Virtualization.framework is in the SDK for both |
| `osdi-worker` | `cc -arch` |
| the Linux VM image | pure download-and-repack (`curl`, `tar`, `cpio`, `gzip`, `python3`) — no compiler produces either guest kernel |
| `senior_worker` | one file for both: it is an **x86-64 Linux** binary either way, because that is what vendor model libraries are |

The helper programs follow the RID rather than the machine, because `src/Ui/CircuitRF.Ui.csproj`
derives `--arch` from `$(RuntimeIdentifier)` and hands it to each helper's build script — so
`dotnet publish -r osx-x64` on an Apple Silicon machine gets x86-64 helpers on its own, with nothing
to remember. They land under `tools/macos-vmhost/build/<arch>/` and `tools/osdi-worker/build/<arch>/`.

**Nothing is trusted to have done that correctly.** Before writing each disk image the script reads
the architecture back out of the built bundle: `lipo -archs` for the Mach-O programs, and the kernel
image's own magic (`ARM\x64` at offset 56, `HdrS` at 0x202) for the Linux guest kernel, which `lipo`
knows nothing about. A helper that quietly fell back to the host, or a stale `tools/*/build`
directory, is caught rather than shipped — the failure it prevents is an application that launches,
reads a kit, describes it correctly and then cannot evaluate a single compiled device model.

Both architectures need macOS 13 (Ventura) or later, which is what .NET 10 requires; that rules out
Intel Macs older than roughly 2017.

Each disk image contains `circuitRF.app` and a drop link to `/Applications`. The app is **ad-hoc
signed**, so the first launch needs right-click ▸ **Open** (or
`xattr -dr com.apple.quarantine /Applications/circuitRF.app`).

For public distribution, sign with a Developer ID certificate and notarise: replace the `"-"` in
`src/Ui/bundleForMacOS.sh`'s `codesign` line with your identity, then — **once per architecture**,
since the ticket is stapled to a specific disk image:

```bash
for arch in arm64 x64; do
  xcrun notarytool submit dist/circuitRF-0.9.0-beta.1-$arch.dmg --apple-id you@example.com \
        --team-id TEAMID --password APP-SPECIFIC-PASSWORD --wait
  xcrun stapler staple dist/circuitRF-0.9.0-beta.1-$arch.dmg
done
```

**A `.app` without a disk image** is what the bundle scripts alone produce. They take the RID from
`CRF_RID` — which `build-dmg.sh` sets for each pass — and fall back to `uname -m`:

```bash
cd src/Ui && ./bundleForMacOS.sh                       # this machine's architecture
cd src/Ui && CRF_RID=osx-x64 ./bundleForMacOS.sh       # → bin/Release/net10.0/osx-x64/circuitRF.app
```

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

**The package declares no dependencies, on purpose.** It used to declare
`Depends: libicu76 | libicu74 | …`, and that is an install failure waiting for the next
distribution — ICU's package name carries its SONAME, so on a machine shipping a newer ICU apt finds
none of the listed names and refuses the package outright ("none of the choices are installable").
The build is self-contained and .NET locates whatever `libicuuc.so.<N>` the system has, so the pin
bought nothing; `postinst` prints a note — without failing — when the system has no ICU at all, and
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` runs the app without culture data on such a machine.

---

## harmonicaRF and wBond

They are the same assembly with a different entry point (`-p:CrfApp=harmonica` / `-p:CrfApp=wbond`),
and today they ship as macOS bundles only:

```bash
./packaging/macos/build-dmg.sh harmonica      # both: harmonicaRF-0.9.0-beta.1-{arm64,x64}.dmg
./packaging/macos/build-dmg.sh wbond          # both: wBond-0.9.0-beta.1-{arm64,x64}.dmg
./packaging/macos/build-dmg.sh wbond x64      # Intel only
```

The architecture argument works exactly as it does for circuitRF, and defaults to both the same way.

For Windows or Linux they publish as plain self-contained binaries — no installer exists yet:

```bash
dotnet publish src/Ui/CircuitRF.Ui.csproj -c Release -r win-x64 --self-contained -p:CrfApp=wbond
#   -> wBond.exe   (harmonica -> harmonicaRF.exe, circuitrf -> circuitRF.exe)
```

---

## The executable name

**What ships is named after the application, never after the assembly:** `circuitRF.exe`,
`harmonicaRF.exe`, `wBond.exe` on Windows, and the same names without the extension elsewhere —
`/opt/circuitrf/circuitRF` on Linux, `circuitRF.app/Contents/MacOS/circuitRF` on macOS.

The *assembly* is still `CircuitRF.Ui`, and cannot be renamed: RfCore grants it
`InternalsVisibleTo`, and all three applications are one assembly with different `Main`s. .NET names
the published native host after the assembly and offers no property to separate the two, so
`src/Ui/CircuitRF.Ui.csproj`'s **`CrfRenameApphost`** target renames the host after publish. It is a
publish-time step only — a plain `dotnet build` / `dotnet run` still produces `CircuitRF.Ui`, so
nothing that launches the build output had to change.

Five files carry the shipped name as a literal string and must agree with that target — the WiX
source and its build script, the Debian `postinst` and `.desktop`, the three macOS bundle scripts and
their `Info.plist`s. `tests/Ui.Tests/PackagingScriptTests.cs` checks all of them, because a drift
there builds cleanly and ships a shortcut to a file that does not exist.

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
