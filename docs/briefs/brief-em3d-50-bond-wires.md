# Brief 50 — bond wires in a `.c3d`, across hierarchy

**Series:** [3D EM, third series](brief-em3d-40-overview.md) · **Tag:** `R-em3d50-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.6 (section, feet, balls, loop height);
[`wbond.md`](../design/wbond.md) (the wire model; a wire's points are its only truth)
**Area:** `src/Design/Layout/Em3d/Em3dWires.cs` (split: resolution vs `.wBond` reading),
`src/WBond/LoopShape.cs` (reused, not changed), `src/Design/ThreeD/` (the `Wire` object, its elaboration),
`src/Ui/ThreeD/Tools/WireTool.cs`
**Depends on:** 45, 48 (and 44 for snapping into instances) · **Blocks:** 52
**Owner decision D7:** in this series.

---

## 0. What this brief delivers

The owner's MMIC-in-package case has **no signal path without wires**. The die's pads are inside a `U1`
instance, and the package's leads are in the parent. A `.wBond` cannot reach across: it is attached to
one `.clay`. So a `.c3d` gets its own **Wire** object:

- the **Wire tool**: click a pad (snapped, anywhere, including inside an instance), click the other pad,
  then move the mouse to set the **loop height**, or type it. A third click ends the wire;
- the wire is §6.6's wire, **the same geometry series 1 builds**: hexagonal or round section, wedge or
  ball per end, the foot, the ball, the neck, and the **assembly loop height** (foot bottom to apex top);
- **Vertex mode edits its shape**: the axis points move as a wBond wire's do;
- *Duplicate* and *Array* (brief 46) make a wire array with its pitch.

Wires already inside a `.clay` instance (its stem-paired `.wBond`) keep coming in with it (brief 42 §2d).

---

## 1. `R-em3d50-1` — split the wire geometry from the `.wBond` reader

**`R-em3d50-1a`** `Em3dWires` today reads a `.wBond`, finds each end's pad in the layout's merged pieces,
and resolves the rings, feet, balls and necks. Split it:
- **resolution**: axis points, section, diameter, styles, foot length and the two pads' top surfaces in,
  and an `Em3dSweep` (plus balls) with its report out;
- **reading**: the `.wBond` and the pad lookup, calling resolution.

Same discipline as brief 42's layout split: **dump every wire problem before the split, and require
byte-identity after** (R-em3d4-4d's bitwise round-trip is the reason the foot's z is *assigned*, not
computed. Keep it that way).

**`R-em3d50-1b` The pad lookup, generalised.** An end's pad is the elaborated conductor **whose top
surface contains the end's plan point at the end's z, within 1 DBU**. It includes conductors inside
instances. Where pieces stack, the highest top surface wins, which is series 1's rule: *a bond lands on
exposed metal*. No pad is a refusal naming the wire and the end, never a foot in mid-air.

## 2. `R-em3d50-2` — the `Wire` object

```jsonc
{ "$type": "Wire", "Name": "w1", "Material": "Gold",
  "Points": [[x,y,z], ..],          // the axis, DBU: the only truth about the shape (wbond.md)
  "DiameterUm": 25, "Section": "Hexagon",
  "Start": { "Style": "Ball" }, "End": { "Style": "Wedge", "FootLengthUm": null } }
```

- `Points` hold the shape. There is no stored profile, as in wBond since the `LoopProfile` removal
  (wbond.md; `LoopShape`'s header explains why).
- A `null` field takes series 1's default and its source note (`WireBondProcess`'s value sources:
  technology, `.wBond` design, built-in). Here the sources are the `.c3d`'s technology, then built-in.
- A wire has no `Placement`. Its points are world points in the document, because it spans between
  things that each have their own placement.

## 3. `R-em3d50-3` — the tool

**`R-em3d50-3a`** *Wire* is in *3D ▸ Draw* and is letter **W** in the Shift+A popup (brief 45).
1. **Start:** a snapped point on a conductor's **top face**. Face-centre snap is the natural target on a
   pad. Any other point is accepted only if it lies on an upward-facing conductor face, and otherwise the
   status bar says *"a wire starts on the top of a pad"*.
2. **End:** the same rule. A rubber-band arch from `LoopShape.Seed` follows the cursor.
3. **Loop height:** the mouse sets it as a box's height is set (brief 45 §3a), **measured the assembly
   way** (§6.6): from the bottom of the lower foot to the top of the wire at its apex. The status bar
   shows both that number and the axis apex height, as `explain` does (R-em3d4-5b), because users hold
   one or the other.

   Typed: `8mil`. The amplitude is solved by `LoopShape.SolveAmplitudeNm` from the requested assembly
   height, less the section and foot geometry. **The number typed is the number measured afterwards**
   (gate 2).

**`R-em3d50-3b` Defaults** come from the last wire drawn, then from the technology's wire-bond process
values, then built-in. The tool shows the current diameter, material and styles on the toolbar, where
they can be changed before the first click.

**`R-em3d50-3c` Editing.** Vertex mode on a wire shows its axis points. Moving one is wBond's point edit,
with the feet re-seated on release. Moving an **end** point re-looks-up its pad, and refuses if none. A
wire whose pad moves (the die is moved) is **not** dragged along. It keeps its points, and elaboration
refuses it with *"w3's start is no longer on a pad"*, because silently re-routing a wire changes its
inductance. *Re-seat Wire Ends* (context menu) moves each end vertically onto the pad now under it,
where there is one.

## 4. `R-em3d50-4` — arrays of wires

*Duplicate* and *Array…* (brief 46) apply. An array element whose ends are not both on pads is created
anyway and flagged in the tree, so a wire array across a row of pads is one command, and a pitch error
shows at once on the ends that miss.

## 5. Gate

`tests/Ui.Tests/ThreeD/Wire*`, `tests/Ui.Tests/Em3d/Em3dWireTests.cs` (must still pass unchanged).

1. **Split is byte-identical** (§1a) on every existing wire fixture and on the Bond wire and Package
   examples.
2. **Typed loop height is measured loop height.** Drawing with a typed assembly loop height gives an
   elaborated wire whose `Em3dWires.AssemblyLoopHeight` equals it to 1 DBU, for ball–wedge, wedge–wedge
   and a pad-height difference of 10 mil.
3. **Across hierarchy.** A wire from `U1/pad3` (a layout instance of a die, at the instance's z) to a
   parent lead elaborates with its start foot on the die pad's top z and its end foot on the lead's.
4. **No pad, no wire.** Moving the die away makes elaboration refuse naming the wire and end. *Re-seat*
   fixes the vertical case only.
5. **Equivalence.** The Bond wire example's `.clay` + `.wBond`, redrawn as a `.c3d` with the layout as an
   instance **without** its `.wBond` and one drawn `Wire` on the same points, gives the same `Em3dSweep`
   rings (1e-12 relative) as the `.cem` route.

## 6. Owner check (pixels not seen)

In the **Debug** build:
- place the example die in a package;
- draw a ball–wedge wire from a die pad to a lead with a typed loop height;
- array five wires at the pad pitch;
- move a mid-point in Vertex mode;
- move the die and see the refusal; re-seat.

## 7. Scope

- No automatic wire routing, no wire-to-wire clearance check. wBond's clearance rule is a per-user
  preference in `src/Ui` today, and bringing it here is a later brief.
- No change to kernel W, wBond or any `.wBond`.
