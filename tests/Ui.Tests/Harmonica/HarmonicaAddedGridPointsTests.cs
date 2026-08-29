// ================================================================
//  HarmonicaAddedGridPointsTests.cs — brief-harmonicarf-r6b §2.2/§2.3
//
//  "Add Point" / "Add Points to VSWR" — Γ points layered ON TOP of the current ring/spoke preset (or
//  an imported .gam), never replacing it. Owner rulings: persists in the .charm; additive on top of
//  the preset (3×12 + 1 added stays 3×12+1, not thrown away); Grid ▸ Reset Grid and a new Grid Preset
//  both clear the added points.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaAddedGridPointsTests(ITestOutputHelper output)
{
    // ══ the solver's own composition — RingGrid ++ AddedGridPoints ══════════

    [Fact]
    public void Solve_ComposesTheRingGridWithAddedPoints_NeverReplacingIt()
    {
        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(80, 10));

        Complex[] added = [new(0.2, 0.1), new(-0.15, -0.4), new(0.05, 0.3)];

        var baseline = new HarmonicaSolver.Options { Rings = 2, Spokes = 6, RasterResolution = 32 };
        vm.SolveFrame(baseline);
        int baseCount = vm.Frame.SmithPower.GridPoints.Count;
        Assert.Equal(2 * 6 + 1, baseCount); // RingGrid(2, 6) — the rings PLUS the centre point

        vm.SolveFrame(baseline with { AddedGridPoints = added });
        int withAdded = vm.Frame.SmithPower.GridPoints.Count;

        output.WriteLine($"base grid {baseCount} points, +{added.Length} added -> {withAdded}");
        Assert.Equal(baseCount + added.Length, withAdded);

        // The added points themselves are genuinely IN the solved set (by Γ), not merely counted.
        var solvedGammas = vm.Frame.SmithPower.GridPoints.Select(p => p.Gamma).ToArray();
        foreach (var a in added)
            Assert.Contains(a, solvedGammas);
    }

    [Fact]
    public void Solve_ComposesAnImportedScatter_WithAddedPointsToo()
    {
        // §2.2's own text: "an imported .gam still replaces the BASE outright" — AddedGridPoints
        // still layers on top of THAT base, not only on the ring/spoke lattice.
        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(60, -8));

        Complex[] imported = [Complex.Zero, new(0.3, 0.2), new(-0.3, -0.2)];
        Complex[] added    = [new(0.5, 0.0)];

        vm.SolveFrame(new HarmonicaSolver.Options
        {
            GammaGrid = imported, AddedGridPoints = added, RasterResolution = 32,
        });

        Assert.Equal(imported.Length + added.Length, vm.Frame.SmithPower.GridPoints.Count);
    }

    // ══ the view model — collection mutation, clearing, persistence ════════

    [Fact]
    public void AddGridPoint_AppendsTheMarkersOwnGamma()
    {
        var vm = new HarmonicaViewModel();
        var marker = vm.Markers[2]; // L1

        Assert.Empty(vm.AddedGridPoints);
        vm.AddGridPoint(marker.Gamma);

        Assert.Single(vm.AddedGridPoints);
        Assert.Equal(marker.Gamma, vm.AddedGridPoints[0]);
    }

    [Fact]
    public void AddGridPoint_MarksTheDocumentDirty()
    {
        var vm = new HarmonicaViewModel();
        bool dirty = false;
        vm.DirtyChanged += () => dirty = true;

        vm.AddGridPoint(vm.Markers[0].Gamma);
        Assert.True(dirty);
    }

    [Fact]
    public void AddGridPointsOnVswrCircle_Adds12PointsFromTheMarkersOwnLocus()
    {
        var vm = new HarmonicaViewModel();
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        marker.VswrValue = 2.5;
        double z0 = vm.Frame.SmithPower.Z0;

        vm.AddGridPointsOnVswrCircle(marker);

        Assert.Equal(12, vm.AddedGridPoints.Count);

        var expected = LoadpullSurface.VswrLocus(marker.Gamma, marker.VswrValue, SurfacePlane.Gamma,
                                                 new Complex(z0, 0.0), nPoints: 12);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(expected[i].Real,      vm.AddedGridPoints[i].Real,      9);
            Assert.Equal(expected[i].Imaginary, vm.AddedGridPoints[i].Imaginary, 9);
        }
    }

    [Fact]
    public void AddGridPoint_IsAdditive_RepeatedCallsAccumulate()
    {
        var vm = new HarmonicaViewModel();
        vm.AddGridPoint(new Complex(0.1, 0.1));
        vm.AddGridPoint(new Complex(0.2, 0.2));
        vm.AddGridPointsOnVswrCircle(vm.Markers[2]);

        Assert.Equal(2 + 12, vm.AddedGridPoints.Count);
    }

    [Fact]
    public void ResetGrid_ClearsAddedGridPoints_TheSameAsCustomGrid()
    {
        var vm = new HarmonicaViewModel();
        vm.AddGridPoint(new Complex(0.1, 0.1));
        vm.SetGammaGrid([new Complex(0.2, 0.2)]); // installs CustomGrid too
        Assert.NotEmpty(vm.AddedGridPoints);
        Assert.NotNull(vm.CustomGrid);

        vm.ResetGrid();

        Assert.Empty(vm.AddedGridPoints);
        Assert.Null(vm.CustomGrid);
    }

    [Fact]
    public void SetGridPreset_ClearsAddedGridPoints_TheOwnersOwnRuling()
    {
        // "the preset must always describe exactly what is on screen" — picking a NEW preset must not
        // leave a stale added point implying a scatter the menu no longer names.
        var vm = new HarmonicaViewModel();
        vm.AddGridPoint(new Complex(0.1, 0.1));
        Assert.NotEmpty(vm.AddedGridPoints);

        vm.SetGridPreset(5, 12);

        Assert.Empty(vm.AddedGridPoints);
    }

    // ══ persistence — survives a .charm round trip ══════════════════════════

    [Fact]
    public void AddedGridPoints_RoundTripThroughLoadCharm()
    {
        var vm = new HarmonicaViewModel();
        vm.AddGridPoint(new Complex(0.25, -0.1));
        vm.AddGridPointsOnVswrCircle(vm.Markers[2]);
        int expectedCount = vm.AddedGridPoints.Count;
        var expected = vm.AddedGridPoints.ToArray();

        string json = vm.ToCharmJson();

        var reopened = new HarmonicaViewModel();
        reopened.LoadCharm(json, null);

        Assert.Equal(expectedCount, reopened.AddedGridPoints.Count);
        for (int i = 0; i < expectedCount; i++)
            Assert.Equal(expected[i], reopened.AddedGridPoints[i]);
    }

    // ══ end to end — a scheduled request really carries AddedGridPoints ═════

    [Trait("Category", "Benchmark")]
    [Fact]
    public async System.Threading.Tasks.Task RequestScheduledFrame_CarriesAddedGridPoints_ThroughOptionsFor()
    {
        // Pool.Completed is what a test reads — HarmonicaSolvePoolTests' own pattern. vm.Frame is only
        // ever assigned by SolveFrame or by whoever wires Pool.Completed to PublishFrame (the VIEW, in
        // production); a bare RequestFrame/RequestScheduledFrame does not update it by itself.
        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(70, 5));

        HarmonicaFrame? published = null;
        vm.Pool.Completed += (f, _) => published = f;

        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();
        Assert.NotNull(published);
        int baseCount = published!.SmithPower.GridPoints.Count;
        Assert.True(baseCount > 0);

        vm.AddGridPoint(new Complex(0.4, 0.1)); // calls RequestScheduledFrame itself
        await vm.Pool.DrainAsync();

        output.WriteLine($"base {baseCount} -> with one added point {published!.SmithPower.GridPoints.Count}");
        Assert.Equal(baseCount + 1, published!.SmithPower.GridPoints.Count);
        Assert.Contains(published!.SmithPower.GridPoints, p => p.Gamma == new Complex(0.4, 0.1));
    }

    // ══ the marker menu — ordering and enablement (§2.2/§2.3) ═══════════════

    [Fact]
    public void MarkerMenu_AddPoint_SitsDirectlyUnderSnapToGrid_ThenAddPointsToVswr_ThenASeparator()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");
        int m = src.IndexOf("private void BuildMarkerMenu(", System.StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private async System.Threading.Tasks.Task ShowMarkerSetVswrDialogAsync", m,
            System.StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // R7A §2.2/§2.4 — these are now built through the shared Item(header, icon, onClick, …) helper
        // rather than a bare `new MenuItem { Header = … }`, so the source pattern to look for changed
        // from `Header = "…"` to `Item("…"`.
        int snap        = body.IndexOf("Toggle(\"Snap to Grid\",", System.StringComparison.Ordinal);
        int addPoint    = body.IndexOf("Item(\"Add Grid Point to Marker\"", System.StringComparison.Ordinal);
        int addVswr     = body.IndexOf("Item(\"Add Grid Points to VSWR\"", System.StringComparison.Ordinal);
        int lastSep     = body.LastIndexOf("new Separator()", System.StringComparison.Ordinal);
        int remove      = body.IndexOf("Item($\"Remove {marker.Name}\"", System.StringComparison.Ordinal);

        Assert.True(snap >= 0 && addPoint >= 0 && addVswr >= 0 && lastSep >= 0 && remove >= 0);
        Assert.True(snap < addPoint,     "Add Grid Point to Marker must come after Snap to Grid");
        Assert.True(addPoint < addVswr,  "Add Grid Points to VSWR must come directly after Add Grid Point to Marker");
        Assert.True(addVswr < lastSep,   "the separator before Remove must come after both");
        Assert.True(lastSep < remove,    "Remove is the last item");

        // Add Points to VSWR is disabled with a stated reason when the circle itself is off.
        Assert.Contains("enabled: marker.VswrEnabled,", body, System.StringComparison.Ordinal);
        Assert.Contains("Turn on this marker's VSWR circle first.", body, System.StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine([dir!.FullName, .. parts]);
        Assert.True(System.IO.File.Exists(path), $"source not found at {path}");
        return System.IO.File.ReadAllText(path);
    }

    // ══ §5's own gate 3 — the cost of a re-solve after Add Point ═══════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void MeasuredCost_AddPointReSolvesTheWholeGrid_RoughlyTheCostOfAFullGrid()
    {
        // §2.2's own text: "a full re-solve of every point is the honest fallback... solving only the
        // new point... is better if ContourGrid.Build's structure allows it. Either is acceptable —
        // say which you did and what it costs." This chose the honest fallback (no ReuseUnchangedGridPoints
        // — the node SET moved, invalidating the RBF factorization cache by construction anyway, so
        // there is no cheap partial path here the way R-h7-12's single dragged point has). Measured at
        // the shipping default preset (3×12 = 37 points) on a real device — one HB solve per point
        // dominates either way, so 38 points costs about what 37 does, not noticeably more.
        var vm = new HarmonicaViewModel();
        vm.Terminations.Set(TerminationSide.Load, 1, new Complex(80, 10));
        var baseline = new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 256 };

        vm.SolveFrame(baseline); // warm-up — JIT/first-solve costs excluded from the measurement

        var sw = System.Diagnostics.Stopwatch.StartNew();
        vm.SolveFrame(baseline);
        sw.Stop();
        double noAddedMs = sw.Elapsed.TotalMilliseconds;
        int noAddedPoints = vm.Frame.SmithPower.GridPoints.Count;
        output.WriteLine($"full re-solve, no added point: {noAddedMs:F1} ms, " +
                         $"{noAddedPoints} points, {vm.LastSolveCount} HB solves");

        var withAdded = baseline with { AddedGridPoints = [new System.Numerics.Complex(0.3, -0.2)] };
        sw.Restart();
        vm.SolveFrame(withAdded);
        sw.Stop();
        double withAddedMs = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"full re-solve, ONE added point: {withAddedMs:F1} ms, " +
                         $"{vm.Frame.SmithPower.GridPoints.Count} points, {vm.LastSolveCount} HB solves");

        Assert.Equal(noAddedPoints + 1, vm.Frame.SmithPower.GridPoints.Count);
        // Same order of magnitude as an ordinary full re-solve — one extra HB solve on ~38 does not
        // noticeably move the total. Generous bound (3×) rather than a tight one: this is a real HB
        // solve on shared CI hardware, not a synthetic microbenchmark.
        Assert.True(withAddedMs < noAddedMs * 3.0 + 50.0,
            $"expected roughly the cost of a full re-solve, got {withAddedMs:F1} ms vs {noAddedMs:F1} ms baseline");
    }
}
