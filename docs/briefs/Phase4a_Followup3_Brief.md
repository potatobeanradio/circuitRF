# Phase 4a Follow-up 3 — Auto-regularize the voltage-pinned DC interface; then golden data

**Context:** The previous session correctly fixed `ExtractDC` to do real Maas-style DC extraction and
correctly STOPPED at the voltage-pinned-interface singularity (ideal choke + ideal source → Z(0)=0 at
the gate/drain interface nodes) instead of clamping. That was the right call. The design decision is
made: **Path A — auto-regularize via `InductanceRegularization`** (linear-engine.md rev 5, §4.3.1). This
brief implements it, then proceeds to the golden data. Keep diagnostics-over-grinding.

## The decision (linear-engine.md §4.3.1 — read it)
A DC interface node pinned through an ideal inductor (choke, no series R) to an ideal voltage source has
Z(0)=0 → singular Y(0). This is the math correctly reporting that an *ideal* bias-tee fixes that node's
DC voltage. The **exact** fix is constrained-system reduction (treat the pinned node's DC voltage as
known, solve the reduced free system) — but that is a **deferred** formulation change. The **v1 fix
(this brief)** is auto-regularization, the inductive dual of gmin.

## Task 1 — Auto-apply InductanceRegularization in the DC interface extraction
In `HbLinearExtractor.ExtractDC` (and wherever the regularization mode is read):
- Keep the honest extraction the previous session built (real Z-column at ω=0, bias sources active for
  V_oc). Keep the singular-Z(0) **detection** it added.
- When Z(0) is singular from a voltage-pinned node AND `InductanceRegularization` is `IfNecessary`
  (default) or `Always`: **auto-apply a tiny series resistance to the offending ideal inductor(s)** (the
  bias-tee chokes with no `R=`) so Z(0) becomes finite → invertible Y(0). Default series-R floor
  **1e-6 Ω**, exposed as an engine setting. **Warn**, naming the interface node(s) regularized (e.g.
  "InductanceRegularization engaged: series R=1e-6 Ω added to ideal inductor Lchoke_g pinning n_gate at
  DC").
- `IfNecessary`: try without first, apply only on the singularity (the detection already there is the
  trigger). `Always`: apply from the start. `Never`: do NOT regularize — fail with the singular-node
  diagnostic (the one already implemented, naming the node and its V_oc(0)).
- This is the inductive dual of the existing gmin (`ConductanceRegularization`) — mirror that code
  path's structure. It is regularization, honestly labeled, NOT a circuit edit and NOT a clamp.
- **Do not implement the exact constrained-reduction ("Option 2")** — it is deferred (§4.3.1). Just the
  auto-R regularization.

**Why this isn't the old clamp:** the rejected fix used Y_VIRT=1e6 to *override* the physics (forcing V
to V_oc). This adds a tiny *real* series R (1e-6 Ω) that makes an ideal element non-degenerate and
converges to the exact answer as R→0 — the same standing as gmin. Honestly labeled, auto, warned.

## Task 2 — Verify self-biasing, then sanity-check
With the DC interface now non-singular and DC a full Newton participant:
- Confirm Hero 2 converges across the sweep.
- **Report DC drain voltage and DC drain current vs Pin.** With ideal bias-tees the DC *voltage* stays
  pinned (~48 V drain, −3.05 V gate) but the DC *current* the supply delivers should shift with drive
  (self-biasing shows in the current). Report the trend so the owner can confirm the DC participates.
- Sanity anchors: gate harmonics above the fundamental ≈ 0 (linear gate input); drain harmonics present
  and decaying.

## Task 3 — Self-generate the Hero 2 regression golden data (only after Tasks 1–2 hold)
- Run `hero2.cnl` over **Pin = −20 to 0 dBm in 1 dB steps (21 points)**, MaxHarm=4.
- Export converged **node voltages AND branch currents** at `n_drain` and `n_gate`, per harmonic
  (DC + 4), per power point, as CSV (real & imag), same column style as the existing files.
- **Label clearly as SELF-GENERATED REGRESSION references, NOT independently validated** (header
  comment or a README in `testdata/Hero2/`). A future independent cross-check is still owed. The old
  external-reference files are NOT trusted — mark them deprecated/superseded or remove them.

## Task 4 — Wire the regression test
- Add a Hero 2 CI regression test: run `hero2.cnl`, compare V and I against the self-generated golden,
  with the owner's tolerance rule: components with magnitude **< 1e-5 (real or imag) are numerical
  noise → pass-by-default**; compare only signal-bearing bins.
- Assert the physics anchors where cheap (DC gate −3.05, DC drain ~48; gate harmonics ≈ 0; DC current
  shifts with Pin).

## Acceptance
1. `ExtractDC` auto-regularizes the voltage-pinned interface via `InductanceRegularization` (warned,
   honestly labeled); `Never` mode still fails with the diagnostic. No clamp, no Y_VIRT.
2. Hero 2 converges; DC current trend vs Pin reported.
3. Self-generated regression golden in `testdata/Hero2/`, labeled, V and I, −20…0 dBm.
4. CI regression test wired with the <1e-5-is-noise rule; anchors asserted.
5. `dotnet build`/`dotnet test` green; Phases 1–3 and the rest of 4a still pass.

## Guardrails
- This routes through the EXISTING `InductanceRegularization` setting — mirror the gmin code path. Do
  not invent a new mechanism, and do NOT implement the deferred Option-2 reduction.
- No clamp, no Y_VIRT, no fudge that overrides physics — a tiny real series R only.
- Generate golden ONLY after Tasks 1–2 hold (golden with a broken DC would freeze wrong physics).
- Diagnostics over grinding: if it won't converge or the DC trend looks wrong, report and stop.
- Update `src/Engine/HarmonicBalance/CLAUDE.md`.