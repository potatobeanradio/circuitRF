---
name: project-brief-hb-spectrum-1-tone-metadata
description: Stage 1 ToneFreqs metadata: single-tone HB emits ToneFreqs[tone(1)]; stacks per sweep; HbSpectrum helper; picker exclusion — completed 2026-06-23
metadata:
  type: project
---

Stage 1: every HB run emits stacking `ToneFreqs` cube. `HbEngine.BuildSingleToneDataSet` adds `ToneFreqs[tone(1)]=[f0]`; two-tone already emits `[tone(2)]=[f1,f2]`. `HbSpectrum` in `src/Core/Expressions/HbSpectrum.cs` centralizes index→frequency math. Picker excludes `ToneFreqs`/`MetaMixOrder` in `TraceRowViewModel` + `PlotInspectorViewModel`. 5 gate tests. Build 0W/0E; 428+376+1404+4 total. Foundation for stage 2 (harmonic axis flip).

**Why:** frozen `k·f0` harmonic axis breaks per-sweep-point frequency when tone is swept; `ToneFreqs` survives stacking and gives stage 2 a path to reconstruct frequency from integer indices.

**How to apply:** stage 2 brief flips the harmonic axis to integer orders and wires `HbSpectrum` + `ToneFreqs[slice]` into all consumers (Trace, TableRenderer, Plot.XLabel, export).
