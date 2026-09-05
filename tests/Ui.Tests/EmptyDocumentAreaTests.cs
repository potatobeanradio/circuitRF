using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  The empty document area shows the Welcome page's icon, and no text at all.
//
//  Owner request, 2026-09-04: with no documents open, show the icon from the Welcome page on its own
//  — not "No documents open", and not "Welcome to circuitRF" either. The Welcome document PAGE is
//  deliberately untouched.
//
//  The text was never in this repository: Dock.Model.Mvvm's DocumentDock.EmptyContent DEFAULTS to the
//  string "No documents open", which is why grepping for it here finds only comments. The first test
//  below pins that default through the real package, because every other assertion in this file is
//  only meaningful while it holds — if a Dock upgrade changes it, this is the test that says so.
//
//  The icon itself is drawn by a DocumentControl.EmptyContentTemplate setter in the shared style
//  file, which is what reaches the document docks Dock's own tab context menu creates and this
//  repository never constructs. That half is source-scanned: it is XAML rendered by the real Dock
//  theme, and this project's tests may not run the Avalonia runtime.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class EmptyDocumentAreaTests
{
    // ── The default this exists to replace ────────────────────────────────────

    [Fact]
    public void DocksOwnDefaultEmptyContent_IsStillTheTextThisReplaces()
    {
        // Not a tautology: it is the reason the three docks below set EmptyContent = null, and the
        // only thing here that can be invalidated by a package upgrade rather than by a code change.
        Assert.Equal("No documents open", new DocumentDock().EmptyContent);
    }

    // ── Every document dock this repository builds ────────────────────────────

    [Fact]
    public void ThePrimaryDocumentDock_CarriesNoEmptyText()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        Assert.Null(((DocumentDock)f.DocumentDock!).EmptyContent);
    }

    [Fact]
    public void ARestoredSplitPane_CarriesNoEmptyText()
    {
        var f = new CircuitRfDockFactory();
        f.CreateLayout();

        f.CreateLayoutFromState(new CwsDockLayout
        {
            DocumentRegion = new CwsDocumentRegion
            {
                Orientation = "Horizontal",
                Children =
                {
                    new CwsDocumentRegion { Documents = { "Amp/schematic/Amp.csch" } },
                    new CwsDocumentRegion { Documents = { "tech/pcb.ctech" } },
                },
            },
        }, floatingGeometry: null, documentIsOpen: _ => true);

        // Pane 0 is the preserved primary dock, covered above; pane 1 is the one built here.
        Assert.Equal(2, f.RestoredDocumentPanes.Count);
        Assert.Null(((DocumentDock)f.RestoredDocumentPanes[1].Dock).EmptyContent);
    }

    /// <summary>
    /// The pane Technology ▾ ▸ Edit… opens beside a layout. It is the pane most likely to be seen
    /// empty, because closing its one tab is the ordinary way to dismiss it.
    /// </summary>
    [Fact]
    public void ThePaneOpenedBesideADocument_CarriesNoEmptyText()
    {
        var f = new CircuitRfDockFactory();
        var root = f.CreateLayout();
        f.InitLayout(root);
        f.RemoveWelcomeStub();

        var layout = new StubDocument("board", StubDocument.StubKind.Welcome);
        var tech   = new StubDocument("pcb",   StubDocument.StubKind.Welcome);
        f.OpenDocument(layout);
        f.OpenDocument(tech);

        Assert.True(f.SplitDocumentRightOf(tech, layout, 0.2));

        var pane = Assert.IsAssignableFrom<IDocumentDock>(tech.Owner);
        Assert.Null(((DocumentDock)pane).EmptyContent);
    }

    /// <summary>
    /// Every <c>new DocumentDock</c> in the factory sets it — the guard against a fourth one being
    /// added later without it, which would show the package default in one pane and the icon in the
    /// rest.
    /// </summary>
    [Fact]
    public void EveryDocumentDockTheFactoryBuilds_SetsEmptyContent()
    {
        var src = ReadRepo("src", "Ui", "ViewModels", "Dock", "CircuitRfDockFactory.cs");

        int created = Regex.Matches(src, @"new DocumentDock\b").Count;
        int cleared = Regex.Matches(src, @"EmptyContent\s*=\s*null").Count;

        Assert.Equal(created, cleared);
    }

    // ── What is drawn instead ─────────────────────────────────────────────────

    /// <summary>
    /// The style is what actually draws the icon, and it is the half that reaches EVERY document
    /// dock — including the ones Dock's own tab context menu creates (New Horizontal/Vertical
    /// Document Dock), which the factory never sees.
    /// </summary>
    [Fact]
    public void TheSharedStyle_TemplatesTheEmptyAreaForBothDocumentControls()
    {
        var styles = ReadRepo("src", "Ui", "Styles", "CircuitRfStyles.axaml");

        // Found from the SETTER outwards, not from the selector inwards: DocumentControl already
        // carries an unrelated chrome style earlier in this file, and looking the selector up by name
        // finds that one.
        var templated = Regex
            .Matches(styles, @"Selector=""([^""]+)""(?:(?!Selector=)[\s\S])*?<Setter Property=""EmptyContentTemplate"">([\s\S]*?)</Setter>")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        Assert.Equal(
            ["dockCtrl|DocumentControl", "dockCtrl|MdiDocumentControl"],
            templated.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var body in templated.Values)
            Assert.Contains($"Kind=\"{EmptyDocumentArea.IconKind}\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// "No text, not even Welcome to circuitRF" — so neither template may carry a TextBlock. This is
    /// the request itself, stated as a test.
    /// </summary>
    [Fact]
    public void NeitherEmptyAreaTemplate_ContainsAnyText()
    {
        var styles = ReadRepo("src", "Ui", "Styles", "CircuitRfStyles.axaml");

        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(styles,
                     @"<Setter Property=""EmptyContentTemplate"">(.*?)</Setter>", RegexOptions.Singleline))
        {
            Assert.DoesNotContain("TextBlock", m.Groups[1].Value, StringComparison.Ordinal);
            Assert.DoesNotContain("Text=",     m.Groups[1].Value, StringComparison.Ordinal);
        }

        // …and there really are two of them, so an empty match set cannot pass this vacuously.
        Assert.Equal(2, Regex.Matches(styles, @"<Setter Property=""EmptyContentTemplate"">").Count);
    }

    /// <summary>
    /// It is the WELCOME PAGE's icon, not a second icon that can drift away from it. The Welcome page
    /// itself is deliberately unchanged — it keeps its heading; only the empty area behind it drops
    /// the text.
    /// </summary>
    [Fact]
    public void TheIconIsTheWelcomePages_AndTheWelcomePageIsUnchanged()
    {
        var stub = ReadRepo("src", "Ui", "Views", "Content", "StubContentView.axaml");

        Assert.Contains($"Kind=\"{EmptyDocumentArea.IconKind}\"", stub, StringComparison.Ordinal);

        // The page still shows its own label — this change is not allowed to strip that.
        Assert.Contains("Text=\"{Binding Label}\"", stub, StringComparison.Ordinal);
        Assert.Equal("Welcome to circuitRF", new StubDocument("Welcome", StubDocument.StubKind.Welcome).Label);
    }

    private static string ReadRepo(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
