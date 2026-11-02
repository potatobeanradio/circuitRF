# Brief 30 — Model Composer: kit data, fit robustness, Verilog-A parity, a swept `dc`

**Series:** Model Composer · **Tag:** `R-mc30-n` · **Phase:** build
**Area:** `src/Design/Modeling/` (`CurrentFit`, `ChargeFit`, `DataImport`), `src/Engine/Modeling/Regrid.cs`,
`src/Ui/ModelComposer/` and `src/Ui/Views/ModelComposer/` (the Data and Current steps), `src/Cli/CliEntry.cs` (`RunDc`
only), `src/Cli/Model.cs` (refusals only), the Verilog-A device path (`src/Engine` external device, `tools/osdi-worker`),
`docs/user/src/reference/model-composer.md`, `docs/user/src/app-notes/an04-model-from-a-reference-kit.md`,
`docs/user/src/reference/cli.md`, `docs/design/cli.md`
**Depends on:** MC-0…29
**Rule:** no vendor, kit, foundry or simulator product name anywhere in the repo, fixtures included. Fixtures are
synthetic.

---

## 0. Why

Building a model from another simulator's design kit — sampling the kit's model as dense simulated tables, fitting
them, and validating against the kit's own large-signal runs — is now documented end to end in
[AN-04](../user/src/app-notes/an04-model-from-a-reference-kit.md), which the Data step links to. Working that flow
through exposes gaps in the product that the note currently has to work around in prose:

1. **`dc` ignores a `parametric_sweep`.** `sparam`, `hb`, `lp` and `lpp` dispatch a sweep that wraps their analysis;
   `RunDc` (`src/Cli/CliEntry.cs`) elaborates and runs one operating point, and takes no `-o`. A swept DC is the
   natural way to take an I–V family headlessly, and today it returns one point with no word about the sweep axis it
   dropped — the exact failure `ChainSelector`'s promotion rule exists to prevent.
2. **RBF re-gridding does not scale and gives no warning.** `Regrid.Rbf` builds one dense `Rbf2D` over every training
   point: memory O(N²), time O(N³), no `RunControl` in the factor, and a multiquadric system that grows
   ill-conditioned as the points crowd. A dense simulated sweep (10⁴–10⁵ points) runs for a long time and then fails.
   It should refuse up front, say why, and name the alternative.
3. **A Table re-grids even when the data is already on a grid.** Intrinsic-plane simulated tables lie exactly on the
   swept Vgs × Vds (× T) grid, yet `FitTable` always samples an ANN or an RBF onto a new grid first. The samples
   themselves are the table; re-gridding them only adds the ANN's error. The note's "write the .cmodel directly"
   section exists because of this.
4. **The defaults under-fit wide-range data without saying so.** Two layers of 8 neurons is right for a measured
   device over an ordinary range; over deep pinch-off to forward conduction at several temperatures it leaves a
   residual, and the user finds out only by reading the Error tab. The drain-current weighting floor (`IdFloor`) that
   governs subthreshold accuracy is a `.cmc` field with no control in the window.
5. **A Verilog-A model converges less often than the native model of the same device.** On identical benches some DC
   points and some loadpull points fail with the Verilog-A instance and succeed with the `ComposedFET`. Separately, nothing guarantees a run
   survives its worker process exiting: a worker exit must fail the point with a message, not the run.
6. **`model export -o x.va` is the only spelling of Verilog-A export, and nothing leads to it.** A user who asks for
   `--veriloga`, `--va` or `--format va` is told the flag is unknown, with no hint that the extension chooses the
   format.
7. **The Data step does not say what the document still needs.** The importer shows one file at a time; nothing shows
   which constituents the imported sets can support (I–V → currents, S/Y/C → charges, cold-FET S → the network fit,
   two temperatures → `T` and self-heating, pulsed → traps, large-signal → validation), so the first hour of a new
   document is spent discovering the table in `model-composer.md#needs` by trial.

## 1. Already done (not part of this brief)

- `docs/user/src/app-notes/an04-model-from-a-reference-kit.md`: the kit-data flow with three block diagrams, the
  intrinsic-plane sampling through a negative copy of the kit's extrinsic network, what each step does with kit data,
  when the defaults are not enough, and validation against the kit. Listed in `_nav.txt`.
- `model-composer.md` links to it from *What it does* and *From another simulator*, and states the RBF's cost on the
  method picker.
- The Data step has a **Which data does a model need?** button opening the note
  (`StepsPanelView.DataGuidePage`); the Import Data dialog has a Help button opening `model-composer.html#preparing`.
  Both are registered in `DocAnchors.ModelComposerLinks`, so the anchor-contract test covers them.

## 2. Decisions (defaults stand unless the owner changes them before the build starts)

- **D1 — a gridded table is fitted on its grid.** When a constituent's training points, after de-embedding, form a
  complete tensor grid in its inputs (every combination present, within a tolerance of 1e-9 of the axis span), the
  Table method fits `TensorSplineFit` to the samples directly. No re-grid, no `Regrid` setting consulted, and the
  report says `Id: Table on the data's own 101 × 121 × 5 grid`. Held-out points still apply: on a grid they are
  removed by whole grid lines, so the error stays comparable with the other methods. A grid with holes (excluded
  points) falls back to the re-grid, and the report says which points broke the grid.
  *Rejected:* a third `RegridSource` value the user must choose. Whether the data is a grid is a fact about the data.
- **D2 — RBF has a hard point limit, measured, not guessed.** Measure `Rbf2D` time and peak memory at 1k, 2k, 4k and
  8k points on the reference machine; set the limit where the factor passes ~30 s. Above it, refuse before any work:
  `RBF re-grid builds one dense system over 24,000 points (4.6 GB); it is meant for a few thousand. Re-grid through
  the ANN.` Pass the `RunControl` into the factor so Cancel works under the limit.
- **D3 — defaults do not change; the fit says when it under-fits.** After an ANN current fit, when the held-out error
  exceeds 2 % of the current's range **and** the training set holds more than 2,000 points, the status line adds one
  sentence naming the next size up (`2×8 under-fits 24,000 points: try 2×16, or a Table`). The thresholds are
  measured on the shipped examples so none of them trips it. *Rejected:* growing the default network with the data
  — it changes every existing document's next fit.
- **D4 — `IdFloor` and `IgFloor` get controls on the Current step**, under the pickers, with the `.cmc` field as the
  only store.
- **D5 — the Data step's needs list is derived, never stored.** One row per capability in
  `model-composer.md#needs`, each lit or dim from the document's data sets, with what it unlocks and, dim, what would
  light it. It reads the same predicates the steps already use to enable themselves (a dim step's tooltip), so the
  list and the rail cannot disagree. No row is a button.
- **D6 — affine periphery scaling is deferred.** A kit's scalable network often follows `a + b·N` per value, which
  `fixed`/`perFinger`/`perFingerInverse` cannot express; adding a rule changes `.cmc`, `.cmodel` and the Verilog-A
  export together and is a brief of its own. Record it in `src/Design/RESOLVED.md` as an open item with the
  three files it touches.

## 3. Requirements

- **R-mc30-1 — `dc` runs a sweep.** A `parametric_sweep` wrapping a DC analysis is dispatched at the sweep through
  `SelectTop`/`ChainSelector` exactly as `RunSparamSweep` does, with the promotion note on stderr; measurements are
  evaluated over the whole result under the inner analysis's name; `-o out.{npy,mat,txt}` exports through
  `DataSetExporter`. An unswept `dc` is byte-for-byte unchanged. `--set` applies first. Gate: a test beside the
  `sparam` sweep test running a two-axis Vgs-inside-Vds family and comparing the cube with `ParametricSweepEngine.Run`
  in process. Update `docs/user/src/reference/cli.md` and `docs/design/cli.md`.
- **R-mc30-2 — RBF limit and cancellation (D2).** In `Regrid.Rbf` or its callers in `CurrentFit` and `ChargeFit` —
  one check, shared. The measurement goes in `src/Design/RESOLVED.md`. Gate: one test that the refusal fires above
  the limit without allocating the matrix, one that a cancelled factor writes nothing.
- **R-mc30-3 — Table on the data's own grid (D1).** `FitTable` and the charge table path. Gate: a synthetic gridded
  intrinsic set where the direct table reproduces the generating function at the sites to round-off and the re-grid
  path does not; a grid with one excluded point falls back and the report names it.
- **R-mc30-4 — the under-fit sentence (D3)** and **the floor controls (D4).** Snapshot the Current step with
  `tools/UiSnapshot --cmc … --step Current` and look at it.
- **R-mc30-5 — Verilog-A convergence parity.** Reproduce first: extend the `GanVerilogA` example bench (or a scratch
  copy of it) with a DC sweep over the model's whole training domain and a loadpull grid, run both instances, and count
  non-converged points on each side. Find why the Verilog-A side fails where the native one does not — candidates in
  order: missing voltage limiting in the exported `.va` (the native device limits its junction steps), Jacobian entries
  the export leaves to finite differences, a different gmin or initial guess at the worker boundary. Fix the cause,
  not the symptom. Record the counts before and after in `src/Engine/RESOLVED.md`. Gate: the DC sweep converges at
  every point on both sides.
- **R-mc30-6 — a worker exit fails the point.** When the Verilog-A worker process exits mid-run, the current point is
  marked failed with the worker's exit code and last stderr line, the worker is restarted, and the run continues; a
  second exit at the same point stops the run with that message. Gate: `tools/fake-model-lib` (or a fake worker) made to
  exit on the Nth call.
- **R-mc30-7 — Verilog-A export is findable.** `model export` given `--veriloga`, `--va`, `--vams` or `--format va`
  refuses with `the extension of -o picks the format: -o <name>.va`. `model explain` on an active document lists the
  two export spellings. Touch `Model.cs` refusals only.
- **R-mc30-8 — the needs list (D5)** on the Data step, active documents only, between the data-set list and the
  guide button. Snapshot it on a new document, on `synthetic-gan`, and on `simulated-tables` (restore any
  `*.cmc-cache/index.json` the snapshot rewrites — opening an example writes its cache index). Its reserved height
  does not change as sets are imported.
- **R-mc30-9 — the docs follow.** When R-mc30-2/3/4 land, rewrite AN-04's *When the defaults are not enough* and
  *Writing the .cmodel directly* to describe the product as it then is (the direct table becomes the recommended
  route for gridded kit data; writing the `.cmodel` by hand stays as the route for laws the composer cannot fit), and
  the method-picker paragraph in `model-composer.md`. Regenerate each edited page with
  `tools/DocGen --page`.

## 4. Not in this brief

- Affine periphery scaling (D6).
- Any change to the default ANN size or iteration counts.
- A wizard. The needs list informs; the steps stay where they are.

## 5. Verification

Targeted test classes only — the new tests, `DocsFactoryTests`, the `sparam` sweep tests, the Model Composer table
and RBF tests. No full-suite run. Each snapshot is looked at and listed in the completion note.
