# Sonnet Brief — Analyses copy/paste: fix sweep-clone crash + chain-aware copy/paste

Three related fixes in the Analyses copy/paste path. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## Bug 1 (crash) — `CloneAnalysis` has no ParametricSweepAnalysis case
`DuplicateAnalysisCommand.CloneAnalysis` (used by Duplicate AND `PasteAnalysesCommand`) switches over
DC/SP/HB and hits `_ => throw new NotSupportedException(...)` for a sweep → pasting/duplicating a sweep crashes.
Add a sweep arm, and let callers re-target the inner link (needed for Bug 2 + lone-sweep paste).

In `src/Ui/Commands/Analysis/DuplicateAnalysisCommand.cs`, change the signature to accept an optional new
inner name and add the sweep case:
```csharp
    internal static Core.Design.Analysis CloneAnalysis(
        Core.Design.Analysis a, string newName, string? newInnerName = null) => a switch
    {
        DcAnalysis =>
            new DcAnalysis(newName) { Enabled = a.Enabled },

        SParameterAnalysis sp =>
            new SParameterAnalysis(newName, sp.Sweeps) { Enabled = a.Enabled },

        HarmonicBalanceAnalysis hb =>
            new HarmonicBalanceAnalysis(newName) { /* …unchanged field copies… */ },

        ParametricSweepAnalysis psa =>
            CloneSweep(psa, newName, newInnerName ?? psa.InnerAnalysisName),

        _ => throw new NotSupportedException($"Cannot clone analysis type {a.GetType().Name}"),
    };

    private static ParametricSweepAnalysis CloneSweep(ParametricSweepAnalysis psa, string name, string inner)
        => psa.Spec is { } spec
            ? new ParametricSweepAnalysis(name, psa.SweepVarName, spec, inner)        { Enabled = psa.Enabled }
            : new ParametricSweepAnalysis(name, psa.SweepVarName, psa.SweepValues, inner) { Enabled = psa.Enabled };
```
(Keep the HB arm exactly as it is now — only add the sweep arm + the `newInnerName` parameter.)

## Bug 2 — copying an analysis must include its sweeps; paste must keep links
### Copy expansion (`AnalysesListViewModel`)
Today Copy serializes only the selected rows. Expand each selected BASE analysis to include the sweeps that
wrap it (so copying a base brings its whole chain); a selected sweep alone is copied as just that sweep.
Add a helper and use it in `Copy`:
```csharp
    /// <summary>Expands a selection so any selected base analysis also carries the parametric sweeps
    /// that (transitively) wrap it. Result is ordered by position in the model so chains stay contiguous
    /// (base first, then its sweeps inner→outer). Selected sweeps with no selected base come along alone.</summary>
    private IReadOnlyList<Analysis> ExpandSelectionToChains(IEnumerable<Analysis> selected)
    {
        if (_schematicVm is null) return selected.ToList();
        var all  = _schematicVm.EditModel.Analyses;
        var keep = new HashSet<Analysis>(selected);

        // Map base name → its wrapping sweeps (follow InnerAnalysisName forward).
        var sweepsByInner = all.OfType<ParametricSweepAnalysis>()
            .ToLookup(p => p.InnerAnalysisName, StringComparer.OrdinalIgnoreCase);

        foreach (var a in selected.ToList())
            if (a is not ParametricSweepAnalysis)          // a base — pull its chain
            {
                var cursor = a.Name;
                while (sweepsByInner[cursor].FirstOrDefault() is { } sw)
                { keep.Add(sw); cursor = sw.Name; }
            }

        return all.Where(keep.Contains).ToList();          // model order → contiguous chains
    }
```
In `Copy`, expand before serializing:
```csharp
        IReadOnlyList<Analysis> toCopy =
            _selectedRows.Count > 0 ? _selectedRows.Select(r => r.Analysis).ToList()
          : SelectedRow is not null ? [SelectedRow.Analysis]
          : [];
        if (toCopy.Count == 0) return;
        await CopyToClipboard(window, ExpandSelectionToChains(toCopy));
```
(`CopyAll` already copies everything — leave it.)

### Paste remapping (`PasteAnalysesCommand`)
`ResolveNames` remaps colliding names but never updates `InnerAnalysisName`, so pasted chains break. Rewrite it
to (1) compute all new names first, then (2) clone each analysis with its name AND a remapped inner link. A
pasted sweep whose inner is also in the paste set points at the remapped inner; a lone sweep whose inner is NOT
in the set re-targets to the user's selected analysis (the "paste onto a different analysis" flow).

Add a `retargetInner` parameter to the command and thread it into `ResolveNames`:
```csharp
    public PasteAnalysesCommand(SchematicEditModel model, IEnumerable<Core.Design.Analysis> toPaste,
                                string? retargetInner = null)
    {
        _model    = model;
        _toAppend = ResolveNames(model.Analyses, toPaste.ToList(), retargetInner);
    }

    private static List<Core.Design.Analysis> ResolveNames(
        IReadOnlyList<Core.Design.Analysis> existing,
        IReadOnlyList<Core.Design.Analysis> pasted,
        string? retargetInner)
    {
        var used  = existing.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: assign collision-free names, building old→new map (intra-paste names collide too).
        var newNames = new string[pasted.Count];
        for (int i = 0; i < pasted.Count; i++)
        {
            string name = used.Contains(pasted[i].Name) ? ResolveConflict(used, pasted[i].Name) : pasted[i].Name;
            used.Add(name);
            remap[pasted[i].Name] = name;
            newNames[i] = name;
        }

        // Pass 2: clone with remapped name + inner link.
        var result = new List<Core.Design.Analysis>(pasted.Count);
        for (int i = 0; i < pasted.Count; i++)
        {
            string? newInner = null;
            if (pasted[i] is ParametricSweepAnalysis psa)
                newInner = remap.TryGetValue(psa.InnerAnalysisName, out var mapped)
                    ? mapped                       // inner is part of the pasted chain
                    : (retargetInner ?? psa.InnerAnalysisName);  // lone sweep → attach to selected analysis
            result.Add(DuplicateAnalysisCommand.CloneAnalysis(pasted[i], newNames[i], newInner));
        }
        return result;
    }
```
In `AnalysesListViewModel.Paste`, pass the selected analysis as the re-target so a lone pasted sweep attaches to
whatever the user clicked:
```csharp
        _schematicVm.Execute(new PasteAnalysesCommand(
            _schematicVm.EditModel, toPaste, retargetInner: SelectedRow?.Analysis.Name));
```

## Tests
- **CloneAnalysis sweep:** clone a `ParametricSweepAnalysis` (both Spec and Values forms) → no throw; var/values/
  enabled preserved; `newInnerName` honored when supplied, else original inner kept.
- **Copy expansion:** model `[DC1, DC1_sweep_Vds, DC1_sweep_Vgs, SP1]`; select `DC1` → `ExpandSelectionToChains`
  returns `[DC1, DC1_sweep_Vds, DC1_sweep_Vgs]` in order; select `SP1` → `[SP1]`.
- **Paste remap:** paste that chain into a model already containing `DC1` → names become `DC1 copy`,
  `DC1 copy_sweep_Vds`… and each sweep's `InnerAnalysisName` points at the remapped base/inner (no dangling
  refs). Paste a lone `DC1_sweep_Vgs` with `retargetInner="SP1"` → its inner becomes `SP1`.

## Gate (manual)
Copy a parametric-sweep card → no crash. Copy a DC that has two sweeps, paste → the base + both sweeps appear as
a working chain. Copy a lone sweep card, select a different analysis, paste → the sweep attaches to that analysis.

## On completion
Note in the nearest CLAUDE.md: `CloneAnalysis` handles `ParametricSweepAnalysis` and takes an optional
`newInnerName`; Copy expands base selections to whole chains; Paste remaps both names and `InnerAnalysisName`,
re-targeting a lone sweep's inner to the selected analysis.
