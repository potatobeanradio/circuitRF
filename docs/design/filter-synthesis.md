# circuitRF — Filter synthesis: from a specification to a placed filter

**Status:** in progress · **Date:** 2026-11-28

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
a trap. `LowpassPrototype.GValues` still gives the g-values alone, for the responses that have no companions. It is
also the one home of the handbook forms (§6e): `ButterworthG`, and `TableChebyshevG`, the 17.37 recurrence the LC Filter
Designer and the Match Designer use.

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

Two realisers in `src/Core/Synthesis/Filters/CoupledArraySynthesis.cs` fill MCLA's stored parameters (rf-passives.md
§3.17) from a bandpass specification on an all-pole prototype (Butterworth or Chebyshev):
`CoupledArrayInterdigitalRealiser` and `CoupledArrayComblineRealiser`. Both follow one path:

1. **Admittances.** The g-values give each strip's admittance to ground and each adjacent pair's mutual admittance,
   per unit length, in units of the terminations' Y_A = 1/Z0.
2. **The fit.** The widths and gaps whose solved cross-section has those admittances.
3. **Length and loading.** L, and for the combline each C and the tap.

The closed forms are `CoupledArraySynthesis`'s public functions. They were the coupled-array tests' own helpers, and
those tests now call them.

**Interdigital** (Matthaei, [10.1109/TMTT.1962.1125556](https://doi.org/10.1109/TMTT.1962.1125556), Table I: the
procedure for narrow and moderate bands).

- **Feed and strips.** The coupled feed, N = n + 2: strips 1 and N are the terminating lines.
- **Design equations.** θ1 = (π/2)(1 − w/2), with w the band over its arithmetic centre. The inverters are
  J = 1/√(g_k·g_(k+1)), and h (`H`, default 0.15) is the resonators' admittance scale.
- **Length.** Every strip is a quarter wave at the arithmetic centre on the modes' mean εeff, less the middle strip's
  open-end extension.
- **Sites.** Each shorted end is a via site.
- **Range.** Matthaei's procedure holds to an octave, so a band wider than two thirds of its centre is refused.

**Combline** (Matthaei, *Microwave J.*, vol. 6, Aug. 1963, no DOI). The feed is tapped, and every strip is a resonator.

- **Choices.** θ0 (`ThetaDeg`) defaults to the textbook's 45°. Each resonator's admittance with its neighbours grounded
  (`ResonatorAdmittance`) defaults to 1/Z0.
- **Design equations.** The slope parameter is b = Y_a·(cot θ0 + θ0·csc²θ0)/2, each inverter is
  J = w·b/√(g_k·g_(k+1)), and each mutual is Y_m = J·tan θ0. Each strip's admittance to ground is what is left of Y_a.
- **The fit reads each pair's mutual as its susceptance at the centre.** That is B_(k,k+1)·tan θ0 of the array shorted
  at its near ends, B = −L⁻¹·Tv·diag(cot(ωℓ/v_m)/v_m)·Tv⁻¹, each mode at its own speed (`ShortedSusceptance`). In a
  homogeneous medium that is Y_m exactly.
  - A combline pair's coupling is the small difference of its electric and magnetic parts. Microstrip's unequal mode
    speeds add to it.
  - Matched on the characteristic admittance alone, the board's filter came out twice as wide as asked.
- **Length.** Every strip is θ0 long at the centre on the middle strip's own line, less its open-end extension.
- **Loading.** Each C tunes its strip to the centre with every other loaded end shorted, the coupled-resonator
  definition (`ComblineLoading`). It is the array's own diagonal susceptance less the open end's fringing capacitance,
  which the part models: the textbook's C = Y·cot θ0/ω0 on the real cross-section.
- **The tap.** The tap gives the prototype's external Q, Qe = g0·g1/w. The port's conductance seen at the loaded end is
  Y0·sin²θt/sin²θ, so sin²θt = b·sin²θ/(Y0·Qe), with b from the same susceptance's slope.
- **The end resonators.** The port's conductance at the tap pulls the end resonator up, 0.7 % at Qe = 10. The end
  strips' C gives that back: the loaded end's susceptance with and without the port, on the strip's own line.

**The fit** (`Fit`).

- **The matrix.** It matches the lossless characteristic admittance matrix
  Yc = L⁻¹·Tv·diag(1/v_m)·Tv⁻¹, with L⁻¹ = c²·C₀ (Paul, [10.1109/9780470547212](https://doi.org/10.1109/9780470547212)).
  - The tests' former helper multiplied L⁻¹ on the other side. That is the same matrix only in a homogeneous medium.
  - On microstrip it is not symmetric, so a symmetric layout could not meet a symmetric design: the walk stalled 4 % off.
- **What each unknown follows.** A width follows its strip's diagonal (ground plus the adjacent mutuals), which a gap
  barely moves. A gap follows its pair's mutual.
- **The walk.** A secant in the logarithms, one slope per unknown. It starts from strips 2H wide and 0.6H apart and keeps
  the layout mirror-symmetric, as the design is.
- **Cost.** It settles to 0.3 % in 6 to 10 field solves, each about half a second.
- **Refusal.** It is refused when a width or a gap leaves 0.001 to 100 substrate heights, or when 30 solves do not
  settle it.

**What the realisation holds.**

- **Parameters.** As the part stores them: `N`, `W[k]`, `S[k]`, `L`, `Form` and `Feed`. The combline adds `Tap`, `Wf`
  (the width of a Z0 line, `LadderPlan.Width`) and `C[k]`.
- **Units.** Lengths are in mm and capacitances in pF, as `LadderPlan.Format` writes them (`0.983213mm`, `1.6404pF`).
- **Sites.** The plan's own (`CoupledArrayPlan.Build`): a `Via` on each shorted end, and a `C` of its value on each
  loaded one.

**Accuracy.** `CoupledArraySynthesisTests` writes each realisation as the part's netlist line, puts its site parts on
its E pins, and runs it lossless through the engine. Every case is 0.1 dB Chebyshev with three resonators.

| Realiser | Stack | Asked | 3 dB centre | 3 dB band / prototype's | Ripple |
|---|---|---|---|---|---|
| Interdigital | 20 mil RO4350B | 2.16–2.64 GHz | −0.9 % | 0.99 | 0.60 dB |
| Interdigital | 100 µm GaAs | 9–11 GHz | −1.5 % | 0.99 | 0.36 dB |
| Combline | 20 mil RO4350B | 1.9–2.1 GHz | −1.1 % | 0.99 | 0.45 dB |
| Combline | 100 µm GaAs | 9.5–10.5 GHz | −1.2 % | 0.98 | 0.53 dB |

- **Tolerances.** The tests hold the centre to 3 % and the band to 20 %.
- **Ripple.** It is above the 0.1 dB asked. The design is TEM, and the quasi-static N-line model keeps each microstrip
  mode at its own speed.
- **The combline's resonators.** Each resonator alone, every other loaded end grounded, resonates within 0.2 % of the
  centre (the test allows 1 %). Its port's reflection is slowest there.
- **The combline's band** still sits about 1 % low. A combline pair's coupling grows below the centre, as cot θ does, so
  its skirts are not symmetric.

**Refusals** (`synthesis.mcla.*`): `band` (not a bandpass), `zeros` (transmission zeros asked), `edges`, `prototype` (a
response with no ladder g-values), `band-too-wide` (the interdigital past an octave; a combline tap past the open end),
`order` (a combline of one resonator), `theta`, `scale` (a design that leaves a strip no admittance to ground),
`substrate` (none given) and `unrealisable` (the fit). All but `unrealisable` come before any field solve.

**The pseudo-interdigital form** is not realised. Its hairpins are half-wave resonators folded into interleaved groups,
so its couplings need relations of their own, not the quarter-wave forms above (§7).

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
  shows whether it can build the filter and, if not, why: first its own check (`Supports`), then what realising it found
  (below). Below them, "What Place writes": the parameters and site values.
- On the right: one Data Display plot, as the Match Designer hosts its own. It shows |S21| and |S11| of the ideal ladder
  and of the selected realisation's bench, both run by the engine on every edit.
- **The ideal at Qu**, for a bandpass on a substrate: |S21| of the same prototype with every resonator at the board's
  unloaded Q, through `CouplingMatrix.Response` with δ = 1/(Qu·FBW) on the inline matrix `CouplingMatrix.AllPole` builds
  from the g-values (`FilterSynthesisService.LossyIdeal`). With no loss it is the ideal ladder's own response.
  - Qu is `CoupledResonatorDesign.UnloadedQ`: β/(2α) of a Z0 line on the substrate at f0, with α MLIN's own conductor
    (Wheeler, with roughness) and dielectric loss. Radiation and the open ends are left out. On the shipped RO4350B
    board at 2.4 GHz, Qu is about 110.
  - A line above the plot reads the loss out: Qu, the ideal at Qu's loss at f0, and the realised bench's loss at f0 with
    the part of it that is dissipated, −10·log(|S21|²/(1 − |S11|²)). On the 5-pole 0.1 dB, 10 % bandpass, the tapped
    hairpin's loss of about 3 dB is nearly all dissipation and close to the ideal at Qu's: the board's loss, not a
    design error. A toggle hides the curve.
  - A test checks the ideal at Qu against the first-order estimate 4.343·Σg/(FBW·Qu) dB at f0, as Hong and Lancaster
    give it ([10.1002/0471221619](https://doi.org/10.1002/0471221619)). They agree to 0.001 dB at Qu = 200.
- **The metrics**, under the plot: the selected realisation's bench read back to its prototype (§6c), in the same
  background task as the bench, by `FilterSynthesisService.Extract` against `Target(spec)` (the all-pole chain, or with
  zeros the folded generalised Chebyshev). Two compact tables, each row a `FilterCheck` with its value, its target and
  pass or fail:
  - the spec's: centre, bandwidth, ripple, return loss and each zero, then the insertion loss at f0 and the unloaded Q
    beside the board's, which the spec asks nothing of;
  - the matrix's: each Qe, k and resonator frequency against the synthesis's (`ToPhysical`'s terms).

  A line above says whose response it is and how many checks fail. A response the extraction refuses (not a bandpass,
  too few points) shows the refusal instead. The extraction is milliseconds beside the bench.
- **Run Planar EM** runs `FilterEmService` (§6d) on the selected realisation on a worker, at `EmPoints` = 31 frequencies
  across the extraction's window, ω = ±3 (`EmFrequencies`; a band other than a bandpass across the plot's sweep). Every
  point then counts towards the fit, which needs 2·(2N + 1) of them, so 31 reach order 7. A bar follows the solved points
  and **Cancel** stops the solve at its next point or stage.
  - The EM |S21| and |S11| join the plot, and the metrics switch to the EM response. **From Planar EM** switches them
    back to the bench's.
  - A run is kept for the process, keyed by the part's stored values: a `ConditionalWeakTable` on the realisation's
    `Parameters` dictionary, which a polish replaces and nothing else shares. Selecting the realisation again shows it
    at once.
  - The button is greyed, its tooltip giving the reason, where `FilterEmService.Refusal` refuses: the bench's background
    task runs the service's own checks before anything is meshed (the drawing, its pins, each site's port). With no
    substrate the tooltip says so.
- **Polish** runs `FilterPolish.Run` (§6c, three passes on the bench) on a worker. It is enabled where
  `FilterPolish.Refusal` passes: hairpins and open loops in a row.
  - The polished part replaces the realiser's entry in the kept realisations. The plot, the metrics, What Place writes
    and Place all take it, and selecting the realiser again gives it.
  - The status line gives the worst return loss across the band before and after.
- **Calibrate from EM**, once the realisation has an EM run: one polish pass with the EM response in place of the
  bench's (`Run`'s `response`), so the curves are corrected at the EM output, then the corrected part on EM. It starts
  from the `PolishAim` the part was solved for, so it builds on a polish rather than undoing it. One pass per click.
- **Tools ▸ Filter Designer…** (both menu surfaces) opens it on no part: the default specification, every family. Place
  targets the schematic active when it opened, and its technology gives the substrate, else the workspace's default.
  Opened with no schematic, Place asks the workspace at the click (`WorkspaceViewModel.FilterPlaceTarget`): the schematic
  active then, else a new cell `Filter1` (`CellCreate.Create`, as New Cell writes it), opened. A new workspace opens no
  schematic, so a Designer that greyed Place there could simulate a filter but never keep it. With no workspace either,
  Place says there is nothing to place into. **Filter Designer…** on a schematic's empty-canvas menu
  does the same for that schematic. The app has no command palette to add it to.
- **Synthesise…** in the Properties of an MLAD, MCRC or MCLA opens it with that family preselected. A family is matched
  against the realiser's name, which by the contract's convention ends in the part type, e.g. "(MCRC)".
- **Place** puts the part to the right of the drawing, or at the origin of an empty one. It reads the part's sites the
  way Place Site Parts reads them, from the part's own plan (`PassiveSites.Read`, with the part in the schematic for that
  one read), and fills them with `PassiveSites.PlaceParts`. Part, site parts and wires go in one `PlaceSitePartsCommand`,
  so one Undo takes them all off.

**Speed.** Measured by `TheDesigner_PathsOnATypicalSpec_AreTimed` on a 5-pole 0.1 dB Chebyshev bandpass, 2.28 to
2.52 GHz, every shipped realiser, the shipped RO4350B board, a Debug build on one shared machine (other builds ran
beside it, so the seconds vary by about 20 % between runs). Before, nothing was on the plot until the realised response
was; after, the ideal is drawn first and the realised response fills in.

| Path | Before | After |
|---|---|---|
| Open, the window's default spec | 5.7 s | Ideal drawn in 0.44 s the first time in a session, of which 0.36 s is on the UI thread: 0.28 s of it is the Data Display's first plot (start-up and JIT), which any plot window pays once. 10 ms after that. Realised in 3.1 s |
| Fill the realiser list (16 `Supports`) | 0.2 ms | 0.4 ms; then each row realised in the background, 26 to 30 s for all 11 |
| Select a realiser | 0.05 to 20.7 s (interdigital 20.7, open loop 4.8, tapped hairpin 3.1) | 0.05 to 0.21 s once its row is checked |
| Edit a field (the ripple) | 9.1 s, the window waiting | Ideal drawn in 8 ms, 1 ms on the UI thread. Realised in 3.9 to 4.8 s, after the 0.25 s pause |
| Combline, realise + bench, cold | 9.2 + 1.7 s | 2.9 to 3.9 + 0.06 s |
| Interdigital, realise + bench, cold | 18.3 + 2.5 s | 8.4 to 9.1 + 0.06 s |
| Open loop / tapped hairpin, realise, cold | 4.8 / 3.1 s | 3.7 to 4.1 / 2.4 to 3.3 s |

Where the time went. `Supports` designs nothing (the list was 0.2 ms), and only the selected realiser was simulated, so
neither was the cause. The ideal is 4 to 18 ms and a bench at most 0.2 s.
- MCLA's cross-section fit is 9 to 11 N-line solves, each two field solves (C and C₀): 1.0 s for 5 strips, 1.6 s for 7.
  The bench then solved the same cross-section again, 1.7 to 2.5 s, because the fit's widths were not on the plan's grid
  and so missed the solver's cache.
- MCRC's k(S) and Qe bisections are 30 halvings each, and every halving is a peak search over 601 frequencies of the
  part's circuit model.
- Nothing was kept: selecting a realiser again realised it again, and every keystroke started a realisation.

What changed.
- **The window** draws the ideal first, on a worker and without waiting for the typing to pause, and says "realising…".
  It then realises and runs the selected realiser alone, and the realised pair fills in when it arrives. Realisations
  are kept for the process by specification, substrate and realiser (a running one is joined, not started twice; cleared
  whole at 256). Every listed realiser its check lets through is then realised in the background, one at a time, and its
  row changes in place to "builds it" or "refuses: why": a check is cheap but not the last word (on this spec the
  coupled-feed and stepped-impedance hairpins pass it and then cannot reach Qe). If the list's own first choice refuses,
  the next is selected. Typed fields wait for 0.25 s without a keystroke before anything is realised; a choice runs at
  once. The bar beside "Realisers" shows after 0.1 s.
- **Cancelling.** `IFilterRealiser.Realise` has an overload with a `CancellationToken`; a realiser that solves nothing at
  length takes the interface's default, which ignores it. MCRC's and MCLA's realisers stop between circuit or field
  solves: each bisection halving and resonator sizing step of `FromCouplings`, each step of the cross-section fit. A new
  specification, or closing the window, cancels the older one's runs. A cancelled run is no result: the cache replaces
  it, and another window that had joined it starts it again under its own token.
- **The N-line solver** (`MicrostripMultiLineModel`) solves C and C₀ side by side. The results are bit-identical.
- **The MCLA fit** steps on the plan's 2 nm grid, so its last solve is the bench's, from the cache. The stored widths and
  gaps move by 1 nm at most.
- **MCRC's bisection** stops once its bracket is under 1 nm (half the plan's grid) and 1e-7 of its range: 21 to 24
  halvings in place of 30. A gap or tap moves by one grid step at most. Each coupled-modes call now has its own validity
  reporter, so two realisers can run side by side (the selected one and the background check).

The realiser, synthesis and device tests pass unchanged at their own tolerances.

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
- Opened with no schematic, it lists every family and realises the filter, and Place is greyed with its reason; given a
  Place target, Place asks it only when clicked and places into what it returns.
- The timing harness above. It asserts that the UI thread's share of each path, and the ideal's drawing, stay under a
  second, and that the checked list is truthful: every row its check let through is realised, and the coupled-feed
  hairpin says "refuses:".
- The ideal at Qu matches the first-order estimate. On the shipped board the Designer draws it with the edge-coupled
  filter, the readout states Qu and both losses, and the toggle hides the curve.
- A realiser that runs until cancelled: the ideal is drawn with "realising…" while it runs, a newer ripple cancels it,
  and the new realisation fills in.
- The metrics, on the tapped hairpin on the shipped board: none while the window opens (under a second on the UI
  thread), then the spec's six rows and the matrix's k and Qe once the bench is shown, with Run Planar EM and Polish
  enabled and Calibrate from EM not.
- Polish replaces the realisation, its note and the status give the worst return loss before and after, and Place puts
  the polished tap down.
- Run Planar EM, cancelled after its first point: "Planar EM cancelled.", no EM result kept, and the button enabled again.
- A lumped ladder with a jumper site: Run Planar EM greyed with the service's refusal, and Polish greyed with its own.

## 6a. Tuning a synthesised filter

**What is built.** `FilterTuning.Lines(spec, realisation, instance, siteParts, siteTolerance)`
(`src/Core/Synthesis/Filters/FilterTuning.cs`) returns the `.cnl` lines that hand a realised filter to circuitRF's own
optimizer: one S-parameter analysis per goal, the `tune` lines, the `goal` lines and one `optimize` line, written by
`TuningDirectiveText`. The `filter` verb appends them to the bench it writes; `siteParts` names the placed site parts in
the order of `FilterRealisation.Sites`.

**What tunes.** The part's stored lengths and gaps, ±20 % around the synthesised values: MCRC's S1 … S(N+1), L1 … LN and
Tap; MCLA's S, S[k], L and Tap. Each named site part's R, L or C, ± its tolerance (5 % unless stated). MCLA's own `C` is
the value its sites want and is not in the model, so its placed capacitors are what tune. MLAD's lengths and widths are
inside its `Sections` text, which no `tune` key reaches, so a ladder tunes through its sites only, and a ladder without
sites is refused.

**The goals**, from the spec's own lowpass prototype (`FilterPrototype`; for a bandpass with transmission zeros, the
generalised Chebyshev of `CouplingMatrix.Characteristic`):

- `Pass`: |S11| at most the prototype's at its passband edge (the ripple level; 3 dB for Butterworth; for an inverse
  Chebyshev, whose Ω = 1 is its stop edge, where its flat passband is `RippleDb` down), over the passband in 20·N + 1
  points: from 0.05 of a lowpass's cutoff, to twice a highpass's, and a bandstop's outer bands out to Ω = 0.5.
- `StopLo`, `StopHi`: |S21| at most −`StopAttenuationDb` at one frequency each side, where the prototype is 6 dB past it.
  The prototype is the one response that meets a ripple goal and a stop goal at its own edges exactly, and a
  distributed filter's skirts are not the prototype's, so goals at those edges would leave nothing to trade.
- `Zero1` …: |S21| at most −`StopAttenuationDb` at each transmission zero.

The optimizer is `auto` with seed 1: CMA-ES finds the region, then Levenberg–Marquardt finishes.

**How far it gets.** The 0.5 dB Chebyshev of order 3 at 2.4 GHz, 10 % wide, edge-coupled on 20 mil RO4350B (§4), its
gaps opened 15 % and its lengths cut 3 %: in 627 evaluations (3 s) both stop goals are met and the ripple band's centre
lands 0.08 % from the spec's. The pass goal is not: |S11| peaks at −5.1 dB against −9.6 dB, which is where the undetuned
design already sits. With one W the chain's image impedances are off (§4), its ripple is about 1.6 dB in the model, and
no gap or length removes that.

**Limits.** The stop goals are points, so an elliptic's stopband past its edges is not held. MLAD's lines and stubs do
not tune (§7).

**Refusals.** `synthesis.opt.*`: `nothing-to-tune` (no stored length or gap and no site part named), `site-parts` (a
count of site parts unlike the sites'), `unreachable` (a prototype that never reaches the stop attenuation).

**Tests.** `tests/Ui.Tests/Synthesis/FilterTuningTests.cs`: the detuned edge-coupled filter, its lines appended to the
bench and run through `OptimizationRun`, meets both stop goals and centres within 1 %; each refusal by its code.
## 6b. Coupling curves on Planar MoM

**What it does.** The hairpin and open-loop realisers (§4) map k to a gap and Qe to a tap on the circuit model. With
`Refine` = `Em` they then measure the same quantities with an EM solver around that answer and read the geometry off the
measured curves. The pure part is `src/Core/Synthesis/Filters/CouplingCurves.cs`; the solver is a seam,
`ICouplingCurveSolver` (a two-pin plan and a sweep in, S-parameters out), so Core stays EM-free. The Design layer's
`CouplingCurvePlanarSolver` (`src/Design/Layout/Em/`) is its Planar MoM: the plan drawn as its PCell draws it, the copper
unioned into one sheet on the substrate's grounded slab, an edge port on each pin, a coarse mesh, no de-embedding. The
ports are grounded, as §6d's are: their source returns through the ground plane, so the outer cell's capacitance is not
in series with them.

**k against S** (Hong and Lancaster, [10.1002/0471221619](https://doi.org/10.1002/0471221619), §8.2). The bench is
§4's: two resonators lightly fed by coupled strips 3·h off, or for the cross square's own pairs (S3, Sx) the square
itself, as §4.1 measures them. It is swept over 21 points, ±15 % about the circuit model's mean resonance. Then k = (f2² − f1²)/(f2² + f1²). A lightly fed resonance is far narrower than that grid's step, so f1
and f2 are not read off the samples: they are the real parts of S21's two poles, from a rational fit over the band
(numerator of degree 3, monic denominator of degree 2, Levy's linearised least squares reweighted by 1/|D| as Sanathanan
and Koerner do, [10.1109/TAC.1963.1105517](https://doi.org/10.1109/TAC.1963.1105517)).

**Qe against the tap.** One resonator, tapped, singly loaded: pin 2's tap is left open, S11' = S11 + S12·S21/(1 − S22).
About the resonance S11' = (1 − j·2Qe·δ)/(1 + j·2Qe·δ) up to a slow phase, whose phase turns ±90° at f0 ± f0/(2·Qe), so
Qe = f0/Δf±90°. With 21 points these are again read off the fit, a one-pole one: Qe = Re f_p/(2·|Im f_p|).

**The curves.** `CouplingCurve` holds measured points and is read backwards by linear interpolation in ln y. Under
`Refine` = `Em`, `FromCouplings` measures k at half, once and twice each circuit gap, and Qe at the circuit tap and a
tenth of its range either side, then reads the gap and the tap off those three points. A target outside them is refused.
The resonator lengths stay the circuit model's, as does a coupled feed's Qe(S1): with its output strip left open, a
coupled feed is no longer a singly loaded resonator.

**Refusals.** `synthesis.em.*`: `no-solver` (`Refine` = `Em` and no solver passed in), `closed-form` (`Em` on Cohn's
edge-coupled or zig-zag chain, which has no k or Qe to measure), `out-of-curve`.

**Tests.** `tests/Core.Tests/Synthesis/CouplingCurveTests.cs`: from synthetic S-parameters on the 21-point grid, a Q =
300 pair's k to 1e-4, and Qe = 15 behind 30 ps of line to 1 %, and the curve read backwards and refused outside.
`Benchmark`, `tests/Ui.Tests/Synthesis/CouplingCurveEmTests.cs`: the tapped hairpin for a 0.1 dB Chebyshev of order 5,
2.2 to 2.6 GHz, on lossless 20 mil RO4350B, on the coarse mesh against the circuit model. One point of each curve:
- k(1,2) at its circuit gap (0.213 mm): 0.101 against 0.133, 24 % low. The ports do not move it (0.101 on the series
  gap as well), as a lightly fed pair's resonances are its own. The test allows 30 %.
- Qe at the circuit tap (6.109 mm): 7.20 against 6.86, 5 % high. On the series gap the tap barely loaded the resonator,
  and Qe read 7957. The test allows 10 %.
- The two sweeps take about 6.5 min.

## 6c. Extraction and polish

**What it does.** Two pieces run the synthesis backwards:
- `FilterExtraction.Extract(spec, f, S11, S21, target, tolerance)` (`src/Core/Synthesis/Filters/FilterExtraction.cs`)
  reads a bandpass filter's prototype back from any two-port's S-parameters: a circuit run, an EM run or a measured
  file. It is a pure function in Core, next to the `CouplingMatrix` it feeds.
- `FilterPolish.Run(spec, realisation, substrate, passes)` (`src/Design/Filters/FilterPolish.cs`) uses it to correct a
  realised MCRC until the matrix its bench extracts to is the synthesised one. It sits beside `FilterSynthesisService`,
  whose bench it runs.

**Off the samples.** These need no model:
- the band, where |S21| is within the ripple of its peak (3 dB for a response without a ripple), each edge interpolated
  linearly in dB; its centre √(f1·f2) and its width f2 − f1;
- the ripple, |S21|'s peak less its lowest across the spec's band;
- the worst return loss across the spec's band;
- the insertion loss at the spec's centre.

**The fit.** The samples within ω = ±3 of the band (`Window`), with ω = (f/f0 − f0/f)/FBW. S11 = F/E and S21 = P/E
share E, monic of degree N; F is of degree N, and P of the spec's count of transmission zeros. They are fitted over
x = ω/max|ω| by `CouplingCurves.Rational`: Levy's linearised least squares, reweighted by 1/|E| as Sanathanan and
Koerner do. It is §6b's pole fit, generalised to several responses over one denominator; `CouplingCurves.Poles` now
calls it. This is the rational-model route to the coupling matrix (García-Lampérez et al.,
[10.1109/TMTT.2003.822021](https://doi.org/10.1109/TMTT.2003.822021)). `VectorFit` (Engine) was the other choice. It
fits real systems in Hz, with poles in conjugate pairs, and the lowpass poles of an asymmetric response are not; it also
cannot hold P to the zeros' count.

**Loss.** A uniform loss δ = 1/(Qu·FBW) on every resonator is the lossless response at s + δ (§2's `Response`), so every
fitted pole and zero sits δ to the left of its lossless place (Macchiarella,
[10.1109/LMWC.2010.2047455](https://doi.org/10.1109/LMWC.2010.2047455)). δ is the shift back that restores
|S11|² + |S21|² = 1 on the samples, found by ternary search between 0 and the nearest fitted pole's distance from the
axis. Then Qu = 1/(δ·FBW).

**The matrix.** The fitted zeros of F and P, each moved δ back, are the lossless reflection and transmission zeros.
- The transmission zeros are put on the axis, as `FilterSpec` holds real zeros only.
- The reflection zeros stay where they are: a mistuned filter's leave the axis, so its F is complex in ω.
- ε is 1/|P's leading coefficient|. εR is 1 unless all N zeros are finite, and ε/√(ε² − 1) then, as in §2.
- E comes from |E|² = |F/εR|² + |P/ε|² on the axis: the roots of that degree-2N polynomial above it.

These polynomials go through §2's `Transversal` and `Folded` as a synthesised set does. `Transversal` now adds F's
imaginary part in ω to Im Q, which a synthesised F does not have. The folded matrix's main line is made positive by
flipping whole rows and columns (`Signed`), so two matrices of one response compare entry by entry. For an all-pole
response the folded form is the inline chain. `ToPhysical` gives its Qe, k_ij and resonant frequencies, except for a
fully canonical matrix, whose source–load coupling no external Q carries.

**Checks.** Each is a `FilterCheck`: the value, its target, the delta and pass or fail.
- Against the spec: the centre within `tolerance` of half the band; the width at least (1 − `tolerance`) of the spec's;
  the ripple at most (1 + `tolerance`) of it; the return loss at least (1 − `tolerance`) of the prototype's at its edge;
  each transmission zero, in order, within `tolerance` of half the band of the spec's.
- Against a target matrix: every entry non-zero in either, named in band-pass terms (`f(i)`, `Qe(S)`, `Qe(L)`,
  `k(i,j)`, else `M(S,j)`), passing when it is within `tolerance` in M. The default, 0.02, is about 1 % of the band in
  a resonator's frequency and 4 % in an end's Qe.

`FilterCheck.Format` gives a value in its own unit (GHz, MHz for the bandwidth, dB, k to four places), and
`FilterSynthesisService.Extract(spec, snp)` reads a two-port against `Target(spec)`: what the Designer's metrics (§6) and
the polish call.

**Polish.** It takes the realisers that solve k and Qe on the circuit model: hairpins and open loops in a row (§4). Each
pass does three things:
1. It runs the bench the Filter Designer runs (`FilterSynthesisService.Cnl`, then `Response`): the part through the
   engine, on the substrate with its loss.
2. It extracts that bench against the all-pole target, `CouplingMatrix.AllPole(g)`.
3. It moves what `FromCouplings` is solved for. Each k(i,i+1) and the Qe are scaled by the ratio the extraction missed
   them by. `FromCouplings` then solves the gaps and the tap on its own curves. Each resonator's length is then scaled
   by its own extracted frequency over f0, as a resonator that sits low is too long.

The curves stand in for the filter, corrected at their output: output space mapping (Bandler et al.,
[10.1109/TMTT.2003.820904](https://doi.org/10.1109/TMTT.2003.820904)). `Run` returns every pass, the first being the
realisation as given, and the realisation of the pass with the best worst return loss, with a note saying how far it
got.
- **Another response.** `Run`'s `response` stands in for the bench in step 1: the Designer's Calibrate from EM passes
  the Planar EM run (§6d), so the curves are corrected at the EM output. Nothing else changes.
- **Where it starts.** Each `PolishStep` carries the `PolishAim` its part was solved for: the targets k and Qe and each
  resonator's length factor. `Run` takes one back as `aim`, so a later polish or calibration multiplies its ratios onto
  the earlier ones'. Started from the synthesis's own targets, it would undo them.
- **Whether it can.** `Refusal(realisation)` is the form check `Run` makes first, for a caller that greys a button.
- `cancel` stops it between passes and inside `FromCouplings`.

**How far it gets.** The Filter Designer's tapped hairpin for a 0.1 dB Chebyshev of order 5, 2.2 to 2.6 GHz, on the
shipped 20 mil RO4350B with its loss. Its worst return loss across the band is 8.6 dB as realised and 15.5 dB after
three passes, against the lossless prototype's 16.4 dB; the passes give 14.2, 15.2 and 15.5 dB.
- As realised, the end couplings are 3 to 7 % strong, Qe is 31 % high at the source and 22 % at the load, and
  resonators 2 and 4 sit 1.1 % and 0.6 % high.
- After three passes every k is within 0.4 % and every resonator within 0.15 % of f0. Qe is 5 % high at the source and
  5 % low at the load: MCRC has one tap for both ends, and the polish corrects their geometric mean.
- The extracted Qu is 114 to 121 throughout, the board's loss.

The three passes take about 25 s, most of it in `FromCouplings`.

**Refusals.** `synthesis.extract.*`: `band` (not a bandpass), `lengths` (the sample counts differ), `target` (a target
not N+2 square), `samples` (too few samples within the window, or under two in the band). `synthesis.polish.form`: not
an MCRC of hairpins or open loops in a row. Cohn's chains have no k or Qe to re-solve, the cross square realises only
|k14|, and MCLA is closed form (§7).

**Tests.**
- `tests/Core.Tests/Synthesis/FilterExtractionTests.cs`:
  - Round trip, from 401 samples of the matrix's own response over ω = ±4. Cameron's 4–2 at Qu = 500, and a mistuned
    0.1 dB order-5 chain (three resonators off tune, the source 10 % heavy) at Qu = 200: M back to 3e-13 and 6e-14, Qu
    within 0.001 and the zeros within 1 Hz.
  - An ideal 0.1 dB Chebyshev of order 5, 10 % wide: the centre to 1e-5, the width to 1e-3, the ripple 0.1 dB and the
    return loss 16.43 dB, every spec check passing.
- `tests/Ui.Tests/Synthesis/FilterPolishTests.cs`: the tapped hairpin above, its worst return loss across the band at
  least 3 dB better once polished, and the realisation returned the best pass's.

## 6d. Planar EM from the designer

**What it does.** `FilterEmService.Run(spec, realisation, substrate, frequencies, progress, token)`
(`src/Design/Filters/`) solves a realisation on Planar MoM. It is the run the Filter Designer and `circuitrf filter --em`
share. The spec gives the ports' Z0 and the bench the part is read from.
- **The drawing.** The bench (§6) is elaborated, and the part's plan is the one its model stamps
  (`PassiveSites.PlansOf`). `PassivePlanPCells.Draw` draws that plan, which is what the placed part's PCell draws, piece
  for piece, so the values are read exactly as a run reads them.
- **The solve** is §6b's `CouplingCurvePlanarSolver`, which now takes shapes and ports as well as a plan. The copper is
  unioned into one sheet on the substrate's grounded slab, on the coarse mesh (ten cells a wavelength, no edge cells),
  with at most 8 threads and no de-embedding.
- **The ports.** Ports 1 and 2 are edge ports on pins 1 and 2, and their source returns through the ground plane
  (Uncalibrated Ports). The default series gap is driven against the floating outer cell, so with no de-embedding that
  cell's capacitance sits in series with each port. On the edge-coupled bandpass below, that left the passband at
  −38 dB, and finer meshes made it worse. Each pin pair the realisation's sites use gets one more port, and parallel
  parts on the same pins share it.
  - A series site: the planar mesh cannot span a slot (`EmPlanarComponentPorts`), so a strip half the narrower pin wide
    joins the two pins across the gap, and an internal delta-gap port is cut across it. The pads' faces stay gridlines,
    so the cut lands between the pads. Shorted, the pair is solid metal.
  - A shunt site: an internal port to ground at the foot of a via grown one via in from the pin
    (`PlanarGroundPath`). The via is the board's 0.3 mm drill, or half the pad where that is narrower.
- **Closing the sites.** Each site port is terminated in its parts' reflection Γ: R, jωL or 1/(jωC) in parallel, and a
  via or 0 Ω is a short (Γ = −1). The two-port is S' = S_ee + S_es·Γ·(I − S_ss·Γ)⁻¹·S_se. The result carries the closed
  two-port and, when there are sites, the raw N-port with the site names in port order.
- **The verb.** `--em` runs it after the bench is written, at `--points` frequencies across the bench's sweep (11
  without `--points`, since 401 full-wave points is not a quick look). It writes `<name>.em.s2p` beside the `.cnl` and
  prints one line: the EM centre and the worst in-band |S11|, each with the circuit model's. The centre is midway between
  the first and last crossings of |S21| 3 dB under its peak (for a lowpass or highpass, its first crossing, the edge).
  Progress goes to stderr. A fuller set of figures is a later wiring.

**From the designer** (§6). Run Planar EM calls `Run` at `EmFrequencies`, 31 points across ω = ±3, so the extraction
gets every point. `Refusal(spec, realisation, substrate)` makes `Run`'s own checks (the drawing, pins 1 and 2, each
site's port) and returns the reason, so the window greys the button before anything is meshed. On the three-pole tapped
hairpin, 2.2 to 2.6 GHz on the shipped board, the first point took about 20 s in a Debug build on a shared machine.

**What it leaves out.** The site parts' own lands and bodies: the strip and the grown via stand for the part. A shunt
part's via is in the EM answer, while the circuit bench grounds the part ideally. Pins with no site stay open, as the
bench leaves them.

**What it costs.** The edge-coupled bandpass below solves in about 0.6 s a point. A grown via takes the solve onto
the general kernel: the 1 GHz lumped Butterworth lowpass below (three sites) takes 20 to 60 s a point. So the verb's
default is 11 points.

**Refusals.** `filter.em.*`, before anything is meshed:
- `sites`: a site of another role than R, L, C or via (a jumper or a load has no value to close its port with), a
  site pin the drawing lacks, or a series site whose pins do not face each other across a straight gap.
- `part`: a part with no plan, or no pins 1 and 2.
- The verb's `refused`: no substrate, or the solve's own failure, such as a mesh past the solver's ceiling.

**Tests** (`tests/Ui.Tests/Synthesis/FilterEmTests.cs`):
- A lumped ladder with a jumper site, and with a site on a pin the part does not draw, is refused as `filter.em.sites`.
- `Benchmark`: §6a's edge-coupled bandpass (0.5 dB Chebyshev, order 3, 2.28 to 2.52 GHz, the shipped 20 mil RO4350B),
  through `filter --em` at 11 points. `bp.em.s2p` passes the band, and its |S21|²-weighted centre is the circuit
  model's within 3 %: 2.352 GHz against 2.389 GHz, 1.6 % low. The run takes about 6 s.
- `Benchmark`: the lumped Butterworth lowpass at 1 GHz on MLAD sites (shunt C, series L, shunt C), at 300 MHz and
  1 GHz. Its ports are G1, G2-G3 and G4, and the raw N-port has five. The closed two-port passes 300 MHz, and at the
  cutoff it is within 1.5 dB of the circuit model: −4.15 dB against −3.43 dB. The run takes about 37 s.

## 6e. Filters on the PCell Designer

**What it does** (brief-pcd-12). A realisation becomes a designed cell (docs/design/pcell-designer.md), so a synthesised
filter is a PCell like any other: drawn by the planner at its parameters' values, its schematic its circuit model,
exported as a `.cpcell`. One Design function does it, `PCellFilter.Make` (`src/Design/PCellDesigner/PCellFilter.cs`),
which the Filter Designer's **Make PCell…** and `circuitrf filter --realiser R --pcell <name>` both call.

- **The cell.** `PCellDesign.Create`, then the Filter Designer's bench without its sweep and substrate
  (`FilterSynthesisService.Circuit`, which `Cnl` now is plus its analysis line, the same bytes) merged into the new cell by
  `CellNetlistMerge`: Ports 1 and 2, the part `F1`, a part on each site. The part states no substrate, so it takes the
  workspace technology's, as every designed cell's parts do.
- **The parameters** are the realisation's rows that hold one positive number with a length unit (`PCellFilter.Dimensions`):
  MCRC's W, L1…LN, the gaps, Tap, Sa, Gap, Wc; MCLA's W[k], S[k] and L, named with the brackets dropped. The part's row
  becomes the parameter's name; the default is the synthesised number in its unit, the range ±`FilterTuning.GeometryRange`
  (20 %), the band §6a's tune lines already use. Counts, words, MLAD's `Sections` text, frequencies and the site parts'
  values stay literal: they are not dimensions.
- **Save** writes what the PCell Designer's Save writes: the parameters, `PCellDesign` (its `Sweep` the bench's 401 points,
  and its `Filter`, below), `LayoutGenerator`, `EmCopper`, `Terminals`, `NumPorts`, a symbol from the Ports, the `.csch`,
  and the `.clay` as `DesignedCellLayout.At` at the defaults. Then `PCellDesignCheck.Run`.
- **The spec is kept** in `CcellPCellDesign.Filter` (`CcellFilter`: response, band, order, edges, ripple, stop, Z0, zeros,
  realiser), additive and omitted for any other cell. `PCellFilter.Metrics(spec, s, source)` reads any two-port against it
  through `FilterSynthesisService.Extract`, and `CircuitMetrics` runs the cell's own response (`PCellDesignResponse.Run`)
  first. `FilterMetricsPanel` (`src/Ui/PCellDesigner/`) shows the result with the Filter Designer's own rows
  (`FilterMetricRow.SpecRows`, `MatrixRows`, `Note`), which the Designer now builds its tables with too.
- **Polish** (`PCellFilter.Polish`): the realisation read back from the cell at its defaults (a bound row at its
  parameter's default, every other row the realiser set as it stands), §6c's `FilterPolish.Run` on it, the polished
  lengths, gaps and tap written back as the defaults, and the cell saved again. Hairpins and open loops in a row, as
  `FilterPolish.Refusal` passes.

**One prototype source.** Three Chebyshev recurrences existed: `LowpassPrototype.GValues`, `FilterLadder.ChebyshevG` and
`MatchSynthesis.RippleG`. The last two were the same text, the handbook's 17.37 for 40/ln 10, so that body moved to
`LowpassPrototype.TableChebyshevG` and both call it; the Butterworth forms were the same arithmetic too
(`LowpassPrototype.ButterworthG`). A test holds both against the old bodies, bit for bit, at orders 1 to 40.
`GValues`' Chebyshev stays its own: it takes 40/ln 10 exactly (§1), squares by `Math.Pow` and forms its angle in another
order, so one body would have moved the LC Filter Designer's values (by the constant, about 1e-5) or every realiser's (in
the last bits).

**How it lands** (`Pcd12FilterCellTests`, the Filter Designer's default specification: a 0.1 dB Chebyshev of order 3,
2.2 to 2.6 GHz, tapped hairpins, on the `microstrip-0508` board):
- Make PCell gives eight parameters, each ±20 %: W 1.057682 mm, Sa 2.115364 mm, S2 = S3 0.15484 mm, Tap 6.36787 mm,
  L1 = L3 37.868106 mm, L2 38.13788 mm. `PCellDesignCheck` finds no error and no warning, planning the cell at the
  defaults and at each of the sixteen bounds; the `.cpcell` is 2657 bytes and names one generator, MCRC. The verb makes
  the same parameters.
- The cell's own circuit response is the Filter Designer's bench: the same extraction to 1e-6 of f0 in the centre and
  0.01 dB in the return loss, check for check. As realised it reads centre 2.3301 GHz, 204.9 MHz wide, worst return loss
  7.96 dB, Qu 144: 8 of 11 checks fail, as the bench's do.
- One polish pass on the parameters takes the worst return loss from 7.96 to 14.16 dB, moving S2, S3, the tap and the
  three lengths (the tap 6.36787 → 7.156678 mm, L2 38.13788 → 38.814244 mm); the cell's response at its new defaults
  reads the polished figure back.

**Possible enhancements.**
- **The site parts' values as parameters.** A lumped ladder's L and C are its design; as parameters, an MLAD cell would
  tune from a bench. They are not dimensions, and their range wants the part's tolerance, not 20 %.
- **MLAD's sections as parameters.** Its lengths and widths live inside the `Sections` text, which no expression reaches;
  the part needs indexed rows first.
- **Calibrate from EM for a designed cell.** `FilterPolish.Run` already takes an EM response; handing it the cell's own Run
  EM would correct the cell against its layout, at one EM run a pass.
- **The Filter Designer reopened on a designed filter cell.** The kept spec is enough to open it filled in, with the cell's
  dimensions as the realisation, so a cell can be resynthesised for another band.
- **A Polish button in the PCell Designer.** `PCellFilter.Polish` is the function; the designer window, which another lane
  owns, would place it beside the metrics panel.

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

- **MCLA: Wenzel's exact synthesis for wide bands** (interdigital,
  [10.1109/TMTT.1965.1126051](https://doi.org/10.1109/TMTT.1965.1126051); combline and capacitively loaded
  interdigital, [10.1109/TMTT.1971.1127609](https://doi.org/10.1109/TMTT.1971.1127609)). Why: Matthaei's forms are
  approximate and refused past an octave, while the arrays are exactly stubs and unit elements on TEM lines, so a
  distributed prototype would make the TEM design exact at any width. It was not used here because its equations
  need the distributed prototype's own synthesis.
- **MCLA: a pseudo-interdigital realiser** (Hong and Lancaster,
  [10.1109/75.401073](https://doi.org/10.1109/75.401073)). Why: the part already builds the interleaved hairpin
  groups, which are compact and need no vias (the GaAs die's case). Their half-wave couplings need their own design
  relations or a coupling-curve fit on the plan.
- **MCLA: the other feed for each form**, a tapped interdigital and a combline with terminating strips. Why: the part
  takes both feeds. A tapped interdigital saves two strips and the end gap the coupled feed needs, which is tight (0.09 mm
  on 20 mil RO4350B, 28 µm on 100 µm GaAs).
- **MCLA: the interdigital fit's mutual read at the centre**, as the combline's is. Why: the interdigital ripples to
  0.36–0.60 dB against the 0.1 dB asked, mostly from microstrip's unequal mode speeds, which the characteristic admittance
  alone leaves out.
- **MCLA: centring the combline's band.** Why: its skirts are not symmetric, so the 3 dB band sits about 1 % below a
  centre its resonators are tuned to. A small shift of the tuning frequency, found from the band edges, would centre it.
- **Designer: the ideal at Qu for the other bands.** A lowpass, highpass or bandstop prototype with lossy elements.
  Why: only a bandpass's resonators map onto `CouplingMatrix.Response`'s single δ, so the other bands show no ideal at
  Qu and no readout.
- **Designer: the resonator's own Qu.** Radiation and the open ends in the unloaded Q, from the realised part's own
  resonator. Why: the line's β/2α leaves both out, so a hairpin loses somewhat more than the ideal at Qu.
- **MCLA: the fit coarse, then fine.** Its steps on the field solver at half the resolution until within the 0.3 %, then
  at full resolution to finish. Why: the fit's solves are nearly all of an MCLA realisation, and a coarse solve costs about
  a tenth of a fine one.
- **MCRC: the peak search narrowed to the last step's peaks.** Why: each halving sweeps 601 points over ±50 % of f0,
  while the peaks move a fraction of a per cent from one halving to the next.
- **MCLA: choosing h, or a combline's Y_a, for the process.** Why: the defaults fit a board. A die with a minimum gap
  wants the scale that keeps the narrowest gap at or above that minimum.

- **EM (§6d): shunt sites on grounded edge ports at the pin.** Why: a grown via takes the solve onto the general
  kernel, 30 to 100 times slower a point than the one-level one (§6d). The via's inductance would then go into the
  closing load, as the circuit bench's VIAGND has it.
- **EM (§6d): de-embedded edge ports.** Why: ports 1 and 2 are solved without the two-line calibration, so the half
  cell beyond each cut is in |S11|. The calibrated run would sharpen the return loss, at a few calibration solves per
  point.
- **EM: the site parts' lands from their `Footprint`.** Why: the strip across a series gap and the via under a shunt pad
  stand for the part. Drawing the footprint's lands would put their capacitance in the answer, as the circuit bench's
  footprint does.
- **EM: an adaptive sweep** (`PlanarAdaptiveSweep`). Why: a coarse uniform grid reads a narrow band's edges between
  samples. Points placed where the response turns would give the edges at the same cost.

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
- **The EM curves checked against the circuit model** (§6b). One hairpin pair at three gaps, 21 points on the coarse
  mesh, its EM k(S) within 20 % of the circuit model's and with the same trend, tagged `Category=Benchmark`. Why: the
  Planar MoM side of the seam builds but has not been run against a design yet.
- **Qe(S1) on EM for coupled feeds** (§6b). The doubly loaded bench's pole gives the loaded Q, Qe = 2·QL. Why: under
  `Refine` = `Em` a coupled hairpin's feed gap is still the circuit model's.
- **Unequal external Q at the two ends** (§4). `FromCouplings` takes one Qe and MCRC one Tap. Why: a filter between
  unequal terminations, or a multiplexer's channel, needs a different Qe at each end.
- **A trisection on MCRC** (§4.1). An arrangement of three open loops in a triangle, 1-3 coupled, after Hong and Lancaster's
  trisection ([10.1049/ip-map:19990146](https://doi.org/10.1049/ip-map:19990146)), with the matrix's CT form (item 8)
  giving its couplings. Why: one zero on the side that needs it, at three poles rather than four. Two in a row place two
  zeros anywhere, which the square, holding mirror zeros only, cannot.
- **A square with k14 of the same sign as k23** (§4.1). Loops 1 and 4 opened away from each other, so that they couple
  magnetically, as 2 and 3 do. Why: complex zeros (item 10), which flatten the group delay, need the cross coupling in
  phase with the main path, and today's square only draws it against.

- **Named numbers in MLAD's list** (§6a). A field may name a variable, `line(W=w1, L=l1)`, which the bench declares and
  `tune` reaches. Why: a ladder's lines and stubs are text today, so the optimizer tunes its sites only.
- **W as a tune variable on Cohn's chains** (§6a). Why: with one W the edge-coupled filter's ripple is about 1.6 dB in the
  model, and gaps and lengths alone leave its pass goal 4.5 dB short.
- **Stop goals over a range** (§6a). Why: the stop goals are points at the edges, which holds an all-pole skirt but not
  an elliptic's stopband ripple past them.
- **Phase loading in the extraction** (§6c). A linear phase in ω on each port, fitted out of S11 and S22 before the
  rational fit. Why: a measured or EM filter has line between each port and its first resonator, whose phase no
  rational of degree N holds, so its fit and matrix suffer. The circuit model's taps sit on the resonators, which is
  why the polish does without.
- **Run Planar EM measured on the five-pole hairpin** (§6, §6d). Its time, and its extraction against the circuit
  bench's, phase loading included. Why: the run was stopped before it finished, so the 31-point cost and the EM matrix
  are not yet known.
- **The Designer's EM screenshot** (§6). `UiSnapshot --filter-designer mcrc --fd-em` captures the five-pole hairpin
  after Run Planar EM, for the user page. Why: the EM run behind it was not run to the end.
- **Polish for Cohn's chains and MCLA** (§6c). A Broyden step on the stored gaps and lengths, its first Jacobian by
  finite differences on the bench. Why: their geometry comes from closed forms, not from k and Qe curves, so output
  space mapping has nothing to re-solve, and an edge-coupled chain's 1.6 dB ripple (§6a) is the case that most needs it.
- **Extraction in the verb and the MCP tool** (§6c). A `filter --extract` mode on a Touchstone file, and the same over
  MCP. Why: the Designer reads its own responses back (§6), but a measured board is the commonest thing to hold against
  its synthesis, and an agent has no window.
- **Complex and asymmetric zeros in the extraction** (§6c). Keep the transmission zeros' imaginary parts, and polish
  the cross square with its k14's sign. Why: the extraction puts every zero on the axis as `FilterSpec` holds them, and
  the polish refuses the square, so neither group-delay zeros (item 10) nor the cross-coupled loops are corrected.

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

- G. L. Matthaei, L. Young and E. M. T. Jones, *Microwave Filters, Impedance-Matching Networks, and Coupling Structures*,
  McGraw-Hill, 1964: the doubly terminated lowpass prototype, its terminations and its frequency transformations to the
  highpass, bandpass and bandstop forms; a book, no DOI: [the book at the Internet Archive](https://archive.org/details/microwavefilters0000matt).
- J.-S. Hong and M. J. Lancaster, "Microstrip cross-coupled trisection bandpass filters with asymmetric frequency
  characteristics," IEE Proc. Microwaves, Antennas and Propagation 146(1), 1999:
  [10.1049/ip-map:19990146](https://doi.org/10.1049/ip-map:19990146) (§7).

- G. L. Matthaei, "Interdigital Band-Pass Filters," *IRE Transactions on Microwave Theory and Techniques*, vol. 10,
  no. 6, pp. 479–491, November 1962. DOI [10.1109/TMTT.1962.1125556](https://doi.org/10.1109/TMTT.1962.1125556). The
  interdigital closed forms (§5).
- G. L. Matthaei, "Comb-Line Band-Pass Filters of Narrow or Moderate Bandwidth," *Microwave Journal*, vol. 6,
  pp. 82–91, August 1963. A trade journal, with no DOI; the same design is in Matthaei, Young and Jones, ch. 10, also
  with no DOI: [the book at the Internet Archive](https://archive.org/details/microwavefilters0000matt). The combline's
  design (§5).
- R. J. Wenzel, "Exact Theory of Interdigital Band-Pass Filters and Related Coupled Band-Pass Structures," *IEEE
  Transactions on Microwave Theory and Techniques*, vol. 13, no. 5, pp. 559–575, September 1965.
  DOI [10.1109/TMTT.1965.1126051](https://doi.org/10.1109/TMTT.1965.1126051). The exact interdigital theory (§7).
- R. J. Wenzel, "Synthesis of Combline and Capacitively Loaded Interdigital Bandpass Filters of Arbitrary Bandwidth,"
  *IEEE Transactions on Microwave Theory and Techniques*, vol. 19, no. 8, pp. 678–686, August 1971.
  DOI [10.1109/TMTT.1971.1127609](https://doi.org/10.1109/TMTT.1971.1127609). The wide-band combline (§7).
- J.-S. Hong and M. J. Lancaster, "Development of New Microstrip Pseudo-Interdigital Bandpass Filters," *IEEE Microwave
  and Guided Wave Letters*, vol. 5, no. 8, pp. 261–263, August 1995.
  DOI [10.1109/75.401073](https://doi.org/10.1109/75.401073). The pseudo-interdigital form (§7).
- C. R. Paul, *Analysis of Multiconductor Transmission Lines*, 2nd ed., Wiley-IEEE Press, 2007.
  DOI [10.1109/9780470547212](https://doi.org/10.1109/9780470547212). The characteristic admittance matrix and the
  shorted array's susceptance (§5).
- C. Sanathanan and J. Koerner, "Transfer function synthesis as a ratio of two complex polynomials," IEEE Trans. AC
  8(1), 1963: [10.1109/TAC.1963.1105517](https://doi.org/10.1109/TAC.1963.1105517) (§6b).
- A. García-Lampérez, S. Llorente-Romano, M. Salazar-Palma and T. K. Sarkar, "Efficient electromagnetic optimization
  of microwave filters and multiplexers using rational models," IEEE Trans. MTT 52(2), 2004:
  [10.1109/TMTT.2003.822021](https://doi.org/10.1109/TMTT.2003.822021). S11 and S21 as rationals over one denominator,
  then the coupling matrix (§6c).
- G. Macchiarella, "Extraction of unloaded Q and coupling matrix from measurements on filters with large losses," IEEE
  Microwave and Wireless Components Letters 20(6), 2010: [10.1109/LMWC.2010.2047455](https://doi.org/10.1109/LMWC.2010.2047455).
  A uniform loss as one shift of every pole and zero, and Qu from it (§6c).
- J. W. Bandler, Q. S. Cheng, S. A. Dakroury, A. S. Mohamed, M. H. Bakr, K. Madsen and J. Søndergaard, "Space mapping:
  the state of the art," IEEE Trans. MTT 52(1), 2004: [10.1109/TMTT.2003.820904](https://doi.org/10.1109/TMTT.2003.820904).
  Output space mapping, the polish (§6c).
