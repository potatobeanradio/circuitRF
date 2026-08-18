using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class SettingsView : Window
{
    // ── State ────────────────────────────────────────────────────────────────

    private readonly string? _workspaceDirPath;

    // Guard: prevents recursive writes during programmatic ComboBox population.
    private bool _updatingGeneral;

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

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public SettingsView() : this(null) { }

    public SettingsView(string? workspaceDirPath)
    {
        _workspaceDirPath = workspaceDirPath;
        _originalTheme    = ThemeService.Active;

        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadGeneralPrefs();
        PopulateThemeCombo();

        // Open on whichever variant circuitRF is actually rendering right now, not a hardcoded
        // default — otherwise the editor shows colors the user isn't looking at.
        DarkRadio.IsChecked  = ThemeService.CurrentVariant == ColorVariant.Dark;
        LightRadio.IsChecked = ThemeService.CurrentVariant != ColorVariant.Dark;

        LoadThemeIntoEditor(ThemeService.Active);
    }

    // ── General tab ──────────────────────────────────────────────────────────

    private void LoadGeneralPrefs()
    {
        _updatingGeneral = true;
        try
        {
            var prefs = AppPreferencesIo.Load();

            LaunchActionCombo.ItemsSource = new[]
            {
                "Welcome", "New Schematic", "New Workspace", "Open Workspace",
                "New Data Display", "New Symbol", "New Layout", "harmonicaRF",
            };
            LaunchActionCombo.SelectedIndex = (int)(prefs.LaunchAction ?? LaunchAction.Welcome);

            // Index == enum ordinal, and the ordinals are a FILE FORMAT (see WindowLayout) —
            // reordering these strings silently changes what every saved preference means.
            WindowLayoutCombo.ItemsSource   = new[] { "Project Tree Focus", "Library Focus", "Project Tree & Library" };
            WindowLayoutCombo.SelectedIndex = (int)(prefs.WindowLayout ?? WindowLayout.ProjectTreeAndLibrary);

            ShowDockersOnLaunchCheck.IsChecked = prefs.ShowDockersOnLaunch ?? true;

            CopyColorCombo.ItemsSource   = new[] { "Follow System", "Force Light", "Force Dark" };
            CopyColorCombo.SelectedIndex = (int)(prefs.CopyColorMode ?? CopyColorMode.FollowSystem);

            TransparentBgCheck.IsChecked = prefs.CopyTransparentBackground ?? true;

            CheckDrcOnExportCheck.IsChecked = prefs.CheckDrcOnExport ?? true;

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

            UpdatePCellTrustStatus(prefs.PCellTrust?.Count ?? 0);
        }
        finally { _updatingGeneral = false; }
    }

    private void OnLaunchSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p =>
        {
            if (LaunchActionCombo.SelectedIndex >= 0)
                p.LaunchAction = (LaunchAction)LaunchActionCombo.SelectedIndex;
            if (WindowLayoutCombo.SelectedIndex >= 0)
                p.WindowLayout = (WindowLayout)WindowLayoutCombo.SelectedIndex;
        });
    }

    private void OnShowDockersOnLaunchChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingGeneral) return;
        AppPreferencesIo.Update(p => p.ShowDockersOnLaunch = ShowDockersOnLaunchCheck.IsChecked);
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

    // ── Generated artwork permissions ────────────────────────────────────────
    //
    // The one way back from "Don't Allow". A refusal is recorded (otherwise the prompt nags, and a
    // prompt that nags is one people learn to dismiss unread), so there has to be somewhere to
    // reverse it. Global rather than per-workspace because the decisions themselves are.

    private void UpdatePCellTrustStatus(int remembered)
        => PCellTrustStatus.Text = remembered == 0
            ? "Nothing remembered."
            : remembered == 1 ? "1 kit remembered." : $"{remembered} kits remembered.";

    private void OnForgetPCellTrustClick(object? sender, RoutedEventArgs e)
    {
        CircuitRF.Ui.Layout.PCells.Wire.PCellTrustPreferences.Forget();
        UpdatePCellTrustStatus(0);
    }

    // ── Theme combo ──────────────────────────────────────────────────────────

    private void PopulateThemeCombo()
    {
        _updating = true;
        try
        {
            var names = ThemeResolver.DiscoverThemeNames(_workspaceDirPath).ToList();
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
        var preset = ThemeResolver.Resolve(active.Name, _workspaceDirPath);
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

        var resolved = ThemeResolver.Resolve(name, _workspaceDirPath);
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
            var names = ThemeResolver.DiscoverThemeNames(_workspaceDirPath).ToList();
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
            var names = ThemeResolver.DiscoverThemeNames(_workspaceDirPath).ToList();
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
