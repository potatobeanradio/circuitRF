# Sonnet Brief — Phases L6/L7 (Ui half): the EM setup document, extraction, mesh viewer, run

**Design:** `docs/design/layout-view.md` §10 — §10.3.3 (cross-section extraction), §10.4 (stackup),
§10.5 (mesh viewer), §10.6 (ports), §10.8 (results + `.snp` co-simulation), §10.10 (the 30-second
acceptance test). Phase table rows **L6** and **L7**.

**Read `src/Engine/Mom/CLAUDE.md` first.** The engine half is complete, validated and documented
there: the kernel, its 17 requirements, its sign conventions, and two findings about the closed-form
oracles themselves. **This brief adds no physics.** Its entire job is: produce a valid `EmProblem`
from real layout geometry, show the mesh, run it, and land the result. If you find yourself computing
a capacitance, an ε_eff or an attenuation in `src/Ui`, stop — you are in the wrong half.

Gate command is plain `dotnet test`.

---

## 1. What already exists — read this before designing anything

Four things are already built that this brief was going to have to invent. Each removes a chunk of
scope; none of them should be re-implemented.

**1. The stackup model is complete and already carries the drawing-layer binding.**
`src/Ui/Layout/TechModel.cs` has `Stackup { BoundaryCondition Top, Bottom; List<StackupLayer> Layers }`
ordered **top to bottom**, and `StackupLayer` carries `Kind` (`Dielectric` / `Conductor` / `Via`),
`ThicknessDbu`, `Epsr`, `TanD`, `Mur`, `SigmaSm`, `IsGroundReference`, and — the one that matters most
— **`List<LayerKey> DrawingLayers`, "which drawing layers map onto this stackup layer."** The
extractor is therefore a pure function of `(shapes, Technology)`. **No `.ctech` schema change and no
`.ctech` editor change is in scope.**

**2. Both starter technologies already carry full stackups, and they are the two the engine was
validated against.** `StarterTechnologies.Pcb2Layer()` is 35 µm copper / 1.6 mm FR-4 (εr 4.4,
tanδ 0.02) / 35 µm copper (`IsGroundReference`) + a plated through-hole;
`StarterTechnologies.MmicGaAs()` is Metal2 / 3 µm air / Metal1 / 100 µm GaAs (εr 12.9, tanδ 0.0006) /
Backside Metal (`IsGroundReference`). Those are exactly the §2.4 pair that `MicrostripOracleTests`
gates the kernel on, down to the metal thicknesses. **The end-to-end oracle in §7 exists because of
this**: extracting `Pcb2Layer` + a rectangle must produce, field for field, the same `EmProblem` the
kernel's Tier 3 gate already validates. Read R-em-4/4a carefully — reproducing it is not automatic,
and the two ways to get it wrong are both 2%-scale errors that look plausible.

**3. `PinInference` already recovers pins from artwork** — position, width, outward direction, layer,
and a `Notes` list for everything it could not settle. See §4's port decision for why v1 does not
need it, and why it is still the right answer at L8.

**4. `LayoutRenderOptions.ShowPCellPins` is the exact precedent for the mesh overlay.** Its doc
comment states the whole contract you want: *"a screen-space overlay… never layer geometry, never
contributes to any `LayoutFrameCounters` geometry count, never reachable by any exporter… Defaults to
`false` so every export/one-shot render draws no pins by construction… the toggle default lives at
the VM layer, not here."* Copy that shape exactly.

Two more seams to follow rather than reinvent:

- **The run path.** `WorkspaceViewModel.RunSchematicDocAsync` is: write input → `Task.Run` the engine
  → post `result.Warnings` to `Messages` → `RunResultsWriter.WriteRun` → `RefreshOpenDataDisplaysAsync`
  → `AutoOpenOrCreateDataDisplayAsync`. An EM run is the same five steps with a different middle.
- **The document shape.** `TechDocument` is a workspace-scoped, **never-scratch** `Document` with an
  editor VM, `IUndoableDocument`, `IActivatableDocument` and a non-null `FilePath`. `.cem` mirrors it.

---

## 2. Decisions taken

**D1 (owner). The EM setup lives in its own document type, `.cem`.** This *changes* §10.8's R17a,
which said an EM setup is "a property of the layout… persisted in the `.clay`". The new answer serves
R17a's own stated purpose better: the standing invariant *"analyses attach to a `TestBench`, never to
a `Cell`"* is satisfied more cleanly by a standalone setup document than by embedding one in a cell
view, and it buys three things embedding does not — several EM setups against one layout, editing a
setup without dirtying the `.clay`, and a setup that is independently diffable and versionable.
**Update §10.8's R17a to match once this lands.**

**D2 (owner). Two buttons.** **Simulate** runs the EM simulation. **Mesh** computes the mesh *only*
and it renders automatically in the layout view. The Mesh button never solves.

**D3 (owner). Every EM setting is controlled in the `.cem` document.** Nothing that affects the
answer lives in a transient dialog, a canvas mode, or a hardcoded panel default.

**D4 (design doc, unchanged). An EM run produces an `.snp` artifact**, consumed by a schematic
through the existing SnP component (§10.8). No new analysis kind, no testbench-model change, no new
result type. Written to a predictable path and stamped with a provenance header so a stale `.snp` next
to an edited layout is detectable.

**D5 (proposed here — say so if you disagree, it is one line). Kernel A needs no port placement
tool.** For a uniform cross-section the two ports *are* the two ends of the extracted line, by
construction. This is the same fact that makes de-embedding a no-op for kernel A (R-mom-15): there is
nothing to place because there is no meshed port to place. The `.cem` carries per-port Z₀ and nothing
else. A Port tool becomes real work at **L8**, when a meshed port exists — and `PinInference` is what
it should be built on then, not a new picking mode. *Consequence:* §10.10's budget table loses its
5-second "Ports" row and the 30-second target gets easier, not harder. Update that table too.

---

## 3. The extractor — the risky part, and the reason it is built first and headless

§10.3.3 is where this phase is won or lost. Its own framing: *"If it does not reduce, refuse **clearly
and specifically**… A vague failure here is what would make v1 feel broken rather than bounded."*
That is the same bet R-mom-17 made on the engine side, and it paid: every kernel refusal now has a
test asserting the wording is specific rather than merely non-empty. Do the same here.

**R-em-1. `CrossSectionExtractor` is framework-free, lives in `src/Ui/Layout/Em/`, references no
Avalonia, and is unit-tested without constructing a document, a canvas or a workspace.** It takes
`(IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron, EmExtractionSettings)` and
returns a result that is either an `EmProblem` + a readback record, or a refusal. It must be possible
to write every test in §7 Tier E without a `LayoutDocument` existing. This is the single structural
decision that made the engine half tractable; it applies unchanged.

**R-em-2. DBU → metres happens exactly once, here, and `EmProblem` never sees an integer coordinate.**
`LayoutView.DbuPerMicron` is the conversion. R-mom-2 says the physics is doubles-in-SI and DBU stops
at the extractor — this is the extractor, so it stops here.

**R-em-3. The stack is built by accumulating `ThicknessDbu` upward from the ground plane.**
`Stackup.Layers` is ordered **top to bottom**, so the extractor walks it in reverse.

**R-em-4. The ground plane is the TOP SURFACE of the highest `IsGroundReference` conductor layer
below the signal, and `Stackup.Bottom == BoundaryCondition.Ground` is only the fallback when no
conductor layer is designated.** Get this backwards and every answer is wrong by one metal thickness.

Both starter technologies set `Bottom = BoundaryCondition.Ground` **and** carry a bottom conductor
layer with `IsGroundReference = true` — `Pcb2Layer`'s "Bottom Copper (1 oz)" and `MmicGaAs`'s
"Backside Metal". Those are at *different heights*: taking the boundary condition literally puts the
plane 35 µm below where the return current actually flows, which is a 2% error on h for a 1.6 mm
board and would fail the Tier A oracle outright. `IsGroundReference` exists precisely to disambiguate
this — read its doc comment in `TechModel.cs`, which was written for exactly this question.

Consequences to implement, not to rediscover:
- **A ground-designated conductor layer is represented by the image plane, and any shapes drawn on
  its drawing layers are ignored — and *reported*.** Kernel A's ground is laterally infinite; a
  finite ground pour is not something it can model, so silently meshing one would be worse than
  saying so.
- **Below the ground plane, extend the lowest dielectric region to −∞.** That region is shielded and
  its contents are irrelevant — this is exactly what the validated `EmProblemBuilders.Microstrip`
  does (substrate spans (−∞, h]), and it is why `EmProblem.Regions` tiling to ∓infinity is cheap.

**R-em-4a. A conductor layer's z band is NOT a dielectric region — it is absorbed into the dielectric
above it.** The stackup does not say what fills a conductor band where no metal is drawn, and the
answer that matches the validated engine problems is "whatever is above it" (metal is deposited on
the layer below and encapsulated by what comes after). So the region list is built from `Dielectric`
layers only, each extended downward through any conductor band beneath it. Check it against the two
starters:

| Stack | Regions produced | Ground | Signal |
|---|---|---|---|
| `Pcb2Layer`, line on Top Copper | FR-4 (−∞, 1.6 mm], air (1.6 mm, +∞) | y = 0 | 1.6 → 1.635 mm |
| `MmicGaAs`, line on **Metal1** | GaAs (−∞, 100 µm], air (100 µm, +∞) | y = 0 | 100 → 103 µm |
| `MmicGaAs`, line on **Metal2** | GaAs (−∞, 100 µm], air (100 µm, +∞) | y = 0 | 106 → 109 µm |

The first two are *exactly* `EmProblemBuilders.Fr4Microstrip` and `GaAsMicrostrip` — the problems the
kernel's Tier 3 gate is built on. That is not a coincidence to be grateful for; it is the reason the
Tier A oracle in §7 can be stated at all. In the Metal1 row the air layer, Metal1's own band and
Metal2's band all collapse into one air region because they are all εr = 1.

**R-em-4b. `MmicGaAs` is a three-conductor stack, so the signal layer is a setting, not an
inference.** Its own comment says so: *"a zero-config MLIN on this starter therefore defaults to
Metal2↔Backside Metal; an MLIN meant for Metal1 (the conventional MMIC RF routing layer) needs the
explicit override."* The `.cem` carries which conductor stackup layer is the signal (defaulting to
the one the drawn shapes actually land on, which is unambiguous whenever they land on exactly one) —
see R-em-11.

**R-em-4c. A `Via` stackup layer is ignored — a uniform cross-section has no vias — and ignoring it
is reported, not silent.** Note that a `Dielectric` layer may itself carry `DrawingLayers`
(`MmicGaAs`'s GaAs layer does); that binding is for other purposes and must not make it a conductor.

**R-em-5. A missing or nonsensical stackup value is a refusal, not a default.** A dielectric with
`Epsr == 0`, a conductor with `SigmaSm == 0`, a layer with `ThicknessDbu == 0`: each names the
stackup layer and says what to set. Silently substituting 1.0 or copper is how a plausible wrong
answer gets shipped.

### R-em-6. The refusal taxonomy

**Every refusal names the specific feature, where it was found, and where the capability arrives.**
Model the wording on `QuasiStaticKernel.CanSolve` — read those strings before writing these. The
extractor owns the **geometric** refusals; the kernel owns the **problem-level** ones and must not be
duplicated. Each of the following gets a test asserting its wording is specific:

| Condition | Must say |
|---|---|
| a non-straight edge (arc, curve, polygon vertex turning) | the coordinate of the bend, and "the quasi-static solver handles uniform cross-sections only; full-wave analysis of discontinuities arrives in L8" |
| two conductors not mutually parallel | both directions found, and the angle between them |
| width varying along the run (a taper) | where it changes, from what to what |
| shapes on a layer bound to no stackup conductor layer | the layer, and that binding is a `.ctech` `DrawingLayers` entry |
| shapes on two or more *signal* conductor stackup layers (a ground-designated one does not count — R-em-4 already ignores it) | which layers, and "z-directed current and multi-level stacks arrive at L9" |
| `Stackup.Top == BoundaryCondition.Ground` | that this is stripline, which needs an image series rather than one image, and is a bounded extension not yet built |
| zero extent along the propagation axis | that ℓ must be positive |
| no shapes on any bound conductor layer | what the setup is pointed at, and what it found |

**R-em-7. The propagation axis is determined, not assumed.** A uniform cross-section has a direction;
the extractor derives it from the conductors' geometry, reports it, and refuses if two conductors
disagree. ℓ is the extent along it; the cross-section is the profile perpendicular to it. Do not
assume x or y. *(The manual cut-line escape hatch §10.3.3 mentions is **out of scope** for this
brief — see §8. Auto-detect must be good enough for the starter cases first.)*

**R-em-8. The extractor produces the §10.3.3 R16a readback, and the panel renders it without
recomputing anything.** *"uniform 2-conductor cross-section · W = 2.9 mm · gap — · ℓ = 20 mm"* is a
structured record (per-conductor width, gap, ℓ, axis, stackup layer names, resolved εr/tanδ/σ), not a
formatted string built in a view model. Same rule as `EmMeshReport`: **report it from the layer that
knows, so the UI has nothing to recompute.**

---

## 4. The `.cem` document

**R-em-9. `.cem` is workspace-scoped and never scratch**, mirroring `TechDocument`: non-null
`FilePath`, `IUndoableDocument`, `IActivatableDocument`, snapshot undo. Registration is one arm in
`WorkspaceScanner.BuildFileNode`'s extension switch plus a `NodeKind`; follow `.ctech`'s existing
treatment through `WorkspaceViewModel.Docking` and the Window menu.

**R-em-10. A `.cem` references its layout by workspace-relative path, never by embedding geometry.**
Use the convention `CellRef` / `WorkspaceRefs` already establish. Re-running after a layout edit must
pick up the edit; that is only true if the geometry is read at run time.

**R-em-11. Everything the kernel takes is in the `.cem`, and the panel hardcodes nothing** (D3):

- the layout reference, and which cell view within it
- **which conductor stackup layer is the signal** (R-em-4b), defaulting to the one the drawn shapes
  land on when that is unambiguous, and required when it is not
- the frequency sweep — **reuse `FrequencySpecViewModel`**, which exists and already handles unit
  suffixes; do not write a second frequency editor
- per-port Z₀ (complex permitted — `RFNetwork.ZToS` already handles it, and a test pins it)
- all six `EmMeshSettings` fields, each defaulting to `EmMeshSettings.Default`
- the Kirschning–Jansen dispersion opt-in, **off by default** and disabled with a stated reason when
  the cross-section is not a single microstrip (`QuasiStaticKernel.TryMicrostripDispersion` returns
  null for exactly those cases — ask it, do not re-derive the condition)
- the `.snp` output path override, if any

**R-em-12. The stackup is shown, not edited.** The `.cem` panel displays the resolved stackup —
layer names, thicknesses, εr, tanδ, σ, which drawing layers bind where — read-only, with a link that
opens the `.ctech`. Two editors for one piece of process data is how they diverge.

**R-em-13. `CanSolve` is called on every settings change and its reason is shown live**, not on the
Simulate click. The kernel already words every problem-level refusal; surfacing it as you type is
free and is what makes the panel feel bounded. Simulate is disabled with that reason visible — the
established `R-L1h-3` disabled-with-reason pattern.

---

## 5. Mesh button and the mesh overlay

**R-em-14. The Mesh button calls `IEmKernel.Mesh` and nothing else.** No solve, no RLGC, no
s-parameters. It is the cheap "is my mesh sane?" answer §10.5 says should land before the solver, and
it must stay cheap enough to press repeatedly.

**R-em-15. The overlay copies `ShowPCellPins`' contract exactly** — screen-space, live-resolved from
the `EmMeshReport`, never layer geometry, never counted in `LayoutFrameCounters`, never reachable by
any exporter, `LayoutRenderOptions` field defaulting `false`, toggle default at the VM layer.

The overlay draws, from `EmMeshReport.Mesh.Segments`:
- conductor segments and dielectric-interface segments in visibly different styles (they are different
  unknowns — free vs bound charge — and a user reading a mesh needs to see which is which)
- the cell boundaries, because the whole point is to see the edge grading
- the interface truncation extent, or a clear indication that it runs off-screen — R-mom-10 calls
  truncation "the one place kernel A can be quietly wrong", and a viewer that hides it defeats the
  reporting the engine already does

**R-em-16. Surface the engine's own report verbatim; do not re-word it.** The unknown count, the
per-conductor and per-interface counts, min/max cell, the truncation half-extent, and every string in
`EmMeshReport.Notes` — including the R-mom-13 Wheeler-crossover note, which is the one that tells a
user sweeping down to 1 MHz that conductor loss is being carried by the DC floor. The engine already
wrote those sentences carefully. Print them.

**R-em-17. The overlay survives the layout being edited underneath it** by being invalidated, not by
being stale. An edited `.clay` clears the displayed mesh; it does not keep drawing the old one.

---

## 6. Simulate, results, and the `.snp` artifact

**R-em-18. The Simulate path is `RunSchematicDocAsync`'s five steps with a different middle.**
Background `Task.Run`, `Messages` for warnings first, then `RunResultsWriter.WriteRun` →
`RefreshOpenDataDisplaysAsync` → `AutoOpenOrCreateDataDisplayAsync`. **No new results plumbing and no
new result type** — the kernel already returns a `DataSet` carrying `S`, per-port `Z0`, and the
`tline` group (`Zc`, `Gamma`, `Eeff`, `AttenDbPerM`, `Rpul`, `Lpul`, `Gpul`, `Cpul`). Those last eight
are what make a wrong answer diagnosable; make sure they reach Data Display rather than being filtered
out on the way.

**R-em-19. The run writes an `.snp` (D4).** Predictable path derived from the cell and setup name,
mirroring `RunResultsWriter`'s convention, so a schematic's SnP reference is stable across runs. Use
the existing `RfCore.Export.TouchstoneExporter`. **It has no comment-header hook today** —
`TouchstoneExportOptions` is `(double Z0Ohms, int Digits, char DigitFormat, MatrixFormat)` — so
adding one is a small **additive** RfCore change and is in scope. Additive means every existing
caller (`DataExporterViewModel`) keeps compiling and its output stays byte-identical.

**R-em-20. The `.snp` carries a provenance stamp, and a stale one is detected rather than silently
believed.** §10.8 is explicit that this is the one failure mode the design introduces and that the
whole mitigation is a header stamp plus a warning. Stamp the stackup identity, mesh settings, port
definitions and a hash of the extracted geometry; on a later run, compare and post a `Messages`
warning on mismatch. **Hash the extracted `EmProblem`, not the raw file bytes** — a cosmetic layout
edit that does not change the cross-section must not report staleness, and a change that does must
always report it.

**R-em-21. No physics in `src/Ui`.** Every number the panel shows comes from `EmMeshReport` or from
the returned `DataSet`. If a quantity is wanted that the engine does not return, add it to the engine
and its `tline` group — do not compute it in a view model.

---

## 7. Validation — the gate ladder

`tests/Ui.Tests/`. Tag anything measured at or above ~5 s `[Trait("Category","Benchmark")]` per
`CLAUDE.md`; nothing here should come close. **Run `dotnet test tests/Ui.Tests` and
`dotnet test tests/Firewall.Tests` as two commands** — this SDK rejects two project paths in one
invocation.

**Tier E — extraction, headless, no document.** The bulk of the value, and it needs no UI at all.
- **The strongest test in the tier, and the one to write first:** `Pcb2Layer` + one rectangle on Top
  Copper extracts to an `EmProblem` **field-for-field equal** to
  `EmProblemBuilders.Fr4Microstrip(2.9e-3)` — same conductor outline, same two regions, same ground
  y, same σ/εr/tanδ, in metres. Likewise `MmicGaAs` + a rectangle on **Metal1** against
  `GaAsMicrostrip`. (Those builders live in `tests/Engine.Tests/Mom/Support/`; either reference them
  or restate the expected values — the point is that the extractor's output is checkable against a
  problem the kernel is already gated on, not merely "reasonable". Pass the starters' real loss
  tangents — `tanD: 0.02` for FR-4, `0.0006` for GaAs — since both builders default to lossless, and
  set `lengthMeters` to match the rectangle you drew.)
- `MmicGaAs` with the line on **Metal2**: three collapsed air regions and h = 106 µm, per R-em-4a's
  table. The intervening air layer and Metal1's empty band must merge, not become spurious regions.
- The ground plane lands at y = 0 — the **top** of Bottom Copper / Backside Metal — not 35 µm (resp.
  3 µm) lower. R-em-4's whole point; test it directly rather than only through Tier A.
- Shapes drawn on a ground-designated conductor layer are ignored **and reported**.
- Every row of R-em-6's table has a test asserting the refusal names the feature, the location, and
  where the capability arrives.
- Round-trip: DBU in, metres out, at more than one `DbuPerMicron`.
- A CW-wound and a CCW-wound rectangle extract identically (the engine normalises winding, but the
  extractor must not depend on that).

**Tier D — the `.cem` document.** Round-trip through `.cem` persistence with every setting non-default;
opens from the project tree; dirty/save/undo behave like `.ctech`; a `.cem` pointing at a missing
layout degrades with a specific message rather than throwing.

**Tier M — mesh.** The overlay's drawn segment count equals `EmMeshReport.UnknownCount`; the report's
numbers reach the panel unmodified; a pixel oracle that the overlay draws nothing when its toggle is
off and that an export render never includes it; editing the layout clears the displayed mesh.

**Tier R — run.** End-to-end from a `.cem`: Simulate produces a `DataSet` with `S`, `Z0` and the
`tline` group, writes an `.snp`, and opens a Data Display. Re-running after a geometry edit updates
the `.snp` and does **not** warn; re-running with a hand-edited stale `.snp` present **does** warn.

**Tier A — the acceptance gate. This is the L6/L7 Ui phase gate.**

> **Draw a 2.9 mm × 20 mm rectangle on `Pcb2Layer`'s Top Copper, point a `.cem` at it, and Simulate —
> Z₀ must land within 3% of 50 Ω and ε_eff between 3.0 and 3.6, matching
> `MicrostripOracleTests.T3_5_TheFiftyOhmHero_LandsAtFiftyOhmsWithAFewHundredUnknowns` to within the
> extraction's own rounding.** Repeat on `MmicGaAs` with a line on **Metal1**, per §10.10's "both
> markets are gated".

That test is the whole point of the brief: it is the *only* thing that proves the Ui half hands the
engine the geometry it thinks it does. The engine's number is already validated against
Hammerstad-Jensen; if the Ui path reproduces it, extraction is correct, and if it does not, extraction
is the only thing that can be wrong. **Write this test early — as soon as Tier E's first extraction
passes — and keep it green.**

**R18 / §10.10, the 30-second target**, is a scripted click/keystroke budget and gates the phase. With
D5 it is: new layout from starter template → draw the line → new `.cem`, pick the layout, type `1`
`20` `GHz` `101` → Simulate. What makes it reachable is *defaults that are already right*
(`EmMeshSettings.Default`, 50 Ω, auto mesh, preset stackups), not fast dialogs. Design the defaults
first.

---

## 8. Milestones, each with its own gate

| | Content | Gate |
|---|---|---|
| **U1** | `CrossSectionExtractor` + the readback record + the whole R-em-6 taxonomy. **No UI.** | Tier E green, and the Tier A oracle passes when the extracted problem is handed straight to the kernel in a test |
| **U2** | `.cem` model, persistence, workspace registration, document shell | Tier D green |
| **U3** | The EM setup panel — stackup readback, frequency sweep, ports, mesh settings, live `CanSolve` reason | Tier D green with every setting driven from the panel |
| **U4** | Mesh button + overlay | Tier M green |
| **U5** | Simulate button, results, `.snp` + provenance stamp | Tier R green — **and Tier A green through the real UI**, which is the L6/L7 Ui acceptance gate |

Stop and report at any gate that does not go green rather than proceeding with a tolerance loosened
to make it pass. **In particular: if the Tier A oracle disagrees with the engine's own number, the
extractor is wrong — do not adjust mesh settings to close the gap.** The engine half spent real effort
establishing that its number is right; §"What the oracles actually established" in
`src/Engine/Mom/CLAUDE.md` records exactly how, including two cases where the closed-form "oracle" was
the thing that was wrong. Extraction has no such defence.

---

## 9. Explicitly out of scope

- **The manual cut-line tool** (§10.3.3's escape hatch). Auto-detect must carry the starter cases
  first; a cut line is a second input path and deserves its own brief once the first one is trusted.
- **A Port tool** — D5. It becomes real work at L8, built on `PinInference`.
- **Any `.ctech` change.** The stackup model is complete (§1.1). If you believe a field is missing,
  stop and report rather than adding one.
- **Multiconductor / coupled lines.** `CanSolve` already refuses with an L7b message; the panel shows
  it and that is the whole deliverable.
- **Current-density heat map** on the mesh layer (§10.5). It needs per-segment solved charge surfaced
  from the engine, which is a small engine addition and a separate decision.
- **Stripline**, **L7b**, **full-wave (L8/L9)**, **wirebonds (LW1/LW2)**.
- **Adaptive frequency sampling.** Kernel A is frequency-independent by construction (R-mom-11 — one
  matrix fill for a 1001-point sweep); there is nothing to adapt.

---

## 10. File map (indicative)

```
src/Ui/Layout/Em/
  CrossSectionExtractor.cs   — shapes + Technology → EmProblem, framework-free (R-em-1)
  EmExtractionResult.cs      — the EmProblem + the R16a readback record, or a refusal
  EmExtractionSettings.cs
  EmSetupModel.cs            — what a .cem holds
  EmSetupPersistence.cs      — .cem read/write, mirrors TechPersistence
  EmSetupDocument.cs         — mirrors TechDocument
  EmSetupEditorViewModel.cs  — mirrors TechEditorViewModel
  EmRunService.cs            — the Simulate path (R-em-18/19/20)
  EmSnpProvenance.cs         — stamp + staleness check
  CLAUDE.md                  — written as this lands

src/Ui/Renderers/
  LayoutRenderer.Mesh.cs     — the overlay (R-em-15)

tests/Ui.Tests/Em/
  CrossSectionExtractionTests.cs   — Tier E
  ExtractionRefusalTests.cs        — Tier E, one per R-em-6 row
  EmSetupDocumentTests.cs          — Tier D
  EmMeshOverlayTests.cs            — Tier M
  EmRunTests.cs                    — Tier R
  EmAcceptanceTests.cs             — Tier A — the 50 Ω hero through the real path
```
