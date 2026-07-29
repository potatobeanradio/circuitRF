using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate for brief-housekeeping-tearoff-palette-repo.md §1 (R-hk-1/R-hk-2): a torn-off `.cdd`
/// canvas must render the same background docked and torn off. Fix: <c>CrfHostWindow</c> (the
/// shared host every tear-off — document or tool, any editor — floats into) now sets the SAME
/// window-level <c>Background="{DynamicResource SystemChromeLowColor}"</c>
/// <c>WorkspaceWindow.axaml</c> itself uses, so a tear-off window can never resolve to a
/// different background than the docked shell.
///
/// <c>CrfHostWindow</c> is an Avalonia <c>Window</c> subclass and cannot be constructed headlessly
/// in this project's test suite (matches every prior "cannot pixel-verify a Window/dialog headlessly"
/// note in this codebase) — this is a source-level regression guard, not a pixel oracle.
/// </summary>
public class TearOffBackgroundTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    [Fact]
    public void CrfHostWindow_SetsTheSameBackgroundResource_WorkspaceWindowUses()
    {
        var hostSrc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "Dock", "CrfHostWindow.cs"));
        var shellSrc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml"));

        Assert.Contains("SystemChromeLowColor", hostSrc);
        Assert.Contains("BackgroundProperty", hostSrc);
        Assert.Contains("Background=\"{DynamicResource SystemChromeLowColor}\"", shellSrc);
    }

    [Fact]
    public void CrfHostWindow_IsTheSharedTearOffHost_UsedByBothDocumentAndToolFloats()
    {
        // Confirms this one fix covers every tear-off-capable editor (schematic/symbol/layout/data
        // display) and every tool panel — both host-window construction sites resolve to the SAME
        // CrfHostWindow type, per the class's own doc comment.
        var factorySrc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "ViewModels", "Dock", "CircuitRfDockFactory.cs"));
        var shellCodeBehindSrc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));

        Assert.Contains("new CrfHostWindow()", factorySrc);
        Assert.Contains("new CircuitRF.Ui.ViewModels.Dock.CrfHostWindow()", shellCodeBehindSrc);
    }
}
