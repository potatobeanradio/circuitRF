# Brief ICF-6 — 3D on a thick die: where cut-port sheets land, and the air box

**Series:** ICF-0 … ICF-6 · **Tag:** `R-icf6-<n>` · **Depends on:** ICF-1 (R-icf1-7, the landing notes)
**Area:** `src/Design/Layout/Em3d/Em3dGenerator.cs` (`ReturnWalk`, `ReturnStated`, the default padding),
`src/Design/ThreeD/C3dCellCuts.cs`, `src/Design/Layout/Em/EmSetup.cs` (the cut entry), `docs/user/src/reference/em-setup.md`.

## The gap

**Landing.** On the field test's cascode in 3D, 7 of 8 cut-pin sheets ran ~755 µm down to the backside; one stopped on
local Metal1 ground 4 µm down. `ReturnWalk` takes the first unmarked level that fully covers the face and otherwise the
ground floor; a cut port has no way to state its return, though a drawn 3D port does (`EmPort3D(…, Return: layer)` →
`ReturnStated`). On a thick die, a sheet to the backside carries ~755 µm of vertical inductance into the result, which is
rarely what an IC designer means by a pin's port.

**Air box.** The default padding is 20·H with H from the BEOL (~0.85 µm), so ~17 µm round the metal while the port column
is ~760 µm tall; the silicon becomes a 37 × 110 µm pillar with air round it.

## Requirements

**R-icf6-1 — A stated return for cut ports.** A cut entry takes `Return: "<layer>"`, and its `Pins` map takes the same
per pin. It is passed into the cut-port generation as the drawn port's `Return` is, so `ReturnStated` and `ReturnWalk` are
reused unchanged. A layer that does not cover the face is refused naming the pin and what it found, as a drawn port's
stated return is. Absent, behaviour is unchanged. Planar ignores it (its ports are grounded or pin-referenced) and says so
once in the run's notes.

**R-icf6-2 — The landing is in the pin check.** With ICF-1's notes forwarded, `check` and `explain` show each cut port's
landing and its height. When a sheet falls to the floor through more than a stated fraction of the shortest wavelength in
the sweep (start at λ/20 in the substrate), `check` warns, naming the pin and `Return` as the remedy.

**R-icf6-3 — The air box is padded from the port column.** When any port sheet reaches the undrawn ground floor, the
lateral padding is taken from the column height (floor to top metal), not from the BEOL thickness. The substrate extends to
the air box's lateral faces rather than ending at the artwork's extent, so the die is not a pillar. A stated padding in the
setup always wins. A setup with no port to the floor keeps today's box byte for byte.

## Gates

Assembled-problem tests only, no solve:
1. A cut entry with `Return: Metal1` lands every sheet on Metal1; the same entry without it lands as today.
2. A `Return` layer that does not cover a face is refused with the pin and the reason.
3. `check` warns on a sheet falling ~755 µm on a thick-silicon fixture, and not on one landing 4 µm down.
4. With a sheet to the floor, the box's lateral padding follows the column height and the substrate reaches the box; a
   setup with no floor port assembles byte-identically to `main`.
5. One by-hand EMerge run on the fixture, time-capped, before and after R-icf6-3, recorded in `src/Design/RESOLVED.md`.

## Not here

A different port construction for tall columns. Planar's tall uncalibrated port (ICF-4).
