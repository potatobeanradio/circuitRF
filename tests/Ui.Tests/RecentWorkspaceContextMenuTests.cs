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

    /// <summary>
    /// <b>Remove from Recent</b> (owner, 2026-08-26) sits below the reveal item, behind a separator —
    /// it is the only item on this menu that changes anything, and the rule holds the grouping.
    /// </summary>
    [Fact]
    public void TheRecentListMenu_OffersRemoveFromRecentBelowASeparator()
    {
        var xaml = Read("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int list = xaml.IndexOf("ItemsSource=\"{Binding RecentWorkspaces}\"", StringComparison.Ordinal);
        Assert.True(list >= 0);
        int end = xaml.IndexOf("</ItemsControl>", list, StringComparison.Ordinal);
        var template = xaml[list..end];

        int reveal = template.IndexOf("Header=\"{Binding RevealLabel}\"", StringComparison.Ordinal);
        int sep    = template.IndexOf("<Separator/>", StringComparison.Ordinal);
        int remove = template.IndexOf("Header=\"Remove from Recent\"", StringComparison.Ordinal);

        Assert.True(remove >= 0, "the recent-list menu should offer Remove from Recent");
        Assert.True(sep >= 0, "a separator goes above Remove from Recent");
        Assert.True(reveal < sep && sep < remove,
                    "order is Reveal, then the separator, then Remove from Recent");
    }

    /// <summary>
    /// Same popup-tree constraint as the other two items: the ENTRY carries the command, and it is
    /// actually assigned when the list is built — otherwise the item is greyed out and does nothing.
    /// </summary>
    [Fact]
    public void TheRemoveItem_BindsTheEntrysOwnCommand_AndTheEntryCarriesIt()
    {
        var xaml = Read("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int at = xaml.IndexOf("Header=\"Remove from Recent\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var item = xaml[at..xaml.IndexOf("/>", at, StringComparison.Ordinal)];

        Assert.Contains("Command=\"{Binding RemoveCommand}\"", item, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding Path}\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("$parent", item, StringComparison.Ordinal);

        var code = Read("src", "Ui", "ViewModels", "Dock", "ProjectTreeTool.cs");
        int start = code.IndexOf("private void RefreshRecent()", StringComparison.Ordinal);
        int stop  = code.IndexOf("private void OpenRecent(", StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start);
        Assert.Contains("RemoveCommand = RemoveRecentCommand,", code[start..stop], StringComparison.Ordinal);
    }

    /// <summary>
    /// The workspace is untouched. The removal path forgets a PATH — it must not reach for any of the
    /// delete/trash verbs, and it has to persist, or the entry returns on the next launch.
    /// </summary>
    [Fact]
    public void RemovingARecentEntry_ForgetsThePathAndDeletesNothing()
    {
        var tool = Read("src", "Ui", "ViewModels", "Dock", "ProjectTreeTool.cs");
        int at   = tool.IndexOf("private void RemoveRecent(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = tool[at..tool.IndexOf("private void ClearRecent(", at, StringComparison.Ordinal)];
        Assert.Contains("_actions?.RemoveRecentWorkspace(cwsPath);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Delete", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Trash", body, StringComparison.Ordinal);

        var ws  = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
        int imp = ws.IndexOf("void ITreeActions.RemoveRecentWorkspace(", StringComparison.Ordinal);
        Assert.True(imp >= 0);
        var body2 = ws[imp..ws.IndexOf("// \u2500\u2500 ITreeActions: workspace-level items", imp, StringComparison.Ordinal)];

        // Persisted, or the entry is back on the next launch.
        Assert.Contains("SaveRecent();", body2, StringComparison.Ordinal);
        // The same case-insensitive comparison PushRecent de-duplicates with.
        Assert.Contains("StringComparison.OrdinalIgnoreCase", body2, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete", body2, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", body2, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Open Workspace in New Window…</b> (owner, 2026-09-03) sits directly below Open Workspace —
    /// the same verb, differing only in where the workspace lands, so the two read as a pair.
    /// </summary>
    [Fact]
    public void TheRecentListMenu_OffersOpenInNewWindowBelowOpenWorkspace()
    {
        var xaml = Read("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int list = xaml.IndexOf("ItemsSource=\"{Binding RecentWorkspaces}\"", StringComparison.Ordinal);
        Assert.True(list >= 0);
        var template = xaml[list..xaml.IndexOf("</ItemsControl>", list, StringComparison.Ordinal)];

        int open      = template.IndexOf("Header=\"Open Workspace\"", StringComparison.Ordinal);
        int newWindow = template.IndexOf("Header=\"Open Workspace in New Window\u2026\"", StringComparison.Ordinal);
        int reveal    = template.IndexOf("Header=\"{Binding RevealLabel}\"", StringComparison.Ordinal);

        Assert.True(newWindow >= 0, "the recent-list menu should offer Open Workspace in New Window");
        Assert.True(open < newWindow && newWindow < reveal,
                    "order is Open Workspace, then Open Workspace in New Window, then Reveal");
    }

    /// <summary>
    /// Same popup-tree constraint as every other item on this menu: the ENTRY carries the command and
    /// is actually given it when the list is built, or the item is greyed out and does nothing.
    /// </summary>
    [Fact]
    public void TheOpenInNewWindowItem_BindsTheEntrysOwnCommand_AndTheEntryCarriesIt()
    {
        var xaml = Read("src", "Ui", "Views", "ProjectTree", "ProjectTreeView.axaml");

        int at = xaml.IndexOf("Header=\"Open Workspace in New Window\u2026\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var item = xaml[at..xaml.IndexOf("/>", at, StringComparison.Ordinal)];

        Assert.Contains("Command=\"{Binding OpenInNewWindowCommand}\"", item, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"{Binding Path}\"", item, StringComparison.Ordinal);
        Assert.DoesNotContain("$parent", item, StringComparison.Ordinal);

        var code = Read("src", "Ui", "ViewModels", "Dock", "ProjectTreeTool.cs");
        int start = code.IndexOf("private void RefreshRecent()", StringComparison.Ordinal);
        int stop  = code.IndexOf("private void OpenRecent(", StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start);
        Assert.Contains("OpenInNewWindowCommand = OpenRecentInNewWindowCommand,",
                        code[start..stop], StringComparison.Ordinal);

        Assert.Contains("private void OpenRecentInNewWindow(string path) => _actions?.OpenWorkspacePathInNewWindow(path);",
                        code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Opening in a new window leaves THIS window alone: no dirty-work prompt, nothing closed, and no
    /// switch — the whole point of the item. It goes through the same App entry point File ▸ Open
    /// Workspace in New Window… uses, so a workspace already open somewhere is activated rather than
    /// opened twice (R-mw1-9).
    /// </summary>
    [Fact]
    public void OpeningInANewWindow_SwitchesNothingInThisOne()
    {
        var ws = Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");
        int at = ws.IndexOf("public void OpenWorkspacePathInNewWindow(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = ws[at..ws.IndexOf("The awaitable form of", at, StringComparison.Ordinal)];

        Assert.Contains("App.OpenWorkspaceInNewWindow(cwsPath);", body, StringComparison.Ordinal);
        Assert.Contains("File.Exists(cwsPath)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SwitchToWorkspace", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PromptSaveBeforeClose", body, StringComparison.Ordinal);
    }
}
