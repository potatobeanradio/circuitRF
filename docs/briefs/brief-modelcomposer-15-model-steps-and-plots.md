# Brief MC-15 — The model steps and every comparison plot

**Series:** `brief-modelcomposer-0-overview.md` (§3, D4, D7, D8, D10, D12, D13) · **Tag:** `R-mc15-<m>` · **Depends on:** MC-14
**Area:** `src/Ui/ModelComposer/`, `src/Ui/Views/ModelComposer/`; the Data Display's contour and surface renderers
(`src/Render/DataDisplay/Renderers/`, used); `docs/user/src/reference/model-composer.md` (source only)
**Read first:** MC-7 … MC-12 (each step shows exactly what its phase computes — nothing is computed here);
`docs/design/data-display.md`; `docs/design/loadpull-contours.md`

---

## 1. Goal

The remaining steps, each a thin view over its phase's function, and the plots the owner asked for: measured
against model I–V, the charge-surface difference, 1-D C–V with the other voltage fixed, and validation overlays.

## 2. Requirements

**R-mc15-1 — The method picker**, the same control for every constituent:
- **Equation**: a template list, or an expression editor with named parameters and their fitted values.
- **Table**: grid spacing and smoothing.
- **ANN**: hidden layers (1–4), neurons per layer (default 2 × 8, MC-6 R-mc6-1), activation, restarts, seed,
  held-out %.

After a fit, one line in the status strip: "Id: ANN 2×8 · held-out 0.8 % · domain 412 pts".

**R-mc15-2 — The Current step.**
- Pickers for `Id` and `IgGs`/`IgGd`, and the dispersion option (tooltip: the simpler alternative to heating and
  traps).
- **Plots:**
  - output and transfer I–V families, measured symbols against model lines, at the cursor's temperature and
    quiescent point;
  - the error map over the bias plane;
  - `Gm` and `Gds` against bias: data derivatives against model, and RF against model when dispersion is on;
  - the extrapolation domain drawn on the bias plane.

**R-mc15-3 — The Charge step.**
- Pickers for `Qg`/`Qd`: adjoint ANN; line integral with path A, B or mean; table; equation; drift-charge.
- **Plots:**
  - **1-D C–V**: any `Cij`, or the circuit set `Cgs Cgd Cds Cm`, against `vgs` with `vds` fixed or against `vds`
    with `vgs` fixed. Measured symbols, model lines; the fixed voltage is the cursor's.
  - **Charge surface**: the model's `Qg` or `Qd` over (vgs, vds), through the surface renderer.
  - **ΔQ map**: the model's charge against the line-integral charge of the data, through the contour renderer,
    in % of the charge's range.

**R-mc15-4 — The Thermal step.**
- `Rth`, `Cth` or the ladder, the extraction Run and its residual line.
- **Plots:** `Tj` against dissipated power; DC I–V with heating against the isothermal model.
- A one-temperature document shows `T0` and the single word "one temperature" in place of the controls (D8).

**R-mc15-5 — The Traps step.**
- The setting (`None`, `CaptureEmission`, `Custom`); state toggles; time constants; the custom expression editor
  with its fixed-point check result.
- A state the data cannot support shows its one-line reason in place of its toggle; a state trained from one
  level shows that level.
- **Plots:** pulsed I–V per trap-setting level, measured against model, with points beyond the trap-setting bias
  (MC-10 R-mc10-4) drawn hollow; DC I–V as validation.

**R-mc15-6 — The Check step.** **Check Charge Conservation** — also on the toolbar — runs MC-8 R-mc8-5.
- Results: one line per part ("Data gate 0.4 % · drain 3.8 % · Model 0.0 % · HB 0.2 µA"). Transcapacitance is
  shown as information.
- **Plots:** the curl map per row; the two-path ΔQ map; the HB check's gate-current spectrum ([P1] Fig. 6.21's
  picture).

**R-mc15-7 — The Validate step.**
- The items of MC-11 R-mc11-4, each with its tolerance and one result line. Run all, or one.
- **Overlays:**
  - S at the measurement plane (rectangular and Smith) at the cursor bias;
  - I–V families;
  - power sweeps (Pout, gain, PAE, Id);
  - loadpull contours, measured against model, through the existing loadpull contour plots;
  - IM3/IM5 against input power.
- "Show testbench" writes the generated `.cnl` beside the `.cmc` and reveals it.

**R-mc15-7a — The Model Validation Report dialog** (owner request, 2026-10-19). From the Validate step and the toolbar,
a dialog holding every MC-11 R-mc11-6 option — the sections; the S biases; the fixed voltages of the C–V families; the PA
classes; the frequency, bias, R_opt and the source's fundamental and 2f0 terminations; the load disc (radius, rings,
spokes); the drive ladder and the compression — each opening on the value stored in the `.cmc` or its default, and
written back to `Steps.Report`. Generate runs MC-11's function off the UI thread with progress and Cancel; the result is:
- a **Data Display document** (`.cdd`) beside the report folder, one tab per section, built by a preset in
  `src/Render/DataDisplay/` (the `DoeDisplayPreset` pattern) so the CLI and the window build the same document: I–V
  families (measured symbols, model lines), the ΔQ maps (contour), the C–V families with the fixed voltage as the legend,
  S overlays at the chosen biases (rectangular and Smith), the Rth plot, and per class the loadpull contours with the
  performance summary table (Preset, MXP, MXE) and the region-of-convergence map;
- **Export PDF**: every tab, through `PlotComposer`, the composer `render` uses.

**R-mc15-8 — The Export step.**
- `N0`/`W0`, read-only here.
- The scaling-rule table: the same rows as the network values, rule column only.
- The **symbol picker**: thumbnails of compatible symbols only (MC-11 R-mc11-2).
- The output path, and **Export**.
- **Copy instance** puts the placement payload on the clipboard (MC-11 R-mc11-3).
- Passive: the cell name, the library, the parameter list and the symbol.
- A stale step blocks Export with the one line that names it, and a **Run stale** button re-runs the stale steps
  in order.

**R-mc15-9 — Plots.**
- Every plot is a `PlotControl` fed from the phase's `DataSet`, with the control's own copy-to-clipboard and
  inspector.
- Measured traces are symbols and model traces are lines, in the Data Display's default trace palette.
- **Open in Data Display** sends the plot's `DataSet` to a new display.

## 3. Not in this phase
The example and the user pages (MC-16).

## 4. Gates (headless Avalonia harness; run only these classes)
- `ModelComposerCurrentStepTests` — a fit updates the I–V overlay, which holds a measured and a model trace per
  family member; the error map and the domain outline are drawn.
- `ModelComposerChargeStepTests` — the 1-D C–V slice follows the cursor's fixed voltage; the ΔQ map is a contour
  plot of the expected `DataSet`.
- `ModelComposerCheckStepTests` — the button runs the check off the UI thread, and the result line matches MC-8's
  function for the same document.
- `ModelComposerExportStepTests` — export from the window is byte-identical to `circuitrf model export` on the same
  document; the symbol picker hides 2-pin symbols; a stale step blocks Export and Run stale clears it.
- `ModelComposerSingleTemperatureTests` — a one-temperature document shows `T0` and no thermal controls.
- `ModelReportDialogTests` — the dialog's options round-trip through `Steps.Report`; the preset's `.cdd` holds one tab per
  section of a generated report, and its PDF is byte-identical to `circuitrf model report --pdf` on the same document.
