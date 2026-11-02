# Brief 7.4f — `.spl` / `.lpcwave` loadpull ingest → loadpull DataSet

**Phase:** 7.4f (Data Display contours — the data-path piece, runs FIRST).
**Design:** `docs/design/loadpull-contours.md` §1 (ingest-first), §2.2 (one shape, two producers), §3 (7.4f).
**Goal:** read measured loadpull files (`.spl`, `.lpcwave`) into the **same loadpull DataSet shape** the
loadpull engine emits, so the Data Display treats measured and simulated loadpull **identically** — the
consumer cannot tell the origin without being told.
**Reference (read-only, outside repo):** `<workspace>/loadpull-contours-refs/SPLData.py`
(`read_spl`, `read_lpcwave`, the header-key guessing, the derived-field logic). **Do not copy its structure
blindly** — it is a dict-of-tuples Python design; we produce a circuitRF `DataSet`. Use it only to learn the
**file grammars** and the **FOM-name dialects**.
**Test data (in repo):** `testdata/spl_test_data/*.spl` (4 files) and `testdata/lpwave_test_data/*.lpcwave`
(5 files). **Broad from the start = handle every file in those two folders.** They are only two formats, but
vary in: number of frequencies, harmonic nesting (f0 / f0+2f0 / f0+2f0+3f0), and **FOM-name dialect**
(HarmonicaRF `.spl` uses `Pout_dBm`/`Gt_dB`/`Eff_%%`; lpcwave-derived `.spl` and `.lpcwave` use
`PoutWaves[dBm]`/`GainWavesTrd[dB]`/`PAEffWaves[%]`).

---

## 0. The contract — match the loadpull engine's DataSet exactly

> **FIRST STEP (mandatory):** open `src/Engine/Loadpull/LoadpullEngine.cs` and find **`BuildLoadpullDataSet`**
> (the method that turns a `LoadpullResult` into a `DataSet`). That method **is the contract.** The readers
> must produce a DataSet with the **same group name, same cube names, same axis names/order/units, and same
> DataKind** for the FOMs that overlap. Read it and write the cube/axis names it uses into your reader; do not
> invent new names. If a measured FOM has no engine equivalent (e.g. `AMPM`), add it as an extra cube — extra
> cubes are fine; **mismatched names for the same quantity are not.**

The loadpull field is intrinsically 2-D: **`{gridPoint, pinStep}`** (one power drive-up per swept Γ). The
engine's `PinStepResult` carries these live FOMs (loadpull.md §4): `PoutW`, `PinDeliveredW`, `PavlW`,
`GtDb`, `GpDb`, `PdcW`, `De`, `Pae`, plus bias V/I and the `V`/`INl` spectra; `GridPointResult` carries
`Gamma`/`Z` per grid point. The measured files carry the **same physical quantities** under different names
and units (dBm vs W, % vs linear). **Normalize to the engine's cube names + units** so a contour/trace
consumer is origin-blind.

**Canonical loadpull DataSet shape (confirm against `BuildLoadpullDataSet`; this is the expected target):**
- Group: the loadpull analysis group (e.g. `LP1` — use whatever `BuildLoadpullDataSet` uses).
- FOM cubes over axes `{gridPoint, pinStep}` — e.g. `Pout`, `Gt`, `Gp`, `PAE`, `DE`, `Pdc`, `Pin`
  (names/units per the engine). Ragged drive-ups (different pinStep counts per grid point) → pad with **NaN**
  to a common `pinStep` length (the surface engine already NaN-drops, per `SPLData.py`).
- `GammaLoad{gridPoint}` (complex) and `ZLoad{gridPoint}` (complex) — the swept termination per grid point.
  (Match the engine's names; if the engine names them differently, use its names.)
- Axes are **named and unit-bearing** (`gridPoint` unitless index; `pinStep` unitless index OR the Pavl/Pin
  value — match the engine). The 7.4b surface engine slices by **axis name**, never position.

**Firewall:** readers are **framework-free** in **RfCore** (no Avalonia). They return a `DataSet`. Wiring into
the data-source library (UI) is the last slice.

---

## 1. Slice plan (small, compile-and-test-gated)

### 7.4f-1 — `.spl` reader (RfCore, headless)
**File:** new `RfCore/src/Loadpull/SplReader.cs` (or the loadpull data namespace already in RfCore — check
where `GamReader` lives and co-locate). Public: `static DataSet ReadSpl(string path)` (+ a `TextReader`
overload for tests).

**`.spl` grammar (from the test files — handle all 4):**
```
! comment lines (start with '!')                         ← skip
Number of Frequencies = N
Number of Variables = 1
VAR=<F0 Load Gamma>, Units=<>                            ← the swept-termination variable
VAR=<Pin_avail>, Units=<dBm>                             ← the drive-up (pinStep) variable
! Freq Points per VAR
1.8 145 70                                               ← <freqGHz> <numGridPts> <numPinSteps>  (one line per freq)
...
Freq = 1.8 GHz
num_src_harmonics = 3, num_ld_harmonics = 3
<column-header line: space-separated field names>        ← the data dialect lives here
<data rows: one per (gridPoint, pinStep), flat>         ← gamma_ld1 is the swept load Γ
Freq = 2.0 GHz                                           ← next freq block repeats
...
```
- **Two header dialects** (detect by which keys are present — mirror `SPLData.py`'s guessing):
  - HarmonicaRF: `Pout_dBm`, `Gt_dB`, `Gp_dB`, `Eff_%%`, `Pin_avail_dBm`, `gamma_ld1`/`gamma_src1`,
    `trans_phase`, `Gamma_in_mag`/`Gamma_in_phase`.
  - lpcwave-derived (`ConvertedFile.spl`): `PoutWaves[dBm]`, `GainWavesTrd[dB]`, `GainWavesPwr[dB]`,
    `PAEffWaves[%]`, `OutEffWaves[%]`, `PinWaves[dBm]`, `|GLWaves@F0|`/`PhiLWaves@F0[deg]`, …
- **The swept load Γ** is `gamma_ld1` (real,imag pair already in the row in HarmonicaRF; in the converted
  dialect it is the `Gamma`/`Phase[deg]` block columns — but `gamma_ld1` is present too; prefer the explicit
  `gamma_ld*` columns). **Group rows into grid points by unique load Γ** (the load Γ is constant within one
  drive-up; it changes at the next grid point). `SPLData.py` uses the column the VAR declares; do the same.
- **Per-frequency:** each `Freq =` block is a separate frequency. circuitRF loadpull `run.npy` is typically
  single-frequency, but the reader must keep all N (add a `freq` axis OR emit one group per freq — match what
  `BuildLoadpullDataSet` does for multi-freq loadpull; if the engine is single-freq today, put `freq` as the
  outermost axis on every cube so multi-freq measured data round-trips).

**FOM-name normalization (the broad part):** build a small **dialect map** `measuredName → (canonicalCube,
unitConversion)`. Examples:
- `Pout_dBm` | `PoutWaves[dBm]` → `Pout` (convert dBm→W to match engine `PoutW`, OR keep dBm — **match the
  engine's stored unit**; check `BuildLoadpullDataSet`).
- `Gt_dB` | `GainWavesTrd[dB]` | `Carrier_Gain21_User[dB]` → `Gt`.
- `Gp_dB` | `GainWavesPwr[dB]` → `Gp`.
- `Eff_%%` | `Eff_%` | `OutEffWaves[%]` → `DE` (convert %→linear if engine stores linear).
- `PAE` | `PAEffWaves[%]` → `PAE`.
- `Pin_avail_dBm` | `PinWaves[dBm]` → `Pin` (the pinStep basis).
- Compute `PAE` if absent (formula in `SPLData.py`: `(pout−pin)/pout · eff`).
Put the map in one table at the top of the reader so adding a dialect later is one line.

**Derived fields (port from `SPLData.py`, but lazily — NOT all in the reader):** the reader stores only the
**raw measured cubes**. Compression preprocessing, AMPM, etc. belong to the **7.4b `LoadpullSurface`** model
(design §1.1: cube stays honest). EXCEPTION: include `AMPM` and `Compression`-source gain only if they are
raw columns; do not *compute* derived fields in the reader.

**Termination metadata:** capture per-harmonic source/load Γ (`gamma_src1/2/3`, `gamma_ld1/2/3` or the
`|GS@Fn|`/`|GL@Fn|` mag/ang forms) where present — at minimum the swept fundamental load Γ for `GammaLoad`.
Higher-harmonic terminations: store as extra `{gridPoint}` cubes (`GammaLoad2f0`, etc.) if present; skip if
absent. Forgiving: missing optional columns never throw.

**Gate (7.4f-1):** `ReadSpl` on all 4 `testdata/spl_test_data/*.spl` returns a DataSet with the expected
`{gridPoint, pinStep}` shape (e.g. `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` → 145 grid × 70 pin at 1.8 GHz;
`GaN_FET_1p6_mm_3_Freq.spl` → 3 freqs × 145 × 70); `GammaLoad` has 145 entries; canonical FOM cubes present
with correct units; both header dialects parse. Unit tests assert grid/pin counts, a spot Γ value, and a spot
FOM value against the raw file.

### 7.4f-2 — `.lpcwave` reader (RfCore, headless)
**File:** `RfCore/src/Loadpull/LpcwaveReader.cs`. Public: `static DataSet ReadLpcwave(string path)`.

**`.lpcwave` grammar (from the test files — handle all 5):**
```
! header block (comments + FileInfo) ─ key lines like:
!   Frequency = 2 GHz
!   Char.Impedances = Source: 50 + 0j Ohm   Load: 50 + 0j Ohm
!   Source Frequencies = F0: 2 GHz  F1: 4 GHz  F2: 6 GHz      ← harmonic nesting (f0/2f0/3f0)
!   Load Frequencies = F0: 2 GHz ...
!   Source/Load Impedances [F0/F1/F2] ...
Point  Gamma  Phase[deg]  Psource[dBm]  PinWaves[dBm] ... PowerIndex    ← column header
!---
# 001  0.000  -88.9                                       ← grid point: index, Γmag, Γphase(deg)
   <drive-up row 1>                                       ← pinStep rows (NO leading '#')
   <drive-up row 2>
   ...
# 002  <Γmag> <Γphase>                                    ← next grid point
   ...
! Frequency = <next> GHz                                  ← next freq block (if multi-freq)
```
- **Block structure:** a `#`-prefixed line starts a new grid point and carries the **swept Γ in mag/phase
  (degrees)** → convert to complex (`Γ = mag·e^{jθ}`). The rows until the next `#` are that point's drive-up.
  (`read_lpcwave` in `SPLData.py` is the exact reference for the block-split + `np.unique` on Γ.)
- **FileInfo block** carries per-harmonic Source/Load frequencies + impedances (the `F0:/F1:/F2:` lines). Parse
  these into termination metadata; the **swept fundamental load Γ** comes from the `#` lines (load pull) — but
  detect **sourcepull vs loadpull** the way `SPLData.py` does (presence of `Load Impedance` ⇒ sourcepull, the
  swept term is source; `Source Impedance` ⇒ loadpull). Set `GammaLoad` (or `GammaSource`) accordingly; record
  which was swept.
- **Dialect:** lpcwave always uses the `*Waves*` names (`PoutWaves[dBm]`, `GainWavesTrd[dB]`, `PAEffWaves[%]`,
  `|GLWaves@F0|`, …) — reuse the **same dialect map** from 7.4f-1 (factor it into a shared
  `LoadpullFomDialect` static so both readers share it).
- **Harmonic nesting variants:** the 5 files span f0, f0+2f0, f0+2f0+3f0, plus `compression-LP-OPT-pattern`
  (an optimizer pattern, possibly irregular grid). The reader must not assume a rectangular Γ grid — grid
  points are just "however many `#` blocks exist." Ragged drive-ups → NaN-pad pinStep.

**Gate (7.4f-2):** `ReadLpcwave` on all 5 `testdata/lpwave_test_data/*.lpcwave` returns the loadpull DataSet
shape; the f0/2f0/3f0 variants all parse; `compression-LP-OPT-pattern.lpcwave` parses without throwing (even
if its grid is irregular); spot Γ + spot FOM checks against the raw file; sourcepull-vs-loadpull detection is
correct.

### 7.4f-3 — wire into the data-source library (UI)
**Goal:** `.spl`/`.lpcwave` become loadable source kinds beside Touchstone/`.npy`, origin-blind downstream.
- In the data-source library (the `DataSource*` classes from 7.2c — formerly `Snp*`), add `SourceKind.Spl`
  and `SourceKind.Lpcwave`; `LoadFileAsync` routes by extension → `SplReader.ReadSpl` / `LpcwaveReader
  .ReadLpcwave` → `DataSet` (same path as the `.npy` cube-only branch from 7.2b: `Data = ds`, `Snp = null`).
- File-picker filter (`DataDisplayView.axaml.cs`): extend "Data Files" to include `*.spl;*.lpcwave`.
- The loaded DataSet flows through the existing 7.2c/7.3 cube-trace machinery unchanged — a measured drive-up
  (Pout vs Pin at a pinned gridPoint) plots like any cube trace. **No origin-specific UI.**

**Gate (7.4f-3):** load `Ideal_GaN_FET_1p6_mm_1p8_GHz.spl` and `4x150_new_wavecal_24012020.lpcwave` via the
data-source library; both appear as normal sources; author a Rect trace `Pout` vs `Pin` at a pinned grid point
through the existing picker; it renders. The viewer shows no `.spl`/`.lpcwave`-specific affordance.

---

## 2. Constraints / gotchas
- **TreatWarningsAsErrors** (Core + UI): nullable property warnings → capture into locals; no unused privates
  (`_ = x;` to discard); no `<`/`>` in XML doc comments.
- **Forgiving parsing** (research-tool philosophy, warn-and-continue): malformed optional columns, missing
  harmonics, irregular grids → skip/NaN + a single `Message`, never throw. Only a structurally unreadable file
  (no data rows, no parseable Γ) is a hard error.
- **NaN discipline:** ragged drive-ups pad with NaN; the 7.4b surface engine NaN-drops. Do not zero-fill.
- **Units:** match `BuildLoadpullDataSet`'s stored units exactly (dBm vs W, % vs linear). The dialect map owns
  every conversion in one place.
- **Lockstep:** if a canonical loadpull FOM-naming convention is established here, record it in
  `src/Core/Data/CLAUDE.md` → "Change carefully" (splotRF reads it too). Flag in the PR.
- **Alpha:** readers are read-only of external files; no new on-disk circuitRF format. No version gating.
- **Firewall:** `SplReader`/`LpcwaveReader`/`LoadpullFomDialect` are RfCore, zero Avalonia. Only 7.4f-3 touches UI.

## 3. Tests
- `RfCore.Tests` (or the existing loadpull test project): one test class per reader. Drive the **real**
  `testdata` files (copy-relative path helper as other tests do). Assert grid/pin counts, a spot complex Γ,
  spot FOM values (raw-file cross-check), dialect coverage (both `.spl` dialects), harmonic-nesting coverage
  (all 3 `.lpcwave` nestings), and the OPT-pattern parses.
- Round-trip sanity: reader DataSet axis names/order match `BuildLoadpullDataSet` for the overlapping cubes
  (a small assertion comparing axis-name sets).
- Keep tests fast; no UI tests for 7.4f-1/-2 (headless). 7.4f-3 gets a smoke test through the data-source
  library if practical (mirror `brief-7.2b` harness).

## 4. Out of scope (later sub-gates)
- Compression preprocessing / 2-D RBF surface / off-grid synthesis → 7.4a/b/c (`LoadpullSurface`).
- Contour iso-line rendering → 7.4d.
- Writers (`write_spl`/`write_lpcwave` in `SPLData.py`) — **not needed**; circuitRF writes `.npy`.
- `.spl`/`.lpcwave` formats beyond what the 9 test files exercise — add dialect-map entries as new files appear.
