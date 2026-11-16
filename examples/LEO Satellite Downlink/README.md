# LEO Satellite Downlink

A low-orbit satellite at 600 km sends a 16APSK single carrier at 20 GHz to a vehicle in a suburb, seen at 30° elevation
as it sets. One schematic, `Downlink`, holds the transponder, the path and the terminal, and one envelope run over 40
**drops** reads the signal at the far end, drop by drop.

| Part | What it is |
|---|---|
| `UPLINK` | The signal as it reaches the transponder: 16APSK at 10 Msym/s, root-raised-cosine roll-off 0.2, a 64-symbol preamble and 36 pilot symbols every 1,440 (`Carrier[1]="SC;…"`), at −3 dBm |
| `IMUX`, `TWTA`, `OMUX` | The transponder: a 20 MHz input filter, a travelling-wave tube amplifier as a 40 dB gain block whose output intercept is 48 dBm, and a 30 MHz output filter. It sends 36 dBm, and its compression is the 2.6 % EVM at `Itx` |
| `CH1` | `PathLoss=satellite Altitude=600 Elevation=30 Pass=setting`: the slant range, its latency and the pass's Doppler. ITU-R P.618 rain, clouds and scintillation, and P.676 gas, at `Availability=99.5`. ITU-R P.681's land mobile-satellite states for a suburb (`States=p681 StateEnvironment=suburban`) at `Velocity=10` m/s. The satellite antenna is 36 dBi, the terminal's flat panel 30 dBi |
| `LNA`, `TERM` | The terminal: 30 dB, 1.5 dB NF |

`ENV1` is 0.2 ms of the carrier with `Noise=yes` and `Equalizer=pilot`: the preamble and pilot blocks train the receiver's
equalizer, the frequency offset is measured and removed, and every symbol is decided. `PASS` sweeps it over drops 1 to 40.

## The path

| Quantity | Value |
|---|---|
| Slant range, latency (`link:CH1:Delay0_s`) | 1,075 km, 3.59 ms |
| Free space | 179.10 dB |
| P.676 gas on the slant path | 0.50 dB |
| P.618 rain, clouds and scintillation exceeded 0.5 % of the time | 3.27 dB |
| Doppler `Doppler_Hz`, and its drift `Drift_Hzps` | −399.28 kHz, −991 Hz/s |

The carrier arrives 399 kHz low. The receiver acquires it from the power spectrum, fits it from the symbols' phases, and
`CFO_Hz` reads it within 160 Hz in every drop with 10 dB of MER or more. The drift moves the frequency by a fifth of a
hertz over the record, so it is read from the channel (`Drift_Hzps`), not from the signal.

## Drops: the P.681 states

Each drop is in one state, because 0.2 ms at 10 m/s is 2 mm of road: GOOD (line of sight, light shadowing) or BAD (heavy
shadowing and blockage). `State` is 1 for GOOD.

| Measure | Value |
|---|---|
| `GoodFraction`, the drops in the GOOD state | 55 % |
| GOOD drops: MER, EVM | 19.5 to 22.3 dB, 7.7 to 10.6 %, no bit errors |
| BAD drops | from 21.7 dB, as clear as a GOOD drop, down to fully blocked (MER below 0 dB, BER 0.34 to 0.5) |
| `Outage`, the drops whose MER is below 15 dB (`MerReq`) | 25 %, its exact 95 % interval 12.7 to 41.2 % |

`BER` counts each drop's 6,132 bits; `BERest` estimates the same from the symbols' SINR and reaches below what one record can
count: 10⁻⁷ to 10⁻⁴ in the GOOD drops. Here 16APSK reaches a bit error rate near 10⁻³ at about 16 dB of MER and passes 10⁻² below
15 dB, so the outage is the fraction of the time the vehicle is shadowed too deeply to decode before coding. In the GOOD state the margin is set by the
noise, about 21 dB, and not by the transponder, whose 2.6 % EVM alone would read 32 dB.

## What was traded for run time

The TWTA is the System amplifier: its soft limiter compresses (AM/AM) but does not turn the phase with drive (no AM/PM),
which a tube does; its 2.6 % EVM is the limiter's alone.


40 drops take about 20 s in a Release build. The outage's interval is wide at 40: 400 drops take three minutes and narrow
it to a few points. `Noise=yes` keeps the run from replaying the transponder's waves between drops, so each drop solves it
again.
