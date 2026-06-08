using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

public partial class SettingsView : Window
{
    // ── State ────────────────────────────────────────────────────────────────

    private readonly string? _workspaceDirPath;

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
        PopulateThemeCombo();
        LoadThemeIntoEditor(ThemeService.Active);
    }

    // ── Theme combo ──────────────────────────────────────────────────────────

    private void PopulateThemeCombo()
    {
        _updating = true;
        try
        {
            var names = ThemeResolver.DiscoverThemeNames(_workspaceDirPath);
            ThemeCombo.ItemsSource = names;

            var activeName = ThemeService.Active.Name;
            var idx = names.ToList().IndexOf(activeName);
            ThemeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        finally { _updating = false; }
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

    private static readonly Dictionary<string, string> RoleLabels = new()
    {
        [ColorRole.SchematicBackground]        = "Background",
        [ColorRole.SchematicGrid]              = "Grid",
        [ColorRole.SchematicWire]              = "Wire",
        [ColorRole.SchematicNodeLabelText]     = "Node Label Text",
        [ColorRole.SchematicInstanceNameText]  = "Instance Name Text",
        [ColorRole.SchematicParameterNameText] = "Parameter Text",
        [ColorRole.SchematicComponentNameText] = "Component Name Text",
        [ColorRole.SchematicConnectedPin]      = "Connected Pin",
        [ColorRole.SchematicWireJunctionDot]   = "Wire Junction Dot",
        [ColorRole.SchematicSymbolLine]        = "Symbol Lines",
        [ColorRole.SchematicSymbolPlus]        = "Symbol +/−",
        [ColorRole.SystemWarning]              = "Warning",
    };

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

    private void OnHexLostFocus(object? sender, RoutedEventArgs e) => ParseAndApplyHex();
    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) ParseAndApplyHex();
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
            AppPreferencesIo.Save(new AppPreferences { ActiveThemeName = name });

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
        AppPreferencesIo.Save(new AppPreferences
        {
            ActiveThemeName = activeName != "Default" ? activeName : null,
        });
        Close();
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
