# Result model (DataSet / DataCube) — local conventions

Standing instructions for `src/Core/Data`. Read with the root `CLAUDE.md`. The result model is the
integration seam with splotRF — treat its contract as load-bearing.

## What it is — two levels
- **`DataCube`** — the storage primitive and the unit splotRF plots: a labeled, unit-bearing,
  N-dimensional array with named axes and a single **`DataKind`** (`Complex` or `Real`).
  One array, one element type. Backed by a flat buffer (`Complex[]` or `double[]`) with strides;
  supports slicing and reduction along named axes.
- **`DataSet`** — the container a run returns: many `DataCube`s keyed by name, coexisting regardless
  of kind. An HB-with-measurements run returns one DataSet holding complex `"V"`/`"I"` spectra
  *and* the real `"PAE"`/`"Gain"` measurements together.

- An **S-parameter result** is the cube `S` → `{freq, i, j}`, where **`i` and `j` are the
  user-assigned port numbers** (`i` = receiving/response port, `j` = driving/stimulus port, exactly
  as in `S(i,j)`). They are **port numbers, never net names** — a user asks for `S(2,1)` and neither
  knows nor cares about internal nets. Same convention for `Y`/`Z` cubes (`param(i,j)`). Identical
  in shape to what Touchstone/splotRF already represent — this is *why* the cubes are the splotRF seam.
- A **1-D slice of a cube is a plot trace.** See "Accessing data" below for the slice notation.
- Conceptually: "xarray for RF." Axes are first-class, with names and units, not bare integers.

## Accessing data
Two layers: convenience accessors that speak the user's language, and a positional escape hatch.

**Network parameters — by port number, the way RF engineers think:**
```
ds.S(2, 1)            // the S21 trace over frequency (a 1-D complex trace)
ds.S(2, 1).dB20()     // |S21| in dB  (20·log10 — the conventional S-parameter dB; see transforms)
ds.Y(1, 2) / ds.Z(...) // same i/j port-number convention
```
`ds.S(2,1)` resolves internally to the `S` cube at `i=2, j=1`, traced over `freq`. The user never
spells out axis plumbing for the most common extraction in the tool. This matches `S(2,1)` in
`measurements.md`, so the accessor and the measurement language agree.

**HB node voltages — by node *name* + positional slice of the remaining axes:**
The `V` cube's axis set follows **what the analysis actually produced** — axes are NOT a fixed template:
- **Single-tone, with sweep:** `V` = `{node, harmonic, Pin}` (3 axes)
- **Single-tone, no sweep:** `V` = `{node, harmonic}` (2 axes — no Pin axis)
- **Two-tone, with sweep:** `V` = `{node, mixIndex, Pin}` (3 axes; mixIndex enumerates `(k1,k2)` pairs)

The node is addressed by its **user-defined name** (resolved against the elaborated node map, like
`V(X1.drain)` in measurements); the bracket then indexes the *remaining* axes positionally.
**Every bracket slot is an axis INDEX, never a physical value** — `1` is harmonic *index* 1, `0` is
Pin *index* 0 (the first sweep point), not 0 W:
```
ds.V("X1.drain", 1, ..)      // harmonic=1 (fundamental), all Pin   -> 1-D trace vs Pin
ds.V("X1.drain", 1, All)     // same; `All` is an alias for the `..` range
ds.V("X1.drain", .., 0)      // all harmonics at Pin index 0         -> the spectrum at that node
ds.V("X1.drain", 1, 2..4)    // harmonic=1, Pin indices 2,3 (end-exclusive) -> length-2 trace
ds.V("X1.drain", 1, 3)       // harmonic=1, single Pin index 3        -> a single complex value
```
Harmonic is addressed **by index**: `0`=DC, `1`=fundamental, `2`=2nd, …

**HB branch currents — by named branch, never by node/net:**
Current is a **branch** property. The `I:instancePath:terminal` cube in the DataSet is the ONLY
public current path. Node/net-indexed current (`ds["INl"][nodeIdx, …]`) is an internal diagnostic,
not a measurement accessor. Node-indexed current (`INl`) is stored internally but filtered from the
trace picker — cubes whose axis set contains `"node"` and whose name is `"I"` or `"INl"` are skipped
by `TraceRowViewModel.RebuildSignals`; only the `I:<path>:<term>` branch cubes (no `node` axis) are offered. Named-branch cubes:
- **Single-tone, with sweep:** `I:M1:d` = `{harmonic, Pin}`  (`ds["I:M1:d"][1, si]` = fundamental)
- **Single-tone, no sweep:** `I:M1:d` = `{harmonic}`         (`ds["I:M1:d"][1]` = fundamental)
- **Two-tone, with sweep:** `I:M1:d` = `{mixIndex, Pin}`     (`ds["I:M1:d"][m, si]` = mix m)

```
ds.I("X1.M1:d", 1, ..)       // drain fundamental (k=1) over all Pin  -> 1-D trace vs Pin
ds.I("X1.M1:d", .., 0)       // all harmonics at Pin index 0           -> spectrum
ds.I("IP1", 1, 3)             // IProbe named IP1, fundamental, Pin=3   -> scalar
```
`ds.I("X1.M1:d", …)` resolves against the `I:X1.M1:d` cube; the instance path and terminal name
come from the component's elaborated instance path and `ComponentModel.TerminalNames`. In
`measurements.md` the surface form is `I(X1.M1:d)` or `I(IProbe1)` for an explicit probe.

*(To select by physical value rather than index — e.g. "Pin = 0.01 W" — resolve the value to an
index against the axis first; a value-based lookup helper may be added later, but the slice API
itself is index-only to keep `3` from meaning both "index 3" and "3 W".)*

**Slice-argument semantics (matches NumPy / Python, and C# `Range`):**
- **Every slot is an axis INDEX, never a physical value** (see above). Resolve a value to an index
  before slicing if needed.
- A single **`int` pins and *removes* (collapses)** that axis: `ds.V("X1.drain", 1, 3)` drops `Pin`.
- A **`Range` keeps** that axis, possibly narrowed: `..`/`All` = whole axis; `2..4` = a sub-range.
- **Ranges are END-EXCLUSIVE.** `2..4` is indices 2,3 (4 not included); `2..3` is index 2 only.
  > **Note — indexing convention.** End-exclusive ranges conform to **NumPy and C#** (`a[2:4]` /
  > `a[2..4]` = indices 2,3). They do **not** match **MATLAB**, where `2:3` is inclusive (indices
  > 2 *and* 3). This is the user-facing slice API; it is deliberately C#/NumPy-native. (The HB
  > internals are transcribed from 1-based *inclusive* MATLAB pseudocode — a separate convention,
  > flagged in `src/Engine/HarmonicBalance/CLAUDE.md`. Do not conflate the two.)
- For "all of axis" support both **`..`** (idiomatic C#) and **`All`** (a readable alias that
  evaluates to the same `Range`).

**Positional escape hatch** — `ds["V"]` indexes *every* axis positionally (no name resolution).
This requires knowing the cube's shape and axis order, which a viewer like splotRF surfaces; prefer
the named accessors above for everyday use.

## What a slice / transform returns
Three distinct operation categories. Keep them straight — they affect rank and `DataKind` differently.

**1. Slicing** (the bracket args above) — changes **rank**, never the values:
- **Any `..`/range present → returns a `DataCube`** whose rank = number of free (non-pinned) axes,
  with those axes' labels and units preserved. A rank-1 result *is* "a trace." `int` pins (drops)
  an axis; `..`/range keeps it.
- **All free axes pinned with `int` → returns the bare element** (`Complex` for a Complex cube,
  `double` for a Real cube), **not** a rank-0 cube. (Matches NumPy: `a[1,3]` is a scalar, `a[1,:]`
  is an array.)
- Slicing a `DataCube` yields a `DataCube` (or the bare element) — a closed algebra, so results
  re-slice freely. `ds["S"]` (whole cube) and `ds.S(2,1)` (sub-cube) are the **same type**.

**2. Element-wise transforms** — preserve **rank and axes**, set the `DataKind`. A rank-2 cube stays
rank-2; only the element type (and values) change. So `.real()` on a rank-2 Complex cube returns a
rank-2 **Real** cube of the same shape — to get a rank-1 result you *slice first, then transform*
(`ds.V("X1.drain", 1, ..).dB20()` → rank-1 Real).
  - Complex → Real: `.real()`, `.imag()`, `.mag()`, `.phase(deg=true)`, `.dB()`, `.dB10()`, `.dB20()`.
  - Complex → Complex: `.conj()`.
  - On an already-Real cube, `.real()`/`.mag()` etc. are no-ops returning the cube (compose cleanly).

  **dB variants — explicit, never context-dependent** (a function must not silently change its math):
  - `.dB10()` = `10·log10(|z|)` — the **power** dB; use when the cube already holds a power.
  - `.dB20()` = `20·log10(|z|)` — the **amplitude** dB; use for wave/voltage/ratio quantities.
    This is the conventional S-parameter dB (`20·log10|S21|`).
  - `.dB()` = `10·log10(|z|)`, i.e. an **alias for `.dB10()`**. Plain power-dB of whatever the cube
    holds, no interpretation of quantity type.
  > **Footgun:** `.dB()` on an amplitude quantity (S-parameter, voltage) gives **half** the
  > conventional value (`10·log10` instead of `20·log10`). For S-parameters/voltages use **`.dB20()`**.
  > `10` vs `20` is not arbitrary: power ∝ |amplitude|², so `20·log10(|v|) = 10·log10(|v|²)` — the
  > same physical dB applied in the amplitude vs power domain.

**3. Reductions** — collapse one **named** axis, dropping rank by one: `.max("Pin")`, `.min("Pin")`,
`.peak("Pin")`, `.at("Pin", idx)`. (Ordering-based reductions like `max`/`min` need a Real cube —
`.mag()` first if the cube is Complex.)

**Down to the raw array:** `.Values` hands out the backing `Complex[]`/`double[]` for a plotting
loop or interop, and `.Axis("Pin")` gives that axis's values/units. Use these when you genuinely want
bare numbers; the default returns keep axes attached so splotRF and the transforms above never lose
the x-axis.

## DataKind — Real and Complex, both honest
- A cube carries a `DataKind`: `Complex` (backed by `Complex[]`) or `Real` (backed by `double[]`).
- **Primary results that carry phase are `Complex`** (S-parameters, harmonic spectra of node
  voltages and branch currents). **Derived measurements take the kind their function returns** —
  `PAE`, `DE`, `Pout_dBm` → `Real`; `Gamma_load`, `Zin` → `Complex`.
- **Never** store a real quantity as complex-with-zero-imaginary. It doubles storage and makes
  downstream code guess whether a zero imaginary part means "no phase" or "not yet computed."
- A `Real` cube may be *promoted* to complex on request (for a consumer that only speaks complex),
  but storage stays the honest kind.

## Rules
- **One result model.** No analysis (S-param, HB, loadpull, sweep) may invent its own result struct;
  every run returns a `DataSet` of `DataCube`s. Measurements (Pout, PAE, IMn, …) are added to the
  DataSet as named cubes (the `user_results` group); they are not a separate type.
- A **sweep** wrapping an analysis adds a sweep axis across the cubes within the one DataSet.
- Axes are **named and unit-bearing**; preserve labels and units through slicing/reduction.
- All complex data is double precision (`System.Numerics.Complex`); all real data is `double`.

## Export
- **`.mat`** (MATLAB/Octave) maps naturally: each named cube → a named variable/struct field, so a
  whole DataSet exports as one `.mat`.
- **`.npy`** (NumPy): export the **whole DataSet as ONE packed structured (record) array** — a single
  `.npy` file whose record fields carry the named cubes plus axis metadata. (Not one file per cube.)
- Details in `docs/design/data-export.md`. The `.npy` "one packed structure" choice is fixed in
  `docs/PRD.md` §11.
- **`.npy` is circuitRF's native data file format** — the file circuitRF writes and splotRF (and other
  viewers) read back. A C# **importer** in RfCore reconstructs a `DataSet` from a `.npy` (the inverse of
  the exporter); the round-trip `export → import` is the symmetry oracle.

## File-format stability — NO backward compatibility during alpha
**Until the product approaches final release, the data-file format is NOT stable and we do NOT support
reading older files.** This is a deliberate standing decision — do not re-raise it, and do not add
migration/compat shims for old files.
- **Breaking the format is fine right now.** If a better layout, dtype, or schema emerges, just change
  it. Update the exporter and importer together; regenerate any test fixtures. Do **not** preserve the
  ability to read files written by an earlier alpha build.
- **Do not build version-negotiation, upgraders, or "read v1, write v2" paths.** A `format_version`
  field may be *written* (cheap, and useful later), and the importer may *reject* a mismatched version
  with a clear error — but it must **not** attempt to *read* an old version. Reject-with-clear-error is
  the only backward behavior; never silent migration.
- **Rationale:** we are in early development; supporting alpha-level files we know we will abandon is
  wasted effort and a drag on changing the format freely while it's still settling.
- This relaxation applies **only to on-disk file backward-compatibility.** The *in-process*
  `DataSet`/`DataCube` API contract with splotRF is still lockstep (see "Change carefully"): changing
  the file format means upgrading the circuitRF exporter and the RfCore importer and splotRF's reader
  together, in the same change — what we drop is the obligation to keep reading *yesterday's files*.
- **Revisit near final product:** when we approach release, this section is replaced by a real
  format-versioning + compatibility policy. Flag that transition when it's time (root `CLAUDE.md`).

## Memory
A `Complex` is 16 bytes (`double` is 8). A dense 50×50 loadpull grid × 100 nodes × 20 spectral lines
is tens of MB — fine in RAM — but large parametric sweeps can blow the budget. **Design the backing
store so it can be swapped** to chunked or memory-mapped storage later without changing the public
API. Don't build that now; just don't preclude it.

**Node-retention policy (HB).** To save memory, an HB analysis records to the `V` cube **only nodes
the user named** (a custom node name) **plus any node a measurement explicitly references** by path.
Rationale: a user cannot query `V(net_417)` for a net they never named, so storing every internal
net is dead weight; but a measurement that reaches a node by path must not silently fail, so its
referenced nodes are retained even if unnamed. The solver still *computes* every node (the MNA
solves the whole vector) — this is a *retention* policy, not a solve change. Branch currents are
identified by component instance + port (`I(X1.M1:d)`), so they are not subject to the named-node
filter — the instance name is the user's handle. Deep, hierarchy-reaching measurements
(see `docs/design/measurements.md`) thus define the retained set together with the named nodes —
the same set a future "retain only referenced nodes" optimization would prune to.

## Named-axis broadcasting in `DataCube.ElementWise` (brief-cube-broadcast-measurements Part A, 2026-06-22)

Cube–cube `+ − * /` now broadcasts by **axis name**, not by position. The previous implementation called `RequireSameShape` and threw when operands had mismatched rank.

**Algorithm:**
- **Fast path** (`SameShapeByName`): if both operands have the same axes in the same order with the same lengths, `ZipIdentical` runs the old element-zip loop unchanged. Byte-identical result.
- **Broadcast path** (`UnionAxes` → `MapPositions` → `BroadcastDecode` + `BroadcastOperandFlat`): the result has the **union** of both axis-sets (higher-rank operand determines axis order; lower-rank operand's axes are appended). Each operand "replicates" across any result axis it lacks. A rank-0 scalar broadcasts against any shape.
- `UnionAxes` throws `ArgumentException` if a shared axis name appears in both operands with **different lengths or differing coordinate values** (within 1e-12 relative tolerance).

**Primary use case:** a swept-variable cube is rank-1 `[Pin]`; an HB output cube may be rank-2 `[RFfreq, Pin]`. A measurement expression that subtracts them no longer throws "rank mismatch."

**Location:** `DataCube.ElementWise` (and helper methods `ZipIdentical`, `SameShapeByName`, `UnionAxes`, `MapPositions`, `BroadcastDecode`, `BroadcastOperandFlat`) in `src/RfCore/Data/DataCube.cs`.

Gate tests: T1–T6 in `tests/RfCore.Tests/DataCubeTests.cs` (broadcast subset-axis, axis-order, scalar, complex×real, incompatible-shared-axis throw, fast-path regression).

## Change carefully
The `DataSet`/`DataCube` **in-process API** contract (cube shape, `DataKind`, axis semantics, the
accessors) is **owned by circuitRF and consumed by splotRF.** Any change to it is a reviewed decision
— flag it (root `CLAUDE.md` → "Ask before") because splotRF must be upgraded in lockstep, and it must
handle both `DataKind`s when consuming a cube. *(The on-disk **file format** is exempt during alpha —
see "File-format stability" above: break it freely, just upgrade exporter+importer+splotRF together. The
lockstep that remains is the in-process API and the requirement that the three serialization sites move
together — not backward-compatibility with old files.)*

**`DataSet.MeasurementsGroup = "measurements"` bare-resolution rule (brief-meas-component, 2026-06-18).**
A bare cube name (no `.`) now resolves in the **default group first**, then the **`"measurements"` group**.
Analysis cubes (those in named groups like `"HB1"`, `"SP1"`) require qualification: `Analysis.Cube` (e.g.
`HB1.V`, `SP1.S`). The old sole-populated-group fallback is removed — a single-analysis run's cubes must
be addressed `SP1.S`, not bare `S`. The measurements group is bare-resolvable so the user references a
measurement as `Pout` (not `measurements.Pout`) in the Data Display, matching how RF tools surface
post-processing equations. Flat Touchstone sources (default group) are unaffected.
**Lockstep with splotRF:** splotRF must be aware that a grouped `.npy` DataSet has bare-resolving measurements.

**Shipped (Phase 7.2a) — per-port reference impedance `Z0` cube.** Every S-parameter DataSet carries a
**`Z0` complex cube** (name `"Z0"`, `DataKind.Complex`) with one axis `Axis("port", [1..n], "port")` (1-based
port numbers) holding the per-port, possibly-complex reference impedances in complex ohms.

Convention:
- `Z0.ComplexValues[k]` = impedance of port `k+1` (0-based index, 1-based port number).
- `DataSetBuilder.FromSnp` writes a **uniform** `Z0` cube (`all entries = snp.Z0`) — Touchstone is uniform by
  definition; every Touchstone-derived S DataSet now carries a `Z0` cube, so consumers can rely on its presence.
- `DataSetBuilder.BuildZ0Cube(Complex[] z0PerPort)` builds the cube from a per-port array.
- `DataSetBuilder.ToSnp` reads the `Z0` cube: uniform → `SNP.Z0 = that value`; absent (legacy `.npy`) → 50 Ω;
  non-uniform → `SNP.Z0 = port-1 value` + `RFNetwork.Warn(...)`.
- `SParameterEngine.Run` overwrites the uniform placeholder with the true per-port complex values from
  `z0PerPort` (already collected via `GetZ0` per Term/Port).
- `Z0Kind` enum (`UniformReal`, `UniformComplex`, `NonUniform`) and `DataSetBuilder.ClassifyZ0(DataCube)` are
  headless helpers for the Data Display non-uniform/complex indicator (Phase 7.2e).
- The `.npy` exporter/importer are generic over `ds.Cubes` — `Z0` round-trips with no change to either.
- **Lockstep:** splotRF must read the `Z0` cube and (a) renormalize/convert using the per-port
  `RFNetwork` overloads (already exist) and (b) surface the non-uniform/complex indicator. The splotRF consumer
  side is a separate lockstep item gated on Phase 7.2 Data Display work.

See `circuitRF/docs/design/data-display.md` §7.2 "Design (RESOLVED)".
