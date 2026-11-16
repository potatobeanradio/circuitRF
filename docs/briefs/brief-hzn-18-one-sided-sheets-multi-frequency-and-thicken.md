# HZN-18 — the rest of the glasses family: one-sided lossy sheets, Multi-Frequencies setups, and Thicken Sheet

Series: `brief-hzn-0-overview.md`. Needs HZN-17.

## Why

After HZN-17, 84 of Hertzon 0.3.26's 99 example designs open. Of the 15 refused, four belong to the smart-glasses antenna examples,
and each of them is stopped by one item. Probing scratch copies with that item removed found nothing else behind any of them:

| Design | Item |
|---|---|
| glasses Ex1 `Ex1` | `Finite Conductivity on one side of a sheet` |
| glasses Ex2 run2 `Ex2` | `Finite Conductivity on one side of a sheet` |
| glasses Ex2 runB `Ex2_Benchmark` | `solution frequency mode 'Multi-Frequencies'` |
| glasses Ex2 runC `T_thick` | `Operation ThickenSheet` |

That makes **88 of 99**. No other single item opens more than one design: the next ones are Split (one design, with another
refusal behind it) and the coaxial wave port (one).

## Established from Hertzon's own documentation (never its source)

- **One-sided and two-sided Finite Conductivity sheets** (changelog 0.3.22): one-sided is "one surface impedance carried the current
  of both sides of the sheet, about twice the conductor loss of a strip in air". Two-sided is the default since then, "like a sheet
  inside the model in common FEM solvers". Projects saved with Two-sided off keep it, and Hertzon's Validation Check warns about it.
  Hertzon's figures for a lossy strip dipole (2·10⁴ S/m, 1 GHz): radiation efficiency 86.0 % one-sided, 91.9 % two-sided, 92.2 % for
  a 0.2 mm solid.
- Hertzon's two-sided sheet is EMerge's split thin conductor (`EMERGE_INTEGRATION.md`), which is what circuitRF's lossy sheet already
  is on EMerge.
- **Thicken Sheet** (tutorial): "a positive thickness grows to the positive side of the sheet's main axis (+Z for a sheet in the XY
  plane), a negative one to the other side"; Reverse direction flips it.

## What is already there

- EMerge 3.0.0's own boundary set-up makes every lossy sheet inside the model a split `ThinConductor`, and a lossy face on the outer
  boundary a `SurfaceImpedance`. It sets neither on a face that already has a condition (`no_overwrite`). So a single
  `SurfaceImpedance` on a sheet's faces is one call in circuitRF's driver.
- `Em3dModePorts.SolverRefusal` is where HZN-16 keeps a periodic cell on EMerge.
- A Multi-Frequencies setup's list is a single-points plan, which HZN-7 already writes (`Frequency` plus `AdditionalFrequencies`).
- A sheet swept along its normal is a prism (HZN-2, HZN-13), and Thicken Sheet is that sweep.

## What to build

### R-hzn18-1 — a one-sided lossy sheet
- **Format.** `.c3d` sheet key `OneSided` (bool, written only when true): the sheet's metal is one surface impedance carrying the
  current of both its faces, rather than a thin conductor with a current on each face. It means something only on a sheet whose
  metal is a lossy conductor. On any other sheet, `check` warns that it does nothing.
- **Engine.** `Em3dSheet.OneSided`, set by the elaborator from the sheet, or from a sheet boolean's carrier.
- **EMerge.** The script carries `oneSided` per sheet. The driver states `SurfaceImpedance` with the sheet's metal and thickness on
  the sheet's faces before EMerge's defaults run, so they make no `ThinConductor` there.
- **Palace and openEMS** refuse it by name in `SolverRefusal` ("…which circuitRF runs on EMerge only"), as HZN-16 does.
- **Translation.** A Finite Conductivity boundary with `TwoSided` False on a sheet sets `OneSided`, with a note that this is Hertzon's
  older default, about twice a two-sided sheet's conductor loss. A boundary on a face of the outer boundary needs nothing: it is one
  surface impedance either way.
- **Save and export.** `C3dToHzn` writes `TwoSided` False for a one-sided sheet. Everything else stays as written.
- **UI.** The Properties panel shows a **One-sided** check box for a selected lossy sheet, with one undo entry per click. Its tooltip
  says what it changes.

### R-hzn18-2 — Multi-Frequencies setups
- A setup in Multi-Frequencies mode with no sweeps solves at each frequency in `MultiFrequencies` (Hertzon accepts `,` or `;`), as
  single points. The adaptive frequencies are listed as unused, as now.
- Auto Open sizes its air box from the lowest of those frequencies, not from `Frequency`.

### R-hzn18-3 — Thicken Sheet
- `ThickenSheet` on an unswept rectangle, circle or polyline in an axis plane is the sweep along its normal by `Thickness`, negated by
  `ReverseDirection`. Expressions are kept.
- `BothSides` True is refused by name, because whether its thickness is per side or in total is not established. Thickening a sheet in
  a tilted or upright plane, or an ellipse, is refused as the sweep is.

## Gates

- **`HznOneSidedMultiFrequencyThickenTests`** (Ui.Tests/Hzn, synthetic fixture), one per claim:
  - a one-sided Finite Conductivity sheet translates with `OneSided` and round-trips through `.c3d`;
  - the EMerge problem carries `oneSided` for that sheet only;
  - Palace and openEMS refuse it by name;
  - `check` warns about `OneSided` on a perfect-conductor sheet;
  - export writes `TwoSided` False;
  - a Multi-Frequencies setup solves at its listed frequencies, and Auto Open sizes from the lowest;
  - a thickened polyline is a prism of that height, and so is a reversed one, below the sheet;
  - `BothSides` True is refused by name.
- **Real files**, under `CIRCUITRF_HERTZON_HOME`: the census reports **88 of 99**.
- **Physics (scratch measurement, recorded in RESOLVED).** A lossy strip dipole on EMerge, one-sided against two-sided. The one-sided
  sheet must lose more, in the direction and roughly the size of Hertzon's figures. Kept short (one frequency, a coarse mesh).
- **UI.** UiSnapshot of the Properties panel with a lossy sheet selected, showing One-sided. Look at it and list it.

## Risk

- One-sided is a legacy model in Hertzon itself. circuitRF carries it so the two tools give the same answer on such a file, not as a
  recommendation. The note says so.

## Rules

As brief-hzn-10. Two other agents share the repo: stage only this brief's files.

## Completion note (2026-11-16)

Built as specified. **Census 84 → 88 of 99**, and every glasses design opens.

- `HznOneSidedMultiFrequencyThickenTests`: seven gates pass. They cover the brief's list, with the perfect-conductor warning checked on
  the assembled problem, plus the four real glasses designs under `CIRCUITRF_HERTZON_HOME`. `HznAutoOpenAndLibraryTests` asserted the
  old refusal of a one-sided sheet; it now asserts a one-sided sheet.
- **Physics:** a lossy open stub on EMerge absorbs 1.3–1.5× more power one-sided than two-sided at every point from 4 to 5.2 GHz,
  Hertzon's direction (numbers in `src/Design/RESOLVED.md` §HZN-18). Each run took 3 s.
- **Found on the way:** writing `"oneSided": false` on every sheet changed every EMerge problem's geometry hash. It is written only
  when true.
- **UI:** UiSnapshot of the Properties panel: the lossy strip shows **One-sided** ticked under Model, and the perfect-conductor ground
  shows no such row. The setup card shows the Multi-Frequencies setup's two frequencies.
- **Docs:** `c3d-format.md` (`OneSided`) and the user page `drawing-in-3d.md` (§One-sided sheet), whose HTML was regenerated.
- **Neighbouring tests:** Hzn, port, EMerge-run, persistence, resistive-film and EM-cell classes: 275 passed. The five
  `Hertzon_Describes_*` tests fail only because this Hertzon download has no `hertzon-cli`.
