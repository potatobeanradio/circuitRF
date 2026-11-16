using System.Globalization;
using System.Numerics;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The time-domain measurements (docs/design/transient.md §8, brief TRN-5 R-trn5-1): functions that read a waveform along a
/// cube's <c>time</c> axis — a transient result's — in <c>measure</c> lines, goals and traces.
///
/// <para><b>One operand rule.</b> The first argument is a real cube with a <c>time</c> axis. That axis is read and every other
/// axis is kept, so a transient inside a parametric sweep gives one value per sweep point. A function that reduces the axis
/// returns a number when nothing else is left; <c>fft</c> and <c>fft_power</c> replace it with a <c>harmonic</c> axis, put
/// last, which is where a harmonic-balance result has it.</para>
///
/// <para><b>The step.</b> <c>rise</c>, <c>fall</c>, <c>overshoot</c>, <c>undershoot</c> and <c>settle</c> read the step from the
/// record's first value to its last. A record that ends where it began (a pulse) has no step, and <c>rise</c>/<c>fall</c> then
/// take the swing between its minimum and maximum. A waveform with no edge of the kind asked for gives NaN — a missing value,
/// so one sweep point without an edge does not fail the others.</para>
///
/// <para><b>Two names differ from the note's first draft</b>, which collided: <c>at(x, t)</c> is <c>value_at(x, t)</c> (an
/// <c>at</c> pinning an axis by index already existed, and <c>value_at</c> already read a point by its value — it now
/// defaults to the <c>time</c> axis), and <c>freq(x)</c> is <c>frequency(x)</c> (<c>freq</c> is the frequency variable).</para>
/// </summary>
public sealed partial class Evaluator
{
    /// <summary>The axis a transient result's waveforms run along.</summary>
    public const string TimeAxisName = "time";

    /// <summary>Every time-domain function. Each reads a whole waveform, so a trace evaluates an expression calling one over
    /// whole cubes (<see cref="CallsAxisFunction"/>), and <c>check</c> refuses one written over a result with no time axis.</summary>
    public static IReadOnlySet<string> TimeDomainFunctions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "rise", "fall", "overshoot", "undershoot", "settle", "period", "frequency",
        "tmin", "tmax", "avg", "rms", "fft", "fft_power", "thd",
    };

    /// <summary>The most harmonics <c>fft</c> reports: the record's own limit, at most this many above DC.</summary>
    public const int FftMaxHarmonics = 16;

    private Value EvalTimeDomain(CallExpr cl, Scope scope)
    {
        string name = cl.Name;
        switch (name)
        {
            case "rise":
            case "fall":
            {
                Arity(cl, 1, 3);
                double lo = OptionalNumber(cl, scope, 1, 10), hi = OptionalNumber(cl, scope, 2, 90);
                // lo = 0 is refused: the swing starts AT 0 %, so the waveform never crosses it from below and every edge read NaN
                if (!(lo > 0 && hi <= 100 && lo < hi))
                    throw Refuse("expr.tran.levels", "{f}(): the levels are percentages of the swing with lo above 0 and below hi, as {f}(x, 10, 90).", ("f", name));
                bool rising = name == "rise";
                return Map(cl, scope, name, "s", (t, x) => Edge(t, x, rising, lo / 100, hi / 100));
            }
            case "overshoot":
                Arity(cl, 1, 1);
                return Map(cl, scope, name, "%", (_, x) => Overshoot(x));
            case "undershoot":
                Arity(cl, 1, 1);
                return Map(cl, scope, name, "%", (_, x) => Undershoot(x));
            case "settle":
            {
                Arity(cl, 1, 2);
                double tol = OptionalNumber(cl, scope, 1, 1);
                if (!(tol > 0)) throw Refuse("expr.tran.tol", "settle(): the tolerance is a positive percentage of the step, as settle(x, 2).");
                return Map(cl, scope, name, "s", (t, x) => Settle(t, x, tol / 100));
            }
            case "period":
                Arity(cl, 1, 1);
                return Map(cl, scope, name, "s", Period);
            case "frequency":
                Arity(cl, 1, 1);
                return Map(cl, scope, name, "Hz", (t, x) => 1 / Period(t, x));
            case "tmin":
            case "tmax":
            {
                Arity(cl, 1, 3);
                var w = Window(cl, scope, name);
                bool max = name == "tmax";
                return Map(cl, scope, name, "s", (t, x) => ExtremeTime(t, x, w, max, name));
            }
            case "avg":
            case "rms":
            {
                Arity(cl, 1, 3);
                var w = Window(cl, scope, name);
                bool rms = name == "rms";
                return Map(cl, scope, name, null, (t, x) => Mean(t, x, w, rms, name));
            }
            case "fft":
            {
                Arity(cl, 2, 4);
                var (f0, periods, hann) = FftArgs(cl, scope, name, 1);
                var (cube, dim) = Operand(cl, scope, name, 0);
                var t = cube.Axes[dim].Values;
                var grid = FftGrid(t, f0, periods, name);
                int k = Harmonics(grid.Resolved, periods);
                return Spectrum(cube, dim, k, x => Dft(grid, x, f0, k, hann), cube.Unit);
            }
            case "fft_power":
            {
                Arity(cl, 3, 5);
                var (f0, periods, hann) = FftArgs(cl, scope, name, 2);
                var (v, dim) = Operand(cl, scope, name, 0);
                var (i, dimI) = Operand(cl, scope, name, 1);
                if (!SameShape(v, i))
                    throw Refuse("expr.tran.fft-power-shape",
                        "fft_power(): the voltage and the current must be over the same axes; they are ({v}) and ({i}).",
                        ("v", AxesOf(v)), ("i", AxesOf(i)));
                var t = v.Axes[dim].Values;
                var grid = FftGrid(t, f0, periods, name);
                int k = Harmonics(grid.Resolved, periods);
                return SpectrumPower(v, i, dim, k, grid, f0, hann);
            }
            case "thd":
            {
                Arity(cl, 2, 3);
                double f0 = Number(cl, scope, 1, name, "the fundamental frequency, as thd(x, 1GHz)");
                if (!(f0 > 0)) throw Refuse("expr.tran.f0", "{f}(): the fundamental frequency must be positive, as {f}(x, 1GHz).", ("f", name));
                double nd = OptionalNumber(cl, scope, 2, 10);
                int n = (int)Math.Round(nd);
                if (n < 2 || Math.Abs(nd - n) > 1e-9)
                    throw Refuse("expr.tran.thd-n", "thd(): the highest harmonic counted is a whole number of at least 2, as thd(x, 1GHz, 10).");
                var (cube, dim) = Operand(cl, scope, name, 0);
                var grid = FftGrid(cube.Axes[dim].Values, f0, 1, name);
                int k = Harmonics(grid.Resolved, 1);
                if (n > k)
                    throw Refuse("expr.tran.thd-resolution",
                        "thd(): the last period holds {n} samples, which resolve harmonics up to {k}; ask for fewer, or run with a smaller Step.",
                        ("n", grid.Resolved.ToString(CultureInfo.InvariantCulture)), ("k", k.ToString(CultureInfo.InvariantCulture)));
                return Map(cl, scope, name, "%", (_, x) =>
                {
                    var s = Dft(grid, x, f0, n, false);
                    double sum = 0;
                    for (int h = 2; h <= n; h++) sum += s[h].Magnitude * s[h].Magnitude;
                    return 100 * Math.Sqrt(sum) / s[1].Magnitude;
                });
            }
        }
        throw new ArityException(name, 1, cl.Args.Length);
    }

    // ── operands ──────────────────────────────────────────────────────────────

    private static ExpressionException Refuse(string id, string template, params (string Name, object? Value)[] args)
        => new(Diagnostic.Create(id, DiagnosticSeverity.Error, template, args));

    private static void TdArity(CallExpr cl, int min, int max)
    {
        if (cl.Args.Length < min || cl.Args.Length > max) throw new ArityException(cl.Name, min, cl.Args.Length);
    }

    private static string AxesOf(DataCube c) => string.Join(", ", c.Axes.Select(a => a.Name));

    private static bool SameShape(DataCube a, DataCube b)
        => a.Rank == b.Rank && a.Axes.Zip(b.Axes).All(p => p.First.Name == p.Second.Name && p.First.Length == p.Second.Length);

    /// <summary>Argument <paramref name="index"/> as a real cube with a time axis, and that axis's position.</summary>
    private (DataCube Cube, int TimeDim) Operand(CallExpr cl, Scope scope, string name, int index)
    {
        var v = EvalExpr(cl.Args[index], scope);
        if (v.Kind != ValueKind.Cube)
            throw Refuse("expr.tran.scalar",
                "{f}(): this value is a single number; {f} reads a waveform over time, as a transient result's TR1.V(\"out\") is.", ("f", name));
        var cube = v.AsCube();
        int dim = -1;
        for (int d = 0; d < cube.Rank; d++) if (cube.Axes[d].Name == TimeAxisName) { dim = d; break; }
        if (dim < 0)
            throw Refuse("expr.tran.no-time",
                "{f}() reads a waveform over time and this value has no 'time' axis (its axes are: {axes}); a time-domain function reads a transient result.",
                ("f", name), ("axes", AxesOf(cube)));
        if (cube.DataKind != DataKind.Real)
            throw Refuse("expr.tran.complex", "{f}(): a waveform is real, and this value is complex.", ("f", name));
        if (cube.Axes[dim].Length < 2)
            throw Refuse("expr.tran.short", "{f}(): the waveform has fewer than two time points.", ("f", name));
        return (cube, dim);
    }

    private double Number(CallExpr cl, Scope scope, int index, string name, string what)
    {
        var v = EvalExpr(cl.Args[index], scope);
        if (v.Kind != ValueKind.Real)
            throw Refuse("expr.tran.number", "{f}(): argument {n} is {what}.", ("f", name),
                         ("n", (index + 1).ToString(CultureInfo.InvariantCulture)), ("what", what));
        return v.AsReal();
    }

    private double OptionalNumber(CallExpr cl, Scope scope, int index, double def)
        => cl.Args.Length > index ? Number(cl, scope, index, cl.Name, "a number") : def;

    /// <summary><c>f(x, t1, t2)</c>'s window; the whole record when neither is given.</summary>
    private (double T1, double T2)? Window(CallExpr cl, Scope scope, string name)
    {
        if (cl.Args.Length == 1) return null;
        if (cl.Args.Length != 3)
            throw Refuse("expr.tran.window", "{f}(): give both ends of the window, as {f}(x, 1u, 2u), or neither.", ("f", name));
        double t1 = Number(cl, scope, 1, name, "the window's start time"), t2 = Number(cl, scope, 2, name, "the window's end time");
        if (!(t2 > t1)) throw Refuse("expr.tran.window-order", "{f}(): the window's end must come after its start.", ("f", name));
        return (t1, t2);
    }

    private (double F0, int Periods, bool Hann) FftArgs(CallExpr cl, Scope scope, string name, int at)
    {
        double f0 = Number(cl, scope, at, name, "the fundamental frequency, as " + name + "(x, 1GHz)");
        if (!(f0 > 0)) throw Refuse("expr.tran.f0", "{f}(): the fundamental frequency must be positive, as {f}(x, 1GHz).", ("f", name));
        double pd = OptionalNumber(cl, scope, at + 1, 1);
        int periods = (int)Math.Round(pd);
        if (periods < 1 || Math.Abs(pd - periods) > 1e-9)
            throw Refuse("expr.tran.periods", "{f}(): periods is a whole number of at least 1.", ("f", name));
        bool hann = false;
        if (cl.Args.Length > at + 2)
        {
            var w = EvalExpr(cl.Args[at + 2], scope);
            string word = w.Kind == ValueKind.String ? w.AsString() : "";
            hann = word.Trim().ToLowerInvariant() switch
            {
                "none" => false,
                "hann" => true,
                _ => throw Refuse("expr.tran.window-kind", "{f}(): the window is \"none\" or \"hann\".", ("f", name)),
            };
        }
        return (f0, periods, hann);
    }

    // ── iteration over every waveform of a cube ─────────────────────────────────

    /// <summary>Applies <paramref name="f"/> to each waveform of argument 0 along its time axis; the axis is reduced away.
    /// <paramref name="unit"/> null keeps the operand's unit.</summary>
    private Value Map(CallExpr cl, Scope scope, string name, string? unit, Func<double[], double[], double> f)
    {
        var (cube, dim) = Operand(cl, scope, name, 0);
        var t = cube.Axes[dim].Values;
        var data = cube.RealValues;
        int nt = t.Length, inner = 1, outer = 1;
        for (int d = dim + 1; d < cube.Rank; d++) inner *= cube.Axes[d].Length;
        for (int d = 0; d < dim; d++) outer *= cube.Axes[d].Length;
        var result = new double[outer * inner];
        var x = new double[nt];
        for (int o = 0; o < outer; o++)
            for (int i = 0; i < inner; i++)
            {
                for (int s = 0; s < nt; s++) x[s] = data[(o * nt + s) * inner + i];
                result[o * inner + i] = f(t, x);
            }
        if (cube.Rank == 1) return new Value(result[0]);
        var axes = cube.Axes.Where((_, d) => d != dim).ToArray();
        return new Value(new DataCube(axes, result) { Unit = unit ?? cube.Unit });
    }

    /// <summary>The spectrum of each waveform: the time axis removed, a <c>harmonic</c> axis 0…k appended last.</summary>
    private static Value Spectrum(DataCube cube, int dim, int k, Func<double[], Complex[]> f, string unit)
    {
        var data = cube.RealValues;
        int nt = cube.Axes[dim].Length, inner = 1, outer = 1, nk = k + 1;
        for (int d = dim + 1; d < cube.Rank; d++) inner *= cube.Axes[d].Length;
        for (int d = 0; d < dim; d++) outer *= cube.Axes[d].Length;
        var result = new Complex[outer * inner * nk];
        var x = new double[nt];
        for (int o = 0; o < outer; o++)
            for (int i = 0; i < inner; i++)
            {
                for (int s = 0; s < nt; s++) x[s] = data[(o * nt + s) * inner + i];
                var spec = f(x);
                Array.Copy(spec, 0, result, (o * inner + i) * nk, nk);
            }
        var axes = cube.Axes.Where((_, d) => d != dim).Append(HarmonicAxis(k)).ToArray();
        return new Value(new DataCube(axes, result) { Unit = unit });
    }

    private static Value SpectrumPower(DataCube v, DataCube i, int dim, int k, FftSamples grid, double f0, bool hann)
    {
        var dv = v.RealValues; var di = i.RealValues;
        int nt = v.Axes[dim].Length, inner = 1, outer = 1, nk = k + 1;
        for (int d = dim + 1; d < v.Rank; d++) inner *= v.Axes[d].Length;
        for (int d = 0; d < dim; d++) outer *= v.Axes[d].Length;
        var result = new double[outer * inner * nk];
        var xv = new double[nt]; var xi = new double[nt];
        for (int o = 0; o < outer; o++)
            for (int c = 0; c < inner; c++)
            {
                for (int s = 0; s < nt; s++) { xv[s] = dv[(o * nt + s) * inner + c]; xi[s] = di[(o * nt + s) * inner + c]; }
                var sv = Dft(grid, xv, f0, k, hann); var si = Dft(grid, xi, f0, k, hann);
                int at = (o * inner + c) * nk;
                result[at] = sv[0].Real * si[0].Real;
                for (int h = 1; h <= k; h++) result[at + h] = 0.5 * (sv[h] * Complex.Conjugate(si[h])).Real;
            }
        var axes = v.Axes.Where((_, d) => d != dim).Append(HarmonicAxis(k)).ToArray();
        return new Value(new DataCube(axes, result) { Unit = "W" });
    }

    /// <summary>A harmonic-balance result's spectral axis: integer orders 0…k, no unit.</summary>
    private static Axis HarmonicAxis(int k)
    {
        var h = new double[k + 1];
        for (int j = 0; j <= k; j++) h[j] = j;
        return new Axis("harmonic", h, "");
    }

    // ── the step measurements ───────────────────────────────────────────────────

    /// <summary>
    /// The first rise (fall) between the fractions <paramref name="lo"/> and <paramref name="hi"/> of the swing: the time the
    /// waveform first reaches <paramref name="hi"/>, less the last time before it that it crossed <paramref name="lo"/>, both
    /// interpolated. The swing is the step from the first value to the last; a record that ends near where it began takes
    /// its minimum and maximum instead.
    /// </summary>
    private static double Edge(double[] t, double[] x, bool rising, double lo, double hi)
    {
        double x0 = x[0], xf = x[^1], mn = x.Min(), mx = x.Max(), swing = xf - x0;
        double from, to;
        if (rising) (from, to) = swing > 0.1 * (mx - mn) ? (x0, xf) : (mn, mx);
        else        (from, to) = -swing > 0.1 * (mx - mn) ? (x0, xf) : (mx, mn);
        if (!(Math.Abs(to - from) > 0)) return double.NaN;
        // y runs from 0 (the start of the edge) to 1 (its end), upwards in both directions
        double Y(int s) => (x[s] - from) / (to - from);
        int hiAt = -1;
        for (int s = 1; s < x.Length; s++) if (Y(s - 1) < hi && Y(s) >= hi) { hiAt = s; break; }
        if (hiAt < 0) return double.NaN;
        double tHi = Cross(t, hiAt, Y(hiAt - 1), Y(hiAt), hi);
        for (int s = hiAt; s >= 1; s--)
            if (Y(s - 1) < lo && Y(s) >= lo) return tHi - Cross(t, s, Y(s - 1), Y(s), lo);
        return double.NaN;
    }

    /// <summary>Where the segment ending at sample <paramref name="s"/> reaches <paramref name="level"/>, by linear interpolation.</summary>
    private static double Cross(double[] t, int s, double y0, double y1, double level)
        => y1 == y0 ? t[s] : t[s - 1] + (t[s] - t[s - 1]) * (level - y0) / (y1 - y0);

    /// <summary>The waveform normalised to its step: 0 at the first value, 1 at the last. Null with no step — a record that
    /// ends near where it began (within a tenth of its swing, <see cref="Edge"/>'s rule), whose "percent of the step" would
    /// otherwise be a finite, enormous and meaningless number.</summary>
    private static double[]? Normalised(double[] x)
    {
        double step = x[^1] - x[0];
        if (!HasStep(x)) return null;
        return [.. x.Select(v => (v - x[0]) / step)];
    }

    /// <summary>Whether the record steps: it ends further from its first value than a tenth of its whole swing.</summary>
    private static bool HasStep(double[] x)
    {
        double step = Math.Abs(x[^1] - x[0]);
        return step > 0 && step > 0.1 * (x.Max() - x.Min());
    }

    /// <summary>How far the waveform goes past its final value, in percent of the step; 0 when it never does.</summary>
    private static double Overshoot(double[] x)
        => Normalised(x) is { } y ? 100 * Math.Max(0, y.Max() - 1) : double.NaN;

    /// <summary>How far the waveform comes back below its final value after the overshoot's peak, in percent of the step: the
    /// first trough a ringing step leaves. 0 when it does not.</summary>
    private static double Undershoot(double[] x)
    {
        if (Normalised(x) is not { } y) return double.NaN;
        int peak = Array.IndexOf(y, y.Max());
        if (y[peak] <= 1) return 0;
        // the first trough: down from the peak until the waveform turns up again, not the deepest dip in the rest of the record
        // (a later load step or second edge is not this step's undershoot)
        double trough = y[peak];
        for (int s = peak + 1; s < y.Length && y[s] <= y[s - 1]; s++) trough = y[s];
        return 100 * Math.Max(0, 1 - trough);
    }

    /// <summary>The time after which the waveform stays within <paramref name="tol"/> of the step of its final value,
    /// interpolated; the first time when it always is. With no step (<see cref="HasStep"/>: a pulse, a rail that starts where
    /// it ends) the band is a fraction of the larger of the final value and the swing — of the step alone it would be a
    /// fraction of a microvolt on a record ending a microvolt from its start, and the "settling" the record's end.</summary>
    private static double Settle(double[] t, double[] x, double tol)
    {
        double xf = x[^1], scale = Math.Abs(xf - x[0]);
        if (!HasStep(x)) scale = Math.Max(Math.Abs(xf), x.Max() - x.Min());
        if (!(scale > 0)) return t[0];
        double band = tol * scale;
        int last = -1;
        for (int s = x.Length - 1; s >= 0; s--) if (Math.Abs(x[s] - xf) > band) { last = s; break; }
        if (last < 0) return t[0];
        double d0 = x[last] - xf, d1 = x[last + 1] - xf, target = Math.Sign(d0) * band;
        return d0 == d1 ? t[last + 1] : t[last] + (t[last + 1] - t[last]) * (d0 - target) / (d0 - d1);
    }

    /// <summary>The period from the rising crossings of the mean over the record's last half, interpolated and averaged;
    /// NaN with fewer than two crossings there.</summary>
    private static double Period(double[] t, double[] x)
    {
        double mid = 0.5 * (t[0] + t[^1]);
        int from = Array.FindIndex(t, v => v >= mid);
        if (from < 0 || x.Length - from < 3) return double.NaN;
        double mean = Integrate(t, x, from, x.Length - 1, square: false) / (t[^1] - t[from]);
        double first = double.NaN, last = double.NaN;
        int n = 0;
        for (int s = from + 1; s < x.Length; s++)
            if (x[s - 1] < mean && x[s] >= mean)
            {
                double c = Cross(t, s, x[s - 1], x[s], mean);
                if (n == 0) first = c;
                last = c; n++;
            }
        return n < 2 ? double.NaN : (last - first) / (n - 1);
    }

    // ── values and statistics over a window ───────────────────────────────────

    private static double ExtremeTime(double[] t, double[] x, (double T1, double T2)? w, bool max, string name)
    {
        if (w is { } r) CheckWindow(t, r.T1, r.T2, name);
        double best = double.NaN, at = double.NaN;
        for (int s = 0; s < x.Length; s++)
        {
            if (w is { } win && (t[s] < win.T1 || t[s] > win.T2)) continue;
            if (double.IsNaN(best) || (max ? x[s] > best : x[s] < best)) { best = x[s]; at = t[s]; }
        }
        return at;
    }

    /// <summary>The time-weighted mean (rms) over the window, by the trapezoidal rule with the window's ends interpolated.</summary>
    private static double Mean(double[] t, double[] x, (double T1, double T2)? w, bool rms, string name)
    {
        double t1 = w?.T1 ?? t[0], t2 = w?.T2 ?? t[^1];
        double tol = 1e-9 * (t[^1] - t[0]);
        CheckWindow(t, t1, t2, name);
        t1 = Math.Max(t1, t[0]); t2 = Math.Min(t2, t[^1]);
        // the samples inside, with the two ends interpolated onto the window's edges
        var ts = new List<double> { t1 };
        var xs = new List<double> { Interp(t, x, t1) };
        for (int s = 0; s < t.Length; s++)
            if (t[s] > t1 + tol && t[s] < t2 - tol) { ts.Add(t[s]); xs.Add(x[s]); }
        ts.Add(t2); xs.Add(Interp(t, x, t2));
        var ta = ts.ToArray(); var xa = xs.ToArray();
        double integral = Integrate(ta, xa, 0, ta.Length - 1, rms);
        return rms ? Math.Sqrt(integral / (t2 - t1)) : integral / (t2 - t1);
    }

    /// <summary>Refuses a window running past the record — read silently, tmin/tmax would return NaN for it.</summary>
    private static void CheckWindow(double[] t, double t1, double t2, string name)
    {
        double tol = 1e-9 * (t[^1] - t[0]);
        if (t1 < t[0] - tol || t2 > t[^1] + tol)
            throw Refuse("expr.tran.window-outside", "{f}(): the window {t1} s to {t2} s runs past the record, which is {r1} s to {r2} s.",
                ("f", name), ("t1", G(t1)), ("t2", G(t2)), ("r1", G(t[0])), ("r2", G(t[^1])));
    }

    private static double Integrate(double[] t, double[] x, int from, int to, bool square)
    {
        double sum = 0;
        for (int s = from + 1; s <= to; s++)
        {
            double a = square ? x[s - 1] * x[s - 1] : x[s - 1], b = square ? x[s] * x[s] : x[s];
            sum += 0.5 * (a + b) * (t[s] - t[s - 1]);
        }
        return sum;
    }

    private static double Interp(double[] t, double[] x, double at)
    {
        int s = Array.BinarySearch(t, at);
        if (s >= 0) return x[s];
        s = ~s;
        if (s <= 0) return x[0];
        if (s >= t.Length) return x[^1];
        return x[s - 1] + (x[s] - x[s - 1]) * (at - t[s - 1]) / (t[s] - t[s - 1]);
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    // ── the spectrum ────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the spectrum's samples are: the last <c>periods</c> whole periods of the record, ending at its last time. On a
    /// uniform grid whose step divides the window, the record's own samples (the window's end point left out, since it
    /// starts the next period); otherwise the record interpolated (cubic, through the four nearest samples) onto as many
    /// equally spaced points as the window held, at least 256.
    /// </summary>
    /// <remarks><c>Resolved</c> is how many of the RECORD's own samples the window holds — what the harmonics a spectrum may
    /// claim rest on. Interpolating onto 256 points adds no resolution: a 2.4 GHz tone sampled every 1 ns would otherwise
    /// report 16 "harmonics" from a cubic through samples periods apart.</remarks>
    private sealed record FftSamples(int N, double TStart, double Dt, int First, double[]? Times, double[] Record, int Resolved);

    private static FftSamples FftGrid(double[] t, double f0, int periods, string name)
    {
        double span = periods / f0, tEnd = t[^1], tStart = tEnd - span, record = tEnd - t[0];
        if (tStart < t[0] - 1e-9 * span)
            throw Refuse("expr.tran.fft-short",
                "{f}(): the record is {r} s long, shorter than the {p} period(s) of {f0} Hz asked for.",
                ("f", name), ("r", G(record)), ("p", periods.ToString(CultureInfo.InvariantCulture)), ("f0", G(f0)));
        int n = t.Length;
        double dt = (t[^1] - t[0]) / (n - 1);
        bool uniform = true;
        for (int s = 1; s < n && uniform; s++) uniform = Math.Abs(t[s] - t[s - 1] - dt) <= 1e-6 * dt;
        double steps = span / dt;
        int ns = (int)Math.Round(steps);
        if (uniform && ns >= 4 && Math.Abs(steps - ns) <= 1e-6 * steps && n - 1 - ns >= 0)
            return new FftSamples(ns, tStart, dt, n - 1 - ns, null, t, ns);
        int inWindow = t.Count(v => v >= tStart);
        // the intervals the window spans, as the uniform path's ns counts them (its end point starts the next period)
        int resolved = Math.Max(0, inWindow - 1);
        if (resolved < 4)
            throw Refuse("expr.tran.fft-resolution",
                "{f}(): the last {p} period(s) of {f0} Hz hold {n} of the record's samples, too few for a spectrum; run with a smaller Step.",
                ("f", name), ("p", periods.ToString(CultureInfo.InvariantCulture)), ("f0", G(f0)),
                ("n", inWindow.ToString(CultureInfo.InvariantCulture)));
        int m = Math.Max(256, inWindow);
        var times = new double[m];
        for (int s = 0; s < m; s++) times[s] = tStart + span * s / m;
        return new FftSamples(m, tStart, span / m, -1, times, t, resolved);
    }

    /// <summary>The highest harmonic a window of <paramref name="n"/> samples over <paramref name="periods"/> periods resolves
    /// below its Nyquist bin, at most <see cref="FftMaxHarmonics"/>.</summary>
    private static int Harmonics(int n, int periods) => Math.Max(1, Math.Min(FftMaxHarmonics, (n / 2 - 1) / periods));

    /// <summary>
    /// The full-amplitude spectrum harmonic balance reports: x(t) = X₀ + Σ Re{Xₖ e^{jk2πf₀t}}, t the record's own time, so a
    /// phase reads as harmonic balance's does. X₀ = Σwx/Σw, Xₖ = 2Σwx e^{−jk2πf₀t}/Σw.
    /// </summary>
    private static Complex[] Dft(FftSamples g, double[] x, double f0, int k, bool hann)
    {
        int n = g.N;
        var xs = new double[n];
        var ts = new double[n];
        for (int s = 0; s < n; s++)
        {
            if (g.Times is null) { xs[s] = x[g.First + s]; ts[s] = g.TStart + s * g.Dt; }
            else { ts[s] = g.Times[s]; xs[s] = Cubic(g.Record, x, ts[s]); }
        }
        var w = new double[n];
        double sw = 0;
        for (int s = 0; s < n; s++) { w[s] = hann ? 0.5 * (1 - Math.Cos(2 * Math.PI * s / n)) : 1; sw += w[s]; }
        var spec = new Complex[k + 1];
        for (int h = 0; h <= k; h++)
        {
            double re = 0, im = 0, om = 2 * Math.PI * h * f0;
            for (int s = 0; s < n; s++)
            {
                double ph = om * ts[s];
                re += w[s] * xs[s] * Math.Cos(ph);
                im -= w[s] * xs[s] * Math.Sin(ph);
            }
            spec[h] = new Complex(re, im) * ((h == 0 ? 1.0 : 2.0) / sw);
        }
        return spec;
    }

    /// <summary>x at <paramref name="at"/> by the cubic through the four nearest samples.</summary>
    private static double Cubic(double[] t, double[] x, double at)
    {
        int s = Array.BinarySearch(t, at);
        if (s >= 0) return x[s];
        s = ~s;
        int a = Math.Clamp(s - 2, 0, Math.Max(0, t.Length - 4)), b = Math.Min(t.Length, a + 4);
        double sum = 0;
        for (int i = a; i < b; i++)
        {
            double l = 1;
            for (int j = a; j < b; j++) if (j != i) l *= (at - t[j]) / (t[i] - t[j]);
            sum += l * x[i];
        }
        return sum;
    }
}
