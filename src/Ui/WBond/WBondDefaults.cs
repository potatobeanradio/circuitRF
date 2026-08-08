using CircuitRF.Ui.Theming;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// What a newly drawn wire gets (wbond.md §6.4) — read from this installation's preferences, with the
/// shipped defaults as the fallback.
///
/// <para><b>One resolver, not a default repeated at each call site.</b> The creation gesture, the
/// Settings page and any future scripted creation all read the same three values here, so a shop that
/// bonds with 0.7 mil aluminium changes them once and every route agrees.</para>
/// </summary>
public static class WBondDefaults
{
    /// <summary>Points per wire — the profile's own resolution (§6.4).</summary>
    public const int ShippedPoints = 7;

    /// <summary>1 mil, the RF packaging norm.</summary>
    public static long ShippedDiameterNm => WBondUnits.ToNm(1.0, WBondUnit.Mil);

    /// <summary>
    /// Gold — both the packaging norm and the metal of <c>mom-wirebond-kernel.md</c>'s LW1 validation
    /// set, so the shipped default and the validated path agree.
    /// </summary>
    public static string ShippedMaterial => WireMaterials.Default.Name;

    public static int Points => Clamp(AppPreferencesIo.Load().WBondWirePoints);

    public static long DiameterNm
    {
        get
        {
            long? stored = AppPreferencesIo.Load().WBondWireDiameterNm;
            return stored is > 0 ? stored.Value : ShippedDiameterNm;
        }
    }

    public static string Material
    {
        get
        {
            string? stored = AppPreferencesIo.Load().WBondWireMaterial;
            return string.IsNullOrWhiteSpace(stored) ? ShippedMaterial : stored;
        }
    }

    /// <summary>
    /// A stored point count is clamped rather than trusted. Two points is a straight chord with no
    /// loop at all and the array reduction has nothing to integrate along; an absurdly large count
    /// costs fill time quadratically for no accuracy. A hand-edited preferences file is the one route
    /// that can carry either.
    /// </summary>
    internal static int Clamp(int? stored) =>
        stored is null ? ShippedPoints : stored.Value < 3 ? 3 : stored.Value > 101 ? 101 : stored.Value;
}
