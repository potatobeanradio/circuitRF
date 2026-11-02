# Brief EMI-6 — Compare: run any set of installed solvers on one problem

**Tag:** `R-emi6-n` · **Series:** EMI (see `brief-emi-0-overview.md`) · **Depends on:** EMI-1, EMI-5
**Area:** `Em3dSolverChoice` (EMI-1), `Em3dRunService` (the generalised RunBoth), `src/Engine/Em3d/Em3dComparison.cs`,
`EmSetupPersistence.cs`, `src/Ui/Layout/Em/EmSetupEditorViewModel*.cs` + `EmSetupEditorView.axaml`, the 3D editor's
Simulate and solve status, `src/Cli` (`em`, `explain`), the MCP `run` tool, `docs/user/src/reference/em-setup.md`.
Findings in `src/Design/RESOLVED.md`.

---

## 0. Why

Today a setup runs Palace, openEMS, or **Both** (Palace then openEMS, then a comparison). With three solvers a
fixed pair stops making sense: the user should pick which installed solvers to compare. The owner asked for exactly
that, with uninstalled solvers shown greyed out. It also makes a new solver's integration testable by comparison from
the day it lands.

## 1. `R-emi6-1` — the choice and the format

- **The setup's Solver** is one of the registry's solvers, or **Compare**.
- **Compare** holds the chosen solvers (two or more, in registry order) and a **Reference** among them (O4: default
  Palace when chosen, else the first chosen).
- **Format, additive:** `"Solver3D": "Compare"` with `"Compare": { "Solvers": ["Palace", "EMerge"], "Reference":
  "Palace" }`. `"Both"` keeps reading as Compare of Palace and openEMS with Palace as reference, and **a file saying
  `Both` is written back as `Both`** while its set is still exactly that pair — no churn in existing files or examples.
  No `FormatVersion` bump.

## 2. `R-emi6-2` — the setup UI

In the EM Setup panel, the Solver combo box lists every registry solver and then **Compare…**. Choosing Compare shows
one checkbox per registry solver and a Reference combo box over the checked ones.

- **A solver that is not installed** (EMI-2's discovery says not found) is **disabled** in both the combo box and the
  checkbox list, with a tooltip shown on the disabled item: "Not installed. Install it from Settings ▸ Solvers."
- **A solver that cannot solve this setup** (its descriptor's `Refusal` is non-null: an eigenmode problem on openEMS, a
  wave port on EMerge before EMW) is disabled the same way, its tooltip the refusal's first sentence.
- No note text under the controls (the owner's standing preference); tooltips only.
- With fewer than two runnable solvers checked, Run is disabled and its tooltip says why.
- The per-solver settings sections (Palace, openEMS, EMerge) show for every solver the choice runs, as `IsPalaceSetup`
  /`IsOpenEmsSetup` do today, driven by the choice's solver list rather than enum matches.
- Installation state is read when the panel opens and when Settings ▸ Solvers closes; no polling.

## 3. `R-emi6-3` — the run

Each chosen solver runs in registry order, one after another (each uses every core: the existing note, generalised).
Each leg writes its own Touchstone and `.npy` with its token; a failed leg does not stop the others (today's Both
behaviour, generalised: "openEMS failed: …; Palace and EMerge completed"). Cancellation keeps completed legs, as today.

## 4. `R-emi6-4` — the comparison

**R-emi6-4a — every other solver against the reference**, on the same frequency grid (the existing rule: no
interpolation, a grid mismatch is a refusal). `Em3dComparison.Compare(reference, other, problem, facts)` takes the facts
each solver's descriptor supplies (EMI-1's `ComparisonFacts`): openEMS's (dielectric fit frequency, solids written as
PEC, sub-cell wires, unconverged ports) as today, EMerge's (its lumped-port model, surface-impedance walls, the
adaptive sweep's reached error), Palace's (none today). The notes stay fact-driven: a note appears only when this run's
facts make it true.

**R-emi6-4b — the file.** One comparison `.npy` per run. **For Palace + openEMS with Palace as reference it is byte-
identical to today's** (cube names `S_palace`, `S_openems`, `dMag_dB`, …), so every existing reader, `.cdd` and test
keeps working. For any other set, each solver's `S_<token>` and each pair's difference cubes with the other solver's
token appended. The summary line (the one a user reads first) names the worst pair.

**R-emi6-4c — the Data Display.** A compare run's traces overlay per solver, as a Both run's do today.

## 5. `R-emi6-5` — CLI and MCP

`em --solver palace,emerge [--reference palace]`, `--solver compare` (the setup's own set), and `both` kept as an
alias for `palace,openems`. An uninstalled or refusing solver in the list is a refusal naming it and why, before
anything runs. `explain` reports the choice, the reference, and each solver's runnable/refused state. MCP `run` takes
the same.

## 6. Tests (targeted)

1. Format: `Both` round-trips as `Both`; the Compare block round-trips; an unknown solver name is refused listing the
   registry.
2. Comparison: Palace + openEMS → the existing golden, byte for byte; three canned S cubes → the pairwise cubes and the
   summary naming the worst pair.
3. Fact-driven notes: an EMerge leg's facts produce its notes; absent facts produce none.
4. View model: an uninstalled solver is disabled with its tooltip; an eigenmode setup disables openEMS with the refusal
   sentence; fewer than two runnable checked disables Run.
5. CLI parse: lists, `both`, `--reference`, and the refusals.

## 7. Docs

`em-setup.md`: a **Compare** section (what it runs, the reference, where results land, how to read the comparison)
replacing the Both description. `cli.md`: `--solver` lists and `--reference`.
