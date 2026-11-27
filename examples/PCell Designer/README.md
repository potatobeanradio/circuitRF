# PCell Designer

A parameterised cell made in the **PCell Designer** from the parts in the Library, placed twice in a bench at two
values, laid out, checked by LVS and simulated. The **PCell Designer** reference page describes every control.

## The cells

| Cell | What it is |
|---|---|
| **PadTee** | A designed cell: Port 1 – `TL1` – bend `B1` – tee `T1` – `TL3` – Port 2 on a 50 Ω microstrip line, the tee's branch `TL2` ending in a drawn pad `SH1` (MSHAPE), 2 mm wide. Parameters `W`, `L1`, `Lstub` and `Lpad`, each with a range |
| **Bench** | `PadTee` placed twice, `X1` with `Lpad = 200 um` and `X2` with `Lpad = 400 um`, joined by a 50 Ω line between two Ports, swept 1–12 GHz |

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
two placed cells as copper. Set `SP1` to run by EM, give `X1`'s `Lpad` a VAR (`Lp1`) and tune it. The pad is open in the
circuit model, so `Lpad` moves only the EM response: `em=correct`, which searches on the corrected circuit model, stops at
its start after one solve, and `em=direct` (algorithm `pattern`) is the mode that moves it.

No results ship with this example.
