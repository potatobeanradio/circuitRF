# Design — Loadpull Performance Summary Table (Phase 7.5)

Status: design (pre-implementation). Owner: Developer. Supersedes nothing; extends the Table plot type
and the loadpull surface engine (Phase 7.4).

This document specifies a new use of the **Table** plot type: a per-frequency summary of loadpull DUT
performance at the **MXP** (max output power) or **MXE** (max efficiency) load termination, at a chosen
compression. It is the in-app equivalent of the reference PowerPoint "Performance Summary" table generator.

---

## 1. Concept

A **Summary Table** is a Table-type plot whose traces are *performance metrics* (not curves). Each trace adds
one column. Each **row is one frequency** of the loadpull dataset (exactly one row per measured frequency — no
drive-up expansion). Every column is evaluated at the **same optimum load termination** for that frequency:
either MXP or MXE, selected once for the whole table.

Example: user creates a Table plot, sets the table optimum to **MXP**, adds a "Pout" summary at compression =
3 dB. The table shows:

| Freq (GHz) | Pout (dBm) |
|-----------:|-----------:|
| 1.8        | 36.2       |
| 1.9        | 36.0       |
| 2.0        | 35.7       |

The user keeps adding metric columns (Eff, Gain, AM/PM, ZL, ZS, Zin, IRL, …). An **auto-fill button** adds the
full standard column set in one click.

This reuses the Phase 7.4 `LoadpullSurface` engine: MXP/MXE give the optimum load coordinate per frequency, and
each metric is read at that coordinate (interpolated or nearest-measured, user's choice).

---

## 2. UX

### 2.1 Table optimum selector (MXP / MXE) — NEW control, table-wide
- A new **MXP / MXE** selector is added to the **Plot Properties header**, placed **to the left of the
  `Plot.FreqUnits` selector**.
- It applies to the **whole table** (all summary columns use the same optimum).
- This is a *distinct* control from the existing per-contour `DisplayMxp`/`DisplayMxe` toggles (those only
  control whether the MXP/MXE glyphs render on a contour plot). The new selector chooses *which optimum load
  the table evaluates at*.
- Visible only when `PlotType == Table`. (For non-Table plots the header keeps its current layout.)

### 2.2 Interp / Nearest selector — NEW control, table-wide
- A second new selector lets the user choose how each metric is read at the optimum load:
  - **Interp** — evaluate the metric's RBF surface at the (interpolated) optimum coordinate.
  - **Nearest** — use the value at the nearest *measured* grid point to the optimum.
- Table-wide (applies to every column). Placed in the Plot Properties header near the MXP/MXE selector.

### 2.2b Compression control — table-wide (NEW)
- A single **compression** control governs the whole table. Changing it updates the compression of **every**
  summary trace at once.
- In a loadpull Summary Table, the **per-trace compression field in each trace card is disabled and greyed
  out** — the user cannot set compression individually; it is driven solely by the table-wide control. (This
  supersedes the earlier "each column carries its own compression" idea: compression is one shared value.)
- The title's `{x}` compression token (§2.6) reads directly from this single control, so it is always
  unambiguous.

### 2.3 "+ Summary" button (replaces "+ Contour" for Table)
- When `PlotType == Table`, the existing **"+ Contour"** button relabels to **"+ Summary"**.
- "+ Summary" adds one summary column (a metric trace). Default metric = Pout; default compression = the card's
  current/last value (fall back to the dataset's recommended compression).

### 2.4 Auto-fill button (standard column set)
- A button (in the Table Properties header or trace area) **auto-fills the standard performance columns** in one
  action, in this exact order (see §4 for the full set). It uses:
  - **one shared compression** for all added columns (the card's current compression value), and
  - **one optimum** — all MXP **or** all MXE (whichever the table selector currently is).
- **Presence-gated:** any column whose backing cube is absent from the dataset is **silently skipped** (no empty
  cells). E.g. if `Zin_real`/`Zin_imag` are missing, the Zin column is omitted.

### 2.5 One row per frequency
- The Table renderer must emit exactly **one data row per loadpull frequency** for summary traces. (Contrast
  with cube-bound Table traces today, which expand the swept X axis into many rows.) Summary traces are
  frequency-indexed; the frequency column is the X/anchor column.

### 2.6 Default title (top-right, right-aligned)
- A Summary Table auto-generates a default plot **Title**:
  - MXP table → `Max P-{x}dB Power Load`
  - MXE table → `Max P-{x}dB Efficiency Load`
  - where `{x}` is the compression level (e.g. `Max P-3dB Power Load`).
- The title is **aligned to the top-right** of the table, and the **text is horizontally right-aligned**.
  (This differs from the centered titles on Rect/Smith/Polar.) The user's CustomTitle still overrides when set.
- Compression is a single table-wide value (§2.2b), so `{x}` is unambiguous — it is that one value.

### 2.7 Trace-card trimming for Table summary traces
When the plot is a Table, the contour/summary trace card **removes** these rows/controls (they don't apply and
free space for summary-specific fields):
- Levels (num-levels OR start:stop:step)
- Show Max Power, Show Max Efficiency
- Fill **colormap** selector
- Fill **color style** selector
- the entire **Lines** row
- the entire **Grids** row
- the entire **Labels** row

The card **keeps** (and adds): metric selector and any units/format needed. The per-trace **compression field
is shown but disabled/greyed** (driven by the table-wide compression control, §2.2b). The MXP/MXE,
Interp/Nearest, and compression controls are all table-wide in the header, NOT per-card.

---

## 3. Per-cell computation (engine)

For frequency `j` and the table's chosen optimum (MXP or MXE) at compression `c`:

1. **Optimum load coordinate.** Use `LoadpullSurface.MaxPower(j, AtCompression(c), plane, z0)` for MXP, or
   `MaxEfficiency(...)` for MXE. This returns `MxxResult { Measured, Interpolated }` (Γ or Z per plane). The
   optimum **load impedance `ZL`** is that coordinate converted to Z (G2Z·z0 when in Γ-plane).
   - **Interp** mode → use `MxxResult.Interpolated`.
   - **Nearest** mode → use `MxxResult.Measured` (the argmax measured node).

2. **Metric value at the optimum.** For each metric column, evaluate that metric's surface at the optimum
   coordinate:
   - **Interp** → `Fit(j, metric, AtCompression(c), plane, z0).Rbf.Evaluate(opt.Re, opt.Im)`.
   - **Nearest** → the metric's measured node value at the nearest measured grid point to the optimum (the same
     node selected as `Measured`). (Engine may add a small helper to return the metric value at a given node /
     nearest node.)
   This mirrors the reference Python: `val = DataInterp[j, 'z_load', key_y, 'Compression', PxdB](ZL.re, ZL.im)`.

3. **Impedance columns (complex).** ZL, ZS, Zin render as complex `"R+jX Ω"` (use existing complex formatting,
   2-decimal real/imag like the reference `complex2str(prec=[2,2])`).
   - **ZL** = the optimum load impedance from step 1 (single value per frequency).
   - **ZS** (source) = the dataset's per-frequency source impedance, assumed **constant across load
     terminations** (reference: `Data[j, 0, 'z_source_1f0']`). Read the source-Z cube directly at frequency `j`
     (first/any grid point). If only a *target* Zsource exists, use that.
   - **Zin** = interpolate `Zin_real` and `Zin_imag` **separately** at the optimum load `ZL`, then combine:
     `Zin = Zin_real(ZL) + j·Zin_imag(ZL)`. Requires both `Zin_real` and `Zin_imag` cubes; omit the column if
     either is missing.

4. **Other scalar metrics** (Pout dBm, Eff %, Gain dB, AM/PM °, IRL dB, VDD, IDQ) — evaluate per the metric's
   surface/source as in step 2, formatted with the metric's unit.

---

## 4. Standard column set (auto-fill order)

From the reference table generator, the full standard set in order:

| # | Header           | Source / cube                          | Type / format     | Notes |
|---|------------------|----------------------------------------|-------------------|-------|
| 1 | Freq (`{unit}`)  | dataset frequency axis                 | real, `G5`        | anchor column, one row per freq |
| 2 | VDD (V)          | dataset `VDD` (per-freq operating pt)  | real              | omit if absent |
| 3 | Idq (mA)         | dataset `IDQ` (per-freq operating pt)  | real (int if >10) | omit if absent |
| 4 | Zsource (Ω)      | source-Z cube (per-freq, const-load)   | complex `R+jX`    | omit if absent |
| 5 | Zin (Ω)          | `Zin_real` + j·`Zin_imag` at `ZL`      | complex `R+jX`    | omit if either cube absent |
| 6 | Zload (Ω)        | optimum `ZL` from MXP/MXE              | complex `R+jX`    | always present (derived) |
| 7 | Power (dBm)      | `Pout` surface at optimum              | real `F1`         | metric alias `Pout` |
| 8 | Efficiency (%)   | `DE`/`Eff` surface at optimum          | real `F1`         | metric alias Efficiency |
| 9 | Gain (dB)        | `Gt` surface at optimum                | real `F1`         | metric alias Gain (Gt) |
| 10| AM/PM (°)        | `trans_phase`/AMPM surface at optimum  | real `F1`         | omit if absent |
| 11| Input Return Loss| `IRL` (or derived from Zin/ZS)         | real `F1` dB      | omit if absent (see open item) |

Notes:
- The metric **alias** system from Phase 7.4h round 5 applies (Pout, Efficiency[DE/DEff/Eff], Gain[Gt/Gt_dB],
  AMPM[trans_phase], PAE, Gp, Zin_real, Zin_imag). Reuse it to resolve headers→cubes.
- Columns 2–5, 10, 11 are **presence-gated**: omitted entirely when their cube is missing.
- Columns 6–9 derive from the core loadpull cubes the engine already reads (`ZLoad`, `Pout`, `DE`, `Gt`) and are
  effectively always available for a valid loadpull dataset.

The earlier short list in the request (ZL, ZS, Zin, Pout, Eff, Gain, AM/PM, IRL) is a subset of this; this table
is authoritative (adds Freq/VDD/Idq as leading columns, matching the reference generator).

---

## 5. Engine additions (RfCore — headless)

`LoadpullSurface` today reads cubes: Pout, Gt, Gp, DE, PAE, PavlDbm, GammaLoad, ZLoad. The summary table needs
new reads + helpers, all in RfCore (firewall: no Avalonia):

1. **Source impedance per frequency.** Read a source-Z cube (name TBD — see open items; reference key
   `z_source_1f0`). Expose `SourceZ(freqIdx) → Complex?` (null if absent). Assumed constant across load
   terminations; read at grid point 0 (or a dedicated per-freq cube).
2. **Zin interpolation.** Allow `Zin_real` and `Zin_imag` as fittable metrics (they already flow through the
   generic metric path if present in the cube set — confirm `BuildFreqSlices` includes them, or add them to the
   optional metric list). Expose a helper `MetricAt(freqIdx, metric, ZLcoord, constraint, mode)` returning the
   interpolated-or-nearest value, so the UI composes `Zin = MetricAt("Zin_real") + j·MetricAt("Zin_imag")`.
3. **Generic "metric at coordinate" accessor.** Add
   `double MetricAtCoord(int freqIdx, string metric, Complex coord, ConstraintSpec constraint, SurfacePlane,
   double? z0, bool nearest)` — Interp evaluates the metric RBF at `coord`; Nearest returns the metric node
   value at the nearest measured node to `coord`. This is the single primitive the table cells call.
4. **Operating-point passthroughs** (VDD, IDQ): these are per-frequency scalars, not surfaces. Expose
   `OperatingPoint(freqIdx, name) → double?` reading a per-freq cube if present (omit column if absent).
5. **MXP/MXE already exist** (`MaxPower`/`MaxEfficiency`) — reuse for the optimum coordinate. No change beyond
   honoring kernel/smooth/epsilon (already threaded in 7.4h round 6).

All additions are presence-tolerant: missing cube → `null`/omit, never throw.

---

## 6. UI / model additions (src/Ui)

1. **Table-wide state on the Plot (or DataDisplay) model:** `TableOptimum { Mxp, Mxe }` and
   `TableReadMode { Interp, Nearest }`. Persisted in `.cdd` (defaulted/nullable per alpha no-back-compat).
2. **Summary trace data:** a Table summary column needs a small authoring record (metric name + compression +
   format), analogous to but lighter than `ContourData`. Either reuse `ContourData` with Table-irrelevant fields
   ignored, or add a `SummaryColumnData` record. Recommended: a dedicated `SummaryColumnData` (MetricName,
   Compression, ColumnKind {Metric, Zload, Zsource, Zin, OperatingPoint}, Format) to keep the model honest and
   the trimmed card simple. Persisted in `TraceConfig` (new optional block, mirrors `ContourTraceConfig`).
3. **TableRenderer:** add a summary path that, per column, asks the engine for the per-frequency value and emits
   exactly one row per frequency. Complex columns format as `R+jX Ω`. Right-aligned, top-right default title.
4. **Header controls:** MXP/MXE selector + Interp/Nearest selector, gated to `IsTablePlot`, left of FreqUnits.
5. **Button relabel + auto-fill:** "+ Contour" → "+ Summary" when Table; auto-fill command builds the §4 column
   set, presence-gating each.
6. **Trace card:** hide the §2.7 rows when the trace is a Table summary trace.

---

## 7. Persistence

- New fields default-initialized (alpha no-back-compat: add nullable/defaulted fields, loaders reject only on
  `format_version` mismatch). `TableOptimum`, `TableReadMode`, and `SummaryColumnData` per trace are written and
  read; older `.cdd` without them loads as a normal Table.

---

## 8. Cube-name mapping (resolved from `LoadpullFomDialect.cs`)

The circuitRF loadpull importer (`src/RfCore/Loadpull/LoadpullFomDialect.cs`) maps measured columns to these
canonical cubes (plus `GammaLoad`/`ZLoad` built by `BuildLoadpullDataSet`):

| Reference name (Python) | circuitRF canonical cube | Notes |
|-------------------------|--------------------------|-------|
| `Pout_dBm`              | `Pout` (stored W)        | metric surface |
| `Gt_dB`                 | `Gt`                     | metric surface (Gain) |
| `Gp_dB`                 | `Gp`                     | metric surface |
| `Eff_%`                 | `DE`                     | metric surface (Efficiency) |
| `PAE`                   | `PAE`                    | metric surface |
| `Pin_avail_dBm`         | `PavlDbm`                | sweep axis basis |
| `z_load_1f0`            | `ZLoad` / `GammaLoad`    | optimum-load coordinate |
| `VDD` (drain voltage)   | **`BiasVLoad`**          | load-side drain bias V → VDD column |
| `IDQ` (drain quiescent) | **`BiasILoad`**          | load-side drain bias I → Idq column (stored A; ×1000 for mA) |
| (gate bias V)           | `BiasVSrc`               | source-side gate bias V |
| (gate quiescent I)      | `BiasISrc`               | source-side gate bias I |
| `z_source_1f0`          | **absent**               | NOT imported today → Zsource column omitted until added |
| `Zin_real` / `Zin_imag` | **absent**               | NOT imported today → Zin column omitted until added |
| `trans_phase` (AM/PM)   | **absent**               | NOT imported today → AM/PM column omitted until added |
| IRL                     | **absent**               | NOT imported today → see item 2 |

Consequences for the standard column set (§4):
- **Always available** (core cubes present): Freq, Zload (from `ZLoad`/MXP-MXE), Power (`Pout`), Efficiency
  (`DE`), Gain (`Gt`), and also PAE/Gp if a user adds them.
- **VDD / Idq** are available, sourced from `BiasVLoad` / `BiasILoad` (the load-side drain bias). These are
  per-frequency operating points read directly (not surface metrics). NOTE: in the current cube they are indexed
  by `{freq, gridPoint, pinStep}` like the FOMs, so "the VDD/Idq value" must be reduced to a single per-freq
  number — take the value at the optimum load grid point and the relevant pin step (or the quiescent/low-drive
  pin step for a true *quiescent* Idq). **Confirm the reduction** (see item 4).
- **Zsource, Zin, AM/PM, IRL** are NOT in the importer today, so per the presence-gating rule these columns are
  **automatically omitted** until the importer adds them. The feature should be built so they light up the
  moment those cubes appear — i.e. wire them by canonical name now, gate on presence.

### Resolved decisions (all open items closed)

1. **IRL (Input Return Loss).** **Derive** when not present as a stored cube. Stored vendor aliases to honor
   first (use directly if present): `Refl_dB` or `ReflectCoefficient_dB` (both already in dB), or
   `ReflectCoefficient` (linear → `20·log10`). Otherwise derive from the input reflection coefficient Γin
   (the same Γin source used for Zin — see §10): `IRL = -20·log10(|Γin|)`. Presence-gated only when neither a
   stored IRL alias nor Γin is available. (`Refl_dB` already appears in SPLData's priority list, confirming it
   as a genuine data column.)
2. **VDD / Idq reduction — use the FIRST sample in the sweep (confirmed).** Idq is constant over the Pin sweep,
   so take `BiasILoad` at the first pinStep; VDD likewise from `BiasVLoad` at the first pinStep. Mirrors the
   reference (`self.Data.VDD[j] = Data[z,0,guess][0]` — first grid point, first sweep sample). Read at grid
   point 0, pinStep 0. Idq displayed in mA (cube stores A → ×1000); VDD in V.
3. **Harmonic loadpull — DEFERRED (confirmed).** Single-`ZL`-per-frequency targets fundamental (1f0) only. If
   harmonic-indexed load cubes are detected, use the fundamental and optionally warn.
4. **Compression — single table-wide value (confirmed).** One compression control for the whole table; per-trace
   compression fields are disabled/greyed (§2.2b, §2.7).
5. **Nearest mode for non-surface columns (confirmed).** ZS, VDD, Idq read directly per frequency and ignore
   the Interp/Nearest selector (it applies only to surface metrics evaluated at the optimum).

---

## 9. Slice plan (open items closed — ready to brief)

1. **7.5a — engine accessors (RfCore):** `MetricAtCoord`, `SourceZ`, `OperatingPoint`, Zin compose helper;
   presence-tolerant. Unit-tested headless against a sample loadpull DataSet.
2. **7.5b — model + persistence:** `TableOptimum`, `TableReadMode`, table-wide `Compression`,
   `SummaryColumnData`, config round-trip.
3. **7.5c — TableRenderer summary path:** one row per freq, complex `R+jX` formatting, right-aligned top-right
   title.
4. **7.5d — header controls + "+ Summary" relabel + card trimming** (incl. per-trace compression disabled/greyed).
5. **7.5e — auto-fill standard column set (presence-gated).**
6. **7.5f — polish:** title compression formatting, harmonic-loadpull guard/warn.
7. **7.5g — importer derived fields (RfCore):** add Zin_real/Zin_imag, AM/PM, IRL derivations to the loadpull
   importer (§10). Independent of the UI slices — can land first so the new columns are populated. Unit-tested.

---

## 10. Importer additions — derived fields (RfCore loadpull importer)

To make Zin, AM/PM, and IRL appear in the summary table, the loadpull importer must derive them at import time
(mirroring `SPLData.py.__init__`). All are **presence-gated on their inputs** — absent input → cube not created
→ column auto-omitted. These are headless RfCore changes (no Avalonia). The derivations below are ported
verbatim from `loadpull-contours-refs/SPLData.py`.

### 10.1 New dialect input columns
`LoadpullFomDialect.Map` (or the loadpull reader) must recognize the vendor input columns that feed the
derivations. Add entries (canonical name + scale):

| Vendor column(s)                                   | Purpose                    | Canonical / handling |
|----------------------------------------------------|----------------------------|----------------------|
| `Gamma_in_mag` + `Gamma_in_phase`                  | Γin (mag, phase°)          | → derive `Zin_real`/`Zin_imag` (10.2) + IRL (10.4) |
| `|GinWaves@F0|` + `PhiinWaves@F0[deg]`             | Γin (mag, phase°), alt names | same |
| `trans_phase` or `PhiLWaves@F0[deg]`               | transducer phase drive-up  | → derive `AMPM` (10.3) |
| `Refl_dB`, `ReflectCoefficient_dB`                 | input return loss (dB)     | → `IRL` passthrough |
| `ReflectCoefficient`                               | reflection coeff (linear)  | → `IRL = 20·log10(·)` |
| `z_source_1f0` / `gamma_source_1f0` (per-freq)     | source termination         | → `Zsource` (per-freq, const across loads) |

> Names vary by vendor/dialect; honor the `.spl` and `.lpcwave` variants SPLData lists. Keep the
> "first present wins" alias order from SPLData.

### 10.2 Zin_real / Zin_imag (from Γin)
From SPLData (mag/phase° → cartesian Γin → Z, 50Ω denorm):
```python
x, y = pol2cart(GinPhase_deg * pi/180, GinMag)   # (mag, phase°) -> cartesian
Zin  = g2z(x + 1j*y) * 50                          # Γin -> Z, 50Ω normalization assumed
Data['Zin_real'] = Zin.real
Data['Zin_imag'] = Zin.imag
```
C# port: for each (freq, gridPoint, pinStep) sample, `Complex gin = Complex.FromPolarCoordinates(ginMag,
ginPhaseDeg * Math.PI/180); Complex zin = RfHelpers.G2Z(gin) * 50.0;` store real/imag into two new real cubes
`Zin_real`, `Zin_imag` with the same `{freq, gridPoint, pinStep}` shape. Created only when a Γin column pair is
present.

### 10.3 AM/PM (from transducer phase drive-up)
From SPLData (phase referenced to first/low-drive sample, then unwrapped):
```python
AMPM = trans_phase[0] - trans_phase                 # relative to first (low-drive) point
AMPM = 180/pi * unwrap(AMPM * pi/180)                # phase unwrap in radians, back to degrees
```
C# port: per grid point, per pinStep drive-up: subtract the first sample, convert to radians, `np.unwrap`
equivalent, convert back to degrees. Store as real cube `AMPM` (degrees), shape `{freq, gridPoint, pinStep}`.
Created only when `trans_phase`/`PhiLWaves@F0[deg]` is present AND `AMPM` is not already present. (Need a small
phase-unwrap helper if RfCore lacks one.)

### 10.4 IRL (input return loss)
Priority:
1. If a stored IRL alias is present (`Refl_dB` / `ReflectCoefficient_dB`), use it directly (dB).
2. Else if `ReflectCoefficient` (linear) present, `IRL = 20·log10(value)`.
3. Else if Γin available (the Zin source), `IRL = -20·log10(|Γin|)` per sample.
4. Else omit.
Store as real cube `IRL` (dB), shape `{freq, gridPoint, pinStep}`. For the summary cell, IRL is a surface metric
(evaluated at the optimum like Pout/DE) OR — if more appropriate — read at the optimum node; treat it as a
standard metric surface via the alias system.

### 10.5 Zsource (per-frequency source termination)
SPLData derives source/load terminations into per-freq complex values (`z_source_1f0`), assumed constant across
load terminations. If the importer already builds a source-Γ/Z (it builds `GammaLoad`/`ZLoad` for the load), add
the analogous source-side `ZSource` per-freq cube when source-termination columns are present (`gamma_src1_*`,
`|GS@F0|`/`PhiS@F0[deg]`, etc., per SPLData's `TerminationName` map). The summary reads it directly per freq.

### 10.6 Alias registration
Register the new canonical cubes in the Phase-7.4h metric alias system so the summary column headers resolve:
`Zin_real`, `Zin_imag`, `AMPM` (→ "AM/PM (°)"), `IRL` (→ "Input Return Loss"), `ZSource` (→ "Zsource (Ω)").

> Verification: after importing a dataset that carries Γin / trans_phase / Refl_dB, the new cubes appear in the
> DataSet, and the summary auto-fill includes the Zin / AM/PM / IRL / Zsource columns. A dataset lacking those
> inputs still imports cleanly with those columns omitted.
