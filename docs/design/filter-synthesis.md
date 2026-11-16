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

(to be written)

## 5. Coupled arrays on MCLA

(to be written)

## 6. The Filter Designer, the `filter` verb and the MCP tool

(to be written)

## 7. Possible enhancements

(to be written)

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

(to be written)

- P. I. Richards, "Resistor-transmission-line circuits", Proc. IRE, 1948. [10.1109/JRPROC.1948.233274](https://doi.org/10.1109/JRPROC.1948.233274)
- H. Ozaki and J. Ishii, "Synthesis of a class of strip-line filters", IRE Trans. Circuit Theory, 1958. [10.1109/TCT.1958.1086441](https://doi.org/10.1109/TCT.1958.1086441)
- G. L. Matthaei, "Design of wide-band (and narrow-band) band-pass microwave filters on the insertion loss basis", IRE Trans. MTT, 1960. [10.1109/TMTT.1960.1124801](https://doi.org/10.1109/TMTT.1960.1124801)
- S. B. Cohn, "Direct-coupled-resonator filters", Proc. IRE, 1957. [10.1109/JRPROC.1957.278389](https://doi.org/10.1109/JRPROC.1957.278389)
- J.-S. Hong and M. J. Lancaster, *Microstrip Filters for RF/Microwave Applications*, Wiley, 2001. [10.1002/0471221619](https://doi.org/10.1002/0471221619)
- B. M. Schiffman and G. L. Matthaei, "Exact design of band-stop microwave filters", IEEE Trans. MTT, 1964. [10.1109/TMTT.1964.1125744](https://doi.org/10.1109/TMTT.1964.1125744)
- R. Levy, "Tables of element values for the distributed low-pass prototype filter", IEEE Trans. MTT, 1965. [10.1109/TMTT.1965.1126048](https://doi.org/10.1109/TMTT.1965.1126048)
- M. C. Horton and R. J. Wenzel, "General theory and design of optimum quarter-wave TEM filters", IEEE Trans. MTT, 1965. [10.1109/TMTT.1965.1125996](https://doi.org/10.1109/TMTT.1965.1125996)
- D. M. Pozar, *Microwave Engineering*, 4th ed., Wiley, 2012, §8.6 and §8.8: a book, no DOI. [Wiley](https://www.wiley.com/en-us/Microwave+Engineering%2C+4th+Edition-p-9780470631553)
