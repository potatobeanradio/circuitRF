# Artwork to Schematic

A small board that arrives the way a real one does — as a fabricator's file set — and the schematic it turns into
in seconds, with no EM run.

| | what it is |
|---|---|
| **Board** | the board as imported from `fab/`: copper, solder mask and vias, nothing else. This is the artwork you recognise. |
| **Board design** | the schematic the board was drawn from, swept 0.1–3 GHz. Keep it closed until you have recognised the board; it is the answer to compare against. |
| `fab/Board/` | the Gerber and Excellon files and their job file |
| `fab/Board.pos`, `fab/Board-bom.csv` | the placement file and bill of materials |

The board: a 450 µm microstrip line enters at the left edge, turns up through a 90° bend, passes a tee whose branch
is an open stub, then a series 2.2 pF capacitor (0402), a shunt inductor (0402) to a ground via standing on the line,
and a series 10 Ω resistor (0603), and leaves at the top edge. Two copper layers on a 254 µm laminate, the bottom
layer a solid ground plane.

**The bill of materials gives no value for L1, on purpose.** A real BOM is often incomplete, and an unknown value is
what the recognition turns into a variable for you to tune. L1 still has its row: a part the BOM does not list at all
is read as not fitted.

---

## 1. Open the artwork

**File ▸ New ▸ Examples ▸ Artwork to Schematic**, choose a folder for your copy, and open **Board ▸ Board** (its layout).

Its technology, `Board/Board.ctech`, is the one the Gerber import wrote beside the board. The import cannot read a
stackup out of Gerber files and guessed ordinary FR-4; the copy here carries the fabricator's numbers instead
(254 µm, εr 3.66), typed in on the **Stackup** tab as the import asks. Do the same on your own boards: every line's
impedance comes from it.

## 2. Create Schematic from Artwork

With the layout focused, choose **Design ▸ Create ▸ Schematic from Artwork…**. Recognition runs as the dialog opens.
Then:

- **Placement…** → `fab/Board.pos`. The file states its own origin, so no origin choice appears.
- **BOM…** → `fab/Board-bom.csv`.

The bottom strip reports what was read: ground on the bottom plane, one via kept as `VIAGND`, two ports where the
line leaves the board, seven `MLIN`s, a bend and a tee, and one open line end — the stub.

## 3. Review the parts table

| Refdes | Kind | Connection | Case | Value |
|---|---|---|---|---|
| C1 | C | series | 0402 | 2.2 pF |
| L1 | L | shunt | 0402 | *(empty — variable `L1_L`)* |
| R1 | R | series | 0603 | 10 Ohm |

Select a row and its pads are marked on the layout. C1 and R1 take their values from the BOM. L1's kind comes from
the BOM's description and its case from the land pattern, but nothing states its value, so it becomes the global variable
**`L1_L`**, starting at 1 µH — a shunt inductor large enough to be invisible, so the first simulation shows the lines
alone. You could type a value into the cell now; leave it empty to tune it in step 5.

## 4. Create, and simulate

Leave the target at **New cell**, `Board_model`, and click **Create**. The schematic opens; the report goes to
**Messages**. Press **Simulate**.

At 2 GHz the recognised board reads S11 ≈ −7.7 dB.

## 5. Tune the unknown part

Simulate **Board design**: at 2 GHz its S11 is −15.8 dB. The difference is L1.

Back in **Board_model**, open the **Tuning** panel. `L1_L` is there already, its range 100 nH – 10 µH around the
starting value. Click the minimum and type `1 nH`, then drag the slider down: the match deepens as L1 falls and
reaches S11 ≈ −15.9 dB at **8.2 nH** — the value in **Board design**. Everything else in the two schematics already
agrees: the line lengths came off the copper to the micrometre.

Then try:

- **Show in Artwork** (right-click any component of `Board_model`) marks the copper it came from.
- **Swap Line Type** on one of the lines, to `TLIN` and back: width and length are kept.
- `circuitrf recognize Board/Board --placement fab/Board.pos --bom fab/Board-bom.csv` from the workspace folder prints
  the same report and parts table without writing anything.

---

## How the board was made

It was authored in circuitRF, as any board would be: the **Board design** schematic, with footprints on its three
parts, **Update Layout from Schematic**, the parts arranged with each line ending at its part's pad or at a bend's or
tee's reference plane, a ground plane drawn, then **File ▸ Export ▸ Gerber** and the placement and BOM writers. The
placement and BOM were cut to the three soldered parts — circuitRF's own writers list every placed instance, lines
included — and L1's BOM row was deleted. The file set was then imported with **File ▸ Import ▸ Board**.


