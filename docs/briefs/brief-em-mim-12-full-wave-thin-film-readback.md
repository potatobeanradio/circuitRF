# Brief MIM-12 — the capacitor the fill gets right and the solve cannot read back

**Status: MIM-8 fixed the fill, and the shipped MIM capacitor still cannot be EM-simulated.** MIM-8's
own gate is an ELECTROSTATIC one — `PlanarFill.ScalarPotentialMatrix` driven by a 1 V / 0 V
instrument with no port in it — and it passes, today, on the user's own plate size at a mesh as
coarse as the run's (cell/separation 150 against the run's 200, which is inside the 300 MIM-8's own
ladder holds to 1 % over):

| plate | d | mesh | cell/sep | `C/(ε₀εᵣA/d)` |
|---|---|---|---|---|
| 60 × 60 µm | 0.2 µm | 2 across | 150 | **1.0032** |
| 60 × 60 µm | 0.2 µm | 4 across | 75 | **1.0032** |
| 60 × 60 µm | 0.2 µm | 8 across | 37.5 | **1.0032** |

**The de-embedded two-port of the same capacitor is not close to that, and is not passive.** Measured
2026-09-16 on an ISOLATED capacitor — the `KIT_MIMCAP` stack (68 × 68 µm Metal1 bottom plate,
60 × 60 µm MIM Metal top plate, MIM via, Metal2 strap, via back to Metal1), fed by 400 µm of clean
16 µm Metal1 line at each end so no other metal is anywhere near the calibration run. Series arm
taken as `−1/Y₂₁`:

| f | σ_max(S) | `−1/Y₂₁` | what 1.0838 pF is |
|---|---|---|---|
| 1 GHz | **1.7322** | −201.2 **+** 174.0j Ω | 0 − 146.8j Ω |
| 2 GHz | 1.0443 | −2.6 **+** 48.5j Ω | 0 − 73.4j Ω |
| 3 GHz | 1.4115 | −17.3 − 8.3j Ω | 0 − 48.9j Ω |

The reactance has **the wrong sign** — the capacitor reads as an inductor — and the resistance is
**negative**, by 200 Ω. This is what reaches the user as "S(1,1) > 1 and no resonance": a series LC
whose C has become an L has no series resonance anywhere.

## Four things it is NOT, each excluded by measurement

**1. Not the mesh.** The same structure at 2, 4 and 8 cells across is the same wrong answer, and
refining makes it very slightly worse:

| cells across | σ_max at 1 GHz | `−1/Y₂₁` at 2 GHz |
|---|---|---|
| 2 | 1.7322 | −2.64 + 48.47j |
| 4 | 1.7439 | −2.67 + 49.15j |
| 8 | 1.7491 | −2.76 + 49.81j |

This is a **converged wrong answer** — `MimThinLayerTests`' own header calls that shape "L8c's
converged-looking-but-wrong mode". No mesh control reaches it, and the reason is structural as well
as empirical: the straddling cell over the plates is set by the plate's own width and the detail
floor, and it reads 40 µm at 2 cells across and **36.4 µm at both 4 and 8**. A wide plate meshes
coarse however hard the narrow metal is refined.

**2. Not the ports, the feeds or the de-embedding.** The control is the same file with the top plate,
the MIM via and the Metal2 strap deleted — **identical Metal1 artwork, identical feeds, identical
ports, identical technology**, differing only in whether a second level exists:

| f | σ_max(S) | gap capacitance |
|---|---|---|
| 1 GHz | 0.99860 | 3.427 fF |
| 2 GHz | 0.99853 | 3.429 fF |
| 3 GHz | 0.99843 | 3.441 fF |

Passive at every point, and a fringing capacitance flat to **0.4 % over 3:1 in frequency**. The
de-embedding machinery is in excellent health; it is handed a bad DUT matrix. **The run's own NOT
PASSIVE sentence blames the peel, and on this evidence that is a misdiagnosis** — see MIM-9.

**3. Not the electrostatic fill.** The table at the top. MIM-8 did what it says it did.

**4. Not a broken kernel producing an active operator.** `(Z + Zᴴ)/2` is positive semi-definite at
every rung measured (60 µm plates, N = 120, d = 0.2 … 2 µm, 0.5 and 2 GHz). The fill is not the thing
manufacturing gain.

## What it IS, and the arithmetic that says so

**It is a dynamic-range failure, and the numbers are not marginal.** Two measurements:

- **The loss lives 11 decades below the matrix.** On the 60 µm plate pair at 0.5 GHz, N = 120,
  d = 0.2 µm: the smallest eigenvalue of `Re(Z)` is **5.7e-8** against a largest of **4.06e3** — a
  ratio of **1.4e-11**. Everything that makes the answer passive is in that bottom sliver.
- **`cond(Z)` is 1.2e8 at 2 GHz and 1.44e9 at 0.5 GHz** on the same fixture, and it rises as the
  frequency falls. That is the MPIE's low-frequency breakdown, which this repository already
  documents as a property of `Z` (`src/Engine/Mom/RESOLVED.md`, P7's finding 3: *"the vector term
  vanishing like ω against a scalar term growing like 1/ω"*). Nothing implements a loop-star,
  loop-tree or A-EFIE remedy for it.

On top of those two, **the capacitance itself is an O(d/h) difference**: the plate capacitance is
what is left after the self and cross scalar blocks nearly cancel, and at the shipped mesh
d/h = 0.2/40 = **1/200** (1/182 once the mesh is refined, which is the wrong direction). Three
losses compound — `cond(Z)` ≈ 1e9 takes about nine digits, the
d/h cancellation takes two more, and the peel then divides by a₂₁². Double precision does not have
enough digits left, and what comes out is noise with the right order of magnitude.

**The ladder over the film thickness is the cleanest single statement of it.** Same plate, same
artwork, same ports, and the straddling cell pinned at 40 µm on every rung, so cell/separation is the
only axis. `MIM Dielectric`'s thickness moves and the `Air` band above it absorbs the change, so
Metal1-to-Metal2 stays at the process's 3 µm and the rest of the stack does not move either:

| d | cell/sep | σ_max | `C_extracted / (ε₀εᵣA/d)` at 1 / 2 / 3 GHz |
|---|---|---|---|
| 2 µm | 20 | 0.998 (passive) | 1.12 / 1.13 / 1.15 — **correct**, the excess is plate fringing |
| 1 µm | 40 | 0.998 (passive) | 1.27 / 1.05 / 1.13 |
| 0.5 µm | 80 | 1.04 – 1.09 | −0.93 / −1.02 / −1.10 — **magnitude right, sign inverted** |
| 0.2 µm | 200 | 1.04 – 1.73 | −0.84 / −1.52 / **+5.90** — incoherent |

**The engine works down to about cell/separation 40 and falls apart by 80.** The shipped MMIC
technology's own 0.2 µm film puts every capacitor on it at 200. And below ~1 GHz the answer stops
being smoothly wrong and becomes noise — σ_max on the same structure, at 100 MHz through 800 MHz in
100 MHz steps, reads **1.11 / 1.00 / 1.28 / 1.00 / 1.00 / 1.63 / 1.79 / 2.04**. It does not converge
towards the electrostatic answer as ω → 0; it disintegrates.

## The correction — one experiment, then three candidates, cheapest first

The acting quantity is the pair's own `d/h`, so **every remedy below is inert on a run whose mesh
resolves its own gaps**, which is MIM-8's own bit-identity discipline and must be kept.

**Step 0, and it decides which of the three is even applicable.** The measurements above cannot
separate two different losses, and the remedies for them are different:

- **(a) the SOLVE loses digits to conditioning** — `cond(Z)` ≈ 1e9 — which iterative refinement
  recovers completely, because the information is still in the matrix;
- **(b) the matrix ENTRIES never carried the small quantity** — a number that is 1e-11 of the norm has
  about five significant digits left in a double, and **no refinement of the solve can add a digit the
  fill never computed**.

The experiment: fill `Z` once, solve, then re-solve with the residual accumulated in double-double,
and separately re-fill `Z` in extended precision and solve that in double. If the first moves the
answer, it is (a) and item 1 is the fix. If only the second moves it, it is (b) and item 1 is
useless — go to item 2. **Do this before committing to anything below.** It is a scratch harness on
the N = 120 plate pair, not a phase.

1. **Iterative refinement on the solve, in extended precision.** Applicable only if step 0 says (a).
   One or two refinement steps on the existing factorisation, residual accumulated at higher
   precision. By far the cheapest thing on the list, and it would pay off on every ill-conditioned
   run rather than only this one.
2. **Congruent facing cells, and the difference in closed form.** The capacitance is a difference of
   two nearly-equal potential coefficients. Force the two levels' cells to be exactly congruent
   where they overlap (they are already on one shared tensor grid — L8b's D8) and compute
   `P_self − P_cross` **as one closed form** rather than as two numbers subtracted.
   `ShallowImageCore.CellPairMean` and `RectangleIntegrals.Corner0AtComplexOffset` already integrate
   the right kernel at a complex depth; what is missing is taking the difference before rounding, not
   a new integral. This removes the d/h cancellation entirely and is exact where the error is, and it
   is the remedy for (b). **Note it addresses the SCALAR half only** — MIM-8's finding 1 left the
   vector half untreated on the grounds that its entry error is five decades below the scalar one,
   which is a statement about entries and not about the inductive part of the answer. If step 0 says
   (b) and item 2 does not close it, the vector half is the next place to look.
3. **A low-frequency-stable formulation** — loop-star/loop-tree or A-EFIE on the affected block.
   This is the textbook cure for the `cond(Z)` half and it would pay off far beyond MIM capacitors:
   every MMIC run below a few GHz is in the same regime (`k·a` ≈ 2e-3 on a 30 µm cell at 2 GHz over
   GaAs). It is also much the largest piece of work here, and it should not be started until 1 and 2
   have been measured, because if 1 and 2 close the gap it is not needed for this defect.

## Gates

- **The ladder above, re-run.** `C_extracted/(ε₀εᵣA/d)` within 10 % and σ_max(S) ≤ 1 at d = 0.2 µm,
  cell/separation 200, at 1, 2 and 3 GHz — the rungs that read −0.84 / −1.52 / +5.90 today.
- **The control must not move.** The single-level gap capacitor stays passive and stays at
  3.427 / 3.429 / 3.441 fF.
- **Bit-identity where no gap is unresolved.** MIM-8's airbridge fixture at cell/separation 1.25:
  not one entry differs.
- **The user's own structure resonates.** The spiral-plus-capacitor `.cem` produces a series
  resonance with σ_max(S) ≤ 1 across the band. **Do not gate on a remembered frequency.** MIM-8's
  note records a design intent of 2.48 GHz against a 3.8 nH spiral; the artwork in the file this
  brief was written from is a 3-turn, 120 µm-inner, 10/8 µm spiral, for which modified Wheeler gives
  ≈ 2.5 nH — which against the kit's own 1.0838 pF resonates near **3.1 GHz**, not 2.48. Gate on L and
  C extracted from the run itself against the closed form, not on a number from another layout. (An inductance
  read off the CURRENT non-passive run is not evidence of anything and must not become the target.)
- **The floor MOVES; it does not disappear.** MIM-9 built R-emsev-4's refusal at today's measured
  floor of cell/separation 40. Re-point that one constant at whatever this brief reaches and leave the
  refusal standing — a fix that removes the refusal instead of moving it is how the next regime
  becomes silent.

## Not in this brief — and by this point, already done

**LAST of the round by design, because it is the only one that is weeks rather than days and because
the three before it each remove a reason to rush this one.** Do not start it until they have landed.

- **The diagnostics — MIM-9, done.** Until this brief lands, a run past the floor refuses instead of
  publishing a sign-inverted capacitor. That is what makes it safe to take the time this needs.
- **A working route for a designer — MIM-10, done.** Nobody is blocked waiting for this.
- **The laterally infinite film — MIM-11, done.** It is real and confirmed and it is **not** what
  breaks this: 0.11° of phase on S₁₁ and 2.7 % on a fringing capacitance. Keeping it out of this
  brief is what stops the two being confused again.
