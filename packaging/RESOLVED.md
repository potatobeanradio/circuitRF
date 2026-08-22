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
text, so it genuinely wants system fonts, unlike IconGen. `build-deb.sh`'s `--depends` names only the
`libicu*` alternatives. A desktop that can run the app almost certainly has fontconfig already, so this
has never been reported; adding `libfontconfig1` to `--depends` would make it explicit rather than
lucky.
