# Brief LCP-1 — A component port on the Planar solver: a strip and a delta gap

**Series:** LCP-0 … LCP-8 (read `brief-lcp-0-overview.md` first) · **Tag:** `R-lcp1-<n>`
**Area:** `src/Design/ThreeD/C3dComponentPorts.cs`, `src/Design/Layout/Em/EmComponentPorts.cs`, `EmGeometry.cs`, `EmRunService.cs`,
`EmSnpProvenance.cs`, a new `src/Design/Layout/Em/EmPlanarComponentPorts.cs`. No UI, no CLI surface.

## The gap

`EmSetup.ComponentPorts` means nothing to a Planar run. The solver cannot span a slot: every polygon edge is a hard gridline and a
cell exists only where a grid row's centre is inside metal, so a gap between two pads always owns an empty row, and refining
adds rows (`brief-em-return-plane-2a`, §1; `src/Engine/Mom/RESOLVED.md`). A port across a part needs metal to cut. The overview's
measurement shows a strip with an `InternalDeltaGap` across it gives the right polarity and the right low-frequency loop, and
that shorted it equals solid metal exactly.

## Requirements

**R-lcp1-1 — Part pads without a `.c3d`.** `C3dComponentPorts`' private `Board`, `Part` and `Pad` read a `C3dInstanceFrame`. Extract
the projection into `src/Design/Layout/Em` so it takes a `LayoutView`, its path and its technology, with the world transform as an
input; `C3dComponentPorts` then calls it with the frame's transform. Extract the entry selection the same way (`"*"`, an entry
beside `"*"` as an override, a part named twice, an unplaced part). `ComponentPortBuildTests` passes unchanged, and is the proof.

**R-lcp1-2 — The construction.** `EmPlanarComponentPorts.Build(EmSetup, EmLayoutSource)` returns, per wanted part, the shapes and label
to add, or why not:
- **Which pads pair** is R-emb1-5's rule, shared: one port for two pads, one per signal pad against the ground pad otherwise,
  `Reference` choosing the negative end. Same refusals, same wording.
- **The strip** is a `RectShape` on the pads' layer: the width the two pads share, or `WidthUm` centred where the pads put it,
  reaching from one pad's copper to the other's and overlapping each. Both pads must be on one layer.
- **The gap** is an `InternalDeltaGap` `LabelShape` (`IsPort`, `PortLayer`, `PortKind`, `PortDirection`) at the middle of what is left
  of the air between the two conductors across that width, not at the pads' midpoint: copper can reach into the gap (a trace's
  cap, a pour), and the cut needs metal of the strip on both sides. Reuse `C3dComponentPorts.Intervals` for the scan.
- **Direction** is from the positive pad to the negative pad along the gap's axis, worked out from the transformed pads so rotation
  and mirroring need no case of their own. The measurement used `R0` for +x and `R270` for −y and agreed with 3D at every port.

**R-lcp1-3 — Verified before meshing.** Refuse by refdes, naming what was found, when: the pads are already one conductor, so a
strip would make a loop rather than a port (R-emb1-4's "different nets"); the strip touches a third conductor; the pads do not face
each other across one axis (the wording `Facing` already has, so a 45° part is left out exactly as in 3D); the pads are not on an
analysis level of this setup; the part lies outside `SolveRegion`; the gap's row reaches beyond the strip into other metal.
**That last one needs looking at first:** read how `PlanarPort.cs` sizes the rooftop row of an internal gap
(around `internalGap`, line ~1766) on a strip abutting wider metal. If the row can widen past the strip, that is a refusal here, or
a fix in the placement of the cut, and the brief's first finding.

**R-lcp1-4 — One entry point for the shapes.** `EmGeometry.ForSetup` and `EmComponentPorts.Collect` are where every caller gets
shapes and labels, but `Collect` takes no setup. Add `Collect(source, setup)`, and move these call sites to it: `EmRunService.cs:278`,
`:380`, `:1026`, `EmGeometry.cs:69`, `EmSnpSchematic.cs:376`, `EmCellCache.cs:165`, `EmSetupEditorViewModel.cs:2288`, `:2437`.
`Em3dGenerator`'s own `PortLabels` stays on the setup-free form: a 3D run builds a sheet, not a strip. `ForSetup` adds the strips, so the
run, the preflight, the panel's Refresh and `explain` cannot disagree. Build once per setup and source; a board has dozens of parts.

**R-lcp1-5 — Numbering and impedance.** Numbers follow the drawn ports and any port a part declares (`LabelShape.EmPort`), in list
order, with no gap for a left-out part, as `FirstComponentPortNumber` does. An entry's `Z0` wins for its slot through a wrapper
on `ResolvePortZ0`; `PortZ0s` is not written.

**R-lcp1-6 — Which solver.** Component ports make `AnyNonEdgePort` true through `Collect`, so the existing
`em.port.internal-needs-full-wave` refusal covers the uniform-line kernel. Keep refusing, do not reroute. Its text speaks of an
"internal port", which is not what the user asked for here: when the setup names component ports, say so. The via-separation refusal
is unchanged.

**R-lcp1-7 — The result names its parts.** The Touchstone header reads `! Port[3] = C1` and `! Port[4] = U1.IN`, as a 3D run's does
(`EmSnpProvenance.cs:180`), and the `.cemr` records them.

**R-lcp1-8 — Nothing reaches the file.** The `.clay` is byte-identical after a run, a Check and a save of the `.cem`.

## Gates

A fixture of two series parts on one line with no vias: **about 2 s** (LCP-0). Tests, one per claim:
1. Built ports equal a hand-drawn twin: identical S to 1e-9 against the same layout with the strips and labels written into it.
2. Shorted in the circuit equals solid metal to 1e-6 (measured 0.0000).
3. Polarity: a part placed `R0`, `R90` and mirrored each gives the sign the pads say.
4. A sweep reaching below `Dcim.LowestFittableFrequency` (7.95 MHz on this stack) stays sane on a trace held only by gap ports:
   the DC row uses `PlanarDcSolve`, and the DCFLOAT finding says gap ports are its hard case.
5. Each refusal in R-lcp1-3, needing no solve.
6. A setup with no `ComponentPorts` writes what it wrote: one small byte-identity check.
7. The `.clay` is unchanged.

## Not here

UI, CLI surface, 3D, co-simulation. Agreement with a 3D result is not a gate: LCP-0 says why.
