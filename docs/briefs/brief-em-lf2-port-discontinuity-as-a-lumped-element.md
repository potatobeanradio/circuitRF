# Brief LFP — the low-frequency wall is the PORT, and the coil finally measures it

**Tags:** `R-lfp-n` · **Area:** `src/Engine/Mom`

**Read first**, end to end, before planning anything: `src/Engine/Mom/RESOLVED.md` §PEEL — especially
§1 (Δℓ is INERT, measured over a 30× range; the amplifier is `1/|a₂₁|²`), §2 (γ is innocent by eight
orders), §5 (three routes tried, none moved the number) and its closing paragraph, which already
names the answer this brief is about; §PCAL7-OWNNET §10 and §11, which is where the numbers below
come from; `src/Engine/Mom/RESOLVED.md` §LF1 §4-5 and §LF2; `docs/design/mom-engine.md` §10.13,
because *"publish an uncalibrated raw point"* is refuted there and must not be re-proposed; and
`PlanarDeembed.cs`'s `PlanarErrorBox` header, `PlanarDcSolve.cs`, and `PlanarExcitation.cs`.

**Do not start by** lengthening the calibration standards (PEEL §1 measured it inert AND worse up the
band), refining the mesh (§2 below measures it worse here too), chasing `DeembedResidual` (PEEL §6:
anti-correlated with the truth), or tuning `PeelErrorBudgetDS`. All four are measured dead ends and
the measurements are cheap to repeat and expensive to redo.

---

## The problem

**A part whose answer is right at 2 GHz is nine times wrong at 0.32 GHz, and the diagnostic that is
supposed to say so under-predicts the error by a factor of forty.**

The shipped `examples/PDK PCells` spiral, after brief PCAL7 fixed its feed neighbourhood
(`Zs = −1/Y₂₁`, 0–8 GHz, 51 points, `circuitrf em` on the file as written):

| f (GHz) | Zs (Ω) | L | σ_max | verdict |
|---|---|---|---|---|
| 0 | +1.774 + j0 | — | 1.0000 | right — `PlanarDcSolve`, no calibration in the path |
| 0.16 | −9.93 + j111.1 | 110.6 nH | 1.0919 | **not a network** |
| **0.32** | **−2.67 + j63.47** | **31.6 nH** | **1.0384** | **not a network, ~9× out** |
| 0.48 | −0.95 + j47.62 | 15.8 nH | 1.0152 | not a network |
| 0.64 | −0.29 + j40.80 | 10.1 nH | 1.0046 | not a network |
| 0.80 | +0.04 + j37.75 | 7.51 nH | 0.9991 | passive, still ~2× out |
| 2.08 | +0.60 + j45.51 | 3.48 nH | 0.9976 | right |
| 8.00 | +1.07 + j141.0 | 2.81 nH | 0.9960 | right |

**The owner's report, 2026-09-15: commercial planar MoM solvers do not have this wall.** That is the
brief's premise and its target. It is consistent with everything measured below: nothing here is a
property of the method of moments, of the layered Green's function, or of this coil. It is a property
of **how this engine's edge port is excited**, and the repository has said so twice already without
acting on it (LF1 §5(a), LF2's closing paragraph, PEEL §5).

---

## What is actually happening, measured

All of this is from a scratch console that solves the coil at one frequency and reads the calibration
off `PlanarFrequencyPoint.Calibrations`. Each number is ~15 s. **No route below is a guess.**

### 1. The answer lives in the seventh decimal place of the raw solve

At 0.32 GHz, the DUT's own raw s-parameters are

```
raw   |S₁₁| = 1.000000        |S₂₁| = 1.552e-06
a₁₁ = 0.999999562 − j0.000488  a₂₁ = 3.317e-05 + j3.086e-04   (|a₂₁| = 3.104e-04)
```

`PlanarDeembed.Apply` forms `y = (S_meas − a₁₁)/a₂₁²`. The numerator is the difference of two numbers
that agree to seven digits; the denominator is 9.6e-8. **A 20 mm piece of metal reads as a perfect
open, and the whole of the published answer is the part of it that does not.**

### 2. The amplification of the DUT's OWN error is `≈ 2/|a₂₁|²`, and it goes as 1/ω²

Perturb the DUT's raw S by a relative η, re-apply the *same* error box — nothing is re-solved, so this
isolates the peel's sensitivity to the DUT's own number from the standards' inconsistency:

| f | \|a₂₁\| | measured amplification | required raw accuracy for \|ΔS\| ≤ 0.01 |
|---|---|---|---|
| 0.32 GHz | 3.104e-04 | **2.11e+07** | **4.7e-10 relative** |
| 2.08 GHz | 1.973e-03 | **4.90e+05** | 2.0e-08 relative |

Linear in η from 1e-13 to 1e-9 at both frequencies, so the slope is real and not a saturation
artefact. The ratio is **43×** against `(2.08/0.32)² = 42.3` — **exactly ω²**, over a 6.5× band.

**One number explains the whole sweep.** Take the engine's own raw-S relative accuracy as a constant
ε and the published error as `ε × 2/|a₂₁(f)|²`. The 2 GHz answer is right to about 1e-2 in S, which
puts ε ≈ 2e-8; the same ε predicts 0.42 at 0.32 GHz (realised: the published |S₁₁| is 0.543 against
≈ 0.070 for a 3.5 nH coil, i.e. ≈ 0.47) and predicts total garbage at 0.16 GHz (realised: 110 nH,
σ_max 1.09). **The wall is not a threshold anyone chose. It is where `2ε/|a₂₁|²` crosses one.**

### 3. `DeembedErrorFloor` measures the STANDARDS' share and nothing else

`ConsistencyResidual × PeelAmplification` reads **1.106e-02** at 0.32 GHz while the realised error is
**≈ 0.47** — under-predicting by ~40×. That is not a bug in the floor; it is the floor's definition.
PEEL derived and validated it on a **uniform line**, where the DUT's own S is the same shape as the
standards' and its error cancels. On any other structure the DUT's own error is amplified by the same
`1/|a₂₁|²` and **nothing measures it at all.** This is why the shipped per-point guard did not fire on
a row that is 9× wrong.

### 4. Refining the mesh makes it worse, on this part, not just on a line

| coil mesh | N | \|a₂₁\| | amplification | L at 0.32 GHz |
|---|---|---|---|---|
| across = 1 | 282 | 4.893e-04 | 8.43e+06 | 31.88 nH |
| across = 2 | 1,697 | 3.104e-04 | 2.11e+07 | 31.56 nH |
| across = 3 | 4,239 | 2.437e-04 | 3.43e+07 | 33.52 nH |

The gap is one cell wide, so a finer mesh is a smaller gap and a smaller `a₂₁` (LF2's finding,
reproduced here on a real part). **A 15× refinement makes the conditioning 4× worse and moves the
answer away from the truth.** Refinement is not a route and must not be offered as a remedy.

### 5. THE ERROR BOX IS A LUMPED SERIES CAPACITANCE, AND THIS IS THE OPENING

The measured box, both ports, both frequencies:

| f | a₁₁ | a₂₂ | \|a₁₁ + a₂₁ − 1\| | \|a₁₁ − a₂₂\| | implied series C |
|---|---|---|---|---|---|
| 0.32 GHz | 0.999999562 − j4.881e-4 | 0.999826708 − j8.023e-4 | **1.82e-04** | 3.59e-04 | **1.55 fF** |
| 2.08 GHz | 0.999991255 − j3.173e-3 | 0.999797676 − j5.112e-3 | **1.20e-03** | 1.95e-03 | **1.51 fF** |

A **pure series element** has `a₁₁ = a₂₂ = Z/(Z + 2Z₀)` and `a₂₁ = 2Z₀/(Z + 2Z₀)`, hence
`a₁₁ + a₂₁ = 1` exactly. Two things follow and both are load-bearing:

- **The implied capacitance is frequency-independent to 3 % over a 6.5× band** (1.55 fF against
  1.51 fF). It is a real, physical, lumped quantity — the delta gap's own series C, which is what
  §10.13 measured from the other side as a clean 6 dB/octave rise in the raw `|S₂₁|`.
- **The departure from a pure series element is ∝ ω and therefore VANISHES exactly where the present
  method fails**: `|a₁₁ + a₂₁ − 1|` is 1.20e-3 at 2.08 GHz and 1.82e-4 at 0.32 GHz, a ratio of 6.6
  against a frequency ratio of 6.5.

**So at the bottom of the band the port discontinuity IS one lumped series element, and removing a
series element is a subtraction of a small number from a small number.** The catastrophic
cancellation is a property of doing it in S.

---

## The three routes, in the order they should be tried

### R-lfp-1 — MEASURE ε FIRST. It decides everything else, and a negative result is a result.

§2 infers ε ≈ 2e-8 from two published answers. **Measure it directly** rather than inferring it:
solve one structure at one frequency twice, changing one thing at a time (the Green's-function fit's
sample count and tolerance, the quadrature order, the LU vs iterative path, the floating-point
accumulation order in the fill), and read how far the raw S moves.

- **If ε is dominated by something cheap to tighten** — a quadrature order, a fit tolerance — then a
  decade of ε buys a decade of band edge, and R-lfp-2 and R-lfp-3 may not be needed at all. That
  outcome would be the whole fix and it is the reason this is first.
- **If ε is a discretisation floor** (the mesh, the rooftop basis), it cannot be tightened without
  refining, and §4 measures refining as a net loss. Then the answer is R-lfp-2 or R-lfp-3.

**Report ε as a number with its dominant contributor named.** Do not proceed past this milestone
without it: both routes below are worth building only if ε cannot be moved.

### R-lfp-2 — remove the port discontinuity as a LUMPED SERIES ELEMENT, below a crossover

§5 is the evidence. Concretely:

- Extract the gap's series impedance where the box is **well conditioned** — the top of the band, or
  wherever `2/|a₂₁|²` is small — and check it against `Z = 2Z₀·a₁₁/a₂₁`.
- Below the crossover, de-embed by removing that element **in Z or Y**, per port, rather than by
  `(S_meas − a₁₁)/a₂₁²`.
- **The crossover is not a taste threshold and must not become one.** It is where the dropped term —
  the box's departure from a pure series element, `|a₁₁ + a₂₁ − 1|`, which is measurable at every
  frequency — becomes smaller than the error the S-domain peel is making, `ε·2/|a₂₁|²`. Both sides
  are quantities the run already has or can compute. Derive it; do not pick it.
- **QSC's own precedent applies and should be followed rather than re-argued**: a quantity that
  cannot be measured at the bottom of a band is supplied from a well-conditioned regime instead, the
  switch is reported per point in the diagnostics group, and the two paths are required to agree at
  the crossover.

**What could make this fail, and it must be measured rather than assumed:** the real part of the
implied Z is 34 kΩ against a reactance of 320 kΩ at 0.32 GHz (Q ≈ 9), which is not a capacitor. That
real part is derived from `a₁₁/a₂₁` with `a₁₁ ≈ 1`, so it may be entirely an artefact of the same
cancellation — or it may be a genuine radiation/loss term the series model drops. **Settle which
before building on it**: a lumped element extracted from a badly-conditioned quotient is not a
lumped element.

### R-lfp-3 — the PORT, which is what the repository has been pointing at since LF1

`1/|a₂₁|²` is the whole amplifier, and `a₂₁ ∝ ω` because §10.6's edge port is a **series delta gap**:
a cut in the metal one cell in from the edge, driven against its own rooftop. LF1 §4 states the
consequence — *"every line would read as an open at both ends"* — and §1 above is that sentence
measured on a real part.

**`PlanarDcSolve` already solves this exact coil correctly at 0 Hz**, publishing +1.774 Ω against
~1.95 Ω by hand, because it uses **conduction terminals**: the conductor's end cells against the
ground node, not a cut in the metal. A port of that shape has an `a₂₁` that does not vanish with ω.

- **The defining property, and the cheapest possible test of any candidate: solve one uniform line at
  two frequencies a decade apart and read `|a₂₁|`.** If it falls with ω the candidate has not solved
  anything. That test costs seconds and comes before any of the rest.
- At AC there is no ground NODE — the return is the layered medium's boundary — so the AC analogue of
  a conduction terminal is an **impressed current into the conductor's end cells with the return
  through the medium**, not a voltage across a gap. Naming the shape is as far as this brief goes;
  what it must satisfy is the test above.
- **This is the expensive route and it touches everything**: the port operator, the excitation, the
  calibration standards (which are built from the DUT's own port resolution), the reference
  impedance, and every recorded number in `HISTORY.md`. **It must be additive** — a second port kind
  alongside the delta gap, off by default until it is measured better — for the same reason
  `PlanarBoundaryCells` is a control rather than a replacement: every L8/L9 measurement in this
  repository was taken on the existing port, and a user reproducing one must be able to.

---

## What must NOT change

- **The 2–8 GHz answers.** They are right, they are the gate PCAL7 shipped, and
  `OwnNetFeedPhysicsTests.TheReportedSpiral_ReadsAsAnInductor` pins them. Whatever ships here must
  leave a well-conditioned point **bit-identical**, the same way PCAL7 left `Straight`, `ViaHop` and
  `Bend` identical to twelve significant figures.
- **The 0 Hz row.** It comes from `PlanarDcSolve` and never touches any of this.
- **The refusals.** PCAL2's clearance refusal, PCAL7's own-net refusal and PEEL's per-point drop all
  stay. A point this brief cannot answer is still a point that must be named, not published.
- **`DeembedResidual` stays what it is** — an honest measure of what was discarded, and not a quality
  measure. Do not re-point it.

## What must SHIP even if every route above fails

**`DeembedErrorFloor` must stop under-predicting.** §3 is a defect in its own right and it is
separable from the accuracy work: the published floor carries the standards' share and omits the
DUT's, and the two are the same amplifier times different errors. Publishing
`max(ConsistencyResidual, ε) × PeelAmplification` — with ε measured once under R-lfp-1 and carried as
a documented engine constant — would have made PEEL's existing per-point guard fire on all four of
the coil's bad rows, which is exactly what it was built to do. **If this brief achieves nothing else,
it must achieve that**, because the alternative is a guard that stays silent on a row that is 9×
wrong.

---

## Milestones

### M0 — stand the measurement up. No new physics.

The scratch console: solve → read `Calibrations` → print `a₁₁`, `a₂₂`, `a₂₁`, the residual, the
amplification, the implied series Z, and the perturbation slope. Every number in §1-§5 came out of
it in about 15 s apiece; PCAL7's own §13 says where to start and what to build first.

**The trap that cost a measurement: the perturbation saturates.** At η = 1e-6 the answer has already
moved by a full unit of S and both frequencies report the same amplification, which is wrong and
looks plausible. Sweep η from 1e-13 and check the slope is linear before quoting it.

### M1 — R-lfp-1. Report ε, and what sets it. **Stop and report here** if it is cheap to move.

### M2 — R-lfp-2's validity, before R-lfp-2's code. Settle the implied Z's real part (§5's own
"what could make this fail"), and measure `|a₁₁ + a₂₁ − 1|` against `ε·2/|a₂₁|²` across the band on
two cross-sections. That crossing IS the crossover; if it does not exist, the route is dead and the
measurement says so in an afternoon.

### M3 — R-lfp-2, if M2 says it lives. Gate: the coil publishes **2.5–3.5 nH and passive from
0.32 GHz up**, the 2–8 GHz rows stay bit-identical, and the uniform-line control still reads
`|S₁₁| ≈ 0` at its own Z_c.

### M4 — the floor, per "What must SHIP" above. Independent of M1-M3 and cheap.

### M5 — R-lfp-3, only if M1 and M3 have both failed, and only as an additive second port kind.

---

## Notes for whoever picks this up

- **The fixtures already exist.** `MmicCoilFixture` is the reported coil; `PlanarOwnNetFixtures` has
  the six reduced ones and the coupled-line control; `examples/PDK PCells/SpiralInductor/em/` is the
  acceptance file. Nothing needs to be drawn.
- **A baseline is a `git worktree` at HEAD, never a `git stash`.** Every before/after number in
  §PCAL7-OWNNET is one probe binary built against both trees.
- **The uniform-line control must not be degenerate and it is easy to make it so.** A 2 mm line at
  `MinCellsAcrossConductor = 2` on this stack meshes to **four unknowns**, because the transverse
  pitch binds and λ/20 does not; it then reports nonsense with great confidence. PEEL §8 recorded the
  same trap on a different fixture. Raise `MeshFrequencyHz` for the control, and check N before
  quoting anything.
- **`|S₂₁|` of the raw solve is the fastest read on how bad a point is going to be.** 1.55e-6 at
  0.32 GHz, 1.03e-5 at 2.08 GHz, and the published error tracks the square of the ratio.
- **Do not tune a threshold until the low points look better.** PCAL7 was held to that and so is this.
