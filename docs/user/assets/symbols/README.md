# Component symbol SVGs

Each standard-library component's documentation embeds its schematic **symbol** as an SVG from this
folder, named after the component: `resistor.svg`, `inductor.svg`, `capacitor.svg`, … The Reference
Guide references them by relative path, e.g. `../assets/symbols/resistor.svg`.

Each component produces **two** files — a light-mode and a dark-mode SVG, e.g. `resistor.svg` and
`resistor-dark.svg`. The documentation embeds both and swaps them by `prefers-color-scheme` (the same way
the header logo swaps), so symbols are legible in either theme.

## Regenerate ALL symbols with one command  (the source of truth)

The artwork is drawn by the **live circuitRF rendering engine** — the same `SchematicRenderer.DrawSymbol`
the app uses — so the docs never drift from the app. From the repo root:

```
dotnet run --project src/Ui -- --generate-symbols docs/user/assets/symbols
```

This renders every catalogued component (glyph + its baked-in text + the display-name caption) to a
light and a dark SVG in this folder, in one pass. **circuitRF is alpha and symbols change — just re-run
this whenever a glyph changes; every doc image updates at once.** No GUI window opens.

The generator is `src/Ui/Diagnostics/SymbolArtworkGenerator.cs`, invoked from `src/Ui/Program.cs`
(`--generate-symbols`). To document a **new** component, add one row to the `Catalog` table in that file:

```csharp
(SymbolKind.Xxx, "file-stem", representativePortCount),
```

then re-run the command. (The `<!-- NEW COMPONENT -->` marker in the file shows where.)

## Manual fallback (copy/paste from the app)

If you need a one-off symbol without running the generator: place the component in a schematic, select it,
**Copy** (`Ctrl/⌘+C`) — circuitRF puts a vector SVG on the clipboard — and save it here as
`<component>.svg`. Prefer the generator for anything you'll keep, so light/dark stay consistent.

## Naming map (SymbolKind → file)

| Component (display) | File |
|---|---|
| R (Resistor)        | `resistor.svg` |
| L (Inductor)        | `inductor.svg` |
| C (Capacitor)       | `capacitor.svg` |
| NonlinearC          | `nonlinear-c.svg` |
| Vdc                 | `vdc.svg` |
| VTone (ToneSource)  | `tone-source.svg` |
| P1Tone              | `p1tone.svg` |
| GND (Ground)        | `ground.svg` |
| Term                | `term.svg` |
| Pin                 | `pin.svg` |
| IProbe              | `iprobe.svg` |
| TLIN                | `tline.svg` |
| M (Mutual)          | `mutual.svg` |
| SnP (Touchstone)    | `snp.svg` |
| Z (ZPort)           | `zport.svg` |
| SDD / FET           | `sdd.svg` |
| Tuner / SourceTuner / LoadTuner | `tuner.svg` |
| VAR                 | `var.svg` |
| MEAS                | `meas.svg` |

<!-- NEW COMPONENT: add a row above and drop a matching <name>.svg in this folder. -->

Files that don't exist yet show as a "symbol pending" placeholder in the docs (the doc page links the
filename regardless), so you can fill them in incrementally.
