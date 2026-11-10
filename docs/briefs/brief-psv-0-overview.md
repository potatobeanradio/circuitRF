# Brief series PSV — passivation, cover layers and vias through more than one dielectric

**Goal.** A semiconductor or PCB stackup can be entered as the process states it and simulated without
editing it to fit the solver. Three things stop that today:

1. **A dielectric above the topmost analysis level is dropped from the planar solve.** Passivation,
   solder mask, a conformal coating or a radome is discarded and the run warns
   (`PlanarExtractor.cs`, "ANT-12 §1a"). The limit is in how the layer stack is built
   (`BuildMediumStack` stops at the top level and puts open air there), not in the layered-medium
   kernel. That kernel already solves levels that are buried under dielectric.
2. **A via that crosses a dielectric interface is refused by the planar kernel**
   (`PlanarKernel.EveryViaLiesInOneMediumRegion`). The closed-form z-integral in `ViaZIntegral` is
   written for one region of the medium. The refusal asks for a conductor level on the interface, which a
   real process often does not have.
3. **A passivation opening over a bond pad cannot be described.** It is not a via (nothing sits above
   it). It is a hole in a dielectric, and the technology has no way to say "this dielectric exists
   everywhere except where this layer is drawn". `PresentWithLayer` (MIM-11) says only the opposite.

These were raised together in GitHub discussion 53, on a semiconductor stack with two dielectrics over
the top metal, a pad-opening layer, and a via through two dielectric regions.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-psv-1-planar-covers.md` | Dielectrics above the top analysis level enter the planar medium; the ANT-12 warning goes | — |
| `brief-psv-2-passivation-openings.md` | `OpenWithLayer` on a dielectric stackup entry: cut out of the slab in 3D, a coverage note in planar, a picker in the stackup editor | 1 (planar half only) |
| `brief-psv-3-via-through-interfaces.md` | The planar kernel splits a via at each dielectric interface it crosses, and the refusal goes | — |
| `brief-psv-4-example-and-docs.md` | A small example workspace showing all three, user docs, MCP/CLI reference, DocGen | 1, 2, 3 |

Briefs 1 and 3 are independent and may be built in either order. Brief 2's 3D half does not need brief 1;
its planar note does.

## Decisions already made

- **D1 — The technology is the source of truth; nothing is added to make the solver happy.** No
  placeholder conductor level, no stacked-via workaround, no opening drawn as a via. Where a solver
  cannot represent something (a hole in a laterally infinite planar dielectric), it says so in the run's
  notes with a number attached. It never refuses a stackup that is physically correct.
- **D2 — `OpenWithLayer` is the inverse of `PresentWithLayer`, and the two are exclusive on one entry.**
  `PresentWithLayer = X`: the dielectric exists only where X is drawn. `OpenWithLayer = X`: it exists
  everywhere except where X is drawn. A dielectric entry carrying both is a validation error. X resolves
  in the same two namespaces MIM-11 set up (conductor entry first, then drawing layer). For an opening,
  only the drawing-layer spelling is useful, and validation says so if a conductor name is given.
- **D3 — 3D cuts, planar notes.** In every 3D route (the layout's 3D view, a 3D `.cem`, a layout
  placed in a `.c3d`, a thermal solve) the opening is a hole in the slab. In the planar kernel every
  dielectric is laterally infinite. That is the 2.5D formulation and not something this series changes.
  So the passivation stays everywhere and the run says what fraction of the analysed area is open.
- **D4 — Shipped technologies are not changed.** None of them has a dielectric above its top metal
  (checked: all 8 starters and every example `.ctech`), so brief 1 moves no shipped result. Passivation
  is shown in brief 4's example with its own technology. It is not added to a starter, because MIM-7
  showed that changing a starter silently moves every workspace copied from it.
- **D5 — The physics is flat slabs.** Real passivation follows the metal's steps, and real openings have
  sloped walls. Both 3D and planar model a flat slab with straight-walled holes. The user docs say so in
  one sentence.

## Gates, for the whole series

1. **Nothing shipped moves.** Every starter technology and every shipped example produces byte-identical
   planar and 3D results on the paths this series touches. A technology with no dielectric above its top
   level, no `OpenWithLayer`, and no via crossing an interface must take exactly the code path it takes
   today.
2. **A covered line agrees with the cross-section kernel.** The cross-section extractor already models
   dielectric above the metal (`CrossSectionExtractor`, "Above the topmost dielectric is whatever
   Stackup.Top says"). A microstrip under a cover, solved by the planar kernel, must agree with it on
   Z₀ and ε_eff to the tolerance the uncovered comparison already uses (brief 1).
3. **A via through two dielectrics agrees with 3D.** Its terminal inductance and S-parameters match a
   3D solve of the same structure (brief 3). Keep the 3D run short and specific: a small fixture, one
   to three frequencies.
4. **An opening is a hole.** A 3D problem built from a layout with an opening drawn over a pad has a
   dielectric solid with a hole ring matching the drawn opening, and the pad metal is exposed in the
   rendered 3D view (brief 2).

## Rules that apply throughout

- **Tests:** one per claim, named classes only. Never the full suite and never all of `Ui.Tests`. No new
  `Category=Benchmark` test and no timing assertion.
- **EM runs are short.** A gate comparing against a 3D solver uses the smallest fixture that shows the
  effect. No whole-board run and nothing over a few minutes.
- **No citations** of papers, authors or other tools, in code, docs or commits. No commercial tool or
  PDK names anywhere.
- **UI work is checked visually** with `tools/UiSnapshot` (the stackup editor, and `--c3d`/`--clay` 3D
  renders), and the PNGs are looked at and listed in the completion note.
- **No DocGen per brief.** Brief 4 regenerates the edited pages once, at the end of the series.
- Findings go in the relevant `RESOLVED.md`, never `CLAUDE.md`.
