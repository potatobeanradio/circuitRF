# PCell Designer

Parameterised cells made in the **PCell Designer** from the parts in the Library, placed in benches, laid out, checked
by LVS and simulated: a pad on a tee placed twice (§1-4), and three showcase cells, a 3-way Wilkinson divider (§5), a
spurline bandstop (§6) and a dual-mode patch bandpass (§7). The **PCell Designer** reference page describes every
control.

## The cells

| Cell | What it is |
|---|---|
| **PadTee** | A designed cell: Port 1 – `TL1` – bend `B1` – tee `T1` – `TL3` – Port 2 on a 50 Ω microstrip line, the tee's branch `TL2` ending in a drawn pad `SH1` (MSHAPE), 2 mm wide. Parameters `W`, `L1`, `Lstub` and `Lpad`, each with a range |
| **Bench** | `PadTee` placed twice, `X1` with `Lpad = 200 um` and `X2` with `Lpad = 400 um`, joined by a 50 Ω line between two Ports, swept 1–12 GHz |
| **Wilkinson3** | A 3-way Wilkinson divider for 2.45 GHz: three 86.6 Ω arms from a cross, three 50 Ω isolation resistors to a drawn star pad (`MSHAPE`, `Model="short"`). Parameters `La`, `Wa`, `Sp` (pad size) and `Riso` |
| **Wilkinson3Bench** | `Wilkinson3` between four Ports, swept 1.5–3.5 GHz by circuit models |
| **Spurline** | A 50 Ω line with an L-shaped slit, one drawn polygon, EM only (`Model="none"`). Parameters `Ls` (slit length, a drag handle), `g` (slit gap) and `W` |
| **SpurlineBench** | `Spurline` between two Ports, swept 3.5–7 GHz by Planar EM, with the optimisation that moves its notch to 5 GHz |
| **DualModePatch** | A square patch with one corner cut, fed along two adjacent edges across a gap, EM only. Parameters `a`, `d` (corner cut) and `s` (gap) |
| **PatchBench** | `DualModePatch` between two Ports, swept 4.9–5.4 GHz by Planar EM |
| **Slotline** | An out-of-phase microstrip-slotline divider: a slot in a drawn ground (two `MSHAPE`s on Bottom Copper), crossed by an input and two output microstrips, EM only. Parameters `Lsl`, `Ws`, `Lss` and `Lms` |
| **SlotlineBench** | `Slotline` between three Ports, solved at 4, 5 and 6 GHz in its 3D view (EMerge) |

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
`R1`, `R2` and `R3` end. **Check** finds no error at any parameter's minimum or maximum.

`La = 19.5 mm` was found in the designer's live response: it is a little over λg/4 (18.8 mm), because the cross and the
tees take part of the junction. Simulate `Wilkinson3Bench`; `Wilkinson3Bench.cdd` shows the transmission and the input
match, and the isolation. At 2.45 GHz:

| S21 | S31 | S41 | S11 | S32 | S42 | S43 | S22, S33, S44 |
|---|---|---|---|---|---|---|---|
| −4.81 dB | −4.93 dB | −5.01 dB | −32.9 dB | −29.1 dB | −48.3 dB | −36.4 dB | −47.6, −25.3, −25.2 dB |

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
cell is EM only. Simulate `SpurlineBench` (Planar, the setup in `em/`); `SpurlineBench.cdd` shows S21 and S11. A coarse
sweep (3 cells across a strip, one edge cell, 15 points, about a minute) puts the notch at **5.39 GHz**.

The bench's **Optimize** runs `em=direct` with pattern search on `X1.Ls`, 8 solves at most, to push S21 down at 5 GHz.
It stopped at `X1.Ls = 9.31 mm` after 8 solves (9 min 51 s on the coarse mesh), and the notch moved to **4.97 GHz**.

## 7. A dual-mode patch bandpass

`DualModePatch` is a square patch `a` wide whose far corner is cut by `d`. Two 50 Ω feeds run along two adjacent edges,
`s` from them. The feeds and the patch are joined by no net, so they are placed by offset: the patch from the first
feed's open end, and the second feed from the patch's second pin. Each feed couples to one of the patch's two degenerate
modes, and the corner cut on the diagonal between them splits the modes and couples them. The unperturbed patch
resonates at

f10 ≈ c / (2 (a + 2ΔL) √εeff),   εeff = (εr + 1)/2 + (εr − 1)/2 · (1 + 12h/a)^−½

where ΔL is the open edge's extension of Hammerstad ([doi:10.1109/EUMA.1975.332206](https://doi.org/10.1109/EUMA.1975.332206)).
With `a = 15 mm`, εeff = 3.45 and ΔL = 0.24 mm, which gives **5.22 GHz**.

Simulate `PatchBench` (Planar); `PatchBench.cdd` shows S21 and S11. A coarse sweep (21 points, about 2 minutes) shows
the two split modes either side of it: S11 dips at **5.10 GHz** and **5.32 GHz**, where S21 reaches −8.2 dB and −11.2 dB.
The passband is 5.05-5.33 GHz, with steep skirts down to −20 dB at 4.9 and 5.4 GHz. Across a 0.2 mm gap the feeds are
under-coupled, which is where the insertion loss comes from. A smaller gap or a longer coupled edge raises the
coupling, and a larger `d` widens the split.

## 8. A slotline divider

`Slotline` splits 5 GHz into two outputs 180° apart. A slot `Ws` wide is cut in the ground, and three microstrips on top
cross it: the input at the slot's middle, and the two outputs `Lsl` either side of it. Each crossing is a
microstrip-to-slotline transition:
- the microstrip runs on `Lms` past the slot to an open end;
- the slot runs on `Lss` past each output crossing to its shorted end.

Each is a quarter wave, so the open microstrip end becomes a short at the slot and the shorted slot end becomes an open
at the microstrip. The two slot waves leave the input in opposite senses, so the outputs are out of phase.

With the quasi-static estimate εs ≈ (εr + 1)/2 = 2.33 for a slot on this thin substrate, λs/4 at 5 GHz is
c / (4 · 5 GHz · √2.33) ≈ 9.8 mm (`Lss`). The 50 Ω line's εeff = 2.86 gives λm/4 ≈ 8.9 mm (`Lms`).

A polygon has no hole, so the ground is two drawn shapes on net 0: `SH1`, the ground less the slot and the block below
it, and `SH2`, that block. Both are placed by offset from the input's crossing, and each output line is placed by
offset from `SH1`'s pin at its crossing. A bench that places only the designed cell draws no ground pour, so the slot
stays open. Planar cannot solve it, because Planar's ground is an unbroken plane. The bench's EM view is its 3D view
(`3d/SlotlineBench.c3d`, made by **New 3D View from Layout**), which turns the drawn ground into copper with the slot cut
in it, with an EMerge setup.

No results ship with this example.
