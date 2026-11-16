# circuitRF — Filter synthesis: from a specification to a placed filter

**Status:** in progress · **Date:** 2026-11-16

A filter is asked for by its specification (`FilterSpec`: response, band, order, edges, ripple, stopband, Z0 and any
transmission zeros) and built by a **realiser**: one family and form that turns the specification into one built-in
part with its parameters, plus the values its lumped sites want (`FilterRealisation`). The parts are the geometry-stored
filter families of the RF passives (docs/design/rf-passives.md: MLAD, MCRC, MCLA, and the lumped sites), so a
synthesised filter is an ordinary placed part the user can still edit by hand. Realisers are found by type
(`FilterRealisers.All`); each family adds its own.

## 1. Prototypes and lumped ladders

**The prototype as a ladder.** `PrototypeLadder` holds g0 … g(N+1) and, for each element, the prototype frequency Ωk at
which it resonates with a companion, +∞ where it has none. Shunt-first, element k is a shunt C for odd k and a series L
for even k, and a series L of finite Ωk has a C of 1/(gk·Ωk²) across it: a tank, the transmission zero at Ωk.
Series-first is the dual, with the same numbers: a series L for odd k, and a shunt C with an L of 1/(gk·Ωk²) in series,
a trap. `LowpassPrototype.GValues` still gives the g-values alone, for the responses that have no companions.

| Response | How |
|---|---|
| Butterworth, Chebyshev | The closed forms (Matthaei, §4.05). The Chebyshev form now takes 40/ln 10 where the tables print 17.37, so its ladder has the equiripple response to rounding instead of to 1e-5. |
| Bessel | The continued fraction of the input impedance (E + αF)/(E − αF) of the ideal filter's own Bessel response (`FilterPrototype`, unit group delay at DC), by Match's extraction (`MatchPrototypes.Extract`). Its reflection zeros are `FilterPrototype`'s left-half-plane factor, which is Matthaei's table 4.07-1: g falling from the source. |
| Elliptic, inverse Chebyshev | Odd orders, by zero shifting from `FilterPrototype`'s E, F and P (Saal and Ulbrich; Amstutz). W = (E ± αF)/(E ∓ αF) is the shunt-first ladder's input admittance, with a pole at infinity. For each zero Ωk, a shunt C of W(jΩk)/(jΩk) is partly removed, which leaves W a zero at jΩk; 1/W's pole there is then removed whole, as a tank A·s/(s² + Ωk²) in the line. What is left is the last shunt C and the load. The zeros are taken highest first, and the next order is tried until every element is positive. |
| Gaussian | Not added. It is not a `FilterResponse`, and adding one changes the ideal `Filter` part's options, model and documentation; §7. |

An even-order elliptic or inverse Chebyshev ladder is refused (`synthesis.proto.even-order`): its |S21(0)| is below 1,
so it has no equal-terminated ladder without a modified response (§7).

**The transforms** (`LumpedLadder.Transform`), each normalised reactance on its own, for either start. ω1 = 2π·F1 is a
lowpass's or a highpass's cutoff; ω0 = 2π·√(F1·F2) and B = 2π·(F2 − F1) are a band's centre and width (Matthaei, §4.04):

| | a prototype L | a prototype C |
|---|---|---|
| lowpass | L·Z0/ω1 | C/(Z0·ω1) |
| highpass | a C of 1/(L·Z0·ω1) | an L of Z0/(C·ω1) |
| bandpass | L·Z0/B in series with a C of B/(L·Z0·ω0²) | C/(Z0·B) in parallel with an L of Z0·B/(C·ω0²) |
| bandstop | L·Z0·B/ω0² in parallel with a C of 1/(L·Z0·B) | C·B/(Z0·ω0²) in series with an L of Z0/(C·B) |

The result is a list of arms, each in the line or to ground, with its parts in series or in parallel, and the load it
is designed into: Z0, or g(N+1)·Z0 (Z0/g(N+1) after a series arm) where g(N+1) ≠ 1, as for an even-order Chebyshev. A
prototype with zeros moves to a lowpass or a highpass only: in a band each of its resonators would sit inside another
(`synthesis.proto.zeros-in-band`).

**The realiser, "Lumped LC ladder (MLAD sites)".** It returns an MLAD whose `Sections` are sites alone, with `Z0` and `F`
(the centre, where the readout states the pads), and each site's value on its G pins. A lowpass starts on a shunt C and a
highpass on a series C, the fewer inductors. A ladder with zeros starts shunt, so its resonators are tanks in the line.
Each arm becomes:

| Arm | Sections |
|---|---|
| one part | a series or a shunt site |
| two parts in series, in the line (a bandpass's series arm) | two series sites; the plan puts a pad between them |
| two parts in parallel, to ground (a bandpass's shunt resonator) | a shunt site, then a parallel site |
| two parts in parallel, in the line (an elliptic tank) | a series site, then a parallel site |
| two parts in series, to ground (a bandstop's trap) | none; refused, `synthesis.proto.bandstop` |

A bandstop always has traps, at either start (except a single tank), so it is refused whole. Its notes state the start
and that the parts are at their ideal values, the pads not counted, and, where the load is not Z0, what it should be.

**The parallel site, a small addition to MLAD's list.** `site(role=…, value=…, type=parallel)` is one more part on the
pins of the site before it, in parallel with that site's part. It has no copper and no pins of its own, so the symbol, the
plan's copper and every stored list are unchanged; `LadderPlan.Build` skips it in its walk and declares it with its site,
on the same pins, kind and span. A parallel site with no site before it is refused, `device.ladder.parallel-alone`.
Without it, a tank in the line could not be written at all, and a shunt resonator only as two shunt sites a tee's width
apart. Place Site Parts fills both sites of a pair, since both are empty when it starts.

**What the tests hold.** `PrototypeLadderTests` (Core):

- each ladder against `FilterPrototype`'s S21, to 1e-6 at nine frequencies, at both starts: Butterworth, Chebyshev of an
  odd and an even order, Bessel 2 and 7, elliptic 3, 5 and 7, inverse Chebyshev 5;
- Bessel against table 4.07-1, N = 2 to 6, to its four decimals;
- the four transforms of an even-order Chebyshev (a load of g(N+1)·Z0), and the lowpass and highpass of an elliptic,
  against the prototype at the mapped frequency, to 1e-6 at both starts;
- the realiser's sites against the plan's, by role, pins and value;
- the parallel site's text, pins and sites, and each refusal's code.

`LumpedLadderRealiserTests` (Engine) fills the realised MLAD with ideal parts of its sites' values, on 20 mil RO4350B at
200 MHz, where the pads cost about 1 %, and finds each −3 dB edge against the ideal filter's (`FilterNetwork`), to ±2 %:

- a 0.1 dB Chebyshev lowpass of order 5: −0.87 %;
- a Butterworth highpass of order 3: −1.13 %;
- a Butterworth bandpass of order 3, 160 to 250 MHz: −1.02 % and −1.61 %;
- a 0.1 dB, 40 dB elliptic lowpass of order 5: −0.41 %, with both zeros, its tanks on parallel sites, below −300 dB.

## 2. The coupling matrix

(to be written)

## 3. Ladders on MLAD

(to be written)

## 4. Coupled resonators on MCRC

(to be written)

## 5. Coupled arrays on MCLA

(to be written)

## 6. The Filter Designer, the `filter` verb and the MCP tool

(to be written)

## 7. Possible enhancements

1. **Even-order elliptic and inverse Chebyshev ladders.** The modified response, which moves the highest zero to
   infinity, or a design for unequal terminations. Why: an even order has |S21(0)| below 1, so §1 refuses it, and the
   designer has to go up an order.
2. **Gaussian and equiripple-delay responses.** Each a `FilterResponse` word with its polynomial; §1's extraction then
   gives the ladder. Why: pulse and wideband-delay filters want them, and only Bessel is offered.
3. **A trap site.** A part in series beyond a shunt site's pad, to ground through a second part. Why: a lumped bandstop's
   shunt arms, and a series-first elliptic's, are LC in series to ground, which no site holds, so §1 refuses them.
4. **Elliptic bandpass and bandstop.** A ladder form with each zero in an arm of its own. Why: the plain transform nests a
   resonator in a resonator, which §1 refuses.
5. **The pads in the values.** Each shunt pad's inductance taken out of its part's value. Why: the pads lower a 1 GHz
   cutoff about 5 % on a 20 mil board, and the realiser states the ideal values.
6. **Parallel parts side by side.** Place Site Parts standing a parallel site's part beside the first. Why: both land on
   the same pins today, one over the other.

## 8. References

- G. L. Matthaei, L. Young and E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling
  Structures*, McGraw-Hill, 1964: §4.04 (the transforms), §4.05 (Butterworth and Chebyshev), table 4.07-1 (maximally
  flat delay). A book, no DOI: [Open Library](https://openlibrary.org/works/OL153218W).
- R. Saal and E. Ulbrich, "On the design of filters by synthesis", *IRE Trans. Circuit Theory*, 1958,
  [10.1109/TCT.1958.1086481](https://doi.org/10.1109/TCT.1958.1086481).
- P. Amstutz, "Elliptic approximation and elliptic filter design on small computers", *IEEE Trans. Circuits Syst.*,
  1978, [10.1109/TCS.1978.1084429](https://doi.org/10.1109/TCS.1978.1084429).
