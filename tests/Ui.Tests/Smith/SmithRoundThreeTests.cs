using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Smith;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The third round of owner items over the finished tool (<c>docs/design/smith-chart.md</c> §9.3).
/// <b>One test per claim</b>, and only the claims whose failure would be silent.
/// </summary>
/// <remarks>
/// The items with no test here are the ones whose evidence is a pixel or a menu: the toolbar's Q
/// glyph, the generator panel's width, the larger component glyphs on the Add and Insert rows, the
/// order of the Conjugate and Import buttons, the flat Add Marker row, and the removal of the
/// constant-Q hover ring. Each is one expression in the AXAML or one branch in
/// <c>PlotControl.RefreshAddMarkerSubmenu</c>, and each fails the moment the window is opened.
/// </remarks>
public sealed class SmithRoundThreeTests
{
    private const double DesignHz = 2.0e9;

    /// <summary>The repository's own `testdata/smith` fixture, found by walking up from the test
    /// binary — <c>SmithDocumentTests</c>' own helper, so one spelling of the path.</summary>
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "testdata", "smith", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"testdata/smith/{name} is not above {AppContext.BaseDirectory}.");
    }

    /// <summary>A generator with a REACTANCE, which is the whole point: a real Z_gen is its own
    /// conjugate and could not tell the two placements apart.</summary>
    private static SmithDesign Design()
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = 50.0;
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 12.0, -8.5));
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
            Values = { LHenry = 3.3e-9 },
        });
        return d;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  1. The generator glyph is at the generator, not at its conjugate
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The glyph sits at Γ(Z_gen) — the impedance the table states.</b>
    /// </summary>
    /// <remarks>
    /// Owner report, 2026-09-19. It used to be drawn at the conjugate-match TARGET, Γ(conj(Z_gen)),
    /// which is the mirror of this point about the real axis — a perfectly plausible position, on
    /// the chart, at the right magnitude, with the wrong sign on its reactance. Nothing about the
    /// picture says which one it is, which is why this is a test rather than a look.
    ///
    /// <para>The negative half is the point of the assertion: with a real Z_gen the two answers
    /// coincide, so the fixture's generator carries −8.5 Ω of reactance.</para>
    /// </remarks>
    [Fact]
    public void TheGeneratorGlyphIsAtTheGeneratorAndNotItsConjugate()
    {
        var design = Design();
        var scene  = SmithPlotBuilder.BuildScene(design, null, (600.0, 600.0), null);

        var g = Assert.Single(scene.GeneratorPoints);

        var zGen     = new Complex(12.0, -8.5);
        var expected = SmithCascade.Gamma(zGen, 50.0);

        Assert.Equal(expected.Real,      g.Real,      12);
        Assert.Equal(expected.Imaginary, g.Imaginary, 12);

        // …and it is NOT the conjugate, which is where it used to be.
        var mirror = SmithCascade.Gamma(Complex.Conjugate(zGen), 50.0);
        Assert.True((g - mirror).Magnitude > 1e-6,
            $"the glyph landed on the conjugate target {mirror} rather than on Γ(Z_gen).");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. Only the user's own data names an axis
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The tool's derived traces put nothing on an axis; an overlay does.</b>
    /// </summary>
    /// <remarks>
    /// Two owner reports, 2026-09-19, and one mechanism: a chart pasted into a presentation carried
    /// a column of Y-axis labels the window had never shown, and the chart itself carried a stack of
    /// <c>freq (a to b)</c> X rows. Both are per-TRACE, and this plot has a dozen traces nobody asked
    /// for by name — one per cascade element, the load points, the generator points, the band, the
    /// arcs.
    ///
    /// <para>The Y half never drew on screen at all: this window hosts a bare <c>PlotControl</c>
    /// rather than a <c>PlotContainerView</c>, so the strips only ever appeared in a copy or an
    /// export. That is exactly the failure a test is for — the surface that shows it is not the
    /// surface anybody looks at while working.</para>
    ///
    /// <para><b>The overlay is the half that keeps this honest.</b> A rule that silenced every trace
    /// would pass the first assertion and be wrong: the labels exist to name the reference data the
    /// user chose, and that data still has to be named.</para>
    /// </remarks>
    [Fact]
    public void OnlyTheUsersOwnDataNamesAnAxis()
    {
        var design = Design();
        var vm     = new SmithChartViewModel(design);

        Assert.All(vm.ChartPlot.Traces, t => Assert.True(t.ExcludeFromAxisLabels,
            $"'{t.CubeName}' is derived by the tool and must name no axis."));

        var (left, right) = PlotLabelStrips.For(vm.ChartPlot, showFilePrefix: false);
        Assert.Empty(left);
        Assert.Empty(right);

        // An overlay is the user's own reference data and DOES name an axis. Resolved through the
        // same call the chart makes, so this is the trace the chart would carry.
        string s1p   = Fixture("generator-3pt.s1p");
        var    trace = SmithOverlays.Load(
            new TraceConfig { SourcePath = s1p, YAxis = DependentVarFormat.Complex },
            new SmithDocumentSources(System.IO.Path.GetDirectoryName(s1p)));

        Assert.NotNull(trace);
        Assert.False(trace!.ExcludeFromAxisLabels);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. A removed marker stays removed
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A marker taken off its trace and harvested does not come back on the next rebuild.</b>
    /// </summary>
    /// <remarks>
    /// Owner report, 2026-09-19 — a marker deleted from its context menu reappeared the moment a
    /// component value changed. The document is the authority for the marker set (see
    /// <c>SmithChartViewModel.HarvestMarkers</c>), and every trace is rebuilt from the design on each
    /// edit, so a removal that never reached the document is undone by the very next keystroke.
    ///
    /// <para>The REBUILD is the assertion. Checking only that the marker left the trace would pass on
    /// the broken code, because the removal itself always worked — what did not was writing it
    /// down.</para>
    /// </remarks>
    [Fact]
    public void AMarkerRemovedFromItsTraceDoesNotReturnOnTheNextRebuild()
    {
        var vm    = new SmithChartViewModel(Design());
        var trace = vm.ChartPlot.Traces.First(t => !t.IsAnnotation && t.Points.Count > 1);

        trace.Markers.Add(new Marker(trace, 0.0, false, false, 1)
        {
            FreePosition = true, PositionStatic = new Vector2(0.37f, -0.21f),
        });
        vm.HarvestMarkers();
        Assert.Single(vm.Design.Markers);

        // THE LIVE TRACE, looked up again. The harvest above pushed an undo entry, which rebuilt the
        // chart, which REPLACED every trace on it — the marker was re-attached to a new object and
        // the one captured above is a discarded copy. That is the same trap the tool itself has to
        // survive, and a test holding the stale reference would remove a marker from nothing and
        // then pass for the wrong reason.
        var live = vm.ChartPlot.Traces.Single(t => t.Markers.Count > 0);
        live.Markers.RemoveAt(0);
        vm.HarvestMarkers();

        Assert.Empty(vm.Design.Markers);

        vm.RebuildChart();
        Assert.Empty(vm.ChartPlot.Traces.SelectMany(t => t.Markers));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. Escape drops both selections
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Escape clears the network strip's element selection.</b>
    /// </summary>
    /// <remarks>
    /// The marker half of the same command is covered by the state it reads — an info box's
    /// <c>IsSelected</c> — which needs the display's own box layer to exist. What is worth a gate
    /// here is that the command clears BOTH and not only the one the key was pressed over: a
    /// half-done clear leaves a selected element the next Delete would remove.
    /// </remarks>
    [Fact]
    public void EscapeClearsTheElementSelection()
    {
        var vm = new SmithChartViewModel(Design());
        vm.SelectElement(0);
        Assert.True(vm.HasSelectedElement);

        vm.ClearSelectionCommand.Execute(null);
        Assert.False(vm.HasSelectedElement);
        Assert.Equal(-1, vm.SelectedElementIndex);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. A frequency label hangs away from the rest of the cluster
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Of two load points, the upper one's label goes above it and the lower one's below.</b>
    /// </summary>
    /// <remarks>
    /// The owner's own rule, stated in canvas coordinates: with the other point below this one, this
    /// one's label goes above. The old placement fanned the stubs radially outward from the centre of
    /// the chart, which spaces the labels from EACH OTHER but says nothing about where the other load
    /// points are — and a locus running outward from the centre puts every label straight over the
    /// next point along it.
    ///
    /// <para>Canvas Y grows DOWNWARD, which is the part that is easy to get backwards and impossible
    /// to see in a screenshot of a chart whose points happen to be nearly level.</para>
    /// </remarks>
    [Fact]
    public void ALabelHangsAwayFromTheOtherLoadPoints()
    {
        // Two points: index 0 is higher on screen (smaller Y).
        Assert.Equal(-1f, SmithChartChrome.LabelDirection([100f, 260f], 0));
        Assert.Equal(+1f, SmithChartChrome.LabelDirection([100f, 260f], 1));
    }
}
