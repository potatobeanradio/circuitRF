# Brief series — Model Composer: nonlinear FET and passive model extraction (MC-1 … MC-28)

**Status:** written, not started · **Date:** 2026-10-19 · **Decisions:** locked (§2), owner-reviewed
**Scope source:** owner request of 2026-10-19 (paraphrased in §0)
**Builds on:** the SDD (`docs/design/sdd.md`), the optimizer core (`docs/design/tuning-optimization.md` §10–§12),
the linear, nonlinear-DC and HB engines, railRF's window rules (`docs/design/railrf.md` §11), the Smith Chart
tool's reuse of plots and schematics (`docs/design/smith-chart.md`), `RunControl` (`src/Engine/RunControl.cs`)
**Requirement tag:** `R-mc<n>-<m>` (phase n, requirement m).
**Design note to be written by MC-1:** `docs/design/model-composer.md` — this overview is its source.

---

## 0. What the owner asked for (paraphrased)

- A new tool under **Tools** that takes measured — or simulated — device data and produces a complete nonlinear
  FET model that circuitRF's DC, S-parameter and harmonic-balance engines use. Today the only route is a
  hand-written SDD with its own I–V and Q–V expressions.
- The same tool also builds **passive** models from S-parameter blocks, so a passive model gets the same export
  path.
- The workflow it supports: on-wafer S-parameters measured across the I–V plane, at one device temperature, often
  **pulsed** to stay isothermal; for devices that trap charge, pulses that start from and return to a chosen
  **quiescent** bias; terminal currents recorded at every point; the probe launch structures removed; an
  **extrinsic network** (series R and L at each terminal, a mutual between Lg and Ld, small capacitors) fitted with
  physically sensible values (never a negative resistor) and then stripped; intrinsic Y-parameters mapped to the
  small-signal circuit; capacitances integrated to charges; and a way to **check charge conservation**.
- Current and charge may each be an **equation, a table or a neural network**, in any combination; the user picks
  the network's depth.
- A **document**, so a model can be resumed later without re-importing data or re-stating fits.
- Data simulated in another tool and saved as `.mat` or `.npy` is accepted in place of measurements.
- Data over **temperature** produces a model over temperature; data at **one temperature** still produces a model,
  understood to be valid at that temperature only.
- Two known pitfalls: **where the bias voltages are referenced** relative to the extrinsic resistances, and
  **charge trapping** in GaN.
- UI: circuitRF's look and feel; toolbars like the schematic, layout and 3D editors; railRF's toggleable panels,
  resizable in both directions; the Smith Chart tool's reuse of plots and schematics, with `.csch` copy and paste;
  measured-versus-model I–V overlays, charge-surface differences, 1-D C–V with the other voltage fixed; **minimal
  text on screen** — one-line errors and warnings only. The owner expects to iterate on the layout.
- A **responsive** UI: long tasks run off the UI thread, can be cancelled, and show a progress bar.

Owner answers on 2026-10-19 (paraphrased):
- The name is **Model Composer**; its document is **`.cmc`**; the exported model is **`.cmodel`**.
- Passive models also export **as a cell**. A FET model is a standalone `.cmodel`, and the user **chooses its
  symbol**.
- A **new stand-alone device**, not generated SDD text.
- Measurement data is **referenced and cached**.
- Trap modelling is a **user setting**; start with a simple model and leave room for more, including ones future
  users and developers add — it is an open research area.
- Passive modelling is in **this** series.
- **Periphery scaling is required**: the base periphery of the measured device is recorded when the model is
  generated, and a user changes the finger count in the schematic. Simple finger-count scaling now; richer rules
  later.
- The owner supplied a stock GaN extrinsic network (D5).
- [P1] is the series' primary reference: where a method choice is open, the briefs follow the approach it
  describes.

Owner request of 2026-10-20 (paraphrased), briefs MC-21 … MC-24:
- A placed `.cmodel` gets per-instance flags that turn pieces of the model off: no charge, charge linearised at the
  bias point, one capacitance (Cgs, Cdg or Cds) linear while the others stay nonlinear, no gate lag, no drain lag.
  The purpose is for a designer to toggle impairments on and off, see what each one does to their design, and debug
  a circuit with them.
- harmonicaRF takes a `.cmodel` as its DUT, offers the same flags, and reads current and voltage at the model's
  intrinsic plane, which is the point of harmonicaRF. Its loadline offers either the intrinsic or the extrinsic I–V.
- The user page for the `.cmodel` explains the new options.

Owner request of 2026-10-21 (paraphrased), briefs MC-26 … MC-28:
- A composed FET can also be exported as Verilog-A source, so a model built in circuitRF runs in other simulators. It
  carries every parameter and switch the `.cmodel` has, and circuitRF, which already runs Verilog-A, compares the two.
- A model with a table constituent is refused rather than exported.
- The linear-charge switches take their bias point as instance parameters.

### 0.1 References

The primary reference, followed where a method choice is open:
- **[P1]** J. C. Pedro, D. E. Root, J. Xu, L. C. Nunes, *Nonlinear Circuit Simulation and Modeling: Fundamentals
  for Microwave Design*, Cambridge University Press, 2018. Ch. 5 (parasitic stripping, the intrinsic linear model);
  ch. 6 (§6.2.5–6.2.9 constitutive relations, tables, ANNs, extrapolation, linearisation; §6.3 charge modelling;
  §6.4 dispersion; §6.5 symmetry; §6.6 self-heating and trapping; §6.7 waveform-based identification).
- Papers behind [P1]'s methods:
  - **[X03]** J. Xu, M. C. E. Yagoub, R. Ding, Q. J. Zhang, "Exact adjoint sensitivity analysis for neural-based
    microwave modeling and design," IEEE T-MTT, Jan. 2003.
  - **[X06]** J. Xu, D. Gunyan, M. Iwamoto, A. Cognata, D. E. Root, "Measurement-based non-quasi-static
    large-signal FET model using artificial neural networks," IMS 2006.
  - **[X07]** J. Xu, D. Gunyan, M. Iwamoto, J. Horn, A. Cognata, D. E. Root, "Drain-source symmetric artificial
    neural network-based FET model with robust extrapolation beyond training data," IMS 2007.
  - **[X10]** J. Xu, J. Horn, M. Iwamoto, D. E. Root, "Large-signal FET model with multiple time scale dynamics
    from nonlinear vector network analyzer data," IMS 2010.
  - **[X14]** J. Xu, R. Jones, S. A. Harris, T. Nielsen, D. E. Root — the GaN dynamic FET model from active
    source-injection measurements, IMS 2014, with its ARFTG (June 2014) validation companion.
  - **[I11]** M. Iwamoto, J. Xu, J. Horn, D. E. Root, "III-V FET high frequency model with drift and depletion
    charges," IMS 2011.
- On traps: **[G19]** J. Gomes, L. Nunes, C. Gonçalves, J. Pedro, "An accurate characterization of
  capture time constants in GaN HEMTs," IEEE T-MTT, Jul. 2019; **[N22]** L. C. Nunes, J. L. Gomes, F. M. Barradas,
  J. C. Pedro, "A simple thermally activated trapping model for AlGaN/GaN HEMTs," EuMIC 2022; **[P21]** J. C. Pedro,
  "Characterization and modeling of AlGaN/GaN HEMT trapping transients," MOS-AK workshop, 2021.

Also used: **[R11]** M. Rudolph, C. Fager, D. E. Root (eds.), *Nonlinear Transistor Model Parameter Extraction
Techniques*, Cambridge University Press, 2011 (ch. 2.8 measurement-based FET thermal model; ch. 3.4 HEMT extrinsic
extraction; ch. 4 uncertainty of small-signal extraction; ch. 7 dispersive effects). **[A07]** P. H. Aaen,
J. A. Plá, J. Wood, *Modeling and Characterization of RF and Microwave Power FETs*, Cambridge University Press,
2007. **[T20]** J. Tarazi, *Nonlinear Modelling of GaAs and GaN High Electron Mobility Transistors*, PhD thesis,
Macquarie University, 2020 (scaling; separating thermal and trap state from steady-state small-signal data).
**[GSF19]** G. P. Gibiino, A. Santarelli, F. Filicori, "Charge-conservative GaN HEMT nonlinear modeling from
non-isodynamic multi-bias S-parameter measurements," Int. J. Microw. Wireless Technol. 11, 2019. **[L26]** H. Luo,
Z. Wang, Y. Guo, "A physics-informed neural network-based scalable model for GaN HEMTs," IEEE T-MTT 74(5), May
2026 (affine finger-count and gate-width scaling of the extrinsic elements, eqs. 21–30).

---

## 1. The phases

| Phase | Brief | What it delivers | Depends on |
|---|---|---|---|
| MC-1 | `brief-modelcomposer-1-cmodel-and-device.md` | The `.cmodel` format, the `ComposedFet` device with equation constituents, elaboration as a cell, periphery scaling, the single-temperature rule, `check` on a `.cmodel`; the design note | — |
| MC-2 | `brief-modelcomposer-2-tables-anns-extrapolation.md` | Runtime table (C² spline) and ANN evaluators, guided extrapolation, the drain–source symmetry option, allocation-free grid evaluation | MC-1 |
| MC-3 | `brief-modelcomposer-3-document-and-data.md` | The `.cmc` document, data sets and their conditions, importers (Touchstone sets, CSV/TSV, `.npy`, `.mat`), the cache, data hygiene | — |
| MC-4 | `brief-modelcomposer-4-pads-network-deembedding.md` | Pad de-embedding, the six-pin extrinsic network (stock + user), de-embedding through any network, intrinsic re-referencing, intrinsic quantities | MC-3 |
| MC-5 | `brief-modelcomposer-5-network-fit.md` | Extrinsic fit: cold-FET direct, multi-bias optimisation, fixed values; bounds, locks, ties; identifiability report | MC-4 |
| MC-6 | `brief-modelcomposer-6-ann-training.md` | The in-house MLP trainer: value training and adjoint (derivative) training, L-BFGS, determinism, held-out error | — |
| MC-7 | `brief-modelcomposer-7-current-constituents.md` | Id and Ig from DC/pulsed I–V by equation, table or ANN; the optional DC/RF dispersion | MC-2, MC-4, MC-6 |
| MC-8 | `brief-modelcomposer-8-charges-and-conservation.md` | Qg and Qd by adjoint ANN (default), line integral, table or equation; **Check Charge Conservation** | MC-2, MC-4, MC-6 |
| MC-9 | `brief-modelcomposer-9-temperature-and-self-heating.md` | Multi-temperature models, the thermal node, Rth extraction, the single-temperature model | MC-7, MC-8 |
| MC-10 | `brief-modelcomposer-10-traps.md` | The trap-model contract, the capture/emission built-in, the custom expression model, trap-state training data from double-pulse or ordinary pulsed sets | MC-7 (MC-9 for thermal coupling) |
| MC-11 | `brief-modelcomposer-11-export-and-validation.md` | Export to `.cmodel` with symbol, scaling rules and provenance; validation against S, I–V and large-signal data; the Model Validation Report (headless) | MC-7, MC-8 (MC-9, MC-10 when present) |
| MC-12 | `brief-modelcomposer-12-passive-models.md` | Passive: S-parameter analysis, ranked topology proposals, fit, geometry-family fit, export as a cell | MC-3, MC-5, MC-11 |
| MC-13 | `brief-modelcomposer-13-cli-and-mcp.md` | The `model` verb and its MCP tool — **agent-ready here** | MC-11, MC-12 |
| MC-14 | `brief-modelcomposer-14-window-and-data-steps.md` | The window: lifecycle, toolbar, panels, background tasks; Data, Pads and Network steps; passive Data and Topology | MC-13 |
| MC-15 | `brief-modelcomposer-15-model-steps-and-plots.md` | Current, Charge, Thermal, Traps, Check, Validate and Export steps; every comparison plot | MC-14 |
| MC-16 | `brief-modelcomposer-16-example-docs-acceptance.md` | A `Model Composer` example workspace, the user pages, the series acceptance run, the design note's final state | all |
| MC-17 | `brief-modelcomposer-17-linear-charges.md` | The `Linear` charge template and a per-method minimum: a model from S at one bias | MC-8, MC-16 |
| MC-18 | `brief-modelcomposer-18-model-from-dc-alone.md` | Typed (or zero) linear charges: a model from DC I–V alone | MC-17 |
| MC-19 | `brief-modelcomposer-19-traps-without-pulsed-data.md` | The threshold-shift trap coupling: traps with no pulsed data, strengths typed or fitted from multi-bias S | MC-10, MC-16 |
| MC-20 | `brief-modelcomposer-20-capacitance-tables.md` | A simulator's capacitance table (matrix or circuit set) feeds the charges | MC-8, MC-16 |
| MC-21 | `brief-modelcomposer-21-instance-switches.md` | Instance switches on a placed `ComposedFet` — no charge, charge linearised at the bias point, no gate lag, no drain lag, isothermal, no dispersion — and the bias-point freeze | MC-9, MC-10, MC-16 |
| MC-22 | `brief-modelcomposer-22-branch-linear-charges.md` | Linear Cgs, Cdg, Cds, Cm one at a time, by a charge-conserving branch construction exact at the bias point | MC-21 |
| MC-23 | `brief-modelcomposer-23-harmonicarf-dut.md` | A `.cmodel` as harmonicaRF's DUT, read at its core's intrinsic plane with no mapping; the switches live | MC-21 (MC-22) |
| MC-24 | `brief-modelcomposer-24-loadline-planes.md` | harmonicaRF's loadline and DCIV family at the intrinsic or the extrinsic plane, for every DUT kind | MC-23 for the composed case |
| MC-25 | `brief-modelcomposer-25-trap-thermal-hb-convergence.md` | Harmonic balance stalls when a trap node and the thermal node are both present; the example's power sweep converges to 22 dBm and a sweep's non-convergence is visible | MC-9, MC-10, MC-21 |
| MC-26 | `brief-modelcomposer-26-veriloga-writer.md` | Verilog-A export, the writer and the static core: network, equation and ANN constituents with emitted gradients, guided extrapolation, symmetry, drift, scaling, temperature; tables refused | MC-1, MC-2, MC-7, MC-8, MC-16 |
| MC-27 | `brief-modelcomposer-27-veriloga-states-and-switches.md` | Verilog-A export: dispersion and trap nodes, self-heating with an optional thermal pin, instance `Rth`/`Cth`/`Temp`, all ten switches, the linear-charge switches at a bias point given as `VgsQ`/`VdsQ`/`TjQ` | MC-26, MC-21, MC-22 |
| MC-28 | `brief-modelcomposer-28-veriloga-export-ui-cli-docs.md` | Export Verilog-A on the Export step, `model export -o x.va` from a `.cmc` or a `.cmodel`, the MCP tool, the example's `.va` and comparison bench, the user page | MC-27 |

MC-1/MC-2, MC-3/MC-4/MC-5 and MC-6 are three independent tracks. MC-7 and MC-8 can run side by side.

---

## 2. Locked decisions

### D1 — Names and files
- The tool is **Model Composer**; menu entry **Tools ▸ Model Composer…**.
- **`.cmc`** — the composer document: JSON text with `FormatVersion`, in circuitRF's usual style.
- **`.cmodel`** — the exported model, and the file the simulator loads. It is a **ZIP container** holding
  `model.json` (readable: terminals, network, constituents, scaling, temperature, traps, symbol, provenance) and
  `arrays/*.npy` (tables and network weights, through RfCore's `NpyReader`/`NpyWriter`). A table can reach tens
  of MB (a grid of ~1,600 × 850 points per quantity), which as JSON text is slow to parse and large; the JSON part
  stays human-readable. `System.IO.Compression` is part of the base library — no new dependency.
- **`<name>.cmc-cache/`** — the derived data cache beside a `.cmc` (D11).

### D2 — Two kinds of document
A `.cmc` has `Kind`: **`Active`** (a three-terminal FET) or **`Passive`** (an N-port). One window serves both; the
step rail differs (§3).

### D3 — The device: `ComposedFet`, a new stand-alone component
```
ComposedFet:X1  g d s  File="gan_8x125.cmodel"  N=8
```
- Nets `g d s`, and a fourth net `t` when the chosen symbol has a fourth pin (the thermal node, MC-9).
- `File` resolves relative to the referencing document, exactly as `SnP`'s `File` does. `N` is the finger count
  (D9). `M`, the device multiplier, keeps its meaning — whole identical devices in parallel (`DeviceMultiplicity`).
- It **elaborates as a cell**: the network's elements become ordinary R, L, C and Mutual components (values scaled
  per D9), and one nonlinear **`ComposedCore`** sits on the intrinsic nets `gi di si`, plus its internal state
  nodes. So `V(X1.gi)` probes an intrinsic voltage, `X1.rg` is a component path, and every engine treats the
  network as it treats any circuit.
- Not generated SDD, because the core needs table and ANN evaluation with exact derivatives, guided extrapolation,
  internal state nodes (thermal, trap, dispersion) and allocation-free grid evaluation for HB.

### D4 — Constituents and methods
- **Constituents:** `Id` (channel current, drain to source), `Ig` (gate conduction: gate–source and gate–drain
  branches, forward and breakdown), `Qg` and `Qd` (terminal charges; `Qs = −Qg − Qd` by KCL, [P1] eq. 6.21).
  Optional: `IdRF` with the DC/RF dispersion of [P1] eq. 6.52.
- **Methods:** each constituent is independently an **Equation**, a **Table** or an **ANN**.
- **Defaults, following [P1]:**
  - `Qg`/`Qd` are ANNs trained by the **adjoint method** ([P1] §6.3.6, [X03]).
  - `Id`/`Ig` are ANNs ([P1] §6.2.7, §6.7).
  - An equation `Id` with ANN charges — the common industry practice — is one selection away.
- **Charges are always charge functions.** No capacitance-based model ([P1] §6.3.7, eq. 6.32) is offered, because
  one produces a spurious DC current in HB ([P1] Fig. 6.21; [GSF19]).
- **No transconductance-delay element.** The charges already carry the delay: `∂Qd/∂vgs` differs from
  `∂Qg/∂vds` by the transcapacitance `Cm`, which is the delay to first order (`τ = −Cm/Gm`, [P1] eqs. 5.18, 5.21).
  [P1] §5.6.5.1 presents the delay as an *alternative* to `Cm` that does not extend well to large signal; adding
  one beside the charges would count it twice.

### D5 — Extrinsic networks: the six-pin contract and the stock networks
An extrinsic network is a **`.csch` with exactly six pins**: **1** extrinsic gate, **2** extrinsic drain,
**3** extrinsic source, **4** intrinsic gate, **5** intrinsic drain, **6** intrinsic source.
- Element values are the network's **variables** (its VAR block). Every variable is a fit unknown, with a minimum,
  a maximum, a lock and an optional tie.
- **Any network the linear engine can solve is valid**: mutuals, transmission lines, an `SnP` block from an EM run.
- Pad/launch de-embedding is a separate step **before** the network. The launch is never part of the model.

Stock networks ship as embedded `.csch` resources in `src/Design/Modeling/StockNetworks/`:
1. **GaN two-shell** (owner-supplied): outer pad capacitors, series inductors with a gate–drain mutual, inner
   capacitors, series resistors. Its topology and starting values:
   ```
   Cpga = 7 fF    Cpda = 7 fF    Cpdga = 0.5 fF
   Cpgi = 4.9 fF  Cpdi = 21 fF   Cpdgi = 1 fF
   Lg = 39.5 pH   Ld = 46.4 pH   Ls = 5.25 pH   M_feedback = 1 pH
   rg = 5.2 Ohm   rd = 9.2 Ohm   rs = 6.6 Ohm

   C:C1  P1 P3  C=Cpga        L:L1  P1 n1  L=Lg       R:R1  n1 P4  R=rg
   C:C4  P1 P2  C=Cpdga       L:L2  P2 n3  L=Ld       R:R2  n3 P5  R=rd
   C:C6  P2 P3  C=Cpda        L:L3  P3 n2  L=Ls       R:R3  n2 P6  R=rs
   C:C2  n1 n2  C=Cpgi        C:C3  n1 n3  C=Cpdgi    C:C5  n3 n2  C=Cpdi
   Mutual:M1  Inductor1="L1"  Inductor2="L2"  M=M_feedback
   ```
   (P1…P6 are the six pins.) Author the `.csch` from this netlist; `netlist --toSchematic` draws a `.cnl` as a
   schematic.
2. **Classic single shell**: `Cpg`, `Cpd`, `Cpgd` at the extrinsic terminals; `Lg`, `Ld`, `Ls`; `Rg`, `Rd`, `Rs`
   (the shell structure of [P1] Fig. 5.10).
3. **Series only**: `Rg`, `Rd`, `Rs`, `Lg`, `Ld`, `Ls`.
4. **None**: the data is already intrinsic.

### D6 — Voltage reference planes
Every data set records **where its bias voltages were measured**:
- `instrument`, with a bias-path resistance per terminal for remote sensing;
- `probe` — the calibration plane, pads included;
- `device` — after pad de-embedding;
- `intrinsic` — simulated data that carries intrinsic voltages.

The composer always models in **intrinsic** voltages, computed by a DC solve of the extrinsic network with the
measured currents. That is [P1] §6.2.6.1 eq. 6.12, generalised to any network. A data set that also carries
intrinsic voltages gets the computed ones reported against them as a cross-check. Intrinsic points do not lie on a
grid ([P1] Fig. 6.5): ANNs train on them directly, and tables re-grid them.

### D7 — Charge conservation
- `ComposedCore` returns `q` and `∂q/∂v` from one function, so every model is **terminal-charge-conserving by
  construction** ([P1] §6.3.4).
- **Check Charge Conservation** runs three checks:
  - **(a) the data:** [P1] eq. 6.27 per row, as a map and an RMS %, plus the two-path line-integral difference
    ([P1] eqs. 6.30/6.31);
  - **(b) the model:** the same test on the model's capacitance matrix;
  - **(c) large signal:** an HB drive at *f* and at *2f*. The change in DC gate current isolates DC generated by
    displacement current ([P1] Fig. 6.21).
- (b) and (c) work on **any** nonlinear device in circuitRF, not only a `ComposedFet`.
- A non-reciprocal capacitance matrix (transcapacitance) is **reported, never an error** ([P1] §6.3.8).

### D8 — Temperature
- Each data set has an ambient (chuck) temperature.
- **Two or more temperatures:** temperature is an input of every constituent; the model follows the elaborator's
  `temp`; self-heating (Rth, Cth, the thermal node) is available. Outside the data's range, temperature is
  clamped, with one warning per run.
- **Exactly one temperature:** the model records `T0` and has **no temperature dependence**; self-heating is
  unavailable. A run whose `temp` differs from `T0` prints **one warning naming both**, and the model is used as
  it is.

### D9 — Periphery scaling
- The model records the **base finger count `N0`** and **unit gate width `W0`**. Export refuses without `N0`.
- The instance parameter **`N`** (a positive integer, default `N0`) scales:
  - the intrinsic constituents by `N/N0`;
  - each network element by its **rule**: `fixed`, `perFinger` (× N/N0) or `perFingerInverse` (× N0/N);
  - `Rth` by N0/N and `Cth` by N/N0. Trap time constants are unscaled.
- **Defaults for the GaN network:** `rg rd rs` → `perFingerInverse`; `Cpgi Cpdi Cpdgi` → `perFinger`;
  `Cpga Cpda Cpdga Lg Ld Ls M_feedback` → `fixed`. Every rule is editable per element.
- Rules are **names** in the `.cmodel`. An unknown name is a refusal that names it — room for later rules: the
  power law of [P1] eq. 5.9, unit-width scaling ([T20]), and **affine** rules (a fixed part plus parts
  proportional to N, 1/N, W·N, W/N or 1/(W·N), and a source-via term W/(N/2 + 1) — [L26] eqs. 21–30), which need
  data from two device sizes (pad capacitances, `Rg`, `Rd`) or three (`Lg`, `Ld`, `Ls`, `Rs`) ([L26] §III-B).

### D10 — Traps
- A setting with three values:
  - **`None`** (default);
  - **`CaptureEmission`** ([P1] eqs. 6.70–6.74, [X10] eqs. 1.3–1.4): a gate-lag state φ1 and a drain-lag state
    φ2, each switchable, with fast diode-like capture and RC emission; the emission time constant is either
    constant or thermally activated (Arrhenius, [N22] eq. 3);
  - **`Custom`**: the state equations and their effect, written as expressions in the one expression engine.
- Every trap model implements **one contract**: its state variables, their equations, and how they enter `Id`/`Ig`.
  A future built-in (the Shockley–Read–Hall form of [G19] eqs. 2–5, whose capture slows as the trap fills, say)
  adds a name and an implementation, nothing else.
- Trap states are inputs of **`Id`**, and of **`Qd`** when pulsed S-parameters exist ([X10]: the trap dependence
  of the drain charge is what reduces saturated power at high frequency). Without such data, `Qd` has no trap
  dependence ([P1] §6.7.3).
- `Ig` and `Qg` never take them as inputs — [X10] eq. 1.5's choice, narrower than [P1] eq. 6.70's general form.
  The one route in is MC-8's drift charge: `Qg` contains `Qdrift(vgd, Id)`, so it depends on the trap states
  **through `Id`**, as [I11]'s did through [X10]'s trapped current.
- **Trap-state training data comes from pulsed measurements; double pulse is not required** (MC-10). Each pulsed
  point is measured from a **trap-setting bias**: the pre-pulse of a double-pulse measurement ([N22], [G19]), or
  else the quiescent point. A point pulsed *toward* it (no lower `vgs`, no higher `vds`) carries exactly that
  state, because only slow emission acts during the pulse. A point pulsed *beyond* it carries a partly captured
  state the data cannot pin down, because capture is not always fast ([G19]); it is validation only unless the
  user says how to map it.
- **The data limits the model; it never blocks it.** Two or more trap-setting levels per state give the full trap
  model. One level gives a model whose trap dependence beyond that level is extrapolated, with one warning. No
  pulsed data gives a model without traps, with MC-7's eq. 6.52 dispersion and MC-9's self-heating as the routes
  to DC/RF dispersion.

### D11 — Data referenced and cached
- Sources are referenced by path **relative to the `.cmc`**.
- Parsed, normalised data is cached in `<name>.cmc-cache/`: `.npy` files plus an `index.json` with each source's
  size, modification time and SHA-256.
- On open:
  - a **changed** source is re-imported, with one status line;
  - a **missing** source with a valid cache keeps working, with one warning;
  - a missing source with no cache is a refusal that names it.
- The cache is derived and may be deleted.

### D12 — Export
- **Active → `.cmodel`** (D1). Stored in it:
  - the **symbol**, chosen at export from library symbols or a workspace `.csym`. Pins 1/2/3 are G/D/S; an
    optional pin 4 is the thermal node. Placing an instance copies the symbol into it, and the Inspector changes it
    per instance.
  - the network and the constituents;
  - the scaling rules, `N0` and `W0`;
  - the temperature range and the trap setting;
  - provenance: source fingerprints, settings and the circuitRF version.
- **Passive → a cell**, created through `CellCreate` (the function New Cell calls): a symbol and a schematic of the
  fitted network. Element values are cell parameters, and so are a family fit's geometry parameters (MC-12).

### D13 — Background work
- Every long operation is a function in `src/Design/Modeling` that takes a **`RunControl`** (progress, stage,
  cancellation). That covers import, de-embedding, fits, ANN training, charge integration, the conservation
  checks, validation runs and export.
- The window runs it off the UI thread and shows **one progress bar with Cancel** in the status strip.
- **A cancelled operation leaves the previous result untouched.**

### D14 — Architecture
- `src/Core/Devices/Composed/` — the `ComposedFet` elaboration, `ComposedCore`, the `.cmodel` reader, writer and
  validator, the runtime evaluators (equation, table, ANN), guided extrapolation, scaling.
- `src/Engine/Modeling/` — numerics with no domain types: MLP training (value and adjoint), L-BFGS, spline fitting
  and re-gridding, line integrals, conservation metrics, network-parameter algebra (S/Y/Z, de-embedding), vector
  fitting.
- `src/Design/Modeling/` — the `.cmc` document and its reader/writer, import and the cache, pad and network
  de-embedding, re-referencing, the fit flows, thermal and trap extraction, validation, export, passive modelling,
  and the stock networks.
- `src/Ui/ModelComposer/` and `src/Ui/Views/ModelComposer/` — the window only.
- `src/Cli/Model.cs` — the `model` verb; its MCP tool goes in `src/Cli/Serve/ToolCatalog.cs`.
- No new project, so `tests/Firewall.Tests` needs no new entry; nothing new crosses the UI firewall.

### D15 — Headless parity
Every operation has a `model` noun (MC-13). The GUI and the CLI call **the same function**. A comment-stripped
source scan keeps the view models free of a second copy — the pattern of `AuthoringCliVerbTests`.

### D16 — Exit codes
**0** done · **1** refusal · **3** finished, but a check failed (charge conservation above `--threshold`, or a
validation tolerance missed) · **130** cancelled, and nothing written.

### D17 — Verilog-A export: no tables
A composed FET exports as one self-contained Verilog-A module as well as a `.cmodel`. A model with any **table**
constituent is refused, by name. The compiler circuitRF uses accepts neither arrays nor `$table_model`, and
`$table_model` would not reproduce the not-a-knot spline anyway. Equation and ANN constituents are written as code and
must match the `ComposedFET` to rounding.

### D18 — Verilog-A export: parameters and the bias point
Every instance parameter and switch of a `ComposedFET` is a module parameter with the same meaning, except `File`,
`Symbol` and `m`. The host simulator's own multiplier is `m`. A module cannot ask its host for the quiescent operating
point, so the linear-charge switches freeze at **`VgsQ`, `VdsQ` (intrinsic) and `TjQ`, given as parameters**. With a
switch on and the voltages unset, the run stops with a message rather than guessing.

---

## 3. UX principles for the window

- **One window per `.cmc`**, non-modal, golden-ratio opening size, as railRF. A dirty bullet in the title; closing
  with unsaved work asks, through the dialog railRF and wBond use.
- A **top toolbar of icon buttons**, each with a tooltip, in the schematic/layout/3D editors' style. **Panel lamps**
  (railRF §11.3 item 5) are persisted in the `.cmc`. Every panel resizes horizontally and vertically through
  splitters.
- A **step rail**, icons with short labels:
  - Active: Data · Pads · Network · Current · Charge · Thermal · Traps · Check · Validate · Export.
  - Passive: Data · Topology · Fit · Export.
- **Plots are `PlotControl`s fed from `DataSet`s**; maps use the existing contour and surface renderers
  (`src/Render/DataDisplay/Renderers/`).
- The **network panel is the schematic canvas**, reused the way the Smith Chart tool reuses it, with `.csch` copy
  and paste in both directions.
- A **bias cursor** on the toolbar (Vgs, Vds, temperature, quiescent point) drives every plot.
- **Minimal text.** A one-line status, warning or error in the status strip; no explanatory prose anywhere; a
  computed value is shown as a value. Never the phrase "the design's own" in UI text.
- Centred text on buttons and in comboboxes (railRF §11.2).
- Every edit is one undo step.
- **This layout is a first iteration**, and the owner will revise it. Build each panel as its own view, so that
  rearranging them is cheap.

---

## 4. Testing rules for the whole series

- The **minimal** tests: one per claim, not per rung; trim `InlineData`.
- Run **only the classes the phase adds or touches** (`--filter "FullyQualifiedName~<Class>"`); never the full
  suite, never all of `Ui.Tests`. Rebuild each test project before `--no-build`.
- **No new timing benchmark tests.** A performance or responsiveness claim asserts a **counter** (evaluations,
  allocations avoided, UI-thread work items).
- **Ground truth is a synthetic device**, written as an **SDD** — independent of the composer's code — and simulated
  by circuitRF to produce the "measurements": DC I–V, pulsed I–V from ≥ 3 quiescent points (one set double-pulse,
  and some points pulsed beyond the trap-setting bias at a partly captured state), multi-bias S at 2–3
  temperatures, embedded in the GaN network and a pad model. One shared fixture, `SyntheticFetData`
  (`tests/Ui.Tests/ModelComposer/Support/`), builds it in memory: ≤ 200 bias points, ≤ 50 frequencies.
- Real-device acceptance uses **local-only fixtures**, skipped with a reason when absent (the `FixtureFact` pattern,
  `tests/Harmonica.Tests/Support/`). Owner data is never committed and never named in commits or docs.
- **Determinism:** seeded everywhere; the same seed gives the same weights, bit for bit, single-threaded.
- Test locations: device and evaluators in `tests/Core.Tests/Composed/`; numerics in `tests/Engine.Tests/Modeling/`;
  document, flows, CLI and window in `tests/Ui.Tests/ModelComposer/`.

---

## 5. Housekeeping for every phase

- Findings go in the relevant `RESOLVED.md`, never in a `CLAUDE.md`. Paraphrase the owner; never quote.
- **No commercial vendor, product or PDK names** anywhere — code, docs, tests, commit text — and not as a glossary
  of names to avoid. Say "a commercial simulator", "a foundry kit".
- Never reference the owner's data files or local paths in the repo.
- No commits unless the owner asks; commits go to `main`.
- Edit `docs/user/src` sources where a phase changes user-visible behaviour; **do not run DocGen** — the owner
  regenerates at the end of the series.
- All algorithms written in-house under MIT; no GPL or copyleft code, none copied.
