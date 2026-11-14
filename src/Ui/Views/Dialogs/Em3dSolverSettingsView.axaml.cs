using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Design.ThreeD.Occ;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Settings ▸ Solvers (brief-em3d-6 R-em3d6-4a): one row per program a 3D run can need, each saying what
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

    // Owner request, 2026-11-08: a button keeps its place once its action ends, disabled, until the page is shown
    // afresh. Tools whose Install was clicked here (one still in progress is also in _installing), and the
    // homes Uninstall removed here, by tool and version.
    private readonly HashSet<SolverTool> _installClicked = [];
    private readonly HashSet<SolverTool> _installing = [];
    private readonly HashSet<(SolverTool Tool, string Version)> _removed = [];

    public Em3dSolverSettingsView()
    {
        InitializeComponent();
        Load();
        // Loaded fires when the tab is first SHOWN, not when the dialog is built: asking three programs
        // their version is not a cost every opening of Settings should pay.
        Loaded += (_, _) => { RefreshAll(); RefreshLocations(); RefreshKernel(); };
        BuildPreferredSolverBoxes();
        CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.Changed += OnSolverInstallStateChanged;
        KernelTitle.Text = GeometryKernel.SettingsTitle;
        // brief-em3d-24 — an install that ends (however it ends) changes what discovery finds.
        CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.Finished += OnInstallFinished;
        CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.Finished += OnRemovalFinished;
        WslMemoryDialog.Changed += OnSubsystemMemoryChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            // brief-emi-6 R-emi6-2 — an open EM Setup panel re-reads which solvers are installed when this page closes.
            _ = CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.RefreshAsync();
            WslMemoryDialog.Changed -= OnSubsystemMemoryChanged;
            CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.Finished -= OnInstallFinished;
            CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.Finished -= OnRemovalFinished;
            CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.Changed -= OnSolverInstallStateChanged;
        };
    }

    private void OnInstallFinished(SolverTool tool) => Dispatcher.UIThread.Post(RefreshAll);

    // ── brief-em3d-63 R-em3d63-3d — the geometry kernel's row ────────────────────────────────────

    /// <summary>Shows what the background probe found — probing now if nothing has asked yet — and the disk cache's size.</summary>
    private void RefreshKernel()
    {
        ShowKernel(GeometryKernel.Shared.Known);
        if (GeometryKernel.Shared.Known is null) _ = ProbeKernelAsync();
    }

    private async Task ProbeKernelAsync()
    {
        KernelCheck.IsEnabled = false;
        try
        {
            var cap = await GeometryKernel.Shared.ProbeAsync();
            ShowKernel(cap);
        }
        finally { KernelCheck.IsEnabled = true; }
    }

    private void ShowKernel(GeometryKernelCapability? cap)
    {
        KernelStatus.Text = GeometryKernel.SettingsStatus(cap);
        KernelPath.Text = cap?.WorkerPath ?? "";
        KernelPath.IsVisible = cap?.WorkerPath is not null;
        _ = Task.Run(() => GeometryKernel.Shared.DiskCacheBytes()).ContinueWith(t =>
            Dispatcher.UIThread.Post(() => KernelCache.Text = $"Built shapes kept on disk: {SolverUninstaller.Size(t.Result)}"),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void OnKernelCheckAgain(object? sender, RoutedEventArgs e) => _ = ProbeKernelAsync();

    private void OnKernelClearCache(object? sender, RoutedEventArgs e)
    {
        GeometryKernel.Shared.ClearDiskCache();
        ShowKernel(GeometryKernel.Shared.Known);
    }

    private void OnRemovalFinished() => Dispatcher.UIThread.Post(RefreshAll);

    public void Load()
    {
        _loading = true;
        try
        {
            var p = AppPreferencesIo.Load();
            PalaceBox.Text  = p.Em3dPalacePath  ?? "";
            GmshBox.Text    = p.Em3dGmshPath    ?? "";
            OpenEmsBox.Text = p.Em3dOpenEmsPath ?? "";
            EmergeBox.Text  = p.Em3dEmergePath  ?? "";
            MpiBox.Text     = p.Em3dMpiLauncherPath ?? "";
            string device = CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred.Id();
            EmergeDeviceBox.SelectedItem = EmergeDeviceBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == device);
            MemoryLimitBox.PlaceholderText = CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.AutomaticText;
            MemoryLimitBox.Text = CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.Preferred is { } gb
                ? gb.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
            ShowScratchFolder();
        }
        finally { _loading = false; }
    }

    // ── Owner request, 2026-11-08: the preferred solvers for new setups ─────────────────────────────────────────────────────

    private const string NotInstalledTip = "Not installed. Install it below to choose it.";
    private const string NoGmshTip = "The thermal solver meshes with Gmsh, which is not installed. Install Gmsh below to choose it.";

    /// <summary>The two combos' rows: Planar then the 3D solvers for a new .cem, the 3D solvers for a new .c3d setup. Each row's Tag
    /// is its solver (null for Planar); availability is set by <see cref="ShowSolverAvailability"/>.</summary>
    private void BuildPreferredSolverBoxes()
    {
        ComboBoxItem Row(string label, Em3dSolverId? id)
        {
            var item = new ComboBoxItem { Content = label, Tag = id };
            ToolTip.SetShowOnDisabled(item, true);
            return item;
        }
        _loading = true;
        try
        {
            PreferredLayoutSolverBox.Items.Clear();
            PreferredLayoutSolverBox.Items.Add(Row(CircuitRF.Ui.Layout.Em.EmSolverPreference.PlanarLabel, null));
            foreach (var id in CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeDChoices)
                PreferredLayoutSolverBox.Items.Add(Row(CircuitRF.Ui.Layout.Em.EmSolverPreference.Label(id), id));
            Preferred3DSolverBox.Items.Clear();
            foreach (var id in CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeDChoices)
                Preferred3DSolverBox.Items.Add(Row(CircuitRF.Ui.Layout.Em.EmSolverPreference.Label(id), id));
            var thermal = Row(CircuitRF.Ui.Layout.Em.EmSolverPreference.ThermalLabel, null);
            thermal.Tag = CircuitRF.Ui.Layout.Em.EmSolverPreference.ThermalStored;
            Preferred3DSolverBox.Items.Add(thermal);

            var layout = CircuitRF.Ui.Layout.Em.EmSolverPreference.Layout;
            Em3dSolverId? layoutId = layout.IsNone ? null : layout.Reference;
            PreferredLayoutSolverBox.SelectedItem = PreferredLayoutSolverBox.Items.OfType<ComboBoxItem>().First(i => (Em3dSolverId?)i.Tag == layoutId);
            var threeD = CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeD;
            bool thermalPreferred = CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeDIsThermal;
            Preferred3DSolverBox.SelectedItem = Preferred3DSolverBox.Items.OfType<ComboBoxItem>()
                .First(i => thermalPreferred ? i.Tag is string : i.Tag is Em3dSolverId id && id == threeD);
        }
        finally { _loading = false; }
        ShowSolverAvailability();
    }

    private void OnSolverInstallStateChanged()
    {
        // Thermal runs circuitRF's own solver on a Gmsh mesh: it is available when the thermal run's own Gmsh check passes.
        _ = Task.Run(() => SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Proceeds)
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                _thermalAvailable = t.IsCompletedSuccessfully ? t.Result : null;
                ShowSolverAvailability();
            }));
    }

    /// <summary>Whether Gmsh is there for a thermal run; null before the first read (Thermal is then enabled, as every row is).</summary>
    private bool? _thermalAvailable;

    /// <summary>Greys every 3D solver row the last install read found missing. Before the first read, every row is enabled (as the
    /// EM Setup panel shows them), not disabled on a guess. A stored choice that is not installed stays shown, greyed.</summary>
    private void ShowSolverAvailability()
    {
        var known = CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.Known;
        foreach (var box in new[] { PreferredLayoutSolverBox, Preferred3DSolverBox })
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                bool installed = item.Tag switch
                {
                    Em3dSolverId id => known is null || known.GetValueOrDefault(id),
                    string          => _thermalAvailable ?? true,
                    _               => true,
                };
                item.IsEnabled = installed;
                ToolTip.SetTip(item, installed ? null : item.Tag is string ? NoGmshTip : NotInstalledTip);
            }
    }

    private void OnPreferredSolverChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { SelectedItem: ComboBoxItem item } box) return;
        if (box == PreferredLayoutSolverBox)
            CircuitRF.Ui.Layout.Em.EmSolverPreference.Layout = item.Tag is Em3dSolverId id ? Em3dSolverChoice.One(id) : Em3dSolverChoice.None;
        else if (item.Tag is Em3dSolverId id3)
            CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeD = id3;
        else if (item.Tag is string)
            CircuitRF.Ui.Layout.Em.EmSolverPreference.ThreeDIsThermal = true;
    }

    private (SolverDiscovery Discovery, TextBlock Status, Button Install, StackPanel Removals)[] Rows =>
    [
        (SolverDiscovery.Palace,  PalaceStatus,  PalaceInstall,  PalaceRemovals),
        (SolverDiscovery.Gmsh,    GmshStatus,    GmshInstall,    GmshRemovals),
        (SolverDiscovery.OpenEms, OpenEmsStatus, OpenEmsInstall, OpenEmsRemovals),
        (SolverDiscovery.Emerge,  EmergeStatus,  EmergeInstall,  EmergeRemovals),
    ];

    private void RefreshAll()
    {
        _ = CircuitRF.Ui.Layout.Em.Em3dSolverInstallState.RefreshAsync();   // the preferred-solver rows follow it
        foreach (var (discovery, status, install, _) in Rows) Refresh(discovery, status, install);
        RefreshMpi();
        RefreshRemovals();
    }

    /// <summary>
    /// brief-em3d-25 — one <i>Uninstall …</i> per home circuitRF installed, newest first, each with its size
    /// measured now; a home other than the newest is labelled as the older version it is (R-em3d25-3). A
    /// program circuitRF did not install has no button here, whatever its row says.
    /// </summary>
    private void RefreshRemovals()
    {
        _ = Task.Run(() =>
        {
            // PlanAll measures every home once, and sees an interrupted removal's leftovers, which only
            // Remove all offers to finish.
            var plan  = new SolverUninstaller().PlanAll();
            var homes = Enum.GetValues<SolverTool>().ToDictionary(t => t, t => plan.Homes.Where(h => h.Record.Tool == t)
                .Select(h => (h.Record, h.Bytes, h.Missing)).ToList());
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (discovery, _, _, panel) in Rows)
                {
                    panel.Children.Clear();
                    var list = homes[discovery.Tool];
                    for (int i = 0; i < list.Count; i++)
                    {
                        var (record, bytes, missing) = list[i];
                        if (_removed.Contains((discovery.Tool, record.Version))) continue;
                        // brief-em3d-26 — a home inside a Linux subsystem distribution says so; one that has
                        // vanished there is offered as the clean-up of circuitRF's record, not as an uninstall.
                        string where = record.Distribution is { } d ? $" in '{d}'" : "";
                        string label = missing
                            ? $"Clean up {discovery.Name} {record.Version}{where} (missing)…"
                            : i == 0
                            ? $"Uninstall {discovery.Name} {record.Version}{where} ({SolverUninstaller.Size(bytes)})…"
                            : $"Remove older {discovery.Name} {record.Version}{where} ({SolverUninstaller.Size(bytes)})…";
                        var button = new Button
                        {
                            Content = label, FontSize = 11, Padding = new Avalonia.Thickness(8, 3),
                            Margin = new Avalonia.Thickness(0, i == 0 ? 6 : 0, 0, 0),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        };
                        ToolTip.SetTip(button, missing
                            ? $"circuitRF installed this at {record.Home} in the Linux subsystem distribution '{record.Distribution}', and it is no longer there. This removes circuitRF's record of it."
                            : $"Installed by circuitRF at {record.Home}{(record.Distribution is { } dist ? $" in the Linux subsystem distribution '{dist}'" : "")}. Shows the space it frees before anything is removed; your documents are not touched.");
                        var tool = discovery.Tool;
                        string version = record.Version;
                        button.Click += async (_, _) =>
                        {
                            button.IsEnabled = false;
                            var outcome = await CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.RemoveAsync(
                                u => u.PlanOne(tool, version), TopLevel.GetTopLevel(this) as Window, messages: null,
                                $"Uninstall {discovery.Name} {version}");
                            if (outcome?.Status == RemovalStatus.Removed)
                            {
                                _removed.Add((tool, version));
                                button.Content = RemovedLabel(discovery.Name, version);
                            }
                            else button.IsEnabled = true;
                        };
                        panel.Children.Add(button);
                    }
                    // A home removed here keeps a disabled button where its Uninstall was.
                    foreach (var (_, version) in _removed.Where(r => r.Tool == discovery.Tool).OrderByDescending(r => r.Version))
                        panel.Children.Add(new Button
                        {
                            Content = RemovedLabel(discovery.Name, version), FontSize = 11, Padding = new Avalonia.Thickness(8, 3),
                            Margin = new Avalonia.Thickness(0, panel.Children.Count == 0 ? 6 : 0, 0, 0),
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, IsEnabled = false,
                        });
                }
                RemoveAll.IsVisible = plan.CanProceed;
                RemoveAllNote.IsVisible = plan.CanProceed && !OperatingSystem.IsWindows();
            });
        });
    }

    private static string RemovedLabel(string name, string version) => $"Uninstalled {name} {version}";

    /// <summary>brief-em3d-25 R-em3d25-2 — every circuitRF-installed solver, after one confirmation.</summary>
    private async void OnRemoveAll(object? sender, RoutedEventArgs e)
    {
        RemoveAll.IsEnabled = false;
        try
        {
            await CircuitRF.Ui.Views.Dialogs.SolverRemovalRunner.RemoveAsync(
                u => u.PlanAll(), TopLevel.GetTopLevel(this) as Window, messages: null, "Remove all 3D solvers");
        }
        finally { RemoveAll.IsEnabled = true; }
    }

    private (SolverDiscovery, TextBlock, Button, StackPanel) Row(SolverTool tool) => Rows.First(r => r.Discovery.Tool == tool);

    /// <summary>The MPI row: which mpirun a Palace run would use, found against the Palace the
    /// Palace row finds — the Spack route needs to know which Palace it is asking about.</summary>
    private void RefreshMpi()
    {
        MpiStatus.Text = "Checking…";
        _ = Task.Run(() =>
        {
            string text;
            Located? located = null;
            try
            {
                var found = SolverDiscovery.Palace.Find(out _);
                if (found is { Distribution: { } distro } && SolverDiscovery.Palace.Subsystem is { } wsl)
                {
                    // brief-em3d-26 R-em3d26-2b — the MPI beside a subsystem Palace, found inside it.
                    var session = new CircuitRF.Design.Em3d.Wsl.WslSession(wsl, distro);
                    var (path, how) = session.Home(out string? why) is { } home
                        ? CircuitRF.Design.Em3d.Wsl.WslPalace.FindMpiLauncher(session, home, found.Path)
                        : (null, why ?? "the distribution did not answer");
                    text = path is not null
                        ? $"{path} in the Linux subsystem distribution '{distro}' ({how}). The path above is not used for it."
                        : $"Not found: {how}. Palace will run on one core.";
                    located = Locate(path, distro, SolverTool.Palace, revealHome: false);
                }
                else
                {
                    var launcher = PalaceRun.FindMpiLauncher(found?.Path ?? "");
                    text = launcher.Path is { } path
                        ? $"{path} ({launcher.How})."
                        : $"Not found: {launcher.How}. Palace will run on one core.";
                    located = Locate(launcher.Path, null, SolverTool.Palace, revealHome: false);
                }
            }
            catch (Exception ex) { text = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                ShowStatus(MpiStatus, text);
                ShowFound("Mpi", MpiBadge, MpiReveal, located);
            });
        });
    }

    /// <summary>
    /// brief-em3d-26 R-em3d26-1d — the location row: Automatic, Native, and one entry per Linux subsystem
    /// distribution, listed off the UI thread (listing starts no distribution). Windows only; elsewhere the
    /// row stays hidden, because there is only one location.
    /// </summary>
    private void RefreshLocations()
    {
        if (SolverDiscovery.Palace.Subsystem is not { } wsl) return;
        _ = Task.Run(() =>
        {
            var state = CircuitRF.Design.Em3d.Wsl.WslDistributions.Read(wsl);
            Dispatcher.UIThread.Post(() =>
            {
                _loading = true;
                try
                {
                    var saved = PalaceLocation.Parse(AppPreferencesIo.Load().Em3dPalaceLocation);
                    var items = new List<ComboBoxItem>
                    {
                        new() { Content = "Automatic", Tag = PalaceLocation.Automatic },
                        new() { Content = "Native (this computer only)", Tag = PalaceLocation.Native },
                    };
                    foreach (var d in state.Distributions)
                        items.Add(new() { Content = $"Linux subsystem: {d.Name}{(d.Version < 2 ? " (WSL 1 — not usable)" : "")}",
                                          Tag = PalaceLocation.Subsystem(d.Name) });
                    if (saved.Kind == PalaceLocationKind.Subsystem && !state.Distributions.Any(d =>
                            string.Equals(d.Name, saved.Distribution, StringComparison.OrdinalIgnoreCase)))
                        items.Add(new() { Content = $"Linux subsystem: {saved.Distribution} (not installed)", Tag = saved });
                    PalaceLocationBox.ItemsSource = items;
                    PalaceLocationBox.SelectedItem = items.FirstOrDefault(i => i.Tag is PalaceLocation l && l.Kind == saved.Kind &&
                        string.Equals(l.Distribution, saved.Distribution, StringComparison.OrdinalIgnoreCase)) ?? items[0];
                    PalaceLocationRow.IsVisible = true;
                    if (!state.Ready) ToolTip.SetTip(PalaceLocationRow, state.Refusal);
                }
                finally { _loading = false; }
                RefreshSubsystemMemory();
            });
        });
    }

    // ── brief-em3d-97 R-em3d97-3 — the subsystem's memory ─────────────────────────────────────

    private string? _subsystemDistribution;

    private void OnSubsystemMemoryChanged() => Dispatcher.UIThread.Post(RefreshSubsystemMemory);

    /// <summary>
    /// "Linux subsystem: 3.8 GB of 7.6 GB", read live (<c>free -b</c> in the distribution) every time it is shown —
    /// a stored figure would be a second copy of <c>.wslconfig</c>'s. Shown only when Palace's location is a
    /// distribution, or Automatic finds Palace in one.
    /// </summary>
    private void RefreshSubsystemMemory()
    {
        if (SolverDiscovery.Palace.Subsystem is not { Available: true } wsl) return;
        var location = PalaceLocation.Parse(AppPreferencesIo.Load().Em3dPalaceLocation);
        _ = Task.Run(() =>
        {
            string? distro = location.Kind switch
            {
                PalaceLocationKind.Subsystem => location.Distribution,
                PalaceLocationKind.Native    => null,
                _                            => SolverDiscovery.Palace.Find(out _)?.Distribution,
            };
            long? bytes = distro is null ? null : new CircuitRF.Design.Em3d.Wsl.WslSession(wsl, distro).MemoryBytes();
            string line = CircuitRF.Ui.Layout.Em.WslMemoryDialogModel.SettingsLine(bytes, MachineMemory.PhysicalBytes);
            Dispatcher.UIThread.Post(() =>
            {
                _subsystemDistribution = distro;
                SubsystemMemoryRow.IsVisible = distro is not null;
                SubsystemMemoryText.Text = line;
            });
        });
    }

    private async void OnChangeSubsystemMemory(object? sender, RoutedEventArgs e)
    {
        var messages = (App.LastActiveWorkspace?.DataContext as ViewModels.WorkspaceViewModel)?.Messages;
        await WslMemoryDialog.OpenAsync(TopLevel.GetTopLevel(this) as Window, messages, _subsystemDistribution);
    }

    private void OnLocationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PalaceLocationBox.SelectedItem is not ComboBoxItem { Tag: PalaceLocation location }) return;
        AppPreferencesIo.Update(p => p.Em3dPalaceLocation = location.ToSetting());
        Refresh(SolverDiscovery.Palace, PalaceStatus, PalaceInstall);
        RefreshMpi();
        RefreshSubsystemMemory();
    }

    /// <summary>Runs discovery for one row off the UI thread and writes its answer back —
    /// <see cref="SolverStatus.Of"/>, which <c>circuitrf solver list</c> prints too.</summary>
    private void Refresh(SolverDiscovery discovery, TextBlock status, Button install)
    {
        status.Text = "Checking…";
        install.IsEnabled = false;   // kept in place while checking; the answer decides whether it shows
        var (badge, reveal) = Chrome(discovery.Tool);
        string key = discovery.Tool.ToString();
        bool emerge = discovery.Tool == SolverTool.Emerge;
        if (emerge) { EmergeAccelerate.IsVisible = false; EmergeEnableGpu.IsVisible = false; }
        _ = Task.Run(() =>
        {
            string text;
            bool offer = false;
            string? updateTo = null;
            RecipePrerequisite? accelerate = null;
            (SolverRecipe, InstallRecord, RecipePrerequisite)? enableGpu = null;
            bool found = false;
            Located? located = null;
            try
            {
                // brief-emi-3 R-emi3-3 — Settings being shown is one of the two points circuitRF finishes EMerge's faster
                // sparse solver once what it needs has appeared. Never asks: the install it belongs to was agreed to.
                if (emerge) EmergeAccelerator.FinishIfDue(discovery.Find(out _));
                // brief-em3d-136 R-em3d136-4b — the GPU line only for a user who chose GPU: a CPU user pays no GPU solve.
                var row = SolverStatus.Of(discovery.Tool, discovery,
                                          gpu: CircuitRF.Ui.Layout.Em.EmPalaceDevicePreference.Preferred == PalaceDevice.Gpu);
                text  = row.GpuLine is { } gpuLine ? row.Summary + Environment.NewLine + gpuLine : row.Summary;
                offer = row.OfferInstall;
                updateTo = row.OfferUpdate ? row.Recipe?.Version : null;
                found = row.Found is not null;
                located = Locate(row.Found?.Path, row.Found?.Distribution, discovery.Tool);
                if (emerge) accelerate = EmergeAccelerator.InstallerFor(row.Found);
                if (emerge) enableGpu = EmergeAccelerator.GpuEnableFor(row.Found);
            }
            catch (Exception ex) { text = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                ShowStatus(status, text);
                ShowFound(key, badge, reveal, located);
                if (emerge) ShowAccelerate(accelerate);
                if (emerge)
                {
                    EmergeEnableGpu.Tag = enableGpu;
                    EmergeEnableGpu.IsVisible = enableGpu is not null && accelerate is null;
                    EmergeEnableGpu.IsEnabled = !CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.IsRunning(SolverTool.Emerge);
                    // Owner request, 2026-10-22: Device chooses where EMerge solves, so with no EMerge there is nothing
                    // to choose. The stored preference is kept, and applies again once EMerge is found.
                    EmergeDeviceBox.IsEnabled = found;
                    ToolTip.SetTip(EmergeDeviceBox, found ? DeviceTip : NoEmergeDeviceTip);
                }
                // Running is set only once the install's plan is made, so a click still waiting on that (or on the
                // consent dialog) counts as running here too.
                bool running = CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.IsRunning(discovery.Tool)
                            || _installing.Contains(discovery.Tool);
                bool installed = !running && !offer && updateTo is null && found && _installClicked.Contains(discovery.Tool);
                install.IsVisible = offer || updateTo is not null || running || installed;
                install.IsEnabled = !running && !installed;
                install.Content   = installed ? "Installed"
                                  : running ? $"{(updateTo is null ? "Installing" : "Updating")} {discovery.Name}… (see Messages)"
                                  : updateTo is not null ? $"Update to {discovery.Name} {updateTo}…"
                                  : $"Install {discovery.Name}…";
                if (emerge)
                {
                    _emergeInstallTip ??= ToolTip.GetTip(install);
                    ToolTip.SetTip(install, updateTo is null ? _emergeInstallTip
                        : $"Installs {discovery.Name} {updateTo} beside the version you have. Shows what it will download first. If it fails, the version you have keeps working; when it succeeds, new runs use {updateTo} and you can remove the older one here.");
                }
            });
        });
    }

    /// <summary>
    /// A status line, with Spack's path padding folded to one "…". A Spack install tree pads every
    /// prefix with a chain of <c>__spack_path_placeholder__</c> directories (<c>padded_length</c>),
    /// which printed in full wrapped a single path over ten rows; the chain carries nothing a reader
    /// can use, and the full text stays on the tooltip for anyone who needs to copy it.
    /// </summary>
    private static void ShowStatus(TextBlock status, string text)
    {
        if (AnonymizeHomeForCapture) text = AnonymizeHome(text);
        status.Text = FoldSpackPadding(text);
        status.Tag  = text;   // the unfolded line, which Copy puts on the clipboard
        ToolTip.SetTip(status, status.Text == text ? null : text);
    }

    /// <summary>
    /// The docs seam (brief-em3d-52): the user guide's picture of this tab is captured on whatever machine runs DocGen,
    /// and its status lines are that machine's real discovery — paths under the author's home directory, which must
    /// never reach the repository. Set only by the figure fixture; a user always sees their own paths.
    /// </summary>
    internal static bool AnonymizeHomeForCapture { get; set; }

    /// <summary>The home directory written as <c>~</c>, keeping the path's shape.</summary>
    internal static string AnonymizeHome(string text)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 ? text.Replace(home, "~", StringComparison.Ordinal) : text;
    }

    /// <summary>Right-click ▸ Copy on a status line: the selection when there is one, otherwise the
    /// whole line UNFOLDED — the folded "…" is not a path anyone can paste into a terminal.</summary>
    private void OnCopyStatusClick(object? sender, RoutedEventArgs e)
    {
        if (OwningStatus(sender) is not { } status) return;
        string selected = status.SelectedText;
        CopyToClipboard(selected.Length > 0 ? selected : StatusText(status));
    }

    /// <summary>Right-click ▸ Copy All: every row, labelled, unfolded — what someone asking "what did it
    /// find?" needs in one paste.</summary>
    private void OnCopyAllStatusClick(object? sender, RoutedEventArgs e)
        => CopyToClipboard(string.Join(Environment.NewLine,
               $"Palace: {StatusText(PalaceStatus)}",
               $"MPI launcher: {StatusText(MpiStatus)}",
               $"Gmsh: {StatusText(GmshStatus)}",
               $"openEMS: {StatusText(OpenEmsStatus)}",
               $"EMerge: {StatusText(EmergeStatus)}"));

    private static string StatusText(TextBlock status) => status.Tag as string ?? status.Text ?? "";

    /// <summary>A ContextMenu lives in its own popup tree, so the owning line is the menu's Parent,
    /// not an ancestor of the MenuItem (the walk <c>MessagesView</c> uses).</summary>
    private static SelectableTextBlock? OwningStatus(object? sender)
    {
        for (var current = sender as Avalonia.LogicalTree.ILogical; current is not null; current = current.LogicalParent)
            if (current is ContextMenu menu) return menu.Parent as SelectableTextBlock;
        return null;
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    internal static string FoldSpackPadding(string text) => SpackInstalls.FoldPadding(text);

    /// <summary>brief-emi-3 R-emi3-4 — the prerequisite's own installer, labelled with the recipe's name for it, while
    /// one is waiting (and not already started from here).</summary>
    private void ShowAccelerate(RecipePrerequisite? prerequisite)
    {
        EmergeAccelerate.IsVisible = prerequisite is not null;
        EmergeAccelerate.Tag       = prerequisite;
        EmergeAccelerate.IsEnabled = !_accelerateWatching;
        if (prerequisite is not null)
            EmergeAccelerate.Content = _accelerateWatching ? $"Waiting for {prerequisite.Name}…" : $"Install {prerequisite.Name}…";
    }

    private object? _emergeInstallTip;

    /// <summary>One watch per process: a second Settings window must not start the installer twice.</summary>
    private static bool _accelerateWatching;

    /// <summary>
    /// brief-emi-3 R-emi3-4 — starts the prerequisite's own installer, which asks the user itself, then looks every few
    /// seconds until it has installed, and builds EMerge's faster sparse solver then (R-emi3-3). The user presses
    /// this and nothing else.
    /// </summary>
    private async void OnEmergeAccelerate(object? sender, RoutedEventArgs e)
    {
        if (EmergeAccelerate.Tag is not RecipePrerequisite { Installer.Count: > 0 } prerequisite || _accelerateWatching) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(prerequisite.Installer[0]) { UseShellExecute = false };
            foreach (string a in prerequisite.Installer.Skip(1)) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowStatus(EmergeStatus, $"The installer for {prerequisite.Name} could not be started: {ex.Message}");
            return;
        }
        _accelerateWatching = true;
        ShowAccelerate(prerequisite);
        try
        {
            await EmergeAccelerator.FinishWhenInstalledAsync(() => SolverDiscovery.Emerge.Find(out _),
                                                             TimeSpan.FromSeconds(5), TimeSpan.FromHours(2), CancellationToken.None);
        }
        finally
        {
            _accelerateWatching = false;
            Dispatcher.UIThread.Post(() => Refresh(SolverDiscovery.Emerge, EmergeStatus, EmergeInstall));
        }
    }

    /// <summary>brief-emi-9 R-emi9-2b — consent, then Rust fetched into EMerge's home, the GPU solver built, the toolchain deleted.</summary>
    private async void OnEmergeEnableGpu(object? sender, RoutedEventArgs e)
    {
        if (EmergeEnableGpu.Tag is not ValueTuple<SolverRecipe, InstallRecord, RecipePrerequisite> (var recipe, var record, var when)) return;
        EmergeEnableGpu.IsEnabled = false;
        try { await CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.EnableEmergeGpuAsync(recipe, record, when, TopLevel.GetTopLevel(this) as Window, null); }
        catch (Exception ex) { ShowStatus(EmergeStatus, ex.Message); }
        finally { Refresh(SolverDiscovery.Emerge, EmergeStatus, EmergeInstall); }
    }

    /// <summary>brief-emi-9 R-emi9-4 — stores where EMerge factorises; Automatic is stored as nothing.</summary>
    private const string DeviceTip =
        "Automatic uses EMerge's GPU sparse solver, when it is installed, for a driven problem large enough to be faster on it, " +
        "and solves a sweep again on the CPU if the GPU's does not converge. Eigenmode problems always solve on the CPU.";

    private const string NoEmergeDeviceTip = "Where EMerge solves. Install EMerge, or name one above, to choose.";

    private void OnEmergeDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || EmergeDeviceBox.SelectedItem is not ComboBoxItem { Tag: string id } || EmergeDevices.Parse(id) is not { } device) return;
        if (CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred != device)
            CircuitRF.Ui.Layout.Em.EmEmergeDevicePreference.Preferred = device;
    }

    /// <summary>brief-em3d-24 — the row's Install …: consent, then the install in the background.</summary>
    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button || !Enum.TryParse<SolverTool>(name, out var tool)) return;
        button.IsEnabled = false;
        _installClicked.Add(tool);
        _installing.Add(tool);
        var (discovery, status, btn, _) = Row(tool);
        try
        {
            var install = CircuitRF.Ui.Views.Dialogs.SolverInstallRunner.InstallAsync(tool, TopLevel.GetTopLevel(this) as Window, messages: null);
            Refresh(discovery, status, btn);
            await install;
        }
        catch (Exception ex) { ShowStatus(status, ex.Message); }
        finally
        {
            _installing.Remove(tool);
            Refresh(discovery, status, btn);
        }
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
        else if (box == EmergeBox)  { AppPreferencesIo.Update(p => p.Em3dEmergePath  = typed); Refresh(SolverDiscovery.Emerge,  EmergeStatus,  EmergeInstall); }
    }

    /// <summary>brief-emi-10 R-emi10-4 — stores the memory limit, blank as Automatic; a refused value is said and not stored.</summary>
    private void OnMemoryLimitCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string text = MemoryLimitBox.Text?.Trim() ?? "";
        string? refused = text.Length == 0 ? CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.TrySet(null)
            : double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gb)
                ? CircuitRF.Ui.Layout.Em.EmMemoryLimitPreference.TrySet(gb)
                : $"A memory limit for a solver is a number of GB, not '{text}'.";
        MemoryLimitStatus.Text = refused ?? "";
        MemoryLimitStatus.IsVisible = refused is not null;
    }

    /// <summary>The scratch folder in use: the chosen one, or the system temp folder (said so); and, when the chosen one is
    /// missing, the sentence a run would be refused with.</summary>
    private void ShowScratchFolder()
    {
        string? chosen = CircuitRF.Design.ScratchFolder.Chosen;
        ScratchFolderText.Text = chosen ?? CircuitRF.Ui.Layout.Em.ScratchFolderPreference.SystemDefault + "  (system temp folder)";
        ScratchDefaultButton.IsEnabled = chosen is not null;
        string? refused = CircuitRF.Design.ScratchFolder.Refusal();
        ScratchFolderStatus.Text = refused ?? "";
        ScratchFolderStatus.IsVisible = refused is not null;
    }

    private async void OnChooseScratchFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title         = "Choose the scratch folder",
                AllowMultiple = false,
            });
            if (folders.Count != 1 || folders[0].TryGetLocalPath() is not { Length: > 0 } path) return;
            CircuitRF.Ui.Layout.Em.ScratchFolderPreference.Set(path);
            ShowScratchFolder();
        }
        catch (Exception) { /* a cancelled or unavailable picker is not an error */ }
    }

    private void OnDefaultScratchFolder(object? sender, RoutedEventArgs e)
    {
        CircuitRF.Ui.Layout.Em.ScratchFolderPreference.Set(null);
        ShowScratchFolder();
    }

    // ── Whether each program is installed, and the "…" that shows where ─────────────────────────────

    /// <summary>Where a row's program is: the program itself, which Browse… opens beside, and what its "…" shows — the
    /// home circuitRF installed it in when it did, since that folder is the whole installation, otherwise (and always for
    /// mpirun, whose home is Palace's) the program selected in its folder. <paramref name="WithPalace"/>: an MPI launcher inside a Palace circuitRF installed.</summary>
    private sealed record Located(string? Program, string Reveal, bool WithPalace);

    /// <summary>Each row's answer, keyed by its reveal button's Tag ("Palace", "Mpi", "Gmsh", "OpenEms", "Emerge").</summary>
    private readonly Dictionary<string, Located?> _found = [];

    private (StackPanel Badge, Button Reveal) Chrome(SolverTool tool) => tool switch
    {
        SolverTool.Palace  => (PalaceBadge,  PalaceReveal),
        SolverTool.Gmsh    => (GmshBadge,    GmshReveal),
        SolverTool.OpenEms => (OpenEmsBadge, OpenEmsReveal),
        _                  => (EmergeBadge,  EmergeReveal),
    };

    /// <summary>
    /// What a found program reveals. Off the UI thread: it reads circuitRF's install records. A program in a Linux
    /// subsystem distribution is shown through Windows' own share of it, which Explorer opens; it has no folder of this
    /// computer's to start Browse… in.
    /// </summary>
    private static Located? Locate(string? path, string? distribution, SolverTool installedBy, bool revealHome = true)
    {
        if (path is null) return null;
        var home = new SolverUninstaller().Installed(installedBy)
            .FirstOrDefault(r => string.Equals(r.Distribution, distribution, StringComparison.OrdinalIgnoreCase) &&
                                 path.StartsWith(r.Home.TrimEnd('/', '\\') + (distribution is null ? Path.DirectorySeparatorChar : '/'),
                                                 OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            ?.Home;
        string reveal = revealHome ? home ?? path : path;
        if (distribution is not null)
            return OperatingSystem.IsWindows() ? new(null, $@"\\wsl.localhost\{distribution}{reveal.Replace('/', '\\')}", false) : null;
        return new(path, reveal, home is not null && installedBy == SolverTool.Palace);
    }

    /// <summary>
    /// The badge beside a program's name — a green check and "Installed" when a run would find it, grey "Not Installed"
    /// when not — and its "…", shown only when there is somewhere to show.
    /// </summary>
    private void ShowFound(string key, StackPanel badge, Button reveal, Located? located)
    {
        _found[key] = located;
        bool installed = located is not null;
        badge.Children.Clear();
        if (installed)
        {
            var icon = new Material.Icons.Avalonia.MaterialIcon { Kind = Material.Icons.MaterialIconKind.CheckCircle, Width = 14, Height = 14 };
            icon.Bind(ForegroundProperty, icon.GetResourceObservable("CrfSuccessBrush"));
            badge.Children.Add(icon);
        }
        var text = new SelectableTextBlock
        {
            Text = !installed ? "Not Installed" : located!.WithPalace && key == "Mpi" ? "Installed with Palace" : "Installed",
            FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        if (installed) text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("CrfSuccessBrush"));
        else text.Opacity = 0.5;
        badge.Children.Add(text);
        if (key == "Mpi" && installed && located!.WithPalace)
            ToolTip.SetTip(badge, "Part of the Palace circuitRF installed: Uninstall Palace removes it too.");
        else ToolTip.SetTip(badge, null);

        reveal.IsVisible = located is not null;
        ToolTip.SetTip(reveal, located is null ? null : $"{FileReveal.Label}: {FoldSpackPadding(located.Reveal)}");
    }

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key } && _found.GetValueOrDefault(key) is { } located)
            FileReveal.Reveal(located.Reveal);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string boxName } || this.FindControl<TextBox>(boxName) is not { } box) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        try
        {
            // Opens where the row's program is, when it is on this computer: the folder holding it, which is where a
            // replacement is most likely to be picked from.
            IStorageFolder? start = null;
            if (_found.GetValueOrDefault(boxName[..^"Box".Length]) is { Program: { } program } &&
                FileReveal.NearestExistingDirectory(Path.GetDirectoryName(program)) is { } folder)
                start = await topLevel.StorageProvider.TryGetReachableFolderAsync(folder);
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title                  = "Choose the program",
                AllowMultiple          = false,
                SuggestedStartLocation = start,
            });
            if (files.Count != 1 || files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

            box.Text = path;
            OnPathCommitted(box, e);
        }
        catch (Exception) { /* a cancelled or unavailable picker is not an error */ }
    }
}
