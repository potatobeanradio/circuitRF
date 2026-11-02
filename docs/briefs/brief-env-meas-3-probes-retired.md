# Brief ENV-MEAS-3 — `Probes` retired: labeled nets record voltage, IProbes measure power; the examples migrated

**Series:** Envelope measurements · **Tag:** `R-envm3-n` · **Briefs:** [1](brief-env-meas-1-probe-power.md),
[2](brief-env-meas-2-power-shorthand.md), 3 (this)
**Depends on:** ENV-MEAS-1 (IProbe recording, the `Pprobe_*` cubes, `Zmeas` gone) and ENV-MEAS-2 (`P("probe")`, used
by the examples' measure lines)
**Area:** `src/Engine/Envelope/EnvelopeEngine.cs`, `EnvelopeEngineMt.cs`, `EnvelopeRunner.cs`, `EnvelopeDataSet.cs`,
`EnvelopePulse.cs`; `src/RfCore/Signals/SignalMeasure.cs` (a cross-spectrum); `src/Core/Design/Analysis.cs`,
`src/Core/Netlist/CnlReader.cs`, `CnlWriter.cs`, `AnalysisDirectiveSchema.cs`;
`src/Design/Schematic/AnalysisSerialization.cs`; `src/Cli/Envelope.cs`; `src/Ui/ViewModels/EnvBodyViewModel.cs`,
`src/Ui/Views/Analyses/EnvBodyView.axaml`, `src/Ui/Commands/Analysis/DuplicateAnalysisCommand.cs`;
`examples/Envelope/*`, `examples/Pulsed Radar/*`, `tests/Ui.Tests/Examples/PulsedRadarExampleAuthoring.cs`;
`docs/user/src/reference/simulations.md`, `data-display.md`, `cli.md`. Findings go in `src/Engine/RESOLVED.md`, with
the UI half in `src/Ui/RESOLVED.md`.

**This brief supersedes two statements in brief 1:** that `V` keeps exactly the nodes listed in `Probes`, and the
series rule that the examples' existing cubes stay bit-identical. The examples are migrated here on purpose. The
physics does not move: every migrated number must equal the old number where the old one was right (§8).

---

## 0. Why

After brief 1 an envelope run has two ways to say "measure here": the `Probes` list, and IProbes. Only one of them
is exact, and the other is where the wrong numbers came from. The **last** listed probe silently became "the
output" for Pout, Gt, AM-AM, NMSE, DE and the pulse cubes, always into 50 Ω, and always for one path only.

This brief collapses the two into one rule that the user learns once:

> **Name a net to see its voltage. Put an IProbe where you want power.**

| What `Probes` did | What replaces it |
|---|---|
| chose the node voltages recorded in `V` | **every labeled net** (the same set HB's `__LabeledNodes` marks), with its **voltage** spectrum `VSD` in dBV/Hz. A voltage needs no impedance, so nothing here assumes one |
| chose where `PSD`, `ACLR`, `Pavg_dBm`, `PAPR_dB`, `CCDF`, `IMD` are computed (into an assumed 50 Ω) | **every IProbe**: the same names, now from the power actually delivered through the probe |
| made its last entry "the output" | **every IProbe**, on a `branch` axis, with exact delivered power. There is no "last" any more; a trace pins `Iout` |

**No 50 Ω assumption is left anywhere in an envelope run.** A voltage is reported as a voltage, and a power is
measured where a current is known. This supersedes brief 1 §3's "envelope powers are into 50 Ω", which was the
interim rule while the node cubes were still powers. A system-level chain of 50 Ω blocks places one IProbe in front
of its load: one click, and its power numbers are then exact rather than assumed.

## 1. `R-envm3-1` — what a run records

- **Voltages:** every labeled net (`NodeMap.LabeledNames`, which the elaborator fills from `TestBench.LabeledNets`),
  in ascending node-index order, the same stable order HB uses. A schematic labels a net by naming it. A `.cnl`
  labels nets with its `labelednets` line.
- **Power:** every IProbe, as brief 1 records them.
- **Neither:** a hand-written netlist with no labels and no IProbes records the MOD_Source nodes, as an empty
  `Probes` does today, and the run notes once that no output power, gain or AM-AM was measured because no IProbe
  was placed. This is the only note this brief adds, and it fires only when there is no value to show.
- **The seam error** (`__EnvSeamError`) is measured over every recorded voltage and IProbe current.
  `StateProbes` (trap, thermal and internal nodes kept out of the warning) is unchanged.

**Size.** Recording every labeled net costs memory: `PulsedPA_pCW` labels all 16 of its nets (~35 MB of waveforms),
and a 40-net PA under a 5-point sweep is near 1 GB. Two things keep that visible and cheap:

- **The envelope card shows one read-only line** under SaveWaveforms, which is always present and never moves:
  "Records 4 labeled nets and 3 IProbes" (counts from the schematic). Hovering it lists them.
- **`SaveWaveforms=no` keeps every spectrum and every power, AM-AM and pulse number.** Only `V`, `I` and the `_t`
  time series go.

## 2. `R-envm3-2` — the node cubes (labeled nets): voltage only

| Cube | Axes | Unit | Meaning |
|---|---|---|---|
| `V` | `time, node, harmonic` (or `mixIndex`) | V | the envelopes, peak phasors, as today (with `SaveWaveforms=yes`) |
| `VSD` | `node, freq` | dBV/Hz | the **voltage spectral density**: 10·log₁₀ of the RMS voltage² per hertz |

- **`VSD` uses the same products, grid, window, segmenting and baseband treatment as today's `PSD`.** Only the
  normalisation changes: today's `PSD` is `S / (2·50)` in mW/Hz, where `S` is the peak-phasor density, and `VSD`
  is `S / 2` in V²/Hz. So at any node, old `PSD` (dBm/Hz) = `VSD` (dBV/Hz) + 10·log₁₀(1000/50) =
  `VSD` + 13.0103 dB. That identity is gate 3's check that nothing else moved.
- **The unit string is `dBV/Hz`.** It is numerically the same as dBV/√Hz on the RMS amplitude density, and the docs
  say so in one line, because both spellings are in common use.
- **The node axis is the labeled nets.** Data Display traces pin a node **by label**
  (`TraceResolve.TryRebindLabelledPins`), so a trace on `Vout` keeps resolving when the axis gains or reorders nets.
- **No other cube is computed at a node.** Channel power, ACPR, IMD, PAPR and CCDF are measurements of power, and
  they move to the IProbes (§3). D1 records the one alternative.

## 3. `R-envm3-3` — the IProbe cubes

Every power measurement moves onto the IProbe `branch` axis and becomes exact: everything that today reads "the last
probe", and the spectral power cubes that today are computed at every probe node. The names users already look for
are kept; they gain the `branch` axis in place of `node`, or in place of nothing:

| Cube | Axes | Meaning |
|---|---|---|
| `PSD` | `branch, freq` | **delivered power spectral density**, dBm/Hz: ½·Re of the cross-spectrum of the probe node's voltage and current, on the products, grid, window and segmenting `VSD` uses |
| `ACLR` | `branch, carrier, metric` | `Pch_dBm`, `ACPR1L`/`U`, `ACPR2L`/`U` from `PSD` |
| `IMD` | `branch, product` | mean delivered power of each listed product, dBm |
| `Pavg_dBm` | `branch` | delivered fundamental-band power: 10·log₁₀ of `Pprobe_fund_W` (brief 1) in mW. At `Iout` this **is** Pout |
| `PAPR_dB`, `CCDF` | `branch` / `branch, prob` | from the instantaneous delivered power p(t) = ½·Re(V₁·I₁*) |
| `Gt_dB` | `branch` | `Pprobe_fund_W` / available drive power |
| `DE_pct` | `branch` | 100 · `Pprobe_fund_W` / `Pdc_W`. Right for one PA; two PAs use measure lines (brief 2) |
| `NMSE_dB`, `Delay_s` | `branch` | the drive aligned to the probe node's fundamental envelope |
| `AMAM`, `AMAM_min`, `AMAM_max`, `AMPM`, `AMPM_min`, `AMPM_max` | `branch, Pin_inst` | §4 |
| the pulse cubes (`Pout_t_dBm`, `PulseTop_dBm`, `PulseDroop_dB`, …) | `branch, …` | as today, from delivered power at each probe |

- **`Pout_avg_dBm` is retired.** It named a role ("the output"), and a probe is a place: `Pavg_dBm` at `Iout` is
  the output power and `Pavg_dBm` at `Iin` the delivered input power, by one definition. The pulse cube
  `Pout_t_dBm` keeps its name, because "output" is what a pulse measurement is about (D4).
- **`Pin_avg_dBm`** (available drive) and **`Pdc_W`** (every non-zero supply) stay scalars: they never depended on
  an output.

- **Every IProbe gets every row**, so the `branch` axis is the same at every sweep point and stacks.
- **A probe that carries no RF** (a supply or bias probe) is NaN in the output rows: those whose mean
  fundamental-band power is below 1e-6 of the available drive (−60 dB). The plots skip NaN. That is a statement
  that there is no signal there, not a guess about meaning, and it keeps the axis stable.
- **`PSD` needs a cross-spectral Welch** in `SignalMeasure`, beside `Welch`, with identical segmenting and window,
  so that at a 50 Ω resistor it equals `VSD` at that node + 13.0103 dB (gate 3). For a passive load its real part
  is ≥ 0 in every bin; a negative bin is a real reverse flow and is plotted as NaN in dBm.
- **The lattice** (two fundamentals) and **pulsed CW** keep today's exclusions: no AM-AM, AM-PM, NMSE, ACLR or CCDF
  where they are excluded today.

## 4. `R-envm3-4` — AM-AM and AM-PM at an IProbe

Per probe, on the existing fixed `Pin_inst` grid (0.5 dB bins, `AmGridLoDbm`..`AmGridHiDbm`, `AmMinSamples`):

- **X:** the instantaneous **available** drive power, |V_drive(t)|² / (8·Re Z_s), unchanged. It is right for any
  source impedance. With several MOD_Sources it is still their summed drive (brief 1 D3).
- **Lag:** `SignalMeasure.Align(drive, V₁ at the probe node)`, per probe.
- **AM-AM:** 10·log₁₀(p_del(t + lag) / p_avs(t)), with p_del(t) = ½·Re(V₁·I₁*) the delivered fundamental-band
  power. A sample with p_del ≤ 0 is skipped, as a sample with p_o ≤ 0 is skipped today. This is exact for any load,
  reactive ones included.
- **AM-PM:** the phase of V₁ at the probe node relative to the drive, minus the mean gain phase, as today. A fixed
  load phase cancels in that subtraction, so the voltage phase is the right one.
- **Memory spread:** `AMAM_min`/`AMAM_max` as today, plus **`AMPM_min`/`AMPM_max`** (new): the phase spread at one
  drive level is the same memory seen in phase.

## 5. `R-envm3-5` — the memory cloud

The min/max cubes are the edges of the cloud. The cloud itself is two time series on one aligned time axis, emitted
for a single envelope that is not a pulse (where AM-AM is emitted):

| Cube | Axes | Meaning |
|---|---|---|
| `Pin_inst_t_dBm` | `time` | the available drive power at each kept sample |
| `Gt_t_dB` | `time, branch` | the instantaneous gain at each sample, the same values AM-AM bins |
| `PM_t_deg` | `time, branch` | the instantaneous phase deviation, the same values AM-PM bins |

- **Decimated** to at most 20,000 samples (D3): every m-th aligned sample, with m fixed by `Stop` and the step only.
  A Pin sweep therefore keeps one time axis and stacks. The `time` axis carries the real sample times.
- **Always emitted**, `SaveWaveforms=no` included: 20,000 points per probe is small, and the cloud is a measurement,
  not a waveform.
- **Plotted with what exists:** `vs` plus a trace drawn with markers and no line:

  ```
  ENV1.Gt_t_dB[:, "Iout"] vs ENV1.Pin_inst_t_dBm
  ```

  Under a Pin sweep, `vs` inherits the family by axis name, so one cloud per drive level appears with no edit.
  `data-display.md` `#versus` gains this as its memory example. **Check that 20,000 markers per trace, and a
  five-member family of them, draws at an interactive rate** in the Data Display. If it does not, lower the cap
  and record the measured point in `src/Ui/RESOLVED.md`; never ship a plot that stalls the canvas.

## 6. `R-envm3-6` — the setup card, the files and the CLI

- **The Probes field leaves the envelope card.** The read-only "Records …" line of §1 takes its slot, so nothing
  below it moves.
- **`Probes=` is deprecated**, read like `Zmeas` (brief 1). The listed nodes join the recorded voltages, so an old
  `.cnl` or agent-written netlist still records what it asked for, and the run notes once that `Probes` is no
  longer a setting and that power is measured at IProbes. The schema entry is marked deprecated, so `check` does
  not call it unknown. `CnlWriter` and `AnalysisSerialization` never write it.
- **CLI:** `circuitrf env --probes` goes the same way (D2). `explain --analysis` lists what a run will record: the
  labeled nets and the IProbes.

## 7. `R-envm3-7` — the examples, migrated

All four envelope examples move to the new system and become its demonstration. For each one: remove `EnvProbes`
and `EnvZmeas` from the `.csch`; rewire the `.cdd` traces; update the README sentences about probes. Every
`.cdd` trace must resolve after the migration (gate 9).

**`Envelope/NR20_ACPR`** and **`Envelope/NR20_2C_400MHz_single`**: they already carry `Iin`, `Iout`, `IDC` and the
labeled nets `VDD Vin Vout`.

- `V` traces on `Vin`/`Vout`: unchanged (labelled pins).
- `PSD`, `ACLR`, `PAPR_dB`, `IMD` traces: the `node` pin becomes a `branch` pin, `Vout` → `Iout` and `Vin` → `Iin`.
  The output traces read the same numbers (a 50 Ω load). **The input traces change, correctly**: they now show the
  power delivered into the gate network rather than the input node's voltage read into an assumed 50 Ω. The README
  says so in one sentence.
- Add a **Voltage spectrum** trace, `VSD` at `Vout`, beside the power `PSD` at `Iout`, so the example shows both
  kinds.
- `AMAM`, `AMAM_min`, `AMAM_max`, `AMPM`: pin `branch = Iout`. Add `AMPM_min`/`AMPM_max`.
- `Pout_avg_dBm` → `Pavg_dBm` at `Iout`. `Gt_dB`, `DE_pct`, `NMSE_dB`: pin `branch = Iout`.
- Add a **Memory** plot: the §5 cloud at `Iout`.
- Add a MEAS component (as `examples/System Design/TxFetFinal` carries one) with brief 2's lines:
  `Pin_del_dBm = dBm(ENV1.P("Iin"))`, `Gp_dB = dBm(ENV1.P("Iout")) - Pin_del_dBm`,
  `PAE_pct = 100*(ENV1.P("Iout") - ENV1.P("Iin"))/ENV1.P("IDC", 0)`, shown in the scalar table beside the
  built-ins.

**`Envelope/NR20_ACPR_vs_Pin`**: the same, under its Pin sweep. The ACLR-vs-Pin traces move from `node = Vout`
to `branch = Iout`, `Pout_avg_dBm` becomes `Pavg_dBm` at `Iout`, the other scalar-vs-Pin traces and the AM-AM family
pin `Iout`, and the MEAS lines plot against Pin with no edit. This is the example that shows a measure
line stacking over a sweep.

**`Pulsed Radar/PulsedPA_pCW`**: it has `IDC` only. Add `IProbe:Iout` between `CBO` and `RL`. Its first node stays
`out`, so the labeled net and every `V` trace keep their meaning, and `RL` moves to a new net. The pulse traces pin
`branch = Iout`. `tj` and the other `V` traces are unchanged. Regenerate the example through
`PulsedRadarExampleAuthoring` rather than by hand, so its authoring test stays the source.

**The output numbers do not move.** In all four the output IProbe feeds a 50 Ω resistor directly, or behind a
100 pF coupling capacitor (~0.8 Ω at 2 GHz). So the migrated output PSD, ACLR, IMD, PAPR, Pout, Gt, DE, AM-AM,
AM-PM and pulse figures equal the old ones within the gate 1 tolerance. The README's output numbers stay as written;
if one moves past that tolerance, stop and say why. The **input-side** spectra and PAPR (old `Vin` traces, now
`Iin`) are expected to move, because the old ones assumed a 50 Ω load at a node that has none. Record the old and
new values in `src/Engine/RESOLVED.md`.

## 8. Docs

- `simulations.md` Envelope: drop the `Probes` row and replace it with the §0 rule; the **Results** paragraph splits
  into node cubes (labeled nets: `V`, `VSD` in dBV/Hz) and IProbe cubes (every power); `#envelope-power` loses the
  last-probe and 50 Ω caveats, which no longer exist, and says what each IProbe gets. Brief 1's "without IProbes,
  powers are into 50 Ω" sentence is replaced by: "without an IProbe an envelope run reports voltages only; place
  one in series with a load to measure its power". Add a short **AM-AM, AM-PM and memory** subsection (§4–5) that
  links `data-display.html#versus`.
- `data-display.md` `#versus`: the cloud example of §5.
- `cli.md`: `--probes` (D2); `explain` lists the recorded nets and IProbes.
- `examples/Envelope/README.md`, `examples/Pulsed Radar/README.md`: the probe sentences.

Regenerate each edited page alone with `DocGen --page`, no figures. The example-derived figures move; the owner
regenerates figures at the end of the series.

## 9. Gates (one test per claim)

Envelope runs stay at `Stop` ≤ 20 µs unless the claim needs more. Run only the touched classes, once, and read the
TRX.

1. **Migration keeps the physics:** for each migrated example at a shortened `Stop`, every output figure the old
   build reported (Pout, Gt, DE, NMSE, AM-AM/AM-PM bins, pulse top and droop) equals the new per-probe figure at
   `Iout` within 0.01 dB, 0.1 pp, or 0.1°. Scratch harness, recorded in `src/Engine/RESOLVED.md`; not a committed
   test.
2. **Recording rule:** a bench with two labeled nets and one IProbe records exactly those two nets in `V` and one
   branch. With none of either, it records the MOD_Source node, and the one note appears.
3. **The two spectra:** (a) at any node, `VSD` + 13.0103 dB equals the old build's `PSD` at that node in every bin
   within 1e-9 dB (computed in the test from `V` by the old normalisation), which proves only the unit changed;
   (b) at a 50 Ω resistor behind an IProbe, the probe's `PSD` equals `VSD` at its node + 13.0103 dB in every bin
   within 1e-9 dB; (c) at a 25 Ω load, `PSD` integrates to `Pprobe_fund_W` within the Welch band-power tolerance the
   ACLR tests use.
4. **AM-AM exactness:** at a 50 Ω resistor, the per-probe `AMAM`/`AMPM` equal the old definition (computed in the
   test from `V`) in every filled bin. With an R + jX load, the per-probe AM-AM equals binned
   ⟨p_del/p_avs⟩ computed in the test from `V` and `I`.
5. **Memory spread:** on the synthetic GaN with drain lag (`ENV-PCW` §0's device), `AMAM_max − AMAM_min` and
   `AMPM_max − AMPM_min` are > 0 in the compressed bins, and 0 within numerical noise with every memory switch off.
6. **Cloud:** `Gt_t_dB` binned on the `Pin_inst` grid reproduces `AMAM` exactly; the decimated length is
   ≤ 20,000 and the same at every point of a Pin sweep.
7. **No-RF probe:** a supply IProbe's AM-AM, Gt and NMSE rows are NaN; its `Pprobe_W[…, 0]` is not.
8. **Deprecated `Probes=`:** an old netlist with `Probes="a,b"` and no labels records `a` and `b`, writes one note,
   and re-saves without the key. `check` reports nothing for it.
9. **Every example `.cdd` trace resolves** after migration, including under the sweep in `NR20_ACPR_vs_Pin`. This
   is a sequence test too: a re-run of `NR20_ACPR` with a Pin sweep added keeps every pinned-`Iout` trace valid.

## 10. Out of scope

- **Pairing an output probe with one of several MOD_Sources** for AM-AM's X axis. Two independently driven PAs need
  two runs today, or a `Drive=` pairing per probe as a follow-up.
- **A density (heat-map) rendering of the cloud.** Markers first; a density plot is a Data Display feature of its
  own.
- **Voltage-domain ACLR or IMD at a node.** A ratio of voltage² equals a ratio of power only when the load is a
  fixed resistance, which is the assumption this series removes (D1).

## 11. Decisions for the owner

- **Taken (owner, 2026-10-28):** the voltage spectrum is `VSD` in dBV/Hz at labeled nets, and `PSD` is the power
  spectrum at IProbes.
- **D1** nodes carry voltage only (`V`, `VSD`), and every power cube is at IProbes (recommended: one place per
  quantity, and no assumed impedance anywhere), or nodes also keep voltage-domain ACPR ratios for a chain with no
  IProbe.
- **D2** `circuitrf env --probes`: kept as a deprecated alias that adds nets to the recorded set (recommended: an
  agent's existing command line keeps recording what it asked for), or removed outright.
- **D3** the cloud's cap: 20,000 samples (recommended, subject to §5's drawing check), or a setting.
- **D4** `Pout_avg_dBm` retired in favour of `Pavg_dBm[branch]` (recommended: a probe is a place, not a role), or
  kept as an alias of `Pavg_dBm` so existing `.cdd` traces of it still resolve once they pin a branch.
- **D5** the order of work: §1–6 (engine, card, deprecation) as one commit, then §7–8 (examples, docs) as a second,
  so gate 1 compares the migrated examples against a build that already has the new cubes (recommended).
