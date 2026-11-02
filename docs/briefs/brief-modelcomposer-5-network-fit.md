# Brief MC-5 — Fitting the extrinsic network

**Series:** `brief-modelcomposer-0-overview.md` (D5, D13) · **Tag:** `R-mc5-<m>` · **Depends on:** MC-4
**Area:** `src/Design/Modeling/` (fit flows); `src/Engine/Optimization/` (the ask/tell algorithms, used as they
are)
**Read first:** [P1] §5.5.2 (cold-FET extraction: §5.5.2.2 capacitances, §5.5.2.4 resistances and inductances,
both forward-bias variants), §5.6.3.1 (what a wrong parasitic does); [R11] ch. 3.4 and **ch. 4 (uncertainty of
small-signal extraction — the identifiability report follows it)**; `docs/design/tuning-optimization.md` §10–§12

---

## 1. Goal

Values for the extrinsic network that make physical sense and make the de-embedded intrinsic device behave like a
lumped, frequency-independent circuit — with an honest statement of which values the data actually determines.

## 2. Requirements

**R-mc5-1 — Methods**, chosen per document; they compose (a direct start, then an optimised refinement).
1. **Cold-FET direct** ([P1] §5.5.2):
   - pinched off at `Vds = 0` → the shunt capacitances from Im Y (eq. 5.4);
   - forward gate at `Vds = 0` → the series R and L by a least-squares fit of eq. 5.7 over a moderate-to-high
     band;
   - the **less-extreme forward-bias variant** of §5.5.2.4 as an option, with the channel-resistance term, because
     strong forward bias can stress GaN.

   A required cold bias absent from the data is a refusal naming the bias it needs.
2. **Multi-bias optimisation.** The user picks bias points — default: one off, one on, one near the knee, one in
   saturation, chosen from the data automatically and editable. The cost combines:
   - (a) the **frequency flatness** of the de-embedded intrinsic elements (MC-4 R-mc4-5) at the active points —
     a correct shell makes them flat ([P1] §5.6.3);
   - (b) the residual of the cold-FET models at the cold points;
   - (c) optionally, the S residual against a fitted small-signal intrinsic circuit per active point.

   The weights are document settings with stated defaults.
3. **Fixed**: a value is typed or taken from EM, for example the manifold from an EM run of the layout. Locked
   values are never fitted.

**R-mc5-2 — Constraints.**
- Bounds per variable; R, L, C ≥ 0 by default ("no negative resistors").
- Ties: a variable equals another times a constant (`Cpga = Cpda`, `Lg = Ld`).
- The coupling `|k| < 1` as a penalty that is zero inside.
- An optimum on a bound is reported as **at bound** with the bound's name.

**R-mc5-3 — Search.**
- The registry's global algorithms (differential evolution or CMA-ES) for a seeded multi-start, then
  Levenberg–Marquardt polish. These are the existing ask/tell implementations in `src/Engine/Optimization/`,
  driven with **this** cost; they never call a simulator.
- The variables are mapped to the unit box logarithmically for positive quantities.
- The same seed gives the same result.
- Cancellable through `RunControl`, with progress per start. A cancelled fit leaves the previous values.

**R-mc5-4 — Identifiability report ([R11] ch. 4).** At the solution:
- from the residual Jacobian, each variable's standard error and its correlation with every other;
- a variable whose relative error exceeds a threshold (default 20 %), or whose |correlation| with another exceeds
  0.95, is **poorly determined**. The report says with which variable, in one line, e.g. "Cpgi poorly determined
  (ρ = −0.98 with Cpga)".

The GaN network's outer/inner capacitor split is the expected case, because only data above a few GHz separates
them; the report suggests a lock or a tie in one line. The multi-start spread of each variable is reported beside
its standard error.

**R-mc5-5 — Results.** The chosen values, their errors, the method and its settings are written to the `.cmc`
`Network` block. Changing them invalidates the intrinsic cache (MC-4) and every later step's result, and the
document says which steps are stale.

## 3. Not in this phase
Constituent fitting; the window (MC-14 shows these results).

## 4. Gates (minimal tests, run only these classes)
- `ColdFetExtractionTests` — on `SyntheticFetData` with the classic shell, the direct method recovers every
  element within the tolerance the synthetic noise level implies (stated in the test); a missing cold bias is
  refused naming it.
- `MultiBiasNetworkFitTests` — on the GaN network, the optimised fit recovers `rg rd rs Lg Ld Ls` within stated
  tolerances, and the de-embedded intrinsic elements are flat to within the stated level; every value respects
  its bounds; a tie holds exactly; the same seed gives the same values.
- `IdentifiabilityReportTests` — with data limited to 2 GHz, the outer/inner gate capacitances are reported as
  poorly determined with each other; with data to 40 GHz they are not.
- `NetworkFitCancelTests` — a fit cancelled mid-start leaves the previous values in the document.
