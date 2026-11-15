# Brief ICF-2 — Every cut instance in `_cosim`; LVS on a kit PCell's generated cell

**Series:** ICF-0 … ICF-6 · **Tag:** `R-icf2-<n>` · **Depends on:** —
**Area:** `src/Design/Layout/Em/EmCoSimNetlist.cs`, `EmCoSimCuts.cs`, `EmCellCutout.cs` (read), `src/Design/Layout/Lvs/LayoutRead.cs`,
`src/Design/Layout/Lvs/DeviceType.cs`.

## The gap

**`_cosim`.** The netlist writer places a cut instance from its ports: each `EmCoSimCut` arrives on a port record, so an
instance whose pins all ended without a port (every pin grounded, a return only, or left out) never reaches the writer.
PR #70 warns about such an instance; the netlist still drops it silently, so a reader of `_cosim` cannot see what was cut.
The same gap affects a partly-ported instance: a pin with no port lands nowhere in the netlist.

**LVS.** A kit's PCell, placed and generated, is a layout cell whose name is the PCell's name plus a hash. The schematic
places the kit part. LVS identifies a device either by a built-in generator (`PCellOrigin.GeneratorId` through
`DeviceTypes.TryGetSymbolKind`, `LayoutRead.cs`) or by the cell name, and a kit-script generator's id is in neither map,
so it compares `<name>_<hash>` against the kit part and reports a mismatch. On the field test's cascode, 3 errors of 3
remaining after PR #70 were this.

## Requirements

**R-icf2-1 — The writer takes the cut list.** `EmCoSimNetlist` receives the cut instances (the plan `EmCellCutout` already
builds, with every pin's class) alongside the ports, and places every cut instance once. A pin's terminal goes to:
its port's node (as now); `0` for a grounded pin; the reference node for a return-only pin (R-icp4-4's reading); and for a
pin left out or not connected, a named open node `nc_<path>_<pin>` with a netlist comment saying why. No other caller
changes; the ports-only path is the same function with an empty cut list.

**R-icf2-2 — An instance with no port is placed and still warned.** Its model is written with every terminal on the node
R-icf2-1 gives it, and PR #70's warning stays, now saying it is placed but connects to the EM result only through ground.

**R-icf2-3 — LVS matches a generated kit PCell to its kit part.** A layout instance whose view carries a `PCellOrigin`
from a kit generator is identified as the kit cell the generator belongs to, not by its generated cell name. Read first
how a kit generator's id names its cell and how the schematic's kit part names it; the match is by that pairing, never by
stripping a hash suffix from a name. A built-in generator keeps its `DeviceTypes` path unchanged.

**R-icf2-4 — Parameters are compared only where both sides state them.** A generated kit PCell's parameters live in its
`PCellOrigin`; where LVS already compares device parameters for built-in generators, extend it to kit generators only for
parameters present on both sides, and report the rest as not compared (info), never as a mismatch.

## Gates

1. A cut instance with every pin grounded appears in `_cosim` with its terminals on `0`, and the warning says it is placed.
2. A partly-ported instance's unported pin lands on `nc_<path>_<pin>` with its comment.
3. A layout with no cut instance writes byte-identical `_cosim` to `main`.
4. A synthetic kit fixture (a kit-script generator registered in the test, a placed generated cell, a schematic placing the
   kit part) passes LVS with zero errors; the same layout with the wrong part reports one mismatch naming both.
5. A built-in PCell's LVS report is unchanged (an existing LVS fixture, byte for byte).

## Not here

Placing several finger ports on one terminal (ICF-3).

## Completion note (2026-11-15)

Built; detail in `src/Design/RESOLVED.md` §brief-icf-2. The per-pin node rule of R-icf2-1 already existed in `EmCoSimCuts.Close`;
what was missing was the instance reaching it, so the cut list rides beside the ports (`EmCoSimPortList.Portless`). R-icf2-3's
mismatch was a folder comparison (a kit reference resolved as a path); the pairing is by generator, the palette's answer in the
GUI and the part id headless. R-icf2-4 needed no code of its own: with the generator on both devices, a parameter the kit's
generator does not draw takes the existing not-drawn note. Gate 4 is a `DeviceType` test rather than a full LVS run on a
registered kit generator.
