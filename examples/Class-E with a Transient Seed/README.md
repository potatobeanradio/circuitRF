# Class-E with a Transient Seed

A 1 GHz class-E power amplifier, solved by harmonic balance started from a transient run (`InitialGuess=transient`),
beside a transient bench that shows the waveforms that start came from. Both benches carry the same stage.

## The stage

A FET (the built-in Angelov law, `Ipk` = 0.5 A) driven hard as a switch: a 30 dBm, 1 GHz `P1Tone` into its gate
through `CIN`, the gate biased at −2.5 V through the choke `LG`, with the gate diode `DG` clamping the drive. The drain
is fed from 5 V through the choke `LD`, past the IProbe `IDD`. `C1` (2.92 pF) is the class-E shunt capacitance;
`L0`–`C0` (7.96 nH, 4.13 pF) are the series tank with the class-E excess reactance, into the 10 Ω load `RL` through
the IProbe `IOUT`. `DB` is the drain's body diode. These are the class-E values for 10 Ω at 1 GHz. The ideal stage
delivers 0.577 · 5² / 10 = 1.44 W (31.6 dBm) at 100 % efficiency.

`RL` is a Term of 10 Ω. Outside S-parameters a Term is a load of its `Z`.

## ClassE_HB

Harmonic balance at 1 GHz with `MaxHarm=31` and `InitialGuess=transient`. Before Newton starts, circuitRF simulates
the stage in time from its DC point at full drive until one period looks like the next, and hands the last period's
spectrum to Newton. The run's message says how that went:

```
harmonic balance started from a transient seed: 43 periods, settled; no choke or block scaled;
harmonic balance converged in 5 Newton iterations from it.
```

| Measure | Result |
|---|---|
| `Pout_dBm` | 31.42 dBm |
| `Pdc_W` | 1.579 W |
| `DE_pct` | 87.9 % drain efficiency |

The run also writes `Seed.Periods` (43), `Seed.Settled` (1) and `Seed.HbIterations` (5). The answer is harmonic
balance's own, to its own tolerance: the transient run only supplies where Newton starts.

**Against the default start.** Set `InitialGuess` back to `dc` and the same answer is reached from the DC point. With
`DriveStepping=Never` it takes 55 Newton iterations, and a `MaxIter` of 54 or less does not converge at all. The
default `DriveStepping=IfNecessary` gets there too, by stepping the drive up. A switching stage is one of the circuits
harmonic balance finds hardest to start, because its devices only conduct as a switch at full drive. The seed is
worth reaching for when a DC start fails or stalls, and `InitialGuess=fallback` tries the DC start first and seeds
only if it fails.

**Fast bias.** `SeedFastBias=yes` (the default) scales, in the transient run only, any RF choke whose reactance is more
than 50 times the impedance it sees, and any DC block whose reactance is less than a fiftieth of it, because those would
keep the transient run going for thousands of periods. Here none qualifies, as the message says, and the run settles in
43 periods unaided. Put a 10 µH choke in place of `LD` and it would be scaled, and named in the message.

## ClassE_Transient

The same stage simulated for 60 ns, 60 periods, from its DC point. Plot `V(d)` (the drain) and `I(IOUT)` together:
the drain voltage swings up to about 19 V while the switch is open and falls back to zero before it closes, which is
the class-E condition. The peak is near the ideal 3.56 × 5 V = 17.8 V. The first 40 ns or so are the start-up the seed
watched settle.

| Measure | Result | Read from |
|---|---|---|
| `Pout_dBm` | 31.42 dBm | `fft_power` of `IOUT` at the fundamental over the last period |
| `Pdc_W` | 1.577 W | `V` and `I` of `IDD` averaged over the last period |
| `DE_pct` | 87.9 % | |
| `Vd_peak` | 18.9 V | The drain's peak in the last period |

The two benches agree on the output power to 0.002 dB.

## Settings traded for run time

**`MaxHarm`.** The drain current switches in a fraction of a period, and harmonic balance needs harmonics to follow
it. At `MaxHarm=31`, efficiency agrees with a transient run at a 1 ps step to 0.06 pp. At 15, the run is no faster
here and the efficiency reads 0.8 pp low:

| `MaxHarm` | `DE_pct` | `Pdc_W` | `Pout_dBm` |
|---|---|---|---|
| 15 | 87.13 % | 1.592 W | 31.422 dBm |
| 31 (shipped) | 87.90 % | 1.579 W | 31.422 dBm |
| 63 | 87.97 % | 1.578 W | 31.423 dBm |

**The transient bench's step.** `Step=5p`, 200 points a period. At 1 ps the efficiency reads 87.96 % instead of 87.94 %,
for five times the steps.
