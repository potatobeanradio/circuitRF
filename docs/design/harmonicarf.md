# harmonicaRF — Interactive Harmonic-Termination Explorer

**Status:** Draft (rev 1) for review · **Date:** 2026-08-06
**Reads with:** `docs/design/harmonic-balance.md` (the HB engine this drives), `docs/design/loadpull.md`
(the `Tuner` component and the Γ-grid × Pin-sweep pattern this reuses), `docs/design/p1tone-harmonic-terminations.md`
(the per-harmonic band rule), `docs/design/data-display.md` (the plot/marker/contour layer this reuses),
`docs/design/ui-architecture.md` (the firewall this obeys), `docs/design/loadpull-contours.md`,
`docs/design/pdk-external-devices.md` (Verilog-A / vendor-kit device workers).
**Defers to:** `src/Engine/CLAUDE.md` (frozen FFT/sign conventions), `src/Ui/DataDisplay/CLAUDE.md`
(display-layer conventions).

> **Spelling.** The product is **harmonicaRF**, one word, lowercase `h`, file extension `.charm`.
> (The name was written both "harmonicaRF" and "harmoniaRF" during scoping; harmonicaRF is the one.)

No code is written until this note is approved.

---

## 1. What harmonicaRF is

harmonicaRF is an **interactive research and teaching instrument for source and load harmonic
termination of a nonlinear active device.** It is not a schematic simulator and it does not replace
any circuitRF analysis. It answers one question, continuously and at frame rate:

> *If I terminate this device like **this** at f₀, 2f₀, 3f₀ … on both the source and the load side,
> what is the current generator actually doing, and what does it cost me in power and efficiency?*

The distinguishing claim is **liveness**. Every other tool in this space is post-process: you set up a
sweep, wait, and read a static contour. harmonicaRF runs harmonic balance *while the user's mouse is
moving*, so the relationship between a termination and the loadline is felt rather than inferred.

The second distinguishing claim is that **the intrinsic plane is the primary view.** Wherever it is
meaningful, harmonicaRF shows the voltage and current *of the current generator*, not of the package
terminals — because that is the plane a designer reasons about when inventing a new termination
strategy.

### 1.1 What it is not

- Not a general circuit simulator. There is exactly one DUT, two termination planes, and an optional
  linear embedding. Anything more structured belongs in a circuitRF schematic.
- Not a replacement for `loadpull` / `loadpull_pursuit`. Those remain the batch, high-point-count,
  reference-grade path. harmonicaRF trades grid density for interactivity.
- Not two-tone. **v1 is single-frequency, single-tone** (§4.6). Two-tone is a v2 slice and carries a
  real bias network with it (§4.4).

### 1.2 Relationship to circuitRF

harmonicaRF is invoked from a new circuitRF **Tools** menu, runs as a document with its own menu set
(no Simulate menu — it is always simulating), works with or without a workspace open, and is
structured so it can ship as a standalone binary (§3).

---

## 2. The performance budget — measured, not assumed

Everything in this design is sized against real measurements taken on the shipping engine before the
design was written. Machine: Apple M4 (4 P-cores + 6 E-cores), .NET 10 Release, single-threaded,
Hero-2's SDD GaN HEMT.

| case | ms / HB solve |
|---|---|
| warm-seeded, K=3 | 0.74 |
| warm-seeded, **K=5** (the default order) | **0.94** |
| warm-seeded, K=7 | 1.08 |
| warm-seeded, K=10 | 1.59 |
| cold (nonlinear-DC seed every point), K=5 | 2.45 |
| Hero-4 (two FETs), K=5 warm | 1.48 |

Integration check — the **full Hero-3 loadpull**, 20 Γ points × 32 Pin steps = **640 HB solves in
0.55 s** (0.86 ms/solve), including tuner mutation and the live compression logic.

**What that buys.** A 61-point Γ grid with ~8 bisection Pin steps per point is ~500 solves:
**≈ 0.45 s single-threaded, ≈ 70 ms across 8 workers.** A single Pin drive-up (the loadline and
power-sweep panels) is ~15–30 solves ≈ **20 ms**. Both targets in §6.8 are comfortably reachable for
built-in models.

### 2.1 Dense vs. sparse MNA — the question, answered

The owner asked whether a dense MNA would be faster than sparse. **It would not, and it is the wrong
lever.** The HB Newton Jacobian is *already* dense (NumFlat LU, `harmonic-balance.md` §8) and it is
tiny: `2·N_nl·(K+1)` = **24 × 24** for one grounded-source FET at K=5. Sparse is used only for the
linear-partition MNA that extracts `Y_NN`. On a circuit this small — a DUT, two tuners, and an
embedding — neither factorization is the cost. The cost is the Newton loop: IFFT → `Evaluate` → FFT →
Jacobian fill, repeated per iteration.

The levers that *do* matter, in descending order of value:

1. **§6.2 — the pre-terminated interface network.** Extract the linear partition once per harmonic as
   an *(N_nl + N_term)*-port network with the tuner ports left open, then obtain `Y_NN(Γ)` for any
   termination by closing those ports algebraically. A marker move then costs **no MNA solve at
   all**. This is the single biggest structural win and it is what makes marker drag free.
2. **Never re-elaborate.** Mutate the termination models in place. `LoadpullEngine` already proves
   this pattern (`TunerModel.SetHarmonicOverride`, `SetSourceDrive`). Going through a global-variable
   override forces a full re-elaboration and is ~1000× the cost of the thing being changed.
3. **Warm-start everything.** 0.94 ms warm vs. 2.45 ms cold is a 2.6× difference, and the warm path
   skips the nonlinear-DC seed entirely (`harmonic-balance.md` §11.1).
4. **Parallelism across Γ points** (§6.7). Ten cores available; a full contour refresh is
   embarrassingly parallel.

### 2.2 The one thing that can break the premise: external models

`HbNewton.cs` calls `ec.Evaluate(...)` **once per time sample**, inside the Newton loop
(`src/Engine/HarmonicBalance/HbNewton.cs:281`). For a built-in model that is a direct call and costs
nothing. For an **external device** (Verilog-A `.osdi` via `osdi-worker`, or a vendor kit via
`senior-worker`), `ExternalDeviceModel.Evaluate` is **one IPC round trip per time sample**.

At K=5 the grid is `nextpow2(4K)` = 32 samples; with 3–4 warm Newton iterations that is ~100–130 round
trips per HB solve. `DeviceWorkerInstance` documents **~100 µs per unbatched point vs ~4 µs batched** —
so an external device costs roughly **10–30 ms per HB solve** instead of ~1 ms, and a live contour
refresh becomes ~15 s. That is not "slower"; that is a different product.

`DeviceWorkerInstance.EvaluateBatch` **already exists** and already does the right thing
(one round trip for the whole vector). **Nothing in `src/Core` or `src/Engine` calls it.**

> **Prerequisite work item H0 (its own brief, before harmonicaRF's solver lands).**
> Push a batched evaluation path down the HB inner loop: gather the whole time-grid of port-voltage
> vectors, call `IExternalDeviceProvider.EvaluateBatch` once, scatter the results. Built-in models
> keep the scalar path (the default interface method already provides the fallback, so no model
> changes). Expected: external-model HB solves drop from ~10–30 ms to ~1.5–3 ms, which brings
> Verilog-A and PDK devices inside the interactive budget. This is the owner's "best effort" answer
> to live external models, made concrete.
>
> The same change benefits `loadpull`, `loadpull_pursuit`, and every ordinary HB sweep on a PDK
> device — it is not harmonicaRF-specific work charged to harmonicaRF.

If H0 lands and an external model is still too slow (a heavy ASM-HEMT with self-heating, say), the
frame scheduler (§6.8) degrades that model to freeze-and-snap contours automatically. Liveness
degrades; correctness never does.

---

## 3. Architecture and packaging

### 3.1 Two projects

```
src/Harmonica/          NEW. Framework-free. References Core, Engine, RfCore. NO Avalonia.
                        The circuit model, the solve orchestrator, the frame scheduler,
                        the inverse solver, .charm I/O, the published DataSet.
                        Fully headless-testable.  → tests/Harmonica.Tests/

src/Ui/Harmonica/       NEW folder inside the existing Ui project. Avalonia + the existing
                        DataDisplay display layer. The document, the view-models, the canvas,
                        Edit Display mode, the menus.

src/Ui/ProgramHarmonica.cs   A second entry point + build configuration producing a standalone
                             `harmonicaRF` binary from the same project.
```

**Why this split.** The owner wants harmonicaRF compilable as its own app. Everything worth reusing —
`PlotControl`, `Marker`, `ContourData`, the Smith renderer, `PlotExporter`'s clipboard path — lives in
`src/Ui` and is Avalonia-bound. Three routes were considered:

- **(a)** Everything in `src/Ui` behind a second entry point. Zero refactor, but the physics is then
  untestable without a UI and sits on the wrong side of the firewall.
- **(b) [chosen]** Framework-free half in `src/Harmonica`, Avalonia host inside `src/Ui`, second entry
  point. The firewall holds, the physics is headless-testable, and the standalone binary is a build
  configuration rather than a project split.
- **(c)** Extract the whole display layer out of `src/Ui` into `src/Display` first, then host from
  there. Architecturally the cleanest and the honest long-term answer — but it is a large refactor of
  a subsystem with a 15,000-line `CLAUDE.md`, and it is not needed to ship harmonicaRF.

(b) is chosen. **(c) is not precluded**: if the display layer is ever lifted out, `src/Ui/Harmonica`
moves with it and `src/Harmonica` does not change at all. That is the same "don't preclude it,
don't build the ceremony now" discipline `ui-architecture.md` §4 applies to the display layer itself.

### 3.2 Firewall compliance

`src/Harmonica` references **no UI framework** and is added to the `tests/Firewall.Tests`
assembly-reference assertion alongside `RfCore`, `Core`, `Engine`, and `Cli`. The standalone binary
does not weaken this: it is `src/Ui` with a different `Main`.

### 3.3 What already exists and is reused verbatim

Cataloguing this up front because a surprising fraction of harmonicaRF is already built.

| need | existing component | where |
|---|---|---|
| HB single-point solve, warm-startable | `HbEngine.RunSinglePoint(p, warmStart)` | `src/Engine/HarmonicBalance` |
| per-harmonic programmable termination | `TunerModel` (`SetHarmonicOverride`, `SetSourceDrive`) | `src/Core/Devices` |
| harmonic-band assignment rule | `roundHalfUp(|f|/f_c)` | `p1tone-harmonic-terminations.md` §3 |
| Γ-grid × Pin drive-up, compression stop | `LoadpullEngine.RunOneTermination` | `src/Engine/Loadpull` |
| scattered Γ points → surface | `Rbf2D`, `LoadpullSurface` | `src/RfCore/Loadpull` |
| surface → iso-lines | `ContourExtractor` | `src/RfCore/Loadpull` |
| derived FOMs (Pout, DE, PAE, Zin, IRL, AM-PM) | `LoadpullPostProcessor.Enrich` | `src/RfCore/Loadpull` |
| Γ grid file I/O | `GamReader` / `GamWriter` | `src/Engine/Loadpull` |
| Smith / Rect plot, axes, ticks, pan/zoom | `PlotControl`, `AxesRenderer`, `PlotRenderer` | `src/Ui/DataDisplay` |
| contour model + level machinery | `ContourData` (`MetricName`, `ConstraintKind.Compression`, `ConstraintValue`) | `src/Ui/DataDisplay/Models` |
| markers, marker info boxes, drag/hit-test | `Marker`, `TraceRenderer_MarkerRenderer` | `src/Ui/DataDisplay` |
| copy plot to clipboard (PDF/SVG/JSON/bitmap) | `PlotExporter.CopyPlotToClipboardAsync` | `src/Ui/DataDisplay` |
| placeable-plot canvas, undo/redo | `PlotContainerViewModel`, `UndoRedo` | `src/Ui/DataDisplay` |
| trace picker over a `DataSet` | `TraceRowViewModel`, `CubeTraceSpecParser` | `src/Ui/DataDisplay` |
| document shell, tear-off, dirty tracking | `DataDisplayDocument` pattern | `src/Ui/DataDisplay` |
| native large-signal FETs | `AngelovFetModel`, `Curtice{Cubic,Quadratic}`, `Materka`, `Statz` | `src/Core/Devices/Fet` |
| Verilog-A / vendor devices, i **and** q | `ExternalDeviceModel` → `NonlinearResult(i, q, dg, dc)` | `src/Core/Devices/External` |

> **Correction to standing memory:** the root `CLAUDE.md` says native `FetModel` "is a plan, not
> code." That is **stale** — five native large-signal FET models exist on a shared `FetModelBase`
> with selectable gate charge (`CapModel` 0 = none, 1 = constant Cgs/Cgd, 2 = junction). This note
> depends on them. Root `CLAUDE.md` should be corrected when this brief lands.

---

## 4. The circuit harmonicaRF solves

### 4.1 Topology

Exactly one signal path, fixed:

```
                     ┌──────────── embedding stack ────────────┐
  SourceTuner ─ s2p_in ─┐                                   ┌─ s2p_out ─ LoadTuner
                        ├─ s4p / s6p (1,2 outer · 3,4/5,6 DUT) ─┤
                        └─ lumped pkg ─ DUT ─ lumped pkg ────┘
```

Every element of the embedding is optional and any combination is legal. The **cascade order is
fixed, outside in: s2p → s4p/s6p → lumped → DUT** (owner-confirmed). Both termination planes are the
tuner planes; both intrinsic planes are inside the DUT (§4.5).

- **`s2p_in` / `s2p_out`** — a two-port at either external port. Port 1 faces the tuner, port 2 faces
  inward.
- **`s4p` / `s6p`** — one block embedding the whole DUT. Ports **1,2** face outward (toward
  `s2p`/tuner); ports **3,4** (s4p, 2-port DUT) or **3,4,5,6** (s6p, 3-port DUT) face the DUT.
- **Lumped package** — a canonical, fixed-topology extrinsic network with editable values:
  series `Rg, Lg` / `Rd, Ld` / `Rs, Ls`, shunt `Cpg, Cpd, Cgd_ext`. Any value may be zero. This is
  deliberately *not* an arbitrary sub-schematic — an arbitrary network is what circuitRF is for.

Internally this is an ordinary elaborated netlist built by harmonicaRF (not authored by the user), so
every existing engine path applies unchanged.

### 4.2 Terminations, markers, and the band rule

Terminations are declared **per harmonic band** and follow the existing, locked band rule
(`p1tone-harmonic-terminations.md` §3): a spectral line at frequency `f` is presented the impedance of
band `n = roundHalfUp(|f| / f_c)`. Single-tone with `f_c = f₀` collapses this to `Z[k]` ↔ harmonic
`k` exactly.

- A **marker** on a Smith chart *is* a band's termination. `S1`/`L1` are the fundamental source/load
  and are **always present**. `S2`/`L2`, `S3`/`L3` … are added and removed from a menu.
- A band with **no marker** is terminated at **1e-6 Ω** (owner-confirmed: ohms, a near-short), for
  every band from 2 to K.
- **Band 0 (DC) is not a marker.** With ideal bias (§4.4) it is a hard short to the supply.

Marker rendering follows the Data Display convention — a filled circle with a thin black outline
stroke, name rendered inside:

| band | fill |
|---|---|
| f₀ (S1/L1) | green |
| 2f₀ | pastel red |
| 3f₀ | pastel yellow |
| 4f₀ | pastel blue |
| 5f₀ | pastel purple |
| 6f₀+ | the five-colour cycle repeats |

**Markers are linked across charts.** A marker is a property of the *circuit*, not of a plot. Moving
`L2` on the power chart moves it on the efficiency chart in the same frame, because both are views of
the same model object.

### 4.3 The DUT

- **Source is always grounded.** A 2-port DUT is used as-is. A **3-port DUT has its source port
  automatically grounded** — so there are only ever two termination planes and two marker families.
- Supported DUT kinds:
  - the five **native FET models** (`Angelov`, `Curtice Cubic/Quadratic`, `Materka`, `Statz`);
  - an **SDD** with user-entered drain-current (and gate-current) equations, authored inside
    harmonicaRF using the standard expression language;
  - `NonlinearC`, `Diode` (for teaching and for the degenerate cases);
  - any **external device** — a compiled Verilog-A `.osdi`, or a vendor-kit part via `senior-worker`.
- **Load routes:** the File menu (*Set DUT…*), and drag-and-drop from the circuitRF Library palette
  when a workspace is open.

### 4.4 Bias — ideal (v1)

Bias is an **ideal connection**: a perfect choke and a perfect DC-block. The RF terminations never see
DC; band 0 is a hard short to the supply. This is faster (no `InductanceRegularization`
fail-then-retry on every one of thousands of solves), pedagogically cleaner, and correct for a
single-tone study.

> **What ideal bias hides, stated so v2 does not inherit a surprise.** The baseband / video
> termination is a real design variable for memory effects and IM3 asymmetry — but it only *does*
> anything under multi-tone excitation, where `(1,−1)` lands in band 0. Under single tone, band 0
> carries only DC. So ideal bias costs nothing in v1 and must be replaced by a real bias network the
> same day two-tone arrives.

Bias entry is either **Vgs** directly or **Idq** (§7.5).

### 4.5 The intrinsic plane — conduction current only

**Decided:** the intrinsic quantities are the **conduction current** — the `i` half of the
`(i, q, dg, dc)` contract — *excluding* the displacement current `jωq` from Cgs/Cgd/Cds.

This split already exists natively and works for external devices too (`ExternalDeviceModel.Evaluate`
returns `NonlinearResult(r.Current, r.Charge, r.Conductance, r.Capacitance)`), so nothing new is
needed in the device layer.

Definitions, per harmonic `k`:

```
Vds_intr(t) , Ids_intr(t)          time-domain, conduction only      → the loadline (§7.3)
Z_L,intr(k) = − V_d,k / I_d,k^cond                                   → the load-side glyph
Z_S,intr(k) = see §4.5.2 — NOT a voltage/current ratio               → the source-side glyph
```

**The two sides are not symmetric, and treating them as if they were is a real error.** §4.5.1–§4.5.3
work this out; §4.5.4 covers `Zin`, which is a different quantity again and is also wanted.

**Three consequences that must be visible in the UI, not buried:**

1. The glyph **coincides with its marker only when charge is off *and* there is no extrinsic
   network.** With charge on, terminal current ≠ conduction current, so the glyph separates from the
   marker even with a bare device. This is the physically informative behaviour and is the whole
   reason for the conduction-only choice — had we used terminal current, KCL would pin the glyph to
   the marker forever and it would convey nothing.
2. `|Γ_intr|` **can exceed 1**, legitimately. The conduction current is not the terminal current, so
   an intrinsic reflection outside the unit circle is ordinary, not an error. It is rendered outside
   the chart boundary (compressed radial scale beyond |Γ|=1) rather than clamped or hidden.
3. Glyphs are drawn as **subtle triangular markers**, always **beneath** the round termination
   markers in z-order, in the same per-band colour at reduced saturation.

#### 4.5.1 The load side — why a ratio is legitimate there

At the intrinsic drain the conduction current is an ideal **current source** injecting into the node.
Everything else attached to that node — Cds, the Cgd path, the extrinsic network, the load tuner — is
what it drives. So

```
Z_L,intr(k) = − V_d,k / I_d,k^cond
```

is literally *the impedance the current generator drives into*, at harmonic `k`, in the large-signal
(describing) sense. It is also exactly the impedance the loadline is drawn against, so the glyph and
the loadline are two views of one quantity. This definition stands.

#### 4.5.2 The source side — a ratio is WRONG, and what it actually computes

**At the intrinsic gate there is no generator.** The gate is the *controlled* node; the excitation
arrives from outside. And for a Thévenin source `Vs` behind `Zs` driving a load `ZL`,

```
V = Vs·ZL/(Zs+ZL) ,  I = Vs/(Zs+ZL)   ⇒   V/I = ZL
```

— a voltage-over-current ratio at a driven node recovers the **load**, never the source. So
`V_g,k / I_g,k` returns the impedance looking *into* the device: a version of `Zin` referred to the
intrinsic plane. It is not the impedance the gate control sees. With **conduction-only** current it is
worse still — a FET's gate conduction current is essentially zero below turn-on, so the ratio is
numerically meaningless as well as conceptually wrong.

> **This exact class of error has already bitten this codebase once.** The loadpull/pursuit `Zin`
> computed `V[src] / INl[src]` — dividing by the SDD's *intrinsic gate* current — and reported
> 5000 Ω where the true source-seen impedance was 192 Ω, whenever a user wired any passive at the
> gate. See `bugfix-loadpull-zin-source-current` and `src/Engine/Loadpull/CLAUDE.md`. The fix was to
> measure the **true delivered current** (`Iin`), not a device-internal one. harmonicaRF must not
> reintroduce the same mistake at a different reference plane.

#### 4.5.3 The correct source impedance — a perturbation of the converged large-signal state

The impedance the gate control sees is a **Thévenin impedance looking outward**, and a Thévenin
impedance is only obtainable by **perturbation**: inject a small test current into the intrinsic
gate–source port at harmonic `k`, on top of the converged large-signal steady state, with the
independent drive killed, and read the port-voltage response.

Two things make this harder than a passive network calculation, and both are real:

**(a) The outward path can run through the device.** The clearest case is a **shared source lead**.
Because harmonicaRF grounds the source at the *package* plane (§4.3), any `Ls`/`Rs` sits between the
intrinsic source and ground and therefore carries the **drain** current — it is common to both loops.
For an ideal `Ids = gm·Vgs` device fed from `Zs`:

```
inject test current It into the g′–s′ port (It in at g′, out at s′):
    KCL at g′:  It = V_g / Zs                      ⇒  V_g = It·Zs
    KCL at s′:  Ids = It + V_s / Z_Ls              ⇒  V_s = Z_Ls·(Ids − It)
    V_t = V_g − V_s = It·(Zs + Z_Ls) − Z_Ls·Ids ,      Ids = gm·V_t
⇒   Z_seen = V_t / It = (Zs + Z_Ls) / (1 + gm·Z_Ls)
```

> **Sign corrected 2026-08-06** (owner-approved, brief-harmonicarf-h4-h5 §0.1). This block previously
> read `V_t = It·(Zs + Z_Ls) + Z_Ls·Ids` and therefore `(Zs + Z_Ls)/(1 − gm·Z_Ls)`. Under circuitRF's
> **passive sign convention** `I[p]` is the current *into* the device at the port's `+` terminal and
> *out of* its `−`, so port 2 = (drain, source) delivers `Ids` **into** node s′ *from the device* and
> KCL there reads `Ids = It + V_s/Z_Ls` — the `Z_Ls·Ids` term subtracts. Two independent checks agree
> with the `+` form: the degenerate case `Zs = 0`, `Z_Ls = R` gives `R/(1 + gm·R) → 1/gm`, the
> source-follower output impedance, which is what looking *out* of a degenerated gate–source port must
> give (the old form is **negative** for `gm·R > 1`, which a passive degeneration cannot produce); and
> H0–H3's Tier-0 gate matches the `+` form to **1.4e-16** across three `Ls` values and every harmonic
> while the `−` form is out by a factor of two. **The physics here was right and only the sign was
> wrong** — the `gm` term's presence, which is the whole point of (a), is unchanged, and the shipped
> code/tests already implement the `+` form.

The `gm` term is not optional. The impedance the gate control sees **depends on the device's own
transconductance**, and under drive on its large-signal value. The same structure arises from an
external feedback element (`Cgd_ext` in the lumped topology, §4.1) and from any embedding block with
gate–drain cross-coupling (an `s4p`'s S₁₃/S₁₄). This is the "feedback" difficulty.

**(b) The test current must not be allowed into the device's own gate shunt.** Cgs and the gate diode
are the *thing being terminated*, not part of the source. Letting the test current flow into them
returns `Z_source ∥ Z_in`, which is neither quantity.

**The computation.** The converged HB Jacobian is precisely the linearization of the whole coupled
system about the large-signal operating point (`harmonic-balance.md` §7):

```
J = Y_NN + ∂I_nl/∂V + ω·∂Q_nl/∂V         (real-split, 2·N_nl·(K+1) square)
```

Being the **conversion matrix**, it already carries the harmonic coupling — a perturbation at harmonic
`i` produces response at harmonic `k`. Split the interface unknowns into the gate port `g` and
everything else `r`:

```
J = [ J_gg  J_gr ]
    [ J_rg  J_rr ]
```

Form `J′` from `J` by replacing **only the gate-port self block** with its linear part:

```
J′_gg = (Y_NN)_gg            — the device's own ∂I_g^dev/∂V_g removed   (addresses (b))
J′_gr = J_gr ,  J′_rg = J_rg ,  J′_rr = J_rr        — everything else kept  (preserves (a))
```

Keeping `J_rg` retains the `Vg → Ids` path; keeping `J_rr` and `Y_NN` retains the common-source and
embedding return paths. Then

```
Y_S,intr = J′_gg − J′_gr · J′_rr⁻¹ · J′_rg          (Schur complement)
Z_S,intr = Y_S,intr⁻¹ = ( J′⁻¹ )_gg                 (equivalent, and cheaper)
```

`Z_S,intr` is a **harmonic-conversion impedance matrix**, `2(K+1) × 2(K+1)` in real-split form:

- its **diagonal entry (k,k)** is the impedance at harmonic `k` in the describing sense — this is what
  the source glyph plots and what a source-termination decision is made against;
- its **off-diagonal entries** measure how strongly the source network converts harmonic `i` into
  harmonic `k`. That is a genuinely useful and rarely-visible quantity for source harmonic
  engineering, so it is published as `Zs_conv` (§5) and is plottable in Edit Display.

**Cost: negligible.** `J` is already assembled and factored at convergence. For one grounded-source FET
at K=5, `J` is 24 × 24 and `J′` is a copy with one 12 × 12 block edited — one inverse, or `2(K+1)`
back-solves, per displayed frame. Microseconds against a ~20 ms frame budget. It is computed once per
displayed operating point, not per grid point.

**Validation.** The closed form `(Zs + Z_Ls)/(1 − gm·Z_Ls)` above is an **exact oracle, independently
derived**, for a linear-`gm`-plus-`Ls` fixture. It gates the whole formulation — a second independent
formulation rather than the solver agreeing with itself (§9 item 9). A second oracle: with `Ls = 0`,
no external feedback, and no embedding cross-coupling, `Z_S,intr(k)` must reduce **exactly** to the
passive source network transformed to the intrinsic plane, for every `k`.

#### 4.5.4 `Zin` — a different quantity, also wanted

`Zin` is the impedance looking **into** the DUT from outside, at the **extrinsic** reference plane, at
the fundamental. It is what a matching network is designed against, and the owner wants it in the
MXP/MXE summary (§7.5):

```
Zin = V_in,1 / I_in,1          I_in,1 = the TRUE current delivered into the DUT input node
```

`I_in,1` is **not** the device's intrinsic gate current — that is precisely the bug quoted in §4.5.2.
harmonicaRF reuses the machinery built for that fix: `LoadpullEngine.ComputeSourceInputCurrent`, the
`Iin` cube, and `LoadpullPostProcessor`'s existing preference for `Iin` over `INl[src]`.

Because the device is **not unilateral**, `Zin` moves with the load termination. The summary therefore
reports `Zin` **at the MXP load and at the MXE load separately**, never as a single number — and the
conjugate-match target `Zin*` inherits the same load dependence (`loadpull.md` §8).

#### 4.5.5 Identifying the intrinsic quantities on an external model

For a native FET or an SDD, "intrinsic" is the i/q split and needs no user input — there is no node
between the caps and the generator.

For a **Verilog-A or vendor model**, the intrinsic plane is a set of real internal nodes (which our
elaborator already makes first-class HB unknowns — `ExternalDeviceModel` remarks: internal nodes are
"deliberately NOT eliminated locally … an internal node voltage carries its own harmonic content and
must be a first-class unknown"). Nothing can *guess* which one is the intrinsic drain.

So harmonicaRF ships an **Intrinsic Mapping panel**: it lists the model's declared node labels and
terminal currents (from `ExternalDeviceDescriptor.Nodes[].Label` and `TerminalNames`) and asks the
user to name

- the intrinsic **drain** node and intrinsic **gate** node,
- the **conduction-current** quantity for each,
- which external pin is the **source** (for the auto-ground of §4.3).

The mapping is keyed by model type and persisted in `.charm`, so it is set once per device.
Until it is set, the intrinsic panels are drawn empty with an explanatory overlay — never with a
plausible-looking wrong answer.

### 4.6 Frequency and tone

**One frequency, one tone.** Both are settings in the strip (§7.5). Two-tone is v2.

---

## 5. The published `DataSet`

harmonicaRF's solver publishes a real `DataSet` of `DataCube`s — the same contract every circuitRF
analysis returns. This is load-bearing for three reasons: Edit Display's trace picker plots *anything*
(owner-confirmed), copy/paste into a circuitRF Data Display works for free, and the display layer
already knows how to consume it.

Principal cubes (single-tone, `harmonic` axis 0…K):

| cube | shape | meaning |
|---|---|---|
| `V` | `[node, harmonic]` | all user-facing node voltages, DC included |
| `I` | `[terminal, harmonic]` | branch/terminal currents |
| `Vds_intr`, `Vgs_intr` | `[harmonic]` | intrinsic voltages |
| `Ids_intr`, `Igs_intr` | `[harmonic]` | intrinsic **conduction** currents |
| `Vds_intr_t`, `Ids_intr_t` | `[tsample]` | the loadline, time domain |
| `Gamma_ext`, `Z_ext` | `[side, harmonic]` | the marker terminations as set |
| `Gamma_intr`, `Z_intr` | `[side, harmonic]` | the glyph values (load: §4.5.1 ratio; source: §4.5.3 diagonal) |
| `Zs_conv` | `[harmonic, harmonic]` | the full source-side harmonic-conversion impedance matrix (§4.5.3) |
| `Zin` | `[harmonic]` | extrinsic input impedance from the **true delivered** current (§4.5.4) |
| `PinSweep` | `[pin]` + FOM cubes | Pavl, Pdel, Pout, Gt, Gp, Pdc, DE, PAE |
| `Contour:<chart>` | `[gridpoint]` | Γ, metric value, converged/compressed flags |
| `DCIV` | `[vgs, vds]` | the family of curves |

`LoadpullPostProcessor.Enrich` supplies the derived FOMs so harmonicaRF does not re-derive Pout / DE /
PAE / Zin / IRL / AM-PM in a second place.

---

## 6. The solve layer (`src/Harmonica`)

### 6.1 The solve context

A `HarmonicaContext` owns one elaborated netlist and one `HbEngine`, built when the *structure*
changes (DUT, embedding stack, harmonic count, frequency) and **never rebuilt when a value changes**.
Terminations, drive, and bias are applied by mutating models in place — the `LoadpullEngine` pattern.

Structural change → rebuild (~ms). Value change → mutate (~µs). Getting this boundary right is what
keeps the tool live; going through the global-variable/`--set` path would force a re-elaboration per
frame and is explicitly forbidden.

### 6.2 The pre-terminated interface network

The dominant repeated cost in a naïve implementation is re-extracting `Y_NN` at every harmonic each
time a termination moves. It is avoidable.

At context build, extract the linear partition **once per harmonic** as an
**(N_nl + N_term)-port admittance**, with the tuner ports left **open** rather than terminated:

```
        ┌ interface (nonlinear-facing) ports ┐   ┌ termination ports ┐
Y_full = │            Y_aa                  Y_ab │
         │            Y_ba                  Y_bb │
```

Then for any termination set `Y_t = diag(1/Z[k])`, the interface admittance the devices see is the
closed-form Schur complement

```
Y_NN(Z) = Y_aa − Y_ab (Y_bb + Y_t)⁻¹ Y_ba
```

`Y_bb + Y_t` is `N_term × N_term` — **2 × 2** for one source and one load termination. Moving a marker
therefore costs a 2×2 inverse and two small products *per harmonic*, with **no MNA solve and no
refactorization at all**. `Y_aa`, `Y_ab`, `Y_ba`, `Y_bb` are invalidated only by a structural change.

The source excitation `Y_s·V_s` gets the same treatment: extract the open-port response once, apply
the termination algebraically per drive step.

> **Validation obligation.** This is an optimization of an existing, validated path, so it must be
> proven equal to it, not merely plausible: a gate test asserts `Y_NN(Z)` from the Schur route is
> bit-comparable (≤ 1e-12 relative) to `HbLinearExtractor`'s direct extraction with the same
> terminations stamped, across a spread of Z including near-short (1e-6 Ω), near-open, and complex.

### 6.3 The Pin drive-up and compression

Per Γ point, at fixed terminations:

1. A **tickle** point establishes small-signal gain (gain is termination-dependent, so the reference
   must be re-established at every Γ — the existing `LoadpullEngine` rule).
2. **Secant bisection on Pin** toward the target compression, rather than the batch engine's uniform
   1 dB ladder. Compression `Gmax − G(Pin) = x` is monotone in Pin over the region of interest, so a
   secant converges in ~5–8 solves against the ladder's ~30. This is the single largest constant-factor
   win in the contour loop and it is why a 61-point grid fits in ~500 solves.
3. Every solve warm-starts from the previous Pin step; every Γ point warm-starts from its
   VSWR-nearest converged neighbour (the existing rule, `loadpull.md` §3.3).
4. **Hard stops:** `PinMax`, or HB non-convergence.

**Points that never reach the target compression before `PinMax` are thrown out of the contour
grid entirely** (owner-confirmed). They are *not* extrapolated into. They are drawn on the Smith chart
as small hollow dots so the hole reads as measured rather than as a rendering gap. The power-sweep
panel still shows the full drive-up at the current L1 position, annotated *"did not reach P-x dB."*

### 6.4 The contour grid

- The grid is a **scattered set of Γ points, not a lattice** — because the user can **drag individual
  grid points** to new positions (owner-confirmed), and because `.gam` import brings in arbitrary
  measured grids.
- Scatter → surface via `RfCore.Loadpull.Rbf2D` / `LoadpullSurface`; surface → iso-lines via
  `ContourExtractor`. All framework-free, all already validated, all headless-testable.
- **Support mask.** An RBF over a scatter with holes punched in it (§6.3) rings near the boundary and
  will happily invent contours in regions with no data. Contours are therefore clipped to a
  **support mask** = the convex hull of converged points, minus a disc around each thrown-out point.
  Outside the mask nothing is drawn. This is a correctness requirement, not cosmetics: an invented
  efficiency ridge in a hole is exactly the kind of artifact this tool must never produce.
- **Dragging one grid point invalidates exactly one Γ sample** — ~8 solves ≈ 8 ms plus a re-fit. Live.
- **MXP / MXE are the argmax over the computed grid.** No search (owner-confirmed) — no
  `PursuitEngine` call. The summary readout is therefore always consistent with what is drawn.
- Contours are **unfilled** (iso-lines only), per the spec.

#### 6.4.1 Keeping the contour pipeline inside the frame budget

The surface fit and iso-line extraction are **not** free relative to the solves. At 30 fps the whole
pipeline gets ~33 ms *including* the HB solves, so the fit must be a small fraction, not an
afterthought. Measures, in descending order of value:

1. **Cache the RBF factorization across frames.** `Rbf2D` builds its kernel matrix from **node
   positions only** (`BuildKernelMatrix(_nodesRe, _nodesIm, …)`), LDLᵀ-factors it, and then solves
   with the node *values* as the right-hand side. During a termination drag **the grid positions do
   not move — only the metric values change.** So the O(n³) factorization is reusable and only the
   O(n²) back-solve re-runs per frame. Both Smith charts share one factorization (two right-hand
   sides against one factor), and so do power and efficiency on the same grid.
   *This needs a small `Rbf2D` API addition* — a factored-kernel object that can be re-solved with a
   new value vector (open item 7). Rebuild is required only when the **node set or the NaN mask**
   changes: a dragged grid point, or a point crossing in or out of the compression hole (§6.3). The
   NaN-drop in `Rbf2D`'s constructor is what makes the mask part of the cache key.
2. **Two raster resolutions.** Evaluate the surface on a coarse raster (96 × 96) during a drag, full
   (256 × 256) on release. Surface evaluation is O(raster × n) and dominates once n reaches the
   hundreds. `Rbf2D.Evaluate(spans)` is already allocation-free, so this is a sizing decision, not a
   rewrite.
3. **Freeze the level set during a drag.** Recomputing levels every frame makes contours jump and
   re-labels continuously. Hold the levels from drag-start; recompute on release. **This also freezes
   the per-level alpha ramp** (§7.2), which is now derived from the level set — without the freeze,
   contours would visibly pulse in opacity as the data moved under a fixed mouse position.
4. **No iso-line labels during a drag** — and they are **off by default** anyway (§7.2). Labelling and
   polyline simplification cost more than the marching-squares pass itself, so the default setting is
   also the fast one. When the user turns labels on, they are still suppressed for the duration of a
   drag and drawn on release.
5. **Run the whole pipeline on the solve thread.** `Rbf2D`, `LoadpullSurface`, and `ContourExtractor`
   are all RfCore and framework-free, so the UI thread receives finished polylines. Pool the raster
   and polyline buffers — per-frame allocation at 30 fps is a GC problem, not a math problem.
6. **The scheduler must time the fit separately from the solves.** §6.8 measures completion; if fit
   and solve are lumped together the scheduler cannot tell "the solver is slow" from "the fit is
   slow" and will degrade the wrong one. Degrading the raster is nearly free perceptually; degrading
   the grid loses information. Separate timers, separate fallbacks.

> **Measurement obligation (phase H3).** Report fit time and extract time *separately* from solve time
> at n = 37 / 61 / 200 grid points. If the fit still dominates at realistic n after (1) and (2), the
> named fallback is Delaunay / natural-neighbour interpolation — O(n log n) to build, no dense solve
> at all — chosen deliberately now rather than discovered late. `LoadpullSurface` already owns the
> scatter→surface step, so the swap is behind one interface.

### 6.5 Per-chart independence

Each Smith chart carries its own **plane** (load / source) and **harmonic** selector; the markers do
not move when the selection changes (owner-confirmed). A 2f₀ *load-plane* map means the grid sweeps
`Γ_L(2f₀)` with everything else pinned — so **each chart owns its own contour computation and its own
invalidation set**. Two charts showing the same plane and harmonic share one computation.

Invalidation rule, per chart: a chart's contours are invalidated by any change **except** a move of
the one marker whose plane and harmonic that chart is displaying (that marker is a cursor on the map,
not an input to it).

> **Not built as written, deliberately, and the reason is cost (H7, 2026-08-07).** The shipped solver
> builds **ONE** `ContourGrid` per frame and derives both metrics (power and efficiency) from it —
> which is why H4–H5's measured cost table shows a single grid solve per frame, and why the raster is
> the first thing §6.8 degrades. Two independently-swept charts would be two grids, i.e. **double the
> dominant term of a frame** (272 HB solves and ~0.5 s each on the shipping fixture). The plane and
> harmonic selectors therefore ship in the Display menu and are **document-wide**: both charts show
> the same sweep, in different metrics. Per-chart independence stays possible — nothing in
> `ContourGrid` prevents it — but it is a second grid's worth of cost and needs to be scheduled as
> such rather than assumed free.

### 6.6 The inverse solve — dragging an intrinsic glyph

**All marked harmonics are solved simultaneously** (owner-confirmed — the harmonics are coupled, and
that coupling is the phenomenon under study).

- **Unknowns:** Re/Im of every *marked* extrinsic termination (S1, L1, L2, L3, …). Unmarked bands stay
  pinned at 1e-6 Ω and their intrinsic values are free to drift.
- **Equations:** every marked band's intrinsic Γ equals its target — the dragged glyph supplying a new
  target, every other glyph its present value. Square system; 8 × 8 for four markers. **The two sides
  supply different residuals**: a load-side target is the §4.5.1 ratio; a source-side target is the
  §4.5.3 conversion-matrix diagonal. Both are functions of the converged state, so the outer Newton is
  unchanged — but see open item 8 on conditioning.
- **Operating point:** the **power-sweep cursor's Pin** (§7.4). Intrinsic impedance is drive-dependent,
  so the equation is only well-posed at a stated drive. The cursor is user-placeable and has a
  *snap to compression* mode, so "set the generator's load at compression" is expressible.
  A **Re-converge at compression** option runs an outer loop (re-find compression → re-solve, capped
  at N iterations) for when the small residual drift matters. Default off: it is ~10× the cost and
  ill-conditioned where the gain curve is flat.
- **Method:** full finite-difference Jacobian on drag-start (8 perturbation solves + residual
  ≈ 9 ms), then **rank-1 Broyden updates per frame** (1–2 solves ≈ 2 ms/frame), with an automatic FD
  refresh whenever the residual stops decreasing.

  > **Measured at H6, and one of the three numbers was wrong.** On Hero 2's device with an Rs/Ls/Rd
  > package, at four markers (8 × 8): FD at start **10.3 ms** (the note says 9 — good), per-frame
  > Broyden **2.45 ms** at exactly **2.00 solves/frame** (says 2 ms, 1–2 solves — good), and full FD
  > every frame **12.9 ms**, not the 30–40 ms this paragraph claims. The reason is that a per-frame
  > rebuild warm-starts from the previous frame's converged spectrum while the 10.3 ms figure is a
  > cold start. **Broyden is still right and still 5.2× cheaper, but it is not the difference between
  > 30 fps and not** — on this model the CONTOURS are, by two orders of magnitude. The stall-driven
  > FD refresh fired **0 times in 60 frames** of a curved drag, on either side.

- **The one candidate that is ill-posed rather than merely unusual (found at H6).** An out-of-circle
  extrinsic solution is allowed and flagged everywhere except the **fundamental source termination**:
  available power is `√(8·P_avl·Re Z_S(1))` and is undefined for `Re Z ≤ 0`, so the drive amplitude —
  and with it the whole stated-drive operating point this section rests on — collapses rather than
  becoming active. The solver refuses that candidate by name instead of solving against no drive.
- **Failure:** if the solve does not converge inside its iteration budget, **the glyph does not move**
  and the previous extrinsic set is retained. No partial application.
- **Reachability.** The map is not onto: with series Rd/Rs or a lossy embedding, whole regions of the
  intrinsic plane are unreachable from any extrinsic termination. Silent sticking is a bad
  experience, so the **reachable region is shaded** on the chart during an intrinsic drag (sampled
  coarsely and cached; refreshed on structural change).
- The extrinsic solution may land outside the unit circle. It is **allowed and flagged** (marker drawn
  with a hatched outline), not clamped — an active source termination is a legitimate thing to
  discover, and hiding it would mislead.

### 6.7 Concurrency

- One **solve thread pool** sized to `cores − 2`, matching repo convention.
- `HbEngine` + `TunerModel` are mutated in place and are therefore **not thread-safe**. Each worker
  owns its **own elaborated netlist and context** (elaboration is ~ms; contexts are pooled and rebuilt
  only on structural change).
- Every job is **cancellable and latest-wins**: a newer frame supersedes an in-flight one rather than
  queueing behind it. Without this, a fast drag builds an unbounded backlog and the UI lags further
  the faster you move — the classic failure mode for live-solve tools.
- The UI thread never solves. It renders the most recent completed result.

### 6.8 The frame scheduler and adaptive quality

A `FrameScheduler` in `src/Harmonica` measures actual completion times and adapts grid density to hold
the frame target. It is deterministic and testable headless (fed a synthetic clock).

| tier | target | contents |
|---|---|---|
| **A — always live** | ≥ 30 fps | loadline, power sweep, Fourier readouts, intrinsic glyphs, all scalar readouts |
| **B — adaptive** | best effort | contour maps on an invalidating drag |
| **C — cached** | on demand | DCIV family |

- **Tier A** is one Pin drive-up ≈ 20 ms. It is never degraded. If a model cannot hold 30 fps here,
  the tool says so in the status strip rather than silently stuttering.
- **Tier B** adapts grid density from measured frame time: a coarse ring set (3 rings × 12 spokes = 37
  points) while dragging, refined to the full user grid on release. If even the coarse grid misses
  budget, the scheduler falls back to **freeze-and-snap** — contours ghosted during the drag, computed
  once on release. This is the owner's "option (a) if not fast enough," reached automatically rather
  than by a setting.
- **Tier C — the DCIV is computed once and held across frames** (owner-confirmed). It depends only on
  the model, its parameters, and the bias sweep range — never on terminations — so it is recomputed
  only when one of those changes. This is a clean and valuable cache boundary.

---

## 7. The user interface

### 7.1 The default (locked) layout

```
┌───────────────────┬───────────────────┬──────────────────────┐
│ Smith — POWER     │ Smith — EFFICIENCY│ Rect — DCIV +        │
│ contours          │ contours          │ LOADLINE             │
│ (@ compression)   │ (@ compression,   │                      │
│                   │  or @ const Pout) │                      │
│                   │                   ├──────────────────────┤
├───────────────────┴───────────────────┤ Rect — POWER SWEEP   │
│  DENSE SETTINGS / READOUTS            │ Gain (L) ·           │
│  (spans both Smith charts)            │ Efficiency (R)       │
│                                       │ vs Pout              │
└───────────────────────────────────────┴──────────────────────┘
```

**The two Smith charts sit side by side** — power on the left, efficiency on the right — with the
dense settings/readout strip spanning beneath both. The right column holds the loadline plot above the
power-sweep plot, full height. The layout and all element positions are **locked by default**;
*Edit Display* (§7.7) unlocks them.

### 7.2 The Smith charts

- Contours: **unfilled iso-lines**. Chart 1 = power at compression. Chart 2 = efficiency at
  compression, with an option for **efficiency at constant Pout** (a user-typed dBm with a *Set from
  MXP* button; unreachable points become holes exactly as in §6.3).
- **Iso-line labels are optional and default OFF.** A dense unfilled contour set is more readable
  without them, and labelling is the most expensive part of the extract step (§6.4.1). A Display-menu
  toggle turns them on.
- **Iso-lines fade with their metric level, not with position.** Alpha is a function of **which level
  the contour is**, so the **highest-level iso-line is fully opaque wherever it lands on the Γ plane**
  and successively lower levels fade out. The top contour is the answer — the one bounding the region
  of best Pout or best efficiency — and the lower ones are context, so the ramp puts emphasis exactly
  where the design decision is made. Position on the chart is irrelevant to it.

  ```
  levels L₀ < L₁ < … < L_{n−1}          (L_{n−1} = the highest)
  α_i = α_floor + (1 − α_floor) · ( i / (n−1) ) ^ p          α_{n−1} = 1 exactly
  ```

  **Ranked, not value-proportional.** With evenly spaced levels (`ContourData.LevelStep`, the usual
  case) the two are identical; when levels are uneven or the metric has a long low tail, a
  value-proportional ramp crushes almost every contour to near-invisible, while the ranked form
  degrades gracefully. `α_floor` and the shaping exponent `p` are theme values (§7.9), not constants,
  so the fade can be made more aggressive or flattened to nothing.

  **Implementation is a flat alpha per polyline** — every vertex of one iso-line shares one level, so
  this is a single paint alpha per contour, with no shader, no per-vertex work, and no geometry-change
  cache to maintain. **Iso-line labels, when enabled, inherit their line's alpha**; a faded contour
  carrying a full-opacity label would misread as the important one.

  This assumes **higher is more interesting**, which holds for both shipped metrics (Pout and
  efficiency). A future lower-is-better metric would need the ramp direction inverted; that becomes a
  per-chart flag if and when such a metric appears, not now.
- Efficiency metric is **DE or PAE**, per-chart selector, **drain efficiency (DE) the default**.
- Per-chart **plane** (load/source) and **harmonic** selectors (§6.5).
- Markers per §4.2; intrinsic glyphs per §4.5, always beneath the markers.
- **Grid points are visible and draggable** (§6.4); thrown-out points render as hollow dots.
- **Marker context menu** (right-click): set as impedance (R + jX); set as gamma (polar mag∠° or
  rectangular re + j·im); normalize toggle; remove marker; snap to MXP / MXE; copy Γ; copy Z.
- **Menu-bar Markers menu**: add/remove harmonic markers on either side.

### 7.3 The loadline plot

DCIV family (Ids vs Vds over a Vgs family) with the time-domain loadline superimposed, live during
drag.

**Plane toggle** (owner-confirmed): the DCIV family and the loadline move together between
**intrinsic** and **extrinsic** — one toggle, not two, so the two curves are always in the same plane
and cannot be misleadingly superimposed. A **persistent subtle indicator** on the panel states which
plane is shown; it is never absent.

### 7.4 The power-sweep plot

Gain (`Gt` or `Gp`) on the left axis and efficiency on the right, against output power. Live.

- **X-axis unit is click-to-cycle** on the axis itself: Pout (dBm) → Pout (W) → Pin available (dBm) →
  Pin available (W).
- Carries the **operating-point cursor** — the Pin at which the intrinsic glyphs, the loadline, and
  the inverse solve (§6.6) are evaluated. Draggable, with a *snap to compression* mode.

### 7.5 The dense settings / readout strip

Deliberately **dense: small fonts, no section titles, no decoration** — the data is self-explanatory to
the intended user, and every element carries a **tooltip** for newcomers. All text is **selectable**
so any readout can be copied.

Inputs: bias (**Vgs** or **Idq** — Idq solves Vgs by 1-D secant on the DC solve at the stated Vds,
quiescent); Vds; frequency; compression level; **compute-charge toggle**; multiplicity `M`; plus
**every parameter the loaded model declares** (so periphery / finger count appear when — and only
when — the model actually has them, rather than being faked).

Readouts: Idq and the **dynamic mean Id under drive** (they differ, and the difference is worth
seeing); each marker's Γ and Z, extrinsic and intrinsic; Pout / Gain / DE / PAE at the cursor; the
**MXP and MXE summaries** from the grid — each carrying its **own `Zin`** (§4.5.4), since `Zin` moves
with the load on a non-unilateral device and one number would be a lie; convergence status and solve
rate; and the **Fourier coefficients of Ids and Vds** (magnitude and phase per harmonic, both planes).

### 7.6 Menus

harmonicaRF documents get their **own menu set** — notably **no Simulate menu**, since it is always
simulating.

- **File** — New, Open `.charm`, Save, Save As, **Set DUT…**, **Import `.gam`…**, **Export `.gam`…**,
  Export Data, Close.
- **Edit** — Undo, Redo, Copy Plot, Copy Readouts, Preferences.
- **Markers** — add/remove harmonic markers (source and load); Reset to defaults.
- **Display** — **Edit Display** (lock/unlock), plane and harmonic selectors, DE/PAE, intrinsic /
  extrinsic loadline plane, contour levels, **iso-line labels** (default off, §7.2).
- **Grid** — grid preset (rings × spokes), Reset grid, Import/Export `.gam`.
- **Help**.

circuitRF gains a **Tools** menu (it has none today) whose first entry is **harmonicaRF**.

### 7.7 Edit Display mode — v1

Unlocking reveals a component toolbar. In edit mode the user can **add, move, resize, and delete**
plots and readouts, and change text size and alignment. This is the `.cdd` placeable-plot canvas
(`PlotContainerViewModel`, `UndoRedo`) applied to harmonicaRF's own `DataSet` — not a second
implementation.

**The trace picker plots anything harmonicaRF solved** (owner-confirmed), over the §5 `DataSet`, using
the existing trace card and `CubeTraceSpecParser`. That is why publishing a real `DataSet` is a
requirement rather than a nicety.

> **Scoped 2026-08-07 (H7).** What is genuinely reused from `.cdd` is `CubeTraceSpecParser`, the
> slicing (`PlotInspectorViewModel.SetCubeDataFrom`) and the undo machinery (`UndoRedoManager` /
> `IUndoableCommand`). What is **not** is `PlotContainerViewModel`'s *placement* model: it positions a
> plot in canvas PIXELS against `DataDisplayViewModel`'s own zoom and pan, so adopting it would mean
> replacing `CharmLayout` — which R-h45-1 built in fractions precisely so H7 would not have to. Edit
> Display therefore writes `CharmPanelPlacement`, and a picked trace becomes an ordinary layout panel
> like the four in §7.1.

> **Measured 2026-08-07 (H7): `Zs_conv`'s off-diagonals are worth the panel.** On a package with a
> source lead (Rs = 0.8 Ω, Ls = 50 pH) the largest off-diagonal reaches **17.5% of the fundamental
> diagonal** (4.567 Ω against 26.056 Ω). On a bare device it is **2.8e-19** of it — round-off. The
> quantity is created by the input/output coupling element, exactly as §4.5.3(a) says.

### 7.8 Clipboard and interchange

- **Plots** copy via `PlotExporter.CopyPlotToClipboardAsync` — PDF, SVG, JSON, and bitmap flavours,
  exactly as `.cdd` already does. Not reinvented.
- **Readouts** are selectable text.
- **Γ grids** import and export as `.gam` via the existing `GamReader` / `GamWriter`, which also gives
  a route for hand-authored and measured grids and pairs naturally with draggable grid points.
- **Terminations → circuitRF**: *Copy termination set* emits a `Tuner` pair (`Z[1]`, `Z[2]`, …) ready
  to paste into a schematic, and *Export testbench* writes a runnable `.cnl` that reproduces the
  current harmonicaRF state through the ordinary loadpull/HB path. This makes every harmonicaRF
  finding checkable by the reference engine — which is also how we validate the tool (§9).

  > **Corrected 2026-08-07 (H7).** The two halves need DIFFERENT components, and the original wording
  > asked for a `Tuner` in both places. *Copy termination set* is a `Tuner` pair and is right: a Tuner
  > pasted into a schematic is driven by the **loadpull** engine, which assigns its role, its tone and
  > its drive. *Export testbench* is a `type=hb` netlist run by `Cli hb`, and **a `Tuner` is inert on
  > that path** — nothing in `HbEngine` calls `SetRole`, `SetTone` or `SetSourceDrive`, so a Tuner
  > pair would present `Z[1]` flat at every harmonic and emit no drive at all. It would run, converge,
  > and be wrong. The export therefore uses the two components `HbEngine` *does* give a harmonic-band
  > ruler to: a **`P1Tone`** for the source (available power plus per-band `Z[k]`) and a **`PnTone`
  > declaring no tones** for the load (zero drive phasor at every spectral line, per-band `Z[k]`
  > live). The DC block is written explicitly in series at each plane, because §6.2 folds it into the
  > termination admittance rather than the netlist. Measured agreement with the frame: **7.5e-12 dB**
  > on Pout for the shipped default document and **1.5e-7 dB** with a full package and three marked
  > bands per side.

---

### 7.9 Colour — harmonicaRF has its own theme

harmonicaRF is **deliberately visually distinct from the circuitRF Data Display.** It is a different
instrument and should not be mistaken for a `.cdd` at a glance.

#### 7.9.1 Built on the existing theming system, not beside it

circuitRF already has the full three-layer scheme from `docs/design/color-themes.md`, and harmonicaRF
uses it rather than inventing a second one:

- **Layer 1** — a block of new **`Harmonica.*` roles** appended to `ColorRole.All`
  (`src/Ui/Theming/ColorRole.cs`), with Light and Dark variants, same as `Schematic.*` and `Layout.*`.
- **Layer 2** — a new `HarmonicaRenderTheme` token struct projecting those roles into the `SKColor`s
  the harmonicaRF renderers draw with. Same pattern as `SchematicRenderTheme.FromTheme(...)`; no
  hardcoded statics as the source of truth.
- **Layer 3** — selection and persistence differ, deliberately: see §7.9.4.

Roles absent from a stored theme fall back to the built-in default, so an old `.charm` still opens
after new roles are added — the same nullable-defaulted forward-compatibility rule the rest of the
format uses.

#### 7.9.2 The dark theme — phosphor green

| role | dark default | notes |
|---|---|---|
| `Harmonica.Background` | `6 12 8` | near-black, faint green cast |
| `Harmonica.AxisLine` | `0 255 65` | phosphor green |
| `Harmonica.AxisText` | `0 255 65` | |
| `Harmonica.ReadoutText` | `0 255 65` | **all** text in the settings/readout strip |
| `Harmonica.GridLine` | `0 90 30` | dark green — deliberately low contrast |
| `Harmonica.SmithGrid` | `0 90 30` | the constant-R / constant-X arcs |
| `Harmonica.Isoline` | `0 255 65` | faded toward the rim per §7.2 |
| `Harmonica.IsolineLabel` | `0 255 65` | only drawn when labels are on |
| `Harmonica.GainTrace` | `0 255 65` | gain on the power-sweep plot |
| `Harmonica.DcivFamily` | `0 200 55` | the DC I–V curves |
| `Harmonica.Loadline` | `255 48 48` | **red** |
| `Harmonica.EfficiencyTrace` | `255 48 48` | **red** |
| `Harmonica.GridPoint` | `0 160 50` | the Γ sample dots |
| `Harmonica.GridPointDropped` | `120 120 120` | hollow, non-compressing (§6.3) |
| `Harmonica.OperatingCursor` | `0 255 65` | the power-sweep cursor |
| `Harmonica.ReachableRegion` | `0 255 65` α40 | intrinsic-drag shading (§6.6) |
| `Harmonica.EditChrome` | `0 255 65` | Edit Display handles and outlines |
| `Harmonica.MarkerBand1…5` | green / pastel red / pastel yellow / pastel blue / pastel purple | §4.2 defaults, unchanged |

**Green is the default for everything textual and structural; red is reserved.** Only the **loadline**
and the **efficiency trace** are red. That reservation is the point — red means "this is the quantity
you are engineering," and spending it anywhere else weakens it.

Marker band colours keep their §4.2 values. They are **roles** (so a user *can* change them) but their
defaults are exactly as specified — the five-colour cycle is a harmonic-identity convention, not a
theme choice, so it survives a theme switch untouched.

#### 7.9.3 The light theme — the same scheme, inverted

Not a recoloured dark theme: the same *structure* (green primary, red reserved, low-contrast grid)
re-derived for a light ground, with the greens and reds darkened enough to stay legible on white.

| role | light default |
|---|---|
| `Harmonica.Background` | `246 250 246` (near-white, faint green cast) |
| `Harmonica.AxisLine` / `AxisText` / `ReadoutText` | `0 110 40` (deep green) |
| `Harmonica.GridLine` / `SmithGrid` | `170 205 180` (pale green) |
| `Harmonica.Isoline` / `IsolineLabel` | `0 110 40` |
| `Harmonica.GainTrace` | `0 110 40` |
| `Harmonica.DcivFamily` | `40 140 70` |
| `Harmonica.Loadline` / `EfficiencyTrace` | `190 30 30` (**red**) |
| `Harmonica.GridPoint` | `60 150 90` |
| `Harmonica.GridPointDropped` | `150 150 150` |
| `Harmonica.OperatingCursor` | `0 110 40` |
| `Harmonica.ReachableRegion` | `0 110 40` α40 |
| `Harmonica.EditChrome` | `0 110 40` |
| `Harmonica.MarkerBand1…5` | as §4.2 |

The variant follows the app theme variant (`ActualThemeVariant`), exactly as the schematic canvas
already does.

#### 7.9.4 The colour editor, and where colours live

- **The editor reuses circuitRF's existing colour UI** rather than a new one:
  `Views/Dialogs/ColorPickerDialog` and the role-list + hex-field pattern from
  `Views/Dialogs/SettingsView`. That code has already absorbed two non-obvious fixes worth inheriting
  rather than rediscovering — the **hex-field key handling** (Return applies *and* sets
  `e.Handled = true`, or the dialog's default button closes the window instead; Escape reverts;
  LostFocus applies; `RRGGBBAA`, with a 6-digit entry taken as opaque), and the requirement that
  `ColorView` be given its Fluent theme or it instantiates blank.

  > **Gotcha the standalone binary will hit (§3.1).** The `ColorView` template comes from
  > `avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml` — note `.xaml`, **not**
  > `.axaml`; that is the actual embedded resource name in 12.0.3. It is in the main app's
  > `App.axaml` today. **The standalone harmonicaRF entry point has its own `Application.Styles` and
  > must carry the same include**, or the colour editor renders as an empty box with no error. Called
  > out here because it fails silently and only in the standalone build.

- **A colour change never invalidates physics.** Colours re-project `HarmonicaRenderTheme` and
  invalidate the canvas — no re-solve, and specifically **no contour-cache or RBF-factorization
  invalidation** (§6.4.1). Live preview is therefore free.
- **Colours persist in the `.charm`**, not in a separately-named `.ccolor`. This diverges from
  circuitRF's Layer 3 (which records a theme *name* in the `.cws` and resolves it against
  workspace → user → Assets) and the divergence is deliberate: harmonicaRF **runs with no workspace
  open and ships as a standalone app**, so a name-plus-search-path scheme has nothing to resolve
  against. The `.charm` therefore embeds the resolved `Harmonica.*` role map for **both variants**,
  matching §8.1's "a `.charm` is self-describing" rule.
- **`.ccolor` import/export is still offered** from Preferences, so a theme can be shared between
  `.charm` files or with the rest of circuitRF. Import writes the roles into the current document;
  export writes them out as an ordinary `.ccolor`.
- **Reset.** Preferences carries a **Reset all colours to defaults** button, plus a per-role revert
  (right-click a role → *Reset*), because after a session of experimenting the usual want is to undo
  one role, not all of them.
- The **iso-line fade parameters** (`α_floor` and the shaping exponent `p`, §7.2) live with the theme
  rather than as constants, so a user who dislikes the fade can flatten it (`α_floor = 1`) without a
  code change.

## 8. Persistence — the `.charm` file

JSON, versioned, following the `DataDisplayConfig` pattern (`FormatVersion`, forward-compatible reads).

**Stored:** the DUT reference or definition (§8.1); model parameter values and the intrinsic mapping;
the embedding stack (lumped values; Touchstone files referenced by **bare filename**, resolved
relative to the `.charm`, matching the `.cdd` portability rule R-dd-6); bias, frequency, compression
target, charge toggle, multiplicity; every marker (band, side, value); the Γ grid including any
user-dragged point positions; per-chart plane/harmonic/metric selections; the display layout
(locked or user-edited); the **`Harmonica.*` colour role map for both light and dark variants**
(§7.9.4) together with the iso-line fade parameters (`α_floor`, `p`) and the iso-line-label toggle;
and HB preferences (§8.2).

**Not stored: results.** The file is re-solved on open (owner-confirmed). At ~0.5 s for a full map
this is cheap, and it eliminates an entire class of stale-data bug.

### 8.1 DUT persistence — embed or reference

Owner-confirmed split:

- **SDD or built-in model** → **fully embedded**, including the equation text and every parameter
  value. A `.charm` is then self-contained and portable.
- **Verilog-A `.osdi` or PDK/vendor model** → **reference only** (path plus the identifying model
  name), because the artifact is a compiled binary or a licensed kit. Opening a `.charm` whose
  reference cannot be resolved reports which file is missing and offers to re-point it — it does not
  fail silently or substitute a different model.

### 8.2 Preferences

HB settings persist **with the `.charm`**, not globally: harmonic order (**default K = 5**),
`FFTOverSample`, `Tol`, `MaxIter`, `DriveStepping`, `GuardHarmonic`, `Lambda`, plus harmonicaRF's own
knobs (frame target, coarse-grid density, Broyden refresh policy, inverse-solve iteration cap).

> The **guard harmonic** deserves a callout: `harmonic-balance.md` §12.1 records that it is
> *necessary* for convergence under Class-F / F⁻¹ terminations — high-harmonic shorts and opens make
> those harmonics stiff. harmonicaRF's entire purpose is to let a user drag terminations into exactly
> those regions, so it will meet this constantly. The guard harmonic is surfaced in Preferences and
> the status strip reports when it engages.

---

## 9. Validation

harmonicaRF must not be a second, divergent answer to a question circuitRF already answers.

1. **Equivalence to the reference path.** For a set of frozen termination configurations, harmonicaRF's
   solve and a `.cnl` run through `LoadpullEngine` / `HbEngine` must agree on Pout, Gain, DE, PAE, and
   the intrinsic spectra to solver tolerance. The *Export testbench* feature (§7.8) is what makes this
   test cheap to write and cheap for a user to reproduce.
2. **Schur-complement equivalence** (§6.2) — the pre-terminated `Y_NN(Z)` matches direct extraction to
   ≤ 1e-12 relative across near-short / near-open / complex terminations. This gates the central
   optimization.
3. **Inverse-solve round trip** (§6.6) — solve for the extrinsic set that produces a target intrinsic
   Γ, apply it, re-measure the intrinsic Γ, and require it to match the target. Includes a
   deliberately unreachable target that must be refused, leaving state untouched.
4. **Conduction/displacement split** — with charge off and no extrinsic network, the **load** glyph ≡
   marker exactly. With charge on, they separate, and the separation matches an independent hand
   computation of `jωq` on a known Cds.
9. **Intrinsic source impedance** (§4.5.3) — two independent oracles, both required:
   (a) a linear-`gm`-plus-`Ls` fixture must return `(Zs + Z_Ls)/(1 − gm·Z_Ls)` to solver tolerance —
   a closed form derived independently of the Jacobian route, so this is a second formulation and not
   the solver agreeing with itself; (b) with `Ls = 0`, no external feedback and no embedding
   cross-coupling, `Z_S,intr(k)` must reduce **exactly** to the passive source network transformed to
   the intrinsic plane, at every `k`. A regression test also asserts that `Z_S,intr` is **not** equal
   to `V_g,k / I_g,k` on a fixture where they differ — pinning the §4.5.2 correction so it cannot
   silently regress, the same way the `Iin` fix is pinned.
10. **RBF factorization cache** (§6.4.1) — a re-solve against a cached factor produces bit-identical
   weights to a full rebuild, and the cache correctly invalidates when the NaN mask changes.
11. **Theme round trip and isolation** (§7.9) — a `.charm` save/reload restores every `Harmonica.*`
   role in both variants; a role omitted from the file resolves to its built-in default; *Reset all*
   restores exactly the §7.9.2/§7.9.3 tables. Separately, a **colour change must not invalidate the
   contour cache or the RBF factorization** — asserted directly, because the failure mode (colours
   silently triggering a re-solve) would be invisible except as a frame-rate collapse.
5. **Contour support mask** (§6.4) — a grid with a deliberate hole produces no iso-lines inside it.
6. **Frame scheduler** (§6.8) — driven by a synthetic clock, the adaptive tiers degrade in the
   specified order and never degrade tier A.
7. **`.charm` round trip** — save, reload, re-solve, and require the recomputed `DataSet` to match.
8. **H0 batching** (§2.2) — batched and unbatched external evaluation produce identical HB results;
   the batched path is measured and the speedup recorded.

Cost discipline: any test measured at or above ~5 s carries `[Trait("Category","Benchmark")]` per the
repo rule. The contour-timing and external-model tests are expected to land there; the correctness
tests must not.

---

## 10. Phasing

Each phase ends build+test green and is independently useful.

| phase | contents |
|---|---|
| **H0** | Batched external-device evaluation down the HB inner loop (§2.2). Its own brief; benefits the whole product, not just harmonicaRF. |
| **H1** | `src/Harmonica` skeleton: circuit model, `HarmonicaContext`, in-place mutation, the published `DataSet`, `.charm` I/O. Headless, fully tested, no UI. |
| **H2** | The pre-terminated interface network (§6.2) + its equivalence gate. The optimization that makes everything else live. |
| **H3** | Secant compression search, the contour grid, support mask, MXP/MXE. Still headless. |
| **H4** | The document shell, the locked default layout, the four panels, markers, glyphs. The `Harmonica.*` roles + `HarmonicaRenderTheme` and both variants (§7.9.1–7.9.3) — the panels need colours the day they exist, and retrofitting a theme onto hardcoded colours is the mistake `color-themes.md` was written to prevent. First interactive build. |
| **H5** | The frame scheduler, concurrency, cancellation, adaptive quality. |
| **H6** | The inverse solve (intrinsic glyph drag) with Broyden and the reachability shading. **Shipped 2026-08-07**, plus the pointer gesture H4–H5 left unbuilt. |
| **H7** | Edit Display mode, the trace picker, clipboard and `.gam` interchange, the circuitRF Tools menu, testbench export, the **colour editor** + `.ccolor` import/export + reset (§7.9.4). **Shipped 2026-08-07** (M1–M5), plus harmonicaRF's own §7.6 menu set and the §7.5 input half. |
| **H8** | Standalone binary entry point + build configuration. |

---

## 11. Open items

1. **`Gp` vs `Gt` for the compression criterion** — the batch engine exposes `GainType`. harmonicaRF
   should too, but the default matters for what the contours mean. Proposing **`Gt`** (matching
   `loadpull.md`'s default), owner to confirm.
2. **Contour level selection** — auto (N levels spanning the data) vs. user-set start/step/stop.
   `ContourData` supports both; proposing auto-with-override, defaulting to 10 levels.
3. **Coarse-grid density during drag** — 3 × 12 = 37 is a guess. Settle empirically at H5 against the
   measured frame budget.
4. ~~**Reachability shading cost** (§6.6) — sampling density unknown until H6; if it proves expensive it
   becomes opt-in rather than automatic.~~ **SETTLED at H6: AUTOMATIC.** The region is sampled as the
   image of the extrinsic boundary circle rather than a filled lattice — 24 solves, **52.4 ms**, paid
   **once per drag** and cached on `(StructuralKey, side, band, Pin)`. 24 samples are within 1% of 48's
   area for half the cost. `ShowReachableRegion` remains a property so a slow model can be told not to.
5. **Root `CLAUDE.md` correction** — the "native `FetModel` is a plan, not code" line is stale (§3.3)
   and should be fixed when this lands.
6. ~~**`.charm` in a workspace**~~ — **SETTLED at H8: IT APPEARS.** `WorkspaceScanner` classifies
   `.charm` as `NodeKind.HarmonicaFile`; the node is openable, reveals, and rides the **Data Displays**
   filter rather than earning a seventh checkbox (a `.charm` is a results-facing document beside a
   `.cdd`). A workspace file that is not in the tree is a file the user has no way to reopen, which is
   the whole argument. It is still not *required* to live in a workspace — the standalone binary opens
   one from anywhere, and a `.charm` outside the workspace root is simply not scanned.
7. **`Rbf2D` factored-kernel API** (§6.4.1) — a small, additive change to an RfCore type on the
   critical path of the existing loadpull contour display. Additive only (the current constructor
   keeps working), but it touches shared code, so it wants its own gate test before harmonicaRF
   depends on it.
8. ~~**Source-glyph drag under the §4.5.3 definition**~~ — **SETTLED at H6: NO separate cadence is
   needed.** Over a 60-frame *curved* drag (a straight one never asks the Jacobian to turn, which is
   the case Broyden handles best), the source side converged 60/60 at 2.00 solves/frame with the
   stall-driven refresh firing **zero** times — identical to the load side, at 2.15 ms/frame against
   1.43. Forcing a refresh every 8 frames cost +14% wall and +11% solves for no measurable gain.
   `InverseSolveOptions.SourceFdRefreshEveryFrames` exists and defaults to 0, kept only because a
   device with stronger feedback might yet need it.
9. **Marker legibility against the phosphor theme** (§7.9.2) — the §4.2 band colours were chosen
   against a conventional plot background, and pastel yellow on near-black is the one likely to read
   poorly. Proposing: keep the hues exactly as specified (they are a harmonic-identity convention) and
   give markers a theme-coloured outline plus a slight fill-darkening on the dark variant if it proves
   necessary. Judge on screen at H4, not on paper.
10. **Whether `Harmonica.*` roles belong in the shared `ColorRole.All`** or in a harmonicaRF-local role
   set. Shared is proposed (one vocabulary, one editor, `.ccolor` interchange works), but it means the
   circuitRF Settings dialog will list roles for a tool the user may never open. If that proves
   cluttered, the fix is role *grouping* in the editor, not a second role system.
