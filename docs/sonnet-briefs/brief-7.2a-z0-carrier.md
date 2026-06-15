# Sonnet Brief — Phase 7.2a: per-port reference-impedance `Z0` cube (carrier + producer fix)

**Design:** `docs/design/data-display.md` §7.2 "Design (RESOLVED)"; contract `src/Core/Data/CLAUDE.md` →
"Planned addition (Phase 7.2)". **This brief is HEADLESS — RfCore + Engine + tests only. No UI.** It is the
foundation for the 7.2 Data Display work (data-source library, dual-source trace, indicator) and **also fixes a
live correctness bug**. The UI pieces (data-source library, the non-uniform indicator, full non-uniform
Z0-dependent compute) are *separate later briefs* — do not touch UI here.

## The bug + the model
`SParameterEngine.Run` already computes S against **per-port complex Z0** — each Term/Port carries its own
(complex-capable) `Z`, collected into `z0PerPort`, and the S-matrix is built via
`RFNetwork.YToS(yMat, z0PerPort)`. **But it then discards the per-port info:** it sets
`refZ0 = z0PerPort[0]`, builds a uniform `SNP`, and `DataSetBuilder.FromSnp` stores **no** reference impedance
at all (and `ToSnp` later fabricates 50 Ω). So a user who sets non-uniform/complex Term `Z` today gets correct
S **values** but a silently mis-recorded reference. Fix: carry per-port Z0 as a **`Z0` complex cube** in the
S-parameter DataSet (single honest source of truth; no second renormalized cube). The math layer
(`RFNetwork.SToS`/`SToZ`/`SToY` `Complex[]` overloads) is already per-port-ready — **do not add RF math here.**

## Deliverables

### 1. `DataSetBuilder` — emit + read the `Z0` cube (`RfCore/src/Data/DataSet.cs`)
- **`Z0` cube shape (convention):** name `"Z0"`, `DataKind.Complex`, **one axis** `Axis("port", [1..n], "port")`
  (1-based port numbers, matching the `i`/`j` axis convention in `FromSnp`); values are the per-port complex
  reference impedances in **port order** (`Z0.Values[k]` = port `k+1`). Values are complex ohms by convention
  (the cube carries no separate value-unit slot — same as `S` being unitless).
- Add a small builder helper, e.g. `public static DataCube BuildZ0Cube(Complex[] z0PerPort)` → builds the
  one-axis complex cube above. (Use the existing `DataCube(Axis[], Complex[])` ctor.)
- **`FromSnp(SNP snp)` now also emits a uniform `Z0` cube** — `BuildZ0Cube` with all entries = `snp.Z0`, length
  = `snp.Ports`. Result: **every** S DataSet (Touchstone-derived included) has a `Z0` cube, so consumers can
  rely on its presence. (Touchstone is uniform by definition — correct.)
- **`ToSnp(DataSet ds)` reads the `Z0` cube** instead of hardcoding 50 Ω:
  - Z0 cube present & **uniform** (all entries equal within tolerance) → `SNP.Z0 = that value`.
  - Z0 cube present & **non-uniform** → `SNP` cannot represent it (uniform-only by design); use
    `Z0.Values[0]` (port 1) to preserve today's behavior, and emit a `RFNetwork.Warn(...)`-style note that
    the non-uniform reference was flattened for SNP/Touchstone. (Faithful non-uniform handling is the later
    cube-direct follow-on, not here.)
  - Z0 cube **absent** (legacy `.npy`) → fall back to `new Complex(50,0)` (current behavior).
- **Classification helper (headless, for the later UI indicator):** add
  `public enum Z0Kind { UniformReal, UniformComplex, NonUniform }` and
  `public static Z0Kind ClassifyZ0(DataCube z0Cube)` (uniform-real / uniform-complex / non-uniform; uniform =
  all entries equal within tolerance, real = all imag ≈ 0). Put it where the UI brief can call it from RfCore.
  This keeps the indicator's "is this non-uniform or complex?" decision headless and testable.
- **Locate the `port` axis by NAME, not position** (in `BuildZ0Cube` consumers, `ClassifyZ0`, and `ToSnp`'s
  read) — see "Sweep interaction" below: under a sweep the `Z0` cube gains leading axes, so the `port` axis is
  not necessarily axis 0. `ClassifyZ0` and `ToSnp`'s Z0 read are defined on a **rank-1 `{port}`** cube (a single
  sweep point); if handed a higher-rank `Z0` cube, throw a clear error ("slice Z0 to a single sweep point
  first") rather than silently misreading axis 0.

### 2. `SParameterEngine.Run` — emit the true per-port `Z0` (`src/Engine/SParameterEngine.cs`)
The S **values** are already correct; only the carrier is missing. After building the DataSet, **overwrite** the
uniform Z0 cube `FromSnp` produced with the real per-port one:
```csharp
var snp = new SNP(freqsHz, sMatrices, MatrixType.S, MatrixFormat.RI, refZ0); // refZ0 stays nominal (port-1)
var ds  = DataSetBuilder.FromSnp(snp);            // S cube + uniform Z0 (placeholder)
ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort)); // overwrite with per-port truth (Add is last-write-wins)
return ds;
```
`z0PerPort` is already in **port-number order** (built after `ports.Sort` by PortNum), so it maps straight to the
1-based `port` axis. (`DataSet.Add` overwrites by key — `_cubes[name] = cube` — so this replaces the placeholder.)
The `refZ0` local can stay as the SNP's nominal field (`FromSnp` ignores `snp.Z0` for the S cube values).

## Sweep interaction (design-correct NOW; not testable until a sweep producer exists)
**Why this matters (owner question):** when S-params are run under a parametric sweep (e.g. 2 bias points),
`ParametricSweepEngine.StackSweepAxis` prepends the sweep axis to **every** cube in each point's DataSet. So
`S` becomes `S{vbias, freq, i, j}` (intended) **and** `Z0` becomes `Z0{vbias, port}` — even though the reference
impedance usually did **not** change between points (the user swept a bias, not a termination). This is the
correct, consistent behavior and we keep it (**option A: let it stack, slice it back**), because Z0 genuinely
*can* vary per sweep point (someone could sweep a Term `Z`), and special-casing `Z0` out of the generic stack
would be a fragile carve-out. Cost is trivial (a few complex numbers × N points).

**Contract the consumer relies on:**
- The `Z0` cube's canonical shape is `{port}`. Under a sweep it is `{…sweep…, port}`, exactly mirroring how `S`
  is `{…sweep…, freq, i, j}`.
- Any consumer needing the per-port vector operates on a **single sweep point**: the dual-source S-trace pins
  its sweep indices to locate its `S` slice, then applies the **same pins** to `Z0` to recover `{port}`.
  (`ToSnp` already implicitly assumes an un-swept `S{freq,i,j}` DataSet — same single-point assumption; the
  sweep-pinning happens upstream in the trace, not inside `ToSnp`/`ClassifyZ0`.)
- Therefore `ClassifyZ0` / `ToSnp`'s Z0 read take a **rank-1 `{port}`** cube and locate the `port` axis by name.

**Two distinct cases (do not conflate — recorded in `data-display.md` §7.2/§7.3):**
1. *Z0 as a consequence of an unrelated sweep* (the bias example): Z0 didn't vary; it just rides the sweep axis.
   The user plots `S21 vs freq` and pins the sweep — handled entirely by **this carrier's slice-with-pins**, no
   dialog needed. **This is the owner's example.**
2. *Z0 as an intentional sweep variable* (sweep a port's `Z`, plot `S11 vs Z0`): Z0 becomes an X-axis or a
   family — handled generically by **7.3 axis-role assignment** (Z0 is just another named axis by then). No
   special work.

**Scope for THIS brief:** make the helpers shape-correct (port-axis-by-name + rank-1 contract + clear error on
higher rank). **Do not** build sweep plumbing or a dialog. **No swept-S-param test yet** —
`ParametricSweepEngine.RunInner` currently dispatches only HB + nested sweeps (not S-param), so a swept-S DataSet
cannot be produced today. (DC and HB sweeps are also coming — e.g. FET curve-tracer Vgs×Vds families — but those
DataSets have no `S`/`Z0` cube, so they don't touch this carrier; they're 7.3 axis-role concerns.)

## Out of scope (explicitly — later briefs)
- Any UI: data-source library, the non-uniform/complex indicator badge, the Messages-pane warning.
- Faithful non-uniform renorm / S→Y/Z / stability from a non-uniform source (the cube-direct compute path).
- Touching `RFNetwork` math (already per-port-ready).
- `FormatVersion` bump: **not needed** — the `.npy` format is self-describing via `__meta__`; a new cube is a
  content change, not a layout change. (Alpha permits breaks anyway, but none is required.)

## Notes / non-breakage
- `NpyWriter`/`DataSetImporter` are **generic** over `ds.Cubes` — the `Z0` complex cube round-trips with **no
  exporter/importer change**. The per-run `.npy` (7.0) writes `RunResult.DataSets` through this exporter, so the
  live `.npy` path carries `Z0` automatically.
- **Lockstep:** splotRF's importer reads all cubes generically, so it loads `Z0` without change; the splotRF
  *consumer* (indicator/renorm) is a separate lockstep item, not this brief.

## Gate (headless tests in `RfCore.Tests` / `CircuitRF.Engine.Tests`)
1. `FromSnp` on a uniform 50 Ω SNP → DataSet contains a `Z0` cube, axis `port` = [1..n], all entries `50+0j`.
2. `SParameterEngine.Run` on a testbench with **non-uniform / complex** Term `Z` (e.g. port 1 = 50, port 2 =
   75−j10) → result DataSet's `Z0` cube has exactly those per-port complex values in port order; the `S` cube is
   unchanged from before this brief (S values were already correct — assert no regression via existing S-param
   tests).
3. **`.npy` round-trip:** export a DataSet with `S` + per-port-complex `Z0` → import → `Z0` cube survives
   (values + `port` axis) bit-for-bit (complex tolerance 0).
4. `ToSnp`: uniform `Z0` cube → `SNP.Z0` = that value; absent `Z0` cube → `SNP.Z0` = 50 Ω; non-uniform `Z0`
   cube → `SNP.Z0` = port-1 value (+ a warning fired).
5. `ClassifyZ0`: uniform-real, uniform-complex, and non-uniform inputs each classify correctly.
6. **Shape-robustness (cheap, no sweep producer needed):** `ClassifyZ0` / `ToSnp`'s Z0 read locate the `port`
   axis by name on a rank-1 cube; handed a synthetic higher-rank `Z0{sweep,port}` cube, they throw a clear
   "slice to a single sweep point first" error rather than misreading axis 0. (Full swept-S round-trip is
   deferred until an S-param sweep producer exists.)
7. Full suite stays green (no FormatVersion bump; existing importer/exporter/S-param tests unaffected).

## On completion
Update `src/Core/Data/CLAUDE.md` — move the "Planned addition" note to a shipped statement of the `Z0` cube
convention (name, `port` axis, complex ohms, present on every S DataSet, `ToSnp` flatten rule). Tick the carrier
in `data-display.md` §7.2 status. Next 7.2 brief: the **data-source library** (`file → DataSet`, generalizing
`SnpLibrary`).
