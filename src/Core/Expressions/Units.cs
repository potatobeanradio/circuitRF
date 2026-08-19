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
        "V",  "kV", "mV", "uV", "nV",     // voltage
        "A",  "mA", "uA", "nA",            // current
        "W",  "mW", "uW", "kW",            // power (linear)
        "dB", "dBm", "dBc", "dBW",         // logarithmic / measurement
        "%",                                 // percentage
        // "nm" and "cm" USED to sit here, with the comment "length not in linear table". That was
        // never true of the physics — a nanometre is 1e-9 metres, not a dimensionless marker — and
        // it made Scale("nm") return null, which Evaluator.ApplyUnit turns into a multiplier of
        // exactly 1. They now live in _scales with their real values.
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
        // Length. Every one of these maps to "metre" — the scale-1 base symbol — INCLUDING "mil",
        // "in" and "inch", which are not SI-prefixed but are still lengths and still need a base
        // whose scale is 1.0. "mil" was absent from this map entirely, so BaseUnit("mil") returned
        // "mil" and Scale of that is 2.54e-5, not 1.0.
        //
        // "m" is deliberately NOT here: it is the SI prefix milli, not a length (see _scales).
        { "mm",  "metre" }, { "um",   "metre" }, { "nm", "metre" }, { "cm", "metre" },
        { "mil", "metre" }, { "in",   "metre" }, { "inch", "metre" },
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
    /// FIELD and the parser has no unit-suffix production — <c>Parser.Parse("2 GHz")</c> is a parse
    /// error at the 'GHz'. A netlist has no separate field to put it in, so the .cnl reader has
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
}
