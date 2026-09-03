using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// MW1 §9 — the shell half: per-window panels, float ownership, and the source-level rules that keep
/// a workspace-lifetime path from reaching a process-wide clear.
///
/// <para>Source scans are used where the behaviour needs a running Avalonia application to observe.
/// They are the same shape <c>HarmonicaStandaloneTests</c> already uses, and they strip comments
/// first — a rule that only matched because the sentence NAMING it was in a comment is not a rule
/// (project-brief-harmonicarf-h8).</para>
/// </summary>
public sealed class MultiWorkspaceShellTests
{
    // ── Two view models, two of everything (R-mw-2) ───────────────────────────

    /// <summary>
    /// Every workspace window owns its own panels and its own dock arrangement. This falls out of the
    /// factory being per view model, which is exactly why MW1's shell half cost so little — but it is
    /// the assumption the whole feature rests on, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void TwoWorkspaces_HaveDisjointDockFactoriesAndToolInstances()
    {
        var a = new WorkspaceViewModel();
        var b = new WorkspaceViewModel();

        Assert.NotSame(a.Factory, b.Factory);
        Assert.NotSame(a.Factory.PaletteTool,     b.Factory.PaletteTool);
        Assert.NotSame(a.Factory.ProjectTreeTool, b.Factory.ProjectTreeTool);
        Assert.NotSame(a.Factory.PropertiesTool,  b.Factory.PropertiesTool);
        Assert.NotSame(a.Factory.MessagesTool,    b.Factory.MessagesTool);
        Assert.NotSame(a.Layout,                  b.Layout);
        Assert.NotSame(a.Messages,                b.Messages);
    }

    /// <summary>
    /// Every float is stamped with the workspace whose factory created it (R-mw1-11) — the fact the
    /// Window menu, the close prompt and the macOS menu attachment all read. Ownership is never
    /// inferred from position, z-order or title, so this is the one thing that has to hold.
    /// </summary>
    [Fact]
    public void AFloatIsStampedWithTheWorkspaceThatCreatedIt()
    {
        var a = new WorkspaceViewModel();
        var b = new WorkspaceViewModel();

        // The factory knows whose it is, and stamps every host window it builds with that. The
        // window itself cannot be constructed headlessly (it needs a real windowing platform), so
        // the stamp SOURCE is asserted here and the one line that applies it is asserted below.
        Assert.Same(a, a.Factory.Owner);
        Assert.Same(b, b.Factory.Owner);
        Assert.NotSame(a.Factory.Owner, b.Factory.Owner);

        Assert.Contains("new CrfHostWindow { OwningWorkspace = Owner }",
                        Strip(Read("src", "Ui", "ViewModels", "Dock", "CircuitRfDockFactory.cs")),
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// Ownership is the stamp, never a guess. The Window menu, the close prompt and the macOS menu
    /// attachment all read it — inferring it from position, z-order or title would sometimes
    /// attribute a panel to the wrong workspace, and every one of those consequences is silent.
    /// </summary>
    [Fact]
    public void TheWindowMenuListsOnlyItsOwnFloats()
    {
        string body = Strip(BodyOf(Read("src", "Ui", "ViewModels", "WorkspaceViewModel.WindowMenu.cs"),
                                   "public IReadOnlyList<WindowMenuEntry> EnumerateWindowEntries()"));

        Assert.Contains("ReferenceEquals(w.OwningWorkspace, this)", body, StringComparison.Ordinal);
        // …and offers the OTHER workspace windows as a band of their own (R-mw1-12).
        Assert.Contains("OfType<Views.WorkspaceWindow>()", body, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(w.DataContext, this)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// MW1 §9.2 — one window resetting its layout leaves the other's arrangement byte-identical.
    ///
    /// <para>Reset Layout is per window because the dock factory is (R-mw-2): it resets the window it
    /// was invoked from and touches no other. The DEFAULT it resets TO stays one application
    /// preference, which is why both windows resetting to the same thing is correct.</para>
    /// </summary>
    [Fact]
    public void ResettingOneWorkspacesLayout_LeavesTheOthersCapturedArrangementIdentical()
    {
        var a = new WorkspaceViewModel();
        var b = new WorkspaceViewModel();

        string beforeA = CaptureJson(a);
        string beforeB = CaptureJson(b);
        Assert.Equal(beforeA, beforeB);            // they start identical, which is what makes this a test

        // A's reset, through the same path View ▸ Reset Layout takes — ProjectTreeAndLibrary is the
        // one preset that REBUILDS the dock rather than only re-selecting a tab.
        a.ApplyWindowLayout(CircuitRF.Ui.Theming.WindowLayout.ProjectTreeAndLibrary);

        Assert.NotEqual(beforeA, CaptureJson(a));  // A's own really did change…
        Assert.Equal(beforeB, CaptureJson(b));     // …and B's is byte-identical
    }

    private static string CaptureJson(WorkspaceViewModel vm)
    {
        var captured = CircuitRF.Ui.Docking.DockLayoutCapture.Capture(
            (Dock.Model.Controls.IRootDock)vm.Layout!,
            screens: []);
        return System.Text.Json.JsonSerializer.Serialize(captured);
    }

    // ── Preferences: two windows, two different fields (R-mw1-8) ──────────────

    /// <summary>
    /// Load → mutate one field → save loses the other window's edit. Every workspace open touches
    /// Recent Workspaces, so with two windows that stops being theoretical.
    /// </summary>
    [Fact]
    public void TwoWindowsEachChangingADifferentPreference_BothSurvive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-mw1-prefs-" + Guid.NewGuid().ToString("N")[..8]);
        string? previous = CircuitRF.Ui.AppDataRoot.IsRedirected ? CircuitRF.Ui.AppDataRoot.Dir : null;
        try
        {
            Directory.CreateDirectory(dir);
            CircuitRF.Ui.AppDataRoot.RedirectTo(dir);

            // Window A reads the preferences, and only THEN does window B change one — the interleave
            // that used to lose an edit, because A's stale snapshot was written back over it.
            var snapshotHeldByWindowA = CircuitRF.Ui.Theming.AppPreferencesIo.Load();
            CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.ShowDockersOnLaunch = false);

            Assert.NotNull(snapshotHeldByWindowA);
            CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.WindowLayout = CircuitRF.Ui.Theming.WindowLayout.ProjectTreeAndLibrary);

            var now = CircuitRF.Ui.Theming.AppPreferencesIo.Load();
            Assert.False(now.ShowDockersOnLaunch);
            Assert.Equal(CircuitRF.Ui.Theming.WindowLayout.ProjectTreeAndLibrary, now.WindowLayout);
        }
        finally
        {
            CircuitRF.Ui.AppDataRoot.RedirectTo(previous);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>A snapshot is a snapshot: poking one must not change what anyone else reads.</summary>
    [Fact]
    public void MutatingALoadedSnapshotWithoutSaving_ChangesNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-mw1-prefs-" + Guid.NewGuid().ToString("N")[..8]);
        string? previous = CircuitRF.Ui.AppDataRoot.IsRedirected ? CircuitRF.Ui.AppDataRoot.Dir : null;
        try
        {
            Directory.CreateDirectory(dir);
            CircuitRF.Ui.AppDataRoot.RedirectTo(dir);

            CircuitRF.Ui.Theming.AppPreferencesIo.Update(p => p.ShowDockersOnLaunch = true);

            var snapshot = CircuitRF.Ui.Theming.AppPreferencesIo.Load();
            snapshot.ShowDockersOnLaunch = false;

            Assert.True(CircuitRF.Ui.Theming.AppPreferencesIo.Load().ShowDockersOnLaunch);
        }
        finally
        {
            CircuitRF.Ui.AppDataRoot.RedirectTo(previous);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── The source-level rules (R-mw1-4, §9.6) ────────────────────────────────

    /// <summary>
    /// The registries expose no process-wide clear a workspace-lifetime path could reach. This is the
    /// rule that would have prevented the original defect: a workspace OPEN calling
    /// <c>Clear()</c> on state shared by every window.
    /// </summary>
    [Fact]
    public void TheKitRegistriesExposeNoProcessWideClear()
    {
        string kits = Strip(Read("src", "Ui", "Schematic", "PdkKitRegistry.cs"));
        string gens = Strip(Read("src", "Ui", "Schematic", "KitLayoutGenerators.cs"));

        foreach (string source in new[] { kits, gens })
        {
            Assert.DoesNotContain("public static void Clear()", source, StringComparison.Ordinal);
            Assert.Contains("ClearWorkspace(", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// No workspace-lifetime path clears a registry process-wide. Scanned over the two methods that
    /// actually run at every workspace open and close, because that is where the defect lived — a
    /// blanket file scan would also catch the legitimate process-exit handlers in <c>App</c>.
    /// </summary>
    [Theory]
    [InlineData("private void RestoreInstalledPdks()")]
    [InlineData("private void ResetPCellGenerators(")]
    [InlineData("private void RegisterKitProviderResolver(")]
    [InlineData("public void OnCleanExit()")]
    public void NoWorkspaceLifetimePathClearsARegistryProcessWide(string signature)
    {
        string body = Strip(BodyOf(Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"), signature));

        Assert.DoesNotContain("PdkKitRegistry.Clear()",           body, StringComparison.Ordinal);
        Assert.DoesNotContain("KitLayoutGenerators.Clear()",      body, StringComparison.Ordinal);
        Assert.DoesNotContain("PCellRegistry.ClearResolvers()",   body, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalDeviceRegistry.ResetResolved()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalDeviceRegistry.Clear()",   body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace window is created in exactly ONE place (R-mw1-2). A fourth construction site added
    /// later is how the layout preference gets silently skipped for one route — that has already
    /// happened once.
    /// </summary>
    [Fact]
    public void AWorkspaceWindowIsConstructedInExactlyOnePlace()
    {
        var sources = new[]
        {
            Strip(Read("src", "Ui", "App.axaml.cs")),
            Strip(Read("src", "Ui", "Views", "WorkspaceWindow.axaml.cs")),
            Strip(Read("src", "Ui", "ViewModels", "WorkspaceViewModel.cs")),
        };

        int constructions = sources.Sum(s => Regex.Matches(s, @"new\s+WorkspaceWindow\s*[{(]").Count);
        Assert.Equal(1, constructions);
    }

    /// <summary>
    /// The nine "find the workspace" lookups are gone: nothing outside <c>WorkspaceLocator</c> and
    /// <c>App</c>'s own window bookkeeping asks for "the first workspace window in the process".
    /// Under two windows that answers with an arbitrary one, so a command invoked in window B runs
    /// against window A.
    /// </summary>
    [Fact]
    public void NoViewResolvesTheWorkspaceByTakingTheFirstWindowInTheProcess()
    {
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(RepoDir("src", "Ui", "Views"), "*.cs",
                                                         SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "WorkspaceLocator.cs") continue;
            string body = Strip(File.ReadAllText(file));
            if (Regex.IsMatch(body, @"OfType<(Views\.)?WorkspaceWindow>\(\)"))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.Empty(offenders);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string RepoDir(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine([dir, .. parts]);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(string.Join('/', parts) + " was not found above the test output.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(RepoDir(parts));

    /// <summary>
    /// Comments removed before scanning. A rule that only matched because the sentence NAMING it was
    /// in a comment is not a rule — recorded in project-brief-harmonicarf-h8.
    /// </summary>
    private static string Strip(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\n]*", "");
    }

    private static string BodyOf(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is no longer there — update this test.");

        int open = source.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }
}
