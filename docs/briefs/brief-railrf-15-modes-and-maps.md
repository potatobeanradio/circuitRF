# Brief 15 — modes and maps: where a resonance lands, not just that it exists

**Series:** [railRF](brief-railrf-0-overview.md) · **Tag:** `R-rail15-n` · **Phase:** P2b
**Area:** `src/Engine/Pdn/`, `src/Render/`, `src/Ui/RailRf/` · **Depends on:** 8, 14 · **Blocks:** nothing
**Design note:** [`railrf.md`](../design/railrf.md) §4.5, §2.4 ("The plane resonances and the impedance map"), §7

---

## 0. What this brief delivers

| Piece | Home | What it is |
|---|---|---|
| `PdnModeSolver` | `src/Engine/Pdn/PdnModeSolver.cs` | A generalised eigenproblem on the loss-free system. **Numerics only** — it takes the mesh adjacency and the cell terms, never a `Technology`. |
| `PdnFieldMap` | `src/Engine/Pdn/PdnFieldMap.cs` | A mode's field, and \|Z\| across the plane at a chosen frequency. |
| The `\|Z\|` overlay | `src/Ui/RailRf/` + `src/Render/` | Brief 8 created the empty tab; this brief fills it. |

### `R-rail15-1` — the modes come out of the SAME discretisation, and that is the point

§4.5:

> The cavity modes come out of the same discretisation as a generalised eigenproblem on the loss-free system,
> **so the mode list and the sweep cannot disagree about the structure.**

A separate analytic mode calculator would be cheaper and would be wrong the moment the board is not a
rectangle — and it would disagree with the sweep on exactly the boards where the answer matters. Build the
eigenproblem on brief 14's own matrices.

**`src/Engine` cannot see `src/Design`** (overview §1a). So `PdnModeSolver` takes the assembled adjacency and
the per-cell L and C as arrays, and returns eigenvalues and eigenvectors. The mapping back to cells and the
drawing both happen on the `src/Design` and `src/Render` side.

---

## 1. `R-rail15-2` — WHERE a mode lands is the finding, not the frequency

§2.4:

> The cavity modes of your actual shape with a field map each, and |Z| across the whole plane at a chosen
> frequency. **A mode whose maximum sits on the load pin field is a problem; the same mode with its maximum
> in a corner is not, and only the map distinguishes them.**

So the mode list is not a list of frequencies. Each row carries the frequency **and** the mode's own field,
**and** the value of that field at each declared observation port — which is what turns "your board has a mode
at 1.2 GHz" into "your board has a mode at 1.2 GHz with its maximum on U1's power pins."

That per-port evaluation is the deliverable. A mode list without it is a list nobody can act on.

---

## 2. `R-rail15-3` — the |Z| map is an overlay, drawn by `src/Render`

Brief 8 built the seam, the theme and the tab; this brief supplies the field. The same rules apply and none of
them is negotiable:

- **Opaque paint** — nothing depends on the page background (brief 8 `R-rail8-10`).
- **Repaint through `InvalidateOverlay`**, never the path cache (`R-rail8-2`).
- **`ContentBounds()` includes the legend**, or Zoom to Fit frames it out (`R-rail8-7`).
- It goes into **brief 9's overlay parameter list**, or it is silently absent from every copy (`R-rail9-3`).

That last one is the specific trap this brief will hit: brief 9's list was written when the drop map and the
class map were the only overlays. **Adding the |Z| map to the renderer and not to that list produces a copy
that looks right and is missing the thing the user copied it for.** Brief 9's gate is written to catch it; run
it.

---

## 3. `R-rail15-4` — the acceptance is the textbook rectangle

§4.5:

> For a rectangle they reduce to the textbook `f_mn = (c / 2√εᵣ)·√((m/a)² + (n/b)²)`, **which is the
> acceptance check.**

§7:

> **The rectangular cavity.** A uniform rectangular plane pair has closed-form modes and a closed-form input
> impedance. The mesh must reproduce **the first six modes to better than 2 %**, with **monotone convergence
> in cell size**.

Both halves. The monotone-convergence half is the one that catches a discretisation that happens to be right
at one cell size — refine three times and assert the error falls each time, rather than asserting a single
number and hoping.

---

## 4. Tests — `tests/Engine.Tests/Pdn/PdnModeTests.cs` and `tests/Ui.Tests/RailRf/RailZMapTests.cs`

- **`R-rail15-4`**: the first six modes of a uniform rectangle against `f_mn`, to 2 %, with monotone
  convergence over three cell sizes. This lives in `Engine.Tests` because it is pure numerics.
- **`R-rail15-1`**: the mode frequencies **agree with the peaks the sweep finds**, on the same board, to a
  stated tolerance — which is the property §4.5 claims and the reason for building the eigenproblem on the
  same matrices. A disagreement here means the two are not, in fact, the same discretisation.
- **`R-rail15-2`**: on a rectangle, the (1,0) mode's field is evaluated at two ports — one at an edge
  maximum, one at the centre null — and the reported per-port values differ by the expected ratio. A mode list
  that reported only frequencies would pass nothing here.
- **`R-rail15-3`**: the |Z| overlay renders, is included in `ContentBounds()`, does not invalidate the path
  cache, and **appears in a clipboard copy's SVG text** — brief 9's gate, re-run with this overlay active.
- **Determinism**: the same board's mode field renders byte-identically twice. An eigensolver returning
  arbitrarily-signed eigenvectors produces a map that flips between runs; normalise the sign explicitly and
  say why.

### `R-rail15-5` — cost, measured once and not asserted

§3's whole argument is that this is cheap where a MoM solve is not: *"a sparse system whose size is the cell
count, solved by exactly the sparse complex LU circuitRF already carries … the difference between a minute
and a day."* Measure it once on the note's own 90 × 70 mm / 3,200-cell board, record the number in
`src/Engine/RESOLVED.md`, and **do not add a timing test.** If it turns out not to be cheap, that is a finding
worth reporting rather than a gate worth writing.

---

## 5. Scope

- **No full-wave.** §3.
- **No new renderer.** Brief 8's `RailMapRenderer` draws every map; this brief adds a field, not a painter.
- **No transient, no time-domain view of a mode.**
- **No mode-based advice.** railRF names the modes and shows where they land. It does not suggest stitching
  vias, a different outline or a damping resistor — §2.7: *"It does not route, place or optimise your board.
  It measures."*

**On completion:** record findings in `src/Engine/RESOLVED.md` and `src/Ui/RESOLVED.md`. Never in a CLAUDE.md.
