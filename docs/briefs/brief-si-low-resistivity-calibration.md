# Brief SI-LOW — de-embedding on a low-resistivity substrate

**Tag:** `R-silow-n`
**Area:** `src/Engine/Mom` (`PlanarCalibration`, `PlanarPortCalibrator` in `PlanarSolve.cs`, `PlanarQuasiStaticLine`, `PlanarConductingMedium`)
**Depends on:** [SI-SIG](brief-si-dielectric-conductivity.md), whose conductivity path, reference fixture and warning this brief builds on.

---

## The gap

**On 0.01 Ω·cm silicon, the planar kernel's S-parameters are unreliable from where the skin depth nears the substrate's thickness up to the quasi-static crossover, and a warning names that range instead of an answer.** On a 100 µm substrate that is 633 MHz to 27 GHz, most of a typical sweep.

The cause is γ. Below the crossover, γ = jω√(LC) takes L from the same geometry with the dielectric removed, which has no current in the silicon. Above about 10 Ω·cm that holds, because the magnetic field passes through the silicon to the backside metal. At 0.01 Ω·cm it does not: return current moves into the silicon, adding series loss and lowering L.

A 1 mm, 90 µm line over 2 µm of oxide on 100 µm of silicon at 1e4 S/m, against `testdata/em-si-sigma`:

| f | Skin depth | Quasi-static γ | Measured γ | Palace | |ΔS| as it runs today |
|---|---|---|---|---|---|
| 1 GHz | 159 µm | 1.4 + j168 | −11.6 + j143, marked unusable | 7.1 + j171 | 0.004 |
| 5 GHz | 71 µm | 4.3 + j840 | 145 + j733, marked unusable | 144 + j739 | 0.08 |
| 10 GHz | 50 µm | 9.3 + j1680 | 304 + j1299, marked unusable | 306 + j1309 | 0.37 |
| 20 GHz | 36 µm | 26 + j3359 | 568 + j2305 | 584 + j2332 | 2.36 |
| 40 GHz | 25 µm | not used | 1063 + j4174 | 1111 + j4158 | 0.011 |

**Three things follow from that table:**

- **The fill and C are already right.** Measured γ agrees with Palace to about 1 % in β at 5 and 10 GHz, and with the SI-SIG fixes Z_c = γ/(jωC*) follows. Only γ's source is wrong, so this should not need new physics in the Green's function.
- **The measured route's values at 5 and 10 GHz are good, and it marks them unusable.** That flag, `UsableLoDegrees` to `UsableHiDegrees`, protects the two-line extraction's branch and conditioning. Whether it is too strict here or is correctly warning about something these two points got right by chance is unknown.
- **Neither route is right at the bottom.** At 1 GHz quasi-static β is within 2 % but its α is 5× low, and the measured α is negative. A negative α also appears at 10 Ω·cm at 1 GHz (−73 + j99). The measured route cannot simply replace the other across the band.

## Why it may be more than moving the crossover

**The risk is the bottom of the regime.** Measured γ needs an electrically long separation, and the standards' length grows as the frequency falls. That is why the quasi-static route exists at all: a 3.8 mm line swept from 100 MHz once needed a 161.5 mm standard. On this substrate a slow wave helps, since β is several times the lossless value, but a sweep reaching down to the onset may still be out of reach of the measured route.

**So there are two candidates, and R-silow-0 decides between them or combines them.**

- **A. Use the measured route in the skin regime.** Move the plan's crossover down for a conducting substrate. Size the separation from a β estimate that knows about the conduction, since `EstimateBeta` reads only εᵣ, and re-examine the usability interval there.
- **B. Give the quasi-static route the missing series term.** Keep γ = √((R + jωL)(jωC)) and supply R(ω) and L(ω) for current in the silicon, as CL7 supplies the ground plane's term. A one-slab closed form does not obviously exist for a line over oxide over a lossy layer over a plane. It would be read from a 2D magnetostatic solve with σ, or from a low-frequency evaluation of the layered Green's function that already holds the skin effect.

**The likely shape is A above a few GHz, with the band between the onset and there as the hard part.** If B is not cheap, an acceptable outcome is A with a narrower warning that names what remains.

## What to build

### R-silow-0 — measure before designing

No code in the product. A throwaway harness, as in SI-SIG's R-sisig-0, on the fixture line.

- **The measured route's values and flags** at 1, 2, 3, 5, 10 and 20 GHz at 1e4 S/m, with the separation it chose, βΔℓ and the conditioning: which standard each point used, and why each is marked unusable.
- **The same at 100 and 1000 S/m,** which are unmeasured. The warning's bound, a skin depth under twice the thickness, comes from 1e4 S/m alone. Extend the Palace reference to these two.
- **The negative α at the bottom of the band,** at 10 and 1e4 S/m: whether it is a branch, a sign or a conditioning problem.
- **Standard lengths and unknowns** for a sweep reaching the onset, against the ceiling.
- **A rough cost for candidate B:** one point, and how far a 2D solve's R and L land from Palace's.

Report for each resistivity which route serves which range. **Stop and report**, with a recommendation between A, B and both. Scope it short: a line, few points, and no whole-board runs.

### R-silow-1 — the route

Per R-silow-0's result. σ = 0 is bit-identical, which SI-SIG's gate 1 tests already hold, and 10 Ω·cm and above keep the numbers SI-SIG recorded.

- **One decision, in one place.** `PlanarSeparationPlan.IsQuasiStaticAt` remains the only thing that says which path a point takes.
- **The branch seed.** SI-SIG stops a quasi-static point in the skin regime from seeding the first measured point. Whatever replaces that must keep a sweep crossing the routes on the right 2π branch, tested as a sweep and not as single points: a single point hides this defect.
- **Standards stay within the ceiling,** or the run refuses by name, on the pattern of the existing separation refusal.

### R-silow-2 — the warning

`PlanarConductingMedium.Findings` narrows to what is left. If nothing is left, the warning goes and the regime stays in the run's note. **A range that remains is still named.** Update `stackup.md` and `mom-engine.md`, whose sections on a conducting substrate state today's range and its measured numbers, and regenerate those pages.

### R-silow-3 — the reference

Extend `testdata/em-si-sigma/reference.csv` to any added conductivity and frequency, with its README. `ConductingDielectricReferenceTests` currently asserts that 5 GHz at 0.01 Ω·cm is wrong, and that assertion is inverted here.

**Findings go in `src/Engine/Mom/RESOLVED.md`.**

## Order and gates

R-silow-0, and stop to report. Then 1, 2 and 3.

| Gate | Holds |
|---|---|
| 1 | Every technology with no dielectric σ, and every result at 10 Ω·cm and above: unchanged to the bit, and no `.snp` provenance hash moves |
| 2 | At 0.01 Ω·cm: within the tolerance SI-SIG's gates use, against the reference, across the range this brief claims |
| 3 | α > 0 at every published point on a conducting substrate |
| 4 | A sweep crossing the routes gives the same answer, within that tolerance, as each point run alone |
| 5 | Any range still not served is a warning naming it. Never a clean result outside it |
| 6 | At 100 and 1000 S/m: within that tolerance, or inside a warning's range |

One test per claim, under the ~5 s `Category=Benchmark` threshold. No new timing benchmarks, and solvers do not run in CI: references are committed.

## Not in this brief

- **1 GHz on a thin-oxide stack at any σ.** |ΔS| is about 0.23 at σ = 0, with a peel amplification of 8e5. That is the de-embedding's own low-frequency limit, recorded in `RESOLVED.md` §SI-SIG, and it needs its own measurement. Gate 3's negative α at 1 GHz is in scope; the peel is not.
- **A coupled-port calibration group on a conducting substrate,** which warns across its sweep. It would need a per-frequency C and the imaginary part of Z_c,m in `PlanarModalMedium`.
- **Palace wave ports on conducting silicon** and any meshing of skin depth in the 3D route.
- **A junction, depletion or carrier model,** temperature dependence, a doped profile, and a via or contact into the silicon, as in SI-SIG.
