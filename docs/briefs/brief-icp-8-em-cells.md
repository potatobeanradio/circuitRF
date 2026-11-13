# Brief ICP-8 — EM cells whose schematic places devices

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp8-<n>` · **Depends on:** ICP-2, ICP-5
**Area:** `src/Design/Layout/Em/ParametricEm/EmModelSwitch.cs` (around line 200), `EmCellCache.cs`, `src/Design/RESOLVED.md`
(SDL-6 R-sdl6-2's stopped item).

## The gap

An analysis line with `model=em` runs a schematic by EM of its own layout. When the schematic places anything that is not
copper — a footprint part, a `pdk://` device, a transistor — it refuses: *simulating them with the EM result needs component
ports at the pads and each part's model across them, which an EM cell does not build.* On an MMIC every amplifier stage
has a transistor, so `model=em` cannot run on one. LCP built component ports for footprint parts and this series for any
cell instance; the EM cell is the one caller that does not use them.

## Requirements

**R-icp8-1 — Non-copper parts become component ports.** Where `EmModelSwitch` now refuses on `notCopper`, it adds an entry
per such part to the in-memory setup it runs (`*` covers exactly that set, by ICP-0 D3), so the re-planned layout's run
cuts out devices and builds gap ports on footprint parts. A part whose port cannot be built still refuses the analysis,
naming it and the reason from the builder.

**R-icp8-2 — The parts go back from the schematic being run.** The netlist the EM cell evaluates closes the N-port with
each part's model through `EmCoSimNetlist.Closures` (ICP-5), read from the schematic and the variable values of this
evaluation, so an optimisation that moves a device parameter re-evaluates the netlist without a new EM solve.

**R-icp8-3 — The cache key.** `EmCellCache` keys on geometry (SDL-6). The cut changes the geometry and the port list, and
both enter the key; a device parameter that changes no layout (bias, a model parameter) does not.

**R-icp8-4 — 3D EM cells** (SDL-8) take the same path through ICP-3.

## Gates

1. A schematic with two lines and a three-terminal device sub-cell runs `model=em` on the ICP-2 fixture and matches the
   `.cem` run of the same layout closed by the same netlist, to 1e-9.
2. Changing the device's model parameter re-evaluates with no new solve (the cache counts one).
3. A device whose port cannot be built refuses, naming it.

## Not here

Optimiser changes (SDL-7's em=correct / em=direct are unchanged).
