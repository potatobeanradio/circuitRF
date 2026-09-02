// ================================================================
//  PlotTypeIntegrityTests.cs  —  brief-dd-plot-type-integrity.md gate tests
//
//  §1 remap matrix (Trace.RemapForPlotType + Plot.SetPlotType's narrowed Table-leaving deletion),
//  §2 container aspect ratio on a plot-type change, §3 Smith/Polar manual axis limits stay square,
//  §4 per-plot-type transform-list filtering.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PlotTypeIntegrityTests
{
    // ---- helpers -----------------------------------------------------------

    private static SNP Snp2() => new SNP(new[] { 1e9, 2e9 }, 2);

    /// <summary>A cube-bound trace shaped like a network-parameter element (S/Z/Y — "i"/"j" port
    /// axes present), matching the owner's worked example SP1.S[:, 1, 1].</summary>
    private static Trace SParamCubeTrace(PlotType seedPlotType, CubeTransform transform)
    {
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "SP1.S",
            Slice = new[]
            {
                new AxisSlice("freq", AxisRole.KeepAsX,    0),
                new AxisSlice("i",    AxisRole.PinToIndex, 0),
                new AxisSlice("j",    AxisRole.PinToIndex, 0),
            },
            Transform = transform,
        };
        t.SetCubeData(new double[] { 1e9, 2e9 },
            new Complex[] { new(0.5, -0.1), new(0.4, -0.2) }, null,
            "freq", "Hz", seedPlotType, FreqUnit.GHz);
        t.Expression = t.BuildPickerExpression();   // mirrors CommitSpec's picker-authored sync
        return t;
    }

    /// <summary>A plain complex cube-bound trace, not S/Z/Y-shaped (no i/j axes) — DefaultRectTransform
    /// gives it Mag, not dB20.</summary>
    private static Trace ComplexCubeTrace(PlotType seedPlotType, CubeTransform transform, string cubeName = "V")
    {
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cubeName,
            Slice    = new[] { new AxisSlice("freq", AxisRole.KeepAsX, 0) },
            Transform = transform,
        };
        t.SetCubeData(new double[] { 1e9, 2e9 },
            new Complex[] { new(1, 0), new(0, 1) }, null,
            "freq", "Hz", seedPlotType, FreqUnit.GHz);
        t.Expression = t.BuildPickerExpression();
        return t;
    }

    private static Trace RealCubeTrace(PlotType seedPlotType, string cubeName = "Pout")
    {
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName  = cubeName,
            Slice     = new[] { new AxisSlice("freq", AxisRole.KeepAsX, 0) },
            Transform = CubeTransform.None,
        };
        t.SetCubeData(new double[] { 1e9, 2e9 }, null,
            new double[] { 0.1, 0.2 },
            "freq", "Hz", seedPlotType, FreqUnit.GHz);
        t.Expression = t.BuildPickerExpression();
        return t;
    }

    private static Trace FamilyCubeTrace(PlotType seedPlotType, CubeTransform transform)
    {
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = "V",
            Slice = new[]
            {
                new AxisSlice("Pin",  AxisRole.FamilyIterate, 0),
                new AxisSlice("freq", AxisRole.KeepAsX,       0),
            },
            Transform = transform,
        };
        var curves = new (double, string?, Complex[]?, double[]?)[]
        {
            (0.0, null, new Complex[] { new(1, 0), new(0, 1) }, null),
            (1.0, null, new Complex[] { new(2, 0), new(0, 2) }, null),
        };
        t.SetFamilyData(new double[] { 1e9, 2e9 }, "freq", "Hz", "Pin", curves, seedPlotType, FreqUnit.GHz);
        t.Expression = t.BuildPickerExpression();
        return t;
    }

    private static Trace ScalarCubeTrace(string cubeName = "PDC")
    {
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cubeName,
            Slice    = Array.Empty<AxisSlice>(),
        };
        t.SetScalarCubeData(null, 5.0, PlotType.Table, FreqUnit.GHz);
        return t;
    }

    // =========================================================================
    //  §1 — Remap matrix
    // =========================================================================

    [Fact]
    public void SmithToRectToSmith_SParamCube_RoundTripsTransformAndExpression()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var t    = SParamCubeTrace(PlotType.Smith, CubeTransform.None);
        plot.Traces.Add(t);
        Assert.Equal("SP1.S[:, 1, 1]", t.Expression);

        plot.SetPlotType(PlotType.Rect);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(CubeTransform.dB20, t.Transform);
        Assert.Equal("dB20(SP1.S[:, 1, 1])", t.Expression);
        Assert.False(t.RectValueInvalid);
        Assert.NotEmpty(t.Points);

        plot.SetPlotType(PlotType.Smith);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(CubeTransform.None, t.Transform);
        Assert.Equal("SP1.S[:, 1, 1]", t.Expression);
        Assert.NotEmpty(t.Points);
    }

    [Fact]
    public void RectToSmithToRect_NonParamComplexCube_UsesMagDefault()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t    = ComplexCubeTrace(PlotType.Rect, CubeTransform.Mag);
        plot.Traces.Add(t);

        plot.SetPlotType(PlotType.Smith);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(CubeTransform.None, t.Transform);

        plot.SetPlotType(PlotType.Rect);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(CubeTransform.Mag, t.Transform);   // Mag default — V has no i/j port axes
        Assert.False(t.RectValueInvalid);
    }

    [Theory]
    [InlineData(PlotType.Smith)]
    [InlineData(PlotType.Rect)]
    public void RealCubeTrace_SurvivesEveryPlotTypeSwitch_Unchanged(PlotType target)
    {
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        var t    = RealCubeTrace(PlotType.Table);
        plot.Traces.Add(t);
        var expr = t.Expression;

        plot.SetPlotType(PlotType.Rect);   // leave Table first (Table↔anything is a no-op)
        plot.SetPlotType(target);

        Assert.Contains(t, plot.Traces);          // never deleted
        Assert.Equal(CubeTransform.None, t.Transform);
        Assert.Equal(expr, t.Expression);          // untouched — real data, nothing to remap
    }

    [Fact]
    public void FamilyTrace_RemapsTransformAndRebuildsPointsOnPlotTypeChange()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t    = FamilyCubeTrace(PlotType.Rect, CubeTransform.Mag);
        plot.Traces.Add(t);
        Assert.True(t.IsFamily);
        Assert.All(t.FamilyCurves, c => Assert.NotEmpty(c.Points));

        plot.SetPlotType(PlotType.Smith);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(CubeTransform.None, t.Transform);
        // Family points must be rebuilt for the Smith plane (complex passthrough), not left stale
        // from the Rect scalar rendering — this is the BuildFamilyPath fix.
        Assert.All(t.FamilyCurves, c => Assert.NotEmpty(c.Points));
        Assert.All(t.FamilyCurves, c => Assert.All(c.Points, p => Assert.True(p.X != 0 || p.Y != 0)));

        plot.SetPlotType(PlotType.Rect);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(CubeTransform.Mag, t.Transform);
        Assert.All(t.FamilyCurves, c => Assert.NotEmpty(c.Points));
    }

    [Fact]
    public void FreqUnitChange_AlsoRebuildsFamilyPoints()
    {
        // Same underlying gap BuildFamilyPath fixes: Plot.FreqUnits's setter only calls
        // Trace.BuildPath, which used to no-op for a family trace.
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t    = FamilyCubeTrace(PlotType.Rect, CubeTransform.Mag);
        plot.Traces.Add(t);
        double firstXGHz = t.FamilyCurves[0].Points[0].X;

        plot.FreqUnits = FreqUnit.MHz;

        double firstXMHz = t.FamilyCurves[0].Points[0].X;
        Assert.Equal(firstXGHz * 1000.0, firstXMHz, 3);
    }

    [Fact]
    public void DerivedMaxGain_NoLongerDeletedEnteringSmith_RoundTrips()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db) { Derived = DerivedParameters.MaxGain };
        plot.Traces.Add(t);

        plot.SetPlotType(PlotType.Smith);
        Assert.Contains(t, plot.Traces);              // kept, not deleted (§1 anchor 3)
        Assert.Equal(DerivedParameters.MaxGain, t.Derived);

        plot.SetPlotType(PlotType.Rect);
        Assert.Contains(t, plot.Traces);
        Assert.Equal(DerivedParameters.MaxGain, t.Derived);
    }

    [Fact]
    public void NetworkTrace_PhaseYAxis_RemappedToComplexNotDeleted()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Phase);
        plot.Traces.Add(t);

        plot.SetPlotType(PlotType.Smith);

        Assert.Contains(t, plot.Traces);                          // kept, not deleted (anchor 3 rewrite)
        Assert.Equal(DependentVarFormat.Complex, t.YAxis);
    }

    [Fact]
    public void ContourTrace_UntouchedByRemap()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            ContourData = new ContourData(),
        };
        plot.Traces.Add(t);

        plot.SetPlotType(PlotType.Smith);
        Assert.Contains(t, plot.Traces);
        plot.SetPlotType(PlotType.Rect);
        Assert.Contains(t, plot.Traces);
    }

    // ---- Table-leaving narrowing -------------------------------------------

    [Fact]
    public void LeavingTable_OrdinaryCubeTraceSurvives()
    {
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        var t    = RealCubeTrace(PlotType.Table);
        plot.Traces.Add(t);

        plot.SetPlotType(PlotType.Rect);

        Assert.Contains(t, plot.Traces);
    }

    [Fact]
    public void LeavingTable_ScalarCubeTraceIsDeleted()
    {
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        var t    = ScalarCubeTrace();
        plot.Traces.Add(t);
        Assert.True(t.CubeIsScalar);

        plot.SetPlotType(PlotType.Rect);

        Assert.DoesNotContain(t, plot.Traces);
    }

    [Fact]
    public void LeavingTable_SummaryColumnIsDeleted()
    {
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        var t = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SummaryColumn = new SummaryColumnData(),
        };
        plot.Traces.Add(t);

        plot.SetPlotType(PlotType.Rect);

        Assert.DoesNotContain(t, plot.Traces);
    }

    [Fact]
    public void EnteringTable_NoTransformOrYAxisChange()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var cube = ComplexCubeTrace(PlotType.Rect, CubeTransform.Mag);
        var net  = new Trace(Snp2(), MatrixType.S, 0, 0, DependentVarFormat.Phase);
        plot.Traces.Add(cube);
        plot.Traces.Add(net);

        plot.SetPlotType(PlotType.Table);

        Assert.Equal(CubeTransform.Mag, cube.Transform);
        Assert.Equal(DependentVarFormat.Phase, net.YAxis);
    }

    // =========================================================================
    //  §2 — Container adopts the configured aspect ratio on a plot-type switch
    // =========================================================================

    [Fact]
    public void ContainerAdoptsGoldenRatio_OnSwitchToRect_KeepsWidth()
    {
        var vm  = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c   = vm.AddPlot(PlotType.Smith, FreqUnit.GHz);
        double startWidth = c.Width;
        double ratio = AppSettingsViewModel.Instance.RectAspectRatio;

        c.Inspector.PlotType = PlotType.Rect;

        Assert.Equal(startWidth, c.Width, 3);         // width kept — plot doesn't jump horizontally
        Assert.Equal(ratio, c.Width / c.Height, 3);
    }

    [Fact]
    public void ContainerReturnsToSquare_OnSwitchBackToSmith()
    {
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(PlotType.Smith, FreqUnit.GHz);

        c.Inspector.PlotType = PlotType.Rect;
        c.Inspector.PlotType = PlotType.Smith;

        Assert.Equal(c.Width, c.Height, 3);
    }

    [Fact]
    public void RectToSmith_IsTheSameSizeAsAnAddedSmith()
    {
        // Reported bug: converting a Rect plot to a Smith chart via the Plot Properties inspector
        // produced a visibly larger chart than the toolbar's Add Smith Chart. The square branch
        // preserved max(Width, Height), which is the Rect's own 520 width, not the 420 default.
        var vm       = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var added    = vm.AddPlot(PlotType.Smith, FreqUnit.GHz);
        var converted = vm.AddPlot(PlotType.Rect, FreqUnit.GHz);

        converted.Inspector.PlotType = PlotType.Smith;

        Assert.Equal(added.Width,  converted.Width,  3);
        Assert.Equal(added.Height, converted.Height, 3);
        Assert.Equal(converted.Width, converted.Height, 3);
    }

    [Fact]
    public void TableToSmith_IsTheSameSizeAsAnAddedSmith()
    {
        // The other non-square starting point: a Table's box is its natural column width, which is
        // NARROWER than the square default — so the old max() rule under-sized this one instead.
        var vm    = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var added = vm.AddPlot(PlotType.Smith, FreqUnit.GHz);
        var c     = vm.AddPlot(PlotType.Table, FreqUnit.GHz);

        c.Inspector.PlotType = PlotType.Smith;

        Assert.Equal(added.Width,  c.Width,  3);
        Assert.Equal(added.Height, c.Height, 3);
    }

    [Fact]
    public void SmithToPolar_KeepsTheUsersOwnSize()
    {
        // Square → square is not a re-shape: the standard size is adopted only when ARRIVING at a
        // square type from a non-square one, so a manual resize survives a Smith/Polar swap.
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(PlotType.Smith, FreqUnit.GHz);
        c.ResizeTo(700, 700);

        c.Inspector.PlotType = PlotType.Polar;

        Assert.Equal(700.0, c.Width,  3);
        Assert.Equal(700.0, c.Height, 3);
    }

    [Fact]
    public void OrdinaryTraceAdd_DoesNotResnapSmithToTheStandardSize()
    {
        // Same guard as the Rect case below: CoerceAspectForPlotType also runs on a plain trace
        // add/remove, and must not undo a user's manual resize of a Smith chart.
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(PlotType.Smith, FreqUnit.GHz);
        c.ResizeTo(700, 700);

        c.PlotVM.Plot.Traces.Add(new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db));
        c.Inspector.NotifyStructureChanged();

        Assert.Equal(700.0, c.Width,  3);
        Assert.Equal(700.0, c.Height, 3);
    }

    [Fact]
    public void TableToRect_AdoptsGoldenRatio()
    {
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(PlotType.Table, FreqUnit.GHz);
        double ratio = AppSettingsViewModel.Instance.RectAspectRatio;

        c.Inspector.PlotType = PlotType.Rect;

        Assert.Equal(ratio, c.Width / c.Height, 3);
    }

    [Fact]
    public void OrdinaryTraceAdd_DoesNotResnapRectToGoldenRatio()
    {
        // Regression guard: CoerceAspectForPlotType also runs on PlotStructureChanged from a plain
        // trace add/remove (same broadcast, brief §2 reuses it) — it must NOT re-snap a user's
        // manual Rect resize just because a trace was added.
        var vm = new DataDisplayViewModel(new DataSourceLibraryViewModel());
        var c  = vm.AddPlot(PlotType.Rect, FreqUnit.GHz);
        c.ResizeTo(700, 700);   // user manually stretches away from golden ratio
        Assert.Equal(700.0, c.Width, 3);
        Assert.Equal(700.0, c.Height, 3);

        c.PlotVM.Plot.Traces.Add(new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db));
        c.Inspector.NotifyStructureChanged();

        Assert.Equal(700.0, c.Width, 3);
        Assert.Equal(700.0, c.Height, 3);
    }

    // =========================================================================
    //  §3 — Smith/Polar manual axis limits stay square
    // =========================================================================

    [Fact]
    public void SmithManualXEdit_CouplesYToASquareWindow()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var vm   = new AxesLimitsViewModel(plot, () => { });
        vm.XAutoscale = false;   // manual edits are ignored while autoscale is on

        vm.XMinText = "-2";
        vm.XMaxText = "2";

        var w = plot.Axes.Window;
        Assert.Equal(w.Width, w.Height, 6);
        Assert.Equal(4.0, w.Width, 6);

        // Y text boxes must refresh to the coupled value immediately.
        Assert.Equal(-2.0, double.Parse(vm.YMinText, System.Globalization.CultureInfo.InvariantCulture), 6);
        Assert.Equal(2.0,  double.Parse(vm.YMaxText, System.Globalization.CultureInfo.InvariantCulture), 6);
    }

    [Fact]
    public void SmithManualYEdit_CouplesXToASquareWindow()
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var vm   = new AxesLimitsViewModel(plot, () => { });
        vm.YAutoscale = false;

        vm.YMinText = "-3";
        vm.YMaxText = "3";

        var w = plot.Axes.Window;
        Assert.Equal(w.Width, w.Height, 6);
        Assert.Equal(6.0, w.Height, 6);
    }

    [Fact]
    public void SmithManualEdit_AsymmetricSpan_MatchesPlotSquareCentredOnOriginDirectly()
    {
        // Both the manual dialog and Plot.Autoscale must agree on what "square" means (brief §3) —
        // assert the dialog's result equals calling the SAME helper directly, so they can never
        // diverge into two different notions of square.
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);
        var vm   = new AxesLimitsViewModel(plot, () => { });
        vm.XAutoscale = false;

        vm.XMinText = "-1";   // asymmetric about the origin
        vm.XMaxText = "3";

        var expected = Plot.SquareCentredOnOrigin(new Avalonia.Rect(-1, 0, 4, 0));
        var actual   = plot.Axes.Window;
        Assert.Equal(expected.X,      actual.X,      6);
        Assert.Equal(expected.Y,      actual.Y,      6);
        Assert.Equal(expected.Width,  actual.Width,  6);
        Assert.Equal(expected.Height, actual.Height, 6);
    }

    [Fact]
    public void RectManualEdit_XAndYStayIndependent()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var vm   = new AxesLimitsViewModel(plot, () => { });
        vm.XAutoscale = false;
        vm.YAutoscale = false;

        vm.XMinText = "0";
        vm.XMaxText = "10";
        vm.YMinText = "-1";
        vm.YMaxText = "1";

        var w = plot.Axes.Window;
        Assert.Equal(10.0, w.Width,  3);   // 3 places: text-parse/Rect float round-trip noise
        Assert.Equal(2.0,  w.Height, 6);   // byte-identical to today: X and Y stay independent on Rect

        vm.YMinText = "-5";                // a Y-only edit must not touch X
        vm.YMaxText = "5";
        w = plot.Axes.Window;
        Assert.Equal(10.0, w.Width,  3);
        Assert.Equal(10.0, w.Height, 6);
    }

    // =========================================================================
    //  §4 — Transform-list filtering
    // =========================================================================

    [Theory]
    [InlineData(PlotType.Rect,  true,  CubeTransform.None, false)]
    [InlineData(PlotType.Rect,  true,  CubeTransform.Conj, false)]
    [InlineData(PlotType.Rect,  true,  CubeTransform.dB20, true)]
    [InlineData(PlotType.Rect,  false, CubeTransform.None, true)]
    [InlineData(PlotType.Rect,  false, CubeTransform.Conj, false)]
    [InlineData(PlotType.Smith, true,  CubeTransform.None, true)]
    [InlineData(PlotType.Smith, true,  CubeTransform.Conj, true)]
    [InlineData(PlotType.Smith, true,  CubeTransform.dB20, false)]
    [InlineData(PlotType.Table, true,  CubeTransform.None, true)]
    [InlineData(PlotType.Table, true,  CubeTransform.Conj, true)]
    public void CubeTraceTransformItems_EnabledMatrix(
        PlotType plotType, bool isComplexData, CubeTransform transform, bool expectedEnabled)
    {
        var items = TraceRowViewModel.BuildTransformItems(isCubeBound: true, plotType, isComplexData);
        var item  = items.Single(i => i.Transform == transform);
        Assert.Equal(expectedEnabled, item.Enabled);
    }

    [Fact]
    public void NetworkTraceTransformItems_NoneStaysEnabledOnRect()
    {
        // A network trace degrades gracefully on Rect (falls back to magnitude) — unlike a cube
        // trace, None/Conj are not disabled for it there.
        var items = TraceRowViewModel.BuildTransformItems(isCubeBound: false, PlotType.Rect, isComplexData: true);
        Assert.True(items.Single(i => i.Transform == CubeTransform.None).Enabled);
        Assert.False(items.Single(i => i.Transform == CubeTransform.dB10).Enabled);
        Assert.False(items.Single(i => i.Transform == CubeTransform.Conj).Enabled);
    }
}
