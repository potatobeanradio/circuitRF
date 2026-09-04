// Validates a Technology without ever throwing — a bad tech warns and still lets you edit
// (docs/design/layout-view.md §2.4 "Missing tech file").

using System.Linq;

namespace CircuitRF.Design.Layout;

/// <summary>Which editor tab a <see cref="TechProblem"/> belongs to — the tab whose fields are the
/// ones that would fix it. The Technology editor shows the ACTIVE tab's problems only (a Gerber
/// suffix collision has nothing to say to someone editing the layer table), and counts the rest on
/// their own tab headers so nothing is hidden.</summary>
public enum TechProblemArea { Layers, Stackup, Drc, Interchange }

/// <summary>One technology-consistency problem, and the tab that owns it.</summary>
public sealed record TechProblem(TechProblemArea Area, string Message);

public static class TechValidation
{
    /// <summary>The messages alone, in <see cref="Analyze"/>'s order — the long-standing shape of
    /// this API, kept for every caller that only wants to say what is wrong.</summary>
    public static IReadOnlyList<string> Validate(Technology tech)
        => [.. Analyze(tech).Select(p => p.Message)];

    /// <summary>
    /// Every problem, each attributed to the tab that can fix it.
    ///
    /// <para><b>One problem per CAUSE, never one per consequence.</b> Two rules here exist because a
    /// real imported board produced 22 messages describing 2 facts, which is a wall of text nobody
    /// reads: an alias shared by N layers is reported once naming them all rather than N-1 times
    /// pairwise, and a stackup carrying vias but no conductors is reported once as the missing
    /// stackup rather than three times per via (both span ends and the wall thickness) — every one
    /// of which is unfixable until the conductors exist.</para>
    /// </summary>
    public static IReadOnlyList<TechProblem> Analyze(Technology tech)
    {
        var problems = new List<TechProblem>();
        var knownLayers = new HashSet<LayerKey>();

        foreach (var layer in tech.Layers)
        {
            if (!knownLayers.Add(layer.Key))
                problems.Add(new(TechProblemArea.Layers,
                    $"Duplicate layer ({layer.Key.Layer},{layer.Key.Datatype}) \"{layer.Name}\"."));
        }

        var conductorNames = new HashSet<string>(
            tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).Select(l => l.Name));

        // A via names two conductors; with no conductor entries at all it CANNOT name them, and
        // neither can anything else in the stackup be checked against them. Say the one thing that is
        // true and actionable — the stackup is missing — and skip the checks it makes unanswerable.
        // This is exactly the state a Gerber set with no job file imports into: artwork, a drill
        // layer, and no substrate information anywhere in the files.
        bool hasVias = tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Via);
        bool stackupIsSubstrateless = conductorNames.Count == 0 && hasVias;
        if (stackupIsSubstrateless)
            problems.Add(new(TechProblemArea.Stackup,
                "The stackup names a via layer but no conductor layers, so nothing says what the via " +
                "connects or what the board is made of. Add the copper and dielectric layers on the " +
                "Stackup tab. (An imported Gerber set carries no stackup unless it ships a job file.)"));

        foreach (var sl in tech.Stackup.Layers)
        {
            foreach (var dl in sl.DrawingLayers)
            {
                if (!knownLayers.Contains(dl))
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Stackup layer \"{sl.Name}\" references unknown drawing layer ({dl.Layer},{dl.Datatype})."));
            }

            if (sl.Kind == StackupKind.Conductor && sl.SigmaSm <= 0)
                problems.Add(new(TechProblemArea.Stackup,
                    $"Stackup layer \"{sl.Name}\" is a conductor with non-positive conductivity ({sl.SigmaSm} S/m)."));

            if (sl.Kind == StackupKind.Dielectric && sl.Epsr < 1)
                problems.Add(new(TechProblemArea.Stackup,
                    $"Stackup layer \"{sl.Name}\" is a dielectric with εr < 1 ({sl.Epsr})."));

            // R-via-3/R-via-2 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): a Via entry is a
            // vertical connector, not a horizontal layer at one z — it has no independent thickness of
            // its own (it traverses whatever dielectric(s) separate the conductors it spans), so the
            // "must have positive thickness" rule below applies only to Dielectric/Conductor entries.
            if (sl.Kind != StackupKind.Via && sl.ThicknessDbu <= 0)
                problems.Add(new(TechProblemArea.Stackup,
                    $"Stackup layer \"{sl.Name}\" has non-positive thickness ({sl.ThicknessDbu} DBU)."));

            // MIM-7 — a dielectric that is patterned with a conductor rather than laterally
            // continuous. Two hard rules; the third thing worth saying is a RECOMMENDATION and is
            // stated in the field's own documentation and in the editor's tooltip rather than
            // failed here: name the conductor directly ABOVE the dielectric, because that is the
            // plate the film is deposited under. Tying it to a conductor further away is legal,
            // honoured, and only harder to read.
            if (sl.PresentWithLayer is { Length: > 0 } plate)
            {
                if (sl.Kind != StackupKind.Dielectric)
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Stackup layer \"{sl.Name}\" is a {sl.Kind} entry but names \"{plate}\" " +
                        "as the conductor it is patterned with. Only a Dielectric entry can be a " +
                        "patterned thin film."));
                else if (!conductorNames.Contains(plate))
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Dielectric stackup layer \"{sl.Name}\" is patterned with an unknown " +
                        $"conductor layer \"{plate}\"."));
                else if (tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor &&
                                                      l.Name == plate && l.IsGroundReference))
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Dielectric stackup layer \"{sl.Name}\" is patterned with \"{plate}\", " +
                        "which is the ground reference. A ground plane is never an analysis level, " +
                        "so this dielectric could never be present in any run — name the signal " +
                        "conductor whose artwork the film is deposited under."));
            }

            if (sl.Kind == StackupKind.Via && !stackupIsSubstrateless)
            {
                if (sl.SpanFromLayer is not { Length: > 0 } || !conductorNames.Contains(sl.SpanFromLayer))
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Via stackup layer \"{sl.Name}\" spans an unknown conductor layer \"{sl.SpanFromLayer}\"."));
                if (sl.SpanToLayer is not { Length: > 0 } || !conductorNames.Contains(sl.SpanToLayer))
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Via stackup layer \"{sl.Name}\" spans an unknown conductor layer \"{sl.SpanToLayer}\"."));
                if (sl.Fill == ViaFillKind.Plated && sl.WallThicknessDbu is not > 0)
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Via stackup layer \"{sl.Name}\" is Plated with no wall thickness."));
            }
        }

        foreach (var rule in tech.DrcRules)
        {
            if (!knownLayers.Contains(rule.Layer))
                problems.Add(new(TechProblemArea.Drc,
                    $"DRC rule \"{rule.Name}\" references unknown layer ({rule.Layer.Layer},{rule.Layer.Datatype})."));

            // A rule may measure a DERIVED region, so the layers it actually reads are inside its
            // expressions. Validating them here — where every other technology-consistency problem
            // is surfaced — is what stops a mistyped expression from becoming a rule that silently
            // measures nothing at run time.
            ValidateRegion(rule, rule.RegionA, "first", knownLayers, problems);

            if (rule.Kind == DrcRuleKind.Density)
            {
                if (rule.WindowDbu is not > 0)
                    problems.Add(new(TechProblemArea.Drc,
                        $"DRC rule \"{rule.Name}\" is a density rule with no window size."));
                if (rule.MinRatio is null && rule.MaxRatio is null)
                    problems.Add(new(TechProblemArea.Drc,
                        $"DRC rule \"{rule.Name}\" is a density rule with neither a minimum nor a maximum."));
            }

            if (rule.Kind == DrcRuleKind.AntennaRatio && rule.MaxRatio is not > 0)
                problems.Add(new(TechProblemArea.Drc,
                    $"DRC rule \"{rule.Name}\" is an antenna rule with no maximum ratio."));

            if (rule.NetScope != DrcNetScope.Any &&
                rule.Kind is not (DrcRuleKind.MinSpacing or DrcRuleKind.MinSeparation))
                problems.Add(new(TechProblemArea.Drc,
                    $"DRC rule \"{rule.Name}\" states a net scope, which only a spacing or " +
                    "separation rule can use."));

            if (rule.NeedsSecondRegion && string.IsNullOrWhiteSpace(rule.RegionB))
                problems.Add(new(TechProblemArea.Drc,
                    $"DRC rule \"{rule.Name}\" is a {rule.Kind} rule but states no second region."));
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
            problems.Add(new(TechProblemArea.Stackup,
                "Stackup has no conductor marked as a ground reference (Stackup tab) — " +
                "microstrip components cannot resolve a ground plane."));

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
    /// technology problem, reported here, at its cause. It also breaks IMPORT: the suffix is rung 2
    /// of <c>GerberLayerIdentity</c>'s cascade and resolves to the FIRST layer claiming it, so every
    /// file of that extension lands on one layer.</item>
    /// <item>Two DXF layer names agreeing merges those layers on export, and on import there is no
    /// way to tell which of them an incoming layer of that name belongs to.</item>
    /// <item>Two GDSII aliases agreeing points two distinct layers at one (layer, datatype) — the
    /// same collision the layer table's own key check catches for un-aliased layers.</item>
    /// </list>
    /// Blank is never a collision: it means "no alias", and every consumer has its own fallback.
    ///
    /// <para><b>One message per shared VALUE, naming the layers that share it</b> — not one per
    /// additional claimant. A Gerber set whose files all carry one extension (a whole board written
    /// as <c>.art</c>, which is a real and common convention) imported as 21 layers claiming the
    /// suffix "art": pairwise reporting made that 20 lines saying one thing.</para>
    /// </summary>
    private static void ValidateInterchange(Technology tech, List<TechProblem> problems)
    {
        var gerber = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var dxf    = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var gdsii  = new Dictionary<(int Layer, int Datatype), List<string>>();

        foreach (var layer in tech.Layers)
        {
            if (layer.Interchange is not { } map) continue;

            if (map.GerberSuffix is { Length: > 0 } suffix)
                Claim(gerber, suffix, layer.Name);

            if (map.DxfLayerName is { Length: > 0 } dxfName)
                Claim(dxf, dxfName, layer.Name);

            // A GDSII alias is only a collision when BOTH halves are stated: one half alone still
            // falls back to the layer's own Layer/Datatype for the other, which the layer-table key
            // check above already covers.
            if (map.GdsiiLayer is { } gl && map.GdsiiDatatype is { } gd)
                Claim(gdsii, (gl, gd), layer.Name);
        }

        foreach (var (suffix, names) in gerber.Where(e => e.Value.Count > 1))
            problems.Add(new(TechProblemArea.Interchange,
                $"{Sharers(names)} share the Gerber suffix \"{suffix}\" — they would write to the same file."));

        foreach (var (dxfName, names) in dxf.Where(e => e.Value.Count > 1))
            problems.Add(new(TechProblemArea.Interchange,
                $"{Sharers(names)} share the DXF layer name \"{dxfName}\" — they would merge on " +
                "export and are indistinguishable on import."));

        foreach (var (alias, names) in gdsii.Where(e => e.Value.Count > 1))
            problems.Add(new(TechProblemArea.Interchange,
                $"{Sharers(names)} share the GDSII alias ({alias.Layer},{alias.Datatype})."));
    }

    private static void Claim<TKey>(Dictionary<TKey, List<string>> claims, TKey value, string layerName)
        where TKey : notnull
    {
        if (!claims.TryGetValue(value, out var names)) claims[value] = names = [];
        names.Add(layerName);
    }

    /// <summary>"Layers "A" and "B"" for a pair, "21 layers ("A", "B", "C" and 18 more)" for a crowd —
    /// the names are what identifies the offenders, and past three of them the COUNT is the fact that
    /// matters. Always names at least three, so the message stays a lead rather than a statistic.</summary>
    private static string Sharers(List<string> names)
    {
        var quoted = names.Select(n => $"\"{n}\"").ToList();
        if (quoted.Count == 2) return $"Layers {quoted[0]} and {quoted[1]}";
        if (quoted.Count == 3) return $"Layers {quoted[0]}, {quoted[1]} and {quoted[2]}";
        return $"{names.Count} layers ({quoted[0]}, {quoted[1]}, {quoted[2]} and {names.Count - 3} more)";
    }

    /// <summary>
    /// Checks one of a rule's region expressions: it must parse, and every layer it names must be
    /// defined. Blank is fine — that means "the rule's own layer", which is what a hand-authored
    /// rule says.
    /// </summary>
    private static void ValidateRegion(
        DrcRule rule, string? text, string which,
        HashSet<LayerKey> knownLayers, List<TechProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!Drc.DrcLayerExprParser.TryParse(text, out var expr, out string? error) || expr is null)
        {
            problems.Add(new(TechProblemArea.Drc,
                $"DRC rule \"{rule.Name}\" has an unreadable {which} region: {error}"));
            return;
        }

        foreach (var key in expr.ReferencedLayers())
            if (!knownLayers.Contains(key))
                problems.Add(new(TechProblemArea.Drc,
                    $"DRC rule \"{rule.Name}\" {which} region references unknown layer " +
                    $"({key.Layer},{key.Datatype})."));
    }
}
