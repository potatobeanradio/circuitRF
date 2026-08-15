using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-harmonicarf-r6a §2.2 — §7.9.4's colour editor, and the rest of harmonicaRF's Appearance
/// settings, lifted from the former <c>HarmonicaPreferencesDialog</c> into a
/// <see cref="HarmonicaSettingsDialog"/> tab.
///
/// <para><b>brief-harmonicarf-r9b</b> re-lays this view out to match circuitRF's own Color Theme tab
/// (<see cref="SettingsView"/>) — a role list with a colour swatch per row, double-click-a-swatch to
/// open <see cref="ColorPickerDialog"/>, RGBA sliders + boxes, and a hex field — while keeping the one
/// structural difference the owner did not ask to remove: every edit still writes straight through
/// <see cref="HarmonicaColorEditor.Set"/> immediately, with no theme combo, no working copies and no
/// deferred commit. See the header comment in the paired <c>.axaml</c> for why.</para>
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
    private List<RoleRowModel> _roleRows = [];

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

    private void PopulateRoles()
    {
        int keep = RoleList.SelectedIndex;
        _roleRows = HarmonicaColorEditor.Roles
            .Select(r => new RoleRowModel
            {
                Role        = r,
                Label       = HarmonicaColorEditor.LabelFor(r),
                SwatchColor = ToAvaloniaColor(_editor.Resolve(r, Variant)),
            })
            .ToList();
        RoleList.ItemsSource = _roleRows;
        RoleList.SelectedIndex = Math.Clamp(keep, 0, _roleRows.Count - 1);
    }

    private string? SelectedRole => (RoleList.SelectedItem as RoleRowModel)?.Role;

    private void OnRoleSelected(object? sender, SelectionChangedEventArgs e) => RefreshEditor();

    private void OnVariantChanged(object? sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        // R9B — without this, flipping Light/Dark leaves the whole list showing the other variant's
        // colours; the same fix SettingsView already carries (its own RefreshAllSwatches).
        RefreshAllSwatches();
        RefreshEditor();
    }

    private void RefreshAllSwatches()
    {
        if (_roleRows.Count == 0) return;
        foreach (var row in _roleRows)
            row.SwatchColor = ToAvaloniaColor(_editor.Resolve(row.Role, Variant));
    }

    private void RefreshEditor()
    {
        if (SelectedRole is not { } role) return;

        var c = _editor.Resolve(role, Variant);
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
            RefreshRoleStateLabels();
        }
        finally { _updating = false; }
    }

    /// <summary>The role-path label and the two buttons' enablement — split out from
    /// <see cref="RefreshEditor"/> so a slider/box edit can update them without re-writing (and
    /// re-triggering) the sliders it just came from.</summary>
    private void RefreshRoleStateLabels()
    {
        if (SelectedRole is not { } role) return;
        RoleNameLabel.Text = role + (_editor.IsOverridden(role, Variant) ? "  (edited)" : "");
        RevertButton.IsEnabled = _editor.IsOverridden(role, ColorVariant.Light)
                               || _editor.IsOverridden(role, ColorVariant.Dark);
        ResetAllButton.IsEnabled = !_editor.IsDefault;
    }

    // ── sliders & RGBA boxes ─────────────────────────────────────────────────

    private void OnSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        ApplyCurrentSliders();
    }

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
        if (SelectedRole is not { } role) return;
        _editor.Set(role, Variant, c);                       // live, immediate — see .axaml header
        if (RoleList.SelectedItem is RoleRowModel row) row.SwatchColor = ToAvaloniaColor(c);
        // RoleNameLabel's "(edited)" suffix and RevertButton's enablement both move with this.
        RefreshRoleStateLabels();
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

    // ── colour picker (double-tap a role) ────────────────────────────────────

    private async void OnRoleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SelectedRole is not { } role) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        // ColorPickerDialog already carries the ColorView Fluent-theme include the .axaml header
        // warns about — ColorView instantiates BLANK without it, and fails silently.
        var picked = await new ColorPickerDialog(_editor.Resolve(role, Variant)).ShowDialog<Rgba?>(owner);
        if (picked is { } c) { SetSlidersFromRgba(c); ApplyRgbaToActiveRole(c); }
    }

    private void SetSlidersFromRgba(Rgba c)
    {
        _updating = true;
        try
        {
            SliderR.Value = c.R;
            SliderG.Value = c.G;
            SliderB.Value = c.B;
            SliderA.Value = c.A;
        }
        finally { _updating = false; }
    }

    private void OnRevertClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedRole is { } role) _editor.Revert(role);
        // R9B — Revert changes the resolved colour without the sliders/hex touching it; the row's
        // own swatch needs the same refresh RefreshEditor's caller-side siblings already get.
        RefreshAllSwatches();
        RefreshEditor();
    }

    private void OnResetAllClick(object? sender, RoutedEventArgs e)
    {
        _editor.ResetAllColours();
        StatusLabel.Text = "All colours reset to the built-in defaults.";
        RefreshAllSwatches();   // R9B — every row's swatch can change here, not just the selected one.
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

        RefreshAllSwatches();   // R9B — an import can rewrite many roles at once.
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

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Avalonia.Media.Color ToAvaloniaColor(Rgba c) => new(c.A, c.R, c.G, c.B);
}
