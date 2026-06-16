namespace CircuitRF.Core.Expressions;

/// <summary>
/// Linear unit-scale table (§8). A unit suffix multiplies the resolved value.
/// dB/dBm are measurement functions, not unit suffixes, and are absent here.
/// </summary>
public static class Units
{
    private static readonly Dictionary<string, double> _scales = new(StringComparer.Ordinal)
    {
        // SI prefixes (standalone, e.g. "M" as a multiplier)
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
        { "kOhm", 1e3  },
        { "MOhm", 1e6  },
        { "GOhm", 1e9  },
        // Length
        { "mm",  1e-3  },
        { "um",  1e-6  },
        { "mil", 2.54e-5 },
        // Angle
        { "deg", Math.PI / 180.0 },
        { "rad", 1.0   },
        // Dimensionless / identity
        { "Ohms", 1.0  },  // alternate spelling seen in netlists
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
        "nm", "cm",                          // length not in linear table
        "%",                                 // percentage
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
}
