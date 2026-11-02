# Brief MC-3 — The `.cmc` document, data sets, importers, the cache and data hygiene

**Series:** `brief-modelcomposer-0-overview.md` (D1, D2, D6, D8, D9, D11, D13) · **Tag:** `R-mc3-<m>` · **Depends on:** —
**Area:** a new `src/Design/Modeling/` (document, data sets, importers, cache); `src/RfCore/` (a `.mat` reader
beside `MatWriter`); `src/Cli/DocumentKinds.cs` (classification); `docs/design/model-composer.md`
**Read first:** [P1] §6.2.6.2 last paragraph and Fig. 6.9 (the data domain and its compliance boundary; defer
stressful measurements), §6.7.2 ¶1 (the measurement conditions a dynamic model needs); [R11] ch. 8.5

---

## 1. Goal

A `.cmc` records **what data a model is built from and under what conditions**, and reopens instantly without
re-importing anything. Every import a user needs — measured or simulated — lands in one normalised form.

## 2. Requirements

**R-mc3-1 — The document.** `.cmc` is JSON with:
- `FormatVersion` and `Kind` (`Active` | `Passive`);
- `BasePeriphery` `{ Fingers, UnitWidth }` (Active);
- `DataSets[]`;
- `Pads`, `Network` and `Steps` — opaque to this phase and filled by later ones; preserved on round trip;
- `Panels` — view state, written only when it differs from the default (railRF's `RailPanels` rule).

The writer is deterministic. The reader and validator name the field they refuse. `check` classifies `.cmc` and
calls this validator.

**R-mc3-2 — Data sets.** Each has a `Name`, a `Kind`, its `Sources` (paths relative to the `.cmc`) and its
`Conditions`.
- **Kinds:** `DcIv` · `PulsedIv` · `SParams` (CW multi-bias) · `PulsedSParams` · `SimulatedTables` (I and Y — or
  S, or C — sampled on a bias grid, as another simulator writes them) · `LargeSignal` (validation only: power
  sweeps, loadpull, two-tone) · `PassiveSParams` (one block, or a family over a geometry value).
- **Conditions:**
  - `AmbientC`;
  - `ReferencePlane` (`instrument` | `probe` | `device` | `intrinsic`, D6), with per-terminal `BiasPathOhms` for
    `instrument`;
  - for pulsed kinds: `PulseWidth`, `Period`, `Quiescent { Vgs, Vds, Ig, Id }`, `SampleDelay` (time from the
    pulse's start to the current or S sample) and `TrapState` (`auto` | `setting` | `extreme`, used by MC-10);
  - for double-pulse data only, `PrePulse { Vgs, Vds, Width, Gap, Id? }`: the pulse that sets the trap state
    before each measuring pulse ([N22], [G19]), and `Gap`, the time from its end to the measuring pulse. Without
    it, the quiescent point is the trap-setting bias — ordinary pulsed data is fully supported. A pre-pulse is
    taken as non-dissipating (pinched off, as [G19]'s is); one whose stated `Id` dissipates is a warning, because
    its heating is not modelled;
  - `SoakTime`;
  - a free `Note`.

  A missing condition that a later step needs is reported **by that step**, not here.
- **Normalised form:** per bias point, the terminal voltages and currents (gate, drain) at the stated plane; for
  S kinds, the 2-port S on its frequency axis with Z0; a stable point id. Points carry an `Excluded` flag and a
  `Reason`.

**R-mc3-3 — Importers.**
- **Touchstone sets**: one file per bias. The bias is read from the file name, or from a comment line, by a
  pattern with named fields (`{vgs}`, `{vds}`, `{T}`, `{id}`, `{ig}`). The pattern is stored in the data set, so a
  re-import is exact.
- **CSV/TSV** with a column map (column → quantity and unit). Units are parsed by the existing value+unit
  conventions.
- **`.npy`** through `NpyReader`, and **`.mat`** through a new `MatReader` in RfCore beside `MatWriter`. It covers
  MAT v5 (numeric matrices, structs of them, compressed elements) and v7.3 (HDF5). Read only what is needed; refuse
  cell arrays and objects by name.
- **Simulated tables** map named arrays to quantities, including intrinsic voltages when present (D6 cross-check)
  and a frequency for a Y table.
- Each importer reports what it read in one line: points, bias range, frequency range.

**R-mc3-4 — The cache (D11).**
- `<name>.cmc-cache/` holds the normalised data as `.npy` and an `index.json`: per source its path, size,
  modification time and SHA-256, and the importer settings.
- Open uses the cache when every fingerprint matches.
- A **changed** source is re-imported (one status line); a **missing** source with a valid cache keeps working
  (one warning); a missing source with no cache is a refusal naming it.
- Writes are atomic: write to a temp folder, then rename. The cache never holds anything that cannot be rebuilt
  from the sources.

**R-mc3-5 — Data hygiene** (flags only — the user decides exclusions).
- **Oscillation:** a bias point whose S has |S11| or |S22| > 1, or K < 1 with |Δ| > 1, below a low-frequency
  limit.
- **Compliance:** a current at an instrument limit — read from the data set's stated limits, or detected as a flat
  plateau.
- **Drift:** a bias point repeated in a data set (or across sets) whose current or S differ by more than a stated
  tolerance. This is the "measure a reference bias at the start and end" check behind [P1]'s warning that stress
  degrades a device.
- **Coverage:** the data domain's hull in (Vgs, Vds), reported for the plots and for extrapolation (MC-2).

**R-mc3-6 — Long work.** Import, hashing and cache rebuild take a `RunControl` (D13): progress per file,
cancellable, and a cancelled import leaves the document and the cache as they were.

## 3. Not in this phase
Pads and de-embedding (MC-4); any fitting; the window (MC-14).

## 4. Gates (minimal tests, run only these classes)
- `CmcFormatTests` — round trip byte-identical; unknown later-phase blocks preserved; refusals name their field.
- `TouchstoneSetImportTests` — a 6-file set with biases in the names, and one with biases in comments, import to
  the same normalised data.
- `MatReaderTests` — a v5 file (written by `MatWriter`) and a v7.3 file (an HDF5 written by the repo's HDF5
  writer) read the same arrays; a cell array is refused by name.
- `CmcCacheTests` — reopen reads no source (counter); a touched source re-imports; a deleted source with a cache
  warns and loads; without a cache it refuses; a cancelled rebuild leaves the old cache.
- `DataHygieneTests` — a synthetic oscillating point, a plateau and a drifted repeat are each flagged; nothing is
  excluded automatically.
