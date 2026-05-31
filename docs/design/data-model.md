# circuitRF — Data Model Design

**Status:** Draft for review (rev 8) · **Date:** 2026-05-30
**Reads with:** `docs/PRD.md` (scope), root `CLAUDE.md` (invariants).
**Defers to:** `docs/design/expressions.md` (full grammar + AD/FD), `docs/design/linear-engine.md` (sparse MNA stamping), `docs/design/harmonic-balance.md` (the Evaluate → conversion-matrix Jacobian), `docs/design/measurements.md` (performance/measurement expressions).

This document defines circuitRF's data model across the three layers. It defines *types and their relationships*, not algorithms. No code is written until this is approved.

> **rev 2 changes (from review):** `ModelKind` simplified to binary; added the **Analysis** model and the **`Cell` vs `TestBench`** split (analyses attach to a `TestBench`, never to a `Cell`); noted the Phase-6 **net-extraction** boundary for schematic wiring; rewrote derivatives as **AD-default + FD-fallback**; `FetModel` supports **2/3/4 ports** (incl. thermal node); clarified **engine-owns-matrix, models-self-stamp**; reframed the **DataCube as storage + accessor** with extraction examples; added a `user_results` store and pointer to `measurements.md`.
>
> **rev 3 changes:** `LoadpullAnalysis` accepts a **Γ-grid or an impedance grid**; **measurements may be declared on any `Cell` at any level** (not only the `TestBench`) and **fan out to one result per cell instance**, keyed by instance path; measurement **operands resolve along a relative downward path from the declaring instance** (downward-only — not up or sideways); noted the **memory consideration** for retaining internal V/I and a future prune-to-referenced-nodes optimization.
>
> **rev 4 changes:** measurements moved **back to the `TestBench`** (not `Cell`); a single TestBench means **no per-instance fan-out** — a measurement is evaluated once, with operands as **absolute downward paths from the top** (`X1.N3`, `X1.inner.N3`); the DataCube gains a **`DataKind` (Real or Complex)** so scalar quantities (Pin, efficiency, PAE) are stored honestly rather than as complex-with-zero-imag.
>
> **rev 5 changes:** introduced the **`DataSet`** (container) vs **`DataCube`** (single-kind array) split — one run returns one `DataSet` holding many named `DataCube`s, so complex spectra and scalar measurements coexist in one result object while each array stays one honest kind; `user_results` is now the **measurement-cube group within the `DataSet`**.
>
> **rev 6 changes:** resolved variables and parameters carry a **`Real` or `Complex` kind** (the same `DataKind` as the result model), not a forced `Complex` — most component values are Real, impedances are Complex; this lets a model validate its inputs (a resistor rejects a complex `R`). Updated §3 (`ElaboratedComponent.Parameters`), §8 (Evaluate result), and §11.
>
> **rev 7 changes:** corrected the §7 result-access notation. S/Y/Z cube axes are **`{freq, i, j}`** where `i`/`j` are **user-assigned port numbers** (not net names, not `outPort`/`inPort`). Replaced the `.Axis(...).Trace(...)` examples with the real accessor scheme: network params via `ds.S(i,j)`; HB cubes via `ds.V("name", <slice>)` with **`int` pinning/collapsing an axis** and **end-exclusive `Range`** (`..`/`All`/`2..4`) keeping it; positional `ds["..."]` escape hatch. Ranges are **NumPy/C#-style end-exclusive, not MATLAB-inclusive**. Bracket slots are **indices, never physical values**. Slicing returns a **`DataCube`** (or the bare element when fully pinned); **element-wise transforms** (`.real/.imag/.mag/.phase/.dB10/.dB20/.conj`) preserve rank and set `DataKind`; **reductions** collapse a named axis. `.dB()` = `.dB10()` (power); use `.dB20()` for amplitude/S-params. Full detail in `src/Core/Data/CLAUDE.md`.
>
> **rev 8 changes:** the **`TestBench` now holds its own `Instances`** (cell instances *and* bare primitives like R/L/C and `Port`/`Term`) plus its own nets — it is a top-level container, not a reference to a single `TopCell`. The `TopCell` field is removed. A `TestBench` has **no cell-port interface** (nothing instantiates it from above); `Port`/`Term` are ordinary primitive instances that fully participate (they define the analysis ports). `Cell` and `TestBench` remain **separate types** (no shared base) to keep the "analyses/measurements attach to TestBench, never Cell" invariant loud. Elaboration now treats the TestBench's own instance list as the root frame. Updated §2.1, §3, §11.

---

## 1. The three layers

circuitRF separates three concerns that must never bleed into each other:

1. **Design layer** — the editable, hierarchical description a user builds: libraries, cells, instances, nets, parameters, variables, and — at the top — a test bench with its analyses. Serializable to the `.cnl` netlist and to JSON. The GUI edits *this* and nothing below it.
2. **Elaboration layer** — the derived, flattened result: hierarchy expanded, parameters and variables resolved to concrete values, internal nets uniquified, nodes numbered. This is what the engine consumes.
3. **Numeric layer** — pure math: the `ComponentModel` instances that stamp/evaluate, the sparse MNA system, and the result model (`DataSet`/`DataCube`). No UI types, no design-layer types.

The data flows one way: **design → elaboration → numeric**. The GUI never hands the engine a design-layer object; it always elaborates first.

```
TestBench → Cell/Instance       (design)
        │  elaborate (flatten + resolve + number)
        ▼
ElaboratedNetlist               (elaboration)
        │  build models + stamp/evaluate, per analysis
        ▼
MnaSystem / DataSet             (numeric)
```

---

## 2. Layer 1 — Design model

The design layer has a **logical** part (built in Phase 1, populated by `.cnl`/JSON) and a **graphical** part (Symbol/Schematic/Layout view geometry, added in Phase 6). The logical part is below; graphical views attach to `Cell` later and are out of scope for the data-model work that gates Phase 1.

### 2.1 Cell vs TestBench — the central distinction

Two concepts the word "cell" was doing double duty for:

- A **`Cell`** is a **reusable definition** — ports, a parameter interface, and contents. It is what lives in a `Library` and gets instanced (possibly many times, with different parameters). **A `Cell` never contains analyses.**
- A **`TestBench`** is **one specific thing you simulate** — its own contents (cell instances *and* bare primitives, with their nets), the global variables in effect, and the list of analyses to run. It is a top-level container, *not* a reference to a single top cell. Analyses attach *here*, at the level where real ports, sources, and terminations exist.

**A `TestBench` holds its own instances, like a top-level schematic.** You drop cell instances (`MyPiCell:X1`), bare primitives (`R`, `L`, `C`), and `Port`/`Term` components directly into it and wire them with nets — exactly what a top-level design is in any real tool. It does **not** point at one `TopCell`; that artificial extra layer is gone (you no longer have to wrap a top-level resistor and two sub-blocks inside a throwaway cell).

**A `TestBench` has no cell-port interface.** A `Cell` has a `Ports` list because a parent connects to it from above; nothing ever instantiates a `TestBench`, so it has no ports to expose and no `Ports` list. Port-ness at the top comes entirely from **`Port`/`Term` primitive components** placed in the instance list — these are ordinary primitives that fully participate (they connect to nets and the S-parameter engine reads them to know where to excite/measure). They are *not* a cell-style interface and are never ignored.

**`Cell` and `TestBench` stay separate types** (no shared base), even though both now carry `Instances` + `Variables`. Keeping them distinct keeps the invariant below loud: a `Cell` is reusable and analysis-free; a `TestBench` is the unique, non-reusable top that owns analyses and measurements.

**Invariant:** *analyses attach to a `TestBench`, never to a `Cell`.* A `Cell` is reusable and could be instanced mid-hierarchy where "sweep the input power" is meaningless; a `TestBench` is the outermost boundary where an analysis is well-defined. This mirrors the prototype netlists, where simulation directives only ever appear at the top design, never inside a `define … end` block. (A future convenience — a `Cell` shipping a *suggested* analysis template the user promotes into a `TestBench` — is explicitly out of v1 to keep the boundary crisp.)

**Measurements attach to the `TestBench`, and reach downward.** A *measurement* (a performance expression like Gain or PAE, §9) lives on the `TestBench`, alongside the analyses. Because there is exactly one TestBench at the top — it is never reused or instanced — a measurement is evaluated **once**, with no per-instance fan-out. Its operands are **absolute downward paths from the top** that may reach into any sub-cell at any depth (`X1.N3`, `X1.inner.N3`). A measurement therefore names a *specific* instance: to measure both stages of a two-stage amplifier, write one measurement naming `X1` and another naming `X2`.

```csharp
class Library
{
    string Name;
    List<Cell> Cells;
}

class Cell
{
    string Name;
    List<string> Ports;                      // ordered port names, used to connect to a parent
    List<ParameterDeclaration> Parameters;   // the cell's parameter interface
    List<Variable> Variables;                // cell-scoped variables
    List<Instance> Instances;                // contents: sub-cells and primitives
    // Phase 6 (graphical, optional): SymbolView, SchematicView, LayoutView (placeholder)
}

class Instance
{
    Guid Id;                                 // identity for GUI selection / undo (Phase 6 only)
    string InstanceName;                     // "X1", "R1"
    string Reference;                        // a primitive type ("R", "FET", …) or a Cell name
    List<string> NetBindings;                // ordered; connects this instance's ports to nets in the parent cell
    List<ParameterAssignment> Overrides;     // parameter name -> expression, evaluated in the PARENT scope
}

class TestBench
{
    string Name;
    List<Instance> Instances;                // top-level contents: cell instances AND primitives (incl. Port/Term)
    List<Variable> GlobalVariables;          // globals in effect for this run
    List<Analysis> Analyses;                 // one or more; see §4
    List<Measurement> Measurements;          // performance expressions; operands reach downward; see §9
    // No TopCell reference and no Ports interface: a TestBench IS the top container, and nothing
    // instantiates it from above. Top-level nets come from the Instances' NetBindings.
}
```

### 2.2 Parameters and variables

A parameter has a *declaration* on the cell (a default) and an *assignment* on the instance (an override). The override expression is evaluated in the parent's scope — this is how `X1 … L1=5 C2=C2` passes a parent value `C2` down into the child. Resolved numeric values are **not** stored here; they are computed during elaboration and live on the elaborated component. This keeps the design layer purely symbolic and editable.

```csharp
class ParameterDeclaration { string Name; string DefaultExpression; string Units; bool Hidden; }
class ParameterAssignment  { string Name; string Expression; string Units; }   // expression in parent scope
class Variable             { string Name; string Expression; string Units; }   // scope is structural (global vs cell)
```

**On scope (important change from a flat netlist).** Rather than keying variables by string triples like *(name, subcircuit-name, subcircuit-instance)*, scope is represented *structurally*: globals live on the `TestBench`, a cell carries its own variables, and an instance's overrides are bound when that instance is elaborated. The elaborator walks a scope chain. This removes the fragile string-keyed lookups and makes arbitrary hierarchy depth correct by construction.

### 2.3 Schematic wiring (logical now, geometry later)

Logical connectivity already lives here as nets (`NetBindings`). Graphical wire geometry — routes, vertices, junctions, x/y, junction dots — is presentation and belongs to the Schematic view in **Phase 6**, not here.

One direction-of-truth point worth stating now so the Phase-6 editor isn't mis-designed: for a `.cnl`/JSON-authored design, nets are **explicit** (the schematic is drawn to match). For a **GUI-authored** design, the opposite holds — wires and pin adjacency *define* the nets — so a **net-extraction** step (geometry → logical nets) runs before elaboration. That extraction is the bridge from the wiring layer to the design model; it is a Phase-6 boundary, flagged here, designed there.

### 2.4 Identity

`Guid Id` appears only where identity genuinely matters — design-layer `Instance` (and later `Cell` views) for GUI selection, undo/redo, and stable references. The elaboration and numeric layers use integer indices and instance paths instead; no GUIDs there.

---

## 3. Layer 2 — Elaboration

The **elaborator** takes a `TestBench` (its own instances + global variables) plus its libraries and produces an `ElaboratedNetlist`. It does three things, recursively, depth-first:

1. **Flatten** the hierarchy: the TestBench's own `Instances` are the **root frame** (elaborated in the scope of the TestBench's `GlobalVariables`); walk each instance; primitives are emitted, cells are recursed into with a fresh scope. (There is no "enter the TopCell first" step — the TestBench's instance list *is* the top.)
2. **Resolve** parameters and variables: every expression is evaluated to a concrete value via the expression engine against the current scope, with **cycle detection** (§5). Units are applied here.
3. **Number** the circuit: ports map onto parent nets; internal nets are made unique by prefixing the instance path; ground is node `0`; remaining nets get integer indices.

```csharp
class ElaboratedNetlist
{
    List<ElaboratedComponent> Components;
    NodeMap Nodes;                           // name <-> index; ground = 0
    IReadOnlyList<int> NonlinearComponents;  // indices into Components whose Model is nonlinear
    IReadOnlySet<int>  NonlinearNodes;       // nodes touched by any nonlinear component (HB partition seed)
}

class ElaboratedComponent
{
    string ComponentType;                    // "R", "C", "FET", "SnP", …
    string InstancePath;                     // "X1.R1" — unique, human-traceable
    int[]  Nodes;                            // resolved node indices
    int    ReferenceNode;                    // supports a reference other than ground
    IReadOnlyDictionary<string, Value> Parameters; // fully resolved (each value Real or Complex, §8), units applied
    ComponentModel Model;                    // the numeric behavior (Layer 3)
    bool   IsNonlinear;                       // derived from Model.Kind
}
```

A resolved parameter `Value` carries a **kind** — `Real` or `Complex` (the same `DataKind` as the result model, §7) — rather than being forced to `Complex`: most component values (`R`, `L`, `C`) are Real, impedances are Complex. This lets a `ComponentModel` validate its inputs (a resistor rejects a complex `R`), and a `Real` value promotes to complex for stamping where needed (§8 promotion rules).

The `NonlinearComponents`/`NonlinearNodes` sets are computed here so the harmonic-balance engine can partition without re-deriving topology. A component contributes nonlinear nodes only if its `ComponentModel` is nonlinear — a passive RLC never seeds the nonlinear partition.

Elaboration is per-`TestBench`, not per-analysis: the flattened netlist is built once, then each `Analysis` runs against it (a parametric sweep re-resolves only the swept variable, not the whole hierarchy). Node ordering for the *numeric* solve (a fill-reducing permutation for the sparse matrix) is a numeric-layer concern and lives in `linear-engine.md`; the elaborator only needs a stable, unique numbering.

---

## 4. Analyses (the simulations to run)

An analysis is a one-time experiment on the assembled circuit. Analyses live on the `TestBench` (§2.1). A `TestBench` may hold several — e.g. an S-parameter run *and* a single-tone HB *and* a loadpull on the same circuit.

```csharp
abstract class Analysis
{
    string Name;
    List<SweepSpec> Sweeps;                  // zero or more parametric sweeps wrapping this analysis
}

class DcAnalysis           : Analysis { }
class SParameterAnalysis   : Analysis { FrequencySpec Freq; }
class HarmonicBalanceAnalysis : Analysis
{
    ToneSpec[] Tones;                        // 1 = single-tone, 2 = two-tone
    int MaxHarmonic;                         // H (Hero 2/4 default 7)
    int MaxMixingOrder;                      // two-tone truncation; >= 5 for Hero 5 (IM2–IM5)
}
class LoadpullAnalysis     : Analysis        // a first-class experiment (PRD differentiator)
{
    PortRef Dut;                             // device-under-test port
    TerminationGrid Grid;                    // user picks ONE coordinate space (see below)
    HarmonicTermination[] Harmonics;         // optional harmonic loadpull terminations
}

// The user defines the sweep in EITHER the reflection-coefficient plane OR the impedance plane;
// the engine converts via Gamma = (Z - Z0)/(Z + Z0). The grid records which space it was defined
// in, because a rectangular grid in one plane maps to a warped region in the other — the Data
// Display must contour on the coordinates the user actually chose.
abstract class TerminationGrid { Complex Z0; }            // reference impedance for the conversion
class GammaGrid    : TerminationGrid { /* real/imag Γ extents + steps, |Γ| limit */ }
class ImpedanceGrid: TerminationGrid { /* R/X extents + steps (Z-plane) */ }

class SweepSpec    { string Variable; double Start, Stop, Step; SweepKind Kind; } // Variable = Pin, a Vds, any §5 variable
class FrequencySpec{ double Start, Stop, Step; SweepKind Kind; }
class ToneSpec     { string FrequencyExpression; string PowerVariable; }          // e.g. 1.995 GHz, drive = Pin
```

A `SweepSpec.Variable` references a variable by name (the prototype's `SweepVar="freq"`, plus Pin/Vds sweeps), tying sweeps directly into the expression engine and producing the sweep axes within the result cubes (§7). Detailed semantics of each analysis (frequency conversion, continuation, truncation geometry) live in the engine notes (`linear-engine.md`, `harmonic-balance.md`); this section fixes only the *data model* of "what to run."

---

## 5. Layer 3 — Numeric: `ComponentModel`

`ComponentModel` is the single base type that every component's electrical behavior derives from — passive and active alike. ("Device" is reserved for its RF meaning, an active part; an active device is simply one kind of `ComponentModel`.) It collapses the previously-separate linear and nonlinear model notions into one type with two optional contributions.

```csharp
// A component is, for harmonic-balance partitioning, either part of the linear
// subnetwork or a nonlinear device — never both. The partition is binary.
enum ModelKind { Linear, Nonlinear }

abstract class ComponentModel
{
    int       PortCount { get; }
    ModelKind Kind      { get; }

    // Linear contribution: the model CONTRIBUTES stamps; the engine OWNS the matrix.
    // The engine hands the model a controlled stamping API (mna.AddAdmittance(a, b, y),
    // plus branch-row helpers for sources/ideal elements). Frequency-domain N-ports
    // (Touchstone block, impedance block, transmission line) contribute their interpolated
    // network at omega; R/L/C contribute element values. A nonlinear model also contributes
    // here during nonlinear DC, stamping its linearized companion each Newton step.
    virtual void Stamp(MnaSystem mna, ElaboratedComponent c, double omega) { }

    // Nonlinear contribution in the time domain (nonlinear DC and harmonic balance).
    // Given port-voltage samples, returns:
    //   i  = port currents
    //   q  = port charges
    //   dg = di/dv  (conductances)
    //   dc = dq/dv  (capacitances)
    virtual NonlinearResult Evaluate(in PortVoltages v) => throw new NotSupportedException();
}

readonly struct NonlinearResult { double[] I; double[] Q; double[,] Dg; double[,] Dc; }
```

**The `i, q, dg, dc` contract is the key extension.** The prototype returned only current and conductance (`i`, `g`); circuitRF adds **charge `q` and capacitance `dc = dq/dv`**. This is required for accurate harmonic balance of reactive nonlinear behavior and is exactly what the v2 ASM-HEMT path needs (charge-based capacitances), per PRD §6.1.

**Who owns the matrix.** The **engine owns `MnaSystem`** and orchestrates assembly and the sweep; each `ComponentModel` only *contributes* its stamps through a controlled API. This inverts the prototype's central-stamping routine (one switch that knew every component type) in favor of distributed self-stamping — which is what makes "adding a new component type as straightforward as possible" (PRD §6) true: a new type is a new class overriding `Stamp` and/or `Evaluate`, touching nothing else. Central *control* stays at the engine; only *extensibility* is distributed.

**How derivatives are produced (`dg`, `dc`).** Three tiers; the contract above is unchanged, only the internal strategy differs:
- **Built-ins** provide closed-form derivatives (best accuracy, continuous by construction).
- **SDD default: forward-mode automatic differentiation.** AD propagates through an `if(cond, …)` by evaluating the condition and differentiating the *active* branch, so piecewise user expressions are supported; the derivative is one-sided at a switching boundary (where a hard conditional may be genuinely discontinuous).
- **SDD fallback: finite-difference**, user-selectable per model, for expressions where the user prefers it or AD misbehaves. (The prototype's central-difference FET, `StepSize = 1e-4`, is the known-good reference for this path.)

Guidance, not a restriction: for models that must stay smooth for tough convergence, prefer **soft switching** (e.g. `tanh`) over a hard `if`; hard conditionals remain allowed, with AD differentiating the active branch. The AD scheme and FD option are detailed in `expressions.md`.

### 5.1 Concrete models (illustrative, not exhaustive)

| Category | Models | Notes |
|---|---|---|
| Passive primitives | `ResistorModel`, `InductorModel`, `CapacitorModel`, `MutualInductanceModel` | `Mutual` references its two inductor instances by resolved instance path |
| Frequency-domain N-ports | `TouchstoneModel`, `ImpedanceBlockModel`, `TransmissionLineModel`, `FrequencyDomainUserModel` | `TouchstoneModel` wraps an **RfCore `Network`** (§6); the impedance block evaluates `Z[i,j]` expressions per frequency |
| Sources & probes | `DcVoltageSource`, `AcVoltageSource`, `AcCurrentSource`, `RfPowerSource`, `PortModel`, `TermModel`, `CurrentProbe` | |
| Nonlinear | `SymbolicDeviceModel` (SDD), `FetModel`, `BjtModel`, `DiodeModel` | SDD = expression engine + AD/FD; the hero PA FET is an SDD |

**`FetModel` port count is configurable — 2, 3, or 4:**
- **2-port** — gate/drain, source internally grounded (a common designer convenience).
- **3-port** — gate/drain/source (the standard intrinsic device).
- **4-port** — adds a **thermal node** for an electro-thermal model (self-heating).

The thermal node is the same mechanism the v2 Verilog-A/OSDI path needs for ASM-HEMT (PRD §6.1), so a native electro-thermal `FetModel` and ASM-HEMT readiness share one concept. Any of these is *also* expressible as an N-port SDD; the native `FetModel` exists for convenience and closed-form derivatives.

The sparse MNA system (`MnaSystem`), how each category stamps into it, DC handling, and node ordering are specified in `linear-engine.md`. How `Evaluate` feeds the conversion-matrix Jacobian is in `harmonic-balance.md`.

---

## 6. RfCore touchpoints

circuitRF consumes RfCore (referenced as a sibling `ProjectReference`, RfCore living outside `circuitRF/src`) rather than reimplementing network math. The types/operations circuitRF depends on — and that the RfCore extraction must expose publicly:

- A **`Network`** type: S/Z/Y representation with per-port (optionally **complex**) reference impedance.
- **Touchstone** read/write (`.sNp`), including the 2-port column-order convention.
- **Interpolation** over frequency.
- **Renormalization** (per-port complex Z₀, power-wave formula) and de-embedding.
- **Construct-from-computed-data**: build a `Network` from a Y or Z matrix set on a frequency grid — the harmonic-balance engine uses this to wrap the linear subnetwork as an N-port block.
- The **`DataSet`** and **`DataCube`** types (shared result contract).

`TouchstoneModel` and the other frequency-domain N-port models hold an RfCore `Network` and stamp its interpolated value at each analysis frequency.

---

## 7. The result model — `DataSet` and `DataCube`

Every analysis run returns one **`DataSet`** — the labeled collection a user thinks of as "the results of this simulation." A `DataSet` holds many **`DataCube`s** keyed by name; each `DataCube` is a single named-axis array with one `DataKind`. Both types live in **RfCore** (so splotRF consumes them directly); circuitRF owns the contract.

- **`DataCube`** — the storage primitive and the unit splotRF plots: a labeled, unit-bearing, N-dimensional array with named axes and a single **`DataKind`** (`Complex` or `Real`). One array, one element type.
- **`DataSet`** — the container a run hands back: named cubes that coexist regardless of kind. An HB-with-measurements run returns one DataSet holding complex `"V"`/`"I"` spectra *and* the real `"PAE"`/`"Gain"` measurements together. A sweep wrapping the analysis adds a sweep axis across the cubes within the one DataSet. (The prototype's `SNPDataStorage` was a small fixed version of this — it held S, Z, Y together; the `DataSet` generalizes it to a whole run's output.)

This is the answer to "one result, two element kinds": **the DataSet holds both** (complex spectra and scalar measurements side by side), while **each DataCube stays one honest kind** — no phantom imaginary parts.

**Why a `DataCube` is single-kind.** Many RF quantities have phase and are genuinely complex (S-parameters, node-voltage spectra, Γ, Z). But circuit-performance quantities are genuinely real (Pin in watts, drain efficiency, PAE, gain in dB). Storing a real quantity as complex-with-zero-imaginary is a lie that leaks: it doubles storage, forces downstream code to guess whether a zero imaginary part means "no phase" or "not yet computed," and risks a plot showing a meaningless 0° phase. So a cube records its element kind — `Complex` backed by `Complex[]`, `Real` backed by `double[]` — and the accessor surfaces the right type. A `Real` cube can be *promoted* to complex on request (for a consumer that only speaks complex), but storage stays honest.

**Which kind a cube gets.** **Primary simulation results that carry phase are `Complex`** (S-parameters, harmonic spectra of node voltages and branch currents); **derived measurements take the kind their measurement function returns** — `PAE`, `DE`, `Pout_dBm` → `Real`; `Gamma_load`, `Zin` → `Complex` (§9). The measurement function declares its output kind, and the cube it adds to the DataSet takes that kind. (splotRF must handle both kinds when consuming a cube — a contract note for the RfCore extraction.)

**Cubes within a DataSet.** Each analysis populates its DataSet with the cubes natural to it (plus any measurement cubes, §9). For S/Y/Z cubes, **`i` and `j` are the user-assigned port numbers** (`i` = response, `j` = stimulus, as in `S(i,j)`) — port *numbers*, never net names:

| Analysis | Primary cubes (name → axes) |
|---|---|
| S-parameter | `S` → `{freq, i, j}` |
| Single-tone HB power sweep | `V`, `I` → `{node, harmonic, Pin}` |
| Two-tone HB | `V`, `I` → `{node, mixIndex, …drive}` |
| Fundamental loadpull | `V`, `I` → `{node, harmonic, gammaRe, gammaIm}` |

**Accessor layer (the ergonomics).** A raw N-D array is unpleasant to use directly, so a thin accessor sits on top — and a **1-D slice is exactly a plot trace** for splotRF. Two layers: convenience accessors that speak the user's language, and a positional escape hatch. (Full specification in `src/Core/Data/CLAUDE.md`.)

```
// Network parameters — by user-assigned PORT NUMBER (i = response, j = stimulus):
ds.S(2, 1);                       // the S21 trace over frequency  -> 1-D complex trace
ds.S(2, 1).dB20();                // |S21| in dB (20·log10 — conventional S-param dB; see below)

// HB node voltages — by node NAME, then a positional slice of the remaining axes.
// V cube is {node, harmonic, Pin}; node is consumed by the call, bracket indexes (harmonic, Pin).
// Every bracket slot is an axis INDEX, never a physical value (1 = harmonic index 1, 0 = Pin index 0):
ds.V("X1.drain", 1, ..);          // harmonic=1 (fundamental), all Pin -> 1-D trace vs Pin
ds.V("X1.drain", 1, All);         // same; `All` is an alias for the `..` range
ds.V("X1.drain", .., 0);          // all harmonics at Pin index 0      -> the spectrum at the node
ds.V("X1.drain", 1, 2..4);        // harmonic=1, Pin indices 2,3 (end-exclusive) -> length-2 trace
ds.V("X1.drain", 1, 3);           // harmonic=1, single Pin index 3   -> a single complex value

// A measurement is just another named cube:
ds["PAE"]; // -> the PAE cube (a 1-D real trace over the swept axis)
```

**Slice semantics:** **every bracket slot is an axis index, never a physical value** (resolve a value to an index first if needed). Harmonic is addressed by index (`0`=DC, `1`=fundamental, …; two-tone uses a tone pair). A single **`int` pins and removes** an axis; a **`Range` keeps** it (`..`/`All` = whole, `2..4` = sub-range). **Ranges are end-exclusive** — conforming to NumPy and C#, **not** MATLAB-inclusive. The `ds["..."]` form indexes every axis positionally as a low-level escape hatch.

**What a slice returns.** **Any `..`/range present → a `DataCube`** of rank = number of free axes (axes/units preserved); a rank-1 result *is* "a trace." **All free axes pinned with `int` → the bare element** (`Complex`/`double`), not a rank-0 cube — matching NumPy (`a[1,3]` scalar vs `a[1,:]` array). Slicing is a closed algebra: `ds["S"]` and `ds.S(2,1)` are the same type, so results re-slice.

**Transforms vs reductions** (full spec in `src/Core/Data/CLAUDE.md`). **Element-wise transforms preserve rank/axes and set `DataKind`** — `.real()`, `.imag()`, `.mag()`, `.phase()`, `.dB()/.dB10()/.dB20()` → Real; `.conj()` → Complex. So `.real()` on a rank-2 Complex cube returns a rank-2 *Real* cube; for a rank-1 result, slice first then transform. **dB is explicit, never context-keyed:** `.dB10()` = `10·log10(|z|)` (power dB), `.dB20()` = `20·log10(|z|)` (amplitude dB — the conventional `20·log10|S21|`), and `.dB()` is an alias for `.dB10()`; use `.dB20()` for S-parameters/voltages. **Reductions** (`.max("Pin")`, `.peak("Pin")`, …) collapse a named axis, dropping rank. `.Values` exposes the raw backing array when bare numbers are wanted.

**Engine requirement surfaced by results.** To support the measurements in §9 (Pout, PAE, IMn, …), the engine must **retain node voltages *and* branch currents** per harmonic per sweep point — measurements need both V and I. This is a stated requirement on what HB stores, not an afterthought.

Axis/units representation, the swappable backing store, slicing/reduction, and `.mat`/`.npy` export shape (per-cube vs whole-DataSet) are detailed in the data-cube note and `src/Core/Data/CLAUDE.md`.

---

## 8. Expression engine, variables & cell parameters

One engine serves global variables, cell parameters, SDD device equations, *and* measurement expressions (§9) (PRD §7). It is a real tokenizer → parser → AST → evaluator — **not** string substitution.

```
Tokenize  →  Parse  →  Ast  →  Evaluate(scope) → Real or Complex (or, for measurements, a cube quantity)
```

- **AST nodes:** complex literal (`j` notation), variable/parameter reference, unary/binary operators (`+ - * / ^`, comparisons `< <= > >= == !=`, boolean `&& || !`), conditional `if(cond, then, else)`, function call (built-in `tan`, `tanh`, `sin`, `cos`, `exp`, `log`, `sqrt`, `abs`, …, and user-defined functions), and a units suffix that scales the value.
- **Scope chain:** global variables (on the `TestBench`) at the base; each elaborated cell instance pushes a scope that binds the cell's parameters to the instance's override *expressions* (evaluated in the parent scope). Resolution walks the chain outward.
- **Cycle detection (required):** when resolving a name, mark it *in-progress*; re-entering an in-progress name is a cycle — the engine reports the offending chain (e.g., `a → b → a`) rather than recursing forever. This closes a real gap in the prototype, which could recurse without a guard.
- **User-defined functions:** `f(x, y) = <expression>` stored as named lambdas; a call binds arguments into a local scope.

The full grammar, operator precedence, and the AD/FD scheme that yields `dg`/`dc` for the SDD live in `docs/design/expressions.md`.

---

## 9. Measurements (performance expressions on results)

Performance parameters — Gain, Pout, Pin, drain efficiency, PAE, IMn/IMD3, and **user-defined** figures of merit — are computed *after* a simulation by evaluating **measurement expressions** over the result cubes, then **added to the run's `DataSet` as named measurement cubes** (the `user_results` group), so they plot and export like primary results.

```csharp
class Measurement { string Name; string Expression; string Units; }  // declared on the TestBench (§2.1)
```

**Declared on the `TestBench`, evaluated once.** There is exactly one TestBench at the top, so a measurement is *not* a reusable template and does **not** fan out — it is evaluated a single time and added to the DataSet as a named cube: `ds["Gain"]`, `ds["PAE"]` (the `user_results` group). (Parametric sweeps still produce a *trace* of the measurement over the swept axis; that is an axis on the one cube, not multiple cubes.)

**Operands are absolute downward paths from the top.** A measurement may reference any node, port, or instance below the top, at any depth, by its path: `V(X1.drain)`, `I(X1.inner.R3)`, `S(2,1)`. Because the path is absolute it names a *specific* instance — to measure both stages of the 2-stage PA hero, write `Gain_stage1` referencing `X1` and `Gain_stage2` referencing `X2`. There is no upward or sideways reach to worry about: everything is anchored at the single top and walks inward.

**Expression engine, extended for cube operands.** Measurements reuse the §8 engine, but operands may be **cube quantities** — a node-voltage spectrum, a branch current over a sweep — not just scalars (the "grows later: vectors" line anticipated in PRD §7). The engine gains built-in measurement functions (`Pout`, `Pin`, `Gain`, `DE`, `PAE`, `IMn`, `dBm`, `mag`, `phase`, harmonic selection, sweep reductions) plus user-defined measurements written in the same language. Each function declares whether it returns a `Real` or `Complex` result, which sets the `DataKind` of the measurement cube it adds to the DataSet (§7).

**Which analysis a measurement reads.** A measurement references quantities that exist only in certain result cubes — `S(2,1)` lives in an S-parameter result, `PAE` needs harmonic-balance voltages and currents. For v1 the simplest well-defined rule is: a measurement is evaluated against each analysis result in which **all** its operands resolve; if its operands resolve in none (or ambiguously in several), it reports an error naming the missing/ambiguous quantity. Whether to also allow a measurement to *name* its target analysis explicitly is a smaller open item for `measurements.md`.

**Memory consideration.** Measurements need both node voltages and branch currents per harmonic per sweep point (§7). Because operands can reach deep into the hierarchy, the *referenced* set can include internal nodes anywhere in the design — so a future optimization that prunes retained state to only-referenced nodes must account for deep references. For v1 the engine retains the full internal solution (it already computes it); the prune-to-referenced-nodes pass is noted as a later optimization, aware that deep measurement reach widens what it must keep.

This layer is large enough to own its own note — **`docs/design/measurements.md`** — covering absolute-path operand resolution, the cube-operand expression extensions, the built-in measurement library, IMn extraction from two-tone results, which analysis result a measurement is evaluated against, and how measurement cubes are named and stored in the DataSet (including their `DataKind`). It touches three things defined here: the result model (measurement cubes in the `DataSet`, §7), the expression engine (cube operands, §8), and the `TestBench` (measurements are declared there, §2.1).

---

## 10. The `.cnl` netlist format

`.cnl` is circuitRF's first-class textual netlist — a vendor-neutral hierarchical format that maps directly onto the design layer. A JSON circuit description (PRD §10) carries the same logical model. The importer skips unknown header/comment lines so real-world exports import cleanly; committed test fixtures are authored in clean `.cnl`.

```
; circuitRF netlist (.cnl)

; global variables (expressions may reference other variables; cycles are rejected)
L1 = 1.0
C2 = gizmo
gizmo = funtimes
funtimes = 2

; cell definition: ports, a parameter interface with defaults, and contents
define MyPiCell ( P1 P2 P3 )
  parameters L1=5 C2=1
  R:R1  P1 N1  R=50 Ohm
  L:L1  P1 0   L=L1 nH
  C:C1  N1 P2  C=C2 pF
  C:C2  P3 N1  C=10 pF
end MyPiCell

; top level (the TestBench): top instances, then the analyses to run
Port:Term1   N3 0   Num=1 Z=50 Ohm
Port:Term2   N7 0   Num=2 Z=50 Ohm
Port:Term3   N0 0   Num=3 Z=50 Ohm
MyPiCell:X1  N3 N0 N2  L1=5    C2=C2
MyPiCell:X2  N7 N2 N0  L1=0.25 C2=10

; analysis directive(s) live at the top level (the TestBench), never inside a define block
analysis SP   type=sparam  start=1 GHz stop=10 GHz step=1 GHz
; measurement expressions (evaluated on results)
measure InsertionLoss = dB(S(2,1))
```

Line shapes: a comment (`;`), a variable assignment (`name = expression [unit]`), a cell definition block (`define … parameters … end`), a primitive line (`Type:Instance net… param=value [unit] …`), a cell-instance line (`CellName:Instance net… override=value …`), an analysis directive (`analysis Name type=… …`), or a measurement (`measure Name = expression`). Analysis and measurement directives belong to the top-level `TestBench`, never inside a `define … end` block — the same rule the prototype netlists follow by construction.

---

## 11. Mapping summary (design → elaboration → numeric)

| Concept | Design layer | Elaboration layer | Numeric layer |
|---|---|---|---|
| The reusable block | `Cell` | (flattened away) | — |
| The thing you run | `TestBench` (own `Instances` + `Analysis` list) | drives elaboration + per-analysis solves | — |
| A placed part | `Instance` | `ElaboratedComponent` | `ComponentModel` |
| Connectivity | `NetBindings` (names) | `NodeMap` (indices) | sparse matrix rows/cols |
| A value | `ParameterDeclaration`/`Assignment` (expression) | resolved `Real`/`Complex` value (kinded) | scalar used in `Stamp`/`Evaluate` |
| A name you can sweep | `Variable` / cell parameter | resolved per scope | a sweep axis on the result cubes |
| A performance number | `Measurement` (expression) | — | named measurement cube in the `DataSet` |
| Results | — | — | a **`DataSet`** of named **`DataCube`s** (in RfCore) |

---

## 12. Open items (resolved in sibling notes, flagged here)

- **Sparse MNA stamping & DC handling** — the model-contributes / engine-owns stamping API, fill-reducing node ordering, and a clean DC formulation (replacing ad-hoc large/small-value fudge factors). → `linear-engine.md`.
- **`Evaluate` → Jacobian** — how `(i, q, dg, dc)` assemble the conversion-matrix Jacobian; FFT/sign conventions. → `harmonic-balance.md`.
- **Expression grammar & AD/FD** — full precedence table, the AD scheme, and the FD fallback behind `dg`/`dc`. → `expressions.md`.
- **Measurements** — cube-operand expression extensions, built-in measurement library, IMn extraction, `user_results` keying. → `measurements.md`.
- **`DataSet`/`DataCube` specifics** — axis/units representation, backing store, slicing/reduction, `.mat`/`.npy` export shape (per-cube vs whole-DataSet), the V-and-I retention requirement. → data-cube note + `src/Core/Data/CLAUDE.md`.
- **Net extraction** — schematic geometry → logical nets, the Phase-6 bridge from the wiring layer to the design model. → a Phase-6 UI note.
- **Mutual coupling** — exact representation linking a `MutualInductanceModel` to its two inductor instances after flattening. → `linear-engine.md`.

---

*On approval, the implementation order is: the expression engine and elaboration (Phase 1, with the `.cnl` reader and cycle-detection tests on the committed fixtures), then the linear engine (Phase 2). No C# is written before this document is signed off.*
