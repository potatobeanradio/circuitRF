# Brief PAA-4 — Feed mode: element scope, weights, quantisation and the channel cell

**Series:** PAA-0 … PAA-10 · **Tag:** `R-paa4-<n>` · **Depends on:** PAA-3 · **Design note:** §4.1–§4.5
**Area:** `src/Core/Expressions/` (element scope, the weight functions), `src/Core/Elaboration/` (placing the channel
cell), `PhasedArrayModel`, `src/Core/Devices/System/` (a `PhaseShifterModel`), `ComponentTypeRegistry`, `src/Core/RESOLVED.md`.

## The gap

A large array cannot have a pin per element, and a system designer wants the per-element chain — LNA or PA, attenuator,
phase shifter — drawn once, with the mixer, filter and common amplifiers outside the array in an ordinary System
schematic. Steering and tapering need a per-element expression that knows the element's position, and those expressions
must stay sweepable: a sweep of the steer angle, the taper level or the phase-shifter bit count is an ordinary
`parametric_sweep`. None of this exists: there is no per-element scope, no steering function, no quantisation, and no
way to place a cell once per element.

## Requirements

**R-paa4-1 — Feed mode.** `Mode=feed` gives the component one pin, `F`. Inside, element n is driven through an ideal
matched weight `wₙ = Amp·exp(j·Phase)·exp(−j·2πf·Delay)`, quantised (R-paa4-4), then through the channel cell if one is
named (R-paa4-5), then into the array network of PAA-3. The feed's split is the ideal N-way of the `Combiner`
(`CombinerModel`'s S, matched and lossless at the common port), so the noise and power conventions of the Chain Budget's
arrays hold unchanged. N up to 4096; above that refused, naming the limit.

**R-paa4-2 — Element scope.** `Amp`, `Phase`, `Delay` and their `[n]` overrides are evaluated once per element in a
scope layered over the instance's, binding `n`, `row`, `col` (1-based) and `x`, `y` (metres from the array centre,
unit-bearing lengths) by D6's convention. The same scope binds those names in the parameters of the channel cell's
instance. A reference to any of the five outside those places is refused by name, with the sentence saying where they
are valid. Cycle detection walks the element scope as an ordinary scope; it adds no edges back. `Phase` takes degrees as
its site unit, as the Combiner's `Phase[k]` does.

**R-paa4-3 — The weight functions.** Standard functions, valid anywhere, whose position arguments default to `x`, `y`
in element scope and are required elsewhere:

- `steer(theta0, phi0, f0[, x, y])` — phase in degrees, `−360·f0/c·(x·sinθ₀cosφ₀ + y·sinθ₀sinφ₀)`;
- `steerDelay(theta0, phi0[, x, y])` — seconds, `−(x·sinθ₀cosφ₀ + y·sinθ₀sinφ₀)/c`;
- `taylor(sll, nbar)`, `chebyshev(sll)`, `cosq(q)` — separable over rows and columns (they read `row`, `col`, `Rows`,
  `Cols` from element scope), normalised to 1 at the peak; refused outside element scope.

Each in `docs/design/expressions.md`'s function table, with its unit behaviour stated (θ₀ unit-bearing or radians as
any trig argument). `steer()` and `steerDelay()` are also what PAA-5's symbol and PAA-9's tool recognise to draw and drag
the beam, so their VAR arguments are discoverable from the parsed expression, not from text.

**R-paa4-4 — Quantisation.** `PhaseBits` rounds each phase to `360/2^b` degrees (0 = continuous); `AttenStep` rounds each
amplitude's attenuation from the largest to the step in dB (0 = continuous); both after the overrides and before the
channel. The quantised weights are in the `array` group as `wQuantised` beside the requested `w`, so a plot shows both.

**R-paa4-5 — The channel cell.** `Channel=<cell>` names a two-port cell (its first two ports in, out); elaboration places
it N times as hierarchical instances `PA1.ch[n]`, with element scope bound in its parameters, and their measurements and
node paths are reachable as any sub-instance's (`V(PA1.ch[3].out)`). A channel with other than two ports is refused
naming its ports. A channel whose resolved parameters do not depend on `n`, `row`, `col`, `x`, `y` is marked
**identical** on the elaborated netlist — the fact PAA-8's fast path reads — and `explain` says which.

**R-paa4-6 — `Off`.** An element in `Off` gets weight zero and its array port terminated in Z0; its channel is still
placed (a channel's noise still reaches the feed through its own path) unless the channel is identical, in which case it
is omitted and `explain` says so.

**R-paa4-7 — A PhaseShifter System block.** An ideal matched two-port: `Phase` (degrees), `IL` (dB), `Bits`, `Delay`
(seconds), with the System palette's conventions and a symbol of its own. Lossless and reciprocal at `IL=0`; its noise is
its loss, as every passive System block's. For channel cells and Elements-mode benches.

**R-paa4-8 — It is a System block among System blocks.** Feed mode composes with the Amplifier, Mixer, Filter,
Attenuator, Combiner and the rest in every analysis they run in: S-parameters, S-parameter noise, HB, multi-tone HB and
envelope. A mixer before the array puts the array at the mixer's output frequency, and the pattern is formed at the
frequency the block sees.

## Gates

1. **Steering.** A 16×16 Feed-mode array on the E5 fixture with `Phase=steer(theta0,0,5.8 GHz)` points its beam at
   `theta0` ∈ {0°, 30°, 45°} within PAA-1's pointing tolerance, and a `parametric_sweep` of `theta0` returns one pattern
   per point.
2. **Element scope equals overrides.** `Phase=steer(…)` and the same values written as 256 `Phase[n]` overrides give
   the same pattern to 1e-12; a reference to `row` in a VAR is refused naming where it is valid.
3. **Taper.** `Amp=taylor(-30,4)` on a 32×32 isotropic-element array gives a first sidelobe within 0.5 dB of −30 dB in
   both principal planes.
4. **Quantisation.** `PhaseBits=3` on a 1×16 line puts quantisation lobes where the closed form for a 45° step puts
   them; `PhaseBits=0` is identical to no field.
5. **Channel cell.** A channel of an Amplifier (G 20 dB) and an Attenuator (3 dB) gives a realized gain 17 dB above the
   same array with no channel, to 1e-9, and is marked identical; a channel whose attenuator is `3+0.1*n dB` is not.
6. **System chain.** Port → Mixer → Filter → Feed-mode PhasedArray with an Amplifier channel runs S-parameters, HB and
   S-parameter noise; the noise figure at the Port matches the Chain Budget's closed form for the same chain with the
   array's gain, to 1e-6 dB, with `Coupling=off`.
7. **PhaseShifter.** `Phase=90` gives S21 = −j at `IL=0` and `Bits=2` rounds 100° to 90°.

## Not here

Receive waves and the array's own noise temperature (PAA-7); the fast path (PAA-8); the symbol (PAA-5).
