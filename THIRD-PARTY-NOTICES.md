# Third-party notices

circuitRF's own source code is released under the MIT License (see [`LICENSE`](LICENSE)). That grant
covers the code in this repository and nothing else. The distribution — and the installers built from
it — also contains third-party components under their own terms, listed here.

**Two of these are copyleft**, one weakly and one at file scope. Neither restricts circuitRF's own
MIT licensing, but both carry obligations that travel with any binary you redistribute, so they are
listed first and in full.

---

## 1. CSparse.NET — LGPL-2.1-only

| | |
|---|---|
| **Component** | CSparse.NET 4.3.0 |
| **Copyright** | Christian Woltering © 2012–2025 |
| **Licence** | GNU Lesser General Public License, version 2.1 **only** (not "or later") |
| **Licence text** | [`licenses/LGPL-2.1.txt`](licenses/LGPL-2.1.txt) |
| **Source** | https://github.com/wo80/CSparse.NET |
| **Used by** | `src/Engine` — sparse complex LU for MNA, harmonic balance, S-parameters, and the AIM accelerator |

### What this means if you redistribute a circuitRF binary

circuitRF's packaged installers are built with `SelfContained` and `PublishSingleFile`, so
`CSparse.dll` is bundled into the published host rather than sitting beside it as a separate,
replaceable file. LGPL-2.1 §6 requires that whoever receives such a combined work be able to modify
CSparse.NET and relink it into a working program.

**That requirement is satisfied here by publication of complete source.** Everything needed to
substitute a modified CSparse.NET and rebuild circuitRF is in this repository: change the
`PackageReference` in `src/Engine/CircuitRF.Engine.csproj` — or drop in a modified assembly — and run
the build described in [`BUILDING.md`](BUILDING.md). No part of circuitRF is withheld, obfuscated, or
distributed in a form that would prevent relinking.

If you redistribute circuitRF binaries yourself, you inherit that obligation: ship this notice, ship
the LGPL text, and either accompany the binaries with the source or point recipients at it.

CSparse.NET is used through a narrow interface (five files: `MnaSystem`, `NonlinearDcEngine`,
`SParameterEngine`, `HbLinearExtractor`, `PlanarAim`), so a distribution that cannot accept an LGPL
component can replace it without touching the rest of the engine.

---

## 2. OSDI header — MPL-2.0

| | |
|---|---|
| **Component** | `osdi.h`, the OSDI ABI header from the ngspice OSDI component |
| **Copyright** | © 2022 SemiMod GmbH |
| **Licence** | Mozilla Public License 2.0 |
| **Licence text** | [`licenses/MPL-2.0.txt`](licenses/MPL-2.0.txt) |
| **In this repo** | [`tools/osdi-worker/osdi.h`](tools/osdi-worker/osdi.h) — vendored verbatim |

MPL-2.0 is copyleft at **file** scope. The file may sit inside an MIT-licensed project, which is
exactly what MPL §3.3 contemplates, but the file itself remains under the MPL and **may not be
relicensed under MIT**. Its header comment is part of the licence and must not be removed or altered.
Modifications to that file, if any are ever made, stay under the MPL and their source must be made
available.

Nothing else in `tools/osdi-worker/` is derived from ngspice: `osdi_worker.c` is first-party code
written against the published ABI this header describes.

---

## 3. Permissively licensed components

None of these impose obligations beyond retaining their notices.

### Libraries

| Component | Licence | Project |
|---|---|---|
| Avalonia (`Avalonia`, `.Desktop`, `.Skia`, `.Themes.Fluent`, `.Controls.ColorPicker`, `.Fonts.Inter`, `.Headless`) | MIT | https://avaloniaui.net/ |
| SkiaSharp (+ `NativeAssets.Win32`) | MIT | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Dock.Avalonia (+ `Model.Mvvm`, `Themes.Fluent`) | MIT | https://github.com/wieslawsoltes/Dock |
| Material.Icons.Avalonia | MIT | https://github.com/SKProCH/Material.Icons |
| NumFlat | MIT | https://github.com/sinshu/numflat |
| FftFlat | MIT | https://github.com/sinshu/FftFlat |
| PureHDF | MIT | https://github.com/Apollo3zehn/PureHDF |
| Svg.Skia | MIT | https://github.com/wieslawsoltes/Svg.Skia |
| Clipper2 | Boost Software License 1.0 | https://github.com/AngusJohnson/Clipper2 |
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| Svg (svg-net) | Microsoft Public License (MS-PL) | https://github.com/svg-net/SVG |
| xunit, Microsoft.NET.Test.Sdk, coverlet.collector | MIT / Apache-2.0 | *(test-time only; not shipped)* |

`Svg` (MS-PL) and `Svg.Skia` are used by `tools/IconGen`, which rasterises the committed brand SVGs
into the `.icns`/`.ico`/`.png` containers at packaging time. `IconGen` is not part of
`circuitRF.slnx` and neither package ships inside the application.

### Fonts

| Font | Licence | Licence text in repo |
|---|---|---|
| IBM Plex Sans — © 2017 IBM Corp., reserved font name "Plex" | SIL Open Font License 1.1 | [`src/Ui/Assets/Fonts/IBM_Plex_Sans/OFL.txt`](src/Ui/Assets/Fonts/IBM_Plex_Sans/OFL.txt), [`docs/user/assets/fonts/OFL.txt`](docs/user/assets/fonts/OFL.txt) |
| Inter — © The Inter Project Authors | SIL Open Font License 1.1 | [`docs/user/assets/fonts/OFL.txt`](docs/user/assets/fonts/OFL.txt) |
| DejaVu Sans — derived from Bitstream Vera, © 2003 Bitstream Inc.; Arev glyphs © Tavmjong Bah | Bitstream Vera Fonts License | [`src/Ui/Assets/Fonts/DejaVu Fonts License.txt`](src/Ui/Assets/Fonts/DejaVu%20Fonts%20License.txt), [`docs/user/assets/fonts/DejaVu Fonts License.txt`](docs/user/assets/fonts/DejaVu%20Fonts%20License.txt) |

Both the OFL and the Bitstream Vera licence carry a **reserved font name** clause: a modified version
of any of these faces must be distributed under a different name. circuitRF embeds them unmodified.

---

## 4. Build-time downloads (not redistributed)

`tools/macos-vmimage/build-image.sh` downloads pinned Alpine Linux and Ubuntu base images to
construct the Linux guest that runs Linux-only device workers on macOS. Those images are fetched by
checksum at build time on the user's own machine and are **not** contained in this repository or in
any circuitRF installer, so no redistribution obligation attaches to their contents. The pinned
versions and hashes are in [`tools/macos-vmimage/sources.lock`](tools/macos-vmimage/sources.lock).

The same is true of the cross-compiler images `tools/senior-worker/build.sh` and
`tools/netlist-worker/Dockerfile` pull on demand.

---

## 5. Test data

`tests/RfCore.Tests/testdata/2SC5226A.s2p` is manufacturer-published small-signal S-parameter data
for a commercially available transistor, used as a Touchstone parser fixture.

The loadpull and contour fixtures under `testdata/spl_test_data/` and `testdata/lpwave_test_data/`
are third-party measured data that **cannot be redistributed** and have never been committed to this
repository. Tests that read them report as *Skipped* with a reason; a fresh clone is fully green
without them.

---

*If you believe a component is listed incorrectly or is missing, please open an issue — corrections
to this file are always in scope.*
