// Validates a Technology without ever throwing — a bad tech warns and still lets you edit
// (docs/design/layout-view.md §2.4 "Missing tech file").

using System.Linq;

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

        var conductorNames = new HashSet<string>(
            tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name));

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

            // R-via-3/R-via-2 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): a Via entry is a
            // vertical connector, not a horizontal layer at one z — it has no independent thickness of
            // its own (it traverses whatever dielectric(s) separate the conductors it spans), so the
            // "must have positive thickness" rule below applies only to Dielectric/Conductor entries.
            if (sl.Kind != StackupKind.Via && sl.ThicknessDbu <= 0)
                problems.Add($"Stackup layer \"{sl.Name}\" has non-positive thickness ({sl.ThicknessDbu} DBU).");

            if (sl.Kind == StackupKind.Via)
            {
                if (sl.SpanFromLayer is not { Length: > 0 } || !conductorNames.Contains(sl.SpanFromLayer))
                    problems.Add($"Via stackup layer \"{sl.Name}\" spans an unknown conductor layer \"{sl.SpanFromLayer}\".");
                if (sl.SpanToLayer is not { Length: > 0 } || !conductorNames.Contains(sl.SpanToLayer))
                    problems.Add($"Via stackup layer \"{sl.Name}\" spans an unknown conductor layer \"{sl.SpanToLayer}\".");
                if (sl.Fill == ViaFillKind.Plated && sl.WallThicknessDbu is not > 0)
                    problems.Add($"Via stackup layer \"{sl.Name}\" is Plated with no wall thickness.");
            }
        }

        foreach (var rule in tech.DrcRules)
        {
            if (!knownLayers.Contains(rule.Layer))
                problems.Add($"DRC rule \"{rule.Name}\" references unknown layer ({rule.Layer.Layer},{rule.Layer.Datatype}).");

            // A rule may measure a DERIVED region, so the layers it actually reads are inside its
            // expressions. Validating them here — where every other technology-consistency problem
            // is surfaced — is what stops a mistyped expression from becoming a rule that silently
            // measures nothing at run time.
            ValidateRegion(rule, rule.RegionA, "first", knownLayers, problems);

            if (rule.Kind == DrcRuleKind.Density)
            {
                if (rule.WindowDbu is not > 0)
                    problems.Add($"DRC rule \"{rule.Name}\" is a density rule with no window size.");
                if (rule.MinRatio is null && rule.MaxRatio is null)
                    problems.Add($"DRC rule \"{rule.Name}\" is a density rule with neither a minimum nor a maximum.");
            }

            if (rule.Kind == DrcRuleKind.AntennaRatio && rule.MaxRatio is not > 0)
                problems.Add($"DRC rule \"{rule.Name}\" is an antenna rule with no maximum ratio.");

            if (rule.NetScope != DrcNetScope.Any &&
                rule.Kind is not (DrcRuleKind.MinSpacing or DrcRuleKind.MinSeparation))
                problems.Add($"DRC rule \"{rule.Name}\" states a net scope, which only a spacing or " +
                             "separation rule can use.");

            if (rule.NeedsSecondRegion && string.IsNullOrWhiteSpace(rule.RegionB))
                problems.Add($"DRC rule \"{rule.Name}\" is a {rule.Kind} rule but states no second region.");
            else
                ValidateRegion(rule, rule.RegionB, "second", knownLayers, problems);
        }

        // R-tec-2 (brief-technology-editor-units-and-layers.md): a microstrip component cannot
        // resolve a ground plane at all without at least one conductor marked IsGroundReference —
        // report it here, close to the cause, rather than letting every microstrip component fail
        // its own substrate resolution independently and further from the root cause. Multiple
        // ground planes are legal and unambiguous ("nearest ground-designated conductor beneath" is
        // well-defined even with two) — only the zero case is flagged.
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count > 0 && !conductors.Any(l => l.IsGroundReference))
            problems.Add("Stackup has no conductor marked as a ground reference (Stackup tab) — " +
                         "microstrip components cannot resolve a ground plane.");

        return problems;
    }

    /// <summary>
    /// Checks one of a rule's region expressions: it must parse, and every layer it names must be
    /// defined. Blank is fine — that means "the rule's own layer", which is what a hand-authored
    /// rule says.
    /// </summary>
    private static void ValidateRegion(
        DrcRule rule, string? text, string which,
        HashSet<LayerKey> knownLayers, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!Drc.DrcLayerExprParser.TryParse(text, out var expr, out string? error) || expr is null)
        {
            problems.Add($"DRC rule \"{rule.Name}\" has an unreadable {which} region: {error}");
            return;
        }

        foreach (var key in expr.ReferencedLayers())
            if (!knownLayers.Contains(key))
                problems.Add($"DRC rule \"{rule.Name}\" {which} region references unknown layer " +
                             $"({key.Layer},{key.Datatype}).");
    }
}
