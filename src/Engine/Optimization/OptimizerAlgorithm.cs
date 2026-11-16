using CircuitRF.Core.Design;

namespace CircuitRF.Engine.Optimization;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The optimizer's numeric half (brief-tuneopt-6 §2, overview D12): algorithms over the unit box
//  [0, 1]ⁿ that never call a simulator. Each is an ASK/TELL state machine — it hands out a batch of
//  points and is told what they cost — so pausing is "stop asking", parallel evaluation is "evaluate
//  the batch concurrently", and a run is repeatable because the seed is part of the state.
//
//  No domain types: a point is a double[], an evaluation a cost, a residual vector and a flag. What
//  a coordinate MEANS (a capacitance on a log scale, the magnitude of a complex load) is
//  src/Design/Optimization's business.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What one point cost. <paramref name="Residuals"/> is the goal-residual vector a least-squares
/// method works on (null when the evaluation produced none). <paramref name="Failed"/> marks a point
/// whose cost is a penalty, not a measurement — a simulation that did not converge, or a point no
/// design satisfies; its <paramref name="Cost"/> is still finite and ranks below every real one, so a
/// method that only compares costs needs nothing else, and a method that models the cost (a gradient,
/// a Jacobian) treats it as a step to shrink.
/// </summary>
public sealed record Evaluation(double Cost, double[]? Residuals = null, bool Failed = false);

/// <summary>A resumable optimizer over the unit box (brief-tuneopt-6 §2).</summary>
public interface IOptimizerAlgorithm
{
    /// <summary>The stable id, one of <see cref="OptimizerAlgorithms.Ids"/>.</summary>
    string Id { get; }

    /// <summary>The number of coordinates.</summary>
    int Dimension { get; }

    /// <summary>The batch of points to evaluate next — one for a simplex step, n for a
    /// finite-difference Jacobian, the batch size for a random search. Asking again before telling
    /// returns the same batch. Empty once <see cref="IsFinished"/>.</summary>
    IReadOnlyList<double[]> Ask();

    /// <summary>The batch's results, one per point, in the order asked.</summary>
    void Tell(IReadOnlyList<Evaluation> results);

    bool IsFinished { get; }

    /// <summary>Why the algorithm finished on its own; null while it runs.</summary>
    string? FinishReason { get; }

    /// <summary>Iterations completed — each algorithm says what one is (a simplex step, an accepted
    /// or abandoned Levenberg–Marquardt step, a line search, a random batch).</summary>
    int Iterations { get; }

    /// <summary>Everything needed to continue later: the batches told so far.</summary>
    OptimizerState Capture();

    /// <summary>Continues from <paramref name="state"/>, which <see cref="Capture"/> of an algorithm
    /// built with the same arguments produced.</summary>
    void Restore(OptimizerState state);
}

/// <summary>
/// An algorithm's whole state, held as the batches it asked for and what each cost. Every algorithm
/// here is deterministic given its arguments and its seed, so replaying these batches rebuilds the
/// state exactly — and the record is plain numbers, which a checkpoint file can hold as it is.
/// </summary>
public sealed class OptimizerState(string algorithm, IReadOnlyList<(double[][] Points, Evaluation[] Results)> batches)
{
    public string Algorithm { get; } = algorithm;
    public IReadOnlyList<(double[][] Points, Evaluation[] Results)> Batches { get; } = batches;
}

/// <summary>
/// The shared machinery: an algorithm is written as one iterator, <see cref="Run"/>, that yields each
/// batch and reads <see cref="Results"/> when resumed. <b>Every piece of mutable state lives in that
/// iterator's locals</b> — so a fresh enumerator is a fresh algorithm, and <see cref="Restore"/> is a
/// replay of the told batches into it, checked point for point.
/// </summary>
public abstract class AskTellAlgorithm : IOptimizerAlgorithm
{
    private IEnumerator<double[][]>? _run;
    private double[][]? _pending;
    private readonly List<(double[][] Points, Evaluation[] Results)> _log = [];

    protected AskTellAlgorithm(string id, double[] start)
    {
        Id        = id;
        Dimension = start.Length;
        Start     = [.. start.Select(Clamp)];
    }

    public string Id { get; }
    public int Dimension { get; }
    public bool IsFinished { get; private set; }
    public string? FinishReason { get; private set; }
    public int Iterations { get; private set; }

    /// <summary>The start point, inside the box.</summary>
    protected double[] Start { get; }

    /// <summary>What the batch just yielded cost — read by <see cref="Run"/> after each yield.</summary>
    protected Evaluation[] Results { get; private set; } = [];

    /// <summary>The algorithm. Yields each batch; finishes by calling <see cref="Finish"/> and
    /// breaking. Never touches a field it writes — locals only (see the class remark).</summary>
    protected abstract IEnumerable<double[][]> Run();

    protected void CompleteIteration() => Iterations++;

    protected void Finish(string reason) => FinishReason ??= reason;

    public IReadOnlyList<double[]> Ask()
    {
        if (IsFinished) return [];
        if (_pending is null) Advance();
        return _pending is null ? [] : [.. _pending.Select(p => (double[])p.Clone())];
    }

    public void Tell(IReadOnlyList<Evaluation> results)
    {
        if (_pending is null)
            throw new InvalidOperationException("Tell was called with no batch asked for.");
        if (results.Count != _pending.Length)
            throw new ArgumentException($"{results.Count} results for a batch of {_pending.Length} points.");
        Results = [.. results];
        _log.Add((_pending, Results));
        _pending = null;
        Advance();
    }

    public OptimizerState Capture() => new(Id, [.. _log]);

    public void Restore(OptimizerState state)
    {
        if (state.Algorithm != Id)
            throw new ArgumentException($"A '{state.Algorithm}' state cannot continue a '{Id}' run.");
        _run?.Dispose();
        _run = null; _pending = null; _log.Clear();
        IsFinished = false; FinishReason = null; Iterations = 0; Results = [];

        foreach (var (points, results) in state.Batches)
        {
            if (_pending is null) Advance();
            if (_pending is null || !SameBatch(_pending, points))
                throw new ArgumentException("The state was captured from a run with different arguments.");
            Tell(results);
        }
    }

    private void Advance()
    {
        _run ??= Run().GetEnumerator();
        while (true)
        {
            if (!_run.MoveNext())
            {
                IsFinished = true;
                FinishReason ??= "the algorithm finished";
                _pending = null;
                return;
            }
            if (_run.Current.Length == 0) continue;
            _pending = [.. _run.Current.Select(p => p.Select(Clamp).ToArray())];
            return;
        }
    }

    private static bool SameBatch(double[][] a, double[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        return true;
    }

    /// <summary>Into the box. A NaN coordinate is a defect in the algorithm, not a point.</summary>
    protected static double Clamp(double v)
        => double.IsNaN(v) ? throw new InvalidOperationException("An optimizer produced a NaN coordinate.")
                           : Math.Clamp(v, 0.0, 1.0);

    /// <summary>The projection onto the box.</summary>
    protected static double[] Project(double[] x) => [.. x.Select(v => Math.Clamp(v, 0.0, 1.0))];

    protected static double InfNorm(double[] a, double[] b)
    {
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    /// <summary>
    /// The points of a forward-difference gradient or Jacobian, as one batch: x + h·eᵢ, or x − h·eᵢ
    /// where the forward point would leave the box. <paramref name="sign"/> gives each step's
    /// direction (+1 forward, −1 backward).
    /// </summary>
    protected static double[][] DifferencePoints(double[] x, double h, IReadOnlyList<int> coords, out double[] sign)
    {
        var pts = new double[coords.Count][];
        sign = new double[coords.Count];
        for (int k = 0; k < coords.Count; k++)
        {
            int i = coords[k];
            sign[k] = x[i] + h <= 1.0 ? 1.0 : -1.0;
            pts[k] = (double[])x.Clone();
            pts[k][i] = x[i] + sign[k] * h;
        }
        return pts;
    }

    /// <summary>
    /// A number option: the value given, else the registry's default for it (R-to7-7), else
    /// <paramref name="auto"/> — the value an <c>auto</c> default works out to for this many
    /// variables. Reading an option the registry does not list is a defect, not a fallback: every
    /// option an algorithm reads is one the reference documents.
    /// </summary>
    protected double Option(IReadOnlyDictionary<string, string>? options, string name, double? auto = null)
    {
        var info = OptimizerAlgorithms.Find(Id)?.Option(name)
                   ?? throw new InvalidOperationException($"'{Id}' reads option '{name}', which the algorithm registry does not list.");
        if (options is not null && options.TryGetValue(name, out var text)
            && double.TryParse(text, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v))
            return v;
        return info.NumericDefault ?? auto
               ?? throw new InvalidOperationException($"'{Id}' option '{name}' defaults to '{info.Default}' and was given no value for it.");
    }

    /// <summary>A word option: the value given, else the registry's default.</summary>
    protected string WordOption(IReadOnlyDictionary<string, string>? options, string name)
    {
        var info = OptimizerAlgorithms.Find(Id)?.Option(name)
                   ?? throw new InvalidOperationException($"'{Id}' reads option '{name}', which the algorithm registry does not list.");
        return options is not null && options.TryGetValue(name, out var text) && text.Length > 0 ? text : info.Default;
    }

    /// <summary>
    /// Whether <paramref name="a"/> ranks ahead of <paramref name="b"/> (R-to7-8): a point that was
    /// evaluated always ranks ahead of one that failed or was infeasible, whatever the two penalty
    /// costs say — a penalty is finite and only orders failures among themselves.
    /// </summary>
    protected static bool Better(Evaluation a, Evaluation b)
        => a.Failed != b.Failed ? !a.Failed : a.Cost < b.Cost;

    /// <summary>Indices of <paramref name="e"/>, best first by <see cref="Better"/>; stable, so a tie
    /// keeps the earlier index ahead.</summary>
    protected static int[] Ranking(IReadOnlyList<Evaluation> e)
        => [.. Enumerable.Range(0, e.Count).OrderBy(i => e[i].Failed ? 1 : 0).ThenBy(i => e[i].Cost)];

    /// <summary>The largest residual — the minimax cost of a point.</summary>
    protected static double Worst(double[] r) => r.Length == 0 ? 0 : r.Max();
}

/// <summary>
/// SplitMix64 — a small generator whose output is fixed by its seed on every platform and runtime, which
/// <see cref="System.Random"/> does not promise. A run's repeatability rests on it.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong _s = seed;

    /// <summary>The increment the state advances by — the 64-bit golden ratio.</summary>
    public const ulong Golden = 0x9E3779B97F4A7C15UL;

    public ulong NextULong() => Mix(_s += Golden);

    /// <summary>The output mix: a bijection of 64-bit words that scatters every input bit over every
    /// output bit. Statistical streams (docs/design/yield.md) hash with it, so a draw is a pure
    /// function of its inputs on every platform.</summary>
    public static ulong Mix(ulong z) => RfCore.Statistics.StatDraws.Mix(z);

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, n).</summary>
    public int Next(int n) => (int)(NextDouble() * n);

    /// <summary>Standard normal, by Box–Muller (one value per call, so the stream stays a plain
    /// function of the seed and the call count).</summary>
    public double NextGaussian()
    {
        double u1 = 1.0 - NextDouble(), u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Cauchy with location <paramref name="loc"/> and scale <paramref name="scale"/>.</summary>
    public double NextCauchy(double loc, double scale) => loc + scale * Math.Tan(Math.PI * (NextDouble() - 0.5));
}

/// <summary>Small dense linear algebra for the methods here (n is the number of optimized values).</summary>
internal static class DenseLinear
{
    /// <summary>Solves A·x = b by Gaussian elimination with partial pivoting; null when A is singular.</summary>
    public static double[]? Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        double scale = 0;
        foreach (double v in a) scale = Math.Max(scale, Math.Abs(v));
        if (scale == 0) return null;

        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++)
                if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
            if (Math.Abs(m[p, c]) <= 1e-14 * scale) return null;
            if (p != c)
            {
                for (int k = 0; k < n; k++) (m[c, k], m[p, k]) = (m[p, k], m[c, k]);
                (x[c], x[p]) = (x[p], x[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                if (f == 0) continue;
                for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                x[r] -= f * x[c];
            }
        }
        for (int r = n - 1; r >= 0; r--)
        {
            double s = x[r];
            for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
            x[r] = s / m[r, r];
        }
        return x;
    }

    public static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>
    /// Eigen-decomposition of a symmetric matrix by cyclic Jacobi rotations: <paramref name="a"/> =
    /// V·diag(values)·Vᵀ, eigenvectors in V's columns. Exact enough and plenty fast for the n of an
    /// optimization; the input is not modified.
    /// </summary>
    public static (double[] Values, double[,] Vectors) SymmetricEigen(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++) v[i, i] = 1;

        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, diag = 0;
            for (int p = 0; p < n; p++)
            {
                diag += m[p, p] * m[p, p];
                for (int q = p + 1; q < n; q++) off += m[p, q] * m[p, q];
            }
            if (off <= 1e-30 * Math.Max(diag, 1e-300)) break;

            for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (m[p, q] == 0) continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq;
                        m[k, q] = s * mkp + c * mkq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk;
                        m[q, k] = s * mpk + c * mqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = m[i, i];
        return (values, v);
    }
}
