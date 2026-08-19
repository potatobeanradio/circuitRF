# circuitRF — Harmonic Balance Engine Design

**Status:** Draft (rev 2, updated 2026-06-23) for review · **Date:** 2026-05-31

> **Stage 1 landed (2026-06-23):** Every HB run now emits a stacking `ToneFreqs` cube: single-tone `[tone(1)]=[f0]`, two-tone `[tone(2)]=[f1,f2]`. Both are non-`__`, so a parametric sweep that changes the tone frequency yields `ToneFreqs[sweep,tone]` with per-point values — unlike the frozen `k·f0` harmonic-axis values, which are baked at the first sweep point. `HbSpectrum` (`src/Core/Expressions/HbSpectrum.cs`) is the single home for the index/order→frequency rule. `ToneFreqs` and `MetaMixOrder` are hidden from the signal picker. **Stage 2 (next brief)** will flip the harmonic axis to integer orders and reconstruct physical frequency from `HbSpectrum` + `ToneFreqs[slice]`.
**Reads with:** `docs/design/data-model.md` (§3 elaboration + partition sets, §5 `ComponentModel`/`Evaluate`, §7 result model), `docs/design/linear-engine.md` (§2.1 the three MNA uses, §4.4 `Z_Port`/tone sources, §10 reuse by HB), `docs/design/nonlinear-dc.md` (the Phase-3 nonlinear-DC solver, AD engine, and SDD this engine **consumes**), `docs/design/measurements.md` (§3.4 IMn, §5 V/I retention + `Pdc` from k=0), `docs/design/expressions.md` (§12 AD for `dg`/`dc`), `docs/PRD.md` (§4 Heroes 2–5, §5 HB scope, §14 NFRs).
**Defers to:** the data-cube note (axis/units, backing store), `src/Engine/CLAUDE.md` (the frozen FFT/sign conventions).

> **Phase note (rev 2):** this engine is **Phase 4**, built on the completed Phase 3 (nonlinear DC + the `Evaluate` `(i,q,dg,dc)` contract + the forward-mode AD engine + the SDD device, all in `nonlinear-dc.md`). Where this note describes the `Evaluate` contract, the AD-derived `dg`/`dc`, the SDD, and the nonlinear-DC solve, those are **built and validated — Phase 4 consumes them**, it does not re-implement them. Phase 4 adds the frequency-domain machinery *on top*: the FFT layer, the conversion-matrix Jacobian, the dense Newton over harmonics, continuation across sweeps (`DriveStepping`), the guard harmonic, and V/I-cube writeback. Phase 4 is **sub-gated** — 4a single-tone→Hero 2, 4b loadpull→Hero 3, 4c multi-tone→Hero 5, 4d multi-device→Hero 4 — so the single-tone engine is proven before the sweep/transform layers pile on.

This note specifies circuitRF's **harmonic-balance (HB) engine**: how an `ElaboratedNetlist` is partitioned, how the error function and its conversion-matrix Jacobian are formed and solved by Newton's method, how the time/frequency transform is conventioned (single- and multi-tone), and how convergence is driven (initial guess, DC seed, continuation, guard harmonic). It gates **Phase 4** and **Heroes 2–5**. It builds directly on the linear engine (§10 of that note is its supplier) and the Phase-3 `Evaluate` contract + AD + nonlinear-DC solver (`nonlinear-dc.md`), which it **consumes**. It defines *method, contracts, and conventions* — not full derivations or C#. No code is written until this is approved.

The formulation follows the owner's IMS short-course "Understanding Harmonic Balance Simulation for RF Power Amplifier Designers" (WSJ-3) and the two anchor references it cites — Kundert & Sangiovanni-Vincentelli (1986) and Maas, *Nonlinear Microwave and RF Circuits*, 2nd ed. (2003) — extended to multi-tone, which the course deliberately left out of scope.

---

## 1. What this engine produces

- **Single-tone HB** (Heroes 2, 4) — the steady-state spectrum of a circuit driven by one RF tone plus DC bias, over a power sweep, reporting Pout/gain/DE/PAE. The strong partition test (Hero 4) places **two** FETs in the nonlinear set across a linear interstage network.
- **Two-tone HB** (Hero 5) — the steady-state `{k₁f₁ + k₂f₂}` spectrum, reaching **mixing order ≥ 5** with baseband and harmonic-zone products retained, for IM2–IM5 extraction.
- **The HB solution feeding loadpull** (Hero 3) — HB is the inner solve the loadpull experiment sweeps; the load/source Γ enters through the linear partition's interface termination, and previous-point continuation carries convergence across the grid.

Every run writes a `DataSet` (data-model §7) whose **primary cubes are `V` and `I`** — node-voltage and branch-current spectra, complex, per harmonic (single-tone axis `harmonic`) or per mixing component (two-tone axis `mixIndex`), per node/terminal, per sweep point — **including the k = 0 (DC) component** (measurements §5). Measurements (Pout, PAE, IMn, …) are evaluated over those cubes afterward; this engine's obligation is to retain V *and* I, DC included, so `Pdc`/`PAE` read from the same self-consistent HB result rather than a separate DC run.

---

## 2. The HB problem, in one frame

HB is a **guess-and-check** steady-state method posed in the **frequency domain**, with the nonlinear device evaluated in the **time domain** and transformed across by FFT:

1. Partition the elaborated circuit into a **linear subnetwork** and a set of **nonlinear devices** (the partition is binary and topological — data-model §3 supplies `NonlinearComponents`/`NonlinearNodes`).
2. The unknowns are the **Fourier components of the node voltages at the nonlinear-facing nodes**, `V` — the nodes where the two subnetworks connect. Nothing interior to either subnetwork is an unknown of the Newton solve.
3. **Guess** `V`; compute the current the *linear* side pushes into those nodes and the current the *nonlinear* side pushes back; the circuit is **balanced** when they cancel at every harmonic (including DC) at every interface node.

The balance condition is the **error function**

```
F(V) = I_linear(V) + I_nonlinear(V) ≈ 0
```

with, writing the linear contribution explicitly (linear-engine §10),

```
I_linear(V) = Y_s · V_s + Y_{N×N} · V
F(V)        = Y_s · V_s + Y_{N×N} · V + I_nonlinear + I_Qnonlinear
```

- `Y_{N×N}` — the linear subnetwork's **interface admittance** seen at the nonlinear-facing nodes, per harmonic (a block-diagonal-over-harmonics frequency-domain N-port). The linear engine extracts it and wraps it as an RfCore `Network` (linear-engine §10). It is **constant across Newton iterations**.
- `Y_s · V_s` — the **source excitation** the independent sources (bias + RF drive) present at the interface, as a Norton current injection. Because `V_s` does not move with the guess of `V`, this term is **computed once** per sweep point (linear-engine §2.1; the deck's "Note: V_s does not change with respect to guess of V").
- `I_nonlinear`, `I_Qnonlinear` — the conductive and charge-displacement currents the nonlinear devices push into the interface, recomputed every iteration from the present `V` (§4).

When `‖F(V)‖` is below tolerance, the guess is the steady-state solution; the full internal solution is then recovered by one linear back-substitution (§9).

---

## 3. Partition and the interface, from the linear engine

The linear engine is the HB engine's supplier (linear-engine §10). At setup, and once per distinct harmonic frequency, HB asks it for **two** things at the nonlinear-facing nodes:

1. **The interface network** `Y_{N×N}(kω₀)` for `k = 0 … 2K` — the admittance the devices see (the "extra port added at each nonlinear node" of the deck's multi-port extraction). Note the upper index is **2K**, not K — the Jacobian's sum-frequency block needs it (§6, §7).
2. **The source-excitation vector** `Y_s · V_s` at the interface, per harmonic — the bias + drive transformed to a Norton injection at the nonlinear nodes.

Both reuse the §9/§10 machinery of the linear note (build the linear-partition MNA, factor per harmonic, multi-RHS extract at the nonlinear-facing nodes; and a source-driven solve for the excitation). The **DC (k = 0) member is formulated exactly like the standalone nonlinear-DC analysis** (inductor short, capacitor open, `gmin`) — there is one DC formulation, used standalone and as the k = 0 slice here, which is why `Pdc` reads from this k = 0 component (linear-engine §2.1, measurements §5).

For **loadpull** (Hero 3) the load/source Γ is a termination on the linear partition: changing Γ_L re-extracts `Y_{N×N}` (at the fundamental, and at the harmonics for harmonic loadpull, where `Z₂f₀ … Z_Hf₀` are set per the analysis's `HarmonicTermination[]`). The nonlinear devices and their `Evaluate` path are untouched by the sweep — only the interface network the deck calls `Y_{N×N}` moves.

### 3.1 Tones are declared by the analysis; sources are validated against the grid (commensurability)
The HB analysis **declares its tone(s)** — the fundamental `f0` (single-tone) or the tone set `{f1, f2, …}` (multi-tone) — and from them the engine builds the harmonic/mixing grid (the frequencies it will stamp `freq` at, §5/§6). Independent tone sources do **not** define the grid; they are **checked against it**. At setup the engine runs a **commensurability check**:

- Enumerate every tone frequency of every voltage source — each `V_1Tone`'s `Freq`, and every `Freq[i]` of every `V_nTone` (linear-engine §4.4).
- Verify each lands **exactly on the declared grid**: an integer combination of the analysis tones (`k·f0` single-tone; `k₁f₁ + k₂f₂` within the retained set, multi-tone).
- If any source frequency is **off-grid**, error at setup naming the offending source and frequency (e.g. *"V_1Tone:Vdrive Freq=2.001 GHz is not commensurate with the HB tone grid {f0 = 2 GHz}"*). This catches the off-grid-drive mistake — the frequency-domain analog of the `Z_Port` band-edge drift (linear-engine §4.4) — before it produces silent garbage, and it is what makes the two-tone setup well-posed (every source must live on the `{k₁f₁+k₂f₂}` lattice the diamond is built on, §6).

This is why the relationship between the user-set **`Freq`** (a source's tone) and the injected **`freq`** (what the engine stamps) is exact by construction: the grid is built from the declared tones, the source `Freq`s are validated onto it, and `freq` at harmonic `k` is the exact `k·f0` (linear-engine §4.4) — so a `Z_Port` band edge, a source `Freq`, and the stamped `freq` all agree to the bit.

### 3.2 The HB analysis directive (`.cnl` grammar)
The HB analysis is declared at the top level (the `TestBench`, data-model §2.1/§10) with an `analysis` line of `type=hb`. It populates the `HarmonicBalanceAnalysis` design type (data-model §4). **All knobs are `key=value` and resolve through the expression engine**, so each may be a literal *or* a named parameter/variable declared elsewhere in the netlist (the "config as parameters" convention — a user sets `MaxHarm=7` once at the top and the directive references it). Keys:

| Key | Meaning | Default |
|---|---|---|
| `Tone` | single-tone fundamental f0 (Hz). Multi-tone uses `NumFreqs=N Tone[1]=… … Tone[N]=…` instead (the `Tone=` scalar is the `NumFreqs=1` spelling — one model, two spellings, mirroring `V_1Tone`/`V_nTone`, linear-engine §4.4) | (required) |
| `NumFreqs` | number of analysis tones (multi-tone); with `Tone[1..N]`. Absent / `1` ⇒ single-tone `Tone=`. Capped at `AnalysisSettings.HbMaxTones` (6), §6.6 | 1 |
| `MaxHarm` | harmonic count K (single-tone): solve harmonics 0…K | 7 |
| `MaxMixOrder` | mixing-order truncation (multi-tone diamond `|k₁|+…+|k_N| ≤ MaxMixOrder`, §6); ignored single-tone. Together with `NumFreqs` it sets the retained-product count, which is capped — §6.6 | 5 |
| `Sweep` | the parametric sweep, `"<var>: <start> .. <stop> step <step>"` (the swept variable is any §8 variable, e.g. `Pavl_dbm`) | none (single point) |
| `FFTOverSample` | anti-aliasing grid multiplier `1·16·…` (§5.3) | 1 |
| `Tol` | absolute convergence tolerance `‖F‖` (§12.2) | 1e-6 |
| `MaxIter` | Newton max iterations per HB solve before continuation backoff / reporting non-convergence | 100 |
| `DriveStepping` | RF-drive continuation mode `{ IfNecessary, Always, Never }` (§11) | IfNecessary |
| `GuardHarmonic` | guard-harmonic profile/index (§12.1); `0`/absent = off | off |
| `ConductanceRegularization` | gmin mode `{ IfNecessary, Always, Never }` (linear-engine §4.3) | IfNecessary |
| `InductanceRegularization` | inductance-block mode `{ IfNecessary, Always, Never }` | IfNecessary |

```
analysis HB1  type=hb  Tone=RFfreq  MaxHarm=MaxHarm  Sweep="Pavl_dbm: -20 .. 20 step 1" \
     FFTOverSample=OverSamp  Tol=HBtol  DriveStepping=DriveStep  GuardHarmonic=Guard
```

The **`Tone` value (or the `Tone[1..N]` set) declares the analysis tones** — the grid the commensurability check (§3.1) validates every source `Freq` against, and the tones the engine stamps mixing products at as exact `k₁·f₁ + … + k_N·f_N` (linear-engine §4.4). Output retention defaults to **all `V` and `I`, all harmonics/mixing products including DC** (§9, measurements §5); a future `Keep=` key may prune. Multi-tone uses `NumFreqs=N Tone[1..N]` with `MaxMixOrder` (the diamond, §6); single-tone uses the scalar `Tone=` with `MaxHarm`. Everything else is identical (Phase 4c). Example two-tone directive (Hero 5):
```
analysis HB1  type=hb  NumFreqs=2 Tone[1]=RFfreq-ToneSpacing/2 Tone[2]=RFfreq+ToneSpacing/2 \
     MaxHarm=MaxHarm MaxMixOrder=MaxMixOrder  Sweep="Pavl_dbm: -20 .. PavlStop_dbm step 1" \
     FFTOverSample=OverSamp Tol=HBtol DriveStepping=DriveStep GuardHarmonic=Guard
```
**Three or more tones use the identical spelling** — only `NumFreqs` and the number of `Tone[i]` entries change. The engine dispatches `T = 2` to the FFT path and `T ≥ 3` to the APFT path (§6.4–§6.6); nothing in the directive says which. Example (`testdata/Hero5/hero5_3tone.cnl`):
```
analysis HB3  type=hb  NumFreqs=3 \
     Tone[1]=RFfreq-ToneSpacing  Tone[2]=RFfreq  Tone[3]=RFfreq+ToneSpacing \
     MaxMixOrder=MaxMixOrder  MaxHarm=MaxHarm  FFTOverSample=OverSamp  Tol=HBtol
```
**Pick `MaxMixOrder` down as the tone count goes up.** The default of 5 is sized for two tones; at 6 tones it asks for 1,827 mixing products and is refused (§6.6). The Analysis Setup dialog shows the count live beside the field.

> **Note — first concrete analysis-directive grammar.** Until now analysis/measurement directives were stored opaquely (`RawDirective`, data-model §10, deferred grammar). The HB directive above is the **first** one given real grammar; it sets the `type=<kind> key=value` pattern the other analyses (`sparam`, `dc`, `loadpull`) will follow when their directives are specified. The `key=value` values resolving through the expression engine (so any knob can be a parameter) is the reusable convention.

---

## 4. The nonlinear side — time-domain evaluation, the `(i, q, dg, dc)` contract

Nonlinear models are time-domain (data-model §5; the deck's "models are typically time-domain"). Per Newton iteration, for each nonlinear node's voltage spectrum `V`:

1. **IFFT** `V → v(t)` on the evaluation grid (§5), at every nonlinear node.
2. **Evaluate** the device(s): `Evaluate(in PortVoltages v)` returns the time-domain samples
   - `i` — port currents `i(t)`
   - `q` — port charges `q(t)`
   - `dg = di/dv` — conductances (the IV-plane slope; for a FET, `g₂₁ = gm`, `g₂₂ = gds`, deck slide 19)
   - `dc = dq/dv` — capacitances (the QV-plane slope; deck slide 23)
3. **FFT** back: `I_nonlinear = FFT{i}`, `Q_nonlinear = FFT{q}`, and the charge current `I_Qnonlinear = jω · Q_nonlinear` (charge → current in the frequency domain, deck slide 22). The derivative samples transform to `G = FFT{dg}` and `C = FFT{dc}`, resolved to harmonic **2K** for the Jacobian (§7).

This is exactly the `(i, q, dg, dc)` extension the data model fixed (§5): the prototype returned only `(i, g)`; circuitRF adds **charge and capacitance** so reactive nonlinearity (Cgs/Cgd, the γ input-shaping effect of Hero-adjacent work) is balanced correctly, and so the v2 ASM-HEMT charge path slots in unchanged (PRD §6.1). The derivatives come from the three-tier scheme in `expressions.md` §12: closed-form for built-ins, forward-mode AD for the SDD (differentiating the active `if` branch), finite-difference as a per-model fallback.

`I_Qnonlinear` and its Jacobian contribution are simply **omitted for a purely resistive nonlinearity** (`q ≡ 0`), recovering the IV-only case the deck uses for the Hero-class loadline/loadpull comparisons.

---

## 5. Time/frequency transform — conventions and the evaluation grid

The transform conventions are **frozen** (recorded in `src/Engine/CLAUDE.md`, the same discipline the linear note applies to sign conventions) so they can never be silently changed under the engine.

### 5.1 Amplitude convention (single-tone, the deck's)
Real, periodic time-domain waveforms; store **DC + positive harmonics only**, with negative frequencies reconstructed as conjugates (no information lost between FFT and IFFT). Following the deck's `get_fft`/`get_ifft`:

- forward: `X = FFT(x)/(N/2)` over the positive half, then **halve the DC bin**;
- inverse: undo the scaling (double DC), append the conjugate mirror, IFFT real.

So `V_{n,0}` is real (DC), and `V_{n,k}` for `k ≥ 1` is the complex phasor of the k-th harmonic at node n. This is the convention every `V`/`I` cube is stored in, and the one measurements assume (`harm(x,k)`, `Pdc` from k=0).

### 5.2 The evaluation grid is not the solution spectrum
Two spectra share one FFT and must be kept distinct:

- **Solution spectrum** — the harmonics actually solved for: `0 … K` (`MaxHarmonic`, single-tone) or the retained mixing set (`MaxMixingOrder`, multi-tone, §6). This sizes the Newton unknowns and the Jacobian. It is a *physics* choice — "how many harmonics participate in the balance."
- **Evaluation grid** — the time/frequency sample count used to evaluate the device and FFT its response. This is a *numerical-accuracy* choice; its job is anti-aliasing.

These are **orthogonal**. The grid floor is set by the Jacobian, not the solution: assembling the full conversion matrix needs `G` and `C` up to harmonic **2K** (the `G_{k+i}` term at `k = i = K`, §7), so the FFT must resolve at least `2K` positive harmonics — i.e. roughly `N ≥ nextpow2(4K)` real samples even before any oversampling. (Rounding up to a base-2 length keeps the FFT optimal.)

### 5.3 `FFTOverSample` — a separate knob from harmonic order
The device generates harmonics far above the retained set; with too few samples they alias down onto `0 … 2K` and corrupt `I_nonlinear` and `G`/`C`. **`FFTOverSample`** (integer `1, 2, 4, 8, …`) multiplies the grid length to push Nyquist up and suppress that aliasing:

```
N = FFTOverSample · nextpow2(4K)            // single-tone; per-dimension for multi-tone (§6)
```

**`FFTOverSample` does not change the Newton solve size.** This is the decided convention, and it is the correct one: promoting the extra bins to unknowns would be a silent re-raising of `K`, for which `MaxHarmonic` is the honest knob. At fixed `K`, oversampling makes the retained components `I_0 … I_{2K}` (and thus the converged `F = 0`) a truer rendering of the K-truncated solution — same physics, cleaner arithmetic. The prior tool's convention (solve size tracks harmonic order; higher FFT bins ignored for the *unknowns*) is therefore carried forward.

One sub-choice is pure convergence tuning and is exposed as a flag rather than fixed: **whether the fixed-size Jacobian's `G`/`C` entries are built from the oversampled (less-aliased) transform or from the minimal grid.** Since `J` only sets Newton's *path*, never the fixed point, this changes iteration count, not the answer. **Default: use the oversampled `G`/`C`** (the FFT is already paid for; take the better Jacobian), behind a flag to flip and measure on the heroes once the engine runs.

---

## 6. Multi-tone — diamond-truncated solution

*Two tones use the multidimensional FFT described in §6.1–§6.3. Three or more use the APFT of §6.4–§6.6, which is where the as-built n-tone engine is specified.*

A multi-tone excitation lifts to a function on a multidimensional torus that is **exactly periodic in each tone's phase**, and that is what the multidimensional FFT samples — so the transform is exact, with no windowing. Whether the *physical* time waveform `x(t)` is itself exactly periodic depends on the tones: it is exactly periodic precisely when they are **commensurate** (rational frequency ratio, hence a common period at their `gcd`, with every mixing product landing on that `gcd`'s harmonic grid), and only **almost**-periodic — never exactly repeating — when they are incommensurate. The Hero-5 tones are commensurate: `f₁ = 1.995`, `f₂ = 2.005 GHz` have ratio `399/401` and a `gcd` of 5 MHz, so `x(t)` repeats exactly every 200 ns; a deliberate two-tone test is normally set up this way. The method does not rely on commensurability either way, because each tone gets **its own phase axis** — `v(φ₁, φ₂)` is exactly `2π`-periodic per axis by construction (the physical signal is the diagonal cut `φ_t = ω_t·t`), so a rectangular grid samples it exactly regardless. That independence from commensurability is exactly the situation the historical **almost-periodic Fourier transform (APFT)** was built for; the per-phase-axis multidimensional FFT delivers the same generality without the APFT's nonuniform-sampling transform matrix, with no windowing and no commensurate-frequency requirement. *At two tones that trade is worth taking, and it is what circuitRF does. Past two it is not — the rectangular grid is exponential in the tone count while the APFT's sample set is not — so `T ≥ 3` uses the APFT after all (§6.4).*

### 6.1 The transform
- Sample on a rectangular `N₁ × N₂` grid — one period of tone-1's phase along axis 1, one period of tone-2's phase along axis 2 (`v(t) = v(φ₁, φ₂)`, `φ_t = ω_t · t`).
- Take a **multidimensional real FFT**. The spectrum comes out indexed by the **tone pair `(k₁, k₂)`** at physical frequency `k₁f₁ + k₂f₂`.
- The single-tone conjugate symmetry generalizes to a **half-plane**: `(−k₁, −k₂)` is the conjugate of `(k₁, k₂)`, so one half-plane (plus the `(0,0)` DC bin) is stored; the rest is reconstructed. Same "no loss of information" property as single-tone.

`N_t` per dimension is sized exactly as single-tone: `FFTOverSample · nextpow2(4·order_t)`, where `order_t` is that tone's per-axis reach (set so the diamond below fits). **This section is the TWO-TONE path and stays that way**: an `N₁ × … × N_T` grid is the obvious generalization and it does not reach the tone counts the engine must support — see §6.4 for the arithmetic that rules it out, and for what replaced it.

### 6.2 Rectangular grid, diamond solution set
The multidimensional FFT is inherently **rectangular** — that is what a multi-D FFT computes. The **retained solution set** (the Newton unknowns) need **not** be the full rectangle. It is a **diamond**:

```
retain (k₁, k₂)  iff  |k₁| + |k₂| ≤ MaxMixingOrder         // the half-plane representatives thereof
```

This is exactly what the PRD's "mixing order ≥ 5" wants: it keeps every low-order product that carries energy and discards the high-high corner bins that do not. The corner bins are still *computed* by the rectangular FFT (so they participate in anti-aliasing, like the `>K` bins single-tone); they simply do not become unknowns. This maps onto the analysis fields already in the data model: single-tone uses `MaxHarmonic` (= K, a 1-D line `0…K`); two-tone uses `MaxMixingOrder` (the diamond). The retained set's size — call it `M` — replaces `(K+1)` in every dimension formula below.

### 6.3 Index map and the `mixIndex` axis
The retained diamond's half-plane representatives are enumerated in a **fixed, documented order** to a linear index `mixIndex = 0 … M−1`, with `(0,0)` at index 0. That linear index is the `mixIndex` axis of the two-tone `V`/`I` cubes (data-model §7), and it is the same enumeration the measurement library's `tone(x, k₁, k₂)` / `IMn(...)` inverts (measurements §3.4).

The axis carries the product **frequency** as its VALUE (signed, in Hz) and the product **tag** as its LABEL. At two tones the tag is `"(k₁,k₂)"`; at `T` tones it is `"(k₁,…,k_T)"` (§6.5). Nothing downstream parses the tag — the data display renders it verbatim and the measurement language matches on it as a string — which is why widening it from two entries to `T` needed no change in either. The Hero-5 products land as:

| Product | `(k₁, k₂)` | Frequency (f₁=1.995, f₂=2.005 GHz) |
|---|---|---|
| carriers | (1,0) / (0,1) | 1.995 / 2.005 GHz |
| IM2 (baseband) | (1,−1) | 0.010 GHz |
| IM3 | (2,−1) / (−1,2) | 1.985 / 2.015 GHz |
| IM5 | (3,−2) / (−2,3) | 1.975 / 2.025 GHz |

Retaining the baseband `(1,−1)` and the close-in `(3,−2)` is what `MaxMixingOrder ≥ 5` buys, and is directly relevant to the source/load baseband-termination effects the tool targets (PRD §5).

### 6.4 True n-tone (≥ 3 tones) — AS BUILT, and why it is not a bigger FFT

This section used to record n-tone as **future development**, and to propose reaching it by lifting the two-tone FFT to a separable `N₁×…×N_T` real FFT. **The first half is done; the second half was measured and rejected.** For `T ≥ 3` circuitRF uses an **almost-periodic Fourier transform (APFT)** instead — the very transform §6's opening paragraph describes the multidimensional FFT as an alternative to. §6.5 specifies it.

**Why the rectangular grid does not reach the required tone count.** The per-axis rule `N_t = FFTOverSample · nextpow2(4·order)` makes the grid `nextpow2(4·order)^T` samples, which is exponential in the tone count:

| tones `T` | order 3 grid | samples | one Newton iteration's arrays |
|---|---|---|---|
| 2 | 16 × 16 | 256 | negligible |
| 4 | 16⁴ | 65,536 | ~7 MB |
| 5 | 16⁵ | 1,048,576 | ~117 MB |
| 6 | 16⁶ | **16,777,216** | **~1.9 GB** |

(One iteration holds roughly 14 such arrays: `v`, `i`, `q` per interface node plus `dg`, `dc` per node pair.) The rectangle is also nearly all waste at high `T` — it computes a full box in order to retain a diamond, and the ratio of box to diamond grows with every tone. The APFT's sample count scales with the **retained set** `M` instead: the same 6-tone order-3 case needs **1,512 samples**, not 16.7 million.

**What that costs, measured.** On the Hero-5 GaN PA (`testdata/Hero5/hero5_6tone.cnl`), single drive point: 6 tones at order 2 (43 products) converges in **0.4 s** end-to-end through the CLI; 6 tones at order 3 (189 products, 756 unknowns) in **4.3 s**. Both dense-Jacobian, no iterative solver.

**The iterative solver this was paired with was not needed and was not built.** §16 item 5 and the old §6.4 both assumed n-tone would have to land together with a block-structured/iterative HB solve, because "the retained set grows steeply with tone count". It does — but the product ceiling (§6.5) bounds it at a size the dense solve handles comfortably, so the two pieces of work are independent after all. §16 item 5 stays deferred on its own merits.

### 6.5 The T-tone lattice and the APFT (as built)

Everything in §6.1–§6.3 holds with `(k₁,k₂)` replaced by the vector `k = (k₁ … k_T)`; the transform and the Jacobian's inner form are what change.

**Retained set — the diamond, generalized.** `k` is retained iff `Σ_t |k_t| ≤ MaxMixOrder` and `k` is a half-**space** representative: `k = 0`, or its FIRST NONZERO component is positive. Exactly one of `{k, −k}` satisfies that, so the retained set carries the full information for a real signal, the other being the conjugate. This is a **single knob** — the existing `MaxMixOrder` — with no per-tone order caps: at 5–6 tones the total order is what binds anyway, so per-tone caps would add a column to the dialog, the `.cnl` and the lattice while changing little.

**Enumeration order — LOCKED, never renumber.** Ascending total order `m = Σ_t |k_t|` (so DC is index 0), then **lexicographic descending** within the half-space. Raising `MaxMixOrder` therefore only APPENDS indices, so a cube's `mixIndex` axis is stable across an order change and existing plots and measurements keep referring to the same products.

*At `T = 2` this rule reproduces §16 item 1's locked two-tone order element for element* — "k₁ descending, then k₂ descending within the upper half-plane" IS lexicographic-descending under the half-space rule. `MixingLatticeTests` pins that equivalence, which is what lets one class serve every tone count without renumbering anything that already exists. Production still dispatches `T = 2` to the frozen `MixingGrid`/`HbFft2D`/`HbNewton2D` path; the two-tone goldens and the data display's two-tone spectrum are deliberately untouched.

**Size in closed form.** The number of integer points with `Σ|k| ≤ O` in `Z^T` is `L = Σ_j 2^j·C(T,j)·C(O,j)`, so `M = (L+1)/2`. `MixingLattice.CountFor` evaluates this without enumerating anything, which is what makes the ceiling free to check.

| tones ↓ / order → | 2 | 3 | 4 | 5 |
|---|---|---|---|---|
| 2 | 7 | 13 | 21 | 31 |
| 3 | 13 | 32 | 63 | 116 |
| 4 | 21 | 65 | 161 | 341 |
| 6 | 43 | **189** | 645 | 1827 |

**The transform.** With `D = 2M` real DOF (layout `2·mixIdx + isIm`, matching the two-tone path, the DC quadrature DOF kept as a fictitious dummy so Maas §7.3's special cases carry over verbatim) and `S ≈ 2D` sample phases `φ_s ∈ [0,2π)^T`:

- **Synthesis** `Γ` (`S×D`, real): `Γ[s, 2m] = cos(k_m·φ_s)`, `Γ[s, 2m+1] = −sin(k_m·φ_s)`. This is §5.1's amplitude convention written as a matrix, so `v(φ) = V_HB[0] + Σ_{k≠0} Re{V_HB[k]·e^{j k·φ}}` exactly as at one and two tones. A pure `cos φ₁` reads 1; a product `cos φ₁·cos φ₂·cos φ₃` reads **0.25** at `(1,1,1)` — the continuation of the 2-D rule that `cos φ₁·cos φ₂` reads 0.5 at `(1,1)`. **The DC halving is GLOBAL** (once, at `k = 0`), never per axis.
- **Analysis** `A = Γ⁺ = (ΓᵀΓ)⁻¹Γᵀ`, factored ONCE per lattice and cached. On the retained lattice `A·Γ = I`, so synthesize→analyze is an exact round trip; out-of-band content is least-squares projected rather than sharply aliased.
- **Sample phases** come from the deterministic `R_T` Kronecker (Weyl) low-discrepancy sequence, `φ_s[t] = 2π·frac(0.5 + (s+1)·g^-(t+1))` with `g` the positive root of `x^(T+1) = x + 1`. No RNG, so a run is bit-reproducible. Equidistribution makes `ΓᵀΓ` near-diagonal, but **correctness does not rest on that choice**: the constructor gates on the measured conditioning of `ΓᵀΓ` and throws rather than returning a silently rank-deficient transform.

**The Jacobian — a triple product, not a convolution.** The residual's nonlinear term is literally `i_nl = A·i(Γ·V)`, so by the chain rule its exact derivative is

```
J_block(n,m) = A·diag(dg)·Γ  +  R(ω_row)·[ A·diag(dc)·Γ ]
```

where `R(ω_row)` is the per-row real form of multiplying by `jω` (row `2k` takes `−ω_k ×` row `2k+1`; row `2k+1` takes `+ω_k ×` row `2k`) — the same rotation §7.2/§7.4 applies to the two-tone 2×2 charge block. `Y_NN` on the mix diagonal, the guard-order cutoff and the Maas §7.3 DC row/column special cases then follow unchanged.

This is the exact derivative of what is actually computed, not an approximation of a convolution, and **it needs no spectrum of the derivative waveform at difference and sum indices at all**. That is the second reason the rectangular route is unnecessary: the `4·order` per-axis rule existed precisely to give the Jacobian its `2·MaxMixOrder` reach (§5.2), and there is nothing left for it to reach.

`HbNewtonNd.CompareJacobianNumericalNd` (central differences of `BuildFNd`) is the oracle, exactly as `CompareJacobianNumerical2D` is for two tones.

**Equivalence with the frozen two-tone path.** The APFT/triple-product formulation is exercised at `T = 2` against `HbNewton2D` on an identical problem (`HbNewtonNdVs2DTests`). They agree to solver accuracy on DC and the carriers, and they **converge to each other as the diamond grows** — the IM3 (2,−1) disagreement runs 5.3e-3 → 2.9e-5 → 3.1e-7 at `MaxMixOrder` 3 → 4 → 5. The residual difference is truncation, and it behaves like truncation: the product sitting ON the diamond edge is the one most exposed to what was discarded, and each formulation discards it differently (the FFT aliases by periodic wrap, the APFT least-squares-projects). Asserting that trend is the gate, not a tolerance.

**Degenerate frequencies are fine and are exercised.** With equally spaced tones — the ordinary multi-carrier stimulus — distinct products land on the same physical frequency: at 1.99/2.00/2.01 GHz, both `(1,-1,0)` and `(0,1,-1)` sit at −10 MHz. They stay INDEPENDENT unknowns, because each tone owns its own phase axis and the torus basis functions are orthogonal regardless of what the frequencies do. This is the property the multidimensional formulation buys (§6's opening paragraph) and `hero5_3tone.cnl` is the fixture that exercises it. The spectrum plot shows both stems at the same x; they are not summed, because each is a separate lattice unknown.

### 6.6 The multi-tone ceiling

`T ≥ 3` solves a DENSE Jacobian of size `2·N·M`, and `M` grows steeply with tone count, so the engine enforces two caps and **refuses at SETUP time — before any extraction or Newton solve**:

- `AnalysisSettings.HbMaxTones` (default **6**) — the declared tone count.
- `AnalysisSettings.HbMaxMixProducts` (default **600**) — retained products `M`, the constraint that actually binds.

600 admits every configuration that is practical on a dense solve (6 tones @ order 3 = 189, 4 @ 4 = 161, 3 @ 9 = 580) and excludes the ones that are not. **The refusal names the knob that binds and a value that works**, because "too large" alone leaves the user guessing which of tone count and mix order to move:

> `HB: 6 tones at MaxMixOrder=5 retains 1,827 mixing products (cap 600, ≈3,654 dense unknowns per interface node). Lower MaxMixOrder to 3 (189 products), or reduce the tone count.`

Refusing at setup time is the point: building the lattice and its APFT transform for 1,827 products would allocate hundreds of MB and factor a 3,654² normal matrix before failing. The Analysis Setup dialog shows the same product count live beside `Max mix order`, so the ceiling is visible while authoring rather than arriving as an error at Run.


---

## 7. The Jacobian — the conversion matrix, real-valued

Newton's method (deck slides 14–18) picks the next guess by the multidimensional update

```
V_{n+1} = V_n − J⁻¹ · F(V_n)            J = dF/dV
```

Differentiating `F` (§2) gives the three-term Jacobian (deck slides 18, 22):

```
J = Y_{N×N}  +  ∂I_nonlinear/∂V  +  ω · ∂Q_nonlinear/∂V
```

- `Y_{N×N}` — the linear interface admittance (§3). **Constant** across iterations; "easy" to compute once per harmonic.
- the nonlinear terms — rebuilt each iteration from `G = FFT{dg}` and `C = FFT{dc}`, the **rate of change of each harmonic of nonlinear current/charge at each node into the nonlinear subnetwork** (deck slide 18). This is the part the model must supply every Newton step.

### 7.1 Real-valued representation (decided)
The Newton solve is carried in the **real-valued split** form. Each node-harmonic unknown `V_{n,k}` contributes its real and imaginary parts as two real unknowns, so the system is **`2·N_nl·(K+1) × 2·N_nl·(K+1)`** (single-tone; replace `(K+1)` with the diamond size `M` for multi-tone), and `J` is entirely real (deck slide 25). The **stored** `V`/`I` cubes remain `System.Numerics.Complex` — the `CLAUDE.md` "all HB quantities are Complex" invariant is read as governing the stored spectra and every other AC/HB quantity; only the *internal Newton representation* is the real split. This is the reconciliation agreed in design review.

**Why real-split rather than a plain complex matrix.** `F(V)` acts on a real time-domain signal, so it is real-differentiable but **not complex-analytic** in `V`: a perturbation of `V_{m,i}` couples into `I_{n,k}` through *both* the difference-frequency component `G_{k−i}` **and** the sum-frequency component `G_{k+i}` of the time-varying conductance. A naïve complex `N_nl·(K+1)` matrix carrying one complex entry per coupling would keep only the `G_{k−i}`, Cauchy-Riemann `[[a,−b],[b,a]]` (holomorphic) part and **silently drop** the `G_{k+i}` (anti-holomorphic, `∂/∂V*`) part — i.e. it would be the wrong Jacobian and forfeit quadratic convergence. The real 2×2 block is precisely what carries both couplings. (An equivalent honest complex form is the augmented `[V; V*]` system; the real split is the simpler bookkeeping and matches the deck pseudocode, so it is what we adopt.)

### 7.2 The blocks
Each `(n,k)–(m,i)` coupling is a real **2×2 sub-matrix** (`n, m` nonlinear-node indices; `k, i` harmonic indices `0 … K`), assembled from three contributions (deck slide 25). With `G_{·,n,m}` and `C_{·,n,m}` the FFT'd conductance/capacitance between nodes `n, m`:

```
∂I_{n,k}/∂V_{m,i} =
  [ Re{G_{k−i}} + Re{G_{k+i}}    −Im{G_{k−i}} + Im{G_{k+i}} ]
  [ Im{G_{k−i}} + Im{G_{k+i}}     Re{G_{k−i}} − Re{G_{k+i}} ]

ω·∂Q_{n,k}/∂V_{m,i} =
  [  0     −kω₀ ] · [ Re{C_{k−i}} + Re{C_{k+i}}    −Im{C_{k−i}} + Im{C_{k+i}} ]
  [ kω₀     0  ]    [ Im{C_{k−i}} + Im{C_{k+i}}     Re{C_{k−i}} − Re{C_{k+i}} ]

Y_{N×N}|_{n,m,k} (diagonal in harmonic, k=i only) =
  [ Re{Y_{n,m}(kω₀)}   −Im{Y_{n,m}(kω₀)} ]
  [ Im{Y_{n,m}(kω₀)}    Re{Y_{n,m}(kω₀)} ]
```

The `G_{k+i}`/`C_{k+i}` reach to index `2K` (at `k = i = K`) — this is the §5.2 grid floor. The full (2K-resolved) conversion matrix is built, not the harmonic-order-truncated approximation: an approximate `J` only alters the convergence path, but given the Hero-2/4 "every point" and Hero-3 "≥95%" bars we keep the true Jacobian to preserve the quadratic convergence the deck advertises.

### 7.3 DC and the reality constraints (Maas)
The DC harmonic carries no imaginary degree of freedom (the signal is real), so the `i = 0` **column** has no imaginary part — its `[·,2]` entries are zeroed — and at `k = i = 0` the block's `[2,2]` is set equal to `[1,1]` (Maas, 2003, p. 145). These special-cases are the deck's slide-26 pseudocode and are carried verbatim into the stamping routine; they are what make the real-split system non-singular and consistent at DC.

### 7.4 Stamping
The Jacobian is assembled by the deck's slide-26 loop, generalized to add the `C` (charge) and `Y_{N×N}` (linear) contributions the slide omitted: for each `(k, i, n, m)`, compute the three 2×2 contributions, sum them, and stamp into the block at the row/col the index map assigns to `(n,k)` and `(m,i)`. For multi-tone the `(k)`/`(i)` scalar harmonic indices become the diamond's `mixIndex`, and the `k−i`/`k+i` difference/sum frequencies become **tone-pair vector** differences/sums `(k₁−i₁, k₂−i₂)` / `(k₁+i₁, k₂+i₂)`, looked up in the rectangular FFT spectrum (which is why the corner bins must be computed even though they are not unknowns).

---

## 8. Solving the Newton update — dense

`J` is `2·N_nl·(K+1)` square (single-tone). **`N_nl`, the number of nonlinear-facing nodes, is far smaller than the full node count** — a single-FET PA has a handful, Hero 4's two FETs still only a small block. So the HB Newton system is solved **dense** (NumFlat LU), distinct from the sparse CSparse path the *linear* MNA uses for the full netlist. Per iteration: factor `J`, solve `J · ΔV = −F`, update `V ← V + λ·ΔV` (with optional damping `λ ≤ 1`, §11). Block-structured or iterative HB solves (exploiting the harmonic/node block pattern) are a noted **future optimization** if the `< 3 s`/point NFR (PRD §14) is missed at Hero-4 scale; v1 is dense. **Multi-tone does not change that**, contrary to the pairing §16 item 7 originally assumed: at `T` tones the system is `2·N_nl·M` for retained-product count `M`, and §6.6 caps `M` at 600 precisely so the dense solve stays adequate — 6 tones at order 3 (756 unknowns) converges in 4.3 s.

This keeps the two linear-algebra regimes clean: **sparse** for the large frequency-domain MNA (10k components), **dense** for the small real HB Jacobian.

---

## 9. Recovering the full internal solution

The Newton solve yields only the **interface** voltage spectrum `V`. Measurements need voltages and currents **everywhere referenced**, plus all branch currents including the DC-source currents for `Pdc` (measurements §5). After convergence, HB performs **one linear back-substitution per harmonic**: drive the linear-partition MNA with the converged interface `V` and the independent sources, and solve for all interior node voltages and branch currents (reusing the per-harmonic factorization already computed for the interface extraction, §3). The k = 0 solve returns the DC-source currents directly.

The result is the full `V`/`I` spectra written to the run's cubes — the engine retains them per node/terminal, per harmonic (k = 0 included), per sweep point (the data-model §7 / measurements §5 retention requirement). The prune-to-referenced-nodes optimization is deferred (measurements §5), aware that measurement paths reach deep.

---

## 10. Initial guess and the DC seed

A good first guess is decisive (deck slides 13, 16 — Newton converges fast from a good start and can be trapped from a bad one). The strategy:

1. **DC operating point first.** Solve the **nonlinear DC** problem at the interface — the k = 0 balance alone — using the **Phase-3 nonlinear-DC solver** (`nonlinear-dc.md`): Newton on the `Evaluate` contract with `gmin` continuity and **`DcBiasStepping`** (the bias-supply ramp, default `IfNecessary`). This is already built and validated (Phase 3's hero converged to vds ≈ 47.0 V); **Phase 4 calls it, it does not re-implement it.** This is the bias point the deck initializes from (`Vds[0]=48 V`, `Vgs[0]=−3.05 V` in slide 28).
2. **Seed the harmonics small.** Set the fundamental and higher harmonics to a small perturbation (the deck's `1e-3`), not zero — a pure-DC guess gives the Jacobian no harmonic signal to work from. Slide 28 shows this `1e-3` seed converging to `< 1e-10` total error in ~7 iterations at K = 5.
3. **Or continue from a neighbor.** Under a sweep (power, Γ), the converged `V` of the previous point is a far better seed than the cold DC start — see §11.

The nonlinear-DC solver is **owned by Phase 3** (`nonlinear-dc.md`) — it is the Newton-on-`Evaluate` problem with the same `(i, q→0 at DC, dg)` contract and `gmin` continuity the linear DC formulation underlies. The linear note owns the *linear* DC; Phase 3 owns the *nonlinear* DC; **this note (Phase 4) consumes the nonlinear-DC solve as its k = 0 seed.** (Earlier drafts, written when HB was Phase 3, described building the nonlinear DC here — that work is done; Phase 4 only calls it.)

---

## 11. Continuation — making convergence robust

HB does not converge from an arbitrary start at full drive; continuation walks a parameter from an easy regime to the target, reusing each solution as the next guess (deck slide 27, "Continuation Methods / source stepping"):

> **Setting (reserved for Phase-4 bring-up): `DriveStepping`** `{ IfNecessary, Always, Never }`, default `IfNecessary` — the tri-state knob governing HB's RF-**drive** continuation, the analog of nonlinear DC's `DcBiasStepping` (which ramps the bias *supplies*). The two are deliberately **separate settings** because they ramp different continuation parameters: `DcBiasStepping` walks the **DC supplies** up to bias the device (Phase 3); `DriveStepping` walks the **RF input drive power** up into compression (this section). `IfNecessary` here means: try the cheap start first — a **warm-start from the previous sweep point** (the common case across a power/Γ sweep), or the DC seed for the first point — and fall back to power-ramping only if that fails. `Always` ramps drive from small-signal every point; `Never` attempts the warm/seed start only and reports non-convergence on failure. The step-count and backoff below are its companions. (Named and reserved here; built in Phase 4 within this continuation framework, not in Phase 3.)

- **Power (source) stepping** (Heroes 2, 4). Start small-signal (near-linear, easy), step `Pin` up into ≥ 3 dB compression, each step seeded by the last converged `V`. Required by the Hero-2/4 "converges at *every* point" bar. This is the `Always`/fallback path of `DriveStepping`.
- **Previous-point continuation** (Hero 3 loadpull **and the generic HB parametric sweep** — see §11.1). Across the Γ grid (or any swept axis), seed each point from the previous converged solution; this is what makes the "≥ 95% of ≥100 points converge" target reachable and cuts the per-point Newton cost.
- **Step backoff on failure.** If Newton hits the max-iteration cap without meeting tolerance (§12), **halve the continuation step** and retry from the last good solution; on repeated failure, report non-convergence at that point with the residual and the last step. (Damping `λ < 1` on the Newton update is the within-step companion knob.)

### 11.1 Parametric-sweep warm-start (as built)

Previous-point continuation was first implemented only inside the **loadpull engine** (which calls `HbEngine.RunSinglePoint(p, warmStart)`, reusing the converged spectrum across the Γ-grid and Pin sweep). The **generic `ParametricSweepEngine`** — the path a plain `type=hb` analysis wrapped in a `type=parametric_sweep` takes — did *not*: it called `HbEngine.Run`, which **cold-started every point** with a full nonlinear-DC operating-point solve plus a near-zero harmonic guess. So a Pin sweep ran an `ExtractDC` + a `NonlinearDcEngine.Run` at *every* power step, discarding the previous (and very nearby) converged solution.

It now warm-starts:

- **`HbEngine.Run(p, warmStart)`** accepts an optional interface-voltage seed `[N, K+1]`. When supplied (and dimensionally matching the current topology), it is used as the Newton initial guess and the **per-point DC seed solve is skipped entirely**; otherwise the DC seed is computed as before. `HbRunResult` exposes the converged interface spectrum (`InterfaceV`) and the `Converged` flag.
- **`ParametricSweepEngine`** threads the previous point's converged `InterfaceV` into the next point's `Run`. The seed **chains only along the innermost sweep axis** — the one whose inner analysis *is* the HB. A nested (outer) sweep's per-point `RunInner` returns a null seed, so each outer-axis step runs a fresh inner sweep whose first point is DC-seeded and which then chains internally. The chain also **resets on any non-converged point** (a bad solution is never propagated) and **falls back to a cold seed** if the interface dimensions change.
- Gated by **`AnalysisSettings.HbSweepWarmStart`** (default **on**). Set false to force a cold DC seed at every point (e.g. to study branch-dependence near a bifurcation). Two-tone HB sweeps are unchanged (they cold-start; `RunTwoTone` does not take a seed).

**Why (benchmark).** On the GaN-PA Pin sweep (the single-FET PA, `Pavl` 0→20 dBm, 11 points), warm-start vs. cold:

| | Newton iterations (Σ) | nonlinear-DC solves | converged result |
|---|---|---|---|
| **Cold** (DC seed each point) | 22 | 11 (one per point) | — |
| **Warm** (previous-point seed) | **12** (≈ 45 % fewer) | **1** (whole sweep) | **bit-identical interface spectrum** |

Warm-start *follows the solution branch* (the physically-correct continuation for a power sweep), so the result is unchanged — it agrees to convergence tolerance (the full V cube, which includes volt-scale back-solved nodes, differs by only ~1e-5 V ≈ 1e-6 relative; the interface unknowns are identical). Gate/benchmark: `HbPinSweepWarmStartBenchTests` (Newton-iteration + DC-solve counts, plus a production-path warm-vs-cold equivalence test through `ParametricSweepEngine.Run`).

The IV/QV models must remain **defined beyond the solution domain** — intermediate Newton iterates overshoot (deck slide 29: iteration #2's Vds swing exceeds the final solution), so a model that returns garbage outside its fitted range breaks convergence even when the final answer is in-range. This is a stated requirement on nonlinear models, echoed from the deck: smooth, continuously-defined, **extended-domain** I/V and Q/V. Where an SDD's expression nonetheless hits a domain error (`log`/`sqrt` of a non-positive argument, etc.) on an overshooting iterate, the evaluator **clamps the offending operation and emits an obvious user-facing warning** rather than hard-failing the solve (resolving `expressions.md` §18 open item 2) — a hard error would abort a run that would otherwise converge once the iterate returns in-domain; the warning names the model and the operation so the user sees it, rather than burying it in a log.

---

## 12. Guard harmonic and the convergence criterion

### 12.1 Guard harmonic (v1 knob)
The **guard harmonic** attenuates the higher-frequency components of the Jacobian's `G`/`C` entries (deck slide 27), damping the Newton step's response in the stiff high harmonics. It is applied to **`J` only, never to `F`**, so it changes the convergence path but not the fixed point. The owner reports it **necessary** for convergence with Class F / F⁻¹ terminations (where high-harmonic short/open loading makes those harmonics stiff). It ships as a **first-class v1 knob** (an attenuation profile over harmonic index), not a hidden internal. The **default profile is a hard cutoff** (zero the Jacobian's `G`/`C` contributions above a guard index); a **tapered** profile (smooth roll-off) is selectable for heuristic tuning once the engine runs.

### 12.2 Convergence test
Balance is declared when the residual norm is below tolerance. **Default: absolute** — `‖F(V)‖ < ε_abs` with `ε_abs = 1e-6` (deck slide 13: "an error < 1e-6 is needed to get reasonable results for all harmonics"). User-selectable alternatives:

- **Relative** — `‖F(V)‖ < ε_rel · ‖reference‖`, normalized to a running scale (e.g. the linear current magnitude), for circuits whose natural current level is far from unity.
- **Normalize-to-drive** — scale the residual by the drive level, so the criterion tracks the source-stepping sweep.

A **max-iteration cap** bounds each Newton solve; exceeding it triggers the §11 continuation backoff. The norm (default L2 over the real-split residual), `ε`, and the cap are advanced settings with documented defaults.

---

## 13. The algorithm, end to end (single-tone; multi-tone substitutes the diamond/mix-index)

```
SETUP (per sweep point's topology)
  partition → nonlinear-facing nodes (data-model §3)
  per harmonic k = 0 … 2K:  extract Y_{N×N}(kω₀)        (linear-engine §10)
  compute source excitation  Y_s · V_s at interface      (constant; §3)

INITIAL GUESS
  solve nonlinear DC at interface (Newton + gmin + source-step)   → V[k=0]   (§10)
  seed V[k≥1] = 1e-3                                              (or previous continuation point)

NEWTON LOOP  (until ‖F‖ < ε or max-iter → backoff §11)
  v(t)        = IFFT(V)                       on grid N = FFTOverSample·nextpow2(4K)   (§5)
  (i,q,dg,dc) = Evaluate(v(t))                per nonlinear device                    (§4)
  I_nl  = FFT(i);  I_Qnl = jω·FFT(q)
  G = FFT(dg);  C = FFT(dc)                    resolved to harmonic 2K
  F   = Y_s·V_s + Y_{N×N}·V + I_nl + I_Qnl
  J   = Y_{N×N} + ∂I_nl/∂V + ω·∂Q_nl/∂V        real 2×2 blocks (§7); apply guard harmonic
  ΔV  = solve(J, −F)                           dense LU (§8)
  V  += λ·ΔV

RECOVER + STORE
  back-substitute converged V + sources through linear partition → full V, I (all k incl. 0)  (§9)
  write V, I cubes for this sweep point        (measurements read Pout/Pdc/PAE/IMn afterward)

CONTINUATION
  advance Pin / Γ; reuse converged V as next seed                (§11)
```

---

## 14. Validation

The HB engine is validated against owner-generated references from other simulators using the **identical SDD FET** transcribed into both tools (PRD §4), so the comparison tests circuitRF's HB math, not a different transistor:

- **Hero 2 / 4** — Pout, gain within ±0.01 dB; DE, PAE within ±0.1 pp absolute; convergence at every power-sweep point into ≥ 3 dB compression at H = 7.
- **Hero 3** — Pout/PAE loadpull contours within Hero-2 tolerances; ≥ 95% of ≥100 Γ points converge with previous-point continuation.
- **Hero 5** — IM3 within ±0.5 dBc, IM2/IM4/IM5 within ±1.0 dBc (or as a self-consistency target if IM data is not exportable), at `MaxMixingOrder ≥ 5`.

Beyond the cross-tool references, the deck establishes a **theory cross-check** the engine should reproduce: HB loadpull `R_opt`, Pout, and efficiency agree with **Cripps' loadline method** and with Class B / J / F / F⁻¹ design theory to within ~1–1.2 VSWR (the deck's Class-B exact match through Class-F⁻¹ at 1.04 VSWR). These make good engine-level sanity tests independent of any reference tool, on the IV-only nonlinearity.

---

## 15. Summary of decisions

- **Error function** `F(V) = Y_s·V_s + Y_{N×N}·V + I_nonlinear + I_Qnonlinear ≈ 0`; unknowns are the interface node-voltage harmonics; balanced when linear and nonlinear currents cancel at every harmonic (incl. DC) at every nonlinear-facing node.
- **Linear side** supplied by the linear engine (§3): interface `Y_{N×N}(kω₀)` for `k = 0…2K` (constant across iterations) **and** the Norton source excitation `Y_s·V_s` (computed once, since `V_s` is fixed).
- **Nonlinear side** via the time-domain `(i, q, dg, dc)` contract: IFFT → `Evaluate` → FFT; `I_Qnonlinear = jω·Q`; derivatives from closed-form / AD / FD (`expressions.md` §12).
- **FFT convention** frozen (DC + positive, DC halved, `2/N` scale, conjugate reconstruction), recorded in `src/Engine/CLAUDE.md`.
- **Evaluation grid ≠ solution spectrum.** Grid floor resolves to harmonic **2K** (Jacobian sum term). **`FFTOverSample`** `(1,2,4,8…)` enlarges the grid for anti-aliasing and **does not** grow the Newton solve. Oversampled `G`/`C` feed the fixed-size Jacobian by default, behind an experiment flag.
- **Two-tone** via multidimensional real FFT on a rectangular `N₁×N₂` grid (real, periodic, exact — no APFT, no windowing), tone-pair `(k₁,k₂)` indexing, **diamond** retained set `|k₁|+|k₂| ≤ MaxMixingOrder` over the rectangular FFT, half-plane conjugate symmetry; linear `mixIndex` axis matches the measurement library.
- **Three to six tones** (§6.4–§6.6) via the **APFT** instead — the rectangular grid is `nextpow2(4·order)^T` samples and does not reach six tones (1.9 GB per iteration at order 3). Same diamond `Σ_t|k_t| ≤ MaxMixOrder`, same half-space rule, same `mixIndex` axis with the tag widened to `(k₁,…,k_T)`; the Jacobian becomes the exact triple product `A·diag(dg)·Γ`, which removes the need for the `4·order` grid entirely. Retained products capped at 600, refused at setup time. The two-tone path is untouched.
- **Jacobian** `J = Y_{N×N} + ∂I_nl/∂V + ω·∂Q_nl/∂V`, the **conversion matrix** in **real-valued** form, size `2·N_nl·(K+1)` (diamond size `M` for multi-tone); each coupling a real 2×2 block from `G_{k±i}`/`C_{k±i}` (both terms required — `F` is not holomorphic); DC/`i=0`/`k=i=0` special-cases per Maas. Full 2K-resolved, not approximate.
- **Newton solve** is **dense** (NumFlat) — `N_nl` ≪ total nodes; block/iterative solves deferred, **including for multi-tone**: the §6.6 product ceiling keeps the T-tone system inside what dense handles (6 tones at order 3 converges in 4.3 s).
- **Full internal V/I** recovered by one per-harmonic linear back-substitution after convergence; all spectra retained incl. k = 0 (`Pdc`/`PAE` source).
- **Initial guess**: nonlinear DC (Newton + gmin + source-step) + `1e-3` harmonic seed, or previous continuation point. Nonlinear-DC specified here as the k = 0 specialization.
- **Continuation**: power/source stepping (Heroes 2/4) and previous-point (Hero 3), with step-halving backoff on max-iter; extended-domain IV/QV required.
- **SDD domain errors clamp+warn** (not hard-error) on overshooting iterates, with an obvious user-facing warning — so continuation survives a transient out-of-domain iterate.
- **Guard harmonic** a first-class v1 knob (attenuates high-harmonic Jacobian terms; `J` only), **hard cutoff by default**, taper selectable for tuning. **Convergence** absolute `1e-6` by default, with relative and normalize-to-drive options and a max-iteration cap.

## 16. Open items

**Resolved in review (2026-05-30):**

1. **`mixIndex` enumeration order** (§6.3) — **decided:** enumerate the retained half-plane diamond representatives by **ascending total mixing order** `m = |k₁| + |k₂|` (so DC `(0,0)` is index 0, the carriers next, then the second-order products, …), and within a given order by the upper-half-plane rule (`k₁ > 0`, or `k₁ = 0 ∧ k₂ ≥ 0`) sorted by `k₁` then `k₂` descending. Low-order-first is deliberate: raising `MaxMixingOrder` then **appends** indices without renumbering existing ones, so cube indices are stable across an order change. The measurement library's `tone`/`IMn` invert this exact order — locked when that library is written; the only thing to confirm later is that the Hero-5 products land on the expected bins.
3. **Guard-harmonic profile** (§12.1) — **decided:** **hard cutoff is the default**; a tapered roll-off is selectable for heuristic tuning once the engine runs.
6. **SDD domain error under overshoot** (§4, §11; closes `expressions.md` §18 open item 2) — **decided:** a `log`/`sqrt`/etc. domain error inside `Evaluate` on an overshooting iterate **clamps and warns** rather than hard-erroring, so continuation is not killed by a transient out-of-domain iterate. The **warning must be surfaced obviously to the user** (not buried in a log), naming the model and the offending operation. (`expressions.md` §18 item 2 should be updated to point here.)

**Deferred to Phase-4 bring-up (settle empirically):**

2. **Oversampled-vs-minimal `G`/`C` for the fixed-size Jacobian** (§5.3) — stays a flag **defaulting to oversampled**; revisit after measuring Hero-2/3 iteration counts. Affects convergence rate only, never the converged answer.
4. **Damping policy** (§8, §11) — whether `λ` is fixed, line-searched, or engaged only after a failed full step; tune alongside the continuation step-backoff during heuristic bring-up.
5. **Block-structured / iterative HB solve** (§8) — deferred; the dense solve stands unless a hero misses the `< 3 s`/point NFR at Hero-4 scale, at which point a block/iterative scheme is the profiled optimization.
**Resolved by implementation:**

7. **True n-tone (≥ 3 tones)** (§6.4–§6.6) — **BUILT** (up to 6 tones). Two corrections to what this item predicted, both worth keeping visible:
   - It is **not** the "dimensionality refactor of the 2-tone FFT" this item described. A separable `N₁×…×N_T` real FFT is `nextpow2(4·order)^T` samples — 1.9 GB of working arrays per Newton iteration at 6 tones and order 3 — so the transform is an **APFT** instead, whose sample count scales with the retained set (1,512 samples for that same case). §6.4 carries the table.
   - It did **not** need to land with the iterative solver (item 5), contrary to the pairing asserted here and in the old §6.4. The product ceiling (§6.6) bounds the retained set at a size the dense solve handles: 6 tones at order 3 converges in 4.3 s. Item 5 stays deferred on its own merits.

   The two-tone path (`MixingGrid`/`HbFft2D`/`HbNewton2D`) is untouched and remains the `T = 2` implementation.

---

*On approval, Phase 4 implements (sub-gated 4a–4d): the partition + interface extraction (reusing the Phase-2 linear engine), the FFT layer (single- and multi-tone, with `FFTOverSample`), the per-iteration IFFT→`Evaluate`→FFT loop (calling the Phase-3 `Evaluate`/AD), the real-valued conversion-matrix Jacobian and dense Newton solve, the initial-guess (Phase-3 nonlinear-DC seed) / continuation (`DriveStepping`) / guard-harmonic machinery, and the V/I-cube writeback — validated on Heroes 2–5 against the transcribed-SDD references and the Cripps/Class-mode theory cross-checks. **4a (single-tone → Hero 2) is the make-or-break gate** before the sweep/transform layers (4b loadpull, 4c multi-tone, 4d multi-device).*
