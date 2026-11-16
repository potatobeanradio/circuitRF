# Harmonic Balance

A 48 V GaN power amplifier, in two benches, over one reusable device cell.

## FET

The device: an **SDD** whose drain current is written as an equation in the same expression
language everything else uses, wrapped in a cell with two pins and one parameter, `Periphery_mm`.
Both benches place it and override that parameter. Push into it (`Ctrl+]`) to see the equations.

## PowerAmplifier

Single tone at 2 GHz, drive swept 0 → 30 dBm. Gate biased at −3.05 V through a choke, drain fed
from 48 V through another, 50 Ω load through a DC block.

**`RL` is a resistor.** A Term would load the output the same way: outside S-parameters a Term is a
load of its `Z`, its resistance at DC and `Z` at every harmonic. In HB, drive with a `P1Tone`/`PnTone`
source and terminate with an `R` or a Term.

Four measurement blocks, which between them cover most of what a measurement can do:

| Block | Shows |
|---|---|
| `MeasInput` | reading a swept variable (`Pin`) as a curve; a measure built on an earlier measure |
| `MeasOutput` | `V` and `I` at the **fundamental** (harmonic index 1) |
| `MeasEfficiency` | harmonic index **0** is DC; drain efficiency and PAE |
| `MeasHarmonics` | `dB()` and `mag()` over a cube; a **complex** measure that plots on a Smith chart |

`abs()` does **not** broadcast over a cube — use `mag()`. `dB()`, `dB10()`, `dBm()`, `conj()`,
`real()`, `imag()`, `phase()`, `log10()` and `ln()` all do.

Expected result: about 39.5 dBm output, 43 % drain efficiency, gain compressing from 16.6 dB to
9.5 dB. Two outer sweeps (`RFfreq`, `VDD`) are declared and disabled — enable one to get a surface.

## TwoTone

The same amplifier with a **PnTone** source at 1990 and 2010 MHz. Under two tones the spectral axis
is `mixIndex` and the second accessor argument is a mixing-product tag rather than a harmonic
number: `"(1,0)"` is the first carrier, `"(0,1)"` is the second, `"(1,-1)"` is IM2, `"(2,-1)"` is
the lower IM3, `"(0,0)"` is DC.

Only one of each conjugate pair is stored: **2f₂−f₁ is addressed as `"(1,-2)"`**, not `"(-1,2)"`.
Asking for a tag that is not there is an error that lists every tag that is.

**Each carrier is measured separately, and the efficiency adds them.** `Pout_f1_W` and `Pout_f2_W`
are read at `"(1,0)"` and `"(0,1)"`, and `Eff` is `(Pout_f1_W + Pout_f2_W)/PDC_W*100`. Doubling one
carrier instead would assume the two come out equal, and they do not — here the upper carrier runs
0.03 dB low in back-off, the gap grows to 0.06 dB through compression and then **changes sign** near
the top of the sweep, where the upper carrier comes out ahead. Doubling `Pout_f1_W` reads 0.25 pp
high at 16 dBm drive and 0.22 pp low at 20 dBm — wrong in both directions, and worst exactly where
the efficiency number is the one being quoted.
