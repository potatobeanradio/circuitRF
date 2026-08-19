# Trace Card — Design

Status: implemented. Audience: circuitRF developers (architecture) + a future
user-documentation pass (see §9, "Interface reference").

The **trace card** is the per-trace editor in the Plot Inspector. Each card authors one
**trace** — a single curve, a *family* of curves, or a scalar value shown on the plot. This
document describes how a card maps a `DataSet`/`DataCube` to a renderable trace, the card's
UI, the V/I symmetry, scalars, and the slice shorthand grammar (including the `~` family
notation) with examples.

Primary source files:
`src/Ui/DataDisplay/ViewModels/TraceRowViewModel.cs` (card VM, signal cascade, axis-role
rebuild), `AxisRoleRowViewModel.cs` (one axis row), `Models/Trace.cs` (the trace binding +
shorthand), `CubeTraceSpecParser.cs` (shorthand → slice), `TraceExpression.cs` (multi-cube
expressions), `ViewModels/PlotInspectorViewModel.cs` (`TrySetCubeData` resolve),
`Views/DataDisplay/PlotInspectorView.axaml` (card layout).

---

## 1. Data model background — DataSet & DataCube

A simulation run produces one grouped **`DataSet`** (`results/<schematic>/run.npy`). A
`DataSet` is an ordered map of **groups** → named **`DataCube`s**:

- One group per analysis (`HB1`, `DC1`, `SP1`, …), a `measurements` group, and a default
  group (`""`, used by flat Touchstone).
- Cubes are addressed `Analysis.Cube` (e.g. `HB1.V`, `SP1.S`, `measurements.PDC`). A **bare**
  name resolves in the default group, then the `measurements` group, so a user can write
  `PDC` for `measurements.PDC`. Analysis cubes must stay qualified (`HB1.V`), because a bare
  `V` would resolve to the wrong group.

A **`DataCube`** is an N-dimensional array (`DataKind` = Real or Complex) with named **axes**.
`Rank` = axis count. Each `Axis` has numeric `Values`, a `Unit`, and optional string
`Labels` (used for node/branch names). The cubes a trace card cares about:

| Cube | Axes (single-point → swept) | Kind | Notes |
|------|------------------------------|------|-------|
| `V` | `[node]` → `[node, harmonic]` → `[sweep…, node, harmonic]` | Complex (HB) / Real (DC) | node axis `Labels` = net names |
| `I` | `[branch]` → `[branch, harmonic]` → `[sweep…, branch, harmonic]` | Complex / Real | branch axis `Labels` = IProbe names + device-port keys |
| `S` | `[freq, i, j]` or `[sweep…, freq, i, j]` | Complex | **Two paths:** default-group `S` (from Touchstone) uses the network/SNP path; named-group `S` (e.g. `SP1.S`, from a sim run) is a first-class `DataCube` — `freq` defaults to X, `i`/`j` are port selectors, `dB20` is the default Rect transform |
| measurement | scalar (rank-0) or `[sweep…]` | Real/Complex | e.g. `PDC`, `Gain` |

Two **provenance side-cubes** mark the "user-relevant" subset of a label axis, mirroring each
other:

- `__LabeledNodes` — the user-named nets (filters the **node** selector).
- `__ProbeBranches` — the IProbe branch names (filters the **branch** selector); device-port
  branches are present in `I` but *not* listed here.

Both are `__`-prefixed, so they survive sweep-stacking unchanged and never appear as
selectable signals.

---

## 2. The trace binding

A cube-bound trace (`Trace`) is the tuple:

```
(SourcePath, CubeName, Slice, Transform)         // single-cube  → slice path
        — or —
(SourcePath, Expression)                          // multi-cube   → expression path

        + optionally (XSpec, XSourcePath)         // "plot versus" — X from another quantity
```

- **`CubeName`** — qualified (`HB1.V`) for analyses, bare (`PDC`) for measurements/default.
- **`Slice`** — `AxisSlice[]`, one **role** per cube axis:
  - `KeepAsX` — this axis is the plot's X axis (a whole axis `:`, or a narrowed range `a:b`).
  - `FamilyIterate` — iterate this axis, one curve per value (a *family*).
  - `PinToIndex` — **Fix** the axis to a single index/label (a selector value).
- **`Transform`** — `None | dB20 | dB10 | dB | Mag | Phase | Real | Imag | Conj`.
- **`Expression`** — a free-form element-wise expression over one or more cubes
  (`mag(HB1.V) - mag(HB1.Vref)`); resolves via `TraceExpression`, not the slice path.
- **`XSpec` / `XSourcePath`** — **plot versus** (`Gain vs Pout`): the trace's X data comes from the
  named quantity instead of the cube's swept axis, optionally out of a different loaded file. Held as
  its own field precisely so the Y side keeps its `CubeName`/`Slice` identity and everything on this
  card goes on working. Full spec: `plot-versus.md`.

### How a binding resolves (`TrySetCubeData`)

The slice's roles determine the result:

| Roles present | Result | Valid on |
|---------------|--------|----------|
| exactly one `KeepAsX`, rest `Fix` | one curve: X = the kept axis, Y = sliced values | Rect / Smith / Polar / Table |
| one `FamilyIterate` + one `KeepAsX`, rest `Fix` | a *family* (one curve per family value) | Rect / Smith / Polar |
| **no** `KeepAsX` (all `Fix`), or a rank-0 cube | a **scalar** value | **Table only** (`<invalid>` elsewhere) |

The node/branch label axis is treated specially during defaulting (see §4).

---

## 3. Card architecture (VM ↔ cube)

```
PlotInspectorViewModel
 └── Traces : TraceRowViewModel        // one card per trace
       ├── AvailableGroups / SelectedGroup     // group cascade  (HB1, DC1, Measurements, S-Parameters)
       ├── AvailableSignals / SelectedSignal   // item within the group (V/I, or PDC…, or S(i,j))
       ├── AxisRoles : AxisRoleRowViewModel[]  // one row per cube axis (X / Fam / Fix / selector + eye)
       ├── ShowAll / ToggleShowAllCommand      // the eye: reveal unlabeled nodes / device-port branches
       ├── SpecShorthand / CommitSpec(text)    // the spec text box (bidirectional)
       └── Transform, Line/Symbol/Z0/Format    // styling
```

Data flow:

- **Picker → trace.** Selecting a group filters `AvailableSignals`; selecting an item sets
  `CubeName` and a *default slice*; editing an axis row rewrites `Slice`. Every change calls
  `FlushSliceAndRebuild` → `RebuildAndNotify` → `TrySetCubeData`, which slices the cube and
  pushes X/Y (or family, or scalar) into the `Trace`, then the plot redraws.
- **Trace → picker (reverse sync).** Editing the spec text box (`CommitSpec`) re-parses the
  text; on success it resets `CubeName/Slice/Transform` and calls `RebuildSignals`, which
  re-selects the matching group + item and rebuilds the axis rows so **every combo on the card
  tracks the typed expression**. On an invalid/multi-cube expression it keeps the expression
  as the source of truth and clears the stale axis rows (best-effort) — the user can recover
  via the combos.

The card never stores cube data; it stores only the *binding*. Re-running a sim re-resolves
the same binding against the new `DataSet` (and `ReseedSliceIfCubeShapeChanged` re-derives the
slice from the shape-independent spec text if axes were added/removed/reordered).

---

## 4. Card UI (top to bottom)

**Identity row** — *what* to plot:
- **Group** selector → **Item** selector → matrix-type (S/Y/Z, network only) → →R (secondary
  Y axis, Rect only).
- The item selector is a compact **V / I** icon-select for analysis groups, and a combo for
  variable lists (measurements, S-parameters). Analysis groups always offer **both** V and I;
  picking one whose cube is absent shows an empty-state ("No node voltages" / "No branch
  currents").

**Axis-role editor** — one row per cube axis, *how* to slice it:
```
 node (V)   [ X ] [ Fam ] [ Fix ]   ▾ Vout      👁
 harmonic   [ X ] [ Fam ] [ Fix ]   ▾ 2.40 GHz
```
- **X** — use as the plot X axis. **Fam** — iterate as a family. **Fix** — pin to one value
  (the **▾** selector). Exactly one X (or none → scalar); at most one Fam.
- The **node/branch** label axis defaults to **Fix** (a selector), never X; the default X
  prefers the **`freq`** axis when present (S/Y/Z parameter cubes and freq-swept cubes), then
  falls back to the first non-label axis (harmonic, sweep…). With no non-label axis (e.g.
  no-sweep DC), the cube has no X and resolves to a scalar.
- The **eye** (👁) sits on the label-axis row and reveals the unlabeled entries — all nodes
  beyond `__LabeledNodes`, all device-port branches beyond `__ProbeBranches`. It is the single
  "show all" control, shared by the node and branch rows.

**Spec row** — the transform combo + the editable **shorthand** text box (§5).

**Style rows** — line, symbol, per-port Z0 (network), and Table number-format.

### Simulated S-parameters vs Touchstone

Two paths produce S-parameter data; which one the card uses depends on how the data arrived:

| Source | Group | `entry.Snp` | Path | CubeName |
|--------|-------|-------------|------|----------|
| Touchstone file (`.s2p`…) | `""` (default) | non-null | **Network/SNP** — group "S-Parameters", matrix-element items | n/a |
| S-param run result (`run.npy`) | `"SP1"` (analysis name) | null | **Cube** — `freq` → X, `i`/`j` port selectors, `dB20` on Rect | `SP1.S` |

The rules:
- Default-group `S` is **always** owned by the network path (skipped by the cube picker).
- Named-group `S` (e.g. `SP1.S`, axes `[(sweep,) freq, i, j]`) is a **first-class cube**
  offered in the group picker under its analysis name.
- `Z0` is **always** skipped (per-port reference impedance, not a signal).
- A sweep axis is **pinned** by default; promote it to **Family** for one S-vs-freq curve per
  sweep point. `i`/`j` are **1-based port selectors** — the shorthand shows port numbers
  (`SP1.S[:, 1, 1]` = S11, `SP1.S[:, 2, 1]` = S21), though the internal `Fix` index stays 0-based.
- `dB20` is the default Rect transform for S/Y/Z parameter cubes (axes `freq`, `i`, `j`).
  Smith and Polar get no transform (`CubeTransform.None`).

---

## 5. The spec shorthand (interface)

The text box is a two-way view of the binding. Grammar:

```
[transform] CubeName[ token, token, … ]
```

- **`transform`** — optional prefix: `dB20 V[…]` (space form) or `mag(V[…])` (function form).
  One of `dB20 dB10 dB mag phase real imag conj`.
- **`CubeName`** — qualified (`HB1.V`) or bare (`PDC`, `V`).
- **token** per axis (in cube-axis order):

  | Token | Meaning (role) |
  |-------|----------------|
  | `:` | whole axis as **X** (`KeepAsX`) |
  | `a:b` | a narrowed range as X |
  | `~` | this axis is the **family** (`FamilyIterate`) |
  | `"Vout"` | **Fix** to the labeled entry (node/branch name) |
  | `3` | **Fix** to integer index 3 |

- A **bare** `CubeName` (no `[...]`) means "the whole cube" — every axis `:`. A picker-authored
  binding that reduces to a single whole-axis X is *displayed* bare (`PDC`, not `PDC[:]`); if a
  user explicitly types `PDC[:]`, that is preserved.
- A fully-**Fixed** spec (no `:`/`~`) is a **scalar** (`DC1.I["Iout"]`, `DC1.V["Vout"]`) — valid
  on a Table.
- On an S/Y/Z cube's **`i`/`j`** axes a bare integer is a **1-based port number**
  (`SP1.S[:, 2, 1]` = S21, `SP1.S[:, 1, 1]` = S11), not a 0-based index — matching how RF
  engineers name S-parameters. Every **other** axis (`freq`, sweep, harmonic) uses 0-based
  integer indices; labeled axes (node/branch) use quoted names. A port outside `1..nPorts` is a
  reported error.

Validity: exactly one X **or** zero X (scalar); at most one `~`; a `~` requires an X. Anything
else is reported inline under the box.

### The `vs` separator (plot versus)

A spec may end with `vs <x-spec>` — `Gain vs Pout`, `Gain[:, ~] vs Pout`,
`dB20(HB1.V[:, "Vout", 1]) vs Pout` — which plots the trace against that quantity instead of the
cube's swept axis. `vs`/`versus` is a **lowest-precedence** separator, split off before any cube-name
scan and recognised at top level only (never inside `[ ]`, `( )`, or a quoted label), at most one per
trace. Both sides are ordinary specs, so nothing above changes. On the card it is the **vs X** row,
where the X side's swept axis and family are inherited from the Y side by axis name — see
`plot-versus.md`.

---

## 6. V/I symmetry

Voltage and current are deliberately symmetric so one mental model and one set of controls
cover both:

| Voltage | Current |
|---------|---------|
| one `V` cube | one `I` cube |
| `node` axis (`Labels` = net names) | `branch` axis (`Labels` = IProbe + device-port names) |
| `__LabeledNodes` (user nets) | `__ProbeBranches` (IProbe branches) |
| `V("Vout", …)` accessor | `I("Iout", …)` accessor |

The same axis-role editor renders both (node row vs branch row), the same eye reveals the
unlabeled subset, and the measurement accessors pin the label axis identically. A user "gets a
branch current" by placing an **IProbe** — only IProbe branches are labeled by default; the
eye exposes raw device-port currents for advanced use.

---

## 7. Scalars & operating points

A no-sweep DC run is an **operating point**: `V[node]` and `I[branch]` are rank-1, and a
measurement like `PDC` is rank-0. Because the node/branch axis is a selector (not X), picking a
node/branch with no other axis yields a **scalar** — rendered as a value cell on a **Table**,
and shown as a soft `<invalid>` on Rect/Smith/Polar (use a Table for operating-point data).
This is why `DC1.I("Iout")*DC1.V("Vout")` works as a measurement and `DC1.I["Iout"]` works as a
scalar trace.

---

## 8. Family of curves & the `~` notation

A **family** plots one curve per value of a chosen axis — e.g. a load-pull-style sweep, or "V
vs harmonic, one curve per bias point." Exactly one axis is the family (`Fam` / `~`), exactly
one is X (`:`), and the rest are Fixed. The family axis's `Labels`/`Values` become the curve
legend. The number of curves is capped (`Trace.MaxFamilyCurves`).

Two ways to express a family:

1. **Explicit `~`** on the family axis, `:` on the X axis, `Fix` elsewhere.
2. **Positional convention** — if you write two `:` (no `~`), the **outer** (first) kept axis
   becomes the family and the **inner** (last) stays X. This makes `V[:, :]` "a family over the
   first axis, plotted against the second" without extra syntax.

### Examples

Assume HB cubes with axis orders `V[node, harmonic]` (no sweep) and, under a `Pin_avail`
parametric sweep, `V[Pin_avail, node, harmonic]`.

| Spec | Reads as | Result |
|------|----------|--------|
| `HB1.V["Vout", :]` | node Fixed to `Vout`, harmonic X | spectrum at one node (one curve) |
| `HB1.V[~, :]` | node family, harmonic X | one curve **per node**, vs harmonic |
| `dB20 HB1.V["Vout", :]` | as above, in dB20 | spectrum, dB |
| `HB1.V[:, "Vout", 0]` | Pin_avail X, node `Vout`, harmonic 0 | DC node voltage vs input power (one curve) |
| `HB1.V[:, "Vout", ~]` | Pin_avail X, node `Vout`, harmonic family | one curve **per harmonic**, vs power |
| `HB1.V[~, "Vout", :]` | Pin_avail family, node `Vout`, harmonic X | one curve **per power**, vs harmonic |
| `mag(HB1.I[~, :])` | branch family, harmonic X, magnitude | one current curve per IProbe branch |
| `HB1.V[:, :, 0]` | positional: Pin_avail family (outer `:`), node X (inner `:`), harmonic 0 | one curve per power, vs node index |

Equivalently in the UI: set one row to **Fam**, one to **X**, leave the label axis on **Fix**
with the **▾** selector; the spec box mirrors the result (e.g. `HB1.V[~, "Vout", :]`).

---

## 9. Multi-cube expressions

When a trace needs arithmetic across cubes, the spec box accepts a free expression
(`mag(HB1.V) - mag(HB1.Vref)`, `dB20(SP1.S[:, 2, 1])`). These take the `TraceExpression` path
(element-wise over the referenced cube slices) instead of the single-cube slice path. Bare
measurement names work as single-token specs (`PDC`); using a bare name *inside* a larger
expression still requires the qualified form today (a noted future enhancement).

---

## 10. Interface reference (for user documentation)

A condensed cheat-sheet to expand into end-user docs:

- **Pick what to plot:** choose a *group* (analysis or Measurements), then an *item* (V, I, a
  measurement, or an S-parameter).
- **Pick a node/branch:** the node (for V) or branch (for I) row shows a selector; the **eye**
  reveals all nodes / all branch currents (default shows only labeled nets / IProbe branches).
- **Choose the X axis:** press **X** on the axis you want along the bottom (frequency/harmonic,
  swept power, etc.).
- **Make a family:** press **Fam** on the axis to sweep as multiple curves (or type `~`).
- **Fix the rest:** other axes show a **Fix** selector — pick the single value to hold.
- **Transform:** choose `dB`, `mag`, `phase`, … (or type `dB20 V[…]` / `mag(V[…])`).
- **Type it directly:** the spec box accepts `[transform] CubeName[tokens]` where a token is
  `:` (X), `~` (family), `"name"`/index (fix), or `a:b` (range). Editing the box updates every
  control on the card; the controls always produce a valid expression.
- **Plot against another quantity:** tick **vs X** and pick it (Gain against **Pout**), or type
  `Gain vs Pout`. Families follow the Y side automatically; the X side can come from another loaded
  file. See `plot-versus.md`.
- **Operating points (no sweep):** values are scalars — view them on a **Table**.

---

## Related design docs
`docs/design/plot-versus.md` (the `vs` separator and the vs-X row),
`docs/design/results-dataset-layout.md` (grouped run.npy), `docs/design/measurements.md`
(`V(...)`/`I(...)`/`S(...)` accessors), `docs/design/data-display.md` (plot types, renderers).
