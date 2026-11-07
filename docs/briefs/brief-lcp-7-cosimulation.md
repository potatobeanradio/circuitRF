# Brief LCP-7 — Co-simulation from a `.cem`

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp7-<n>` · **Depends on:** LCP-1, LCP-5
**Area:** `src/Design/Layout/Em/EmCoSimNetlist.cs`, `EmRunService.cs` (`WithCoSim`), `EmSnpSchematic.cs`, `EmResultRecord.cs`.

## The gap

Component ports exist to put the parts back: a run writes `<setup>_cosim.cnl`, with each part across its port. That writer takes a
`C3dDocument` and a `C3dComponentPortBuild` (`EmCoSimNetlist.cs:218`, `:275`), and `WithCoSim` only runs from the 3D-view door. A Planar
run makes a `_sim` cell, whose `TermG` on a part's port would have no part on it.

## Requirements

**R-lcp7-1 — The writer reads ports, not a document.** Replace the two types in its signature with what it uses: per port its number,
part, pins, schematic id, layout path and nets. Both builders produce that. Output for a `.c3d` is byte-identical.

**R-lcp7-2 — A Planar run writes `_cosim`.** A finished Planar run naming component ports writes the `.cnl`, and with Create SnP schematic
on makes the `_cosim` cell in place of `_sim`, with parts read through `ModelsFor` and the placeholder warning a `.c3d` gives. A 3D `.cem`
gets this from LCP-5's door.

**R-lcp7-3 — A part's nets in the netlist.** Each part stands from its port's node to ground, as a `.c3d`'s does. A multi-pad part maps
terminals through `PartPins`. Both polarities were confirmed against 3D in LCP-0's measurement.

**R-lcp7-4 — Fields.** A Planar run with Radiation pattern stores a pattern per port, so the EM Field Data block's pins are its component
ports by part. **A via anywhere still stops a Planar pattern**, and component-port boards usually have vias, so the run's note says that
when it applies rather than leaving an absent pattern to explain itself.

## Gates

No Palace, and no EM solve at all in most of them: the writer takes a Touchstone, so write a small synthetic `.s4p`.
1. The `.c3d` netlist is byte-identical to before.
2. A synthetic board S-matrix with a known part across each port: `sparam` on the written `.cnl` agrees with closing the same matrix by
   the terminated-network formula, to 1e-6.
3. The 2 s fixture run writes `_cosim.cnl`, and `_cosim` replaces `_sim` with Create SnP schematic on.
4. A placeholder warning for a part with no model on the schematic.
