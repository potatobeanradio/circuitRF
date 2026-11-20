using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request (2026-09-05): a "Pop Out" entry on the CANVAS context menu of both editors — the
/// menu a right-click on empty canvas raises, not the one a right-click on an instance raises — so
/// moving up one level of hierarchy has a context-menu route beside the toolbar button and Ctrl+[.
///
/// <para>The schematic had no canvas menu at all before this: its single ContextMenu cancelled its
/// own <c>Opening</c> whenever the click missed every component. It now serves BOTH menus from that
/// one instance — component items hidden for the canvas menu, Pop Out hidden for the component one.
/// The layout already had one menu for both cases, so Pop Out simply leads it.</para>
///
/// <para>Neither view can be constructed headlessly (this suite calls no Avalonia runtime API — see
/// <see cref="LayoutContextMenuStackingTests"/>'s own note), so the wiring is pinned by reading the
/// real AXAML and code-behind, and the enabled-state PREDICATE both menus read is exercised against
/// the real documents.</para>
/// </summary>
public class CanvasContextMenuPopOutTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string SchematicXaml() =>
        ReadRepoFile(Path.Combine("src", "Ui", "Views", "Content", "SchematicView.axaml"));

    private static string SchematicCodeBehind() =>
        ReadRepoFile(Path.Combine("src", "Ui", "Views", "Content", "SchematicView.axaml.cs"));

    private static string LayoutCodeBehind() =>
        ReadRepoFile(Path.Combine("src", "Ui", "Views", "Layout", "LayoutEditorView.axaml.cs"));

    // ── Schematic ─────────────────────────────────────────────────────────────

    [Fact]
    public void SchematicMenu_DeclaresPopOut_HiddenByDefault_AndWiredToItsOwnHandler()
    {
        var menu = XDocument.Parse(SchematicXaml())
            .Descendants()
            .First(e => e.Name.LocalName == "ContextMenu"
                        && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                           == "ComponentContextMenu");

        var popOut = menu.Elements()
            .Single(e => e.Name.LocalName == "MenuItem" && (string?)e.Attribute("Header") == "Pop Out");

        Assert.Equal("OnCtxPopOut", (string?)popOut.Attribute("Click"));

        // Hidden by default, because the COMPONENT menu is the common case: an item that defaulted to
        // visible would appear on every component's menu for the one frame before Opening ran.
        Assert.Equal("False", (string?)popOut.Attribute("IsVisible"));
    }

    [Fact]
    public void SchematicBackgroundRightClick_NoLongerCancelsTheMenu()
    {
        string src = SchematicCodeBehind();

        // The old behaviour, verbatim: `e.Cancel = true; // don't show menu on empty canvas click`.
        // With Pop Out on it, the canvas menu now has something to show.
        Assert.DoesNotContain("don't show menu on empty canvas click", src);
    }

    [Fact]
    public void SchematicCanvasMenu_ShowsPopOutAndTheFilterDesigner_HidingEveryComponentItemGenerically()
    {
        string src = SchematicCodeBehind();

        // Walking the menu's own Items — rather than naming each component item — is what stops an
        // item added later from leaking onto the canvas menu by being forgotten in this handler.
        Assert.Contains("foreach (var entry in ComponentContextMenu.Items)", src);
        Assert.Contains("control.IsVisible = hasTarget;", src);
        Assert.Contains("CtxPopOut.IsVisible = CtxFilterDesigner.IsVisible = !hasTarget;", src);
    }

    /// <summary>The canvas menu's Filter Designer… row (docs/design/filter-synthesis.md §6): hidden by default as Pop Out
    /// is, and opening the Designer on no part over this view's own schematic, which Place then targets.</summary>
    [Fact]
    public void SchematicMenu_DeclaresTheFilterDesigner_HiddenByDefault_OverThisSchematic()
    {
        var menu = XDocument.Parse(SchematicXaml())
            .Descendants()
            .First(e => e.Name.LocalName == "ContextMenu"
                        && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                           == "ComponentContextMenu");
        var row = menu.Elements()
            .Single(e => e.Name.LocalName == "MenuItem" && (string?)e.Attribute("Header") == "Filter Designer…");

        Assert.Equal("OnCtxFilterDesigner", (string?)row.Attribute("Click"));
        Assert.Equal("False", (string?)row.Attribute("IsVisible"));
        Assert.Contains("=> Views.Filters.FilterDesignerWindow.Show(Vm, null, TopLevel.GetTopLevel(this) as Window);",
                        SchematicCodeBehind());
    }

    [Fact]
    public void SchematicPopOut_IsGatedOnCanPopOut_AndRoutesThroughTheToolbarsOwnHandler()
    {
        string src = SchematicCodeBehind();

        Assert.Contains("CtxPopOut.IsEnabled = canPop;", src);
        Assert.Contains("as SchematicDocument)?.CanPopOut", src);

        // One mechanism, three surfaces (menu item, toolbar button, Ctrl+[) — the menu item delegates
        // rather than calling PopOutOf a second time.
        Assert.Contains("private void OnCtxPopOut(object? sender, RoutedEventArgs e) => OnToolbarPopOut(sender, e);", src);
    }

    [Fact]
    public void SchematicMenu_PushInAndPopOut_PointTheSameWayTheToolbarButtonsDo()
    {
        var menu = XDocument.Parse(SchematicXaml())
            .Descendants()
            .First(e => e.Name.LocalName == "ContextMenu"
                        && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                           == "ComponentContextMenu");

        static string? IconOf(XElement item) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == "MenuItem.Icon")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "MaterialIcon")
                ?.Attribute("Kind")?.Value;

        var items = menu.Elements().Where(e => e.Name.LocalName == "MenuItem").ToList();

        // Owner, 2026-09-05: the menu's Push In pointed RIGHT while the toolbar button for the same
        // command pointed DOWN. Down is in, up is out, on every surface.
        Assert.Equal("ArrowDown", IconOf(items.Single(i => (string?)i.Attribute("Header") == "Push In")));
        Assert.Equal("ArrowUp",   IconOf(items.Single(i => (string?)i.Attribute("Header") == "Pop Out")));

        string? ToolbarIcon(string name) => XDocument.Parse(SchematicXaml())
            .Descendants()
            .Single(e => e.Name.LocalName == "Button"
                         && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name)
            .Elements().FirstOrDefault(c => c.Name.LocalName == "MaterialIcon")?.Attribute("Kind")?.Value;

        Assert.Equal("ArrowDown", ToolbarIcon("PushInBtn"));
        Assert.Equal("ArrowUp",   ToolbarIcon("PopOutBtn"));
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    [Fact]
    public void LayoutMenu_ContributesPopOut_GatedOnCanPopOut_AndRoutingThroughDoPopOut()
    {
        string src = LayoutCodeBehind();

        Assert.Contains("MenuItem BuildPopOutItem()", src);
        Assert.Contains("Header = \"Pop Out\"", src);
        Assert.Contains("doc is { CanPopOut: true, Hierarchy: not null }", src);
        Assert.Contains("if (can) mi.Click += (_, _) => DoPopOut(doc!);", src);

        // Ctrl+[ is Key.OemOpenBrackets, which a KeyGesture renders by that NAME — so the accelerator
        // rides on the tooltip in both editors rather than on InputGesture, which is also what the
        // whole documentation build turned on: "Ctrl+[" is not a parseable KeyGesture at all, and the
        // AXAML attribute took the headless DocGen run down with "Requested value '[' was not found."
        Assert.DoesNotContain("InputGesture = new KeyGesture(Key.OemOpenBrackets", src);
        Assert.DoesNotContain("InputGesture=\"Ctrl+[\"", SchematicXaml());

        // Contributed in the view, alongside Re-reference Cell… — LayoutCanvas has no LayoutDocument
        // and therefore no way to reach the hierarchy host.
        Assert.DoesNotContain("Pop Out", ReadRepoFile(Path.Combine("src", "Ui", "Controls", "LayoutCanvas.cs")));
    }

    // ── The predicate both menus read ─────────────────────────────────────────

    [Fact]
    public void SchematicDocument_CanPopOut_IsFalseAtTheTopLevel_AndTrueOneLevelDown()
    {
        var doc = new SchematicDocument("TB", new SchematicViewModel(new SchematicEditModel()));
        Assert.False(doc.CanPopOut);   // the item is present but disabled

        doc.PushIn(new SchematicViewModel(new SchematicEditModel()), "X1");
        Assert.True(doc.CanPopOut);    // enabled

        doc.PopOut();
        Assert.False(doc.CanPopOut);
    }

    [Fact]
    public void LayoutDocument_CanPopOut_IsFalseAtTheTopLevel_AndTrueOneLevelDown()
    {
        static LayoutView Fresh() => new()
        {
            DbuPerMicron = 1000,
            DisplayUnit  = LayoutUnit.Um,
            SnapDbu      = 1000,
            AngleMode    = AngleMode.AnyAngle,
        };

        var doc = new LayoutDocument("Untitled-Layout-1", new LayoutEditorViewModel(Fresh()));
        Assert.False(doc.CanPopOut);

        doc.PushIn(new LayoutEditorViewModel(Fresh()), "X1");
        Assert.True(doc.CanPopOut);

        doc.PopOut();
        Assert.False(doc.CanPopOut);
    }
}
