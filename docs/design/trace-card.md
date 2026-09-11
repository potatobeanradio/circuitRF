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
- On a **`port`** axis a bare integer is likewise a **port number** (ANT-7), but it is resolved
  against the axis's own VALUES rather than by subtracting one: a far-field cube's port axis carries
  the numbers of the ports that were actually DRIVEN and those need not start at 1 or be contiguous.
  A port the cube does not hold is refused listing the ones it does. harmonicaRF's intrinsic-plane
  `port` axis holds `0, 1, 2 …`, so its existing specs mean exactly what they always meant.

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

## 9a. The WSProbe section (WSP-4)

A source carrying a `wsp` matrix and its `__WspProbes` table offers, in the ordinary item picker and
in its own group (`SP1 ▸ WSProbe`), every quantity T. A. Winslow, *General Circuit Analysis Using
The WSProbe* (2023) derives from that matrix. Picking one opens a **WSProbe** section on the card.

**A probe trace is a cube trace.** Its `CubeName` is the run's own `…wsp` cube, so every "is that
cube still in this source" check downstream is the ordinary one; its `Slice` is authored against the
METRIC's axes — the matrix cube's leading axes, `{freq}` or `{Pin, freq}` — so the family/slider
mechanism, the markers, the Table, the export and `.cdd` persistence work with no probe-specific
path. What the `Trace.Wsp` spec changes is only where the VALUES come from:
`TraceResolve.SetCubeDataFromCore` substitutes `WspSource`'s metric cube for the raw matrix at the
same one interception point a renormalized S/Z/Y cube is substituted at.

**No numerics live in the Data Display.** Every value is a call into `src/RfCore/Stability/` — the
same functions the S-parameter engine computes a run's `H0:`/`ZG:`/`SM_Y0:` cubes with, and the same
ones `wsp_H0(SP1.wsp, idx)` resolves to in a `measure` line. The gate is bit identity, the rule
`NetworkMetrics` is already under.

**The list, and its gating** (`WspMetrics`, in `src/Render/DataDisplay/Models/WspTrace.cs`):

| Group | Items | Plots on |
|---|---|---|
| Driving point | `H0`, `Y0`, `1/H0`, `1/Y0` | Polar, Rect; Smith for `H0`/`Y0` |
| Bidirectional | `ZG`, `ZL`, `YG`, `YL`, `Zop`, `Yop` | Smith, Polar, Rect |
| Loop gain | `LG`, `F`, `LGF`, `LGR`, `LG_H`, `LG_MF`, `LG_MR`, `LG_MGF`, `LG_MGR` | Polar, Rect |
| Match | nodal Γ | Smith, Polar, Rect |
| Stability margin | `SM_Y0`, `SM_H0`, `SM`, and the proxies `rY`, `iY`, `rH`, `iH` | Rect only |
| Probe pair | `F_LGa`, `F_LGf`, `F_LGH`, `F_LGM`, `LGa`, `LGf`, `LGH`, `LGM` | Polar, Rect |
| Probe set | Ohtomo's `G_i` | Polar, Rect |
| Envelope | `1/H0env`, `1/Y0env` | Polar, Rect |
| Envelope | `unstable`, `SMenv`, `NDFenc` | Rect only |

A metric that does not fit the plot type is offered **disabled with a reason**, never removed — the
rule the derived metrics already follow, for the same reason: a metric that vanishes reads as one
circuitRF does not have. A Table takes everything.

**Case is load-bearing in these names and cannot be folded away.** The document writes `LGF` for one
probe's forward synthetic-circulator loop gain (Eq. 99) and `LGf` for a probe pair's
feedback-as-synthetic-FET loop gain (Eq. 149); likewise `LG_H` against `LGH`. `WspMetrics.TryParse`
resolves the exact spelling first and offers the loose, case-insensitive form only for names that
have no collision under it — so `lgf` resolves to neither rather than to one of them.

**The card's own controls.** A **probe** picker (labels from `__WspProbes`, in `idx` order, showing
`idx` beside each — probes are named and never indexed in a `.cdd`, because `idx` is assigned at
elaboration and a design that gains a probe renumbers the ones after it). A **with** picker for the
pair metrics. An ordered **set** for Ohtomo, where order is part of the answer. A **Z0**, blank
meaning "the source group's own port-1 Re(Z0), else 50 Ω", resolved at draw time. Each row is shown
only when its metric reads it: an inert control implies it changed the answer.

**The readouts** (`WspReadouts`) are the three readings the document takes off a polar plot by eye,
made countable: Kurokawa's start-up frequencies on a driving-point locus (`none` is a real answer and
is printed), the encirclement count of a polar locus, and — for a margin — its minimum together with
the Kurokawa search of the *matching* driving-point function (`SM_Y0` ↔ `1/Y0`). **Mark crossings**
places one marker per reported frequency. A margin on a rect plot also draws two horizontal reference
lines: the run's own `MarginThreshold` (dashed, from the `__WspMarginThreshold` cube) and the −12 dB
floor, below which one side of the node presents negative resistance.

### The Envelope sub-card (R-wsp4-9)

An envelope quantity is not read at one probe: it is read at a SUSPECT probe, on a circuit whose
SOURCE and LOAD terminations have been replaced. So the section grows three more rows.

- **source** and **load** each name a probe plus a **|Γ| ladder** — one magnitude per rung, comma
  separated. `(not pulled)`, a blank ladder, or a single `0` is that side's off state; a `0` rung
  *beside* others is kept, because there it is the matched termination the rest are read against.
  [E]'s own ladder is `0.9, 0.875, 0.874`, which is how the ρ at which encirclements first appear is
  read off one card rather than off three runs.
- **θ step** is the angular step of both grids, in degrees, and the line beside it counts the
  terminations the card is asking for before anything is computed — each is a rank-1 update per
  frequency, so halving the step quadruples a two-sided sweep.
- **passive** appears only for `NDFenc`: the passivated run the NDF is taken against, named as a cube
  in the same source (`SP2.wsp`). Blank reads the `wsp_passive` beside this group's own `wsp`.

The pulled probe must sit **directly at its `Term`** with the named side facing it — §9's own
precondition, checked against the run's `__WspTermZ` metadata. A probe with feedback across it fails
it, and the card shows the library's `wsprobe.envelope-probe-not-at-termination` sentence on its
error line rather than drawing a curve computed from a shunt update that is not the physics.

**The cube has four grid axes, always all four**: `rhoS`, `thetaS`, `rhoL`, `thetaL`, plus `freq` for
the two loci. A side that is off contributes two length-1 axes rather than disappearing, so the rank
is constant and a slice authored against it survives an edit to either ladder. **θ is carried in
DEGREES**, not as an ordinal — `SMenv` against `θS` with one curve per `θL` (or per rung) is [E]
Fig. 6–9, and an index axis could not be read that way. `SMenv` carries the margin's own two
reference lines, because it is the margin.

**Not built: the θS × θL grid as a COLOURED map.** `unstable` is a real number per termination and is
drawn through the ordinary family mechanism (one curve per `θL`); a filled raster of the grid would
need the Data Display's heatmap fill, which `brief-dd-loadpull-contour-ux-round8` §3 deliberately
withholds from the UI as experimental. The numbers are all there, and the readout names the first
flagged termination and how many of them there are.

**The reduced two-port and the pair blocks are virtual NETWORK groups.** `DataSourceView` adds
`<analysis> ▸ WSProbe <label> ▸ reduced 2-port` carrying `S`, `Y`, `Z` and a per-port `Z0` of the
reduction at that probe (Eq. 44), so µ, µ′, K, |Δ|, MAG/MSG and both stability circles apply to it
through the code that already computes them. It is appended AFTER the analysis groups, because
`FindCubeSpec` answers with the first group carrying an `S` and that has to keep being the run's own.

The same mechanism carries R-wsp4-6's second half:

- `<analysis> ▸ WSProbe A→B ▸ inner block` / `▸ feedback block` — `wsp_block_calc`'s `{1..4}` and
  `{5..8}` (Eq. 142/143);
- `… ▸ inner block (design)` / `▸ feedback block (design)` — `wsp_block_design` and `wsp_fb_design`,
  the blocks "as if broken out and terminated with your target loadlines" (E.8/E.9);
- `<analysis> ▸ WSProbes A, B, C ▸ [Y]` — `wsp_ymatrix` over an ordered probe set (Eq. 185).

**The pair blocks arrive when the pair is PICKED, not eagerly.** N probes have N(N−1) ordered pairs
and four blocks each; materializing them all would be thousands of cubes nobody asked for on the
matrices §8's NDF work contemplates. The arrow is part of the identity — `A→B` and `B→A` bracket two
different two-ports, because Fig. 40's orientation is GEN → LOAD. The full probe set's `[Y]` is the
one set that needs no picker and is materialized eagerly; any subset arrives from the card's own
ordered set, and its order is part of its identity too.

**The `plot` verb** takes the same trace: `--trace cube=SP1.wsp,probe=GATE,metric=invH0,y=polar`,
with `with=`, `set=A;B`, `z0=`, `side=G|L` and `gi=` for the metrics that read them, and
`src=`/`load=`/`gammaS=`/`gammaL=`/`theta=`/`passive=` for the envelope. It writes the same `.cdd`
the window writes, so the byte-identity gate covers probe traces with no new plotting path.

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
- **A WSProbe node:** pick the run's `▸ WSProbe` group, then the quantity; the section below chooses
  which probe it is taken at. `1/H0` and `1/Y0` on a Polar plot are the document's own stability
  reading, and the card reports the crossings it finds beside them.

---

## Related design docs
`docs/design/plot-versus.md` (the `vs` separator and the vs-X row),
`docs/design/results-dataset-layout.md` (grouped run.npy), `docs/design/measurements.md`
(`V(...)`/`I(...)`/`S(...)` accessors), `docs/design/data-display.md` (plot types, renderers).
