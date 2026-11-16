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

(to be written)

## 4. Coupled resonators on MCRC

(to be written)

## 5. Coupled arrays on MCLA

(to be written)

## 6. The Filter Designer, the `filter` verb and the MCP tool

(to be written)

## 7. Possible enhancements

(to be written)

1. **The coupling matrix in extended precision, or from an eigenproblem.** The residues lose digits where two poles lie
   close together. Two ways would lift the 1e-8 to 3e-7 floor (§2) back to 1e-13 and let `MaxOrder` rise past 10:
   double-double arithmetic in `Transversal`, or the poles and residues as the eigenvalues and eigenvectors of a
   well-conditioned matrix. Why: narrow filters with zeros near the band edge, the case that most needs the matrix,
   are the ones that cluster their poles.
2. **Arrow, CT, CQ and box forms.** Each is further rotations of the folded or arrow matrix, as Cameron 2003 sets out.
   Why: the folded form's cross-diagonal couplings are hard to lay out in microstrip past N = 4. Trisections and
   quartets put each zero in a local group that an MCRC arrangement can draw.
3. **Lossy and predistorted synthesis.** Why: with a finite Qu, the response today only shows the loss and its rounded
   edges. A predistorted matrix would restore the flat passband on a die or a lossy laminate, at a known cost in
   insertion loss.
4. **Complex (group-delay) zeros.** The recursion takes complex ω_n in symmetric pairs unchanged. Only the validation
   and `FilterSpec`'s real zero list hold them back. Why: self-equalised filters flatten the group delay without an
   external equaliser.

## 8. References

(to be written)

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
