# Brief SYS-5 — the ideal power amplifier

**Read first:** `brief-sys-series.md`, `src/Core/Devices/MixerModel.cs` (the whole file — this
component is its two-port sibling and shares its limiter, its intercept arithmetic and its
snap-to-ideal conventions), and `brief-sys-2-ideal-s-block.md` for the port and net conventions.

**Owner's specification:** gain, IIP3 or OIP3, return loss, **no DC power consumption**. So: no
bias pins, no supply parameter, no efficiency, no self-heating. It is a two-port that makes a
signal bigger and distorts it.

## The device

Two ports, four nets in ± pair order `[in+, in−, out+, out−]`, `ModelKind.Nonlinear`, memoryless:

```
   i_in  = v_in / Zin                                       (the input is a resistance)
   i_out = ( v_out − G · psi(v_in) ) / Zout                  (the output is a source behind Zout)
   psi(x) = Vsat · tanh(x / Vsat)                            (psi = identity when IP3 is off)
```

`G` is a VOLTAGE gain the model derives; the user types a power gain in dB. With both ports
matched, an input of peak `A` puts `G·A/2` across a matched load, so
`Gp = (G·A/2)²/(2·Zout) ÷ A²/(2·Zin) = G²·Zin/(4·Zout)`, hence

```
   G = 2 · sqrt( 10^(Gain/10) · Zout / Zin )
```

The gate must do that arithmetic itself, from the dB the user typed, and never read `G` back out of
the model — the same rule `MixerModelTests` follows for the mixer's multiplier constant.

**Unilateral by default.** `S12 = 0`: the reverse path is absent, not small, so an ideal amplifier
is unconditionally stable and cannot oscillate around a mismatch. `S12` is a parameter (in dB,
default 200 = none) for users who want a reverse path, and the doc comment should say plainly that
turning it on is what makes stability a question at all.

## Parameters

| Parameter | Default | Shown on schematic | Meaning |
|---|---|---|---|
| `Gain` | `20` dB | yes | Small-signal power gain, input port to output port. |
| `IP3` | `40` dBm | yes | Third-order intercept. Its reference is `IP3Ref`. `200` means the amplifier is exactly linear and never compresses. |
| `IP3Ref` | `Output` | no | `Input` or `Output`. **D5.** |
| `Zin` | `50` Ω | no | Input port resistance. |
| `Zout` | `50` Ω | no | Output port resistance, and the source resistance the output sits behind. |
| `RLin` | `200` dB | no | Input return loss. 200 means exactly matched. |
| `RLout` | `200` dB | no | Output return loss. |
| `S12` | `200` dB | no | Reverse isolation. 200 means unilateral. |

**D5 — one field plus a selector, not two fields.** `OIP3 = IIP3 + Gain` is an identity, so two
independent fields can be made to contradict each other and one of them then silently wins. One
`IP3` with an `IP3Ref` of `Input` or `Output` says exactly what the user read off the datasheet and
cannot disagree with itself. Default `Output`, because a power amplifier's datasheet quotes OIP3.
The parameter description must state the conversion so a user never has to guess which one is
displayed.

The limiter is on the INPUT-referred signal, so the model converts first:
`IIP3 = IP3 − Gain` when `IP3Ref = Output`, then `Vsat = 0.5·sqrt(2·1e-3·10^(IIP3/10)·Zin)` —
`MixerModel`'s own derivation, `IIP3 = 2·Vsat` in volts, which fixes the third-order intercept
exactly because `tanh`'s expansion is `x − x³/3 + …`.

Two consequences worth documenting rather than leaving a user to discover:

- **P1dB falls out at IIP3 − 9.6 dB** (the `tanh` limiter's own value, and within a few tenths of a
  dB of the textbook cubic's). It is not a separate parameter, and it is not adjustable
  independently of the intercept — an ideal amplifier has one nonlinearity, and this is it.
- **There is gain at DC.** A memoryless block with a flat gain has it at every frequency, ω = 0
  included. That is what makes the HB DC harmonic well behaved. A user who wants a DC block should
  place a series capacitor, and the documentation should say so.

## Milestones

1. `AmplifierModel` — `Evaluate`, `EvaluateInto` (the closed-form allocation-free path, as the
   built-ins do), the derived constants, the snap-to-ideal thresholds.
2. Factory registration, registry defaults, parameter descriptions, elaborator 4-net check.
3. The compression and intercept gates below.

## Must NOT

- Add a DC supply pin, a `Vdd`, an efficiency, a PAE or a thermal node. The owner's specification
  says no DC power consumption, and adding one would put this component in competition with the
  FET models, which is the wrong tool for a system diagram.
- Add `Psat` or a separate `P1dB`. Both are consequences of `IP3` here; two knobs that set one
  curve is how a model becomes unfalsifiable.
- Add noise figure. Not requested, and the analyses in this repository do not carry a noise result
  to put it in.
- Reuse `MixerModel`'s code by copying it. If the limiter and the intercept derivation are worth
  sharing — and they are, once SYS-4 also uses them — factor them into one small internal helper
  the mixer also calls, and gate that the mixer's results are bit-identical afterwards.

## Gates

- **Gain, independently computed.** Small-signal `S21` equals the `Gain` typed, at three gains and
  three `Zin`/`Zout` combinations, to 1e-9 — with the voltage-gain algebra done in the test.
- **Unilateral and matched:** `S12 = 0` exactly (no entry stamped), `S11 = S22 = 0` at the default
  return losses, and each becomes exactly `10^(−RL/20)` when set.
- **The intercept is what was typed.** A two-tone HB run at a level well below compression puts IM3
  products where `IP3` says, to within 0.1 dB, for `IP3Ref` both ways — and the two settings agree
  once the gain is accounted for, which is the gate that proves the selector is not decorative.
- **The 3:1 slope**: raising both tones 1 dB raises IM3 by 3 dB, over at least a 15 dB range.
- **Compression:** the 1 dB compression point lands at `IIP3 − 9.6 dB` (input-referred) within
  0.2 dB, measured through the real solver.
- **Ideal is exactly linear:** at `IP3 = 200` a single tone driven hard produces no harmonics at all
  — assert their absence, not their smallness.
- **Cascade sanity:** an attenuator, then this amplifier, then an attenuator, gives the algebraic
  net gain, and the two-stage intercept follows the standard cascade formula the test computes
  itself.
- `dotnet test tests/Core.Tests` and `tests/Engine.Tests`; write-up in `src/Core/RESOLVED.md`.
