# Sonnet Brief — G_A^zz's accuracy ceiling: is ρ/λ ≤ 0.1 real, and is it asked correctly?

**Design:** `docs/design/layout-view.md` §10.7 (the hero this limit refuses) and §11's phase-table row
**L9**. **This is a FOLLOW-UP to L9**, a sibling of `brief-via-z-integral.md` and
`brief-ground-vias-and-interior-electrostatics.md`, and it is independent of both. It closes — or
earns, on numbers — the single limit that stops a via-bearing full-wave run on ordinary geometry.

**Read, in this order, before planning anything:**

1. `src/Engine/Mom/CLAUDE.md` **§L9c's "Tier 5, the reported curve"** — the whole table and all three
   things read off it, especially (2), which diagnoses the mechanism and records a remedy that
   **measured worse**.
2. **§L9d's M5** (`G_A^zz`'s range is a REFUSAL) and **§L9c's M2/D3** (`EvaluateInterior`'s three
   extractions and its own Tier 3 rungs). The second is the oracle this brief leans on.
3. **`Dcim.cs`'s `DcimSettings` doc comments in full** — `BranchPointOrders`, `BranchSamples`,
   `PathExtent`, `FarPathExtent`. Every one carries the measurement that chose its default. **Note
   which fit those measurements were taken on** (§0.2 item 5).
4. **`PlanarSolve.cs` around the `HasVerticalBasis(mesh)` guard**, and `PlanarKernelSet.WithinValidatedRange`.
   Read the comment there before the code: it already says the limit *"binds ONLY the ẑẑ block"*.
5. Then `Dcim.FitAtHeights` end to end, and `SommerfeldIntegral.EvaluateInterior` /
   `CanIntegrateInterior`.

---

## Gate command

```
dotnet test tests/Engine.Tests --no-build
dotnet test tests/Ui.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

**The routine tier has NO headroom** — `Engine.Tests` is at **993 tests in 68 s** against the ~60 s
ceiling, already over, and that is stated rather than smoothed. **Nothing routine may fill a matrix or
run a fit sweep.** The opt-in tier is ~42 minutes and you must say what you add to it.

**One measurement discipline.** Every timing number is taken ALONE and the report says so — L8d's own
finding is that a benchmark sharing a run with nine others reads more than twice as slow.

---

## 0. Read this before planning anything

### 0.1 The limit, exactly, and what it costs

`Dcim.ValidatedRhoOverLambdaAtHeights = 0.1`, against `ValidatedRhoOverLambdaLayered = 1.0` for the
top-half-space pairing — **an order of magnitude tighter**, measured on the SCALED error a fill
actually experiences rather than on a strict relative one. `PlanarSolve` turns it into a refusal
whenever a mesh carries vertical current.

**It binds real geometry.** §10.7's own FR-4 hero is 2.9 × 20 mm; at 10 GHz λ₀ = 30 mm, so its
diagonal is **0.67 λ** and a via-bearing DUT that size is refused outright. An MMIC at a few hundred µm
is ~0.01 λ and passes comfortably. So the current state is: *full-wave analysis of a via structure is
available at MMIC scale and unavailable at board scale.*

### 0.2 Seven things that are true before you start

1. **`G_A^zz` is the SOLE outlier, by three orders of magnitude.** L9c's Tier 5, as a fraction of the
   free-space kernel, worst over ρ/λ ≤ 0.1 and separately over ρ/λ ≤ 1, on the grounded stacks:
   `G_A^xx`, `G_q` and the mixed component are **≤ 1.9e-2 out to ρ/λ = 1** — L9b's own envelope for the
   top-half-space pairing. `G_A^zz` is 1.3e-3 at ρ/λ = 0.1 and **14** at ρ/λ = 1 on the 100 µm GaAs
   slab (3.7 cross-region; 7.3e-1 on the MMIC stack). **The limit is about one component, and the
   whole brief should stay pointed at it.**
2. **The mechanism is DIAGNOSED, not guessed.** The winning depth set carries **Σ|A_i| = 1.1e9 with
   two images of 5.6e8 at depths of 8.9 cm and 16.9 cm** — depths comparable to ρ itself, whose
   cancellation is exact on the sampled path and degrades as ρ walks into them. That is why the error
   grows like **ρ⁴** rather than smoothly.
3. **The obvious remedy was tried and measured WORSE.** An amplitude-conditioning cap — reject a
   candidate depth set whose Σ|A_i| runs far past the data it fits — took the GaAs low–low case from
   **14 to 39** at a cap of 1e4 (the rejected candidate was the better one spatially in spite of its
   conditioning), and at 1e2 rejected every candidate on every stack. It is a correct diagnosis and a
   bad selector. **Do not re-derive it.**
4. **`SommerfeldIntegral.EvaluateInterior` is accurate everywhere out there and is independently
   gated** — 2.2e-11 on the εᵣ-uniform reduction, 2.4e-15 on its own zero-remainder rung, 4.0e-7
   against the shipped `EvaluateLayered` on a different contour, **9.6e-10 under a 100× coarsening**,
   and 5.4e-15 on cross-region reciprocity. **One interior point costs 40–50 ms** (~15,000 kernel
   evaluations). The standing objection to using it in production is that it is "far too slow to fill a
   matrix with" — **which is true and is not the question this brief asks** (§3, M2).
5. **`DcimSettings`' defaults were chosen on a measurement that does not transfer here, and one of the
   knobs is switched OFF because of a trade that does not apply.** `BranchSamples` — a third sampling
   block straddling the branch point, *"the softer alternative to pinning Taylor coefficients
   exactly"* — is 0 by default because it *"buys FR-4's G_A another 30× and costs FR-4's G_q 4×, and
   G_q is the kernel that carries the charge, so it wins the trade."* `BranchPointOrders` is 1 for the
   same shape of reason. **Both measurements were taken on the ONE-LAYER (L8a) fit, and both are
   trades BETWEEN components** — settled by sharing one `DcimSettings` across all of them.
   `PlanarKernelSet` still shares one. **The failing component is `G_A^zz` alone, and `G_q` at heights
   is fine, so the trade that chose those defaults is not the trade this case faces.**
6. **The refusal is asked of the MESH DIAGONAL.** `PlanarSolve` computes `Diagonal(mesh)` and asks
   `WithinValidatedRange(extent)` — while its own comment two lines above says the limit *"binds ONLY
   the ẑẑ block"*, and `FillMultiLevel`'s `zi && zj` arm is the only consumer of
   `GreensKernel.VerticalVectorPotential` anywhere. **The quantity asked about and the quantity that
   matters are not the same quantity.**
7. **A depth search that is not Prony-on-two-fixed-paths is what §L9c says this needs, and the standard
   one is a general complex eigensolver — declined at L7b-b, L8a, L9c and L9e.** That decline stands
   until the three cheaper routes below have been measured. **It is not the starting point.**

---

## 1. Decisions taken

**D1 — THREE MEASUREMENTS COME BEFORE ANY NEW NUMERICS, and each is a stopping point.** M0 asks
whether the question is right; M1 asks whether existing knobs answer it; M2 asks whether the failing
block needs a fit at all. **If any of them closes the limit, the brief ends there and the rest is not
built.** This area has three precedents for exactly that shape (L7b-b weighed Route B against a
measurement and declined; L8a's branch-point table chose on numbers; the via z-integral brief's own M1
found L9c's cost premise false).

**D2 — NARROWING THE QUESTION IS NOT WIDENING THE ANSWER, and the report must be able to tell them
apart.** M0 changes which ρ the refusal is asked about. It must not, as a side effect, leave some other
pairing ungoverned. **If narrowing exposes that nothing checks the horizontal pairings on a general
stack, that is a FINDING and it gets its own check — not a silent gap.**

**D3 — `ValidatedRhoOverLambdaAtHeights` moves only on a MEASURED curve, and the curve is L9c's own.**
Re-run Tier 5's sweep, on the same six stacks, both interior pairings, all four components, against
`EvaluateInterior`. **Do not move the constant on a plausibility argument**; L9c measured the 14× that
justifies it and the same measurement is what would justify moving it.

**D4 — per-COMPONENT DCIM settings are legitimate; per-component TOLERANCES are not.** §0.2 item 5's
trade is real and the honest resolution is to stop forcing four components to share one setting. What
is *not* legitimate is a different accuracy claim per component hidden behind different knobs — the
reported range stays one number per pairing class, measured the same way for all four.

**D5 — no ACA, no vector fitting, no new package.** Unchanged from L9e, which measured ACA at 53–62%
rank and deferred it with the number.

**D6 — nothing outside `src/Engine/Mom/` moves, and no s-parameter that is accepted today may
change.** Every structure inside ρ/λ ≤ 0.1 must produce the same answer — bit-identically where the
path is unchanged, and to the fit's own reproducibility where it is not. That is L9b's R-dcm-1
precedent and it is the gate that says a "widening" did not quietly become a "different kernel".

---

## 2. What already exists, and what genuinely does not

**Exists, and must be reused rather than rewritten:**

- `SommerfeldIntegral.EvaluateInterior` / `CanIntegrateInterior`, and its whole Tier 3 ladder.
- `RadialRemainderTable.Build` / `BuildDerivative` / **`BuildFrom`** — the last one takes an arbitrary
  radial function and was added by the via z-integral brief for exactly this shape of need.
- `PlanarKernelTerms.FromDcimAtHeights` and `FromDcimAtHeightsMinusStaticAsymptotes`, and
  `PlanarKernelSet`'s shared fit cache and its `Model`/`Asymptote` accessors.
- `DcimSettings`' four sampling knobs, all reachable and all documented with the measurement that
  chose them.
- **L9c's Tier 5 harness itself** (`ViaBasisTests.M3_Tier5_TheFitVsTheOracle_PerPairingPerComponent_IsTheReportedCurve`,
  ~4 min, `Category=Benchmark`) — the curve this brief exists to move is already a test. **Extend it;
  do not start a second one.**

**Does NOT exist:**

- Any per-component `DcimSettings`. `PlanarKernelSet` holds one and hands it to every `FitAtHeights`.
- Any path that evaluates a spatial kernel without a fit.
- Any refusal scoped to a subset of a mesh's basis pairs.
- Any depth search other than Prony on the two fixed paths (plus the optional third sample block).

---

## 3. The three measurements, in order

**These milestones produce numbers and a recommendation. M0 changes a few lines; M1 changes a
default; M2 changes a path. Each is a legitimate place to stop.**

### M0 — is the refusal asking the right question? (R-zz-1)

`G_A^zz` appears only between two VERTICAL bases. Measure, on real meshes:

1. **The largest ρ between two vertical basis footprints**, against the mesh diagonal the refusal
   currently uses. On L9's phase-gate airbridge the two posts are 180 µm apart on a 316 µm diagonal —
   a small difference, and that fixture will not show the effect. **Build the case that does**: §10.7's
   own FR-4 hero at 10 GHz has a 0.67 λ diagonal, and a SINGLE via on it has a vertical-to-vertical ρ
   of about its own footprint — roughly 0.02 λ. Report both numbers for one-via, two-via-close and
   two-via-far layouts.
2. **What that does to the verdict.** If a single-via board-scale structure passes once the question is
   scoped correctly, that is most of the limit gone for a few lines of code.
3. **What governs everything else** (D2). The mixed component couples a via to *every* horizontal
   basis, so its ρ genuinely spans the mesh — it is inside 3.8e-3 out to ρ/λ = 1 and therefore needs
   no refusal today, but **say so explicitly with the number** rather than leaving it unasked. Same
   for `G_A^xx` and `G_q` at the interior pairings.

**Report:** the verdict table before and after, and whether any pairing class is left ungoverned.

### M1 — do the existing knobs already fix it? (R-zz-2)

§0.2 item 5. Re-run L9c's Tier 5 curve for `G_A^zz` alone, sweeping the knobs whose defaults were
chosen on a different fit and a between-component trade:

- `BranchSamples` ∈ {0, 32, 64, 128} × `BranchExtent` ∈ {0.5, 1, 2} — *"the softer alternative to
  pinning Taylor coefficients exactly"*, currently off because of a trade with `G_q`.
- `BranchPointOrders` ∈ {1, 2, 3} — 2 and 3 are exact statements that *"fight the sampled data"* for
  `G_q` and *"buy another 60×"* for `G_A` on the one-layer fit. **Neither has ever been measured at
  interior heights**, where L9c separately established that the sum rule is a theorem for a wholly
  different reason.
- `PathExtent` / `Samples`, and `FarPathExtent` / `FarSamples` / `FarOrder`.

**Report the table the way L8a and L8c report theirs** — every configuration, on every stack, for every
component, so the trade is visible rather than asserted. If a per-component setting takes `G_A^zz`
inside the envelope the other three already meet, **D4's per-component plumbing is the whole fix** and
M2 is not built.

### M2 — does the failing block need a fit at all? (R-zz-3)

The standing objection to `EvaluateInterior` is that it cannot fill a matrix. **It does not have to.**
At a fixed height pairing the ẑẑ kernel is a function of ρ ALONE, the fill already consumes it through
a radial table, and the ẑẑ block is 8 unknowns of 1,023 on L9's own fixture. So:

1. **Measure what a directly-integrated table costs.** A DCIM-based table on L9d's two-level fixture is
   10,636 samples and 15.6 ms. At 40–50 ms per `EvaluateInterior` point that is ~500 s — prohibitive.
   **But L9c measured a 100× coarsening at 9.6e-10**, and the table's own spacing is set from the
   mesh's smallest cell rather than from anything the kernel needs. **Measure the table's required
   sample count directly**, by refining until the assembled ẑẑ block stops moving, rather than
   inheriting the DCIM table's spacing.
2. **Report seconds per pairing per frequency**, and the fraction of a de-embedded point (65.5 s at
   N = 514, measured alone). A via-bearing mesh asks for a handful of ẑẑ pairings, not one per entry.
3. **And report what it buys**: the same Tier 5 curve with the fit replaced by the table, which should
   collapse to `EvaluateInterior`'s own accuracy — i.e. the limit becomes whatever the ORACLE's range
   is, and L9c's rungs say that is everywhere it was tested.

**If M2 lands, `ValidatedRhoOverLambdaAtHeights` is replaced by a COST decision rather than an accuracy
one**, and the refusal should be re-worded on that — R-mom-17's shape applied to seconds, exactly as
`SurfaceMesher`'s own N ceiling is.

### M3 — only if all three fail

Then the depth search is live, and it should be answered with **L7b-b's discipline**: build the
cheapest thing that could work, measure what it costs in accuracy against `EvaluateInterior`, and
report that number **before** committing to a hand-written non-Hermitian complex eigensolver this
repository has declined four times. Candidates in cost order: a third sampling path at a different
angle; a constrained least-squares amplitude solve on a fixed physically-motivated depth ladder;
GPOF with an SVD rank truncation. **Stopping here with the measurement is a complete outcome.**

---

## 4. Requirements

**R-zz-1 — the refusal is scoped to the pairs that use the kernel it is about.** Asked of the largest
separation between vertical bases, not of the mesh diagonal. Its message must quote the number it
actually used and say what it is a separation *between*, so a user can act on it (moving two vias
apart is not the same instruction as making the board smaller).

**R-zz-2 — `DcimSettings` becomes per-component, or is shown not to need to be.** If M1 says the
defaults already serve `G_A^zz`, say so and change nothing. If it says otherwise, the plumbing is a
lookup on `GreensKernel` in `PlanarKernelSet` and **the one-layer path must not see it at all**
(R-dcm-1: the shipped fit stays byte-identical).

**R-zz-3 — a direct-integration path is a SETTING, not a replacement.** Like `UseRadialTable = false`,
the fit stays reachable and remains the default until a measurement says otherwise. The two paths must
agree inside ρ/λ ≤ 0.1 to the fit's own accuracy, which is the rung that says the new path is wired
correctly rather than merely fast.

**R-zz-4 — whatever the constant becomes, its REFUSAL STRING carries the measurement.** The current
one names 14× on the GaAs slab, the Σ|A_i| = 1.1e9 diagnosis, and the fact that the remedy is a depth
search this repository declined. Whatever replaces it must be equally specific about what it is
refusing and why — R-mom-17, and `EmRefusalWordingTests`' sweep must stay green.

**R-zz-5 — D6's bit-identity.** Every structure accepted today produces the same answer. Pin it by
reconstruction at full precision on a small two-level fixture with a via, not by a tolerance.

---

## 5. The oracle ladder

| Tier | What | Where it comes from |
|---|---|---|
| 0 | **`EvaluateInterior`'s own rungs still pass** before anything is concluded from it — 2.2e-11 / 2.4e-15 / 4.0e-7 / 9.6e-10 / 5.4e-15 | L9c's Tier 3, unchanged |
| 1 | **M0's scoping is CORRECT**: on a mesh with vertical bases, the largest ρ any `VerticalVectorPotential` query actually sees equals the number the refusal used — asserted by instrumenting the fill, not by reading the code | new |
| 2 | **Nothing is left ungoverned** (D2): every pairing class either has a refusal or has a measured number in the note saying why it does not need one | new |
| 3 | **THE CURVE**: L9c's Tier 5 re-run, six stacks × two interior pairings × four components, against `EvaluateInterior`, scaled measure, at ρ/λ ≤ 0.1 and ≤ 1 | `ViaBasisTests.M3_Tier5`, extended |
| 4 | **R-zz-5 bit-identity** on a structure accepted today | reconstruction at full precision |
| 5 | **Reciprocity and passivity** of a solved two-level structure with a via, unchanged | `ViaBasisTests.M5_2` |
| 6 | **Cost**, measured ALONE, against 65.5 s per de-embedded point at N = 514 | `MultiLevelPortTests.M5_5` |
| 7 | **L9's phase gate re-run**, and L8's — both must stay green and gate 1's \|S₂₁\| table must not move | `L9PhaseGateTests`, `L8PhaseGateTests` |

**Tier 3 is the whole point.** If the curve does not come inside the ≤ 1.9e-2 envelope the other three
components already meet at ρ/λ = 1, the constant does not move, whatever else was built.

---

## 6. What must NOT be built here

- **Anything about the ground via, the via's current profile, or interior electrostatics** — those are
  `brief-ground-vias-and-interior-electrostatics.md` and are independent of this.
- **A general complex eigensolver, GPOF-with-SVD, vector fitting, ACA, or a new package** — not before
  M3, and M3 is a measurement with a report, not a licence.
- **An amplitude-conditioning cap** (§0.2 item 3). Measured worse. If you reach for it, you have not
  read L9c's Tier 5.
- **Any change to `Dcim.ValidatedRhoOverLambda` or `ValidatedRhoOverLambdaLayered`** — the ONE-LAYER
  and TOP-HALF-SPACE limits are governed by L8a's and L9b's own measurements and are not in scope.
- **Any change to the fill, the mesher, the basis set, the port model, or the calibration.**
- **A losslessness check.** Still not added anywhere, and still more true with vias.
- **A new starter technology.** Hand-build fixtures beside the tests that need them.

---

## 7. Milestones, each with its own gate

| M | What | Gate |
|---|---|---|
| **M0** | Scope the refusal to the pairs that use the kernel (R-zz-1, D2) | Tiers 1, 2; **a legitimate stopping point** |
| **M1** | The knob sweep, per component (R-zz-2) | Tier 3's curve; report the whole table |
| **M2** | Direct integration as a reachable path (R-zz-3) | Tiers 3, 6 |
| **M3** | The depth-search measurement, only if M0–M2 leave the limit standing | Numbers reported; **stopping here is complete** |
| **M4** | Whatever the constant becomes, plus its refusal wording (R-zz-4, R-zz-5) | Tiers 4, 5, 7 |

**The natural fault line is after M0**, and it may be the only milestone needed. §0.2 item 6 is a
few-line change against a limit that currently refuses a whole class of board-scale structures; if the
arithmetic in §3/M0 holds up, ship that, report it, and leave M1–M3 as a measured deferral.

---

## 8. File map (indicative)

```
src/Engine/Mom/PlanarSolve.cs      the extent the refusal is asked about (M0), and its wording
src/Engine/Mom/PlanarKernelSet.cs  WithinValidatedRange takes the right quantity; per-component
                                   DcimSettings if M1 says so
src/Engine/Mom/Dcim.cs             ValidatedRhoOverLambdaAtHeights and its refusal string — ONLY if a
                                   measured curve moves it
src/Engine/Mom/PlanarFill.cs       the direct-integration path as a setting (M2 only)
tests/Engine.Tests/Mom/ViaBasisTests.cs        Tier 3 — extend M3_Tier5, do not start a second file
tests/Engine.Tests/Mom/MultiLevelPortTests.cs  Tiers 1, 2, 4, 6
```

---

## 9. What to report back on, whatever else happens

1. **M0's two numbers** — the mesh diagonal and the largest vertical-to-vertical separation, on a
   one-via and a two-via board-scale layout — and whether the verdict changed.
2. **Whether anything was left ungoverned** by narrowing the question, and what you did about it.
3. **M1's table in full**, including the configurations that made things worse. The knobs' existing
   defaults are on the record with their own measurements; yours must be too.
4. **M2's seconds per pairing**, and the sample count the ẑẑ block actually needs — measured by
   refining until the block stops moving, not inherited from the DCIM table's spacing.
5. **What `ValidatedRhoOverLambdaAtHeights` became**, with the curve. If it did not move, say what it
   would take, in the same shape L9c's own deferral is written.
6. **Whether §10.7's FR-4 hero with a via runs at 10 GHz** at the end of this. That is the one-sentence
   answer to "did this work".
7. **Any place an accepted answer moved** (D6). If a structure inside ρ/λ ≤ 0.1 moved by an ulp, say so
   and say why rather than adjusting a tolerance.
