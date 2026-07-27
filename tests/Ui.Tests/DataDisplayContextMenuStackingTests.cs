using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-datadisplay-fix-context-menu-stacking.md — same class of defect as the
// layout-canvas context-menu-stacking fix, in src/Ui/DataDisplay/: three of PlotControl's four
// right-click menus (marker, trace header, table) constructed a BRAND-NEW ContextMenu on every click
// and opened it manually, never tracking or closing the previous one — each right-click stacked
// another popup. The main plot menu (Pattern A — cache the built instance) and
// MarkerInfoBoxView's marker-box menu (Pattern B — one instance, rebuilt on Opening) were ALREADY
// correct; this fix converges the three broken sites onto the same two patterns, already proven
// correct elsewhere in these same two files, rather than inventing anything new.
//
// PlotControl is a Control subclass and this project's test suite must not call any Avalonia runtime
// API at all (tests/Ui.Tests/CircuitRF.Ui.Tests.csproj's own header comment), so the menu-building/
// event-wiring behavior cannot be driven directly. Per the brief's own fallback (§5 gate 2): assert
// the STRUCTURAL invariant that makes stacking impossible by construction — every remaining
// `new ContextMenu()` in PlotControl.cs is either the one legitimate Pattern-A builder (itself only
// ever called from behind a `??=` guard) or is itself behind a `??=` guard.

public class DataDisplayContextMenuStackingTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string PlotControlSource() =>
        ReadRepoFile(Path.Combine("src", "Ui", "DataDisplay", "Controls", "PlotControl.cs"));

    private static string MarkerInfoBoxViewSource() =>
        ReadRepoFile(Path.Combine("src", "Ui", "Views", "DataDisplay", "MarkerInfoBoxView.axaml.cs"));

    [Fact]
    public void PlotControl_OnlyOneUnguardedContextMenuConstruction_TheStaticPatternABuilder()
    {
        string src = PlotControlSource();
        var newContextMenuLines = src.Split('\n')
            .Where(l => l.Contains("new ContextMenu()") && !l.TrimStart().StartsWith("//"))
            .ToList();

        // Every DYNAMIC-content site (marker/trace-header/table) must construct behind `??=` so it
        // runs at most once per PlotControl instance, never per click.
        var unguarded = newContextMenuLines.Where(l => !l.Contains("??=")).ToList();
        Assert.Single(unguarded); // exactly one — BuildContextMenu()'s own internal `var menu = new ContextMenu();`
        Assert.Contains("var menu = new ContextMenu();", unguarded[0]);

        // _markerContextMenu, _traceHeaderContextMenu, _tableContextMenu each guard their own
        // `new ContextMenu()` directly; _contextMenu (the pre-existing correct one) guards a call to
        // BuildContextMenu() instead, so it doesn't contain the literal text and isn't counted here.
        var guarded = newContextMenuLines.Where(l => l.Contains("??=")).ToList();
        Assert.True(guarded.Count >= 3, $"expected at least 3 null-coalescing-guarded ContextMenu fields, found {guarded.Count}");
        Assert.Contains(src.Split('\n'), l => l.Contains("_contextMenu ??= BuildContextMenu()"));
    }

    [Fact]
    public void PlotControl_TheDynamicMenus_ClearItemsBeforeRepopulating()
    {
        // The three previously-broken sites must clear the reused menu's Items before rebuilding —
        // otherwise a stale item from a PRIOR click (a different marker/trace/cell) would linger.
        string src = PlotControlSource();
        Assert.Contains("menu.Items.Clear();", src); // ShowTraceHeaderContextMenu / PopulateTableContextMenu
        // MarkerInfoBoxView.PopulateMarkerMenu (shared by both surfaces) does its own Items.Clear() —
        // verified separately below; ShowMarkerContextMenu calls that shared helper directly rather
        // than clearing locally, which is correct (one clear, not two).
        Assert.Contains("MarkerInfoBoxView.PopulateMarkerMenu(", src);
    }

    [Fact]
    public void PlotControl_TheDynamicMenus_CloseBeforeOpen_SoASecondRightClickReplaces()
    {
        string src = PlotControlSource();
        int closeCount = src.Split('\n').Count(l => l.Contains(".Close();") && l.Contains("re-opens"));
        Assert.True(closeCount >= 3, $"expected Close()-before-Open() on all three fixed menus, found {closeCount}");
    }

    [Fact]
    public void MarkerInfoBoxView_StillUsesTheSingleInstancePlusOpeningPattern_Untouched()
    {
        string src = MarkerInfoBoxViewSource();

        Assert.Contains("menu.Opening += (_, _) => RebuildContextMenu(menu);", src);
        Assert.Contains("ContextMenu = menu;", src);

        int count = src.Split('\n').Count(l => l.Contains("new ContextMenu()"));
        Assert.Equal(1, count); // constructed once, in OnDataContextChanged — never per click
    }

    [Fact]
    public void PopulateMarkerMenu_StillClearsItemsFirst_TheSharedContractNeededNoChange()
    {
        string src = MarkerInfoBoxViewSource();
        int idx = src.IndexOf("internal static void PopulateMarkerMenu(");
        Assert.True(idx >= 0, "PopulateMarkerMenu not found");
        int bodyStart = src.IndexOf('{', idx);
        Assert.True(bodyStart >= 0);
        string bodyOpening = src.Substring(bodyStart, 200);
        Assert.Contains("menu.Items.Clear();", bodyOpening);
    }
}
