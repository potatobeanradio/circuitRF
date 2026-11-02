# Brief MC-26 — A composed FET as Verilog-A source: the writer and the static core

**Series:** `brief-modelcomposer-0-overview.md` (D17, D18) · **Tag:** `R-mc26-<m>` · **Depends on:** MC-1, MC-2, MC-7,
MC-8, MC-16
**Area:** a new `src/Design/Modeling/VerilogA/` (`CmodelVerilogA` and its emitters); `tests/Ui.Tests/ModelComposer/VerilogA/`
and `tests/Core.Tests/Composed/`; `docs/design/model-composer.md` (a new section)
**Read first:** `docs/design/model-composer.md` §3 (the `.cmodel`), §4.1–§4.10 (the device, the evaluators, guided
extrapolation, symmetry); `ComposedCoreModel`'s class remarks; `ComposedCoreProgram`; `RuntimeEvaluators.cs` (the ANN);
`GuidedExtrapolation.cs` (`DomainGeometry.Project` especially); `DriftCharges`; `ComposedNetworkTemplate.Synthesize`;
`docs/user/src/reference/veriloga.md` (how circuitRF runs a `.va`, and its thermal terminal)

---

## 1. Goal

A model built in the Model Composer runs today only in circuitRF. The owner wants it to run in **other simulators**
too. To do that, export the same model as **Verilog-A source**: one self-contained `.va` module that carries the
extrinsic network, the core, and every parameter and switch a placed `ComposedFET` has. circuitRF already compiles and
runs Verilog-A, so the export can be checked here: the `.va` placed as a `VerilogA` component must give the same answers
as the `.cmodel` placed as a `ComposedFET`.

The series is three briefs:
- **MC-26 (this one)** builds the writer and everything that has no state of its own: the network, equation and ANN
  constituents, guided extrapolation, the asinh gate current, drain–source symmetry, drift charges, periphery scaling
  and temperature without self-heating.
- **MC-27** adds the internal state nodes (dispersion, traps), self-heating and all ten switches, including the
  linear-charge switches with their explicit bias point.
- **MC-28** adds the Export step button, the `model export` CLI and MCP path, the user page and the acceptance run on
  the shipped example.

**Tables are not exported (D17).** A model with any table constituent is refused, by name. The Verilog-A compiler
circuitRF uses accepts neither arrays nor `$table_model`, and `$table_model`'s interpolation is not circuitRF's
not-a-knot spline in any case. Every other method is exported, and **must be exact**: equations and ANNs are written
as code, so the `.va` computes the same numbers as the `.cmodel` to rounding.

## 2. Requirements

**R-mc26-1 — One function, one module, deterministic bytes.** `CmodelVerilogA.Write(ComposedModel model, VerilogAOptions
options) → string` and `CmodelVerilogA.Export(string cmodelPath, string vaPath, VerilogAOptions?, RunControl?)` in
`src/Design/Modeling/VerilogA/`. It takes the `ComposedModel` that `CmodelFile.Read` returns, so a `.cmodel` alone (with
no `.cmc`) can be exported. `CmodelExport.Assemble`'s in-memory model goes through the same `Write`, which is how MC-28's
window and CLI export straight from a `.cmc`.
- **One module.** `module <name>(g, d, s)`, or `(g, d, s, t)` for a self-heating model (MC-27). `<name>` is the
  `.cmodel`'s file name made into a legal identifier: a non-identifier character becomes `_` and a leading digit gets
  a `m_` prefix. `options.ModuleName` overrides it, and an illegal override is refused by name.
- **Deterministic.** The same model gives the same bytes. Write every double in shortest round-trip form, invariant
  culture, with an explicit exponent where one is needed (`1e-08`, never a locale comma), `\n` line ends and no
  timestamp of the writer's own. The header records the model's `Provenance.Created` and version and a SHA-256 of
  `model.json` as the `.cmodel` holds it.
- **Write by a temporary file and a rename**, as `CmodelExport.Export` does, so a refusal, failure or cancellation
  leaves no file.
- **Includes:** `disciplines.vams` and `constants.vams` only. Nothing else is `` `include ``d, so the file is self-contained.
- **Readable.** The header comment lists every module parameter with its unit, default and meaning, and every
  switch with its meaning. That lets someone using another simulator understand the file with nothing else to hand.
  Section comments name the `.cmodel` field each block came from (`// Core.Constituents.Id (ANN, 3 hidden layers)`).

**R-mc26-2 — The refusals.** Each is a `VerilogAExportException` whose message begins with the field, as
`CmodelFormatException`'s does. All of them are collected and reported in one message, not stopped at the first.
- A **table** constituent: `Core.Constituents.Id.Method: 'Table' cannot be written as Verilog-A; refit Id as an ANN or an
  equation to export it.` Name every table constituent, `IdRF` included.
- An extrinsic-network element the writer has no translation for (R-mc26-4), by element name and type.
- An equation function outside the set in R-mc26-5, by name. That covers any function added to the expression engine
  later, which then needs its own row here.
- A trap model with no Verilog-A emitter (MC-27). The trap registry is extensible, so a model a developer adds without
  an emitter is refused by name rather than written wrong.
- A **name collision**. Many simulators read netlist parameter names without regard to case, so two module parameters
  that differ only in case are refused, naming both. So is a parameter or network name that is a Verilog-A keyword or
  built-in function (`real`, `begin`, `exp`, `ddt`, …), with the list taken from the language reference. Renaming
  silently would change names the user typed in the network. §5 records this as open.

**R-mc26-3 — Module parameters (the `.cmodel`'s parameters, D18).** Each is a `parameter real` (or `integer`) with a
`from` range wherever circuitRF refuses a value, so the host refuses the same values:

| Parameter | Default | Meaning |
|---|---|---|
| `N` | `N0` | Finger count. `from [1:inf)`, and an `integer`, as circuitRF requires a whole number |
| `Temp` | unset (sentinel) | The device's temperature in °C. Unset means the simulator's `$temperature`. Clamped to `[Tmin, Tmax]` exactly as `ComposedCoreModel` clamps |
| every **network variable** | its value in the `.cmodel` | The six-pin network's element values, in base SI, before periphery scaling. The scaling rule is applied in the code |
| every **equation constituent parameter** | its fitted value | Prefixed by the constituent (`Id_beta`) so two constituents' parameters cannot collide |
| `Gx_Id`, `Gx_IgGs`, `Gx_IgGd` (when present) | `Extrapolation.Gx` | Guided extrapolation's conductances |

ANN weights and normalisations are **literals in the code, not parameters**. A 4 × 64 network alone holds about
12,700 numbers. Nobody would edit a parameter list that long, and some hosts cap the parameter count. The multiplier is the **host's** (`m`/`$mfactor`): the
module declares no `m`, so `m` is never applied twice. `File` and `Symbol` have no meaning in a `.va` and are not
written. The switches, `Rth`, `Cth` and the bias-point parameters are MC-27's.

**R-mc26-4 — The extrinsic network.** Mirror `ComposedNetworkTemplate.Synthesize`. Pins 1–3 are the module's `g d s`,
pins 4–6 are internal nodes `gi di si`, and every network net is an internal node. Each variable is multiplied by its
`PeripheryScaling` rule's factor at `N`. Translations:
- `R` → `I(a,b) <+ V(a,b)/R`, written so that a zero resistance is refused at the parameter (`from (0:inf)`) rather
  than dividing by zero;
- `C` → `I(a,b) <+ ddt(C*V(a,b))`;
- `L` → a named branch, `V(br) <+ L*ddt(I(br))`;
- `Mutual` (`Inductor1`, `Inductor2`, `M`) → the two inductor branches' flux equations coupled,
  `V(br1) <+ L1*ddt(I(br1)) + M*ddt(I(br2))`, and the same for the second branch. Under periphery scaling `M` takes
  the rule its variable carries;
- `Short` → `V(a,b) <+ 0`.

Anything else is refused (R-mc26-2). The 1 µΩ pass-through `ChargeFit` writes for a model with no network is written
like any other resistor, so that case needs no special handling.

**R-mc26-5 — Equation constituents.** Translate from the expression engine's AST, never from the text. The function
set is the one `SddCompiled` accepts, and three of the mappings are **traps**:
- `log` and `ln` are both the **natural** logarithm in circuitRF, but Verilog-A's `log` is **base 10**: write `ln(…)`.
  circuitRF's `log10` → Verilog-A `log`.
- `if(c, a, b)` → `((c) ? (a) : (b))`; `^` → `pow`; `sign(x)` → `((x) > 0 ? 1 : ((x) < 0 ? -1 : 0))`.
- A literal carrying a unit is written in base SI, resolved by `Units`. No unit token ever reaches the `.va`.

`exp sqrt tanh sin cos abs pow sinh cosh tan min max atan atan2 asin acos` map by name. A table-driven test holds
**every** function in the set, so a function added to the engine without a row here fails a test instead of being
silently mistranslated.

**R-mc26-6 — Value and gradient, emitted.** MC-27's linear-charge switches need `C = ∂(Qg, Qd)/∂(vgs, vds)` at a point
the circuit is not at, and guided extrapolation needs `∇f` at the boundary point. Verilog-A's automatic derivatives give
neither: they differentiate at the current operating point only. So every constituent is emitted as a **function body
that writes its value and its gradient into named variables**:
- an **equation** by source-to-source forward-mode differentiation of its AST. Each node writes `v` and `dv/d<input>`
  per input. This is written in-house; check it against the engine's own dual-number evaluation (`EvalDualInto`) at
  random points;
- an **ANN** by its derivative network, as `AnnEvaluator` carries it (`Tanh`: `h' = (1 − h²)·a'`; `Sigmoid` exactly as
  the evaluator defines it). Write it neuron by neuron, with the input normalisation and the output de-normalisation
  applied as in the evaluator. Verilog-A has no arrays, so every neuron is a scalar variable;
- the **asinh transform** (`I0·sinh(u)`) and **drift charges** by the chain rule, as `AsinhOutput` and `DriftCharges`
  form them.

The compiler then differentiates that emitted code again where the circuit needs it. That is exactly what makes the
continuation's derivative right, because the boundary point moves with the operating point.

**Each constituent is evaluated at more than one point** (the operating point, the boundary point, and in MC-27 the
bias point and the quadrature nodes). Without arrays or user functions shared across points, emit each evaluation as
its own copy of the code with suffixed variable names. **Check whether the compiler accepts `analog function`s with
`output` arguments first.** If it does, one function per constituent returning value and gradient is far smaller, and
should be used. Record the answer in the design note either way.

**R-mc26-7 — Guided extrapolation, exactly.** Port `DomainGeometry.Project` and `GuidedExtrapolation.Evaluate` to
straight-line code. Use the same normalisation (`u = (p − centre)/extent`), the polygon vertices as literals in
counter-clockwise order, the inside test, and the nearest-edge search **in the same order with the same strict `<`
tie-break**. The box clamp applies on each other input. The continuation is `f(p_b) + ∇f(p_b)·(p − p_b)`, and for a
current `+ (Gx/0.05)·s·d` with `s` on the constituent's own branch (`vds` for Id, `vgs` for IgGs, `vgs − vds` for IgGd).
Write `f(p)` itself as that continuation evaluated at `p_b`. Inside the domain `p_b = p`, so the same code gives the
evaluator's value there with no branch on "inside". The compiler then derives `∇f(p_b) + Pᵀ H (p − p_b)` on its own,
which is the gradient circuitRF forms by hand. Charges stop at the linear term, as in circuitRF. Equations are not
extrapolated.

**R-mc26-8 — The core.** As `ComposedCoreModel` writes it, in the terminal frame:
- `vgs = V(gi,si)`, `vds = V(di,si)`. The gate carries `IgGs + IgGd`. The drain carries `Id − IgGd`. The charges are
  `Qg` on (gi, si) and `Qd` on (di, si), contributed as `ddt` of the charge, so the transcapacitance is kept exactly as
  the charge function gives it. Everything is multiplied by `N/N0`.
- **Exchange symmetry** (`vds < 0`): evaluate at the image `(vgs − vds, −vds)` and map back as §4.10 states, gradient
  included.
- **Temperature, no self-heating:** a `Single` model has no `T` input. When the device's temperature differs from
  `T0`, write one `$warning` at `@(initial_step)` naming both. A `Range` model reads the clamped temperature, with one
  `$warning` at `@(initial_step)` when it clamps.
- **Non-finite values.** circuitRF refuses a NaN or infinite value by name. In the `.va`, a constituent that evaluates
  non-finite raises `$fatal` naming the constituent and the operating point. Verify that the worker reports a model's
  `$fatal` as a refusal with that text and not as a crashed worker. If it does not, record the gap and fall back to
  `$error`.

**R-mc26-9 — Design note.** Add a new section to `docs/design/model-composer.md`, **Verilog-A export**: the module
layout, the parameter table, the value-and-gradient emission and why automatic derivatives cannot replace it, the
extrapolation port, the three function-mapping traps, the table refusal and its reason, the `analog function` finding,
and code size and compile time measured on the shipped example and on a maximum-size ANN (4 × 64). The prototype
compiled a 4 × 64 tanh network with a thermal node in about 10 s.

## 3. Not in this phase
Dispersion, traps, self-heating and every switch (MC-27). The window, the CLI and the docs page (MC-28). Exporting a
table constituent, in any form (D17). A passive model, which already exports as a cell.

## 4. Gates
Two kinds. **Text-level** gates need nothing installed. **Compiled** gates run the `.va` through the real compiler and
circuitRF's `VerilogA` component. Give the compiled gates a `CompilerFact` attribute modelled on `FixtureFact`: it skips
**with a reason** when no Verilog-A compiler or device worker is reachable. The owner runs them on the Mac, so they must
actually run there and not just skip. Use `SyntheticGan`/`SyntheticFetData` for the truth. Build small `.cmodel`s in
memory for the feature cases, never from owner data.

Text-level:
- `CmodelVerilogAWriterTests`: same model, same bytes (twice, and across a write → read → write of the `.cmodel`). Each
  refusal in R-mc26-2, by its sentence, including two tables reported in one message.
- `EquationTranslationTests`: every function in the set, plus `log`/`ln`/`log10`, `if`, `^`, `sign` and a unit-carrying
  literal. Each case is checked by evaluating the translated text's semantics against the engine. The emitted
  forward-mode gradient equals `EvalDualInto`'s at 50 seeded points to 1e-14 relative.

Compiled, each comparing the `.va` placed as `VerilogA` with the same `.cmodel` placed as `ComposedFET`, on one bench
with the same bias network:
- **DC I–V** over a grid that reaches **outside** the training polygon on every side, so guided extrapolation is
  exercised: `Id`, `Ig` agree to 1e-9 relative (or 1e-15 A absolute, whichever is larger).
- **S-parameters** at three biases, 1–20 GHz: `|ΔS|` ≤ 1e-9.
- **One HB power sweep** to 3 dB compression: Pout and gain within 0.001 dB, PAE within 0.01 pp.
- Each of these for: an all-equation model; an all-ANN model (`Tanh` and a `Sigmoid` variant); the asinh gate current;
  `Exchange` symmetry swept through `vds = 0`; drift charges; a `Range` model at two temperatures with one beyond the
  range (clamped, warned); `N ≠ N0` with every scaling rule; `m = 2`, which also shows the host's multiplier and
  circuitRF's agree on the inductors.
- One maximum-size ANN (4 × 64) compiles. Print its compile time to the test output; do not assert it.

Run only these classes, the existing `Composed` and `ModelComposer` classes, and `Firewall.Tests`.

## 5. Open for the owner
- **A name that clashes in case or with a Verilog-A keyword is refused, not renamed.** Renaming (`rg` → `rg_1`) would
  export every model but change names the user chose and will look for in the other simulator. Refusing is the default
  here.
