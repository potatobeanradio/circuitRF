using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// <b>File ▸ New ▸ Examples</b> — pick a folder, get a copy of a shipped example workspace, and it opens.
///
/// <para>Kept in its own file for the same reason the revision and sharing partials are: none of it
/// is about editing a design, and the two things that decide anything —
/// <see cref="ExampleWorkspaces"/> (what there is) and <see cref="ExampleWorkspaceInstall"/> (the
/// copy) — both live outside this view model. What is left here is a folder picker, a refusal and a
/// report.</para>
///
/// <para><b>Where it opens depends on whether this window is EMPTY</b> — no workspace, no torn-off
/// document window, and no unsaved docked tab (<see cref="WindowIsEmptyForExample"/>). A clean
/// docked tab, such as the scratch schematic the launch action opens, does not stop it.</para>
///
/// <para><b>Not empty: a NEW WINDOW.</b> Somebody reaching for an example usually has a design open
/// and is looking something up; replacing their workspace to show them an example is the opposite of
/// helping, and it would drag the dirty-work prompt in with it. A torn-off document window counts
/// even with no workspace: a workspace opened here would make it foreign to a design it has nothing
/// to do with. So does unsaved work in a docked tab, which the switch would discard.</para>
///
/// <para><b>Empty: THIS WINDOW</b>, through <see cref="SwitchToWorkspaceReporting"/> — the funnel
/// Open Recent ends in, and so the copy lands in Open Recent like any opened workspace. Not through
/// <c>OpenRecentWorkspace</c> itself: its missing-path pruning, dirty prompt and EM-in-flight
/// confirmation are for REPLACING a workspace, and here nothing is replaced. Its concurrent-open
/// notice is skipped too, since the copy was written a moment ago and no other process can have it
/// open. A new window here would leave the empty one behind for the user to close.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// The examples this build ships, in index order — what the File ▸ New ▸ Examples submenu is built
    /// from. Empty is a supported state: the menu hides rather than showing rows that cannot work.
    /// </summary>
    public static IReadOnlyList<ExampleWorkspace> Examples { get; } = ExampleWorkspaces.All();

    /// <summary>Whether there is an Examples submenu to show at all.</summary>
    public static bool HasExamples => Examples.Count > 0;

    /// <summary>
    /// The in-window File ▸ New ▸ Examples rows. Built once on first use, because the shipped set cannot
    /// change while the application is running — unlike Open Recent, which is rebuilt on every push.
    ///
    /// <para>Each row's <c>Command</c> is ASSIGNED, not bound, which is what
    /// <c>RebuildRecentMenuItems</c> already does for the same reason: a <c>MenuItem</c> handed to a
    /// parent through <c>ItemsSource</c> is its own container, and a binding written in code would
    /// resolve against whatever DataContext it inherits rather than against this view model.</para>
    /// </summary>
    /// <remarks>The macOS menu bar is populated from <see cref="Examples"/> directly by
    /// <c>WorkspaceWindow.axaml.cs</c>: a <c>NativeMenuItem</c> is not a <c>Control</c>, so the two
    /// surfaces cannot share one collection — but they do share the one enumeration above.</remarks>
    public ObservableCollection<Control> ExampleMenuItems => _exampleMenuItems ??= BuildExampleMenuItems();

    private ObservableCollection<Control>? _exampleMenuItems;

    private ObservableCollection<Control> BuildExampleMenuItems()
    {
        var items = new ObservableCollection<Control>();
        foreach (var e in Examples)
        {
            var item = new MenuItem
            {
                Header           = e.Title,
                Command          = OpenExampleCommand,
                CommandParameter = e.Folder,
            };
            ToolTip.SetTip(item, e.Summary);
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// Installs the example whose <see cref="ExampleWorkspace.Folder"/> is
    /// <paramref name="folder"/> — the menu passes the folder name rather than the record, because a
    /// <c>NativeMenuItem</c>'s CommandParameter has to survive being written in XAML.
    /// </summary>
    [RelayCommand]
    private async Task OpenExample(string? folder)
    {
        ExampleWorkspace? example = null;
        foreach (var e in Examples)
            if (string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase)) { example = e; break; }

        if (example is null)
        {
            Messages.Error($"There is no example called '{folder}' in this build.");
            return;
        }

        if (await (ExampleParentDirPicker ?? PickExampleParentDirAsync)(example) is not { } parentDir)
            return;

        // Asked before anything is written. A copy has no rollback, so a refusal that arrives
        // halfway through leaves a workspace nobody asked for.
        if (ExampleWorkspaceInstall.Refusal(example, parentDir) is { } refusal)
        {
            // Linked when the refusal is a folder already in the way: the user's next step is to
            // go and look at it, and the sentence names it without being able to open it.
            string dest = ExampleWorkspaceInstall.DestinationFor(example, parentDir);
            Messages.Error(refusal, Directory.Exists(dest) || File.Exists(dest) ? dest : null);
            return;
        }

        ExampleInstallResult result;
        try
        {
            // Off the UI thread: this walks and copies a directory tree, and WorkspaceCopy is
            // framework-free by design so that it can be.
            result = await Task.Run(() => ExampleWorkspaceInstall.Run(example, parentDir));
        }
        catch (Exception ex)
        {
            Messages.Error($"The '{example.Title}' example could not be copied: {ex.Message}");
            return;
        }

        // Reported, not swallowed: the workspace opens either way, and a file that did not arrive
        // is something the user finds out about now rather than when a cell fails to resolve.
        foreach (string failure in result.Failures)
            Messages.Warning($"Example '{example.Title}': {failure}");

        _lastWorkspaceParentDir = parentDir;
        Messages.Info($"Copied the '{example.Title}' example to '{result.WorkspaceDir}' "
                    + $"({result.FileCount} file(s)). It is yours to edit.", result.WorkspaceDir);

        if (WindowIsEmptyForExample())
            await SwitchToWorkspaceReporting(result.CwsPath);
        else
            (OpenExampleInNewWindowHook ?? (cws => App.OpenWorkspaceInNewWindow(cws)))(result.CwsPath);
    }

    /// <summary>
    /// Whether the copy opens in THIS window: no workspace, no torn-off document window, and no
    /// unsaved work in a docked tab. See the class comment for why each keeps the example out.
    ///
    /// <para><b>A clean DOCKED document does not count.</b> The launch action's scratch schematic is
    /// one — nobody has put anything in it — and counting it sent every example from a freshly
    /// launched window to a second one. The switch replaces it the way it replaces the Welcome tab.
    /// A dirty one does count: <c>SwitchToWorkspace</c> discards docked tabs without asking (Open
    /// Recent asks first, and this path deliberately does not), so unsaved work would vanish.</para>
    ///
    /// <para><b>A torn-off document window always counts</b>, clean or not. The registries hold
    /// every one, including those Close Workspace left behind (<c>ResetToBlankShell</c>); the walk
    /// over this shell's floats catches a document kind nothing registers. It excludes
    /// <see cref="ITool"/> by name, because Dock's <c>Tool</c> also declares <see cref="IDocument"/>
    /// and a torn-off panel is not a document window.</para>
    /// </summary>
    internal bool WindowIsEmptyForExample()
    {
        if (CurrentWorkspacePath is not null) return false;

        IEnumerable<IDockable> tracked = _openDocsByPath.Values
            .Concat(_scratchDocs).Concat(_scratchSymbols).Concat(_scratchLayouts)
            .Concat(_scratchDataDisplays).Concat(_scratchHarmonicas).Concat(_scratchSmithCharts)
            .Concat(_scratchWBonds);
        if (tracked.Any(d => !IsDockableDocked(d))) return false;

        static bool IsDocument(IDockable d) => d is IDocument and not ITool and not StubDocument;

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.Windows.OfType<CrfHostWindow>()
                   .Where(w => w.PlatformImpl is not null && ReferenceEquals(w.OwningWorkspace, this))
                   .Any(w => DockablesIn(w.Window?.Layout).Any(IsDocument)))
            return false;

        return !HasAnyDirtyWork(includeFloated: false);
    }

    /// <summary>
    /// Asks where the copy goes; null when the user cancels. Null — the default — is the platform
    /// folder picker below. A seam because the picker needs a real window, and the rule the tests hold
    /// (<see cref="WindowIsEmptyForExample"/>) is decided after it.
    /// </summary>
    internal Func<ExampleWorkspace, Task<string?>>? ExampleParentDirPicker { get; set; }

    /// <summary>
    /// Opens the copy in a new workspace window. Null — the default — is
    /// <c>App.OpenWorkspaceInNewWindow</c>, which is static and so cannot be observed from a test.
    /// </summary>
    internal Action<string>? OpenExampleInNewWindowHook { get; set; }

    private async Task<string?> PickExampleParentDirAsync(ExampleWorkspace example)
    {
        var window = ResolveOwner(null);
        if (window is null) return null;

        // Where the picker opens: beside the workspace that is already open, which is where a
        // sibling project belongs — the same answer the Clone Workspace dialog's Browse gives.
        IStorageFolder? start = null;
        try { start = await window.StorageProvider.TryGetReachableFolderAsync(_lastWorkspaceParentDir); }
        catch { /* the picker opens wherever the platform likes */ }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = $"Put the '{example.Title}' example in…",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }
}
