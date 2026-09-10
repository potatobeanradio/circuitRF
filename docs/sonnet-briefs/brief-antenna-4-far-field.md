# Brief — ANT-4: the far field

**Series:** `brief-antenna-0-overview.md` §3. **Depends on:** ANT-1 (or every measurement is taken on
a board with no feed). **Blocks:** ANT-5, ANT-6, ANT-7, ANT-10, ANT-11.

The pivot of the series. Everything else that looks away from the metal is a post-process of what
this brief produces.

---

## 1. Why this is not a second L8a

The instinct from L8a and L9a is that anything touching the layered Green's function is a schedule
risk. **It is not, here, and the reason is structural rather than optimistic.**

DCIM exists because the **matrix fill** needs the Green's function in the **spatial** domain, which
means inverting a Sommerfeld integral that is oscillatory, slowly convergent and full of branch points
and poles. That is the part that was hard and it is done.

The far field needs the **spectral** Green's function at exactly **one point per direction**,
k_ρ = k₀ sin θ, by stationary phase. `SpectralGreens.ReflectionTe(kRho)`, `ReflectionTm(kRho)` and
`Kz0(kRho)` already return it in closed form; `LayeredSpectralGreens` does the same for a general
stack. **No DCIM, no Sommerfeld quadrature, and no `Dcim.WithinValidatedRange` refusal** — the
far-field module never touches that path and must not inherit its range limit.

The current side is closed form too, so the whole thing is an exact sum:

> **E_θ, E_φ(θ, φ) = a sum over N basis coefficients, with no quadrature error of its own.**

O(N) per direction. N ≈ 4,000 over a 1° × 1° hemisphere grid is order 10⁸ complex operations —
seconds, and trivially parallel over directions. Cost is not a design constraint here and must not be
allowed to become one by premature approximation.

## 2. The shape of the answer, and what must be derived rather than copied

For horizontal electric current at the metal plane, observed in the upper half space, stationary
phase gives a form of

```
E_θ(θ,φ)  =  C(r) · [  J̃_x cos φ + J̃_y sin φ ] · f_TM(θ)
E_φ(θ,φ)  =  C(r) · [ −J̃_x sin φ + J̃_y cos φ ] · f_TE(θ)
```

with `C(r)` the usual −j k₀ η₀ e^{−jk₀r} / 4πr, `J̃` the 2-D Fourier transform of the surface current
evaluated at (k_x, k_y) = k₀ sin θ (cos φ, sin φ), and `f_TM` / `f_TE` the layered-medium element
factors built from the TE/TM reflection coefficients at that same k_ρ, referred to the source height.

**Do not take those factors from this brief.** They must be derived from `SpectralGreens`' own
conventions — its `ProperRoot` branch choice, its sign convention, and the height referencing in
`KernelAtHeights` — and then **pinned against the oracle in §5.2**, which is what decides whether the
derivation is right. This repository has twice found the *oracle* wrong rather than the method
(§L8a, §L7b-b), so both sides get written independently.

**The rooftop transform is a closed form and must be treated like L8c's six inner integrals.** An
x-directed rooftop spanning a cell pair is a triangle in x and a rectangle in y; its transform is a
product of elementary factors. Derive it once, in one place, with the normalisation stated — L8c's
own header records that "L8c normalises each rooftop to UNIT TOTAL CURRENT ACROSS ITS SHARED EDGE, so
a basis coefficient I_b is in AMPERES", and the transform must be consistent with that or the pattern
is right in shape and wrong in level. Test it against adaptive quadrature to 1e-12, exactly as the
inner integrals were.

**Conformal (cut) cells.** `PlanarCell.Region` means a boundary cell's metal is not its rectangle. The
transform of a cut cell is not the rectangle's. Either derive it for the cut region or **refuse the
combination by name** and say which phase supplies it — a rectangle's transform silently used for a
cut cell is a smooth, plausible, wrong far field, which is the exact failure mode
`src/Engine/Mom/CLAUDE.md` warns about for a neighbouring pole. Refusing is an acceptable v1; guessing
is not.

## 3. The upper hemisphere is the whole domain, and the θ axis says so

With an analytically infinite ground plane there is **no field at θ > 90°**. Not small — identically
zero, by construction.

**Decision: the θ axis spans 0…90° only, and the run says why.** The alternative — a 0…180° axis half
full of structural zeros — invites a polar plot that looks like a measurement of a very good antenna,
and there is nothing in a plot to tell a user that the lower half is an artefact of the medium rather
than a property of the design. A missing axis range with a stated reason cannot be misread; a zero can.

**This is the first half of the staged front-to-back refusal** (`brief-antenna-0-overview.md` §4).
ANT-5 creates the metric entry, present and refused. **ANT-11 extends this axis to 180°** and narrows
the refusal. Nothing here should be written in a way that makes that extension a rewrite: the θ axis
is data, not a constant.

## 4. The data model

**R-res-6 holds: no new result type.** A far-field run adds one group of cubes to the `DataSet` the
planar run already returns, exactly as kernel B added `"planar"`. `DataCube` is already N-rank with
named, unit-bearing axes and slicing, so nothing new is needed to carry this.

Group `"farfield"`, axes `[freq, theta, phi, port]`:

| cube | kind | unit | note |
|---|---|---|---|
| `Etheta` | Complex | V (far-field pattern, r-normalised) | |
| `Ephi` | Complex | V | |
| `U` | Real | W/sr | radiation intensity — derived, but stored, because every metric reads it |

**Keep the port axis from the first commit**, even though every fixture in this series is a one-port.
`PlanarPortSolution.Currents` is already one vector per driven port, so it is free at construction —
and it is what makes array pattern synthesis possible later. Retro-fitting an axis onto a shipped cube
is not free.

**The r-normalisation must be stated in the axis or the note, once.** A pattern quantity that is
"E times r" and a pattern quantity that is "E at 1 m" differ by a factor nobody will notice until they
compare against a measurement.

**Which excitation.** The pattern belongs to one driven port at a time, in the same sense the current
map does — `PlanarCurrentDensity`'s signature enforces that by construction and the comment says why:
"a map that superposed every excitation would be a map of nothing". Same rule, same reason. Whether
the normalisation is 1 V incident or 1 W accepted is a **decision ANT-5 must settle before it computes
gain**, and it must be recorded here in the cube's own units either way.

## 5. Gates

Four oracles, in increasing order of what they can catch. All three of the first are independent of
the MoM solve, which is the point.

### 5.1 The εᵣ = 1 reduction

Collapse the slab to free space and the far field must reduce to **free space plus one image** — the
same reduction that validated the matrix fill (`src/Engine/Mom/CLAUDE.md` §1 calls it "the strongest"
of the exact reductions, and the direct analogue of the image gate that validated kernel A's
R-mom-7). This catches sign and branch errors in `f_TM`/`f_TE` with no substrate physics involved.

### 5.2 The infinitesimal horizontal dipole over a grounded slab — **the key gate**

A closed-form far field exists for this, and it involves **no MoM at all**: one current element, the
layered element factors, done analytically. Drive a single rooftop at the centre of a large mesh and
compare. This is what tells you whether the element factors were derived correctly, and it is the
direct analogue of L8a validating DCIM against direct Sommerfeld integration — *a second, independent
formulation that shares no approximation with the first.*

Write the oracle from published theory, not from `SpectralGreens`. An oracle assembled from the same
functions it is testing proves only that they are self-consistent.

### 5.3 The rooftop transform against quadrature

1e-12, as L8c's inner integrals were. Includes the cut-cell case if §2 derives it rather than refusing
it.

### 5.4 Power balance — reported first, gated once trusted

The hemisphere integral of `U` must equal the power the solve says left the port, less the losses. In
full that is ANT-5's itemisation; here, the weaker and still valuable statement is:

> ∫ U dΩ over the upper hemisphere ≤ P_accepted, and the shortfall is accounted for by dielectric
> loss and surface-wave power.

**Report the four terms before gating any of them.** If they do not sum, something is wrong and the
balance is what localises it. Note that the copper term is **identically zero** in this kernel
(perfect conductor — `SigmaSm` is carried through the pipeline and never read by the fill), so the
balance will close *optimistically*, and that is expected, not a defect. ANT-5 owns saying so.

### 5.5 Structural

- The far-field path must not call `Dcim` at all — assert it, so the validated-range refusal cannot
  leak in later by an innocent-looking refactor.
- Reciprocity: a symmetric structure driven at symmetric ports gives mirrored patterns.
- Cost: pattern evaluation for the measured board's mesh over a 1° grid, **measured once in a scratch
  harness and reported**. It does not become a timing test.
- `RESOLVED.md` write-up in `src/Engine/Mom`; `CLAUDE.md` gains the new group name, the θ-range
  decision and its reason, and the no-DCIM invariant.

## 6. Must NOT

- **Do not route the far field through DCIM or the spatial-domain kernel.** It would import a
  validated-range refusal the far field does not need and an approximation it does not have.
- **Do not emit a 0…180° θ axis padded with zeros.** §3.
- **Do not compute a pattern from the reduced per-cell |J| map.** `PlanarCurrentDensity` is a
  *display* reduction — it collapses a rooftop pair to a per-cell scalar and loses the basis
  structure. The far field reads `PlanarPortSolution.Currents` directly. Two definitions of one
  physical quantity is what that file's own header exists to prevent.
- **Do not superpose ports.** One driven port per pattern, enforced by the signature.
- **Do not use a rectangle's transform for a cut cell.** Derive it or refuse it.
- **Do not add a far-field measurement to the `TestBench` `measure` grammar.** An EM run is a `.cem`
  run producing a `DataSet`, not a TestBench analysis; these are diagnostics cubes, in the group
  pattern kernel B already established.
- **Do not optimise before measuring.** The direct sum is exact; an FFT or an interpolation over
  directions trades that away for a cost that has not been shown to matter.

## 7. Reading order

`src/Engine/Mom/SpectralGreens.cs` — `ReflectionTe`, `ReflectionTm`, `Kz0`, `ProperRoot`,
`KernelAtHeights`, and `LayeredSpectralGreens` (the whole far-field kernel is here) ·
`src/Engine/Mom/PlanarBasisFunctions.cs` (the rooftop, and its unit-current normalisation) ·
`src/Engine/Mom/PlanarExcitation.cs` (`PlanarPortSolution.Currents`) ·
`src/Engine/Mom/PlanarCurrentDensity.cs` header (why a display reduction is not a physical quantity's
second definition) · `src/Engine/Mom/PlanarKernel.cs` `BuildDataSet` (how a diagnostics group is
added) · `src/Engine/Mom/HISTORY.md` §L8a (how an independent second formulation was used as an
oracle, and the two occasions the oracle was the thing that was wrong).
