namespace CircuitRF.Core.Netlist.Spice;

/// <summary>How an element crosses into the SPICE dialect (docs/design/transient.md §9.2.1, D7).</summary>
public enum SpiceTranslation
{
    /// <summary>Written with the same meaning, or written as an idiom whose meaning is the same.</summary>
    Exact,
    /// <summary>Written only where every parameter maps one to one; refused by parameter otherwise (a diode, a BJT).</summary>
    ByParameter,
    /// <summary>Refused by name unless a key asks for the approximate translation (<c>SpiceLines=rlgc</c>, <c>SpiceFit=yes</c>).</summary>
    OnRequest,
    /// <summary>Refused by name.</summary>
    Refused,
}

/// <summary>
/// One row of the element table: the circuitRF component types it covers, what the dialect writes for them, whether that
/// is exact, and what circuitRF's own reader makes of the written line again.
/// </summary>
/// <param name="Types">circuitRF component types (the elaborated <c>ComponentType</c>), compared case-insensitively.</param>
/// <param name="Dialect">The dialect's spelling, in words.</param>
/// <param name="Translation">Whether it is exact.</param>
/// <param name="ReadsBackAs">The component type circuitRF's reader produces from the written line, or null when the line is
/// not read back as one component (an idiom of several lines, a file included as it is).</param>
/// <param name="Note">What the row means, for the table in spice-models.md and for a refusal.</param>
public sealed record SpiceElementEntry(
    IReadOnlyList<string> Types, string Dialect, SpiceTranslation Translation, string? ReadsBackAs, string Note);

/// <summary>
/// The element table of docs/design/transient.md §9.2.1, as data (brief TRN-6 R-trn6-2). <see cref="SpiceDeckWriter"/> writes
/// from it, and <see cref="SpiceNetlistReader"/> and the source translation read the same rows back: the stimuli
/// (<see cref="Stimuli"/>) and the element letters a written deck uses (<see cref="Letters"/>), so a mapping exists in both
/// directions or in neither. Exact entries only (D7): nothing here is "close enough".
/// </summary>
public static class SpiceElementTable
{
    /// <summary>The independent-source stimuli, both directions: the dialect's word, circuitRF's voltage type and its current
    /// type. SIN is a tone source with its transient keys (Delay, Damping).</summary>
    public static readonly IReadOnlyList<(string Stimulus, string VoltageType, string CurrentType)> Stimuli =
    [
        ("PULSE", "V_Pulse", "I_Pulse"),
        ("PWL",   "V_PWL",   "I_PWL"),
        ("EXP",   "V_Exp",   "I_Exp"),
        ("SFFM",  "V_SFFM",  "I_SFFM"),
        ("SIN",   "V_1Tone", "I_1Tone"),
    ];

    /// <summary>The element letters a written deck uses for a single circuitRF type, both directions: the reader turns a line
    /// with that letter into the type, and the writer writes the type with that letter.</summary>
    public static readonly IReadOnlyList<(char Letter, string Type)> Letters =
    [
        ('R', "R"),
        ('C', "C"),
        ('L', "L"),
        ('K', "Mutual"),
        ('T', "TLIN"),
    ];

    /// <summary>circuitRF's type for a stimulus, or null.</summary>
    public static string? TypeForStimulus(string stimulus, bool current)
    {
        string s = stimulus.Trim().ToUpperInvariant() == "SINE" ? "SIN" : stimulus.Trim().ToUpperInvariant();
        foreach (var row in Stimuli)
            if (row.Stimulus == s) return current ? row.CurrentType : row.VoltageType;
        return null;
    }

    /// <summary>The stimulus a circuitRF waveform or tone type is written as, or null.</summary>
    public static string? StimulusForType(string type)
    {
        foreach (var row in Stimuli)
            if (row.VoltageType.Equals(type, StringComparison.OrdinalIgnoreCase) ||
                row.CurrentType.Equals(type, StringComparison.OrdinalIgnoreCase))
                return row.Stimulus;
        return null;
    }

    /// <summary>The circuitRF type a dialect element letter reads as, or null when the letter has no single type.</summary>
    public static string? TypeForLetter(char letter)
    {
        char u = char.ToUpperInvariant(letter);
        foreach (var row in Letters)
            if (row.Letter == u) return row.Type;
        return null;
    }

    /// <summary>The element letter a circuitRF type is written with, or null when it has no single letter.</summary>
    public static char? LetterForType(string type)
    {
        foreach (var row in Letters)
            if (row.Type.Equals(type, StringComparison.OrdinalIgnoreCase)) return row.Letter;
        return null;
    }

    /// <summary>The rows, in the order of §9.2.1.</summary>
    public static readonly IReadOnlyList<SpiceElementEntry> Entries =
    [
        new(["R", "C", "L"], "R, C, L (IC= on C and L)", SpiceTranslation.Exact, null,
            "An inductor's series R and C become a resistor and a capacitor in series on an internal node. A resistor's TC1/TC2 "
          + "are resolved at its temperature before writing."),
        new(["Mutual"], "K, the coupling coefficient M/√(L1·L2)", SpiceTranslation.Exact, "Mutual", "Reads back as M = k·√(L1·L2)."),
        new(["Transformer"], "a VCVS and a CCCS: E across the primary, F into the secondary", SpiceTranslation.Exact, null,
            "The ideal N:1 transformer, V1 = N·V2 and I2 = −N·I1."),
        new(["VCCS", "VCVS"], "G, E", SpiceTranslation.Exact, null, "The positional controlled sources."),
        new(["IProbe"], "a zero-volt V, its current read back", SpiceTranslation.Exact, "IProbe", ""),
        new(["Port", "Term"], "R of its Z", SpiceTranslation.Exact, "R",
            "An undriven port is its termination. A complex or frequency-dependent Z is refused."),
        new(["Vdc", "Idc"], "V, I with DC", SpiceTranslation.Exact, null,
            "The dialect's I sinks from its first node and circuitRF's sources deliver into theirs, so I is written on the nets "
          + "swapped."),
        new(["V_Pulse", "V_PWL", "V_Exp", "V_SFFM", "I_Pulse", "I_PWL", "I_Exp", "I_SFFM"], "PULSE, PWL, EXP, SFFM",
            SpiceTranslation.Exact, null, "A File= PWL is written inline."),
        new(["V_Expr", "I_Expr"], "B, an expression in time", SpiceTranslation.Exact, null, ""),
        new(["V_1Tone", "I_1Tone", "P1Tone"], "SIN with its delay and damping (B with a Ramp); a P1Tone's Z in series",
            SpiceTranslation.Exact, null,
            "A P1Tone's amplitude is from its available power into its Z. A harmonic termination Z[k] other than Z[1] is refused."),
        new(["TLIN"], "T, Z0 and TD", SpiceTranslation.Exact, "TLIN", "A lossless ideal line. A lossy TLIN is a line of §9.2.3."),
        new(["VSwitch"], "B, a conductance with the same transition", SpiceTranslation.Exact, null,
            "ln g follows the cubic smoothstep in the control voltage. Hysteresis is refused."),
        new(["SDD"], "B current sources; each charge a B voltage source driving a 1 F capacitor", SpiceTranslation.Exact, null,
            "The inverse of the charge idiom spice-models.md §8.8 reads. A function with no dialect spelling refuses the instance."),
        new(["SpiceModel"], "the file included as it is, its subcircuit called", SpiceTranslation.Exact, null,
            "By construction: the dialect reads the file circuitRF read."),
        new(["VerilogA"], "N, on the same compiled .osdi, loaded by the dialect's OSDI command", SpiceTranslation.Exact, null,
            "The build for this platform (OsdiBuilds). A .va source is refused until it is compiled."),
        new(["Diode", "BJT_NPN", "BJT_PNP", "MOS1_N", "MOS1_P", "MOS3_N", "MOS3_P"], "D, Q, M with a .model card",
            SpiceTranslation.ByParameter, null, "spice-models.md §15 tabulates every parameter."),
        new(["MBEND", "MGAP", "MTEE", "MCROSS"], "L, C", SpiceTranslation.Exact, null,
            "Exact where the model is a fixed lumped network of geometry alone (spice-models.md §15.6); refused otherwise unless "
          + "SpiceFit=yes."),
        new(["MLIN", "SLIN", "CPWG", "MCURVE", "MTAPER", "MKLOPF"], "TXL", SpiceTranslation.OnRequest, null,
            "Per-unit-length R, L, G, C fitted at SpiceLineFreq with SpiceLines=rlgc; a lossy TLIN too."),
        new(["MCLIN", "CLIN"], "CPL", SpiceTranslation.OnRequest, null,
            "Per-unit-length matrices fitted at SpiceLineFreq with SpiceLines=rlgc. The dialect's CPL takes diagonal R and G only."),
        new(["SnP", "Filter", "Duplexer"], "a subcircuit of circuitRF's fit", SpiceTranslation.OnRequest, null, "SpiceFit=yes."),
    ];

    /// <summary>The row naming <paramref name="componentType"/>, or null.</summary>
    public static SpiceElementEntry? For(string componentType)
    {
        foreach (var e in Entries)
            foreach (var t in e.Types)
                if (t.Equals(componentType, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }
}
