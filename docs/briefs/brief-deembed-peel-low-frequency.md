# Brief PEEL — the de-embedding peel's 1/ω amplification, so the bottom of a band stops being published as though it were answered

**Tags:** `R-peel-n` · **Area:** `src/Engine/Mom`

**Read first**, end to end, before planning anything: `src/Engine/Mom/RESOLVED.md` §QSC in full —
this brief is its unfinished half, and §6 already names the thing measured here and declines to size
it; §LF1, §LF2 and §LF3, because the guard this brief adds sits beside LF2's and must not become a
second answer to the same question; `PlanarDeembed.cs`'s file header (D6 and D7) and `SolveErrorBox`
line by line, because the whole mechanism is visible in nine lines of it; `PlanarCalibration.cs`'s
`QuasiStaticCrossoverHz`, `QuasiStaticSeparationM` and `PlanarCalibrationSettings`' own header on
`QuasiStaticSeparationHeights` (which already half-states the mechanism, at lines 187-190); and
`PlanarSolve.cs`'s LF2 block, whose comment says *"the fit is the LAST of four walls at the bottom of
a band, not the first"* and then gates the substitution on the fit anyway.

---

## The problem

**A trace de-embedded at the bottom of its band is wrong by 59 dB and nothing in the run says so.**
The reported file (`extract1` — a 3.8 mm microstrip on the 0.6 mm laminate starter stack, 254 µm wide,
a 558.8 µm pad on P2) swept from 1 MHz to 2 GHz publishes, at 9.98 MHz, |S₁₁| = −1.37 dB where the
equivalent closed-form line gives −60.2 dB, and ∠S₂₁ = +58.9° where the line gives −0.09°. The error
decays as 1/f and only falls under 1 dB somewhere near 250 MHz.

**It is not a refusal, not a warning, and not a note.** The one sentence the run does emit down there
— *"Below 7.952 MHz the full-wave fit has no valid range, so 3 points carry the 0 Hz conduction
solve"* — invites exactly the wrong reading, that 7.952 MHz is where trouble ends.

### It is the peel, and every other candidate is measurably innocent

Measured on the reported file, 21 log points from 5 to 500 MHz, `Cli em` on the `.cem` (N = 574, four
standards at 467 / 552 / 841 / 1534, 2 min 16 s, Debug):

- **The calibration is excellent at every frequency, including the worst ones.** ε_eff reads 2.7905
  at 10 MHz falling to 2.7868 at 500 MHz, against a closed-form 2.777; Z_c reads 105.69-105.76 Ω
  against 106 Ω. **0.4 % and 0.3 %, at the point that is 59 dB wrong.** §QSC §6 measured the same
  thing at 100 MHz and said the same about it.
- **α is right too, and this rules out the one candidate that would have cost a day.** The run's
  `AttenDbPerM` reads 0.1622 dB/m at 10 MHz = 0.01867 Np/m, against a skin-effect conductor loss of
  R/(2Z_c) = 0.0189 Np/m computed from δ = 20.9 µm on 35 µm of metal. A supplied γ whose real part
  were wrong would produce an error in `x = e^{−γℓ}` that does not shrink with ω, which is the shape
  wanted — but α is right to ~1 %, so it is not the shape present. **Do not re-measure this.**
- **It does not converge under mesh refinement — it gets WORSE.** The same file at 6 cells across
  instead of 3, 20 cells/λ instead of 5, Staircase instead of Conformal (N = 879, standards to 2,607)
  gives max|ΔS| = 0.915 at 10 MHz against 0.863 on the coarse mesh. A discretisation error does not
  behave like that, which is what makes the rest of this brief necessary.
- **The DUT itself is fine.** At 2 GHz the same run agrees with the closed-form line to 0.5 dB in
  |S₁₁|, and §QSC §6 already attributes the residual top-of-band growth to the artwork's 40 corners
  and its P2 pad rather than to the instrument.

### The mechanism, in nine lines of `SolveErrorBox`

With `x_i = e^{−γℓ_i}` and `m_i` the symmetrised `M₂₁` of standard *i*, the code computes

```
a₂₂² = (m₂/x₂ − m₁/x₁) / (m₂x₂ − m₁x₁)
```

Substituting D6's own `M₂₁ = a₂₁²x/(1 − a₂₂²x²)` into both halves gives

```
numerator   = a₂₁²·a₂₂²·(x₂² − x₁²) / [(1 − a₂₂²x₂²)(1 − a₂₂²x₁²)]
denominator = a₂₁²·      (x₂² − x₁²) / [(1 − a₂₂²x₂²)(1 − a₂₂²x₁²)]
```

so the quotient is `a₂₂²` **identically** — and **both halves carry the factor (x₂² − x₁²) ≈ 2γΔℓ,
which QSC froze Δℓ and therefore made proportional to ω.** The ratio is exact in algebra and is a
0/0 in floating point: each half is an O(ω) quantity formed as the difference of two O(1) numbers, so
an absolute error ε in `m` becomes a relative error ε/(2βΔℓ) in `a₂₂²`. It reaches `a₁₁` through
D6's own correction term and then the peel divides by `a₂₁²`, which is where §QSC's shorthand
"the `a₂₁ ∝ ω` floor" comes from — but **the ω is not in `a₂₁`, it is in `x₂² − x₁²`, and that
matters because it is the half with a design decision behind it.**

**The law this predicts is the law measured.** `CalElectricalDeg` is already published per (frequency,
port); the cancellation depth is `2·βΔℓ` in radians:

| f (MHz) | max\|ΔS\| vs ideal line | βΔℓ (deg) | depth 2βΔℓ | product |
|---|---|---|---|---|
| 9.976 | 0.86267 | 0.0863 | 3.01e-03 | 0.0026 |
| 25.06 | 0.57856 | 0.2167 | 7.56e-03 | 0.0044 |
| 99.76 | 0.16032 | 0.8625 | 3.01e-02 | 0.0048 |
| 250.6 | 0.05267 | 2.1666 | 7.56e-02 | 0.0040 |
| 500.0 | 0.02033 | 4.3229 | 1.51e-01 | 0.0031 |

**The product is flat to within a factor of two while the error itself moves 42× and the frequency
50×.** The log-log slope of max|ΔS| against f is **−0.984** over the band. On the refined mesh the
same product reads 0.0024 / 0.0058 / 0.0038 — **the same constant**, which is why refining does not
help: a finer mesh shortens the bulk cell that floors Δℓ (βΔℓ falls 0.0863° → 0.0741° at 10 MHz)
by about as much as it improves whatever the constant is.

So the error is **`floor / (2βΔℓ)`**, the amplifier is understood and computable for free, and
**what the floor of ≈ 0.004 actually IS remains open.** That is M1, and it is the whole question:
0.004 absolute in a differenced `M₂₁` is far above roundoff and far above the run's own
`DeembedResidual`.

### The diagnostic that hid this is anti-correlated with the truth

`DeembedResidual` on port 1 of that sweep reads **1.95e-10 at 10 MHz and rises to 9.38e-9 at
500 MHz** — smallest exactly where the answer is worst, by a factor of 48, in the same direction over
the same band. `DeembedRejected` (the a₂₂ sign margin) is a healthy 913× at 10 MHz. Both are honest
about what they measure and both are useless here, for a reason worth stating once in the code:
**the residual is the two standards' agreement with each other, and two standards that are becoming
identical agree better and better while determining the error box less and less.** A reader — or a
brief — checking de-embedding quality by the number named `DeembedResidual` is told the bottom of the
band is the best-calibrated part of the sweep. That is why §QSC could name this floor and still ship.

**Nothing published from a run distinguishes a well-conditioned peel from one amplifying by 332×.**
Fixing that is half of this brief and must ship even if the other half fails.

---

## Milestones

### M1 — what the floor is. MEASUREMENT ONLY, and it decides everything after it.

`floor ≈ 0.004`, mesh-invariant across a 1.5× refinement. Find it. The instrument is the same one
§QSC §5 already built: **a uniform line, where the de-embedded S₁₁ must be exactly 0**, so the error
is the answer and no ideal-MLIN reference is in the path at all.

Dump, per frequency, on a uniform line at 10 / 30 / 100 / 300 MHz: `m₁`, `m₂`, `x₁`, `x₂`, the two
differenced halves, `a₁₁`, `a₂₁`, `a₂₂`, and the de-embedded S₁₁. Then answer, in this order:

1. **Does the floor live in `m` or in `x`?** Perturb γ by a known relative amount and see whether the
   de-embedded error moves proportionally. If it does, the floor is a γ error after all and α's
   innocence above only clears the real part — β would then be the suspect, and ε_eff's 0.4 %
   disagreement with closed form is the right size to check against `2βΔℓ`'s own budget.
2. **Do the two standards share their mesh over the length they have in common?** D7 already argues
   that differencing two standards makes the end effects cancel *exactly*; that argument holds only
   if the cancelled thing is bit-identical on both sides. `BuildLine` realises a separation as a whole
   number of bulk cells — establish whether the long standard is the short one plus Δℓ of cells with
   every other row identical, or two independent meshes that merely agree in construction. **If it is
   the second, M2(a) is the whole fix and the rest of this brief is a guard.**
3. **Is the floor the same on a second stack?** One stack cannot tell a constant from a coincidence.
   Use the two other starter stacks §QSC M1 already measured on.

**Stop and report if M1 shows the floor falling with mesh on some other geometry** — that would make
this a convergence problem after all and every milestone below is the wrong shape.

### M2 — attack the floor, then the amplifier. Three routes, in cost order, and each is testable alone.

**(a) Make the difference cancel what it is supposed to cancel.** If M1.2 found two independent
meshes, build the long standard as the short standard plus inserted cells, so the feed region's rows
are bit-identical and its discretisation error subtracts out of `m₂ − m₁` rather than adding in
quadrature. This is D7's own argument applied to `M₂₁` instead of to `C`, it needs no new standard and
no new physics, and it attacks the floor rather than the amplifier — which is the only route that
helps at *every* frequency rather than buying back a decade.

**(b) Continue the error box in frequency instead of re-solving it where it is ill-conditioned.**
The evidence that this is legitimate is already in the run: on the quasi-static path ε_eff and Z_c are
**constant to five significant figures** from 10 MHz to 500 MHz, because they are frequency-independent
by construction down there. The feed's own `a₁₁`/`a₂₂` are a physical structure's reflections and are
equally smooth. So solve the box where `2βΔℓ` is healthy and carry it downward, the way `previousA21`
already carries a branch across a sweep. Gate it on the uniform-line control, which gives a right
answer to compare against at every frequency without a reference file.

**(c) Add a REFLECT standard below the crossover.** This is what TRL has a reflect standard *for*:
`a₁₁` and `a₂₂` from an O(1) measurement, with no differencing of two nearly-identical lines. γ is
already supplied analytically down there, so the reflect adds one unknown and the usual argument
removes it again (it cancels between two ports that share it; it is known outright for a shorted via).
Cost is one more small mesh whose size does not grow as the band edge falls — the same property that
made QSC affordable. **Most expensive and most likely to actually close the band**, so it is third
only because (a) and (b) must be tried first and might make it unnecessary.

**Report which of the three moved the number and by how much, including the ones that did not.**
A route that fails is worth as much here as one that works, because the next person will otherwise
try it.

### M3 — the guard, which ships whatever M2 achieves

Even a fixed peel has a floor somewhere, and nothing today can see it.

- **Publish the conditioning.** `|m₂x₂ − m₁x₁| / max(|m₂x₂|, |m₁x₁|)` is already computed inside
  `SolveErrorBox` and costs nothing to keep. It goes in `PlanarErrorBox` beside the two residuals and
  into the `.npy` diagnostics group beside `DeembedResidual`, per (frequency, port).
- **Say in the code, once, why `DeembedResidual` is not a quality measure here**, at its own XML doc —
  `PlanarErrorBox`'s header already carries the *"honest measure of what was discarded, not a proven
  predictor of accuracy"* caveat and this is the concrete case it was hedging about.
- **Gate on it.** Below a threshold the run either substitutes (LF2's conduction solve, where the
  points are low enough for that to be an answer) or refuses with a sentence naming the real remedy —
  the lowest frequency at which this stack's peel is conditioned, which is computable from Δℓ and γ
  before any solve, exactly as `Dcim.LowestFittableFrequency` is. **Warnings are reported and still
  exit 0; a refusal is a refusal.**
- **The threshold is MEASURED against the uniform-line control, not chosen.** Pick the budget in
  |ΔS| first, then read off the depth that delivers it, then report the frequency that implies on each
  starter stack the way §QSC reported its crossover.
- **Re-point LF2's note.** Its current sentence is true and reads as a floor when it is a ceiling on
  one of four walls. It must say which wall each substituted point is below, and a point above the fit
  floor but below the peel's must not be silent.

### M4 — end to end on the reported file, at the band that provoked this

`extract1`, 1 MHz – 2 GHz, 11 log points — **the sweep as the user actually wrote it**, not §QSC's
100 MHz – 6 GHz. Every point must be either right to a stated tolerance, substituted with a note
saying so, or refused with a sentence. **"Published silently and 59 dB wrong" must not be reachable
from that file.**

Report the ideal-line comparison over the whole sweep as a table, and state the frequency below which
this file is no longer answered — whatever M2 achieved. If that frequency is still 250 MHz, say so
plainly; a guard that tells the truth is the deliverable and the accuracy is a bonus.

---

## Gates

- **The uniform-line control is the primary gate**, at every frequency and not just its ends: a
  de-embedded S₁₁ that must be exactly 0 is the only oracle here that does not smuggle in a second
  model. §QSC §5 already reads 0.84 / 0.39 / 0.19 / 0.11 at 100 / 300 / 690 MHz / 1.28 GHz on a
  deliberately coarse 24-unknown mesh — **that is the curve this brief has to bend**, and it is
  already 1/f.
- **Above the quasi-static crossover, every published s-parameter must be BIT-IDENTICAL to today.**
  This brief touches the bottom of a band. The whole L8/L9 acceptance set rests on the measured path
  and none of it may move.
- **`RawSolveAndCalibrationRemedyTests` and the QSC tests pass unchanged**, except where M3 deliberately
  re-points a message, which is a changed assertion with a recorded reason and not a deleted one.
- The new conditioning diagnostic asserted with its own recorded number on the reported file, the way
  §QSC recorded the a₂₂ margin.
- No new `Category=Benchmark` test unless a measured one crosses ~5 s. The reported file is 2 min 16 s
  in Debug at N = 574 over 21 points; a 5-point version of it is well under the routine tier.

## Must NOT

- **Do not re-lengthen Δℓ below the crossover.** It is the obvious move, it works, and it is exactly
  the 1/f_lo standard QSC existed to remove — a 161.5 mm standard at 79,055 unknowns. Anyone reaching
  for it has rediscovered the problem, not the solution. Say so in `RESOLVED.md` so the next person
  does not spend the day.
- **Do not present a substituted or extrapolated point as a solved one.** RAW1 §5 and §QSC's own
  "Must NOT" both say this about a different flag; it applies to every route in M2 and to M3's guard.
  A run that crosses a threshold must be able to say which side each point came from.
- **Do not make the conditioning threshold a `.cem` field or a panel control in this brief.** Measure
  it, fix it per stack, report it in the notes — QSC's own reasoning about its crossover, unchanged.
- **Do not take the quasi-static γ from `RlgcExtractor`** to "improve" the floor. D7's rule stands and
  §QSC §2 settled it on four structural grounds; kernel A is the oracle, never an input.
- Do not touch `src/Core`, `src/Engine`'s non-Mom code, `src/Ui`, `src/Design` or `RfCore`.
- Anonymise the fixture before committing anything derived from it — no workspace names and no paths.

## Cost

M1 is measurement only and is most of the value; if it identifies the floor as a shared-mesh failure,
M2(a) is small and the rest is a guard. M3 is contained — one field on a record, one diagnostic cube,
one threshold and two messages — and **must ship even if M1 and M2 close nothing**, because the
defect that reached a user is not the 59 dB, it is that the 59 dB arrived with no sentence attached.
M2(c) is the largest item and is the only one likely to buy back the whole decade; do not start it
before M1 has said what the floor is.

## On completion

- `src/Engine/Mom/RESOLVED.md` gains a `§PEEL` section: M1's identification of the floor in full, the
  measured `floor / 2βΔℓ` law with both meshes' tables, which of M2's three routes moved the number and
  which did not, M3's measured threshold per starter stack, and every trap found.
- `src/Engine/Mom/RESOLVED.md` §QSC §6 is updated — it names this floor and declines to size it, and
  must not be left standing as though the sizing were unknown.
- `docs/design/mom-engine.md`'s de-embedding section gains the conditioning discussion and the
  "`DeembedResidual` is not a quality measure below the crossover" sentence. It is a design note and
  is edited normally.
- **Do not write to any `CLAUDE.md`.** Report stale lines to the owner by file and line instead — in
  particular the `Cli em` paragraph, which says nothing today about a band having a usable bottom.
