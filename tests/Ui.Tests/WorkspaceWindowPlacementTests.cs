using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Views;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported, 2026-08-21: a new workspace window "appears lower on the screen than macOS" on
/// Windows and "the lower portion of the window is cutoff the screen".
///
/// <para>Placement is only checkable by opening the application on each platform and looking, which is
/// why the arithmetic lives in <see cref="WorkspaceWindowPlacement"/> and is asserted here against
/// synthetic screens instead — including the macOS display the owner calls "perfect" today, so the fix
/// for Windows is proven not to move that one.</para>
/// </summary>
public class WorkspaceWindowPlacementTests(ITestOutputHelper output)
{
    private const double DeclaredWidth  = 1200.0;
    private const double DeclaredHeight = 800.0;
    private const double MinWidth       = 800.0;
    private const double MinHeight      = 500.0;

    private static (double Width, double Height) Fit(double areaW, double areaH, double scaling) =>
        WorkspaceWindowPlacement.Fit(DeclaredWidth, DeclaredHeight, areaW, areaH, scaling, MinWidth, MinHeight);

    /// <summary>
    /// The macOS case the owner reports as already correct. Measured on the owner's own machine
    /// (2026-08-21, an Avalonia 12.0.3 probe): a 1920x1080 display reports its working area as
    /// 1920x996 <b>points</b> with <see cref="Avalonia.Platform.Screen.Scaling"/> <b>1</b> — not 2, even
    /// though the window's <c>RenderScaling</c> there is 2. The declared size fits, so nothing changes.
    /// </summary>
    [Fact]
    public void MacOsDisplay_OpensAtTheDeclaredSize()
    {
        var (w, h) = Fit(1920, 996, 1.0);
        output.WriteLine($"macOS 1920x996 points @ Scaling 1 -> {w}x{h}");

        Assert.Equal(DeclaredWidth,  w, 6);
        Assert.Equal(DeclaredHeight, h, 6);
    }

    /// <summary>
    /// The reported failure: the same physical display on Windows at 150% scaling is only 693 DIPs tall
    /// once the taskbar is gone, so the declared 800-DIP height overflows the screen by ~110 DIPs. That
    /// is the "lower portion cut off" — and centring alone would not fix it, it would only split the
    /// overflow between the top and the bottom.
    /// </summary>
    [Theory]
    [InlineData(1920, 1040, 1.50)]   // 1080p at 150%, taskbar removed
    [InlineData(1920, 1040, 1.25)]   // 1080p at 125%
    [InlineData(1366,  728, 1.00)]   // an unscaled 1366x768 laptop
    public void WindowsDisplay_ShrinksToFitTheWorkingArea(double areaW, double areaH, double scaling)
    {
        var (w, h) = Fit(areaW, areaH, scaling);
        output.WriteLine($"Windows {areaW}x{areaH} px @ Scaling {scaling} -> {w}x{h}");

        Assert.True(w * scaling <= areaW, $"width {w} DIPs still overflows a {areaW}px working area");
        Assert.True(h * scaling <= areaH, $"height {h} DIPs still overflows a {areaH}px working area");

        // It is a fit, not a shrink for its own sake: neither axis is reduced below what fits.
        Assert.True(w <= DeclaredWidth  && h <= DeclaredHeight);
        Assert.True(h >= MinHeight);
    }

    /// <summary>
    /// The 150% 1080p case in full, because it is the one the owner is looking at: 1040/1.5 = 693 DIPs
    /// of working area, less the edge margin.
    /// </summary>
    [Fact]
    public void The150PercentLaptop_LandsInsideTheWorkingAreaWithRoomToSpare()
    {
        var (w, h) = Fit(1920, 1040, 1.5);
        output.WriteLine($"1080p @ 150% -> {w}x{h} DIPs");

        // Width is untouched — 1920/1.5 = 1280 DIPs of room, and the declared 1200 already fits. Only
        // the axis that actually overflows is reduced.
        Assert.Equal(DeclaredWidth, w, 6);
        Assert.Equal(1040 / 1.5 - WorkspaceWindowPlacement.EdgeMargin, h, 6);   // ~645.3
        Assert.True(h < DeclaredHeight);
    }

    /// <summary>
    /// A display too small for the window's own <c>MinHeight</c> cannot honour both; Avalonia clamps
    /// back up to the minimum regardless, so returning anything smaller here would only be a lie about
    /// the size the window opens at.
    /// </summary>
    [Fact]
    public void AScreenSmallerThanTheMinimum_ReturnsTheMinimum()
    {
        var (w, h) = Fit(640, 400, 1.0);
        output.WriteLine($"640x400 -> {w}x{h}");

        Assert.Equal(MinWidth,  w, 6);
        Assert.Equal(MinHeight, h, 6);
    }

    /// <summary>
    /// No usable display information — which happens while a display is being attached and while a
    /// window is dragged between two. Guessing here would produce a permanently tiny window, which is
    /// worse than the bug being fixed, so the declared size is returned untouched.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void NoScreenInformation_LeavesTheDeclaredSizeAlone(double areaW, double areaH)
    {
        var (w, h) = Fit(areaW, areaH, 1.0);
        Assert.Equal(DeclaredWidth,  w, 6);
        Assert.Equal(DeclaredHeight, h, 6);
    }

    /// <summary>A scaling the platform could not report is read as 1, never as a divide-by-zero.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void NonPositiveScaling_IsReadAsOne(double scaling)
    {
        var (w, h) = Fit(1920, 996, scaling);
        Assert.Equal(DeclaredWidth,  w, 6);
        Assert.Equal(DeclaredHeight, h, 6);
    }

    // ── Wiring ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The window must ASK to be centred. With no <c>WindowStartupLocation</c> it takes the default —
    /// <c>Manual</c> with no <c>Position</c> — and the OS places it: macOS cascades within the visible
    /// frame, Win32's <c>CW_USEDEFAULT</c> cascades down-and-right off the top-left without caring
    /// whether the result fits. That difference IS the "appears lower on the screen" half of the report.
    /// </summary>
    [Fact]
    public void TheWorkspaceWindow_AsksToBeCentred()
    {
        var xaml = Read("src/Ui/Views/WorkspaceWindow.axaml");
        Assert.Contains("WindowStartupLocation=\"CenterScreen\"", xaml, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The fit is applied in the CONSTRUCTOR. <c>CenterScreen</c> is applied by <c>Show()</c> off the
    /// size the window has by then, so resizing in <c>OnOpened</c> would centre the OLD size and leave
    /// the window visibly off-centre.
    /// </summary>
    [Fact]
    public void TheFit_RunsBeforeTheWindowIsShown()
    {
        var cs  = Read("src/Ui/Views/WorkspaceWindow.axaml.cs");
        var ctor = Body(cs, "public WorkspaceWindow()");
        Assert.Contains("FitToScreen();", ctor, System.StringComparison.Ordinal);
        Assert.DoesNotContain("FitToScreen", Body(cs, "protected override void OnOpened("),
                              System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Screen.Scaling, never the window's RenderScaling.</b> A working area is in physical pixels on
    /// Windows and in points on macOS; converting a DIP size with <c>RenderScaling</c> (2 on Retina)
    /// doubles it against an area that was never scaled — the exact bug that pinned the Match Designer
    /// to the screen corner (see <c>MatchWindowPlacement</c>), and here it would halve the window on
    /// every Retina Mac.
    /// </summary>
    [Fact]
    public void TheFit_UsesTheScreensScaling_NotTheWindowsRenderScaling()
    {
        var body = Body(Read("src/Ui/Views/WorkspaceWindow.axaml.cs"), "private void FitToScreen()");
        Assert.Contains("screen.Scaling", body, System.StringComparison.Ordinal);
        Assert.DoesNotContain("RenderScaling", body, System.StringComparison.Ordinal);
        Assert.Contains("WorkingArea", body, System.StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Source text from <paramref name="signature"/> to the end of its brace-balanced body.</summary>
    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, System.StringComparison.Ordinal);
        if (start < 0) return "";
        int open = source.IndexOf('{', start);
        if (open < 0) return "";

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[start..(i + 1)];
        }
        return source[start..];
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }
}
