# Sonnet Brief — WSP-5: the WSProbe under harmonic balance (large-signal small-signal analysis)

**Read `brief-wsprobe-0-overview.md` first** (D-10) and `brief-wsprobe-8-hb-performance.md`
alongside — that brief is the fast path of this one and its gates require this brief's straightforward
path to exist as the oracle. Depends on WSP-1 (cube layout, accessors) and reuses WSP-2/3 unchanged.
Reference: T. A. Winslow, *General Circuit Analysis Using The WSProbe* (2023), §4.2 (p. 43: "Linear
analysis, harmonic balance, or AC analysis can all be accommodated"), §4.9 (Fig. 30, start-up to
steady state), §7 (p. 111: Ohtomo "extended into the nonlinear regime using Harmonic Balance"), §9
(p. 120: "A single S-parameter or Harmonic Balance analysis sweep can generate the starting transfer
functions"). Method references the design note must carry: S. A. Maas, *Nonlinear Microwave and RF
Circuits*, 2nd ed., ch. 3 (the conversion matrix); A. Suarez, *Analysis and Design of Autonomous
Microwave Circuits* (2009), ch. 1–2 (large-signal stability via a small-signal perturbation of the
periodic steady state) — both already in the document's reference list ([11], [24], [25]).

**Scope.** A new engine capability — the **conversion-matrix small-signal solve** around a converged
HB operating point — and the WSProbe's `wsp` computed with it over a swept probe (tickle) frequency
`ssfreq`. The HB Newton solve is untouched; the HB Jacobian it already builds is this analysis at
`ssfreq = 0` and is the gate.

---

## 1. What is being computed

The owner's description is the right one. The circuit is driven hard by its HB tones (bias + the
fundamental(s) at the requested drive); at that operating point the nonlinear devices are
**linearised** into periodically time-varying conductances and capacitances; the probe then injects
a **vanishingly small** series voltage or shunt current at a frequency `ω_ss` that is in general
**not** on the HB grid, and the circuit's linear response at `ω_ss` is read at every probe. Because the
linearisation is time-varying, a stimulus at `ω_ss` produces responses at every sideband
`ω_ss + k·ω0`, `k = −K … K`; the `wsp` entries are the responses **at `ω_ss` itself** (the `k = 0`
sideband), which is the large-signal counterpart of the S-parameter `wsp` — and every function of
WSP-2/3 applies to it unchanged.

This is the standard large-signal stability formulation (Suarez; the document's [11], [24], [25]): a
right-half-plane pole of the linearised periodic system shows up in `1/H0(ω_ss)` and `1/Y0(ω_ss)` with
Kurokawa's signature exactly as in the linear case (Fig. 30), and a **parametric** (sub-harmonic)
instability shows up at `ω_ss ≈ ω0/2` — which no S-parameter analysis can see, and is the case the
EuMIC-2024 margin paper was written about (overview §1.1).

---

## 2. The directive

**R-wsp5-1.** On the HB analysis line, a small-signal sweep spelled like the S-parameter sweep and
honouring its unit rules (`docs/design/cli.md` §10A.3; the AUT-8 `Unit=` lesson):

```
analysis HB1 type=hb Tone=RFfreq MaxHarm=7 Sweep="Pavl_dbm: -20 .. 20 step 1" \
     SSStart=0.1 SSStop=10 SSNpts=991 SSUnit=GHz   [SSMaxHarm=K_ss]
```

`SSStart`/`SSStop` with `SSStep` or `SSNpts`, `SSUnit` applying to all three; `SSLog` for a log
grid; `MarginThreshold=<dB>|none` exactly as on the S-parameter line (WSP-9 R-wsp9-3), applied per
operating point. Absent ⇒ no small-signal solve and the run is byte-identical to today (R-wsp5-9(a)). `SSMaxHarm`
truncates the sideband order (`K_ss ≤ K`, default `K`) and is reported in the run summary whenever it
is below `K`. `HarmonicBalanceAnalysis` gains the matching `*Expr` fields; `CnlReader`/`CnlWriter`
round-trip them; the Analysis Setup dialog gets a **Small-signal** group with the same fields and the
live point count.

A WSProbe present with no `SS*` keys is not an error: the probe is transparent (WSP-1 R-wsp1-1) and
the run simply carries no `wsp`. The run summary says so once (`wsprobe.hb-no-ss-sweep`: "3 WSProbes
present; add SSStart/SSStop to the HB analysis to compute their large-signal transfer functions").

---

## 3. The conversion-matrix small-signal solve

Notation: `N_int` interface nodes (the nonlinear-facing nodes, `HbLinearExtractor`'s `_interfaceNodes`;
**the probes are not interface nodes** — they live in the linear partition and are reached by the
back-solve), `K` harmonics, `K_ss ≤ K` the sideband order, `ω0` the fundamental (single-tone; §7
below for two-tone), `ω_k = ω_ss + k·ω0` for `k = −K_ss … K_ss`.

**R-wsp5-2. Two-sided device spectra.** After HB convergence the engine holds, per device and port
pair, the one-sided spectra `G[n,m,k]` and `C[n,m,k]` (and the `w ≥ 2` buckets `Dw`) for `k = 0 … 2K`,
normalised as `HbFft` normalises them (DC bin `÷N`, AC bins `÷(N/2)`, so an AC bin is twice the
two-sided coefficient — the `ConversionWeight` comment in `HbNewton.BuildJ`). The small-signal solve
uses the **two-sided** coefficients of the real periodic waveforms:

```
G⁽²⁾[k] = G[0]            k = 0
G⁽²⁾[k] = G[k] / 2        k > 0
G⁽²⁾[k] = conj(G[−k]) / 2 k < 0
```

(likewise `C⁽²⁾`, `Dw⁽²⁾`). These are computed **once per operating point** and reused for every
`ω_ss` (WSP-8 relies on that).

**R-wsp5-3. The linear network at every sideband.** `Y_NN(ω_k)` from the extractor for every `k`.
For `ω_k < 0`, stamp at `|ω_k|` and take the **complex conjugate** (the linear network is real in the
time domain; no model is ever called with a negative frequency, which the Touchstone interpolators
would not survive). For `ω_k = 0` exactly, use the DC formulation (`ExtractDC`'s real matrix). This is
the dominant cost of the whole analysis and the subject of WSP-8.

**R-wsp5-4. The system.** Unknowns: the interface voltages at every sideband,
`V[n, k]`, `n = 1 … N_int`, `k = −K_ss … K_ss`, complex, size `n_c = N_int·(2K_ss + 1)`. The
conversion matrix `J_ss` (complex, dense) has blocks

```
J_ss[(n,k), (m,l)] = δ_kl · Y_NN(ω_k)[n,m]
                   + G⁽²⁾[n,m, k−l]
                   + j·ω_k · C⁽²⁾[n,m, k−l]
                   + Σ_w H[w](ω_k) · Dw⁽²⁾[n,m, k−l]
```

with `G⁽²⁾[·, ·, k−l] = 0` for `|k−l| > 2K`. No half-amplitude weights, no real-split, no DC special
cases: at `ω_ss ≠ 0` every sideband is an ordinary complex unknown. (At `ω_ss = 0` the `k` and `−k`
unknowns become conjugates of each other, which is why `BuildJ` folds them into the real-split form;
that folding is the gate, R-wsp5-9(b).) A control-current SDD contributes its `J_cc` blocks the same
way `BuildJ` adds them; an external device that cannot report spectra is a refusal naming the instance
(`wsprobe.hb-device-no-spectra`).

**R-wsp5-5. The probe injections and readings.** For each probe `p` and each of its two injections
(unit `vS` in its branch with `− at G, + at L`; unit `iP` into its G node — WSP-1 R-wsp1-5's exact
stamps, at `ω_ss`, in the **linear partition** with all independent sources off):

1. **Norton excitation at the interface, sideband 0 only.** Solve the linear partition at `ω_ss` with
   the injection to get the open-circuit interface voltages `V_oc`, and `I_src = −Y_NN(ω_ss)·V_oc`
   (the extractor's `ISrcFromVoc` pattern). The right-hand side of the conversion system is `I_src` in
   the `k = 0` block and zero elsewhere.
2. **Solve** `J_ss · V = −RHS` for the interface voltages at all sidebands.
3. **Device current at sideband 0:** `I_nl,0 = −(Y_NN(ω_ss)·V[·,0] + I_src)` (the balance
   `Y·V + I_src + I_nl = 0` at `k = 0`).
4. **Back-solve** the linear partition at `ω_ss` with the injection **and** `I_nl,0` at the interface
   (`SolveFullNetwork`'s convention) and read, at every probe `j`, `iS_j = x[br_j]` and
   `vP_j = x[nG_j − 1]`.

Fill `wsp(2p−1, ·)` from the series injection and `wsp(2p, ·)` from the shunt one exactly as WSP-1
R-wsp1-5 does. The factorisations at `ω_ss` and the dense factorisation of `J_ss` are shared by all
`2N` right-hand sides of that `ω_ss`.

**R-wsp5-6. Cubes.** Per operating point: `wsp {ssfreq, row, col}` and the **eight** per-probe
defaults `{ssfreq}` — WSP-1's six through `WspReduction` and WSP-9's `SM_Y0`/`SM_H0` through
`WspMargin` (D-2); `__WspProbes` as in WSP-1.
Under a drive sweep, `ParametricSweepEngine` stacks them to `{Pin, ssfreq, row, col}`. The `ssfreq`
axis carries unit `Hz` and the actual frequencies (not indices — this is a genuine frequency axis, not
a harmonic-order axis; `HbSpectrum` is not involved).

`HB1.wsp`, `HB1.idx("GATE")`, `HB1.H0("GATE")` … resolve exactly as `SP1.*` do (WSP-1 R-wsp1-11); the
functions of WSP-2/3 take an `HB1.wsp` cube with no change, since they map over the leading axes.

---

## 4. Where a large-signal instability shows up, and what the docs must say

The design note and the user docs state, with Fig. 30 redrawn:

- At low drive the HB `wsp` tends to the S-parameter `wsp` linearised at the DC operating point
  (R-wsp5-9(c)); the two analyses answer the same question at the two ends of the drive sweep.
- A right-half-plane pole of the linearised periodic system is seen at `ω_ss` near its imaginary
  part; Kurokawa's signature on `1/H0(ω_ss)`/`1/Y0(ω_ss)`; the same `wsp_unstable_freq_kurokawa`.
- A **parametric** instability (period doubling) is seen at `ω_ss ≈ ω0/2` (and its images at
  `ω0/2 + kω0`); this is what a linear analysis cannot see and the reason to run the sweep across
  the drive.
- The steady-state condition `1/H0 = 0` (Fig. 30, p. 70) is reached only by an autonomous solution
  the HB analysis was not asked for; the `wsp` of a *converged, non-oscillating* HB solution
  answers whether that solution is stable, not what it would become.
- **The margin under drive.** [M]'s own amplifier lost its margin in the *small-signal* simulation —
  the linear `SM_Y0`/`SM_H0` of WSP-9 already catch that case. The drive-swept fan of
  `SM_Y0(ssfreq)` is the large-signal extension: a margin that collapses only above some `Pin` is a
  drive-dependent instability, and the parametric case at `ω0/2` reads as a notch in the fan that is
  absent at low drive. The run summary prints the per-operating-point minimum, and the threshold
  diagnostic names the drive level as well as the frequency.

---

## 5. Two-tone and multi-tone operating points

**R-wsp5-7.** With `T ≥ 2` tones the sidebands are `ω_ss + k₁ω₁ + … + k_Tω_T` over the retained
mixing lattice (the `MixingGrid`/`MixingLattice` the two-tone Jacobian already enumerates), and the
device spectra are the two-sided lattice spectra. The structure is identical (replace the `k` index by
the lattice index); v1 of this brief **supports single-tone and two-tone** and refuses `T ≥ 3` with
the count of retained products it would need (`wsprobe.hb-tones-unsupported`), because the APFT path's
conversion blocks are a separate piece of work.

---

## 6. Interaction with loadpull and harmonicaRF

`LoadpullEngine` and `LoadpullPursuitEngine` own their HB loops; they do **not** run the small-signal
solve in this brief (each grid point would multiply the cost by the `ssfreq` count). They are not
refused — a loadpull `.cnl` with `SS*` keys runs its loadpull and reports once that the small-signal
sweep applies to `hb` only. The envelope method of WSP-3 §6 (rank-1 re-termination of `wsp`) is the
document's replacement for a stability loadpull, and it works on an HB `wsp` exactly as on a linear
one — a large-signal stability envelope for no HB solves at all beyond the nominal one.

harmonicaRF's source-impedance computation already edits the HB Jacobian (`harmonicarf.md` §6.2);
this brief does not change it, but WSP-8's precomputed linear-partition data is the same shape it
needs and should be shared when that tool is next touched.

---

## 7. Gates

`tests/Engine.Tests/HarmonicBalance/WSProbeHbTests.cs`.

**R-wsp5-9(a) — untouched without `SS*`.** `hero2.cnl` with two WSProbes and no `SS*` keys: `V`, `I`,
`INl` byte-identical to the unprobed run.

**(b) — the HB Jacobian is the `ω_ss = 0` case.** At the converged Hero 2 operating point build `J_ss`
at `ω_ss = 0` with `K_ss = K`, fold its `±k` blocks into the one-sided real-split form (the inverse of
R-wsp5-2's mapping, with the DC row/column rules of `BuildJ`'s Maas special cases), and assert it
equals `HbNewton.BuildJ`'s matrix to 1e-12 relative. This pins the two-sided coefficient convention,
the `jω_k` factor and the `Y_NN` placement in one comparison.

**(c) — low drive tends to the linear `wsp`.** Hero 2 at `Pavl = −60 dBm`, `SSStart..SSStop` over
0.5–4 GHz: `wsp(ssfreq)` equals the S-parameter `wsp(freq)` of the same netlist (DC-linearised, WSP-1)
to 1e-6 relative at every point, and `SM_Y0`/`SM_H0` equal the S-parameter margins to 1e-6 absolute. Then at `+10 dBm` assert they **differ** (by more than 1e-2 relative
somewhere) — the analysis must see the drive.

**(d) — the independent oracle: two-tone HB.** Take a single-tone operating point at `f0 = 2 GHz`,
`Pavl` in compression. Separately run a **two-tone** HB with tone 2 a `−90 dBm` source at
`f2 = 2.3 GHz` at the probe's G node (a `V_1Tone` in series is the series injection; an `I_1Tone` to
ground is the shunt one) and `MaxMixOrder` large enough to retain `(k, 1)` for `|k| ≤ K_ss`. The
response at mixing product `(0, 1)` (i.e. `f2`) at every probe, divided by the injected amplitude,
must equal the conversion-matrix `wsp` entries at `ω_ss = 2π·2.3 GHz` to 1e-5 relative; and the
responses at `(k, 1)` must equal the solved sideband voltages `V[·, k]`. This is the gate that proves
the small-signal solve is the linearisation of the engine's own nonlinear solution, through a code
path that shares no linearisation with it.

**(e) — a parametric instability.** A pumped varactor divider fixture (a `NonlinearC` in a resonant
loop with a pump at `f0`, the textbook period-doubling case): below the pump threshold
`wsp_unstable_freq_kurokawa(H0)` over `ssfreq ∈ [0.3 f0, 0.7 f0]` is empty; above it, it reports a
frequency within a grid step of `f0/2`. The threshold is verified independently by the two-tone
oracle of (d) (the `(−1, 1)` product's growth), not by a textbook number.

**(f) — negative sidebands are conjugated.** A netlist with an `SnP` in the linear partition:
`Y_NN(ω_ss − kω0)` for `ω_ss − kω0 < 0` equals `conj(Y_NN(|…|))`, and the `SnP` model is never called
with a negative frequency (assert via the model's own call log or a stub).

**(g) — sweep stacking and accessors.** A `Pavl` sweep produces `wsp[Pavl, ssfreq, row, col]`;
`HB1.H0("GATE")` is `[Pavl, ssfreq]`; `wsp_unstable_freq_kurokawa(at(HB1.H0("GATE"), "Pavl", 3))`
evaluates.

**(h) — two-tone operating point.** Hero 5's two-tone operating point with a probe: `wsp` exists,
(c)'s low-drive limit holds against the S-parameter `wsp`, and `T = 3` is refused with the message.

**(i) — counters, not timings.** Per `ω_ss` per operating point on the straightforward path: exactly
`2K_ss + 1` linear-partition factorisations *or cache hits* (count both), one dense factorisation of
`J_ss`, `2N` dense solves and `2N` sparse back-solves. WSP-8 will lower these and assert its own
counts against the same fixture.

## On completion

Findings to `src/Engine/RESOLVED.md`; **never a `CLAUDE.md`**. Append **§10** "Large-signal small-signal
solve" to `docs/design/stability-wsprobe.md` (§9 is WSP-9's margin section) and a pointer in `docs/design/harmonic-balance.md` §7
(the Jacobian section) noting that the conversion matrix now has a second consumer. Update
`src/Engine/HarmonicBalance/CLAUDE.md`'s convention list with the two-sided coefficient rule of
R-wsp5-2 (a frozen convention, like the FFT sign).
