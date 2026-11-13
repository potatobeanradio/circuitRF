# 3D Wave Ports

How to set up and run a **wave port**, on Palace and on openEMS. A wave port turns a region of the air box's face
into a matched continuation of the line that ends there: the line is fed and measured in its own field, so there is no
gap and no parasitic of the port's own in the answer. This workspace has three cells that use one:

- **Launch** is the **3D Connector** example's launch with its back end open: the coax runs to the air box's face, and
  that end is a wave port. Set against the 3D Connector's lumped gap port, it shows what the gap was costing.
- **Pair** is an edge-coupled stripline pair with **two conductors ending on each face**. Each strip end is its own
  port, a **terminal** of the face's wave port, so the pair is a four-port. It runs on openEMS (below says why not on Palace).
- **Coupled Microstrip** is a pair of microstrip lines of **different widths** in a closed housing, with a two-terminal
  wave port at each end, run on **both** solvers: on Palace, each end is one port face carrying both strips' modes,
  converted to one port per strip.

The reference chapter is **EM Setup ▸ Wave ports**, and its section **Several conductors on one face**, in
**Help ▸ circuitRF Documentation**. This page is the walk-through; it does not repeat that chapter.

Opening, orbiting, `check` and `explain` need no solver. The Launch needs the geometry kernel (its housing is a boolean
and its pin is filleted, as in the 3D Connector), and **Run** needs Palace or openEMS; the first press without one
offers to install it.

## What is here

| Cell | View | What it is |
|---|---|---|
| **Board** | layout, mil | The 3D Connector's board, unchanged: 20 mil PTFE-glass laminate and a 62 mil line |
| **Flange** | 3D view, mm | The 3D Connector's flange, unchanged, the source of `Launch/3d/flange.step` |
| **Launch** | 3D view, mm | The 3D Connector's launch with the bore cut through the housing's back and the pin run to it. P1 is a wave port on the air box's xmin face; P2 is still the board's lumped port. Setups *Palace* and *openEMS* |
| **Pair** | 3D view, mm | Two copper strips of zero thickness in a PTFE fill between two ground planes, 15 mm long, with a two-terminal wave port at each end. Setup *openEMS* |
| **Coupled Microstrip** | 3D view, mm | Two copper strips of zero thickness, 1.2 mm and 2 mm wide and 0.3 mm apart, on the 20 mil PTFE-glass laminate, 15 mm long, in a housing whose floor, sides and lid are PEC, with a two-terminal wave port at each end. Setups *Palace* and *openEMS* |

One technology, `tech/board-and-connector.ctech`, the 3D Connector's, serves all five.

**The Launch's coax** is the 3D Connector's: `pin_d` = 0.4 mm inside `bore_d` = 1.34 mm of PTFE (εr 2.1). Its
impedance by the closed form is Z₀ = η₀/(2π√εr)·ln(D/d) = **50.02 Ω**. The 3D Connector's README says 50.06 Ω because
it writes η₀/2π as 60; this page uses η₀ itself. The only change from that launch: the bore and the pin now run to
the housing's back face at x = −5 mm, which is the air box's xmin face (its padding is 0).

**Pair's section**, stated once as the cell's VARs: strip width `w` = 1.65 mm, gap `s` = 0.4 mm, ground-plane spacing
`b` = 2 mm, length `len` = 15 mm. The strips are sheets, because Cohn's closed form for this line is exact only at zero
thickness. The fill's top and bottom are the air box's **PEC** faces (the two ground planes); its sides are **PMC**
faces, 10 strip widths out from each strip, standing in for an unbounded plane; its ends are the two wave ports. By
Cohn's closed form, the even mode's impedance is **57.51 Ω** and the odd mode's **43.10 Ω**: they straddle 50 Ω, so
the coupling is easy to see, and the line as a whole is matched to 50 Ω.

**Pair's numbering.** The xmin face's terminals are 1 (strip a) and 2 (strip b), and the xmax face's are 3 (strip a)
and 4 (strip b): **S21 is the near-end coupling, S31 the thru and S41 the far-end coupling.**

## Launch, by hand

1. Open **Launch**'s 3D view and look at the object tree's **P1**: a wave port on the xmin face, its rectangle over
   the coax. The overlay draws its **voltage-path arrow from the housing to the pin**, across the PTFE.
2. P1 states nothing but its rectangle, 2 mm square and centred on the axis. To draw it yourself, delete it and use the
   **Port** tool (**Shift+A P**) on the YZ plane at x = −5 mm, the air box's xmin face, with two corners 1 mm either
   side of the axis. The pin lies inside the housing on that face, so the arrow is inferred along a ray from the bore's
   wall to the pin, and the status line says so: *"… the path runs from 'housing' to 'pin' along a ray, because
   'housing' encloses 'pin' on the face."* Read the arrow before a run.
   Right-clicking the bore's end face (the PTFE disc round the pin) and choosing *Make Port ▸ Wave* makes the same port
   on the 1.34 mm square round the disc. Both solvers run that one too. openEMS's current probe round the pin needs a
   grid cell of room from the housing, which is why this setup's openEMS section states a grading ratio of **1.15**: at
   the default 1.3 the cells in the bore grow too fast from the pin, and openEMS refuses either rectangle, naming the
   clearance it is short of.
3. **Simulate ▸ Setup Analyses…**: run *Palace*, then make *openEMS* the active setup and run it.
4. Compare |S11| with the 3D Connector's (its *fillet* runs, below). At 18 GHz the lumped gap port read **−40.75 dB**
   on Palace and **−19.03 dB** on openEMS; the wave port reads **−19.53 dB** on Palace and **−21.38 dB** on openEMS.

The deep null the 3D Connector showed at 18 GHz on Palace was the gap's capacitance tuning out the launch's own
reactance. With the gap gone, the launch's |S11| is about −20 dB across the upper band on both solvers: that is the
launch itself. A connector that ends inside the model rather than on the box's face is fed across the same ring of PTFE by
a **coaxial port** instead (**EM Setup ▸ Coaxial ports**; the **Loop Sensor** example feeds a real-size SMA that way).

## Pair, by hand

1. Open **Pair**'s 3D view. Each end face carries one port with **two numbered arrows**, one per strip, each running up
   from the bottom ground plane (`airbox/zmin`, the port's **reference**) to its strip.
2. To make them yourself, delete both ports, right-click the fill's end face at x = 0 and choose *Make Port ▸ Wave*,
   then do the same on its end face at x = 15 mm. Each makes one port, named after its face, with a **terminal** per
   strip: 1 (strip a) and 2 (strip b) on xmin, 3 and 4 on xmax. The two ground planes are the air box's PEC faces,
   which count as one reference, and the status line names it: *"'airbox/zmin' is the reference: the air box's PEC
   faces are one ground; 'airbox/zmin' names it."*
3. **Terminal S** means each strip end is a port of its own: the voltage from the reference to that strip, the current
   on that strip. The result is a four-port, numbered as above, so S21 is the near-end coupling, S31 the thru and S41
   the far-end coupling.
4. Run *openEMS*. Open the result in a **Data Display** and plot dB(S31), dB(S21) and dB(S41). Cohn's ideal coupled line
   gives at 10 GHz **−0.088 dB**, **−16.98 dB** and **−64.47 dB**; openEMS reads **−0.092 dB**, **−16.94 dB** and
   **−60.88 dB**. The near-end coupling peaks where the line is a quarter wave long (3.4, 10.3 and 17.2 GHz) and falls to
   nulls between; the far-end coupling of a homogeneous line is zero, and what is left is the residue of the small
   reflections at each end.

## Coupled Microstrip, by hand

1. Open **Coupled Microstrip**'s 3D view: strip **a** (1.2 mm) and strip **b** (2 mm) on the laminate, the floor the
   ground, the housing's sides 4.5 mm out from the centre and its lid 3 mm above the laminate, all PEC. Each end face
   carries one port with two terminals, numbered as Pair's: 1 (a) and 2 (b) at x = 0, 3 (a) and 4 (b) at x = 15 mm, so
   **S31 is a's thru, S21 the near-end and S41 the far-end coupling, and S11 and S22 are the two lines' reflections.**
   Each terminal's arrow runs up from the floor (`airbox/zmin`, the reference) to its strip.
2. Run *Palace*, then *openEMS* (2–12 GHz in 21 points). Plot dB(S11) and dB(S22) from both on one Data Display: the
   two lines reflect differently, which no symmetry route could have produced. At 2 GHz Palace reads **−21.74 dB** and
   **−15.48 dB**, openEMS **−21.00 dB** and **−15.72 dB**.
3. Read Palace's notes: one per port says its terminals were converted from the face's two modes (*modes not
   degenerate, Robin correction K = 1.048 to 1.060*), and one gives each terminal's power and the singular values of S.

## What each solver did

**On the Launch, Palace** solved the coax's own mode on the face at every frequency and projected the field onto it.
It reports the mode's impedance, **49.18 to 49.31 Ω** over the sweep, against the closed form's 50.02 Ω, and the run's
note says it renormalised the port to 50 Ω. That 1.7 % is the *Draft* preset's first-order elements: at element order 2
(8 min 30 s, 8.9 GB) the same run reports 50.13 to 50.26 Ω, and |S11| moves to −25.02 dB at 10 GHz and −21.44 dB at
18 GHz. As on the 3D Connector, Draft's |S11| is a first look, not a converged one, and the port is not what limits it.

**On the Launch, openEMS** computed no mode. It grew its grid out past the face, carried the coax through that
extension, fed it from the far end with a radial source, and measured voltage and current on three planes around the
face. The run's note reports what that line measured: **54.59 Ω**, **ε_eff 3.08**, where the coax is 50.02 Ω and
2.1. **On this grid openEMS does not resolve the coax**: its Cartesian cells staircase the round pin, and at this
setup's grid (20 cells per wavelength, grading 1.15) the pin is 3 cells across. The run's note names the staircase; its error
falls with the cell, but a coax needs about 20 cells across its pin to come within a few percent, and openEMS's grid
has one largest cell for the whole problem: at 20 cells across the pin, this one would be 137 million cells. A cylindrical grid, which is exact
for a coax, cannot be used either: the coax meets a board. **So on this cell, Palace's answer is the one to trust**, and
openEMS's shows what its port does, not what this connector does.

**On Pair, openEMS** fed each strip on its own, one run per terminal, four runs, and built S from all four runs
together: a coupled pair whose ends run into the feed's absorber is terminated in the pair's own impedances, so no
single run gives a column of S by itself. Each terminal's line measured **50.17 Ω** and **ε_eff 2.10** with the others
passive, and the whole S matrix lies within 0.003 of Cohn's ideal line at every frequency from 2 to 18 GHz. The grid is
90 cells per wavelength, which puts 8 cells between each strip and a ground plane: with 4, a stripline's impedance
reads about 5 % low.

**Why Pair has no Palace setup.** Pair's two strips sit in one homogeneous fill, so their two modes travel at one
speed, and a Palace run refuses such a face before its 3D solve, naming openEMS (**EM Setup ▸ Wave ports ▸ On
Palace**). Palace returns two such modes in an arbitrary mixture, drawn again every time it solves the face: over a
sweep Pair's modal S came back non-reciprocal, and even solved one frequency at a time one face's two modes came back
as nearly the same field at 10 GHz. Giving Pair PEC sides (its PMC sides leave the two ground planes separate
conductors on the face, a third mode) or perfect-conductor strips does not change that, so they were not taken further.

**On Coupled Microstrip, Palace** solved each end face's two modes, which travel at different speeds (K, the ratio of
their wavenumbers, is 1.048 to 1.060), launched each in turn, and converted the modal S to one port per strip. Its
adaptive sweep writes no wavenumber, so the run also solved each face's modes on their own at five of the sweep's
frequencies, a few seconds each. Its own check warns at 12 GHz that terminal 4's voltage is 0.11 % off Palace's mode
impedance, just past the 0.1 % it warns at: that is the copper's loss, which the conversion was not measured with.

**On Coupled Microstrip, openEMS** fed each strip on its own as for Pair. The two agree to max |ΔS| 0.008–0.027 over
the whole band: at 10 GHz Palace reads |S11| **−17.38 dB** and |S22| **−16.08 dB**, openEMS **−17.82 dB** and
**−15.30 dB**. Two things to know:
- **The lid.** Each strip has PEC on both sides of it, the floor below and the lid above, so openEMS's port feeds it
  from both, as it feeds a stripline. But a strip on the laminate with air above it is a microstrip, and its voltage to
  the lid is not its voltage to the floor, so the port reads the floor's alone: the reference the terminal's arrow runs
  from, as Palace does. The run's notes say so for each end face. A strip with one material on both sides, as in Pair,
  is read to both planes.
- **The loss.** The laminate's loss tangent is a conductivity to openEMS, exact at the band's centre only, and the
  strips are **copper**, the technology's metal, which each solver models its own way on a sheet of zero thickness:
  Palace's terminals keep 97.9–99.1 % of their power, openEMS's 96.4–100.1 %. Perfect-conductor strips would take that
  difference away; whether the example should use them is left open, so it uses the technology's copper.

## The numbers

Every time and size below was **measured once**, on an Apple M4 with 10 cores and 16 GB, running macOS 27.0, Palace
0.18.1 on 10 processes, Gmsh 4.15.2, openEMS 0.37.0-rc3 and OpenCASCADE 8.0.1. A time is the wall clock for the whole
run; memory is Palace's own peak over all its processes; openEMS's size is the number of cells in its grid. Your
machine will differ.

| Run | Time | Size | \|S11\| at 10 GHz | \|S11\| at 18 GHz | \|S21\| at 18 GHz |
|---|---|---|---|---|---|
| Launch, Palace (Draft) | 1 min 5 s | 4.5 GB | −21.71 dB | −19.53 dB | −0.562 dB |
| Launch, openEMS (20 cells per wavelength, grading 1.15) | 2 min 17 s | 937,440 cells | −19.36 dB | −21.38 dB | −0.452 dB |

| Coupled Microstrip | Time | Size | \|S11\| / \|S22\| at 2 GHz | at 6 GHz | at 10 GHz |
|---|---|---|---|---|---|
| Palace (element order 2, no refinement passes) | 14 min 34 s | 5.1 GB | −21.74 / −15.48 dB | −24.31 / −18.63 dB | −17.38 / −16.08 dB |
| openEMS (300 cells per wavelength) | 7 min 28 s | 6,053,568 cells | −21.00 / −15.72 dB | −24.05 / −18.27 dB | −17.82 / −15.30 dB |

| Coupled Microstrip, thru \|S31\| / near-end \|S21\| / far-end \|S41\| | at 2 GHz | at 6 GHz | at 10 GHz |
|---|---|---|---|
| Palace | −0.149 / −19.10 / −35.94 dB | −0.139 / −21.31 / −24.19 dB | −0.314 / −16.63 / −19.57 dB |
| openEMS | −0.096 / −19.43 / −35.56 dB | −0.077 / −21.56 / −23.52 dB | −0.218 / −17.43 / −19.07 dB |

| Pair, openEMS (90 cells per wavelength), at 10 GHz | Time | Size | \|S31\| thru | \|S21\| near-end | \|S41\| far-end |
|---|---|---|---|---|---|
| openEMS | 52 s | 1,033,923 cells | −0.092 dB | −16.94 dB | −60.88 dB |
| Cohn's ideal line | | | −0.088 dB | −16.98 dB | −64.47 dB |

`check` on this workspace reports no error and one warning, the 3D Connector's: openEMS will not represent the pin's
100 µm fillet. The Pair's openEMS runs also warn that openEMS chose a time step about 1.45 times circuitRF's own
estimate; that is the estimate disagreeing, not the answer.

**Settings, and what they trade.** The Launch's setups are the 3D Connector's: Palace's *Draft* preset (element order 1,
no refinement passes) and openEMS's default grid but for its grading ratio, 1.15 against the default 1.3, each 2–18 GHz in
5 points, so a first run takes a minute or two.
Palace at element order 2 is above: eight times the time and twice the memory, and |S11| moves by 3.3 dB at 10 GHz and 1.9 dB at 18 GHz. The grading is
the one openEMS setting this coax needs (above): it buys the current probe its cell of room for 937,440 cells against
526,768, and more cells per wavelength would not, since the cells in the bore are set by the grading from the pin, not by
the wavelength. The Pair's setup is 2–18 GHz in 33 points, which costs openEMS nothing (one run
covers the band) and shows the coupling's shape; its 90 cells per wavelength are the cost of 8 cells per strip height.
Coupled Microstrip's Palace setup is the *Standard* preset's element order 2 with no refinement passes (with Standard's
two passes the estimate is 8.5 GB) and four mesh regions, 20 µm boxes along the strips' edges; its openEMS setup takes
300 cells per wavelength and a 5 µm smallest cell (6.05 million cells; the grid's own smallest is 36 µm). Both sweep
2–12 GHz in 21 points, below the housing's own modes: Palace's check finds the faces' third mode evanescent at 12 GHz.

## What is deliberately not here

- **Mixed-mode S-parameters** (Sdd, Scc): circuitRF plots terminal S; a differential view of the pair needs a
  mixed-mode conversion it does not have yet.
- **A hollow waveguide on openEMS**: a region met by one conductor has no voltage between conductors, and openEMS would
  need a mode-matching port, which circuitRF does not build. Palace runs one.
