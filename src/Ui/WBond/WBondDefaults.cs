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

    /// <summary>
    /// The shipped paste pitch — 5 mil, which is what paste has always offset by (the coarse nudge
    /// step). Stated here rather than reached for at the call site, because it is now a setting.
    /// </summary>
    public static long ShippedPastePitchNm => WireEdits.CoarseNudgeNm;

    /// <summary>4 mil — the z a new wire's feet land at, stated once in <c>WBondEmbedding</c>.</summary>
    public static long ShippedFootZNm => WBondUnits.ToNm(WBondEmbedding.DefaultWire.FootZMils, WBondUnit.Mil);

    /// <summary>
    /// <b>Wire z-height</b> (Settings ▸ Wirebonds) — the z BOTH feet of a new wire land at.
    ///
    /// <para>One setting for two paths that had drifted apart (owner, 2026-08-17): the wires a new
    /// wBond component is created with, and the wires DRAWN in the layout view, which used to land at
    /// z = 0 because that view has no z axis to have meant anything by. <i>"Being consistent is more
    /// important than being right, and we can't guess what height the user wants the wire
    /// landings."</i></para>
    ///
    /// <para><b>Zero is honoured, unlike the diameter and point count beside it.</b> A foot at z = 0
    /// is a wire landing on the reference plane and a negative one is a foot in a cavity below it —
    /// both are geometry someone bonds, so "not set" here can only mean the JSON key being absent,
    /// never a value that happens to be zero.</para>
    ///
    /// <para>The PROFILE view's own wire tool deliberately does not read this: there the user clicks a
    /// z, which is the whole point of drawing in that view.</para>
    /// </summary>
    public static long FootZNm => AppPreferencesIo.Load().WBondWireFootZNm ?? ShippedFootZNm;

    /// <summary>
    /// How far the next PASTE is placed from what is already in the design (owner, 2026-08-16).
    /// Placement only — a paste never re-spaces the wires it is carrying.
    /// </summary>
    public static long PastePitchNm
    {
        get
        {
            long? stored = AppPreferencesIo.Load().WBondPastePitchNm;
            return stored is > 0 ? stored.Value : ShippedPastePitchNm;
        }
    }

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
