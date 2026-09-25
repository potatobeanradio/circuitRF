# Brief 4 — bond wires in 3D: cross-section, bond style, feet, loop height

**Series:** [3D EM, first series](brief-em3d-0-overview.md) · **Tag:** `R-em3d4-n` ·
**Design note:** [`em-3d.md`](../design/em-3d.md) §6.6, §4.1a (wire metals)
**Area:** `src/WBond/WBondDesign.cs` (per-wire fields), `src/WBond/WBondIo.cs`,
`src/Design/Layout/Em3d/Em3dWires.cs` (new), `src/Design/Layout/Assembly/WasmModel.cs` **or**
`src/Design/Layout/TechModel.cs` (D2)
**Depends on:** 3, and owner decision D2 · **Blocks:** 7, 9 (case A)

---

## 0. What this brief delivers

A layout with a stem-paired `.wBond` (`WBondCell`'s WB40 pairing) generates, in the `Em3dProblem`
of brief 3, one swept solid per wire. It has a **flat-bottomed, perimeter-matched hexagon** by
default, a **foot** at each wedge end lying face-down on its pad, a **ball** at each ball end, and a
**loop height** reported by the assembly definition.

**Kernel W is not touched.** Every wire it solves today, it solves identically afterwards. The foot,
the ball, the neck and the cross-section exist only in the 3D model (§6.6).

---

## 1. `R-em3d4-1` — the per-wire fields in `.wBond`

**`R-em3d4-1a`** On `Wire`, additive, nullable, omitted at default, and **no `.wBond`
`FormatVersion` bump**:

| Field | Type | Absent means |
|---|---|---|
| `CrossSection` | `Hexagon \| Round` | `Hexagon` |
| `StartBond` | `Wedge \| Ball` | `Wedge` |
| `EndBond` | `Wedge \| Ball` | `Wedge` |
| `FootLengthNm` | long? | the process default (§3) |

**`R-em3d4-1b`** On the array, a `FootLengthNm` override that applies to every member without one
of its own. Precedence: **wire, then array, then process default.** It is the same chain §6.6
describes, and each level is visible in `explain` (brief 5).

**`R-em3d4-1c` The ball/wedge designation returns, and this time something reads it.** It was
removed on 2026-08-18 because nothing branched on it (`src/WBond/LoopShape.cs`'s header records
why). A `.wBond` written in between reads as wedge–wedge. **Kernel W still does not read it**,
and a source scan in `src/WBond/` (outside `WBondDesign`/`WBondIo`) holds that. If kernel W starts
branching on bond style, that is a separate decision and must not happen by drift.

**`R-em3d4-1d`** The wBond editor's wire table gains the four columns. The generated reference page
(`circuitrf reference wbond`) describes them (gate 8).

---

## 2. `R-em3d4-2` — the cross-section

**`R-em3d4-2a` Perimeter-matched hexagon** (§6.6, decided): side = πd/6, height flat-to-flat
(√3π/6)·d, width corner-to-corner (π/3)·d. **Flat top and flat bottom.** One helper computes all
three from d. It is the only place the numbers appear, with §6.6's table cited in its comment.

**`R-em3d4-2b` Swept with "up" held fixed** (world +z), so the bottom face stays parallel to the pad
along the whole wire. The sweep is along the wire's axis polyline as the `.wBond` stores it
(`WireGeometry3D` reads the same points). **The polyline is read as the axis**, not as the bottom
surface (§6.6).

**`R-em3d4-2c` Near-vertical segments.** "Up held fixed" degenerates where the path is vertical (a
ball neck, §4b). There the section's orientation is carried over from the previous segment. A
segment within a small angle of vertical uses that rule, the angle is a named constant, and a test
sweeps a path through vertical and asserts no zero-area or flipped section.

**`R-em3d4-2d`** `Round` is a circle of diameter d, swept the same way.

**`R-em3d4-2e`** F0's Q3 measures the hexagon's corner-crowding penalty. The generator does **not**
correct for it. `explain` reports the cross-section per wire and cites the F0 figure, so a user
comparing against kernel W knows what part of a difference is the section.

---

## 3. `R-em3d4-3` — process defaults: foot length, ball diameter, ball height

**`R-em3d4-3a` Where they live is owner decision D2** (overview §1d). This brief's default is
the **`.wasm`**, if the owner agrees. It is the assembly house's document, it already resolves per
workspace with a per-`.wBond` override (`AssemblyRef`), and it holds `Process` rules. The
alternative is the `.ctech`, beside brief 2's materials, exactly as the note says. Either way:
- three nullable fields: `DefaultFootLengthNm`, `DefaultBallDiameterNm`, `DefaultBallHeightNm`;
- **one resolver**, `WireBondProcess.Resolve(wire, array, design, workspace)`, which is the only code
  that knows where the defaults live. Moving them later changes that function and nothing else.

**`R-em3d4-3b` When nothing states a default**, the generator uses a built-in starting value and
**says so on every run** as a note naming the value and where to set it. The starting values are
named constants with provenance comments: foot length **2d**, ball diameter **2.5d**, ball height
**0.5d**. The ratios are §6.6's "about twice the diameter" and conventional first guesses for the
other two. They are flagged **unverified** in the comment and in `RESOLVED.md` until the owner's
assembly data (brief 1 §1) replaces them. A silently-used guess is the one outcome to avoid.

---

## 4. `R-em3d4-4` — feet and balls

**`R-em3d4-4a` A wedge end has a foot** (§6.6): a straight run of the same cross-section, lying with
its **bottom face on the pad's top surface**, extending from the wire's end point **outward, away
from the loop**, along the plan direction of the wire's final segment. Length from §3. The loop
the `.wBond` describes is unchanged. The foot is added **beyond** the end point, never replacing
part of the loop.

**`R-em3d4-4b` A ball end** is a `TruncatedSphere` (a flattened ball) of §3's diameter and height,
its bottom on the pad. The wire meets its top face. **If the `.wBond` path does not arrive
vertically**, the generator inserts a vertical **neck** from the ball's top to where the path
leaves, so the wire lands end-on, face to face (§6.6).

**`R-em3d4-4c` The pad's top surface** is found from the layout, the same way kernel W finds the
bond's pad (`WBondEmbedding`). **Do not re-derive it.** A wire end over no pad is a refusal naming
the wire, never a foot on nothing. A foot that would overhang its pad's edge is a **warning** naming
the wire and the overhang. It is still generated, because a foot longer than the pad is a real
assembly defect a user wants to see modelled rather than rejected.

**`R-em3d4-4d` Face-to-face contact is the point** (§6.6): the foot's bottom face and the pad's top
face must be **coplanar to the bit**, so the mesher sees a shared surface and not a sliver. Compute
the foot's z from the pad's z with the same arithmetic, not from the wire's axis minus half a
height, which accumulates rounding. Gate 3 asserts exact equality.

---

## 5. `R-em3d4-5` — loop height, the assembly definition

**`R-em3d4-5a`** Reported loop height = the top surface of the wire at its apex, minus the top
surface of the **lower** of the two pads it is bonded to (§6.6). One function computes it from the
generated solid, not from the `.wBond`'s polyline.

**`R-em3d4-5b`** It differs from wBond's own `LoopHeight` (max z − min z of the axis polyline) by
about one section height. **Both are reported, side by side, labelled**, because a user holding
an assembly specification and a user holding the `.wBond` will each look for their own number.

**`R-em3d4-5c` An override is out of scope** for this brief. §6.6 says the 3D setup "accepts [the
assembly height] as an override". That needs a rule for **how** the axis is reshaped to meet it,
which wBond's `LoopShape` owns, and it is a separate decision. This brief reports the number only.
Say so in `src/Design/RESOLVED.md`.

---

## 6. `R-em3d4-6` — wire metals through the technology

**`R-em3d4-6a`** In a 3D setup, a wire's `Material` is looked up in the technology's `Materials`
first (brief 2), then in the `.wBond`'s own list (§4.1a). A name both define **differently** is a
warning, and the technology's values are used.

**`R-em3d4-6b` An unknown name is a refusal in 3D.** `WBondDesign.MaterialFor` falls back silently to
the design's first material and then to gold. That is kernel W's existing behaviour, out of scope
to change here, but the 3D generator **must not call it**. It resolves names itself and refuses a
name found nowhere, naming the wire and listing the known metals. Record the kernel W fallback in
`src/WBond/RESOLVED.md` as an observation for the owner, not a fix.

---

## 7. Gate

`tests/Ui.Tests/Em3d/Em3dWireTests.cs`. No solver.

1. **Hexagon dimensions** from d = 25.4 µm match §6.6's table to 1e-12 relative, and its perimeter
   equals πd.
2. **Bottom face parallel to z = const** at every section of a looped wire.
3. **Foot on pad, exactly**: foot bottom z == pad top z, bitwise (§4d).
4. **Foot direction**: outward from the loop, along the plan direction of the final segment. Test
   both ends of an asymmetric wire.
5. **Ball end with a non-vertical arrival** inserts a neck; with a vertical arrival, inserts none.
6. **Loop height**: for brief 1's case A geometry, the assembly height and the wBond height differ
   by one section height ± the foot geometry's exact contribution (compute the expected value in
   the test from first principles, not from the implementation).
7. **Kernel W unchanged**: every `.wBond` in `examples/` and `testdata/` produces a byte-identical
   kernel W result. Compare the kernel's assembled inputs, not a full solve (as brief 2 gate 4).
8. **Round-trip and reference page**: every existing `.wBond` round-trips byte-identically;
   `circuitrf reference wbond` shows the four new fields.
9. **Unknown metal is a refusal**, not gold.
10. **Default provenance note** appears when no process default is stated, and not when one is.

## 8. Scope

- **No assembly-loop-height override** (§5c).
- **No change to kernel W, `LoopShape`, or `MaterialFor`.**
- **No bumps, no flip-chip.** Later.
