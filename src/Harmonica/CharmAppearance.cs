using System.Globalization;

namespace CircuitRF.Harmonica;

/// <summary>
/// R-h45-12 — the appearance block of a <c>.charm</c>: the resolved <c>Harmonica.*</c> role map for
/// <b>both</b> variants, plus the §7.2 iso-line fade parameters and the label toggle.
///
/// <para><b>Why the colours live in the .charm and not in a named .ccolor</b> (§7.9.4, and this
/// diverges from circuitRF's own Layer 3 deliberately): circuitRF records a theme NAME in the
/// <c>.cws</c> and resolves it against workspace → user → Assets. harmonicaRF runs with <b>no
/// workspace open</b> and ships as a standalone app, so a name-plus-search-path scheme has nothing to
/// resolve against. The <c>.charm</c> therefore embeds the resolved map, matching §8.1's "a .charm is
/// self-describing" rule. <c>.ccolor</c> import/export is still offered from Preferences (H7) — it
/// goes through <c>ColorThemeIo</c>, so this type owes it nothing.</para>
///
/// <para><b>This type carries no role vocabulary.</b> <c>src/Harmonica</c> is on the framework-free
/// side of the wall and cannot see <c>ColorRole</c>, which lives in <c>src/Ui/Theming</c>. So the map
/// is <c>role-name → "r,g,b,a"</c> — plain data, in the SAME comma-separated convention
/// <see cref="CharmIo.TerminationsToJson"/> already uses one file over. <c>src/Ui</c> owns the
/// mapping in exactly one place (<c>HarmonicaAppearanceBridge</c>) and this file stays ignorant of
/// what any particular role means.</para>
///
/// <para><b>Absent means default, never empty.</b> A role missing from a stored map falls back to the
/// built-in default (<c>ColorTheme.Resolve</c>'s own rule), and an absent block entirely means "this
/// document has never been recoloured". That is what makes an old <c>.charm</c> open unchanged after
/// new roles are added, and it is the same nullable-defaulted rule the rest of the format follows.
/// </para>
/// </summary>
public sealed record CharmAppearance
{
    /// <summary>Resolved <c>Harmonica.*</c> roles for the LIGHT variant, as <c>"r,g,b,a"</c>.</summary>
    public IReadOnlyDictionary<string, string> Light { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Resolved <c>Harmonica.*</c> roles for the DARK variant, as <c>"r,g,b,a"</c>.</summary>
    public IReadOnlyDictionary<string, string> Dark { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>§7.2's α_floor. Null = the built-in default.</summary>
    public double? IsoAlphaFloor { get; init; }

    /// <summary>§7.2's shaping exponent <c>p</c>. Null = the built-in default.</summary>
    public double? IsoAlphaExponent { get; init; }

    /// <summary>D11 — iso-line labels default OFF. Null = the built-in default (false).</summary>
    public bool? ShowIsoLineLabels { get; init; }

    /// <summary>R-h9b-7 — the Γ grid-point dots. Same shape of display-only toggle as
    /// <see cref="ShowIsoLineLabels"/>: null = the built-in default, which is OFF.</summary>
    public bool? ShowGridPoints { get; init; }

    /// <summary>brief-harmonicarf-r5 §1 — the diagnostics overlay HUD. Same shape of display-only
    /// toggle as <see cref="ShowGridPoints"/>: null = the built-in default, which is OFF (guardrail 6
    /// — it must cost nothing measurable, and "on by default" would mean every document pays for the
    /// rolling-window bookkeeping whether anyone is looking at it or not).</summary>
    public bool? ShowDiagnosticsOverlay { get; init; }

    /// <summary>brief-harmonicarf-r6d §4 — the power-sweep panel's own title fly menu: Power Sweep
    /// (false) or Time Domain (true). Same shape as <see cref="ShowGridPoints"/>: null = the built-in
    /// default, which is Power Sweep (false).</summary>
    public bool? ShowPowerSweepTimeDomain { get; init; }

    /// <summary>
    /// R-h9c-7 (R1C §5) — the readout strip's per-row Z/Γ format, keyed by
    /// <c>HarmonicaReadout.FormatKey</c> ("S1.Z", "L2.Gamma", "MXP.Zin", …), each value either
    /// <c>"RealImaginary"</c> or <c>"MagnitudeAngle"</c>. Display-only — the same shape of setting as
    /// <see cref="ShowGridPoints"/>, absent ⇒ default (real/imaginary). A missing or malformed entry
    /// falls back to the default rather than failing the load, matching
    /// <see cref="TryDecode"/>'s own "never guess, but never refuse to open" rule.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadoutFormats { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True when the document carries nothing worth writing — the ordinary case for a
    /// <c>.charm</c> nobody has recoloured. Used to omit the block entirely so an untouched file
    /// re-serialises byte-for-byte.</summary>
    public bool IsDefault
        => Light.Count == 0 && Dark.Count == 0
        && IsoAlphaFloor is null && IsoAlphaExponent is null && ShowIsoLineLabels is null
        && ShowGridPoints is null && ShowDiagnosticsOverlay is null && ShowPowerSweepTimeDomain is null
        && ReadoutFormats.Count == 0;

    public static readonly CharmAppearance Default = new();

    // ── the colour encoding, in ONE place ─────────────────────────────────────

    /// <summary>Encodes one RGBA as the stored <c>"r,g,b,a"</c> form.</summary>
    public static string Encode(byte r, byte g, byte b, byte a)
        => string.Create(CultureInfo.InvariantCulture, $"{r},{g},{b},{a}");

    /// <summary>
    /// Decodes a stored <c>"r,g,b,a"</c>. A three-component value is accepted as fully opaque, so a
    /// hand-edited file need not spell out an alpha it does not care about. Anything else is
    /// REFUSED (returns false) rather than guessed at — a colour silently read as black is exactly
    /// the kind of defect that reads as a rendering bug much later.
    /// </summary>
    public static bool TryDecode(string? value, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0; a = 255;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string[] parts = value.Split(',');
        if (parts.Length is not (3 or 4)) return false;

        if (!TryByte(parts[0], out r) || !TryByte(parts[1], out g) || !TryByte(parts[2], out b))
            return false;
        if (parts.Length == 4 && !TryByte(parts[3], out a)) return false;
        return true;

        static bool TryByte(string s, out byte v)
        {
            v = 0;
            if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                return false;
            if (i is < 0 or > 255) return false;
            v = (byte)i;
            return true;
        }
    }
}
