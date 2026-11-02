# Brief ENV-TLIN — an envelope run with a transmission line in it diverges

**Tag:** `R-envtlin-n` · **Depends on:** the envelope analysis as merged, with ENV-SEG (fc15a183)
**Area:** `src/Engine/Envelope/` (`EnvelopeEngine.cs`, `EnvelopeBaseband.cs`, `EnvelopeTangentCheck.cs`,
`EnvelopeNewton.cs`), `src/Core/Devices/TLineModel.cs`, `tests/Engine.Tests/Envelope/`. Findings go in
`src/Engine/RESOLVED.md`. HB, S-parameter, DC and loadpull runs must be **byte-identical** after this brief, and so must
every envelope run with no line in it. If any of them changes, stop and say why.

---

## 0. The gap

An envelope run of a small power amplifier fails after a few tens of steps as soon as one ideal transmission line
(TLIN) sits in series at its output. Without the line the same run finishes in 0.12 s. The line's length, loss, form
and the drive level make no difference:

| TL3 between the output DC block and the 50 Ω load | Pin | result |
|---|---|---|
| none (the block goes straight to the load) | 14 dBm | runs: 20 µs in 1.2 s, 1 µs in 0.12 s |
| `Z=50 Ohm L=300 mm` | 14 dBm | step 23 (46.8 ns) did not converge, even split into 16 sub-steps |
| `Z=35 Ohm L=300 mm` | 14 dBm | step 22 |
| `Z=50 Ohm L=100 mm` | 14 dBm | step 24 |
| `Z=50 Ohm L=30 mm` | 14 dBm | step 23 |
| `Z=50 Ohm L=30 mm Eeff=1 Ac=20 Ad=20 F=2 GHz` (lossy) | 14 dBm | step 22 |
| `Z=50 Ohm E=90 F=2.14 GHz` (electrical form) | 14 dBm | step 22 |
| `Z=50 Ohm L=3 mm` (nearly a short) | 14 dBm | step 23 |
| `Z=50 Ohm L=3 mm` | **−10 dBm** | step 26 |

What this rules out, and what it points at:

- **Not the straight-line (tangent) approximation running out of accuracy.** A 3 mm line is almost transparent at
  every harmonic of 2.14 GHz, and it fails at the same step as a 300 mm one.
- **Not the device's nonlinearity.** At −10 dBm the FET is close to linear, and the run still fails, three steps later.
- **Something grows step by step.** The DC solve and the harmonic-balance start succeed; the stepping diverges after
  ~45–55 ns. A growing mode in the stepped system (an element whose stepping model is not passive, or a wrong-signed
  slope) fits that; an ill-posed first solve does not.
- **A lead:** every failing run prints the line model's DC clamp, sometimes at **1 Hz**:
  `TLIN:TL3: sin(θ)≈0 at f=1 Hz (θ=3.6E-09°, a quarter-wave open/short resonance); clamping |sinh γl| to a floor and
  proceeding.` 1 Hz and 2 Hz are where the baseband product recovers each element's slope (`EnvelopeBaseband.cs`
  header). A line is meant to be HELD at its DC stamp for the baseband product instead (`BasebandHeldModel`), so a
  1 Hz evaluation of the line still reaching the run, or its clamped value being used as a slope somewhere, is the
  first thing to check.
- **Why the existing tests did not catch it:** every envelope test with a line (`EnvelopeBasebandTests`,
  `EnvelopeSnpTests`) is linear — a source, the line, a resistor — at `MaxHarm=1`, `Accuracy=draft`. None has a
  nonlinear device, more than one harmonic, or a line in series behind a DC block, which leaves the node between them
  defined at DC only through the line.

## 1. Reproduction

Self-contained; runs in under half a second with `circuitrf env repro.cnl`:

```
define TestPA_FET (gate drain)
  parameters Periphery_mm=1
  Sv = -0.837
  Sc = 0.71
  TV0 = 4.268
  TC = 1.507
  th = 0.001
  a = 0.176
  g = 0.089
  lam = 0.0012
  B = 1130
  C0 = 1.5e-12
  kc = 0.5
  sc = 1.0
  Vp = -2.0
  Cgd = 1.5e-13
  Cds = 8e-13
  SDD:X1  gate  0  drain  0  I[1,0]=_v1/50  I[2,0]=Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1)))+1))*ln(exp(-(2*TV0 - 2*_v1 +2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1) * (_v2*lam + 1))/2  I[1,1]=C0*(_v1 + (kc/sc)*ln(cosh(sc*(_v1 - Vp)))) + Cgd*(_v1 - _v2)  I[2,1]=Cds*_v2 + Cgd*(_v2 - _v1)
end TestPA_FET
Pin = -10
VDD = 48
MOD_Source:SRC1 n1 0 Fc=2140 MHz Pavg=Pin dBm Z=50 Ohm Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=0;P=0;Seed=1" Record=0.0005 Shaping=-80
C:Cin Vin n1 C=100 pF
L:L1 n2 Vin L=1 uH
Vdc:V1 n2 0 Vdc=-3.05 V
TestPA_FET:X1 Vin Vd Periphery_mm=1
L:Ld n4 Vd L=20 nH
C:Cbp n4 n8 C=10 uF
R:Resr n8 0 R=0.05 Ohm
L:Lfeed n9 n4 L=100 nH
R:Rfeed VDD n9 R=0.5 Ohm
Vdc:V2 VDD 0 Vdc=VDD V
C:Cout Vd n10 C=100 pF
TLIN:TL3 n10 Vout Z=50 Ohm L=3 mm
R:RL Vout 0 R=50 Ohm
analysis ENV1 type=env Stop=1e-6 Accuracy=standard MaxHarm=5 MaxMixOrder=7 Probes="Vin,Vd,Vout"
```

Output today: `Error: envelope step 26 (t = 5.29E-08 s) did not converge, even split into 16 sub-steps`.
Replace the `TLIN` line with `R:Rz n10 Vout R=0.001 Ohm` and it runs.

## 2. What to do

### 2a. Find the cause before changing anything

Measure, in this order, and record each answer in RESOLVED:

1. **Does harmonic balance converge on the same circuit** (one tone at 2.14 GHz, `MaxHarm=5`, the same line)? If not,
   the fault is upstream of the envelope.
2. **Bisect the envelope settings on the reproduction:** `MaxHarm=1`; `Accuracy=draft` / `fine`; the line moved to the
   input side; the DC block `Cout` removed (line straight from `Vd`); a 10 kΩ resistor from `n10` to ground. Which
   one makes it run says where to look.
3. **Where the 1 Hz evaluation of the line comes from** (a breakpoint or a stack trace in `TLineModel.Stamp` at
   f < 10 Hz during an envelope run), and whether its value reaches the baseband C recovery, the tangent check, or
   the stepping.
4. **The growing mode:** the per-step Newton residual and the norm of the solution over the first 30 steps. If it grows
   geometrically, find the element whose stepping model (`G + (jkω₀ + α)C` with the line's tangent) is not passive at
   some harmonic k: the real part of its admittance going negative across the band is enough to make Gear-2 grow.

Write down the cause in one paragraph before fixing it. If it turns out to be something none of the above names, say
so and stop for the owner rather than patching symptoms.

### 2b. Fix it at the cause

Whatever the fix, it must hold for every row of the table in §0, and:

- **A short line is transparent.** The reproduction with `L=3 mm` gives Pout_avg, Gt and ACPR1/2 at `Vout` within
  0.05 dB of the same circuit with the line replaced by `R:Rz n10 Vout R=0.001 Ohm` (record both runs at 20 µs).
- **A long line is still named.** At `L=300 mm`, `Pin = 14`, the run completes and posts the existing
  `env-tangent` warning naming `'TL3'` if its error across the band is above the warning threshold; at `L=3 mm` no
  line warning is posted. Report both warning texts verbatim in RESOLVED.
- **No new silent fallback.** If some line genuinely cannot be stepped (state why), refuse with a message naming it,
  never continue on a clamped value.

## 3. Tests

In `tests/Engine.Tests/Envelope/` (one test per claim, minimal `InlineData`):

1. The reproduction (1 µs) runs to the end, for `L=3 mm` and `L=300 mm`.
2. `L=3 mm` matches the line-free circuit within 0.05 dB on Pout_avg and ACPR1 (2 µs is enough if it holds; say which
   length was used).
3. `L=300 mm` at 14 dBm posts the `env-tangent` warning naming `'TL3'`; `L=3 mm` posts none.
4. The existing `EnvelopeBasebandTests` and `EnvelopeSnpTests` pass unchanged.

Run only the envelope test classes (`dotnet test tests/Engine.Tests --filter "FullyQualifiedName~Envelope"`); do not run
the full suite.

## 4. Text

If the fix changes what a line does in an envelope run, update the **Frequency-dependent blocks** paragraph of
`docs/user/src/reference/simulations.md` to match, and regenerate that page only. No change history in the page.

## 5. Out of scope

- A frequency-dependent model of lines across the band (a rational fit, as SnP blocks get). Only the divergence is in
  scope; the straight-line approximation and its warning stay.
- The microstrip, stripline and coupled-line models, unless §2a shows they share the cause. If they do, add one row
  for each to test 1 and say so.
