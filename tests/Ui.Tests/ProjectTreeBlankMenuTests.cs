// ================================================================
//  ProjectTreeBlankMenuTests.cs — the Workspace panel's right-click-on-empty-space menu
//  (owner, 2026-09-23)
//
//  The menu's Import submenu is a hand-maintained copy of File ▸ Import, as the macOS NativeMenu
//  already is. This holds the copy to the original — header, command and tooltip, in order — so an
//  importer added to File ▸ Import and forgotten here fails a test instead of going missing.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Views.ProjectTree;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ProjectTreeBlankMenuTests
{
    [Fact]
    public void ImportSubmenu_IsFileImport_ItemForItem()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src/Ui/Views/WorkspaceWindow.axaml"));
        int start = xaml.IndexOf("<MenuItem Header=\"_Import\">", StringComparison.Ordinal);
        int end   = xaml.IndexOf("<MenuItem Header=\"_Export\">", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "File ▸ Import was not found in WorkspaceWindow.axaml.");

        var file = Regex.Matches(xaml[start..end],
                @"<MenuItem Header=""([^""]+)""\s+Command=""\{Binding (\w+)\}""[^/]*?(?:ToolTip\.Tip=""([^""]*)"")?\s*/>",
                RegexOptions.Singleline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value,
                          m.Groups[3].Success ? System.Net.WebUtility.HtmlDecode(m.Groups[3].Value) : null))
            .ToList();

        Assert.NotEmpty(file);
        Assert.Equal(file, ProjectTreeView.ImportItems.Select(i => (i.Header, i.Command, i.Tip)));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
