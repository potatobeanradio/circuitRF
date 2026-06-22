# Brief 7.5g — Loadpull importer derived fields: Zin, AM/PM, IRL, ZSource (RfCore, headless)

**Phase:** 7.5 (loadpull summary table). **Layer:** RfCore only — NO Avalonia/UI. **Depends on:** nothing
(can land first; populates cubes the summary table consumes). **Design:**
`circuitRF/docs/design/loadpull-summary-table.md` §10.

Goal: derive `Zin_real`/`Zin_imag`, `AMPM`, `IRL`, and `ZSource` cubes at loadpull import time so they appear
in the summary table. **All presence-gated on their inputs** — absent input → cube NOT created → summary column
auto-omitted. Reference derivations are ported verbatim from `loadpull-contours-refs/SPLData.py` (read-only).

The two loadpull readers share an identical `FreqBlock` → `AssembleDataSet` structure:
- `<workspace>/RfCore/src/Loadpull/SplReader.cs`
- `<workspace>/RfCore/src/Loadpull/LpcwaveReader.cs`

Both build a `FreqBlock` with `Dictionary<string,double[]> Foms` (canonical name → `double[nGrid*nPin]`,
row-major `gi*nPin + pi`), a `Complex[] GammaLoad` (per grid point), `NGrid`, `NPin`, `FreqGHz`. Then
`AssembleDataSet` turns each `Foms` entry into a `DataCube` over `{gridPoint, pinStep}` (single-freq) or
`{freq, gridPoint, pinStep}` (multi-freq), and adds `GammaLoad`/`ZLoad` over `{gridPoint}` / `{freq, gridPoint}`.

**Strategy:** capture the raw input columns (Γin mag/phase, transducer phase, reflection, source Γ/Z) into the
`FreqBlock` during parsing, then add a **shared derivation step** that computes the derived `Foms` entries
before `AssembleDataSet`. Because derived fields live in `Foms` (drive-up cubes) or a new per-grid complex
array, `AssembleDataSet` carries them into the DataSet with no further change (for the drive-up ones). ZSource
needs a small explicit add (it's per-freq, not a drive-up).

Implement once as a shared helper used by BOTH readers. Put the helper in a new file
`<workspace>/RfCore/src/Loadpull/LoadpullDerivedFields.cs`.

---

## Part A — capture raw input columns into FreqBlock

Both readers map header columns to canonical cubes via `LoadpullFomDialect.Map`, dropping unmapped columns. The
derivation inputs are currently unmapped/dropped. Capture them into new `FreqBlock` fields. Add to **both**
readers' private `FreqBlock` class:

```csharp
// Raw derivation inputs (per grid*pin drive-up unless noted). Empty/null when the column is absent.
public double[]? GinMag;     // |Γin|        (linear)         row-major gi*NPin+pi
public double[]? GinPhase;   // ∠Γin         (degrees)        row-major gi*NPin+pi
public double[]? TransPhase; // transducer phase (degrees)    row-major gi*NPin+pi
public double[]? ReflDb;     // input return loss (dB)        row-major gi*NPin+pi  (stored alias, if present)
public double[]? ReflLin;    // reflection coeff (linear)     row-major gi*NPin+pi  (stored alias, if present)
public Complex   SourceGamma = Complex.Zero;  // per-freq source Γ (first grid pt); see Part D
public bool      HasSourceGamma;
```

### A.1 — column recognition
The vendor input column names (first-present-wins, per SPLData):
- Γin: pair `Gamma_in_mag` + `Gamma_in_phase`, OR `|GinWaves@F0|` + `PhiinWaves@F0[deg]`.
- transducer phase: `trans_phase` OR `PhiLWaves@F0[deg]`.
- reflection (IRL): `Refl_dB` or `ReflectCoefficient_dB` (dB) ; `ReflectCoefficient` (linear).
- source Γ: `gamma_src1_real`/`gamma_src1_imag` (already gamma-expanded in SplReader, see note) OR
  `|GS@F0|`/`PhiS@F0[deg]`.

> **SplReader note:** `ExpandGammaHeader` expands only `gamma_src*`/`gamma_ld*` columns into `_real`/`_imag`
> pairs. `Gamma_in_mag`/`Gamma_in_phase` and `|GinWaves@F0|`/`PhiinWaves@F0[deg]` are plain columns (NOT
> expanded) — read them as ordinary columns by header name. Source Γ via `gamma_src1_real/_imag` IS expanded;
> via `|GS@F0|`/`PhiS@F0[deg]` is plain.

### A.2 — capture during the data-fill loop
In each reader's `ParseFreqBlock`, after the column-index map is built and during/after the row-fill loop,
populate the new `FreqBlock` fields by reading the relevant columns the same way FOMs are read (by absolute
column index in SplReader; by `dataOffset`-relative index in LpcwaveReader). Allocate each `double[nGrid*nPin]`
only when its column(s) exist; leave null otherwise. NaN for invalid/short rows (mirror the existing
`isValid`/NaN handling).

Do NOT scale these via the dialect (they're not in the Map). Capture raw values; the derivation step scales.

### A.3 — keep it localized
Capturing is reader-specific (column-index conventions differ), so do A.2 inline in each reader. Everything
after capture (the math) is shared in Part B.

---

## Part B — shared derivation helper (new file)

`<workspace>/RfCore/src/Loadpull/LoadpullDerivedFields.cs`:

```csharp
using System;
using System.Numerics;
using RfCore.Data;     // not strictly needed here; remove if unused

namespace RfCore.Loadpull
{
    /// <summary>
    /// Computes derived loadpull FOM cubes (Zin_real/Zin_imag, AMPM, IRL) from raw input columns,
    /// porting SPLData.py.__init__. All outputs are presence-gated on their inputs: when an input is
    /// absent the corresponding output is not produced. Operates per grid point on drive-up arrays.
    /// Shared by SplReader and LpcwaveReader.
    /// </summary>
    internal static class LoadpullDerivedFields
    {
        /// <summary>
        /// Add derived FOM arrays into <paramref name="foms"/> (keyed by canonical name, row-major
        /// gi*nPin+pi) from the raw input arrays. No-op for any input that is null.
        /// </summary>
        public static void Derive(
            System.Collections.Generic.Dictionary<string, double[]> foms,
            int nGrid, int nPin,
            double[]? ginMag, double[]? ginPhaseDeg,
            double[]? transPhaseDeg,
            double[]? reflDb, double[]? reflLin)
        {
            int n = nGrid * nPin;

            // ── Zin_real / Zin_imag ─────────────────────────────────────────
            // SPLData: x,y = pol2cart(GinPhase*pi/180, GinMag); Zin = g2z(x+jy)*50
            if (ginMag is not null && ginPhaseDeg is not null)
            {
                var zinRe = new double[n];
                var zinIm = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double mag = ginMag[i], phDeg = ginPhaseDeg[i];
                    if (double.IsNaN(mag) || double.IsNaN(phDeg)) { zinRe[i] = double.NaN; zinIm[i] = double.NaN; continue; }
                    double ph = phDeg * Math.PI / 180.0;
                    var gin = new Complex(mag * Math.Cos(ph), mag * Math.Sin(ph));
                    var zin = RfHelpers.G2Z(gin) * 50.0;     // normalized G2Z × 50Ω, per SPLData
                    zinRe[i] = zin.Real; zinIm[i] = zin.Imaginary;
                }
                foms["Zin_real"] = zinRe;
                foms["Zin_imag"] = zinIm;
            }

            // ── AMPM ─────────────────────────────────────────────────────────
            // SPLData (per grid point drive-up): AMPM = trans_phase[0] - trans_phase, then unwrap (deg).
            if (transPhaseDeg is not null)
            {
                var ampm = new double[n];
                for (int gi = 0; gi < nGrid; gi++)
                {
                    int baseI = gi * nPin;
                    double first = transPhaseDeg[baseI];
                    // relative to first sample
                    var rel = new double[nPin];
                    for (int pi = 0; pi < nPin; pi++)
                    {
                        double v = transPhaseDeg[baseI + pi];
                        rel[pi] = (double.IsNaN(v) || double.IsNaN(first)) ? double.NaN : (first - v);
                    }
                    UnwrapDegInPlace(rel);                  // np.unwrap equivalent, NaN-aware
                    Array.Copy(rel, 0, ampm, baseI, nPin);
                }
                foms["AMPM"] = ampm;
            }

            // ── IRL (input return loss, dB) ──────────────────────────────────
            // Priority: stored dB alias → stored linear alias (20log10) → derive from Γin (-20log10|Γin|).
            if (reflDb is not null)
            {
                foms["IRL"] = (double[])reflDb.Clone();
            }
            else if (reflLin is not null)
            {
                var irl = new double[n];
                for (int i = 0; i < n; i++)
                    irl[i] = double.IsNaN(reflLin[i]) ? double.NaN
                           : 20.0 * Math.Log10(Math.Max(Math.Abs(reflLin[i]), 1e-300));
                foms["IRL"] = irl;
            }
            else if (ginMag is not null)
            {
                var irl = new double[n];
                for (int i = 0; i < n; i++)
                    irl[i] = double.IsNaN(ginMag[i]) ? double.NaN
                           : -20.0 * Math.Log10(Math.Max(ginMag[i], 1e-300));
                foms["IRL"] = irl;
            }
            // else: no IRL inputs → omit.
        }

        /// <summary>
        /// In-place phase unwrap of a degree-valued array (port of np.unwrap on radians, applied in degrees).
        /// NaN-aware: NaN entries are left as NaN and do not affect the running offset.
        /// </summary>
        private static void UnwrapDegInPlace(double[] deg)
        {
            const double period = 360.0, half = 180.0;
            double offset = 0.0;
            double? prevRaw = null;
            for (int i = 0; i < deg.Length; i++)
            {
                double v = deg[i];
                if (double.IsNaN(v)) continue;
                if (prevRaw is double p)
                {
                    double d = v - p;
                    // shift d into (-180, 180], accumulate the removed whole periods into offset
                    double corr = d - Math.Round(d / period) * period;
                    // running correction so that the unwrapped step matches np.unwrap
                    offset += (corr - d);
                }
                prevRaw = v;
                deg[i] = v + offset;
            }
        }
    }
}
```

> **Unwrap check:** the goal is the same monotone-continuity np.unwrap gives. The above accumulates the
> period corrections. Add a focused unit test (Part E) with a known wrapping ramp to lock the behavior; if the
> closed form above is fiddly, an equivalent explicit implementation is fine — what matters is the test passes.

### B.1 — call site (both readers)
In each reader's `ParseFreqBlock`, after FOMs are filled and the raw inputs captured (Part A), call:
```csharp
LoadpullDerivedFields.Derive(
    block.Foms, block.NGrid, block.NPin,
    block.GinMag, block.GinPhase, block.TransPhase, block.ReflDb, block.ReflLin);
```
Because derived entries are added to `block.Foms`, `AssembleDataSet` emits them as `{[freq,] gridPoint,
pinStep}` cubes automatically — no change to `AssembleDataSet`/`AddFreqSlice` for Zin/AMPM/IRL.

---

## Part C — guard: AMPM only if not already present
Per SPLData (`GenerateAMPM = 'AMPM' not in header and trans_phase in header`): if a dataset already carries an
`AMPM` column (mapped to a canonical `AMPM` FOM), do NOT overwrite it. In `Derive`, before writing `AMPM`,
check `if (!foms.ContainsKey("AMPM")) { ... }`. (Zin_real/Zin_imag/IRL similarly: only derive when not already
present as a mapped cube — wrap each block in a `!foms.ContainsKey(...)` guard.)

---

## Part D — ZSource (per-frequency source impedance)

ZSource is per-freq (not a drive-up), assumed constant across load terminations. Capture the source Γ from the
first grid point of each block (Part A's `SourceGamma`/`HasSourceGamma`), convert to Z (×50), and add a cube in
`AssembleDataSet`:
- Single-freq: `ds.Add("ZSource", new DataCube(new[]{ /* scalar or 1-elem gridless */ }, new Complex[]{ z }))`.
  Simplest shape: a **scalar** cube — `DataCube.Scalar(zSource)` — OR a `{gridPoint:1}`-style 1-element cube.
  The engine's `SourceZ(freqIdx)` (brief 7.5a) reads `zc[0]` for the single-freq shape; match that. Recommend a
  rank-1 cube over a synthetic 1-length `gridPoint` axis to keep indexing uniform, OR a scalar — coordinate the
  exact shape with 7.5a's reader (7.5a reads `zc[fi,0]` multi-freq, `zc[0]` single-freq). A `{freq}` (multi)
  and scalar (single) pairing is cleanest:
  - single-freq: `ds.Add("ZSource", DataCube.Scalar(zSource));` and have 7.5a read scalar via `zc[]`/`Rank==0`.
  - multi-freq: `ds.Add("ZSource", new DataCube(new[]{ freqAxis }, zSourcePerFreq));` 7.5a reads `zc[fi]`.

> **Coordinate the exact ZSource cube shape with brief 7.5a.** The two must agree. Recommended contract:
> ZSource is rank-1 over `{freq}` in multi-freq, rank-0 scalar in single-freq; 7.5a's `SourceZ(freqIdx)` reads
> `zc[fi]` (multi) or the scalar (single). Update 7.5a's `SourceZ` reader to match whatever shape is chosen
> here. If unsure, default to: **always rank-1 over a `freq` axis** (length 1 in single-freq) — uniform and
> simplest for the engine (`zc[fi]`). Pick one and note it in both files.

Source Γ recognition (per SPLData `TerminationName`): `gamma_src1_real`/`gamma_src1_imag` (RI), or
`|GS@F0|`/`PhiS@F0[deg]` (MA). Convert MA→cartesian, then `Z = GammaToZ(Γ)` using the reader's existing
`GammaToZ` (Z0=50). If no source-Γ columns exist, do not add ZSource (presence-gated).

---

## Part E — alias registration (so summary headers resolve)
The summary UI (later slices) resolves column headers via the metric-alias system. Register the new canonical
cubes so they map to display headers:
- `Zin_real`, `Zin_imag` → used by the Zin composer (not shown as standalone columns by default).
- `AMPM` → "AM/PM (°)".
- `IRL` → "Input Return Loss".
- `ZSource` → "Zsource (Ω)".

Where the alias table lives is a UI concern (Phase 7.4h r5 metric alias map). If the alias registry is in
RfCore, add entries here; if it's in `src/Ui` (e.g. the contour metric list builder), DEFER registration to the
UI slice 7.5e and just note the canonical names produced. **Do not create a UI dependency from RfCore.** For
this brief: produce the cubes with the canonical names above; alias display-name mapping is handled in 7.5e.

---

## Constraints / gotchas
- RfCore firewall: no Avalonia/UI. `Complex` = `System.Numerics.Complex`. `RfHelpers.G2Z` is normalized
  (×50 for actual Z, exactly as SPLData's `g2z(...)*50`).
- Reader symmetry: implement capture (Part A) in BOTH `SplReader` and `LpcwaveReader`; share the math (Part B).
- Presence-gating is the contract: any missing input → no cube → no column. A dataset with none of these
  inputs must import **byte-for-byte as today** (no new cubes, no behavior change).
- Row-major index is `gi*nPin + pi` everywhere (matches existing `Foms` layout).
- NaN handling: invalid/short rows are NaN in FOMs already; carry NaN through derivations (guard each math op).
- `DataCube` ctor clones input arrays; fine. `ds.Add(name, cube)` is the existing add API.
- Alpha no-back-compat: these are additive cubes; no format_version bump needed for the DataSet (in-memory).

## Tests (RfCore.Tests)
Add a `LoadpullDerivedFieldsTests.cs` (pure unit tests on the helper) + an integration assertion via the
existing reader tests if a fixture with the right columns exists.
1. **Zin derivation.** Given `ginMag`/`ginPhaseDeg` for a couple of points with known values, assert
   `Zin_real`/`Zin_imag` equal `RfHelpers.G2Z(Γin)*50` real/imag (recompute independently). NaN in → NaN out.
2. **AMPM relative + unwrap.** Feed a single grid point drive-up of transducer phase that wraps past ±180°;
   assert `AMPM[0] == 0`, the curve is monotone/continuous (no ±360 jumps), and matches a hand-computed unwrap.
   NaN samples stay NaN and don't corrupt the running offset.
3. **IRL priority.** (a) with `reflDb` present → `IRL` equals it; (b) only `reflLin` → `20log10`; (c) only
   `ginMag` → `-20log10(|Γin|)`; (d) none → no `IRL` key.
4. **Presence-gating.** Call `Derive` with all inputs null → `foms` unchanged (no keys added).
5. **AMPM already-present guard.** Pre-seed `foms["AMPM"]`; call `Derive` with `transPhaseDeg` → existing AMPM
   not overwritten.
6. **(integration, if fixture available)** Read an `.spl`/`.lpcwave` that carries Γin/trans_phase/Refl; assert
   the DataSet contains `Zin_real`/`Zin_imag`/`AMPM`/`IRL`/`ZSource` with the right shape; read one back and
   sanity-check a value. A fixture WITHOUT those columns imports with none of them present.
