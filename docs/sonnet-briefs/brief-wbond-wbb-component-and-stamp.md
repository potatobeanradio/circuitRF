# Sonnet Brief — wBond WB-B: the component and its stamp

**Design:** `docs/design/wbond.md`, approved 2026-08-07. This brief implements its **phase WB-B** — the
schematic component: the dynamic symbol, the MNA stamp, the reference-conductor refusal, the
expression-bound parameters, and the coupling audit.

**WB-A is complete and is the foundation.** `src/WBond` holds the Grover kernels, the ground-plane
image, the array reduction `L_arr = (AᵀL⁻¹A)⁻¹`, per-wire current sharing, the internal-impedance
table, the incremental fill, `.wBond` I/O and the CSV importer — 110 tests, all green. **This brief
adds no physics.** It wires existing physics into the engine.

**No editor is built here.** No canvas, no profile view, no drag, no panel — that is WB-C. The only
UI in this brief is *generated symbol geometry*, which is data, not a control.

**Read, in this order, before planning anything:**

1. **`docs/design/wbond.md` §5 (all of it), §3.4, §3.6, §7.** §5 is the specification this brief
   implements. §3.4 is the reduction WB-A already built — **read it to understand what you are
   wrapping, not to re-derive it.** §7 is the coupling audit and why it is load-bearing.
2. **`docs/sonnet-briefs/brief-wbond-wba-headless-model-and-physics.md` §0.3 and §1.** The six
   standing facts and eleven decisions. D2 (direction is data), D5 (the reduction), D6 (maintain the
   factor) and D7 (gold, 85 °C) all constrain this brief.
3. **Then the code, not summaries of it:** `src/Core/Devices/SnpModel.cs` (**the stamp template —
   read it first**), `src/Core/Devices/InductorModel.cs` (the series-branch shape), `src/Core/IMnaContext.cs`,
   `src/Core/Devices/ComponentModelFactory.cs` (`TryCreate`'s parameterised switch, and `Register`),
   `src/WBond/ArrayReduction.cs` and `src/WBond/CholeskyFactor.cs` (what exists), and — for the symbol —
   the `GeneratedCellStore` content-version machinery in `src/Ui`.

---

## Gate command

```
dotnet test tests/WBond.Tests  --no-build
dotnet test tests/Core.Tests   --no-build      # you are adding a device model here
dotnet test tests/Engine.Tests --no-build      # a new linear stamp — this is the risk gate
dotnet test tests/Ui.Tests     --no-build      # the symbol generator lives here
dotnet test tests/Firewall.Tests --no-build
```

Run as separate commands (`MSB1008`).

**`Engine.Tests` is ~3 min 24 s and is not optional here.** A new `ComponentModel` participates in
every linear and HB solve path. **No hero golden may move.** If one shifts by an ulp, say so and say
why — do not adjust a tolerance.

**`Ui.Tests` is ~27 s / 5,075 tests** and is in the gate because symbol generation lives in `src/Ui`
(§0.3 item 5).

---

## 0. Read this before planning anything

### 0.1 What is being built

A `ComponentModel` that stamps a wBond design's array-basis impedance into the MNA matrix, a dynamic
symbol whose pins follow the design's arrays, the refusal that makes the return path explicit, and a
solve-time audit that reports coupling the model does not capture.

### 0.2 What WB-A already gives you

| need | component | note |
|---|---|---|
| wire-basis **L** from geometry | `InductanceMatrix.Fill(WireMesh)` | 0.144 s at 600 wires, parallel |
| `L_arr = (AᵀL⁻¹A)⁻¹` | `ArrayReduction.Reduce(l, factor, map, count, names)` | pass the factor — see WB-A's own trap |
| per-wire current sharing | `ArrayReduction.CurrentShares(J)` | |
| R(f) and L_int(f) | `InternalImpedance.PerMetre(f, radius, sigma)` | exact, one q-table |
| σ(T) at 85 °C | `WireMaterial.SigmaAt(tempC)` | |
| real SPD factor + rank-k update | `CholeskyFactor` | **does not carry over to complex — §0.3 item 2** |
| `.wBond` read | `WBondIo.ReadFile` | |
| CSV table read | `WireTableCsv.ReadFile` | how a 600-wire design arrives |
| profile → polyline | `LoopProfile.CreateWire` | feet exact, height above the chord |

### 0.3 Six things that are true before you start

1. **The project reference has already been inverted, and it was measured, not assumed.**
   `src/WBond` turned out to use **nothing** from Core, Engine or RfCore, so its three references were
   removed and **`src/Core` now references `src/WBond`**. That is what lets a `ComponentModel` reach
   the physics without a cycle. **Do not add a reference from `src/WBond` back to Core** — if
   something there ever needs Core, the type moves, not the arrow. The `.csproj` says so in a comment.

2. **`Z_arr(ω) = (AᵀZ(ω)⁻¹A)⁻¹` cannot use `CholeskyFactor`, and this is the sharpest technical trap
   in the brief.** `Z = R + jω(L + L_int)` is **complex symmetric**, *not* Hermitian: `Zᵀ = Z` but
   `Z* ≠ Z`. Cholesky requires Hermitian positive-definite and simply does not apply — and a complex
   LDLᵀ without pivoting can break down on a matrix that is perfectly well conditioned, unlike the
   real SPD case. **Use LU with partial pivoting** (or Bunch–Kaufman if you want the symmetry back).
   Reusing `CholeskyFactor` by "just making it complex" is the wrong answer and will pass a
   well-conditioned test before failing on a real design.

3. **The frequency dependence is DIAGONAL, and that is worth knowing before you design the sweep.**
   `Z(ω) = jωL + D(ω)` where `L` is the frequency-independent full matrix from WB-A and `D(ω)` is
   **diagonal** — per-wire `R(f)` and `jωL_int(f)`. Two consequences:
   - `L` is filled **once** for a whole sweep. Only `D` moves.
   - **When every wire shares a radius and a metal — the common case for an array —
     `D(ω) = d(ω)·I` is a scalar multiple of the identity.** Then `L + D/(jω)` has the same
     eigenvectors as `L`, so one symmetric eigendecomposition of `L` (once, O(N³)) makes every
     frequency an O(N²) solve. That is a large win and it is **named here, not required** — measure
     first (M1), and only build it if M1 says the sweep is too slow.

4. **The `REF` pin declares and refuses; it does not stamp.** `L_arr` is a *loop* inductance whose
   return is the image plane at z = 0, so the circuit element is a **series branch from `in_k` to
   `out_k`** and the return is implicit in the schematic's own ground. A reader will expect a
   2M+1-terminal stamp and there isn't one. `REF` exists so the assumption is *stated* and so the
   model can refuse the case where it is false (R-wbb-4). Say this in the code comment, because it
   looks like an omission.

5. **Symbol generation lives in `src/Ui`** (`GeneratedCellStore`), on the far side of the firewall.
   So WB-B spans two projects: the stamp in `src/Core/Devices`, the symbol in `src/Ui`. **The
   generator must carry a content version** — `project-brief-L5-followups` records the MTee failure
   where a generator fix left stale on-disk cells in place. For wBond the same bug reorders array pins
   and wires correctly-named pins to the wrong nets. Silent, and electrically wrong.

6. **`ComponentModelFactory.Register` is parameterless-only.** wBond needs parameters (a `.wBond`
   path or an embedded design, plus overrides), so it belongs in `TryCreate`'s parameterised switch
   alongside `ExtDevice` and `VerilogA` — not in the `Register` dictionary.

---

## 1. Decisions taken — do not relitigate these

- **D1 — The stamp is the exact complex reduction** `Z_arr(ω) = (AᵀZ(ω)⁻¹A)⁻¹`, owner decision
  2026-08-07 (wbond.md §5.3, WB19a). **Not** R and L reduced independently.
- **D2 — M coupled series branches**, one per array, `in_k → out_k`, via branch-current expansion —
  the `SnpModel` mechanism with a series rather than shunt topology.
- **D3 — Two pins per array**, input left, output right, plus one `REF`. Pin order follows array
  order.
- **D4 — `REF` must be connected**, and the model refuses to stamp when the ground plane is disabled
  and no array is nominated as the return (WB20).
- **D5 — Parameters are ordinary circuitRF expressions**, so loop height is sweepable through
  `parametric_sweep` (WB21).
- **D6 — The coupling audit reports, never refuses** (WB30), and names the manual remedy, because
  `CouplingDomain` is v2 and the audit is the whole safety mechanism in v1 (WB30a).
- **D7 — `Z_arr/jω → L_arr` as R → 0 is a required cross-oracle** (WB19b), not a nice-to-have: it is
  the only check that ties the simulator's exact path to the editor's fast one.

---

## 2. M1 — measure the swept cost before building the sweep

**Do this first and report before building on it.**

`wbond.md` §5.3 asserts one complex N × N factorisation per frequency point is "fine for a swept
simulation". **That assertion is unmeasured.** A complex LU at N = 600 is roughly 4× the flops of a
real one and 2× a Cholesky's, so the honest estimate is 100–200 ms per point — which is 20–40 s for a
201-point sweep and 100–200 s for a 1001-point one. That may be acceptable; it may not.

**Measure, at N = 600, M = 12, taken alone:**

1. ms for one `Z_arr(ω)` evaluation: complex fill of `Z` + factorisation + M solves + M × M inverse.
2. ms for a 201-point sweep, and the extrapolated 1001-point figure.
3. ms for the **uniform-array** eigendecomposition route of §0.3 item 3, if (2) looks bad — one
   symmetric eigendecomposition of `L` plus O(N²) per point.

**Report those numbers before continuing.** If (2) exceeds ~30 s for 201 points, say so: the
uniform-array route stops being an optimisation and becomes the shipping path for the common case,
and the owner should know that before the sweep UX is designed in WB-C.

---

## 3. Requirements

### R-wbb-1 — the model
`WBondModel : ComponentModel`, `ModelKind.Linear`, `PortCount = 2M + 1`. Terminal names are
`<array>.i`, `<array>.o` in array order, then `REF`. Registered in `ComponentModelFactory.TryCreate`'s
parameterised switch (§0.3 item 6).

### R-wbb-2 — the stamp (D2)
One branch per array. For array *k*:

```
br[k] = mna.AddBranch()
mna.AddBranchCurrent(br[k], Nodes[2k], Nodes[2k+1])        // current in → out
mna.AddConstraint(br[k], Nodes[2k],   +1)
mna.AddConstraint(br[k], Nodes[2k+1], -1)
for j:  mna.AddBranchConstraint(br[k], br[j], -Z_arr[k,j])
```

Expose `ArrayBranchIndices` the way `SnpModel` exposes `PortBranchIndices`, so measurements can reach
the per-array currents.

### R-wbb-3 — `Z_arr(ω)` (D1, §0.3 items 2 and 3)
`Z(ω) = jω·L + D(ω)`, `D` diagonal from `InternalImpedance.PerMetre` × each wire's path length.
Reduce with **LU + partial pivoting**, not Cholesky. **`L` is filled once per structural change and
reused across every frequency** — refilling per frequency would be ~0.15 s × 1001 points and is the
single easiest way to make this unusably slow.

### R-wbb-4 — the reference conductor (D4 / WB20)
`REF` must be connected. **Refuse to stamp**, with a specific message, when the ground plane is
disabled and no array is nominated as the return. Legitimate configurations:
- ground plane enabled → `REF` ties to the plane's net; the image terms *are* the return path;
- ground plane disabled → one or more arrays nominated as return (downbonds, RW14); the reduction
  runs on the rest with those as reference.

**A refusal must name the design and the fix**, per the house pattern — not "invalid configuration".

### R-wbb-5 — the dynamic symbol (D3, §0.3 item 5)
Two pins per array, input left, output right, `REF` bottom. Regenerated whenever the array list
changes, content-addressed through `GeneratedCellStore` **with a generator content version**. Body
shows name, array count, wire count, total wire length.

### R-wbb-6 — parameters (D5)
Expose through the ordinary parameter path: ground-plane enable, operating temperature, global
diameter and material overrides, fidelity mode, and **each bound `LoopProfile`'s loop height**.
Changing loop height must re-run the profile generator, refill `L`, and re-solve — so a
`parametric_sweep` over `X1.G1.LoopHeight` works end to end through `Cli`.

### R-wbb-7 — the coupling audit (D6 / WB30)
On every solve, report — never refuse — when two wBond instances have wires within a threshold scaled
to their heights above ground. **The message names the remedy** ("merge the wires into a single
wBond"), because with `CouplingDomain` deferred to v2 that is the only fix available (WB30a).

### R-wbb-8 — measurements
Per-array current and voltage reachable as named cubes, following the existing branch-current cube
convention (`instancePath:terminalName`).

---

## 4. The oracle ladder

| tier | what | pass |
|---|---|---|
| **0** | **`Z_arr(ω)/jω → L_arr` as R → 0** (D7 / WB19b) — set σ → ∞ or evaluate at a frequency where R/ωL is negligible, and compare against WB-A's real reduction | ≤ 1e-9 rel |
| **1** | One array of one wire, ground plane on: the stamped two-port Z₁₁ equals `jω·L_arr[0,0] + R` from `InternalImpedance` directly | ≤ 1e-12 rel |
| **2** | **Two uncoupled wBond arrays far apart** stamp as two independent series impedances — the off-diagonal `Z_arr[0,1]` must be small, and the network's S-parameters must match two separate one-array wBonds cascaded | ≤ 1 % |
| **3** | **Reciprocity and passivity** of the stamped N-port across a sweep: `S = Sᵀ`, and `‖S‖ ≤ 1` for a lossy design | 1e-12 / no eigenvalue > 1 |
| **4** | **Losslessness**: with σ → ∞ the stamped network is lossless (`SᴴS = I`) | ≤ 1e-9 |
| **5** | The exact complex reduction vs. **independent R and L reduction**, reported as a *difference*, at a low frequency and in a lossy aluminium array — this quantifies what D1 bought | reported, not gated |
| **6** | Symbol pin count and order follow the array list; **reordering arrays regenerates the symbol** and does not reuse a stale cached one (§0.3 item 5) | exact |
| **7** | A wBond with the ground plane disabled and no nominated return **refuses**, naming the design | message asserted |
| **8** | `parametric_sweep` over a loop height runs end to end through `Cli hb`/`sparam` and the resulting L_arr moves monotonically with loop height | monotone |
| **9** | The coupling audit fires on a constructed two-wBond adjacency and names the manual remedy | message asserted |
| **10** | Cost: §2's three numbers, plus fill-once-vs-refill confirmation | reported, measured alone |

**Tier 0 is the one that matters most.** It is the only check tying the simulator's exact path to the
editor's fast path, and it is free.

---

## 5. What must NOT be built here

- **The editor** — canvas, profile view, alt-drag, transforms, panel, snapping, units UI. All WB-C.
- **`CouplingDomain`** — v2 (WB29). v1 ships the audit only.
- **The assembly DRC or `.wasm`** — WB-D.
- **The standalone entry point** — WB-E.
- **Any MoM or kernel W code** — WB-F, downstream of `mom-wirebond-kernel.md` LW1.
- **Any new physics in `src/WBond`.** If you find yourself deriving something, stop — WB-A's oracles
  are green and this brief is plumbing.
- **A reference from `src/WBond` back to Core** (§0.3 item 1).
- **Reusing `CholeskyFactor` for the complex reduction** (§0.3 item 2).
- **Refilling `L` per frequency** (R-wbb-3).
- **Any change to an existing device model or hero golden.**

---

## 6. Milestones

| M | What | Gate |
|---|---|---|
| **M1** | `Z_arr(ω)` — complex assembly, LU, reduction — plus §2's measurements | **Tiers 0, 1**; the numbers reported; **a legitimate stopping point** |
| **M2** | `WBondModel` + factory registration + the stamp | **Tiers 2, 3, 4**; `Engine.Tests` green, no golden moved |
| **M3** | `REF` pin and the return-path refusal | **Tier 7** |
| **M4** | Dynamic symbol generation with content versioning | **Tier 6**; `Ui.Tests` green |
| **M5** | Parameters, loop-height sweep through `Cli` | **Tier 8** |
| **M6** | The coupling audit | **Tier 9** |

**Two fault lines.**

- **After M1.** If the swept cost is worse than ~30 s for 201 points, stop and report — that changes
  what WB-C can promise about sweeps and **the owner decides**, not you.
- **After M2.** If any hero golden moves, stop. A new linear stamp must be additive; a moved golden
  means it is not.

**M1 → M3 is a shippable increment** (a wBond that simulates from a CSV-imported design, refusing the
ill-posed case). M4–M6 make it pleasant.

---

## 7. File map (indicative)

```
src/WBond/
  ComplexLu.cs                  NEW. LU + partial pivoting for the complex symmetric Z (§0.3 item 2)
  ImpedanceReduction.cs         NEW. Z(w) = jwL + D(w), reduce to Z_arr(w). R-wbb-3
  CouplingAudit.cs              NEW. R-wbb-7 — geometry only, no engine types

src/Core/Devices/
  WBondModel.cs                 NEW. R-wbb-1/2/4/8 — the stamp, the REF refusal
  ComponentModelFactory.cs      register "wBond" in the parameterised switch (§0.3 item 6)

src/Ui/...                      R-wbb-5 — the symbol generator + its content version
tests/WBond.Tests/              tiers 0, 1, 5, 10
tests/Core.Tests/               tiers 2, 3, 4, 7
tests/Ui.Tests/                 tier 6
```

---

## 8. What to report back on, whatever else happens

1. **§2's three numbers** — one-point, 201-point, and the uniform-array route if measured. **This is
   the gating result of the brief.**
2. **Tier 0's measured agreement** between `Z_arr/jω` and `L_arr`.
3. **Tier 5's difference** — how much the exact complex reduction actually differs from reducing R
   and L independently, at low frequency and in a lossy aluminium array. Nobody knows this number
   yet, and it is what justifies D1.
4. **Whether any hero golden moved**, and by how much.
5. **What you added to the `Category=Benchmark` tier**, in minutes.
6. **Anything in `wbond.md` §5 that turned out to be wrong.** WB-A found four such things; treat a
   contradiction as a finding to report, not an obstacle to work around.
