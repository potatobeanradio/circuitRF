using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-lcp-7 — co-simulation from a layout's Planar .cem. Gate 2 closes a synthetic board S-matrix through the written netlist and
// through the terminated-network formula; gates 3 and 4 run LCP-1's two-part board (about 2 s) with Create SnP schematic on.
// Gate 1 (a .c3d's netlist byte-identical to before) is CoSimC3dBytesTests.
public sealed class PlanarCoSimTests(PlanarComponentPortTests.Board board) : IClassFixture<PlanarComponentPortTests.Board>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lcp7-cosim-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly double[] FreqsGHz = [1, 2, 3];

    /// <summary>
    /// Gate 2 (R-lcp7-1, R-lcp7-3) — a 5-port board: two connector ports, C1 across port 3, and a three-pad part U1 (pins IN, GND,
    /// OUT) across ports 4 and 5, its model a 3-port file. The netlist's S-parameters equal the board closed by the parts' own
    /// S-matrix, S' = S_ee + S_ek Γ (I − S_kk Γ)⁻¹ S_ke, with U1's GND terminal on ground (its port 2 shorted).
    /// </summary>
    [Fact]
    public void TheNetlist_ClosesTheBoard_AsTheTerminatedNetworkFormulaDoes()
    {
        Touchstone(Path.Combine(_dir, "board.s5p"), 5, (i, j, f) => Polar(i == j ? 0.2 + 0.05 * i : 0.12 + 0.02 * (i + j), 0.4 * i + 0.7 * j + 0.3 * f));
        Touchstone(Path.Combine(_dir, "u1.s3p"), 3, (i, j, f) => Polar(i == j ? 0.3 : i == 0 && j == 2 || i == 2 && j == 0 ? 0.8 : 0.1, 0.5 * (i + j) - 0.2 * f));

        static EmCoSimPart U1(string pin) => new("U1", pin, "GND", ["IN", "GND", "OUT"], null, null, ("N_" + pin, "GND"), false);
        var ports = new List<EmCoSimPort>
        {
            new(1, "1", "50", null),
            new(2, "2", "50", null),
            new(3, "C1", "50", new EmCoSimPart("C1", "1", "2", ["1", "2"], null, null, ("N3", "GND"), false)),
            new(4, "U1.IN", "50", U1("IN")),
            new(5, "U1.OUT", "50", U1("OUT")),
        };
        var models = new EmCoSimModels(new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase)
        {
            ["C1"] = new("C1", "C", ["a", "b"], [new ParameterAssignment("C", "2", "pF")]),
            ["U1"] = new("U1", "SnP", ["a", "b", "c"], [new ParameterAssignment("NumPorts", "3"), new ParameterAssignment("File", "u1.s3p")]),
        }, null, null);
        var setup = new EmSetup { Name = "board", Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz") };

        string cnl = Path.Combine(_dir, EmCoSimNetlist.FileNameFor(setup));
        File.WriteAllText(cnl, EmCoSimNetlist.Text(setup, "board.s5p", ports, models));
        Assert.Contains("SnP:U1  p4  0  p5", File.ReadAllText(cnl), StringComparison.Ordinal);
        var run = SchematicRunService.RunNetlist(cnl, _dir);
        Assert.True(run.Status == RunStatus.Success, run.StatusMessage);
        var s = run.DataSets[0]["S"];

        var boardS = RfCore.TouchstoneIO.ReadFile(Path.Combine(_dir, "board.s5p"));
        var partS = RfCore.TouchstoneIO.ReadFile(Path.Combine(_dir, "u1.s3p"));
        for (int f = 0; f < FreqsGHz.Length; f++)
        {
            Complex B(int i, int j) => boardS.Matrices[f][i, j];
            Complex U(int i, int j) => partS.Matrices[f][i, j];
            // The load on board ports 3, 4, 5: C1's reflection, and U1's ports 1 and 3 with port 2 shorted (Γ = −1).
            var zc = 1 / (new Complex(0, 2 * Math.PI * FreqsGHz[f] * 1e9) * 2e-12);
            var gamma = new Complex[3, 3];
            gamma[0, 0] = (zc - 50) / (zc + 50);
            int[] kept = [0, 2];
            for (int a = 0; a < 2; a++)
                for (int b = 0; b < 2; b++)
                    gamma[1 + a, 1 + b] = U(kept[a], kept[b]) - U(kept[a], 1) * U(1, kept[b]) / (1 + U(1, 1));

            // M = I − S_kk Γ; S' = S_ee + S_ek Γ M⁻¹ S_ke.
            var m = new Complex[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    Complex sum = i == j ? 1 : 0;
                    for (int k = 0; k < 3; k++) sum -= B(2 + i, 2 + k) * gamma[k, j];
                    m[i, j] = sum;
                }
            for (int e = 0; e < 2; e++)
                for (int g = 0; g < 2; g++)
                {
                    var x = Solve(m, [B(2, g), B(3, g), B(4, g)]);
                    Complex expected = B(e, g);
                    for (int i = 0; i < 3; i++)
                        for (int k = 0; k < 3; k++) expected += B(e, 2 + i) * gamma[i, k] * x[k];
                    var got = s.At("freq", f).At("i", e).At("j", g).ComplexValues[0];
                    Assert.True((got - expected).Magnitude < 1e-6, $"S({e + 1},{g + 1}) at {FreqsGHz[f]} GHz: {got} against {expected}");
                }
        }
    }

    /// <summary>
    /// Gates 3 and 4 (R-lcp7-2) — the Planar run writes <c>board_cosim.cnl</c> with each part from its port's node to ground, makes
    /// the <c>board_cosim</c> cell in place of <c>board_sim</c>, and warns that the parts have no model on the board's schematic
    /// (it has none), in the sentence a <c>.c3d</c> gives. The netlist it wrote simulates.
    /// </summary>
    [Fact]
    public void APlanarRun_WritesCoSim_AndItsCellReplacesSim()
    {
        var setup = PlanarComponentPortTests.Setup(["C1", "C2"]);
        setup.CreateSnpSchematic = true;
        var r = EmRunService.Run(setup, board.Source(), Path.Combine(board.Workspace, "results"));
        Assert.Equal(EmRunStatus.Ok, r.Status);

        Assert.Equal(EmSnpSchematicOutcome.Created, r.CoSim!.Outcome);
        Assert.Equal(Path.Combine(board.Workspace, "board_cosim.cnl"), r.CoSim.Path);
        string[] lines = [.. File.ReadAllLines(r.CoSim.Path!).Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " "))];
        Assert.Contains("SnP:BOARD p1 p2 p3 p4 NumPorts=4 File=\"results/board.s4p\"", lines);
        Assert.Contains("Port:P1 p1 0 Num=1 Z=50 Ohm", lines);
        Assert.Contains("Port:P2 p2 0 Num=2 Z=50 Ohm", lines);
        Assert.Contains("C:C1 p3 0 C=C1_value", lines);
        Assert.Contains("C:C2 p4 0 C=C2_value", lines);
        Assert.Contains("; BOARD's ports: p1 is 1, p2 is 2, p3 is C1, p4 is C2.", lines);

        Assert.Equal(EmSnpSchematicOutcome.Created, r.SnpSchematic!.Outcome);
        Assert.Equal("board_cosim", r.SnpSchematic.CellName);
        Assert.False(Directory.Exists(Path.Combine(board.Workspace, "board_sim")));

        Assert.Contains("C1, C2 have no model on the board's schematic, so the co-simulation netlist will carry a placeholder for each.", r.Warnings);

        var sim = SchematicRunService.RunNetlist(r.CoSim.Path!, board.Workspace);
        Assert.True(sim.Status == RunStatus.Success, sim.StatusMessage);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Complex Polar(double magnitude, double phase) => Complex.FromPolarCoordinates(magnitude, phase);

    /// <summary>A Touchstone of <paramref name="n"/> ports at <see cref="FreqsGHz"/>, reciprocal, row by row, four pairs a line.</summary>
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
                    sb.Append(' ').Append(v.Real.ToString("R", CultureInfo.InvariantCulture))
                      .Append(' ').Append(v.Imaginary.ToString("R", CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>A 3×3 complex solve, by Gaussian elimination with partial pivoting.</summary>
    private static Complex[] Solve(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        var m = (Complex[,])a.Clone();
        var x = (Complex[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = Enumerable.Range(c, n - c).MaxBy(r => m[r, c].Magnitude);
            for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
            (x[c], x[p]) = (x[p], x[c]);
            for (int r = c + 1; r < n; r++)
            {
                var factor = m[r, c] / m[c, c];
                for (int k = c; k < n; k++) m[r, k] -= factor * m[c, k];
                x[r] -= factor * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            for (int k = r + 1; k < n; k++) x[r] -= m[r, k] * x[k];
            x[r] /= m[r, r];
        }
        return x;
    }
}
