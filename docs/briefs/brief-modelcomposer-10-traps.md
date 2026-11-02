# Brief MC-10 — Traps: the contract, the capture/emission model, the custom model

**Series:** `brief-modelcomposer-0-overview.md` (D4, D10, D13) · **Tag:** `R-mc10-<m>` · **Depends on:** MC-7 (MC-9 for thermal coupling)
**Area:** `src/Core/Devices/Composed/Traps/` (the contract, the registry, the built-in, the custom model, the
internal nodes); `src/Design/Modeling/` (trap-state training data, the trap step)
**Read first:** [P1] §6.6.4 (Fig. 6.37, eqs. 6.70–6.74), §6.7.2 (eq. 6.75, Fig. 6.45), §6.7.3; **[X10]**
(eqs. 1.1–1.9: the dynamical equations, trap states as waveform extremes, `Qd` trap-dependent, `Ig`/`Qg` not);
**[X14]** (training on DC, S-parameters and waveforms together); **[N22]** (the thermally activated emission;
double-pulse trap state); **[G19]** (capture is not always fast; the double-pulse pre-pulse); [P21]

---

## 1. Goal

A trap model is a **user setting** with a simple built-in, a user-written alternative, and a contract that lets
future developers and users add better ones without touching anything else. Pulsed data — double-pulse or
ordinary — trains the currents' dependence on the trap states. Less data gives a more limited model, never a
refusal to build one.

## 2. Requirements

**R-mc10-1 — The contract.** A trap model declares:
- its **name**;
- its **state variables** (count and names, e.g. `phi1`, `phi2`; at most 4);
- its **parameters**, with units;
- for each state, its **dynamics** as an internal-node current and charge `(i, q)` as functions of the state, the
  intrinsic voltages and `T`, with exact derivatives;
- the state's value at a **DC operating point**.

Models are registered by name. A `.cmodel` `Traps` block is `{ Model, Parameters, Enabled states, Trained levels
per state }`; an unknown model name is a refusal that names it (MC-1 R-mc1-2). The states enter **`Id`**, and
**`Qd`** when the data supports it (R-mc10-5), as the inputs `phi1…`; never as inputs of `Ig` or `Qg` ([X10]
eq. 1.5; D10). With MC-8's drift charge, `Qg` depends on them through `Id` (MC-8 R-mc8-3).

**R-mc10-2 — The built-in `CaptureEmission`** ([P1] eqs. 6.73–6.74), with two independently switchable states.
- **Gate-lag `phi1`** relaxes towards `vgs` with `τ_emit,1` always, and is additionally captured through `f`
  whenever `vgs < phi1`.
- **Drain-lag `phi2`** relaxes towards `vds` with `τ_emit,2` always, and is additionally captured through `f`
  whenever `vds > phi2`.
- The one-sided capture term is the diode-like `f` of [P1] eqs. 6.73–6.74 and [X10] eqs. 1.3–1.4, of the gap `x`
  (`phi1 − vgs`, or `vds − phi2`). Requirements:
  - C∞-smooth, never a hard switch, so Newton sees smooth derivatives;
  - **`f(0) = 0`**, so the DC state is exactly `v`;
  - negligible for a negative gap (far below `|x|/τ_emit`), so emission is undisturbed;
  - capture slows as the gap closes — the fast-then-slow capture [G19] measures.

  For example `f(x) = (x_ref/τ_capt)·(e^((x − x_ref)/Vc) − e^(−x_ref/Vc))`: `τ_capt` is the capture time at the
  reference gap `x_ref` (default 1 V), and `Vc` (default 50 mV) sets how fast it slows — with the defaults, from
  10 ns at a 1 V gap to about 0.1 ms at 0.5 V. An unshifted softplus such as `(x + √(x² + ε²))/2` fails the second
  requirement: it is `ε/2` at zero and parks the DC state about `ε·√(τ_emit/τ_capt)/2` away from `v`.
- Each state is an internal node with `q = C·phi` (normalised `C`, so the node voltage **is** `phi` in volts),
  through MC-1's seam.
- At DC, `phi1 = vgs` and `phi2 = vds`.
- Under CW with `τ_emit ≫ 1/f` and `τ_emit ≫ τ_capt`, HB settles `phi1` near `min vgs(t)` and `phi2` near
  `max vds(t)` — [P1] eq. 6.75 — short of the extreme by the gap at which a cycle's capture balances a cycle's
  emission (a few tenths of a volt with the defaults). Capture need not finish within one RF period; [X10]'s
  `τ_capt ≪ 1/f` is sufficient, not necessary.
- **Emission time constant:** `Constant` (default) or `ThermallyActivated` — `τ_emit(T)` with
  `1/τ_emit ∝ T²·exp(−Ea/kT)` ([N22] eq. 3), set by `Ea` and `τ_emit` at a reference temperature. `T` here is
  in **kelvin**, although the series states temperatures in °C everywhere else. `T` is the
  junction temperature when self-heating exists (MC-9), else the ambient. The capture time constant stays fixed:
  [N22] finds its temperature dependence negligible for PA prediction.
- Defaults: `τ_capt = 10 ns`, `τ_emit = 1 ms`; drain-lag on and gate-lag off when the setting is first chosen.
  [G19] measures capture from below a microsecond to tens of milliseconds; the diode-like `f` spans that from one
  time constant. The Shockley–Read–Hall form of [G19] eqs. 2–5 is a future built-in under the same contract.
- The step states once, in its tooltip, that CW HB depends on the time constants only weakly, through their ratio.
  Two-tone spacing near `1/τ_emit` does depend on them.

**R-mc10-3 — The `Custom` model.** Per state:
- a **dynamics** expression `dphi/dt = …` in the one expression engine, over the state, `vgs`, `vds`, `T` and named
  parameters;
- a **DC value** expression.

Derivatives come by the engine's AD. The step checks that the expressions reference only those names, and that
the DC expression is a fixed point of the dynamics (to 1e-9) at sample biases. This is how users add their own
— for example the thermally activated emission of [P21]/[N22], where the emission **rate** `1/τ_emit` follows
`T²·e^(−Ea/kT)` with `T` in kelvin — without
a code change. A developer adds a built-in by implementing the same contract and registering a name.

**R-mc10-4 — Trap-state training data.** The quiescent point `(Vgs,q, Vds,q)` is re-referenced to intrinsic like
any point (MC-4; its currents are a condition, and a dissipating quiescent point with no recorded current is a
refusal), and so is a `PrePulse` level. A data set's **trap-setting bias** `(Vgs,s, Vds,s)` is its `PrePulse`
level when it has one (double pulse, [N22], [G19]) and its quiescent point otherwise (ordinary pulsed data). For
each enabled state, a pulsed point is:
- **toward** the trap-setting bias when it is pulsed no lower in `vgs` (gate-lag) and no higher in `vds`
  (drain-lag): `phi = (Vgs,s, Vds,s)`, exactly. No capture acts during such a pulse, only emission, which is slow,
  so the mapping needs neither a double pulse nor a capture time constant.
- **beyond** it otherwise. Its state lies between the trap-setting bias and the pulse's extreme, depending on how
  far capture got before the sample — which [G19] shows can take anything from under a microsecond to tens of
  milliseconds. The data set's `TrapState` condition (MC-3) decides:
  - **`auto`** (default): validation only — left out of the trap fit, drawn in the plots, and counted in one line
    ("drain-lag: 380 points beyond the trap-setting bias, validation only");
  - **`setting`**: `phi = (Vgs,s, Vds,s)` — the user asserts the sample precedes capture (a cold-pulsed set used
    as trap-free, say);
  - **`extreme`**: `phi1 = min(Vgs,s, vgs)`, `phi2 = max(Vds,s, vds)` — [X10] eqs. 1.8–1.9 applied to the pulse;
    the user asserts capture finished before the sample.
- The step warns when `SampleDelay`, the pulse width or `PrePulse.Gap` is not ≪ `τ_emit`, since emission then
  moves the state during the measurement.
- DC points map to `phi = (vgs, vds)`.
- **What the data allows, per enabled state**, counting the distinct trap-setting levels among the points used
  (toward points, and beyond points the user has mapped):
  - **≥ 2 levels** — the full fit.
  - **1 level** — the fit runs, with one warning ("drain-lag from one trap level, 28 V"). The state's effect is
    known between the DC points and that level; beyond it, the trap input extrapolates linearly (MC-2's box
    rule). The level is recorded in the `.cmodel` (R-mc10-1), and `model explain` reports it.
  - **0 levels** (no pulsed data, or every pulsed point beyond and left at `auto`) — the state cannot be enabled,
    and the refusal names the data it needs. The model is still built, without that state; MC-7's dispersion
    option and MC-9's self-heating remain the routes to DC/RF dispersion.

**R-mc10-5 — Fitting with traps.**
- `Id` re-trains with the enabled states as extra inputs, by any of the three methods, using MC-6's **combined**
  mode ([X03] §IV-C; the frozen-state small-signal model of [X10] §VI). Pulsed I–V and pulsed S stand in for the
  large-signal waveforms [X10] and [X14] train on; [P1] §6.6.4 names them the primary trap characterisation:
  - DC I–V values at `phi = (vgs, vds)` (and `Tj` from MC-9);
  - pulsed I–V values at their R-mc10-4 states;
  - the S-parameters' `Gm`/`Gds` as derivatives at each DC point, with `phi` (and `Tj`) frozen at their DC
    values — the small-signal model of [X10] §VI.

  The DC/RF dispersion then follows from the trap and thermal states, and MC-7's eq. 6.52 option is not needed;
  enabling both is refused.
- `Qd` gains the trap inputs only when pulsed S-parameters exist, under the same level rule as `Id` (R-mc10-4):
  one level warns, none leaves `Qd` trap-independent ([P1] §6.7.3) with one line saying so. Its derivative targets
  are each pulsed set's `C21`/`C22` at its points' trap states.
- Equation templates gain two trap terms, each zero at DC by construction (`phi = v`) and each saturating, as
  [G19] eqs. 8, A.1, A.8 and [L26] eq. 33 find them to be:
  - a threshold shift, `vgs,eff = vgs − a1·tanh((vgs − phi1)/c1) − a2·tanh((phi2 − vds)/c2)` — [P21]'s back-gate
    view;
  - a knee term on the template's knee parameter (`α` in the Angelov, Curtice and Statz forms):
    `α·(1 + a3·tanh((phi2 − vds)/c3))`.
- DC I–V is a training target by default ([X14]), or is held out as **validation, not playback** ([P1] §6.7.5) —
  a setting. Either way the step reports the DC I–V error separately from the training error.
- `Traps: None` gives a `.cmodel` byte-identical to the one MC-7 writes.

**R-mc10-6 — Long work.** Re-training through `RunControl`, cancellable, previous result kept (D13).

## 3. Not in this phase
Estimating time constants from transient data (no such data kind exists yet — a future data kind and built-in);
multiple-time-constant capture/emission ([P21] calls it open).

## 4. Gates (minimal tests, run only these classes)
- `ThermallyActivatedEmissionTests` — `τ_emit` follows the Arrhenius form between two temperatures, and the trap
  node's recovery time changes by the predicted factor.
- `CaptureEmissionNodeTests` — DC gives `phi = (vgs, vds)` to 1e-12; HB at a frequency satisfying eq. 6.75 gives
  the DC component of `phi2` within a stated tolerance of `max vds(t)`; S-parameters at that bias are those of the
  frozen-state model.
- `TrapTrainingDataTests` — on a hand-made pulsed set: toward points map to the trap-setting bias under every
  `TrapState` (the pre-pulse level when one is given, the quiescent point otherwise); `auto` leaves beyond points
  out and counts them, and `setting` and `extreme` map them as specified; one trap level fits with one warning, and
  none refuses the state, naming what it needs; a dissipating pre-pulse and the emission warning fire.
- `TrappedModelFitTests` — on `SyntheticFetData` with a drain-lag truth:
  - with pulsed data from 3 quiescent points, the model reproduces each level's pulsed I–V, the DC I–V
    (validation) and the RF `Gds` within stated tolerances;
  - with one quiescent level, it fits, warns once, and reproduces that level's pulsed I–V;
  - with no pulsed data, the trap step is unavailable and the model exports with `Traps: None`;
  - enabling eq. 6.52 dispersion alongside traps is refused.
- `TrapInputsTests` — `Ig` and `Qg` take no trap input; with MC-8's drift charge, `∂Qg/∂phi2` equals
  `∂Qdrift/∂Id · ∂Id/∂phi2`.
- `CustomTrapModelTests` — a `Custom` model written to equal `CaptureEmission` gives identical DC/SP/HB results
  to 1e-9; a dynamics expression naming an unknown variable is refused; a DC expression that is not a fixed point
  is refused.
- `TrapRegistryTests` — an unknown model name in a `.cmodel` is refused by name; `Traps: None` is byte-identical to
  the MC-7 model.
