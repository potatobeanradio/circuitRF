// brief-dfr31-1 R-dfr31-1-3 — what a terminal or a ground may be named by, resolved to the problem's conductor objects.
//
// A person types what they can see: an object's full path as a refusal printed it, a net they gave a shape in the 2D layout,
// or the end of a path ("TESTPT" for ".../Bottom Copper (1 oz)/TESTPT"). Tried in that order, so an exact path always wins
// over a net of the same spelling and a net over a suffix. A suffix that ends more than one path is refused with them listed,
// never resolved to the first; a name that matches nothing is refused with the nearest few names, which is the list the
// person was missing.

using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>brief-dfr31-1 — the names a static terminal may use, over one problem's conductors.</summary>
public sealed class Em3dConductorNames
{
    /// <summary>How many near names a refusal lists.</summary>
    public const int NearNames = 5;

    private readonly IReadOnlyList<string> _conductors;
    private readonly HashSet<string> _conductorSet;
    private readonly Dictionary<string, List<string>> _onNet = new(StringComparer.Ordinal);

    public Em3dConductorNames(IReadOnlyList<string> conductors, IReadOnlyDictionary<string, HashSet<string>> nets)
    {
        _conductors = conductors;
        _conductorSet = new HashSet<string>(conductors, StringComparer.Ordinal);
        foreach (string c in conductors)
            if (nets.TryGetValue(c, out var set))
                foreach (string n in set)
                    (_onNet.TryGetValue(n, out var l) ? l : _onNet[n] = []).Add(c);
    }

    /// <summary>How a name resolved: the objects, or why not (with the candidates or near names).</summary>
    public sealed record Resolution(IReadOnlyList<string> Objects, string How, IReadOnlyList<string> Ambiguous, IReadOnlyList<string> Near)
    {
        public bool Ok => Objects.Count > 0;
    }

    /// <summary>The conductors on net <paramref name="net"/>, in problem order.</summary>
    public IReadOnlyList<string> OnNet(string net) => _onNet.TryGetValue(net, out var l) ? l : [];

    /// <summary>An exact object path, else a net, else a unique path suffix (R-dfr31-1-3).</summary>
    public Resolution Resolve(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return new([], "", [], []);
        if (_conductorSet.Contains(name)) return new([name], "object", [], []);
        // A generated body name (Em3dBodies) is its largest member's path: the picker writes it, so it resolves.
        if (name.StartsWith(Em3dBodies.GeneratedPrefix, StringComparison.Ordinal) &&
            _conductorSet.Contains(name[Em3dBodies.GeneratedPrefix.Length..]))
            return new([name[Em3dBodies.GeneratedPrefix.Length..]], "body", [], []);
        if (_onNet.TryGetValue(name, out var onNet)) return new(onNet, "net", [], []);
        var suffix = _conductors.Where(c => c.EndsWith("/" + name, StringComparison.Ordinal)).ToList();
        if (suffix.Count == 1) return new(suffix, "suffix", [], []);
        if (suffix.Count > 1) return new([], "", suffix, []);
        return new([], "", [], Near(name));
    }

    /// <summary>Up to <see cref="NearNames"/> object paths and nets closest to <paramref name="name"/>, nearest first.</summary>
    public IReadOnlyList<string> Near(string name)
    {
        string key = name.Trim().ToLowerInvariant();
        static string Last(string p) => p[(p.LastIndexOf('/') + 1)..];
        return [.. _conductors.Concat(_onNet.Keys).Distinct(StringComparer.Ordinal)
            .Select(c => (Name: c, D: Math.Min(Distance(key, Last(c).ToLowerInvariant()),
                                               Last(c).Contains(key, StringComparison.OrdinalIgnoreCase) ||
                                               key.Contains(Last(c).ToLowerInvariant(), StringComparison.Ordinal) ? 1 : int.MaxValue)))
            .OrderBy(x => x.D).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(NearNames).Select(x => x.Name)];
    }

    /// <summary>A refusal's tail: the near names, or nothing when there are none.</summary>
    public static string NearText(IReadOnlyList<string> near)
        => near.Count == 0 ? "" : $" The nearest names are {string.Join(", ", near.Select(n => $"'{n}'"))}.";

    /// <summary>A refusal's sentence for a suffix that ends several paths.</summary>
    public static string AmbiguousText(string name, IReadOnlyList<string> candidates)
        => $"'{name}' ends {candidates.Count.ToString(CultureInfo.InvariantCulture)} conductors' names — " +
           string.Join(", ", candidates.Take(8).Select(n => $"'{n}'")) + (candidates.Count > 8 ? ", …" : "") +
           " — so it names none of them. Give the full path of the one meant.";

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>
/// brief-dfr34-4 R-dfr34-4-1 — a setup's ground (Ground3D) as a list: comma-separated names, as a terminal's conductors are,
/// with <see cref="DefaultEntry"/> as an entry meaning the default ground (the ground-reference layers and the metal touching
/// them). Blank is the default ground alone; a list without the entry is exactly the names, which is what one name always
/// meant, so a file written before the list reads as it did.
/// </summary>
public static class Em3dGroundList
{
    /// <summary>The entry that stands for the default ground.</summary>
    public const string DefaultEntry = "+ default ground";

    /// <summary>Every entry of <paramref name="ground3D"/>, trimmed, blanks dropped, in order.</summary>
    public static IReadOnlyList<string> Entries(string? ground3D)
        => [.. (ground3D ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    private static bool IsDefault(string entry)
        => string.Equals(entry.TrimStart('+').Trim(), DefaultEntry[2..], StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the default ground is part of the ground: the box is blank, or lists <see cref="DefaultEntry"/>.</summary>
    public static bool IncludesDefault(string? ground3D)
    {
        var entries = Entries(ground3D);
        return entries.Count == 0 || entries.Any(IsDefault);
    }

    /// <summary>The named entries: every entry but <see cref="DefaultEntry"/>.</summary>
    public static IReadOnlyList<string> Names(string? ground3D) => [.. Entries(ground3D).Where(e => !IsDefault(e))];

    /// <summary>The ground with <paramref name="name"/> added to it: a blank ground keeps the default ground as its first entry,
    /// and a name already listed is not listed twice.</summary>
    public static string Add(string? ground3D, string name)
    {
        name = name.Trim();
        var entries = Entries(ground3D).ToList();
        if (entries.Count == 0) entries.Add(DefaultEntry);
        if (name.Length > 0 && !entries.Contains(name, StringComparer.Ordinal)) entries.Add(name);
        return string.Join(", ", entries);
    }

    /// <summary>How a sentence names the ground: "the ground-reference conductors", the names, or both.</summary>
    public static string Describe(string? ground3D)
    {
        var names = Names(ground3D);
        var parts = new List<string>();
        if (IncludesDefault(ground3D)) parts.Add("the ground-reference conductors");
        parts.AddRange(names.Select(n => $"'{n}'"));
        return string.Join(" and ", parts);
    }
}
