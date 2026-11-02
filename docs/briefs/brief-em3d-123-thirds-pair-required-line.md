# Brief 123 — openEMS grid: a required line on a thirds edge

**Series:** [3D EM, eighth series](brief-em3d-112-overview.md) · **Tag:** `R-em3d123-n` ·
**Precedent:** R-em3d8-2/-3 (required lines, the thirds rule, ports on lines), R-em3d65-3e (a kernel solid's lines),
brief 122 (`FdtdGrid.ThirdsViolations` and the repair on violation), all recorded in `src/Engine/RESOLVED.md`
**Area:** `src/Engine/Em3d/FdtdGrid.cs` (`CollectRequired`, `KernelLines`, `Merge`, `ThirdsViolations`),
`src/Design/Em3d/CsxcadWriter.cs` (how a port or a material is written when its extent is not on a line),
`tests/Engine.Tests/Em3d/FdtdGridTests.cs`, `tests/Ui.Tests/Em3d/OpenEmsBackendTests.cs`, the openEMS goldens
**Depends on:** 122 · **Found by:** brief 122 (`src/Engine/RESOLVED.md` § "openEMS grid: a thirds pair broken by grading
in a narrow gap", "A second kind, NOT repaired")

---

## 0. The defect, as far as it is known

The thirds rule puts an edge's two lines a third of a cell inside the metal and two thirds outside, **with nothing
between**, so the edge singularity sits a third of a cell into one cell. Brief 122 made the fill keep that. It left a
second way a pair loses it: **another required line lies between the pair's two lines**, almost always ON the edge. The
edge then has three lines, the middle one on the edge, which is the grid the rule exists to avoid, and two cells of h/3
and 2h/3, which can set the time step. `ThirdsViolations` skips these pairs on purpose, since no cell size can clear them.

Brief 122's trace of the routine openEMS classes found such a pair in **15 of 21 grids**. Three sources are known:

| source | where | why the line is there |
|---|---|---|
| a lumped port's extent (`PortExtent`, fixed) | `FdtdGridTests.Microstrip()`: both strip edges, 2 of 2 pairs; every port drawn across a strip's full width | R-em3d8 puts every port extent on a line (`Gate1_…_PortsOnLines`) |
| a dielectric's face (`MaterialFace`) | the Board fixture's ground and copper at x = ±5 mm, where the laminate ends with the copper (its ports end there too; R-1.1 says which line it is) | a material interface is a required line |
| **a kernel solid's own face** | the 3D Connector housing: every pair on the housing, e.g. its edge at x = −1 mm with lines at −0.808 and −1.383 mm and its own face line at −1 mm between them | `KernelLines` puts a line on every planar face normal to the axis, then adds the thirds pair around the face's edge. **So the thirds rule has never taken effect on a kernel solid.** The ring path (an extruded plate) puts no line on its own edge |

**Nobody has measured whether any of this costs accuracy.** The edge always has a line here, so the pair may only cost
cells and Δt. That is why this brief measures first and may change nothing (D1).

**Upstream.** openEMS's own `python/Tutorials/MSL_NotchFilter.py` (pinned 0.37.0-rc3, `~/repos/openEMS-Project`, lines
48–62 and 83–87) adds the thirds lines at ±W/2 + (2r/3, −r/3) and runs its MSL port from −W/2 to W/2 **with no line at
±W/2**: CSXCAD snaps a primitive's extent to the nearest grid line. Its substrate spans the whole mesh, so it has no
material face on a copper edge. Read the source before relying on this; do not invent from it.

## 1. `R-em3d123-1` — measure first

1. **Classify.** Extend 122's trace (scratch, not shipped) to name the KIND of every required line inside a pair:
   `PortExtent`, `MaterialFace`, a kernel solid's own face, or anything else (`AirBoxFace`, `SheetPlane`,
   `WavePortPlane`, `MetalEdge` of another solid). Run it over 122's R-1.3 set plus `FdtdGridTests.CaseB()`. Count per
   kind and per fixture. If a kind appears that the table above does not list, report it.
2. **Accuracy, three variants**, each a scratch switch:
   - **V0**, today.
   - **V1**: when a required line lies inside a pair, drop the pair and keep the edge line (what `ThirdsRule = false`
     does at that edge).
   - **V2**: keep the pair, and do not force a line where a port extent or a material face falls inside it (it snaps,
     as upstream's does). For a kernel solid, V2 omits the face line on the edge when its pair is placed, as the ring
     path does.

   Run each against a reference produced **independently of circuitRF** (never V0 against V2):
   - (a) **Homogeneous stripline** with lumped ports across the strip's full width (`OpenEmsBackendTests.Gate7`'s
     fixture, after confirming its pairs are split by its ports): Z₀ and ε_eff by the two-length method (10 and 30 mm,
     so the ports' own parasitics cancel), against Cohn's zero-thickness Z₀ and εr.
   - (b) **Microstrip**, `FdtdGridTests.Microstrip()`'s section (600 µm on 254 µm of εr 9.8), the same two-length
     method, against Hammerstad–Jensen Z₀ and Kirschning–Jansen ε_eff(f). State the closed form's own accuracy and judge
     against it.
   - (c) **The 3D Connector's Launch on openEMS** (kernel housing), against its recorded Palace result.
   - For every variant and case, also record cells, smallest cell, Δt and run time.
3. **Record** the counts and the table in `src/Engine/RESOLVED.md`, under 122's section, before any code.
   **Stop and report** if neither V1 nor V2 beats V0 by more than the reference's own uncertainty in (a)–(c). Then D1
   applies: the brief closes with the measurement and no code change.

Keep the runs short (one short sweep per case, a few frequencies), and use a scratch harness, not new Benchmark tests.

## 2. `R-em3d123-2` — the fix, only if R-1 finds one worth having

**Default: V2 for port extents, material faces and a kernel solid's own face; V1 for every other kind (D4).**

- In `CollectRequired` (after collecting, before `Merge`), a `PortExtent` or `MaterialFace` line strictly inside a
  thirds pair of a conductor is not added as a required line. The source is kept, so `explain` still names it as
  snapped. `KernelLines` does not add the face line at an edge where it places a pair.
- **The writer must be right for an extent that is not on a line.** Find what `CsxcadWriter` assumes (a lumped port's
  `start`/`stop`, its probes, a material box) and either snap explicitly to the line the grid has, stating which, or
  confirm from CSXCAD's source that openEMS snaps the same way. A port whose extent moves by h/3 is a narrower port, so
  say in the notes which line it landed on.
- `FdtdGridTests.Gate1_…_PortsOnLines` changes meaning. Rewrite it to state the new rule, and do not delete it.
- A grid with no such pair is built exactly as today (no churn by construction, as in 122).

## 3. Decisions — each with this brief's default

| # | Question | Default |
|---|---|---|
| D1 | R-1 finds no measurable gain | **Change nothing.** Record the measurement and close; the cells and Δt alone do not justify moving every golden |
| D2 | V1 or V2 | **V2** (upstream's way) unless R-1 shows V2 hurts the port (a), in which case V1 |
| D3 | A kernel solid's own face line at a thirds edge | **Omit it** when the pair is placed, matching the ring path |
| D4 | A line that cannot move (air box face, sheet plane, wave-port feed line, another solid's edge) inside a pair | **V1**: the edge keeps one line and loses its pair |
| D5 | An example's recorded numbers move | **Re-record** under that example's own rules, and update every quoted number in its README and NUG page |

## 4. Gate

1. **`FdtdGridTests`:** `Microstrip()` and `CaseB()` have no required line of a V2 kind inside any thirds pair, and
   their pairs are whole (`ThirdsViolations` empty). Gate 2's fifty layouts still pass 122's assertion.
2. **Accuracy:** R-1's cases (a) and (b) as tests against their closed forms at the tolerance R-1 measured, tagged
   `Category=Benchmark` only if one runs past ~5 s.
3. **Goldens:** every openEMS golden that moves is re-recorded, and the lines that moved are named in
   `src/Engine/RESOLVED.md` with the reason. A golden R-1.1 found clean is byte-identical.
4. **The examples, once:** `Em3dConnectorExampleTests.Gate6` and `Em3dWavePortsExampleTests.Gate4` (Benchmark). Re-record
   under D5 if they move.

Scope the runs: `FdtdGridTests`, the openEMS classes named in 122's R-1.3, the Benchmark gates once each. No full suite.

## 5. Owner check

`circuitrf explain` on the 3D Connector's Launch, setup *openEMS*, before and after: the cell count, the smallest cell and
what set it, and the time step. Then the same on a microstrip with a lumped port across its full width: the port's note
says which line each extent landed on.

## 6. Scope

- The Cartesian grid only (the cylindrical grid has no thirds rule).
- No new setting. Brief 122's repair and `GradingRatio` are unchanged.
- Palace is untouched: its mesh is Gmsh's.
