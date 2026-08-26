// Where the EM solver's core cap is STORED — the half of the old EmSolveCores that could not cross
// the UI firewall (brief-cli-em-verb.md R-emcli-3).
//
// R-emp-6 is unchanged: the cap is a property of the MACHINE and not of the design, so it lives in
// AppPreferences and never in the `.cem`. What changed is that the RUN no longer reaches for it —
// EmRunService.Run takes the cap as an argument, this is what the GUI fills it from, and a headless
// run passes nothing (Automatic). See CircuitRF.Design.Layout.Em.EmSolveCores for the choice list,
// the clamp and the labels, all of which are pure and moved with the run service.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>The stored core cap. <b>Null means Automatic</b>, which maps to
/// <c>PlanarSolveSettings.MaxDegreeOfParallelism = null</c> — the unbounded behaviour every run had
/// before this control existed.</summary>
public static class EmSolveCorePreference
{
    /// <summary>
    /// Test seam of the shape <c>SkiaFonts.TestOverrideTypeface</c> already established here. Without
    /// it a UI test that exercises the control writes the developer's REAL preferences file, which is
    /// a side effect no test should have.
    /// </summary>
    internal static int?  TestOverrideStore;
    internal static bool  TestOverrideActive;

    /// <summary>The stored cap, sanitised. Null = automatic.</summary>
    public static int? Preferred
    {
        get => EmSolveCores.Sanitise(
            TestOverrideActive ? TestOverrideStore : AppPreferencesIo.Load().EmMaxCores);
        set
        {
            int? v = EmSolveCores.Sanitise(value);
            if (TestOverrideActive) TestOverrideStore = v;
            else AppPreferencesIo.Update(p => p.EmMaxCores = v);
        }
    }
}
