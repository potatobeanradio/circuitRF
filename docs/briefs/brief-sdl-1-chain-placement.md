# Brief SDL-1 — Connected placement for distributed parts

**Series:** [Schematic-driven layout](brief-sdl-0-overview-and-groundwork.md) · **Tag:** `R-sdl1-n`
**Area:** `src/Design/Layout/SchematicSync/` (new `JointGraph.cs`, `ChainPlacer.cs`), the planner from
SDL-0, `src/Design/Layout/SchematicLayoutOrientation.cs`, `docs/design/layout-view.md` §9.1,
`docs/user/src/reference/layout-editor.md`
**Depends on:** SDL-0. **Gate designs:** C1-C7.

---

## 0. What this adds

When two parts with layouts are joined by a net in the schematic, Update Layout from Schematic places
them so the two pins meet: same point, facing each other. A branch-line coupler built from MTEEs and
MLINs comes out as one connected loop instead of a row of loose strips. Parts that do not join anything
keep today's row placement.

Everything needed is already there. `PCellPin`/`LayoutPin` carry position, layer, width and an outward
direction at any angle (`PCellContract.cs:29`, `LayoutModel.cs:1103`). `CellPins.Resolve` is the one
place a cell's pins are answered, generated or imported. Instances already take any rotation
(`RotationDegrees`, backed by `RotDeg`) and `MirrorX`. `SchematicLayoutOrientation.PinAlignment` already
matches schematic ports to layout pins.

## 1. Decisions

- **D1 — A joint is a net with exactly two layout pins.** Both pins on the same layer, neither on the
  ground net. Nets with three or more layout pins, ground nets and cross-layer pairs are not joints in
  this brief: they keep the ratsnest and get a report line (SDL-2 and SDL-3 handle the ones that have a
  defined answer).
- **D2 — Mirror comes from the schematic; rotation is solved.** Whether a bend turns left or right, or
  which side a tee's stub leaves, is chirality, and pin geometry cannot decide it. Each part keeps the
  mirror state the schematic gives it (as the orientation link does today). Its rotation is then the one
  value that makes its joining pin face the parent's pin. When the linked orientation and the solved
  rotation disagree, the solved one wins and the orientation link is updated, because the link exists to
  keep the two views alike and a joined layout is what the schematic describes.
- **D3 — Poses are exact until the last step.** Each part's pose (origin, angle) is carried through the
  chain in doubles and rounded to DBU once, when the instance is written. Rounding at every link adds one
  DBU of error per part, which on an any-angle chain is visible within twenty parts (design H6, SDL-3).
- **D4 — A loop that does not close is reported, never forced (S1).** A closing joint within 1 DBU in
  each axis and 1e-6° counts as joined. Anything more is a report line naming the loop's parts, the gap
  in x and y and the angle error. The parts stay where the chain put them.
- **D5 — Existing parts are anchors in this brief.** A part already in the layout from an earlier run is
  not moved here; new parts chain onto it. SDL-4 defines when an existing part may move.

## 2. `R-sdl1-1` — the joint graph

`JointGraph.Build(extraction, resolvedInstances, technology)`:

- Nodes: placed instances with at least one resolved pin. Pin-to-port matching goes through
  `SchematicLayoutOrientation.PinAlignment`, not a second matcher.
- Edges: one per joint (D1), holding both (instance, pin) ends.
- Nets that are not joints are listed with their reason (`ThreeOrMorePins`, `Ground`, `LayerMismatch`,
  `OnePin`) so the report can say why each ratsnest line remains.
- Connected components of the graph are **chains**. Ordering everywhere is by schematic id (S5).

## 3. `R-sdl1-2` — placing a chain

`ChainPlacer.Place(chain, anchorPose)`:

1. **Anchor.** First match wins: an existing instance in the chain (D5; lowest schematic id if several);
   the part joined to the lowest-numbered schematic Port; the lowest schematic id. A new chain's anchor
   takes pose (0, 0, 0°) and the whole chain is moved as one rigid group by the row packer (§4).
2. **Tree.** Breadth-first from the anchor; neighbours visited in schematic-id order. Each tree edge
   places the child: child mirror from D2; rotation `θ = θ_parent_out + 180° − θ_child_local_out` (with
   the mirror applied to the local direction first); translation so the child's pin lands exactly on the
   parent's pin. Pin points are the pins' own X/Y (an MLIN pin sits mid-way across its end edge).
3. **Closing joints.** Every non-tree edge is checked against D4's tolerance. Closed: nothing to report.
   Open: a `Warning` line, e.g. *"Loop MT1, ML1, MT2, ML4, MT3, ML2, MT4, ML3 does not close: MT3 pin 3
   is 50.0 µm from ML4 pin 2 (x 0, y −50.0 µm), angle 0°."*
4. **Width.** Pins of different widths are joined centre to centre with no report: the schematic chose
   the widths. A pin whose width is 0 (unstated) is joined and reported as `Info` once per part.

## 4. `R-sdl1-3` — fitting chains into the layout

- A new chain is one rigid group. The existing row packer (`PlaceNewInstances`) places groups by their
  bounding boxes, exactly as it places single parts today; parts in no chain keep today's behaviour.
- A chain that includes an existing part is placed relative to that part and not packed.
- A chain with two or more existing parts: the first is the anchor, and joints to the others are
  closing joints (D4). Nothing existing moves.
- The `PlaceAtArtwork` path (placement onto drawn artwork) is unchanged for parts in no chain. A chain
  part that `PlaceAtArtwork` would have placed is placed by the chain instead, and that is reported once
  per run (`Info`).

## 5. `R-sdl1-4` — the setting and the report

- One setting, **Connect parts on Update Layout from Schematic**, default ON, beside the existing
  orientation-link setting in Settings (`docs/user/src/reference/settings.md`). OFF restores today's row
  placement exactly. The planner takes it as an argument (`src/Design/CLAUDE.md`: a preference is an
  argument).
- The run's summary adds one line: *"Joined N of M connections; K left as ratsnest"*. Each ratsnest net
  that remains has its own line with its D1 reason.

## 6. Tests

- Designs C1-C7 through `SdlChecker`. C6 expects exactly one loop-gap warning, with the 50 µm in the
  right axis. C7's copper equals C2's after the rigid transform that maps their anchors.
- One unit test per D-rule: mirrored MBEND turns the other way (D2); a 12-part 30° chain's last pin
  matches the exact double computation to ≤ 1 DBU (D3); a 1-DBU closing gap is joined and a 2-DBU one is
  reported (D4); an existing part does not move (D5).
- Setting OFF: the C2 plan equals the pre-series plan byte for byte.
- One undo step reverts the whole run, placement records included.
- Visual check: render C2 and C4 with `tools/UiSnapshot` and look at them; list the PNGs in the
  completion note.

## 7. Docs

`layout-view.md` §9.1: D1-D5. User page `layout-editor.md`, section on Update Layout from Schematic:
parts now join where the schematic joins them; what a loop-gap line means; the setting.

## 8. Gate

`dotnet test tests/Ui.Tests --no-build --filter "FullyQualifiedName~SchematicSync|FullyQualifiedName~SchematicToLayout"`,
then `tests/Firewall.Tests`. Findings in `src/Design/RESOLVED.md`.
