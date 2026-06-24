# circuitRF — Loadpull Simulation Post-Processor (design)

**Status:** Phase 1 (derived-field enrichment) implemented; Phase 2 (`.spl`/`.lpcwave` export) designed,
not yet implemented.

**Reads with:** `docs/design/loadpull.md` (the LP engine + Tuner), `docs/design/loadpull-contours.md`
(the `LoadpullSurface` consumer + contour rendering), `src/Engine/Loadpull/CLAUDE.md` (engine conventions,
the `V`/`INl` sign convention), `brief-7.4f` / `brief-7.5g` (the `.spl`/`.lpcwave` readers and the
`LoadpullDerivedFields` helper this post-processor reuses).

---

## 1. Why this exists

The loadpull **engine** (`LoadpullEngine`, `docs/design/loadpull.md`) runs the 2-D Γ-grid × Pin sweep and
emits a `DataSet` of **raw physics + core FOMs**: the HB spectra `V`/`INl` at the interface nodes, the
terminations `GammaLoad`/`ZLoad`, the bias `V`/`I`, and the headline FOMs it can compute cheaply from the
converged solution (`Pout` in Watts, `Gt`/`Gp` in dB, `DE`/`PAE`, `Pdc`).

A **measured** loadpull file (`.spl`/`.lpcwave`) carries additional **derived display metrics** that the
RfCore readers compute at import time via `LoadpullDerivedFields.Derive` — `Pout_dBm`, `Zin_real`/`Zin_imag`,
`AMPM`, `IRL`. The Data Display contour picker offers these for measured data. A **simulated** run lacked
them, so a simulated loadpull contour could not match a measured one (the user-reported gap).

**Principle — the engine computes physics, not display metrics.** Rather than grow the engine with
display-oriented derivations, a separate **post-processor** takes the engine's `DataSet` and adds the derived
metrics, using the *same* `LoadpullDerivedFields` math the readers use. This keeps:

- the **engine** focused on the solve (raw spectra + the FOMs that need the solver/bias it alone has),
- the **derived FOMs** in one shared place (`LoadpullDerivedFields`), identical for measured and simulated,
- the **Data Display** origin-blind: a simulated contour renders identically to a measured one,
- a clean input for **export**: the enriched `DataSet` is what a `.spl`/`.lpcwave` writer serializes.

```
                    raw + core FOMs + node provenance          enriched DataSet
  LoadpullEngine  ───────────────────────────────────▶  LoadpullPostProcessor  ───────────▶ run.npy
   (the solve)      V, INl, GammaLoad, ZLoad, Pout(W),      (adds Pout_dBm,           (Data Display
                    Gt, Gp, DE, PAE, Pdc, Bias*,             Zin_real/imag,           reads derived
                    __SrcNodeIdx, __LoadNodeIdx              IRL, AMPM)               metrics directly)
                                                                   │
                                                                   ▼
                                                          SplWriter / LpcwaveWriter   (Phase 2)
                                                          (enriched DataSet → file)
```

---

## 2. What the engine emits (the post-processor's input contract)

`LoadpullEngine.BuildLoadpullDataSet` emits, over axes `{gridPoint[, pinStep]}` (+ `{node, harmonic}` for
spectra):

| Cube | Axes | Kind | Meaning |
|---|---|---|---|
| `Pout` | gridPoint, pinStep | Real | output power, **Watts** |
| `Gt`, `Gp` | gridPoint, pinStep | Real | transducer / power gain, dB |
| `DE`, `PAE` | gridPoint, pinStep | Real | efficiency (linear fraction) |
| `Pdc` | gridPoint, pinStep | Real | DC power, Watts |
| `BiasVLoad`/`BiasILoad`/`BiasVSrc`/`BiasISrc` | gridPoint, pinStep | Real | bias V/I |
| `Converged`, `IsTickle`, `PavlDbm` | gridPoint, pinStep | Real | bookkeeping / power axis |
| `GammaLoad`, `ZLoad` | gridPoint | Complex | load termination (the Γ/Z plane coordinate) |
| `StopCode` | gridPoint | Real | per-grid stop reason |
| `V`, `INl` | gridPoint, pinStep, node, harmonic | Complex | interface spectra |

**New engine provenance (this work):** two rank-0 metadata cubes naming the DUT interface nodes so a
DataSet-only consumer can locate the input/output ports without re-deriving topology:

| Cube | Kind | Meaning |
|---|---|---|
| `__SrcNodeIdx` | Real (scalar) | index into the `node` axis of the **source-side DUT** node (DUT input / gate). −1 if unknown. |
| `__LoadNodeIdx` | Real (scalar) | index into the `node` axis of the **load-side DUT** node (DUT output / drain). −1 if unknown. |

These mirror `ctx.SrcIfIdx`/`ctx.LoadIfIdx` already used internally by `ComputeFoms`. They are `__`-prefixed
(metadata: hidden from pickers, passed through `StackSweepAxis` unstacked). They are **provenance, not a
derived FOM** — the engine knows the topology; recording it is free and avoids the post-processor guessing.

---

## 3. The derived fields (Phase 1)

`LoadpullPostProcessor.Enrich(DataSet ds, string group = "")` returns the same `DataSet` with these cubes
added to `group` (presence-gated — never overwrites an existing key; no-op when an input is absent):

### 3.0 Canonical field names (user-facing)

Both the post-processor (simulated) and the `.spl`/`.lpcwave` readers (measured) emit ONE canonical,
unit-suffixed name set so the display layer serves both interchangeably. There is **no bare `Pout`**
(ambiguous W vs dBm). The post-processor renames the engine's raw cubes (drops the old names via
`DataSet.RemoveFromGroup`):

| Canonical | Unit | From engine raw |
|---|---|---|
| `Pout_dBm` | dBm | `Pout` (W) → 10·log10(W)+30 |
| `Pout_W` | W | `Pout` (W) |
| `Gt_dB`, `Gp_dB` | dB | `Gt`, `Gp` |
| `Efficiency` | % | `DE` (fraction × 100) |
| `PAE` | % | `PAE` (fraction × 100) |
| `Pdc_W` | W | `Pdc` |
| `AMPM_deg` | deg | derived (spectra) |
| `IRL_dB` | dB | derived (spectra) |
| `Zin_real`, `Zin_imag` | Ω | derived (spectra) |
| `BiasVLoad`/`BiasVSrc` | V | unchanged |
| `BiasILoad`/`BiasISrc` | A | unchanged name, sign-flipped → +Idq |

The measured side maps file columns to these names in `LoadpullFomDialect.Map` (PassThrough scales — the
stored value stays in the displayed unit) and derives `Pout_W` from `Pout_dBm` in `LoadpullDerivedFields`.

### 3.1 `Pout_dBm` — output power in dBm
`Pout_dBm = 10·log10(Pout_W) + 30`, element-wise over `{gridPoint, pinStep}`. NaN where `Pout ≤ 0` or NaN.
This is the headline contour metric most users expect (the `LoadpullSurface` Γ-plane contour of saturated
output power). The engine already emits `Pout` in Watts; the surface previously converted W→dBm for display,
but a discrete `Pout_dBm` cube gives parity with the reader (which emits `Pout_dBm` natively) and a directly
selectable metric.

### 3.2 `Zin_real` / `Zin_imag` — input impedance (Ω)
Computed by `LoadpullDerivedFields.Derive` from the **input reflection coefficient** Γin (mag + phase):
`Zin = G2Z(Γin)·50`. The post-processor produces Γin from the engine spectra at the source-DUT node,
fundamental (harmonic index 1):

```
Zin(gi,pi)  = V[gi,pi, srcNode, 1] / INl[gi,pi, srcNode, 1]      // input impedance looking INTO the DUT
Γin(gi,pi)  = Z2G( Zin / 50 )                                     // normalized reflection coefficient
ginMag      = |Γin| ,  ginPhaseDeg = ∠Γin (deg)
```

Sign convention: `INl[n,k]` is current **into** the device at node n (passive sign, `src/Engine/HarmonicBalance/
CLAUDE.md`), so `V/INl` at the source node is the impedance presented to the source by the DUT input —
exactly `Zin`. (The pursuit engine's auto-Zsource uses the same quantity; `Zsource = conj(Zin)`.) Points
with zero/NaN input current → NaN (dropped downstream, same as a measured NaN).

### 3.3 `IRL_dB` — input return loss (dB)
`LoadpullDerivedFields` priority: stored dB → stored linear → derive from |Γin|. For the simulated path only
the last applies: `IRL_dB = +20·log10(|Γin|)`. **Sign convention (RF-engineer / S11-style): a good input
match is NEGATIVE** (−200 dB ≈ perfect match, 0 dB = full reflection, > 0 = reflection gain / active).

### 3.4 `AMPM` — AM-to-PM conversion (deg)
The drive-up change in the DUT's transmission phase, per grid point, unwrapped (the reader's definition):
`AMPM[gi,pi] = trans_phase[gi, 0] − trans_phase[gi, pi]`, then `UnwrapDegInPlace`. The post-processor supplies
`trans_phase` from the spectra:

```
trans_phase(gi,pi) = ∠V[gi,pi, loadNode, 1] − ∠V[gi,pi, srcNode, 1]   (deg)   // through phase
```

AM/PM is a **relative** quantity (referenced to the lowest-drive point of each grid sweep), so the absolute
phase reference cancels — any consistent transmission-phase definition yields the same AM/PM curve.

### 3.5 Reuse, don't fork
The Zin/IRL/AMPM math lives **only** in `LoadpullDerivedFields.Derive`. The post-processor's job is to (a)
compute the *raw inputs* (`ginMag`, `ginPhaseDeg`, `transPhaseDeg`) from the simulated spectra, then (b) call
the shared helper — byte-identical to how `SplReader`/`LpcwaveReader` capture those inputs from file columns
and call the same helper. This guarantees simulated and measured derivations agree.

### 3.6 Display-convention fixes (simulated runs only)
The engine reports two quantities in physics conventions that differ from what the Summary Table and
users expect; the post-processor corrects them **in place**, gated on `__SrcNodeIdx` (engine output only —
a measured `.spl`/`.lpcwave` already carries these in display form and has no such marker):

- **Idq sign:** the engine stores `BiasILoad`/`BiasISrc` with the passive sign (current *into* the device
  node → drain Idq negative). The post-processor negates them so Idq displays **positive**.
- **Efficiency units:** the engine emits `DE`/`PAE` as a 0..1 **fraction**; the post-processor multiplies by
  **100** so they display in **%** (matching the measured `.spl` `Eff[%]`/`PAE[%]` columns and the Summary
  Table header). The MXE search argmax is unaffected (monotonic scale).

These are the only in-place mutations; everything else is additive.

### 3.7 The `__Freq` carrier (engine provenance)
The LP FOM cubes carry no `freq` axis (single-frequency), so `LoadpullSurface` would report Freq = 0. The
**engine** emits a rank-1 `__Freq {freq}=[ToneHz]` carrier cube (same convention the `.spl` reader uses);
`LoadpullSurface.BuildFreqSlices` recovers the tone from it. This is engine provenance (like
`__SrcNodeIdx`), not a post-processor output — the post-processor doesn't know the tone frequency.

### 3.8 Summary-table metric registration (consumer requirement)
`LoadpullSurface` only loads cubes named in its `metricNames` whitelist into the per-grid drive-up store
that `Fit`/`MetricAtCoord` read. The derived metrics (`Pout_dBm`, `Zin_real`, `Zin_imag`, `AMPM`, `IRL`)
**must be in that whitelist** or the summary's `MetricAtCoord` returns NaN. The Summary Table's Power
column binds to **`Pout_dBm`** (not `Pout`, which is Watts) so its "Power (dBm)" header matches its values.

### 3.9 Presence-gating & idempotence
Additive outputs are added only if absent; the in-place fixes (§3.6) run once — a `__lpEnriched` sentinel
makes a repeat `Enrich` a full no-op (so the sign flip / ×100 are never applied twice). Inputs gate each
output (`__SrcNodeIdx` ≥ 0 + `V`/`INl` present → Zin/IRL/AMPM; `Pout` present → `Pout_dBm`). A DataSet with no
spectra (e.g. a measured `.spl` that already carries the derived fields) is returned unchanged — so the
post-processor is safe to run on any loadpull DataSet.

---

## 4. Group-awareness

A simulated LP `run.npy` nests its cubes under the analysis-name group (e.g. `LP1`); a flat `.spl` is at top
level (`group = ""`). `Enrich(ds, group)` reads and writes within the given group (group-qualified specs,
`ds["{group}.{name}"]`), mirroring `LoadpullSurface(ds, group)` (brief 09) and `LoadpullRecognition`
(brief 08). When the run pipeline enriches the engine's **flat** DataSet (before grouping), `group = ""`.

---

## 5. Where it runs

The **run pipeline** invokes the post-processor immediately after the engine, on the flat result, before the
results are grouped and written to `run.npy`:

```
SchematicRunService.RunNetlist:
    var ds = new LoadpullEngine(nl, tb).Run(p);
    ds = LoadpullPostProcessor.Enrich(ds);          // ← derived metrics added here
    // → RunResultsWriter groups ds under the analysis name → run.npy
```

So the persisted `run.npy` already carries the derived cubes, and the Data Display reads them with no
ingest-time work (and brief 08/09 recognition + group-aware surface pick them up automatically). The same
applies to the Loadpull-Pursuit follow-on `LoadpullData`.

**Engine stays pure:** `LoadpullEngine` gains only the two `__*NodeIdx` provenance cubes; it computes no
derived display metric.

---

## 6. Phase 2 — export to `.spl` / `.lpcwave`

The enriched `DataSet` is the single source for export. A `SplWriter` (and `LpcwaveWriter`) consumes it and
emits the vendor column layout the readers parse, so a simulated run can be saved as a measured-style file
and re-opened (round-trip) or handed to external tools.

**Column mapping (`.spl`, HarmonicaRF layout — from `SplReader`):** per (grid point, drive step):
`Pavl[dBm]` (← `PavlDbm`), `Pout[dBm]` (← `Pout_dBm`), `Gt[dB]`/`Gp[dB]` (← `Gt`/`Gp`), `Eff[%]`/`PAE[%]`
(← `DE`/`PAE`×100), `Pdc[W]`, `gamma_ld` RI pair (← `GammaLoad`), `Gin` mag/phase (← Γin from `Zin`),
`trans_phase` (← from `AMPM` reconstruction or stored), bias columns (← `Bias*`). One block per frequency
(simulated runs are single-frequency today → one block); `ZSource` rank-1 `{freq}` from the source match.

**Open items for Phase 2:** exact `.spl` header/column spelling and required vs optional columns (derive from
`SplReader.Parse`, do not invent); `.lpcwave` wave-quantity columns (`PoutWaves[dBm]` etc.); a writer↔reader
round-trip test (write enriched sim DataSet → `SplReader.ReadSpl` → assert cube parity); wiring an Export
action in the Data Display / a CLI export verb. The post-processor (Phase 1) is a prerequisite — the writer
serializes its output, it does not re-derive.

---

## 7. Testing

- **Derived-field unit tests** (`LoadpullPostProcessor`): a synthetic engine-shaped DataSet (with `V`/`INl`,
  `__SrcNodeIdx`/`__LoadNodeIdx`, `Pout`) → `Enrich` adds `Pout_dBm`, `Zin_real`/`Zin_imag`, `IRL`, `AMPM`;
  values match hand-computed `LoadpullDerivedFields` output; idempotent re-run; no-op when inputs absent.
- **dBm correctness:** `Pout_dBm = 10log10(Pw)+30` (a known Pout → known dBm) — guards against a W↔dBm slip.
- **End-to-end (run pipeline):** a small LP run → enrich → the Data Display contour metric list offers
  `Pout`/`Pout_dBm`/`Zin_real`/`Zin_imag`/`IRL`/`AMPM` (extends the metric-list regression test).
- **Phase 2:** the writer↔reader round-trip parity test above.

---

## 8. Non-goals / boundaries

- **No engine FOM migration.** `Gt`/`Gp`/`DE`/`PAE`/`Pdc` stay in the engine (they need the converged solve +
  bias the engine alone has). A future refactor *could* move all FOM math into the post-processor; not now.
- **No new on-disk format** for `run.npy` — derived cubes are ordinary `DataSet` cubes, exported by the
  existing `.npy` path. Firewall: `LoadpullPostProcessor` is headless RfCore (no Avalonia); only the run
  pipeline call site is in `src/Ui`.
- **Multi-frequency:** the engine is single-frequency per LP run today; the post-processor and the Phase-2
  writer carry a `freq` axis only if the engine begins emitting one (the readers already support `{freq, …}`).
