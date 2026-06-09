namespace CircuitRF.Ui.Schematic;

// ── Item 8: Central component-type metadata registry ─────────────────────────
// Keyed by SymbolKind for now; when the component model gains a richer type
// system (v2, real component-model factory), re-key off that type instead.
// Lives here (Avalonia-free) so the renderer, palette, and auto-naming all share it.

/// <summary>Physical dimension of a component parameter — drives the closed Unit ComboBox.</summary>
public enum UnitDimension
{
    None,
    Resistance,
    Inductance,
    Capacitance,
    Frequency,
    Voltage,
    Current,
    Power,
    Length,
    Angle,
}

/// <summary>One entry in the default parameter template for a freshly-placed component.</summary>
public readonly record struct DefaultParam(string Name, string Expression, string Unit, bool ShowOnSchematic, UnitDimension Dimension = UnitDimension.None);

/// <summary>Display metadata for one component type.</summary>
public sealed record ComponentTypeInfo(
    /// <summary>Short label shown on the schematic (e.g. "R", "C", "FET").</summary>
    string DisplayName,
    /// <summary>Prefix used when auto-generating instance names (e.g. "R", "C", "X").</summary>
    string InstancePrefix,
    /// <summary>Whether to show the type label by default when a component is placed.</summary>
    bool DefaultShowTypeLabel = true,
    /// <summary>Whether to show the instance name by default when a component is placed.</summary>
    bool DefaultShowInstanceName = true);

/// <summary>
/// Maps SymbolKind → display metadata.
/// Use <see cref="DisplayName(SymbolKind,int)"/> for the on-schematic type label instead of SymbolKind.ToString().
/// </summary>
public static class ComponentTypeRegistry
{
    private static readonly Dictionary<UnitDimension, string[]> _unitOptions = new()
    {
        [UnitDimension.None]        = ["None"],
        [UnitDimension.Resistance]  = ["None", "mΩ", "Ω", "kΩ", "MΩ", "GΩ"],
        [UnitDimension.Inductance]  = ["None", "pH", "nH", "µH", "mH", "H"],
        [UnitDimension.Capacitance] = ["None", "fF", "pF", "nF", "µF", "mF", "F"],
        [UnitDimension.Frequency]   = ["None", "Hz", "kHz", "MHz", "GHz", "THz"],
        [UnitDimension.Voltage]     = ["None", "nV", "µV", "mV", "V", "kV"],
        [UnitDimension.Current]     = ["None", "nA", "µA", "mA", "A"],
        [UnitDimension.Power]       = ["None", "fW", "pW", "nW", "µW", "mW", "W", "dBm"],
        [UnitDimension.Length]      = ["None", "nm", "µm", "mm", "cm", "m", "mil"],
        [UnitDimension.Angle]       = ["None", "deg", "rad"],
    };

    /// <summary>Closed list of unit strings for a given physical dimension. Always has "None" at index 0.</summary>
    public static string[] UnitOptions(UnitDimension dim)
        => _unitOptions.TryGetValue(dim, out var opts) ? opts : _unitOptions[UnitDimension.None];

    private static readonly Dictionary<SymbolKind, ComponentTypeInfo> Registry = new()
    {
        [SymbolKind.Resistor]      = new("R",     "R"),
        [SymbolKind.Inductor]      = new("L",     "L"),
        [SymbolKind.Capacitor]     = new("C",     "C"),
        [SymbolKind.VoltageSource] = new("V",     "V"),
        [SymbolKind.ToneSource]    = new("VTone", "V"),   // NOTE: abbreviation; revisit when palette has richer type
        // Ground is self-identifying via its symbol glyph; suppress both labels by default.
        [SymbolKind.Ground]        = new("GND",   "GND",  DefaultShowTypeLabel: false, DefaultShowInstanceName: false),
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
    /// Port-count-aware display name.
    /// <list type="bullet">
    ///   <item>ZPort with portCount ≥ 1 → "Z{portCount}P" (portCount = N, the number of network ports).</item>
    ///   <item>Sdd with portCount ≥ 1 → "SDD{portCount}".</item>
    ///   <item>All other kinds → <see cref="DisplayName(SymbolKind)"/>.</item>
    /// </list>
    /// </summary>
    public static string DisplayName(SymbolKind kind, int portCount)
    {
        if (kind == SymbolKind.ZPort && portCount >= 1) return $"Z{portCount}P";
        if (kind == SymbolKind.Sdd   && portCount >= 1) return $"SDD{portCount}";
        return DisplayName(kind);
    }

    /// <summary>Instance-name prefix for auto-naming (e.g. "R", "C", "X").</summary>
    public static string InstancePrefix(SymbolKind kind) => Get(kind).InstancePrefix;

    /// <summary>
    /// Default parameter template for a freshly-placed component of the given type and port count.
    /// Each entry carries the parameter name, a blank default expression, unit, and whether
    /// it shows on the schematic. This is the single source of truth for "what params does a
    /// newly-placed component have and show."
    ///
    /// ZPort: generates NumPorts (= N, hidden) then the full N×N Z[i,j] matrix (1-based i,j), matching
    /// ZPortModel's Z[i,j] convention. Works for any N ≥ 1 — no hardcoded 1/2/3 cases.
    ///
    /// Sdd: SddModel equations are user-authored. Default is minimal: only NumPorts (= N, hidden).
    /// The engine reads the equation slots at elaboration time; no universal per-port param default exists.
    /// Checked against SddModel constructor: expects portCount, currentAst[], chargeAst[], params dict —
    /// the per-port equation arrays are filled in by the user; no registry default beyond port count.
    ///
    /// Parameter names match what the engine/device models expect at elaboration time, with
    /// one known exception: VoltageSource shows Vac/Freq here while VoltageSourceModel currently
    /// reads "V" — the schematic params and the model will converge when the model is updated.
    /// </summary>
    public static IReadOnlyList<DefaultParam> DefaultParameters(SymbolKind kind, int portCount)
    {
        switch (kind)
        {
            case SymbolKind.Resistor:      return [new("R",    "1", "Ω",   true, UnitDimension.Resistance)];
            case SymbolKind.Inductor:      return [new("L",    "1", "nH",  true, UnitDimension.Inductance)];
            case SymbolKind.Capacitor:     return [new("C",    "1", "pF",  true, UnitDimension.Capacitance)];
            // NOTE: VoltageSourceModel currently reads "V"; these names are the intended schematic-layer params.
            case SymbolKind.VoltageSource: return [new("Vac",  "1", "V",   true, UnitDimension.Voltage),
                                                   new("Freq", "2", "GHz", true, UnitDimension.Frequency)];
            // V and Freq match V_1Tone factory keys (V= amplitude, Freq= frequency in Hz).
            case SymbolKind.ToneSource:    return [new("V",    "1", "V",   true, UnitDimension.Voltage),
                                                   new("Freq", "2", "GHz", true, UnitDimension.Frequency)];

            case SymbolKind.ZPort:
            {
                int n = portCount >= 1 ? portCount : 2;
                var ps = new List<DefaultParam>(1 + n * n) { new("NumPorts", $"{n}", "", false, UnitDimension.None) };
                for (int p = 1; p <= n; p++)
                    for (int q = 1; q <= n; q++)
                        ps.Add(new($"Z[{p},{q}]", "50", "Ω", true, UnitDimension.Resistance));
                return ps;
            }

            // SDD equations are user-authored; only expose NumPorts as a default param.
            // The engine reads per-port current/charge equations separately from the param dict.
            case SymbolKind.Sdd:
            {
                int n = portCount >= 1 ? portCount : 2;
                return [new("NumPorts", $"{n}", "", false, UnitDimension.None)];
            }

            // Ground/Port/FetSdd/Generic need no default parameters.
            default: return [];
        }
    }

    /// <summary>
    /// Parses a short type code (case-insensitive) to a SymbolKind and, for variadic-port types,
    /// the parsed port count N.
    ///
    /// Canonical codes: R, L, C, V, VTone, GND, Port/P, FET/SDD/FetSDD, Z{N}P (any N ≥ 1),
    /// SDD{N} (any N ≥ 1), X.
    ///
    /// <list type="bullet">
    ///   <item>Z{N}P (e.g. Z2P, Z5P) → (ZPort, portCount=N)</item>
    ///   <item>SDD{N} (e.g. SDD2, SDD3) → (Sdd, portCount=N)</item>
    ///   <item>SDD / FET / FetSDD (no number) → (FetSdd, portCount=0) — existing 3-port SDD FET device</item>
    ///   <item>All other codes → portCount=0 (use type's built-in default from SymbolPortDefs)</item>
    /// </list>
    ///
    /// Falls back to enum-name parsing if no short code matches.
    /// </summary>
    public static bool TryParseCode(string input, out SymbolKind kind, out int portCount)
    {
        kind      = default;
        portCount = 0;
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
            case "FETSDD": kind = SymbolKind.FetSdd;        return true;  // aliases for the same device; portCount=0 → 3-port default
            case "X":      kind = SymbolKind.Generic;       return true;

            default:
                // Z{N}P — variadic Z-port network (e.g. Z1P, Z2P, Z5P).
                if (s.Length >= 3 && s[0] == 'Z' && s[^1] == 'P' &&
                    int.TryParse(s[1..^1], out int zn) && zn >= 1)
                {
                    kind      = SymbolKind.ZPort;
                    portCount = zn;
                    return true;
                }
                // SDD{N} — generic variadic SDD device (e.g. SDD2, SDD3).
                if (s.Length >= 4 && s.StartsWith("SDD", StringComparison.Ordinal) &&
                    int.TryParse(s[3..], out int sn) && sn >= 1)
                {
                    kind      = SymbolKind.Sdd;
                    portCount = sn;
                    return true;
                }
                return Enum.TryParse(input, ignoreCase: true, out kind);
        }
    }
}
