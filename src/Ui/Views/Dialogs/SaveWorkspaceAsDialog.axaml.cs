using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Result returned by <see cref="SaveWorkspaceAsDialog"/> on Save. The copy's folder is
/// <c>ParentDir/Name/</c> and must not already exist — the dialog gates Save on that, and the caller
/// re-checks at copy time.
/// </summary>
public sealed record SaveWorkspaceAsResult(string ParentDir, string Name);

/// <summary>
/// <c>File ▸ Save Workspace As…</c>'s "where to" step.
///
/// <para>A folder dialog rather than a <c>SaveFilePicker</c>, because a workspace IS a folder — its
/// manifest is a dotfile named <c>.cws</c> with no stem, so a file picker can only ever produce a
/// name (<c>untitled.cws</c>) that nothing in circuitRF looks for. Same shape as
/// <see cref="NewWorkspaceDialog"/> and deliberately so: the two commands both make a workspace
/// folder, and the one that copies should not ask a different-looking question from the one that
/// creates. No technology row — the copy has the source's technology already.</para>
/// </summary>
public partial class SaveWorkspaceAsDialog : Window
{
    private string? _sourceRoot;
    private string? _parentDir;
    private bool _settingSuggested;
    private bool _userOverrodeName;

    public SaveWorkspaceAsDialog() => InitializeComponent();

    /// <param name="sourceRoot">The workspace folder being copied.</param>
    /// <param name="defaultParentDir">Where to offer to put the copy — the source's own parent.</param>
    public SaveWorkspaceAsDialog(string sourceRoot, string defaultParentDir) : this()
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _parentDir  = defaultParentDir;

        SourceLabel.Text = $"Copies the whole workspace folder '{SourceName}' and everything in it "
                         + "to a new location.";

        UpdateSuggestedName();
        UpdateView();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private string SourceName
        => _sourceRoot is null
            ? ""
            : Path.GetFileName(_sourceRoot.TrimEnd(Path.DirectorySeparatorChar));

    private async void OnChooseClick(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? startLocation = null;
        if (_parentDir is not null)
            startLocation = await StorageProvider.TryGetFolderFromPathAsync(_parentDir);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to save the copy",
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
        if (_parentDir is null || name.Length == 0) return;
        if (Problem(name) is not null) return;

        Close(new SaveWorkspaceAsResult(_parentDir, name));
    }

    /// <summary>
    /// The single question behind the validation line, the Save button and the preview: what is
    /// wrong with copying to <c>_parentDir/name</c>? Name rules first (they are about the text the
    /// user is typing), then <see cref="WorkspaceCopy.Refusal"/> — which is the SAME function the
    /// command re-asks at commit time, so the dialog cannot let through a destination the copy would
    /// then reject.
    /// </summary>
    private string? Problem(string name)
    {
        if (_parentDir is null) return "Choose where to save the copy.";
        if (NameValidator.Validate(name) is { } nameError) return nameError;

        return WorkspaceCopy.Refusal(_sourceRoot, Path.Combine(_parentDir, name));
    }

    // The source's own name with "-copy" appended, then -copy-2, -copy-3… until one is free. Reads
    // as what it is in a file manager, which "Untitled-Workspace-1" would not.
    private void UpdateSuggestedName()
    {
        if (_parentDir is null || _sourceRoot is null) return;

        string suggested = NextFreeCopyName(_parentDir, SourceName);
        _settingSuggested = true;
        try { NameBox.Text = suggested; }
        finally { _settingSuggested = false; }
    }

    private static string NextFreeCopyName(string parentDir, string sourceName)
    {
        string first = $"{sourceName}-copy";
        if (!Exists(parentDir, first)) return first;

        for (int n = 2; n <= 9999; n++)
        {
            string candidate = $"{sourceName}-copy-{n}";
            if (!Exists(parentDir, candidate)) return candidate;
        }
        return first;

        static bool Exists(string parent, string name)
        {
            string p = Path.Combine(parent, name);
            return Directory.Exists(p) || File.Exists(p);
        }
    }

    private void UpdateView()
    {
        var name = NameBox.Text?.Trim() ?? "";
        LocationBox.Text = _parentDir ?? "";

        string? message = name.Length == 0 ? null : Problem(name);

        ValidationMessage.Text      = message;
        ValidationMessage.IsVisible = message is not null;

        OkButton.IsEnabled = _parentDir is not null && name.Length > 0 && message is null;

        if (OkButton.IsEnabled)
        {
            PreviewLabel.Text      = $"Will copy to: {Path.Combine(_parentDir!, name)}{Path.DirectorySeparatorChar}";
            PreviewLabel.IsVisible = true;
        }
        else
        {
            PreviewLabel.IsVisible = false;
        }
    }
}
