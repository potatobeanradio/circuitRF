using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// Two owner reports, 2026-08-12, both about a DETACHED document window:
/// <list type="bullet">
/// <item>"The EM Setup .cem file is not saved using the keyboard shortcut when the document is
/// detached from the dock. (It does save when it's docked.)"</item>
/// <item>"Pressing Fit Windows to Frame in the Workspace toolbar creates a duplicate document window
/// for any documents that are detached from the dock."</item>
/// </list>
/// </summary>
public sealed class EmSetupWindowFixesTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    // ── Ctrl+S in a detached .cem window ─────────────────────────────────────────────────────

    [Fact]
    public void TheEmSetupView_BindsSaveItself_SoADetachedWindowCanSave()
    {
        // Ctrl/Meta+S was bound ONLY on WorkspaceWindow. A torn-off document lives in its own
        // CrfHostWindow, into which WorkspaceViewModel.WireWindowUndo injects undo/redo and nothing
        // else — so a detached .cem had no Save binding at all, which is exactly what was reported.
        //
        // A source scan because this view cannot be constructed without an Avalonia app host, the same
        // fallback this suite already uses for every other view-level wiring claim.
        string xaml = File.ReadAllText(RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml"));

        int block = xaml.IndexOf("<UserControl.KeyBindings>", StringComparison.Ordinal);
        Assert.True(block > 0, "EmSetupEditorView declares no KeyBindings — a detached .cem cannot save");
        int end = xaml.IndexOf("</UserControl.KeyBindings>", block, StringComparison.Ordinal);
        string bindings = xaml[block..end];

        // Both modifiers: Ctrl for Windows/Linux, Meta for macOS.
        foreach (string gesture in (string[])["Ctrl+S", "Meta+S"])
            Assert.Contains($"Gesture=\"{gesture}\"", bindings, StringComparison.Ordinal);

        // Bound to the document's OWN command, not SaveAllDocuments — a floating window must never
        // have to resolve "which document is active" to save the one it is showing.
        Assert.Contains("{Binding ViewModel.SaveCommand}", bindings, StringComparison.Ordinal);
    }

    // ── Fit Windows to Frame duplicating a detached document window ──────────────────────────

    /// <summary>
    /// Minimal <see cref="IHostWindow"/> stand-in. The real one is a <c>Window</c> and needs an
    /// Avalonia app host; nothing here calls anything but identity, so a stub is enough to prove which
    /// host a rebuilt layout ends up pointing at — which IS the bug.
    /// </summary>
    private sealed class StubHost : IHostWindow
    {
        public IHostWindowState? HostWindowState { get; set; }
        public bool IsTracked { get; set; }
        public IDockWindow? Window { get; set; }
        public void Present(bool isDialog) { }
        public void Exit() { }
        public void SetPosition(double x, double y) { }
        public void GetPosition(out double x, out double y) { x = 0; y = 0; }
        public void SetSize(double width, double height) { }
        public void GetSize(out double width, out double height) { width = 0; height = 0; }
        public void SetTitle(string? title) { }
        public void SetLayout(IDock layout) { }
        public void SetWindowState(DockWindowState state) { }
        public DockWindowState GetWindowState() => DockWindowState.Normal;
        public void SetActive() { }
    }

    [Fact]
    public void InitDockWindow_KeepsAnAlreadyPresentedHost_InsteadOfBuildingASecond()
    {
        // Confirmed against decompiled Dock 12.0.0.2: FactoryBase.InitLayout walks rootDock.Windows
        // calling the two-argument InitDockWindow, whose base implementation resolves a host through
        // GetHostWindow UNCONDITIONALLY — i.e. `new CrfHostWindow()` — and then assigns it over
        // whatever was there. CarryOverDocumentWindows deliberately moves a PRESENTED float onto the
        // new root with its live host intact (that is what keeps torn-off documents open across a
        // rebuild), so the assignment orphans the on-screen window and ShowWindows then presents the
        // replacement: the duplicate.
        var factory = new CircuitRfDockFactory();
        var host = new StubHost();
        var window = factory.CreateDockWindow();
        Assert.NotNull(window);
        window!.Host = host;

        factory.InitDockWindow(window!, owner: null);

        Assert.Same(host, window!.Host);
        Assert.Same(window, host.Window);   // and the two are still linked to each other
    }

    [Fact]
    public void InitDockWindow_StillBuildsAHost_WhenTheWindowHasNoneYet()
    {
        // The control. A freshly deserialized layout's windows carry no host, and that is precisely
        // the case the locator exists for — the fix must not swallow it.
        var factory = new CircuitRfDockFactory();
        var window = factory.CreateDockWindow();
        Assert.NotNull(window);
        Assert.Null(window!.Host);

        bool locatorRan = false;
        factory.DefaultHostWindowLocator = () => { locatorRan = true; return new StubHost(); };

        factory.InitDockWindow(window!, owner: null);

        Assert.True(locatorRan, "a window with no host must still get one built");
        Assert.NotNull(window!.Host);
    }
}
