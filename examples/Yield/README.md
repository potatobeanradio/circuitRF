# Yield, Corners and Centering

Three test benches, one for each question the **Yield** panel answers (View ▸ Panels ▸ Yield, tabbed
behind the Optimizer), and a fourth for the question to ask before any of them — which values matter —
in the **Sensitivity** panel (the tab between the Optimizer and Yield). Each one already has its tolerances, its specs and its run settings saved in
the schematic, so you can open it, pick the mode named below and press **▶** before reading anything
else. Every run here takes a second or two.

Every number below is for the saved seed, 1. The same seed draws the same trials on any machine, so
your run prints the same numbers.

## BandpassYield — centre, loosen, estimate

The three-resonator bandpass filter from the Optimization example, at that example's *Equiripple*
point. Each of the six parts has its own value here — 1.052 nH and 24.44 pF in each shunt
resonator, 44.67 nH and 0.5712 pF in the series one — because a tolerance belongs to a part. Two
parts that read one variable would always be drawn equal.

| | |
|---|---|
| Tolerances | every L and C **± 2 %**, Gaussian, ± 2 % at 3σ, truncated at 3σ |
| Correlation | `C1.C` and `C3.C`, **ρ = 0.9** — the two shunt capacitors come from one lot |
| Optimizer goals (`use=opt`) | `Passband` −0.5 … 0 dB · `StopLow` ≤ −25 dB · `StopHigh` ≤ −25 dB |
| Specs (`use=yield`) | `PassbandSpec` ≥ **−1 dB** over 0.9–1.1 GHz · `StopHighSpec` ≤ **−22 dB** over 1.35–2 GHz |
| Run | 500 trials, seed 1, random sampling, target 80 % |

The goals come in two sets, and that is the workflow this bench shows:

1. **Centre** with the tight goals. In the Optimizer, **Run** meets all three in 3 iterations and 41
   simulations — the values as drawn are the equiripple point rounded to four digits, so there is
   little to do. The optimizer ignores the two specs.
2. **Loosen.** The specs are what a built filter must meet, and they are looser than the goals the
   design was centred on. The yield ignores the three tight goals.
3. **Estimate.** In the Yield panel, choose **Yield** and press **▶**:

| | Yield | 95 % interval |
|---|---|---|
| Overall | **86.4 %** (432 of 500) | 83.1 % – 89.3 % |
| `PassbandSpec` | 86.4 % | 83.1 % – 89.3 % |
| `StopHighSpec` | 100 % | 99.3 % – 100 % |

The whole interval lies above the 80 % target, so the target is met. Every failure is the
passband's: the high stopband has 9 dB to spare at the centre, and its worst trial still has 8 dB.

**Open yield display** (the chart button) opens `BandpassYield.yield.cdd`, saved beside the
schematic and waiting for your run. It holds each spec's trials as a family against its limit, a
histogram of each spec's worst value, the yield against `C1.C`, and the statistics table.

**Contributions** ranks `C1.C` first for `PassbandSpec`, but a straight line through the trials
explains only about 4 % of that spec's spread. The reason is in the filter: a shunt capacitor
2 % low drops the passband edge at 0.9 GHz to −0.66 dB, and 2 % high drops the edge at 1.1 GHz to
−0.72 dB. The spec gets worse whichever way the part moves, and no straight line follows that. The
yield-against-`C1.C` plot says the same thing another way: the yield stays between about 70 % and
100 % across the whole spread of `C1.C`, so no single part's value decides whether a filter passes.
To see which trials fail and why, select one in the trial table and **Send trial to Tuning**.

## AmplifierCorners — six corners, one failing, then optimized across all of them

A small FET amplifier with resistive feedback (`Rfb` from drain to gate), biased at
V<sub>GS</sub> = `VGS` = −0.8 V from a supply `Vdd` = 3.3 V. The FET's current falls with
temperature (`Betatc` = −0.3 %/°C), so it loses gain hot, and it loses a little more at low supply.

| | |
|---|---|
| Variables | `VGS` −1.2 … −0.4 V and `Rfb.R` 150–1000 Ω, both tune and opt |
| Tolerances | `Rfb.R` ± 5 % · `Q1.Beta` ± 10 % (both at 3σ) · `Q1.Vto` σ 0.05 V |
| Goals | `Gain`: dB(S21) **≥ 12.3 dB** over 1.8–2.2 GHz · `Match`: dB(S11) **≤ −9.5 dB** over the same band |
| Corners | −40, 25 and 85 °C × `Vdd` 3.0 and 3.6 V, plus `WorstGain` |
| Optimizer | Gradient (Levenberg–Marquardt), seed 1, **across all corners** |

`Q1.Vto` is written as an absolute σ and the other two as percentages. A percentage follows the
nominal when the nominal moves; an absolute spread stays the same size.

**The six temperature and supply corners were made by Generate…** in Corners mode, from the
temperatures −40, 25, 85 and the values `Vdd` = 3.0, 3.6. Choose **Corners** and press **▶**:

| Corner | Gain margin | Match margin |
|---|---|---|
| nominal | 0.79 dB | 1.48 dB |
| `tm40_Vdd3p0` | 1.44 dB | 1.93 dB |
| `tm40_Vdd3p6` | 1.81 dB | 1.91 dB |
| `t25_Vdd3p0` | 0.61 dB | 1.39 dB |
| `t25_Vdd3p6` | 1.01 dB | 1.58 dB |
| **`t85_Vdd3p0`** | **−0.23 dB — fails** | 0.59 dB |
| `t85_Vdd3p6` | 0.18 dB | 0.90 dB |
| `WorstGain` | 0.18 dB | 1.26 dB |

Hot at low supply is the one corner that fails, and only on gain. With **MC at each corner** ticked,
the grid shows each corner's yield instead: 100 % at room temperature, 74.5 % at 85 °C and 3.6 V,
and **13.5 %** at 85 °C and 3.0 V.

That run has trials to plot. **Open yield display** writes `AmplifierCorners.yield.corners.cdd` beside
the schematic: one tab per corner, each with that corner's trials, histograms, yield sensitivity and
statistics table. It opens on `t85_Vdd3p0`, where 27 of the 200 trials pass. The corners run without
MC has no trials, so there is no display of it.

**`WorstGain` is a statistical corner.** It was saved from a Yield run at the nominal: trial 131
had the smallest gain margin of the 200 (0.18 dB), and right-click ▸ **Save as corner…** on its row
made a corner that replays that trial's draws — its `Rfb`, `Beta` and `Vto` offsets — around
whatever the design's values are now.

**Optimized across the corners.** The Optimizer is set to evaluate every point at all eight corners,
so each point costs eight simulations. **Run** meets both goals at every corner in 5 iterations and
128 simulations:

| | Start | Best |
|---|---|---|
| `VGS` | −0.8 V | −0.772 V |
| `Rfb.R` | 300 Ω | 310.3 Ω |
| worst gain, `t85_Vdd3p0` | 12.07 dB | 12.30 dB |

The report names `t85_Vdd3p0` as the corner that binds both goals. The optimizer raised the bias
current just enough to bring that corner's gain to 12.3 dB, and stopped there.

**Meeting a goal at a corner is not a yield at that corner.** The optimizer meets the gain goal
at `t85_Vdd3p0` with nothing to spare, so the parts' tolerances push about half of the amplifiers
built below it: run **MC at each corner** again after **Push**, and that corner's yield is 43 %. To
leave room for the tolerances, write the goal with a margin (12.6 dB, say), optimize, and run the
corners' Monte Carlo again.

## DividerCentering — from 70 % to above 95 %

A 1 V supply divided down to a 0.5 V reference, which must stay inside **0.49–0.51 V**. Both
resistors are ± 3 % Gaussian (at 3σ). `R2` is 1033 Ω and `R1` starts at 1000 Ω, so the output sits at
0.508 V, close to the top of the window.

| | |
|---|---|
| Designable | `R1.R` 500–2000 Ω (opt) |
| Tolerances | `R1.R` and `R2.R` ± 3 % (3σ) |
| Spec | `Vout` = DC1.V("out"), **in 0.49 … 0.51 V** |
| Run | 500 trials, seed 1, target 95 % |
| Centering | CMA-ES, 100 common trials, 10 iterations, 1000 verification trials |

Choose **Yield** and press **▶**: **70.8 %** [66.6 %, 74.8 %] of 500 trials pass, short of the
95 % target.

Now choose **Centering** and press **▶**. The search moves `R1` and scores every position on the
same 100 trials, then the starting value and the centred one are each run on 1000 fresh trials:

> yield 73 % [70.1 %, 75.7 %] → 99.1 % [98.3 %, 99.6 %] on the same 1000 fresh trials; the
> intervals do not overlap

`R1` moves from 1000 Ω to 1039 Ω, which puts the output close to 0.5 V, in the middle of its window.
That verified pair, not the search's own figure, is the result. **Lock in** keeps the centred value
as a preset, and **Push** writes it into the schematic.

With **Surrogate: quadratic** in the centering settings, each position costs 10 simulations instead
of 100, because circuitRF fits `Vout` to a smooth curve through a few runs and counts the yield on
the curve. The whole run takes 410 simulations instead of 4,100 and verifies at 99.5 %
[98.8 %, 99.8 %] — the final check is still simulated.

## SnpSensitivity — which S-parameters move the gain

An amplifier's Touchstone block (`AMP`) driving a spiral inductor's (`L1`) into a 0.5 pF shunt
capacitor. Two specs over 2.4–3 GHz: `Gain` dB(S21) **≥ 9 dB** and `Isolation` dB(S12) **≤ −36 dB**.
`C1.C` is ± 5 % (3σ); nothing else has a tolerance.

Open the schematic and the **Sensitivity** tab. Before anything runs it lists both blocks under
**S-parameter blocks**:

| Block | | Why |
|---|---|---|
| `AMP` | 2 ports · **untied** · 8 factors | an amplifier: S21 is about 50 dB above S12, so each of the four entries moves on its own |
| `L1` | 2 ports · **tied** · 6 factors | a passive spiral: S21 = S12, so one value moves both |

and the cost: 15 factors, **31 simulations**. Press **▶**. For `Gain` the list reads:

| Entry | basis | share |
|---|---|---|
| `L1 · S21` | 0.1 dB · 1° | 54 % |
| `AMP · S21` | 0.1 dB · 1° | 33 % |
| `L1 · S11` | 0.1 dB · 1° | 6.4 % |
| `AMP · S22` | 0.1 dB · 1° | 5.5 % |
| `C1.C` | σ | 1.1 % |

The two S21s are almost all of it — the gain is the amplifier's gain less the spiral's loss. `AMP · S11`
and `AMP · S12` are last with 0 %: with 50 Ω at the input the gain does not depend on them. Choose
`Isolation` in the picker and `AMP · S12` comes second, at 42 % — the same block, ranked by a different
spec. Under **Specs**, `Gain` reads m₀ 0.048 · σ 0.020: met at the nominal, by about two and a half times
what all these steps together move it.

Things to click:

- **Port** regroups the same run by port (no simulation): `L1 · port 1` and `L1 · port 2` lead, at 60 % and
  54 % — they overlap, because S21 belongs to both ports.
- **Instance**, then the arrow on `L1`, opens it into its entries, and each entry into its dB and phase.
- Select `AMP · S21`: the matrix tile opens, `S21` the darkest cell, with **Tie reciprocal pairs** unticked.
  Select `L1 · S21` and the tile colours S21 and S12 together, ticked.
- Untick `AMP` under S-parameter blocks: the cost drops to **15 simulations**, and after **▶** `AMP` is a
  greyed `excluded` row at the bottom — left out, not found insensitive.
- Right-click `C1.C` ▸ **Send to Tuning**, or `L1 · S21` ▸ **Add tolerance** — then **Run Yield on these**
  opens the Yield panel on the same schematic in Monte Carlo mode.

The command line runs the same: `circuitrf yield sens SnpSensitivity/schematic/SnpSensitivity.csch --by port`.

## Settings

The trial counts are kept small so that every run here finishes in a second or two:

- **BandpassYield, 500 trials.** The interval is ± 3 %. Halving its width takes four times the
  trials: about 2,000 for ± 1.5 %.
- **AmplifierCorners, 200 trials.** Enough for the corner yields above to be told apart. At 200
  trials the interval around a 100 % yield still reaches down to 98.2 %.
- **DividerCentering, 100 common trials.** Every candidate is scored on the same 100 trials. That
  is enough to tell 60 % from 100 %, but not 99 % from 100 %. The 1000 verification trials are what
  measure the final yield.

The amplifier's sweep is 21 points over 1–3 GHz (100 MHz steps), five of them in the goals' band.
The bandpass keeps the Optimization example's 151-point sweep.

The command line runs the same: `circuitrf yield estimate BandpassYield/schematic/BandpassYield.csch`,
`circuitrf yield corners AmplifierCorners/schematic/AmplifierCorners.csch --mc`,
`circuitrf opt AmplifierCorners/schematic/AmplifierCorners.csch` and
`circuitrf yield center DividerCentering/schematic/DividerCentering.csch`.
