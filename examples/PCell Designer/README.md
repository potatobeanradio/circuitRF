# PCell Designer

Parameterised cells made in the **PCell Designer** from the parts in the Library, placed in benches, laid out, checked
by LVS and simulated: a pad on a tee placed twice (§1-4), and four showcase cells, a 3-way Wilkinson divider (§5), a
spurline bandstop (§6), a dual-mode patch bandpass (§7) and a slotline divider (§8). The **PCell Designer** reference
page describes every control.

## The cells

| Cell | What it is |
|---|---|
| **PadTee** | A designed cell: Port 1 – `TL1` – bend `B1` – tee `T1` – `TL3` – Port 2 on a 50 Ω microstrip line, the tee's branch `TL2` ending in a drawn pad `SH1` (MSHAPE), 2 mm wide. Parameters `W`, `L1`, `Lstub` and `Lpad`, each with a range |
| **Bench** | `PadTee` placed twice, `X1` with `Lpad = 200 um` and `X2` with `Lpad = 400 um`, joined by a 50 Ω line between two Ports, swept 1–12 GHz |
| **Wilkinson3** | A 3-way Wilkinson divider for 2.45 GHz: three 86.6 Ω arms from a cross, three 50 Ω isolation resistors to a drawn star pad (`MSHAPE`, `Model="short"`). Parameters `La`, `Wa`, `Sp` (pad size) and `Riso` |
| **Wilkinson3Bench** | `Wilkinson3` between four Ports, swept 1.5–3.5 GHz by circuit models |
| **Spurline** | A 50 Ω line with an L-shaped slit, one drawn polygon, EM only (`Model="none"`). Parameters `Ls` (slit length, a drag handle), `g` (slit gap) and `W` |
| **SpurlineBench** | `Spurline` between two Ports, placed at `Ls = 9.21 mm`, swept 4.6–5.6 GHz in 25 MHz steps by Planar EM, with the optimisation that moved its notch to 5 GHz |
| **DualModePatch** | A square patch with one corner cut, fed along two adjacent edges across a gap, EM only. Parameters `a`, `d` (corner cut) and `s` (gap) |
| **PatchBench** | `DualModePatch` between two Ports, swept 4.9–5.5 GHz by Planar EM |
| **Slotline** | An out-of-phase microstrip-slotline divider: a slot in a drawn ground (one `MSHAPE` on Bottom Copper), crossed by an input and two output microstrips, EM only. Parameters `Lsl`, `Ws`, `Lss` and `Lms` |
| **SlotlineBench** | `Slotline` between three Ports, solved at 11 points over 4–6 GHz in its 3D view (EMerge, Standard mesh) |

## 1. The designed cell

Open `PadTee`'s schematic and choose **Tools ▸ PCell Designer…**. The canvas shows its layout at the values in the
**Value** column. Type `600 um` as `Lpad`'s value and the pad grows; change `Lstub` and the pad moves with the stub's
end. Nothing in the cell holds a coordinate: the lines are joined pin to pin, and the pad's far edge is tied to `Lpad`
by **Stretch by Parameter**. The pad is `Model="open"`, so in a circuit simulation it is an open end.

## 2. The bench

Open `Bench` and press **Simulate**: `Bench.cdd` shows |S21| and |S11|. Then **Update Layout from Schematic** draws the
two placements, each at its own `Lpad`; select one in the layout and drag its `Lpad` handle. **LVS** compares the layout
with the schematic and finds no error; its one warning is the ground pour under `TL1`, which no via stitches.

## 3. Export and import

**Export…** in the designer writes `PadTee.cpcell`. **File ▸ Import ▸ PCell Package…** in another workspace on the same
board makes `PadTee` a cell there, placed exactly as here. From the command line:

```
circuitrf pcell check PadTee
circuitrf pcell render PadTee -o padtee.svg --set Lpad=600um
circuitrf pcell export PadTee -o PadTee.cpcell
```

## 4. Optimising by EM

With **Create or update EM setup** on, Update Layout from Schematic also writes the bench's Planar setup, and EM solves the
two placed cells as copper. Set `SP1` to run by EM and tune `X1.Lpad`. The pad is open in the circuit model, so `Lpad`
moves only the EM response: `em=correct`, which searches on the corrected circuit model, stops at its start, because an
open pad gives the circuit model no slope. `em=direct` (pattern search) moved it from 200 µm to 600 µm, its maximum, in 8
solves (9 min 19 s, coarse Planar).

## 5. A 3-way Wilkinson divider

`Wilkinson3` splits 2.45 GHz three ways. Its input feeds a cross `X1`: the middle arm `AM` runs straight to the tee
`T2`, and the outer arms fold once (`B1`, `B3`) and run beside it to `T1` and `T3`. Each arm is a quarter wave of
√3·Z0 = 86.6 Ω (`Wa = 0.36 mm`). Each tee's branch carries a 50 Ω resistor to the star pad `SH1`, a drawn pad whose
`Model="short"` makes its three pins one node. A star of three resistors cannot be planar with all three outputs
outside it, so `R3` is a 1206 whose body bridges the middle arm.

Nothing is stretched, and the cell's two loops close by expression. The outer tees sit 6.2 mm off the axis and 6.2 mm
short of `T2`, which is the fold. The lines to the resistors are lengths in `Sp`, and the pad's pins are written where
`R1`, `R2` and `R3` end. A 0.3 mm line `LQ2` keeps `R2`'s pad off the bend `BR2`. **Check** finds no error at any
parameter's minimum or maximum, and **LVS** finds none on the cell's own layout.

`La = 19.5 mm` was found in the designer's live response: it is a little over λg/4 (18.8 mm), because the cross and the
tees take part of the junction. Simulate `Wilkinson3Bench`; `Wilkinson3Bench.cdd` shows the transmission and the input
match, and the isolation. At 2.45 GHz:

| S21 | S31 | S41 | S11 | S32 | S42 | S43 | S22, S33, S44 |
|---|---|---|---|---|---|---|---|
| −4.81 dB | −4.95 dB | −5.01 dB | −32.1 dB | −30.0 dB | −47.4 dB | −34.7 dB | −45.0, −24.6, −25.4 dB |

The three outputs are −4.77 dB plus the board's loss, within 0.2 dB of each other.

## 6. A spurline bandstop

`Spurline` is a 50 Ω line 16 mm long with an L-shaped slit. A slot `g` wide runs along the axis and turns out through
the line's edge at its far end. That leaves a spur `(W − g)/2` wide and `Ls` long, joined to the line at one end. The
spur and the line beside it are a coupled pair, and the line stops where the spur is a quarter wave:

f0 ≈ c / (4 · Ls · √εav),   εav = (εe + εo) / 2

The even- and odd-mode permittivities come from the coupled-line equations of Kirschning and Jansen
([doi:10.1109/TMTT.1984.1132616](https://doi.org/10.1109/TMTT.1984.1132616)). For two 0.45 mm strips 0.2 mm apart on
this board, εe = 2.84 and εo = 2.41, so εav = 2.62. `Ls = 8.5 mm` gives f0 ≈ 5.45 GHz, and 5 GHz needs `Ls` ≈ 9.26 mm.

The slit's far end follows `Ls` by **Stretch by Parameter**, so `Ls` is a drag handle wherever the cell is placed. The
cell is EM only. The sweep runs 4.6-5.6 GHz in 25 MHz steps, because the notch is only about 50 MHz wide at −20 dB and
a coarser sweep misses its bottom. With `X1.Ls = 8.5 mm` (the cell's default) the notch is at **5.38 GHz**, −33.9 dB at
the nearest point.

The bench's **Optimize** runs `em=direct` with pattern search on `X1.Ls`, pushing S21 below −30 dB at 5 GHz:
- The first pass (12 solves, 14 min) stopped at `X1.Ls = 9.31 mm`, which puts the notch at 4.93 GHz. Its steps were
  still a quarter of the range when the budget ran out, which is coarse for a notch this sharp.
- A second pass with the range narrowed to 9-9.4 mm met the goal on its first new solve, at 9.2125 mm.

The bench ships with `X1.Ls = 9.21 mm` and is laid out at that value. Simulate `SpurlineBench` (Planar, the setup in
`em/`, about 2 minutes); `SpurlineBench.cdd` shows S21 and S11. On the shipped files the notch is at **4.99 GHz**, where
S21 is −32.7 dB at 4.975 GHz and −32.3 dB at 5.000 GHz. The closed-form estimate of 9.26 mm is within 0.05 mm of the
optimised length.

## 7. A dual-mode patch bandpass

`DualModePatch` is a square patch `a` wide whose far corner is cut by `d`. Two 50 Ω feeds run along two adjacent edges,
`s` from them. The feeds and the patch are joined by no net, so they are placed by offset: the patch from the first
feed's open end, and the second feed from the patch's second pin. Each feed couples to one of the patch's two degenerate
modes, and the corner cut on the diagonal between them splits the modes and couples them. The unperturbed patch
resonates at

f10 ≈ c / (2 (a + 2ΔL) √εeff),   εeff = (εr + 1)/2 + (εr − 1)/2 · (1 + 12h/a)^−½

where ΔL is the open edge's extension of Hammerstad ([doi:10.1109/EUMA.1975.332206](https://doi.org/10.1109/EUMA.1975.332206)).
With `a = 15 mm`, εeff = 3.45 and ΔL = 0.24 mm, which gives **5.22 GHz**.

Simulate `PatchBench` (Planar); `PatchBench.cdd` shows S21 and S11. The sweep is 25 points over 4.9-5.5 GHz and takes
about 3 minutes. It shows the two split modes either side of 5.22 GHz:

| | first pole | second pole | between them |
|---|---|---|---|
| Where S11 dips | 5.08-5.10 GHz, −5.4 dB | 5.35 GHz, −3.2 dB | |
| S21 | −7.3 dB | −10.7 dB | −11.7 dB at 5.25 GHz |

The skirts fall to −17.5 dB at 4.9 GHz and −25 dB at 5.5 GHz.

**The feeds are under-coupled, and no gap fixes it.** The first design (`s = 0.2 mm`, `d = 3.5 mm`) peaked at −8.2 and
−11.2 dB. `em=direct` (pattern search, 12 Planar solves, 25 min) on `s` and `d` asked for S21 above −3 dB over
5.1-5.3 GHz. It drove `s` to its minimum, 0.16 mm, which is the board's 0.15 mm copper-spacing rule, and `d` to
3.9 mm, the values the cell now ships with. That gained 1 dB and stopped there.

Why −3 dB is out of reach: at a single pole, S21 = 2Qu / (2Qu + Qe).
- The −7.3 dB peak puts the external Q at about 2.6 times the patch's unloaded Q.
- −3 dB needs about 0.8 times, so the coupling would have to grow by a factor of about 1.8.
- An edge gap at the spacing rule cannot give that.
- The patch's unloaded Q is low because an open patch on a 0.508 mm board is an antenna: much of its energy radiates.

A usable two-pole passband needs feeds that touch the patch, or an inset feed. Both are a different cell, since the
patch and its feeds are then joined by a net.

## 8. A slotline divider

`Slotline` splits 5 GHz into two outputs 180° apart. A slot `Ws` wide is cut in the ground, and three microstrips on top
cross it: the input at the slot's middle, and the two outputs `Lsl` either side of it. Each crossing is a
microstrip-to-slotline transition:
- the microstrip runs on `Lms` past the slot to an open end;
- the slot runs on `Lss` past each output crossing to its shorted end.

Each is a quarter wave, so the open microstrip end becomes a short at the slot and the shorted slot end becomes an open
at the microstrip. The slot carries the same voltage both ways from the input, so two outputs that cross it alike are
in phase. Output 3 crosses it the other way round, with its port on the input's side and its stub on the other, which
puts the two outputs 180° apart.

With the quasi-static estimate εs ≈ (εr + 1)/2 = 2.33 for a slot on this thin substrate, λs/4 at 5 GHz is
c / (4 · 5 GHz · √2.33) ≈ 9.8 mm (`Lss`). The 50 Ω line's εeff = 2.86 gives λm/4 ≈ 8.9 mm (`Lms`).

The ground is one drawn shape on net 0, `SH1`. A polygon has no hole, so its outline runs in to the slot along a
zero-width cut below the input crossing, around the slot, and back out the same way. The copper is then one piece with
the slot inside it, and LVS reads its one pin on that piece. `SH1` and both output lines are placed by offset from the
input's crossing: the outputs at `Dy = ±Lsl`, and output 3 turned by 180°. The bench's ground pour stops at the cell's
drawn ground, so the slot stays open. Planar cannot solve it, because Planar's ground is an unbroken plane. The bench's EM view is its 3D view
(`3d/SlotlineBench.c3d`, made by **New 3D View from Layout**), which turns the drawn ground into copper with the slot cut
in it, with an EMerge setup.

Simulate `SlotlineBench`; `SlotlineBench.cdd` shows the split and the two outputs' phases. The solve uses EMerge's
Standard mesh (36,000 tetrahedra, 5.8 GB). It runs an adaptive sweep of 11 points over 4-6 GHz, fitted from 10 full
solves, in 42 s:

| f | S11 | S21 | S31 | ∠S21 − ∠S31 | S32 |
|---|---|---|---|---|---|
| 4.0 GHz | −13.2 dB | −7.06 dB | −7.01 dB | 179° | −10.4 dB |
| 4.6 GHz | −10.4 dB | −6.23 dB | −6.19 dB | 180° | −11.7 dB |
| 5.0 GHz | −9.8 dB | −5.95 dB | −5.94 dB | 180° | −11.8 dB |
| 5.4 GHz | −10.3 dB | −5.72 dB | −5.73 dB | 180° | −11.3 dB |
| 6.0 GHz | −12.3 dB | −5.43 dB | −5.44 dB | 180° | −9.9 dB |

The split is even to 0.05 dB and the outputs are 179-180° apart across the band. The loss is the board's, and it is
mostly radiation:
- At 5 GHz, |S11|² + |S21|² + |S31|² = 0.61, so 39 % of the input power does not come back out of the ports.
- The dielectric (tanδ = 0.0037) and the copper along the 30-40 mm a wave travels account for about 0.3 dB, or 7 % of
  it.
- The rest, about 32 %, leaves through the slot. A slot in a ground plane is a magnetic dipole, and on a 0.508 mm board
  with εr = 3.66 little of its field is held in the substrate, so it radiates into the open box on both sides of the
  board.

The outputs are also poorly matched (S22 ≈ −5 dB): a lossless three-port cannot be matched at every port, and this one
has no isolation network. The transitions are centred above 5 GHz (S21 is still rising at 6 GHz), and longer `Lss` and
`Lms` move the centre down.

No results ship with this example.
