# PSV-4 — the example workspace, the docs and the reference

Series: `brief-psv-0-overview.md`. Depends on 1, 2 and 3. If brief 3 stopped at its risk check, build
this without its bench and say so in the README.

## R-psv4-1 — the example: "Passivated Pad"

A small shipped workspace under `examples/`, registered as the existing examples are (File ▸ New ▸
Examples, `reference examples`). Its own technology is a copy of the MMIC GaAs starter (D4: no starter
is changed), plus:

- a 1 µm nitride passivation (εr 7.0, tanδ 0.001) above Metal2, with `OpenWithLayer` = a new drawing
  layer, `PAD_OPEN`;
- a second dielectric above it (e.g. a 5 µm polyimide, εr 3.3), so the stack has two covers, as a real
  process can.

Benches, numbered in run order, each solving in well under a minute:
1. **A Metal2 50 Ω line with and without the covers** (a parametric sweep or two benches). It shows the
   covers in the planar solve (brief 1).
2. **A probe pad with an opening, through the 3D view** (a 3D `.cem` or `.c3d`). It shows the hole
   (brief 2). The README includes a rendered 3D picture of the pad exposed through the opening.
3. **A via through two dielectrics** (brief 3), if built.

README: what each bench shows, and the flat-slab statement (D5) in one sentence. Do not explain to the
reader what passivation is. **Document the setting you traded away** if any bench is coarsened to run
fast.

## R-psv4-2 — docs

- `docs/user/src/reference/stackup.md`: the **Opened by layer** column, beside **Present with layer**,
  in the column table. Add a short section on covers and openings.
- `docs/user/src/reference/mom-engine.md`: covers (brief 1), openings as a coverage note (brief 2), and
  vias across interfaces (brief 3), in the limitations list. Remove anything no longer true.
- `docs/design/em-3d.md` (~line 322, where `PresentWithLayer` is described): `OpenWithLayer` beside it.
- The `.ctech` format as the MCP `reference` tool and `circuitrf reference` serve it: wherever
  `PresentWithLayer` is documented there, document `OpenWithLayer`.

## R-psv4-3 — DocGen, once

Regenerate `docs/user` for the edited pages only, with figures for the pages that gained a figure.
Check that the diff touches only those pages.

## Gate

The example's benches run headless (`circuitrf` verbs) with no display, each in under a minute, and
`check` reports 0 errors on the workspace.
