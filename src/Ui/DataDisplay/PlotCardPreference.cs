// Whether a plot added to a Data Display starts on a card (CardStyle): per user, in AppPreferences. It decides the
// DEFAULT of a new plot only — its marker boxes and marker table follow the plot's card; the plot's own Card flag is what its inspector toggles and what the .cdd saves, so a display
// opens as it was saved on any machine. The shape of Realistic3DPreference, test seam included — without it a test that
// adds a plot reads the developer's real preferences file.

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.DataDisplay;

public static class PlotCardPreference
{
    internal static bool? TestOverrideStore;
    internal static bool TestOverrideActive;

    /// <summary>Settings ▸ Appearance ▸ Background cards. On by default.</summary>
    public static bool NewPlots
    {
        get => TestOverrideActive ? TestOverrideStore ?? true : AppPreferencesIo.Load().DataDisplayPlotsAsCards ?? true;
        set
        {
            if (TestOverrideActive) { TestOverrideStore = value; return; }
            AppPreferencesIo.Update(p => p.DataDisplayPlotsAsCards = value);
        }
    }
}
