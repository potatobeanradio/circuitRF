// HZN-9 R-hzn9-2 — a 3D design of a Hertzon project (.hzn) dragged out of the Project Tree, so it can be dropped into a 3D
// view as an instance. A prefixed text string, as CellDragPayload is, so it travels on the native pasteboard and only a
// target that understands it acts on it (no tree reads this prefix, so it cannot be mistaken for a file move).

namespace CircuitRF.Ui.ThreeD;

public sealed record HznDesignDragPayload(string HznAbsPath, string Design)
{
    private const string Prefix = "circuitrf-hzndesign:";

    /// <summary><c>circuitrf-hzndesign:&lt;absolute .hzn path&gt;\n&lt;design name&gt;</c>.</summary>
    public string Serialize() => $"{Prefix}{HznAbsPath}\n{Design}";

    /// <summary>Parses a string produced by <see cref="Serialize"/>; false for anything else.</summary>
    public static bool TryParse(string? s, out HznDesignDragPayload result)
    {
        result = default!;
        if (s is null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string rest = s[Prefix.Length..];
        int nl = rest.IndexOf('\n');
        if (nl <= 0 || nl == rest.Length - 1) return false;
        result = new HznDesignDragPayload(rest[..nl], rest[(nl + 1)..]);
        return true;
    }
}
