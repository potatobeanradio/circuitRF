using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Design.Revision;
using CircuitRF.Ui.Revision;
using CircuitRF.Ui.Layout.Drc;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class SettingsView : Window
{
    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The workspace directory this dialog was CONSTRUCTED with. <b>Read it through
    /// <see cref="CurrentWorkspaceDirectory"/> and never directly</b> — it is null whenever the caller
    /// did not know, and the fallback is what answers there.
    /// </summary>
    private readonly string? _workspaceDirPath;

    // Guard: prevents recursive writes during programmatic ComboBox population.
    private bool _updatingGeneral;

    /// <summary>The On Launch ▸ Action rows, in the order the combobox shows them. Each row names its
    /// <see cref="LaunchAction"/> member explicitly, so the saved value never depends on a row's
    /// position.</summary>
    internal static readonly (string Label, LaunchAction Action)[] LaunchActionRows =
    [
        ("Welcome",          LaunchAction.Welcome),
        ("Do Nothing",       LaunchAction.DoNothing),
        ("New Schematic",    LaunchAction.NewSchematic),
        ("New Workspace",    LaunchAction.NewWorkspace),
        ("Open Workspace",   LaunchAction.OpenWorkspace),
        ("New Data Display", LaunchAction.NewDataDisplay),
        ("New Symbol",       LaunchAction.NewSymbol),
        ("New Layout",       LaunchAction.NewLayout),
        ("harmonicaRF",      LaunchAction.NewHarmonica),
        ("Smith Chart",      LaunchAction.NewSmithChart),
        ("New 3D Design",    LaunchAction.New3DDesign),
    ];

    // Snapshot of ThemeService.Active taken on open — restored by Cancel/Revert.
    private ColorTheme _originalTheme;

    // Working copies of each variant's role map — mutated as the user edits.
    private Dictionary<string, Rgba> _workingLight = [];
    private Dictionary<string, Rgba> _workingDark  = [];

    // Prevents recursive updates when we set slider values in code.
    private bool _updating;

    private ColorVariant SelectedVariant =>
        DarkRadio.IsChecked == true ? ColorVariant.Dark : ColorVariant.Light;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// The workspace the user is looking at, supplied by the application because a dialog cannot see
    /// the window list.
    ///
    /// <para><b>Consulted on ACTIVATION as well as at construction</b>, because this dialog is not
    /// modal and outlives any one workspace: somebody who opens Settings, switches workspace and looks
    /// back must be shown the workspace they are looking at. Null — nothing installed it, or nothing is
    /// open — means "no workspace", which is a state the per-workspace row states plainly rather than
    /// hiding.</para>
    /// </summary>
    internal static Func<string?>? ActiveWorkspaceDirectory { get; set; }

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001), and is what the docs
    // factory captures with: a figure of "no workspace open" is the reproducible one.
    public SettingsView() : this(null) { }

    public SettingsView(string? workspaceDirPath)
    {
        _workspaceDirPath = workspaceDirPath;
        _originalTheme    = ThemeService.Active;

        InitializeComponent();
        Loaded    += OnLoaded;
        Activated += OnActivatedRefresh;
    }

    /// <summary>brief-em3d-64 R-em3d64-5b — opens on the tab whose header is <paramref name="header"/> (the refusal on open's
    /// <i>Open Settings ▸ Solvers</i>); an unknown header leaves the first tab selected.</summary>
    public void SelectTab(string header)
    {
        if (Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == header) is { } tab) Tabs.SelectedItem = tab;
    }

    /// <summary>
    /// Re-reads what depends on WHICH workspace is open. Only the Revision Control tab does — and
    /// re-reading is also how a change made in another window, or by hand, reaches this dialog: the
    /// per-workspace flag lives in the <c>.cws</c>, not in anything this window holds.
    /// </summary>
    private void OnActivatedRefresh(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        RevisionSettings.SetWorkspace(CurrentWorkspaceDirectory());
    }

    /// <summary>
    /// What was passed in, or — when nothing was — whatever the application says is in front.
    ///
    /// <para><b>Everything that depends on which workspace is open goes through here, including the
    /// colour themes</b>, so all three platforms behave the same (owner, 2026-09-06). They did not:
    /// <c>circuitRF ▸ Settings…</c> and <c>⌘,</c> go through the macOS application menu, which
    /// constructs this dialog itself and passed null — and on that platform those are the only ways
    /// most people open it. <c>ThemeResolver</c> was therefore never offered the open workspace on a
    /// Mac, so a workspace's own <c>.ccolor</c> themes never appeared in the combo box. It read as
    /// nothing at all, because a theme that is not listed looks exactly like a workspace that has
    /// none.</para>
    ///
    /// <para><b>The constructor argument WINS where there is one</b>, and that is not an accident: on
    /// Windows and Linux each workspace window opens its own Settings dialog scoped to its own
    /// workspace, and asking "which window is in front" from a dialog that is itself in front would
    /// answer with somebody else's workspace. The fallback is for a dialog that was never told.</para>
    /// </summary>
    private string? CurrentWorkspaceDirectory()
        => _workspaceDirPath ?? ActiveWorkspaceDirectory?.Invoke();

    /// <summary>
    /// Populate every tab exactly as opening the dialog does.
    ///
    /// <para>For the User-Docs factory only. It captures this dialog's CONTENT hosted inside its own
    /// window (a Window cannot live inside another Window), so this window is never shown and its
    /// <c>Loaded</c> never fires — without this the figure would be a dialog of empty combo boxes and
    /// unticked checkboxes, which is a picture of nothing the application ever does.</para>
    /// </summary>
    internal void PopulateForCapture() => OnLoaded(this, new RoutedEventArgs());

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadGeneralPrefs();
        LoadRevisionTab();
        PopulateThemeCombo();

        // Open on whichever variant circuitRF is actually rendering right now, not a hardcoded
        // default — otherwise the editor shows colors the user isn't looking at.
        DarkRadio.IsChecked  = ThemeService.CurrentVariant == ColorVariant.Dark;
        LightRadio.IsChecked = ThemeService.CurrentVariant != ColorVariant.Dark;

        LoadThemeIntoEditor(ThemeService.Active);
    }

    // ── Revision Control tab ─────────────────────────────────────────────────

    private void LoadRevisionTab()
        // No visibility to apply: the tab is on every machine (owner, 2026-09-07). What a machine with
        // no git gets is the tab with its rows greyed and a sentence saying why, which the tab's own
        // view decides — and it subscribes to the git-path control itself, so nothing here has to.
        => RevisionSettings.SetWorkspace(CurrentWorkspaceDirectory());

    // ── General tab ──────────────────────────────────────────────────────────

    private void LoadGeneralPrefs()
    {
        _updatingGeneral = true;
        try
        {
            var prefs = AppPreferencesIo.Load();

            // Rows are (label, member) pairs in DISPLAY order; the member, never the row index, is
            // what is saved. LaunchAction's ordinals are a FILE FORMAT (see its own remarks), so the
            // enum is only ever appended to — but a row can be shown anywhere, which is how
            // "Do Nothing" sits directly under "Welcome" while DoNothing is the last member.
            // SmithWindowTests asserts every member appears exactly once.
            LaunchActionCombo.ItemsSource = LaunchActionRows.Select(r => r.Label).ToArray();
            int launchRow = Array.FindIndex(LaunchActionRows,
                r => r.Action == (prefs.LaunchAction ?? LaunchAction.Welcome));
            LaunchActionCombo.SelectedIndex = launchRow >= 0 ? launchRow : 0;

            // Index == enum ordinal, and the ordinals are a FILE FORMAT (see WindowLayout) —
            // reordering these strings silently changes what every saved preference means.
            WindowLayoutCombo.ItemsSource   = new[] { "Project Tree Focus", "Library Focus", "Project Tree & Library" };
            WindowLayoutCombo.SelectedIndex = (int)(prefs.WindowLayout ?? WindowLayout.ProjectTreeAndLibrary);

            ShowDockersOnLaunchCheck.IsChecked = prefs.ShowDockersOnLaunch ?? true;

            SyncThemeButtons(prefs.Appearance ?? AppearanceMode.System);

            RealisticOnOpenCheck.IsChecked = Realistic3DPreference.OnOpen;

            CopyColorCombo.ItemsSource   = new[] { "Follow System", "Force Light", "Force Dark" };
            CopyColorCombo.SelectedIndex = (int)(prefs.CopyColorMode ?? CopyColorMode.FollowSystem);

            TransparentBgCheck.IsChecked = prefs.CopyTransparentBackground ?? true;

            CheckDrcOnExportCheck.IsChecked = prefs.CheckDrcOnExport ?? true;

            // R-lvs12-1e: the LVS gate's default is OFF, deliberately — see AppPreferences.
            CheckLvsOnExportCheck.IsChecked = prefs.CheckLvsOnExport ?? false;

            LinkOrientationCheck.IsChecked = prefs.LinkSchematicLayoutOrientation ?? true;
            ConnectPartsCheck.IsChecked = prefs.ConnectPartsOnUpdateLayout ?? true;
            EmSetupOnUpdateCheck.IsChecked = prefs.EmSetupOnUpdateLayout ?? false;

            CoalesceRasterFillCheck.IsChecked = prefs.CoalesceRasterFillOnImport ?? true;

            ShowGerberArcTolerance(prefs);

            // Index == RulerSizeMode ordinal, serialized as a number — keep these in enum order.
            DefaultRulerSizeModeCombo.ItemsSource   = new[] { "Fixed", "Scaled" };
            DefaultRulerSizeModeCombo.SelectedIndex = (int)(prefs.DefaultRulerSizeMode ?? RulerSizeMode.Fixed);

            // brief-dfr22-1: index 0 is Automatic (null); the rest are PreferredUnits' choices, stored by name.
            PreferredLengthUnitCombo.ItemsSource = new[] { "Automatic" }
                .Concat(PreferredUnits.LengthChoices.Select(LayoutUnits.Suffix)).ToArray();
            PreferredLengthUnitCombo.SelectedIndex = PreferredUnits.ParseLength(prefs.PreferredLengthUnit) is { } lu
                ? 1 + PreferredUnits.LengthChoices.ToList().IndexOf(lu) : 0;
            PreferredFrequencyUnitCombo.ItemsSource = new[] { "Automatic" }.Concat(PreferredUnits.FrequencyChoices).ToArray();
            PreferredFrequencyUnitCombo.SelectedIndex = PreferredUnits.ParseFrequency(prefs.PreferredFrequencyUnit) is { } fu
                ? 1 + PreferredUnits.FrequencyChoices.ToList().IndexOf(fu) : 0;

            MsgTimestampCombo.ItemsSource   = new[] { "Time", "Date + Time", "Hidden" };
            MsgTimestampCombo.SelectedIndex = (int)(prefs.MessageTimestamp ?? MessageTimestampMode.Time);

            // wbond.md §6.4. Diameter is shown in mil because that is the unit a bonder is specified
            // in; it is stored in nanometres like every other wBond dimension.
            WBondPointsUpDown.Value = WBondDefaults.Points;
            WBondDiameterUpDown.Value = (decimal)WBondUnits.FromNm(WBondDefaults.DiameterNm, WBondUnit.Mil);

            WBondMaterialCombo.ItemsSource = WireMaterials.All.Select(m => m.Name).ToArray();
            WBondMaterialCombo.SelectedItem = WBondDefaults.Material;
            WBondFootZUpDown.Value = (decimal)WBondUnits.FromNm(WBondDefaults.FootZNm, WBondUnit.Mil);
            WBondPastePitchUpDown.Value = (decimal)WBondUnits.FromNm(WBondDefaults.PastePitchNm, WBondUnit.Mil);

            // Read back through the sanitiser, not from the raw preference: a hand-edited or absent
            // value has to show the number the CHECK will actually use, or the dialog reports a rule
            // that is not the one running.
            WireClearanceUpDown.Value = (decimal)WBondWireClearance.Mil;

        }
        finally { _updatingGeneral = false; }
    }

    private void OnLaunchSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p =>
        {
            if (LaunchActionCombo.SelectedIndex >= 0 && LaunchActionCombo.SelectedIndex < LaunchActionRows.Length)
                p.LaunchAction = LaunchActionRows[LaunchActionCombo.SelectedIndex].Action;
            if (WindowLayoutCombo.SelectedIndex >= 0)
                p.WindowLayout = (WindowLayout)WindowLayoutCombo.SelectedIndex;
        });
    }

    private void OnShowDockersOnLaunchChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.ShowDockersOnLaunch = ShowDockersOnLaunchCheck.IsChecked);
    }

    // Takes effect at the next open; a design already open keeps whatever its toolbar button says.
    private void OnRealisticOnOpenChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        Realistic3DPreference.OnOpen = RealisticOnOpenCheck.IsChecked == true;
    }

    // ── Theme (light / dark / system) ────────────────────────────────────────

    /// <summary>
    /// The three buttons are one control: whichever was clicked becomes the mode, and the other two
    /// give up the accent. Which one was clicked is read from its <c>Tag</c> rather than from three
    /// separate handlers — the handler is then the same code for all three, and adding a fourth mode
    /// is one button.
    ///
    /// <para><b>Write-through, like every other General setting</b> — Cancel restores the colour
    /// PALETTE and nothing else, and a live preview the user has been looking at for a minute is not
    /// something to silently undo on the way out.</para>
    /// </summary>
    private void OnThemeModeClick(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        if (sender is not Button { Tag: string tag }
            || !Enum.TryParse<AppearanceMode>(tag, out var mode)) return;

        AppearanceService.Set(mode);
        SyncThemeButtons(mode);
    }

    private void SyncThemeButtons(AppearanceMode mode)
    {
        ThemeSystemBtn.Classes.Set("ToolActive", mode == AppearanceMode.System);
        ThemeLightBtn .Classes.Set("ToolActive", mode == AppearanceMode.Light);
        ThemeDarkBtn  .Classes.Set("ToolActive", mode == AppearanceMode.Dark);
    }

    private void OnCopyColorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral || CopyColorCombo.SelectedIndex < 0) return;
        AppPreferencesIo.Update(p => p.CopyColorMode = (CopyColorMode)CopyColorCombo.SelectedIndex);
    }

    private void OnTransparentBgChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.CopyTransparentBackground = TransparentBgCheck.IsChecked);
    }

    private void OnCheckDrcOnExportChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.CheckDrcOnExport = CheckDrcOnExportCheck.IsChecked);
    }

    private void OnCheckLvsOnExportChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.CheckLvsOnExport = CheckLvsOnExportCheck.IsChecked);
    }

    private void OnLinkOrientationChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.LinkSchematicLayoutOrientation = LinkOrientationCheck.IsChecked);
    }

    private void OnConnectPartsChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.ConnectPartsOnUpdateLayout = ConnectPartsCheck.IsChecked);
    }

    private void OnEmSetupOnUpdateChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.EmSetupOnUpdateLayout = EmSetupOnUpdateCheck.IsChecked);
    }

    private void OnCoalesceRasterFillChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.CoalesceRasterFillOnImport = CoalesceRasterFillCheck.IsChecked);
    }

    private void OnDefaultRulerSizeModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral || DefaultRulerSizeModeCombo.SelectedIndex < 0) return;
        var mode = (RulerSizeMode)DefaultRulerSizeModeCombo.SelectedIndex;
        AppPreferencesIo.Update(p => p.DefaultRulerSizeMode = mode);
    }

    private void OnPreferredLengthUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral || PreferredLengthUnitCombo.SelectedIndex < 0) return;
        int i = PreferredLengthUnitCombo.SelectedIndex;
        string? name = i == 0 ? null : PreferredUnits.LengthChoices[i - 1].ToString();
        AppPreferencesIo.Update(p => p.PreferredLengthUnit = name);
        ShowGerberArcTolerance(AppPreferencesIo.Load());   // the same tolerance, in the new unit
    }

    // ── Gerber arc tolerance ─────────────────────────────────────────────────────────────────
    //
    // Stored in microns, shown in the Length unit chosen under Units (micrometres when that is Automatic, since
    // that is the unit a tolerance this small is thought of in). Read in nanometre steps.

    private const int ArcToleranceDbuPerMicron = 1000;

    private static LayoutUnit ArcToleranceUnit(AppPreferences prefs) =>
        PreferredUnits.ParseLength(prefs.PreferredLengthUnit) ?? LayoutUnit.Um;

    private void ShowGerberArcTolerance(AppPreferences prefs)
    {
        var unit = ArcToleranceUnit(prefs);
        double um = prefs.GerberArcToleranceMicrons ?? CircuitRF.Design.Layout.Interchange.GerberImport.DefaultArcToleranceMicrons;
        long nm = (long)Math.Round(um * ArcToleranceDbuPerMicron);
        GerberArcToleranceBox.Text = LayoutUnits.Format(nm, unit, ArcToleranceDbuPerMicron, maxDecimals: 6);
        GerberArcToleranceUnit.Text = LayoutUnits.Suffix(unit);
    }

    private void CommitGerberArcTolerance()
    {
        var prefs = AppPreferencesIo.Load();
        if (LayoutUnits.TryParse(GerberArcToleranceBox.Text ?? "", ArcToleranceUnit(prefs), ArcToleranceDbuPerMicron, out long nm)
            && nm >= CircuitRF.Design.Layout.Interchange.GerberImport.MinArcToleranceMicrons * ArcToleranceDbuPerMicron
            && nm <= CircuitRF.Design.Layout.Interchange.GerberImport.MaxArcToleranceMicrons * ArcToleranceDbuPerMicron)
        {
            double um = nm / (double)ArcToleranceDbuPerMicron;
            AppPreferencesIo.Update(p => p.GerberArcToleranceMicrons = um);
            prefs.GerberArcToleranceMicrons = um;
        }
        ShowGerberArcTolerance(prefs);   // a value that would not read goes back to the one in force
    }

    private void OnGerberArcToleranceLostFocus(object? sender, RoutedEventArgs e) => CommitGerberArcTolerance();

    private void OnGerberArcToleranceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitGerberArcTolerance();
        e.Handled = true;
    }

    private void OnPreferredFrequencyUnitChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral || PreferredFrequencyUnitCombo.SelectedIndex < 0) return;
        int i = PreferredFrequencyUnitCombo.SelectedIndex;
        string? name = i == 0 ? null : PreferredUnits.FrequencyChoices[i - 1];
        AppPreferencesIo.Update(p => p.PreferredFrequencyUnit = name);
    }

    private void OnWireClearanceChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingGeneral || WireClearanceUpDown.Value is not { } mil) return;
        WBondWireClearance.Mil = (double)mil;
    }

    private void OnWBondPointsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingGeneral || WBondPointsUpDown.Value is not { } points) return;
        AppPreferencesIo.Update(p => p.WBondWirePoints = (int)points);
    }

    private void OnWBondDiameterChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingGeneral || WBondDiameterUpDown.Value is not { } mils || mils <= 0) return;
        AppPreferencesIo.Update(p => p.WBondWireDiameterNm = WBondUnits.ToNm((double)mils, WBondUnit.Mil));
    }

    /// <summary>
    /// Wire z-height. <b>No positive guard</b>, unlike its neighbours: zero is a wire landing on the
    /// reference plane and a negative value is a foot in a cavity below it, so the only value this
    /// cannot take is "none" — which is what an empty box means and is why that is the one case
    /// skipped.
    /// </summary>
    private void OnWBondFootZChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingGeneral || WBondFootZUpDown.Value is not { } mils) return;
        AppPreferencesIo.Update(p => p.WBondWireFootZNm = WBondUnits.ToNm((double)mils, WBondUnit.Mil));
    }

    private void OnWBondPastePitchChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingGeneral || WBondPastePitchUpDown.Value is not { } mils || mils <= 0) return;
        AppPreferencesIo.Update(p => p.WBondPastePitchNm = WBondUnits.ToNm((double)mils, WBondUnit.Mil));
    }

    private void OnWBondMaterialChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral || WBondMaterialCombo.SelectedItem is not string material) return;
        AppPreferencesIo.Update(p => p.WBondWireMaterial = material);
    }

    private void OnMsgTimestampChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral || MsgTimestampCombo.SelectedIndex < 0) return;
        var mode = (MessageTimestampMode)MsgTimestampCombo.SelectedIndex;
        AppPreferencesIo.Update(p => p.MessageTimestamp = mode);
        MessageDisplay.Mode = mode;   // live
    }

    // ── Theme combo ──────────────────────────────────────────────────────────

    private void PopulateThemeCombo()
    {
        _updating = true;
        try
        {
            var names = ThemeResolver.DiscoverThemeNames(CurrentWorkspaceDirectory()).ToList();
            var activeName = ThemeService.Active.Name;
            var idx = names.IndexOf(activeName);

            if (idx >= 0 && !DiffersFromPreset(ThemeService.Active))
            {
                // Active theme matches the discovered preset exactly.
                ThemeCombo.ItemsSource = names;
                ThemeCombo.SelectedIndex = idx;
            }
            else
            {
                // Active colors differ from any preset (or preset name unknown) — show as "Custom".
                if (!names.Contains("Custom", StringComparer.OrdinalIgnoreCase))
                    names.Insert(0, "Custom");
                ThemeCombo.ItemsSource = names;
                ThemeCombo.SelectedItem = "Custom";
            }
        }
        finally { _updating = false; }
    }

    /// <summary>
    /// Returns true if the active theme's colors differ from its named preset.
    /// A name that resolves to a different preset (e.g. unsaved "Custom" falls back to built-in)
    /// is treated as differing. Compares all ColorRole.All for both Light and Dark variants.
    /// </summary>
    private bool DiffersFromPreset(ColorTheme active)
    {
        var preset = ThemeResolver.Resolve(active.Name, CurrentWorkspaceDirectory());
        if (!string.Equals(preset.Name, active.Name, StringComparison.OrdinalIgnoreCase))
            return true;  // resolver returned a fallback — name not found in any source
        foreach (var role in ColorRole.All)
        {
            if (active.Resolve(role, ColorVariant.Light) != preset.Resolve(role, ColorVariant.Light))
                return true;
            if (active.Resolve(role, ColorVariant.Dark)  != preset.Resolve(role, ColorVariant.Dark))
                return true;
        }
        return false;
    }

    private void OnThemeComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        if (ThemeCombo.SelectedItem is not string name) return;

        var resolved = ThemeResolver.Resolve(name, CurrentWorkspaceDirectory());
        ThemeService.Active = resolved;
        LoadThemeIntoEditor(resolved);
    }

    // ── Load a theme into the editor ─────────────────────────────────────────

    private void LoadThemeIntoEditor(ColorTheme theme)
    {
        var (light, dark) = theme.GetRoleMaps();

        // Copy role maps so we have mutable working copies.
        _workingLight = ColorRole.All
            .ToDictionary(r => r, r => theme.Resolve(r, ColorVariant.Light));
        _workingDark = ColorRole.All
            .ToDictionary(r => r, r => theme.Resolve(r, ColorVariant.Dark));

        PopulateRoleList();

        // Select first role if nothing selected.
        if (RoleList.SelectedIndex < 0 && RoleList.ItemCount > 0)
            RoleList.SelectedIndex = 0;
        else
            RefreshEditor();
    }

    // ── Role list ────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Every role is listed under its own key, prefix and all</b> (owner, 2026-08-16).
    ///
    /// <para>The schematic roles used to be shortened here — <c>Schematic.Wire</c> shown as "Wire",
    /// <c>System.Warning</c> as "Warning" — dating from when they were the only roles there were.
    /// Every family added since (<c>Layout.</c>, <c>Harmonica.</c>, <c>wBond.</c>) shows its full key,
    /// so the shortened dozen read as a nameless group at the top of a list of qualified ones, and
    /// three different colours all appeared as "Wire". Removing the map is the whole change: the row
    /// label already falls back to the role key.</para>
    /// </summary>
    private static readonly Dictionary<string, string> RoleLabels = [];

    private List<RoleRowModel> _roleRows = [];

    private void PopulateRoleList()
    {
        var variant = SelectedVariant;
        var map     = variant == ColorVariant.Dark ? _workingDark : _workingLight;

        _roleRows = ColorRole.All
            .Select(role => new RoleRowModel
            {
                Role        = role,
                Label       = RoleLabels.TryGetValue(role, out var lbl) ? lbl : role,
                SwatchColor = ToAvaloniaColor(map.GetValueOrDefault(role, new Rgba(128, 128, 128))),
            })
            .ToList();

        var prevIdx = RoleList.SelectedIndex;
        RoleList.ItemsSource = _roleRows;
        RoleList.SelectedIndex = Math.Clamp(prevIdx, 0, _roleRows.Count - 1);
    }

    private void OnVariantChanged(object? sender, RoutedEventArgs e)
    {
        RefreshAllSwatches();
        RefreshEditor();
    }

    private void RefreshAllSwatches()
    {
        if (_roleRows.Count == 0) return;
        var map = SelectedVariant == ColorVariant.Dark ? _workingDark : _workingLight;
        foreach (var row in _roleRows)
            row.SwatchColor = ToAvaloniaColor(map.GetValueOrDefault(row.Role, new Rgba(128, 128, 128)));
    }

    private void OnRoleSelected(object? sender, SelectionChangedEventArgs e) => RefreshEditor();

    private void RefreshEditor()
    {
        if (RoleList.SelectedItem is not RoleRowModel row) return;
        var map  = SelectedVariant == ColorVariant.Dark ? _workingDark : _workingLight;
        var rgba = map.GetValueOrDefault(row.Role, new Rgba(128, 128, 128));
        SetSlidersFromRgba(rgba);
        RoleNameLabel.Text = row.Role;
    }

    // ── Sliders & hex ────────────────────────────────────────────────────────

    private void SetSlidersFromRgba(Rgba c)
    {
        _updating = true;
        try
        {
            SliderR.Value = c.R;
            SliderG.Value = c.G;
            SliderB.Value = c.B;
            SliderA.Value = c.A;
            LabelR.Text = c.R.ToString();
            LabelG.Text = c.G.ToString();
            LabelB.Text = c.B.ToString();
            LabelA.Text = c.A.ToString();
            ColorPreviewRect.Fill = new SolidColorBrush(ToAvaloniaColor(c));
            HexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";
        }
        finally { _updating = false; }
    }

    private void OnSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        ApplyCurrentSliders();
    }

    // ── RGBA integer edit boxes ──────────────────────────────────────────────

    private void OnRgbaBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) ApplyRgbaBox(box);
    }

    private void OnRgbaBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Return)  { ApplyRgbaBox(box);  e.Handled = true; }
        else if (e.Key == Key.Escape) { RevertBox(box); e.Handled = true; }
    }

    private void ApplyRgbaBox(TextBox box)
    {
        if (_updating) return;
        if (!int.TryParse(box.Text, out int val)) { RevertBox(box); return; }
        val = Math.Clamp(val, 0, 255);
        var slider = BoxToSlider(box);
        if (slider is null) { RevertBox(box); return; }
        box.Text = val.ToString();   // normalize (removes leading zeros etc.)
        slider.Value = val;          // → OnSliderChanged → ApplyCurrentSliders → full sync
    }

    private void RevertBox(TextBox box)
    {
        if (_updating) return;
        var slider = BoxToSlider(box);
        if (slider is not null) box.Text = ((int)slider.Value).ToString();
    }

    private Slider? BoxToSlider(TextBox box) => box.Name switch
    {
        "LabelR" => SliderR,
        "LabelG" => SliderG,
        "LabelB" => SliderB,
        "LabelA" => SliderA,
        _        => null,
    };

    private void OnHexLostFocus(object? sender, RoutedEventArgs e) => ParseAndApplyHex();
    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            ParseAndApplyHex();
            e.Handled = true;   // prevent Return from reaching the window default button
        }
        else if (e.Key == Key.Escape)
        {
            RefreshEditor();    // revert hex display to the current working-map color
            e.Handled = true;
        }
    }

    private void ParseAndApplyHex()
    {
        var txt = HexBox.Text?.Trim().TrimStart('#') ?? "";
        if (txt.Length == 6) txt += "FF";
        if (txt.Length != 8) return;
        try
        {
            var val = Convert.ToUInt32(txt, 16);
            var c   = new Rgba((byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val);
            SetSlidersFromRgba(c);
            ApplyRgbaToActiveRole(c);
        }
        catch { }
    }

    private void ApplyCurrentSliders()
    {
        var c = new Rgba(
            (byte)SliderR.Value,
            (byte)SliderG.Value,
            (byte)SliderB.Value,
            (byte)SliderA.Value);

        _updating = true;
        try
        {
            LabelR.Text = c.R.ToString();
            LabelG.Text = c.G.ToString();
            LabelB.Text = c.B.ToString();
            LabelA.Text = c.A.ToString();
            ColorPreviewRect.Fill = new SolidColorBrush(ToAvaloniaColor(c));
            HexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";
        }
        finally { _updating = false; }

        ApplyRgbaToActiveRole(c);
    }

    private void ApplyRgbaToActiveRole(Rgba c)
    {
        if (RoleList.SelectedItem is not RoleRowModel row) return;

        // Fork to Custom if editing a non-Custom preset.
        ForkToCustomIfNeeded();

        var map = SelectedVariant == ColorVariant.Dark ? _workingDark : _workingLight;
        map[row.Role] = c;

        // Update the swatch in the list row.
        row.SwatchColor = ToAvaloniaColor(c);

        // Push live preview via ThemeService.
        PushLivePreview();
    }

    // ── Fork to Custom ───────────────────────────────────────────────────────

    private void ForkToCustomIfNeeded()
    {
        if (ThemeService.Active.Name == "Custom") return;

        // Rename working copies to "Custom" and update combo without re-loading maps.
        _updating = true;
        try
        {
            var names = ThemeResolver.DiscoverThemeNames(CurrentWorkspaceDirectory()).ToList();
            if (!names.Contains("Custom", StringComparer.OrdinalIgnoreCase))
                names.Insert(0, "Custom");

            ThemeCombo.ItemsSource = names;
            ThemeCombo.SelectedItem = "Custom";
        }
        finally { _updating = false; }
    }

    // ── Live preview ─────────────────────────────────────────────────────────

    private void PushLivePreview()
    {
        var name  = ThemeCombo.SelectedItem as string ?? "Custom";
        var theme = new ColorTheme(
            name,
            new Dictionary<string, Rgba>(_workingLight),
            new Dictionary<string, Rgba>(_workingDark));
        ThemeService.Active = theme;
    }

    // ── Save Theme ───────────────────────────────────────────────────────────

    private void OnSaveThemeClick(object? sender, RoutedEventArgs e)
    {
        var name = ThemeCombo.SelectedItem as string ?? "Custom";
        if (name == "Default") name = "Custom";

        var theme = new ColorTheme(
            name,
            new Dictionary<string, Rgba>(_workingLight),
            new Dictionary<string, Rgba>(_workingDark));

        try
        {
            ThemeResolver.SaveUserTheme(theme);
            ThemeService.Active = theme;

            // Persist as user preference.
            AppPreferencesIo.Update(p => p.ActiveThemeName = name);

            // Ensure name appears in combo.
            var names = ThemeResolver.DiscoverThemeNames(CurrentWorkspaceDirectory()).ToList();
            _updating = true;
            try
            {
                ThemeCombo.ItemsSource  = names;
                ThemeCombo.SelectedItem = name;
            }
            finally { _updating = false; }
        }
        catch (Exception ex)
        {
            // Surface the error without crashing.
            SaveThemeButton.Content = $"Save failed: {ex.Message[..Math.Min(40, ex.Message.Length)]}";
        }
    }

    // ── Help ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the Settings chapter of the User Documentation — every tab of this dialog, control by
    /// control. It is the other half of the tooltip pass: the short sentence lives on the control,
    /// and the paragraph that used to sit under it lives on that page.
    /// </summary>
    private void OnHelpClick(object? sender, RoutedEventArgs e)
        => DocLauncher.Open(SettingsDocPage);

    /// <summary>The page this dialog's Help button opens. Held by <c>DocAnchors.WholePages</c>.</summary>
    internal const string SettingsDocPage = "reference/settings.html";

    // ── Revert ───────────────────────────────────────────────────────────────

    private void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        ThemeService.Active = _originalTheme;
        LoadThemeIntoEditor(_originalTheme);
        PopulateThemeCombo();   // restores combo selection to original theme name
    }

    // ── Cancel / Close ───────────────────────────────────────────────────────

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        ThemeService.Active = _originalTheme;
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        // Persist the current active theme as the user preference.
        var activeName = ThemeService.Active.Name;
        // Null means "the shipped default" (ThemeResolver.DefaultThemeName), so choosing it clears
        // the preference rather than pinning a name that may move again.
        AppPreferencesIo.Update(p => p.ActiveThemeName =
            activeName != ThemeResolver.DefaultThemeName ? activeName : null);
        Close();
    }

    // ── Color picker (double-tap role) ───────────────────────────────────────

    private async void OnRoleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RoleList.SelectedItem is not RoleRowModel row) return;
        var map     = SelectedVariant == ColorVariant.Dark ? _workingDark : _workingLight;
        var current = map.GetValueOrDefault(row.Role, new Rgba(128, 128, 128));

        var dialog = new ColorPickerDialog(current);
        var result = await dialog.ShowDialog<Rgba?>(this);
        if (result is { } picked)
        {
            SetSlidersFromRgba(picked);
            ApplyRgbaToActiveRole(picked);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Avalonia.Media.Color ToAvaloniaColor(Rgba c)
        => new(c.A, c.R, c.G, c.B);
}

// Must be namespace-level (not nested) so XAML DataTemplate x:DataType can reference it.
internal sealed class RoleRowModel : System.ComponentModel.INotifyPropertyChanged
{
    public string Role  { get; init; } = "";
    public string Label { get; init; } = "";

    private Avalonia.Media.Color _swatchColor;
    public Avalonia.Media.Color SwatchColor
    {
        get => _swatchColor;
        set
        {
            if (_swatchColor == value) return;
            _swatchColor = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SwatchColor)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
