// ================================================================
//  PortTypesExampleTests.cs — the shipped Port Types example (PWL-7): every port type on the simplest structure it takes,
//  measured against a closed form and against each other.
//
//  `expected-numbers.json` beside the README holds every accuracy figure the README's tables print (the agreement matrices
//  included, not the times). The routine gates: `check` is clean on the workspace, every quoted value's text is in the
//  README, and the Choosing a port table is the same, cell by cell, in the README and in the user docs. The benchmark gate
//  re-runs each recorded setup and recomputes its figures against its reference, as the harness that wrote them did.
// ================================================================

using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class PortTypesExampleTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-pwl7-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    /// <summary><c>check</c> on the workspace, as a process: every document elaborates and every port resolves, wall ports on a
    /// box-shaped line included (their end-run check once refused a line drawn as a box), with no warning.</summary>
    [KernelFact]
    public void CheckIsClean()
    {
        using var doc = JsonDocument.Parse(Cli("check", Root(), "--json"));
        var diags = doc.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        var errors = diags.Where(d => d.GetProperty("severity").GetString() == "error").Select(d => d.GetProperty("message").GetString()!).ToList();
        var warnings = diags.Where(d => d.GetProperty("severity").GetString() == "warning").Select(d => d.GetProperty("message").GetString()!).ToList();
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    /// <summary>R-pwl7-3 — the Choosing a port table is one guide in two places: the README's and the user docs' (EM Setup ▸
    /// Choosing a port) are the same, row by row and cell by cell, whitespace aside.</summary>
    [Fact]
    public void TheChoosingAPortTable_IsTheSameInTheReadmeAndTheUserDocs()
    {
        var readme = Table(File.ReadAllText(Path.Combine(Root(), "README.md")), "## Choosing a port");
        var docs = Table(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "user", "src", "reference", "em-setup.md")), "## Choosing a port");
        Assert.True(readme.Count >= 14, $"the README's table has {readme.Count} rows");
        Assert.Equal(readme.Count, docs.Count);
        for (int r = 0; r < readme.Count; r++) Assert.Equal(readme[r], docs[r]);
    }

    /// <summary>R-pwl7-2 — every figure the README's tables print is in expected-numbers.json, and its text is in the README.</summary>
    [Fact]
    public void TheReadmeCitesEveryNumberInTheFile()
    {
        string readme = File.ReadAllText(Path.Combine(Root(), "README.md"));
        foreach (var cell in Numbers().Cells)
            foreach (var row in cell.Rows)
                foreach (string text in row.Readme)
                    Assert.True(readme.Contains(text, StringComparison.Ordinal), $"{cell.Cell} {row.Port} · {row.Solver}: '{text}' is not in the README");
    }

    /// <summary>R-pwl7-2 — each recorded setup, run again, reproduces its recorded accuracy figures against its reference to the
    /// file's tolerances. Hours with Palace (each Palace run is capped by its own four frequencies, not here).</summary>
    [CircuitRF.Ui.Tests.Em3d.PalaceFact]
    [Trait("Category", "Benchmark")]
    public void EveryRecordedRun_ReproducesItsFigures()
    {
        var numbers = Numbers();
        var tol = numbers.Tolerance;
        var moved = new List<string>();
        foreach (var cell in numbers.Cells)
        {
            var reference = Reference(cell);
            foreach (var row in cell.Rows)
            {
                string snp = Path.Combine(_tmp, $"{cell.Cell} {row.Port} {row.Solver}".Replace(' ', '_') + $".s{cell.Ports}p");
                string path = Path.Combine(Root(), row.Document);
                var args = row.Setup is { Length: > 0 } s ? new[] { "em", path, "--setup", s, "-o", snp } : ["em", path, "-o", snp];
                var wall = Stopwatch.StartNew();
                Cli(args);
                output.WriteLine($"{cell.Cell} {row.Port} · {row.Solver}: {wall.Elapsed.TotalSeconds:F0} s");
                if (!File.Exists(snp)) { moved.Add($"{cell.Cell} {row.Port} · {row.Solver}: no result"); continue; }
                var got = Figures(TouchstoneIO.ReadFile(snp), reference, cell);
                void Hold(string what, double? recorded, double? now, double t)
                {
                    if (recorded is { } r && now is { } n && Math.Abs(r - n) > t)
                        moved.Add($"{cell.Cell} {row.Port} · {row.Solver}: {what} {n:G5}, recorded {r:G5} (± {t})");
                }
                Hold("|ΔS11|", row.DS11, got.DS11, tol.DS);
                Hold("|ΔS21|", row.DS21, got.DS21, tol.DS);
                Hold("∠S21 error", row.PhaseDeg, got.PhaseDeg, tol.PhaseDeg);
                Hold("VSWR", row.Vswr, got.Vswr, tol.Vswr);
            }
        }
        Assert.True(moved.Count == 0, "Port Types results moved from the recorded ones:\n" + string.Join("\n", moved));
    }

    // ── the figures, as the harness computes them ───────────────────────────────────────────────

    private sealed record Got(double? DS11, double? DS21, double? PhaseDeg, double? Vswr);

    /// <summary>Worst |ΔS11|, |ΔS21|, ∠S21 error and VSWR of the reflections, over the frequencies both have.</summary>
    private static Got Figures(SNP s, Dictionary<double, Complex[,]>? reference, ExpectedCell cell)
    {
        if (reference is null) return new(null, null, null, null);
        double dS11 = 0, dS21 = 0, ph = 0, vswr = 1;
        for (int k = 0; k < s.Frequencies.Length; k++)
        {
            double f = Math.Round(s.Frequencies[k]);
            if (!reference.TryGetValue(f, out var r)) continue;
            var m = s.Matrices[k];
            var (ti, tj) = (cell.Through[0] - 1, cell.Through[1] - 1);
            dS11 = Math.Max(dS11, (m[0, 0] - r[0, 0]).Magnitude);
            dS21 = Math.Max(dS21, (m[ti, tj] - r[ti, tj]).Magnitude);
            ph = Math.Max(ph, Math.Abs((m[ti, tj] / r[ti, tj]).Phase * 180 / Math.PI));
            foreach (int i in cell.Reflections) vswr = Math.Max(vswr, RfHelpers.VswrFromGamma(r[i - 1, i - 1], m[i - 1, i - 1]));
        }
        return new(dS11, dS21, ph, vswr);
    }

    /// <summary>The cell's reference by frequency (Hz): its closed form's test bench (the … Reference cell) run, or the exact formula.</summary>
    private Dictionary<double, Complex[,]>? Reference(ExpectedCell cell)
    {
        const double C0 = 299_792_458.0;
        var freqs = cell.ReferenceGHz.Select(g => Math.Round(g * 1e9)).ToList();
        switch (cell.Reference)
        {
            case "waveguide":
                return freqs.ToDictionary(f => f, f =>
                {
                    double k0 = 2 * Math.PI * f / C0, beta = Math.Sqrt(k0 * k0 - Math.Pow(Math.PI / 22.86e-3, 2));
                    var t = Complex.Exp(-Complex.ImaginaryOne * beta * 30e-3);
                    return new Complex[,] { { 0, t }, { t, 0 } };
                });
            case "slab":
                return freqs.ToDictionary(f => f, f => Slab(f));
            case null:
                return null;
            default:
                Directory.CreateDirectory(_tmp);
                string snp = Path.Combine(_tmp, $"reference_{cell.Ports}.s{cell.Ports}p");
                Cli("sparam", Path.Combine(Root(), $"{cell.Reference} Reference", "schematic", $"{cell.Reference} Reference.csch"), "-o", snp);
                var s = TouchstoneIO.ReadFile(snp);
                return Enumerable.Range(0, s.Frequencies.Length).ToDictionary(k => Math.Round(s.Frequencies[k]), k =>
                {
                    int n = cell.Ports; var m = new Complex[n, n];
                    for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = s.Matrices[k][i, j];
                    return m;
                });
        }

        static Complex[,] Slab(double f)
        {
            // normal incidence on 5 mm of air, 3 mm of εr 4 and 5 mm of air, mode-normalised, by ABCD in the wave impedance;
            // the Floquet cell's TE00 ports are 1 (below) and 3 (above)
            const double Eta0 = 376.730313668;
            double k0 = 2 * Math.PI * f / C0;
            Complex[,] Sec(double eps, double l)
            {
                double k = k0 * Math.Sqrt(eps), z = Eta0 / Math.Sqrt(eps);
                return new Complex[,] { { Math.Cos(k * l), Complex.ImaginaryOne * z * Math.Sin(k * l) },
                                        { Complex.ImaginaryOne * Math.Sin(k * l) / z, Math.Cos(k * l) } };
            }
            Complex[,] Mul(Complex[,] a, Complex[,] b) => new Complex[,]
            {
                { a[0, 0] * b[0, 0] + a[0, 1] * b[1, 0], a[0, 0] * b[0, 1] + a[0, 1] * b[1, 1] },
                { a[1, 0] * b[0, 0] + a[1, 1] * b[1, 0], a[1, 0] * b[0, 1] + a[1, 1] * b[1, 1] },
            };
            var m = Mul(Mul(Sec(1, 5e-3), Sec(4, 3e-3)), Sec(1, 5e-3));
            Complex den = m[0, 0] + m[0, 1] / Eta0 + m[1, 0] * Eta0 + m[1, 1];
            Complex s11 = (m[0, 0] + m[0, 1] / Eta0 - m[1, 0] * Eta0 - m[1, 1]) / den, s21 = 2 / den;
            var s = new Complex[4, 4];
            s[0, 0] = s[2, 2] = s[1, 1] = s[3, 3] = s11;
            s[2, 0] = s[0, 2] = s[3, 1] = s[1, 3] = s21;
            return s;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The rows of the first table after <paramref name="heading"/>, each a list of its cells, whitespace-normalised.</summary>
    private static List<string> Table(string text, string heading)
    {
        int at = text.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no '{heading}'");
        var rows = new List<string>();
        bool inTable = false;
        foreach (string raw in text[at..].Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('|')) { inTable = true; if (!Regex.IsMatch(line, @"^\|[\s:|-]+\|$")) rows.Add(Regex.Replace(line, @"\s+", " ")); }
            else if (inTable) break;
        }
        return rows;
    }

    private static string Root() => Path.Combine(RepoRoot(), "examples", "Port Types");

    private static string RepoRoot()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
        throw new InvalidOperationException("repo root not found");
    }

    /// <summary>The CLI as a process; its stdout.</summary>
    private static string Cli(params string[] args)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(PortTypesExampleTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return outTask.GetAwaiter().GetResult();
    }

    // ── expected-numbers.json ───────────────────────────────────────────────────────────────────

    private sealed record ExpectedRow(string Port, string Solver, string Document, string? Setup, double? DS11, double? DS21, double? PhaseDeg,
                                      double? Vswr, double? Eeff, List<string> Readme);

    private sealed record ExpectedMatrix(List<string> Names, List<List<double?>> Vswr, List<List<double?>> DS21);

    private sealed record ExpectedCell(string Cell, string? Reference, int Ports, List<int> Through, List<int> Reflections,
                                       List<double> ReferenceGHz, List<ExpectedRow> Rows, ExpectedMatrix Matrix);

    private sealed record Tolerances(double DS, double PhaseDeg, double Vswr);

    private sealed record ExpectedNumbers(string About, Tolerances Tolerance, List<ExpectedCell> Cells);

    private static ExpectedNumbers Numbers()
        => JsonSerializer.Deserialize<ExpectedNumbers>(File.ReadAllText(Path.Combine(Root(), "expected-numbers.json")))!;
}
