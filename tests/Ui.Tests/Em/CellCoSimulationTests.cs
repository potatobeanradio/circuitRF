// brief-icp-5 — the co-simulation netlist puts each cut instance's own model back on its pins. No EM solve: the board is a small
// synthetic Touchstone (LCP-7's gates do the same), and one gate reads ICP-4's real Planar build for what it hands the writer.

using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class CellCoSimulationTests(CellPinReferenceTests.Fixture fx, ITestOutputHelper output)
    : IClassFixture<CellPinReferenceTests.Fixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("icp5-cosim-").FullName;
    private static readonly double[] FreqsGHz = [1, 2, 3];
    private static readonly EmSetup Setup = new() { Name = "chip", Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz") };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Gate 1 (R-icp5-2, R-icp5-3) — a resistor tee sub-cell X1 (pins A, B, C) cut out with A and B on board ports 2 and 3,
    /// C unported and on ground in the schematic it is placed in. The netlist's S11 equals the board closed by the tee's own
    /// two-port with C grounded, S' = S_ee + S_ek Γ (I − S_kk Γ)⁻¹ S_ke.</summary>
    [Fact]
    public void ATeeCutOut_ClosesTheBoard_AsTheTerminatedNetworkFormulaDoes()
    {
        Touchstone(Path.Combine(_dir, "chip.s3p"), 3, (i, j, f) => Complex.FromPolarCoordinates(i == j ? 0.25 + 0.05 * i : 0.15 + 0.03 * (i + j), 0.5 * i + 0.6 * j + 0.2 * f));
        var lib = new Library("lib");
        var tee = new Cell("Tee");
        tee.Ports.AddRange(["A", "B", "C"]);
        tee.Instances.Add(new Instance("R1", "R", ["A", "m"], [new ParameterAssignment("R", "20", "Ohm")]));
        tee.Instances.Add(new Instance("R2", "R", ["B", "m"], [new ParameterAssignment("R", "30", "Ohm")]));
        tee.Instances.Add(new Instance("R3", "R", ["m", "C"], [new ParameterAssignment("R", "40", "Ohm")]));
        lib.Cells.Add(tee);
        var models = Models(lib, new Instance("X1", "Tee", ["na", "nb", "0"], []));
        var cut = Cut("X1", ["X1"], Pin("A"), Pin("B"), Pin("C", EmPinClass.Floating));
        var ports = new List<EmCoSimPort> { new(1, "1", "50", null), CutPort(2, cut, "A"), CutPort(3, cut, "B") };

        string cnl = Path.Combine(_dir, "chip_cosim.cnl");
        File.WriteAllText(cnl, EmCoSimNetlist.Text(Setup, "chip.s3p", ports, models));
        var x1 = Read(File.ReadAllText(cnl)).TestBench.Instances.Single(i => i.InstanceName == "X1");
        Assert.Equal(["p2", "p3", "0"], x1.NetBindings);
        var run = SchematicRunService.RunNetlist(cnl, _dir);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var s = run.DataSets[0]["S"];

        var board = RfCore.TouchstoneIO.ReadFile(Path.Combine(_dir, "chip.s3p"));
        double r1 = 20, r2 = 30, r3 = 40;
        for (int f = 0; f < FreqsGHz.Length; f++)
        {
            Complex B(int i, int j) => board.Matrices[f][i, j];
            // Γ = (Z − 50)(Z + 50)⁻¹ of the tee's Z = [[R1+R3, R3], [R3, R2+R3]].
            Complex[,] zm = { { r1 + r3 - 50, r3 }, { r3, r2 + r3 - 50 } }, zp = { { r1 + r3 + 50, r3 }, { r3, r2 + r3 + 50 } };
            var gamma = Mul(zm, Inv(zp));
            Complex[,] m = { { 1 - (B(1, 1) * gamma[0, 0] + B(1, 2) * gamma[1, 0]), -(B(1, 1) * gamma[0, 1] + B(1, 2) * gamma[1, 1]) },
                             { -(B(2, 1) * gamma[0, 0] + B(2, 2) * gamma[1, 0]), 1 - (B(2, 1) * gamma[0, 1] + B(2, 2) * gamma[1, 1]) } };
            var mi = Inv(m);
            Complex[] x = [mi[0, 0] * B(1, 0) + mi[0, 1] * B(2, 0), mi[1, 0] * B(1, 0) + mi[1, 1] * B(2, 0)];
            Complex expected = B(0, 0);
            for (int i = 0; i < 2; i++)
                for (int k = 0; k < 2; k++) expected += B(0, 1 + i) * gamma[i, k] * x[k];
            var got = s.At("freq", f).At("i", 0).At("j", 0).ComplexValues[0];
            Assert.True((got - expected).Magnitude < 1e-6, $"S11 at {FreqsGHz[f]} GHz: {got} against {expected}");
        }
    }

    /// <summary>Gate 2 (D2) — X1/R1, a resistor inside cell Stage whose R is 2·Rp, Rp bound by X1 to the VAR Rtop: written with the value
    /// the elaborated design gives it, and the comment names the parameter.</summary>
    [Fact]
    public void ANestedInstance_IsWrittenWithTheElaboratedValue_AndTheCommentNamesIt()
    {
        var lib = new Library("lib");
        var amp = new Cell("Stage");
        amp.Ports.AddRange(["p", "n"]);
        amp.Parameters.Add(new ParameterDeclaration("Rp", "10"));
        amp.Instances.Add(new Instance("R1", "R", ["p", "n"], [new ParameterAssignment("R", "Rp*2", "Ohm")]));
        lib.Cells.Add(amp);
        var x1 = new Instance("X1", "Stage", ["a", "0"], [new ParameterAssignment("Rp", "Rtop")]);
        var models = Models(lib, x1) with { Variables = [new Variable("Rtop", "25", null)] };

        var tb = new TestBench("tb");
        tb.GlobalVariables.Add(new Variable("Rtop", "25", null));
        tb.Instances.Add(x1);
        double elaborated = new Elaborator(lib).Elaborate(tb).Components.Single(c => c.InstancePath == "X1.R1").Parameters["R"].AsReal();

        var cut = Cut("X1/R1", ["X1", "R1"], Pin("1"), Pin("2"));
        string text = EmCoSimNetlist.Text(Setup, "chip.s2p", [CutPort(1, cut, "1"), CutPort(2, cut, "2")], models);
        output.WriteLine(text);
        var r1 = Read(text).TestBench.Instances.Single(i => i.InstanceName == "X1_R1");
        Assert.Equal(elaborated, double.Parse(r1.Overrides.Single(o => o.Name == "R").Expression, CultureInfo.InvariantCulture));
        Assert.Equal(50, elaborated);
        Assert.Contains("X1_R1 is X1/R1", text, StringComparison.Ordinal);
        Assert.Contains("and R is written as the value the design gives it there: a VAR that drove it no longer tunes it here.", text, StringComparison.Ordinal);
    }

    /// <summary>Gate 3 (R-icp5-2 step 2) — a hand-placed MIM capacitor PCell with no schematic counterpart gets the component its generator
    /// makes, at the snapshot's W and L (its capacitance), in the component's own parameter names.</summary>
    [Fact]
    public void AHandPlacedPCell_GetsItsGeneratorsComponent_AtTheSnapshotsValues()
    {
        var cut = Cut("@3", [null], Pin("1"), Pin("2")) with { GeneratorId = "MIMCAP" };
        cut = cut with { PCellParameters = new Dictionary<string, PCellValue> { ["W"] = 60e-6, ["L"] = 80e-6, ["Connection"] = PCellValue.Text("series") } };
        string text = EmCoSimNetlist.Text(Setup, "chip.s2p", [CutPort(1, cut, "1"), CutPort(2, cut, "2")], EmCoSimModels.None);
        output.WriteLine(text);
        var mim = Read(text).TestBench.Instances.Single(i => i.InstanceName == "at3");
        Assert.Equal("MIMCAP", mim.Reference);
        Assert.Equal(["p1", "p2"], mim.NetBindings);
        Assert.Equal(["L=8E-05", "W=6E-05"], mim.Overrides.Select(o => $"{o.Name}={o.Expression}"));
        Assert.Contains("The model came from the cell: MIMCAP's component at the generator's values (L, W).", text, StringComparison.Ordinal);
    }

    /// <summary>Gate 4 (R-icp5-3) — pins IN and OUT against a model whose terminals are A, B, C: a placeholder, and the warning lists
    /// both sets.</summary>
    [Fact]
    public void PinNamesThatDoNotMatch_GiveAPlaceholder_ListingBothSets()
    {
        var lib = new Library("lib");
        var tee = new Cell("Tee");
        tee.Ports.AddRange(["A", "B", "C"]);
        lib.Cells.Add(tee);
        var cut = Cut("X1", ["X1"], Pin("IN"), Pin("OUT"));
        var ports = new List<EmCoSimPort> { CutPort(1, cut, "IN"), CutPort(2, cut, "OUT") };
        var closures = EmCoSimNetlist.Closures(ports, Models(lib, new Instance("X1", "Tee", ["a", "b", "c"], [])));
        Assert.All(closures, c => Assert.Equal(EmCoSimCloseKind.Placeholder, c.Kind));
        Assert.Contains(closures.SelectMany(c => c.Warnings), w => w.Contains("its pins (IN, OUT) do not match its model's terminals (A, B, C)", StringComparison.Ordinal));
    }

    /// <summary>Gate 5 (R-icp5-4) — every name derived from a path survives the reader, none collides with a footprint part, a Port line
    /// or another derived name, and each comment carries its path.</summary>
    [Fact]
    public void EveryDerivedName_SurvivesTheReader()
    {
        string[] paths = ["X1/Q3", "@2", "X1/@2", "P1", "1A", "X1/Q3 b"];
        var ports = new List<EmCoSimPort> { new(1, "U1", "50", new EmCoSimPart("X1_Q3", "1", "2", ["1", "2"], null, null, (null, null), false)) };
        int n = 2;
        foreach (string path in paths) ports.Add(CutPort(n++, Cut(path, [], Pin("A")), "A"));
        string text = EmCoSimNetlist.Text(Setup, "chip.s7p", ports, EmCoSimModels.None);
        output.WriteLine(text);
        var (_, tb) = Read(text);
        Assert.Empty(tb.ReadWarnings);
        string[] expected = ["X1_Q3", "X1_Q3_2", "at2", "X1_at2", "P1_2", "X1A", "X1_Q3_b"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), tb.Instances.Where(i => i.InstanceName != EmCoSimNetlist.BoardInstance).Select(i => i.InstanceName).Order(StringComparer.Ordinal));
        Assert.Equal(tb.Instances.Count, tb.Instances.Select(i => i.InstanceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string path in paths) Assert.Contains($"({path}.A)", text, StringComparison.Ordinal);
    }

    /// <summary>Gate 7 (R-icp5-3) — X1.B touches X2.A, both cut out: the two terminals share one node; X2.G is tied to ground in the
    /// entry, so its terminal is on 0.</summary>
    [Fact]
    public void TwoAbuttingInstances_ShareANode_AndAGroundedPinIsOnGround()
    {
        var lib = new Library("lib");
        var two = new Cell("Two"); two.Ports.AddRange(["A", "B"]); lib.Cells.Add(two);
        var three = new Cell("Three"); three.Ports.AddRange(["A", "B", "G"]); lib.Cells.Add(three);
        var models = Models(lib, new Instance("X1", "Two", ["a", "m"], []), new Instance("X2", "Three", ["m", "b", "g"], []));
        var x1 = Cut("X1", ["X1"], Pin("A"), Pin("B", EmPinClass.Abutting, "X2.A"));
        var x2 = Cut("X2", ["X2"], Pin("A", EmPinClass.Abutting, "X1.B"), Pin("B"), Pin("G", EmPinClass.Grounded));
        string text = EmCoSimNetlist.Text(Setup, "chip.s2p", [CutPort(1, x1, "A"), CutPort(2, x2, "B")], models);
        output.WriteLine(text);
        var tb = Read(text).TestBench;
        Assert.Equal(["p1", "j_X1_B"], tb.Instances.Single(i => i.InstanceName == "X1").NetBindings);
        Assert.Equal(["j_X1_B", "p2", "0"], tb.Instances.Single(i => i.InstanceName == "X2").NetBindings);
        Assert.True(EmCoSimNetlist.DrawnFromNetlist([CutPort(1, x1, "A")]));
    }

    /// <summary>Gate 8 (ICP-4's gate 4) and R-icp5-1 — ICP-4's Tri cell with B and C referenced to E, built on Planar: each port carries
    /// the instance and its pins' classes, and the netlist puts the model's E terminal on 0 with the sentence saying it is the N-port's
    /// reference, not ground in the chip.</summary>
    [Fact]
    public void APinReferencedPort_PutsTheReferenceTerminalOnGround_AndSaysWhatThatMeans()
    {
        var entry = new EmComponentPort("X1").WithPins(new Dictionary<string, EmPinOptions>
        {
            ["B"] = new(Reference: "E"), ["C"] = new(Reference: "E"), ["E"] = new(Port: false),
        });
        var build = fx.Build(fx.TriLine(), [entry]);
        var built = build.Ports.Where(p => p.Ok).ToList();
        Assert.Equal(2, built.Count);
        var part = Assert.Single(built.Select(p => p.CutPart).Distinct());
        Assert.Equal(["X1"], part!.SchematicChain);
        Assert.Equal([EmPinClass.Routed, EmPinClass.Routed, EmPinClass.Return], part.Pins.Select(p => p.Class));

        var ports = new List<EmCoSimPort> { new(1, "1", "50", null), new(2, "2", "50", null), new(3, "3", "50", null) };
        ports.AddRange(built.Select(p => new EmCoSimPort(p.Number, p.Name, "50", EmCoSimPart.Of(p))));
        var lib = new Library("lib");
        var q = new Cell("Tri"); q.Ports.AddRange(["B", "C", "E"]); lib.Cells.Add(q);
        string text = EmCoSimNetlist.Text(Setup, "chip.s5p", ports, Models(lib, new Instance("X1", "Tri", ["b", "c", "e"], [])));
        output.WriteLine(text);
        Assert.Equal(["p4", "p5", "0"], Read(text).TestBench.Instances.Single(i => i.InstanceName == "X1").NetBindings);
        Assert.Contains("X1.E is the reference of X1.B and X1.C: its terminal is on 0, tied to BOARD's reference, which is not the same as " +
                        "the pin being grounded in the chip.", text, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static EmCoSimCutPin Pin(string name, EmPinClass cls = EmPinClass.Routed, string? abuts = null) => new(name, null, cls, abuts, null);

    private static EmCoSimCut Cut(string path, IReadOnlyList<string?> chain, params EmCoSimCutPin[] pins) => new(path, "Cell", null, chain, pins);

    private static EmCoSimPort CutPort(int number, EmCoSimCut cut, string pin)
        => new(number, $"{cut.Path}.{pin}", "50", new EmCoSimPart(cut.Path, pin, null, [], cut.SchematicChain.LastOrDefault(), null, (null, null), false) { Cut = cut });

    private static EmCoSimModels Models(Library lib, params Instance[] instances)
        => new(instances.ToDictionary(i => i.InstanceName, StringComparer.OrdinalIgnoreCase), lib, null);

    private (Library Library, TestBench TestBench) Read(string text) => new CnlReader().Read(text, "chip_cosim", _dir);

    private static Complex[,] Inv(Complex[,] a)
    {
        var det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        return new[,] { { a[1, 1] / det, -a[0, 1] / det }, { -a[1, 0] / det, a[0, 0] / det } };
    }

    private static Complex[,] Mul(Complex[,] a, Complex[,] b)
    {
        var c = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++) c[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j];
        return c;
    }

    /// <summary>A reciprocal Touchstone of <paramref name="n"/> ports at <see cref="FreqsGHz"/>, row by row, four pairs a line.</summary>
    private static void Touchstone(string path, int n, Func<int, int, int, Complex> s)
    {
        var sb = new StringBuilder("# GHz S RI R 50\n");
        for (int f = 0; f < FreqsGHz.Length; f++)
        {
            sb.Append(FreqsGHz[f].ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (j > 0 && j % 4 == 0) sb.Append('\n');
                    var v = s(Math.Min(i, j), Math.Max(i, j), f);
                    sb.Append(' ').Append(v.Real.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(v.Imaginary.ToString("R", CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }
        }
        File.WriteAllText(path, sb.ToString());
    }
}
