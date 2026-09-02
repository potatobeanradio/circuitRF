using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Rewrites the pair a library file states a nonlinear CHARGE with — a behavioural voltage source
/// driving a linear capacitor — as the one charge equation it algebraically is.
///
/// <para><b>This is not an optimisation, and that is the whole reason it exists.</b> A behavioural
/// voltage source is a branch equation: a relation BETWEEN node voltages, carrying a branch-current
/// unknown of its own. DC and S-parameter analysis solve one exactly. Harmonic balance cannot: its
/// unknowns are the voltage phasors at the nonlinear-facing nodes, and a branch current is neither
/// one of them nor reducible into the linear subnetwork, because its own row is nonlinear. The
/// COLLAPSED device states a charge instead, and harmonic balance has applied <c>jkω</c> to charge
/// harmonics since it was written — so this is the only formulation in which the idiom runs in the
/// analysis the physics is written for.</para>
///
/// <para><b>The algebra, in full, because the sign is the part that is silent when it is wrong.</b>
/// With the source holding <c>V(p) − V(mid) = f</c> and a linear capacitor <c>K</c> from
/// <c>mid</c> to <c>m</c>, the current entering at <c>p</c> is the current through <c>K</c> — they
/// are in series and nothing else touches <c>mid</c> — so the port's stored charge IS the
/// capacitor's:</para>
/// <code>
///   Q = K·(V(mid) − V(m)) = K·(V(p) − f − V(m)) = K·(v_port − f)
/// </code>
/// <para>and the capacitor's own value cancels out of whatever <c>f</c> divided by it. The mirrored
/// spelling — the source's MINUS on the outside, the capacitor reaching the plus terminal — gives
/// the same expression, which is why one formula covers both.</para>
///
/// <para><b>"Nothing else on <c>mid</c>" is the entire correctness condition</b>, and it is checked
/// against the definition's own port list and every other element in it, never against the text. A
/// third element there makes the two no longer in series and the collapse simply wrong — it is not
/// approximated, the pair is left as the general branch-row device, which still solves at DC and in
/// S-parameters.</para>
///
/// <para><b>One further condition the brief did not state:</b> <c>f</c> must not sense the
/// constrained pair itself, because that pair is what ceases to exist. Such a source is implicit in
/// its own output and has no collapsed form to be written into.</para>
/// </summary>
internal static class SpiceChargePairCollapse
{
    /// <summary>The SDD equation slot a behavioural VOLTAGE source lands in — the branch equation.</summary>
    private const string BranchEquation = "V[1]";

    /// <summary>The slot the collapsed device states its charge in.</summary>
    private const string ChargeEquation = "I[1,1]";

    /// <summary>
    /// Returns <paramref name="t"/> with every charge pair in it rewritten, or the same instance
    /// when none matched.
    /// </summary>
    internal static SubcircuitTranslation Collapse(SubcircuitTranslation t)
    {
        if (t.Refusal is not null) return t;

        // Nothing to look for unless a branch equation is actually present. The overwhelming
        // majority of definitions contain none, and this keeps them free.
        if (!t.Elements.Any(IsBehaviouralVoltageSource)) return t;

        // How many elements touch each net, and which nets the definition exposes. A port is
        // connected to whatever the CALL SITE wires to it, so it can never be an interior node.
        var touches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in t.Elements)
            foreach (string net in e.Nets.Distinct(StringComparer.OrdinalIgnoreCase))
                touches[net] = touches.GetValueOrDefault(net) + 1;

        var exposed = new HashSet<string>(t.Definition.Ports, StringComparer.OrdinalIgnoreCase);

        var replaced  = new SubcircuitElement[t.Elements.Count];
        var consumed  = new bool[t.Elements.Count];
        bool anything = false;

        for (int i = 0; i < t.Elements.Count; i++)
        {
            if (consumed[i] || !IsBehaviouralVoltageSource(t.Elements[i])) continue;

            var (capIndex, collapsed) = TryPair(t.Elements, i, touches, exposed);
            if (collapsed is null) continue;

            replaced[i]        = collapsed;
            consumed[capIndex] = true;
            anything           = true;
        }

        if (!anything) return t;

        var elements = new List<SubcircuitElement>(t.Elements.Count);
        for (int i = 0; i < t.Elements.Count; i++)
        {
            if (consumed[i]) continue;
            elements.Add(replaced[i] ?? t.Elements[i]);
        }

        return t with { Elements = elements };
    }

    private static bool IsBehaviouralVoltageSource(SubcircuitElement e)
        => e.Refusal is null
        && e.Symbol == SymbolKind.Sdd
        && e.Nets.Count >= 2
        && e.Parameters.Any(p => p.Name.Equals(BranchEquation, StringComparison.Ordinal));

    /// <summary>
    /// The capacitor this source is in series with, and what the two become — or a null element
    /// when the pattern does not hold exactly.
    /// </summary>
    private static (int CapIndex, SubcircuitElement? Collapsed) TryPair(
        IReadOnlyList<SubcircuitElement> elements, int sourceIndex,
        IReadOnlyDictionary<string, int> touches, IReadOnlySet<string> exposed)
    {
        var source = elements[sourceIndex];

        // Either terminal of the source may be the interior one. Index 1 is the spelling the
        // measured files use; index 0 is its mirror and gives the same collapsed expression.
        for (int end = 0; end < 2; end++)
        {
            string mid   = source.Nets[end];
            string outer = source.Nets[1 - end];

            // A port belongs to the call site, and ground belongs to the whole design. Neither is
            // ever private to two elements, whatever this definition's own element list says.
            if (exposed.Contains(mid) || IsGround(mid)) continue;
            if (touches.GetValueOrDefault(mid) != 2) continue;

            // The source's own SENSE pairs (ports 2..N) may not name the interior node either: the
            // node is about to stop existing, and an expression reading it has nothing to read.
            for (int n = 2; n < source.Nets.Count; n++)
                if (source.Nets[n].Equals(mid, StringComparison.OrdinalIgnoreCase))
                    return (-1, null);

            for (int c = 0; c < elements.Count; c++)
            {
                if (c == sourceIndex) continue;
                var cap = elements[c];
                if (!IsLinearCapacitor(cap)) continue;

                int at = cap.Nets[0].Equals(mid, StringComparison.OrdinalIgnoreCase) ? 0
                       : cap.Nets[1].Equals(mid, StringComparison.OrdinalIgnoreCase) ? 1
                       : -1;
                if (at < 0) continue;

                string far = cap.Nets[1 - at];
                string k   = cap.Parameters.First(p => p.Name.Equals("C", StringComparison.Ordinal))
                                .Expression;

                var built = Build(source, end == 1 ? outer : far, end == 1 ? far : outer, k, cap);
                return built is null ? (-1, null) : (c, built);
            }
        }

        return (-1, null);
    }

    private static bool IsLinearCapacitor(SubcircuitElement e)
        => e.Refusal is null
        && e.Symbol == SymbolKind.Capacitor
        && e.Nets.Count == 2
        && e.Parameters.Any(p => p.Name.Equals("C", StringComparison.Ordinal));

    /// <summary>Whether a net name is the design's ground, which is never an interior node.</summary>
    private static bool IsGround(string net)
        => net is "0" || net.Equals("gnd", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The collapsed device: the same equation-defined device, its port 1 spanning the pair's two
    /// outer terminals, stating <c>K·(v_port − f)</c> as its stored charge.
    /// </summary>
    private static SubcircuitElement? Build(
        SubcircuitElement source, string plus, string minus, string k, SubcircuitElement cap)
    {
        string f = source.Parameters
                         .First(p => p.Name.Equals(BranchEquation, StringComparison.Ordinal))
                         .Expression;

        // `_v1` is the CONSTRAINED pair, which the collapse dissolves. A source that reads its own
        // output is implicit in itself and has no collapsed form.
        Expr ast;
        try { ast = Parser.Parse(f); }
        catch { return null; }
        if (AstWalker.CollectRefs(ast).Contains(PortVoltage(1))) return null;

        var parameters = new List<EditableParameter>();
        foreach (var q in source.Parameters)
        {
            if (q.Name.Equals(BranchEquation, StringComparison.Ordinal))
            {
                parameters.Add(new EditableParameter
                {
                    Name       = ChargeEquation,
                    Expression = $"({k})*({PortVoltage(1)}-({f}))",
                });
                continue;
            }
            parameters.Add(q);
        }

        var nets = new List<string>(source.Nets.Count) { plus, minus };
        for (int n = 2; n < source.Nets.Count; n++) nets.Add(source.Nets[n]);

        var notes = new List<string>(source.Notes)
        {
            $"It drives '{cap.InstanceName}' and nothing else, which is how this dialect writes a "
          + "nonlinear CHARGE: the two are one capacitance whose stored charge is "
          + $"({k})·(V(port) − its expression), and '{cap.InstanceName}'s own value cancels. Placed "
          + "as that one device, so harmonic balance can carry it — a voltage constraint on its own "
          + "solves at DC and in S-parameters, but is not one of harmonic balance's unknowns.",
        };

        return source with
        {
            Nets       = nets,
            Parameters = parameters,
            Notes      = notes,
        };
    }

    private static string PortVoltage(int port)
        => "_v" + port.ToString(CultureInfo.InvariantCulture);
}
