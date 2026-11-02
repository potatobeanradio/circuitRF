# Brief MC-24 — harmonicaRF's loadline at the intrinsic or the extrinsic plane

**Series:** `brief-modelcomposer-0-overview.md` · **Tag:** `R-mc24-<m>` · **Depends on:** MC-23 for the composed case
(the rest applies to every DUT kind and can land first) · **Read with:** `docs/design/harmonicarf.md` §4.5, §5, §6.2,
§6.8, §7.3
**Area:** `src/Harmonica/` (`HarmonicaNetlist`, `InterfaceNetwork`, `IntrinsicPlane` or a sibling `ExtrinsicPlane`,
`DcivFamily`, `HarmonicaDataSet`, `CharmIo`), `src/Ui/Harmonica/HarmonicaSolver.cs` (`BuildLoadline`),
`HarmonicaViewModel`, the panel renderer; `docs/user/src/reference/harmonicarf.md`; `docs/design/harmonicarf.md`
**Tests:** `tests/Harmonica.Tests`

---

## 1. Goal

§7.3 promises a **plane toggle**: the DCIV family and the loadline move together between intrinsic and extrinsic, and
the panel always says which is shown. The toggle exists (`ToggleLoadlinePlane`, `HarmonicaViewModel.IntrinsicPlane`,
`HarmonicaFrame.PlaneLabel`), but only the **label** follows it. `HarmonicaSolver.BuildLoadline` always reads
`IntrinsicPlane.Loadline`, and `DcivFamily` is always intrinsic. So a user who switches to "extrinsic" sees intrinsic
curves under an extrinsic label, which is a wrong answer under a right label. This brief makes the extrinsic plane real
for every DUT kind. A `.cmodel` is the case where the two planes differ most: Rg/Rd/Rs, Lg/Ld/Ls and the pad
capacitances sit between them.

## 2. Requirements

**R-mc24-1 — What "extrinsic" is.** The **DUT's own terminals**, the plane `HarmonicaNetlist` already calls the
package plane (inside any `LumpedPackage` and embedding). For a `.cmodel` that is the reference plane the model was
extracted at, after the pads, so it is the plane where a designer's measured I–V lives.
- The extrinsic voltage is `vds,ext(t) = V(drain) − V(source)` at those terminals.
- The extrinsic current is the **total** terminal drain current: conduction, displacement and any network branch.
- Intrinsic stays §4.5's conduction current at the device's ports (`DutDevice`, MC-23).
- For a native FET or an SDD with no capacitance, the two planes coincide in voltage, and the currents differ by `jωq`
  exactly as §4.5 consequence 1 says. That is informative, not a defect.

**R-mc24-2 — Recovering the terminals' state.** The pre-terminated interface (§6.2) keeps only device-facing nodes and
the two termination planes. Recover the DUT-terminal voltages and the drain-terminal current from the converged
spectrum through the same factorisation, as `InterfaceNetwork.PlaneState` already does for the termination planes.
Recommended route: put an `IProbe` in series with the DUT's drain (and gate, for the gate-side terminal readouts) in
`HarmonicaNetlist`, and recover node voltages and the probe's branch current the way `HbLinearBackSolver` does for an
ordinary HB run. Any equivalent route will do, provided it passes the truth gate. The probes are zero-volt branches,
so they change no answer: hold that with a gate that compares a solve before and after they were added.

**R-mc24-3 — The extrinsic loadline.** `IntrinsicPlane.Loadline`'s extrinsic counterpart synthesises
`HarmonicaSettings.LoadlineSamples` time samples from the recovered harmonics, by the same direct sum R-h9b-13 uses
(not `HbFft.Inverse`). `BuildLoadline` reads the plane it is given. For the extrinsic plane it needs no intrinsic map, so
an external DUT with no mapping still draws its extrinsic loadline, and its intrinsic one stays empty as today.
Publish `Vds_ext_t` / `Ids_ext_t` beside `Vds_intr_t` / `Ids_intr_t`, both always present, and add both to the trace
picker's descriptions.

**R-mc24-4 — The extrinsic DCIV family.** DC I–V at the DUT terminals, for each Vgs over the Vds sweep. Where the two
planes coincide at DC (no internal network: native FET, SDD, external with no series elements stated), reuse the
intrinsic family and count no extra computation. Otherwise (a `.cmodel`, or any DUT with internal series resistance)
solve it with `NonlinearDcEngine` on a two-source bench of the DUT alone. Use continuation along Vds from the previous
point, and take the self-heating, trap and dispersion DC values the model itself gives. `DcivFamily.Key` gains the plane
and stays tier C: computed once per key and held. A failed point is a gap in the curve, never an interpolated value.
Keep `DcivComputeCount`, and extend it per plane.

**R-mc24-5 — One toggle, both curves, always labelled.** The existing toggle drives both the family and the loadline
(§7.3: never one without the other). The persistent plane indicator stays, and the axis titles name the plane (`Vds,i` /
`Ids,i` vs `Vds` / `Id`). The plane is persisted in the `.charm`'s view preferences, defaulting to intrinsic. A document
written without it opens intrinsic. A plane that cannot be drawn (intrinsic on an unmapped external model) keeps its
existing empty-with-reason overlay.

**R-mc24-6 — Cost.** The extrinsic loadline is a product per harmonic on the solve already done. It must not add an HB
solve per frame. Hold this with a counter (solves per frame unchanged when toggling), not a timing test.

**R-mc24-7 — Docs** (sources only). In `harmonicarf.md` (user), the loadline section says what each plane shows, and
why at the extrinsic plane the current includes the capacitive and network current. Add one sentence for a `.cmodel`:
the extrinsic plane is where its measurements were taken. The harmonicaRF design note's §7.3 is brought in line, and §5
lists the new cubes.

## 3. Not in this phase
A DCIV family at the **operating** trap state and temperature (the dynamic I–V the RF loadline actually moves on, as
distinct from the DC family). It is a natural follow-on for GaN and is listed for the owner (§5).

## 4. Gates
- `ExtrinsicLoadlineTests`:
  - **truth**: on the `synthetic-gan` `.cmodel` DUT with a nonzero package, the recovered `V(drain) − V(source)` and
    the drain-terminal current at every harmonic equal a full `HbEngine` solve of the same netlist (node voltages and an
    `IProbe` branch current) to 1e-9 relative;
  - adding the probes changes no published cube (bit-identical `Pout`, `DE`, intrinsic spectra);
  - a native FET with charge off draws identical intrinsic and extrinsic loadlines, and with charge on they differ by
    `jωq`, checked per harmonic.
- `DcivPlaneTests`:
  - the composed extrinsic family at three points equals `NonlinearDcEngine` on the DUT bench to 1e-9;
  - a native FET's extrinsic family is the intrinsic one with no extra computation (`DcivComputeCount`);
  - toggling the plane twice computes each family once.
- `.charm` round trip of the plane; a pre-existing document opens intrinsic.
- Toggling adds no HB solve (the solve counter).
- Run `tests/Harmonica.Tests` class-filtered to these plus `LoadlineSamplesTests`, `LoadlineHarmonicContentTests`,
  `DcivFamilyTests`, `ContextAndPersistenceTests`; `Firewall.Tests`.

## 5. Open for the owner
- Extrinsic = the DUT's own terminals (chosen). The alternative is the termination plane, beyond any package and
  embedding: that is what a load-pull system measures when the fixture is not de-embedded.
- An "at the operating state" DCIV family, as §3 describes.
