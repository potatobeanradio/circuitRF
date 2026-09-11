// ================================================================
//  MarkerImpedanceFormatTests.cs — a marker's impedance is not spelled in decibels
//
//  Owner-reported: a marker on S(1,1) in a Rect plot read its impedance in dB. A marker takes its
//  MatrixFormat from the trace's y-axis, and a Rect S-parameter trace is plotted in dB, so the
//  "impedance=" row — which borrowed that same format — came out as "… dB ∠…°". A decibel is not a
//  unit of impedance, the Ω that follows the row says so, and the flyout offered no way to change it
//  because the Γ/S format selector is (correctly) hidden on a Rect plot.
//
//  The Rect plot that shows an impedance row is the CUBE-BOUND one — a run's own S cube, where
//  MarkerShowsImpedance asks only whether the trace is a reflection element and never looks at the
//  y-axis. Both that path and the Smith/Polar one land in Trace.FormatImpedance, which is what these
//  tests drive directly: one formatter, so one test of it covers both.
//
//  Two things fix it and both are gated here: the readout is spelled with the marker's own
//  MatrixFormatImpedance (default RI — R+jX, the form a matching network is built from), and the
//  flyout offers a selector for it wherever an impedance is actually printed.
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using NumFlat;

namespace CircuitRF.Ui.Tests;

public class MarkerImpedanceFormatTests
{
    private const double F = 1e9;

    /// <summary>A 1-port whose S11 is a plain reflection. <paramref name="yAxis"/> picks the shape:
    /// Db is what a Rect plot uses (and what makes a marker's own format dB), Complex is the
    /// Smith/Polar one, where the impedance row is shown for an ordinary network trace.</summary>
    private static Trace ReflectionTrace(DependentVarFormat yAxis)
    {
        var m = new Mat<Complex>(1, 1);
        m[0, 0] = new Complex(0.4, -0.2);
        var snp = new SNP([F], [m], MatrixType.S, MatrixFormat.DB, new Complex(50, 0));
        return new Trace(snp, MatrixType.S, 0, 0, yAxis);
    }

    private static Trace DbRectReflectionTrace() => ReflectionTrace(DependentVarFormat.Db);

    [Fact]
    public void AMarkerOnADbTraceStillReadsItsImpedanceInRealAndImaginary()
    {
        var t = DbRectReflectionTrace();
        var marker = new Marker(t, F, isMulti: false, isDelta: false, index: 1)
        {
            UseNormalizedImpedance = false,
        };

        // The marker's VALUE format is dB — that part is right, and is what the trace plots.
        Assert.Equal(MatrixFormat.DB, marker.MatrixFormat);
        // Its IMPEDANCE format is not, and defaults to R+jX.
        Assert.Equal(MatrixFormat.RI, marker.MatrixFormatImpedance);

        string readout = t.GetMarkerImpedanceString(marker);
        Assert.DoesNotContain("dB", readout, StringComparison.Ordinal);
        Assert.DoesNotContain("∠", readout, StringComparison.Ordinal);
        Assert.Contains("j", readout, StringComparison.Ordinal);     // "R+jX"
        Assert.EndsWith("Ω", readout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Smith/Polar shape, where an ordinary network trace really does offer the row: the marker's
    /// value format there is magnitude/angle, and the impedance is STILL R+jX — the ask was that a
    /// marker added to Smith, Polar or Rect defaults its impedance to real/imaginary.
    /// </summary>
    [Fact]
    public void OnAComplexPlaneTraceTheRowIsOfferedAndStillDefaultsToRealAndImaginary()
    {
        var t = ReflectionTrace(DependentVarFormat.Complex);
        var marker = new Marker(t, F, isMulti: false, isDelta: false, index: 1)
        {
            UseNormalizedImpedance = false,
        };

        Assert.True(t.MarkerShowsImpedance(marker), "an S(i,i) trace on a complex plane offers the row");
        Assert.Equal(MatrixFormat.MA, marker.MatrixFormat);          // the value reads in mag/angle…
        string readout = t.GetMarkerImpedanceString(marker);
        Assert.DoesNotContain("∠", readout, StringComparison.Ordinal);   // …and the impedance does not
        Assert.Contains("j", readout, StringComparison.Ordinal);
    }

    /// <summary>The impedance format is honoured, not merely hard-coded to RI — a user who asks for
    /// magnitude/angle gets it.</summary>
    [Fact]
    public void TheImpedanceFormatIsTheMarkersOwnSetting()
    {
        var t = DbRectReflectionTrace();
        var marker = new Marker(t, F, isMulti: false, isDelta: false, index: 1)
        {
            UseNormalizedImpedance = false,
            MatrixFormatImpedance  = MatrixFormat.MA,
        };

        Assert.Contains("∠", t.GetMarkerImpedanceString(marker), StringComparison.Ordinal);
    }

    /// <summary>The normalized form reads the same way — it is the same number over Z0.</summary>
    [Fact]
    public void TheNormalizedFormUsesTheImpedanceFormatToo()
    {
        var t = DbRectReflectionTrace();
        var marker = new Marker(t, F, isMulti: false, isDelta: false, index: 1)
        {
            UseNormalizedImpedance = true,
        };

        string readout = t.GetMarkerImpedanceString(marker);
        Assert.StartsWith("impedance=Z0*(", readout, StringComparison.Ordinal);
        Assert.DoesNotContain("dB", readout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Rect case is exactly the one with no control: the Γ/S selector is hidden there because the
    /// plotted value is scalar, so the impedance selector must NOT share its gate.
    /// </summary>
    [Fact]
    public void TheFlyoutOffersAnImpedanceFormatSelectorWhereverAnImpedanceIsPrinted()
    {
        string vm = File.ReadAllText(Path.Combine(
            RepoRoot(), "src/Ui/DataDisplay/ViewModels/MarkerEditorViewModel.cs"));
        Assert.Contains("ShowImpedanceFormatSelector", vm, StringComparison.Ordinal);
        Assert.Contains("MarkerShowsImpedance(_marker)", vm, StringComparison.Ordinal);
        Assert.Contains("_marker.MatrixFormatImpedance = value;", vm, StringComparison.Ordinal);

        string xaml = File.ReadAllText(Path.Combine(
            RepoRoot(), "src/Ui/Views/DataDisplay/MarkerEditorView.axaml"));
        Assert.Contains("{Binding ShowImpedanceFormatSelector}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding ImpedanceFormat}", xaml, StringComparison.Ordinal);
    }

    /// <summary>A chosen impedance format survives a save/load of the display.</summary>
    [Fact]
    public void TheImpedanceFormatIsPersisted()
    {
        var cfg = new MarkerConfig();
        Assert.Equal(MatrixFormat.RI, cfg.MatrixFormatImpedance);   // what a pre-existing .cdd loads as

        string saver = File.ReadAllText(Path.Combine(
            RepoRoot(), "src/Ui/DataDisplay/ViewModels/DataDisplayViewModel.cs"));
        Assert.Contains("MatrixFormatImpedance = m.MatrixFormatImpedance,", saver, StringComparison.Ordinal);

        string loader = File.ReadAllText(Path.Combine(
            RepoRoot(), "src/Render/DataDisplay/PlotConfigLoader.cs"));
        Assert.Contains("MatrixFormatImpedance  = mc.MatrixFormatImpedance,", loader, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
