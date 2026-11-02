# Sonnet Brief — RND-1: the render layer below the UI firewall

**Read `brief-render-0-overview.md` first.** This brief implements R-rnd0-1 and R-rnd0-3. It adds no
verb, no flag and no capability: when it is done the application looks and behaves exactly as it did,
and `src/Cli` is able to draw. Every visible feature is RND-2 onward.

Precedents to follow rather than re-derive: `brief-cli-em-verb.md` (the `src/Design` carve-out),
`brief-automation-2-schematic-below-the-firewall.md` (R-aut2-3 — whole files, no reshaping) and
`brief-automation-4-check-and-explain.md` R-aut4-3 (the DRC engine's move, including its
stop-and-report clause).

---

## 1. The project

```
src/Render/CircuitRF.Render.csproj      →  Core, Engine, Design, RfCore, SkiaSharp
src/Ui/CircuitRF.Ui.csproj              →  + CircuitRF.Render
src/Cli/CircuitRF.Cli.csproj            →  + CircuitRF.Render
tests/Firewall.Tests/UiFirewallTests.cs →  + { "CircuitRF.Render", "CircuitRF.Render.dll" }
circuitrf.slnx                          →  + the project
```

**R-rnd1-1. The namespace changes with the project.** `CircuitRF.Ui.Renderers` inside a non-UI
assembly would lie about the architecture forever — R-emcli-4's rule, and the reason the DRC engine
became `CircuitRF.Design.Layout.Drc` rather than keeping its old name. The new namespace is
`CircuitRF.Render`. The resulting `using` churn is **one line in `src/Ui/GlobalUsings.cs`**, which
is what that file exists for; read its header comment before adding to it.

**R-rnd1-2. Whole files, moved, not rewritten.** R-aut2-3. `git mv` and fix the namespace. A
renderer that is refactored on the way down cannot be gated by §5's byte-identity tests, because
there is nothing left to compare against.

---

## 2. What moves, and the measurement that says it is cheap

Measured before this brief was written (`grep` over `src/Ui/Renderers`, not assumed):

| File(s) | Avalonia? | Notes |
|---|---|---|
| `SchematicRenderer.cs` (1,631) | none | `using SkiaSharp` + the design model |
| `LayoutRenderer*.cs` (6 files, ~5,800) | none | + `CircuitRF.Engine.Mom` for the mesh insets |
| `SymbolEditorRenderer.cs` (528) | none | |
| `LayoutRenderTheme.cs`, `SchematicRenderTheme.cs`, `LayerFillPaint.cs` | none | |
| `LayoutRenderDetail.cs`, `LayoutPathCache.cs`, `BitmapCache.cs`, `LayoutRulerRenderer.cs` | none | |
| `SkiaFonts.cs` | **`Avalonia.Platform`** | §3 |
| `ComponentPreviewRenderer.cs` | **`Avalonia`, `Media.Imaging`, `Platform`** | §4 |
| `UiTypefaceInstaller.cs` | module initializer for `src/Ui` | stays |
| `HarmonicaRenderTheme.cs`, `WBondRenderer.cs` | none | move too — same reasons, and they are neighbours |

The renderers reach four `src/Ui` namespaces. Measured by type, not by `using` line:

- `CircuitRF.Ui.Theming` — `ThemeService`, `ColorTheme`, `ColorRole`, `ColorVariant`,
  `ThemeResolver`, `ClipboardRenderPolicy`, `AppPreferences`. **Only `ThemeResolver` names Avalonia**,
  and only through an *already-injected* `Func<string, ColorTheme?>` provider seam. `ColorTheme.cs`
  says in its own header that it could migrate. Move the whole folder except `AppPreferences`
  (a per-user preference store, which is `src/Ui`'s business) — see §3.
- `CircuitRF.Ui.Layout` — `LayoutOverlay` (4 uses), `LayoutHitTest` (6), `CellPins` (2),
  `LayoutHandles` (1), and the handle marker types `LayoutOverlay` carries. **These are editor
  overlay types, and every export path already passes `Overlay = null`.**
- `CircuitRF.Ui.Layout.PCells` — reached ONLY through `LayoutOverlay.PCellHandlePreview` and
  `PCellHandleMarker`. No renderer names `PCellRegistry`, `PCellGeometryCache` or
  `GeneratedCellStore` — verified by grep. **A generated PCell cell is an ordinary cell folder on
  disk** (`GeneratedCellStore.ReservedFolderName`), so an instance of one resolves through
  `CellLayoutResolver` like any other and no generator runs at render time.
- `CircuitRF.Ui.Schematic` — `EditableSymbol` and the schematic overlay types. `EditableComponent`,
  `EditableWire` and `EditableSchematic` are **already** in `src/Design`.

**R-rnd1-3. Take the overlay types down with the renderers, and take nothing else.** They are
persisted-model-free value types describing a frame's transient chrome. If the closure turns out to
be larger than this table — if `LayoutOverlay` drags a view model, a command, or a canvas behind it —
**stop and report exactly what blocked it.** R-aut4-3's clause, applied here. A `src/Render` that
draws committed geometry and refuses to draw a selection marquee is a completely adequate outcome for
this series; a `src/Render` that pulled a canvas below the wall is not.

---

## 3. Fonts and themes: the two silent-fallback traps, and why both must be fixed here

Both are the same shape of bug, both are already documented in this repo as having bitten, and
neither surfaces as an error.

**R-rnd1-4. The fonts must become ordinary embedded resources.** `SkiaFonts.Load` goes through
`Avalonia.Platform.AssetLoader`, which throws `Unable to locate 'Avalonia.Platform.IAssetLoader'`
with no live app host — so it falls back to `SKTypeface.Default`. That fallback is correct for
`LayoutTextOutline`'s flattening (a label's glyph outlines, where the repo already reports the
substitution rather than hiding it) and **completely unacceptable here**: a rendered schematic whose
text is a different typeface at different metrics is a *different picture*, and §5's byte-identity
gate would have to be weakened to accept it.

`SKTypeface.FromStream` over `Assembly.GetManifestResourceStream` needs no platform at all. This is
the same fix `ShippedTechnologies` and the `reference` pages already use, for the same reason, and
`src/Design/CircuitRF.Design.csproj`'s comments state it twice.

- Move `src/Ui/Assets/Fonts/**` to `src/Render/Assets/Fonts/**` as `<EmbeddedResource>`.
- **`src/Ui` keeps its `avares://CircuitRF.Ui/Assets/Fonts/…` URIs working by linking the same
  files back**, not by keeping a second copy:
  `<AvaloniaResource Include="..\Render\Assets\Fonts\**" Link="Assets\Fonts\%(RecursiveDir)%(Filename)%(Extension)" />`.
  Two copies of a font file is two files that will be updated on one side only, and nothing would
  report the drift. Note that `src/Ui`'s existing include is a blanket `Assets\**`, so the link must
  be added deliberately — check the published output actually contains them.
- `SkiaFonts.TestOverrideTypeface` **stays**, and its doc comment gains the note that production and
  the CLI now both take the real face. `LayoutTextOutline.TypefaceSource` stays exactly as it is:
  it is a *different* seam serving a *different* consumer, and `convert`'s behaviour must not change
  in this brief. If wiring it to the new embedded loader would make `convert`'s label flattening
  match the GUI's, **say so and leave it** — that is a real finding and a one-line follow-up, not
  this brief's business.

**R-rnd1-5. The built-in `.ccolor` themes must resolve headlessly, or `ThemeResolver` lies.** Its
chain is workspace dir → user themes dir → built-in provider → `ColorTheme.BuiltIn`, and step 3 is
`SetBuiltInProvider`, registered by `App.axaml.cs` from `avares://CircuitRF.Ui/Assets/Color/*.ccolor`.
With no app host that provider is simply never installed, so **a theme name that resolves in the GUI
falls silently through to `BuiltIn`** — a different picture, reported as a success. Move
`src/Ui/Assets/Color/*.ccolor` on the fonts' terms (embedded here, linked back for XAML) and give
`ThemeResolver` a framework-free default provider that reads its own assembly. **That folder holds
exactly one file, `Default.ccolor`** — `ThemeResolver.DefaultThemeName`'s own comment records that the
six `wBond-…` palettes were folded into it and deleted, so do not go looking for a set. `App.axaml.cs` may
then stop registering one; if it must keep doing so for a reason the move exposes, say why.

**The `.ccolor` reader itself is already framework-free** — `ColorThemeIo` is `System.Text.Json`
over `Rgba`, and `Rgba` is already in `src/Design/Theming`.

---

## 4. `ComponentPreviewRenderer` — the seam, not the file

It is the closest thing in the repo to what RND-2 needs: fit-to-box, pad markers, a solved fit that
converges downward, off the UI thread already. But it returns an `Avalonia.Media.Imaging.Bitmap`.

It is **already split** for exactly this: `RasterSymbol` and `RasterFootprint` produce an `SKBitmap`
"with no Avalonia in it — which is what lets a headless test measure where the drawing actually
landed", and `Wrap` is the two-line Avalonia adapter over them.

**R-rnd1-6. The `SKBitmap` half moves; the `Wrap` half stays.** `CircuitRF.Render` gets the raster
functions; `src/Ui` keeps a thin `ComponentPreviewRenderer` holding only `Wrap` and the two public
`Render*` methods that call it. This is the same shape as the DRC move (the engine went, the two
files that post to the Messages panel and read a per-user preference stayed).

---

## 5. Gates

1. **The firewall row.** `CircuitRF.Render.dll` joins `UiFirewallTests.NonUiAssemblies`. This is the
   assertion the whole brief exists to be able to make; it must be present and passing before
   anything in RND-2 is written.
2. **Byte identity, per renderer, per format.** For a fixture schematic, symbol and layout, the SVG
   and PDF produced by calling `CircuitRF.Render` from a project with no Avalonia reference are
   compared **byte for byte** against what `SchematicClipboard.TryRenderToSvg`/`TryRenderToPdf`,
   `SymbolClipboard` and the layout clipboard export produce in-process. Same theme, same variant,
   same viewport, same `excludeGrid`.
   - **PDF carries a creation date.** `SKDocument.CreatePdf` writes `SKDocumentPdfMetadata`; if the
     two files differ only in that field, exclude *that field by name* and say so — the `em` verb's
     provenance-timestamp exclusion is the precedent and the standard. If they differ anywhere else,
     that is a finding, not an exclusion.
   - **This gate is only meaningful once R-rnd1-4 lands.** Write it first and watch it fail on the
     typeface, then fix the fonts. A gate that was written after the workaround proves nothing.
3. **The font is the real font.** Assert `SkiaFonts.DejaVuRegular.FamilyName` (and the Plex faces)
   from a headless test with no Avalonia platform, and assert it is NOT `SKTypeface.Default`'s.
4. **The theme is the real theme.** From the same kind of test, resolve `"Default"` through
   `ThemeResolver` and assert it came from the shipped `.ccolor` — not from `ColorTheme.BuiltIn`.
   Then assert the two agree, which is a standing claim `src/Ui/CLAUDE.md` already makes and nothing
   currently checks headlessly.
5. **Nothing in the application changed.** Full `dotnet test` green, and specifically
   `tests/Ui.Tests` — the renderers' existing tests move with them or keep passing where they are.
   Read the TRX; do not re-run to find out what broke (repo `CLAUDE.md`).
6. **The published app still has its fonts.** `dotnet publish` `src/Ui` and assert the `avares` URIs
   still resolve — R-rnd1-4's link is exactly the kind of build-file change that works in `dotnet
   run` and fails in a bundle, which is how `tools/pcell-python` shipped broken once
   (`packaging-installed-app-no-pcell-artwork`).

---

## 6. On completion

Findings go to **`src/Render/RESOLVED.md`** (create it) and `src/Ui/RESOLVED.md` for what changed on
that side — **never a `CLAUDE.md`**. Update `docs/design/ui-architecture.md` §3 with the new
assembly and its one-way arrow, and the repo-root `CLAUDE.md`'s source map with `src/Render` —
that file is standing project memory and a fifth project not named in it is a fifth project nobody
finds.

**Report, do not silently absorb:**
- The measured closure (§2) against the table's prediction, whether it was larger or smaller.
- Whether `LayoutOverlay` came down cleanly (R-rnd1-3), and if not, precisely what blocked it and
  what `src/Render` therefore cannot draw.
- Whether the PDF comparison needed a metadata exclusion, and which field.
- Whether wiring `LayoutTextOutline.TypefaceSource` to the new embedded loader would change
  `convert`'s output — a yes is a finding about a shipped verb, and it is not this brief's to fix.
