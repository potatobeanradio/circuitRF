# Loadpull and Pursuit

Two ways to find a device's optimum load, over the same GaN model.

## Loadpull — a grid you specify

`Loadpull` terminates the device between a **SourceTuner** and a **LoadTuner**, both with their
bias tees on, and sweeps the load over the grid in `load_grid.gam`. At each grid point the engine
walks a drive ladder up to 3 dB gain compression and reports the operating point there — which is
what makes the resulting contours comparable point to point.

The grid file is plain text and worth reading:

```
# gamma Z0=50 mag_ang
0.0  0
0.20  0
0.20  30
...
```

The header tags say what the numbers mean. **With no `gamma` tag the file is read as impedance**, so
a grid written without one is a completely different sweep that runs without complaint.

`RFfreq` is authored as expression `2` with unit `GHz` in the VAR's own unit column. A unit written
**inside** the expression (`2 GHz`) is a parse error — circuitRF's grammar has no unit suffix — and
an unresolvable variable is skipped rather than reported where you typed it.

## LoadpullPursuit — a search, not a grid

`LoadpullPursuit` starts from a guess and walks toward the optimum, then draws two constant-VSWR
rings around what it found. It needs no grid file. The outer sweep takes it across 1.8–2.2 GHz;
a second, disabled sweep over `VDD` is there to enable when you want a surface.

Both benches carry `Meas` blocks reading `V` at the drain by net name and harmonic index — the same
accessor the harmonic-balance benches use, because a loadpull result is a cube like any other.

## Linked Plots — a display that follows its markers and controls

Run **Loadpull** and **LoadpullPursuit**, then open `Linked Plots.cdd`. Its plots are linked: a marker or a
control on one decides what another shows. Nothing in it simulates; it reads the two results.

**Load.** Drag **m1** anywhere on the Smith chart, between the measured loads (the dots) as well as onto one.
The plot on the right shows Gt and PAE against Pout *at m1's load*: dashed, because they are interpolated
from the loads around it, and solid once m1 sits on a measured load (Shift-drag puts it back on one). The
**At marker** table reads Pout, Gt, PAE and the load at the same point.

**Frequency.** The Pursuit bench searched for the optimum at 1.8, 2.0 and 2.2 GHz. The left plot is the
reactance of the maximum-power and maximum-efficiency loads at each frequency. Click m2's box and step it
with the arrow keys: the Pout and PAE contours on the right follow it to that frequency.

**Panel.** The controls are locked in place (right-click one for **Lock Position**), so a drag anywhere on
them works them rather than moving them.
- The **Frequency** knob turns with a drag (up or right turns it up) or the mouse wheel. The contours, the measured loads and
  every curve follow it.
- **m3** is free and **m4** sits on a measured load. The Pout and Gt curves at m3 are interpolated (dashed);
  the ones at m4 are what the run computed there (solid).
- The **Measured** button shows or hides m4's curves.
- The **Drive step** slider picks the drive the harmonics plot reads: the drain voltage's harmonics at m4.
- The readouts under them show each control's value.

Every one of these can be set without a window too: `circuitrf render "Linked Plots.cdd" --tab Panel
--control freq=2.2GHz --control measured=0 -o panel.png`.
