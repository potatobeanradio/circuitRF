# Brief SYS-6 — the ideal filter, and the duplexer built out of two of them

**Read first:** `brief-sys-series.md`, `brief-sys-2-ideal-s-block.md`, and
`src/Core/Match/MatchPrototypes.cs` — whose `MatchPoly` (`Roots`, `FromRoots`, `Mul`, `Trim`,
`Eval`) and `Hurwitz` spectral factorisation are the reusable half of this brief, and whose
comment "never trim by exact zero" is a trap already paid for once. Also
`src/Ui/Schematic/BuiltInSymbols.cs` §`BuildMatchVariant`/`MatchWaveStack` for the dynamic glyph,
and `docs/user/src/reference/dynamic-symbols.md`, which must gain a section.

## The filter is a transfer function, not a ladder

The obvious implementation — synthesise a doubly-terminated LC ladder from prototype g-values and
stamp the elements, as `MatchModel` does — is the wrong one here, and for a specific reason:
**a doubly-terminated ladder does not admit an arbitrary source/load impedance ratio.** The
termination ratio is fixed by the family and the order (an even-order Chebyshev has a particular
one; Butterworth needs equal ends), so "input/output impedance" — which the owner listed as a user
parameter — would become a constrained pair with refusals attached. That is the Match component's
territory and it already lives there.

Stamped as an S-matrix instead, the reference impedances are simply what S is defined against:
port 1 is matched to `Zin`, port 2 to `Zout`, the response is exactly the prototype's, any pair of
impedances works, and there is no synthesis feasibility question at all. It is also a lossless
impedance transformer in the bargain, which is a real thing a real filter can be designed to be —
say so in the documentation rather than leaving it as a surprise.

**It must be the true rational S, not a magnitude.** A magnitude-only response is zero-phase, has
no group delay, and would make the Bessel option meaningless — Bessel exists for its phase.

## The mathematics

Let `s = jω/ω_c` after the frequency transformation below. For every family except Bessel the
characteristic function `C_n` gives

```
   |S21(jw)|^2 = 1 / (1 + eps^2 · C_n(w)^2)              |S11|^2 = 1 - |S21|^2
```

and the causal `S21(s) = k / E(s)` (plus transmission zeros where the family has them) comes from
factoring the denominator into its left-half-plane roots — `MatchPrototypes.Hurwitz`, which is
already written and already knows about the relative-tolerance trim. `S11(s) = F(s)/E(s)` follows
from the Feldtkeller relation `E(s)E(−s) = F(s)F(−s) + P(s)P(−s)`.

| `Response` | `C_n(ω)` | Extra parameter | Transmission zeros |
|---|---|---|---|
| `Butterworth` | `ω^n` | — | all at infinity |
| `Chebyshev` | `T_n(ω)` | `Ripple` (dB, passband) | all at infinity |
| `InvChebyshev` | `1 / T_n(1/ω)` | `Astop` (dB, stopband floor) | on the jω axis, in the stopband |
| `Bessel` | — (defined by its polynomial) | — | all at infinity |
| `Elliptic` | `R_n(ξ, ω)` | `Ripple` **and** `Astop` | on the jω axis |

**Bessel is not of that form** and must not be forced into it: `S21(s) = θ_n(0)/θ_n(s)` from the
reverse Bessel polynomial, normalised for unit delay at DC, with `S11` from the same Feldtkeller
step. Its `|S21|` is not equiripple or maximally flat in magnitude — it is maximally flat in group
delay, which is the only reason to choose it, and which is what its gate must measure.

**Frequency transformation**, applied to the prototype before evaluation:

```
   Lowpass    s -> s / w_c
   Highpass   s -> w_c / s
   Bandpass   s -> (1/BW)·( s^2 + w_0^2 ) / s        w_0 = sqrt(F1·F2), BW = w2 - w1
```

The bandpass transformation **doubles the degree**: a user's `Order = 3` bandpass is a 6th-degree
network, and `Order` means the prototype order. Say which in the parameter description, because
both conventions exist in the wild and a user comparing against a datasheet needs to know.

## Parameters

| Parameter | Default | Meaning |
|---|---|---|
| `Response` | `Chebyshev` | The five families above. |
| `Form` | `Bandpass` | `Lowpass`, `Highpass`, `Bandpass`. Drives the glyph. |
| `Order` | `3` | Prototype order. |
| `Fc` | `1 GHz` | Cutoff, for `Lowpass`/`Highpass`. |
| `F1`, `F2` | `0.9`, `1.1 GHz` | Band edges, for `Bandpass`. |
| `Ripple` | `0.1` dB | Passband ripple. `Chebyshev`, `Elliptic`. |
| `Astop` | `40` dB | Stopband floor. `InvChebyshev`, `Elliptic`. |
| `Zin`, `Zout` | `50` Ω | Port reference impedances. |
| `IL` | `0` dB | A flat insertion loss on top of the ideal response. |

`IL` multiplies `S21` and leaves `S11` alone, so the block is genuinely lossy rather than
redistributing energy — which is what a real filter's dissipation does.

Parameters that do not apply to the selected `Response` must be **ignored, not refused**: a user
switching Chebyshev to Butterworth should not have to clear a ripple field. The parameter
descriptions say which family reads which.

## The duplexer is two filters sharing a node

Three ports, six nets `[ant+, ant−, tx+, tx−, rx+, rx−]`, and **no new mathematics at all**: it
stamps two independent `Filter` S-blocks, one between the ANT pair and the TX pair, one between the
ANT pair and the RX pair, onto the same ANT nets. Four branch currents, no internal node, and the
antenna-node interaction, the TX-to-RX isolation and each arm's stopband reflection all fall out of
the shared node rather than being separate parameters.

Its parameters are two complete filter specifications with a `Tx`/`Rx` prefix, plus one shared
`Zant`. **Do not add an `Isolation` parameter**: the isolation a duplexer achieves is a consequence
of the two responses and their junction, and a user who types one would be overriding physics with
a number. If the measured isolation surprises them, that is the model telling them something true
about their band plan.

**A phasing line is deliberately absent.** A real duplexer needs one because real filters do not
present an ideal open outside their band; the rational reflections here do carry the right phase,
so the ideal junction behaves. A user who wants to model the phasing places a `TLIN` in the arm.

## Milestones

1. **The polynomial core**, in `src/Core/Systems/` (or beside Match — one folder, named in the
   write-up), reusing `MatchPoly` and `Hurwitz`. Butterworth, Chebyshev, inverse Chebyshev, Bessel.
   Pure functions, tested against closed-form magnitudes with no simulator in the loop.
2. **The transformations** and the S evaluation at a given ω, including the ω = 0 and ω → ∞ limits.
3. **`FilterModel`** on SYS-2's base, registry, factory, elaborator net count, and the **dynamic
   glyph on `Form`** wired exactly where `Match`'s is (`EditableSchematic.BuildRenderModel`,
   `DocSymbolGlyph`, the per-variant cache).
4. **`DuplexerModel`** — two filter stamps, one shared node.
5. **Elliptic (D6), last and separable.** It is the only item in the series needing mathematics the
   repository does not have: Jacobi elliptic `sn`/`cn`/`dn` (AGM or Landen), the complete integral
   `K(m)`, the degree equation relating order, selectivity and the two ripple figures, and the
   elliptic rational function's poles and zeros. **If it exceeds a day's work, stop and report** —
   the other four families ship without it, the `Response` list simply does not offer it yet, and a
   half-correct elliptic filter is worse than an absent one.

## Must NOT

- Synthesise an LC ladder, mint internal nodes, or touch `MatchModel`. The two components answer
  different questions and share only polynomial helpers.
- Add PIM. Its S is frequency-dependent and a memoryless nonlinearity cannot attach to it inside one
  component — refuse by name, and name the alternative in the refusal (an `Atten` at `Loss = 0`
  with a PIM specification, placed in the path).
- Add a band-stop `Form`. Not requested. It is a two-line transformation once the rest is built, and
  it is the owner's call whether to want it.
- Refuse an order the family supports. If an order genuinely cannot be realised for a family, that
  is a finding for the write-up, not a silent clamp.
- Copy `MatchWaveStack` into a second place. Factor it, and gate that `Match`'s own primitives are
  byte-identical afterwards.

## Gates

Every one of these computes its expectation independently — from the textbook magnitude formula,
not from our own polynomial machinery:

- **Butterworth:** `|S21|² = 1/(1 + (ω/ω_c)^{2n})` to 1e-10 at fifteen frequencies, for n = 1…7.
- **Chebyshev:** `|S21|² = 1/(1 + ε²T_n²(ω/ω_c))`, and separately the structural properties — the
  passband has exactly `n` ripple extrema and each touches the stated ripple.
- **Inverse Chebyshev:** the stopband floor equals `Astop` and is equiripple; the passband is
  maximally flat.
- **Bessel:** group delay, computed by differencing the simulated phase, is flat at DC to the order's
  own tolerance and monotone thereafter. Magnitude is NOT the gate for this family.
- **Rolloff:** every family reaches `20·n` dB per decade far into the stopband.
- **Unitarity:** `|S11|² + |S21|² = 1` to 1e-12 at `IL = 0`, across the whole sweep and every form.
- **Limits:** lowpass at ω = 0 is an exact through; highpass at ω = 0 is an exact open; bandpass at
  ω = 0 is an exact open. All three must SOLVE (SYS-2's degenerate-case gate is what makes them).
- **Transformations:** a highpass at `Fc` mirrors its lowpass about `Fc`; a bandpass is geometrically
  symmetric about `sqrt(F1·F2)`.
- **Unequal impedances:** a filter with `Zin = 50`, `Zout = 25` is matched at both ports in its
  passband, and its measured S in a uniform 50 Ω system renormalises to that.
- **Duplexer:** each arm reproduces the corresponding standalone filter's `S21` to 1e-9 in its own
  passband; the TX-to-RX isolation is measured and reported in the write-up (not asserted against a
  number pulled from the air); the ANT port's return loss is good in both bands.
- **HB:** a two-tone signal through a bandpass filter passes the in-band tone and rejects the
  out-of-band one at the level the response states, and creates nothing.
- `dotnet test tests/Core.Tests`, `tests/Engine.Tests`, `tests/Ui.Tests` (the glyph); write-up in
  `src/Core/RESOLVED.md`, with the glyph half in `src/Ui/RESOLVED.md`.
