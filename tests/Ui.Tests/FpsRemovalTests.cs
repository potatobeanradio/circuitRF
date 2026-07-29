using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate for brief-cell-first-and-ui-fixes.md §5 (R-cc-6): the FPS readout is gone from the
/// schematic canvas overlay AND the toolbar — plumbing included, not just the drawing.</summary>
public class FpsRemovalTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return dir!;
    }

    [Theory]
    [InlineData("src/Ui/Renderers/SchematicRenderer.cs")]
    [InlineData("src/Ui/Controls/SchematicCanvas.cs")]
    [InlineData("src/Ui/Views/Content/SchematicView.axaml.cs")]
    [InlineData("src/Ui/Views/Content/SchematicView.axaml")]
    public void NoFpsPlumbingRemainsInTheSchematicPath(string relativePath)
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.DoesNotContain("ShowFps", src);
        Assert.DoesNotContain("showFps", src);
        Assert.DoesNotContain("FpsText", src);
        Assert.DoesNotContain("LastFrameTicks", src);
        Assert.DoesNotContain("DrawFpsOverlay", src);
        Assert.DoesNotContain("previousFrameTicks", src);
    }

    [Fact]
    public void SchematicClipboard_NoLongerPassesAShowFpsArgument()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Clipboard", "SchematicClipboard.cs"));
        Assert.DoesNotContain("showFps", src);
    }
}
