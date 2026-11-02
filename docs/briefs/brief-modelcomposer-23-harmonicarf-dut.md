# Brief MC-23 — A `.cmodel` as harmonicaRF's DUT, read at its intrinsic plane

**Series:** `brief-modelcomposer-0-overview.md` (D3, D14) · **Tag:** `R-mc23-<m>` · **Depends on:** MC-21 (MC-22 for the
branch switches' rows) · **Read with:** `docs/design/harmonicarf.md` §4.3, §4.5, §6.6, §8.1 and `src/Harmonica/CLAUDE.md`
**Area:** `src/Harmonica/` (`CircuitModel`/`DutSpec`, `HarmonicaNetlist`, `HarmonicaContext`, `IntrinsicPortMap`,
`IntrinsicPlane`, `DcivFamily`, `CharmIo`, `PaClassPresets`), `src/Ui/Harmonica/` and
`src/Ui/Views/Dialogs/HarmonicaSetDutDialog.*`; `docs/user/src/reference/harmonicarf.md`, `model-composer.md`;
`docs/design/harmonicarf.md`, `docs/design/model-composer.md`
**Tests:** `tests/Harmonica.Tests` (and `tests/Ui.Tests` for the dialog view model)

---

## 1. Goal

harmonicaRF exists to show what the **intrinsic** device sees: the conduction-current loadline, the intrinsic
termination glyphs, and drags of those glyphs. A Model Composer model is the device a designer actually measured, and
its `.cmodel` already **knows** where its intrinsic plane is: the core sits on `gi di si`, inside the extrinsic network
the composer fitted and stripped. So harmonicaRF should take a `.cmodel` as its DUT, read the intrinsic quantities at
the core without asking the user to name nodes (the step an external model needs, §4.5.5), and offer MC-21/22's
switches live. A designer toggles **No Charge**, **Linear Cdg** or **No Drain Lag** and watches the loadline, the
contours and the power sweep move.

## 2. Requirements

**R-mc23-1 — A new DUT kind.** `DutKind.Composed`. `DutSpec` gains `ModelFile` (the `.cmodel` path), `Fingers` (`N`,
null = the model's `N0`) and `Switches` (`ComposedSwitches`, MC-21). `Multiplicity` is `m` as today. `StructuralKey`
includes the file's path and last-write stamp, `N` and the switches, so a toggle is a structural change and rebuilds.
`TypeName`, `Parameters`, `SddText`, `IntrinsicMapping` and `Capacitances` do not apply to it. `Capacitances` (R7D) is
SDD-only and stays so: a composed model carries its own charges.

**R-mc23-2 — The netlist.** `HarmonicaNetlist.DutLine` emits `ComposedFet:DUT  gate drain source  File="…" N=… m=…` plus
`ComposedSwitches.Write()`. Three nets, so a self-heating model uses its own internal `Rth ∥ Cth`. The source is grounded
at the package plane, as for every other kind (§4.3).

**R-mc23-3 — Finding the device.** `HarmonicaContext.DutComponent` finds the component whose `InstancePath` is
`DUT`. For a composed DUT that is a cell, so the nonlinear device is the core at
`DUT.<ComposedNetworkTemplate.CoreInstanceName>`. Add a `DutDevice` that returns the **core** for `Composed` and the DUT
itself for every other kind, and move every intrinsic reader onto it: `IntrinsicPlane`, `HarmonicaDataSet.Intrinsic`,
`DcivFamily`, the Idq solve, the glyphs, the inverse solve, `Reachability`. Find them with `grep -n DutComponent`.
Anything that really means the DUT's **terminals** keeps `DutComponent` (MC-24 adds readers of that kind).

**R-mc23-4 — The intrinsic plane, with no mapping.** `IntrinsicPortMap.For` returns `TwoPort` for `Composed`. The core's
port 0 is `(gi, si)` and port 1 is `(di, si)`, already pair-referenced, so the §4.5.1 load ratio and the §4.5.3 `J′`
source route both apply exactly. The external-model refusal about a lifted source does not apply. The core has
**more than two ports**: the dispersion node, trap states and the thermal node (`ComposedCoreModel.PortCount`).
- `IntrinsicPlane.Evaluate`, `Loadline`, `LoadImpedance` and `SourceImpedance` must read ports 0 and 1 only for V and I.
- The `J′` route must treat the state and thermal ports as the internal HB unknowns they are.
- Prove both with the gates. Do not assume them.

**R-mc23-5 — The DCIV family for a composed DUT.** `DcivFamily` evaluates the device directly at DC port voltages
(§7.3, tier C). For a composed core, a DC point also needs its state ports at their DC values:
- `x = IdDC − IdRF`;
- trap states from `ComposedCoreProgram.DcSeeds`;
- the thermal rise from `ΔT = Rth,DC · P(vgs, vds, ΔT)`, solved per point by a scalar Newton, where `Rth,DC` is the sum
  of the model's Foster ladder at the run's ambient. With `SelfHeating=false` it is zero.

The family is then the intrinsic **DC** I–V, the quantity the loadline is drawn against. `DcivFamily.Key` includes
the switches.

**R-mc23-6 — Bias at two planes.** harmonicaRF's Vgs/Vdd (or Idq) are applied at the package plane. With a composed DUT,
the extrinsic Rg/Rd/Rs make the intrinsic DC bias differ from it. The readout strip shows the intrinsic DC bias
(`Vgs,i`, `Vds,i`) beside the applied one whenever the DUT has an internal network. Publish both in the DataSet
(`Vgs_intr_dc`, `Vds_intr_dc`).

**R-mc23-7 — The switches, live.** The DUT panel (and Set DUT) shows MC-21/22's switches as checkboxes under the
model's file and `N`. A switch the loaded model cannot honour is disabled, with its MC-21 R-mc21-2 note as its tooltip.
A toggle rebuilds and re-solves like any other structural change, with the frame scheduler's usual cancellation. The
panel's title line carries `ComposedSwitches.Describe()` whenever it is not the default. A screen showing a model with
its charge off must say so.

**R-mc23-8 — The bias freeze.** harmonicaRF calls MC-21's `BiasFreeze.Ensure` wherever it builds or re-biases a
netlist, including the in-place `VdcOverride` path and the Idq search. Every solve-pool thread then evaluates a core
frozen at the current quiescent point, before the pool starts and never during a solve.

**R-mc23-9 — Persistence and loading.**
- `.charm` stores a composed DUT **by reference** (§8.1's rule for a file the document does not own): the path relative
  to the `.charm` when it lies under the same folder tree, otherwise absolute, plus `N`, `m` and the non-default
  switches.
- A missing file takes the existing "model" re-point offer (`CharmIo`'s missing-artifact path).
- Set DUT gains a **Model Composer (.cmodel)** kind with a file picker.
- A `.cmodel` dropped on the window, or a placed ComposedFet dragged from a schematic, sets it.
- `Capacitances` and the SDD editor are hidden for this kind.

**R-mc23-10 — Class presets and intrinsic drags.**
- **Presets** (`PaClassPresets`) stay "never a solver" (owner rule). For a composed DUT they use the drain-side
  transform MC-11's report already uses: the intrinsic class load, with the linearised output capacitance `C22` taken
  off at the intrinsic DC bias, mapped through the model's network with both gates AC-shorted. Move that transform from
  `src/Design/Modeling/ModelValidationReport` down into `src/Harmonica`, where the report then calls it (Design already
  references Harmonica). A preset applied this way says once, in the readout, that `C22` was linearised at the bias
  point.
- **Intrinsic drags.** `IntrinsicDragAllowed` stays false for a composed DUT: its network couples input and output, so no
  per-side ABCD exists, and the reason sentence must say exactly that. Offer the drag through `InverseSolve` (§6.6's HB
  route, still in the tree) for this kind only. If it is not reachable from the drag today, wire it, and keep its
  failure rule: a failed solve moves nothing. **If that wiring proves larger than this brief, stop and report rather than
  shipping a half-route.** The glyphs, the loadline and the contours do not depend on it.

**R-mc23-11 — Docs** (sources only).
- `harmonicarf.md` (user): the new DUT kind; that a `.cmodel`'s intrinsic plane needs no naming; the switches panel; the
  two bias readouts.
- `model-composer.md`: a short **In harmonicaRF** subsection under `#switches`, linked both ways.
- The harmonicaRF design note: §4.3 (DUT kinds), §4.5.5 (composed = located by construction), §8.1 (reference).

## 3. Not in this phase
The extrinsic loadline and extrinsic DCIV family (MC-24). Two-tone in harmonicaRF.

## 4. Gates
- `ComposedDutTests` (Harmonica.Tests), on the example's `synthetic-gan` `.cmodel`:
  - the netlist line round-trips and elaborates; `DutDevice` is the core, and `IntrinsicPortMap` is `TwoPort` with both
    sides available;
  - **intrinsic truth**: at one converged point, harmonicaRF's intrinsic `V_d,k`, `I_d,k^cond` and `Z_L,intr(k)` equal a
    full `HbEngine` solve of the same netlist read at the core's ports (conduction half from the core's own `i`), to
    1e-9 relative;
  - the `J′` source impedance with the trap and thermal ports present equals a direct perturbation solve to 1e-6;
  - the DCIV family at three points equals `NonlinearDcEngine` on a bench of the core alone, with ideal sources at the
    intrinsic nodes, self-heating included, to 1e-9.
- `ComposedDutSwitchTests`:
  - toggling `Charge` changes `StructuralKey` and rebuilds;
  - an Idq search re-freezes (`FreezeCount` rises) and `LinearC` S-parameters at the new bias still equal the nonlinear
    model's to 1e-12;
  - with `Charge=false` and no extrinsic network capacitors the intrinsic glyph coincides with the drain marker (§4.5
    consequence 1) — use a model with a series-only network.
- `.charm` round trip: relative path, a re-point offer on a missing file, the switches preserved.
- Presets: the moved transform gives the report's numbers bit for bit (the report's existing gate still passes).
- Drag (if wired): one load-side drag on the composed DUT converges and a failed one moves nothing.
- Run `tests/Harmonica.Tests` class-filtered to these plus `IntrinsicPlaneTests`, `DcivFamilyTests`, `IdqVgsSolveTests`,
  `PaClassPresetsTests`, `ContextAndPersistenceTests`; the report's gate; `Firewall.Tests`.
