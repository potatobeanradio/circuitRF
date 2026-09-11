// ================================================================
//  AntennaFeedbackRound3Tests.cs — the antenna-display feedback of
//  2026-09-11, round three. All four items are about the 3D plot.
//
//  Two are controls that did nothing: the line and marker properties
//  on a surface trace card, and the X / Fam / Fix buttons on the
//  angle axes. "It looks the same whatever I pick" is exactly the
//  failure a test can settle — so §2 MEASURES the inertness rather
//  than asserting the new gate alone, because a gate hiding a control
//  that WAS doing something is the worse bug of the two.
//
//  One is a text box that should have been a list. One is an empty
//  plot that drew nothing while three checkboxes said it would.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class AntennaFeedbackRound3Tests(ITestOutputHelper output)
{
    private const int W = 420, H = 420;

    // ══ fixtures ═════════════════════════════════════════════════════════════

    /// <summary>A trace bound to the far-field fixture's own <c>U</c>, sliced as given, resolved
    /// against <paramref name="type"/>.</summary>
    private static Trace Resolve(string spec, PlotType type)
    {
        var ds = PatternFixture.Data;
        Assert.True(CubeTraceSpecParser.TryParse(spec, ds, out string cube, out var slice,
                                                 out var transform, out string err), err);
        var t = new Trace(new SNP([PatternFixture.FHz], 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            CubeName = cube, Slice = slice, Transform = transform,
        };
        TraceResolve.SetCubeDataFrom(t, ds, type, FreqUnit.GHz);
        return t;
    }

    private static Plot SurfacePlot(string spec = "db10(farfield.U[0, :, :, 1])")
    {
        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz) { PolarDbFloor = -40 };
        plot.Traces.Add(Resolve(spec, PlotType.Surface3D));
        plot.Autoscale(force: true);
        return plot;
    }

    /// <summary>
    /// <b>How much GEOMETRY one frame drew</b> — the number of filled/stroked paths in the vector
    /// form of the same picture. The ground disc and each axis arm are paths; every sentence,
    /// letter and legend number is a <c>&lt;text&gt;</c> element and is not counted.
    ///
    /// <para>Paths rather than pixels ON PURPOSE. A pixel count over a canvas carrying a
    /// three-line refusal is a count of TEXT, and <c>SkiaFonts.TestOverrideTypeface</c> is a
    /// process-wide static that other tests in this assembly set and clear — so two renders inside
    /// one test can legitimately use two different typefaces under a parallel run, and a pixel
    /// comparison between them fails for a reason that has nothing to do with the scene. The
    /// geometry count is immune to that, and it is also the more direct question: did the disc and
    /// the axes get drawn.</para>
    /// </summary>
    private static int ScenePaths(Plot plot)
    {
        string svg = PlotDocumentWriter.BuildSvgString(
            c => SurfaceRenderer.Draw(c, (W, H), plot, PlotDetail.Full, RenderTheme.Light),
            new PagePlacement(W, H, 0));
        return svg.Split("<path").Length - 1;
    }

    /// <summary>How many colour bands the legend drew — the bar is painted as horizontal bands, and
    /// nothing else on a surface emits a filled rect.</summary>
    private static int LegendBands(Plot plot)
    {
        string svg = PlotDocumentWriter.BuildSvgString(
            c => SurfaceRenderer.Draw(c, (W, H), plot, PlotDetail.Full, RenderTheme.Light),
            new PagePlacement(W, H, 0));
        return svg.Split("<rect").Length - 1;
    }

    private static string InspectorXaml() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "Views", "DataDisplay",
                                      "PlotInspectorView.axaml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §1 — the line and marker properties are not on a 3D trace card
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A surface reads no line style, no line width, no marker shape, no marker size and neither
    /// colour</b> — it is filled facets on the plot's own colour ramp. So the card does not offer
    /// them, and the source is the evidence: <c>SurfaceRenderer</c> mentions none of the six.
    /// </summary>
    [Fact]
    public void TheSurfaceRendererReadsNoLineOrMarkerPropertyFromATrace()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Render", "DataDisplay",
                                                   "Renderers", "SurfaceRenderer.cs"));
        foreach (string member in new[]
                 { "t.LineWidth", "trace.LineWidth", ".MarkerSize", ".LineColor", ".MarkerColor",
                   ".LineStyle", ".SymbolStyle" })
            Assert.DoesNotContain(member, src, StringComparison.Ordinal);
        output.WriteLine("SurfaceRenderer reads none of the six trace style properties");
    }

    /// <summary>The card follows: the two rows are off on a surface and on everywhere a curve is
    /// actually drawn.</summary>
    [Fact]
    public void TheLineAndSymbolRows_AreOffOnASurfaceAndOnForACurve()
    {
        foreach (var (type, expected) in new[]
                 {
                     (PlotType.Rect,      true),
                     (PlotType.Polar,     true),
                     (PlotType.Smith,     true),
                     (PlotType.Table,     false),
                     (PlotType.Surface3D, false),
                 })
        {
            var plot = new Plot(type, FreqUnit.GHz);
            var t    = Resolve("db10(farfield.U[0, :, :, 1])", type);
            plot.Traces.Add(t);
            var row = new TraceRowViewModel(t, new PlotInspectorViewModel(plot, () => { }, null));
            Assert.Equal(expected, row.ShowLineAndSymbol);
            output.WriteLine($"{type,-10} line/symbol rows: {row.ShowLineAndSymbol}");
        }
    }

    /// <summary>And the view binds to it rather than to the Table-only gate it used to share.</summary>
    [Fact]
    public void TheViewBindsBothRowsToTheNewGate()
    {
        string xaml = InspectorXaml();
        Assert.Equal(2, xaml.Split("IsVisible=\"{Binding ShowLineAndSymbol}\"").Length - 1);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §2 — X / Fam / Fix on the angle axes of a 3D plot: what they actually did
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The measurement behind the answer.</b> A surface opens BOTH angle axes whole whatever the
    /// slice records, so pinning θ to one index — the thing the Fix button and its picker do —
    /// leaves the drawn surface identical, down to every sample. That is why the buttons are gone
    /// from those rows rather than merely disabled: they were not doing a small thing, they were
    /// doing nothing.
    /// </summary>
    [Fact]
    public void PinningAnAngleAxisChangesNothingOnASurface()
    {
        var whole  = SurfacePlot("db10(farfield.U[0, :, :, 1])").Traces[0].SurfaceGrid;
        var pinned = SurfacePlot("db10(farfield.U[0, 2, :, 1])").Traces[0].SurfaceGrid;
        // The pinned spec above is what the Fix button writes for theta — and it resolves to the
        // whole axis regardless, which is the point.

        Assert.NotNull(whole);
        Assert.NotNull(pinned);
        Assert.Equal(whole!.ThetaCount, pinned!.ThetaCount);
        Assert.Equal(whole.PhiCount,    pinned.PhiCount);
        Assert.Equal(whole.Db.Length,   pinned.Db.Length);
        for (int i = 0; i < whole.Db.Length; i++)
            Assert.Equal(whole.Db[i], pinned.Db[i], 12);

        output.WriteLine($"theta pinned vs whole: {whole.ThetaCount} x {whole.PhiCount}, identical");
    }

    /// <summary>
    /// <b>And the picker that is KEPT is kept because it does change the picture.</b> The port axis
    /// is not one of the two angles, so the surface pins it — at the index this row chooses. Drop
    /// that control and a two-port antenna could only ever show port 1.
    /// </summary>
    [Fact]
    public void PinningANonAngleAxisDoesChangeTheSurface()
    {
        var p1 = SurfacePlot("db10(farfield.U[0, :, :, 1])").Traces[0].SurfaceGrid;
        var p2 = SurfacePlot("db10(farfield.U[0, :, :, 2])").Traces[0].SurfaceGrid;

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.Equal(p1!.Db.Length, p2!.Db.Length);
        int differing = 0;
        for (int i = 0; i < p1.Db.Length; i++)
            if (Math.Abs(p1.Db[i] - p2.Db[i]) > 1e-9) differing++;

        output.WriteLine($"port 1 vs port 2: {differing} of {p1.Db.Length} samples differ");
        Assert.True(differing > 0, "the port pin is inert too — the picker would be pointless");
    }

    /// <summary>
    /// The card's own answer to both of the above: on a surface the role buttons are gone, the two
    /// angle rows are <b>off the card entirely</b>, and every other row keeps its value picker
    /// <b>whatever role the slice happens to carry</b>. That last part is the trap: a trace carried
    /// over from a rect plot has a freq axis marked KeepAsX, and the ordinary rule hides a picker on
    /// the X axis.
    /// </summary>
    [Fact]
    public void OnASurfaceTheAngleRowsGoAndEveryOtherRowKeepsItsPicker()
    {
        var plot  = SurfacePlot();
        var owner = new TraceRowViewModel(plot.Traces[0],
                                          new PlotInspectorViewModel(plot, () => { }, null));

        var theta = new AxisRoleRowViewModel(owner, "theta", "deg", ["0", "30", "60"], isX: true, pinIndex: 0);
        var port  = new AxisRoleRowViewModel(owner, "port",  "",    ["1", "2"],        isX: true, pinIndex: 0);

        Assert.True(theta.IsRowVisible, "pre-condition: an angle row is ordinary on a cut");

        theta.ApplySurfaceMode(surfaceMode: true, isAngleAxis: true);
        port .ApplySurfaceMode(surfaceMode: true, isAngleAxis: false);

        Assert.False(theta.IsRowVisible, "the angle row must be off a surface card entirely");
        Assert.False(theta.ShowRoleButtons);
        Assert.False(theta.ShowPinPicker);

        Assert.True (port.IsRowVisible);
        Assert.False(port.ShowRoleButtons);
        Assert.True (port.ShowPinPicker, "an X-role non-angle axis must still offer its picker");

        // Switching the plot back to a cut brings the row and all three buttons back.
        theta.ApplySurfaceMode(surfaceMode: false, isAngleAxis: false);
        Assert.True (theta.IsRowVisible);
        Assert.True (theta.ShowRoleButtons);
        Assert.False(theta.ShowPinPicker);          // it is the X axis again
    }

    /// <summary>
    /// <b>And the default TITLE stops naming them too.</b> A surface authored from the picker
    /// carries θ and φ pins from the cut it started as — <c>SurfaceResolve</c> ignores them, so a
    /// pattern drawn across every direction was titled "(θ=0 deg, φ=0 deg)", which is not noise but
    /// a claim the picture contradicts. With nothing else pinned the brackets go with them.
    /// </summary>
    [Fact]
    public void TheDefaultTitleDoesNotNameTheAnglesTheSurfaceDrawsAcross()
    {
        var ds = PatternFixture.Data;

        string Title(string spec)
        {
            var t = Resolve(spec, PlotType.Surface3D);
            TraceResolve.ApplyPinnedAxisDisplay(t, ds, FreqUnit.GHz);
            Assert.NotNull(t.SurfaceGrid);
            return TraceLabeler.QuantityFor(t);
        }

        string bothPinned = Title("db10(farfield.U[0, 0, 0, 1])");
        string onePinned  = Title("db10(farfield.U[0, :, 0, 1])");
        string neither    = Title("db10(farfield.U[0, :, :, 1])");

        output.WriteLine($"both pinned: {bothPinned}");
        output.WriteLine($"phi pinned:  {onePinned}");
        output.WriteLine($"neither:     {neither}");

        foreach (string title in new[] { bothPinned, onePinned, neither })
        {
            Assert.DoesNotContain("θ", title, StringComparison.Ordinal);
            Assert.DoesNotContain("φ", title, StringComparison.Ordinal);
            Assert.DoesNotContain("theta", title, StringComparison.Ordinal);
            Assert.DoesNotContain("phi",   title, StringComparison.Ordinal);
        }

        // All three are the SAME title, because all three draw the same surface.
        Assert.Equal(neither, bothPinned);
        Assert.Equal(neither, onePinned);

        // The pins that do choose what is drawn are still named.
        Assert.Contains("5 GHz", neither, StringComparison.Ordinal);
    }

    /// <summary>No empty brackets left behind when the angles were the only thing in them — the
    /// pattern-cut case a polar plot beside it still names in full.</summary>
    [Fact]
    public void TheBracketsGoWithThemAndACutStillNamesItsPlane()
    {
        var ds = PatternFixture.Data;

        // A single-frequency, single-port cube: on a surface nothing is left to pin.
        var bare = Resolve("db10(farfield.U[0, 0, 0, 1])", PlotType.Surface3D);
        bare.Slice = [.. bare.Slice!.Where(a => a.AxisName is "theta" or "phi")];
        string surfaceTitle = TraceLabeler.QuantityFor(bare);
        output.WriteLine($"surface, nothing else pinned: \"{surfaceTitle}\"");
        Assert.DoesNotContain("(", surfaceTitle, StringComparison.Ordinal);
        Assert.DoesNotContain(")", surfaceTitle, StringComparison.Ordinal);

        // The SAME slice on a polar cut names its plane, because there it is the plane being drawn.
        var cut = Resolve("db10(farfield.U[0, :, 0, 1])", PlotType.Polar);
        TraceResolve.ApplyPinnedAxisDisplay(cut, ds, FreqUnit.GHz);
        Assert.Null(cut.SurfaceGrid);
        string cutTitle = TraceLabeler.QuantityFor(cut);
        output.WriteLine($"polar cut: \"{cutTitle}\"");
        Assert.Contains("φ", cutTitle, StringComparison.Ordinal);
    }

    /// <summary>The two angle axes the card marks are the two the RESOLVE opens — one finder, asked
    /// by both, so the card cannot name a different pair from the picture.</summary>
    [Fact]
    public void TheCardAndTheResolveAgreeOnWhichAxesAreTheAngles()
    {
        var cube = PatternFixture.Data["farfield.U"];
        Assert.True(SurfaceResolve.TryFindAngleAxes(cube, out int th, out int ph));
        Assert.Equal("theta", cube.Axes[th].Name);
        Assert.Equal("phi",   cube.Axes[ph].Name);

        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "DataDisplay",
                                                   "ViewModels", "TraceRowViewModel.cs"));
        Assert.Contains("SurfaceResolve.TryFindAngleAxes", src, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §3 — the radial unit is a list, and it does not run off the panel
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A unit is chosen, not typed.</b> The first row takes whatever the cube says; the rest name
    /// one outright, and picking the first row stores the empty string the plot uses for "the
    /// cube's own".
    /// </summary>
    [Fact]
    public void TheRadialUnitIsAListWhoseFirstRowIsTheCubesOwn()
    {
        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz);
        var vm   = new PlotInspectorViewModel(plot, () => { }, null);

        // The row is a DASH, not a sentence: the combo is sized by its widest row and sits at the end
        // of a line of short unit spellings (owner, 2026-09-11).
        Assert.Equal("\u2014", PlotInspectorViewModel.PolarDbUnitFromData);
        Assert.True(PlotInspectorViewModel.PolarDbUnitFromData.Length <= 2,
                    "the 'take the cube's own' row sets the width of the whole combo");
        Assert.Equal(PlotInspectorViewModel.PolarDbUnitFromData, vm.PolarDbUnitOptions[0]);
        Assert.Contains("dBi",      vm.PolarDbUnitOptions);
        Assert.Contains("dB(W/sr)", vm.PolarDbUnitOptions);
        Assert.Equal(PlotInspectorViewModel.PolarDbUnitFromData, vm.PolarDbUnitSelection);

        vm.PolarDbUnitSelection = "dBi";
        Assert.Equal("dBi", plot.PolarDbUnit);

        vm.PolarDbUnitSelection = PlotInspectorViewModel.PolarDbUnitFromData;
        Assert.Equal("", plot.PolarDbUnit);
        output.WriteLine(string.Join(" / ", vm.PolarDbUnitOptions));
    }

    /// <summary>
    /// <b>A unit that arrived from a file is shown, never silently replaced.</b> The CLI and a saved
    /// <c>.cdd</c> both carry a free string, so the list has to be open at the bottom — a closed one
    /// would open blank on such a plot and rewrite the unit on the first click.
    /// </summary>
    [Fact]
    public void AUnitFromAFileIsAddedToTheListRatherThanDropped()
    {
        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz) { PolarDbUnit = "dB(A/m)" };
        var vm   = new PlotInspectorViewModel(plot, () => { }, null);

        Assert.Contains("dB(A/m)", vm.PolarDbUnitOptions);
        Assert.Equal("dB(A/m)", vm.PolarDbUnitSelection);
    }

    /// <summary>
    /// <b>The row wraps rather than clipping.</b> Reported: the unit control was cut off at the
    /// right edge of the Plot Inspector. A horizontal StackPanel lays its children out past its own
    /// edge without complaint, so the fix is the PANEL, and the last control is the one that proves
    /// it — its label and its combo travel together.
    /// </summary>
    [Fact]
    public void ThePatternRowWrapsAndTheUnitIsACombo()
    {
        string xaml = InspectorXaml();
        Assert.Contains("<WrapPanel IsVisible=\"{Binding HasPatternControls}\">", xaml);
        Assert.Contains("ItemsSource=\"{Binding PolarDbUnitOptions}\"", xaml);
        Assert.Contains("SelectedItem=\"{Binding PolarDbUnitSelection, Mode=TwoWay}\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding PolarDbUnit, Mode=TwoWay}\"", xaml);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §4 — an empty 3D plot draws the scene its own checkboxes describe
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A 3D plot placed with no trace on it yet draws its ground disc, its axes and its colour
    /// bar.</b> All three are on by default, and the plot drew none of them until something
    /// resolved — so placing one produced a blank rectangle and there was nothing to say which way
    /// the camera was even pointing.
    /// </summary>
    [Fact]
    public void AnEmpty3DPlotDrawsItsAxesGroundAndLegend()
    {
        var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz);
        plot.Autoscale(force: true);
        int all = ScenePaths(plot);

        plot.SurfaceShowGroundDisc = false;
        int noDisc = ScenePaths(plot);
        plot.SurfaceShowAxes = false;
        int noAxes = ScenePaths(plot);

        output.WriteLine($"empty plot: {all} paths -> no disc {noDisc} -> no axes {noAxes}");
        Assert.True(all    > noDisc, "the ground disc was not drawn on an empty plot");
        Assert.True(noDisc > noAxes, "the scene axes were not drawn on an empty plot");

        // The legend is the third default-on control, and it is text plus colour bands rather than
        // geometry — so it is asked for by its own marks. It was already unconditional (round 2);
        // what this holds is that an empty plot is not an exception to that.
        plot.SurfaceShowAxes = true;
        plot.SurfaceShowGroundDisc = true;
        Assert.True(LegendBands(plot) > 0, "the colour bar was not drawn on an empty plot");
        plot.SurfaceShowLegend = false;
        Assert.Equal(0, LegendBands(plot));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 — a 3D plot offers only the quantities it can actually draw
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Every far-field cube that carries two angle axes, by name — the expectation both
    /// tests below are written against, derived from the fixture rather than listed by hand. A
    /// list would be a second opinion about which quantities are directional, and the point of the
    /// rule is that there is only one.</summary>
    private static string[] DirectionalCubes() =>
    [
        .. PatternFixture.Data.CubesIn(CircuitRF.Engine.Mom.PlanarFarField.Group)
            .Where(c => !c.Key.StartsWith("__", StringComparison.Ordinal)
                     && SurfaceResolve.TryFindAngleAxes(c.Value, out _, out _))
            .Select(c => c.Key).Order()
    ];

    /// <summary>
    /// <b>The rule, over the far-field group a real solve publishes.</b> Some of its cubes are
    /// functions of DIRECTION — the field components, the radiation intensity, the Ludwig-3 pair,
    /// the polarization quantities — and the rest of the registry is per-frequency and per-port:
    /// TRP, peak EIRP, the efficiencies, the beamwidths. Only the first kind may be drawn as a
    /// surface, and on every other plot type the question is not asked at all.
    /// </summary>
    [Fact]
    public void OnlyTheDirectionalQuantitiesMayBeDrawnAsASurface()
    {
        var ds = PatternFixture.Data;
        var drawable = new List<string>();
        int greyed = 0;

        foreach (var (name, cube) in ds.CubesIn(CircuitRF.Engine.Mom.PlanarFarField.Group))
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            string? reason = SurfaceResolve.DisabledReasonOn(PlotType.Surface3D, cube);

            // The rule IS the resolve's own finder — not a second opinion beside it.
            Assert.Equal(SurfaceResolve.TryFindAngleAxes(cube, out _, out _), reason is null);
            if (reason is null) drawable.Add(name); else greyed++;

            // And on every other plot type nothing is greyed for this reason.
            foreach (var other in new[] { PlotType.Rect, PlotType.Polar, PlotType.Smith, PlotType.Table })
                Assert.Null(SurfaceResolve.DisabledReasonOn(other, cube));
        }

        output.WriteLine($"drawable as a surface: {string.Join(", ", drawable.Order())}");
        output.WriteLine($"greyed: {greyed} rows");

        // The named ones are here because they are the reason the rule is not "everything in the
        // farfield group": a pattern surface of U and of the Ludwig-3 pair is exactly the picture
        // this plot exists for, and a surface of TRP is not a thing.
        Assert.Contains("U",      drawable);
        Assert.Contains("Etheta", drawable);
        Assert.True(greyed > drawable.Count,
                    "most of a far-field registry is per-frequency, and that is what is being greyed");
    }

    /// <summary>
    /// <b>The picker itself, end to end.</b> The rule above is only worth having if the rows the
    /// user sees follow it — so this drives the real trace card against a real loaded file and reads
    /// the far-field rows off it. Every greyed row carries a REASON, which is this repository's
    /// standing rule: a row that is greyed with nothing to read cannot be told apart from a broken
    /// one, and a row that vanishes cannot be told apart from a quantity the run never published.
    /// </summary>
    [Fact]
    public async Task ThePickerGreysTheQuantitiesA3DPlotCannotDraw()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_ff_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(PatternFixture.Data, path,
                                                 RfCore.Export.ExportFormat.Npy);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            foreach (var (type, expectAllEnabled) in new[] { (PlotType.Surface3D, false), (PlotType.Rect, true) })
            {
                var plot = new Plot(type, FreqUnit.GHz);
                var t    = Resolve("db10(farfield.U[0, :, :, 1])", type);
                plot.Traces.Add(t);
                var row = new TraceRowViewModel(t, new PlotInspectorViewModel(plot, () => { }, lib));

                string? group = row.AvailableGroups.FirstOrDefault(
                    g => g.Contains("farfield", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(group);
                row.SelectedGroup = group;

                var farfield = row.AvailableSignals.ToList();
                Assert.NotEmpty(farfield);

                var off = farfield.Where(i => !i.IsEnabled).Select(i => i.Label).ToList();
                var on  = farfield.Where(i =>  i.IsEnabled).Select(i => i.Label).ToList();
                output.WriteLine($"{type}: enabled [{string.Join(", ", on)}]");
                output.WriteLine($"{type}: greyed  {off.Count} rows");

                // Every greyed row says why.
                foreach (var i in farfield.Where(i => !i.IsEnabled))
                    Assert.False(string.IsNullOrWhiteSpace(i.DisabledReason), $"{i.Label} greyed with no reason");

                if (expectAllEnabled)
                    Assert.Empty(off);
                else
                {
                    Assert.NotEmpty(off);
                    Assert.Equal(DirectionalCubes(), on.Order().ToArray());
                }
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>A derived network metric and a WSProbe quantity are functions of FREQUENCY, so a 3D
    /// plot takes none of them either — and both answer with the same sentence, from the one
    /// place.</summary>
    [Fact]
    public void NoDerivedOrProbeQuantityIsOfferedOnA3DPlot()
    {
        foreach (var d in new[] { DerivedParameters.MaxGain, DerivedParameters.K,
                                  DerivedParameters.Mu, DerivedParameters.LoadStabilityCircle })
        {
            var onRect    = new TraceDataItem(null!, d, PlotType.Rect,      omitFilePrefix: true);
            var onSurface = new TraceDataItem(null!, d, PlotType.Surface3D, omitFilePrefix: true);
            Assert.False(onSurface.IsEnabled, $"{d} must not be offered on a 3D plot");
            Assert.Equal(SurfaceResolve.NotOnASurfaceRefusal, onSurface.DisabledReason);
            // Rect is untouched — a circle locus is still refused there for its own reason.
            Assert.Equal(d.IsScalarVsFrequency(), onRect.IsEnabled);
        }

        foreach (var m in Enum.GetValues<WspMetric>())
        {
            if (m == WspMetric.None || WspMetrics.Info(m) is null) continue;
            Assert.Equal(SurfaceResolve.NotOnASurfaceRefusal,
                         WspMetrics.DisabledReasonOn(m, PlotType.Surface3D));
        }
    }

    /// <summary>
    /// <b>The frequency is in a 3D pattern's title even when the SLICE does not call it pinned.</b>
    ///
    /// <para>Reported 2026-09-11: no frequency in the default title. The picker's own default slice
    /// makes <c>freq</c> the X axis — and a surface ignores that and pins it, so the label, which
    /// read the slice's roles, had nothing to report. Which frequency a pattern was taken at is the
    /// first thing a reader of a swept run needs.</para>
    ///
    /// <para>The same rule that drops θ and φ puts the frequency back: the question is what the
    /// PICTURE pins, and both directions of the difference come from one predicate.</para>
    /// </summary>
    [Fact]
    public void ASurfaceTitleNamesItsFrequencyWhateverRoleTheSliceGaveIt()
    {
        var ds = PatternFixture.Data;

        string Title(AxisRole freqRole)
        {
            var t = Resolve("db10(farfield.U[0, :, :, 1])", PlotType.Surface3D);
            // Rewrite freq's role to the one under test, exactly as the picker's default slice
            // (KeepAsX) or a carried-over cut (PinToIndex) would have left it.
            t.Slice = [.. t.Slice!.Select(a =>
                a.AxisName == "freq" ? new AxisSlice(a.AxisName, freqRole, a.Index, Label: a.Label) : a)];
            TraceResolve.SetCubeDataFrom(t, ds, PlotType.Surface3D, FreqUnit.GHz);
            Assert.NotNull(t.SurfaceGrid);
            return TraceLabeler.QuantityFor(t);
        }

        string asX     = Title(AxisRole.KeepAsX);
        string asPin   = Title(AxisRole.PinToIndex);
        string asFam   = Title(AxisRole.FamilyIterate);
        output.WriteLine($"freq as X:      {asX}");
        output.WriteLine($"freq as pin:    {asPin}");
        output.WriteLine($"freq as family: {asFam}");

        // One picture, one title — the role the slice happens to carry cannot change what is drawn,
        // so it must not change what the title says either.
        Assert.Contains("5 GHz", asX,   StringComparison.Ordinal);
        Assert.Equal(asPin, asX);
        Assert.Equal(asPin, asFam);

        // A polar CUT is the other side of the same rule: there the role IS what is drawn, so a
        // freq axis kept as X is the sweep and has no single value to name.
        var cut = Resolve("db10(farfield.U[:, 0, 0, 1])", PlotType.Polar);
        TraceResolve.ApplyPinnedAxisDisplay(cut, ds, FreqUnit.GHz);
        Assert.Null(cut.SurfaceGrid);
        string cutTitle = TraceLabeler.QuantityFor(cut);
        output.WriteLine($"polar cut, freq as X: {cutTitle}");
        Assert.DoesNotContain("5 GHz", cutTitle, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §6 — "+ Trace" on an empty 3D plot draws something
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Clicking + Trace on an empty 3D plot must make a surface appear.</b>
    ///
    /// <para>It did not: a far-field <c>.npy</c> carries an S network alongside its pattern cubes,
    /// and the seed took the S branch — S(1,1), a function of frequency, with no surface in it. The
    /// user clicked a button and the plot stayed empty but for a sentence. The 3D branch is asked
    /// FIRST now, and the cube it picks is the first one that passes the resolve's own angle test.
    /// </para>
    ///
    /// <para>The gate is the GRID, not the cube's name: a trace that resolved a surface is a trace
    /// the user can see.</para>
    /// </summary>
    [Fact]
    public async Task AddingATraceToAnEmpty3DPlotDrawsASurface()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_seed_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(PatternFixture.Data, path,
                                                 RfCore.Export.ExportFormat.Npy);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            // Pre-condition: this source DOES carry an S network, which is what used to be seeded.
            Assert.NotNull(lib.Entries.Single().Snp);

            var plot = new Plot(PlotType.Surface3D, FreqUnit.GHz);
            var vm   = new PlotInspectorViewModel(plot, () => { }, lib);
            Assert.Empty(plot.Traces);

            vm.AddTraceCommand.Execute(null);

            var t = Assert.Single(plot.Traces);
            output.WriteLine($"seeded: {t.Expression}  transform={t.Transform}");
            output.WriteLine($"error:  {t.ExpressionError ?? "(none)"}");

            Assert.True(t.IsCubeBound, "the seed took the S-network branch again");
            Assert.Null(t.ExpressionError);
            Assert.NotNull(t.SurfaceGrid);
            Assert.True(t.SurfaceGrid!.ThetaCount > 1 && t.SurfaceGrid.PhiCount > 1);

            // Both angles are open in the spec the card shows, because that is what is drawn — the
            // ordinary seed would have written a slice pinning them.
            Assert.DoesNotContain("theta=", t.Expression ?? "", StringComparison.Ordinal);
            Assert.Equal(2, (t.Slice ?? []).Count(a => a.Role != AxisRole.PinToIndex));

            // And the plot actually has geometry on it now, which is the whole of the report.
            plot.Autoscale(force: true);
            Assert.True(ScenePaths(plot) > ScenePaths(new Plot(PlotType.Surface3D, FreqUnit.GHz)),
                        "the seeded trace drew no facets");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// <b>The one case the furniture still stands down for is a REFUSAL.</b> Its sentence is drawn
    /// where the surface would have been, and an axis arm through the middle of it is how a sentence
    /// that must be read ends up struck through. So toggling the disc and the axes changes nothing
    /// on a refused plot — which is exactly what distinguishes it from the empty one above.
    /// </summary>
    [Fact]
    public void ARefusedTraceKeepsTheSceneToItself()
    {
        // A COMPLEX field with no dB transform: its linear magnitude is not decibels, and a pattern
        // radius is. This is Trace.PatternValueRefusal, drawn where the surface would have been.
        var plot = SurfacePlot("farfield.Etheta[0, :, :, 1]");
        Assert.Null(plot.Traces[0].SurfaceGrid);
        Assert.False(string.IsNullOrEmpty(plot.Traces[0].ExpressionError));

        int on = ScenePaths(plot);
        plot.SurfaceShowGroundDisc = false;
        plot.SurfaceShowAxes       = false;
        int off = ScenePaths(plot);

        output.WriteLine($"refused plot: {on} paths with the scene on, {off} with it off");
        Assert.Equal(on, off);

        // And the sentence IS there — otherwise the two counts would agree on an empty picture.
        string svg = PlotDocumentWriter.BuildSvgString(
            c => SurfaceRenderer.Draw(c, (W, H), plot, PlotDetail.Full, RenderTheme.Light),
            new PagePlacement(W, H, 0));
        Assert.Contains("<text", svg, StringComparison.Ordinal);
    }
}
