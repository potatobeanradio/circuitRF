# 3D Eigenmode

**Is there a cavity resonance inside my band, and what moves it?** A line in a metal housing with a lid on is a line
in a **cavity**, and a cavity has resonant modes. The lowest has its electric field pointing from the floor up to the
lid, strongest in the middle, which is where the line runs. If that mode falls inside the band the line carries, the
housing couples the line to itself through it, and the transmission has a notch.

This workspace answers that question with an **eigenmode** solve, which finds each mode's frequency and Q directly,
on Palace and on EMerge, the two finite-element solvers circuitRF runs. A **driven** sweep of the same model then
shows the notch at the same frequency. It has two cells:

- **Cavity**: a 50 Ω microstrip through an aluminium housing. Its first cavity mode is inside the band.
- **Cavity with post**: the same housing with one grounded post from the floor to the lid, beside the line. The mode
  moves **above** the band and the notch is gone.

The reference chapter is **EM Setup ▸ Eigenmodes**, in **Help ▸ circuitRF Documentation**. This page is the
walk-through; it does not repeat that chapter. Opening, orbiting, `check` and `explain` need no solver. **Run** needs
the setup's solver — Palace for *Modes* and *Driven*, EMerge for *Modes (EMerge)* and *Driven (EMerge)* — and the
first press without it offers to install it. The walk-through below is Palace's; *On EMerge* repeats it on EMerge.

## What is here

| Cell | View | What it is |
|---|---|---|
| **Cavity** | 3D view, mm | An aluminium housing, a laminate on its floor, a 50 Ω line along its length, and the lid. A lumped port at each end of the line, 0.2 mm short of the end wall. Setups *Modes* (eigenmode) and *Driven* on Palace, and *Modes (EMerge)* and *Driven (EMerge)*, the same two on EMerge |
| **Cavity with post** | 3D view, mm | The same, plus one aluminium post, `post_d` across, from the floor through the laminate to the lid. The same four setups |

Two cells rather than one with a switch, so each opens and runs as it stands, and their results sit side by side.
One technology, `tech/housing-and-board.ctech`, serves both.

**The cavity, stated once as each cell's VARs** (the Variables panel): interior `a` = 22 mm along the line, `d` = 20 mm
across it and `h` = 4 mm high, walls, floor and lid `wall` = 1 mm thick. The laminate is `t_sub` = 0.254 mm of
PTFE-glass (εr 2.2, the technology's material) and the line `w` = 0.78 mm wide, which is 50 Ω on it. The post is
`post_d` = 2.5 mm across, centred on the cavity's length and `post_gap` = 0.8 mm clear of the line's edge. The ports
are numbers, not expressions: change `a` and move them yourself.

**The band** is X band, **8–12 GHz**, the band this line is meant for.

**The line is a sheet of perfect conductor**, not copper. With every metal lossy, Palace's search for the second and
third modes below did not finish (the reason is in *Run settings*). The housing and the post are aluminium, and their
loss is in every Q below.

## The question, in one closed form

An empty rectangular cavity a × d, with its field from floor to lid, has its lowest mode at
f = (c/2)·√((1/a)² + (1/d)²). For 22 × 20 mm that is **10.13 GHz**, just above the band's middle. That figure is an
**upper bound**, not a prediction: the laminate on the floor stores some of the field in εr 2.2, which pulls the real
mode down. It is the one number on this page that is not circuitRF's own output.

## Cavity: *Modes*

1. Open **Cavity**'s 3D view. **Simulate ▸ Setup Analyses…**, make *Modes* the active setup and **Run**.
2. The mode table:

| Mode | f | Q | Q unloaded | Most energy in |
|---|---|---|---|---|
| 1 | **9.956 GHz** | **539** | **3,236** | the air, 97 % |
| 2 | 10.18 GHz | 1.1 | lossless | the laminate, 83 % |
| 3 | 14.43 GHz | 1.4 | lossless | the laminate, 83 % |

How to read it:

- **f** is the mode's frequency. Mode 1 is 1.7 % below the closed form's 10.13 GHz: the laminate's pull.
- **Q** is the *loaded* Q, with every loss the problem has. Here that is the aluminium, the laminate, and the two
  ports: an eigenmode solve treats each 50 Ω port as a resistor that the mode drives. **Q unloaded** takes the ports'
  share out. Mode 1's Q of **539** against **3,236** unloaded says the ports, not the walls, are what damp it most.
  The ports are the rest of your circuit, so the loaded Q is the one that sets the notch's width: about f/Q, 18 MHz.
- **Most energy in** is each mode's energy participation: where it lives. Mode 1 lives in the air of the cavity.
- **Modes 2 and 3 are not resonances.** A Q of about 1 means the energy leaves within one cycle. These two rows are
  the line itself, between its two 50 Ω ports, with its field in the laminate under the strip. They have no notch, and
  nothing the post does moves them much. A cavity resonance is a row with a Q in the hundreds or more.

`circuitrf em "Cavity/3d/Cavity.c3d" --setup Modes` prints the same table.

## Cavity: where the mode lives

The object tree's **Field plots** has one saved plot, **Cavity mode**: |E| of mode 1 on a horizontal cut at
mid-laminate height, 0.127 mm above the floor. Tick it after the run. The field is strongest in the middle of the
cavity, on the line, and falls to zero at all four walls. That middle is where the post goes.

## Cavity: *Driven*

Make *Driven* the active setup and **Run**: Palace over 8–12 GHz, 801 points, a 5 MHz step. The step has to be
under the notch's width, which the loaded Q gave as about 18 MHz. At a 0.1 GHz step the two nearest points,
9.9 and 10.0 GHz, read −0.11 and −0.35 dB, and the notch would not be there to see.

Plot S21 in dB. It sits within 0.15 dB of 0 dB across the band, except within 0.1 GHz of one sharp notch:
**−11.69 dB** at **9.955 GHz**. That is the eigenmode's 9.956 GHz to within the 5 MHz step: the same resonance, found two ways. The
depth depends on which sample lands nearest the bottom of a notch 18 MHz wide; its frequency does not.

## Cavity with post: *Modes*

Open **Cavity with post** and run *Modes*:

| Mode | f | Q | Q unloaded | Most energy in |
|---|---|---|---|---|
| 1 | 10.22 GHz | 1.1 | lossless | the laminate, 83 % |
| 2 | **12.84 GHz** | **2,480** | **2,879** | the air, 97 % |
| 3 | 14.45 GHz | 1.4 | lossless | the laminate, 83 % |

Rows 1 and 3 are the line again, barely moved. The cavity mode is row 2, at **12.84 GHz**: above the band's top of
12 GHz. Its loaded Q is now close to its unloaded Q, because little of its field is left at the ports. The saved
**Cavity mode** plot shows mode 2.

## Cavity with post: *Driven*

Run *Driven*. S21 stays within **−0.093 dB** of 0 dB across the whole band: no notch. The resonance is still there,
at 12.84 GHz, and a sweep that ran past 12 GHz would find it. It is just no longer in the band.

## Why it moved

**The post does not damp the mode. It shortens the cavity the mode sees.** A conductor from floor to lid forces the
vertical electric field to zero where it stands, so the field cannot peak there any more. Look at the two **Cavity
mode** plots: with the post, |E| is zero round the post and the mode has moved into the open half of the housing, on
the far side of the line. A smaller cavity resonates higher, so the frequency rises from 9.956 GHz to 12.84 GHz.

The Q confirms it: with the post the loaded Q went **up**, from 539 to 2,480. A damped mode would have a lower Q.

The post works because it stands at the mode's maximum. One post moves this mode by about 30 %; moving it further
needs more posts, or a wall.

## On EMerge

Each cell also has *Modes (EMerge)* and *Driven (EMerge)*: the same two setups, solved by EMerge at its *Draft*
preset (element order 2). Run them as above. They need EMerge, which **Settings ▸ Solvers ▸ Install EMerge …**
installs in about two minutes.

| Cell | | Palace | EMerge |
|---|---|---|---|
| Cavity | the cavity mode | 9.956 GHz, Q 539, Q unloaded 3,236 | **9.9567 GHz**, Q **564**, Q unloaded **2,568** |
| Cavity | \|S21\| at 9.955 GHz, the deepest sample | −11.69 dB | **−12.56 dB** |
| Cavity | the worst \|S21\| more than 0.1 GHz from the notch | within 0.15 dB of 0 dB | **−0.106 dB** |
| Cavity with post | the cavity mode | 12.84 GHz, Q 2,480, Q unloaded 2,879 | **12.82 GHz**, Q **3,345**, Q unloaded **3,784** |
| Cavity with post | the least \|S21\| in the band | −0.093 dB | **−0.121 dB** |

EMerge's mode tables, as `circuitrf em "Cavity/3d/Cavity.c3d" --setup "Modes (EMerge)"` prints them:

| Cell | Mode | f | Q | Q unloaded | Most energy in |
|---|---|---|---|---|---|
| Cavity | 1 | **9.9567 GHz** | 564 | 2,568 | the air, 97 % |
| Cavity | 2 | 11.80 GHz | 2.0 | 12 | the laminate, 84 % |
| Cavity with post | 1 | 11.81 GHz | 2.0 | 12 | the laminate, 84 % |
| Cavity with post | 2 | **12.82 GHz** | 3,345 | 3,784 | the air, 97 % |

How to read the differences:

- **The resonance agrees.** The cavity mode is within 0.003 % of Palace's with the line alone and 0.15 % with the
  post, and both solvers put the Cavity's notch on the same 5 MHz sample.
- **The EMerge setups search from 10 GHz, the band's centre**, where Palace's search from 8. EMerge's eigensolver
  finds the modes nearest the frequency it is given, on either side of it, where Palace's finds the first ones above
  it. From 8 GHz with three modes, EMerge found the Cavity's mode, but with the post only one of the line's rows: the
  12.84 GHz mode was too far away. EMerge also evaluates the ports' resistance at that frequency rather than at each
  mode's own, which moves the loaded Q: the Cavity's mode reads Q 694 searched from 8 GHz and 564 from 10 GHz.
- **Q unloaded is the least certain number in EMerge's table.** circuitRF computes each port's external Q from
  EMerge's mode field and takes the ports' share out of the loaded Q, a small difference of two larger numbers. It
  reads 21 % below Palace's with the line alone and 31 % above with the post.
- **The line's rows differ**, and that is expected. They are not resonances (a Q near 1 or 2); each solver returns
  whichever heavily damped solutions lie nearest its search.
- **The notch's depth is not a difference between the solvers.** It is where the 9.955 GHz sample falls on a notch
  18 MHz wide. At the bottom of a notch like this one, |S21| is the loaded Q over the unloaded Q, and Palace's own
  table gives 539 / 3,236: **−15.57 dB**. Swept across the notch in 0.1 MHz steps (9.94–9.97 GHz, Standard),
  EMerge's bottom is **−15.55 dB** at **9.9564 GHz**. The two samples read −11.69 and −12.56 dB because each solver's
  resonance sits a different fraction of a megahertz from 9.955 GHz. To read a notch's depth, sweep across it finely.

## Eigenmode is FEM only

openEMS is a time-domain solver and has no eigensolver. Set *Modes*' solver to openEMS and **Run** refuses before
anything is meshed: *"This setup asks for an eigenmode solve on openEMS, and only Palace and EMerge find eigenmodes:
openEMS is a time-domain (FDTD) solver and has no eigensolver. Set the setup's Solver3D to Palace, or run it with
`circuitrf em --solver palace`."*

## Run settings, and the numbers

Every setup is **Draft with element order 2**, "At metal and ports" (`EdgeRefinement`) **1**, and the **Direct**
linear solver, as in the 3D Package example's *Lid modes*. The air box is flush with the housing's outside and PEC on
every face: the housing's interior is the problem. *Modes* asks for 3 modes above 8 GHz, the band's start.

The EMerge setups are **Draft**, which sets element order 2, elements at most 0.2 of a wavelength and 0.25 of that
along metal edges, and an adaptive sweep to 0.001 on |S|. *Modes (EMerge)* asks for 3 modes about 10 GHz.

Measured once, on an Apple M4 (10 cores, 16 GB), macOS 27.0, Palace 0.18.1 on 10 processes, Gmsh 4.15.2, and EMerge
3.0.0b2 with its fast sparse solver. A time is the whole run's wall clock and memory is the solver's own peak over its
processes. Your machine will differ.

| Run | Time | Memory |
|---|---|---|
| Cavity, *Modes* | 3 min 42 s | 3.7 GB |
| Cavity, *Driven* | 34 s | 4.3 GB |
| Cavity with post, *Modes* | 1 min 47 s | 4.9 GB |
| Cavity with post, *Driven* | 41 s | 4.3 GB |
| Cavity, *Modes (EMerge)* | 3 min 21 s | 2.7 GB |
| Cavity, *Driven (EMerge)* | 3 s | 1.6 GB |
| Cavity with post, *Modes (EMerge)* | 3 min 21 s | 2.9 GB |
| Cavity with post, *Driven (EMerge)* | 3 s | 1.8 GB |

An EMerge eigenmode run uses one core, whatever is installed, so it takes minutes where its driven sweep takes
seconds.

**What Draft trades on EMerge, and how to change it.** Set **Quality** to *Standard* or *Accurate* in the setup's
EMerge section (`"EMerge": { "Quality": "Standard" }` in the file). On the Cavity's *Driven (EMerge)*:

| Quality | Tetrahedra | Notch | \|S21\| at 9.955 GHz | Time | Memory |
|---|---|---|---|---|---|
| Draft (shipped) | 7,410 | 9.955 GHz | −12.56 dB | 3 s | 1.6 GB |
| Standard | 15,124 | 9.955 GHz | −13.03 dB | 6 s | 3.1 GB |
| Accurate (three mesh refinement passes) | 60,503 | 9.955 GHz | −13.49 dB | 34 s | 6.9 GB |

The notch's frequency is settled at Draft; its sampled depth is not, for the reason above. On *Modes (EMerge)*,
searched from 8 GHz, Draft found the cavity mode at 9.9571 GHz with Q 694 in 2 min 20 s, and Standard at 9.9569 GHz
with Q 729 in 4 min 56 s (2.8 GB).

*Modes* takes longer than *Driven* because a lossy metal makes the eigenproblem nonlinear: Palace finds the modes of
a linearised problem in seconds, then refines each one, and the refinement is most of the time. That refinement is what
did not finish when the line was lossy too, and why the line is a perfect conductor.


