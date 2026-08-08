namespace CircuitRF.WBond;

/// <summary>
/// A bond-wire metal (wbond.md §2.3).
///
/// <para><b>Conductivity is stored at the 20 °C reference and evaluated at the operating
/// temperature</b> — the 85 °C figures quoted in the design note are <i>derived</i>, not a second
/// set of constants (WB4c). Storing them as literals would break <c>T = 20 °C</c> recovery, which
/// is the one way to get this wrong (brief-wbond-wba §0.3 item 6).</para>
/// </summary>
/// <param name="Name">Display name, and the key a <see cref="Wire"/> refers to.</param>
/// <param name="Sigma20">Conductivity at 20 °C, S/m.</param>
/// <param name="Alpha20">Temperature coefficient of resistance at 20 °C, 1/K.</param>
/// <param name="DensityKgM3">Mass density, kg/m³ — carried for future bond-strength/mass work.</param>
public sealed record WireMaterial(string Name, double Sigma20, double Alpha20, double DensityKgM3)
{
    /// <summary>
    /// Conductivity at <paramref name="tempC"/>: σ(T) = σ₂₀ / (1 + α₂₀·(T − 20)).
    /// At the shipped default of 85 °C this is 22–25 % below the 20 °C figure (WB4a).
    /// </summary>
    public double SigmaAt(double tempC) => Sigma20 / (1.0 + Alpha20 * (tempC - 20.0));
}

/// <summary>
/// The shipped metals and the default operating temperature.
///
/// <para><b>Why 85 °C is the default (WB4a).</b> A wire that carries current is never at room
/// temperature. 85 °C is itself optimistic for a high-power part, but it is far closer than the
/// handbook figure, and a default that is optimistic-but-close beats one that is wrong by a
/// quarter.</para>
///
/// <para><b>The RF penalty is about half the DC penalty (WB4b).</b> Deep in the skin regime
/// R_ac ∝ 1/√σ rather than 1/σ, so gold's 22 % DC rise becomes ~10.5 % once the current is confined
/// to a skin. <see cref="InternalImpedance"/> traverses the whole transition, so nothing here needs
/// special-casing — but a user comparing against a room-temperature hand calculation should expect
/// two different numbers depending on where they look.</para>
/// </summary>
public static class WireMaterials
{
    /// <summary>The shipped default operating temperature, °C (WB4a, owner decision 2026-08-07).</summary>
    public const double DefaultOperatingTempC = 85.0;

    /// <summary>4N gold — the RF packaging norm, and the metal of kernel W's validation set.</summary>
    public static readonly WireMaterial Gold      = new("Gold",      4.10e7, 0.0034, 19_300);

    /// <summary>Al-1%Si in practice, so σ runs ~5–8 % below pure aluminium.</summary>
    public static readonly WireMaterial Aluminium = new("Aluminium", 3.77e7, 0.0039,  2_700);

    /// <summary>Bare or Pd-coated; the coating is thin against δ above ~1 GHz.</summary>
    public static readonly WireMaterial Copper    = new("Copper",    5.80e7, 0.0039,  8_960);

    public static readonly WireMaterial Silver    = new("Silver",    6.30e7, 0.0038, 10_490);

    /// <summary>The default wire metal (WB4a / D7).</summary>
    public static WireMaterial Default => Gold;

    public static IReadOnlyList<WireMaterial> All { get; } =
        [Gold, Aluminium, Copper, Silver];

    /// <summary>Looks a metal up by name, case-insensitively. Returns null if unknown.</summary>
    public static WireMaterial? ByName(string name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
