# circuitRF — Model Composer and the composed FET

**Status:** built, MC-1 … MC-28 — the `.cmodel` format and the `ComposedFET` device (MC-1); table and ANN constituents
with guided extrapolation (MC-2); the `.cmc` document, its data and cache (MC-3); pads, the six-pin network and the
intrinsic data (MC-4); the extrinsic fit (MC-5); the ANN trainer (MC-6); the currents and DC/RF dispersion (MC-7); the
charges and Check Charge Conservation (MC-8); temperature and self-heating (MC-9); traps (MC-10); export, validation and
the Model Validation Report (MC-11); passive models (MC-12); the `model` verb and its MCP tool (MC-13); the window
(MC-14); the steps and their plots (MC-15); the example, the user pages and the series acceptance (MC-16); the later
phases (MC-17 … MC-25) in their own sections; the Verilog-A export, its Export step button, verb, MCP path and example
bench (MC-26 … MC-28, §25). §20 records
every deviation from the series as built, §21 the open items and §22 the example and the acceptance ·
**Source:** `docs/sonnet-briefs/brief-modelcomposer-0-overview.md` (decisions D1–D16) and the phase briefs
`brief-modelcomposer-1` … `-28` · **Primary reference:** [P1] J. C. Pedro, D. E. Root, J. Xu, L. C. Nunes,
*Nonlinear Circuit Simulation and Modeling*, Cambridge University Press, 2018.
**Reads with:** `sdd.md` (the expression engine and dual-number AD the constituents share),
`nonlinear-dc.md` (the `Evaluate` contract), `harmonic-balance.md`, `data-model.md` §5.

This note is the standing reference for the Model Composer series. Each phase updates the section it
builds; the overview remains the record of why each decision was taken.

---

## 1. What it is

**Model Composer** (Tools ▸ Model Composer…) turns measured or simulated device data into a nonlinear FET
model that DC, S-parameters and harmonic balance use, and turns S-parameter blocks into passive models.
Its document is the `.cmc`; what it exports for a transistor is a `.cmodel`, which a schematic places as a
`ComposedFET`. A passive model exports as an ordinary cell.

The device is not generated SDD text: its core needs table and ANN evaluation with exact derivatives,
guided extrapolation, internal state nodes and allocation-free grid evaluation, none of which an SDD
line can carry.

## 2. The decisions, in brief

| | Decision |
|---|---|
| D1 | `.cmc` is the composer document (JSON); `.cmodel` is a ZIP of `model.json` + `arrays/*.npy`; `<name>.cmc-cache/` is derived data. |
| D2 | A `.cmc` is `Active` (a three-terminal FET) or `Passive` (an N-port). |
| D3 | `ComposedFET:X1 g d s File=… N=…` elaborates **as a cell**: the network's elements plus one `ComposedCore` on `gi di si`. |
| D4 | Constituents `Id`, `Ig`, `Qg`, `Qd` (optional `IdRF`); each an Equation, a Table or an ANN. Charges are always charge functions; there is no transconductance delay (the charges carry it). |
| D5 | An extrinsic network is a six-pin `.csch`: extrinsic G, D, S, then intrinsic G, D, S; its variables are its element values. Stock networks ship as resources. |
| D6 | Every data set records where its bias voltages were measured; the composer models in intrinsic voltages. |
| D7 | `q` and `∂q/∂v` come from one function, so a model is terminal-charge-conserving by construction; Check Charge Conservation reports on data, model and large signal. |
| D8 | Two or more temperatures: `T` is an input, clamped to the range with one warning. One temperature: the model records `T0`, has no `T`, and a run elsewhere warns once naming both. |
| D9 | `N0`/`W0` are recorded; `N` scales the core by `N/N0` and each network variable by its rule. `M` keeps its meaning. |
| D10 | Traps are a setting — `None`, `CaptureEmission`, `Custom` — behind one contract. |
| D11 | Data sources are referenced, relative to the `.cmc`, and cached. |
| D12 | Active exports a `.cmodel` with symbol, rules, temperature, traps and provenance; passive exports a cell through `CellCreate`. Export needs the charge step's record, not fitted charges: with DC I–V alone the charges are typed constant capacitances, or zero (MC-18). |
| D13 | Every long operation is a `src/Design/Modeling` function taking a `RunControl`. |
| D14 | `src/Core/Devices/Composed/` (device, format, evaluators), `src/Engine/Modeling/` (numerics), `src/Design/Modeling/` (document and flows), `src/Ui/ModelComposer/` (window only). No new project. |
| D15 | Every operation has a `model` CLI noun calling the same function as the GUI. |
| D16 | Exit codes 0 / 1 / 3 / 130. |

## 3. The `.cmodel` container (as built)

`CmodelFile` (`src/Core/Devices/Composed/CmodelFile.cs`) is the only reader, validator and writer.

- **Entries.** `model.json` first, then `arrays/<name>.npy` in ordinal name order. Every entry's timestamp
  is 1980-01-01 00:00. Arrays go through RfCore's own `.npy` writer and reader (`DataSetExporter.ExportNpy`,
  `DataSetImporter.ImportNpy` — the stream forms of the file exporter), one cube named after the entry.
- **Deterministic.** JSON properties in the fixed order below, two-space indentation, `\n` line ends,
  doubles in shortest round-trip form. The writer stamps nothing — `Provenance.Created` is a field the
  caller supplies — so the same model writes the same bytes.
- **Validated both ways.** `Read` validates before returning and `Write` validates before writing.

### 3.1 `model.json`

```json
{
  "FormatVersion": 1,
  "Kind": "Fet",
  "Terminals": ["G", "D", "S"],
  "BasePeriphery": { "Fingers": 8, "UnitWidth": "125 um" },
  "Temperature": { "Mode": "Single", "T0": 25 },
  "Network": {
    "Netlist": "define gan (P1 P2 P3 P4 P5 P6)\nparameters rg=5.2 …\nR:rg n1 P4 R=rg\n…\nend\n",
    "Scaling": { "rg": "perFingerInverse", "Cpgi": "perFinger" }
  },
  "Core": {
    "Constituents": {
      "Id": { "Method": "Equation", "Inputs": ["vgs", "vds"], "Expression": "…", "Parameters": { "beta": 0.04 } },
      "Ig": { "Method": "Equation", "Inputs": ["vgs", "vds"], "IgGs": "…", "IgGd": "…", "Parameters": { } },
      "Qg": { "Method": "Equation", "Inputs": ["vgs", "vds"], "Expression": "…", "Parameters": { } },
      "Qd": { "Method": "Equation", "Inputs": ["vgs", "vds"], "Expression": "…", "Parameters": { } }
    }
  },
  "Symbol": "symbols/gan.csym",
  "Provenance": { "Created": "…", "CircuitRfVersion": "…", "Sources": [ { "Path": "…", "Sha256": "…" } ], "SettingsDigest": "…",
                  "Charges": "…" }                       // only with linear (MC-17) or typed (MC-18) charges
}
```

`Temperature` is `{ "Mode": "Range", "Tmin": …, "Tmax": … }` for a ranged model. `Symbol` is omitted for
the standard FET symbol.

**`Traps` (MC-10)**, after `Thermal`, gives the core one internal state node per enabled trap state (§13):

```json
"Traps": { "Model": "CaptureEmission", "States": ["phi2"],
           "Parameters": { "TauCapt2": 1e-8, "TauEmit2": 1e-3, "Xref": 1, "Vc": 0.05 },
           "Levels": { "phi2": [5, 10, 20] } }
```

`Model` names a registered model (`CaptureEmission`, `Custom`, or one a developer adds); `States` the enabled ones, in
the model's order; `Parameters` the model's own (a declared one left out takes its default); `Levels` the distinct
trap-setting levels each state was trained at, volts — a model trained from one level says so. A `Custom` model adds
`"Expressions": { "phi2": { "Dynamics": "…", "Dc": "vds" } }`. A block naming `None` reads as no traps and is never
written, so a model without traps is byte-identical to one written before traps existed. The states are inputs of
`Id` and `Qd` (`phi1` …), and their domain's `Box` holds each one's training interval. `"Coupling": "Shift"` (MC-19,
§13.6) replaces those inputs on `Id` with a threshold shift whose strengths `ShiftDrain` and `ShiftGate` sit in
`Parameters`; `Trained`, the default, is never written, so a block from before couplings existed is read and written
byte for byte.

**`Thermal` (MC-9)**, after `Temperature`, makes the model self-heat (§12.2): `{ "Rth": 60, "Cth": 1.7e-8 }` for one
pole, or `{ "Ladder": [ { "Rth": …, "Cth": … }, … ] }` for a Foster ladder of up to five, in K/W and J/K at the base
periphery; `RthSlope` (K/W per K) with `Tref` (°C) when the thermal resistance varies with the ambient. Absent: no
thermal node.

**Table and ANN constituents (MC-2)** name arrays instead of carrying expressions, and store the domain
they were trained over. `Core.Symmetry` and the top-level `Extrapolation` appear only when used:

```json
"Core": {
  "Symmetry": "Exchange",
  "Constituents": {
    "Id": { "Method": "Table", "Inputs": ["vgs", "vds"], "Table": "Id", "Smoothing": 0,
            "Domain": { "Polygon": [[-3, 0], [1, 0], [1, 8], [0, 10], [-3, 10]] } },
    "Ig": { "Method": "ANN", "Inputs": ["vgs", "vds"], "IgGs": "IgGs", "IgGd": "IgGd", "Activation": "Sigmoid",
            "Domain": { "Polygon": [ … ] } },
    "Qg": { "Method": "ANN", "Inputs": ["vgs", "vds", "T"], "Network": "Qg", "Activation": "Tanh",
            "Domain": { "Polygon": [ … ], "Box": { "T": [25, 125] } } },
    "Qd": { … }
  }
},
"Extrapolation": { "Mode": "Local", "Gx": { "Id": 2.0, "IgGs": 0.1, "IgGd": 0.1 } }
```

- A **table** `name` is `arrays/name.values.npy` (the grid, one axis per input, named for it and carrying
  its sites) and `arrays/name.coefficients.npy` (the spline's coefficients, one axis per input at its
  Greville abscissae). `ComposedArrays.Table` builds both; `TensorSplineFit.TableArrays` fits and builds.
- An **ANN** `name` is `name.inputOffset`, `name.inputScale` (`x̂ = (x − offset)·scale`), `name.W1 … name.WL`
  (`outputs × inputs`) with `name.b1 … name.bL`, the last layer being the single linear output, and
  `name.outputOffset`, `name.outputScale` (`y = offset + scale·ŷ`). `ComposedArrays.Ann` builds them.
- `Ig` names two payloads, `IgGs` and `IgGd`, under one method, one activation and one domain.
- **`Transform` (MC-7)** on a table or ANN current: `{ "Kind": "Asinh", "Scale": I0 }` — the payload computes `u`
  and the current is `I0·sinh(u)`, its gradient and Hessian-along-a-direction by the chain rule (`AsinhOutput`), so
  guided extrapolation continues the current itself. It exists for the gate current (§9.3).
- **`Core.Dispersion` (MC-7)**: `{ "Tau": τ }`, present exactly when `Core.Constituents.IdRF` is; `Id` is then
  `IdDC`. `IdRF` takes a `G_x` like `Id`.
- **`Drift` (MC-8)**, [I11]'s decomposition, on `Qg` and `Qd` together (§10.3). `Qg` names `QGS`, `QGD` and `Qdrift`,
  `Qd` names `QGD`, `Qdrift` and `QDS` (the same two networks) and carries `Parameters: { "Lambda": λ }`; each piece is
  an ANN of its own variables. The `Domain` is the data's hull, and its `Box` holds each piece's training interval,
  keyed `QGS`, `QGD`, `QDS`, `Qdrift.vgd`, `Qdrift.Id`; outside it a piece is continued to first order
  (`BoxContinuation`).

### 3.2 What the validator refuses

Every refusal is a `CmodelFormatException` whose message begins with the field: `Core.Constituents.Id.Method:
'Table' is not a known method; the known methods are Equation.`

- `FormatVersion` above the reader's — "written by a newer circuitRF".
- `Kind` other than `Fet`; `Terminals` other than `G D S`.
- `BasePeriphery.Fingers` below 1; `UnitWidth` not a positive number with a length unit.
- `Temperature.Mode` other than `Single`/`Range`; `Tmin ≥ Tmax`.
- `Network.Netlist`: not exactly one `define` block; not exactly six distinct pins; a pin named `0`; an
  element outside the block; any line the `.cnl` reader does not understand; a variable that does not
  resolve to a finite real number; an internal net named `gi`/`di`/`si`, or an element named `core`
  (the names the synthesised cell gives the intrinsic pins and the core).
- `Network.Scaling.<var>`: an unknown rule, by name; a variable the network does not have.
- A constituent: an unknown `Method`, by name; an input listed twice, or other than `vgs`/`vds` (and `T`
  for a `Range` model) — `T` on a single-temperature model and `phi1`/`phi2` without traps say why. `Ig`
  must carry exactly `IgGs` and `IgGd`; the others their method's key (`Expression`, `Table`, `Network`).
- An equation: an expression that does not parse or names something that is neither an input nor a
  parameter; a parameter sharing an input's name or not finite; a `Domain`, `Smoothing` or `Activation`
  (an equation is global and is not extrapolated).
- A table or ANN: inputs without both `vgs` and `vds`; any `Parameters`; a missing `Domain`; a polygon of
  fewer than three vertices, with no area, or not convex; a `Box` missing an input other than `vgs`/`vds`,
  naming one it does not have, or with `lo ≥ hi`; a missing or misshapen array, named; a value that is
  not finite. A table additionally: an axis not named for its input in order, fewer than two sites, sites
  not strictly increasing, coefficients that do not match the grid, a domain reaching outside the grid,
  a negative `Smoothing`, an `Activation`. An ANN: no hidden layer or more than four, more than 64 neurons
  in one, an output layer wider than one, a zero input scale, a `Smoothing`.
- `Thermal`: on a `Single` model ("self-heating needs a model over a temperature range"); both `Rth`/`Cth` and a
  `Ladder`, or neither; an unknown key; a pole count outside 1–5; an `Rth` or `Cth` that is not positive; an
  `RthSlope` that makes the thermal resistance non-positive inside `[Tmin, Tmax]`; a network element named `Rth`,
  `Cth` (`Rth1` … for a ladder) or a net named `t` (`t_1` …), the names the synthesised cell gives the thermal network.
- `Extrapolation.Gx`: missing for a table or ANN current, present for an equation current or an unknown
  key, or not a positive number. `Extrapolation.Mode`: anything but `Global`/`Local` (absent is `Global`). `Core.Symmetry`: anything but `None`/`Exchange`.
- `Transform`: on an equation or a charge; a `Kind` other than `Asinh`; a `Scale` that is not a positive current.
- `Traps`: an unknown `Model`, by name, listing the known ones; an unknown field; no state enabled, a state listed twice,
  out of the model's order or not the model's; an undeclared or non-finite parameter (a `Custom` model's parameters are
  its own, but none may share an input's name); `Levels` for a state not enabled; `Expressions` on a model other than
  `Custom`; the model's own checks (`CaptureEmission`: a time, `Xref` or `Vc` that is not positive, a negative `Ea`;
  `Custom`: an expression that does not parse, names anything but its own state, `vgs`, `vds`, `T` and the parameters,
  a `Dc` that reads its own state, or a `Dc` that is not a fixed point of its `Dynamics`). Combined with `Core.Dispersion`/`IdRF` (the trap states carry
  the DC/RF difference instead) or with `Core.Symmetry: Exchange` (the states follow the forward device, which has no
  image). A trap state named as an input of `Ig` or `Qg` — never, [X10] eq. 1.5 — or of a model without traps. A
  `Coupling` other than `Trained`/`Shift`; under `Shift`, an `Id` with a trap-state input
  (`Core.Constituents.Id.Inputs`), a model that declares no shift strengths (`Traps.Coupling`; `CaptureEmission` only),
  an enabled state's strength missing or not finite (`Traps.Parameters.ShiftDrain`/`ShiftGate`); a strength under
  `Trained`.
  `Core.Dispersion`: missing with `IdRF`, present without it, or a `Tau` that is not a positive time.
- `Drift`: on a current; on one charge and not the other; inputs other than `vgs, vds`; a missing or extra piece; a
  `QGD` or `Qdrift` naming a different network in `Qg` and `Qd`; a parameter other than `Qd`'s `Lambda`, or a
  `Lambda` outside [0, 1]; a box missing a piece's interval or holding another key; a `Smoothing`.

## 4. The device

### 4.1 Elaboration as a cell (R-mc1-5)

`Elaborator.ComposedFetCell` intercepts a `ComposedFET` line before the primitive branch:

1. Three nets, or four when the model self-heats — the fourth is the thermal node `t` (§12.2); four on a model
   without `Thermal` is refused naming the file. Parameters `File`, `N`, `m` (or `M`) and `Symbol` (artwork,
   ignored); anything else is refused by name.
2. `File` resolves exactly as an SnP's (`BaseDirectory`, workspace-relative), with the same named
   refusals: none given, a folder, not found; an unreadable model names the validator's field.
3. The model loads through `ComposedModelCache` — keyed by full path, size and modification time,
   `TouchstoneCache`'s pattern — so a hundred instances read the file once (`LoadCountFor`).
4. `ComposedNetworkTemplate.Synthesize` builds the cell for this (file, N, m), cached per elaboration:
   every network variable resolved, multiplied by its rule's factor, and every element parameter that
   evaluates to a real number written into the instance **as that number**. Pins 1–3 keep the network's
   names and become the cell's ports; pins 4–6 become `gi`, `di`, `si`. One `ComposedCore:core gi di si
   File="<absolute>" N=<N>` is added — `gi di si t` for a self-heating model, with the thermal network's R and C
   from `t` to ground (§12.2), `t` the cell's fourth port when the instance binds a fourth net.
5. The cell flattens through `FlattenCell` like any sub-cell, so paths and probes are ordinary
   (`X1.rg`, `V(X1.gi)`, `X1.core`). It is never added to a library, the palette or a file.

### 4.2 The core (R-mc1-6)

`ComposedCoreModel`: nonlinear, two ports sharing the source — (gi, si) and (di, si), so `v = (vgs, vds)`,
the FET family's arrangement and the elaborator's same three-to-four net expansion.

- `i = (IgGs + IgGd, Id − IgGd)`, `q = (Qg, Qd)`, with `dg`/`dc` from the same dual-number evaluation, all
  times `N/N0`. A transcapacitance is kept.
- Constituents compile once per model (`ComposedCoreProgram`, `CompiledSddExpr.CompileNamed`) with the
  seeds `vgs`, `vds` (and `T`) in that lane order. `EvaluateInto` runs them through
  `CompiledSddExpr.EvalDualInto`, which writes the gradient into caller memory — no allocation per sample.
- A value or derivative that is NaN or infinite is a `ComposedEvaluationException` naming the model, the
  constituent and the operating point — never a silent zero.
- `InternalStateNodeCount` is the seam for MC-7, MC-9 and MC-10: that many ground-referenced pairs follow
  the two intrinsic ports, minted by the elaborator as `__composed_<path>_x<k>` with ordinary global rows
  (never eliminated locally — their harmonics matter in HB). MC-1 declares none.
- `Activity` is `ActiveExact` with `gm` (port 1 on port 0) as the controlled conductance, as for the
  built-in FETs.
- **Dispersion (MC-7, [P1] eq. 6.52).** A model with `IdRF` has one state node `x` (port 2, `(x, 0)`), whose voltage
  is a current in amps. The drain carries `IdRF + x − IgGd`; the node's port carries `i = x − (IdDC − IdRF)` and
  `q = τ·x`, so KCL there is `τ dx/dt + x = IdDC − IdRF` — at DC the drain carries `IdDC`, far above 1/τ its small
  signal is `IdRF`'s. Both currents are taken in the terminal frame, after the drain–source image, so `x` means the
  same on either side of `vds = 0`. `x` is unscaled; `N/N0` multiplies the drain current it feeds.
  `ControlledConductances` adds `(1, 2)`: zeroing it with `gm` leaves the drain a conductance.
- **Self-heating (MC-9).** A model with `Thermal` takes a fourth net, the thermal node, as its last port `(t, 0)` —
  a net of the cell, not a minted node, because the cell's `Rth ∥ Cth` and an instance's fourth pin attach to it.
  Every constituent is evaluated at `T = temp + ΔT`, `ΔT` the node's voltage, and the T lane's derivatives become
  that column of `dg` and `dc`. The port carries `−P`, `P = ig·vgs + id·vds` from the (scaled) terminal currents
  ([P1] eq. 6.67), with `∂P/∂v` from the same Jacobian. Every entry of the thermal row and column is a controlled
  conductance, so the passivated device is thermally decoupled.
- `ComposedCore` is also a `.cnl` token in its own right — the intrinsic device alone.
- A table or ANN payload compiles to a `RuntimeFunction`: its inputs gathered from the seed lanes, the
  evaluator run inside its `GuidedExtrapolation`, its gradient scattered back (zero in a lane it does not
  read).

### 4.3 Periphery scaling (R-mc1-7, D9)

`PeripheryScaling` holds the rules by name: `fixed` (×1, the default), `perFinger` (×N/N0),
`perFingerInverse` (×N0/N). A later rule adds a name and a factor. `N` must be a positive whole number;
anything else is refused with a sentence saying `N` is a finger count.

**The multiplier.** `m` identical devices in parallel multiplies every admittance by `m`. Inductors and
mutuals are branch elements, which the device multiplier refuses, so their values (`L`, `M`) are divided
by `m` instead; every other element, and the core, carries `m` and the engine's multiplier applies it.

**The thermal network (MC-9)** scales like any other element: `Rth × N0/N`, `Cth × N/N0` (D9), and both carry `m`. The
core's power scales by `N/N0` and by `m`, so `ΔT` depends on neither.

### 4.4 Temperature (R-mc1-8, D8)

A `Single` model evaluates with no `T`; a run whose `temp` differs from `T0` gets one warning per model
file per run, naming both. A `Range` model evaluates at `temp` clamped to `[Tmin, Tmax]`, with one warning
when it clamps. Both go through the netlist's own warning channel (`AddWarningOnce`). A self-heating model
adds `ΔT` to the clamped ambient; the junction temperature itself is not clamped, so beyond the training interval a
table or ANN is continued by its guided extrapolation and an equation is global. An `RthSlope` is taken at the
clamped ambient.

### 4.5 The symbol (R-mc1-9)

`SymbolKind.ComposedFET` (palette: Devices, with Nonlinear and DataFiles as filters) carries `File`, `N`
and `Symbol`. Its glyph is a virtual reference, `composedfet://<symbol>|<file>`, resolved by
`ComposedFetSymbolProvider` through the `CellSymbolResolver` seam SpiceModel and wBond use:

- the instance's `Symbol`, or the model's own when the instance's is blank;
- a library symbol by its kind's name, or a `.csym` relative to the `.cmodel` whose pins 1, 2 and 3
  exist — they bind to `g d s` by number;
- otherwise the standard FET glyph and pin geometry, with one extraction warning naming the symbol.

When `File` changes in the Inspector, the new model's `Symbol` is copied into the instance unless the
instance carries a symbol of its own choosing. `Symbol` never reaches the netlist.

### 4.6 `check`

`DocumentKinds` classifies `.cmodel` as `composed-model`; `check` calls `CmodelFile.Read` and reports
its sentence (error, exit 1) or a one-line summary (periphery and temperatures). It has no rule of its own.

### 4.7 Tables (R-mc2-1)

`TableEvaluator`: a tensor-product cubic B-spline over a rectilinear grid, non-uniform axes allowed.
The knot vector is not stored — `CubicBSpline.Knots` derives it from the sites by one rule the fitter and
the evaluator share:

- four or more sites: the **not-a-knot** spline (end knots of multiplicity four, every interior site a
  simple knot except the second and the second-to-last) — one coefficient per site, C² at every knot, and
  O(h⁴) error up to the ends, where a natural spline's zero-curvature end condition costs O(h²);
- two or three sites: one cubic piece over the axis, carrying the line or parabola through them exactly.

Value, gradient and the second derivatives come from the basis (Cox–de Boor with derivatives), never from
a difference. **Never bilinear**: its slope jumps at every knot, which is what makes table models wrong at
low drive ([P1] §6.2.6.2). Even a C² cubic has piecewise-constant third derivatives and none beyond, so a
table cannot predict high-order distortion at very low drive ([X06] §I); where low-level IM matters, an
ANN is the default.

`TensorSplineFit` (`src/Engine/Modeling/`) fits the coefficients once, when a model is written. It is
separable — one-dimensional fits along each axis in turn — and with a smoothing weight λ > 0 each axis
minimises `Σ (yᵢ − s(xᵢ))² + λ ∫ s″(u)² du` over the axis mapped to [0, 1], so one λ means the same on
every axis. The normal equations are banded (half-bandwidth three) and solved by a banded Cholesky factor
computed once per axis. λ = 0 is interpolation; an axis of two or three sites is never smoothed.

### 4.8 ANNs (R-mc2-2)

`AnnEvaluator`: affine input normalisation, one to four hidden layers of one to 64 neurons (`Tanh`, the
default, or logistic `Sigmoid`, [P1] Fig. 6.10), a linear output and its de-normalisation. The gradient
with respect to the inputs is carried forward through the layers alongside the value — the derivative
network of [P1] §6.2.7 — and, when asked, the Hessian along one direction with it. Scratch is on the
stack (64 neurons × 5 inputs), so the evaluator is immutable, thread-safe and allocates nothing.

### 4.9 Guided extrapolation (R-mc2-3)

Each table or ANN constituent stores its training domain (`TrainingDomain`): a convex polygon in
(`vgs`, `vds`) and an interval per other input. `GuidedExtrapolation` works in coordinates normalised by
the domain's extent along each input, so a distance mixes volts and degrees on equal terms. Inside, it
is the evaluator. Outside, with `p_b` the nearest domain point and `d = |p − p_b|`:

```
f(p) = f(p_b) + ∇f(p_b)·(p − p_b)                     charges stop here
     + G_x · (v_own − v_own,b) · d / d₀                currents; d₀ = 0.05 (5 % of the extent)
```

`v_own` is `vds` for `Id`, `vgs` for `IgGs`, `vgs − vds` for `IgGd`. The added term is a conductance on
the constituent's own branch that grows with distance and vanishes, with its derivative, at the boundary
([P1] Fig. 6.12b). `G_x` is stored per current in `Extrapolation.Gx`; `GuidedExtrapolation.DefaultGx` is
ten times the largest training conductance, for the phases that write models.

**The local law (MC-29, `Extrapolation.Mode: "Local"`).** Under the global law above the same `G_x` applies at every
edge, and for a gate current that is ten times the FORWARD diode's conductance — so where the data stops on the reverse
side, with the measured current zero, the continuation conducts tens of siemens. The local law replaces `G_x` in the
added term by

```
G_loc(p_b) = min( √((K·g_own(p_b))² + G_floor²), G_x )        g_own = ∇f(p_b)·own,  K = 10,  G_floor = 1e-7·G_x
```

the soft maximum of ten times the edge's own conductance and a floor, capped by `G_x`. At a quiet edge the
continuation is the linear one; at the forward edge it is the edge's own steep conductance, still bounded by the cap.
`G_floor` is 1e-6 of the largest own-branch conductance in the data (`G_x` is ten times that): small enough that a gate
three volts past the data draws ~0.1 mA through it on a 5 S device, and it keeps `G_loc` smooth through `g_own = 0`, so
the only kink is the cap's, outside the domain. `G_loc` moves with `p_b`, so its gradient enters too:
`(s·d/d₀)·∂G/∂a·K·P(D H own)` below the cap, one more Hessian-along-a-direction evaluation per outside sample. A file
without `Mode` is the global law, so every model written before MC-29 evaluates unchanged; the composer exports `Local`.

**The gradient is exact.** `p_b` moves with `p`, so the derivative of the linear continuation is
`∇f(p_b) + Pᵀ H(p_b)(p − p_b)`, `P` being the projection's Jacobian — `ê êᵀ` beyond an edge, zero in a
vertex's normal cone, one along an unclamped box axis. That is why the evaluators return the Hessian
along a direction. Value and gradient are continuous across the boundary; across a vertex's normal cone,
outside the domain, the gradient may jump. The polygon must be convex, or the nearest point — and with
it the value — would jump outside it.

Equations are not extrapolated: an equation is global by construction ([P1] §6.2.5.2).

### 4.10 Drain–source symmetry (R-mc2-4)

`Core.Symmetry: "Exchange"` (off by default — a field-plated power device is deliberately asymmetric,
[P1] §6.5.5). The constituents are read for `vds ≥ 0`; for `vds < 0` the core evaluates them at the image
`(vgs − vds, −vds)` ([P1] eq. 6.61) and transforms back: `Id` changes sign, `IgGs` and `IgGd` exchange
([P1] eq. 6.64), `Qg` carries over and `Qd = −Qg′ − Qd′` ([P1] eq. 6.63 in charge form). Each gradient
maps through the image's Jacobian (`∂/∂vgs = ∂′/∂vgs′`, `∂/∂vds = −∂′/∂vgs′ − ∂′/∂vds′`). Continuity at
`vds = 0` is a property of correct data; MC-7/MC-8 report the jump.

### 4.11 Grid evaluation (R-mc2-5)

`ComposedCoreModel.EvaluateGrid` writes an HB time grid straight into the caller's `GridResult` — no
per-sample arrays and no per-grid scratch (the base class's `EvaluateInto` loop allocates five buffers per
call, and HB calls once per Newton iteration). `ComposedCoreGridTests` holds it at zero bytes with an
allocation counter, never a timing.

## 5. The `.cmc` document and its data (MC-3)

### 5.1 The document (R-mc3-1, R-mc3-2)

`CmcFile` reads, validates and writes it in `CmodelFile`'s shape: a `JsonDocument` walk in which every
miss names its field (`DataSets[1].Conditions.Quiescent.Vgs: it is not a number.`), and a `Utf8JsonWriter`
in a fixed order with `\n` line ends — the same document writes the same bytes, and the writer validates
before it writes. **An unknown key anywhere is a refusal**: a misspelt condition silently dropped would
leave a set measured under conditions nobody stated. `Pads`, `Network` and `Steps` are the exception —
opaque `JsonNode`s, written back verbatim, so this build loses nothing a later phase wrote. `Panels` is
written only when a panel is hidden (railRF's rule). `View` (MC-14) holds the window's selected step and bias cursor,
written only when set — view state, never an undo step (§18).

Rules the validator holds: a Passive document takes only `PassiveSParams` sets and no periphery, an Active
one never takes them; names are unique; sources are relative; pulse conditions (`PulseWidth`, `Period`,
`Quiescent`, `SampleDelay`, `TrapState`, `PrePulse`) appear only on a pulsed kind, with `Period` longer
than the pulse and the sample inside it; `BiasPathOhms` only with the `instrument` plane. A condition a
later step needs is reported by that step, never here. `CmcFile.Warnings` is the one thing a well-formed
document still says: a pre-pulse dissipating ≥ 1 mW, whose heating is not modelled.

**Exclusions live in the document**, by stable point id, because the cache must hold nothing a re-import
cannot rebuild. `NormalisedData.WithExclusions` applies them; nothing excludes a point automatically.

### 5.2 The normalised form and the importers (R-mc3-3)

`NormalisedData`: per point a `BiasPoint` (`PointId`, `Vgs`, `Vds`, `Ig`, `Id`, ambient, intrinsic
voltages), and for S kinds `S[point, freq, i, j]` with one `Z0`, and a capacitance table `C[point, i, j]` (§10.1).
**A quantity a source does not carry is NaN, never zero** — a zero gate current is a measurement.

`DataImport` — each importer refuses with its source and why, never a guessed bias or a skipped row:
- **Touchstone sets**: one S-parameter file per point, every file sharing one frequency axis and one real
  Z0. `BiasPattern` turns `Vgs{vgs}_Vds{vds}` into a regex: fields may state a unit (`{id:mA}`), whitespace
  matches any run, `*` matches anything; against a name it must match the whole stem, against comments it
  is searched in the header comment lines joined by spaces.
- **CSV/TSV**: delimiter inferred from the first row (tab, then `;` without `,`, then `,`); a cell's own
  unit wins over the column's, through `TunableValue.TryParse`.
- **`.npy` / `.mat`**: bias arrays are per point, or the axes of a grid whose current or table array is
  shaped `[vgs, vds]` (or `[vds, vgs]`, detected from the shape). A Y table converts to S at the import's
  Z0 — exact and invertible, so one normalised form serves both.

`MatReader` (`src/RfCore/Export`) reads MAT v5 — numeric matrices, 1 × 1 structs of them (`s.field`),
compressed elements, either byte order — and v7.3 through PureHDF. **Every array comes back in C order
over MATLAB's logical shape**: v5 is column-major and is transposed; a v7.3 dataset with a `MATLAB_class`
attribute is the same bytes with reversed HDF5 dimensions and is transposed too; an HDF5 dataset without
one (`MatWriter`'s own, h5py's) is already C order. Given a name set, an unrequested variable is skipped —
a compressed one without inflating past its name. Cell arrays, objects, sparse matrices and struct arrays
are refused by name when asked for (or when everything is).

### 5.3 The cache (R-mc3-4, R-mc3-6)

`CmcCache` keeps `<name>.cmc-cache/index.json` (per set: its `.npy`, a settings key — kind, sources and
import settings as the `.cmc` writes them — and per source path, size, modification ticks, SHA-256) and
one circuitRF `.npy` per set. **Open reads no source when size and time match** (`SourceReads` is the
counter); a moved size or time is hashed, and the same hash refreshes the index without re-importing. A
changed hash re-imports the set with one status line; a missing source with a cache warns and loads; a
missing source with no cache throws `DataImportException` naming it. A rebuild writes a whole sibling
folder and swaps it in by rename; failure or cancellation before the swap deletes it, so the old cache
survives untouched.

### 5.4 Hygiene (R-mc3-5)

`DataHygiene.Check` flags, never excludes: oscillation (|S11| or |S22| > 1, or K < 1 with |Δ| > 1, at or
below 2 GHz), compliance (a stated limit, or with none a plateau of ≥ 3 points within 10⁻⁴ of the set's
largest current), drift (a repeated bias within 1 mV, in one set or across sets of the same kind and
conditions, whose current differs by more than 2 % + 1 µA or whose S differs by more than 0.02). Every
tolerance is a `HygieneSettings` property. `DataHygiene.Coverage` is the convex hull of the non-excluded
(Vgs, Vds).

## 6. Pads, the network and the intrinsic data (MC-4)

### 6.1 The blocks (R-mc4-1, R-mc4-2)

`Pads` and `Network` stay `JsonNode`s on `CmcDocument`, written back verbatim, and `CmcExtrinsic` is their one
reader and writer: `CmcFile.Validate` parses both through it, so a misspelt key is refused by field like the rest.
`PadsSettings` is `Kind` (`None`, `OpenShort`, `TwoPorts`, `Network`) and the paths that kind needs; a path another
kind would read is refused, not ignored. `NetworkSettings` is the `Schematic`, per variable `Min`, `Max`, `Lock`,
`Tie` (`To`, `Factor`; a chain of ties is refused) and `Scaling` (a `PeripheryScaling` name), and an optional
`FlatnessBand`.

### 6.2 Networks as schematics (`PinNetwork`)

A network is read the way the application reads any schematic — `SchematicCircuit.Extract`, `CnlTextOf`,
`RoundTrip` — and solved by the engine as it is. Nothing here stamps a matrix.

- **Pins.** Exactly N `Pin` symbols numbered 1…N, each once, or a refusal listing what was found
  (`found 1, 2, 3, 4, 4, 6`). Two pins on one net are refused too: the extraction names a net once, so the second
  pin's name would reach no element. Join them through a `Short`.
- **Variables.** The VAR block, each with its value in base SI (the elaborator's `ResolvedGlobals`) and the kind of
  element whose value names it (R, L, C, mutual M, or other) — which is what its default bounds follow: `Min` 0 for
  the four, `Max` 100× the starting value. Overrides replace a variable's expression with its number before
  elaboration, so a trial point is a different circuit, never a patched matrix.
- **The coupling constraint.** `CouplingFactors` gives `|M|/√(L1·L2)` per mutual at any values; a fit holds it below
  1 as a constraint, never as a bound on `M`.
- **Pin 3 is ground.** Data is common-source, so the source pin is node 0 of every bench, as it is in the
  measurement and in any simulation that generated the data. Ports C (4–6) and D (5–6) float on pin 6, which the
  engine handles as any port with a reference.

`SixPinNetwork.Blocks` runs one S-parameter analysis with four 50 Ω ports and converts to Y (`RFNetwork.SToY`):
`Y_N = [[Y_aa, Y_ab], [Y_ba, Y_bb]]` per frequency. `SixPinNetwork.Dc` is the DC solve of R-mc4-4 — `LinearDcEngine`
with `Vdc` sources on pins 1 and 2 and `I_1Tone` DC sources (circuitRF's only DC current source) drawing from 4 and
5 into 6 — done once per unit source: the network is linear, so the four solves are its `DcTransfer` and every
point is a dot product. `PadNetwork` is the two-pin form for `Network` pads.

**Stock networks** (`StockNetworks`, resources `src/Design/Modeling/StockNetworks/*.csch`): GaN two-shell (D5's
values), classic single shell and series only. They were drawn from their netlists by `NetlistSchematic`, with six
numbered pins on labelled stubs. `Choose` copies one beside the `.cmc` (never over an existing file:
`-2`, `-3`, …) and writes a network block with every variable at its defaults and D9's scaling rules.

### 6.3 The algebra (`src/Engine/Modeling/NetworkAlgebra.cs`)

`C2`, a 2×2 complex matrix, carries everything: a FET's two ports, a pad, each block of the 4-port. Closed forms
throughout. The condition number is `σmax²/|det|` from the Frobenius norm and the determinant — no eigen-solve,
and no cancellation in `σmin`.

- Open-short: `Y_dev = ((Y_m − Y_o)⁻¹ − (Y_s − Y_o)⁻¹)⁻¹`.
- Two-ports: `T_dev = T_gate⁻¹ · T_meas · reverse(T_drain)⁻¹`, in ABCD from S directly (finite for a pure series
  or shunt pad, where Y or Z is not). Each pad file has port 1 at the probe.
- Through the network: `Y_int = Y_ba (Y_aa − Y_ext)⁻¹ Y_ab − Y_bb`, the exact inverse of
  `Y_ext = Y_aa − Y_ab (Y_bb + Y_int)⁻¹ Y_ba`. A point and frequency where the condition number of
  `(Y_aa − Y_ext)`, `Y_ab` or `Y_ba` exceeds 1e10 is flagged and left out of the flatness metric.
- A pad is interpolated linearly in its own form (Y or ABCD) between samples; outside its axis it is a refusal
  naming both ranges.

### 6.4 Re-referencing and the quantities (R-mc4-4, R-mc4-5)

A set's plane defaults to `probe`. `instrument` subtracts the bias-path drops (the source path carries `Ig + Id`);
`instrument` and `probe` then pass through a network pad's DC map; every plane but `intrinsic` then goes through the
network's `DcTransfer`. A missing gate current is taken as 0 A with one note; a missing drain current is a refusal
when anything between the stated plane and the core drops volts, because the answer would depend on it. A set that
carries intrinsic voltages of its own gets `max |ΔV|` in one note.

`IntrinsicQuantities.Of` gives `Gm`, `Gds`, `C11…C22`, `Cgs`, `Cgd`, `Cds`, `Cm` and `Tau = −Cm/Gm` from one
`Y_int`; `Flatness` is `(max − min)/|mean|` over the band, flagged frequencies left out.

### 6.5 The result and its cache (R-mc4-6, R-mc4-7)

`IntrinsicExtraction.Run` returns one `DataSet` per data set, on a `point` axis carrying the point ids:
`Vgs`, `Vds`, `Ig`, `Id` as measured and `Vgsi`, `Vdsi` computed (both clouds, for [P1] Fig. 6.5), `Excluded`;
for S data also `Y [point, freq, port, port]`, `Cond`, `Flagged`, a cube per quantity on `(point, freq)`,
`Flatness [point, quantity]`, the `Band` used, and `Notes` (a label axis) when there were any. `Compute` is the same
work for one set with no cache. `Run` solves the network at the variables' document `Value`s (MC-5) with the
caller's overrides on top, so a fitted value changes the key and reopens nothing stale.

The cache is `<name>.cmc-cache/intrinsic/<key>.npy`, the key a SHA-256 over the data's own `.npy` bytes, the set's
conditions and exclusions as the `.cmc` writes them, the pads block and each pad file's bytes, the network file's
bytes, the variable values and the band. An unchanged network reopens with no solve (`Computations` counts sets
computed). Entries are written only when every set is done — a cancellation writes nothing — each by a temporary
file and a rename, and entries no set uses are removed. `CmcCache`'s folder swap carries the subfolder across.
**Known gap:** a file a network schematic itself references (an `SnP` block) is not in the key.

## 7. Fitting the network (MC-5)

### 7.1 One least-squares problem (`NetworkFit`, R-mc5-1, R-mc5-2)

Every method is a residual vector over the network's free variables, the network solved by the engine at each
trial point (`SixPinNetwork.Blocks`). Three kinds of term:

- **(a) Flatness**, at the active points: each of `Re Y_ij` and `Im Y_ij/ω` of the de-embedded intrinsic Y about its
  own mean over the flatness band, divided by the point's admittance scale (its measured capacitance at the band's
  bottom, and `max(|G|, ω_mid·C)` for the conductances). A correct shell leaves the intrinsic device a lumped
  `G + jωC` ([P1] §5.6.3), so the minimum is the shell that makes it one.
- **(b) The cold-FET models** ([P1] §5.5.2), compared in the MEASURED domain by variable projection: each trial
  de-embeds the cold point, fits the cold core in closed form, embeds that core back and compares.
  - *Pinch-off*, Vds = 0: `Gm = 0`, `R_DS → ∞` and intrinsic `C_DS = 0` (the book's own assumption), so the core is
    `[[Cgs + Cgd, −Cgd], [−Cgd, Cgd]]`, linear in its two unknowns. Residual: Y per entry ÷ ω·C_scale — eq. 5.4's
    quantities, with the series elements kept, which is the only thing that separates an outer capacitance from an
    inner one on one device (the book separates them with a scaled array instead).
  - *Forward gate, extreme* (§5.5.2.4.1): the intrinsic device is one node, so the model is the network's `Y_aa` —
    its admittance with the intrinsic ports shorted. Residual: Z ÷ Z_scale (eq. 5.7's domain). Embedding through
    `Y_aa − Y_ab Z (I + Y_bb Z)⁻¹ Y_ba` keeps a zero core impedance finite.
  - *Forward gate, moderate* (Fig. 5.17): `Z_int = [[Z1 + Rch/3, Rch/2], [Rch/2, Rch]]`, `1/Z1 = 1/R1 + jωC1`
    linear in its unknowns, `R_ch` a document setting (a test structure's; a refusal when absent).
- **(c)** optionally, S of the band-mean `G + jωC` embedded, against the measured S at every frequency.

Each term's cost is its weight times its mean square (defaults `Flatness` 1, `Cold` 1, `SmallSignal` 0). A mutual is
solved at `|k| ≤ 0.999` — the engine is never handed a non-physical coupling, whose warning goes straight to stderr —
and the excess is a penalty residual that is zero inside. Bounds default to R-mc4-2's (`Min` 0, `Max` 100× start).
A tie is `value(To) × Factor`, applied whenever values are composed, so it holds exactly; a tied or locked variable
is not a coordinate. A locked variable's `Value` is the fixed value — typed, or taken from EM.

### 7.2 The points

A point is at Vds = 0 V within 20 mV. Pinch-off, when not named: the lowest Vgs whose low-frequency `Y22` is
capacitive (`Re < ½|Y22|`). Forward gate: the highest Vgs whose low-frequency `Y11` is resistive and whose gate
current, when measured, flows in. Active, when not named: off (lowest Vgs at the highest Vds), on (highest Vgs at
the lowest Vds), the knee (highest Vgs, the first Vds whose Id reaches 90 % of the saturated one; without currents
the Vds nearest a quarter of the highest) and saturation. The chosen points are written back into `Fit`, so the user
edits a choice rather than starting one. Cold-FET methods refuse a missing cold point naming the bias it needs; the
multi-bias fit notes a missing one and goes on.

### 7.3 Search (R-mc5-3)

Coordinates are the unit box: logarithmic for a positive quantity (from `Min`, or a millionth of `Max` when `Min`
is 0), linear otherwise; a free variable without both bounds is a refusal. **Cold-FET direct** alternates
Levenberg–Marquardt on the capacitances (pinch-off term, series held) and on the R, L and M values (forward term,
capacitances held) until a round moves nothing by 1e-7, at most eight rounds, then polishes both terms together —
the moderate core's `R1` against `Rg` is a valley the alternation zig-zags along (measured: cost 2.4e-3 stalled,
4.2e-8 with the joint polish). **Multi-bias** runs the registry's CMA-ES (default) or DE per start with seed
`Seed + start`, from the same start point, `Budget` evaluations each (default 200 per variable), polishes each
start's best with Levenberg–Marquardt and keeps the lowest cost. A batch is evaluated in parallel and told back in
order, so the same seed gives the same values on any core count. Progress is a stage per start; cancellation is
checked per batch and throws — nothing is returned, nothing written.

### 7.4 Identifiability (R-mc5-4)

At the solution: the residual Jacobian by central differences (in ln v for a logarithmic variable), `s² =
SSR/(m − n)`, `Cov = s²(JᵀJ)⁻¹` through the SVD of the unit-diagonal `JᵀJ` (a direction the data does not see is a
huge variance, not a failure). Per variable: the standard error, the relative error, the most-correlated partner and
`ρ`, the spread of the polished starts, and `at bound Min`/`Max` within 0.5 % of the box. **Poorly determined** is a
relative error above `PoorRelativeError` (0.2) or `|ρ|` above `PoorCorrelation` (0.95). Lines: `Cpgi poorly
determined (ρ = −0.98 with Cpga)` (or `(±35 %)`), `rg at bound Max (520)`, and one suggestion per variable or pair:
`Lock Cpga, or tie it to Cpgi × 1.43.` Measured on the GaN network: `ρ(Cpga, Cpgi)` is −0.999 with data to 2 GHz and
−0.70 with data to 40 GHz.

### 7.5 The result in the document (R-mc5-5)

`Run` returns `NetworkFitResult`; `Apply` writes each fitted and tied variable's `Value`, the resolved `Fit`
settings and the `Result` record (`Method`, `Cost`, per-variable `StdError`/`Spread`/`AtBound`, the report lines and
the `Digest` of the schematic path and values); `FitFile` reads, fits, applies and writes — what the window and the
`model` verb call. The intrinsic cache keys on the values, so it invalidates itself. A later step records the digest
it was computed through as `"Network"` in its `Steps` entry; `StaleSteps` names every entry whose digest differs.

## 8. Training an ANN (MC-6)

`src/Engine/Modeling/Ann/` trains MC-2's network and hands back the runtime `AnnEvaluator` itself, with its
`.cmodel` arrays (`AnnTrainingResult.Arrays`), so a trained network loads with no conversion. Which data a
constituent trains on, and its weights and anchors, belong to MC-7 … MC-10.

### 8.1 The problem (R-mc6-1 … R-mc6-3b)

`AnnTrainer.Train(AnnTrainingProblem, AnnTrainingSettings, RunControl?)`. A problem has 1–5 inputs and any of:
**values** (`AnnValueData`: points, targets, a weight per point), **derivatives** (`AnnDerivativeData`: points, the
input directions `j`, the targets of `∂f/∂x_j`, a weight per point and direction — 0 marks a component not measured)
and an **anchor** that fixes a derivative-trained function's constant. Values and derivatives together are [X03]'s
combined training (eq. 8), each over its own point set.

- **Normalisation.** Each input maps its range over every point onto [−1, 1]. The output maps the values' range onto
  [−1, 1]; with no values it is scaled so the derivative targets are of order one, and offset to the anchor's value.
- **The loss.** Each term is a weighted *mean* in normalised units — value residuals ÷ the output scale, derivative
  residuals ÷ the RMS of their targets — times `ValueWeight` or `DerivativeWeight`, so the term weights are
  dimensionless. A constant per term moves no minimiser: within a term it is `Σ w (f − y)²` and
  `Σ w (∂f/∂x_j − t)²` exactly, and non-conservative derivative data gets the least-squares compromise ([P1] §6.3.6
  ¶2). The anchor adds `Weight·((f(x₀) − y₀)/outputScale)²`; `L2` adds `λ·ΣW²` over the normalised weights.
- **The adjoint network** (`AnnObjective`). The input derivatives are carried forward with the value (the derivative
  network of MC-2); the loss is back-propagated through both networks at once — [X03] eq. 17's three paths:
  `z̄ = ā ⊙ σ′ + Σⱼ ūⱼ ⊙ σ″ ⊙ vⱼ`, `v̄ⱼ = ūⱼ ⊙ σ′`, `∂L/∂W += z̄ aᵀ + Σⱼ v̄ⱼ uⱼᵀ`. Never a finite difference.
- **Size.** Default 2 × 8 `tanh`. When the parameters exceed a tenth of the targets (one per value, one per measured
  derivative component — weight not 0 — and the anchor) the result carries one warning ([L26]).

### 8.2 The optimiser (R-mc6-4)

In-house **L-BFGS** (`Lbfgs`): two-loop recursion, memory 10, a strong-Wolfe line search with cubic-interpolation
zoom, a pair with unsafe curvature skipped, one fall-back to steepest descent before giving up. Stops on the
gradient's largest component, the relative loss change or the iteration cap (defaults 1e-9, 1e-14, 2000).
Initialisation is Glorot-uniform from `SplitMix64`, biases zero. `Restarts` (default 3) seeded starts; the start with
the lowest held-out loss is kept (training loss when nothing is held out).

### 8.3 Held-out error (R-mc6-5)

`HeldOutSplit`: each point's normalised coordinates are quantised and interleaved into a Morton key; the points are
sorted by that key (ties by the exact coordinates, then the targets — so by content, never by array position), cut
into as many contiguous strata as points are held out (default 10 %), and one point is drawn per stratum by the seed.
Values and derivatives are split separately. `AnnFitError` reports training and held-out RMS, absolute and relative to
the targets' range — computed through the runtime network itself. Early stopping (off by default) keeps the iterate
with the lowest held-out loss and stops after `EarlyStoppingPatience` iterations without a new one.

### 8.4 Determinism and long work (R-mc6-6, R-mc6-7)

Single-threaded training gives the same weights bit for bit on every platform: the points are summed in fixed blocks
of 64 in a fixed order, and `tanh`, the logistic function and `exp` are computed from IEEE arithmetic alone
(`DeterministicMath`) because the platform's `Math.Tanh` comes from its C library and differs in the last place
between operating systems. `Parallel` evaluates the blocks on several threads and is promised only to 1e-12 relative.
Progress is a stage per start and a tick per iteration on `RunControl`; a cancel throws `OperationCanceledException`
out of `Train`, so the caller keeps whatever network it had (D13).

## 9. The current constituents (MC-7)

`CurrentFit` (`src/Design/Modeling/`): `Run` from the open data and its intrinsic result, `Apply` into
`Steps.Current`, `FitFile` (read, open the cache, de-embed, fit, write — what the window and the `model` verb call),
`Load` (the record and its arrays back from the cache, or null to fit again). Settings and record:
`CurrentFitSettings`, `CurrentFitRecord`; templates: `CurrentTemplates`; parameter fits: `EquationFit`.

### 9.1 Training points (R-mc7-1)

The intrinsic points (`Vgsi`, `Vdsi`) of the chosen sets — by default every `DcIv`, `PulsedIv` and
`SimulatedTables` set — excluded points removed. One trap state (all DC, or all pulsed from one `Quiescent` and one
`PrePulse`); a mix is refused naming the condition. Temperatures (set `AmbientC` and per-point `T`, within 0.01 °C)
may differ: two or more make `T` an input (§12.1). A point with no drain current is left out, one with no gate current is left out of `Ig` only; with no
gate current at all `Ig` is the equation `0`.

**The gate split.** `Sign`: for `vds ≥ 0` a positive (forward) gate current is all `IgGs`, a negative one all `IgGd`
(breakdown at the drain side); for `vds < 0` the roles exchange. At `vds = 0` with the gate forward, both junctions
conduct and the split gives it all to `IgGs` — the terminal currents are still right, since the drain terminal
carries `Id − IgGd` and the channel target is **`Id_terminal + IgGd`**. `GateSource`/`GateDrain` put it all in one.

The training domain is the points' convex hull (`DataHygiene.Coverage`), refused when it has no area.

### 9.2 Methods (R-mc7-2)

Every method holds out the same points (`HeldOutSplit` over the domain, `HeldOutFraction`, seeded), so errors
compare; a network is trained on the rest with no split of its own.

- **ANN** (default, 2 × 8 `tanh`, 3 starts, 6000 iterations): MC-6 value training. `Id` weighs a point
  `1/max(|Id|, IdFloor·max|Id|)²`. Measured on the synthetic device: `IdFloor` 0.1 gives 0.6 % worst held-out error
  over the range where 0.01 gives 2.7 % for no better sub-threshold fit and uniform weights 0.1 % with sub-threshold
  errors three times larger; the stiffness of relative weights also needs ~6000 L-BFGS iterations (2000 left 5.6 %).
- **Table**: the scattered points re-gridded on the hull's bounding box at `GridStep` (exactly; the last site at or
  beyond the hull), then MC-2's spline. The source is a network trained on the training points — continued
  **linearly** outside the hull, never with its `G_x` term, which grows with the square of the distance and made the
  spline ring back into the boundary cells (7.7 % against the network's own 1.5 %) — or `Regrid.Rbf` through the
  points (multiquadric, coordinates normalised over the points). A step over twice `SmallestSwing` (0.05 V) reports
  `table step 0.2 V: low-level IM below ~0.1 V swing unreliable` ([P1] Fig. 6.8).
- **Equation**: a template — the `Id` laws of the built-in Angelov, Curtice quadratic and cubic, Statz and Materka
  FETs, copied as expressions (no temperature coefficients, n-channel), and a `Diode` gate — or the user's own, its
  parameters fitted by the registry's `cmaes` (or `de`) then Levenberg–Marquardt over the unit box (logarithmic for
  a positive range of two decades), weighted as above. The expression compiles once with `vgs`, `vds` and the free
  parameters as seed lanes, so at most 14 are free. Each template estimates its start from the data (peak `gm`, the
  threshold, the knee). The gate's two branches share one parameter list, so a template's free parameters are
  suffixed `Gs` and `Gd`.

### 9.3 The gate current

**Weighting alone cannot fit a gate current in a network or a table.** With a linear output and `1/|Ig|²` weights,
the ~160 near-zero reverse points outweigh the handful of forward ones by ten orders of magnitude and the forward
conduction is not fitted at all (8 % of its range, held-out points 2–3 decades off). So a table or network `Ig` is
fitted in `u = asinh(Ig/I0)`, unweighted — an equal error in `u` is an equal ratio above `I0` — and exported with
`Transform: Asinh, Scale: I0`, `I0 = IgFloor·max|Ig|` (default 1e-4: 0.17 decades at the faintest held-out points
where 1e-6 gave 1.1). An equation `Ig` already has the decades in its form and is fitted to `Ig` weighted by
`1/max(|Ig|, I0)²`.

### 9.4 Domain, symmetry and the report (R-mc7-3, R-mc7-5)

A table or ANN current carries the hull as its `Domain` and `G_x` = `GuidedExtrapolation.DefaultGx` of the largest
own-branch conductance over the training points (never below 1e-12 S). With `Symmetry: Exchange` only `vds ≥ 0`
points are used and the record carries the jumps at `vds = 0` over the domain's `vgs`: `2·Id(vgs, 0)` (value),
`∂Id/∂vgs(vgs, 0)` (the slope in `vds` jumps by exactly that) and `IgGs − IgGd` (the gate branch). The report also
has, per current, training and held-out RMS error, absolute and relative to the targets' range, and `Conductance`:
the fitted `Gm`/`Gds` against the data's DC derivatives from `LocalGradients` (a local quadratic through the 12
nearest points in normalised coordinates), RMS and relative.

### 9.5 DC/RF dispersion (R-mc7-4)

Off by default; the window presents it as the simpler alternative to modelling heating and traps (MC-9, MC-10),
which [P1] prefers. `IdRF` is a network trained by MC-6 **derivative** training on the band-mean intrinsic `Gm` and
`Gds` of the S sets (flagged frequencies left out), anchored to `IdDC` at `Reference` (default: the S point nearest
the middle of the S data). `Tau` defaults to 1 µs ([X06]). **Eq. 6.49's residual** is the share of the RF
conductances no single current reproduces — RMS `|∇IdRF − (Gm, Gds)|` over RMS `|(Gm, Gds)|` — zero when 6.49 holds
(measured 0.08 % on conservative data). Testing 6.49 by differencing the conductances was tried and dropped: on a
0.3 V grid it called exactly conservative data 86 % non-conservative.

### 9.6 The record and the cache

`Steps.Current` = `{ Network, Settings, Result }`. `Result.Constituents` are written by
`CmodelFile.ConstituentJson`, so the `.cmc` and the `.cmodel` cannot spell one differently; the arrays are one
grouped `.npy` in `<name>.cmc-cache/current/<key>.npy`, `key` a SHA-256 of their bytes, written by a temporary file
and a rename after the fit, other entries removed. `CmcFile.Validate` parses `Steps.Current` (an unknown key is a
refusal by field); other steps stay opaque. `NetworkFit.StaleSteps` names the step when the network moves on.

## 10. The charge constituents (MC-8)

`ChargeFit` (`src/Design/Modeling/`): `Gather` (the capacitance data, a `ChargeData`), `Run` from the open data or from
a `ChargeData`, `Apply` into `Steps.Charge`, `FitFile`, `Load`, and for the report and the window `Model` (the fitted
charges and currents as a `ComposedModel`), `Capacitances` and `Slice` (each `Cij` along one voltage with the other
fixed — MC-15's 1-D plots). Settings and record: `ChargeFitSettings`, `ChargeFitRecord`; templates: `ChargeTemplates`.

### 10.1 Training data (R-mc8-1)

Every set with intrinsic S (or those named), one trap state as for the currents; two or more temperatures make `T` an
input of an ANN charge (§12.1). At each point,
`C11 … C22` averaged over the flatness band with flagged frequencies left out. A point whose worse **diagonal**
flatness (`C11` or `C22` — the diagonals never cross zero, so theirs is always defined) exceeds `FlatnessLimit`
(0.1) weighs `(limit/flatness)²`, and the report counts them. Both charges are zero at the anchor: `(0, 0)` or the
stated point, moved to the nearest data point when it lies outside the hull.

**Capacitance tables (MC-20).** A `SimulatedTables` set may carry the capacitances themselves: the matrix
`C[point, 2, 2]` (`Cij = ∂Qi/∂vj`, the `Y = jωC` convention, so `C12 = −Cgd`) or the circuit set `cgs`, `cgd`, `cds`
and optionally `cm`, which `DataImport` turns into the matrix with `IntrinsicQuantities`' relations inverted
(`C11 = Cgs + Cgd`, `C12 = −Cgd`, `C21 = Cm − Cgd`, `C22 = Cds + Cgd`); an absent `cm` is 0, and the extraction says the
model then has no transcapacitance. `CmcFile.Validate` refuses both spellings in one set, a partial circuit set, a
table on another kind, and a table away from `ReferencePlane=intrinsic` — capacitances cannot be de-embedded through
the network without their frequency response. (The import cache's settings probe keeps the plane for that reason;
its key reads no condition.)
- **Alone**, `IntrinsicExtraction` writes `C11 … C22` and the circuit set on a one-point frequency axis at 0 Hz, with
  `Flagged` 0, `Band` [0, 0] and flatness 0 for the capacitances (NaN for `Gm`, `Gds`, `Tau`), so `Gather` reads it
  exactly as it reads S, each point weighing 1, and only adds a report line naming the spelling. There is no `Y`,
  `Gm`, `Gds` or `Tau` cube: the network fit (which reads `NormalisedData.S`), the RF current terms, dispersion, the
  shift coupling and τth all find no admittance; a step that names such a set says it has none
  (`IntrinsicExtraction.NoAdmittance`). The MC-17 minimums apply as for S points.
- **Beside Y or S**, Y feeds the charges and the table is cross-checked: the largest `|Cij(table) − Im Yij/ω|` over
  every point and unflagged frequency, relative to the point's largest table entry, as one note in D6's form —
  `max |ΔC| 0.02 % between its capacitance table and Im Y/ω`.
- `DataImport.CapacitanceSource` (`Y`, `table`, `circuit set`) is what `model explain` and the Data step's set list
  show. `DataHygiene` adds one set-level `CapacitanceSign` flag (empty point id) when a matrix-spelled table has
  `C12 > 0` at most of its points; `DataHygiene.SetWarnings` turns it into the warning `model import` and the window
  print. The intrinsic cache key moved to `mc20-1`.

### 10.2 Methods (R-mc8-2)

- **ANN** (default, 2 × 8 `tanh`, 3000 iterations): MC-6 derivative training, `∂Q/∂vgs → Ci1`, `∂Q/∂vds → Ci2`,
  anchored. Domain: the hull; charges are continued linearly (`G_x = 0`). Measured on the synthetic device through
  the GaN network: held-out capacitances within 0.49 % of the row's largest, the charges within 0.06 % of their range.
- **Table**: each capacitance interpolated over the points, both paths of [P1] Fig. 6.16 integrated onto the hull's
  bounding grid (`LineIntegral.OntoGrid`, adaptive Simpson, cumulative over the sites), `Path` A, B or their mean
  (default) kept as MC-2's table; the RMS of `Q_A − Q_B` over the charge's range is recorded. **The interpolant is a
  network by default**: an RBF through each capacitance alone is exact at the points but the two are not mutually
  conservative between them, and the integrals carry that across the domain — C11 fitted to 15.6 % of its range
  through an RBF, 0.34 % through a network.
- **Equation**: `Depletion` ([P1] eq. 6.11 for a gate–source and a gate–drain junction, `min`/`max` so neither branch
  is evaluated outside its range, `Fc` locked at 0.8) or `Angelov` (each junction Angelov's `Cp + C0·(1 + tanh ψ1)·(1 ± tanh ψ2)` —
  a constant plus a step from 0 to `2·C0` in its own voltage, switched by `tanh` in `vds` — integrated along that
  voltage as `Cp·v + C0·(v + ln(cosh ψ1)/P1)·(1 ± tanh ψ2)`), or the user's own; fitted by
  `EquationFit` with **derivative terms** (the expression's gradient lanes are the targets), then shifted by a
  parameter `Qanchor` so it is zero at the anchor.
- **`Linear`** (MC-17): `Qg = C11·vgs + C12·vds`, `Qd = C21·vgs + C22·vds` — matrix form, so each charge's two
  parameters are its own two targets. Each starts at its data's mean, bounded at ±100× the row's larger typical size
  (unbounded in sign); one point is fitted exactly, two or three to the least-squares constant matrix, and the report
  gives each value's RMS spread about it and the circuit set (`Cgs = C11 + C12`, `Cgd = −C12`, `Cds = C22 + C12`,
  `Cm = C21 − C12`). A charge function like the others, so conserving by construction; an equation, so no domain and
  no extrapolation. `ChargeFit.LinearProvenance` is the line the `.cmodel`'s `Provenance.Charges` and `model explain`
  carry — `Qg, Qd: Linear from N bias point(s)`, N from the record's `Points` — so it is never read as nonlinear.

**The minimum is the method's** (MC-17 R-mc17-2, `ChargeFit.Require`, run by every fit; `Gather` applies none). An ANN
or Table charge, and the drift decomposition, need `SurfaceMinimum` (4) points spanning an area of the bias plane, and
the refusal (`ChargeFit.Refusal`, also the window's tooltip on the method) names the way out: `3 usable S point(s); an
ANN charge needs at least 4 — the Linear equation fits from one.` An equation needs as many targets (two per point) as
free parameters, checked in its fit. A default ANN with too few points is refused, never switched to `Linear`. Points
that span no area give `ChargeData` a degenerate domain — the points' extremes, so `SpansArea` is false — and the
anchor stays where it was stated, since only an equation, defined everywhere, is fitted to them. The plots skip the
charge surfaces then, and a C–V slice over one level sweeps a volt either side of it (`ChargeFit.SliceSpan`).

**Typed charges** (MC-18, `ChargeFit.Typed`). With no S, Y or capacitance data — no set `Gather` would take
capacitances from — nothing is fitted: the step writes `Linear` constituents from `Settings.Typed`, the circuit set
`Cgs`, `Cgd`, `Cds`, `Cm` (each 0 by default; the three two-terminal values never negative), converted by R-mc17-1's
relations read backwards (`TypedCharges.Matrix`: `C11 = Cgs + Cgd`, `C12 = −Cgd`, `C21 = Cm − Cgd`, `C22 = Cds + Cgd`)
and shifted to zero at the anchor. The circuit set is the stored form because it is what a datasheet gives
(`Cgd = Crss`, `Cgs = Ciss − Crss`, `Cds = Coss − Crss`), and the form bias-dependent datasheet curves will take. The
record carries `Typed`, no arrays, no errors and `Points` 0; `LinearProvenance` then reads `Qg, Qd: typed (…)` with the
four values. All four at zero is a legitimate DC-only model and is said (`ChargeFit.ZeroLine`) on the step, at export
and in `model explain`. `Typed` beside such data is refused naming both — typing would discard it — rather than
silently preferring either. `fit all` with no S-parameters holds the network at its values with one line
(`ModelCommands.NetworkHeld`); naming the network step still refuses.

### 10.3 The drift decomposition (R-mc8-3)

`Qg = QGS(vgs) + QGD(vgd) + Qdrift(vgd, Id)`, `Qd = −QGD(vgd) − (1 − λ)·Qdrift(vgd, Id) + QDS(vds)` ([I11] eq. 3).
`Id` is the current step's channel current as the core evaluates it (`ComposedCoreProgram.Channel`: `IdRF` when
dispersive), and the core takes `∂/∂v` through it by the chain rule, so the charges inherit whatever the current
depends on — when MC-10's traps arrive, `Qg` depends on the trap states through `Id`, D10's one exception.

1. `QGS` and `QGD`: 1-D adjoint fits to `Cgs = C11 + C12` and `Cgd = −C12` at the points with `|vds| ≤ VdsZero`
   (0.05 V) — [I11] §III defines the depletion charges as what is measured there.
2. `Qdrift`: with `R1 = C11 − QGS' − QGD'` and `R2 = C12 + QGD'`, the decomposition says `R1 = ∂Qdr/∂vgd + τ·gm` and
   `R2 = −∂Qdr/∂vgd + τ·gds`, so **exactly** `τ = (R1 + R2)/(gm + gds)` ([I11] eq. 4's first line) and
   `∂Qdr/∂vgd = (R1·gds − R2·gm)/(gm + gds)`. [I11] eq. 4 prints the second as `½(Cii-sum terms)`, which drops
   `τ·(gm − gds)/2`; the exact form is used. A point weighs `(gm + gds)²` relative to the largest — the solve's
   conditioning — and `Qdrift = 0` along `Id = 0` is 21 value targets trained with the derivatives.
3. `QDS`: a 1-D fit to `C22 + C12 − λ·∂Qdrift/∂vds` at every point. Its residual is the spread of `Cds` across `vgs`
   at fixed `vds` — what the decomposition cannot represent — recorded as `CdsSpread` and reported in one line.

It needs the current step (refused otherwise). A training problem whose values are all one number now takes its
output scale from its derivatives (`AnnTrainer`), since zero targets carry no size.

### 10.4 The report and the record (R-mc8-4)

Per capacitance, the RMS of the model's `∂Q/∂v` against it over every point, absolute and as a fraction of its range.
`Steps.Charge` = `{ Network, Settings, Result }` as `Steps.Current`; arrays in `<name>.cmc-cache/charge/<key>.npy`.

## 11. Check Charge Conservation (MC-8)

`ConservationCheck` (`src/Design/Modeling/`), numerics `CurlMetric` and `LineIntegral` (`src/Engine/Modeling/`). Each
part returns a number and, where it has one, a map; `RunFile`/`Run` do all three for a `.cmc`, read-only. With fewer
than 4 points across the bias plane (MC-17), (a) is not run — one line naming the point count, `Data` null, not a
failure — and (b) reads the model over a box a volt either side of the points, where a linear charge's curl is
exactly zero; (c) is unchanged. Typed charges (MC-18) have no data: (a) is not run, and (b) and (c) read the model over
and at the middle of the current step's intrinsic I–V points (`ChargeFit.TypedRegion`).

- **(a) The data** (`Data`): per row the curl `∂Ci1/∂vds − ∂Ci2/∂vgs` by central differences of a smooth interpolant
  — a network per capacitance by default — normalised by the RMS over the points of `√((t1² + t2²)/2)`, as a map
  and an RMS; and the two-path difference over the grid sites inside the hull, as a fraction of the charge's range.
  An RBF interpolant read 33 % curl on exactly conservative synthetic data (its errors at the hull's edges, divided
  by terms that are small); the networks read 0.33 % and 1.5 %. **The curl is read only away from the hull's edge**
  (`EdgeMargin`, 2 % of each axis' span): a value interpolant's derivative across its edge is constrained from one
  side only, and on conservative depletion data over a rectilinear grid the network read 0.43 % inside and 54 % on
  the edge — a third of a grid's points, and an RMS of 32 % that reported a defect the data does not have. Edge
  points are NaN in the map; a hull with no interior point is read everywhere.
- **(b) The model** (`Model`): the same curl on a capacitance matrix over a grid (step 0.05 V), differenced a tenth of
  the step apart. On a device in a `.cnl` (`DeviceCapacitance`): the instance's one nonlinear component, ports 1 and
  2 gate–source and drain–source sharing a source, any further ports its own state nodes — solved at DC, eliminated
  at the check's frequency, `C = Im Y / ω` — so a capacitance written through an auxiliary node is read as a
  charge's `∂q/∂v` is. The transcapacitance's RMS is reported as information ([P1] §6.3.8).
- **(c) Large signal** (`LargeSignal`, `Drive`): the device alone on a testbench of ideal tone sources through 1 mΩ
  (an ideal source on an HB interface node has no finite admittance), solved by HB at order ≥ 5 at `f` and `2f`;
  `ΔIdc = Igate,dc(2f) − Igate,dc(f)`, relative to the fundamental gate current at `f`, and one line through the gate
  bias resistor. **The drain is driven too, in quadrature** (`DrainAmplitude`, 1 V): with the drain held at a
  constant voltage the trajectory is a line in `(vgs, vds)`, `∮C11 dvgs = 0` along it for any `C11`, and no
  capacitance makes DC — the check as first written could not fail. An ellipse encloses area, the same ellipse at
  every `f` makes the displacement DC exactly `f·∮C·dv`, and conduction DC cancels in the difference.
- The composer's model is run as a `ComposedFET` of a temporary `.cmodel` whose network joins each pin to its
  intrinsic one through 1 µΩ (`ChargeFit.IntrinsicNetwork`), with zero currents until the current step has run.

## 12. Temperature and self-heating (MC-9)

`ThermalFit` (with `TemperaturePlan`, `ThermalHeating`), `ThermalFitSettings` (with `ThermalFitRecord`); the
`.cmc` block is `Steps.Thermal`.

### 12.1 Temperature as an input (R-mc9-1, R-mc9-4)

`ThermalFit.Plan` takes the distinct ambient temperatures (within 0.01 °C) of every I–V and S set. **One**: the
model is `Single` at `T0`, no constituent reads `T`, self-heating is unavailable and the reason is the only text —
"one temperature" — and a run at another `temp` warns once (§4.4). **Two or more**: the current step's points carry
`T` and every table or ANN constituent gains it as a third input — an ANN's third input, a table's third axis (spline
in `T` too, sites every `TemperatureStep`, 25 °C by default; such a table re-grids through a network, the RBF being
two-dimensional), and an equation may read `T`. A template's `TemperatureCoefficients` names parameters made linear
in temperature, `p → (p + p_T·(T − Tnom))`, `p_T` free from 0 within the parameter's own range over the data's span,
`Tnom` the lowest temperature, locked. Absent, it is the template's current scale (`CurrentTemplate.Scale`: Angelov
`Ipk`, Curtice and Statz `Beta`, Materka `Idss`; none for the cubic and the diode); an empty list is none. The domain's `Box` holds `T`'s training interval;
`ThermalFit.ModelTemperature` gives the `Range` covering it and every ambient.

What `T` is for each point:

| Data | `T` |
|---|---|
| pulsed, from a quiescent bias that dissipates nothing (or states no current) | the chuck temperature |
| pulsed, from a dissipating quiescent bias | chuck + `Rth·Pq` once `Rth` is known; until then the chuck, with one line |
| DC, self-heating modelled | the junction temperature `Tamb + Rth(Tamb)·P`, `P = ig·vgsi + id·vdsi` |
| DC, self-heating not modelled | `Tamb`, with one line: the DC heating is embedded statically |

The charges gain `T` only when their S data spans two or more temperatures ([X10] eqs. 1.1, 1.5), each S point at its
ambient (its DC junction temperature with self-heating); otherwise one line says they are temperature-independent.
Only an ANN charge takes `T` (one line names a Table, Equation or drift charge fitted over every temperature's points
instead). The adjoint problem then has three inputs: the capacitances constrain `∂Q/∂v`, and the charge's zero holds
at the anchor bias at **every** temperature — `∂Q/∂T = 0` at nine temperatures along it plus the value at the middle —
because a charge moving with `T` there would carry a displacement current `dQ/dT·dT/dt` nobody measured.

With self-heating an ANN `Id` trains by MC-6's combined mode: the I–V values at each point's `Tj`, and every
SParams point's band-averaged RF `Gm`/`Gds` as `∂Id/∂vgs`, `∂Id/∂vds` at `(vgs, vds, Tj)`, `Tj` frozen at the point's DC
value ([X03] §IV-C, [X10] §VI) — so one function gives both of [P1] eq. 6.69's output conductances. A Table or
Equation `Id` trains on the values only, with one line. The Gm/Gds-against-DC-slopes report is skipped (a self-heated
DC slope includes the heating); without self-heating it compares each temperature's points among themselves.

### 12.2 The thermal node (R-mc9-2)

The convention is the compact-model one for an exposed thermal terminal: the node whose voltage is `ΔT` carries the
power, and the model's own network goes from it to thermal ground — [P1] Fig. 6.32's `Rth ∥ Cth` from `Tj` to `T0`;
[R11] ch. 7's fourth terminal "loaded by the thermal impedance"; [T20] §4.3's lumped network on the model's thermal
node. So **a network on pin 4 sits in parallel with the model's own** (`Rth ∥ Rext` at DC): the brief's gate asked
for "series", and the owner chose the convention the references and the industry use.

`ComposedNetworkTemplate.Synthesize` adds `R:Rth t 0` and `C:Cth t 0` (one pole), or a Foster ladder `t —(Rth1 ∥ Cth1)—
t_1 — … —(Rthn ∥ Cthn)— 0`, values scaled by D9 and `RthAt(temp)/Rth`. With four nets `t` is the cell's fourth port;
with three it is an internal net, still probed as `V(X1.t)`. A ladder's impedance is `Σ Ri/(1 + jωRiCi)`.

### 12.3 Rth (R-mc9-3)

`ThermalFit.Run` with `SelfHeating` and no typed `Rth`:

1. **The isothermal model.** `CurrentFit.IsothermalId` trains `Id(vgs, vds, T)` on the pulsed sets (default: every
   PulsedIv set whose quiescent bias does not dissipate; a dissipating one named is refused) by `Isothermal` (default:
   the current step's `Id` method); fewer than two chuck temperatures is a refusal that says to type `Rth`.
2. **Each DC point's junction temperature** — the `T` at which that model gives the point's channel current
   (`id + IgGd` by the current step's gate split): a 5 K scan from 50 K below the ambient to 500 K above, then
   bisection to 1 mK at the first sign change. No root: unresolved, counted.
3. Points below `MinPower` (0.1) of the largest DC power are left out and counted — their rise is not resolvable
   against the isothermal model's error.
4. **The fit**: `Tj − Tamb = Rth·P` through the origin, or (`RthModel: Linear`) `(Rth + slope·(Tamb − Tref))·P`,
   `Tref` the lowest DC ambient. The record keeps the RMS residual in K and relative to the RMS rise, the counts, and
   the vgs, vds and power spans of the points used.

A typed `Rth` skips all of it, and the current step "de-heats" the DC data by putting each point at its `Tj` — which
needs two or more ambients for `T` to be an input at all.

**The dynamics** are one of: `Cth`; `TauTh` (`Cth = τ/Rth`); a `Ladder` of `{ Fraction, Tau }` (`Ri = f·Rth`,
`Ci = τ/Ri`, fractions adding to 1); or `FitTau` — one `τ` for every S point's `Re Y22(f) = a + b/(1 + (2πfτ)²)`, each
point's `a` and `b` solved exactly per trial `τ`, searched in log `τ` (a 61-point scan, then golden section). `FitTau` is
refused while `Steps.Traps.Settings.Model` names a trap model: in GaN that roll-off also holds trap time constants,
measured near equilibrium ([N22] §III). `ThermalFit.TauFitNote` is the window's tooltip for it. Without any of them
the record has no poles, one line says so, and `ThermalFit.Network` gives no thermal network.

Every long part takes the `RunControl`; `FitFile` writes the document only when the run completes (D13).

## 13. Traps (MC-10)

`src/Core/Devices/Composed/Traps/` holds the contract and the models; `TrapFit` (with `TrapMapping`) and
`TrapFitSettings` (with `TrapFitRecord`) the step, whose `.cmc` block is `Steps.Traps`.

### 13.1 The contract and the registry (R-mc10-1)

A trap model is a `TrapModelDefinition` registered by name in `TrapModels` — its states (at most four, `phi1` … in lane
order), its parameters with units, and `Build`, which compiles a `Traps` block into `TrapDynamics`: per state the node's
current and charge `(i, q)` from `(phi, vgs, vds, T)` with exact derivatives, its DC value, the direction capture moves
it (`CaptureSign`) and its emission time when it has one. `RateTrapDynamics` is the form both built-ins take, a rate
`dphi/dt = D`: the node carries `q = C·phi` and `i = −C·D` with one normalised `C`, so the node voltage IS the state in
volts. A developer adds a model by implementing the same contract and registering it; nothing else names a model.

**Why `C` is 1 mF.** Newton is invariant to the scaling of a row, so `C` only trades two floors, both measured on the
gate device. The DC engine's `gmin` moves a DC state by `gmin·phi·τ_emit/C` — 1e-12 of it at 1 mF and 1 ms (at 1 F,
1e-14). In HB the round-off of the charge's DC part (`C` times tens of volts), multiplied by `kω`, floors the residual
near `6e-5·C` A at 2 GHz with eight harmonics — 6e-8 A at 1 mF against the 1e-6 A default tolerance; at 1 F HB stalled
at 1.6e-7 A with backtracking even at −20 dB drive, and at 10 mF at 3e-9 A against a 1e-9 tolerance.

In the core (`ComposedCoreModel`) each enabled state is a ground-referenced port after the two intrinsic ones, minted
by the elaborator through MC-1's seam; the states are seed lanes after `T`, so `Id`'s and `Qd`'s gradients by them land
in those ports' columns, and the nodes read the intrinsic `vgs`, `vds` and the device temperature (`temp + ΔT` when it
self-heats). With MC-8's drift charge `Qg` sees the states through the channel current alone. A trap node's coupling to
the gate and drain, and the drain's to the state, are controlled conductances, so passivated the node stands alone.

### 13.2 `CaptureEmission` (R-mc10-2)

Gate-lag `dphi1/dt = (vgs − phi1)/τ_emit,1 − f1(phi1 − vgs)`; drain-lag `dphi2/dt = (vds − phi2)/τ_emit,2 +
f2(vds − phi2)` ([P1] eqs. 6.73–6.74), with `f(x) = (x_ref/τ_capt)·(e^((x − x_ref)/Vc) − e^(−x_ref/Vc))`: `f(0) = 0`, so
DC is exactly `phi = v`; at most 0.2 V/s for a negative gap with the defaults; 10 ns at a 1 V gap and ~0.1 ms at
0.5 V. The exponential is continued by its tangent above `e^20` (a capture ~2e-9 of `τ_capt`) so a Newton start a drain
voltage away stays finite — C¹ there, and no solution sits there. Defaults: `TauCapt` 10 ns, `TauEmit` 1 ms,
`Xref` 1 V, `Vc` 50 mV; drain-lag on and gate-lag off when the model is first chosen. **ThermallyActivated** emission
(the setting `Emission`, a state's `Ea` in the block): `1/τ_emit(T) = (1/τ_emit)·(T/Tref)²·e^(−(Ea/k)(1/T − 1/Tref))`,
T in kelvin, `Tref` in °C as everywhere else; capture stays fixed ([N22]). `TrapFitSettings.TimeConstantsNote` is the
step's tooltip: CW HB depends on the time constants only through their ratio.

Under CW with `τ_capt ≪ 1/f ≪ τ_emit` the state settles where a cycle's capture balances a cycle's emission — [P1]
eq. 6.75 with the gap kept: driven ±4 V about 6 V at 1 MHz, `phi2` sits 0.638 V below the drain's peak, on the cycle
balance of its own dynamics to 1e-4 V, moving 3.5 mV over the cycle (one cycle's emission).

### 13.3 `Custom` (R-mc10-3)

Per state a `Dynamics` (`dphi/dt`) expression over the state itself, `vgs`, `vds`, `T` (°C) and the block's
parameters, and a `Dc` expression over the same less the state, compiled by the one expression engine with its dual numbers. When built, every name is checked and the
`Dc` must be a fixed point — `|D(phi_dc)| ≤ 1e-9 V·|∂D/∂phi|` at 30 biases (−5 … 1 V, 0 … 50 V) and 25 and 125 °C. A
`Custom` state follows the convention odd states capture downward (like gate-lag) and even states upward. Written to
equal `CaptureEmission` (its exponential continued by `if(u<20, exp(u), exp(20)*(1+u−20))`) it gives the same DC and S
exactly and HB within 1e-12.

### 13.4 The data's trap states (R-mc10-4)

`IntrinsicExtraction` re-references a pulsed set's `Quiescent` bias and `PrePulse` level exactly as its points (the
`Setting` cube: `Vgsi`, `Vdsi`, and whether the drain current was stated; the cache key moved to `mc10-1`). The
trap-setting bias is the pre-pulse level when there is one, else the quiescent point. `TrapFit.Map`, per enabled state:
a pulsed point is **toward** the setting when its own DC state lies on the setting's side in the direction capture moves
(`CaptureSign`) — pulsed no lower in `vgs` for gate-lag, no higher in `vds` for drain-lag — and takes the setting's
state exactly (only slow emission acted); otherwise **beyond**, and the set's `TrapState` decides: `auto` leaves it as
validation (counted against every state it lies beyond, one line per state; an excluded point counts nowhere), `setting` maps it to the setting's state, `extreme` to the pulse's own
(`max` for drain-lag, `min` for gate-lag; [X10] eqs. 1.8–1.9). DC points sit at their DC state. Refused: a pulsed set
with neither bias; a bias that states no drain current while the data shows it conducting (the nearest measured point,
both voltages normalised by the data's spans, times its drain voltage, above 1 % of the largest point power). One line
per timing (`SampleDelay`, `PulseWidth`, `PrePulse.Gap`) above a tenth of `τ_emit`. The levels are the distinct settings
(within 1 mV) of the sets that contribute a used point: two or more fit fully, one fits with one line ("drain-lag
from one trap level, 10 V") and is recorded in the block's `Levels`, none refuses the state naming what it needs. The
step is unavailable under the trained coupling without pulsed I–V — "no pulsed I–V" is the only text; the shift
coupling (§13.6) needs none.

### 13.5 Fitting with traps (R-mc10-5, R-mc10-6)

The current step, with traps modelled, maps its sets instead of refusing a mix of trap states: `Id` takes the enabled
states as further inputs (beyond points left out under `auto`; the DC points too when `DcIv` is `Validation`), `Ig`
never does and trains on every point. By any method: an ANN, and a table's re-gridding network, train by MC-6's
combined mode on the I–V values and on every CW S point's RF `Gm`/`Gds` as derivatives with the trap states (and `Tj`)
frozen at their DC values ([X10] §VI); a table gains an axis per state every `TrapStep` (1 V); an equation template
gains the saturating terms `vgs → vgs − a1·tanh((vgs − phi1)/c1) − a2·tanh((phi2 − vds)/c2)` (a `Custom` model's
`phi3`, `phi4` likewise, `a3`/`c3` and `a4`/`c4`, gate-like or drain-like by their capture direction) and, on the first
drain-like state, `α → α·(1 + ak·tanh((phi2 − vds)/ck))` on its knee parameter (`Gamma` for `CurticeCubic`), amplitudes from 0, widths
from 5 V — zero at DC by construction — and trains on values only, with one line. The DC I–V error at the DC trap
states is recorded (`DcIv`) and reported apart from the training error. Dispersion and drain–source exchange are
refused alongside traps. The record carries the `Traps` block, levels included, exactly as the `.cmodel` will.

`Qd` takes the states only when pulsed S-parameters give every enabled state a level (one warns), and only as an ANN:
its adjoint problem gains a lane per state, the zero held along each at the anchor as for `T`. Otherwise one line says
it is trap-independent. `Qg` never takes them. `TrapFit.FitFile` runs the step and re-trains the currents through one
`RunControl`, writing nothing when cancelled (D13).

### 13.6 The threshold-shift coupling (MC-19)

A trained coupling needs pulsed I–V, because only pulsed data holds currents at trap states away from DC. Without it
MC-10 built no traps, against D10's rule that less data gives a more limited model, never a refusal. The `Shift`
coupling keeps `CaptureEmission`'s dynamics (typed time constants) and takes the states into the current through a
threshold shift:

`Id(vgs, vds, T, φ) = Id₀(vgs − ΔV, vds, T)`, `ΔV = Σₖ aₖ·sₖ·(φₖ − φₖ,DC(vgs, vds, T))`

over the enabled states, `sₖ` the state's `CaptureSign` and `φₖ,DC` its `DcValue`; for `CaptureEmission`,
`ΔV = a_d·(φ2 − vds) + a_g·(vgs − φ1)` with `a_d` = `ShiftDrain`, `a_g` = `ShiftGate`. It is [P1] eq. 6.70's trapped
current — the drain current a function of the trap states as well as the voltages — specialised to the back-gating
reading of §6.6.4 ([P21]): the trapped charge acts as a back gate whose effect on the channel is a shift of the
effective gate voltage. It is also the first-order term of the saturating `vgs → vgs − a1·tanh((vgs − phi1)/c1) −
a2·tanh((phi2 − vds)/c2)` that MC-10's equation templates gain (§13.5), with `aₖ = ak/ck`. At any DC point
`φ = φ_DC`, so `ΔV = 0` and the DC is `Id₀`'s exactly — measured 1.25e-11 relative over a 63-point grid against the
trap-free device, the residue of the DC engine's `gmin` on the state nodes.

**In the core.** `ComposedCoreProgram.Drain` evaluates `Id₀` at `vgs − ΔV` and chains its gradient through `ΔV`:
`∂Id/∂x = ∂Id₀/∂x − gm₀·∂ΔV/∂x` over every lane — `vgs`, `vds`, `T` and each state, with `∂ΔV/∂φₖ = aₖ·sₖ` and
`∂ΔV/∂(vgs, vds, T) = −Σ aₖ·sₖ·∇φₖ,DC` from `TrapDynamics.DcValue`'s gradient overload. `Ig`, `Qg` and `Qd` take no
shift, and a drift charge reads the unshifted `Id₀` (`DriftChannel`), so the charges stay trap-independent.
`TrapShift` holds the compiled `aₖ·sₖ`; `Channel` returns the shifted current, so a caller holding the states at their
DC values reads the frozen-trap conductances.

**Fitting the strengths (R-mc19-3).** Every CW S frequency lies far above `1/τ_emit`, so the states are frozen at their
DC values; holding `φ` fixed,

- `gm_RF = ∂Id/∂vgs = gm₀·(1 − ∂ΔV/∂vgs) = (1 − a_g)·gm₀`
- `gds_RF = ∂Id/∂vds = gds₀ − gm₀·∂ΔV/∂vds = gds₀ + a_d·gm₀`

at each point's DC bias, where `ΔV = 0`. Each relation is linear in one strength through the origin, so each is one
least-squares slope, `a = Σ gm₀·y / Σ gm₀²` with `y = gds_RF − gds₀` (`a_d`) or `gm₀ − gm_RF` (`a_g`), standard error
`√(Σr²/(n − 1) / Σ gm₀²)`. `gm_RF` and `gds_RF` are `Re Y21` and `Re Y22` of the intrinsic S averaged over the flatness
band, flagged frequencies left out — as the dispersion fit reads them (`TrapFit.ShiftPoints`). A point whose `gm₀` is
below the current step's `IdFloor` of the largest is left out. With self-heating modelled and `Rth` known, `gm₀` and
`gds₀` are the isothermal derivatives at `Tj = Tamb + Rth·P`, so the thermal part of the DC/RF difference is not read
as traps; without self-heating the report says `ShiftDrain` holds it too. The record carries each strength with its
error (none when typed) and the RMS residuals of both relations. On the gate's truth (`a_d` 0.05, `a_g` 0.1, simulated
by circuitRF as DC I–V and 63-bias S) both came back within 0.002 %, the residuals 0.001 % of the RF conductances.

**The flow (R-mc19-4, R-mc19-5).** `Coupling` is a setting written when it is `Shift`. The default stays `Trained` —
MC-10's gate still holds that an unset coupling without pulsed I–V is unavailable — so the window, and `model fit`
choosing a model, write `Shift` when the document has no pulsed I–V; the coupling list then offers `Shift` alone, with
`TrapFit.ShiftOnly` as its line. Under `Shift` the current step fits a trap-free `Id₀`: it maps nothing, leaves out
pulsed I–V beside DC (one line), and does not train on the RF conductances under self-heating, because those are what
the strengths are fitted to. `AllSteps` keeps its order: the trap step's `FitFile` then fits the strengths and leaves
the current as it is. A strength typed in `Settings.Parameters` is locked and not fitted; with no S points to fit an
untyped strength, the step refuses naming both ways out (type it, or add multi-bias S). The charge step prints MC-10's
trap-independent line. Under drive the states lower the current: on the gate's truth at class AB (`Vgs` −1 V, `Vds`
10 V, 1 GHz into 150 Ω) the 3 dB compression point falls from 25.13 to 19.19 dBm, 5.95 dB lower. Not in this phase:
fitting the time constants, bias-dependent strengths, `Shift` for `Custom`, and combining `Shift` with pulsed-data
training.

## 14. Export, validation and the report (MC-11)

### 14.1 Staleness (R-mc11-1)

A fit stamps its `Steps.<step>` block with two digests as it writes: `Network` (`NetworkFit.Digest`, as MC-5 already
did for the current and charge steps — now every step) and `Data` (`CmcStamps.DataDigest`): the pads block, and every
data set a fit can read — its definition as the `.cmc` writes it and each source's SHA-256 from the cache index. The
network fit's own record carries `Data` too. `CmcStamps.StaleSteps` lists every step whose stamp differs from the document
now. A large-signal set is validation only and is left out, so adding one stales nothing; a step written before stamps
existed carries none and is never called stale by the data.

### 14.2 Export (`CmodelExport`)

`Assemble` builds the whole `ComposedModel` in memory and validates it with MC-1's validator; `Export` writes it by a
temporary file and a rename, so a refusal, failure or cancellation leaves no file.
- **Network**: the six-pin `.csch` as one `define extrinsic (pins…)` block (`PinNetwork.DefineBlock`), every variable a
  `parameters` entry holding its value in use as a number, `\n` line ends on every system; each variable's rule that is
  not `fixed`. No network: the 1 µΩ pass-through `ChargeFit` already uses.
- **Constituents** from the recorded current and charge steps with their arrays from the cache; `Gx`, `Dispersion` and
  `Symmetry` from the current step; `Thermal` from the thermal record when self-heating is on; `Traps` from the current
  record, or under a shift coupling the trap step's settings with its strengths (`TrapFit.ShiftBlock`).
- **Temperature**: `Single` at the current fit's data temperature unless a constituent reads `T`; then `Range` over the
  plan (or the constituents' own boxes at one temperature).
- **Refusals**, each naming its field: a Passive document; no `N0`; a stale step (all of them, in one sentence); an
  unfitted current step, a charge step not yet run (fitted or typed), or one whose arrays left the cache; traps modelled and not run; self-heating on and no
  thermal network.
- **Provenance**: the caller's `Created` (default now, UTC), the build's version without a commit suffix, each source's
  hash from the cache index, and a digest of the network and steps blocks.

### 14.3 The symbol and the placement (R-mc11-2, R-mc11-3)

`ModelSymbols`: pins bind by number, so a symbol must have pins 1, 2 and 3; a fourth only on a model with a thermal
node; none beyond. A library symbol qualifies only when its kind's own pins are `g, d, s` in that order (the FET laws) —
a JFET's or BJT's three pins mean something else in the same positions. A `.csym` is stored relative to the `.cmodel`
(the provider resolves it from there) and relative to the `.cmc` in `Steps.Export.Settings.Symbol`.
`ComposedFetPlacement.Payload` is `SchematicPersistence.SerializeSelection` of one `ComposedFET` (absolute `File`,
`N = N0`, the symbol) — the editor's own paste reads it.

### 14.4 Validation (`ModelValidation`, R-mc11-4, R-mc11-5)

The model is a `.cmodel` named by the caller or the current state exported to `<name>.cmc-cache/validate/model.cmodel`.
Every item but the pulsed one is a generated `.cnl` (`ValidationBench`) run through its verb's own engine call:

| item | testbench | measure |
|---|---|---|
| S (every CW S set) | ideal tees at the device-plane bias (MC-4's re-referencing, no network), ports at the data's Z0; the data's own frequencies | `PadDeembedding.Embed` (the inverse of `Remove`) back to the probe plane; \|ΔS\| median and 95th percentile over every entry; per-bias RMS |
| DC I–V (DC sets, simulated tables with currents) | voltage sources and probes; `ComposedCore` for intrinsic-plane data | RMS ΔId over the set's current range |
| Pulsed I–V | none: a DC solve cannot hold a trap state | the channel current at each point's MC-10 trap state and quiescent junction temperature, on MC-4's intrinsic data (`CurrentFit.Gather`) |
| Power sweep | `P1Tone` behind the stated source terminations and a gate tee, a `Tuner` load with its tee, probes at both device terminals, `parametric_sweep` over `Pin` wrapping `hb` | Pout (dBm), gain, PAE, DE, Id, Ig from the probes; RMS ΔPout (or Δgain) and ΔPAE (or ΔDE) |
| Two-tone | `PnTone`, two tones about the centre | carrier, IM3 (2f1 − f2), IM5 (3f1 − 2f2) by frequency on the mix axis |
| Loadpull file | a loadpull over the file's own loads through the loadpull engine (§14.5) | ΔPout and ΔPAE at each measured (load, drive) against the model's at that load, interpolated to that drive |

Biases run in parallel; a cancelled run throws and reports nothing. Items carry `.meas`/`.model` cubes on shared axes.

**Large-signal data** (`DataConditions.LargeSignal`): `Measurement` (`PowerSweep`, `TwoTone`, `Loadpull`), the
frequency, spacing, bias, `SourceZ`/`LoadZ` and their harmonic lists (a harmonic not listed is shorted). A CSV maps
`pin pout gain pae de id ig im3 im5` — powers in dBm, gain in dB, efficiencies in %, a unit on those refused unless it is
that one; a loadpull file is `Format: loadpull`, read through `RfCore.Loadpull.LoadpullFiles.Read` — the call the Data
Display and `render` make — so it is a `.spl`, `.lpcwave`, `.satwave` or `.cst` (brief LPW). A file of ONE load (a
`.satwave`, a one-block `.cst`) is also accepted as a `PowerSweep` set: its drive-up is the sweep (available power, Pout,
Gt, PAE, drain efficiency, the bias currents), and every condition the set does not state is the file's — frequency,
bias, source and load terminations, and the load's harmonic terminations where the waves measured them
(`GammaLoad_<n>f0`). So such a set needs nothing typed but `Measurement: PowerSweep`. The cache never imports a
large-signal set. The data is compared at the device plane: pads are not re-embedded for large signal.

### 14.5 Loadpull of a composed FET

A `ComposedFET`'s terminals sit behind its extrinsic network, so no nonlinear device touches the nets its tuners sit on.
`LoadpullEngine` takes such a tuner net into the HB interface as an extra node and reads Pout and the bias currents from
the tuners' own branch currents (the load tuner's Z_Port and choke, the source tuner's Z_Port and choke) through the
linear back-solver; a tuner on a device terminal reads the interface current exactly as before. Pout is therefore the
power into the load, read through the model's network — which is part of the model. So the `lp` and `lpp` verbs run a
composed FET like any other device.

Validation against a loadpull file generates an `lp` testbench over the file's own loads (the grid written as a `.gam`
beside it) and runs it through the loadpull engine, each load driven up to `ValidationSettings.Compression` (3 dB of Gt by
default; the model's ladder steps by the smallest gap between the measured drives and runs 10 dB past the highest).
`ValidationSettings.CompressionReference` (`Gt`, the default, or `Gp`) and `CompressionType` (`Gmax`, the default, or
`Gss`) choose the gain and where the depth is measured from, as a contour's compression does (brief LPW, R-lpw-7); the
engine is run with `GainType` set to match, and 3 dB further for `Gss`, since from the small-signal gain the same depth is
reached at a higher drive when the gain expands first. Both keys are written only when they differ from the default.
Model and measurement are compared **at compression**, never drive by drive: `AtCompression` reads each load's figures
where its own reference gain has fallen that far below its running maximum (or its first drive's, for `Gss`),
interpolated between the two drives either side, and
every figure the file carries is compared — Pout, Gt, PAE and drain efficiency (RMS), AM/PM, and Zin as the VSWR between
measured and model (`Mismatch`, |Z1 − Z2| / |Z1 + Z2*|, needs no reference). Each side's MXP and MXE at compression are
compared by their Pout or efficiency and by their load, as a VSWR. Only loads the MEASUREMENT reaches compression at count;
a file that reaches it nowhere is a note, not a failure. Tolerances: `PowerDb`, `EfficiencyPoints`, `AmPmDegrees` (5°)
and `ImpedanceVswr` (1.5). A multi-frequency file is compared at the stated `FrequencyHz`.

The report's PA modes run `ModelValidation.LoadSweep`: the same engine, one run per load and the loads in parallel (the
engine solves its loads in turn), each driven up to the report's compression or its PinMax. A fixed ladder solved every
rung to PinMax and kept only those up to compression; at 2 GHz, where this GaN's gain is ~19 dB, the rest was deep
overdrive that failed at every rung and a class ran for many minutes. The region of convergence is therefore the loads the
engine's continuation and cold retries bring to compression or the cap. It writes the loadpull DataSet under the canonical
names a measured file carries (`Pout_dBm`, `Gt_dB`, `Efficiency`, `PAE` in %, …), so RfCore's `LoadpullSurface` (the Data
Display's performance summary) reads it unchanged.

**The engine's first rung continues from the tickle when it fails.** The tickle sits tens of dB below PinStart; on a
high-gain stage behind a sharply matched source (this GaN at 2 GHz, source 16.6 + j179 Ω) that jump is not one Newton
solve, and every load stopped on its first rung from −10 dBm while the same loads walked cleanly to compression from
−30. `DriveLadder.ContinueThroughJump` now bisects from the tickle, as it already did from a previous rung; never for a
vacuous-tolerance refusal (`SinglePointResult.ToleranceVacuous`), which smaller steps would only hide, and continuity is
still never judged against the tickle.

### 14.6 The Model Validation Report (R-mc11-6)

`ModelValidationReport.Generate` → `ModelReport` (sections of DataSets, lines, conditions, scorecard rows and
testbenches, and the `ReportCard`); `Write` → a folder with `report.npy` (one group per section), `report.json` (the
card), `summary.txt` (the card as text) and `testbenches/`. The options (`ReportOptions`) live in
`Steps.Report.Settings`.

The report has two readers — the model's developer and its user, the designer, who may work elsewhere — and the card
(`ModelReportCard.cs`) is written for the second:
- **Scorecard** (the PDF's first page): one row per test, Green / Yellow / Red, with what the grade means for a design.
  A validation item grades its worst measure against the validation's own tolerance (within: Green; within
  `ReportGrades.WarnFactor` = 2× of it: Yellow; beyond: Red) and, apart, its convergence (none failed: Green; up to 5 %:
  Yellow; more: Red). Rth is banded at 10 % / 25 %, a charge's RMS at 2 % / 5 % of its range, a PA class's disc
  convergence at 95 % / 75 % (Red when the class's own load fails). A set validation could not compare is Yellow
  ("not checked"). The overall grade is the worst row.
- **Where the model is supported by data**: the drain current's and the charges' training domains (the hull of their
  training points, intrinsic), the hottest I–V point, the temperature mode, the checked frequency span, whether any
  large-signal data was compared, and what extrapolation does.
- **How the model was fitted**: each constituent's method — an equation and its template (named when the document's
  record holds the same constituent), a table and its grid, an ANN and its hidden layers (read from its weight arrays) —
  with the fit's held-out error; traps, self-heating, the extrinsic network and how it was fitted.
- **Data**: every set, what it was used for (I–V fit, C–V fit, checked), its points and its bias values — every value
  when there are few, the span and count when there are many.
- **Per test**, the conditions that reproduce it: bias, terminations to the top harmonic, frequency, ambient, Rth at
  that ambient, and the solver's harmonic count, Newton tolerance, FFT oversample and iteration cap. Every generated HB
  and loadpull testbench now WRITES those keys (`ValidationBench.HbKeys`, the analysis's own defaults) so the testbench
  and the report cannot disagree.
- **Coverage** (the first tab): every set's bias points at the device plane, and the PA bias.

The card carries no clock time, so the window's PDF and the verb's stay the same bytes.
- **DC**, **S**: the validation's items; S picks out the stated biases (default: lowest, middle, highest |S21| per set).
- **Thermal** (self-heating model, DC data): each DC point's equivalent junction temperature — where the isothermal model
  carries the measured current ([R11] §2.8) — against its power; the slope per ambient against `RthAt(ambient)`.
- **Charge**: the model's `Qg`, `Qd` against the line integral (mean of paths A and B, RBF interpolants) of the measured
  capacitances, as a difference map in % of each charge's range.
- **C–V**: `C11 … C22` and `Cgs = C11 + C12`, `Cgd = −C12`, `Cds = C22 + C12`, `Cm = C21 − C12` against vgs at a family
  of fixed vds and against vds at a family of fixed vgs (the outer axis is the legend), with the measured points nearest
  each fixed voltage.
- **PA classes** — harmonicaRF's method. Each class's intrinsic terminations to the fifth harmonic are
  `PaClassPresets.IntrinsicLoad` (Z0 = R_opt), carried to the extrinsic plane in closed form as `IntrinsicAbcd` does:
  the device's drain self-capacitance `C22` linearized at the intrinsic DC bias (R9D §3.4's linearized output
  capacitance) is taken off the target, and the model's own network, as the drain-side two-port with both gates
  AC-shorted (IntrinsicAbcd's no-feedback condition), is inverted bilinearly. A band at the map's pole takes its intrinsic
  value; a band needing a negative resistance keeps its reactance at the chart's edge, and both are named. The transform
  is `ComposedClassLoads` in `src/Harmonica`, which harmonicaRF's own presets call for a composed DUT
  (brief-modelcomposer-23); its drain two-port is the network as `ComposedNetworkTemplate.Synthesize` scales it for an
  instance, at the model's own `N0` and `m = 1` here, bit-identical to the raw network. The source's
  2f0 is the user's, at the extrinsic plane. The fundamental is load-pulled (§14.5) over a disc about the class's own
  fundamental load (the disc's Möbius automorphism, so every point stays on the chart): performance at that load, at MXP
  and MXE at the stated compression through `LoadpullSurface`, and the share of the disc that converged to that
  compression — the region of convergence. Defaults: the middle S frequency, half the largest measured vds (the drain swings
  to about twice its bias), the vgs where the model's current is 2 % of Imax there, `R_opt = 2·0.9·vds/Imax` with Imax
  the largest drain current in the I–V DATA, the source the conjugate of the model's input at that load. Imax is never
  read from the model at (highest vgs, highest vds): the data cannot reach that corner, so a network is unconstrained
  there.

The Data Display document and the dialog are the window's (MC-15); the `model report` noun is MC-13's.

## 15. Passive models (MC-12)

A `Passive` document turns an N-port S-parameter block — or a family of them over one geometry value — into a lumped
(or partly distributed) circuit, and exports it as an ordinary cell. Everything is in `src/Design/Modeling/Passive/`
except the vector fit, which is numerics (`src/Engine/Modeling/VectorFit.cs`).

### 15.1 Data (R-mc12-1)

`PassiveSParams` sets as MC-3 imports them; a family's geometry value comes from the bias pattern's `{geometry}` field
(`spiral_{geometry}t`, a unit allowed: `{geometry:um}`), from the name or the comments, onto `BiasPoint.Geometry` (NaN
otherwise; a cache written before the column existed reads NaN). `{geometry}` on an active set is refused.
`PassiveBlock.From` gives one block per non-excluded source.

### 15.2 Analysis (`PassiveAnalysis`, R-mc12-2)

One line each: the port count; reciprocity (`max |Sij − Sji|`, `TouchstoneHealth.ReciprocityError`); passivity (the
largest eigenvalue of `SᴴS`, i.e. σmax² from `RFNetwork.Passivity`, over the band; above `1 + 1e-6` the frequencies
are listed as a range); symmetry (`max |S11 − S22|`, port order reversed for N > 2); the low-frequency asymptotes
(series R and L — or series C — of the principal impedance, shunt C per port); the self-resonances (upward zero
crossings of the principal reactance, interpolated in log f by `PassiveMetrics.SelfResonance`, and the downward ones,
the parallel resonances an inductor's self-resonance is); and the vector fit. The **principal impedance** is Z for a
one-port and the π model's series branch `−1/Y21` otherwise; the shunts are the row sums of Y. These are also what a
library topology's starting values read.

### 15.3 Vector fitting (`VectorFit`, R-mc12-2)

Gustavsen and Semlyen's method, written from the paper, with poles shared by every response and real arithmetic (a
complex pole travels with its conjugate; the pair's basis is `1/(s − a) + 1/(s − a*)` and `j/(s − a) − j/(s − a*)`).
Frequencies are divided by the top angular frequency first. Relocation is the fast formulation: each response's rows
`[Φ, 1, s | −F·Φ]` are triangularised alone and only σ's rows of R are stacked — the same solution as the full system
at (responses)⁻² the cost, measured 8.9 s → 0.7 s on a non-rational 2-port that climbs to order 12. **The new poles are
the roots of the secular equation `1 + Σ r_k/(z − a_k) = 0`, found by an Aberth iteration** on
`p(z) = σ(z)·Π(z − a_k)`; near pole `a_i` the two `1/(z − a_i)` terms of `p′/p` are combined analytically, so a
converging fit — where every root sits next to an old pole — loses no digits. There is no general eigensolver in the
repository (`Dcim.cs` gives why) and this needs none. Unstable poles are reflected; order is chosen by error
(`FitByError`: the lowest order from 1 whose relative error is at most 1e-3, default maximum 12). Y is fitted unless
Y does not exist at every frequency or Z fits better.

### 15.4 The library (`PassiveTopologies`, R-mc12-3)

Embedded `.csch` cells under `Passive/Topologies/`, pins 1…N the ports, references ground — drawn by
`netlist --to-schematic` with numbered pins on labelled stubs, the stock extrinsic networks' construction. One-ports:
`series-rlc`, `parallel-rlc`, `inductor-skin` (`R0 + Rsk·√f` as a one-port `Z_Port`, L, and the self-resonance C),
`capacitor-esr-esl` (with pad C), `resistor-parasitics`. Two-ports: `pi`, `tee`, `bond-wire` (skin-effect R and L),
`via` (series R–L, `Cv/2` each side), `spiral` (series R–L, inter-winding C, `Cox` – `Rsub ∥ Csub` per side), `mim`,
`tline-pads` (`TLIN` with `E` at 1 GHz, pad C each side). Each entry carries its seed — closed forms of the analysis's
asymptotes and resonances (`Ls = 1/((2πf0)²·Cs)` at a series resonance, and so on), falling back to a typical value
where an asymptote has the wrong sign — and its **positive** non-R/L/C variables (`R0`, `Rsk`, `Zt`, `Et`), which get an
element's logarithmic coordinate and bounds. `PassiveNetwork` reads any of them — or a user's `.csch` — through
`PinNetwork` (which now also takes "N pins numbered 1…N") and solves S at the block's own Z0 with the engine.

### 15.5 The fit and the proposals (`PassiveFit`, R-mc12-4, R-mc12-5)

The residual is the topology's S minus the block's, real and imaginary parts of every entry at every frequency, each
frequency weighted: uniform, or a band whose frequencies weigh `Emphasis` (default 10) times the rest in the cost. The
search is MC-5's, now **`BoxLeastSquares`** (`src/Design/Modeling/`), which both fits share: `FitVariables` (starts,
bounds, locks, ties as coordinates of the unit box), the registry's global algorithm per seeded start, the
Levenberg–Marquardt polish, the identifiability report. Bounds default to 0 … 1000× the start (a library seed is cruder
than an extrinsic network's start, hence wider than MC-4's 100×). A full fit is LM from the start, then `Starts`
(default 2) global searches of 100 evaluations per variable each polished, keeping the best.

**Proposals.** Every library topology with the block's port count gets its seed and a short fit (LM, one 40-per-variable
global search, LM), and a user's drawn topology the same from its own values. They are ranked by

    AIC = n·ln(max(RSS, n·(1e-6)²)/n) + 2k

with n the number of real residuals and k the fitted variables. The floor — a |ΔS| of a part in a million — is what
makes two exact fits compare by element count alone; without it the comparison is between two numbers near 1e-30 that
measure only where each optimiser stopped. The top three are offered. Measured on the gates: the generating topology's
AIC is lower than the runner-up's by 2,200 to 13,000.

### 15.6 Families (`FamilyFit`, R-mc12-6)

One topology across the family. (1) Each training geometry is fitted on its own in ascending order: the first by a
proposal's short fit from its seed, each next by LM from its neighbour's solution and from its own seed, the lower
kept, a short search when both land more than ten times worse than the first. (2) Each fitted variable becomes a law
of the normalised geometry `x = (g − mid)/half` over the training range: a polynomial of degree 0–3 or
`(a0 + a1·x)/(1 + b1·x)` with `|b1| < 0.95` (no pole in the range), chosen by **leave-one-out** error, the simplest
form within 2 % of the best winning; a law that leaves the variable's bounds anywhere in the range is inadmissible, as
is a non-monotone one when `Monotone` is set. (3) Every law's coefficients are refined together by LM against every
training block. (4) The held-out geometry — the middle one, `sorted[(n−1)/2]`, unless named — is predicted and
compared; it never trains. A named one at an end of the range is an extrapolation and is predicted as one — the laws
as they are, without the bounds the fit holds them to. A family needs three training geometries besides it. Locked
variables stay constant — one locked with no value is held, at every geometry, where the first geometry's fit put it;
a tie holds exactly through the laws.

### 15.7 Export as a cell (`PassiveCellExport`, R-mc12-7)

Through `CellCreate.Create` (schematic view) and `CellCreate.WriteSymbolView`, which now takes the symbol to write —
the auto-generated N-pin box, or a chosen `.csym` whose pin count matches. The schematic is the topology with its VAR
block removed; the variables become `.ccell` parameters in base SI with the base unit (Ω, H, F), defaulting to the
fitted values. A family's cell has the geometry parameter first, defaulting to the held-out geometry (the nearer end
of the range when the held-out one lies outside it), and every
element an expression of it through **`validrange(g, lo, hi)`** — a new built-in that returns its value unchanged and,
outside the range, posts one elaboration warning naming the instance, the parameter, its value and the range
(`Evaluator.Warn`, pointed by the elaborator at the netlist's warnings). It is not a clamp: outside the range the
model still answers, and says it is extrapolating. An existing cell name is refused and the existing cell is left as
it was.

### 15.8 The document (`PassiveStep`, R-mc12-8)

`Steps.Passive` = `Settings` (`Set`, `Block`, `Topology` — a library id or a `.csch` relative to the `.cmc` —,
`Variables` in the network block's own shape, `Fit`, `Family`, `Export` with `Cell`, `Library`, `Symbol`) and `Result`
(the topology, the values or the laws, the quality and the report), stamped by `CmcStamps`. An Active document with
the block is refused. `AnalyseFile`, `ProposeFile`, `FitFile` and `ExportFile` are what the window and the `model`
verb call; every long one takes a `RunControl` (vector fitting is cancellable between orders, the fits per batch) and
`FitFile` writes only on success, so a cancelled fit leaves the previous result. `ExportFile` refuses a stale fit.

## 16. Where it lives

| Path | What |
|---|---|
| `src/Core/Devices/Composed/` | `ComposedModel` (records), `CmodelFile`, `ComposedNetworkTemplate`, `ComposedCoreProgram`, `ComposedModelCache`, `ComposedCoreModel`, `PeripheryScaling`; MC-2: `CubicBSpline`, `RuntimeEvaluators` (`TableEvaluator`, `AnnEvaluator`), `GuidedExtrapolation` (with `TrainingDomain`), `ComposedArrays` (the payload arrays, built and validated) |
| `src/Engine/Modeling/TensorSplineFit.cs` | fitting a table's coefficients, with smoothing |
| `src/Core/Elaboration/Elaborator.cs` | `ComposedFetCell`, `ResolveComposedParameters`, the core's node expansion and temperature warnings |
| `src/Design/Schematic/ComposedFetSymbolProvider.cs` | the symbol rule above |
| `src/Cli/Check.cs`, `DocumentKinds.cs` | `check` on a `.cmodel` and a `.cmc` |
| `src/Design/Modeling/` | MC-3: `CmcDocument`, `CmcFile` (with `BiasPattern`), `NormalisedData`, `DataImport`, `CmcCache`, `DataHygiene` |
| `src/RfCore/Export/MatReader.cs` | MAT v5 and v7.3 |
| `src/Design/Modeling/` | MC-4: `CmcExtrinsic`, `PinNetwork` (with `ModelingException`), `SixPinNetwork` and `PadNetwork`, `StockNetworks` and `StockNetworks/*.csch`, `PadDeembedding`, `IntrinsicExtraction`, `IntrinsicQuantities` |
| `src/Engine/Modeling/NetworkAlgebra.cs` | MC-4: `C2` and the conversions and de-embeddings |
| `src/Design/Modeling/` | MC-5: `NetworkFit` (with `NetworkFitProblem`, the residual terms, the drivers and the report), `NetworkFitSettings` and `NetworkFitRecord` |
| `src/Engine/Modeling/Ann/` | MC-6: `AnnTrainer` (with its problem, settings and result records), `AnnObjective` (the loss and its adjoint gradient), `Lbfgs`, `HeldOutSplit`, `DeterministicMath` |
| `src/Design/Modeling/` | MC-7: `CurrentFit`, `CurrentFitSettings` (with `CurrentFitRecord`), `CurrentTemplates`, `EquationFit` |
| `src/Engine/Modeling/` | MC-7: `Regrid` (grid axes, sampling, the RBF source), `LocalGradients` |
| `src/Core/Devices/Composed/` | MC-7: `ModelDispersion` and `OutputTransform` (records), `AsinhOutput` (evaluator), the state node in `ComposedCoreModel` |
| `src/Design/Modeling/` | MC-8: `ChargeFit` (with `ChargeData`), `ChargeFitSettings` (with `ChargeFitRecord`), `ChargeTemplates`, `ConservationCheck` (with `DeviceCapacitance`) |
| `src/Engine/Modeling/LineIntegral.cs` | MC-8: `LineIntegral` (paths A and B onto a grid), `CurlMetric` |
| `src/Core/Devices/Composed/` | MC-8: `DriftCharges` (with `BoxContinuation`); `ComposedCoreProgram.Channel` and `.Charges` |
| `src/Core/Devices/Composed/` | MC-9: `ModelThermal`, `ThermalPole` (records); the thermal port in `ComposedCoreModel`; the thermal network in `ComposedNetworkTemplate.Synthesize` |
| `src/Design/Modeling/` | MC-9: `ThermalFit` (with `TemperaturePlan`, `ThermalHeating`), `ThermalFitSettings` (with `ThermalFitRecord`); `T` through `CurrentFit`, `EquationFit` and `ChargeFit` |
| `src/Core/Devices/Composed/Traps/` | MC-10: `TrapModel.cs` (`ModelTraps`, `TrapParameter`, `TrapModelDefinition`, `TrapDynamics`, `RateTrapDynamics`), `TrapModels` (the registry), `CaptureEmissionTraps`, `CustomTraps`, `TrapShift` (MC-19's coupling); the trap ports in `ComposedCoreModel` and lanes in `ComposedCoreProgram` (`DcSeeds`) |
| `src/Design/Modeling/` | MC-10: `TrapFit` (with `TrapMapping`), `TrapFitSettings` (with `TrapFitRecord`); the `Setting` cube in `IntrinsicExtraction`; the trap states through `CurrentFit` and `ChargeFit` |
| `src/Design/Modeling/` | MC-11: `CmcStamps`, `CmodelExport` (with `ExportSettings`), `ModelSymbols`, `ComposedFetPlacement`, `ValidationBench`, `ModelValidation` (with `LoadSweep`), `ModelValidationReport` (with `ReportOptions`); `PinNetwork.DefineBlock`, `PadDeembedding.Embed`, `DataImport.CsvRows`, `LargeSignalConditions` in `CmcDocument` |
| `src/Design/Modeling/` | MC-12: `BoxLeastSquares` (with `FitCoordinate`, `FitVariables`), shared with `NetworkFit` |
| `src/Design/Modeling/Passive/` | MC-12: `PassiveNetwork`, `PassiveAnalysis` (with `PassiveBlock`), `PassiveTopologies` and `Topologies/*.csch`, `PassiveFit` (with `PassiveFitSettings`), `FamilyFit` (with `FamilySettings`, `ElementLaw`), `PassiveCellExport`, `PassiveStep` |
| `src/Engine/Modeling/VectorFit.cs` | MC-12: `VectorFit`, `RationalModel`, `LeastSquares` |
| `src/Core/Expressions/` | MC-12: `validrange` and `Evaluator.Warn` |
| `src/Design/Modeling/ModelCommands.cs` | MC-13: the operations no step function was whole — `New`, `Import` (with `Sources`, `FormatOf`, `DefaultName`), `Deembed`, `Fit` (with `AllSteps`, `Configure`, `FitOptions`), `Explain`, `ExportCell`; `CmcFile.ParseDataSet`, `ConservationCheck.RunDevice`/`DefaultThreshold`, `ModelValidationReport.DefaultFolder` |
| `src/Cli/Model.cs`, `src/Cli/Serve/ToolCatalog.cs` | MC-13: the `model` verb and its MCP tool (`cli.md` §31); `reference cmc`/`cmodel` in `DocumentSchema` |
| `src/Design/Modeling/VerilogA/` | MC-26: `CmodelVerilogA` (with `VerilogAOptions`, `VerilogAExportException`), `VaEquation`, `VaAnn`, `VaDomain`, `VaNetwork`, `VaText` (§25) |

## 17. Gates (MC-1 … MC-12)

`tests/Core.Tests/Composed/CmodelFormatTests`; `tests/Engine.Tests/Composed/` — `ComposedFetEquivalenceTests`
(against an SDD with the same expressions in the same network: DC to 1e-12, S at three biases to 1e-9, HB
harmonics to 1e-9), `PeripheryScalingTests`, `ComposedFetProbeTests`, `ComposedFetTemperatureTests`;
`tests/Ui.Tests/ModelComposer/` — `CheckCmodelTests`, `ComposedFetSymbolTests`. The shared device is
`tests/Core.Tests/Composed/Support/ComposedTestModels.cs`, linked into the other two projects.

MC-2: `tests/Core.Tests/Composed/AnnEvaluatorTests` (value, gradient and Hessian-along-a-direction
against finite differences, both activations); `tests/Engine.Tests/Composed/` — `TableEvaluatorTests`
(the cubic's error bound, C² across a knot, λ = 0 is interpolation), `GuidedExtrapolationTests`
(continuity across three edges, the exact Jacobian outside edges and vertices, 10⁴ finite points over ±10×
the domain, a DC solve from 2.5 extents outside landing on the in-domain answer), `DrainSourceSymmetryTests`
(eq. 6.55 at the terminals in both half-spaces, the swapped-terminal circuit), `ComposedCoreGridTests`
(zero bytes per grid; an ANN model against its equation twin in DC, S and HB to 1e-9). Their models are
`tests/Engine.Tests/Composed/Support/RuntimeTestModels.cs`: hand-set networks that also write themselves
out as the expression they compute, and a table-and-ANN model over a pentagon with an oblique edge.

MC-3: `tests/Ui.Tests/ModelComposer/` — `CmcFormatTests`, `TouchstoneSetImportTests`, `CmcCacheTests`,
`DataHygieneTests`; `tests/RfCore.Tests/Export/MatReaderTests` (a v5 file the test writes byte by byte and
a v7.3 file from `MatWriter` read the same arrays; a cell array refused by name).

MC-4: `tests/Ui.Tests/ModelComposer/` — `DeembedThroughNetworkTests` (a known linear core inside the GaN and the
classic networks, written by hand, de-embeds through the stock schematics to its own Y — measured ~1e-15 relative;
the pin refusal; the cache), `PadDeembedTests` (open-short and two-port pads around the core removed to 1e-9; a short
pad refused naming both ranges), `IntrinsicReReferencingTests` (intrinsic voltages against the SDD truth's internal
nodes to 1e-9; instrument with bias-path R equals device), `IntrinsicQuantitiesTests` (the elements to 1e-8 —
measured ~1e-14; a 1.5× `Lg` takes `Cgs` flatness from ~1e-15 to ~0.11). The truth is
`Support/SyntheticFetData.cs`.

MC-5: `tests/Ui.Tests/ModelComposer/` — `ColdFetExtractionTests` (the classic shell from a pinch-off and a forward
point with 1e-4 noise on S, both forward variants, every element from starts 25–40 % off: moderate within 0.2 %,
extreme within 0.7 % except `Cpgd` 3 %, its collapsed core's 0.02 Ω landing on the series resistances; the refusal),
`MultiBiasNetworkFitTests` (the GaN network, six series elements and a tied `Cpga`/`Cpda` within 0.4 %, the
de-embedded elements at the true network's own noise floor, bounds, the tie, the document round trip and a stale
step, the same seed bit for bit), `IdentifiabilityReportTests`, `NetworkFitCancelTests` (cancelled 20 evaluations
into the first start, the `.cmc` byte for byte unchanged). The per-bias cores are hand-written in
`Support/SyntheticFetData.cs`: a linear SDD per active point, plain R and C for the cold states.

MC-6: `tests/Engine.Tests/Modeling/Ann/` — `AnnValueTrainingTests` (1 × 10 `tanh` on `tanh(x)·y²` at 200 scattered
points: held-out RMS below 0.5 % of the range, measured 0.15 %), `AnnAdjointTrainingTests` (gradient-only training of
a non-separable `Q` within 0.5 % of its range — measured 0.02 %; a target field with curl 0.6 lands within 1 % of the
least-squares projection a grid solver computes — measured 0.05 %, where `Q` itself is 4.5 % off; the adjoint
weight-gradient against central differences, both activations, measured 1e-10; combined training of
`f(v1, v2, s)` with `s` tied to the bias at the value points recovers values and partials at fixed `s` within 1 % —
measured 0.01 % and 0.03 % — where value-only training fits the values but misses the partials by 10 %),
`AnnDeterminismTests` (same seed bit for bit, parallel within 1e-12, another seed differs; the held-out set is the
same after a shuffle and touches every octant), `AnnTrainingCancelTests` (cancelled ten iterations into the second
start: no result, the caller's network unchanged).

MC-7: `tests/Engine.Tests/Composed/DispersionNodeTests` (IdDC ≠ IdRF: DC node voltages as the `Id = IdDC` device's to
1e-12; S at 0.5–20 GHz with τ = 1 ms as the `Id = IdRF` device biased at the same intrinsic point — measured 2.9e-8;
HB's fundamental monotonic across ±2.5 decades of the corner, no step over a quarter of the swing, each end on its
reference); `tests/Ui.Tests/ModelComposer/` — `CurrentFitAnnTests` (192 DC points of the SDD truth through the GaN
network, one start, `Ig` at 2000 iterations: held-out `Id` 1.5 % of the range and 4.1 % of its own value, `Ig` 0.13
decades where it conducts above I0; the record round-trips through the document), `CurrentFitTableTests` (the table
1.5 %, as its network; the step warning at 0.2 V and not 0.1 V), `CurrentFitEquationTests` (the Angelov template
recovers all seven parameters of Angelov data to 1e-3). `CmcFormatTests` refuses a misspelt `Steps.Current` key. The
I–V grid (`SyntheticFetData.Iv`) is denser below 1 V, and the held-out biases lie between its lines.

MC-8: `tests/Engine.Tests/Modeling/ChargeLineIntegralTests` (both paths equal a non-separable charge to 1e-21 C; with a
constant curl κ they differ by `−κ·Δvgs·Δvds` at every site); `tests/Ui.Tests/ModelComposer/` — `ChargeFitAdjointTests`
(90 S points of the SDD truth through the GaN network, one start: held-out capacitances within 0.49 % of the row's
largest, charges within 0.06 % of their range up to the anchor), `ConservationCheckDataTests` (closed-form
capacitances at a jittered grid: 2.5 % and 0.17 % away from the edge; a Gaussian planted in `C12` peaks 2.93 inside
it, 0.12 at most beyond three widths, the drain row untouched), `ConservationCheckLargeSignalTests` (the truth as a charge SDD: 7e-23
of the fundamental; with its gate current as capacitances times dV/dt through two auxiliary ports, the f→2f and
2f→3f steps agree to 5e-6 and the spurious DC is 0.41 % of the fundamental), `TranscapacitanceReportTests` (the
synthetic `.cmodel` through a `ComposedFET`: curl 6e-6, `Cm` 39 fF RMS reported), `DriftChargeTests` (an [I11]
truth as an SDD at its intrinsic plane, `Id` the truth's: QGS, QGD and QDS within 0.34 %, `Qdrift` at `Id = 0`
0.27 % of its size, held-out capacitances within 0.29 %), `ChargeMethodsTests` (the Depletion template recovers a
depletion truth's capacitances to 1e-15 of their range through `EquationFit`'s derivative terms, and the composer's
whole `Run` on it reads 1.5 % in (a) on a rectilinear grid, ~1e-13 in (b) and 0 in (c); the Angelov template, by
Levenberg–Marquardt from its own start, recovers all thirteen parameters of capacitances written by hand from
Angelov's published form; a drift fit with no currents is refused). `SyntheticFetData.BiasedS` simulates multi-bias S through
ideal bias tees; `SDocument` de-embeds it through the stock network or not at all.

MC-9: `tests/Engine.Tests/Composed/ThermalPinTests` (a four-pin instance: `V(t) = Rth·P` to 1e-6 from the currents in
`rg` and `rd`; an external R and C on pin 4 give `(Rth ∥ Rext)·P`; three nets probe the same node as `X1.t`; `N = 2·N0`
halves `Rth` and doubles `Cth`); `tests/Ui.Tests/ModelComposer/` — `RthExtractionTests` (pulsed I–V at 25, 100 and
175 °C and self-heated DC at 25 and 100 °C of a 60 K/W truth: the default ANN isothermal `Id` gives 59.6 K/W, gated at
±3 %; DC only with `Rth` typed de-heats to the isothermal truth within 3e-10, against 13 % with the points left at the
ambient), `ElectrothermalModelTests` (that DC data and an equation `Id` through the thermal node: the self-heated DC
current within 0.5 % along vgs = 0.4 V, its negative DC output conductance kept; `Re Y22` at 1 GHz, far above the
~160 kHz corner, within 2 % of the truth's isothermal `Gds` at its junction temperature), `SingleTemperatureStepTests`
(one temperature: no `T` input, self-heating refused with "one temperature", the model `Mode: Single` at `T0`). The
truth is `SyntheticFetData.ThermalCell` — an SDD at its intrinsic plane whose third port is the thermal node.

MC-10: `tests/Engine.Tests/Composed/` — `CaptureEmissionNodeTests` (DC `phi2 = vds` to 1e-12 of itself at three biases;
HB at 1 MHz, ±4 V about 6 V: 0.638 V below the drain's peak and on the cycle balance within 0.02 V — measured 1e-4 V;
S at 0.5–20 GHz as the frozen-state device's within 1e-6 — measured 1.2e-7 with both DC solves at 1e-14 A, where the
DC engine's default 1e-6 A left the two biases 1e-6 V apart), `ThermallyActivatedEmissionTests` (0.5 eV: the emission
rate ×22.3 at 75 °C and the node's recovery time ×235.6 between 25 and 125 °C, each as predicted to 1e-9),
`CustomTrapModelTests` (a `Custom` equal to `CaptureEmission`: DC and S identical, HB within 1e-12; an unknown name and
a non-fixed-point `Dc` refused by field), `TrapInputsTests` (`Ig` and `Qg` refuse a trap input, `Qd` takes one; with
the drift charge `∂Qg/∂phi2 = ∂Qdrift/∂Id · ∂Id/∂phi2` to 1e-12, in the program and in the core's charge Jacobian);
`tests/Ui.Tests/ModelComposer/` — `TrapTrainingDataTests` (toward points at the setting under every `TrapState`, the
pre-pulse level over the quiescent one; `auto` leaves 21 beyond points out and counts them, `setting` and `extreme` map
them; one level warns once, a cold pulse and no pulsed data refuse naming the remedy; a conducting pre-pulse with no
current refused, a 0.5 ms pulse warns), `TrappedModelFitTests` (the closed-form drain-lag truth: from quiescent 5, 10
and 20 V with the DC held out, each level's pulsed I–V within 1 % — measured 0.17–0.63 % — the DC I–V 0.10 %, the RF
`Gds` 1.7 % of its RMS where the DC slope is 77 % away; one level warns once and reproduces it at 0.10 %; no pulsed
data: unavailable, no `Traps`; dispersion refused), `TrapRegistryTests` (an unknown model refused by name; a `None`
block byte-identical to none; the current step under `None` records exactly what it did without the step). The truth is
`Support/SyntheticTrapData.cs`; the engine gates' device is `tests/Engine.Tests/Composed/Support/TrapTestModels.cs`.

MC-11: `tests/Ui.Tests/ModelComposer/` — `CmodelExportTests` (a `.cmc` whose steps record the SDD truth's equations, its
data that truth's multi-bias S through the GaN network: the export as a `ComposedFET` against the truth's SDD, DC node
voltages, S at three biases and HB to 1e-9; no N0, an unfitted charge step and data changed under the steps each refused
by name, no file left), `SymbolChoiceTests` (a 2-pin and an unheated 4-pin `.csym` refused and absent from the
candidates, a JFET kind absent; the stored `.csym` relative to the model, in the `.cmc`, and in the pasted instance),
`ValidationServiceTests` (S |ΔS| median 2.5e-10; Lg × 3 fails at 95 % 0.098; a power sweep against the truth's own HB
passes and its testbench runs through the `hb` verb with exit 0), `ModelValidationReportTests` (each class's extrinsic
loads present harmonicaRF's intrinsic targets through the network and C22 to 1e-9 where reachable; a report on a 7-point
disc runs S, charge, C–V and two classes with MXP and MXE, and writes its folder). The fixture is
`Support/ExportFixture.cs`.

MC-12: `tests/Engine.Tests/Modeling/VectorFitTests` (two lightly damped pairs and a real pole shared by three responses
with constant and proportional terms: the poles to 1e-8, error by order stops at 5); `tests/Ui.Tests/ModelComposer/Passive/`
— `PassiveAnalysisTests` (a series RLC between two ports: reciprocal to 1e-12, largest eigenvalue ≤ 1, the reactance
crossing within 1e-4 of 1/(2π√LC) and the vector fit's pole within 1e-6; S raised 10 % over 4–5 GHz flagged at exactly
those frequencies, eigenvalue 1.21), `TopologyProposalTests` (S of `parallel-rlc`, `pi` and `mim` at known values ranks
that topology first among the five or seven candidates and recovers every value within 1e-3), `FamilyFitTests` (six
spirals over turns 1.5 … 6.5 with L ∝ t^1.8 and R_sub ∝ 1/t, side 2 tied to side 1: the laws come out linear,
quadratic, rational and cubic as the truth is, the held-out 3.5 within max |ΔS| 0.01 — measured 3.5e-4),
`PassiveCellExportTests` (a fitted π exported, placed and simulated through extraction, the `.cnl` round trip and the
engine equals the fitted network to 1e-12; an existing name refused and the cell unchanged; a family cell at
turns = 2.5 equals its laws to 1e-12 and warns once at 7), `PassiveStepFileTests` (Touchstone files named
`via_{geometry}t` fitted as a family into the `.cmc`, read back, exported from the record). The data is
`Passive/SyntheticPassiveData.cs`.

## 18. The window (MC-14)

`src/Ui/ModelComposer/` (view models) and `src/Ui/Views/ModelComposer/` (views): a toolbar in the schematic and layout
editors' style, and railRF's panels — Steps (rail and the step's settings), Plots, Network and Values, each its own view
with a lamp kept in `Panels`, the last lit lamp disabled, splitters both ways.

**One document in memory, edited; the file is where runs happen.** Every edit is a `CmcEdit` — a label and a
redo/undo pair over whatever document is current — so an undo after a run reverts the edit and keeps the run's result.
A run that changes results (import, fit) writes the document, calls the file-based `ModelCommands` function the
`model` verb calls, reads the result back and replays the edits made while it ran. The window's other needs are the
shared functions in `ModelCommands.Window.cs` — `Save`, `Load` (the cache, the intrinsic data, the pad-removed data,
the hygiene flags and the stale steps in one `ModelData`), `ChooseNetwork`, `UseNetworkFile`, `InstallNetwork`,
`InstallTopology`, `Preview`, `Propose`, `Analyse` — so the window's folders hold no flow (`ModelComposerNoLogicTests`).

**One task at a time.** A refresh (opening data, re-de-embedding after an edit to the data, the pads or the network)
reads the document in memory, writes nothing, and gives way to a run the user asks for; a second run is refused in the
status line. Progress is coalesced: a report stores the latest and posts a UI work item only when none is queued, so
`UiWorkItems ≤ ProgressReports` however fast a fit reports. `CurrentTask` is the whole task, result handover and clean-up
included — awaiting the worker alone let the next run start while the previous one was still clearing up.

**The network panel** is `SchematicCanvas` drawing the network's `.csch`; copy and paste are a schematic selection on
the clipboard, net labels included — the stock networks connect through free-standing labels at their pins, which the
selection format now carries when its copier passes them.

Gates: `ModelComposerLifecycleTests`, `ModelComposerPanelsTests`, `ModelComposerBackgroundTaskTests`,
`ModelComposerNetworkPanelTests`, `ModelComposerNoLogicTests` — view-model driven with no application host, the
lamp-to-panel wiring read from the AXAML, as railRF's window gate does.

## 19. The model steps and every comparison plot (MC-15)

**Each step is a thin view over its phase.** Settings are edits through the step's own `WithSettings` (one undo step);
runs are the functions the `model` verb calls — `ModelCommands.Fit` (Current, Charge, Thermal, Traps, Run stale),
`ConservationCheck.RunFile` (Check), `ModelValidation.RunFile` (Validate), `CmodelExport.Export` /
`ModelCommands.ExportCell` (Export), `ModelValidationReport.Generate` + `Write` (the report).

**The plots' numbers are computed in `src/Design`, never in the window.** `ModelStepPlots.Build(cmcPath, doc, loaded,
step)` returns a `StepPlotData` — a `DataSet` of `<set>.<quantity>` cubes: the fit's own training points (gathered by
`CurrentFit.Gather`, which now records each point's `Origin` so the window can group the measured families), the model
evaluated at each point's own inputs (DC sets through `DcSeeds`, pulsed sets at their own trap states), the data's own
gm/gds along each measured family, the error as a map, the domain outline; the charge surfaces and ΔQ maps through the
report's own `ChargeSurface`; the report's own `Thermal` section; and per pulsed set its points beyond the trap-setting
bias. The window computes it off the UI thread when the step's results or the data change, keyed on every step's
`Result`. The 1-D C–V slice is `StepPlotData.Slice` on the compiled model — a hundred evaluations, so it follows the
cursor synchronously. `ModelStepPlots.Check` turns a conservation report into the curl and two-path maps and the HB
spectrum (`DriveCurrents.GateSpectrum`, new: the gate current at DC and each harmonic).

**A map is a square grid.** The contour renderer and `ContourExtractor` index a `SurfaceGrid` as `res × res` and draw
nothing when `XSpace` and `YSpace` differ in length, so every map here is `MapPoints` (41) square over the data's hull,
NaN outside it. The surface renderer draws angle-axis patterns only, so the charge surface is drawn through the contour
renderer as well.

**The report's display is a preset** (`ModelReportDisplayPreset`, in `src/Render/DataDisplay`, the `DoeDisplayPreset`
pattern): one tab per section that holds data, over the report's own `report.npy`. A `.cdd` contour trace is
loadpull-only — it re-derives its curves from a loadpull source — so the ΔQ maps there are a family over the shorter
axis; the PA classes use real loadpull contours and the performance summary table, and the region of convergence is
two cubes the report now writes (`GammaLoad.converged`, `GammaLoad.failed`). `WritePdf` draws every tab through
`PlotComposer` as a multi-page PDF with no title in its metadata, so the window's PDF and `model report --pdf`'s are the
same bytes whatever they are called; the placement `render` used was moved below the firewall (`PlotPlacement`) so both
compose through one function.

Gates: `ModelComposerCurrentStepTests`, `ModelComposerChargeStepTests`, `ModelComposerCheckStepTests`,
`ModelComposerExportStepTests` (byte identity against the CLI process), `ModelComposerSingleTemperatureTests`,
`ModelReportDialogTests` (byte identity of the PDF against the CLI process).

## 20. Deviations from the series, as built

Each departure from the briefs, with its reason. The detail of each is in the `RESOLVED.md` named.

| Phase | The brief said | As built, and why |
|---|---|---|
| MC-4 | — | A network override keeps its variable's unit (as its base unit): stripping it let an editor field's own `pF` scale a fitted value again (`src/Design/RESOLVED.md`). |
| MC-5 | Cold-FET by alternating fits | Alternation, then a joint polish of both cold terms: alternation alone stalls on the moderate core's `R1`/`Rg` valley. Mutual couplings are solved at `|k| ≤ 0.999` with the excess as a penalty, so the engine never prints a non-physical coupling per trial. |
| MC-6 | — | The trainer uses its own `exp` and `tanh`: the platform's are not bit-identical across operating systems, and "same seed, same weights" is a promise across them. |
| MC-7 | `Ig` weighted by `1/max(|Ig|, I0)²` | A table or ANN `Ig` is fitted in `asinh(Ig/I0)`, unweighted, and the `.cmodel` carries the transform: weighting cannot fit ten decades with a linear output. `IdFloor` 0.1 and 6000 iterations by measurement. Eq. 6.49's residual is the share of the RF conductances `IdRF` does not reproduce; differencing conductances called conservative data 86 % non-conservative. |
| MC-8 | Check (c) with the drain held at DC | The drain is driven in quadrature: with `vds` constant every loop integral is zero and the check could never fail. The interpolant for tables and check (a) is a network, not an RBF (33 % curl on conservative data). [I11] eq. 4's `CGD,drift` is used in its exact form (the printed one drops `τ(gm − gds)/2`). Check (a) reads no curl within 2 % of the hull's edge. |
| MC-9 | Pin 4 "exposes ΔT" with an external network in series | Pin 4 is the junction node and what attaches there is in parallel with the model's Rth (owner, following the references and compact-model practice). |
| MC-10 | — | The trap node's normalising capacitance is 1 mF, where both the DC `gmin` floor and HB's charge round-off clear. Drain–source exchange with traps is refused; `Custom` odd states are gate-like and even drain-like; beyond points still train `Ig`. |
| MC-11 | Loadpull of a composed FET by per-load HB sweeps | `LoadpullEngine` accepts a tuner on a net no nonlinear device touches (the extrinsic terminal), so `lp` and `lpp` run a `ComposedFET` (owner); the report's PA classes follow harmonicaRF's intrinsic-load method (owner). |
| MC-12 | — | A new expression built-in, `validrange(x, lo, hi)`, carries a family cell's out-of-range warning; library bounds run 0 … 1000× the seed; a family needs three training geometries and one held out. |
| MC-14 | — | Free-standing net labels now travel on the schematic clipboard: the stock networks connect through them. |
| MC-15 | A charge surface; ΔQ maps in the report's display | The charge surface is drawn as a contour (the surface renderer draws angle axes only); a `.cdd` contour is loadpull-only, so the ΔQ maps there are families. |
| MC-16 | — | Three defects found by the acceptance run and fixed (below). Rth is typed in the example rather than extracted (below). |
| MC-19 | No pulsed data offers `Shift` only; the charges take no shift | `Coupling` stays `Trained` unless written, since MC-10's gate holds that an unset coupling without pulsed I–V is unavailable; the window and `model fit` write `Shift` when there is no pulsed I–V. A drift charge reads the unshifted `Id₀`, so the charges stay trap-independent through it too. |

**MC-16's fixes.**

- **A Tuner shorted both carriers of a two-tone run.** `TunerModel.GetZ` took any frequency off the exact harmonic grid
  as its catch-all `Zdefault`, and under two tones the carriers sit 0.25 % either side of `f_c`. A Tuner load declaring
  `Z[1] = 50 Ω` presented 1 µΩ to both, so the Validate step's two-tone comparison (whose bench loads the device with a
  Tuner) measured a shorted output. The Tuner now takes the sources' band rule, `n = roundHalfUp(|f|/f_c)`, which is
  the same answer on every single-tone grid (`src/Core/RESOLVED.md`).
- **A cache rewrite deleted every fit.** A source whose bytes are unchanged but whose timestamp moved (a copy, a
  checkout) rewrites the cache index, and the rewrite carried only `intrinsic/` across. `current/` and `charge/` hold the
  fitted arrays, which only a fit can rebuild. Every subfolder is carried now (`src/Design/RESOLVED.md`).
- **Check Charge Conservation could not run on a model with traps.** Check (c) asks HB for 1e-12 A, and a trap or
  dispersion node's residual floor is ~1e-8 A by design (MC-10's 1 mF). Such a model is driven at 1e-7 A, a resolution of
  2e-4 of the default drive's fundamental gate current. Check (a) also crashed on data at two or more temperatures: its
  2-D interpolant was handed the domain's `T` box. And check (b) read its curl across the hull's edge, where the charges
  are their linear continuation: 0.6 % on a model that conserves charge by construction, 7e-6 with (a)'s edge margin.

## 21. Open items

- **Power-law and unit-width scaling.** Periphery scaling is by finger count only. The power law of [P1] eq. 5.9,
  unit-width scaling ([T20]), and the affine rules of [L26] eqs. 21–30 (fixed parts plus parts proportional to `N`,
  `1/N`, `W·N`, `W/N`, `1/(W·N)` and a source-via term) need data from two or three device sizes, which no step reads.
  Rule names are a refusal-checked list, so they can be added without a format change.
- **Multi-time-constant traps.** `CaptureEmission` has one capture and one emission time per state; a measured
  recovery spanning decades needs a ladder of states per mechanism.
- **Trap estimation from transient data.** The time constants are typed. A step that fits them to a drain-current
  transient (or to the low-frequency `Y22` dispersion, which today only τth reads) does not exist.
- **Large-signal waveforms as a training source** ([P1] §6.7). Large-signal sets validate; nothing trains on them.
- **Noise.** No constituent models noise sources.
- **Rth extraction with drain lag.** Extraction compares pulsed and DC currents at the same bias as if both sat at one
  trap state. A pulse from a 0 V drain leaves the CaptureEmission state ~0.5 V short of `vds` (capture slows as the gap
  closes), and the shortfall reads as heating: 62 K/W against a 40 K/W truth. Made to capture fully within the
  pulse (a 0.1 V reference gap), the same truth read 15.5 K/W against 25 — low instead of high, with every DC junction
  temperature inside the chuck range, so a second effect is in play that has not been isolated. With drain lag modelled,
  Rth is typed. An extraction through the trapped current, each pulsed point at its own trap state, would remove the
  first limit; the second needs a gate of its own.
- **The extrapolation conductance near a data edge** — closed by MC-29's local law (§4.9). Under the global law `G_x`
  was ten times the forward diode's conductance at every edge, and the Pulsed Radar device with grids stopping at −6 V
  drew ~1 A of reverse gate current at −7 V and saturated early. The composer's Current step names the conductance at
  each current's lowest and highest `vgs` edge, so an edge the data stops short at is visible.
- **`ValidationBench` on a self-heating model at the intrinsic plane** writes a `ComposedCore` with three nets, which a
  thermal core refuses (it takes four). Only intrinsic-plane data sets of self-heating models reach it.

- **`netlist --to-schematic` on a composed-FET bench** keeps a quoted `File="…"` as quoted text, and a loadpull
  analysis's grid as the absolute path the netlist reader resolved; a schematic stores both unquoted and relative to its
  workspace root. The example's authoring rewrites the two; the drawer does not yet.

## 22. The example and the series acceptance (MC-16)

**`examples/Model Composer`** holds three documents built by `ModelComposerExampleAuthoring` through `ModelCommands`, and
the benches that use them. `synthetic-gan` is `SyntheticGan`'s truth (an SDD: `ComposedTestModels`' currents and charges,
`Id` falling 0.4 %/K with the junction temperature through a 25 K/W, 10 µs thermal node, and a drain-lag node in the
CaptureEmission form at its defaults) inside the GaN network with `Lg`, `Ld`, `Ls` away from stock, behind open/short
pads: DC at 25 °C, pulsed from 0 V at 25 and 125 °C (`extreme`), from 10 and 20 V, and a double pulse to 20 V, and S at
25 and 85 °C. The document fits the three inductances, types Rth and τth, fits a 2 × 16 ANN `Id` and an ANN `Ig`
(`IgFloor` 1e-7) with `T` and `phi2` as inputs, CaptureEmission drain lag from levels 0, 10 and 20 V, and adjoint ANN
charges; the `.cmodel` is placed at `N = 8` in a class-AB power sweep and a loadpull. `passive-spiral` is a six-spiral
family fitted as one topology with its substrate sides tied, exported as a cell and placed at 3 turns; `simulated-tables`
imports a simulator's `.npy` tables and shows the intrinsic-voltage cross-check. Each document ships its cache, which
holds its fitted arrays. The example's `.gitignore` lets the cache and the tables through.

**`ModelComposerAcceptanceTests`** runs that flow in a temporary folder from the raw files and holds the exported model
to the truth: the network's inductances within 2 %, DC RMS ≤ 1 % of range, S |ΔS| 95th percentile ≤ 0.02 per set, check
(b) < 0.1 % and (c) below its threshold, the power sweep up to 3 dB compression at `N0` and `2·N0` (Pout 0.5 dB, PAE
3 pp; measured 0.39 dB and 2.6 pp) and IM3 at three drives (1.5 dB; measured 0.2–1.0) — thresholds changed once from
the brief's, with the measurements, in
`src/Design/RESOLVED.md`. It is `Category=Benchmark` (12½ minutes: the fit and the truth's two-tone);
`TheSyntheticData_IsSmallLabelled_AndAsksForEveryStep` is its routine wiring. `ModelComposerRealDataTests` runs the same
flow on a local device folder when present and reports, asserting only that it completes. `ExampleWorkspaceLoadsTests`
copies the example (new timestamps, as File ▸ New ▸ Examples gives) and checks every document, every step fresh, and
the benches.

## 23. Instance switches and the bias-point freeze (MC-21)

**`ComposedSwitches`** (`src/Core/Devices/Composed/`) is the one reader and writer of the six switches — `Charge`,
`LinearC`, `GateLag`, `DrainLag`, `SelfHeating`, `Dispersion`, read by `BooleanParameter`, a bare spelling taken as written
before anything is evaluated. The elaborator reads them off the `ComposedFET` line, keys `_composedCells` on them, and
writes the non-default ones onto the synthesised `ComposedCore`, so each core is built with them; `NetExtractor` writes a
switch only when it is not the default, the schematic's annotation and `explain` go through `Annotate`, and the parameter
editor's picker is the registry's. A non-default instance adds one note per run (`X1: linear C at the bias point, no gate
lag.`), and a switch naming a part the model lacks adds one saying it changes nothing.

**What each does to the core.**
- `Charge=false`: rows 0 and 1 of `q` and `dc` are zero. The dispersion and trap nodes' own charges are not intrinsic
  charge and stay.
- `SelfHeating=false`: `T` is the clamped ambient, the T lane maps to no column, and the thermal port carries no power. The
  port stays, so the cell's `Rth ∥ Cth` and a bound pin 4 are unchanged and sit at 0 K.
- `Dispersion=false`: `_dispersive` is false, so no `x` node and the drain conducts `Id` (the DC current). A drift charge
  still reads `IdRF`, as fitted: the switch changes the drain, not the charge.
- **No lag** (`GateLag`/`DrainLag=false`) removes the states of that `CaptureSign` (−1 gate, +1 drain). A removed state's
  seed is `DcValue(k, vgs, vds, T)` at the instantaneous intrinsic voltages; after the constituents are evaluated, its lane's
  gradient folds into `vgs`, `vds` and `T` through `DcValue`'s gradient overload and is cleared, and its node is not minted
  (`InternalStateNodeCount` counts kept states). Under `Shift` the shift term is then `c·(φ − φ_DC) = 0` and its
  derivatives cancel exactly through the fold. `CaptureEmission` and `Custom` (whose DC expression's dual gives it) both
  supply the overload; for a model that does not, the core uses a central difference of `DcValue` (relative steps 1e-6 in
  the voltages, 1e-4 in `T`), detected once at construction.

**The freeze.** `LinearC=true` makes the core an `IBiasFrozen` that needs a bias point. `BiasFreeze.Ensure`
(`src/Engine`) solves `NonlinearDcEngine` and hands each such model its port voltages at the solution; the core evaluates
its charge there as fitted and stores `q(v_Q)` and the 2×2 `C(v_Q)` (scaled, transcapacitance kept) in one immutable
record. After that, `q = q(v_Q) + C(v_Q)·(v − v_Q)` and `dc` is that constant matrix, with no temperature or trap column.
A repeat freeze at the same port voltages changes nothing; a moved DC solution re-freezes (`FreezeCount`). A design with
no linear-charge instance returns at once with no DC solve.

Callers: `SParameterEngine`'s preparation (`Apply`, at the operating point it already solved, or 0 V when that did not
converge), `HbEngine.Run` and `RunSinglePoint` — warm-started or not, so loadpull, pursuit, sweeps, the optimizer and
the validation bench all go through them — and harmonicaRF's own HB solve. `BiasFreezeTests` scans `src/Engine` and
`src/Harmonica`: every file calling an HB Newton solver or `StampLinearized` must freeze, and so must each public `Run*`
method of `HbEngine`.

**The unfrozen refusal.** A linear-charge core asked for its charge before any freeze throws
`ComposedEvaluationException` naming the instance. The DC engine reads no charge and says so explicitly:
`NonlinearDcEngine.Run` opens a `ChargeReading.NotRead()` scope, inside which the unfrozen core returns its currents
without refusing.

## 24. One capacitance at a time (MC-22)

Four more switches, `LinearCgs`, `LinearCdg`, `LinearCds` and `LinearCm`, hold one circuit-set capacitance at its
bias-point value and leave the others nonlinear. `ComposedSwitches.FrozenBranches` (a `ChargeBranches` set) is what the
core reads. `LinearC` stands for all four but keeps its own exact tangent path, and `Charge=false` empties the set. Any
branch switch makes the core need a bias point, so `NeedsBiasPoint`, `BiasFreeze` and the unfrozen refusal behave as
they do for `LinearC`.

**The convention** is `IntrinsicQuantities`' and MC-17's: from `C = ∂(Qg, Qd)/∂(vgs, vds)`,
`Cgs = C11 + C12`, `Cdg = −C12`, `Cds = C22 + C12`, `Cm = C21 − C12`. Branch `b` has a controlling voltage `u_b`
(`vgs`, `vgs − vds`, `vds`, `vgs`) and an incidence `e_b` over (g, d) (`(1,0)`, `(1,−1)`, `(0,1)`, `(0,1)`), and
`C = Σ_b e_b C_b ∇u_bᵀ`.

**Why the Jacobian alone cannot be edited.** Holding `C_b` at `C_b(v_Q)` in `dc` gives the matrix
`C̃ = C − e_b (C_b − C_b(v_Q)) ∇u_bᵀ`. Its rows are gradients only if each row's curl vanishes; `C`'s does, and the
correction's is `e_b,i (∇C_b × ∇u_b)`, which is zero only when `C_b` depends on `u_b` alone, that is, for a separable
charge. So in general `C̃` is the Jacobian of no charge: the Newton matrix would disagree with the `q` it differentiates,
and a capacitance with curl makes DC under drive. A constant re-weighting `q′ = A·Q̃` does not help either: three free
branches equal to the model's at every `v` force `A = I`, which leaves the fourth following the model too.

**The construction** (`BranchLinearisation`, `src/Core/Devices/Composed/`). `Q̃(v)` is the charge with its temperature and
trap inputs held at the frozen point's values (`Frozen.Held`, the seed vector there), in the terminal frame. With
`Δv = v − v_Q`:

```
r_b(v) = [ ∫₀¹ C_b(v_Q + s·Δv) ds − C_b(v_Q) ] · Δu_b,     q(v) = Q̃(v) − Σ_{b∈F} e_b · r_b(v)
```

`q` is an explicit function of `v`, so it conserves charge. `r_b` and its gradient vanish at `v_Q`, so every
combination has `Q̃`'s C matrix there (the core skips the quadrature at `Δv = 0` exactly). Freezing all four gives
`Q̃(v_Q) + C(v_Q)·Δv` by the fundamental theorem along the line. The straight line from the bias point is a definition,
chosen because it makes those three hold. A separable branch frozen alone is that branch replaced by its tangent.

**Evaluation.** 8-point Gauss–Legendre (`Nodes`), exact for a cubic charge. `dc` is the exact derivative of that sum,
`∇r_b = (I_b − C_b(v_Q))·∇u_b + Δu_b·Σ w_k s_k ∇C_b(v_k)`. The last term needs second derivatives, which come from:
- an equation: `SecondOrderDual` (`src/Core/Expressions/`), a value, gradient and 2×2 Hessian over `(vgs, vds)`, run
  through `SddCompiler`'s node tree for the same AST. `Dual` stops at the gradient, and the register program is
  `Dual`-only;
- a table or ANN inside its training domain: the evaluator's own Hessian along `vgs` and along `vds`
  (`GuidedExtrapolation.HessianAlong`);
- a drift charge, or a table or ANN outside its domain (the linear continuation): a central difference of the analytic
  `C` with a 1e-6 V step (`DifferenceStep`). `BranchLinearChargesGanTests` holds this path's `dc` against a central
  difference of `q`.

Under `Exchange` the surface applies the image itself, so gradients map as in `Core` and a Hessian maps as `JᵀHJ`. A line
crossing `vds = 0` needs nothing special.

**Measured on the shipped SyntheticGan model** (ANN charges over `vgs, vds, T`):
- With self-heating, every one of the 16 combinations gives exactly `LinearC`'s S-parameters at the bias point. Those
  differ from the model's by |ΔS| 6.8e-10 at 0.5 GHz, falling with frequency. The held surface has no temperature
  column, and in the model as fitted the thermal node still moves the charge slightly. Isothermal, all 16 match the
  model to 1e-12.
- All four against `LinearC`: below 1e-6 relative inside the training hull, but 1.8e-5 at vgs ≈ −2.9 V. A line that
  leaves the hull crosses its boundary, where the continued capacitance is continuous but its slope is not, and
  Gauss–Legendre is not exact across that kink.
- Check (c) reads exactly 0 A for `LinearCgs` and `LinearCdg`, and for the model as fitted too. Harmonic balance writes a
  charge's current as `jkω·Q_k`, so a charge-based device has no displacement current at DC, whatever its charge. Here
  the check confirms that the charge is single-valued but does not separate the modes.

- The example's GanPowerSweep bench does not converge at every rung even for the model as fitted: Pin 14 and 16 to
  21 dBm stop at 100 iterations. `TheGanBenches_Run` never saw it, because it checks the run's `Converged`, which a
  parametric sweep never sets to false. The per-switch sweep gate therefore stops at 13 dBm, where every rung of the
  fitted model converges. The stall needs the drain-lag trap node and the thermal node together; either one switched
  off converges in 14 to 23 iterations (`src/Core/RESOLVED.md`). On the full sweep, `LinearCgs` converged at 18 dBm, where the fitted model does not, and missed
  22 dBm, the rung after the run of misses whose results it starts from.

**Cost.** A frozen instance evaluates the surface at 8 nodes per sample, each with its Hessian (for network charges, two
extra passes per charge). Only instances with a branch switch pay it, and an instance with none runs the code it ran before.

## 25. Verilog-A export (MC-26)

`CmodelVerilogA.Write(ComposedModel, VerilogAOptions)` writes a model as one self-contained Verilog-A module, and
`Export(cmodelPath, vaPath)` writes that to a file by a temporary file and a rename. It reads the `ComposedModel` that
`CmodelFile.Read` returns, so a `.cmodel` alone exports; given a `.cmc`, `Export` assembles it with
`CmodelExport.Assemble` and passes the in-memory model through the same `Write` (MC-28). The code is in
`src/Design/Modeling/VerilogA/`. MC-26 wrote everything that has no state of its own; MC-27 adds the dispersion node, the
trap states, self-heating with its thermal pin, and every instance switch with the bias point the linear-charge switches
freeze at (§25.11 – §25.16); MC-28 puts it in front of the user (§25.17 – §25.20).

### 25.1 The module

```
// header: provenance, model.json SHA-256, every parameter with unit, default and meaning
`include "disciplines.vams"
`include "constants.vams"
module <name>(g, d, s);
  electrical gi, di, si;                 // network pins 4-6
  electrical <network nets>;             // and gnd, ground, when an element reaches 0
  branch (a, b) br_<L>;                  // one per inductor
  parameter integer N = <N0> from [1:inf);
  parameter real Temp = -273.15 from [-273.15:inf);
  parameter real <network variable> = <base SI value> [from (0:inf)];
  parameter real <constituent>_<parameter> = <fitted value>;
  parameter real Gx_<payload> = <G_x> from (0:inf);
  analog function real f_<payload>;  ...  // value, gradient into the d_ outputs
  analog function real x_<payload>;  ...  // a table or ANN continued outside its domain
  analog begin
    kN = N/N0;  <var>_N = <var>*(N/N0 or N0/N);
    Tdev = Temp set ? Temp : $temperature - `P_CELSIUS0;  T = min(max(Tdev, Tmin), Tmax);
    @(initial_step) $warning(...) when the model is used outside its temperature;
    <network contributions>
    vgs = V(gi, si); vds = V(di, si); image under Exchange;
    id = x_Id(ugs, uds, T); igs, igd, qg, qd likewise;   $fatal on a value that is not finite
    I(gi, si) <+ kN*(igs + igd);  I(di, si) <+ kN*(id - igd);
    I(gi, si) <+ ddt(kN*qg);      I(di, si) <+ ddt(kN*qd);
  end
endmodule
```

- **The name** is the `.cmodel`'s file name made into an identifier (`gan 4x125` → `gan_4x125`, `4x125` → `m_4x125`).
  `ModuleName` overrides it, and an illegal override is refused.
- **The bytes** are deterministic. Doubles are written in shortest round-trip form with a lower-case exponent, and a
  whole number is written `5.0`, because `1/2` between two integer literals is integer division in Verilog-A. Lines end
  in `\n` and the file is printable ASCII (`°C` is written `degC`). The header carries the model's `Provenance.Created`,
  its circuitRF version and the SHA-256 of `model.json`. `Export` hashes the entry as the file holds it, and `Write`
  hashes `CmodelFile.ModelJson`; the two agree because the writer is deterministic.
- **The parameters** are those of D18; MC-27's (`Rth`, `Cth`, the ten switches, `VgsQ`, `VdsQ`, `TjQ`, the trap
  parameters) are in §25.11 – §25.15. `N` is an integer from 1, as circuitRF requires.
  `Temp`'s unset value is −273.15 °C, which circuitRF refuses as a device temperature, so it cannot mean anything else.
  Network variables are written at the base periphery, and the scaling is code. A variable that is an R's value has
  `from (0:inf)`. Equation parameters are prefixed by their constituent (`Id_beta`, `Ig_Isg`), so two constituents'
  parameters cannot collide. ANN weights and normalisations are literals: a 4 × 64 network holds about 12,700 numbers,
  nobody edits a list that long, and some hosts cap the count. There is no `m` parameter, because the host's own
  multiplier scales the whole device. In circuitRF the multiplier scales the whole residual, so the branch rows of the
  internal inductors are scaled too, and the result is the same as circuitRF's own `L/m`.

### 25.2 The network

`VaNetwork` mirrors `ComposedNetworkTemplate.Synthesize`. Pins 1–3 are `g d s`, pins 4–6 are `gi di si`, and every
other net keeps its name. `R` is written `I <+ V/R`, `C` is `I <+ ddt(C·V)`, `L` is `V(br) <+ L·ddt(I(br))`, and each
`Mutual` adds `M·ddt(I(br_other))` to both inductors' branches. `Short` is written `V <+ 0`. Anything else is refused by
element name and type, and so is a Mutual whose ends are not two `L` elements. An element's value is translated from its
AST over the scaled variables. It is then checked against the number circuitRF folds it to, with and without the site
unit, and a value whose units cannot be followed is refused rather than written wrong.

### 25.3 Value and gradient, emitted

Each payload is an `analog function` that returns its value and writes its gradient into one `output` argument per
input. The installed compiler accepts `output` arguments, accepts calls from one function into another, lets a function
read module parameters, and **differentiates through output arguments**. That was checked first, with
`f(p_b) + ∇f(p_b)·(p − p_b)` and `p_b` clamped on one axis. A probe through the worker read `∂F/∂x` with the
`∂(∂f/∂y)/∂x` term included. So each payload is one function. Copying the code once per evaluation point with suffixed
names was not needed.

Verilog-A's automatic derivatives cannot replace the emitted gradient. They differentiate at the circuit's operating
point only. Guided extrapolation needs `∇f` at the boundary point, and MC-27's linear-charge switches need `C` at a bias
point, and the circuit is at neither. The compiler then differentiates the emitted code again where the circuit needs
it, and because `p_b` moves with `p`, that derivative is the continuation's exact gradient.

- **An equation** (`VaEquation`) is differentiated source to source in forward mode from the expression engine's AST.
  Every non-leaf node writes its value into a temporary and its derivative by each input into another, with the
  arithmetic of the engine's dual numbers in the same order. The engine's domain guards are written too: `exp` is capped
  at 700, `ln` of a non-positive number is taken at 1e-300 and `sqrt` of a negative number at 0. The emitted gradient
  equals `EvalDualInto`'s to 1e-14 at 50 seeded points for every function (`EquationTranslationTests`).
- **An ANN** (`VaAnn`) is written neuron by neuron as `AnnEvaluator` carries it: the bias plus the weighted inputs in
  column order, `h′ = (1 − h²)·z′` for `Tanh` and `h(1 − h)·z′` for the logistic `Sigmoid`, then the de-normalisation.
  The first layer's gradient is the literal `W·scale`, formed the way the evaluator forms it.
- **Asinh** gives `f = I0·sinh(u)` and `∇f = ∇u·I0·cosh(u)`. **A drift charge's pieces** are 1- and 2-input ANNs
  continued to first order beyond their box (`BoxContinuation`), and the analog block sums them as `DriftCharges` does
  ([I11] eq. 3), reading the channel current at the same point.

**The function map.** The set is the SDD compiler's 21 functions. A test reads them from `SddCompiled.cs` and
`SddRegisterProgram.cs` and fails when one has no row. A function outside the set is refused by name. There are three
traps. circuitRF's `log` and `ln` are both natural, but Verilog-A's `log` is base 10, so both are written `ln` and
`log10` is written `log`. `if(c, a, b)` becomes `c ? a : b`. `^` becomes `pow`, and `sign` is spelled out as a ternary.
A literal carrying a unit is base SI in the AST, so no unit reaches the file.

### 25.4 Guided extrapolation

`VaDomain` ports `DomainGeometry.Project` and `GuidedExtrapolation.Evaluate` line for line. The centre, extent and
counter-clockwise vertices are literals computed exactly as the constructor computes them, and the edge vectors and
squared lengths are formed the same way. The inside test, the edge-by-edge nearest-point search with its strict `<`, and
the box clamp all read the same numbers in the same order. Inside the domain `p_b` is `p` itself, because a
round trip through normalised coordinates would move it by a rounding. Outside, every axis comes back through
`centre + extent·u_b`, as in circuitRF. The value is `f(p_b) + ∇f(p_b)·(p − p_b)`, and for a current it adds
`(G_x/0.05)·s·√d²` when `d² > 0`. The guard keeps the compiler from differentiating `√0`. Under the local law `G_x` there
is `gl = min(sqrt(ga*ga + (1e-7*Gx)^2), Gx)` with `ga = 10·g_own` from the payload's gradient at `p_b`, and `Gx_<payload>`
is its cap; the gradient function adds `gl`'s own gradient through one more call of the Hessian function along `own`.

### 25.5 The core

`vgs`, `vds`, the `Exchange` image and its map back (`Id` negated, `IgGs`/`IgGd` exchanged, `Qd = −Qg′ − Qd′`) follow
§4.10. The compiler then derives the image's Jacobian itself. The currents and charges times `N/N0` are as in §4.2. The
temperature follows §4.4. `Tdev` is the instance's `Temp`, or `$temperature` in °C. A `Range` model reads it clamped,
and a `Single` model reads no `T`. Each says so once with a `$warning` at `@(initial_step)` when it is used outside its
temperature, using circuitRF's own 1e-9 tolerance. A value that is not finite raises `$fatal`, naming the
constituent and the point.

### 25.6 Refusals

All refusals are collected and reported in one `VerilogAExportException`, one line per field:

- a **table** constituent, every one by name, `IdRF` included (D17). The compiler takes neither arrays nor
  `$table_model`, and `$table_model` does not interpolate as circuitRF's not-a-knot spline does;
- a **network element** with no translation, or a parameter on an element other than its value;
- a **function** outside the set, or another construct with no translation;
- a **trap model with no Verilog-A writer** (§25.12), and a `Custom` trap expression with no translation;
- a **name** that is a Verilog-A keyword, built-in function or standard discipline or nature (`VaText.Reserved`, from
  the Verilog-AMS 2.4 keyword list), that the module already uses (`vgs`, `N`, `f_Id`, …), or a parameter that differs
  from another only in case, naming both. Names are refused and never renamed (open item below).

### 25.7 What had to change outside the writer

- **The device worker never installed the model's log hook.** A compiled model sends every `$display`, `$warning`,
  `$error` and `$fatal` through an `osdi_log` pointer that the library exports and the host must fill in. Left null,
  the first message a model printed killed the worker with nothing on stderr. `tools/osdi-worker` now installs one. It
  writes each distinct message to stderr once, because the worker evaluates every point as a DC operating point, so
  `@(initial_step)` runs on every evaluation. A `$fatal` is always written. A `$fatal` point therefore comes back as a
  refused point carrying the model's sentence (`tools/osdi-worker/RESOLVED.md`).
- **A `VerilogA` instance's `m` was forwarded to the model as a parameter**, and every model refused it, although the
  comment beside the code says circuitRF's own multiplier applies. It is now kept back, and the elaborator's
  multiplier applies (`src/Core/RESOLVED.md`).
- **The DC engine dropped a model's refusal.** A refused point is taken as a step to shorten, so a `$fatal` sentence never
  reached the user. When the solve then fails, the non-convergence report now ends with the last refusal
  (`src/Engine/RESOLVED.md`).

### 25.8 Measured

| What | Code | Compile |
|---|---|---|
| `ComposedTestModels`' all-equation model | 12 KiB | 0.1 s |
| The shipped example's own Ig, Qg and Qd networks (3 inputs, 2 × 8, Tanh, Asinh gate) with an equation Id | 110 KiB | 0.8 s |
| One 4 × 64 Tanh ANN (2 inputs) as Id | 1.2 MB | 30 s |

These were measured on an Apple-silicon Mac with the compiler on PATH. The shipped example as a whole — ANN currents
and charges, a drain-lag state and self-heating — exports since MC-27 as 267 KiB and compiles in 11 s; most of the
growth is the charges' Hessian-carrying copies for the bias point (§25.14). The brief's prototype compiled a 4 × 64 network with a thermal node
in about 10 s, a third of what this module takes. Each neuron here carries one gradient sum per input, which the
compiler differentiates again. After the first compile, the artefact is cached on the source's content.

### 25.9 Gates

`tests/Ui.Tests/ModelComposer/VerilogA/`:
- **`CmodelVerilogAWriterTests`** (text level): the same bytes twice, across a write → read → write, and from `Export`.
  Also the header and parameters, the module name, and each refusal by its sentence, including three tables in one
  message.
- **`EquationTranslationTests`** (text level): the function set against the engine's source, and every function plus
  `^`, a conditional, `e`/`pi`, division and a unit literal, run through a test-side Verilog-A interpreter
  (`VaInterpreter`) against `EvalDualInto` at 50 seeded points to 1e-14. It also checks the three traps' spellings.
- **`CompiledVerilogATests`** (`CompilerTheory`/`CompilerFact`, which skip with a reason when no compiler or worker is
  reachable). Ten cases each compare the `.va` placed as `VerilogA` with the `.cmodel` placed as `ComposedFET` on one
  bench. The cases are all-equation, all-ANN `Tanh` and `Sigmoid`, the asinh gate, `Exchange` swept through `vds = 0`,
  drift charges continued beyond their boxes, a `Range` model at 50 °C and at 150 °C (clamped, warned), `N = 7` and
  `m = 2`. The gates are:
  - **DC**, over a grid outside the pentagon on every side: `Id`, `Ig` within max(1e-9 relative, 1e-15 A);
  - **S**, at three biases, one beyond the oblique edge, 1–20 GHz: within 1e-9·max(1, |S|);
  - **HB**, a drive sweep to 3 dB compression: Pout and gain within 0.001 dB, PAE within 0.01 pp.

  The worst |ΔS| is 1e-14 to 3e-13, except `m = 2` at 9e-10. That is circuitRF's own `m`: at 1 GHz a `ComposedFET` with
  `m = 2` sits 8.5e-11 from two parallel `ComposedFET`s, while the `.va` with `m = 2` sits 2.8e-14 from two parallel
  `.va`s, and two parallel of each agree exactly. HB agrees to about 1e-10 dB. The DC solves run to a residual of
  1e-12 A. At a forward-biased gate the default 1e-6 alone leaves two exact implementations 2.5e-8 apart.
  `Exchange` uses the equation model, whose currents are continuous at `vds = 0`. The ANN test networks are not
  continuous there, and neither simulator solves near zero with them. Two more tests check that a non-finite
  constituent is refused naming it through the worker, and that a 4 × 64 network compiles (Benchmark tier, 30 s).

### 25.10 Open

- **A name that clashes in case or with a keyword is refused, not renamed** (brief §5). Renaming (`rg` → `rg_1`) would
  export every model, but it would change names the user chose and will look for in the other simulator.
- **`atan`, `asin`, `acos` and `atan2` have no derivative in the engine.** Both SDD compilers carry them as constants
  under differentiation (`CMathConstNode`, `EmitMathConst`), so an equation using one has a zero gradient through it in
  circuitRF. The emitted gradient copies the engine. The value code, however, is differentiated by the compiler, which
  gets the true derivative, so a `.va` and a `.cmodel` with such an equation disagree in S-parameters and HB but not
  in DC.
- **In circuitRF a `$fatal` is not a stop.** The worker refuses the point and the DC engine shortens its step, so the run
  ends unconverged with the sentence in its report, instead of stopping at the first refusal as another simulator would.

### 25.11 MC-27: the module grows a thermal pin, state nodes and its instance's parameters

```
module <name>(g, d, s[, t]);             // t only on a self-heating model
  thermal t;  thermal ti, t_1, ...;      // the network's own nodes
  electrical x;                          // a model with IdRF
  electrical phi1, phi2, ...;            // one per enabled trap state, in the model's order
  parameter real Rth = 0 from [0:inf);   // [0:0] on a model that does not self-heat
  parameter real Cth = 0 from [0:inf);
  parameter integer Charge = 1 from [0:1];  ... the ten, in ComposedSwitches.Names' order
  parameter real VgsQ = -1e+300;  VdsQ = -1e+300;  TjQ = -273.15 from [-273.15:inf);
  parameter real TauCapt2 = ... from (0:inf);  ...      // the trap model's own; Trap_<p> for Custom
  parameter real ShiftDrain = ...;  ShiftGate = ...;     // a Shift coupling's strengths
```

Every parameter and switch a placed `ComposedFET` has is a module parameter and does the same thing. A switch naming a
part the model lacks still exists and changes nothing, so every export's parameter list is the same; the header says
which switches do nothing for this model, with `ComposedSwitches.MissingPart`'s own reasons. `Rth` and `Cth` keep
`Temp`'s pattern: 0 is unset, the elaborator's refusal of a value ≤ 0 becomes the range, and a model that does not
self-heat refuses any value by `from [0:0]`, as the elaborator refuses it.

### 25.12 The state nodes

- **Dispersion** (§4.2's form): `I(x) <+ V(x) − (id − idrf) + ddt(τ·V(x))`, the drain carrying `idrf + V(x)`. Both
  currents are taken in the terminal frame, after any image; `x` is unscaled and `N/N0` multiplies the drain current it
  feeds. A drift charge reads `IdRF`, as fitted.
- **Traps**: one electrical node per enabled state, named for it, carrying `RateTrapDynamics`' form
  `I(phi) <+ ddt(C·V(phi)) − C·rate`, `C` = `RateTrapDynamics.NodeCapacitance` written from the constant. The rate and the
  DC value are two analog functions per state, `r_<state>(ph, vgs, vds, T)` and `dc_<state>(vgs, vds, T)`, reading the
  intrinsic voltages (a model with traps has no image) and the device temperature — the junction's when it self-heats,
  a one-temperature model's own `T0` otherwise.
- **Writers are per trap model**: `ITrapVerilogA`, registered by model name in `VaTraps`, gives the parameters and the
  two functions. A model registered with `TrapModels` and given no writer is refused by name. `CaptureEmission` is
  written line for line — the capture exponential continued by its tangent beyond `ExpLimit`, thermally activated
  emission (T in kelvin, `Tref` in °C) for a state whose `Ea` the model states, `Ea` written only then — and its
  parameters keep their names with `CaptureEmissionTraps.Build`'s refusals as ranges. `Custom`'s `Dynamics` and `Dc` go
  through the equation translator, value only (the compiler differentiates), its parameters prefixed `Trap_`.
- **The `Shift` coupling**: `Id` is evaluated at `vgs − ΔV`, `ΔV = Σ aₖ·sₖ·(φₖ − φₖ,DC)` with `aₖ` the module parameter
  and `sₖ` `CaptureSign`, as `TrapShift.Delta` forms it. A drift charge reads the unshifted `Id`, as circuitRF's does.

### 25.13 Self-heating and the thermal pin

`T = Tamb + ΔT`, `Tamb` the clamped device temperature and `ΔT` the internal node `ti`'s temperature. The power
`ig·vgs + id·vds` of the scaled terminal currents flows into `ti`. The network — `Rth ∥ Cth`, or a Foster ladder through
`t_1 … t_(n−1)` as `ComposedNetworkTemplate` names them — sits on `ti`, never on `t`: a host commonly grounds an
unconnected terminal, and a network written on it would be shorted and the model would silently stop heating. Wired
(`$port_connected(t)`), `t` is joined to `ti` by a zero-temperature-difference branch, so what is attached sits in
parallel with the model's network. Unwired, the module grounds `t` itself rather than leaving it with no equation:
circuitRF grounds a terminal beyond `Pins` only when the model collapses it, and a node left alone makes the S-parameter
matrix singular — regularised with gmin, S moved by 2e-8 (`src/Design/RESOLVED.md`). Scaling is the elaborator's: `Rth ×
N0/N` with `RthSlope` taken at `Tamb`, `Cth × N/N0`, or the instance's own `Rth`/`Cth` at its finger count, every ladder
section by the one factor that keeps the shape. The thermal nodes are read through `potential()` and `flow()`: the
module's parameter `Temp` hides the discipline's `Temp()` access function and the compiler refuses the call.

### 25.14 The switches

| Switch | What the module writes |
|---|---|
| `Charge = 0` | `qg = qd = 0`; the state nodes' own charges stay |
| `LinearC = 1` | `q(v_Q) + C(v_Q)·(v − v_Q)`, the charge read at `VgsQ`, `VdsQ`, `TjQ` and the states' DC values there |
| `LinearCgs/Cdg/Cds/Cm = 1` | the held surface less each frozen branch's residual `r_b`, `BranchLinearisation`'s construction |
| `GateLag/DrainLag = 0` | each state with that `CaptureSign` is `dc_<state>(vgs, vds, T)` as code, its node `V(phi) <+ 0` |
| `SelfHeating = 0` | `T = Tamb`, no power into `ti`; the network and the pin stay, so a wired pin sits at 0 K |
| `Dispersion = 0` | the drain carries `IdDC`, `V(x) <+ 0` |

A node cannot be left out of a module by a parameter, so a switched-off state is tied to zero and collapses. Because a
lag switched off writes the DC value as code, the compiler folds the state's lane into `vgs`, `vds` and `T` itself, and
under `Shift` that state's term is exactly zero. `Charge = 0` wins over the linear-charge switches and `LinearC` over the
branch switches, as in `ComposedSwitches.FrozenBranches`.

**The bias-point charge** is `qh(vg, vd, Th, h_<state>…)`: the intrinsic charge with its temperature and trap inputs
held, returning `Qg` and writing `Qd` and `C = ∂(Qg, Qd)/∂(vgs, vds)` in the terminal frame — circuitRF's
`HeldSurface`, from emitted gradients. An equation's gradient is its own function's. An ANN's must be the
continuation's exact gradient, `∇f(p_b) + Pᵀ H (p − p_b)`, because the Gauss nodes between the bias point and a driven
point leave the domain under drive: `VaDomain.GradientFunction` ports `GuidedExtrapolation.Evaluate` whole, the
projection's Jacobian included, and reads `H·w` from `VaAnn.HessianFunction`, forward mode over the gradient code.
A drift charge's pieces use `VaDomain.BoxGradientFunction`, and its channel current's gradient comes the same way. The
branch rule's nodes and weights are `BranchLinearisation.LineNode`'s, written as literals, and `C_b` at each node is
`qh`'s; the compiler supplies `∇C_b` by differentiating that code. Where circuitRF has no exact Hessian (a drift charge,
an ANN outside its domain) it differences `C`; the two then differ only in the Jacobian, which moves no converged
answer.

### 25.15 The bias point (D18)

`VgsQ` and `VdsQ` are the INTRINSIC voltages at the point circuitRF's freeze would settle on, `TjQ` its junction
temperature (the ambient plus the DC rise). circuitRF finds that point itself from a DC solve; a Verilog-A module cannot
ask its host for a quiescent operating point, so the user states it. Read it from a DC solve in circuitRF
(`V(X1.gi, X1.si)`, `V(X1.di, X1.si)`, the temperature plus `V(X1.t)`) or from the other simulator's operating point of
the internal nodes; the terminal bias differs from it by the drops on the network's resistances.

- **Unset is a refusal**: with any linear-charge switch on and `Charge = 1`, an unset `VgsQ` or `VdsQ` (−1e300, which no
  bias point can be) stops the module at `@(initial_step)` with `$fatal`, naming the switch and both parameters. A
  default bias would be a number nobody chose, giving a plausible wrong answer.
- **`TjQ` unset** is the device's temperature (`Tamb`). On a self-heating device with `SelfHeating = 1` and a
  linear-charge switch on, the module says once, with `$warning`, that the charge is frozen at the ambient and not at
  the junction temperature.
- **What circuitRF does differently**: it solves the quiescent point before the first charge is read and re-freezes when
  the DC solution moves (`BiasFreeze`). The module holds whatever point it is given; change the bias and the parameters
  must change with it.

Two things had to change outside the writer. The DC engine reported nothing when a model refused every point — the
`$fatal` above does exactly that — because its report is built from a residual there was none of
(`src/Engine/RESOLVED.md`). And `ComposedCoreModel.FrozenAt` exposes the point a freeze settled on, which the gates pass
to the module.

### 25.16 Gates (MC-27)

All against the `.cmodel` placed as a `ComposedFET` on one bench, to §25.9's tolerances, in
`tests/Ui.Tests/ModelComposer/VerilogA/`; the worst |ΔS| everywhere is 3e-14 to 1.2e-13.
- **`VerilogAStateNodeTests`**: dispersion, `CaptureEmission` gate-lag alone, drain-lag alone and both thermally
  activated on a `Range` model away from `Tref`, a `Custom` model, and the `Shift` coupling — DC on a grid, S at
  10 kHz, 1 GHz and 10 GHz (both sides of `1/τ`), one HB point. A model with trap states runs HB to a residual of
  1e-6 A: at 2 GHz the states' charge round-off floors it near 1e-7 A.
- **`VerilogAThermalTests`**: one pole, a three-section ladder with `RthSlope` at 60 °C, instance `N`, `Rth`, `Cth`,
  `Temp`, and a ladder given an instance `Rth`/`Cth` — `Id` and the rise at a dissipating bias, the three-pin placement
  heating (the rise above 5 K, `Id` moved by more than 1 %), no thermal warning, and S across the thermal pole. The pin
  wired to an external 100 K/W agrees with the `ComposedFET` given the same pin-4 network, and sits at 0 K with
  `SelfHeating = 0`.
- **`VerilogASwitchTests`**: `Charge`, `SelfHeating`, `GateLag`, `DrainLag`, both lags and `Dispersion`, each moving the
  answer; DC, S and one HB point. A switch for a part the model lacks changes nothing.
- **`VerilogALinearChargeTests`**: the point read from `FrozenAt` as `VgsQ`, `VdsQ`, `TjQ`; `LinearC`, each branch
  switch and `LinearCgs` with `LinearCds` on a self-heating device whose gate charge moves with temperature, and
  `LinearC` plus a branch pair on an all-ANN device (the continuation's gradient) and a drift device — S at the bias
  point and an HB sweep to compression. Unset `VgsQ` is refused with the sentence; unset `TjQ` warns once.
- **`CmodelVerilogAWriterTests`**: every declared parameter is in the header and the switches are
  `ComposedSwitches.Names` in order with their defaults, so a switch added to circuitRF without one here fails; a trap
  model with no writer is refused by name.

### 25.17 MC-28: three entry points, one function

`CmodelVerilogA.Export(path, vaPath, options, rc)` is what all three call (D15). `path` is a `.cmodel`, or a `.cmc`
assembled as `CmodelExport.Export` assembles it — the stale-step refusal first, then the writer's own. A `.cmc`'s header
hashes `CmodelFile.ModelJson` of the assembled model, the bytes its `.cmodel` would hold, and records
`VerilogAOptions.Created` (null: the time of the export). The result carries the module name, the parameter count and
the switches that change nothing for the model; `TablesLine(CmcDocument)` is the one line naming the fitted table
constituents, read from the step records before anything is assembled.
- **The Export step**: **Export Verilog-A…** beside **Export**, enabled when Export is and no constituent is a table;
  the tables line shows under the step when one is. The save dialog offers `<name>.va` beside the `.cmodel` path. The
  run is `RunOnFile`, as Export's; the line under the buttons is `<file>: module <name>`.
- **`model export <path> -o <out>`**: `-o`'s extension picks the format — `.cmodel`, `.va`, `.vams` — and any other is
  refused listing them. `<path>` may be a `.cmodel`; a `.cmodel` to `.cmodel` is refused. `--module` is new;
  `--created` and `--symbol` belong to a `.cmodel` and are refused for a `.va`. `--json` adds `result.model.verilogA`
  (`path`, `module`, `parameters`, `inertSwitches`); the output's kind is `veriloga`. Exit 1 on a refusal, every one
  of the writer's listed; 130 on a cancel.
- **The MCP `model` tool**: the `export` noun's `output` picks the format the same way, with `module` and a `.cmodel`
  `path`.

### 25.18 The example bench

`examples/Model Composer/synthetic-gan/gan_4x125.va` is the writer's output for the shipped `.cmodel`, written by
`ModelComposerExampleAuthoring.BuildTheVerilogABench` (the whole-workspace rebuild calls it too). The workspace's
`.gitattributes` marks `*.va -text`, so no checkout converts its line ends. `GanVerilogA` places the 8-finger device as
a `ComposedFET` on ports 1 – 2 and as a `VerilogA` (`File=synthetic-gan/gan_4x125.va Pins=3`) on ports 3 – 4, each on
its own copy of one bias network (100 nH chokes, 100 pF blocks, 50 Ω ports, input and supply probes), with a `VGS` sweep
from −2 to 0 V, S from 0.5 to 20 GHz and an HB drive sweep from 0 to 16 dBm at 2 GHz; its measures put each pair side by
side. Neither thermal terminal is wired. A schematic draws one net per pin, so the `ComposedFET`'s unwired T pin is
drawn from a one-pin net, `t1`, whose label the authoring then removes; the bench extracts as the three-net line a
schematic with an unwired T pin always has.

Three things had to change for the bench to exist:
- **A `VerilogA`'s `File` resolves against the workspace root**, as an SnP's and a `ComposedFET`'s do
  (`Elaborator.ResolveExtDeviceParameters`, and the parameter editor's description of the file). Until then a relative
  path resolved against the process's working directory, so no shipped workspace could name a `.va` at all.
- **`check` of a `VerilogA` compiles it**: elaboration describes the model through the worker. A machine with no
  compiler or no worker fails `check` on the bench, so `ExampleWorkspaceLoadsTests` checks the bench itself where both
  are reachable, and otherwise its own extracted netlist with `X2` placed as the `.cmodel` — the wiring, the analyses and
  the measures — while `ModelComposerExampleShippedTests` holds the `.va` to the writer's output.
- **A measure cannot raise a result to a power** (`mag(V)^2` is refused); the bench writes `real(V*conj(V))`.

### 25.19 Acceptance

`ModelComposerVerilogABenchTests` (a `CompilerTheory`) runs the bench's own extracted netlist through the engines, with
the DC residual at 1e-12 A — the bench's 1e-6 A default would let the two halves of one solve stop further apart than
the comparison allows — and compares the two devices at every point of the bench's three analyses, to §25.9's tolerances:
the switches at their defaults, `GateLag = 0` (a switch this model has no part for), `DrainLag = 0`, `SelfHeating = 0`,
and `LinearC = 1` with the module given the point circuitRF's freeze settled on (`VgsQ = −0.8453`, `VdsQ = 7.891`,
`TjQ = 26.36`). Measured:

| Switches | Worst \|ΔS\| | Largest HB difference: Pout, gain | PAE |
|---|---|---|---|
| defaults | 1.1e-13 | 3.4e-7 dB | 1.3e-6 pp |
| `GateLag = 0` | 1.1e-13 | 3.4e-7 dB | 1.3e-6 pp |
| `DrainLag = 0` | 1.3e-13 | 1.1e-6 dB | 6.7e-6 pp |
| `SelfHeating = 0` | 1.3e-13 | 3.4e-7 dB | 1.3e-6 pp |
| `LinearC = 1` | 9.3e-14 | 3.4e-7 dB | 1.3e-6 pp |

The DC probe currents agree to 1e-9 relative at every `VGS`. The HB differences are the bench's own 1e-6 A HB residual.

### 25.20 Gates (MC-28)

- **`ModelExportVerilogACliTests`**: the verb as a process against the in-process `Export`, from a `.cmc` (every line but
  the provenance line, which a `.cmc` export records at its own time) and from a `.cmodel` (byte for byte, with
  `--module`); the `--json` shape; each refusal — an unknown extension, `.cmodel` to `.cmodel`, `--created` with a `.va`,
  an illegal `--module`, and a table model naming both its tables in one refusal — writing nothing.
- **`ModelMcpToolTests`**: the tool's `.va` is the CLI's bytes; the table refusal over the protocol.
- **`ModelComposerExportStepTests`**: the button sits beside Export in the view; unblocked, it suggests `<name>.va`
  beside the `.cmodel` path and writes the in-process bytes, with its result line; a table blocks it with the one line
  and leaves Export enabled.
- **`ModelComposerExampleShippedTests`**: the committed `.va` is `Write` of the committed `.cmodel`, in the repository and
  as it ships — except the writer's version on the first line, so a release does not fail it before a re-export.
  **`ExampleWorkspaceLoadsTests`**: the bench checks clean, with or without a compiler.
- **`ModelCliNoLogicTests`**, **`ModelComposerNoLogicTests`**: both reach the export through `CmodelVerilogA.Export(`.
