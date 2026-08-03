using RfCore;

namespace CircuitRF.Core.Pdk;

/// <summary>One port's label, as the file itself declares it.</summary>
/// <param name="Port">1-based port index, exactly as written.</param>
/// <param name="Name">The label verbatim.</param>
/// <param name="Group">
/// The object the port is a terminal of — the label with its terminal suffix removed. Several
/// ports sharing a group are terminals of one multi-terminal object.
/// </param>
public sealed record TouchstonePortLabel(int Port, string Name, string Group);

/// <summary>How a port split was arrived at — the three states carry different weight.</summary>
public enum PortSplitConfidence
{
    /// <summary>The file's own structure decides it: a multi-terminal object begins.</summary>
    Structural,
    /// <summary>Nothing in the file distinguishes the ports. Do not guess on the caller's behalf.</summary>
    Ambiguous,
}

/// <summary>Where a network's externally-connectable ports stop.</summary>
/// <param name="ExternalPortCount">
/// Ports 1..N are externally connectable. Meaningless unless <paramref name="Confidence"/> is
/// <see cref="PortSplitConfidence.Structural"/>.
/// </param>
/// <param name="Confidence">How far this can be trusted.</param>
/// <param name="Reason">Why, in words, for a user-facing report.</param>
public sealed record TouchstonePortSplit(
    int                 ExternalPortCount,
    PortSplitConfidence Confidence,
    string              Reason);

/// <summary>
/// Reads the per-port labels a Touchstone file may carry in its comments.
///
/// <para>Electromagnetic solvers commonly emit <c>! Port[k] = &lt;name&gt;</c> alongside the data,
/// naming each port after the geometry it was placed on. That is a property of the FORMAT and of
/// how such tools write it — nothing here knows anything about any particular supplier or part, and
/// nothing may.</para>
///
/// <para>Why it matters: a network extracted from a physical structure often exposes more ports than
/// the part has pins. The extra ports are openings left where lumped components attach, so a caller
/// needs to know where the externally-connectable ports stop. When the file's own structure says
/// so, this reports it; when it does not, this reports <see cref="PortSplitConfidence.Ambiguous"/>
/// rather than a plausible-looking number. A wrong split silently builds a different circuit, so
/// "I cannot tell" is the only safe answer, and the caller supplies the split as run-time data.</para>
/// </summary>
public static class TouchstonePortLabels
{
    /// <summary>Reads the labels a parsed network carries. Empty when it declares none.</summary>
    public static IReadOnlyList<TouchstonePortLabel> Read(SNP snp) =>
        Parse((snp ?? throw new ArgumentNullException(nameof(snp)))
              .Comments.Select(c => c.Text));

    /// <summary>Reads the labels out of comment text, with or without the leading '!'.</summary>
    public static IReadOnlyList<TouchstonePortLabel> Parse(IEnumerable<string> commentLines)
    {
        ArgumentNullException.ThrowIfNull(commentLines);

        var byPort = new SortedDictionary<int, TouchstonePortLabel>();
        foreach (string raw in commentLines)
        {
            if (!TryParseLine(raw, out int port, out string name)) continue;
            // First declaration wins: a file restating a port is describing the same port, and
            // taking the later one would silently prefer whatever happened to be written last.
            if (!byPort.ContainsKey(port))
                byPort[port] = new TouchstonePortLabel(port, name, GroupOf(name));
        }
        return [.. byPort.Values];
    }

    /// <summary>
    /// Where the externally-connectable ports stop, decided only by what the file declares.
    ///
    /// <para>The rule: ports are external until the first port that belongs to a multi-terminal
    /// object, because an object carrying several terminals is a place components attach, not a
    /// pin. If no object carries more than one terminal, nothing in the file distinguishes the
    /// ports and the answer is <see cref="PortSplitConfidence.Ambiguous"/>.</para>
    /// </summary>
    public static TouchstonePortSplit SplitExternal(IReadOnlyList<TouchstonePortLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
            return new(0, PortSplitConfidence.Ambiguous, "the file declares no port labels.");

        var sizes = labels.GroupBy(l => l.Group, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var ordered = labels.OrderBy(l => l.Port).ToList();
        int firstShared = ordered.FindIndex(l => sizes[l.Group] > 1);

        if (firstShared < 0)
            return new(0, PortSplitConfidence.Ambiguous,
                       "every port names a different object, so nothing marks where the " +
                       "externally-connectable ports stop.");

        if (firstShared == 0)
            return new(0, PortSplitConfidence.Ambiguous,
                       "the first port already belongs to a multi-terminal object, so the file " +
                       "does not show any port as externally connectable.");

        string group = ordered[firstShared].Group;
        return new(firstShared, PortSplitConfidence.Structural,
                   $"ports 1-{firstShared} each name a different object; port {firstShared + 1} " +
                   $"begins '{group}', which carries {sizes[group]} terminals.");
    }

    // ── parsing ───────────────────────────────────────────────────────────────

    /// <summary>Matches <c>Port[k] = name</c>, with or without a leading '!' and any spacing.</summary>
    private static bool TryParseLine(string? raw, out int port, out string name)
    {
        port = 0;
        name = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string s = raw.TrimStart();
        if (s.StartsWith('!')) s = s[1..].TrimStart();

        const string kw = "Port[";
        if (!s.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return false;

        int close = s.IndexOf(']', kw.Length);
        if (close < 0) return false;
        if (!int.TryParse(s[kw.Length..close], out port) || port < 1) return false;

        int eq = s.IndexOf('=', close + 1);
        if (eq < 0) return false;

        name = s[(eq + 1)..].Trim();
        return name.Length > 0;
    }

    /// <summary>
    /// The object a terminal belongs to: the label without its terminal suffix.
    ///
    /// <para>Two suffix spellings are stripped, both common in solver output: a modal index
    /// (<c>name:1</c>) and a terminal index (<c>name_T3</c>). A label carrying neither IS its own
    /// group, which is what makes a single-terminal port distinguishable from a shared one.</para>
    /// </summary>
    public static string GroupOf(string name)
    {
        string s = name;

        int colon = s.LastIndexOf(':');
        if (colon > 0 && IsAllDigits(s.AsSpan(colon + 1))) s = s[..colon];

        int us = s.LastIndexOf('_');
        if (us > 0 && us + 2 <= s.Length - 1 &&
            (s[us + 1] == 'T' || s[us + 1] == 't') && IsAllDigits(s.AsSpan(us + 2)))
            s = s[..us];

        return s;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }
}
