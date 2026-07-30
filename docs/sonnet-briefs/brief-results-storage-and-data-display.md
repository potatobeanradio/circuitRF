# Sonnet Brief — Flatten analysis-result storage, multi-dataset Data Displays, auto-created `.cdd`

**Read §1 first — the current convention is one file per *analysis*, not per run, which changes the naming
scheme the owner proposed.**

**Primary files:**
- `src/Ui/Schematic/RunResultsWriter.cs` — the whole path convention. `SchematicKey` (~line 29),
  `ResolveResultsRoot` (~line 60), plus `WriteRun` and the owner-identity/collision-detection helper below it.
- `src/Ui/Views/DataDisplay/DataDisplayView.axaml.cs` — **line ~230** hardcodes a `run.npy` special case.
- `docs/design/data-display.md` **§3 and §7.0** — the documented convention. **Must be updated, not left
  contradicting the code.**
- `circuitRF_demo/results/FET_curve_tracer/` — a **committed** workspace on the old layout (§5).

Gate command is plain `dotnet test`.

---

## 1. Naming — exactly as the owner proposed

`WriteRun` writes **one grouped `run.npy` per run**, containing every analysis as a group plus a
`measurements` group:

```csharp
var runNpy = Path.Combine(dir, "run.npy");
DataSetExporter.Export(grouped, runNpy, ExportFormat.Npy);
```

So flattening is trivial and the owner's proposal is right as stated:

```
results/<schematicKey>.npy          // was: results/<schematicKey>/run.npy
```

`SchematicKey` (~line 29) already returns `cell`, or `cell.view` when a cell has several schematics, so
**uniqueness across a flat workspace-wide directory is already solved.** Reuse it unchanged.

**Note:** the class-level header comment previously claimed a per-analysis
`results/<schematicKey>/<analysisName>.npy` convention. That was stale and has been corrected — the design
doc's §7.0 explicitly records the per-analysis scheme as an *earlier plan that does not ship*. Do not
reintroduce it.

### 1.1 The trap that a naive flattening walks straight into

**R-res-0. `WriteRun` currently deletes every `.npy` in its directory each run:**

```csharp
foreach (var stale in Directory.GetFiles(dir, "*.npy"))
    File.Delete(stale);
```

That is safe inside a **per-schematic** directory. In a **shared flat `results/`** it is catastrophic: every
run would wipe every other schematic's results *and* every user-named baseline the owner asked for in §2.

**Delete only the specific file about to be written**, and nothing else. This is the single most damaging thing
this change could get wrong, and it is a two-line change away from happening.

### 1.2 The `.source` collision marker has no home in a flat layout

`WriteRun` writes a `.source` file into `results/<schematicKey>/` and compares owners to catch two cells
sharing a results directory. **Flattening removes that directory.**

**R-res-0a. Decide and report: drop the collision check, or give it a new home.** Dropping it is defensible —
`SchematicKey` already disambiguates `cell` from `cell.view`, and two cells cannot share a folder name, so the
collision it guards against may no longer be reachable. If it *is* still reachable, a single manifest in
`results/` is the alternative. **Do not silently leave orphaned `.source` files behind** either way; clean them
up during §5's migration.

## 2. User-specified result filenames

**R-res-2. There is one file per *run*, so the override is a schematic-level setting, not per-analysis.** Blank
means the §1 default.

The owner suggested the analysis editor, which is the right area — but it belongs as a **single "Results file"
field near the analyses list**, not a field on each analysis card, since all analyses in a run share the one
file. A per-card field would imply per-analysis files, which is not how this works.

- Blank → `<schematicKey>.npy`.
- Set → that name, `.npy` appended if absent, always inside `results/`. **Reject or sanitize path
  separators** — this is a filename, not a path, and letting it escape `results/` breaks the
  manual-management premise.
- **Persist it in the `.csch`** so it survives reload and travels with the design.

**R-res-3. The default overwrites silently on every run, and naming a file is how a user preserves a
baseline.** State that in the field's tooltip. It is the right behaviour — the default file means "current
results" — but it must be predictable, since accumulating timestamped files is what the owner is escaping.

## 3. A Data Display mixing datasets from several files

**R-res-4. A `.cdd` references a *set* of `.npy` files, each with a short display alias.** Today it holds one
source path; it becomes a list.

- The alias **defaults to the file stem** and is user-editable.
- Aliases must be **unique within the `.cdd`** — that is what makes trace labels unambiguous when two
  datasets carry the same curve.
- **Store aliases in the `.cdd`**, not derived at load time: `baseline` vs `tuned` is a display decision the
  user made and it must survive reload.
- Trace labels are qualified by alias — e.g. `baseline:S21`, `tuned:S21` — so the same metric from two runs is
  distinguishable on one plot. That is the entire point of the feature.

**R-res-5. Missing files are a first-class case, not an edge case.** The owner's stated goal is deleting `.npy`
files by hand in Finder — so a `.cdd` referencing a deleted file **will** happen, routinely.

- Report the missing dataset **by name**, keep every other dataset live, and **preserve the plot
  configuration** so re-running restores the traces.
- Never drop or rewrite trace definitions because their data is temporarily absent. If deleting a results file
  silently destroys a `.cdd`'s configuration, the flat-directory feature actively backfires.

**R-res-6. An open Data Display refreshes when a referenced `.npy` is rewritten**, and refreshes only the
dataset that changed. Re-running an analysis while looking at its plot is the common workflow.

## 4. Picker, and auto-created `.cdd`

**R-res-7. The Data Display source picker lists every `.npy` in `results/`**, resolved through
`ResolveResultsRoot` (~line 60) so scratch sessions work identically. It is multi-select, since §3 makes a
`.cdd` hold several.

**Remove the `run.npy` special case at `DataDisplayView.axaml.cs` ~line 230.** It encodes the old convention
and will silently misbehave under the new one.

**R-res-8. When an analysis runs and no `<schematicKey>.cdd` exists, create one, save it, and open it —
unprompted.** Matching R-L5-15's rule for layouts: the command's value is that it just works.

**R-res-9. When a `.cdd` does exist, open and focus it — do not prompt.** Same reasoning, and it retires the
current prompt. This is a deliberate behaviour change from today.

**R-res-10. The auto-created `.cdd` should not be blank.** The owner's stated purpose is helping new users, and
an empty canvas helps nobody. Pre-populate a sensible default for the analysis that just ran — an S-parameter
run gets an S-parameter plot, a power sweep gets a rectangular plot of the swept metric. **If the plot-type
defaults are not readily available, say so rather than shipping an empty display**; it is the difference
between the feature working and merely existing.

## 5. Migration — there is a committed workspace on the old layout

**R-res-11. Migrate `<schematicKey>/<analysis>.npy` → `<schematicKey>.<analysis>.npy` on workspace open**, and
report what moved. Leaving both layouts working indefinitely doubles the read paths forever.

**`circuitRF_demo/results/FET_curve_tracer/` is in the repository** and must be migrated as part of this work,
along with `circuitRF_demo/results/FET_curve_tracer.cdd`, which references the old path. A committed demo that
no longer opens is worse than the bug being fixed.

**R-res-12. Update `docs/design/data-display.md` §3 and §7.0.** They document the old convention and are cited
in `RunResultsWriter`'s own header comment — leaving them stale means the next reader implements the old
scheme from the design doc.

## 6. Guardrails

- Do not change the `.npy` format or introduce `.npz` (§1).
- Do not invent a second schematic key — `SchematicKey` exists (§1).
- Do not let a user-specified filename escape `results/` (§2).
- Do not discard `.cdd` trace configuration when a source file is missing (§3).
- Don't touch `src/Core`, `src/Engine`, `RfCore`.

## 7. Gate

1. Builds green; `dotnet test` green; no existing test regresses.
2. **Flat naming (R-res-1)** — a run writes exactly one file, `results/<schematicKey>.npy`, containing every
   analysis as a group; no per-schematic subdirectory is created. Two cells whose schematics share a view name
   still produce distinct filenames.
2a. **Stale-delete is scoped (R-res-0)** — with results from **three** schematics plus a user-named baseline
   present, running one schematic leaves the other four files **untouched**. This is the regression test that
   matters most; assert file-by-file, not by count.
2b. **Collision marker (R-res-0a)** — no orphaned `.source` files remain after migration; state whether the
   check was dropped or rehomed.
3. **Filename override (R-res-2/3)** — a named file is written where named; blank falls back to the default;
   the name round-trips through `.csch`; a name containing a path separator is rejected or sanitized; a re-run
   overwrites without prompting.
4. **Multi-dataset `.cdd` (R-res-4)** — one display plots the same metric from two `.npy` files with distinct
   aliased labels; aliases round-trip through the `.cdd`; duplicate aliases are refused.
5. **Missing source (R-res-5)** — deleting a referenced `.npy` reports it by name, leaves other datasets
   plotted, and **preserves trace configuration**; restoring the file restores the traces without re-editing.
6. **Live refresh (R-res-6)** — re-running updates the open display; an unchanged dataset is not reloaded.
7. **Picker (R-res-7)** — lists every `.npy` in `results/` for both a saved workspace and a scratch session;
   no `run.npy` special case remains anywhere.
8. **Auto-create (R-res-8/9/10)** — running with no `.cdd` creates, saves, opens and focuses one containing a
   **non-empty** default plot; running when one exists opens and focuses it with **no prompt**.
9. **Migration (R-res-11)** — a workspace on the old layout migrates on open with a report;
   `circuitRF_demo` opens and its `.cdd` resolves. Old `results/<schematicKey>/` directories and their
   `.source` files are gone afterwards.

## 8. On completion

Record in `src/Ui/CLAUDE.md`: that results are **one grouped `run.npy` per run**, now flattened to
`results/<schematicKey>.npy`, and that a per-analysis filename scheme is an **earlier plan that must not be
reintroduced** (the stale class comment that claimed otherwise has been fixed); **R-res-0's scoped delete** and
why the original wildcard delete was safe only inside a per-schematic directory; what happened to the
`.source` collision check; **R-res-5's missing-file rule and why it is first-class** given manual file
management is the point; that the post-run prompt was deliberately removed; and whether R-res-10's default
plot was achievable or an empty display shipped.
