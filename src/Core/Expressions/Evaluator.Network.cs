using System.Numerics;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// The network-metric built-ins — <c>mu</c>, <c>mu_prime</c>, <c>K</c>, <c>delta_mag</c>,
/// <c>max_gain</c>, <c>max_gain_lin</c>, <c>passivity</c> — plus <c>group_delay</c> and <c>vswr</c>
/// (brief-tuneopt-9 R-to9-1/2), so a stability factor or a delay is one goal expression away; and the
/// noise figure from any source, <c>nf</c>, with its constant-NF circles, <c>nf_circle</c>, both read off
/// the NFmin, Rn and GammaOpt cubes a <c>Noise=yes</c> run puts beside S.
///
/// <para><b>Nothing here computes a metric.</b> Every value comes from <see cref="NetworkMetrics"/>'
/// matrix-level overloads — the same calls the Data Display's µ/K/MAG/σ/group-delay traces make —
/// and <see cref="RfHelpers.VswrFromGamma"/>. This file reads an <c>{…, freq, i, j}</c> S cube into
/// matrices, finds the reference impedances, and reassembles the answer over the leading axes.</para>
///
/// <para><b>The reference impedances come from the analysis that OWNS the cube</b>, found by
/// reference exactly as the WSProbe functions find their probe labels
/// (<see cref="MeasurementContext.TryFindOwner"/>). <see cref="NetworkMetrics"/>' rule — renormalize
/// to a uniform real reference first, always — needs the per-port Z0 an S cube does not carry, so a
/// cube that has been sliced or computed (and so has no owner) is refused rather than assumed to be
/// 50 Ω: a silently wrong reference gives a plausible µ.</para>
/// </summary>
public sealed partial class Evaluator
{
    internal static bool IsNetworkBuiltin(string name) => name is
        "mu" or "mu_prime" or "K" or "delta_mag" or "max_gain" or "max_gain_lin" or "passivity" or
        "group_delay" or "vswr" or "nf" or "nf_circle";

    private Value EvalNetworkCall(CallExpr cl, Scope scope) => cl.Name switch
    {
        "mu"           => TwoPortMetric(cl, scope, NetworkMetric.Mu),
        "mu_prime"     => TwoPortMetric(cl, scope, NetworkMetric.MuPrime),
        "K"            => TwoPortMetric(cl, scope, NetworkMetric.K),
        "delta_mag"    => TwoPortMetric(cl, scope, NetworkMetric.DeltaMag),
        "max_gain"     => TwoPortMetric(cl, scope, NetworkMetric.MaxGain, unit: "dB"),
        "max_gain_lin" => TwoPortMetric(cl, scope, NetworkMetric.MaxGainLinear),
        "passivity"    => EvalPassivity(cl, scope),
        "group_delay"  => EvalGroupDelay(cl, scope),
        "vswr"         => EvalVswr(cl, scope),
        "nf"           => EvalNf(cl, scope),
        "nf_circle"    => EvalNfCircle(cl, scope),
        _              => throw new UnknownFunctionException(cl.Name),
    };

    // ── The S-cube reader ────────────────────────────────────────────────────

    /// <summary>An S cube read as matrices: the leading axes (freq last), one matrix per leading
    /// point, and the per-port reference impedances for each block of <c>FreqCount</c> matrices; <c>Owner</c> is the
    /// analysis's DataSet, where its other cubes are.</summary>
    private sealed record SNetwork(Axis[] Leading, int N, Mat<Complex>[] Mats, int FreqCount, Complex[][] Z0PerSlice, DataSet Owner);

    private SNetwork ReadSNetwork(CallExpr cl, Scope scope)
    {
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind != ValueKind.Cube)
            throw new TypeErrorException(
                $"{cl.Name}(): the first argument must be an analysis's S cube — SP1.S — not a single number.");
        var cube = v.AsCube();
        if (cube.DataKind != DataKind.Complex)
            throw new TypeErrorException($"{cl.Name}(): the first argument must be an S cube (complex), e.g. SP1.S.");
        var (leading, n, raw) = MatrixCube(cube, cl.Name, "an S cube");
        if (leading.Length == 0 || leading[^1].Name != "freq")
            throw new ExpressionException(
                $"{cl.Name}(): the S cube must be laid out {{…, freq, i, j}}; this one's axes are " +
                $"{string.Join(", ", cube.Axes.Select(a => a.Name))}.");

        DataSet? owner = null;
        _ctx?.TryFindOwner(cube, NetworkMetrics.SCubeName, out owner);
        if (owner is null)
            throw new ExpressionException(
                $"{cl.Name}(): pass the analysis's S cube itself (SP1.S). This value is not one an analysis " +
                "produced — sliced or computed — so its ports' reference impedances are unknown, and the " +
                "metric is only defined after renormalizing to a uniform real reference.");

        int nFreq  = leading[^1].Length;
        int blocks = n == 0 ? 0 : raw.Length / (n * n);
        var mats   = new Mat<Complex>[blocks];
        for (int b = 0; b < blocks; b++)
        {
            var m = new Mat<Complex>(n, n);
            int o = b * n * n;
            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                m[r, c] = raw[o + r * n + c];
            mats[b] = m;
        }

        // Z0 is [port] for one run, or [sweep…, port] when a parametric sweep stacked it alongside S.
        int slices = nFreq == 0 ? 0 : blocks / nFreq;
        Complex[][] z0;
        if (owner.Contains(NetworkMetrics.Z0CubeName))
        {
            var zv = owner[NetworkMetrics.Z0CubeName].ComplexValues;
            if (zv.Length == n)
                z0 = [.. Enumerable.Repeat(zv, Math.Max(slices, 1))];
            else if (zv.Length == slices * n)
                z0 = [.. Enumerable.Range(0, slices).Select(s => zv[(s * n)..((s + 1) * n)])];
            else
                throw new ExpressionException(
                    $"{cl.Name}(): the analysis's Z0 cube holds {zv.Length} values, which is not one per port " +
                    $"({n}) nor one per port per sweep point ({slices * n}).");
        }
        else
        {
            // The fallback NetworkMetrics.ReadZ0 applies to a DataSet with no Z0 cube (a legacy file).
            var fallback = NetworkMetrics.ReadZ0(owner, n);
            z0 = [.. Enumerable.Repeat(fallback, Math.Max(slices, 1))];
        }
        return new SNetwork(leading, n, mats, nFreq, z0, owner);
    }

    /// <summary>The ordered (input, output) port pair: arguments 2 and 3 when given, (1, 2) for a
    /// two-port, otherwise a refusal naming the spelling.</summary>
    private (int In, int Out) PortPair(CallExpr cl, Scope scope, int n)
    {
        if (cl.Args.Length == 3)
        {
            int i = PortArg(cl, scope, 1, n), j = PortArg(cl, scope, 2, n);
            if (i == j)
                throw new ExpressionException($"{cl.Name}(): the input and output port must differ; both are {i}.");
            return (i, j);
        }
        if (n == 2) return (1, 2);
        throw new ExpressionException(
            n < 2
                ? $"{cl.Name}(): a two-port metric needs at least 2 ports; this network has {n}."
                : $"{cl.Name}(): this network has {n} ports; name the input and output port — {cl.Name}(SP1.S, 1, 2).");
    }

    private int PortArg(CallExpr cl, Scope scope, int i, int n)
    {
        var v = EvalExpr(cl.Args[i], scope);
        if (v.Kind != ValueKind.Real)
            throw new TypeErrorException($"{cl.Name}(): argument {i + 1} must be a 1-based port number.");
        int p = (int)Math.Round(v.AsReal());
        if (p < 1 || p > n)
            throw new ExpressionException($"{cl.Name}(): port {p} is outside 1..{n}.");
        return p;
    }

    /// <summary>Run <paramref name="perSlice"/> over each sweep slice's block of matrices (each slice
    /// has its own reference impedances) and concatenate the answers in cube order.</summary>
    private static double[] PerSlice(SNetwork s, Func<Mat<Complex>[], Complex[], double[], double[]> perSlice)
    {
        var freqs = s.Leading[^1].Values;
        var all   = new double[s.Mats.Length];
        for (int slice = 0, o = 0; o < s.Mats.Length; slice++, o += s.FreqCount)
        {
            var part = perSlice(s.Mats[o..(o + s.FreqCount)], s.Z0PerSlice[slice], freqs);
            Array.Copy(part, 0, all, o, part.Length);
        }
        return all;
    }

    // ── The metrics ──────────────────────────────────────────────────────────

    private Value TwoPortMetric(CallExpr cl, Scope scope, NetworkMetric metric, string unit = "")
    {
        if (cl.Args.Length is not (1 or 3)) throw new ArityException(cl.Name, 1, cl.Args.Length);
        var s = ReadSNetwork(cl, scope);
        var (pin, pout) = PortPair(cl, scope, s.N);
        var vals = PerSlice(s, (m, z0, _) => NetworkMetrics.TwoPortMetric(m, z0, metric, pin, pout));
        return LeadingCube(s.Leading, vals, unit);
    }

    /// <summary><c>passivity(S)</c> is σ_max of the whole network, any N; <c>passivity(S, i, j)</c>
    /// is that of the extracted (input, output) two-port — not the same thing on an N-port.</summary>
    private Value EvalPassivity(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length is not (1 or 3)) throw new ArityException(cl.Name, 1, cl.Args.Length);
        var s = ReadSNetwork(cl, scope);
        double[] vals;
        if (cl.Args.Length == 3)
        {
            var (pin, pout) = PortPair(cl, scope, s.N);
            vals = PerSlice(s, (m, z0, _) => NetworkMetrics.PassivityPair(m, z0, pin, pout));
        }
        else vals = PerSlice(s, (m, z0, _) => NetworkMetrics.PassivityFull(m, z0));
        return LeadingCube(s.Leading, vals, "");
    }

    /// <summary>
    /// <c>group_delay(z)</c> — −dφ/dω along <c>freq</c> of a complex cube, unwrapped, in seconds — or
    /// <c>group_delay(S, in, out)</c>, the delay from port <c>in</c> to port <c>out</c> after the same
    /// renormalization the other network metrics make (S21's phase depends on the reference). Both
    /// reach <see cref="RFNetwork.GroupDelay(IReadOnlyList{Complex}, IReadOnlyList{double})"/>.
    /// </summary>
    private Value EvalGroupDelay(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length == 3)
        {
            var s = ReadSNetwork(cl, scope);
            var (pin, pout) = PortPair(cl, scope, s.N);
            var vals = PerSlice(s, (m, z0, f) => NetworkMetrics.GroupDelay(m, z0, f, pin, pout));
            return LeadingCube(s.Leading, vals, "s");
        }
        if (cl.Args.Length != 1) throw new ArityException(cl.Name, 1, cl.Args.Length);

        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind != ValueKind.Cube)
            throw new ExpressionException(
                "group_delay(): the argument is a single number, so it has no 'freq' axis to differentiate along.");
        var cube = v.AsCube();
        if (cube.DataKind != DataKind.Complex)
            throw new TypeErrorException(
                "group_delay(): the argument must be complex — a transmission coefficient such as SP1.S(2, 1) — " +
                "since the delay is the slope of its phase.");
        int a = cube.Axes.ToList().FindIndex(x => x.Name == "freq");
        if (a < 0)
            throw new ExpressionException(
                $"group_delay(): the value has no 'freq' axis; its axes are {string.Join(", ", cube.Axes.Select(x => x.Name))}.");

        // Every line along freq, whatever axes sit either side of it.
        var raw   = cube.ComplexValues;
        var freqs = cube.Axes[a].Values;
        int len   = freqs.Length;
        int inner = 1;
        for (int k = a + 1; k < cube.Rank; k++) inner *= cube.Axes[k].Length;
        int outer = len == 0 ? 0 : raw.Length / (len * inner);
        var outv  = new double[raw.Length];
        var line  = new Complex[len];
        for (int o = 0; o < outer; o++)
        for (int i = 0; i < inner; i++)
        {
            int baseIdx = o * len * inner + i;
            for (int f = 0; f < len; f++) line[f] = raw[baseIdx + f * inner];
            var tau = RFNetwork.GroupDelay(line, freqs);
            for (int f = 0; f < len; f++) outv[baseIdx + f * inner] = tau[f];
        }
        return new Value(new DataCube([.. cube.Axes], outv) { Unit = "s" });
    }

    /// <summary><c>vswr(Γ)</c> = (1 + |Γ|)/(1 − |Γ|), element-wise; +∞ at and beyond |Γ| = 1, where
    /// no standing-wave ratio exists, rather than the negative number the formula gives there.</summary>
    private Value EvalVswr(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 1) throw new ArityException(cl.Name, 1, cl.Args.Length);
        var v = EvalExpr(cl.Args[0], scope);
        static double Of(Complex g)
        {
            double r = RfHelpers.VswrFromGamma(Complex.Zero, g);
            return r >= 1 ? r : double.PositiveInfinity;
        }
        switch (v.Kind)
        {
            case ValueKind.Real:    return new Value(Of(new Complex(v.AsReal(), 0)));
            case ValueKind.Complex: return new Value(Of(v.AsComplex()));
            case ValueKind.Cube:
                var cube = v.AsCube();
                double[] outv = cube.DataKind == DataKind.Complex
                    ? [.. cube.ComplexValues.Select(Of)]
                    : [.. cube.RealValues.Select(x => Of(new Complex(x, 0)))];
                return new Value(new DataCube([.. cube.Axes], outv));
            default:
                throw new TypeErrorException("vswr(): the argument must be a reflection coefficient.");
        }
    }

    // ── Noise ────────────────────────────────────────────────────────────────

    /// <summary>The two-port noise parameters the analysis owning the S cube reported, one per leading point, with
    /// Γopt on Re Z0 of port 1 of that point's sweep slice — read from its NFmin, Rn and GammaOpt cubes, which a
    /// sweep stacks as it stacks S.</summary>
    private (SNetwork S, TwoPortNoise[] P) ReadNoise(CallExpr cl, Scope scope)
    {
        var s = ReadSNetwork(cl, scope);
        if (s.N != 2) throw new ExpressionException(NoiseFunctionDiagnostics.NotTwoPort(cl.Name, s.N));
        if (!(s.Owner.Contains("NFmin") && s.Owner.Contains("Rn") && s.Owner.Contains("GammaOpt")))
            throw new ExpressionException(NoiseFunctionDiagnostics.NotRun(cl.Name));
        double[] nfMin = s.Owner["NFmin"].RealValues, rn = s.Owner["Rn"].RealValues;
        Complex[] gOpt = s.Owner["GammaOpt"].ComplexValues;
        foreach (var (name, got) in new[] { ("NFmin", nfMin.Length), ("Rn", rn.Length), ("GammaOpt", gOpt.Length) })
            if (got != s.Mats.Length)
                throw new ExpressionException(NoiseFunctionDiagnostics.CubeShape(cl.Name, name, got, s.Mats.Length));

        var p = new TwoPortNoise[s.Mats.Length];
        for (int b = 0; b < p.Length; b++)
            p[b] = new TwoPortNoise(Math.Pow(10, nfMin[b] / 10), gOpt[b], rn[b], s.Z0PerSlice[b / s.FreqCount][0].Real);
        return (s, p);
    }

    /// <summary>
    /// <c>nf(S, Γs)</c> — the noise figure in dB from a source of reflection Γs (on Re Z0 of port 1), per frequency:
    /// <c>F = Fmin + 4(Rn/R)|Γs − Γopt|²/((1 − |Γs|²)|1 + Γopt|²)</c> (<see cref="NoiseMetrics.NoiseFactor"/>). Γs is a
    /// number, or a cube with one value per frequency point (<c>SP1.GammaOpt</c>). NaN where |Γs| ≥ 1: no passive
    /// source presents that, and the formula's number there means nothing.
    /// </summary>
    private Value EvalNf(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length != 2) throw new ArityException(cl.Name, 2, cl.Args.Length);
        var (s, p) = ReadNoise(cl, scope);
        var g = EvalExpr(cl.Args[1], scope);
        Complex[] gs = g.Kind switch
        {
            ValueKind.Real    => [.. Enumerable.Repeat(new Complex(g.AsReal(), 0), p.Length)],
            ValueKind.Complex => [.. Enumerable.Repeat(g.AsComplex(), p.Length)],
            ValueKind.Cube when g.AsCube().BufferLength == p.Length =>
                g.AsCube().DataKind == DataKind.Complex ? g.AsCube().ComplexValues
                                                        : [.. g.AsCube().RealValues.Select(x => new Complex(x, 0))],
            ValueKind.Cube => throw new ExpressionException(NoiseFunctionDiagnostics.Source(cl.Name, $"{g.AsCube().BufferLength} values", p.Length)),
            _              => throw new ExpressionException(NoiseFunctionDiagnostics.Source(cl.Name, g.Kind.ToString(), p.Length)),
        };
        var vals = new double[p.Length];
        for (int b = 0; b < p.Length; b++)
            vals[b] = gs[b].Magnitude >= 1 ? double.NaN : 10 * Math.Log10(NoiseMetrics.NoiseFactor(p[b], gs[b]));
        return LeadingCube(s.Leading, vals, "dB");
    }

    /// <summary>
    /// <c>nf_circle(S, NFdB[, npts])</c> — the source reflections that give a noise figure of NFdB, as a closed loop of
    /// <c>npts</c> (default 101) points per frequency: <c>{…, freq, point}</c>, point k at angle 2πk/(npts − 1) round
    /// <see cref="NoiseMetrics.NoiseCircle"/>, on Re Z0 of port 1. Below NFmin there is no circle and the points are NaN.
    /// </summary>
    private Value EvalNfCircle(CallExpr cl, Scope scope)
    {
        if (cl.Args.Length is not (2 or 3)) throw new ArityException(cl.Name, 2, cl.Args.Length);
        var (s, p) = ReadNoise(cl, scope);
        var nf = EvalExpr(cl.Args[1], scope);
        if (nf.Kind != ValueKind.Real) throw new ExpressionException(NoiseFunctionDiagnostics.Figure(cl.Name, nf.Kind.ToString()));
        int npts = 101;
        if (cl.Args.Length == 3)
        {
            var v = EvalExpr(cl.Args[2], scope);
            double x = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
            if (!(x >= 3) || x != Math.Floor(x) || x > int.MaxValue)
                throw new ExpressionException(NoiseFunctionDiagnostics.CirclePoints(cl.Name,
                    v.Kind == ValueKind.Real ? x.ToString(System.Globalization.CultureInfo.InvariantCulture) : v.Kind.ToString()));
            npts = (int)x;
        }

        double f = Math.Pow(10, nf.AsReal() / 10);
        var pts = new Complex[p.Length * npts];
        for (int b = 0; b < p.Length; b++)
        {
            var (centre, radius) = NoiseMetrics.NoiseCircle(p[b], f);
            for (int k = 0; k < npts; k++)
                pts[b * npts + k] = centre + radius * Complex.Exp(new Complex(0, 2 * Math.PI * k / (npts - 1)));
        }
        var point = new Axis("point", [.. Enumerable.Range(0, npts).Select(k => (double)k)]);
        return new Value(new DataCube([.. s.Leading, point], pts));
    }
}
