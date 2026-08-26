using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The two update settings, their overrides, and the greyed <i>Last checked</i> line — implemented
/// once and hosted by both settings dialogs.
///
/// <para>Everything resolves through <see cref="UpdatePolicy"/>: a <c>no-auto-update</c> file beside
/// the install and <c>CRF_NO_UPDATE_CHECK=1</c> both beat the preference, and under either the
/// checkbox renders <b>disabled, with the reason</b>. A checkbox the user can tick that changes
/// nothing is worse than one they cannot.</para>
/// </summary>
public partial class UpdateSettingsView : UserControl
{
    /// <summary>
    /// The populate guard. Setting <c>IsChecked</c> raises <c>IsCheckedChanged</c>, so without it the
    /// dialog would write the preference it just read on every open — and "opening and closing
    /// Settings without touching anything writes neither key" is the property the fresh-install
    /// default rests on.
    /// </summary>
    private bool _loading;

    public UpdateSettingsView()
    {
        InitializeComponent();
        Load();
    }

    /// <summary>Hides the section header, for a host that supplies its own (a tab title).</summary>
    public bool ShowSectionHeader
    {
        get => SectionHeader.IsVisible;
        set => SectionHeader.IsVisible = value;
    }

    public void Load()
    {
        _loading = true;
        try
        {
            AppPreferences prefs = AppPreferencesIo.Load();

            // Absence IS the default: a machine with no preferences.json at all reads automatic
            // updates ON and betas OFF, with no first-run seeding anywhere.
            AutoUpdateCheck.IsChecked   = prefs.AutomaticUpdates   ?? true;
            IncludeBetasCheck.IsChecked = prefs.IncludeBetaUpdates ?? false;

            UpdatePolicyState policy = UpdatePolicy.Current;

            if (policy.IsOverridden)
            {
                AutoUpdateCheck.IsChecked    = false;
                AutoUpdateCheck.IsEnabled    = false;
                UpdateOverrideText.Text      = policy.Reason;
                UpdateOverrideText.IsVisible = true;
            }
            else
            {
                AutoUpdateCheck.IsEnabled    = true;
                UpdateOverrideText.IsVisible = false;
            }

            // Set here AS WELL AS in the parent's changed handler, or the two desynchronise: a
            // disabled child whose state still reads "on" survives review and confuses users.
            SyncBetaEnablement();

            // The updater's own state file, never AppPreferences: LastCheckUtc changes on every
            // check, and putting it in preferences.json would rewrite the whole file on a 24-hour
            // timer and race this dialog's own load-mutate-save.
            DateTime? last = UpdateStateIo.Load().LastCheckUtc;
            LastCheckedText.Text = last is null
                ? "Last checked: never"
                : $"Last checked: {last.Value.ToLocalTime():g}";
        }
        finally { _loading = false; }
    }

    private void SyncBetaEnablement()
        => IncludeBetasCheck.IsEnabled = AutoUpdateCheck.IsChecked == true && AutoUpdateCheck.IsEnabled;

    private void OnAutoUpdateChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // Update, never Load-then-Save, so a partial write cannot clobber the other fields.
        AppPreferencesIo.Update(p => p.AutomaticUpdates = AutoUpdateCheck.IsChecked);
        SyncBetaEnablement();
        ApplyUpdatePreferenceChange();
    }

    private void OnIncludeBetasChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        AppPreferencesIo.Update(p => p.IncludeBetaUpdates = IncludeBetasCheck.IsChecked);
        ApplyUpdatePreferenceChange();
    }

    /// <summary>
    /// A settings change that only mutates JSON is incomplete. Automatic updates off discards the
    /// staged update outright; betas off discards a staged <i>prerelease</i> and leaves a staged
    /// stable version alone. A user who unchecks the box and is then moved to a new version on the
    /// next relaunch has been lied to by the checkbox.
    /// </summary>
    private void ApplyUpdatePreferenceChange()
        => UpdatePreferenceChange.Apply(
               AutoUpdateCheck.IsChecked == true,
               IncludeBetasCheck.IsChecked == true);
}
