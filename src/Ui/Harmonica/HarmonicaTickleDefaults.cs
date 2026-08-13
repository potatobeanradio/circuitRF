using CircuitRF.Harmonica;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// R-h9r2-18a — what a BRAND NEW harmonicaRF document's tickle starts at, read from this
/// installation's preferences with the shipped default as the fallback.
///
/// <para><b>Follows the wBond wire-defaults precedent exactly</b> (<c>WBondDefaults</c>,
/// wbond.md §6.4 / <c>src/Ui/CLAUDE.md</c>'s own note on it): the tickle is a PREFERENCE, not design
/// state — it describes how one user likes their compression measured, not a property of any one
/// circuit — so a <c>.charm</c> arriving from someone else must not change what THEIR document was
/// solved with (<c>HarmonicaSettings.TickleEnabled</c>/<c>TickleDbm</c> are what a loaded document
/// carries and always win over this). This resolver is read ONLY at the moment a brand new document
/// is created — <c>WorkspaceViewModel.NewHarmonica</c> and the standalone binary's own shell — never
/// by <see cref="HarmonicaViewModel.DefaultModel"/> itself, which stays preference-free and
/// deterministic for every test and probe that constructs a document directly.</para>
/// </summary>
public static class HarmonicaTickleDefaults
{
    public const bool   ShippedEnabled = true;
    public const double ShippedDbm     = -50.0;

    public static bool Enabled
        => AppPreferencesIo.Load().HarmonicaTickleEnabled ?? ShippedEnabled;

    public static double Dbm
        => AppPreferencesIo.Load().HarmonicaTickleDbm ?? ShippedDbm;

    /// <summary>
    /// <see cref="HarmonicaViewModel.DefaultModel"/>'s own fixture, with the tickle overridden from
    /// this installation's preferences — what every "New" harmonicaRF document actually opens on.
    /// </summary>
    public static CircuitModel SeedModel()
    {
        var model = HarmonicaViewModel.DefaultModel();
        return model with { Settings = model.Settings with { TickleEnabled = Enabled, TickleDbm = Dbm } };
    }
}
