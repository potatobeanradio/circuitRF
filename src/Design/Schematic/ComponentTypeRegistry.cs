using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.WBond;

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.PCells;
namespace CircuitRF.Design.Schematic;

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
    /// <summary>The MMIC passives (brief-agent-authoring-overview.md AA-1): MIM capacitor, spiral
    /// inductor, thin-film resistor and air bridge. A category on <see cref="Matching"/>'s precedent —
    /// it names a class of parts a user goes looking for, the ones a die is drawn from, and none of
    /// them is a microstrip line or a lumped element in the sense those two categories mean.</summary>
    Mmic,
    /// <summary>System-level blocks (brief-sys-series.md, owner request 2026-08-31): the balun,
    /// circulator, switches, ideal amplifier, directional coupler, 90° hybrid, ideal filter,
    /// attenuator and duplexer — plus the two mixer tiles, which keep <see cref="Devices"/> as their
    /// primary. Follows <see cref="Matching"/>'s precedent: it names a CLASS of parts a user goes
    /// looking for — the boxes a system block diagram is drawn out of — not one part. Its members
    /// are what you reach for when the thing being designed is a signal CHAIN rather than a
    /// circuit.</summary>
    System,
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
    int[]? SkipIndices = null,
    /// <summary>
    /// Default EXPRESSION for each format entry (parallel; blank when absent or shorter).
    ///
    /// <para><b>Why a blank default is not neutral</b> (owner report, 2026-08-29). A parameter with
    /// an empty expression renders NO schematic label — <c>EditableSchematic.BuildRenderModel</c>
    /// skips it — regardless of its ShowOnSchematic flag, which is right for a label but makes the
    /// checkbox look broken at exactly the moment a user ticks it on a freshly added group. A group
    /// whose members are meant to show on the schematic therefore states real defaults here, so the
    /// row that appears is a complete one. It also stops <c>NumFreqs</c> counting a tone whose
    /// frequency and amplitude are both silently zero.</para>
    ///
    /// <para>Left null where blank genuinely IS the right start: an SDD equation slot and a VAR row
    /// have no defensible default value, and inventing one would be a guess the user must then
    /// notice and undo.</para>
    /// </summary>
    string[]? DefaultExpressions = null)
{
    /// <summary>True if the given index is in the skip set.</summary>
    public bool IsSkipped(int index) => SkipIndices is not null && SkipIndices.Contains(index);

    /// <summary>The default expression for format entry <paramref name="i"/>, or "" when none.</summary>
    public string DefaultExpression(int i)
        => DefaultExpressions is not null && i < DefaultExpressions.Length ? DefaultExpressions[i] : "";
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
        [UnitDimension.Conductance] = ["None", "nS", "µS", "mS", "S", "kS"],
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
        // SRLC / PRLC: two tiles, two engine components, one purpose — an R, an L and a C in one
        // part, so a real component (a ceramic capacitor with its vendor ESR and ESL, a tank) is one
        // symbol instead of three wired together. Their pins sit exactly where R/L/C's do, so
        // swapping one in costs no rewiring; the search terms say so, because "ESR"/"ESL" is what a
        // user reading a datasheet actually types.
        [SymbolKind.Srlc]          = new("SRLC",  "SRLC",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["SRLC", "Series RLC", "RLC", "series", "ESR", "ESL", "ceramic", "capacitor"],
            IsCommon: true),
        [SymbolKind.Prlc]          = new("PRLC",  "PRLC",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["PRLC", "Parallel RLC", "RLC", "parallel", "tank", "resonator"],
            IsCommon: true),
        // The six two-element members (owner, 2026-09-20). Each carries only the values it has, so
        // the reason to place one instead of an SRLC is exactly that there is no third number to
        // read and dismiss. NOT IsCommon: the palette's curated head is already 28 rows and these
        // belong beside SRLC/PRLC in the Lumped tail, which is where a reader who wants one looks.
        // Their search terms carry what a user actually types — "lossy inductor", "trap", "tank".
        [SymbolKind.Srl]           = new("SRL",   "SRL",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["SRL", "Series RL", "RL", "series", "lossy inductor", "ESR", "coil", "inductor"]),
        [SymbolKind.Src]           = new("SRC",   "SRC",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["SRC", "Series RC", "RC", "series", "lossy capacitor", "ESR", "damper", "snubber", "capacitor"]),
        [SymbolKind.Slc]           = new("SLC",   "SLC",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["SLC", "Series LC", "LC", "series", "trap", "notch", "resonator"]),
        [SymbolKind.Prl]           = new("PRL",   "PRL",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["PRL", "Parallel RL", "RL", "parallel", "lossy inductor", "choke", "damped"]),
        [SymbolKind.Prc]           = new("PRC",   "PRC",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["PRC", "Parallel RC", "RC", "parallel", "leaky capacitor", "shunt", "damped"]),
        [SymbolKind.Plc]           = new("PLC",   "PLC",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["PLC", "Parallel LC", "LC", "parallel", "tank", "resonator"]),
        [SymbolKind.Vdc]           = new("Vdc",   "V",
            Category: ComponentCategory.Sources,
            SearchTerms: ["Vdc", "DC", "bias", "supply", "voltage", "V"],
            IsCommon: true),
        // NOTE: abbreviation; revisit when palette has richer type
        [SymbolKind.ToneSource]    = new("VTone", "V",
            Category: ComponentCategory.Sources,
            SearchTerms: ["VTone", "ToneSource", "tone", "RF", "signal"],
            IsCommon: true),
        // ITone: the current-source dual of VTone, same engine shape, same parameter story with
        // I/Idc where VTone has V/Vdc. Common for the same reason VTone is — it is the other half of
        // "drive this node", and a user looking for one expects to find the other beside it.
        [SymbolKind.CurrentToneSource] = new("ITone", "I",
            Category: ComponentCategory.Sources,
            SearchTerms: ["ITone", "CurrentToneSource", "current source", "tone", "RF", "signal", "I"],
            IsCommon: true),
        // VCCS: "G" is its instance prefix by long convention (the SPICE G element), even though
        // circuitRF's own current DIRECTION is the opposite of that element's — see VccsModel.
        [SymbolKind.Vccs]          = new("VCCS", "G",
            Category: ComponentCategory.Sources,
            SearchTerms: ["VCCS", "G", "transconductance", "controlled", "dependent", "gm", "vccs"],
            IsCommon: false),
        // VCVS: "E" is its instance prefix by the same long convention (the SPICE E element), and
        // unlike the VCCS the direction needs no apology — a voltage gain has no arrow to get wrong.
        [SymbolKind.Vcvs]          = new("VCVS", "E",
            Category: ComponentCategory.Sources,
            SearchTerms: ["VCVS", "E", "voltage gain", "controlled", "dependent", "amplifier", "vcvs"],
            IsCommon: false),
        // Op-amp / OpAmp5: ONE engine component ("OpAmp"), two tiles differing only in whether the
        // supply pins are drawn — the Mixer/MixerD arrangement. "U" is the prefix every drawing of
        // one uses; a schematic that swaps one tile for the other should not renumber.
        [SymbolKind.OpAmp]         = new("OpAmp", "U",
            Category: ComponentCategory.Devices,
            SearchTerms: ["OpAmp", "op-amp", "op amp", "operational amplifier", "opamp", "ideal", "U",
                          "inverting", "non-inverting", "feedback", "amplifier"],
            IsCommon: false),
        [SymbolKind.OpAmp5]        = new("OpAmp5", "U",
            Category: ComponentCategory.Devices,
            SearchTerms: ["OpAmp5", "op-amp", "op amp", "operational amplifier", "opamp", "supply",
                          "V+", "V-", "U"],
            IsCommon: false),
        // The ideal transformer. "T" is the prefix drawings use; TLIN's is "TL", so the two never
        // number into each other.
        [SymbolKind.Transformer]   = new("Transformer", "T",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["Transformer", "ideal transformer", "turns ratio", "N:1", "balun", "coupled",
                          "winding", "xfmr", "T"],
            IsCommon: false),
        // Mixer / MixerD: ONE engine component ("Mixer"), two tiles — the TermG pattern. Primary
        // category is Devices rather than Sources because a mixer is a thing you put IN the signal
        // path, not a thing that drives it; the Nonlinear filter carries it too, since it is one.
        // "MIX" is the instance prefix for both: a schematic that swaps one tile for the other
        // should not renumber.
        [SymbolKind.Mixer]         = new("Mixer", "MIX",
            Category: ComponentCategory.Devices,
            ExtraCategories: [ComponentCategory.Nonlinear, ComponentCategory.System],
            SearchTerms: ["Mixer", "mix", "multiplier", "downconvert", "upconvert", "LO", "IF",
                          "conversion", "heterodyne"],
            IsCommon: true),
        [SymbolKind.MixerD]        = new("MixerD", "MIX",
            Category: ComponentCategory.Devices,
            ExtraCategories: [ComponentCategory.Nonlinear, ComponentCategory.System],
            SearchTerms: ["MixerD", "Mixer", "differential mixer", "balanced", "mix", "multiplier",
                          "LO", "IF", "conversion"],
            IsCommon: false),
        // ── System-level blocks (brief-sys-1) ─────────────────────────────────
        // Eleven tiles under the new System category. Each names an engine component that does not
        // exist yet — deliberately: SYS-1 ships the ARTWORK and the palette, and a tile placed after
        // it fails at elaboration the way any unimplemented primitive does. SYS-2 onwards makes
        // each one real.
        //
        // Two groups share one engine component and one instance prefix, the TermG pattern: the two
        // switch tiles are one "Switch", and the coupler with both hybrids is one "Coupler".
        // Swapping SPST for SPDT, or a coupler for a hybrid, should not renumber a schematic.
        //
        // DISPLAY NAMES ARE THE COMPONENT'S OWN WORD, NOT ITS ABBREVIATION (owner, 2026-08-31):
        // "Filter", "Circulator", "Attenuator", "Directional Coupler" — never FLT, CIRC, ATT, CPL.
        // The abbreviation is what a user TYPES (the instance prefix and the short type code, both
        // unchanged); the display name is what they READ, on the tile and under the symbol, and
        // there a four-letter code is a thing to be decoded rather than a name. Every one of these
        // is a term of the discipline the tile is named after, so it needs no decoding at all.
        [SymbolKind.Balun]         = new("Balun", "BAL",
            Category: ComponentCategory.System,
            SearchTerms: ["Balun", "balanced", "unbalanced", "transformer", "differential",
                          "single-ended", "hybrid"],
            IsCommon: false),
        [SymbolKind.Circulator]    = new("Circulator", "CIRC",
            Category: ComponentCategory.System,
            SearchTerms: ["Circulator", "circ", "isolator", "nonreciprocal", "ferrite",
                          "duplex", "three-port"],
            IsCommon: true),
        [SymbolKind.Switch]        = new("Switch", "SW",
            Category: ComponentCategory.System,
            SearchTerms: ["Switch", "SPST", "sw", "on", "off", "open", "closed", "throw"],
            IsCommon: false),
        [SymbolKind.SwitchD]       = new("SwitchD", "SW",
            Category: ComponentCategory.System,
            SearchTerms: ["Switch", "SPDT", "swd", "throw", "transfer", "select", "two-way"],
            IsCommon: false),
        [SymbolKind.Amp]           = new("Amp", "AMP",
            Category: ComponentCategory.System,
            SearchTerms: ["Amplifier", "amp", "gain", "PA", "LNA", "driver", "buffer"],
            IsCommon: true),
        [SymbolKind.Coupler]       = new("Directional Coupler", "CPL",
            Category: ComponentCategory.System,
            SearchTerms: ["Coupler", "directional coupler", "cpl", "coupling", "directivity",
                          "tap", "through", "isolated"],
            IsCommon: true),
        // Both hybrids carry their phase in the DISPLAY NAME, not just in the glyph: "HYB" alone
        // stopped being an answer the moment there were two of them, and the schematic label is
        // where a reader looks to tell one instance from another.
        [SymbolKind.Hybrid90]      = new("Hybrid90", "HYB",
            Category: ComponentCategory.System,
            SearchTerms: ["Hybrid", "90", "quadrature", "branchline", "3 dB", "hyb", "splitter",
                          "combiner"],
            IsCommon: false),
        [SymbolKind.Hybrid180]     = new("Hybrid180", "HYB",
            Category: ComponentCategory.System,
            SearchTerms: ["Hybrid", "180", "rat race", "ratrace", "sum", "difference", "anti-phase",
                          "hyb", "splitter", "combiner", "magic tee"],
            IsCommon: false),
        [SymbolKind.Filter]        = new("Filter", "FLT",
            Category: ComponentCategory.System,
            SearchTerms: ["Filter", "flt", "lowpass", "highpass", "bandpass", "bandstop",
                          "Butterworth", "Chebyshev", "Gaussian", "passband", "stopband"],
            IsCommon: true),
        [SymbolKind.Atten]         = new("Attenuator", "ATT",
            Category: ComponentCategory.System,
            SearchTerms: ["Attenuator", "att", "atten", "pad", "loss", "dB"],
            IsCommon: true),
        [SymbolKind.Duplexer]      = new("Duplexer", "DPX",
            Category: ComponentCategory.System,
            SearchTerms: ["Duplexer", "dpx", "diplexer", "TX", "RX", "antenna", "front end"],
            IsCommon: false),
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
        // VProbe: a one-pin voltage probe. Its instance name (VP1, VP2, ...) becomes an ALIAS for
        // the net it touches, so the results carry that net under the user's own name as well as
        // under the net's. It stamps nothing -- see SymbolKind.VProbe.
        [SymbolKind.VProbe]        = new("VProbe", "VP",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["VProbe", "V", "voltmeter", "voltage", "probe", "meter", "net name", "label"],
            IsCommon: true),
        // WSProbe: the stability probe of T. A. Winslow, General Circuit Analysis Using The WSProbe
        // (2023). Electrically an IProbe — a 0 V series short — but its value is the run's `wsp`
        // matrix and every quantity the document derives from it. Instance name (WSP1, WSP2, ...) is
        // the document's "Label", and is what `__WspProbes` maps to the 1-based `idx`.
        [SymbolKind.WSProbe]       = new("WSProbe", "WSP",
            Category: ComponentCategory.Terminals,
            SearchTerms: ["WSProbe", "Winslow", "stability", "probe", "H0", "Y0", "loop gain",
                          "driving point"],
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
        [SymbolKind.ModSource]     = new("MOD_Source", "S",
            Category: ComponentCategory.Sources,
            SearchTerms: ["MOD_Source", "modulated", "LTE", "NR", "5G", "OFDM", "ACPR", "ACLR", "envelope", "two-carrier", "multi-carrier", "IQ", "waveform"],
            IsCommon: true),
        [SymbolKind.Snp]           = new("SnP",   "S",
            Category: ComponentCategory.DataFiles,
            SearchTerms: ["SnP", "Touchstone", "snp", "s2p", "sparam file", "data file", "network"],
            IsCommon: true),
        // SpiceModel — a SPICE .model card or .subckt definition placed as a component, run from
        // the file rather than copied into a cell. ComponentCategory.DataFiles for the same reason
        // SnP is there: what the user is placing is a FILE they already have, and that is how they
        // look for it. It is the reference half of a pair whose other half is the project tree's
        // "Copy to Workspace as Cell…"; both read the same file through SpiceCellImport.
        // Its primary category stays DataFiles — a SPICE component IS a file reference, and that is
        // where a user browsing by artifact looks for it. Devices and Nonlinear are FILTER keywords
        // added on the owner's request (2026-09-01): what the referenced card usually contains is a
        // transistor or diode, so a user shopping for an active part must find it there too. Same
        // kind, same glyph, same engine component, still one AllItems row.
        [SymbolKind.SpiceModel]    = new("SPICE", "X",
            Category: ComponentCategory.DataFiles,
            SearchTerms: ["SPICE", "SpiceModel", "spice model", "model card", ".model", ".subckt",
                          "subckt", "subcircuit", "netlist", "model file", "lib"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Devices, ComponentCategory.Nonlinear]),
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
        //
        // DataFiles is a FILTER keyword, added on the owner's request (2026-09-17), and it is
        // SpiceModel's own arrangement read the other way round: that one is primarily a file
        // reference and borrows Devices/Nonlinear, this one is primarily a device and borrows
        // DataFiles. What is placed either way is a FILE the user already has, which is how a user
        // browsing by artifact looks for it — and the two belong side by side there, since the
        // choice between them is which compiled form the model came in. Primary stays Devices, so
        // the tile still sorts and appears exactly once.
        [SymbolKind.VerilogA]      = new("VerilogA", "X",
            Category: ComponentCategory.Devices,
            SearchTerms: ["VerilogA", "Verilog-A", "OSDI", "compact model", "compiled", "custom",
                          "transistor", "device", "nonlinear", "BSIM", "PSP", "HICUM", "external",
                          "data file", "model file", "file"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear, ComponentCategory.DataFiles]),
        // A composed FET (brief-modelcomposer-1): a .cmodel the Model Composer wrote, placed as a
        // device. Beside VerilogA for VerilogA's reason — a transistor circuitRF does not ship — and
        // DataFiles as a filter keyword for the same reason, since what is placed is a FILE.
        [SymbolKind.ComposedFET]   = new("ComposedFET", "X",
            Category: ComponentCategory.Devices,
            SearchTerms: ["ComposedFET", "Model Composer", "cmodel", ".cmodel", "FET", "HEMT", "GaN",
                          "transistor", "measured model", "table model", "neural network", "device",
                          "nonlinear", "model file"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear, ComponentCategory.DataFiles]),
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
        // Two tiles, one law — the opposite arrangement from the five FET tiles above, and for the
        // opposite reason: there the names denote different equations, here they denote the same
        // equations with one sign changed. Both spellings of the polarity are search terms on both
        // tiles ("PNP" finds the n-p-n too), because a user looking for one is looking for the pair.
        [SymbolKind.BjtNpn]        = new("NPN", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["NPN", "BJT", "bipolar", "transistor", "Gummel", "Poon", "PNP", "Q",
                          "silicon", "SiGe", "HBT", "nonlinear", "device"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.BjtPnp]        = new("PNP", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["PNP", "BJT", "bipolar", "transistor", "Gummel", "Poon", "NPN", "Q",
                          "silicon", "SiGe", "HBT", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // The three p-channel MESFET laws. Every one of them lists "PMF" — the model-card type
        // name — because a user who has just imported a p-channel card is looking for exactly this.
        [SymbolKind.PFetCurtice]   = new("Curtice-P", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Curtice-P", "PMF", "p-channel", "pchannel", "Curtice", "FET", "MESFET",
                          "quadratic", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.PFetStatz]     = new("Statz-P", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Statz-P", "PMF", "p-channel", "pchannel", "Statz", "FET", "MESFET",
                          "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.PFetMaterka]   = new("Materka-P", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["Materka-P", "PMF", "p-channel", "pchannel", "Materka", "Kacprzak", "FET",
                          "MESFET", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // The JFET pair. "JFET" and "junction FET" are search terms on both, and so is "MESFET",
        // because a user who does not know which family their part belongs to is looking at the
        // same shape on a datasheet.
        [SymbolKind.JfetN]         = new("NJFET", "J",
            Category: ComponentCategory.Devices,
            SearchTerms: ["NJFET", "NJF", "JFET", "junction FET", "J", "PJFET", "Shichman", "Hodges",
                          "square law", "depletion", "FET", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.JfetP]         = new("PJFET", "J",
            Category: ComponentCategory.Devices,
            SearchTerms: ["PJFET", "PJF", "JFET", "junction FET", "J", "NJFET", "Shichman", "Hodges",
                          "square law", "depletion", "p-channel", "FET", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // The IGBT pair. The search terms cover what a user is building — a motor drive, an
        // inverter, a hard-switched bridge — because "IGBT" is a part someone reaches for by
        // application rather than by law.
        [SymbolKind.IgbtN]         = new("NIGBT", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["NIGBT", "IGBT", "insulated gate", "bipolar", "motor drive", "inverter",
                          "bridge", "switch", "power", "tail current", "PIGBT", "Q", "transistor",
                          "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.IgbtP]         = new("PIGBT", "Q",
            Category: ComponentCategory.Devices,
            SearchTerms: ["PIGBT", "IGBT", "insulated gate", "bipolar", "p-channel", "inverter",
                          "switch", "power", "NIGBT", "Q", "transistor", "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // The ferrite bead. A LUMPED element, not a device — it is a linear impedance. The search
        // terms are what a user is actually trying to do with one: damp a rail, suppress EMI, stop a
        // decoupling network ringing.
        [SymbolKind.Bead]          = new("Bead", "FB",
            Category: ComponentCategory.Lumped,
            SearchTerms: ["Bead", "ferrite", "ferrite bead", "FB", "EMI", "suppression", "damping",
                          "choke", "common mode", "supply rail", "decoupling", "lossy", "absorb"],
            IsCommon: false),
        // The level-3 pair. Two more tiles rather than a "level" parameter on the level-1 ones,
        // for the reason the five MESFET laws are five tiles: a level is a different set of
        // equations, and its six short-channel parameters mean nothing to level 1. One tile
        // presenting the union of both sets would silently accept whichever the user did not mean.
        [SymbolKind.Mos3N]         = new("NMOS3", "M",
            Category: ComponentCategory.Devices,
            SearchTerms: ["NMOS3", "NMOS", "MOSFET", "MOS", "level 3", "short channel", "DIBL",
                          "velocity saturation", "submicron", "PMOS3", "FET", "transistor", "M",
                          "nonlinear", "device", "bulk", "body"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.Mos3P]         = new("PMOS3", "M",
            Category: ComponentCategory.Devices,
            SearchTerms: ["PMOS3", "PMOS", "MOSFET", "MOS", "level 3", "short channel", "DIBL",
                          "velocity saturation", "submicron", "NMOS3", "FET", "transistor", "M",
                          "nonlinear", "device", "p-channel", "bulk", "body"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // The vertical power MOSFET pair. "VDMOS" is the model-card type name and is a search term
        // on both, as are the words a user reaches for when they want a switch rather than an
        // amplifier — body diode, half bridge, synchronous rectifier.
        [SymbolKind.VdmosN]        = new("NVDMOS", "M",
            Category: ComponentCategory.Devices,
            SearchTerms: ["NVDMOS", "VDMOS", "power MOSFET", "power", "MOSFET", "MOS", "switch",
                          "body diode", "half bridge", "synchronous rectifier", "avalanche",
                          "PVDMOS", "M", "transistor", "nonlinear", "device"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.VdmosP]        = new("PVDMOS", "M",
            Category: ComponentCategory.Devices,
            SearchTerms: ["PVDMOS", "VDMOS", "power MOSFET", "power", "MOSFET", "MOS", "switch",
                          "body diode", "high side", "p-channel", "NVDMOS", "M", "transistor",
                          "nonlinear", "device"],
            IsCommon: false,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        // The MOS pair. Two tiles for one law in two channel types, arranged like the BJT's pair
        // rather than the FET's five: the names denote the same equations with one sign changed.
        // Both channel spellings are search terms on both tiles, because a user looking for one is
        // looking for the pair — and "MOSFET", "NMOS" and "PMOS" all land here, which is what a
        // user who has just been refused a NMOS model card will type.
        [SymbolKind.Mos1N]         = new("NMOS1", "M",
            Category: ComponentCategory.Devices,
            SearchTerms: ["NMOS", "NMOS1", "MOSFET", "MOS", "level 1", "Shichman", "Hodges",
                          "square law", "PMOS", "FET", "transistor", "M", "nonlinear", "device",
                          "enhancement", "bulk", "body"],
            IsCommon: true,
            ExtraCategories: [ComponentCategory.Nonlinear]),
        [SymbolKind.Mos1P]         = new("PMOS1", "M",
            Category: ComponentCategory.Devices,
            SearchTerms: ["PMOS", "PMOS1", "MOSFET", "MOS", "level 1", "Shichman", "Hodges",
                          "square law", "NMOS", "FET", "transistor", "M", "nonlinear", "device",
                          "enhancement", "p-channel", "bulk", "body"],
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
        // CLIN — the ideal coupled pair, TLIN's four-port sibling: same category, same ideal/lossless
        // standing, no substrate and no layout. Prefix "CL" (no other kind or alias spells it).
        [SymbolKind.CLine]         = new("CLIN", "CL",
            Category: ComponentCategory.TransmissionLine,
            SearchTerms: ["CLIN", "CLine", "coupled line", "coupled lines", "coupled pair", "coupler",
                          "even mode", "odd mode", "Ze", "Zo", "ideal", "lossless", "line"],
            IsCommon: false),
        // BCLIN — the offset broadside-coupled STRIPLINE pair: a transmission line first (no microstrip
        // substrate), its cross-section resolved from two adjacent conductors of the stackup.
        [SymbolKind.BCLine]        = new("BCLIN", "BCL",
            Category: ComponentCategory.TransmissionLine,
            SearchTerms: ["BCLIN", "broadside", "broadside coupled", "stripline", "offset stripline",
                          "coupled stripline", "coupled lines", "coupler", "3 dB coupler", "even mode",
                          "odd mode", "Shelton", "field solver"],
            IsCommon: false),
        // Match — a synthesised bandpass matching network (match.md §8.4). Prefix "MN", NOT "M":
        // "M" is Mutual. The search terms deliberately cover what it SOLVES (match, interstage, Cgs,
        // Ropt) as well as how it works (Fano, Norton, Chebyshev), because the people who reach for
        // it think of it both ways.
        [SymbolKind.Match]         = new("Match", "MN",
            Category: ComponentCategory.Matching,
            SearchTerms: ["impedance matching", "filter", "filter design", "transform", "Chebyshev",
                          "Butterworth", "Bessel", "match", "matching", "interstage", "Fano", "Norton",
                          "absorb", "Cgs", "Cds", "Ropt", "bandpass", "lowpass", "highpass",
                          "dual-band", "tri-band", "multiband"],
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
        // Grounded coplanar waveguide and stripline (brief-artsch-1): transmission lines bound to the stackup
        // as MLIN is, so TransmissionLine is their primary group; neither is a microstrip.
        [SymbolKind.Cpwg]          = new("CPWG",  "CPW",
            Category: ComponentCategory.TransmissionLine,
            SearchTerms: ["CPWG", "GCPW", "grounded coplanar", "coplanar waveguide", "conductor-backed coplanar",
                          "coplanar", "CPW", "line", "Ghione"],
            IsCommon: true),
        [SymbolKind.Slin]          = new("SLIN",  "SL",
            Category: ComponentCategory.TransmissionLine,
            SearchTerms: ["SLIN", "stripline", "strip line", "offset stripline", "buried", "inner layer",
                          "line", "Wheeler", "Cohn"],
            IsCommon: true),
        [SymbolKind.MBend]         = new("MBEND", "MB",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MBEND", "microstrip bend", "bend", "corner", "miter"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.MCurve]        = new("MCURVE", "MCV",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MCURVE", "microstrip curve", "curved bend", "radiused bend", "arc", "smooth bend",
                          "bend", "radius"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        // MCLIN — the edge-coupled microstrip pair, MLIN's four-port sibling, wired as CLIN.
        [SymbolKind.MCLine]        = new("MCLIN", "MCL",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MCLIN", "coupled microstrip", "edge coupled", "coupled lines", "coupled line",
                          "coupler", "directional coupler", "even mode", "odd mode", "Kirschning", "Jansen"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        // MBCLIN — broadside-coupled microstrip: one strip buried, one above it, a single ground.
        [SymbolKind.MBCLine]       = new("MBCLIN", "MBCL",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MBCLIN", "broadside", "broadside coupled", "broadside microstrip", "coupled lines",
                          "coupler", "multilayer coupler", "even mode", "odd mode", "field solver"],
            ExtraCategories: [ComponentCategory.TransmissionLine]),
        [SymbolKind.MMiter]        = new("MMITER", "MMT",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["MMITER", "mitred bend", "mitered bend", "chamfered bend", "chamfer", "miter", "mitre",
                          "bend", "corner", "45 degree bend"],
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
        // The two vias (brief-via-component.md). Microstrip, because that is where they are used: a
        // line changing layers, and the return of a shunt part on a board. Not transmission lines.
        [SymbolKind.Via]          = new("VIA", "VIA",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["VIA", "via", "plated through hole", "PTH", "through hole", "layer change",
                          "layer transition", "barrel", "blind via", "stub", "Goldfarb", "Pucel"]),
        [SymbolKind.ViaGnd]       = new("VIAGND", "VIAG",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["VIAGND", "via", "ground via", "via to ground", "grounding via", "shunt",
                          "return", "pad capacitance", "plated through hole"]),
        // A test-point pad (brief-dfr26-7): with the vias, since it is one ending on a round pad.
        [SymbolKind.TestPad]      = new("TESTPAD", "TP",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["TESTPAD", "test point", "test pad", "probe pad", "pad", "round pad", "pad capacitance",
                          "cut-out", "clearance", "via", "EM port"]),
        // The MMIC passives (AA-1), in their own category. Not also under Lumped: that category is
        // held to the ideal lumped elements (LibraryCatalogTests), and these are process parts.
        // A ground-signal-ground probe pad and the microstrip leaving it (brief-review-6 follow-up): with the
        // microstrip parts, since it is MLIN under the hood, and in the MMIC group, where on-wafer probing is.
        [SymbolKind.GsgPad]       = new("GSGPAD", "GSG",
            Category: ComponentCategory.Microstrip,
            SearchTerms: ["GSGPAD", "GSG", "ground signal ground", "probe pad", "RF probe", "on-wafer", "wafer probe",
                          "pitch", "probe", "pad", "launch"],
            ExtraCategories: [ComponentCategory.Mmic]),
        [SymbolKind.MimCap]       = new("MIMCAP", "MIM",
            Category: ComponentCategory.Mmic,
            SearchTerms: ["MIMCAP", "MIM", "MIM capacitor", "metal insulator metal", "capacitor", "MMIC",
                          "thin film capacitor", "nitride"]),
        [SymbolKind.Spiral]       = new("SPIRAL", "SPL",
            Category: ComponentCategory.Mmic,
            SearchTerms: ["SPIRAL", "spiral", "spiral inductor", "square spiral", "inductor", "coil", "MMIC",
                          "Wheeler"]),
        [SymbolKind.OctSpiral]    = new("OSPIRAL", "OSPL",
            Category: ComponentCategory.Mmic,
            SearchTerms: ["OSPIRAL", "octagonal spiral", "octagon", "spiral", "spiral inductor", "inductor",
                          "coil", "MMIC", "Wheeler"]),
        [SymbolKind.Tfr]          = new("TFR", "TFR",
            Category: ComponentCategory.Mmic,
            SearchTerms: ["TFR", "thin film resistor", "resistor", "sheet resistance", "ohms per square",
                          "MMIC", "NiCr", "TaN"]),
        [SymbolKind.Airbridge]    = new("AIRBRIDGE", "AB",
            Category: ComponentCategory.Mmic,
            SearchTerms: ["AIRBRIDGE", "air bridge", "airbridge", "crossover", "bridge", "MMIC"]),
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
    /// Whether an edit gesture may leave this component's pin BEHIND — carrying no wire with it when
    /// it moves, and holding no wire back when a wire moves out from under it.
    ///
    /// <para><b>True for exactly one kind, and the answer is what that kind is FOR.</b> Every other
    /// component in this editor owes the user one guarantee: moving the picture never re-wires the
    /// circuit. Connectivity here is geometric — a port and a wire vertex at the same point ARE one
    /// node — so a symbol that moved without taking its wires would silently produce a different
    /// netlist while still looking wired up. That is why a drag drags the wires, a rotate re-routes
    /// them, and a wire cannot slide out from under a pin.</para>
    ///
    /// <para>A <see cref="SymbolKind.VProbe"/> has nothing to re-wire. It stamps nothing, so moving
    /// it cannot change the circuit — only which point of the circuit is being reported, which is the
    /// whole reason to pick one up. Made to drag its wire along, it would deform the schematic every
    /// time somebody moved it to look somewhere else, and the user would have to repair the drawing
    /// after every look (owner, 2026-09-06). So it comes off, the wire stays exactly where it was
    /// drawn, and a probe that ends up on nothing is REPORTED at extraction rather than silently
    /// reading a floating point.</para>
    ///
    /// <para><b>Selecting the probe together with its wire still keeps them together</b>, and needs
    /// nothing from this: the two move by the same delta, so the pin is still on the wire when the
    /// gesture ends. This governs only the case where they move by DIFFERENT amounts.</para>
    /// </summary>
    public static bool DetachesFreely(SymbolKind kind) => kind is SymbolKind.VProbe;

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
    /// The parameter that DECIDES how many terminals a placed component has, or null when the count
    /// is fixed by the type.
    ///
    /// <para><b>Why this is a fact and not a description.</b> A reference catalogue that printed a
    /// port count for one of these would print a number that is plausible, specific and wrong — the
    /// failure class <c>sweep-unit-scale-and-mark</c> records, where a mark read without its scale
    /// produced a run at 2 Hz that looked entirely normal. The honest answer is "it depends, and
    /// here is what on", and this is the one place that can give it
    /// (brief-automation-6-reference-and-components.md R-aut6-9).</para>
    ///
    /// <para><b>It answers for the SYMBOL, and a token can be variadic where its symbols are not.</b>
    /// <see cref="SymbolKind.Switch"/> and <see cref="SymbolKind.SwitchD"/> are two tiles over one
    /// engine component whose port count is <c>1 + Throws</c>: each tile draws a fixed pin set and
    /// seeds its own <c>Throws</c>, so the tile is fixed and the component is not. Naming the
    /// parameter here is true of both readings — it is what an edit would change.</para>
    ///
    /// <para><b>Not a substitute for asking the model.</b> Where a primitive is parameterless the
    /// model itself answers, and that is what a caller should read; this is for the ones nobody may
    /// construct without inventing values first.</para>
    /// </summary>
    public static string? PortCountParameter(SymbolKind kind) => kind switch
    {
        // The three variadic boxes. Their pin geometry is generated from N — SymbolPortDefs.For
        // takes the count — and PortCountParameterIsExercisedTests holds that agreement.
        SymbolKind.Snp or SymbolKind.ZPort or SymbolKind.Sdd => "NumPorts",
        // A Verilog-A model's terminal count is the MODEL's, filled in from the file it names.
        SymbolKind.VerilogA                                  => "Pins",
        // 1 common port plus `Throws` throws. See the remarks above for why both tiles answer.
        SymbolKind.Switch or SymbolKind.SwitchD              => "Throws",
        // A wBond's terminals are 2 per wire array it places, plus the reference pin when RefPin is
        // on — so the count follows the arrays selected out of the design it carries or links to,
        // and there is no fixed answer at all until one is chosen.
        SymbolKind.WBond                                     => "Arrays",
        _                                                    => null,
    };

    /// <summary>
    /// What the terminal ORDER means for this kind — the fact a pin name cannot carry and no figure
    /// can show.
    ///
    /// <para><b>Why this exists.</b> <see cref="SymbolPortDefs"/> names a terminal wherever the
    /// terminal HAS a name (<c>g d s</c>, <c>a c</c>, <c>out+ out- ctrl+ ctrl-</c>), and for those
    /// the name is the whole answer. But most two-terminal parts have no names to give, so the
    /// catalogue could only ever say "net 1 is terminal 1" — a table that restates the row number
    /// and carries nothing (owner, 2026-09-05). What a caller actually needs to know about those is
    /// whether the order MATTERS, and if so what distinguishes the ends. That is a fact about the
    /// component, not about a pin, so it is stated once here.</para>
    ///
    /// <para><b>"They are interchangeable" is an answer, not a blank.</b> A reader who is told a
    /// resistor's ends may be swapped is finished; a reader shown a numbered table has to go and
    /// find out. The two are the same length on the page and only one of them is information.</para>
    ///
    /// <para><b>Empty is still the honest default.</b> A kind nobody has written a note for gets
    /// nothing rather than a guess — the rule <see cref="ParameterDescription"/> already follows,
    /// and for the same reason: an invented claim about terminal order is exactly the
    /// plausible-specific-and-wrong failure the reference surface exists to prevent. A file-backed
    /// component (<see cref="SymbolKind.SpiceModel"/>, <see cref="SymbolKind.VerilogA"/>) is left
    /// deliberately empty — its terminals come from the file it names, so no note written here could
    /// be true of a placed one.</para>
    /// </summary>
    public static string TerminalNote(SymbolKind kind) => kind switch
    {
        // ── Symmetric, and saying so is the point ────────────────────────────
        SymbolKind.Resistor or SymbolKind.Capacitor or SymbolKind.Bead
                              or SymbolKind.Src or SymbolKind.Prc =>
            "The two terminals are interchangeable — swapping them gives the same circuit.",

        SymbolKind.Tline or SymbolKind.Mlin or SymbolKind.Cpwg or SymbolKind.Slin =>
            "The two terminals are interchangeable: the line is uniform, so neither end is the input.",

        SymbolKind.CLine or SymbolKind.MCLine or SymbolKind.BCLine or SymbolKind.MBCLine =>
            "Terminals 1 and 2 are the two ends of one line, 3 and 4 the two ends of the other; 3 sits "
          + "beside 1 and 4 beside 2. Driving 1 of a matched pair, 2 is the through port, 3 the "
          + "coupled port and 4 the isolated port.",

        SymbolKind.Atten or SymbolKind.Filter =>
            "The two terminals are interchangeable — this block is symmetric, and its parameters "
          + "describe both ports at once.",

        SymbolKind.MBend =>
            "The two terminals are interchangeable. One width and one angle describe the whole bend, "
          + "so the pins differ only in which way it turns on the canvas.",

        SymbolKind.MCurve =>
            "The two terminals are interchangeable. One width, one radius and one angle describe the "
          + "whole curve, so the pins differ only in which way it turns on the canvas.",

        SymbolKind.MMiter =>
            "The two terminals are interchangeable. The chamfer is symmetric about the corner's bisector, "
          + "so the pins differ only in which way it turns on the canvas.",

        // ── Symmetric ALONE, and not once coupled ────────────────────────────
        // The dot convention, which is a property of the pair rather than of either inductor. It is
        // stated on all three because all three implement IInductiveBranch and any of them may be
        // either end of a Mutual (owner, 2026-09-05).
        SymbolKind.Inductor or SymbolKind.Srlc or SymbolKind.Prlc
                            or SymbolKind.Srl  or SymbolKind.Slc
                            or SymbolKind.Prl  or SymbolKind.Plc =>
            "Interchangeable on its own — but NOT once coupled. A Mutual (M) couples the two "
          + "elements' branch currents, and each branch current runs from that element's own "
          + "terminal 1 to its terminal 2, so swapping one of them reverses the sign of the "
          + "coupling. Terminal 1 is the dotted end.",

        // ── Asymmetric, and the ends are told apart by a parameter ───────────
        SymbolKind.Mtaper =>
            "Not interchangeable: terminal 1 is the W1 end and terminal 2 the W2 end.",

        SymbolKind.Mklopf =>
            "Not interchangeable: terminal 1 is the Z1 end and terminal 2 the Z2 end.",

        SymbolKind.Via =>
            "Terminal A lands on FromLayer and terminal B on ToLayer. Swapping them swaps which "
          + "layer each end is on, and which end a stub hangs from when the drill runs past a layer.",

        SymbolKind.ViaGnd =>
            "One terminal, A, on FromLayer. The far end is ground: the via lands on GroundLayer.",

        SymbolKind.GsgPad =>
            "Not interchangeable: terminal 1 is where the probe's signal tip lands, the middle of the signal pad, "
          + "and terminal 2 the far end of the line. Put the measurement port on terminal 1.",

        SymbolKind.TestPad =>
            "Not interchangeable: terminal 1 is the feed — the via on ViaFrom, or the end of the side stub — and "
          + "terminal 2 is the pad centre. Leave 2 open for a test point that is not in use, or continue it to a "
          + "connector.",

        SymbolKind.MimCap =>
            "Not interchangeable: terminal 1 is the top plate and terminal 2 the bottom plate, the one "
          + "lying on the substrate, which carries the plate's capacitance to ground. For a shunt "
          + "capacitor ground terminal 2.",

        SymbolKind.Spiral =>
            "Not interchangeable in the drawn part: terminal 1 is the outer end and terminal 2 the "
          + "inner end, brought out over the turns on the bridge metal. The model itself is symmetric.",

        SymbolKind.OctSpiral =>
            "Not interchangeable in the drawn part: terminal 1 is the outer end and terminal 2 the "
          + "inner end, brought out over the turns on the bridge metal. The model itself is symmetric.",

        SymbolKind.Tfr =>
            "Interchangeable: the two ends of the film.",

        SymbolKind.Airbridge =>
            "Terminals 1 and 2 are the bridge's two landings, and 3 and 4 the two ends of the line it "
          + "crosses; each pair is interchangeable. Tie 3 and 4 to ground for a bridge over a ground strap.",

        SymbolKind.Match =>
            "Not interchangeable: terminal 1 is the R1 side and terminal 2 the R2 side. The "
          + "synthesised ladder is stored R1-first, so swapping the two reverses every asymmetric "
          + "match.",

        SymbolKind.NonlinearC =>
            "C(V) is evaluated at V(terminal 1) − V(terminal 2), so any odd coefficient (C1, C3, …) "
          + "makes the two terminals distinct. With C0 alone the capacitor is linear and they are "
          + "interchangeable.",

        // ── Sources: the polarity the pin names carry, and what it is FOR ────
        SymbolKind.Vdc =>
            "Vdc is the voltage at + relative to −.",

        SymbolKind.ToneSource =>
            "V and Vdc are the voltage at + relative to −, and Phase is referred to the same pair.",

        SymbolKind.CurrentToneSource =>
            "A positive I is delivered INTO the + terminal and drawn out of the −, which is what the "
          + "arrow on the + lead points at. Note this is the opposite sense to the VCCS's arrow, "
          + "which is a controlled source and is drawn the way controlled sources are.",

        SymbolKind.P1Tone or SymbolKind.PnTone or SymbolKind.ModSource =>
            "+ is the RF output and − the reference, normally ground — Term's own convention, since "
          + "this is a source with Term's internal resistance in front of it. The source's internal "
          + "impedance Z is in series with +.",

        // ── Named terminals whose names are abbreviations ────────────────────
        SymbolKind.Diode =>
            "a is the anode and c the cathode; forward current flows a to c.",

        SymbolKind.IProbe =>
            "A 0 V ammeter: the current it reports flows np to nm. Swapping the two negates every "
          + "measurement that reads it.",

        SymbolKind.VProbe =>
            "One terminal, at the arrow's tip. It taps the net it touches and names it after the "
          + "instance; there is no second terminal to get the wrong way round, and nothing it can "
          + "short together.",

        SymbolKind.WSProbe =>
            "G is the generator-side terminal, L the load-side. ZG looks out of G, ZL out of L; "
          + "swapping them swaps every G/L-labelled output and negates nothing else.",

        _ => "",
    };

    /// <summary>
    /// Engine type-reference string for a given SymbolKind — what goes in the .cnl Reference field
    /// and into <see cref="Instance.Reference"/>. Differs from <see cref="DisplayName(SymbolKind)"/>
    /// for ZPort ("Z" vs "Z_Port"), ToneSource ("VTone" vs "V_1Tone"), CurrentToneSource
    /// ("ITone" vs "I_1Tone").
    /// </summary>
    public static string EngineReference(SymbolKind kind, int portCount = 0) => kind switch
    {
        SymbolKind.Resistor      => "R",
        SymbolKind.Inductor      => "L",
        SymbolKind.Capacitor     => "C",
        SymbolKind.Srlc          => "SRLC",
        SymbolKind.Prlc          => "PRLC",
        SymbolKind.Srl           => "SRL",
        SymbolKind.Src           => "SRC",
        SymbolKind.Slc           => "SLC",
        SymbolKind.Prl           => "PRL",
        SymbolKind.Prc           => "PRC",
        SymbolKind.Plc           => "PLC",
        SymbolKind.Vdc           => "Vdc",
        SymbolKind.ToneSource    => "V_1Tone",
        SymbolKind.CurrentToneSource => "I_1Tone",
        SymbolKind.Vccs          => "VCCS",
        SymbolKind.Vcvs          => "VCVS",
        SymbolKind.OpAmp         => "OpAmp",
        SymbolKind.OpAmp5        => "OpAmp",  // SAME engine component as OpAmp, Pins=5
        SymbolKind.Transformer   => "Transformer",
        SymbolKind.Mixer         => "Mixer",
        SymbolKind.MixerD        => "Mixer",  // SAME engine component as Mixer — no parallel model
        // The system blocks. NONE of these resolves yet — no ComponentModelFactory entry exists for
        // any of them — which is exactly the intent of SYS-1: the tile places, saves and reloads,
        // and simulating it fails the way any unimplemented primitive does.
        SymbolKind.Balun         => "Balun",
        SymbolKind.Circulator    => "Circulator",
        SymbolKind.Switch        => "Switch",
        SymbolKind.SwitchD       => "Switch",   // SAME engine component as Switch — no parallel model
        SymbolKind.Amp           => "Amp",
        SymbolKind.Coupler       => "Coupler",
        SymbolKind.Hybrid90      => "Coupler",  // SAME engine component as Coupler, at 3.01 dB / 90°
        SymbolKind.Hybrid180     => "Coupler",  // and again, at 3.01 dB / 180°
        SymbolKind.Filter        => "Filter",
        SymbolKind.Atten         => "Atten",
        SymbolKind.Duplexer      => "Duplexer",
        SymbolKind.Term          => "Port",  // engine Reference stays "Port" for .cnl compat
        SymbolKind.TermG         => "Port",  // SAME engine component as Term — R-hk-6, no parallel model
        SymbolKind.Pin           => "Pin",   // sentinel — IsPrimitive("Pin")==false; elaborator skips it
        SymbolKind.IProbe        => "IProbe",
        SymbolKind.WSProbe       => "WSProbe",
        // Sentinel, like Var and Meas: no ComponentModelFactory entry exists and none can. A VProbe
        // line DOES reach the .cnl and the elaborator -- unlike VAR/MEAS -- but the elaborator turns
        // it into a net-name alias and builds no model, which is what makes it free of the circuit.
        SymbolKind.VProbe        => "VProbe",
        SymbolKind.Sdd           => "SDD",
        SymbolKind.ZPort         => "Z_Port",
        SymbolKind.Ground        => "GND",
        SymbolKind.Var           => "VAR",   // sentinel — never emitted as an Instance; not a factory primitive
        SymbolKind.Meas          => "MEAS",  // sentinel — never emitted as an Instance; rows route to tb.Measurements
        SymbolKind.P1Tone        => "P1Tone",
        SymbolKind.PnTone        => "PnTone",
        SymbolKind.ModSource     => "MOD_Source",
        SymbolKind.Snp           => "SnP",
        // Sentinel, like Var and Meas: no ComponentModelFactory entry exists and none can. A
        // SpiceModel resolves at EXTRACTION to whatever its file describes — a primitive for a
        // .model card, a subcircuit cell for a .subckt — so this string never reaches the engine.
        // It exists so every SymbolKind answers, and so a stray emission fails by name rather than
        // by silently stamping something.
        SymbolKind.SpiceModel    => "SpiceModel",
        SymbolKind.NonlinearC    => "NonlinearC",
        SymbolKind.Mutual        => "Mutual",
        SymbolKind.Tline         => "TLIN",
        SymbolKind.CLine         => "CLIN",
        SymbolKind.MCLine        => "MCLIN",
        SymbolKind.BCLine        => "BCLIN",
        SymbolKind.MBCLine       => "MBCLIN",
        SymbolKind.Match         => "Match",
        // All three tuner tiles place the identical engine component.
        SymbolKind.Tuner         => "Tuner",
        SymbolKind.SourceTuner   => "Tuner",
        SymbolKind.LoadTuner     => "Tuner",
        SymbolKind.Mlin          => "MLIN",
        SymbolKind.Cpwg          => "CPWG",
        SymbolKind.Slin          => "SLIN",
        SymbolKind.MBend         => "MBEND",
        SymbolKind.MCurve        => "MCURVE",
        SymbolKind.MMiter        => "MMITER",
        SymbolKind.MTee          => "MTEE",
        SymbolKind.MCross        => "MCROSS",
        SymbolKind.Mtaper        => "MTAPER",
        SymbolKind.Mklopf        => "MKLOPF",
        SymbolKind.Via           => "VIA",
        SymbolKind.ViaGnd        => "VIAGND",
        SymbolKind.TestPad       => "TESTPAD",
        SymbolKind.GsgPad        => "GSGPAD",
        SymbolKind.MimCap        => "MIMCAP",
        SymbolKind.Spiral        => "SPIRAL",
        SymbolKind.OctSpiral     => "OSPIRAL",
        SymbolKind.Tfr           => "TFR",
        SymbolKind.Airbridge     => "AIRBRIDGE",
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
        SymbolKind.ComposedFET     => ComponentModelFactory.ComposedFetTypeName,
        // Two engine components over one model, so the netlist says which transistor it is rather
        // than leaving it to a parameter that a later edit could silently disagree with the symbol.
        // p-channel is a leading "P" on the engine name, for the three laws that have one.
        SymbolKind.PFetCurtice     => "PFET_Curtice",
        SymbolKind.PFetStatz       => "PFET_Statz",
        SymbolKind.PFetMaterka     => "PFET_Materka",
        SymbolKind.JfetN           => "JFET_N",
        SymbolKind.JfetP           => "JFET_P",
        SymbolKind.IgbtN           => "IGBT_N",
        SymbolKind.IgbtP           => "IGBT_P",
        SymbolKind.Bead            => "Bead",
        SymbolKind.VdmosN          => "VDMOS_N",
        SymbolKind.VdmosP          => "VDMOS_P",
        // One engine component per LAW and per CHANNEL — a level is different equations, a channel
        // is a sign the schematic has to show. Neither can be a parameter without letting the
        // netlist and the symbol disagree.
        SymbolKind.Mos1N           => "MOS1_N",
        SymbolKind.Mos3N           => "MOS3_N",
        SymbolKind.Mos3P           => "MOS3_P",
        SymbolKind.Mos1P           => "MOS1_P",
        SymbolKind.BjtNpn          => "BJT_NPN",
        SymbolKind.BjtPnp          => "BJT_PNP",
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
    /// <para>wBond's `File` names the layout wire file the instance is checked against (wbond.md §9.7).
    /// Bringing wires IN is File ▸ Import ▸ Wirebond Wires…, which has its own picker; this one is a
    /// path, and a path is exactly the kind of value nobody should be asked to type.</para>
    public static bool IsFilePathParameter(SymbolKind kind, string parameterName)
        => kind is SymbolKind.VerilogA or SymbolKind.WBond
        && parameterName.Equals("File", StringComparison.Ordinal);

    /// <summary>A newly placed wBond's operating temperature, °C: <see cref="CircuitRF.WBond.WireMaterials.DefaultOperatingTempC"/>,
    /// the one default wire temperature, so the Inspector and the wBond editor cannot disagree.</summary>
    public static string WBondDefaultTempC { get; } =
        CircuitRF.WBond.WireMaterials.DefaultOperatingTempC.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A newly placed wBond's output-end temperature in solved mode, °C (brief-wbond-wire-temperature D4):
    /// <see cref="CircuitRF.WBond.WireMaterials.DefaultEndTempC"/> — a package lead, where <c>TempStart</c> is a die pad.</summary>
    public static string WBondDefaultTempEndC { get; } =
        CircuitRF.WBond.WireMaterials.DefaultEndTempC.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The unit a parameter is ALWAYS stated in, for the Inspector to show in place of a unit combo —
    /// or empty when the parameter has an ordinary one. Today that is the temperatures: <c>Temp</c>,
    /// <c>Tnom</c> and <c>Dtemp</c> are degrees Celsius on every built-in kind, and are declared
    /// unitless only because a <c>C</c> unit token would collide with capacitance in the <c>.cnl</c>
    /// tokenizer. A combo reading "None" on them said nothing about the scale a user was typing in.
    ///
    /// <para>Never on <see cref="SymbolKind.Generic"/>: a placed cell or kit part declares its own
    /// parameters, and a name alone says nothing about what that cell means by it.</para>
    /// </summary>
    public static string FixedUnitLabel(SymbolKind kind, string parameterName)
        => kind is not SymbolKind.Generic && parameterName is "Temp" or "Tnom" or "Dtemp" or "TempStart" or "TempEnd" ? "°C" : "";

    /// <summary>
    /// True when a parameter's value is fixed by the TILE the user placed, so it is shown but never
    /// editable — not in the properties dialog, and not through a schematic label either.
    ///
    /// <para><b>The switches' <c>Throws</c> is the case (owner, 2026-09-13).</b> It is what makes
    /// <c>Switch</c> and <c>SwitchD</c> one engine component, and it has exactly one legal value per
    /// tile: 1 for the SPST, 2 for the SPDT. Nothing follows an edit of it —
    /// <c>EditableSchematic</c>'s pin table is written per tile, so the symbol keeps its 2 or 3 pins
    /// whatever this says — so changing it only desynchronises the model's port count from the
    /// drawing, and the design then fails at RUN with a message about nets the user never typed
    /// (<i>"Switch 'SW1': expected 8 nets (com+, com−, 1+, 1−, 2+, 2−, 3+, 3−); got 6"</i>). Placing
    /// the other tile is how you get the other throw count.</para>
    ///
    /// <para>Shown rather than hidden, because "how many throws has this switch" is exactly what a
    /// reader of the dialog wants to know — the same call
    /// <c>ParameterRowViewModel.ExpressionReadOnly</c> makes for a VerilogA model's terminal
    /// count.</para>
    /// </summary>
    public static bool IsStructuralParameter(SymbolKind kind, string parameterName)
        => (kind is SymbolKind.Switch or SymbolKind.SwitchD
            && parameterName.Equals("Throws", StringComparison.Ordinal))
        // The op-amp's Pins is the same arrangement: fixed by the tile, 3 or 5.
        || (kind is SymbolKind.OpAmp or SymbolKind.OpAmp5
            && parameterName.Equals("Pins", StringComparison.Ordinal));

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
    /// <c>Model</c>, <c>Pins</c> and <c>OpVars</c> are circuitRF's own and structural — the symbol
    /// cannot draw without <c>Pins</c>, and <c>OpVars</c> is a setting with its own control rather
    /// than a value — so they are not removable. Widening this to another component type is
    /// a matter of adding it here, but only where a parameter is genuinely independent of its
    /// neighbours.</para>
    /// </summary>
    /// <summary>
    /// True when this type's own default for <paramref name="parameterName"/> is EMPTY — a row such as a test pad's
    /// <c>ClearD</c> or a via's <c>Drill</c>, where empty means "follow the technology". Clearing such a row is a value
    /// the user can choose, not a typing slip to revert.
    /// </summary>
    public static bool HasEmptyDefault(SymbolKind kind, string parameterName)
        => DefaultParameters(kind, 2).Any(d => string.Equals(d.Name, parameterName, StringComparison.Ordinal) && d.Expression.Length == 0);

    public static bool IsRemovableParameter(SymbolKind kind, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName)) return false;

        return kind switch
        {
            // Asked of the factory, which owns the list — see ComponentModelFactory
            // .IsVerilogAHostParameter for why there is one predicate and not four copies of it.
            SymbolKind.VerilogA => !ComponentModelFactory.IsVerilogAHostParameter(parameterName),

            // SDD: every visible row is a user-authored equation or a named constant — SddName and
            // SddPortCount are minted at elaboration and never stored, NumPorts is not shown. Each is
            // independent of its neighbours, which is the condition above. Per-row removal is also
            // what the equation PICKER implies: you add one named slot, so you remove one named slot,
            // and "remove the last one you happened to add" was never the right gesture here.
            SymbolKind.Sdd => true,

            // ZPort: the Z[i,j] matrix is structural — its shape is the port count — so an entry is
            // never removable. Anything ELSE on a ZPort is removable, and that is not a hypothetical:
            // it is how a design already carrying an inert Z[n] from the old "+" button gets rid of
            // it, now that the button and the row rename are both gone.
            SymbolKind.ZPort => !RxZMatrixEntry.IsMatch(parameterName) && parameterName != "NumPorts",

            _ => false,
        };
    }

    /// <summary>The Z-matrix entry spelling <c>ComponentModelFactory</c> reads — nothing else on a
    /// ZPort is one, whatever it looks like.</summary>
    private static readonly Regex RxZMatrixEntry = new(@"^Z\[\d+,\d+\]$", RegexOptions.Compiled);

    public static string ParameterDescription(SymbolKind kind, string parameterName)
        => kind is SymbolKind.Mixer or SymbolKind.MixerD ? MixerParameterDescription(parameterName)
         : kind is SymbolKind.Duplexer ? DuplexerParameterDescription(parameterName)
         : SystemBlockParameterDescription(kind, parameterName) is { Length: > 0 } sysDesc ? sysDesc
         : ViaParameterDescription(kind, parameterName) is { Length: > 0 } viaDesc ? viaDesc
         : TestPadParameterDescription(kind, parameterName) is { Length: > 0 } tpDesc ? tpDesc
         : MmicParameterDescription(kind, parameterName) is { Length: > 0 } mmicDesc ? mmicDesc
         : CoupledPairParameterDescription(kind, parameterName) is { Length: > 0 } cpDesc ? cpDesc
         : WBondParameterDescription(kind, parameterName) is { Length: > 0 } wbDesc ? wbDesc
         : PlanarLineParameterDescription(kind, parameterName) is { Length: > 0 } plDesc ? plDesc
         : TlineParameterDescription(kind, parameterName) is { Length: > 0 } tlDesc ? tlDesc
         : kind is SymbolKind.ComposedFET ? ComposedFetParameterDescription(parameterName)
         : kind is not SymbolKind.VerilogA ? "" : parameterName switch
        {
            "File"  => "The model to load: a compiled model (.osdi), or Verilog-A source (.va, .vams) "
                     + "which circuitRF builds once with the compiler installed on this machine and "
                     + "reuses until the source changes. Choosing one fills in Model and Pins below.",
            "Model" => "Which device type inside that file to place. A file usually declares one, and "
                     + "then this can be left blank; when it declares several, pick the one you want.",
            "Pins"  => "How many terminals the symbol draws. It is the model's own terminal count, "
                     + "filled in from the file — change it only if you are drawing before choosing one.",
            "OpVars" => "Whether this instance publishes the operating-point variables its model "
                      + "computes — transconductances, capacitances, node temperatures. On by "
                      + "default; turn it off on devices you are not studying to keep a swept "
                      + "result small.",
            _       => "",
        };

    /// <summary>A composed FET's rows (brief-modelcomposer-1, -9, -21).</summary>
    private static string ComposedFetParameterDescription(string parameterName) => parameterName switch
    {
        "File"   => "The .cmodel to place — a model the Model Composer exported.",
        "N"      => "The finger count. Blank is the model's own base finger count; the intrinsic device "
                  + "and each network element scale from it by the rule the model records.",
        "Symbol" => "The symbol drawn: a library symbol's name or a .csym path relative to the .cmodel. "
                  + "Blank is the model's own. Its pins 1, 2 and 3 are g, d and s; a pin 4 is the thermal node t.",
        "Rth"    => "This device's thermal resistance, °C/W, at its finger count. Blank is the model's own, scaled to N. "
                  + "Only a model that self-heats has one.",
        "Cth"    => "This device's thermal capacitance, J/°C, at its finger count. Blank is the model's own, scaled to N. "
                  + "With Rth it sets the thermal time constant Rth·Cth.",
        "Temp"   => "This device's temperature, °C — the baseplate or heatsink it is mounted on. Blank follows the design's "
                  + "temp. A self-heating model's channel rises above it.",
        ComposedSwitches.ChargeName      => "Off: no intrinsic charge at all. The network's capacitors stay.",
        ComposedSwitches.LinearCName     => "On: the intrinsic charge is linearised at the quiescent DC bias point.",
        ComposedSwitches.LinearCgsName   => "On: Cgs is held at its bias-point value; the other capacitances follow the model.",
        ComposedSwitches.LinearCdgName   => "On: Cdg is held at its bias-point value; the other capacitances follow the model.",
        ComposedSwitches.LinearCdsName   => "On: Cds is held at its bias-point value; the other capacitances follow the model.",
        ComposedSwitches.LinearCmName    => "On: the transcapacitance Cm is held at its bias-point value; the others follow the model.",
        ComposedSwitches.GateLagName     => "Off: gate-lag traps follow the voltage, with no lag.",
        ComposedSwitches.DrainLagName    => "Off: drain-lag traps follow the voltage, with no lag.",
        ComposedSwitches.SelfHeatingName => "Off: evaluated at the run's temperature, with no self-heating.",
        ComposedSwitches.DispersionName  => "Off: the drain carries its DC current at every frequency.",
        _        => "",
    };

    /// <summary>A composed FET's switches as a picker (brief-modelcomposer-21). Every BooleanParameter spelling
    /// works in a netlist; the picker offers the two the parameter is seeded with.</summary>
    private static readonly string[] ComposedSwitchOptions = ["true", "false"];

    /// <summary>TLIN's two forms (brief-artsch-2). A placed TLIN carries the angle form's rows; the
    /// physical form's keys are named here because no row of a fresh instance shows them.</summary>
    private static string TlineParameterDescription(SymbolKind kind, string parameterName) => (kind, parameterName) switch
    {
        (SymbolKind.Tline, "Z") => "Characteristic impedance, real.",
        (SymbolKind.Tline, "E") => "Electrical length at F (the angle form). Or state the line physically instead: L, its "
                                 + "length, with Eeff, the effective permittivity (default 1, an air line; below 1 is refused), "
                                 + "so θ(f) = 2π·f·L·√Eeff/c₀. E and L together are refused. The angle form's loss is A, "
                                 + "total dB at F, ∝ f; the physical form's is Ac (conductor, ∝ √f) and Ad (dielectric, ∝ f), "
                                 + "both dB/m at F, both default 0. A with L is refused.",
        (SymbolKind.Tline, "F") => "Reference frequency: where E is stated, and where A, Ac and Ad are given. A physical "
                                 + "line needs it only for Ac or Ad, and is refused without it then.",
        _ => "",
    };

    /// <summary>CPWG's and SLIN's parameters (brief-artsch-1): what each dimension is, and where the
    /// substrate comes from, since none of it is a row.</summary>
    private static string PlanarLineParameterDescription(SymbolKind kind, string parameterName) => (kind, parameterName) switch
    {
        (SymbolKind.Cpwg, "W") => "Centre strip width.",
        (SymbolKind.Cpwg, "G") => "Gap from the strip's edge to the coplanar ground, the same on both sides. The grounds "
                                + "themselves are taken as wide. The substrate height to the backing plane, its εr and loss, "
                                + "and the copper's thickness come from the technology.",
        (SymbolKind.Cpwg, "L") => "Line length.",
        (SymbolKind.Cpwg, "SignalLayer") => "The conductor the strip and its coplanar grounds are on. Empty: the technology's top conductor.",
        (SymbolKind.Cpwg, "GroundReference") => "The plane backing the line. Empty: the nearest ground-designated conductor below the "
                                              + "signal layer, or above it when there is none below.",
        (SymbolKind.GsgPad, "W") => "Width of the line leaving the signal pad. Placed: the 50 Ω width on the technology.",
        (SymbolKind.GsgPad, "L") => "Length of the line, from the signal pad's edge to terminal 2.",
        (SymbolKind.GsgPad, "Pitch") => "Probe pitch: centre of the signal pad to the centre of each ground pad. The probe's, "
                                      + "e.g. 100, 125 or 150 µm.",
        (SymbolKind.GsgPad, "PadW") => "Width of each pad, across the line.",
        (SymbolKind.GsgPad, "PadL") => "Length of the signal pad along the line. A ground pad grows longer when its vias need the room.",
        (SymbolKind.GsgPad, "NVia") => "Ground vias in each ground pad, in a row along it. 0 leaves the ground pads to be "
                                     + "grounded some other way.",
        (SymbolKind.GsgPad, "SignalLayer") => "The conductor the pads and the line are on. Empty: the technology's top conductor.",
        (SymbolKind.GsgPad, "GroundReference") => "The plane under the line, which the ground vias land on. Empty: the nearest "
                                                + "ground-designated conductor below the signal layer.",
        (SymbolKind.Slin, "W") => "Strip width.",
        (SymbolKind.Slin, "L") => "Line length.",
        (SymbolKind.Slin, "SignalLayer") => "The conductor the strip is on. Its planes are the nearest ground-designated conductors "
                                          + "above and below it, and the dielectric between them sets H1, H2, εr and loss. Empty: "
                                          + "the topmost conductor that has a plane on both sides.",
        _ => "",
    };

    /// <summary>The wBond parameters a generic row shows, where the row alone does not say what an
    /// empty value does.</summary>
    private static string WBondParameterDescription(SymbolKind kind, string parameterName)
        => kind is not SymbolKind.WBond ? "" : parameterName switch
        {
            "Temp" => "Checked: the wires are held at this temperature, in °C, in every analysis, and a DC, harmonic-"
                    + "balance or loadpull run reports it as WireTemp. Empty: the temperature stored with the wires — "
                    + "125 °C unless changed. Unchecked: the wires' temperature is SOLVED from each DC and large-signal "
                    + "run's currents, between TempStart and TempEnd, and reported as WireTemp; the impedance is then "
                    + "evaluated at the hotter of those two ends, in every analysis. Each metal's conductivity is read "
                    + "from its conductivity-against-temperature table; a temperature outside the table is held at the "
                    + "table's nearest end, and the run says so in the Messages panel.",
            "TempStart" => "With Temp unchecked: the temperature, in °C, each array's wires are held at where they start, "
                         + "at the array's input pin — typically the die. Default 125 °C.",
            "TempEnd"   => "With Temp unchecked: the temperature, in °C, each array's wires are held at where they end, "
                         + "at the array's output pin — typically the package lead. Default 85 °C.",
            _      => "",
        };

    /// <summary>
    /// What a component's model IS — the closed form it computes and the range that form is stated
    /// over — for the components whose model is an estimate rather than an ideal element
    /// (brief-agent-authoring-overview.md AA-1). Empty for the rest, which is the honest answer for an
    /// ideal R and never a placeholder. The catalogue prints it, so a caller choosing a part sees
    /// where its answer stops being trustworthy before it relies on one.
    /// </summary>
    public static string ModelValidity(SymbolKind kind) => kind switch
    {
        SymbolKind.GsgPad =>
            "MLIN, twice: half the signal pad (PadW wide, PadL/2 long, from the tip to its edge), then the line, "
          + "cascaded, with MLIN's validity. The ground pads are ideal ground at the probe's reference plane; their "
          + "vias are artwork, not circuit. A microstrip, never a coplanar line.",
        SymbolKind.Cpwg =>
            "Quasi-static: Ghione-Naldi's conductor-backed coplanar map with the slot walls' capacitance for the "
          + "copper's thickness, or MLIN's Hammerstad-Jensen where its air capacitance is the larger (a far "
          + "coplanar ground). Against a field solve over W/H 0.19-2.5, G/H 0.3-6, t/G <= 0.18: Z0 within "
          + "2.3 %; eeff within 0.9 % with the ground close, 4.1 % with it far on thick copper. "
          + "Frankel dispersion; Wheeler incremental-inductance conductor loss with roughness; filling-factor "
          + "dielectric loss. Coplanar grounds taken as wide; no lid.",
        SymbolKind.Slin =>
            "Cohn's exact centred stripline with Wheeler's (1978) thickness correction, within 0.03 % of a field "
          + "solve centred; offset as two centred halves in parallel, 1.2 % high at H2/H1 = 2 and 3.4 % at 4. "
          + "TEM: eeff = er. Wheeler incremental-inductance conductor loss with roughness; tanD dielectric loss.",
        SymbolKind.MimCap =>
            "C from the film's permittivity and thickness with Palmer's edge fringing (W, L >= 10 film "
          + "thicknesses); ESR from both plates' sheet resistance with skin effect, Rs·L/(3W) each; "
          + "dielectric loss from the film's tanD; the bottom plate's capacitance through the substrate to "
          + "ground. Lumped while the longer side is under a twentieth of a wavelength in the film.",
        SymbolKind.TestPad =>
            "AN ESTIMATE. The pad's capacitance to the nearest plane on each side, as a disc over a plane (Kirchhoff), "
          + "C = ε0εr·πr²/h + ε0εr·r·[ln(16πr/h) − 1], with h the dielectric between them and the layers combined in "
          + "series; where that plane is in ClearLayers, the pad faces it outside the ClearD hole and the next plane "
          + "through it, the hole's rim calibrated to a field solve; the via's inductance by Goldfarb and Pucel over its length, with the "
          + "barrel's coaxial capacitance to each plane it passes (the clearance as the outer radius) at its point "
          + "along it; with InnerPadD set, each unused pad's rim coupling across its AntipadD clearance to its own plane "
          + "and to the neighbouring planes' antipad edges, less the bare barrel's, calibrated to a field solve. "
          + "Lossless. Every inner conductor counts as a plane under the pad unless ClearLayers names it. "
          + "An EM setup of the pad, its via and its planes is the reference: set Centre to Port to give it a port.",
        SymbolKind.Spiral =>
            "AN ESTIMATE. L by partial-inductance summation (Greenhouse 1974) over the drawn coil, its escape "
          + "and its pad, with their images in the ground plane under the substrate; each conductor is a "
          + "line on its centre, with no current crowding. Against a coarse planar EM extraction of the "
          + "default 2.5-turn coil on the shipped GaAs it read 13 % low (1.50 against 1.72 nH); the EM's "
          + "zero-thickness sheet accounts for about half of that. R from the drawn trace length with skin "
          + "effect; trace-to-ground and crossing capacitance as parallel plates. For a value to rely on, "
          + "EM-extract the drawn coil: circuitrf em --component.",
        SymbolKind.OctSpiral =>
            "AN ESTIMATE. L by partial-inductance summation (Greenhouse 1974) over the drawn coil, its escape "
          + "and its pad, with their images in the ground plane under the substrate; each conductor is a "
          + "line on its centre, with no current crowding. R from the drawn trace length with skin effect; "
          + "trace-to-ground and crossing capacitance as parallel plates. For a value to rely on, EM-extract "
          + "the drawn coil: circuitrf em --component.",
        SymbolKind.Tfr =>
            "R = sheet resistance × L/W from the technology's resistor film; contacts and end overlap not "
          + "included; the film's capacitance to ground split between the ends.",
        SymbolKind.Airbridge =>
            "Series L of a straight ribbon (Grover) and R with skin effect, from landing to landing; the "
          + "crossed line's segment, two bridge widths long, likewise from 3 to 4; the crossing capacitance "
          + "W·Wu over the bridge height, without fringing, between the two midpoints. Valid for a span "
          + "longer than its own width.",
        _ => "",
    };

    /// <summary>The MMIC passives' parameter meanings (AA-1), each with the range its model is
    /// stated over. Only geometry is a parameter: the process numbers come from the technology.</summary>
    private static string MmicParameterDescription(SymbolKind kind, string parameterName) => (kind, parameterName) switch
    {
        (SymbolKind.MimCap, "W") => "Top-plate width, across the feed. C is the film's ε·W·L/t with Palmer's edge "
                                  + "fringing, valid while W and L are each at least ten film thicknesses.",
        (SymbolKind.MimCap, "L") => "Top-plate length, along the feed. The plates' series resistance grows with L/W.",
        (SymbolKind.Spiral, "N") => "Turns. The layout draws whole quarter turns, and the inductance is summed over "
                                  + "exactly the coil it draws — an estimate.",
        (SymbolKind.Spiral, "W") => "Trace width.",
        (SymbolKind.Spiral, "S") => "Spacing between turns.",
        (SymbolKind.Spiral, "Din") => "Inner clear opening across the first pair of sides.",
        (SymbolKind.OctSpiral, "N") => "Turns, from the inner end at the middle of the innermost flat. The layout draws "
                                     + "whole eighth turns, and the inductance is summed over exactly the coil it "
                                     + "draws — an estimate.",
        (SymbolKind.OctSpiral, "W") => "Trace width.",
        (SymbolKind.OctSpiral, "S") => "Spacing between turns.",
        (SymbolKind.OctSpiral, "Din") => "Inner clear opening across the innermost pair of parallel flats.",
        (SymbolKind.Tfr, "W") => "Film width. R is the film's sheet resistance times L/W squares; the contacts "
                               + "and the metal overlap at each end are not included.",
        (SymbolKind.Tfr, "L") => "Film length between the contacts.",
        (SymbolKind.Airbridge, "L") => "Span: the bridge's length between its two landings.",
        (SymbolKind.Airbridge, "W") => "Bridge width.",
        (SymbolKind.Airbridge, "Wu") => "Width of the line crossed. The crossing capacitance is the overlap W·Wu over "
                                      + "the bridge's height, without fringing.",
        _ => "",
    };

    /// <summary>The vias' parameter meanings (brief-via-component.md). Each says what an EMPTY row does,
    /// because empty is the default and it is not the same thing as zero.</summary>
    private static string ViaParameterDescription(SymbolKind kind, string parameterName)
    {
        if (kind is not (SymbolKind.Via or SymbolKind.ViaGnd)) return "";
        return parameterName switch
        {
            "FromLayer"   => "The conductor terminal A lands on. Default: the top conductor, or the highest one "
                           + "a drill to the other end starts on when none joins the top conductor to it.",
            "ToLayer"     => "The conductor terminal B lands on. Default: the farthest conductor a drill reaches "
                           + "from FromLayer that is not a ground plane. A drill that runs on past it leaves a stub, "
                           + "which is modelled.",
            "GroundLayer" => "The ground plane the via lands on. Default: the nearest ground-designated conductor "
                           + "beneath FromLayer.",
            "Drill"       => "The finished hole diameter. Empty: the technology's via drill, else 0.3 mm.",
            "Pad"         => "The pad diameter on each end. Empty: the technology's via pad, else 0.6 mm.",
            "Antipad"     => "The clearance diameter in every ground plane the barrel passes. Empty: the pad "
                           + "plus 0.3 mm.",
            "Plating"     => "The barrel wall thickness. Empty: the via layer's own, else 25 µm. Ignored for a "
                           + "filled via.",
            "IncludeC"    => "Whether the pad-to-plane capacitance is modelled. With it off the via is its "
                           + "barrel's resistance and inductance only.",
            "C"           => "Replaces the computed capacitance outright. Empty: computed from the pad, the "
                           + "antipad and the dielectric, which is an estimate (see the component's help).",
            _             => "",
        };
    }

    /// <summary>The test pad's parameter meanings (brief-dfr26-7). Each says what an EMPTY row does.</summary>
    private static string TestPadParameterDescription(SymbolKind kind, string parameterName)
    {
        if (kind != SymbolKind.TestPad) return "";
        return parameterName switch
        {
            "PadLayer"    => "The conductor the pad is on. Default: the lowest conductor that is not a ground plane.",
            "D"           => "The pad's diameter. The test fixture usually sets it.",
            "Feed"        => "Where terminal 1 joins: Via — through a via at the pad's centre from ViaFrom — or N, E, S "
                           + "or W, a short stub leaving the pad on that side.",
            "FeedW"       => "The side stub's width. Empty: the 50 Ω microstrip width on PadLayer. Not used through a via.",
            "ViaFrom"     => "The layer the trace arrives on: the via starts here, where terminal 1 is, and runs to "
                           + "PadLayer. Default: the top conductor. Not used for a side-fed pad.",
            "Drill"       => "The via's drill. Empty: the technology's via drill.",
            "InnerPadD"   => "The unused pads the via keeps on each conductor it passes (not those in ClearLayers). "
                           + "Empty: none. Board CAD keeps them unless told to remove them, and each adds capacitance.",
            "AntipadD"    => "The clearance round the via in each plane it passes. Empty: the technology's via pad + 0.3 mm.",
            "ClearLayers" => "The conductors whose plane gets a round cut-out under the pad. Empty: none. Through the "
                           + "cut-out the pad faces the next conductor not named here.",
            "ClearD"      => "The cut-out's diameter on those conductors. Empty: D + 0.5 mm. 0, or no wider than "
                           + "AntipadD, cuts nothing: those planes stay planes.",
            "Centre"      => "Open: the pad is metal and nothing more (a test point not in use). Port: the layout also "
                           + "carries an EM port at the pad's centre, an internal port to ground.",
            "PortNum"     => "The EM port's number when Centre is Port. Empty: the next free number.",
            _             => "",
        };
    }

    /// <summary>The physical coupled pairs' parameter meanings (MCLIN, BCLIN, MBCLIN). The broadside
    /// pairs' cross-section rows say what EMPTY does, because empty — follow the stackup — is the
    /// default and is not zero.</summary>
    private static string CoupledPairParameterDescription(SymbolKind kind, string parameterName)
    {
        const string Empty = " Empty: from the stackup between LayerA and LayerB, else the standalone default.";
        return (kind, parameterName) switch
        {
            (SymbolKind.MCLine, "W") => "Width of each strip. Both strips are the same width; the pair is symmetric.",
            (SymbolKind.MCLine, "S") => "Edge-to-edge gap between the strips. Smaller is tighter coupling; the "
                                      + "published model covers S/H from 0.1 to 10.",
            (SymbolKind.MCLine, "L") => "Length of the coupled section. Coupling peaks where it is a quarter "
                                      + "wavelength, between the even and odd modes' own.",
            (SymbolKind.BCLine or SymbolKind.MBCLine, "W") => "Width of each strip (both the same).",
            (SymbolKind.BCLine or SymbolKind.MBCLine, "O") => "Lateral offset between the strips' centres. 0 puts "
                                      + "one directly over the other (tightest coupling); the coupling falls as it grows.",
            (SymbolKind.BCLine or SymbolKind.MBCLine, "L") => "Length of the coupled section.",
            (SymbolKind.BCLine or SymbolKind.MBCLine, "Hs") => "Separation between the strips' facing surfaces." + Empty,
            (SymbolKind.BCLine, "Ha") => "Strip A's top surface to the upper ground plane." + Empty,
            (SymbolKind.BCLine or SymbolKind.MBCLine, "Hb") => "Strip B's bottom surface to the lower ground plane." + Empty,
            (SymbolKind.MBCLine, "Hd") => "Dielectric thickness over the ground. Empty: strip A sits on the top "
                                      + "surface (Hd = Hb + T + Hs), unless the stackup puts dielectric over it.",
            (SymbolKind.BCLine or SymbolKind.MBCLine, "T") => "Strip thickness (both strips). Empty: each strip "
                                      + "takes its own conductor's thickness from the stackup, else 18 µm.",
            (SymbolKind.BCLine or SymbolKind.MBCLine, "Er") => "Relative permittivity of the dielectric (one value: "
                                      + "a stack of several is thickness-averaged)." + Empty,
            (SymbolKind.BCLine or SymbolKind.MBCLine, BroadsideSubstrateInjection.LayerAParam) =>
                "The conductor strip A (pins 1 and 2) is drawn on. Default: the first conductor with a signal "
              + "conductor directly beneath it" + (kind == SymbolKind.BCLine ? " and ground planes above and below." : " and a ground plane below."),
            (SymbolKind.BCLine or SymbolKind.MBCLine, BroadsideSubstrateInjection.LayerBParam) =>
                "The conductor strip B (pins 3 and 4) is drawn on. Default: the next conductor below LayerA.",
            _ => "",
        };
    }

    /// <summary>
    /// The mixer's parameter meanings. Kept out of the switch above because it is the one component
    /// whose non-idealities are OFF by default at a large number rather than at zero, and a reader
    /// meeting "200 dB" in a table needs to be told that is the ideal case rather than a claim.
    /// </summary>
    /// <summary>
    /// The duplexer's parameter meanings, which are the FILTER's twice over.
    ///
    /// <para>Written as a prefix strip rather than as forty switch arms, because that is what the
    /// component is: two complete filter specifications and one shared antenna impedance. Twenty
    /// hand-copied sentences would drift from the filter's own the first time one of them was
    /// improved, and the drift would be invisible — each half would still read correctly on its
    /// own.</para>
    /// </summary>
    private static string DuplexerParameterDescription(string parameterName)
    {
        if (parameterName.Equals("Zant", StringComparison.Ordinal))
            return "The impedance the shared antenna port PRESENTS - the arms' own Zin, under a "
                 + "shorter name, so a complex value is conjugate-matched by a Term at its "
                 + "conjugate. The TX and RX arms both look into it, which is what makes them "
                 + "interact at all.";

        string arm = parameterName.StartsWith("Tx", StringComparison.Ordinal) ? "TX"
                   : parameterName.StartsWith("Rx", StringComparison.Ordinal) ? "RX"
                   : "";
        if (arm.Length == 0) return "";

        string rest = parameterName[2..];

        // TxZ / RxZ are the arm's own port impedance; the filter spells that Zout, since from the
        // arm's point of view the antenna is port 1 and the transceiver side is port 2.
        string filterName = rest.Equals("Z", StringComparison.Ordinal) ? "Zout" : rest;
        string body = SystemBlockParameterDescription(SymbolKind.Filter, filterName);
        if (body.Length == 0) return "";

        // The isolation between the two arms is deliberately NOT a parameter — it is what these two
        // responses and the shared node produce. Said on every row rather than nowhere, because the
        // field a user looks for is the one that is not here.
        return $"{arm} arm. {body} There is no isolation parameter: the TX-to-RX isolation is what "
             + "these two responses and the antenna junction produce between them.";
    }

    /// <summary>
    /// The system blocks' parameter meanings (brief-sys-1 onwards, as each block gains its model).
    /// Three of these choose which variant of a DYNAMIC glyph is drawn, and a reader meeting
    /// <c>Direction</c> or <c>State</c> in a parameter table needs to be told it changes the
    /// PICTURE, because that is unusual here and it is the whole reason those components draw
    /// themselves the way they do. The rest are ordinary electrical parameters, and the ones that
    /// are OFF at a large number rather than at zero say so, because "200 dB" in a table reads as a
    /// claim rather than as the ideal case unless it is spelled out.
    /// </summary>
    private static string SystemBlockParameterDescription(SymbolKind kind, string parameterName)
        => (kind, parameterName) switch
    {
        (SymbolKind.Circulator, "Direction") =>
            "Which way the circulator turns: CW circulates port 1 to 2 to 3 to 1, CCW reverses it. "
          + "The arrow drawn inside the symbol follows this.",
        (SymbolKind.Circulator, "IL") =>
            "Insertion loss along the forward path, as a positive number of dB.",
        (SymbolKind.Circulator, "Isolation") =>
            "How far below the signal the leakage the WRONG way round the circle sits. The 200 dB "
          + "default means none, and the entry is not stamped at all.",
        (SymbolKind.Circulator, "RL") =>
            "Return loss at every port that does not state its own VSWR. 200 dB means exactly "
          + "matched.",
        (SymbolKind.Circulator, "Z0") =>
            "REFERENCE impedance of all three ports, real or complex - what S is defined against, "
          + "unlike a Zin/Zout, which names what a port presents. It is NOT the way to detune the "
          + "match: use VSWR1/Ang1, which set port 1's own reflection rather than the reference "
          + "every port shares.",
        (SymbolKind.Circulator, "VSWR1" or "VSWR2" or "VSWR3") =>
            "Voltage standing-wave ratio at this port, with the other two matched - a real "
          + "circulator is badly matched and this is how far. 1 means the port does not state one "
          + "and falls back to RL. Pair it with the matching Ang: the SAME VSWR at a different "
          + "angle is a completely different load to whatever is connected here.",
        (SymbolKind.Circulator, "Ang1" or "Ang2" or "Ang3") =>
            "Angle of this port's reflection coefficient, in degrees. Read only when the matching "
          + "VSWR states a mismatch. Frequency-flat: it is the mismatch you want to test against, "
          + "not a rotating one.",
        (SymbolKind.Balun, "Zunb") =>
            "Reference impedance of the unbalanced port.",
        (SymbolKind.Balun, "Zbal") =>
            "Reference impedance of EACH balanced port to ground, so the differential impedance "
          + "across BAL+ and BAL− is twice this. The 50/50 default is the ordinary 1:2 balun.",
        (SymbolKind.Balun, "IL") =>
            "Insertion loss from the unbalanced port to the balanced pair, as a positive number "
          + "of dB.",
        (SymbolKind.Balun, "AmpImb") =>
            "How far apart in level the two balanced outputs sit, in dB. 0 is a perfect split; the "
          + "imbalance is applied symmetrically, half up on one output and half down on the other.",
        (SymbolKind.Balun, "PhaseImb") =>
            "Departure from 180°, in degrees. 0 gives exactly antiphase outputs.",
        (SymbolKind.Coupler or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "Coupling") =>
            "How far below the input the coupled port sits, in dB. This alone sets the split — the "
          + "through port gets whatever is left, so a 20 dB coupler already loses 0.04 dB through "
          + "its main arm. 3.0103 dB is the equal split that makes a hybrid.",
        (SymbolKind.Coupler or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "Phase") =>
            "Phase of the coupled port relative to the through port, in degrees — 90 for a "
          + "quadrature hybrid, 180 for an anti-phase one. This block holds it at EVERY frequency, "
          + "which no real coupler does; build one from four quarter-wave TLIN arms if you need "
          + "the bandwidth to be real.",
        (SymbolKind.Coupler or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "Directivity") =>
            "How far below the COUPLED port the isolated port sits, in dB. The 200 dB default means "
          + "the isolated port is exactly isolated — no entry is stamped at all.",
        (SymbolKind.Coupler or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "IL") =>
            "Loss ADDED on top of the split, as a positive number of dB. It is not a substitute for "
          + "the split: an ideal coupler's main-arm loss is already in the Coupling arithmetic.",
        (SymbolKind.Coupler or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "RL") =>
            "Return loss at every port. 200 dB means all four ports are exactly matched.",
        (SymbolKind.Coupler or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "Z0") =>
            "Reference impedance of all four ports. Each port is this resistance to its own "
          + "reference.",
        (SymbolKind.OpAmp or SymbolKind.OpAmp5, "A") =>
            "Open-loop voltage gain. 0, the default, is the ideal op-amp: infinite gain, so the "
          + "inputs are held at the same voltage and the feedback network alone sets the gain.",
        (SymbolKind.OpAmp or SymbolKind.OpAmp5, "Rout") =>
            "Output resistance. It acts only with a finite A; the ideal op-amp has none to act against.",
        (SymbolKind.OpAmp or SymbolKind.OpAmp5, "Pins") =>
            "3 for the tile without supply pins, 5 for the one with them. Fixed by the tile — place "
          + "the other one to get the other count. The supply pins carry no current.",
        (SymbolKind.Transformer, "N") =>
            "Turns ratio, primary to secondary: V(primary) = N × V(secondary), and the currents in "
          + "the opposite ratio. Ideal — lossless at every frequency, DC included.",
        (SymbolKind.Switch, "State") =>
            "1 closes the switch, 0 opens it. The symbol is drawn in the position it is set to, "
          + "so a swept State reads off the schematic.",
        (SymbolKind.SwitchD, "State") =>
            "Which throw the common port is connected to — 1 or 2, or 0 for both open. The blade "
          + "in the symbol points at it.",
        (SymbolKind.Switch or SymbolKind.SwitchD, "Throws") =>
            "How many throws the switch has: 1 for the SPST tile, 2 for the SPDT. It is what makes "
          + "the two tiles one component, and it is fixed by the tile — place the other one to get "
          + "the other throw count.",
        (SymbolKind.Switch or SymbolKind.SwitchD, "IL") =>
            "Insertion loss of the path the switch is making, as a positive number of dB.",
        (SymbolKind.Switch or SymbolKind.SwitchD, "Isolation") =>
            "How far below the signal the leakage past an OPEN throw sits. The 200 dB default means "
          + "none — the ideal switch leaks nothing, and the entry is not stamped at all.",
        (SymbolKind.Switch or SymbolKind.SwitchD, "OffState") =>
            "What an open throw looks like from its own port: Reflective is an open circuit, "
          + "Absorptive is a matched termination. Reflective is what a series switch does.",
        (SymbolKind.Switch or SymbolKind.SwitchD, "Z0") =>
            "Reference impedance of every port. Each port is this resistance to its own reference.",
        (SymbolKind.Switch or SymbolKind.SwitchD, "RL") =>
            "Return loss of the closed path. 200 dB means the closed switch is exactly matched.",
        (SymbolKind.Filter, "Form") =>
            "Lowpass, Highpass, Bandpass or Bandstop. The symbol strikes a line through every wave "
          + "the network blocks, so the shape is read off the glyph. Lowpass and Highpass read Fc; "
          + "Bandpass and Bandstop read F1 and F2. For a Bandstop those two edges are the STOPBAND.",
        (SymbolKind.Filter, "Response") =>
            "Which prototype family the response comes from: Butterworth (maximally flat "
          + "magnitude), Chebyshev (equiripple passband, reads Ripple), InvChebyshev (flat "
          + "passband and an equiripple stopband floor, reads Astop), Bessel (maximally flat GROUP "
          + "DELAY — the Bessel-Thomson response, chosen for its phase), Gaussian (a Gaussian "
          + "magnitude, −3.01 dB at the cutoff) or Elliptic (equiripple in both bands, reads Ripple "
          + "and Astop). A parameter the family does not read is ignored, so you never have to "
          + "clear a field to change family.",
        (SymbolKind.Filter, "Order") =>
            "The PROTOTYPE order. A Bandpass or Bandstop transformation doubles the degree, so "
          + "Order = 3 as a bandpass or a bandstop is a 6th-degree network — both conventions exist "
          + "in the wild, and this one is the prototype's. The stopband slope of an all-pole family "
          + "is 20 x Order dB per decade; InvChebyshev and Elliptic level off at their own floor "
          + "instead.",
        (SymbolKind.Filter, "Fc") =>
            "The cutoff, for Lowpass and Highpass. For Chebyshev and Elliptic it is the RIPPLE "
          + "bandwidth edge (where the response leaves the ripple band), for Butterworth and "
          + "Gaussian the 3.01 dB point, for InvChebyshev the STOPBAND edge — where Astop is first "
          + "met — and for Bessel the reciprocal of the group delay. Ignored for Bandpass and "
          + "Bandstop.",
        (SymbolKind.Filter, "F1") =>
            "Lower edge, for Bandpass and Bandstop. On a Bandpass it is the passband edge; on a "
          + "Bandstop it is the stopband edge. The centre is the GEOMETRIC mean of F1 and F2 and "
          + "the response is geometrically symmetric about it. Ignored for Lowpass and Highpass.",
        (SymbolKind.Filter, "F2") =>
            "Upper edge, for Bandpass and Bandstop. Read with F1 — the two set both the centre and "
          + "the width. Ignored for Lowpass and Highpass.",
        (SymbolKind.Filter, "Ripple") =>
            "Passband ripple, in dB, for Chebyshev and Elliptic. The passband swings between 0 and "
          + "-Ripple exactly this many times, which is what the order buys. Ignored by the other "
          + "three families.",
        (SymbolKind.Filter, "Astop") =>
            "Stopband floor, in dB below the passband, for InvChebyshev and Elliptic. The stopband "
          + "is equiripple AT this level rather than falling away past it — that is the trade those "
          + "two families make for a sharper transition. Ignored by the other three families.",
        (SymbolKind.Filter, "Zin") =>
            "The impedance port 1 PRESENTS - so a complex value is conjugate-matched by a Term at "
          + "its conjugate: Zin = 5+j100 wants Z = 5-j100 across it for maximum power transfer. "
          + "This block is stamped as its scattering matrix rather than synthesised as a ladder, so "
          + "Zin and Zout may differ freely: the filter is then also an ideal lossless impedance "
          + "transformer, matched at BOTH ports in its passband. Measured in a uniform 50 ohm "
          + "system an unequal pair shows the transformer's mismatch, which is the answer and not a "
          + "fault.",
        (SymbolKind.Filter, "Zout") =>
            "The impedance port 2 PRESENTS. See Zin — the two are independent, and an unequal pair "
          + "is a lossless transformer as well as a filter.",
        (SymbolKind.Filter, "IL") =>
            "A flat insertion loss laid on top of the ideal response, in dB. It multiplies S21 and "
          + "leaves S11 alone, so the block genuinely dissipates rather than reflecting what it "
          + "loses — which is what a real filter's loss does. 0 is the lossless ideal.",
        (SymbolKind.Amp, "Gain") =>
            "Small-signal gain from the input port to the output port. Shown beside the symbol, "
          + "because a triangle with a number inside it stops being readable at three digits.",
        (SymbolKind.Amp, "IP3") =>
            "Third-order intercept, referred to whichever port IP3Ref names. It is the amplifier's "
          + "ONE nonlinearity, so it sets IM3 and compression together: the 1 dB compression point "
          + "follows at IIP3 - 8.96 dB and is not separately adjustable. The 200 dBm default means "
          + "the amplifier is exactly linear and never compresses.",
        (SymbolKind.Amp, "IP3Ref") =>
            "Whether IP3 above is an input-referred number or an output-referred one. OIP3 = IIP3 "
          + "+ Gain is an identity, so this is one field and a reference rather than two fields "
          + "that could disagree. Output is the default, because that is the form a power "
          + "amplifier's datasheet quotes.",
        (SymbolKind.Amp, "Zin") =>
            "The impedance the input port PRESENTS, and what RLin is measured against. A complex "
          + "value is conjugate-matched by a Term at its conjugate, and is accepted only while the "
          + "amplifier is LINEAR - set IP3 to 200, since the tile's own default of 40 dBm is not.",
        (SymbolKind.Amp, "Zout") =>
            "The impedance the output port PRESENTS, and what RLout is measured against. See Zin "
          + "for the complex case.",
        (SymbolKind.Amp, "RLin") =>
            "Input return loss. The 200 dB default means exactly matched - no reflection entry is "
          + "stamped at all. The gain you typed is what you measure at any value of it.",
        (SymbolKind.Amp, "RLout") =>
            "Output return loss. 200 dB means exactly matched.",
        (SymbolKind.Amp, "S12") =>
            "Reverse isolation. The 200 dB default means the amplifier is unilateral - the reverse "
          + "path is absent, not small, which is what makes an ideal amplifier unconditionally "
          + "stable. Setting it is what makes stability a question at all.",
        (SymbolKind.Atten, "Loss") =>
            "How far the attenuator knocks the signal down, as a positive number of dB. 0 dB is an "
          + "ideal through, which is a legitimate thing to place.",
        (SymbolKind.Atten, "Z0") =>
            "Reference impedance of both ports. Each port is this resistance to its own reference.",
        (SymbolKind.Atten, "RL") =>
            "Return loss of both ports. The 200 dB default means exactly matched — no reflection "
          + "entry is stamped at all.",

        (SymbolKind.CLine, "Ze") =>
            "Even-mode characteristic impedance, in ohms: what each line looks like when both are "
          + "driven in phase. Never below Zo. The pair is matched to Z0 when Ze x Zo = Z0 squared.",
        (SymbolKind.CLine, "Zo") =>
            "Odd-mode characteristic impedance, in ohms: what each line looks like when the two are "
          + "driven in antiphase. At a quarter wave the coupling is (Ze - Zo) / (Ze + Zo).",
        (SymbolKind.CLine, "E") =>
            "Electrical length at F, in degrees — the even mode's, and the odd mode's too unless Eo "
          + "is set. 90 is a quarter wave, where the coupling peaks.",
        (SymbolKind.CLine, "F") =>
            "The frequency E (and Eo) are quoted at. Both scale in proportion to frequency.",
        (SymbolKind.CLine, "Eo") =>
            "Odd-mode electrical length at F, in degrees. Leave it blank for a homogeneous medium "
          + "(stripline), where both modes travel together; set it to model an inhomogeneous one such "
          + "as microstrip, where the odd mode is faster and so electrically shorter.",

        // PIM and PIMPc are one specification in two fields and are worded as a pair, because
        // either number alone says nothing: a product level means nothing without the carriers it
        // was measured against. They read the same on all three blocks that can carry them.
        (SymbolKind.Atten or SymbolKind.Circulator or SymbolKind.Coupler
                          or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "PIM") =>
            "Passive intermod: the third-order product this block puts on its output when two "
          + "carriers of PIMPc each drive its input, as an absolute level. The -200 dBm default "
          + "means there is no intermod here and none is calculated at all. A part quoted in dBc "
          + "converts by adding PIMPc — -153 dBc at +43 dBm is -110 dBm.",
        (SymbolKind.Atten or SymbolKind.Circulator or SymbolKind.Coupler
                          or SymbolKind.Hybrid90 or SymbolKind.Hybrid180, "PIMPc") =>
            "Power per carrier the PIM figure above was measured at — the second half of one "
          + "specification. Two carriers at this power produce a product at exactly PIM; the "
          + "product then rides the third power of drive, so 10 dB less carrier is 30 dB less "
          + "product.",
        _ => "",
    };

    private static string MixerParameterDescription(string parameterName) => parameterName switch
    {
        "ConvGain" => "Single-sideband power conversion gain, RF port to IF port, at the LO drive "
                    + "Plo names. Negative is a loss. Both sidebands are produced at this level.",
        "Plo"      => "The LO power the gain above holds at. Mixing is a product, so conversion "
                    + "gain tracks LO amplitude: drive the LO harder than this and you get more.",
        "Zrf"      => "RF port impedance. The port is this resistance to its own reference.",
        "Zlo"      => "LO port impedance.",
        "Zif"      => "IF port impedance — also the source resistance the IF output sits behind.",
        "IsoLO_RF" => "LO-to-RF isolation: how far below the LO its leakage at the RF port sits. "
                    + "The 200 dB default means none — the ideal mixer leaks nothing.",
        "IsoLO_IF" => "LO-to-IF isolation, the LO feedthrough a real mixer shows at its IF port. "
                    + "200 dB means none.",
        "IsoRF_IF" => "RF-to-IF isolation: straight-through RF feedthrough, unconverted. "
                    + "200 dB means none.",
        "IIP3"     => "Input-referred third-order intercept at the RF port, which sets both "
                    + "compression and IM3. The 100 dBm default means the RF path is exactly "
                    + "linear and the mixer never compresses.",
        _          => "",
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
            // SRLC / PRLC carry all three, all shown: the reason to place one instead of a plain C
            // is that the R and the L are the interesting numbers, so hiding either would defeat it.
            case SymbolKind.Srlc:
            case SymbolKind.Prlc:      return [new("R",   "1", "Ω",   true, UnitDimension.Resistance),
                                               new("L",   "1", "nH",  true, UnitDimension.Inductance),
                                               new("C",   "1", "pF",  true, UnitDimension.Capacitance)];
            // The two-element members carry EXACTLY the values they have — the whole reason to
            // place one rather than an SRLC with a number nobody meant. Same defaults and same
            // units as the three-element pair, so a swap between them reads the same.
            case SymbolKind.Srl:
            case SymbolKind.Prl:       return [new("R",   "1", "Ω",   true, UnitDimension.Resistance),
                                               new("L",   "1", "nH",  true, UnitDimension.Inductance)];
            case SymbolKind.Src:
            case SymbolKind.Prc:       return [new("R",   "1", "Ω",   true, UnitDimension.Resistance),
                                               new("C",   "1", "pF",  true, UnitDimension.Capacitance)];
            case SymbolKind.Slc:
            case SymbolKind.Plc:       return [new("L",   "1", "nH",  true, UnitDimension.Inductance),
                                               new("C",   "1", "pF",  true, UnitDimension.Capacitance)];
            case SymbolKind.Vdc:       return [new("Vdc", "0", "V",   true, UnitDimension.Voltage)];
            // V and Freq match V_1Tone factory keys (V= amplitude, Freq= frequency in Hz).
            // Vdc (hidden) provides a DC bias offset on the tone source.
            //
            // `Phase` is SEEDED, hidden, and carries its `deg` unit, for a reason that is not
            // cosmetic: an angle parameter reaches the model in RADIANS (the Elaborator applies the
            // unit — TLIN's `E` convention), so a Phase row a user adds BY HAND and leaves unitless
            // would silently mean radians. Seeding it with the unit already on it removes that from
            // the list of things anyone has to know. It matches what P1Tone and PnTone already seed,
            // and what UserParamTemplate hands every ADDED tone of a multi-tone source.
            case SymbolKind.ToneSource: return [new("V",     "1", "V",   true,  UnitDimension.Voltage),
                                                new("Freq",  "1", "GHz", true,  UnitDimension.Frequency),
                                                new("Phase", "0", "deg", false, UnitDimension.Angle),
                                                new("Vdc",   "0", "V",   false, UnitDimension.Voltage)];
            // ITone mirrors ToneSource exactly, with I/Idc for V/Vdc — I and Freq match the
            // I_1Tone factory keys, Idc (hidden) is the DC offset.
            case SymbolKind.CurrentToneSource:
                                        return [new("I",     "1", "mA",  true,  UnitDimension.Current),
                                                new("Freq",  "1", "GHz", true,  UnitDimension.Frequency),
                                                new("Phase", "0", "deg", false, UnitDimension.Angle),
                                                new("Idc",   "0", "mA",  false, UnitDimension.Current)];
            // VCCS: one parameter, the transconductance. I = G·(V(ctrl+) − V(ctrl−)).
            case SymbolKind.Vccs:       return [new("G", "10", "mS", true, UnitDimension.Conductance)];
            // VCVS: one parameter, the voltage gain. V(out+) − V(out−) = E·(V(ctrl+) − V(ctrl−)).
            // Dimensionless, so it carries no unit at all rather than a blank one.
            case SymbolKind.Vcvs:       return [new("E", "1", "", true, UnitDimension.None)];
            // Op-amp / OpAmp5: A=0 is the ideal op-amp (infinite gain — see OpAmpModel for why zero
            // spells it), Rout the output resistance, and Pins the tile's own fixed count.
            case SymbolKind.OpAmp:      return [new("A",    "0", "",  false, UnitDimension.None),
                                                new("Rout", "0", "Ω", false, UnitDimension.Resistance),
                                                new("Pins", "3", "",  false, UnitDimension.None)];
            case SymbolKind.OpAmp5:     return [new("A",    "0", "",  false, UnitDimension.None),
                                                new("Rout", "0", "Ω", false, UnitDimension.Resistance),
                                                new("Pins", "5", "",  false, UnitDimension.None)];
            // Transformer: the turns ratio, primary to secondary.
            case SymbolKind.Transformer: return [new("N", "1", "", true, UnitDimension.None)];
            // Mixer / MixerD: identical parameter list, because they are the same component.
            //
            // The first two are the whole of the ideal device and they belong together: the mixing
            // law is a PRODUCT, so its conversion gain scales with LO amplitude, and a gain quoted
            // without the LO drive it holds at would be a number with no meaning. Quoting the pair
            // is also how a real part is specified, so `ConvGain` = −7 dB at `Plo` = +7 dBm reads
            // off a datasheet unchanged. Both show on the schematic; nothing else does, because a
            // mixer wearing eleven labels is unreadable and the rest are ideal by default anyway.
            //
            // The three isolations and IIP3 default to numbers so large they mean "off" — 200 dB
            // and 100 dBm. They are honest numbers rather than sentinels, and MixerModel snaps
            // each to EXACTLY ideal above its own threshold, so a freshly-placed mixer stamps no
            // leakage terms at all rather than 1e-10 of one.
            // ── System blocks (brief-sys-1) ───────────────────────────────────
            // ONLY the parameters the GLYPH reads, plus the one number each of the two blocks whose
            // artwork was specified around a label actually shows there. Nothing here is read by an
            // engine — none of these components exists yet — and the electrical parameter lists
            // arrive with the models, in SYS-2 onwards. Declaring a full datasheet's worth of rows
            // now would be inventing the model's interface a brief early, and every one of them
            // would silently do nothing.
            //
            // Direction / State / Form are DISPLAY parameters in the same sense SnP's PinConfig and
            // Match's Form are: they choose which cached glyph variant is drawn. They are hidden
            // from the schematic labels because the glyph already says what they say — a switch
            // drawn open does not also need to be captioned "Off".
            // Circulator: Direction chooses the glyph AND the direction energy goes, so the picture
            // and the stamp cannot disagree. Only it and the loss show on the schematic — a
            // circulator wearing five labels is unreadable, and the rest are ideal by default.
            // VSWR1..3 / Ang1..3 detune each port's own match in MAGNITUDE and PHASE, which is what
            // a power amplifier on port 1 actually feels and what RL alone cannot say. All six are
            // seeded at their ideal values, and VSWR = 1 means "not stated" so that port keeps
            // falling back to RL - a user who never opens these sees exactly what they saw before.
            // Ang carries its `deg` unit for the reason ToneSource's Phase does: an angle reaches
            // the model in RADIANS, so a row added by hand and left unitless would silently mean
            // radians.
            case SymbolKind.Circulator:
                return [new("Direction", "CW",   "",    false, UnitDimension.None),
                        new("IL",        "0",    "dB",  false, UnitDimension.None),
                        new("Isolation", "200",  "dB",  false, UnitDimension.None),
                        new("RL",        "200",  "dB",  false, UnitDimension.None),
                        new("Z0",        "50",   "Ω",   false, UnitDimension.Resistance),
                        new("VSWR1",     "1",    "",    false, UnitDimension.None),
                        new("Ang1",      "0",    "deg", false, UnitDimension.Angle),
                        new("VSWR2",     "1",    "",    false, UnitDimension.None),
                        new("Ang2",      "0",    "deg", false, UnitDimension.Angle),
                        new("VSWR3",     "1",    "",    false, UnitDimension.None),
                        new("Ang3",      "0",    "deg", false, UnitDimension.Angle),
                        new("PIM",       "-200", "dBm", false, UnitDimension.Power),
                        new("PIMPc",     "43",   "dBm", false, UnitDimension.Power)];
            // Balun: the three-port ground-referenced form (brief-sys-3 D3). Zbal is the impedance
            // of EACH balanced port to ground, so the 50/50 defaults are the ordinary 1:2 balun —
            // 100 Ω differential presenting 50 Ω single-ended.
            case SymbolKind.Balun:
                return [new("Zunb",     "50", "Ω",   false, UnitDimension.Resistance),
                        new("Zbal",     "50", "Ω",   false, UnitDimension.Resistance),
                        new("IL",       "0",  "dB",  false, UnitDimension.None),
                        new("AmpImb",   "0",  "dB",  false, UnitDimension.None),
                        new("PhaseImb", "0",  "deg", false, UnitDimension.Angle)];
            // Coupler / Hybrid90 / Hybrid180: ONE engine component ("Coupler"), three tiles,
            // differing only in Coupling and Phase — the Mixer/MixerD and Switch/SwitchD
            // arrangement. 3.0103 dB is the equal split written to the precision that makes
            // c = t = 1/√2; a hybrid is a 3 dB coupler and nothing else.
            //
            // Coupling shows on the schematic for the directional coupler, because it is the number
            // that instance is ABOUT and the tile is drawn around it. The hybrids do not show it:
            // their split is in the tile's own name, and a "Hybrid90" captioned "3.0103 dB" is two
            // ways of saying the same thing on one drawing.
            case SymbolKind.Coupler:
                return [new("Coupling",    "20",   "dB",  true,  UnitDimension.None),
                        new("Phase",       "90",   "deg", false, UnitDimension.Angle),
                        new("Directivity", "200",  "dB",  false, UnitDimension.None),
                        new("IL",          "0",    "dB",  false, UnitDimension.None),
                        new("RL",          "200",  "dB",  false, UnitDimension.None),
                        new("Z0",          "50",   "Ω",   false, UnitDimension.Resistance),
                        new("PIM",         "-200", "dBm", false, UnitDimension.Power),
                        new("PIMPc",       "43",   "dBm", false, UnitDimension.Power)];
            case SymbolKind.Hybrid90:
            case SymbolKind.Hybrid180:
                return [new("Coupling",    "3.0103", "dB",  false, UnitDimension.None),
                        new("Phase",       kind == SymbolKind.Hybrid90 ? "90" : "180",
                                                     "deg", false, UnitDimension.Angle),
                        new("Directivity", "200",    "dB",  false, UnitDimension.None),
                        new("IL",          "0",      "dB",  false, UnitDimension.None),
                        new("RL",          "200",    "dB",  false, UnitDimension.None),
                        new("Z0",          "50",     "Ω",   false, UnitDimension.Resistance),
                        new("PIM",         "-200",   "dBm", false, UnitDimension.Power),
                        new("PIMPc",       "43",     "dBm", false, UnitDimension.Power)];
            // Switch / SwitchD: ONE engine component ("Switch"), two tiles, differing only in
            // `Throws` — the Mixer/MixerD arrangement, and the same reason. `State` names which
            // throw is CLOSED, so 0 opens the lot and 1 is the SPST's only throw; it is a plain
            // number precisely so a parametric sweep over it gives every switch position in one
            // run, with the glyph following along. See SwitchState/SwitchThrow for why both enums
            // are numbered to match it.
            //
            // `State` SHOWS on the schematic (owner, 2026-09-13). It was hidden on the reading that
            // the blade already draws the position, which is true of a switch sitting at a literal —
            // but a State driven by a variable or a sweep is a caption the picture cannot carry, and
            // the label is also the only place such a value can be TYPED now that the properties
            // dialog offers a closed picker (NamedParamOptions).
            case SymbolKind.Switch:
                return [new("State",     "1",           "",   true,  UnitDimension.None),
                        new("Throws",    "1",           "",   false, UnitDimension.None),
                        new("IL",        "0",           "dB", false, UnitDimension.None),
                        new("Isolation", "200",         "dB", false, UnitDimension.None),
                        new("OffState",  "Reflective",  "",   false, UnitDimension.None),
                        new("Z0",        "50",          "Ω",  false, UnitDimension.Resistance),
                        new("RL",        "200",         "dB", false, UnitDimension.None)];
            case SymbolKind.SwitchD:
                return [new("State",     "1",           "",   true,  UnitDimension.None),
                        new("Throws",    "2",           "",   false, UnitDimension.None),
                        new("IL",        "0",           "dB", false, UnitDimension.None),
                        new("Isolation", "200",         "dB", false, UnitDimension.None),
                        new("OffState",  "Reflective",  "",   false, UnitDimension.None),
                        new("Z0",        "50",          "Ω",  false, UnitDimension.Resistance),
                        new("RL",        "200",         "dB", false, UnitDimension.None)];
            // The filter (brief-sys-6). NOTHING shows on the schematic, which is the same choice
            // Match makes with the same glyph: the picture already says lowpass/bandpass/highpass,
            // and the band itself cannot be captioned in one line because Fc and F1/F2 are
            // alternatives — whichever pair the Form does not read would be a caption stating a
            // frequency the filter is not at.
            //
            // Ripple and Astop are declared unconditionally even though no single Response reads
            // both: a user switching Chebyshev to Butterworth must not have to clear a field, so a
            // parameter the family does not read is IGNORED rather than refused (FilterModel).
            case SymbolKind.Filter:
                return [new("Response", "Chebyshev", "",   false, UnitDimension.None),
                        new("Form",     "Bandpass",  "",   false, UnitDimension.None),
                        new("Order",    "3",         "",   false, UnitDimension.None),
                        new("Fc",       "1",         "GHz", false, UnitDimension.Frequency),
                        new("F1",       "0.9",       "GHz", false, UnitDimension.Frequency),
                        new("F2",       "1.1",       "GHz", false, UnitDimension.Frequency),
                        new("Ripple",   "0.1",       "dB",  false, UnitDimension.None),
                        new("Astop",    "40",        "dB",  false, UnitDimension.None),
                        new("Zin",      "50",        "Ω",   false, UnitDimension.Resistance),
                        new("Zout",     "50",        "Ω",   false, UnitDimension.Resistance),
                        new("IL",       "0",         "dB",  false, UnitDimension.None)];

            // The duplexer is two complete filter specifications plus one shared antenna impedance,
            // and deliberately NO Isolation: the isolation a duplexer achieves is what its two
            // responses and their junction produce, and a user who typed one would be overriding
            // physics with a number (DuplexerModel).
            //
            // The two default bands do not overlap and leave a gap, so a freshly placed duplexer is
            // a working one rather than two filters fighting over the same frequency.
            case SymbolKind.Duplexer:
                return [new("Zant",       "50",        "Ω",  false, UnitDimension.Resistance),
                        new("TxResponse", "Chebyshev", "",   false, UnitDimension.None),
                        new("TxForm",     "Bandpass",  "",   false, UnitDimension.None),
                        new("TxOrder",    "3",         "",   false, UnitDimension.None),
                        new("TxFc",       "1",         "GHz", false, UnitDimension.Frequency),
                        new("TxF1",       "0.9",       "GHz", false, UnitDimension.Frequency),
                        new("TxF2",       "1",         "GHz", false, UnitDimension.Frequency),
                        new("TxRipple",   "0.1",       "dB",  false, UnitDimension.None),
                        new("TxAstop",    "40",        "dB",  false, UnitDimension.None),
                        new("TxZ",        "50",        "Ω",  false, UnitDimension.Resistance),
                        new("TxIL",       "0",         "dB",  false, UnitDimension.None),
                        new("RxResponse", "Chebyshev", "",   false, UnitDimension.None),
                        new("RxForm",     "Bandpass",  "",   false, UnitDimension.None),
                        new("RxOrder",    "3",         "",   false, UnitDimension.None),
                        new("RxFc",       "1",         "GHz", false, UnitDimension.Frequency),
                        new("RxF1",       "1.1",       "GHz", false, UnitDimension.Frequency),
                        new("RxF2",       "1.2",       "GHz", false, UnitDimension.Frequency),
                        new("RxRipple",   "0.1",       "dB",  false, UnitDimension.None),
                        new("RxAstop",    "40",        "dB",  false, UnitDimension.None),
                        new("RxZ",        "50",        "Ω",  false, UnitDimension.Resistance),
                        new("RxIL",       "0",         "dB",  false, UnitDimension.None)];
            // The amplifier's triangle and the attenuator's bowtie are both drawn EMPTY on purpose,
            // with the number they are about shown as the label underneath — so these two tiles ship
            // that label from the moment they are placed. The amplifier shows TWO, because gain and
            // intercept are the pair a system diagram is read for and neither is in the picture.
            //
            // IP3Ref is a hidden enum NAME (brief-sys-5's D5): one intercept field plus a reference,
            // because OIP3 = IIP3 + Gain is an identity and two independent fields can be made to
            // contradict each other. Output is the default because that is the form a power
            // amplifier's datasheet quotes.
            case SymbolKind.Amp:
                return [new("Gain",   "20",     "dB",  true,  UnitDimension.None),
                        new("IP3",    "40",     "dBm", true,  UnitDimension.Power),
                        new("IP3Ref", "Output", "",    false, UnitDimension.None),
                        new("Zin",    "50",     "Ω",   false, UnitDimension.Resistance),
                        new("Zout",   "50",     "Ω",   false, UnitDimension.Resistance),
                        new("RLin",   "200",    "dB",  false, UnitDimension.None),
                        new("RLout",  "200",    "dB",  false, UnitDimension.None),
                        new("S12",    "200",    "dB",  false, UnitDimension.None)];
            // 10 dB, which is brief-sys-2's stated default for the MODEL — SYS-1 had seeded 3 here
            // as a label placeholder before there was a model to disagree with, and a tile whose
            // placed value differs from what the same component takes when the parameter is absent
            // is a trap rather than a choice.
            case SymbolKind.Atten:
                return [new("Loss",  "10",   "dB",  true,  UnitDimension.None),
                        new("Z0",    "50",   "Ω",   false, UnitDimension.Resistance),
                        new("RL",    "200",  "dB",  false, UnitDimension.None),
                        new("PIM",   "-200", "dBm", false, UnitDimension.Power),
                        new("PIMPc", "43",   "dBm", false, UnitDimension.Power)];
            case SymbolKind.Mixer:
            case SymbolKind.MixerD:
                return [new("ConvGain", "-7",  "dB",  true,  UnitDimension.None),
                        new("Plo",      "7",   "dBm", true,  UnitDimension.Power),
                        new("Zrf",      "50",  "Ω",   false, UnitDimension.Resistance),
                        new("Zlo",      "50",  "Ω",   false, UnitDimension.Resistance),
                        new("Zif",      "50",  "Ω",   false, UnitDimension.Resistance),
                        new("IsoLO_RF", "200", "dB",  false, UnitDimension.None),
                        new("IsoLO_IF", "200", "dB",  false, UnitDimension.None),
                        new("IsoRF_IF", "200", "dB",  false, UnitDimension.None),
                        new("IIP3",     "100", "dBm", false, UnitDimension.Power)];

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

            // MOD_Source: one 20 MHz LTE carrier at 2 GHz, 10 dBm average — a ready modulated source. The Signal
            // Designer rewrites Carrier[i] / Record / Clip; Carrier strings are descriptions, never expressions.
            case SymbolKind.ModSource: return [
                new("Fc",         "2",    "GHz", true,  UnitDimension.Frequency),
                new("Pavg",       "10",   "dBm", true,  UnitDimension.Power),
                new("Z",          "50",   "Ω",   true,  UnitDimension.Resistance),
                new("Carrier[1]", "\"LTE;BW=20MHz;Mod=64QAM;Off=0;P=0;Seed=1\"", "", true, UnitDimension.None),
                new("Record",     "1e-3", "",    false, UnitDimension.None)];

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

            // SpiceModel: a .model card or .subckt definition the user points at.
            //
            // `File` and `Name` are the whole interface — WHICH file, and WHICH definition in it.
            // Both are shown on the schematic: the type label already renders the definition's name
            // (EditableComponent.TypeLabelText), so what these rows add is the two questions a
            // reader of someone else's schematic actually asks.
            //
            // `PinConfig` and `Pitch` are SnP's own spellings and SnP's own values, meaning the same
            // two things, because they answer the same question about the same box — a .subckt draws
            // as an N-port box and its pins need laying out. They are ARTWORK and are hidden, as
            // SnP's are; a .model card ignores them entirely (it draws as the device).
            //
            // There is deliberately no port-count parameter. A SnP has one because a Touchstone file
            // states its ports in the FILE NAME, so the count is known before anything is read; a
            // .subckt states them on its own definition line, so a second copy on the instance could
            // only ever go stale.
            case SymbolKind.SpiceModel:
                return [
                    new(SpiceModelSymbolProvider.FileParameter, "", "", true,  UnitDimension.None),
                    new(SpiceModelSymbolProvider.NameParameter, "", "", true,  UnitDimension.None),
                    // `Section` is blank for every file that declares none, which is nearly all of
                    // them — and blank is the whole file, today's behaviour exactly. Hidden on the
                    // schematic for that reason: a row that is empty on every instance but the rare
                    // sectioned one is noise on the sheet.
                    new(SpiceModelSymbolProvider.SectionParameter, "", "", false, UnitDimension.None),
                    new("PinConfig", "Standard", "", false, UnitDimension.None),
                    new("Pitch",     "Loose",    "", false, UnitDimension.None),
                ];

            // VerilogA: a compiled model the user points at. Only `File` is required — everything
            // else the model needs is one of ITS parameters, added by the user in the dialog and
            // forwarded verbatim, because a compact model has hundreds and they belong to its author.
            //
            // `Pins` is circuitRF's, not the model's: the symbol has to know how many terminals to
            // draw before anything has read the file. Set it to what the model declares.
            //
            // `OpVars` is circuitRF's too, and it is seeded TRUE. A compiled model computes tens of
            // internal quantities and publishing them is the useful default — a read-back nobody
            // switched on is a read-back nobody finds. The switch exists for the other direction: a
            // design full of such devices, swept over a few hundred points, carries thousands of
            // result names, and a user studying one device can stop paying for the rest. Absent means
            // true as well, so a schematic saved before this existed behaves identically.
            // A composed FET: the .cmodel, the finger count (blank = the model's own N0) and the symbol
            // (blank = the model's own). Symbol is artwork and never reaches the engine. Rth and Cth (blank = the
            // model's, scaled to N) set this device's own thermal network; Temp (blank = the design's temp) its temperature.
            // The ten instance switches (brief-modelcomposer-21, -22) follow, seeded at their defaults — the model as fitted —
            // and never drawn; extraction writes one only when it is not the default.
            case SymbolKind.ComposedFET:
                return [
                    new("File",   "",     "", true,  UnitDimension.None),
                    new("N",      "",     "", true,  UnitDimension.None),
                    new("Symbol", "",     "", false, UnitDimension.None),
                    new("Rth",    "",     "", false, UnitDimension.None),
                    new("Cth",    "",     "", false, UnitDimension.None),
                    new("Temp",   "",     "", false, UnitDimension.None),
                    .. ComposedSwitches.Names.Select(n => new DefaultParam(
                        n, ComposedSwitches.DefaultOf(n) ? "true" : "false", "", false, UnitDimension.None)),
                ];

            case SymbolKind.VerilogA:
                return [
                    new("File",   "",     "", true,  UnitDimension.None),
                    new("Model",  "",     "", false, UnitDimension.None),
                    new("Pins",   "2",    "", false, UnitDimension.None),
                    new("OpVars", "true", "", false, UnitDimension.None),
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
            // `WarnUnsynced` and `File` (wbond.md §9.7, revised 2026-10-05): `File` names the layout's
            // `.wBond` this instance is CHECKED against — blank until Update Layout from Schematic
            // writes it — and `WarnUnsynced` says whether a Run warns when the two differ. The
            // schematic's own wires simulate either way. (`Source`, WB45's Carried/Linked switch, is
            // retired; older documents still carry it and nothing reads it.)
            case SymbolKind.WBond:
                return [
                    new("Design",      WBondEmbedding.DefaultPayload, "", false, UnitDimension.None),
                    new("Arrays",      WBondSymbolProvider.DefaultArraysKey, "", false, UnitDimension.None),
                    new("File",        "", "", false, UnitDimension.None),
                    new("WarnUnsynced", "true", "", false, UnitDimension.None),
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
                    // °C. 125, not blank (owner, 2026-10-05): a blank box said nothing about what the run
                    // used. 125 °C is THE default wire temperature everywhere (WireMaterials.
                    // DefaultOperatingTempC). A .csch is loaded as written, so an existing instance's blank
                    // keeps meaning the payload's stored temperature — itself read as 125 when it is the old
                    // default 85 — and the row shows it as placeholder text. Unlike LoopHeight/Diameter/
                    // Material below, which must stay blank (§2.2).
                    new("Temp",        WBondDefaultTempC, "", false, UnitDimension.None),
                    // brief-wbond-wire-temperature D4 — the Temp row's checkbox, and the two boundary
                    // temperatures under it. A NEW placement solves its temperature (FixedTemp "false");
                    // an instance that states no FixedTemp — every one saved before this — is FIXED, so its
                    // answer does not move (ComponentModelFactory.WireThermalSpecOf). TempStart/TempEnd are
                    // expression rows, so `TempStart = tdie` is typable and sweepable.
                    new("FixedTemp",   "false", "", false, UnitDimension.None),
                    new("TempStart",   WBondDefaultTempC, "", false, UnitDimension.None),
                    new("TempEnd",     WBondDefaultTempEndC, "", false, UnitDimension.None),
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
                    // Recombination — the second exponential, off at Isr = 0, which is the ordinary
                    // case. Declared rather than left implicit because a model card that states it
                    // has nowhere else to put it, and a parameter the engine reads but the dialog
                    // never shows is one a user cannot discover, correct, or sweep.
                    new("Isr",  "0",     "A",  false, UnitDimension.Current),
                    new("Nr",   "2",     "",   false, UnitDimension.None),
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
                    // Nbv is the PUBLISHED 1, not N: nothing physical ties the reverse knee to the
                    // forward ideality, and no parameter table states that it does.
                    new("Nbv",  "1",     "",   false, UnitDimension.None),
                    new("Tt",   "0",     "",   false, UnitDimension.None),
                    // Geometry, then temperature — the same tail the BJT rows carry, and for the
                    // same reason: Area scales the currents and the capacitance BEFORE any
                    // temperature relation, and Xti/Eg/Tnom are what those relations are written in.
                    // Every one of these was already live in CreateDiodeModel and simply had no row,
                    // so a .model card's XTI had nowhere to land (owner, 2026-09-01). Eg is 1.16
                    // (Temperature.SiliconBandgapEv), which is the factory's own fallback — stating
                    // a different number here would change every existing diode silently.
                    new("Area", "1",     "",   false, UnitDimension.None),
                    new("Xti",  "3",     "",   false, UnitDimension.None),
                    new("Eg",   "1.16",  "V",  false, UnitDimension.Voltage),
                    new("Temp", "26.85", "",   false, UnitDimension.None),
                    new("Tnom", "26.85", "",   false, UnitDimension.None),
                ];

            // The p-channel laws share their n-channel counterpart's list, because they ARE that
            // law with every sign reversed. Only the threshold default differs — a p-channel
            // depletion device pinches off at a POSITIVE gate voltage, and a card states it that
            // way.
            // Read from the n-channel list rather than copied, so the two cannot drift: a
            // parameter added to the Curtice law reaches its p-channel tile with no second edit.
            // Only the leading threshold row is replaced.
            case SymbolKind.PFetCurtice:
                return [
                    new("Vto",  "2",   "V", true, UnitDimension.Voltage),
                    .. DefaultParameters(SymbolKind.FetCurtice, portCount).Skip(1),
                ];

            case SymbolKind.PFetStatz:
                return [
                    new("Vto",  "2",   "V", true, UnitDimension.Voltage),
                    .. DefaultParameters(SymbolKind.FetStatz, portCount).Skip(1),
                ];

            case SymbolKind.PFetMaterka:
                return [
                    new("Idss", "0.1", "A", true, UnitDimension.Current),
                    new("Vp0",  "2",   "V", true, UnitDimension.Voltage),
                    .. DefaultParameters(SymbolKind.FetMaterka, portCount).Skip(2),
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

            // Both channels share ONE parameter list — one set of equations with one sign changed.
            // The defaults are a generic small-signal depletion JFET: it conducts at zero gate bias
            // and pinches off at −2 V, so a freshly dragged tile is a working device. As with every
            // other family here they are a STARTING POINT, not a claim about any particular part.
            //
            // Vto is stated AS A CARD STATES IT — negative for n-channel, positive for p-channel —
            // and the model applies the channel sign itself.
            case SymbolKind.JfetN:
            case SymbolKind.JfetP:
                return [
                    new("Vto",     "-2",    "V",  true,  UnitDimension.Voltage),
                    new("Beta",    "1e-4",  "",   true,  UnitDimension.None),
                    // Lambda = 0 means the output conductance is NOT MODELLED.
                    new("Lambda",  "0",     "",   true,  UnitDimension.None),
                    // The gate is TWO junctions, one to each end of the channel. Both conduct and
                    // both store depletion charge, which is what makes this a JFET rather than a
                    // MESFET with a different knee.
                    new("Is",      "1e-14", "A",  false, UnitDimension.Current),
                    new("N",       "1",     "",   false, UnitDimension.None),
                    // Recombination — a SECOND exponential with its own ideality, off at Isr = 0,
                    // exactly as the diode's is.
                    new("Isr",     "0",     "A",  false, UnitDimension.Current),
                    new("Nr",      "2",     "",   false, UnitDimension.None),
                    new("Cgs",     "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Cgd",     "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Pb",      "1",     "V",  false, UnitDimension.Voltage),
                    new("M",       "0.5",   "",   false, UnitDimension.None),
                    new("Fc",      "0.5",   "",   false, UnitDimension.None),
                    // Ohmic. MODEL parameters, not separately placed resistors — a non-zero one
                    // moves the intrinsic device onto an internal node the elaborator mints.
                    new("Rd",      "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Rs",      "0",     "Ω",  false, UnitDimension.Resistance),
                    // Geometry, then temperature — the same tail the diode and BJT rows carry.
                    new("Area",    "1",     "",   false, UnitDimension.None),
                    new("Xti",     "3",     "",   false, UnitDimension.None),
                    new("Eg",      "1.16",  "V",  false, UnitDimension.Voltage),
                    // Vto shifts ADDITIVELY in volts per degree; Beta scales in PERCENT per degree.
                    // Two different shapes, and confusing them costs several percent at ΔT = 100.
                    new("Vtotc",   "0",     "",   false, UnitDimension.None),
                    new("Betatce", "0",     "",   false, UnitDimension.None),
                    new("Temp",    "26.85", "",   false, UnitDimension.None),
                    new("Tnom",    "26.85", "",   false, UnitDimension.None),
                ];

            // Both channels share ONE parameter list. The defaults are a plausible mid-voltage
            // switching part rather than zeros — a threshold, a transconductance, a body diode with
            // a breakdown, and the two Miller plateaus — so a freshly dragged tile turns on, blocks,
            // and freewheels, which are the three things a user immediately tries. A STARTING
            // POINT, not a claim about any particular part.
            //
            // Kp here is A/V² for the WHOLE DEVICE. There is no W/L to apply: the geometry is the
            // die, and a discrete part's card is written for that die.
            // Both polarities share ONE parameter list. These are DATA SHEET quantities — a
            // threshold, a transconductance, a current gain, a transit time — which is what an
            // equivalent-circuit model is parameterised by, and deliberately not the ambipolar
            // transport model's parameters, which describe the silicon instead. The two sets do not
            // map onto one another; see IgbtModel.
            case SymbolKind.IgbtN:
            case SymbolKind.IgbtP:
                return [
                    new("Vto",    "5",     "V",  true,  UnitDimension.Voltage),
                    new("Kp",     "8",     "",   true,  UnitDimension.None),
                    new("Lambda", "0",     "",   false, UnitDimension.None),
                    // The bipolar's gain is LOW by construction — its base is deliberately wide.
                    // It sets how the collector current divides between the channel and the
                    // bipolar, and therefore how much of the turn-off current is in the tail.
                    new("Bf",     "0.5",   "",   true,  UnitDimension.None),
                    new("Is",     "1e-12", "A",  false, UnitDimension.Current),
                    new("N",      "1",     "",   false, UnitDimension.None),
                    // Tau IS the current tail: it is the stored base charge, and turn-off cannot
                    // remove it through the gate.
                    new("Tau",    "1",     "us", true,  UnitDimension.None),
                    new("Rbe",    "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Rce",    "0",     "Ω",  false, UnitDimension.Resistance),
                    // Forward BREAK-OVER, across the drift region — the V_CES rating. It is a limit
                    // rather than an operating mode, unlike the power MOSFET's avalanche, which is
                    // rated. Bv = 0 means NOT MODELLED, never "breaks over at 0 V".
                    new("Bv",     "0",     "V",  true,  UnitDimension.Voltage),
                    new("Ibv",    "1e-3",  "A",  false, UnitDimension.Current),
                    new("Nbv",    "1",     "",   false, UnitDimension.None),
                    new("Cjc",    "0",     "pF", false, UnitDimension.Capacitance),
                    new("Vj",     "0.8",   "V",  false, UnitDimension.Voltage),
                    new("Mj",     "0.5",   "",   false, UnitDimension.None),
                    new("Fc",     "0.5",   "",   false, UnitDimension.None),
                    new("Cge",    "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Cgcmax", "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Cgcmin", "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Vgct",   "1",     "V",  false, UnitDimension.Voltage),
                    new("Rg",     "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Rc",     "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Re",     "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Vtotc",  "0",     "",   false, UnitDimension.None),
                    new("Kptc",   "0",     "",   false, UnitDimension.None),
                    new("Xti",    "3",     "",   false, UnitDimension.None),
                    new("Eg",     "1.16",  "V",  false, UnitDimension.Voltage),
                    new("Temp",   "26.85", "",   false, UnitDimension.None),
                    new("Tnom",   "26.85", "",   false, UnitDimension.None),
                ];

            // The ferrite bead. Every default is ZERO except the winding resistance, and zero
            // means NOT MODELLED for each of the three parallel elements — never "a short". A bead
            // with nothing typed into it is a piece of wire with a milliohm of resistance, which is
            // the honest starting point: the four numbers come off a data sheet's impedance curve
            // and there is no representative bead to pre-fill them with.
            //
            // Rp is what CAPS the impedance — at the parallel resonance the reactive branches
            // cancel and |Z| is Rdc + Rp, which is the peak a data sheet plots. A bead entered
            // without it has no maximum at all and goes on rising for ever.
            case SymbolKind.Bead:
                return [
                    new("Rdc", "0.01", "Ω",  true,  UnitDimension.Resistance),
                    new("L",   "0",    "uH", true,  UnitDimension.Inductance),
                    new("Rp",  "0",    "Ω",  true,  UnitDimension.Resistance),
                    new("Cp",  "0",    "pF", true,  UnitDimension.Capacitance),
                ];

            case SymbolKind.VdmosN:
            case SymbolKind.VdmosP:
                return [
                    new("Vto",    "3",     "V",  true,  UnitDimension.Voltage),
                    new("Kp",     "1",     "",   true,  UnitDimension.None),
                    new("Lambda", "0",     "",   false, UnitDimension.None),
                    // Rds = 0 means the off-state leakage is NOT MODELLED, never "a short circuit".
                    new("Rds",    "0",     "Ω",  false, UnitDimension.Resistance),
                    // The body diode. It carries load current in a half bridge, so it is not a
                    // leakage path and its parameters are not decoration.
                    new("Is",     "1e-13", "A",  false, UnitDimension.Current),
                    new("N",      "1",     "",   false, UnitDimension.None),
                    // Bv = 0 means avalanche is NOT MODELLED, never "breaks down at 0 V".
                    new("Bv",     "0",     "V",  true,  UnitDimension.Voltage),
                    new("Ibv",    "1e-3",  "A",  false, UnitDimension.Current),
                    new("Nbv",    "1",     "",   false, UnitDimension.None),
                    // Tt is the reverse-recovery charge — usually the dominant switching loss in a
                    // hard-switched bridge.
                    new("Tt",     "0",     "",   false, UnitDimension.None),
                    new("Cjo",    "0",     "pF", false, UnitDimension.Capacitance),
                    new("Vj",     "0.8",   "V",  false, UnitDimension.Voltage),
                    new("Mj",     "0.5",   "",   false, UnitDimension.None),
                    new("Fc",     "0.5",   "",   false, UnitDimension.None),
                    // The gate. Cgdmax/Cgdmin are the two ends of a data sheet's reverse-transfer
                    // curve and Vgdt is how sharply it falls between them — that collapse IS the
                    // Miller plateau, and a constant capacitance of either value gets the switching
                    // time wrong by the ratio of the two.
                    new("Cgs",    "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Cgdmax", "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Cgdmin", "0",     "pF", true,  UnitDimension.Capacitance),
                    new("Vgdt",   "1",     "V",  false, UnitDimension.Voltage),
                    // Ohmic. Rg is in the DRIVE path, in series with a capacitance that large, so
                    // it sets the switching speed as much as the drive current does.
                    new("Rg",     "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Rd",     "0",     "Ω",  false, UnitDimension.Resistance),
                    new("Rs",     "0",     "Ω",  false, UnitDimension.Resistance),
                    // Temperature. Vto shifts ADDITIVELY in volts per degree; Kptc scales Kp in
                    // PERCENT per degree, and with Kptc left at zero the mobility follows T^-1.5 —
                    // which is why on-resistance rises with temperature, which is what makes
                    // paralleling these parts work.
                    new("Vtotc",  "0",     "",   false, UnitDimension.None),
                    new("Kptc",   "0",     "",   false, UnitDimension.None),
                    new("Xti",    "3",     "",   false, UnitDimension.None),
                    new("Eg",     "1.16",  "V",  false, UnitDimension.Voltage),
                    new("Temp",   "26.85", "",   false, UnitDimension.None),
                    new("Tnom",   "26.85", "",   false, UnitDimension.None),
                ];

            // Both channels share ONE parameter list, because they are one set of equations with
            // one sign changed — the BJT pair's arrangement, for the BJT pair's reason.
            //
            // The DEFAULTS are deliberately a bare square-law device: a threshold, a
            // transconductance parameter and a geometry, with every process quantity at zero. A
            // MOS parameter set is a property of a PROCESS, and there is no such thing as a
            // representative one — inventing plausible numbers here would put a specific fabricated
            // transistor in the palette and let a user simulate it without ever noticing they had
            // not supplied a model. The BJT's own defaults are a published RF part carried through
            // verbatim, which is a different situation: there the numbers describe something real.
            //
            // Where a card states a PROCESS quantity instead of a device one the model derives the
            // other from it — Nsub gives Gamma and Phi, Uo with Tox gives Kp, Rsh with Nrd/Nrs
            // gives Rd/Rs, Cj/Cjsw with the junction areas give Cbd/Cbs — and a stated value always
            // wins. That is why both spellings of each pair have a row: the card may carry either.
            //
            // Tox = 0 means NO OXIDE CAPACITANCE, so the intrinsic gate charge is absent and only
            // the overlaps remain. That is the published rule and the honest one — there is nothing
            // to guess an oxide thickness from — but it does mean a card with no Tox gives a device
            // with almost no gate capacitance, which is worth knowing before wondering where the
            // gain went.
            // Level 3 reads level 1's list rather than copying it, so the two cannot drift: a
            // parameter added to one reaches the other with no second edit. What differs is exactly
            // what the two laws differ on — Lambda is dropped (level 3 computes the output slope
            // from a real channel shortening instead of fitting it) and the six short-channel
            // parameters are added.
            case SymbolKind.Mos3N:
            case SymbolKind.Mos3P:
            {
                var shared = DefaultParameters(kind == SymbolKind.Mos3N ? SymbolKind.Mos1N : SymbolKind.Mos1P,
                                               portCount)
                    .Where(p => p.Name != "Lambda")
                    .ToList();
                // Inserted after the geometry rather than appended, because these are what a reader
                // of a level-3 parameter set looks for first.
                int at = shared.FindIndex(p => p.Name == "Uo");
                shared.InsertRange(at < 0 ? shared.Count : at,
                [
                    // Each is OFF at zero, and each turns on exactly one mechanism.
                    new("Eta",   "0",   "",    true,  UnitDimension.None),
                    new("Theta", "0",   "",    true,  UnitDimension.None),
                    // Kappa's conventional default is non-zero, unlike the rest: the published model
                    // states it, and channel-length modulation is the mechanism a short device is
                    // least likely to be without. It still does nothing without Nsub, which is what
                    // the depletion width is derived from.
                    new("Kappa", "0.2", "",    false, UnitDimension.None),
                    new("Vmax",  "0",   "",    true,  UnitDimension.None),
                    new("Delta", "0",   "",    false, UnitDimension.None),
                    new("Xj",    "0",   "um",  false, UnitDimension.Length),
                ]);
                return shared;
            }

            case SymbolKind.Mos1N:
            case SymbolKind.Mos1P:
                return [
                    // The channel-current law.
                    new("Vto",    "1",     "V",     true,  UnitDimension.Voltage),
                    new("Kp",     "2e-5",  "",      true,  UnitDimension.None),
                    new("Gamma",  "0",     "",      true,  UnitDimension.None),
                    new("Phi",    "0.6",   "V",     false, UnitDimension.Voltage),
                    // Lambda = 0 means the output conductance is NOT MODELLED, never "saturates
                    // flat at zero slope".
                    new("Lambda", "0",     "",      true,  UnitDimension.None),
                    // Geometry. Metres, like every other length in circuitRF — a card states them
                    // unscaled and the parameter dialog offers the usual sub-multiples.
                    new("W",      "100",   "um",    true,  UnitDimension.Length),
                    new("L",      "100",   "um",    true,  UnitDimension.Length),
                    new("Ld",     "0",     "um",    false, UnitDimension.Length),
                    new("Tox",    "0",     "nm",    false, UnitDimension.Length),
                    // The process alternatives to Kp and Gamma/Phi. Used only where the device
                    // quantity above is absent.
                    new("Uo",     "600",   "",      false, UnitDimension.None),
                    new("Nsub",   "0",     "",      false, UnitDimension.None),
                    // Gate overlaps. Cgso/Cgdo are per unit WIDTH, Cgbo per unit LENGTH — getting
                    // that the wrong way round is a capacitance wrong by the aspect ratio.
                    new("Cgso",   "0",     "",      false, UnitDimension.None),
                    new("Cgdo",   "0",     "",      false, UnitDimension.None),
                    new("Cgbo",   "0",     "",      false, UnitDimension.None),
                    // The two bulk junctions. Is is per junction; Js is a density and needs an area.
                    new("Is",     "1e-14", "A",     false, UnitDimension.Current),
                    new("Js",     "0",     "",      false, UnitDimension.None),
                    new("N",      "1",     "",      false, UnitDimension.None),
                    new("Cbd",    "0",     "fF",    false, UnitDimension.Capacitance),
                    new("Cbs",    "0",     "fF",    false, UnitDimension.Capacitance),
                    new("Cj",     "0",     "",      false, UnitDimension.None),
                    new("Cjsw",   "0",     "",      false, UnitDimension.None),
                    new("Ad",     "0",     "",      false, UnitDimension.None),
                    new("As",     "0",     "",      false, UnitDimension.None),
                    new("Pd",     "0",     "um",    false, UnitDimension.Length),
                    new("Ps",     "0",     "um",    false, UnitDimension.Length),
                    new("Pb",     "0.8",   "V",     false, UnitDimension.Voltage),
                    new("Mj",     "0.5",   "",      false, UnitDimension.None),
                    new("Mjsw",   "0.33",  "",      false, UnitDimension.None),
                    new("Fc",     "0.5",   "",      false, UnitDimension.None),
                    // Ohmic. MODEL parameters, not separately placed resistors — a non-zero one
                    // moves the intrinsic device onto an internal node the elaborator mints, so the
                    // schematic shows one transistor either way.
                    new("Rd",     "0",     "Ω", false, UnitDimension.Resistance),
                    new("Rs",     "0",     "Ω", false, UnitDimension.Resistance),
                    new("Rsh",    "0",     "Ω", false, UnitDimension.Resistance),
                    new("Nrd",    "0",     "",      false, UnitDimension.None),
                    new("Nrs",    "0",     "",      false, UnitDimension.None),
                    // Temperature. Temp and Tnom are in DEGREES CELSIUS and unitless here, for the
                    // same reason the diode's and the BJT's are — there is no temperature
                    // UnitDimension, and a "C" unit token would collide with capacitance in the
                    // .cnl parameter tokenizer.
                    new("Xti",    "3",     "",      false, UnitDimension.None),
                    new("Eg",     "1.16",  "V",     false, UnitDimension.Voltage),
                    new("Temp",   "26.85", "",      false, UnitDimension.None),
                    new("Tnom",   "26.85", "",      false, UnitDimension.None),
                ];

            // Both polarities share ONE parameter list, because they are one set of equations with
            // one sign changed. The defaults are a real, published small-signal RF silicon n-p-n
            // parameter set carried through verbatim, so a freshly dragged transistor is a working
            // GHz device rather than a shape waiting for numbers. They are a STARTING POINT, not a
            // claim about any particular part — edit them for the transistor you actually have.
            case SymbolKind.BjtNpn:
            case SymbolKind.BjtPnp:
                return [
                    // Transport and forward gain.
                    new("Is",   "9.57e-17",  "A", true,  UnitDimension.Current),
                    new("Bf",   "131.1",     "",  true,  UnitDimension.None),
                    new("Nf",   "1",         "",  false, UnitDimension.None),
                    // Vaf/Var = 0 means the Early effect is NOT MODELLED, never "zero volts".
                    new("Vaf",  "71.02",     "V", true,  UnitDimension.Voltage),
                    new("Ikf",  "0.09745",   "A", false, UnitDimension.Current),
                    new("Ise",  "1.618e-15", "A", false, UnitDimension.Current),
                    new("Ne",   "1.692",     "",  false, UnitDimension.None),
                    // Reverse (the transistor upside down) — it is what sets saturation behaviour.
                    new("Br",   "3.287",     "",  false, UnitDimension.None),
                    new("Nr",   "0.959",     "",  false, UnitDimension.None),
                    new("Var",  "4.081",     "V", false, UnitDimension.Voltage),
                    new("Ikr",  "0.07617",   "A", false, UnitDimension.Current),
                    new("Isc",  "5.969e-15", "A", false, UnitDimension.Current),
                    new("Nc",   "1.974",     "",  false, UnitDimension.None),
                    // Parasitic resistances. MODEL parameters, not separately placed resistors —
                    // a non-zero one moves the junctions onto an internal node the elaborator
                    // mints, so the schematic shows one device either way. Rb modulates with base
                    // current between Rb and Rbm; Irb sets where.
                    new("Rb",   "9.72444",   "Ω", false, UnitDimension.Resistance),
                    new("Irb",  "3.017e-6",  "A", false, UnitDimension.Current),
                    new("Rbm",  "6.94667",   "Ω", false, UnitDimension.Resistance),
                    new("Re",   "0.7979",    "Ω", false, UnitDimension.Resistance),
                    new("Rc",   "2.089",     "Ω", false, UnitDimension.Resistance),
                    // Junction charge. Xcjc is the fraction of Cjc on the INTERNAL base node; the
                    // rest sits across Rb, which is why it matters at RF and not at DC.
                    new("Cje",  "82.87",     "fF", false, UnitDimension.Capacitance),
                    new("Vje",  "0.8281",    "V",  false, UnitDimension.Voltage),
                    new("Mje",  "0.7138",    "",   false, UnitDimension.None),
                    new("Cjc",  "87.81",     "fF", false, UnitDimension.Capacitance),
                    new("Vjc",  "0.7715",    "V",  false, UnitDimension.Voltage),
                    new("Mjc",  "0.7552",    "",   false, UnitDimension.None),
                    new("Xcjc", "0.6209",    "",   false, UnitDimension.None),
                    new("Fc",   "0.6275",    "",   false, UnitDimension.None),
                    // Transit time — the parameter that sets fT, and the reason this device is
                    // usable above a gigahertz at all.
                    new("Tf",   "1.72653e-11", "", true,  UnitDimension.None),
                    new("Xtf",  "0.07",        "", false, UnitDimension.None),
                    new("Vtf",  "0.00381019",  "V", false, UnitDimension.Voltage),
                    new("Itf",  "0.027024",    "A", false, UnitDimension.Current),
                    new("Tr",   "1.71536e-8",  "", false, UnitDimension.None),
                    // Geometry, then temperature. Temp and Tnom are in DEGREES CELSIUS and
                    // unitless here, for the same reason the diode's are — there is no temperature
                    // UnitDimension, and a "C" unit token would collide with capacitance in the
                    // .cnl parameter tokenizer. Both default to the same value, so a device the
                    // user never sets a temperature on sits exactly at its extraction point.
                    new("Area", "1",     "", false, UnitDimension.None),
                    new("Xti",  "6.548", "", false, UnitDimension.None),
                    new("Xtb",  "1.303", "", false, UnitDimension.None),
                    new("Eg",   "1.11",  "V", false, UnitDimension.Voltage),
                    new("Temp", "26.85", "", false, UnitDimension.None),
                    new("Tnom", "26.85", "", false, UnitDimension.None),
                ];

            case SymbolKind.NonlinearC:
                return [new("C0", "1", "pF", true, UnitDimension.Capacitance)];

            // TLIN: ideal lossless transmission line. Z = characteristic impedance (real, lossless),
            // E = electrical length in degrees at reference frequency F. All three show on the schematic.
            // The physical form (L, Eeff, Ac, Ad — brief-artsch-2) is reached by the parameter editor's
            // Electrical/Physical switch; a fresh TLIN stays in the angle form.
            case SymbolKind.Tline:
                return [new("Z", "50", "Ω",   true, UnitDimension.Resistance),
                        new("E", "90", "deg", true, UnitDimension.Angle),
                        new("F", "1",  "GHz", true, UnitDimension.Frequency)];

            // CLIN: ideal coupled pair. Ze/Zo = even/odd-mode impedances, E = (even-mode) electrical
            // length in degrees at F. The defaults are CLineModel's own: a 50-ohm, 10 dB, quarter-wave
            // pair. Eo (odd-mode length) is OPTIONAL and starts blank — blank means "the same as E",
            // the homogeneous case — and NetExtractor drops a blank Eo rather than emit `Eo=`.
            case SymbolKind.CLine:
                return [new("Ze", "69.37", "Ω",   true,  UnitDimension.Resistance),
                        new("Zo", "36.04", "Ω",   true,  UnitDimension.Resistance),
                        new("E",  "90",    "deg", true,  UnitDimension.Angle),
                        new("F",  "1",     "GHz", true,  UnitDimension.Frequency),
                        new("Eo", "",      "deg", false, UnitDimension.Angle)];

            // BCLIN / MBCLIN: the strips' W, O and L are the user's; every cross-section row starts EMPTY,
            // which means "from the stackup between LayerA and LayerB" (BroadsideSubstrateInjection), and
            // with no stackup the model's own standalone default. A typed value wins for that row only.
            case SymbolKind.BCLine:
                return [new("W",  "0.4", "mm", true,  UnitDimension.Length),
                        new("O",  "0",   "mm", true,  UnitDimension.Length),
                        new("L",  "10",  "mm", true,  UnitDimension.Length),
                        new("Hs", "",    "µm", false, UnitDimension.Length),
                        new("Ha", "",    "µm", false, UnitDimension.Length),
                        new("Hb", "",    "µm", false, UnitDimension.Length),
                        new("T",  "",    "µm", false, UnitDimension.Length),
                        new("Er", "",    "",   false, UnitDimension.None),
                        new(BroadsideSubstrateInjection.LayerAParam, "", "", false, UnitDimension.None),
                        new(BroadsideSubstrateInjection.LayerBParam, "", "", false, UnitDimension.None)];
            case SymbolKind.MBCLine:
                return [new("W",  "0.4", "mm", true,  UnitDimension.Length),
                        new("O",  "0",   "mm", true,  UnitDimension.Length),
                        new("L",  "10",  "mm", true,  UnitDimension.Length),
                        new("Hs", "",    "µm", false, UnitDimension.Length),
                        new("Hb", "",    "µm", false, UnitDimension.Length),
                        new("Hd", "",    "µm", false, UnitDimension.Length),
                        new("T",  "",    "µm", false, UnitDimension.Length),
                        new("Er", "",    "",   false, UnitDimension.None),
                        new(BroadsideSubstrateInjection.LayerAParam, "", "", false, UnitDimension.None),
                        new(BroadsideSubstrateInjection.LayerBParam, "", "", false, UnitDimension.None)];

            // Match: `Design` is the WHOLE design, base64 of its JSON (match.md §7.2) — hidden, never
            // a text row (ParameterEditorViewModel.IsMatchPanelParameter), and the only input the
            // model reads. The rest are ECHO parameters: the Designer writes them so the design can be
            // READ — in the `.cnl` line, where the payload is still a base64 token, and by the glyph,
            // which draws itself from `Form` and `Bands` — and NOTHING reads them back. That is why
            // they are duplicated here at all rather than derived at render time; match.md §7.2 makes
            // the design authoritative, so an echo can never become a second input.
            //
            // EVERY ONE OF THEM IS ShowOnSchematic: false (owner, 2026-08-28). A Match exposes no
            // parameters to the page: `F1`, `F2` and `Order` used to be drawn beside it, and what
            // they were saying — which band, how big — the glyph now says itself, without three text
            // rows nobody can edit there anyway. The Designer is the only place a Match is edited, so
            // it is the only place its numbers belong. `EditableComponent.LabelParameters` enforces
            // the same rule on instances placed before this change, whose files still say true.
            //
            // The default payload is a REAL design (1.8–2.2 GHz, order 4, 50 Ω to 10 Ω,
            // Chebyshev-Fano) and not a blank: a freshly dropped Match must simulate immediately. The
            // ECHO parameters below MUST agree with MatchEmbedding.DefaultDesign — they are what a
            // reader (and the glyph) sees before the Designer has ever rewritten them, and a set that
            // disagrees with the payload describes a component that does not exist. Same rule wBond
            // follows in shipping a default wire rather than an empty array.
            case SymbolKind.Match:
                return [new("Design",   MatchEmbedding.DefaultPayload, "", false, UnitDimension.None),
                        new("F1",       "1.8",           "GHz", false, UnitDimension.Frequency),
                        new("F2",       "2.2",           "GHz", false, UnitDimension.Frequency),
                        // Multiband (match.md §18). Bands is 1 and the second and third bands are 0
                        // in the default, which is the single-band design every existing Match
                        // already is; the Designer rewrites all five together whenever the band count
                        // moves.
                        new("Bands",    "1",             "",    false, UnitDimension.None),
                        new("F3",       "0",             "GHz", false, UnitDimension.Frequency),
                        new("F4",       "0",             "GHz", false, UnitDimension.Frequency),
                        new("F5",       "0",             "GHz", false, UnitDimension.Frequency),
                        new("F6",       "0",             "GHz", false, UnitDimension.Frequency),
                        new("Order",    "4",             "",    false, UnitDimension.None),
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

            // CPWG (brief-artsch-1): W, G, L, the gap being the one dimension the technology has no
            // opinion on. 1.4 mm / 0.3 mm is ~50 Ω on the 1.6 mm FR-4 fallback substrate (the coplanar
            // grounds pull it down from MLIN's 2.9 mm). The layer pair is MLIN's: the signal layer and the
            // plane backing it. PlanarLineSubstrateInjection.ApplyTechnologyDefaults re-synthesises W for
            // 50 Ω on the placing technology, keeping this gap ratio.
            case SymbolKind.Cpwg:
                return [new("W", "1.4", "mm", true, UnitDimension.Length),
                        new("G", "0.3", "mm", true, UnitDimension.Length),
                        new("L", "10",  "mm", true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // SLIN (brief-artsch-1): W, L, and only a SignalLayer — both planes are found from it, the
            // nearest reference conductor above and below, so a GroundReference would name one of two.
            // 0.65 mm is ~50 Ω centred in 1.6 mm of FR-4 (H1 = H2 = 0.8 mm, the model's fallback).
            case SymbolKind.Slin:
                return [new("W", "0.65", "mm", true, UnitDimension.Length),
                        new("L", "10",   "mm", true, UnitDimension.Length),
                        SignalGroundLayerParams[0]];

            // MCLIN: edge-coupled pair — W (each strip), S (edge gap), L. Substrate as MLIN's: resolved
            // from the technology at extraction, never a declared parameter. 2.9 mm / 0.5 mm is the
            // FR-4 baseline; MicrostripSubstrateInjection rewrites W to the technology's 50-ohm width and
            // S to the same at placement.
            case SymbolKind.MCLine:
                return [new("W", "2.9", "mm", true, UnitDimension.Length),
                        new("S", "0.5", "mm", true, UnitDimension.Length),
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

            // MCurve: radiused bend — W, Angle (deg, CCW from the input arm, as MBend's), Radius (the
            // CENTRE-LINE radius). Radius defaults to 3W, the low end of the effective-radius rule's
            // published 3W-7W range (MicrostripCurve); MicrostripSubstrateInjection keeps it 3x the
            // technology's own 50-ohm width at placement.
            case SymbolKind.MCurve:
                return [new("W",      "2.9", "mm",  true, UnitDimension.Length),
                        new("Angle",  "90",  "deg", true, UnitDimension.Angle),
                        new("Radius", "8.7", "mm",  true, UnitDimension.Length),
                        .. SignalGroundLayerParams];

            // MMiter: chamfered bend — W, Angle (deg, CCW from the input arm, as MBend's), M (the
            // Douville-James miter fraction of the outer-to-inner corner diagonal; 0.5 cuts corner to
            // corner at 90 deg). M defaults to 0.5 here; MicrostripSubstrateInjection replaces it at
            // placement with the Douville-James optimum for the technology's own 50-ohm width.
            case SymbolKind.MMiter:
                return [new("W",     "2.9", "mm",  true, UnitDimension.Length),
                        new("Angle", "90",  "deg", true, UnitDimension.Angle),
                        new("M",     "0.5", "",    true, UnitDimension.None),
                        .. SignalGroundLayerParams];

            // MTee: microstrip T-junction — W1/W2 (through arms, may differ), W3 (branch), and each
            // arm's own drawn length L1/L2/L3. MicrostripTeeModel simulates them as MLIN sections from
            // each pin to the junction's reference planes at the crossing arm's edge (brief DFR26-5), so
            // the same geometry gives the same answer as an MLIN. They must be declared rather than
            // derived: deriving arm length from arm width, as this cell used to, makes a width gripper
            // move the junction and the other two pins along the perpendicular axis.
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

            // VIA / VIAGND (brief-via-component.md). Every row may be left EMPTY, which means "follow
            // the technology": the layers default from the stackup, Drill and Pad from the technology's
            // own via defaults, Plating from the via layer's wall. Whatever the technology does not
            // state takes circuitRF's default and the run's Messages NAME it (ViaSubstrateInjection).
            // The barrel length, materials and the planes it passes are never parameters: they are
            // resolved from the stackup and injected at extraction, as a microstrip's substrate is.
            case SymbolKind.Via:
                return [new(ViaSubstrateInjection.FromLayerParam, "", "", false, UnitDimension.None),
                        new(ViaSubstrateInjection.ToLayerParam,   "", "", false, UnitDimension.None),
                        .. ViaDimensionParams];
            case SymbolKind.ViaGnd:
                return [new(ViaSubstrateInjection.FromLayerParam,   "", "", false, UnitDimension.None),
                        new(ViaSubstrateInjection.GroundLayerParam, "", "", false, UnitDimension.None),
                        .. ViaDimensionParams];

            // TESTPAD (brief-dfr26-7 R-dfr26-7-1). Empty rows follow the technology, as a via's do; the layer
            // rows are conductor NAMES. What the stackup resolves — the dielectric the pad faces, the via's
            // length and the planes it passes — is injected at extraction (TestPadInjection).
            case SymbolKind.TestPad:
                return [new(TestPadInjection.PadLayerParam,    "",     "",   false, UnitDimension.None),
                        new("D",                               "1.5",  "mm", true,  UnitDimension.Length),
                        new("Feed",                            TestPadPCell.ViaFeed, "", true, UnitDimension.None),
                        new("FeedW",                           "",     "mm", false, UnitDimension.Length),
                        new(TestPadInjection.ViaFromParam,     "",     "",   false, UnitDimension.None),
                        new("Drill",                           "",     "mm", false, UnitDimension.Length),
                        new("InnerPadD",                       "",     "mm", false, UnitDimension.Length),
                        new("AntipadD",                        "",     "mm", false, UnitDimension.Length),
                        new(TestPadInjection.ClearLayersParam, "",     "",   true,  UnitDimension.None),
                        new("ClearD",                          "",     "mm", false, UnitDimension.Length),
                        new("Centre",                          TestPadPCell.CentreOpen, "", true, UnitDimension.None),
                        new("PortNum",                         "",     "",   false, UnitDimension.None)];

            // The MMIC passives (AA-1). Geometry only, in micrometres because that is what a die is
            // drawn in; everything a process decides is injected from the technology at extraction
            // (MmicPassiveInjection), as a microstrip's substrate is.
            // GSGPAD: W and L are the line's, MLIN's own rows (W re-synthesised for 50 Ω on the placing technology);
            // the probe's own numbers are in µm, which no technology rewrites — a probe's pitch is the probe's.
            case SymbolKind.GsgPad:
                return [new("W",     "2.9", "mm", true, UnitDimension.Length),
                        new("L",     "100", "µm", true, UnitDimension.Length),
                        new("Pitch", "150", "µm", true, UnitDimension.Length),
                        new("PadW",  "80",  "µm", true, UnitDimension.Length),
                        new("PadL",  "80",  "µm", true, UnitDimension.Length),
                        new("NVia",  "1",   "",   true, UnitDimension.None),
                        .. SignalGroundLayerParams];
            case SymbolKind.MimCap:
                return [new("W", "50", "µm", true, UnitDimension.Length),
                        new("L", "50", "µm", true, UnitDimension.Length)];
            case SymbolKind.Spiral:
            case SymbolKind.OctSpiral:
                return [new("N",   "2.5", "",   true,  UnitDimension.None),
                        new("W",   "10",  "µm", true,  UnitDimension.Length),
                        new("S",   "10",  "µm", false, UnitDimension.Length),
                        new("Din", "100", "µm", false, UnitDimension.Length)];
            case SymbolKind.Tfr:
                return [new("W", "10", "µm", true, UnitDimension.Length),
                        new("L", "20", "µm", true, UnitDimension.Length)];
            case SymbolKind.Airbridge:
                return [new("L",  "30", "µm", true,  UnitDimension.Length),
                        new("W",  "10", "µm", false, UnitDimension.Length),
                        new("Wu", "10", "µm", false, UnitDimension.Length)];

            // Ground/Generic need no default parameters.
            default: return [];
        }
    }

    /// <summary>
    /// Parses a short type code (case-insensitive) to a SymbolKind and, for variadic-port types,
    /// the parsed port count N.
    ///
    /// Canonical codes: R, L, C, V, VTone, ITone, VCCS/G, Mixer/MIX, MixerD/MIXD, GND, Term/T,
    /// TermG/TG, Balun, Circulator/CIRC, Switch/SW, SwitchD/SWD, Amp, Coupler/CPL,
    /// Hybrid90/HYB, Hybrid180, Filter/FLT, Attenuator/ATT, Duplexer/DPX, SDD, Z{N}P (any N ≥ 1),
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
            case "SRLC":   kind = SymbolKind.Srlc;          return true;
            case "PRLC":   kind = SymbolKind.Prlc;          return true;
            case "SRL":    kind = SymbolKind.Srl;           return true;
            case "SRC":    kind = SymbolKind.Src;           return true;
            case "SLC":    kind = SymbolKind.Slc;           return true;
            case "PRL":    kind = SymbolKind.Prl;           return true;
            case "PRC":    kind = SymbolKind.Prc;           return true;
            case "PLC":    kind = SymbolKind.Plc;           return true;
            case "V":
            case "VDC":    kind = SymbolKind.Vdc;           return true;
            case "VTONE":  kind = SymbolKind.ToneSource;    return true;
            case "ITONE":  kind = SymbolKind.CurrentToneSource; return true;
            case "VCCS":
            case "G":      kind = SymbolKind.Vccs;          return true;
            case "MIXER":
            case "MIX":    kind = SymbolKind.Mixer;         return true;
            case "MIXERD":
            case "MIXD":   kind = SymbolKind.MixerD;        return true;
            // The system blocks. SW/SWD and CPL/HYB are distinct codes over shared engine
            // components, because the code names the TILE — which is what a user typed.
            // Each takes the short code AND the display name, because those are the two things a
            // user has seen: the abbreviation is what the netlist and the instance names spell, and
            // the display name is what the palette tile and the on-schematic label say. Making
            // someone learn that the tile reading "Attenuator" is typed "ATT" buys nothing.
            case "BALUN":  kind = SymbolKind.Balun;        return true;
            case "CIRCULATOR":
            case "CIRC":   kind = SymbolKind.Circulator;   return true;
            case "SWITCH":
            case "SW":     kind = SymbolKind.Switch;       return true;
            case "SWITCHD":
            case "SWD":    kind = SymbolKind.SwitchD;      return true;
            case "AMP":    kind = SymbolKind.Amp;          return true;
            case "DIRECTIONAL COUPLER":
            case "COUPLER":
            case "CPL":    kind = SymbolKind.Coupler;      return true;
            case "HYBRID90":
            case "HYB":
            case "HYB90":  kind = SymbolKind.Hybrid90;     return true;
            case "HYBRID180":
            case "HYB180": kind = SymbolKind.Hybrid180;    return true;
            case "FILTER":
            case "FLT":    kind = SymbolKind.Filter;       return true;
            case "ATTENUATOR":
            case "ATT":    kind = SymbolKind.Atten;        return true;
            case "DUPLEXER":
            case "DPX":    kind = SymbolKind.Duplexer;     return true;
            case "GND":    kind = SymbolKind.Ground;        return true;
            case "TERM":
            case "T":      kind = SymbolKind.Term;          return true;
            case "TERMG":
            case "TG":     kind = SymbolKind.TermG;         return true;
            case "PIN":    kind = SymbolKind.Pin;           return true;
            case "IPROBE":
            case "IP":     kind = SymbolKind.IProbe;        return true;
            case "VPROBE":
            case "VP":     kind = SymbolKind.VProbe;        return true;
            case "WSPROBE":
            case "WSP":    kind = SymbolKind.WSProbe;       return true;
            case "VAR":    kind = SymbolKind.Var;           return true;
            case "MEAS":   kind = SymbolKind.Meas;          return true;
            case "P1TONE": kind = SymbolKind.P1Tone;        return true;
            case "PNTONE": kind = SymbolKind.PnTone;        return true;
            case "MOD_SOURCE":
            case "MODSOURCE": kind = SymbolKind.ModSource;  return true;
            case "NONLINEARC":
            case "NLC":    kind = SymbolKind.NonlinearC;  return true;
            case "MUTUAL":
            case "MUT":    kind = SymbolKind.Mutual;       return true;
            case "TLIN":
            case "TL":     kind = SymbolKind.Tline;        return true;
            case "CLIN":
            case "CL":     kind = SymbolKind.CLine;        return true;
            case "MCLIN":
            case "MCL":    kind = SymbolKind.MCLine;       return true;
            case "BCLIN":
            case "BCL":    kind = SymbolKind.BCLine;       return true;
            case "MBCLIN":
            case "MBCL":   kind = SymbolKind.MBCLine;      return true;
            case "MATCH":
            case "MN":     kind = SymbolKind.Match;        return true;
            case "TUNER":  kind = SymbolKind.Tuner;         return true;
            case "SOURCETUNER":
            case "SRCTUNER": kind = SymbolKind.SourceTuner; return true;
            case "LOADTUNER":
            case "LDTUNER":  kind = SymbolKind.LoadTuner;    return true;
            case "MLIN":
            case "ML":       kind = SymbolKind.Mlin;         return true;
            case "CPWG":
            case "GCPW":     kind = SymbolKind.Cpwg;         return true;
            case "SLIN":     kind = SymbolKind.Slin;         return true;
            case "VIA":      kind = SymbolKind.Via;          return true;
            case "VIAGND":   kind = SymbolKind.ViaGnd;       return true;
            case "TESTPAD":  kind = SymbolKind.TestPad;      return true;
            case "GSGPAD":
            case "GSG":      kind = SymbolKind.GsgPad;       return true;
            case "MIMCAP":   kind = SymbolKind.MimCap;       return true;
            case "SPIRAL":   kind = SymbolKind.Spiral;       return true;
            case "OSPIRAL":  kind = SymbolKind.OctSpiral;    return true;
            case "TFR":      kind = SymbolKind.Tfr;          return true;
            case "AIRBRIDGE": kind = SymbolKind.Airbridge;   return true;
            case "MBEND":
            case "MB":       kind = SymbolKind.MBend;        return true;
            case "MCURVE":
            case "MCV":      kind = SymbolKind.MCurve;       return true;
            case "MMITER":
            case "MMT":      kind = SymbolKind.MMiter;       return true;
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
            case "PFET_CURTICE": kind = SymbolKind.PFetCurtice;  return true;
            case "PFET_STATZ":   kind = SymbolKind.PFetStatz;    return true;
            case "PFET_MATERKA": kind = SymbolKind.PFetMaterka;  return true;
            // "NJF"/"PJF" are the SPICE card's own type names for the junction FET.
            case "NJF":
            case "NJFET":
            case "JFET_N": kind = SymbolKind.JfetN;         return true;
            case "PJF":
            case "PJFET":
            case "JFET_P": kind = SymbolKind.JfetP;         return true;
            case "NIGBT":
            case "IGBT_N": kind = SymbolKind.IgbtN;         return true;
            case "PIGBT":
            case "IGBT_P": kind = SymbolKind.IgbtP;         return true;
            case "BEAD":
            case "FB":     kind = SymbolKind.Bead;          return true;
            // "VDMOS" is the card's own type name; the channel comes from a bare keyword on the
            // card rather than from the type, so a bare "VDMOS" here means the n-channel tile.
            case "VDMOS":
            case "NVDMOS":
            case "VDMOS_N": kind = SymbolKind.VdmosN;        return true;
            case "PVDMOS":
            case "VDMOS_P": kind = SymbolKind.VdmosP;        return true;
            // "NMOS"/"PMOS" are the SPICE card's own type names and are mapped to level 1
            // deliberately: it is the only level circuitRF implements, so a bare card type has
            // exactly one answer. If a second level is added, a bare "NMOS" must keep meaning
            // level 1 — silently re-pointing it would change every design already written.
            case "NMOS":
            case "NMOS1":
            case "MOS1_N": kind = SymbolKind.Mos1N;         return true;
            case "NMOS3":
            case "MOS3_N": kind = SymbolKind.Mos3N;         return true;
            case "PMOS3":
            case "MOS3_P": kind = SymbolKind.Mos3P;         return true;
            case "PMOS":
            case "PMOS1":
            case "MOS1_P": kind = SymbolKind.Mos1P;         return true;
            case "NPN":
            case "BJT_NPN": kind = SymbolKind.BjtNpn;       return true;
            case "PNP":
            case "BJT_PNP": kind = SymbolKind.BjtPnp;       return true;
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
    /// True when the parameter editor's generic "+"/"−" indexed-group buttons apply to this type.
    ///
    /// <para><b>SDD is excluded even though it HAS a template</b> (owner report, 2026-09-02). Its
    /// usable slots depend on the port count and are spelled with TWO indices (<c>I[p,w]</c>), which
    /// the generic index parser cannot read — so "+" offered <c>I[n]</c>, which silently
    /// overwrote the seeded <c>I[n,0]</c> for a port that had one and was a hard run error
    /// ("references port 3 but only 2 port(s)") for a port that does not exist. It gets
    /// <see cref="SddEquationSlots"/> and its own picker instead. The template stays because it
    /// still drives row-name editing and canonical sorting.</para>
    /// </summary>
    public static bool AllowsIndexedParamAdd(SymbolKind kind)
        => kind is not SymbolKind.Sdd && UserParamTemplate(kind) is not null;

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
            SkipIndices:     [1],
            DefaultExpressions: ["50"]),

        // ToneSource: each group = Freq[n]/V[n]/Phase[n].  Tones start at 2 (tone 1 is the base
        //             scalar V/Freq that gets migrated to V[1]/Freq[1] on first add).
        SymbolKind.ToneSource => new IndexedParamGroup(
            NameFormats:     ["Freq[{0}]", "V[{0}]", "Phase[{0}]"],
            DefaultUnits:    ["GHz", "V", "deg"],
            ShowOnSchematic: [true, true, false],
            Dimensions:      [UnitDimension.Frequency, UnitDimension.Voltage, UnitDimension.Angle],
            FirstAddIndex:   2,
            SkipIndices:     null,
            DefaultExpressions: ["1", "1", "0"]),

        // ITone: the current dual of ToneSource's group — Freq[n]/I[n]/Phase[n], same indexing rule
        //        (tone 1 is the base scalar I/Freq, migrated to I[1]/Freq[1] on first add).
        SymbolKind.CurrentToneSource => new IndexedParamGroup(
            NameFormats:     ["Freq[{0}]", "I[{0}]", "Phase[{0}]"],
            DefaultUnits:    ["GHz", "mA", "deg"],
            ShowOnSchematic: [true, true, false],
            Dimensions:      [UnitDimension.Frequency, UnitDimension.Current, UnitDimension.Angle],
            FirstAddIndex:   2,
            SkipIndices:     null,
            DefaultExpressions: ["1", "1", "0"]),

        // PnTone: each group = Freq[n]/Pavl[n]/Phase[n] (the power-source analog of ToneSource). The
        //         component is seeded with tones 1 & 2 (DefaultParameters); "+" adds tone 3, 4, …
        //         FirstAddIndex=2 keeps tone 1 from being removed (it's always the first tone).
        SymbolKind.PnTone => new IndexedParamGroup(
            NameFormats:     ["Freq[{0}]", "Pavl[{0}]", "Phase[{0}]"],
            DefaultUnits:    ["GHz", "dBm", "deg"],
            ShowOnSchematic: [true, true, false],
            Dimensions:      [UnitDimension.Frequency, UnitDimension.Power, UnitDimension.Angle],
            FirstAddIndex:   2,
            SkipIndices:     null,
            DefaultExpressions: ["1", "0", "0"]),

        // ZPort is DELIBERATELY ABSENT (owner report, 2026-09-02). It used to add Z[n] scalars, and
        // the component cannot use one: ComponentModelFactory reads Z-matrix entries through
        // ^Z\[(\d+),(\d+)\]$ only, so a Z[n] matched nothing and landed in numericParams under a
        // name no expression can reference — inert in every path, on every run. Nor is there
        // anything for a "+" to add: the parameter set is exactly the N×N matrix its port count
        // fixes, and every entry is seeded at placement. A helper constant reaches Z[i,j]
        // expressions from a VAR component, which the elaborator already injects
        // (Elaborator.InjectZPortScopeVars).
        //
        // Being absent here also makes ZPort row names non-editable, which is the same fact from
        // the other side: a renamed Z[i,j] stops being read at all.

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

    // ── Named-value parameters: a closed set of NAMES, offered as a picker ────

    private static readonly string[] FilterResponseOptions =
        [nameof(FilterResponse.Butterworth), nameof(FilterResponse.Chebyshev),
         nameof(FilterResponse.InvChebyshev), nameof(FilterResponse.Bessel),
         nameof(FilterResponse.Gaussian), nameof(FilterResponse.Elliptic)];

    private static readonly string[] NetworkFormOptions =
        [nameof(NetworkForm.Lowpass), nameof(NetworkForm.Highpass),
         nameof(NetworkForm.Bandpass), nameof(NetworkForm.Bandstop)];

    private static readonly string[] CirculatorDirectionOptions =
        [nameof(CirculatorDirection.CW), nameof(CirculatorDirection.CCW)];

    private static readonly string[] SwitchOffStateOptions =
        [nameof(SwitchOffState.Reflective), nameof(SwitchOffState.Absorptive)];

    /// <summary>
    /// <c>State</c>'s legal values, per tile (owner, 2026-09-13): the numeral is the value, so these
    /// are committed exactly as typed and there is no enum to <c>nameof</c> against — the numbering
    /// IS <see cref="SwitchState"/>'s and <see cref="SwitchThrow"/>'s, which exist to match it.
    ///
    /// <para><b>The SPDT's list has THREE entries, not two.</b> Its <c>State</c> names which throw
    /// is closed — 1 or 2 — with 0 opening both, so a two-entry picker would leave the second throw
    /// unreachable from the dialog, which is the position half the reason to place an SPDT at all.
    /// The SPST has only one throw and so really is 0 or 1.</para>
    /// </summary>
    private static readonly string[] SwitchStateOptions  = ["0", "1"];
    private static readonly string[] SwitchDStateOptions = ["0", "1", "2"];

    private static readonly string[] Ip3ReferenceOptions =
        [nameof(Ip3Reference.Input), nameof(Ip3Reference.Output)];

    /// <summary>The Tuner family's <c>BiasTee</c> switch. Not an enum anywhere — the engine
    /// string-compares the resolved value against "on" (<see cref="ComponentModelFactory"/>'s
    /// tuner branch), so these two spellings ARE the contract and there is no type to
    /// <c>nameof</c> against. Committed bare, exactly as
    /// <see cref="DefaultParameters"/> writes the default: <c>CnlReader</c> is what quotes it on
    /// the way to the elaborator, so a quoted spelling here would arrive double-quoted.</summary>
    private static readonly string[] BiasTeeOptions = ["off", "on"];

    /// <summary>The Tuner family's <c>ShowBias</c> switch — display-only, dropped at extraction
    /// (<c>NetExtractor</c>), read back by <c>EditableComponent.GetBoolParam</c> as a
    /// case-insensitive "true". Lower-case to match the default and every other boolean parameter
    /// the schematic writes.</summary>
    private static readonly string[] ShowBiasOptions = ["false", "true"];

    /// <summary>A test pad's <c>Feed</c> and <c>Centre</c> (brief-dfr26-7): the spellings <c>TestPadPCell</c> reads.</summary>
    private static readonly string[] TestPadFeedOptions = [TestPadPCell.ViaFeed, "N", "E", "S", "W"];
    private static readonly string[] TestPadCentreOptions = [TestPadPCell.CentreOpen, TestPadPCell.CentrePort];

    /// <summary>
    /// The closed set of NAMES a parameter accepts, for a parameter whose value is an enum name
    /// rather than a number — <c>Response</c>, <c>Form</c>, <c>Direction</c>, <c>OffState</c>,
    /// <c>IP3Ref</c> — or, for the switches' <c>State</c>, a closed set of NUMERALS. The Parameter
    /// Editor renders these as a picker committing the entry verbatim, which is what the model reads.
    ///
    /// <para><b>Not <see cref="EnumParamOptions"/>, and the difference is the stored value.</b> That
    /// one is for a parameter whose value is a raw INDEX (MBend's <c>Miter</c> is 0/1/2 on the
    /// schematic label and in the netlist); this one's value IS the name, so there is no index to
    /// read out beside it and no translation to explain.</para>
    ///
    /// <para><b>Every name here is spelled with <c>nameof</c> against the enum the factory parses
    /// it with</b>, deliberately: a hand-typed option that no longer matches its enum member is not
    /// a broken picker, it is a picker that silently commits a value
    /// <c>ComponentModelFactory.EnumNamed</c> cannot parse — which reads as the DEFAULT, with no
    /// message, which is the whole class of bug this replaces.</para>
    ///
    /// <para>Returns null (the ordinary text box) for every other parameter. The SnP and wBond
    /// name-valued parameters are absent on purpose: both components replace the generic rows with
    /// their own panel, which already offers those as pickers.</para>
    /// </summary>
    public static IReadOnlyList<string>? NamedParamOptions(SymbolKind kind, string paramName) => (kind, paramName) switch
    {
        (SymbolKind.Circulator, "Direction")                      => CirculatorDirectionOptions,
        (SymbolKind.Switch or SymbolKind.SwitchD, "OffState")     => SwitchOffStateOptions,
        // The switch position (owner, 2026-09-13). A closed set of numerals rather than a free-text
        // box: a State naming a throw that does not exist closes nothing SILENTLY, by the model's own
        // rule, so a typo'd "3" is an open switch with nothing said anywhere. An expression — the
        // variable or sweep this parameter is a plain number in order to support — is still reachable
        // through the schematic label, which is why State now shows there.
        (SymbolKind.Switch, "State")                              => SwitchStateOptions,
        (SymbolKind.SwitchD, "State")                             => SwitchDStateOptions,
        (SymbolKind.Filter, "Response")                           => FilterResponseOptions,
        (SymbolKind.Filter, "Form")                               => NetworkFormOptions,
        (SymbolKind.Duplexer, "TxResponse" or "RxResponse")       => FilterResponseOptions,
        (SymbolKind.Duplexer, "TxForm" or "RxForm")               => NetworkFormOptions,
        (SymbolKind.Amp, "IP3Ref")                                => Ip3ReferenceOptions,
        // The Tuner family (owner request, 2026-09-01). Both parameters accept a closed set of two
        // spellings and neither is a number, so a free-text box could only ever be got wrong — a
        // typo'd "ON " or "0" reads as the default with nothing said anywhere, which is exactly the
        // class of bug this picker exists to remove. All three tiles share one parameter list.
        (SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner, "BiasTee")  => BiasTeeOptions,
        (SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner, "ShowBias") => ShowBiasOptions,
        // A via's capacitance switch: the same two words, turned into 1/0 at extraction
        // (ViaSubstrateInjection.FlagExpression), since a bare word in an expression is a name.
        (SymbolKind.Via or SymbolKind.ViaGnd, "IncludeC")                                => ShowBiasOptions,
        // A test pad's feed side and what its centre is (brief-dfr26-7): closed sets of words the generator reads.
        (SymbolKind.TestPad, "Feed")                                                     => TestPadFeedOptions,
        (SymbolKind.TestPad, "Centre")                                                   => TestPadCentreOptions,
        // A composed FET's instance switches (brief-modelcomposer-21): yes or no. A switch bound to an expression keeps a
        // text box (ParameterRowViewModel), since a picker cannot hold one.
        (SymbolKind.ComposedFET, _) when ComposedSwitches.IsSwitch(paramName)                => ComposedSwitchOptions,
        _ => null,
    };

    // ── Signal/Ground layer override parameters (brief-technology-editor-units-and-layers.md R-tec-6) ──

    /// <summary>
    /// R-tec-6/7/8: every microstrip component's per-instance layer-selection override — the PCell
    /// contract's own R11 ("Signal Layer + Ground Reference, defaulting from the stackup and
    /// per-instance overridable") already had its DEFAULT half implemented
    /// (<see cref="CircuitRF.Design.Layout.PCells.SubstrateResolver"/>) and its OVERRIDE plumbing already
    /// present (<see cref="CircuitRF.Design.Layout.PCells.PCellLayerSelection"/>,
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
    /// <summary>The via components' four dimensions, empty by default (follow the technology), and
    /// the two capacitance controls. <c>C</c> empty is the computed value; a number replaces it.</summary>
    private static readonly IReadOnlyList<DefaultParam> ViaDimensionParams =
    [
        new("Drill",    "",     "mm", true,  UnitDimension.Length),
        new("Pad",      "",     "mm", false, UnitDimension.Length),
        new("Antipad",  "",     "mm", false, UnitDimension.Length),
        new("Plating",  "",     "µm", false, UnitDimension.Length),
        new("IncludeC", "true", "",   false, UnitDimension.None),
        new("C",        "",     "pF", false, UnitDimension.Capacitance),
    ];

    public static readonly IReadOnlyList<DefaultParam> SignalGroundLayerParams =
    [
        new("SignalLayer", "", "", false, UnitDimension.None),
        new("GroundReference", "", "", false, UnitDimension.None),
    ];

    /// <summary>Which of the two layer-choice roles (if any) a given (owner, paramName) pair is —
    /// null for every other parameter. Reused by <see cref="MicrostripSubstrateInjection.IsMicrostripKind"/>'s
    /// own kind set rather than a second, duplicate list.</summary>
    public enum LayerChoiceKind { Signal, Ground }

    /// <summary>
    /// True for a parameter naming SEVERAL conductors as comma-separated text — a test pad's <c>ClearLayers</c>
    /// (brief-dfr27-1 R-dfr27-1-2). The parameter dialog offers the technology's conductors with a check box each
    /// and writes the same text, so a file written before the checklist reads unchanged.
    /// </summary>
    public static bool IsLayerChecklistParameter(SymbolKind kind, string paramName)
        => kind == SymbolKind.TestPad && paramName == TestPadInjection.ClearLayersParam;

    public static LayerChoiceKind? LayerChoiceKindFor(SymbolKind kind, string paramName)
    {
        // A via's two ends take any conductor; its GroundLayer only a ground-designated one, as a
        // microstrip's GroundReference does.
        if (ViaSubstrateInjection.IsViaKind(kind))
            return paramName switch
            {
                ViaSubstrateInjection.FromLayerParam or ViaSubstrateInjection.ToLayerParam => LayerChoiceKind.Signal,
                ViaSubstrateInjection.GroundLayerParam => LayerChoiceKind.Ground,
                _ => null,
            };
        // A test pad's conductor and its via's far end take any conductor, as a via's two ends do. ClearLayers names
        // several, so it is a checklist instead (IsLayerChecklistParameter).
        if (kind == SymbolKind.TestPad)
            return paramName is TestPadInjection.PadLayerParam or TestPadInjection.ViaFromParam ? LayerChoiceKind.Signal : null;
        // A broadside pair's two strips each take a signal conductor.
        if (BroadsideSubstrateInjection.IsBroadsideKind(kind))
            return BroadsideSubstrateInjection.LayerParams.Contains(paramName) ? LayerChoiceKind.Signal : null;
        if (!PlanarLineSubstrateInjection.IsStackupLineKind(kind)) return null;
        return paramName switch
        {
            "SignalLayer"     => LayerChoiceKind.Signal,
            "GroundReference" => LayerChoiceKind.Ground,
            _ => null,
        };
    }
}
