# Brief 52 — the showcase: an MMIC in a package

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d52-n` ·
**Precedent:** [brief 30](brief-em3d-30-showcase.md) (the `3D EM` example workspace: one numbers file,
quoted by the README and the user page, and a test that holds all three together)
**Area:** `examples/3D Package/` (new), `examples/examples.json`, `docs/user/src/` (a new page), the
figure catalogue, `tests/Ui.Tests/Examples/`
**Depends on:** all of 41–51 (51 only for the parameterised cavity, §1c)

---

## 0. What this brief delivers

The owner's motivating case, shipped as an example someone who has never seen circuitRF can open, and a
user page that teaches the editor through it:

- an **MMIC cell** whose layout is drawn in **µm** on a GaAs-like MMIC technology: a small thru line with
  pads. It is simple enough to solve on a laptop;
- a **package cell** whose **3D view** is drawn in **mil** on a ceramic package technology:
  - base, cavity walls and lid, drawn with the editor's tools;
  - two leads;
  - the MMIC placed as a **layout instance** on the die attach;
  - **wires** from the die pads to the leads;
  - two ports at the leads;
- **two embedded setups:** a Palace driven sweep (the default), and a lid **eigenmode** setup, the cavity
  resonance being the classic package problem;
- a **user page**, *Drawing in 3D*: the modes, B, snapping, drawing, face editing, hierarchy, swap view,
  ports and Simulate, each with its keys.

---

## 1. `R-em3d52-1` — the workspace

**`R-em3d52-1a`** `examples/3D Package/`, registered in `examples.json`, offered by *Tools ▸ Examples*.
Two technologies in `tech/`:
- one MMIC technology (GaAs substrate, two metals, a back-side ground);
- one package technology (alumina, gold, a lid alloy).

They are **named generically**. No foundry's or supplier's name appears anywhere (CLAUDE.md, *Commercial
Vendor References*): not a process name, not a PDK name, not a part number.

**`R-em3d52-1b` The package's `.c3d` is authored with the editor's own operations**, not hand-written
JSON. The file must read like something a user drew. The completion note lists the gestures used. A test
still reads it, so a hand edit later is not forbidden, only not the origin.

**`R-em3d52-1c` Optional, if brief 51 is built:** the cavity's width, length and height are parameters
(`cav_w`, `cav_l`, `cav_h`), so the lid-resonance section of the page can show a changed parameter moving
the mode.

## 2. `R-em3d52-2` — the numbers, measured once, quoted everywhere

Brief 30's discipline, unchanged:
- `expected-numbers.json` is the **one** source for:
  - S21 at the band centre;
  - the first lid mode's frequency and Q;
  - each setup's measured run time and peak memory on the owner's Mac, and the preset used;
- the README and the user page **quote each value's text**, and a test fails when they disagree;
- **presets are chosen by measurement** (*document the setting you traded away*). If `Standard` takes
  too long for a newcomer's first press, use `Draft` and state `Standard`'s numbers beside it.

**`R-em3d52-2a` An external check on the lid mode.** The first cavity mode of the empty package (without
the die) is compared with the rectangular-cavity closed form for the drawn cavity dimensions. The
difference the die and the wires make is **stated**, not hidden. This is the one number on the page that
is an external reference rather than circuitRF's own output, and the page says which is which.

## 3. `R-em3d52-3` — the user page

`docs/user/src/…/drawing-in-3d.md` (follow the existing user-doc structure and the figure catalogue).
Sections:
1. **The 3D view of a cell** — what it is, `.c3d`, units, and where it sits beside the other three views.
2. **Selecting**: O / F / V, hover, click, **B**, the context menu.
3. **Snapping.**
4. **Drawing**: the plane, the grid, box in three clicks, typed values, Shift+A.
5. **Moving and editing**: Move, the gizmo, push/pull, folding, and why a solid never opens.
6. **Hierarchy**: placing a layout, the die-attach handle, swap view, arrays, push in, flatten; **why a
   different technology is allowed here** and not in a layout.
7. **Wires.**
8. **Simulating**: setups, ports and their arrows, the air box, boundaries, Run, fields.
9. **Headless**: the reference page, `em x.c3d`, `check`, `explain`.

**Figures** come from `render x.c3d` sections (brief 42) and from the Metal offscreen path where a
perspective picture is needed. They are registered in the figure catalogue. **DocGen is not run by this
brief** (the owner regenerates at the end of the series). The figure-existence gate is therefore red until
then, as brief 30 recorded, and this brief records it the same way.

## 4. Gate

`tests/Ui.Tests/Examples/Em3dPackageExampleTests.cs`.

1. **Opens and checks clean.** `check` on the workspace: 0 errors. Warnings are listed and justified in
   the README.
2. **Elaborates.** Both technologies resolve per instance, and the `U1/` names exist. The ports' inferred
   polarity is as the README states.
3. **Numbers agree.** README, user page and `expected-numbers.json` quote identical text.
4. **The solve** (`Category=Benchmark`): the driven setup reproduces `expected-numbers.json` within its
   stated tolerance. The eigenmode setup gives the lid mode within the closed-form comparison's stated
   difference.
5. **No vendor names.** The existing name gate passes over `examples/3D Package/`. A gate may not list
   the vendor names it forbids (the WSProbe series' rule): it passes by the existing mechanism.

## 5. Owner check — the series walk-through (overview R-em3d40-3)

On a Mac account with Palace installed by the assistant (brief 24), in the **Debug** build:
1. open *Tools ▸ Examples ▸ 3D Package*;
2. orbit; B through the lid to the die;
3. swap U1 to its 3D view (none exists, so see the disabled reason) and back;
4. move a wire's mid-point; undo;
5. Simulate the driven setup; read S21;
6. run the lid-mode setup; see the mode's field;
7. then, from an empty cell, redraw a simpler package from the page's instructions alone.

Record the elapsed time of each step, and anything that did not feel immediate.

## 6. Scope

- One example workspace. The `3D EM` workspace from brief 30 is unchanged. Its setups keep working, and
  *New 3D View from Layout* (brief 48 §7) on any of its cells is mentioned on the page as the way to turn
  one of them into an editable view.
