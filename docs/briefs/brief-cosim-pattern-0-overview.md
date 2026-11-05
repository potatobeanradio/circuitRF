# Brief CP-0 — EM results as schematic blocks, and the radiation pattern from a schematic: overview

**Series:** CP-0 … CP-7 · **Tag:** `R-cp<n>-<m>` · **Design note:** a new section of `docs/design/sar.md` beside §11
**Area:** `src/Design/Em3d/`, `src/Design/Layout/Em/`, `src/Design/Schematic/`, `src/Design/Circuit/`, `src/Core/Devices/`,
`src/Engine/ParametricSweepEngine.cs`, `src/Core/Expressions/`, `src/Cli/`, `src/Ui/`, `examples/SAR MRI Loop Coil/`,
`docs/user/src/reference/{cosimulation,antennas}.md`

## 0. Why

A 3D run with component ports gives an N-port, and a schematic can put the parts back, so S-parameters follow every part value
with no new EM run. The radiation pattern does not. It is stored per port, and nothing combines those patterns for a particular
set of parts. A field test on an antenna board with a shunt inductor and a series capacitor had a pattern for each port excited
alone and none for the antenna as built. That board's run is estimated at about 25 GB, so one run per value is not a usable way
to tune.

The `fields` analysis (SAR-9, SAR-10) already solves this for B1 and SAR, but in a way this series also changes:
- The block is an ordinary SnP, whose symbol does not say it stands for anything beyond a Touchstone file.
- The fields are reached through a comment line in that Touchstone file.
- A run's settings are not kept with its result, so a result outlives the knowledge of how it was made.
- Starting a new run or writing its field set can remove what the last good run left.

## 1. What the user does

1. Tick the parts in **Component ports**, turn on **Radiation pattern**, and run the 3D setup once. The run writes its results and a
   **result record** (`.cemr`) that holds the settings it used.
2. Place an **EM Field Data** block in a schematic and pick a result record. Its pins are named after the run's ports. Wire any part from a
   port's node to ground.
3. Add a **Fields** analysis naming the block, the frequency and the driving circuit port. Add an S-parameter analysis too for a
   denser sweep; any other component, Term or analysis in the schematic runs and plots alongside it.
4. Plot `F1.farfield.U` and `F1.S` in the **Data Display**, on the plots antennas use today.
5. Change values with a **tuning slider**, a **parametric sweep**, `--set`, the **optimizer** or **yield**. None starts an EM run.

The result is what the block uses. Editing the setup afterwards, not re-running it, or a re-run that fails changes nothing the
block reads.

## 2. Decisions

- **D1 — Part values live in the schematic and nowhere else.** No value list beside the plot and no load field in the setup.
- **D2 — A new EM Field Data component**, not the SnP with a field reference. Fields runs only on it. It holds field data or it is
  refused, so the name is always true. Plain SnP stays what it is: a Touchstone file, measured or from a run with no fields.
- **D3 — The Fields analysis stays.** It asks for work an S-parameter run should not pay for unasked, and carries settings an
  S-parameter analysis has no meaning for (field frequency, multi-channel drive and weights, norm, cube averaging). It also
  publishes the circuit's S at its own frequencies, so `F1.S` lines up with the fields it gives.
- **D4 — The block works on saved data, never on the setup.** It points at a result record on disk and reads no `.c3d` or `.cem`
  to simulate. The current setup is compared only to say that it differs, and that never blocks anything.
- **D5 — A result records how it was made**, complete enough to read back after the setup has changed.
- **D6 — A run that does not finish changes no result.** A run that finishes overwrites the result of the same name, as a planar run
  does. The setup's **Output name** decides that name for every file of the result, so a new name is how an earlier result is kept. A
  copied result keeps working.
- **D7 — A pattern exists only at frequencies the run solved.** Far-field phase across a coarse sweep is not safe to interpolate.
  A frequency between points is refused, listing the points.
- **D8 — Loaded ports re-solved inside Palace are out of scope**, as are lumped elements on Palace.

## 3. The arithmetic

For a 3D run the stored `E^V_k` is the field with 1 V across port k and every other port terminated in its Z0. With the circuit's
waves at the block, `a_k = (V_k + Z0_k·I_k) / (2·√Z0_k)` (`FieldCosimulation.Weigh`), the field of the whole circuit is

```
E = Σ_k  a_k · √Z0_k · (1 + S_kk) · E^V_k
```

S and Z0 come from the result. E is then divided by the circuit drive port's total voltage, so the published cubes keep their
meaning (1 V at the driven port), and `Em3dRadiation.Publish` forms every metric from them as it does for a 3D run. A planar run
stores a different excitation; CP-7 covers it.

## 4. Risks, stated before the work starts

- **The approach is proven on near fields, not yet on far fields.** The loop coil co-simulation agrees with a lumped run to within
  0.3 % in B1. CP-3's physical gates extend that claim. CP-1 and CP-2 are worth having even if those gates fail, since they fix
  SAR's block and its saved data, so they may safely come first.
- **Polarity.** A port's S and its pattern must share one sign. An error here is invisible on the port's own pattern and shows only
  when two ports add, so the gates need two ports contributing strongly.
- **High-Q loads.** A low-loss part near resonance magnifies small errors in S and in the first-order absorbing faces. Draft quality
  may not be enough. CP-3 measures this.
- **What a gap port is.** The N-port has one common reference, but a gap port's negative end is a pad, not ground. A part from a gap
  port's node to ground is right. A part between two gap ports' nodes is in series with the port references and is not the board.
  A shunt branch inside an `.s2p` stands across the gap and does not reach the real ground plane. CP-6 warns on the first; the docs
  state the second.
- **The part's body is not in the 3D model**, with this method or any other that uses a gap port.

## 5. Briefs

| Brief | Delivers | Depends on |
|---|---|---|
| CP-1 | The result record; a run that does not finish changes no result | — |
| CP-2 | The EM Field Data block; Fields requires it; the SAR example and test benches moved | CP-1 |
| CP-3 | Fields gives a far field and the circuit's S, with the physical gates | CP-2 |
| CP-4 | Tuning sliders, optimizer and yield on the pattern | CP-3 |
| CP-5 | A parametric sweep wraps it, so values overlay on one plot | CP-3 |
| CP-6 | The run makes the schematic; analysis editor; docs | CP-3 |
| CP-7 | Planar results as EM Field Data blocks | CP-3 |

Field-test boards stay outside the repo. No board, result or board-specific name is committed.
