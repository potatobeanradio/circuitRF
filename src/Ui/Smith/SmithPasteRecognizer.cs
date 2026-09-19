using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// What a paste produced: a cascade, or a sentence naming what stopped it.
/// </summary>
/// <param name="Elements">The recognised cascade, generator-first. Null when
/// <paramref name="Refusal"/> is set; the two are never both present.</param>
/// <param name="Refusal">The sentence the strip shows instead of pasting.</param>
/// <param name="Note">What the recognizer DECIDED that the user could not see — today, which of
/// <c>R-smith7-7</c>'s two end rules fired. Null when there is nothing to say.</param>
public sealed record SmithPasteResult(
    IReadOnlyList<SmithElement>? Elements,
    string?                      Refusal,
    string?                      Note)
{
    internal static SmithPasteResult No(string refusal)  => new(null, refusal, null);
    internal static SmithPasteResult Yes(IReadOnlyList<SmithElement> elements, string note)
        => new(elements, null, note);
}

/// <summary>
/// Decides whether a pasted <c>.csch</c> selection is a cascade this tool can represent
/// (<c>brief-smith-7-clipboard.md</c> <c>R-smith7-6</c>, <c>R-smith7-7</c>;
/// <c>docs/design/smith-chart.md</c> §6.2).
/// </summary>
/// <remarks>
/// <b>The refusals matter more than the successes.</b> A permissive reader that accepted PART of a
/// paste would replace the user's network with something that is not what they copied and report
/// success — which is exactly what <c>RailClipboard</c>'s marker guard exists to prevent, and why
/// brief 1 built this tool's own guard in its shape. Every exit below is either the whole cascade or a
/// sentence naming the offending net, instance or parameter; there is no partial answer.
///
/// <para><b>The connectivity is <c>NetExtractor</c>'s, not a second net builder.</b> Working out which
/// pins share a net is the schematic editor's own union-find over on-grid connection points, geometric
/// T-junctions, wire vertices and same-name labels — five layers of rules with real corner cases in
/// each. A copy of that here would be a recognizer that agreed with the application about most
/// selections, and the ones it disagreed about would be pasted as a different circuit that still
/// evaluates. So the selection is handed to <see cref="NetExtractor.Extract"/> and what is read back
/// is the net NAMES it resolved, per instance, in port order.</para>
///
/// <para><b>It reads the components too, and that is not a second source of truth.</b> The extraction
/// answers "what is connected to what"; the refusals have to name a SYMBOL and a PARAMETER as the user
/// spelled them, and an instance carries neither — a <c>Tline</c>, an open stub and a shorted stub are
/// one engine reference, and <c>C = Cnom*2</c> has already become an override by then. So topology
/// comes from the extraction and identity from the component, joined on the instance name, which is
/// what <c>NetExtractor</c> uses verbatim.</para>
/// </remarks>
public static class SmithPasteRecognizer
{
    /// <summary>The ground net's name, as <c>NetExtractor</c> emits it.</summary>
    private const string GroundNet = "0";

    /// <summary>The grid connection points are quantised to. See <see cref="Recognize"/>.</summary>
    private const double SmithGrid = 100.0;

    /// <summary>
    /// Recognises a pasted selection, or refuses it by name.
    /// </summary>
    /// <param name="components">What <c>SchematicClipboard.PasteAsync</c> returned.</param>
    /// <param name="wires">Its wires — the other half of the connectivity.</param>
    /// <param name="mirrored">The strip's current mirror setting, which decides which geometric end is
    /// the generator when no port numbers the ends (<c>R-smith7-7</c>).</param>
    /// <param name="designFrequencyHz">What a line's reference frequency defaults to when the pasted
    /// component carries none — the same default <see cref="SmithElementFactory"/> uses on
    /// placement.</param>
    public static SmithPasteResult Recognize(
        IReadOnlyList<EditableComponent> components,
        IReadOnlyList<EditableWire>      wires,
        bool                             mirrored,
        double                           designFrequencyHz)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(wires);

        if (components.Count == 0)
            return SmithPasteResult.No("There is no schematic selection on the clipboard to paste.");

        // ── 1. Vocabulary (R-smith7-6 rule 1) ────────────────────────────────
        var candidates = new List<EditableComponent>();
        var ports      = new List<EditableComponent>();

        foreach (var c in components)
        {
            if (c.Symbol == SymbolKind.Ground) continue;

            if (c.Symbol is SymbolKind.Term or SymbolKind.TermG or SymbolKind.Pin)
            { ports.Add(c); continue; }

            if (!IsVocabulary(c.Symbol))
                return SmithPasteResult.No(
                    $"{Named(c)} is a {ComponentTypeRegistry.DisplayName(c.Symbol)}, which the "
                  + "Smith Chart cannot represent. Its vocabulary is R, L, C, SRLC, PRLC, Z1P, S1P, "
                  + "S2P, TLIN and the two stubs — plus grounds and ports.");

            candidates.Add(c);
        }

        if (candidates.Count == 0)
            return SmithPasteResult.No(
                "The pasted selection has ports and wires but no elements, so there is no cascade in "
              + "it to replace this one with.");

        // ── 2. Connectivity, from the editor's own extractor ─────────────────
        // The grid is the projection's own and the clipboard's own — SmithNetworkModel builds at 100
        // and SchematicClipboard renders a selection at 100. It matters here because NetExtractor
        // QUANTISES connection points to it, so a different number would merge or split nets.
        var model = new SchematicEditModel { GridSize = SmithGrid, GridSnap = false };
        foreach (var c in components) model.Components.Add(c);
        foreach (var w in wires)      model.Wires.Add(w);

        NetExtractor.ExtractionResult extraction;
        try { extraction = NetExtractor.Extract(model); }
        catch (Exception ex)
        {
            return SmithPasteResult.No(
                $"The pasted selection could not be read as a circuit: {ex.Message}");
        }

        var instances = new Dictionary<string, Instance>(StringComparer.Ordinal);
        foreach (var inst in extraction.TestBench.Instances) instances[inst.InstanceName] = inst;

        // ── 3. One record per element: its nets, its placement, its values ───
        var wired = new List<Wired>(candidates.Count);
        foreach (var c in candidates)
        {
            if (!instances.TryGetValue(c.InstanceName, out var inst))
                return SmithPasteResult.No(
                    $"{Named(c)} is not connected to anything the pasted selection carries, so there "
                  + "is no place in the cascade for it.");

            var signal = inst.NetBindings.Where(n => n != GroundNet).Distinct(StringComparer.Ordinal).ToList();
            bool grounded = inst.NetBindings.Any(n => n == GroundNet) || ImplicitlyGrounded(c);

            wired.Add(new Wired(c, signal, grounded));
        }

        // ── 4. Which nets the PORTS sit on, and what numbers they carry ──────
        var portNets = new List<(string Net, int? Num, EditableComponent Comp)>();
        foreach (var p in ports)
        {
            string? on = PortNet(p, instances, extraction);
            if (on is null || on == GroundNet) continue;      // a port on ground says nothing about an end
            portNets.Add((on, IntParam(p, "Num"), p));
        }

        var distinctPortNets = portNets.Select(p => p.Net).Distinct(StringComparer.Ordinal).ToList();
        if (distinctPortNets.Count > 2)
            return SmithPasteResult.No(
                $"The pasted selection has {distinctPortNets.Count} port nets "
              + $"({string.Join(", ", distinctPortNets.OrderBy(n => n, StringComparer.Ordinal))}); a "
              + "cascade has exactly two ends, and nothing here says which two of those you meant.");

        // ── 5. Placement, and the open stub (R-smith7-6 rules 2-4) ───────────
        var placed = new List<Placed>(wired.Count);
        foreach (var w in wired)
        {
            if (w.Signal.Count == 0)
                return SmithPasteResult.No(
                    $"{Named(w.Comp)} has every pin on ground, so it is not in the through path at "
                  + "all.");

            if (w.Signal.Count > 2)
                return SmithPasteResult.No(
                    $"{Named(w.Comp)} binds {w.Signal.Count} nets "
                  + $"({string.Join(", ", w.Signal)}); a Smith cascade element has one or two, because "
                  + "everything in it is either in the through path or hanging off it to ground.");

            if (w.Signal.Count == 1)
            {
                if (!w.Grounded)
                    return SmithPasteResult.No(
                        $"{Named(w.Comp)} hangs off net {w.Signal[0]} with its other end on neither "
                      + "the through path nor ground. Every shunt element in a Smith cascade returns "
                      + "to ground — that is what makes its trajectory an admittance move.");

                placed.Add(new Placed(w, SmithPlacement.Shunt, w.Signal[0], null));
                continue;
            }

            // Two signal nets AND a ground is a branch out of the cascade, not an element of it.
            if (w.Grounded)
                return SmithPasteResult.No(
                    $"{Named(w.Comp)} is in the through path between {w.Signal[0]} and {w.Signal[1]} "
                  + "and also on ground. A Smith cascade element is one or the other.");

            placed.Add(new Placed(w, SmithPlacement.Series, w.Signal[0], w.Signal[1]));
        }

        // A dangling net is one with a single element on it and no port. On a TLIN that is an OPEN
        // STUB — the far end left open is exactly what tells it from the shorted one — but only once
        // BOTH ends are pinned by ports, because a series line whose far end is bare is the same
        // graph. With fewer than two ports to pin them the line is read as SERIES and the bare net is
        // an end, which is the commoner drawing.
        //
        // The test is the number of PORTS and not the number of port NETS: a cascade of nothing but
        // shunt arms has one net with both ports on it, and its ends are pinned just as firmly as a
        // two-net one's. Counting nets there would leave a stub looking like the second end.
        var occupancy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in placed)
        {
            Bump(occupancy, p.NetA);
            if (p.NetB is { } b) Bump(occupancy, b);
        }
        foreach (var (portNet, _, _) in portNets) Bump(occupancy, portNet);

        bool endsPinnedByPorts = portNets.Count >= 2;

        if (endsPinnedByPorts)
        {
            for (int i = 0; i < placed.Count; i++)
            {
                var p = placed[i];
                if (p.Placement != SmithPlacement.Series)   continue;
                if (p.NetB is not { } far)                  continue;
                if (occupancy.GetValueOrDefault(far) != 1)  continue;
                if (distinctPortNets.Contains(far))         continue;

                if (p.Wired.Comp.Symbol == SymbolKind.Tline)
                {
                    placed[i] = p with { Placement = SmithPlacement.Shunt, NetB = null, OpenStub = true };
                    occupancy[far] = 0;
                    continue;
                }

                // R-smith7-6 rule 4, and it has to be caught HERE rather than left to the loose-end
                // count below, because the two refusals answer different questions. An arm whose far
                // end goes nowhere is a drawing about ONE element and the sentence has to name it; a
                // walk with the wrong number of ends is a statement about the whole selection.
                return SmithPasteResult.No(
                    $"{Named(p.Wired.Comp)} hangs off net {p.NetA} with its other end on net {far}, "
                  + "which is neither the through path nor ground. Every shunt element in a Smith "
                  + "cascade returns to ground — that is what makes its trajectory an admittance "
                  + "move.");
            }
        }

        // ── 6. The through path is a WALK, and a branch is a refusal (rule 3) ─
        var series = placed.Where(p => p.Placement == SmithPlacement.Series).ToList();
        var shunt  = placed.Where(p => p.Placement == SmithPlacement.Shunt).ToList();

        var seriesDegree = new Dictionary<string, List<Placed>>(StringComparer.Ordinal);
        foreach (var p in series)
        {
            Add(seriesDegree, p.NetA, p);
            Add(seriesDegree, p.NetB!, p);
        }
        foreach (var (branchNet, on) in seriesDegree)
            if (on.Count > 2)
                return SmithPasteResult.No(
                    $"Net {branchNet} has {on.Count} through-path elements on it "
                  + $"({string.Join(", ", on.Select(p => p.Wired.Comp.InstanceName))}); that is a "
                  + "branch, and a Smith cascade is a walk with no branches in it.");

        // The ends of the walk: nets with fewer than two series elements on them. A cascade with no
        // series elements at all has ONE net and both of its ends are that net, which is a real
        // design (a single shunt capacitor) and not a degenerate one.
        var allNets = new List<string>();
        foreach (var p in placed)
        {
            if (!allNets.Contains(p.NetA, StringComparer.Ordinal)) allNets.Add(p.NetA);
            if (p.NetB is { } b && !allNets.Contains(b, StringComparer.Ordinal)) allNets.Add(b);
        }

        var walkEnds = allNets.Where(n => seriesDegree.GetValueOrDefault(n)?.Count is not 2).ToList();

        if (series.Count == 0)
        {
            if (allNets.Count != 1)
                return SmithPasteResult.No(
                    $"The pasted selection has {allNets.Count} separate nets and nothing joining them "
                  + $"({string.Join(", ", allNets)}); a cascade is one connected walk.");
        }
        else if (walkEnds.Count != 2)
        {
            return SmithPasteResult.No(
                $"The pasted selection has {walkEnds.Count} loose ends "
              + $"({string.Join(", ", walkEnds)}); a cascade has exactly two, one for the generator "
              + "and one for the load.");
        }

        // ── 7. Which end is the generator (R-smith7-7) ───────────────────────
        string genEnd;
        string endRule;

        var numbered = portNets.Where(p => p.Num is > 0).OrderBy(p => p.Num!.Value).ToList();
        if (numbered.Count > 0 && (series.Count == 0 || walkEnds.Contains(numbered[0].Net, StringComparer.Ordinal)))
        {
            genEnd  = numbered[0].Net;
            endRule = $"The generator end is the one carrying port {numbered[0].Num} "
                    + $"({numbered[0].Comp.InstanceName}).";
        }
        else
        {
            // No port numbers the ends, so the drawing does — and it is NOT a bare "leftmost". A
            // flipped strip would otherwise reverse every pasted network that carried no port,
            // silently and half the time.
            var ends = series.Count == 0 ? allNets : walkEnds;
            genEnd = ends
                .OrderBy(n => EndX(n, placed), mirrored ? Descending.Instance : Ascending.Instance)
                .First();
            endRule = mirrored
                ? "No port numbered the ends, so the right-hand end is the generator — the strip is "
                + "mirrored."
                : "No port numbered the ends, so the left-hand end is the generator.";
        }

        // ── 8. The walk itself, generator first ──────────────────────────────
        var ordered  = new List<Placed>(placed.Count);
        var usedSer  = new HashSet<Placed>();
        var usedSh   = new HashSet<Placed>();
        string net   = genEnd;

        while (true)
        {
            foreach (var s in shunt.Where(s => s.NetA == net && !usedSh.Contains(s))
                                   .OrderBy(s => DrawX(s), mirrored ? Descending.Instance : Ascending.Instance))
            { ordered.Add(s); usedSh.Add(s); }

            var next = seriesDegree.GetValueOrDefault(net)?.FirstOrDefault(p => !usedSer.Contains(p));
            if (next is null) break;

            ordered.Add(next);
            usedSer.Add(next);
            net = next.NetA == net ? next.NetB! : next.NetA;
        }

        if (ordered.Count != placed.Count)
        {
            var stranded = placed.Where(p => !ordered.Contains(p))
                                 .Select(p => p.Wired.Comp.InstanceName);
            return SmithPasteResult.No(
                $"{string.Join(", ", stranded)} could not be reached by walking from the generator "
              + "end, so the pasted selection is more than one circuit.");
        }

        // ── 9. Values (rule 5) ───────────────────────────────────────────────
        var elements = new List<SmithElement>(ordered.Count);
        foreach (var p in ordered)
        {
            if (Convert(p, designFrequencyHz) is not { } converted)
                return SmithPasteResult.No(Refusal(p));
            elements.Add(converted);
        }

        return SmithPasteResult.Yes(elements, endRule);
    }

    // ── the two intermediate records ─────────────────────────────────────────

    private sealed record Wired(EditableComponent Comp, IReadOnlyList<string> Signal, bool Grounded);

    private sealed record Placed(
        Wired Wired, SmithPlacement Placement, string NetA, string? NetB, bool OpenStub = false);

    // ── the vocabulary ───────────────────────────────────────────────────────

    /// <summary>
    /// The symbols a Smith element can be, derived from <see cref="SmithComponentMap"/> rather than
    /// listed — so a kind added to that table is accepted here with nothing to keep in step, which is
    /// the same rule the Add and Insert menus follow.
    /// </summary>
    private static readonly HashSet<SymbolKind> Vocabulary =
        [.. SmithComponentMap.AllKinds.Select(k => SmithComponentMap.Component(k).SymbolKind)];

    private static bool IsVocabulary(SymbolKind kind) => Vocabulary.Contains(kind);

    /// <summary>A shunt one-port Touchstone has a single pin and an IMPLICIT ground reference, so it
    /// binds no ground net for <see cref="Recognize"/> to see.</summary>
    private static bool ImplicitlyGrounded(EditableComponent c)
        => c.Symbol == SymbolKind.Snp
        && IntParam(c, "NumPorts") == 1
        && !string.Equals(TextParam(c, "RefNode"), "true", StringComparison.OrdinalIgnoreCase);

    // ── kinds and values ─────────────────────────────────────────────────────

    /// <summary>
    /// One placed element as a <see cref="SmithElement"/>, or null when a parameter it carries is one
    /// this tool cannot represent.
    /// </summary>
    /// <remarks>
    /// <b>Null rather than a best effort</b>, because silently dropping an expression would change the
    /// circuit and report success. <see cref="Refusal"/> says which parameter it was.
    /// </remarks>
    private static SmithElement? Convert(Placed p, double designFrequencyHz)
    {
        var c = p.Wired.Comp;
        if (KindOf(p) is not { } kind) return null;

        var e = new SmithElement
        {
            Kind             = kind,
            Placement        = p.Placement,
            Name             = c.InstanceName,
            Enabled          = c.Disable == DisableState.None,
            ActiveParameter  = SmithComponentMap.DefaultParameter(kind),
        };

        switch (kind)
        {
            case SmithElementKind.R:
                if (Real(c, "R") is not { } r) return null;
                e.Values.ROhm = r;
                break;

            case SmithElementKind.L:
                if (Real(c, "L") is not { } l) return null;
                e.Values.LHenry = l;
                break;

            case SmithElementKind.C:
                if (Real(c, "C") is not { } cap) return null;
                e.Values.CFarad = cap;
                break;

            case SmithElementKind.Srlc:
            case SmithElementKind.Prlc:
                if (Real(c, "R")  is not { } sr) return null;
                if (Real(c, "L")  is not { } sl) return null;
                if (Real(c, "C") is not { } sc) return null;
                e.Values.ROhm = sr; e.Values.LHenry = sl; e.Values.CFarad = sc;
                break;

            case SmithElementKind.Z1P:
                if (ComplexParam(c, "Z[1,1]") is not { } z) return null;
                e.Values.ImpedanceOhm = z;
                break;

            case SmithElementKind.S1P:
            case SmithElementKind.S2P:
                string file = TextParam(c, "File") ?? "";
                if (file.Length == 0) return null;
                e.FileRef = file;
                break;

            default:   // the three TLIN-backed kinds
                if (Real(c, "Z") is not { } z0) return null;
                if (Degrees(c, "E") is not { } deg) return null;
                double f = Real(c, "F") ?? designFrequencyHz;
                e.Values.Z0Ohm               = z0;
                e.Values.ElectricalLengthDeg = deg;
                e.Values.ReferenceFrequencyHz = f > 0 ? f : designFrequencyHz;
                break;
        }

        return e;
    }

    /// <summary>
    /// Which <see cref="SmithElementKind"/> a placed component is — the one place the three
    /// <c>Tline</c>-backed kinds and the two <c>Snp</c> ones are told apart.
    /// </summary>
    /// <remarks>
    /// <see cref="SmithComponentMap.Component"/> maps a kind to a symbol and this is its inverse, which
    /// is deliberately NOT a second table: the symbol alone does not determine the kind (three kinds
    /// share <c>Tline</c> and two share <c>Snp</c>), so what resolves the ambiguity is the placement
    /// and the port count the extraction just established. Reading them off a table here would be
    /// reading them off something that cannot know either.
    /// </remarks>
    private static SmithElementKind? KindOf(Placed p) => p.Wired.Comp.Symbol switch
    {
        SymbolKind.Resistor  => SmithElementKind.R,
        SymbolKind.Inductor  => SmithElementKind.L,
        SymbolKind.Capacitor => SmithElementKind.C,
        SymbolKind.Srlc      => SmithElementKind.Srlc,
        SymbolKind.Prlc      => SmithElementKind.Prlc,
        SymbolKind.ZPort     => SmithElementKind.Z1P,
        SymbolKind.Snp       => IntParam(p.Wired.Comp, "NumPorts") == 1
                                    ? SmithElementKind.S1P : SmithElementKind.S2P,
        SymbolKind.Tline     => p.Placement == SmithPlacement.Series ? SmithElementKind.Tline
                              : p.OpenStub                           ? SmithElementKind.StubOpen
                              :                                        SmithElementKind.StubShorted,
        _ => null,
    };

    /// <summary>
    /// The sentence for an element whose values could not be read — <b>naming the instance AND the
    /// parameter</b>, because a refusal whose text does not identify the offending object is not one
    /// anyone can act on.
    /// </summary>
    private static string Refusal(Placed p)
    {
        var c = p.Wired.Comp;

        if (c.Symbol == SymbolKind.Snp && (TextParam(c, "File") ?? "").Length == 0)
            return $"{Named(c)} is a Touchstone element with no File, and a file element IS its file.";

        if (c.Symbol == SymbolKind.Snp && IntParam(c, "NumPorts") is not (1 or 2))
            return $"{Named(c)} is a {IntParam(c, "NumPorts")}-port Touchstone element; the Smith "
                 + "Chart's vocabulary has one- and two-port files only.";

        var offender = c.Parameters.FirstOrDefault(prm => !IsLiteral(prm));
        return offender is null
            ? $"{Named(c)} could not be read as a Smith element."
            : $"{Named(c)} carries {offender.Name} = \"{offender.Expression}\", which is not a plain "
            + "value. The Smith Chart stores numbers, not expressions, so pasting this would quietly "
            + "drop it and change the circuit.";
    }

    /// <summary>True when a parameter's expression is a value this tool can store — a literal number,
    /// the engine's own <c>complex(re,im)</c>, a file path or a bare word such as <c>true</c>.</summary>
    private static bool IsLiteral(EditableParameter p)
    {
        string t = (p.Expression ?? "").Trim();
        if (t.Length == 0) return true;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
        if (ParseComplex(t) is not null) return true;
        return p.Name is "File" or "RefNode" or "PinConfig" or "InterpMode" or "InterpDomain"
                      or "ExtrapMode" or "Pitch";
    }

    /// <summary>
    /// One parameter as a base-SI number, or null when it is not a plain value.
    /// </summary>
    /// <remarks>
    /// <b>The scale comes off the parameter's OWN unit</b>, never off the field it is being read
    /// into: <c>MatchValueFormat.Scale</c> runs the editor's unit string through
    /// <c>UnitNormalizer</c> and then <c>Units</c>, which is the same pair the extractor uses, so a
    /// label reading <c>L = 3.3 nH</c> comes back as 3.3e-9 H and not as 3.3.
    /// </remarks>
    private static double? Real(EditableComponent c, string name)
    {
        var p = c.Parameters.FirstOrDefault(x => x.Name == name);
        if (p is null) return null;
        return MatchValueFormat.TryParse(p.Expression, p.Unit, out double v) ? v : null;
    }

    /// <summary>
    /// A TLIN's electrical length, in DEGREES.
    /// </summary>
    /// <remarks>
    /// <b>The one parameter whose stored unit is not base SI</b> (<c>SmithElementValues</c>'s own
    /// named exception), so it is read here rather than through <see cref="Real"/>: the general path
    /// would apply <c>Units.Scale("deg")</c> = π/180 and store 90° as 1.57.
    /// </remarks>
    private static double? Degrees(EditableComponent c, string name)
    {
        var p = c.Parameters.FirstOrDefault(x => x.Name == name);
        if (p is null) return null;
        if (!double.TryParse((p.Expression ?? "").Trim(), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out double v)) return null;
        return p.Unit.Trim() is "rad" ? v * 180.0 / Math.PI : v;
    }

    private static Complex? ComplexParam(EditableComponent c, string name)
    {
        var p = c.Parameters.FirstOrDefault(x => x.Name == name);
        if (p is null) return null;

        string t = (p.Expression ?? "").Trim();
        if (ParseComplex(t) is { } z) return z;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double re)
             ? new Complex(re, 0.0) : null;
    }

    /// <summary><c>complex(re,im)</c>, the only spelling of a complex constant the expression engine
    /// parses and the only one this tool writes.</summary>
    private static Complex? ParseComplex(string text)
    {
        if (!text.StartsWith("complex(", StringComparison.Ordinal) ||
            !text.EndsWith(")", StringComparison.Ordinal)) return null;

        var parts = text[8..^1].Split(',');
        if (parts.Length != 2) return null;
        return double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double re)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double im)
             ? new Complex(re, im) : null;
    }

    private static string? TextParam(EditableComponent c, string name)
        => c.Parameters.FirstOrDefault(x => x.Name == name)?.Expression?.Trim();

    private static int? IntParam(EditableComponent c, string name)
        => int.TryParse(TextParam(c, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
             ? v : null;

    // ── small helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The net a port sits on.
    /// </summary>
    /// <remarks>
    /// A <c>Term</c>/<c>TermG</c> is an instance, so its first net binding is the answer. A <c>Pin</c>
    /// is not — it NAMES its net instead, and <see cref="NetExtractor.ExtractionResult.CellPorts"/> is that list in
    /// ascending <c>Num</c> order, which is why the Pin's own <c>Num</c> indexes it.
    /// </remarks>
    private static string? PortNet(EditableComponent p, IReadOnlyDictionary<string, Instance> instances,
                                   NetExtractor.ExtractionResult extraction)
    {
        if (instances.TryGetValue(p.InstanceName, out var inst))
            return inst.NetBindings.Count > 0 ? inst.NetBindings[0] : null;

        if (p.Symbol != SymbolKind.Pin) return null;

        string name = TextParam(p, "Name") ?? "";
        string expected = name.Length > 0
            ? name
            : "P" + (IntParam(p, "Num") ?? 0).ToString(CultureInfo.InvariantCulture);

        return extraction.CellPorts.FirstOrDefault(n => string.Equals(n, expected, StringComparison.Ordinal));
    }

    /// <summary>The x of an end net, for the geometric end rule — the drawn x of the element sitting
    /// on it, which is the only coordinate a net has.</summary>
    private static double EndX(string net, IReadOnlyList<Placed> placed)
    {
        foreach (var p in placed)
            if (p.NetA == net || p.NetB == net)
                return p.Wired.Comp.X;
        return 0.0;
    }

    private static double DrawX(Placed p) => p.Wired.Comp.X;

    /// <summary>The instance name, or the symbol's own when the component has none — a refusal has to
    /// name something.</summary>
    private static string Named(EditableComponent c)
        => string.IsNullOrWhiteSpace(c.InstanceName)
             ? $"An unnamed {ComponentTypeRegistry.DisplayName(c.Symbol)}"
             : c.InstanceName;

    private static void Bump(Dictionary<string, int> map, string key)
        => map[key] = map.GetValueOrDefault(key) + 1;

    private static void Add(Dictionary<string, List<Placed>> map, string key, Placed value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    private sealed class Ascending : IComparer<double>
    {
        public static readonly Ascending Instance = new();
        public int Compare(double a, double b) => a.CompareTo(b);
    }

    private sealed class Descending : IComparer<double>
    {
        public static readonly Descending Instance = new();
        public int Compare(double a, double b) => b.CompareTo(a);
    }
}
