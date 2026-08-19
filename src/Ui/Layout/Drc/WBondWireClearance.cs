// Where the built-in wire-to-wire clearance is stored, and what a stored value means.
//
// ── Why a PREFERENCE and not a property of the design ──────────────────────────────────────────
//
// The obvious alternative is the `.wasm`, and that is exactly where a HOUSE's clearance belongs
// (`wire_spacing(all) >= 2mil` already says it). This one is not a house's: it is the guard band
// circuitRF applies to a design that has referenced no rule file at all, so storing it in the very
// document whose absence it exists to cover is circular.
//
// The remaining candidates are the design and the user. It follows `AppPreferences.CheckDrcOnExport`
// — per USER — for that setting's own reason: a workspace arriving from someone else must not
// silently loosen a check you rely on. A design that genuinely needs a different number needs a
// `.wasm`, which is the answer the panel points at.

using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.Ui.Theming;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// The built-in rule's clearance, in nanometres — stored in mil, because mil is the unit it is
/// stated, shown and edited in (a bonder is set up in mil).
/// </summary>
public static class WBondWireClearance
{
    /// <summary>
    /// Test seam of the shape <see cref="Em.EmSolveCores.TestOverrideActive"/> already established
    /// here. Without it a UI test that exercises the control writes the developer's REAL preferences
    /// file, which is a side effect no test should have.
    /// </summary>
    internal static double? TestOverrideStore;
    internal static bool    TestOverrideActive;

    /// <summary>The stored clearance in nanometres, sanitised. Never null: an unusable stored value
    /// reads as the default rather than as "no rule".</summary>
    public static double Nm
    {
        get => Sanitise(TestOverrideActive ? TestOverrideStore : AppPreferencesIo.Load().WireClearanceMil);
        set
        {
            double mil = value / WBondUnits.NmPerUnit(WBondUnit.Mil);
            if (TestOverrideActive) TestOverrideStore = mil;
            else AppPreferencesIo.Update(p => p.WireClearanceMil = mil);
        }
    }

    /// <summary>The stored clearance as the user states it.</summary>
    public static double Mil
    {
        get => Nm / WBondUnits.NmPerUnit(WBondUnit.Mil);
        set => Nm = WBondUnits.ToNm(value, WBondUnit.Mil);
    }

    /// <summary>
    /// A stored value is sanitised rather than trusted. Null (never set) and anything not finite read
    /// as the default; a negative one reads as the default too, since a negative clearance would
    /// silently ask the checker to tolerate metal inside metal. Zero is honoured — it means "report
    /// only what actually collides" — and is held at
    /// <see cref="WBondBuiltInRules.MinimumClearanceNm"/>, which is what keeps EXACT contact
    /// reportable (see that member for why a limit of literally zero cannot find it).
    /// </summary>
    public static double Sanitise(double? storedMil)
    {
        if (storedMil is not { } mil || double.IsNaN(mil) || double.IsInfinity(mil) || mil < 0)
            return WBondBuiltInRules.DefaultClearanceNm;

        return Math.Max(WBondBuiltInRules.MinimumClearanceNm, WBondUnits.ToNm(mil, WBondUnit.Mil));
    }
}
