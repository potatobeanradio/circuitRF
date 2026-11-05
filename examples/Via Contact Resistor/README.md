# Via Contact Resistor

A thin-film resistor contacted through vias, on a silicon back-end. The technology, `tech/rfic-Si_3LM_TFR_100um.ctech`, is an example with
generic round numbers, taken from no process.

The film has a level of its own between Metal1 and Metal2. The `TFR` in the schematic chooses `Resistor`, 50 Ω/sq, in its `Film` row, and the
part draws a Metal2 head and a contact via at each end. Its length is the distance between the vias' inner edges.

## What it shows

**Metal can cross the film.** The Metal1 line under the resistor joins the input through `Via12` and passes under the film without touching
it, and `circuitrf lvs` reads one resistor between two nets. On a level with metal, the same crossing would short that part of the film.

**The film chooses the contact.** Change `Film` to `Resistor Hi`, 500 Ω/sq, and the readout follows. It is the same level, so the artwork
keeps its shape. A film on a level with metal would draw overlap contacts instead.

**The substrate is a constant loss tangent.** `High-resistivity silicon` is tanδ 0.0015, its value at 10 GHz for 10 kΩ·cm. Real conduction
loss falls with frequency and a constant tanδ does not, so read its substrate loss as correct near 10 GHz only.

## Running it

`ViaResistor/em/ViaResistor.cem` is a planar run of all four levels at DC, with a coarse mesh and no edge mesh. It took 3 s and 3.8 GB on one
machine, and reads 251.4 Ω against the part's 250 Ω: a via-contacted film reads long by up to half a via cell at each end.

A full-wave point on this artwork is refused on a 16 GB machine: the calibration standards alone need about 30 GB, at the coarsest mesh
tried, because a 100 µm substrate asks for long standards and the stack's thin levels ask for small cells. Solve it in 3D on Palace if you
need the sweep. EMerge cannot take this artwork, whose Metal1 and Metal2 are both closer to the film than the volume it solves a film as,
and openEMS needs a cell the size of the thinnest gap, which costs hours.
