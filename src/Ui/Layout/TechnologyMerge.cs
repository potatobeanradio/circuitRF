// Combining technologies section by section (docs/design/layout-view.md §2.4a).
//
// <b>The problem this solves.</b> Before it, a technology was all-or-nothing: importing a process
// produced a whole new `.ctech`, and importing again over the same name silently replaced the file —
// hand-authored rules, edited colours, a chosen ground reference, all gone with no prompt and no
// record. There was no way to take one process's DRC rules into a technology you had already tuned,
// no way to reuse a layer table across workspaces, and no way to send someone your rules.
//
// <b>Why there is no new file format.</b> A `.ctech` with only DRC rules populated is a valid
// `.ctech` — it loads, it round-trips, and it is obviously a technology file to anyone who opens it.
// Inventing a `.cdrc` would mean a second format, a second reader, a second version field and a
// second set of bugs, to express something the format already expresses. "Export my rules" writes a
// `.ctech`; "import just the rules" reads one and takes one section.

using CircuitRF.Ui.Layout.Assembly;

namespace CircuitRF.Ui.Layout;

/// <summary>Which parts of a technology an operation applies to.</summary>
[Flags]
public enum TechSection
{
    None      = 0,
    Layers    = 1,
    Stackup   = 2,
    DrcRules  = 4,

    /// <summary>
    /// A `.wasm`'s assembly rules (wbond.md §8, WB31). Deliberately NOT part of
    /// <see cref="All"/> — a <see cref="Technology"/> holds none of these, and folding them into the
    /// "everything" flag would make every existing technology merge ask a question about a document
    /// it has no relationship to. It is the same enum because it is the same operation with the same
    /// modes, the same conflict record and the same report; it is a separate FLAG because assembly
    /// rules live in a separate document, which is the whole of WB31.
    /// </summary>
    AssemblyRules = 8,

    All       = Layers | Stackup | DrcRules,
}

/// <summary>What to do when both technologies carry an item with the same identity.</summary>
public enum TechMergeMode
{
    /// <summary>The incoming item wins.</summary>
    Replace,

    /// <summary>
    /// The existing item wins; only genuinely new items are added.
    ///
    /// <para>This is the default for a REASON. A user who has tuned a technology and then imports a
    /// process update almost never wants their own edits silently reverted — and unlike a bad merge,
    /// a missed update is visible (the value is simply the old one) and fixable. Replace is offered,
    /// but it is not what happens when someone clicks through without reading.</para>
    /// </summary>
    AddMissingOnly,

    /// <summary>
    /// The user decides item by item, from a list of the actual collisions.
    ///
    /// <para>A blanket policy answers "what usually happens"; this answers "what about THIS rule".
    /// A process update typically changes a handful of values out of a hundred, and the user often
    /// wants most of them and not the two they deliberately tuned — which neither blanket answer
    /// expresses.</para>
    /// </summary>
    Selective,
}

/// <summary>
/// One item present in both technologies, with enough of each side to choose between them.
/// </summary>
/// <param name="Key">
/// Stable identity, unique across sections — what a caller ticks to say "replace this one".
/// Section-qualified because a layer and a rule may legitimately share a name.
/// </param>
public sealed record TechMergeConflict(
    TechSection Section, string Key, string Label, string Mine, string Theirs);

/// <summary>What a merge did, per section. Every number is reported to the user.</summary>
public sealed record TechMergeReport(
    int LayersAdded,   int LayersReplaced,   int LayersKept,
    int StackupAdded,  int StackupReplaced,  int StackupKept,
    int RulesAdded,    int RulesReplaced,    int RulesKept,
    IReadOnlyList<string> Warnings)
{
    public static readonly TechMergeReport Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    public int TotalAdded    => LayersAdded + StackupAdded + RulesAdded;
    public int TotalReplaced => LayersReplaced + StackupReplaced + RulesReplaced;
    public int TotalKept     => LayersKept + StackupKept + RulesKept;

    public bool ChangedNothing => TotalAdded == 0 && TotalReplaced == 0;

    /// <summary>A one-line summary in the user's own terms.</summary>
    /// <param name="ruleNoun">What the rules are called on this side of the merge. Defaults to the
    /// technology's own wording; an assembly merge passes "assembly rule" so the same report type
    /// reads correctly for both without a second report type existing.</param>
    public string Summary(string ruleNoun = "DRC rule")
    {
        if (ChangedNothing) return "Nothing changed — every incoming item was already present.";

        var parts = new List<string>();
        if (LayersAdded + LayersReplaced > 0)
            parts.Add($"{LayersAdded} layer(s) added, {LayersReplaced} replaced");
        if (StackupAdded + StackupReplaced > 0)
            parts.Add($"{StackupAdded} stackup entr(ies) added, {StackupReplaced} replaced");
        if (RulesAdded + RulesReplaced > 0)
            parts.Add($"{RulesAdded} {ruleNoun}(s) added, {RulesReplaced} replaced");

        string kept = TotalKept > 0 ? $"; {TotalKept} existing item(s) kept" : "";
        return string.Join("; ", parts) + kept + ".";
    }
}

public static class TechnologyMerge
{
    /// <summary>
    /// Everything present in BOTH technologies, so a caller can offer the choice per item rather
    /// than only as a blanket policy. Pure — it changes nothing.
    /// </summary>
    public static IReadOnlyList<TechMergeConflict> FindConflicts(
        Technology target, Technology source, TechSection sections)
    {
        var found = new List<TechMergeConflict>();

        if (sections.HasFlag(TechSection.Layers))
        {
            var byKey = target.Layers.ToDictionary(l => l.Key);
            foreach (var incoming in source.Layers)
                if (byKey.TryGetValue(incoming.Key, out var mine))
                    found.Add(new TechMergeConflict(
                        TechSection.Layers, LayerKeyOf(incoming.Key),
                        $"Layer {incoming.Key.Layer}/{incoming.Key.Datatype}",
                        mine.Name, incoming.Name));
        }

        if (sections.HasFlag(TechSection.Stackup))
        {
            var byName = new Dictionary<string, StackupLayer>(StringComparer.Ordinal);
            foreach (var sl in target.Stackup.Layers) byName.TryAdd(sl.Name, sl);

            foreach (var incoming in source.Stackup.Layers)
                if (byName.TryGetValue(incoming.Name, out var mine))
                    found.Add(new TechMergeConflict(
                        TechSection.Stackup, StackupKeyOf(incoming.Name),
                        $"Stackup \"{incoming.Name}\"",
                        Describe(mine), Describe(incoming)));
        }

        if (sections.HasFlag(TechSection.DrcRules))
        {
            var byName = new Dictionary<string, DrcRule>(StringComparer.Ordinal);
            foreach (var r in target.DrcRules) byName.TryAdd(r.Name, r);

            foreach (var incoming in source.DrcRules)
                if (byName.TryGetValue(incoming.Name, out var mine))
                    found.Add(new TechMergeConflict(
                        TechSection.DrcRules, RuleKeyOf(incoming.Name),
                        $"Rule \"{incoming.Name}\"",
                        Describe(mine), Describe(incoming)));
        }

        return found;
    }

    private static string LayerKeyOf(LayerKey k)   => $"Layers|{k.Layer}/{k.Datatype}";
    private static string StackupKeyOf(string n)   => $"Stackup|{n}";
    private static string RuleKeyOf(string n)      => $"DrcRules|{n}";

    private static string Describe(StackupLayer s) =>
        $"{s.Kind}, {s.ThicknessDbu} DBU" + (s.IsGroundReference ? ", ground" : "");

    private static string Describe(DrcRule r)
    {
        string what = r.Kind switch
        {
            DrcRuleKind.Density      => $"window {r.WindowDbu}, {r.MinRatio}..{r.MaxRatio}",
            DrcRuleKind.AntennaRatio => $"max ratio {r.MaxRatio}",
            _                        => $"{r.ValueDbu} DBU",
        };
        string region = r.RegionA is { } a ? $" on {a}" : "";
        return $"{r.Kind}, {what}{region}";
    }

    /// <summary>
    /// Merges <paramref name="source"/>'s chosen sections into <paramref name="target"/>, in place.
    ///
    /// <para>Identity is per section and deliberately not uniform: a layer IS its
    /// <see cref="LayerDef.Key"/> (§2.1 — the name is a label), a stackup entry is its
    /// <see cref="StackupLayer.Name"/> (which is also what <c>SpanFromLayer</c>/<c>SpanToLayer</c>
    /// reference, so matching on anything else would break those references), and a DRC rule is its
    /// <see cref="DrcRule.Name"/> (the process's own name for it, which is what a violation is traced
    /// back to).</para>
    /// </summary>
    /// <param name="replaceKeys">
    /// For <see cref="TechMergeMode.Selective"/>: the conflict keys the user chose to replace, from
    /// <see cref="FindConflicts"/>. Null replaces nothing.
    /// </param>
    public static TechMergeReport Merge(
        Technology target, Technology source, TechSection sections, TechMergeMode mode,
        IReadOnlySet<string>? replaceKeys = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        if (sections == TechSection.None) return TechMergeReport.Empty;

        var warnings = new List<string>();
        int la = 0, lr = 0, lk = 0, sa = 0, sr = 0, sk = 0, ra = 0, rr = 0, rk = 0;

        if (sections.HasFlag(TechSection.Layers))
        {
            var byKey = target.Layers.ToDictionary(l => l.Key);

            foreach (var incoming in source.Layers)
            {
                if (!byKey.TryGetValue(incoming.Key, out var existing)) { target.Layers.Add(Clone(incoming)); la++; continue; }
                if (!WantsReplace(mode, replaceKeys, LayerKeyOf(incoming.Key))) { lk++; continue; }

                // A layer that changes NAME under the same key is worth saying: every name-first
                // match in this codebase — paste reconciliation, technology retargeting — keys on
                // LayerDef.Name, so silently renaming one changes how future pastes land.
                if (!string.Equals(existing.Name, incoming.Name, StringComparison.Ordinal))
                    warnings.Add($"Layer {incoming.Key.Layer}/{incoming.Key.Datatype} was renamed " +
                                 $"\"{existing.Name}\" → \"{incoming.Name}\".");

                target.Layers[target.Layers.IndexOf(existing)] = Clone(incoming);
                byKey[incoming.Key] = incoming;
                lr++;
            }
        }

        if (sections.HasFlag(TechSection.Stackup))
        {
            var byName = new Dictionary<string, StackupLayer>(StringComparer.Ordinal);
            foreach (var sl in target.Stackup.Layers) byName.TryAdd(sl.Name, sl);

            foreach (var incoming in source.Stackup.Layers)
            {
                if (!byName.TryGetValue(incoming.Name, out var existing))
                {
                    target.Stackup.Layers.Add(Clone(incoming));
                    byName[incoming.Name] = incoming;
                    sa++;
                    continue;
                }

                if (!WantsReplace(mode, replaceKeys, StackupKeyOf(incoming.Name))) { sk++; continue; }
                target.Stackup.Layers[target.Stackup.Layers.IndexOf(existing)] = Clone(incoming);
                sr++;
            }

            // A merged stackup is ORDER-SENSITIVE in a way the other sections are not: entries are
            // top-to-bottom and a substrate resolution reads that order. Appending an incoming entry
            // to the end puts it at the bottom of the stack, which is almost never where it belongs.
            if (sa > 0)
                warnings.Add($"{sa} stackup entr(ies) were appended at the BOTTOM of the stack. " +
                             "Stackup order is physical (top to bottom) — check it in the Stackup tab.");
        }

        if (sections.HasFlag(TechSection.DrcRules))
        {
            var byName = new Dictionary<string, DrcRule>(StringComparer.Ordinal);
            foreach (var r in target.DrcRules) byName.TryAdd(r.Name, r);

            foreach (var incoming in source.DrcRules)
            {
                if (!byName.TryGetValue(incoming.Name, out var existing))
                {
                    target.DrcRules.Add(Clone(incoming));
                    byName[incoming.Name] = incoming;
                    ra++;
                    continue;
                }

                if (!WantsReplace(mode, replaceKeys, RuleKeyOf(incoming.Name))) { rk++; continue; }
                target.DrcRules[target.DrcRules.IndexOf(existing)] = Clone(incoming);
                rr++;
            }

            // Rules brought in WITHOUT their layers reference layers that may not exist here. That is
            // the single most likely way this feature is misused — "just the rules, please" — and the
            // result is a rule that measures nothing while looking perfectly healthy in the editor.
            // Said at merge time, where the user can still act on it, not only at run time.
            if (!sections.HasFlag(TechSection.Layers) && (ra > 0 || rr > 0))
            {
                var known = target.Layers.Select(l => l.Key).ToHashSet();
                int dangling = target.DrcRules.Count(r => LayersOf(r).Any(k => !known.Contains(k)));

                if (dangling > 0)
                    warnings.Add($"{dangling} rule(s) name layers this technology does not define, so " +
                                 "they will measure nothing. Import the layer table too, or map them " +
                                 "in the DRC tab.");
            }
        }

        return new TechMergeReport(la, lr, lk, sa, sr, sk, ra, rr, rk, warnings);
    }

    /// <summary>
    /// A new technology carrying only the chosen sections of <paramref name="source"/> — what
    /// "export my DRC rules" writes.
    ///
    /// <para>The result is an ordinary, valid <c>.ctech</c>: it loads, it round-trips, and the import
    /// side needs no knowledge that it was produced this way. A rules-only file simply has no layers,
    /// which the importer reports as "this file offers DRC rules only".</para>
    /// </summary>
    public static Technology Extract(Technology source, TechSection sections, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var result = new Technology
        {
            Name             = name ?? source.Name,
            DefaultDisplayUnit = source.DefaultDisplayUnit,
            DefaultSnapDbu   = source.DefaultSnapDbu,
        };

        if (sections.HasFlag(TechSection.Layers))
            foreach (var l in source.Layers) result.Layers.Add(Clone(l));

        if (sections.HasFlag(TechSection.Stackup))
        {
            result.Stackup.Top = source.Stackup.Top;
            result.Stackup.Bottom = source.Stackup.Bottom;
            foreach (var sl in source.Stackup.Layers) result.Stackup.Layers.Add(Clone(sl));
        }

        if (sections.HasFlag(TechSection.DrcRules))
            foreach (var r in source.DrcRules) result.DrcRules.Add(Clone(r));

        return result;
    }

    /// <summary>
    /// Whether a colliding item should be replaced.
    ///
    /// <para><see cref="TechMergeMode.Selective"/> with no key set replaces NOTHING — the safe
    /// reading of "the user was asked and ticked none", never "the user was asked and everything
    /// wins".</para>
    /// </summary>
    private static bool WantsReplace(TechMergeMode mode, IReadOnlySet<string>? keys, string key) =>
        mode switch
        {
            TechMergeMode.Replace        => true,
            TechMergeMode.AddMissingOnly => false,
            _                            => keys is not null && keys.Contains(key),
        };

    /// <summary>Which sections a technology actually carries — drives what an import dialog offers.</summary>
    public static TechSection SectionsPresentIn(Technology tech)
    {
        var s = TechSection.None;
        if (tech.Layers.Count > 0) s |= TechSection.Layers;
        if (tech.Stackup.Layers.Count > 0) s |= TechSection.Stackup;
        if (tech.DrcRules.Count > 0) s |= TechSection.DrcRules;
        return s;
    }

    /// <summary>Every drawing layer a rule reads — its own layer plus any its expressions name.</summary>
    private static IEnumerable<LayerKey> LayersOf(DrcRule rule)
    {
        yield return rule.Layer;

        foreach (string? text in new[] { rule.RegionA, rule.RegionB })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!DrcLayerExprParser.TryParse(text, out var expr, out _) || expr is null) continue;
            foreach (var k in expr.ReferencedLayers()) yield return k;
        }
    }

    // ── Assembly rules (wbond.md §8) ────────────────────────────────────────────
    //
    // The SAME operation, widened rather than forked (brief-wbond-wbd §1.2). Identity is a rule's
    // Name, exactly as it is for a DRC rule and for the same reason: the name is what a violation
    // traces back to. The section is part of the key, because WB32 makes the section meaningful — a
    // machine limit and a process preference may legitimately share a name and are not the same rule.

    private static string AssemblyKeyOf(WasmSection section, string name) => $"AssemblyRules|{section}|{name}";

    private static string Describe(WasmRule r) =>
        r.Expression.Length > 60 ? r.Expression[..57] + "…" : r.Expression;

    /// <summary>Every assembly rule present in BOTH rule sets, so a caller can choose per item.</summary>
    public static IReadOnlyList<TechMergeConflict> FindAssemblyConflicts(WasmFile target, WasmFile source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var found = new List<TechMergeConflict>();

        foreach (var section in new[] { WasmSection.Machine, WasmSection.Process, WasmSection.Material })
        {
            var byName = new Dictionary<string, WasmRule>(StringComparer.Ordinal);
            foreach (var r in target.RulesOf(section)) byName.TryAdd(r.Name, r);

            foreach (var incoming in source.RulesOf(section))
                if (byName.TryGetValue(incoming.Name, out var mine))
                    found.Add(new TechMergeConflict(
                        TechSection.AssemblyRules, AssemblyKeyOf(section, incoming.Name),
                        $"{section} rule \"{incoming.Name}\"",
                        Describe(mine), Describe(incoming)));
        }

        return found;
    }

    /// <summary>
    /// Merges <paramref name="source"/>'s assembly rules, envelopes and material lists into
    /// <paramref name="target"/>, in place — the same modes, the same conflict keys and the same
    /// report as a technology merge.
    /// </summary>
    public static TechMergeReport MergeAssembly(
        WasmFile target, WasmFile source, TechMergeMode mode, IReadOnlySet<string>? replaceKeys = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var warnings = new List<string>();
        int added = 0, replaced = 0, kept = 0;

        foreach (var section in new[] { WasmSection.Machine, WasmSection.Process, WasmSection.Material })
        {
            var targetRules = target.RulesOf(section);
            var byName = new Dictionary<string, WasmRule>(StringComparer.Ordinal);
            foreach (var r in targetRules) byName.TryAdd(r.Name, r);

            foreach (var incoming in source.RulesOf(section))
            {
                if (!byName.TryGetValue(incoming.Name, out var existing))
                {
                    targetRules.Add(Clone(incoming));
                    byName[incoming.Name] = incoming;
                    added++;
                    continue;
                }

                if (!WantsReplace(mode, replaceKeys, AssemblyKeyOf(section, incoming.Name))) { kept++; continue; }
                targetRules[targetRules.IndexOf(existing)] = Clone(incoming);
                replaced++;
            }
        }

        // Envelopes ride along with the rules that look them up. Bringing rules without their tables
        // is the assembly-side twin of importing DRC rules without their layers — the rule looks
        // perfectly healthy and measures against a limit that does not exist — so a missing table is
        // said at merge time, where the user can still act on it.
        var envByName = new Dictionary<string, WasmEnvelope>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in target.Envelopes) envByName.TryAdd(e.Name, e);

        foreach (var incoming in source.Envelopes)
        {
            if (!envByName.TryGetValue(incoming.Name, out var existing))
            {
                target.Envelopes.Add(Clone(incoming));
                envByName[incoming.Name] = incoming;
                continue;
            }

            if (!WantsReplace(mode, replaceKeys, $"AssemblyRules|Envelope|{incoming.Name}")) continue;
            target.Envelopes[target.Envelopes.IndexOf(existing)] = Clone(incoming);
        }

        foreach (long d in source.AllowedDiametersNm)
            if (!target.AllowedDiametersNm.Contains(d)) target.AllowedDiametersNm.Add(d);

        foreach (string m in source.AllowedMetals)
            if (!target.AllowedMetals.Contains(m, StringComparer.OrdinalIgnoreCase))
                target.AllowedMetals.Add(m);

        var known = target.Envelopes.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int dangling = target.AllRules().Count(t =>
            Drc.DrcPredicateParser.TryParse(t.Rule.Expression, out var p, out _) && p is not null &&
            p.ReferencedEnvelopes().Any(n => !known.Contains(n)));

        if (dangling > 0)
            warnings.Add($"{dangling} assembly rule(s) look up envelope tables this rule set does not " +
                         "declare, so they will measure against nothing.");

        return new TechMergeReport(0, 0, 0, 0, 0, 0, added, replaced, kept, warnings);
    }

    /// <summary>
    /// The union the DRC actually evaluates: a technology's die-side rules and a `.wasm`'s
    /// assembly-side rules, together, with any NAME shared between the two sides listed.
    ///
    /// <para><b>The two lists stay separate rather than being concatenated, and that is the honest
    /// shape.</b> A <see cref="DrcRule"/> is a measurement kind applied to a region; a
    /// <see cref="WasmRule"/> is a predicate over wires. Flattening them into one list would need a
    /// discriminated wrapper that every consumer would immediately unwrap again. "Union" here means
    /// what §8 means by it — the check evaluates both sets in one run and reports into one panel.</para>
    ///
    /// <para><b>Why a shared name is a collision worth reporting.</b> A violation names its rule, and
    /// that name is also the waiver's own record of what was waived. Two rules called
    /// "MinWireSpacing" — one from the process, one from the house — make both of those ambiguous.
    /// Neither side is modified: the collision is reported and both rules still run.</para>
    /// </summary>
    public static IReadOnlyList<TechMergeConflict> FindCheckUnionCollisions(Technology? tech, WasmFile? wasm)
    {
        if (tech is null || wasm is null) return [];

        var byName = new Dictionary<string, DrcRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in tech.DrcRules) byName.TryAdd(r.Name, r);

        var found = new List<TechMergeConflict>();
        foreach (var (section, rule) in wasm.AllRules())
            if (byName.TryGetValue(rule.Name, out var mine))
                found.Add(new TechMergeConflict(
                    TechSection.AssemblyRules, AssemblyKeyOf(section, rule.Name),
                    $"Rule name \"{rule.Name}\" is used by both the technology and the assembly rules",
                    Describe(mine), Describe(rule)));

        return found;
    }

    // ── Clones ──────────────────────────────────────────────────────────────────
    // Merging must never alias the source's objects into the target: the two technologies outlive the
    // merge independently, and a later edit to one would otherwise silently change the other.

    private static LayerDef Clone(LayerDef l) => new()
    {
        Key = l.Key, Name = l.Name, Color = l.Color, FillOpacity = l.FillOpacity,
        ZOrder = l.ZOrder, Visible = l.Visible, Selectable = l.Selectable,
        Purpose = l.Purpose, Interchange = l.Interchange,
    };

    private static StackupLayer Clone(StackupLayer s) => new()
    {
        Kind = s.Kind, Name = s.Name, ThicknessDbu = s.ThicknessDbu,
        Epsr = s.Epsr, TanD = s.TanD, Mur = s.Mur, SigmaSm = s.SigmaSm,
        DrawingLayers = [.. s.DrawingLayers], IsGroundReference = s.IsGroundReference,
        Fill = s.Fill, WallThicknessDbu = s.WallThicknessDbu,
        SpanFromLayer = s.SpanFromLayer, SpanToLayer = s.SpanToLayer,
    };

    private static WasmRule Clone(WasmRule r) => new()
    {
        Name = r.Name, Expression = r.Expression, Description = r.Description, Severity = r.Severity,
    };

    private static WasmEnvelope Clone(WasmEnvelope e) => new()
    {
        Name = e.Name, Points = [.. e.Points],
    };

    private static DrcRule Clone(DrcRule r) => new()
    {
        Name = r.Name, Kind = r.Kind, Layer = r.Layer,
        RegionA = r.RegionA, RegionB = r.RegionB,
        ValueDbu = r.ValueDbu, WindowDbu = r.WindowDbu,
        MinRatio = r.MinRatio, MaxRatio = r.MaxRatio,
        NetScope = r.NetScope, Severity = r.Severity,
    };
}
