# Brief MIM-12a — the film's own contribution is a closed form, and the fit is extrapolating it

**Status: MIM-12 step 0 is done and this is what it hands over.** Step 0's question was whether the
SOLVE loses digits it has or the matrix ENTRIES never carried them. It is the entries, and the digits
are lost in the DCIM fit at 1e-2 rather than in double precision at 1e-16 — fourteen decades from
where MIM-12's own text looks. `src/Engine/Mom/RESOLVED.md` §MIM-12 carries the verdict, `HISTORY.md`
§MIM-12 the six tables, and `Mim12KernelFitTests` the gates. **MIM-12's items 1 and 3 are retired by
measurement; its item 2 is right in purpose and one level too low.** This brief is item 2 in its true
location.

**What is already settled, and must not be re-measured:**

- A plate pair's capacitance is a `d/cell` difference of the same-level and cross-level potential
  coefficients — 1/75 on the shipped 0.2 µm film under a 15 µm cell — so it inherits `cell/d` times
  whatever relative error those two carry.
- The exact kernel does not move with frequency at all here: `G_q` at ρ = 2 µm is 5.656193e3 at 1 GHz
  and 5.655920e3 at 10 GHz by direct Sommerfeld integration. The FIT carries 8.3e-5 at 10 GHz and
  2.7e-2 at 1 GHz. **Every bit of the answer's frequency dependence is fit error.**
- It is not the conditioning. The electrostatic instrument is a 32-cell system conditioned at **575**
  and is already sign-inverted at 1 GHz, so iterative refinement and loop-star/A-EFIE cannot act.
- It is not the low-frequency path widening, which must stay: on an ordinary one-level run it takes
  `G_q` from 46 % wrong to 4.3e-4 and every run on a 100 µm slab below ~30 GHz needs it.
- `DcimModel.FitResidual` is blind to all of this — it grades the exponentials against the samples and
  it is the SAMPLES that are of the wrong function. **The acceptance instrument here is direct
  Sommerfeld integration, never the fit's own residual.**

## The finding this brief is built on, measured 2026-09-16

**The cross-level kernel's entire near field is a closed-form image series, and no part of it is
currently extracted.**

`LayeredSpectralGreens.AsymptoticAtHeights` returns zero coefficients for a cross-REGION pairing, and
its own comment is correct about why: a transmission chain carries `e^{−jk_z t}` for the full
thickness it crosses, so the kernel decays and there is no non-decaying term to extract. **Decaying is
not the same as small.** It decays like `e^{−k_ρ d}` with d = 0.2 µm, which is structure out to
k_ρ ≈ 5.0e6 m⁻¹ — and the sampling path the fit sees stops at **1.9e5**:

| | reach |
|---|---|
| default `PathExtent` 300 at 1 GHz | 6.3e3 m⁻¹ |
| widened to `CalibratedPathProduct` = 20 | **1.9e5 m⁻¹ at EVERY frequency** |
| the stack's own 1/H | 9.4e3 m⁻¹ |
| **the film's own 1/d** | **5.0e6 m⁻¹** |

The widening holds `PathExtent·k₀·TopZ` = 20, i.e. `k_ρ,max = 20/TopZ`, so it is frequency-independent
by construction and is **short by 26× at every frequency in the band**. The fit therefore extrapolates
the film's whole contribution from a path that never reaches it. That it lands within 8.3e-5 at 10 GHz
is luck, not margin — `HISTORY.md` §MIM-12 Table 4 is the same lottery drawn out over `PathExtent`.

**And the thing it is extrapolating has a closed form.** Measured with
`SommerfeldIntegral.EvaluateInterior`, forming `A_eff(ρ) = 4π√(ρ²+d²)·G_cross(ρ)`:

| ρ/d | 0.005 | 0.02 | 0.05 | 0.1 | 0.25 |
|---|---|---|---|---|---|
| `A_eff`, d = 0.2 µm | 0.164856 | 0.164854 | 0.164843 | 0.164803 | 0.164528 |

Flat to 3e-4 over a factor of 20 in ρ, and **identical at 1 GHz and 10 GHz to six figures**. The
cross-level kernel near a thin film is `A/(4π√(ρ²+d²))` plus something small. The same probe on the
SAME-level pairing gives `4πρ·G_self → 0.101635`, which is `2/(ε₁+ε₂) = 0.10152` — i.e. exactly what
`AsymptoticAtHeights` already returns there as `(DirectCoefficient + ImageCoefficient) = (1+R_e)/ε_m`.
**One side of the difference has its leading term exact and the other has none of it.**

**It is a SERIES, not one image, and that is measured too.** Peeling the quasi-static transmitted
chain — a charge on the ε₁|ε₂ interface seen on the ε₂|ε₃ interface d away, images at (2n+1)·d with
amplitudes `A·qⁿ`, `A = 2/(ε₁+ε₂) · 2ε₂/(ε₂+ε₃) = 0.177014`, `q = R₁₂R₂₃ = −0.230249` — leaves:

| ρ | 1 nm | 0.2 µm | 1 µm | 5 µm | 15 µm | 40 µm | 85 µm |
|---|---|---|---|---|---|---|---|
| exact `G_q` | 6.559e4 | 4.523e4 | 1.140e4 | 2.216e3 | 6.891e2 | 2.135e2 | 6.73e1 |
| after peeling **1** term | −4.838e3 | −4.574e3 | −2.411e3 | −5.991e2 | −2.499e2 | −1.386e2 | −9.84e1 |
| after peeling **6** terms | **−73.80** | **−73.80** | **−73.83** | **−74.12** | **−74.09** | **−72.69** | −67.39 |

**The remainder is a constant to 0.4 % over four decades of ρ**, and it is 1.1e-3 of the kernel. That
is the shape a DCIM decomposition is built to carry, and it is the same shape the same-level pairing's
remainder already has. One term is not enough — it leaves a factor of 50 of variation — and `q⁶` is
1.5e-4, which is where the table above stops moving.

**The arithmetic that says this closes the defect.** After the peel the fit is asked only for a
remainder that is 1.1e-3 of the kernel, so its 1e-2 relative error becomes 1.1e-5 of the kernel, and
`cell/d` = 75 turns that into **0.08 % of the capacitance**. Even a 10 % error on the remainder gives
0.8 %. Today the fit carries 2.7e-2 of the *whole* kernel and 2.7e-2 × 75 is the 200 % that inverts
the sign.

## What to build

**M1 — the coefficients, derived from the cascade rather than transcribed.** The numbers above are a
quasi-static hand formula and they are the ACCEPTANCE TARGET, not the implementation: what ships must
be the k_ρ → ∞ limit of the same generalised-reflection cascade `AsymptoticAtHeights` already takes
for a same-region pairing, so it stays right when the formulation changes. Two things make that
tractable — at k_ρ → ∞ every region's `k_z → −j k_ρ`, so a chain across several regions collapses to a
single exponential in total crossed thickness; and every round trip below the crossed region has died,
exactly as in the same-region branch.

*Gate:* the derived `(A, q, depths)` reproduce 0.177014 / −0.230249 / depths (2n+1)·d on this stack to
1e-6, the assembled series reproduces `A_eff` → 0.164856 to 1e-4, and the peeled remainder is flat to
1 % over ρ ∈ [1 nm, 15 µm] at 1 and 10 GHz. Direct Sommerfeld is the oracle throughout.

**M2 — the images go where MIM-8's already go.** `InteriorAsymptote` carries one direct and one image
and **must not be widened into a series** — `ShallowImageSplit` already carries a `IReadOnlyList<
ComplexImage>` that `ShallowImageCore.CellPairMean` integrates in closed form over a cell pair, which
is precisely the machinery this needs. The change is that a thin cross-region pairing's removed list
holds EXACT images instead of fitted ones, and the fit is asked only for what is left.

*Gate:* the electrostatic ladder — MIM-8's own 1 V / 0 V instrument on the 60 µm capacitor, with the
run's own fit (`Dcim.ForStackAtFrequency` applied, which is the thing `MimThinLayerTests` does not do)
— reads `C/(ε₀εᵣA/d)` within 5 % at **1, 2, 3 and 10 GHz**, against today's −0.54 / 1.34 / 1.60 / 1.00
(`Mim12KernelFitTests.T2`, whose literals this phase is expected to replace). And on two meshes, since
the answer must stop depending on the mesh as well as on the frequency.

**M3 — the accelerated path, before anything is believed.** MIM-8's own follow-up is the trap and it
is one commit old: `PlanarAimBordered` reads `TermsQ`/`TermsQFar` in three places and a non-empty
removed list must be added back in all of them, or the operator evaluates a kernel with its dominant
term deleted — measured at 0.78 of the block's largest entry when it happened. **A branch whose
off-state is the identity is a branch no existing test exercises**, which is why `MimThinLayerTests.T9`
asserts its fixture is in the regime before it compares anything. Do the same here.

*Gate:* T9's shape on a fixture whose removed list is the new exact one.

**M4 — the full-wave ladder, and the floor MOVES.** Only now is a de-embedded two-port worth running.
Re-run MIM-12's own ladder (one plate pair, one set of feeds and ports, the straddling cell pinned at
40 µm, only the film thickness moving) and re-point `PlanarLevels.FullWaveCellOverSeparation` at
whatever it reaches. **Leave the refusal standing** — a fix that removes it instead of moving it is how
the next regime becomes silent, and MIM-9 wrote that constant expecting exactly this.

*Gate:* `C/(ε₀εᵣA/d)` within 10 % and σ_max(S) ≤ 1 at d = 0.2 µm, cell/separation 200, at 1, 2 and
3 GHz — the rungs that read −0.84 / −1.52 / +5.90 today. The single-level control stays passive at
3.427 / 3.429 / 3.441 fF. The shipped spiral-plus-capacitor `.cem` produces a series resonance, gated
on L and C extracted from the run itself against the closed form and **not on a remembered frequency**
(MIM-12's own warning: the design intent recorded against that layout and the artwork in the file
disagree by 25 %).

## Bit-identity, and it is narrower than it looks

**A same-region pairing must be untouched, bit for bit.** The trigger is a pairing that crosses a
region AND whose crossed thickness the mesh does not resolve — MIM-8's own `ShallowImageCells` test on
the cell against the separation, not "a film is present". MIM-8's airbridge fixture at cell/separation
1.25 is the gate: not one of 4.5 million entries may differ. A run with no cross-region pairing at all
— every single-level run, which is most of them — must not enter this code path.

## Traps, each already paid for once

- **Do not use `FitResidual` as the acceptance instrument.** At 1 GHz, ρ = 0.02 µm, the cross-level
  decomposition reports its own remainder as 8.9e-7; the true remainder is 2.3e4 on a kernel of 6.5e4.
- **Do not tune `PathExtent` instead.** The window in which the capacitance is right is
  frequency-dependent and about 2× wide at 2 GHz (`HISTORY.md` §MIM-12 Table 4). There is no rule to
  draw from it, and the widening that is there is load-bearing for every other run.
- **`PlanarFillSettings.DirectScalarKernel` is a measurement, not the fix**, and it is off. It removes
  the frequency AND mesh dependence and is still 24 % low, because the table it builds is tabulating a
  remainder that still has the film's structure in it — the images it subtracts are the FIT's. **This
  phase should make that path work too**, and if it does not, that is evidence the peel is incomplete.
- **A bonus worth taking deliberately:** extracting the transmitted term also shortens the direct
  Sommerfeld tail, which `EvaluateInterior`'s own comment names as the cost of a thin spacer. That is
  what makes `DirectScalarKernel` affordable as a routine oracle rather than at 50 s a frequency.
- **Scope the general case rather than generalising the formula.** The measured series is for ONE
  crossed region with both points on its bounding interfaces, which is where metal goes. A pairing
  several regions apart, or with a point in the interior of a region, is a different chain — handle it
  or refuse it by name (R-mom-17), and do not ship a formula that is silently wrong for it.
- **The vector half is unmeasured here, not excluded.** MIM-8's finding 1 measured the vector block's
  ENTRY error five decades below the scalar one, and that is a statement about entries rather than
  about the inductive part of the answer — MIM-8's own note says so. Measure it on this fixture before
  deciding; the same extrapolation argument applies to `G_A` word for word.

## Not in this brief

- **Iterative refinement, loop-star and A-EFIE.** Retired by MIM-12 step 0, not deferred. Building one
  anyway would put a mechanism in the tree that the measurement says cannot act here.
- **The separation floor below 0.05 µm** (MIM-8 finding 6). Still a kernel-tier question and still
  four times below the shipped film.
- **The laterally infinite film** (MIM-11). Real, confirmed, and 0.11° of phase — keeping it out is
  what stops the two being confused again.

## Conventions

`brief-em-mim-series.md` §Conventions binds this brief. Write-ups to `src/Engine/Mom/RESOLVED.md` and
every measured table to `HISTORY.md`; **nothing to any `CLAUDE.md`**. Correct in place any sentence
this work makes false — MIM-12 had to correct four, and one of them was a run-visible note quoting a
1 % accuracy at a reader whose capacitor had the wrong sign.
