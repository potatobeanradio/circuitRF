# Brief MC-9 — Temperature and self-heating

**Series:** `brief-modelcomposer-0-overview.md` (D8, D9, D13) · **Tag:** `R-mc9-<m>` · **Depends on:** MC-7, MC-8
**Area:** `src/Design/Modeling/` (temperature flows, Rth extraction); `src/Core/Devices/Composed/` (the thermal
node, the optional fourth pin)
**Read first:** [P1] §5.2.2, §6.6.1–6.6.3 (eqs. 6.66–6.69: why DC and RF output conductance differ; the class-A
cooling example); [R11] **ch. 2.8 (Iwamoto, Xu, Root — the measurement-based FET thermal model)**; [T20] ch. 4
(why a single Rth does not scale to many fingers — recorded as a later rule, not built)

---

## 1. Goal

Data at several temperatures gives a model with temperature as an input, and optionally a self-heating thermal
node. Data at one temperature gives a single-temperature model (D8), and the user knows exactly what that means.

## 2. Requirements

**R-mc9-1 — Temperature as an input.**
- With data sets at ≥ 2 ambient temperatures, every constituent gains the input `T`:
  - ANN: a third input;
  - table: an axis, spline in `T` as well;
  - equation: `T` is available, and templates gain linear temperature coefficients on named parameters.
- **What `T` means for each data kind:**
  - pulsed sets from a non-dissipating quiescent point → the chuck temperature;
  - pulsed sets from a dissipating quiescent point → the chuck temperature + `Rth·Pq`, once `Rth` is known
    (R-mc9-3);
  - DC sets → the junction temperature `Tamb + Rth·P` when self-heating is modelled; when it is not, `T = Tamb`,
    and the step says the DC heating is then embedded statically.
- Charges gain `T` only when S data exists at ≥ 2 temperatures ([X10] eqs. 1.1 and 1.5 make both charges
  functions of `Tj`); otherwise they stay temperature-independent, and the step says so in one line.
- With self-heating, `Id` trains by MC-6's **combined** mode: I–V values at each point's `Tj`, and the RF
  `Gm`/`Gds` as derivatives at `Tj` frozen at its DC value ([X10] §VI). That is what lets one function give the
  DC and the RF output conductance of [P1] eq. 6.69 at once.

**R-mc9-2 — The thermal node.**
- An internal node whose voltage is the temperature rise `ΔT` (K). The core injects the intrinsic dissipated power
  `P = Id·vds + Ig,gs·vgs + Ig,gd·vgd` into it ([P1] eq. 6.67).
- `Rth` and `Cth` are ordinary R and C from the node to ground in the synthesised cell (MC-1 R-mc1-5), so they
  scale by D9 (`Rth × N0/N`, `Cth × N/N0`).
- An optional **multi-pole** network: a Foster ladder of up to 5 RC pairs.
- `T = temp + ΔT` feeds every constituent.
- With a 4-pin symbol, pin 4 `t` **is** the thermal node, so a user can attach a heatsink network; the fourth net
  becomes legal (MC-1 refused it).

**R-mc9-3 — Extracting Rth** ([P1] §6.6, [R11] ch. 2.8).
- Given an isothermal model `Id(v, T)` trained on pulsed data at ≥ 2 chuck temperatures, and DC data at the same
  biases: for each DC point, find the `Tj` at which the isothermal model gives the DC current; then fit
  `Tj − Tamb = Rth·P` by least squares. Either `Rth` is constant, or it varies linearly with `Tamb` (an option).
- Report the fit residual, and the bias region it came from (points with too little power to resolve `ΔT` are
  excluded and counted).
- Without pulsed data, `Rth` may be typed. The DC data is then "de-heated" — each point is moved to its `Tj` — and
  trained as if isothermal, which needs ≥ 2 ambient temperatures for `T` to be an input at all.
- `Cth`, or the ladder, is typed — or comes from a thermal time constant `τth = Rth·Cth`. With S data below the
  thermal corner **and `Traps` set to `None`**, the step offers a fit of `τth` to the low-frequency `Re Y22`
  roll-off of [P1] Fig. 6.34. With traps modelled it does not: in GaN the same roll-off also holds trap time
  constants, and those measured near equilibrium differ from the large-signal ones ([N22] §III). Its tooltip says
  the fitted corner may include trapping.

**R-mc9-4 — The single-temperature model (D8).**
- With one ambient temperature, the step shows `T0` and no temperature controls.
- Self-heating is unavailable, and the reason is the only text: "one temperature".
- MC-1's run-time warning covers use at another `temp`.

**R-mc9-5 — Long work.** Re-training with `T` and the `Rth` search run through `RunControl`, cancellable,
previous result kept (D13).

## 3. Not in this phase
Trap states (MC-10); finger-count-dependent mutual heating ([T20] — a later scaling rule).

## 4. Gates (minimal tests, run only these classes)
- `RthExtractionTests` — on `SyntheticFetData` with a truth `Rth`, the extracted `Rth` is within a stated
  tolerance; a de-heated DC-only path with the truth `Rth` typed reproduces the isothermal truth.
- `ElectrothermalModelTests` — the model reproduces the truth's self-heated DC I–V, including a negative DC
  output conductance where the truth has one, and its RF `Gds` (S at f ≫ 1/τth) equals the truth's isothermal
  `Gds` — [P1] eq. 6.69's two limits.
- `ThermalPinTests` — a 4-pin instance exposes `ΔT` on pin 4; an external RC on pin 4 changes `ΔT` as a series
  thermal resistance should; `N = 2·N0` halves `Rth`.
- `SingleTemperatureStepTests` — a one-temperature document offers no `T` input and no self-heating, and the
  exported model is `Mode: Single` with `T0`.
