# Brief EMDC-2 — The DC row in every 3D result

**Tag:** `R-emdc2-n` · **Series:** EMDC (read `brief-emdc-0-overview.md` first) · **Depends on:** EMDC-1
(`Em3dDcSolve.Solve`, `Em3dDcResult`)
**Area:** `src/Design/Layout/Em3d/Em3dGenerator.cs` and `src/Design/ThreeD/C3dProblemAssembly.cs` (the 0 Hz note and a
flag), `src/Design/Em3d/Em3dRunService.cs` (one DC solve per run), one new shared splice
(`src/Design/Em3d/Em3dDcRow.cs`), each solver's results path (`Solvers/Palace`, `Solvers/OpenEms`, `Solvers/EMerge`),
Compare, `explain`, docs. Findings in `src/Design/RESOLVED.md`.
**Out of scope:** circuit-side warnings (overview §5), and any change to a sweep that does not contain 0 Hz. Every
existing 3D golden and replay must stay byte-identical, and the test list holds that.

---

## 1. `R-emdc2-1` — the problem says it was asked for DC

`Em3dGenerator.Go` and `C3dProblemAssembly.Assemble` already detect a 0 Hz point (`positive.Length < freqs.Length`).
Carry it as `Em3dGenerationResult.WantsDc` (init, default false), not on `Em3dProblem`, so no lowering and no
provenance hash can see it. **Replace the note** in both places, which said a full-wave solver has no DC answer, with
one sentence: the sweep's 0 Hz point is answered by circuitRF's conduction solve of the conductors, not by the 3D
solver. Both routes say it in the same words. Factor the sentence into one constant rather than two literals.

## 2. `R-emdc2-2` — one DC solve per run, before the legs

In `Em3dRunService.Run`, after the problem is built and before any leg executes: when `WantsDc`, call
`Em3dDcSolve.Solve` once, as its own progress stage ("DC conduction solve"), honouring cancellation, and hand the
`Em3dDcResult` to every leg through the run context the legs already share. **A Compare run solves DC once**, never
once per solver. Its notes join the run's notes. When it returns a `Refusal` (overview O2), the run goes on. The
refusal sentence goes to the run's **Warnings**, with the effect stated: this result has no 0 Hz row, so a circuit's
DC solve through it uses the lowest frequency. Where the refusal is Gmsh missing, attach the same install offer a
Palace or thermal run gets (`SolverInstaller.OfferFor`).

## 3. `R-emdc2-3` — one splice, called by every solver

`Em3dDcRow.Prepend(DataSet data, string group, Em3dDcResult dc)` inserts f = 0 at the front of the group's frequency
axis.

- **S** gets the DC matrix.
- **Every other cube on that axis in that group** gets NaN at 0 Hz: none of them has a DC meaning. Cubes in other
  groups (diagnostics, mode impedances) are not touched.
- The axis must stay strictly increasing. A group whose first frequency is already ≤ 0 is a defect: throw, naming the
  group.

**Each solver calls it in exactly one place**: after its DataSet is complete and **before both `WriteNpy` and
`WriteSnp`**, so the `.npy` and the `.sNp` carry the same rows. That place is Palace's `Em3dRunService.Palace.cs`
around `WriteNpy`/`WriteSnp`, openEMS's `Em3dRunService.OpenEms.cs`, and EMerge's `Em3dRunService.Emerge.cs` before
`EmergeResults.WriteSnp`. A leg that writes no `.sNp` (eigenmode, static) never reaches it. **The reference:** the DC S
must be referred to exactly what that group's S rows are referred to. Read what each solver's S cube is in before
splicing; the Touchstone header carries one R. If the solvers differ, convert the DC row with the group's own
renormalisation rather than writing a second rule. Record what you found in RESOLVED.

## 4. `R-emdc2-4` — what the file says about its DC row

Add a header line through each solver's existing `lines` list, built by one shared function so the three solvers say
it in one form:

```
! circuitRF-EM 3D DC row: circuitRF conduction solve of the conductors, 4,812 elements, 0.42 s; ports as terminal pairs
```

Also add **one line per lumped element** stating what it became at DC (resistor 100 Ohm / short / open). When the sweep
asked for DC and the solve refused, add instead `! circuitRF-EM 3D DC row: none — <the refusal>`. A sweep with no 0 Hz
point adds no line at all, which is what keeps the goldens byte-identical.

## 5. `R-emdc2-5` — Compare, `explain`, the run summary

- **Compare** (overview O3): its agreement statistics between solvers skip 0 Hz, and its report says once that the DC
  row is circuitRF's own and identical for every solver.
- **`explain --analysis` on a 3D setup** whose sweep contains 0 Hz says how 0 Hz will be answered, and whether Gmsh was
  found for it. Use the same constant as §1.
- **The Simulate row's summary** (`WorkspaceViewModel.EmRunSummary`) counts the DC point among the points solved, as
  it already does for planar.

## 6. Tests (targeted; one per claim)

1. **The splice** (`Em3dDcRowTests`, no solver): a group with S and a second cube on 1–10 GHz gains f = 0 with the DC
   S and NaN in the second cube. Another group is untouched. A group already starting at 0 throws.
2. **No 0 Hz, no change:** each solver's existing replay or golden test passes with no expected value touched. Name
   the classes you ran in RESOLVED.
3. **One solve per Compare run:** a two-solver run (a fake leg is fine if the suite already has one; otherwise
   `[PalaceAndEmergeFact]`) calls `Em3dDcSolve` once and both files carry identical 0 Hz rows.
4. **Each solver end to end**, one `[PalaceFact]`, one `[OpenEmsFact]` and one `[EmergeFact]`: EMDC-1's gap-capacitor
   problem on a sweep starting at 0 Hz. Each `.sNp` begins with a 0 Hz row with |S₂₁| < 1e-12 and carries the §4 line,
   and its `.npy` carries the same row. Tag any that runs ≥ 5 s `Category=Benchmark`, as the repo's rule says.
5. **The defect, closed** (the circuit the overview measured): `circuitrf dc` on a 5 V source, the solved gap
   capacitor's `.s2p` and a 50 Ω load puts the load at 0 V (it read 5 V before this series). Commit the small `.s2p`
   produced by test 4 under `testdata/em3d/dc-row/` so this half runs in the routine gate with no solver. The
   production run is test 4's.
6. **Gmsh missing:** with discovery stubbed to not find Gmsh, a run whose sweep holds 0 Hz succeeds, its Warnings carry
   the refusal and the install offer, and the file's header says `DC row: none`.

## 7. Docs (sources only; the owner regenerates at the series' end)

- `docs/user/src/reference/em-setup.md` § Solver — planar or 3D: a short **The DC point** paragraph.
  - Start the sweep at 0 Hz and the file's first row is a DC conduction solve of the conductors, the same rule as
    planar.
  - What it models: conductor resistance, PEC as a short, lumped R/L/C. What it does not: dielectric leakage.
  - It needs Gmsh, also on an openEMS-only install.
  - Without it, a circuit's DC solve through the file uses the lowest frequency.
- `docs/user/src/reference/em-solvers.md`: one row in the capability table, "0 Hz point: circuitRF conduction solve",
  identical for all three solvers.
- `docs/design/em-3d.md`: one paragraph pointing at `Em3dDcSolve` and the port model in EMDC-0 §3.
