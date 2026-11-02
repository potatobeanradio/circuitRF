# Brief — a two-port stability template, and templates that bring their Data Display

**Tag:** `R-tst-n` · **Phase:** feature (owner specification, 2026-09-28)
**Area:** `src/Ui/resources/schematic-templates/` (+ a new `data-display-templates/` beside it),
`src/Ui/Schematic/ShippedSchematicTemplates.cs`, `src/Ui/CircuitRF.Ui.csproj` (the `EmbeddedResource`
items), `src/Ui/Views/Dialogs/InputNameDialog.axaml(.cs)` (`OfferSchematicTemplates`),
`src/Ui/ViewModels/WorkspaceViewModel.cs` (New Cell / New Schematic, ~L14597–14920),
`src/Ui/Schematic/RunResultsWriter.cs` (`AutoDisplayCandidates`), one shipped example workspace
**Depends on:** nothing · **Blocks:** nothing
**Rule:** no vendor, product or reporter names in the template, its display, the example or the docs.

---

## 0. What was asked

A designer checking an LNA's two-port data wanted what an RF designer looks at first: stability (μ, μ′,
source and load stability circles) and the gain available. circuitRF already computes every one of
them — `DerivedParameters.SourceStabilityCircle` / `LoadStabilityCircle` / `Mu` / `MuPrime` /
`MaxGain` in the Data Display, `NetworkMetric.Mu`/`MuPrime`/`K`/`MaxGain` in RfCore — but a user has to
know that and build the plots by hand. The owner's specification:

1. **A 2-port stability schematic template:** two ports, an SnP block, an S-parameter analysis.
2. **Its Data Display** — the page, left to right, top to bottom:
   - a Smith chart of the **source** stability circles (left) and one of the **load** stability
     circles (right);
   - a Rect plot of **μ and μ′** over frequency;
   - a Rect plot of **|S11| and |S22|** (dB);
   - a Rect plot of **|S21|** (dB, left axis) and **|S12|** (dB, **right** y-axis);
   - a separate Rect plot of **Max Gain** (dB).
3. **New Cell and New Schematic** get an **Include Data Display** checkbox beside the template picker.
   Checked, the template's `.cdd` is copied into the workspace; it is **disabled** (and unchecked)
   when the chosen template has no display. (Default when enabled: checked.)
4. The schematic must **point at** that display, so pressing Simulate populates it and opens it.

## 1. `R-tst-1` — templates carry an optional display

- A template's display is a real, authored `.cdd` embedded next to it, matched by file stem
  (`Two_Port_Stability.csch` ↔ `Two_Port_Stability.cdd`), read through the ordinary `.cdd` reader —
  the same no-second-representation rule `ShippedSchematicTemplates` states. `ShippedSchematicTemplate`
  gains `HasDataDisplay`. The documentation-only schematics stay excluded.
- A test loads every shipped template's display through the ordinary reader and fails on any trace
  whose cube or derived quantity the template's analysis cannot produce.

## 2. `R-tst-2` — the link is the existing convention, not a new field

After a run, `AutoOpenOrCreateDataDisplayAsync` already opens the AUTHORED `<schematicKey>.cdd` beside
the bench before it would create one under `results/` (`RunResultsWriter.AutoDisplayCandidates`). So
"the schematic points at the display" is: **write the copy as `<schematic name>.cdd` beside the new
`.csch`** and rewrite its data source (`SelectedDataSource` and every trace's source reference) to the
results file THIS schematic's run writes — resolve that name with the same function the run uses, never
by string-building a second spelling. No new `.csch` field. If that convention turns out not to reach
the case (e.g. a results-directory override), say so and bring it to the owner rather than adding a field.

Before the first run the copied display has no data: it must open without error and read as waiting
for a run, not as broken.

## 3. `R-tst-3` — the template

- `Two_Port_Stability.csch`: `Port` 1 and 2 (50 Ω), one 2-port `SnP` whose `File` is empty so the
  user's first action is to choose their file (check what an empty `File` does at Simulate — it must be
  a clear refusal naming the parameter, not a crash), and an S-parameter analysis with a sensible
  default sweep. Add a short text note on the sheet saying to set the SnP file.
- The display as §0.2. Stability circles need a frequency: use the Data Display's own marker/frequency
  selection for circles as it works today — do not invent a new mechanism; if circles at "all
  frequencies" is the only option, say so in RESOLVED and use it.

## 4. `R-tst-4` — the dialogs

`InputNameDialog.OfferSchematicTemplates` gains the checkbox; both New Cell and New Schematic paths in
`WorkspaceViewModel` copy the display when checked, into the same folder as the new `.csch`, as one
operation with the schematic (a failure to write the display is a warning; the schematic still exists).
A name clash with an existing `.cdd` is refused with the name, never overwritten.

**Creation logic lives in `src/Design`, not in the view model** (`src/Design/CLAUDE.md`: an operation
that lives only in a view model is not a capability). The copy-and-repoint is a function beside
`CellCreate`; if `circuitrf new cell` has no template flag today, do not add one in this brief — note it.

## 5. `R-tst-5` — an example

Add a stability page to an existing shipped example workspace (or a new small one, per the example
workspace rules: a folder + a row in `examples.json`), using a two-port file that circuitRF generates
itself (e.g. an S-parameter run of a simple FET bias network exported as `.s2p`) — **not** a vendor's
file, which the repo may not carry.

## 6. Gates

1. New Schematic → Two-Port Stability → Include Data Display → set the SnP file → Simulate: the display
   opens populated with all six plots (scripted headlessly through the view models; the GUI cannot be
   launched from the agent's shell — say pixels were not seen).
2. A template with no display: the checkbox is disabled and nothing extra is written.
3. Existing templates and New Cell with "(Empty)": byte-identical output to today.

## 7. Tests (minimal)

One per claim: display discovered by stem; copy written beside the `.csch` and repointed to that
schematic's results name; checkbox disabled without a display; name clash refused; every shipped
display's traces resolvable against its template's analysis. `--filter` on the classes touched.

## 8. On completion

`src/Ui/RESOLVED.md` (and `src/Design/RESOLVED.md` for the creation function), never any `CLAUDE.md`.
User doc source for New Cell / templates updated; no DocGen run.
