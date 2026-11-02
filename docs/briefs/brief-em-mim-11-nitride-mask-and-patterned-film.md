# Brief MIM-11 — the nitride is a mask, and the film it defines is modelled everywhere

Two separate things, and they are worth keeping apart because one is achievable and one is not.

- **The MASK is missing and should exist.** In this process the MIM nitride is streamed out as its
  own layer and becomes its own mask. `KIT_MIMCAP` draws no nitride at all, so a `.gds` written from
  this kit is not manufacturable and a DRC deck has nothing to check. **This is a plain gap and MIM-11
  closes it.**
- **The FILM cannot be laterally patterned in a 2.5D solve, and pretending otherwise would be worse
  than the current state.** The layered Green's function is built on laterally infinite strata;
  `PatternedDielectric`'s own header states it — *"the 2.5D premise cannot express that laterally —
  inside a run every dielectric is laterally infinite"*. Drawing a nitride polygon cannot change
  that, and a drawing layer that looked like it did would be a worse lie than no drawing layer at all.

The user's suspicion is **confirmed**, and there is no configuration that avoids it. With `MIM Metal`
among the analysis levels the 0.2 µm εᵣ = 6.8 band is present across the entire plane, over every turn
of the spiral and everywhere else — and `MIM Metal` is among them either way. The shipped `.cem` names
no levels at all since the pinned-level fix (2026-09-16), so the extractor's default takes **every
level carrying artwork**, which on a layout with a capacitor in it is all three; the owner's own copy
predates that fix and names the same three explicitly. Leaving `MIM Metal` out is not an option
either: MIM-12 needs it in, and without it the capacitor is not in the answer at all. `PresentWithLayer` is a **per-run
on/off switch**, never a lateral pattern: `PatternedDielectric.Deactivate` turns the film to air when
the plate level is not in the run, and does nothing at all when it is.

## What the infinite film actually costs, measured

Before designing around it, here is its size. Measured 2026-09-16 on a Metal1 line with a DETACHED
Metal2 island placed well clear of it — so Metal2 is an analysis level, the medium reaches past the
film rather than truncating at it, and no via crosses the film's interface. The island is identical in
both runs, so the difference is the film alone. `Epsr` is the only thing that moves, 6.8 against 1.0:

| quantity | change |
|---|---|
| max \|ΔS\| | 2.26e-3 |
| phase of S₁₁ | **+0.108°** |
| a Metal1 gap capacitance | **−2.68 %** |

**So it is a second-order error, not the blocker.** It is worth fixing for correctness and for the
mask, and it is worth SAYING, but nobody should spend a week on it expecting the resonance to appear.
MIM-12 is where the resonance is.

**A trap found while measuring this, worth knowing.** The medium is built from the ground plane up to
the TOPMOST analysis level and terminated in an air half-space there, so on a Metal1-only run the
film above Metal1 is discarded entirely — two runs differing only in `Epsr` = 6.8 against 1.0 came
back **bit-identical**. The engine says so in a warning, and the warning is correct. Any measurement
of a superstrate has to put a level above it first.

**A second effect, the same size, that nobody has named.** `MIM Metal` entering the run also makes
`PatternedDielectric` stop reverting `Metal1`'s `SheetAt`, so Metal1's analysis sheet moves from the
BOTTOM of its band to the TOP — **z = 100 µm becomes z = 103 µm**. Measured on the same line:
Z_c 86.749 → 87.418 Ω (+0.77 %), gap capacitance +1.51 %. **Putting a capacitor anywhere in a layout
therefore changes the modelled height of every Metal1 conductor in it.** That is MIM-6 working
exactly as designed and it is the right trade, but it is invisible today and it is the same order as
the film itself. It belongs in the level note.

## What to build

**1. `KIT_MIMCAP` draws the nitride.** A `Nitride` (layer 6) rectangle enclosing the `MIM Metal` top
plate by a stated enclosure. The kit's own comment currently argues the opposite —

> **Nothing here draws the dielectric.** It is a stackup layer declared present wherever `MIM Metal`
> is drawn, so the top plate IS the statement that it is there; a drawn one would be a second
> insulator under the first.

— and that argument was sound for the stackup as written and is **wrong for manufacturing**. Replace
it, do not delete it: the new comment says the nitride is drawn because it is a MASK, and that the
stackup consumes it through `PresentWithLayer` so there is still exactly one insulator. Both
`kit.py`'s module-level `MIM_DIELECTRIC` note and `mimcap`'s docstring say this today and both must
change together. The enclosure is a process number the kit does not have — `tech` carries layers and
a stackup and no DRC rules — so it takes the same `_min_feature_dbu(tech)` route the plate enclosure
already takes, and says so.

**2. `PresentWithLayer` accepts a DRAWING layer as well as a conductor name.** Today it resolves a
conductor stackup entry by name. Let it also name a drawing layer, so
`"PresentWithLayer": "Nitride"` means *this film is present in a run whose layout carries Nitride
artwork*. The semantics are unchanged — it is still the per-run on/off — but the file now says the
true thing (the nitride mask defines the film) instead of a proxy (the top plate does). It also
decouples the film from the plate, which is what lets a nitride-only structure be drawn at all.
`TechValidation` already resolves this field by name and must learn the second namespace; an
unresolvable name keeps today's behaviour, which is to leave the film ACTIVE and say so.

**3. The extractor states the approximation, with a number.** Today the run's level note lists the
band in its medium string — *"0.103 mm εᵣ=12.9 | 0.0002 mm εᵣ=6.8 | 0.0028 mm εᵣ=1"* — and **nothing
anywhere says that band is laterally infinite**, or how little of the layout actually has nitride on
it. A reader who knows the stackup sees a correct list and no approximation. There should be a note,
and it should carry the coverage fraction, which the extractor can compute from artwork it already
walks (the layout extent it measures for the detail floor, against the nitride polygons item 2 makes
it resolve). The percentage below is illustrative:

> `MIM Dielectric` is a patterned thin film (0.2 µm, εᵣ = 6.8) and this run carries it. **The 2.5D
> medium has no way to make a dielectric laterally finite, so it is modelled across the whole plane**
> — including under metal that has no nitride over it. Nitride artwork covers 4 % of this layout's
> extent. On Metal1 interconnect the error this introduces is of order 0.1° of phase and a few per
> cent of a fringing capacitance (§MIM-11); it is not the reason a capacitor reads wrong (§MIM-12).
> To model the interconnect without it, run the coil and the capacitor separately (§MIM-10).

and, beside it, the sheet move:

> `Metal1`'s analysis sheet is on the TOP of its band because a patterned film sits above it, so this
> run's Metal1 is at z = 103 µm rather than 100 µm. A run with no capacitor in it puts it at 100 µm
> and is the run to compare against.

**4. The `.ctech` ships the change.** The MMIC entry in **`src/Ui/Layout/StarterTechnologies.cs`**
(not `src/Design/Layout/ShippedTechnologies.cs` — both exist and only the former carries this
stackup) gets `Nitride` wired as the film's `PresentWithLayer`, and
`examples/PDK PCells/tech/mmic-GaAs_2LM_100um.ctech` moves with it. An existing `.cws` naming
`MIM Metal` keeps working — the conductor spelling is not removed.

## Explicitly NOT in this brief

- **Making the film laterally finite.** It cannot be done in this kernel and it is not a deferral, it
  is a property of the formulation. The routes that exist elsewhere — volume-equivalent currents over
  the film, or a dielectric-brick region — are a different solver, not a setting, and none of them is
  justified by a 0.1° error.
- **Changing what MIM-12 changes.** Nothing here moves a number in a solve.
