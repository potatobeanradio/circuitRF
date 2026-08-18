using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>Open Workspace on a recent-list row's context menu</b> (owner, 2026-08-18), above the reveal item.
///
/// <para>The one thing here that is not obvious, and that a future edit could quietly undo: a
/// <c>ContextMenu</c> is its own popup visual tree, so the <c>$parent[ItemsControl]</c> walk the row's
/// Button uses to reach <c>ProjectTreeTool.OpenRecentCommand</c> resolves to nothing from inside a menu
/// item. The ENTRY has to carry the command — which is exactly why the reveal item already worked that
/// way, and why binding the new item the way the Button is bound would produce a menu item that silently
/// does nothing.</para>
/// </summary>
public class RecentWorkspaceContextMenuTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>The menu carries Open Workspace, and it is ABOVE the reveal item.</summary>
    [Fact]
    public void TheRecentListMenu_OffersOpenWorkspaceAboveReveal()
    {
        var xaml = Read("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int list = xaml.IndexOf("ItemsSource=\"{Binding RecentWorkspaces}\"", StringComparison.Ordinal);
        Assert.True(list >= 0);
        int end = xaml.IndexOf("</ItemsControl>", list, StringComparison.Ordinal);
        var template = xaml[list..end];

        int open   = template.IndexOf("Header=\"Open Workspace\"", StringComparison.Ordinal);
        int reveal = template.IndexOf("Header=\"{Binding RevealLabel}\"", StringComparison.Ordinal);

        Assert.True(open >= 0, "the recent-list menu should offer Open Workspace");
        Assert.True(reveal >= 0);
        Assert.True(open < reveal, "Open Workspace goes above the reveal item");
    }

    /// <summary>
    /// It binds the ENTRY's own command and passes the entry's path — not a <c>$parent</c> walk, which
    /// does not resolve out of a popup tree and would leave the item dead.
    /// </summary>
    [Fact]
    public void TheOpenItem_BindsTheEntrysOwnCommand()
    {
        var xaml = Read("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int open = xaml.IndexOf("Header=\"Open Workspace\"", StringComparison.Ordinal);
        Assert.True(open >= 0);
        var item = xaml[open..xaml.IndexOf("/>", open, StringComparison.Ordinal)];

        Assert.Contains("Command=\"{Binding OpenCommand}\"", item, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding Path}\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("$parent", item, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and the entry is actually given that command when the list is built. Without this the binding
    /// above resolves to null and the item is greyed out, which is the failure mode a XAML-only check
    /// cannot see.
    /// </summary>
    [Fact]
    public void EveryRecentEntry_CarriesTheOpenCommand()
    {
        var code = Read("src", "Ui", "ViewModels", "Dock", "ProjectTreeTool.cs");

        int at  = code.IndexOf("private void RefreshRecent()", StringComparison.Ordinal);
        int end = code.IndexOf("private void OpenRecent(", StringComparison.Ordinal);
        Assert.True(at >= 0 && end > at);

        Assert.Contains("OpenCommand   = OpenRecentCommand,", code[at..end], StringComparison.Ordinal);

        // The same command the row's own click runs — one way to open a workspace from this list, not two.
        Assert.Contains("private void OpenRecent(string path) => _actions?.OpenWorkspacePath(path);",
                        code, StringComparison.Ordinal);
    }
}
