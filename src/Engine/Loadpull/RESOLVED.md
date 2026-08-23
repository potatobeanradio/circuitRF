# src/Engine/Loadpull — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Harmonica/RESOLVED.md`'s pattern: a completed brief's detail lands here, one `##` section
per brief, sparingly — only for findings that are still true, still surprising, and would cost someone
real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

## The recommended grid sampled a BOX where the setting names a circle (2026-08-23)

**Reported:** with `VSWR2 = 20` and a follow-on loadpull enabled, the recommended grid is missing the
lower-impedance terminations — the low-Z side only reaches about a VSWR of 3 from MXE — while
carrying a lot of very high impedances that are *outside* VSWR2 altogether. Both halves are real,
and they are one defect.

**The builder was doing exactly what it was specified to do**, and the spec's circle math is right —
the defect was in the SAMPLING.
Checked by re-deriving the whole grid from the run's own recorded optima and comparing against the
134 terminations the follow-on actually simulated: **identical, point for point.** The constant-VSWR
circle formula was also re-derived from `VswrFromZ`'s own definition of Γ and comes out as written —
centre `(Re(z)·(1+g²)/(1−g²), Im(z))`, radius `Re(z)·2g/(1−g²)`. The imaginary centre really does
stay at `Im(z)` (it is `Im(Z) − Im(z)` that appears in the algebra, not `Im(Z) + Im(z)`), so the
apparent asymmetry there is not a bug.

`VSWR2` is centred on **MXE**, and box4 was the union of the three boxes' extents.

### What that costs, in a real run's own numbers

MXP = 80.48 + j0.00 Ω, MXE = 124.77 − j6.17 Ω; `VSWR1 = 2` at 4×4, `VSWR2 = 20` at 10×10.

| box | Re extent (Ω) | Im extent (Ω) |
|---|---|---|
| box1 (VSWR1 about MXP) | 40.2 … 161.0 | −60.4 … 60.4 |
| box2 (VSWR1 about MXE) | 62.4 … 249.5 | −99.7 … 87.4 |
| box3 = box4 (VSWR2 about MXE) | **6.2 … 2495.3** | −1250.7 … 1238.4 |

A constant-VSWR circle about `R` spans `R/v … R·v`: the upper half is ×20 and the lower half is
÷20, so **95 % of the box's width is above MXE and 5 % below it.** The broad grid then samples that
box *linearly*, so its 10 columns are 6.2, 282.8, 559.4, … 2495.3 Ω — **one single column below
280 Ω**, and the step (276.6 Ω) is wider than the entire region a loadpull is about. Everything
between about 7 Ω and 280 Ω is covered only by the two small VSWR1 boxes around the optima. So the
grid did get bigger; it grew almost entirely into the high-impedance corner.

Three more consequences of the same geometry, all measured on that run:

- **40 of the 134 points sit BEYOND VSWR2 from MXE** — one at a VSWR of **2010** (6.2 + j1238 Ω).
  A box's corners reach far past the circle they bound, so the same sampling that under-reached on
  the low-resistance side over-reached on the diagonals. That is where the very high impedances come
  from, and they are not a separate problem.
- **65 of the 134 points sit at |Γ| > 0.95, and 90 at |Γ| > 0.9** — bunched invisibly against the
  Smith chart rim, where a 2-D interpolant gets nothing usable from them. Only 14 are inside
  |Γ| < 0.5.
- **The "discard any broad point inside box1 or box2" step did nothing**: at a 276.6 Ω spacing no
  broad point can land in a 120 Ω-wide box. Dedup removed nothing either. 16 + 16 + 100 + 2 = 134.

Raising `VSWR2` therefore trades resolution for reach at a very poor rate: at `VSWR2 = 3` the same
10×10 covers 41.6 … 374.3 Ω with a 37 Ω step — a worse low-impedance floor but a usable grid; at 20
the floor drops to 6.2 Ω and the mid-range resolution is gone.

### The fix: sample the disc, not its bounding box

`SampleVswrDisc` replaces the broad box sample. In the reflection plane referenced to MXE, `n` rings
at `|Γ| = g·k/n` × `n` angles, alternate rings offset half a step, mapped back with
`Z = (z + Γ·conj(z))/(1 − Γ)`. Equal |Γ| spacing is a gentle geometric progression in VSWR (1.2,
1.44, 1.77, 2.2, 2.75, 3.55, 4.79, 6.99, 11.9, 20 at `VSWR2 = 20`, `n = 10`), and the outermost ring
keeps its cardinal points so `Re(MXE)/VSWR2 + j·Im(MXE)` is always a grid point.

Same run rebuilt: **93 points instead of 134**, lowest resistance **6.24 Ω at exactly VSWR 20.00**
from MXE, **nothing beyond VSWR2**, 3 points at |Γ| > 0.95 instead of 65. The reach is monotone in
the setting — 37 Ω within VSWR 4, 20 Ω within 8, 13 Ω within 12. Fewer points AND better coverage,
because the discarded ones were the rim.

### The focused VSWR1 regions had it too — milder in magnitude, worse in proportion

Asked and measured rather than assumed. Of a `VSWR1 = 2` box's 16 points, **12 (75 %) lie outside the
requested circle** — against 40 % for the VSWR2 box — but the worst corner reaches only **VSWR 3.32**,
a 1.66× overshoot rather than 100×. The difference is the nonlinearity, not the geometry: a box corner
sits at √2 × the radius in Z either way, and that maps to a small VSWR excess down at VSWR 2 and to an
enormous one up near |Γ| = 1. Separately, at an even resolution no row lands on the optimum's own
reactance, so the circle's low-impedance extreme is missed by 60 Ω on an 80 Ω optimum.

Both focused regions now use the same ring sampler. On the run above, terminations **within VSWR1 of
MXP go from 10 to 28 and of MXE from 17 to 28** (within VSWR 1.5 of either: 10 → 22) — the whole
focused budget lands where it was asked to, where before most of it had spilled into the corners.
Final grid: **99 points** vs the original 134.

With both focused regions circular, step 7's exclusion tests the **disc** rather than a bounding box.
That is a fix in its own right: a broad point in what used to be a box corner is outside the VSWR1
circle, so the focused sampling never covered it and discarding it left a hole. At a small `VSWR2` the
exclusion can still discard the broad disc's own low extreme, correctly — the whole broad disc then
lies inside the focused ones, which sample that region more finely.

`VswrCircleBox`, `SampleBox` and `InsideBox` are gone with the last caller; `git show f958cdf` has them
if the box sampling is ever wanted back.

### One trap in gating it

The obvious assertion — "min Re equals `Re(MXE)/VSWR2`" — is false at small `VSWR2` and it is not the
code's fault. At `VSWR2 = 3` the grid's lowest resistance is 40.24 Ω, which is the VSWR1 box around
**MXP**, and the disc's own 41.59 Ω extreme is discarded for being inside that box. The claim that
holds at every setting is *reaches at least as low as the circle does*, plus the exact extreme at a
`VSWR2` large enough to escape the focused boxes.

## Round 11 — the drive-up continuity guard, lifted here from harmonicaRF (2026-08-15)

harmonicaRF's own Pin ladder was found converging, at ‖F‖ ≈ 2e-9, onto roots of the harmonic-balance
residual that draw kilowatts from a 48 V supply (`src/Harmonica/RESOLVED.md` §Round 11). This engine's
inner drive-up has the same shape, so the question was whether it has the same defect. It does.

### The shared primitive, and which way the dependency runs

`DriveLadder` (new, this directory) owns the continuity criterion and the bisection walk. It is HERE
rather than in `src/Harmonica` because **`src/Harmonica` references `src/Engine` and not the other way
round** — `PinSearch.IsDiscontinuous`/`ContinueThroughJump` are now thin adapters over it. Two drive-ups
that disagreed about what "the same solution branch" means would be worse than either rule alone.

### The measurement that had to come first: the frozen heroes do not move

Hero 3 and Hero 3B are self-generated goldens, so the guard firing anywhere in them would have meant
re-verifying them. Measured before shipping the change, and now gated
(`LoadpullLadderContinuityTests.TheFrozenHeroes_NeverFireTheGuard`):

| run | terminations | PinStep | continuations | retries | non-convergent | PAE > 100% |
|---|---|---|---|---|---|---|
| `hero3.cnl` | 20 | 1 dB | **0** | 0 | 0 | 0 |
| `hero3_at_compression.cnl` | 20 | 1 dB | **0** | 0 | 0 | 0 |
| Hero 3B pursuit | 21 cached | 1 dB | **0** | 0 | 0 | 0 |

Hero 3B's pursuit still reports MXP 80.476 Ω / 40.625 dBm and MXE 140.31 Ω / 69.62%, matching what
`CLAUDE.md` already records. **Nothing moved, and the guard is free at a 1 dB step.**

### …and it is emphatically not inert. Class F is what exposes it

**The owner's own hint, and it was decisive.** An earlier probe over the Hero 3 grid at 2–6 dB steps
found nothing, and that was a false negative for a reason worth keeping (below). With genuine Class F
load terminations — near-short at 2f₀, near-open at 3f₀, referenced to R_opt = 80 Ω — the unguarded
ladder produces roots like these, all reported `Converged`:

| Γ | rung | Pout | Pdc | DE (PAE is comparable) |
|---|---|---|---|---|
| 0.442 + j0.114 | 20 → 23 dBm | 38.5 → **84.9 dBm** | 2.04 kW | **15,035%** |
| 0.507 − j0.384 | 20 → 23 dBm | 36.5 → **108.0 dBm** | **66.7 kW** | **94,087%** |

`testdata/Hero3/hero3_classF.cnl` is that fixture. It is **not a golden and not owner-verified
physics** — it exists to be wrong without the guard. On its own 20-point grid at its 2 dB step:

| | guard OFF | guard ON |
|---|---|---|
| points with PAE > 100% | **11** | **1** |
| of those, PAE > 150% ("gross") | **8** | **0** |
| non-convergent | 0 | 0 |
| continuations | — | 24 |
| solves | 353 | 399 (**+13%**) |

**The oracle is energy conservation, not agreement with a finer ladder.** A guarded 0.5 dB walk and a
guarded 0.25 dB walk of this fixture agree to **0.05 dB** across all points — that step-independence is
what shows the guarded answer is the physical branch — but a coarse ladder's last rung legitimately
differs from a fine one's, so "close to the reference" would be a tolerance to tune rather than a fact
to assert.

### THE SURVIVING POINT IS A DIFFERENT DEFECT, and it is recorded rather than tuned away

At Γ = 0.6 (Z = 200 Ω) the 2 dB ladder is **perfectly continuous** — Pout tracks Pin at 1:1 or less on
every rung, so the guard correctly never fires — and it simply arrives just past 100% by its
last rung — PAE 101.6% (Pout 9.79 W against Pdc 9.38 W). The same fixture at a guarded 0.5 dB or
0.25 dB has no such point.
**That is a coarse ladder DRIFTING off the physical branch, not JUMPING to another one.** The
continuity guard catches jumps and does not claim to catch drift; the remedy there is a finer
`PinStep`, which is what the emitted warning says.

### THE ENERGY SCREEN IS PAE, NOT DE — and it must go SILENT against an ACTIVE termination

Raised by the owner, and it corrected the screen twice over.

**1. `DE ≤ 100%` is not a physical law.** The steady-state balance is
`Pout ≤ Pdc + Pin_delivered + P_active`. With every termination passive, `P_active = 0` and that
rearranges to exactly **`PAE = (Pout − Pin_delivered)/Pdc ≤ 1`**. `DE = Pout/Pdc ≤ 1` does **not**
follow: a low-gain stage driven hard can legitimately put out more than its DC input, with the
difference supplied by the RF drive. The screen as first written tested DE and would have accused such
a stage of impossible physics. It now tests PAE.

**2. Against an active termination there is no bound left to test at all.** A negative-real
termination is a power SOURCE, so `P_active > 0` and PAE above 100% is then perfectly physical —
which is much of the point of setting one. The engine does not compute `P_active`, so
`GridPointResult.HasActiveTermination` is carried on every result and **both** consumers (the engine's
own warning and the pursuit's unscorable rule) skip entirely when it is set. Silence there is a refusal
to guess, not an oversight. Negative-real terminations are a deliberate research capability — the
engine stamps one as a negative conductance and a `.gam` may carry `|Γ| > 1`; nothing refuses one.

**The continuity guard is a SEPARATE question and still applies**, because it tests smoothness along a
branch rather than an energy budget — nothing in `|ΔPout| > ΔPin + margin` assumes passivity. Measured
on `hero3_classF_active.cnl` (the Class F fixture with `Z[2] = −20 Ω`), 20 points, 2 dB step:

| | guard OFF | guard ON | guarded 0.5 dB reference |
|---|---|---|---|
| points flagged active | 20/20 | 20/20 | 20/20 |
| **warnings emitted** | **0** | **0** | **0** |
| points with PAE > 100% | 1 | **0** | 0 |
| worst PAE seen | **33,729%** | **82.1%** | **82.2%** |
| continuations | — | 20 | 15 |
| non-convergent | 0 | **1** | 0 |

The guarded coarse walk lands within 0.1 percentage point of the guarded fine walk, which is what says
it found the physical branch. **The one non-convergent point is an honest trade, not a regression:**
guard-off "completed" that sweep by converging onto the PAE-33,729% root; guard-on refuses the leap,
cannot reach the rung by continuation or by a cold seed either, and truncates saying so.

**A spurious fire cannot corrupt a correct answer, by construction rather than by measurement.**
`ContinueThroughJump` returns null unless it finds a chain that is continuous at EVERY sub-step, and
the caller then keeps what it had. So firing can only replace a discontinuous answer with a continuous
one, or cost solves and change nothing. That is why the guard being over-eager on an active termination
(20 fires out of 20 points) is a cost question and never a correctness one.

### Two traps found on the way

- **`TunerModel` holds exactly ONE harmonic override at a time** (`_overrideHarmonic` is a single
  `int?`), and `RunOneTermination` spends it on the swept harmonic. So a caller cannot stack
  `SetHarmonicOverride(2, …)` and `SetHarmonicOverride(3, …)`: the second replaces the first, and the
  sweep then replaces that. **Both calls fail silently.** Harmonic terminations other than the swept one
  must come from the netlist's own `Z[k]=`. This is exactly what made the first probe measure a circuit
  that was not Class F at all and report a clean bill of health.
- **`AnalysisSettings.DriveStepping` is documented but not implemented.** It is resolved from the
  directive, copied between settings objects, and never read by anything that ramps drive — `grep` finds
  no consumer. It was a candidate explanation for why the batch engine looked immune; it is not one, and
  nobody should reach for it as an existing continuation mechanism.

### What changed

- `DriveLadder.cs` (new) — `IsDiscontinuous`, `PoutDbm`, `ContinueThroughJump<T>`.
- `LoadpullEngine.RunOneTermination` — one guard for both failure modes (the Newton fails, or lands on
  another root), then a cold-seeded retry as the last resort, replacing the old break-on-first-failure.
  `GridPointResult.Continuations`/`.Retries` count it.
- `LoadpullAnalysisParams.ContinuityMarginDb`, from the new `ContinuityMargin=` directive key on both
  `type=loadpull` and `type=loadpull_pursuit`. Default 3 dB; **0 disables the guard entirely**.
- `LoadpullPursuitEngine.Query` — a termination whose drive-up violates PAE > 100% is marked
  **unscorable**. This belongs in the pursuit rather than in the ladder because a search *ascends* its
  criterion: an 89 dBm Pout against a real 36 is not one bad sample among many, it is a global attractor
  that steepest-ascent walks straight to and reports as MXP, converged.
- `LoadpullEngine` emits one `AddWarningOnce("loadpull-energy-violation", …)` per run naming the first
  offending Γ and Pin. **Necessary, not sufficient** — of the four nonphysical points that started this
  work in harmonicaRF, this caught two; the others reported 82.5 dBm at DE 51%.
- `GridPointResult.HasActiveTermination` (new), computed once per grid point from every declared
  `Z[k]` on both tuners plus the swept `Z` — the flag both energy screens read.
- `testdata/Hero3/hero3_classF.cnl` and `hero3_classF_active.cnl` (new). Neither is a golden; they
  exist to be wrong without the guard, and the second to be right *with* PAE > 100%.
