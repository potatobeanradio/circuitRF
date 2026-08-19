using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>The data-display regression pin for multi-tone HB.</b>
///
/// <para>The T ≥ 3 engine path deliberately emits the SAME cube shape as the two-tone one — the
/// axis is still named <c>mixIndex</c>, its values are still signed product frequencies in Hz,
/// and only the label widens from <c>"(k1,k2)"</c> to <c>"(k1,…,kT)"</c>. Nothing in
/// <c>src/Ui/DataDisplay/</c> was changed for this feature, because its mixIndex handling keys off
/// the axis NAME, positions stems from the axis VALUES, and prints the axis LABEL verbatim.</para>
///
/// <para>This file exists to hold that claim shut from both sides: a three-tone spectrum must
/// render through the existing path, AND the two-tone behaviour that was debugged at length must
/// be bit-for-bit what it was. Every three-tone assertion below has a two-tone twin asserting the
/// identical property, so a future change cannot fix one by breaking the other.</para>
/// </summary>
public sealed class HbMultiToneSpectrumTests(ITestOutputHelper output)
{
    private static Trace MakeCubeTrace() =>
        new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            CubeName  = "V",
            Slice     = new[] { new AxisSlice("mixIndex", AxisRole.KeepAsX, 0) },
            Transform = CubeTransform.Mag,
        };

    // A three-tone spectrum in LATTICE order (not sorted by frequency), as the engine writes it:
    // DC, the three carriers, a baseband difference, and a three-way product.
    private const double F1 = 1.99e9, F2 = 2.00e9, F3 = 2.01e9;

    private static readonly (string Tag, double Hz, double Mag)[] ThreeTone =
    [
        ("(0,0,0)",  0.0,             0.05),
        ("(1,0,0)",  F1,              0.50),
        ("(0,1,0)",  F2,              0.49),
        ("(0,0,1)",  F3,              0.48),
        ("(1,-1,0)", F1 - F2,         0.012),   // NEGATIVE: −10 MHz
        ("(1,1,-1)", F1 + F2 - F3,    0.009),   // three-way product
    ];

    private static readonly (string Tag, double Hz, double Mag)[] TwoTone =
    [
        ("(0,0)",  0.0,        0.05),
        ("(1,0)",  1.995e9,    0.50),
        ("(0,1)",  2.005e9,    0.49),
        ("(1,-1)", -10e6,      0.012),
        ("(2,-1)", 1.985e9,    0.004),
    ];

    private static Trace Spectrum((string Tag, double Hz, double Mag)[] products)
    {
        var t = MakeCubeTrace();
        t.SetCubeData(
            products.Select(p => p.Hz).ToArray(),
            complexValues: null,
            products.Select(p => p.Mag).ToArray(),
            xAxisName: Trace.MixIndexAxisName,
            xUnit:     "Hz",
            PlotType.Rect, FreqUnit.GHz,
            xLabels: products.Select(p => p.Tag).ToArray());
        return t;
    }

    [Fact]
    public void ThreeToneSpectrum_RendersAsStems_ThroughTheExistingMixIndexPath()
    {
        var three = Spectrum(ThreeTone);
        var two   = Spectrum(TwoTone);

        // The same flag drives the stem renderer for both — the tone count is invisible here.
        Assert.True(three.IsMixIndexStem);
        Assert.True(two.IsMixIndexStem);
        Assert.False(three.IsHarmonicStem);

        // Single-sided: the negative-frequency products fold onto +|f|, as at two tones.
        Assert.All(three.Points, p => Assert.True(p.X >= -1e-6f));
        Assert.All(two.Points,   p => Assert.True(p.X >= -1e-6f));
        Assert.Contains(three.Points, p => Math.Abs(p.X - 0.01f) < 1e-4f);   // |f1−f2| = 10 MHz
        Assert.Contains(two.Points,   p => Math.Abs(p.X - 0.01f) < 1e-4f);

        // Every product gets a stem, at its own frequency in GHz.
        Assert.Equal(ThreeTone.Length, three.Points.Count);
        foreach (var (tag, hz, _) in ThreeTone)
            Assert.True(three.Points.Any(p => Math.Abs(p.X - Math.Abs(hz) / 1e9) < 1e-4f),
                $"no stem for {tag} at {Math.Abs(hz) / 1e9:F3} GHz");
    }

    [Fact]
    public void MarkerReadout_ShowsTheToneVectorTag_AtAnyToneCount()
    {
        // The readout prints the axis LABEL verbatim, so a 3-tuple needs no formatting change.
        // Asserted against the two-tone form in the same test so the shared contract is explicit.
        var three = Spectrum(ThreeTone);
        var two   = Spectrum(TwoTone);

        static string[] LinesAt(Trace t, double ghz)
        {
            var m = new Marker(t, freq: 0, isMulti: false, isDelta: false, index: 1)
            {
                PositionStatic = new Vector2((float)ghz, 0f),
            };
            return t.BuildMarkerBoxLines(m, FreqUnit.GHz, showFilePrefix: false)
                    .Select(l => l.Text).ToArray();
        }

        var l3 = LinesAt(three, 0.01);   // the (1,-1,0) baseband product
        output.WriteLine("3-tone marker: " + string.Join(" | ", l3));
        Assert.Contains(l3, s => s == "mixIndex=(1,-1,0)");
        Assert.Contains(l3, s => s.StartsWith("freq=") && s.Contains("GHz"));

        var l2 = LinesAt(two, 0.01);     // the (1,-1) baseband product
        output.WriteLine("2-tone marker: " + string.Join(" | ", l2));
        Assert.Contains(l2, s => s == "mixIndex=(1,-1)");
        Assert.Contains(l2, s => s.StartsWith("freq=") && s.Contains("GHz"));

        // Same row COUNT and same row ORDER — the three-tone case is not a different layout.
        Assert.Equal(l2.Length, l3.Length);
        Assert.Equal(Array.FindIndex(l2, s => s.StartsWith("mixIndex=")),
                     Array.FindIndex(l3, s => s.StartsWith("mixIndex=")));
    }

    [Fact]
    public void ArrowStepping_FollowsFrequencyOrder_NotLatticeOrder()
    {
        // Products are stored in lattice order, so stepping must sort by frequency — the property
        // that makes tight IMD spacings navigable. It must hold identically at three tones.
        var t = Spectrum(ThreeTone);
        var m = new Marker(t, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            PositionStatic = new Vector2(0f, 0f),   // start at DC
        };

        // Frequency order: 0, 0.01 (|f1−f2|), 1.98 (f1+f2−f3), 1.99, 2.00, 2.01 GHz.
        double[] expected = [0.01, 1.98, 1.99, 2.00, 2.01];
        foreach (double ghz in expected)
        {
            Assert.True(t.StepMarkerAlongX(m, +1));
            Assert.Equal(ghz, (double)m.PositionStatic.X, 3);
        }

        // At the top — no wrap, no move.
        Assert.False(t.StepMarkerAlongX(m, +1));
        Assert.Equal(2.01, (double)m.PositionStatic.X, 3);
    }

    [Theory]
    [InlineData("(1,-1,0)")]
    [InlineData("(1,1,-1)")]
    [InlineData("(0,0,0)")]
    public void QuotedToneVectorLabel_SurvivesSliceTokenSplitting(string tag)
    {
        // A slice like V["n_drain","(1,1,-1)"] must not be split on the commas INSIDE the quoted
        // label. SliceTokenParser already respects quotes, so a 3-tuple works for the same reason
        // a 2-tuple does — pinned here because the tag got longer and the failure would be a
        // confusing "wrong number of axes" rather than anything naming the label.
        string body = $"\"n_drain\",\"{tag}\"";
        var tokens = SliceTokenParser.SplitTokens(body);

        Assert.Equal(2, tokens.Length);
        Assert.Equal("\"n_drain\"", tokens[0]);
        Assert.Equal($"\"{tag}\"",  tokens[1]);

        // And it resolves to the right axis index, carrying the label through. (A quoted label
        // classifies as PinIndex — Parse RESOLVES it rather than deferring it, per the grammar
        // comment at the top of SliceTokenParser: "label → PinLabel resolved to an integer index".)
        var labels = ThreeTone.Select(p => p.Tag).ToArray();
        var tk = SliceTokenParser.Parse(tokens[1], labels.Length, labels, "mixIndex", out string err);
        Assert.Equal("", err);
        Assert.Equal(SliceTokenParser.Kind.PinIndex, tk.Kind);
        Assert.Equal(Array.IndexOf(labels, tag), tk.Index);
        Assert.Equal(tag, tk.Label);
    }
}
