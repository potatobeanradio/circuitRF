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

(to be written)

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

(to be written)

## 7. Possible enhancements

(to be written)

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
- **MCLA: choosing h, or a combline's Y_a, for the process.** Why: the defaults fit a board. A die with a minimum gap
  wants the scale that keeps the narrowest gap at or above that minimum.

## 8. References

(to be written)

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
