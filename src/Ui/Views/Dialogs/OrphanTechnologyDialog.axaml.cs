using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The user's choice from <see cref="OrphanTechnologyDialog"/> (brief-foreign-documents.md R-fgn-4).
/// <see cref="Path"/> non-null means a specific <c>.ctech</c> file (browsed, or picked from the current
/// workspace) — resolved live through the same <see cref="TechnologyCache"/> every
/// other resolution uses. <see cref="StarterTech"/> non-null (with <see cref="Path"/> null) means a
/// built-in starter — frozen, since there is no file to track. Never both non-null.
/// </summary>
public readonly record struct OrphanTechnologyChoice(string? Path, Technology? StarterTech);

/// <summary>
/// R-fgn-4: shown once per loose <c>.clay</c> with no ancestor workspace at all — offers three routes
/// (browse for a <c>.ctech</c>; pick one from the CURRENT workspace; or a built-in
/// starter). Session-scoped only — the caller (<c>WorkspaceViewModel</c>) never writes the choice to the
/// file; §2.1's own guardrail points users at <c>Change Technology…</c> for a permanent choice instead
/// of duplicating that mechanism here.
/// </summary>
public partial class OrphanTechnologyDialog : Window
{
    public OrphanTechnologyDialog() => InitializeComponent();

    public OrphanTechnologyDialog(string? currentWorkspacePath, string fileName) : this()
    {
        MessageText.Text =
            $"'{fileName}' isn't part of any workspace, so it has no technology to draw layers with. " +
            "Choose one for this session:";

        var paths = WorkspaceTechnologies(currentWorkspacePath);
        if (paths.Count > 0)
        {
            var items = paths
                .Select(p => new TechFileItem(TryReadTechName(p) ?? Path.GetFileNameWithoutExtension(p), p))
                .ToList();
            CurrentWorkspaceList.ItemsSource = items;
            CurrentWorkspaceList.SelectedIndex = 0;
            CurrentWorkspaceRadio.IsChecked = true;
            StarterRadio.IsChecked = false;
        }
        else
        {
            CurrentWorkspaceRadio.IsEnabled = false;
            CurrentWorkspaceList.IsEnabled = false;
        }

        OkButton.Click     += (_, _) => Close(BuildResult());
        CancelButton.Click += (_, _) => Close(null);
    }

    /// <summary>
    /// Every <c>.ctech</c> in the current workspace, <b>the workspace default first</b> — the row the
    /// dialog preselects.
    /// </summary>
    /// <remarks>
    /// It used to list the <c>tech/</c> folder only. A technology minted by a Gerber import lives
    /// beside the import, not in <c>tech/</c> — so a designer who had just made that one the
    /// workspace default was offered only the OTHER technology, picked it, and the session choice
    /// then held it until a restart. The enumeration is the one the MLIN technology picker uses.
    /// </remarks>
    internal static IReadOnlyList<string> WorkspaceTechnologies(string? currentWorkspacePath)
    {
        if (currentWorkspacePath is null || Path.GetDirectoryName(currentWorkspacePath) is not { } root
            || !Directory.Exists(root))
            return [];

        string? defaultPath = null;
        try
        {
            if (WorkspacePersistence.LoadFromFile(currentWorkspacePath).DefaultTechRef is { Length: > 0 } r)
                defaultPath = Path.GetFullPath(Path.Combine(root, r));
        }
        catch { /* unreadable .cws — no default to put first */ }

        return [.. DocumentRemovalImpact.TechnologiesIn(root)
            .OrderBy(p => defaultPath is not null
                          && string.Equals(Path.GetFullPath(p), defaultPath, System.StringComparison.OrdinalIgnoreCase)
                          ? 0 : 1)];
    }

    private sealed record TechFileItem(string Label, string AbsolutePath)
    {
        public override string ToString() => Label;
    }

    private static string? TryReadTechName(string path)
    {
        try
        {
            var tech = TechPersistence.LoadFromFile(path);
            return tech.Name is { Length: > 0 } n ? n : null;
        }
        catch
        {
            return null; // corrupt/unreadable — fall back to the filename stem, never throw here
        }
    }

    private OrphanTechnologyChoice? BuildResult()
    {
        if (BrowseRadio.IsChecked == true)
        {
            // Handled by a dedicated async path (file picker) — see ShowAsync.
            return _browsedPath is { } p ? new OrphanTechnologyChoice(p, null) : null;
        }

        if (CurrentWorkspaceRadio.IsChecked == true &&
            CurrentWorkspaceList.SelectedItem is TechFileItem item)
        {
            return new OrphanTechnologyChoice(item.AbsolutePath, null);
        }

        // docs/sonnet-briefs/brief-misc-termg-units-technologies.md §3: PCB/MMIC now come from the
        // SAME shipped-technology set the New Workspace dialog offers (R-misc-6's own "one
        // authored representation, not two") — never StarterTechnologies' own, possibly-diverged
        // in-memory content. "Empty" (a genuinely blank technology, not a curated default) is
        // orthogonal to that concern and is left as-is.
        Technology starter = StarterMmicRadio.IsChecked == true ? ShippedTechnologies.Load("mmic-GaAs_2LM_100um")
            : StarterEmptyRadio.IsChecked == true                ? StarterTechnologies.Empty()
            : StarterPcb4Radio.IsChecked == true                 ? ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz")
            : ShippedTechnologies.Load(ShippedTechnologies.DefaultId);
        return new OrphanTechnologyChoice(null, starter);
    }

    private string? _browsedPath;

    /// <summary>
    /// Shows the dialog. Browsing for a file needs an async file picker before OK can be pressed
    /// meaningfully, so the browse row is handled here rather than in the constructor's synchronous
    /// Click handler: clicking "Browse…" immediately opens the picker, and a chosen file both fills in
    /// <see cref="_browsedPath"/> and closes the dialog directly.
    /// </summary>
    public static async Task<OrphanTechnologyChoice?> ShowAsync(Window owner, string? currentWorkspacePath, string fileName)
    {
        var dlg = new OrphanTechnologyDialog(currentWorkspacePath, fileName);
        dlg.BrowseRadio.Click += async (_, _) =>
        {
            var result = await dlg.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a Technology",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("circuitRF Technology") { Patterns = ["*.ctech"] },
                    new FilePickerFileType("All Files")             { Patterns = ["*.*"] },
                ],
            });
            if (result.Count == 0) { dlg.BrowseRadio.IsChecked = false; return; }

            dlg._browsedPath = result[0].Path.LocalPath;
            dlg.Close(new OrphanTechnologyChoice(dlg._browsedPath, null));
        };

        return await dlg.ShowDialog<OrphanTechnologyChoice?>(owner);
    }
}
