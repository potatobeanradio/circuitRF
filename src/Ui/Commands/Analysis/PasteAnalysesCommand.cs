using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Commands.Analysis;

/// <summary>
/// Appends pasted/inserted analyses to the model with name-collision resolution.
/// One undoable action — all analyses paste and undo together.
/// Analyses are appended verbatim (§5.1 faithful); unresolved VAR refs surface via the
/// expression-preview hint when the user opens the editor, not auto-rewritten/dropped.
/// </summary>
internal sealed class PasteAnalysesCommand : IUiCommand
{
    private readonly SchematicEditModel _model;
    private readonly List<Core.Design.Analysis> _toAppend;

    public string Description => $"Paste {_toAppend.Count} analysis/analyses";

    public PasteAnalysesCommand(SchematicEditModel model, IEnumerable<Core.Design.Analysis> toPaste,
                                string? retargetInner = null)
    {
        _model    = model;
        _toAppend = ResolveNames(model.Analyses, toPaste.ToList(), retargetInner);
    }

    public void Execute()
    {
        foreach (var a in _toAppend)
            _model.Analyses.Add(a);
        _model.NotifyChanged();
    }

    public void Undo()
    {
        foreach (var a in _toAppend)
            _model.Analyses.Remove(a);
        _model.NotifyChanged();
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
            string name = used.Contains(pasted[i].Name)
                ? ResolveConflict(used, pasted[i].Name)
                : pasted[i].Name;
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
                    ? mapped                                            // inner is part of the pasted chain
                    : (retargetInner ?? psa.InnerAnalysisName);        // lone sweep → attach to selected analysis
            result.Add(DuplicateAnalysisCommand.CloneAnalysis(pasted[i], newNames[i], newInner));
        }
        return result;
    }

    private static string ResolveConflict(HashSet<string> used, string baseName)
    {
        string candidate = baseName + " copy";
        if (!used.Contains(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            candidate = baseName + " copy " + n;
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
