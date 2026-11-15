# Brief ICF-4 — Planar on a thick, conducting substrate: measure, decide, fix

**Series:** ICF-0 … ICF-6 · **Tag:** `R-icf4-<n>` · **Depends on:** ICF-1 (the run notes)
**Area:** `src/Engine/Mom/PlanarLocalGrid.cs`, `SurfaceMesher.cs`, `Dcim.cs`, `PlanarKernelSet.cs`, `PlanarSolve.cs`, `PlanarFill.cs`,
`SurfaceWavePoles.cs`, `src/Design/Layout/Em/EmSnpProvenance.cs`.

## The gap

On a spiral of about 100 µm over ~750 µm of conducting silicon, with the local grid on (the product default since PMS-7),
a field test found three effects, each repeatable bit for bit:

1. **The result moves with the sweep.** L at 16 GHz is 690 pH in a 1–40 GHz sweep and 617 pH in 16–22 GHz. Pinning the
   mesh frequency to 40 GHz reproduces the first exactly. Cause confirmed by reading: the local grid's base block is
   λ_g/`CellsPerWavelength` at the mesh frequency, which defaults to the sweep's top, and on artwork smaller than one or
   two blocks the partition (`PlanarLocalGrid.Build`, `BaseCuts`) follows that step.
2. **Narrow bands of wrong rows.** 20–250 MHz wide at 2.5, 18.0 and 19.0 GHz (|S21| −16.5 dB between −1.8 and −2.8 dB;
   19.05 GHz shows gain), clean 0.25 GHz away. Gone with σ = 0 on the substrate, and gone with the local grid off. A step
   of ~10 % in L between 1.5 and 2 GHz goes the same way. Cause not measured; the hypothesis is the per-frequency kernel
   fit (`Dcim.FitAtHeights` choosing a candidate by its residual on the integration path, which can accept large,
   nearly cancelling images that are right on the path and wrong in space), picked into by the mesh's ρ floor. Nothing
   checks a device kernel's fit in space today; only a calibration standard's fit is compared with a limit.
3. **Not passive above ~31 GHz.** σ_max up to 1.28 at 40 GHz on the coil pair, the excess on one port; present on both
   meshes and with σ = 0. Two candidates: (a) the grounded uncalibrated edge port is a lumped source ~760 µm tall, 0.3 λ in
   silicon at 34 GHz — the reason `PlanarFill` gives for the de-embedded path stopping grounding above the crossover; (b)
   the TE1 surface-wave pole at cut-on (~30.1 GHz on that stack) next to the branch point.

## Part A — measure (no product change)

Build a synthetic thick-silicon stack in the test tree (BEOL of a few µm over ~750 µm of silicon at ~0.02 S/m equivalent
loss, a ground at the back) and a ~100 µm two-turn spiral with an underpass, close to the field test's geometry, written
for the test. Scratch harness, memory watchdog, results in `src/Engine/Mom/RESOLVED.md` §ICF-4.

**A1 — sweep dependence.** L at 16 GHz with the mesh frequency at 22 GHz, 40 GHz, and the local grid off. Expect the
field test's split. Then the same with base cuts anchored to the artwork's extent (R-icf4-1's candidate, prototyped in the
harness): does L stop moving, and how far is it from the local-grid-off answer?

**A2 — bad rows.** Find one bad window on the fixture (sweep 17.5–19.5 GHz at 50 MHz). At a bad and a clean frequency, for
each height pairing the mesh uses, compare `EvaluateAtHeights` with direct Sommerfeld integration for ρ from 1 to 150 µm.
If the bad frequency's fit is wrong in space and the clean one's is not, the hypothesis holds. If both agree in space, the
kernel is not the cause: stop A2, record it, and narrow by turning the local grid's refinement steps off one at a time.

**A3 — passivity.** At 32 GHz on the lossless stack: (i) fit with `ExtractSurfaceWaves = false`; (ii) return each lead
through a drawn via instead of the grounded edge port. If (i) removes the excess, it is the pole; if (ii) does, it is the
port; if both, record both.

**Decision.** Record which causes are confirmed. A cause not confirmed is not fixed in Part B; it gets the warning in
R-icf4-5 only.

## Part B — fix what A confirmed

**R-icf4-1 — The local grid does not depend on the sweep where the artwork is smaller than its block.** When λ_g/N at the
mesh frequency exceeds the artwork's extent in an axis, the base cuts start from the artwork's extent (or anchor to
feature edges — whichever A1 shows closer to the local-grid-off answer). Above that size nothing changes. `MeshHash`
gains an algorithm term so a result written before reads as stale.

**R-icf4-2 — A device kernel's fit is checked in space.** If A2 confirms it: after `FitAtHeights` picks a candidate, check
it against direct integration at a few ρ across the mesh's range; on failure take the next candidate, and if none passes,
warn naming the frequency and the height pairing. The check's cost is recorded; if it is more than ~10 % of a point's fill
time, it runs only on the conducting-substrate path that A2 showed needs it.

**R-icf4-3 — The tall uncalibrated port.** If A3 confirms (a): above the frequency where the port's height passes a stated
fraction of λ in the substrate (A3 records where the error starts), the run warns, naming the port, its height and that
fraction. A vertical current term for the port is a later brief with its own measurement, not this one.

**R-icf4-4 — The pole at cut-on.** If A3 confirms (b): read the residue-radius handling (`Dcim.cs`) and the pole search
next to cut-on; fix what is wrong there, gated on A3's fixture going passive.

**R-icf4-5 — The passivity warning says where.** Whatever A finds, the non-passive warning (PR #70) names the port carrying
the excess (the top singular vector's dominant port) and, on a substrate thicker than λ/10 at the worst row, says so with
the substrate's electrical thickness, instead of "not identified".

## Gates

Part A has no test gate; its record is the deliverable and the owner reads it before Part B starts.
Part B, one routine test per requirement built:
1. The fixture's L at 16 GHz is the same within 1e-9 relative for mesh frequency 22 and 40 GHz (R-icf4-1); a board fixture's
   mesh is byte-identical to `main`.
2. A fit that fails the space check falls back or warns (R-icf4-2) — on a kernel built to fail, not on a full solve.
3. The warnings of R-icf4-3 and R-icf4-5 name the port.

## Not here

New port constructions. Changing the λ_g/N rule above the artwork's size.
