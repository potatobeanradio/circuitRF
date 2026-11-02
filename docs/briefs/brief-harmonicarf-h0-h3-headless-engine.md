# Sonnet Brief — harmonicaRF H0–H3: the headless engine

**Design:** `docs/design/harmonicarf.md`, approved 2026-08-06. That note is the specification; this
brief implements its **phases H0, H1, H2 and H3** — everything below the UI. **No pixel is drawn in
this brief.** H4–H8 (the document shell, the four panels, the frame scheduler, the inverse solve, Edit
Display, the colour theme, the standalone binary) each get their own brief; §10 names them so nothing
is lost.

**Why this is the cut.** The whole product rests on two claims — that the physics is right, and that a
termination change is cheap. Both are provable headless, and if either is wrong every UI decision
downstream is built on sand. `src/Harmonica` is framework-free by design (`harmonicarf.md` §3.1), so
this tranche is fully testable with no Avalonia in sight.

**Read, in this order, before planning anything:**

1. **`docs/design/harmonicarf.md` §2, §4.5, §6.1–§6.4.** §2 is the measured budget every design
   decision here is sized against. **§4.5.1–§4.5.4 is the physics that this brief exists to get right**
   — read all four subsections, not a summary. §6.2 is the optimization; §6.3–§6.4 the contour path.
2. **`docs/design/harmonic-balance.md` §3, §7, §8, §11.1.** §3 is the interface extraction you are
   about to restructure; §7 is the Jacobian you are about to reuse for something new; §11.1 is
   warm-start, already built.
3. **`docs/design/loadpull.md` §1, §3, §4.** The `Tuner`, the Γ-grid × Pin pattern, and the live
   measurements. harmonicaRF reuses all of it and must not fork it.
4. **The `Iin` bug and its fix** — memory `bugfix-loadpull-zin-source-current`, and
   `src/Engine/Loadpull/CLAUDE.md`. **This is the same error class this brief is guarding against at a
   different reference plane** (§0.3 item 4). Read it before you write a single impedance.
5. Then the code, not summaries of it: `HbEngine.RunSinglePoint` and `SinglePointResult`
   (`src/Engine/HarmonicBalance/HbEngine.cs:806`), `HbNewton.cs` around **line 281** (the per-sample
   `Evaluate` loop M1 changes), `HbLinearExtractor`, `TunerModel` (`SetHarmonicOverride:94`,
   `SetSourceDrive:79`, `FundamentalZ:324`), `LoadpullEngine.RunOneTermination`,
   `ExternalDeviceModel.Evaluate`, `DeviceWorkerInstance.EvaluateBatch:62`,
   `IExternalDeviceProvider.EvaluateBatch:44`, `RfCore/Loadpull/Rbf2D.cs:55` (the constructor —
   note where the values enter), `LoadpullSurface`, `ContourExtractor.Extract:84`.

---

## Gate command

```
dotnet test tests/Harmonica.Tests --no-build      # new in this brief
dotnet test tests/Engine.Tests    --no-build      # M1 touches HbNewton — this is the risk gate
dotnet test tests/Core.Tests      --no-build
dotnet test tests/RfCore.Tests    --no-build
dotnet test tests/Firewall.Tests  --no-build
```

Run as separate commands — this SDK rejects more than one explicit project path per invocation
(`MSB1008`).

**`Engine.Tests` has NO headroom.** It is ~1,000 tests in **~3 min 24 s** measured alone, and the root
`CLAUDE.md` records that plainly rather than smoothing it. **M1 touches `HbNewton` — the hottest code
in the repo — so `Engine.Tests` is not optional here, it is the risk gate.** Do not add a routine test
to `Engine.Tests`; harmonicaRF's tests live in `tests/Harmonica.Tests`.

**Test-cost discipline for the new project.** At the measured **0.94 ms per warm HB solve**, most
correctness tests here are genuinely fast: a full 61-point contour grid is ~500 solves ≈ **0.5 s** and
belongs in the routine tier. Anything that repeats a full grid many times, or that drives an external
worker in a loop, will cross the ~5 s threshold and **must** carry `[Trait("Category","Benchmark")]`.
Measure before you tag; state in the report what you added to the opt-in tier (currently ~40 min).

**One measurement discipline.** `L8d`'s finding, restated because it has bitten this repo twice:
a benchmark sharing a run with others reads more than twice as slow, and L9d's 71.9 s was first
mis-measured at 16.79 s that way. **Take every timing measurement alone, and say in the report that
you did.**

---

## 0. Read this before planning anything

### 0.1 What is being built

A headless engine that holds one nonlinear DUT between two programmable harmonic terminations,
re-solves harmonic balance when a termination changes, and reports what the **current generator** is
doing. It publishes an ordinary `DataSet`. It has no UI and no opinions about one.

### 0.2 The measured budget — these numbers are the design

Taken on an Apple M4 (4P + 6E), .NET 10 Release, single-threaded, Hero-2's SDD GaN HEMT, **before**
the design was written. They are re-takeable; re-take them if you doubt them.

| case | ms / HB solve |
|---|---|
| warm-seeded, K=3 | 0.74 |
| warm-seeded, **K=5** (the shipping order) | **0.94** |
| warm-seeded, K=7 | 1.08 |
| warm-seeded, K=10 | 1.59 |
| cold (nonlinear-DC seed each point) | 2.45 |
| Hero-4, two FETs, K=5 warm | 1.48 |

Integration: **the full Hero-3 loadpull — 20 Γ points × 32 Pin steps = 640 HB solves — runs in
0.55 s** (0.86 ms/solve), tuner mutation and live compression logic included.

**What this brief must preserve:** a 61-point grid with a secant Pin search (~8 solves/point) is
~500 solves ≈ **0.45 s single-threaded**. If your implementation is materially worse than that, stop
and report rather than proceeding — the UI phases have no way to recover it.

### 0.3 Seven things that are true before you start

1. **Native FET models EXIST.** `AngelovFetModel`, `CurticeCubicFetModel`, `CurticeQuadraticFetModel`,
   `MaterkaFetModel`, `StatzFetModel`, all on `FetModelBase` with selectable gate charge
   (`CapModel` 0 = none, 1 = constant Cgs/Cgd, 2 = junction). **The root `CLAUDE.md` says
   `FetModel` "is a plan, not code" and that line is STALE.** Correct it as part of this brief
   (§7 M6). Do not write a sixth FET model.
2. **The i/q split already exists everywhere, including for external devices.**
   `NonlinearResult(Current, Charge, Conductance, Capacitance)`; `ExternalDeviceModel.Evaluate`
   forwards a provider's `Charge` and `Capacitance` unchanged. **You do not need to add a
   conduction-current path** — you need to stop summing the two.
3. **`EvaluateBatch` exists and NOTHING calls it.** `DeviceWorkerInstance.EvaluateBatch` does the
   right thing already (one round trip for a whole vector; its own doc comment records ~100 µs
   unbatched vs ~4 µs batched per point). `IExternalDeviceProvider.EvaluateBatch` has a default
   scalar-loop implementation, so **no provider needs changing**. The gap is entirely between
   `HbNewton` and the model.
4. **This codebase has already shipped the exact bug this brief must avoid.** Loadpull's `Zin`
   computed `V[src] / INl[src]` — dividing by the SDD's *intrinsic gate* current — and reported
   **5000 Ω where the true source-seen value was 192 Ω**, whenever a user wired any passive at the
   gate. The fix was to measure the **true delivered current** (`Iin`), not a device-internal one.
   `harmonicarf.md` §4.5.2 is that same mistake caught at the intrinsic plane before it shipped.
   **Do not reintroduce it.**
5. **The contour path is already built and is framework-free.** `Rbf2D` (scatter → weights),
   `LoadpullSurface` (→ `SurfaceGrid`), `ContourExtractor.Extract` (→ iso-polylines),
   `LoadpullPostProcessor.Enrich` (Pout / DE / PAE / Zin / IRL / AM-PM) all live in `src/RfCore`.
   **Do not write a second contour implementation, and do not re-derive a single FOM that `Enrich`
   already produces.**
6. **`Rbf2D`'s expensive half depends only on node POSITIONS.** Its constructor builds the kernel
   matrix from `(_nodesRe, _nodesIm)`, LDLᵀ-factors it, and only then solves with `_nodeValues` as the
   right-hand side. Values enter last. That is what makes §6.4.1's cache possible, and it is why the
   cache key must include the **NaN mask** — the constructor drops NaN nodes, so which nodes exist
   depends on the values after all.
7. **`LoadpullEngine` walks a uniform Pin ladder** (`PinStart`, `PinStep`, `PinMax`, one overshoot
   step past compression). That is correct for a batch reference run and **too slow for this one**.
   D4 replaces it *inside harmonicaRF only*. **`LoadpullEngine`'s own behaviour and every Hero 3/3B
   golden must not move.**

---

## 1. Decisions taken — do not relitigate these

These were settled with the owner during design review. If implementation shows one of them is wrong,
**stop and report**; do not quietly substitute another.

- **D1 — Intrinsic means CONDUCTION CURRENT ONLY.** The `i` half of `(i, q, dg, dc)`, excluding `jωq`.
  Both the loadline and the glyphs. Consequence, and it is intended: the load glyph separates from its
  marker whenever charge is on, even with no extrinsic network.
- **D2 — The intrinsic SOURCE impedance is NOT a voltage/current ratio.** A ratio at a driven node
  returns the load, never the source; at the intrinsic gate that yields a version of `Zin`. Use the
  `J′` Schur route of §4.5.3. This is the single most important correctness item in the brief.
- **D3 — Extract the linear partition ONCE per harmonic with the termination ports OPEN**, and close
  them algebraically (§6.2). A marker move must cost no MNA solve and no refactorization.
- **D4 — Secant bisection on Pin toward the compression target**, not a uniform ladder. ~5–8 solves
  per Γ point rather than ~30.
- **D5 — Non-compressing Γ points are HOLES.** Thrown out of the grid, never extrapolated into, and
  contours clipped to a support mask.
- **D6 — MXP/MXE are the argmax over the computed grid.** No search, no `PursuitEngine` call.
- **D7 — `src/Harmonica` is framework-free** and joins the `tests/Firewall.Tests` assertion.
- **D8 — Ideal bias; single frequency; single tone; K = 5 default.**
- **D9 — A band with no marker is terminated at 1e-6 Ω** (ohms, near-short), bands 2…K.
- **D10 — `.charm` stores setup only**, re-solved on open. Built-in/SDD models embedded whole
  (equation text included); Verilog-A/PDK models referenced.
- **D11 — Batching (M1) is a PREREQUISITE, not an optimization.** Without it an external model costs
  ~10–30 ms per solve and the product's central claim fails for exactly the devices a professional
  user cares about.

---

## 2. What already exists, and what genuinely does not

**Exists — use it, do not reimplement:**

| need | component |
|---|---|
| warm-startable single-point HB | `HbEngine.RunSinglePoint(p, warmStart, settingsOverride)` → `SinglePointResult` |
| interface extraction | `HbLinearExtractor` (`InterfaceCount`, `InterfaceNodes`, `ExtractDC`) |
| per-harmonic programmable termination | `TunerModel.SetHarmonicOverride(k, Z)` / `SetSourceDrive(f0, Pavl)` / `FundamentalZ(f0)` |
| true delivered input current | `LoadpullEngine.ComputeSourceInputCurrent`, `TunerModel.SourceZPortBranchIndex`, `ChokeBranchIndex`, `SinglePointResult.BackSolver` |
| scatter → surface → iso-lines | `Rbf2D`, `LoadpullSurface`, `ContourExtractor.Extract` / `LevelsBetween` / `LevelsByStep` |
| derived FOMs | `LoadpullPostProcessor.Enrich` |
| Γ grid I/O | `GamReader`, `GamWriter` |
| batched external evaluation | `DeviceWorkerInstance.EvaluateBatch`, `IExternalDeviceProvider.EvaluateBatch` |
| an external device to test against, with no vendor kit | `tools/fake-osdi-model/fake_osdi.osdi` + `tools/osdi-worker/` |

**Does NOT exist — this brief builds it:**

- Any batched path between `HbNewton` and a provider.
- The open-port interface extraction and its Schur re-termination (D3).
- A secant compression search (D4).
- Reuse of an `Rbf2D` factorization across value vectors.
- A contour support mask (D5).
- `Z_S,intr` in any form (D2).
- Anything named `Harmonica`.

---

## 3. M1 — the measurement that decides the external-model story

**Do this first and report before building on it.** D11 asserts batching pays; M1 proves it or
disproves it.

`HbNewton.cs:281` calls `ec.Evaluate(new PortVoltages(portV))` once per time sample, inside the Newton
loop. At K=5 the grid is `nextpow2(4K)` = 32 samples; with 3–4 warm iterations that is ~100–130 calls
per HB solve. For a built-in model those are direct calls and cost nothing. For
`ExternalDeviceModel` each one is an IPC round trip.

**Measure, using `tools/fake-osdi-model` so no vendor kit is needed** (if that model is not adequate —
too trivial, wrong port count, no charge — say so and hand-build a fixture; do not silently substitute
a built-in and call it an external measurement):

1. ms per HB solve, external device, **unbatched** (today).
2. ms per HB solve, external device, **batched** (after the change).
3. ms per HB solve, built-in device, **before and after** — this must not move.

**Then report the three numbers before continuing.** If (2) does not land within ~3× of the built-in
figure, the frame scheduler's degradation path (H5) becomes the primary external-model story rather
than the fallback, and the owner needs to know that now rather than at H5.

**The change itself.** Gather the whole time grid of port-voltage vectors, call the provider once,
scatter the results back. Built-in models keep the scalar path — `IExternalDeviceProvider`'s default
`EvaluateBatch` already loops, so **no model and no provider changes**. Shape the seam so
`NonlinearDcEngine` and `HbNewton2D` can adopt it later without a second design.

> **This is the highest-risk edit in the brief.** `HbNewton` is the hottest code in the repo and every
> hero golden runs through it. **Hero 2, 3, 3B, 4 and 5 goldens must be bit-identical afterwards.** If
> any number moves by an ulp, say so and say why — do not adjust a tolerance.

---

## 4. Requirements

### R-hrf-1 — the conduction/displacement split
`Ids_intr(t)` and `Vds_intr(t)` are the **conduction** current and the terminal voltage. The `q`
contribution is excluded from the current, never from the voltage. With `CapModel = 0` (or an SDD with
no charge term) and no extrinsic network, the **load** glyph must equal its marker exactly.

### R-hrf-2 — the load-side intrinsic impedance
`Z_L,intr(k) = − V_d,k / I_d,k^cond`. Legitimate because the conduction current is an ideal current
source at that node (§4.5.1). This one **is** a ratio.

### R-hrf-3 — the source-side intrinsic impedance (D2)
Build `J′` from the converged HB Jacobian `J = Y_NN + ∂I_nl/∂V + ω·∂Q_nl/∂V` by replacing **only** the
gate-port self block with its linear part:

```
J′_gg = (Y_NN)_gg          — the device's own ∂I_g^dev/∂V_g removed  (excludes Cgs / gate diode)
J′_gr = J_gr ,  J′_rg = J_rg ,  J′_rr = J_rr    — kept (retains gm and the common-source path)

Z_S,intr = ( J′_gg − J′_gr · J′_rr⁻¹ · J′_rg )⁻¹ = ( J′⁻¹ )_gg
```

Publish the **full conversion matrix** as `Zs_conv`; the glyph plots its diagonal. `J` is already
assembled and factored at convergence and is 24 × 24 for one FET at K=5 — this is microseconds, once
per displayed operating point, **not** per grid point.

**Gated by Tier 0 and Tier 1 of §5. Do not ship it on inspection.**

### R-hrf-4 — `Zin` is a separate, extrinsic quantity
`Zin = V_in,1 / I_in,1` at the **extrinsic** plane, where `I_in,1` is the **true delivered** current —
reuse `ComputeSourceInputCurrent` / the `Iin` cube, per §0.3 item 4. Report it **at the MXP load and
at the MXE load separately**; the device is not unilateral and one number would be a lie.

### R-hrf-5 — the solve context
One `HarmonicaContext` owns one elaborated netlist and one `HbEngine`. Rebuilt only on **structural**
change (DUT, embedding stack, K, frequency). A **value** change (termination, drive, bias) mutates
models in place. **Going through a global-variable override forces a re-elaboration and is forbidden**
— it is ~1000× the cost of the thing being changed.

### R-hrf-6 — open-port extraction and algebraic re-termination (D3)
Extract once per harmonic as an `(N_nl + N_term)`-port with the termination ports **open**:

```
Y_NN(Z) = Y_aa − Y_ab (Y_bb + Y_t)⁻¹ Y_ba          Y_t = diag(1/Z[k])
```

`Y_bb + Y_t` is 2 × 2 for one source and one load termination. Same treatment for the source
excitation `Y_s·V_s`. Invalidated only by a structural change. **Gated by Tier 2.**

### R-hrf-7 — the Pin search (D4)
Tickle → secant bisection on Pin toward `Gmax − G(Pin) = x`. Warm-start every solve from the previous
Pin step and every Γ point from its VSWR-nearest converged neighbour (the existing rule). Hard stops:
`PinMax`, or HB non-convergence. Record which.

### R-hrf-8 — holes and the support mask (D5)
A Γ point that does not reach the compression target before `PinMax` is **excluded from the grid** and
flagged. Contours are clipped to a support mask = convex hull of converged points **minus a disc
around each excluded point**. **Outside the mask nothing is drawn.** An RBF will happily invent an
efficiency ridge inside a hole; that is exactly the artifact this tool must never produce.
**Gated by Tier 7.**

### R-hrf-9 — the RBF factorization cache
Cache the LDLᵀ factor keyed on **(node positions, NaN mask)**; re-solve per value vector. Power and
efficiency on the same grid share one factor. This needs a small **additive** `Rbf2D` API — a factored
object re-solvable with a new RHS. **The existing constructor must keep working unchanged**; it is on
the critical path of the shipping loadpull contour display. **Gated by Tier 6.**

### R-hrf-10 — the published `DataSet`
Per `harmonicarf.md` §5, including `Zs_conv` and `Zin`. This is the contract H4–H7 build on, so get
the axis names and shapes right now. Cube names and axis order are part of the deliverable.

### R-hrf-11 — `.charm` I/O (D10)
JSON, `FormatVersion`, roles/fields absent → built-in default (the `DataDisplayConfig` pattern).
Touchstone files referenced by **bare filename**, resolved relative to the `.charm` (the `.cdd` rule).
Setup only — **no results**. A `.charm` whose external-model reference cannot be resolved reports which
file is missing; it does not fail silently and does not substitute another model.

### R-hrf-12 — Touchstone coverage
If an embedding file does not reach `K·f₀`, **refuse**, naming the file, the missing frequency, and the
file's range. An explicit opt-in gives constant hold-last-value — **never polynomial extrapolation**.
Silent extrapolation to 5f₀ would corrupt precisely the study this tool exists for.

---

## 5. The oracle ladder

Each tier is a separate, independent check. **Where a tier names a closed form, that closed form is
the oracle — not another circuitRF path agreeing with itself.**

| tier | what | pass |
|---|---|---|
| **0** | `Z_S,intr` against a **hand-derived closed form**: ideal `Ids = gm·Vgs` with source-lead `Ls`, fed from `Zs` ⇒ `Z_seen = (Zs + Z_Ls)/(1 − gm·Z_Ls)` | solver tolerance |
| **1** | `Z_S,intr` with `Ls = 0`, no external feedback, no embedding cross-coupling ⇒ **exactly** the passive source network at the intrinsic plane, every `k` | ≤ 1e-10 rel |
| **2** | `Y_NN(Z)` via R-hrf-6's Schur route vs. `HbLinearExtractor`'s direct extraction with the same Z stamped — across near-short (1e-6 Ω), near-open, and complex | ≤ 1e-12 rel |
| **3** | harmonicaRF vs. `LoadpullEngine` on the **same** configuration: Pout, Gain, DE, PAE, intrinsic spectra | solver tolerance |
| **4** | charge off + no extrinsic network ⇒ load glyph ≡ marker. Charge on ⇒ they separate, and the separation matches a hand `jωq` on a known Cds | exact / 1e-10 |
| **5** | batched vs. unbatched external evaluation | **bit-identical** |
| **6** | `Rbf2D` re-solve against a cached factor vs. full rebuild; cache invalidates on NaN-mask change | **bit-identical** |
| **7** | a grid with a deliberate hole produces **no** iso-line inside it | no polyline vertex inside the excluded disc |
| **8** | cost: ms/solve (built-in, external before/after), ms for a 61-point grid, fit and extract time **separately** at n = 37 / 61 / 200 | reported, measured alone |

**Tier 0 is the one that matters most.** It is the only check in this brief that tests D2's formulation
against something derived independently of the solver. A regression test must also assert
`Z_S,intr ≠ V_g,k / I_g,k` on a fixture where they differ — pinning the correction the way the `Iin`
fix is pinned, so it cannot silently revert.

---

## 6. What must NOT be built here

- **Any UI. Any Avalonia. Any drawing.** Not a control, not a view-model, not a colour.
- **The inverse solve** (intrinsic glyph drag, §6.6) — H6.
- **The frame scheduler, concurrency, cancellation, adaptive quality** (§6.8) — H5. Build the engine
  single-threaded and re-entrant-*ready* (no static mutable state, no shared `TunerModel`), but do not
  build the pool.
- **The colour theme, `Harmonica.*` roles, `HarmonicaRenderTheme`** — H4/H7.
- **Two-tone, a real bias network, baseband/video termination.** v2, and they arrive together.
- **Any change to `LoadpullEngine`'s behaviour, `PursuitEngine`, or any Hero golden.** harmonicaRF
  *calls into* and *reuses* that machinery; it does not modify it. Extracting a shared helper is fine
  if and only if the existing callers' results are bit-identical.
- **A second contour, surface, FOM, or colour implementation.** §0.3 item 5.
- **A sixth FET model, or any new device model.** §0.3 item 1.
- **Widening any validated limit** — `Dcim.ValidatedRhoOverLambda*`, `SurfaceMesher.UnknownCeiling`,
  or any refusal threshold. Nothing here needs them and they were measured.
- **Polynomial extrapolation of Touchstone data.** R-hrf-12.

---

## 7. Milestones, each with its own gate

| M | What | Gate |
|---|---|---|
| **M1** | Batched external evaluation (§3) + its three measurements | Tier 5; **all hero goldens bit-identical**; **a legitimate stopping point** |
| **M2** | `src/Harmonica` skeleton + `tests/Harmonica.Tests` (both into `circuitRF.slnx`), `HarmonicaContext`, in-place mutation, the circuit model, the published `DataSet`, `.charm` I/O | Firewall green with `CircuitRF.Harmonica` added; R-hrf-5/10/11/12 |
| **M3** | Open-port extraction + Schur re-termination (R-hrf-6) | **Tier 2** |
| **M4** | `Z_S,intr` (R-hrf-3), `Z_L,intr` (R-hrf-2), the conduction split (R-hrf-1), `Zin` (R-hrf-4) | **Tiers 0, 1, 4** + the `≠ V_g/I_g` regression |
| **M5** | Secant Pin search, contour grid, holes + support mask, MXP/MXE, RBF factor cache | Tiers 6, 7; R-hrf-7/8/9 |
| **M6** | Equivalence + cost; root `CLAUDE.md` `FetModel` correction (§0.3 item 1) | **Tier 3**, Tier 8 |

**Two natural fault lines.**

- **After M1.** If batching does not pay, stop and report — that changes the external-model story and
  the owner decides, not you.
- **After M3.** If the Schur route does not reproduce direct extraction to 1e-12, **stop.** Everything
  downstream assumes it, and a subtly wrong `Y_NN` would surface as physics that looks plausible and
  is not. Do not "tighten the tolerance until it passes."

M4 is the correctness heart of the brief. If M5 turns out to be larger than it looks, **stopping after
M4 and reporting is a good outcome** — an engine that is right and fast, with contours still on the
uniform ladder, is strictly better than today and leaves H4 unblocked.

---

## 8. File map (indicative)

```
src/Engine/HarmonicBalance/HbNewton.cs      M1: the per-sample Evaluate loop (~line 281) → gather,
                                            one batched call, scatter. Built-ins keep the scalar path.
src/Core/Devices/External/ExternalDeviceModel.cs   M1: expose the batched entry; no provider changes

src/Harmonica/                              NEW project — framework-free, no Avalonia
  HarmonicaContext.cs                       R-hrf-5: netlist ownership, structural vs value change
  CircuitModel.cs                           DUT, embedding stack, terminations, bias (§4.1–§4.4)
  InterfaceNetwork.cs                       R-hrf-6: open-port extraction + Schur re-termination
  IntrinsicPlane.cs                         R-hrf-1/2/3: the conduction split, Z_L, Z_S via J′
  PinSearch.cs                              R-hrf-7: tickle + secant compression
  ContourGrid.cs                            R-hrf-8/9: grid, holes, support mask, RBF factor cache
  HarmonicaDataSet.cs                       R-hrf-10
  CharmIo.cs                                R-hrf-11

src/RfCore/Loadpull/Rbf2D.cs                R-hrf-9: ADDITIVE factored-kernel API only
tests/Harmonica.Tests/                      NEW — the full §5 ladder
tests/Firewall.Tests/                       add CircuitRF.Harmonica to the assertion
circuitRF.slnx                              both new projects
CLAUDE.md (root)                            the FetModel correction (§0.3 item 1)
```

---

## 9. What to report back on, whatever else happens

1. **M1's three numbers** (§3), and whether the built-in figure moved. If any hero golden shifted by
   an ulp, say so and say why.
2. **Tier 0's result** — the measured `Z_S,intr` against `(Zs + Z_Ls)/(1 − gm·Z_Ls)`. **This is the
   deliverable of the brief.** If it does not match, report that rather than adjusting the
   formulation to fit.
3. **Tier 2's worst relative error**, across all three Z regimes, and what `Y_bb + Y_t` actually was.
4. **The cost of a 61-point contour grid**, measured alone, against the 0.45 s target — and **fit and
   extract time reported separately from solve time** at n = 37 / 61 / 200 (§6.4.1's obligation). If
   the fit dominates at realistic n, say so; the named fallback is Delaunay / natural-neighbour, and
   that is the owner's call, not yours.
5. **Solves per Γ point** under the secant search vs. the uniform ladder's ~30.
6. **What you added to the `Category=Benchmark` tier**, in minutes.
7. **Anything in `harmonicarf.md` that turned out to be wrong.** The design was written against
   measurements and code reading, not against a running implementation — treat a contradiction as a
   finding to report, not an obstacle to work around.

---

## 10. The follow-on briefs (not this one)

Named so the scope of *this* brief is unambiguous and nothing is orphaned:

| brief | phase | scope |
|---|---|---|
| `brief-harmonicarf-h4-document-and-panels` | H4 | Document shell, locked layout, four panels, markers, intrinsic glyphs, `Harmonica.*` roles + `HarmonicaRenderTheme`, both variants |
| `brief-harmonicarf-h5-frame-scheduler` | H5 | Concurrency, per-worker contexts, latest-wins cancellation, adaptive quality tiers, separate fit/solve timers |
| `brief-harmonicarf-h6-inverse-solve` | H6 | Simultaneous all-harmonic inverse solve, Broyden updates, reachability shading, operating-point cursor |
| `brief-harmonicarf-h7-edit-display` | H7 | Edit Display, trace picker, clipboard, `.gam` interchange, colour editor + reset, Tools menu, testbench export |
| `brief-harmonicarf-h8-standalone` | H8 | Standalone entry point + build config (**including the `ColorPicker` Fluent `.xaml` StyleInclude — it fails silently without it**) |
