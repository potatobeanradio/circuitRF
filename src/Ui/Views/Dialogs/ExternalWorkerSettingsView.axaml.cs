using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Security;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The external-device-worker consent checkbox and its overrides — implemented once and hosted by
/// both settings dialogs.
///
/// <para>Everything resolves through <see cref="ExternalWorkerPolicy"/>: a <c>no-device-workers</c>
/// file beside the install and <c>CRF_NO_DEVICE_WORKERS=1</c> both beat the preference, and under
/// either the checkbox renders <b>disabled, with the reason</b>. A checkbox the user can tick that
/// changes nothing is worse than one they cannot.</para>
/// </summary>
public partial class ExternalWorkerSettingsView : UserControl
{
    /// <summary>
    /// The populate guard. Setting <c>IsChecked</c> raises <c>IsCheckedChanged</c>, so without it the
    /// dialog would write the preference it just read on every open — and "opening and closing
    /// Settings without touching anything writes nothing" is the property the fresh-install default
    /// rests on.
    /// </summary>
    private bool _loading;

    public ExternalWorkerSettingsView()
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
            // Absence IS the default: a machine with no preferences.json at all reads ON.
            AllowWorkersCheck.IsChecked = AppPreferencesIo.Load().ExternalDeviceWorkers ?? true;

            ExternalWorkerPolicyState policy = ExternalWorkerPolicy.Current;

            if (policy.IsOverridden)
            {
                AllowWorkersCheck.IsChecked  = false;
                AllowWorkersCheck.IsEnabled  = false;
                WorkerOverrideText.Text      = policy.Reason;
                WorkerOverrideText.IsVisible = true;
            }
            else
            {
                AllowWorkersCheck.IsEnabled  = true;
                WorkerOverrideText.IsVisible = false;
            }
        }
        finally { _loading = false; }
    }

    private void OnAllowWorkersChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // Update, never Load-then-Save, so a partial write cannot clobber the other fields.
        AppPreferencesIo.Update(p => p.ExternalDeviceWorkers = AllowWorkersCheck.IsChecked);

        // A settings change that only mutates JSON is incomplete — the same rule the update
        // checkbox follows when it discards a staged version. A worker started before the box was
        // unchecked is still running and still answering, so unchecking would not take effect until
        // the workspace was reopened; the user would have every reason to believe it had. Dropping
        // the resolved providers ends them. Turning it back ON drops them too, so a kit that was
        // refused a moment ago is re-resolved rather than remembered as unavailable.
        try { ExternalDeviceRegistry.ResetResolved(); } catch { /* nothing here may fail Settings */ }
    }
}
