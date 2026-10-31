---
title: File Formats
slug: reference/file-formats.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > File Formats
lede: circuitRF stores a project as a folder of small, human-readable, text files — diffable and version-control friendly. The core idea: the **schematic** (what you draw) is the source of truth; the **netlist** (what the engine runs) is derived from it.
keywords: file extension, cws, cwsuser, csch, csym, clay, ctech, cem, cnl, ccell, cdd, format, on disk
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#principle">Schematic vs. netlist</a></li>
      <li><a href="#family">The file family at a glance</a></li>
      <li><a href="#hierarchy">Workspace › Library › Cell</a></li>
      <li><a href="#cwsuser">Your own view of a workspace</a></li>
      <li><a href="#testbench">What makes a TestBench</a></li>
      <li><a href="#netlistcnl">The generated <code>netlist.cnl</code></a></li>
      <li><a href="#rules">Shared rules</a></li>
    </ol>
  </nav>

## Schematic vs. netlist {#principle}

Two complementary artifacts, related one way at the engine boundary:

```text
  .csch  (schematic: placement, wires, labels, canvas objects, view state)
     │  net extraction  — headless, deterministic
     ▼
  design model  ≡  what a .cnl represents
     │
     ▼
  engine → DataSet (results)
```

- **`.csch`** — the schematic: where each component sits, how wires route, net labels, junction
  dots, canvas objects, zoom/pan. It carries *no* elaborated netlist, matrices, or results. This is
  what you edit and save.

- **`.cnl`** — the netlist, and *only* the netlist: components, parameters, nets, variables,
  analyses, measurements. A hand-authored `.cnl` and one produced by extracting a schematic are the
  same kind of artifact — a pure netlist the engine consumes. See the [Netlist format](netlist.html)
  chapter.

A hand-authored `.cnl` with no `.csch` is fine — it simulates, it just has no drawing to edit
visually until you draw one.

## The file family at a glance {#family}

<table>
    <thead><tr><th>Extension</th><th>What it is</th></tr></thead>
    <tbody>
      <tr><td><code>.csch</code></td><td>Schematic view — placement, wires, labels, canvas objects, analyses, measurements.</td></tr>
      <tr><td><code>.csym</code></td><td>Symbol view — a cell's glyph: drawing primitives + pins mapped to ports. See the <a href="symbol-editor.html">Symbol Editor</a>.</td></tr>
      <tr><td><code>.ccell</code></td><td>Cell manifest — the cell's declared parameters + defaults, which view is primary per type, and the <code>IsTestBench</code> flag.</td></tr>
      <tr><td><code>.clay</code></td><td>Layout view — the cell's physical geometry: shapes on layers, instances, PCell placements, bitmaps, the display unit and the snap grid. A real, first-class view; see <a href="layout-editor.html">The Layout Editor</a>.</td></tr>
      <tr><td><code>.cdd</code></td><td>Data Display config — placed plots/tables/contours, their binding to a run's results, markers, view state.</td></tr>
      <tr><td><code>.cnl</code></td><td>Netlist — the engine's input (derived from a schematic, or hand-authored).</td></tr>
      <tr><td><code>.cws</code></td><td>Workspace config — the top-level "what am I working on" document. References libraries, known files, the default technology and assembly rules, and any other projects or kits this one uses.</td></tr>
      <tr><td><code>.cwsuser</code></td><td>Your own view of a workspace — the panel arrangement, which tabs were open, which tree categories you expanded, and the colour theme. Sits beside the <code>.cws</code>. Optional, personal, and safe to delete; see <a href="#cwsuser">Your own view of a workspace</a>.</td></tr>
      <tr><td><code>.clib</code></td><td>Library manifest — name, version, metadata (cells are discovered by scanning, not listed here).</td></tr>
      <tr><td><code>.ccolor</code></td><td>Color theme — a named light+dark palette for rendering.</td></tr>
      <tr><td><code>.ctech</code></td><td>Technology — the layer table (GDSII layer/datatype pairs, colours, purposes), the substrate stackup, the DRC rules, and the default display unit and snap grid. Shared at workspace level: every layout in the workspace resolves against one of these. See <a href="layout-editor.html#technology">Technology</a>.</td></tr>
      <tr><td><code>.cmat</code></td><td>Material library — named materials (εr, tanδ, μr, σ₂₀, α₂₀, the thermal conductivity, density and specific heat, σ(T) and k(T) tables, and where each value comes from) in exactly the form a technology's own Materials list takes. A technology names its libraries, relative to the <code>.ctech</code>; several technologies may share one. It opens as its own document, and is also edited from the technology editor's Materials tab. circuitRF ships <code>generic-materials.cmat</code>, and New Workspace copies it beside the technology. See <a href="materials.html">Materials</a>.</td></tr>
      <tr><td><code>.c3d</code></td><td>3D view — a cell's solid model: boxes, prisms, cylinders, sheets, polyhedra, bond wires, the kernel operations below, placed cells, ports, face boundaries, VARs and any number of embedded EM setups. See <a href="drawing-in-3d.html">The 3D Editor</a> and <a href="#c3d-operations">The 3D view's operations</a>.</td></tr>
      <tr><td><code>.cem</code></td><td>EM setup — one electromagnetic run's configuration: which layout, the stackup mapping, the ports and their reference impedances, the mesh settings, the frequency plan and the de-embedding choice. See <a href="em-setup.html">EM Setup</a>.</td></tr>
      <tr><td><code>.charm</code></td><td>harmonicaRF document — the DUT, the source and load termination planes at every harmonic, the package, the display configuration and the markers. See <a href="harmonicarf.html">harmonicaRF</a>.</td></tr>
      <tr><td><code>.wBond</code></td><td>wBond design — bondwire geometry: the wires, their arrays and profiles, the substrate and the solver settings. Self-contained and shareable. See <a href="wbond.html">wBond</a>.</td></tr>
      <tr><td><code>.crail</code></td><td>railRF document — the rails, their sources, loads and reference, the parts and the part library they resolve against, the target and the band. See <a href="railrf.html">railRF</a>.</td></tr>
      <tr><td><code>.cmodel</code></td><td>Composed FET model — a nonlinear transistor model placed as a <code>ComposedFET</code>: its extrinsic network, its current and charge constituents, the finger count and gate width it was measured at, the temperatures it is valid at, its symbol and where it came from. A ZIP holding a readable <code>model.json</code> and any arrays as <code>.npy</code>. See <a href="cmodel.html">Writing a .cmodel</a>.</td></tr>
      <tr><td><code>.cmc</code></td><td>Model Composer document — the data a model is built from and the conditions it was measured under, from which a <code>.cmodel</code> is exported. JSON text; its data stays in the files it names, with a cache beside it in <code>&lt;name&gt;.cmc-cache/</code>. See <a href="#cmc">The Model Composer document</a>.</td></tr>
      <tr><td><code>.csmith</code></td><td>Smith Chart document — a generator impedance over frequency and an ordered cascade of two-pin elements, plus what the chart is normalized to, the overlays, the markers and the constant-Q setting. Numbers are in base SI. See <a href="smith-chart.html">Smith Chart</a>.</td></tr>
    </tbody>
  </table>

<p class="small">These are circuitRF's own formats. The <strong>interchange</strong> formats it reads
and writes — GDSII, DXF, Gerber plus Excellon drill, and <code>.kicad_pcb</code> board files — are not
in this family and are not documents you open: they are imported into cells, or exported from them. See
<a href="layout-editor.html#interchange">Interchange</a> for what each carries and what it cannot. The
same goes for <strong>STEP</strong> (<code>.step</code>, <code>.stp</code>), the solid-model interchange
format: its parts are imported into a 3D view, and a 3D view or a layout is exported as one — see
<a href="drawing-in-3d.html#step">The 3D Editor ▸ STEP</a>.</p>

<p class="small">Results are not documents either. A run writes <code>.npy</code> (see
<a href="npy-export.html">.npy export</a>), and the Data Display, <code>circuitrf read</code> and
<code>circuitrf plot</code> read <code>.npy</code>, Touchstone <code>.sNp</code>, the loadpull
<code>.spl</code> and <code>.lpcwave</code>, and <strong>binary simulation datasets</strong>
(<code>.ds</code>): blocks of nested sweeps, read as one group per block named after its analysis
(<code>SP1</code>), each variable a cube on the block's sweep axes. Only a full grid is read. Export Data
and a run verb's <code>-o out.ds</code> write one. Try <code>circuitrf read sweep.ds</code>.</p>

<div class="callout note">
<span class="label">Which of these are documents you open</span>
<p><code>.csch</code>, <code>.csym</code>, <code>.clay</code>, <code>.cdd</code>, <code>.ctech</code>,
<code>.cem</code>, <code>.charm</code>, <code>.wBond</code>, <code>.crail</code> and
<code>.csmith</code> each open as a tab in the workspace.
<code>.ccell</code>, <code>.clib</code>, <code>.cws</code> and <code>.cwsuser</code> are manifests the
application maintains for you — though double-clicking either half of a workspace opens the whole
workspace. <code>.cnl</code> is the engine's input and is normally derived rather than edited.</p>
</div>

## A layout's review state {#clay-review}

A `.clay` carries the [Layout Review](layout-editor.html#layout-review)'s state beside the artwork, and
both keys are left out of the file until there is something to say:

| Key | Holds |
|---|---|
| `Review` | `Target`, `"Mmic"` when the layout is reviewed as a die (absent: a board); `Disabled`, the ids of the checks turned off; and `Thresholds`, each threshold changed from its default, keyed `<check>.<threshold>` (`"edge-copper.pullback": 300000`) &mdash; a length in the layout's database units, an area in mm² (whatever unit it is shown in), a number of substrate heights and a count as the number itself. A threshold set back to its default is removed. A check or threshold id this version does not know is kept as it is. |
| `ReviewAcceptances` | The findings accepted, each with `Key`, `CheckId`, `Reason` (required), `AcceptedUtc`, `LayerName` and `Summary` (the finding's sentence when it was accepted). `Key` is `<check>\|<layer>/<datatype>\|<minX>,<minY>,<maxX>,<maxY>`: the check, the finding's layer and its marker's bounding box in database units, exactly as `circuitrf review --json` reports it. Moving the marked copper changes the key, and the acceptance is then listed as stale. |

## The 3D view's operations {#c3d-operations}

A `.c3d` holds four kinds of object that the geometry kernel (OpenCASCADE, which ships inside circuitRF)
builds rather than the editor. `circuitrf reference 3d-view` prints every field; these are the rules a
hand-written or scripted file must follow.

| `$type` | What it is | Its operands |
|---|---|---|
| `Boolean` | `Op` — `Subtract`, `Unite` or `Intersect` — of one `Blank` and one or more `Tools`. `KeepTools` (Subtract only) also keeps each Tool as a solid of its own, right after the result. | `Blank`, `Tools` |
| `Fillet` | The named `Edges` of its `Target` rounded by `Radius`. | `Target` |
| `Chamfer` | The named `Edges` of its `Target` cut by `Distance` (and `Distance2` along the edge's second face). | `Target` |
| `Step` | One solid part of a STEP file in the cell's `3d/` folder: `File`, `Part` (its occurrence path in the file's assembly, `1/2`), `Hash`, the file's `Unit`, and the `SourcePath` it was imported from. | none |

- **An operation takes its operand's name.** A Boolean is its Blank to the rest of the document, a Fillet
  or Chamfer its Target: the object inside has no `Name`, and a port or a boundary written against the
  name before the operation existed still lands after it. A Tool keeps its own name, unique across the
  document. Operations nest — a Blank may itself be a Boolean, a Target a Boolean result.
- **`"Enabled": false`** makes an operation as if it were not there: its operands elaborate as ordinary
  objects, the Blank or Target under the operation's name. It is written only when false.
- **Face names.** A Blank's faces keep their bare names (`zmax`); a Tool's are `<tool>:<face>`
  (`bore:side`); a face the operation cut in pieces is `zmax#1`, `zmax#2`, and a reference to `zmax`
  covers every piece. A fillet's new face is `fillet(<edge>)`, a chamfer's `chamfer(<edge>)`. A STEP
  part's faces are `face<n>`, numbered as the file's shape numbers them.
- **Edge names** are the two faces an edge separates, in order: `xmax|zmax`, `side|top`,
  `bore:side|zmax`. Two separate runs between the same pair are `a|b|1`, `a|b|2`, numbered in the
  object's own frame. An edge whose face has gone is refused by name, never moved to a nearby edge.
- **The `Hash` rule.** `Hash` is `sha256:<hex>` of the STEP file's bytes, recorded at import. A part's
  `face<n>` names mean something only for those bytes, so a `Step` object whose file no longer hashes to
  its `Hash` is an error, naming the part and the file (`check` reports it), and **Reload from Source** —
  which re-matches every reference by geometry — is how a changed file is taken in on purpose. The file itself is the import's own copy, beside
  the `.c3d`; the source it came from is only remembered.
- **Without the kernel** a 3D view holding any of these — even disabled — is not opened, and `check`
  reports one error per object; see [The 3D Editor](drawing-in-3d.html#operations).

## The composed FET model {#cmodel}

A `.cmodel` is a ZIP container. `model.json` is the model itself, readable as text; `arrays/<name>.npy` holds any
array it needs (a table, a network's weights), in circuitRF's own `.npy` layout. It holds the extrinsic network, the four
constituents `Id`, `Ig`, `Qg` and `Qd` (each an equation, a table or an ANN), the periphery and temperatures it
describes, and optionally self-heating, traps and dispersion. The Model Composer writes it, and so can a script of your
own: [Writing a .cmodel](cmodel.html) gives every field, the arrays' layout, and complete Python and MATLAB scripts.
`circuitrf check` reads a `.cmodel` and names the field of anything it refuses.

## The Model Composer document {#cmc}

A `.cmc` is JSON text. It names the data a model is built from and the conditions each set was measured
under; it never holds the data itself. Numbers are in base SI — volts, amps, seconds, ohms, hertz —
except temperatures, in °C.

| Field | What it holds |
|---|---|
| `FormatVersion` | `1`. A file from a newer circuitRF is refused, saying so. |
| `Kind` | `Active` (a FET) or `Passive` (an N-port). |
| `BasePeriphery` | `Fingers` and `UnitWidth` (`"125 um"`) of the measured device. `Active` only. |
| `DataSets` | Each with a `Name`, a `Kind`, its `Sources`, its `Conditions`, its `Import` settings and any `Exclusions`. |
| `Pads` | How the probe pads are removed — see below. Absent when the data is at the device plane. |
| `Network` | The six-pin extrinsic network and its variables' settings — see below. Absent when the data is intrinsic. |
| `Steps` | Each model step's settings and result: `Current`, `Charge`, `Thermal` and `Traps` below. A step not described here is kept as written. |
| `Panels` | `Hidden`: the window panels switched off. Absent when every panel shows. |

A data set's `Kind` is `DcIv`, `PulsedIv`, `SParams` (CW multi-bias), `PulsedSParams`, `SimulatedTables`
(currents and Y, S or C on a bias grid, from another simulator), `LargeSignal` (validation only) or
`PassiveSParams`. Its `Sources` are paths relative to the `.cmc`.

| `Conditions` field | What it holds |
|---|---|
| `AmbientC` | The chuck temperature, °C. |
| `ReferencePlane` | Where the bias voltages were measured: `instrument`, `probe`, `device` or `intrinsic`. `BiasPathOhms` (`Gate`, `Drain`, `Source`) goes with `instrument`. |
| `PulseWidth`, `Period`, `SampleDelay` | Pulsed sets: the pulse, its repetition and when in the pulse the sample is taken, in seconds. |
| `Quiescent` | Pulsed sets: `Vgs`, `Vds` and optionally `Ig`, `Id` of the bias the pulses start from. |
| `PrePulse` | Double-pulse sets: `Vgs`, `Vds`, `Width`, `Gap` (from its end to the measuring pulse) and optionally `Id`. A pre-pulse whose current dissipates is a warning: its heating is not modelled. |
| `TrapState` | Pulsed sets: `auto`, `setting` or `extreme`. |
| `SoakTime`, `Compliance`, `Note` | The soak before measuring, the instrument's current limits (`Gate`, `Drain`, in A) and a note. |

`Import` says how the sources were read, so a re-import is exact:

- `touchstone` — one file per bias. `Pattern` reads the bias from the file name (`PatternSource`
  `name`) or its comment lines (`comment`): literal text with the fields `{vgs}`, `{vds}`, `{T}`, `{id}`
  and `{ig}`, each optionally with its unit, `{id:mA}`. A space matches any run of spaces and `*`
  matches anything. `Vgs{vgs}_Vds{vds}` reads `Vgs-1.5_Vds10.s2p`. A `PassiveSParams` set needs no
  pattern; a family over a geometry value reads it with `{geometry}`: `spiral_{geometry}t` reads
  `spiral_2.5t.s2p`, and `{geometry:um}` a length in micrometres.
- `csv` — CSV or TSV. `Columns` maps a column (its header, or its number from 1 with `"Header": false`)
  to `vgs`, `vds`, `ig`, `id`, `T`, `vgsi` or `vdsi`, with the `Unit` its numbers are in. A cell that
  carries its own unit (`12 mA`) uses it.
- `npy`, `mat`, `npz` — named arrays in circuitRF's `.npy`, a MATLAB `.mat` (version 5 or 7.3) or a NumPy `.npz`.
  `Arrays` maps the quantities above, `freq`, a 2-port table `S` or `Y` (or its four elements `S11`…`S22` or
  `Y11`…`Y22`, all four together), and the capacitances to array names; a struct's field is `s.field`.
  `ArrayUnits` gives a quantity whose array is not in SI its unit, `{"freq": "GHz", "id": "mA"}`; `T` takes `C` or
  `K`. The bias arrays are one value per point, or the axes of a grid vgs × vds or vgs × vds × T, and every other
  array's axes may come in any order that fits their lengths
  ([Model Data](model-data.html#format-arrays), `circuitrf reference model-data`). A Y table is converted
  to S at `Z0` (default 50 Ω); `FrequencyHz` gives a table with no frequency array its frequency.
  A `SimulatedTables` set's capacitances, in farads, are either the matrix `C` shaped `[point, 2, 2]`, with
  `Cij = ∂Qi/∂vj` (gate 1, drain 2, so `C12 = −Cgd`), or the circuit set `cgs`, `cgd` and `cds` together, and
  optionally `cm`, one value per point; not both. Either is read at the `intrinsic` reference plane only.

`Exclusions` lists the points left out of every fit — `Point`, a point's id, and the `Reason`. A
Touchstone point's id is its file; a CSV row's is `<file>:<line>`; an array point's is `<file>#<index>`.

`Pads` has a `Kind` and the files that kind needs, relative to the `.cmc`:

- `OpenShort` — `Open` and `Short`, the open and short dummy structures as 2-port Touchstone files.
- `TwoPorts` — `Gate` and `Drain`, a Touchstone 2-port per side, port 1 at the probe and port 2 facing
  the device.
- `Network` — `Gate` and `Drain`, a schematic per side with pin 1 at the probe and pin 2 facing the
  device, returning through ground. Series resistance in one also drops the bias voltage.

A pad file must cover the data's frequencies; one that does not is refused, naming both ranges.

`Network` holds:

| Field | What it holds |
|---|---|
| `Schematic` | The network, a `.csch` with exactly six pins: 1, 2, 3 the extrinsic gate, drain and source, 4, 5, 6 the intrinsic ones. Its VAR block's variables are its element values. |
| `Variables` | Per variable: `Min`, `Max`, `Lock` (`true` holds it at its value), `Tie` (`To` another variable, times `Factor`), `Scaling` (`fixed`, `perFinger` or `perFingerInverse`) and `Value`, the value in use in base SI — typed and locked for a fixed value, or written by a fit. Absent: the schematic's own value. |
| `FlatnessBand` | `[low, high]` in Hz: the band the frequency flatness of the intrinsic elements is measured over. Absent: 1/10 to 1/2 of the data's highest frequency. |
| `Fit` | How the network is fitted — see below. Every field is optional. |
| `Result` | What the last fit found: `Method`, `Cost`, per fitted variable its `StdError`, `Spread` across the starts and `AtBound` (`Min` or `Max`), the `Report` lines, and a `Digest` of the values. Written by the fit. |

`Fit` holds:

| Field | What it holds |
|---|---|
| `Method` | `MultiBias` (the default), `ColdFet`, or `ColdFetThenMultiBias` — the cold-FET values as the start of the multi-bias fit. |
| `PinchOff`, `Forward` | The cold points, each `{ "Set", "Point" }`. Absent: chosen at Vds = 0 V — the lowest Vgs that is pinched off, and the highest with the gate conducting. A cold-FET method without one stops, naming the bias it needs. |
| `ForwardVariant` | `Extreme` (the default: the device collapsed to a node by strong forward gate current) or `Moderate` (a gentler forward bias, with the channel resistance). |
| `ChannelResistance` | Ω, for `Moderate`: the channel resistance, from another test structure. |
| `ColdBand` | `[low, high]` in Hz for the cold points. Absent: every frequency. |
| `Active` | The active points of the multi-bias fit, a list of `{ "Set", "Point" }`. Absent: one off, one on, one near the knee and one in saturation, chosen from the data and written here after a fit. |
| `Weights` | `Flatness` (1), `Cold` (1) and `SmallSignal` (0): the frequency flatness of the intrinsic elements at the active points, the cold-FET models at the cold points, and S against a fitted small-signal circuit. |
| `Starts`, `Seed`, `Algorithm`, `Budget` | The global search: 4 seeded starts (seed 1) of `cmaes` or `de`, each spending `Budget` evaluations (200 per variable) before a gradient polish. The same seed gives the same values. |
| `PoorRelativeError`, `PoorCorrelation` | A value whose relative error exceeds the first (0.2), or whose correlation with another exceeds the second (0.95), is reported as poorly determined, with a lock or a tie suggested. |

R, L and C values stay at or above zero by default, a mutual's coupling stays below 1, and a value that ends on a
bound is reported as at that bound.

A stock network — GaN two-shell, classic single shell or series only — is copied beside the `.cmc` when
chosen and is then an ordinary schematic. An R, L, C or mutual value starts with `Min` 0 and `Max` 100 times
its starting value.

`Steps.Current` is the drain and gate currents' step: `Settings`, the `Result` of the last fit, and `Network`, a
digest of the network values the fit was made through (a later network fit makes it stale). `Settings` holds:

| Field | What it holds |
|---|---|
| `DataSets` | The I–V sets to fit to. Absent: every `DcIv`, `PulsedIv` and `SimulatedTables` set. If pulsed they must share one `Quiescent` and `PrePulse`, unless `Traps` models trapping; a mix is refused, naming what differs. With traps each point takes the trap state `Steps.Traps` maps it to, as further inputs of `Id`. At two or more temperatures the currents take the temperature `T` as an input: each point at its chuck temperature, a DC point at its junction temperature when `Thermal` models self-heating. |
| `Id`, `Ig` | Each `Method` (`ANN`, the default, `Table` or `Equation`) and that method's settings: `Hidden` (`[8, 8]`), `Activation` and `MaxIterations` (6000) for a network; `GridStep` (0.1 V), `Regrid` (`ANN`, or `RBF` through the points themselves), `Smoothing` and, with `T` an input, `TemperatureStep` (25 °C, which needs `Regrid: ANN`) and with traps `TrapStep` (1 V) for a table; `Template` or the user's own `Expression` (`Id`) or `IgGs` and `IgGd` (`Ig`), with `Parameters` (`Value`, `Min`, `Max`, `Lock` each), `TemperatureCoefficients` (template parameters made linear in `T`; absent is the template's current scale with data at two or more temperatures, `[]` is none) and `Algorithm` (`cmaes`, `de` or `lm`) for an equation. The templates are `Angelov`, `CurticeQuadratic`, `CurticeCubic`, `Statz` and `Materka` for `Id`, and `Diode` for `Ig`. |
| `IdFloor` | 0.1: `Id` is fitted to each point's own value down to this fraction of the largest current, and to that floor below it. |
| `IgFloor` | 1e-4: `Ig` is fitted to the same ratio everywhere above this fraction of the largest gate current (`I0` of its `Transform`). |
| `GateSplit` | `Sign` (the default): at `vds ≥ 0` a forward gate current is the gate–source branch and a reverse one the gate–drain branch, the other way round at `vds < 0`. `GateSource` or `GateDrain` puts all of it in one branch. |
| `Symmetry` | `Exchange`: fit `vds ≥ 0` only, for a model with drain–source symmetry. |
| `SmallestSwing` | 0.05 V: a table whose step is more than twice this warns that low-level intermodulation is unreliable. |
| `Seed`, `Restarts`, `HeldOutFraction` | 1, 3 and 0.1: the same seed gives the same model; each network is trained from that many starts; that fraction of the points, spread over the domain, is never trained on and reports the error. |
| `Dispersion` | Present to fit `IdRF`: `Tau` (1e-6 s), `DataSets` (absent: every S-parameter set), `Reference` (`[vgs, vds]` where `IdRF` equals `Id`; absent: the S point nearest the middle) and `Hidden`. |

`Result` holds the fitted constituents exactly as the `.cmodel` will (`Constituents`), their `Gx`, `Tau`, and
`Arrays` — the name of the cache file holding their arrays — then per current the training and held-out RMS
error, absolute and relative to the range (`Errors`), `Conductance` (the fitted Gm and Gds against the data's
own DC derivatives), `Symmetry` (with `Exchange`: the value, slope and gate jumps at `vds = 0`), `Eq649` (the
share of the RF conductances no single current reproduces), `Reference`, with traps the `Traps` block exactly as the
`.cmodel` will carry it and `DcIv` (the DC I–V's RMS error at its DC trap states: `Count`, `Rms`, `Relative`), and the
`Report` lines.

`Steps.Charge` is the gate and drain charges' step, with the same three fields. The charges are fitted to the
intrinsic capacitances of the S-parameter sets, averaged over the `FlatnessBand`. A document with no S data has none to
fit: the step writes `Linear` charges from `Typed` instead. `Settings` holds:

| Field | What it holds |
|---|---|
| `DataSets` | The S-parameter sets to fit to. Absent: every set with S data. One trap state, as for the currents, unless `Traps` models trapping: then an `ANN` `Qd` takes the trap states when pulsed S-parameters give each a level, and `Qg` never does. At two or more temperatures an `ANN` charge takes `T` as an input; any other method is fitted temperature-independent. |
| `Qg`, `Qd` | Each `Method` (`ANN`, the default, `Table` or `Equation`) and that method's settings: `Hidden` (`[8, 8]`), `Activation` and `MaxIterations` (3000) for a network, trained on the charge's two capacitances; `GridStep` (0.1 V), `Interpolant` (`ANN`, or `RBF` through the points themselves), `Path` (`A`, `B` or `Mean`, the default) and `Smoothing` for a table integrated from the capacitances; `Template` (`Depletion`, `Angelov` or `Linear`) or the user's own `Expression`, with `Parameters` and `Algorithm`, for an equation. |
| `Anchor` | `[vgs, vds]` where both charges are zero. Absent: `[0, 0]`, or the data point nearest it when it lies outside the data. |
| `FlatnessLimit` | 0.1: a point whose `C11` or `C22` varies across the band by more than this fraction of its mean counts for less. |
| `Seed`, `Restarts`, `HeldOutFraction` | 1, 3 and 0.1, as for the currents. |
| `Drift` | Present to fit the drift decomposition instead of `Qg` and `Qd`'s methods: `Lambda` (0), `VdsZero` (0.05 V — the points this close to `vds = 0` fit the depletion charges), `Hidden` and `MaxIterations`. Needs the current step: the drift charge reads its `Id`. |
| `Typed` | Constant capacitances in farads for a document with no S data: `Cgs`, `Cgd`, `Cds` and `Cm`, each 0 when absent. `Cgs`, `Cgd` and `Cds` are never negative; `Cm`, a transcapacitance, may be. A datasheet gives `Cgd = Crss`, `Cgs = Ciss − Crss` and `Cds = Coss − Crss`. They become `C11 = Cgs + Cgd`, `C12 = −Cgd`, `C21 = Cm − Cgd` and `C22 = Cds + Cgd` of the `Linear` charges. Absent with no S data: all four are 0. Present beside S data: the step refuses, since typing would discard the data. |

`Result` holds `Constituents` (`Qg` and `Qd` exactly as the `.cmodel` will), `Arrays`, `Anchor`, `Errors` (per
capacitance `C11`, `C12`, `C21`, `C22`: the RMS error of the fitted charge's derivative, and that as a fraction of
the capacitance's range), `PathDifference` (a table's two integration paths' RMS difference as a fraction of the
charge's range), `CdsSpread` (with `Drift`: how much `Cds` varies across `vgs` at a fixed `vds`, which `QDS` cannot
follow), with a trapped `Qd` the `Traps` block, `Points` (the number of S biases the charges were fitted to), `Typed`
(the four capacitances the charges were written from, when there was no S data), and the `Report` lines.

`Steps.Thermal` is temperature and self-heating: `Settings` and the `Result` of the last run. With the data at one
temperature the model is valid at that temperature only and self-heating is unavailable. `Settings` holds:

| Field | What it holds |
|---|---|
| `SelfHeating` | `true` to give the model a thermal node, and to put each DC point at its junction temperature. Needs two or more temperatures. |
| `Rth` | A typed thermal resistance in °C/W. Absent: extracted from pulsed I–V at two or more chuck temperatures and DC I–V at the same biases. |
| `RthModel` | `Constant` (the default) or `Linear`: an extracted thermal resistance that varies linearly with the ambient. |
| `Cth`, `TauTh`, `Ladder`, `FitTau` | At most one: a thermal capacitance in J/°C; a thermal time constant in s; a ladder of `{ "Fraction", "Tau" }` poles whose fractions of `Rth` add up to 1; or `true` to fit the time constant to the low-frequency roll-off of the S data's Re Y22, which is refused while traps are modelled. |
| `PulsedSets`, `DcSets` | The sets the extraction reads. Absent: every `PulsedIv` set whose quiescent bias dissipates nothing, and every `DcIv` set. |
| `MinPower` | 0.1: DC points dissipating less than this fraction of the largest DC power are left out of the extraction. |
| `Isothermal` | How the isothermal drain current is built, with the same fields as the current step's `Id`. Absent: the current step's own. |
| `TauSets` | The S-parameter sets `FitTau` reads. Absent: every `SParams` set. |

`Result` holds `Temperatures` (the data's), `Rth`, `RthSlope` and `Tref`, `RthSource` (`Extracted` or `Typed`),
`Poles` (the thermal network's `Rth` and `Cth`, absent until its dynamics are set), `TauTh`, `Extraction` (the fit's
residual in K and relative to the temperature rise, the points used, those left out for low power and those
unresolved, and the vgs, vds and power ranges of the points used), `TauFit` and the `Report` lines.

`Steps.Traps` is trapping: `Settings` and the `Result` of the last run. The `Trained` coupling needs pulsed I–V; the
`Shift` coupling does not, and maps no data to trap states. A pulsed set's trap state
is set by its `PrePulse` level when it has one, else its `Quiescent` bias. A point pulsed toward that bias — no lower
in `vgs` for gate-lag, no higher in `vds` for drain-lag — is at that bias's trap state; one pulsed beyond it is placed
by the set's `TrapState`: `auto` (the default) uses it to check the model only, `setting` puts it at the bias's trap
state, `extreme` at the pulse's own. DC points are at their DC state. A state trained from one trap level is used
with a warning; one with none cannot be enabled. `Settings` holds:

| Field | What it holds |
|---|---|
| `Model` | `None` (the default), `CaptureEmission` or `Custom`. |
| `Coupling` | `Trained` (the default) or `Shift`. Under `Shift` the current step fits a trap-free `Id` to the DC I–V and this step fits the strengths. |
| `States` | The enabled states. Absent: drain-lag (`phi2`) for `CaptureEmission`, every state with expressions for `Custom`. |
| `Emission` | `Constant` (the default) or `ThermallyActivated`, which needs each enabled state's `Ea`. `CaptureEmission` only. |
| `Parameters` | The model's parameters, as the `.cmodel` names them. Absent ones take their defaults: capture 10 ns, emission 1 ms, `Xref` 1 V, `Vc` 50 mV, `Tref` 25 °C. Under `Shift`, a `ShiftDrain` or `ShiftGate` here is typed and is not fitted. |
| `Expressions` | `Custom`: per state, `{ "Dynamics": …, "Dc": … }`. |
| `DcIv` | `Target` (the default): the DC I–V trains the model. `Validation`: it is held out, and only checks it. |

`Result` holds `Levels` (per state, the trap-setting levels in the data, V), `Beyond` (per state, the pulsed points
beyond their bias used only to check the model) and the `Report` lines. Under `Shift` it holds `Coupling: "Shift"`,
`Strengths` (per strength, `{ "Value", "Error" }` in V/V; a typed strength has no `Error`) and `Residual` (`Points`, the
number of S biases fitted, and `Gm` and `Gds`, the RMS residuals in S of `gm_RF = (1 − ShiftGate)·gm` and
`gds_RF = gds + ShiftDrain·gm`) instead of `Levels` and `Beyond`.

`Steps.Passive` is a `Passive` document's model: `Settings` and the `Result` of the last fit. `Settings` holds:

| Field | What it holds |
|---|---|
| `Set` | The passive data set. Absent: the document's only one. |
| `Block` | The block one fit reads, by its source. Absent: the set's only one. Not with `Family`. |
| `Topology` | A library topology — `series-rlc`, `parallel-rlc`, `inductor-skin`, `capacitor-esr-esl`, `resistor-parasitics`, `pi`, `tee`, `bond-wire`, `via`, `spiral`, `mim`, `tline-pads` — or a `.csch` relative to the `.cmc` whose pins 1…N are the ports. |
| `Variables` | Per variable `Min`, `Max`, `Lock`, `Tie` and `Value`, as the network block has them. Bounds default to 0 … 1000× the starting value. |
| `Fit` | `Weighting` (`Uniform`, or `Band` with `Band` in Hz and `Emphasis`, default 10), `Starts` (2), `Seed` (1), `Algorithm` (`cmaes` or `de`), `Budget`, `ProposalBudget` (40 per variable), `PoorRelativeError` (0.2), `PoorCorrelation` (0.95). |
| `Family` | Fit every block as one model over a geometry: `Geometry` (the parameter's name, e.g. `turns`), `Form` (`Auto`, `Constant`, `Linear`, `Quadratic`, `Cubic` or `Rational`), `Monotone`, `HeldOut` (the geometry checked and not trained on; absent: the middle one). |
| `Export` | `Cell`, `Library` (a folder relative to the `.cmc`) and an optional `Symbol` (`.csym`); absent symbol: a box with N pins. |

`Result` holds the `Topology`, the fitted `Values` (base SI), `Cost`, `RmsDeltaS` and `Aic`, a family's laws
(`Family`: the geometry's range, the held-out geometry and its |ΔS|, and per variable its `Form` and `Coefficients` in
the normalised geometry `(g − Mid)/Half`), and the `Report` lines. The exported cell's parameters are the variables,
defaulting to the fitted values; a family's are the geometry first and every element as an expression of it, with a
warning when a schematic sets the geometry outside the range the family was fitted over.

The data read from the sources is cached in `<name>.cmc-cache/`. When a source changes it is read
again; when one goes missing the cached copy is used, with a warning. A step's arrays are cached there
too, and a missing one is fitted again. The cache can be deleted at any time. `circuitrf check` reads a
`.cmc` and names the field of anything it refuses.

## Workspace › Library › Cell {#hierarchy}

The conceptual hierarchy maps to folders of the small files above — never one monolithic blob.

### Cell = a folder of views

```text
<CellName>/
   .ccell              manifest: parameters + defaults + primary view per type + IsTestBench
   schematic/  *.csch  schematic views
   symbol/     *.csym  symbol views
   layout/     *.clay  layout views
```

A cell need not have every view. A view sub-folder with exactly one file makes that file primary
by default; `.ccell` records the primary when there are several. A placed component references its
cell by **relative path** and resolves its glyph through the cell's primary symbol — so a broken
path shows a "Not Found" glyph, and a resolved cell with a missing primary symbol shows a
plain-rectangle stand-in. The cell folder is the unit you copy to share or reuse a cell.

### Library = a folder of cells

```text
<LibraryName>/
   .clib               manifest: name, version, metadata
   <CellA>/  …
   <CellB>/  …
```

Membership is **filesystem-is-truth** — the Project Tree discovers cells by scanning, so the
`.clib` stays lightweight (no cell index). The standard component libraries ship in this shape.
*File → Add Library* points the workspace at an external library folder.

### Workspace = the project that references the above

A workspace is a **folder** (its name = the folder name) containing a `.cws` file. Membership is
the filesystem; the `.cws` records configuration only: referenced libraries, "Known Files"
bookmarks, the default technology and assembly rules, and the other projects and kits this one
uses. It **references, never embeds** — the same cell or library can be used by multiple
workspaces.

A workspace that references a [vendor part library](pdk-integration.html#vendor) lists it under
`VendorLibraries`, by name and folder; the key is absent when there are none:

```json
"VendorLibraries": [ { "Name": "Caps", "Path": "/home/you/libraries/caps" } ]
```

A placed part names its file as `vendorlib://Caps/<file inside the library>`, so moving the library is
one edit to `Path`.

Beside it sits a second, optional file: the **`.cwsuser`**, which is yours rather than the
project's.

## Your own view of a workspace {#cwsuser}

The `.cwsuser` holds the things that describe **your afternoon rather than the design**: where you
put the panels, which tabs were open and which one was in front, which categories of the Project
Tree you had expanded, and which colour theme you were using. Nothing in it affects a simulation, a
schematic or a layout.

Three things follow, and all three are deliberate:

- **It is optional, always.** A workspace with no `.cwsuser` opens perfectly normally, on the default
  arrangement — exactly like a brand-new one. Nothing is reported and nothing is wrong. This is the
  usual state of a workspace somebody hands you.
- **It does not travel.** It is left out of a workspace **archive**, because your monitor layout is of
  no use to whoever you send the archive to. It **is** carried by *File ▸ Save Workspace As*, because
  that copy is still yours, on the same machine, and the window switches straight to it.
- **Deleting it is a repair, not a risk.** If your panels have ended up somewhere unusable, close the
  workspace, delete the `.cwsuser`, and open the workspace again. You get the default arrangement
  back and lose nothing but the arrangement.

Double-clicking either the `.cws` or the `.cwsuser` opens the whole workspace — they are two halves
of one thing. A `.cwsuser` on its own, with no `.cws` beside it, is not a workspace, and circuitRF
says so rather than opening an empty window.

## What makes a TestBench {#testbench}

A **TestBench is not a separate file type** — it is a cell whose schematic carries
[analyses](simulations.html) and [measurements](measurements.html), making it runnable.
"TestBench" is a role, marked by the `IsTestBench` flag in the cell's `.ccell`, which the Project
Tree uses to show it as runnable. A testbench is authored, saved, and version-controlled exactly
like any other cell.

<div class="callout note">
    <span class="label">Port numbers are 1-based for you, positional in the netlist</span>
    <p>Everything you see — symbol pins, dialogs, error messages — numbers ports from <strong>1</strong> (a VNA
    has port 1 and port 2, never port 0). The <code>.cnl</code> doesn't list port numbers at all: a component
    line names its nets in <em>terminal order</em>, and the engine infers the port from position. Net extraction
    emits nets in the symbol's terminal order — the one seam where the two conventions meet, and a tested one.</p>
  </div>

## The generated `netlist.cnl` {#netlistcnl}

The GUI does not keep per-cell `.cnl` files. When you simulate a TestBench, extraction writes a
single `netlist.cnl` to the workspace root and the engine runs it. It is:

- **Overwritten every run** — one scratch netlist for the latest simulation, whichever testbench
  produced it.

- **Stamped with provenance** — a header comment records which TestBench produced it and when
  (e.g. `; netlist.cnl — generated from TestBench "PA_loadpull" at 2026-06-06T14:22:31Z`).

- **Human-inspectable and re-runnable** — it's exactly what the engine saw, and you can re-run it
  headless from <a href="cli.html">the command line</a>.

It is a generated scratch artifact, not part of the saved project — the `.csch` is the source of
truth.

## Shared rules {#rules}

- **Text, JSON-friendly, human-diffable**, with stable key ordering for clean diffs.

- **Paths, not payloads** — bitmaps store a file path, never pixels; a missing image shows a
  placeholder box, never a crash. Relative paths (under the project) are preferred so a moved or
  shared project keeps its images; absolute paths are accepted.

- **Enums serialize as names**, so files stay readable and stable across versions.

- **Runtime identity is never persisted** — internal object IDs are regenerated on load and carry
  no meaning across sessions.

- **Alpha policy** — a `format_version` is written and rejected on mismatch; there is no silent
  migration. (circuitRF is pre-release; formats may break and regenerate until near release.)

---

<p class="small">See also: <a href="netlist.html">Netlist format</a> · <a href="grid.html">Grid &amp;
  Connectivity</a> · <a href="symbol-editor.html">Symbol Editor</a>. Full design:
  <code>docs/design/project-file-formats.md</code>.</p>
