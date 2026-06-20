# Phase 4b — Code Review & Correction: Loadpull / Pursuit Engines (Claude Code / Sonnet)

**This is a REVIEW-AND-REPORT-FIRST task, not a feature task.** The Phase 4b-1/4b-2 loadpull and pursuit
engines "passed" only against **self-generated golden data**, so bugs baked in at creation passed too.
An owner + design review found multiple confirmed defects, most stemming from **one missing foundation:
there is no stated current-direction (sign) convention, so signs were patched ad-hoc per call site until
results matched self-generated data.** Fix the foundation first, then the consumers, then **regenerate the
golden data** (it is currently wrong) for owner verification.

**Work in two passes: (A) document the convention + write diagnostics + REPORT back BEFORE changing
calculations; (B) after the report, apply the fixes.** Do not jump straight to editing — the report is how
we confirm the convention is right before signs propagate.

> Files in scope: `src/Engine/Loadpull/LoadpullEngine.cs`, `LoadpullPursuitEngine.cs`, `PursuitEngine.cs`,
> and `src/Engine/HarmonicBalance/HbEngine.cs` (the `INl` writeback — the sign origin) + its CLAUDE.md.
> Read: `docs/design/loadpull.md`, `docs/design/loadpull_pursuit.md`, `src/Engine/HarmonicBalance/CLAUDE.md`.

## PASS A — Document the convention, write diagnostics, REPORT (no calc changes yet)

### A1 — State the current-direction convention at its source (HbEngine `INl`)
The HB engine produces `INl[node, k]` but **never documents its sign**. Determine empirically and then
**document in `HbEngine.cs` and `HarmonicBalance/CLAUDE.md`** the exact meaning: is `INl[n,k]` the current
the nonlinear device **injects into** interface node n, or the current flowing **into the device** from n?
State it as one sentence, unambiguously, e.g.: *"INl[n,k] is the current the nonlinear device injects into
node n (positive = out of the device into the external network)."* Everything downstream derives from this.

The engineering rules the consumers must then follow (common knowledge, state them in the doc):
- **Power delivered to a load** = ½·Re(V · conj(I_into_load)). The current *into the load* is the current
  *out of the DUT* at that port.
- **Zin of the DUT** = V / I_into_DUT. The current *into the DUT* is the **negative** of the current *out of
  the DUT*. (So if the sweep data gives current out of the DUT, negate it for Zin.)
- A single convention, applied once — **no per-call-site sign flips tuned to make a number look positive.**

### A2 — Write a sign/unit diagnostic at a KNOWN operating point
Add a diagnostic (a test or a `--diagnose` path) that runs **Hero 2's verified bias** (the owner
hand-checked Pout/efficiency/DC current there) and **prints, with units and signs**, at the fundamental:
`V[load]`, `INl[load]`, `V[src]`, `INl[src]`, the computed Pout (W and dBm), Pin_delivered, Gt, Gp, Pdc, DE,
Zin, Zsource. **Report these numbers back to the owner** alongside what the convention (A1) predicts they
*should* be. This converts "signs tuned to pass" into "signs derived and verified against known physics."

### A3 — Report the confirmed-bug findings (below) with the diagnostic evidence
Confirm each item in PASS B against the diagnostic output and report before fixing. **Stop here and report.**

## PASS B — The fixes (after the report is reviewed)

### B1 — Apply the current-direction convention uniformly
Replace all ad-hoc sign flips with the single A1 convention:
- `ComputeFoms` currently does `pout = −0.5·Re(...)` but `pinDelivered = +0.5·Re(...)` — reconcile both to
  the stated convention (power delivered = ½·Re(V·conj(I_into_that_port))), not opposite hand-patches.
- **Zin (auto-Zsource): Zin = V / I_into_DUT.** If `INl` is current out of the DUT, negate it. Report the
  resulting Zin is **positive-real** at the bias (a passive-input DUT must have Re(Zin) > 0) — that is the
  correctness check.

### B2 — Compression overshoot: exact +0.1 dB input step (not +1 full PinStep)
`RunOneTermination` currently overshoots by a whole `PinStep` (sets `overshot`, breaks on the next full
step). Change to: when compression ≥ target is first reached, take **one final input-power step of exactly
+0.1 dB** (Pavl + 0.1 dB), solve, record, then stop. This tightly brackets P-xdB for the interpolator
(loadpull.md §3.1).

### B3 — MXP criterion and interpolation in dBm, not Watts
- `ExtractCriterion` returns `PoutW` (Watts) as the MXP criterion → return **Pout in dBm** (steepest-ascent
  must operate on dBm; the gradient surface differs).
- The compression-point **linear interpolation** (the `below`/`above` bracket logic, ~lines 430-470) is
  convoluted and operates on Watts. Rewrite it cleanly: find the two converged steps that bracket the
  P-xdB gain drop, linearly interpolate **Pout (dBm)** and **efficiency** to the exact P-xdB point. With the
  B2 +0.1 dB step, the bracket is tight. Verify the interpolation against a hand calc.

### B4 — Remove the misleading `WToDb`; gain uses an explicitly-named ratio-to-dB
`WToDb(ratio)` is used for **gain** (Pout/Pavl, Pout/Pin) — a dimensionless ratio, where 10·log10(ratio) is
correct. But the **name** implies converting a power to "dB," which is meaningless. **Rename to
`RatioToDb` (or `GainToDb`)** and **audit every call site to confirm it is only ever applied to a ratio,
never to an absolute power.** Absolute powers always go through `WattsToDbm`. (Delete any `DbToW` if present.)

### B5 — Fix the steepest-ascent metric mismatch (PursuitEngine — likely the real cause of the bad
MXP↔MXE ~1.05 result)
`FitLinearPlane` computes the gradient `(m1,m2)` in **Euclidean (Re,Im) Z-space**, but `StepAlongDirection`
scales the step by **VSWR**. Direction and distance are in *different metrics* — so the ascent direction is
wrong (Euclidean-Z gradient ≠ VSWR-metric gradient), and the search stalls/wanders, collapsing MXP and MXE
together. **Fix:** make the search self-consistent — the gradient and the step must use the **same metric**.
Either (a) do the whole search in the Γ-plane where VSWR distance is natural (compute the gradient in Γ,
step in Γ), or (b) keep Z but measure the tangent-plane neighbour offsets in the *same* VSWR-consistent way
the step uses. Recommend (a): Γ-plane is the natural Smith-chart space for a VSWR-metric search; convert
Z↔Γ at the boundaries via RfCore. Report the corrected MXP↔MXE separation — it should be **~2–2.5 VSWR**
(Pedro) for this stable FET, NOT ~1.05.

### B6 — Fix the broken mirror-neighbour logic (PursuitEngine)
In the tangent stage, the unscorable-neighbour fallback does `n1Z = TangentNeighbours(...).Item1 *
Complex(-1,0)` — this **negates the impedance** (non-physical negative-R probe) instead of stepping in the
**−direction** from the start point. Fix to a proper mirror: `mirror = start − (neighbour − start)`.

### B7 — Remove the gamma confusion + redundant parameter (PursuitEngine.Query, ~lines 245-247)
```
var gamma = RfHelpers.Z2G((z / 50.0) - Complex.One + Complex.One);  // dead: -1+1 cancels
gamma = (z - 50) / (z + 50);                                        // hardcoded 50 Ω
```
Delete the dead first line. The search works **in Z** (loadpull_pursuit.md §1.2); derive Γ only at I/O
boundaries via **RfCore** using the **actual Z0** (not hardcoded 50). And `RunOneTermination` takes **both**
`z` and `gamma` — redundant. Pass **only Z** through the internal path; compute Γ from Z (with the real Z0)
only where Γ is actually needed (output, warm-start metric). Remove the redundant parameter.

### B8 — Regenerate the golden data (it is currently wrong)
Hero 3 and Hero 3B golden data were generated by the buggy code, so the regression tests are validating
wrong physics. After B1–B7, **regenerate** the Hero 3 / Hero 3B golden, label self-generated/not-validated,
and **report the key numbers (Pout dBm, DE, MXP/MXE Z, MXP↔MXE VSWR, Zsource) for OWNER hand-verification
before freezing.** Do not silently re-freeze — the owner verifies against hand calcs, not "it passes."

## Acceptance
1. PASS A complete: convention documented at the `INl` source; sign/unit diagnostic at Hero 2 bias reported.
2. Single current-direction convention applied uniformly; Zin positive-real at bias; no per-site sign flips.
3. +0.1 dB overshoot; MXP criterion + interpolation in dBm; clean interpolation verified vs hand calc.
4. `WToDb` renamed + audited (ratio-only); no power expressed in bare "dB".
5. Steepest-ascent metric self-consistent; MXP↔MXE separation ~2–2.5 VSWR (Pedro) reported.
6. Mirror-neighbour fixed (no negative-R probes); gamma confusion + redundant param removed; Z0 not hardcoded.
7. Golden regenerated, key numbers reported for owner verification (not auto-frozen).
8. `dotnet build`/`dotnet test` green; Phases 1–4a still pass.

## Guardrails
- **REPORT after PASS A before changing any calculation.** The convention must be confirmed before signs
  propagate.
- One stated convention, applied once — **no sign flips tuned to make numbers look right.**
- Self-generated golden proves self-consistency, NOT correctness — the owner verifies the regenerated
  numbers by hand.
- Diagnostics over grinding — if a fix surfaces a deeper issue, report it; don't improvise.
- Update `src/Engine/HarmonicBalance/CLAUDE.md` (the INl convention) and the Loadpull CLAUDE.md.
