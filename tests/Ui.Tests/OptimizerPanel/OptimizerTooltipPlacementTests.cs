using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CircuitRF.Ui.Tests.OptimizerPanel;

/// <summary>
/// The Variables header and rows sit at the Optimizer panel's bottom (the Goals list takes the star row), where a tip placed
/// at the cursor flips up onto the pointer: the button loses <c>:pointerover</c>, the tip closes, and it re-opens 100 ms
/// later — the buttons flash. Every tip there is placed above its control WITH the offset; <c>Placement="Top"</c> alone is
/// pushed back down by <c>VerticalOffset</c>'s 20.0 default (src/Ui/RESOLVED.md, "Two tooltips flashed repeatedly").
/// </summary>
public sealed class OptimizerTooltipPlacementTests
{
    [Fact]
    public void EveryTipFromTheVariablesHeaderDown_IsPlacedAboveWithItsOffset()
    {
        string axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Optimization", "OptimizerToolView.axaml"));
        int at = axaml.IndexOf("<!-- Variables header + toolbar.", StringComparison.Ordinal);
        Assert.True(at >= 0, "the Variables header marker moved");

        // The Add flyout is a popup of its own, below its button: its tips are not low in the panel.
        string rest = axaml[at..];
        int fo = rest.IndexOf("<Button.Flyout>", StringComparison.Ordinal);
        rest = rest[..fo] + rest[(rest.IndexOf("</Button.Flyout>", fo, StringComparison.Ordinal))..];

        var tips = rest.Split('<').Where(e => e.Contains("ToolTip.Tip=", StringComparison.Ordinal)).ToList();
        Assert.True(tips.Count >= 8, $"only {tips.Count} tip(s) found below the Variables header");
        Assert.All(tips, e => Assert.Contains("ToolTip.Placement=\"Top\" ToolTip.VerticalOffset=\"-4\"", e, StringComparison.Ordinal));

        foreach (string line in axaml.Split('\n').Where(l => l.Contains("ToolTip.Placement=\"Top\"", StringComparison.Ordinal)))
            Assert.Contains("ToolTip.VerticalOffset=", line, StringComparison.Ordinal);
    }

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }
}
