using System.IO;
using System.Linq;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Window menu (owner request, 2026-07-30): every open circuitRF window, selectable to bring it to
//  the front — workspace first, then torn-off DOCUMENT windows, a separator, then floating TOOL
//  windows, with a dirty indicator on anything unsaved.
//
//  WorkspaceViewModel cannot be constructed headlessly (its ctor builds a Dock layout and posts to
//  the Dispatcher) — a standing constraint recorded in src/Ui/CLAUDE.md — and EnumerateWindowEntries
//  reads Application.Current.ApplicationLifetime, which does not exist in a headless test run. So the
//  two rules that are genuinely ours (ordering, and how the workspace label is derived) are pinned
//  here against the same source text/behaviour, and a source scan guards the wiring.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class WindowMenuTests
{
    private static string RepoRoot()
    {
        var dir = System.AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    // ── The workspace label must come from the FOLDER, never the .cws file stem ──
    //
    // `.cws` is a dotfile: Path.GetFileNameWithoutExtension(".cws") returns ".cws", not the workspace
    // name. This trap is already documented in src/Ui/CLAUDE.md and has been hit before.

    [Fact]
    public void WorkspaceLabel_DerivesFromFolderName_NotTheCwsFileStem()
    {
        var cws = Path.Combine("home", "designs", "AmpProject", ".cws");

        var wrong = Path.GetFileNameWithoutExtension(cws);
        var right = Path.GetFileName(Path.GetDirectoryName(cws));

        // .NET treats a dotfile as ALL extension: GetExtension(".cws") == ".cws", so
        // GetFileNameWithoutExtension(".cws") is the EMPTY string — verified directly, not assumed.
        // (src/Ui/CLAUDE.md previously said it returns ".cws"; the consequence is the same — never
        // the workspace name — but the detail was wrong and is corrected there.)
        Assert.Equal("", wrong);              // the trap, reproduced so the test has teeth
        Assert.Equal("AmpProject", right);    // what the menu must show
    }

    [Fact]
    public void ShellHeader_UsesGetDirectoryNamePlusGetFileName_NotGetFileNameWithoutExtension()
    {
        var src = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));

        // The header derivation lives in ShellHeader(); assert it never reaches for the file stem.
        var shell = src[src.IndexOf("internal string ShellHeader()", System.StringComparison.Ordinal)..];
        shell = shell[..shell.IndexOf("\n    }", System.StringComparison.Ordinal)];

        Assert.Contains("GetDirectoryName", shell);
        Assert.Contains("GetFileName(dir)", shell);
        Assert.DoesNotContain("GetFileNameWithoutExtension", shell);
    }

    // ── Ordering: workspace, documents, separator, tools ──────────────────────

    [Fact]
    public void EnumerationOrder_IsWorkspaceThenDocumentsThenSeparatorThenTools()
    {
        var src = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));
        var body = src[src.IndexOf("EnumerateWindowEntries()", System.StringComparison.Ordinal)..];

        var shellIdx = body.IndexOf("ShellHeader()", System.StringComparison.Ordinal);
        var docsIdx  = body.IndexOf("!w.FloatsAnyTool()", System.StringComparison.Ordinal);
        var toolsIdx = body.IndexOf("w.FloatsAnyTool())", System.StringComparison.Ordinal);

        Assert.True(shellIdx >= 0 && docsIdx > shellIdx,
            "the workspace entry must be added before any document float");
        Assert.True(toolsIdx > docsIdx,
            "tool floats must be added after document floats");

        // Exactly one separator, and it precedes the first tool entry only.
        Assert.Contains("SeparatorBefore: i == 0", body);
        Assert.Contains("SeparatorBefore: false", body);
    }

    [Fact]
    public void ClosedWindows_AreSkipped_ViaPlatformImplGuard()
    {
        var src = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));

        // A closed window lingering in desktop.Windows once crashed Window.SortWindowsByZOrder; the
        // same PlatformImpl guard RaiseFloatingToolWindows uses must apply here too.
        Assert.Contains("PlatformImpl is not null", src);
    }

    // ── Dirty marking ─────────────────────────────────────────────────────────

    [Fact]
    public void DirtyMark_MatchesTheBulletDocumentTabsAlreadyUse()
    {
        var windowMenu = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));
        Assert.Contains("DirtyMark = \"• \"", windowMenu);

        // Same glyph the tab titles use, so the two surfaces cannot disagree.
        var schematicDoc = Read(Path.Combine("src", "Ui", "Schematic", "SchematicDocument.cs"));
        Assert.Contains("• ", schematicDoc);
    }

    [Fact]
    public void DataDisplayDirty_IsReDerived_BecauseItsIsDirtyIsNotWiredToLiveEdits()
    {
        var src = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));

        // DataDisplayDocument.IsDirty is documented as never wired to live edits, so its title bullet
        // can be stale; HasUnsavedChanges() is the authoritative baseline comparison.
        Assert.Contains("HasUnsavedChanges()", src);
    }

    [Fact]
    public void WorkspaceEntry_CountsOnlyDockedDocuments_NotFloats()
    {
        var src = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));
        var shell = src[src.IndexOf("internal string ShellHeader()", System.StringComparison.Ordinal)..];
        shell = shell[..shell.IndexOf("\n    }", System.StringComparison.Ordinal)];

        // A torn-off document carries its own bullet on its own entry; counting it here as well would
        // mark the workspace dirty for work the menu already attributes to another window.
        Assert.Contains("DocumentDock?.VisibleDockables", shell);
    }

    // ── Both menu surfaces come from ONE resolution ───────────────────────────

    [Fact]
    public void BothMenuSurfaces_AreWired_AndShareOneOrderingRule()
    {
        var axaml = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        var code  = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));

        // In-window menu: bound to the live collection, rebuilt as it opens.
        Assert.Contains("ItemsSource=\"{Binding WindowMenuItems}\"", axaml);
        Assert.Contains("SubmenuOpened=\"OnWindowMenuOpened\"", axaml);

        // macOS: a declared "Window" native item, populated in code-behind.
        Assert.Contains("<NativeMenuItem Header=\"Window\">", axaml);
        Assert.Contains("RebuildNativeWindowMenu", code);

        // Both surfaces must consume the SAME enumeration — never two ordering rules.
        Assert.Contains("EnumerateWindowEntries()", code);
    }

    [Fact]
    public void WindowMenu_SitsImmediatelyAfterSimulate()
    {
        var axaml = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));

        var simulate = axaml.IndexOf("Header=\"_Simulate\"", System.StringComparison.Ordinal);
        var window   = axaml.IndexOf("Header=\"_Window\"",   System.StringComparison.Ordinal);
        var help     = axaml.IndexOf("Header=\"_Help\"",     System.StringComparison.Ordinal);

        Assert.True(simulate >= 0 && window > simulate, "Window must follow Simulate");
        if (help >= 0)
            Assert.True(window < help, "Window must sit before Help");
    }

    // ── The empty-submenu latch (owner-reported: "No items appear under the Window menu") ──

    [Fact]
    public void RebuildNeverLeavesTheCollectionEmpty_OrTheMenuLatchesShutForever()
    {
        var src = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"));

        // An Avalonia MenuItem derives HasSubMenu from its item COUNT. With an empty ItemsSource the
        // parent is a leaf: clicking opens nothing, so SubmenuOpened — which is what triggers the
        // rebuild — can never fire. Empty is therefore self-latching, not merely transient.
        Assert.Contains("WindowMenuItems.Count == 0", src);
        Assert.Contains("(No Windows)", src);
    }

    [Fact]
    public void TheMenuIsSeededEagerly_NotOnlyOnSubmenuOpened()
    {
        var code = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));

        // Must be built before the first click. Recent Workspaces (the working precedent) is seeded
        // in the VM constructor for the same reason.
        var onOpened = code[code.IndexOf("protected override void OnOpened", System.StringComparison.Ordinal)..];
        onOpened = onOpened[..onOpened.IndexOf("\n    }", System.StringComparison.Ordinal)];
        Assert.Contains("RebuildWindowMenuItems", onOpened);

        // And refreshed when the workspace comes forward, so floats opened since then appear.
        Assert.Contains("Activated +=", code);
    }

    // ── The macOS trap: the in-window Menu is hidden there, so SubmenuOpened never fires ──

    [Fact]
    public void NativeWindowMenu_HasItsOwnJustInTimeRefresh_NotOnlyTheInWindowSubmenuOpened()
    {
        var axaml = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        var code  = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));

        // The in-window Menu is hidden on macOS, so its SubmenuOpened cannot drive the native menu.
        Assert.Contains("IsVisible=\"{OnPlatform True, macOS=False}\"", axaml);

        // NativeMenu.NeedsUpdate is Avalonia's documented "modify items before the menu is shown"
        // hook — the macOS counterpart. Without it the native menu was built once and never again.
        Assert.Contains("NeedsUpdate", code);
    }

    [Fact]
    public void WindowMenuChanged_IsActuallySubscribed_SoEveryRebuildReachesBothSurfaces()
    {
        var code = Read(Path.Combine("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));

        // Declaring the event but never subscribing it is precisely what left the macOS menu stale.
        Assert.Contains("WindowMenuChanged       += RebuildNativeWindowMenu", code);
        Assert.Contains("WindowMenuChanged       -= RebuildNativeWindowMenu", code);
    }
}
