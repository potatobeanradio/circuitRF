# Brief — Loadpull UI 07: extraction + `.cnl` round-trip + end-to-end Run

**Goal:** Close the loop so an authored Loadpull (or Loadpull-Pursuit) analysis actually **runs** from the
GUI: the extractor carries it into the `TestBench`, `CnlWriter` emits the directive in the grammar
`CnlReader` parses back, and Run dispatches it through the (already-built) engine — validated end-to-end on
Hero 3.

**Depends on:** briefs 05 + 06 (so there is an authored LP/LPP to extract) and ideally 01–02 (so a tuner
schematic can be drawn). Brief 04 covers `.csch`/clipboard/template serialization; THIS brief covers the
**`.cnl` netlist** path used by Run.

**Reads with:** `docs/design/analysis-authoring.md` §6 (extraction/run wiring — DC/SP/HB already done),
`docs/design/net-extraction-and-run.md`, and the existing readers/writers: find `CnlReader` /
`CnlWriter` (grep for `TryParseLoadpullDirective` / `TryParseLoadpullPursuitDirective` — per
`src/Engine/Loadpull/CLAUDE.md` and `loadpull_pursuit.md` §9, the **reader already parses** both directives,
because the engine consumed hand-authored `.cnl` loadpull files for the Hero 3 / 3B regressions). So the
reader side is likely **already done**; this brief mostly confirms it and ensures the **writer** emits them.

## 1 — Extractor carries LP/LPP into the TestBench

Read `NetExtractor.Extract` — it already copies `model.Analyses` into `tb.Analyses` (the loop
`foreach (var analysis in model.Analyses) tb.Analyses.Add(analysis);`). Because `LoadpullAnalysis` and
`LoadpullPursuitAnalysis` are `Analysis` subclasses, they are **already carried** with no change. Confirm:
- They reach `tb.Analyses`.
- The relative `Grid` / `OutputGrid` paths are resolvable at run time. The model carries a `SourceDirectory`
  field that the **reader** sets; for the GUI-authored path, the analysis is built without one. Decide where
  the base directory comes from when running an authored (not `.cnl`-read) loadpull: the run pipeline knows
  the schematic's directory (`SchematicRunService` / `WorkspaceViewModel`). Set `SourceDirectory` (or resolve
  the `GridPath`/`OutputGridPath` to absolute) at the point the run writes `netlist.cnl`, OR ensure the
  `CnlWriter` emits the grid path and the reader's `SourceDirectory` (the netlist's own directory) resolves
  it on read-back. **Recommended:** since Run already round-trips through `netlist.cnl` (extract → write
  `.cnl` → engine reads `.cnl`), the cleanest fix is: `CnlWriter` emits `Grid="<path>"` relative to the
  netlist location, and the reader sets `SourceDirectory` to the netlist's directory (it already does this
  for hand-authored files). Verify the written `.gam` path resolves from the `netlist.cnl` directory; if the
  user picked an absolute or schematic-relative `.gam`, translate it so the emitted path is correct relative
  to where `netlist.cnl` is written. This is the one real piece of plumbing in this brief — get it right and
  test it.

## 2 — `CnlWriter` emits the LP/LPP directives

Find `CnlWriter` (it emits DC/SP/HB + `measure` lines today — `analysis-authoring.md` §6). Add emission for
`LoadpullAnalysis` and `LoadpullPursuitAnalysis` in the **exact grammar** the reader parses. Derive the
grammar from the reader (do not invent it): open the reader's `TryParseLoadpullDirective` /
`TryParseLoadpullPursuitDirective` and emit key=value pairs with the same key spellings it accepts
(`LoadTuner=`, `SourceTuner=`, `Sweep=`, `Tone=`, `TuneHarm=`, `MaxHarm=`, `Grid=`, `Compression=`,
`GainType=`, `PinStart=`, `PinStep=`, `PinMax=`, `Tickle=`, `MaxIter=`, … and for pursuit `EffType=`,
`ZsourceOBO=`, `SearchMethod=`, `OutputGrid=`, `VSWR1=`/`VSWR1_resolution=`, `VSWR2=`/`VSWR2_resolution=`,
`keepNonconvergingPoints=`, `nonconvergentVSWR=`, `CreateLoadpullResult=`, `LoadpullResultZsource=` — match
the reader's actual tokens, including the `\`-continuation style noted in `loadpull_pursuit.md` §9 if the
reader requires line continuations for long directives).

- Emit only the keys whose values differ from the engine defaults if the writer minimizes output elsewhere;
  otherwise emit all (match the existing HB writer's verbosity).
- The directive `type=` tag: confirm the reader dispatches `type=loadpull` and `type=loadpull_pursuit`
  (`loadpull_pursuit.md` §0: "a new analysis type, `type=loadpull_pursuit`"). Emit those exact type strings.

## 3 — Reader confirmation (likely already done)
Confirm `CnlReader` round-trips both directives back into `LoadpullAnalysis` / `LoadpullPursuitAnalysis` with
all fields. If a key the UI now authors is NOT yet parsed (e.g. a key the engine defaulted and the
hand-authored Hero files never set), add it to the reader so the round-trip is lossless. Add a
writer→reader round-trip test (mirror `CnlWriterTests`): author an LP, write `.cnl`, read it back, assert the
`LoadpullAnalysis` fields match.

## 4 — Run dispatch
Run already dispatches `tb.Analyses` to engines and skips disabled ones (`analysis-authoring.md` §6;
`SchematicRunService`). Confirm `LoadpullAnalysis` → `LoadpullEngine` and `LoadpullPursuitAnalysis` →
`LoadpullPursuitEngine` are wired in the dispatch (the CLI already runs these for the regressions — find the
CLI's dispatch and ensure the GUI run path reaches the same engine entry points). If the GUI run pipeline has
an analysis-type switch that only knows DC/SP/HB, extend it for LP/LPP. Results are written to
`results/<key>/<name>.npy` like other analyses (the loadpull result cubes — `results-dataset-layout.md` /
`brief-7.4f-loadpull-ingest` show the Data Display can already ingest loadpull results, so the run just needs
to produce them).

## 5 — End-to-end acceptance on Hero 3
The decisive test (manual + a headless integration test if feasible):
1. Build a Hero-3-style schematic in the GUI: the GaN SDD FET DUT, a **LoadTuner** on the drain (DUT output)
   with `BiasTee=on`, `Vbias`=drain supply, `Z[1]` set; a **SourceTuner** on the gate (DUT input) with
   `BiasTee=on`, `Vbias`=gate bias. (Or load an existing Hero 3 schematic if one exists in `testdata`/demo.)
2. Author a **Loadpull** analysis: `LoadTuner=LoadTuner1`, `SourceTuner=SourceTuner1`, `Sweep=Load`,
   `Tone`=f0, `Grid`=the Hero 3 `.gam`, `Compression=3`, `PinStart`/`PinStep`/`PinMax` per the Hero 3 setup.
3. Run. Confirm: the netlist round-trips, the `LoadpullEngine` runs the 2-D sweep, results land in
   `results/<key>/`, and the Data Display can open them (loadpull contour ingest already exists).
4. Compare against the existing Hero 3 self-generated regression (`testdata/Hero3/…`) — the GUI-authored run
   should reproduce the CLI/hand-authored numbers (the engine is identical; this validates the
   authoring→extraction→run plumbing, not the math).
5. Repeat for a **Loadpull-Pursuit** on the Hero 3B setup (`testdata/Hero3B/hero3B_at_compression.cnl`,
   `PinMax=30`), confirming MXP/MXE come out and (if `CreateLoadpullResult=on`) the follow-on loadpull data
   appears.

## Verify
1. `dotnet build` zero warnings; `dotnet test` green incl. the new writer↔reader round-trip test.
2. The end-to-end Hero 3 GUI run produces loadpull results matching the regression within tolerance.
3. The `.gam` path resolves correctly relative to the written `netlist.cnl` (the one plumbing risk — test a
   schematic in a different directory than the `.gam`).
4. Firewall passes.

## Notes / risks
- The biggest unknown is path resolution for `Grid`/`OutputGrid` in the GUI run path (§1). Resolve it once,
  test it explicitly, and document the chosen convention in `net-extraction-and-run.md`.
- If the reader turns out to already parse everything and the writer already emits LP/LPP (some projects wire
  the writer when the reader lands), this brief collapses to "confirm + add the round-trip test + the Hero 3
  e2e validation." That's a fine outcome — verify before writing new emission code.
