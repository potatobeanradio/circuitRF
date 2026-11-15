# Brief series ICF — component ports on IC cells: what a field test on an open SiGe kit left open

**Goal.** Close what the ICP series (component ports on cell instances) and its follow-up fixes left open, once it was run
end to end on a real kit's own PCells: a cascode with two HBTs and a MIM capacitor cut out, a pair of coupled spiral
inductors, and an HBT core matched by them, on Planar and on EMerge.

**Origin.** GitHub issue 69, the reply of 2026-11-15, and PR #70 (merged as `a95799a0f`), which fixed the small, local
items: an unbuildable pin Reference falls back to ground in the shared decision, a cut pin whose conductor reaches nothing
is warned as not connected, the run follows the pre-solve check's Uncalibrated prediction, `_cosim` writes a model's file
relative to the workspace, a DC operating point through an SnP with no 0 Hz row warns, every solver warns on non-passive
rows, and drawn ports carry the same names and a per-port header line on both solvers. The external kit is not named
anywhere in this repository and nothing from it is committed.

## What is left, grouped

| Group | Items | Size |
|---|---|---|
| Loose ends | a Reference that fails its solver's own construction; four planar run notes that say the wrong thing; the header's `mesh:` value; 3D cut-port landings reported as verified; EMerge `--mesh-only`; `plot` over two results; one docs sentence | small, independent |
| Cut instances | a cut instance with no port is not placed in `_cosim`; LVS compares a kit PCell's generated cell against the schematic's kit part | small–medium |
| Multi-finger devices | a pin at more than one face is refused | medium, one owner decision |
| Planar on thick, conducting silicon | the local grid moves with the sweep's top frequency; narrow bands of wrong rows; rows not passive above ~31 GHz | investigation first |
| Planar mesh on small IC cells | board defaults mesh a 37 × 110 µm cell to ~22k unknowns; a MIM via array to ~48k | medium, accuracy-gated |
| 3D on a thick die | cut-port sheets run ~755 µm to the backside; the air box is padded from the metal stack, not the port column | medium |

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-icf-1-loose-ends.md` | The small items, each its own requirement; ships alone | — |
| `brief-icf-2-cut-instance-completeness.md` | `_cosim` places every cut instance; LVS matches a kit PCell's generated cell to its kit part | — |
| `brief-icf-3-multi-finger-devices.md` | One port per finger face, named `Q1.C[1]`; the model put back across them | 2 |
| `brief-icf-4-thick-substrate-accuracy.md` | Measure the three planar effects on a thick lossy substrate, decide, then fix what the measurement supports | 1 (the notes) |
| `brief-icf-5-ic-mesh-defaults.md` | An IC rule in Auto mesh, the detail floor, and a via-array merge for MIM | 4's decision on the local grid |
| `brief-icf-6-3d-thick-die.md` | A stated return for cut ports, and the air box padded from the port column | 1 (the landing notes) |

ICF-1 and ICF-2 can be built in either order and ship on their own. ICF-4 starts with a measurement and may end in a
warning rather than a fix.

## Approach risk, said up front

- **ICF-4 is the uncertain one.** Two of its three causes are hypotheses from the field test (the kernel fit for the bad
  rows; the tall uncalibrated edge port or the TE1 pole for the passivity), not measurements. The brief is written as
  measure → decide → fix, with a stop rule. If neither experiment separates the causes, the deliverable is a warning that
  names the port and the substrate's electrical thickness, not a fix.
- **Moving the local grid's base cuts (ICF-4) changes every existing local-grid result.** `EmSnpProvenance.MeshHash`
  hashes the mesh settings, not the realised grid, so a result written before the change would not read as stale unless
  the hash gains an algorithm term. ICF-4 adds one.
- **An IC rule in Auto mesh (ICF-5) changes results for small cells by design.** Boards stay byte-identical; shipped MMIC
  examples that fall under the rule are re-run and their pages updated, with the |ΔS| recorded.

## Decisions for the owner before the brief that needs them

- **D1 (ICF-3).** A multi-finger pin's model: tie every finger's port to the one model terminal (simple, loses per-finger
  phase), or split the device into N one-finger instances scaled by the model's own finger parameter. The brief
  recommends tying first, with splitting only where the part's model declares a finger-count parameter.
- **D2 (ICF-4).** Whether a confirmed cause that cannot be fixed in the brief ships as a warning on its own.

## Simulation budget, for every brief

- Fixtures are synthetic, built on shipped technologies or a thick-silicon stack written for the test (no kit layout is
  committed). At most 6 frequency points in a routine test; a routine test that solves stays under ~5 s.
- **No Palace in any test or gate.** 3D gates assert the assembled problem. EMerge only by hand, time-capped.
- No new `Category=Benchmark` test and no timing assertion. One test per claim. Run the named test classes only, never all
  of `Ui.Tests`.
- Measurements (ICF-4, ICF-5) run in a scratch harness with a memory watchdog, are recorded in the relevant `RESOLVED.md`
  and are not added as tests.

## Rules that apply throughout

- **No second construction and no second check.** `check`, `explain`, the panel, the layout marks and the run call one
  builder.
- **Existing results do not move** unless the brief says which ones move and why. A setup naming no cell instance produces
  byte-identical output on every path ICF-1, ICF-2, ICF-3 and ICF-6 touch.
- **Diagnostics are returned, never posted** (`src/Design/CLAUDE.md`).
- **The kit is never named**, nor its cells, in code, tests, docs or commit messages.
