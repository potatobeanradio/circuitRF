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
optional desktop shortcut, and registers every type circuitRF opens — the workspace (`.crfw` /
`.cws`), the documents (`.csch`, `.clay`, `.csym`, `.cdd`, `.ctech`, `.cem`), and `.charm` /
`.wBond` — so double-clicking one opens circuitRF. The same set is declared on macOS in
`src/Ui/Assets/macOS/Info.plist` and on Linux in `packaging/linux/circuitrf-mime.xml`; the three must
agree, and `tests/Ui.Tests/WBondStandaloneTests.cs` fails the build if they do not, or if a type is
registered that `App.OpenFiles` has no case for. The installed program is **`circuitRF.exe`** — see
*The executable name* below.

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

Each disk image contains `circuitRF.app` and a drop link to `/Applications`.

### Gatekeeper: what users see, and how to stop them seeing it

**By default the build is ad-hoc signed, and Gatekeeper refuses an ad-hoc signed app** — there is no
identity behind the signature for it to trust, so `spctl --assess` rejects it however well the bundle
is formed. **Since macOS 15 (Sequoia) the old Control-click ▸ Open bypass is gone**: the user gets a
blocked launch, then has to go to **System Settings ▸ Privacy & Security** and press **Open Anyway**.

No entitlement, `Info.plist` key or `codesign` flag changes this. In particular
`LSFileQuarantineEnabled` does not — it governs files the app *creates*, not the app itself.

Locally, clearing the quarantine attribute skips the whole thing:

```bash
xattr -dr com.apple.quarantine /Applications/circuitRF.app
```

#### Signing with a paid Apple Developer account

`build-dmg.sh` **signs if this machine can and builds unsigned if it cannot** — it never fails for
want of a certificate, and it never signs silently without saying so.

**You need a "Developer ID Application" certificate, and a paid membership does not give you one
automatically.** What it does give you automatically is **"Apple Development"** certificates, which
appear in the very same list and are for running your builds on your own machines. Signing a release
with one is *worse* than ad-hoc: it looks signed, Gatekeeper still refuses it, and the notary service
rejects it outright. The script therefore accepts nothing but a `Developer ID Application` identity
and says so when it finds only the other kind.

Create one, once:

> **Xcode ▸ Settings ▸ Accounts ▸ (your Apple ID) ▸ Manage Certificates ▸ + ▸ Developer ID
> Application** — or at [developer.apple.com/account/resources/certificates](https://developer.apple.com/account/resources/certificates).
> You must be the Account Holder of the team. `security find-identity -v -p codesigning` lists what
> you have.

After that, **just run the build**. It finds the certificate, uses it, and — the first time — offers
to store your notary credentials:

```bash
./packaging/macos/build-dmg.sh
```

It asks for your **Apple ID**, your **Team ID**, and an **app-specific password** — created at
[appleid.apple.com](https://appleid.apple.com) ▸ Sign-In and Security ▸ App-Specific Passwords, and
**not** your account password. `xcrun notarytool store-credentials` does that prompting itself and
puts them in the keychain, so this script never reads, holds or echoes a password, and nothing lands
in your shell history. It is a one-time step; later builds notarise without asking.

##### The three values, and the two that go wrong

`notarytool` asks for an Apple ID, a Team ID and a password, and returns the same unhelpful 401 —
*"Invalid credentials. Username or password is incorrect"* — for several quite different mistakes.
The build script prints the first two off your own certificates; these are what they mean.

| Value | What it is | How it goes wrong |
|---|---|---|
| **Apple ID** | the email you sign in to the developer account with | a *different* Apple ID from the one that owns the team |
| **Team ID** | ten characters — the certificate's **`OU`** field | reading the value in **brackets** in the identity list instead |
| **Password** | an **app-specific password** | using the Apple ID account password |

**The Team ID trap is worth spelling out.** In `security find-identity -v -p codesigning` you see
something like `Apple Development: you@example.com (5K57RC984E)`. For a **Developer ID** certificate
the bracketed value *is* the Team ID — but for an **Apple Development** certificate it is a
per-certificate identifier and the Team ID is something else entirely. Read the real one out of the
certificate:

```bash
security find-certificate -a -c "Apple D" -p | \
  while openssl x509 -noout -subject 2>/dev/null; do :; done
#   … /CN=Apple Development: you@example.com (5K57RC984E)/OU=74Y39278RS/O=Your Name/…
#                                             ^^^^^^^^^^ not this   ^^^^^^^^^^ this
```

The build script prints them for you, filtered to unexpired certificates, when it offers to store
credentials.

**The password must be app-specific.** Make one at
[appleid.apple.com](https://appleid.apple.com) ▸ Sign-In and Security ▸ App-Specific Passwords — it
looks like `abcd-efgh-ijkl-mnop`, and the option only appears once the account has two-factor
authentication. Your Apple ID password will always return a 401 here, and the message will not tell
you that is why.

**The name Apple asks you for when creating it is a label for you and nothing else** — it is never
sent anywhere and is matched against nothing. Do not confuse it with the keychain profile name
(`circuitrf-notary`), which is unrelated and *is* looked up. Name it after the **tool**, not the
product: one app-specific password notarises everything you sign with that Apple ID, so
`notarytool` ages better than `circuitRF`, which would imply you need a second one for wBond.

> **"…to sign in to an app or service not provided by Apple." Yes, use it anyway.** That sentence on
> appleid.apple.com is consumer-facing and predates `notarytool`; it is not a rule that excludes
> Apple's own tools. `notarytool`'s man page says the opposite in as many words — create one *"by
> following the instructions on 'Using app-specific passwords'"*, and *"any developer that has
> accepted the relevant agreements can use app-specific passwords with the Apple notary Service."*
>
> The reason it is needed has nothing to do with who wrote the app. Your Apple ID has two-factor
> authentication, and `notarytool` is a non-interactive command-line client that cannot show you a
> 2FA prompt. An app-specific password is the credential that stands in for that flow. Your account
> password would have to be paired with a 2FA code that nothing here can ask you for — which is why
> it returns a 401 rather than a challenge.
>
> **The alternative, if the wording still grates:** an **App Store Connect API key**, which is a key
> file rather than a password and is the better choice for CI (it does not break when the account
> password changes, and it is revocable on its own). `store-credentials` takes it instead:
>
> ```bash
> xcrun notarytool store-credentials circuitrf-notary \
>       --key AuthKey_XXXXXXXXXX.p8 --key-id XXXXXXXXXX --issuer <issuer-uuid>
> ```
>
> Create it in App Store Connect ▸ Users and Access ▸ Integrations ▸ Keys. Either credential produces
> the same stored profile, and the build script does not care which you used.

To iterate on credentials without running a build:

```bash
xcrun notarytool store-credentials circuitrf-notary \
      --apple-id you@example.com --team-id 74Y39278RS
```

It prompts for the password, validates against Apple before saving, and only writes the keychain
entry when the three agree. Once it succeeds, builds notarise without asking again.

**Notarising is not optional if you want the prompt gone.** A Developer ID signature *without*
notarisation is still refused on first launch, so a signed-but-unnotarised build reports that plainly
rather than implying the job is done.

Overrides, for CI and for when you want the other behaviour:

| Variable | Effect |
|---|---|
| `CRF_SIGN_IDENTITY="Developer ID Application: NAME (TEAMID)"` | use this identity, ask nothing |
| `CRF_SIGN=never` | ad-hoc build even on a machine that could sign |
| `CRF_NOTARY_PROFILE=<name>` | use this notarytool keychain profile (default `circuitrf-notary`) |
| `CRF_NOTARIZE=never` | sign, but skip notarisation |

Nothing prompts unless the shell is interactive: a scripted run with several certificates installed
and no `CRF_SIGN_IDENTITY` refuses to guess between them and builds unsigned instead.

#### What changes when a real identity is used

- **the hardened runtime** (`--options runtime`) goes on, because notarisation requires it. That in
  turn is why `Assets/macOS/Entitlements.plist` declares `allow-jit`,
  `allow-unsigned-executable-memory`, `disable-library-validation` and
  `allow-dyld-environment-variables` — .NET's JIT does not run without the first two, and the third
  is what lets `osdi-worker` `dlopen()` a vendor kit's compiled model, which nobody signed with your
  certificate. Those keys are inert in an ad-hoc build.
- **a secure timestamp** (`--timestamp`, which needs the network) replaces `--timestamp=none`.
  Notarisation rejects a signature without one.
- **the `.dmg` itself is signed**, then notarised, then **stapled**. The staple is what makes the
  first launch work offline: without it the Mac must reach Apple to check, so a user on a poor
  connection still meets a prompt for an app that is genuinely notarised.

`crf-vmhost` is signed separately either way, with its own `com.apple.security.virtualization`
entitlement — see `packaging/RESOLVED.md` for why that has to happen after the `--deep` pass and be
followed by a re-seal.

#### What signing does NOT fix: a downloaded kit

Approving or notarising circuitRF covers everything inside its bundle — including `crf-vmhost` and
`osdi-worker`, which need no separate approval of their own. It does **not** cover a PDK the user
installed separately. A downloaded kit's compiled model library is quarantined, and `dlopen` refuses
it with no prompt and no System Settings entry, on **every** loader — this is a property of the
library, not of who is loading it. circuitRF recognises that refusal and prints the remedy
(`xattr -dr com.apple.quarantine <kit folder>`); see `tools/osdi-worker/README.md`.

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

## Automatic updates — the two user-local channels

circuitRF, harmonicaRF and wBond check GitHub for a newer release, download it in the background and
swap it in at the next launch. **A silent update requires an install location the running user can
write**, which `%ProgramFiles%` and `/opt` are not — so the machine-wide `.msi` and the `.deb` are
notify-only, and two additional artifacts exist for the installs that do update themselves. The full
reasoning is in `docs/design/auto-update.md`; what a release has to produce is here.

| Artifact | Built by | Installs to | Updates itself |
|---|---|---|---|
| `circuitRF-<ver>-<arch>.msi` | `build-msi.ps1` | `%ProgramFiles%\circuitRF` | notify only |
| `circuitRF-<ver>-win-<arch>-user.msi` | `build-msi.ps1 -Scope perUser` | `%LOCALAPPDATA%\Programs\circuitRF` | **yes** |
| `circuitRF-<ver>-win-<arch>.zip` | `build-msi.ps1 -Scope perUser` | — (the update payload) | — |
| `circuitRF-<ver>-<arch>.dmg` | `build-dmg.sh` | `/Applications` | **yes**, for an admin user |
| `circuitRF-<ver>-<arch>.deb` | `build-deb.sh` | `/opt/circuitrf` | notify only |
| `circuitRF-<ver>-linux-<arch>.tar.gz` | `build-tarball.sh` | `~/.local/share/circuitRF` | **yes** |

**These names are a contract, not a convention.** `src/Ui/Updates/UpdateAssetNames.cs` parses exactly
these spellings. Rename an artifact and updates stop — with no error anywhere, no log line and no user
report, because a user who is not being offered an update has nothing to notice.
`tests/Ui.Tests/Updates/UpdateAssetNamingConventionTests.cs` asserts the scripts still construct them.

### Windows, per-user

```powershell
.\packaging\windows\build-msi.ps1 -Scope perUser -Arch x64
```

It builds a small native **launcher stub** first (`packaging\windows\stub\build-stub.ps1`, needing
`zig` or MSVC's `cl.exe`), then publishes, then emits both the `-user.msi` and the `.zip`. The stub is
the one file that never changes: shortcuts and file associations point at it, and it starts whatever
version the `current` file names. That is what makes an update a pointer flip rather than an overwrite
of a file that is currently in use — which matters, because you cannot overwrite a running `.exe`.

**Authenticode signing is a prerequisite for the per-user channel, exactly as Developer ID is on
macOS.** R-AU-25's third step compares the staged payload's publisher against the **running**
application's, and an unsigned build has no publisher to compare against — so `PayloadVerifier`
refuses, and the install falls back to notify-only. The symptom is not an error: a writable per-user
install simply says a new version is available and declines to install it. Sign the published
`circuitRF.exe` with `signtool` before packaging, or the `.msi` and the `.zip` will ship an
installation that can never update itself.

The updater verifies **every** `.exe` and `.dll` in the staged tree, not just the apphost — a payload can
otherwise carry a genuine signed `circuitRF.exe` beside anything at all. `PublishSingleFile` makes that
one file today; if it is ever turned off, every third-party assembly in the tree needs signing too or
updates stop. Design §9.1.

### Linux, user-local

```bash
packaging/linux/build-tarball.sh x64
```

No `fpm` and no root. The archive contains `app-<version>/` plus an `install.sh` that lays down
`~/.local/share/circuitRF/current -> app-<version>`, a launcher at `~/.local/bin/circuitrf` and the
`.desktop` entry — both pointing at the stable `current/` path, so an update re-registers nothing.

### macOS

**No additional artifact.** The updater consumes the existing `.dmg` (`hdiutil attach`, `ditto` the
`.app` out, `hdiutil detach`), so the mac release gains nothing that could drift out of step with what
users download by hand.

**Developer ID signing and notarization are a hard prerequisite, not a nicety.** Since Ventura, a
process that modifies another application's bundle needs "App Management" permission unless it is
signed with the **same Team ID** as the bundle it is modifying. An ad-hoc build has no Team ID, so it
cannot silently self-update even in principle — and the failure mode is a TCC prompt, not an error.
Measured on macOS 26.5.2, 2026-08-25:

- a Developer ID-signed app with Team `L6234MFQ5Z` modifying `/Applications/circuitRF.app`
  (same team) — **allowed, no prompt**;
- the same app modifying a different vendor's bundle — **denied**, `EPERM`;
- the identical app re-signed **ad-hoc** — **denied on both**.

`build-dmg.sh` now notarizes and staples the `.app` as well as the `.dmg`, in that order. A staple is
attached to one artifact and does not travel with a bundle copied out of a stapled image (verified:
`stapler validate` on an extracted bundle reports no ticket), so without the extra pass the installed
application has no ticket and every Gatekeeper assessment of it has to reach Apple.

---

## The manual acceptance matrix

**Run this at every release that changes signing, packaging layout, or the updater.** No test can cover
any of it — the outcomes are dialogs that either appear or do not.

| Platform | Check | Result |
|---|---|---|
| macOS | An updated build launches with **no** Gatekeeper dialog and **no** App Management prompt | |
| macOS | The released `.dmg` itself is signed and stapled — the updater verifies the **image** before it mounts it, so an unsigned image is refused and the update never happens (design §9.2) | |
| Windows | The published `circuitRF.exe` is Authenticode-signed — an unsigned one is notify-only, silently | |
| Windows | `PublishSingleFile` is still on, so "every PE in the payload" and "the whole application" are the same set (design §9.1) | |
| Windows | A per-user install updates with **no** UAC prompt and **no** SmartScreen warning | |
| Linux | A `~/.local` install updates and the `.desktop` entry still launches afterwards | |
| All | A read-only install (`.msi` / `.deb` / standard-user macOS) is notify-only and writes nothing | |

**And one thing that is not a build step.** Until design §15.5's signed manifest exists, **the GitHub
release is a signing key on two of the three platforms** — whoever can publish a release gets code
execution on Linux, and on Windows through the `pcell-python/**` payload that Authenticode cannot cover.
Two-factor on the account, no long-lived release tokens, and the same care over who can push a tag as
over who holds the Developer ID certificate. Design §9.1 has the full table.

How to drive one without waiting for a real release: install the previous version, publish the new one
as a GitHub prerelease, tick **Settings ▸ General ▸ Updates ▸ Include beta releases**, then
**Help ▸ Check for Updates…** (which ignores the 24-hour throttle). The Message Panel says when it has
staged. Quit and relaunch — that relaunch is what the matrix is actually testing.

---

## Release checklist

1. `dotnet test` is green (see the root `CLAUDE.md` for what the default test gate covers).
2. Bump the repo-root `VERSION` file — that is the only place, and it feeds the About box, every
   installer and all their file names.
3. Build every artifact on its own platform: the three per-machine `.msi`s **and** the three
   `-user.msi` + `.zip` pairs (Windows), both `.dmg`s (macOS), both `.deb`s **and** both `.tar.gz`s
   (Linux).
4. Install each one on a clean machine; confirm the icon appears in the file manager, **Help ▸ About**
   shows the version you set, and double-clicking a workspace (`.crfw` / `.cws`) opens it. Check a
   document type too (a `.csch` is the quickest): it should open as an orphan tab, and with circuitRF
   already running it should open in the RUNNING copy rather than starting a second one.
5. **Run the manual acceptance matrix above** if this release touched signing, packaging or the
   updater.
6. **If a release key is compiled in** (`src/Ui/Updates/ReleaseKeys.cs` — see below), build and sign the
   manifest, and attach **both** files to the release under exactly those names:

   ```bash
   dotnet run --project tools/ReleaseSigner -- manifest dist \
       --base-url https://github.com/potatobeanradio/circuitRF/releases/download/v<version>
   dotnet run --project tools/ReleaseSigner -- sign dist/update-manifest.json ~/keys/release-key.pem
   dotnet run --project tools/ReleaseSigner -- verify \
       dist/update-manifest.json dist/update-manifest.json.sig "<the public key from ReleaseKeys.cs>"
   ```

   Forgetting this on a keyed build means **no client is offered the release at all**, silently — which
   is the intended failure direction and is exactly why the `verify` step is in the list.
7. Attach them to the GitHub release and update the download table in `README.md`.
8. Publish it — see **Publishing the GitHub release** below for the tag and the flag, which are what
   decide who is offered it.

---

## The release signing key (design §15.5)

**This is not a build step. It is a one-time decision, and it is one-way.**

Until a key exists, an update's integrity rests on the platform's own code signing: everything on macOS,
only the PEs on Windows — so **not** `pcell-python/**/*.py`, which circuitRF executes — and nothing at
all on Linux. Whoever can publish a release to the repository therefore gets code execution on two of
the three platforms. A release key removes that: a release is accepted only when a manifest signed by a
key that is on no host names the payload and its SHA-256.

```bash
dotnet run --project tools/ReleaseSigner -- keygen ~/keys/circuitrf-release.pem
```

It prints the public half. Paste that into `ReleaseKeys.PublicKeySpkiBase64` and commit it — a *public*
key belongs in version control, because it is the thing being trusted and should be reviewable, diffable
and attributable to the commit that introduced it.

**What changes the moment you do:**

- The build carrying the key, and every build after it, **refuses any release without a validly signed
  manifest**. Clients older than it are unaffected and keep updating normally, so this is a forward
  migration rather than a flag day — but there is no going back for a client that has the key.
- Every release from then on must carry `update-manifest.json` and `update-manifest.json.sig`. Step 6 of
  the checklist above.
- A signed manifest may name the payload on **any** `https` host, so the compiled-in host allow-list
  stops constraining a keyed build. That is what makes moving off GitHub, or mirroring, possible for
  clients already in the field (design §15.4, §15.6).

**The private key.** Off this repository, off the build machine if you can manage it, owner-read-only
(`keygen` sets that), and **backed up somewhere you will still have in five years**. Losing it strands
every installed copy that carries the matching public key: they will refuse every release you publish
after that, silently, and the only way back is a hand reinstall by every user. Treat it exactly as you
treat the Developer ID certificate — it is the same kind of object and, on Linux, a more powerful one.

---

## Publishing the GitHub release

Four things on the release page decide who gets the update. Nothing else does.

| | |
|---|---|
| **Tag** | `v<version>`, matching the repo-root `VERSION` file exactly — `v1.1.0`, `v1.1.0-beta.1`. |
| **Pre-release** | Ticked for a beta, clear for a stable release. This is the whole channel mechanism. |
| **Draft** | Never. A draft is offered on neither channel. |
| **Assets** | Straight from `dist/`, names unchanged. |

**A beta needs the suffix in the tag as well as the flag.** The flag alone keeps it off the stable
channel today, but it burns the version: tag a beta `v1.1.0` and your testers are running `1.1.0`, so
when the real 1.1.0 ships it is not *greater* than what they have and they are never offered it. They
sit on a beta that calls itself the release until you ship 1.1.1.

**Number betas `beta.1`, `beta.2`, `beta.10`** — dot-separated, so they compare numerically. Written
`beta1`/`beta2`/`beta10` they compare as text, `beta10` sorts before `beta2`, and a tester on beta.2 is
never offered beta.10.

**Do not rename an asset.** Matching is by exact name against the tag's spelling
(`circuitRF-1.1.0-beta.1-arm64.dmg`), and a mismatch means the release is silently never offered — a
user who is not offered an update has nothing to notice.

**The "Latest" badge is ignored.** circuitRF reads the release list and orders it by SemVer itself, so
*Set as the latest release* cannot hold users on a version or roll them back. To withhold a bad release,
draft it or delete it and ship the next one.

---

## Notes for contributors

- **There is no Makefile, and none is needed.** Each installer is built by the one tool its platform
  requires, on that platform; a Makefile would only wrap a single script per OS.
- `dist/`, `publish/`, the generated icons and `packaging/windows/Files.wxs` are all build products
  and are git-ignored. Nothing produced by this document should ever be committed.
- The Windows file list is **harvested** from the publish output at build time rather than listed by
  hand — circuitRF ships its user documentation as loose files beside the executable, and a
  hand-written list goes stale the moment that set changes.
