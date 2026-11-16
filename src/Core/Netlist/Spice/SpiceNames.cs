using System.Text;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// The names a written deck uses, and the circuitRF name each stands for (docs/design/transient.md §9.2.2; brief TRN-6
/// R-trn6-6). The dialect's names are case-insensitive and restricted in their characters; circuitRF's are neither. Every net
/// and every instance path gets a sanitized name, unique after case folding, and the map runs both ways so every vector read
/// back is renamed to its circuitRF net or instance path.
///
/// <para><b>A net is never renamed onto a name the dialect reserves</b>: <c>0</c> is ground, and so is <c>gnd</c> in the
/// dialect's default configuration, so a circuitRF net called <c>gnd</c> that is not ground would silently be shorted to it.
/// Both are taken before any net is named.</para>
/// </summary>
public sealed class SpiceNameMap
{
    private readonly Dictionary<string, string> _netToDeck = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deckToNet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pathToDeck = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deckToPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase) { "0", "gnd" };
    private readonly List<(string CircuitRf, string Deck, bool IsNet)> _order = [];
    private readonly HashSet<string> _mapped = new(StringComparer.Ordinal);

    /// <summary>The deck's name for net <paramref name="net"/>; ground (<c>0</c>, or an empty name) is <c>0</c>.</summary>
    public string Net(string net)
    {
        if (net.Length == 0 || net == "0") return "0";
        if (_netToDeck.TryGetValue(net, out var d)) return d;
        d = Unique(Sanitize(net, null));
        _netToDeck[net] = d;
        _deckToNet[d] = net;
        _order.Add((net, d, true));
        return d;
    }

    /// <summary>The deck's name for the element <paramref name="path"/>, beginning with <paramref name="letter"/> (the
    /// dialect reads an element's kind from its first letter). <paramref name="suffix"/> distinguishes the several lines one
    /// element is written as (an inductor's series resistor); the first line written for an element is the one the map
    /// names.</summary>
    public string Instance(string path, char letter, string? suffix = null)
    {
        string key = suffix is null ? path : path + "\u0001" + suffix + "\u0001" + letter;
        if (_pathToDeck.TryGetValue(key, out var d)) return d;
        d = Unique(Sanitize(suffix is null ? path : path + "_" + suffix, letter));
        _pathToDeck[key] = d;
        // the first line an element is written as names it in the map, whether or not it carries a suffix (an SDD is only
        // ever suffixed lines, one per equation)
        if (_mapped.Add(path))
        {
            _deckToPath[d] = path;
            _order.Add((path, d, false));
        }
        return d;
    }

    /// <summary>A model card's name for the element <paramref name="path"/>: <paramref name="prefix"/> and the sanitised path,
    /// made unique without regard to case as every other deck name is — <c>D1</c> and <c>d1</c>, or <c>X1.D1</c> and
    /// <c>X1_D1</c>, would otherwise write two <c>.model</c> cards of one name, and the dialect run one device on the other's
    /// card.</summary>
    public string Model(string prefix, string path) => Unique(prefix + Sanitize(path, null));

    /// <summary>A fresh internal node name derived from <paramref name="hint"/>, used only inside the deck.</summary>
    public string Internal(string hint)
    {
        string d = Unique(Sanitize("_" + hint, null));
        return d;
    }

    /// <summary>The circuitRF net a deck name stands for, or null.</summary>
    public string? NetOf(string deckName) => _deckToNet.TryGetValue(deckName, out var n) ? n : deckName == "0" ? "0" : null;

    /// <summary>The circuitRF instance path a deck element name stands for, or null.</summary>
    public string? PathOf(string deckName) => _deckToPath.TryGetValue(deckName, out var p) ? p : null;

    /// <summary>Every net the deck names, circuitRF name to deck name, in the order they were first named.</summary>
    public IReadOnlyList<(string Net, string Deck)> Nets => [.. _order.Where(o => o.IsNet).Select(o => (o.CircuitRf, o.Deck))];

    /// <summary>Every element the deck names, circuitRF instance path to deck name, in the order they were first named.</summary>
    public IReadOnlyList<(string Path, string Deck)> Instances => [.. _order.Where(o => !o.IsNet).Select(o => (o.CircuitRf, o.Deck))];

    private string Unique(string name)
    {
        if (_taken.Add(name)) return name;
        for (int k = 2; ; k++)
        {
            string candidate = $"{name}_{k}";
            if (_taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>Letters, digits and underscore only; anything else becomes an underscore. An element name starts with its
    /// letter.</summary>
    internal static string Sanitize(string raw, char? letter)
    {
        var sb = new StringBuilder(raw.Length + 2);
        foreach (char ch in raw)
            sb.Append(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? ch : '_');
        if (sb.Length == 0) sb.Append('_');
        if (letter is char l)
        {
            if (char.ToUpperInvariant(sb[0]) != char.ToUpperInvariant(l)) sb.Insert(0, l + "_");
            else if (sb[0] != l) sb[0] = l;
        }
        return sb.ToString();
    }
}
