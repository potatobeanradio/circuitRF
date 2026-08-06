# `src/Ui/Layout/Em` — the EM setup: extraction, `.cem`, mesh, run

Standing instructions for the Ui half of phases **L6/L7**. Read with `src/Ui/CLAUDE.md` and — before
touching anything about the physics — **`src/Engine/Mom/CLAUDE.md`**, which owns the kernel, its 17
requirements, its sign conventions, and two findings about the closed-form oracles themselves.
Design note: `docs/design/layout-view.md` §10. Brief: `docs/sonnet-briefs/brief-L6-L7-em-ui.md`.

**This half adds no physics.** Its whole job is: produce a valid `EmProblem` from real layout
geometry, show the mesh, run it, and land the result. If you find yourself computing a capacitance,
an ε_eff or an attenuation in here, stop — you are in the wrong half (R-em-21).

Gate command is plain `dotnet test`.

---

## R-em-1 — framework-free, and enforced

Nothing under this folder references Avalonia or SkiaSharp. That is the single structural decision
that made the engine half tractable, applied unchanged: every extraction test runs without
constructing a document, a canvas or a workspace. `EmFrameworkFreeTests` scans the source (comments
stripped, since several files' own headers say "no Avalonia") and was confirmed to fail against a
deliberately added `Avalonia.Point`. The views live in `src/Ui/Views/Layout/EmSetupEditorView.axaml`;
the overlay lives in `src/Ui/Renderers/LayoutRenderer.Mesh.cs`.

---

## The two DBU scales — the trap this phase actually hit

**R-em-2 says "DBU → metres happens exactly once, here." It is two conversions with two different
scales, and conflating them is silent.**

- **Shape coordinates** convert with the layout's own `DbuPerMicron`.
- **Stackup thicknesses** convert with `LayoutUnits.DefaultDbuPerMicron`, ALWAYS.

Neither `Technology` nor the `.ctech` file carries a `DbuPerMicron`, so there is nothing else those
integers could be relative to — and `SubstrateResolver` already named this same constant
`FallbackDbuPerMicron` for exactly this reason. Using the layout's resolution for the stackup
rescales every substrate height by that ratio: a plausible-looking answer, wrong by 10× on a layout
drawn at 100 DBU/µm. `CrossSectionExtractor.StackupDbuPerMicron` is the one place this is stated;
`DbuToMetres_RoundTrips_AtSeveralResolutions` is the test that caught it.

---

## R-em-4 / R-em-4a — the height rules, and why each is a 2%-scale trap

**The ground plane is the TOP SURFACE of the highest `IsGroundReference` conductor BELOW the signal.**
`Stackup.Bottom == BoundaryCondition.Ground` is only the fallback when no conductor is designated.
Both starter technologies set the boundary condition **and** carry a ground-designated conductor, at
*different heights* — taking the boundary condition literally puts the plane 35 µm below where the
return current actually flows, a 2% error on h for a 1.6 mm board that fails the Tier A oracle
outright.

**A conductor's z band is NOT a dielectric region — it is absorbed into the dielectric ABOVE it.**
The stackup does not say what fills a conductor band where no metal is drawn, and "whatever is above
it" is what matches the validated engine problems (metal is deposited on the layer below and
encapsulated by what comes after). So regions are built from `Dielectric` layers only, each extended
downward through any conductor band beneath it, plus a synthesized top region, then **adjacent
same-material regions are merged**. That merge is what collapses `MmicGaAs`'s explicit air layer,
Metal1's empty band and Metal2's band into one air region; without it the Metal2 case grows two
spurious regions and stops matching `GaAsMicrostrip`.

| Stack | Regions produced | Ground | Signal |
|---|---|---|---|
| `Pcb2Layer`, line on Top Copper | FR-4 (−∞, 1.6 mm], air (1.6 mm, +∞) | y = 0 | 1.6 → 1.635 mm |
| `MmicGaAs`, line on **Metal1** | GaAs (−∞, 100 µm], air (100 µm, +∞) | y = 0 | 100 → 103 µm |
| `MmicGaAs`, line on **Metal2** | GaAs (−∞, 100 µm], air (100 µm, +∞) | y = 0 | 106 → 109 µm |

The first two are *exactly* `EmProblemBuilders.Fr4Microstrip` and `GaAsMicrostrip` — the problems
the kernel's own Tier 3 gate is built on. That is not a coincidence to be grateful for; it is the
reason the Tier A oracle is statable at all.

**One deliberate difference from `GaAsMicrostrip`:** that builder leaves `groundSigmaSm` at its
`CopperSigma` default, while the extractor reads the stackup's own Backside Metal (gold). The
stackup's value is the physically correct one and it moves only Wheeler's conductor-loss term.

---

## L9d — N levels, a `LayerStack`, vias, and a port's LEVEL

Brief: `docs/sonnet-briefs/brief-L9d-multilevel-ports-and-references.md`. The engine half — the
discriminated kernel wrapper, the via-port refusal, the de-embedding refusal, the cost — is in
`src/Engine/Mom/CLAUDE.md` §L9d. **This is the first L9 slice that reaches `src/Ui` at all**, and the
whole of it is still R-em-1 framework-free.

### The extractor produces N levels, and a one-level extraction is BIT-FOR-BIT the L8 path

`PlanarExtractor` now groups signal conductors by level, builds a `LayerStack` from the stackup bands
and a `PlanarVia` per `ViaShape`, and sets `PlanarProblem.MediumStack` / `PlanarConductorLayer.ZM`
**only when `levels.Count > 1`**. A single-level layout therefore takes L8's shipped one-slab path
unchanged — which is what keeps `Ui.Tests` green at 4,737 with only one behavioural test updated.

**`BuildMediumStack` restates R-em-4a for the PLAN view, and getting it wrong inserts an air gap.**
The metal's own z band is not a dielectric region; it is absorbed into the dielectric ABOVE it, and
adjacent same-material regions merge — **but the merge must never cross a level boundary**, or two
levels collapse into one and the whole point is lost. The first version left the metal's own band
uncovered and would have inserted a spurious air gap the thickness of Metal1 into every two-level
stack; the fix falls back to the dielectric whose `BottomM` equals the band's top.

**A via's footprint is an equal-area SQUARE** (side = 0.886 × drill diameter), because L9c's basis is
one cell of L8b's shared tensor grid and a circle is not. The span comes from the stackup entry's own
`SpanFromLayer`/`SpanToLayer` — the same fields the `.ctech` editor writes and R-via-3 already
declared — never from geometry.

### The ungrounded refusal was NARROWED, and the accepted SET did not widen

L9b measured what an open-below stack costs DCIM and the two halves are genuinely different, so the
refusal now names both rather than one:

- **a DENSER bottom half-space is a permanent structural refusal** — the second branch point is a term
  a sum of exponentials in k_z0 cannot carry, measured at 59× (G_q) and 2.3e+4× (G_A) on oxide over
  silicon;
- **an equal-or-lighter bottom is fittable**, and what actually blocks it is the *de-embedding's*
  grounded-slab C_pul plus the substrate-height end runs — i.e. exactly the L9c Tier 4 gap the engine
  note names.

**Nothing new was accepted.** Splitting one refusal into two accurate ones is the whole change; do not
read it as the ungrounded case having arrived.

### D3's port extraction gained a LEVEL, and the ambiguity is refused by name

`EmPortExtraction.NearestPolygon` returns `(Poly, Level, Containing)`, and a port label sitting on
metal on **more than one level** is refused naming the label — picking the lowest would drive a
different conductor with the same footprint and produce a complete, plausible answer for a structure
nobody drew. The port is constructed with `problem.Layers.Count > 1 ? level : null`, so a one-level
layout passes `null` and lets the engine infer, exactly as before. Each port's note names its level.

### The provenance stamp had to learn all of it, or staleness silently stops working

Same failure mode L8e's own note records: `EmSnpProvenance.GeometryHash(PlanarProblem)` hashed the
one-slab shape, so a two-level run would have stamped a constant and the staleness check would have
gone on *appearing* to work. It now also hashes the `MediumStack` (terminations plus each layer's
thickness / εᵣ / tanδ / µᵣ), every `PlanarVia` (span, σ, footprint) and each level's `LevelZ`.

### The analysis-levels control, and why it is undoable

`EmSetupModel.AnalysisLevelNames` (a `.cem` field, `null` when empty so every pre-L9d file round-trips
byte-identically) selects which signal conductor levels take part; the panel shows one checkbox per
level via `EmAnalysisLevelRow`. Each toggle commits **one undoable snapshot** and invalidates the mesh
— it changes the problem, not the view.

### L9's phase gate found the via extraction was DEAD CODE — read this before touching `BuildStack`

`BuildStack` skips every `StackupKind.Via` entry, correctly: a via contributes no thickness and has no
z band of its own. But the `ViaShape` branch looked its drawing layer up in `BuildLayerBinding`'s map,
which is built FROM those bands — so **a via's drawing layer was never in the map, the lookup could
never match, every drawn via was silently counted as `ignoredOther`, and `BuildVias` was unreachable.**
That is why `BuildVias` had no test: nothing could reach it. Fixed by `BuildViaBinding(Stackup)`, a
separate map read straight off `tech.Stackup.Layers`.

**The two bindings answer different questions and are deliberately NOT merged** — where a layer sits in
z, versus which two conductors a via joins. Adding Via entries to `BuildStack` as zero-thickness bands
would have worked too and would have put a band with no meaningful z into every consumer of `stack`
(`groundBand`, `slabBands`, `stack[0].BottomM`, `UngroundedRefusal`), which is a wider blast radius for
no gain.

**The gate that catches it is `L9PhaseGateTests.Gate1_…`**, and it catches it for a structural reason
rather than by watching a number: its fixture's Metal1 has a GAP, so with the posts dropped the only
path across is capacitive and |S₂₁| collapses. Two runs of the same artwork, one with vias and one
without, cannot be equal unless the vertical basis carries nothing.

### One pre-L9d test UPDATED, not loosened

`Extractor_RefusesMultipleMetalLevels_ByName_PointingAtL9` asserted the refusal L9d delivers. It is
now `Extractor_TwoMetalLevels_ExtractAsATwoLevelProblemOnAGeneralMedium`, keeping the half that still
matters (the levels are named and ordered).

## L8b — the planar extractor and the PLAN-VIEW overlay

Brief: `docs/sonnet-briefs/brief-L8b-planar-mesher-and-overlay.md`. The engine half — the mesher, the
N report, the R17 ceiling and every measured number — is in `src/Engine/Mom/CLAUDE.md` §L8b. **This
half adds no physics either**; it produces a `PlanarProblem` from real layout geometry, draws the
mesh, and shows the numbers the engine already computed.

### D5 — the overlay is a GENUINE plan-view overlay now, and the INSET STAYS

This is the one place L8b reverses an earlier decision. The note below ("the mesh overlay is an INSET
PANEL") was correct and **stays correct for kernel A**, whose mesh is a cross-section with no
coordinate mapping to the plan view. Kernel B's surface mesh lives in the SAME (x, y) plane the canvas
already draws, so for the first time the mapping exists and §10.5's "a system layer superimposed on the
geometry drawing cell boundaries" is the right picture.

**Both overlays exist. Which one is drawn follows from which mesh was computed, not from a mode** —
`ShowEmMesh`/`EmMesh` and `ShowPlanarMesh`/`PlanarMesh` are independent, both default to false at the
render layer, and a document with only one report draws only one overlay. `LayoutRenderer.Mesh.cs`
draws the inset screen-space AFTER the path-space transform is restored;
`LayoutRenderer.PlanarMesh.cs` draws in world coordinates INSIDE it. R-em-15's contract is copied
exactly for the new one, R-em-17 included — and R-em-17 matters *more* here, because a plan-view mesh
drawn over edited artwork looks like it still matches.

**The metres → DBU mapping is one scalar, and it stays that way only because `PlanarExtractor` does
NOT translate or centre the geometry.** `CrossSectionExtractor` centres so that truncation is
symmetric — a requirement with no analogue for a bounded piece of artwork. Do not add a centring step
here without also giving the overlay the offset.

### The extractor is NOT bolted onto `CrossSectionExtractor`, deliberately

That file is 939 lines and almost all of it is the hard part of §10.3.3 — detecting that geometry
*reduces* to straight, mutually parallel, constant-width conductors, and refusing specifically when it
does not. **A planar extractor needs none of it, because accepting geometry that does not reduce is
the entire point of a full-wave kernel.** Merging them would put the refusal logic of one on the
acceptance path of the other: every bend refusal would need an "unless planar" branch, and the first
one forgotten would be a silent capability loss.

What IS shared is restated rather than called: the **two-DBU-scales rule** (shape coordinates use the
layout's own `DbuPerMicron`, stackup thicknesses use `LayoutUnits.DefaultDbuPerMicron`, ALWAYS) and
**R-em-4's ground rule** (the top surface of the highest ground-designated conductor below the
signal). Both are 2%-scale traps and both are recorded in this file already.

**Its own refusals, all pointing at L9 by name:** more than one signal conductor level; more than one
dielectric between the ground plane and the metal (L8a's D2 is ONE grounded slab — this is what
refuses MMIC GaAs's Metal2, which sits above an explicit air layer, while Metal1 extracts cleanly);
no ground plane at all.

### R-msh-8a — name the analytic alternative, never refuse for it

`PlanarExtractor.AnalyticAlternativeFor` maps a PCell generator id to the validated closed-form model
that already covers it: MKLOPF → `MicrostripKlopfModel`, MTAPER → `MicrostripTaperModel`, MLIN →
`MicrostripLineModel`. The note rides through to `PlanarMeshReport.Notes` and is surfaced verbatim.
**MBEND is deliberately absent** — `MicrostripBendModel` exists, but a bend is exactly the
discontinuity kernel B is FOR, and R-pc-18 records that mitred and unmitred are distinct.

### D7 — the `.cem` says which analysis it is; there is no automatic selection

`EmSetup.AnalysisKind` (`CrossSection` | `Planar`) plus `EmSetup.PlanarMesh` (D3's three controls).
Both are **omitted from the file when they hold their defaults**, so every `.cem` written before L8b
loads and re-serialises byte-identically — pinned by a test that asserts the serialized text contains
neither field. Choosing the kernel automatically from the geometry is a registry decision and it
arrived with the registry, in **L8e** — `AnalysisKind` gained `Auto` and `Auto` is now the DEFAULT.
See §L8e below; the two fields still omit themselves at their defaults, and a pre-L8b `.cem` still
round-trips byte-identically.

The panel gained `BuildPlanarMesh` beside `BuildMesh` and surfaces `PlanarMeshSummary` +
`PlanarMeshNotes`; it never solves, and there is no planar solve seam on the VM at all.

## L7b-b — N conductors reach the schematic, and this half was almost free

The general modal decomposition (`src/Engine/Mom/CLAUDE.md`'s L7b-b section) needed **no change to
the extractor, the `.cem` schema or the panel** — L7b had already made all three general in N. The
Ui-side delta is three lines and two updated tests:

- **`EmSolveResult.SolveNotes` → `EmRunService`.** R-gen-5's mode-coupling residual is a
  per-**solve** number: the extractor could not have produced it, because it does not know the
  frequencies. It rides an additive, defaulted field on `EmSolveResult` and is appended to
  `warnings` beside `MeshReport.Notes` and `Rlgc.Notes`, surfaced verbatim per R-em-16.
- **The `tline` group now carries a MODE AXIS** — rank-2 `[freq, mode]` cubes plus
  `ModeCouplingResidual` over `[freq]`. The `…Even`/`…Odd` names survive as an alias **for N = 2
  only**, sliced from the same arrays, so every saved `.cdd` trace pointing at `tline.ZcEven` keeps
  working. Three conductors publish no even/odd names at all — those belong to a pair.
- **`EmBackAnnotation` needed no change, and Tier G5 proves it rather than assuming it.** R-cpl-12's
  two-step key (deterministic `EM_<setup>` name, then the file already read) exists precisely so a
  changing port count repoints the same component; going 4 → 6 ports is the same operation as
  4 → 2, and `TG5_GoingFromTwoConductorsToThree_RepointsTheSameComponent` drives it end to end.

**Who refuses what is unchanged in shape, only in content.** The extractor still owns the geometric
refusals and the kernel still owns the problem-level ones — but an ASYMMETRIC pair is now ACCEPTED
and runs to a real `.s4p`. The kernel-side refusal that replaced it is **R-gen-9's conductor
ceiling** (`QuasiStaticKernel.MaxSignalConductors`, 16, with its measured cost in the engine note).
Two Ui tests were **updated, not loosened** — `EmSetupDocumentTests.TheKernelsOwnRefusal_IsShownLive`
and `EmRunTests.AnAsymmetricPair_IsRefusedByTheKERNEL` both asserted the asymmetric refusal; they
now assert that an asymmetric pair runs, and the "a kernel refusal is the KERNEL's, not the
extractor's" claim moved to the ceiling fixture, which is still true of it.

## L7b — the coupled pair reaches the schematic

The Ui half of L7b: **2N ports**, per-port reference impedances in the `.cem`, and `.snp`
back-annotation. Brief: `docs/sonnet-briefs/brief-L7b-coupled-lines-and-cosim.md`.

**D3 — the extractor builds `2N` ports, not 2.** Port `2k−1` is conductor *k*'s NEAR end, `2k` its
FAR end. Kernel A built exactly two, both on `conductors[0]`, which was right when only one line
could be solved and silently wrong the moment a coupled pair could. The numbering is stated here and
in the kernel; never re-derive it — a transposed map swaps a coupler's through and coupled ports,
which no magnitude plot of a symmetric structure would reveal.

**R-cpl-6 — per-port Z₀ is ADDITIVE, so every existing `.cem` still loads.** `Port1Z0`/`Port2Z0`
survive as the NEAR/FAR defaults and keep that meaning at any conductor count (odd ports take
`Port1Z0`, even ports `Port2Z0`); `EmSetup.PortZ0s` overrides an individual port and is **omitted
from the file entirely when empty**, so a setup that overrides nothing re-serializes byte-identically.
A list that *replaced* the pair would have had to be synthesised on load for every existing file, and
the near/far distinction — the one a user actually thinks in — would have been lost. The panel shows
the port list only above two ports; a single line's two ports are fully described by the pair, and
two controls for one value is worse than one.

**`EmBackAnnotation` places-or-updates an ORDINARY `SnP`** (R-cpl-11) — no new component, no new
analysis kind. Framework-free, returns an `IUiCommand` and never executes it, the same contract
`SchematicToLayoutGenerator.Run` keeps.

- **R-cpl-12's key is two-step**, the confident-then-conservative shape `KitPaletteMerge` already
  uses: the deterministic `EM_<setup>` name first, then any `SnP` already pointing at this exact
  file. **Both are needed.** Name-only breaks when a user renames the component; path-only breaks
  when the port count changes — editing a pair into a single line turns the artifact from `.s4p`
  into `.s2p` at a different path, and a path-only key would place a second component beside the
  first. A re-run that changes nothing returns `NothingChanged` and does not dirty the schematic.
- **R-cpl-13 — the stored `File` follows `WorkspaceRefs`** (workspace-relative inside, absolute
  outside, `/` separators), and that is also what the RUN path needs: `NetExtractor` emits `File`
  verbatim into `netlist.cnl`, which is written to the **workspace root**, and `CnlReader` resolves a
  relative SnP path against that file's own directory. An outside reference is reported, never
  silently stored in a form that will not travel.
- **`NumPorts` comes from the SOLVED port count, not a sniff.** `SetSnpFileCommand` re-sniffs it off
  disk, resolving a relative path against the **schematic's** directory while the engine resolves the
  same string against the **workspace root** — for a schematic in a sub-folder those bases differ and
  the sniff can quietly fail. Back-annotation needs no sniff: the kernel just solved the problem.
  `SetSnpReferenceCommand` sets both fields explicitly. *(That base-mismatch is pre-existing and is
  flagged, not fixed, here.)*

**One real bug this surfaced, fixed in Core:** `CnlWriter` wrote an SnP's `File` **unquoted**, while
`CnlReader` resolves a relative Touchstone path only inside its quoted-string branch — so a relative
SnP path reached the model verbatim and was looked for relative to the process working directory.
`hero1.cnl`'s own `File="…s2p"` is the canonical form. It had never bitten because the Browse… picker
always produced an absolute path; EM back-annotation is the first thing to write a relative one from
the schematic side. Gated by three tests in `Core.Tests/Netlist/CnlWriterTests.cs`.

## Who refuses what

**The extractor owns the GEOMETRIC refusals; the kernel owns the problem-level ones, and they are
not duplicated.** A coupled pair extracts cleanly and the KERNEL decides — **as of L7b-b a
symmetric OR asymmetric pair and any N up to the conductor ceiling are ACCEPTED; only the ceiling
itself is refused, by name, with the number and what bounds it**. Do not add a second
copy of that judgement here: the extractor's job is to produce the problem, not to grade it. Every extractor refusal
names the specific feature, where it was found, and where the capability arrives; each row of
R-em-6's table has a test asserting the wording is specific rather than merely non-empty.

Coordinates in a refusal are printed in the **technology's own display unit** (mil on `Pcb2Layer`),
which is what the user reads. Tests assert against `LayoutUnits.Format` rather than a hard-coded
string — not circular, since the thing under test is that the extractor names the RIGHT coordinate.

---

## D1 — the `.cem` is its own document, and this CHANGED the design note

`docs/design/layout-view.md` §10.8's R17a used to say an EM setup is "a property of the layout…
persisted in the `.clay`". It now says a `.cem`, and the note carries the reasoning. A `.cem` is
**workspace-scoped and never scratch**, mirroring `TechDocument` exactly — there is no
materialize/offer-a-save-target path to build or test. It references its layout by workspace-relative
path and **never embeds geometry**: re-running after a layout edit picks the edit up only because the
geometry is read at run time, in `WorkspaceViewModel.ResolveEmLayout`, which prefers the OPEN
editor's live model over the on-disk file so an unsaved edit is what gets analysed.

**D5 — kernel A needs no port placement tool**, and §10.10's budget table lost its 5-second "Ports"
row accordingly. For a uniform cross-section the two ports ARE the two ends of the extracted line, by
construction — the same fact that makes de-embedding a no-op (R-mom-15). A Port tool becomes real
work at L8, built on `PinInference`.

---

## R-em-20 — hash the extracted `EmProblem`, never the file bytes

A cosmetic layout edit (a silkscreen label, a renamed net, a nudged via) must NOT report staleness,
and a change that genuinely moves the cross-section must ALWAYS report it. The `EmProblem` is exactly
"everything the answer depends on and nothing else", so hashing it makes both halves true by
construction rather than by a heuristic about which edits matter. Geometry, mesh settings and port
impedances get **three separate hashes** so a mismatch can say WHICH of them moved.

Staleness is compared **before** the file is overwritten — the whole point is to tell the user their
schematic has been reading stale s-parameters, which is only knowable from the file about to be
replaced. An `.snp` with no circuitRF stamp (hand-written, third-party) is *not* stale; there is
simply nothing to compare against.

The provenance stamp rides on `TouchstoneExportOptions.HeaderComments` — a small **additive** RfCore
change, defaulting to null, so every existing caller (`DataExporterViewModel`) keeps compiling and
its output stays byte-identical.

---

## The mesh overlay is an INSET PANEL, and that is not a shortcut

The mesh lives in the **cross-section** plane (x across the line, y above the ground plane); the
layout canvas shows the **plan** view. There is no coordinate mapping between them, so painting mesh
segments onto plan-view artwork would be a picture of nothing. §10.5's "mesh viewer" is a
cross-section viewer, and an inset panel is what that is on this canvas.

`LayoutRenderOptions.ShowEmMesh`/`EmMesh` copy `ShowPCellPins`' contract exactly: screen-space, never
layer geometry, never counted in `LayoutFrameCounters`, never reachable by any exporter, defaulting
to `false` so every export/one-shot render draws no mesh **by construction**; the toggle default
lives at the VM layer (`LayoutEditorViewModel.ShowEmMesh`, default on).

**The conductor gets a locator box, and here is why that is honest.** On a true-scale panel spanning
the 20-substrate-height truncation (R-mom-10 requires truncation to be visible), a 2.9 mm strip 35 µm
thick is four pixels — physically correct and useless to look at. The box is drawn AT the conductor's
own bounds, only widened to a legible minimum, so it says "the conductor is here" without redrawing
it bigger. Do not "fix" this by cropping the panel to the conductors; that hides truncation, which is
the one place kernel A can be quietly wrong.

**R-em-16 — print the engine's notes, do not re-word them.** Unknown count, per-conductor and
per-interface counts, min/max cell, truncation half-extent and every string in `EmMeshReport.Notes`
(including the R-mom-13 Wheeler-crossover note) go to the panel verbatim.

**R-em-17** — an edited `.clay` CLEARS the displayed mesh. `LayoutEditorViewModel`'s own
`Model.Changed` subscription nulls `EmMeshReport`; the overlay survives an edit by being invalidated,
never by going stale.

---

## File map

```
CrossSectionExtractor.cs   shapes + Technology → EmProblem, framework-free (R-em-1)
EmExtractionResult.cs      the EmProblem + the §10.3.3 R16a readback, or a refusal
EmExtractionSettings.cs    signal layer, per-port Z0, and the subject a refusal names
EmSetupModel.cs            what a .cem holds (R-em-11)
EmSetupPersistence.cs      .cem read/write — mirrors TechPersistence exactly
EmSetupDocument.cs         mirrors TechDocument (never scratch)
EmSetupEditorViewModel.cs  the panel's VM: live CanSolve, Mesh, Simulate
EmSetupSnapshotCommand.cs  coarse-grained snapshot undo, mirroring TechSnapshotCommand
EmRunService.cs            the Simulate path (R-em-18/19/20), headless
EmSnpProvenance.cs         the stamp + the staleness check
EmPortExtraction.cs        L8e D3: the layout's own IsPort labels → PlanarPorts
```

Tests: `tests/Ui.Tests/Em/` — Tier E (`CrossSectionExtractionTests`, `ExtractionRefusalTests`),
Tier D (`EmSetupDocumentTests`), Tier M (`EmMeshOverlayTests`), Tier R (`EmRunTests`),
Tier A (`EmAcceptanceTests`, `EmAcceptanceBudgetTests`), plus the R-em-1 guard
(`EmFrameworkFreeTests`); and for L8e, `EmPortExtractionTests`, `PlanarRunTests`,
`EmRefusalWordingTests`, `L8PhaseGateTests`.

---

## L8e — auto-selection's call site, ports from labels, and the stamp that had to grow

### `EmRunService` runs BOTH extractors before it runs either kernel

The registry (`EmKernelRegistry.Choose`, engine-side) takes two *verdicts*, not geometry — it is behind
the firewall from both extractors. So this file expands the frequency list, runs
`CrossSectionExtractor` **and** `PlanarExtractor`, hands the registry the pair, and dispatches on its
answer. Both extractors run even when the setup names a kernel explicitly, because an explicit
`CrossSection` that A refuses must be able to say *"…and the planar kernel would analyse this"*, and
that sentence is only available if the other extractor was asked.

`choice.Reason` is added to the run's notes on every path. **A user who cannot tell which solver
produced a number cannot tell whether the number is credible**, and with `Auto` as the default the
answer is no longer implied by the setup file.

### D3 — ports are `LabelShape`s with `IsPort`, and the `.clay` schema did not change

`EmPortExtraction.Extract(shapes, problem, dbu, z0For)` is framework-free like everything else here.
The rules, and each one exists because its alternative fails silently:

- **Numbering from the label text** — `1`, `P1`, `p2`, `#3`, `Port 4`. Unnumbered labels take the
  lowest free number; the layout editor's Port tool calls the *same* parser for its auto-name, so what
  the tool writes and what the extractor reads cannot disagree.
- **Two labels naming port 1 is a refusal by name.** Picking one is a coin flip on which end is which.
- **The side is inferred from the nearest conductor boundary, REPORTED, and refused when ambiguous.**
  A label at a corner is equidistant from two edges. A wrong side reverses the direction of current
  into the structure — a hard π in S₂₁, which is smooth, plausible, and invisible in a magnitude plot.
  Every resolved port's note names its side *and* which way current flows in.
- **A label off the metal is refused by name**, and a layout with no port labels is refused with a
  pointer at the Port tool rather than a generic "no ports".
- **Z₀ comes from the `.cem`**, per port. A layout is geometry; an impedance is an analysis setting.

The reference plane is **not user-positionable** — it is one mesh cell in from the drawn edge, where
L8d's calibration actually removes the discontinuity. It is drawn over the layout from the coordinates
the *engine* reports (`EmRunResult.PlanarPorts`), never from a Ui re-derivation, so the picture cannot
disagree with the number.

### D9 — the stamp had to learn the planar problem, or staleness silently stops working

`EmSnpProvenance` hashed the `EmProblem`. A planar run has none. Left alone it would have stamped a
cross-section for a run that has no cross-section — and the staleness check would have gone on
*appearing* to work while comparing a constant.

So there are now two `BuildHeader` overloads over one shared three-hash core, and
`GeometryHash`/`MeshHash`/`PortHash` have `PlanarProblem`/`PlanarMeshSettings`/`IReadOnlyList<PlanarPort>`
counterparts. The three labels differ per kernel — "the layout geometry" / "the mesh settings" / "the
ports" — because that is what a planar user would have to go and change.

### The heat map is adopted, not re-derived

`AdoptCurrentDensity` takes the engine's `PlanarCurrentDensityMap` and hands the renderer
`density.Normalised(cell)` plus `ScaleCaption`. **The Ui does not decide what the colour means**: the
reduction, the units, the normalisation, and the caption all come from the engine
(`src/Engine/Mom/CLAUDE.md` §L8e D5). One port, one frequency; no sweep, no superposition.
