# Brief CP-7 — Planar results as EM Field Data blocks

**Series:** `brief-cosim-pattern-0-overview.md` · **Tag:** `R-cp7-<n>` · **Depends on:** CP-3
**Area:** `src/Design/Em3d/{PortFarFieldSet,FieldCosimulation}.cs`, `src/Design/Layout/Em/EmRunService.cs`, `src/Engine/Mom/`, `docs/user/src/reference/{antennas,cosimulation}.md`

## 0. Why

A planar run with **Radiation pattern** publishes the same `farfield` cubes, with a port axis, through the same `FarFieldStage`. An edge-fed
patch tuned by a matching network or a part across an in-plane gap has the same need as a 3D antenna, and plots and metrics carry over.
The stored excitation is different, so CP-3's sum does not apply to it.

## 1. Requirements

**R-cp7-1 — Find out first, and report.** Before any change, answer these in `RESOLVED.md`:
- **The weights.** A planar pattern holds its driven port's delta gap at 1 V and every other port's gap at 0 V (`PlanarFarFieldPattern`'s
  summary), not terminated in Z0. So the sum is expected to be `E = Σ_k V_k · E_k`, with no S term. Confirm against `PlanarFarField` and
  `PlanarCurrentDensity`.
- **De-embedding.** The block's S appears to be de-embedded to a reference plane while the pattern is driven at the gap. If so, the
  circuit's voltage at a block port is not that gap's voltage, and the weights need the calibration's transform. Say whether the run keeps
  what that transform needs, per port and per frequency. **If it does not, stop and report the options**; do not ship a weight that
  ignores it.
- **Which ports.** Which planar port kinds a pattern accepts today (an `Internal` probe is refused) and whether a gap between two
  in-plane pads can be a port at all. Component ports are built only in a 3D view.
- **Multiple ports.** No antenna fixture to date has more than one port, so confirm the port axis with a real two-port.

**R-cp7-2 — The record.** CP-1 already writes a record for a planar run. Add what R-cp7-1 needs, and the record's excitation kind (`TerminatedZ0`,
`ShortedOthers`), which `PortFarFieldSet` dispatches on. The 3D sum and the planar one share `Weigh`'s wave and voltage inputs.

**R-cp7-3 — The grid and the budget.** A planar pattern is a hemisphere, and its metrics carry surface-wave and dielectric-and-ground terms.
Say how each power term behaves when summed. A term that cannot be summed is omitted by name and never published as a stale per-port value.

## 2. Gates

Measured once with a scratch harness, keeping runs short: a two-port planar antenna, one port driving and one loaded with a resistor,
recombined, against a planar solve where that load is real, if the kernel can state one, or else against the 3D result of the same
structure. Limits as CP-3's G4. If the check cannot be made independently, report that rather than calling it passed.
