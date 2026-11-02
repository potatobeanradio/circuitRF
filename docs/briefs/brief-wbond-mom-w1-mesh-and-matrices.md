# Sonnet Brief — wBond MoM WM-1: the segment mesh and the frequency-independent matrices

**Design:** `docs/design/mom-wirebond-kernel.md` (kernel W, staged W1 → W2 → W3) and
`docs/design/wbond.md` §3. This brief implements the **first half of kernel W1** — the mesh and every
matrix that does not depend on frequency. **It contains no solve and produces no S-parameters.**
WM-2 adds the solve, the N-port and the cross-check against the analytic model; WM-3 is the
performance tranche. §12 names both so nothing here is orphaned.

**Where findings go: `src/WBond/Mom/RESOLVED.md`, which you create in this brief.**
**Do not write in any `CLAUDE.md`.** Not the repo root one, not `src/Engine/Mom/CLAUDE.md`, not a new
one. Everything you learn, every measurement, every place this brief was wrong — all of it goes in
`RESOLVED.md`, in the style of `src/Engine/Mom/RESOLVED.md` (read its first 40 lines for the shape).

---

## Gate command

```
dotnet test tests/WBond.Tests --no-build
dotnet test tests/Firewall.Tests --no-build
```

Run as separate commands — this SDK rejects more than one explicit project path per invocation
(`MSB1008`). You need neither `Engine.Tests` nor `Ui.Tests`: this brief adds files under
`src/WBond/Mom/` and touches nothing else. **If you find yourself editing `src/Core`, `src/Engine`
or `src/Ui`, stop and report — you have left this brief's scope.**

### Test-cost discipline — read this before writing a single test

MoM tests are trivially easy to make slow, and this repo's routine gate is already ~4 min. The rule
for this brief:

- **Every routine test uses N_s ≤ 250 segments.** That is an 8-wire array at 24 segments each, or a
  single wire at 200 — enough for every oracle in §9, and the fills are milliseconds.
- The fill is O(N²) in Grover calls at ~30–45 ns each. N_s = 250 is ~30 k calls with images: under a
  millisecond. There is **no reason** for a routine test in this brief to exceed a second.
- Anything you measure at or above **~5 s must carry `[Trait("Category","Benchmark")]`**, and must be
  measured **alone** (`--filter` on the class, `--no-build`) before you tag it. A benchmark measured
  inside a full run reads more than twice slow — that mistake has been made twice in this repo.
- **Do not write a convergence sweep in the routine tier.** §9.7's convergence gate uses four segment
  counts on a *single wire*; if yours takes more than a second you have picked counts that are too
  large.
- State in your report exactly what you added to the opt-in Benchmark tier (currently ~40 min
  repo-wide) and what each one measured.

---

## 0. Read this before planning anything

### 0.1 What is being built, in one paragraph

`src/WBond` today computes a **wire-basis** quasi-static model: one current basis function per wire
(uniform axial current) and one charge basis function per wire (uniform charge per unit length),
reduced onto a user-declared array basis. That is a lumped model, and it dies as a wire approaches a
tenth of a wavelength. This brief keeps every kernel that model uses — `Grover.Mutual`,
`PotentialCoefficients.Kernel`, `Filament.Image`, `InternalImpedance.PerMetre` — and **changes only
the cells they are filled over**: from whole wires to **segments** (current) and **nodes** (charge).
The result is a distributed ladder with full mutual coupling, valid far past where the lumped model
is. Nothing about the physics is new. What is new is the discretisation, the incidence bookkeeping,
and the frequency-independent assembly that makes a sweep cheap.

### 0.2 The single most important structural fact

**Every matrix in this brief is frequency-independent.** `L`, `P`, `A`, `R`, `G`, `K̃`, `W`, `H` are
filled once for a design and never refilled. WM-2's per-frequency work is one diagonal update and one
factorisation. That property is the entire speed argument for kernel W1 — it is what
`mom-wirebond-kernel.md` §4.1 means by "the property that makes it feel fast" — and **anything you
build in this brief that has an `omega` parameter in it is a design error.** The only exception is
the per-segment internal impedance table of §7, which is diagonal, closed-form, and costs nothing.

### 0.3 Seven things that are true before you start

1. **The segment-basis inductance fill costs exactly what the wire-basis fill already costs.**
   `InductanceMatrix.Block` already loops over every ordered *filament* pair of two wires and sums
   them down into one number. The segment basis does the identical loop and simply does not sum
   down. A measured 600-wire cold fill is ~0.54 s over ~3,600 filaments; the same 3,600 filaments as
   a segment matrix cost the same Grover calls. **You are not making the fill more expensive, you are
   keeping more of its output.** Say this in `RESOLVED.md` once you have measured it, because it is
   the fact that makes the whole kernel affordable.

2. **`WireMesh` already flattens a design into filaments with images and per-wire index spans, and
   `Filament.Image()` already carries the current-reversal sign.** Read `src/WBond/InductanceMatrix.cs`
   lines 1–170 and `src/WBond/Grover.cs` lines 1–70 before you design anything. You are writing a
   *finer* mesh, not a different one, and it must reuse `Filament`, not invent a segment struct.

3. **The two sign rules are opposite and both already written down.** `InductanceMatrix.Block` **adds**
   its image term (the reversal is baked into the image filament's direction);
   `PotentialCoefficients.Block` **subtracts** its image term (a charge has no direction, and the
   image charge is negative). Copy both, verbatim, with the comments. An image-sign error here is a
   finite, plausible, non-NaN wrong answer — which is why §9.4 tests it by flipping the sign and
   confirming the oracle fails.

4. **`PotentialCoefficients.Kernel(p, q, farThresholdFactor)` is exactly the double line integral
   `∫∫ ds ds′ / |r−r′|` between two filaments, in metres, positive, direction-independent.** It is
   the only kernel the charge fill needs. Its near/far threshold is **3.5**, measured, and the reason
   it is 3.5 rather than the 3 an earlier brief proposed is written in its own doc comment — a
   threshold of 3 misses the 0.1 % target at 0.17 %. **Do not re-tune it here.** It was measured
   against *wire*-length cells; §6.4 says what to do about that.

5. **`src/WBond` is a leaf project with no `ProjectReference` at all, and it must stay one.** It
   cannot see `RfCore`, so it cannot produce a `DataSet` or an `RFNetwork`. That is deliberate — it is
   what lets `src/Core` reference it without a cycle. **This brief adds no project reference.** The
   kernel returns raw `Complex[]` / `double[]` and WM-2 converts, in exactly the way
   `src/Ui/WBond/WBondTouchstoneExport.cs` already converts the analytic model's output.
   `tests/Firewall.Tests` asserts the no-UI half of this and will fail if you reach for Avalonia.

6. **Do not implement `IEmKernel`, and do not add an `EmAnalysisKind`.** `src/Engine/Mom/`'s own
   registry documents why: `EmProblem` (a 2-D cross-section) and `PlanarProblem` (a plan view) are
   deliberate *siblings* with no shared base, because "two things that are genuinely different,
   described by two types, is the cheapest arrangement to be correct in". A 3-D wire array is a third
   such thing. Kernel W gets its own entry point with its own honest signature, in `src/WBond/Mom/`,
   next to the physics it reuses. Registry integration is a later, separate decision and is **not** in
   any of these three briefs.

7. **The retarded form (kernel W2) is not built here and nothing here may assume it.** Keep the
   quasi-static kernels exactly as they are. WM-3 §11 names W2 as the phase after these three.

---

## 1. Where the code goes

```
src/WBond/Mom/WireMomMesh.cs          — segmentation, nodes, incidence, terminals, the mesh report
src/WBond/Mom/WireMomSettings.cs      — the knobs (segmentation target, caps, thresholds)
src/WBond/Mom/SegmentInductance.cs    — L,  N_s × N_s, real symmetric
src/WBond/Mom/NodePotential.cs        — P,  N_n × N_n, real SPD
src/WBond/Mom/SegmentInternalZ.cs     — the per-segment diagonal D(ω) source
src/WBond/Mom/MomAssembly.cs          — G, K̃, W, H — the frequency-independent reduction
src/WBond/Mom/RESOLVED.md             — your findings. NOT CLAUDE.md.
tests/WBond.Tests/Mom/…               — the oracles of §9
```

Namespace `CircuitRF.WBond.Mom`.

---

## 2. The formulation, in full — implement this, do not re-derive it

Quasi-static PEEC, thin-wire, one axial-current unknown per segment and one charge unknown per node.
The derivation below is complete and has been checked; §2.6 lists the four checks so you can confirm
it rather than redo it.

### 2.1 Symbols

| | size | what |
|---|---|---|
| `N_s` | | segments (current cells) |
| `N_n` | | nodes (charge cells) |
| `M` | | wire arrays |
| `T = 2M` | | terminals — `G1.i, G1.o, G2.i, …` |
| `N_r` | | reduced nodes = `N_n` − (nodes merged by terminal shorting) |
| **L** | N_s × N_s | segment partial inductance (external), H. Real symmetric. |
| **P** | N_n × N_n | node coefficients of potential, F⁻¹. Real symmetric positive definite. |
| **A** | N_s × N_n | incidence. `A[k, start(k)] = +1`, `A[k, end(k)] = −1`. |
| **R** | N_n × N_r | the shorting map. 0/1; **exactly one 1 per row**. |
| **Ã** | N_s × N_r | `A R` |
| **E** | N_r × T | the terminal selector — reduced nodes are ordered **terminals first**, so `E = [I_T ; 0]` |
| **D(ω)** | N_s diagonal | per-segment `R_seg(f) + jω·L_int,seg(f)` |

### 2.2 The two physical relations

Branch (PEEC voltage drop along segment *k*, mutual coupling included):

```
(A V)_k  =  V_start(k) − V_end(k)  =  Σ_j Z_kj(ω) I_j        with  Z(ω) = jω L + D(ω)
```

Node (charge conservation at node *n*):

```
(Aᵀ I)_n  +  jω Q_n  =  I_ext,n                              with  V = P Q
```

`(Aᵀ I)_n` is the net current *leaving* node *n* through segments — a segment starting at *n* carries
current away and contributes `+I_k` with `A[k,n] = +1`; a segment ending at *n* contributes `−I_k`.

### 2.3 Terminal shorting, done exactly

Every node belongs to exactly one **reduced node**: either a terminal (all input-end nodes of array
*k*'s wires collapse into terminal `2k`; all output-end nodes into terminal `2k+1`) or itself. So
`V = R u` with `u` the reduced-node potentials, `R` is 0/1 with one 1 per row, and the terminal
potentials are `v_p = Eᵀ u`. External current enters only at terminals: `I_ext = R e`, `e = E i_p`.

**This is what makes shorting exact rather than approximate.** Two wires of one array genuinely share
both end nodes; forcing their potentials equal is the physical statement, and summing rows and
columns of a node matrix is only equivalent to it because `R` has one 1 per row.

### 2.4 The reduction

Substituting `V = R u`, `Q = P⁻¹ R u`, `I_ext = R E i_p` and using `Rᵀ Aᵀ = Ãᵀ`:

```
Ã u              =  Z(ω) I
Ãᵀ I  +  jω G u  =  E i_p ,          G ≡ Rᵀ P⁻¹ R          (N_r × N_r, SPD)
```

Eliminate `u`:

```
u  =  G⁻¹ (E i_p − Ãᵀ I) / (jω)
⇒   [ jω Z(ω) + K̃ ] I  =  W i_p
```

with the three **frequency-independent** blocks

```
K̃  =  Ã G⁻¹ Ãᵀ      (N_s × N_s, real symmetric)
W   =  Ã G⁻¹ E       (N_s × T)
H   =  Eᵀ G⁻¹ E      (T × T)
```

and, writing `M̃(ω) ≡ jω Z(ω) + K̃ = (jω)² L + jω D(ω) + K̃`:

```
I         =  M̃(ω)⁻¹ W i_p
Z_port(ω) =  ( H  −  Wᵀ M̃(ω)⁻¹ W ) / (jω)                     ← the whole answer
Y_port    =  Z_port⁻¹
```

**That is the deliverable shape.** WM-2 implements the last three lines. WM-1 delivers `L`, `P`, `A`,
`R`, `Ã`, `G`, `K̃`, `W`, `H` and the machinery for `D(ω)`.

### 2.5 Why this arrangement and not the obvious one

The obvious PEEC arrangement solves the nodal admittance `Y_node(ω) = Aᵀ Z(ω)⁻¹ A + jω P⁻¹` and then
Schur-complements onto the ports. That needs **two** dense factorisations per frequency (one for `Z`,
one for the internal-node block) and it needs `P⁻¹` formed explicitly. The arrangement above needs
**one** factorisation per frequency, of one `N_s × N_s` complex **symmetric** matrix, and everything
else is precomputed. On a 40-wire array at 24 segments (`N_s ≈ 960`) that is the difference between
~20 ms and ~50 ms per point; on the 200-wire case it is the difference between six seconds and three.

**A route you must not spend time on.** `ImpedanceReduction`'s doc comment names an eigendecomposition
shortcut — when every wire shares a radius and a metal, `D(ω)` is a scalar multiple of a diagonal, so
`Z(ω)` can be diagonalised once and inverted per frequency for free. **That shortcut does not transfer
to this kernel.** `M̃(ω) = (jω)²L + jω D(ω) + K̃` is a *quadratic* pencil in `jω` whose three matrices
`L`, `Λ` and `K̃` are not simultaneously diagonalisable, so no single similarity transform frees the
sweep. Record that in `RESOLVED.md` — it is a negative result worth having on paper, and the next
person to read `ImpedanceReduction`'s comment will otherwise chase it.

### 2.6 Four checks that confirm the algebra rather than redoing it

Put these in `RESOLVED.md` once you have confirmed each, with the test that confirms it:

1. **`Z_port` is complex symmetric by construction** — `H`, `K̃`, `L` and `D` are all symmetric, so
   reciprocity is structural, not a rounding accident.
2. **`M̃(ω) → K̃` as ω → 0, and `K̃` may be singular.** `null(K̃) = null(Ãᵀ)`, which is non-trivial
   exactly when terminal shorting creates a loop (two wires in one array, shorted at both ends, are a
   loop). `W`'s columns lie in `range(Ã) ⊥ null(Ãᵀ)`, so the blow-up is projected out and `Z_port`
   stays finite — but the **conditioning of `M̃` degrades like 1/ω**. That is a named risk; WM-2 §8
   gates the lowest usable frequency. It is not a bug to fix in WM-1.
3. **`Z_port → (H − Wᵀ K̃⁺ W)/(jω)` at DC** — purely capacitive, which is the right answer for
   terminals referenced to a plane through nothing but shunt capacitance.
4. **`Eᵀ G⁻¹ Ãᵀ = (Ã G⁻¹ E)ᵀ = Wᵀ`** — `G` is symmetric, so `W` serves both places and is computed
   once.

---

## 3. The mesh — `WireMomMesh`

### 3.1 Segmentation: subdivide, never merge

Each `Wire` is a polyline of ≥2 `Point3`. **Subdivide each polyline segment into
`ceil(len_i / maxSegmentLength)` equal parts.** Never merge two polyline segments into one, never
move a vertex.

That rule is chosen for three reasons and each one is worth keeping:

- **It preserves the authored geometry exactly.** Every original vertex survives as a node.
- **It makes subdivision invariance testable** (§9.2), which is the sharpest gate in this brief.
- **It is deterministic** — the same design meshes the same way every time, so a cached fill is safe.

```
maxSegmentLength = wire.PathLengthMetres() / settings.TargetSegmentsPerWire
```

`TargetSegmentsPerWire` defaults to **24**. That is a starting value, not a measured one — §9.7's
convergence test is what justifies it or changes it, and whichever way it lands, **write the measured
convergence table into `RESOLVED.md`.** `mom-wirebond-kernel.md` §3 expects ~25–30 over a ~100 mil
arc, so 24 is in the right neighbourhood; do not take that as confirmation.

Clamp with `settings.MaxSegmentsPerWire` (default 200) and report when the clamp bites — silently
coarsening a wire is exactly the kind of thing that produces a confidently wrong number.

### 3.2 Nodes and incidence

Wire *w* with `n_w` segments has `n_w + 1` nodes, numbered along the wire from the **input end**
(`Wire.Points[0]` — `Wire.Reverse()` exists precisely because that convention is explicit and never
inferred). Segment *k* of wire *w* runs from node *k* to node *k+1*, so:

```
A[globalSeg(w,k), globalNode(w,k)]     = +1
A[globalSeg(w,k), globalNode(w,k+1)]   = −1
```

**Store `A` as two `int[]` arrays (`StartNode[]`, `EndNode[]`), not as a matrix.** Every product this
brief needs (`AR`, `ÃG⁻¹Ãᵀ`, `ÃG⁻¹E`) is an O(1)-per-entry index operation against those arrays, and
a dense `N_s × N_n` matrix at the 200-wire size is 288 MB of almost entirely zeros.

### 3.3 Terminals

Terminal `2k` = every wire-in-array-*k*'s node 0. Terminal `2k+1` = every wire-in-array-*k*'s node
`n_w`. **Terminal order and naming must match `WBondTouchstoneExport.PortNames(design,
WBondPortBasis.Terminals)` exactly** — `G1.i, G1.o, G2.i, G2.o, …`. Assert that in a test (§9.9): a
file and a schematic symbol that disagree about which port is which is the failure mode this repo has
already paid for once.

`R` is built by assigning every node a reduced index, terminals taking `0 … T−1` **first** (so
`E = [I_T ; 0]` is a slice, not a matrix product), free nodes taking `T … N_r−1`. Store `R` as an
`int[] ReducedOfNode`.

### 3.4 Images

`Filament.Image()` on every segment filament, exactly as `WireMesh` already does, when
`design.GroundPlane.Enabled`. The plane is at z = 0 by construction — there is no height field, the
model's z origin *is* the ground reference.

**When the ground plane is disabled there is no reference conductor and no terminal basis.** Mirror
`WBondTouchstoneExport.RefuseIfReturnPathUndeclared`'s refusal here rather than producing a network
referenced to nothing (`mom-wirebond-kernel.md` RW13: a port carries an explicit reference conductor,
and the UI does not permit a port without one).

### 3.5 The mesh report — RW2

`Mesh(...)` returns a report **before any fill happens**, carrying at minimum:

- `N_s`, `N_n`, `N_r`, `T`, wires, arrays;
- **predicted peak memory** — see §8, and state the arithmetic, do not hand-wave it;
- the number of wires the `MaxSegmentsPerWire` clamp bit;
- **the s/a warning (RW17)**: any wire pair whose closest approach is below `6 × radius` gets a named
  warning ("wires `A` and `B` approach to 3.8 a; the thin-wire reduced kernel is a few percent
  optimistic below 6 a"). Use `WireGeometry3D.ClosestApproach`, which already exists.

`mom-wirebond-kernel.md` RW2 is explicit that the predicted N is reported **before** the solve. The
repo has already been burned by a ceiling that predicted, passed, and threw twenty minutes later
(`src/Engine/Mom/RESOLVED.md`, the de-embed closeout). Do not repeat it.

---

## 4. `L` — `SegmentInductance`

```
L[p,q] = Grover.Mutual(fil[p], fil[q])  +  Grover.Mutual(fil[p], image[q])      (image term ADDED)
```

Fill the **upper triangle only** and mirror — `L` is symmetric because the kernel is. Use
`Parallel.For` over rows, exactly as `PotentialCoefficients.Fill` does, and default `parallel: true`
for this kernel (the wire-basis default is `false`; that was a different cost regime).

`L[p,p]` uses `Grover.SelfExternal`, plus the self-image term.

**Do not add a quadrature path, a multi-filament path, or a new self-inductance formula.** Grover's
closed forms with the GMD floor are what the analytic model is validated on; using anything else here
would make §9.3's identity gate untestable.

---

## 5. `P` — `NodePotential`

### 5.1 The charge cell is a half-cell, and that is the only subtle piece of geometry in this brief

The canonical Ruehli PEEC pairing puts current on segments and charge on **nodes**, where node *n*'s
charge cell is the union of the **halves** of its incident segments nearest to it. So:

- an interior node's cell = second half of segment *k−1* + first half of segment *k*;
- a wire-end node's cell = the outer half of its single incident segment.

Build a flat `halfFilaments` array once at mesh time (each segment contributes exactly two halves,
each tagged with the node it belongs to), plus its images. **Two halves per segment, `2 N_s` halves
total, and each node owns 1 or 2 of them.**

```
P[m,n] = (1/(4πε₀ · l_m · l_n)) · Σ_{p ∈ cell m} Σ_{q ∈ cell n}
             [ Kernel(p, q)  −  Kernel(p, Image(q)) ]           (image term SUBTRACTED)
```

`l_m` is the cell's total length. This is `PotentialCoefficients.Block` with the wire spans replaced
by cell spans, and it should read like it.

### 5.2 Cost

At most 4 sub-pairs per `(m,n)`, ×2 for images = 8 `Kernel` calls per entry, against 2 `Grover.Mutual`
calls per entry in `L`. The `Kernel` far branch is a reciprocal square root and the near branch is
mostly `ParallelScalarKernel` (a bond array is mostly parallel filaments), so the charge fill should
land at a *fraction* of the inductance fill, not 4× it — `PotentialCoefficients`' own note measures
0.06–0.08× at the wire basis. **Measure it and put the number in `RESOLVED.md`.** If it comes out
above the inductance fill, say so and stop rather than shipping a surprise; WM-3 has the budget to fix
it and this brief does not.

### 5.3 `P` must be SPD

Assert it (`CholeskyFactor.Factor` succeeding *is* the assertion — it throws on a non-SPD matrix).
A `P` that fails Cholesky means a broken image sign or an overlapping cell, and finding that out here
rather than in WM-2's solve is worth a routine test (§9.6).

---

## 6. `D(ω)` — `SegmentInternalZ`

Per segment, from the wire's material at the design's `OperatingTempC`:

```
(rPerMetre, lIntPerMetre) = InternalImpedance.PerMetre(f, radius, sigma)
D[k](ω) = rPerMetre · l_k  +  j·ω·lIntPerMetre · l_k
```

Identical to `ImpedanceReduction.WireInternalImpedance`, with the segment's length in place of the
wire's path length. **Because the scaling is by length and lengths add, `D` summed over a wire's
segments equals the wire's own `D` exactly.** That additivity is half of §9.3's identity gate; the
other half is that partial inductance is additive under subdivision.

Cache `(rPerMetre, lIntPerMetre)` per **distinct (radius, sigma) pair**, not per segment — an array of
identical wires has one entry, and the Bessel evaluation is the only transcendental work per
frequency.

---

## 7. `MomAssembly` — `G`, `K̃`, `W`, `H`

All frequency-independent. Compute once, in this order:

1. **`G = Rᵀ P⁻¹ R`.** Cholesky-factor `P` (real SPD → `CholeskyFactor`), solve `P X = R` for the
   `N_r` right-hand sides (`R`'s columns are 0/1 indicators, so build each RHS by marking membership —
   do not form `R`), then `G = Rᵀ X`, again a scatter-add rather than a GEMM.
   **`P⁻¹` is never formed.** Cost `O(N_n³/3 + N_n² N_r)`.
2. **Cholesky-factor `G`** (SPD: it is a congruence of an SPD matrix by a full-column-rank map).
3. **`Y = G⁻¹ Ãᵀ`** (`N_r × N_s`) by `N_s` triangular solves. `Ãᵀ`'s columns have at most 2 nonzeros
   (`+1` at the reduced index of the start node, `−1` at the end node — **and they cancel to a zero
   column if shorting merged both ends of a segment into one terminal**, which cannot happen for a
   real wire but must not crash).
4. **`K̃ = Ã Y`** (`N_s × N_s`, real symmetric — symmetrise explicitly).
5. **`W = first T columns of Yᵀ`**, i.e. `Wᵀ = Eᵀ G⁻¹ Ãᵀ = ` the first `T` **rows** of `Y`. Free.
6. **`H = Eᵀ G⁻¹ E`** = the leading `T × T` block of `G⁻¹`, from `T` solves. Free.

**`P` may be released after step 1** — nothing downstream reads it. Say so in the code, because at the
200-wire size that is 288 MB.

Step 4 is `O(N_s² N_r)` and is the largest single one-time cost in the kernel — roughly two dense
factorisations' worth. It is paid once per design, against a 201-point sweep. **Measure it and record
it**; WM-3 decides whether it needs to be cheaper.

---

## 8. Memory, stated as arithmetic

| | bytes | 8-wire, 24 seg (`N_s` = 192) | 40-wire (`N_s` = 960) | 200-wire (`N_s` = 4,800) |
|---|---|---|---|---|
| `L` | 8·N_s² | 0.3 MB | 7.4 MB | 184 MB |
| `P` (transient) | 8·N_n² | 0.3 MB | 8.0 MB | 192 MB |
| `K̃` | 8·N_s² | 0.3 MB | 7.4 MB | 184 MB |
| **peak here** | | ~1 MB | ~23 MB | **~560 MB** |
| WM-2's `M̃` (complex) | 16·N_s² | 0.6 MB | 14.7 MB | 369 MB |

Put the real numbers in `RESOLVED.md`, measured with `GC.GetTotalAllocatedBytes` or working set, not
predicted from this table. **The mesh report's predicted peak must include WM-2's `M̃`**, because a
report that says "560 MB" and then WM-2 allocates another 369 MB per solving thread is a report that
lied.

**The ceiling and its refusal.** Declare `WireMomUnknownCeiling` in `WireMomSettings` (start at
**8,000 segments**, ~1 GB peak) and refuse above it **at mesh time**, with a message that names a
**binding** remedy. This repo has a memory on exactly this failure: a refusal that named knobs which
did not change the outcome (`em-refusal-must-name-a-binding-remedy`). The binding knobs here are real
and there are three, so name them with the numbers:

> *"This design meshes to 11,400 segments (≈2.1 GB) — above the 8,000-segment ceiling. Lowering
> `Segments per wire` from 24 to 16 gives 7,600. Solving one array at a time gives ≤1,900. The wire
> count itself is the other lever: 200 wires cannot be solved at 24 segments each on this build."*

---

## 9. The oracles — all of these are cheap

Every test below runs at `N_s ≤ 250` and completes in well under a second. **None of them is tagged
`Benchmark`.** Use `tests/WBond.Tests/TestDesigns.cs`'s existing builders
(`SingleHorizontalWire`, `ParallelArray`, `BallBond`) rather than writing new fixtures.

### 9.1 Straight-wire self-inductance (Rosa/Grover)
A single straight horizontal wire, ground plane **off**, uniform current forced (sum `L` over all
segment pairs). Must equal `Grover.SelfExternal` on the whole wire as one filament, **to 1e-12
relative**. This is the additivity property, tested at its simplest.

### 9.2 Subdivision invariance — the sharpest gate in this brief
Mesh one design at `TargetSegmentsPerWire` = 6, 12 and 24. For each, sum `L` down to the wire basis:
`L_wire[i,j] = Σ_{p∈i} Σ_{q∈j} L[p,q]`. **All three must agree to 1e-12 relative**, because a double
line integral does not care how its domain is partitioned. If they do not, your mesher moved a vertex
or your image is rebuilt inconsistently.

### 9.3 The identity gate against the analytic model — build this one first
For a design meshed at any subdivision:

```
Σ_{p ∈ wire i, q ∈ wire j} L_seg[p,q]   ==   InductanceMatrix.Block(wireMesh, i, j)
```

to **1e-12 relative**, on a 4-wire two-array design with images on, ball-bond profiles (so the
skew-filament path is exercised, not just the parallel one).

The charge dual, with `farThresholdFactor = double.PositiveInfinity` **on both sides** so the near/far
split cannot make them differ:

```
Bᵀ P_node B   ==   PotentialCoefficients matrix ,      B[m, i] = l_m / l_i  for cell m on wire i
```

to **1e-10 relative**.

**These two gates validate the mesher, the incidence, the images, both sign rules and both fills
against code that is already validated, in a test that takes milliseconds.** Write them first; if
either fails, nothing else in this brief is worth debugging yet.

### 9.4 The image signs, tested by breaking them
Two tests that flip one sign each (`L`'s image term to minus, `P`'s to plus) and assert §9.3 **fails**.
A sign error here is a finite, plausible number, not a NaN — the independent tell is monotonicity:
raising a wire lowers its capacitance and raises its inductance, and each sign error inverts one of
those. `PotentialCoefficients`' gate C2 is the pattern to copy.

### 9.5 Wire over a ground plane, closed form
A long straight horizontal wire at height *h*, radius *a*: `L/ℓ = (μ₀/2π)·acosh(h/a)`. Sum the
segment `L` with images to the wire basis, divide by length. **Within 2 %** at `h/a = 30` with the
wire ≥ 40× longer than *h* (the closed form is per-unit-length and ignores ends). Existing coverage in
`ImageAndSelfInductanceTests` is the model for this — check whether it already gives you the oracle
before writing a new one.

### 9.6 `P` is SPD
`CholeskyFactor.Factor(P, N_n)` succeeds on a ball-bond design with images on and with images off.

### 9.7 Segment-count convergence — one wire, four counts, and it must be cheap
A single ball-bond wire over ground, `TargetSegmentsPerWire` ∈ {6, 12, 24, 48} (`N_s ≤ 48`). Assert
the **charge** quantity `Σ_{m,n} (P⁻¹)_{mn}` (the wire's total capacitance to the plane) converges
monotonically and that the 24→48 change is below the 12→24 change. **Record the four values in
`RESOLVED.md` as the table that justifies the default of 24.** `L` does not need a convergence test —
§9.2 proves it is exactly invariant.

### 9.8 `G`, `K̃`, `H` structure
- `G` symmetric, Cholesky succeeds.
- `K̃` symmetric to 1e-12; `K̃` positive **semi**definite (its null space is `null(Ãᵀ)`, non-trivial
  exactly when an array has ≥2 wires — assert the nullity equals the loop count, which is a direct
  check on §2.6 item 2).
- `H` symmetric, `T × T`, positive definite.

### 9.9 Port naming parity
`WireMomMesh.TerminalNames(design)` equals
`WBondTouchstoneExport.PortNames(design, WBondPortBasis.Terminals)`, element for element.
`tests/WBond.Tests` already references `CircuitRF.Ui`? **It does not** — it references WBond, Engine,
Core and RfCore. So assert against the *documented* order `G1.i, G1.o, …` here, and put the
cross-assembly parity assertion in WM-2, where `Ui.Tests` is already in the gate.

### 9.10 The refusals
- Ground plane disabled → mesh refuses, message names the missing reference conductor.
- A design meshing above `WireMomUnknownCeiling` → refuses **at mesh time**, message contains all
  three binding remedies of §8 with real numbers substituted.
- A wire pair below `s/a = 6` → **warns**, does not refuse, and the warning names both wires and the
  actual ratio.

---

## 10. What is explicitly NOT in this brief

- Any solve, any factorisation of `M̃`, any S-parameter, any `DataSet`, any Touchstone. → **WM-2**
- The comparison study against the analytic model with capacitance on. → **WM-2**
- Any UI, any dialog, any `Cli` verb. → **WM-2**, which adds a Touchstone export option and one
  **Design → Compare Distributed Model…** dialog, and nothing more.
- A complex-symmetric `LDLᵀ`, frequency parallelism, ACA, or any measured optimisation. → **WM-3**
- The retarded kernel (W2), meshed surfaces (T2), overmold, stepped ground. → not scheduled.
- `IEmKernel`, `EmAnalysisKind`, registry integration. → deliberately not now (§0.3 item 6).

---

## 11. Report back

In `src/WBond/Mom/RESOLVED.md`, and in your closing message:

1. **The measured fill costs** — `L`, `P`, and step 4 of §7 — at `N_s` = 192, 960 and (Benchmark tier)
   4,800. Measured alone. Say whether §0.3 item 1 held: is the segment `L` fill the same cost as the
   wire-basis fill over the same filaments?
2. **The §9.7 convergence table**, and whether 24 survived as the default.
3. **The measured peak memory** at each size against §8's predicted arithmetic.
4. **The `P` fill's ratio to the `L` fill** (§5.2), and whether the near/far threshold of 3.5 —
   measured for *wire*-length cells — is still right for *half-segment* cells. If you re-measure it,
   sweep it the way `PotentialCoefficients`' own comment does and record the table. **If you do not
   re-measure it, say that you did not**, because it is a live question, not a settled one.
5. **Anything in §2 that turned out wrong.** The algebra was checked but not implemented; if a sign,
   a transpose or a size is off, the correction belongs in `RESOLVED.md` in bold, and this brief's
   file should be left alone — briefs are the historical record of what was asked, not of what was
   true.
6. **What you added to the Benchmark tier** and what each measured.
