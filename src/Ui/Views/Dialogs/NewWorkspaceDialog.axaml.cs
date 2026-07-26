using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>The starter technology chosen in the New Workspace dialog.</summary>
public enum NewWorkspaceTechChoice { Pcb, Mmic, None }

/// <summary>
/// Result returned by NewWorkspaceDialog on OK.  ParentDir is the chosen parent folder;
/// Name is the validated workspace name.  The workspace folder = ParentDir/Name/ and must
/// not already exist — the dialog gates OK on this and the caller re-checks at create time.
/// Technology is the starter technology chosen (or None) — see docs/design/layout-view.md §2.4.
/// </summary>
public sealed record NewWorkspaceResult(string ParentDir, string Name, NewWorkspaceTechChoice Technology);

/// <summary>
/// Custom "New Workspace" modal.  Returns NewWorkspaceResult via ShowDialog, or null on cancel.
/// Mirrors InputNameDialog's return-or-null ShowDialog contract.
/// The system folder picker is used only behind "Choose…" to pick the PARENT location;
/// the workspace folder itself is created by the caller (never by picking an existing folder).
/// </summary>
public partial class NewWorkspaceDialog : Window
{
    private string? _parentDir;
    private string? _suggestedName;
    private bool _settingSuggested;
    private bool _userOverrodeName;

    public NewWorkspaceDialog() => InitializeComponent();

    public NewWorkspaceDialog(string defaultParentDir) : this()
    {
        _parentDir = defaultParentDir;
        UpdateSuggestedName();
        UpdateView();
        NameBox.Focus();
    }

    private async void OnChooseClick(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? startLocation = null;
        if (_parentDir is not null)
            startLocation = await StorageProvider.TryGetFolderFromPathAsync(_parentDir);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to create the workspace",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        if (folders.Count == 0) return;
        _parentDir = folders[0].Path.LocalPath;
        if (!_userOverrodeName)
            UpdateSuggestedName();
        UpdateView();
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_settingSuggested)
            _userOverrodeName = true;
        UpdateView();
    }

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
        if (_parentDir is null) return;
        if (File.Exists(Path.Combine(_parentDir, ".cws"))) return;
        if (Directory.Exists(Path.Combine(_parentDir, name))) return;

        var tech = TechMmicRadio.IsChecked == true ? NewWorkspaceTechChoice.Mmic
            : TechNoneRadio.IsChecked == true       ? NewWorkspaceTechChoice.None
            : NewWorkspaceTechChoice.Pcb;

        Close(new NewWorkspaceResult(_parentDir, name, tech));
    }

    // Sets NameBox.Text to the next free Untitled-Workspace-N for _parentDir,
    // suppressing the OnNameChanged user-override flag during the programmatic change.
    private void UpdateSuggestedName()
    {
        if (_parentDir is null) return;
        _suggestedName = NextFreeUntitledWorkspaceName(_parentDir);
        _settingSuggested = true;
        try { NameBox.Text = _suggestedName; }
        finally { _settingSuggested = false; }
    }

    // Returns the lowest Untitled-Workspace-N (N ≥ 1) such that parentDir/name does not exist.
    private static string NextFreeUntitledWorkspaceName(string parentDir)
    {
        for (int n = 1; n <= 9999; n++)
        {
            var candidate = $"Untitled-Workspace-{n}";
            if (!Directory.Exists(Path.Combine(parentDir, candidate)))
                return candidate;
        }
        return "Untitled-Workspace";
    }

    private void UpdateView()
    {
        var name      = NameBox.Text?.Trim() ?? "";
        var nameError = NameValidator.Validate(name);
        var isNested  = _parentDir is not null && File.Exists(Path.Combine(_parentDir, ".cws"));

        LocationBox.Text = _parentDir ?? "";

        string? message = null;
        if (isNested)
        {
            message = "A workspace cannot be created within another workspace. Select new Location.";
        }
        else if (nameError is not null)
        {
            message = nameError;
        }
        else if (_parentDir is not null && name.Length > 0)
        {
            if (Directory.Exists(Path.Combine(_parentDir, name)))
                message = $"A workspace folder named '{name}' already exists here.";
        }

        ValidationMessage.Text      = message;
        ValidationMessage.IsVisible = message is not null;

        OkButton.IsEnabled = _parentDir is not null
                          && name.Length > 0
                          && message is null;

        if (_parentDir is not null && name.Length > 0 && nameError is null && !isNested)
        {
            PreviewLabel.Text      = $"Will create: {Path.Combine(_parentDir, name)}{Path.DirectorySeparatorChar}";
            PreviewLabel.IsVisible = true;
        }
        else
        {
            PreviewLabel.IsVisible = false;
        }
    }
}
