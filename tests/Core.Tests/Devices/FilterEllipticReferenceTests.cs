using System.Globalization;
using System.Runtime.CompilerServices;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The elliptic and inverse-Chebyshev ladders against <c>testdata/filter-elliptic</c>, an
/// independent 120-digit reference written by a different route (mpmath's own Jacobi functions,
/// polynomial roots for the poles, polynomial zero shifting). Each row is a normalised lowpass —
/// 1 rad/s, 1 Ω — which is what a lowpass at <c>Fc = 1/2π</c> Hz from a 1 Ω source is.
/// </summary>
public class FilterEllipticReferenceTests(ITestOutputHelper output)
{
    private const double Tolerance = 1e-8;

    private sealed record Row(string Family, int Order, double Ripple, double Astop, string Type,
                              string First, double? Load, (bool Inductor, double Value)[] Elements)
    {
        public override string ToString() =>
            $"{Family} n={Order} {Ripple}/{Astop} dB type {Type} {First}-first";
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static List<Row> Rows()
    {
        var path = Path.Combine(RepoRoot(), "testdata", "filter-elliptic", "elliptic-ladders.csv");
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split(',');
            double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            (bool, double)[] els = c[8].Length == 0 ? [] :
                [.. c[8].Split(';').Select(t => (t[0] == 'L', D(t[2..])))];
            rows.Add(new Row(c[0], int.Parse(c[1], CultureInfo.InvariantCulture), D(c[2]), D(c[3]),
                             c[4], c[5], c[6] == "none" ? null : D(c[6]), els));
        }
        return rows;
    }

    private static FilterLadderResult Build(Row r) => FilterLadder.Synthesize(
        r.Family == "elliptic" ? FilterResponse.Elliptic : FilterResponse.InvChebyshev,
        NetworkForm.Lowpass, r.Order, 1.0 / (2.0 * Math.PI), 0, 0,
        r.Family == "elliptic" ? r.Ripple : 0.1, 1.0,
        r.First == "parallel" ? FilterFirstElement.Parallel : FilterFirstElement.Series,
        r.Astop,
        r.Type switch { "b" => EllipticType.B, "c" => EllipticType.C, _ => EllipticType.A });

    /// <summary>
    /// Every row with a ladder is reproduced element for element to 1e-8 relative, load included;
    /// every row the reference could not realise is refused rather than answered.
    /// </summary>
    [Fact]
    public void EveryReferenceLadderIsReproduced()
    {
        var failures = new List<string>();
        double worst = 0;
        string worstAt = "";
        int built = 0, refused = 0;
        foreach (var r in Rows())
        {
            var got = Build(r);
            if (r.Load is null)
            {
                refused++;
                if (got.Ok && got.Realization!.First.ToString().Equals(r.First, StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{r}: the reference has no ladder, circuitRF returned one");
                continue;
            }
            built++;
            if (!got.Ok) { failures.Add($"{r}: refused — {got.Refusal!.Message}"); continue; }
            var real = got.Realization!;
            if (real.Elements.Count != r.Elements.Length)
            {
                failures.Add($"{r}: {real.Elements.Count} elements, reference {r.Elements.Length}");
                continue;
            }
            double err = Math.Abs(real.LoadOhms - r.Load.Value) / r.Load.Value;
            for (int i = 0; i < r.Elements.Length; i++)
            {
                if (real.Elements[i].Inductor != r.Elements[i].Inductor)
                {
                    err = double.PositiveInfinity;
                    break;
                }
                err = Math.Max(err, Math.Abs(real.Elements[i].Value - r.Elements[i].Value) / r.Elements[i].Value);
            }
            if (err > worst) { worst = err; worstAt = r.ToString(); }
            if (!(err <= Tolerance)) failures.Add($"{r}: relative error {err:G3}");
        }
        output.WriteLine($"{built} ladders, {refused} refusals; worst {worst:G3} at {worstAt}");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }
}

public class FilterEllipticHighOrderTests(ITestOutputHelper output)
{
    /// <summary>
    /// Past the reference table: a ladder that comes back IS the response — checked here by an
    /// independent ABCD chain of the placed elements against the ideal block — or the order is
    /// refused with a reason. Never a ladder that is approximately right.
    /// </summary>
    [Theory]
    [InlineData(EllipticType.A, 23)] [InlineData(EllipticType.A, 25)] [InlineData(EllipticType.A, 27)]
    [InlineData(EllipticType.A, 29)] [InlineData(EllipticType.A, 31)]
    [InlineData(EllipticType.C, 22)] [InlineData(EllipticType.C, 26)] [InlineData(EllipticType.C, 30)]
    [InlineData(EllipticType.B, 24)] [InlineData(EllipticType.B, 28)]
    public void AHighOrderLadderIsTheResponseOrIsRefused(EllipticType type, int n)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var built = FilterLadder.Synthesize(FilterResponse.Elliptic, NetworkForm.Lowpass, n,
            1.0 / (2.0 * Math.PI), 0, 0, 0.1, 1.0, FilterFirstElement.Parallel, 60, type);
        sw.Stop();
        if (!built.Ok)
        {
            output.WriteLine($"n={n} {type}: refused in {sw.Elapsed.TotalMilliseconds:F1} ms — {built.Refusal!.Message}");
            Assert.False(string.IsNullOrWhiteSpace(built.Refusal.Message));
            return;
        }
        var real = built.Realization!;
        var ideal = FilterNetwork.Create(FilterResponse.Elliptic, NetworkForm.Lowpass, n,
            1.0 / (2.0 * Math.PI), 0, 0, 0.1, 60, 0, type);
        double worst = 0;
        for (int i = 0; i <= 400; i++)
        {
            double w = 0.05 * Math.Pow(400.0, i / 400.0);
            double got = LadderS21(real, w).Magnitude;
            double want = ideal.At(w).S21.Magnitude;
            worst = Math.Max(worst, Math.Abs(got - want));
        }
        output.WriteLine($"n={n} {type}: {real.Elements.Count} elements, load {real.LoadOhms:G6}, " +
                         $"worst |ΔS21| {worst:G3}, {sw.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(worst < 1e-6, $"n={n} {type}: worst |ΔS21| {worst:G3}");
    }

    /// <summary>|S21| of the placed lowpass elements by nodal analysis, 1 Ω source.</summary>
    internal static System.Numerics.Complex LadderS21(FilterRealization real, double w)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal) { ["0"] = -1 };
        int Node(string n)
        {
            if (!names.TryGetValue(n, out int i)) { i = names.Count - 1; names[n] = i; }
            return i;
        }
        Node("p1");
        foreach (var e in real.Elements) { Node(e.NodeA); Node(e.NodeB); }
        string load = FilterLadder.LoadNode(real.Elements);
        int nn = names.Count - 1;
        var y = new System.Numerics.Complex[nn, nn];
        var s = new System.Numerics.Complex(0, w);
        void Stamp(int a, int b, System.Numerics.Complex g)
        {
            if (a >= 0) y[a, a] += g;
            if (b >= 0) y[b, b] += g;
            if (a >= 0 && b >= 0) { y[a, b] -= g; y[b, a] -= g; }
        }
        foreach (var e in real.Elements)
            Stamp(Node(e.NodeA), Node(e.NodeB), e.Inductor ? 1.0 / (s * e.Value) : s * e.Value);
        int p1 = Node("p1"), p2 = Node(load);
        Stamp(p1, -1, 1.0 / real.SourceOhms);
        Stamp(p2, -1, 1.0 / real.LoadOhms);
        var rhs = new System.Numerics.Complex[nn];
        rhs[p1] = 1.0 / real.SourceOhms;   // a 1 V source behind Rs, as a Norton current
        var v = Solve(y, rhs);
        return 2.0 * v[p2] * Math.Sqrt(real.SourceOhms / real.LoadOhms);
    }

    private static System.Numerics.Complex[] Solve(System.Numerics.Complex[,] a, System.Numerics.Complex[] b)
    {
        int n = b.Length;
        for (int k = 0; k < n; k++)
        {
            int p = k;
            for (int i = k + 1; i < n; i++) if (a[i, k].Magnitude > a[p, k].Magnitude) p = i;
            for (int j = 0; j < n; j++) (a[k, j], a[p, j]) = (a[p, j], a[k, j]);
            (b[k], b[p]) = (b[p], b[k]);
            for (int i = k + 1; i < n; i++)
            {
                var f = a[i, k] / a[k, k];
                for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                b[i] -= f * b[k];
            }
        }
        var x = new System.Numerics.Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            var acc = b[i];
            for (int j = i + 1; j < n; j++) acc -= a[i, j] * x[j];
            x[i] = acc / a[i, i];
        }
        return x;
    }
}
