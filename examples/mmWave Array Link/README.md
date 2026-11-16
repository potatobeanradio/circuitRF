# mmWave Array Link

A 28 GHz base station with a 16 × 16 array sends NR 64-QAM to an 8 × 8 array 400 m down a street, around a corner. The
path is 3GPP TR 38.901's street-canyon micro cell without line of sight, and its multipath is the report's clustered delay
line CDL-C: 24 clusters of 20 rays, each with its own direction at both ends. Each **drop** of the channel finds the best
pair of beams through that drop's rays, and the envelope run reads EVM and bit errors through them.

| Cell | What it is |
|---|---|
| `Patch Element` | One inset-fed patch, 3.57 × 2.80 mm on a 0.161 mm laminate (εr 3.66): the *Phased Array* example's 5.9 GHz patch scaled by 5.9/28, so it resonates at 28 GHz (S11 −14 dB). Its EM result ships in `results/` |
| `TxChannel` | Each transmit element's path: a 12 dB driver and a 20 dB amplifier |
| `RxChannel` | Each receive element's path: a 20 dB, 1.5 dB NF LNA and a 3 dB attenuator |
| `Array Link` | The bench |

In the bench, `GNB` plays NR 20 MHz at 60 kHz subcarrier spacing into `PRE`, a pre-driver whose compression sets the
transmitter's EVM, and the transmit array `PT` (Feed mode, half a wavelength, each element through `TxChannel`). `CH1`
carries it, `Tx=PT Rx=PR`, to the receive array `PR`, whose feed is the probe `Irx`. Both arrays' phases are
`steer(0,0,f0)`, so the channel picks their beams (`BeamSelect` unstated is `auto`) from a grid of candidates one beamwidth
apart, through each array's own `steer()`. `ENV1` is four OFDM symbols with `Noise=yes` and `Equalizer=pilot`; `DROPS`
sweeps it over drops 1 to 20.

## The path

| Quantity | Value |
|---|---|
| Path loss | 145.1 dB: 113.4 free space, 31.6 the UMi non-line-of-sight excess |
| Shadowing σ, drawn per drop (`link:CH1:Shadowing_dB`) | 7.8 dB |
| Delay spread, coherence bandwidth | 100 ns, 2 MHz: the 20 MHz signal fades frequency by frequency, and the pilot equalizer undoes it |

The ends are far apart: the 16 × 16 array's far field begins 2.7 m away, so the channel's plane-wave rays hold.

## The beams

Every drop scores 5,365 beam pairs through its rays (`link:CH1:Beam_dB` lists them, strongest first) and takes the
strongest. Over the 20 drops the transmit beam is one of two (7.2° and 14.5° off boresight) and the receive beam one of
six: the clusters are the same in every drop, their phases are not, and which pair adds up best changes with them. The
power through the chosen pair, `Beam_dB`, runs from −95.6 to −123.8 dB, the shadowing's spread and the clusters'
interference together. `BoundLoss_dB` is what the codebook costs against the best any phase-only weights could do through
the same rays: 2.3 to 9.0 dB, because a grid of single beams points at one cluster while the bound collects several.

**Open the bench in the Phased Array tool** (*Tools ▸ Phased Array*, with `PT` or `PR` selected) and its **Channel** tab
draws the drop's rays on the (u, v) map with the candidate beams, and lists the ranked pairs. Click another pair to use it
(it writes `BeamSelect=index`), or the check button to write it into the arrays' `steer()`; move the drop slider to see
another realization.

## EVM and bit errors over the drops

| Measure | Value |
|---|---|
| MER over the drops | 6.3 to 33.8 dB, median 25.7 dB |
| EVM at the strongest drops | 2.0 %, the pre-driver's compression |
| Drops with bit errors | 8 of 20 |
| `Outage`, the drops whose MER is below 20 dB (`MerReq`) | 20 %, its exact 95 % interval 5.7 to 43.7 % |

`BERest` estimates each drop's bit error rate from its symbols' SINR, below what four symbols can count. With no velocity
stated the channel is frozen within each drop (`link:CH1:fD_Hz` is 0); give `RxVel` a speed and each ray's Doppler follows
from its direction.

## What was traded for run time

**The amplifiers in each element's path are linear** (`IP3` 200 dBm). An envelope run evaluates every nonlinear device at
every step, and with 256 nonlinear amplifiers one drop of two symbols ran for more than ten minutes; the compression is
carried by the one pre-driver instead. The *Phased Array* example's **6 Envelope** bench shows what each element's own compression
does to a beam. **Four OFDM symbols** (72 µs) is enough for EVM and the pilot equalizer, and 20 drops take about a minute in
a Release build; `Noise=yes` keeps each drop from replaying the transmitter's waves, so every drop solves both arrays. **The
element is isolated**: its record is one patch, so the arrays carry its pattern and its match but no coupling between
elements.

The element record is reduced to 28 GHz and a 5° grid (110 KB). Simulating `Patch Element/em/Patch Element.cem` writes the
full one again in a few seconds; the bench reads either.
