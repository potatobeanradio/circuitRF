using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Tools ▸ harmonicaRF opens in its OWN window, sized like the shell and offset down-right so the
/// workspace's title bar stays visible (owner, 2026-08-19) — and the two menu commands that were
/// silently dead outside a workspace.
///
/// <para><b>The placement arithmetic is tested against SYNTHETIC screens</b>, the same way
/// <see cref="ScreenPlacementTests"/> is, so the whole thing is exercised with no display attached.
/// The wiring — which is a <c>WorkspaceViewModel</c> nothing in this suite constructs — is held by
/// source assertions, the pattern this suite already uses for view-model behaviour it cannot
/// instantiate.</para>
/// </summary>
public sealed class HarmonicaOwnWindowTests
{
    private static readonly ScreenRect Fhd = new(0, 0, 1920, 1040);   // 1080 minus a 40px taskbar
    private static readonly List<ScreenRect> SingleScreen = [Fhd];

    private const double Offset = ScreenPlacement.TitleBarHeight;

    // ── The trim, which is the whole point ───────────────────────────────────

    /// <summary>
    /// <b>A maximized shell is the case that decides the design.</b> Its offset copy overhangs the
    /// bottom-right by exactly the offset, and the obvious repair — clamping the POSITION, which is
    /// what <see cref="ScreenPlacement.Place"/> does — slides the new window straight back onto the
    /// shell's own corner and hides the title bar the offset existed to leave showing. Trimming the
    /// size instead keeps the corner where it was put.
    /// </summary>
    [Fact]
    public void AMaximizedShell_TrimsTheNewWindow_RatherThanSlidingItBack()
    {
        // The shell fills the working area; the instrument wants the same size, offset.
        var wanted = new ScreenRect(Fhd.X + Offset, Fhd.Y + Offset, Fhd.Width, Fhd.Height);

        var trimmed = WorkspaceViewModel.TrimToScreen(wanted, SingleScreen);

        // The corner is untouched — that is the requirement.
        Assert.Equal(Fhd.X + Offset, trimmed.X);
        Assert.Equal(Fhd.Y + Offset, trimmed.Y);

        // …and it gave up exactly the offset in each dimension to stay on screen.
        Assert.Equal(Fhd.Width  - Offset, trimmed.Width);
        Assert.Equal(Fhd.Height - Offset, trimmed.Height);
        Assert.Equal(Fhd.Right,  trimmed.Right);
        Assert.Equal(Fhd.Bottom, trimmed.Bottom);
    }

    /// <summary>
    /// The trimmed rectangle then survives <see cref="ScreenPlacement.Place"/> <b>byte-identical</b>.
    /// That is what makes the trim the placement rather than a suggestion: Place returns an already
    /// reachable window exactly as given (its own gate 11), so the safety net never fires and the
    /// offset is never quietly undone.
    /// </summary>
    [Fact]
    public void TheTrimmedRectangle_SurvivesThePlacer_Unchanged()
    {
        var wanted  = new ScreenRect(Fhd.X + Offset, Fhd.Y + Offset, Fhd.Width, Fhd.Height);
        var trimmed = WorkspaceViewModel.TrimToScreen(wanted, SingleScreen);

        var placed = ScreenPlacement.Place(trimmed, SingleScreen, [], sameConfiguration: false);

        Assert.Equal(trimmed, placed);
    }

    /// <summary>
    /// The untrimmed rectangle does NOT survive it — which is the negative half of the test above and
    /// the reason <c>TrimToScreen</c> has to exist at all. Without it the window comes back at the
    /// shell's own corner with the shell's title bar hidden behind it.
    /// </summary>
    [Fact]
    public void WithoutTheTrim_ThePlacerSlidesItBackOntoTheShellsCorner()
    {
        var wanted = new ScreenRect(Fhd.X + Offset, Fhd.Y + Offset, Fhd.Width, Fhd.Height);

        var placed = ScreenPlacement.Place(wanted, SingleScreen, [], sameConfiguration: false);

        Assert.NotEqual(wanted, placed);
        Assert.Equal(Fhd.X, placed.X);      // slid back — the offset is gone
        Assert.Equal(Fhd.Y, placed.Y);
    }

    /// <summary>A shell that does NOT fill the screen keeps its full size — the trim only ever gives
    /// up what it must, so an ordinary windowed shell produces an exact-size copy.</summary>
    [Fact]
    public void AWindowedShell_KeepsTheFullSize()
    {
        var wanted = new ScreenRect(200 + Offset, 100 + Offset, 1200, 800);

        Assert.Equal(wanted, WorkspaceViewModel.TrimToScreen(wanted, SingleScreen));
    }

    /// <summary>
    /// The trim never produces something unusably small, however far into the corner the shell is —
    /// <see cref="ScreenPlacement.MinWindowSize"/> is the floor, and <c>Place</c> takes it from there.
    /// </summary>
    [Fact]
    public void AShellInTheFarCorner_StillYieldsAUsableSize()
    {
        var wanted = new ScreenRect(Fhd.Right - 10, Fhd.Bottom - 10, 1400, 900);

        var trimmed = WorkspaceViewModel.TrimToScreen(wanted, SingleScreen);

        Assert.Equal(ScreenPlacement.MinWindowSize, trimmed.Width);
        Assert.Equal(ScreenPlacement.MinWindowSize, trimmed.Height);
    }

    /// <summary>
    /// With no screen information there is nothing to trim against, so the rectangle is returned
    /// untouched rather than guessed at — the same "never relocate blind" rule
    /// <see cref="ScreenPlacement.Place"/> follows.
    /// </summary>
    [Fact]
    public void WithNoScreens_NothingIsTrimmed()
    {
        var wanted = new ScreenRect(40, 40, 4000, 3000);

        Assert.Equal(wanted, WorkspaceViewModel.TrimToScreen(wanted, []));
    }

    /// <summary>
    /// Multi-monitor: the trim uses the screen the top-left corner is actually ON, not the first one
    /// in the list. A shell on the second monitor trimmed against the first would come back the wrong
    /// size for no visible reason.
    /// </summary>
    [Fact]
    public void TheTrimUsesTheScreenTheCornerIsOn()
    {
        var second = new ScreenRect(1920, 0, 2560, 1400);
        List<ScreenRect> two = [Fhd, second];

        var wanted = new ScreenRect(1920 + Offset, Offset, 2560, 1400);

        var trimmed = WorkspaceViewModel.TrimToScreen(wanted, two);

        Assert.Equal(second.Right,  trimmed.Right);
        Assert.Equal(second.Bottom, trimmed.Bottom);
    }

    /// <summary>
    /// The offset IS the title-bar height, and that is load-bearing rather than a taste: the
    /// requirement is that the workspace's title bar stays visible, and the new window's frame top
    /// lands exactly at the bottom of the shell's title bar at this value. A smaller number covers
    /// part of the thing the offset exists to leave showing.
    /// </summary>
    [Fact]
    public void TheOffsetIsAtLeastATitleBarHigh()
        => Assert.True(Offset >= ScreenPlacement.TitleBarHeight);

    // ── The wiring ───────────────────────────────────────────────────────────

    /// <summary>
    /// Tools ▸ harmonicaRF opens the document and then takes it straight back out into its own
    /// window. Asserted on the source because nothing in this suite constructs a
    /// <c>WorkspaceViewModel</c> (it needs a live Dock factory and a shell window); the same pattern
    /// <c>NewWorkspaceTechnologyPickerTests</c> uses.
    /// </summary>
    [Fact]
    public void NewHarmonica_FloatsTheDocumentIntoItsOwnWindow()
    {
        var body = MethodBody(
            ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs")),
            "private void NewHarmonica()");

        Assert.Contains("OpenDocumentInOwnWindow(doc)", body, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The float reuses the drag tear-off path.</b> Hand-assembling a window model instead is what
    /// produced the "a floating panel cannot be re-docked" bug recorded on
    /// <c>CircuitRfDockFactory.FloatTool</c>; going through <c>SplitToWindow</c> is what makes this
    /// window behave exactly like one the user tore off themselves.
    /// </summary>
    [Fact]
    public void TheFloatGoesThroughSplitToWindow_NotAHandBuiltWindow()
    {
        var body = MethodBody(
            ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.Docking.cs")),
            "internal bool OpenDocumentInOwnWindow(IDockable document)");

        Assert.Contains("_factory.SplitToWindow(", body, System.StringComparison.Ordinal);

        // The two deferred posts are not optional: a PROGRAMMATIC float does not go through
        // OnDocumentDockPropertyChanged, so without them the new window shows "Close Workspace"
        // instead of "Close Window" and, on macOS, no menu bar at all.
        Assert.Contains("TryWireHostWindowsUndo", body, System.StringComparison.Ordinal);
        Assert.Contains("TryWireWindowFocusTracking", body, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>File ▸ New must work with no workspace</b> (owner, 2026-08-19). It used to be the workspace
    /// command and nothing else, so in the standalone harmonicaRF binary — which has no
    /// <c>WorkspaceViewModel</c> anywhere — New was a silent no-op: enabled, clicked, nothing.
    /// </summary>
    [Fact]
    public void HarmonicaNew_OpensAWindow_WhenThereIsNoWorkspace()
    {
        var body = MethodBody(
            ReadRepoFile(Path.Combine("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs")),
            "private void NewDocument()");

        Assert.Contains("NewHarmonicaCommand", body, System.StringComparison.Ordinal);
        Assert.Contains("new HarmonicaShellWindow().Show()", body, System.StringComparison.Ordinal);
    }

    /// <summary>File ▸ Close had the identical hole — no factory to ask, so it did nothing.</summary>
    [Fact]
    public void HarmonicaClose_ClosesTheWindow_WhenThereIsNoWorkspace()
    {
        var body = MethodBody(
            ReadRepoFile(Path.Combine("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs")),
            "private void CloseDocument()");

        Assert.Contains("CloseDockable", body, System.StringComparison.Ordinal);
        Assert.Contains(".Close()", body, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A dockable in a FLOATING window sits in a <c>CrfHostWindow</c> whose DataContext Dock sets to
    /// the <c>IDockWindow</c> — not to the workspace. Reading only the top level therefore returned
    /// null for a torn-off instrument, silently killing New, Close and the saved-file notification.
    /// Floating is the DEFAULT now, so the fallback is mandatory rather than a nicety.
    /// </summary>
    [Fact]
    public void HarmonicaViewsWorkspace_FallsBackToTheShellWindow()
    {
        var source = ReadRepoFile(Path.Combine("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs"));
        var body   = MethodBody(source, "private ViewModels.WorkspaceViewModel? Workspace");

        Assert.Contains("OfType<Views.WorkspaceWindow>()", body, System.StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// One member's own source, from its signature to its closing brace — brace-matched rather than
    /// a fixed window, so a method that grows a paragraph of comment does not silently start
    /// returning someone else's code (or, worse, stop returning its own).
    ///
    /// <para>An expression-bodied member has no braces; for those the text runs to the terminating
    /// semicolon.</para>
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' was not found — it has been renamed or removed.");

        int brace = source.IndexOf('{', start);
        int semi  = source.IndexOf(';', start);

        // Expression-bodied: the semicolon arrives before any brace.
        if (brace < 0 || (semi >= 0 && semi < brace)) return source[start..(semi + 1)];

        int depth = 0;
        for (int i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"'{signature}' has no matching closing brace.");
        return "";
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }
}
