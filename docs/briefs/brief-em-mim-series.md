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

## Second round — MIM-9 … MIM-13 (2026-09-16)

**Origin.** The same user's LC resonator, on the shipped MMIC technology, one day after MIM-8 landed:
S(1,1) > 1 across the band and no series resonance anywhere. MIM-8 fixed the FILL and the fill is
measurably right — `C/(ε₀εᵣA/d)` = **1.0032** on that user's plate size at a mesh as coarse as the
run's. The
de-embedded two-port of the same capacitor reads **−201 + 174j Ω** where 1.0838 pF is −147j, so the
capacitor comes back as an inductor with negative resistance.

**These are numbered in the order they should be built, and the order is not the order of
importance.** MIM-12 is the real fix and it is last, because it is the only one that is weeks rather
than days and because each of the three before it removes a reason to rush it: MIM-9 makes the engine
refuse instead of publishing a wrong answer, MIM-10 gives a designer a way to finish the job
meanwhile, and MIM-11 takes the one confounding factor off the table. **Every measurement the round
rests on is already taken** and is tabulated in MIM-12; nothing before it needs a new EM sweep.

9. **Say what the run actually did.** Reporting only, on an answer that does not move — so it depends
   on nothing and is never revisited. The NOT PASSIVE sentence blames the de-embedding while the
   de-embed's own error floor reads 1.6e-3 against a non-passivity excess of 0.73; the three remedies
   it offers are measurably inert; the level-separation note *reassures* on the strength of an
   ELECTROSTATIC validation; and `R-emsev-4`'s refusal, deferred at MIM-8 pending exactly the
   measurement that now exists, gets built at today's floor of cell/separation 40. **Days.**
   → **MIM-9.**
10. **An LC resonator that can be simulated this week.** EM everything except the 0.2 µm gap and
    insert the closed-form capacitance between the plates — what a MMIC designer does anyway. **Its
    gating experiment has already been run and came back passive** (σ_max 0.9977 on the coil alone
    with the capacitor deleted), so most of it is documentation and a kit change. → **MIM-10.**
11. **The nitride is a mask and there is no mask.** `KIT_MIMCAP` draws no nitride, so the kit is not
    manufacturable. Independent of everything else, and the film it defines is modelled laterally
    infinite whatever is drawn — measured cost **0.108° of phase, 2.7 % of a fringing capacitance**,
    which is worth stating and is not the blocker. → **MIM-11.**
12. **The full-wave path cannot read back a capacitance the fill gets right.** A dynamic-range
    failure, not a quadrature one: `Re(Z)`'s smallest eigenvalue is 1.4e-11 of its largest, `cond(Z)`
    is 1e8–1e9 and rises as frequency falls, and the capacitance is an O(d/h) difference at
    d/h = 1/200. Excluded by measurement: the mesh (2/4/8 cells across is the same wrong answer), the
    ports and de-embedding (the same structure minus the second level is passive and flat to 0.4 %),
    the electrostatic fill, and an active operator. **Weeks. Last.** → **MIM-12.**
12a. **The film's own contribution is a closed form and the fit is extrapolating it.** MIM-12's step 0
    refuted item 12's diagnosis: it is the KERNEL FIT, not dynamic range. A cross-region pairing has
    nothing extracted, its near field is an image series at (2n+1)·d whose structure needs
    k_ρ ≈ 5.0e6 m⁻¹, and the sampling path stops at 1.9e5 at every frequency — short by 26×. Peeling
    six terms leaves a remainder flat to 0.4 % over four decades of ρ and 1.1e-3 of the kernel, which
    is 0.08 % of the capacitance instead of 200 %. → **MIM-12a.**
13. **The unknown budget.** Ceilings are 5,000 dense and 12,000 accelerated; the edge fan is
    expensive on a spiral for a reason the engine already prints; the cell count should be on screen
    before the solve, not after the refusal. Independent; take it whenever. → **MIM-13.**
14. **The floor is a stale measurement now, and it is refusing a design that may already work.**
    12a repaired the kernel and did NOT re-run the de-embedded ladder, because the replacement fixture
    measures its own ports (6-10× low at cell/separation 2, where no film is involved, and halving with
    every refinement). So `FullWaveCellOverSeparation` is still 40, from a kernel that no longer
    exists, and the resonator this round started from is refused at 66.7. Build the fixture, re-run the
    ladder, re-point the one constant — and if the new number leaves that design still outside, give
    the mesh a per-LEVEL refinement so a plate can be resolved without refining a spiral.
    **Depends on MIM-12a.** → **MIM-14.**

**One convention added by this round.** *Never gate a full-wave capability on an electrostatic
measurement.* MIM-8's instrument is excellent and its 1 % is true; it is simply not a statement about
a de-embedded s-parameter, and the note that quoted it at a user whose capacitor had the wrong sign
is the cost of not saying which one was measured.

**And a second, added by MIM-12's step 0.** *A gate that constructs its own kernel can stop measuring
what the application does.* MIM-8's capacitance fixture builds its own `PlanarKernelSet` and so never
calls `Dcim.ForStackAtFrequency`, which every run calls — so it measured 10 GHz with no low-frequency
widening and read 1.003 where the run's own arithmetic reads −0.54 at 1 GHz. The divergence was a
whole sign and no test could see it. Item 12's own framing above is wrong for that reason and is
corrected by 12a rather than edited away.

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
