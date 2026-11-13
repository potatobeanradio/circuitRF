// brief-icp-2a — the Uncalibrated Port: an edge port driven against the ground plane at its cut and not de-embedded. Fixtures on
// the shipped MMIC technology, at most two frequency points; the measurement behind the bounds is in src/Design/RESOLVED.md
// §brief-icp-2 — the measurement.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class UncalibratedPortTests(UncalibratedPortTests.Fixture fx, ITestOutputHelper output)
    : IClassFixture<UncalibratedPortTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Gate 1 — on an isolated line, both ports uncalibrated: passive, within the measured bound of the calibrated
    /// answer, and with no calibration line solved.</summary>
    [Fact]
    public void OnAnIsolatedLine_IsPassive_CloseToCalibrated_AndSolvesNoStandard()
    {
        var top = new FrequencySpec("20", "20", 1, SweepKind.Linear, "GHz", "GHz");
        var cal = fx.Run("line-cal", Line(uncalibrated: false), top);
        var raw = fx.Run("line-raw", Line(uncalibrated: true), top);
        Assert.True(cal.Status == EmRunStatus.Ok, cal.Error);
        Assert.True(raw.Status == EmRunStatus.Ok, raw.Error);
        var (a, n) = S(cal);
        var (b, _) = S(raw);
        double worst = a.Zip(b, (x, y) => (x - y).Magnitude).Max();
        output.WriteLine($"max |ΔS| uncalibrated vs calibrated = {worst:E2}");
        // ICP-2a's bound: the worst the measurement found (7.0e-2, a 10 µm line at 30 GHz); this line reads 5.6e-2 at 20 GHz.
        Assert.True(worst < 7e-2, $"max |ΔS| {worst}");
        Assert.True(MaxColumnPower(b, n) <= 1 + 1e-9);
        Assert.Contains(raw.Notes ?? [], t => t.Contains("are Uncalibrated Ports", StringComparison.Ordinal));
        Assert.DoesNotContain(raw.Notes ?? [], t => t.StartsWith("De-embedding costs", StringComparison.Ordinal));
    }

    /// <summary>Gate 2 — the device-pitch case: three lines meeting a vacated site from three sides. Calibrated, the feeds'
    /// neighbours refuse the run; uncalibrated, it solves, passive.</summary>
    [Fact]
    public void AtADeviceSite_CalibratedIsRefused_UncalibratedSolves()
    {
        var cal = fx.Run("site-cal", Site(uncalibrated: false), Points(1));
        Assert.Equal(EmRunStatus.Refused, cal.Status);
        Assert.Contains("not isolated", cal.Error, StringComparison.Ordinal);
        var raw = fx.SiteRaw;
        Assert.True(raw.Status == EmRunStatus.Ok, raw.Error);
        var (s, n) = S(raw);
        Assert.Equal(6, n);
        Assert.True(MaxColumnPower(s, n) <= 1 + 1e-9);
    }

    /// <summary>Gate 3 — a run whose edge ports are series gaps still grounds its Uncalibrated Ports: the same S as the
    /// grounded run, to 1e-12.</summary>
    [Fact]
    public void ASeriesGapRun_StillGroundsItsUncalibratedPorts()
    {
        var gap = fx.Run("site-gap", Site(uncalibrated: true), Points(1), s => s.EdgePortReturn = PlanarEdgePortReturn.SeriesGap);
        Assert.True(gap.Status == EmRunStatus.Ok, gap.Error);
        var (a, _) = S(fx.SiteRaw);
        var (b, _) = S(gap);
        Assert.All(a.Zip(b), p => Assert.True((p.First - p.Second).Magnitude < 1e-12, $"{p.First} vs {p.Second}"));
    }

    /// <summary>Gates 4 and 5 — the flag reaches the file only when a port carries it: the port-set hash and the Touchstone
    /// line move for an uncalibrated port and for nothing else, and the .clay key is omitted when false.</summary>
    [Fact]
    public void TheFlag_ReachesTheFile_OnlyWhenAPortCarriesIt()
    {
        var port = new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, new Complex(50, 0));
        Assert.Equal(EmSnpProvenance.PortHash([port]), EmSnpProvenance.PortHash([port with { Uncalibrated = false }]));
        Assert.NotEqual(EmSnpProvenance.PortHash([port]), EmSnpProvenance.PortHash([port with { Uncalibrated = true }]));
        Assert.Contains("edge, uncalibrated (not de-embedded)", File.ReadAllText(fx.SiteRaw.SnpPath!), StringComparison.Ordinal);
        Assert.Contains("\"Uncalibrated\": true", File.ReadAllText(fx.SiteRaw.RecordPath!), StringComparison.Ordinal);

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", uncalibrated: false));
        string plain = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("PortUncalibrated", plain, StringComparison.Ordinal);
        Assert.Equal(plain, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(plain)));
        ((LabelShape)view.Shapes[0]).PortUncalibrated = true;
        var back = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        Assert.True(((LabelShape)back.Shapes[0]).PortUncalibrated);
        Assert.True(((LabelShape)LayoutGeometry.Clone(back.Shapes[0])).PortUncalibrated);
    }

    /// <summary>Gate 6 — the Properties box: the check box writes the key as one undo entry, and is disabled for a port that
    /// is not an edge port.</summary>
    [Fact]
    public void ThePropertiesBox_WritesTheKey_AsOneUndoEntry_AndIsDisabledForAnInternalPort()
    {
        var model = new LayoutView { DbuPerMicron = Dbu };
        model.Shapes.Add(new RectShape { Layer = M1, X1 = 0, Y1 = Um(-36), X2 = Um(600), Y2 = Um(36) });
        var edge = Edge(0, 0, LayoutRotation.R0, "1", uncalibrated: false);
        var inner = new LabelShape
        {
            Layer = M1, PortLayer = M1, X = Um(300), Y = 0, Text = "2", Height = Um(5), IsPort = true,
            PortKind = PlanarPortKind.InternalDeltaGap, PortDirection = LayoutRotation.R0,
        };
        model.Shapes.Add(edge);
        model.Shapes.Add(inner);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        vm.SelectShapes([edge]);
        Assert.True(props.PortUncalibratedEditable);
        Assert.False(props.PortUncalibratedValue);
        props.PortUncalibratedValue = true;
        Assert.True(edge.PortUncalibrated);
        vm.UndoRedo.Undo();
        Assert.False(edge.PortUncalibrated);

        vm.SelectShapes([inner]);
        Assert.False(props.PortUncalibratedEditable);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────

    private static FrequencySpec Points(int n) => new("10", "10", n, SweepKind.Linear, "GHz", "GHz");

    private static LabelShape Edge(double x, double y, LayoutRotation dir, string text, bool uncalibrated) => new()
    {
        Layer = M1, PortLayer = M1, X = Um(x), Y = Um(y), Text = text, Height = Um(5), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = dir, PortUncalibrated = uncalibrated,
    };

    private static RectShape Rect(double x0, double y0, double x1, double y1)
        => new() { Layer = M1, X1 = Um(Math.Min(x0, x1)), Y1 = Um(Math.Min(y0, y1)), X2 = Um(Math.Max(x0, x1)), Y2 = Um(Math.Max(y0, y1)) };

    /// <summary>A 72 µm (≈50 Ω) line, 600 µm long, an edge port at each end.</summary>
    private static LayoutView Line(bool uncalibrated)
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(Rect(0, -36, 600, 36));
        v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", uncalibrated));
        v.Shapes.Add(Edge(600, 0, LayoutRotation.R180, "2", uncalibrated));
        return v;
    }

    /// <summary>Three 40 µm lines, 200 µm long, meeting a vacated 80 µm site from the left, the right and below; an edge port
    /// at each line's far end and at each face the site leaves. Wide lines and no edge mesh keep the T a routine solve.</summary>
    private static LayoutView Site(bool uncalibrated)
    {
        const double W = 40, L = 200, h = 40;
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(Rect(-h - L, -W / 2, -h, W / 2));
        v.Shapes.Add(Rect(h, -W / 2, h + L, W / 2));
        v.Shapes.Add(Rect(-W / 2, -h - L, W / 2, -h));
        v.Shapes.Add(Edge(-h - L, 0, LayoutRotation.R0, "1", uncalibrated));
        v.Shapes.Add(Edge(h + L, 0, LayoutRotation.R180, "2", uncalibrated));
        v.Shapes.Add(Edge(0, -h - L, LayoutRotation.R90, "3", uncalibrated));
        v.Shapes.Add(Edge(-h, 0, LayoutRotation.R180, "4", uncalibrated));
        v.Shapes.Add(Edge(h, 0, LayoutRotation.R0, "5", uncalibrated));
        v.Shapes.Add(Edge(0, -h, LayoutRotation.R270, "6", uncalibrated));
        return v;
    }

    private static (Complex[] S, int N) S(EmRunResult r)
    {
        var data = r.Data!;
        var cube = data.CubesIn(data.Groups.First(x => data.CubesIn(x).ContainsKey("S")))["S"];
        return (cube.ComplexValues, cube.Axes[1].Values.Length);
    }

    private static double MaxColumnPower(Complex[] s, int n)
    {
        double worst = 0;
        for (int k = 0; k < s.Length / (n * n); k++)
            for (int j = 0; j < n; j++)
            {
                double c = 0;
                for (int i = 0; i < n; i++) c += Math.Pow(s[k * n * n + i * n + j].Magnitude, 2);
                worst = Math.Max(worst, c);
            }
        return worst;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icp2a-").FullName;
        private readonly Technology _tech = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        private readonly Lazy<EmRunResult> _siteRaw;

        public Fixture()
        {
            TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), _tech);
            WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            _siteRaw = new(() => Run("site-raw", Site(uncalibrated: true), Points(1)));
        }

        public EmRunResult SiteRaw => _siteRaw.Value;

        public EmRunResult Run(string cell, LayoutView view, FrequencySpec freq, Action<EmSetup>? tweak = null)
        {
            string dir = CellFolder.CreateCellFolder(_root, cell);
            CellCreate.WriteLayoutView(dir, cell, view);
            string clay = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            var setup = new EmSetup
            {
                Name = cell, LayoutRef = cell + ".clay", AnalysisKind = EmAnalysisKind.Planar,
                PlanarMesh = cell.StartsWith("site", StringComparison.Ordinal)
                    ? new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false)
                    : new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20),
                Frequency = freq, AdaptiveSampling = false,
            };
            tweak?.Invoke(setup);
            return EmRunService.Run(setup, new EmLayoutSource(clay, LayoutPersistence.LoadFromFile(clay), _tech, Dbu),
                                    Path.Combine(_root, "results-" + cell));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
