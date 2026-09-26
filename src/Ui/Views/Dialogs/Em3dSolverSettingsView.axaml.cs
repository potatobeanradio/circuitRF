using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Settings ▸ 3D EM (brief-em3d-6 R-em3d6-4a): one row per program a 3D run can need, each saying what
/// <see cref="SolverDiscovery"/> found. The same discovery a run calls at its top, so the row and the
/// run never disagree.
///
/// <para><b>Blank is the default and means "search".</b> It is never seeded with a discovered path — a
/// seeded value would freeze today's answer into <c>preferences.json</c> and stop tracking a program
/// the user later installs or upgrades, the rule <see cref="VerilogACompilerSettingsView"/> states for
/// the same reason.</para>
/// </summary>
public partial class Em3dSolverSettingsView : UserControl
{
    /// <summary>The populate guard — opening and closing Settings without touching anything must write
    /// nothing. Same reason as <see cref="VerilogACompilerSettingsView"/>'s.</summary>
    private bool _loading;

    public Em3dSolverSettingsView()
    {
        InitializeComponent();
        Load();
        // Loaded fires when the tab is first SHOWN, not when the dialog is built: asking three programs
        // their version is not a cost every opening of Settings should pay.
        Loaded += (_, _) => RefreshAll();
        // brief-em3d-24 — an install that ends (however it ends) changes what discovery finds.
        CircuitRF.Ui.Layout.Em.SolverInstallRunner.Finished += OnInstallFinished;
        DetachedFromVisualTree += (_, _) => CircuitRF.Ui.Layout.Em.SolverInstallRunner.Finished -= OnInstallFinished;
    }

    private void OnInstallFinished(SolverTool tool) => Dispatcher.UIThread.Post(RefreshAll);

    public void Load()
    {
        _loading = true;
        try
        {
            var p = AppPreferencesIo.Load();
            PalaceBox.Text  = p.Em3dPalacePath  ?? "";
            GmshBox.Text    = p.Em3dGmshPath    ?? "";
            OpenEmsBox.Text = p.Em3dOpenEmsPath ?? "";
            MpiBox.Text     = p.Em3dMpiLauncherPath ?? "";
        }
        finally { _loading = false; }
    }

    private (SolverDiscovery Discovery, TextBlock Status, Button Install)[] Rows =>
    [
        (SolverDiscovery.Palace,  PalaceStatus,  PalaceInstall),
        (SolverDiscovery.Gmsh,    GmshStatus,    GmshInstall),
        (SolverDiscovery.OpenEms, OpenEmsStatus, OpenEmsInstall),
    ];

    private void RefreshAll()
    {
        foreach (var (discovery, status, install) in Rows) Refresh(discovery, status, install);
        RefreshMpi();
    }

    private (SolverDiscovery, TextBlock, Button) Row(SolverTool tool) => Rows[tool switch
    {
        SolverTool.Palace => 0,
        SolverTool.Gmsh   => 1,
        _                 => 2,
    }];

    /// <summary>The MPI row: which mpirun a Palace run would use, found against the Palace the
    /// Palace row finds — the Spack route needs to know which Palace it is asking about.</summary>
    private void RefreshMpi()
    {
        MpiStatus.Text = "Checking…";
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                string palace = SolverDiscovery.Palace.Find(out _)?.Path ?? "";
                var launcher = PalaceRun.FindMpiLauncher(palace);
                text = launcher.Path is { } path
                    ? $"{path} ({launcher.How})."
                    : $"Not found: {launcher.How}. Palace will run on one core.";
            }
            catch (Exception ex) { text = ex.Message; }
            Dispatcher.UIThread.Post(() => MpiStatus.Text = text);
        });
    }

    /// <summary>Runs discovery for one row off the UI thread and writes its answer back —
    /// <see cref="SolverStatus.Of"/>, which <c>circuitrf solver list</c> prints too.</summary>
    private static void Refresh(SolverDiscovery discovery, TextBlock status, Button install)
    {
        status.Text = "Checking…";
        install.IsVisible = false;
        _ = Task.Run(() =>
        {
            string text;
            bool offer = false;
            try
            {
                var row = SolverStatus.Of(discovery.Tool, discovery);
                text  = row.Summary;
                offer = row.OfferInstall;
            }
            catch (Exception ex) { text = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                status.Text = text;
                bool running = CircuitRF.Ui.Layout.Em.SolverInstallRunner.IsRunning(discovery.Tool);
                install.IsVisible = offer || running;
                install.IsEnabled = !running;
                install.Content   = running ? $"Installing {discovery.Name}… (see Messages)" : $"Install {discovery.Name}…";
            });
        });
    }

    /// <summary>brief-em3d-24 — the row's Install …: consent, then the install in the background.</summary>
    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button || !Enum.TryParse<SolverTool>(name, out var tool)) return;
        button.IsEnabled = false;
        try
        {
            var install = CircuitRF.Ui.Layout.Em.SolverInstallRunner.InstallAsync(tool, TopLevel.GetTopLevel(this) as Window, messages: null);
            var (discovery, status, btn) = Row(tool);
            Refresh(discovery, status, btn);
            await install;
        }
        catch (Exception ex) { Row(tool).Item2.Text = ex.Message; }
    }

    /// <summary>
    /// Writes the named path. Blank clears it back to "search", stored as null rather than as an empty
    /// string so "never chosen" and "deliberately cleared" stay the same state.
    /// </summary>
    private void OnPathCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box) return;

        string? typed = box.Text?.Trim() is { Length: > 0 } t ? t : null;
        if (box == PalaceBox)       { AppPreferencesIo.Update(p => p.Em3dPalacePath  = typed); Refresh(SolverDiscovery.Palace,  PalaceStatus,  PalaceInstall); RefreshMpi(); }
        else if (box == MpiBox)     { AppPreferencesIo.Update(p => p.Em3dMpiLauncherPath = typed); RefreshMpi(); }
        else if (box == GmshBox)    { AppPreferencesIo.Update(p => p.Em3dGmshPath    = typed); Refresh(SolverDiscovery.Gmsh,    GmshStatus,    GmshInstall); }
        else if (box == OpenEmsBox) { AppPreferencesIo.Update(p => p.Em3dOpenEmsPath = typed); Refresh(SolverDiscovery.OpenEms, OpenEmsStatus, OpenEmsInstall); }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string boxName } || this.FindControl<TextBox>(boxName) is not { } box) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title         = "Choose the program",
                AllowMultiple = false,
            });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

            box.Text = path;
            OnPathCommitted(box, e);
        }
        catch (Exception) { /* a cancelled or unavailable picker is not an error */ }
    }
}
