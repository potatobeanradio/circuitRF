using System.Linq;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A document docked into a torn-off document window shows as a tab there, and the window's title names
/// the documents it holds.
/// </summary>
public sealed class FloatingDocumentWindowTests
{
    /// <summary>
    /// Dock's <c>DockDockableIntoRoot</c> — what a drop on the window's root resolves to — puts the
    /// document in a NEW document dock beside the shown one, and a root draws only its active dockable.
    /// Driven through Dock's own <see cref="DockManager"/> so a Dock upgrade that changes the route is
    /// caught here.
    /// </summary>
    [Fact]
    public void ADocumentDroppedOnAFloatsRoot_JoinsItsTabs()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        f.InitLayout(root);
        var a = new Document { Id = "A", Title = "A.clay" };
        var b = new Document { Id = "B", Title = "B.csch" };
        f.AddDockable(f.DocumentDock!, a);
        f.AddDockable(f.DocumentDock!, b);

        // A tab tear-off, registered without a host so it stays headless (as EmptiedFloatingWindowTests).
        f.RemoveDockable(a, collapse: true);
        var window = f.CreateWindowFrom(a)!;
        root.Windows ??= f.CreateList<IDockWindow>();
        root.Windows.Add(window);
        window.Owner = root;
        window.Factory = f;
        f.InitDockable(window.Layout!, null);
        var floatRoot = window.Layout!;

        bool docked = new DockManager(new DockService())
            .ValidateDockable(b, floatRoot, DragAction.Move, DockOperation.Fill, bExecute: true);

        Assert.True(docked);
        var shown = Assert.Single(floatRoot.VisibleDockables!.OfType<IDocumentDock>());
        Assert.Same(shown, floatRoot.ActiveDockable);
        Assert.Equal(["A.clay", "B.csch"], shown.VisibleDockables!.Select(d => d.Title));
        Assert.Same(b, shown.ActiveDockable);
    }

    /// <summary>
    /// A save that names a document (a Smith chart's first save, any Save As) renames its tab in place with no dock event, and a
    /// torn-off window showing it kept the old name. A host window cannot be built headlessly (TearOffBackgroundTests), so this holds
    /// the wiring: the one handler every document's Title change reaches re-titles the floating windows as well as the main one.
    /// </summary>
    [Fact]
    public void ADocumentsTitleChange_ReTitlesTheTornOffWindows()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "WorkspaceViewModel.DirtyTitle.cs"));
        int at = src.IndexOf("private void OnWatchedDocumentPropertyChanged", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = src[at..src.IndexOf('}', at)];
        Assert.Contains("RefreshWindowTitle()", body);
        Assert.Contains("PostRefreshFloatingTitles()", body);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void TheTitleNamesEveryDocument_UntilItWouldGrowTooLong()
    {
        Assert.Equal("Amp.clay", WorkspaceViewModel.FloatTitleFor(["Amp.clay"]));
        Assert.Equal("Amp.clay, Bias.csch", WorkspaceViewModel.FloatTitleFor(["Amp.clay", "Bias.csch"]));

        var many = Enumerable.Range(1, 20).Select(i => $"Matching-Network-{i}.clay").ToList();
        string title = WorkspaceViewModel.FloatTitleFor(many);
        Assert.True(title.Length <= WorkspaceViewModel.MaxFloatTitleLength, title);
        Assert.StartsWith("Matching-Network-1.clay, ", title);
        int named = title.Split(", ").Length;
        Assert.EndsWith($" + {20 - named} more", title);
    }
}
