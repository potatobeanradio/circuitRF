# Brief SYS-4 — passive intermodulation on the ideal blocks

**Read first:** `brief-sys-series.md` (the second finding), `brief-sys-2-ideal-s-block.md`,
`brief-sys-3-balun-circulator-coupler.md`, and `src/Core/Devices/MixerModel.cs` — whose `tanh`
limiter, intercept arithmetic and snap-to-ideal thresholds this brief reuses rather than reinvents.

**What the owner asked for:** the circulator, the directional coupler and the 90° hybrid may carry
a user-specified passive intermod; **the default is off, with no intermod calculation at all**.
This brief adds it there and to the attenuator, which makes an attenuator with `Loss = 0` a
standalone PIM generator that can be placed in front of anything.

## The mechanism

A nonlinearity in this repository is `Evaluate(v)` — memoryless. A block whose ideal S is
frequency-flat and whose `Y = (1/Z₀)(I − S)(I + S)⁻¹` exists can be written memorylessly, and PIM
is then a soft limiter applied to the port voltages before the linear map:

```
   phi(v) = Vsat · tanh(v / Vsat)          (phi = identity, exactly, when PIM is off)
   i_p    = SUM_w  H[w](omega) · SUM_q  M^w_pq · phi(v_q)
```

with `M⁰ = Re(Y)` at `H[0] = 1`, and — only where S is complex — `M² = Im(Y)` at
`H[2](ω) = j·sign(ω)`. That second bucket is the whole reason the 90° hybrid can host PIM: it is a
frequency-domain factor carried inside a nonlinear model, which is a facility the SDD already uses
and which HB (`HbNewton`), the DC engine (`NonlinearDcEngine`, which already expects `H(0) = 0` for
some `w`) and `StampLinearized` all honour. Override `ComponentModel.Weight` and it works.

**`tanh`, not the textbook `a₁x − a₃x³`, and the reason is on the record:** a bare cubic turns over
and goes negative past its peak, Newton finds that root, and harmonic balance converges cleanly
onto nonsense. `tanh` is monotone and bounded everywhere and has the same third-order term. Its
fifth- and seventh-order products come out at `tanh`'s own fixed ratios — an ideal, not a fit — and
the user documentation must say so rather than implying the model was tuned to a measurement.

## Kind switches per INSTANCE

- **PIM off** (the default): `ModelKind.Linear`, SYS-2's wave-constraint stamp, the exact ideal S,
  **zero** cost in the HB nonlinear partition.
- **PIM on**: `ModelKind.Nonlinear`, the memoryless form above.

This is legal because every engine reads `Kind` off the model instance
(`SParameterEngine:300`, `NonlinearDcEngine:281/1139`, `ElaboratedComponent.IsNonlinear`) and
because a LINEAR model may already use the 2N-net ± pair convention nonlinear models require —
`ZPortModel` is the proof, so **the net contract does not change when a user turns PIM on**.

One consequence to document rather than hide: `SParameterEngine` runs a nonlinear DC solve for the
whole netlist as soon as ANY component is nonlinear. Turning PIM on therefore changes what an
S-parameter run does, even though it must not change what it reports.

**`Y` must exist, and the constructor must check.** `Y = (1/Z₀)(I − S)(I + S)⁻¹` fails when
`det(I + S) = 0`. For the ideal circulator `det(I + S) = 2` and for the ideal coupler it is well
conditioned, but a user's own numbers can reach a singular S. Refuse at construction, by name,
saying which block and that PIM needs an S the block can express memorylessly — never produce a
NaN inside a Newton iteration, where nothing on the stack can name the instance.

## Parameters (**D4**)

**Recommended — the datasheet form.** A PIM specification is quoted as an absolute product level
against two stated carriers:

| Parameter | Default | Meaning |
|---|---|---|
| `PIM` | `-200` dBm | The third-order product level. **−200 means off** — the model snaps to an exactly linear path above the threshold, as `MixerModel` does with `IIP3`. |
| `PIMPc` | `43` dBm | Power per carrier the `PIM` figure was measured at. |

The conversion is one line and belongs in the model, not in the user's head:

```
   IIP3(dBm) = (3·PIMPc − PIM) / 2
   Vsat      = 0.5·sqrt(2 · 1e-3 · 10^(IIP3/10) · Z0)          (volts; MixerModel's own derivation)
```

Worked, so the gate can check it independently: a part specified at −110 dBm with two +43 dBm
carriers is `IIP3 = (129 + 110)/2 = 119.5 dBm`. The dBc alternative (`PIM = −153 dBc` for the same
part, `IIP3 = PIMPc − PIM_dBc/2`) is the same arithmetic in different clothes; pick one and put the
other in the documentation as a conversion.

## Milestones

1. **The overlay in the base class** — `phi`, the `Y` derivation with its refusal, the conditional
   `Kind`, `Evaluate`/`EvaluateInto`, and `Weight` for the complex case. Nothing block-specific.
2. **Attenuator and circulator** (both real S — no weighting bucket needed). This is the milestone
   that proves the arithmetic.
3. **Coupler and hybrid**, including the quadrature bucket.
4. **The equivalence gate below**, which is the one that matters.

## Must NOT

- Change any block's behaviour when PIM is off. That is not a hope, it is milestone 4's gate.
- Add PIM to the filter or the duplexer. Their S is frequency-dependent, a memoryless nonlinearity
  cannot be attached to it inside one component, and the honest answer — an attenuator with
  `Loss = 0` and a PIM spec placed in front — is better than a fiction. Refuse by name, and say
  what to do instead in the refusal.
- Add PIM to the balun or the amplifier. The amplifier has an intercept of its own (SYS-5), and
  putting two nonlinearities in one device makes both unreadable.
- Model PIM as a noise-like or randomised process. It is a deterministic memoryless nonlinearity
  here, and a user reading a −150 dBc number expects a −150 dBc product.

## Gates

- **Off is off, exactly.** With `PIM` at its default, every block's simulated S is **bit-identical**
  to SYS-3's, and the netlist reports no nonlinear component. Assert both.
- **Nearly-off agrees.** With `PIM` set 60 dB below the level that would matter, the S-parameters
  agree with the linear path to 1e-12 — this is what catches a `Y` derivation that is subtly not
  the inverse of the S being stamped.
- **The specified level comes out.** Two tones at `PIMPc` through the block produce an IM3 product
  at the `PIM` level to within 0.1 dB, computed end to end through the real HB solver and compared
  against the dBm figure the user typed — not against a number read back out of the model. Do this
  at two carrier powers and confirm the 3:1 slope.
- **PIM routes like the signal.** In the circulator, products generated by two tones entering
  port 1 appear at port 2 and are isolated from port 3 by the same isolation the linear path has.
- **The quadrature bucket is real:** the hybrid with PIM on still shows `arg(S31) − arg(S21) = −90°`
  at small signal, at every swept frequency. A weighting term with the wrong sign passes every
  amplitude test and fails this one.
- **DC.** A block with PIM on solves at ω = 0, where `H[2](0) = 0` removes the quadrature bucket.
  Confirm the remaining DC Jacobian is not singular for each block; if it is for one of them, say
  which and why in the write-up rather than adding a conductance to paper over it.
- `dotnet test tests/Core.Tests` and `tests/Engine.Tests`; write-up in `src/Core/RESOLVED.md`.
