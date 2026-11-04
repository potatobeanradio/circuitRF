# Brief DFR34-2 — the air box a 3D view starts with

**Series:** designer feedback round 34. · **Tag:** `R-dfr34-2-<n>`

The default air box made from a layout is still wrong in two field cases this round, after three earlier fixes. Each
of those fixes tuned the floor rule. Neither case below reaches it.

Read first: `Em3dGenerator.DefaultPadding` and `DefaultFacePadding`, `Em3dDefaultPadding.For`, `ByHeightApplies`,
`C3dProblemAssembly.PlaneFloor`, `Em3dRunService.FarFieldFrequencies`, and the air box rows in `C3dPropertiesViewModel`.

## 1. A radiating setup pads every face by a quarter wavelength at the lowest frequency

**The gap.** A 2.4 GHz PCB antenna cut from a larger board (44 × 95 × 1.5 mm) kept the new-setup default sweep,
0.5–8 GHz, with a radiation pattern on. `DefaultPadding` gives λ/4 at the lowest frequency on all six faces, 150 mm
each. The air box was about 340 × 395 × 301 mm around a 1.5 mm board. The Properties panel showed it as
338 % / 158 % / **9986 %**, because padding there is a percentage of the content's extent along that axis. The
height-based rules from round 33 are gated off for a radiating setup (`ByHeightApplies` requires `!RadiationPattern`).

**R-dfr34-2-1 — radiating padding from the pattern's frequency, not the sweep's start.** Use the lowest frequency the
pattern is asked at (or the band of interest when one is stated), λ/4 there. Cap each face at a stated multiple of the
structure's largest dimension, with a note when the cap applies. *Decision D1 for the owner:* the cap — recommend
λ/4 at the pattern frequency, floored at λ/8 at the sweep's lowest, since the far-field transform needs the boundary
in the far-ish field only at the frequencies the pattern is read at.
**R-dfr34-2-2 — a new setup's default sweep is not 0.5–8 GHz for an antenna.** When the layout has one port and an
open structure, offer the sweep around the frequency the user types, or ask. Small: one default.

## 2. The floor's padding is measured from the top of the stack

**The gap.** On a 10-layer board, the −Z face got 10 × H, where H is the dielectric under the **top** metal. The
floor-on-a-plane rule did not apply because it needs a ground-reference plane flush with the bottom. The board was
imported with a shipped technology that marks only Inner 1 and Inner 8 as ground, and the bottom copper (a 91 % pour)
is unmarked. The number was reasonable here only because the top and bottom dielectrics are equal.

**R-dfr34-2-3 — the −Z face measures H from the bottom metal upward**, as +Z does from the top down.
**R-dfr34-2-4 — the floor rule takes the bottom-most conductor whose coverage is at least 95 %, marked or not**, and
says in a note that it treated it as a floor. Ground-reference flags on an imported board describe the technology
file, not the board.

## 3. The panel

**R-dfr34-2-5 — padding shown in length with its basis** ("150 mm — λ/4 at 0.5 GHz", "0.69 mm — 10 × H"), with the
percentage as secondary text or not at all. A percentage of a 1.5 mm extent is not a number anyone can judge.

## Gates

- The antenna fixture with a pattern at 2.45 GHz: every face λ/4 at 2.45 GHz (30.6 mm) or the cap, not 150 mm (R-1).
- The 10-layer fixture with an asymmetric stack (bottom dielectric half the top's): the −Z padding follows the bottom
  (R-3), and a floor on an unmarked 95 % bottom pour (R-4).
- Existing air box gates unchanged, unless a gate's own comment shows it held the defect.
- UiSnapshot of the air box Properties rows, inspected (R-5).
