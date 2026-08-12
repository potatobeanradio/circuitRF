using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CircuitRF.Core.Devices.External;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// §4.3's <i>Set DUT…</i>. Every decision lives in <see cref="HarmonicaDutEditor"/> and
/// <see cref="HarmonicaDutCatalog"/>; what is here is the window, the two pickers, and the row
/// widgets — the parts a headless test cannot construct anyway.
/// </summary>
public partial class HarmonicaSetDutDialog : Window
{
    private HarmonicaDutEditor _editor = new(new DutSpec { Kind = DutKind.Sdd, TypeName = "SDD" });
    private ExternalDeviceDescriptor? _descriptor;
    private IReadOnlyList<HarmonicaExternalType> _types = [];
    private bool _loading;

    public HarmonicaSetDutDialog() => InitializeComponent();

    public HarmonicaSetDutDialog(DutSpec current) : this()
    {
        _editor = new HarmonicaDutEditor(current);

        _loading = true;
        LawCombo.ItemsSource = HarmonicaDutCatalog.NativeFetLaws.Select(l => l.Display).ToList();

        KindSdd.IsChecked      = current.Kind == DutKind.Sdd;
        KindFet.IsChecked      = current.Kind == DutKind.NativeFet;
        KindDiode.IsChecked    = current.Kind == DutKind.Diode;
        KindExternal.IsChecked = current.Kind == DutKind.External;

        int law = IndexOfLaw(current.TypeName);
        LawCombo.SelectedIndex = law >= 0 ? law : 0;

        SddPorts2.IsChecked = current.SddPortCount != 3;
        SddPorts3.IsChecked = current.SddPortCount == 3;

        // A provider name is either the built-in file form or a kit name — the built-in resolver's
        // own spelling decides which, so there is no second rule for telling them apart.
        string? file = current.Provider is null ? null : VerilogAFileResolver.ModelFileIn(current.Provider);
        SourceFile.IsChecked = file is not null || current.Kind != DutKind.External;
        SourceKit.IsChecked  = current.Kind == DutKind.External && file is null && current.Provider is not null;
        ModelFileBox.Text    = file ?? "";

        RefreshKits();
        if (file is null && current.Provider is { Length: > 0 })
            KitCombo.SelectedItem = current.Provider;

        _loading = false;

        ApplyKindVisibility();
        RefreshExternalTypes(current.TypeName);
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    /// <summary>The DUT the user settled on, or null when the dialog was cancelled.</summary>
    public static async Task<DutSpec?> ShowAsync(Window owner, DutSpec current)
        => await new HarmonicaSetDutDialog(current).ShowDialog<DutSpec?>(owner);

    // ── kind ──────────────────────────────────────────────────────────────────

    private void OnKindChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;

        _editor.SetKind(
            KindFet.IsChecked      == true ? DutKind.NativeFet :
            KindDiode.IsChecked    == true ? DutKind.Diode :
            KindExternal.IsChecked == true ? DutKind.External : DutKind.Sdd);

        if (_editor.Kind == DutKind.NativeFet && LawCombo.SelectedIndex >= 0)
            _editor.SetNativeLaw(HarmonicaDutCatalog.NativeFetLaws[LawCombo.SelectedIndex].TypeName);

        ApplyKindVisibility();
        if (_editor.Kind == DutKind.External) RefreshExternalTypes(_editor.TypeName);
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    private void ApplyKindVisibility()
    {
        bool fet = _editor.Kind == DutKind.NativeFet;
        bool ext = _editor.Kind == DutKind.External;
        bool sdd = _editor.Kind == DutKind.Sdd;

        LawLabel.IsVisible        = fet;
        LawCombo.IsVisible        = fet;
        SddChooser.IsVisible      = sdd;
        ExternalChooser.IsVisible = ext;
        MappingPanel.IsVisible    = ext;

        FileRow.IsVisible = ext && SourceFile.IsChecked == true;
        KitRow.IsVisible  = ext && SourceKit.IsChecked  == true;
    }

    /// <summary>R-h9c-11 — SDD2 vs SDD3.</summary>
    private void OnSddPortsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized || _editor.Kind != DutKind.Sdd) return;
        _editor.SddPortCount = SddPorts3.IsChecked == true ? 3 : 2;
        RefreshStatus();
    }

    private void OnLawChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || LawCombo.SelectedIndex < 0 || _editor.Kind != DutKind.NativeFet) return;
        _editor.SetNativeLaw(HarmonicaDutCatalog.NativeFetLaws[LawCombo.SelectedIndex].TypeName);
        RebuildParameterRows();
        RefreshStatus();
    }

    private static int IndexOfLaw(string typeName)
    {
        for (int i = 0; i < HarmonicaDutCatalog.NativeFetLaws.Count; i++)
            if (string.Equals(HarmonicaDutCatalog.NativeFetLaws[i].TypeName, typeName,
                              StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ── external: a file, or a kit ────────────────────────────────────────────

    private void OnExternalSourceChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        ApplyKindVisibility();
        OnProviderCommitted(sender, e);
    }

    private void OnProviderCommitted(object? sender, RoutedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.External) return;
        RefreshExternalTypes(null);
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    private async void OnBrowseModelFileClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a compiled model",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Compiled model") { Patterns = ["*.osdi"] }],
        });
        if (picked.Count == 0) return;

        ModelFileBox.Text = picked[0].Path.LocalPath;
        OnProviderCommitted(sender, e);
    }

    private async void OnAddKitFolderClick(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder holding device kits",
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;

        HarmonicaDutCatalog.AddKitFolder(picked[0].Path.LocalPath);
        RefreshKits();
        OnProviderCommitted(sender, e);
    }

    private void RefreshKits()
    {
        object? keep = KitCombo.SelectedItem;
        KitCombo.ItemsSource = HarmonicaDutCatalog.KitNames();
        if (keep is not null) KitCombo.SelectedItem = keep;
    }

    private string? CurrentProvider()
    {
        if (SourceKit.IsChecked == true)
            return KitCombo.SelectedItem as string;

        string path = ModelFileBox.Text?.Trim() ?? "";
        return path.Length == 0 ? null : HarmonicaDutCatalog.ProviderForModelFile(path);
    }

    private void RefreshExternalTypes(string? preferType)
    {
        if (_editor.Kind != DutKind.External)
        {
            _types = [];
            _descriptor = null;
            TypeCombo.ItemsSource = null;
            return;
        }

        string? provider = CurrentProvider();
        _types = provider is null ? [] : HarmonicaDutCatalog.Describe(provider, out string? err);
        if (provider is null) _typeError = null;
        else _typeError = _types.Count == 0 ? DescribeError(provider) : null;

        TypeCombo.ItemsSource = _types.Select(t => t.Display).ToList();

        int idx = preferType is { Length: > 0 }
            ? _types.ToList().FindIndex(t => string.Equals(t.TypeId, preferType, StringComparison.Ordinal))
            : -1;
        TypeCombo.SelectedIndex = idx >= 0 ? idx : (_types.Count > 0 ? 0 : -1);

        CommitSelectedType(provider);
    }

    private string? _typeError;

    private string? DescribeError(string provider)
    {
        HarmonicaDutCatalog.Describe(provider, out string? err);
        return err;
    }

    private void CommitSelectedType(string? provider)
    {
        int i = TypeCombo.SelectedIndex;
        _descriptor = i >= 0 && i < _types.Count ? _types[i].Descriptor : null;
        _editor.SetExternal(provider ?? "", _descriptor);
    }

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _editor.Kind != DutKind.External) return;
        CommitSelectedType(CurrentProvider());
        RebuildParameterRows();
        RefreshMapping();
        RefreshStatus();
    }

    // ── parameters (R-h8-2 — read from the model, never a table) ──────────────

    private void RebuildParameterRows()
    {
        ParamHost.Children.Clear();

        // The one reader. HarmonicaInputs.DeclaredModelParameters answers "what does this model
        // declare" for every kind, so the dialog does not get a second opinion.
        var probe = HarmonicaViewModel.DefaultModel() with { Dut = _editor.Build() };
        var declared = HarmonicaInputs.DeclaredModelParameters(probe);

        ParamHeader.Text = declared.Count == 0
            ? "This model declares no parameters here."
            : $"{declared.Count} parameter(s), as the model itself declares them";

        foreach (var input in declared)
        {
            string name = input.Key[HarmonicaInputs.ParameterPrefix.Length..];

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,*,44"),
                ColumnSpacing     = 6,
            };

            var label = new TextBlock
            {
                Text = name, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(label, input.Tooltip);
            Grid.SetColumn(label, 0);

            var box = new TextBox { Text = input.Text, Tag = name };
            box.LostFocus += (_, _) => { _editor.SetParameter(name, box.Text ?? ""); RefreshStatus(); };
            Grid.SetColumn(box, 1);

            var unit = new TextBlock
            {
                Text = input.Unit, Opacity = 0.55, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(unit, 2);

            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(unit);
            ParamHost.Children.Add(row);

            // Seed the editor with whatever is being shown, so a parameter the user never touches
            // still travels — a declared default that vanished because nobody clicked in the box
            // would be a device configured differently from the one on screen.
            _editor.SetParameter(name, input.Text);
        }
    }

    // ── §4.5.5 — the mapping, offered from the model's OWN node names ─────────

    private void RefreshMapping()
    {
        var choices = HarmonicaDutEditor.NodeChoices(_descriptor);
        var withNone = new List<string> { "(not named)" };
        withNone.AddRange(choices);

        _loading = true;
        foreach (var (combo, value) in new (ComboBox, string?)[]
                 {
                     (GateCombo,   _editor.GateNode),
                     (DrainCombo,  _editor.DrainNode),
                     (SourceCombo, _editor.SourcePin),
                 })
        {
            combo.ItemsSource = withNone;
            int i = value is { Length: > 0 } ? withNone.IndexOf(value) : 0;
            combo.SelectedIndex = i >= 0 ? i : 0;
            combo.IsEnabled = choices.Count > 0;
        }
        _loading = false;
    }

    private void OnMappingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _editor.GateNode  = Selected(GateCombo);
        _editor.DrainNode = Selected(DrainCombo);
        _editor.SourcePin = Selected(SourceCombo);
        RefreshStatus();

        static string? Selected(ComboBox c)
            => c.SelectedIndex <= 0 ? null : c.SelectedItem as string;
    }

    // ── status + commit ───────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        string? problem = _typeError ?? _editor.Validate();

        if (problem is null && _editor.Kind == DutKind.External
            && _editor.Build().IntrinsicMapping is null)
            problem = "The intrinsic plane is not named: the glyphs and the loadline will be empty.";

        StatusLabel.Text = problem ?? "";
        ApplyButton.IsEnabled = _typeError is null && _editor.Validate() is null;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_editor.Validate() is not null) return;
        Close(_editor.Build());
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
