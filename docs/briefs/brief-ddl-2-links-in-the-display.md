# Brief DDL-2 — linking in the window

**Series:** DDL-0 … DDL-8 · **Tag:** `R-ddl2-<n>` · **Depends on:** DDL-1
**Area:** `src/Ui/DataDisplay/ViewModels/` (`AxisRoleRowViewModel`, `TraceRowViewModel`, `PlotInspectorViewModel`,
`DataDisplayViewModel`, `MarkerInfoBoxViewModel`, `UndoCommands.cs`), `src/Ui/DataDisplay/Controls/PlotControl.cs`,
the trace card's AXAML, `src/Render/DataDisplay/Models/TraceLabeler.cs`.

## The gap

After DDL-1 a linked pin can be typed (`@m1`) and is resolved, but the window offers no way to make one by pointing.
Nothing follows a marker while it is dragged, and nothing in the window says a link is broken or offers to mend it.

## Requirements

**R-ddl2-1 — The pin row picks a source.** Each linkable pin row on a trace card (DDL-1 R-ddl1-6) gains a link toggle
beside its index control. Off is the index, as today. On is a picker listing every link source in the display:
markers by name, with their plot and trace ("m1 — Smith 1, GammaLoad"), and controls once DDL-5 exists. A source that
would close a cycle is listed greyed, with the reason as its tooltip. A source whose coordinates do not match the
axis by name or unit is listed after the ones that do, with what it supplies. The row reads `@m1 → 17 (Γ 0.31∠142°)`:
the link, the index it resolved to, and the source's own reading. The spec text box shows `@m1` (DDL-1 R-ddl1-5), and
the spec and the picker stay in step both ways, as the other combos do.

**R-ddl2-2 — From the marker's side.** A marker's context menu gains **Link plots ▸**, listing the plots whose traces
have a pin this marker can drive. Ticking one links that pin. This is the one-gesture route from an overview to a
detail plot that already exists.

**R-ddl2-3 — Live follow.** Moving a source (dragging a marker, arrow-stepping it with
`PlotContainerViewModel.StepSelectedMarkers`, moving it from the info box) re-resolves its dependents in `LinkGraph`
order. During a drag, updates are coalesced to the latest position per rendered frame, so a slow consumer never
queues a backlog. A consumer that is slow by kind updates when the drag ends, and its card says "updates on release".
That means a contour refit, and an off-grid loadpull read until its stack is cached (DDL-3). Re-resolution uses the
light path markers already use. It never rebuilds marker info boxes (the focus rule in `src/Ui/DataDisplay/CLAUDE.md`,
"Arrow-key marker fine-movement").

**R-ddl2-4 — The legend and the title say what is shown.** A linked trace's label carries the link and the case:
`Pout @ m1 (gridPoint 17)`, or `S(2,1) @ m2 (Vgs −0.4 V, Vds 6 V)` once DDL-4 names coordinates. This comes from
`TraceLabeler`'s minimal-label policy (`data-display.md` §2.7). It is a component of the trace's identity, never a
pre-joined string.

**R-ddl2-5 — Dangling, said and mended (DDL-0 D2).** A frozen link shows a chip on its pin row and a note on the trace
card: "m3 is gone — showing gridPoint 17, where m3 last was". The chip offers **Link to ▾** (the R-ddl2-1 picker). When
a source with the frozen link's name exists again, it also offers **Use the new m3** as one click. Nothing relinks by
itself. The plot draws normally (it is a static plot), with the link name in its legend struck through.

**R-ddl2-6 — Undo.** Making, changing or removing a link is one undo step on the display's stack. Moving a source is the
step it already is (`MoveMarkerFrequencyCommand`, or the control's own step in DDL-5). Dependents are derived and
record nothing. Deleting a marker (`RemoveMarkerCommand`) freezes its dependents. Undo restores the same marker
object, id included, so they are live again with no choice to make.

**R-ddl2-7 — Copy, paste, duplicate.** Pasting plots (`PasteCommand`) or duplicating a tab gives each pasted marker a
new id and name. A link inside the pasted set that pointed at a marker also in the set is remapped to that marker's
copy, so a pasted overview and detail pair drives itself. A link to a marker outside the set keeps pointing at the
original.

**R-ddl2-8 — Deleting a plot or trace that carries a source** freezes its dependents (as R-ddl2-5), and the Messages
panel names them once ("Pout and Gt on Detail no longer follow m1; they show gridPoint 17").

## Gates

1. **Point and pick.** In UiSnapshot: an overview Smith plot with m1 and a detail Rect plot. Link the detail's
   `gridPoint` pin to m1 from the picker, move m1 with an arrow key, and the detail redraws at the new sample. The
   PNG is looked at, and the row text matches R-ddl2-1.
2. **Coalesced drag.** Sixty scripted pointer moves within one frame produce one re-resolution of the dependent, not
   sixty (counted, not timed).
3. **Dangling and relink.** Delete m1: the chip and note appear and the plot is unchanged. Undo: live again. Delete
   m1, add a new marker (it takes the name m1): the chip offers "Use the new m1", and one click relinks.
4. **Paste remap.** Copy an overview plus detail pair and paste it: the pasted detail follows the pasted marker, and
   moving the original marker does not move the pasted detail.
5. **Cycle in the picker.** With A pinned by m1 on B, B's picker lists a marker on A greyed, with the chain in its
   tooltip.

## Not here

Off-grid positions (DDL-3); family, versus, trial and contour-frequency sources and consumers (DDL-4); controls
(DDL-5).
