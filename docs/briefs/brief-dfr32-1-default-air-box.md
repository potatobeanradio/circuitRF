# Brief DFR32-1 — a default air box sized for a board, not a cube of air

**Series:** designer feedback round 32 (overview: `brief-dfr32-0-overview.md`). · **Tag:** `R-dfr32-1-<n>`

## The gap

A 3D setup that states no `AirBox` pads every face, top and bottom included, by
`Em3dGenerator.DefaultPadding`: the structure's largest extent, or λ/8 at the lowest frequency when that is smaller.
On a 17.5 × 4.3 mm region of a 0.27 mm-thick two-layer board, that put 17.5 mm of air on all six faces. The box was
about 52 × 39 × 35 mm round a board a quarter of a millimetre thick, so nearly all of the mesh was air. The Properties
panel shows the padding as a percentage of the content's extent along each axis, which made the z faces read
**6296 %**. The x and y faces read 100 %.

The workaround makes it worse in a way nothing reports. The designer typed 20 % on every face, and the run went
through. But 20 % of the z extent is about 54 µm, so the absorbing top face sat 54 µm above top copper on a 200 µm
substrate. A first-order absorbing face that close to a microstrip's fringing field changes the answer, and the run
said nothing about it.

## What to do

**R-dfr32-1-1 — measure first.** Do not change a default before the numbers are in. On three synthetic structures
(a 50 Ω microstrip line on a thin laminate, a via transition through a ground plane, a two-pole coupled-line filter),
sweep the padding at Draft over the new 0.5–8 GHz default and record |ΔS| against a large-box reference (today's
default), tetrahedra and Palace's peak memory for each:

- top face at 3, 5, 10 and 20 × H above top copper, where H is the dielectric height from the top conductor to the
  lowest ground-reference conductor;
- lateral faces at 3, 5 and 10 × H, and at today's structure's extent.

Keep each run to a few minutes (use the smallest structures that show the effect). Put the table in
`src/Design/RESOLVED.md`.

**R-dfr32-1-2 — the new default, if the numbers support one.** For a driven problem that does not ask for a radiation
pattern: pad by the smallest multiple of H, per axis group (lateral, top, bottom), that keeps |ΔS| ≤ 0.01 across the
band on all three structures. Cap it at today's value, so the box only shrinks. A static solve, a radiation pattern
and an eigenmode keep today's rules. `DefaultPadding`'s basis string names the rule ("5 × the 0.2 mm substrate
height"), so the panel and `explain` print it. **Go/No-go:** if no multiple of H meets 0.01 on all three, stop and
report the table. Do not pick a compromise silently.

**R-dfr32-1-3 — say when a stated padding is too close.** When a face's stated padding puts an absorbing face closer
to a conductor than the measured minimum, the run and the 3D view post a warning. The warning names the face, the
distance and the minimum. A conducting face is exempt, because it is meant to be close. This is a warning, never a
refusal: the designer may want a shielded box.

**R-dfr32-1-4 — the panel's percentage.** A z padding shown as a percentage of a sub-millimetre extent is unreadable
whatever the default is. Show each face's padding as a length beside its percentage ("54 µm, 20 %"). Do not change
what `PaddingPercent` means in the file, because files written with it must keep their boxes.

## Gates

- One test per claim: the default rule's value on a synthetic thin board; the warning on a too-close stated face and
  none on a conducting face; the panel's length readout.
- The Palace and openEMS geometry goldens move if the default changes. Regenerate them deliberately
  (`CRF_WRITE_PALACE_GOLDENS=1`), and confirm in the diff that only the box coordinates moved. Check the shipped 3D
  examples still open and their recorded results still load. If an example's result was recorded with the old box,
  say so in its README rather than re-running it unasked.
- Docs: `em-3d.md`'s air-box section states the new rule and the warning.
