# Brief MIM-13 — where the unknowns go on a spiral, and the two ceilings

**Smallest of the round and independent of the other four — take it whenever.** It is numbered last
because nothing depends on it, not because it matters least: the ceiling is the first wall a user
hits. Three separate things get conflated into "the mesh is bad", and none of them is a bad mesh.

## 1. The ceilings are 5,000 and 12,000, and a spiral with the edge mesh on is past both

`SurfaceMesher.UnknownCeiling` = **5,000** (dense) and `AcceleratedUnknownCeiling` = **12,000**
(AIM). The user reports ~30,000 unknowns with the edge mesh on. That is past both, so the run is
refused — and the dense refusal's own last clause is *"full-wave analysis of a structure this size
needs matrix compression, which is not built"*, which is true of the dense path and **misleading now
that AIM exists**. A user reading it concludes the tool cannot do this at all.

**The multi-level AIM restriction was retired at P12** (`PlanarAimBordered`'s header: *"Until P12
`PlanarAimGeometry.Build` refused any mesh carrying a ẑ basis and `PlanarSolveContext` refused the
general (multi-level) kernel under `Aim`"*), so a report that AIM "needs a multi-level kernel" is
either a stale message or a different refusal being read as one. **First action: capture the exact
refusal text from the edge-mesh-on run** — one run, and the answer decides whether anything here is a
code change or a wording change.

Either way both refusals should name the two numbers, say which path each belongs to, and say what
the structure costs, rather than leaving the reader to infer that 30,000 is hopeless. 30,000 is 2.5×
the accelerated ceiling, not 100×.

## 2. The edge fan costs more on a spiral than on anything else, and the engine already says why

The note that fires on the owner's own spiral layout:

> Bulk pitch spans 7.33× here, so each edge fan has further to climb; on mostly-rim artwork that can
> RAISE the cell count. Edge mesh off removes the fan.

That is the mechanism and it is correct. The fan is clamped at 3× per cell, so each attractor costs
about log₃(bulk/edge) cells, and **a spiral is almost entirely rim** — three turns of 10 µm metal
with 8 µm gaps has no interior to speak of. Combine that with a bulk pitch spanning 7.33× (because
the same layout also carries a 500 µm extent and a 38 µm bulk cell) and the fan multiplies rather
than adds.

**This is a real cost and not a defect**, and two commits on 2026-09-15 already went at it
("The edge fan ignores the control that sets mesh density" and "Coarsening the mesh now shortens the
edge fan instead of lengthening it"). What is missing is not an algorithm, it is a **budget the user
can see before they wait**: the cell count, and where the cells went — bulk against fan, per level —
reported from the mesh **before the solve starts**, so a 30,000-unknown mesh is a number on screen in
a second rather than a refusal after a minute.

## 3. Edge mesh off is not free, and the run says so in a note nobody reads twice

> Edge mesh off — the 1/√d edge current is not resolved, so **loss and Z₀ will read low**.

On a spiral inductor that is the Q. A user who turns the edge mesh off to make the run fit is
trading exactly the quantity they are usually there for. That sentence is right and it should be a
**warning** on a run whose artwork is mostly rim, not a note among thirty.

## What to build

- **A mesh budget, before the solve.** Cell and unknown count, split bulk/fan and per level, against
  both ceilings, emitted from the mesh rather than from the fill. It costs nothing and it turns
  "I waited and then it refused" into "I can see this is 2.5× over, let me coarsen".
- **Both refusals name both ceilings** and which path each governs, and neither says compression is
  unavailable while AIM exists.
- **The edge-mesh-off note is promoted to a warning** where the artwork is mostly rim — the same
  `climb` quantity the pitch field already computes is the discriminator, so nothing new is measured.
- **Capture the AIM refusal the user actually saw** before deciding whether item 1 is code.

## Not in this brief

Raising either ceiling. That is a memory and time question with its own measurements — the run-level
arithmetic is `PlanarSystem.ResidentPhrase` and the measurements are `PlanarBudgetTests` — and none of
the three items above needs it.
