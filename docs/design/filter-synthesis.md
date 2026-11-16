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

`CouplingMatrix` (`src/Core/Synthesis/Filters/CouplingMatrix.cs`) is the numerical core under every coupled-resonator
realiser: pure numbers in the normalised lowpass domain (passband |ω| ≤ 1), with no part output. A matrix is N+2 square,
indexed S, 1 … N, L.

**The polynomials** (`Characteristic`). The generalised Chebyshev filtering function of order N, equiripple at a given
return loss, with up to N prescribed finite transmission zeros ω_n outside the passband (the rest at infinity), by
Cameron's recursion ([10.1109/22.754877](https://doi.org/10.1109/22.754877)): C_N(ω) = cosh Σ arccosh x_n with
x_n = (ω − 1/ω_n)/(1 − ω/ω_n), built as U + √(ω² − 1)·V, whose U is the reflection numerator F. P is the product over
the zeros; ε sets the ripple at ω = 1; εR is 1 unless all N zeros are finite (fully canonical). E comes from
|E|² = |F/εR|² + |P/ε|², as the roots of F/εR + jP/ε and their conjugates that lie in the upper ω half-plane. Everything
but E is real in ω, so the work is done there with `MatchPoly` (`Mul`, `Add`, `FromRoots`, `Roots`, `Eval`) and only E
needs complex coefficients. The result, `ChebyshevPolynomials`, holds E, F and P as complex coefficients in s with ε
and εR, so S11 = F/(εR·E) and S21 = P/(ε·E); P carries a factor j when N − nfz is even, which keeps the matrix real.

**The transversal matrix** (`Transversal`). On s = jω, y22 and y21 have the common denominator Re Q, with
Q = E + F/εR and Re, Im taking the coefficients' parts; its N real roots λ_k are the poles. From the residues r22k and
r21k at each, M_kk = −λ_k, M_Lk = √r22k and M_Sk = r21k/√r22k; M_SL is the direct term a fully canonical y21 keeps as
s → ∞ (Cameron 2003, [10.1109/TMTT.2002.806937](https://doi.org/10.1109/TMTT.2002.806937)).

**The folded form** (`Folded`). Plane rotations about resonator pivots clear the source row from the far end inwards,
then the load column, alternating towards the centre: for N = 4, M_S4, M_S3, M_S2, M_2L, M_3L, then M_13. What is left is
the main line, the cross-diagonal couplings, the self-couplings and M_SL. The source and load rows are never pivots, so
M_SL survives unchanged. `Synthesise` runs all three.

**The response** (`Response`). [A] = [G + (s + δ)·U + jM], with unit conductances G at S and L and U the resonator
identity: S21 = 2·A⁻¹(L, S) and S11 = 2·A⁻¹(S, S) − 1, one LU solve per frequency (`WspMatrix.Lu`). A finite unloaded Q
puts δ = 1/(Qu·FBW) on every resonator, which is the lossless polynomials evaluated at s + δ.

**Physical terms** (`ToPhysical`, `FromPhysical`). At a centre f0 and fractional bandwidth FBW (Hong and Lancaster, §8.1,
[10.1002/0471221619](https://doi.org/10.1002/0471221619)):

- Qe = 1/(FBW·M²) at each port;
- k_ij = FBW·M_ij between resonators;
- each resonator's own frequency f_i = f0·(√(1 + b²) − b), with b = FBW·M_ii/2. This is M_ii = (f0/f_i − f_i/f0)/FBW
  solved, so a positive self-coupling tunes a resonator low.

A matrix whose source or load couples past its end resonator has no external Q, and is refused
(`synthesis.cm.port-cross-coupling`).

**The all-pole case** (`AllPole`). M(i, i+1) = 1/√(g_i·g(i+1)), source and load included, from `LowpassPrototype.GValues`
(Atia and Williams, [10.1109/TMTT.1972.1127732](https://doi.org/10.1109/TMTT.1972.1127732)).

**Credit.** The pipeline follows the Standard Coupling Matrix Synthesis Code (MIT,
[github.com/Yellowbooker/Standard-Coupling-Matrix-Synthesis-Code](https://github.com/Yellowbooker/Standard-Coupling-Matrix-Synthesis-Code)),
reimplemented in C# in the ω domain.

**Refusals.** All are coded `synthesis.cm.*`:

| Code | When |
|---|---|
| `order` | the order is outside 1 to `MaxOrder` = 10 |
| `return-loss` | the return loss is not positive |
| `too-many-zeros` | there are more zeros than the order |
| `zero-in-band` | a zero lies at \|ω\| ≤ 1 |
| `port-cross-coupling` | the source or load couples past its end resonator (`ToPhysical`) |
| `band-invalid` | f0 or FBW is not positive |

**Tests** (`tests/Core.Tests/Synthesis/CouplingMatrixTests.cs`):

- **Cameron's 4–2 example.** 22 dB return loss, zeros at ω = 1.3217 and 1.8082. E, F, P and ε (1.1548) match the printed
  Table 1 of Cameron's 2011 review ([10.1109/MMM.2011.942007](https://doi.org/10.1109/MMM.2011.942007)) to its four
  decimals. The folded matrix has the folded pattern (only M_14 and M_24 cross-coupled).
- **The same example's response.** The folded matrix's response matches, to 1e-3 over −3 ≤ ω ≤ 4, that of the review's
  printed two-trisection matrix (Fig. 21), a similar matrix of the same filter.
- **The matrix against the polynomials.** The matrix response, lossless and at Qu = 200 with FBW = 5 %, equals the
  polynomials' to 1e-9 for 4–2, symmetric 6–2, fully canonical 4–4 and all-pole 5. It holds to 1e-7 for 8–2 and to
  1e-5 for 10–4.
- **All-pole Chebyshev.** The synthesis matches the g-value formula for N = 3 to 6, to 5e-5.
- **Physical terms.** Qe and k match Hong's g-value forms, and `ToPhysical` and `FromPhysical` round-trip.

**Accuracy and limits.**

- **Close poles.** The residues come from polynomial coefficients. Poles a few 1e-4 apart near the band edge, as 8–2
  and 10–4 with zeros close to the band have, lose digits: about 1e-8 and 3e-7 in the response, against 1e-13 with the
  poles apart.
- **The g-value constant.** The all-pole comparison stops at about 1e-5 because `GValues` takes 40·log10 e as 17.37.
- **The folded matrix itself.** It is compared with the review's trisection form, by response, and not entry by entry
  with Cameron's folded table. The 1999 and 2003 papers were not open to this work.

**Possible enhancements** are in §7: extended precision, the arrow, CT, CQ and box forms, lossy and predistorted
synthesis, and complex zeros.

## 3. Ladders on MLAD

Six realisers write an MLAD (rf-passives.md §3.16): its `Sections` list, `Z0` and `F`, F the cutoff or the band's
centre. The designs are pure functions in `src/Core/Synthesis/Filters/LadderSynthesis.cs`, prototype g-values and a
substrate in, sections out; `LadderRealisers.cs` wraps each as an `IFilterRealiser` with the checks they share.

**What every ladder shares.**
- **The prototype** is `LowpassPrototype.GValues`, Butterworth or Chebyshev. An even-order Chebyshev ends on
  g(N+1) ≠ 1, a load other than Z0, so it is not offered; an odd order is.
- **Each line and stub** is sized at the design frequency on MLIN's own model: the width whose dispersive Z0 is the
  impedance asked for (`LadderPlan.Width`), the length whose phase is the angle asked for. An open stub is drawn short
  by its end extension. A stub below Z0 is a cross of two arms of twice its impedance, so no arm is wider than the
  ports' line.
- **A band** is centred at (F1 + F2)/2 with Δ = (F2 − F1)/f0, not at the spec's geometric centre. These are
  commensurate-line filters, and their response is symmetric in frequency about f0.
- **Widths.** A realisation whose widths fall outside H/4 to 10·H gets a note saying so. That is the range the
  stepped-impedance lowpass takes as realisable (below).
- **Points, not junctions.** The closed forms place each element at a point. The model adds the tees', crosses' and
  gaps' own reactance, which is what the tests' tolerances absorb.

| Realiser | Design | Sections |
|---|---|---|
| Stepped-impedance lowpass | Shunt element first. Each shunt C is a Zlow line of βl = g·Zlow/R0 and each series L a Zhigh line of βl = g·R0/Zhigh, at fc (Pozar §8.6, a book, no DOI). Zlow and Zhigh are given (`ZLow`, `ZHigh`), or by default MLIN's at W = 10·H and W = H/4: the top of the dispersion fit's 0.1 ≤ W/H ≤ 10, and as narrow as a laminate is commonly etched. On 20 mil RO4350B with 35 µm copper that is about 16 Ω and 117 Ω at 2.4 GHz. | `line` |
| Open-stub lowpass, Richards and Kuroda | Richards' transform ([10.1109/JRPROC.1948.233274](https://doi.org/10.1109/JRPROC.1948.233274)) makes each series L a series short stub of g·R0 and each shunt C a shunt open stub of R0/g, all λ/8 at fc. Kuroda's identity, [UE Z1][series Z2] ≡ [shunt Z1(Z1 + Z2)/Z2][UE Z1 + Z2] (as Ozaki and Ishii work it, [10.1109/TCT.1958.1086441](https://doi.org/10.1109/TCT.1958.1086441)), turns a series stub with a unit element beside it into a shunt stub. Read backwards, it moves a unit element through a shunt stub and leaves a series stub behind, which the next pass turns. Each series stub is worked from the nearer port, and a unit element of R0 is added there when none is left; on a matched port it changes only the phase. Every step is exact in Richards' variable. Both the series-first and the shunt-first prototype are worked, and the one with the smaller impedance spread is kept: series-first for N = 3 and 7, shunt-first for 5, each then symmetric. | open `stub`, λ/8 `line` |
| Stub lowpass with zeros | The series-first prototype's L's are Zhigh lines as above. Each shunt C is an open stub λ/4 at its zero f∞, whose admittance at fc is the capacitor's: tan(π·fc/2f∞)/Zs = g/R0 (Pozar §8.6). The zeros are the spec's `TransmissionZerosHz`, one for every stub or one per stub, each above fc. It needs no elliptic prototype. | `line`, open `stub` |
| Shorted-stub bandpass | Matthaei 1960 ([10.1109/TMTT.1960.1124801](https://doi.org/10.1109/TMTT.1960.1124801)), in Hong and Lancaster's form ([10.1002/0471221619](https://doi.org/10.1002/0471221619), §5.3). Shorted stubs λ/4 at f0 are joined by λ/4 lines of admittance J(i,i+1)·Y0, with θ = (π/2)(1 − Δ/2) and h = 2. The inverters are J12 = √(h·g1/g2), J(n−1,n) = √(h·g1·g(n+1)/g(n−1)), and h·g1/√(gi·gi+1) between; N(i,i+1) = √(J² + (h·g1·tan θ/2)²). The stubs are Y1 = N12 − J12, Yn = (gn·gn+1 − g1)·tan θ + N(n−1,n) − J(n−1,n), and Yi = N(i−1,i) + N(i,i+1) − J(i−1,i) − J(i,i+1). On ideal lines this lands a 0.1 dB Chebyshev band's edges within 0.3 % at N = 5 over Δ = 0.4. N ≥ 2. | shorted `stub`, `line` |
| Quarter-wave stub bandstop | Open stubs λ/4 at f0 of Zk = 4·R0/(π·gk·Δ), joined by λ/4 lines of R0 (Pozar §8.8, a book, no DOI). | open `stub`, `line` |
| End-coupled bandpass | Cohn 1957 ([10.1109/JRPROC.1957.278389](https://doi.org/10.1109/JRPROC.1957.278389)). The inverters are Z0·J01 = √(πΔ/2g0g1), Z0·Jk = πΔ/(2√(gk·gk+1)) and Z0·J(N,N+1) = √(πΔ/2gN·gN+1). Each is a series susceptance b = Z0J/(1 − (Z0J)²), a gap whose MGAP Cg is b/(ω0·R0); its S comes from bisection on log S over MGAP's own closed form (`MicrostripGap`). Resonator k is π − ½[atan 2bk + atan 2b(k+1)] long, less atan(ω0·Cp·Z) for each end's Cp. All lines are R0 wide. | `gap`, `line` |

**Refusals,** each coded `synthesis.ladder.*`:
- `unsupported`: the spec is not the realiser's band, prototype or order, or it asks for zeros the realiser does not
  place, or none of a realiser that does. The reason is `Supports`' sentence.
- `no-substrate`: a ladder needs a substrate.
- `width-unreachable`: an impedance no width gives. This wraps the ladder's `device.ladder.width-unreachable`. A
  narrow-band stub filter asks for it: a 2 % bandstop's stubs are over 1 kΩ.
- `impedance-order`: Zlow not under Z0, or Zhigh not over it.
- `zero-count`, `zero-in-band`: the zeros are not one or one per stub, or one is not above fc.
- `band-too-wide`: an end-coupled inverter of Z0·J ≥ 1, or a resonator whose gaps load it past a half wave.
- `gap-unreachable`: a coupling needs more Cg than the narrowest gap gives. The narrowest gap is the metal's thickness,
  or a micron at least. The message gives the Cg needed and the Cg that gap reaches; on 20 mil RO4350B a 10 % band
  needs about 0.6 pF.

**What the tests hold** (`tests/Engine.Tests/Synthesis/LadderRealiserTests.cs`). Each realisation is placed as it
stores its parameters, its list read back unchanged, and run through the engine on 20 mil RO4350B unless stated:
- the stepped-impedance lowpass, N = 6 Butterworth at 2.5 GHz on the default impedances: −3 dB at fc, −4.7 % (within
  5 %);
- the Kuroda lowpass at 2.4 GHz: N = 3 at −2.1 % and N = 5 at −3.2 % (within 5 %). N = 5's end stubs are 181 Ω, a
  few microns wide, and its note says so;
- the stub lowpass with a zero at 1.3·fc: the zero at +3.2 % (within 5 %), under −30 dB, the passband at fc/2 above
  −1 dB;
- the shorted-stub bandpass, N = 3 Butterworth over Δ = 1: the −3 dB band's centre at +0.1 % (within 2 %);
- the bandstop, N = 3 over Δ = 0.5: the notch at +0.6 % (within 1 %), under −40 dB;
- the end-coupled bandpass, N = 3 over 3 % at 10 GHz on 25 mil alumina: the centre at +0.04 % (within 2 %);
- each refusal's code.

The line and stub helpers the ladder's own tests used (`Line`, `Stub`, `Length`, `OpenEnd`) are now
`LadderSynthesis`'s. `LadderSParamTests` keeps the composite, the DC block, the highpass, the matchers and the
transformer.

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
(`synthesis.mcrc.cross-coupling`), except k(1,4) on the cross-coupled square (§4.1).

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

### 4.1 Cross-coupled filters

**What is built.** A four-pole quasi-elliptic bandpass filter, equiripple in the passband with one transmission zero
either side, as one MCRC: open loops (Gap = W) in Hong and Lancaster's square, `Arrangement` = `Cross`, tapped
([10.1109/22.543968](https://doi.org/10.1109/22.543968)). The realiser is "Open-loop bandpass, cross-coupled (MCRC)",
`CrossCoupledLoopRealiser` in `src/Core/Synthesis/Filters/CrossCoupledRealisers.cs`. It takes a Chebyshev spec of order
4, at most 20 % wide, with two zeros at mirror frequencies, f1·f2 = f0²: their normalised frequencies
ω = (f/f0 − f0/f)/FBW must lie either side of the band and agree in size to 5 %, and their mean is used.

**The matrix.** The return loss comes from the ripple, RL = −10·log10(1 − 10^(−Ap/10)), and §2 gives the folded 4–2
matrix at RL with zeros at ±ω_z, then its physical terms. With mirror zeros the folded matrix holds M12 = M34, M23 and
M14 and nothing else: no self-coupling, M13 = M24 = 0. Its loop product M12·M23·M34·M14 is negative, the cross coupling
against the main path, which is what puts both zeros at real frequencies. The square draws exactly that:

- loops 1 and 4 face across their openings, an electric coupling, across `Sx`;
- loops 2 and 3 face back to back, a magnetic coupling of the opposite sign, across S3;
- 1-2 and 3-4 face through sides next to their openings, a mixed coupling, across S2 and S4.

So the sign of k14 against k23 is the square's geometry: only |k| is realised.

**The geometry.** `CoupledResonatorDesign.FromCouplings` with `Arrangement` = `Cross` runs §4's four steps, with three
differences:

1. **k12 and k34** are solved on the inline pair. The square's 1-2 pair is that pair turned a quarter turn: two loops
   opened on opposite sides, facing through the sides next to their openings.
2. **k23 and k14** are solved in the square itself. Its other two loops are shortened to 0.6·l, which puts them near
   1.7·f0, outside the window the peaks are searched in, and its other gaps are 5·h. So the two peaks left are the
   pair's own split, and their mean is the pair's own pull on each loop. For k23 the signal reaches loops 2 and 3
   through the detuned end loops. The square is lossless and symmetric, so each of the pair's modes still peaks at
   0 dB, narrow, and golden section finds it.
3. **The tap** runs from the middle of the outer side, round the corner and along the top side. In the square both of
   those sides are free, so the range is the odd row's.

Each loop is then sized for f0 where it sits, each coupled side by its own pair's mean: two sides for every loop, and the
tap's pull as well for loops 1 and 4.

**How close it lands.** On 20 mil RO4350B, lossless, at 2.4 GHz, with 0.5 dB ripple, FBW = 5 % and zeros at ω = ±2
(2.2830 and 2.5230 GHz), the realised part written as a netlist line and run through the engine gives:

| | Asked | Realised | Off |
|---|---|---|---|
| Lower zero | 2.2830 GHz | 2.2710 GHz | −0.52 % |
| Upper zero | 2.5230 GHz | 2.5025 GHz | −0.81 % |
| Centre | 2.4000 GHz | 2.4062 GHz | +0.26 % |
| Ripple bandwidth | 120.0 MHz | 113.6 MHz | −5.4 % |

The matrix asks for k12 = k34 = 0.0347, k23 = 0.0322, k14 = 0.0043 and Qe = 33.7, which give S2 = S4 = 0.83 mm,
S3 = 1.32 mm, Sx = 1.80 mm and Tap = 2.88 mm, with the end loops 40.30 mm and the middle ones 39.04 mm. The design
takes about 15 s, most of it the square's own pairs, each solve a four-loop model.

**Limits.**

- **The circuit model leaves things out.** The square's diagonals (1-3 and 2-4) and the corners' coupling are not in
  the model (rf-passives.md §3.15). An EM run of the artwork shows them, and they move the zeros.
- **The 1-2 pair is measured aligned.** In the square, loops 1 and 2 sit (Sx − S3)/2 apart sideways and differ in size
  by a few per cent. Their facing sides overlap a little less than the aligned pair's do.
- **Only mirror zeros.** Unmirrored zeros need self-couplings and M24, which the square cannot draw.
- **No trisection.** A three-pole trisection needs three loops coupled in a triangle, 1-3 included, and no MCRC
  arrangement draws one. It is listed in §7.

**Refusals.** `synthesis.xc.*`: `band` (not a bandpass), `response` (not Chebyshev), `order` (not 4), `bandwidth`
(outside (0, 20 %]), `zeros` (not two, not one either side, or not mirrored to 5 %), and `substrate` (none given).
`Supports` gives the same sentence. A coupling or Qe the model cannot reach is refused by the solve
(`synthesis.mcrc.out-of-reach`).

**Tests.** `tests/Engine.Tests/Synthesis/CrossCoupledRealiserTests.cs` covers three things:

- **The realised part.** It is found through `FilterRealisers.All`, written as an `MCRC` netlist line and run through
  the engine. Its zeros fall within 3 % of those asked for, its centre within 2 % and its ripple bandwidth within 20 %.
- **The matrix.** The folded matrix of mirror zeros holds only the square's couplings, with a negative loop product.
- **The refusals.** Each refusal gives its code.

`CouplingMatrixTests` gains the 4–2 mirror case: the matrix's response equals its polynomials to 1e-9.
`CoupledResonatorRealiserTests` shares its board and band helpers, and its inline forms land where they did.

## 5. Coupled arrays on MCLA

(to be written)

## 6. The Filter Designer, the `filter` verb and the MCP tool

**One seam, three entry points.** The window, `circuitrf filter` and the MCP `create` tool's `filter` mode all go
through `FilterSynthesisService` (`src/Design/Filters/`). Each one asks every realiser in `FilterRealisers.All` whether
it `Supports` the specification, and calls `Realise` on the one chosen. Nothing in the service designs anything. It
does four things:
- **`Substrate`** resolves the technology's microstrip between the chosen signal and ground layers through
  `SubstrateResolver`, as a run does. It is null with the reason when the stack has none.
- **`Part`** makes the realised part as a palette drop would, then sets each row the realisation states. A value with
  a spaced unit (`50 Ohm`) is split into the expression and the unit, as `Units.SplitTrailingUnit` splits an inline
  unit. A glued unit (`1.2mm`) or a word stays the expression, with no unit.
- **`Cnl`** writes the test bench: two ports of Z0, the part with pins 1 and 2 on them and every other pin on a net of
  its own name, one ordinary part per site, and an S-parameter sweep across the band.
  - Each site part is an R, L or C of the site's value, with the part's `Footprint`, else the R/C default on the
    technology (`FootprintDefaults.For`). A via site gets a VIAGND.
  - The part's line states the substrate (`H`, `T`, `Er`, `Sigma`, `TanD`), so the netlist runs the same in any folder,
    and it is the substrate the realiser designed on. `CnlTechnologyBinding` leaves a line that states every substrate
    parameter as it is.
  - The pin order is the symbol's own (`SymbolPortDefs.For` on the part), so a part whose pins follow its parameters
    (MLAD's G pins, MCLA's E pins) is wired the way a schematic would wire it.
- **`Ideal`** gives the response the realisation is judged against: the doubly terminated prototype from
  `LowpassPrototype.GValues`, series element first, transformed to the band by the textbook frequency transformations
  (Matthaei, Young and Jones), as L and C parts run by the engine.
  - Port 2 is the prototype's own load, so an even-order Chebyshev shows its ripple and not a mismatch.
  - The ideal `Filter` block was the other choice. It has no bandstop form, and it is not the prototype the realisers
    start from. The ladder covers all four bands, and it covers every response `GValues` learns as the realisers add
    them.

**The verb** (`src/Cli/FilterVerb.cs`) takes the specification as flags.
- `--tech` names a `.ctech`, a folder in a workspace, or a shipped id. Without it, the global `--workspace` is used,
  then the workspace the output is written in.
- Without `--realiser` it lists every realiser with `yes` or `no` and the reason. With `--json`, each realiser is a
  `filter.realiser.listed` diagnostic carrying `name`, `supports` and `why`, so no new member was needed in the result
  envelope.
- With `--realiser` it writes the bench to `-o`. `--to-schematic` draws that bench through `NetlistToSchematic`, the
  path `netlist --to-schematic` takes.
- Its refusals are coded `filter.*`. An unknown flag is `filter.args.unknown-option`, which the MCP gate that probes
  every advertised argument reads.
- An internal overload takes the realiser list, so a test can hand the verb a realiser of its own. `FilterRealisers.All`
  finds types in Core's assembly only.

**The MCP tool** is a mode of `create`, not a new tool: what it makes is a document, and every tool on the surface is
a standing cost to a client. Its arguments are named after the flags. The one exception is `technology` for `--tech`,
because `create workspace` already advertises a `tech` that means something else, and the schema is the union across
modes.

**The window** (`src/Ui/Filters/FilterDesignerViewModel.cs`, `src/Ui/Views/Filters/`) is laid out as the Match
Designer is.
- On the left: the specification, the family filter, the substrate it found, then the realisers. Under each one it
  shows whether it can build the filter and, if not, why. Below them, "What Place writes": the parameters and site
  values.
- On the right: one Data Display plot, as the Match Designer hosts its own. It shows |S21| and |S11| of the ideal ladder
  and of the selected realisation's bench, both run by the engine on every edit.
- **Synthesise…** in the Properties of an MLAD, MCRC or MCLA opens it with that family preselected. A family is matched
  against the realiser's name, which by the contract's convention ends in the part type, e.g. "(MCRC)".
- **Place** puts the part to the right of the drawing. It reads the part's sites the way Place Site Parts reads them,
  from the part's own plan (`PassiveSites.Read`, with the part in the schematic for that one read), and fills them with
  `PassiveSites.PlaceParts`. Part, site parts and wires go in one `PlaceSitePartsCommand`, so one Undo takes them all off.

**Tests** (`tests/Ui.Tests/Synthesis/FilterDesignerTests.cs`) use a realiser of their own: a lumped Butterworth or
Chebyshev lowpass written as MLAD sites.
- The verb lists it, and says why it refuses a highpass.
- Realised on the shipped RO4350B board, the verb writes a netlist that states the 0.508 mm substrate and the board's
  default land, and draws it. The netlist's −3 dB point is fc within 3 %.
- The ideal ladder loses exactly the ripple at each passband edge, in all four bands and for an even order too.
- The `create` tool advertises the mode and its arguments. One call through the adapter becomes the verb's command line
  and writes the netlist.
- The Designer preselects the family and lists the realiser, plots four traces, and refuses Place on a band the realiser
  cannot build. Its Place adds the ladder, two inductors and a capacitor, and one Undo takes them all off.

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

7. **The coupling matrix in extended precision, or from an eigenproblem.** The residues lose digits where two poles lie
   close together. Two ways would lift the 1e-8 to 3e-7 floor (§2) back to 1e-13 and let `MaxOrder` rise past 10:
   double-double arithmetic in `Transversal`, or the poles and residues as the eigenvalues and eigenvectors of a
   well-conditioned matrix. Why: narrow filters with zeros near the band edge, the case that most needs the matrix,
   are the ones that cluster their poles.
8. **Arrow, CT, CQ and box forms.** Each is further rotations of the folded or arrow matrix, as Cameron 2003 sets out.
   Why: the folded form's cross-diagonal couplings are hard to lay out in microstrip past N = 4. Trisections and
   quartets put each zero in a local group that an MCRC arrangement can draw.
9. **Lossy and predistorted synthesis.** Why: with a finite Qu, the response today only shows the loss and its rounded
   edges. A predistorted matrix would restore the flat passband on a die or a lossy laminate, at a known cost in
   insertion loss.
10. **Complex (group-delay) zeros.** The recursion takes complex ω_n in symmetric pairs unchanged. Only the validation
   and `FilterSpec`'s real zero list hold them back. Why: self-equalised filters flatten the group delay without an
   external equaliser.

- **Ladders: the optimum distributed lowpass** (Levy's tables, [10.1109/TMTT.1965.1126048](https://doi.org/10.1109/TMTT.1965.1126048);
  Horton and Wenzel, [10.1109/TMTT.1965.1125996](https://doi.org/10.1109/TMTT.1965.1125996)). Why: Kuroda's redundant
  unit elements push the end stubs of N ≥ 5 to 180 Ω and more, which no laminate etches. Unit elements that are part
  of the response keep every impedance near Z0 and sharpen the skirt as well.
- **Ladders: Schiffman and Matthaei's exact bandstop** ([10.1109/TMTT.1964.1125744](https://doi.org/10.1109/TMTT.1964.1125744)).
  Why: the narrow-band form widens the stopband as Δ grows. The exact one holds the band edges for wide stopbands, and
  takes a choice of connecting-line impedance that can bring the stubs into reach.
- **Ladders: a process's minimum width and gap.** Why: the realisable range is H/4 to 10·H and the narrowest gap is the
  metal's thickness. Those are guesses that suit laminates; on a die the process's own rules are the real limits, and
  a refusal against them would name the rule.
- **Ladders: `h` as a knob on the shorted-stub bandpass.** Why: h scales the interior stubs' admittance. A smaller h
  narrows wide stubs, and lets a narrower band fit on the board before `width-unreachable`.
- **Ladders: junction compensation.** Why: a wide cross shifts a shorted-stub bandpass's centre by about 3 % at Δ = 0.5
  with 0.1 dB ripple, more than the closed form's own error. Shortening each stub by the cross's reference-plane
  reactance at f0 would keep the centre within a percent without the optimizer.

## 8. References


- R. J. Cameron, "General coupling matrix synthesis methods for Chebyshev filtering functions", IEEE T-MTT 47(4),
  1999, pp. 433–442: [10.1109/22.754877](https://doi.org/10.1109/22.754877). The recursion, E from F and P, and the
  coupling matrix from the admittance residues.
- R. J. Cameron, "Advanced coupling matrix synthesis techniques for microwave filters", IEEE T-MTT 51(1), 2003,
  pp. 1–10: [10.1109/TMTT.2002.806937](https://doi.org/10.1109/TMTT.2002.806937). The N+2 transversal matrix and its
  folding sequence.
- R. J. Cameron, "Advanced filter synthesis", IEEE Microwave Magazine 12(6), 2011, pp. 42–61:
  [10.1109/MMM.2011.942007](https://doi.org/10.1109/MMM.2011.942007). Table 1 and Fig. 21, the 4–2 test values.
- R. J. Cameron, C. M. Kudsia and R. R. Mansour, *Microwave Filters for Communication Systems*, 2nd ed., Wiley, 2018:
  [10.1002/9781119292371](https://doi.org/10.1002/9781119292371). The N+2 response and the lossy shift.
- A. E. Atia and A. E. Williams, "Narrow-bandpass waveguide filters", IEEE T-MTT 20(4), 1972, pp. 258–265:
  [10.1109/TMTT.1972.1127732](https://doi.org/10.1109/TMTT.1972.1127732). The coupling matrix of an all-pole filter.
- J.-S. Hong and M. J. Lancaster, *Microstrip Filters for RF/Microwave Applications*, Wiley, 2001, §8.1:
  [10.1002/0471221619](https://doi.org/10.1002/0471221619). Qe, k and asynchronous tuning.
- G. L. Matthaei, L. Young and E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling
  Structures*, McGraw-Hill, 1964: §4.04 (the transforms), §4.05 (Butterworth and Chebyshev), table 4.07-1 (maximally
  flat delay). A book, no DOI: [Open Library](https://openlibrary.org/works/OL153218W).
- R. Saal and E. Ulbrich, "On the design of filters by synthesis", *IRE Trans. Circuit Theory*, 1958,
  [10.1109/TCT.1958.1086481](https://doi.org/10.1109/TCT.1958.1086481).
- P. Amstutz, "Elliptic approximation and elliptic filter design on small computers", *IEEE Trans. Circuits Syst.*,
  1978, [10.1109/TCS.1978.1084429](https://doi.org/10.1109/TCS.1978.1084429).

- P. I. Richards, "Resistor-transmission-line circuits", Proc. IRE, 1948. [10.1109/JRPROC.1948.233274](https://doi.org/10.1109/JRPROC.1948.233274)
- H. Ozaki and J. Ishii, "Synthesis of a class of strip-line filters", IRE Trans. Circuit Theory, 1958. [10.1109/TCT.1958.1086441](https://doi.org/10.1109/TCT.1958.1086441)
- G. L. Matthaei, "Design of wide-band (and narrow-band) band-pass microwave filters on the insertion loss basis", IRE Trans. MTT, 1960. [10.1109/TMTT.1960.1124801](https://doi.org/10.1109/TMTT.1960.1124801)
- S. B. Cohn, "Direct-coupled-resonator filters", Proc. IRE, 1957. [10.1109/JRPROC.1957.278389](https://doi.org/10.1109/JRPROC.1957.278389)
- J.-S. Hong and M. J. Lancaster, *Microstrip Filters for RF/Microwave Applications*, Wiley, 2001. [10.1002/0471221619](https://doi.org/10.1002/0471221619)
- B. M. Schiffman and G. L. Matthaei, "Exact design of band-stop microwave filters", IEEE Trans. MTT, 1964. [10.1109/TMTT.1964.1125744](https://doi.org/10.1109/TMTT.1964.1125744)
- R. Levy, "Tables of element values for the distributed low-pass prototype filter", IEEE Trans. MTT, 1965. [10.1109/TMTT.1965.1126048](https://doi.org/10.1109/TMTT.1965.1126048)
- M. C. Horton and R. J. Wenzel, "General theory and design of optimum quarter-wave TEM filters", IEEE Trans. MTT, 1965. [10.1109/TMTT.1965.1125996](https://doi.org/10.1109/TMTT.1965.1125996)
- D. M. Pozar, *Microwave Engineering*, 4th ed., Wiley, 2012, §8.6 and §8.8: a book, no DOI. [Wiley](https://www.wiley.com/en-us/Microwave+Engineering%2C+4th+Edition-p-9780470631553)
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
- **A trisection on MCRC** (§4.1). An arrangement of three open loops in a triangle, 1-3 coupled, after Hong and Lancaster's
  trisection ([10.1049/ip-map:19990146](https://doi.org/10.1049/ip-map:19990146)), with the matrix's CT form (item 8)
  giving its couplings. Why: one zero on the side that needs it, at three poles rather than four. Two in a row place two
  zeros anywhere, which the square, holding mirror zeros only, cannot.
- **A square with k14 of the same sign as k23** (§4.1). Loops 1 and 4 opened away from each other, so that they couple
  magnetically, as 2 and 3 do. Why: complex zeros (item 10), which flatten the group delay, need the cross coupling in
  phase with the main path, and today's square only draws it against.

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
- **Tools ▸ Filter Designer, on the active schematic.** Today the window opens only from a placed MLAD, MCRC or MCLA, so
  you have to place a part before you can synthesise one.
- **Apply to the selected part.** Writing the realisation into the part that opened the Designer, as one undo step,
  would keep its wiring and its place, where Place adds a second filter beside it.
- **Signal and ground layer pickers in the window.** The verb takes `--signal` and `--ground`, but the window uses the
  technology's default layers, so a filter on an inner layer of a board, or on a die's second metal, can only be
  designed headless.
- **A family on `IFilterRealiser`.** The family filter matches the part type at the end of the realiser's name. A
  property would not depend on how a name is written.
- **Markers on the Designer's plot.** The plot is a bare PlotControl with no info-box layer, so a marker shows no
  readout. Hosting it as the Signal Designer hosts its plots would let you read a band edge off the curve.
- **A `result.filter` object in the JSON envelope.** The list rides on `filter.realiser.listed` diagnostics. An object
  with the realisation's parameters and site values would let an agent read what was written without parsing the `.cnl`.

## 8. References

- G. L. Matthaei, L. Young and E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling Structures*,
  McGraw-Hill, 1964: the doubly terminated lowpass prototype, its terminations and its frequency transformations to the
  highpass, bandpass and bandstop forms; a book, no DOI: [the book at the Internet Archive](https://archive.org/details/microwavefilters0000matt).
- J.-S. Hong and M. J. Lancaster, "Microstrip cross-coupled trisection bandpass filters with asymmetric frequency
  characteristics," IEE Proc. Microwaves, Antennas and Propagation 146(1), 1999:
  [10.1049/ip-map:19990146](https://doi.org/10.1049/ip-map:19990146) (§7).
