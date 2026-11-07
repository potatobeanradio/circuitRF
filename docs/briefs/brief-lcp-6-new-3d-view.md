# Brief LCP-6 — New 3D View carries a `.cem`'s component ports

**Series:** LCP-0 … LCP-8 · **Tag:** `R-lcp6-<n>` · **Depends on:** LCP-5
**Area:** `src/Design/ThreeD/C3dHierarchy.cs` (`NewFromLayout`, `TranslatePorts`, `TranslateWith`),
`src/Ui/ViewModels/WorkspaceViewModel.ThreeD.cs`, `src/Cli/Authoring.cs`, `src/Cli/Serve/ToolCatalog.cs`.

## The gap

`NewFromLayout` copies each 3D `.cem` naming the layout and skips every Planar one (`if (!setup.Is3D) continue`). A part list ticked on a
Planar setup would be absent from the 3D view made next, with no message saying so. And a 3D `.cem` holding component ports now reaches
`TranslateWith`, whose generator call refuses them, which would lose that setup's drawn ports to a "left out" line.

## The rule

**New 3D View brings in the component ports of each `.cem` on the layout. After that, the two are separate.** The same on every route:
the `.cem` window's button, the layout toolbar, the Project tree, Design ▸ New 3D View from Layout, `new view3d --from-layout`, and MCP
`create what=view3d`. All of them already reach `NewFromLayout`, so the rule lives there and nothing passes a source `.cem`.

| A `.cem` naming the layout | What the new `.c3d` gets |
|---|---|
| 3D setup | An embedded copy, as today, with its `ComponentPorts` |
| Planar, with component ports | A new 3D setup named after the `.cem`: its sweep, its `ComponentPorts`, the default 3D solver |
| Planar, no component ports | Nothing, as today |

## Requirements

**R-lcp6-1 — Translation ignores them.** `TranslatePorts` and `TranslateWith` clear `ComponentPorts` on the clone used to read drawn port
labels. The embedded setup keeps its list. Drawn port labels still become `.c3d` ports as today, numbered first.

**R-lcp6-2 — The new setup from a Planar `.cem`.** Carries the name (made unique by the rule already there), `Frequency` and
`ComponentPorts` with each entry's Z0, width and reference. Planar mesh settings do not cross: they mean nothing to a 3D solver, and the
3D setup takes its normal defaults. It counts as a copied setup, so the "no 3D setup names this layout" default does not also fire.

**R-lcp6-3 — One setup per `.cem`.** Lists are never merged. Different `.cem` files may name different parts, and a union would be a set
nobody chose.

**R-lcp6-4 — Copied once.** The `.c3d` holds its own list. Editing either side never touches the other, and neither file records a link
that could suggest otherwise. `FromLayout` on a port is about drawn labels and is not extended to these.

**R-lcp6-5 — Rechecked in 3D.** The list is copied, not the verdict. A part that built as a strip and cannot be built as a sheet is left
out by the ordinary rule, naming why, the first time the 3D setup is checked. Creating the view does not run that check: on a board it
is seconds of work per part.

**R-lcp6-6 — Says what it did.** `C3dFromLayout` reports which setups came across and how many component ports each carries. The GUI posts
it to Messages, one line per setup; `new view3d` prints it and adds it to `--json`.

## Gates

No solves. All on documents:
1. The full workflow: a Planar `.cem` with C1 and C2 ticked, New 3D View, then the `.c3d`'s setup lists C1 and C2.
2. Change the `.c3d`'s list to C2 alone, and the `.cem` is byte-identical; change the `.cem`, and the `.c3d` is byte-identical.
3. Two `.cem` files with different lists give two setups, unmerged.
4. A Planar `.cem` with no component ports adds no setup; drawn port labels translate as they did.
5. A 3D `.cem` with component ports keeps its drawn ports (the refusal path of R-lcp6-1), and its list.
6. Every route: the GUI command and the CLI verb produce the same `.c3d` bytes.
