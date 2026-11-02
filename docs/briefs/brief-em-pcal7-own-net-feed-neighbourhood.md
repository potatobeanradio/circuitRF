# Brief PCAL7 — a port feed that runs beside its OWN net, so a spiral inductor simulates as drawn

**Tags:** `R-pcal7-n` · **Area:** `src/Engine/Mom`

**Read first**, end to end, before planning anything: `PlanarFeedExtension.cs`'s file header — it
describes *this exact failure* ("a non-passive answer that reads as a perfect open is what a user
got, with no refusal") for a feed whose CROSS-SECTION is wrong, and this brief is the same failure
caused by a feed whose NEIGHBOURHOOD is wrong, fixed by the same instrument; `PlanarPort.cs`'s
`MeasureFeedClearance` (line ~2030) and `TryFormCalibrationGroup` (line ~2487) and
`TryWidenForNeighbours` (line ~2194), which between them hold the three `mine.Contains(label)` skips
this brief is about; `PlanarSolve.cs`'s widen/group/refuse block (line ~2040), whose ordering decides
everything; `src/Engine/Mom/RESOLVED.md` §PCAL1–§PCAL6; and `docs/design/mom-engine.md` §10.13,
because "just don't de-embed" is already refuted there and must not be re-proposed.

**Do not start by touching the mesher, the fill, the via path or the PCell.** All four are measured
innocent below, and the measurements are cheap to repeat but expensive to redo from scratch.

---

## The problem

**A spiral inductor drawn with ordinary edge ports publishes an open circuit, and the file carries
nothing that says so.** Reported 2026-09-15 against the shipped `examples/PDK PCells` workspace: a
3-turn `KIT_SPIRAL` (w = 10 µm, s = 8 µm, inner 120 µm, Metal1 coil with a Metal2 underpass) on the
100 µm GaAs 2LM starter stack, swept 0–8 GHz at 51 points with everything left at its default.

Reading the published `.s2p` as a series impedance, `Zs = −1/Y₂₁`:

| f (GHz) | Zs (Ω) | implied L | error |
|---|---|---|---|
| 0 | **+1.765 + j0** | — | none: the DC conduction solve, against ~1.95 Ω by hand |
| 0.32 | −584.4 + j2115.6 | 1052 nH | ~300× |
| 0.48 | −282.4 + j1470.3 | 487 nH | ~150× |
| 2.08 | −16.5 + j386.5 | 29.6 nH | ~9× |
| 8.00 | −1.2 + j221.3 | 4.5 nH | ~1.8× |

The coil is **2.5–3.5 nH over 2–8 GHz** (measured under R-pcal7-2 below; modified-Wheeler gives
2.48 nH for 3 turns / 10 µm / 8 µm / 120 µm inner).

**Every AC point has a negative resistance**, `|S₁₁|` exceeds 1 from 0.32 to 1.12 GHz (worst 1.0111),
and the reactance follows no L law at all. The one correct point in the file is 0 Hz, which comes
from `PlanarDcSolve` and never touches any of this.

**What the run says about it** is one sentence, at the bottom of a long notes list, reporting a
0.4–2.2 % passivity excess (σ_max peaks at 1.0225) — and two sentences that are actively misleading:

> `note: Port 1's feed is clear: no other conductor within the 300 µm of line the calibration standard reproduces.`
> `note: Port 2's feed is clear: no other conductor within the 300 µm of line the calibration standard reproduces.`

Both port feeds have metal **8 µm** away, running parallel to them for hundreds of microns. PCAL2's
clearance check — the check that exists precisely to refuse this — reports them clear, because the
metal 8 µm away is the port's own net. On a spiral it always is. **That is the whole bug.**

### It is not the mesh, the vias, the underpass or the PCell

Every control below was run through `Cli em` as a process, on the reported `.cem`'s own mesh
settings, at 2 GHz, and each is a complete structure of its own:

| control | L at 2 GHz | verdict |
|---|---|---|
| 0.7 mm straight Metal1 line | 1.402 nH | ✅ |
| the *same* line broken by a Metal2 underpass and two vias | 1.422 nH | ✅ multi-level + via path innocent |
| 90° bend, 0.6 mm | 1.542 nH | ✅ corners innocent |
| the coil with the underpass deleted, port 1 straight onto its inner terminal | open circuit | ✅ underpass innocent |

The meshed structure is also **not severed** — `PlanarConductors.Of` gives one conducting component
of 2,655 cells carrying both ports, with 50 z-directed via bases, and `FindSeveredConductors` is
silent (correctly: it under-reports by design, and the two ports here stand on different polygons so
it never looks).

**And the wrong answer is mesh-invariant.** On the reduced fixture below, at three refinements:

| N (unknowns) | 382 | 599 | 877 |
|---|---|---|---|
| L at 2 GHz | 135.6 nH | 135.1 nH | 134.9 nH |

It converges, to the wrong number. (Separately worth knowing: the reported spiral needs **23,765
unknowns** at default mesh settings and is refused by the 5,000-unknown dense ceiling, so the
coarsened mesh in that `.cem` is not optional on this part — and it is not the cause either.)

### The A/B that isolates it

Two structures with **identical total length and an identical 50 µm-coupled section**, differing only
in whether the coupled neighbour is present inside the calibration standard's 300 µm window at the
ports:

| fixture | geometry | L at 2 GHz | σ_max(S) |
|---|---|---|---|
| `Splay` | arms coupled 50 µm in the middle, both feeds isolated for 400 µm at the ports | **3.289 nH** | 0.99432 ✅ |
| `Coupled` | same arms, coupled 50 µm all the way to the ports | **135.6 nH** | 1.0037 ❌ |

A 41× error, produced by moving the coupling into the port's window and nothing else.

### The mechanism, in four lines of code

`MeasureFeedClearance` (`PlanarPort.cs:2106`) classifies neighbour metal and then:

```csharp
int label = conn.LabelOf(ci);
if (mine.Contains(label)) continue;   // the port's own net, not a neighbour
```

so own-net metal is never measured, `Breached` comes back false, and `PlanarSolve.cs:2072` —

```csharp
var c0 = Measure(widened[i]);
if (c0 is not { Breached: true, Neighbour: PlanarNeighbourClass.Driven }) continue;
```

— never calls `TryFormCalibrationGroup`. The port therefore keeps D6's **scalar** error box, measured
on an **isolated uniform line** of its own width (standards of N = 103/148, the same size a plain
microstrip's would be). The peel then forms

```
y_ij = (S_meas,ij − δ_ij·a₁₁) / (a₂₁(i)·a₂₁(j))
```

with `a₂₁² ~ 1e-4`, so an error box that is the wrong structure becomes an open circuit — exactly the
amplification `PlanarFeedExtension.cs`'s header already documents for the taper.

**The skip's own rationale is sound and does not cover this case.** It reads *"a flare or pad on the
port's own net is R-fed-1's job, it grows a collinear lead and peels it exactly"* — true of metal
**in line with** the feed. A **parallel return run** of the same net is a different object, and it is
what a spiral inductor is made of.

**Two more things make it worse than a miss.** `TryFormCalibrationGroup` **already declines own-net
metal by name** (`PlanarPort.cs:2564`, *"metal … that is part of the port's OWN net, reaching the
reference plane as a separate run"*) — a sentence that is unreachable today, because the breach that
would call it never fires. And two genuinely **separate** conductors 50 µm apart are handled
correctly: PCAL4 forms a group and builds a modal error box. It is only the same-net case that falls
through the floor.

---

## The fix, and it is measured, not proposed

Three requirements. Each was implemented as a throwaway probe (an env-gated bypass of the three
`mine.Contains(label)` skips, since reverted) and run end to end. **No route below is a guess.**

### R-pcal7-1 — own-net metal is a neighbour

Remove the exclusion at all three sites so the clearance measurement, PCAL3's widening and PCAL4's
grouping can all see it. Own-net metal takes the **driven** threshold (`DrivenNeighbourClearanceHeights`,
5 h) — that is what was measured, because an own-net conductor at the reference plane carries current
the DUT sets and perturbs the local modes at least as much as an independently driven one does.

**Measured effect, alone:** the reported spiral **refuses**, with PCAL2's own sentence, instead of
publishing an open circuit. Necessary; not sufficient.

### R-pcal7-2 — grow the feed until its NEIGHBOURHOOD is clear, and peel it exactly

This is R-fed-1 with its trigger widened from *"the cross-section is not uniform for the end run"* to
*"…or something is inside the clearance for the end run"*. The lead is real metal for the solve and
is removed afterwards as a matched section of the line the calibration itself measured, so **the
user's reference planes stay on the user's own drawn metal edges** — which is what makes this not an
approximation. A de-embedded port *is defined as* what an ideal matched line feeding that edge would
measure; growing the line and peeling it is that definition, executed.

**Measured on the reported artwork, unchanged — same `.clay`, same port labels, same `.cem`:**

| lead grown on each port | clearance verdict | L at 2 GHz (incl. leads) | σ_max |
|---|---|---|---|
| none (today) | "feed is clear" (wrongly) | 31.7 nH | 1.0183 ❌ |
| 150 µm | still breached | refused | — |
| **265 µm** | **clear** | **4.125 nH** | 0.99516 ✅ |
| 340 µm | clear | 4.104 nH | ✅ |

Subtracting the leads at the run's own `Z_c·√ε_eff/c` = 0.881 nH/mm puts the coil at **3.5–3.7 nH**
at 2 GHz, against a modified-Wheeler estimate of 2.48 nH. **Overshoot costs 0.5 %**, which is the
signature of an exact peel and is what licenses rounding the lead up.

**Swept over the reported band, the same artwork with 265 µm leads:**

| f (GHz) | Zs (Ω) | L coil (nH) | σ_max |
|---|---|---|---|
| 0.32 | −3.73 + j77.2 | 37.9 | **1.0466** ❌ |
| 2.24 | +0.64 + j55.3 | 3.46 | 0.99510 ✅ |
| 4.16 | +0.82 + j87.4 | 2.88 | 0.99433 ✅ |
| 6.08 | +0.88 + j120.6 | 2.69 | 0.99319 ✅ |
| 8.00 | +0.84 + j151.7 | 2.55 | 0.99172 ✅ |

**From ~2 GHz up this is a real inductor**: positive R, passive, and landing on Wheeler at the top of
the band. **At 0.32 GHz it is still wrong** — 37.9 nH and 4.7 % non-passive, improved 28× from the
published 1052 nH but not fixed. That residue is **brief PEEL's `1/(2βΔℓ)` amplification**, not this
brief's: at 0.32 GHz, `2βΔℓ` ≈ 8e-3 on this stack, which predicts an amplifier of ~10², the size
observed. **This brief must not try to absorb it, and must not tune anything to make it look
better.** See M5.

**The length rule is `L ≥ endRun − u*`**, where `u*` is the distance inward from the port's drawn
edge at which offending metal first appears. For port 1: the coil's outer edge sits 40 µm inward of
the lead's edge, `endRun` = 3 h = 300 µm, so `L ≥ 260 µm` — and 265 µm clears while 150 µm does not.
The rule is exact, not a heuristic.

**Cost on this part: none.** 4,189 unknowns with the leads against 4,814 without — the tensor grid
redistributes. That is a happy accident of this geometry and not a general claim; see M4.

### R-pcal7-3 — where extension cannot clear, PCAL4's modal group already works

When the obstruction is a *parallel* own-net run — two terminals brought out side by side, which is
the other common inductor drawing — extension never clears it, because the neighbour's own lead grows
with it. There the neighbour is a driven conductor at a shared reference plane, which is exactly what
PCAL4 is for. Lifting the own-net decline in `TryFormCalibrationGroup`:

```
note: Ports 1, 2 form one CALIBRATION GROUP: their feeds are mutually coupled at the reference
      plane, the nearest pair 50 µm apart. They share one 2-conductor calibration standard and one
      MODAL error box of 2×2 blocks … The profile spans 0 µm to 70 µm across.
```

| `Coupled` fixture | L at 2 GHz | \|S₂₁\| | σ_max |
|---|---|---|---|
| today | 135.6 nH | 0.0586 | 1.0037 ❌ |
| own-net decline lifted | **1.165 nH** | 0.9799 | 0.9999 ✅ |

against a two-wire estimate of ≈ 1.0–1.3 nH. **The decline's stated objection — "two conductors the
structure shorts together somewhere this profile cannot see" — is not borne out**: the error box is a
local property of the cross-section and the excitation, and the DUT's topology beyond the reference
plane does not enter it. M3 must still prove that with an oracle rather than on this one number.

---

## What this does NOT fix, and must keep refusing

`Hair50` — a 300 µm hairpin whose U-turn sits **inside** the 300 µm end run — refuses after
R-pcal7-1, and should. No lead and no group can reproduce a feed that bends within the length the
standard replaces. **A refusal with PCAL2's sentence is the right answer there**, and it is a strict
improvement on today's silent 159 nH.

`TryWidenForNeighbours`' own-net decline (`PlanarPort.cs:2280`) stays a decline: PCAL3's widened
neighbour is *floating at zero net charge*, which an own-net conductor is not, and inventing a third
boundary condition is not this brief. What changes is that the sentence now **reaches the user**.

---

## Milestones

### M0 — stand the fixtures up. No solve, no full-wave cost.

Six reduced fixtures, all 10 µm Metal1 on the GaAs 2LM starter stack, all two-port, all solvable in
seconds to a couple of minutes each. Build them as engine fixtures, not as `.clay` files in a user
workspace:

| name | geometry | today | must become |
|---|---|---|---|
| `Straight` | 700 µm line | 1.402 nH ✅ | **bit-identical** |
| `ViaHop` | same, via → Metal2 → via | 1.422 nH ✅ | **bit-identical** |
| `Bend` | one 90° corner | 1.542 nH ✅ | **bit-identical** |
| `Splay` | coupled in the middle, feeds isolated | 3.289 nH | see M1's warning |
| `Coupled` | 800 µm arms, 50 µm apart to the ports | 135.6 nH ❌ | ≈ 1.2 nH, passive |
| `Hair50` | 300 µm arms, U-turn inside the end run | 159 nH ❌ | **refusal** |

Plus the acceptance fixture: `examples/PDK PCells` already carries the spiral cell and the generated
`KIT_SPIRAL` artwork **byte-identical** to the reported workspace — it is missing only the two port
labels and an `em/SpiralInductor.cem`. Add both. That makes the fixture reproducible in-repo, and it
makes the shipped example actually demonstrate EM on a PCell, which it currently does not.

### M1 — R-pcal7-1, and the regression question it raises

Three sites, one behaviour. Then **run the whole engine fixture set and report every number that
moved**, because this is the milestone that can quietly change answers that pass today.

**It is not a no-op, and the brief will not pretend it is.** Measured: `Straight`, `ViaHop` and `Bend`
come back **bit-identical to twelve significant figures** — a feed with nothing inside its clearance
reaches none of this, which is R-pcal4-1's own invariant and it holds. But **`Splay` changes**:

| `Splay` | L at 2 GHz | σ_max |
|---|---|---|
| today | 3.289 nH | 0.99432 |
| own-net visible (its two feeds are 350 µm apart, so a group forms) | 1.554 nH | 0.99990 |

Both are passive; both are plausible; **neither is known to be right.** `Splay` passes today with no
flag on it, so this is a file whose published answer would change by 2× with nothing in the run
saying which version to believe.

**Settle it with an oracle before shipping M1, and the oracle is a control, not a reference file.**
Two coupled uniform lines of length ℓ, ports at all four ends: de-embedded, the answer must be a pure
coupled-line section — `|S₁₁|` ≈ 0 and each mode's transmission exactly `e^{−γ_m ℓ}` for the γ_m the
calibration itself measured. That is §QSC §5's instrument applied to a pair, it needs no external
model, and **the error is the answer**. Run it at the separations the fixtures use (18 / 50 / 350 µm)
and at two lengths.

**Stop and report if the control says the grouped path is the worse of the two** — that inverts M3
and most of this brief.

### M2 — R-pcal7-2, the neighbourhood shortfall

`PlanarFeedExtension.Extend` already runs **before meshing**, on the artwork, and already computes
`wanted = EndRunHeights × slabHeight`. Add a second shortfall term beside the cross-section one and
take the max.

- **Measure `u*` on the ARTWORK, not on the mesh.** The clearance check lives after meshing and this
  has to run before it; re-deriving it from polygons is the same thing `Extend` already does for
  uniformity. The post-mesh `MeasureFeedClearance` then *confirms* — and if it still breaches, that
  is the refusal, with no loop.
- **Include every other port's grown lead in `u*`.** This is what makes non-convergence detectable
  rather than infinite: compute the leads, recompute `u*`, and if the shortfall has not fallen, the
  obstruction is parallel and the port is M3's, not M2's. One fixed-point step is enough; do not
  iterate to a limit.
- **The existing declines stay declines** — a lead that would run into other metal, an end face that
  is not a single straight segment, a port whose level is ambiguous. Each already reports itself.
- **The note must say which shortfall grew the lead.** "60 µm on top of 239 µm it already had"
  currently means *the cross-section changed*; a lead grown because the coil is 8 µm away is a
  different sentence and a user chasing it needs to know which.

**Gate:** the acceptance fixture publishes 4.1 ± 0.2 nH at 2 GHz (leads included) with σ_max ≤ 1.005,
and 2.5–3.5 nH of coil across 2–8 GHz, with **no change to the `.clay`, the port labels or the
`.cem`**. And `Straight`/`ViaHop`/`Bend` grow no lead and stay bit-identical.

### M3 — R-pcal7-3, the own-net calibration group

Lift the decline at `PlanarPort.cs:2564`. Then handle the interaction M2 creates:

- **A group's members must peel the same length.** `CommonPeelLength` already refuses a group whose
  leads differ, and M2 will routinely produce leads differing by microns. **Grow every member of a
  group to the longest member's lead** — a longer lead is always peelable exactly, so this costs
  accuracy nothing and removes a refusal that would otherwise fire on almost every grouped part.
- **Ordering: M2 first, then M3, then the refusal.** Extension is exact and removes the problem;
  grouping models it. Where both could apply, the extended answer is the one to trust, and M3's
  answer must agree with it — which is the gate.
- **Report the group's own conditioning.** A 2-conductor modal box on two conductors that are the
  same net can have a mode the DUT barely excites; `ModeSeparationDegrees` already exists and PCAL6
  already knows it is corrupted by the same 1/ω amplification at the bottom of a band. Say what it
  read.

**Gate:** `Coupled` lands at 1.2 ± 0.2 nH, passive; the M1 coupled-line control passes at the same
separation; and a structure where M2 *can* clear gives the same answer through both paths to within
1 %.

### M4 — cost, and the ceiling

A grown lead adds gridlines to a tensor grid shared by the whole layout. On the acceptance fixture it
**reduced** N (4,814 → 4,189), which is luck, not a law.

- Measure ΔN across the fixture set and on one real board.
- **If the lead would push the mesh past the ceiling, refuse by name** — quoting the unknown count,
  the lead that caused it, and the fact that it was grown rather than drawn. The existing ceiling
  refusal is already the best-written message in this area; extend it rather than adding a second.
- Do **not** shorten `EndRunHeights` to buy the budget back. It sizes the calibration standard, and
  trading calibration quality for mesh size silently is the trade this whole brief exists to stop.

### M5 — acceptance, on the file as the user wrote it

`circuitrf em examples/PDK\ PCells/em/SpiralInductor.cem`, 0–8 GHz, 51 points, **nothing in the
workspace edited**. Every point must be right to a stated tolerance, or refused with a sentence.
"Published silently and 10× wrong" must not be reachable from that file.

Report: the `Zs = −1/Y₂₁` table over the whole sweep against the 3.5–3.7 nH extracted here; σ_max at
every point; and the DC point, which is already right and must stay right.

**The bottom of the band is brief PEEL's, not this one — and it is already measured to survive M2.**
With the feeds cleared, 0.32 GHz still reads 37.9 nH at σ_max = 1.0466 (table above). So the honest
acceptance statement for this brief is:

- **2 GHz – 8 GHz: right**, passive, 2.5–3.5 nH, and that is the gate.
- **Below ~1 GHz: still wrong, and it must be SAID** — a note or a refusal, not a published number
  with a 4.7 % passivity excess buried in the notes. Whether that is a guard here or PEEL's
  `2βΔℓ` conditioning gate is a scheduling question, but **one of the two must ship before this
  example is called fixed**, because the reported sweep starts at 0 Hz and a user reading a plot
  will read the bottom decade first.

Do not tune a threshold until the low points look better.

---

## Notes for whoever picks this up

- **Probe before you build.** The whole diagnosis above was reached with a scratch console that calls
  `EmSetupPersistence.LoadFromFile` → `EmSetupResolver.Resolve` → `EmGeometry.Flatten` →
  `PlanarExtractor.Extract` → `EmPortExtraction.Extract` → `PlanarGroundPath.Extend` →
  `PlanarFeedExtension.Extend` → `PlanarKernel.Mesh` → `PlanarPorts.ResolveAll` →
  `PlanarConductors.Of`, and prints connectivity and unknown counts. **It runs in 0.6 s** and
  answered "is the mesh severed?" and "how many unknowns at default settings?" without a single
  full-wave solve. Build it first.
- **The three `mine.Contains(label)` sites are easiest to explore behind an env flag** before being
  removed properly — that is how the R1/R2/R3 numbers in this brief were obtained, and it lets you
  run the same fixture with and without in one loop.
- **`DeembedResidual` is not a quality measure here either.** On the reported file it reads
  2.78e-9 at 0.32 GHz, the point that is 292× wrong, and rises to 3.9e-8 at 8 GHz where the answer is nearly right — anti-correlated with the truth. Brief PEEL says why; do not use it as a gate.
- **One loose end nobody has chased:** the reported sweep asks for 51 points and the file carries 50
  — 0.16 GHz is absent, with no note. Probably the calibration-validity floor. Worth one look during
  M5, and worth a note wherever a point is dropped.
