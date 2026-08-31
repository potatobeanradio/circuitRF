// Validates a Technology without ever throwing — a bad tech warns and still lets you edit
// (docs/design/layout-view.md §2.4 "Missing tech file").

using System.Linq;

namespace CircuitRF.Design.Layout;

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

            // MIM-7 — a dielectric that is patterned with a conductor rather than laterally
            // continuous. Two hard rules; the third thing worth saying is a RECOMMENDATION and is
            // stated in the field's own documentation and in the editor's tooltip rather than
            // failed here: name the conductor directly ABOVE the dielectric, because that is the
            // plate the film is deposited under. Tying it to a conductor further away is legal,
            // honoured, and only harder to read.
            if (sl.PresentWithLayer is { Length: > 0 } plate)
            {
                if (sl.Kind != StackupKind.Dielectric)
                    problems.Add($"Stackup layer \"{sl.Name}\" is a {sl.Kind} entry but names \"{plate}\" " +
                                 "as the conductor it is patterned with. Only a Dielectric entry can be a " +
                                 "patterned thin film.");
                else if (!conductorNames.Contains(plate))
                    problems.Add($"Dielectric stackup layer \"{sl.Name}\" is patterned with an unknown " +
                                 $"conductor layer \"{plate}\".");
                else if (tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor &&
                                                      l.Name == plate && l.IsGroundReference))
                    problems.Add($"Dielectric stackup layer \"{sl.Name}\" is patterned with \"{plate}\", " +
                                 "which is the ground reference. A ground plane is never an analysis level, " +
                                 "so this dielectric could never be present in any run — name the signal " +
                                 "conductor whose artwork the film is deposited under.");
            }

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

        ValidateInterchange(tech, problems);

        return problems;
    }

    /// <summary>
    /// The Interchange tab's fields decide what a layer is CALLED in an exported file, so two layers
    /// claiming one name is not a cosmetic clash — it silently merges or destroys geometry:
    /// <list type="bullet">
    /// <item>Two Gerber suffixes agreeing means both layers write to <c>&lt;cell&gt;.&lt;suffix&gt;</c>
    /// and the second overwrites the first. The exporter now disambiguates rather than clobbering,
    /// but the resulting file names are then not the ones the technology asked for — which is a
    /// technology problem, reported here, at its cause.</item>
    /// <item>Two DXF layer names agreeing merges those layers on export, and on import there is no
    /// way to tell which of them an incoming layer of that name belongs to.</item>
    /// <item>Two GDSII aliases agreeing points two distinct layers at one (layer, datatype) — the
    /// same collision the layer table's own key check catches for un-aliased layers.</item>
    /// </list>
    /// Blank is never a collision: it means "no alias", and every consumer has its own fallback.
    /// </summary>
    private static void ValidateInterchange(Technology tech, List<string> problems)
    {
        var gerber = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dxf    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gdsii  = new Dictionary<(int Layer, int Datatype), string>();

        foreach (var layer in tech.Layers)
        {
            if (layer.Interchange is not { } map) continue;

            if (map.GerberSuffix is { Length: > 0 } suffix)
            {
                if (gerber.TryGetValue(suffix, out var first))
                    problems.Add($"Layers \"{first}\" and \"{layer.Name}\" share the Gerber suffix \"{suffix}\" — " +
                                 "they would write to the same file.");
                else gerber[suffix] = layer.Name;
            }

            if (map.DxfLayerName is { Length: > 0 } dxfName)
            {
                if (dxf.TryGetValue(dxfName, out var first))
                    problems.Add($"Layers \"{first}\" and \"{layer.Name}\" share the DXF layer name \"{dxfName}\" — " +
                                 "they would merge on export and are indistinguishable on import.");
                else dxf[dxfName] = layer.Name;
            }

            // A GDSII alias is only a collision when BOTH halves are stated: one half alone still
            // falls back to the layer's own Layer/Datatype for the other, which the layer-table key
            // check above already covers.
            if (map.GdsiiLayer is { } gl && map.GdsiiDatatype is { } gd)
            {
                if (gdsii.TryGetValue((gl, gd), out var first))
                    problems.Add($"Layers \"{first}\" and \"{layer.Name}\" share the GDSII alias ({gl},{gd}).");
                else gdsii[(gl, gd)] = layer.Name;
            }
        }
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
