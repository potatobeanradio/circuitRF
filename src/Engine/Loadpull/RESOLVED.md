# src/Engine/Loadpull — resolved briefs (detail, off the CLAUDE.md growth path)

Mirrors `src/Harmonica/RESOLVED.md`'s pattern: a completed brief's detail lands here, one `##` section
per brief, sparingly — only for findings that are still true, still surprising, and would cost someone
real time to rediscover. `CLAUDE.md` stays for durable, still-true conventions.

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
