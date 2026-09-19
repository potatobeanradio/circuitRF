using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.Smith;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// Overlays and markers — the reference material under the work
/// (<c>brief-smith-8-overlays-markers.md</c> §3; <c>docs/design/smith-chart.md</c> §5.7, §4.5).
/// <b>One test per claim.</b>
/// </summary>
/// <remarks>
/// <b>Every one of these runs with no display</b>, which is the same property brief 5's gate has and
/// for the same reason: an overlay is a <c>Trace</c> the existing machinery resolves and draws, a
/// marker is the Data Display's own object, and the persistence is <c>SmithDesignIo</c>'s. What a
/// running Avalonia would add is the pixels.
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
    /// curve in entirely the wrong place and is exactly the failure <c>R-smith8-3</c> exists to
    /// prevent.
    /// </remarks>
    private static string Write75OhmS1p(string dir, string name = "part.s1p")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "# HZ S RI R 75\n1.0e9 0.0 0.0\n2.0e9 0.0 0.0\n3.0e9 0.0 0.0\n");
        return path;
    }

    /// <summary>
    /// A 50 Ω two-port whose load stability circle has round numbers:
    /// S = [0.7, 0.2; 3.0, 0.5], so Δ = 0.35 − 0.6 = −0.25 and
    /// |S₂₂|² − |Δ|² = 0.25 − 0.0625 = 0.1875.
    /// </summary>
    private static string WriteUnstableS2p(string dir, string name = "device.s2p")
    {
        string path = Path.Combine(dir, name);
        // Touchstone 2-port row order is S11 S21 S12 S22.
        File.WriteAllText(path,
            "# HZ S RI R 50\n"
          + "1.0e9 0.7 0.0 3.0 0.0 0.2 0.0 0.5 0.0\n"
          + "2.0e9 0.7 0.0 3.0 0.0 0.2 0.0 0.5 0.0\n"
          + "3.0e9 0.7 0.0 3.0 0.0 0.2 0.0 0.5 0.0\n");
        return path;
    }

    /// <summary>
    /// A one-port whose |Γ| is 3 — far outside the disc, so what it does to the window is visible.
    /// </summary>
    /// <remarks>
    /// <b>The three points are deliberately NOT collinear.</b> <c>Plot.AutoscaleCore</c> skips a
    /// trace whose bounding box is degenerate in both axes, so a fixture repeating one Γ would be
    /// ignored by the autoscale for a reason that has nothing to do with the flag under test — and
    /// the test would pass whatever the flag said.
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
        d.Chart.DesignFrequencyHz = DesignHz;
        d.Generator.Rows.Add(new SmithGeneratorRow(DesignHz, 50.0, 0.0));
        return d;
    }

    private static SmithOverlayRef Overlay(string relative, string quantity = "S11",
                                        string derived = "None", bool renormalize = true)
        => new()
        {
            SourceKind  = SmithOverlaySource.TouchstoneFile,
            Source      = relative,
            Quantity    = quantity,
            Derived     = derived,
            Renormalize = renormalize,
        };

    private static SmithOverlayResolver.Resolution Resolve(
        SmithOverlayRef overlay, string? dir, double z0 = ChartZ0)
        => SmithOverlayResolver.Resolve(overlay, dir, z0, sources: null, colorIndex: 1);

    // ── 1. R-smith8-3 — the renormalization, with a hand-computed point ──────

    /// <summary>
    /// <b>A 75 Ω Touchstone overlay lands where 50 Ω says it should.</b>
    /// </summary>
    /// <remarks>
    /// The one test that catches a plausible-looking wrong curve. Both halves are asserted: ON, the
    /// point is at Γ = 0.2, the closed-form answer for 75 Ω on a 50 Ω chart; OFF, it is at the
    /// origin, which is the file's own number and is what a reader would see and believe.
    /// </remarks>
    [Fact]
    public void SeventyFiveOhmOverlay_IsRenormalizedToTheChartsZ0()
    {
        string dir = TempDir();
        Write75OhmS1p(dir);

        var on = Resolve(Overlay("part.s1p"), dir);
        Assert.Null(on.Unresolved);
        Assert.NotNull(on.Trace);

        // Six places, not more: Trace.Points is Vector2 and the numbers are single precision. The
        // distinction this test is about is 0.2 versus 0.0.
        var point = on.Trace!.Points[0];
        Assert.Equal(0.2, point.X, 6);      // (75 − 50) / (75 + 50), exactly
        Assert.Equal(0.0, point.Y, 6);

        // And OFF is the file's own numbers — the perfect match it is not.
        var off = Resolve(Overlay("part.s1p", renormalize: false), dir);
        Assert.NotNull(off.Trace);
        Assert.Equal(0.0, off.Trace!.Points[0].X, 6);
        Assert.Equal(0.0, off.Trace!.Points[0].Y, 6);
    }

    // ── 2. R-smith8-1 — stability circles through the EXISTING derived path ──

    /// <summary>
    /// <b>A stability-circle overlay resolves through <c>DerivedParameters</c> and produces the
    /// published centre and radius.</b>
    /// </summary>
    /// <remarks>
    /// The oracle is the textbook closed form written out here independently —
    /// C_L = conj(S₂₂ − Δ·conj(S₁₁)) / (|S₂₂|² − |Δ|²), r_L = |S₁₂S₂₁| / ‖S₂₂|² − |Δ|²|. The fixture
    /// is chosen so both are exact: C_L = 0.675/0.1875 = 3.6 and r_L = 0.6/0.1875 = 3.2.
    ///
    /// <para>What this actually gates is that <b>no second stability computation was written</b>:
    /// the overlay sets <c>Trace.Derived</c> and <c>BuildDerivedPath</c> — the Data Display's own —
    /// fills the circle lists.</para>
    /// </remarks>
    [Fact]
    public void StabilityCircleOverlay_ResolvesThroughTheExistingDerivedPath()
    {
        string dir = TempDir();
        WriteUnstableS2p(dir);

        var resolved = Resolve(Overlay("device.s2p", derived: "LoadStabilityCircle"), dir);
        Assert.Null(resolved.Unresolved);

        var trace = resolved.Trace!;
        Assert.Equal(DerivedParameters.LoadStabilityCircle, trace.Derived);
        Assert.True(trace.IsStabilityCircle);
        Assert.NotEmpty(trace.StabilityCircleCentres);

        Assert.Equal(3.6, trace.StabilityCircleCentres[0].X, 6);
        Assert.Equal(0.0, trace.StabilityCircleCentres[0].Y, 6);
        Assert.Equal(3.2, trace.StabilityCircleRadii[0],     6);

        // And the source circle, from the same fixture through the same path:
        //   C_S = conj(S11 − Δ·conj(S22)) / (|S11|² − |Δ|²) = (0.7 + 0.125) / (0.49 − 0.0625)
        var source = Resolve(Overlay("device.s2p", derived: "SourceStabilityCircle"), dir);
        Assert.Equal(0.825 / 0.4275, source.Trace!.StabilityCircleCentres[0].X, 6);
        Assert.Equal(0.6   / 0.4275, source.Trace!.StabilityCircleRadii[0],     6);
    }

    // ── 3. R-smith8-2 — an unresolvable reference marks its row ──────────────

    /// <summary>
    /// <b>A reference that does not resolve marks its row with the path in its tooltip, and the
    /// document still opens with everything else drawn.</b>
    /// </summary>
    /// <remarks>
    /// This is the requirement that separates an overlay from an S1P ELEMENT. An element's missing
    /// file is a refusal because the cascade cannot be walked without it; an overlay's costs the
    /// user a comparison and nothing else, so the chart must carry on.
    /// </remarks>
    [Fact]
    public void AnUnresolvableOverlay_MarksItsRowAndDoesNotStopTheDocument()
    {
        string dir = TempDir();
        Write75OhmS1p(dir);

        var design = Design();
        design.Overlays.Add(Overlay("part.s1p"));
        design.Overlays.Add(Overlay("no-such-part.s2p"));

        var vm = new SmithChartViewModel(design) { DocumentDirectory = dir };

        Assert.Null(vm.Refusal);                                   // the document is fine
        Assert.Equal(2, vm.OverlayRows.Count);

        Assert.False(vm.OverlayRows[0].IsUnresolved);
        Assert.True(vm.OverlayRows[1].IsUnresolved);
        Assert.Contains("no-such-part.s2p", vm.OverlayRows[1].Unresolved);

        // The one that DID resolve is still on the chart. Everything else on it is the cascade's.
        Assert.Contains(vm.ChartPlot.Traces, t => t.SourceRef == "part.s1p");
        Assert.DoesNotContain(vm.ChartPlot.Traces, t => t.SourceRef == "no-such-part.s2p");
    }

    // ── 4. R-smith8-2 — a relative path survives a moved document ────────────

    /// <summary>
    /// <b>Write the pair, move them both, reopen: the overlay still resolves.</b>
    /// </summary>
    /// <remarks>
    /// That is the whole reason the reference is relative to the DOCUMENT rather than absolute — an
    /// archived or moved workspace is the ordinary case, and an absolute path in a `.csmith` would
    /// resolve on one machine and nowhere else.
    /// </remarks>
    [Fact]
    public void ARelativeOverlayReference_SurvivesAMovedDocument()
    {
        string first = TempDir();
        Directory.CreateDirectory(Path.Combine(first, "data"));
        Write75OhmS1p(Path.Combine(first, "data"));

        var design = Design();
        design.Overlays.Add(Overlay(Path.Combine("data", "part.s1p")));

        string csmith = Path.Combine(first, "match.csmith");
        File.WriteAllText(csmith, SmithDesignIo.Serialize(design));

        // Move the PAIR — the document and the folder beside it — as an archive would.
        string second = TempDir();
        Directory.Move(Path.Combine(first, "data"), Path.Combine(second, "data"));
        File.Move(csmith, Path.Combine(second, "match.csmith"));

        var reopened = SmithDesignIo.LoadFromFile(Path.Combine(second, "match.csmith"));
        var vm       = new SmithChartViewModel(reopened) { DocumentDirectory = second };

        Assert.False(vm.OverlayRows[0].IsUnresolved);

        // And it is the SAME curve, in the same place — a reference that resolved to a different
        // file would pass an "is it marked" test and fail the user.
        var trace = vm.ChartPlot.Traces.Single(t => t.SourceRef == Path.Combine("data", "part.s1p"));
        Assert.Equal(0.2, trace.Points[0].X, 6);
    }

    // ── 5. R-smith8-4 — out of the autoscale unless the row says otherwise ───

    /// <summary>
    /// <b>An overlay does not reframe the chart, and does when its row says so.</b>
    /// </summary>
    /// <remarks>
    /// A stability circle can be enormous — this fixture's load circle is centred at 3.6 with radius
    /// 3.2 — and one unlucky overlay reframing the window would squash the cascade the user is
    /// working on into a corner of it.
    /// </remarks>
    [Fact]
    public void OverlaysAreOutOfTheAutoscale_UnlessTheRowSaysOtherwise()
    {
        string dir = TempDir();
        WriteFarOutS1p(dir);

        var excluded = Resolve(Overlay("far.s1p"), dir);
        Assert.True(excluded.Trace!.ExcludeFromAutoscale);

        var included = new SmithOverlayRef
        {
            Source             = "far.s1p",
            Quantity           = "S11",
            IncludeInAutoscale = true,
        };
        Assert.False(Resolve(included, dir).Trace!.ExcludeFromAutoscale);

        // And the flag reaches the window rather than only the field.
        var design = Design();
        design.Overlays.Add(Overlay("far.s1p"));
        var vm = new SmithChartViewModel(design) { DocumentDirectory = dir };
        double unitDisc = vm.ChartPlot.Axes.Window.Width;

        var design2 = Design();
        design2.Overlays.Add(new SmithOverlayRef
        {
            Source = "far.s1p", Quantity = "S11", IncludeInAutoscale = true,
        });
        var vm2 = new SmithChartViewModel(design2) { DocumentDirectory = dir };

        Assert.True(vm2.ChartPlot.Axes.Window.Width > unitDisc * 2,
                    $"an included overlay at |Γ| = 3 should open the window well past the disc: "
                  + $"{unitDisc} → {vm2.ChartPlot.Axes.Window.Width}");
    }

    // ── 6. R-smith8-6 — the VSWR circle is NOT centred on the marker ─────────

    /// <summary>
    /// <b>A constant-VSWR circle about an off-centre marker is <c>VswrLocus</c>'s circle.</b>
    /// </summary>
    /// <remarks>
    /// The correction <c>vswr-locus-gamma-plane.md</c> records and <c>HarmonicaVswrHandle</c>'s
    /// header repeats: "the matched point" in that derivation means Γ = 0 specifically, not
    /// "wherever the marker happens to be". A marker at (0.3, −0.2) with VSWR 3 has its true centre
    /// at about (0.23, −0.16) — far outside any grab tolerance — and the CENTRE is asserted because
    /// that is where the wrong version differs visibly.
    /// </remarks>
    [Fact]
    public void AVswrCircleAboutAnOffCentreMarker_IsNotCentredOnTheMarker()
    {
        var marker = new Complex(0.3, -0.2);
        var (centre, radius) = SmithMarkerBridge.VswrCircle(marker, vswr: 3.0, z0: ChartZ0);

        Assert.Equal(0.23,  centre.Real,      2);
        Assert.Equal(-0.16, centre.Imaginary, 2);

        // The wrong answer — a circle of radius ρ = (V−1)/(V+1) centred on the marker — is the one
        // this test exists to keep out. It is off by far more than any grab tolerance.
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

    // ── 7. R-smith8-5 — markers round-trip through the `.csmith` ─────────────

    /// <summary>
    /// <b>A marker placed on a curve survives a save, a reload and every rebuild in between, in the
    /// Data Display's own shape.</b>
    /// </summary>
    /// <remarks>
    /// The rebuild is the part that is easy to miss: every trace on this chart is thrown away and
    /// rebuilt on each edit, so the markers on them go too. The document is therefore the authority
    /// and the plot is re-populated from it — which is also what makes an undo restore a marker
    /// rather than only the numbers.
    /// </remarks>
    [Fact]
    public void MarkersRoundTripThroughTheCsmith_InTheDataDisplaysOwnShape()
    {
        string dir = TempDir();
        Write75OhmS1p(dir);

        var design = Design();
        design.Overlays.Add(Overlay("part.s1p"));

        var vm = new SmithChartViewModel(design) { DocumentDirectory = dir };

        var overlayTrace = vm.ChartPlot.Traces.Single(t => t.SourceRef == "part.s1p");
        overlayTrace.Markers.Add(new Marker(overlayTrace, 2.0, false, false, 1, FreqUnit.GHz)
        {
            VswrEnabled = true,
            VswrValue   = 3.0,
            Style       = MarkerStyle.Large,
        });

        vm.HarvestMarkers();

        var stored = Assert.Single(vm.Design.Markers);
        Assert.Equal("part.s1p S11", stored.TraceName);
        Assert.True(stored.VswrEnabled);
        Assert.Equal(3.0, stored.VswrValue);
        Assert.Equal("Large", stored.Style);
        Assert.Equal("GHz", stored.FreqUnits);

        // A REBUILD must not lose it — every trace was just replaced.
        vm.RebuildChart();
        Assert.Single(vm.ChartPlot.Traces.Single(t => t.SourceRef == "part.s1p").Markers);

        // And so must a save and a reload, onto the same curve.
        string json     = vm.Serialize();
        var    reloaded = SmithDesignIo.Deserialize(json);
        var    vm2      = new SmithChartViewModel(reloaded) { DocumentDirectory = dir };

        var restored = vm2.ChartPlot.Traces.Single(t => t.SourceRef == "part.s1p").Markers;
        var one      = Assert.Single(restored);
        Assert.True(one.VswrEnabled);
        Assert.Equal(3.0, one.VswrValue);
        Assert.Equal(MarkerStyle.Large, one.Style);
        Assert.Equal(FreqUnit.GHz, one.FreqUnits);

        // A placement is ONE undo entry, and undoing it takes the marker off the chart.
        Assert.Contains("Add marker", vm.UndoRedo.UndoDescription, StringComparison.Ordinal);
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Design.Markers);
        Assert.Empty(vm.ChartPlot.Traces.Single(t => t.SourceRef == "part.s1p").Markers);
    }
}
