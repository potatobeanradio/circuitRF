# System Design

A complete 2.4 GHz radio, built from the **System** block library: transmitters and receivers,
each drawn twice — once as a **direct-conversion (zero-IF)** radio and once as a
**superheterodyne** — so the two architectures can be compared on the same band, at the same
power, with the same parts.

Six benches. Every number below is what the shipped file produces; nothing here is quoted from a
datasheet.

## Why 2.4 GHz

The band is 2400-2483.5 MHz, and it is the one worth building an example on:

- **It is shared.** Several short-range standards occupy it at once, so one front end illustrates
  all of them without the example belonging to any particular one. The benches name the **band**
  and never a standard, which is also why nothing here is tuned to a channel plan.
- **Direct conversion is what is really used there**, so the zero-IF bench is not a strawman - and
  putting a superhet beside it at the *same* RF frequency makes the comparison an argument about
  the conversion plan alone.
- **+30 dBm is a plausible output**, which is enough power for passive intermod to be worth
  computing rather than asserted.

## The benches

| Cell | What it shows |
|---|---|
| `TxDirectConversion` | I/Q modulator, four gain stages, coupler, circulator |
| `TxSuperhet` | the same output section fed through a 374 MHz IF |
| `RxDirectConversion` | I/Q demodulator, and the DC offset that comes with it |
| `RxSuperhet` | preselector, LNA, downconversion, IF strip |
| `TxFetFinal` | System driver stages into a **nonlinear FET** final |
| `CascadeBudget` | the gain / IP3 / noise-figure arithmetic, beside a real S-parameter run |

A **T/R switch** is the one part all five radio benches share. It is the same SPDT in every one of
them, wired the same way - `T1` is the transmit arm, `T2` the receive arm - and the only thing the
transmitters and receivers differ in is `State`. Its `IL`, `Isolation` and return loss are VAR-block numbers
(`SWil`, `SWiso`, `SWrl`), so changing the part changes every bench that carries it.

Every bench **sweeps its drive level**, so each figure of merit is a curve rather than a number:
the transmitters over baseband or IF drive, the receivers over signal level at the antenna. The
single-point numbers quoted below are read off those sweeps at the operating point each bench
centres on.

## Data displays

Beside each bench is a **`.cdd` data display** of the same name, laid out and ready: output power,
gain and compression, two-tone linearity, spectral purity, and a table carrying every metric at
every drive point. `CascadeBudget.cdd` shows the measured front-end response instead, because its
budget numbers are scalars and live in the schematic's own measurement blocks.

**Run the bench first.** A display reads `results/<Cell>.npy`, which Simulate writes; the results
themselves are not shipped, so the order is: open the schematic, Simulate, open the display of the
same name. Measured wall clock for the whole sweep, release build:

| Bench | Sweep | Time |
|---|---|---|
| `TxSuperhet` | 15 points | 13.3 s |
| `TxDirectConversion` | 15 points | 11.8 s |
| `RxSuperhet` | 13 points | 7.1 s |
| `RxDirectConversion` | 15 points | 4.9 s |
| `TxFetFinal` | 29 points | 3.0 s |
| `CascadeBudget` | 601 points | 0.2 s |

They render headlessly too, which is how the pictures in this file were checked:

```
circuitrf hb    "TxDirectConversion/schematic/TxDirectConversion.csch" -o results/TxDirectConversion.npy
circuitrf render TxDirectConversion.cdd --data results/TxDirectConversion.npy -o tx.png
```

`CascadeBudget` is an S-parameter bench, so it is `circuitrf sparam`, not `hb`.

## Transmitters

Both transmitters end in the same output section — bandpass filter, 20 dB directional coupler,
circulator, and an antenna deliberately mismatched to **100 Ω (VSWR 2)** so the circulator has
something to do. The reflected power lands in the circulator's dump load instead of the PA, and
the coupler's two ports read it.

`TxDirectConversion` builds an **I/Q modulator** out of two mixers and a quadrature hybrid:

| Quantity | Value |
|---|---|
| Output, both carriers | **+30.0 dBm** |
| Gain, baseband to antenna | **54.0 dB** |
| Gain, modulator output to antenna | **61.3 dB** |
| Sideband rejection | 33.0 dBc |
| Carrier (LO) feedthrough | 32.4 dBc |
| IM3 / OIP3 | 33.9 dBc / +43.9 dBm |
| Leakage into the receiver port | **+1.9 dBm per carrier** |
| T/R isolation | 25.1 dB |

**The sideband rejection is an I/Q imbalance calculation, and the VAR block holds both halves.**
At the shipped 0.3 dB and 2° it is 33.0 dBc, which is what an uncalibrated modulator achieves, and
it is set by those two numbers alone. They contribute about equally here: halving the gain error
alone gives 35.1 dBc, halving the phase error alone gives 35.1 dBc, and halving both gives
39.3 dBc — the 6 dB you would expect from cutting the whole imbalance in half.

Set `IQgain` and `IQphase` to 0 and the rejection jumps to **63.4 dBc**. That is *not* the circuit —
the cancellation in the circuit is exact, and what is left is the harmonic-balance spectrum's own
truncation floor. You can watch it be numerical: the same point reads 64.7, 74.8, 97.2 and 105.1 dBc
at `MaxMixOrder` 4, 5, 6 and 7 (63.4 at the shipped drive). Make the amplifiers ideal as well and it
falls to the solve's floor. A number that moves 10 dB every time you add a mixing index is a property of the
spectrum, not of the radio — which is the reason the shipped imbalance is non-zero.

**The carrier feedthrough is the zero-IF problem, stated as a number.** `LOIFiso` is the mixer's
LO-to-IF isolation and ships at 70 dB, which represents a double-balanced mixer *after* carrier-null
calibration. Set it to 40 — the raw part — and the feedthrough collapses to **2.2 dBc**: the LO comes
out at +24.6 dBm against the wanted +26.8, sitting in the middle of the channel where **no filter can
reach it**, because the LO *is* the channel centre.

`TxSuperhet` puts a 374 MHz IF in front of the same output section, with a 2066 MHz LO:

| Quantity | Value |
|---|---|
| Output, both carriers | **+30.0 dBm** |
| Gain, IF to antenna | **54.0 dB** |
| Sideband rejection | below what the solve can resolve |
| Carrier (LO) feedthrough | below what the solve can resolve |
| IM3 / OIP3 | 34.0 dBc / +44.0 dBm |
| Leakage into the receiver port | +1.9 dBm per carrier |

**That contrast is the whole point of the pair.** The superhet's unwanted sideband lands at
1692 MHz and its LO at 2066 MHz — both far outside the passband — so the RF filters delete them, and
what the display shows is not a rejection figure but an absence. Do not read a number off those two
traces: they sit at the spectrum's truncation floor and climb with every mixing index you add
(141 dBc at `MaxMixOrder` 4, 151 at 5, 188 at 6). The zero-IF radio's two spurs land *on top of* its
own channel and are stuck at 32-33 dBc for as long as the I/Q balance and the carrier null hold.
That is a real number, and it is the one that matters.

**The two transmitters are not identical parts in a different order**, and it is worth knowing where
they differ, because only the conversion plan is being argued about. The output section from the
first gain stage onward is the same chain with the same coupler, circulator, switch and antenna; the
gain is redistributed slightly (`A3` is 14 dB in the zero-IF bench and 16 dB in the superhet, and
`A4` is 14.8 against 14.9) and the superhet's first RF filter is 5th-order rather than 3rd, so both
land on +30.0 dBm through different conversion losses. The superhet also carries an IF filter the zero-IF radio has no place
for, and its mixer is specified by `IsoLO_IF`/`IsoRF_IF` rather than by `LOIFiso`, because out-of-band
leakage is a filter's problem there and an in-band one here. Everything else is shared.

## The T/R switch

One SPDT, carried by all five radio benches, with the same convention everywhere: **`T1` is the
transmit arm, `T2` the receive arm, and `State` says which one is live.** The transmitters set
`State = 1` and terminate `T2` in the 50 Ω the receiver presents; the receivers set `State = 2` and
terminate `T1` in the 50 Ω the PA presents. Nothing else about the part changes between them, which
is the point — it is one component in one radio, drawn from each side.

Its three numbers are VAR-block globals — `SWil` 0.5 dB, `SWiso` 28 dB, `SWrl` 26 dB — so changing
the part changes every bench at once. **The three are not independent**, and the section on passive
blocks below says why: 26 dB is the least return loss a 0.5 dB switch can have and still be a
passive part in this model.

Three things it is worth placing for:

**Deleting it and setting its loss to zero are different measurements, and the gap between them is
the point.** Deleted, the antenna sees +30.34 dBm; with the switch in place, +29.99; with the switch
in place and `SWil` set to 0, **+30.50**. So the part specifies 0.5 dB and costs 0.51 dB of
through loss — and then hands **0.16 dB back**, because a well-matched lossy two-port between a
source and a VSWR-2 antenna improves that mismatch. A pad IS a match improver; it is just a much
smaller effect than the arithmetic of "0.5 dB in, 0.5 dB out" suggests, and the only way to see the
two halves separately is to change the part rather than remove it.

**On transmit, the receiver port is a hostile place.** The switch specifies 28 dB of isolation and
the bench measures 25.1 dB of T/R isolation end to end, so at +27.0 dBm per carrier at the antenna
`Prx1` reads **+1.9 dBm per carrier** — a milliwatt and a half arriving at an LNA input that
expects −70. That is the number behind every receive-path limiter and shunt protection switch in a
TDD radio, and it is why `Isolation` is the specification that matters on this part rather than
`IL`. Raise `SWiso` and watch it fall away decibel for decibel.

**On receive, it is the first element in the chain**, so its loss goes straight into the noise
figure — see `CascadeBudget`, where it is stage 0.

## Passive intermod

The circulator carries `PIM = −67 dBm` at `PIMPc = 43 dBm` — a ferrite part quoted the usual way,
as a product level at a stated power *per carrier*. Ferrites are among the worst PIM offenders in a
transmit chain, which is why the specification lives on this block.

At the shipped drive the carriers reach the circulator at **+28.68 dBm**, 14.32 dB below the
condition the part was measured at. **The product rides the third power of drive**, so it falls
3 × 14.32 = 43.0 dB, to −110.0 dBm at the circulator's own port.

To read it you have to switch off **every other** source of third-order product in the bench, which
means six parts and not four: set the four amplifier `IP3` fields *and both mixers'* `IIP3` to 200.
The IM3 bin at the antenna then holds **−110.1 dBm**, and nothing else — set `PIM` to −300 as well and
it drops below −280 dBm, which is the solve's floor. (Leave the mixers alone and you get −56.1 dBm,
because the mixers are then the loudest thing in the bin; that number is a real measurement of a
different quantity.)

**Do the arithmetic for the run you are reading**, because idealising the amplifiers moves the
level: they no longer compress, so the carriers now reach the circulator at +29.22 dBm rather than
+28.68, which lifts the product 3 × 0.54 = 1.6 dB to −108.3 dBm there, and the antenna is a further
1.69 dB away through the circulator and the switch. That predicts −110.0 dBm at the antenna against
−110.1 measured — **the cubic law to 0.1 dB**, and real rather than a rule of thumb. Quoting the
shipped-drive figure straight against the idealised run happens to land on the same answer, because
those two 1.7 dB corrections nearly cancel; it is the right number for the wrong reason.

And at 1 W the circulator's intermod sits **103 dB below the amplifiers' own IM3** — PIM is a
high-power, multi-carrier problem, and this bench shows *why* it is not the binding spec on a 1 W
radio rather than pretending it is.

## Receivers

`RxSuperhet` is a preselector, an LNA, a second gain stage, the mixer, a 374 MHz IF filter and an
IF strip with a 6 dB pad in it:

| Quantity | Value |
|---|---|
| Gain, antenna to IF output | **52.0 dB** |
| OIP3 / IIP3 | +21.3 dBm / **−30.7 dBm** |
| IM3 at −70 dBm per carrier | 78.6 dBc |

`RxDirectConversion` splits the RF in phase and the LO in quadrature into two mixers:

| Quantity | Value |
|---|---|
| Gain, antenna to I baseband | **32.4 dB** |
| OIP3 / IIP3 | +15.0 dBm / **−17.4 dBm** |
| I/Q amplitude balance | 0.0000004 dB |
| **DC offset at the I output** | **39.7 mV** |
| Wanted signal at the same node | 4.2 mV |

**The in-phase RF splitter costs 3.01 dB, and it has to.** `SPL` is a four-port coupler set to split
equally with no phase difference — and a matched, lossless, reciprocal four-port *cannot* do that:
its two outputs must differ by 90° (or 180°). That is a theorem, not a modelling limitation, and it
is why a real in-phase splitter is a Wilkinson, which is a THREE-port with a resistor in it.
circuitRF has no Wilkinson block, so the only in-phase split a `Coupler` can honestly represent is a
resistive one, whose 3.01 dB of dissipation is exactly what buys back passivity. Set its `IL` to
0.2 dB and the Messages panel says so: σ_max = 1.38, a splitter with 2.8 dB of gain in it.

**The DC offset is simulated, not asserted.** The mixer leaks LO back to its own RF port through
`LORFiso`, and the multiplier then mixes that leakage with the LO it came from: `v_lo × v_lo` has a
DC term. It lands at mixing index `(0,0,0)` and comes out **9.5 times larger than the signal it is
sitting on** at −70 dBm in, and 24 times larger at −78. That is the reason a zero-IF receiver needs DC servo or offset cancellation before its
baseband chain can have any gain at all. Raise `LORFiso` and watch it fall away.

## Mixing System blocks with a compact model

`TxFetFinal` is the same idea as the transmitters above, except the final stage is not an `Amp`
tile — it is the **SDD FET** from the Harmonic Balance example, with its own gate and drain bias
networks, in a cell you can push into (`Ctrl+]`). Two System gain stages and a pad drive it:

| Quantity | Value |
|---|---|
| Output at the antenna, both carriers | **+30.1 dBm** |
| Gain, source to antenna | **52.1 dB** |
| RF out of the drain, both carriers | 1.77 W |
| RF into the gate | 7.8 mW |
| DC draw | 7.71 W |
| PAE at the device | **22.9 %** |
| IM3 / OIP3 | 25.0 dBc / +39.6 dBm |
| Second harmonic at the antenna | −106 dBc |

**Read the four power rows together — they are the reason the bench carries an `IProbe` on each
side of the FET.** `Ppa_W` sums *both* carriers out of the drain and `Pin_W` sums both into the
gate, so `PAE = (Ppa − Pin)/PDC` is the device's own power-added efficiency. The antenna sees
1.02 W, which is 2.38 dB less than the drain's 1.77 W: the output filter, coupler, circulator and
switch specify 2.05 dB of insertion loss between them, and the antenna is deliberately mismatched.
Dividing antenna power by DC draw instead would give 13.3 %, and that is neither PAE nor drain
efficiency — it is the whole transmitter's efficiency with the output network's losses charged to
the device. Both numbers are worth having; they are not the same number.

**Nothing special is needed to mix the two worlds.** A System block presents an impedance and a
transfer function; the FET is a nonlinear device in the same MNA matrix, behind an ordinary DC block
and choke. The only thing to watch is levels: the `Amp` tiles are ideal until their `IP3` says
otherwise, and the FET is not — 23 % PAE at 8 dB of back-off from saturation is the honest two-tone
answer, not an underperforming design.

The second harmonic reading is the output bandpass doing its job; delete `F2` and it comes back.

**The IM3 curve has a null in it, and it is real.** Sweeping the drive, IM3 falls to 21.6 dBc at
−28 dBm, climbs slowly, and then at **−23 dBm jumps to 40.7 dBc before coming straight back
down** — 16 dB above the surrounding trend in one step, with the two points either side of it at
29.0 and 33.5 dBc. That is a third-order
*sweet spot*: the device's third- and fifth-order contributions arrive in antiphase and cancel, and
a real FET does exactly this. The sweep steps 1 dB rather than 2 for this reason — at 2 dB the null
lands between samples and reads as a single wild point, which looks like a solver artifact and is
not one. Its **depth** is the one number here not to quote to three figures: a cancellation this
sharp moves 1.5 dB between `MaxMixOrder` 5 and 6, while the output power and the gain move by less
than 0.1 dB. An `Amp` tile has one nonlinearity and so cannot produce this; it is the compact
model's own behaviour, which is most of why the bench exists.

## Noise figure — read this before looking for it

**circuitRF has no noise analysis.** There is no noise source, no noise-correlation propagation and
no `NF` parameter on any block, and there is no version of this example that could have simulated
one. Adding it is a real engine feature, not a field on a tile.

So `CascadeBudget` does the next honest thing: it computes the **Friis cascade** in ordinary
measurement expressions, from a per-stage noise figure you type into a VAR block, *beside* two
quantities the engine really does compute. The point of putting them together is that you can see
which column is which:

| Quantity | Arithmetic vs engine |
|---|---|
| Cascade gain | 52.00 dB vs **51.99 dB** simulated |
| Cascade IIP3 | −30.65 dBm vs **−30.71 dBm** simulated |
| Cascade NF | 3.84 dB — **arithmetic only** |
| Noise floor in 20 MHz | −97.1 dBm — follows from the NF |
| Sensitivity at 10 dB SNR | −87.1 dBm — follows from the NF |

The gain and IIP3 rows are cross-checks: the same chain, worked out two completely different ways,
agreeing to 0.06 dB. (The simulated column is `RxSuperhet`, whose front end this budget describes.)

**The sum is written one term per stage**, not as a single Friis expression, because which stage
the noise figure came from is the thing worth reading off it. `F0` to `F8` are each stage's
contribution to the total noise factor, referred to the input; add them and you have `Ftot`:

| | F0 switch | F1 preselector | F2 LNA | F3 second stage | F4–F8 everything after |
|---|---|---|---|---|---|
| contribution to `Ftot` | 1.122 | 0.656 | 0.566 | 0.035 | 0.042 |
| share | 46 % | 27 % | 23 % | 1.4 % | 1.7 % |

**Stage 0 is the T/R switch, and it is the single largest term** — which is where a front-end loss
hurts most: it lands ahead of every gain stage, so its 0.5 dB goes into the noise figure one for
one, 3.34 dB becomes 3.84 dB, and the sensitivity moves with it. Everything from the mixer onward
is 1.7 % of the total put together, which is the Friis result stated as a design instruction: past
the LNA, only the gain in front of a stage matters. The NF row has no simulated partner and will
not have one until the engine grows a noise analysis. Treat it as a spreadsheet that happens to
live in the design.

The same cell also carries a real S-parameter run over the RF front end, 1.5–2.7 GHz:

| Frequency | Front-end S21 |
|---|---|
| 2440 MHz (wanted) | +27.50 dB |
| 1692 MHz (image) | −22.34 dB |

**49.8 dB of image rejection**, set by the elliptic preselector's 45 dB stopband floor. The image
sits at `f_RF − 2·f_IF`, which is why the IF choice and the preselector are one decision: halve the
IF and the image moves into the filter's transition, where the same part rejects far less.

**The preselector is Elliptic and not Chebyshev on purpose.** Swap `Response` to Chebyshev, leaving
everything else alone, and the front end reads −48.21 dB at the image instead of −22.34: 75.7 dB of
image rejection rather than 49.8. That is the difference between a stopband that keeps falling
forever and one with a floor. A real preselector has a floor — set by package feedthrough and board
coupling, not by its own poles — and `Astop` is the parameter that says where it is. With it, the
rejection you read is the one you specified; without it, the number is whatever the polynomial
happens to give at that frequency.

## The harmonic-balance spectrum, and how to change it

The four benches with three tones — both transmitters and both receivers — run at
**`MaxMixOrder` 4**. `TxFetFinal` has two tones and runs at 5.

That choice is the single thing that sets how long a run takes, and it is worth knowing why. With
three tones, order 4 puts 65 mixing indices in the spectrum and order 5 puts 116, and the cost of a
Newton iteration goes as the cube of that number: a measured 94 ms per iteration at order 4 against
470 ms at order 5, on the same circuit. The iteration *count* does not go the same way — the
solve converges in 7 Newton steps at order 5, 17 at order 4 and 26 at order 3, and the line search
has to backtrack 2, 35 and 75 times respectively. A smaller spectrum is a worse-conditioned problem;
truncating harder is not simply cheaper.

**To raise it,** open the bench's HB analysis and set **Max mix order** to 5. Every run gets
about 5× slower (`TxDirectConversion`: 11.8 s becomes 57.0 s). Here is what changes, at each
bench's own operating point:

| | order 4 | order 5 |
|---|---|---|
| `TxDirectConversion` output, gain, T/R isolation | +29.992 dBm, 53.989 dB, 25.099 dB | +29.988, 53.987, 25.099 |
| `TxDirectConversion` IM3 / OIP3 | 33.891 dBc / +43.934 dBm | 33.842 / +43.908 |
| `TxDirectConversion` sideband / LO feedthrough | 33.027 dBc / 32.402 dBc | 32.853 / 32.268 |
| `TxSuperhet` output, gain | +29.992 dBm, 53.983 dB | +29.987, 53.982 |
| `TxSuperhet` IM3 / OIP3 | 34.004 dBc / +43.985 dBm | 33.947 / +43.955 |
| `TxSuperhet` sideband / LO feedthrough | 140.59 dBc / 120.38 dBc | 150.79 / 129.81 |
| `RxDirectConversion` gain, OIP3, DC offset | 32.432 dB, +16.597 dBm, 39.705 mV | 32.432, +16.592, 39.705 |
| `RxSuperhet` gain, OIP3, IM3 | 51.992 dB, +21.282 dBm, 78.58 dBc | 51.992, +21.385, 78.79 |

Every physical quantity is settled by order 4 — powers, gains and isolations to better than 0.03 dB,
intercepts and IM3 to better than 0.21 dB. **The one row that moves by ten decibels is the one that
is below the solve's floor at either order**, which is the point made under `TxSuperhet` above: a
suppressed sideband that climbs with every index you add is not being measured, it is being
truncated. Order 5 does not make those numbers true; it makes them larger. (The zero-IF radio's own
sideband and LO rows move by 0.15 dB, because there they are real signals in the channel.)

The same field is `--maxmix` on the command line, so a run can be checked both ways without editing
the document: `circuitrf hb "TxSuperhet/schematic/TxSuperhet.csch" --maxmix 5`.

## Passive blocks, and the numbers they can actually have

Every System block here except the amplifiers declares itself **passive**, and circuitRF checks that
claim against the numbers in the file on every run. If a block's S-matrix can deliver more power
than it absorbs, the Messages panel names the instance:

```
system.block-not-passive: 'SW1' (SwitchModel) is declared passive and is not — σ_max(S) = 1.09993,
so it delivers up to 0.83 dB more power than it absorbs.
```

**A plausible datasheet can fail it, and that is worth understanding before you edit one of these
parts.** The blocks build their S-matrix from real, in-phase amplitudes: `IL`, `RL`, `Isolation` and
`Directivity` are each `10^(−dB/20)` with no phase. In a real part those terms arrive at arbitrary
phases and do not add; here they do. So the test is not "does each port absorb more than it emits" —
every one of these blocks passes that — but whether the whole matrix does, which is σ_max(S) ≤ 1.
Three shapes of it come up in this workspace:

- **The T/R switch** ships at 0.5 dB loss, 28 dB isolation, **26 dB return loss**, off-state
  **absorptive** (`SWil`, `SWiso`, `SWrl` in the VAR block). The return loss is not free to choose:
  a symmetric two-port needs `RL ≥ −20·log₁₀(1 − 10^(−IL/20))`, which is 25.1 dB at 0.5 dB of loss
  and 19.3 dB at 1 dB. Drop `SWrl` to 18 and the message appears; raise `SWil` to 1 dB and 20 dB of
  return loss is enough again. The off-state matters more than it looks: a **reflective** open throw
  returns `S = 1` at that port, and then any leakage into it puts the matrix over unity whatever the
  other numbers are — a 0.5 dB switch with 28 dB of isolation is over unity at *infinite* return
  loss. Absorptive costs nothing here, because every throw is terminated externally in 50 Ω: the two
  off-states give results identical to the last printed digit.
- **The circulator** ships at 0.6 dB loss, 30 dB isolation, 32 dB return loss. Its S is circulant,
  so σ_max is exactly `10^(−IL/20) + 10^(−Iso/20) + 10^(−RL/20)` — three numbers you can add in your
  head. That makes the trade explicit: a 0.4 dB circulator with 22 dB of isolation is over unity
  *before its return loss is counted*, and no return loss rescues it. You buy the suppressions with
  loss. A real ferrite does not face this trade, because its isolation and its reflection are not in
  phase with its through path.
- **The in-phase splitter** in `RxDirectConversion` pays 3.01 dB, and has to — see the receiver
  section above. A matched lossless reciprocal four-port must put 90° between its outputs, so the
  only in-phase split a `Coupler` can represent is a resistive one.

The check is cheap enough to leave on: a norm bound per block per frequency, and a singular-value
decomposition only where that bound cannot settle it.

**Where to see it mattering.** `CascadeBudget`'s antenna-port match plot reads −0.10 dB or better at
every one of its 601 points — no passive chain can do better than 0 dB, and a block that is not
passive puts that plot above it, two components away from the part responsible.

## Notes that will save you an afternoon

**A swept measurement may not use `^`.** `mag(V)^2` evaluates happily at a single point and fails
the moment a sweep makes it a cube — *"Cube operand must go through Add/Sub/Mul/Div"*. Every power
here is written `dB(V) + K` instead (`K` is 10 into 50 Ω, 6.9897 into 100 Ω), and a sum of powers as
`mag(V)*mag(V) + ...`. `dB`, `mag`, `log10`, `real`, `conj` and the rest broadcast; the power
operator does not. The measurements in these benches therefore read the same swept or not.
(`CascadeBudget` does use `^`, on the scalars of its Friis sum, where there is no cube to broadcast.)

**The Mixer converts RF port → IF port, and only that way.** Its law is
`i_if = (v_if − K·v_rf·v_lo − …)/Zif`; there is no term carrying the IF port back to RF. So an
**upconverter drives the RF pin with baseband or IF and takes the RF output off the IF pin**, which
is what both transmitters do. Wiring an upconverter "the way the block diagram reads" gives a
circuit that solves and produces nothing.

**Both sidebands always appear.** A product of two cosines is half the sum plus half the difference,
so a real mixer tile cannot select one. The superhet selects by filtering; the zero-IF radio selects
by quadrature cancellation. Those are the only two options, here and in hardware.

**In a multi-tone run, two mixing indices that land on the same frequency cannot be separated.**
An interferer exactly at the image converts to exactly the wanted IF, and the solver has no way to
tell the two apart — the number it reports for either is meaningless. That is not a modelling
shortcoming so much as a statement about images: they land *on* the wanted signal, which is why the
preselector has to remove the whole image band. Measure image rejection the way `CascadeBudget`
does, from the front end's S-parameters.

**An intercept read where the product is below the solver's floor is not a measurement, and the
FIRST point of a sweep is the one to distrust.** The zero-IF receiver's IIP3 curve sits flat at
−17.4 dBm from −66 dBm upward, reads 1.6 dB high over the −82 to −70 dBm rungs, and at the bottom
rung reads **−44 dBm** — 27 dB out. Every point of a swept HB run but the first is warm-started from
the one below it; the first is cold-started, and its third-order product, at −137 dBm, is the one
that lands on the solve's own floor. Moving the sweep's start does not help — it only moves which
rung is cold. Read an intercept off the FLAT part of its curve, which is the same rule that applies
on a bench and the reason the curve is worth plotting instead of quoting one point.

**A suppressed spur is the same trap, one level up.** A sideband or an LO that the filters have
removed does not read as zero; it reads as whatever the truncated mixing spectrum leaves behind, and
that number changes when you change `MaxMixOrder`. If a rejection figure moves with the spectrum
setting, it is telling you about the spectrum. The section above shows how to make it do that.

**Keep multi-tone runs to three tones where you can.** Four tones on a common frequency grid made
two identical carriers here come out 6.2 dB apart; moving two of them off the grid brought them back
to within 0.19 dB. The benches use three tones each and every carrier pair agrees to better than
0.03 dB.

**A measurement row ending in a variable name loses it.** A row is `name = expression [unit]`, and
the last token of `Gain = Pout − Pin` is read as the unit, leaving an expression that will not
parse. Two habits avoid it, and both are used here: put the expression in parentheses, or lift the
global into a measurement of its own first (`Prf_dBm = Prf`, then subtract *that*). The same rule is
why several rows here end in a `;` comment.

**An angle typed into a hand-written `.cnl` without a unit is radians.** `Phase=90` is 90 radians —
116.62° — and an I/Q modulator built that way quietly achieves about 13 dB of sideband rejection
instead of cancelling. The schematic editor always writes the unit, so this only bites netlists you type
yourself. Write `Phase=90 deg`.

**A stopband filter is reflective.** Looking at the node between the mixer and the RF filter, the
rejected sideband reads about 6 dB *higher* than the wanted one — the filter's stopband input is
nearly an open, so the node voltage roughly doubles. Nothing is wrong; measure what the load
receives, not what a reflective node swings to.

**Three separately plausible numbers on one passive block can still sum above unity**, because the
family builds its S-matrix from real, IN-PHASE amplitudes converted straight from dB. The section
on passive blocks above has the arithmetic and the three shapes it takes here.
