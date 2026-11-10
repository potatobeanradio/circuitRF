namespace CircuitRF.Core.Expressions;

/// <summary>
/// Linear unit-scale table (§8). A unit suffix multiplies the resolved value.
/// dB/dBm are measurement functions, not unit suffixes, and are absent here.
/// </summary>
public static class Units
{
    private static readonly Dictionary<string, double> _scales = new(StringComparer.Ordinal)
    {
        // SI prefixes (standalone, e.g. "M" as a multiplier).
        //
        // "m" is MILLI here, not the metre — a deliberate decision (brief-core-length-units §5 q1,
        // owner's call). Re-pointing it at 1.0 was the obvious fix for the length table below and
        // was rejected: a bare-prefix value in a hand-authored .cnl ("C=1m" meaning one millifarad)
        // would silently become a thousand times larger, with nothing anywhere reporting it. The
        // metre therefore gets its own symbol, "metre", in the Length block. Do NOT "tidy" these
        // into agreement.
        { "T",   1e12  },
        { "G",   1e9   },
        { "M",   1e6   },
        { "k",   1e3   },
        { "m",   1e-3  },
        { "u",   1e-6  },
        { "n",   1e-9  },
        { "p",   1e-12 },
        { "f",   1e-15 },
        // Frequency
        { "Hz",  1.0   },
        { "kHz", 1e3   },
        { "MHz", 1e6   },
        { "GHz", 1e9   },
        { "THz", 1e12  },
        // Inductance
        { "H",   1.0   },
        { "mH",  1e-3  },
        { "uH",  1e-6  },
        { "nH",  1e-9  },
        { "pH",  1e-12 },
        { "fH",  1e-15 },
        // Capacitance
        { "F",   1.0   },
        { "mF",  1e-3  },
        { "uF",  1e-6  },
        { "nF",  1e-9  },
        { "pF",  1e-12 },
        { "fF",  1e-15 },
        // Resistance
        { "Ohm",  1.0  },
        { "ohm",  1.0  },
        { "kOhm", 1e3  },
        { "MOhm", 1e6  },
        { "GOhm", 1e9  },
        // A kit ties every unused package pin to ground through 1 TOhm, so the absence of this
        // one entry silently turned "TOhm" into a NET — one phantom node shared by fourteen
        // resistors, which then had no constraint of its own and made the whole matrix singular.
        { "TOhm", 1e12 },
        { "mOhm", 1e-3 },   // the other end of the same series, absent for the same reason
        // Conductance / transconductance. The base symbol is "S" (siemens); a VCCS's G is the only
        // parameter that uses it today. "S" is a plain unit, not an SI prefix, so it carries none of
        // the SplitTrailingUnit ambiguity the single-letter prefixes above do.
        { "S",   1.0   },
        { "kS",  1e3   },
        { "mS",  1e-3  },
        { "uS",  1e-6  },
        { "nS",  1e-9  },
        // Voltage, current and power — the PREFIXED spellings only.
        //
        // These used to sit in _identityUnits with no scale at all, which made Scale() return null,
        // which ApplyUnit turns into a multiplier of exactly 1: "Vdc=2 mV" resolved to TWO VOLTS and
        // "I=2 mA" to two amps. Same defect, same fix and same reasoning as nm/cm before them (see
        // the note in _identityUnits) — a millivolt is a thousandth of a volt, not a dimensionless
        // marker.
        //
        // The BASE symbols V, A and W deliberately stay in _identityUnits, where their multiplier of
        // 1 is already correct. Moving them here would flip IsKnown, which the conservative .cnl
        // token gates read — and "W" is this codebase's own name for a microstrip WIDTH, so
        // `L = 2 * W` would start splitting as an expression plus a unit. GenuineIdentityUnits_
        // AreStillIdentityOnly holds that line.
        { "kV",  1e3   },
        { "mV",  1e-3  },
        { "uV",  1e-6  },
        { "nV",  1e-9  },
        { "mA",  1e-3  },
        { "uA",  1e-6  },
        { "nA",  1e-9  },
        { "kW",  1e3   },
        { "mW",  1e-3  },
        { "uW",  1e-6  },
        // Length. "metre" is the scale-1 BASE symbol (see the SI-prefix note above for why it is not
        // "m"); it is what BaseUnit returns for every unit in this block, which is the property
        // ParametricSweepEngine's own re-attach depends on.
        { "metre", 1.0 },
        { "mm",  1e-3  },
        { "um",  1e-6  },
        { "cm",  1e-2  },
        { "nm",  1e-9  },
        { "mil", 2.54e-5 },
        { "in",   2.54e-2 },
        { "inch", 2.54e-2 },
        // Angle
        { "deg", Math.PI / 180.0 },
        { "rad", 1.0   },
        // Time — a controlled source's delay T and the Delay block's T (issue 55). Lower-case "s",
        // so it never meets the siemens ("S", "mS", "nS" …): the table is ordinal, and "ms" is a
        // millisecond while "mS" is a millisiemens. Before these existed, "1 us" on an instance line
        // was refused as an unknown unit, and so was the IGBT tile's own Tau default.
        { "s",   1.0   },
        { "ms",  1e-3  },
        { "us",  1e-6  },
        { "ns",  1e-9  },
        { "ps",  1e-12 },
        { "fs",  1e-15 },
        // Dimensionless / identity
        { "Ohms", 1.0  },  // alternate spelling seen in netlists
        { "ohms", 1.0  },  // lowercase variant
    };

    /// <summary>Returns the linear scale factor for a unit string, or null if unrecognised.</summary>
    public static double? Scale(string unit)
        => _scales.TryGetValue(unit, out var s) ? s : null;

    public static bool IsKnown(string unit) => _scales.ContainsKey(unit);

    // Identity/measurement units: valid in netlists but carry no scale multiplier.
    // Absent from _scales by design (see UnitNormalizer.cs "table-uncovered" comments).
    private static readonly HashSet<string> _identityUnits = new(StringComparer.Ordinal)
    {
        "V",                                // voltage      — base symbol, multiplier 1
        "A",                                // current      — base symbol, multiplier 1
        "W",                                // power        — base symbol, multiplier 1
        "dB", "dBm", "dBc", "dBW",         // logarithmic / measurement
        "%",                                 // percentage
        // "nm" and "cm" USED to sit here, with the comment "length not in linear table". That was
        // never true of the physics — a nanometre is 1e-9 metres, not a dimensionless marker — and
        // it made Scale("nm") return null, which Evaluator.ApplyUnit turns into a multiplier of
        // exactly 1. They now live in _scales with their real values.
        //
        // The PREFIXED voltage, current and power units (kV/mV/uV/nV, mA/uA/nA, kW/mW/uW) sat here
        // for the same wrong reason and moved out for the same right one on 2026-08-29: "2 mA" was
        // resolving to two amps. Only the three BASE symbols belong here now — see the block in
        // _scales for why moving those three as well would be a different bug.
    };

    /// <summary>
    /// Returns true for any unit token that should be consumed after a "key=value" param
    /// token: both linear-scale units (<see cref="IsKnown"/>) and identity/measurement
    /// units (V, A, W, dBm, dB, …) that carry no multiplier but are still valid units.
    ///
    /// Only call this when the candidate token follows a "key=value" token (position gate).
    /// A net token such as "Vout" can never appear in that position, so single-letter "V"
    /// is unambiguous here.
    /// </summary>
    public static bool IsRecognizedUnit(string unit)
        => IsKnown(unit) || _identityUnits.Contains(unit);

    /// <summary>
    /// True when <paramref name="unit"/> (engine spelling) may be glued to a number as a unit LITERAL — <c>10um</c>,
    /// <c>2.4GHz</c>, <c>2u</c>, <c>48V</c>: every linear-scale unit, including the bare SI prefixes (so <c>2m</c> is
    /// two MILLI, never two metres — the metre is <c>metre</c>), and the three scale-1 base symbols V, A and W.
    /// <c>2W</c> is two watts: it did not parse before literals existed, so no existing text changes meaning, but a
    /// user who meant <c>2*W</c> (a microstrip width) must write the <c>*</c>. The logarithmic units are refused by the
    /// tokenizer with their own message (<see cref="IsLogarithmic"/>).
    /// </summary>
    public static bool IsSuffixUnit(string unit) => IsKnown(unit) || unit is "V" or "A" or "W";

    /// <summary>dB, dBm, dBc, dBW — measurement units that are not multipliers (expressions.md §8: they are functions).</summary>
    public static bool IsLogarithmic(string unit) => unit is "dB" or "dBm" or "dBc" or "dBW";

    /// <summary>The multiplier a unit literal's number is scaled by: the linear scale, or 1 for V, A and W.</summary>
    public static double SuffixScale(string unit) => Scale(unit) ?? 1.0;

    private static readonly System.Text.RegularExpressions.Regex _gluedNumber = new(
        @"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)([A-Za-zΩµμ%°]+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Splits a WHOLE value written as one number with a unit glued to it — <c>1uF</c>, <c>2.5nH</c>, <c>1Ω</c> — into the
    /// number and the suffix as written (not normalised, not checked against the table: the caller decides which
    /// suffixes it lifts). Anything else — <c>10um + 1mil</c>, <c>2*w</c>, <c>Vs_mag</c> — is not split. This is the one
    /// home of the shape both the <c>.cnl</c> reader and the schematic's parameter editor lift; a splitter that looked
    /// only at a trailing run would tear <c>10um + 1mil</c> into <c>10um + 1</c> and <c>mil</c>.
    /// </summary>
    public static bool TrySplitGluedNumber(string s, out string number, out string suffix)
    {
        var m = _gluedNumber.Match(s);
        number = m.Success ? m.Groups[1].Value : s;
        suffix = m.Success ? m.Groups[2].Value : "";
        return m.Success;
    }

    // Maps prefixed units to their scale-1 base symbol.
    private static readonly Dictionary<string, string> _baseUnitMap = new(StringComparer.Ordinal)
    {
        // Inductance
        { "mH", "H" }, { "uH", "H" }, { "nH", "H" }, { "pH", "H" }, { "fH", "H" },
        // Capacitance
        { "mF", "F" }, { "uF", "F" }, { "nF", "F" }, { "pF", "F" }, { "fF", "F" },
        // Resistance (prefixed variants only; Ohm/ohm are already base)
        { "kOhm", "Ohm" }, { "MOhm", "Ohm" }, { "GOhm", "Ohm" },
        { "TOhm", "Ohm" }, { "mOhm", "Ohm" },
        // Voltage
        { "kV", "V" }, { "mV", "V" }, { "uV", "V" }, { "nV", "V" },
        // Current
        { "mA", "A" }, { "uA", "A" }, { "nA", "A" },
        // Power
        { "mW", "W" }, { "uW", "W" }, { "kW", "W" },
        // Conductance
        { "kS", "S" }, { "mS", "S" }, { "uS", "S" }, { "nS", "S" },
        // Length. Every one of these maps to "metre" — the scale-1 base symbol — INCLUDING "mil",
        // "in" and "inch", which are not SI-prefixed but are still lengths and still need a base
        // whose scale is 1.0. "mil" was absent from this map entirely, so BaseUnit("mil") returned
        // "mil" and Scale of that is 2.54e-5, not 1.0.
        //
        // "m" is deliberately NOT here: it is the SI prefix milli, not a length (see _scales).
        { "mm",  "metre" }, { "um",   "metre" }, { "nm", "metre" }, { "cm", "metre" },
        { "mil", "metre" }, { "in",   "metre" }, { "inch", "metre" },
        // Angle. "deg" was the one scaled unit missing here, so BaseUnit("deg") returned "deg" while its scale is
        // π/180: a value converted to SI (radians) was labelled degrees (brief-yield-15 R-ya15-5).
        { "deg", "rad" },
        // Time.
        { "ms", "s" }, { "us", "s" }, { "ns", "s" }, { "ps", "s" }, { "fs", "s" },
    };

    /// <summary>
    /// Returns the scale-1 base symbol for <paramref name="unit"/>:
    /// frequency units (Hz/kHz/MHz/GHz/THz) → "Hz"; SI-prefixed units → their base
    /// symbol (e.g. "mV"→"V", "pF"→"F", "kOhm"→"Ohm"); all others (dBm, V, Ohm, …)
    /// and empty/unknown strings pass through unchanged.
    /// </summary>
    public static string BaseUnit(string unit)
    {
        if (string.IsNullOrEmpty(unit)) return unit;
        if (unit is "kHz" or "MHz" or "GHz" or "THz") return "Hz";
        return _baseUnitMap.TryGetValue(unit, out var b) ? b : unit;
    }

    /// <summary>
    /// Splits an assignment right-hand side written with an INLINE unit — "2 GHz" → ("2", "GHz") —
    /// into the expression and the unit. Returns (<paramref name="rhs"/> trimmed, null) when the
    /// last whitespace-separated token is not a unit, so "a + b" and "50" are returned untouched.
    ///
    /// <para><paramref name="includeIdentityUnits"/> widens the last-token test from the
    /// linear-scale table to <see cref="IsRecognizedUnit"/>, so "48 V" also splits. The .cnl reader
    /// leaves it false — that is the rule netlists have always had — and callers that verify the
    /// split against the parser (see <c>NetExtractor</c>) set it true.</para>
    ///
    /// <para>The unit comes back in its ENGINE spelling: an editor glyph is normalized through
    /// <see cref="UnitNormalizer"/> first, so "50 Ω" splits and yields "Ohm". Only the candidate
    /// token is normalized, never the expression — 'Ω' and 'µ' are letters as far as the tokenizer
    /// is concerned, so a variable may legitimately be named with one.</para>
    ///
    /// <para>This is the single home for that rule. A unit properly belongs in the row's own unit
    /// FIELD, and the parser reads a unit only GLUED to a number (<c>2GHz</c>, a unit literal) —
    /// <c>Parser.Parse("2 GHz")</c> is a parse error at the 'GHz'. A netlist has no separate field to put it in, so the .cnl reader has
    /// always accepted the inline spelling and lifted it into <c>Variable.Unit</c>; the schematic
    /// VAR editor has a real unit column and did not, which made the identical text mean two
    /// different things in the two entry points — and the schematic one meant "silently no
    /// variable at all", since <c>Elaborator</c> skips a global it cannot resolve.</para>
    ///
    /// <para><b>The last-token test alone is not safe on its own.</b> Every bare SI prefix is a
    /// unit here, so "2 * f" ends in the femto- prefix and "R * m" in milli-. A netlist accepts
    /// that risk (it has no alternative spelling); a caller that does have one should split only
    /// when the unsplit text does not parse and the split text does.</para>
    /// </summary>
    public static (string Expression, string? Unit) SplitTrailingUnit(
        string rhs, bool includeIdentityUnits = false)
    {
        var tokens = rhs.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return (rhs.Trim(), null);
        var last = UnitNormalizer.ToEngineUnit(tokens[^1]);
        bool isUnit = includeIdentityUnits ? IsRecognizedUnit(last) : IsKnown(last);
        return isUnit ? (string.Join(" ", tokens[..^1]), last) : (rhs.Trim(), null);
    }

    /// <summary>
    /// Lifts a unit written inline in an assignment's EXPRESSION — <c>2 GHz</c>, <c>48 V</c> — into
    /// the unit the row would have carried had there been a unit column to type it in. Returns the
    /// expression unchanged with a null unit when there is nothing to lift.
    ///
    /// <para><b>The split is verified against the parser, not just the unit table</b>, which is what
    /// makes the wide table safe. Every bare SI prefix is a unit name here, so a purely token-based
    /// rule tears <c>2 * f</c> into <c>2 *</c> + femto and <c>R * m</c> into <c>R *</c> + milli —
    /// expressions that are perfectly legal. So: leave anything that already parses completely alone,
    /// and accept a split only when it turns text the parser rejects into text it accepts. A SPACED
    /// unit always produces a parse error (juxtaposition is not an operator in this grammar), so
    /// this is reachable by exactly the assignments it is for and unreachable by every one that was
    /// already working. A GLUED unit (<c>2GHz</c>) is a unit literal since 2026-09-27: it parses, so it
    /// is kept as written — the same value, still unit-bearing.</para>
    ///
    /// <para><b>Why the two entry points share it (AUT-8 R-aut8-6).</b> The reference page states,
    /// without qualification, that a variable may carry a unit, and <c>RFfreq = 2 GHz</c> is its own
    /// worked example. <c>VDS = 48 V</c> was
    /// <c>Parse error at position 6: Unexpected token 'V'</c> — because the <c>.cnl</c> reader passed
    /// <c>includeIdentityUnits: false</c> and so lifted only the linear-SCALE units, while the
    /// schematic's VAR path (<c>NetExtractor.LiftInlineUnit</c>, which this replaces) lifted both. One
    /// documented rule cannot have two implementations that disagree about which units count, so
    /// there is now one implementation and the rule is the wider one.</para>
    /// </summary>
    public static (string Expression, string? Unit) LiftInlineUnit(string expression)
    {
        try
        {
            Parser.Parse(expression);
            return (expression, null);      // already a valid expression — nothing to lift
        }
        catch (ExpressionException)
        {
            var (expr, unit) = SplitTrailingUnit(expression, includeIdentityUnits: true);
            if (unit is null) return (expression, null);
            try { Parser.Parse(expr); }
            catch (ExpressionException) { return (expression, null); }   // split didn't help
            return (expr, unit);                 // already the engine spelling
        }
    }
}
