# Indoor IoT Link

A battery sensor sends 100 kb/s FSK at 868.95 MHz to a gateway two floors up. Will it get through? A
**Propagation Channel** answers as a percentage: each run is one **drop**, one realization of the shadowing and the
fading, and a sweep over `drop` is the Monte Carlo. Both benches share the same link:

| Part | What it is |
|---|---|
| `SENSOR` | A MOD_Source playing 2-FSK, ±45 kHz deviation, PN9 (511 bits, one 5.11 ms record), at 10 dBm |
| `CH1` | ITU-R P.1238 office without line of sight (`PathLoss=P1238 Environment=office Los=nlos`), 28 m, `Floors=2`, and a concrete wall and two partitions on the path (`Partitions="10,6,6"`, dB each) |
| | Multipath from the office's own power-delay profile, `profiles/office_pdp.csv`: six taps over 110 ns (`Multipath=file`), each Rayleigh-fading at walking pace (`Fading=rayleigh Velocity=1`, 1 m/s) |
| `LNA`, `GW` | The gateway: a 2 dBi antenna (`RxGain=2`), a 20 dB, 6 dB NF front end and its 50 Ω input |

The sensitivity is −98 dBm, `Sens`: on the second bench the FSK detector's bit errors pass 1 % below about −100 dBm, and
−98 keeps 2 dB in hand.

## 1 Coverage

An S-parameter run swept over 2,000 drops. Each drop's received power is the sensor's 10 dBm plus the channel's gain
through that drop, `SP1.link("CH1", "Gain_dB")`, with the path loss, both antennas, the shadowing and the fading in it.

| Measure | Value |
|---|---|
| Path loss, P.1238 + two floors + partitions | 104.3 dB (60.2 free space, 3.1 the office's excess, 19 two floors, 22 the walls) |
| Median received power `PrxMedian_dBm` | −94.2 dBm |
| `Outage`, the drops below −98 dBm | 29.1 %, its exact 95 % interval 27.1 to 31.1 % |

The median is only 3.8 dB above the sensitivity, and the office's shadowing (σ = 5 dB) and the Rayleigh fading together
need about 20 dB for 99 %: two floors and three walls are too much for this sensor. `Prx_cdf` draws the distribution.
Move the gateway one floor down (`Floors=1`, 9 dB less) or raise the sensor's power, and run it again. The whole sweep takes
two seconds.

## 2 Bit Errors

The same link in an envelope run with `Noise=yes`, over drops 1 to 50: the FSK record is demodulated at `Irx` and every bit
decided. `BER` is each drop's counted bit error rate, `Prx_dBm` its received power (the same drops as the first bench's),
and `BERmean` their average.

| Measure | Value |
|---|---|
| Drops with any bit error | 14 of 50 |
| Drops above 1 % | 12 of 50, every one below −100 dBm |
| `BERmean` | 0.026 |
| `Outage` of the received power below −98 dBm | 30 % (17.9 to 44.6 %) |

Plot `BER` against `Prx_dBm` and the sensitivity is where the errors start. The run's notes say what regime the link is in:
f_D = 2.9 Hz, so the 5.11 ms record sees a frozen channel (f_D·T = 0.015) and every drop's statistics come from other drops;
the 18 ns delay spread gives an 11 MHz coherence bandwidth, against a signal 290 kHz wide, so the office fades it flat.

## What was traded for run time

The envelope bench reads 50 drops (3 s); 2,000 would take two minutes and would only resolve the bit error rate the
coverage bench already gives as an outage. `Noise=yes` keeps the run from replaying the sensor's waves between drops, so each
drop solves both ends.
