# P1Tone Harmonic Terminations — band-assignment rule (single-tone & two-tone HB)

Status: design spec for `brief-sweep-5-p1tone-source.md`. Headless RF rule + stamping contract. No UI.

## 1. What the user declares
P1Tone (and, by the same rule, any future harmonic-terminated source/load) accepts an optional set of
**per-harmonic-band reference impedances**, mirroring the Tuner's `Z[k]` convention:

- `Z[0]`  — DC termination (optional; default = internal `Z`, i.e. the real reference, or a near-short per the
  existing `Zdefault` idiom). 
- `Z[1]`  — fundamental band (defaults to the internal reference impedance `Z`, e.g. 50 Ω).
- `Z[2]`, `Z[3]`, … `Z[K]` — 2nd, 3rd, … harmonic bands (optional).
- `Zdefault` — catch-all for any band the user did not declare (default = `Z`, the internal reference; this keeps
  Pavl and the presented fundamental impedance consistent, exactly as the Tuner does).

`G[k]` (reflection-coefficient spelling) is converted to `Z[k] = Z0·(1+Γ)/(1−Γ)` at construction, identical to
the Tuner. Each `Z[k]`/`Zdefault` is an **expression** (so it can reference swept variables); resolved per sweep
point.

These are **band** impedances, not per-mixing-product impedances. The user thinks in "fundamental / 2nd harmonic
/ 3rd harmonic." The engine excites the network at *every* retained spectral line (every harmonic in single-tone;
every mixing product `(k1,k2)` in two-tone). The rule below maps each spectral line to exactly one declared band.

## 2. The center frequency `f_c` (the band ruler)
Define a single **band-center fundamental** `f_c`:

- **Single-tone:** `f_c = f0` (the one tone).
- **Two-tone:** `f_c = (f1 + f2) / 2` — the arithmetic mean of the two fundamentals.
  Rationale: the two fundamentals are intentionally close (a two-tone IM test uses f1, f2 a small Δ apart), so
  their mean is the natural center of "the fundamental band." Both f1 and f2 then round to band 1 (see §3), and
  the harmonic bands sit near `n·f_c`.

The **half-band width** is `f_c / 2`: band `n` owns the frequency interval `[(n − ½)·f_c, (n + ½)·f_c)`.

## 3. The rule — band assignment by frequency (LOCKED)
For a spectral line at signed physical frequency `f` (single-tone: `f = k·f0`; two-tone: `f = k1·f1 + k2·f2`):

```
n = round( |f| / f_c )                      // nearest harmonic band
present:
    n == 0                       → Z[0] if declared, else Zdefault     // DC / baseband (e.g. f1−f2)
    n >= 1 and Z[n] declared     → Z[n]
    n >= 1 and Z[n] not declared → Zdefault
```

Impedance is **conjugate-symmetric in frequency**: at a negative-frequency representative the engine already
extracts at `|f|` and conjugates (`HbEngine.ExtractMix`), so the stamp uses `Z(|f|)` and the existing conjugation
path handles the sign. P1Tone therefore only ever computes `Z` at `|f|` — no special negative-frequency case.

This is exactly the behavior the owner asked for, and the "mixing-order dependence" is **subsumed by frequency**:

- **IM3 lower** `(2,−1)` → `2f1 − f2`. With f1,f2 near f_c this is ≈ `f_c − (3/2)Δ`, well inside band 1
  → **Z[1]**. Likewise IM5 `(3,−2)`, IM7 `(4,−3)` sit just below f1 → still band 1 → **Z[1]**.
  ✓ matches "Z[1] used for IM3, IM5, IM7…".
- **2nd-harmonic zone** `(1,1)` → `f1 + f2 ≈ 2f_c` → band 2 → **Z[2]**. Its neighbors `(2,0)=2f1`, `(0,2)=2f2`,
  and 2nd-harmonic IM `(3,−1)=3f1−f2 ≈ 2f_c − 2Δ`, `(1,−1)`… all round to band 2 → **Z[2]**.
- **The crossover the owner intuited** happens automatically: a high-order IM product "off the 2nd harmonic"
  drifts down in frequency as its order grows (each extra `(+1,−1)` step subtracts Δ). It stays in band 2 until
  its frequency drops below `1.5·f_c`, at which point `round(|f|/f_c)` flips to 1 and it is presented **Z[1]**.
  The switchover is precisely at `|f| = 1.5·f_c`, i.e. when `|k1·f1 + k2·f2|` crosses `1.5·f_c`. No order
  threshold is hard-coded; the frequency decides, which is the physically correct criterion (the termination a
  product "sees" should depend on where it lands in the spectrum, not on how we labelled it).

### 3.1 Worked switchover (why frequency, not order)
Let f1 = 1.00 GHz, f2 = 1.02 GHz ⇒ f_c = 1.01 GHz, Δ = 0.02 GHz, half-band = 0.505 GHz; band 2 = [1.515, 2.525)
GHz; band 1 = [0.505, 1.515) GHz.

| product (k1,k2) | f = k1·f1+k2·f2 | \|f\|/f_c | band n | Z presented |
|---|---|---|---|---|
| (1,0) f1 | 1.00 | 0.990 | 1 | Z[1] |
| (2,−1) IM3 | 0.98 | 0.970 | 1 | Z[1] |
| (3,−2) IM5 | 0.96 | 0.950 | 1 | Z[1] |
| (1,1) | 2.02 | 2.000 | 2 | Z[2] |
| (2,0) 2f1 | 2.00 | 1.980 | 2 | Z[2] |
| (3,−1) | 1.98 | 1.960 | 2 | Z[2] |
| (4,−2) | 1.96 | 1.941 | 2 | Z[2] |
| … (n,−(n−2)) drifting down … | → 1.515 | → 1.500 | 2→1 at 1.515 GHz | Z[2] then Z[1] |

The (k1,k2) family hanging off the 2nd harmonic keeps Z[2] until it crosses 1.515 GHz, then takes Z[1] — exactly
"until an IM product off the 2nd harmonic lands down near the Z[1] frequency."

### 3.2 Tie-break and guards
- **Exact half-band tie** (`|f|/f_c` lands on `n+0.5`): `Math.Round` uses banker's rounding; force
  round-half-**up** (`Math.Floor(x + 0.5)`) so a product exactly on a boundary goes to the **higher** band
  (deterministic, documented). Negligible in practice (requires exact commensurate hits).
- **Off-grid / sub-DC:** `n == 0` (any product with `|f| < f_c/2`, e.g. the baseband IM `(1,−1) = f1−f2 = Δ`)
  → DC band → `Z[0]`/`Zdefault`. This is correct: `f1−f2` is a baseband product, not a fundamental.
- **Above the declared top band** (`n > K`): `Zdefault`. (The user can always declare more bands.)
- **Single-tone** reduces cleanly: `f = k·f0`, `f_c = f0` ⇒ `n = k`, so `Z[k]` maps 1:1 to harmonic k —
  identical to the Tuner's single-tone behavior. No special-casing needed; the same code path serves both.

## 4. Stamping contract (must match the engine)
P1Tone = an RF drive at f0 **in series with** its harmonic-terminated reference impedance — i.e. the SourceTuner
topology minus the bias-tee/role machinery:

- **Drive branch:** a tone voltage source `Vs` (Group-2 branch) active only at the fundamental f0
  (`|ω − 2π f0| < OmegaTol`), zero at all other lines — exactly `ToneSourceModel`/SourceTuner drive stamping.
  `|Vs| = sqrt(8 · Pavl_W · Re(Z_present(f0)))` where `Z_present(f0)` is the band-1 impedance (override-aware),
  so available power and the presented fundamental impedance stay consistent (same formula the Tuner's
  `SetSourceDrive` uses).
- **Series termination:** a Group-2 `Z` element (use the Tuner's `StampZPort` pattern: `V(na)−V(nb)−Z·I = 0`)
  between the source's external node and the drive node, with `Z = Z_present(ω)` from the §3 rule evaluated at
  the line's `|f|`.
- **`Z_present(ω)`** is computed by a `GetZ(omega)`-style method that:
  1. maps `omega → f = omega/2π`;
  2. computes `n = roundHalfUp(|f| / f_c)`;
  3. returns `Z[n]` (override-aware, if a swept-harmonic override mechanism is added later) else `Zdefault`,
     with `n==0 → Z[0]/Zdefault`.
  `f_c` is injected at construction/setup: single-tone `f0`; two-tone `(f1+f2)/2`. The engine knows both tones at
  `HbEngine.Run`/`RunTwoTone` setup — pass `f_c` (and the tone set) into the model the same way the Tuner gets
  its tone via `SetTone`/`SetSourceDrive`.

**Per-line evaluation:** the HB linear extractor calls `Stamp(mna, c, omega)` once per retained spectral line
(per harmonic single-tone; per mixing product two-tone), so the §3 mapping runs per line automatically and the
right `Z` lands on each. The conjugate-symmetry handling for negative-ω representatives is already in
`HbEngine.ExtractMix` — P1Tone always evaluates at `|f|`.

## 5. Why this rule (design defense, for the doc)
- **Frequency is the physical truth.** A spectral line is terminated by whatever impedance the network presents
  at *its frequency*. Binning by nearest harmonic band is the faithful discretization of "the user specified the
  termination vs. frequency, coarsely, per harmonic."
- **Order-independent, label-independent.** Two products at the same frequency get the same termination
  regardless of `(k1,k2)`. This avoids the unphysical situation where two lines at ~the same frequency see
  different Z because one was "called" IM5 and the other 2f1.
- **Graceful, monotone crossovers.** As Δ→0 (tones merge), every band-n zone collapses onto `n·f_c` and the rule
  → single-tone harmonic mapping continuously. As Δ grows, products spread and reassign bands exactly when they
  cross a half-band boundary — no discontinuity, no hidden order cliff.
- **Matches the Tuner.** Same `Z[k]`/`Zdefault`/`G[k]` surface and the same `round(ω/ω0)` spirit, generalized
  from `ω0` to `f_c`. A user who knows the Tuner needs no new mental model.

## 6. S-parameter port role (brief-p1tone-num-sddx-defaults)

A top-level P1Tone also participates in S-parameter analysis as a port, identical to a `Term`.

### 6.1 The `Num` parameter
P1Tone carries a `Num` parameter (auto-assigned at placement from the shared Term + P1Tone pool, so
Port numbers never collide). `SParameterEngine.GetPortNum` reads it exactly like Term's `Num`.

### 6.2 S-param stamping
In S-parameter analysis `_fc = 0` (no tone context set). Two code paths:

- **Wave path** (`Re(Z0) > 1e−12`, the common case): P1Tone stamps `G = 1/Z` conductance via the
  usual `AddAdmittance` path, and its Kurokawa S-extraction uses `Node0/Node1`. Same as Term.
- **Legacy path** (`Re(Z0) ≤ 0`): `P1ToneModel.StampAsSParamPort(mna, c)` stamps a 0 V source
  branch between `Nodes[0]` and `Nodes[1]`, mirroring `TermModel.Stamp`. `LastBranchIndex` is read
  by `CollectPortsAndBranchLabels` and stored in the `PortEntry`.

Buried P1Tone (dotted `InstancePath`) is skipped identically to buried Term — it contributes no
S-param port and no matrix stamps.

### 6.3 Auto-placement numbering
`SchematicViewModel.NextFreeTermNum` scans both `SymbolKind.Term` and `SymbolKind.P1Tone` instances
so P1Tone:P1 (Num=1) and Term:T1 (Num=1) cannot coexist on the same testbench top level. This
matches the S-param port-extraction invariant that `Num` values must be unique across all port types.

## 7. Limitations (state in the doc)
- The band model is **uniform-width** (`f_c`-spaced). It assumes the harmonic bands of interest sit near integer
  multiples of `f_c`. For wildly non-commensurate two-tone setups (f1, f2 far apart), "fundamental band" is less
  meaningful — but two-tone IM analysis intrinsically assumes closely-spaced tones, so this is the right regime.
  Document that f1≈f2 is the intended use; for far-apart tones the nearest-band rule still applies but the
  "bands" are just `round(|f|/f_c)` bins.
- Only **band-resolution** control is offered (one Z per harmonic band), not per-mixing-product control. That is
  intentional: per-product termination is not physically realizable by a passive network and would over-specify.

## 8. PnTone — the multi-tone authoring variant (brief-pntone, 2026-06-24)

P1Tone drives a SINGLE tone. To author a two-tone (or higher) HB from the Schematic Editor conveniently,
**PnTone** is a clone of P1Tone that injects multiple tones from one component — the power-domain analog of
`V_nTone` (which is `V_1Tone` with indexed tones).

- **Same symbol & 2-pin geometry as P1Tone** (`SymbolKind.PnTone` → P1Tone glyph; default 2-pin vertical).
- **Per-tone fields** `Freq[i]` / `Pavl[i]` / `Phase[i]`, added/removed with the parameter editor's "+"/"−"
  (mirrors `V_nTone`'s `Freq[i]`/`V[i]`/`Phase[i]`). Seeded with **two tones** so a freshly-placed PnTone is a
  ready two-tone source. Shared `Z` (= Zdefault reference) and optional band `Z[k]` terminations, exactly as
  P1Tone — the §3 band rule applies to the whole multi-tone spectrum.
- **Topology** = P1Tone's, but the drive branch injects each tone's phasor at its own frequency:
  `nRef --[V_drive(ω)]-- nDrv --[Z_Port GetZ(ω)]-- nExt`, where at a spectral line ω the drive equals the
  matching tone's `|Vs_i|∠Phase_i` (`|Vs_i| = sqrt(8·Re(Z(Freq_i))·Pavl_i_W)`), else 0.
- **HB engine.** `Run`/`RunTwoTone` call `PnToneModel.SetToneContext(f_c)` (the band ruler — `f0`, resp.
  `(f1+f2)/2`); PnTone drives at its own `Freq[i]`. The commensurability checks validate each tone lands on the
  HB grid. The two fundamentals for the mixing grid still come from the **HB directive** (`NumFreqs`, `Tone[i]`);
  PnTone supplies the drive at those frequencies. `Model`: `src/Core/Devices/PnToneModel.cs`; engine reference
  `"PnTone"`; factory scans consecutive `Freq[i]` (no `NumFreqs` needed).
- **Not an S-param port** (no `Num`): in S-param mode it is passive — presents `Z[1]` between its terminals and
  ties off its internal node (self-contained; no port-pool/lint changes).
- Gate tests: `PnToneTwoToneTests` (Engine — one PnTone drives both carriers + produces IM3 through a cubic SDD);
  `PnToneComponentTests` (Ui — registry, seeded two-tone defaults, per-tone template, shared symbol, extraction).
