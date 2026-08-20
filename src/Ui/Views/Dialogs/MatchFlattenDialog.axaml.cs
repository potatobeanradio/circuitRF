using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What the user chose in <see cref="MatchFlattenDialog"/>.</summary>
/// <param name="CellName">The new cell's name, already validated by <c>NameValidator</c>.</param>
/// <param name="ParentDir">Where to create it.</param>
/// <param name="ReplaceInPlace">Whether the <c>Match</c> is replaced by an instance of it.</param>
public sealed record MatchFlattenChoice(string CellName, string ParentDir, bool ReplaceInPlace);

/// <summary>
/// The name-and-destination prompt for <b>Flatten to Cell</b> (match.md §11.2). Built on
/// <see cref="InputNameDialog"/>'s shape rather than inventing a second one — same field, same
/// <c>NameValidator</c> check on OK, same Enter/Escape handling — with the two things flatten needs
/// on top: where the cell goes, and the <i>Replace …</i> checkbox, <b>on by default</b>.
/// </summary>
public partial class MatchFlattenDialog : Window
{
    private string _parentDir = "";
    private string _workspaceRoot = "";

    /// <summary>The AXAML designer needs a parameterless constructor.</summary>
    public MatchFlattenDialog() => InitializeComponent();

    /// <param name="instanceName">The <c>Match</c> being flattened — named in the prompt and the checkbox.</param>
    /// <param name="defaultName">The seeded cell name, already free of collisions.</param>
    /// <param name="parentDir">The workspace root, and the default destination.</param>
    public MatchFlattenDialog(string instanceName, string defaultName, string parentDir) : this()
    {
        _parentDir = parentDir;
        _workspaceRoot = parentDir;

        PromptLabel.Text =
            $"Write {instanceName}'s matching network as ordinary L and C components in a new cell. "
            + "Both terminations travel with it, disabled, and the design is recorded in the cell's "
            + "annotation and on the cell itself.";
        NameBox.Text = defaultName;
        DestinationBox.Text = parentDir;
        ReplaceBox.Content = $"Replace {instanceName} with an instance of the new cell";

        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) => TryCommit();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { TryCommit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e) => await OnBrowseAsync();

    private async Task OnBrowseAsync()
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(_parentDir);
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Where to Create the Cell",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picked.Count == 0) return;

        string chosen = picked[0].Path.LocalPath;

        // A cell is found by the workspace scanner walking the workspace root, and a cell reference
        // is a path relative to the schematic that uses it. A folder outside the workspace would
        // give a reference that resolves today and breaks the moment the workspace is moved or
        // archived — so it is refused here rather than allowed and reported later.
        if (!IsInsideWorkspace(chosen))
        {
            ValidationMessage.Text =
                "That folder is outside this workspace. A flattened cell has to live inside the "
                + "workspace, or nothing will find it after the workspace is moved or archived.";
            ValidationMessage.IsVisible = true;
            return;
        }

        ValidationMessage.IsVisible = false;
        _parentDir = chosen;
        DestinationBox.Text = chosen;
    }

    private bool IsInsideWorkspace(string dir)
    {
        try
        {
            string root = Path.GetFullPath(_workspaceRoot);
            string full = Path.GetFullPath(dir);
            return full.Equals(root, System.StringComparison.Ordinal)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, System.StringComparison.Ordinal);
        }
        catch (System.Exception ex) when (ex is IOException or System.ArgumentException)
        {
            return false;
        }
    }

    private void TryCommit()
    {
        string name = NameBox.Text?.Trim() ?? "";

        string? reason = NameValidator.Validate(name);
        if (reason is null && Directory.Exists(Path.Combine(_parentDir, name)))
            reason = $"A cell named '{name}' already exists here. Choose another name — flattening "
                     + "never writes over a cell that is already in the workspace.";

        if (reason is not null)
        {
            ValidationMessage.Text = reason;
            ValidationMessage.IsVisible = true;
            return;
        }

        Close(new MatchFlattenChoice(name, _parentDir, ReplaceBox.IsChecked == true));
    }
}
