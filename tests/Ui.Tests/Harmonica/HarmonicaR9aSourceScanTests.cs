// ================================================================
//  HarmonicaR9aSourceScanTests.cs — brief-harmonicarf-r9a
//
//  Source-scan gates for the R9A items that are pure text changes on views the test harness cannot
//  instantiate headlessly: §3 (the two horizontal rule Borders are gone), §6 ("Add Grid Points" /
//  "Add Grid Points to VSWR" menu text) and §9 (the Efficiency Metric "DE" menu item reads "Drain
//  Efficiency" on its two .axaml surfaces).
// ================================================================

using System;
using System.IO;
using CircuitRF.Ui.Views.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR9aSourceScanTests
{
    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Removes <c>//</c>-to-end-of-line and <c>/* … */</c> spans — the same simple,
    /// string-literal-blind stripper this repo's other Harmonica source-scan tests use.</summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(src[i]);
        }
        return sb.ToString();
    }

    // ══ §3 — the two horizontal rule Borders are removed from ReadoutStripView.axaml ══════════════

    [Fact]
    public void ReadoutStripView_HasNeitherInputRuleNorColumnRule()
    {
        string axaml = StripComments(ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml"));
        Assert.DoesNotContain("InputRule", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnRule", axaml, StringComparison.Ordinal);

        string codeBehind = StripComments(ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs"));
        Assert.DoesNotContain("InputRule", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnRule", codeBehind, StringComparison.Ordinal);
    }

    // ══ §6 — "Add Point" → "Add Grid Points", "Add Points to VSWR" → "Add Grid Points to VSWR" ════

    [Fact]
    public void HarmonicaView_UsesTheNewGridPointsMenuText_NotTheOldText()
    {
        string src = StripComments(ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs"));

        Assert.Contains("Add Grid Points", src, StringComparison.Ordinal);
        Assert.Contains("Add Grid Points to VSWR", src, StringComparison.Ordinal);

        Assert.DoesNotContain("\"Add Point\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Add Points to VSWR\"", src, StringComparison.Ordinal);
    }

    // ══ §9 — Display ▸ Efficiency Metric ▸ "DE" reads "Drain Efficiency" (display text only) ══════

    [Fact]
    public void HarmonicaView_TogglesDrainEfficiencyLabel_WithTheDECommandParameterUnchanged()
    {
        string src = StripComments(ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs"));
        Assert.Contains("Toggle(\"Drain Efficiency\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaAppMenuInjector_UsesDrainEfficiencyLabel()
    {
        string src = StripComments(ReadSource("src", "Ui", "Harmonica", "HarmonicaAppMenuInjector.cs"));
        Assert.Contains("Item(\"Drain Efficiency\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaMenuView_Axaml_UsesDrainEfficiencyHeaders()
    {
        string src = StripComments(ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaMenuView.axaml"));
        Assert.Contains("Header=\"Drain Efficiency\"", src, StringComparison.Ordinal);
        Assert.Contains("Header=\"_Drain Efficiency\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"DE\"", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"_DE\"", src, StringComparison.Ordinal);
    }

    // ══ §11 — nothing is posted to the message line while a gesture is live ═══════════════════════

    [Theory]
    [InlineData(false, null,        "idle summary", "idle summary")]
    [InlineData(false, "",          "idle summary", "idle summary")]
    [InlineData(false, "solve failed: x", "idle summary", "solve failed: x")]
    [InlineData(true,  null,        "idle summary", "")]
    [InlineData(true,  "",          "idle summary", "")]
    [InlineData(true,  "solve failed: x", "idle summary", "")]
    public void MessageLineText_IsEmptyWhileAGestureIsLive_RegardlessOfStatusMessage(
        bool gestureLive, string? statusMessage, string idleSummary, string expected)
        => Assert.Equal(expected, HarmonicaView.MessageLineText(gestureLive, statusMessage, idleSummary));
}
