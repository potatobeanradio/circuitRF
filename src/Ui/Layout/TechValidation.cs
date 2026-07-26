// Validates a Technology without ever throwing — a bad tech warns and still lets you edit
// (docs/design/layout-view.md §2.4 "Missing tech file").

namespace CircuitRF.Ui.Layout;

public static class TechValidation
{
    public static IReadOnlyList<string> Validate(Technology tech)
    {
        var problems = new List<string>();
        var knownLayers = new HashSet<LayerKey>();

        foreach (var layer in tech.Layers)
        {
            if (!knownLayers.Add(layer.Key))
                problems.Add($"Duplicate layer ({layer.Key.Layer},{layer.Key.Datatype}) \"{layer.Name}\".");
        }

        foreach (var sl in tech.Stackup.Layers)
        {
            foreach (var dl in sl.DrawingLayers)
            {
                if (!knownLayers.Contains(dl))
                    problems.Add($"Stackup layer \"{sl.Name}\" references unknown drawing layer ({dl.Layer},{dl.Datatype}).");
            }

            if (sl.Kind == StackupKind.Conductor && sl.SigmaSm <= 0)
                problems.Add($"Stackup layer \"{sl.Name}\" is a conductor with non-positive conductivity ({sl.SigmaSm} S/m).");

            if (sl.Kind == StackupKind.Dielectric && sl.Epsr < 1)
                problems.Add($"Stackup layer \"{sl.Name}\" is a dielectric with εr < 1 ({sl.Epsr}).");

            if (sl.ThicknessDbu <= 0)
                problems.Add($"Stackup layer \"{sl.Name}\" has non-positive thickness ({sl.ThicknessDbu} DBU).");
        }

        foreach (var rule in tech.DrcRules)
        {
            if (!knownLayers.Contains(rule.Layer))
                problems.Add($"DRC rule \"{rule.Name}\" references unknown layer ({rule.Layer.Layer},{rule.Layer.Datatype}).");
        }

        return problems;
    }
}
