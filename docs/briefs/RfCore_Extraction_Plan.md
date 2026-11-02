# RfCore Extraction Plan (for Claude Code / Sonnet)

**Status:** Draft for review · **Date:** 2026-05-30
**Why:** circuitRF's Phase-2 S-parameter path depends on RfCore (the `SNP`/`RFNetwork` types,
Touchstone I/O, power-wave renormalization, and the `DataSet`/`DataCube` result model). RfCore does
not exist yet — its math lives inside **splotRF** and must be extracted into a standalone sibling
library. This plan covers that extraction. It is a **prerequisite** to the Phase-2 engine work, not
parallel to it.

> Reads with: circuitRF `docs/design/data-model.md` §6 (RfCore touchpoints), `src/Core/Data/CLAUDE.md`
> (the DataSet/DataCube contract), root `CLAUDE.md` (RfCore is an external sibling `ProjectReference`).

---

## The two kinds of work (different risk profiles)

This extraction fuses two different jobs. Keep them mentally separate — they need different safety nets.

- **LIFT (surgery on working, untested code):** move `SNP`, `RFNetwork`, `TouchstoneIO`, and the pure
  half of `Misc.cs` out of splotRF into a new `RfCore` project. The code works today; the risk is
  **silently breaking splotRF**, which has **no automated test suite**. This is the dangerous part.
- **AUTHOR (greenfield):** write `DataSet` and `DataCube` fresh, to the contract in
  `src/Core/Data/CLAUDE.md`. splotRF never had these (they postdate it). Ordinary greenfield risk.

---

## Naming (decided)

- Keep the existing type names: **`SNP`** (the N-port parameter container) and **`RFNetwork`** (the
  static math library). Do **not** rename to "Network" — it collides with computer-networking, and
  renaming during a no-test-net move multiplies the chance of a subtle break. circuitRF will adopt
  the `SNP`/`RFNetwork` terminology in its own docs.
- Namespace changes from `splotRF` to `RfCore` on the moved files (this is the one unavoidable churn;
  see "splotRF-wide ripple" below).

---

## Safety net FIRST — characterization tests (hard gate)

splotRF has **no tests**, and `RFNetwork.cs` is the math Hero 1's `< 1e-6` accuracy depends on. Moving
it blind means a subtle regression (a changed `using`, a static-state assumption, a NumFlat version
mismatch) would not surface until Hero 1 mysteriously misses tolerance — with two possible suspects
(the moved RfCore math vs. the new MNA engine) and no way to tell them apart.

**Therefore, before moving any code:** capture `RFNetwork`'s current numerical behavior as RfCore unit
tests.
- Load known Touchstone files; run each conversion (`SToZ`, `ZToS`, `SToY`, `YToS`, `ZToY`, `YToZ`),
  the renormalization (`SToS` with real *and* complex per-port Z₀), and the round-trips
  (S→Z→S, S→Y→S must return the original within tolerance).
- `RFNetwork.CompareRMS` already exists and is the ready-made comparison primitive — use it (or assert
  on its components) to pin outputs to golden values.
- Include a complex-Z₀ renormalization case (the power-wave path) and a 2-port round-trip through the
  T-matrix (`SToT2Port`/`TToS2Port`).
This converts a scary untested refactor into a verifiable one, and leaves RfCore with the test suite
splotRF never had — which Hero 1 then leans on as *verified*, not assumed.

**Gate:** these tests must exist and pass against the *original* code before the lift proceeds.

---

## What moves, what stays, what splits

**Move to RfCore (pure, mostly verbatim — only the namespace changes):**
- `SNP.cs` — the N-port S/Z/Y container (frequencies, `Mat<Complex>[]`, complex `Z0`, Touchstone
  metadata, comments). Already pure (`System.Numerics` + NumFlat).
- `RFNetwork.cs` — the full static math library: generalized N-port conversions with complex per-port
  Z₀; the direct power-wave renormalization (`SToS`, Kurokawa); parallelized SNP-sweep overloads;
  T-matrix; de-embedding; stability; `CompareRMS`; `FormatComplex`.
- `TouchstoneIO.cs` — Touchstone 1.1 reader/writer.

**Split `Misc.cs` (it straddles the boundary — do NOT move it whole):**
- → RfCore: `RfHelpers` (`Z2G`, `G2Z`, `VswrFromGamma`, `VswrFromZ`, `ComplexToString`,
  `RoundNearest`, `Nicenum`, `RoundTick`). These are pure math/formatting. `ComplexToString` is
  borderline-presentation but is harmless pure code and useful for debugging RfCore — it rides along.
- → STAYS in splotRF: `TraceProperties`, `MarkerType`, `LineType`, `PlotDetail`, `PrecisionFormat`
  and anything touching `Avalonia.Media` / `Color`. **Moving these would drag Avalonia into RfCore**,
  violating the cross-platform-pure-core rule (root `CLAUDE.md`). This is the single easiest mistake
  to make in the whole extraction — watch it.

**Author fresh in RfCore (greenfield):**
- `DataCube` — single-`DataKind` (Real/Complex) labeled N-D array with named axes, the accessor API
  (`ds.S(i,j)`, `ds.V(name, slice)`), slice semantics (int pins/collapses, end-exclusive Range keeps),
  and the element-wise transforms (`.real/.imag/.mag/.phase/.dB10/.dB20/.conj`) + reductions. Full
  contract in `src/Core/Data/CLAUDE.md`. Build to that spec exactly.
- `DataSet` — the named-cube container a run returns; holds many `DataCube`s of mixed kind.
- **Construct-from-computed-matrix factory** (small — mostly already present): the existing
  `SNP(double[] freqs, Mat<Complex>[] matrices, type, format, z0)` constructor IS the entry point the
  HB engine needs ("build an SNP from a Y/Z matrix computed on a frequency grid"). Add a thin
  convenience that takes a computed **Y** sweep and returns an **S** `SNP` (loop + `YToS`), so the
  linear engine's "extract port Y → wrap as SNP" path is one call. Document the existing constructor
  as the canonical computed-data entry point.
- interpolation routines - must interpolate a stored network to an arbitrary analysis
frequency
---

## splotRF-wide ripple (the part that touches more than the moved files)

After the lift, splotRF's *own* code that references `splotRF.SNP` / `splotRF.RFNetwork` /
`splotRF.RfHelpers` (the ViewModels, `Trace.cs`, `Plot.cs`, the renderers, etc.) must update its
namespace references to `RfCore.*`. This is a mechanical find-replace but it is **splotRF-wide**, not
confined to the four moved files. Moving the files and leaving the references stale will leave splotRF
not compiling. Plan for it.

---

## Git strategy

- **RfCore = its own NEW git repo.** It is a sibling project (cloned side-by-side with circuitRF and
  splotRF) and needs its own history. New `.csproj`, target `net10`, NumFlat pinned to splotRF's
  version (**1.3.0**), `CommunityToolkit.MVVM` NOT included (RfCore is pure, no UI/MVVM).
- **splotRF: work on a branch** (`extract-rfcore`). On that branch, in order:
  1. Write characterization tests (against original code) — gate.
  2. Stand up the RfCore repo/project; move the files; split `Misc.cs`; rename namespaces.
  3. Repoint splotRF at RfCore via `ProjectReference`; fix the splotRF-wide `using` ripple.
  4. **Manual validation (owner):** load Touchstone files, plot on Smith/polar/rectangular, exercise
     conversions/renormalization/de-embedding — splotRF's only regression net. (Owner does this, not
     Sonnet.)
  5. Merge the branch only once splotRF is confirmed working against extracted RfCore.
- circuitRF does not change during the extraction; it gains the RfCore `ProjectReference` when the
  Phase-2 engine work begins.

---

## Executor & roles

- **Sonnet (Claude Code)** executes the lift and the greenfield authoring, using the characterization
  tests as the behavior-preserving contract it can check itself against.
- **Owner** writes/approves the characterization-test golden values and performs the **manual splotRF
  validation** (step 4) — the one thing with no automated net that only a human can eyeball (is the
  Smith chart right?).
- Flag back to Opus/Chat (design) anything where the extraction reveals a design question rather than
  a mechanical move.

---

## RfCore public surface (what circuitRF depends on — per data-model §6)

The extraction must leave these public on RfCore:
- `SNP` — construct (incl. from a computed `Mat<Complex>[]` on a frequency grid), index by frequency,
  read `Z0`/`Type`/`Frequencies`/`Matrices`/`Ports`.
- `RFNetwork` — the conversions, `SToS` renormalization (per-port complex Z₀, power-wave), `YToS`/
  `ZToS` (the linear engine extracts port Y/Z then converts), T-matrix, interpolation over frequency
  (confirm/locate — if interpolation isn't in the files seen, it must be added or located).
- Touchstone read/write.
- `DataSet` / `DataCube` (newly authored).
- `RfHelpers` (Z2G/G2Z/VSWR/formatting).

**Author fresh in RfCore — frequency interpolation (net-new; splotRF has none):**
circuitRF's `TouchstoneModel` must interpolate a stored network to an arbitrary analysis
frequency (Hero 1's embedded SNP block needs this on the critical path). splotRF never had
interpolation, so this is authored fresh as a pure `SNP`-level operation in RfCore.

- **Method:** cubic spline (per real & imag component) as the **default**; **linear** as a
  selectable fallback. (Makima / modified-Akima is a noted FUTURE option; rational / vector-fitting
  is explicitly deferred.)
- **Interpolation format is user-directable** — the representation the interpolation runs in is a
  choice, because the same network interpolated in different bases gives different between-point
  results:
  - parameter type: **S / Z / Y** (interpolating in a non-stored type converts first via
    `RFNetwork` — e.g. S→Z, interpolate Z, convert back; this needs the network's Z₀ and composes
    on top of the extracted conversion math),
  - complex format: **real/imag** or **mag/phase**.
  - **Hard requirement:** mag/phase interpolation MUST phase-unwrap across frequency points, or it
    produces garbage at every ±180° wrap. Not optional.
  - **v1 scoping:** the **S / real-imag** path is the fully-working, fully-tested default (Hero 1
    exercises it). The Z/Y and mag/phase format options are wired but may be lightly tested in v1.
- **Out-of-range behavior:** emit an **obvious warning** to the user (NOT an error). The user sets
  the policy: **clamp** (hold the endpoint value) or **extrapolate**. When extrapolating, the
  warning is sterner (extrapolated S-parameters are routinely non-physical), and extrapolation
  should use **linear** end-segment behavior even when the interior method is spline, so a spline
  doesn't diverge to nonsense just past the last point.
- **Characterization test (clean):** interpolating a network *at its own stored frequency points*
  must return those exact points (interpolant passes through the data). Plus: a known analytic
  network (e.g. an ideal delay line, whose phase is linear in frequency) interpolated mid-point
  matches the closed-form value within tolerance.

---

## Acceptance

1. RfCore builds standalone (no Avalonia/SkiaSharp/MVVM dependencies).
2. Characterization tests pass in RfCore (conversions, renormalization incl. complex Z₀, round-trips).
3. `DataSet`/`DataCube` implemented to `src/Core/Data/CLAUDE.md`, with their own unit tests
   (accessor, slice semantics, transforms, DataKind).
4. splotRF builds against RfCore via `ProjectReference` and is **manually confirmed** to still plot
   and convert correctly (owner).
5. Frequency interpolation present and tested in RfCore.

*After RfCore lands and splotRF still works, the Phase-2 engine brief follows: MNA assembly, DC,
S-parameter extraction (Y-default), and Hero 1 + Hero 1B, consuming RfCore.*