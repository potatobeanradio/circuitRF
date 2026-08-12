// ================================================================
//  HarmonicaReadoutFontTests.cs  —  §4 of brief-harmonicarf-r1c-chrome-readouts-dut-and-export
//
//  "The data read out (and config settings) in the lower left of the data display should render at
//  a size that depends on the window size… Same as how the Smith charts currently scale size to
//  match occupy the maximum space and reduce in size when less pixels are available."
// ================================================================

using System;
using System.IO;
using CircuitRF.Ui.Views.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaReadoutFontTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(910, 342, 10.26)]     // §7.1's default layout at an ordinary ~1400×900 window
    [InlineData(2900, 646, 16.0)]     // a large display — clamped at the ceiling
    [InlineData(860, 212.8, 8.0)]     // a small window — clamped at the floor
    public void FontSizeFor_ScalesWithTheStripsShorterSide_AndClamps(double w, double h, double expected)
    {
        double got = ReadoutStripView.FontSizeFor(w, h);
        output.WriteLine($"{w}×{h} → {got:F3} pt");
        Assert.InRange(got, expected - 0.05, expected + 0.05);
    }

    [Fact]
    public void FontSizeFor_NeverLeavesTheStatedRange()
    {
        // §4's own range: "below ~8 pt the strip is unreadable; above ~16 pt it stops being dense."
        foreach (var (w, h) in new (double, double)[]
                 { (1, 1), (10000, 10000), (5000, 3), (3, 5000), (0, 0) })
        {
            double got = ReadoutStripView.FontSizeFor(w, h);
            Assert.InRange(got, ReadoutStripView.MinFontSize, ReadoutStripView.MaxFontSize);
        }
    }

    [Fact]
    public void FontSizeFor_TracksTheShorterSide_NotTheLonger()
    {
        // A wide-but-short strip and a narrow-but-tall one of the same shorter dimension must land on
        // the identical font size — density is bounded by whichever axis is tightest.
        double wide = ReadoutStripView.FontSizeFor(2000, 300);
        double tall = ReadoutStripView.FontSizeFor(300, 2000);
        Assert.Equal(wide, tall);
    }

    // ── the in-place update path must thread the font size too (§4's own guardrail) ──────────────

    [Fact]
    public void EveryHardcodedTenPointFontIsGone_FromSetItemsAndSetInputs()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        // Every element in the strip must scale together — labels, values, units, editors, error
        // line — so a literal "FontSize          = 10," (the old hardcoded value) surviving anywhere
        // in the element-construction code means one of them silently stayed fixed.
        Assert.DoesNotContain("FontSize          = 10,", src, StringComparison.Ordinal);

        // The in-place path (UpdateInPlace) must set FontSize on every element kind it touches, or a
        // resize mid-typing would move every OTHER row's font but leave the focused one behind.
        Assert.Contains("box.FontSize   = fontSize;", src, StringComparison.Ordinal);
        Assert.Contains("check.FontSize = fontSize;", src, StringComparison.Ordinal);
        Assert.Contains("label.FontSize   = fontSize;", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AFontSizeChangeAloneDoesNotCountAsAShapeChange()
    {
        // §4's own guardrail, quoted directly: "A font-size change is a shape change in the layout
        // sense but must NOT count as one for that guard — thread the size through the in-place path
        // too." The rebuild-vs-in-place decision must be keyed on the input SIGNATURE alone, never on
        // the font size — otherwise every resize would rebuild the row and drop whichever editor had
        // focus, the one failure §7.5's H7 note calls "the single most disruptive thing this panel
        // could do".
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");
        int sigEquals = src.IndexOf("if (signature == _inputSignature", StringComparison.Ordinal);
        Assert.True(sigEquals >= 0, "the shape-change guard must still be keyed on the signature");

        // The comparison itself must not also test the font size.
        int lineEnd = src.IndexOf('\n', sigEquals);
        string guardLine = src[sigEquals..lineEnd];
        Assert.DoesNotContain("fontSize", guardLine, StringComparison.OrdinalIgnoreCase);
    }

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
}
