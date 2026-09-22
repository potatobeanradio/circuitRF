using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Render.Smith;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Smith;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// Overlays — the reference material under the work, added through the Plot Properties inspector
/// (<c>brief-smith-12-overlays-via-the-inspector.md</c> §8; <c>docs/design/smith-chart.md</c> §5.7,
/// §7). <b>One test per claim, and only the claims whose failure would be silent.</b>
/// </summary>
/// <remarks>
/// <b>Every one of these runs with no display.</b> An overlay is an ordinary Data Display
/// <c>Trace</c>, resolved by <c>PlotConfigLoader.LoadTrace</c> and written by
/// <c>DataDisplayViewModel.BuildTraceConfig</c> — the `.cdd`'s own reader and writer — and the
/// persistence is <c>SmithDesignIo</c>'s. What a running Avalonia would add is the pixels.
///
/// <para><b>Three of the brief's ten claims are gated elsewhere and are not repeated here:</b>
/// <c>PlotConfigLoader.LoadPlot</c>'s behaviour is the existing `.cdd` suite run as-is (the
/// extraction is pure, so if any of it had moved the extraction was wrong);
/// <c>SmithRoundThreeTests.OnlyTheUsersOwnDataNamesAnAxis</c> is the axis-label claim, re-pointed at
/// the new path; and <c>PdnImpedanceTests.ThePanelRestylesThisPlotAndCannotReAimIt</c> already
/// asserts, by name, that railRF's impedance plot still refuses <b>Add</b> — which is exactly
/// <c>R-smith12-1</c>'s "a plot with <c>IsFixedReadout</c> and no <c>AllowUserTraces</c>".</para>
/// </remarks>
public sealed class SmithOverlayTests
{
    private const double DesignHz = 2.0e9;
    private const double ChartZ0  = 50.0;

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csmith-ov-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A one-port file referenced to 75 Ω whose S₁₁ is exactly zero.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole point of the renormalization test.</b> S₁₁ = 0 against 75 Ω IS 75 Ω, so
    /// on a 50 Ω chart it belongs at Γ = (75 − 50)/(75 + 50) = <b>0.2</b> exactly. A trace that was
    /// not renormalized draws it at the ORIGIN — the perfect match — which is a plausible-looking
    /// curve in entirely the wrong place.
    /// </remarks>
    private static string Write75OhmS1p(string dir, string name = "part.s1p")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "# HZ S RI R 75\n1.0e9 0.0 0.0\n2.0e9 0.0 0.0\n3.0e9 0.0 0.0\n");
        return path;
    }

    /// <summary>
    /// A one-port whose |Γ| is 3 — far outside the disc, so what it does to the window is visible.
    /// </summary>
    /// <remarks>
    /// <b>The three points are deliberately NOT collinear.</b> <c>Plot.AutoscaleCore</c> skips a
    /// trace whose bounding box is degenerate in both axes, so a fixture repeating one Γ would be
    /// ignored by the autoscale for a reason that has nothing to do with the flag under test.
    /// </remarks>
    private static string WriteFarOutS1p(string dir, string name = "far.s1p")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "# HZ S RI R 50\n1.0e9 3.0 0.0\n2.0e9 0.0 3.0\n3.0e9 -3.0 0.0\n");
        return path;
    }

    private static SmithDesign Design()
    {
        var d = new SmithDesign();
        d.Chart.Z0Ohm             = ChartZ0;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 50.0, 0.0));
        d.Elements.Add(new SmithElement
        {
            Kind = SmithElementKind.L, Placement = SmithPlacement.Series, Name = "L1",
            Values = new SmithElementValues { LHenry = 2e-9 },
        });
        return d;
    }

    /// <summary>One overlay, in the shape the document stores — a Touchstone reference relative to
    /// the document, renormalized to the chart, out of the autoscale.</summary>
    private static System.Text.Json.JsonElement Overlay(string relative, bool renormalize = true,
                                                        bool excludeFromAutoscale = true)
        => SmithOverlays.Write(new TraceConfig
        {
            SourcePath           = relative,
            YAxis                = DependentVarFormat.Complex,
            Z0                   = "50",
            Z0Override           = renormalize,
            ExcludeFromAutoscale = excludeFromAutoscale,
        });

    /// <summary>The window, with one Touchstone in its data-source library and selected — which is
    /// what <b>Add</b> seeds a trace from.</summary>
    private static async Task<SmithChartViewModel> WindowWithSource(SmithDesign design, string dir,
                                                                    string absSource)
    {
        var vm = new SmithChartViewModel(design) { DocumentDirectory = dir };
        await vm.PlotHost.Library!.SelectDataSourceAsync(absSource);
        return vm;
    }

    private static Trace AddedTrace(SmithChartViewModel vm)
    {
        vm.ChartContainer.Inspector.AddTraceCommand.Execute(null);
        return vm.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels);
    }

    // ── 1. R-smith12-4 — the whole feature ───────────────────────────────────

    /// <summary>
    /// <b>A trace added in the inspector survives a component edit</b> — with its colour and its
    /// markers.
    /// </summary>
    /// <remarks>
    /// <c>SmithPlotBuilder.Fill</c> clears the plot's traces and refills them from the design on
    /// every committed edit, which is why brief 5 closed the trace set in the first place. On the old
    /// code this test fails on its second act.
    /// </remarks>
    [Fact]
    public async Task ATraceAddedInTheInspector_SurvivesAComponentEdit()
    {
        string dir = TempDir();
        string abs = Write75OhmS1p(dir);

        var vm    = await WindowWithSource(Design(), dir, abs);
        var trace = AddedTrace(vm);

        trace.Properties.LineWidth = 3.5;
        trace.Markers.Add(new Marker(trace, 2.0, false, false, 1, FreqUnit.GHz));
        vm.HarvestMarkers();

        // An ordinary committed edit, which replaces the whole design through a snapshot.
        vm.ChartZ0Entry = "75 Ω";

        var after = vm.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels);
        Assert.Equal(3.5, after.Properties.LineWidth);
        Assert.Single(after.Markers);

        // And the document carries it, so a save would too.
        Assert.Single(vm.Design.Overlays);
    }

    // ── 2. R-smith12-5a — the same object, not a copy of it ──────────────────

    /// <summary>
    /// <b>…and it is the SAME <c>Trace</c> instance.</b>
    /// </summary>
    /// <remarks>
    /// The inspector's trace cards, its selection and the trace's markers all hold the OBJECT.
    /// Re-resolving every overlay from its config on each <c>Fill</c> leaves every one of them
    /// pointing at a discarded copy — and round three's own marker test did exactly that, removing a
    /// marker from nothing. A test that only compared the trace's SETTINGS would pass on that code.
    /// </remarks>
    [Fact]
    public async Task TheAddedTraceIsTheSameInstanceAcrossARebuild()
    {
        string dir = TempDir();
        string abs = Write75OhmS1p(dir);

        var vm    = await WindowWithSource(Design(), dir, abs);
        var trace = AddedTrace(vm);

        vm.ChartZ0Entry = "75 Ω";

        Assert.Same(trace, vm.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels));
    }

    // ── 3. R-smith12-4a — the full card state, through the UNDO path ─────────

    /// <summary>
    /// <b>A `.csmith` round-trips a trace card's full state, including fields
    /// <c>SmithOverlayRef</c> could not hold.</b>
    /// </summary>
    /// <remarks>
    /// <b>Through <c>SerializeUnvalidated</c> specifically</b>, which is what every committed edit
    /// calls to build the undo snapshot: a round trip that loses a field there loses it on the next
    /// UNDO rather than on the next save, which is a far harder thing to notice.
    ///
    /// <para>Line width, marker glyph and a cube slice are asserted by name because they are three of
    /// the twenty-three properties the old seven-field row had no room for.</para>
    /// </remarks>
    [Fact]
    public void TheCardsFullStateRoundTripsThroughTheUndoSnapshot()
    {
        var design = Design();
        design.Overlays.Add(SmithOverlays.Write(new TraceConfig
        {
            SourcePath            = "meas/dut.s2p",
            Row                   = 1,
            Col                   = 0,
            YAxis                 = DependentVarFormat.Complex,
            Z0                    = "75",
            Z0Override            = true,
            ExcludeFromAutoscale  = true,
            MaximumFractionDigits = 6,
            CubeName              = "SP1.S",
            CubeSlice             = [new AxisSliceConfig { AxisName = "freq", Index = 3 }],
            Properties = new TracePropertiesConfig
            {
                LineEnabled = true, LineWidth = 3.5, LineType = LineType.Dashed,
                MarkerEnabled = true, MarkerType = MarkerType.Diamond, MarkerSize = 4.5,
            },
        }));

        var back = SmithDesignIo.DeserializeUnvalidated(SmithDesignIo.SerializeUnvalidated(design));

        var cfg = SmithOverlays.Read(Assert.Single(back.Overlays));
        Assert.NotNull(cfg);
        Assert.Equal("meas/dut.s2p", cfg!.SourcePath);
        Assert.Equal(1, cfg.Row);
        Assert.Equal("75", cfg.Z0);
        Assert.True(cfg.Z0Override);
        Assert.True(cfg.ExcludeFromAutoscale);
        Assert.Equal(6, cfg.MaximumFractionDigits);
        Assert.Equal("SP1.S", cfg.CubeName);
        Assert.Equal(3, Assert.Single(cfg.CubeSlice).Index);
        Assert.Equal(3.5, cfg.Properties.LineWidth);
        Assert.Equal(LineType.Dashed, cfg.Properties.LineType);
        Assert.Equal(MarkerType.Diamond, cfg.Properties.MarkerType);
    }

    // ── 4. R-smith12-4c — a brief-8 document still opens and draws ───────────

    /// <summary>
    /// <b>A <c>SmithOverlayRef</c>-era `.csmith` opens and draws the same curve</b>, and writes
    /// itself back in the new shape.
    /// </summary>
    /// <remarks>
    /// Nothing shipped carries an overlay, so this is cheap insurance rather than a feature — and the
    /// alternative is a user's own `.csmith` quietly losing its reference data. The Γ asserted is the
    /// one brief 8's own resolver produced for this fixture: 0.2, the closed form for 75 Ω on a 50 Ω
    /// chart.
    /// </remarks>
    [Fact]
    public void ABriefEightDocumentOpensDrawsTheSameCurveAndIsRewritten()
    {
        string dir = TempDir();
        Write75OhmS1p(dir);

        // Hand-written in the old shape, which is the only way it can exist now: the writer has no
        // code for it at all.
        string json = """
        {
          "FormatVersion": 1,
          "Chart": { "Z0Ohm": 50, "DesignFrequencyHz": 2000000000 },
          "Generator": { "Rows": [ { "FrequencyHz": 2000000000, "ResistanceOhm": 50, "ReactanceOhm": 0 } ] },
          "Overlays": [
            { "SourceKind": "TouchstoneFile", "Source": "part.s1p", "Quantity": "S11",
              "Renormalize": true, "Visible": true, "IncludeInAutoscale": false, "Dashed": true }
          ]
        }
        """;

        var design = SmithDesignIo.Deserialize(json);
        Assert.Empty(design.Overlays);                       // not yet migrated
        Assert.Single(design.LegacyOverlays);

        var vm = new SmithChartViewModel(design) { DocumentDirectory = dir };

        var trace = vm.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels);
        Assert.Equal(0.2, trace.Points[0].X, 6);
        Assert.Equal(0.0, trace.Points[0].Y, 6);
        Assert.True(trace.ExcludeFromAutoscale);             // IncludeInAutoscale was false
        Assert.Equal(LineType.Dashed, trace.Properties.LineType);

        // And the old block is gone from what it writes.
        string written = vm.Serialize();
        Assert.DoesNotContain("\"SourceKind\"", written, StringComparison.Ordinal);
        Assert.Contains("\"SourcePath\"", written, StringComparison.Ordinal);
    }

    // ── 5. R-smith12-7 — the one that catches a plausible wrong curve ────────

    /// <summary>
    /// <b>A 75 Ω overlay lands where 50 Ω says it should, and a trace the inspector adds seeds that
    /// way.</b>
    /// </summary>
    /// <remarks>
    /// Renormalization to the chart's Z₀ is a requirement and not an option (<c>R-smith8-3</c>): a
    /// 75 Ω part drawn on a 50 Ω chart without it is a curve in the wrong place that looks entirely
    /// plausible. Both halves are asserted — ON, the point is at Γ = 0.2; OFF, it is at the origin,
    /// which is the file's own number and is what a reader would see and believe.
    /// </remarks>
    [Fact]
    public async Task ASeventyFiveOhmOverlay_LandsWhereFiftyOhmsSaysItShould()
    {
        string dir = TempDir();
        string abs = Write75OhmS1p(dir);

        var on = new SmithChartViewModel(Design()) { DocumentDirectory = dir };
        on.Design.Overlays.Add(Overlay("part.s1p"));
        on.ReloadOverlays(force: true);
        on.RebuildChart();

        var renormalized = on.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels);
        Assert.Equal(0.2, renormalized.Points[0].X, 6);   // (75 − 50) / (75 + 50), exactly
        Assert.Equal(0.0, renormalized.Points[0].Y, 6);

        var off = new SmithChartViewModel(Design()) { DocumentDirectory = dir };
        off.Design.Overlays.Add(Overlay("part.s1p", renormalize: false));
        off.ReloadOverlays(force: true);
        off.RebuildChart();
        Assert.Equal(0.0, off.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels).Points[0].X, 6);

        // And the seed is the ON answer, because the user never asked for the OFF one.
        var added = AddedTrace(await WindowWithSource(Design(), dir, abs));
        Assert.True(added.Z0OverrideEnabled);
        Assert.Equal(ChartZ0, added.Z0.Real, 9);
        Assert.Equal(0.2, added.Points[0].X, 6);
    }

    // ── 6. R-smith12-2 — Add seeds from the library, not from the last trace ─

    /// <summary>
    /// <b><c>Add</c> seeds from the selected source, on a chart whose last trace is the tool's
    /// own.</b>
    /// </summary>
    /// <remarks>
    /// On a Data Display the commonest <b>Add</b> is "another one like the last", and that is right
    /// there. Here the last trace is always one of the tool's — a cube trace whose name is an
    /// element's and whose points were pushed in by the evaluator — so a clone of it is bound to a
    /// cube that exists nowhere and draws a frozen copy of a curve that will not track the design. It
    /// looks like a trace and it is not one, which is why the assertion is on what the added trace is
    /// BOUND to rather than on the count.
    /// </remarks>
    [Fact]
    public async Task AddSeedsFromTheLibraryAndNotFromTheToolsOwnLastTrace()
    {
        string dir = TempDir();
        string abs = Write75OhmS1p(dir);

        var vm = await WindowWithSource(Design(), dir, abs);

        // The last trace before Add is one of the tool's, and it is the one a clone would copy.
        var last = vm.ChartPlot.Traces.Last();
        Assert.True(last.ExcludeFromAxisLabels);

        var added = AddedTrace(vm);
        Assert.NotEqual(last.CubeName, added.CubeName);
        Assert.Equal(abs, added.SourceRef);
        Assert.NotEmpty(added.Points);

        // R-smith12-7: the user's own data is what names an axis, so the flag must stay CLEAR.
        Assert.False(added.ExcludeFromAxisLabels);
    }

    // ── 7. R-smith12-1 — the flag is split, and only the set is opened ───────

    /// <summary>
    /// <b>The trace set is open and the plot type is not.</b>
    /// </summary>
    /// <remarks>
    /// <c>Plot.IsFixedReadout</c> means two things; this chart wants only the second undone. The
    /// per-card half is the other half of the same rule: a card for one of the tool's OWN traces
    /// offers no trash and no data pickers, because removing it would remove it until the next
    /// keystroke and re-aiming it would be undone by the next rebuild.
    /// </remarks>
    [Fact]
    public async Task TheTraceSetIsOpenAndThePlotTypeIsNot()
    {
        string dir = TempDir();
        string abs = Write75OhmS1p(dir);

        var vm        = await WindowWithSource(Design(), dir, abs);
        var inspector = vm.ChartContainer.Inspector;

        Assert.True(vm.ChartPlot.IsFixedReadout);
        Assert.True(vm.ChartPlot.AllowUserTraces);
        Assert.True(inspector.CanEditTraceSet);
        Assert.False(inspector.CanChangePlotType);

        AddedTrace(vm);

        var ours    = inspector.Traces.Single(c => c.Trace.ExcludeFromAxisLabels == false);
        var toolsOwn = inspector.Traces.First(c => c.Trace.ExcludeFromAxisLabels);

        Assert.True(ours.CanRemove);
        Assert.True(ours.CanPickTraceData);
        Assert.False(toolsOwn.CanRemove);
        Assert.False(toolsOwn.CanPickTraceData);
    }

    // ── 8. R-smith12-5c — the marker key survives a reorder ──────────────────

    /// <summary>
    /// <b>A marker taken on an overlay is stored against a name that does not move when the overlay
    /// list is reordered.</b>
    /// </summary>
    /// <remarks>
    /// Markers are stored against a trace's LABEL rather than its index, because an index moves the
    /// moment an element is deleted or an overlay is reordered — and a marker that silently slid onto
    /// the next curve would be a reading reported against the wrong thing. The key is therefore
    /// derived from the overlay's own config and from nothing else.
    /// </remarks>
    [Fact]
    public void TheMarkerKeyIsTheOverlaysOwnAndDoesNotMoveOnAReorder()
    {
        var a = new TraceConfig { SourcePath = "meas/dut.s2p", Row = 1, Col = 0 };
        var b = new TraceConfig { SourcePath = "ref/part.s1p" };

        Assert.Equal("dut.s2p S21", SmithOverlays.Key(a));
        Assert.Equal("part.s1p S11", SmithOverlays.Key(b));

        // Two configs with the same content are the same key wherever they sit in the list.
        Assert.Equal(SmithOverlays.Key(a),
                     SmithOverlays.Key(SmithOverlays.Read(SmithOverlays.Write(a))!));
    }

    // ── 9. R-smith8-2 — a reference that does not resolve ────────────────────

    /// <summary>
    /// <b>An overlay whose file is missing says why, does not stop the document, and is not thrown
    /// away.</b>
    /// </summary>
    /// <remarks>
    /// That is the requirement separating an overlay from an S1P ELEMENT: an element's missing file
    /// is a refusal because the cascade cannot be walked without it; an overlay's costs the user a
    /// comparison and nothing else.
    ///
    /// <para><b>The last assertion is the one with teeth.</b> An unresolved overlay has no trace on
    /// the plot, so a harvest that wrote back only what it could see would DELETE a user's reference
    /// material because the file it names happened to be on a disk that was not mounted.</para>
    /// </remarks>
    [Fact]
    public void AnUnresolvableOverlaySaysWhy_AndIsNotDiscardedByTheNextHarvest()
    {
        string dir = TempDir();
        Write75OhmS1p(dir);

        var design = Design();
        design.Overlays.Add(Overlay("part.s1p"));
        design.Overlays.Add(Overlay("no-such-part.s2p"));

        var vm = new SmithChartViewModel(design) { DocumentDirectory = dir };

        Assert.Null(vm.Refusal);                                       // the document is fine
        Assert.True(vm.HasUnresolvedOverlays);
        Assert.Contains("no-such-part.s2p", vm.UnresolvedOverlayNote);
        Assert.Contains("no-such-part.s2p", vm.StripNotice);

        // The one that DID resolve is on the chart; the other is not.
        var drawn = Assert.Single(vm.ChartPlot.Traces, t => !t.ExcludeFromAxisLabels);
        Assert.Equal("part.s1p", drawn.SourceRef);

        vm.HarvestOverlays();
        Assert.Equal(2, vm.Design.Overlays.Count);
    }

    // ── 10. R-smith12-6 — out of the autoscale, and the card can say otherwise ─

    /// <summary>
    /// <b>An overlay does not reframe the chart, and does when its card says so.</b>
    /// </summary>
    /// <remarks>
    /// §5.7's reason is specific: a stability circle can be enormous — an unconditionally stable
    /// device's load circle routinely sits far outside the unit disc — and one unlucky overlay
    /// reframing the window would squash the cascade the user is working on into a corner of it.
    /// Brief 8 had this as a column on a panel; it is now a field on <c>TraceConfig</c> and a
    /// checkbox on the trace card, which is also what makes it reach a `.cdd`.
    /// </remarks>
    [Fact]
    public void AnOverlayStaysOutOfTheAutoscale_UnlessItsCardSaysOtherwise()
    {
        string dir = TempDir();
        WriteFarOutS1p(dir);

        var excluded = new SmithChartViewModel(Design()) { DocumentDirectory = dir };
        excluded.Design.Overlays.Add(Overlay("far.s1p"));
        excluded.ReloadOverlays(force: true);
        excluded.RebuildChart();
        double unitDisc = excluded.ChartPlot.Axes.Window.Width;

        var included = new SmithChartViewModel(Design()) { DocumentDirectory = dir };
        included.Design.Overlays.Add(Overlay("far.s1p", excludeFromAutoscale: false));
        included.ReloadOverlays(force: true);
        included.RebuildChart();

        Assert.True(included.ChartPlot.Axes.Window.Width > unitDisc * 2,
                    "an included overlay at |Γ| = 3 should open the window well past the disc: "
                  + $"{unitDisc} → {included.ChartPlot.Axes.Window.Width}");

        // And the card's checkbox is the control for it, in both directions.
        var card = excluded.ChartContainer.Inspector.Traces.Single(c => !c.Trace.ExcludeFromAxisLabels);
        Assert.False(card.IncludeInAutoscale);
        card.IncludeInAutoscale = true;
        Assert.False(card.Trace.ExcludeFromAutoscale);
    }

    // ── 11. R-smith8-2 — a relative reference survives a moved document ──────

    /// <summary>
    /// <b>Write the pair, move them both, reopen: the overlay still resolves.</b>
    /// </summary>
    /// <remarks>
    /// That is the whole reason the reference is relative to the DOCUMENT rather than absolute — an
    /// archived or moved workspace is the ordinary case, and an absolute path in a `.csmith` would
    /// resolve on one machine and nowhere else. <b>It survives the change of writer</b>: the config
    /// is written by the `.cdd`'s own <c>BuildTraceConfig</c>, which spells a reference relative to
    /// the results ROOT and cannot reach a file sitting beside a scratch document.
    /// </remarks>
    [Fact]
    public async Task ARelativeOverlayReference_SurvivesAMovedDocument()
    {
        string first = TempDir();
        Directory.CreateDirectory(Path.Combine(first, "data"));
        string abs = Write75OhmS1p(Path.Combine(first, "data"));

        var vm = await WindowWithSource(Design(), first, abs);
        AddedTrace(vm);

        string csmith = Path.Combine(first, "match.csmith");
        File.WriteAllText(csmith, vm.Serialize());

        // The reference is RELATIVE, which is what makes the move work at all.
        var stored = SmithOverlays.Read(Assert.Single(vm.Design.Overlays));
        Assert.Equal("data/part.s1p", stored!.SourcePath);

        // Move the PAIR — the document and the folder beside it — as an archive would.
        string second = TempDir();
        Directory.Move(Path.Combine(first, "data"), Path.Combine(second, "data"));
        File.Move(csmith, Path.Combine(second, "match.csmith"));

        var reopened = SmithDesignIo.LoadFromFile(Path.Combine(second, "match.csmith"));
        var vm2      = new SmithChartViewModel(reopened) { DocumentDirectory = second };

        Assert.False(vm2.HasUnresolvedOverlays);

        // And it is the SAME curve, in the same place — a reference that resolved to a different
        // file would pass an "is it marked" test and fail the user.
        var trace = vm2.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels);
        Assert.Equal(0.2, trace.Points[0].X, 6);
    }

    // ── 12. R-smith8-6 — the VSWR circle is NOT centred on the marker ────────

    /// <summary>
    /// <b>A constant-VSWR circle about an off-centre marker is <c>VswrLocus</c>'s circle.</b>
    /// </summary>
    /// <remarks>
    /// The correction <c>vswr-locus-gamma-plane.md</c> records and <c>HarmonicaVswrHandle</c>'s
    /// header repeats: "the matched point" in that derivation means Γ = 0 specifically, not "wherever
    /// the marker happens to be". A marker at (0.3, −0.2) with VSWR 3 has its true centre at about
    /// (0.23, −0.16) — far outside any grab tolerance — and the CENTRE is asserted because that is
    /// where the wrong version differs visibly.
    /// </remarks>
    [Fact]
    public void AVswrCircleAboutAnOffCentreMarker_IsNotCentredOnTheMarker()
    {
        var marker = new Complex(0.3, -0.2);
        var (centre, radius) = SmithMarkerBridge.VswrCircle(marker, vswr: 3.0, z0: ChartZ0);

        Assert.Equal(0.23,  centre.Real,      2);
        Assert.Equal(-0.16, centre.Imaginary, 2);

        Assert.True((centre - marker).Magnitude > 0.05,
                    $"the centre {centre} is suspiciously close to the marker {marker}; "
                  + "a circle centred on the marker is the documented mistake.");

        // At Γ = 0 the two DO coincide, which is what the derivation actually says.
        var (atOrigin, rho) = SmithMarkerBridge.VswrCircle(Complex.Zero, vswr: 3.0, z0: ChartZ0);
        Assert.Equal(0.0, atOrigin.Magnitude, 9);
        Assert.Equal(0.5, rho, 9);                            // (3 − 1) / (3 + 1)

        // And the answer is the locus's own, not a reconstruction: every sample sits on it.
        var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
            marker, 3.0, RfCore.Loadpull.SurfacePlane.Gamma, new Complex(ChartZ0, 0.0));
        foreach (var p in pts)
            Assert.Equal(radius, (p - centre).Magnitude, 9);
    }

    // ── 13. R-smith8-5 — markers on an overlay round-trip ────────────────────

    /// <summary>
    /// <b>A marker placed on an overlay survives a save, a reload and every rebuild in between.</b>
    /// </summary>
    /// <remarks>
    /// The rebuild is the part that is easy to miss: every trace on this chart is thrown away and
    /// rebuilt on each edit, so the markers on them go too. The document is therefore the authority
    /// and the plot is re-populated from it — which is also what makes an undo restore a marker
    /// rather than only the numbers.
    /// </remarks>
    [Fact]
    public void MarkersOnAnOverlayRoundTripThroughTheCsmith()
    {
        string dir = TempDir();
        Write75OhmS1p(dir);

        var design = Design();
        design.Overlays.Add(Overlay("part.s1p"));

        var vm      = new SmithChartViewModel(design) { DocumentDirectory = dir };
        var overlay = vm.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels);

        overlay.Markers.Add(new Marker(overlay, 2.0, false, false, 1, FreqUnit.GHz)
        {
            VswrEnabled = true, VswrValue = 3.0, Style = MarkerStyle.Large,
        });
        vm.HarvestMarkers();

        var stored = Assert.Single(vm.Design.Markers);
        Assert.Equal("part.s1p S11", stored.TraceName);
        Assert.True(stored.VswrEnabled);

        // A REBUILD must not lose it — every trace the TOOL owns was just replaced.
        vm.RebuildChart();
        Assert.Single(vm.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels).Markers);

        // And so must a save and a reload, onto the same curve.
        var vm2 = new SmithChartViewModel(SmithDesignIo.Deserialize(vm.Serialize()))
        { DocumentDirectory = dir };

        var one = Assert.Single(vm2.ChartPlot.Traces.Single(t => !t.ExcludeFromAxisLabels).Markers);
        Assert.True(one.VswrEnabled);
        Assert.Equal(3.0, one.VswrValue);
        Assert.Equal(MarkerStyle.Large, one.Style);
    }

    // ── 14. §8 — the panel is gone ───────────────────────────────────────────

    /// <summary>
    /// <b>No Overlays panel survives anywhere in <c>src/Ui</c>.</b>
    /// </summary>
    /// <remarks>
    /// A panel left in place beside the inspector is two authors of one list, and the two would
    /// disagree the first time either changed. The scan is over the SOURCE because that is where a
    /// half-removed feature hides: a view model with no view, a command nothing binds.
    /// </remarks>
    [Fact]
    public void TheOverlaysPanelIsGone()
    {
        string ui = SourceRoot("src/Ui");

        foreach (string name in new[] { "SmithOverlayRowViewModel", "AddOverlayCommand",
                                        "RemoveOverlayCommand", "OverlayRows" })
        {
            var hits = Directory
                .EnumerateFiles(ui, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                .Where(f => File.ReadAllText(f).Contains(name, StringComparison.Ordinal))
                .ToList();

            Assert.True(hits.Count == 0,
                        $"'{name}' is brief 8's Overlays panel and must be gone; found in "
                      + string.Join(", ", hits.Select(Path.GetFileName)));
        }
    }

    private static string SourceRoot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
