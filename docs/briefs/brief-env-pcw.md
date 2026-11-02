# Brief ENV-PCW — pulsed CW in the Signal Designer, the pulse measurements, and a pulsed-radar example

**Tag:** `R-envpcw-n` · **Depends on:** the envelope analysis as merged, through ENV-SEG, ENV-TLIN and ENV-FIT
**Area:** `src/RfCore/Signals/SignalSpec.cs` (`CarrierStandard`, `CarrierSpec`, `ParseCarrier`, `Label`),
`src/RfCore/Signals/ModulatedSignal.cs` (the carrier switch), `src/Engine/Envelope/EnvelopeDataSet.cs` (new cubes),
`src/Ui/Signals/SignalDesignerViewModel.cs` and its view, `docs/user/src/reference/signal-designer.md`,
`components.md` (MOD_Source), `simulations.md` (Envelope); `tests/Ui.Tests/ModelComposer/Support/SyntheticGan.cs` and
`tests/Ui.Tests/Examples/ModelComposerExampleAuthoring.cs` (a second synthetic device, §5); a new example workspace
`examples/Pulsed Radar/` and its row in `examples/examples.json` (§6). Findings go in `src/Engine/RESOLVED.md` (engine) and
`src/Ui/RESOLVED.md` (Designer). Every existing LTE, NR, CW and FILE signal must generate **byte-identical** samples
after this brief, and every existing envelope run must give byte-identical results. If any of them changes, stop
and say why.

---

## 0. The gap

A pulsed-radar power amplifier is specified by what happens *inside* a pulse. **Pulse droop** is the output falling
across the top of each pulse, as the channel heats and the drain supply sags while its storage capacitors discharge.
A typical system budget is about 0.5 dB, and 0.3 dB is preferred. **Pulse-to-pulse stability** is the spread of
amplitude and phase between pulses at the same point in the pulse. On GaN it needs both thermal and trapping memory
to reproduce. Harmonic balance cannot see either effect: it solves one steady state, so the device sits at one
temperature and one trap state. The envelope run can see both, because it already steps a ComposedFET's thermal
and trap states and the bias network as real memory over time.

**What exists today is enough to run it, and nothing to set it up or read it:**

- **No pulse signal.** The Signal Designer offers LTE, NR, CW and a custom I/Q file. A pulse has to be written by
  hand as a two-column I/Q file with a `# sample_rate_hz` header.
- **The power means the wrong thing for a pulse.** `Pavg` is the average over the whole record, off-time included.
  A radar engineer states the **pulse-top** power. A 50 µs pulse in a 100 µs period at a stated 15 dBm `Pavg` is
  ~18 dBm at the top, and the user has to work that out (10·log₁₀ of the power duty, edges included).
- **The bandwidth has to be faked.** A FILE carrier measures its bandwidth from the file: twice the edge holding
  99 % of the power, over 0.9. A pulse's sharp edges make that wide and arbitrary, and the envelope rate, the
  preview span and the ACLR bands are all read from it. Today the workaround is a hand-written
  `# channel_bw_hz` line.
- **Nothing is measured.** The run reports ACLR, CCDF, PAPR, AM-AM and NMSE, which mean little for a pulse train.
  It reports no droop, no pulse-to-pulse figure, no rise or fall time and no phase droop. The user reads the probe
  voltages from the `.npy` and computes Pout(t) by hand.
- **Where the run starts matters, and the user is not told.** The envelope run starts from harmonic balance at the
  **first stimulus sample** (`EnvelopeEngine.SteadyStart`, at `Drv(s, 0)`). A record that starts in the off-time
  starts at quiescent, which is right for the first pulse. A record that starts on the pulse top starts at the CW
  steady state, with the device already fully heated, and the first pulse then shows no droop at all.

### Measured (what made this brief)

The fixture is the shipped example `examples/Model Composer/synthetic-gan/gan_4x125.cmodel` (one thermal pole:
`Rth` 25 °C/W, `Cth` 0.4 µJ/°C at its own size; drain lag with 10 ns capture and 1 ms emission). The bench is the
envelope bench the traps work used, with a FILE carrier playing a hand-made pulse: period 100 µs, 50 µs top
starting 10 µs into the record, 0.5 µs raised-cosine edges, 30.72 MS/s, `# channel_bw_hz 4e6`, `Pavg` 15 dBm
(power duty 0.494, so ~18.1 dBm at the top), `Stop=200e-6` (two pulses), `Segments=1`:

```
Pin = 15
MOD_Source:SRC1 n1 0 Fc=2e9 Pavg=Pin dBm Z=50 Ohm Carrier[1]="FILE;File=pulse.txt;Fs=30.72MHz;Off=0;P=0;Seed=1"
C:C1 Vin n1 C=100 pF
L:L1 n2 Vin L=100 nH
Vdc:V1 n2 0 Vdc=-0.8 V
ComposedFET:X1 Vin Vout 0 File="examples/Model Composer/synthetic-gan/gan_4x125.cmodel" N=8
L:L2 VDD Vout L=100 nH
Vdc:V2 VDD 0 Vdc=8 V
C:C2 Vout n5 C=100 pF
R:RL n5 0 R=50 Ohm
analysis ENV1 type=env Stop=200e-6 Accuracy=standard MaxHarm=5 Segments=1 Probes="Vin,X1.t,Vout"
```

Each run took 39 s (Debug), with 1.33 Newton iterations per step and seam error 0. Pout is the harmonic-1 envelope
at `Vout` in 50 Ω, read 1.5 µs after the pulse starts and again 1 µs before it ends:

| instance switches | pulse | Pout start → end | droop | T (thermal node) start → end | T in the off-time |
|---|---|---|---|---|---|
| `SelfHeating=false` | 1, 2 | 25.878 → 25.872 dBm | 0.007 dB | 0 | 0 |
| `SelfHeating=true DrainLag=false` | 1, 2 | 26.575 → 26.568 dBm | 0.007 dB | 1.44 → 2.09 °C | back to 1.37 °C |
| `SelfHeating=true` (drain lag on, the default) | 1 | 25.864 → 25.848 dBm | 0.016 dB | 1.47 → 2.36 °C | falls to **0.53 °C** |
| | 2 | 25.871 → 25.848 dBm | 0.023 dB | 0.73 → 2.35 °C | 0.53 °C |
| `SelfHeating=true DrainLag=false Rth=500 Cth=2e-8` (τ kept at 10 µs) | 1, 2 | 26.08 → 25.72 dBm | **0.36 dB** | 51.7 → 97.9 °C | — |

What that says:

1. **The mechanism works today.** Temperature rises across the top with the thermal pole's time constant, and the
   run starts from quiescent because the record starts in the off-time.
2. **The fixture barely heats, and its Rth is not why.** At `N=8` (1 mm of gate) its own thermal resistance is
   25 × 4/8 = 12.5 °C/W, which is realistic for a 1 mm GaN die. What is not realistic is its POWER DENSITY: it carries
   ~86 mA at Vgs = 0 and 10 V (about 0.1 A/mm, against ~1 A/mm for GaN), and its I–V data stops at Vds = 20 V
   (at 28 V its drain current rises as the gate goes more negative: extrapolation, not a device). ΔT = Rth·P_diss,
   and since `Rth` scales as N0/N while P_diss scales as N, ΔT depends on power density alone. So the top heats by
   0.65 °C and droops 0.007 dB, at the noise of the reading. `Rth=500 Cth=2e-8` (40×, τ kept) stands in for the
   missing power density: the top then heats by 46 °C, about what a real 1 mm GaN die dissipating 3–4 W during a
   pulse would see at 12.5 °C/W, and droops 0.36 dB. **500 °C/W is not a realistic thermal resistance** — it is a
   test setting that reproduces a realistic temperature rise on a device that dissipates too little. At an honest
   `Rth=20` (the top of the realistic range for a 1 mm die) and driven 5 dB harder, it heats 2.5 °C and droops
   0.024 dB. **The ComposedFET handles a pulse correctly — thermal state, traps, the T pin, a thermal ladder — but
   this model's data is a half-watt device** (271 mA at Vgs +1.4 V, 8 V: ~0.27 A/mm). A pulsed example needs a
   ComposedFET built from data at radar-GaN power density, which §5 makes.
3. **Drain lag makes pulse 1 differ from pulse 2.** The traps capture at the pulse peak in 10 ns and emit over 1 ms,
   so the quiescent current after the first pulse is lower. The off-time temperature settles at 0.53 °C instead of
   returning to the 1.35 °C quiescent value. That is current collapse, the physical effect a pulse-to-pulse figure
   exists to report. It is not a defect.

---

## 1. The pCW carrier

A fifth carrier standard, `PCW`, generated rather than read from a file. In the netlist:

```
Carrier[1]="PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"
```

| field | meaning | default |
|---|---|---|
| `PRI` | pulse repetition interval (s). The record is one PRI, which repeats, so the run's `Stop` sets how many pulses are simulated. | 100 µs |
| `PW` | pulse width (s), from the 50 % point of the rising edge to the 50 % point of the falling edge | 10 µs |
| `Rise`, `Fall` | 10–90 % edge times (s), raised-cosine | 100 ns |
| `Delay` | the off-time before the first rising edge (s). It must be > 0, so the run starts at quiescent (§0). | PRI − PW, centred, or 1 µs, see D4 |
| `Off`, `P` | as for every carrier | 0 |

- **Time fields take `s`, `ms`, `us`, `ns`** and a bare number in seconds, through a `ParseSeconds` beside
  `ParseHz`, with the same exact-case rule (`m` is milli). `µs` is accepted as an alias of `us`.
- **Refused with a sentence that names the field:** `PW` ≥ `PRI`, an edge longer than half of `PW`, a negative
  value, `Delay + PW > PRI`, and a pCW carrier mixed with any other carrier (see D2).
- **The envelope is real and non-negative**: exactly 0 in the off-time, exactly 1 on the flat top, raised-cosine
  edges. There is no intrapulse modulation in v1 (§8).
- **Its bandwidth is the edges', not a guess.** Set `BandwidthHz` from the faster edge, and say how in a comment
  (for example the 99 %-power bandwidth of a raised-cosine edge of that 10–90 % time). The envelope rate, the
  preview span and the ACLR bands then follow from it. Pick the native rate as CW does (1.92 MHz × 2ᵏ, the smallest
  k that puts at least ~10 samples in the faster edge and a whole number of samples in a PRI). Report a PRI that
  cannot hold a whole number of samples at any such rate as a refusal, not a rounding.
- **`Label`** reads `pCW 50 µs / 100 µs` (width / PRI), and `Summary` follows it.

## 2. Power: pulse-top, not record average

**D1 (owner).** For a pulse, the number a user states is the pulse-top power, and `Pavg` cannot carry it without the
user doing the duty arithmetic. Options:

- **(a, recommended)** A pCW record is normalised to **unit power on the flat top**, not to unit mean power, and
  `Pavg` on a source whose only carrier is pCW means the **pulse-top** available power. The inspector and the
  Designer label it *Pulse-top power* for such a source. `Pin_avg_dBm` and `Pout_avg_dBm` stay record averages,
  so nothing about them changes meaning. One key keeps one place where power is written, at the cost of `Pavg`
  meaning the top for this one carrier kind.
- **(b)** A new MOD_Source key `Ptop`, refused together with `Pavg`, legal only when the source's one carrier is
  pCW. It is more explicit, and it adds a second power key that every reader of a MOD_Source has to know.
- **(c)** `Pavg` stays the record average and the Designer computes it from a pulse-top entry. This is the least
  code and the worst outcome: edit `PW` in the netlist and the top power moves silently.

Whichever is chosen, the Designer shows **both** the top power and the record average, and the duty they differ by.

## 3. The Signal Designer

- **A *Pulsed CW* carrier kind** in the carrier list, beside CW. Its card has PRI (with PRF shown read-only beside
  it), pulse width (with duty shown beside it), rise, fall and delay. Entry and refusal behaviour, units and the
  outlined-invalid-entry pattern are the same as the card's other numeric fields.
- **The preview shows the pulse in time.** Spectrum and CCDF are the Designer's two views today, and a CCDF says
  nothing about a pulse train. For a pCW source, show |envelope| against time over one PRI, with the edges zoomable,
  and keep the spectrum. Hide the CCDF and the EVM tab for a pCW source, and say why in the panel's empty-state
  text, not in a note under a value.
- **Presets:** none in v1. A pulse is four numbers and nothing standardises them.
- **Greyed for a pCW source**, as for a custom file: Clip to PAPR, spectral shaping, Record (the record is the
  PRI). Say in the doc which are greyed and why.

## 4. What the envelope run reports for a pCW source

New cubes in `EnvelopeDataSet`, added only when the source's carrier is pCW (an existing run gains nothing):

| cube | axes | meaning |
|---|---|---|
| `Pout_t_dBm` | time | output power at the last probe (the probe `Pout_avg_dBm` uses), so it plots in the Data Display with no arithmetic |
| `PulseTop_dBm` | pulse | Pout at the droop window's start, per pulse |
| `PulseDroop_dB` | pulse | Pout(window start) − Pout(window end), per pulse; positive = drooping |
| `PulsePhaseDroop_deg` | pulse | the output phase change relative to the input across the same window |
| `PulseRise_s`, `PulseFall_s` | pulse | 10–90 % of the output envelope |
| `PulseToPulse_dB`, `PulseToPulse_deg` | — | peak-to-peak spread over the pulses of `PulseTop_dBm` and of the output phase at the window start (the first pulse counts) |

- **The droop window** is the flat top from `MeasureStart` to `MeasureEnd`, as fractions of the top (D3). A
  pulse only partly inside `Stop` is not measured, and the report says how many pulses were.
- **ACLR, CCDF, PAPR, AM-AM and NMSE** are not reported for a pCW source (D5). They are defined for a modulated
  signal, and on a pulse they print numbers that look like results. The CLI table prints the pulse figures instead.
- **The Messages pane** says what was measured: *n pulses, droop window 10–90 % of the top*.
- **Run cost has to be stated up front, because a thermal train is long.** The run settles to a steady pulse train
  only after several of the slowest thermal or trap time constants. On the fixture's 1 ms emission that means
  milliseconds of record at a step of a few ns. Measured above: 200 µs took 39 s (Debug). Before a pCW run starts,
  report the slowest memory the run already finds (`EnvelopeMemory`) against `Stop`, as *the first pulse from cold*
  or *a settled train*. D6 is about not paying that cost.

## 5. A radar-class device: a second synthetic GaN, built through the Model Composer

The example and the physics gate need a ComposedFET that heats the way a radar transistor does, with a thermal
resistance in the range a designer expects (about 1–20 °C/W for a packaged part, ~1–3 °C/W for a 10–20 mm device).
Build it exactly the way `synthetic-gan` was built, so it is checkable against its own truth:

- **Generalise `SyntheticGan` into a parameter set** with today's constants as one instance and a radar instance as
  the second. The existing instance must regenerate `examples/Model Composer/synthetic-gan`'s data **byte-identical**
  (that is the refactor's gate). The truth stays an SDD, independent of the composer's code.
- **The radar truth, as targets** (state the values chosen, and why, in `src/Ui/RESOLVED.md` and the example README):
  - a measured cell of 2 mm (for example 8 × 250 µm), placed in the example at 12 mm (N = 48);
  - ~1 A/mm peak current, knee ~5 V, pinch-off near −3 V, I–V and S data reaching ≥ 100 V (twice a 48 V supply;
    the first model's 20 V data limit is the reason it cannot be biased like a radar part);
  - drain current falling ~0.15–0.3 %/K (the first model's `Kt` is 0.4 %/K at a power density where it never
    mattered; at radar density it would droop ~1 dB per 30 °C, which is high);
  - **a three-pole Foster thermal ladder**, at the 12 mm placement ~1.5 °C/W in total: a fast channel/epi pole
    (~1 µs), the die (~20 µs) and the die attach and flange (~0.5 ms). One 10 µs pole understates droop on a long
    pulse: in a real part the package keeps pulling the gain down for the whole pulse;
  - drain lag as the first model has it (10 ns capture, 1 ms emission), so pulse-to-pulse behaviour is real.
- **Fit it in the composer**, as `ModelComposerExampleAuthoring` fits the first: the Thermal step **types** Rth and
  its ladder (`fraction:τ` pairs), as the first model types its Rth, for the same reason (drain lag is modelled).
  Commit the `.cmc`, its data and the exported `.cmodel` inside the example workspace (§6), so a user can open the
  Thermal step and see where every number came from.
- **Acceptance of the device** (on the example bench, §6): at 48 V and its design drive, a 100 µs pulse heats the
  junction by 30–60 °C across the top and droops **0.3–1 dB**; the model's Pout and drain current agree with the
  truth SDD within the tolerance the first model's own acceptance used. If the droop lands outside 0.3–1 dB, adjust
  the drain current's temperature coefficient within the range above, never the thermal resistance.

## 6. The example workspace: `examples/Pulsed Radar/`

A self-contained workspace (File ▸ New ▸ Examples copies it on its own, so nothing in it may point into another example):

- **The device**: the radar ComposedFET of §5, with its `.cmc` and data, as a reusable cell placed at N = 48.
- **`PulsedPA`**: a 48 V class-AB stage at a radar frequency (S-band, e.g. 3 GHz), biased and matched for the
  device (a load near its power optimum, not a bare 50 Ω), drain fed through a choke with a **storage capacitor and
  the supply's source resistance drawn on the schematic**, so supply sag is in the run as well as heating.
- **`PulsedPA_pCW`**: one envelope run on a pCW source, 100 µs pulses at 10 % duty (PRI 1 ms) or as short a PRI
  as keeps the run under ~2 min (state the trade in the README, per the examples' rule for settings traded away),
  at least three pulses, so pulse 1 (from cold) and the later pulses can be compared.
- **`PulsedPA_pCW.cdd`**, opened by Simulate: Pout(t) over the run, the junction temperature from the T pin over
  the same time, Pout(t) across one pulse top zoomed, and the pulse table (`PulseDroop_dB`, `PulsePhaseDroop_deg`,
  `PulseToPulse_dB`/`_deg`, rise and fall).
- **`PulsedPA_droop_vs_PW`** (if the run cost allows): a parametric sweep of `PW` (10, 30, 100 µs), droop against
  pulse width, which shows each thermal pole's time constant.
- **`README.md`**, in the shape of the other examples' READMEs: what each bench runs, the expected results
  (droop, ΔT, Pout, efficiency) measured on the merged build, **the expected wall-clock of each run**, and the
  settings traded away for speed.
- **A row in `examples/examples.json`**, and `ExampleWorkspacesTests` passes with it (index vs disk, every schematic
  extracts with its device in it).

## 7. Gates

The physics gates use the radar device of §5 at a realistic thermal resistance. No gate overrides `Rth` to make a
device heat: §0's `Rth=500` was a diagnosis, and a gate built on it would teach the next reader that the number is
normal.

1. **Parse and round trip:** the netlist form, `ToString`/JSON, the `Label`, every refusal in §1 (one test per
   refusal *rule*, not per value).
2. **Generated record:** exactly 0 in the off-time, exactly 1 on the flat top, a 10–90 % time equal to `Rise` and
   `Fall` to one sample, a 50 %-to-50 % width equal to `PW` to one sample, and a power duty that matches the
   analytic one.
3. **Starts at quiescent:** with `Delay` > 0, the thermal node at t = 0 equals the DC operating point's.
4. **No heat, no droop:** `SelfHeating=false DrainLag=false GateLag=false`: `PulseDroop_dB` ≤ 0.01 dB.
5. **The droop is the thermal physics, checked independently of the envelope engine.** From harmonic balance
   alone: (a) the dissipation at pulse-top drive, from one HB run; (b) the ladder's rise
   ΔT(t) = P_diss·Σᵢ Rᵢ(1 − e^(−t/τᵢ)) at the window's two ends; (c) the gain at those two temperatures, from two
   HB runs with `SelfHeating=false` and `Temp` set to each. The envelope's `PulseDroop_dB` must match
   G(T_start) − G(T_end) within 10 % or 0.02 dB, whichever is larger. This is the gate that proves the feature
   measures what it claims, because it holds even if the envelope's own thermal state were wrong.
6. **Drain lag makes the pulses differ:** with drain lag on and two pulses, `PulseToPulse_dB` > 0, and the
   off-time temperature after pulse 1 is below the quiescent value, as measured in §0.
7. **Nothing else moved:** every existing LTE/NR/CW/FILE generation byte-identical, every existing envelope test
   unchanged.
8. **Designer:** the card's fields, the refusals, the time preview, the greyed controls and the Apply round trip,
   in `Ui.Tests` on the view model.
9. **The first synthetic GaN is unchanged:** its regenerated data byte-identical after `SyntheticGan` is
   generalised (§5).
10. **The radar device meets §5's acceptance**, and the example's `PulsedPA_pCW` run reproduces the README's
    numbers (one test, the example as a user opens it).

Run only the test classes this touches (`RfCore.Tests` signals, the envelope classes in `Engine.Tests`, the Signal
Designer classes in `Ui.Tests`), once, and read the TRX. Gates 5, 6 and 10 are envelope runs: keep each record as
short as the claim allows (two pulses), and if either crosses ~5 s, measure it and tag it per the repo's rule.

## 8. Out of scope, and said so in the doc

- **Intrapulse modulation**: linear FM chirps and phase codes, which real radar pulses usually carry. The pCW
  record is real, so adding a phase function later changes the generator and nothing downstream. Name it as the
  natural follow-up.
- **Gate or drain pulsing** (the bias switched with the RF). That is a pulsed bias source, not a signal, and it is a
  separate brief.
- **A pulsed source in harmonic balance or loadpull.** There, a MOD_Source is a CW tone at its power. For pCW that
  power is the pulse top under D1 (a), which is the right steady-state answer, and the doc says so.
- **Spectral mask and radar range-sidelobe figures.**

## 9. Decisions for the owner

- **D1** pulse-top power: (a) `Pavg` means the top for a pCW-only source (recommended), (b) a `Ptop` key, or
  (c) Designer arithmetic only.
- **D2** pCW mixed with other carriers: refuse in v1 (recommended; a pulsed carrier beside a modulated one has no
  clear power or record meaning), or allow with the record = lcm(PRI, 0.5 ms).
- **D3** droop window default: 10–90 % of the flat top (recommended), or from the end of the rise + a fixed settle
  to the start of the fall.
- **D4** default `Delay`: centre the pulse in the PRI (recommended; equal off-time both sides, so the first pulse
  starts cold and the last ends clear of the wrap), or a fixed 1 µs.
- **D5** ACLR/CCDF/AM-AM for a pCW source: omit (recommended), or keep them and label them.
- **D6** a settled train without simulating milliseconds: start the slow thermal and trap states from the train's
  **average** dissipation instead of quiescent, as a `Start=quiescent|train` setting. Measure the error against a
  long run before offering it. Recommended as a follow-up brief, not part of this one.

- **D7** the example's frequency, pulse and PRI: S-band 3 GHz, 100 µs at 10 % duty (recommended, a common
  long-pulse case where the package pole shows), or the owner's own. A 1 ms PRI makes each pulse ~10× the run cost
  of the pulse alone; if three pulses at 1 ms exceed ~2 min, shorten the PRI and say so in the README.
- **D8** the order of work: §1–4 (signal and measurements), then §5 (device), then §6 (example), each its own
  commit, so the device can be judged before an example is written around it (recommended).

Edit the doc sources only. The owner regenerates `docs/user` at the end of a series.
