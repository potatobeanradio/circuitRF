// ================================================================
//  TransientModulated.cs  —  a MOD_Source played in time (brief MTR-1; docs/design/transient.md §6.4)
//
//  v(t) = Re{ Vs · g(t) · b(RecordStart + t) · e^(j2πFc·t) } behind its Z: Vs its peak available-drive phasor, b the
//  composite baseband record (unit mean power, each carrier at its offset from Fc), g a raised-cosine soft start over
//  Ramp. b is read from a table over the run's window, built once by the signal's own band-limited window interpolation
//  and read between its points by the cubic through the four nearest.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Engine.Transient;

internal sealed class TransientModulated
{
    /// <summary>Table points per hertz of the record's widest excursion from Fc (offset plus half the native rate).</summary>
    internal const double TableOversample = 32;

    /// <summary>The longest table a run builds (points); past it the run is refused, naming the window.</summary>
    internal const int MaxTablePoints = 1 << 24;

    public required ElaboratedComponent Ec { get; init; }
    public required ModSourceModel Model { get; init; }
    /// <summary>The source's right-hand-side column at Fc for its drive Vs (real and imaginary parts).</summary>
    public double[] Re = [], Im = [];
    public required double RecordStartS { get; init; }
    public required double RampS { get; init; }
    public required double RateHz { get; init; }
    private long _first;          // the table's first point, in record samples at RateHz
    private int _count;
    private Complex[]? _table;

    /// <summary>The record's widest excursion from Fc: the largest |carrier offset| plus half that carrier's native rate.</summary>
    public static double SpanHz(ModSourceModel m) =>
        m.Signal.Carriers.Select(c => Math.Abs(c.OffsetHz) + c.NativeRateHz / 2).DefaultIfEmpty(0).Max();

    /// <summary>The highest frequency the source brings to the run: Fc plus <see cref="SpanHz"/>.</summary>
    public static double HighestHz(ModSourceModel m) => m.FcHz + SpanHz(m);

    /// <summary>The source as a played drive over a run of <paramref name="stopS"/>, or a refusal.</summary>
    public static TransientModulated Create(ElaboratedComponent ec, ModSourceModel m, double stopS, out string? refusal)
    {
        double rate = TableOversample * Math.Max(SpanHz(m), 1.0);
        double recordStart = Key(ec, "RecordStart"), ramp = Key(ec, "Ramp");
        var d = new TransientModulated { Ec = ec, Model = m, RecordStartS = recordStart, RampS = ramp, RateHz = rate };
        refusal = null;
        if (ramp < 0) { refusal = $"{ec.InstancePath}: Ramp must not be negative."; return d; }
        if (!double.IsFinite(recordStart)) { refusal = $"{ec.InstancePath}: RecordStart is not a finite time."; return d; }
        double points = stopS * rate + 6;
        if (points > MaxTablePoints)
        {
            refusal = string.Format(CultureInfo.InvariantCulture,
                "{0} (MOD_Source): a {1:G4} s window of its signal is {2:G3} points at {3:G4} Hz, more than a transient run samples "
                + "({4}); shorten Stop.", ec.InstancePath, stopS, points, rate, MaxTablePoints);
            return d;
        }
        d._first = (long)Math.Floor(recordStart * rate) - 2;
        d._count = (int)Math.Ceiling(points);
        return d;
    }

    /// <summary>g(t): 0 before the start, the raised cosine over Ramp, 1 after.</summary>
    public double Envelope(double t)
    {
        if (t < 0) return 0;
        return RampS > 0 && t < RampS ? 0.5 * (1 - Math.Cos(Math.PI * t / RampS)) : 1;
    }

    /// <summary>b(RecordStart + t), the composite baseband (unit mean power).</summary>
    public Complex Baseband(double t)
    {
        var tab = _table ??= Model.Signal.CompositeWindow(RateHz, 0, _first, _count);
        double u = (RecordStartS + t) * RateHz - _first;
        int i = (int)Math.Floor(u);
        i = Math.Clamp(i, 1, tab.Length - 3);
        double f = u - i;
        // the cubic through points i-1 … i+2 (Lagrange), at i + f
        double w0 = -f * (f - 1) * (f - 2) / 6, w1 = (f + 1) * (f - 1) * (f - 2) / 2, w2 = -(f + 1) * f * (f - 2) / 2, w3 = (f + 1) * f * (f - 1) / 6;
        return w0 * tab[i - 1] + w1 * tab[i] + w2 * tab[i + 1] + w3 * tab[i + 2];
    }

    /// <summary>The instantaneous available-drive phasor at t (V peak): Vs·g(t)·b(RecordStart + t), as the envelope reports it.</summary>
    public Complex Drive(double t) => Model.VsPeak * Model.DriveScale * Envelope(t) * Baseband(t);

    /// <summary>Adds the source's drive at <paramref name="t"/> to the right-hand side.</summary>
    public void AddTo(double t, double[] b)
    {
        double g = Envelope(t);
        if (g == 0) return;
        var z = g * Baseband(t) * Complex.FromPolarCoordinates(1, 2 * Math.PI * Model.FcHz * t);
        double c = z.Real, s = z.Imaginary;
        for (int i = 0; i < b.Length; i++) b[i] += Re[i] * c - Im[i] * s;
    }

    private static double Key(ElaboratedComponent ec, string key) =>
        ec.Parameters.TryGetValue(key, out var v) && v.Kind is ValueKind.Real or ValueKind.Complex
            ? (v.Kind == ValueKind.Real ? v.AsReal() : v.AsComplex().Real) : 0;
}
