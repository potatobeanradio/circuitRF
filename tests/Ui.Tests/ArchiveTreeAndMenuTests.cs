using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CircuitRF.Ui.Archive;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The Archive Workspace dialog's tree behaviour, and the menu surfaces that reach it
/// (owner request, 2026-08-15).
///
/// <para><see cref="ArchiveTreeNode"/> carries no Avalonia, so the tri-state roll-up and the
/// build-on-expand are tested directly. The XAML surfaces are pinned by source scan, this
/// codebase's established fallback for a view that cannot be constructed headlessly.</para>
/// </summary>
public sealed class ArchiveTreeAndMenuTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadSource(string relative) => File.ReadAllText(Path.Combine(SourceRoot(), relative));

    private static ArchiveOption Option(string name, bool selected = false) => new()
    {
        Kind        = ArchiveOptionKind.Result,
        DisplayName = name,
        SourcePath  = "/tmp/" + name,
        ArchivePath = "results/" + name,
        Selected    = selected,
        SizeBytes   = 1024,
    };

    // ── Tree behaviour ────────────────────────────────────────────────────────

    [Fact]
    public void AGroupBuildsItsRowsOnFirstExpand_NotBefore()
    {
        int built = 0;
        var group = ArchiveTreeNode.Group("Results", () =>
        {
            built++;
            return new[] { ArchiveTreeNode.Leaf(Option("a.npy")), ArchiveTreeNode.Leaf(Option("b.npy")) };
        });

        // A collapsed heading still needs the expander arrow, so it holds one placeholder row.
        Assert.Equal(0, built);
        Assert.Single(group.Children);

        group.IsExpanded = true;
        Assert.Equal(1, built);
        Assert.Equal(2, group.Children.Count);

        group.IsExpanded = false;
        group.IsExpanded = true;
        Assert.Equal(1, built);          // built once, not on every open
    }

    [Fact]
    public void TickingARow_RollsUpToTheGroup_ThroughTheIndeterminateMiddle()
    {
        var a = Option("a.npy");
        var b = Option("b.npy");
        var group = ArchiveTreeNode.Group("Results", () => new[] { ArchiveTreeNode.Leaf(a), ArchiveTreeNode.Leaf(b) });
        group.IsExpanded = true;

        Assert.False(group.IsChecked);

        group.Children[0].IsChecked = true;
        Assert.Null(group.IsChecked);                 // some on, some off

        group.Children[1].IsChecked = true;
        Assert.True(group.IsChecked);
    }

    [Fact]
    public void TickingAGroup_TicksEveryRowUnderIt_AndTheOptionsBehindThem()
    {
        var a = Option("a.npy");
        var b = Option("b.npy");
        var group = ArchiveTreeNode.Group("Results", () => new[] { ArchiveTreeNode.Leaf(a), ArchiveTreeNode.Leaf(b) });
        group.IsExpanded = true;

        group.IsChecked = true;

        Assert.True(a.Selected);
        Assert.True(b.Selected);
        Assert.All(group.Children, c => Assert.True(c.IsChecked));
    }

    [Fact]
    public void TickingAnUnopenedGroup_StillDecidesForTheRowsItStandsFor()
    {
        // The bug this pins: a group ticked while collapsed looked ticked, built its rows later from
        // their own defaults, and archived the opposite of what the user chose.
        var a = Option("a.npy");
        var b = Option("b.npy");
        var group = ArchiveTreeNode.Group("Results", () => new[] { ArchiveTreeNode.Leaf(a), ArchiveTreeNode.Leaf(b) });

        group.IsChecked = true;
        Assert.True(a.Selected);
        Assert.True(b.Selected);

        group.IsExpanded = true;
        Assert.All(group.Children, c => Assert.True(c.IsChecked));
    }

    [Fact]
    public void NestedGroups_RollUpTwoLevels()
    {
        var cdd = Option("x.cdd");
        var npy = Option("x.npy");

        var outer = ArchiveTreeNode.Group("Results", () => new[]
        {
            ArchiveTreeNode.Group("Data Displays", () => new[] { ArchiveTreeNode.Leaf(cdd) }),
            ArchiveTreeNode.Group("Analysis",      () => new[] { ArchiveTreeNode.Leaf(npy) }),
        });
        outer.IsExpanded = true;
        foreach (var child in outer.Children) child.IsExpanded = true;

        outer.Children[0].Children[0].IsChecked = true;
        Assert.Null(outer.IsChecked);

        outer.Children[1].Children[0].IsChecked = true;
        Assert.True(outer.IsChecked);
        Assert.True(cdd.Selected);
        Assert.True(npy.Selected);
    }

    // ── The surfaces that reach it ────────────────────────────────────────────

    [Fact]
    public void TheProjectTreeHeaderOffersCloseAndArchive_WhenAWorkspaceIsOpen()
    {
        var xaml = ReadSource("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");

        Assert.Contains("Header=\"Close Workspace\" Command=\"{Binding CloseWorkspaceCommand}\"", xaml);
        Assert.Contains("Header=\"Archive Workspace…\" Command=\"{Binding ArchiveWorkspaceCommand}\"", xaml);
    }

    [Fact]
    public void TheProjectTreeHeaderOffersOpenAndUnarchive_WhenNoWorkspaceIsOpen()
    {
        // "No workspace open" is the only thing on the panel to right-click at that point, so the
        // menu must not be hidden wholesale on !HasWorkspace the way it used to be.
        var xaml = ReadSource("src/Ui/Views/ProjectTree/ProjectTreeView.axaml");

        Assert.DoesNotContain("<ContextMenu IsVisible=\"{Binding HasWorkspace}\">", xaml);
        Assert.Contains("Header=\"Open…\" Command=\"{Binding OpenWorkspaceCommand}\"", xaml);
        Assert.Contains("Header=\"Unarchive Workspace…\" Command=\"{Binding UnarchiveWorkspaceCommand}\"", xaml);
        Assert.Contains("IsVisible=\"{Binding !HasWorkspace}\"", xaml);
    }

    [Fact]
    public void TheTreesOpenItemReusesTheFileMenusOwnPickerAndCode()
    {
        // The owner's own words: "Reuse the same picker and code as the File->Open Workspace menu
        // code." One command, not a second copy of the folder picker and its .cws check.
        var vm = ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs");

        Assert.Contains("OpenWorkspaceFromTreeAsync() => OpenWorkspaceCommand.ExecuteAsync(null)", vm);
        Assert.Contains("ArchiveWorkspaceFromTreeAsync() => ArchiveWorkspaceCommand.ExecuteAsync(null)", vm);
        Assert.Contains("CloseWorkspaceFromTreeAsync() => CloseWorkspaceCommand.ExecuteAsync(null)", vm);
        Assert.Contains("UnarchiveWorkspaceFromTreeAsync() => UnarchiveWorkspaceCommand.ExecuteAsync(null)", vm);
    }

    [Fact]
    public void ArchivingSavesFirst_SoTheArchiveIsNotOfAnOlderDesign()
    {
        var vm = ReadSource("src/Ui/ViewModels/WorkspaceViewModel.cs");
        var body = vm[vm.IndexOf("private async Task ArchiveWorkspace(Window? owner)")..];
        body = body[..body.IndexOf("\n    /// <summary>", StringComparison.Ordinal)];

        // Unsaved work goes through the same prompt closing a workspace uses, and the .cws is always
        // refreshed — an archive is built from disk, so anything still in memory would be missing.
        Assert.Contains("PromptSaveBeforeClose(window, \"archiving the workspace\"", body);
        Assert.Contains("WriteWorkspaceFile(CurrentWorkspacePath", body);
        // The dialog comes before the destination picker: choosing what goes in is the decision,
        // and being asked where to put it first would be asking about the wrong thing.
        Assert.True(body.IndexOf("ArchiveWorkspaceDialog", StringComparison.Ordinal)
                    < body.IndexOf("SaveFilePickerAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void BothFileMenus_OfferArchiveAndUnarchive()
    {
        var xaml = ReadSource("src/Ui/Views/WorkspaceWindow.axaml");
        var root = XDocument.Parse(xaml);

        // Once in the macOS NativeMenu and once in the in-window Menu — the two are hand-mirrored,
        // and an item that lands in only one is invisible on half the platforms.
        Assert.Equal(2, root.Descendants().Count(e =>
            (string?)e.Attribute("Header") is { } h && h.Replace("_", "") == "Archive Workspace…"));
        Assert.Equal(2, root.Descendants().Count(e =>
            (string?)e.Attribute("Header") is { } h && h.Replace("_", "") == "Unarchive Workspace…"));
    }
}
