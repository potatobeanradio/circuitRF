# Brief ICP-9 — User pages and a shipped MMIC example

**Series:** ICP-0 … ICP-9 · **Tag:** `R-icp9-<n>` · **Depends on:** all
**Area:** `docs/user/src/reference/em-setup.md` (`#component-ports`, `#port-storage`), `mom-engine.md` (`#ports`, the port
types, `#component-ports`, `#feed`), `layout-editor.md` (`#ports`), `em-3d.md`, `cosimulation.md`, `em-cells.md`,
`docs/design/cli.md`, `src/Cli/DocumentSchema.cs` (the format reference), `examples/` (`examples.json`, a new folder), the
example pages under `docs/user/src/examples/`.

## The gap

Every page that describes component ports describes footprint parts: a gap between two pads, `"*"` meaning every
two-terminal part, the netlist mapping terminals by pad order. None says a cell instance can be cut out, where its ports
sit, what `"*"` takes on a chip, or what happens to a nested instance's parameters. And no page names the Uncalibrated Port
(ICP-2a) — the port every cut face carries, and one a user can also set by hand on any edge port.

## Requirements

**R-icp9-1 — `em-setup.md`.** `#component-ports` gains a section on cell instances: the two constructions and when each is
chosen (ICP-0 D1); the path grammar and `@n` with its caveat; what `"*"` takes and what it leaves (D3) and how to name a
copper cell; Port at Pin versus Cell, with a figure of each on one instance; the keep-out box; Reference (ground, or a
pin per port, or one common node, and when one cannot be built); grounded pins; `Keep`; the four pin classes and the
pin check that reports them before meshing; one port per connected pin and what happens to an unconnected one. The
`#port-storage` table gets a row for a cut instance's ports (in the setup, never the `.clay`).

**R-icp9-2 — `mom-engine.md`.** The Uncalibrated Port gets its own subsection beside the port types (`#ports`), and the
de-embedding and feed sections (`#feed`) point to it:
- what it is — an edge port driven against the ground plane at its cut, never de-embedded, grounded at every frequency
  whatever the setup's edge-port return, reported at the cut in its own Z0;
- what the result carries that a calibrated port removes — the half cell beyond the cut and the grounded source's own small
  series and shunt launch — with ICP-2a's measured table (within 5.4e-3 … 7e-2 of the calibrated answer on isolated lines,
  5–30 GHz; passive) so a user can judge it against their band;
- why it exists and when to choose it — a calibrated port's feed must be clear of other metal for hundreds of µm on a GaAs
  stack (5 substrate heights from a ported neighbour, 2 from an unported one), which no device's pins can be; ICP-2's
  measurement (refused at 20, 40 and 80 µm; a grounded third pin did not help) is the evidence, quoted rather than
  summarised;
- what it costs and saves — no feed lead, no calibration lines, nothing refused for nearby metal, and a run 3–8× faster;
- that it is not an internal port, nor a delta gap, and that a run's notes and the Touchstone's port line name it.
On a cut instance: its ports are Uncalibrated Ports at the cut faces, always, with ICP-2's reference-plane figure (6.1e-2
closed with an ideal line at 20 GHz). On 3D a cut instance's ports are edge sheets to the return, and the sheet's height is
part of the result (ICP-3's measurement).

**R-icp9-7 — `layout-editor.md` `#ports`.** The **Uncalibrated port** box in a port's Properties, beside **Use edge width**:
what ticking it does in one sentence, that it is greyed for an internal port, and a link to the `mom-engine.md` subsection.
`em-3d.md`: the box changes nothing on a 3D run, where every edge port is already a lumped sheet. The `.clay` key
(`PortUncalibrated`, omitted when false) goes in the format reference the `reference` tool serves.

**R-icp9-3 — `cosimulation.md`.** Where each model comes from (schematic, nested schematic, the PCell's own component, a
placeholder), pins by name, unported terminals, and that a nested instance's parameters are values in the netlist so a VAR
that drove them no longer tunes them there.

**R-icp9-4 — `em-cells.md`.** A schematic with devices runs `model=em` (ICP-8); remove the sentence that says it cannot.

**R-icp9-5 — An example: `examples/MMIC Component Ports`.** A small GaAs stage on the shipped MMIC technology: input and output
lines, a series MIM cap and a shunt spiral as kit PCells, and a three-pin device cell with a layout view, a symbol and a
built-in or SDD model (no vendor kit). Its `.cem` cuts out the device with `"*"` and the MIM cap by name; its README shows
the Touchstone header's port names, the `_cosim` netlist, and the cosim result next to the circuit-model result, and says
that the cut faces are Uncalibrated Ports and what that means for the result (one paragraph, linking `mom-engine.md`). It must
solve on Planar in a couple of minutes, its README says which settings were traded for that, and it carries no
loadpull or HB that needs more than the cosim netlist. Registered in `examples.json` and copyable through
`create what=example`.

**R-icp9-6 — `cli.md`.** The `--component-ports` grammar, `--port-at`, and `explain`'s new walk lines.

**R-icp9-7 — Two layout shapes worth trying by hand.** The requester offered test layouts (ICP-0 § Feedback): on the
shipped GaAs kit, the SpiralResonator with its spiral and MIM cap cut out by name; and, in their external kit, an HBT
cascode with one transistor inside a sub-cell, interconnect, a MIM cap and ground vias — which exercises path picking,
cutting by cell, grounded emitters and pins a few µm apart. The first is this example's own neighbour and is run by hand
once on Planar; the second is run by the requester and reported on the issue. Nothing from the external kit is copied
into this repository.

## Gates

- Each edited page regenerated and looked at; figures checked with Svg.Skia, not a preview tool.
- The Uncalibrated Port is findable: `mom-engine.md`'s subsection is linked from `#feed`, `#component-ports`,
  `layout-editor.md` `#ports` and `em-setup.md`, and the format reference lists `PortUncalibrated`.
- The example opens, `check` passes, `explain --component-ports` prints the cut, and its Planar run finishes inside the time
  its README states (by hand, once — not a test).
- The existing example and doc gates (`examples.json` listing, link checks) pass.

## Not here

Any engine change. A real chip.
