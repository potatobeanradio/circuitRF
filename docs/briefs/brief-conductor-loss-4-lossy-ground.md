# Brief CL4 — the lossy ground plane, as an impedance termination

**Series:** [`brief-conductor-loss-0-overview.md`](brief-conductor-loss-0-overview.md) ·
**Tags:** `R-cl4-n` · **Area:** `src/Engine/Mom` · **Depends on:** CL1, CL2, CL3 all shipped

**Read first:** `src/Engine/Mom/CLAUDE.md` §3.1 (the layered Green's function and DCIM) and §5 (the
validated ranges — every one of them is at stake here); `LayeredMedium.cs` in full; `SpectralGreens.cs`'s
pole finder and its L9c block comment; `Dcim.cs`'s header; `PlanarProblem.cs:340-370`;
`brief-L9a-general-layered-medium.md` and `brief-L9b-dcim-general-medium.md`, because this brief
walks into the same spectrum they did; **CL3 §0 and `RESOLVED.md` §QSC, because the calibration path
that supplies γ below a per-stack crossover has a PEC ground of its own and this brief does not
reach it.**

---

## This brief is allowed to fail

**A measured refusal is a successful outcome.** Four independent systems sit downstream of the
bottom termination, each with its own validated range, and any one of them may say no. The
deliverable is then a named refusal with numbers behind it, recorded in `RESOLVED.md`, and the
overview §2 figure stays in the user docs as a stated limit. **Do not force a fit, widen a validated
range, or relax `PoleLossCeiling` to make this land.** §6 of `src/Engine/Mom/CLAUDE.md` exists
because this area has produced measured negative results before and they were worth more than a
forced yes.

## The problem

Kernel B's ground is not meshed. It is the laterally infinite PEC the transmission-line cascade
terminates on, `Γ = −1`. There is no current to put CL1's `Z_s` on, and it is worth **21.1%** of the
conductor term on FR-4, **~11%** on the MMIC starter and **25.0%** on a low-loss laminate
(overview §2) — the microstrip case only, since a ground drawn as artwork is a meshed level that CL1
already covers.

## What stays exactly the same

**The plane remains laterally infinite and analytic. It is never meshed, it adds no unknowns, and
R17's ceiling does not move.** Only the terminating reflection changes.

That is not a compromise, it is the point: the impedance condition holds at **every spectral
component `k_ρ`**, so the non-uniform return current under a strip — concentrated beneath it,
falling off as roughly `1/(1+(x/h)²)` — and the loss it dissipates are implied by the spectrum and
fall out of the inverse transform. Nothing has to represent the ground's current distribution
explicitly. Kernel A reaches the same place from the other side, by receding the plane and
recomputing L without meshing it either.

**And the Leontovich condition is better satisfied here than anywhere in CL1.** It assumes a surface
locally flat on the scale of a skin depth. A strip edge violates that outright, which is why CL1
carries an edge-convergence gate; a laterally infinite plane is the geometry the condition was
derived for. **The physics in this brief is on firmer ground than CL1's — the risk is entirely in
the four downstream systems of the next section, not in the model.**

Three things it still cannot do, unchanged by this brief:

- **No ground edges.** Still infinite: no finite-ground radiation, no edge diffraction. That is
  ANT-11's `PlanarFiniteGround` outline correction and is unrelated.
- **No patterned ground** — a slot, a void, a gap. Those must be drawn as artwork on a meshed level,
  where CL1 already covers the loss, and the Green's-function ground must not then also be beneath
  them.
- **One σ and one thickness** for the whole infinite plane, because it is one scalar.

### And one more, which is CL3 §0's, inherited

**The quasi-static calibration path's ground is a PEC too, and this brief does not change that.**
`PlanarQuasiStaticLine` runs the standard's electrostatics over a `GroundedSlab`, whose floor is
perfect by construction; nothing in the termination this brief re-points is read by that solve. So
below `PlanarCalibration.QuasiStaticCrossoverHz` — 3.048 GHz on the FR-4 starter, **26.07 GHz on the
MMIC starter** — the supplied γ gains no ground term, exactly as CL3 §0 found it gains no strip
term. **Whatever CL3's milestone 0 decided for the strip has to be extended here or the residue
stated with its size**, and the size is this brief's own headline number: 21.1% (FR-4) / ~11%
(GaAs) / 25.0% (low-loss laminate) of the conductor term.

**R-cl4-3's ratio is the place this bites.** That gate asks whether the measured ground share
appears — a ratio of α_c-with-ground to α_c-strip-only — and if the frequency it is asked at is
below the crossover, its denominator was set by a calibration carrying neither term. **Read α from a
de-embedded S₂₁ over a known length and name the path per point** (CL3's R-cl3-6), or ask the gate
above the crossover on both starters, which on GaAs means above 26 GHz.

## The change, which is small; and the consequences, which are not

The termination reflection becomes, per polarization,

```
Γ_bottom^{e,h} = (Z_s − Z_line^{e,h}) / (Z_s + Z_line^{e,h})
```

with CL1's `Z_s` — the **one-sided** form `η_c·coth(γ_c t)` for a plane with air below, not the
two-sided one, and this is the first place a factor of two can enter unseen.

The cascade already supports an impedance termination: `TerminationKind.HalfSpace` is matched into a
stated material and `LayeredMedium.cs:118-121` says covering both costs nothing *there*. The sites
are `LayeredMedium.cs:441-442`, `SpectralGreens.cs:777`, `:804` and `:1358`. **That is the easy
half and it is perhaps thirty lines.**

## The four things that must be MEASURED, in this order, stopping at the first that refuses

1. **Can DCIM still fit it?** (`Dcim.CanFit`, `Dcim.Fit`.) The exponential sum in `k_z0` is entire
   and **cannot carry a branch cut** — that is exactly why an open-below stack with a denser bottom
   is refused by name (§7). An impedance termination does not obviously add a cut, but it makes
   `Γ_bottom` a function of `k_ρ` where it was a constant, and **whether the fit survives is a
   measurement, not an argument**. Gate: the spectral residual against direct numerical Sommerfeld
   integration, on both starters, against §5's `ValidatedRhoOverLambdaLayered` of 1.6e-2. Report the
   residual at the same ρ/λ points §5 tabulates.
2. **Where did the surface-wave poles go?** (`SpectralGreens.RefineComplex`.) A lossy floor moves
   them further off the real axis. `PlanarMetrics` refuses `PowerSurfaceWave` past
   `|Im k_ρ|/Re k_ρ = 0.05` and `PowerDielectric` goes with it (§7). Gate: report the worst pole's
   ratio on both starters across the band. **If it crosses 0.05, the antenna metrics start refusing
   on ordinary boards** — that is a product regression, not a detail, and it may on its own be the
   reason this brief refuses.
3. **Is the ground-attachment basis still legitimate?** `PlanarProblem.cs:354-365` refuses a
   `GroundTerminal` via unless the bottom termination **is** a PEC, by name, because the half
   rooftop's lower terminal is that perfect conductor. An impedance floor makes that sentence false.
   Either the refusal is re-stated in terms of the new termination, or the half-rooftop is
   re-derived against it. **Do not simply delete the check** — it is the only thing standing between
   a backside via and a basis function whose lower terminal does not exist.
4. **What happens below the plane?** `FrontToBackDb` is refused *always* because the field below a
   laterally infinite PEC is identically zero (§7, §3.7). An impedance floor leaks, so the true
   ratio is finite. Gate: either the refusal's stated reason is corrected, or the metric becomes
   computable — **decide explicitly and record which**, because a refusal whose reason has become
   false is worse than a missing metric.

## Gates

- **R-cl4-1 — the PEC reduction.** At σ = ∞ the termination returns exactly `−Complex.One` and the
  whole kernel is **bit-identical** to CL3's. Not 1e-12. **The quasi-static calibration path is
  bit-identical at every σ**, since it never reads the termination at all — assert that rather than
  assuming it, because it is also the statement of what this brief fails to reach.
- **R-cl4-2 — the εᵣ = 1 image reduction still holds.** §3.1's strongest oracle (free space plus one
  image) must survive, at σ = ∞.
- **R-cl4-3 — against kernel A, both surfaces lossy.** A uniform 50 Ω microstrip on both starters:
  kernel B's α_c against `RlgcModel.RMatrix`'s full term, the ground included. The overview's
  measured ground shares are the target: 21.1% (FR-4) and ~11% (GaAs) of the conductor term must
  appear. **This is the gate that says the term is right rather than merely present**, because CL1
  already established the strip's share independently.
- **R-cl4-4 — the four measurements above, tabulated whether they pass or refuse.**
- **R-cl4-5** — `tests/Engine.Tests` green, triaged from the TRX exactly as CL3 did.

## Must NOT

- Widen `ValidatedRhoOverLambdaLayered`, `ValidatedRhoOverLambdaAtHeights`, `PoleLossCeiling`, or
  `CalibratedPathProduct` to make a gate pass. Each was set by a measurement and §5 says so.
- Use the two-sided `coth` for the plane. It is one-sided.
- Change what the quasi-static calibration path supplies without re-running CL3's milestone-0
  measurement. Its γ is the standard's electrostatics, not this brief's termination, and a ground
  term bolted onto it here would be a second loss model with no gate on it.
- Mesh the ground plane. That is a different kernel and R17's ceiling forbids it on any real board.
- Touch CL1's sheet term, CL2's residual, or the port algebra.
- Ship a partial result silently. If two of the four measurements pass and two refuse, the brief
  lands as a refusal with all four numbers.

## On completion

- `src/Engine/Mom/RESOLVED.md` `§CL4`: all four measurements with their numbers, the decision on
  `FrontToBackDb`, the ground-attachment resolution, and — if it refused — the named reason and what
  would have to change, in §7's own style.
- `docs/user/src/reference/mom-engine.md`: either the ground-plane limit CL3 stated is retracted, or
  it stays with its measured size and now also says it was attempted and why it refused.
- **No `CLAUDE.md` edit.** §5's validated-range table and §7's refusal list will both need lines.
  **Report them to the owner by file and line.**
