using CircuitRF.Core.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

// brief-dfr32-2 R-dfr32-2-1..3 — an edge port's 3D sheet: bounded on a long edge, a stated Width winning, the bar the
// layout draws being the run's sheet, a .clay without the field round-tripping unchanged, and a port on a round pad's
// arc meshing like one on a straight line end. Synthetic geometry on the shipped two-layer RO4350B board: Top Copper
// (35 µm) over 508 µm of dielectric to an undrawn Bottom Copper ground, which is the air box's PEC floor. H = 508 µm.
public sealed class PortSheetWidthTests(ITestOutputHelper output) : IDisposable
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0);
    private const double H = 508e-6;
    private readonly string _root = Directory.CreateTempSubdirectory("dfr32-2-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static Technology Tech() => ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");

    private static LabelShape Port(string text, double xUm, double yUm, LayoutRotation direction, double? widthUm = null) => new()
    {
        Layer = Top, PortLayer = Top, X = Um(xUm), Y = Um(yUm), Text = text, Height = Um(200), IsPort = true,
        PortKind = PlanarPortKind.Edge, PortDirection = direction, PortWidth = widthUm is { } w ? Um(w) : null,
    };

    /// <summary>A 6 × 3 mm pour with a 0.3 mm line off its right edge (so the run has a second port); port 1 on the
    /// pour's 3 mm left edge, 0.5 mm below its centre.</summary>
    private static List<LayoutShape> Pour(double? statedUm = null) =>
    [
        new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Um(6000), Y2 = Um(3000) },
        new RectShape { Layer = Top, X1 = Um(6000), Y1 = Um(1350), X2 = Um(9000), Y2 = Um(1650) },
        Port("1", 0, 1000, LayoutRotation.R0, statedUm),
        Port("2", 9000, 1500, LayoutRotation.R180),
    ];

    /// <summary>Every face stated, so another default cannot move these numbers: 1 mm of air on the four sides and the
    /// top, all absorbing; the floor is the undrawn ground plane as PEC (a ZMin padding would not apply to it).</summary>
    private static (EmSetup Setup, EmLayoutSource Source, Technology Tech) Problem(IEnumerable<LayoutShape> shapes)
    {
        var tech = Tech();
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        var face = new EmAirBoxFace(1000, Em3dBoundaryKind.Absorbing);
        var setup = new EmSetup
        {
            Name = "ps", LayoutRef = "ps/layout/ps.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
            Palace = new CemPalace { Quality = PalaceQuality.Draft },
            AirBox = new EmAirBox(face, face, face, face, null, face),
        };
        return (setup, new EmLayoutSource("/nowhere/ps.clay", view, tech, Dbu), tech);
    }

    private static Em3dGenerationResult Generate(IEnumerable<LayoutShape> shapes)
    {
        var (setup, source, tech) = Problem(shapes);
        return Em3dGenerator.Generate(setup, source, tech);
    }

    // ── R-dfr32-2-2 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A port on a 3 mm edge (more than 4 × 508 µm) and no width stated: a sheet 2 × H wide, centred on the
    /// port, and a note naming Width as the way to change it.</summary>
    [Fact]
    public void APortOnALongEdge_GetsASheetTwiceTheHeightWide_AndTheNoteNamesWidth()
    {
        var g = Generate(Pour());
        Assert.True(g.Ok, g.Refusal);
        var p = g.Problem!.Ports.Single(q => q.Number == 1);
        Assert.Equal(1000e-6 - H, p.Min.Y, 9);
        Assert.Equal(1000e-6 + H, p.Max.Y, 9);
        Assert.Contains(g.Notes, n => n.StartsWith("Port 1 is on an edge 3000 µm long", StringComparison.Ordinal) &&
                                      n.Contains("State the port's Width", StringComparison.Ordinal));
    }

    /// <summary>A stated Width wins over the rule.</summary>
    [Fact]
    public void AStatedWidth_IsTheSheetsWidth()
    {
        var g = Generate(Pour(statedUm: 300));
        Assert.True(g.Ok, g.Refusal);
        var p = g.Problem!.Ports.Single(q => q.Number == 1);
        Assert.Equal(300e-6, p.Max.Y - p.Min.Y, 9);
        Assert.Contains(g.Notes, n => n.StartsWith("Port 1's sheet is 300 µm wide, the Width the port states", StringComparison.Ordinal));
    }

    /// <summary>A label with no Width writes no key, so every existing .clay round-trips byte for byte; a stated one is
    /// written and read back.</summary>
    [Fact]
    public void APortWithNoWidth_WritesNoKey_AndRoundTrips()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(Pour());
        string none = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("PortWidth", none, StringComparison.Ordinal);
        Assert.Equal(none, LayoutPersistence.Serialize(LayoutPersistence.Deserialize(none)));

        view.Shapes.Clear();
        view.Shapes.AddRange(Pour(statedUm: 300));
        var back = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        Assert.Equal(Um(300), back.Shapes.OfType<LabelShape>().Single(l => l.Text == "1").PortWidth);
    }

    // ── R-dfr32-2-1 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The bar the layout editor draws for a port (EdgePortSheet.Bar over the editor's own conductor lookup and
    /// the technology's stack) spans exactly the sheet the run builds — bounded, and stated.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(300.0)]
    public void TheDrawnBar_IsTheRunsSheet(double? statedUm)
    {
        var shapes = Pour(statedUm);
        var g = Generate(shapes);
        Assert.True(g.Ok, g.Refusal);
        var sheet = g.Problem!.Ports.Single(q => q.Number == 1);

        var label = shapes.OfType<LabelShape>().Single(l => l.Text == "1");
        var hint = LayoutPortDirection.Resolve(LayoutPortDirection.LookupFor(shapes), label)!.Value;
        var bar = EdgePortSheet.Bar(hint, label, Tech(), Dbu);

        Assert.Equal(sheet.Min.Y * 1e6 * Dbu, bar.PlaneY - bar.WidthDbu / 2.0, 0);
        Assert.Equal(sheet.Max.Y * 1e6 * Dbu, bar.PlaneY + bar.WidthDbu / 2.0, 0);
        Assert.Equal(sheet.Min.X * 1e6 * Dbu, bar.PlaneX, 0);
    }

    /// <summary>The port's hit region and selection highlight span the narrowed bar, not the 3 mm edge it stands on —
    /// the same <see cref="EdgePortSheet.Bar"/> the renderer draws with.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(300.0)]
    public void ThePickRegion_SpansTheDrawnBar_NotTheEdge(double? statedUm)
    {
        var shapes = Pour(statedUm);
        var label = shapes.OfType<LabelShape>().Single(l => l.Text == "1");
        var hint = LayoutPortDirection.Resolve(LayoutPortDirection.LookupFor(shapes), label)!.Value;
        var bar = EdgePortSheet.Bar(hint, label, Tech(), Dbu);
        Assert.True(bar.WidthDbu < hint.WidthDbu);

        var pick = CircuitRF.Render.LayoutHitTest.PortPickBbox(label, hint, atAnchor: false, Tech(), Dbu);
        var expected = LayoutPortDirection.MarkerBbox(label, bar, atAnchor: false, padding: Math.Max(1, label.Height / 4));
        Assert.Equal(expected, pick);
        Assert.True(pick.MaxY - pick.MinY < hint.WidthDbu);
    }

    // ── R-dfr32-2-3 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A round pad 1 mm across, polygonised at 64 vertices as an imported pad is, and a 0.2 mm trace 3 mm
    /// long off its right side. Port 1 is either on the pad's left arc, 250 µm above its centre, or at the trace's
    /// straight far end; the other end of the structure is left without a port.</summary>
    private static List<LayoutShape> PadAndTrace(bool onArc)
    {
        const int n = 64;
        const double r = 500;
        const double arcY = 250;
        var xy = new long[2 * n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * (i + 0.5) / n;
            xy[2 * i] = Um(r * Math.Cos(a));
            xy[2 * i + 1] = Um(r * Math.Sin(a));
        }
        return
        [
            new PolygonShape { Layer = Top, Xy = xy },
            new RectShape { Layer = Top, X1 = Um(400), Y1 = Um(-100), X2 = Um(3400), Y2 = Um(100) },
            onArc ? Port("1", -Math.Sqrt(r * r - arcY * arcY), arcY, LayoutRotation.R0)
                  : Port("1", 3400, 0, LayoutRotation.R180),
        ];
    }

    /// <summary>The port on the arc cuts the pad flat across its sheet, and the note says how deep the cut is.</summary>
    [Fact]
    public void APortOnAnArc_CutsThePadFlatAcrossItsSheet_AndSaysSo()
    {
        var g = Generate(PadAndTrace(onArc: true));
        Assert.True(g.Ok, g.Refusal);
        var sheet = g.Problem!.Ports.Single();
        Assert.Contains(g.Notes, n => n.StartsWith("Port 1 is on a curved edge of", StringComparison.Ordinal) &&
                                      n.Contains("beyond the sheet's plane", StringComparison.Ordinal));
        output.WriteLine(string.Join(" | ", g.Notes.Where(n => n.StartsWith("Port 1", StringComparison.Ordinal))));
        Assert.True(sheet.Max.Y - sheet.Min.Y > 400e-6, $"sheet {sheet.Max.Y - sheet.Min.Y}");
    }

    /// <summary>
    /// The gate (counters, not timings): meshed with Draft settings, the port on the arc makes no more than 1.5 × the
    /// tetrahedra the port on the trace's straight end makes, and no sliver — before the cut its smallest tetrahedron
    /// edge was 7 nm, where the sheet's corner and the arc's met a few nanometres apart (src/Design/RESOLVED.md, DFR32-2).
    /// </summary>
    [GmshFact]
    public void APortOnAnArc_MeshesWithin1Point5TimesTheStraightEnd()
    {
        var (arc, arcMin) = Mesh(PadAndTrace(onArc: true), "arc");
        var (straight, straightMin) = Mesh(PadAndTrace(onArc: false), "straight");
        output.WriteLine($"arc: {arc} tetrahedra, smallest edge {arcMin * 1e6:G4} µm; " +
                         $"straight: {straight} tetrahedra, smallest edge {straightMin * 1e6:G4} µm; ratio {arc / (double)straight:G4}");
        Assert.True(arc <= 1.5 * straight, $"arc {arc} against straight {straight}");
        Assert.True(arcMin > 1e-6, $"smallest edge on the arc {arcMin * 1e6:G4} µm");
    }

    private (long Tetrahedra, double SmallestEdgeM) Mesh(List<LayoutShape> shapes, string name)
    {
        var (setup, source, tech) = Problem(shapes);
        var g = Em3dGenerator.Generate(setup, source, tech);
        Assert.True(g.Ok, g.Refusal);
        var low = GmshGeoWriter.Write(g.Problem!, PalaceSettings.Resolve(setup.Palace));
        Assert.True(low.Ok, low.Refusal);
        string dir = Path.Combine(_root, name);
        string gmsh = SolverDiscovery.Gmsh.Check(SolverDiscovery.CapabilitiesFor(SolverTool.Gmsh)).Installation!.Path;
        var step = PalaceRun.Mesh(dir, low, gmsh, null, default);
        Assert.True(step.Ok, step.Message);
        return (step.Tetrahedra ?? 0, SmallestTetEdge(Path.Combine(dir, GmshGeoWriter.MeshFile)));
    }

    /// <summary>The shortest tetrahedron edge in a binary MSH 2.2 file, metres (the mesh is in µm). Diagnostic only.</summary>
    private static double SmallestTetEdge(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        string Line()
        {
            var sb = new System.Text.StringBuilder();
            for (int c; (c = r.ReadByte()) != '\n';) sb.Append((char)c);
            return sb.ToString().Trim();
        }
        var nodes = new Dictionary<int, (double X, double Y, double Z)>();
        double best = double.PositiveInfinity;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string tag = Line();
            if (tag == "$Nodes")
            {
                int count = int.Parse(Line());
                for (int i = 0; i < count; i++) nodes[r.ReadInt32()] = (r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
                Line(); Line();
            }
            else if (tag == "$Elements")
            {
                int count = int.Parse(Line());
                for (int read = 0; read < count;)
                {
                    int type = r.ReadInt32(), many = r.ReadInt32(), tags = r.ReadInt32();
                    int per = type switch { 1 => 2, 2 => 3, 4 => 4, 8 => 3, 9 => 6, 11 => 10, 15 => 1, _ => throw new InvalidDataException($"element type {type}") };
                    for (int e = 0; e < many; e++)
                    {
                        r.ReadInt32();
                        for (int t = 0; t < tags; t++) r.ReadInt32();
                        var v = new int[per];
                        for (int k = 0; k < per; k++) v[k] = r.ReadInt32();
                        if (type is not (4 or 11)) continue;   // a tetrahedron, first or second order: its corners first
                        for (int a = 0; a < 4; a++)
                            for (int b = a + 1; b < 4; b++)
                            {
                                var (p, q) = (nodes[v[a]], nodes[v[b]]);
                                best = Math.Min(best, Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y) + (p.Z - q.Z) * (p.Z - q.Z)));
                            }
                    }
                    read += many;
                }
                break;
            }
        }
        return best * 1e-6;
    }
}
