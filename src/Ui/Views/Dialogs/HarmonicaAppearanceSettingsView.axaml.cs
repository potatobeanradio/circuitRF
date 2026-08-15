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
/// brief-harmonicarf-r6a §2.2 — §7.9.4's colour editor, and the rest of harmonicaRF's Appearance
/// settings, lifted from the former <c>HarmonicaPreferencesDialog</c> (its own code-behind, unchanged
/// in behaviour) into a <see cref="HarmonicaSettingsDialog"/> tab.
///
/// <para><b>Live preview is free and must stay free (R-h7-16).</b> Every edit here writes
/// <c>CharmAppearance</c> through <see cref="HarmonicaColorEditor"/>, which re-projects
/// <c>HarmonicaRenderTheme</c> and invalidates the canvas — no re-solve, no re-fit and specifically
/// no RBF re-factorization. That holds by construction: this view can reach the appearance and
/// nothing else.</para>
/// </summary>
public partial class HarmonicaAppearanceSettingsView : UserControl
{
    private HarmonicaColorEditor _editor = null!;
    private bool _updating;

    public HarmonicaAppearanceSettingsView() => InitializeComponent();

    /// <summary>Called once by the hosting dialog, before this view is shown — mirrors the former
    /// dialog's own constructor-time setup.</summary>
    public void Attach(HarmonicaViewModel vm)
    {
        _editor = vm.ColorEditor;

        DarkRadio.IsChecked  = vm.Variant == ColorVariant.Dark;
        LightRadio.IsChecked = vm.Variant == ColorVariant.Light;

        PopulateRoles();
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
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // ColorPickerDialog already carries the ColorView Fluent-theme include §7.9.4 warns about.
        var picked = await new ColorPickerDialog(_editor.Resolve(role, Variant)).ShowDialog<Rgba?>(owner);
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

    // ── .ccolor interchange ──────────────────────────────────────────────────

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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
}
