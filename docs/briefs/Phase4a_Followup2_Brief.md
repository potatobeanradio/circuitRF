# Phase 4a Follow-up 2 — Correct the DC Interface Extraction (remove the virtual-admittance clamp)

**Context:** A review found the HB engine's DC handling wrong. **Good news first: the `HbNewton.cs`
Newton loop is structurally CORRECT** — it solves all harmonics k=0…K simultaneously (unknowns =
2·N·(K+1)), DC is a full participant in the residual and Jacobian, and the k≥1 `HbLinearExtractor.Extract`
is a correct Maas Norton absorption. **Do NOT rewrite the engine.** The bug is localized to ONE method:
`HbLinearExtractor.ExtractDC`, which fakes the DC interface with a `Y_VIRT = 1e6 S` clamp. This brief
fixes that one method. Keep the diagnostics-over-grinding discipline.

## The bug (one method)
`ExtractDC` stamps a virtual admittance `Y_DC_VIRT = 1e6 S` at each interface node to "pin" the DC
voltage to the nonlinear-DC operating point. **This is wrong** — it clamps the DC interface voltage so
it CANNOT shift with drive, defeating the self-biasing physics (the whole point of solving DC in the
Newton). A 1e6 S admittance dominates the real circuit. This is NOT regularization (regularization is
the 1e-12 gmin already in `BuildMna`); it is a clamp that overrides physics.

## The correct formulation (Maas §3.3.1, eqns 3.10–3.14 — the partitioning)
The DC harmonic is treated **exactly like every other harmonic**, at ω=0:
- The linear subcircuit's admittance submatrix is **diagonal in harmonic** (Maas 3.10–3.11):
  `Y_{m,n} = diag[Y_{m,n}(0), Y_{m,n}(ω0), Y_{m,n}(2ω0), …]`. The **k=0 entry is the REAL DC admittance
  `Y_{m,n}(0)`** of the linear (bias) network — NOT a fake clamp.
- The bias and drive sources are **absorbed into the linear subcircuit** and converted to a Norton
  current-source vector `I_s` in parallel with the N interface ports (Maas 3.13–3.14: `I = I_s +
  Y'_{NN}·V`). The **DC bias voltages (Vb1=gate, Vb2=drain) live in the k=0 slot of that source vector**
  (Maas 3.12). So the bias supplies enter the k=0 balance as the DC component of `I_src`, through the
  network's real `Y(0)` — solved simultaneously with the harmonics so mixing can shift the DC.

**`ExtractDC` should therefore do exactly what `Extract(omega)` already does, at ω=0**, with the bias
sources ACTIVE:
1. Build the linear-partition MNA at ω=0 (DC: inductor→short, capacitor→open, gmin to ground — the
   existing `BuildMna` path, omega=0). This is the SAME DC formulation as linear-engine §5.
2. Zero sources → extract `Y_{NN}(0)` by the Z-column method (as `Extract` does).
3. Active sources → solve → `V_oc(0)` at the interface (this will be the bias-set DC voltage:
   gate≈−3.05 V, drain≈48 V, set by the bias-tee chokes which are DC shorts to the supplies).
4. `I_src(0) = −Y_{NN}(0)·V_oc(0)` (the Norton form, identical to `Extract`).
Return `(Y_{NN}(0), I_src(0))` — the REAL DC interface, no clamp.

**Delete `Y_DC_VIRT`** from both `HbLinearExtractor` and `HbNewton`, and the doc comments referencing it.

## The genuine subtlety — flag it, don't improvise (this is what tripped the last attempt)
At DC the interface node is tied through a bias-tee choke (a DC short) to an ideal voltage source, so
the node is **voltage-pinned** — which can make `Z_{NN}(0)` singular when you try to invert it to
`Y_{NN}(0)` (a voltage-pinned node has ≈0 impedance to the source). The previous attempt panicked at this
and clamped. The CORRECT responses, in order of preference:
1. The **gmin** already stamped in `BuildMna` (1e-12 S) plus the active bias sources should give a finite
   `V_oc(0)` directly — you may not need to invert a singular Y at all if you get `V_oc` from the
   active-source solve (step 3) and `Y_{NN}(0)` is only needed for the Norton current.
2. If `Y_{NN}(0)` (from `Z_{NN}` inversion) is genuinely singular/ill-conditioned because the interface
   is purely voltage-pinned, **STOP and report it with the convergence/singular-node diagnostic** — do
   NOT invent another clamp or fudge. This is a formulation question to bring back to design, not to
   improvise. Report: which interface node, the `Z_{NN}(0)` condition, and what `V_oc(0)` the active
   solve gives.

In short: try the real extraction (gmin-regularized, bias sources active). If it works (very likely — the
bias-tees give every interface node a real, if small, DC path through the choke to the supply, which is a
finite admittance, not a true open), great. If it's singular, flag it — don't clamp.

## Sanity check after the fix
- DC interface voltages should still land at the bias point (gate ≈ −3.05 V, drain ≈ 48 V at low drive)
  — but now because the REAL bias network sets them, not a clamp.
- **The DC drain current `I_nl[drain, 0]` must MOVE with Pin** (rise with drive for this class-AB bias —
  self-biasing). Report `I_nl[drain,0]` and the DC drain voltage vs Pin so the owner can confirm the DC
  shifts. (With the clamp, DC voltage couldn't move; with the real extraction, it can.)
- Hero 2 should still converge.

## After the fix — proceed to the golden data (the prior brief's Tasks 2–3)
Once `ExtractDC` is corrected and the DC-shifts-with-drive sanity check holds, do the prior follow-up
brief's Task 2 (self-generate the Hero 2 regression golden, Pin −20…0 dBm, V and I, labeled
self-generated/regression-not-validated) and Task 3 (wire the CI regression test, <1e-5-is-noise rule).
**Generate the golden ONLY after the DC fix** — golden data with the clamp would freeze wrong physics.

## Guardrails
- This is a ONE-METHOD fix (`ExtractDC`) + deleting `Y_DC_VIRT`. Do NOT rewrite `HbEngine.cs`,
  `HbNewton.cs`'s loop, `HbFft.cs`, or `Extract(omega)` — they are correct.
- Do NOT introduce another clamp/fudge for the DC singularity. Real extraction (gmin + active sources),
  or flag it.
- Diagnostics over grinding. If the corrected extraction won't converge or the DC won't shift sensibly,
  REPORT the diagnostic and stop — do not launch a large investigation or burn context. Small fixes OK.
- Update `src/Engine/HarmonicBalance/CLAUDE.md`: DC interface is extracted as the real Y(0) + bias Norton
  source (Maas 3.10–3.14), NOT clamped; DC is a full Newton participant.