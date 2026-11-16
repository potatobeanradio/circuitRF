# circuitRF — Filter synthesis: from a specification to a placed filter

**Status:** in progress · **Date:** 2026-11-16

A filter is asked for by its specification (`FilterSpec`: response, band, order, edges, ripple, stopband, Z0 and any
transmission zeros) and built by a **realiser**: one family and form that turns the specification into one built-in
part with its parameters, plus the values its lumped sites want (`FilterRealisation`). The parts are the geometry-stored
filter families of the RF passives (docs/design/rf-passives.md: MLAD, MCRC, MCLA, and the lumped sites), so a
synthesised filter is an ordinary placed part the user can still edit by hand. Realisers are found by type
(`FilterRealisers.All`); each family adds its own.

## 1. Prototypes and lumped ladders

(to be written)

## 2. The coupling matrix

(to be written)

## 3. Ladders on MLAD

(to be written)

## 4. Coupled resonators on MCRC

**What is built.** An all-pole bandpass filter (Butterworth or Chebyshev, 1 to 20 resonators, at most 20 % wide) as one
MCRC, in six forms, each a realiser (`src/Core/Synthesis/Filters/CoupledResonatorRealisers.cs`):

| Realiser | Shape, feed | Route |
|---|---|---|
| Edge-coupled bandpass | Straight, coupled | Cohn's closed forms |
| Zig-zag bandpass | ZigZag, coupled | Cohn's, each resonator plus its corner's excess |
| Hairpin bandpass, coupled feed | Hairpin (Sa = 2·W), coupled | k(S) and Qe(S1) on the circuit model |
| Hairpin bandpass, tapped feed | Hairpin (Sa = 2·W), tapped | k(S) and Qe(Tap) |
| Stepped-impedance hairpin bandpass | Hairpin, `Profile` = SIR (the U a 2·Z0 line over 0.3 of L), coupled | k(S) and Qe(S1) |
| Open-loop bandpass, inline | OpenLoop (Gap = W), tapped | k(S) and Qe(Tap) |

The realisation is the part's stored values as text, as a placed part holds them: N, W, L1 … LN, the gaps S_k the shape
needs, Tap, Sa, Gap, Wc, Uc, and every name (Shape, Profile, Feed, Form, Arrangement, Coupling), so applied to a placed
MCRC it leaves nothing of an earlier design in force. Lengths are written in mm to the nanometre. W is the Z0 line's width
at the centre, from the same width search the plans use (`BranchLinePlan.Width`). The design code is
`CoupledResonatorDesign.cs`; the helpers that were the part's tests' own (Cohn's design, the open-loop design, the corner
excess, the open end) moved there and the tests call them.

**Edge-coupled and zig-zag: Cohn's J-inverters**
([10.1109/TMTT.1958.1124542](https://doi.org/10.1109/TMTT.1958.1124542)). Each coupled section is an admittance
inverter, J01·Z0 = √(π·FBW/(2·g0·g1)) at the ends and π·FBW/(2·√(g_k·g_(k+1))) between, with modes Ze, Zo =
Z0(1 ± JZ0 + (JZ0)²). An MCLIN pair has one width and neighbours of unequal width are refused, so every strip takes W and
each gap is the one at which MCLIN's own modes (Kirschning and Jansen,
[10.1109/TMTT.1984.1132616](https://doi.org/10.1109/TMTT.1984.1132616)) give Cohn's voltage coupling,
(Ze − Zo)/(Ze + Zo) = JZ0/(1 + (JZ0)²), by bisection. Each section is a quarter wave in its modes' mean εeff at the
centre. Each resonator is its two sections less the coupled open end at each of its ends, the mean of the even- and
odd-mode extensions in that section's static modes, and the resonators take their mean length. A zig-zag adds one
corner's excess (two arms less the corner's own phase as a length of line).

**Hairpins and open loops: coupling coefficients and external Q** (Hong and Lancaster,
[10.1109/22.543968](https://doi.org/10.1109/22.543968), and their book,
[10.1002/0471221619](https://doi.org/10.1002/0471221619)). The prototype asks for k(i,i+1) = FBW/√(g_i·g_(i+1)) and
Qe = g0·g1/FBW. Each is mapped to geometry by solving the part's own circuit model (MCLIN pairs, no EM), on the
substrate's lossless twin:

1. One resonator, fed lightly by coupled strips 3·h off, is scaled until its |S21| peak sits at f0.
2. Each gap is the one at which two such resonators split into f1 and f2 with (f2² − f1²)/(f2² + f1²) = k, by bisection
   on the gap in [0.1·h, 5·h].
3. The feed is the one at which one resonator, fed alike at both ends, has Qe = ω0·τ at its resonance, τ being S21's
   group delay there (Ness, [10.1109/22.664135](https://doi.org/10.1109/22.664135)): the feed gap S1 = S(N+1) for a
   coupled feed, the tap for a tapped one. The group delay is a local measure: a tapped resonator has a direct path
   between its taps that keeps |S21| high far below the resonance, so a 3 dB band read off it is not the resonator's. A
   tap so heavy that no resonance shows counts as Qe = 0, so the bisection moves on.
4. Each resonator is then sized for f0 where it sits: a coupled side moves a resonator's frequency by the shift of the
   pair's mean √(f1·f2) from the lone resonator's, and a feed by half the shift of a resonator fed at both ends. The middle
   loops come out about 3 % shorter than the end ones.

A hairpin's tap runs along its end section, from the U to the open end. A loop's runs from the middle of the side
opposite its opening, and in an odd row round the corner and up the outer side, the corner skipped; in an even row the
last loop's outer side is its coupled one.

**The seam for cross couplings.** `CoupledResonatorDesign.FromCouplings` takes the coupling matrix k_ij and Qe directly,
with the shape's own values, so the cross-coupled forms can hand it what the coupling-matrix synthesis finds. It builds
an inline row, so it refuses a non-zero k_ij between resonators that do not sit side by side
(`synthesis.mcrc.cross-coupling`). The cross-coupled square, and anything with transmission zeros, is left to that
synthesis (§2).

**How close it lands.** On 20 mil RO4350B, lossless, a 0.5 dB Chebyshev of order 3 at 2.4 GHz, each realised part
written as a netlist line and run through the engine (the ripple band against the specification):

| Form | FBW | Centre | Bandwidth |
|---|---|---|---|
| Edge-coupled | 10 % | −0.59 % | +6.0 % |
| Zig-zag | 10 % | −0.59 % | +4.3 % |
| Hairpin, coupled feed | 10 % | +0.15 % | +3.5 % |
| Hairpin, tapped | 10 % | +0.53 % | +6.1 % |
| Stepped-impedance hairpin | 5 % | +0.04 % | +1.4 % |
| Open loops, tapped | 5 % | +0.12 % | −3.3 % |

Cohn's chains run in about 0.1 s; the forms solved on the model in 2 to 7 s, a few hundred small model solves each.

**Limits.**
- One width per chain. Cohn's design fixes each section's W and S; with one W only the coupling ratio is met, so each
  section's image impedance is a few per cent off, and the band comes out about 5 % wider than asked.
- The stepped-impedance hairpin's coupled ends are shorter than a uniform hairpin's, so its feeds and couplings are
  weaker: a coupled feed reaches 5 % but not 10 % on 20 mil RO4350B (the feed gap 0.09 mm at 5 %), and a tap on its wide ends is
  too heavy for any band these designs take, so the tapped form is not offered.
- A tapped hairpin's lightest tap is where the arm leaves the U, which sets a narrowest band; a tapped open loop's
  strongest is near its outer side's far end, which sets a widest.
- Under `Pairs` a hairpin's own arms do not couple and loops couple only through facing sides; the design is the model's,
  and an EM run of the artwork shows what it leaves out (rf-passives.md §3.15).
- A k or Qe the ranges cannot reach is refused (`synthesis.mcrc.out-of-reach`), as is a Z0 the substrate has no width
  for (`width-unreachable`).

**Refusals.** `synthesis.mcrc.*`: `band` (not a bandpass), `zeros` (transmission zeros asked for), `response` (not
Butterworth or Chebyshev), `order` (not 1 to 20), `bandwidth` (outside (0, 20 %]), `substrate` (none given),
`width-unreachable`, `out-of-reach`, `cross-coupling`. `Supports` gives the same sentence the refusal carries.

**Tests.** `tests/Engine.Tests/Synthesis/CoupledResonatorRealiserTests.cs`: each of the six forms, found through
`FilterRealisers.All`, realised, written as an `MCRC` netlist line from its stored text and run through the engine,
passes its band (centre within 2 %, ripple bandwidth within 15 %); each refusal by its code. `CoupledResonatorSParamTests`
now takes its Cohn and open-loop designs from Core and passes as before.

## 5. Coupled arrays on MCLA

(to be written)

## 6. The Filter Designer, the `filter` verb and the MCP tool

(to be written)

## 7. Possible enhancements

- **A width per section in Cohn's chains** (MCRC, §4). An option giving each coupled section its own W, as Cohn's Ze
  and Zo ask. Why: with one W only the coupling ratio is met, and the edge-coupled band comes out about 5 % wider than asked.
- **Hairpins designed under `Coupling` = `Full`** (§4). The routine solves whatever the part's model holds, so a hairpin
  designed with `Full` takes its arms' own coupling into k(S) and Qe. Why: arms closer than 2·W move the band by several
  per cent under `Full` (rf-passives.md §3.15), which a `Pairs` design does not see.
- **A tap on a stepped-impedance hairpin's narrow centre** (§4). MCRC taps only end sections. Why: the wide ends hold
  most of the voltage, so a tap there is too heavy for any narrow band, and the usual tapped SIR filter taps the narrow
  line.
- **k(S) and Qe tables per substrate and width** (§4). Solve the pair and the fed resonator once over a grid of gaps and
  taps, then interpolate. Why: a design takes 2 to 7 s now, too slow for a designer's preview that redraws as the
  specification is typed.
- **Unequal external Q at the two ends** (§4). `FromCouplings` takes one Qe and MCRC one Tap. Why: a filter between
  unequal terminations, or a multiplexer's channel, needs a different Qe at each end.

## 8. References

- S. B. Cohn, "Parallel-coupled transmission-line-resonator filters," IRE Trans. MTT 6(2), 1958:
  [10.1109/TMTT.1958.1124542](https://doi.org/10.1109/TMTT.1958.1124542) (§4).
- J.-S. Hong and M. J. Lancaster, "Couplings of microstrip square open-loop resonators for cross-coupled planar
  microwave filters," IEEE Trans. MTT 44(11), 1996: [10.1109/22.543968](https://doi.org/10.1109/22.543968) (§4).
- J.-S. Hong and M. J. Lancaster, *Microstrip Filters for RF/Microwave Applications*, Wiley, 2001:
  [10.1002/0471221619](https://doi.org/10.1002/0471221619) (§4).
- M. Makimoto and S. Yamashita, "Bandpass filters using parallel coupled stripline stepped impedance resonators," IEEE
  Trans. MTT 28(12), 1980: [10.1109/TMTT.1980.1130258](https://doi.org/10.1109/TMTT.1980.1130258) (§4).
- J. B. Ness, "A unified approach to the design, measurement, and tuning of coupled-resonator filters," IEEE Trans. MTT
  46(4), 1998: [10.1109/22.664135](https://doi.org/10.1109/22.664135) (§4).
- M. Kirschning and R. H. Jansen, "Accurate wide-range design equations for the frequency-dependent characteristic of
  parallel coupled microstrip lines," IEEE Trans. MTT 32(1), 1984:
  [10.1109/TMTT.1984.1132616](https://doi.org/10.1109/TMTT.1984.1132616) (§4).
