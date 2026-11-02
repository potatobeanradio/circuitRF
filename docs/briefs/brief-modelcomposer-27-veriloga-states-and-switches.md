# Brief MC-27 — Verilog-A export: state nodes, self-heating and every switch

**Series:** `brief-modelcomposer-0-overview.md` (D17, D18) · **Tag:** `R-mc27-<m>` · **Depends on:** MC-26, MC-9, MC-10,
MC-19, MC-21, MC-22
**Area:** `src/Design/Modeling/VerilogA/`; `tests/Ui.Tests/ModelComposer/VerilogA/`; `docs/design/model-composer.md`
(the Verilog-A section MC-26 opened)
**Read first:** MC-26 (the writer, the value-and-gradient emission); `ComposedCoreModel`'s class remarks
(dispersion, traps, self-heating, switches, branch switches); `CaptureEmissionTraps`, `CustomTraps`,
`RateTrapDynamics`, `TrapShift`; `BranchLinearisation`; `ComposedSwitches`; `Elaborator.ComposedFetCell` (the `Rth`,
`Cth` and `Temp` instance overrides); `ComposedNetworkTemplate` (the thermal network and its ladder scaling);
`docs/user/src/reference/veriloga.md` §terminals

---

## 1. Goal

MC-26 writes everything that has no state of its own. This brief finishes the model so that **every parameter and
every switch a placed `ComposedFET` has is a parameter of the `.va` module**, and each one does the same thing. That
covers the dispersion node, the trap states, self-heating with its optional thermal pin, the instance `Rth`, `Cth` and
`Temp`, and all ten switches.

Nine switches carry over exactly. The other five — `LinearC` and the four branch switches — need the **bias point**.
In circuitRF the engine solves DC first and freezes the charge there (MC-21 R-mc21-3). A Verilog-A module cannot ask
its host for a quiescent operating point. So, by the owner's choice (D18), **the bias point is given as instance
parameters**. With those parameters set to the point circuitRF froze at, the `.va` must agree with the `ComposedFET`.

## 2. Requirements

**R-mc27-1 — Dispersion.** A model with `IdRF` gets an internal electrical node `x`, whose voltage is a current in
amps, exactly as §4.2 states: `I(x) <+ V(x) − (IdDC − IdRF) + ddt(τ·V(x))`. The drain carries `IdRF + V(x) − IgGd`.
Both currents are taken in the terminal frame, after any drain–source image. `x` is unscaled; `N/N0` multiplies the
drain current it feeds. A drift charge reads `IdRF`, as fitted.

**R-mc27-2 — Traps.** One internal electrical node per enabled state, in the model's order, named for the state
(`phi1`, `phi2`, …). The node carries `RateTrapDynamics`' form exactly: `I(phi) <+ ddt(1e-3·V(phi)) − 1e-3·Rate`, using
`RateTrapDynamics.NodeCapacitance` by name, not a copied literal. The states read the intrinsic `vgs`, `vds` (never
their image, which a model with traps does not have) and `T`.
- **Emitters are per trap model**, behind a small interface beside `TrapModelDefinition` (for example
  `ITrapVerilogA`), so a model a developer adds is either given an emitter or refused by name (MC-26 R-mc26-2).
- **`CaptureEmission`:** both states, the capture exponential **continued by its tangent beyond `ExpLimit`** (use the
  constant), and thermally activated emission when `Ea` is present (T in kelvin, `Tref` in °C, as the class states).
  `TauCapt1/2`, `TauEmit1/2`, `Ea1/2`, `Xref`, `Vc` and `Tref` are module parameters with the fitted values as
  defaults; `Ea` is written only when the model has it.
- **`Custom`:** the `Dynamics` and `Dc` expressions through MC-26's equation translator. Its parameters become module
  parameters, prefixed `Trap_`.
- **`Shift` coupling** (MC-19): `Id` is evaluated at the shifted gate voltage that `TrapShift.Delta` forms,
  `ΔV = Σ aₖ·sₖ·(φₖ − φₖ,DC)`. `ShiftDrain` and `ShiftGate` are module parameters.

**R-mc27-3 — Self-heating and the thermal pin.** A model with `Thermal` gets a fourth terminal `t` of the `thermal`
discipline, and the core is evaluated at `T = Tamb + ΔT`, where `ΔT` is the thermal node's temperature. The node
receives the dissipated power `ig·vgs + id·vds` from the scaled terminal currents, as §4.2 forms it.
- **The thermal network lives on an internal thermal node `ti`, never on the terminal.** When `t` is connected
  (`$port_connected(t)`), `t` and `ti` are joined by a zero-temperature-difference branch. When it is not connected,
  `t` is left alone. The reason: a host commonly grounds an unconnected terminal (circuitRF's own worker does, see
  `veriloga.md`). A network written on `t` would then be shorted to 0 K and the model would silently stop self-heating.
  The compiler accepted this construction when it was prototyped; the gate checks that it behaves correctly.
- `Rth ∥ Cth`, or a Foster ladder of up to five sections in series, from `ti` to thermal ground. Each section gets
  internal nodes as `ComposedNetworkTemplate` names them (`t_1` …). Scale as it scales: `Rth × N0/N`, `Cth × N/N0`, a
  ladder by the one factor that keeps its sum. `RthSlope` with `Tref` is taken at the clamped ambient.
- **Instance `Rth`, `Cth`, `Temp`** are module parameters with the meaning `Elaborator.ComposedFetCell` gives them, for
  one pole and for a ladder: unset means the model's scaled value, and a value means this device's own, at its finger
  count. Mirror the elaborator's refusals as `from` ranges.

**R-mc27-4 — The nine exact switches.** `integer` parameters, `from [0:1]`, with the defaults `ComposedSwitches.DefaultOf`
gives, written in `ComposedSwitches.Names` order. Each does what `ComposedCoreModel` does:
- `Charge = 0`: both intrinsic charges are zero. The dispersion and trap nodes' own charges stay.
- `SelfHeating = 0`: evaluate at the ambient and put no power into `ti`. The thermal network and the pin stay, so a
  wired pin 4 sits at a 0 K rise.
- `Dispersion = 0`: the drain carries `IdDC`. The `x` node is tied to zero (`V(x) <+ 0`) so it carries nothing; a
  module cannot leave a node out depending on a parameter.
- `GateLag = 0` / `DrainLag = 0`: every state with that `CaptureSign` takes `DcValue(vgs, vds, T)` at the instantaneous
  voltages, and its node is tied to zero. The current then reads the DC value directly. Because that DC value is written
  as code, the compiler's derivatives fold the state's lane into `vgs`, `vds` and `T` on their own. Under `Shift`, that
  state's shift term is then exactly zero. `Custom`'s `Dc` expression goes through the translator.
- A switch naming a part the model lacks still exists as a parameter and changes nothing (the same principle as MC-21
  R-mc21-2). That keeps every export's parameter list the same, and the header comment says which switches do nothing
  for this model.

**R-mc27-5 — The bias point (D18) and the five linear-charge switches.** `LinearC`, `LinearCgs`, `LinearCdg`,
`LinearCds` and `LinearCm` are `integer` parameters as above. Three more parameters give the point they freeze at:

| Parameter | Default | Meaning |
|---|---|---|
| `VgsQ` | unset | The **intrinsic** gate–source voltage at the bias point, V |
| `VdsQ` | unset | The **intrinsic** drain–source voltage at the bias point, V |
| `TjQ` | unset → the device's temperature | The junction temperature at the bias point, °C. For a self-heating device, the ambient plus the DC rise |

- **The frozen charge is circuitRF's, from these numbers.** The trap states at the bias point are
  `DcValue(VgsQ, VdsQ, TjQ)`, and the charge's temperature and trap inputs are held at `TjQ` and those states.
  - `LinearC`: `q = q(v_Q) + C(v_Q)·(v − v_Q)` with `C` from MC-26's emitted gradient. Under `Exchange` it is in the
    terminal frame.
  - The branch switches: `BranchLinearisation`'s construction exactly. That is the residual `r_b` per frozen branch,
    with the line integral by the same `Nodes`-point Gauss–Legendre rule. Take the nodes and weights from
    `BranchLinearisation` and do not retype them. `C_b` at each node comes from the emitted gradient, and the compiler
    supplies `∇C_b` by differentiating it. That is the exact path circuitRF uses where a surface has second
    derivatives.
  - `LinearC` stands for all four, keeps its own exact tangent path, and `Charge = 0` wins over both, as in
    `ComposedSwitches.FrozenBranches`.
- **Unset is a refusal, not a guess** — the counterpart of circuitRF's unfrozen refusal. If any linear-charge switch is
  on and `VgsQ` or `VdsQ` is unset, the module raises `$fatal` at `@(initial_step)`. The message names the instance's
  switch and both parameters, and says they are the intrinsic voltages at the bias point. A default bias would be a
  number nobody chose, and it would give a plausible wrong answer. Use a sentinel default that no voltage can take, and
  write it in the header comment.
- **`TjQ` unset on a self-heating device with `SelfHeating = 1`** is allowed, but writes one `$warning` at
  `@(initial_step)` saying the charge is frozen at the ambient and not at the junction temperature.
- **The voltages are intrinsic.** A designer in another simulator knows the terminal bias. The intrinsic voltages differ
  from it by the drops on the network's resistances. The header comment says so, and the user page (MC-28) says how to
  read them: from a DC solve in circuitRF (`V(X1.gi, X1.si)`, `V(X1.di, X1.si)` and the thermal node), or from the other
  simulator's own operating point of the internal nodes.

**R-mc27-6 — Design note.** Extend the Verilog-A section: the state nodes and their scaling, the thermal pin
construction and why the network sits on `ti`, the switch table with what each writes, the bias-point parameters and
the unset refusal, and the difference from circuitRF, which finds the point itself.

## 3. Not in this phase
The window, the CLI, the MCP tool and the user page (MC-28). A bias point found by the module itself — for example a
slow internal node tracking the DC voltage. It was considered and not chosen (D18): under drive it freezes at the
average voltage, not the quiescent one.

## 4. Gates
Compiled gates use MC-26's `CompilerFact`. Each compares the `.va` placed as `VerilogA` with the `.cmodel` placed as
`ComposedFET` on one bench. Tolerances are MC-26's: DC 1e-9 relative, `|ΔS|` ≤ 1e-9, HB Pout and gain 0.001 dB, PAE
0.01 pp.
- `VerilogAStateNodeTests`:
  - dispersion: S at one bias, `|ΔS|` ≤ 1e-9 at 10 kHz and at 10 GHz, which are the two sides of `1/τ`.
  - `CaptureEmission` with each state alone and both together, `Ea` present and absent.
  - a `Custom` model.
  - the `Shift` coupling.
  - DC plus one HB point for each of the above.
- `VerilogAThermalTests`:
  - one pole and a three-section ladder, `RthSlope`, and instance `Rth`/`Cth`/`Temp`. DC at a dissipating bias:
    `Id` and the thermal node's rise agree.
  - **The pin unconnected (three-pin placement) still self-heats.** This is the one that catches a network written on
    the terminal.
  - The pin wired to an external `Rth` agrees with the `ComposedFET` given the same pin-4 network.
- `VerilogASwitchTests`: each of the nine exact switches alone, plus `GateLag = 0` with `DrainLag = 0`. DC, S at one
  bias and one HB point each. A switch on a model lacking its part changes nothing.
- `VerilogALinearChargeTests`: read the point circuitRF's freeze settled on (`BiasFreeze`'s record) and pass it as
  `VgsQ`, `VdsQ` and `TjQ`. Then, for `LinearC` and each branch switch alone, plus `LinearCgs` with `LinearCds`: S at
  the bias point, `|ΔS|` ≤ 1e-9, and an HB sweep to compression within 0.001 dB. With a switch on and `VgsQ` unset, the
  run is refused with the `$fatal` sentence. With a self-heating model and `TjQ` unset, the warning appears once.
- Text-level: every module parameter appears in the header comment, and the switch list is `ComposedSwitches.Names`
  in order. That catches a switch added to circuitRF without one here.

Run only these classes, MC-26's, the existing `Composed` and `ModelComposer` classes, and `Firewall.Tests`.

## 5. Open for the owner
- **`TjQ` unset means the device's temperature, with a warning.** The alternative is to solve the junction temperature
  at `VgsQ`, `VdsQ` inside the module by fixed-point iteration. That needs nothing more from the user, but it costs a
  loop of current evaluations on every call. Most hosts give a module no portable way to evaluate it once and keep the
  result.
