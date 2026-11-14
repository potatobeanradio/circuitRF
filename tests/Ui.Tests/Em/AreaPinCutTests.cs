// brief-icp-10 — a cell instance cut out at an AREA pin: a kit that marks its pin with a box on a pin-purpose layer ("Metal3.pin") gives
// no face, only the box's middle and a 0° default, so the cut reads the face from where the interconnect meets the pin's metal. Found on
// an open SiGe kit's transistor PCells, whose pins are bars a route meets at either end. Built only; nothing is solved.

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class AreaPinCutTests(AreaPinCutTests.Fixture fx, ITestOutputHelper output) : IClassFixture<AreaPinCutTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0), M2 = new(2, 0), M3 = new(3, 0), M3Pin = new(3, 2), Via23 = new(6, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>The pin's face is the side of its bar the route meets — the bar's −x end, looking −x — whether the route abuts it or is
    /// drawn onto it; in Cell mode it is where the route crosses the box. The pin is stated at the bar's middle looking +x, on a
    /// pin-purpose layer no conductor lists.</summary>
    [Theory]
    [InlineData(false, false, 0.0)]
    [InlineData(false, true, 0.0)]
    [InlineData(true, false, -20.0)]
    public void AnAreaPin_TakesItsFace_WhereTheInterconnectMeetsIt(bool cellMode, bool onto, double faceUm)
    {
        var build = fx.Build(Line(onto), new EmComponentPort("X1")
        {
            PortAt = cellMode ? EmComponentPortAt.Cell : EmComponentPortAt.Pin,
            KeepOutUm = cellMode ? new EmKeepOut(-20, -80, 220, 80) : null,
        });
        var port = Assert.Single(build.Ports, p => p.Name == "X1.A");
        output.WriteLine(port.Refusal ?? $"{port.Gap!.X} {port.Gap.PortDirection} {port.WidthM}");
        Assert.True(port.Ok, port.Refusal);
        Assert.Equal(Um(faceUm), port.Gap!.X);
        Assert.Equal(LayoutRotation.R180, port.Gap.PortDirection);
        Assert.Equal(20e-6, port.WidthM, 9);
        Assert.Equal(M3, port.Gap.PortLayer);                 // the conductor's drawing layer, not the pin purpose it was stated on
    }

    /// <summary>A parent via joining Metal2 and Metal3 over the cell's Metal1 plate lands on nothing the cut removes: the instance is
    /// cut. It was refused as landing on the instance's own metal, whatever level that metal was on.</summary>
    [Fact]
    public void AViaOverDeviceMetal_OnALevelItDoesNotJoin_IsNotALanding()
    {
        var v = Line(onto: false);
        v.Shapes.Add(R(M2, 140, 30, 170, 55));
        v.Shapes.Add(R(Via23, 150, 38, 160, 48));
        v.Shapes.Add(R(M3, 140, 30, 170, 55));
        var build = fx.Build(v, new EmComponentPort("X1"));
        Assert.DoesNotContain(build.Ports, p => p.Refusal?.Contains("lands on", StringComparison.Ordinal) == true);
        Assert.Contains(build.Ports, p => p.Ok && p.Name == "X1.A");
    }

    /// <summary>A 300 µm Metal3 line of 20 µm meeting Bar's −x end at x = 0 (or drawn 50 µm onto it), and Bar placed at the origin.</summary>
    private LayoutView Line(bool onto)
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(R(M3, -300, -10, onto ? 50 : 0, 10));
        v.Shapes.Add(new LabelShape
        {
            Layer = M3, PortLayer = M3, X = Um(-300), Y = 0, Text = "1", Height = Um(20), IsPort = true, PortKind = PlanarPortKind.Edge,
            PortDirection = LayoutRotation.R0, PortUseEdgeWidth = true, PortUncalibrated = true,
        });
        v.Instances.Add(new LayoutInstance { CellRef = "../../Bar", Mag = 1.0, SchematicId = "X1" });
        return v;
    }

    private static RectShape R(LayerKey l, double x0, double y0, double x1, double y1)
        => new() { Layer = l, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icp10-").FullName;
        private readonly Technology _tech = ShippedTechnologies.Load("rfic-Si_3LM_TFR_100um");
        private int _n;

        public Fixture()
        {
            // The kit's spelling: a pin purpose of Metal3, listed in the layer table and in no conductor's drawing layers.
            _tech.Layers.Add(new LayerDef { Key = M3Pin, Name = "Metal3.pin" });
            TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), _tech);
            WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            // Bar: a 200 × 40 µm Metal3 bar over a 200 × 120 µm Metal1 plate (a device's own metal on another level), its pin A a box on
            // Metal3.pin stated at the bar's middle looking +x, as a kit's pin box reads.
            string dir = CellFolder.CreateCellFolder(_root, "Bar");
            var c = new LayoutView { DbuPerMicron = Dbu };
            c.Shapes.Add(R(M1, 0, -60, 200, 60));
            c.Shapes.Add(R(M3, 0, -20, 200, 20));
            c.Shapes.Add(R(M3Pin, 0, -20, 200, 20));
            c.Pins.Add(new LayoutPin { Name = "A", X = Um(100), Y = 0, WidthDbu = Um(40), OutwardDeg = 0, Layer = M3Pin });
            CellCreate.WriteLayoutView(dir, "Bar", c);
        }

        public EmPlanarComponentPortBuild Build(LayoutView view, EmComponentPort entry)
        {
            string name = $"Top{Interlocked.Increment(ref _n)}";
            string dir = CellFolder.CreateCellFolder(_root, name);
            CellCreate.WriteLayoutView(dir, name, view);
            string clay = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            var setup = new EmSetup
            {
                Name = name, LayoutRef = name + ".clay", AnalysisKind = EmAnalysisKind.Planar,
                PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20),
                Frequency = new FrequencySpec("10", "10", 1, SweepKind.Linear, "GHz", "GHz"),
                AdaptiveSampling = false, ComponentPorts = [entry],
            };
            return EmPlanarComponentPorts.Build(setup, new EmLayoutSource(clay, LayoutPersistence.LoadFromFile(clay), _tech, Dbu))!;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
