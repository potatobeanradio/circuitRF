# Sonnet Brief — wBond WB-A: the headless model and physics

**Design:** `docs/design/wbond.md`, approved 2026-08-07. That note is the specification; this brief
implements its **phase WB-A** — the framework-free half, everything below the UI. **No pixel is drawn
in this brief.** WB-B through WB-F (the component and its stamp, the editor, the assembly DRC, the
standalone binary, kernel W integration) each get their own brief; §10 names them so nothing is
orphaned.

**Why this is the cut.** The product rests on two claims: **the physics is right**, and **a drag is
cheap**. Both are provable with no UI in sight, and if either is wrong every editor decision in WB-C is
built on sand. `src/WBond` is framework-free by design (`wbond.md` §11), so this tranche is fully
headless-testable.

**A property worth knowing up front: WB-A is purely additive.** It adds one project and one test
project. It modifies **no** existing engine, device, or hot path — the component stamp is WB-B's job,
the editor is WB-C's. There is no `HbNewton`-class risk anywhere in this brief. Treat any pressure to
edit `src/Core` or `src/Engine` as a signal you have wandered into WB-B.

**Read, in this order, before planning anything:**

1. **`docs/design/wbond.md` §2, §3 (all of it), §4, §9.** §3 is the physics this brief exists to get
   right — read all six subsections, not a summary. **§3.4's derivation is complete and verified; do
   not re-derive it, implement it.** §4 is the measured budget every structural decision is sized
   against. §9 is the file format.
2. **`docs/design/mom-wirebond-kernel.md` §§3, 4.1, 9.1, 11.** §4.1 is the same quasi-static PEEC
   physics from the solver's side; §11 is the oracle list this brief's ladder extends. **Kernel W is
   not built here and nothing here may depend on it** (`wbond.md` WB1).
3. **`docs/design/harmonicarf.md` §3.1–§3.3.** The framework-free-project-plus-second-entry-point
   pattern that `src/WBond` copies exactly. §3.3's "what already exists" discipline is the one to
   imitate.
4. Then the code, not summaries of it: `src/Core/Devices/Temperature.cs` (the temperature plumbing
   R-wb-2 uses), `src/Core/Devices/SnpModel.cs` (branch-current expansion — read it now, it is WB-B's
   template and it tells you what shape `Z_arr(ω)` must be handed over in), `src/RfCore`'s `DataSet` /
   `DataCube` and its Touchstone writer, `tests/Firewall.Tests/UiFirewallTests.cs` (the assertion you
   are joining), `src/Ui/Layout/LayoutUnits.cs` (**read it, do not reference it** — see §0.3 item 1).

---

## Gate command

```
dotnet test tests/WBond.Tests    --no-build      # new in this brief
dotnet test tests/Firewall.Tests --no-build      # you are adding an assembly to its assertion
dotnet test tests/Core.Tests     --no-build
dotnet test tests/RfCore.Tests   --no-build
```

Run as separate commands — this SDK rejects more than one explicit project path per invocation
(`MSB1008`).

**You do not need `Engine.Tests` or `Ui.Tests` for this brief**, because WB-A touches neither. If you
find yourself needing them, you have exceeded scope — stop and report. (`Engine.Tests` is ~3 min 24 s
on its own; not running it when nothing can reach it is the point of scoping the gate.)

**Test-cost discipline.** The measured numbers in §0.2 say most of this is genuinely fast: a full cold
fill at 600 wires is ~0.54 s and belongs in the routine tier. What will cross the ~5 s threshold and
**must** carry `[Trait("Category","Benchmark")]`: the R3 multi-filament cross-check (R-wb-8), any
convergence sweep over segment count at large N, and the drag-budget measurement if you loop it.
Measure before you tag; state in the report what you added to the opt-in tier (currently ~40 min).

**One measurement discipline, restated because it has bitten this repo twice.** A benchmark sharing a
run with others reads more than twice as slow; L9d's 71.9 s was first mis-measured at 16.79 s that way.
**Take every timing measurement alone, and say in the report that you did.**

---

## 0. Read this before planning anything

### 0.1 What is being built

A framework-free library that holds a set of 3D bond wires, computes their mutual inductance matrix
from closed-form filament formulae with a ground-plane image, reduces it onto a user-defined array
basis, computes frequency-dependent resistance and internal inductance, does all of that
**incrementally** when one wire moves, and reads and writes `.wBond`. It has no UI, no schematic
component, and no opinions about either.

### 0.2 The measured budget — these numbers are the design

Taken while writing `wbond.md`, .NET 10 Release, single-threaded, on this machine, **before** any
implementation existed. They are re-takeable; re-take them if you doubt them.

| operation | measured |
|---|---|
| Grover skew kernel (general position) | **41.7 ns** / filament pair |
| Grover parallel kernel | **28.4 ns** / filament pair |
| cold full fill, 600 wires × 6 filaments, symmetry + images | **~0.54 s** |
| one wire moved — recompute 2N−1 wire-pair blocks | **~3.6 ms** |
| 50-wire group moved | **~173 ms** |
| Cholesky of **L** + 12 solves ⇒ **AᵀL⁻¹A**, N = 600 | **22.9 ms** |
| rank-1 Cholesky update, N = 600 | **0.144 ms** |
| Cholesky + solves, N = 1,200 | 181 ms |

**The finding that shapes the whole design (`wbond.md` WB13): the linear algebra is NOT the
bottleneck — the Grover fill is.** This inverts the usual intuition about a 600 × 600 matrix problem.
Every optimisation effort belongs in the fill and its caching, and essentially none in the solve.

**What this brief must preserve:** a single-wire drag update — block recompute + rank-2 factor update +
M triangular solves — in **≤ 10 ms at 600 wires**. If your implementation is materially worse, **stop
and report** rather than proceeding; WB-C has no way to recover it.

### 0.3 Six things that are true before you start

1. **The "framework-free" layout core is NOT referenceable, and this is a real constraint the design
   note does not spell out.** `LayoutUnits.cs`, `LayoutModel.cs`, `LayoutPersistence.cs` and
   `Drc/DrcEngine.cs` contain no Avalonia *in their source* — but they live in `CircuitRF.Ui`, which
   references Avalonia, so referencing any of them from `src/WBond` fails
   `tests/Firewall.Tests` immediately. **Verified, not assumed.** Consequences, all deliberate:
   - **Units:** `src/WBond` carries its own six-line nm-per-unit table (mil = 25,400 nm, µm = 1,000,
     mm = 1,000,000, inch = 25,400,000, nm = 1). Duplicating six integer constants is correct;
     taking an Avalonia dependency to avoid it is not. Add a comment pointing at `LayoutUnits.cs` as
     the authority, so the two cannot silently diverge, and note that if the display layer is ever
     lifted out of `src/Ui` (`ui-architecture.md` §4's deferred refactor) this copy folds into it.
   - **`.clay` embedding is an OPAQUE PASSTHROUGH in WB-A** — see R-wb-11. Do not parse it.
   - **The assembly DRC is WB-D**, not here, and it will live on the `src/Ui` side where `DrcEngine`
     is.
2. **The array-basis derivation is DONE and numerically verified.** `L_arr = (Aᵀ L⁻¹ A)⁻¹`, with
   current sharing `I = L⁻¹ A L_arr J`. It was checked against three independent excitation vectors to
   machine precision, and against the closed form `(L_s + (N−1)M)/N` for N identical coupled wires.
   **Implement it; do not re-derive it, and do not "improve" it into a different reduction.**
3. **Both Grover formulae are needed, and the crossover is 10⁻⁶ rad — measured, not guessed.** The
   skew formula converges to the parallel closed form to **9 digits at ε = 10⁻⁶** and only loses ~3
   digits by 10⁻⁸. It exists for *speed and exactness at ε ≡ 0*, not as numerical rescue. **Setting
   the crossover at a cautious 10⁻³ silently eats a 3 × 10⁻⁶ relative error on every nominally-parallel
   pair in the design** — which is most of them, in a real array.
4. **The `d ← max(d, GMD)` clamp is PHYSICS, not a numerical guard.** Consecutive filaments of the same
   wire share an endpoint, so d = 0 and the skew formula returns `NaN`. The right separation is not
   zero — they are the same conductor of radius *a*, so it is the cross-section's GMD. Measured: the
   formula is stable down to d = 10⁻¹⁴ m and `NaN`s only at exactly zero, so the clamp is **never
   load-bearing numerically.** Implementing it as an epsilon guard rather than as the GMD gives a
   finite, plausible, wrong answer.
5. **The internal impedance is a function of ONE dimensionless parameter.** `Z_int/R_dc` depends only on
   q = a/δ — not on radius, metal or frequency separately. That is what makes it a 1-D table rather
   than a complex Bessel evaluation per wire per frequency. Both asymptotes are verified (§5 tier 6).
6. **The default conductivities are 85 °C values, derived from a 20 °C reference.** The 85 °C column in
   `wbond.md` §2.3 is *computed*, not a second set of constants: the model stays
   σ(T) = σ₂₀/(1 + α₂₀(T − 20)). Storing the 85 °C numbers as literals and calling it done breaks
   `T = 20 °C` recovery and is the one way to get this wrong.

---

## 1. Decisions taken — do not relitigate these

Settled with the owner during design review. If implementation shows one is wrong, **stop and report**;
do not quietly substitute another.

- **D1 — `Wire.Points` is the truth.** A wire is always a 3D polyline. A `LoopProfile` is a *generator*
  that writes those points (the PCell pattern); breaking the binding leaves the points untouched.
- **D2 — Wire direction is data.** `Points[0]` is the input, `Points[^1]` the output. Reversing negates
  that wire's off-diagonal row and column. `Reverse Wire` is an explicit command; direction is never
  silently re-inferred.
- **D3 — The image is "mirror through z = 0 AND reverse traversal".** One rule; correct sign for
  horizontal (anti-parallel) and vertical (parallel) current alike.
- **D4 — Self-inductance is the parallel-filament mutual at d = GMD, with GMD = a (external only), and
  `L_int(f)` added from the same Bessel evaluation that gives `R(f)`.** Do **not** make the GMD
  frequency-dependent to fake the transition: it is right at both ends, wrong in between, and
  double-counts against the Bessel term.
- **D5 — `L_arr = (Aᵀ L⁻¹ A)⁻¹`.** Resistance never enters this. **A** is 0/1 with exactly one 1 per
  row; ungrouped wires each form their own single-member array so the algebra has no special case.
- **D6 — Maintain a Cholesky FACTOR of L, not an explicit inverse.** Rank-k factor updates are O(kN²)
  and stable; the M × M answer needs M triangular solves, never a full inverse.
- **D7 — Default material is gold; default operating temperature is a flat 85 °C**; both editable.
- **D8 — `src/WBond` is framework-free** and joins the `tests/Firewall.Tests` assertion.
- **D9 — `.wBond` stores setup only, never results.** Re-derived on open; a cold fill is 0.54 s.
- **D10 — The equipotential-pad limitation is documented, never warned about.** No message fires. There
  is no threshold that separates the good case from the bad without sheet resistance and frequency.
- **D11 — R1 (isolated Bessel) is the shipping resistance tier.** R2 (proximity correction) and R3
  (multi-filament) are staged; R3 exists in this brief **only** as a verification path (R-wb-8).

---

## 2. What already exists, and what genuinely does not

**Exists — use it, do not reimplement:**

| need | component |
|---|---|
| temperature-dependent material properties | `src/Core/Devices/Temperature.cs` |
| result container | `DataSet` / `DataCube` (`src/RfCore`) |
| Touchstone write | `src/RfCore`'s writer |
| the versioned-JSON persistence pattern | `DataDisplayConfig` — `FormatVersion`, absent field ⇒ built-in default |
| the framework-free-project pattern + its firewall entry | `src/Harmonica` and `tests/Firewall.Tests/UiFirewallTests.cs` |
| the branch-current-expansion stamp shape WB-B will need | `src/Core/Devices/SnpModel.cs` (read now, implement in WB-B) |
| dense linear algebra | NumFlat — **check what it gives you before hand-rolling Cholesky**; a rank-k update may still need writing |

**Does NOT exist — this brief builds it:**

- Every Grover filament formula, in any form.
- The image construction and its sign rule.
- The array-basis reduction.
- The incremental fill and the block cache.
- A Cholesky rank-k update (unless NumFlat has one — check first).
- The `Z_int/R_dc` table and its Kelvin/Bessel evaluation.
- `.wBond` I/O, the CSV importer.
- Anything named `WBond`.

---

## 3. M1 — the measurement that decides whether WB-C is possible

**Do this first and report before building on it.**

§0.2's numbers were taken on **flat scalar arguments in a tight loop** — no object graph, no polyline
indirection, no 600 × 600 block map, no bounds checks. The real fill walks
`WireArray → Wire → Point3[]` and writes into a block-structured matrix. **A naive faithful
implementation can easily land at 100–150 ns/pair instead of 41.7**, which turns the 3.6 ms
single-wire drag into 12 ms and the cold fill into 1.6 s — and WB-C's 60 fps claim dies quietly.

**Build the two kernels and the fill over the REAL data structures first, then measure:**

1. ns per filament pair, skew and parallel, called through the real geometry accessors.
2. cold full fill, 600 wires × 6 filaments, with images — against the 0.54 s reference.
3. one-wire incremental block recompute — against the 3.6 ms reference.

**Report those three numbers before continuing.** If (1) is worse than ~2× the reference, say so and
say what dominates — most likely candidates, in order: `Point3` as a heap type rather than a `readonly
record struct`, recomputing segment direction/length per pair instead of caching per segment, or
`Math.Atanh`/`Math.Atan2` being called with recomputed arguments. **Do not micro-optimise past 2×
without reporting**; the budget has ~3× of headroom at 5 ms against a 16.7 ms frame and burning the
brief on SIMD would be the wrong trade.

**One structural hint that is not optional:** precompute per-segment invariants (unit vector, length,
midpoint) once per fill, not once per pair. There are 3,600 segments and 13 M pairs; anything computed
per pair that depends on only one segment is being computed 3,600× too often.

---

## 4. Requirements

### R-wb-1 — the data model
`WBondDesign` → `WireArray[]` → `Wire[]`, plus `Material[]`, `LoopProfile[]`, `GroundPlane`,
`OperatingTemp`. A wire is `Point3[]` (≥ 2) + diameter + material ref + profile binding. **A wire
belongs to exactly one array** (D5). Refuse an empty array — it makes **A** rank-deficient and
`L_arr` singular, and the failure would otherwise surface as a confusing linear-algebra error far from
its cause.

### R-wb-2 — materials and temperature
Store σ at the **20 °C reference** with α₂₀; report and use σ at `OperatingTemp`, default **85 °C**
(D7, §0.3 item 6). Gold is the default material. Uses `Temperature.cs`.

### R-wb-3 — Grover general (skew) filaments
`wbond.md` §3.1 formula (a), verbatim. Crossover to (b) at **ε < 10⁻⁶ rad** (§0.3 item 3).

### R-wb-4 — Grover parallel filaments
`wbond.md` §3.1 formula (b): `M = (μ₀/4π)[f(s+m) − f(s) − f(s+m−l) + f(s−l)]`,
`f(z) = z·asinh(z/d) − √(z²+d²)`. **This form is the one that has been hand-verified** against the
skew limit and against Grover's closed form for equal overlapping filaments — a plausible-looking
different combination of the four end-pair terms is the easiest thing in this brief to get subtly
wrong, and it will pass a self-consistency check while failing tier 1.

### R-wb-5 — the GMD clamp
`d ← max(d, GMD)` everywhere, self and mutual (D4, §0.3 item 4).

### R-wb-6 — the image
Mirror through z = 0, reverse traversal, `L_ij = M(i,j) − M(i, image(j))` (D3). **Gated by tier 2, and
tier 2 asserts the horizontal and vertical cases separately.**

### R-wb-7 — the array reduction
`L_arr = (Aᵀ L⁻¹ A)⁻¹` via the Cholesky factor (D5, D6). Because **A** is 0/1, `Aᵀ Γ A` is a block
*sum*, not a matrix multiply — do not emit a general GEMM for it. Publish per-wire current sharing
`I = L⁻¹ A L_arr J` alongside; it is two extra triangular solves and WB-C's panel needs it.

### R-wb-8 — resistance and internal inductance
`Z_int/R_dc` as a function of q = a/δ alone (§0.3 item 5), evaluated to double precision and validated
against both asymptotic series (tier 6). R1 is the shipping tier. **R3 (multi-filament) is built here
as a VERIFICATION path only** — it is the same Grover kernel with ring filaments, so it costs almost
nothing to add and it is the only honest way to bound R1's proximity error on a real array. Report
that bound; do not ship R3 as a mode.

### R-wb-9 — the incremental fill
Moving wire *k* changes exactly row and column *k* of **L** — `ΔL = e_k rᵀ + r e_kᵀ`, **rank 2 whatever
N is**. Recompute 2N−1 wire-pair blocks, rank-2-update the Cholesky factor, re-solve. **Gated by
tier 7, which asserts bit-level agreement with a full rebuild** — an incremental path that drifts from
the cold path is worse than no incremental path, because it is invisible.

### R-wb-10 — rigid-motion invariance
Under a rigid **translation** of a selection, intra-selection direct mutuals are unchanged; under a
**horizontal** translation the intra-selection image mutuals are unchanged too. Exploit both. Worth
~8 % for 50-of-600 and ~33 % when a whole 200-wire array moves. **Gated by tier 7 as well** — the
invariance must be exact, not approximate, or it is a bug generator.

### R-wb-11 — `.wBond` I/O
Versioned JSON, `FormatVersion`, absent field ⇒ built-in default. Stores wires, arrays, profiles and
bindings, materials, ground plane, operating temperature, view state, parameter values. **No results**
(D9). **Embedded layout geometry is stored and round-tripped as an OPAQUE JSON blob in WB-A** (§0.3
item 1) — WB-A must preserve it byte-faithfully across a load/save cycle without interpreting it, and
a test must assert that round-trip. PDK-PCell flattening and cell-reference resolution are WB-C's,
where the layout model is reachable.

### R-wb-12 — CSV wirebond-table import
from-pad / to-pad / profile / diameter / material / array, one row per wire (`mom-wirebond-kernel.md`
RW16). This is how a 600-wire design actually arrives, and it is what makes WB-A demonstrable before
any editor exists. A malformed row reports its line number and what it expected — it does not skip
silently.

---

## 5. The oracle ladder

Each tier is an independent check. **Where a tier names a closed form, that closed form is the
oracle — not another wBond path agreeing with itself.**

| tier | what | pass |
|---|---|---|
| **0** | Parallel-filament mutual (R-wb-4) vs. Grover's closed form for equal overlapping filaments, `M = (μ₀/2π)[l·asinh(l/d) − √(l²+d²) + d]` | ≤ 1e-12 rel |
| **1** | **Skew formula → parallel formula as ε → 0.** Two independent formulations, checked against each other at ε = 10⁻², 10⁻³, 10⁻⁶ | **9 digits at 10⁻⁶** (measured; do not weaken) |
| **2** | **The image sign, horizontal and vertical asserted SEPARATELY** by hand-derived sign; then wire-over-ground vs. `L = (μ₀ℓ/2π)·ln(2h/a)` | sign exact; ≤ 1e-6 rel vs. closed form |
| **3** | Self-inductance via GMD vs. Rosa `(μ₀ℓ/2π)[ln(2ℓ/a) − 1 + ¼]` | **0.23 % at ℓ/a = 79** — this is Rosa's own ℓ≫a error, not yours. Assert the value, and assert it *tightens* as ℓ/a grows |
| **4** | Array reduction vs. `(L_s + (N−1)M)/N` for N identical coupled wires | ≤ 1e-12 rel |
| **5** | `u = L_arr·(AᵀL⁻¹A)·u` for ≥ 3 random excitations; `L_arr` symmetric and positive definite for random geometries; current shares sum to the array current | machine precision |
| **6** | `Z_int/R_dc` vs. **both** series: small-q `1 + q⁴/48`, large-q `q/2 + ¼ + 3/(32q)` | ≤ 1e-6 for q ≤ 0.5 and q ≥ 5 **(see the gap note below)** |
| **7** | **Incremental fill vs. full rebuild** after a single-wire move, a rigid group translation, and a horizontal group translation | **bit-identical** |
| **8** | Order invariance: `L_arr` unchanged by wire ordering within an array, and by array ordering (up to the corresponding permutation) | machine precision |
| **9** | Composition: a wire of length 2ℓ vs. two cascaded ℓ wires; reversing a wire negates exactly its off-diagonal row and column and nothing else | ≤ 1e-10 / exact |
| **10** | Cost: §3's three numbers, plus a single-wire drag update at 600 wires against the 10 ms budget, plus R3's proximity bound on R1 | reported, **measured alone** |

**Tier 6's gap is real and must not be papered over.** The two series bracket the useful range but
**neither is accurate for q ≈ 1–4**: at q = 2 the exact value is 1.264643 while the small-q series
gives 1.333 (+5.4 %) and the large-q gives 1.296875 (+2.5 %). That band is not exotic — it is roughly
100 MHz–1 GHz for a 0.5 mil gold wire, i.e. the low end of the tool's own range. **Tier 6 therefore
gates the series only where each is valid, and the middle band is gated by an independent
complex-Bessel evaluation** (series-summed `I₀`/`I₁` of complex argument, or Kelvin `ber`/`bei` of
√2·q — both are standard). Do not tune a single fit to straddle the gap and call it validated.

**Tiers 1 and 2 are the ones that matter most.** Tier 1 is the only check that tests one formulation
against a genuinely independent one. Tier 2 is the classic silent failure: a wrong image sign produces
a plausible, self-consistent, **10–30 % wrong** array inductance that no self-consistency check will
ever catch.

---

## 6. What must NOT be built here

- **Any UI. Any Avalonia. Any drawing.** Not a control, not a view-model, not a colour.
- **Any reference from `src/WBond` to `src/Ui`** — including the "framework-free" layout files (§0.3
  item 1). The firewall test will catch it; catching it in design is cheaper.
- **The component, the dynamic symbol, or the MNA stamp** — WB-B. In particular **do not implement
  `Z_arr(ω) = (AᵀZ⁻¹A)⁻¹`** here; WB-A ships `L_arr` only. Shape the reduction so the complex version
  is the same code over a different scalar type, and stop there.
- **The editor, the profile view, alt-drag, transforms, the panel** — WB-C.
- **The assembly DRC, `.wasm`, any 3D rule predicate** — WB-D.
- **The standalone entry point and build configuration** — WB-E.
- **Any MoM, any kernel W code, any mesh** — WB-F, and downstream of `mom-wirebond-kernel.md` LW1.
- **PDK-PCell flattening or `.clay` interpretation** — R-wb-11 keeps it opaque.
- **R2 or R3 as shipping resistance modes.** R3 is a verification path only (R-wb-8).
- **Any change to `src/Core`, `src/Engine`, `src/Ui`, or any existing golden.** WB-A is additive.
- **Widening any validated limit anywhere in the repo.** Nothing here needs one.

---

## 7. Milestones, each with its own gate

| M | What | Gate |
|---|---|---|
| **M1** | Both Grover kernels + the fill over real data structures, and §3's three measurements | **Tiers 0, 1**; the three numbers reported; **a legitimate stopping point** |
| **M2** | `src/WBond` skeleton + `tests/WBond.Tests` (both into `circuitrf.slnx`), data model, materials/temperature, units table | Firewall green with `CircuitRF.WBond` added; R-wb-1, R-wb-2 |
| **M3** | Images, GMD self-inductance, the assembled wire-basis **L** | **Tiers 2, 3** |
| **M4** | The array reduction + current sharing | **Tiers 4, 5, 8** |
| **M5** | `Z_int/R_dc` table, R(f), L_int(f); R3 verification path | **Tier 6** incl. the gap band; R1's proximity bound reported |
| **M6** | Incremental fill, block cache, Cholesky factor + rank-k update, rigid-motion invariance | **Tiers 7, 9**; the 10 ms drag budget (**tier 10**) |
| **M7** | `.wBond` I/O incl. opaque-blob round-trip, CSV import | R-wb-11, R-wb-12 |

**Three fault lines where stopping is the right answer.**

- **After M1.** If the real-structure kernel is materially worse than 2× the reference, that changes
  what WB-C can promise and **the owner decides, not you.**
- **After M3.** If tier 2 does not reproduce `ln(2h/a)`, **stop.** Everything downstream assumes the
  image is right, and a wrong sign looks like plausible physics. Do not adjust a tolerance to pass.
- **After M4.** If tier 4 or 5 fails, the reduction is wrong — and since `wbond.md` §3.4 verified it
  numerically before implementation, a failure here means the *implementation* diverged from a
  derivation known to be correct. Report which, rather than editing the formula.

M3 and M4 together are the correctness heart of the brief. **If M6 turns out larger than it looks,
stopping after M5 and reporting is a good outcome** — a correct, well-oracled physics library with a
cold-fill-only path is genuinely useful (it already supports CSV-driven analysis), and it leaves WB-B
completely unblocked, since the stamp needs `L_arr`, not the incremental path.

---

## 8. File map (indicative)

```
src/WBond/                          NEW project — framework-free, no Avalonia
  CircuitRF.WBond.csproj            references Core, Engine, RfCore
  WBondDesign.cs                    R-wb-1: design, arrays, wires, profiles, ground plane
  Materials.cs                      R-wb-2: 20 °C reference + α₂₀, σ(T), gold default
  WBondUnits.cs                     §0.3 item 1: the six nm-per-unit constants, with the
                                    comment pointing at src/Ui/Layout/LayoutUnits.cs
  Grover.cs                         R-wb-3/4/5: both kernels, the ε crossover, the GMD clamp
  ImageGround.cs                    R-wb-6: mirror + reverse traversal
  InductanceMatrix.cs               the assembled L, per-segment invariant precompute (§3)
  ArrayReduction.cs                 R-wb-7: L_arr = (A^T L^-1 A)^-1, current sharing
  InternalImpedance.cs              R-wb-8: the q-table, R(f), L_int(f)
  MultiFilament.cs                  R-wb-8: R3 verification path only
  IncrementalFill.cs                R-wb-9/10: block cache, rank-k Cholesky, rigid invariance
  WBondIo.cs                        R-wb-11: versioned JSON, opaque geometry blob
  WireTableCsv.cs                   R-wb-12

tests/WBond.Tests/                  NEW — the full §5 ladder
tests/Firewall.Tests/UiFirewallTests.cs   add CircuitRF.WBond to the assertion table
circuitrf.slnx                      both new projects
```

---

## 9. What to report back on, whatever else happens

1. **M1's three numbers** (§3) against the 41.7 ns / 0.54 s / 3.6 ms references, and what dominates if
   they are worse. **This is the gating result of the brief.**
2. **Tier 1's measured agreement** between the two Grover formulations at ε = 10⁻⁶, and where you set
   the crossover. If you moved it off 10⁻⁶, say why.
3. **Tier 2's result, horizontal and vertical stated separately**, and the measured error against
   `ln(2h/a)`.
4. **Tier 6's behaviour in the q ≈ 1–4 gap** — what you used there, and its measured error against an
   independent complex-Bessel evaluation.
5. **R3's proximity bound on R1** at a realistic array pitch (s/a ≈ 8). This number tells the owner
   whether R2 is worth scheduling; nobody knows it yet.
6. **The single-wire drag update at 600 wires**, measured alone, against the 10 ms budget — and
   separately, how much of it is fill vs. factor update vs. solves. WB13 predicts the fill dominates;
   confirm or refute it with numbers.
7. **What you added to the `Category=Benchmark` tier**, in minutes.
8. **Anything in `wbond.md` that turned out to be wrong.** The design was written against closed-form
   derivations and standalone measurements, not a running implementation — treat a contradiction as a
   finding to report, not an obstacle to work around. §3.4's derivation and §4.1's measurements are the
   two places a contradiction would matter most.

---

## 10. The follow-on briefs (not this one)

Named so the scope of *this* brief is unambiguous and nothing is orphaned:

| brief | phase | scope |
|---|---|---|
| `brief-wbond-wbb-component-and-stamp` | WB-B | Dynamic symbol generation, M-coupled-branch stamp of `Z_arr(ω)` (the exact complex reduction), `REF` pin + return-path refusal, expression-bound parameters, loop-height sweep, the coupling audit (WB30 — **load-bearing in v1**) |
| `brief-wbond-wbc-editor` | WB-C | Layout Editor + profile view + panel; `LoopProfile` binding; selection/drag/keyboard; alt-drag height/span scaling; draw, duplicate-with-pitch, rotate-about-end-point, reverse wire; units; pH readout; snapping; hierarchy descent; clipboard; envelope rendering; `.clay` embedding and PDK-PCell flattening |
| `brief-wbond-wbd-assembly-drc` | WB-D | The `.wasm` document and resolver, `DrcLayerExprParser` reuse, wire-set operands, 3D segment-to-segment predicates, the loop-height-vs-span envelope, machine/process/material sections |
| `brief-wbond-wbe-standalone` | WB-E | Third entry point + build config (**`<StartupObject>` set explicitly for all THREE configurations** — R-h8-5 bites on the third exactly as it did on the second; **assembly name stays `CircuitRF.Ui`** for RfCore's `InternalsVisibleTo`), project-tree drag-drop, Touchstone export |
| `brief-wbond-wbf-kernel-w` | WB-F | Fidelity selector routing to kernel W1/W2. **Downstream of `mom-wirebond-kernel.md` LW1** |
