# circuitRF `.npy` data-file format — consumer guide

> **Audience:** Authors of splotRF, Python post-processing scripts, or any tool that
> consumes a circuitRF `.npy` export.  Distinct from `data-export.md` (the writer's design
> note).
>
> **Alpha stability:** The on-disk format is **NOT stable** during the alpha phase.
> Backward compatibility is explicitly declined — if the exporter changes, old files
> become unreadable and must be regenerated.  The `format_version` integer in `__meta__`
> encodes the writer's generation; the importer (C#) and any consumer script should
> reject mismatched versions with a clear error.  Do not build persistent file archives
> or third-party tools on this format until it is declared stable (post-v1.0).

---

## 1  Overview

A circuitRF export is a single **NumPy structured array** saved to a `.npy` file.  The
file is self-describing: the NumPy dtype header names every field and its sub-shape; the
`__meta__` JSON blob carries axis names, units, values, labels, and **group membership**.
No sidecar files, no `.npz` archives, no manifest.

**Current `format_version` is `2`** (see §2.1 — format_version 2 added DataSet *groups*).
Verify it before reading (§6); an alpha file from a different generation is not
compatible.

**Two levels of consumption:**

| Level | What you get | How |
|-------|-------------|-----|
| **Level 1** | All `DataSet` cubes rehydrated: groups, DataKinds, shapes, axes, numeric values | Read `__meta__`, index cube fields |
| **Level 2** | Any linear-interior node voltage or branch current reconstructed from the linear MNA system | Solve `G(ω_k)·x = bSrc − iNl` with the `__linnet_*` payload (see §5) |

Level 1 is implemented in C# (`DataSetImporter`) and trivially accessible from Python.
Level 2 is **documented here but not yet implemented in C#** — Phase 7 (splotRF
interactive reconstruction) will add the C# solve; see §5 for the full recipe.

**Producing a file (C#):**

```csharp
using RfCore.Export;

// Minimal: one DataSet to a .npy
DataSetExporter.Export(ds, "run.npy", ExportFormat.Npy);

// With options + the linear-network payload for Level-2 reconstruction
var opts = new ExportOptions {
    IncludeLinearNetwork = true,             // emit the __linnet_* fields (§4)
    LinearEvalMode       = LinearEvalMode.EvaluateNone,   // None | EvaluateAll | EvaluateSpecified
};
DataSetExporter.Export(ds, "run.npy", ExportFormat.Npy, opts, linearPayload);
```

`ExportFormat` also offers `Mat` (MATLAB `.mat`) and `Tsv` (tab-delimited); this guide
covers the `Npy` layout. `LinearEvalMode` (`EvaluateAll` / `EvaluateSpecified` with
`EvalNodeNames`/`EvalBranchRefs`) pre-evaluates extra linear-interior nodes/branches into
the exported cubes so a consumer need not do the §5 solve for them.

---

## 2  File structure

The file is a NumPy `.npy` file containing a **1-element structured array** (`shape = (1,)`).
Each field in the structured dtype corresponds to one cube or metadata blob:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  .npy preamble: magic (6 B) + version (2 B) + header-length (2 or 4 B)      │
│  .npy header: Python dict with 'descr' (the structured dtype), 'shape': (1,)│
├──────────────────────────────────────────────────────────────────────────────┤
│  Data section — one element of the struct, laid out field-by-field:          │
│                                                                              │
│  per-cube fields (one per DataSet cube, in ds.Groups × CubesIn order):       │
│    ('V',         '<c16', (2, 5, 4))   # complex128, shape = axis lengths     │
│    ('Pout',      '<f8',  (4,))        # float64                              │
│    ('I:M1:g',   '<c16', (5, 4))       # colons OK in NumPy field names       │
│    ('HB1.V',     '<c16', (2, 5, 4))   # group-qualified on a name collision  │
│    …                                                                         │
│                                                                              │
│  metadata field:                                                             │
│    ('__meta__',  '|S<N>')             # N = exact UTF-8 byte length of JSON  │
│                                                                              │
│  linear-network fields (only when exported with IncludeLinearNetwork=true): │
│    ('__linnet_omegas',           '<f8',  (K+1,))                             │
│    ('__linnet_G_rows',           '<i4',  (nnz,))                             │
│    ('__linnet_G_cols',           '<i4',  (nnz,))                             │
│    ('__linnet_G_data',           '<c16', (K+1, nnz))                         │
│    ('__linnet_bSrc',             '<c16', (S, K+1, mnaSize))                  │
│    ('__linnet_iNl',              '<c16', (S, K+1, N_interface))              │
│    ('__linnet_interface_nodes',  '<i4',  (N_interface,))                     │
│    ('__linnet_mna_size',         '<i8')   # scalar int64                     │
│    ('__linnet_non_ground_count', '<i8')   # scalar int64                     │
└──────────────────────────────────────────────────────────────────────────────┘
```

**The NumPy field name is an OPAQUE key — do not parse it.**  The authoritative
`(group, cube)` pair for every field lives in `__meta__` (the `group`/`cube` keys on each
entry).  Field names are assigned to be unique within the file:

1. Base = the cube name, with `/` replaced by `__slash__` (avoids HDF5-style path
   confusion).  Reverse with `field_name.replace('__slash__', '/')` *only* if you need the
   bare cube name — but prefer reading `cube`/`group` from `__meta__`.
2. **On a name collision** (two groups hold a same-named cube), the loser becomes
   `EscapeName(group) + "." + EscapeName(cube)` — e.g. `HB1.V`.  The `.` here is a literal
   field-name character, **not** a structural delimiter; don't split on it.
3. **On a further collision**, a `~N` suffix is appended (`HB1.V~2`).

**`__meta__` JSON schema (format_version 2):**
```json
{
  "format_version": 2,
  "groups": ["HB1", "SP1", "measurements"],
  "V": {
    "group": "HB1",
    "cube":  "V",
    "kind":  "Complex",
    "axes": [
      {"name": "node",     "unit": "V",   "values": [1, 2],
       "labels": ["X1.gate", "X1.drain"]},
      {"name": "harmonic", "unit": "",    "values": [0, 1, 2, 3, 4]},
      {"name": "Pin/dBm",  "unit": "dBm", "values": [-20, -18, -16, -14]}
    ]
  },
  "S": { "group": "SP1", "cube": "S", "kind": "Complex", "axes": [ … ] },
  "Gain": { "group": "measurements", "cube": "Gain", "kind": "Real", "axes": [ … ] },
  "__linnet_node_names":   ["n_src", "n_zs", "n_gate", "n_gbias", "n_drain", "n_zl", "n_dbias"],
  "__linnet_branch_names": ["branch#0", "branch#1", "branch#2", "L:Lchoke_g", "branch#4", "L:Lchoke_d", "branch#6"]
}
```

Per-entry keys: **`group`** (the group name; `""` for the default group), **`cube`** (the
cube name within that group), **`kind`** (`"Complex"` or `"Real"`), and **`axes`**.  Each
axis has `name`, `unit`, `values` (G17 round-trip precision), and an optional `labels`
array (present only when the axis carries string labels, e.g. the `node` axis of `V` and
branch-current cubes).  Node/branch name arrays are stored in `__meta__` (not as separate
structured fields) because NumPy structured arrays cannot hold variable-length strings
without pickling.

---

## 2.1  Groups (added in format_version 2)

A single run now produces **one grouped DataSet**: a group per analysis (keyed by the
analysis's results name — `HB1`, `SP1`, `DC1`, …) plus a `measurements` group holding the
post-run [measurements](../user/reference/measurements.html).  The export preserves this
structure:

- `__meta__["groups"]` lists the group names **in order**.  The default (unnamed) group is
  the empty string `""`.
- Every cube entry names its `group` and `cube`.  Reconstruct the grouping by walking the
  entries, not by parsing field names.

The C# importer rebuilds the groups automatically: `DataSetImporter.Import` returns a
`DataSet` whose `Groups`, `CubesIn(group)`, and qualified lookup (`ds["HB1.V"]`) all work
as they did at write time.  A **bare** cube name (`ds["V"]`) resolves in the default group
first, then the `measurements` group — the same rule the measurement evaluator uses.

> **Single-analysis files** (e.g. a CLI `sparam` export) typically have just the default
> group `""` plus possibly `measurements`; `groups` may be `[""]`.  Code that handles the
> grouped case handles this trivially.

---

## 3  Level 1 — load a DataSet

### 3.1 Python

```python
import json
import numpy as np

# Load the file
arr  = np.load('hero2_result.npy', allow_pickle=False)
meta = json.loads(arr['__meta__'][0])      # bytes → dict

# Check format version
assert meta['format_version'] == 2, f"Unsupported version {meta['format_version']}"

# Groups present in this run (e.g. analyses + a measurements group)
print("groups:", meta['groups'])          # e.g. ['HB1', 'SP1', 'measurements']

# Build a (group, cube) → numpy-field-name index from __meta__.
# NEVER parse the field name yourself — read group/cube from the entry.
by_group_cube = {
    (entry['group'], entry['cube']): field
    for field, entry in meta.items()
    if isinstance(entry, dict) and 'cube' in entry
}
v_field = by_group_cube[('HB1', 'V')]     # the opaque numpy field name, e.g. 'V' or 'HB1.V'

# Pull the cube
V = arr[v_field][0]       # shape (N_nodes, K+1, S), dtype complex128
# V[node_idx, harmonic_k, sweep_si]

# Read axis metadata (keyed by the same field name)
v_axes = meta[v_field]['axes']
node_axis  = v_axes[0]    # {"name": "node", "labels": ["n_gate", "n_drain"]}
harm_axis  = v_axes[1]    # {"name": "harmonic", "values": [0, 1, 2, 3, 4]}
sweep_axis = v_axes[2]    # {"name": "Pin/dBm", "values": [-20, -18, -16, -14]}

# Look up a node by name and plot its spectrum
node_labels  = node_axis['labels']                # ["n_gate", "n_drain"]
drain_idx    = node_labels.index('n_drain')       # 1
pin_dBm      = sweep_axis['values']
V_drain_fund = V[drain_idx, 1, :]                 # fundamental (k=1) vs Pin sweep

print("V_drain fundamental (V) at each Pin:")
for si, pin in enumerate(pin_dBm):
    print(f"  Pin={pin} dBm → |V_drain|={abs(V_drain_fund[si]):.4f} V")

# Pull a real cube
pout = arr['Pout'][0]     # shape (S,), dtype float64
print(f"Pout at Pin=-20 dBm: {pout[0]:.2f} dBm")
```

### 3.2 C# (using `DataSetImporter`)

```csharp
using RfCore.Export;

// Load Level-1: reconstructed DataSet (groups preserved) + optional linnet payload
var (ds, linnet) = DataSetImporter.Import("hero2_result.npy");

// ds is a grouped DataSet; iterate groups → cubes
foreach (var group in ds.Groups)                              // e.g. "HB1", "SP1", "measurements"
    foreach (var (name, cube) in ds.CubesIn(group))
        Console.WriteLine($"{group}.{name}: kind={cube.DataKind}, " +
                          $"shape=[{string.Join(",", cube.Axes.Select(a => a.Length))}]");

// Index the voltage cube — qualified ("HB1.V") or bare ("V", resolves default→measurements)
var V = ds["HB1.V"];                          // DataCube, DataKind.Complex
var axes = V.Axes;
string[] nodeLabels = axes[0].Labels!;        // ["n_gate", "n_drain"]
int drainIdx = Array.IndexOf(nodeLabels, "n_drain");

// Get V_drain at fundamental (k=1), sweep 0 (Pin=-20 dBm)
// Flat index = drainIdx * (K+1 * S) + k * S + si
int K1 = axes[1].Length;                     // K+1 = 5
int S  = axes[2].Length;                     // S   = 4
int flatIdx = drainIdx * (K1 * S) + 1 * S + 0;  // k=1, si=0
Complex vDrain = V.ComplexValues[flatIdx];
Console.WriteLine($"V_drain fundamental at Pin=-20 dBm: |V|={vDrain.Magnitude:F4} V");

// Check for linear-network payload
if (linnet is not null)
{
    Console.WriteLine($"Linear network loaded: {linnet.NodeNames.Length} nodes, " +
                      $"{linnet.GRows.Length} nnz, mnaSize={linnet.MnaSize}");
}
```

**Exceptions thrown by the importer:**

| Condition | Exception |
|-----------|-----------|
| File not found | `FileNotFoundException` |
| Bad magic / not a `.npy` file | `InvalidDataException` |
| `format_version` absent or wrong | `InvalidDataException` — regenerate from the current exporter |
| Truncated data | `InvalidDataException` |

---

## 4  `__linnet_*` field reference

When exported with `IncludeLinearNetwork = true`, these fields are present.

| Field | NumPy dtype | Shape | Description |
|-------|------------|-------|-------------|
| `__linnet_omegas` | `float64` | `(K+1,)` | ω_k = k·2π·f₀ rad/s. ω₀=0 (DC). |
| `__linnet_G_rows` | `int32` | `(nnz,)` | COO row indices, 0-based. Shared by all harmonics (union of all harmonics' sparsity patterns). |
| `__linnet_G_cols` | `int32` | `(nnz,)` | COO column indices, 0-based. |
| `__linnet_G_data` | `complex128` | `(K+1, nnz)` | G matrix entries: `G_data[k, nz]` = G(ω_k) at `(G_rows[nz], G_cols[nz])`. Zero-padded for entries absent from a specific harmonic (e.g. capacitors at DC where ω₀=0). |
| `__linnet_bSrc` | `complex128` | `(S, K+1, mnaSize)` | Source RHS snapshot: `bSrc[si, k, m]` = RHS at sweep si, harmonic k, MNA row m. |
| `__linnet_iNl` | `complex128` | `(S, K+1, N_interface)` | NL interface currents: `iNl[si, k, n]` = NL current at interface node n, harmonic k, sweep si. |
| `__linnet_interface_nodes` | `int32` | `(N_interface,)` | 1-based circuit node indices at the NL–linear boundary. |
| `__linnet_mna_size` | `int64` | scalar | = NonGroundCount + BranchCount |
| `__linnet_non_ground_count` | `int64` | scalar | Number of voltage-unknown rows/cols (MNA indices 0..NonGroundCount-1). |

**Name maps (in `__meta__` JSON):**

| Key | Length | Meaning |
|-----|--------|---------|
| `__linnet_node_names[i]` | `NonGroundCount` | Name of circuit node `i+1` (1-based). MNA row `i` is that node's voltage unknown. |
| `__linnet_branch_names[b]` | `MnaSize − NonGroundCount` | Name of branch `b`; its MNA row is `NonGroundCount + b`. |

---

## 5  Level 2 — lazy reconstruction of linear-interior V/I

### 5.1  The math

The HB engine partitions the circuit into a **linear sub-network** and a set of **nonlinear
devices** (SDDs, FETs).  At each harmonic k and sweep point si, the linear subnetwork
satisfies:

```
G(ω_k) · x(si, k)  =  b(si, k)

where:
  G(ω_k)         — the linearised MNA admittance matrix, size mnaSize × mnaSize
  x(si, k)       — the unknown vector (node voltages + branch currents)
  b(si, k)       — the source RHS, formed as:
                     b[m]  = bSrc[si, k, m]        for all m
                     b[interface_nodes[n]−1]  −= iNl[si, k, n]   for n=0..N_interface−1
```

`x[0..NonGroundCount−1]` are node voltages.
`x[NonGroundCount..mnaSize−1]` are branch currents (one per inductor/Vsrc/IProbe/tuner branch).

The HB sweep already computed `x` for the **nonlinear interface nodes** as part of the
convergence loop — that is the primary result in the `DataSet`.  The Level-2 step uses the
same linear system to recover **any other node voltage or branch current** without re-running
the HB sweep.

### 5.2  Index conventions — the bits that bite

**1-based circuit nodes vs 0-based MNA rows:**
- Circuit node numbering is 1-based (ground = 0, first non-ground = 1).
- `interface_nodes[n]` is a 1-based circuit node.
- The MNA row for that node is `interface_nodes[n] − 1` (0-based).
- `node_names[i]` is the name of the node whose MNA index is `i` (= circuit node `i+1`).
- To look up a node by name: `mna_idx = node_names.index("n_drain")`.

**Sparse G — union sparsity pattern:**
- `G_rows` and `G_cols` are the **union** of all harmonics' sparsity patterns, sorted by
  `(row, col)`.  At DC (k=0, ω=0), capacitor entries are zero — those positions exist in
  `G_rows`/`G_cols` but `G_data[0, nz]` is 0+0j for those entries.
- To build `G(ω_k)` in scipy: `csc_matrix((G_data[k], (G_rows, G_cols)), shape=(M, M))`
  where `M = mna_size`.

**DC harmonic (k=0):**
- `omegas[0] = 0`.  The G matrix is real-valued at DC (ω=0 → no imaginary parts from
  L or C), but stored as complex128 with zero imaginary parts.  Treat it as-is — `spsolve`
  handles complex zero-imaginary parts correctly.

**Branch currents:**
- MNA indices `NonGroundCount` to `mnaSize−1` are branch currents.
- `branch_names[b]` gives the human-readable name for branch b; its MNA index is
  `NonGroundCount + b`.
- Example: `branch_names = ["branch#0", "branch#1", ..., "L:Lchoke_g", ...]`
  → look for `"L:Lchoke_g"` to find the drain-side choke current.

### 5.3  Complete Python worked example

Reference circuit: **Hero 2** — single-tone GaN PA, 2 GHz, 4-point sweep (−20 to −14 dBm).
File exported with `IncludeLinearNetwork=True`.

```python
import json
import numpy as np
import scipy.sparse
import scipy.sparse.linalg

arr  = np.load('hero2_result.npy', allow_pickle=False)
meta = json.loads(arr['__meta__'][0])

# ── Load linnet fields ──────────────────────────────────────────────────────
omegas           = arr['__linnet_omegas'][0]          # shape (5,)
G_rows           = arr['__linnet_G_rows'][0]          # shape (nnz,)
G_cols           = arr['__linnet_G_cols'][0]          # shape (nnz,)
G_data           = arr['__linnet_G_data'][0]          # shape (K+1, nnz)
bSrc             = arr['__linnet_bSrc'][0]            # shape (S, K+1, mnaSize)
iNl              = arr['__linnet_iNl'][0]             # shape (S, K+1, N_interface)
interface_nodes  = arr['__linnet_interface_nodes'][0] # shape (N_interface,)  1-based
mna_size         = int(arr['__linnet_mna_size'][0])   # 14
non_ground_count = int(arr['__linnet_non_ground_count'][0])  # 7

node_names   = meta['__linnet_node_names']    # ['n_src', 'n_zs', 'n_gate', ...]
branch_names = meta['__linnet_branch_names']  # ['branch#0', ..., 'L:Lchoke_g', ...]

K1 = len(omegas)    # 5  (K+1, includes DC)
S  = bSrc.shape[0]  # 4  sweep points
N  = len(interface_nodes)  # 2  (n_gate, n_drain)

print(f"K+1={K1}, S={S}, mnaSize={mna_size}, NonGnd={non_ground_count}")
print(f"Interface nodes (1-based): {interface_nodes.tolist()}")
# → [3, 5]   (n_gate = circuit node 3, n_drain = circuit node 5)


# ── Helper: reconstruct x = G⁻¹·(bSrc − iNl) at (si, k) ──────────────────

def solve_linear(si, k):
    """Reconstruct node voltages and branch currents at sweep si, harmonic k."""
    # Build G(ω_k) from COO triplets
    G_k = scipy.sparse.csc_matrix(
        (G_data[k], (G_rows, G_cols)),
        shape=(mna_size, mna_size))

    # Form RHS: start with bSrc, subtract iNl at interface rows
    b = bSrc[si, k, :].copy()
    for n in range(N):
        mna_row = int(interface_nodes[n]) - 1   # 1-based → 0-based
        b[mna_row] -= iNl[si, k, n]

    # Solve — x[0..non_ground_count-1] = node voltages
    #         x[non_ground_count..]    = branch currents
    x = scipy.sparse.linalg.spsolve(G_k, b)
    return x


# ── Level-2 example: recover V_gate and V_drain at DC (k=0), Pin=-20dBm ──

x_dc = solve_linear(si=0, k=0)

gate_idx  = node_names.index('n_gate')   # MNA index 2  (circuit node 3, 1-based)
drain_idx = node_names.index('n_drain')  # MNA index 4  (circuit node 5, 1-based)

V_gate  = x_dc[gate_idx].real    # expected ≈ −3.050 V
V_drain = x_dc[drain_idx].real   # expected ≈ 48.000 V

print(f"\nDC (k=0), si=0 (Pin=−20 dBm):")
print(f"  V_gate  = {V_gate:.4f} V   (expected ≈ −3.050 V)")
print(f"  V_drain = {V_drain:.4f} V  (expected ≈ 48.000 V)")


# ── Branch current: drain-side choke at DC ──────────────────────────────────

if 'L:Lchoke_d' in branch_names:
    branch_idx     = branch_names.index('L:Lchoke_d')   # 0-based branch index
    mna_branch_idx = non_ground_count + branch_idx       # MNA row for this branch
    I_Lchoke_d_dc  = x_dc[mna_branch_idx].real          # ≈ 0.049 A (drain bias current)
    print(f"  I(L:Lchoke_d) DC = {I_Lchoke_d_dc*1000:.2f} mA   (≈ 49 mA drain bias)")


# ── Recover V_drain at fundamental (k=1) across all sweep points ─────────

print(f"\nV_drain fundamental (k=1) vs Pin:")
for si in range(S):
    x_fund = solve_linear(si=si, k=1)
    V_mag  = abs(x_fund[drain_idx])
    print(f"  si={si}: |V_drain|@fundamental = {V_mag:.4f} V")


# ── Cross-check against the exported V cube ──────────────────────────────
# The exported V cube contains the interface-node voltages from the HB sweep.
# Level-2 solution should match those entries at the interface nodes.

V_cube    = arr['V'][0]          # shape (N_interface, K+1, S)
V_meta    = meta['V']
node_lbls = V_meta['axes'][0]['labels']   # ['n_gate', 'n_drain']

# V_drain at fundamental, si=0: from cube vs. Level-2 reconstruction
drain_in_cube = node_lbls.index('n_drain')
V_drain_cube  = V_cube[drain_in_cube, 1, 0]         # complex
x_fund_si0    = solve_linear(si=0, k=1)
V_drain_recon = x_fund_si0[drain_idx]               # complex

print(f"\nCross-check V_drain at k=1, si=0:")
print(f"  From DataSet cube : {V_drain_cube.real:.6f} + j{V_drain_cube.imag:.6f}")
print(f"  From Level-2 solve: {V_drain_recon.real:.6f} + j{V_drain_recon.imag:.6f}")
print(f"  Relative error    : {abs(V_drain_cube - V_drain_recon)/abs(V_drain_cube):.2e}")
# Should be < 1e-8 (numerical round-off only)
```

**Expected output for Hero 2:**
```
K+1=5, S=4, mnaSize=14, NonGnd=7
Interface nodes (1-based): [3, 5]

DC (k=0), si=0 (Pin=−20 dBm):
  V_gate  = −3.0500 V   (expected ≈ −3.050 V)
  V_drain = 48.0000 V   (expected ≈ 48.000 V)
  I(L:Lchoke_d) DC = 49.13 mA   (≈ 49 mA drain bias)

V_drain fundamental (k=1) vs Pin:
  si=0: |V_drain|@fundamental = <value> V
  si=1: …

Cross-check V_drain at k=1, si=0:
  From DataSet cube : <re> + j<im>
  From Level-2 solve: <re> + j<im>
  Relative error    : <1e-8
```

The DC check (V_gate = −3.050 V, V_drain = 48.000 V) is the cleanest verification
point: it requires only real arithmetic (ω₀=0, bSrc DC entries are the bias voltages),
and the answer is the known gate/drain bias of the Hero 2 circuit.

### 5.4  C# sketch — Level 2 (to be implemented in Phase 7)

```csharp
// Phase 7 implementation sketch — not yet in the codebase.
// Uses the imported ImportedLinearNetwork from DataSetImporter.

using CSparse.Complex;
using CSparse.Storage;
using System.Numerics;
using RfCore.Export;

var (ds, linnet) = DataSetImporter.Import("hero2_result.npy");
if (linnet is null) throw new InvalidOperationException("No linear-network data in file.");

int M  = (int)linnet.MnaSize;
int N  = linnet.InterfaceNodes.Length;
int K1 = linnet.Omegas.Length;
int S  = linnet.BSrc.GetLength(0);

// Reconstruct x(si=0, k=0) — DC harmonic, first sweep point
int si = 0, k = 0;

// Build G(ω_k) from COO triplets into a CSparse matrix
int nnz = linnet.GRows.Length;
var rowIdx = linnet.GRows;
var colIdx = linnet.GCols;
var vals   = new Complex[nnz];
for (int nz = 0; nz < nnz; nz++) vals[nz] = linnet.GData[k, nz];
var G = SparseMatrix.OfIndexed(M, M, rowIdx.Zip(colIdx, (r, c) => (r, c)).Zip(vals, (rc, v) => (rc.r, rc.c, v)));

// Form RHS: bSrc − iNl at interface rows
var b = new Complex[M];
for (int m = 0; m < M; m++) b[m] = linnet.BSrc[si, k, m];
for (int n = 0; n < N; n++)
    b[linnet.InterfaceNodes[n] - 1] -= linnet.INl[si, k, n];  // 1-based → 0-based

// Solve G·x = b using CSparse LU
// ... (CSparse.Complex factorization) ...
// x[linnet.NodeNames.AsSpan().IndexOf("n_drain")] → V_drain
```

This is the Phase-7 implementation target.  The exported payload already contains
everything needed; Phase 7 adds the sparse-LU call on top.

### 5.5  Status

**Level 1 (C# importer and Python recipe): implemented and tested in Phase 5-8.**
**Level 2 (C# sparse-LU solve): documented here; implementation deferred to Phase 7 (splotRF interactive reconstruction).**

The `ImportedLinearNetwork` object returned by `DataSetImporter.Import` already exposes
all Level-2 data (`GRows`, `GCols`, `GData`, `BSrc`, `INl`, `InterfaceNodes`,
`NodeNames`, `BranchNames`, `MnaSize`, `NonGroundCount`).  A Level-2 consumer needs
only to add the linear-algebra step shown above.

---

## 6  Checking `format_version`

Both the C# importer and any Python consumer should verify the version before proceeding:

```python
meta = json.loads(arr['__meta__'][0])
SUPPORTED_VERSION = 2
if meta.get('format_version') != SUPPORTED_VERSION:
    raise ValueError(
        f"circuitRF .npy format_version mismatch: "
        f"file has {meta.get('format_version')!r}, expected {SUPPORTED_VERSION}. "
        f"Alpha files are not backward-compatible — regenerate from the current exporter.")
```

The C# importer (`DataSetImporter.Import`) performs this check automatically and throws
`InvalidDataException` on mismatch.

---

## 7  Quick-reference: Hero 2 circuit dimensions

Reference circuit used in all Phase-5 tests.  Useful for verifying a consumer
implementation against a known answer.

| Quantity | Value |
|----------|-------|
| Fundamental frequency | 2 GHz |
| Harmonics K+1 | 5 (DC + 4 RF) |
| Sweep | Pin = −20, −18, −16, −14 dBm (S=4 points) |
| Non-ground nodes | 7 (n_src, n_zs, n_gate, n_gbias, n_drain, n_zl, n_dbias) |
| Branch count | 7 |
| mnaSize | 14 |
| Interface nodes | 2 (n_gate = circuit node 3, n_drain = circuit node 5) |
| nnz (union sparsity) | 35 |
| DC V_gate | −3.050 V |
| DC V_drain | 48.000 V |
| DC I_drain (bias) | ≈ 49 mA |

---

*See also: `docs/design/data-export.md` (writer/format design); the end-user
[Results &amp; .npy export](../user/reference/npy-export.html) chapter; `src/RfCore/Export/`
(`DataSetExporter.cs`, `ExportOptions.cs`, `NpyWriter.cs`, `NpyReader.cs`,
`DataSetImporter.cs`, `ImportedLinearNetwork.cs`); `src/RfCore/Data/` (`DataSet.cs`,
`DataCube.cs`); `tests/Engine.Tests/Export/` (round-trip oracle).*
