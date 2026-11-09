using System.Globalization;
using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The gain and stability circles of a two-port, beside <c>ns_circle</c>: <c>ga_circle</c> (constant available gain, source
/// plane), <c>gp_circle</c> (constant operating gain, load plane), <c>stab_circle</c> and <c>stab_side</c>. The math is
/// <see cref="TwoPortCircles"/>; this file reads the S cube, puts each port on the real part of its own reference — port 1's
/// for the source plane, port 2's for the load plane, the reference <c>Sopt</c> is on — and lays the points out as
/// <c>ns_circle</c> does, so a Smith-chart trace draws them with nothing new.
/// </summary>
public sealed partial class Evaluator
{
    internal static bool IsCircleBuiltin(string name) => name is "ga_circle" or "gp_circle" or "stab_circle" or "stab_side";

    private Value EvalCircleCall(CallExpr cl, Scope scope) => cl.Name switch
    {
        "ga_circle"   => EvalGainCircle(cl, scope, source: true),
        "gp_circle"   => EvalGainCircle(cl, scope, source: false),
        "stab_circle" => EvalStabilityCircle(cl, scope),
        "stab_side"   => EvalStableSide(cl, scope),
        _             => throw new UnknownFunctionException(cl.Name),
    };

    private List<string>? _notes;

    /// <summary>What the last evaluations had to say that is not a refusal — a gain with no circle at some frequencies —
    /// cleared by taking. A measurement reports them as the run's warnings.</summary>
    public IReadOnlyList<string> TakeNotes()
    {
        var n = _notes ?? [];
        _notes = null;
        return n;
    }

    /// <summary>The S cube as two-port matrices, each port on the real part of its own reference.</summary>
    private (SNetwork S, Mat<Complex>[] M) ReadTwoPortRealRef(CallExpr cl, Scope scope)
    {
        var s = ReadSNetwork(cl, scope);
        if (s.N != 2)
            throw new ExpressionException(CircleFunctionDiagnostics.NotTwoPort(
                cl.Name, $"[{string.Join(", ", s.Leading.Select(a => a.Name))}, {s.N}, {s.N}]"));
        var m = new Mat<Complex>[s.Mats.Length];
        for (int b = 0; b < m.Length; b++)
        {
            var z0 = s.Z0PerSlice[b / s.FreqCount];
            Complex[] real = [new(z0[0].Real, 0), new(z0[1].Real, 0)];
            m[b] = z0[0] == real[0] && z0[1] == real[1] ? s.Mats[b] : RFNetwork.SToS(s.Mats[b], z0, real);
        }
        return (s, m);
    }

    /// <summary>The closed loop of <paramref name="npts"/> points round each circle, NaN where there is none.</summary>
    private static Value CirclePoints(Axis[] leading, GammaCircle?[] circles, int npts)
    {
        var pts = new Complex[circles.Length * npts];
        for (int b = 0; b < circles.Length; b++)
            for (int k = 0; k < npts; k++)
                pts[b * npts + k] = circles[b] is { } c && double.IsFinite(c.Radius)
                    ? c.Centre + c.Radius * Complex.Exp(new Complex(0, 2 * Math.PI * k / (npts - 1)))
                    : new Complex(double.NaN, double.NaN);
        var point = new Axis("point", [.. Enumerable.Range(0, npts).Select(k => (double)k)]);
        return new Value(new DataCube([.. leading, point], pts));
    }

    /// <summary>
    /// <c>ga_circle(S, GdB[, npts])</c>: the source reflections that give an available gain of GdB, on port 1's Re Z0;
    /// <c>gp_circle(S, GdB[, npts])</c>: the load reflections that give an operating gain of GdB, on port 2's. A gain above
    /// <c>max_gain</c> has no circle: its points are NaN, and one note per evaluation names the frequencies.
    /// </summary>
    private Value EvalGainCircle(CallExpr cl, Scope scope, bool source)
    {
        Arity(cl, 2, 3);
        var (s, m) = ReadTwoPortRealRef(cl, scope);
        var g = EvalExpr(cl.Args[1], scope);
        var levels = PerPointLevels(g, m.Length, got => CircleFunctionDiagnostics.Gain(cl.Name, got));
        int npts = cl.Args.Length == 3 ? CirclePointCount(cl, scope, 2) : 101;

        var circles = new GammaCircle?[m.Length];
        var none = new SortedSet<double>();
        var freqs = s.Leading[^1].Values;
        for (int b = 0; b < m.Length; b++)
        {
            double max = RFNetwork.MaxGainLinear(m[b]), gain = Math.Pow(10, levels[b] / 10);
            circles[b] = source ? TwoPortCircles.AvailableGain(m[b], gain, max) : TwoPortCircles.OperatingGain(m[b], gain, max);
            if (circles[b] is null) none.Add(freqs[b % s.FreqCount]);
        }
        if (none.Count > 0)
            (_notes ??= []).Add(CircleFunctionDiagnostics.NoCircle(cl.Name,
                g.Kind == ValueKind.Real ? g.AsReal().ToString("G6", CultureInfo.InvariantCulture) + " dB" : "the gain asked for",
                none.Count, freqs.Length, [.. none]).Render());
        return CirclePoints(s.Leading, circles, npts);
    }

    /// <summary>"source" or "load", argument 1 of the stability functions.</summary>
    private bool SourcePlane(CallExpr cl, Scope scope)
    {
        var v = EvalExpr(cl.Args[1], scope);
        string plane = v.Kind == ValueKind.String ? v.AsString() : v.ToString() ?? "";
        if (plane.Equals("source", StringComparison.OrdinalIgnoreCase)) return true;
        if (plane.Equals("load", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ExpressionException(CircleFunctionDiagnostics.Plane(cl.Name, plane));
    }

    /// <summary><c>stab_circle(S, "source"|"load"[, npts])</c>: where |Γout| = 1 on the source plane, or |Γin| = 1 on the
    /// load plane. It leaves <c>stab_side</c>'s answer beside it as the companion <c>side</c>, which is what a Smith chart
    /// shades the unstable side by.</summary>
    private Value EvalStabilityCircle(CallExpr cl, Scope scope)
    {
        Arity(cl, 2, 3);
        var (s, m) = ReadTwoPortRealRef(cl, scope);
        bool source = SourcePlane(cl, scope);
        int npts = cl.Args.Length == 3 ? CirclePointCount(cl, scope, 2) : 101;
        var circles = m.Select(x => (GammaCircle?)(source ? TwoPortCircles.SourceStability(x) : TwoPortCircles.LoadStability(x)))
                       .ToArray();
        if (s.Leading.Length > 0)
            _companions = new() { [StableSideCompanion] = new DataCube(s.Leading, StableSides(m, source)) };
        return CirclePoints(s.Leading, circles, npts);
    }

    /// <summary>The companion <c>stab_circle</c> leaves: its plane's <see cref="StableSides"/>.</summary>
    public const string StableSideCompanion = "side";

    /// <summary><c>stab_side(S, "source"|"load")</c>: 1 where the inside of that plane's stability circle is the stable
    /// region, −1 where the outside is, per frequency; NaN where the locus is a straight line.</summary>
    private Value EvalStableSide(CallExpr cl, Scope scope)
    {
        Arity(cl, 2, 2);
        var (s, m) = ReadTwoPortRealRef(cl, scope);
        return LeadingCube(s.Leading, StableSides(m, SourcePlane(cl, scope)), "");
    }

    private static double[] StableSides(Mat<Complex>[] m, bool source)
    {
        var vals = new double[m.Length];
        for (int b = 0; b < m.Length; b++)
        {
            var c = source ? TwoPortCircles.SourceStability(m[b]) : TwoPortCircles.LoadStability(m[b]);
            bool inside = source ? TwoPortCircles.StableInsideSource(m[b]) : TwoPortCircles.StableInsideLoad(m[b]);
            vals[b] = double.IsFinite(c.Radius) ? inside ? 1 : -1 : double.NaN;
        }
        return vals;
    }
}

/// <summary>The refusals and the note of the circle functions (Evaluator.Circles.cs), as coded diagnostics.</summary>
public static class CircleFunctionDiagnostics
{
    public static CircuitRF.Diagnostics.Diagnostic NotTwoPort(string name, string shape) => CircuitRF.Diagnostics.Diagnostic.Create(
        "measure.circle.not-two-port", CircuitRF.Diagnostics.DiagnosticSeverity.Error,
        "{name}(): gain and stability circles belong to a two-port, an S cube shaped [freq, 2, 2]; this one is {shape}.",
        ("name", name), ("shape", shape));

    public static CircuitRF.Diagnostics.Diagnostic Gain(string name, string got) => CircuitRF.Diagnostics.Diagnostic.Create(
        "measure.circle.gain", CircuitRF.Diagnostics.DiagnosticSeverity.Error,
        "{name}(): the gain is a real number in dB, as in {name}(SP1.S, 12), or one per frequency point, as in " +
        "{name}(SP1.S, max_gain(SP1.S) - 1); got {got}.",
        ("name", name), ("got", got));

    public static CircuitRF.Diagnostics.Diagnostic Plane(string name, string got) => CircuitRF.Diagnostics.Diagnostic.Create(
        "measure.circle.plane", CircuitRF.Diagnostics.DiagnosticSeverity.Error,
        "{name}(): the plane is \"source\" or \"load\", as in {name}(SP1.S, \"source\"); got {got}.",
        ("name", name), ("got", got));

    /// <summary>A gain with no circle at some frequencies: above the two-port's maximum gain there.</summary>
    public static CircuitRF.Diagnostics.Diagnostic NoCircle(string name, string gain, int count, int total, double[] hz)
    {
        const int Shown = 6;
        var inv = CultureInfo.InvariantCulture;
        string list = string.Join(", ", hz.Take(Shown).Select(f => (f / 1e9).ToString("G6", inv))) +
                      (hz.Length > Shown ? $" and {hz.Length - Shown} more" : "");
        return CircuitRF.Diagnostics.Diagnostic.Create(
            "measure.circle.none", CircuitRF.Diagnostics.DiagnosticSeverity.Warning,
            "{name}(): {gain} is above the maximum gain at {count} of {total} frequencies ({list} GHz), so there is no " +
            "circle there and its points are NaN.",
            ("name", name), ("gain", gain), ("count", count), ("total", total), ("list", list));
    }
}
