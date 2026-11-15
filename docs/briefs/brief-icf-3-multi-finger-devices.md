# Brief ICF-3 — Multi-finger devices: one port per finger face

**Series:** ICF-0 … ICF-6 · **Tag:** `R-icf3-<n>` · **Depends on:** ICF-2 (the writer takes the cut list)
**Area:** `src/Design/Layout/Em/EmCellCutout.cs`, `EmPlanarComponentPorts.cs`, `src/Design/ThreeD/C3dCellCuts.cs`, `EmCoSimNetlist.cs`,
`EmPortHeader.cs`, `docs/user/src/reference/em-setup.md`.

## The gap

A pin name at more than one face of a cut instance (a multi-finger transistor's gate, drain or source) is refused per pin:
`EmCellCutout` says "the pin name … is at more than one face … and one port per pin name is built in this series", and the
co-simulation keys a cut port by (path, pin). The face builders already make one port per face, so the cut, the ports and
the solve need nothing new. What is missing is the name of each finger's port and how the model is put back across them.

## Decision D1 (owner, before building)

- **Tie (recommended first).** Every finger's port is its own port in the Touchstone; `_cosim` joins a pin's finger ports
  onto the one model terminal. The interconnect to each finger is in the result; the device sees the fingers in parallel
  at one node, so phase differences between fingers inside the device are lost. Needs nothing from the model.
- **Split.** The device is written as N one-finger instances, each across its own finger's ports, with the model's finger
  parameter set to one finger and the rest scaled by the model's own rule. Keeps per-finger phase, but needs to know which
  parameter counts fingers and how the others scale, which differs between models and kits.

The brief builds Tie, and builds Split only where a part's model declares its finger-count parameter (a kit says it; a
built-in FET model names it). Without that declaration, Split is refused naming what is missing.

## Requirements

**R-icf3-1 — Finger ports and their names.** A pin at k > 1 faces builds k ports, named `<path>.<pin>[1]` … `[k]`, ordered
along the instance's own axis (lowest x, then lowest y, in the cell's coordinates, so the order is the same whatever the
placement's rotation). A pin at one face keeps its name with no index. The Touchstone port names, the header line
(`EmPortHeader`), `explain` and the panel rows use the indexed name.

**R-icf3-2 — References per finger.** A pin `Reference` on a multi-finger pin pairs finger i with the reference pin's
finger i when both have k faces and they face each other pairwise; otherwise each finger falls back to ground (ICF-1's
rule) with the reason. Grounded and return-only classes apply to every finger of the pin.

**R-icf3-3 — `_cosim`, Tie.** Each finger port's node is joined to the model terminal by an ideal short (or the netlist's
own equivalent: the same node name), written with a comment naming the fingers joined.

**R-icf3-4 — `_cosim`, Split.** Where the part declares its finger parameter, N instances `<refdes>_f1` … are written, each
on its finger's ports, finger parameter 1, other parameters per the declared rule. Where fingers of different pins do not
pair one to one (k_G ≠ k_D), Split is refused for that instance and Tie is used, said in the netlist.

**R-icf3-5 — The pin check.** The pin classes report each finger (`Q1.G[2]: routed, face at …`). A finger that is not
connected (PR #70's dead-end check) is warned on its own.

## Gates

1. A two-finger fixture (synthetic cell, two gate faces, two drain faces, one source) builds 5 ports with indexed names;
   a one-finger cell's ports keep their names (byte-identical Touchstone).
2. The order of finger indices is the same with the instance rotated 90°.
3. Pin references pair fingers when they face; otherwise each falls back with its reason.
4. `_cosim` under Tie writes one device with joined nodes; S of the closed netlist equals the same layout with the fingers
   joined by drawn metal and one port per pin, to the tolerance the existing ICP-5 gate uses.
5. Split writes N instances for a part declaring its finger parameter, and refuses with its reason for one that does not.

## Not here

Fingers of one pin at different layers; a finger count that changes with a parameter during a sweep.
