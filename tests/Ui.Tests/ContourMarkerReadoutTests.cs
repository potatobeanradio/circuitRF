// ================================================================
//  ContourMarkerReadoutTests.cs
//
//  GitHub issue #2 ("loadpull data show"): a marker dropped on a loadpull contour must read out
//  EVERY contour in that plot at the one termination it sits on — power AND efficiency at the same
//  tuner setting — plus the impedance in ohms, and Γ when the plot is a Smith/polar chart.
//
//  Row order is part of the contract: name, one row per contour trace in placement order,
//  impedance, then Γ.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ContourMarkerReadoutTests
{
    // ---- Fixture ----------------------------------------------------------

    /// <summary>A contour trace whose surface reads a constant, so a row's value identifies the trace
    /// it came from without needing a real loadpull fit behind it.</summary>
    private static Trace ContourTrace(string metric, double constantValue, bool gammaPlane,
                                      Complex? z0 = null)
    {
        var snp   = new SNP(new[] { 1e9 }, 1);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            ContourData = new ContourData
            {
                MetricName     = metric,
                GammaPlane     = gammaPlane,
                EvaluateMetric = (_, _) => constantValue,
            },
        };
        if (z0 is { } z) trace.Z0 = z;
        return trace;
    }

    private static Marker ContourMarker(Trace owner, Complex coord)
    {
        var m = new Marker(owner, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            MarkerKind     = MarkerKind.Contour,
            PositionStatic = new System.Numerics.Vector2((float)coord.Real, (float)coord.Imaginary),
        };
        owner.Markers.Add(m);
        return m;
    }

    private static List<string> Lines(Trace owner, Marker m, IReadOnlyList<Trace>? plotTraces) =>
        owner.BuildMarkerBoxLines(m, FreqUnit.GHz, showFilePrefix: false, plotTraces: plotTraces)
             .Select(l => l.Text).ToList();

    // ---- The issue itself -------------------------------------------------

    /// <summary>
    /// Power and efficiency plotted together: one marker, both readings, in the order the traces
    /// were placed — NOT just the trace that happens to own the marker.
    /// </summary>
    [Fact]
    public void EveryContourInThePlot_IsReadOut_InPlacementOrder()
    {
        var pout = ContourTrace("Pout_dBm", 41.5, gammaPlane: true);
        var pae  = ContourTrace("PAE",      63.25, gammaPlane: true);
        var marker = ContourMarker(pae, new Complex(0.0, 0.0));   // marker owned by the SECOND trace

        var lines = Lines(pae, marker, new[] { pout, pae });

        Assert.Equal("m1",              lines[0]);
        Assert.StartsWith("Pout_dBm=",  lines[1]);   // placement order, not owner-first
        Assert.StartsWith("PAE=",       lines[2]);
        Assert.Contains("41.5",         lines[1]);
        Assert.Contains("63.25",        lines[2]);
        Assert.Contains("dBm",          lines[1]);
        Assert.Contains("%",            lines[2]);
    }

    /// <summary>Impedance, then Γ, after the metric rows — and only those five rows.</summary>
    [Fact]
    public void RowOrder_IsName_Metrics_Impedance_Gamma()
    {
        var pout = ContourTrace("Pout_dBm", 41.5, gammaPlane: true);
        var pae  = ContourTrace("PAE",      63.25, gammaPlane: true);
        var lines = Lines(pout, ContourMarker(pout, new Complex(0.2, -0.1)), new[] { pout, pae });

        Assert.Equal(5, lines.Count);
        Assert.Equal("m1", lines[0]);
        Assert.StartsWith("Pout_dBm=", lines[1]);
        Assert.StartsWith("PAE=",      lines[2]);
        Assert.StartsWith("Z=",        lines[3]);
        Assert.EndsWith(" Ω",          lines[3]);
        Assert.StartsWith("Γ=",        lines[4]);
    }

    // ---- The impedance is against the contour's own reference -------------

    /// <summary>
    /// Γ = 1/3 is 100 Ω against a 50 Ω reference and 50 Ω against 25 Ω. The conversion is the
    /// loadpull surface's own (Z = Z0·(1+Γ)/(1−Γ)) — the same one the surface was fitted with.
    /// </summary>
    [Theory]
    [InlineData(50.0, 100.0)]
    [InlineData(25.0,  50.0)]
    public void ImpedanceRow_AccountsForTheReferenceZ0(double z0, double expectedOhms)
    {
        var t = ContourTrace("Pout_dBm", 41.5, gammaPlane: true, z0: new Complex(z0, 0));
        var m = ContourMarker(t, new Complex(1.0 / 3.0, 0.0));

        Assert.Equal(expectedOhms, t.ContourImpedance(new Complex(1.0 / 3.0, 0.0)).Real, 6);

        var zRow = Lines(t, m, new[] { t }).Single(l => l.StartsWith("Z="));
        Assert.StartsWith($"Z={expectedOhms:G4}", zRow);
    }

    /// <summary>The impedance reads in rectangular ohms (R+jX), not in the marker's Γ format —
    /// "133.3∠26.6°" is not a number anyone puts in a matching network.</summary>
    [Fact]
    public void ImpedanceRow_ReadsInRectangularOhms_EvenWhenTheMarkerFormatIsPolar()
    {
        var t = ContourTrace("Pout_dBm", 41.5, gammaPlane: true);
        var m = ContourMarker(t, new Complex(0.2, 0.2));
        m.MatrixFormat = MatrixFormat.MA;      // Γ still polar…

        var lines = Lines(t, m, new[] { t });
        Assert.Contains("+j", lines.Single(l => l.StartsWith("Z=")));   // …impedance is not
        Assert.Contains("∠",  lines.Single(l => l.StartsWith("Γ=")));
    }

    // ---- Plane gating ------------------------------------------------------

    /// <summary>On a Rect (Z-plane) contour the coordinate already IS the impedance, so there is no Γ
    /// row to add — a Γ row there would only repeat the row above it.</summary>
    [Fact]
    public void ZPlaneContour_ShowsImpedanceButNoGammaRow()
    {
        var t = ContourTrace("Pout_dBm", 41.5, gammaPlane: false);
        var m = ContourMarker(t, new Complex(30.0, -12.0));

        var lines = Lines(t, m, new[] { t });

        Assert.DoesNotContain(lines, l => l.StartsWith("Γ="));
        var zRow = lines.Single(l => l.StartsWith("Z="));
        Assert.Contains("30", zRow);
        Assert.Contains("-j12", zRow);
    }

    // ---- Scope: contours only ---------------------------------------------

    /// <summary>Non-contour traces sharing the plot contribute no rows — the readout is a comparison
    /// between loadpull surfaces, and nothing else in the plot is sliceable by termination.</summary>
    [Fact]
    public void NonContourTracesInTheSamePlot_AreNotReadOut()
    {
        var pout    = ContourTrace("Pout_dBm", 41.5, gammaPlane: true);
        var ordinary = new Trace(new SNP(new[] { 1e9, 2e9 }, 2), MatrixType.S, 1, 0,
                                 DependentVarFormat.Db);
        var lines = Lines(pout, ContourMarker(pout, Complex.Zero), new[] { pout, ordinary });

        Assert.Equal(4, lines.Count);                       // name, Pout, Z, Γ
        Assert.DoesNotContain(lines, l => l.Contains("S(2,1)"));
    }

    /// <summary>With no plot context (export/design-time paths pass null) the marker still reports its
    /// own contour rather than falling back to nothing.</summary>
    [Fact]
    public void WithoutPlotContext_TheOwnContourIsStillReadOut()
    {
        var t = ContourTrace("Pout_dBm", 41.5, gammaPlane: true);
        var lines = Lines(t, ContourMarker(t, Complex.Zero), plotTraces: null);

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("Pout_dBm=", lines[1]);
    }

    /// <summary>A marker on an ordinary (non-contour) trace is untouched by any of this.</summary>
    [Fact]
    public void OrdinaryMarker_IsUnchanged()
    {
        var snp = new SNP(new[] { 1e9, 2e9 }, 2);
        var t   = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var m   = new Marker(t, 1e9, isMulti: false, isDelta: false, index: 1);
        t.Markers.Add(m);

        var lines = Lines(t, m, new[] { t });

        Assert.Equal("m1", lines[0]);
        Assert.StartsWith("freq=", lines[1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Γ="));
    }

    // ---- The info box actually gets the plot's traces ----------------------

    /// <summary>
    /// End-to-end wiring: it is <see cref="MarkerInfoBoxViewModel.PlotTraces"/> that hands the builder
    /// the plot, so a correct builder with a null-supplying VM would still ship the old one-row box.
    /// </summary>
    [Fact]
    public void TheInfoBoxViewModel_SuppliesThePlotsTraces_ToTheBuilder()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.AddPlot();
        var container = display.Plots[0];

        var pout = ContourTrace("Pout_dBm", 41.5, gammaPlane: true);
        var pae  = ContourTrace("PAE",      63.25, gammaPlane: true);
        container.PlotVM.Plot.Traces.Add(pout);
        container.PlotVM.Plot.Traces.Add(pae);
        var marker = ContourMarker(pout, Complex.Zero);
        marker.InfoBoxPos = new Avalonia.Point(0, 0);

        display.OnContainerPlotChanged(container);

        var box = Assert.Single(display.MarkerInfoBoxes);
        Assert.NotNull(box.PlotTraces);

        var lines = Lines(box.Trace, box.Marker, box.PlotTraces);
        Assert.StartsWith("Pout_dBm=", lines[1]);
        Assert.StartsWith("PAE=",      lines[2]);
    }
}
