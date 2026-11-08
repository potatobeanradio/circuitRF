using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-lcp-1 — component ports on the Planar solver: a strip joining a part's two pads, cut by an internal delta gap.
// One small board on the shipped two-layer RO4350B stack: a 1100 µm line 12 mm long, with edge ports at both ends, broken by
// two 800 µm gaps, each bridged by a placed two-pad part (C1 at x = 4 mm, C2 at x = 8 mm). The trace between the parts is
// joined to nothing but them. No vias, three points: the routine gate LCP-0 sized.
public sealed class PlanarComponentPortTests(PlanarComponentPortTests.Board board, Xunit.Abstractions.ITestOutputHelper output)
    : IClassFixture<PlanarComponentPortTests.Board>
{
    private const int Dbu = 1000;
    private static readonly LayerKey Top = new(1, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Gate 1 — the built ports are exactly the hand-drawn twin: the same layout with the strips and labels written
    /// into it gives the same S, to 1e-9.</summary>
    [Fact]
    public void BuiltPorts_EqualTheirHandDrawnTwin()
    {
        var built = board.ComponentRun;
        Assert.True(built.Status == EmRunStatus.Ok, built.Error);
        var twin = board.TwinRun;
        Assert.True(twin.Status == EmRunStatus.Ok, twin.Error);
        var (a, n) = S(built);
        var (b, m) = S(twin);
        Assert.Equal(4, n);
        Assert.Equal(n, m);
        Assert.Equal(a.Length, b.Length);
        output.WriteLine($"max |ΔS| built vs twin = {a.Zip(b, (x, y) => (x - y).Magnitude).Max():E2}; |S33| = {a[10].Magnitude:F4}, |S31| = {a[8].Magnitude:F4}");
        for (int i = 0; i < a.Length; i++) Assert.True((a[i] - b[i]).Magnitude < 1e-9, $"entry {i}: {a[i]} vs {b[i]}");
    }

    /// <summary>Gate 2 — both component ports shorted in the circuit equal solid metal (the strips with no cut), to 1e-6.</summary>
    [Fact]
    public void ShortedInTheCircuit_EqualsSolidMetal()
    {
        var (s4, n) = S(board.ComponentRun);
        var (s2, two) = S(board.SolidRun);
        Assert.Equal(2, two);
        int freqs = s2.Length / 4;
        for (int f = 0; f < freqs; f++)
        {
            var shorted = Short(s4, n, f);
            output.WriteLine($"f {f}: shorted |S21| = {shorted[1, 0].Magnitude:F6}, solid |S21| = {s2[f * 4 + 2].Magnitude:F6}, " +
                             $"max |ΔS| = {Enumerable.Range(0, 4).Max(k => (shorted[k / 2, k % 2] - s2[f * 4 + k]).Magnitude):E2}");
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    Assert.True((shorted[i, j] - s2[f * 4 + i * 2 + j]).Magnitude < 1e-6,
                                $"f {f}, S{i + 1}{j + 1}: {shorted[i, j]} vs {s2[f * 4 + i * 2 + j]}");
        }
    }

    /// <summary>Gate 3 — the gap points at pad 1 (the positive end), the way positive port current crosses the cut, whatever the
    /// placement: R0, R90 and mirrored each give the direction the transformed pads say.</summary>
    [Theory]
    [InlineData(0, false, LayoutRotation.R180)]
    [InlineData(90, false, LayoutRotation.R270)]
    [InlineData(0, true, LayoutRotation.R0)]
    [InlineData(270, true, LayoutRotation.R270)]
    public void TheGapPointsAtPadOne(double rotation, bool mirror, LayoutRotation expected)
    {
        var source = board.Source(extraParts: [Place("Cap", "C9", 6000, 6000, rotation, mirror)]);
        var build = EmPlanarComponentPorts.Build(Setup(["C9"]), source)!;
        var c9 = Assert.Single(build.Ports);
        Assert.True(c9.Ok, c9.Refusal);
        Assert.Equal(expected, c9.Gap!.PortDirection);
        Assert.Equal(PlanarPortKind.InternalDeltaGap, c9.Gap.PortKind);
        Assert.Equal("C9", c9.Gap.Text);
    }

    /// <summary>The port's + is its positive pad, in the solved answer and not only in the label: port 1 drives current from left
    /// to right through C1's gap (the board is one series loop of the four ports), so S31 is positive at the lowest frequency
    /// exactly when C1's positive pad is its left one. The gap once pointed the other way and every component port's sign was
    /// reversed against the 3D build and the circuit.</summary>
    [Fact]
    public void ThePositivePad_IsThePortsPlus()
    {
        var build = EmPlanarComponentPorts.Build(Setup(["C1", "C2"]), board.Source())!;
        var c1 = Assert.Single(build.Ports, p => p.Refdes == "C1");
        var pads = EmPartPads.Project(board.Source().View, board.ClayPath, board.Tech, CircuitRF.Design.ThreeD.C3dTransform.Identity)
                             .Parts.Single(p => p.Refdes == "C1").Pads;
        bool plusOnTheLeft = pads.Single(p => p.Pin == c1.Pin).Cx < pads.Single(p => p.Pin != c1.Pin).Cx;
        var (s, n) = S(board.ComponentRun);
        double s31 = s[2 * n].Real;
        output.WriteLine($"C1 + pad {(plusOnTheLeft ? "left" : "right")}, S31 = {s[2 * n]}");
        Assert.True(Math.Abs(s31) > 0.3, $"|S31| = {Math.Abs(s31)}");
        Assert.Equal(plusOnTheLeft, s31 > 0);
    }

    /// <summary>Gate 4 — a sweep reaching below the lowest fittable frequency stays sane on the trace held only by gap ports:
    /// passive at every point, and with both parts shorted the line passes the wave at 1 MHz.</summary>
    [Fact]
    public void ASweepBelowTheFitFloor_StaysSaneOnATraceHeldOnlyByGapPorts()
    {
        var setup = Setup(["C1", "C2"]);
        setup.Frequency = new FrequencySpec("1", "3", 2, SweepKind.Linear, "MHz", "MHz");
        var r = EmRunService.Run(setup, board.Source(), board.Results("lowband"));
        Assert.True(r.Status == EmRunStatus.Ok, r.Error);
        var (s, n) = S(r);
        Assert.All(s, z => Assert.True(double.IsFinite(z.Real) && double.IsFinite(z.Imaginary)));
        Assert.All(s, z => Assert.True(z.Magnitude <= 1 + 1e-6, $"|S| = {z.Magnitude}"));
        var through = Short(s, n, 0);
        Assert.True(through[1, 0].Magnitude > 0.99, $"|S21| shorted = {through[1, 0].Magnitude}");
    }

    /// <summary>Gate 5 — each of R-lcp1-3's refusals, by refdes, before anything is meshed.</summary>
    [Fact]
    public void EachRefusal_NamesThePart_WithNoSolve()
    {
        string Refusal(EmLayoutSource source, EmSetup setup, string rd)
        {
            var build = EmPlanarComponentPorts.Build(setup, source)!;
            var p = Assert.Single(build.Ports, q => q.Refdes == rd);
            Assert.False(p.Ok);
            Assert.Contains(rd, p.Refusal);
            Assert.Contains(build.Warnings, w => w.Contains(rd + " is left out", StringComparison.Ordinal));
            return p.Refusal!;
        }

        // The pads already one conductor: copper joins them under the part.
        Assert.Contains("one piece of copper", Refusal(board.Source(extra: [Rect(3600, -200, 4400, 200)]), Setup(["C1", "C2"]), "C1"));
        // The strip would touch a third conductor: an island in the gap.
        Assert.Contains("touch other copper", Refusal(board.Source(extra: [Rect(3900, -100, 4100, 100)]), Setup(["C1", "C2"]), "C1"));
        // The pads do not face each other across one axis.
        Assert.Contains("do not face each other",
                        Refusal(board.Source(extraParts: [Place("Diag", "D1", 6000, 6000, 0, false)]), Setup(["D1"]), "D1"));
        // Not on an analysis level of this setup.
        var levels = Setup(["C1"]);
        levels.AnalysisLevelNames = ["Some Other Level"];
        Assert.Contains("analysis levels", Refusal(board.Source(), levels, "C1"));
        // Outside the solve region.
        var region = Setup(["C1", "C2"]);
        region.SolveRegion = EmSolveRegion.FromCorners(-100, -2000, 6000, 2000);
        Assert.Contains("solve region", Refusal(board.Source(), region, "C2"));
        // The gap's row would reach past the strip: pad 1's copper runs on beside the strip, past the gap's middle.
        Assert.Contains("lies against its strip", Refusal(board.Source(extra: [Rect(3000, 550, 4200, 900)]), Setup(["C1", "C2"]), "C1"));
    }

    /// <summary>Two parts sharing a refdes are refused by name, never a crash and never another part's reason.</summary>
    [Fact]
    public void ARefdesPlacedTwice_IsRefusedByName()
    {
        var build = EmPlanarComponentPorts.Build(Setup(["C1", "C2"]), board.Source(extraParts: [Place("Cap", "C1", 6000, 6000)]))!;
        var c1 = Assert.Single(build.Ports, p => p.Refdes == "C1");
        Assert.Equal(EmPartPads.PlacedMoreThanOnce("C1"), c1.Refusal);
        Assert.True(Assert.Single(build.Ports, p => p.Refdes == "C2").Ok);
    }

    /// <summary>Artwork on a layer the technology names as silkscreen is never metal: it is one note, not a warning that it might be
    /// an unbound resistor, which every board with land patterns used to get; a truly unbound layer keeps its warning.</summary>
    [Fact]
    public void Silkscreen_IsANote_NotAWarning()
    {
        var silk = new RectShape { Layer = new LayerKey(5, 0), X1 = Um(1000), Y1 = Um(1000), X2 = Um(2000), Y2 = Um(1500) };
        var pre = EmRunService.Preflight(Setup([]), board.Source(extra: [silk]));
        Assert.DoesNotContain(EmFindings.WarningTexts(pre.Findings), w => w.Contains("Silk Top", StringComparison.Ordinal));
        Assert.Contains(EmFindings.NoteTexts(pre.Findings), n => n.Contains("'Silk Top'", StringComparison.Ordinal) &&
                                                                  n.Contains("none of which is metal", StringComparison.Ordinal));
    }

    /// <summary>Gate 6 — a setup with no component ports gets exactly what it got: the same labels, the same geometry, the
    /// same impedances.</summary>
    [Fact]
    public void ASetupWithNoComponentPorts_SeesWhatItSaw()
    {
        var source = board.Source();
        var setup = Setup([]);
        Assert.Null(EmPlanarComponentPorts.Build(setup, source));
        var before = EmComponentPorts.Collect(source);
        var after = EmComponentPorts.Collect(source, setup);
        Assert.Equal(before.Shapes, after.Shapes);
        Assert.Equal(before.Notes, after.Notes);
        var flat = EmGeometry.Flatten(source.View, source.AbsolutePath, source.Technology);
        static string Text(IEnumerable<LayoutShape> shapes)
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.AddRange(shapes);
            return LayoutPersistence.Serialize(v);
        }
        Assert.Equal(Text(flat.Shapes), Text(EmGeometry.ForSetup(setup, source).Shapes));
        var z0 = after.Z0For(setup);
        for (int slot = 0; slot < 4; slot++) Assert.Equal(setup.ResolvePortZ0(slot), z0(slot));
    }

    /// <summary>Gate 7 — nothing reaches the file: the .clay is byte-identical after a run, a preflight and a save of the .cem.</summary>
    [Fact]
    public void TheClay_IsUnchanged()
    {
        Assert.Equal(EmRunStatus.Ok, board.ComponentRun.Status);
        var setup = Setup(["C1", "C2"]);
        var pre = EmRunService.Preflight(setup, board.Source());
        Assert.Null(pre.Refusal);
        EmSetupPersistence.SaveToFile(Path.Combine(board.Workspace, "board.cem"), setup);
        Assert.Equal(board.ClayBytes, File.ReadAllBytes(board.ClayPath));
    }

    /// <summary>R-lcp1-5/-7 — the parts' ports follow the drawn ones in list order, take their entry's Z0, and the result
    /// names them on the Touchstone header and in the record.</summary>
    [Fact]
    public void TheResult_NumbersAndNamesTheParts()
    {
        var r = board.ComponentRun;
        Assert.Equal(EmRunStatus.Ok, r.Status);
        string snp = File.ReadAllText(r.SnpPath!);
        Assert.Contains("! Port[3] = C1", snp, StringComparison.Ordinal);
        Assert.Contains("! Port[4] = C2", snp, StringComparison.Ordinal);
        var record = File.ReadAllText(r.RecordPath!);
        Assert.Contains("\"C1\"", record, StringComparison.Ordinal);
        var ports = r.PlanarPorts!;
        Assert.Equal(new Complex(25, 0), ports.Single(p => p.Number == 4).Z0);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static RectShape Rect(double x0, double y0, double x1, double y1)
        => new() { Layer = Top, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    internal static LayoutInstance Place(string cell, string refdes, double xUm, double yUm, double rotation = 0, bool mirror = false)
        => new() { CellRef = "../../" + cell, X = Um(xUm), Y = Um(yUm), Mag = 1.0, RefDes = refdes, RotationDegrees = rotation, MirrorX = mirror };

    internal static EmSetup Setup(string[] parts) => new()
    {
        Name = "board", LayoutRef = "Board.clay",
        AnalysisKind = EmAnalysisKind.Planar,
        PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false),
        Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
        AdaptiveSampling = false,
        ComponentPorts = [.. parts.Select(p => p == "C2" ? new EmComponentPort(p, Z0: "25") : new EmComponentPort(p))],
    };

    private static (Complex[] Values, int Ports) S(EmRunResult r)
    {
        var data = r.Data!;
        string g = data.Groups.First(x => data.CubesIn(x).ContainsKey("S"));
        var cube = data.CubesIn(g)["S"];
        return (cube.ComplexValues, cube.Axes[1].Values.Length);
    }

    /// <summary>Ports 3 and 4 terminated in a short (Γ = −1): S' = S_ee − S_ek (I + S_kk)⁻¹ S_ke at frequency index f.</summary>
    private static Complex[,] Short(Complex[] s, int n, int f)
    {
        Complex At(int i, int j) => s[f * n * n + i * n + j];
        var (a, b, c, d) = (1 + At(2, 2), At(2, 3), At(3, 2), 1 + At(3, 3));
        var det = a * d - b * c;
        var inv = new[,] { { d / det, -b / det }, { -c / det, a / det } };
        var result = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Complex sum = 0;
                for (int k = 0; k < 2; k++)
                    for (int l = 0; l < 2; l++)
                        sum += At(i, 2 + k) * inv[k, l] * At(2 + l, j);
                result[i, j] = At(i, j) - sum;
            }
        return result;
    }

    /// <summary>The workspace, written once: footprints, the board cell, and the three runs gates 1, 2 and 7 read.</summary>
    public sealed class Board : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("lcp1-").FullName;
        private readonly Lazy<EmRunResult> _component, _twin, _solid;
        public string Workspace { get; }
        public string ClayPath { get; }
        public byte[] ClayBytes { get; }
        public Technology Tech { get; }

        public Board()
        {
            Workspace = Path.Combine(_root, "ws");
            Directory.CreateDirectory(Workspace);
            Tech = ShippedTechnologies.Load("pcb-2layer_RO4350B_20mil_1oz");
            TechPersistence.SaveToFile(Path.Combine(Workspace, "tech.ctech"), Tech);
            WorkspacePersistence.SaveToFile(Path.Combine(Workspace, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            Footprint("Cap", [("1", -950, 0), ("2", 950, 0)]);
            Footprint("Diag", [("1", -950, 0), ("2", 950, 1200)]);
            string dir = CellFolder.CreateCellFolder(Workspace, "Board");
            var view = BoardView();
            CellCreate.WriteLayoutView(dir, "Board", view);
            ClayPath = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            ClayBytes = File.ReadAllBytes(ClayPath);

            _component = new(() => EmRunService.Run(Setup(["C1", "C2"]), Source(), Results("component")));
            // The hand-drawn twin carries C2's 25 Ω the only way a drawn port can: in the setup's per-port list.
            _twin = new(() =>
            {
                var setup = Setup([]);
                setup.PortZ0s = [setup.ResolvePortZ0(0), setup.ResolvePortZ0(1), setup.ResolvePortZ0(2), new Complex(25, 0)];
                return EmRunService.Run(setup, Twin(labels: true), Results("twin"));
            });
            _solid = new(() => EmRunService.Run(Setup([]), Twin(labels: false), Results("solid")));
        }

        public EmRunResult ComponentRun => _component.Value;
        public EmRunResult TwinRun => _twin.Value;
        public EmRunResult SolidRun => _solid.Value;

        public string Results(string name) => Path.Combine(_root, "results-" + name);

        /// <summary>The board as read off disk, with any extra copper or parts added in memory (a part's own pads are copper).</summary>
        public EmLayoutSource Source(IEnumerable<LayoutShape>? extra = null, IEnumerable<LayoutInstance>? extraParts = null)
        {
            var view = LayoutPersistence.LoadFromFile(ClayPath);
            view.Shapes.AddRange(extra ?? []);
            view.Instances.AddRange(extraParts ?? []);
            return new EmLayoutSource(ClayPath, view, Tech, Dbu);
        }

        /// <summary>The same layout with the built strips (and, with <paramref name="labels"/>, the gap labels numbered in their
        /// text) written into it, as a user would draw them.</summary>
        private EmLayoutSource Twin(bool labels)
        {
            var build = EmPlanarComponentPorts.Build(Setup(["C1", "C2"]), Source())!;
            var view = LayoutPersistence.LoadFromFile(ClayPath);
            view.Shapes.AddRange(build.Strips.Select(s => new RectShape { Layer = s.Layer, X1 = s.X1, Y1 = s.Y1, X2 = s.X2, Y2 = s.Y2 }));
            if (labels)
                foreach (var p in build.Ports.Where(p => p.Ok))
                    view.Shapes.Add(new LabelShape
                    {
                        Layer = p.Gap!.Layer, PortLayer = p.Gap.PortLayer, X = p.Gap.X, Y = p.Gap.Y, Text = $"P{p.Number}", Height = p.Gap.Height,
                        IsPort = true, PortKind = p.Gap.PortKind, PortDirection = p.Gap.PortDirection,
                    });
            return new EmLayoutSource(ClayPath, view, Tech, Dbu);
        }

        private static LayoutView BoardView()
        {
            var view = new LayoutView { DbuPerMicron = Dbu };
            foreach (var (x0, x1) in new[] { (0.0, 3600.0), (4400.0, 7600.0), (8400.0, 12000.0) })
                view.Shapes.Add(new RectShape { Layer = Top, X1 = Um(x0), Y1 = Um(-550), X2 = Um(x1), Y2 = Um(550) });
            foreach (var (text, x, dir) in new[] { ("1", 0.0, LayoutRotation.R0), ("2", 12000.0, LayoutRotation.R180) })
                view.Shapes.Add(new LabelShape
                {
                    Layer = Top, PortLayer = Top, X = Um(x), Y = 0, Text = text, Height = Um(300), IsPort = true,
                    PortKind = PlanarPortKind.Edge, PortDirection = dir,
                });
            view.Instances.Add(Place("Cap", "C1", 4000, 0));
            view.Instances.Add(Place("Cap", "C2", 8000, 0));
            return view;
        }

        private void Footprint(string name, (string Pin, double X, double Y)[] pads)
        {
            string cellDir = CellFolder.CreateCellFolder(Workspace, name);
            var cell = new LayoutView { DbuPerMicron = Dbu };
            foreach (var (pin, x, y) in pads)
            {
                cell.Pins.Add(new LayoutPin { Name = pin, X = Um(x), Y = Um(y), WidthDbu = Um(1100), Layer = Top });
                cell.Shapes.Add(new RectShape { Layer = Top, Pin = pin, X1 = Um(x - 550), Y1 = Um(y - 550), X2 = Um(x + 550), Y2 = Um(y + 550) });
            }
            CellCreate.WriteLayoutView(cellDir, name, cell);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
