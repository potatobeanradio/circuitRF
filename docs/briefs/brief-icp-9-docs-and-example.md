# Brief ICP-9 — User pages and a shipped MMIC example

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp9-<n>` · **Depends on:** all
**Area:** `docs/user/src/reference/em-setup.md` (`#component-ports`, `#port-storage`), `mom-engine.md` (`#component-ports`),
`cosimulation.md`, `em-cells.md`, `docs/design/cli.md`, `examples/` (`examples.json`, a new folder), the example pages under
`docs/user/src/examples/`.

## The gap

Every page that describes component ports describes footprint parts: a gap between two pads, `"*"` meaning every
two-terminal part, the netlist mapping terminals by pad order. None says a cell instance can be cut out, where its ports
sit, what `"*"` takes on a chip, or what happens to a nested instance's parameters.

## Requirements

**R-icp9-1 — `em-setup.md`.** `#component-ports` gains a section on cell instances: the two constructions and when each is
chosen (ICP-0 D1); the path grammar and `@n` with its caveat; what `"*"` takes and what it leaves (D3) and how to name a
copper cell; Port at Pin versus Cell, with a figure of each on one instance; the keep-out box; Reference (ground, or a
pin, and when a pin reference cannot be built); one port per connected pin and what happens to an unconnected one. The
`#port-storage` table gets a row for a cut instance's ports (in the setup, never the `.clay`).

**R-icp9-2 — `mom-engine.md`.** On Planar a cut instance's ports are edge ports at the cut faces, de-embedded to the face
like any edge port, with the measured findings from ICP-2 (the reference-plane |ΔS|, the device pitch at which ports group
or decline their feed). On 3D they are edge sheets to the return, and the sheet's height is part of the result
(ICP-3's measurement).

**R-icp9-3 — `cosimulation.md`.** Where each model comes from (schematic, nested schematic, the PCell's own component, a
placeholder), pins by name, unported terminals, and that a nested instance's parameters are values in the netlist so a VAR
that drove them no longer tunes them there.

**R-icp9-4 — `em-cells.md`.** A schematic with devices runs `model=em` (ICP-8); remove the sentence that says it cannot.

**R-icp9-5 — An example: `examples/MMIC Component Ports`.** A small GaAs stage on the shipped MMIC technology: input and output
lines, a series MIM cap and a shunt spiral as kit PCells, and a three-pin device cell with a layout view, a symbol and a
built-in or SDD model (no vendor kit). Its `.cem` cuts out the device with `"*"` and the MIM cap by name; its README shows
the Touchstone header's port names, the `_cosim` netlist, and the cosim result next to the circuit-model result. It must
solve on Planar in a couple of minutes, its README says which settings were traded for that, and it carries no
loadpull or HB that needs more than the cosim netlist. Registered in `examples.json` and copyable through
`create what=example`.

**R-icp9-6 — `cli.md`.** The `--component-ports` grammar, `--port-at`, and `explain`'s new walk lines.

## Gates

- Each edited page regenerated and looked at; figures checked with Svg.Skia, not a preview tool.
- The example opens, `check` passes, `explain --component-ports` prints the cut, and its Planar run finishes inside the time
  its README states (by hand, once — not a test).
- The existing example and doc gates (`examples.json` listing, link checks) pass.

## Not here

Any engine change. A real chip.
