# Brief ICP-2a — The Uncalibrated Port: an edge port at a face, not de-embedded

**Series:** ICP-0 … ICP-9 (read `brief-icp-0-overview.md` first) · **Tag:** `R-icp2a-<n>` · **Depends on:** — ·
**Needed by:** ICP-2 (its ports at a cut face)
**Area:** `src/Engine/Mom/PlanarPort.cs` (`PlanarPort`, `PlanarPortResolution`), `PlanarFeedExtension.cs`, `PlanarSolve.cs`
(`PlanarSolveContext.Excite`, the calibration notes), `PlanarGroundedEdgePort.cs`; `src/Design/Layout/LayoutModel.cs`
(`LabelShape`), its copy sites, `EmPortExtraction.cs`, `EmSnpProvenance.cs`; `src/Ui/ViewModels/LayoutShapePropertiesViewModel.cs`
and the port label's Properties rows.

## The gap

ICP-2's measurement (`src/Design/RESOLVED.md` §brief-icp-2 — the measurement) found that a calibrated edge port cannot sit at
a device's pins. Every calibrated port's feed must be clear of other metal for the run of line its calibration standard
reproduces — 5 substrate heights from a neighbour carrying a port, 2 from one carrying none, i.e. 500 µm and 200 µm on
100 µm GaAs — and a pin on a perpendicular side cannot be grouped into one calibration. A 3-pin site was refused at 20, 40
and 80 µm, and grounding the third pin did not help.

ICP-0 named the fallback: a port at the face that is not de-embedded. The engine nearly has it. LFE's grounded edge port
(`PlanarEdgePortReturn.Ground`) drives the conductor's end face against the ground plane; solved with no calibration it is
a lumped port with its launch in the answer, not the open circuit a raw series-gap edge port is. Measured on isolated
lines against the calibrated answer of the same layout, 5–30 GHz: within 1.3e-2 … 2.7e-2 on a 72 µm line, 5.4e-3 … 7.0e-2
on a 10 µm line; passive at every point; 3–8× faster because no standards are solved. The 3-pin site at 40 µm solves
passive and reciprocal to 1e-8.

## Decisions

- **D1 — Its name is "Uncalibrated Port"**, in the Properties box, the run notes, the Touchstone, the docs and the CLI. The
  word says what the user is giving up; "raw", "lumped" or "internal" each name something else in this application.
- **D2 — It is an EDGE port with a flag, not a new port kind.** Every place that asks `Kind == Edge` (resolution, the
  severed-conductor check, level inference, the layout's port arrow) keeps working; only the calibration and the feed lead
  skip it. `PlanarPortResolution.IsDeembeddable` is the one place that says so, as it is for `DeembedDeclined`.
- **D3 — It always returns through the ground plane**, at every frequency and whatever `EdgePortReturn` says. The
  crossover that limits the grounded return (`GroundedEdgePortBelowHz`) is about calibration standards' short lines
  coupling, and this port has none; a series-gap edge port uncalibrated is an open circuit.
- **D4 — Reported at its cut, in its declared Z0** (as `DeembedDeclined` and an internal port are). No feed lead is grown,
  no clearance is measured, no standard is built, and a neighbour never refuses it.
- **D5 — Only an edge port can be uncalibrated.** An internal port and an internal delta gap are never calibrated anyway;
  the flag on one is ignored with a note. On a 3D run every edge port is already a lumped sheet, so the flag changes
  nothing there and is said once.
- **D6 — Existing results do not move.** A layout with no uncalibrated port solves, hashes and writes byte-identically.

## Requirements

**R-icp2a-1 — Engine.** `PlanarPort.Uncalibrated` (init, default false) carries to `PlanarPortResolution.Uncalibrated` at
resolution. `IsDeembeddable` is false for it. `PlanarFeedExtension` grows no lead for it and does not count it as a peer.
`PlanarSolveContext` grounds it at every frequency: at a frequency where the run grounds every edge port nothing changes;
where it does not, the grounded update (`PlanarGroundedEdgePort.Solve`) is applied to the uncalibrated ports alone, the
others staying series gaps. The calibration notes list the uncalibrated ports in one line of their own and say what the
answer carries at them (the half cell beyond the cut and the grounded source's own lumped launch).

**R-icp2a-2 — The label.** `LabelShape.PortUncalibrated` (`.clay` key, omitted when false, no `FormatVersion` bump),
copied wherever a label is copied (`LayoutGeometry`'s clone, the renderer's two copies, `LayoutPortDirection`).
`EmPortExtraction` sets `PlanarPort.Uncalibrated` from it for an edge port and notes it per port.

**R-icp2a-3 — The file says so.** The Touchstone's per-port line reads `edge, uncalibrated (not de-embedded)` where it
read `edge, de-embedded`, and the port-set hash takes `:U` for an uncalibrated port (appended only then, so an existing
`.snp` stays current). The `.cemr` port record carries the same word.

**R-icp2a-4 — The Properties box.** An edge port label gets an "Uncalibrated port" check box beside "Use edge width",
one undo entry per change, its tooltip one sentence: the port is not de-embedded, so its launch is in the result, and it
can sit where a calibrated port's feed would be refused. It does not appear for an internal port (no control shifts: the
row's space is the existing port-options block's). Visual check through UiSnapshot.

**R-icp2a-5 — ICP-2 builds it.** ICP-2's cut-face ports are Uncalibrated Ports, always: one kind per run of cut ports,
predictable, and never refused for a neighbour. ICP-2's gate 2 (the reference plane) is re-pointed at the uncalibrated
face, with its tolerance from this brief's measurement.

## Gates

At most 6 points, the shipped MMIC technology, one test per claim:
1. A 10 µm line with both edge ports uncalibrated solves passive and within the measured bound of the calibrated answer
   (7e-2 to 30 GHz, the RESOLVED table); no standard is solved (the run's notes say none).
2. Three lines meeting a vacated site from three sides (40 µm lines, an 80 µm site, edge mesh off): calibrated, refused;
   uncalibrated, solved — the device-pitch case, at a mesh a routine test can afford.
3. A run with `EdgePortReturn = SeriesGap` still grounds an uncalibrated port (its S equals the grounded run's to 1e-12).
4. A layout with no uncalibrated port: S and the `.snp` byte-identical to before.
5. The `.clay` round-trips with and without the key; the Touchstone line and the hash say uncalibrated only when one is.
6. The view-model: the check box writes the key and one undo entry, and is absent for an internal port.

## Not here

3D (nothing to change, D5). Any change to how a calibrated port is calibrated.

## Completion note (2026-11-13)

Built as written: `PlanarPort.Uncalibrated`/`PlanarPortResolution.Uncalibrated`, the feed extension skipping it, the grounded
update applied to it at every frequency, its own note, `LabelShape.PortUncalibrated`, the Touchstone line, the port hash and the
`.cemr` field, and the "Uncalibrated port" box in Properties (checked by UiSnapshot). Gate `tests/Ui.Tests/Em/UncalibratedPortTests.cs`,
five tests. Gate 1 runs at 20 GHz alone; the 72 µm line reads 5.6e-2 there against the 7e-2 bound.
