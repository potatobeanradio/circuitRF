# Brief MIM-14 — re-measure the full-wave floor on the repaired kernel, and make whatever it buys reachable

**The kernel defect is fixed and the limit that exists because of it has not been re-measured.** That
is the whole of this brief. `PlanarLevels.FullWaveCellOverSeparation` is 40, from MIM-12's ladder,
taken on a kernel MIM-12a then repaired; §MIM-12a's own closing section says so and calls the number
*conservative, not current*. Nobody knows where the engine now reaches, and a refusal standing on an
obsolete measurement costs a user a capability that may already exist.

**It is blocking a real design today.** A spiral inductor and a MIM capacitor in one EM setup — the
structure this whole round started from — is refused before a matrix is filled:

```
Conductor levels 0 and 1 are 0.2 µm apart and the largest cell straddling them is
13.333 µm, i.e. cell/separation = 66.7, past this solve's measured full-wave floor of 40.
```

**66.7 against 40 is 1.7× over a stale number.** The mesh itself is not the problem — 2,982 unknowns,
60 % of the dense ceiling, on three levels (2,123 Metal1 / 506 plate / 269 Metal2 / 84 vertical). The
plates are 68 × 68 µm and 60 × 60 µm with a 0.2 µm film; the 13.333 µm straddling cell is already
finer than `MinCellsAcrossConductor` = 2 asks for, because the drawn plate via and the upper strap
force gridlines through the plate.

## What is already settled, and must not be re-measured

- **The kernel is right across the band.** MIM-8's electrostatic instrument, driven through the run's
  own fit, reads `C/(ε₀εᵣA/d)` = **1.006 at 1, 2, 3 and 10 GHz on two meshes**, where before MIM-12a
  it read −0.54 / 1.34 / 1.60 / 1.00. The cross-level kernel is **1.4e-6** wrong at 1 GHz against
  direct Sommerfeld integration, where it was 2.7e-2.
- **Two independent routes agree.** The fitted path reads 1.006 and `DirectScalarKernel` — the same
  kernel by direct integration rather than by fit — reads 1.015. That agreement is what says the peel
  is complete, and it is the standing cross-check for anything below.
- **It was never the conditioning.** `cond(Z)` got ~35 % WORSE while the answer became right
  (1.443e9 → 1.96e9). Do not reopen iterative refinement, loop-star or A-EFIE.
- **The mesh knobs do not act on the ratio in the way a reader expects** — but every one of those
  measurements was taken on the BROKEN kernel and is suspect for exactly the reason this brief
  exists. See M3.

## Why M4 stalled, which is the actual problem to solve

MIM-12a did not re-run the ladder, and the reason is on the record: **the replacement fixture measures
its own ports, not the film.** Two overlapping 80 × 80 µm plates with edge ports on the two levels,
de-embedded, read a series capacitance from `−1/Y₂₁` that is

- **6–10× below the electrostatic mutual capacitance of the same mesh**, and
- low at **d = 20 µm, cell/separation 2** — a regime with no thin film in it at all and one this floor
  already admits, and
- **halving with every mesh refinement** (0.0068 → 0.0045 → 0.0027 → 0.0014 pF over four meshes while
  the electrostatic value holds at 0.026–0.028 pF), while
- a plain single-level through line on the same port setup comes back **8 % non-passive**.

A fixture that cannot read a capacitance where the answer is known cannot grade one where it is not.
**So M1 is the fixture, and it is most of this brief.** The ladder itself is an afternoon once the
fixture is trustworthy.

## What to build

### M1 — a de-embedded two-port fixture that measures the STRUCTURE

**Three controls, all containing no film, and all must pass before the fixture grades anything:**

1. A plain through line on the same port setup is **passive** (σ_max ≤ 1) and returns its own Z₀ and
   electrical length. Today it is 8 % non-passive, which alone invalidates every rung.
2. At **d = 20 µm, cell/separation 2**, the series element read from `−1/Y₂₁` matches the
   ELECTROSTATIC mutual capacitance of the SAME mesh to **within 10 %**. Not against ε₀εᵣA/d — against
   the electrostatic instrument on the same artwork, so plate fringing is common to both and cancels.
3. The reading **stops moving with refinement**: three meshes, the spread under 5 %. A quantity that
   halves each time is a port artefact whatever its value.

**The most likely cause, and check it first.** The failing fixture puts one edge port on each level. A
de-embedded edge port above the lowest analysis level is precisely what MIM-4 has not built
(`PlanarSolve`'s `LevelIsOnSlabTop` gate, and the interior-height static Green's function behind it).
**Feed both ports on the LOWEST level instead** — which is what the shipped capacitor cell already
does, routing its top plate up a via, across the upper metal and back down to a landing pad on the
bottom level, so both terminals abut the same metal. That topology is the real one, it is what a user
draws, and it needs nothing MIM-4 has not shipped.

**If the three controls cannot be made to hold, STOP and report.** That is a finding about ports and
de-embedding, not about the film, and it is larger than this brief. Say which control failed, by how
much, and on what geometry.

### M2 — re-run the ladder

MIM-12's own design, unchanged so the rows are comparable: **one plate pair, one set of feeds, one set
of ports, the straddling cell PINNED, and only the film thickness moving**, so cell/separation is the
only axis. Rungs at **20, 40, 80, 200, and 67** — the last because it is where the blocked design sits
and a floor that lands just under it is worth knowing about precisely.

At every rung report, at 1, 2 and 3 GHz:

- `C/(ε₀εᵣA/d)` from `−1/Y₂₁`, **and** the electrostatic value on the same mesh, and their ratio;
- σ_max(S);
- the de-embed's own counters (`DeembedResidual`, `DeembedErrorFloor`, `CalibrationUsable`,
  `DeembedRejected`) — MIM-9 made the attribution follow them and this is the measurement that says
  whether they are telling the truth.

**The gate for "this rung is inside the floor":** the series element within **10 %** of the
electrostatic value on the same mesh, and σ_max ≤ 1, at all three frequencies. The floor is the
largest rung that passes, rounded DOWN to the rung, never interpolated.

**Every table to `HISTORY.md` §MIM-14, beside MIM-12's.** Keep MIM-12's rows; they are the before.

### M3 — re-point the constant, and re-point the sentences

**One constant moves.** `PlanarLevels.FullWaveCellOverSeparation`, with its doc comment re-pointed at
the new ladder and MIM-12's kept beside it as the before. **Leave the refusal standing** — MIM-9's
instruction, and it has now paid for itself twice.

Four run-visible sentences quote the old ladder and currently end by saying it predates the repair;
they take the new table (`PlanarSolve.LevelSeparationVerdict`, the three arms of
`LevelSeparationNotes`, and `NonPassivityCause`'s first arm). **The "what does NOT act" clause is the
one to be careful with**: its numbers (Cells per wavelength 20 → 400 leaves σ_max at 1.0443; 2 → 4 → 8
cells across moves it 1.7322 → 1.7491) were measured on the broken kernel. **Re-measure them on the
repaired one or delete them.** Naming an inert knob is what MIM-9 existed to stop; quoting a stale
measurement of inertness is the same defect one turn on.

### M4 — make the headroom reachable, and which half of this you build depends on M2's number

**Case A — the new floor is at or above ~70.** Nothing more to build. The blocked design runs; M5 is
the acceptance and the brief ends there.

**Case B — the new floor lands between 40 and 67.** The user now needs to mesh the plates finer
without refining everything else, and **there is no way to do that today.** `MinCellsAcrossConductor`
is global: taking it from 2 to 8 to put ~7.5 µm cells on a 60 µm plate also puts ~1.25 µm cells on the
spiral's 10 µm track. Build the smallest thing that works:

- **A per-LEVEL `MinCellsAcrossConductor` override**, on the EM setup beside the analysis-level list —
  the plate level at 8 while the interconnect stays at 2. Per level and not per shape: a level is
  already the unit this panel talks in, and the refusal itself is stated per adjacent level PAIR.
- **The refusal names the cell size that would clear it**, in the document's own display unit, and the
  control that reaches it — *"cells of 8 µm or finer over 'MIM Metal' and 'Metal1' would clear this;
  the largest there is 13.333 µm"*. Only where the mesh can actually reach it. R-prt-13: report the
  number, and name a knob only when it binds.
- **Measure what that refinement costs in unknowns on the blocked design** before claiming it is a
  remedy. A multi-level mesh is judged against the DENSE 5,000 ceiling whether or not the accelerator
  is on (MIM-13 left widening that an open owner decision, deliberately). **If the refinement M4 asks
  for pushes the acceptance structure past 5,000, say so and stop** — trading one refusal for another
  silently is the outcome this round exists to prevent, and "the accelerated ceiling for multi-level
  meshes" is then the next brief rather than a paragraph in this one.

**Case C — the ladder says the floor should not move, or should come DOWN.** A real outcome and not a
failure. Write it up with the rows, leave the constant alone, and MIM-10's composition route stays the
answer for this structure. Do not adjust a fixture to reach a number.

### M5 — the acceptance is the structure that was refused

A spiral and a capacitor in **one** EM run, gated against the composition route rather than against a
remembered frequency: MIM-10's own measured answer on the same coil and the same 1.0838 pF is a series
resonance at **2.750 GHz, |S21| −0.083 dB, |S11| −29.6 dB**, stable to three decimals on two meshes and
at 7 or 23 EM points. The all-EM run must land on that: **peak within 2 % in frequency, |S21| within
0.3 dB, |S11| below −20 dB, and passive at every solved point.**

The example workspace's `SpiralInductor.clay` is this layout **with the capacitor instance removed** —
that is MIM-10's fixture and it must stay as it is. This needs the resonator WITH the capacitor, as a
second example layout beside it, generic (no path, no name, no process identity) and
`[Trait("Category", "Benchmark")]`.

## Traps, each already paid for once in this series

- **Never gate a full-wave capability on an electrostatic measurement** (this round's own added
  convention). The electrostatic instrument is the CONTROL here — the thing the de-embedded reading is
  compared against — never the thing that says the two-port is right.
- **A gate that constructs its own kernel stops measuring what the application does.** Every
  measurement in this brief goes through `Dcim.ForStackAtFrequency`, which is what a run applies.
  MIM-8's fixture did not and read 1.003 where the run read −0.54.
- **Never read a small element's value off raw S.** A raw edge port's own ~0.3 fF discontinuity masks
  the capacitor behind it; MIM-2's retracted finding 2 is that mistake.
- **`DcimModel.FitResidual` is blind to this whole class of error.** Direct Sommerfeld integration is
  the kernel oracle; the electrostatic instrument is the capacitance oracle.
- **Do not tune `PathExtent`.** The window in which the capacitance looked right was
  frequency-dependent and about 2× wide; there is no rule in it.
- **One known residual, and measure whether it binds rather than assuming.** MIM-12a's finding 6
  declines a face with a thin region on both sides, and that decline is reachable on the shipped stack:
  the UPPER plate sits at the bottom of a 2.8 µm region with the 0.2 µm film below it, so its
  same-level kernel is not peeled. Measured at 1 GHz over the cell's own ρ range, the upper plate's
  self-pairing is **4.5e-3** out where the lower plate's is **5.2e-7**. It does not move the
  electrostatic ladder, and the fitted-versus-integrated agreement bounds its total contribution at
  ~1 %. If a rung fails at a ratio the kernel comparison says should pass, this is the first suspect.
- **Keep the EM runs short.** The ladder is five rungs at three frequencies, not a 51-point 0–8 GHz
  sweep; probe the engine from a scratch harness and save a full `.cem` run for M5.

## Not in this brief

- **MIM-4's interior-height static Green's function.** If M1 shows the fixture needs a de-embedded port
  above the lowest level, do not build it — re-topologise the fixture as M1 says and record that MIM-4
  is what the other topology waits on.
- **Widening the accelerated unknown ceiling for multi-level meshes.** Named as a dependency in Case B,
  scoped there, and built nowhere here.
- **Making the film laterally finite.** MIM-11 settled that it cannot be done in this kernel.
- **Removing the refusal.** The floor moves or it does not; it does not disappear.

## Conventions

`brief-em-mim-series.md` §Conventions binds this brief. Write-ups to `src/Engine/Mom/RESOLVED.md`
§MIM-14 and every measured table to `HISTORY.md` §MIM-14; **nothing to any `CLAUDE.md`**. Correct in
place any sentence this work makes false — and there are at least five that currently say the ladder
predates the repair and has not been re-run.
