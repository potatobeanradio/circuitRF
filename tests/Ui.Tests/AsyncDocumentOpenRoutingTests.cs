using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Every route that opens a .clay or a .cws must reach the ASYNCHRONOUS open.
//
//  Owner report, 2026-09-04: a workspace whose .cws restores a large .clay showed no docked windows
//  and no indication of anything happening, because the read ran inline on the UI thread. The read
//  now runs on a background thread behind a cancellable progress row — but that is only true of the
//  paths that actually call the async entry point, and there are seven of them: the file picker, the
//  project tree (double-click and the cell-view route), push-in, an import's own "open what I just
//  made", and the operating system's double-click, which arrives three different ways.
//
//  Source-scanned rather than driven, for the same reason the .plist/.wxs/mime parity tests in this
//  suite are: the failure is a call site quietly left on the synchronous overload, which no
//  behavioural test would notice — it still opens the document, just with the window frozen.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class AsyncDocumentOpenRoutingTests
{
    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── The operating system's double-click ────────────────────────────────────────────────────
    //
    // All four arrival paths (argv on Windows/Linux, the macOS Apple Event, the Windows named pipe
    // and the Linux socket, plus a new window's launch workspace) funnel into App.OpenFiles — that
    // is the property this pins, because a fifth path added later that does NOT funnel there would
    // bypass both of the routing rules below.

    [Fact]
    public void EveryOperatingSystemOpenRoute_FunnelsThroughOpenFiles()
    {
        var app = Read(Path.Combine("src", "Ui", "App.axaml.cs"));

        // The forwarded-files handler (Windows pipe / Linux socket, via Program.HandleExternalFiles)
        // and the Apple Event / argv handler both dispatch through the one funnel.
        Assert.Contains("HandleFilesInternal", app);
        Assert.Contains("OpenFiles(vm, paths)", app);

        // …and the funnel routes a .cws through the AWAITED workspace open, so the async restore
        // inside it actually runs to completion before the documents named alongside it are opened.
        Assert.Contains("await vm.OpenWorkspacePathAsync(cwsPath)", app);

        // …and every other document type through OpenDocumentByPath, which is where .clay is routed.
        Assert.Contains("vm.OpenDocumentByPath(doc)", app);
    }

    // ── .clay, wherever it is opened from ──────────────────────────────────────────────────────

    [Fact]
    public void OpenDocumentByPath_RoutesAClayThroughTheAsynchronousOpen()
    {
        var vm = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        // The operating system's double-click and the Project Tree both land here.
        Assert.Contains("case \".clay\":  _ = OpenOrActivateLayoutAsync(abs); return true;", vm);
    }

    /// <summary>
    /// The synchronous overload still exists and is still correct — it is what an already-read model
    /// is handed to. What must not happen is a USER-INITIATED open reaching it, because that one pays
    /// the read on the UI thread.
    ///
    /// <para>Two call sites are deliberate exceptions and are named here rather than left to be
    /// rediscovered: both need the opened document to exist by the time the next line runs (the EM
    /// mesh push writes into it; Generate Layout reads its view model back). Both are reached only
    /// when the layout is not already open, and both are documented at the call site.</para>
    /// </summary>
    [Fact]
    public void NoUserInitiatedLayoutOpen_StillUsesTheSynchronousOverload()
    {
        var vm = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        var callSites = System.Text.RegularExpressions.Regex
            .Matches(vm, @"[^A-Za-z]OpenOrActivateLayout\(")
            .Count;

        // The declaration, the two documented exceptions, the preloaded-model hand-off from
        // OpenPrimaryLayoutIfResolvableAsync, the already-in-memory shortcut and the tail of
        // OpenOrActivateLayoutAsync itself, plus the restore loop's own preloaded open.
        Assert.True(callSites <= 7,
            $"{callSites} synchronous OpenOrActivateLayout call sites — a new one has appeared. "
            + "A user-initiated open must call OpenOrActivateLayoutAsync; see this test's summary "
            + "for the two exceptions that may not.");

        // The project tree's two routes and push-in are async, specifically.
        Assert.Contains("_ = OpenOrActivateLayoutAsync(node.AbsolutePath); return; }", vm);
        Assert.Contains("else if (viewType == ViewType.Layout)  _ = OpenOrActivateLayoutAsync(path);", vm);
        Assert.Contains("await OpenOrActivateLayoutAsync(result[0].Path.LocalPath);", vm);
    }

    // ── .cws, wherever it is opened from ───────────────────────────────────────────────────────

    [Fact]
    public void EveryWorkspaceOpenRoute_AwaitsTheSwitch()
    {
        var vm = Read(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains("private async Task SwitchToWorkspace(string cwsPath)", vm);

        // Every call site awaits it. An un-awaited one would return before the layouts are read and
        // let the caller act on a workspace that is not finished opening.
        var calls = System.Text.RegularExpressions.Regex.Matches(vm, @"SwitchToWorkspace\(\w");
        foreach (System.Text.RegularExpressions.Match call in calls)
        {
            int lineStart = vm.LastIndexOf('\n', call.Index) + 1;
            string line = vm[lineStart..vm.IndexOf('\n', call.Index)];
            if (line.Contains("private async Task")) continue;   // the declaration
            Assert.Contains("await SwitchToWorkspace(", line);
        }

        // …and the restore inside it reads the layouts off the UI thread first.
        Assert.Contains("await PreloadRestoredLayoutsAsync(docs, workspaceDir)", vm);
    }
}
