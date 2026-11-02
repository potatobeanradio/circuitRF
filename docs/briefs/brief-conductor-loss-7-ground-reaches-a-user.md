# Brief CL7 — the ground plane reaches a user

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl7-n` · **Area:** `src/Engine/Mom`, `src/Design/Layout/Em`, `docs/user/src/reference` ·
**Depends on:** CL5 and CL6 both shipped

**Read first:** `RESOLVED.md` §CL4 **§7 (the kernel-A comparison and what its SHARE column does NOT
say), §8 (all four traps — every one of them becomes user-visible in this brief) and §9**;
`RESOLVED.md` §CL3 in full, because this brief is the same shape and §CL3 §1 is the instrument §0
below uses; `RESOLVED.md` §QSC; `PlanarExtractor.cs:873-925` and `:1379-1470` (the two places a
medium is built) and `:1247` (`generalMedium`, the line that decides which kernel a design gets);
`EmSnpProvenance.cs:200-220`; `PlanarSolve.cs:400-475` (`DescribedByTheSlab`).

---

## The problem

CL4 built a conducting ground plane on the general kernel and CL6 built it on the one-slab kernel.
**Every gate passes and no run a user can make gets one**, because `PlanarExtractor` writes
`Termination.Pec` (`:1470`) and a floorless `GroundedSlab` (`:920`). This brief writes the real
floor, absorbs the consequences, and puts CL3's gates under the result — the three CL4 §9 named as
missing: **no passivity/reciprocity direction check, no hero check, no golden-move tabulation.**

It is worth **21.1% of the conductor term on FR-4, ~11% on the MMIC starter and 25.0% on a low-loss
laminate** (overview §2). On the MMIC starter the conductor term is itself 92-99% of the line's loss,
so this is about a tenth of the total loss of every GaAs line the tool draws.

**It is wide, not deep**, and every hard question in it was already answered by CL4's own traps. Two
of them are milestones rather than details, because both are silent and both become user-visible at
exactly the moment the extractor changes.

## 0. Milestone 0 — where the quasi-static path's γ gets its GROUND term. This GATES the flip

**Exactly CL3 §0's problem, one surface over, and CL4 §1 already sized it.** Below
`PlanarCalibration.QuasiStaticCrossoverHz` a port is calibrated from a γ supplied by the standard's
own electrostatics, where the floor is a PEC by construction and nothing reads the termination this
series re-pointed.

| stack | crossover | at 2 / 10 / 20 GHz |
|---|---|---|
| FR-4 1.6 mm starter | **3.048 GHz** | quasi-static / measured / measured |
| **MMIC starter, 100 µm GaAs** | **26.07 GHz** | **quasi-static / quasi-static / quasi-static** |

**CL3's answer does not extend, and the reason is structural rather than incidental.** Its supplier is

```
R = Σ_levels Re(Z_s(ω,σ,t)) · [ ΔS_level · Δℓ / |ΔT|² ]
```

— a sum over **MESHED** levels, read off the same charge vector the fill loaded. **The plane is not a
meshed level and has no `ΔS` of its own.** So after this brief a GaAs line's `S` carries a ground term
at every frequency and the γ its error box is solved against carries one at none of them.

**Three candidates. Two of them are already ruled on by doctrine and one is the work.**

- **(a) The plane's own INDUCED CHARGE, through the same TEM identity — the leading candidate.**
  `R = Re(Z_s)·∫|q_s|²dx/(∫q_s dx)²` is not specific to a drawn conductor; it needs a surface and a
  charge density on it. The electrostatic solve that produces the strip's `q` determines the plane's
  induced density completely, and `Re(Z_s)` is `PlanarSurfaceImpedance.Plane` — CL6's floor, not a
  fourth spelling. **The whole of CL3 §1's argument transfers if and only if this comes off the same
  solve and the same mesh**: that is what made the static-charge supplier track the fill's own term
  across a 32% edge-mesh change while kernel A did not move at all. Deriving the plane's density and
  showing it moves with the mesh the way the fill's ground term does **is** milestone 0.
- **(b) Leave γ's ground term out and state the residue at its measured size.** Legitimate, and a far
  more defensible NO than CL3's equivalent was: CL3 declined it because it was a **4× error** on GaAs,
  where this is ~11% of the conductor term. **But "more defensible" is not a measurement** — the
  residue's effect on the published `S`, `planar.Gamma` and `planar.Zc` has to be measured on CL3
  §0's own three consequences (the error box's `e^{−α·Δℓ}`, Z_c's `√(1 + R/(jωL))` magnitude AND
  phase, and the step at the crossover in a quantity that is physically smooth) before it can be
  chosen.
- **(c) Kernel A's Wheeler ground term. REFUSED by doctrine**, R-qsc-2's first bullet and
  `PlanarDeembed`'s D7 rule: kernel A is the ORACLE and is not an input. CL4 §7 is an independent
  reason — kernel A's ground term agrees with kernel B's only where the substrate is electrically
  thin (6% at k₀H ≤ 0.07) and the two part company above it.

**The instrument is CL3 §0's own**: α_c as kernel B's two LOSSY standards see it, minus the PEC one,
on the same standards at four frequencies and four meshes — now with the GROUND's σ as the variable
and `PlanarFillSettings.PerfectConductor` held off, since that flag makes the strip perfect and **not
the plane** (CL4 §8). The supplier tracks it, or it does not.

## 0b. Milestone 0b — the power budget stops labelling the ground's loss "dielectric"

**CL4 §8's fourth trap, which was harmless while no run had a conducting floor and is not afterwards.**
`P_conductor` is an integral over the fill's own basis functions and the plane has none, so what the
plane absorbs arrives as everything-else-minus and is booked against `DielectricW`. **On a low-tanδ
substrate that is most of what the dielectric line reads** — on the MMIC starter, tanδ = 0.002 against
a ground term worth ~11% of a conductor term that is 92-99% of the loss.

CL4 deliberately did not fix it: its own "Must NOT" reserved CL2's residual arithmetic, and splitting
the plane out needs a second quadratic form in the spectral domain rather than a relabelling. **This
brief may not leave it unstated, because this brief is what puts a number in front of a user.**
Decide explicitly, and record which: split it out, or **rename and re-document the line** so it says
what it contains. A budget line whose label became false is worse than a missing one — CL4 §6's own
rule, applied to the other end of the same physics.

## The change, which really is small

Both medium-building sites read what they already hold. `groundBand` carries `Layer.SigmaSm` and its
own `TopM`/`BottomM`, so σ and t are in hand at `PlanarExtractor.cs:920` and `:1470` alike, and the
floor is `Termination.LossyGround(σ, t)` — CL4's constructor, unchanged.

**Three pieces of plumbing that fail silently if missed**, all of them already found once:

1. **`EmSnpProvenance` must hash the floor on the `GroundedSlab` spelling too.** CL4 §8 found the hash
   reading `ms.Bottom.Material.EpsR`, which is `EmMaterial.Air` for a PEC and a conducting plane
   alike, and fixed it for `LayerStack`. **Two runs differing only in the ground's σ must not share a
   cached `.snp`**, on either path.
2. **`PlanarSolve.DescribedByTheSlab` and the DCIM fit cache key** take `GroundedSlab` as an identity.
   CL6 gave it a floor; if either still compares the floorless part, a lossy-ground run is served a
   perfect-ground fit or pushed onto the wrong C_pul route, and both are plausible-looking answers.
3. **A plane that is absorbed or skipped has no σ to read.** `PlanarExtractor.cs:739`'s comment
   records that a conductor band which is neither a level nor the return plane is absorbed into a
   neighbouring dielectric — *"18 µm of copper becomes 18 µm of FR-4"*. Those paths must not acquire
   a floor by accident, and the existing warnings stay exactly as they are.

**A perfect floor must remain spellable, and `PerfectConductor` is NOT it.** That flag makes the strip
perfect and nothing else, because the plane is not in the fill (CL4 §8) — so the re-bless's two-sided
assertions need a separate way to say "ground perfect". **Name it and keep it permanently**, on
`PerfectConductor`'s own pattern: every CL4, CL6 and CL7 ground figure is a comparison against it, and
a measurement whose reference cannot be reproduced is not a measurement.

**No `.cem` key and no UI control**, per overview §5 and §D3's rule: whether Maxwell's equations
include Ohm's law is not a decision a user should be asked to make. It never reaches
`EmSetupPersistence`.

## On sizing the re-bless

**CL3 planned for ~90 files and moved two tests in one file.** Do not size this one from a file count
either — and do not size it from CL3's outcome. **CL3 moved the strip; this moves every microstrip
run's α by 11-25% of a conductor term that, on the substrate class this series exists for, is nearly
all of the loss.** Plan it as a re-bless, run the suite ONCE, and triage from the TRX.

## Gates

- **R-cl7-1 — a perfect floor is bit-identical to pre-CL7.** The oracle spelling above, on both
  starters at 2 / 10 / 20 GHz, through `PlanarSolve` rather than through the kernel. This is what says
  the move came from the plane and not from something else in the same commit.
- **R-cl7-2 — against kernel A, both surfaces lossy, and gate the TERM.** Uniform 50 Ω lines on both
  starters, γ from the two-line extraction directly so the QSC crossover is not in the path, the four
  alphas of CL4 §7 ({PEC, real} strip × {PEC, real} ground) off one mesh and one pair of standards.
  **Target: the ground TERM agreeing with `RlgcModel.RMatrix`'s to CL4's measured ≈6% wherever
  k₀H ≤ 0.07**, plus its two independent signatures — the 1/√σ scaling (CL4 measured 2.0033 against
  √4 = 2) and additivity (`(α_strip + α_ground)/α_total` = 1.0002 … 1.0021).
  **Do NOT gate the SHARE.** CL4 §7 established that kernel B reads 27.48% where kernel A reads 21.06%
  on the same row, and that the inflation is exactly CL1's measured single-sheet strip deficit in the
  denominator: *"quoting kernel B's share as the ground's share of the conductor term would be quoting
  CL1's deficit back with the opposite sign."* Above k₀H ≈ 0.3 neither kernel is authoritative — kernel
  A's quasi-TEM rule has no radiation term and the two-line instrument has lost resolution — and those
  rows are **reported, not resolved**.
- **R-cl7-3 — milestone 0's decision, measured** on CL3 §0's instrument, tabulated at four frequencies
  and four meshes on both starters, whichever way it goes.
- **R-cl7-4 — milestone 0b's decision, recorded**, with the size of what the dielectric line was
  carrying on both starters.
- **R-cl7-5 — the three checks CL4 carried none of.** Passivity and reciprocity with a DIRECTION check
  (loss may only increase); the hero check; the golden-move tabulation.
- **R-cl7-6 — the re-bless, triaged from the TRX in ONE run**, on CL3 §4's terms: every re-blessed
  literal asserted as exact equality **beside the pre-CL7 one**, which is asserted against the
  perfect-ground oracle, and the per-entry move **range-asserted** rather than merely recorded. A
  move nobody can account for is a finding, not a re-bless — CL3 §4's second row was exactly that.
- **R-cl7-7 — `tests/Ui.Tests/Em` green**, including the `.cem` round trip and `EmCliVerbTests`'
  byte-for-byte comparison of `circuitrf em` against `EmRunService.Run`. **The provenance claim is
  ASSERTED rather than assumed** (R-cl3-2's shape): two stackups differing only in the ground's σ
  must produce different hashes and must not share a cached `.snp`.

## Must NOT

- Add a `.cem` key, an `EmSetupPersistence` field, or any UI control for this. Overview §5.
- Widen any validated range, `PoleLossCeiling`, or `CalibratedPathProduct` to make a gate pass.
- **Let the half-measure back in.** A floor written only where `generalMedium` (`PlanarExtractor.cs:1247`)
  is already true would give two physically identical designs different ground physics according to
  whether the stackup carries one dielectric entry or two — **a user adding a solder mask would
  silently change their ground model.** CL4 §9 refused it and CL6 exists so it does not have to be
  reconsidered.
- Put a lumped resistance at a ground attachment's lower terminal. The plane's loss arrives through
  the termination; a spreading resistance on the basis as well would double-count it (CL4 §5).
- Re-bless one-sided, or re-bless a move that has not been accounted for.
- Quote kernel B's ground SHARE as the ground's share of the conductor term, in a gate, a note, a
  diagnostic or the user docs. The overview's 21.1% / ~11% / 25.0% are statements about the PHYSICS,
  reproduced by kernel A to the digit.
- Take kernel A as an INPUT anywhere, in milestone 0 or outside it.
- Mesh the ground plane, or re-open `FrontToBackDb`.

## On completion

- `src/Engine/Mom/RESOLVED.md` `§CL7`: milestone 0's measurement and its decision, milestone 0b's
  decision and the size of what it was hiding, R-cl7-2's four-alpha table on both starters, and the
  full re-bless triage with every move attributed.
- `docs/user/src/reference/mom-engine.md`: **the ground-plane limit is RETRACTED** — it has stood
  through CL3 and CL4 with its measured size and is now false. Say what replaced it.
- **No `CLAUDE.md` edit.** §5's conductor-loss bullet, §5's validated-range table and §7's
  antenna-metrics bullet all carry lines that this brief makes stale, on top of the ones CL4 §11 and
  CL6 already reported. **Report them to the owner by file and line.**
