using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.WBond;

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
    /// <summary>Semiconductor devices — the built-in nonlinear diode and FET family.</summary>
    Devices,
    TransmissionLine,
    Microstrip,
    /// <summary>Impedance-matching networks (owner decision, 2026-08-19 — match.md §8.4). A NEW
    /// category for what is currently one component, deliberately, against the wBond precedent of
    /// not inventing one: "Matching" names a class of things a user goes looking for, not this one
    /// part, and it is where a future matching-network family belongs. <c>Other</c> would hide the
    /// headline component of the release behind the least descriptive label in the picker.</summary>
    Matching,
    Sources,
    DataFiles,
    Terminals,
    Other,
    /// <summary>Nonlinear built-ins — NonlinearC, VerilogA, Diode, the FET family, and every SDD
    /// tile — gathered as one filter regardless of their primary category (owner request,
    /// 2026-08-16). Always an <see cref="ComponentTypeInfo.ExtraCategories"/> membership, never a
    /// primary <see cref="ComponentTypeInfo.Category"/>.</summary>
    Nonlinear,
}

// UnitDimension lives in CircuitRF.Design.Cells — CcellParameter persists it, so the `.ccell` reader
// names it and it had to cross to the non-UI side (brief-cli-em-verb.md R-emcli-3).

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
        // "metre" and not "m" — deliberately, and it is the ONE user-visible consequence of
        // brief-core-length-units §5 q1. The expression engine keeps "m" as the SI prefix MILLI, so
        // offering it here for a LENGTH would hand the user a value a thousand times too small with
        // nothing reporting it. "metre" is the engine's own scale-1 length symbol; the two spellings
        // now agree everywhere rather than meaning different things in two places.
        [UnitDimension.Length]      = ["None", "nm", "µm", "mm", "cm", "metre", "mil"],
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
        // TermG: Term with port 2 permanently grounded — a packaging convenience over the SAME
        // engine component ("Port"), never a parallel model (brief-housekeeping-tearoff-palette-
        // repo.md §4/R-hk-6).
        [SymbolKind.TermG]         = new("TermG", "Term",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["TermG", "Term", "grounded port", "1-port", "port", "sparam", "termination"],
            IsCommon: false),
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
        [SymbolKind.Sdd]           = new("SDD",   "X",
            Category: ComponentCategory.Other,
            SearchTerms: ["SDD", "Sdd", "nonlinear", "behavioral"],
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.ZPort]         = new("Z",     "Z",
            Category: ComponentCategory.Other,
            SearchTerms: ["Z", "ZPort", "impedance", "network"]),
        [SymbolKind.Generic]       = new("X",     "X",
            Category: ComponentCategory.Other,
            SearchTerms: ["X", "Generic", "custom", "subcircuit"]),
        // Unknown is a load-time-only sentinel (R-hk-19a) — never placed by the user, never shown
        // in the palette (LibraryCatalog.AllItems filters it out explicitly). A minimal, well-formed
        // entry still exists here so it participates like any other SymbolKind wherever code assumes
        // every enum value has registry metadata.
        [SymbolKind.Unknown]       = new("Unknown", "X",
            DefaultShowTypeLabel: true, DefaultShowInstanceName: true,
            Category: ComponentCategory.Other,
            SearchTerms: ["Unknown", "unrecognized"],
            IsCommon: false),
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
        // wBond — a wirebond design placed as a component. ONE tile, not one per `.wBond` in the
        // workspace (§5 question 4): a wBond is like SnP — one component type with a file parameter
        // — rather than like a PDK part, where one tile per part is the point. Surfacing a
        // workspace's `.wBond` files as separate palette entries is a bigger idea and deserves its
        // own decision rather than arriving as a side effect of making the component placeable.
        //
        // ComponentCategory.Other, per the brief's own instruction not to invent a category for one
        // component. It is neither a data file (the `.wBond` is a design, not measured data) nor a
        // lumped element, and a "Packaging" category with a single member reads as an oversight.
        [SymbolKind.WBond]         = new("wBond", "W",
            Category: ComponentCategory.Other,
            SearchTerms: ["wBond", "wirebond", "wire bond", "bondwire", "bond wire", "wire",
                          "package", "packaging", "inductance", "mutual", "array"],
            IsCommon: false),
        [SymbolKind.NonlinearC]    = new("NonlinearC",   "C",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["NLC", "NonlinearC", "nonlinear capacitor", "nonlinear", "varactor", "varicap", "CV", "C(V)"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // ── Semiconductor devices ────────────────────────────────────────────
        // A model the USER supplies. Sits with the built-in devices because that is what a user is
        // looking for when they reach for it — a transistor circuitRF does not ship — and it needs
        // no kit, no manifest and nothing installed beyond the compiled model file itself.
        [SymbolKind.VerilogA]      = new("VerilogA", "X",
            Category: ComponentCategory.Devices,
            SearchTerms: ["VerilogA", "Verilog-A", "OSDI", "compact model", "compiled", "custom",
                          "transistor", "device", "nonlinear", "BSIM", "PSP", "HICUM", "external"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.Diode]         = new("Diode", "D",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Diode", "D", "junction", "rectifier", "varactor", "schottky", "pn", "nonlinear"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // Five distinct types, not five settings of one — see SymbolKind for why. Each tile places
        // a different engine component with its own parameter set; "FET" and "MESFET" are search
        // terms on every one of them so a user who does not know which law they want still finds
        // the family.
        [SymbolKind.FetCurtice]    = new("Curtice", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Curtice", "FET", "MESFET", "GaAs", "quadratic", "transistor", "nonlinear", "device"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.FetCurticeCubic] = new("CurticeCubic", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["CurticeCubic", "Curtice", "cubic", "Ettenberg", "FET", "MESFET", "transistor", "nonlinear", "device", "intermod"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.FetStatz]      = new("Statz", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Statz", "FET", "MESFET", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.FetMaterka]    = new("Materka", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Materka", "Kacprzak", "FET", "MESFET", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.FetAngelov]    = new("Angelov", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Angelov", "Chalmers", "HEMT", "FET", "MESFET", "pHEMT", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.Mutual]        = new("M",   "M",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["Mutual", "mutual", "M", "coupling", "inductance", "transformer"],
            IsCommon: false),
        [SymbolKind.Tline]         = new("TLIN", "TL",
            Category: ComponentCategory.TransmissionLine,
            SearchTerms: ["TLIN", "TLine", "transmission line", "tline", "ideal", "lossless", "line"],
            IsCommon: true),
        // Match — a synthesised bandpass matching network (match.md §8.4). Prefix "MN", NOT "M":
        // "M" is Mutual. The search terms deliberately cover what it SOLVES (match, interstage, Cgs,
        // Ropt) as well as how it works (Fano, Norton, Chebyshev), because the people who reach for
        // it think of it both ways.
        [SymbolKind.Match]         = new("Match", "MN",
            Category: ComponentCategory.Matching,
            SearchTerms: ["impedance matching", "filter", "filter design", "transform", "Chebyshev",
                          "Butterworth", "Bessel", "match", "matching", "interstage", "Fano", "Norton",
                          "absorb", "Cgs", "Cds", "Ropt", "bandpass", "lowpass", "highpass"],
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
        // ExtraCategories: [TransmissionLine] on every one (brief-housekeeping-tearoff-palette-repo.md
        // R-hk-5) — a microstrip line IS a transmission line; the enum-typed ExtraCategories set
        // membership (matched against ComponentCategory.TransmissionLine directly) is what TLIN itself
        // already carries as its primary Category, so there is no hand-typed keyword string to drift
        // from TLIN's own spelling.
        [SymbolKind.Mlin]          = new("MLIN",  "ML",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MLIN", "microstrip", "microstrip line", "line", "hammerstad"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.MBend]         = new("MBEND", "MB",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MBEND", "microstrip bend", "bend", "corner", "miter"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.MTee]         = new("MTEE",  "MT",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MTEE", "microstrip tee", "T-junction", "tee", "stub", "power divider"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.MCross]       = new("MCROSS", "MX",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MCROSS", "microstrip cross", "cross junction", "cross"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        // brief-mtaper-mklopf.md
        [SymbolKind.Mtaper]       = new("MTAPER", "MTP",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MTAPER", "microstrip taper", "taper", "linear taper", "width step"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.Mklopf]       = new("MKLOPF", "MKF",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MKLOPF", "klopfenstein", "klopfenstein taper", "taper", "impedance transformer"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
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
    /// True when this component type owns a "Num" parameter that must be unique across the WHOLE
    /// design — the s-parameter port-numbering pool (Term, TermG, P1Tone all share ONE numbering
    /// space today; Pin has its own separate pool, <c>NextFreePinNum</c> in
    /// <c>SchematicViewModel.cs</c>, and is deliberately not part of this set).
    ///
    /// This is the SINGLE place that answers "does a newly-introduced component of this kind need a
    /// fresh Num." Every entry point that can introduce a component into a design (placement,
    /// inline type-change, clipboard paste) must check it — docs/sonnet-briefs/
    /// brief-misc-termg-units-technologies.md §1 found three independently hand-maintained
    /// <c>SymbolKind</c> lists doing this same check, already diverged from each other (TermG was
    /// added to two of the three and missed from the third within the same week). Extend THIS set
    /// when a future port-bearing component needs the same behaviour — never reintroduce a
    /// per-call-site list.
    /// </summary>
    public static bool OwnsUniquePortNum(SymbolKind kind) =>
        kind is SymbolKind.Term or SymbolKind.TermG or SymbolKind.P1Tone;

    /// <summary>
    /// Engine type-reference string for a given SymbolKind — what goes in the .cnl Reference field
    /// and into <see cref="Instance.Reference"/>. Differs from <see cref="DisplayName(SymbolKind)"/>
    /// for ZPort ("Z" vs "Z_Port"), ToneSource ("VTone" vs "V_1Tone").
    /// </summary>
    public static string EngineReference(SymbolKind kind, int portCount = 0) => kind switch
    {
        SymbolKind.Resistor      => "R",
        SymbolKind.Inductor      => "L",
        SymbolKind.Capacitor     => "C",
        SymbolKind.Vdc           => "Vdc",
        SymbolKind.ToneSource    => "V_1Tone",
        SymbolKind.Term          => "Port",  // engine Reference stays "Port" for .cnl compat
        SymbolKind.TermG         => "Port",  // SAME engine component as Term — R-hk-6, no parallel model
        SymbolKind.Pin           => "Pin",   // sentinel — IsPrimitive("Pin")==false; elaborator skips it
        SymbolKind.IProbe        => "IProbe",
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
        SymbolKind.Match         => "Match",
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
        SymbolKind.Diode         => "Diode",
        SymbolKind.VerilogA      => "VerilogA",
        // Lower-case 'w' on purpose: ComponentModelFactory registers the type as "wBond" and its
        // own lookup is case-insensitive, but the .cnl a user reads should spell it the way the
        // document format and the editor both do.
        SymbolKind.WBond         => "wBond",
        // One engine component per law — deliberately NOT one "FET" with a mode parameter.
        SymbolKind.FetCurtice      => "FET_Curtice",
        SymbolKind.FetCurticeCubic => "FET_CurticeCubic",
        SymbolKind.FetStatz        => "FET_Statz",
        SymbolKind.FetMaterka      => "FET_Materka",
        SymbolKind.FetAngelov      => "FET_Angelov",
        _                        => Get(kind).DisplayName,
    };

    /// <summary>
    /// True when a BUILT-IN primitive's parameter names a file, so the editor offers a Browse…
    /// picker beside the text box.
    ///
    /// <para>Kit parts declare this on the cell itself; a built-in has no cell to declare it on, so
    /// it is stated here. A path is exactly the kind of value nobody should be asked to type, and a
    /// mistyped one fails much later with a worse message.</para>
    /// </summary>
    /// <para>wBond's `File` is back, and only for the LINKED half of WB45 (wbond.md §9.7). A carried
    /// wBond still names no file and still has nothing to browse for — bringing wires in is
    /// File ▸ Import ▸ Wirebond Wires…, which has its own picker. A linked one genuinely points at a
    /// path, and a path is exactly the kind of value nobody should be asked to type.</para>
    public static bool IsFilePathParameter(SymbolKind kind, string parameterName)
        => kind is SymbolKind.VerilogA or SymbolKind.WBond
        && parameterName.Equals("File", StringComparison.Ordinal);

    /// <summary>
    /// A one-line explanation of a BUILT-IN primitive's parameter, shown as the row's tooltip, or ""
    /// when there is nothing worth saying.
    ///
    /// <para>A kit part carries its own descriptions on the cell (<c>CcellParameter.Description</c>);
    /// a built-in has no cell to carry one, so the few that genuinely need explaining are stated
    /// here. Deliberately not a description for every parameter of every primitive — "R: the
    /// resistance" is noise. These three are the ones a user cannot answer by reading the name,
    /// because the answer is inside a file they just chose.</para>
    /// </summary>
    /// <summary>
    /// True when this one parameter can be removed from a placed component on its own — the row
    /// carries its own "×".
    ///
    /// <para><b>Why this is not the "−" button.</b> That button removes the LAST indexed GROUP
    /// (P1Tone's <c>Z[k]</c>, ToneSource's <c>Freq[n]</c>/<c>V[n]</c>/<c>Phase[n]</c>), and
    /// last-only is correct there for two reasons: the indices are a sequence, so removing from the
    /// middle would leave a hole, and a group's members must go together or the tone is half
    /// deleted. Neither applies to a compiled model's parameters — they are independent, unordered
    /// names, and with hundreds available "remove the last one you happened to add" is not a way to
    /// remove the first one. So they get per-row removal instead, and the two mechanisms stay
    /// separate rather than one being bent to cover both.</para>
    ///
    /// <para>Scoped to VerilogA today, and to parameters the model itself declares: <c>File</c>,
    /// <c>Model</c> and <c>Pins</c> are circuitRF's own and structural — the symbol cannot draw
    /// without <c>Pins</c> — so they are not removable. Widening this to another component type is
    /// a matter of adding it here, but only where a parameter is genuinely independent of its
    /// neighbours.</para>
    /// </summary>
    public static bool IsRemovableParameter(SymbolKind kind, string parameterName)
        => kind == SymbolKind.VerilogA
        && parameterName is not ("File" or "Model" or "Pins")
        && !string.IsNullOrWhiteSpace(parameterName);

    public static string ParameterDescription(SymbolKind kind, string parameterName)
        => kind is not SymbolKind.VerilogA ? "" : parameterName switch
        {
            "File"  => "The compiled model (.osdi) to load. circuitRF runs a model you built — it does "
                     + "not compile Verilog-A itself. Choosing one fills in Model and Pins below.",
            "Model" => "Which device type inside that file to place. A file usually declares one, and "
                     + "then this can be left blank; when it declares several, pick the one you want.",
            "Pins"  => "How many terminals the symbol draws. It is the model's own terminal count, "
                     + "filled in from the file — change it only if you are drawing before choosing one.",
            _       => "",
        };

    /// <summary>
    /// The gate-charge, gate-conduction and temperature parameters every built-in FET law shares.
    /// Factored out because they are genuinely the same parameters read by the same base class —
    /// unlike the drain-current parameters, which are per-law and must NOT be shared.
    ///
    /// All hidden by default: they are secondary to the law's own parameters, and a FET showing
    /// eleven labels is unreadable.
    /// </summary>
    private static DefaultParam[] FetSharedDefaults() =>
    [
        new("Cgs",      "0",     "pF", false, UnitDimension.Capacitance),
        new("Cgd",      "0",     "pF", false, UnitDimension.Capacitance),
        // 0 = no gate charge, 1 = constant Cgs/Cgd, 2 = bias-dependent junction charge.
        new("CapModel", "1",     "",   false, UnitDimension.None),
        new("Vbi",      "1",     "V",  false, UnitDimension.Voltage),
        new("Mj",       "0.5",   "",   false, UnitDimension.None),
        new("Fc",       "0.5",   "",   false, UnitDimension.None),
        // Gate conduction is OFF at Is = 0 — a forward-conducting gate is opt-in.
        new("Is",       "0",     "A",  false, UnitDimension.Current),
        new("N",        "1",     "",   false, UnitDimension.None),
        new("Xti",      "0",     "",   false, UnitDimension.None),
        new("Eg",       "1.16",  "V",  false, UnitDimension.Voltage),
        new("Temp",     "26.85", "",   false, UnitDimension.None),
        new("Tnom",     "26.85", "",   false, UnitDimension.None),
    ];

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
            case SymbolKind.TermG:
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
            // InterpMode, InterpDomain, ExtrapMode are the remaining 7 fixed params.
            // InterpMode default is cubic spline (per docs/design; the smoothest choice for a
            // typically-sparse measured/simulated sweep); InterpDomain default is MA (magnitude/angle
            // — owner's explicit preference, 2026-08-13, overriding the earlier RI-default choice).
            case SymbolKind.Snp:
            {
                int n = portCount >= 1 ? portCount : 2;
                return [
                    new("NumPorts",    $"{n}",         "",  false, UnitDimension.None),
                    new("File",        "",              "",  true,  UnitDimension.None),
                    new("RefNode",     "false",         "",  false, UnitDimension.None),
                    new("PinConfig",   "Standard",      "",  false, UnitDimension.None),
                    new("Pitch",       "Loose",         "",  false, UnitDimension.None),
                    new("InterpMode",  "CubicSpline",   "",  false, UnitDimension.None),
                    new("InterpDomain","MA",            "",  false, UnitDimension.None),
                    new("ExtrapMode",  "NearestEdge",   "",  false, UnitDimension.None),
                ];
            }

            // VerilogA: a compiled model the user points at. Only `File` is required — everything
            // else the model needs is one of ITS parameters, added by the user in the dialog and
            // forwarded verbatim, because a compact model has hundreds and they belong to its author.
            //
            // `Pins` is circuitRF's, not the model's: the symbol has to know how many terminals to
            // draw before anything has read the file. Set it to what the model declares.
            case SymbolKind.VerilogA:
                return [
                    new("File",  "",  "", true,  UnitDimension.None),
                    new("Model", "",  "", false, UnitDimension.None),
                    new("Pins",  "2", "", false, UnitDimension.None),
                ];

            // wBond: `Design` CARRIES the wires (WBondEmbedding) — it does not name a file. A .wBond
            // may also hold layout artwork, which a schematic component has nowhere to put, so a
            // component that referenced one would be pointing at something most of which it cannot
            // express; a blank reference is what used to render the "Not Found" placeholder on every
            // freshly-dropped wBond. Seeded with the ONE-ARRAY, ONE-WIRE default, so a dropped wBond
            // renders, wires up and simulates with nothing to configure. It is hidden: the payload is
            // machine-written, and File ▸ Import ▸ Wirebond Wires… is how it is replaced.
            //
            // `Arrays` is circuitRF's own bookkeeping, not an engine parameter: it records the array
            // list this instance's wiring was drawn against, so an IMPORT that reorders the arrays can
            // be reported instead of silently re-pointing every wire (§5 question 3 / M2). Hidden by
            // default — it is a record, not a value to read on a page — and editable in the parameter
            // dialog, which is how a user acknowledges a change after re-checking the wiring. It is
            // filtered out of the extracted netlist by NetExtractor.
            //
            // `Temp` and `GroundPlane` are real engine overrides (ComponentModelFactory reads both)
            // and are deliberately left BLANK: blank means "use the design's own value", and a
            // seeded default here would silently override what the payload itself states.
            //
            // `SymbolPitch` is artwork only — Tight or Loose, exactly as SnP's `Pitch` means them
            // (owner, 2026-08-16). It changes how far apart the port ROWS sit on the symbol and
            // nothing else; the pin ORDER, and therefore the wiring, is the array order either way.
            // Like `Arrays` it is filtered out of the extracted netlist.
            //
            // **Named `SymbolPitch`, not `Pitch`** (owner): on a wirebond component "pitch" is what a
            // reader will take to mean the WIRE pitch — the centre-to-centre spacing of the bonds
            // themselves, a physical quantity this parameter has nothing to do with. SnP has no such
            // collision, which is why it can keep the short name.
            //
            // `RefPin` exposes the floating REF terminal, and is OFF by default — matching SnP's own
            // `RefNode`. Unlike the two above it IS forwarded to the engine, because it changes the
            // terminal count (2M vs 2M+1); see WBondModel.
            //
            // `LoopHeight`, `Diameter` and `Material` are the CONTROLLING parameters of wbond.md
            // §5.5.1/WB44 — the handles a VAR, a parametric sweep or an optimiser can turn. Every one
            // of them is declared BLANK, and that is the part that would silently break every existing
            // design if it were got wrong: a wBond shipping `LoopHeight = 20 mil` among its defaults
            // regenerates every placed instance's wires to 20 mil on its next run. Blank means "as
            // drawn"; NetExtractor drops a blank rather than emitting it, so an unset parameter never
            // reaches the engine at all (WB44 property 2).
            //
            // Array-scoped spellings — `LoopHeight_G1`, `Diameter_G1`, `Material_G2` (O-10: array names
            // ARE the pin names, and the array is the only scope there is) — are not here because the
            // array names are not knowable until the design
            // is decoded. The wBond parameter panel generates them from the instance's own array list.
            //
            // `Source` and `File` are WB45's carried-or-linked axis. `Carried` by construction: a
            // freshly placed wBond has no cell and no file to link to. Only Update Layout from
            // Schematic flips it, and says so (WB45a) — never a later scan noticing a file exists,
            // which would change which wires simulate with nothing on screen.
            case SymbolKind.WBond:
                return [
                    new("Design",      WBondEmbedding.DefaultPayload, "", false, UnitDimension.None),
                    new("Arrays",      WBondSymbolProvider.DefaultArraysKey, "", false, UnitDimension.None),
                    new("Source",      nameof(WBondPlacement.WireSource.Carried), "", false, UnitDimension.None),
                    new("File",        "", "", false, UnitDimension.None),
                    new("SymbolPitch", nameof(WBondSymbolPitch.Loose), "", false, UnitDimension.None),
                    new("RefPin",      "false", "", false, UnitDimension.None),
                    // `IncludeCapacitance` is the ONE wBond parameter whose default changes the
                    // answer for designs that already exist (wbond.md §5.5): every other default
                    // reproduces prior behaviour, and this one turns capacitance ON. Declared "true"
                    // rather than blank so the parameter panel's checkbox has a definite state to
                    // show; WBondPlacement.ApplyDesign overwrites it from an imported design's own
                    // flag, so the wBond editor's toggle is still what a placed component inherits.
                    new("IncludeCapacitance", "true", "", false, UnitDimension.None),
                    // `er` — the plastic overmold's relative permittivity (wbond.md §3.7). Declared
                    // "1" (air) rather than blank for the same reason IncludeCapacitance is declared
                    // "true": the parameter panel puts it beside that checkbox and a box showing
                    // nothing would not say what medium the capacitance was computed in.
                    //
                    // "1" is also the value every design had before this existed, so declaring it
                    // changes no existing answer. WBondPlacement.ApplyDesign overwrites it from an
                    // imported design's own OvermoldEr, exactly as it does IncludeCapacitance, so the
                    // wBond editor's setting is what a placed component inherits.
                    //
                    // NOT in the name-valued list of Elaborator.ResolveWBondParameters: it is an
                    // ordinary real expression, which is what makes `er` sweepable and optimisable.
                    new("er",          "1", "", false, UnitDimension.None),
                    new("Temp",        "", "", false, UnitDimension.None),
                    new("GroundPlane", "", "", false, UnitDimension.None),
                    new("LoopHeight",  "", "mil", false, UnitDimension.Length),
                    new("Diameter",    "", "mil", false, UnitDimension.Length),
                    new("Material",    "", "",    false, UnitDimension.None),
                ];

            // ── Semiconductor devices ────────────────────────────────────────
            // Every name below is a factory key — ComponentModelFactory.CreateDiodeModel /
            // CreateFetModel read exactly these strings. A typo here is a parameter that silently
            // takes its default instead of the user's value, so the two lists are checked against
            // each other by test, not by eye.
            //
            // ShowOnSchematic is reserved for the handful of parameters that DEFINE the device.
            // The rest are real and editable in the parameter dialog; showing all fifteen would
            // bury the schematic under label text.
            //
            // Temp and Tnom are in DEGREES CELSIUS and unitless here — there is no temperature
            // UnitDimension, and adding a "C" unit token would collide with capacitance in the
            // .cnl parameter tokenizer. Both default to the same value, so a device the user never
            // sets a temperature on is evaluated exactly at its extraction point.

            case SymbolKind.Diode:
                return [
                    new("Is",   "1e-14", "A",  true,  UnitDimension.Current),
                    new("N",    "1",     "",   true,  UnitDimension.None),
                    // Rs is a MODEL parameter, not a separate placed resistor. Non-zero moves the
                    // junction onto an internal node the elaborator mints (DiodeModel §Rs).
                    new("Rs",   "0",     "Ω",  true,  UnitDimension.Resistance),
                    new("Cj0",  "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Vj",   "1",     "V",  false, UnitDimension.Voltage),
                    new("M",    "0.5",   "",   false, UnitDimension.None),
                    new("Fc",   "0.5",   "",   false, UnitDimension.None),
                    // Bv = 0 means breakdown is NOT MODELLED — never "breaks down at 0 V".
                    new("Bv",   "0",     "V",  false, UnitDimension.Voltage),
                    new("Ibv",  "1e-3",  "A",  false, UnitDimension.Current),
                    new("Tt",   "0",     "",   false, UnitDimension.None),
                    new("Temp", "26.85", "",   false, UnitDimension.None),
                ];

            case SymbolKind.FetCurtice:
                return [
                    new("Vto",    "-2",   "V", true,  UnitDimension.Voltage),
                    new("Beta",   "0.02", "",  true,  UnitDimension.None),
                    new("Alpha",  "2",    "",  true,  UnitDimension.None),
                    new("Lambda", "0",    "",  true,  UnitDimension.None),
                    .. FetSharedDefaults(),
                    new("Betatc",  "0", "", false, UnitDimension.None),
                    new("Alphatc", "0", "", false, UnitDimension.None),
                    new("Vtotc",   "0", "", false, UnitDimension.None),
                ];

            case SymbolKind.FetCurticeCubic:
                return [
                    new("A0",    "0.1",  "", true,  UnitDimension.None),
                    new("A1",    "0.05", "", true,  UnitDimension.None),
                    new("A2",    "0",    "", true,  UnitDimension.None),
                    new("A3",    "0",    "", true,  UnitDimension.None),
                    new("Gamma", "2",    "", true,  UnitDimension.None),
                    // NOT the quadratic law's Beta: here it is the gate-voltage shift with drain
                    // bias, in 1/V, which is why this is a separate component type.
                    new("Beta",  "0",    "",  false, UnitDimension.None),
                    new("Vds0",  "5",    "V", false, UnitDimension.Voltage),
                    .. FetSharedDefaults(),
                    new("Gammatc", "0", "", false, UnitDimension.None),
                ];

            case SymbolKind.FetStatz:
                return [
                    new("Vto",    "-2",   "V", true,  UnitDimension.Voltage),
                    new("Beta",   "0.02", "",  true,  UnitDimension.None),
                    new("B",      "0.3",  "",  true,  UnitDimension.None),
                    new("Alpha",  "2",    "",  true,  UnitDimension.None),
                    new("Lambda", "0",    "",  false, UnitDimension.None),
                    .. FetSharedDefaults(),
                    new("Betatc",  "0", "", false, UnitDimension.None),
                    new("Alphatc", "0", "", false, UnitDimension.None),
                    new("Vtotc",   "0", "", false, UnitDimension.None),
                ];

            case SymbolKind.FetMaterka:
                return [
                    new("Idss",  "0.1", "A", true,  UnitDimension.Current),
                    new("Vp0",   "-2",  "V", true,  UnitDimension.Voltage),
                    new("Gamma", "0",   "",  true,  UnitDimension.None),
                    new("Alpha", "2",   "",  true,  UnitDimension.None),
                    .. FetSharedDefaults(),
                    new("Alphatc", "0", "", false, UnitDimension.None),
                    new("Gammatc", "0", "", false, UnitDimension.None),
                    new("Vtotc",   "0", "", false, UnitDimension.None),
                ];

            case SymbolKind.FetAngelov:
                return [
                    new("Ipk",    "0.1", "A", true,  UnitDimension.Current),
                    new("Vpk",    "-1",  "V", true,  UnitDimension.Voltage),
                    new("P1",     "1",   "",  true,  UnitDimension.None),
                    new("P2",     "0",   "",  false, UnitDimension.None),
                    new("P3",     "0",   "",  false, UnitDimension.None),
                    new("Alpha",  "2",   "",  true,  UnitDimension.None),
                    new("Lambda", "0",   "",  false, UnitDimension.None),
                    .. FetSharedDefaults(),
                    new("Alphatc", "0", "", false, UnitDimension.None),
                    new("Vtotc",   "0", "", false, UnitDimension.None),
                ];

            case SymbolKind.NonlinearC:
                return [new("C0", "1", "pF", true, UnitDimension.Capacitance)];

            // TLIN: ideal lossless transmission line. Z = characteristic impedance (real, lossless),
            // E = electrical length in degrees at reference frequency F. All three show on the schematic.
            case SymbolKind.Tline:
                return [new("Z", "50", "Ω",   true, UnitDimension.Resistance),
                        new("E", "90", "deg", true, UnitDimension.Angle),
                        new("F", "1",  "GHz", true, UnitDimension.Frequency)];

            // Match: `Design` is the WHOLE design, base64 of its JSON (match.md §7.2) — hidden, never
            // a text row (ParameterEditorViewModel.IsMatchPanelParameter), and the only input the
            // model reads. The rest are ECHO parameters: the Designer writes them so the user can
            // display the design on the schematic, and NOTHING reads them back. That is why they are
            // duplicated here at all rather than derived at render time — an instance has to carry
            // them to be able to show them, and match.md §7.2 makes the design authoritative so the
            // echo cannot become a second input.
            //
            // The default payload is a REAL design (1.8–2.2 GHz, order 4, 50 Ω to 10 Ω,
            // Chebyshev-Fano) and not a blank: a freshly dropped Match must simulate immediately. The
            // seven ECHO parameters below MUST agree with MatchEmbedding.DefaultDesign — they are what
            // a reader sees on the page before the Designer has ever rewritten them, and a set that
            // disagrees with the payload describes a component that does not exist. Same rule wBond
            // follows in shipping a default wire rather than an empty array.
            case SymbolKind.Match:
                return [new("Design",   MatchEmbedding.DefaultPayload, "", false, UnitDimension.None),
                        new("F1",       "1.8",           "GHz", true,  UnitDimension.Frequency),
                        new("F2",       "2.2",           "GHz", true,  UnitDimension.Frequency),
                        new("Order",    "4",             "",    true,  UnitDimension.None),
                        new("Response", "ChebyshevFano", "",    false, UnitDimension.None),
                        new("Form",     "Bandpass",      "",    false, UnitDimension.None),
                        new("R1",       "50",            "Ω",   false, UnitDimension.Resistance),
                        new("R2",       "10",            "Ω",   false, UnitDimension.Resistance)];

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

            // MTee: microstrip T-junction — W1/W2 (through arms, may differ), W3 (branch), and each
            // arm's own drawn length L1/L2/L3. The lengths are ARTWORK only (MicrostripTeeModel has
            // no length term — the junction's reference planes are at the arm edges), but they must
            // be declared rather than derived: deriving arm length from arm width, as this cell used
            // to, makes a width gripper move the junction and the other two pins along the
            // perpendicular axis.
            //
            // Each L defaults to its OWN W (owner's call, 2026-08-12) — a square stub per arm, which
            // is the smallest thing that still reads as a junction with pins at its own edge, and
            // leaves the user to add real line length as an MLIN rather than inheriting a stub they
            // did not ask for. Note this is a SHORTER default than the pre-2026-08-12 derived stub
            // (2.5× the width); that factor survives only as the fallback for a cell authored before
            // these parameters existed, so nothing already drawn moves.
            // MicrostripSubstrateInjection.ApplyTechnologyDefaults keeps L == W against the
            // technology's own synthesised 50 Ω width at placement.
            case SymbolKind.MTee:
                return [new("W1", "2.9", "mm", true, UnitDimension.Length),
                        new("W2", "2.9", "mm", true, UnitDimension.Length),
                        new("W3", "2.9", "mm", true, UnitDimension.Length),
                        new("L1", "2.9", "mm", true, UnitDimension.Length),
                        new("L2", "2.9", "mm", true, UnitDimension.Length),
                        new("L3", "2.9", "mm", true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // MCross: microstrip cross-junction — W1-W4 and L1-L4, one pair per arm. Same reasoning
            // as MTee above, including L defaulting to its own W.
            case SymbolKind.MCross:
                return [new("W1", "2.9", "mm", true, UnitDimension.Length),
                        new("W2", "2.9", "mm", true, UnitDimension.Length),
                        new("W3", "2.9", "mm", true, UnitDimension.Length),
                        new("W4", "2.9", "mm", true, UnitDimension.Length),
                        new("L1", "2.9", "mm", true, UnitDimension.Length),
                        new("L2", "2.9", "mm", true, UnitDimension.Length),
                        new("L3", "2.9", "mm", true, UnitDimension.Length),
                        new("L4", "2.9", "mm", true, UnitDimension.Length),
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

            // Ground/Generic need no default parameters.
            default: return [];
        }
    }

    /// <summary>
    /// Parses a short type code (case-insensitive) to a SymbolKind and, for variadic-port types,
    /// the parsed port count N.
    ///
    /// Canonical codes: R, L, C, V, VTone, GND, Term/T, TermG/TG, SDD, Z{N}P (any N ≥ 1),
    /// SDD{N} (any N ≥ 1), X.
    ///
    /// <list type="bullet">
    ///   <item>Z{N}P (e.g. Z2P, Z5P) → (ZPort, portCount=N)</item>
    ///   <item>SDD{N} (e.g. SDD2, SDD3) → (Sdd, portCount=N)</item>
    ///   <item>SDD (no number) → (Sdd, portCount=0) — 2-port default</item>
    ///   <item>"FET"/"FETSDD" deliberately do NOT parse (R-hk-19: the library FET was hard-removed
    ///   with no compatibility alias, brief-housekeeping-tearoff-palette-repo.md §7A)</item>
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
            case "TERMG":
            case "TG":     kind = SymbolKind.TermG;         return true;
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
            case "MATCH":
            case "MN":     kind = SymbolKind.Match;        return true;
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
            // "FET"/"FETSDD" are deliberately NOT mapped here (R-hk-19: the library FET was hard-
            // removed with no compatibility alias) — they fall through to Enum.TryParse below,
            // which fails since "FetSdd" no longer exists, so a typed "FET" correctly does not parse.
            case "SDD":    kind = SymbolKind.Sdd;            return true;  // bare SDD → portCount=0 (2-port default)
            case "WBOND":
            case "WB":     kind = SymbolKind.WBond;         return true;
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
    /// <see cref="CircuitRF.Design.Layout.StackupLayer.SpanFromLayer"/>/<c>SpanToLayer</c>'s own
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
