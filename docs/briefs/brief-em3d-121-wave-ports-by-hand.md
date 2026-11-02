# Brief 121 — wave ports by hand: a coax, and terminals against the air box's ground

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d121-n` ·
**Precedent:** [brief 114](brief-em3d-114-terminal-wave-port-model.md) (the N-conductor inference, `RayPath`, *Make Port ▸
Wave* writing terminals) and its gate `tests/Ui.Tests/ThreeD/TerminalWavePortTests.cs`
**Area:** `src/Design/ThreeD/C3dPorts.cs` (`Resolve`'s wave branch ~515–575, `InferReference` ~614, `TerminalsFor` ~728,
`RayPath` ~758), `src/Ui/ThreeD/C3dEditorViewModel.Simulate.cs` (`PortFromFace` ~741, `MakePortItem` ~810; read only
unless a gate needs it), `examples/3D Wave Ports/` (Launch and Pair, README), `docs/user/src/reference/em-setup.md`,
`docs/user/src/new-user-guide/index.md` §14, `tests/Ui.Tests/ThreeD/`, `tests/Ui.Tests/Examples/Em3dWavePortsExampleTests.cs`
**Depends on:** 114, 116, 117 · **Found by:** brief 117 (`examples/RESOLVED.md` § brief-em3d-117, first bullet)

---

## 0. Why this brief exists

Brief 117's example teaches a newcomer to set up a wave port, and **neither of its two ports can be made by hand**. Both
are stated in the `.c3d`, and the README has to say that the gesture does not work. Measured on the shipped files (a
port drafted as *Make Port ▸ Wave* drafts it: the face's rectangle, nothing else stated):

| Cell | What *Make Port ▸ Wave* does today | What it should write |
|---|---|---|
| **Launch** (coax on the xmin face) | Greyed out. Its tip is the refusal *"'housing' and 'pin' overlap across its region of the xmin face, so no straight path runs between them; state its VoltagePath."* The editor has no field for a voltage path | P1, pin against housing, its path from the bore's wall to the pin |
| **Pair** (two strips; the ground planes are the air box's PEC zmin/zmax faces) | Writes ONE port, strip_b against strip_a (*"neither is in the ground set, and their surfaces are equal, so the one on the lower edge is negative"*) | One port with two terminals, strip_a and strip_b, referenced to the box's PEC faces |

Both causes are in `C3dPorts`, and both were left open on purpose by brief 114:

1. **The coax.** `Resolve`'s single-port branch finds the voltage path with `Path`, which measures the gap between the
   two conductors' bounding boxes in the face. A housing's box covers its pin's, so `Path` returns null and the port is
   refused. Terminals already fall back to `RayPath` for exactly this geometry (R-em3d114-1e, "a stripline's joined
   grounds, a shield"). Brief 114's own note: *"A single wave port is unchanged; it still refuses an enclosed conductor
   (a coax) and asks for VoltagePath. Extending the fallback to it would only turn refusals into answers, but no brief
   asked for that."* This brief asks.
2. **Box-face grounds.** `TerminalsFor` returns null (the two-conductor reading) whenever exactly two NON-box conductors
   meet the region, whatever PEC box faces also meet it. Its own reference rule could not answer anyway: zmin and zmax
   are both in the ground set with equal areas, which `InferReference` calls a tie. Drawing the planes instead does not
   help either: two ground sheets tie the same way, and the second is then refused as *"neither a terminal nor the
   reference"*. A united ground (one Boolean) is one conductor, but openEMS writes a Boolean as a polyhedron, and a
   polyhedron leaves out the grid nodes on its faces (brief 42), so the planes would move.

**The engine already handles both answers.** openEMS's placement classifies a strip against every PEC box face
(`FdtdWavePorts.GroundSegments`), so a strip between zmin and zmax is *"a strip between two reference planes"* whichever
face the port names. And a coax with a stated path runs on both solvers today: brief 117's gate 4 re-runs it.

## 1. `R-em3d121-1` — a single wave port infers a coax's path

In `Resolve`'s single-port branch, when `Path(feet[negative], feet[positive])` is null, fall back to `RayPath` from the
negative conductor's section to the positive's foot, as `WaveTerminals` does, **only when the negative's section
encloses the positive's foot with clearance** (the positive lies in a hole of the negative's section and touches it
nowhere). Otherwise the refusal stands as it is. Two conductors that genuinely overlap on the face are shorted, and a
ray between them is not a voltage path.

- The negative end is chosen as today (`Negative`: the ground set's, else the larger surface). For the Launch that is
  the housing, as the shipped file states.
- The reason the port reports names the fallback, as the terminals' does: *"… the path runs from 'housing' to 'pin'
  along a ray, because 'housing' encloses 'pin' on the face"*. `explain` and the status line print it.
- `Flip` behaves as it does for any single port.
- A stated `VoltagePath` still wins.

**Expected:** on the Launch, the inferred path is the one brief 117 stated, from the bore's wall (z = axis_z + 0.67 mm)
to the pin's surface (axis_z + 0.2 mm) along the centreline, give or take which of the four ray directions `RayPath`
takes when they tie. Report which it takes. **Polarity matters, the direction along the axis does not:** every ray runs
from housing to pin.

## 2. `R-em3d121-2` — the air box's PEC faces are one reference for terminals

1. **`TerminalsFor`:** when two or more non-box conductors meet the region, **none of them is in the ground set, none's
   section encloses another's** (§1's test: the coax keeps its two-conductor reading), and one or more PEC box faces
   meet it, the non-box conductors are terminals and the reference is a box face. Today this case returns null at the
   `found.Count(n => !IsBoxFace(n)) == 2` line.
2. **`InferReference`:** when every ground-set candidate is a PEC face of the air box, they are **one ground**, not a
   tie. Take the first in the box's face order (xmin, xmax, ymin, ymax, zmin, zmax), and give the reason *"the air box's
   PEC faces are one ground; 'airbox/zmin' names it"*. A tie between drawn conductors is still refused.
3. The terminal order is `TerminalsFor`'s, by foot along the rectangle's long axis. For Pair that gives strip_a 1 and
   strip_b 2 on xmin, and 3 and 4 on xmax, which is brief 117's numbering (1–3 and 2–4 on one strip).

**What stays as it is (D1's default):** `Resolve` on a STORED port without `Terminals`. A file whose bare wave port meets
two strips and the box's PEC faces keeps its strip-to-strip reading, since the reader never invents terminals (overview
D4) and existing results must not move. It gains a **note** naming the alternative: *"'xmin' meets 'strip_a', 'strip_b'
and the air box's PEC faces; it is read as one port between the two strips. Make Port ▸ Wave on that face writes a
terminal per strip, referenced to the box."*

## 3. `R-em3d121-3` — the example made by hand

1. Re-author both ports **by the gesture**, the way `Em3dConnectorAuthoring` replays the 3D Connector's (a test-side
   replay against `C3dEditorViewModel`):
   - Launch: delete P1, then *Make Port ▸ Wave* on the bore's end face at x = −5 mm.
   - Pair: delete both ports, then *Make Port ▸ Wave* on the fill's xmin face and then its xmax face.
2. Ship what the gesture writes: no `VoltagePath` on P1, no stated `Reference` on Pair's ports unless the gesture writes
   one (it does today, and should keep doing so).
3. **The Launch's rectangle changes.** The bore face's bounding square is 1.34 mm, where the shipped port is 2 × 2 mm.
   Both regions are the same PTFE annulus once the metal is removed, but the Gmsh geometry differs, so re-run brief 117's
   gate 4 (Benchmark). If a recorded |S| moves past its tolerance, re-record the runs from the new file and update every
   quoted string in the README and the new-user guide. `TheQuotedValues_AreTheRecordedOnes` and gate 2 hold them
   together. Pair's rectangle is the fill's face, as shipped, so its lowered problem must come out identical (gate 3
   below), and its numbers stand.
4. **README and new-user guide §14:** the "by hand" steps become the gestures (right-click the face ▸ *Make Port ▸ Wave*;
   read the arrow; for Pair, two numbered arrows per face and the reference named in the status line). Delete the two
   paragraphs that say the file states what the gesture cannot. `examples/RESOLVED.md` § brief-em3d-117: mark its first
   bullet resolved by this brief.
5. **`em-setup.md`:** #wave-ports gets one sentence: a conductor enclosed by another (a coax) has its path inferred along
   a ray from the outer to the inner. #wave-port-terminals: the air box's PEC faces count as one reference, and the
   sentence *"Two candidates with equal surfaces are refused, asking for `Reference`"* gains *"…among drawn conductors"*.

## 4. Decisions — each with this brief's default

| # | Question | Default |
|---|---|---|
| D1 | A stored bare wave port meeting two strips and the box's PEC faces | **Unchanged reading, plus the note** (§2). Changing it would move existing results and contradict D4 |
| D2 | Which box face names the reference | **The first PEC face in xmin…zmax order.** openEMS uses every PEC face anyway; Palace refuses terminals (D14) |
| D3 | An editor field to state a voltage path | **Not built.** With §1, no shipped example needs one; a geometry the ray gets wrong can still state it in the file. Say so in em-setup.md |
| D4 | §1's fallback when the negative does NOT enclose the positive, but their boxes overlap (an L-shaped return beside a strip) | **Refused, as today.** Report any such case found in the repo's fixtures |

## 5. Gate

`tests/Ui.Tests/ThreeD/` (a new class, or beside `TerminalWavePortTests`). Kernel-free fixtures where possible. 116's
`Coax()` (a shield prism with a 64-gon hole, PTFE and a pin) is the coax.

1. **Coax, inferred.** 116's coax with `VoltagePath`, `Positive` and `Negative` removed resolves: negative 'shield',
   positive 'pin', a path from the hole's edge to the pin's surface along u or v through the centre, and the reason
   names the fallback. A pin moved so that it touches the shield is still refused with today's sentence.
2. **Pair, by the gesture.** On 117's Pair with its ports deleted, *Make Port ▸ Wave* on the fill's xmin face writes one
   port named `xmin`, `Number` 0, reference `airbox/zmin`, terminals (1, P1, strip_a) and (2, P2, strip_b), and the status
   line names the reference and the reason. On xmax: 3 and 4. One undo entry each.
3. **Same problem as shipped.** The gesture-made Pair lowers (`C3dProblemAssembly.Assemble`, setup *openEMS*) to ports
   equal to the shipped file's: number, positive, negative, voltage path to 1 DBU, face group. The gesture-made Launch
   (`[KernelFact]`) lowers P1 to the shipped polarity, and to the path §1 reports.
4. **Unchanged.** 114's gates pass unchanged (the shield fixture still takes 'gnd' as reference). A stored bare port on
   Pair's face still reads strip-to-strip, with D1's note.
5. **The example.** `Em3dWavePortsExampleTests` passes (routine tier). Gate 4 (Benchmark) passes, or is re-recorded per
   §3.3 with the reason in `examples/RESOLVED.md`.

Scope the runs: `--filter` on the new class, `TerminalWavePortTests` and `Em3dWavePortsExampleTests`; the Benchmark
gate once. No full suite.

## 6. Owner check

Open **3D Wave Ports ▸ Launch**, delete P1, right-click the bore's end face ▸ *Make Port ▸ Wave*: an arrow from housing
to pin appears. Open **Pair**, delete both ports, *Make Port ▸ Wave* on each end face: two numbered arrows per face, and
the status line names `airbox/zmin` as the reference. Run *openEMS* on Pair; the result matches the shipped one.

## 7. Scope

- No solver change: openEMS's placement already reads every PEC face as ground, and Palace still refuses terminals (D14).
- No voltage-path editor (D3).
- `3D Connector` is untouched. Its gap port is lumped and is not affected.
