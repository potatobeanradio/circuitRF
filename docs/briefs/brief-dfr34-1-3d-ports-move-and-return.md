# Brief DFR34-1 — 3D ports: moving one, choosing its return, and returns through an opening

**Series:** designer feedback round 34. · **Tag:** `R-dfr34-1-<n>`

A field test on a 10-layer board took a test point's round pad into 3D from its layout. Two ports, P1 at a capacitor's
edge and P2 on the pad's flat edge near an RF connector. P2 could not be built. Fixing it needed three things the 3D
view does not offer: moving the port by hand, saying which metal it returns through, and a return that is on the same
layer as the line.

Read first: `Em3dGenerator` (the port's return walk `ReturnWalk`, `EndCover`, `EndReturnRefusal`, the two tolerances
`ReturnGapToleranceOfWidth` and `TranslatedReturnGapOfWidth`), `C3dHierarchy.TranslateWith`, `C3dDocument`'s port model
(`Positive` / `Negative`), `C3dPropertiesViewModel` and the 3D editor's Modify commands (`ThreeDModifyCommand`: Move,
Move Along, face and vertex Move).

Already fixed in round 34 and not to be redone: a port is no longer slid onto a narrower line
(`lineSpansSheet`); a `.c3d` translation ignores the setup's solve region; the refusals name where the Return column is.

## 1. A port cannot be picked and moved in the 3D view

**The gap.** A 3D port is a rectangle the user is expected to adjust ("Move the port in the 3D editor", the translation
note says). Picking it in the canvas is hard: it is a thin sheet inside metal and dielectric, and a click usually lands
on what is around it. Move works on sheets, but a port has no Move entry of its own.

**R-dfr34-1-1 — Move… on a port**, in the Object Tree row's context menu and in the 3D view's context menu when a port
is under the pointer or selected. It runs the existing Move (base point, then target, snapped; X/Y/Z lock) on the
port's rectangle. **R-dfr34-1-2 — ports rank first in the 3D pick stack**, as in the layout editor, and a second press
at the same point cycles to what is underneath. Ports drawn over the geometry (not depth-tested) while the Ports row or
the port tool is active, so a port inside a via or a pad is seen.

## 2. A 3D port's return cannot be stated

**The gap.** The `.c3d` port model has `Negative`, but the Properties panel shows neither `Positive` nor `Negative`.
The automatic choice took Inner 1, the nearest ground-reference plane. Inner 1 had an opening under the pad, so the
port was refused, and nothing in the 3D view let the user pick Inner 3, which was solid there.

**R-dfr34-1-3 — Return in the port's Properties:** a picker (owner rule: a picker, not a text box) listing Automatic,
then every conductor in the stack top to bottom with ground-reference ones marked, then "Same layer (coplanar)" (§3).
Writing it sets the port's `Negative`. The refusal text names this control when the setup belongs to a `.c3d`.

## 3. Openings and coplanar ground

**The gap.** The walk stops at the first ground-reference plane, short or not. A plane whose metal *surrounds* the
line's end (an anti-pad, a clearance under a pad) is an opening, not a pulled-back plane, and the walk should pass
through it to the next one. The walk also skips the signal layer, so a pad ringed by same-layer ground cannot return
there.

**R-dfr34-1-4 — an opening is walked through.** When the plane's metal lies on both sides of the line's end along the
line, and the line's end is inside a hole in it, the walk continues to the next plane and says so in a note naming
the opening's layer and size. **R-dfr34-1-5 — unmarked solid metal is a return when nothing marked reaches**: if a
conductor that is not marked ground covers the whole sheet before any marked plane does, return through it with a
note, rather than refusing. **R-dfr34-1-6 — a coplanar return** (Return = Same layer): the sheet runs from the line's
end across the gap to the nearest same-layer metal on the same net as ground. *Decision D1 for the owner:* is
same-layer ground a lumped port across the gap (two sheets, one each side, in parallel), or one sheet to the nearer
side? Recommend two sheets, which is how a GCPW launch is usually modelled.

## Gates

- On a fixture board shaped like the field case (round pad, opening in the first two inner planes, solid third plane):
  New 3D View from Layout brings P2 in returning through the third plane, with the opening note (R-4).
- Return = a named conductor, in the Properties picker, round-trips through the `.c3d` and is what the run uses (R-3).
- A coplanar return on a GCPW fixture solves, and its |S21| over 1–6 GHz agrees with a wave-port reference within
  0.1 dB (R-6).
- UiSnapshot pictures of the context menus and the Properties picker, inspected (R-1, R-3).
