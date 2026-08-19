# `src/WBond/Mom` — resolved briefs (detail, off the CLAUDE.md growth path)

Completed work's detail lands here instead of `CLAUDE.md`, which stays for durable, still-true
conventions only. Same pattern as `src/Engine/Mom/RESOLVED.md` and `src/Ui/DataDisplay/RESOLVED.md`.

---

## Progress and cancellation through the kernel (2026-08-19)

The MoM kernel now reports through `WBondRunControl`/`WBondProgress` (`src/WBond/WBondRunControl.cs`,
namespace `CircuitRF.WBond` — it serves the lumped path too, not only `Mom`). The UI half and the
reasoning are in `src/Ui/RESOLVED.md`; three things belong here.

**`CircuitRF.Engine.RunControl` is unreachable from this project and the duplicate is deliberate.**
`src/Core` references `src/WBond` and `src/Engine` references `src/Core`, so `WBond → Engine` is a
cycle. The shape is copied field for field so the UI reporter is the same code for an EM run and a
wirebond run. **Do not "fix" this by adding a project reference** — it does not compile, and the leaf
status is what lets `src/Core` reference this project at all.

**Every long loop takes it, including the two Choleskys.** `SegmentInductance.Fill`,
`NodePotential.Fill`, `CholeskyFactor.Factor`/`InvertInPlace`, `MomAssembly.Build`,
`WireMomSolver.Create`/`Solve`. `CholeskyFactor` takes `(run, stage)` and reports **only when `stage`
is non-null** — ticking without owning a stage would advance whatever counter the caller had already
opened, which is how a bar ends up past its own denominator. Cancellation is honoured whenever `run`
is non-null, labelled or not: an inverse at N_s = 4,800 is tens of seconds and a Stop must not have to
wait for it. `InvertInPlace` counts `2n` column-steps because two of its four passes are cubic and the
other two are O(n²) transposes.

**The throttle is a CAS, not a `Stopwatch.Restart()`.** Both fills tick once per row from every worker
of a `Parallel.For`; a stopwatch restart admits one report per thread per interval, a
compare-and-swap on one timestamp admits exactly one. `MomProgressTests.TickStage_IsExactUnderParallelTicking`
holds the counter exact under 10,000 concurrent ticks.

## `brief-wbond-mom-w1-mesh-and-matrices.md` — the segment mesh and the frequency-independent matrices (2026-08-18)

Kernel W1's first half: the segment/node mesh, `L`, `P`, `A`, `R`, `Ã`, `G`, `K̃`, `W`, `H` and the
machinery for `D(ω)`. **No solve, no S-parameters, no UI, no project reference added** — `src/WBond`
is still a leaf. Everything is under `src/WBond/Mom/`, namespace `CircuitRF.WBond.Mom`, with 37
routine tests in `tests/WBond.Tests/Mom/` running in **200 ms** and 3 `Category=Benchmark` methods.

Nothing in `src/Core`, `src/Engine`, `src/Ui`, or the existing `src/WBond` physics was touched.

### The headline: the FILL is not the cost any more, the ASSEMBLY is

`InductanceMatrix`'s own remarks record WB13 — "the fill is the bottleneck, not the solve" — measured
at the wire basis. **That inverts at the segment basis, and by two orders of magnitude.** Release,
Apple Silicon, 40 wires × 24 segments (N_s = 1,040, N_n = 1,080, N_r = 1,020):

| | ms | note |
|---|---|---|
| `L` fill | 22.6 | |
| `P` fill | 8.2 | 0.36 × the `L` fill |
| **§7 step 1** — cholesky(P) | 129 | |
| **§7 step 1** — 1,020 solves against P | **726** | |
| **§7 step 2** — cholesky(G) | 101 | |
| **§7 step 3** — 1,040 solves against G | **656** | |
| **§7 step 4** — `K̃ = Ã Y` | **1.0** | |
| assembly total | **1,650** | **54 × the two fills together** |

At 200 wires (N_s = 5,200, N_n = 5,400): `L` 0.36 s, `P` 0.10 s, **assembly 313 s**.

**The brief's §7 is wrong about which step costs.** It calls step 4 "the largest single one-time cost
in the kernel — roughly two dense factorisations' worth". Step 4 is the *smallest* step, by nearly
three orders of magnitude, because `Ã` has exactly two non-zeros per row: `K̃ = Ã Y` is a subtract of
two rows of `Y` per output row, O(N_s²), not the O(N_s² N_r) a dense GEMM would cost. What actually
costs is the two batches of triangular solves — N_r of them against `P` and N_s against `G` — each
roughly 6 × a Cholesky factorisation.

**For WM-3, the single highest-value item is that both batches are embarrassingly parallel over their
right-hand sides.** They were left serial here on purpose: the brief sends "any measured optimisation"
to WM-3, and the measurement is more useful than the fix. On this machine that is an ~8 × available
for a five-line change, and it is worth more than anything else named in WM-3.

### §0.3 item 1 held, exactly as stated

**The segment-basis `L` fill costs what the wire-basis fill costs over the same filaments: 47.2 ms
against 45.7 ms, 1.03 ×.** The comparison is against a design whose polyline vertices *are* the
mesh's segment endpoints, so both fills walk the identical 1,040 filaments — a wire-basis fill over
the design's own 7-point wires would be 240 filaments and would prove nothing.
`MomCostTests.TheSegmentFillCostsWhatTheWireBasisFillCostsOverTheSameFilaments` holds it.

### §9.3's two identity gates: one is exact, one is not, and the reasons are different

Both were written first, as the brief asks, and both immediately paid for themselves.

**The inductance identity is exact where the kernel is a closed form and 1.5e-10 where it is not.**

| design | identity vs `InductanceMatrix.Block` | subdivision invariance |
|---|---|---|
| 4 straight parallel wires, 2 arrays | **5.2e-15** | **4.7e-15** |
| 4 ball bonds, 2 arrays | **1.5e-10** | **1.7e-10** |

The brief asks for 1e-12 on both. **It is not reachable on a curved wire, and the mesher is not the
reason.** Two amplifiers stack, both belonging to the existing kernel:

1. **`Grover.Skew` loses ~3 digits on a nearly-parallel, distant pair as the pieces shorten.** Pinned
   on *two whole filaments*, split and summed, with no mesher involved: a parallel far pair is
   additive to 2.1e-16 at every subdivision, a **1°-skew** far pair is additive to 4.1e-16 at k = 2
   and 4 and then **2.3e-13 at k = 8**. Its four-term `Atanh`/`Atan2` difference simply cancels harder
   as `l/d` falls.
2. **The cross-array blocks of a wire over a plane are a 130 × cancellation** between the direct and
   image halves (`|direct| = 6.3e-11` against a sum of `4.8e-13`). 2e-13 × 130 is 2.6e-11, and 1.5e-10
   is that with the rest of the pair loop's rounding on top.

The tests gate the two halves separately at 1e-11 — where they comfortably sit (direct 2.4e-13, image
1.0e-12) — so the attribution is a measurement rather than a story, and the combined gate is 1e-9.

**The charge dual is not an identity on a curved wire at all, and the cause is isolated.**

| design | `Bᵀ P_node B` vs `PotentialCoefficients` |
|---|---|
| 4 straight parallel wires, 2 arrays | **6.0e-15** |
| 4 ball bonds, 2 arrays | **4.8e-3 at 6 seg/wire, 5.4e-3 at 24** |

The brief asks for 1e-10. **`PotentialCoefficients.Kernel`'s near branch for *non-parallel* filaments
is a fixed 4-point Gauss-Legendre rule, and a fixed-order rule on a near-singular integrand is not
additive under subdivision.** Isolated by holding the geometry fixed and varying only the rule's
order on this design's own self block:

| Gauss order | node-basis vs wire-basis |
|---|---|
| **4 (shipped)** | **5.4e-3** |
| 8 | 6.1e-4 |
| 16 | 4.4e-5 |
| 32 | 2.0e-5 |

So it is the **order**, not the GMD floor (which contributes the ~2e-5 residual) and not the half-cell
construction or either image sign — those are all pinned independently and are exact.

**The finer mesh is the more accurate of the two.** Against the order-32 value, the node basis at 24
segments is 0.03 % off and the *wire* basis is 0.56 % off. The disagreement is dominated by the
reference being wrong, not by the thing being tested. `PotentialCoefficients` is untouched; raising
its rule's order is a WM-3 question, and it is likely to tighten the §9.7 convergence below at the
same time.

### The near/far threshold: 3.5 was measured for wire-length cells and is outside its own target here

The brief says the threshold is 3.5, measured, and "**do not re-tune it here** … it was measured
against *wire*-length cells; §6.4 says what to do about that." **§6.4 does not exist in the brief** —
§6 is `D(ω)` and has no subsections — so the question was answered by measuring.

Swept on a 40-wire / 10-array ball-bond design at 24 segments per wire (N_s = 1,040), worst per-wire
self-capacitance error against an all-near reference — the same 0.1 % target and the same
"smallest value inside it" rule `PotentialCoefficients` used to pick 3.5:

| factor | 2 | 3 | **3.5** | **4** | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|
| worst self-C error | 0.508 % | 0.173 % | **0.121 %** | **0.0675 %** | 0.0400 % | 0.0218 % | 0.0130 % |

**3.5 is outside 0.1 % at half-cell scale; 4.0 is the smallest swept value inside it.** So
`WireMomSettings.FarThresholdFactor` defaults to **4.0** and `PotentialCoefficients.FarThresholdFactor`
is left at 3.5, which is correct for its own cells. The extra half costs nothing measurable: the
N_s = 1,040 fill is 3.1 ms at both, because the near branch for the parallel pairs that dominate a
bond array is `Grover.ParallelScalarKernel`, a closed form. Forcing the accurate kernel everywhere
costs 26 ms, so the far branch is still earning its keep.
`MomAssemblyTests.TheShippedFarThreshold_KeepsSelfCapacitanceInsideOneTenthOfAPercent` holds it, and
asserts 3.5 really is outside — a gate that would pass with either value holds nothing shut.

*One caution for whoever revisits this.* Measured on the **whole** `C` matrix rather than its
diagonal, every threshold from 2 to 8 reads 0.3–1.0 % and does not fall monotonically. That is not the
threshold: the off-diagonal entries between distant arrays are themselves near-cancellations, so a
tiny absolute error is a large relative one there. The self capacitance is the quantity to sweep
against, and it follows `≈ 1/(24 f²)` almost exactly (0.121 % predicted 0.136 % at 3.5; 0.0130 %
predicted 0.0170 % at 7).

### §9.7 — the convergence table, and 24 survives with a stated error

Single ball bond over ground, `Σ_{m,n}(P⁻¹)_{mn}` — the wire's total capacitance to the plane:

| segments/wire | N_s | C (F) | change |
|---|---|---|---|
| 6 | 8 | 4.3185e-14 | |
| 12 | 14 | 4.3289e-14 | +0.240 % |
| 24 | 26 | 4.3492e-14 | +0.467 % |
| 48 | 51 | 4.3621e-14 | +0.296 % |
| 96 | 99 | 4.3655e-14 | +0.078 % |

Monotone, and the 24 → 48 change is below the 12 → 24 one, which is the brief's stated gate. **The
default stays 24, and the honest reading is that it carries ~0.4 % of capacitance error** against the
extrapolated limit — comfortable for a bond-wire shunt arm, and worth stating rather than implying
convergence. Note the ladder is not nested (each target is its own partition, and the polyline's own
6 vertices set a floor of N_s = 8), which is why the step sizes are not monotone.

The convergence is slow at the same 0.3–0.5 % scale as the quadrature effect above, and on the same
kind of cells. **They are plausibly the same defect**, which would make raising `GaussKernel`'s order
a double win. That is a hypothesis supported by the order sweep, not a measured claim, and it belongs
to WM-3.

`L` needs no convergence test — subdivision invariance proves it is exactly invariant.

### §2's algebra: correct as written, with one size correction and one prediction correction

Implemented as specified. §2.6's four checks:

1. **Symmetric by construction** — `H`, `K̃` and `G` are each symmetrised explicitly on assembly.
   None is symmetric bit-for-bit, because each is built from triangular solves that visit its two
   halves in different orders, so reciprocity is made structural here rather than assumed downstream.
2. **`null(K̃) = null(Ãᵀ)`, and its dimension is the loop count `W − M`** — confirmed at 0, 2 and 4
   loops. Pinned from both sides without an eigensolver: the explicit loop vectors (+1 along one wire,
   −1 along another of the same array) give `Ãᵀz = 0` and `zᵀK̃z = 0` to 1e-12, so nullity ≥ count;
   deflating `K̃` by all of them and finding a healthy Cholesky gives nullity ≤ count.
   **"Cholesky threw" is not a usable rank test** — an under-deflated matrix is singular in exact
   arithmetic and factorises anyway with a pivot ~1e-16 of the scale, and a first version of this test
   passed at 2 loops and failed at 4 for exactly that reason. The committed test compares the smallest
   pivot instead.
3. Purely capacitive DC limit — WM-2's, once there is a `Z_port` to take it of.
4. **`Eᵀ G⁻¹ Ãᵀ = (Ã G⁻¹ E)ᵀ = Wᵀ`** — confirmed against an independently formed `Ã G⁻¹ E`, so `W`
   is computed once as a slice of `Y`.

**The eigendecomposition shortcut does not transfer**, as §2.5 says: `M̃(ω) = (jω)²L + jω D(ω) + K̃`
is a quadratic pencil whose three matrices are not simultaneously diagonalisable. Recorded in
`MomAssembly`'s own remarks so the next reader of `ImpedanceReduction`'s comment does not chase it.

**§8's memory arithmetic understated the peak, and the missing term is the Cholesky factor.**
`CholeskyFactor.Factor` does not modify its input, so `P` and its factor are alive together, and so
are `G` and its factor. The 200-wire run predicted 825 MB against a **1,305 MB** working set. With the
factors counted the prediction is 996 MB, and the report now spells out four residencies rather than
three (fill / reduce / assemble / WM-2's solve). WM-2's own `M̃` is included, as the brief requires —
a report that stops at this brief's own matrices would be a report that lied.

### Two places the brief contradicts itself, both about the ground plane

§3.4 requires the mesher to refuse a design with no ground plane (RW13 — a port carries an explicit
reference conductor), and §9.10 tests that refusal. But **§9.1 specifies its oracle with the ground
plane off**, and **§9.6 asks for `P`'s definiteness "with images on and with images off"**. Neither
design can be meshed.

Resolved by keeping the refusal — it is the one both the design note and §9.10 ask for — and applying
each oracle to the half of the fill it is actually about: §9.1 sums the **direct** half of `L` on a
mesh that does have a plane, and §9.6 factors both `P` and the direct-only potential matrix over the
same cells, which is numerically what "images off" means. Both are noted in the tests themselves.

### Smaller things worth having on paper

- **The two sign rules were copied verbatim and are both tested by being broken.** Flipping `L`'s
  image to minus and `P`'s to plus each produces a finite, plausible, non-NaN number, and each
  inverts a monotonicity: a subtracted current image *raises* self inductance, an added charge image
  *lowers* capacitance to the plane. Both are asserted, because "the gate failed" alone would not
  distinguish a sign error from a broken mesh.
- **Subdivision reproduces the authored endpoints exactly.** `a + (b−a)·t` does not return `b`
  bit-for-bit at `t = 1`, so the two ends are special-cased. Without that the invariance gate would
  have been comparing meshes whose wires end in slightly different places.
- **The clamp is reported, never absorbed.** `MaxSegmentsPerWire` walks the effective target down
  rather than truncating, because the count is `Σ_i ceil(len_i / maxLen)` over the polyline and not
  the target — a wire with unevenly spaced vertices rounds up on every one of them. Wires that hit it
  are counted in the report and named in a warning.
- **The ceiling refusal names three remedies and the test checks each one BINDS.** Not that the
  message mentions them: it re-meshes at the segments-per-wire value the message names and asserts
  that value really does fit, and meshes the worst single array and asserts the quoted number is that
  array's. `em-refusal-must-name-a-binding-remedy` is the memory this comes from.
- **`Predict` allocates nothing.** RW2 wants the number before the solve, and the repository has
  already paid once for a ceiling that predicted, passed, and threw twenty minutes later.
- **`D(ω)` is additive over a wire's segments exactly**, at 1e-12 relative against
  `ImpedanceReduction.WireInternalImpedance`, at 0.1, 10 and 40 GHz. The Bessel evaluation is cached
  per distinct `(radius, sigma)` — one entry for an array of identical wires, however many thousand
  segments it meshes to.
- **The proximity warning (RW17) is broad-phased through `WirePairSweep`**, which already exists, so
  it is not an O(W²) exact-distance sweep at 600 wires.

### The Benchmark tier this brief adds

Three methods in `MomCostTests`, **~19 s together in Debug**, measured alone with `--no-build`:

| method | what it measures |
|---|---|
| `TheSegmentFillCostsWhatTheWireBasisFillCostsOverTheSameFilaments` | §0.3 item 1, as a ratio (0.23 s) |
| `TheChargeFillIsCheaperThanTheInductanceFill_AndTheAssemblyDominatesBoth` | the P/L ratio and the assembly's order of growth (12 s) |
| `ThePredictedPeakIsTheRightSize` | §8's arithmetic against `GC.GetTotalAllocatedBytes` (6 s) |

The 200-wire (N_s = 5,200) figures above were taken **out of band**, not committed as a test: 313 s of
assembly in Release is more than the whole opt-in tier's current budget deserves for one point.

Every other test in this brief is routine and the whole `Mom` folder runs in **200 ms**. Nothing here
crosses N_s = 250 except the three Benchmark methods.

---

## `brief-wbond-mom-w2-solve-and-nport.md` — the solve, the N-port, and the analytic cross-check (2026-08-18)

WM-1's frequency-independent matrices turned into an N-port: `WireMomSolver` (`M̃(ω)`, one dense
complex LU per frequency, the port reduction), `WireMomResult` (Z/Y per point, plus the notes), the
`SeriesArmImpedance` accessor, a **Model** option on the Touchstone export, and a
**Design ▸ Compare Distributed Model…** dialog. 34 routine tests in `tests/WBond.Tests/Mom`
(the whole `Mom` folder now runs in **2 s**) and 8 in `tests/Ui.Tests`. **No Benchmark tests added.**

Nothing in `src/Core`, `src/Engine` or `RfCore` was touched, and `src/WBond` is still a leaf project.

### 1. §6.6's correlation table — the answer to the question that motivated the tranche

4 wires in 2 arrays, 10 mil pitch, 100 mil span, 30 mil loop, 1 mil gold over ground, 24 segments per
wire (N_s = 108, T = 4). Produced by `WBondMomCompareViewModel.Compare` — **the same call the Compare
dialog renders**, so the gate, this table and the screen are one computation rather than three that
agree.

|  f (GHz) | L lumped (pH) | L MoM (pH) |   ΔL % | C lumped (fF) | C MoM (fF) |  ΔC % | max ΔY/Y % | \|S21\| lumped (dB) | \|S21\| MoM (dB) |
|---|---|---|---|---|---|---|---|---|---|
|     0.01 |       1690.00 |    1690.00 |  0.000 |        59.645 |     64.881 |   8.8 |      0.000 |           −0.0077 |        −0.0077 |
|      0.1 |       1677.95 |    1677.95 |  0.000 |        59.645 |     64.882 |   8.8 |      0.000 |           −0.0088 |        −0.0088 |
|        1 |       1636.28 |    1636.33 |  0.003 |        59.645 |     64.900 |   8.8 |      0.020 |           −0.0588 |        −0.0583 |
|        5 |       1594.57 |    1595.58 |  0.063 |        59.645 |     65.348 |   9.6 |      0.511 |           −0.8687 |        −0.8597 |
|       10 |       1520.88 |    1521.44 |  0.037 |        59.645 |     66.807 |  12.0 |      2.202 |           −2.4882 |        −2.4650 |
|       20 |       1292.61 |    1252.77 | −3.082 |        59.645 |     73.764 |  23.7 |     11.439 |           −5.1003 |        −4.8579 |
|       40 |        810.23 |     402.94 | −50.269 |        59.645 |    160.430 | 169.0 |    130.343 |           −6.9233 |        −3.0385 |

**The reading, in one line each.**

- **The series inductance agrees to the last digit printed below 1 GHz** (0.000 %), to 0.06 % at
  5 GHz and 0.04 % at 10 GHz. That half is an identity and it behaves like one.
- **The capacitance never agrees, and it is the MoM value that is larger — by 8.8 %, at every
  frequency below 1 GHz.** That is the end concentration: charge piles up at a wire's ends and the
  lumped model spreads it uniformly per unit length. **8.8 % is the number to quote** for what one
  charge basis function per wire costs on a 100 mil bond.
- **The divergence is smooth and monotone across all seven points** — 0.000, 0.000, 0.020, 0.511,
  2.202, 11.44, 130.3 % — which is the actual content of "they should be correlated". A
  non-monotone step would have been a bug at one frequency.
- **At 40 GHz the two models are no longer describing the same thing.** This fixture is 1,690 pH
  against ~60 fF and self-resonates near 50 GHz; at 40 GHz the lumped model reads 810 pH and the MoM
  403, and |S21| differs by **3.9 dB**. That is the headline caveat for anyone who plots a lumped
  wBond above ~20 GHz, and it is not a defect in either model.

**The high-frequency numbers are NOT a mesh artefact.** Swept at 12 / 24 / 48 / 96 segments per wire,
the 40 GHz row moves from 168.6 % to 169.0 / 169.0 / 168.7 % and `max ΔY/Y` from 129.9 % to
130.3 / 130.7 / 130.8 %. The whole table is mesh-stable to a fraction of a percent; the low-frequency
capacitance drifts the most, 8.3 % → 9.4 % over that eightfold refinement, which is WM-1 §9.7's own
slow convergence showing up again.

**§6.6's `[1.0, 2.0]` capacitance band does not survive 40 GHz** — the ratio there is **2.69**. The
band is asserted up to 20 GHz, where `Im(row sum)/ω` still means "a capacitance"; above that it is the
structure's shunt susceptance near resonance, and the two models resonate at different frequencies.
**The sign check — MoM ≥ lumped — is asserted at every point**, which is the part the brief says would
be a sign error rather than a modelling difference.

### 2. §6.3's actual agreement: 1e-7, not 1e-3 — and the brief's oracle is the wrong one

**Measured relative agreement at 10 MHz between `−1/Y_port[2k,2k+1]` and
`ImpedanceReduction.ArrayImpedance`: 8.6e-8 (two straight wires), 2.6e-8 (four ball bonds), with the
resistance at 9.1e-8.** Four orders inside the brief's 0.1 %. **The gate is tightened to 1e-5
relative** — two orders tighter than asked, still 100× of margin.

**The brief names the wrong oracle, and it is off by 5.96 %.** §6.3 asks for `Im(Z_series)/ω` to match
`ArrayReduction.PicoHenries(k,k)`. **`ArrayReduction` consumes `L` and `A` and nothing else** — it is
the *external* partial inductance, and the internal inductance lives in `D(ω)`, which never reaches
it. At DC that is μ₀/8π per unit length: **127 pH on a 100 mil wire, 63.5 pH for two in parallel,
against a 1,065 pH external value.** So the gate as written would fail at 0.1 % by a factor of 56 —
*on a correct solver*. The right oracle is `ImpedanceReduction.ArrayImpedance`, which is what the
analytic **stamp** uses, and which §6.3 already names for the resistance half.
`TheExternalOnlyArrayReduction_IsSeveralPercentBelowTheSeriesArm_BecauseItHasNoInternalInductance`
pins the 5.96 % so nobody re-points the gate at the cheaper number.

### 3. §5's low-frequency floor: 100 kHz, measured, and it barely depends on the loop count

Series inductance from `Y_port` against `ImpedanceReduction.ArrayImpedance`, decade by decade with
five points per decade, on four designs:

| f | 2 straight wires (1 loop) | 10 straight wires (9 loops) | 4 ball bonds (2 loops) | 20 ball bonds (18 loops) |
|---|---|---|---|---|
| 1 kHz  | 1189 % | 512 % | 271 % | 298 % |
| 10 kHz | 29.3 % | 9.96 % | 4.51 % | 4.38 % |
| 30 kHz | 0.767 % | 0.477 % | 0.422 % | 0.405 % |
| 50 kHz | 0.120 % | 0.165 % | 0.330 % | 0.175 % |
| **100 kHz** | **0.060 %** | **0.080 %** | **0.044 %** | **0.057 %** |
| 300 kHz | 0.0041 % | 0.00012 % | 0.0052 % | 0.0042 % |
| 1 MHz | 0.00034 % | 0.00089 % | 0.00092 % | 0.00088 % |
| 10 MHz | 0.0000086 % | 0.0000028 % | 0.0000026 % | 0.00006 % |

**100 kHz is where it crosses 0.1 %, and `WireMomSettings.MinimumFrequencyHz` ships as 1e5.** The
brief's "a default of 1 MHz is a reasonable expectation" is **ten times conservative** — 1 MHz is
already good to 1e-5 relative.

**The surprise worth recording: the floor is essentially independent of the loop count.** `K̃`'s
nullity goes 1 → 18 across these designs and the departure at 100 kHz stays inside 0.044–0.080 %. The
conditioning argument predicts the 1/ω growth correctly and predicts nothing useful about the nullity.

**`SeriesArmImpedance` has no floor and is not gated by one** — it never forms `K̃`, so there is
nothing to be ill-conditioned. It reproduces the analytic array impedance to 1e-10 at 1 kHz.
Refusing there would be refusing the one thing that still works.

### 4. §6.5's convergence: 24 survives the stated gate, and raising it would buy almost nothing

8 wires in 2 arrays, `max|ΔS|` between meshes, 50 Ω:

| f | \|S(24)−S(12)\| | \|S(48)−S(24)\| | \|S(96)−S(48)\| | ratio 48/24 | ratio 96/48 |
|---|---|---|---|---|---|
| 1 GHz  | 1.043e-4 | 7.846e-5 | 6.453e-5 | 0.752 | 0.822 |
| 10 GHz | 1.334e-3 | 9.257e-4 | 8.018e-4 | 0.694 | 0.866 |
| 40 GHz | 6.443e-3 | 3.817e-3 | 3.599e-3 | 0.592 | 0.943 |

The brief's gate — the 48↔24 change below the 24↔12 one — **holds at all three frequencies**, so 24
survives. But **the ratios RISE toward 1** (0.59 → 0.94 at 40 GHz), which is the more useful result:
**this ladder is not converging, and refining past 24 does not fix it.** 24 → 48 costs **6.3× per
frequency point and 3.3× the setup** and removes 3.8e-3 of a 7.4e-3 gap to the 96-segment answer;
48 → 96 costs another ~7× and removes 3.6e-3. **So the default stays 24, and the honest statement is
that it carries ~1e-2 of |S| error at 40 GHz** — about 0.09 dB — which no available segment count
removes.

**Where that residual lives is measured, not guessed: it is entirely the CHARGE path.**
`SeriesArmImpedance` reproduces `ImpedanceReduction.ArrayImpedance` to **5e-13 … 4e-10 at every rung**
(12, 24, 48 and 96 segments, at 1 and 40 GHz), so the current path is mesh-invariant and contributes
none of the ladder above. That leaves `NodePotential`, and WM-1 already isolated the cause there:
**`PotentialCoefficients.Kernel`'s near branch for non-parallel filaments is a fixed 4-point
Gauss-Legendre rule, and a fixed-order rule on a near-singular integrand is not additive under
subdivision.** WM-1 called that "plausibly the same defect" as its own slow §9.7 convergence, from an
order sweep on one block. **This is a second, independent measurement supporting it** — and it makes
raising `GaussKernel`'s order the highest-value accuracy item in WM-3, ahead of anything about speed.

### 5. The per-frequency solve cost — WM-3's baseline

Release, Apple Silicon, straight-wire designs in 2 arrays at 24 segments per wire, measured with the
rest of the suite filtered out. Setup is mesh + `L` fill + `MomAssembly.Build`; per-point is the
`M̃` assembly, the LU and the T solves.

| N_s | setup | per frequency point |
|---|---|---|
| 192 | 29.3 ms | **2.32 ms** |
| 384 | 97.8 ms | **14.58 ms** |
| 960 | 1,261 ms | **212.7 ms** |

Fitted exponents: **setup ~N^2.35, per point ~N^2.8**. The per-point exponent is the dense LU it is;
the setup exponent is below cubic because WM-1's two batches of triangular solves are the bulk of it
and their right-hand-side counts grow with N as well. **The Compare dialog predicts from exactly these
two fits**, so the number a user sees before pressing Run is this measurement rather than a guess.

A 201-point export of a 40-wire array at 24 segments (N_s ≈ 1,040) is therefore **~1.5 s of setup plus
~50 s of sweep** — which is what the export dialog's tooltip should eventually say more precisely, and
which is the case WM-3's complex-symmetric factorisation and frequency parallelism exist for.

**§6.8 was NOT written.** The brief makes it optional here and mandatory in WM-3; a half-measured
201-point benchmark is worth less than the table above, which WM-3 can regress against directly.

### 6. Losslessness: σ = 1e12 S/m is not lossless, and the residual proves itself

§6.4 says to set σ = 1e12 "so R → 0" and gate |S†S − I| at 1e-9. **Measured at σ = 1e12 the defect is
2.2e-5 at 1 GHz, 3.8e-5 at 10 GHz and 2.7e-5 at 40 GHz — four orders above the stated gate, on a
correct solver.**

The residual is the wires' own skin-effect resistance and nothing else, and the test proves that
rather than asserting it: swept over σ ∈ {1e10, 1e12, 1e14, 1e16, 1e20} the defect falls as **1/√σ to
three digits** (2.23e-4, 2.22e-5, 2.22e-6, 2.22e-7, 2.22e-9 at 1 GHz), which is exactly the
high-frequency resistance law and is not a law any numerical artefact obeys. **So the gate is taken at
σ = 1e20, where the defect is 2.2e-9 to 3.8e-9, and the 1/√σ scaling is asserted alongside it** — the
scaling is what makes it a losslessness gate rather than a tolerance nobody can interpret.

### 7. §6.2, the identity gate: 3.23e-11, and it is WM-1's own limit

`SeriesArmImpedance` on a 24-segment mesh against `ImpedanceReduction.ArrayImpedance`, on 4 ball bonds
in 2 arrays: **3.229e-11 at 10 MHz, 1 GHz and 20 GHz alike** — identical to three digits at all three,
because the frequency dependence cancels out of a relative comparison of two assemblies over the same
`L`. It sits just inside the brief's 1e-10 and it is set by WM-1's inductance identity (1.5e-10 on a
curved wire, limited by `Grover.Skew`'s cancellation on nearly-parallel distant pairs), not by anything
this brief added. **It was written first and it passed first**, which is the only time that has been
true in this tranche.

### 8. Corrections to the brief (§9 item 7)

1. **§6.3's oracle is wrong** — `ArrayReduction.PicoHenries` is external inductance only, 5.96 % low.
   See §2 above.
2. **§6.4's σ = 1e12 does not reach 1e-9** — it reaches 2e-5, and the residual is real ohmic loss.
   See §6.
3. **§6.6's `[1.0, 2.0]` capacitance band fails at 40 GHz** at 2.69, for reasons that are physics.
   See §1.
4. **§7.3(b)'s frequency grid cannot express §6.6's seven points.** A log grid from 0.01 to 40 GHz at
   7 points is 0.01, 0.040, 0.158, 0.63, 2.51, 10.0, 39.8 GHz — not 0.01, 0.1, 1, 5, 10, 20, 40, which
   is not a grid of either kind (three decades then roughly ×2). The dialog keeps the Start/Stop/
   Points/Log controls §7.3 asks for and opens on the log grid; **the study takes its frequencies as an
   explicit list** through the public `WBondMomCompareViewModel.Compare`, which is also what makes the
   test and the dialog one computation.
5. **§7.4's "the menu item comes along for free" is not true for circuitRF.** `WBondMenuView` exists
   only in the standalone shell's window; in circuitRF the wBond editor is a document tab under
   `WorkspaceWindow`'s menu bar, which has no wBond Design submenu at all. A menu item alone would
   have shipped the feature to one binary of the two. **The entry point that genuinely reaches both is
   a toolbar button on `WBondEditorView`** — the view both binaries share — and the standalone menu
   item binds to that same view method rather than to a second implementation.
6. **§6.6 cannot live in `tests/WBond.Tests`.** One of the two models it compares is
   `WBondTouchstoneExport.TerminalAdmittances`, in `src/Ui`, and `src/WBond` is a leaf project. It is
   in `tests/Ui.Tests/WBondMomCompareTests.cs`.
7. **`MomAssembly.G`'s doc comment said "in inverse farads".** `P` is in inverse farads;
   `G = Rᵀ P⁻¹ R` is in **farads**, which is what makes `H/(jω)` an impedance. Corrected in place.
   §2's algebra itself is right as written and was implemented unchanged.

### 9. Smaller things worth having on paper

- **`WireMomMesh.RefusalFor` was added** because `Predict` deliberately does not refuse. A panel that
  shows the predicted unknown count for a design with no ground plane, and only discovers the refusal
  after Run, is the exact failure `Predict` exists to prevent — so the refusal is now askable without
  building, and `Build` is still the only thing that throws.
- **`Z_port` is asserted symmetric BEFORE it is symmetrised** (2e-13 raw). Asserting after would be
  testing the symmetriser rather than the solve.
- **The shunt capacitance is read as a row sum of `Y`, for both models identically.** Driving every
  port to the same potential leaves no voltage across any series element, so all that flows is the
  shunt. Any extraction that differed between the two models would have made the comparison a
  comparison of extractions.
- **`max|ΔY|/|Y|` is a max-norm ratio, not a per-entry relative.** WM-1 recorded the trap: the
  off-diagonal entries between distant arrays are near-cancellations, so a per-entry relative there is
  a huge number about a physically negligible quantity, and it does not fall monotonically.
- **`IncludeCapacitance = false` is neither refused nor obeyed.** The MoM network *is* the coupled
  L–C ladder — with `G⁻¹ → 0` the whole reduction degenerates — so the capacitance is included and a
  note saying so is attached to the result *and* written into an exported file's header.
- **The quasi-static note carries both of its numbers**: λ/10 at the top requested frequency, and the
  design's own widest wire-pair centre-to-centre separation. Centroids rather than closest approach,
  because the closest approach of two distant wires understates their separation by their own length.

---

## Follow-up (2026-08-18): the MUTUAL inductance, lumped against MoM — and how to get one out at all

Owner question, after WM-2 landed: *"how does the effect mutual inductance compare between lumped and
MoM? Use 2 wires. (How do you even get the mutual result out of the MoM results?)"*

### The extraction — there is no mutual in a MoM result until you transform it

The solve publishes a **2M × 2M terminal-basis `Z_port`**, every terminal referenced to the plane.
There is no off-diagonal in it that is a mutual: `Z_port[0,2]` is one terminal against another, not one
array against another. It has to go onto the array (differential) basis first —
**`WireMomSolver.PortImpedanceInArrayBasis(f)`**, added for this:

```
Z_arr = T Z_port Tᵀ      T[k, 2k] = +1,  T[k, 2k+1] = −1      (M × M)
M_ij  = Im(Z_arr[i,j]) / ω
```

Injecting `+i` at terminal `2k` and `−i` at `2k+1` is `i = Tᵀ i_arr`, and the voltage across the pair is
`v_arr = T v`, so `v_arr = T Z_port Tᵀ i_arr`. **T's rows summing to zero is the whole trick**: below
resonance `Z_port` is dominated by a common-mode `1/(jωC)` open circuit — 4 MΩ against the 0.1 Ω you
are trying to read — and a zero-row-sum congruence annihilates it *exactly* rather than cancelling
megohms numerically.

**`SeriesArmImpedance` would have answered nothing.** It also returns an M × M array-basis matrix with
a mutual in it, but it removes the shunt *by construction* and is therefore provably equal to
`ImpedanceReduction.ArrayImpedance` (§6.2's identity gate, 3.2e-11). Its mutual *is* the lumped mutual,
at every mesh and every frequency. The comparison has to come out of the full solve.

**How far to trust it, measured rather than assumed.** Run the same transform on the *lumped* model,
where `ArrayImpedance` gives the answer independently:

| f | transform vs `ArrayImpedance` |
|---|---|
| 10 MHz | 1.35e-7 |
| 1 GHz | 1.80e-5 |
| 10 GHz | 1.16e-3 |
| 20 GHz | 3.7e-2 |
| 40 GHz (past resonance) | **35** |

This fixture's lumped self-resonance is **27.6 GHz**. The transform is exact well below it, is worth
three digits at 10 GHz, and is meaningless past it — which is correct, not a defect: above resonance the
shunt *is* the network and no series mutual inductance exists to extract. `TheArrayBasisTransform_…`
gates the first three rows, so this control travels with the code.

### The result: the lumped model's error on the MUTUAL is 20–34× its error on the SELF

Two identical 100 mil / 30 mil-loop / 1 mil gold bonds over ground, **one per array**, 24 segments per
wire (N_s = 54). 10 mil pitch:

| f (GHz) | L11 lumped (pH) | L11 MoM (pH) | ΔL11 % | M12 lumped (pH) | M12 MoM (pH) | ΔM12 % | ΔM/ΔL | k lumped | k MoM |
|---|---|---|---|---|---|---|---|---|---|
| 0.01 | 2648.14 | 2648.14 | 0.000 | 755.636 | 755.636 | −0.000 | — | 0.2853 | 0.2853 |
| 0.1  | 2641.57 | 2641.57 | 0.000 | 755.636 | 755.642 | 0.001 | 44.8 | 0.2861 | 0.2861 |
| 0.5  | 2588.00 | 2588.01 | 0.000 | 755.638 | 755.775 | 0.018 | 40.5 | 0.2920 | 0.2920 |
| 1    | 2562.88 | 2562.93 | 0.002 | 755.650 | 756.186 | 0.071 | 37.1 | 0.2948 | 0.2950 |
| 2    | 2547.80 | 2548.00 | 0.008 | 755.709 | 757.829 | 0.281 | 34.9 | 0.2966 | 0.2974 |
| 5    | 2556.10 | 2557.47 | 0.054 | 756.124 | 769.481 | 1.767 | 33.0 | 0.2958 | 0.3009 |
| 10   | 2641.26 | 2647.48 | 0.235 | 756.512 | 813.902 | **7.586** | **32.2** | 0.2864 | 0.3074 |

and across separation, at 10 GHz:

| pitch | ΔL11 % | ΔM12 % | ΔM/ΔL | k lumped | k MoM |
|---|---|---|---|---|---|
| 5 mil  | 0.170 | **5.72** | 33.6 | 0.4340 | 0.4581 |
| 10 mil | 0.235 | **7.59** | 32.2 | 0.2864 | 0.3074 |
| 20 mil | 0.487 | **9.44** | 19.4 | 0.1586 | 0.1727 |

**Five things, in order of how much they matter.**

1. **It is an identity below ~100 MHz, exactly as the self inductance is** — agreement to 1e-5 or
   better at 0.01 and 0.1 GHz. Partial *mutual* inductance is additive under subdivision for the same
   reason partial self inductance is, so uniform current gives the identical number. There is no
   "mutual-specific" low-frequency error to worry about.
2. **The lumped model's error on the mutual is 20–34× its error on the self, at every frequency above
   1 GHz.** That ratio is the headline. Anyone reading §6.6's "0.037 % at 10 GHz" and concluding the
   lumped model is good to a fraction of a percent at 10 GHz is reading the *self* inductance; **the
   crosstalk between two bond wires is off by 7.6 % there**.
3. **Both errors scale as f²**, cleanly (each doubling of f multiplies both by ~4.0–4.3 over
   0.5–10 GHz), which is why the **ratio is essentially constant in frequency**. It varies with
   *separation* instead — 33.6 / 32.2 / 19.4 at 5 / 10 / 20 mil — not with frequency.
   *A tempting explanation is ruled out by this:* self inductance being stationary (second-order) in the
   current error while a mutual is first-order would put ΔL at f⁴ against ΔM at f². **Both measure f²**,
   so both are first order in the current perturbation and the ratio is a geometric sensitivity, not an
   order-of-accuracy difference. Recorded because it is the argument someone will reach for.
4. **The lumped model always UNDERSTATES the coupling.** `M12(MoM) ≥ M12(lumped)` at every frequency
   and every pitch tested, and the coupling coefficient shows it plainly: over 0.01 → 10 GHz the MoM's
   k rises 0.2853 → 0.3074 while the lumped k goes 0.2853 → 0.2864 and then *falls*. **A lumped
   wirebond model's mutual is frequency-independent by construction** — it is a fixed geometric number
   times a uniform current — and the ~0.1 % drift it does show is the array reduction reacting to its
   own shunt, not to any change in the current profile.
5. **None of this is the mesh.** M12 moves by **0.024–0.030 %** between 12 and 48 segments per wire, at
   every frequency and every pitch — three orders below the 7.6 % being measured. The mutual is far
   better converged than the *capacitance* is (WM-1 §9.7's 0.3–0.5 %) for a structural reason: it is a
   current-path quantity, and the current path is subdivision-exact while the charge path's fixed
   4-point Gauss rule is not.

**The practical reading.** Use the lumped model for a bond's own inductance up to ~10 GHz and it costs
you a quarter of a percent. Use it to predict *coupling* between two bonds at 10 GHz and it costs you
7–9 %, in the optimistic direction. That is the case the distributed model exists for, and it is a
sharper argument for it than anything in §6.6.

Gated by `tests/Ui.Tests/WBondMomMutualTests.cs` — the identity at low frequency, the sign, the
`ΔM/ΔL > 10` headline, the mesh drift and the transform control. It is in `Ui.Tests` for §6.6's own
reason: the lumped half is `WBondTouchstoneExport.TerminalAdmittances`, in `src/Ui`.

---

## `brief-wbond-mom-w3-speed.md` — making the kernel fast, and honest about what it costs (2026-08-18)

No physics changed. Every WM-1 and WM-2 oracle passes at its existing tolerance, and the routine
`Mom` tier grew from 64 to **91 tests** while its wall clock went DOWN, from 2 s to **1 s**. Seven milestones were attempted, **six
were kept and one is a negative result**; two sub-decisions inside the kept ones were reverted after
measurement and are recorded below with the numbers that killed them.

**Every measurement here was taken ALONE** — `dotnet test tests/WBond.Tests -c Release --no-build
--settings circuitrf.benchmark.runsettings --filter "FullyQualifiedName~<class>"`, nothing else
running — on **Apple Silicon, 10 cores, 16 GB, .NET 10, Release, 2026-08-18**.

### 1. The table, before and after

Straight-wire designs (`TestDesigns.ParallelArray`), so `N_s` is exactly wires × segments.

| | **S** (8 wires, 24/wire) | **M** (40 wires, 24/wire) | **reduced L** (200 wires, 8/wire) |
|---|---|---|---|
| `N_s` / `N_n` / `N_r` / T | 192 / 200 / 188 / 4 | 960 / 1,000 / 928 / 8 | 1,600 / 1,800 / 1,416 / 16 |
| `L` fill (parallel) | 1.6 → 1.6 ms | 88 → 15 ms | 131 → 15 ms |
| `P` fill (parallel) | 3.2 → 3.2 ms | 30 → 10 ms | 27 → 10 ms |
| `G` (chol `P` + reduce) | 8.5 → **4.4** ms | 697 → **202** ms | 3,670 → **968** ms |
| `K̃`, `W`, `H` | 6.0 → **2.1** ms | 628 → **170** ms | 2,432 → **502** ms |
| **setup total** | 19.2 → **12.6** ms | 1,442 → **626** ms | 6,263 → **1,506** ms |
| per-point `M̃` assembly | 0.15 → 0.08 ms | 1.02 → 0.59 ms | 2.75 → 3.32 ms |
| per-point factorisation | 1.93 → **0.92** ms | 205 → **100** ms | 933 → **471** ms |
| per-point T solves + reduce | 0.20 → 0.39 ms | 6.3 → 5.5 ms | 32 → 30 ms |
| **per point** | 2.28 → **1.39** ms | 212 → **107** ms | 968 → **504** ms |
| 201-point sweep, serial | 0.39 → 0.21 s | 42.0 → **21.3** s | 194 → **101** s (extrapolated) |
| **201-point sweep, parallel** | — → **0.05 s** | — → **4.5 s** | — → **31 s** (extrapolated from 41) |
| peak working set | 116 MB | 191 MB | 400 MB |

**The fill rows need a caveat that cost an hour to notice.** A fill measured as the first big
allocation of a process reads 6–14× its steady-state cost — 131 ms against 15 ms at `N_s` = 1,600 —
because it is page-faulting a fresh 20 MB array and running on tier-0 code. Neither number is wrong;
the "before" column above is a first-call number and the "after" is too, taken the same way. The
fills' *steady-state* costs (best of three, warm) are **L 14.6 ms / P 9.5 ms at `N_s` = 960** and
**L 15.8 / P 7.6 at 1,600** — they are not a meaningful part of the cost at any size, which is the
point WM-1 already made.

**Size L was measured once, by hand, out of the test suite** (200 wires at the Balanced 24, `N_s` =
4,800, `N_n` = 5,000, `N_r` = 4,616): **setup 34.5 s** (`L` 252 ms, `P` 103 ms, `G` **19.3 s**,
`K̃`/`W`/`H` **14.9 s**) and **14.17 s per frequency point**, of which the factorisation is 13.85 s.
Ten threads at 353 MB each; 1,164 MB working set against an 846 MB prediction. A 201-point sweep of
it is **~12.4 minutes**. Size L is in the shipped tier only at the reduced 8 segments/wire, exactly
as §1 permits.

### 2. Which milestones were kept, and by how much

| | what | measured | kept? |
|---|---|---|---|
| **M1** | explicit `P⁻¹`/`G⁻¹` + scatter-add products | setup **1.67× / 1.99× / 3.59×** (S / M / reduced-L) | **kept** |
| **M2** | complex-symmetric `LDLᵀ` | per point **1.75× / 1.76× / 1.90×**; whole sweep **2.50×** at M | **kept** |
| **M3** | frequency-parallel sweep | **4.00× / 3.83× / 2.97×** on 10 cores | **kept** |
| **M4** | the segmentation ladder | no speed claim; the table is in §5 below | **kept** |
| **M5** | the cost model and the warning | predicts **1.16×** at M, **1.12×** at reduced L | **kept** |
| **M6** | plan reuse + cancellation | structural; setup is 34.5 s at L, so it is worth a lot | **kept** |
| **M7** | NumFlat instead of the hand-written solver | **0.91× / 0.81×** — NumFlat is SLOWER | **not added** |

M2's whole-sweep 2.50× is larger than its 1.76× per-point factorisation win because the multi-RHS
`SolveInPlace(block, columns)` came with it: the T port solves now share one triangular sweep instead
of reading the factor T times.

### 3. M1 and WM-1's own five-line prediction are the SAME win, and WM-1's factor was 2× optimistic

WM-1 closed with: *"both batches are embarrassingly parallel over their right-hand sides… on this
machine that is an ~8× available for a five-line change, and it is worth more than anything else named
in WM-3."* **Measured, at the `G` factor's own size, excluding the Cholesky both routes pay:**

| | `N_r` = 928 | `N_r` = 1,416 |
|---|---|---|
| N triangular solves, serial | 595 ms | 2,147 ms |
| N triangular solves, `Parallel.For` | **165 ms** (3.6×) | **566 ms** (3.8×) |
| `InvertInPlace`, serial | 238 ms | 875 ms |
| `InvertInPlace`, parallel | **169 ms** | **497 ms** |

**Parallelising the solves buys 3.6×, not 8×, and lands within 2 % of the explicit inverse at
`N_r` = 928.** Both routes are memory-bandwidth-bound, not core-bound: each triangular solve streams
the whole N × N factor, so ten of them at once compete for bandwidth. The inverse is the better of
the two anyway — it does a third of the flops (`N³/3` against `N³`), needs no per-thread right-hand-side
buffers, and is 1.14× ahead at `N_r` = 1,416 where the gap widens — but the honest statement is that
**M1 and the five-line change would have arrived at nearly the same place, and neither reaches 8×.**

Bandwidth is also why the parallel inverse is worth so little on its own: **1.00× at N = 200, 0.87–0.97×
at 400, 1.17–1.44× at 928, 1.73–1.99× at 1,416.** `CholeskyFactor.ParallelThreshold` was **reverted from
96 to 512** on exactly those numbers — at 200 the fan-out made the setup 3 ms *slower*.

**The other reverted sub-decision: the inverse's inner loops were first written as C# local functions**,
which the compiler hoists into a display class — the JIT then keeps the array in memory rather than a
register and cannot drop the bounds check. **9.56 ms against 1.99 ms at N = 200, a 4.8× penalty for a
syntactic choice.** They are static row kernels now, with the reason written next to them.

### 4. M2: the symmetric factorisation is safe here, and that is a measurement

A 201-point sweep at `N_s` = 960 with both factorisations:

- **max |Z_ldlt − Z_lu| / |Z_lu| = 9.9e-13** (worst at 40 GHz), against the brief's 1e-9 gate;
- **zero points took the LU fallback**, at the shipped 1e-12 pivot-ratio floor;
- 6.58 s against 16.44 s for the same sweep on the LU.

The guard is still there and is still worth having, because the failure it catches is not a singular
matrix. `[[0,1],[1,0]]` is symmetric, has determinant −1 and condition number 1, and **has no unpivoted
`LDLᵀ` at all** — `MomFactorisationTests.AWellConditionedMatrixCanHaveNoUnpivotedFactorisation` pins
that, and its near-miss twin (`[[ε,1],[1,0]]`, which factorises silently and reports a pivot ratio of
ε²) pins what the guard actually reads. The fallback path itself is tested by demanding a pivot ratio
of 1.0, which nothing can satisfy: the sweep falls back at every point, says so in its notes, and
returns the LU's numbers bit for bit.

### 5. M4: the ladder, and why Balanced stays at 24

The ball-bond design of WM-2 §6.5 (8 wires, 2 arrays, 7 points/wire), three frequencies. The accuracy
column reproduces WM-2's own table exactly, which is the check that this is the same computation:

| rung | segments/wire | `N_s` | 3-point wall clock | max\|ΔS\| against the next rung up (1 / 10 / 40 GHz) |
|---|---|---|---|---|
| **Fast** | 8 | 96 | **8.1 ms** | 2.06e-4 / 2.60e-3 / **9.94e-3** |
| **Balanced** | 24 | 208 | **17.8 ms** | 7.85e-5 / 9.26e-4 / **3.82e-3** |
| **Accurate** | 48 | 408 | **80.1 ms** | 6.45e-5 / 8.02e-4 / **3.60e-3** |
| (96) | 96 | 792 | 302.5 ms | — |

**24 stays.** WM-2 already measured that this ladder is not converging — the rung-to-rung ratios rise
toward 1 (0.59 → 0.94 at 40 GHz) because the residual is `PotentialCoefficients.Kernel`'s fixed-order
Gauss rule on near-singular pairs, not the mesh — so **Accurate costs 4.5× and removes 6 % of the gap
at 40 GHz.** Fast costs a fifth of Balanced and carries ~1e-2 of |S| at 40 GHz against it, which is
about 0.09 dB: real, bounded, and the right trade for a design that would otherwise not solve.

**A rung is not a continuous knob, and the jumps are large.** The mesh count is `Σ_i ceil(len_i/maxLen)`
over the polyline, so on a 7-point ball bond at 200 wires the rungs 7, 8 and 9 all produce the identical
2,400-segment mesh while 6 → 7 jumps `N_s` from 1,600 to 2,400 — **a 3.3× jump in sweep cost between two
adjacent integers.** `SegmentsForBudget` searches the rungs rather than interpolating for exactly that
reason, and its own test asserts that the rung above the answer really does not fit.

### 6. M5: the fitted cost model, and where it is right

```
setup(N)      = 2.94e-7 · N²  +  2.51e-10 · N³          seconds
perPoint(N)   = 1.37e-8 · N²  +  1.25e-10 · N³          seconds
speedup(k)    = k / (1 + 0.167·(k − 1))
sweep(N,p,k)  = setup(N) + p · perPoint(N) / speedup(k)
```

Fitted on the S and L end points and checked against the two middle sizes. Measured against predicted:

| | setup | per point | sweep |
|---|---|---|---|
| S (192) | 12.6 vs 13 ms | 1.39 vs 0.91 ms | 15.3 vs 19.9 ms (**1.3×**, 2.2× in a hot process) |
| M (960) | 626 vs 493 ms | 107 vs 123 ms | 5.78 vs 6.69 s (**1.16×**) |
| reduced L (1,600) | 1,506 vs 1,781 ms | 504 vs 547 ms | 6.33 vs 7.39 s at 41 points (**1.12×**) |
| L (4,800) | 34.5 vs 39 s | 14.2 vs 12.7 s | ~12.4 min predicted |

**Two deliberate departures from the brief's §6, both measured into existence:**

1. **The per-point cost carries a quadratic term.** §6 writes it as `c·N³` alone, which is 0.65× at
   `N_s` = 192 because forming `M̃` is quadratic and is a third of a small point. Since the accuracy
   gate is taken at size S, dropping the term would have made the gate measure the model's own missing
   term.
2. **The sweep is divided by a measured speedup, not by the thread count.** Frequency parallelism gives
   **1.00 / 1.92 / 3.23 / 3.61–4.00×** at 1 / 2 / 4 / 10 threads — bandwidth again — so a model that
   assumed `×threads` would be **2.5× optimistic on exactly the large runs where the number matters**.
   `ParallelContentionFraction = 0.167` is what reproduces the 10-thread measurement.

The prediction now has **one** implementation. The Compare dialog carried its own power-law fit of the
WM-2 numbers (`29.3·(N/192)^2.35`, `2.32·(N/192)^2.8`); M1 and M2 made every constant in it wrong by
two to three times within a day of it being written, so it is deleted and the panel, the export
dialog's cost note, the ceiling refusal and the slow-run warning all quote `WireMomCost`.

**What the user is told before they wait**, on a 200-wire array at 201 points:

> ⚠ This sweep is predicted to take about 14 min (5,000 unknowns × 201 point(s), 10 thread(s)). At
> 6 segments per wire it would be about 19.9 s (1,400 unknowns).

A **warning, not a refusal** — the run is legal, and `RefusalFor` still returns null for it.

### 7. M3's thread count is a memory decision, and the note says which one it made

`16·N_s²` bytes per thread: 14.7 MB at `N_s` = 960 and **353 MB at 4,800**. The count is
`clamp(1, cores, budget / bytesPerThread)` with the budget defaulting to a quarter of
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` — a quarter because the process is a GUI with a
document model and possibly a second analysis in it, and a sweep that takes all the memory makes the
rest of the application the thing that fails. Every multi-point result carries the line:

> Solved 10 frequency points at a time (14.7 MB of workspace each, 10 core(s) available).

**The per-thread workspace is also a correctness fix, not only a budget.** `M̃` and the `D(ω)` buffer
were fields on `WireMomSolver`; two frequency points in flight at once would have overwritten each
other's matrix. `MomCostModelTests.TheParallelSweepAgreesWithTheSerialOne` is the gate.

### 8. M6: the solver already WAS the plan, so nothing was split

The brief asks for a `WireMomPlan` / `Solve(plan, freqs, ct)` split. `WireMomSolver.Create` already does
every frequency-independent thing and `Solve` already takes only a grid — **a second type would have
been a rename.** What was actually missing, and is now there:

- **cancellation through setup**, not only through the frequency loop. Setup is 34.5 s at size L, so a
  caller that can cancel a sweep but not its setup still leaves a user waiting half a minute;
- **`WireMomSolver.Matches(design, settings)`**, so a caller re-exporting on a second grid can ask
  whether the plan it is holding is the right one;
- **`WBondTouchstoneExport.SolveDistributed(..., solver, ...)`**, which reuses it.

`Matches` is **reference equality on the design plus record equality on the settings, and it is not a
staleness check** — an edited design still matches. That is written into its doc comment and asserted
by a test, because the alternative (a mutation-aware cache) is the trap `WireMesh`'s own comment
already records. The caller holds the plan; nothing static holds one.

### 9. M7: NumFlat is slower than the code we already have — a negative result, recorded so it is not re-run

| | `ComplexLu` (ours) | `ComplexLdlt` (ours) | NumFlat `Lu()` |
|---|---|---|---|
| n = 960 | 210 ms | **103 ms** | 232 ms |
| n = 1,600 | 928 ms | **471 ms** | 1,149 ms |

**NumFlat's complex LU is 10–24 % slower than our own**, and 2.2–2.4× slower than the factorisation the
kernel actually uses. So no `PackageReference` was added. The other two rules of §8 never came into
play and were checked rather than assumed: NumFlat and its `MatFlat` dependency ship **no `runtimes/`
folder and no native binary**, so the repo-root "ask before adding a native dependency" rule was not
triggered; and the leaf-project property was never at risk, since a package reference is not a project
reference. `tests/Firewall.Tests` is green.

### 10. The corrected sizing table — the design note's §3 is ~9× optimistic, after WM-3

`mom-wirebond-kernel.md` §3 predicts **~3 s per frequency at ~6,000 unknowns**. The measured cubic
coefficient is **1.28e-10 s per `N_s`³** (14.17 s at `N_s` = 4,800), so:

| `N_s` | measured / predicted per point | 201-point sweep at 10 threads |
|---|---|---|
| 960 | 107 ms | 4.5 s |
| 1,600 | 504 ms | 31 s |
| 4,800 | **14.2 s** | **~12.4 min** |
| 6,000 (the note's case) | **~27.6 s** | ~25 min |

**The note is 9.2× optimistic even with the complex-symmetric factorisation in place, and was 18×
before it.** The reason is the one §0.3 names and this brief confirms: managed
`System.Numerics.Complex` arithmetic in a dense unblocked factorisation runs at roughly 1.5 GFLOP/s
scalar. **The design note stays as written** — it is the record of what was proposed, and correcting it
in place would erase the finding.

The brief's own §0.3 arithmetic (55.8 ms at N = 600, cubed to ~29 s at N = 4,800) turns out to describe
**our LU** almost exactly; what it could not know is that M2 halves it.

### 11. §9's targets: three met, one missed by 1.55×, one report-only

| | setup | per point | 201-point sweep |
|---|---|---|---|
| **S** (192) | ≤ 100 ms → **12.6 ms** ✔ | ≤ 2 ms → **1.39 ms** ✔ | ≤ 0.5 s → **0.05 s** ✔ |
| **M** (960) | ≤ 2 s → **0.63 s** ✔ | ≤ 120 ms → **107 ms** ✔ | ≤ 5 s → **4.5 s** ✔ (5.8 s on a warm-cache run) |
| **reduced L** (1,600) | ≤ 6 s → **1.5 s** ✔ | ≤ 550 ms → **504 ms** ✔ | ≤ 20 s → **31 s** ✘ (1.55×) |
| **L** (4,800) | report only → 34.5 s | report only → 14.2 s | report only → ~12.4 min |

**The reduced-L sweep target is missed and the reason is measured, not guessed.** 201 points at 504 ms
is 101 s of arithmetic; the sweep target of 20 s needs a 5× parallel speedup and the machine delivers
3.0× at that size, because the sweep is bandwidth-bound rather than core-bound (§3 and §6 above). No
further constant-factor work closes a 1.55× gap that is set by memory bandwidth; a blocked (BLAS-3)
factorisation would, and it is named in §12.

### 12. Named, and deliberately not built

- **ACA / low-rank compression of `L` and `K̃`.** The genuine answer above `N_s` ≈ 5,000, already named
  by `mom-wirebond-kernel.md` §7.3. A project, not a milestone — and now decidable with real numbers:
  at `N_s` = 4,800 the dense factorisation is 13.9 s of the 14.2 s point.
- **A blocked (BLAS-3) factorisation and inverse.** *New from this brief, and the highest-value one:*
  every cubic step in the kernel is currently unblocked, so it is matrix-**vector** work and is
  bandwidth-bound. That is what caps the parallel inverse at 1.4–2×, the frequency sweep at 4×, and the
  managed arithmetic at ~1.5 GFLOP/s. It is the only change measured that would move the sweep
  target of §9 for reduced L.
- **Iterative solve (GMRES).** `src/Engine/Mom/PlanarGmres.cs` is the precedent, but the trade is much
  worse here: T right-hand sides per point and a matrix that changes every point. Measure before
  believing.
- **Single precision.** Rejected: `P` and `G` are inverted, and WM-2 §5's low-frequency conditioning is
  already the delicate part.
- **The retarded kernel (W2).** Every matrix becomes frequency-dependent, so a 201-point sweep costs 201
  fills instead of one and **every structural speed argument in these three briefs evaporates.** Its own
  brief, its own cost story, its own gates.
- **An incremental / drag-interactive MoM path**, as §0.5 forbids. `IncrementalFill` and `QualityLadder`
  serve the analytic model, which is what the editor drags against.

### 13. The Benchmark tier this brief adds

**6 methods, 7 cases, 61 s together**, measured as a tier (`--settings circuitrf.benchmark.runsettings
--filter "FullyQualifiedName~MomSpeedTests"`):

| method | what it measures | s |
|---|---|---|
| `TheSymmetricFactorisationAgreesWithTheLuAcrossASweep` | §3's 201-point `LDLᵀ`-vs-LU agreement and the fallback count | 21 |
| `NumFlatDoesNotBeatTheHandWrittenSolver` | §8, and it fails if NumFlat ever gets within 1.5× | 12 |
| `TheStageTableAndTheTargets(200 wires)` | §1/§9 at reduced L | 10 |
| `TheFrequencyParallelSpeedupIsMeasuredNotAssumed` | §4.1's speedup curve at 1/2/4/all threads | 11 |
| `TheStageTableAndTheTargets(40 wires)` | §1/§9 at size M | 5 |
| `TheSegmentationLadderCostsWhatItBuys` | §5's cost-and-accuracy ladder | 1 |
| `ThePredictionMatchesTheStopwatchAtSizeS` | §6's accuracy gate at size S | <1 |

**In Debug — which is what the brief's own gate command runs — the same seven cases are 5 min 20 s**,
and the whole `WBond.Tests` opt-in tier is 5 min 53 s. Repo-wide the tier goes from **122 to 128
methods**.

**Every COMPARATIVE assertion in these methods is taken only in an optimised build**, and the numbers
are printed either way. Two of them are not close calls in Debug. **NumFlat ships as a Release-built
NuGet package**, so a Debug run compares optimised IL against unoptimised and its LU reads 235 ms
against our `LDLᵀ`'s 1,149 — a 4.9× "win" that is entirely the compiler. And the `LDLᵀ`-vs-LU **sweep
ratio collapses to 1.00×** in Debug, because per-operation overhead on `System.Numerics.Complex`
swamps the factor-of-two difference in how many operations there are. The predictions, fitted to
Release numbers, read 0.16–0.37×. Everything that is about arithmetic rather than wall clock — the
1e-9 agreement, the zero fallbacks, the ladder's |ΔS|, and the parallel speedup, which compares a
build against itself — is asserted unconditionally.

**Two pre-existing Benchmark tests are unreliable, and neither is this brief's.** Both touch only
wire-basis code that WM-3 does not modify (`git status` shows exactly two changed files under
`src/WBond` outside `Mom/`: `CholeskyFactor` and `ComplexLu`).

- **`M1CostTests.M6_1_SingleWireDragUpdate_StaysInsideTheTenMillisecondBudget`** — WB-A's 10 ms drag
  budget, measured in Release, **fails in Debug at 17–20 ms**. It is the one failure left in the
  brief's own gate command, which runs Debug. **Verified not to be a WM-3 regression by deleting this
  brief's only change on that code path** — `SolveInPlace`'s consumed-factor guard — and re-measuring
  at 17.4 and 18.0 ms, and by its passing in every Release run.
- **`CapacitanceCostTests.C4_TheCapacitanceFillAndReductionAreCheaperThanTheInductanceOnes`** — a
  per-pair cost ratio between two wire-basis fills, **failed 1 run in 4 in Release** (P 48.2 ms against
  L 35.6 ms) and passed the other three. Recorded rather than dismissed. Everything else
this brief added — 15 factorisation-correctness tests, 12 cost-model / plan / cancellation tests, 2 UI
tests — is **routine**, and the whole `Mom` folder still runs in 1 s.

**One test was written routine, as §10 requires, and then moved.** `ThePredictionMatchesTheStopwatchAtSizeS`
compares a prediction fitted in Release against a stopwatch; it **failed 2 runs in 4** with the full
solution suite running alongside it, because a Debug build under a ten-way parallel start reads seven
times the Release prediction. That is the case the repo's own rule already covers (`RfCore.Tests`'
`Rbf2DPerfTests`: fast, but wall-clock-sensitive, so tagged `Benchmark`). Its routine half —
`ThePredictionIsSelfConsistent`, which gates the model's shape and its agreement with what the report
prints, with no stopwatch in it — stays where §10 wants it, and the prediction is still gated against a
stopwatch at three sizes. Six further runs under the same load are clean.

**Its band is 3× pessimistic and 2× optimistic, deliberately.** A 21-point sweep of 192 unknowns is nine
milliseconds, where the model competes with page faults and thread-pool wake-ups and loses by a factor
that depends on how hot the process is (15.3 ms cold, 9.1 ms hot). Over-predicting a 9 ms job costs
nobody anything; **under-predicting a five-minute one is the failure the gate exists for.**

### 14. Smaller things worth having on paper

- **`MomStageTimes` is threaded through the real code path rather than replicated in a benchmark.** A
  cost table measured by a test that re-implements the assembly measures the replica and goes stale the
  first time the real one is restructured — which is precisely what M1 did to it.
- **The four-term index expressions are exactly the trap the brief predicted.** `K̃[p,q] = Ginv[s_p,s_q]
  − Ginv[s_p,e_q] − Ginv[e_p,s_q] + Ginv[e_p,e_q]` with one index swapped produces a plausible finite
  wrong answer. WM-1 §9.8's structural gates and WM-2 §6.3's 1e-7 end-to-end agreement both caught the
  intermediate versions; neither needed a new test.
- **The degenerate segment needed no special case after M1.** The old code guarded `rs == re` (a segment
  whose two ends merged into one terminal) before a triangular solve; the four-term expression evaluates
  to an exact zero row on its own.
- **The `Y = G⁻¹Ãᵀ` intermediate is gone**, which removes `8·N_r·N_s` bytes from the peak — 177 MB at
  size L. §8's memory arithmetic is otherwise unchanged, because both inverses overwrite their originals
  and `P` is dead the moment it has been factorised.
- **`CholeskyFactor.InvertInPlace` is genuinely in place**, in three passes: invert the triangle
  (`X L = I`, so every row of a step is independent — the `L X = I` form is a serial recurrence),
  transpose in place, then `U Uᵀ` in LAPACK's `lauum` order, where column *i* of the result reads only
  columns > *i* and everything it touches is still `U`.
- **`GC.GetGCMemoryInfo()` reports 16 GB here and `Process.PeakWorkingSet64` reports 0 on macOS.** The
  benchmark prints `Environment.WorkingSet` instead. Anyone reading a peak-memory zero in an older log
  should know it is the API, not the run.
- **One unexplained test failure is on the record.** A single `Ui.Tests` run reported 1 failure of 7,902
  during this work, with no TRX logger attached, and did not reproduce in **ten** subsequent runs —
  three of them with `Engine.Tests` running alongside, which is the condition that reproduces the known
  wall-clock flakes in seconds. It is named here because it was seen, not because it is understood.
