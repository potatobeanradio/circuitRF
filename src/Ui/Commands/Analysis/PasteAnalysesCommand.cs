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

    public PasteAnalysesCommand(SchematicEditModel model, IEnumerable<Core.Design.Analysis> toPaste)
    {
        _model    = model;
        _toAppend = ResolveNames(model.Analyses, toPaste);
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

    // Builds the collision-free list. Works from a growing HashSet so intra-paste names
    // don't collide with each other (e.g. pasting two SP1s → SP1, SP1 copy).
    private static List<Core.Design.Analysis> ResolveNames(
        IReadOnlyList<Core.Design.Analysis> existing,
        IEnumerable<Core.Design.Analysis> pasted)
    {
        var used   = existing.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<Core.Design.Analysis>();
        foreach (var a in pasted)
        {
            string name = a.Name;
            if (used.Contains(name))
                name = ResolveConflict(used, name);
            used.Add(name);
            result.Add(DuplicateAnalysisCommand.CloneAnalysis(a, name));
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
