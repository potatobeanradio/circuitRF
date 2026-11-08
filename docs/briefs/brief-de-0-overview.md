# Brief series DE — the Deembed component, TRL calibration, and the "TRL and De-embedding" example

**Goal.** A user with TRL measurements (Thru, Reflect, Line, each a `.s2p` measured at the instrument's plane) can get the two
fixture files from them, place a **Deembed** block on each side of a measured part, and read the part at its own plane. The
Deembed block is the SnP block's opposite: it holds a Touchstone file of any even port count, and instead of adding that
network to the circuit it takes it away. It goes anywhere in any schematic and works in every analysis an SnP works in.

**Why a series.** circuitRF can place a measured network but has no way to take one away, and nothing turns a set of
calibration standards into fixture files. De-embedding today means another tool. The engine already does TRL-style
calibration internally for its planar ports, but none of that reaches a schematic.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-de-1-network-math.md` | The 2K-port inverse and the 2-port TRL solver, in `src/RfCore`, as pure functions with tests | — |
| `brief-de-2-deembed-model.md` | The `Deembed` netlist type and its model: DC, S-parameters, HB, loadpull, sweeps; the cancellation gate | 1 |
| `brief-de-3-schematic-editor.md` | Palette entry, symbol, parameter editor, docs and MCP reference for the component | 2 |
| `brief-de-4-trl-analysis.md` | A `trl` test-bench analysis that writes fixture A and B `.s2p` files, in the GUI, the CLI and MCP | 1 |
| `brief-de-5-example-workspace.md` | The "TRL and De-embedding" example: README with the equations, a TRL bench, a 2-port de-embed bench, a 6-port FET de-embed bench | 2, 3, 4 |

## Decisions already made

- **D1 — `Deembed(F)` is the network `F⁻¹`, wired exactly as an SnP on `F` would be wired.** Ports 1…K are one side, K+1…2K
  the other, as in the file. The cascade of `F` and `F⁻¹` is a through connection, in either order. So the rule for a user is:
  *draw the chain that would ADD the fixture, then swap the SnP for a Deembed and the part for the measurement.*
  `Port1 — SnP(A) — DUT` gives what the instrument measured; `Port1 — Deembed(A) — SnP(measured)` gives the DUT back.
  For a fixture on the output side, `SnP(measured) — Deembed(B) — Port2`.
- **D2 — An SnP and a Deembed on the same Touchstone file cancel to numerical noise.** This is the series' gate (§ Gates). It
  holds at every frequency, not just the file's own points. That requires one thing: the Deembed inverts **the same
  interpolated S the SnP would stamp** at that frequency, through the same `TouchstoneCache` interpolator and the same
  `InterpMode`/`InterpDomain`/`ExtrapMode` settings, and never a separately fitted or resampled copy. Below or above the file's
  band both clamp (or extrapolate) the same way, so they still cancel. At 0 Hz the same holds.
- **D3 — The port count is even.** A Deembed needs a matching outer group and inner group. `NumPorts` comes from the file as
  an SnP's does. An odd count is refused when it is read, with a sentence naming the instance and the count.
- **D4 — One model class.** The `Deembed` netlist type builds an `SnpModel` with `Inverse = true`, the precedent EM Field Data
  set (`ComponentModelFactory.CreateEmFieldDataModel`). Every engine site that reads a block's port branches through
  `is SnpModel` (DC, HB, S-parameters, the SDD port validators) then works without edits. Every site that reads the
  block's **file** instead of its stamp must apply the inverse or refuse it by name. Brief 2 lists those sites.
- **D5 — Works in every analysis that stamps a linear block per frequency.** That covers DC (stamped at ω = 0), S-parameters,
  HB at every harmonic, loadpull and pursuit, parametric sweeps, optimization and yield. **Envelope** is the exception to
  handle on purpose. The inverse of a delay is a negative delay, which no causal rational fit can represent. Brief 2 decides
  between the envelope's existing tangent fallback, if it says so, and a refusal that names the instance. A silent fit is
  not an option. **Fields**: a Deembed is never an EM Field Data block, so `fields block=` keeps refusing one. A Deembed
  elsewhere in a fields circuit just stamps, like any other linear block.
- **D6 — A Deembed is active.** The inverse of a lossy network has gain (σ_max(F⁻¹) > 1). `ActivityFor` returns `BlackBox`
  always, and NDF refuses it with a sentence saying it is a de-embedding block, the inverse of a passive network.
- **D7 — No S-parameter entry offsets on a Deembed.** The `SdB_i_j` / `Sdeg_i_j` keys are refused, and the sensitivity,
  yield and tunable catalogues do not list a Deembed's entries. Perturbing a fixture before it is inverted is not something
  a user can reason about.
- **D8 — TRL is a test-bench analysis, `type=trl`.** It reads three SnP instances on its own bench by name (`thru=`,
  `reflect=`, `line=`), solves at the Thru file's frequencies, writes two `.s2p` files and returns a `DataSet`. Analyses attach
  to a TestBench, and a bench with the three standards on it is a schematic the user can duplicate and point at their own
  files. Its output paths resolve exactly as an SnP's `File` does, so the string in `fixtureA=` can be pasted into a
  Deembed's `File` unchanged.
- **D9 — Fixture orientation.** Fixture A's port 1 is the instrument side and port 2 the DUT side. Fixture B's port 1 is the
  DUT side and port 2 the instrument side. This is the order a measurement chain reads left to right, so
  `Port1 — Deembed(A) — measured — Deembed(B) — Port2` needs no flipping.
- **D10 — The word is "Deembed"** in the palette, the netlist type, messages and the docs. The palette search also finds
  "de-embed", "deembedding", "inverse", "fixture removal" and "TRL".

## Measured before this series was written

A numpy prototype, run outside the repo, of exactly the formulas brief 1 specifies:

| Claim | Result |
|---|---|
| The 2K-port inverse cancels, `D·F` and `F·D` against a through connection, random complex networks | max error 1.6e-15 (K=1), 8.4e-15 (K=2), 3.2e-14 (K=3, 6-port), 4.0e-14 (K=4) |
| The inverse has gain | σ_max(F⁻¹) 2.2 … 51 on those networks, so D6 is real |
| TRL recovers a DUT through two different, lossy, mismatched fixtures with a non-ideal short (Γ = −0.98∠, 3 ps offset), 0.5-20 GHz, 40 points | DUT error ≤ 7e-15 at every point, including the line's 180° and 360° crossings |
| TRL recovers the fixtures themselves | A and B to ≤ 5e-15, **only with continuity tracking** (below) |
| Without tracking | The DUT is still exact, but the fixtures' S21 flips sign from point to point, and above 180° of line the eigenvalue assignment breaks (DUT error 1.1) |

The DUT does not depend on the sign of the fixtures' S21 square root, only on products of it. A fixture **file** does,
because the Deembed interpolates it. A file whose S21 flips sign between two samples interpolates through zero. That is
why brief 1's continuity rules are requirements and not refinements.

## Gates, for the whole series

1. **Cancellation (D2).** `Port — SnP(f) — Deembed(f) — Port` and `Port — Deembed(f) — SnP(f) — Port` equal a through
   connection with max |ΔS| ≤ 1e-12. Check at the file's points, at points between them, below and above its band, for a
   2-, 4- and 6-port file. Put a part X behind the pair, and the DC node voltages and an HB spectrum match X alone to the
   engines' own tolerance.
2. **TRL round trip.** Synthetic standards and a synthetic measured DUT, made by circuitRF from known fixtures, give back the
   known fixtures and the known DUT. Feed the TRL output through the Deembed path, not the math alone.
3. **The example runs**, headless, with no display, in a few seconds per bench.

## Rules that apply throughout

- **One inverse, one TRL.** The Deembed stamp, the TRL analysis and any check or explain path call the `src/RfCore` functions
  from brief 1. Nothing re-derives the algebra.
- **Existing results do not move.** A design with no Deembed and no `trl` analysis produces byte-identical output on every
  path this series touches. `SnpModel` with `Inverse = false` stamps exactly as before.
- **A singular point is a refusal, not a NaN.** Where a fixture's transmission block cannot be inverted (a fixture with a DC
  block, at 0 Hz), the run refuses, naming the instance and the frequency.
- **Tests:** one per claim, named classes only. Never all of `Ui.Tests` and never the full suite. No new `Category=Benchmark`
  test and no timing assertion. Each analysis in gate 1 runs in milliseconds on lumped parts.
- **No citations** of papers, authors or other tools, in the code or the docs. The equations are written out in our own
  words. No commercial tool or PDK names anywhere.
- UI work is checked visually with `tools/UiSnapshot`, and the PNGs looked at.
