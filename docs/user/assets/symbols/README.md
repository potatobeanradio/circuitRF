# Component symbol SVGs

Each standard-library component's documentation embeds its schematic **symbol** as an SVG from this
folder, named after the component: `resistor.svg`, `inductor.svg`, `capacitor.svg`, …

Each component produces **two** files — a light-mode and a dark-mode SVG, e.g. `resistor.svg` and
`resistor-dark.svg`. The documentation carries both and swaps them by `prefers-color-scheme` (the same
way the header logo swaps), so symbols are legible in either theme.

Every file here is **generated**, carries a banner saying so, and will be overwritten. There is no
manual fallback any more: a hand-made file is a file nothing keeps in step with the application, which
is what `fet.svg` was before it was deleted.

## What a symbol figure shows

The glyph, **its connection leads, and its pins drawn in the UNCONNECTED state** — the component as you
meet it in the palette, before anything is wired to it. Pins come from the schematic renderer's own
`DrawPortMarkers`, and the variadic port stubs from its own `DrawVariadicPortLeads`, so a documentation
figure and the canvas cannot disagree about what a component looks like.

## Regenerate ALL symbols with one command (the source of truth)

The artwork is drawn by the **live circuitRF rendering engine** — the same
`SchematicRenderer.DrawSymbol` the app uses — so the docs never drift from the app. From the repo root:

```
dotnet run --project src/Ui -- --generate-symbols docs/user/assets/symbols
```

No GUI window opens. This is also run for you as one step of a full documentation regeneration:

```
dotnet run --project tools/DocGen -- --out docs/user
```

which additionally captures the UI figures, the toolbars and their manifests, extracts the fonts and
rebuilds every page. See `docs/design/user-docs-factory.md`.

## Documenting a new component

Add one row to `Catalog` in `src/Ui/Diagnostics/SymbolArtworkGenerator.cs`:

```csharp
(SymbolKind.Xxx, "file-stem", representativePortCount),
```

then re-run. Nothing else changes — and until you do,
`DocsFactoryTests.EveryUserPlaceableSymbolKindHasASymbolCatalogRow` fails, so a new component cannot
quietly ship undocumented. The only two kinds deliberately excluded are `Generic` and `Unknown`, which
a user cannot place; widening that list is a visible edit to `SymbolArtworkGenerator.NotUserPlaceable`.

## Naming map (SymbolKind → file stem)

Generated from the catalog, in catalog order.

| SymbolKind | File stem |
|---|---|
| Resistor | `resistor` |
| Inductor | `inductor` |
| Capacitor | `capacitor` |
| NonlinearC | `nonlinear-c` |
| Vdc | `vdc` |
| ToneSource | `tone-source` |
| P1Tone | `p1tone` |
| PnTone | `pntone` |
| Ground | `ground` |
| Term | `term` |
| TermG | `termg` |
| Pin | `pin` |
| IProbe | `iprobe` |
| Tline | `tline` |
| Mutual | `mutual` |
| Snp | `snp` |
| ZPort | `zport` |
| Sdd | `sdd` |
| Tuner | `tuner` |
| SourceTuner | `source-tuner` |
| LoadTuner | `load-tuner` |
| Var | `var` |
| Meas | `meas` |
| Diode | `diode` |
| Match | `match` |
| Mlin | `mlin` |
| MBend | `mbend` |
| MTee | `mtee` |
| MCross | `mcross` |
| Mtaper | `mtaper` |
| Mklopf | `mklopf` |
| VerilogA | `verilog-a` |
| WBond | `wbond` |
| FetCurtice | `fet-curtice` |
| FetCurticeCubic | `fet-curtice-cubic` |
| FetStatz | `fet-statz` |
| FetMaterka | `fet-materka` |
| FetAngelov | `fet-angelov` |
