// brief-icp-2 — a cell instance cut out of a Planar run, an Uncalibrated Port (brief-icp-2a) at each pin face. Fixtures on the shipped
// MMIC technology: a 72 µm line broken by a 200 µm two-pin cell (Seg), placed at the top level as X1 and inside a Mid cell as X1/Q3,
// and a three-pin cell (Fet) met by three lines. At most two frequency points; uncalibrated ports solve no calibration standard.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Mom;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PlanarCellCutoutTests(PlanarCellCutoutTests.Fixture fx, ITestOutputHelper output)
    : IClassFixture<PlanarCellCutoutTests.Fixture>
{
    private const int Dbu = 1000;
    private static readonly LayerKey M1 = new(1, 0), M2 = new(2, 0), Post = new(3, 0);
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    // ── Gate 1: built equals hand-drawn ───────────────────────────────────────────────────────────────────────────────

    /// <summary>A cut (Pin mode, Cell mode; top-level X1 and nested X1/Q3) gives S identical to 1e-9 with the same layout edited by
    /// hand: instance deleted, conductor trimmed, Uncalibrated edge labels drawn.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BuiltEqualsHandDrawn(bool nested, bool cellMode)
    {
        string tag = $"{(nested ? "n" : "t")}{(cellMode ? "c" : "p")}";
        // Every port uncalibrated on both sides, so neither run solves a calibration standard: the claim is equality.
        var built = fx.Run("built-" + tag, LineWithSeg(nested, overlap: true, uncalibratedEnds: true), [Entry(nested ? "X1/Q3" : "X1", cellMode)]);
        var twin = fx.Run("twin-" + tag, Twin(nested, cellMode), []);
        Assert.True(built.Status == EmRunStatus.Ok, built.Error);
        Assert.True(twin.Status == EmRunStatus.Ok, twin.Error);
        var (a, n) = S(built);
        var (b, m) = S(twin);
        Assert.Equal(4, n);
        Assert.Equal(n, m);
        double worst = a.Zip(b, (x, y) => (x - y).Magnitude).Max();
        output.WriteLine($"{tag}: max |ΔS| built vs hand-drawn = {worst:E2}");
        Assert.True(worst < 1e-9, $"max |ΔS| {worst}");
        string path = nested ? "X1/Q3" : "X1";
        string snp = File.ReadAllText(built.SnpPath!);
        Assert.Contains($"! Port[3] = {path}.A", snp, StringComparison.Ordinal);
        Assert.Contains($"! Port[4] = {path}.B", snp, StringComparison.Ordinal);
    }

    // ── Gate 2: the reference plane ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The 200 µm segment cut, its faces Uncalibrated Ports, closed with an ideal line of its length (the whole line's own
    /// γ and Z_c), matches the whole line to ICP-2a's bound.</summary>
    [Fact]
    public void TheCutClosedWithAnIdealLine_MatchesTheWholeLine()
    {
        var whole = fx.Run("whole", LineWithSeg(nested: false, overlap: false), [], top: true);
        var cut = fx.Run("cut", LineWithSeg(nested: false, overlap: false), [Entry("X1", false)], top: true);
        Assert.True(whole.Status == EmRunStatus.Ok, whole.Error);
        Assert.True(cut.Status == EmRunStatus.Ok, cut.Error);
        var (s2, _) = S(whole);
        var (s4, _) = S(cut);
        double worst = 0;
        for (int k = 0; k < s2.Length / 4; k++)
        {
            var closed = CloseWithLine(k, s2, s4, 200.0 / 1200.0);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) worst = Math.Max(worst, (closed[i, j] - s2[k * 4 + i * 2 + j]).Magnitude);
        }
        output.WriteLine($"max |ΔS| cut-and-closed vs whole = {worst:E2}");
        Assert.True(worst < 7e-2, $"max |ΔS| {worst}");
    }

    // ── Gate 3: a three-pin instance ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A three-pin cell met by three lines at an 80 µm site solves with three cut ports, passive.</summary>
    [Fact]
    public void AThreePinInstance_SolvesWithThreePorts()
    {
        var r = fx.Run("fet", Fet(), [new EmComponentPort("X1")], site: true);
        Assert.True(r.Status == EmRunStatus.Ok, r.Error);
        var (s, n) = S(r);
        Assert.Equal(6, n);
        string snp = File.ReadAllText(r.SnpPath!);
        foreach (var pin in new[] { "G", "D", "S" }) Assert.Contains($"= X1.{pin}", snp, StringComparison.Ordinal);
        for (int k = 0; k < s.Length / (n * n); k++)
            for (int j = 0; j < n; j++)
                Assert.True(Enumerable.Range(0, n).Sum(i => Math.Pow(s[k * n * n + i * n + j].Magnitude, 2)) <= 1 + 1e-9);
    }

    // ── Gate 4: each refusal, and the pin classes, with no solve ──────────────────────────────────────────────────────

    [Fact]
    public void EachRefusal_AndEachPinClass_IsSaidBeforeMeshing()
    {
        string Why(LayoutView v, EmComponentPort entry, string pin, Action<EmSetup>? tweak = null)
        {
            var build = fx.Build(v, [entry], tweak);
            var p = build.Ports.First(x => pin.Length == 0 ? x.Name == "X1" : x.Name.EndsWith("." + pin, StringComparison.Ordinal));
            Assert.False(p.Ok);
            output.WriteLine(p.Refusal);
            return p.Refusal!;
        }

        // Two conductors meet one pin: a second line parallel to the left one, both touching pin A's face.
        var two = LineWithSeg(false, overlap: false);
        two.Shapes[0] = R(M1, 0, -36, 500, -6);
        two.Shapes.Add(R(M1, 0, 6, 500, 36));
        Assert.Contains("separate conductors meet its face", Why(two, Entry("X1", false), "A"), StringComparison.Ordinal);

        // A pin name at two faces (several fingers).
        var fingers = fx.LineWith("Seg2", draw: c => { Pin(c, "A", 0, 0, 180, 72); Pin(c, "A", 200, 0, 0, 72); c.Shapes.Add(R(M1, 0, -36, 200, 36)); });
        Assert.Contains("more than one face", Why(fingers, Entry("X1", false), "A"), StringComparison.Ordinal);

        // The parent's metal overlapping the cell beyond its pin (pin mode): a parent patch on the segment, starting at pin A's face
        // and so one piece with the left line.
        var over = LineWithSeg(false, overlap: false);
        over.Shapes.Add(R(M1, 500, -10, 600, 10));
        Assert.Contains("Cell mode", Why(over, Entry("X1", false), "A"), StringComparison.Ordinal);

        // A parent via landing on the cell's metal (pin mode).
        var via = LineWithSeg(false, overlap: false);
        via.Shapes.Add(R(Post, 590, -5, 610, 5));
        via.Shapes.Add(R(M2, 580, -15, 620, 15));
        Assert.Contains("lands on X1's own metal", Why(via, Entry("X1", false), ""), StringComparison.Ordinal);

        // The pin's layer not an analysis level.
        Assert.Contains("not one of this setup's analysis levels",
                        Why(LineWithSeg(false, false), Entry("X1", false), "A", s => s.AnalysisLevelNames = ["Metal2"]), StringComparison.Ordinal);

        // The face outside the solve region.
        Assert.Contains("outside this EM setup's solve region",
                        Why(LineWithSeg(false, false), Entry("X1", false), "B", s => s.SolveRegion = new EmSolveRegion(-10, -100, 650, 100)),
                        StringComparison.Ordinal);

        // Floating: no line meets pin B (a warning, not a port); abutting: pin A touches another cut instance.
        var classes = fx.LineWith("Seg", place: v =>
        {
            v.Shapes.Clear();
            v.Instances.Clear();
            v.Shapes.Add(R(M1, 0, -36, 500, 36));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1"));
            v.Instances.Add(fx.Place("Seg", 500, 0, "X1"));
            v.Instances.Add(fx.Place("Seg", 700, 0, "X2"));
        });
        var b = fx.Build(classes, [new EmComponentPort("X1"), new EmComponentPort("X2")]);
        Assert.Contains(b.Warnings, w => w.StartsWith("X2.B has nothing connected to it", StringComparison.Ordinal));
        Assert.Contains(b.Notes, w => w.StartsWith("X1.B touches only X2", StringComparison.Ordinal));
        Assert.Contains(b.Ports, p => p.Ok && p.Name == "X1.A");
    }

    // ── Gate 5: a setup with no cut writes what it wrote ──────────────────────────────────────────────────────────────

    [Fact]
    public void ASetupWithNoCut_SeesThePlainFlatten()
    {
        var source = fx.Source("plain", LineWithSeg(nested: true, overlap: true));
        static string Text(IEnumerable<LayoutShape> shapes)
        {
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.AddRange(shapes);
            return LayoutPersistence.Serialize(v);
        }
        var plain = EmGeometry.Flatten(source.View, source.AbsolutePath, source.Technology);
        Assert.Equal(Text(plain.Shapes),
                     Text(EmGeometry.Flatten(source.View, source.AbsolutePath, source.Technology, new HashSet<string>()).Shapes));
        Assert.Equal(Text(plain.Shapes), Text(EmGeometry.ForSetup(fx.Setup("plain", []), source).Shapes));
    }

    // ── Gate 6: the .clay files are unchanged ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheClayFiles_AreUnchanged_AfterARun_ACheck_AndASave()
    {
        var source = fx.Source("unchanged", LineWithSeg(nested: true, overlap: true, uncalibratedEnds: true));
        string[] files = [source.AbsolutePath, .. LayoutFlatten.FilesRead(source.View, source.AbsolutePath)];
        var before = files.Select(File.ReadAllBytes).ToList();
        var setup = fx.Setup("unchanged", [Entry("X1/Q3", false)]);
        Assert.Equal(EmRunStatus.Ok, EmRunService.Run(setup, source, fx.Results("unchanged")).Status);
        Assert.Null(EmRunService.Preflight(setup, source).Refusal);
        EmSetupPersistence.SaveToFile(Path.Combine(Path.GetDirectoryName(source.AbsolutePath)!, "unchanged.cem"), setup);
        Assert.Equal(before, files.Select(File.ReadAllBytes).ToList());
    }

    // ── Gate 7: pins stay apart; Keep holds a net out of the cut ──────────────────────────────────────────────────────

    [Fact]
    public void ABoxOverAPourJoiningTwoPins_IsRefused_AndKeepingThePourBuilds()
    {
        // A grounded-strap pour under the segment, joined to both lines: a cell-mode box cuts it in two and the pins stay apart; with
        // the pour kept, it joins A to B and both are refused; pin mode with the pour leaves it touching the cell's metal.
        var pour = LineWithSeg(false, overlap: false);
        // The strap's bar lies wholly inside the box; its two legs reach out under each line's remnant.
        pour.Shapes.Add(new RectShape { Layer = M1, Net = "STRAP", X1 = Um(440), Y1 = Um(-38), X2 = Um(760), Y2 = Um(-30) });
        pour.Shapes.Add(new RectShape { Layer = M1, Net = "STRAP", X1 = Um(440), Y1 = Um(-60), X2 = Um(500), Y2 = Um(0) });
        pour.Shapes.Add(new RectShape { Layer = M1, Net = "STRAP", X1 = Um(700), Y1 = Um(-60), X2 = Um(760), Y2 = Um(0) });
        var kept = fx.Build(pour, [Entry("X1", true, keepOut: [-40, -40, 240, 40]) with { Keep = "STRAP" }]);
        Assert.All(kept.Ports.Where(p => p.Cutout), p => Assert.Contains("meet one piece of metal", p.Refusal, StringComparison.Ordinal));
        var cutThrough = fx.Build(pour, [Entry("X1", true, keepOut: [-40, -40, 240, 40])]);
        Assert.Equal(2, cutThrough.Ports.Count(p => p.Ok));
    }

    // ── R-icp2-8: the panel's Refresh builds off its thread, and drops a stale build ─────────────────────────────────────

    [Fact]
    public async Task Refresh_ReturnsBeforeASlowBuild_AppliesItWhenItLands_AndDropsAStaleOne()
    {
        var source = fx.Source("panel", LineWithSeg(nested: false, overlap: true, uncalibratedEnds: true));
        var vm = new CircuitRF.Ui.Layout.Em.EmSetupEditorViewModel(Path.Combine(Path.GetDirectoryName(source.AbsolutePath)!, "panel.cem"),
                                                                   fx.Setup("panel", [Entry("X1", false)]))
        {
            ResolveLayout = _ => source,
        };
        using var gate = new ManualResetEventSlim(false);
        int built = 0;
        vm.ComponentBuilder = (s, src) => { gate.Wait(TimeSpan.FromSeconds(30)); Interlocked.Increment(ref built); return EmPlanarComponentPorts.Build(s, src); };
        int before = vm.RefreshesApplied;

        vm.Refresh();
        var first = vm.WhenRefreshed;
        Assert.False(first.IsCompleted);
        Assert.Equal(before, vm.RefreshesApplied);

        // The setup moves on while the first build is held: that build is history when it lands.
        vm.Working.ComponentPorts = [Entry("X1", false) with { Z0 = "25" }];
        vm.Refresh();
        var second = vm.WhenRefreshed;
        gate.Set();
        await first;
        await second;

        Assert.Equal(2, built);
        Assert.Equal(before + 1, vm.RefreshesApplied);
        var ports = vm.LastComponentBuild!.Ports.Where(p => p.Ok).ToList();
        Assert.Equal(["X1.A", "X1.B"], ports.Select(p => p.Name));
        Assert.All(ports, p => Assert.Equal(new Complex(25, 0), p.Z0));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static EmComponentPort Entry(string path, bool cellMode, double[]? keepOut = null)
    {
        keepOut ??= cellMode ? [-20, -60, 220, 60] : null;
        return new EmComponentPort(path)
        {
            PortAt = cellMode ? EmComponentPortAt.Cell : EmComponentPortAt.Pin,
            KeepOutUm = keepOut is { } k ? new EmKeepOut(k[0], k[1], k[2], k[3]) : null,
        };
    }

    private static RectShape R(LayerKey l, double x0, double y0, double x1, double y1)
        => new() { Layer = l, X1 = Um(Math.Min(x0, x1)), Y1 = Um(Math.Min(y0, y1)), X2 = Um(Math.Max(x0, x1)), Y2 = Um(Math.Max(y0, y1)) };

    private static void Pin(LayoutView v, string name, double x, double y, double outward, double width)
        => v.Pins.Add(new LayoutPin { Name = name, X = Um(x), Y = Um(y), WidthDbu = Um(width), OutwardDeg = outward, Layer = M1 });

    private static LabelShape Edge(double x, double y, LayoutRotation dir, string text, bool uncalibrated = false) => new()
    {
        Layer = M1, PortLayer = M1, X = Um(x), Y = Um(y), Text = text, Height = Um(20), IsPort = true, PortKind = PlanarPortKind.Edge,
        PortDirection = dir, PortUseEdgeWidth = uncalibrated ? true : null, PortUncalibrated = uncalibrated,
    };

    /// <summary>
    /// A 1200 µm line of 72 µm with a 200 µm gap from 500 to 700 µm (x along the line, or y when nested, where Mid is placed R90),
    /// bridged by Seg (pins A at its left face, B at its right). With <paramref name="overlap"/> the line before it runs 20 µm onto
    /// Seg's pad, so pin mode has a trace to trim. Edge ports at the far ends.
    /// </summary>
    private LayoutView LineWithSeg(bool nested, bool overlap, bool uncalibratedEnds = false)
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        double reach = overlap ? 520 : 500;
        bool u = uncalibratedEnds;
        if (!nested)
        {
            v.Shapes.Add(R(M1, 0, -36, reach, 36));
            v.Shapes.Add(R(M1, 700, -36, 1200, 36));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", u));
            v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2", u));
            v.Instances.Add(fx.Place("Seg", 500, 0, "X1"));
        }
        else
        {
            v.Shapes.Add(R(M1, -36, 0, 36, reach));
            v.Shapes.Add(R(M1, -36, 700, 36, 1200));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R90, "1", u));
            v.Shapes.Add(Edge(0, 1200, LayoutRotation.R270, "2", u));
            v.Instances.Add(fx.Place("Mid", 0, 500, "X1", rotation: 90));
        }
        return v;
    }

    /// <summary>The same layout edited by hand: no instance, the line trimmed to the cut, Uncalibrated edge labels at the faces.</summary>
    private static LayoutView Twin(bool nested, bool cellMode)
    {
        double a = cellMode ? 480 : 500, b = cellMode ? 720 : 700;
        var v = new LayoutView { DbuPerMicron = Dbu };
        if (!nested)
        {
            v.Shapes.Add(R(M1, 0, -36, a, 36));
            v.Shapes.Add(R(M1, b, -36, 1200, 36));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1", uncalibrated: true));
            v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2", uncalibrated: true));
            v.Shapes.Add(Edge(a, 0, LayoutRotation.R180, "3", uncalibrated: true));
            v.Shapes.Add(Edge(b, 0, LayoutRotation.R0, "4", uncalibrated: true));
        }
        else
        {
            v.Shapes.Add(R(M1, -36, 0, 36, a));
            v.Shapes.Add(R(M1, -36, b, 36, 1200));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R90, "1", uncalibrated: true));
            v.Shapes.Add(Edge(0, 1200, LayoutRotation.R270, "2", uncalibrated: true));
            v.Shapes.Add(Edge(0, a, LayoutRotation.R270, "3", uncalibrated: true));
            v.Shapes.Add(Edge(0, b, LayoutRotation.R90, "4", uncalibrated: true));
        }
        return v;
    }

    /// <summary>Three 40 µm lines, 200 µm long, meeting Fet's faces at an 80 µm site from the left (G), the right (D) and below (S).</summary>
    private LayoutView Fet()
    {
        var v = new LayoutView { DbuPerMicron = Dbu };
        v.Shapes.Add(R(M1, -240, -20, -40, 20));
        v.Shapes.Add(R(M1, 40, -20, 240, 20));
        v.Shapes.Add(R(M1, -20, -240, 20, -40));
        v.Shapes.Add(Edge(-240, 0, LayoutRotation.R0, "1"));
        v.Shapes.Add(Edge(240, 0, LayoutRotation.R180, "2"));
        v.Shapes.Add(Edge(0, -240, LayoutRotation.R90, "3"));
        v.Instances.Add(fx.Place("Fet", 0, 0, "X1"));
        return v;
    }

    private static (Complex[] S, int N) S(EmRunResult r)
    {
        var data = r.Data!;
        var cube = data.CubesIn(data.Groups.First(x => data.CubesIn(x).ContainsKey("S")))["S"];
        return (cube.ComplexValues, cube.Axes[1].Values.Length);
    }

    /// <summary>The cut 4-port's ports 3 and 4 closed by an ideal line carrying the whole line's own γ and Z_c over
    /// <paramref name="fraction"/> of its length, at frequency index <paramref name="k"/>.</summary>
    private static Complex[,] CloseWithLine(int k, Complex[] s2, Complex[] s4, double fraction)
    {
        const double z0 = 50;
        Complex s11 = s2[k * 4], s12 = s2[k * 4 + 1], s21 = s2[k * 4 + 2], s22 = s2[k * 4 + 3];
        Complex a = ((1 + s11) * (1 - s22) + s12 * s21) / (2 * s21);
        Complex bb = z0 * ((1 + s11) * (1 + s22) - s12 * s21) / (2 * s21);
        Complex c = ((1 - s11) * (1 - s22) - s12 * s21) / (2 * s21 * z0);
        Complex zc = Complex.Sqrt(bb / c);
        Complex gs = Complex.Log(a + Complex.Sqrt(a * a - 1)) * fraction;
        Complex la = Complex.Cosh(gs), lb = zc * Complex.Sinh(gs), lc = Complex.Sinh(gs) / zc, ld = la;
        Complex den = la + lb / z0 + lc * z0 + ld;
        var t = new[,] { { (la + lb / z0 - lc * z0 - ld) / den, 2 * (la * ld - lb * lc) / den }, { 2 / den, (-la + lb / z0 - lc * z0 + ld) / den } };
        Complex At(int i, int j) => s4[k * 16 + i * 4 + j];
        var m = new Complex[2, 2]; var rhs = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Complex ts = 0, tse = 0;
                for (int q = 0; q < 2; q++) { ts += t[i, q] * At(2 + q, 2 + j); tse += t[i, q] * At(2 + q, j); }
                m[i, j] = (i == j ? 1 : 0) - ts; rhs[i, j] = tse;
            }
        var det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
        var inv = new[,] { { m[1, 1] / det, -m[0, 1] / det }, { -m[1, 0] / det, m[0, 0] / det } };
        var result = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Complex v = At(i, j);
                for (int q = 0; q < 2; q++)
                    for (int r = 0; r < 2; r++) v += At(i, 2 + q) * inv[q, r] * rhs[r, j];
                result[i, j] = v;
            }
        return result;
    }

    public sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("icp2-").FullName;
        public Technology Tech { get; } = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");
        private int _n;

        public Fixture()
        {
            TechPersistence.SaveToFile(Path.Combine(_root, "tech.ctech"), Tech);
            WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
            // Seg: a 200 µm × 72 µm series element, pins A (left face) and B (right face).
            Cell("Seg", c => { Pin(c, "A", 0, 0, 180, 72); Pin(c, "B", 200, 0, 0, 72); c.Shapes.Add(R(M1, 0, -36, 200, 36)); });
            // Mid: Seg placed as Q3, turned so that Mid placed R90 lays it along +y.
            Cell("Mid", c => c.Instances.Add(new LayoutInstance { CellRef = "../../Seg", Mag = 1.0, SchematicId = "Q3" }));
            // Fet: a cross 80 µm across, pins G (left), D (right), S (bottom), each 40 µm wide.
            Cell("Fet", c =>
            {
                Pin(c, "G", -40, 0, 180, 40); Pin(c, "D", 40, 0, 0, 40); Pin(c, "S", 0, -40, 270, 40);
                c.Shapes.Add(R(M1, -40, -20, 40, 20));
                c.Shapes.Add(R(M1, -20, -40, 20, -20));
            });
        }

        private void Cell(string name, Action<LayoutView> draw)
        {
            string dir = CellFolder.CreateCellFolder(_root, name);
            var v = new LayoutView { DbuPerMicron = Dbu };
            draw(v);
            CellCreate.WriteLayoutView(dir, name, v);
        }

        public LayoutInstance Place(string cell, double x, double y, string id, double rotation = 0)
            => new() { CellRef = "../../" + cell, X = Um(x), Y = Um(y), Mag = 1.0, SchematicId = id, RotationDegrees = rotation };

        /// <summary>A line meeting a cell of its own at 500 µm (written here, under <paramref name="cell"/>).</summary>
        public LayoutView LineWith(string cell, Action<LayoutView>? draw = null, Action<LayoutView>? place = null)
        {
            if (draw is not null && !Directory.Exists(Path.Combine(_root, cell))) Cell(cell, draw);
            var v = new LayoutView { DbuPerMicron = Dbu };
            v.Shapes.Add(R(M1, 0, -36, 500, 36));
            v.Shapes.Add(R(M1, 700, -36, 1200, 36));
            v.Shapes.Add(Edge(0, 0, LayoutRotation.R0, "1"));
            v.Shapes.Add(Edge(1200, 0, LayoutRotation.R180, "2"));
            v.Instances.Add(Place(cell, 500, 0, "X1"));
            place?.Invoke(v);
            return v;
        }

        public EmLayoutSource Source(string cell, LayoutView view)
        {
            string name = $"{cell}{Interlocked.Increment(ref _n)}";
            string dir = CellFolder.CreateCellFolder(_root, name);
            CellCreate.WriteLayoutView(dir, name, view);
            string clay = Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories).Single();
            return new EmLayoutSource(clay, LayoutPersistence.LoadFromFile(clay), Tech, Dbu);
        }

        public EmSetup Setup(string name, IReadOnlyList<EmComponentPort> entries, bool site = false, bool top = false) => new()
        {
            Name = name, LayoutRef = name + ".clay", AnalysisKind = EmAnalysisKind.Planar,
            PlanarMesh = site ? new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: false)
                              : new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20),
            Frequency = site ? new FrequencySpec("10", "10", 1, SweepKind.Linear, "GHz", "GHz")
                      : top ? new FrequencySpec("20", "20", 1, SweepKind.Linear, "GHz", "GHz")
                             : new FrequencySpec("5", "20", 2, SweepKind.Linear, "GHz", "GHz"),
            AdaptiveSampling = false,
            ComponentPorts = [.. entries],
        };

        public string Results(string name) => Path.Combine(_root, "results-" + name);

        public EmRunResult Run(string name, LayoutView view, IReadOnlyList<EmComponentPort> entries, bool site = false, bool top = false)
            => EmRunService.Run(Setup(name, entries, site, top), Source(name, view), Results(name));

        public EmPlanarComponentPortBuild Build(LayoutView view, IReadOnlyList<EmComponentPort> entries, Action<EmSetup>? tweak = null)
        {
            var setup = Setup("b", entries);
            tweak?.Invoke(setup);
            return EmPlanarComponentPorts.Build(setup, Source("b", view))!;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
