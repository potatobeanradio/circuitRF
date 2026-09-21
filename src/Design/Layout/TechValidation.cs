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

        // ── GI2 R-gi2-9 — the SKELETON state, reported once ──────────────────────────────────────
        //
        // The same rule as the one above, applied to the state GI2's skeleton creates: entries exist,
        // in the right order, bound to their drawing layers, and every substrate VALUE is still unset.
        // Without this, a six-layer skeleton engages every per-row check at once — eleven "non-positive
        // thickness" problems and five "εr < 1" — which is the 22-message wall arriving by a different
        // door, and it would make the skeleton a regression rather than a head start.
        //
        // R-gi2-11: the distinction that matters is UNSET versus WRONG. Exactly zero on a freshly
        // imported row is unset and is summarised here; a NEGATIVE thickness, or a permittivity
        // someone typed as 0.5, is wrong and is still reported on its own row below. That is also what
        // makes the summary progressive (gate 5): filling in one dielectric's thickness and εr drops
        // it out of both counts and raises nothing new.
        var substrateRows = tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via).ToList();
        int unsetThickness = substrateRows.Count(l => l.ThicknessDbu == 0);
        int unsetEpsr = substrateRows.Count(l => l.Kind == StackupKind.Dielectric && l.Epsr == 0);
        bool stackupIsSkeleton = substrateRows.Count > 0 && (unsetThickness > 0 || unsetEpsr > 0);
        if (stackupIsSkeleton)
        {
            int conductorRows = substrateRows.Count(l => l.Kind == StackupKind.Conductor);
            int dielectricRows = substrateRows.Count - conductorRows;
            var still = new List<string>();
            if (unsetThickness > 0)
                still.Add($"{unsetThickness} still need{(unsetThickness == 1 ? "s" : "")} a thickness");
            if (unsetEpsr > 0)
                still.Add($"{unsetEpsr} dielectric(s) still need a relative permittivity and a loss tangent");

            problems.Add(new(TechProblemArea.Stackup,
                $"The stackup's structure is in place — {conductorRows} conductor(s) and {dielectricRows} " +
                $"dielectric(s) — but its substrate values are not: {string.Join(" and ", still)}. Fill " +
                "them in on the Stackup tab; nothing will simulate until they are set. (A Gerber import " +
                "with no job file creates exactly this shape: the artwork states the layers and their " +
                "order, and states nothing at all about the substrate.)"));
        }

        // ── R-rail27-1c — a conductor with no drawing layer, so no artwork sits on it ────────────
        //
        // Reported from the field, 2026-09-21: a Gerber set's inner plane was classified as a
        // `drawing` layer (its file was named for the NET it carries, which matches no copper
        // pattern), the designer added the conductor to the stackup by hand, and nothing anywhere
        // said the two were never joined. railRF's reference combo now LISTS such a conductor and
        // says what is missing; this is the same fact reaching somebody who never opens railRF —
        // the Technology editor, `circuitrf check`, and every other validation surface at once.
        //
        // A WARNING, never an error: a stackup skeleton legitimately has such entries before the
        // artwork arrives, so `circuitrf check` still exits 0 (every TechProblem lands there as a
        // warning already).
        //
        // ── AND WHY ONLY AN *INNER* CONDUCTOR ────────────────────────────────────────────────────
        //
        // An OUTERMOST conductor with no drawing layer is BLANKET METAL, which is an ordinary
        // construction and not a defect: `mmic-GaAs_2LM_100um`'s `Backside Metal` is exactly that —
        // the whole die backside, unpatterned, with no artwork to point at and nothing missing.
        // An inner one cannot be blanket, whatever the process: unpatterned metal in the middle of a
        // stack shorts every via that passes through it. So an inner conductor claiming no drawing
        // layer is always artwork that was never attached.
        var conductorEntries = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor)
            .ToList();

        for (int i = 1; i < conductorEntries.Count - 1; i++)
        {
            var inner = conductorEntries[i];
            if (inner.DrawingLayers.Count > 0) continue;

            problems.Add(new(TechProblemArea.Stackup,
                $"Conductor \"{inner.Name}\" claims no drawing layer, so no artwork sits on it: it is " +
                "priced by nothing, extracted by nothing, and cannot be named as a reference return. " +
                "Attach the drawing layer that carries this plane's copper on the Stackup tab."));
        }

        // ── GI3 R-gi3-8 — two entries with one name ───────────────────────────────────────────────
        //
        // SpanFromLayer, SpanToLayer and PresentWithLayer all resolve a stackup entry BY NAME, and the
        // sets they resolve against are HashSets — so two conductors sharing a name collapse to one,
        // every reference to that name becomes ambiguous, and until now nothing said a word about it.
        // Hand-authored stackups grow duplicates easily: the natural names for the layers between six
        // copper sheets repeat.
        //
        // Reported ONCE PER DUPLICATED NAME, naming every entry that carries it — this file's own
        // one-problem-per-cause rule. A via spanning the ambiguous name is deliberately NOT reported as
        // well: the name IS in the conductor set, so the span check below passes, and a second message
        // about a consequence would bury the cause.
        //
        // Dielectrics and vias are held to the same rule even though nothing references either by name
        // today. The cost of allowing a duplicate is not paid by whoever creates it; it is paid by
        // whoever adds the next name-based reference.
        foreach (var group in tech.Stackup.Layers
                     .Select((l, i) => (Layer: l, Index: i))
                     .GroupBy(e => e.Layer.Name, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var entries = group.ToList();
            problems.Add(new(TechProblemArea.Stackup,
                $"{entries.Count} stackup entries share the name \"{group.Key}\" — " +
                string.Join(", ", entries.Select(e => $"#{e.Index + 1} ({e.Layer.Kind})")) +
                ". A via's span, a patterned dielectric's plate and every other name-based reference " +
                "in a technology resolves to the FIRST entry with that name, so the rest are " +
                "unreachable and nothing reports it. Give each entry its own name."));
        }

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

            // Epsr == 0 is UNSET (GI2's skeleton spelling) and is summarised above; anything else
            // below 1 is a value someone entered that no material has.
            if (sl.Kind == StackupKind.Dielectric && sl.Epsr < 1 && !(stackupIsSkeleton && sl.Epsr == 0))
                problems.Add(new(TechProblemArea.Stackup,
                    $"Stackup layer \"{sl.Name}\" is a dielectric with εr < 1 ({sl.Epsr})."));

            // R-via-3/R-via-2 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): a Via entry is a
            // vertical connector, not a horizontal layer at one z — it has no independent thickness of
            // its own (it traverses whatever dielectric(s) separate the conductors it spans), so the
            // "must have positive thickness" rule below applies only to Dielectric/Conductor entries.
            //
            // R-gi2-11: zero is unset and is summarised above; negative is wrong and is always its
            // own problem, because nobody imports a negative thickness — someone typed it.
            if (sl.Kind != StackupKind.Via && sl.ThicknessDbu <= 0 &&
                !(stackupIsSkeleton && sl.ThicknessDbu == 0))
                problems.Add(new(TechProblemArea.Stackup,
                    $"Stackup layer \"{sl.Name}\" has non-positive thickness ({sl.ThicknessDbu} DBU)."));

            // MIM-7 — a dielectric that is patterned rather than laterally continuous. Two hard
            // rules; the third thing worth saying is a RECOMMENDATION and is stated in the field's
            // own documentation and in the editor's tooltip rather than failed here: name the
            // conductor directly ABOVE the dielectric, because that is the plate the film is
            // deposited under. Tying it to a conductor further away is legal, honoured, and only
            // harder to read.
            //
            // MIM-11 — the name resolves in TWO namespaces now: a CONDUCTOR stackup entry, as
            // before and with priority, or a DRAWING LAYER, which is the mask that actually defines
            // where a thin film exists. Both are legal spellings of the same tie and only the
            // unresolvable third case is a problem. The ground-reference rule is asked of the
            // conductor namespace ALONE — a drawing layer is not a plane and cannot be one.
            if (sl.PresentWithLayer is { Length: > 0 } plate)
            {
                bool namesAConductor = conductorNames.Contains(plate);
                bool namesADrawingLayer = tech.Layers.Any(l => l.Name == plate);

                if (sl.Kind != StackupKind.Dielectric)
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Stackup layer \"{sl.Name}\" is a {sl.Kind} entry but names \"{plate}\" " +
                        "as what it is patterned with. Only a Dielectric entry can be a " +
                        "patterned thin film."));
                else if (!namesAConductor && !namesADrawingLayer)
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Dielectric stackup layer \"{sl.Name}\" is patterned with \"{plate}\", " +
                        "which is neither a conductor in this stackup nor one of this technology's " +
                        "drawing layers. Name the plate the film is deposited under, or the mask " +
                        "layer that defines it."));
                else if (namesAConductor &&
                         tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor &&
                                                      l.Name == plate && l.IsGroundReference))
                    problems.Add(new(TechProblemArea.Stackup,
                        $"Dielectric stackup layer \"{sl.Name}\" is patterned with \"{plate}\", " +
                        "which is the ground reference. A ground plane is never an analysis level, " +
                        "so this dielectric could never be present in any run — name the signal " +
                        "conductor whose artwork the film is deposited under."));
            }

            // GI1 R-gi1-2/R-gi1-3: a via entry that is NOT PLATED connects nothing, by definition —
            // it is a hole, not a vertical conductor — so a missing span is its correct state and not
            // a problem to report. The same is true of a routed layer, which reaches this as
            // Plated == false for exactly that reason. Its drawing-layer binding and its thickness
            // rule are still checked above; only the two span questions and the wall-thickness one
            // stop applying, because all three are questions about metal.
            if (sl.Kind == StackupKind.Via && !stackupIsSubstrateless && sl.Plated != false)
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
