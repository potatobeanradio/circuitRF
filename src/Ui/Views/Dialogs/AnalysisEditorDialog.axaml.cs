using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CircuitRF.Core.Design;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Add/Edit Analysis dialog (analysis-authoring.md §4.2).
/// Returns the committed <see cref="Analysis"/> list via
/// <c>ShowDialog&lt;IReadOnlyList&lt;Analysis&gt;?&gt;</c>, or null on Cancel.
/// Open via the static <see cref="ShowAsync"/> factory, which handles null-owner
/// fall-back using the ResolveOwner pattern from WorkspaceViewModel.
/// </summary>
public partial class AnalysisEditorDialog : Window
{
    private AnalysisEditorViewModel Vm => (AnalysisEditorViewModel)DataContext!;

    // Prevents re-entrancy when we programmatically sync NameBox.Text ← vm.Name.
    private bool _suppressNameSync;

    public AnalysisEditorDialog() => InitializeComponent();

    public AnalysisEditorDialog(AnalysisEditorViewModel vm, bool isEdit = false) : this()
    {
        DataContext = vm;
        SpBodyViewControl.DataContext = vm.SpBody;
        HbBodyViewControl.DataContext = vm.HbBody;
        LpBodyViewControl.DataContext = vm.LpBody;
        LppBodyViewControl.DataContext = vm.LppBody;

        var heading = isEdit ? "Edit Analysis" : "Add Analysis";
        DialogTitle.Text = heading;
        Title            = heading;

        // Seed control state from VM (radio, name, enabled)
        DcRadio.IsChecked = vm.IsDc;
        SpRadio.IsChecked = vm.IsSp;
        HbRadio.IsChecked = vm.IsHb;
        LpRadio.IsChecked = vm.IsLp;
        LppRadio.IsChecked = vm.IsLpp;
        NameBox.Text           = vm.Name;
        EnabledCheck.IsChecked = vm.Enabled;

        // Propagate VM state changes back to controls.
        vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AnalysisEditorViewModel.CanCommit):
                    OkButton.IsEnabled = vm.CanCommit;
                    break;
                case nameof(AnalysisEditorViewModel.Name):
                    _suppressNameSync = true;
                    NameBox.Text = vm.Name;
                    _suppressNameSync = false;
                    UpdateNameError(vm.NameError);
                    break;
                case nameof(AnalysisEditorViewModel.NameError):
                    UpdateNameError(vm.NameError);
                    break;
            }
        };

        UpdateBodyPanels(vm.Type);
        UpdateNameError(vm.NameError);
        OkButton.IsEnabled = vm.CanCommit;
    }

    // ── Type radio ────────────────────────────────────────────────────────────

    private void OnTypeRadioChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;

        var kind = rb.Name switch
        {
            nameof(DcRadio) => AnalysisEditorViewModel.AnalysisKind.DC,
            nameof(SpRadio) => AnalysisEditorViewModel.AnalysisKind.SP,
            nameof(HbRadio) => AnalysisEditorViewModel.AnalysisKind.HB,
            nameof(LpRadio) => AnalysisEditorViewModel.AnalysisKind.LP,
            nameof(LppRadio) => AnalysisEditorViewModel.AnalysisKind.LPP,
            _               => Vm.Type,
        };
        Vm.Type = kind;
        UpdateBodyPanels(kind);
    }

    private void UpdateBodyPanels(AnalysisEditorViewModel.AnalysisKind kind)
    {
        DcBodyPanel.IsVisible = kind == AnalysisEditorViewModel.AnalysisKind.DC;
        SpBodyPanel.IsVisible = kind == AnalysisEditorViewModel.AnalysisKind.SP;
        HbBodyPanel.IsVisible = kind == AnalysisEditorViewModel.AnalysisKind.HB;
        LpBodyPanel.IsVisible = kind == AnalysisEditorViewModel.AnalysisKind.LP;
        LppBodyPanel.IsVisible = kind == AnalysisEditorViewModel.AnalysisKind.LPP;
    }

    // ── Name ──────────────────────────────────────────────────────────────────

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressNameSync) return;
        Vm.Name = NameBox.Text ?? "";
        UpdateNameError(Vm.NameError);
        OkButton.IsEnabled = Vm.CanCommit;
    }

    private void UpdateNameError(string? error)
    {
        NameErrorLabel.Text      = error ?? "";
        NameErrorLabel.IsVisible = error is not null;
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        // Sync Enabled from checkbox before building (name is already synced on each keystroke)
        Vm.Enabled = EnabledCheck.IsChecked == true;
        var result = Vm.BuildAnalyses();
        if (result is null) return;
        Close(result);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    // ── Static factory ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and shows the dialog modally. Falls back to the first visible window when
    /// <paramref name="owner"/> is null (handles the macOS NativeMenu null-CommandParameter case).
    /// Returns the committed <see cref="Analysis"/> list, or null on Cancel.
    /// </summary>
    public static async Task<IReadOnlyList<Analysis>?> ShowAsync(
        Window? owner, AnalysisEditorViewModel vm, bool isEdit = false)
    {
        if (owner is null
            && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            owner = desktop.Windows
                           .OfType<Window>()
                           .FirstOrDefault(w => w.IsActive && w.IsVisible)
                ?? desktop.Windows
                           .OfType<Window>()
                           .FirstOrDefault(w => w.IsVisible);
        }

        if (owner is null) return null;

        var dialog = new AnalysisEditorDialog(vm, isEdit);
        return await dialog.ShowDialog<IReadOnlyList<Analysis>?>(owner);
    }
}
