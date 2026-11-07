# Brief LCP-8 — Docs: where each kind of port is stored

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp8-<n>` · **Depends on:** the others; written last, against what shipped
**Area:** `docs/user/src/reference/em-setup.md`, `em-3d.md`, `cosimulation.md`, `mom-engine.md`, `cli.md`, `antennas.md`.

## The gap

There are now four places a port can be stored, one more than before, and "component port" sits close to a similar-sounding term already
in the docs ("Ports a component carries", a TESTPAD with `Centre = Port`). Nothing says in one place where a port lives, and several
pages state what this series changes.

## Requirements

**R-lcp8-1 — One table, in `em-setup.md`, linked from the others.** Stating the line clearly: **a port you draw lives in the document you
draw it in. A component port is not drawn, and lives in the setup that names it.**

| Port | Where it is stored | How you move it |
|---|---|---|
| A port label drawn on a layout | The `.clay` | Drag it in the layout |
| A port drawn in a 3D view | The `.c3d` | Drag it, or edit it in the Properties Inspector |
| A port a placed part declares (TESTPAD `Centre = Port`) | The part's own layout | Move the part |
| **A component port** | **The `.cem`, or the `.c3d`'s setup** | **Move the part** |

**R-lcp8-2 — Say what a `.cem` holds about drawn ports.** A `.cem` also stores *settings* for drawn ports, by number: reference impedance
(`PortZ0s`) and, for a 3D setup, its port table (`Ports3D`). The table above has to survive a reader who opens a `.cem` and sees those,
so one sentence separates a port from its settings: the position and the type of a drawn port are the `.clay`'s, and one layout can be
measured at 50 Ω by one setup and 75 Ω by another.

**R-lcp8-3 — Planar says what it is.** In `em-setup.md`'s Component ports: on the Planar solver a component port is a gap port. The solver
cannot span the air between two pads, so it adds a strip of metal across the part's gap, in the solved problem only, and cuts it at the
middle with an internal delta gap, and the layout is not changed. A link to `mom-engine.md#ports`, which gains the same in its own words.

**R-lcp8-4 — Stating the 2D-to-3D rule.** "New 3D View brings in the component ports of each `.cem` on that layout. After that, the two are
separate: changing the parts in one leaves the other alone." With what happens to a Planar `.cem` (a new 3D setup, named after it), and
that a part is checked again in 3D and may be left out.

**R-lcp8-5 — Planar and 3D are not promised to match.** Say that they are different solvers and their answers differ, more with frequency.
No figure from LCP-0 is presented as a general accuracy, and nothing implies a user should expect agreement to a tolerance.

**R-lcp8-6 — Limits, by name.** Parts whose pads do not face each other along x or y are left out. A Planar run refuses vias farther apart
than 0.1 λ unless the direct kernel is on. A via stops a Planar pattern. Component ports on a 3D `.cem` cannot be combined with a Solve
Region. Marks appear on a layout only while a `.cem` is open.

**R-lcp8-7 — What goes.** Every sentence these make false:
- `em-setup.md`: "A `.cem` on a layout refuses it: component ports are built in a 3D view".
- `cosimulation.md`, § A planar result: "since component ports are built only in a 3D view", and "Two pads with a real gap between them
  cannot be a port", which keeps its truth only for a drawn port.
- `em-3d.md`, `#part-ports`: "A 3D setup of a 3D view" as the only container.
- `cli.md` on `--component-ports`, and `DocumentSchema`'s text if LCP-2 left any.

**R-lcp8-8 — Style.** Present tense, what the product does. No change history, no "now" or "used to". Regenerate only the pages edited;
the full DocGen run is the owner's, at the end of the series.

## Gate

The docs' link and anchor checks. A grep that no page says component ports are 3D-only. No simulation.
