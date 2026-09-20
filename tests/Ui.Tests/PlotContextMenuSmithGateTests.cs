// ================================================================
//  PlotContextMenuSmithGateTests.cs — the Smith-only row, and the event that never fires
//
//  Owner-reported, 2026-09-20: "Show Admittance Grid" appeared on rectangular, polar,
//  table and 3D plots. The gate existed and read correctly — it just lived in a
//  `ContextMenu.Opening` handler, and Avalonia raises Opening from ONE place: the static
//  ControlContextRequested handler, which runs only for a menu the framework opens because
//  it is a control's ContextMenu PROPERTY. PlotControl opens its menu by hand
//  (`_contextMenu.Open(this)`), so the public Open(Control) overload runs straight to the
//  popup and the handler never ran once — leaving the row with the default IsVisible of
//  true on every plot type.
//
//  PlotControl is an Avalonia control this suite does not instantiate, so the wiring is
//  gated against the source with comments stripped.
// ================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests;

public class PlotContextMenuSmithGateTests
{
    /// <summary>The admittance row is gated on the plot type, and the gate is applied on the path
    /// that actually opens the menu — not from an Opening handler this menu never receives.</summary>
    [Fact]
    public void AdmittanceGridRow_IsGatedOnSmith_AndRefreshedOnTheOpenPath()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs")));

        // The gate itself, and the one method that owns it.
        var refresh = Regex.Match(
            code,
            @"private void RefreshContextMenuState\(\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        Assert.True(refresh.Success, "RefreshContextMenuState must exist and own the per-open state");
        Assert.Contains("_admittanceMenuItem.IsVisible = _plot?.PlotType == PlotType.Smith;",
                        refresh.Groups["body"].Value);

        // …applied before the menu is opened, every time — the menu instance is cached for the
        // control's lifetime, so a plot whose type changes afterwards must still be re-read.
        int open = code.IndexOf("_contextMenu.Open(this);", StringComparison.Ordinal);
        Assert.True(open >= 0, "the plot context menu is opened by hand");
        int built = code.LastIndexOf("_contextMenu ??= BuildContextMenu();", open, StringComparison.Ordinal);
        Assert.True(built >= 0);
        Assert.Contains("RefreshContextMenuState();", code.Substring(built, open - built));

        // …and NOT from Opening, which would silently do nothing on a hand-opened menu.
        Assert.DoesNotContain(".Opening", code);
    }

    private static string SourceFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}
