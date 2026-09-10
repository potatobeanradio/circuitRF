# Brief — ANT-5: directivity, gain, efficiency, beamwidth — and the staged front-to-back refusal

**Series:** `brief-antenna-0-overview.md` §4. **Depends on:** ANT-4. **Blocks:** ANT-7, ANT-11.

The numbers an antenna is judged by. Most of them are short arithmetic over ANT-4's cubes. **What this
brief is really about is naming the conventions once**, because every metric here has at least two
defensible definitions and a tool that reports one without saying which produces a number nobody can
reproduce.

It also owns the **metric registry**, which is where the front-to-back refusal is staged.

---

## 1. The conventions, settled here and stated in the cube names

| quantity | definition taken | why |
|---|---|---|
| **Directivity** D | 4π·U_peak / P_rad | Standard, unambiguous. |
| **Gain** G | D · η_rad | Efficiency only. **Excludes mismatch.** |
| **Realized gain** | G · (1 − \|S₁₁\|²) | Includes mismatch. |
| **Radiation efficiency** η_rad | P_rad / P_accepted | Denominator is power *accepted at the port*, not incident. |

**Both gains ship, both are named in full, and neither is called just "gain".** On the measured board
— mismatched by 3.2 dB at resonance — the two differ by a factor of two. This is the single most
common place antenna tools quietly disagree, and the cost of getting it wrong is a user comparing a
circuitRF number against a datasheet and concluding one of them is broken.

**P_accepted is the denominator** because it is what the solve actually knows: mismatch is already in
S₁₁ and multiplying it in twice is the classic double-count. State it in the cube's own note.

## 2. The metric registry, and the staged refusal

Every metric is a `[freq, port]` real cube in ANT-4's `"farfield"` group, and every one goes through
**one registry** that knows, per metric: its name, its unit, how to compute it, and **whether it can
be computed at all in the current medium, with the sentence saying why not.**

That last field is the whole point of having a registry rather than a list of adds.

### 2a. Front-to-back is present and refused

With an analytically infinite ground plane the field at θ > 90° is identically zero, so F/B is
infinite. **Reporting ∞, or a large finite number, or omitting the metric, are all worse than
refusing it.**

- **Present, not absent.** `FrontToBackDb` exists in the registry from this phase, so the Data Display
  picker, the CLI's metric listing, the exporter and the docs are all plumbed on day one.
- **Refused, with its own sentence**, naming the reason (the ground plane is laterally infinite, so
  there is no lower hemisphere) and the phase that supplies it (ANT-11).
- **ANT-11 narrows this refusal, it does not delete it.** After ANT-11 the metric is available when a
  ground outline is present and still refused when it is not.
  `LayeredMedium.CanHost` states the rule being followed: deleting a refusal instead of narrowing it
  is how a kernel starts silently answering questions it cannot answer.

**Build the refusal so ANT-11 flips one predicate.** If activating it means touching the picker, the
exporter and the CLI, the staging was not done.

### 2b. The other metrics

| cube | kind | note |
|---|---|---|
| `DirectivityDbi` | Real | Peak. |
| `DirectivityPeakThetaDeg`, `DirectivityPeakPhiDeg` | Real | Where the peak is — a peak directivity with no direction is half an answer, and on a small ground plane the peak is not always at broadside. |
| `GainDbi` | Real | D · η_rad. |
| `RealizedGainDbi` | Real | Includes mismatch. |
| `RadiationEfficiency` | Real | Fraction, not dB. |
| `PowerRadiated`, `PowerDielectric`, `PowerSurfaceWave`, `PowerConductor` | Real | §3. |
| `BeamwidthDeg` | Real | Per named cut — §4. |
| `FrontToBackDb` | Real | **Refused** — §2a. |

## 3. The loss itemisation, which is where this can be better than typical

Radiation efficiency by power balance alone is one number and tells a designer nothing about what to
change. The four terms tell them everything, and **three of the four are already computable**:

- **P_radiated** — the hemisphere integral of ANT-4's `U`. Direct.
- **P_dielectric** — tanδ is in the solve; the loss is extractable from the solved currents against
  the lossy medium.
- **P_surface-wave** — from the residues at the TM₀/TE₁ poles. `SurfaceWavePoles.Find` already returns
  the modes with polarization, index and the machinery to evaluate the dispersion function; this is
  the term that a laterally-infinite substrate model captures uniquely well, and on a thin FR-4 patch
  it is a real fraction.
- **P_conductor** — **identically zero, and it must be reported as zero with a note, not omitted.**
  Kernel B's metal is a perfect conductor: `PlanarConductorLayer.SigmaSm` and `ThicknessM` are carried
  through the whole pipeline and never read by the fill. The design note records the magnitude on a
  comparable structure — a full-wave insertion loss reads optimistic by **6.5 % / 3.0 % / 2.1 %** of
  the total conducted loss at 2 / 10 / 20 GHz on 1.6 mm FR-4 — and that is the closest available
  yardstick for how optimistic efficiency will read. A σ field the user can edit and the solver
  ignores is exactly the shape of thing that gets trusted silently, which is why the design note says
  it out loud and why this cube must too.

**The three computed terms plus the radiated term must equal P_accepted**, up to the missing copper.
Compute the balance **and** the itemisation independently and compare — a natural self-test of the
kind this area already relies on. Report it before gating it.

**Two honest consequences to write into the note**, because a user will otherwise draw the wrong
conclusion from a low efficiency:

1. Surface-wave power is a **loss** in this model, permanently — the substrate is laterally infinite,
   so power launched into a surface wave never comes back. On a real 70 mm board it reaches the edge
   and radiates, usually badly. So the reported efficiency is a **lower bound** on what a small board
   does, and the pattern is missing the edge-diffracted contribution. ANT-11 is where that stops being
   only a caveat.
2. The missing copper term pushes the other way. Say both; do not let them be assumed to cancel.

## 4. Beamwidth needs a named cut, and must not guess one

A 3 dB beamwidth is meaningless without saying in which plane. The E- and H-planes of a patch follow
from its polarization, which follows from the current distribution — but a bent, slotted or
circularly-polarized structure has no obvious principal plane and inventing one would be the same
class of error as inventing a current direction for the mesher.

**So: `BeamwidthDeg` is reported per cut, and a cut is either named by the user (a φ value) or derived
and REPORTED (the plane containing the peak and the dominant current axis).** When neither is
available, refuse it by name like F/B. Do not default to φ = 0 silently.

Report the cut alongside the number, in the cube's own axis or note.

## 5. Gates

- **The rectangular-patch cavity model** is an independent analytic oracle for directivity and pattern
  shape on a thin substrate. It already agreed with the measured resonance to 0.3 % on the board in
  the overview, which is what makes it trustworthy here.
- **The power balance closes** to the missing-copper term, on at least two substrates of very
  different thickness (the thin FR-4 case, where surface-wave and dielectric loss dominate, and a
  thick low-loss case, where radiation does). Two regimes, because a balance that closes in one may be
  closing on a cancellation.
- **Gain vs realized gain differ by exactly (1 − |S₁₁|²)** — trivially true if implemented from the
  same S₁₁, which is the point: assert that they are not computed from two different mismatch
  estimates.
- **Efficiency ≤ 1**, always, and a violation is an error rather than a clamp. A clamped efficiency
  hides exactly the kind of balance error this itemisation exists to catch.
- **`FrontToBackDb` is present in the registry and refuses**, with a sentence naming ANT-11. Assert
  the refusal, so deleting it later is a test failure rather than a quiet change.
- **`PowerConductor` is present and zero**, with its note. Assert the note exists — this is the term
  most likely to be silently dropped as "not applicable".
- Peak direction is reported and is not assumed to be broadside.
- Beamwidth refuses rather than guessing a cut.
- `RESOLVED.md` write-up in `src/Engine/Mom`; `CLAUDE.md` gains the two gain conventions, the
  efficiency denominator, the zero-copper fact and the staged refusal.

## 6. Must NOT

- **Do not report a metric called "gain" with no qualifier.**
- **Do not omit `PowerConductor` because it is zero.** A missing term reads as "not a factor"; a zero
  term with a note reads as "this kernel does not model it".
- **Do not clamp efficiency**, or normalise the itemisation to sum to one. The residual is the
  diagnostic.
- **Do not compute a second S₁₁, or a second mismatch factor.** Both gains read the same one.
- **Do not let the F/B refusal be an absent metric.** §2a.
- **Do not use incident power as the efficiency denominator.**
- **Do not treat surface-wave power as radiation.** It is not, in this model, and the note must say
  what that means for a finite board.

## 7. Reading order

ANT-4 (the cubes this reads) · `src/Engine/Mom/SurfaceWavePoles.cs` (`Find`,
`SurfaceWaveSearchReport`, `DispersionFunction`) · `src/Engine/Mom/SpectralGreens.cs`
(`SurfaceWaveModes`, `ModeCountFromCutoffs`) · `docs/design/mom-engine.md` §10.9's perfect-conductor
note (the 6.5 / 3.0 / 2.1 % figures and why they are stated out loud) ·
`docs/user/src/reference/mom-engine.md` §"Cannot" (conductor loss) ·
`src/Engine/Mom/LayeredMedium.cs` `CanHost` (the refusal pattern being followed).
