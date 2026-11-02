# Brief BSP-2 — Flip in the layout editor

**Series:** bottom-side placement — read `brief-bsp-0-overview.md` first. · **Tag:** `R-bsp2-<k>`
**Area:** `src/Ui/Layout/LayoutEditorViewModel.Rotate.cs` (the shared rigid-transform path), `LayoutEditorViewModel.cs`
(commands), `LayoutEditorViewModel.Designators.cs`, `src/Ui/ViewModels/LayoutShapePropertiesViewModel.cs` and its view,
`src/Ui/Controls/LayoutCanvas.cs` (keys), the layout editor's toolbar/Edit menu/context menu, `src/Design/Layout/Footprints/FootprintLabel.cs`;
`docs/user/src/reference/layout-editor.md`, `src/Ui/RESOLVED.md`
**Tests:** new `tests/Ui.Tests/Layout/LayoutFlipTests.cs`; the rotate/mirror and designator classes named in §5
**Depends on:** BSP-1.

---

## 1. Goal

A user selects a placed part and flips it to the other face. Its lands, mask openings, silkscreen, courtyard and
designator go to the bottom layers and the part is mirrored, but its text still reads left to right from above;
flipping again brings it back exactly. Loose shapes in the selection on a surface role go to the other surface too, so a part and the tracks under it
flip together.

## 2. Requirements

**R-bsp2-1 — The command.** **Flip** (key as the owner answers O2 — proposed **B**), in the Edit menu beside Rotate and
Mirror, in the canvas context menu ("Flip to Bottom" / "Flip to Top" for a single part; "Flip" for a mixed selection),
and on the toolbar beside Mirror. Availability as `MirrorAvailability`. One undo step.

**R-bsp2-2 — What a flip does.** It runs through the same rigid-transform path as `MirrorSelection` (horizontal axis,
pivot the selection's combined centre, bulges flipped, instance rotation negated and `MirrorX` toggled — see that file's
header), and in addition:
- every selected **instance**: `Side` toggled;
- every selected **loose shape**: its layer passed through `SurfaceRoles.For(DrawTechnology).Swap` (a shape on a layer
  that is not a surface role keeps it); a label moves as BSP-1 R-bsp1-9 moves one — centre carried, glyphs readable;
- a selected **via** keeps its span (a through via is symmetric); a blind/buried via's span is mapped to the mirrored
  conductor pair through the stackup — or, if that is more than a small change, the flip is refused while one is
  selected, saying so.
Flip twice is the identity, byte-for-byte on the saved file (test).

**R-bsp2-3 — The one refusal, and what is omitted.** On a technology with no bottom copper the flip is refused and
nothing moves. Otherwise it always proceeds: a loose shape on a surface role the technology has on one side only would be
lost, so the flip is refused while such a shape is SELECTED (saying which and why), whereas a placed part's missing role is
simply not drawn on the bottom (BSP-1 R-bsp1-4) and the flip reports it once — "R12: no bottom silkscreen in this
technology, so its silkscreen is not drawn while it is on the bottom". Flipping back restores it, because the cell itself
never changed.

**R-bsp2-4 — Mirror keeps its meaning and moves text.** Mirror stays a same-face reflection. Its refusal on text is
replaced by BSP-1's rule: a selected label is moved (its centre reflected) and keeps its reading direction.

**R-bsp2-5 — Properties.** A placed part's properties show **Side**: Top / Bottom, a combo. Changing it IS a flip of that
one part about its own origin (not the selection centre), with the same refusals. A multi-selection shows *mixed* when
sides differ.

**R-bsp2-7 — Designators.** A placement's designator (`FootprintLabel`, drawn on the silkscreen ROLE) is drawn on the
BOTTOM silkscreen role when the placement is on the bottom, reading left to right as on a top part; its offset (`LabelDx/Dy`) is mirrored with
the part so it stays on the same side of the body. Moving a designator on a bottom part moves it in board coordinates,
as on a top part.

**R-bsp2-8 — Layers panel.** Hiding Silk Bottom hides a flipped part's designator; hiding Bottom Copper hides its lands.
No new code is expected — verify it.

## 3. Out of scope

No view from below (owner); placing a new part directly on the bottom from the library (a later convenience — Flip after
placing covers it); exporters (BSP-3).

## 4. Things that will bite

- `MirrorSelection` negates instance rotation and toggles `MirrorX` by composing a world reflection — reuse that exact
  path; a second implementation of the geometry will disagree with it on non-cardinal angles.
- A PCell instance with handles (MLIN, MTEE…) flipped to the bottom has its handles in mirrored space: check the handle
  solver still drags the right way (`PCellHandleSolver`), and if not, make the handles follow the transform, don't
  disable them.
- The selection may hold the instance AND shapes inside a group: flip what Mirror would move, nothing more.

## 5. Tests (one per claim)

`LayoutFlipTests`: a flipped chip land pattern's lands on Bottom Copper and X mirrored about the pivot (R-bsp2-2);
flip twice identical bytes (R-bsp2-2); a selected trace goes to Bottom Copper with the part and a shape on an inner layer
stays (R-bsp2-2); refused on a single-sided technology, and a missing bottom silk reported, not refused (R-bsp2-3); Mirror
now moves a label and keeps it readable (R-bsp2-4); Side combo flips about the part's own origin (R-bsp2-5); a bottom part's designator on Silk Bottom, readable, on the same side of the body (R-bsp2-7). Then the
existing rotate/mirror, designator and PCell-handle classes by name. A headless view check (the Avalonia.Headless
harness) of the context menu wording is enough for the UI; no screenshots in tests.

## 6. Docs

`layout-editor.md`: a short **Bottom-side parts** section — Flip, the Side property, what goes to which layer, text keeps
its reading direction, and what is omitted when the technology has no bottom layer for a role. Edit the source only; DocGen is the owner's.
