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
    // R-h9r2-21 — +25% on the fraction AND both clamps (0.03→0.0375, 8→10, 16→20), or the increase
    // evaporates the moment the strip is clamped at either end.
    [Theory]
    [InlineData(910, 342, 12.825)]    // §7.1's default layout at an ordinary ~1400×900 window
    [InlineData(2900, 646, 20.0)]     // a large display — clamped at the (new) ceiling
    [InlineData(860, 212.8, 10.0)]    // a small window — clamped at the (new) floor
    public void FontSizeFor_ScalesWithTheStripsShorterSide_AndClamps(double w, double h, double expected)
    {
        double got = ReadoutStripView.FontSizeFor(w, h);
        output.WriteLine($"{w}×{h} → {got:F3} pt");
        Assert.InRange(got, expected - 0.05, expected + 0.05);
    }

    [Fact]
    public void FontSizeFor_NeverLeavesTheStatedRange()
    {
        // R-h9r2-21's own range: "below ~10 pt the strip is unreadable; above ~20 pt it stops being dense."
        foreach (var (w, h) in new (double, double)[]
                 { (1, 1), (10000, 10000), (5000, 3), (3, 5000), (0, 0) })
        {
            double got = ReadoutStripView.FontSizeFor(w, h);
            Assert.InRange(got, ReadoutStripView.MinFontSize, ReadoutStripView.MaxFontSize);
        }
    }

    [Fact]
    public void TheThreeConstants_Are25PercentAboveTheirPreR2bValues()
    {
        Assert.Equal(0.0375, ReadoutStripView.FontSizeFraction, precision: 9);
        Assert.Equal(10.0,   ReadoutStripView.MinFontSize);
        Assert.Equal(20.0,   ReadoutStripView.MaxFontSize);
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

    // ══ R-h9r2-22 — diagnosed: BOTH invalidation paths already call Refresh() from LIVE bounds ═════
    //
    // Read closely (no live window available here): PanelHost.SizeChanged → Refresh() covers a window
    // resize; an Edit Display move/resize goes through HarmonicaEditDisplay.PlacePanel/ResizePanel,
    // which reassigns HarmonicaViewModel.Layout, whose OnLayoutChanged fires RedrawRequested →
    // HarmonicaView.OnRedraw → Refresh() — so an Edit Display drag of the strip's OWN panel already
    // recomputes ReadoutFontSize() from PanelHost's live Bounds too, with no separate wiring needed.
    // Neither path shows a bug on inspection; R-h9r2-21's widened clamp (10..20pt, was 8..16pt) is
    // what actually makes a resize's effect visible across more of the realistic window-size range —
    // pinned here so a regression (either wire silently dropped) is caught even without a live window.

    [Fact]
    public void BothInvalidationPaths_StillCallRefresh_FromLiveBounds()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");

        Assert.Contains("PanelHost.SizeChanged += (_, _) => Refresh();", src, StringComparison.Ordinal);
        Assert.Contains("private void OnRedraw() => Dispatcher.UIThread.Post(Refresh", src, StringComparison.Ordinal);
        Assert.Contains("_doc.ViewModel.Harmonica.RedrawRequested += OnRedraw;", src, StringComparison.Ordinal);

        // ReadoutFontSize() itself must read PanelHost.Bounds live, not a cached value.
        int m = src.IndexOf("private double ReadoutFontSize()", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    }", m, StringComparison.Ordinal);
        string body = src[m..mEnd];
        Assert.Contains("PanelHost.Bounds", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EditDisplayPanelMoves_ReassignLayout_WhichFiresRedrawRequested()
    {
        // The chain PlacePanel/ResizePanel → Layout = l → OnLayoutChanged → RedrawRequested → Refresh
        // is what makes an Edit Display drag of the readout strip's own panel re-font it live, with no
        // extra wiring in HarmonicaView needed for that case specifically.
        string viewModelSrc = ReadSource("src", "Ui", "Harmonica", "HarmonicaViewModel.cs");
        Assert.Contains("EditDisplay  = new HarmonicaEditDisplay(() => Layout, l => Layout = l);",
            viewModelSrc, StringComparison.Ordinal);
        Assert.Contains(
            "partial void OnLayoutChanged(CharmLayout value)     { RedrawRequested?.Invoke(); DirtyChanged?.Invoke(); }",
            viewModelSrc, StringComparison.Ordinal);
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
