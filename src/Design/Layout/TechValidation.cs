// Validates a Technology without ever throwing — a bad tech warns and still lets you edit
// (docs/design/layout-view.md §2.4 "Missing tech file").

using System.Linq;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout;

/// <summary>Which editor tab a <see cref="TechProblem"/> belongs to — the tab whose fields are the
/// ones that would fix it. The Technology editor shows the ACTIVE tab's problems only (a Gerber
/// suffix collision has nothing to say to someone editing the layer table), and counts the rest on
/// their own tab headers so nothing is hidden.</summary>
public enum TechProblemArea { Layers, Stackup, Drc, Interchange }

/// <summary>One technology-consistency problem, and the tab that owns it.</summary>
/// <param name="Id">A stable rule id (<c>tech.material.unknown</c>), or null for the problems that
/// predate ids — which <c>check</c> reports under its one <c>check.tech.problem</c> id either way,
/// carrying this as its <c>rule</c> argument.</param>
/// <param name="Severity">How serious. <b>Warning is what every problem without an id has always
/// been</b>, so the default changes nothing; brief-em3d-2's material and body rules state their
/// own.</param>
public sealed record TechProblem(
    TechProblemArea Area, string Message, TechFix? Fix = null,
    string? Id = null, DiagnosticSeverity Severity = DiagnosticSeverity.Warning);

/// <summary>
/// A one-step repair a problem can offer — attach <paramref name="Layer"/> to the conductor named
/// <paramref name="ConductorName"/>. Offered only where exactly one layer is the plausible answer,
/// so pressing it is never a guess the user did not see.
/// </summary>
/// <param name="Label">What the button says.</param>
public sealed record TechFix(string Label, string ConductorName, LayerKey Layer);

public static class TechValidation
{
    /// <summary>
    /// The one drawing layer that is plausibly <paramref name="conductor"/>'s copper, or null where
    /// there is no single answer.
    /// </summary>
    /// <remarks>
    /// <b>A SUGGESTION, never a stackup decision</b> — it decides the words of a message and what a
    /// button offers, and nothing is attached until somebody presses it. The candidates are the
    /// layers no Conductor or Via entry claims, less the drill layers and the artwork kinds a
    /// fabrication set always carries (<c>GerberLayerCascade.IsNonConductorArtwork</c>, suffix
    /// included). One whose name matches the conductor's, ignoring case, is the answer; failing that,
    /// a sole candidate is — but only where this is the one inner conductor waiting for a layer: with
    /// two (GND and PWR, one unclaimed `gnd`), the sole candidate was offered to both, and pressing
    /// both buttons attached one plane's copper to two conductors. Anything else is no answer at all.
    /// </remarks>
    public static LayerDef? LikelyDrawingLayerFor(Technology tech, StackupLayer conductor)
    {
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(conductor);

        var claimed = tech.Stackup.Layers
            .Where(l => l.Kind is StackupKind.Conductor or StackupKind.Via)
            .SelectMany(l => l.DrawingLayers)
            .ToHashSet();

        var candidates = tech.Layers
            .Where(l => !claimed.Contains(l.Key))
            .Where(l => !string.Equals(l.Purpose, Interchange.GerberLayerCascade.DrillPurpose, StringComparison.Ordinal))
            .Where(l => !Interchange.GerberLayerCascade.IsNonConductorArtwork(
                l.Interchange?.GerberFileFunction, l.Name, l.Interchange?.GerberSuffix))
            .ToList();

        var byName = candidates
            .Where(l => string.Equals(l.Name?.Trim(), conductor.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byName.Count == 1) return byName[0];
        if (byName.Count > 0 || candidates.Count != 1) return null;

        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        bool anotherWaiting = conductors
            .Skip(1).Take(Math.Max(0, conductors.Count - 2))
            .Any(l => !ReferenceEquals(l, conductor) && l.DrawingLayers.Count == 0);
        return anotherWaiting ? null : candidates[0];
    }

    /// <summary>The messages alone, in <see cref="Analyze"/>'s order — the long-standing shape of
    /// this API, kept for every caller that only wants to say what is wrong. <b>Info findings are
    /// left out</b> (brief-em3d-2): they say what the technology does, not what is wrong with it, and
    /// every caller here counts or posts what it gets as problems.</summary>
    public static IReadOnlyList<string> Validate(Technology tech)
        => [.. Analyze(tech).Where(p => p.Severity != DiagnosticSeverity.Info).Select(p => p.Message)];

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

            // Field report, 2026-09-22: the designer's answer to this message was "I have a gnd
            // layer". He did — a drawing layer named `gnd`, which the conductor `GND` had never been
            // joined to, and nothing here said the two were about each other. Where one layer is the
            // plausible answer it is NAMED, and the editor offers to attach it.
            var likely = LikelyDrawingLayerFor(tech, inner);
            problems.Add(new(TechProblemArea.Stackup,
                $"Conductor \"{inner.Name}\" claims no drawing layer, so no artwork sits on it: it is " +
                "priced by nothing, extracted by nothing, and cannot be named as a reference return. " +
                (likely is null
                    ? "Attach the drawing layer that carries this plane's copper on the Stackup tab — " +
                      "the picker is at the foot of the conductor's card."
                    : $"Drawing layer '{likely.Name}' is claimed by no conductor and is very likely " +
                      "this plane's copper: attach it to this conductor."),
                likely is null ? null : new TechFix($"Attach '{likely.Name}' to {inner.Name}", inner.Name, likely.Key)));
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

        ValidateDeviceRules(tech, knownLayers, problems);
        ValidateInterchange(tech, problems);
        ValidateMaterials(tech, problems);
        ValidateBodies(tech, knownLayers, problems);

        return problems;
    }

    // ── Named materials and 3D bodies (brief-em3d-2 R-em3d2-4) ─────────────────────────────────
    //
    // Under the Stackup area: the stackup card's material picker is where a named material is
    // chosen, and there is no Materials or Bodies tab yet (R-em3d2-5d). A problem with no tab behind
    // it would count on no header and be reported by `check` alone.

    /// <summary>The ids of brief-em3d-2's rules — one place, so a test and a reader spell them
    /// identically.</summary>
    public static class Ids
    {
        public const string MaterialUnknown         = "tech.material.unknown";
        public const string MaterialDuplicate       = "tech.material.duplicate";
        public const string MaterialMissingProperty = "tech.material.missing-property";
        public const string MaterialPartial         = "tech.material.partial";
        public const string MaterialDisagrees       = "tech.material.disagrees";
        public const string MaterialInvalid         = "tech.material.invalid";
        public const string MaterialNotReadYet      = "tech.material.not-read-yet";
        public const string BodySitsOnUnknown       = "tech.body.sits-on-unknown";
        public const string BodyNameClash           = "tech.body.name-clash";
        public const string BodyOutlineLayerUnknown = "tech.body.outline-layer-unknown";
    }

    private static TechProblem Material(string id, DiagnosticSeverity severity, string message)
        => new(TechProblemArea.Stackup, message, Id: id, Severity: severity);

    private static string MaterialList(Technology tech)
        => tech.Materials.Count == 0
            ? "This technology defines no materials."
            : "Defined materials: " + string.Join(", ", tech.Materials.Select(m => $"\"{m.Name}\"")) + ".";

    private static void ValidateMaterials(Technology tech, List<TechProblem> problems)
    {
        // ── tech.material.duplicate — one problem per duplicated NAME, naming how many share it ───
        foreach (var group in tech.Materials
                     .GroupBy(m => m.Name ?? "", StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
            problems.Add(Material(Ids.MaterialDuplicate, DiagnosticSeverity.Error,
                $"{group.Count()} materials are named \"{group.Key}\" (material names ignore case). " +
                "A stackup entry naming it could mean any of them; rename all but one."));

        // ── the material's own shape: a tensor of three, and the temperature tables ─────────────
        foreach (var m in tech.Materials)
        {
            if (m.EpsrTensor is { } tensor
                && (tensor.Length != 3 || tensor.Any(v => !double.IsFinite(v) || v < 1)))
                problems.Add(Material(Ids.MaterialInvalid, DiagnosticSeverity.Error,
                    $"Material \"{m.Name}\" states an εr tensor that is not three finite values of at " +
                    "least 1 (xx, yy, zz)."));

            ValidateTemperatureTable(m, m.SigmaVsTemp, "conductivity against temperature (SigmaVsTemp)",
                "every solver uses its conductivity at 20 °C (Sigma20)" +
                (m.Alpha20 is not null ? " and, for a bond wire, its temperature coefficient (Alpha20)" : ""),
                problems);
            ValidateTemperatureTable(m, m.ThermalKVsTemp, "thermal conductivity against temperature (ThermalKVsTemp)",
                "no thermal solver exists yet", problems);
        }

        // ── Use: every stackup entry that names one ─────────────────────────────────────────────
        foreach (var layer in tech.Stackup.Layers)
        {
            if (layer.Material is null) continue;
            string what = layer.Kind switch
            {
                StackupKind.Dielectric => "Dielectric",
                StackupKind.Conductor  => "Conductor",
                _                      => "Via",
            };

            if (tech.FindMaterial(layer.Material) is not { } m)
            {
                problems.Add(Material(Ids.MaterialUnknown, DiagnosticSeverity.Error,
                    $"{what} entry \"{layer.Name}\" names material \"{layer.Material}\", which this " +
                    $"technology does not define, so the entry's own numbers are used. {MaterialList(tech)}"));
                continue;
            }

            if (layer.Kind == StackupKind.Dielectric)
            {
                if (m.Epsr is null)
                {
                    problems.Add(Material(Ids.MaterialMissingProperty, DiagnosticSeverity.Error,
                        $"Dielectric entry \"{layer.Name}\" names material \"{m.Name}\", which states no " +
                        "εr (Epsr) — a dielectric needs one. The entry's own εr is used."));
                    continue;
                }

                // R-em3d2-2c: never silent — an entry partly defined by its material says which half.
                var own = new List<string>();
                if (m.TanD is null) own.Add("tanδ");
                if (m.Mur  is null) own.Add("µr");
                if (own.Count > 0)
                    problems.Add(Material(Ids.MaterialPartial, DiagnosticSeverity.Info,
                        $"Dielectric entry \"{layer.Name}\" takes εr from material \"{m.Name}\"; its " +
                        $"{string.Join(" and ", own)} {(own.Count == 1 ? "is" : "are")} the entry's own, " +
                        "because the material does not state " + (own.Count == 1 ? "it." : "them.")));
            }
            else if (m.Sigma20 is null)
            {
                problems.Add(Material(Ids.MaterialMissingProperty, DiagnosticSeverity.Error,
                    $"{what} entry \"{layer.Name}\" names material \"{m.Name}\", which states no " +
                    "conductivity at 20 °C (Sigma20) — a conductor needs one. The entry's own σ is used."));
            }
        }

        // ── Use: every body ─────────────────────────────────────────────────────────────────────
        foreach (var body in tech.Bodies)
        {
            if (tech.FindMaterial(body.Material) is not { } m)
            {
                problems.Add(Material(Ids.MaterialUnknown, DiagnosticSeverity.Error,
                    string.IsNullOrEmpty(body.Material)
                        ? $"Body \"{body.Name}\" names no material; a body needs one. {MaterialList(tech)}"
                        : $"Body \"{body.Name}\" names material \"{body.Material}\", which this technology " +
                          $"does not define. {MaterialList(tech)}"));
                continue;
            }
            if (m.Epsr is null && m.Sigma20 is null)
                problems.Add(Material(Ids.MaterialMissingProperty, DiagnosticSeverity.Error,
                    $"Body \"{body.Name}\" names material \"{m.Name}\", which states neither εr (Epsr) " +
                    "nor conductivity at 20 °C (Sigma20), so nothing says what the body is."));
        }
    }

    /// <summary>
    /// The owner's placeholders for temperature-dependent conductivity (2026-09-25): a table is
    /// validated as a table — finite, above absolute zero, positive, strictly increasing in
    /// temperature — and, being read by nothing yet, is reported at info so a stated table never
    /// looks as though it were in force.
    /// </summary>
    private static void ValidateTemperatureTable(
        TechMaterial m, List<TechTemperaturePoint>? table, string what, string instead,
        List<TechProblem> problems)
    {
        if (table is not { Count: > 0 }) return;

        string? fault = null;
        for (int i = 0; i < table.Count && fault is null; i++)
        {
            var p = table[i];
            if (!double.IsFinite(p.TempC) || !double.IsFinite(p.Value))
                fault = $"point {i + 1} is not a finite number";
            else if (p.TempC < -273.15)
                fault = $"point {i + 1} is below absolute zero ({p.TempC} °C)";
            else if (p.Value <= 0)
                fault = $"point {i + 1} has a value of zero or less ({p.Value})";
            else if (i > 0 && p.TempC <= table[i - 1].TempC)
                fault = $"its temperatures do not strictly increase (point {i + 1}, {p.TempC} °C, " +
                        $"follows {table[i - 1].TempC} °C)";
        }

        if (fault is not null)
            problems.Add(Material(Ids.MaterialInvalid, DiagnosticSeverity.Error,
                $"Material \"{m.Name}\" states {what}, but {fault}."));

        problems.Add(Material(Ids.MaterialNotReadYet, DiagnosticSeverity.Info,
            $"Material \"{m.Name}\" states {what} ({table.Count} point{(table.Count == 1 ? "" : "s")}). " +
            $"It is carried in the file and read by nothing yet: {instead}."));
    }

    private static void ValidateBodies(
        Technology tech, HashSet<LayerKey> knownLayers, List<TechProblem> problems)
    {
        if (tech.Bodies.Count == 0) return;

        var entryNames = new HashSet<string>(tech.Stackup.Layers.Select(l => l.Name), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var body in tech.Bodies)
        {
            if (!seen.Add(body.Name))
                problems.Add(Material(Ids.BodyNameClash, DiagnosticSeverity.Error,
                    $"Two bodies are named \"{body.Name}\". A body's name is how the 3D problem names its " +
                    "solid, so it must be unique."));
            else if (entryNames.Contains(body.Name))
                problems.Add(Material(Ids.BodyNameClash, DiagnosticSeverity.Error,
                    $"Body \"{body.Name}\" has the same name as a stackup entry. A body's name is how the " +
                    "3D problem names its solid, so it must differ from every entry's."));

            if (!entryNames.Contains(body.SitsOn))
                problems.Add(Material(Ids.BodySitsOnUnknown, DiagnosticSeverity.Error,
                    $"Body \"{body.Name}\" sits on \"{body.SitsOn}\", which is not a stackup entry. " +
                    "Entries: " + string.Join(", ", tech.Stackup.Layers.Select(l => $"\"{l.Name}\"")) + "."));

            foreach (var key in body.OutlineLayers)
                if (!knownLayers.Contains(key))
                    problems.Add(Material(Ids.BodyOutlineLayerUnknown, DiagnosticSeverity.Error,
                        $"Body \"{body.Name}\" takes its outline from layer ({key.Layer},{key.Datatype}), " +
                        "which this technology does not define."));
        }
    }

    /// <summary>
    /// brief-em3d-2 R-em3d2-4a — <b>the one rule that reads the file as written</b>:
    /// <c>tech.material.disagrees</c>, a stackup entry whose own numbers differ from the material it
    /// names.
    ///
    /// <para>It needs the technology from <see cref="TechPersistence.DeserializeUnresolved"/>.
    /// After an ordinary load every named entry's numbers have been overwritten with its material's,
    /// so the rule could never fire there — an inert rule, which is exactly what the design note
    /// forbids ("an edited number that nothing reads must not pass silently"). The editor keeps the
    /// two equal on save, so the warning means a hand edit.</para>
    /// </summary>
    public static IReadOnlyList<TechProblem> AnalyzeRaw(Technology raw)
    {
        var problems = new List<TechProblem>();
        foreach (var layer in raw.Stackup.Layers)
        {
            if (raw.FindMaterial(layer.Material) is not { } m) continue;

            var differ = new List<string>();
            if (layer.Kind == StackupKind.Dielectric)
            {
                if (m.Epsr is { } e && !Same(layer.Epsr, e)) differ.Add($"εr {Num(layer.Epsr)} vs {Num(e)}");
                if (m.TanD is { } t && !Same(layer.TanD, t)) differ.Add($"tanδ {Num(layer.TanD)} vs {Num(t)}");
                if (m.Mur  is { } u && !Same(layer.Mur,  u)) differ.Add($"µr {Num(layer.Mur)} vs {Num(u)}");
            }
            else if (m.Sigma20 is { } sg && !Same(layer.SigmaSm, sg))
            {
                differ.Add($"σ {Num(layer.SigmaSm)} vs {Num(sg)}");
            }

            if (differ.Count > 0)
                problems.Add(Material(Ids.MaterialDisagrees, DiagnosticSeverity.Warning,
                    $"Stackup entry \"{layer.Name}\" names material \"{m.Name}\" but its own numbers differ " +
                    $"(entry vs material: {string.Join("; ", differ)}). The material's values are the ones " +
                    "used; the entry's are what a build older than named materials would read. Saving from " +
                    "the Technology editor makes them agree."));
        }
        return problems;

        static bool Same(double a, double b)
            => a == b || Math.Abs(a - b) <= 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));
        static string Num(double v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── The device-recognition deck (brief-lvs-14-recognition.md R-lvs14-2c/2d) ───────────────
    //
    // R-lvs14-2c: a rule that will not parse is a `check` ERROR, before any run. `check` already
    // calls Analyze for a technology, so the deck joins the validator table the same way
    // DrcPredicateParser did — and for the identical reason. A deck that fails at run time instead
    // is a deck that fails during the one operation the user wanted to succeed.
    //
    // ── WHY THESE LAND UNDER `Drc` AND NOT UNDER AN AREA OF THEIR OWN ─────────────────────────
    //
    // TechProblemArea names the EDITOR TAB whose fields would fix the problem, and R-lvs14-4d says
    // this brief ships no rule-authoring UI. A `Devices` member with no tab behind it would count
    // on no tab header and would therefore be invisible in the editor — reported by `check` and
    // nowhere else, which is the half-visible state the enum exists to prevent. The deck is
    // hand-edited beside DrcRules, in the same file and in the same layer grammar, so the DRC Rules
    // tab is where someone editing one is already looking.

    /// <summary>
    /// Every way a <see cref="DeviceRule"/> or a <see cref="TechConstant"/> can be unusable, said
    /// before any run reads one.
    /// </summary>
    /// <remarks>
    /// <b>The constants are EVALUATED here, not merely parsed.</b> A constant can refer to another
    /// one, so a cycle is representable — and it is the expression engine's own cycle detection
    /// that finds it (R-lvs14-2b), through the same <c>Evaluator.Resolve</c> every other binding in
    /// circuitRF resolves through. Nothing about a constant depends on geometry, so the whole
    /// question is answerable statically and there is no reason to leave it to a run.
    /// </remarks>
    private static void ValidateDeviceRules(
        Technology tech, HashSet<LayerKey> knownLayers, List<TechProblem> problems)
    {
        // ── The constants ────────────────────────────────────────────────────────────────────
        var scope = new Core.Expressions.Scope("technology");
        foreach (var constant in tech.Constants)
        {
            if (constant.Name is not { Length: > 0 })
            {
                problems.Add(new(TechProblemArea.Drc, "A technology constant has no name."));
                continue;
            }
            scope.Bind(constant.Name, constant.Expression ?? "", NormalizeUnit(constant.Unit));
        }

        foreach (var constant in tech.Constants)
        {
            if (constant.Name is not { Length: > 0 }) continue;
            try { new Core.Expressions.Evaluator().Resolve(constant.Name, scope); }
            catch (Exception ex)
            {
                problems.Add(new(TechProblemArea.Drc,
                    $"Technology constant \"{constant.Name}\" does not evaluate: {ex.Message}"));
            }
        }

        var declared = tech.Constants
            .Where(c => c.Name is { Length: > 0 })
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        // ── The rules ────────────────────────────────────────────────────────────────────────
        foreach (var rule in tech.DeviceRules)
        {
            string name = rule.Name is { Length: > 0 } n ? n : "(unnamed)";
            if (rule.Name is not { Length: > 0 })
                problems.Add(new(TechProblemArea.Drc, "A device rule has no name."));

            // R-lvs14-2d. An unknown kind LISTS the real ones and is never a fallback — `--tech`'s
            // own rule. A fallback here would recognise a resistor as something else and then match
            // it against nothing, with no line saying why.
            if (!Lvs.DeviceRecognition.TryParseKind(rule.Kind, out _))
                problems.Add(new(TechProblemArea.Drc,
                    $"Device rule \"{name}\" declares kind \"{rule.Kind}\", which is not one of: " +
                    $"{Lvs.DeviceRecognition.KindNames}."));

            ValidateDeviceRegion(name, rule.Body, "body", knownLayers, problems);

            if (rule.Terminals.Count == 0)
                problems.Add(new(TechProblemArea.Drc,
                    $"Device rule \"{name}\" names no terminal layers, so nothing it recognised " +
                    "could ever be connected to anything."));

            for (int i = 0; i < rule.Terminals.Count; i++)
                ValidateDeviceRegion(name, rule.Terminals[i], $"terminal {i + 1}", knownLayers, problems);

            foreach (var (parameter, formula) in rule.Parameters)
            {
                Core.Expressions.Expr ast;
                try { ast = Core.Expressions.Parser.Parse(formula ?? ""); }
                catch (Exception ex)
                {
                    problems.Add(new(TechProblemArea.Drc,
                        $"Device rule \"{name}\" parameter {parameter} does not parse: {ex.Message}"));
                    continue;
                }

                // Every name a formula may use is known before any geometry exists: the four
                // measured quantities, and the constants this technology declares. A typo caught
                // here is a typo that never becomes a device with no value and no explanation.
                foreach (string reference in Core.Expressions.AstWalker.CollectRefs(ast))
                    if (!Lvs.DeviceRecognition.Measured.Contains(reference) && !declared.Contains(reference))
                        problems.Add(new(TechProblemArea.Drc,
                            $"Device rule \"{name}\" parameter {parameter} refers to \"{reference}\", " +
                            $"which is neither a measured quantity ({Lvs.DeviceRecognition.MeasuredNames}) " +
                            "nor a constant this technology declares."));
            }
        }
    }

    /// <summary>One of a device rule's region expressions: it must parse, and every layer it names
    /// must be defined. <see cref="ValidateRegion"/>'s twin, naming a device rule instead.</summary>
    private static void ValidateDeviceRegion(
        string ruleName, string? text, string which,
        HashSet<LayerKey> knownLayers, List<TechProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add(new(TechProblemArea.Drc,
                $"Device rule \"{ruleName}\" states no {which} region."));
            return;
        }

        if (!Drc.DrcLayerExprParser.TryParse(text, out var expr, out string? error) || expr is null)
        {
            problems.Add(new(TechProblemArea.Drc,
                $"Device rule \"{ruleName}\" has an unreadable {which} region: {error}"));
            return;
        }

        foreach (var key in expr.ReferencedLayers())
            if (!knownLayers.Contains(key))
                problems.Add(new(TechProblemArea.Drc,
                    $"Device rule \"{ruleName}\" {which} region references unknown layer " +
                    $"({key.Layer},{key.Datatype})."));
    }

    /// <summary>A unit as the expression engine spells one, or null where none was stated.</summary>
    private static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        string engine = Core.Expressions.UnitNormalizer.ToEngineUnit(unit);
        return engine.Length > 0 ? engine : null;
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
