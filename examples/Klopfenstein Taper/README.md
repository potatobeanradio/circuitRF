# Klopfenstein Taper — closed form vs MoM

A 50 → 100 Ω Klopfenstein taper, 15 mm long, on 30 mil RO4350B. The same structure is described
twice, and the point of the example is to run both and put the two S11 curves on one plot.

## The equation-based side

`Taper/schematic/Taper.csch` places an **MKLOPF** between a 50 Ω and a 100 Ω Term and sweeps
0.5–12 GHz. It solves in well under a second. The substrate (H, εr, tanδ, metal thickness and
conductivity) is not typed in anywhere — MKLOPF reads it from the workspace's technology, which is
the same `.ctech` the EM setup reads.

## The EM side

`Taper/em/Taper-MoM.cem` runs the **full-wave planar** kernel over the identical artwork in
`Taper/layout/Taper.clay`, with an edge port at each end, 20 cells per wavelength and edge meshing
on. It takes about a minute. Press **Simulate** with the `.cem` open, or run

```
circuitrf em "Taper/em/Taper-MoM.cem"
```

No EM results ship with this example — the whole point is to generate them.

The `.clay` is drawn artwork, not a placed MKLOPF instance, so that the EM run works straight
from a clone with nothing generated first. That also means **Update Layout from Schematic** has
nothing here to recognise as `TL1`: run it and the taper is placed a second time, on top of the
one already drawn. circuitRF says so when it happens — undo, or delete one of the two.

## Comparing them

Port 2 is referenced to **100 Ω**, not 50. The `.npy` the EM writes carries the per-port reference
impedances and is the file to plot; the companion `.s2p` can only state **one** reference impedance
in its header, so reading S11 back from the Touchstone file compares the taper against the wrong
termination and makes it look far worse than it is.

Expect close agreement through the rolloff (about −10 dB at 1 GHz, −20 dB by 5 GHz) and a visible
difference in exactly where the deep nulls land above 8 GHz — the closed form is a cascade of ideal
uniform sections, while the EM sees the real staircased flare, the substrate's dispersion and the
de-embedding reference planes.
