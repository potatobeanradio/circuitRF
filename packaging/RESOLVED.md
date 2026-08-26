# packaging — resolved findings

Findings from packaging work that would otherwise have to be rediscovered. Each entry names the
symptom first, because that is what the next person will have in front of them.

---

## A Windows release shipped 5 of 9 artifacts: the launcher stub "could not be built" for arm64 and x86 (2026-08-25)

**Symptom:** `.\packaging\windows\build-windows.ps1` on a Windows-on-ARM machine builds the x64
stub, then reports for arm64 and for x86 that no C toolchain could build the launcher stub. Which
architectures fail VARIES BETWEEN RUNS, which is the single most important fact in this entry and
the one that took two runs to see. The
per-user `.msi` and the `.zip` update payload are skipped for both, and the run ends
`INCOMPLETE: no launcher stub for arm64, x86.` — five artifacts where a release needs nine, with no
self-updating channel on two of the three architectures.

**Two unrelated causes that look identical in the log.** They have to be separated before either can
be fixed, and the log line that separates them is the one that is *absent*.

### x86: nothing was wrong with the compiler. PowerShell threw away a successful build.

The only output was one line:

```
'-macrofusio' is not a recognized feature for this target (ignoring feature)
```

That is an LLVM **warning**, on stderr, which says in its own text that it is ignoring the feature
and carrying on. zig exited 0. The stub compiled.

`build-stub.ps1` captured the compiler's output the obvious way:

```powershell
$ErrorActionPreference = 'Stop'          # at the top of the script
...
$log = & zig cc ... 2>&1
if ($LASTEXITCODE -ne 0) { ... }
```

**Under Windows PowerShell 5.1 that is not a capture, it is a trap.** Merging a *native* command's
stderr into the success stream while the preference is `'Stop'` turns the first stderr line into a
`NativeCommandError` and **throws**. `$LASTEXITCODE` is never read. The exit code is irrelevant — it
can be 0. So **any warning at all fails the build**, and the more warnings a toolchain emits the
more likely it is to be declared broken.

**How to tell this apart from a real compiler failure, from the log alone:** the script prints its
own line on the exit-code path (`zig cc failed with exit N`). For arm64 that line is present. For
x86 it is **absent** — the single line in the log is the caught exception's `.Message`, because a
`NativeCommandError`'s message *is* the stderr text it objected to. An absent line is the evidence.

**It does not reproduce on a developer's machine.** pwsh 7.3+ dropped this behaviour, so the same
script and the same warning succeed under `pwsh` on macOS or Linux and fail only under the
`powershell.exe` that cuts the release. Verified both ways.

**Fix:** `Invoke-Compiler` in `build-stub.ps1` sets the preference to `'Continue'` for exactly the
duration of the call, then decides from the exit code and the artifact.
`tests/Ui.Tests/PackagingScriptTests.cs`'s `PowerShellScripts_CaptureNativeStderr_OnlyWithErrorActionContinue`
holds it shut — it tracks the preference down each script rather than guessing by line distance, and
it found **four more instances of the same bug** in `build-windows.ps1`'s WiX extension check, where
one incidental line from `wix` would have ended the entire Windows release at its first step.

### arm64 and x86: zig on this machine crashes at random, roughly six times in seven

zig exits `-1073741819` (0xC0000005, an access violation) having printed **nothing at all** — a
crash, not a refusal, so neither the stub source nor the flags are implicated. The machine ran the
correct **native** `zig-aarch64-windows-0.16.0`, so the usual "you are running the x86_64 zig under
emulation" answer did not apply.

**The first theory was wrong, and it is worth recording why**, because it is a good theory that
survives the first run's evidence and dies on the second's. It was: with no explicit `-mcpu`, zig
resolves the CPU natively whenever the target's architecture and OS match the host's, so
`-target aarch64-windows-gnu` on a Windows-on-ARM host is a **native** build down a different code
path while every other architecture is a cross build. That explains why aarch64 alone failed in run
1 — and it **predicts x86 works**, because x86 is a cross build from an ARM host. x86 does not work.

**What actually settles it: the same command gave different answers on different runs.**

```
run 1   x64    native CPU, shared cache        -> BUILT, first attempt
        arm64  native CPU, shared cache        -> crashed
        x86    native CPU, shared cache        -> ran far enough to emit an LLVM warning
run 2   x64    baseline, baseline -O0, then private cache -> BUILT on the third attempt
        arm64  all four routes                 -> crashed
        x86    all four routes                 -> crashed
```

Run 2's fourth route is character for character what built x64 on the **first** attempt in run 1,
and it crashed. The x86 native-CPU command emitted a warning and carried on in run 1 and crashed
silently in run 2. No deterministic fault in a target, a flag, a cache or the source can do that.
**Across 15 attempts, 2 succeeded** — roughly one in seven, spread over every target.

**Fix: retry.** A ladder of four *different* ideas was the wrong shape for a dice roll; at one in
seven it fails more often than not. `build-stub.ps1` now retries each route — 40 attempts per
architecture, which takes the miss rate from 4.6% at twenty attempts to 0.2%. It costs nothing when
it is not needed, because a crash returns instantly and a healthy zig never sees attempt two.

**What counts as a crash is deliberately not one magic number.** Every NTSTATUS exception code has
its top bit set and so arrives as a **negative** exit code — stack overflow `0xC00000FD`, illegal
instruction `0xC000001D`, heap corruption `0xC0000374` are the same event and want the same answer.
Testing for the one code that happened is how the next one gets misread as a compiler refusal and
retried zero times. **No output at all counts too:** a compiler that refuses code says why, on
stderr, always; every crash observed here printed nothing whatsoever. A genuine compile error is
therefore tried once per route and not forty times — verified.

**How often it crashed is reported on success rather than swallowed.** A toolchain that works one
time in seven is a fact about the machine that the operator needs, and a tidy "OK" is how it stays
unfixed.

**The flags were kept anyway, on their own merits** — `-mcpu=baseline` because the stub ships to
other people's machines and building it for whichever CPU cut the release is wrong even when it
works; the private zig cache because a crash part-way through writing `%LOCALAPPDATA%\zig` can leave
a half-written entry, and this machine is producing crashes.

**Still open: why zig crashes at all.** Nothing here diagnoses that — it is worked around. The
`vswhere` route below is the way off zig entirely, and it now reports why it was skipped rather than
not appearing in the log at all.

### Also found: `build-stub.sh` had drifted and could not produce a working stub at all

The cross-platform route was left behind when the app name moved to a bare token (2026-08-25). It
still passed `-DCRF_APP_NAME="\"$app\""`, so the name arrived **including the quotes** and the stub
went looking for a file literally called `"circuitRF".exe`. It also still used `-mwindows`, which is
a no-op under `zig cc` and yields a CONSOLE stub. Neither shows up on the machine that cuts a
release, because a release is cut on Windows with the `.ps1`. Both fixed, and it now reads the
machine and subsystem back out of the PE exactly as the `.ps1` does.

---

## `wix build` failed with WIX0091 "duplicate Registry" the moment `.cws` was added (2026-08-23)

**Symptom:** `.\packaging\windows\build-windows.ps1` publishes and harvests fine, then stops with four
errors — `WIX0091: Duplicate Registry with identifier 'regw.…'` and `'regFn.…'` — pointing at the
workspace ProgId's first `<Extension>` and, as "the previous error", at its second. No .msi is
produced.

**Not architecture-specific, and there is no second script to fix.** `build-windows.ps1` takes
`-Arch x64|arm64|x86` and compiles the same single `circuitRF.wxs` for all three; the failure is in
that file, so it failed identically on every architecture.

**Cause: the `<Verb>` was declared twice under one ProgId.** The workspace ProgId claims both
spellings, and each extension carried its own copy of the open verb:

```xml
<ProgId Id="circuitRF.Workspace" ...>
  <Extension Id="crfw" ...><Verb Id="open" Command="Open" .../></Extension>
  <Extension Id="cws"  ...><Verb Id="open" Command="Open" .../></Extension>   <!-- the duplicate -->
</ProgId>
```

A `Verb` is registered against the **ProgId, not the extension it is nested in**. It writes
`HKCR\<ProgId>\shell\open` (the friendly name, from `Command`) and
`HKCR\<ProgId>\shell\open\command` — neither key mentions `crfw` or `cws`. Two verbs under one
ProgId are therefore two *byte-identical* rows, and WiX generates a row's identifier from its
root/key/name, so the ids collide. That is the whole of it, and it explains the count: exactly two
duplicates, one of them prefixed `regFn` — the friendly-name row.

**Verified against the real toolset, not reasoned about.** `wix` warns that it "only supports
Windows", but the compile and link stages are managed and do run on macOS — enough to reproduce
WIX0091 exactly, isolate the cause by bisecting the two candidate attributes, confirm the fix links
clean, and dump the linked output's registry table. (`wix build -o out.wixipl` writes a zip
containing `wix-ir.json`; the `Registry` symbols in it are the rows the .msi will carry. What blocks
a full macOS run is only `Directory/@Name` validation, which mis-fires off-Windows.)

**A first attempt blamed `ContentType` and was wrong.** Both extensions also named the same content
type, which looks like the same kind of collision. It is not: `ContentType` writes
`HKCR\.<ext>\Content Type`, which is keyed by the *extension*, so it may safely repeat. Removing it
changed neither the reported identifiers nor their count — the tell that the diagnosis was wrong,
since a fix that touches the cause always moves at least one of the two. The bisect above confirms
it directly: duplicate content type + single verb links clean; single content type + duplicate verb
fails.

**Fix:** declare the verb under the first extension only —
`<Extension Id="cws" ContentType="application/x-circuitrf-workspace" />`, self-closing. Nothing is
lost, which the linked registry table shows: `.cws` still gets its own `HKCR\.cws` default value
naming `circuitRF.Workspace`, and that ProgId's single `shell\open\command` is what Explorer runs.
The dump also confirms the whole table is otherwise sound — all ten extensions registered, all nine
ProgIds carrying an icon and an open verb, no duplicate row anywhere.

**Guard:** `PackagingScriptTests.WindowsInstallerDeclaresEachProgIdVerbOnlyOnce` parses the `.wxs`
and fails if any ProgId contains the same `Verb` id twice. Verified by re-introducing the duplicate:
it goes red and names the ProgId. It exists because `wix build` is the only other thing that
notices, and it runs on Windows, with the WiX toolset installed, at release time — the furthest
possible point from the edit.

**Linux and macOS were checked at the same time and are clean.** macOS packaged without complaint.
On Linux the shape that is illegal on Windows is the *correct and only* representation: a
shared-mime-info `<mime-type>` is a container of globs, so `*.crfw` and `*.cws` under one type is
how it is meant to be written, and there is no per-extension verb to duplicate — the `.desktop`
entry carries one `Exec=` for all nine types. Checked rather than assumed: the mime xml parses, no
`<mime-type>` element repeats, no glob is claimed by two types, the `.desktop`'s `MimeType=` list
and the declared types are the same set of nine in both directions, and `build-linux.sh`, `postinst`
and `postrm` pass `bash -n`.

## Opening a document by double-click: two things were already wrong before anything was added (2026-08-23)

**Asked for:** every document type (`.csch`, `.clay`, `.cdd`, `.csym`, `.ctech`, and `.cem`) should
open from Finder / Explorer / a Linux file manager, the way `.cws`, `.charm` and `.wBond` already did.
Adding them is three declaration files and a dispatcher case each. Looking at those three files first
turned up two defects in what was already there.

### 1. Windows never registered `.cws` — only `.crfw`

`circuitRF.wxs` had one `<Extension Id="crfw">` for the workspace ProgId. The macOS plist and the
Linux mime file had claimed **both** spellings since R-h8-10, and `App.OpenFiles` has always opened
both, for the reason the plist's own comment gives: a workspace saved into a folder is that folder's
`.cws`, and `.crfw` is the standalone spelling. So on Windows every `.cws` on the machine had no
owner and double-clicking one did nothing at all.

Nothing caught it because the "a declared type must be handled" parity test existed for the plist
only. There are now three — one per platform — plus one asserting the three claim the **same set**,
which is the check that would have caught this: a type registered on one platform and not the others
is a file that opens on the developer's machine and not on the user's.

### 2. `src/Ui/linux/` and `packaging/linux/` were two divergent copies, and the tests read the one that never shipped

`build-linux.sh` installs `packaging/linux/circuitrf.desktop` and `packaging/linux/circuitrf-mime.xml`.
`WBondStandaloneTests` read `src/Ui/linux/` — a second set that nothing referenced anywhere else. They
disagreed on:

| | `src/Ui/linux/` (tested) | `packaging/linux/` (shipped) |
|---|---|---|
| harmonicaRF MIME | `application/x-harmonicarf-document` | `application/x-circuitrf-harmonica` |
| wBond MIME | `application/x-wbond-design` | `application/x-circuitrf-wbond` |
| `Exec=` | `/usr/bin/circuitrf %U` | `/opt/circuitrf/circuitRF %F` |
| `postinst` | `xdg-mime` / `xdg-desktop-menu`, three apps | `ln -sf` + `update-*-database`, one app |
| entries | three (`circuitrf`, `harmonicarf`, `wbond`) | one |

**`packaging/linux/` is now the only copy** and the tests read it. The `x-circuitrf-*` names win
because they are the ones that have actually been installed on a machine, and they match the
`ContentType` attributes in the `.wxs`.

**The two extra `.desktop` entries are gone rather than moved, and that is the honest count**:
`build-linux.sh` does one `dotnet publish` with no `CrfApp` loop, so the `.deb` ships **one**
application. A menu entry for harmonicaRF or wBond would launch a binary that is not in the package.
If the `.deb` ever ships all three, `TheLinuxPackageShipsItsDesktopEntry` is where they come back.

### The declaration, in one place per platform

| | file | what one type costs |
|---|---|---|
| macOS | `src/Ui/Assets/macOS/Info.plist` | one `UTExportedTypeDeclarations` dict + one `CFBundleDocumentTypes` dict |
| Windows | `packaging/windows/circuitRF.wxs` | one `<ProgId>` with an `<Extension>` and an `open` `<Verb>` |
| Linux | `packaging/linux/circuitrf-mime.xml` + `circuitrf.desktop` | one `<mime-type>` + one entry in `MimeType=` |

**No build script, install layout or shipped-file set changed.** The six new types are `Editor` role
on macOS, not `Viewer` — unlike `.charm` and `.wBond`, circuitRF is the only application that opens
them, so there is no exported/imported split to make and no Launch Services arbitration to lose.

All six are JSON, and the Linux entries say so with `<sub-class-of type="application/json"/>`: without
it `shared-mime-info` content-sniffs them as `text/plain`, and the sniffed answer can beat the glob's.

---

## `build-linux.sh` dies at its first step: no `libSkiaSharp.so` on Linux (2026-08-21)

**Reported:** `./packaging/linux/build-linux.sh x64` on Linux arm64, at `🎨 Building icons...`:

```
System.DllNotFoundException: Unable to load shared library 'libSkiaSharp' or one of its dependencies.
  .../tools/IconGen/bin/Debug/net10.0/runtimes/linux-arm64/native/libSkiaSharp.so: cannot open ...
   at Svg.Skia.SKSvgSettings..ctor()
   at Program.<Main>$(String[] args) in .../tools/IconGen/Program.cs:line 57
```

**Cause: SkiaSharp does not ship its Linux natives transitively, and never has.** `tools/IconGen`
referenced only `Svg.Skia` 5.2.1, which resolves `SkiaSharp` **4.148.0**, whose own `.nuspec` lists
exactly two native-asset dependencies for `net6.0`/`net9.0`:

```xml
<dependency id="SkiaSharp.NativeAssets.macOS" version="4.148.0" />
<dependency id="SkiaSharp.NativeAssets.Win32"  version="4.148.0" />
```

There is no Linux entry — by design; SkiaSharp expects a Linux consumer to choose between the
fontconfig-linked build and the standalone one. So the package graph put `libSkiaSharp.so` nowhere,
on any Linux. **This is not arm64-specific**: an x64 Linux machine fails identically with `linux-x64`
in the probed paths. It is invisible on Windows and macOS, where the transitive natives are present
and the tool works — which is why it survived to a first Linux packaging run.

**Fix:** `tools/IconGen/IconGen.csproj` names `SkiaSharp.NativeAssets.Linux.**NoDependencies**`
4.148.0, conditioned on the host being Linux.

**Why `.NoDependencies` and not the plain package — measured, not assumed.** Reproduced and fixed in
a bare `mcr.microsoft.com/dotnet/sdk:10.0` arm64 container (same architecture as the report):

| IconGen references | result in a bare container |
|---|---|
| `Svg.Skia` only | `libSkiaSharp.so: cannot open shared object file` — no `.so` anywhere in the output |
| `+ SkiaSharp.NativeAssets.Linux` | `.so` present, and then **`libfontconfig.so.1: cannot open shared object file`** |
| `+ SkiaSharp.NativeAssets.Linux.NoDependencies` | ✓ all three icon sets rendered, no system packages installed |

The plain package's `libSkiaSharp.so` links `libfontconfig.so.1` (confirmed directly — the string is
in the ELF; the `.NoDependencies` build has no such reference). The three brand SVGs contain **zero**
`<text>` elements, so nothing here needs a font at all, and paying for a system dependency to
rasterise pure paths would only move the failure to whichever build machine lacks it.

**Why the reference is host-conditioned** (`Condition="$([MSBuild]::IsOSPlatform('Linux'))"`): the
package is ~192 MB unpacked across 13 Linux RIDs, and every packaging script runs IconGen locally for
the machine it is on, so a Windows or macOS restore would pay that for nothing. Verified both ways —
`SkiaSharp.NativeAssets.Linux` is absent from the macOS `project.assets.json` (the `HarfBuzzSharp`
Linux natives that appear there are its own transitive dependency, unrelated), and all three icon sets
build on Linux arm64.

Gate: `tests/Ui.Tests/PackagingScriptTests.IconGenNamesALinuxNativeSkiaSharp` — nothing else can
notice this from a macOS or Windows CI run.

### Noted in passing, not changed

`Avalonia.Skia` 12.0.3 depends on the **plain** `SkiaSharp.NativeAssets.Linux`, so the shipped
application's own `libSkiaSharp.so` does want `libfontconfig.so.1` at run time — and the app renders
text, so it genuinely wants system fonts, unlike IconGen. `build-linux.sh` now declares **no**
`--depends` at all (see below). A desktop that can run the app almost certainly has fontconfig already,
so this has never been reported; adding `libfontconfig1` to `--depends` would make it explicit rather
than lucky.

---

## A versioned `libicu` dependency makes the .deb uninstallable on the next distribution (2026-08-21)

`sudo apt install ./circuitRF-1.0.0-beta.1-arm64.deb` refused before unpacking anything:

```
 circuitrf : Depends: libicu76 but it is not installable or
                      libicu74 but it is not installable or  … (down to libicu67)
      but none of the choices are installable:
      [no choices]
```

**`[no choices]` is not "ICU is missing" — it is "none of those package NAMES exists here."** ICU bumps
its SONAME every release and the Debian package name follows it, so an alternatives list can only name
the versions that existed the day it was written. A machine shipping `libicu77`/`libicu78` has perfectly
good ICU and still matches nothing in the list, and dpkg alternatives have no wildcard or
version-range form that could express "any libicu" — so the failure was scheduled, not accidental, and
widening the list only moves the date.

**The pin was never what made ICU work.** The package installs a **self-contained** publish, and .NET's
globalization shim `dlopen()`s `libicuuc.so.<N>` across a wide range of N at start-up; it finds whatever
the machine has, with no help from package metadata. So the dependency was removed outright rather than
extended. What replaces it is a **soft check in `postinst`**: if `ldconfig -p` shows no `libicuuc.so.*`
it prints what to install and names `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, and the install still
succeeds — a machine with no ICU gets an app that starts without culture data, which beats an app that
cannot be installed.

Gate: `tests/Ui.Tests/PackagingScriptTests.DebDeclaresNoVersionedIcuDependency` — it reads the `--depends`
lines of `build-linux.sh` (ignoring comments, which discuss the old list on purpose) and fails on any
`libicu<digits>`, and it requires `postinst` to keep the `libicuuc` check, since with no declared
dependency that check is the only thing left that can tell a user what is wrong. Verified to fail on
reintroduction, not just assumed.

---

## `codesign --deep` strips crf-vmhost's virtualization entitlement (2026-08-22)

**Symptom, and where it hides:** an installed circuitRF refuses every compiled device model with

```
crf-vmhost: the virtual machine configuration was rejected: Invalid virtual machine configuration.
The process doesn't have the "com.apple.security.virtualization" entitlement.
```

while the *same* `crf-vmhost`, run out of `tools/macos-vmhost/build/`, starts a VM perfectly. So
`dotnet run` worked throughout development, and **every `.dmg` ever produced shipped a VM host that
could not start** — on both architectures. Found only by running the binary out of the packaged
`.app` rather than out of the build tree.

**Cause.** `bundleFor*MacOS.sh` ended with

```sh
codesign --force --deep --sign "-" --entitlements "$ENTITLEMENTS" --timestamp=none "$BUNDLE_DIR"
```

`--deep` re-signs **every nested executable with the entitlements given on that command line**, and
`$ENTITLEMENTS` is circuitRF's (`com.apple.security.files.user-selected.read-write`). So the deep
pass overwrote the entitlement `tools/macos-vmhost/build.sh` had correctly applied minutes earlier.
Measured, not inferred:

```
$ codesign -d --entitlements - --xml .../build/arm64/crf-vmhost   → com.apple.security.virtualization
$ codesign -d --entitlements - --xml .../circuitRF.app/.../crf-vmhost
                                                  → com.apple.security.files.user-selected.read-write
```

Nothing warns. `codesign` was asked to do exactly this and did it.

**Fix — inside-out, and the ORDER is the whole of it.** After the deep pass: re-sign `crf-vmhost`
with `tools/macos-vmhost/crf-vmhost.entitlements`, then **re-seal the bundle without `--deep`**.

- The re-seal is not optional bookkeeping: the outer signature records each nested binary's cdhash,
  so a bundle whose inner binary changed afterwards fails `codesign --verify --deep --strict`.
- The re-seal must **not** be `--deep`, or it strips the entitlement straight back off — which is
  the same bug wearing a hat.

Verified after the fix: the entitlement is present, `codesign --verify --deep --strict` says *valid
on disk* and *satisfies its Designated Requirement*, and the packaged binary boots the guest and
reaches `CRF-GUEST-READY`.

`PackagingScriptTests.MacBundleScripts_ReSignTheVmHostWithItsOwnEntitlements` holds both halves shut
for all three bundle scripts.

---

## The Intel guest image needs the x86-64 glibc runtime too (2026-08-22)

**Would have been the first Intel build's failure, caught before it shipped.**
`tools/macos-vmimage/build-image.sh` carried the Ubuntu x86-64 glibc runtime into the initramfs
under `if [ "$arch" = aarch64 ]`, and `sources.lock` explained why: *"Only needed on Apple Silicon;
an Intel host runs x86-64 natively against its own guest userland."*

**The second half of that sentence is false.** The guest userland is **Alpine, which is musl** —
on *both* architectures. `senior_worker` and every vendor model library it loads are built against
**glibc** and name `/lib64/ld-linux-x86-64.so.2` as their ELF interpreter:

```
$ file tools/senior-worker/build/senior_worker
ELF 64-bit LSB executable, x86-64, ..., interpreter /lib64/ld-linux-x86-64.so.2
```

Alpine has no such file. On Apple Silicon the runtime went in for Rosetta's sake and the loader came
with it; on Intel nothing would have put it there, and the worker would have died at the dynamic
loader before running a line — a failure naming a loader, not a model.

The two hosts need it for different reasons and **both need it**: Rosetta translates *instructions*
and supplies no libraries (and the native userland is arm64 besides); an Intel guest runs the program
natively, but musl is still not glibc. The guard is gone and both lists in
`tools/macos-vmhost/ensure-built.sh` now include the Ubuntu tarball — listing it for arm64 only made
an Intel machine look cached when it was one download short.

**Adjacent, same shape:** the kernel is per-architecture in FORM as well as content. An aarch64
`Image` is unwrapped from its EFI *zboot* container because `VZLinuxBootLoader` runs no EFI stub; an
x86-64 `bzImage` is handed over **unchanged**, because its self-decompressing stub is part of the x86
boot protocol that boot loader implements. The old single "does this look like a kernel" check
warned about missing arm64 magic on a perfectly good bzImage; it is per-architecture now
(`ARM\x64` at offset 56, `HdrS` at 0x202), and `build-macos.sh` re-checks the same magic in the
finished bundle, because `lipo` knows nothing about a Linux kernel.

---

## "It never used to ask" — Gatekeeper, and what did and did not change (2026-08-22)

**Reported:** after the Intel-packaging work, macOS blocks the app on first launch and the user has
to go to System Settings to allow it. It is believed not to have done that before.

**The signing change was NOT the cause, and that was measured rather than argued.** Two copies of
the same bundle, one signed with the pre-change sequence (a single `codesign --deep` pass) and one
with the new sequence, compared:

```
                    old.app                      new.app
Format              app bundle, Mach-O (arm64)   app bundle, Mach-O (arm64)
CodeDirectory       flags=0x2(adhoc)             flags=0x2(adhoc)
Signature           adhoc                        adhoc
TeamIdentifier      not set                      not set
spctl --assess      rejected                     rejected
```

Identical in every respect Gatekeeper reads. Only the CDHash differs, as it must — the nested
`crf-vmhost` changed.

**What actually decides it.** An ad-hoc signature (`codesign --sign "-"`) has no identity behind it,
so Gatekeeper cannot trust it *however the bundle is formed*. Every build this repository has ever
produced is refused once the file carries `com.apple.quarantine`. The two things that plausibly
changed for the reporter:

1. **macOS 15 (Sequoia) removed the Control-click ▸ Open bypass.** The blocked-launch-then-System
   Settings ▸ Privacy & Security ▸ Open Anyway flow is what replaced it. Everything in this repo
   told users to Control-click, which had quietly become wrong.
2. **Quarantine only attaches to a file that was downloaded.** An app run from `bin/` or copied by
   hand has no quarantine attribute and is never assessed. The first install from a real `.dmg` is
   the first time Gatekeeper is involved at all.

**`LSFileQuarantineEnabled` has nothing to do with any of this**, though the comment beside it in all
three `Info.plist`s claimed it "suppresses the quarantine 'damaged app' dialog for ad-hoc signed
builds". It governs whether files the app *creates or downloads* are quarantined — the flag a browser
opts into. The comment is corrected in place; believing it costs an afternoon.

**The only fix is Developer ID + notarisation**, and it is now a no-edit path: `CRF_SIGN_IDENTITY`
selects the certificate (default `-`, ad-hoc, unchanged), and `CRF_NOTARY_PROFILE` names a
`notarytool store-credentials` keychain profile, at which point `build-macos.sh` signs the disk image,
notarises it and staples the ticket. Setting an identity also turns on the hardened runtime and a
secure timestamp, both of which notarisation requires.

**Two traps found while wiring that up:**

- **A signed-but-unnotarised build is still refused.** The script says so explicitly rather than
  reporting success, because "I signed it and it still asks" is the obvious next report.
- **`--` is illegal inside an XML comment, `plutil -lint` accepts it anyway, and `codesign` does
  not.** Writing `--options runtime` in a comment in `Entitlements.plist` produced
  `Failed to parse entitlements: AMFIUnserializeXML: syntax error near line 19` — a line number and
  no cause. The natural thing to write in a comment about signing is a codesign flag, so this is
  easy to walk into; spell the flags out in prose.
  `PackagingScriptTests.MacPlists_HaveNoDoubleHyphenInsideAComment` holds it. It immediately found
  three dormant instances of the same thing in the `*-Info.plist`s, which survive only because
  nothing hands those files to the strict parser.

**Entitlements the hardened runtime forces.** `Assets/macOS/Entitlements.plist` now declares
`allow-jit` and `allow-unsigned-executable-memory` (.NET's JIT writes and then executes memory),
`disable-library-validation` (`osdi-worker` `dlopen()`s a vendor kit's compiled model, which carries
nobody's signature but its author's) and `allow-dyld-environment-variables`. They are inert in an
ad-hoc build. Declared now rather than at notarisation time because the failure they prevent —
notarises cleanly, then dies at launch or at the first device model — costs a round trip through
Apple's notary service to discover.

**Verified 2026-08-22, once a Developer ID Application certificate existed** (the entitlement set
above was written before one did, so this was the pass that could have found it wrong and did not):
the bundle signs with `flags=0x10000(runtime)` and a secure timestamp, all four .NET entitlements
survive into the signature, `crf-vmhost` keeps `com.apple.security.virtualization` through the
re-sign and re-seal with a real identity, `codesign --verify --deep --strict` passes, the app
launches (so the JIT entitlements are right), and the packaged `crf-vmhost` boots a guest to
`CRF-GUEST-READY`. `spctl -a` then reports exactly the expected remaining gap:

```
rejected
source=Unnotarized Developer ID
```

which is the plainest possible statement that signing alone does not remove the prompt.

**Still not verified here:** the notary service round trip itself (submit / staple), which needs
working credentials.

---

## Do the bundled helper programs need approving separately? No — but a KIT's model library does (2026-08-22)

Asked while fixing the Gatekeeper story, and answered by measurement rather than reasoning, because
every wrong answer here is silent.

**What is actually in `Contents/MacOS/` that macOS could execute:**

| File | Kind | Gatekeeper's involvement |
|---|---|---|
| `circuitRF` / `harmonicaRF` / `wBond` | Mach-O | the app itself |
| `crf-vmhost`, `osdi-worker`, the `.dylib`s | Mach-O | executed/loaded by macOS |
| `senior_worker` | **Linux ELF** | **none, ever** |

`senior_worker` is worth stating plainly: macOS never executes it. It is copied into a directory
shared into the Linux guest and run by Linux, so macOS code signing and quarantine are irrelevant to
it. It carries a quarantine attribute after a download like everything else, and that attribute is
inert.

**A quarantined Mach-O helper is SIGKILLed, silently.** Not prompted, not refused with a message —
exit 137 and nothing on stderr:

```
$ xattr -w com.apple.quarantine '0081;…' ./crf-vmhost && ./crf-vmhost --help
$ echo $?
137
```

**But the gate is the BUNDLE's own quarantine attribute, not each file's.** Measured on clean copies
taken from a quarantined `.dmg`, with the exec order varied to rule out an assessment cache:

| Bundle state | nested `crf-vmhost` |
|---|---|
| quarantined, unapproved | killed (137) |
| bundle's own attribute deleted (what Open Anyway does) | **runs** |
| bundle's attribute kept, `USER_APPROVED` bit (0x0040) set | **runs** |
| `xattr -dr` over the whole bundle | **runs** |

In the middle two cases every helper still carries its own quarantine attribute and runs anyway. So
**one approval of circuitRF covers all of them** — there is no second dialog, and notarising removes
even the first.

**The one case approval does NOT cover: a vendor kit's own native model library.** It is not inside
our bundle, so nothing about signing circuitRF reaches it. If the user downloaded the kit, its
`.osdi` is quarantined and `dlopen` is refused outright:

```
dlopen(…/model.osdi): code signature not valid for use in process:
                      library load disallowed by system policy
```

Two things worth knowing about that refusal:

- **`com.apple.security.cs.disable-library-validation` does not help.** That entitlement relaxes the
  *Team ID* check; this is the quarantine policy, which is separate. Demonstrated with a test binary
  carrying neither the hardened runtime nor library validation — still refused.
- **Both workers print the full `dlerror`**, so the reason does reach stderr rather than becoming a
  bare "model would not load". The remedy is `xattr -dr com.apple.quarantine <kit dir>`, or a kit the
  vendor notarised.

---

## Quarantine and the artwork generators; and the cache that makes this trap easy to mis-measure (2026-08-22)

**Do the Python PCell/artwork generators need the same handling?** Partly, and the split is sharp:

| What a kit ships | Quarantined, on macOS |
|---|---|
| a `.py` generator script | **runs fine** — a script is DATA the interpreter reads; macOS never assesses it |
| a compiled Python extension the script imports | **blocked** — that import is a `dlopen`, and it hits the same wall a compiled device model does |

So `WorkerOutputDiagnosis` is wired into `PCellWorkerProvider.Failed` as well. dyld's refusal arrives
verbatim inside the Python traceback, so the same phrase match works with nothing new to teach it.

**THE CACHING TRAP, which is why this was nearly recorded backwards.** A first measurement appeared
to show that a properly signed loader (python.org's `python3`, Developer ID, notarised) could load a
quarantined library while an ad-hoc one could not — a tidy result, and wrong. The policy decision is
**cached per file**:

```
fresh library, CLEAN, first load      -> allowed
same file, quarantine set afterwards  -> STILL allowed      <- the cache, not a rule about loaders
fresh library, quarantined before use -> BLOCKED
same file, Apple-signed python3       -> BLOCKED
```

The first experiment had loaded the library clean before quarantining it, so it was reading a cached
allow. Re-run with a library quarantined *before its first load*, everything is blocked — ad-hoc
loader, hardened-runtime loader, `disable-library-validation` loader and Apple's own `python3` alike.

**Two lessons worth keeping.** The block is a property of the LIBRARY, not of who loads it: no amount
of signing or notarising circuitRF will make a downloaded kit load, which is exactly why the message
tells the user to clear the kit's attribute rather than implying a better-signed build would help.
And any test of this must use a file that has never been loaded — `cp` on macOS preserves extended
attributes, so "copy a clean one" does not give you a clean one either.

---

## Signing with a paid Apple account: the certificate a paid account does NOT give you (2026-08-22)

`build-macos.sh` now signs if the machine can and builds unsigned if it cannot, resolving the identity
itself instead of requiring one to be typed. The interesting part is what it refuses.

**A paid membership issues "Apple Development" certificates automatically. They are not the ones.**
They appear in the same `security find-identity -v -p codesigning` list as the certificate you need,
and signing a release with one is **worse than ad-hoc**: the bundle looks signed, Gatekeeper still
refuses it, and the notary service rejects it outright. Only a **`Developer ID Application`**
certificate distributes, and it has to be created deliberately (Xcode ▸ Settings ▸ Accounts ▸ Manage
Certificates ▸ + ▸ Developer ID Application). The resolver greps for that exact prefix and treats
anything else as no certificate at all — then says so, naming the trap, because "you have a paid
account and two certificates and it still built unsigned" is otherwise a baffling result.

Resolution order, all of it non-fatal: `CRF_SIGN=never` → ad-hoc; `CRF_SIGN_IDENTITY` → verbatim;
otherwise one Developer ID certificate is used automatically, several are offered as a numbered
choice when the shell is interactive, and **several with no TTY refuses to guess** — the wrong pick
is a release signed by the wrong entity, which is not a thing to decide by sort order.

Notary credentials are handled the same way: the profile (`circuitrf-notary` by default) is looked
for in the keychain, and if it is missing an interactive run offers to create it by handing off to
`xcrun notarytool store-credentials`, which does its own secure prompting. **This script never reads,
holds or echoes a password**, and none of it reaches shell history. The credential is an
APP-SPECIFIC password from appleid.apple.com, never the account password.

---

## notarytool's 401, and the Team ID that is not the Team ID (2026-08-22)

**Reported:** `Error: HTTP status code: 401. Invalid credentials. Username or password is incorrect.
Use the app-specific password generated at appleid.apple.com.` — with the operator unsure what the
"Developer Apple ID" was and confident the Team ID was the easy part.

**The Team ID was the least safe of the three.** `security find-identity -v -p codesigning` shows

```
Apple Development: someone@example.com (5K57RC984E)
```

and the bracketed value is **not** a Team ID on an *Apple Development* certificate — it is a
per-certificate identifier. The Team ID is the certificate's **`OU`** field, which is a different
string entirely:

```
/UID=PW66EES55M/CN=Apple Development: someone@example.com (5K57RC984E)/OU=74Y39278RS/O=Someone/C=US
                                                          ^^^^^^^^^^ not this        ^^^^^^^^^^ this
```

On a **Developer ID Application** certificate the two ARE the same string, which is precisely why
this is easy to get wrong and hard to doubt: every example anyone has seen agrees with the wrong rule.

**All three values are now read off the machine and printed** at the point they are asked for
(`crf_apple_team_hints` / `crf_apple_id_hints` in `build-macos.sh`), filtered to unexpired certificates
— an expired one names a team the account may no longer be in, so suggesting it would be worse than
suggesting nothing. On this machine that resolves to two real teams (an individual and an LLC), which
is itself worth seeing: with two teams, "the Team ID" is not even a single answer.

**The 401 itself is almost always the password's KIND, not its content.** It must be an APP-SPECIFIC
password from appleid.apple.com (Sign-In and Security), available only once the account has
two-factor authentication; the Apple ID account password returns this 401 forever and the message
never says so. The failure path now names that first, then the two runner-up causes (an
app-specific password made on a *different* Apple ID; a mistyped or revoked one), and gives the
standalone `store-credentials` command so credentials can be iterated on without paying for a build
each time.

---

## An installed app rendered no PDK artwork: two independent defects (2026-08-22)

**Symptom.** A signed, notarized, downloaded and installed `/Applications/circuitRF.app`: create a
workspace, add a PDK (IHP `ihp-sg13g2`), and every one of the kit's 34 layout cells draws as a
placeholder. The kit itself imported perfectly — 110 placeable parts, 110 symbols, "34 parametric
layout cell(s)", technology read, 4 compiled models found. Only the artwork was missing. The same
workspace under `dotnet run` is fine, which is the tell that this is a packaging-shaped bug and not a
kit-shaped one.

Two separate causes, either of which alone is fatal to generated artwork.

### 1. circuitRF's own Python package was never shipped

The Messages panel carried the whole answer:

```
The PCell generator '…/kit_entry.py': The PCell generator closed its output before sending a reply.
--- generator output ---
  File "…/kit_entry.py", line 7, in <module>
    import circuitrf_pcell as crf
ModuleNotFoundError: No module named 'circuitrf_pcell'
```

`PCellPythonPackage.Locate()` looks for the package **beside the executable** and, failing that,
walks up for a `tools/pcell-python` source tree. Nothing ever copied it into the build output, so
**only the second branch had ever run** — and it always succeeds in a development tree and can never
succeed in an installed app. `find /Applications/circuitRF.app -iname '*pcell*'` returned one thing:
a documentation page.

This is the same class of bug as `CrfPublishHelperPrograms` in `src/Ui/CircuitRF.Ui.csproj`, whose
own comment already describes it ("a kit that evaluates fine under `dotnet run` and refuses on an
installed copy"), and it is fixed the same way: an item group that copies
`tools/pcell-python/**/*.py` to `pcell-python/` in the output and the publish tree.

Two details in that item are load-bearing:

- **The glob is rooted at `pcell-python/`, not at each package.** `%(RecursiveDir)` begins *after*
  the `**`, so a per-package `Include` flattens `circuitrf_pcell/__init__.py` to `__init__.py` — a
  directory of loose modules, which is not an importable package and would fail identically.
- **`*.py` only**, which is also what leaves `__pycache__` behind.

**No packaging script needed changing, and this was checked rather than assumed** — all three
packagers take the whole publish tree: `bundleForMacOS.sh` does `cp -R "${PUBLISH_DIR}/."`,
`build-linux.sh` hands fpm `"${PUBLISH}/=/opt/circuitrf/"`, and `build-windows.ps1`'s `Add-Directory`
harvester recurses subdirectories generically into `Files.wxs`.

The gate is `PCellVendorBridgeTests.ThePythonPackageIsShippedBesideTheExecutable_NotFoundBySourceTreeWalkUp`,
which asserts `PCellPythonPackage.RootDirectory` **equals** `AppContext.BaseDirectory/pcell-python`.
The pre-existing test beside it (`CircuitRfFindsItsOwnPythonPackage`) passes under either branch,
which is exactly how this shipped.

### 2. A bundled macOS app resolves `python3` to Apple's frozen 3.9 stub

Fixing (1) alone would have moved the failure, not removed it. `PythonInterpreterDiscovery` tries
PATH first, documented as "it is what the user's own shell would run". **In an application launched
from the Finder that premise is false**: the process inherits `/usr/bin:/bin:/usr/sbin:/sbin` and
nothing of the login shell, so `python3` means `/usr/bin/python3` — Apple's Command Line Tools stub,
frozen at 3.9.6 — and never the 3.13 the same machine has in `/usr/local/bin`. The log recorded the
choice plainly: `Using Python 3.9.6 for generated artwork (found on PATH: python3)`.

3.9 clears the discovery floor (which is what circuitRF's *own* package needs) and then cannot
**parse** a kit that uses anything newer. Measured on sg13g2: its cells use `match`, so under 3.9.6
registration returns `Could not import 'sg13g2_pycell_lib.ihp': invalid syntax (res_base_code.py,
line 61)` and **zero** generators; under 3.13.1, **34** generators and one unrelated vendor-side
problem (`inductors` has no `model` attribute).

That message is also precisely the failure mode `MinimumMinor`'s own comment says the version floor
exists to prevent — "refused by version rather than allowed to fail later on syntax, which reads as a
broken kit". The floor was doing its job; the candidate ORDER was not.

**Fix: the stub is demoted to last resort, and only when it is an IMPLICIT choice.**
`IsImplicitlyAppleCommandLineToolsPython` is true only for a *bare name* that PATH resolves to
`/usr/bin/python3`, so:

- a kit manifest's `interpreter`, or a `.cws` recording the absolute path, is a deliberate statement
  and is honoured;
- a virtual environment is untouched — its `python3` is in the environment's own `bin`;
- on a machine that has nothing else, `/usr/bin/python3` is still reached, as the last candidate.

**The recorded choice is demoted by the same rule**, and that half matters as much as the candidate
list: the `.cws` here already said `"PythonInterpreter": "python3"`, written by a session with a full
PATH. Replaying a *name* is only sound while the name means the same thing, so a bare record that
would now land on the stub is re-derived. Without this the workspace stays broken after the app is
fixed.

PATH is threaded through `Find`/`Candidates` as an optional `pathVariable` rather than read only from
the environment — the Finder case is then testable without mutating the process environment out from
under a parallel test run.

### 3. A `SyntaxError` from a kit now names the interpreter that parsed it

`cni/bridge.py`'s `_interpreter_note` appends the running version and `sys.executable` to a
`SyntaxError` only. "invalid syntax (res_base_code.py, line 61)" reads as a broken kit and sends the
reader to the vendor; the same line ending "parsed by Python 3.9.6
(/Library/Developer/CommandLineTools/usr/bin/python3)" names the actual problem. Stated as a fact
about which interpreter ran, not as a diagnosis — a genuine syntax error in a kit is still possible,
and the version is the piece the reader cannot otherwise see.

**Still true, and not a bug:** a machine whose only Python is Apple's 3.9 stub cannot generate this
kit's artwork. circuitRF bundles no interpreter and installs no packages. What changed is that the
message now says so.
