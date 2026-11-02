# Brief MC-11 — Export and validation

**Series:** `brief-modelcomposer-0-overview.md` (D1, D3, D9, D12, D13, D16) · **Tag:** `R-mc11-<m>` · **Depends on:** MC-7, MC-8 (MC-9, MC-10 when present)
**Area:** `src/Design/Modeling/` (export, validation); the existing run services behind the `sparam`, `dc`, `hb`
and `lp` verbs (used, not changed); `src/RfCore/Loadpull/` readers (used)
**Read first:** [P1] §6.7.1 (validation against large-signal data; why DC and S alone do not prove a large-signal
model), §6.7.5 (DC as validation, not playback); `docs/design/loadpull.md`

---

## 1. Goal

Turn a composed model into a `.cmodel` a schematic can use, and show — against every kind of data the user has —
how well it predicts, at the **measurement** plane, through the same engines a design will use.

## 2. Requirements

**R-mc11-1 — Export.** It assembles the `.cmodel` (MC-1 schema) from the `.cmc`:
- the network with its fitted values and scaling rules;
- each constituent's result, with its domain and `G_x`;
- table splines fitted to coefficients;
- thermal, traps, the temperature mode, `N0`/`W0`, the symbol and provenance.

Refusals, each naming what is missing:
- no `N0`;
- an unfitted constituent;
- a **stale** step (anything fitted before the network or the data last changed).

The export is a function that writes nothing until it has assembled and validated the whole model in memory
(MC-1 R-mc1-2's validator), then writes atomically.

**R-mc11-2 — Symbol (D12).**
- The choices: library symbols and workspace `.csym` files whose pins 1, 2 and 3 exist. A pin 4 is allowed only
  when the model has a thermal node, and a 4-pin symbol on a model without one is refused.
- The default is the standard 3-terminal FET symbol.
- The choice is stored in the `.cmodel` and the `.cmc`.

**R-mc11-3 — Placement payload.** A headless function returns a `ComposedFet` instance (`File`, `N = N0`, the
symbol) in the schematic clipboard format. The window puts it on the clipboard so the user pastes it into any
`.csch` — the Smith Chart tool's pattern.

**R-mc11-4 — Validation.** Runs analyses on the model through **the existing run services**, in generated
testbenches. Each generated testbench is ordinary `.cnl` text, and "show testbench" writes it out, so what was
simulated is inspectable.
- The model under test is the exported `.cmodel`, or the current state written to a temporary one.
- **S-parameters** at every measured bias, at the measurement plane — pads re-embedded — against the data.
  `|ΔS|` per bias, and median and 95th percentile overall (small entries are judged by `|ΔS|`, never by dB).
- **DC I–V** output and transfer families against DC data; pulsed I–V against pulsed data at the mapped trap state
  (MC-10).
- **Large signal**, for `LargeSignal` data sets:
  - power sweeps at a stated source/load, bias and frequency (Pout, gain, PAE, drain current against input
    power);
  - loadpull files through RfCore's readers, run as an `lp` sweep over the measured loads;
  - two-tone IM3/IM5 against input power.

  Each runs HB with the data's terminations.
- Tolerances are settings. The summary is one line per item ("S |ΔS| median 0.006, 95 % 0.021 ✓").
- Results are `DataSet`s with measured and model cubes on the same axes, for MC-15's overlays.

**R-mc11-4a — Large-signal data** (added in this phase). A `LargeSignal` data set states its measurement in
`Conditions.LargeSignal`: `PowerSweep` or `TwoTone` (a CSV of `pin pout gain pae de id ig im3 im5`, powers in dBm, gain in
dB, efficiencies in %), or `Loadpull` (a `.spl`/`.lpcwave` through RfCore's readers, `Import.Format: loadpull`), with the
frequency, tone spacing, bias, source and load impedances and their harmonic terminations. The cache never imports one;
adding one stales no fitted step.

**R-mc11-5 — Long work.** Through `RunControl`: one stage per item, biases in parallel where the run service
allows, cancellable. A cancelled validation reports nothing (D13).

**R-mc11-6 — The Model Validation Report** (owner request, 2026-10-19, paraphrased: one report showing the model's fit to
measurement and its predicted PA performance, the options set in a dialog in MC-14/MC-15). The headless half is here: a
function that runs every section the document's data supports and writes a folder (`report.npy` with one group per
section, `summary.txt`, the testbenches), its options kept in `Steps.Report`:
- DC I–V and pulsed I–V, measured against model;
- the charge-surface difference (model against the line integral of the measured capacitances, % of range);
- 1-D C–V: each capacitance and the circuit set Cgs Cgd Cds Cm against one voltage at a family of fixed values of the
  other (the family is the legend), with the nearest measured points;
- S at chosen biases, every S set at its own ambient temperature;
- with a self-heating model and DC data: the equivalent junction temperature of each DC point against its power, the
  data's Rth per ambient against the model's — the Rth validation when data over temperature exists;
- PA performance at **Class B, J, F and F⁻¹** (any of harmonicaRF's classes), harmonics to the fifth, the source's 2f0 as
  the user enters it. The terminations follow **harmonicaRF's method** (owner direction): its intrinsic presets carried
  to the extrinsic plane through the model's own network with the output capacitance linearized at the intrinsic bias.
  For each class: performance at the class's load and at MXP and MXE at the stated compression (RfCore's loadpull
  surface, the Data Display's performance summary), and the **region of convergence** — the share of a disc of loads
  about the class's fundamental load where HB converged to that compression.

`LoadpullEngine` (the `lp` and `lpp` verbs) load-pulls a composed FET: its tuners sit on nets behind the model's network,
and Pout is read from the load tuner's own branch currents. Reading Pout through that network is valid — the network is
part of the model (owner, paraphrased). The report's PA modes run one HB power sweep per load on a fixed drive ladder, so
the region of convergence is measured the same way at every load.

## 3. Not in this phase
The export, validation and report UI (MC-15); the CLI spelling (MC-13).

## 4. Gates (minimal tests, run only these classes)
- `CmodelExportTests` — a model composed from `SyntheticFetData`, exported and placed in a `.cnl`, simulates to the
  same DC/SP/HB result as the composer's in-memory model (1e-9); export refuses without `N0`, with an unfitted
  constituent, and with a stale step, naming each; a failed export leaves no file.
- `SymbolChoiceTests` — a 2-pin symbol is refused; a 4-pin symbol on a model without a thermal node is refused;
  the stored symbol is used on placement.
- `ValidationServiceTests` — on synthetic data, S validation is within tolerance and a deliberately perturbed `Lg`
  fails it; a power-sweep validation against the truth's HB result is within tolerance; the generated testbench
  text runs unchanged through `circuitrf hb`.
