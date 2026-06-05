# Phase 4c — Implementation Brief: Multi-Tone (Two-Tone) Harmonic Balance → Hero 5 (Claude Code / Sonnet)

**Goal:** extend the HB engine to **multi-tone (two-tone)** — the multidimensional-FFT / diamond-truncated
solution — validated on **Hero 5** (the two-tone intermodulation PA). Builds on the proven single-tone HB
engine (4a) and the now-trusted Jacobian (post-convergence-fix).

> Read first, in order: root `CLAUDE.md`, `src/Engine/CLAUDE.md`, `src/Engine/HarmonicBalance/CLAUDE.md`,
> then `docs/design/harmonic-balance.md` — especially §3.1 (commensurability), §3.2 (the `NumFreqs`/`Tone[i]`
> directive), §5.2–5.3 (grid vs solution spectrum, FFTOverSample), **§6 (multidimensional FFT, the diamond,
> the mixIndex enumeration)**, §7.2/§7.4 (the Jacobian's tone-pair index arithmetic), §16 item 1 (the locked
> mixIndex order). Also `docs/design/linear-engine.md` §4.4 (`V_nTone` multi-tone source). Design note wins
> over the brief.

## Prerequisite (done)
Phases 1–4b complete and passing. The single-tone HB engine works, the Jacobian is verified against the
finite-difference oracle (the permanent `JacobianFd_MatchesAnalytic` test), convergence is solid. **4c
GENERALIZES the existing engine from a 1-D harmonic axis to a 2-D mixing lattice — it does NOT rewrite it.**
The error function, the real-split Jacobian blocks, the dense Newton, continuation, the guard harmonic, the
nonlinear-DC seed all carry over; what changes is the index space (scalar harmonic k → tone-pair (k₁,k₂)),
the FFT (1-D → multidimensional), and the source (`V_1Tone` → `V_nTone`).

## Working style
**Diagnostics over deep convergence problem-solving** (same discipline as all of Phase 4). Two-tone is
stiffer than single-tone; if a power-sweep point won't converge, report the residual trajectory and which
mixing products carry it — don't grind. Small fixes OK; large re-architecture → flag.

## Scope — generalize the engine to two-tone (in this order)

### STEP 1 — directive + source: `NumFreqs`/`Tone[i]` and `V_nTone` in HB
- **HB directive** (harmonic-balance.md §3.2): parse `NumFreqs=N Tone[1]=… … Tone[N]=…` as the multi-tone
  spelling; the scalar `Tone=` is `NumFreqs=1`. Add `MaxMixOrder` (the diamond bound). Populate the
  `HarmonicBalanceAnalysis` tone set. (Hero 5: `NumFreqs=2`, two tones symmetric about RFfreq ± ToneSpacing/2.)
- **`V_nTone` in HB** (linear-engine §4.4): the multi-tone source is exercised in HB for the first time. Each
  `Freq[i]` stamps its phasor `V[i]` (which **may differ per tone** — `V[1]` and `V[2]` independent) at the
  matching mixing frequency. Hero 5 uses equal amplitudes, but the implementation must honor unequal `V[i]`.
- **Commensurability check** (§3.1): now does real work — validate every source `Freq`/`Freq[i]` lands on the
  `{k₁f₁+k₂f₂}` lattice; error naming any off-grid source.

### STEP 2 — the mixing-frequency grid (the diamond)
Per harmonic-balance.md §6.2–6.3:
- Build the retained **diamond** solution set: tone pairs `(k₁,k₂)` with `|k₁|+|k₂| ≤ MaxMixOrder`,
  half-plane representatives only (conjugate symmetry — §6.1).
- Enumerate them to the linear **`mixIndex`** axis in the **locked order** (§16 item 1): ascending total order
  `m=|k₁|+|k₂|` (so `(0,0)`=DC is index 0, carriers next, …), within an order by the upper-half-plane rule
  (`k₁>0`, or `k₁=0 ∧ k₂≥0`) sorted k₁ then k₂ descending. **This order is locked — the measurement library
  inverts it.** Raising `MaxMixOrder` must only append indices, never renumber.
- The retained-set size `M` replaces `(K+1)` everywhere in the dimension formulas (Newton unknowns
  `2·N_nl·M`, Jacobian `2·N_nl·M` square).

### STEP 3 — the multidimensional FFT
Per harmonic-balance.md §6.1:
- Sample on a rectangular `N₁ × N₂` grid (one period of each tone's phase axis: `v(φ₁,φ₂)`, `φ_t=ω_t·t`).
  Per-dimension `N_t = FFTOverSample · nextpow2(4·order_t)`.
- Multidimensional real FFT ↔ spectrum indexed by tone pair `(k₁,k₂)` at `k₁f₁+k₂f₂`. Half-plane conjugate
  symmetry stored; rest reconstructed (the single-tone DC-halved/positive-only convention generalizes — keep
  the FROZEN amplitude convention from §5.1, now per-axis).
- The **rectangular FFT computes the corner bins** (outside the diamond) too — they are NOT unknowns but
  ARE needed for anti-aliasing and for the Jacobian's sum/difference lookups (§6.2, §7.4).
- Reuse the existing 1-D FFT primitive per axis (a multi-D FFT is separable 1-D FFTs along each axis) — do
  not write a new FFT kernel; compose the existing one.

### STEP 4 — the Jacobian over the 2-D lattice
Per harmonic-balance.md §7.2/§7.4:
- The real 2×2 block structure is UNCHANGED (and its phasor-convention scaling is the post-fix-correct one —
  do not re-derive it; the FD test guards it). What changes: the scalar harmonic indices `k`,`i` become
  tone-pair `(k₁,k₂)`,`(i₁,i₂)`, and the `k−i`/`k+i` difference/sum frequencies become **vector**
  `(k₁−i₁, k₂−i₂)` / `(k₁+i₁, k₂+i₂)`, looked up in the rectangular FFT spectrum.
- The C (charge) rotation uses the **mixing-product frequency** `ω = 2π(k₁f₁+k₂f₂)` for the row's `(k₁,k₂)`
  (the generalization of `kω₀`).
- DC reality special-cases (§7.3) apply to the `(0,0)` index (the diamond's index 0) exactly as the
  single-tone k=0 cases.
- **Extend the permanent FD-Jacobian test to two-tone:** at a Hero 5 operating point, compare analytic vs
  finite-difference Jacobian over the 2-D mixing set, tight tolerance, all blocks. This is the oracle that
  the index/frequency arithmetic is right.

### STEP 5 — result cubes + measurements for IMD
- Write the `V`/`I` cubes with the **`mixIndex`** axis (data-model §7). DC (`(0,0)`) included.
- Implement enough of the measurement selectors to read the IMD products for the gate: `tone(x, k₁, k₂)`
  inverts the mixIndex enumeration; the Hero-5 products land per §6.3's table (carriers (1,0)/(0,1); IM2
  baseband (1,−1); IM3 (2,−1)/(−1,2); IM5 (3,−2)/(−2,3)). Compute **IM3 in dBc** (relative to a carrier) as
  the headline FOM, plus IM2/IM5.

### STEP 6 — diagnostics
- Extend the convergence trace to two-tone: residual per Newton iteration, per power-sweep step, and (on
  failure) which mixing products carry the residual.

## Acceptance gate — Hero 5 (self-generated regression, owner-verified)
`testdata/Hero5/hero5.cnl` — the grounded-source GaN HEMT PA, two tones at RFfreq ± ToneSpacing/2 (1.995 /
2.005 GHz, 10 MHz spacing), `MaxMixOrder=5`, `MaxHarm=4`, Pavl swept −20 … PavlStop_dbm. Note the non-trivial
**baseband load** `ZLoad_0 = 10+j10` (deliberately set to exercise even-order IM behavior).
- Two-tone HB converges across the power sweep; the mixing products land on the expected frequencies
  (the §6.3 table — verify the carriers, IM2 baseband, IM3, IM5 bins).
- **Self-generate the Hero 5 regression golden** (à la Heroes 2/3): run the sweep, export the V/I spectra at
  n_drain and n_gate over the mixIndex axis, label self-generated/not-independently-validated, place in
  `testdata/Hero5/`. Wire a CI regression test with the <1e-5-is-noise rule. The owner verifies key numbers.
- **Physics anchor (independent of the engine): the IM3 3:1 slope.** At low drive (below compression), the
  IM3 product power must rise ~3 dB per 1 dB of input power (the classic third-order slope), while the
  carriers rise 1:1. Report IM3 and carrier power vs Pavl at low drive and confirm the ~3:1 slope — this is
  the independent sanity check that the two-tone mixing is physically correct, not just self-consistent.
- **Unequal-amplitude source test:** one focused test with `V[1] ≠ V[2]` confirming each tone's excitation
  voltage is stamped at the correct magnitude (Hero 5 itself uses equal amplitudes; this guards the general
  path).
- The FD-Jacobian test passes in two-tone (Step 4).
- `dotnet build`/`dotnet test` green; Phases 1–4b still pass (single-tone unchanged — it's the `NumFreqs=1`
  path).

## Guardrails
- GENERALIZE, don't rewrite: single-tone is the `NumFreqs=1` case of the same engine; its behavior and golden
  must stay unchanged. The real-split Jacobian blocks and their (post-fix) scaling are correct — reuse, the
  FD test guards them.
- Compose the existing 1-D FFT for the multi-D FFT (separable per axis) — no new FFT kernel.
- The mixIndex enumeration order is **locked** (§16 item 1) — append-on-raise, never renumber; the
  measurement library depends on it.
- Honor unequal `V[i]` in `V_nTone` even though Hero 5 uses equal amplitudes.
- Self-generated golden proves self-consistency, not correctness — the IM3 3:1-slope check is the
  independent physics anchor; the owner verifies key numbers by hand.
- Diagnostics over grinding: two-tone non-convergence → report residual + carrying mixing products, don't
  grind or burn context.
- Update `src/Engine/HarmonicBalance/CLAUDE.md` with the multi-tone generalization.

*Phase 4c exit (Hero 5: two-tone converges, IM products correct, 3:1 slope holds, golden frozen) leaves only
4d (multi-device → Hero 4) to complete Phase 4.*
