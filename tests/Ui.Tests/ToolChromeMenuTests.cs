using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Floating tool window title bar: the chrome menu (Float / Dock / Dock as Tabbed Document) is
//  removed, because on a floating tool window those items do nothing and "Dock" is permanently
//  disabled (owner-reported 2026-07-30). A menu that cannot act is exactly what R13a forbids.
//
//  These are source scans: App.axaml styling has no headlessly assertable output (this codebase's
//  standing constraint for AXAML — see LayoutContextMenuStackingTests for the same fallback). The
//  scoping rule is what actually matters and is what these guard.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class ToolChromeMenuTests
{
    private static string AppAxaml()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "Ui", "App.axaml"));
    }

    [Fact]
    public void ChromeMenu_IsRemoved_ViaTheControlsOwnMenuButtonThemeHook()
    {
        var axaml = AppAxaml();
        Assert.Contains("dockCtrl|ToolChromeControl:floating", axaml);
        Assert.Contains("MenuButtonTheme", axaml);
    }

    /// <summary>
    /// The load-bearing constraint. <c>ToolChromeControl</c> also provides the chrome for DOCKED tool
    /// panels, where <c>Float</c> does real work — dropping the <c>:floating</c> qualifier would
    /// delete a working affordance in order to fix a broken one. If this test fails because someone
    /// widened the selector, that is the bug, not the test.
    /// </summary>
    [Fact]
    public void TheStyle_IsScopedToFloatingChromeOnly_NeverAllToolChrome()
    {
        var axaml = AppAxaml();

        // Match the SETTER, not a prose mention of the property in a comment.
        var idx = axaml.IndexOf("Property=\"MenuButtonTheme\"", StringComparison.Ordinal);
        Assert.True(idx > 0, "expected a MenuButtonTheme setter");

        var before = axaml[..idx];
        var selectorStart = before.LastIndexOf("<Style Selector=", StringComparison.Ordinal);
        Assert.True(selectorStart > 0, "MenuButtonTheme setter must live inside a Style");

        var selector = before[selectorStart..];
        Assert.Contains("ToolChromeControl:floating", selector);
    }
}
