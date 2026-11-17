using System.Globalization;
using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// <c>waveform(x[, f0])</c> — a harmonic-balance spectrum (or <c>fft</c>'s) as the real waveform it stands for, over one
/// period (brief-run-display-hb-preset-and-units R-rdh-3). It is <c>fft</c>'s inverse, in the full-amplitude convention
/// harmonic balance reports: x(t) = Σ Re{Xₘ e^{j2πfₘt}}, so X₀ is the mean and every other product's magnitude is its peak.
///
/// <para><b>The spectral axis is replaced IN PLACE by a <c>time</c> axis</b> (HB's <c>V[node, harmonic]</c> becomes
/// <c>V[node, time]</c>), every other axis kept, so every time-domain function reads the result unchanged and
/// <c>fft(waveform(x, f0), f0)</c> gives <c>x</c> back.</para>
///
/// <para><b>The period.</b> On a <c>harmonic</c> axis the values are harmonic numbers, so the fundamental is an argument, as
/// <c>fft</c>'s is. On a <c>mixIndex</c> axis the values are the products' frequencies, each stored once with its sign (a
/// negative-frequency representative is the conjugate, so its term is the same), and the period is the tones' common one:
/// the largest f0 that every product is a whole multiple of. Tones with none, or whose common period would take more than
/// <see cref="WaveformMaxSamples"/> samples, are refused by name; a waveform cut short of its period is not one.</para>
/// </summary>
public sealed partial class Evaluator
{
    /// <summary>Samples per period of the highest product, and the fewest a period gets.</summary>
    public const int WaveformSamplesPerCycle = 16, WaveformMinSamples = 128;

    /// <summary>The most samples one period may take: past this the tones' common period is too long to draw.</summary>
    public const int WaveformMaxSamples = 65_536;

    /// <summary>How close to a whole multiple of f0 every product must be, in cycles of f0 — absolute, not relative: a tolerance
    /// growing with the multiple would let a rational approximation of an irrational ratio pass (√2 within 1e-7 by 3363/2378),
    /// while frequencies typed in any unit are whole multiples to within ~1e-12 cycles.</summary>
    private const double WaveformCycleTol = 1e-6;

    private Value EvalWaveform(CallExpr cl, Scope scope)
    {
        const string name = "waveform";
        TdArity(cl, 1, 2);
        var v = EvalExpr(cl.Args[0], scope);
        if (v.Kind != ValueKind.Cube)
            throw Refuse("expr.wave.scalar",
                "waveform(): this value is a single number; waveform reads a spectrum, as a harmonic-balance result's HB1.V(\"out\") is.");
        var cube = v.AsCube();
        int dim = -1;
        for (int d = 0; d < cube.Rank; d++)
            if (cube.Axes[d].Name is "harmonic" or "mixIndex") { dim = d; break; }
        if (dim < 0)
            throw Refuse("expr.wave.no-spectrum",
                "waveform() reads a spectrum and this value has no 'harmonic' or 'mixIndex' axis (its axes are: {axes}).", ("axes", AxesOf(cube)));
        if (cube.DataKind != DataKind.Complex)
            throw Refuse("expr.wave.real",
                "waveform(): a waveform needs each product's phase as well as its size, and this value is real; give the complex spectrum, as HB1.V(\"out\"), not its magnitude.");

        var axis = cube.Axes[dim];
        bool harmonic = axis.Name == "harmonic";
        double? given = cl.Args.Length > 1 ? Number(cl, scope, 1, name, "the fundamental frequency, as waveform(x, 1GHz)") : null;
        if (given is { } g && !(g > 0))
            throw Refuse("expr.wave.f0", "waveform(): the fundamental frequency must be positive, as waveform(x, 1GHz).");
        if (harmonic && given is null)
            throw Refuse("expr.wave.need-f0",
                "waveform(): a 'harmonic' axis counts harmonics, not hertz; give the fundamental, as waveform(HB1.V(\"out\"), 1GHz).");

        // Each product's frequency in hertz.
        var hz = harmonic ? axis.Values.Select(k => k * given!.Value).ToArray() : axis.Values.ToArray();
        double f0 = given ?? CommonFundamental(hz);
        double highest = 0;
        foreach (double f in hz)
        {
            double m = Math.Abs(f) / f0;
            if (Math.Abs(m - Math.Round(m)) > WaveformCycleTol)
                throw Refuse("expr.wave.not-multiple",
                    "waveform(): the product at {f} Hz is not a whole multiple of {f0} Hz, so the waveform has no period of 1/{f0} s.",
                    ("f", G(f)), ("f0", G(f0)));
            highest = Math.Max(highest, Math.Round(m));
        }
        double nd = Math.Max(WaveformMinSamples, WaveformSamplesPerCycle * highest);
        if (nd > WaveformMaxSamples)
            throw Refuse("expr.wave.too-long",
                "waveform(): the products repeat every {p} s (1/{f0} Hz), which takes {n} samples to draw, more than {max}; " +
                "plot the spectrum instead, or move the tones onto a shorter common period.",
                ("p", G(1 / f0)), ("f0", G(f0)), ("n", G(nd)), ("max", WaveformMaxSamples.ToString(CultureInfo.InvariantCulture)));
        int n = (int)nd;

        // One period, both ends drawn: n intervals of 1/(n·f0). fft's grid reads the first n of them back exactly.
        var t = new double[n + 1];
        for (int s = 0; s <= n; s++) t[s] = s / (n * f0);

        // e^{j2πfₘt} per product and sample.
        int np = hz.Length;
        var basis = new Complex[np * (n + 1)];
        for (int m = 0; m < np; m++)
            for (int s = 0; s <= n; s++)
            {
                double ph = 2 * Math.PI * hz[m] * t[s];
                basis[m * (n + 1) + s] = new Complex(Math.Cos(ph), Math.Sin(ph));
            }

        var data = cube.ComplexValues;
        int inner = 1, outer = 1, nt = n + 1;
        for (int d = dim + 1; d < cube.Rank; d++) inner *= cube.Axes[d].Length;
        for (int d = 0; d < dim; d++) outer *= cube.Axes[d].Length;
        var result = new double[outer * nt * inner];
        for (int o = 0; o < outer; o++)
            for (int i = 0; i < inner; i++)
                for (int s = 0; s <= n; s++)
                {
                    double x = 0;
                    for (int m = 0; m < np; m++)
                    {
                        var z = data[(o * np + m) * inner + i] * basis[m * nt + s];
                        x += z.Real;
                    }
                    result[(o * nt + s) * inner + i] = x;
                }
        var axes = cube.Axes.Select((a, d) => d == dim ? new Axis(TimeAxisName, t, "s") : a).ToArray();
        return new Value(new DataCube(axes, result) { Unit = cube.Unit });
    }

    /// <summary>The largest frequency every product is a whole multiple of, found from the smallest non-zero one: f₍min₎/d for
    /// the first d that fits. Refused when no d up to the sample budget does — the tones then have no period to draw.</summary>
    private static double CommonFundamental(double[] hz)
    {
        var mags = hz.Select(Math.Abs).Where(f => f > 0).ToArray();
        if (mags.Length == 0)
            throw Refuse("expr.wave.dc-only", "waveform(): the spectrum holds DC only, which has no period; give the fundamental, as waveform(x, 1GHz).");
        double fmin = mags.Min(), fmax = mags.Max();
        // f0 below this would take more than the budget's samples at the highest product
        double floor = WaveformSamplesPerCycle * fmax / WaveformMaxSamples;
        for (int d = 1; fmin / d >= floor; d++)
        {
            double f0 = fmin / d;
            if (mags.All(f => { double m = f / f0; return Math.Abs(m - Math.Round(m)) <= WaveformCycleTol; }))
                return f0;
        }
        throw Refuse("expr.wave.no-period",
            "waveform(): the products from {lo} Hz to {hi} Hz share no common period short enough to draw in {max} samples; " +
            "plot the spectrum instead.",
            ("lo", G(fmin)), ("hi", G(fmax)), ("max", WaveformMaxSamples.ToString(CultureInfo.InvariantCulture)));
    }
}
