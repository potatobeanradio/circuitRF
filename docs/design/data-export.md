# Data-export design — `.mat` and `.npy` (Phase 5-7)

> **STATUS: APPROVED AND IMPLEMENTED (Phase 5-7, 2026-06-06)**
>
> All §10 decisions resolved; three owner conditions met (PureHDF spike ✓, k=0 DC
> round-trip oracle ✓, SizeWarningThresholdMiB implemented ✓).
>
> Covers the `.mat` and `.npy` exporter API, format layout, linear-network
> serialization, `LinearEvalMode` enum, and disk-estimate/warning behaviour.

---

## 1. Scope and placement

The exporters live in **RfCore** (the circuitRF/splotRF shared library) because splotRF
is the primary consumer and both tools need the same format.  A single entry-point in
`RfCore.Export` takes a `DataSet`, an output path, a format tag, and an `ExportOptions`
record.  The caller (circuitRF CLI, future GUI, splotRF) is responsible for obtaining the
`DataSet` and — when `IncludeLinearNetwork = true` — for passing a populated
`ILinearNetworkPayload` sourced from the retained `HbLinearBackSolver`.

---

## 2. `.mat` format

### 2.1 Format target: MATLAB v7.3 / HDF5

**Decision: v7.3 (HDF5) as the sole target.** Justification:

- The MATLAB v5 format has a hard ~2 GB limit *per variable*. A parametric HB DataSet
  with `IncludeLinearNetwork = true` can exceed this: bSrc alone is
  `S × (K+1) × mnaSize × 16 bytes` (e.g., 100 sweeps × 8 harmonics × 500 unknowns =
  640 MB). v5 cannot hold it.
- HDF5 groups map directly to DataSet → group-of-datasets, preserving the named-cube
  structure without flattening.
- HDF5 string datasets carry the node/branch name maps cleanly (variable-length strings).
- v7.3 is supported by MATLAB ≥ R2011b, Octave ≥ 7.x (with HDF5 support), Python h5py,
  and Julia HDF5.jl — the consumer tools a researcher would actually use.
- The C# HDF5 ecosystem (HDF.PInvoke, PureHDF) gives write access without native GPL deps.

There is no v5 fallback; the format gate is "does it load in MATLAB R2011b+ and in h5py?"

### 2.2 HDF5 layout

```
/dataset/
  V                        # complex float64 dataset, shape [nNodes, nHarm, nPin] (or similar)
  PAE                      # real float64 dataset, shape [nPin]
  S                        # complex float64 dataset, shape [nFreq, ni, nj]
  <cube name> …            # one dataset per named DataCube, named identically
  __axes__/                # group: one sub-group per cube, carrying axis metadata
    V/
      axes.json            # JSON blob: [{name, unit, values[], labels?}, …]
    PAE/
      axes.json
    …
  __linear_network__/      # present only when IncludeLinearNetwork = true (see §4)
    omegas                 # float64[K+1] — angular frequencies ω_k = k·2π·f0; DC k=0 → ω=0
    G_rows                 # int32[nnz] — row indices (CSC / triplet; topology-invariant)
    G_cols                 # int32[nnz] — column indices
    G_data                 # complex float64[K+1, nnz] — matrix entries per harmonic
    bSrc                   # complex float64[S, K+1, mnaSize]
    iNl                    # complex float64[S, K+1, N_interface]
    interface_nodes        # int32[N_interface] — circuit node indices (1-based, non-ground)
    node_names             # string dataset [nonGroundCount]  — index i → node name (1-based node i+1)
    branch_names           # string dataset [branchCount]     — index b → "type:instancePath:terminal"
    mna_size               # scalar int64 = nonGroundCount + branchCount
    non_ground_count       # scalar int64 = nonGroundCount
```

**Complex vs Real**: DataCube knows its `DataKind`. A `Complex` cube is written as a
float64 HDF5 dataset with a 2-element compound type `{real: float64, imag: float64}` (the
standard HDF5 complex encoding that MATLAB and h5py both read natively). A `Real` cube is
written as plain float64. The consumer never guesses the kind — it is encoded in the dtype.

**Cube naming**: cube names are used verbatim as HDF5 dataset names. The colon in
`I:X1.M1:d` is a valid HDF5 name character; no escaping needed for HDF5. MATLAB `h5read`
also accepts colons in dataset names. If a cube name contains `/`, it must be escaped as
`__slash__` to avoid creating a sub-group; document this in the format spec.

**Axis metadata**: stored as a JSON blob per cube under `__axes__/<cube>/axes.json`.
Example for the `V` cube:
```json
[
  {"name": "node",     "unit": "",  "values": [1, 2, 3],
   "labels": ["n_gate", "n_drain", "n_bias"]},
  {"name": "harmonic", "unit": "",  "values": [0, 1, 2, 3, 4, 5, 6, 7]},
  {"name": "Pin",      "unit": "W", "values": [0.001, 0.002, 0.005, 0.01, 0.02]}
]
```
`labels` is present only on the node/branch axes of `V` and `I` cubes (where
`Axis.Labels` is non-null). Axis order matches the cube's axis order. The consumer uses
these to reconstruct the full labeled view — the axis *values* arrays carry the physical
coordinate (frequency in Hz, power in W, etc.).

---

## 3. `.npy` format

### 3.1 One packed structured array — no `.npz` fallback

**Decision: always write a single `.npy` file.** Justification:

NumPy's structured array dtype supports *sub-array fields* — each field declares its own
shape:
```python
dtype([
    ('V',   np.complex128, (nNodes, nHarm, nPin)),
    ('PAE', np.float64,    (nPin,)),
    ('S',   np.complex128, (nFreq, ni, nj)),
    …
])
```
Fields can have different ranks and lengths; this is not "ragged" in NumPy's sense. All
cubes in a DataSet from one analysis run have *fixed* shapes (determined at run time), so
every field has a known compile-time shape. The dtype is self-describing: `np.load('f.npy')`
returns a `(1,)`-shaped structured array whose field names and sub-shapes are embedded in
the dtype header. No separate manifest is needed.

**When `IncludeLinearNetwork = true`**: the G matrices across all harmonics share the same
sparsity pattern (the circuit topology is sweep-invariant, and harmonic-to-harmonic only
the *values* change, not the pattern). This allows the triplet representation:
```
G_rows: int32[nnz]        # shared by all harmonics
G_cols: int32[nnz]        # shared by all harmonics
G_data: complex128[K+1, nnz]  # data for each harmonic — fixed shape
```
All are fixed-shape tensors → embed as `__linnet_*` fields in the same structured array.
No `.npz` fallback is needed. If a future circuit produces different sparsity patterns per
harmonic (not possible with the current `BuildMna` — topology is constant at a given omega
because only source *values* change, not the nonzero pattern), revisit this decision.

### 3.2 Layout

The `.npy` file contains a 1-element structured array (`shape = (1,)`) whose dtype is
assembled at export time:

```
fields (per-cube, in DataSet.Cubes enumeration order):
  (<cube_name>,  <np_dtype>,  <cube_shape_tuple>)
  …

metadata field:
  ('__meta__',  'S<N>')   # N = exact JSON length; no padding required

optional linear-network fields (only when IncludeLinearNetwork = true):
  ('__linnet_omegas',           np.float64,    (K+1,))
  ('__linnet_G_rows',           np.int32,      (nnz,))
  ('__linnet_G_cols',           np.int32,      (nnz,))
  ('__linnet_G_data',           np.complex128, (K+1, nnz))
  ('__linnet_bSrc',             np.complex128, (S, K+1, mnaSize))
  ('__linnet_iNl',              np.complex128, (S, K+1, N_interface))
  ('__linnet_interface_nodes',  np.int32,      (N_interface,))
  ('__linnet_mna_size',         np.int64,      ())
  ('__linnet_non_ground_count', np.int64,      ())
```

`np_dtype` mapping: `DataKind.Complex` → `np.complex128`; `DataKind.Real` → `np.float64`.

**Consumer access pattern**:
```python
arr   = np.load('result.npy', allow_pickle=False)
meta  = json.loads(arr['__meta__'][0])   # dict: cube_name → {axes: […]}
V     = arr['V'][0]                      # shape (nNodes, nHarm, nPin), dtype complex128
PAE   = arr['PAE'][0]                    # shape (nPin,), dtype float64
omegas = arr['__linnet_omegas'][0]       # shape (K+1,)
G_rows = arr['__linnet_G_rows'][0]       # shape (nnz,)
G_data = arr['__linnet_G_data'][0]       # shape (K+1, nnz)
```

### 3.3 Metadata blob (`__meta__`)

The metadata field carries everything needed to reconstruct the labeled view:
```json
{
  "V": {
    "kind": "Complex",
    "axes": [
      {"name": "node",     "unit": "",  "values": [1, 2, 3],
       "labels": ["n_gate", "n_drain", "n_bias"]},
      {"name": "harmonic", "unit": "",  "values": [0, 1, 2, 3, 4, 5, 6, 7]},
      {"name": "Pin",      "unit": "W", "values": [0.001, 0.002, 0.005]}
    ]
  },
  "PAE": {
    "kind": "Real",
    "axes": [
      {"name": "Pin", "unit": "W", "values": [0.001, 0.002, 0.005]}
    ]
  }
}
```
The JSON is serialised first; its byte length L determines the `'S<L>'` dtype. The
consumer does `json.loads(arr['__meta__'][0])` — NumPy's `'S'` dtype returns `bytes`;
Python's `json.loads` accepts `bytes` directly.

**Cube name escaping in the metadata**: the colon in `I:X1.M1:d` is legal in a NumPy
field name. NumPy structured-array field names may not start with a digit or contain
null bytes; otherwise any Unicode is permitted. Document that field names match the
DataSet cube names verbatim except that a `/` is replaced with `__slash__` (same
escaping rule as for HDF5).

---

## 4. Linear-network serialization (`IncludeLinearNetwork = true`)

### 4.1 What is serialised and why

A consumer needs to solve `G(ω_k) · x = b`, where
`b = bSrc[si][k] - iNl_at_interface_nodes` (exactly the computation in
`HbLinearExtractor.SolveFullNetwork`). Providing G, bSrc, iNl, interface-node list, and
name maps is sufficient to reconstruct any linear-interior node voltage or branch current
without rerunning the HB sweep.

### 4.2 G(ω_k) — the per-harmonic linear MNA matrix

- **What it is**: the full-network linear admittance / MNA matrix built by
  `HbLinearExtractor.BuildMna(omega, zeroDrive: true)`. This is a complex sparse square
  matrix of size `mnaSize × mnaSize` (where `mnaSize = nonGroundCount + branchCount`).
- **Topology-invariance**: the nonzero *pattern* is fixed by circuit topology and does not
  change across harmonics (only the element *values* — admittances, impedances — change
  with ω). This lets us store `rows` and `cols` once and `data` as `[K+1, nnz]`.
- **Source of truth**: `HbLinearExtractor._luCache` currently stores `(SparseLU, Size,
  YNN)`. The exporter needs the sparse G *before* factorization. **Implementation
  requirement (§6.1)**: extend `_luCache` to also store the compressed sparse matrix
  (the pre-factored `SparseMatrix<Complex>` built by `MnaSystem.ToCsc()`) so the
  exporter can retrieve it via a new accessor without recomputing.
- **Sparse representation on disk**: CSC or triplet (owner choice — triplet is simpler to
  write; CSC is the native format after `MnaSystem.ToCsc()`). Recommendation: store
  triplets (`rows`, `cols`, `data`) in the file; the consumer reconstructs CSC or
  whatever sparse format it prefers. On read, `scipy.sparse.csc_matrix((data, (rows,
  cols)), shape=(mnaSize, mnaSize))`.
- **DC harmonic (k=0)**: ω₀ = 0. `BuildMna(0.0, zeroDrive: true)` is the real DC
  matrix. It is included as harmonic index 0 in `G_data[0, :]`. The consumer must use
  the real part when k=0 (or tolerate a numerically real complex matrix — the imaginary
  parts are zero at DC by construction).
- **Never densify**: the matrix is not written as a dense array. Even a modest 500-node
  circuit at 8 harmonics with ~5 000 NZ/harmonic → 40 000 complex entries → 640 KB.
  Dense would be 500² × 8 × 16 bytes = 32 MB for one circuit instance; unacceptable.

### 4.3 bSrc — per-(harmonic, sweep) source RHS

`bSrc[si][k]` is the full-MNA right-hand-side vector (length `mnaSize`) snapshotted at
sweep point `si`, harmonic `k`, with all sources active and component state current. It
is already retained in `HbLinearBackSolver._bSrc`. On disk: `complex128[S, K+1, mnaSize]`.

**Indexing note**: `_bSrc` in C# is `Complex[][][]` with layout `[sweepIdx][k][mnaIdx]`.
The exporter transposes this to `[S, K+1, mnaSize]` row-major for the file.

### 4.4 iNl — per-(harmonic, sweep) nonlinear interface currents

`iNl[si][n, k]` is the NL current at interface node n, harmonic k, sweep si — already
retained in `HbLinearBackSolver._iNl` (`Complex[][,]` with layout `[si][n, k]`). On
disk: `complex128[S, K+1, N_interface]`, transposed to `[si, k, n]`.

The consumer subtracts `iNl[si, k, n]` from `b[si][k][interfaceNode_n - 1]` to form the
full RHS before solving (see `SolveFullNetwork` for the exact operation).

### 4.5 Interface-node index list

`_interfaceNodes` from `HbLinearExtractor` — the circuit node indices (1-based,
non-ground) at the nonlinear-linear boundary. Length `N_interface`. On disk: `int32[N_interface]`.

The consumer uses this to know which rows of `bSrc` to apply the `iNl` subtraction to.

### 4.6 Node-index ↔ name map

Covers all `nonGroundCount` circuit nodes (matrix rows/cols 0 to `nonGroundCount - 1`).
Matrix index `i` → circuit node `i + 1` → name from `NodeMap.NameOf(i + 1)`. The map
is a string array of length `nonGroundCount`:

```
node_names[i] = "X1.drain"   (circuit node i+1, the name the user gave it)
```

Source: `HbLinearBackSolver._nodes` (the `NodeMap` from the elaborated netlist).

### 4.7 Branch-index ↔ name map

Matrix rows/cols `nonGroundCount` to `mnaSize - 1` are branch-current unknowns (inductors,
voltage sources, IProbes, TunerModel choke/bias branches). Matrix index `b` (0-based branch
index) → `b + nonGroundCount` (matrix index) → human-readable name.

**Current state**: `HbLinearExtractor._branchNamer` yields only `"branch#{n}"` — not
useful for round-trip validation by name. **Implementation requirement (§6.2)**: during
`BuildMna` (or in a subsequent recording pass using the same component order), inspect
`ComponentModel.LastBranchIndex` on each stamped component to build:

```
branch_names[b] = "L:X1.Lchoke"          (InductorModel, LastBranchIndex = nonGroundCount + b)
branch_names[b] = "V:X1.Vbias"           (VoltageSourceModel)
branch_names[b] = "IProbe:IP1"           (CurrentProbeModel)
branch_names[b] = "Tuner:X1.T1:choke"   (TunerModel ChokeBranchIndex)
branch_names[b] = "Tuner:X1.T1:bias"    (TunerModel BiasSupplyBranchIndex)
```

Naming convention: `"<modelKind>:<instancePath>:<role>"` where `<role>` is omitted for
single-branch models. This is richer than the current diagnostic namer but uses the same
`ElaboratedComponent.InstancePath` already available in the extractor.

### 4.8 Reconstruction formula (the consumer's algorithm)

Given the exported data, a consumer reconstructs `x(si, k)` as follows (pseudocode):
```python
G_k = scipy.sparse.csc_matrix(
        (G_data[k], (G_rows, G_cols)), shape=(mna_size, mna_size))
b   = bSrc[si, k, :].copy()
for n in range(N_interface):
    b[interface_nodes[n] - 1] -= iNl[si, k, n]   # 1-based node → 0-based index
x   = scipy.sparse.linalg.spsolve(G_k, b)
# x[0 .. non_ground_count-1] = node voltages
# x[non_ground_count ..] = branch currents
V_node = x[node_names.index("X1.drain")]          # look up 0-based index
```

The round-trip test (§7) verifies this matches `HbLinearBackSolver.GetSolution(k, si)`.

---

## 5. `LinearEvalMode` enum

```csharp
/// <summary>
/// Controls whether linear-interior node voltages and branch currents are
/// eagerly evaluated and written into the exported DataSet.
/// Orthogonal to <see cref="ExportOptions.IncludeLinearNetwork"/>.
/// </summary>
public enum LinearEvalMode
{
    /// <summary>
    /// Do not evaluate any linear-interior quantities beyond what is already
    /// in the DataSet.  The most compact output.  If IncludeLinearNetwork is
    /// also false, the consumer gets only the primary analysis results.
    /// </summary>
    EvaluateNone,

    /// <summary>
    /// Evaluate every linear-interior node voltage (all nonGroundCount nodes)
    /// and every linear branch current (all branchCount branches) across all
    /// harmonics and sweep points, and add them to the exported DataSet as
    /// cubes named "V_int" (node axis = node_names, harmonic, sweep) and
    /// "I_int" (branch axis = branch_names, harmonic, sweep).
    ///
    /// Expensive: cost is (nonGroundCount + branchCount) × (K+1) × S × 16 bytes
    /// of working memory before writing.  The disk-size estimate fires first.
    /// </summary>
    EvaluateAll,

    /// <summary>
    /// Evaluate only the caller-supplied list of node names and branch refs,
    /// then add those to the exported DataSet.  An empty list evaluates nothing
    /// (equivalent to EvaluateNone; not an error).  Name resolution uses the
    /// same path as measurement node/branch resolution (Phase 5-4).
    /// </summary>
    EvaluateSpecified
}
```

**Naming of produced cubes** (`EvaluateAll` / `EvaluateSpecified`):
- Node voltages are added to the existing `V` cube's node axis if the node is not already
  there, **or** written as a new cube `V_linear` (separate cube, separate node axis)
  to avoid mutating the primary `V` cube's layout. **Owner decision §8.4.**
- Branch currents are added as individual cubes `I:<instancePath>:<terminal>` (same naming
  as the device branch-current cubes already in the DataSet), only for branches not yet
  present. This matches the existing DataSet naming convention.

**Name resolution for `EvaluateSpecified`**:
- Node names: resolved via `ILinearBackSolver.TryGetNodeNumber` (the same path used by
  `HbLinearExtractor` and the measurement resolver in Phase 5-4). Unresolved names →
  warning on stderr, skip (do not abort).
- Branch refs: resolved by matching against `branch_names` (§4.7). An `I:X1.Lchoke`
  ref matches `branch_names[b] = "L:X1.Lchoke"` — the naming scheme must be documented
  so the caller can form valid refs. **Open question §8.5**: define the canonical branch
  ref string format for the `EvaluateSpecified` caller list.

---

## 6. Disk-space estimate and > 100 MB warning

### 6.1 Estimate formula

All sizes in bytes. Computed *before* any evaluation or serialization.

```
// Base: existing DataSet cubes
existingBytes = Σ_{cube in ds.Cubes} product(cube.Shape) × bytesPerElement(cube.Kind)
  where bytesPerElement(Complex) = 16, bytesPerElement(Real) = 8

// Linear-network payload (IncludeLinearNetwork = true)
linearNetworkBytes =
    nnz × (4 + 4 + 16) × (K+1)   // G_rows(4) + G_cols(4) + G_data(16) per harmonic
  + S × (K+1) × mnaSize × 16      // bSrc
  + S × (K+1) × N_interface × 16  // iNl
  + negligible (omegas, interface_nodes, name strings)

// Eager evaluation (LinearEvalMode != EvaluateNone)
evalAllBytes      = (nonGroundCount + branchCount) × (K+1) × S × 16
evalSpecifiedBytes = (|nodeList| + |branchList|) × (K+1) × S × 16

totalEstimateBytes =
    existingBytes
  + (IncludeLinearNetwork ? linearNetworkBytes : 0)
  + (EvaluateAll     ? evalAllBytes       :
     EvaluateSpecified ? evalSpecifiedBytes : 0)
```

**Units for the warning**: convert to MiB (÷ 2²⁰) for the warning message to match how
users think about file sizes.

### 6.2 Warning behaviour

Emit to `Console.Error` (the run log) **before any evaluation or serialization** when the
estimate exceeds `ExportOptions.SizeWarningThresholdMiB` (default 100 MiB):

```
[Export] Estimated output: 347.2 MiB — exceeds the 100 MiB advisory threshold.
         Dominant contributor: EvaluateAll (200 interior nodes × 8 harmonics × 50 sweep
         points = 320.0 MiB).
         Consider: LinearEvalMode.EvaluateSpecified with a node list, or
                   IncludeLinearNetwork = false.
         Proceeding — no data has been written yet.
```

Rule for dominant contributor: the largest of `existingBytes`, `linearNetworkBytes`,
`evalAllBytes` / `evalSpecifiedBytes`. Report that contributor by name with its breakdown.

**No abort**: the warning is informational. The export proceeds unconditionally after
logging. If the owner wants a confirmation gate (interactive Y/N), that is a CLI-layer
concern, not the exporter's.

---

## 7. API surface

### 7.1 `ExportOptions`

```csharp
/// <summary>
/// Options controlling the export of a DataSet to .mat or .npy.
/// Construct with <c>new ExportOptions()</c> and use init-setters.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>
    /// When true, serialize the per-harmonic linear MNA matrix G(ω_k), the
    /// per-(harmonic, sweep) source RHS bSrc and NL interface currents iNl,
    /// and the node/branch index↔name maps — enough for a consumer to
    /// reconstruct any linear-interior node voltage or branch current.
    /// Default: false (compact output).
    /// </summary>
    public bool IncludeLinearNetwork { get; init; } = false;

    /// <summary>
    /// Controls whether linear-interior V/I are eagerly evaluated into the
    /// exported DataSet before writing.  Default: EvaluateNone.
    /// </summary>
    public LinearEvalMode LinearEvalMode { get; init; } = LinearEvalMode.EvaluateNone;

    /// <summary>
    /// Node names to evaluate when LinearEvalMode = EvaluateSpecified.
    /// Uses the same name-resolution path as measurement references ("X1.drain").
    /// Ignored for EvaluateAll / EvaluateNone.
    /// Empty list with EvaluateSpecified → evaluate nothing (not an error).
    /// </summary>
    public IReadOnlyList<string> EvalNodeNames { get; init; } = [];

    /// <summary>
    /// Branch refs to evaluate when LinearEvalMode = EvaluateSpecified.
    /// Format: "I:instancePath:terminal" matching branch_names entries.
    /// </summary>
    public IReadOnlyList<string> EvalBranchRefs { get; init; } = [];

    /// <summary>
    /// Estimated-output-size advisory threshold, in MiB.  Before any evaluation
    /// or serialization, the exporter estimates the output size and warns (to
    /// stderr, no abort) if the estimate exceeds this value.  Default: 100 MiB.
    /// </summary>
    public int SizeWarningThresholdMiB { get; init; } = 100;
}
```

### 7.2 `ILinearNetworkPayload` — the bridge from the engine

The exporter cannot depend on `HbLinearBackSolver` directly (engine → RfCore would be a
circular dep; the exporter lives in RfCore). Instead, a thin interface is defined in
RfCore (or in a shared contract assembly):

```csharp
/// <summary>
/// Read-only view of the linear-network data retained by the HB back-solver,
/// passed to the exporter by the caller.
/// </summary>
public interface ILinearNetworkPayload
{
    int SweepCount        { get; }
    int HarmonicCount     { get; }  // K+1 (includes DC at index 0)
    int MnaSize           { get; }  // nonGroundCount + branchCount
    int NonGroundCount    { get; }
    int InterfaceCount    { get; }  // N_interface

    double[]    Omegas           { get; }  // [K+1] — ω_k = k·2π·f0
    int[]       InterfaceNodes   { get; }  // [N_interface] circuit node indices (1-based)
    string[]    NodeNames        { get; }  // [NonGroundCount] — index i → name of node i+1
    string[]    BranchNames      { get; }  // [branchCount] — index b → name string

    /// <summary>
    /// Sparse G at harmonic k: triplet form (rows, cols, data) of the full-MNA matrix.
    /// rows and cols are 0-based; data is the complex G values at ω_k.
    /// Rows and cols are IDENTICAL for all k (topology-invariant); the caller may
    /// request any k and obtain the shared pattern with harmonic-specific data.
    /// </summary>
    (int[] Rows, int[] Cols, Complex[] Data) GetSparseG(int k);

    /// <summary>bSrc[si][k][mnaIdx] — source RHS snapshotted during the sweep.</summary>
    Complex GetBSrc(int sweepIdx, int k, int mnaIdx);

    /// <summary>iNl[si][n][k] — NL interface current at interface node n, harmonic k.</summary>
    Complex GetINl(int sweepIdx, int interfaceNodeIdx, int k);
}
```

`HbLinearBackSolver` (or a new `HbLinearNetworkPayload` wrapper) implements this
interface.  The engine returns an `ILinearNetworkPayload?` from `HbRunResult` alongside
the `BackSolver`, populated when the caller opts into `IncludeLinearNetwork` at run time
(or always, if the engine retains the data regardless and the caller opts in at export
time).

**Implementation note**: `GetSparseG(k)` extracts from the cached `_luCache` entry. This
requires `HbLinearExtractor._luCache` to store the compressed sparse matrix alongside
the LU (§6 implementation requirements).

### 7.3 Exporter entry point

```csharp
namespace RfCore.Export;

public enum ExportFormat { Mat, Npy }

public static class DataSetExporter
{
    /// <summary>
    /// Export a DataSet to .mat or .npy.
    /// Estimates disk size first; warns to stderr if > 100 MiB.
    /// Does not abort on warning — proceeds unconditionally.
    /// </summary>
    /// <param name="ds">The DataSet to export.</param>
    /// <param name="path">Output file path (extension ignored; format determines it).</param>
    /// <param name="format">Mat or Npy.</param>
    /// <param name="options">Export options. Null → default (no linear network, no eval).</param>
    /// <param name="linearPayload">
    /// Required when options.IncludeLinearNetwork = true or
    /// options.LinearEvalMode != EvaluateNone. Null otherwise.
    /// </param>
    public static void Export(
        DataSet             ds,
        string              path,
        ExportFormat        format,
        ExportOptions?      options        = null,
        ILinearNetworkPayload? linearPayload = null);
}
```

---

## 8. Implementation requirements (changes outside RfCore)

These are changes to `circuitRF` (not RfCore) needed to support the export.

### 8.1 `HbLinearExtractor._luCache` — cache the sparse G

Currently: `Dictionary<double, (SparseLU Lu, int Size, Complex[,] YNN)>`.
Change: add `SparseMatrix<Complex> G` (the CSparse compressed matrix, pre-factorization)
to the cache tuple:
```csharp
Dictionary<double, (SparseLU Lu, int Size, Complex[,] YNN, CompressedColumnStorage<Complex> G)>
```
In `Extract(omega)`, after `BuildMna` and before `Factorize`, capture `mnaZ.ToCSC()`.
In `SolveFullNetwork(omega, …)`, same for the DC k=0 lazy-cache path. `MnaSystem.ToCSC()`
must be added (trivially: wrap the triplet dict into a CSparse `SparseMatrix.OfTriplets`
— this is already done inside `Factorize`; just expose it before factoring).

### 8.2 Branch-name map

In `HbLinearExtractor`, after `BuildMna(omega, zeroDrive: true)`, walk the components in
order and inspect `LastBranchIndex` on each stamped model. Build:
```csharp
private string[]? _branchNames;  // [branchCount] built once, cached
```
The walk is O(nComponents) and happens once per extractor lifetime (the component order
and branch assignments are deterministic — `BuildMna` is called with the same netlist each
time). Names follow §4.7.

### 8.3 `ILinearNetworkPayload` implementation

`HbLinearBackSolver` or a new `HbLinearNetworkPayload` wrapper class implements
`ILinearNetworkPayload`. The wrapper accesses `_iNl`, `_bSrc`, `_nodes`, and the
extended `_luCache` through the extractor (via new `internal` read-only accessors on
`HbLinearExtractor`). `HbRunResult.LinearNetworkPayload` (nullable) exposes this to
callers — populated unconditionally (the data is already retained), consumed by the
exporter only when the caller requests it.

---

## 9. Test oracle — linear-network round-trip

**What it proves**: the serialized G/bSrc/iNl/maps is a complete and correct description
of the linear system; a consumer reconstructing from it gets the same result as the
engine's own `GetSolution`.

**Setup**: Hero 2 (single-FET PA) at a mid-sweep pin point (e.g., sweep index 5),
harmonic k=1 (fundamental). After the HB run, export with `IncludeLinearNetwork = true`.

**In-process round-trip** (C# test, no file I/O, uses the `ILinearNetworkPayload` API
directly):
1. Retrieve `(rows, cols, data) = payload.GetSparseG(k=1)`.
2. Assemble `G` as a `SparseMatrix` (reuse CSparse).
3. Build `b = bSrc[si=5, k=1, :]` (copy).
4. Apply `b[interfaceNode_n - 1] -= iNl[si=5, n, k=1]` for each interface node n.
5. Back-solve: `x = G.Solve(b)`.
6. Assert `|x[nodeIdx] - backSolver.GetSolution(k=1, si=5)[nodeIdx]| < 1e-10` for the
   drain node (circuit node number from `TryGetNodeNumber("X1.drain")`).
7. Assert the same tolerance for a branch current (e.g., `x[nonGroundCount + branchIdx]`
   matching the `I:X1.M1:d` value from the DataSet at the same point).

**File round-trip** (optional but recommended): write to `.mat` and `.npy`, read back
the G/bSrc/iNl arrays in C# (or via a helper script), perform the same reconstruction,
assert the same tolerance. This tests the serialization path, not just the API.

---

## 10. Open decisions for owner

The following require owner input before implementation begins.

| # | Question | Default assumption (what will be implemented if not redirected) |
|---|---|---|
| 8.1 | `.mat` v7.3/HDF5: confirm as the sole format target (no v5 fallback). | v7.3 only. |
| 8.2 | `.npy` with `IncludeLinearNetwork`: confirm the `__linnet_*` fields stay in a single `.npy` (no `.npz` fallback). | Single `.npy` always. |
| 8.3 | `EvaluateNone` as the third `LinearEvalMode` value, or keep it two-valued (`EvaluateAll` / `EvaluateSpecified`) and use "empty `EvalNodeNames` list" to mean "evaluate nothing"? | Three-valued (with `EvaluateNone`) for clarity. |
| 8.4 | Eagerly evaluated V/I from `EvaluateAll`/`EvaluateSpecified`: merge into the existing `V` cube (new node axis entries) or write as a separate `V_linear` cube? | Separate `V_linear` + `I_linear` cubes to avoid mutating primary cube layout. |
| 8.5 | Branch ref format for the `EvaluateSpecified` caller list: `"L:X1.Lchoke"` (matching `branch_names`) or `"I:X1.Lchoke"` (current-measurement style)? | `"I:instancePath:terminal"` style, matching existing branch-current cube naming, with a lookup table mapping to `branch_names` entries internally. |
| 8.6 | `HbRunResult.LinearNetworkPayload`: expose always (the data is retained anyway) or only when a run-time flag is set? | Expose always; cost is near-zero (no new computation, just accessors). |
| 8.7 | The `> 100 MB` threshold: hardcoded constant or an `ExportOptions` field (`SizeWarningThresholdMiB`, default 100)? | **OWNER DECISION: expose now** as `ExportOptions.SizeWarningThresholdMiB` (default 100). |
| 8.8 | HDF5 library choice for `.mat` v7.3: PureHDF (managed, MIT, cross-platform) or HDF.PInvoke (p/invoke wrapper, cross-platform but heavier)? | PureHDF — managed, MIT, no native dependency (consistent with the managed-only rule in root CLAUDE.md). |

---

## 11. Disk estimate examples

Three representative cases to validate the estimate formula at review time:

**Hero 2 single-tone PA, no linear network**
- V: 3 nodes × 8 harmonics × 20 sweep points × 16 bytes = 7.5 KB
- I: 3 branches × 8 × 20 × 16 = 7.5 KB
- PAE, Gain, Pout_dBm: ≈ 0.5 KB total
- **Total ≈ 16 KB** — no warning.

**Hero 2 + IncludeLinearNetwork (mnaSize ≈ 10, nnz ≈ 25)**
- G data: 9 harmonics × 25 entries × 16 bytes = 3.6 KB; rows/cols: 200 bytes
- bSrc: 20 × 9 × 10 × 16 = 28.8 KB
- iNl: 20 × 9 × 2 × 16 = 5.8 KB (N_interface = 2: gate, drain)
- **Linear network total ≈ 38 KB** — still no warning.

**Large parametric sweep: 200 Vgg points × (Hero 2 inner) + EvaluateAll**
- V: 200 × 3 × 8 × 20 × 16 = 1.5 MB
- EvaluateAll: (10 nodes + 5 branches) × 9 × 20 × 200 × 16 = 86.4 MB
- **Dominant contributor: EvaluateAll ≈ 86 MB** — warning fires.
  Message: "EvaluateAll over 15 linear unknowns × 9 harmonics × 20 × 200 sweep points ≈ 86.4 MiB"

---

*— End of draft. Owner approval required before implementation of any code in §6/§7.*
