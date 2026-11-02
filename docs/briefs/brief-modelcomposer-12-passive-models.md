# Brief MC-12 — Passive models: analysis, topology proposals, fitting, geometry families, export as a cell

**Series:** `brief-modelcomposer-0-overview.md` (D2, D12, D13) · **Tag:** `R-mc12-<m>` · **Depends on:** MC-3, MC-5, MC-11
**Area:** `src/Design/Modeling/Passive/`; `src/Engine/Modeling/` (vector fitting, passivity); stock passive
topologies as embedded `.csch` resources; `src/Design/Cells/CellCreate.cs` (used)
**Read first:** `docs/design/tuning-optimization.md` §10–§12; MC-5 (the same constraint and identifiability
machinery); B. Gustavsen and A. Semlyen, "Rational approximation of frequency domain responses by vector fitting,"
IEEE Trans. Power Delivery, 1999 (the published method; written in-house)

---

## 1. Goal

From an S-parameter block — or a family of them over a geometry value — a lumped (or partly distributed) circuit
that reproduces it. circuitRF proposes the topology; the user may draw their own; the result is an ordinary cell a
schematic uses. A user can already do this by hand in a schematic. The composer's value is the proposal, the
family fit and the export.

## 2. Requirements

**R-mc12-1 — Data.** `PassiveSParams` (MC-3): one N-port block, or a family where each file carries a geometry
value (`turns = 2.5`, `length = 300 µm`) from its name or a column. Pads are optional, as in MC-4.

**R-mc12-2 — Analysis**, reported in one line each:
- port count;
- reciprocity (`max |Sij − Sji|`);
- passivity (the largest eigenvalue of `SᴴS` per frequency; a block that is not passive is flagged with the
  frequencies);
- symmetry;
- low-frequency asymptotes from Y and Z (series R and L; shunt C);
- resonances (self-resonant frequencies);
- a **vector fit** of Y (or Z): poles and residues, order chosen by error. Its poles guide the topology choice.

**R-mc12-3 — The topology library.** Embedded `.csch` cells. Pins 1…N are the ports; references are ground.
- One-ports: series RLC, parallel RLC, inductor with self-resonance and skin-effect loss (`R(f) = R0 + Rs·√f`),
  capacitor with ESR/ESL and pad capacitance, resistor with parasitics.
- Two-ports: series element with shunt pad capacitors (π), T network, bond wire, via, spiral inductor (π with
  series R–L, inter-winding C, a substrate network per side), MIM capacitor, a transmission-line segment with pad
  capacitors.

Every element value is a variable with bounds, a lock, a tie and units, exactly as an extrinsic network's are
(MC-4 R-mc4-2).

**R-mc12-4 — Proposals.**
- Each candidate whose port count matches gets analytic starting values from R-mc12-2, then a short seeded fit.
- Candidates are ranked by an information criterion that penalises element count — `n·ln(RSS/n) + 2k`, stated
  in the design note — and the **top three** are offered.
- A **user-drawn** topology (any `.csch` with N pins, drawn or pasted in the network panel) takes the same fit.

**R-mc12-5 — Fit.** The MC-5 machinery: the registry's global algorithm, then Levenberg–Marquardt, on a weighted
`|ΔS|` residual over frequency. Bounds ≥ 0 by default; locks, ties and the identifiability report are as in MC-5.
A frequency weighting (uniform, or emphasis on a band) is a setting.

**R-mc12-6 — Geometry families.**
- One topology across the family.
- Fit each geometry on its own; then fit each element as a smooth function of the geometry value — a polynomial
  of degree ≤ 3 or a low-order rational, chosen by held-out error, monotone when asked; then a joint refinement.
- A **held-out geometry** check: by default the middle one, or the user's choice.
- The family becomes one cell with the geometry as a parameter, valid over the fitted range, with a warning
  outside it.

**R-mc12-7 — Export as a cell (D12).** Through `CellCreate`, the function the GUI's New Cell calls:
- a schematic view holding the topology;
- element values as **cell parameters** (defaults are the fitted values), or, for a family, as expressions of the
  geometry parameter;
- a symbol: the auto-generated N-pin box, or one the user chooses.

An existing cell name is refused, never overwritten (`CellCreate`'s own rule). The cell is placed in the library
the user picks.

**R-mc12-8 — Long work.** Through `RunControl` — vector fitting, proposals and fits — cancellable, previous
result kept (D13).

## 3. Not in this phase
The passive window steps (MC-14); the CLI spelling (MC-13).

## 4. Gates (minimal tests, run only these classes)
- `PassiveAnalysisTests` — reciprocity, passivity and the self-resonant frequency of a known RLC block are
  reported correctly; a non-passive block is flagged with its frequencies.
- `TopologyProposalTests` — S generated from each of three known library topologies ranks that topology first
  and recovers its values within tolerance.
- `FamilyFitTests` — five synthetic spiral geometries train and the held-out sixth is within a stated `|ΔS|`.
- `PassiveCellExportTests` — the exported cell, placed and simulated, equals the fitted network (1e-12); an
  existing cell name is refused.
- `VectorFitTests` — recovers the poles of a known rational function to 1e-8.
