using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// §7.9.4's colour editor, and the rest of harmonicaRF's Preferences.
///
/// <para><b>Live preview is free and must stay free (R-h7-16).</b> Every edit here writes
/// <c>CharmAppearance</c> through <see cref="HarmonicaColorEditor"/>, which re-projects
/// <c>HarmonicaRenderTheme</c> and invalidates the canvas — no re-solve, no re-fit and specifically
/// no RBF re-factorization. That holds by construction: this dialog can reach the appearance and
/// nothing else.</para>
///
/// <para><b>The two inherited fixes are reused, not re-derived</b> (§7.9.4): the hex field's
/// Return-applies-and-handles / Escape-reverts / LostFocus-applies contract with <c>RRGGBBAA</c> and
/// a six-digit entry taken as opaque, and <c>ColorView</c>'s Fluent theme — the latter by going
/// through <see cref="ColorPickerDialog"/>, which already carries it.</para>
/// </summary>
public partial class HarmonicaPreferencesDialog : Window
{
    private readonly HarmonicaViewModel _vm;
    private readonly HarmonicaColorEditor _editor;
    private bool _updating;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaPreferencesDialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaPreferencesDialog(HarmonicaViewModel vm)
    {
        _vm     = vm;
        _editor = vm.ColorEditor;
        InitializeComponent();

        DarkRadio.IsChecked  = vm.Variant == ColorVariant.Dark;
        LightRadio.IsChecked = vm.Variant == ColorVariant.Light;

        PopulateRoles();
        LoadFade();
        LoadTickleDefault();
        if (RoleList.ItemCount > 0) RoleList.SelectedIndex = 0;
    }

    private ColorVariant Variant => DarkRadio.IsChecked == true ? ColorVariant.Dark : ColorVariant.Light;

    // ── the role list ────────────────────────────────────────────────────────

    private sealed record RoleRow(string Role, string Label)
    {
        public override string ToString() => Label;
    }

    private void PopulateRoles()
    {
        int keep = RoleList.SelectedIndex;
        RoleList.ItemsSource = HarmonicaColorEditor.Roles
            .Select(r => new RoleRow(r, HarmonicaColorEditor.LabelFor(r)))
            .ToList();
        RoleList.SelectedIndex = Math.Clamp(keep, 0, HarmonicaColorEditor.Roles.Count - 1);
    }

    private string? SelectedRole => (RoleList.SelectedItem as RoleRow)?.Role;

    private void OnRoleSelected(object? sender, SelectionChangedEventArgs e) => RefreshEditor();

    private void OnVariantChanged(object? sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        RefreshEditor();
    }

    private void RefreshEditor()
    {
        if (SelectedRole is not { } role) return;

        var c = _editor.Resolve(role, Variant);
        _updating = true;
        try
        {
            RoleNameLabel.Text     = role + (_editor.IsOverridden(role, Variant) ? "  (edited)" : "");
            ColorPreviewRect.Background = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
            HexBox.Text            = $"{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";
            RevertButton.IsEnabled = _editor.IsOverridden(role, ColorVariant.Light)
                                  || _editor.IsOverridden(role, ColorVariant.Dark);
            ResetAllButton.IsEnabled = !_editor.IsDefault;
        }
        finally { _updating = false; }
    }

    // ── the hex field — the inherited key handling, verbatim in behaviour ────

    private void OnHexLostFocus(object? sender, RoutedEventArgs e) => ParseAndApplyHex();

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            ParseAndApplyHex();
            // Without this the window's default button takes the Return and the dialog closes
            // instead of applying — the exact defect SettingsView already absorbed.
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RefreshEditor();          // revert to the working value
            e.Handled = true;
        }
    }

    private void ParseAndApplyHex()
    {
        if (_updating || SelectedRole is not { } role) return;

        string txt = HexBox.Text?.Trim().TrimStart('#') ?? "";
        if (txt.Length == 6) txt += "FF";        // a six-digit entry is fully opaque
        if (txt.Length != 8) { RefreshEditor(); return; }

        try
        {
            uint val = Convert.ToUInt32(txt, 16);
            _editor.Set(role, Variant,
                        new Rgba((byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val));
        }
        catch (FormatException) { }
        catch (OverflowException) { }

        RefreshEditor();
    }

    private async void OnPickClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedRole is not { } role) return;

        // ColorPickerDialog already carries the ColorView Fluent-theme include §7.9.4 warns about.
        var picked = await new ColorPickerDialog(_editor.Resolve(role, Variant)).ShowDialog<Rgba?>(this);
        if (picked is { } c) _editor.Set(role, Variant, c);
        RefreshEditor();
    }

    private void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedRole is { } role) _editor.Revert(role);
        RefreshEditor();
    }

    private void OnResetAllClick(object? sender, RoutedEventArgs e)
    {
        _editor.ResetAllColours();
        StatusLabel.Text = "All colours reset to the built-in defaults.";
        RefreshEditor();
    }

    // ── §7.2's fade parameters ───────────────────────────────────────────────

    private void LoadFade()
    {
        _updating = true;
        try
        {
            AlphaFloorSlider.Value = _editor.IsoAlphaFloor;
            AlphaExpSlider.Value   = _editor.IsoAlphaExponent;
            IsoLabelsCheck.IsChecked = _editor.ShowIsoLineLabels;
            AlphaFloorLabel.Text = _editor.IsoAlphaFloor.ToString("0.00");
            AlphaExpLabel.Text   = _editor.IsoAlphaExponent.ToString("0.00");
        }
        finally { _updating = false; }
    }

    private void OnFadeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _editor.IsoAlphaFloor    = AlphaFloorSlider.Value;
        _editor.IsoAlphaExponent = AlphaExpSlider.Value;
        AlphaFloorLabel.Text = AlphaFloorSlider.Value.ToString("0.00");
        AlphaExpLabel.Text   = AlphaExpSlider.Value.ToString("0.00");
    }

    private void OnIsoLabelsChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _editor.ShowIsoLineLabels = IsoLabelsCheck.IsChecked == true;
        _vm.ShowIsoLineLabels     = _editor.ShowIsoLineLabels;
    }

    // ── R-h9r2-18a — the tickle default a brand new document seeds from ─────────

    private void LoadTickleDefault()
    {
        _updating = true;
        try
        {
            TickleDefaultEnabledCheck.IsChecked = HarmonicaTickleDefaults.Enabled;
            TickleDefaultDbmBox.Text = HarmonicaTickleDefaults.Dbm.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            TickleDefaultDbmBox.IsEnabled = HarmonicaTickleDefaults.Enabled;
        }
        finally { _updating = false; }
    }

    private void OnTickleDefaultChanged(object? sender, RoutedEventArgs e)
    {
        if (_updating) return;
        TickleDefaultDbmBox.IsEnabled = TickleDefaultEnabledCheck.IsChecked == true;
        CommitTickleDefault();
    }

    private void OnTickleDefaultDbmLostFocus(object? sender, RoutedEventArgs e) => CommitTickleDefault();

    private void OnTickleDefaultDbmKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) { CommitTickleDefault(); e.Handled = true; }
        else if (e.Key == Key.Escape) { LoadTickleDefault(); e.Handled = true; }
    }

    private void CommitTickleDefault()
    {
        if (_updating) return;
        if (!double.TryParse(TickleDefaultDbmBox.Text, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double dbm))
        {
            LoadTickleDefault();
            return;
        }

        bool enabled = TickleDefaultEnabledCheck.IsChecked == true;
        AppPreferencesIo.Update(p =>
        {
            p.HarmonicaTickleEnabled = enabled;
            p.HarmonicaTickleDbm     = dbm;
        });
    }

    // ── .ccolor interchange ──────────────────────────────────────────────────

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import .ccolor",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Colour theme") { Patterns = ["*.ccolor"] }],
        });
        if (picked.Count == 0) return;

        try
        {
            var (light, dark) = _editor.ImportCcolor(
                await System.IO.File.ReadAllTextAsync(picked[0].Path.LocalPath));
            StatusLabel.Text = light + dark == 0
                ? $"'{picked[0].Name}' carries no Harmonica.* roles — nothing changed."
                : $"Imported {light} light and {dark} dark roles.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }

        RefreshEditor();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export .ccolor",
            DefaultExtension = "ccolor",
            SuggestedFileName = "harmonica.ccolor",
        });
        if (file is null) return;

        try
        {
            await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, _editor.ExportCcolor());
            StatusLabel.Text = $"Exported to {file.Name}.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
