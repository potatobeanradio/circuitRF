# Brief PW-1 — a port's stated Width, honoured by the planar run and kept by a `.c3d`

**Tag:** `R-pw1-<n>` · **Builds on:** `brief-dfr32-2-ports-width-curves-labels.md` (R-dfr32-2-1/2, which added
`LabelShape.PortWidth` and the 2 × H rule for the 3D sheet).

A port's `Width` is a property of the port label in the `.clay`. Today only one of the three ways a layout reaches a
solver reads it. This brief makes the other two agree with it.

## Where it stands

A board with component pads on the sides of its traces: one port on the bottom side of an 11.25 mm line, at a shunt
part's 885 µm pad, and one on the top side of a 3.17 mm line, at a series part's 1.03 mm pad. Neither port is at a
line end. The edge each stands on (`LayoutPortDirection.EdgeAt`) is the whole side of the line.

| Path | What the port drives | Reads `Width`? |
|---|---|---|
| 3D `.cem` on the layout (`Em3dGenerator.BuildPorts`) | `EdgePortSheet.Span`: the stated width, else 2 × H (406.4 µm here) | **Yes.** Verified on this board: 885 µm and 1026 µm stated, sheets built 885 µm and 1026 µm wide, centred on the ports. |
| Planar `.cem` (`EmPortExtraction` → `PlanarPort` → `PlanarPorts`) | The contiguous row of rooftops along the edge: the whole 11.25 mm side | **No.** Nothing on the planar path reads `PortWidth`. |
| `.c3d` that places the layout as an instance | The `.c3d`'s own `Ports` records only. The layout's port labels are ignored (`C3dElaborator`: "a layout's port shapes") | **Only once.** `C3dHierarchy.NewFromLayout` → `TranslatePorts` runs the 3D generator at creation and freezes each sheet as a rectangle. A later `Width` edit never reaches it. A layout with no 3D `.cem` at creation gives a `.c3d` with no ports at all. |

The layout editor shows the 3D answer: the bar, and since 2026-10-31 the highlight and hit region, are
`EdgePortSheet.Bar`. The Inspector's Width field is blank when nothing is stated, and its placeholder shows the 3D
rule's width. So the canvas shows a 0.4 mm port that a planar run builds 11.25 mm wide. Nothing on the port says which
of the two a user is getting, and there is no way to ask for the whole edge on purpose.

## 1. The planar run honours the port's width

**R-pw1-0 — a "Use edge width" checkbox on the port, ON by default** (decision D1, owner, 2026-10-31).
- **Checked:** the port drives the whole conductor edge it stands on, measured from the shape (`EdgeAt`). This is what
  a planar port has always done and what most users expect. The Width field is disabled and shows that edge width.
- **Unchecked:** the Width field is live. A stated width wins; blank means 2 × H centred on the port, clipped to the
  edge.

Stored as `LabelShape.PortUseEdgeWidth` (`bool?`, additive, omitted when null, no `FormatVersion` bump). **Null reads as
`PortWidth is null`:** an existing port with no stated width is checked, and an existing port that states a width
stays unchecked, so its width still wins. Writing the default leaves the field null, so an untouched `.clay`
round-trips byte for byte. In the Inspector the checkbox sits on its own row above Width. Width is disabled when the
box is checked, never hidden, so nothing below it moves. With several ports selected, a mixed state shows
indeterminate and a click sets them all.

`EdgePortSheet.Span` takes the flag. The rule becomes: checked → the whole edge; unchecked → stated, else 2 × H. The
`Edge` result on a short edge (≤ 4 × H) goes away for an unchecked port: unchecked with nothing stated is 2 × H,
clipped to the edge, which on a short edge is the edge anyway.

**R-pw1-0b — one meaning on both kernels** (decision D1b, owner, 2026-10-31). The checkbox is a property of the port,
and the canvas draws one bar, so it means the same in both runs. Default ON therefore changes 3D too: a port on a long
edge with nothing stated goes back to the whole edge. That is the sheet DFR32-2 bounded, because on a ground pour's
edge it crossed a via and the run refused. Where a 3D sheet touches a second net or a via (the existing refusal) and
the port uses its edge width, the refusal names the checkbox as the fix. Before landing, run every shipped example's
3D setups and list each port whose sheet changes. The 3D run's note for a bounded sheet ("State the port's Width …")
names the checkbox as well, since Width is only live with it unchecked.

**R-pw1-1 — a planar edge port drives only the span `EdgePortSheet.Span` gives it.** `PlanarPort` gains an optional
transverse span (metres, nullable, additive). `EmPortExtraction` fills it from the same call the 3D generator makes, with H
from the stack (`EdgePortSheet.HeightToReturnDbu`). `PlanarPorts` keeps the rooftops of the run whose transverse
centres lie inside the span. The mesher puts a gridline at each end of the span, the way it already seeds a port's
transverse position (`PlanarMeshPitchField`), so the cut ends on a cell boundary rather than mid-cell. A checked port
passes no span, which keeps today's planar behaviour exactly.

**R-pw1-2 — de-embedding a partial-width port.** An edge port is de-embeddable today (`PlanarPortResolution.IsDeembeddable`)
and its calibration feed is grown at the port's width. A port narrower than its edge has its feed grown at the span's
width, centred on the span. Where the feed cannot be grown (it would overlap other metal), the port is not
de-embedded and the run says so by port number. It is never silently de-embedded at the edge's width.

**R-pw1-3 — the run's note matches 3D's.** "Port n's cut is W wide, the Width the port states" or "… 2 × H centred on
the port, on an edge L long", from the same wording `Em3dGenerator` posts.

## 2. A `.c3d` keeps up with its layout's ports

**R-pw1-4 — the 3D editor says when a port is stale.** On open and on a layout change, for each `.c3d` port whose number
matches a port label in an instanced layout, re-run the translation (`TranslatePorts`' sheet, in memory) and compare
rectangles. Where they differ, post one message per port: the old and new widths. Nothing is rewritten silently,
because a `.c3d` port may have been edited by hand on purpose.

**R-pw1-5 — Update Ports from Layout.** A command on the 3D editor (and a `history`-safe write, so it can be undone)
that re-translates every port the layout states and replaces the matching records. Ports the `.c3d` has that the layout
does not state are left alone.

**R-pw1-6 — a `.c3d` made from a layout with no 3D setup still brings in its ports** (decision D2, owner, 2026-10-31).
When no 3D `.cem` names the layout, `NewFromLayout` translates the port labels with a default driven setup and posts
a note saying it did. R-pw1-5 does the same. It saves the user drawing every port by hand, and a port they do not want
is deleted like any other. A port that cannot be one rectangle, or whose sheet the generator refuses, is listed by
name with the reason, as `TranslatePorts` already lists them, and the rest are still brought in.

## Gates

- One test per claim: the checkbox's null reading (no width → checked, a stated width → unchecked) and an untouched
  `.clay` round-tripping byte for byte; checked drives the whole edge on both kernels; unchecked and blank is 2 × H;
  a stated width narrows the planar port's rooftop row to the span (count the rooftops); a null span reproduces today's port bit for bit on an existing planar fixture; a partial-width port's
  feed width, and the refusal note when it cannot grow; the 3D via/second-net refusal naming the checkbox for an
  edge-width port; a stale `.c3d` port reported with both widths; Update Ports from Layout replaces exactly the
  stated ports; a `.c3d` made from a layout with port labels and no 3D `.cem` has those ports, with the note.
- The shipped examples' planar and 3D setups re-run (generation and `explain` only, no solves), with every port whose
  span changed listed in the completion note.
- A planar run on a short synthetic case (a line with a side port at a pad), not a whole board: a stated width, and
  the result differs from the whole-edge port. Keep it under a minute.
- UiSnapshot PNGs, looked at: the Inspector with the box checked (Width disabled, showing the edge) and unchecked
  (Width live, placeholder 2 × H), the port's bar on the canvas for each; the 3D editor's stale-port message.
- Docs: the port sections of `docs/user/src/reference/layout-editor.md`, `em-setup.md` and `em-3d.md`, saying once
  that the checkbox and Width apply to every run, and the `.c3d` section saying a new `.c3d` brings in the layout's
  ports.
