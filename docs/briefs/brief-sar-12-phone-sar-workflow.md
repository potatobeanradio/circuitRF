# Brief SAR-12 — The phone SAR workflow

**Series:** `brief-sar-0-overview.md` (L1, L2) · **Tag:** `R-sar12-<m>`
**Depends on:** SAR-7
**Area:** `C3dPhantom` (`FlatBox`, a generic head shape), a device-placement helper in the `.c3d` editor (`src/Ui`)
with its model in `src/Design/ThreeD`, the SAR readouts (SAR-6), `docs/design/sar.md` §14

---

## 1. Goal

A wireless-device engineer places an antenna or a whole device model (STEP) against a phantom filled with
tissue-simulating liquid. They run it at the device's bands and read 1 g and 10 g peak SAR, normalised to the
device's rated power, beside the published limits.

## 2. Requirements

**R-sar12-1 — Phantoms.**
- **The flat phantom:** a `FlatBox` (SAR-3) with a thin low-loss shell and a liquid fill. Dimensions and shell
  thickness are parameters, with defaults taken only from free public regulator documents (cited) or, failing that,
  stated as circuitRF's own and labelled so (L2).
- **A generic head:** an ellipsoid shell head with ears modelled as flattened spacers. It is circuitRF's own shape,
  not any standard's mannequin, and **named as such**, so nobody takes it for the standard one.

The liquid is SAR-2's head or body liquid at the run's frequency.

**R-sar12-2 — Placement.** A helper places a selected object (an antenna or a device group) against a phantom face at a
given **separation**, measured along the face normal from the object's nearest point, with a **tilt angle** about a
chosen axis. It writes ordinary transforms, so nothing in the format is phone-specific. The separation is shown as a
dimension in the 3D view.

**R-sar12-3 — Bands and power.** One SAR frequency per run (L6). A **band sweep** helper creates one setup per listed
frequency from a template setup (each with its liquid values) and runs them. The result table has one row per band:
psSAR1g and psSAR10g at the rated power (`DrivePowerW`, the conducted power the device delivers), the duty-cycle
scaling factor when the user gives one, and the location.

**R-sar12-4 — Limits as reference values (L1).** A small table of published exposure limits, each cited and dated, is
shown beside the result **as a reference value**, never as pass or fail:
- 1.6 W/kg over 1 g (head and trunk), under a national regulator's rules;
- 2.0 W/kg over 10 g (head and trunk), under the international guidelines;
- limb values.

A user can add their own. Exceeding a reference value colours the number and says which reference; it does not say
"fail".

**R-sar12-5 — System check.** A built-in example puts a half-wave dipole at a fixed separation from the flat phantom
and reports psSAR1g and psSAR10g per watt: the shape of a laboratory system check. Its values are **circuitRF's own
results**, recorded in the design note with their grid convergence, because the standard's target values are not
available (L2). A user with their own target values enters them and sees the difference.

## 3. Not in this phase

Hand and body-worn positions; absorbed power density above 6 GHz; compliance reports.

## 4. Gates (minimal)

- `PlacementTests`: separation and tilt produce the intended nearest distance, to 1 DBU.
- `BandSweepTests`: three bands make three setups with their own liquid values; the result table has three rows.
- The system-check example runs headlessly in the opt-in `Benchmark` tier; the routine gate checks its committed
  result loads and its readout table renders.
