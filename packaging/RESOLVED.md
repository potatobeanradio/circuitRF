# packaging — resolved findings

Findings from packaging work that would otherwise have to be rediscovered. Each entry names the
symptom first, because that is what the next person will have in front of them.

---

## `build-deb.sh` dies at its first step: no `libSkiaSharp.so` on Linux (2026-08-21)

**Reported:** `./packaging/linux/build-deb.sh x64` on Linux arm64, at `🎨 Building icons...`:

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
text, so it genuinely wants system fonts, unlike IconGen. `build-deb.sh` now declares **no**
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
lines of `build-deb.sh` (ignoring comments, which discuss the old list on purpose) and fails on any
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
(`ARM\x64` at offset 56, `HdrS` at 0x202), and `build-dmg.sh` re-checks the same magic in the
finished bundle, because `lipo` knows nothing about a Linux kernel.
