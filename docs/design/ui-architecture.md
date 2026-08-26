# circuitRF — UI Architecture & the Framework Firewall

**Status:** Draft (rev 1) for review · **Date:** 2026-06-06 · **Phase:** 6a

This document defines the **structural boundary** between circuitRF's engine/core and its user interface:
the layering, the in/out contract, where the display layer lives, and \u2014 critically \u2014 the **enforced firewall**
that keeps the engine free of any UI-framework dependency so circuitRF can be re-skinned (or out-live
Avalonia) with minimal trouble. Companion: `ui-design.md` (the interaction spec); `src/Ui/CLAUDE.md` (standing
UI rules). The root `CLAUDE.md` already states the three-layer architecture and the relevant invariants; this
note formalizes and **enforces** them for the UI boundary.

**Why this exists (owner intent):** Avalonia may someday be replaced by a better UI system. The circuitRF
*engines* (the value of the product) must be skinnable by any new UI with as little trouble as possible. That
is only true if the engine never depends on the UI framework \u2014 and "never depends" must be a **checked
invariant**, not a hope, because over a long GUI build the separation erodes silently unless something fails
the build when it's violated.

---

## 1. The layers and the dependency direction

circuitRF is built in layers with a **strict one-way dependency direction** (root `CLAUDE.md` "Architecture"):

```
  RfCore (sibling)  ──►  used by everything below as the shared result/network library
        ▲
  src/Core    (Design + Elaboration layers): cells, instances, nets, parameters, the expression
        ▲      engine, elaboration → the elaborated netlist. NO UI, NO engine-numerics.
  src/Engine  (Numeric layer): MNA, HB, loadpull/pursuit, the analyses. Produces a DataSet.
        ▲      NO UI.
  src/Design  (design-layer DOCUMENT artifacts): the layout model and `.clay` reader, the
        ▲      technology model and `.ctech` reader, the `.ccell` cell-folder format, the `.cem`
        ▲      EM setup and the extractors that turn geometry + stackup into an EmProblem.
        ▲      NO UI — it draws nothing, docks nothing and observes no canvas.
  src/Ui      (Presentation layer): Avalonia + SkiaSharp. Depends on Core, Engine, Design, RfCore.
        ▲      NOTHING depends on src/Ui.
  src/Cli     (headless driver): depends on Core/Engine/Design/RfCore, NOT on src/Ui.
```

`src/Design` was carved out of `CircuitRF.Ui` in 2026-08 so `src/Cli` could run an EM setup
(`docs/sonnet-briefs/brief-cli-em-verb.md`). Nothing was rewritten to do it — the code had been
framework-free by rule since L6/L7 and simply lived in the wrong assembly. The layout EDITOR, the DRC
engine, the PCell generators and the `.cem` editor all stayed in `src/Ui`; what crossed is the model,
the readers, and the extractors.

**The rule:** dependencies point **up the stack only** (UI → Engine → Core → RfCore). Nothing below `src/Ui`
may reference `src/Ui`, and **nothing below `src/Ui` may reference any UI framework** (Avalonia, and any
UI-framework integration package). `src/Cli` proves the engine is fully usable with **no UI at all** \u2014 it is
the standing existence proof that the engine half is UI-agnostic (it has driven every hero through Phases
1\u20135).

---

## 2. The engine\u2194UI contract: design-model in, `DataSet` out

The entire surface between the UI and the engine is two data shapes \u2014 no UI types cross down, no engine
internals leak up:

- **Down (UI → engine): the design model.** The UI edits the *design layer* (cells, schematics, nets,
  parameters, Vars, directives, measurements) and hands the engine a design to **elaborate and run**. The UI
  never touches matrices, unknown vectors, or the linear/nonlinear partition. (Root invariant: *the GUI never
  simulates the design layer directly \u2014 always elaborate first.*) The schematic editor reaches the engine via
  **net extraction** (`ui-design.md` §5), which emits exactly the design model an authored `.cnl` produces \u2014
  so the UI is just another front-end onto the same elaboration path the CLI uses.
- **Up (engine → UI): the `DataSet`.** Every analysis returns a **`DataSet`** of single-kind `DataCube`s
  (root invariant; Phase 5). The UI's data display consumes *only* the `DataSet` \u2014 it does not reach into
  engine state. Measurements are cubes in that `DataSet`. This is why the display layer can be built against a
  stable, UI-free contract.

Because the contract is just "design model down, `DataSet` up," **a replacement UI re-implements only the
presentation of those two shapes** \u2014 the engine, elaboration, and result model are untouched. That is the
re-skin story, made concrete.

> The `DataSet`/`DataCube` contract is owned by circuitRF and consumed by splotRF; changing it is an
> "Ask before" decision (root `CLAUDE.md`) \u2014 splotRF upgrades in lockstep. (On-disk *file* format is exempt
> during alpha \u2014 `src/Core/Data/CLAUDE.md` "File-format stability".)

---

## 3. The firewall: no UI framework below `src/Ui` \u2014 enforced

### 3.1 The rule
**`RfCore`, `src/Core`, and `src/Engine` must reference no UI framework** \u2014 no Avalonia, no
Avalonia-integration packages, no UI-framework type in a public or internal signature. All UI-framework code
lives in **`src/Ui`** only. A future re-skin **replaces `src/Ui`** (and re-hosts the display layer, §4) and
nothing else.

### 3.2 The enforced check (the one piece of 6a that is code)
A rule described but not enforced erodes. 6a adds an **automated check that fails the build/CI** if any
non-UI project references a UI framework:
- **Primary mechanism:** an assembly-reference assertion \u2014 a small test (in the existing test suite, runnable
  in CI on all three OSes) that loads each non-UI assembly (`RfCore`, `CircuitRF.Core`, `CircuitRF.Engine`,
  `CircuitRF.Design`, `CircuitRF.Cli`, `CircuitRF.Harmonica`, `CircuitRF.WBond`) and asserts its referenced-assemblies list contains **no `Avalonia*`** (and no other
  UI-framework package). Fails with a clear message naming the offending project and reference.
- **What it does NOT forbid:** see §3.3 (headless SkiaSharp is allowed). The check targets *UI frameworks*
  (Avalonia and its integration layers), not 2D-graphics math libraries.

This check is the firewall. It is cheap, runs in CI, and turns "keep the core UI-agnostic" from a discipline
into an invariant a machine guarantees \u2014 the same role the FD-Jacobian and export round-trip oracles play for
correctness.

### 3.3 The SkiaSharp nuance (headless drawing is fine; the control is UI)
SkiaSharp is a **2D graphics library**, not a UI framework. Using Skia to *draw* (produce a surface/bitmap,
compute geometry) is acceptable in a render layer; what is UI is the **Avalonia-integrated control** that
hosts a Skia surface in a window and pumps input events. The firewall therefore distinguishes:
- **Allowed below `src/Ui`:** headless Skia rendering and geometry (a renderer that draws to a Skia surface
  given a model + a transform), *if* a component ever needs it. (In practice the renderers live with the
  display layer, §4.)
- **UI-only (in `src/Ui`):** the Avalonia custom control hosting the Skia surface, input handling, the
  windowing, Dock, MVVM view-models bound to Avalonia.
**Keep the Skia *rendering* code separate from the Avalonia *control hosting* code** (mirroring splotRF's
Renderer-vs-PlotControl split). A re-skin keeps the renderers and re-hosts them in the new framework's
surface \u2014 so the rendering investment survives a framework change. The §3.2 check allows SkiaSharp; it forbids
Avalonia in the core.

---

## 4. The display layer (C1): circuitRF's own, `DataCube`-native, splotRF as reference

(Decision recap from `ui-design.md` §6 and Phase 6a §4.)

- **circuitRF builds its own display layer**, fresh and **`DataCube`-native** (a trace is a slice of a cube),
  living **in circuitRF under `src/Ui`** (C1 \u2014 e.g. `src/Ui/Display`). It is **not** a dependency on splotRF
  and **not** in RfCore.
- **splotRF is reference material only** \u2014 mine its proven techniques (the three-coordinate-space transform
  pipeline, Smith/polar/rectangular rendering math, the placeable-plot canvas, plot/trace/table rendering,
  MarkerInfoBox, autoscale-with-marker-preservation, tick snapping, pan/zoom/hit-test) and **re-implement them
  cleanly against `DataCube`**. Do not take splotRF's code as a dependency; do not reinvent ignoring its
  solved problems. splotRF continues or is discontinued independently; circuitRF neither depends on nor
  constrains it.
- **Structured for a possible future lift-out, but not lifted now.** Keep the display engine
  **UI-framework-light** per §3.3 (Skia-render core + thin Avalonia host) and `DataCube`-native, so it *could*
  later be promoted to a shared library if something ever needs to share it (e.g. a future splotRF v2 adopting
  circuitRF's engine). Do not build that shared-lib ceremony now \u2014 the "don't preclude it" discipline, same as
  the DataCube memory backing.
- The two canvases (schematic, §`ui-design.md` 3/4; data-display, §6) **share the transform/interaction
  machinery** (the §3.3 Skia-render core). Build that shared canvas core once; the schematic and data-display
  layers render different content through it.

---

## 5. What a re-skin would touch (the future-proofing, made concrete)

If Avalonia is ever replaced, the work is bounded to:
1. **Re-host the canvas:** swap the Avalonia custom control that hosts the Skia surface + input for the new
   framework's equivalent. The **Skia renderers and the transform/geometry/hit-test core are reused** (§3.3).
2. **Re-build the shell:** windows, Dock regions, tabs, menus, toolbars, dialogs in the new framework
   (`ui-design.md` §2,7,8) \u2014 re-binding to the same view-model intentions.
3. **Re-bind the view-models:** the MVVM view-models are Avalonia-flavored; a new framework re-expresses them.

What is **NOT** touched: `RfCore`, `src/Core`, `src/Engine`, `src/Cli`, the elaboration path, the analyses,
the `DataSet`/`DataCube` model, net extraction (it's core logic, headless), the measurement evaluator, and the
file format. That is the entire value of the firewall: **the simulator survives the UI.**

---

## 6. Acceptance (the architecture half of 6a)

1. This note (`ui-architecture.md`) written and reviewed: the layering, the design-model-in/`DataSet`-out
   contract, the firewall rule, the SkiaSharp nuance, the C1 display placement, and the re-skin scope.
2. **The firewall check exists and passes** (§3.2): an assembly-reference assertion (CI, all three OSes) that
   `RfCore`/`Core`/`Engine`/`Cli` reference no Avalonia, with a clear failure message. This is the one bit
   of code in 6a.
3. Root `CLAUDE.md` gains a one-line pointer to this firewall; `src/Ui/CLAUDE.md` points here for the
   layering/display decisions.

---

## 7. Guardrails (standing, for the whole GUI build)

- **Nothing below `src/Ui` references a UI framework.** The §3.2 check enforces it; if a GUI task wants to
  reach a UI type from the core, that's the signal the boundary is being crossed \u2014 stop and rethink, don't
  add the reference.
- **Design model down, `DataSet` up** \u2014 no UI types into the engine, no engine internals into the UI beyond
  the `DataSet`.
- **Keep Skia rendering separate from Avalonia hosting** (§3.3) so the rendering survives a re-skin.
- The display layer is **circuitRF's own, `DataCube`-native** (C1) \u2014 splotRF is reference, not a dependency.
- Changing the `DataSet`/`DataCube` contract is "Ask before" (root `CLAUDE.md`) \u2014 splotRF lockstep.
- Net extraction and the measurement evaluator are **core logic, headless** \u2014 they live below `src/Ui` and
  must stay UI-free (they're testable without a UI, which is also how they're validated).
