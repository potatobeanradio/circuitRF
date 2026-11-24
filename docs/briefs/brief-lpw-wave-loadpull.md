# Brief LPW — wave-derived loadpull metrics, `.cst` and `.satwave` import, a chosen compression reference

**Tag:** `R-lpw-<n>` · **Origin:** GitHub issue #87, with sample files attached to the issue · **Depends on:** nothing
open
**Area:** `src/RfCore/Loadpull/` (new `LoadpullWaves.cs`, `CstReader.cs`, `SatwaveReader.cs`, `LoadpullFiles.cs`;
`LpcwaveReader.cs`, `LoadpullDerivedFields.cs`, `LoadpullFomDialect.cs`, `LoadpullSurface.cs`), the five places that
open a loadpull file today (below), `src/Render/DataDisplay/` (`ContourResolve`, `Models/ContourData.cs`,
`Models/DataDisplayConfig.cs`, `SummaryResolve`), the contour card in `src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs`,
`src/Design/Modeling/` (`CmcFile.cs`, `ModelValidation.cs`), `docs/design/loadpull-contours.md`,
`docs/design/model-composer.md` §14.4, the user page for loadpull files.

## The gap

Measured loadpull files carry the incident and reflected waves at the device's planes (a₁, b₁, a₂, b₂, at f0 and, in
some files, at its harmonics), and the bias at each drive step. circuitRF reads the metrics such a file states, and
derives only a fixed few (`LoadpullDerivedFields`: Pout_W, Zin, AM/PM, IRL). Three things are missing:

- **A. Metrics from the waves.** Delivered input power, power gain against it, PAE with the delivered input, the
  measured load reflection, and the same at each harmonic the file holds. A file that states none of these, but
  carries waves, ends up with fewer metrics than its data supports.
- **B. The `.cst` measured-wave format** (and its single-load companion, `.satwave`, which shares `.lpcwave`'s columns).
  Neither can be opened.
- **C. The compression reference.** Contours and the summary table read "at compression" from `Gt_dB`, measured from its
  running maximum (`LoadpullSurface`, `CompressionType.Gmax`, hard-wired). A contour may already slice at a constant
  metric level instead (`ConstraintKind.ConstantMetric`, on the contour card), but its list holds only
  `LoadpullSurface`'s fixed metric names. The reference gain cannot be chosen, and neither can a small-signal reference
  (`CompressionType.Gss`, which exists and nothing selects).

## What the samples show (read before writing any code)

The issue's attachment (`lp_samples.zip`: one `.lpcwave`, one `.satwave`, three `.cst` at f0, f0 + 2f0 and f0 + 2f0 +
3f0) was examined while this brief was written:

- **`.cst` layout.** A block per load: `#KEY: value` header lines (`#FUNDAMENTAL FREQUENCY: 14.0 GHz`, `#HARMONICS: n`
  for n harmonics above f0, `#Z0SOURCE`, `#Z0LOAD`, optional `#GAMMASOURCE: |Γ| ∠deg`, `#GAMMALOAD: |Γ| ∠deg`, bias
  and pulse lines), a column title line, then `#BEGIN` … `#END`. Data rows are
  `Frequency Vin Iin Vout Iout Re[a1] Im[a1] Re[b1] Im[b1] Re[a2] Im[a2] Re[b2] Im[b2]`, one row per harmonic per drive
  step, f0 first (`+1.7E10`, `+3.4E10`, `+1.7E10`, …). The DC columns repeat on a drive's harmonic rows. Files have CRLF
  endings. Blocks have different drive counts (ragged: 23 to 30 steps in one file). Only the f0 load varies between
  blocks. The harmonic loads are not in the header.
- **Γ_L = a₂/b₂.** At f0, a₂/b₂ reproduces each block's `#GAMMALOAD` to the stated digits.
- **`.cst` waves are peak, `.lpcwave` waves are RMS.** In the `.lpcwave`, `PinDelWaves` = |a₁|² − |b₁|² and
  `PoutWaves` = |b₂|² − |a₂|², in W, to the file's rounding: RMS waves. The `.cst` states no convention. Read as RMS,
  its blocks give drain efficiencies up to 97 % and about 12–13 W/mm on the stated peripheries. Read as peak
  (P = ½|a|²), they give 35–49 % and 6–7 W/mm. So peak is taken, it is recorded as the reason, and the poster is asked
  to confirm on the issue.
- **Harmonic Γ from waves is noise at low drive.** At 2f0, a₂/b₂ reads |Γ| > 1 at the first drive steps and settles
  to a steady value (~0.4) as b₂ grows. A per-load harmonic Γ is therefore weighted by |b₂|², never a plain mean or
  the first row's.

**The samples are not committed** (`R-lpw-10`). They live on the issue. Fetch them into a scratch folder outside the
repository to develop and check against.

## Requirements

### A — Metrics from waves

**R-lpw-1 — One derivation, `LoadpullWaves`.** Given, per drive step and harmonic: the waves, the wave convention
(`Rms` | `Peak`), Z0 at each port, Γ_S at f0 (from the file, or 0 when it states none), and the bias Vin, Iin, Vout,
Iout, it produces the canonical cubes over `[gridPoint, pinStep]`. All are in the units `LoadpullFomDialect` stores (dBm,
dB, %). With P(x) = |x|² for RMS waves and ½|x|² for peak:

| Cube | From |
|---|---|
| `PavlDbm` | available source power: the generator wave b_g = a₁ − Γ_S·b₁, P(b_g)/(1 − \|Γ_S\|²) |
| `PinInc_dBm` (new) | P(a₁) |
| `PinDel_dBm` (new) | P(a₁) − P(b₁) |
| `Pout_dBm`, `Pout_W` | P(b₂) − P(a₂) at f0 |
| `Gt_dB` | Pout / Pavl |
| `Gp_dB` | Pout / PinDel |
| `Pdc_W` | Vin·Iin + Vout·Iout |
| `Efficiency` | Pout / Pdc (drain) |
| `PAE` | (Pout − PinDel) / Pdc |
| `Zin_real`, `Zin_imag` | Z0·(1 + Γin)/(1 − Γin), Γin = b₁/a₁ |
| `IRL_dB` | −20·log₁₀\|Γin\| |
| `AMPM_deg` | ∠(b₂/a₁) minus its value at the lowest drive, unwrapped |
| `GammaLoadMeas` (new, complex) | a₂/b₂ at each drive (the measured load, beside the nominal `GammaLoad[gridPoint]`) |

For each harmonic n ≥ 2 that a file holds: `Pout_<n>f0_dBm`, `PinDel_<n>f0_dBm` and a per-load
`GammaLoad_<n>f0[gridPoint]` (complex, weighted by P(b₂) over the drives, as the samples require). Harmonic metrics
are separate rank-2 cubes, never a third axis. A third axis would be read by `LoadpullSurface` and
`LoadpullRecognition` as a leading frequency sweep: both key on rank.

Every output is presence-gated, as `LoadpullDerivedFields` is now: a column the file states wins over the derived
one, and the derivation fills only what is missing. A quantity whose inputs are absent is not produced, rather than
produced as zero.

**R-lpw-2 — `.lpcwave` uses it.** `LpcwaveReader` reads the `A1reF0 … B2imF0` columns it currently skips (and the
`…F1`/`…F2` ones when present), as RMS, and passes them through `LoadpullWaves`. The file's stated metrics still win.

**R-lpw-3 — The new names are metrics.** `PinInc_dBm`, `PinDel_dBm` and the harmonic power cubes join
`LoadpullSurface`'s metric list. They can then be fitted, contoured, chosen as a constant level (C) and read by the
summary table. `ContourData`'s display names and units, and `LoadpullFomDialect`'s map, gain them too.

### B — The `.cst` and `.satwave` formats

**R-lpw-4 — `CstReader`.** Reads the layout above into the canonical loadpull `DataSet` that `LpcwaveReader` builds:
- `GammaLoad`/`ZLoad` from each block's `#GAMMALOAD` at `#Z0LOAD`;
- the waves as **peak**, with the reason in a comment beside the constant;
- the metrics through `LoadpullWaves`;
- ragged blocks NaN-padded, as `LpcwaveReader` pads them;
- blocks at more than one `#FUNDAMENTAL FREQUENCY` as a leading frequency axis.

The `#MEASUREMENT TYPE`, `#SWEPT HARMONIC`, `#GAMMASOURCE` and the pulse and bias lines are kept as provenance
attributes. A block whose harmonic rows do not interleave as `#HARMONICS` says (a missing 2f0 row) is refused, naming
the block and the drive. A measurement type this brief does not handle (anything but `Power Sweep`) is refused by name.
CRLF and LF both read.

**R-lpw-5 — `SatwaveReader`.** A `.satwave` is one load's drive-up in `.lpcwave`'s columns, with the load and source in
the `!` header. It reads as a one-point loadpull through the same column handling (shared, not copied).

**R-lpw-6 — One dispatcher.** `LoadpullFiles.Read(path)` picks the reader by extension and, for an unknown one, by
content. It is the only call site for all four readers. The five places that open a loadpull file today switch to it:
`DataSourceLibraryViewModel` (three calls), `PlotSourceFile` (`render`), `ModelValidation`. So do the file-picker
patterns and the project tree's recognition in `WorkspaceViewModel`, and `WorkspaceArchiveScanner`'s list. A source
scan test holds the rule that no other file calls a loadpull reader directly.

### C — The compression reference

**R-lpw-7 — A chosen reference.** `ConstraintSpec.AtCompression` gains a reference gain, `Gt_dB` (the default, and
everything before this brief) or `Gp_dB`, and a type, `Gmax` (the default: from the running maximum) or `Gss` (from the
small-signal gain, the first valid drive). `LoadpullSurface` computes compression from that gain and type. Its
`StackKey` and fit cache key include both, so changing one refits and never reuses a stale stack. The contour card's
compression row gains the two pickers. They are persisted in the contour config (`CompressionReference`,
`CompressionType`, defaulted so an older `.cdd` reads back as it drew). The summary table's compression gains the same
pair, table-wide, beside its compression value.

**R-lpw-8 — Constant levels include the new metrics.** The constant-metric picker lists what R-lpw-3 added. So a
contour can be cut at constant delivered input power, which is the comparison a model-to-measurement check usually
wants. The alias rule that greys a constraint metric in the same group as the contoured one (`MetricAliasGroup`)
places `PinInc`/`PinDel`/`PavlDbm` in an input-power group of their own.

### The Model Composer

**R-lpw-9 — `.cst` and `.satwave` as large-signal data.** A `LargeSignal` set with `Format: loadpull` accepts all four
files through `LoadpullFiles.Read` (`CmcFile` and the set's file picker). A one-load file (a `.satwave`, or a `.cst` with
one block) is also accepted as a `PowerSweep` set: the power-sweep validation compares Pout, gain, PAE and DE against
its drive-up, as it does a CSV now. Its source and load terminations are the file's, so nothing has to be typed. The
validation's "3 dB of Gt" (`ValidationSettings.Compression`) takes R-lpw-7's reference and type, defaulting to what
it reads now. `model-composer.md` §14.4 is updated.

### Test data

**R-lpw-10 — Small, synthetic, in the samples' layout.** Committed fixtures are written for the tests in each format's
exact layout, learned from the samples: header lines, column order, CRLF, ragged blocks, interleaved harmonic rows,
`!`-commented headers for `.lpcwave`/`.satwave`. They are small (at most 3 loads × 8 drives × 3 harmonics). Their
values come from a small analytic device evaluated at known loads, so every metric has an exact expected value. No row
of a sample file is committed, and no fixture or test names where the samples were kept. The samples are used, out of
the repository, for three checks whose results are recorded in `src/RfCore/RESOLVED.md` (in words, no paths):
1. every sample reads, with the expected block and drive counts;
2. the `.lpcwave` and `.satwave` samples' own computed columns (`PinDelWaves`, `PoutWaves`, `GainWavesTrd`,
   `GainWavesPwr`, `PAEffWaves`, `OutEffWaves`, `|GinWaves@F0|`) are reproduced by `LoadpullWaves` from their wave
   columns, to the files' rounding;
3. the `.cst` convention evidence above, rerun against the reader's output.

## Gates

1. **The derivation.** A synthetic `.lpcwave` fixture whose stated columns were computed from its waves: with those
   columns removed, `LoadpullWaves` reproduces them within 0.01 dB / 0.01 %. Γ_S ≠ 0 is covered, so `PavlDbm` is not
   P(a₁).
2. **`.cst`.** Synthetic f0, 2f0 and 3f0 fixtures: block and drive counts, NaN padding for the ragged block, `GammaLoad`
   from the header, every metric of R-lpw-1 at its analytic value. The harmonic Γ is weighted so that a noisy first
   drive does not move it. A missing harmonic row is refused with the block and drive named.
3. **One dispatcher.** The source scan of R-lpw-6, and a `.cst` opening in the Data Display, in `render` and as a Model
   Composer set through the same call.
4. **Compression reference.** On a fixture where Gt and Gp compress differently, contours at 3 dB of Gp and 3 dB of Gt
   differ, each matching a hand-computed compression point. Switching the reference refits (the cache key test). An
   older `.cdd` opens drawing what it drew.
5. **Constant delivered input.** A contour of Pout at constant `PinDel_dBm` matches the fixture's analytic Pout at that
   input.
6. **Model Composer.** A one-load `.cst` as a PowerSweep set validates a model against its drive-up (using the composer's
   existing test device). Validation reads the same figures through `LoadpullFiles` as the Data Display does.

## Not here

Waveform (time-domain) reconstruction from the harmonic waves, and a waveform comparison in model validation. The
harmonic phases are relative to a reference the format does not state; this is worth a brief of its own once confirmed.
Also not here: source-pull `.cst` files (refused by measurement type, R-lpw-4), writing `.cst`, and calibrating or
de-embedding the waves (they are taken at the planes the file states).
