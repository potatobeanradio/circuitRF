namespace CircuitRF.Ui.Schematic;

// ── Item 8: Central component-type metadata registry ─────────────────────────
// Keyed by SymbolKind for now; when the component model gains a richer type
// system (v2, real component-model factory), re-key off that type instead.
// Lives here (Avalonia-free) so the renderer, palette, and auto-naming all share it.

/// <summary>One entry in the default parameter template for a freshly-placed component.</summary>
public readonly record struct DefaultParam(string Name, string Expression, string Unit, bool ShowOnSchematic);

/// <summary>Display metadata for one component type.</summary>
public sealed record ComponentTypeInfo(
    /// <summary>Short label shown on the schematic (e.g. "R", "C", "FET").</summary>
    string DisplayName,
    /// <summary>Prefix used when auto-generating instance names (e.g. "R", "C", "X").</summary>
    string InstancePrefix);

/// <summary>
/// Maps SymbolKind → display metadata.
/// Use <see cref="DisplayName"/> for the on-schematic type label instead of SymbolKind.ToString().
/// </summary>
public static class ComponentTypeRegistry
{
    private static readonly Dictionary<SymbolKind, ComponentTypeInfo> Registry = new()
    {
        [SymbolKind.Resistor]      = new("R",     "R"),
        [SymbolKind.Inductor]      = new("L",     "L"),
        [SymbolKind.Capacitor]     = new("C",     "C"),
        [SymbolKind.VoltageSource] = new("V",     "V"),
        [SymbolKind.ToneSource]    = new("VTone", "V"),   // NOTE: abbreviation; revisit when palette has richer type
        [SymbolKind.Ground]        = new("GND",   "GND"),
        [SymbolKind.Port]          = new("Port",  "P"),
        [SymbolKind.FetSdd]        = new("FET",   "X"),
        [SymbolKind.Sdd]           = new("SDD",   "X"),
        [SymbolKind.ZPort]         = new("Z",     "Z"),
        [SymbolKind.Generic]       = new("X",     "X"),
    };

    /// <summary>Returns the full metadata for a SymbolKind; falls back to a generic entry if unknown.</summary>
    public static ComponentTypeInfo Get(SymbolKind kind)
        => Registry.TryGetValue(kind, out var info) ? info : new(kind.ToString(), "X");

    /// <summary>Short display name for the on-schematic type label (e.g. "R", "C", "FET").</summary>
    public static string DisplayName(SymbolKind kind) => Get(kind).DisplayName;

    /// <summary>
    /// Port-count-aware display name. For ZPort, returns "Z{N}P" where N = portCount/2 (nodes=2N).
    /// Falls back to <see cref="DisplayName(SymbolKind)"/> for all other kinds.
    /// </summary>
    public static string DisplayName(SymbolKind kind, int portCount)
        => kind == SymbolKind.ZPort && portCount >= 2
            ? $"Z{portCount / 2}P"
            : DisplayName(kind);

    /// <summary>Instance-name prefix for auto-naming (e.g. "R", "C", "X").</summary>
    public static string InstancePrefix(SymbolKind kind) => Get(kind).InstancePrefix;

    /// <summary>
    /// Default parameter template for a freshly-placed component of the given type.
    /// Each entry carries the parameter name, a blank default expression, unit, and whether
    /// it shows on the schematic. This is the single source of truth for "what params does a
    /// newly-placed component have and show."
    ///
    /// Parameter names match what the engine/device models expect at elaboration time, with
    /// one known exception: VoltageSource shows Vac/Freq here while VoltageSourceModel currently
    /// reads "V" — the schematic params and the model will converge when the model is updated.
    /// </summary>
    public static IReadOnlyList<DefaultParam> DefaultParameters(SymbolKind kind, int portCount) => kind switch
    {
        SymbolKind.Resistor      => [new("R",    "1", "ohm", true)],
        SymbolKind.Inductor      => [new("L",    "1", "nH",  true)],
        SymbolKind.Capacitor     => [new("C",    "1", "pF",  true)],
        // NOTE: VoltageSourceModel currently reads "V"; these names are the intended schematic-layer params.
        SymbolKind.VoltageSource => [new("Vac",  "1", "V",   true), new("Freq", "2", "GHz", true)],
        // V and Freq match V_1Tone factory keys (V= amplitude, Freq= frequency in Hz).
        SymbolKind.ToneSource    => [new("V",    "1", "V",   true), new("Freq", "2", "GHz", true)],
        // Z[1,1] matches the Z_Port factory's matrix-entry key convention.
        SymbolKind.Z1P         => [new("NumPorts", "1", "", false), new("Z[1,1]", "50", "ohm", true)],
        SymbolKind.Z2P         => [new("NumPorts", "2", "", false),
                                    new("Z[1,1]", "50", "ohm", true), new("Z[1,2]", "50", "ohm", true),
                                    new("Z[2,1]", "50", "ohm", true), new("Z[2,2]", "50", "ohm", true)],
        SymbolKind.Z3P         => [new("NumPorts", "3", "", false),
                                    new("Z[1,1]", "50", "ohm", true), new("Z[1,2]", "50", "ohm", true), new("Z[1,3]", "50", "ohm", true),
                                    new("Z[2,1]", "50", "ohm", true), new("Z[2,2]", "50", "ohm", true), new("Z[2,3]", "50", "ohm", true),
                                    new("Z[3,1]", "50", "ohm", true), new("Z[3,2]", "50", "ohm", true), new("Z[3,3]", "50", "ohm", true)],
        // SDD equations are user-authored; no universal default param set.
        // Ground/Port/Generic need no default parameters.
        _                        => [],
    };

    /// <summary>
    /// Parses a short type code (case-insensitive) to a SymbolKind.
    /// Canonical codes: R, L, C, V, VTone, GND, Port/P, FET/SDD/FetSDD, Z1P/Z2P/…, X.
    /// SDD and FetSDD are aliases for FetSdd (same device).
    /// ZNP codes (any N) resolve to ZPort; port count is set separately in the component model.
    /// Falls back to enum-name parsing if no short code matches.
    /// </summary>
    public static bool TryParseCode(string input, out SymbolKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim().ToUpperInvariant();
        switch (s)
        {
            case "R":      kind = SymbolKind.Resistor;      return true;
            case "L":      kind = SymbolKind.Inductor;      return true;
            case "C":      kind = SymbolKind.Capacitor;     return true;
            case "V":      kind = SymbolKind.VoltageSource; return true;
            case "VTONE":  kind = SymbolKind.ToneSource;    return true;
            case "GND":    kind = SymbolKind.Ground;        return true;
            case "PORT":
            case "P":      kind = SymbolKind.Port;          return true;
            case "FET":
            case "SDD":
            case "FETSDD": kind = SymbolKind.FetSdd;        return true;  // aliases for the same device
            case "Z1P":    kind = SymbolKind.Z1P;           return true;
            case "Z2P":    kind = SymbolKind.Z2P;           return true;
            case "Z3P":    kind = SymbolKind.Z3P;           return true;
            case "X":      kind = SymbolKind.Generic;       return true;
            
            default:
                // ZNP codes (ZNP ) all map to ZPort; port count must be stored separately.
                if (s.Length >= 3 && s[0] == 'Z' && s[^1] == 'P' &&
                    int.TryParse(s[1..^1], out _))
                {
                    kind = SymbolKind.ZPort;
                    return true;
                }
                return Enum.TryParse(input, ignoreCase: true, out kind);
        }
    }
}
