using System.IO;
using System.Linq;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-dock-layout-persistence.md §4B gates 16–18 — the two window-behaviour bugs.
///
/// <para>Real <c>Window</c>s cannot be constructed without an Avalonia platform, so what is asserted
/// here is the MECHANISM each fix rests on: which owner mode a floated dockable gets, and that a
/// tool-only float contributes no document context. The parts that genuinely need a live desktop
/// (z-order, focus) are pinned by source scan and named as not-interactively-verified in the
/// completion note.</para>
/// </summary>
public sealed class DockWindowBehaviourTests
{
    private sealed class FakeDocument : Document { }

    private static ToolDock ToolDockWith(params ITool[] tools)
    {
        var f = new CircuitRfDockFactory();
        return new ToolDock
        {
            VisibleDockables = f.CreateList<IDockable>(tools),
            ActiveDockable   = tools.Length > 0 ? tools[0] : null,
        };
    }

    private static DocumentDock DocumentDockWith(params IDockable[] docs)
    {
        var f = new CircuitRfDockFactory();
        return new DocumentDock
        {
            VisibleDockables = f.CreateList(docs),
            ActiveDockable   = docs.Length > 0 ? docs[0] : null,
        };
    }

    // ── Gate 16 — the macOS menu bar survives a floating tool window ──────────

    /// <summary>
    /// R-dock-12's application-scope attachment, kept as the baseline (it is what the menu bar falls
    /// back to when no window is key).
    ///
    /// <para><b>It is NOT what makes a floating window show the menu</b> — observed directly: with this
    /// in place the owner still saw an empty menu bar while a floating tool window was key, so Avalonia
    /// does not fall back to the application-scope menu for a key window that has none of its own.
    /// <see cref="EveryFloatedWindowGetsTheMacOsMenu_NotOnlyOneHostingADocument"/> covers the mechanism
    /// that actually works.</para>
    ///
    /// <para>Asserted on every platform, not behind a macOS branch, so the wiring is not macOS-only
    /// code that nobody else ever exercises.</para>
    /// </summary>
    [Fact]
    public void Gate16_NativeMenuIsAttachedAtApplicationScope_OnEveryPlatform()
    {
        var src = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");

        Assert.Contains("NativeMenu.SetMenu(app, menu)", src);
        Assert.Contains("AttachNativeMenuAtApplicationScope", src);

        // The attachment must not be gated on macOS — see the method's own doc comment.
        var method = src[src.IndexOf("internal static void AttachNativeMenuAtApplicationScope")..];
        var body   = method[..method.IndexOf("private void AttachNativeMenuAtApplicationScope")];
        Assert.DoesNotContain("IsMacOS", body);
    }

    [Fact]
    public void Gate16_TheApplicationScopedMenuIsTheSAMEInstanceTheShellDeclares_NotACopy()
    {
        // A duplicate menu per window would also work and is the wrong fix: it multiplies the thing
        // that must stay in sync.
        var src = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");
        Assert.Contains("var menu = NativeMenu.GetMenu(shell);", src);
        Assert.Contains("NativeMenu.SetMenu(app, menu);", src);
    }

    // ── Gate 17 — a tool window is not a document context ────────────────────

    /// <summary>
    /// R-dock-13. "The active document" must keep meaning the last active DOCUMENT, not become null
    /// the moment the user clicks into a tool panel — otherwise Save greys out because someone
    /// clicked the Messages list, which is a worse bug than the one being fixed.
    /// </summary>
    [Fact]
    public void Gate17_AToolOnlyFloat_ContributesNoDocument_SoTheActiveDocumentIsUnchanged()
    {
        var toolOnly = ToolDockWith(new MessagesTool(), new AnalysesTool());
        Assert.Null(WorkspaceViewModel.FindAnyDocumentInDock(toolOnly));
    }

    [Fact]
    public void Gate17_ADocumentFloat_DoesContributeItsDocument()
    {
        var doc = new FakeDocument();
        Assert.Same(doc, WorkspaceViewModel.FindAnyDocumentInDock(DocumentDockWith(doc)));
    }

    [Fact]
    public void Gate17_FocusTrackingOnlyOverridesTheActiveDocumentWhenOneWasFound()
    {
        // The mechanism: _focusedWindowDocument is written only inside `if (doc is not null)`, so a
        // tool float leaves it alone and command enablement keeps targeting the real document.
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        var i   = src.IndexOf("void ApplyFocusedDocument()");
        Assert.True(i > 0, "ApplyFocusedDocument not found");

        var body        = src[i..(i + 1600)];
        var assign      = System.Text.RegularExpressions.Regex.Match(body, @"_focusedWindowDocument\s*=\s*doc;");
        var guardIndex  = body.IndexOf("if (doc is not null)");
        Assert.True(assign.Success, "the active-document override assignment was not found");
        Assert.True(guardIndex > 0 && assign.Index > guardIndex,
            "the active-document override must only be written when a document was actually found");
    }

    // ── Gate 18 — tool windows raise with the workspace; documents do not ────

    /// <summary>
    /// R-dock-14. Ownership was the brief's preferred mechanism and does NOT deliver the raise here —
    /// see <see cref="Gate18_TheWorkspaceRaisesFloatingToolWindows_AndTakesFocusBack"/> for what does.
    /// The modes are still meaningful and still pinned: <c>None</c> on documents is what positively
    /// stops THEM being owned, which is the half a naive fix breaks.
    /// </summary>
    [Fact]
    public void Gate18_AFloatedToolIsOwned_AFloatedDocumentIsAPeer()
    {
        Assert.Equal(DockWindowOwnerMode.Default,
                     CircuitRfDockFactory.OwnerModeFor(ToolDockWith(new MessagesTool())));

        // The half a naive fix breaks: owning documents too would make a torn-off schematic float
        // above the shell, which is not what a peer window does.
        Assert.Equal(DockWindowOwnerMode.None,
                     CircuitRfDockFactory.OwnerModeFor(DocumentDockWith(new FakeDocument())));
    }

    [Fact]
    public void Gate18_ContainsTool_FindsAToolAtAnyDepth()
    {
        var f      = new CircuitRfDockFactory();
        var nested = new ToolDock { VisibleDockables = f.CreateList<IDockable>(new PaletteTool()) };
        var outer  = new DocumentDock { VisibleDockables = f.CreateList<IDockable>(nested) };

        Assert.True(CircuitRfDockFactory.ContainsTool(outer));
        Assert.False(CircuitRfDockFactory.ContainsTool(DocumentDockWith(new FakeDocument())));
        Assert.False(CircuitRfDockFactory.ContainsTool(null));
    }

    /// <summary>
    /// Which owner mode actually works here was decompiled, not assumed:
    /// <c>DockWindowOwnerMode.RootWindow</c> looks for the root dock's own <c>IDockWindow</c>, which
    /// our shell has none of (it is hosted by a <c>DockControl</c>), so it resolves to a NULL owner —
    /// the opposite of what R-dock-14 asks for. This pins the reasoning next to the code.
    /// </summary>
    [Fact]
    public void Gate18_RootWindowModeIsDeliberatelyNotUsed_AndTheReasonIsRecorded()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs");
        Assert.Contains("DockWindowOwnerMode.RootWindow", src);          // named in the doc comment…
        Assert.DoesNotContain("= DockWindowOwnerMode.RootWindow", src);  // …but never assigned.
        Assert.Contains("Topmost", src);                                 // and Topmost is ruled out.
    }

    /// <summary>
    /// Owner report: floating tool panels stayed behind other applications when the workspace window
    /// was focused. Ownership alone did not deliver R-dock-14, so the raise is explicit.
    ///
    /// <para>R-dock-15 is the hard part and what this pins: raising must NOT steal focus. The idiom —
    /// activate every tool window, then re-activate the initiator — is Dock's own
    /// (<c>WindowActivationHelper.ActivateAllWindows</c>, which it runs on every window drag); that
    /// helper is <c>internal</c>, so this is the same shape over just our tool windows.</para>
    /// </summary>
    [Fact]
    public void Gate18_TheWorkspaceRaisesFloatingToolWindows_AndTakesFocusBack()
    {
        var src = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");

        // The raise must be driven from the shell's Activated hook. Asserted by BEHAVIOUR rather than
        // by the exact lambda text: that handler became a block lambda on 2026-07-30 when the Window
        // menu started refreshing there too, and pinning the one-liner made this test fail for a
        // change that did not affect what it guards.
        var activatedIdx = src.IndexOf("Activated +=", StringComparison.Ordinal);
        Assert.True(activatedIdx > 0, "the shell must subscribe to Activated");

        var handlerRegion = src[activatedIdx..Math.Min(src.Length, activatedIdx + 600)];
        Assert.Contains("RaiseFloatingToolWindows();", handlerRegion);

        var i    = src.IndexOf("private void RaiseFloatingToolWindows()");
        Assert.True(i > 0, "RaiseFloatingToolWindows not found");
        var body = src[i..src.IndexOf("\n    protected override", i)];

        // Tool floats only — a torn-off DOCUMENT window is a peer and must stay where it is.
        Assert.Contains("w.FloatsAnyTool()", body);
        Assert.Contains("tool.Activate();", body);

        // …then focus returns to the workspace. Without this the raise hands the keyboard to
        // whichever panel was raised last, which is the half a naive fix breaks.
        var raiseIndex = body.IndexOf("tool.Activate();");
        var backIndex  = body.IndexOf("Activate();", raiseIndex + 1);
        Assert.True(backIndex > raiseIndex, "the workspace must be re-activated after the panels");
    }

    /// <summary>
    /// Our own <c>Activate()</c> re-raises <c>Activated</c>, and the platform delivers that
    /// asynchronously — so the guard must outlive the synchronous call, or the raise loops forever.
    /// </summary>
    [Fact]
    public void TheRaiseGuard_IsReleasedOnALaterDispatcherPass_NotSynchronously()
    {
        var src  = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml.cs");
        var i    = src.IndexOf("private void RaiseFloatingToolWindows()");
        var body = src[i..src.IndexOf("\n    protected override", i)];

        Assert.Contains("if (_raisingFloatingTools) return;", body);
        Assert.Contains("_raisingFloatingTools = true;", body);
        Assert.Contains("DispatcherPriority.Background", body);

        // A plain `finally { _raisingFloatingTools = false; }` would release before the asynchronous
        // Activated arrives, which is exactly the loop this avoids.
        Assert.DoesNotContain("finally\n        {\n            _raisingFloatingTools = false;",
                              body.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Gate18_TheDragTearOffPathAndTheRestorePathShareOneOwnerModeDecision()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/Dock/CircuitRfDockFactory.cs");
        var uses = System.Text.RegularExpressions.Regex.Matches(src, @"OwnerModeFor\(").Count;
        Assert.True(uses >= 3, "CreateWindowFrom, the restore builder, and the helper itself");
    }

    // ── Reported bug: no macOS menu bar while a floating TOOL window is key ──

    /// <summary>
    /// Owner report: on macOS, focusing a floating tool window left the File/Edit/View/… menus empty.
    ///
    /// <para>Attaching the menu at APPLICATION scope (R-dock-12's own preference) was not enough —
    /// observed directly: with that in place the symptom persisted, so Avalonia does not fall back to
    /// the application-scope menu for a key window that has none of its own. What does work is
    /// attaching the SAME <c>NativeMenu</c> instance to the window itself, which is already the proven
    /// mechanism for torn-off DOCUMENT windows here. The bug was purely the <c>doc is not null</c>
    /// gate that skipped tool-only floats.</para>
    /// </summary>
    [Fact]
    public void EveryFloatedWindowGetsTheMacOsMenu_NotOnlyOneHostingADocument()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        var i   = src.IndexOf("void ApplyFocusedDocument()");
        Assert.True(i > 0);
        var body = src[i..(i + 1600)];

        var attachIndex = body.IndexOf("AttachSharedNativeMenuIfMacOS(shellWindow, window)");
        var docGuard    = body.IndexOf("if (doc is not null)");
        Assert.True(attachIndex > 0, "the menu attach must still happen");
        Assert.True(attachIndex < docGuard, "…and must NOT sit inside the has-a-document branch");
    }

    /// <summary>
    /// Owner report: tearing a harmonicaRF document off its own tab crashed with
    /// <c>ArgumentException: "The menu being updated does not match."</c>, thrown from Avalonia's native
    /// menu exporter (<c>__MicroComIAvnMenuProxy.Update</c>) inside <c>HarmonicaMenuView.RecomputeAttachment</c>.
    ///
    /// <para>Root cause: <c>ApplyFocusedDocument</c> unconditionally attached circuitRF's OWN shared
    /// <c>NativeMenu</c> to every torn-off window — including one hosting a <c>HarmonicaDocument</c> or a
    /// <c>WBondDocument</c>, both of which independently manage their OWN per-window native-menu
    /// attachment (<c>HarmonicaMenuView.RecomputeAttachment</c> / the structurally identical mechanism in
    /// <c>WBondMenuView</c>). Two different <c>NativeMenu</c> C# instances being assigned to the SAME
    /// window in close succession corrupts <c>AvaloniaNativeMenuExporter</c>'s native-side state — the
    /// crash was a race between this call and the document's own attach, not a bug in either mechanism
    /// alone.</para>
    ///
    /// <para>Fix: exclude both document types from the shared-menu attach, since neither needs it — each
    /// already attaches its own menu to its own torn-off window.</para>
    /// </summary>
    [Fact]
    public void HarmonicaAndWBondDocuments_AreExcludedFromTheSharedMenuAttach_TheyOwnTheirOwn()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        var i   = src.IndexOf("void ApplyFocusedDocument()");
        Assert.True(i > 0);
        var body = src[i..(i + 1600)];

        var attachIndex = body.IndexOf("AttachSharedNativeMenuIfMacOS(shellWindow, window)");
        Assert.True(attachIndex > 0, "the menu attach must still happen for every OTHER document type");

        // The guard immediately preceding the attach call must exclude both document types by name —
        // not merely "doc is not null", or the harmonicaRF tear-off crash reproduces immediately.
        var guardStart = body.LastIndexOf("if (", attachIndex, StringComparison.Ordinal);
        Assert.True(guardStart >= 0, "expected an `if (...)` guard directly ahead of the attach call");
        var guardEnd = body.IndexOf(')', guardStart);
        var guard    = body[guardStart..guardEnd];

        Assert.Contains("HarmonicaDocument", guard, StringComparison.Ordinal);
        Assert.Contains("WBondDocument", guard, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wiring that installs the <c>Activated</c> handler used to run only off DocumentDock
    /// changes — which floating a TOOL never causes, so a torn-off tool window got no handler at all
    /// and could never attach its menu, whatever the gate above said.
    /// </summary>
    [Fact]
    public void FloatingAToolReWiresPerWindowState_NotJustFloatingADocument()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");
        Assert.Contains("_factory.WindowAdded += (_, _) =>", src);

        var i    = src.IndexOf("_factory.WindowAdded += (_, _) =>");
        var body = src[i..src.IndexOf("};", i)];
        Assert.Contains("TryWireWindowFocusTracking", body);
        Assert.Contains("TryWireHostWindowsUndo", body);
        // …and the stale-host sweep that stops the very next drag crashing.
        Assert.Contains("PurgeClosedHostWindows", body);
    }

    /// <summary>
    /// Owner's call: a tool panel is associated with the WORKSPACE, not with a document of its own, so
    /// its File menu reads "Close Workspace".
    ///
    /// <para>R-dock-13 still holds and is the reason this is a separate flag rather than clearing the
    /// active document: Save and Save-As must stay enabled and keep acting on the last active DOCUMENT
    /// while a tool panel has focus.</para>
    /// </summary>
    [Fact]
    public void AFocusedToolWindow_ReadsCloseWorkspace_WithoutLosingTheActiveDocument()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

        Assert.Contains(
            "ClosesASingleDocumentWindow =>\n        !_focusedWindowIsToolOnly && _focusedWindowDocument is not null;",
            src.Replace("\r\n", "\n"));
        Assert.Contains("=> ClosesASingleDocumentWindow ? \"Close Window\" : \"Close Workspace\";", src);

        // The tool-only branch must set the flag and leave _focusedWindowDocument alone.
        var i = src.IndexOf("else if (WindowFloatsATool(window))");
        Assert.True(i > 0, "tool-only focus branch not found");
        var end  = src.IndexOf("window.Activated +=", i);
        var body = src[i..end];
        Assert.Contains("_focusedWindowIsToolOnly = true;", body);
        // The comment names it; what must not appear is an ASSIGNMENT that would clobber it.
        Assert.DoesNotContain("_focusedWindowDocument =", body);
    }

    /// <summary>
    /// Windows/Linux get NO in-window menu inside a floating tool window — the owner's own constraint.
    /// True by construction: <c>TornOffFileMenuView</c> is embedded in the four DOCUMENT views only,
    /// and a tool float hosts a tool's view. Pinned so adding it to a tool view fails here.
    /// </summary>
    [Fact]
    public void TheInWindowTearOffMenu_IsEmbeddedInDocumentViewsOnly()
    {
        // This list is the reason the EM Setup gap survived: it enumerated four document views and
        // EmSetupEditorView was never added when that document type shipped, so a torn-off .cem had
        // no File menu at all on Windows/Linux and nothing failed. Adding a document type means
        // adding it HERE too — or, if it deliberately carries its own menu (harmonicaRF and wBond
        // both do, being standalone applications), to the exemption list below.
        string[] documentViews =
        [
            "src/Ui/Views/Content/SchematicView.axaml",
            "src/Ui/Views/Content/SymbolEditorView.axaml",
            "src/Ui/Views/Layout/LayoutEditorView.axaml",
            "src/Ui/Views/Layout/EmSetupEditorView.axaml",
            "src/Ui/Views/DataDisplay/DataDisplayView.axaml",
        ];
        foreach (var v in documentViews)
            Assert.Contains("TornOffFileMenuView", ReadRepoFile(v));

        string[] toolViews =
        [
            "src/Ui/Views/Properties/PropertiesView.axaml",
            "src/Ui/Views/Analyses/AnalysesToolView.axaml",
            "src/Ui/Views/ProjectTree/ProjectTreeView.axaml",
            "src/Ui/Views/Palette/PaletteToolView.axaml",
            "src/Ui/Views/Messages/MessagesView.axaml",
        ];
        foreach (var v in toolViews)
            Assert.DoesNotContain("TornOffFileMenuView", ReadRepoFile(v));
    }

    // ── Reopening a closed tool panel (View ▸ Panels) ────────────────────────

    /// <summary>
    /// Owner report: a closed tool panel could not be brought back except via View ▸ Reset Layout,
    /// which also discards every other panel placement the user had set up.
    /// </summary>
    [Fact]
    public void EveryToolPanelIsReachableByItsStableId()
    {
        var factory = new CircuitRfDockFactory();
        factory.CreateLayout();

        foreach (var id in DockPanelIds.All)
            Assert.NotNull(factory.ToolById(id));

        Assert.Null(factory.ToolById("NoSuchPanel"));
        Assert.Null(factory.ToolById(null));
    }

    [Fact]
    public void ADockedToolIsFound_AndAClosedOneIsNot()
    {
        var factory = new CircuitRfDockFactory();
        factory.CreateLayout();

        var properties = factory.PropertiesTool!;
        Assert.True(factory.TryFindTool(properties, out var parent, out var window));
        Assert.NotNull(parent);
        Assert.Null(window);           // docked in the shell, not floating

        // Closing it removes it from its dock; it must then read as NOT shown, or the View item
        // would "focus" a panel that is not on screen and appear to do nothing.
        parent!.VisibleDockables!.Remove(properties);
        Assert.False(factory.TryFindTool(properties, out _, out _));
    }

    [Fact]
    public void AToolInAFloatingWindowIsFound_WithItsWindow()
    {
        var factory = new CircuitRfDockFactory();
        var state   = DockLayoutDefaults.Default();

        // Messages floated instead of docked.
        state.Panels.RemoveAll(p => p.Id == DockPanelIds.Messages);
        state.FloatingWindows.Add(new CwsFloatingWindow
        {
            X = 10, Y = 10, Width = 300, Height = 200, Panels = [DockPanelIds.Messages],
        });

        factory.CreateLayout();
        factory.CreateLayoutFromState(state);

        Assert.True(factory.TryFindTool(factory.MessagesTool!, out var parent, out var window));
        Assert.NotNull(parent);
        Assert.NotNull(window);        // …and the caller knows WHICH window to bring forward
    }

    /// <summary>
    /// A closed panel keeps its instance — and therefore its state — so reopening restores the panel
    /// the user had rather than a blank replacement.
    /// </summary>
    [Fact]
    public void ClosingAToolDoesNotDiscardItsInstance()
    {
        var factory = new CircuitRfDockFactory();
        factory.CreateLayout();

        var before = factory.AnalysesTool!;
        Assert.True(factory.TryFindTool(before, out var parent, out _));
        parent!.VisibleDockables!.Remove(before);

        Assert.Same(before, factory.ToolById(DockPanelIds.Analyses));
    }

    [Fact]
    public void ShowToolPanel_FocusesWhenShown_AndOpensAFloatingWindowWhenNot()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.Docking.cs");
        var i   = src.IndexOf("private void ShowToolPanel(string? panelId)");
        Assert.True(i > 0, "ShowToolPanel not found");
        var body = src[i..src.IndexOf("\n    /// <summary>\n    /// Where a reopened panel goes", i)];

        Assert.Contains("_factory.TryFindTool(tool, out var parent, out var window)", body);
        Assert.Contains("_factory.SetActiveDockable(tool);", body);
        // A floating panel needs ITS window raised, not the shell's.
        Assert.Contains("window?.Host is Window host", body);
        Assert.Contains("_factory.FloatTool(tool,", body);
        // A newly opened window still goes through R-dock-6 validation.
        Assert.Contains("placer.Place(", body);
    }

    [Fact]
    public void EveryPanelHasAViewMenuEntryOnBothSurfaces()
    {
        var axaml = ReadRepoFile("src/Ui/Views/WorkspaceWindow.axaml");

        foreach (var id in DockPanelIds.All)
        {
            var occurrences = System.Text.RegularExpressions.Regex
                .Matches(axaml, $"CommandParameter=\"{id}\"").Count;
            Assert.True(occurrences >= 2,
                $"{id} needs an entry in BOTH the macOS NativeMenu and the in-window Menu (found {occurrences})");
        }
    }

    // ── Reported bug: a torn-off DOCUMENT outlived its workspace ─────────────

    /// <summary>
    /// Owner report: a torn-off document did not close with the workspace, so reopening that
    /// workspace showed the same file in two windows.
    ///
    /// <para>The fix narrows brief-foreign-documents.md R-fgn-2 exactly where it was wrong: a document
    /// whose file lives INSIDE the workspace is that workspace's own and closes with it (tear-off is
    /// presentation only, R-fgn-1), while a FOREIGN one — opened from outside via File ▸ Open —
    /// survives, which is what R-fgn-2 was actually protecting.</para>
    /// </summary>
    [Theory]
    [InlineData("Amp/schematic/Amp.csch",            true )]   // inside → the workspace's own
    [InlineData("results/Amp.cdd",                   true )]
    [InlineData("../OtherWorkspace/Other.csch",      false)]   // outside → foreign, survives
    public void ADocumentBelongsToTheWorkspaceOnlyWhenItsFileIsInside(string relative, bool expected)
    {
        var wsDir = Path.Combine(Path.GetTempPath(), "crf-ws-close");
        var abs   = Path.GetFullPath(Path.Combine(wsDir, relative));

        var doc = new SchematicDocument("Doc", NewSchematicViewModel());
        doc.Materialize(abs);

        Assert.Equal(expected, WorkspaceViewModel.BelongsToWorkspace(doc, wsDir));
    }

    [Fact]
    public void AScratchDocument_BelongsToNoWorkspace_AndSurvives()
    {
        // No path, so nothing makes it the workspace's — matches the pre-existing behaviour for
        // scratch tabs, which R-fgn-2 has always kept alive across a switch.
        var scratch = new SchematicDocument("Untitled-Schematic-1", NewSchematicViewModel());
        Assert.False(WorkspaceViewModel.BelongsToWorkspace(scratch, Path.GetTempPath()));
    }

    [Fact]
    public void EveryWorkspaceSwitchPath_ClosesTheFloatsItOwns()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

        // New Workspace, Open/switch workspace, and Close Workspace all leave a workspace behind.
        Assert.Equal(2, System.Text.RegularExpressions.Regex
            .Matches(src, @"CloseFloatedDocumentsOwnedByWorkspace\(CurrentWorkspacePath\)").Count);

        // ResetToBlankShell (Close Workspace) closes a floated document it owns rather than
        // unconditionally treating every float as a survivor.
        Assert.Contains("if (IsDockableDocked(dockable) || FloatedDocumentClosesWithWorkspace(dockable))", src);
    }

    /// <summary>
    /// The load-bearing pairing: anything a workspace switch will CLOSE must be something it first
    /// OFFERS TO SAVE. Both the dirty check and the save prompt use the same predicate as the close
    /// itself, so unsaved work in a torn-off document cannot vanish silently.
    /// </summary>
    [Fact]
    public void WhateverTheSwitchCloses_TheSwitchAlsoOffersToSave()
    {
        var src = ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

        var keepCount = System.Text.RegularExpressions.Regex.Matches(
            src,
            @"bool Keep\(IDockable d\) => includeFloated \|\| IsDockableDocked\(d\) \|\| FloatedDocumentClosesWithWorkspace\(d\);").Count;

        Assert.Equal(2, keepCount);   // HasAnyDirtyWork and PromptSaveBeforeClose
    }

    private static CircuitRF.Ui.ViewModels.SchematicViewModel NewSchematicViewModel() =>
        new(new CircuitRF.Ui.Schematic.SchematicEditModel());

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var full = Path.Combine(dir!.FullName, relativePath);
        Assert.True(File.Exists(full), $"expected repo file not found: {relativePath}");
        return File.ReadAllText(full);
    }
}
