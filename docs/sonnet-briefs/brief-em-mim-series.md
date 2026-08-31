# Brief series — MIM capacitors in the planar MoM (kernel B)

**Origin.** An investigation on 2026-08-30 (owner request: why can the EM engine not simulate a MIM
capacitor — a thin capacitor dielectric between two metal plates, sitting in an interlayer
dielectric — and what would enable both the SHUNT form, whose bottom plate runs through a backside
via to the wafer's back metal, and the SERIES form, whose plates are fed and drained on different
metal levels through vias).

The finding: it is not one missing capability but FOUR independent ones, and none of them is "the
stackup model cannot say it" — `StackupLayer` already expresses a thin capacitor dielectric, a plate
conductor and a plate-connection via entry, and the `.ctech` editor already lets a Via entry bind a
drawing layer and name its span. What is missing:

1. **Via artwork is point-only.** `PlanarExtractor` consumes only `ViaShape` (a pad/drill point,
   meshed as its equal-area square). A MIM plate connection is a drawn REGION nearly as large as
   the plate, and a rectangle or polygon drawn on a via-bound layer today falls into
   `ignoredOther` SILENTLY. The engine end is already region-shaped: `PlanarVia` carries an
   arbitrary polygon list and the mesher makes one vertical basis per covered cell. → **MIM-1.**
2. **No shipped technology demonstrates the structure.** The GaAs starter has `Cap Dielectric` and
   `Nitride` DRAWING layers but no stackup entries behind them. → **MIM-2.**
3. **Thin-layer numerics are unvalidated.** A 50–300 nm dielectric layer passes
   `LayerStack.CanRepresent` (any positive thickness), but nothing has ever MEASURED the DCIM
   height-pair fits or the cross-level near quadrature at plate-scale separations against
   micron-scale cells — and §L8c's recorded failure mode (images closer to the plane than a cell
   width, converging gently enough to look converged) is exactly what plate spacing will
   reproduce. → **MIM-3.**
4. **De-embedding stops at the slab top.** A de-embedded edge port on any level above the lowest
   analysis level throws (`PlanarSolve`'s `LevelIsOnSlabTop` gate), and more than one dielectric
   between ground and the lowest level refuses at extraction — both because the only static
   Green's function in the repository is an image series over ONE grounded slab. MIM feeds arrive
   on upper metal, so every realistic de-embedded MIM run hits one of the two. This is §7's
   "all of Part B", already on record as the most valuable remaining work. → **MIM-4.**

One thing is deliberately NOT on the list: **drawing the capacitor dielectric.** The 2.5D premise
is a laterally infinite stratified medium, so the thin dielectric enters as a full stackup layer —
the standard approximation in the planar-EM class of tool (the fields that set C are under the
plates, where the layer really is present). Drawn artwork on a dielectric-bound layer stays
ignored, by design. MIM-2's starter states this in its own documentation note.

**The briefs.** Each is self-contained; the dependency column is the only ordering that matters.

| # | Brief | Kind | Depends on | What it buys |
|---|---|---|---|---|
| MIM-1 | `brief-em-mim-1-region-vias.md` | code, extraction + UI verify | — | drawn rectangles/polygons on via-bound layers become `PlanarVia` footprints; the silent drop becomes a note |
| MIM-2 | `brief-em-mim-2-gaas-starter-mim.md` | authored data | MIM-1 (to RUN, not to author) | the shipped GaAs `.ctech` gains a MIM plate level, capacitor dielectric and plate-via entry; shunt, series, and two-caps-joined-by-a-line fixtures |
| MIM-6 | `brief-em-mim-6-level-reference-surface.md` | code, extraction + editor | MIM-2 (its fixtures) | a conductor entry states which surface its sheet sits on; the shipped MIM gap becomes the 0.2 µm the process states, not 3.2 µm |
| MIM-3 | `brief-em-mim-3-thin-layer-gate.md` | measure, then maybe code | MIM-6 (physics tier) | the error-vs-separation ladder for closely spaced levels; a validated range or a named refusal |
| MIM-4 | `brief-em-mim-4-interior-static-greens.md` | code, the big one | — | the interior-height static Green's function: de-embedded ports off the slab top, stratified sub-feed dielectrics |
| MIM-5 | `brief-em-mim-5-import-coverage-note.md` | wording + docs | — | the technology import says which conductors its via list cannot reach; the user page documents adding MIM rows by hand |
| MIM-7 | `brief-em-mim-7-one-technology.md` | code, extraction + tech data + docs | MIM-2, MIM-6 | one shipped GaAs technology: a tied capacitor dielectric enters the medium only when its plate is an analysis level, so the second `.ctech` retires |

A de-embedded shunt or series MIM run needs MIM-1 + MIM-2 + MIM-4 (with MIM-3's verdict bounding
the trustworthy separations). MIM-1 + MIM-2 alone already give a runnable RAW solve — internal
ports, or de-embedding off — which is why they are first.

**Networks, not single parts, are the acceptance topology.** An MMIC matching section is several
capacitors joined by transmission lines in one EM run, and nothing in this series is per-capacitor:
the extractor takes every shape on the selected levels and every via region, so multiple caps plus
interconnect fall out of the same machinery (MIM-2 carries the two-caps-plus-line fixture that
pins it). The two honest bounds on such a run are ports (a de-embedded edge port must sit on the
lowest analysis level until MIM-4 — a Metal1-fed network is fine today, a Metal2-fed one is not)
and the unknown budget: the shared tensor grid means each small plate's fine gridlines extend
across the whole domain, so a long line between caps grows N quickly — the AIM path and P12's
bordered-via machinery are the existing mitigations, and the ceiling refusal names them.

**Learned at MIM-2 (2026-08-30), and binding on the rest of the series.** MIM-2 shipped, with two
deviations and one retraction on the record (`src/Ui/RESOLVED.md` §MIM-2):

- It shipped as a SECOND technology rather than three entries on the starter — measured, not filed:
  a capacitor dielectric between the interconnect metals makes every airbridge-post via refuse
  (`PlanarKernel.CanSolve`, via crossing a dielectric interface — a whole-run refusal) and shifts
  the upper-metal line's substrate resolution. **Superseded at MIM-7 (2026-08-30), which is BUILT:**
  both costs came from the film being present in runs with no capacitor in them, and
  `StackupLayer.PresentWithLayer` ties it to its plate so it is not. There is ONE MMIC technology
  again; `mmic-GaAs_2LM_100um_MIM` is retired. `src/Design/RESOLVED.md` §MIM-7.
- Its FINDING 1 is real and became **MIM-6**, which is **BUILT (2026-08-30)**: a conductor entry now
  states which surface of its band its sheet sits on, with the absorption direction paired to that
  choice, and the shipped MIM technology's levels extract at 103 / 103.2 / 106 µm with a 0.2 µm plate
  gap. **MIM-3's physics tier is therefore unblocked and must be run against a post-MIM-6 build** —
  on an earlier one it validates a 3.2 µm regime while the true 0.2 µm one, the risky one, stays
  unmeasured. `src/Design/RESOLVED.md` §MIM-6.
- Its FINDING 2 ("the plate capacitance is not in the answer") is RETRACTED: the measurement read
  RAW, un-de-embedded S, and a raw edge port's own ~0.3 fF series discontinuity masks any small
  element behind it. Hence a convention for every brief here: **never read a small element's value
  off raw S.** Gate a with/without COMPARISON on the same artwork (the discontinuity is common and
  cancels — the L9 phase gate's own shape), or measure de-embedded.

**Conventions that bind every brief here** (same as `brief-em-perf-series.md`):

- **Write-ups go to the area's `RESOLVED.md` (narrative) and, for engine work,
  `src/Engine/Mom/HISTORY.md` (every measured table). Do not add to any `CLAUDE.md`.** If a
  sentence already in a `CLAUDE.md` or in `docs/design/mom-engine.md` becomes false because of your
  work, correct that sentence in place with a dated `> Built at MIM-x` note and add nothing else.
- **Refusals follow R-mom-17**: name the specific feature, where it was found, and where the
  capability arrives. A refusal that names a phase number goes stale silently — name the
  capability, not the schedule.
- **Bit-identity is the gate wherever the arithmetic is unchanged**; a stated tolerance wherever
  it is not. Never loosen an existing gate to make a brief pass — say which gate and why in the
  write-up and stop.
- **No new timing tests in the routine tier.** Structural counters are the routine gate; wall
  clock goes to `HISTORY.md`, measured with a scratch harness (RELEASE build, alone), and only
  under `[Trait("Category", "Benchmark")]` if a test must carry it at all.
- **Name no foundry, no commercial tool and no specific process design kit**, in code, comments,
  fixtures or prose — root `CLAUDE.md` §Commercial Vendor References. Every layer name, thickness
  and permittivity in these briefs is a generic textbook value.
- **No native dependencies** without asking (root `CLAUDE.md`). Everything here is managed C#.
