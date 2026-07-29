using System.Linq;

namespace CircuitRF.Ui.Schematic;

// ── Item 8: Central component-type metadata registry ─────────────────────────
// Keyed by SymbolKind for now; when the component model gains a richer type
// system (v2, real component-model factory), re-key off that type instead.
// Lives here (Avalonia-free) so the renderer, palette, and auto-naming all share it.

/// <summary>
/// Palette category for grouping components in the Library Palette header ComboBox.
/// All/Common/RecentlyUsed are virtual categories (filters); these are the real ones.
/// </summary>
public enum ComponentCategory
{
    Lumped,
    TransmissionLine,
    Microstrip,
    Sources,
    DataFiles,
    Terminals,
    Other,
}

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

/// <summary>
/// Describes how the "+" and "−" buttons work for a user-extensible component type.
/// Each "group" consists of one or more parameters sharing the same integer index (e.g.
/// Freq[2]/V[2]/Phase[2] for ToneSource tone 2, or Z[0] for P1Tone DC harmonic).
/// </summary>
public sealed record IndexedParamGroup(
    /// <summary>
    /// Format strings (using {0} for the index) for each name in one group.
    /// Example: ["Freq[{0}]","V[{0}]","Phase[{0}]"] for ToneSource.
    /// The first format is the "primary" used by FindTopGroupIndex.
    /// </summary>
    string[] NameFormats,
    /// <summary>Default unit for each format entry (parallel; last value repeated if shorter).</summary>
    string[] DefaultUnits,
    /// <summary>ShowOnSchematic default per format entry (parallel; false when shorter).</summary>
    bool[] ShowOnSchematic,
    /// <summary>UnitDimension per format entry (parallel; None when shorter).</summary>
    UnitDimension[] Dimensions,
    /// <summary>Lowest index that the user may add via "+" (e.g. 0 for P1Tone, 2 for ToneSource).</summary>
    int FirstAddIndex = 0,
    /// <summary>Indices that must not be added (reserved/fixed). Null = no skips.</summary>
    int[]? SkipIndices = null)
{
    /// <summary>True if the given index is in the skip set.</summary>
    public bool IsSkipped(int index) => SkipIndices is not null && SkipIndices.Contains(index);
}

/// <summary>Display metadata for one component type.</summary>
public sealed record ComponentTypeInfo(
    /// <summary>Short label shown on the schematic (e.g. "R", "C", "FET").</summary>
    string DisplayName,
    /// <summary>Prefix used when auto-generating instance names (e.g. "R", "C", "X").</summary>
    string InstancePrefix,
    /// <summary>Whether to show the type label by default when a component is placed.</summary>
    bool DefaultShowTypeLabel = true,
    /// <summary>Whether to show the instance name by default when a component is placed.</summary>
    bool DefaultShowInstanceName = true,
    /// <summary>Primary palette category (drives sort order in AllItems and the on-tile tooltip).</summary>
    ComponentCategory Category = ComponentCategory.Other,
    /// <summary>
    /// Search terms for the Library Palette: display name, type code, and aliases (e.g. "cap", "res", "ind").
    /// Null = no aliases beyond what Search derives from DisplayName + Category.
    /// </summary>
    IReadOnlyList<string>? SearchTerms = null,
    /// <summary>True = shown in the curated Common virtual category.</summary>
    bool IsCommon = false,
    /// <summary>
    /// Additional categories this component belongs to. The item appears under <see cref="Category"/>
    /// AND each extra category in ByCategory filtering. AllItems still lists it once, sorted by
    /// <see cref="Category"/>. Null means single-category.
    /// </summary>
    IReadOnlyList<ComponentCategory>? ExtraCategories = null);

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
        [SymbolKind.Resistor]      = new("R",     "R",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["R", "Resistor", "res", "resistance"],
            IsCommon: true),
        [SymbolKind.Inductor]      = new("L",     "L",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["L", "Inductor", "ind", "coil", "inductance"],
            IsCommon: true),
        [SymbolKind.Capacitor]     = new("C",     "C",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["C", "Capacitor", "cap", "capacitance"],
            IsCommon: true),
        [SymbolKind.Vdc]           = new("Vdc",   "V",
            Category: ComponentCategory.Sources,
            SearchTerms: ["Vdc", "DC", "bias", "supply", "voltage", "V"],
            IsCommon: true),
        // NOTE: abbreviation; revisit when palette has richer type
        [SymbolKind.ToneSource]    = new("VTone", "V",
            Category: ComponentCategory.Sources,
            SearchTerms: ["VTone", "ToneSource", "tone", "RF", "signal"],
            IsCommon: true),
        // Ground is self-identifying via its symbol glyph; suppress both labels by default.
        [SymbolKind.Ground]        = new("GND",   "GND",
            DefaultShowTypeLabel: false, DefaultShowInstanceName: false,
            Category: ComponentCategory.Terminals,
            SearchTerms: ["GND", "Ground", "gnd", "reference"],
            IsCommon: true),
        [SymbolKind.Term]          = new("Term",  "Term",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["Term", "T", "port", "sparam", "termination"],
            IsCommon: true),
        // Pin: interface terminal — connectivity only, no electrical model.
        // Num/Name labels identify the port; type and instance-name labels suppressed (Num shows instead).
        [SymbolKind.Pin]           = new("Pin",   "Pin",
            DefaultShowTypeLabel: false, DefaultShowInstanceName: false,
            Category: ComponentCategory.Terminals,
            SearchTerms: ["Pin", "P", "io", "terminal", "interface", "port"],
            IsCommon: true),
        // IProbe: 0 V series ammeter. Instance name (IP1, IP2, …) identifies the DC result cube I:IP1.
        [SymbolKind.IProbe]        = new("IProbe", "IP",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["IProbe", "I", "ammeter", "current", "probe", "meter"],
            IsCommon: true),
        [SymbolKind.FetSdd]        = new("FET",   "X",
            Category: ComponentCategory.Other,
            SearchTerms: ["FET", "SDD", "FetSDD", "transistor", "nonlinear"]),
        [SymbolKind.Sdd]           = new("SDD",   "X",
            Category: ComponentCategory.Other,
            SearchTerms: ["SDD", "Sdd", "nonlinear", "behavioral"]),
        [SymbolKind.ZPort]         = new("Z",     "Z",
            Category: ComponentCategory.Other,
            SearchTerms: ["Z", "ZPort", "impedance", "network"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.Generic]       = new("X",     "X",
            Category: ComponentCategory.Other,
            SearchTerms: ["X", "Generic", "custom", "subcircuit"]),
        [SymbolKind.Var]           = new("VAR",   "VAR",
            Category: ComponentCategory.Other,
            SearchTerms: ["VAR", "Variable", "var", "vars", "parameter", "sweep"],
            IsCommon: true),
        [SymbolKind.Meas]          = new("MEAS",  "MEAS",
            Category: ComponentCategory.Other,
            SearchTerms: ["MEAS", "Measurement", "measure", "meas", "equation", "eqn"],
            IsCommon: true),
        [SymbolKind.P1Tone]        = new("P1Tone", "P",
            Category: ComponentCategory.Sources,
            SearchTerms: ["P1Tone", "power", "Pavl", "available power", "RF source", "drive", "harmonic"],
            IsCommon: true),
        [SymbolKind.PnTone]        = new("PnTone", "P",
            Category: ComponentCategory.Sources,
            SearchTerms: ["PnTone", "multi-tone", "two-tone", "2-tone", "two tone", "IM3", "intermod", "power", "Pavl", "RF source"],
            IsCommon: true),
        [SymbolKind.Snp]           = new("SnP",   "S",
            Category: ComponentCategory.DataFiles,
            SearchTerms: ["SnP", "Touchstone", "snp", "s2p", "sparam file", "data file", "network"],
            IsCommon: true),
        [SymbolKind.NonlinearC]    = new("NonlinearC",   "C",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["NLC", "NonlinearC", "nonlinear capacitor", "nonlinear", "varactor", "varicap", "CV", "C(V)"],
            IsCommon: false),
        [SymbolKind.Mutual]        = new("M",   "M",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["Mutual", "mutual", "M", "coupling", "inductance", "transformer"],
            IsCommon: false),
        [SymbolKind.Tline]         = new("TLIN", "TL",
            Category: ComponentCategory.TransmissionLine,
            SearchTerms: ["TLIN", "TLine", "transmission line", "tline", "ideal", "lossless", "line"],
            IsCommon: true),
        // Tuner: general programmable RF termination (loadpull.md §1). Single DUT-facing pin;
        // the reference net is hard-coded ground at extraction. Appears under Terminals + Sources.
        [SymbolKind.Tuner]         = new("Tuner", "Tuner",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["Tuner", "tuner", "loadpull", "load pull", "sourcepull", "termination", "Z", "gamma"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Sources]),
        // SourceTuner / LoadTuner: same engine component as Tuner ("Tuner"), different glyph +
        // prefix + single-pin net ordering (loadpull.md §1, §9). Match the symbol to its analysis role.
        [SymbolKind.SourceTuner]   = new("SourceTuner", "SourceTuner",
            Category: ComponentCategory.Sources,
            SearchTerms: ["SourceTuner", "source tuner", "tuner", "sourcepull", "drive", "loadpull"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Terminals]),
        [SymbolKind.LoadTuner]     = new("LoadTuner", "LoadTuner",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["LoadTuner", "load tuner", "tuner", "loadpull", "termination"],
            IsCommon: true),
        // Microstrip built-ins (brief-L5a-pcell-contract-and-microstrip.md) — SymbolKind-registered
        // exactly like Tline, not on-disk cell folders (see src/Ui/CLAUDE.md's L5a note).
        [SymbolKind.Mlin]          = new("MLIN",  "ML",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MLIN", "microstrip", "microstrip line", "line", "hammerstad"],
            IsCommon: true),
        [SymbolKind.MBend]         = new("MBEND", "MB",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MBEND", "microstrip bend", "bend", "corner", "miter"]),
        [SymbolKind.MTee]         = new("MTEE",  "MT",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MTEE", "microstrip tee", "T-junction", "tee", "stub", "power divider"]),
        [SymbolKind.MCross]       = new("MCROSS", "MX",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MCROSS", "microstrip cross", "cross junction", "cross"]),
        // brief-mtaper-mklopf.md
        [SymbolKind.Mtaper]       = new("MTAPER", "MTP",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MTAPER", "microstrip taper", "taper", "linear taper", "width step"]),
        [SymbolKind.Mklopf]       = new("MKLOPF", "MKF",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MKLOPF", "klopfenstein", "klopfenstein taper", "taper", "impedance transformer"]),
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
        if (kind == SymbolKind.Snp   && portCount >= 1) return $"S{portCount}P";
        return DisplayName(kind);
    }

    /// <summary>Instance-name prefix for auto-naming (e.g. "R", "C", "X").</summary>
    public static string InstancePrefix(SymbolKind kind) => Get(kind).InstancePrefix;

    /// <summary>
    /// Engine type-reference string for a given SymbolKind — what goes in the .cnl Reference field
    /// and into <see cref="Instance.Reference"/>. Differs from <see cref="DisplayName(SymbolKind)"/>
    /// for FetSdd ("FET" vs "SDD"), ZPort ("Z" vs "Z_Port"), ToneSource ("VTone" vs "V_1Tone").
    /// </summary>
    public static string EngineReference(SymbolKind kind, int portCount = 0) => kind switch
    {
        SymbolKind.Resistor      => "R",
        SymbolKind.Inductor      => "L",
        SymbolKind.Capacitor     => "C",
        SymbolKind.Vdc           => "Vdc",
        SymbolKind.ToneSource    => "V_1Tone",
        SymbolKind.Term          => "Port",  // engine Reference stays "Port" for .cnl compat
        SymbolKind.Pin           => "Pin",   // sentinel — IsPrimitive("Pin")==false; elaborator skips it
        SymbolKind.IProbe        => "IProbe",
        SymbolKind.FetSdd        => "SDD",
        SymbolKind.Sdd           => "SDD",
        SymbolKind.ZPort         => "Z_Port",
        SymbolKind.Ground        => "GND",
        SymbolKind.Var           => "VAR",   // sentinel — never emitted as an Instance; not a factory primitive
        SymbolKind.Meas          => "MEAS",  // sentinel — never emitted as an Instance; rows route to tb.Measurements
        SymbolKind.P1Tone        => "P1Tone",
        SymbolKind.PnTone        => "PnTone",
        SymbolKind.Snp           => "SnP",
        SymbolKind.NonlinearC    => "NonlinearC",
        SymbolKind.Mutual        => "Mutual",
        SymbolKind.Tline         => "TLIN",
        // All three tuner tiles place the identical engine component.
        SymbolKind.Tuner         => "Tuner",
        SymbolKind.SourceTuner   => "Tuner",
        SymbolKind.LoadTuner     => "Tuner",
        SymbolKind.Mlin          => "MLIN",
        SymbolKind.MBend         => "MBEND",
        SymbolKind.MTee          => "MTEE",
        SymbolKind.MCross        => "MCROSS",
        SymbolKind.Mtaper        => "MTAPER",
        SymbolKind.Mklopf        => "MKLOPF",
        _                        => Get(kind).DisplayName,
    };

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
    /// </summary>
    public static IReadOnlyList<DefaultParam> DefaultParameters(SymbolKind kind, int portCount)
    {
        switch (kind)
        {
            case SymbolKind.Resistor:  return [new("R",   "1", "Ω",   true, UnitDimension.Resistance)];
            case SymbolKind.Inductor:  return [new("L",   "1", "nH",  true, UnitDimension.Inductance)];
            case SymbolKind.Capacitor: return [new("C",   "1", "pF",  true, UnitDimension.Capacitance)];
            case SymbolKind.Vdc:       return [new("Vdc", "0", "V",   true, UnitDimension.Voltage)];
            // V and Freq match V_1Tone factory keys (V= amplitude, Freq= frequency in Hz).
            // Vdc (hidden) provides a DC bias offset on the tone source.
            case SymbolKind.ToneSource: return [new("V",    "1", "V",   true,  UnitDimension.Voltage),
                                                new("Freq", "1", "GHz", true,  UnitDimension.Frequency),
                                                new("Vdc",  "0", "V",   false, UnitDimension.Voltage)];
            // Pavl/Z/Freq/Phase match P1ToneModel factory keys.
            // Num is the s-param port index; auto-assigned at placement from the shared Term+P1Tone pool.
            case SymbolKind.P1Tone: return [
                new("Num",   "1",   "",    true,  UnitDimension.None),
                new("Pavl",  "0",   "dBm", true,  UnitDimension.Power),
                new("Z",    "50",   "Ω",   true,  UnitDimension.Resistance),
                new("Freq",  "1",   "GHz", true,  UnitDimension.Frequency),
                new("Phase", "0",   "deg", false, UnitDimension.Angle)];

            // PnTone: multi-tone power source — seeded with TWO tones so a freshly-placed PnTone is a
            // ready two-tone source. Per-tone Freq[i]/Pavl[i]/Phase[i]; shared Z (= Zdefault reference).
            // The "+"/"−" buttons add/remove tones (UserParamTemplate). Not an S-param port (no Num).
            case SymbolKind.PnTone: return [
                new("Freq[1]",  "1.99", "GHz", true,  UnitDimension.Frequency),
                new("Pavl[1]",  "0",    "dBm", true,  UnitDimension.Power),
                new("Phase[1]", "0",    "deg", false, UnitDimension.Angle),
                new("Freq[2]",  "2.01", "GHz", true,  UnitDimension.Frequency),
                new("Pavl[2]",  "0",    "dBm", true,  UnitDimension.Power),
                new("Phase[2]", "0",    "deg", false, UnitDimension.Angle),
                new("Z",       "50",    "Ω",   true,  UnitDimension.Resistance)];

            case SymbolKind.ZPort:
            {
                int n = portCount >= 1 ? portCount : 2;
                var ps = new List<DefaultParam>(1 + n * n) { new("NumPorts", $"{n}", "", false, UnitDimension.None) };
                for (int p = 1; p <= n; p++)
                    for (int q = 1; q <= n; q++)
                        ps.Add(new($"Z[{p},{q}]", "50", "Ω", true, UnitDimension.Resistance));
                return ps;
            }

            // SDD: seed one I[x,0] = _vx/50 per port so a freshly-placed SDD is functional
            // (acts as N independent 50-Ω conductances) and shows the port-voltage notation.
            case SymbolKind.Sdd:
            {
                int n = portCount >= 1 ? portCount : 2;
                var ps = new List<DefaultParam>(1 + n) { new("NumPorts", $"{n}", "", false, UnitDimension.None) };
                for (int x = 1; x <= n; x++)
                    ps.Add(new($"I[{x},0]", $"_v{x}/50", "", true, UnitDimension.None));
                return ps;
            }

            // Term: Num (port index, auto-assigned at placement) + Z (reference impedance).
            // DefaultParameters emits Num="1" as a placeholder; CommitPlacement overwrites it
            // with the next-free integer among existing Terms in the schematic.
            case SymbolKind.Term:
                return [new("Num", "1",  "",  true,  UnitDimension.None),
                        new("Z",   "50", "Ω", true,  UnitDimension.Resistance)];

            // Pin: Num (interface port index, auto-assigned) + optional Name (port label).
            // Num is auto-assigned at placement (CommitPlacement overrides the "1" placeholder).
            // Name defaults to "" (empty); extraction uses "P{Num}" when Name is blank.
            // Polarity (not in DefaultParameters) may be set via the parameter editor to
            // "Plus" or "Minus" to form a differential port pair sharing the same Num.
            case SymbolKind.Pin:
                return [new("Num",  "1", "", true,  UnitDimension.None),
                        new("Name", "",  "", false, UnitDimension.None)];

            // VAR: parameter rows are user-authored variable definitions; freshly placed VAR has no rows.
            case SymbolKind.Var: return [];

            // MEAS: parameter rows are user-authored measurement equations; freshly placed MEAS has no rows.
            case SymbolKind.Meas: return [];

            // SnP: N-port Touchstone file-backed component.
            // NumPorts (hidden) is required by CnlReader. File, RefNode, PinConfig, Pitch,
            // InterpMode, ExtrapMode are the remaining 6 fixed params.
            case SymbolKind.Snp:
            {
                int n = portCount >= 1 ? portCount : 2;
                return [
                    new("NumPorts",  $"{n}",         "",  false, UnitDimension.None),
                    new("File",      "",              "",  true,  UnitDimension.None),
                    new("RefNode",   "false",         "",  false, UnitDimension.None),
                    new("PinConfig", "Standard",      "",  false, UnitDimension.None),
                    new("Pitch",     "Loose",         "",  false, UnitDimension.None),
                    new("InterpMode","Cubic",         "",  false, UnitDimension.None),
                    new("ExtrapMode","NearestEdge",   "",  false, UnitDimension.None),
                ];
            }

            case SymbolKind.NonlinearC:
                return [new("C0", "1", "pF", true, UnitDimension.Capacitance)];

            // TLIN: ideal lossless transmission line. Z = characteristic impedance (real, lossless),
            // E = electrical length in degrees at reference frequency F. All three show on the schematic.
            case SymbolKind.Tline:
                return [new("Z", "50", "Ω",   true, UnitDimension.Resistance),
                        new("E", "90", "deg", true, UnitDimension.Angle),
                        new("F", "1",  "GHz", true, UnitDimension.Frequency)];

            // Mutual: Inductor1 and Inductor2 are instance-name strings (no unit);
            // M is the mutual inductance value.
            case SymbolKind.Mutual:
                return [new("Inductor1", "\"L1\"", "", true,  UnitDimension.None),
                        new("Inductor2", "\"L2\"", "", true,  UnitDimension.None),
                        new("M",         "0", "pH", true, UnitDimension.Inductance)];

            // Tuner: programmable termination (loadpull.md §1). Z[1] REQUIRED (fundamental); Zdefault is the
            // catch-all (engine default 1e-6); Z0 sets Γ-normalisation for any G[k] form (default 50). BiasTee=
            // "on"/"off" toggles the internal bias-tee + supply; Vbias is the DC bias at the DUT-facing port.
            // BiasTee=on is required by the Loadpull directive (loadpull.md §1.1) — "off" is fine for a standalone
            // tuner. The Loadpull analysis decides the tuned harmonic (TuneHarm) and the role (Load/Source), NOT
            // this component. Z[1] accepts complex literals (e.g. 50+j*10). Add Z[2], Z[3], … via the editor "+".
            // SourceTuner/LoadTuner share this list — they are the same engine component ("Tuner"); only glyph,
            // instance prefix, and single-pin net ordering differ (loadpull.md §1, §9).
            //
            // Γ-vs-Z entry: the engine factory accepts either Z[k] (impedance) or G[k] (reflection coefficient,
            // normalised to Z0) per harmonic — but not both for the same k. The "+" button produces Z[k]; to
            // enter a reflection coefficient instead, rename the row to G[k] and set Z0 (a dedicated Γ/Z picker
            // is future polish, out of scope here).
            //
            // ShowBias is DISPLAY-ONLY (never reaches the engine — dropped at extraction): when true (with
            // BiasTee=on) the glyph draws the embedded bias-tee + DC supply teed off the single DUT-facing lead.
            case SymbolKind.Tuner:
            case SymbolKind.SourceTuner:
            case SymbolKind.LoadTuner:
                return [new("Z[1]",     "50",    "Ω", true,  UnitDimension.Resistance),
                        new("Zdefault", "1e-6",  "Ω", false, UnitDimension.Resistance),
                        new("Z0",       "50",    "Ω", false, UnitDimension.Resistance),
                        new("BiasTee",  "off",   "",  false, UnitDimension.None),
                        new("Vbias",    "0",     "V", false, UnitDimension.Voltage),
                        new("ShowBias", "false", "",  false, UnitDimension.None)];

            // MLIN: microstrip line — W (width), L (length). Both SI length, defaulted in mm here
            // — a fixed, technology-independent baseline; MicrostripSubstrateInjection.
            // ApplyTechnologyLengthUnit rewrites these to the placing workspace's own
            // DefaultDisplayUnit (mil for PCB, um for MMIC) right after placement, same physical
            // magnitude. 2.9mm/10mm is the ~50 Ω-on-1.6mm-FR4 hero (docs/design/microstrip-models.md's
            // own worked reference). The unit belongs ONLY in the Unit field — DefaultParam's
            // Expression is always a bare number in that unit (matching every other entry in this
            // file, e.g. Resistor's `new("R", "1", "Ω", ...)`); embedding it in BOTH was a real
            // bug (a component showed "2.9mm mm").
            // Substrate (H/T/Er/Sigma/TanD) is NOT a declared parameter (R-pc-2's "one list" —
            // it is resolved automatically from the workspace technology at extraction time, per
            // R-pc-8, and injected as elaborator overrides; only an explicit per-instance layer
            // override, not exposed here, changes which layer it resolves from).
            case SymbolKind.Mlin:
                return [new("W", "2.9", "mm", true, UnitDimension.Length),
                        new("L", "10",  "mm", true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // MBend: microstrip bend — W, Angle (deg, CCW from the input arm), Miter
            // (0=None/square corner, 1=Fifty/50% chamfer, 2=Optimal/Douville-James — brief-
            // mtaper-mklopf.md §1A). Default Optimal (owner follow-up, 2026-07-29 — the real
            // Douville-James optimum, not a bare square corner, is the sensible out-of-the-box
            // choice for a freshly-placed bend). Rendered as a None/Fifty/Optimal ComboBox in the
            // Parameter Editor (EnumParamOptions below), not a raw 0/1/2 number box.
            case SymbolKind.MBend:
                return [new("W",     "2.9", "mm",  true, UnitDimension.Length),
                        new("Angle", "90",  "deg", true, UnitDimension.Angle),
                        new("Miter", "2",   "",    true, UnitDimension.None),
                        .. SignalGroundLayerParams];

            // MTee: microstrip T-junction — W1/W2 (through arms, may differ), W3 (branch).
            case SymbolKind.MTee:
                return [new("W1", "2.9", "mm", true, UnitDimension.Length),
                        new("W2", "2.9", "mm", true, UnitDimension.Length),
                        new("W3", "2.9", "mm", true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // MCross: microstrip cross-junction — W1-W4, one per arm.
            case SymbolKind.MCross:
                return [new("W1", "2.9", "mm", true, UnitDimension.Length),
                        new("W2", "2.9", "mm", true, UnitDimension.Length),
                        new("W3", "2.9", "mm", true, UnitDimension.Length),
                        new("W4", "2.9", "mm", true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // MTaper: linear taper — W1 (pin 1 end), W2 (pin 2 end), L (length). N (section count
            // override) is deliberately NOT a default/shown parameter — 0 (auto, brief §1.1's own
            // rule) is the right default for every placement; an advanced user overrides it via the
            // Component Parameters editor's "add parameter" path, not a pre-populated row.
            case SymbolKind.Mtaper:
                return [new("W1", "2.9", "mm", true, UnitDimension.Length),
                        new("W2", "1.0", "mm", true, UnitDimension.Length),
                        new("L",  "10",  "mm", true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // MKlopf: Klopfenstein taper — Z1/Z2 entry route (R-klp-3a's default; W1/W2 is the
            // alternative route, added by the user via "add parameter" when they want geometry-
            // first entry — NOT both shown at once, to avoid an ambiguous default). GammaMax and L
            // are the other headline parameters (F3db is the alternative to L, same "add it if you
            // want it" convention). Offset defaults to 0 (a straight Klopfenstein taper); SmoothSteps
            // defaults to 1 (true) per R-klp-4a. N is an advanced override, not shown by default.
            case SymbolKind.Mklopf:
                return [new("Z1", "50", "Ω", true, UnitDimension.Resistance),
                        new("Z2", "100", "Ω", true, UnitDimension.Resistance),
                        new("GammaMax", "0.05", "", true, UnitDimension.None),
                        new("L", "20", "mm", true, UnitDimension.Length),
                        new("Offset", "0", "mm", true, UnitDimension.Length),
                        new("SmoothSteps", "1", "", true, UnitDimension.None),
                        .. SignalGroundLayerParams];

            // Ground/FetSdd/Generic need no default parameters.
            default: return [];
        }
    }

    /// <summary>
    /// Parses a short type code (case-insensitive) to a SymbolKind and, for variadic-port types,
    /// the parsed port count N.
    ///
    /// Canonical codes: R, L, C, V, VTone, GND, Term/T, FET/SDD/FetSDD, Z{N}P (any N ≥ 1),
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
            case "V":
            case "VDC":    kind = SymbolKind.Vdc;           return true;
            case "VTONE":  kind = SymbolKind.ToneSource;    return true;
            case "GND":    kind = SymbolKind.Ground;        return true;
            case "TERM":
            case "T":      kind = SymbolKind.Term;          return true;
            case "PIN":    kind = SymbolKind.Pin;           return true;
            case "IPROBE":
            case "IP":     kind = SymbolKind.IProbe;        return true;
            case "VAR":    kind = SymbolKind.Var;           return true;
            case "MEAS":   kind = SymbolKind.Meas;          return true;
            case "P1TONE": kind = SymbolKind.P1Tone;        return true;
            case "PNTONE": kind = SymbolKind.PnTone;        return true;
            case "NONLINEARC":
            case "NLC":    kind = SymbolKind.NonlinearC;  return true;
            case "MUTUAL":
            case "MUT":    kind = SymbolKind.Mutual;       return true;
            case "TLIN":
            case "TL":     kind = SymbolKind.Tline;        return true;
            case "TUNER":  kind = SymbolKind.Tuner;         return true;
            case "SOURCETUNER":
            case "SRCTUNER": kind = SymbolKind.SourceTuner; return true;
            case "LOADTUNER":
            case "LDTUNER":  kind = SymbolKind.LoadTuner;    return true;
            case "MLIN":
            case "ML":       kind = SymbolKind.Mlin;         return true;
            case "MBEND":
            case "MB":       kind = SymbolKind.MBend;        return true;
            case "MTEE":
            case "MT":       kind = SymbolKind.MTee;         return true;
            case "MCROSS":
            case "MX":       kind = SymbolKind.MCross;       return true;
            case "MTAPER":
            case "MTP":      kind = SymbolKind.Mtaper;       return true;
            case "MKLOPF":
            case "MKF":      kind = SymbolKind.Mklopf;       return true;
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
                // S{N}P — Touchstone file-backed N-port (e.g. S1P, S2P, S3P).
                if (s.Length >= 3 && s[0] == 'S' && s[^1] == 'P' &&
                    int.TryParse(s[1..^1], out int snp) && snp >= 1)
                {
                    kind      = SymbolKind.Snp;
                    portCount = snp;
                    return true;
                }
                return Enum.TryParse(input, ignoreCase: true, out kind);
        }
    }

    /// <summary>
    /// Returns the indexed-parameter group descriptor for component types whose parameter set
    /// can be extended by the user via the "+" button in the parameter editor.
    /// Returns null for ordinary fixed-parameter types (R, L, C, V, Term, Pin, Ground, …).
    /// </summary>
    public static IndexedParamGroup? UserParamTemplate(SymbolKind kind) => kind switch
    {
        // P1Tone: Z[k] harmonic termination impedances — DC (0), then 2, 3, 4, …  (Z[1] skipped:
        //         the fundamental is already represented by the scalar "Z" parameter).
        SymbolKind.P1Tone => new IndexedParamGroup(
            NameFormats:     ["Z[{0}]"],
            DefaultUnits:    ["Ω"],
            ShowOnSchematic: [true],
            Dimensions:      [UnitDimension.Resistance],
            FirstAddIndex:   0,
            SkipIndices:     [1]),

        // ToneSource: each group = Freq[n]/V[n]/Phase[n].  Tones start at 2 (tone 1 is the base
        //             scalar V/Freq that gets migrated to V[1]/Freq[1] on first add).
        SymbolKind.ToneSource => new IndexedParamGroup(
            NameFormats:     ["Freq[{0}]", "V[{0}]", "Phase[{0}]"],
            DefaultUnits:    ["GHz", "V", "deg"],
            ShowOnSchematic: [true, true, false],
            Dimensions:      [UnitDimension.Frequency, UnitDimension.Voltage, UnitDimension.Angle],
            FirstAddIndex:   2,
            SkipIndices:     null),

        // PnTone: each group = Freq[n]/Pavl[n]/Phase[n] (the power-source analog of ToneSource). The
        //         component is seeded with tones 1 & 2 (DefaultParameters); "+" adds tone 3, 4, …
        //         FirstAddIndex=2 keeps tone 1 from being removed (it's always the first tone).
        SymbolKind.PnTone => new IndexedParamGroup(
            NameFormats:     ["Freq[{0}]", "Pavl[{0}]", "Phase[{0}]"],
            DefaultUnits:    ["GHz", "dBm", "deg"],
            ShowOnSchematic: [true, true, false],
            Dimensions:      [UnitDimension.Frequency, UnitDimension.Power, UnitDimension.Angle],
            FirstAddIndex:   2,
            SkipIndices:     null),

        // ZPort: user adds extra Z[n] scalar params (1D; existing Z[i,j] 2D params are unaffected).
        SymbolKind.ZPort => new IndexedParamGroup(
            NameFormats:     ["Z[{0}]"],
            DefaultUnits:    ["Ω"],
            ShowOnSchematic: [true],
            Dimensions:      [UnitDimension.Resistance],
            FirstAddIndex:   1,
            SkipIndices:     null),

        // SDD: user adds named I[…] equation slots.
        SymbolKind.Sdd => new IndexedParamGroup(
            NameFormats:     ["I[{0}]"],
            DefaultUnits:    [""],
            ShowOnSchematic: [true],
            Dimensions:      [UnitDimension.None],
            FirstAddIndex:   1,
            SkipIndices:     null),

        // VAR: user-authored variable rows — adds Var{n} placeholder names.
        SymbolKind.Var => new IndexedParamGroup(
            NameFormats:     ["Var{0}"],
            DefaultUnits:    [""],
            ShowOnSchematic: [false],
            Dimensions:      [UnitDimension.None],
            FirstAddIndex:   1,
            SkipIndices:     null),

        // MEAS: user-authored measurement equation rows — adds Meas{n} placeholder names.
        SymbolKind.Meas => new IndexedParamGroup(
            NameFormats:     ["Meas{0}"],
            DefaultUnits:    [""],
            ShowOnSchematic: [false],
            Dimensions:      [UnitDimension.None],
            FirstAddIndex:   1,
            SkipIndices:     null),

        // NonlinearC: user adds C1, C2, … higher-order polynomial coefficients (raw SI: F/V, F/V², …).
        SymbolKind.NonlinearC => new IndexedParamGroup(
            NameFormats:     ["C{0}"],
            DefaultUnits:    [""],
            ShowOnSchematic: [false],
            Dimensions:      [UnitDimension.None],
            FirstAddIndex:   1,
            SkipIndices:     null),

        // Tuner family (general / Source / Load): user adds Z[2], Z[3], … per-harmonic termination
        // impedances. The fundamental is the Z[1] row itself (it IS the first index, not a separate
        // scalar), so the first addable index is 2. All three share this template (same engine component).
        SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner => new IndexedParamGroup(
            NameFormats:     ["Z[{0}]"],
            DefaultUnits:    ["Ω"],
            ShowOnSchematic: [true],
            Dimensions:      [UnitDimension.Resistance],
            FirstAddIndex:   2,
            SkipIndices:     null),

        _ => null,
    };

    /// <summary>MBend's own "Miter" mode names — a plain 0/1/2 number box gives no hint that the
    /// value is really a closed set of named modes at all (owner-reported: "no way to change the
    /// miter mode... at least it's not obvious"). Order matches <c>MicrostripBendMiter</c>'s own
    /// enum values exactly (index == numeric value).</summary>
    public static readonly IReadOnlyList<string> MBendMiterOptions = ["None", "Fifty", "Optimal"];

    /// <summary>
    /// Named option labels for a parameter whose numeric value is really a closed set of modes —
    /// the Parameter Editor (<see cref="ParameterRowViewModel"/>) renders a ComboBox of these labels
    /// (committing the selected INDEX as the parameter's expression) instead of a raw number box
    /// when this returns non-null. Null (the default for every other parameter) means "not an enum
    /// parameter — render the ordinary Expression text box."
    /// </summary>
    /// <summary>
    /// Named option labels for a parameter whose numeric value is really a closed set of modes —
    /// the Parameter Editor (<see cref="ParameterRowViewModel"/>) renders a ComboBox of these labels
    /// (committing the selected INDEX as the parameter's expression) instead of a raw number box
    /// when this returns non-null. Null (the default for every other parameter) means "not an enum
    /// parameter — render the ordinary Expression text box."
    ///
    /// <b>Deliberately NOT applied to the on-schematic label</b> (owner's explicit preference,
    /// 2026-07-29): the label's inline text-edit box only ever accepts the raw numeric value (there
    /// is no combo there), so the label keeps showing that same raw value ("Miter = 2") rather than
    /// a translated name it couldn't be typed back in as — the Parameter Editor's own subtle,
    /// read-only index readout next to the combo (<see cref="ParameterRowViewModel.EnumIndexReadout"/>)
    /// is what explains the connection between the two instead.
    /// </summary>
    public static IReadOnlyList<string>? EnumParamOptions(SymbolKind kind, string paramName) => (kind, paramName) switch
    {
        (SymbolKind.MBend, "Miter") => MBendMiterOptions,
        _ => null,
    };

    // ── Signal/Ground layer override parameters (brief-technology-editor-units-and-layers.md R-tec-6) ──

    /// <summary>
    /// R-tec-6/7/8: every microstrip component's per-instance layer-selection override — the PCell
    /// contract's own R11 ("Signal Layer + Ground Reference, defaulting from the stackup and
    /// per-instance overridable") already had its DEFAULT half implemented
    /// (<see cref="CircuitRF.Ui.Layout.PCells.SubstrateResolver"/>) and its OVERRIDE plumbing already
    /// present (<see cref="CircuitRF.Ui.Layout.PCells.PCellLayerSelection"/>,
    /// <see cref="CircuitRF.Ui.Schematic.MicrostripSubstrateInjection.BuildOverrides"/>'s own optional
    /// override parameters) — nothing populated them from a real component parameter until this.
    ///
    /// R-tec-7: stored as the stackup layer's own NAME (a string), matching
    /// <see cref="CircuitRF.Ui.Layout.StackupLayer.SpanFromLayer"/>/<c>SpanToLayer</c>'s own
    /// established convention — names, not indices/keys, survive a technology change meaningfully
    /// (the L1g lesson). R-tec-8: empty means "follow the technology" (the zero-configuration
    /// default this whole substrate design exists for) — never shown on schematic by default
    /// (<c>ShowOnSchematic: false</c>), since annotating every instance with its resolved layer names
    /// would be noise, and on a two-layer board it is the default anyway.
    /// </summary>
    public static readonly IReadOnlyList<DefaultParam> SignalGroundLayerParams =
    [
        new("SignalLayer", "", "", false, UnitDimension.None),
        new("GroundReference", "", "", false, UnitDimension.None),
    ];

    /// <summary>Which of the two layer-choice roles (if any) a given (owner, paramName) pair is —
    /// null for every other parameter. Reused by <see cref="MicrostripSubstrateInjection.IsMicrostripKind"/>'s
    /// own kind set rather than a second, duplicate list.</summary>
    public enum LayerChoiceKind { Signal, Ground }

    public static LayerChoiceKind? LayerChoiceKindFor(SymbolKind kind, string paramName)
    {
        if (!MicrostripSubstrateInjection.IsMicrostripKind(kind)) return null;
        return paramName switch
        {
            "SignalLayer"     => LayerChoiceKind.Signal,
            "GroundReference" => LayerChoiceKind.Ground,
            _ => null,
        };
    }
}
