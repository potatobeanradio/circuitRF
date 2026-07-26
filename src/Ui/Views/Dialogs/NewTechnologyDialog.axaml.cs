using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>The starting point chosen in the New Technology dialog.</summary>
public enum NewTechnologyStarter { Pcb, Mmic, Empty }

/// <summary>
/// Result returned by <see cref="NewTechnologyDialog"/> on OK. Name is the validated technology
/// name (also used as the .ctech file stem — technology names, like cell names, are filesystem
/// path components, so <see cref="NameValidator"/> is sufficient without a separate slug step).
/// </summary>
public sealed record NewTechnologyResult(string Name, NewTechnologyStarter Starter, bool SetAsDefault);

/// <summary>
/// Custom "New Technology…" modal. Mirrors <see cref="NewWorkspaceDialog"/>'s return-or-null
/// ShowDialog contract and live-validation idiom, simplified: a technology always saves into the
/// current workspace's <c>tech/</c> folder, so there is no parent-location picker.
/// </summary>
public partial class NewTechnologyDialog : Window
{
    private readonly string? _techDir;

    public NewTechnologyDialog() => InitializeComponent();

    /// <param name="techDir">Absolute path of the workspace's tech/ folder (may not exist yet).</param>
    /// <param name="suggestedName">Initial name shown in the field, pre-selected.</param>
    public NewTechnologyDialog(string techDir, string suggestedName) : this()
    {
        _techDir = techDir;
        NameBox.Text = suggestedName;
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        UpdateView();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e) => UpdateView();

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            TryCommit();
            e.Handled = true;
        }
    }

    private void TryCommit()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (NameValidator.Validate(name) is not null) return;
        if (_techDir is not null && File.Exists(Path.Combine(_techDir, $"{name}.ctech"))) return;

        var starter = StarterMmicRadio.IsChecked == true ? NewTechnologyStarter.Mmic
            : StarterEmptyRadio.IsChecked == true          ? NewTechnologyStarter.Empty
            : NewTechnologyStarter.Pcb;

        Close(new NewTechnologyResult(name, starter, SetAsDefaultCheck.IsChecked == true));
    }

    private void UpdateView()
    {
        var name      = NameBox.Text?.Trim() ?? "";
        var nameError = NameValidator.Validate(name);

        string? message = nameError;
        if (message is null && _techDir is not null && name.Length > 0
            && File.Exists(Path.Combine(_techDir, $"{name}.ctech")))
        {
            message = $"A technology named '{name}' already exists.";
        }

        ValidationMessage.Text      = message;
        ValidationMessage.IsVisible = message is not null;

        OkButton.IsEnabled = name.Length > 0 && message is null;

        if (name.Length > 0 && message is null)
        {
            PreviewLabel.Text      = $"Will create: tech/{name}.ctech";
            PreviewLabel.IsVisible = true;
        }
        else
        {
            PreviewLabel.IsVisible = false;
        }
    }
}
