# Phase 4b-2 — Implementation Brief: Loadpull Pursuit (MXP/MXE Search + Auto-Zsource) → Hero 3B (Claude Code / Sonnet)

**Goal:** the **`loadpull_pursuit`** analysis — a query-minimizing steepest-ascent search that finds the **MXP** (max output power) and **MXE** (max efficiency) terminations at constant compression, reports the conjugate-match **Zsource** at backoff, and emits a recommended-terminations **`.gam`** file. Validated on **Hero 3B**. Built entirely on the proven 4b-1 loadpull engine.

> Read first, in order: root `CLAUDE.md`, `src/Engine/CLAUDE.md`, then `docs/design/loadpull_pursuit.md` (the whole note — authoritative), and the docs it builds on: `docs/design/loadpull.md` (the 4b-1 engine — the `Tuner`, `Loadpull` directive, 2-D sweep, live measurements, VSWR warm-start), `docs/design/harmonic-balance.md` (the inner HB solve), `docs/design/measurements.md` (Pout/Pdc/DE/PAE). RfCore `RFNetwork.VSWR` is the distance metric throughout. Where this brief and a design note disagree, the design note wins — flag, don't guess.

## Prerequisite (done)
Phases 1–4b-1 complete and passing: the loadpull engine, the `Tuner` (with internal bias-tee + exposed bias-supply V/I nodes), the 2-D Γ×Pin sweep with compression stop, the live Pavl/Pin_delivered/Pout/Gt/Gp, VSWR warm-start. **4b-2 CONSUMES the 4b-1 engine — each search "query" is one 4b-1 adaptive-drive-to-compression run. Do not modify the inner sweep or the HB solve.**

## Working style (same discipline as 4b-1)
**Diagnostics over deep problem-solving.** Searches will hit non-convergent and non-compressing terminations — that's expected and handled (§7 of the design). Record and move on; don't grind. Small fixes OK; large re-architecture → flag.

## Scope — build these, in this order

### STEP 1 — Efficiency calculation (add to the LP engine)
Per loadpull_pursuit.md §2. 4b-1 exposed the Tuner bias-supply V/I nodes but didn't compute efficiency. Add:
- **Pdc** = Σ `Vdc·Idc` over the Tuners' internal DC bias supplies (read the bias-supply nodes the Tuner exposes).
- **DE** = Pout/Pdc ; **PAE** = (Pout − Pin_delivered)/Pdc.
- Compute alongside the existing live Pout/Pin_delivered per inner-sweep point.
- Tests: DE/PAE at a known operating point match a hand calc (reuse Hero 2's verified numbers — the owner hand-checked efficiency there).

### STEP 2 — The steepest-ascent search engine (one engine, MXP & MXE)
Per loadpull_pursuit.md §1. One engine; the criterion selects the objective:
- **MXP criterion** = Pout at compression; **MXE criterion** = DE (default) or PAE (`EffType`).
- **Each query = one 4b-1 inner adaptive-Pin drive-to-compression run** at a candidate termination, returning the criterion (and the cached full inner sweep — Step 4).
- **Distance = `RFNetwork.VSWR`** between two complex terminations (Z/Γ-agnostic). `Dn`, `Ds`, threshold are **VSWR-denominated**. Internal working representation is **Z** (the VSWR-circle math, Step 5, has only a Z form).
- **Baylis algorithm:** (a) tangent-plane stage — query 2 neighbors at `Dn`, fit `∆C = m1∆x + m2∆y`, take the steepest-ascent perpendicular; (b) ascend by `Ds`, query, repeat if criterion increased, else shrink `Ds` to 1/3; (c) on convergence (`Ds < Dn`), final refinement — fit the 2nd-order polynomial (Baylis Eq. 4) over the surrounding points and report its analytic optimum.
- Acceptance target: reported optimum within **≤ 1.1 VSWR** of a high-resolution reference loadpull.
- Tests: on Hero 3B, MXP converges from a couple of start points to the same optimum within 1.1 VSWR; query count is modest.

### STEP 3 — MXP→MXE data sharing + Pedro seed (cheap second search)
Per loadpull_pursuit.md §4.
- **Cache every query** (its full inner compression sweep) keyed by termination, **VSWR-deduplicated** (a request within a tiny VSWR of a cached point is a cache hit — no re-solve). MXE reads efficiency from the *same* cached sweeps MXP already ran.
- **Run MXP first, then seed MXE ~2.25 VSWR from MXP** (the Pedro coupling — MXP↔MXE is empirically 2–2.5 VSWR for a stable FET), so MXE is a short refinement, not a cold search.
- Tests: MXE uses materially fewer new queries than MXP; the two optima land 2–2.5 VSWR apart (Pedro sanity check).

### STEP 4 — Auto-Zsource (conjugate match at backoff)
Per loadpull_pursuit.md §6. **Zin is computed ONCE per optimum, after it's found — not per query.**
- At the MXP (and MXE) load termination, set drive to **`ZsourceOBO` dB backed off** from compression (default 5 dB; granularity set by `PinStep`).
- Compute **Zin = V/I at f0** at the source-Tuner DUT-facing port from the converged HB result.
- Report **Zsource = Zin\*** (conjugate), per optimum (load-dependent for non-unilateral devices).

### STEP 5 — The recommended-terminations `.gam` builder
Per loadpull_pursuit.md §5 (only when `OutputGrid` is set):
1. Find MXP, MXE.
2. **`VSWR1` (focused, default 1.5) VSWR circle** around each → box1 (MXP), box2 (MXE) via min/max X,Y. (VSWR circle in Z domain, §5.1 — compute box extents directly from `z_center ± z_radius`, no need to sample the circle.)
3. Sample box1, box2 each `VSWR1_resolution`×`VSWR1_resolution` (default 4×4).
4. **`VSWR2` (broad, default 3) VSWR circle** around MXE → box3; combine box1/2/3 extents → box4.
5. Sample box4 `VSWR2_resolution`×`VSWR2_resolution` (default 4×4), **discarding points inside box1 or box2**.
6. Write focused + broad points + the MXP & MXE points to the `.gam`.
7. **Non-convergent exclusion** (unless `keepNonconvergingPoints`): drop output points within `nonconvergentVSWR` (default 1.05) of any termination found non-converging during the search; **warn** the user that points were removed.
- Tests: the `.gam` is dense near the optima, sparse outside; excluded points are within the exclusion radius of recorded non-convergent terminations.

### STEP 6 — The `loadpull_pursuit` directive + non-compression exit
Per loadpull_pursuit.md §3, §7.
- **Directive** `type=loadpull_pursuit`: all `loadpull` keys **except `Grid`**, plus `EffType` (DE/PAE, default DE), `ZsourceOBO` (default 5), `OutputGrid` (optional — no file if absent), `VSWR1`/`VSWR1_resolution` (1.5/4), `VSWR2`/`VSWR2_resolution` (**3**/4), `keepNonconvergingPoints` (false), `nonconvergentVSWR` (1.05). All values resolve through the expression engine. (Third concrete analysis directive, after `hb` and `loadpull`.)
- **Returns:** MXP termination & Pout, MXE termination & efficiency, Zsource (Zin\*) per optimum, and the `.gam` (if `OutputGrid`).
- **Non-compression exit (§7):** a candidate that doesn't reach `Compression` within `PinMax` is **unscorable** → rejected as an ascent step (shrink Ds / try elsewhere), excluded like a non-convergent point. If the **start point itself** is unscorable (no tangent plane formable), **abort with a clear message** ("DUT does not compress within PinMax=… — raise PinMax or check bias/load"). **Never silently raise `PinMax`.**
- **Generality:** works for any `TuneHarm` (1/2/3…) on `Sweep=Load`/`Source`; must **not crash** on a noisy 3f0/source pursuit (degrade gracefully).

## Acceptance gate — Hero 3B
`testdata/Hero3B/hero3B_at_compression.cnl` (Hero-3 PA with `PinMax=30` to compress, `loadpull_pursuit` directive). Owner-verified, self-generated regression (à la Hero 2/3).
- MXP & MXE found within a modest query budget; optima 2–2.5 VSWR apart (Pedro).
- MXE cheaper than MXP (cache hits + Pedro seed).
- Reported optima within **≤ 1.1 VSWR** of a high-res reference loadpull (self-generated, owner-verified).
- Auto-Zsource (Zin\* at 5 dB backoff) reported for both optima.
- `.gam` written with focused+broad structure; non-convergent points excluded (warned) unless `keepNonconvergingPoints`.
- **Non-compression exit verified:** lowering `PinMax` (e.g. to −18) aborts cleanly with the no-compression message — does not crash.
- Self-generated regression golden, labeled not-independently-validated, wired into CI with the <1e-5-is-noise rule where numeric.
- `dotnet build`/`dotnet test` green; Phases 1–4b-1 still pass.

## Guardrails
- CONSUME the 4b-1 engine — each query is a 4b-1 drive-to-compression run; do not modify the inner sweep or HB solve.
- Distance is **always `RFNetwork.VSWR`** (Z/Γ-agnostic); VSWR-circle math is **Z domain**, convert on write via RfCore.
- **Never silently raise `PinMax`** (user safety cap); unscorable start → abort with a clear message.
- Must not crash on a degenerate (3f0/source, noisy) pursuit — degrade gracefully.
- Diagnostics over grinding: non-convergent/non-compressing terminations are expected — record, exclude, move on.
- Update `src/Engine/CLAUDE.md` (and the loadpull CLAUDE.md) with the pursuit analysis, efficiency calc, and the `.gam` builder.
- Flag design questions to Opus/Chat; don't improvise.

*Phase 4b-2 exit (Hero 3B: MXP/MXE found ≤1.1 VSWR, auto-Zsource, `.gam` emitted, non-compression exit clean) completes the loadpull differentiator. Remaining Phase 4: 4c multi-tone → Hero 5, 4d multi-device → Hero 4.*
