# Brief MC-19 — Traps without pulsed data: the threshold-shift coupling

**Series:** `brief-modelcomposer-0-overview.md` (D10) · **Tag:** `R-mc19-<m>` · **Depends on:** MC-10, MC-16
**Area:** `src/Core/Devices/Composed/` (`Traps/`, `ComposedCoreProgram.cs`, `CmodelFile.cs`);
`src/Design/Modeling/TrapFit.cs`, `TrapFitSettings.cs`, `ModelCommands.cs`; the Traps step in `src/Ui/ModelComposer/`;
`docs/user/src/reference/model-composer.md`, `file-formats.md`; `docs/design/model-composer.md`
**Read first:** MC-10 and its §1 rule (*less data gives a more limited model, never a refusal*); [P1] §6.6.4 and §6.7.2;
[X10]; `TrapFit.Unavailable`; `CaptureEmissionTraps` (`DcValue`, `CaptureSign`)

---

## 1. Goal

Today a trap model needs pulsed I–V (`TrapFit.Unavailable`: no `PulsedIv` set, no traps). The trap states are inputs
the current is *trained* on, and only pulsed data holds currents at trap states away from DC. That breaks MC-10's own
rule: a user with DC I–V and multi-bias S gets no traps at all, only DC/RF dispersion. Dispersion has a single linear
τ, with none of the fast-capture, slow-emission asymmetry that makes a trapped PA's knee walk out under drive.

This brief adds a **generic coupling**. The trap dynamics stay those of `CaptureEmission` (typed time constants, as
today). The current takes its trap states through a **threshold shift**, which leaves DC exact by construction. The
coupling's strengths are typed, or fitted from the DC/RF difference in `gm` and `gds` that multi-bias S already shows.

## 2. Requirements

**R-mc19-1 — The coupling, in the format.** The `Traps` block gains `Coupling`:
- `Trained` — today's behaviour: `Id` takes the enabled states as inputs. A block with no `Coupling` is `Trained`, and
  is read and written byte-identically to today's.
- `Shift` — `Id` has **no** trap-state inputs. The core evaluates it at a shifted gate voltage,

  `Id(vgs, vds, T, φ) = Id₀(vgs − ΔV, vds, T)`,  `ΔV = Σₖ aₖ · sₖ · (φₖ − φₖ,DC(vgs, vds, T))`,

  over the enabled states, where `sₖ` is the state's `CaptureSign` and `φₖ,DC` its `DcValue`. For `CaptureEmission`
  this is `ΔV = a_d·(φ2 − vds) + a_g·(vgs − φ1)`: a drain-lag state held above `vds`, or a gate-lag state held below
  `vgs`, lowers the current. Parameters `ShiftDrain` (`a_d`) and `ShiftGate` (`a_g`), in V/V, carried in the block's
  `Parameters`.

At any DC operating point `φ = φ_DC`, so `ΔV = 0` and the model's DC is exactly `Id₀`'s, the trap-free current fitted
to the DC I–V. `Ig`, `Qg` and `Qd` do not take the shift. Every derivative the Newton step needs (`∂Id/∂vgs`, `∂vds`,
`∂T`, `∂φₖ`) is exact, by the chain rule through `ΔV`. `CmodelFile.Validate` refuses `Shift` with an `Id` that has
trap-state inputs, `Shift` on a model other than `CaptureEmission` (`Custom` follows in a later brief once its DC value
and capture sign are declared), and a non-finite strength.

**Check the form against [P1] before building.** Confirm the threshold-shift coupling against [P1]'s trapping
treatment (§6.6.4, §6.7.2) and cite the equation followed in the design note. Where [P1] couples the states
differently, follow [P1] and record why. The derivation in R-mc19-3 must then be redone for that form.

**R-mc19-2 — Availability.** `Unavailable` no longer refuses on missing pulsed data when the coupling is `Shift`. With
pulsed data the default stays `Trained`, and `Shift` is still selectable. With none, the Traps step offers `Shift` only
and says why in one line.

**R-mc19-3 — Fitting the strengths from S.** With the trap frozen at RF (every S frequency far above `1/τ_emit`), the
small-signal conductances of R-mc19-1's form are:

- `gds_RF = gds₀ + a_d·gm₀`
- `gm_RF = (1 − a_g)·gm₀`

`gm₀` and `gds₀` are the derivatives of the fitted `Id₀`. `gm_RF = Re Y21` and `gds_RF = Re Y22` are the intrinsic
values averaged over the flatness band, read exactly as the dispersion fit reads them. Fit `a_d` and `a_g` by linear
least squares over the active biases. Leave out points whose `gm₀` is below `IdFloor` of its largest, as the current
weighting does. Report each strength with its standard error and the RMS residual of both relations.

When self-heating is modelled, `gm₀` and `gds₀` are the isothermal derivatives at each point's junction temperature, so
the thermal part of the DC/RF difference is not read as traps. When self-heating is *not* modelled and the data heats,
the report says that the fitted `a_d` holds the thermal difference too.

**R-mc19-4 — Typed strengths.** Either strength can be typed and locked; a typed one is not fitted. With no S data and
an untyped enabled state, the step refuses, naming both ways out (type it, or add multi-bias S).

**R-mc19-5 — The fit flow.** Under `Shift` the Traps step fits the strengths and leaves the current as it is: `Id₀` is
the trap-free current the Current step fitted from DC. `AllSteps` keeps its order (current, traps, charge). The charges
are trap-independent, with the line MC-10 already prints.

**R-mc19-6 — The window.** The Traps step gains the coupling choice, and under `Shift` the two strengths with lock,
value and ± after a fit. Its plots: `Gm` and `Gds` against bias at the cursor, RF from S (symbols) against the shifted
model's frozen-trap conductances (lines), with the trap-free model dashed.

**R-mc19-7 — Docs** (sources only; the owner runs DocGen): in `model-composer.md`, the traps section describes
the two couplings. Its "No pulsed data" row becomes *threshold-shift traps, time constants typed, strengths typed or
fitted from multi-bias S*. The "What a model needs" table gains that row, and the sentence after it is corrected.
`file-formats.md` documents `Coupling` and the two parameters. The design note records the form, its [P1] citation and
the R-mc19-3 derivation.

Owner rules: no change history in the pages; no vendor names; no "the design's own".

## 3. Not in this phase
Fitting the time constants (an open item already); bias-dependent strengths; `Shift` for `Custom`; combining a
`Shift` coupling with pulsed-data training.

## 4. Gates
- `TrapShiftCouplingTests`:
  - **DC is exact.** A `Shift` model's DC sweep equals its trap-free `Id₀` at every point to 1e-9 relative.
  - **The strengths are recovered.** A truth `.cmodel` with an equation `Id`, `Shift`, `a_d = 0.05` and `a_g = 0.1`
    is simulated by circuitRF as DC I–V and multi-bias S. The document built from that data with no pulsed sets fits
    both strengths within 2 %.
  - **The traps act.** On that truth, harmonic balance at the class-AB bias gives a lower output power at compression
    than the trap-free `Id₀` (the knee walkout). Choose the drive and strengths so the difference is well clear of
    the HB tolerance, and record the measured difference in `RESOLVED.md`.
  - **The format.** A `.cmodel` without `Coupling` round-trips byte-identically, and each R-mc19-1 refusal names
    its field.
- The existing MC-10 trap gates pass unchanged.
- Run only these classes, the MC-10 trap classes and `ModelComposerDocumentedPageTests`.
