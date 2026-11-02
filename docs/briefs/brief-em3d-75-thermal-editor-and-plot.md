# Brief 75 — the thermal editor and Plot Temperature

**Series:** [3D thermal, fifth series](brief-em3d-71-thermal-overview.md) · **Tag:** `R-em3d75-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §8.5; overview §1b–§1d, §1i, D9
**Area:** `src/Ui/ThreeD/` (tools, menus, the Setups dialog's thermal page, the fields over the model),
`src/Ui/Viewer3D/Viewer3DViewModel.Fields.cs`, `src/Render/Scene3D/Fields/` (temperature, range, wires),
`src/Render/Scene3D/` (drawing sources, probes, regions), `tests/Ui.Tests/`
**Depends on:** 73, 74 · **Blocks:** 81; brief 77 draws its wire results through §5 here

---

## 0. What this brief delivers

Everything the user does by hand for a thermal run, and everything they see after it:

1. **Drawing** heat sources, probes and mesh regions in the `.c3d` editor (§1).
2. **Face ▸ Thermal boundary** and **contact resistance** from the context menu (§2).
3. **The Setups dialog's thermal page** (§3).
4. **Plot Temperature**: on one face, on every face, on a clip plane; hover readout; hot-spot marker; the
   colour range (§4).
5. **Line plots** and the **probe/measures table** across a sweep (§4d–§4e).
6. **Wires coloured along their length** by T(s) (§5) — the path brief 77's result is drawn through.

---

## 1. `R-em3d75-1` — drawing the thermal places

**`R-em3d75-1a` Tools**, on the 3D menu and the toolbar beside *Port*, each a `C3dToolKind`:
- **Heat Source** — draws a rectangle or polygon on a face or a plane, **exactly as the Port tool draws its
  sheet** (reuse `PortTool`'s picking and snapping, not a copy), then asks its name and default power; a
  *Heat Source from Solid* command on an object's context menu makes a volumetric one;
- **Probe** — point (click on a face), face (Face mode, context menu *Add Probe ▸ Face*), solid (Object mode),
  spot (click a centre on a face, type a diameter), line (two clicks), wire (Object mode on a wire);
- **Mesh Region** — draws a box as the Box tool does, then asks its element size.

**`R-em3d75-1b` Drawing them.** Heat sources in a warm hatched colour, probes as small markers with their
names, mesh regions as dashed wireframe boxes with their size as a label; each has a visibility toggle in the
view menu and a group in the object tree (*Heat sources*, *Probes*, *Mesh regions*) whose rows select, rename,
hide and delete as objects do. They are never drawn as solids, and never picked as faces for snapping onto
(they are not geometry — overview §1c).

**`R-em3d75-1c`** The Properties inspector edits every field brief 73 §4 defines, with expressions (brief 51).
Undo/redo covers every edit.

## 2. `R-em3d75-2` — boundaries and contacts from faces

In Face mode, the context menu of a face gains **Thermal ▸ Fixed Temperature… / Convection… / Insulated / Clear**,
writing to the **active thermal setup**'s `Boundaries` (a greyed item with a tooltip when the active setup is
not thermal). Conditioned faces are tinted by kind in thermal context (a blue-for-fixed, green-for-convection
tint the legend names), as EM boundaries are tinted today. **Two touching objects selected** ▸ *Thermal ▸
Contact Resistance…* writes a `ContactResistances` entry, prefilled with the technology's pair value when one
exists, and says which it is.

## 3. `R-em3d75-3` — the Setups dialog's thermal page

`C3dSetupAnalysesView` gains **Thermal** beside the EM problem types. Its page has, top to bottom:
**Sources** (every heat source with its default and an override cell), **Boundaries** (the list §2 writes,
editable here too, with *All exposed faces: convection h, T_amb*), **Sweep** (up to two variables),
**Measures** (a text list, parsed live, errors inline), **Mesh** (order, size-from-sources, min through
thickness, grading, the convergence-check box) and **Balance** (`KOfT` here; `SigmaOfT` appears with brief 77).
Everything the dialog writes is the `Thermal` section of brief 73 — no state lives only in the view model.
The **size estimate** from `explain` (brief 74 §6) shows under the Mesh group and updates as sizes change.

## 4. `R-em3d75-4` — Plot Temperature

The `.c3d` editor already draws a run's fields over the model with a staleness banner (brief 49 R-em3d49-5b);
temperature is a new **quantity** on that path, read from the run's `thermal.pvd` through `FieldRun` with no
new reader (brief 74 §5b).

**`R-em3d75-4a` Where.** After a thermal run of the active setup:
- right-click a face ▸ **Plot Temperature** shows temperature on that face alone, the rest of the model drawn
  as it was, dimmed; the command toggles, and several faces accumulate;
- **View ▸ Temperature ▸ All Faces** paints every exposed and conditioned face;
- **On Clip Plane** paints the clip section (the existing clip plane — the view to see a channel under a field
  plate).
The menu item is greyed with a tooltip when no thermal result exists for the active setup, or it is stale.

**`R-em3d75-4b` The colour range — D9.** Temperature's default range is the **true minimum and maximum** of
what is drawn (`FieldColorScale` with percentile 100, stated in the legend as *"maximum"*): the peak is the
answer and must never be clipped away, unlike an EM field's singular edges (the 99th-percentile default stays
for EM). The legend reads °C. **Fix range across sweep** is a toggle: the range becomes the min and max over
**every** sweep point, so stepping the sweep never rescales the colours; off, each point has its own range.

**`R-em3d75-4c` Readouts.** Hover shows T at the cursor on a painted surface (the existing `FieldSampler`,
interpolated, not nearest node). A **hot-spot marker** sits at the maximum of what is drawn, labelled with its
temperature and its object. A **sweep slider** steps the `.pvd`'s steps, labelled with the sweep values.

**`R-em3d75-4d` Line plot.** A line probe's T(s) opens in a small plot panel (the existing plotting control,
not a new one), and so does **Temperature Along… ▸ pick two points** without creating a probe — the owner's
"make Rth by hand" tool: the plot shows distance, both end temperatures and their difference, with the
cursor's readout.

**`R-em3d75-4e` Probe table.** A panel lists every probe statistic and every measure at the current sweep point,
with a column per sweep point on request, and flags any probe that crossed its `LimitC` (brief 73 §4b). It reads
the `.npy`, not the field files.

## 5. `R-em3d75-5` — wires coloured along their length

A wire result is a table of (arc length s, T) per wire (brief 77 writes it; this brief draws a synthetic one). The
scene's wire solid is already swept as rings along its resolved centreline; each ring gets **its arc length**
along the same centreline the solver used, and its colour is T(s) interpolated. Nothing re-tessellates: the
colour is a per-vertex scalar on the existing wire mesh (`FieldVertex`'s path). The hot-spot marker and hover
readout work on wires as on faces, naming the wire (`w1[3]`) and s.

## 6. Gates

Counters and headless checks; pixels are not seen (overview §1k).

1. **Round trips**: each tool's commit writes exactly the brief-73 record; undo restores byte-identical JSON.
2. **Menu state**: *Plot Temperature* is enabled only with a current thermal result for the active setup
   (three cases: none, current, stale).
3. **Range**: a synthetic `.vtu` with one hot node — the legend's high end equals it (percentile 100); with
   *Fix range across sweep*, two steps give one range, the union.
4. **Readout**: `FieldSampler` at a known interior point of a linear field returns the exact value.
5. **Wire colouring**: a synthetic T(s) linear in s on a two-segment wire — each ring's scalar equals T at
   that ring's arc length to 1e-12; the arc length is the 3D one (a wire with vertical rise: the sum of its
   3D segment lengths, not its plan length).
6. **Line plot**: *Temperature Along* between two points of a linear field gives a straight line whose end
   values match to 1e-9.
7. **No re-tessellation**: changing the sweep step uploads scalars only (a counter: zero geometry uploads).

## 7. Owner check list (Debug build)

1. Draw a heat source on a die face, a spot probe on the top, a line probe down through the die, and a mesh
   region round the source. Set the heatsink face to 25 °C from the context menu.
2. Open Setups ▸ Thermal; add a sweep of the power; Simulate.
3. Plot Temperature on the die face; then All Faces; then the clip plane through the source. Hover; find the
   hot-spot marker.
4. Step the sweep with *Fix range* on and off.
5. Temperature Along… from the source to the heatsink face; read ΔT.

## 8. Scope

- **No solver changes.** Everything here reads brief 74's files.
- **The EM field defaults are unchanged** (99th percentile), gated by the existing field tests.
- Findings in `src/Ui/RESOLVED.md` and `src/Render/RESOLVED.md`; never `CLAUDE.md`.
