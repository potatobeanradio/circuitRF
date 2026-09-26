// brief-em3d-29 R-em3d29-3b / 3c / 3d — what is drawn from a field array, and in which colours.
//
// QUANTITIES (3b). From a vector: its PEAK magnitude √(|Re|² + |Im|²) — |E| — and, when complex, its
// INSTANTANEOUS magnitude |Re{E·e^{jφ}}| = |Re·cos φ − Im·sin φ|, which the phase animation sweeps. From a
// scalar: its value (signed, a real potential), or when complex its magnitude and its instantaneous value
// Re{V·e^{jφ}}. A quantity needing an array the files do not hold is not offered: the choices are built
// from the arrays a step LISTS, never derived from another array.
//
// COLOUR (3c). Linear or dB. The range is automatic, clamped at a chosen PERCENTILE of the values drawn so
// one singular edge — a conductor's corner, where the field is genuinely unbounded — does not wash out the
// picture; the percentile and the range are stated in the legend. A value outside the range is drawn in
// the END colour (the shader clamps), never transparent. An animated quantity's range is its PEAK's, so the
// colours do not rescale while the phase turns.
//
// THE GPU PATH (2c / 3d). A drawn vertex carries its position and the value's real and imaginary parts
// (FieldVertex, 36 bytes, floats). The phase is a UNIFORM: an animated frame uploads no vertex at all.

using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CircuitRF.Render.Scene3D.Fields;

/// <summary>How a value is read from an array's channels.</summary>
public enum FieldMode
{
    /// <summary>A vector's (or complex scalar's) peak magnitude: |E|.</summary>
    Peak,
    /// <summary>Re{v·e^{jφ}}: a vector's magnitude, a scalar's signed value. Animated.</summary>
    Instantaneous,
    /// <summary>A real scalar's signed value (a potential).</summary>
    Value,
}

/// <summary>A drawable quantity: an array, where it lives, and how it is read.</summary>
public sealed record FieldQuantity(FieldArrayInfo Array, bool OnBoundary, FieldMode Mode)
{
    public bool IsVector => Array.Components == 3;
    /// <summary>Signed: drawn on a diverging map with a symmetric range, never in dB.</summary>
    public bool Signed => !IsVector && Mode is FieldMode.Instantaneous or FieldMode.Value;
    public bool Animated => Mode == FieldMode.Instantaneous;

    /// <summary>The shader's mode number (scene.wgsl fs_field).</summary>
    public int ShaderMode => (IsVector, Mode) switch
    {
        (true, FieldMode.Instantaneous) => 1,
        (true, _) => 0,
        (false, FieldMode.Value) => 2,
        (false, FieldMode.Instantaneous) => 3,
        _ => 4,
    };

    /// <summary>"|E|", "Re{E·e^{jφ}}", "V".</summary>
    public string Symbol => (IsVector, Mode) switch
    {
        (true, FieldMode.Instantaneous) => $"|Re{{{Array.Name}·e^{{jφ}}}}|",
        (true, _) => $"|{Array.Name}|",
        (false, FieldMode.Instantaneous) => $"Re{{{Array.Name}·e^{{jφ}}}}",
        (false, FieldMode.Peak) => $"|{Array.Name}|",
        _ => Array.Name,
    };

    public string Label => $"{Symbol} — {FieldNames.Friendly(Array.Name)}{(OnBoundary ? " (surfaces)" : "")}"
                           + (Mode == FieldMode.Instantaneous ? ", animated" : "");

    /// <summary>
    /// The quantities a step's arrays offer (3b): every listed array, read every way its kind allows. The
    /// error indicator and other per-cell arrays are offered as values on the volume only.
    /// </summary>
    public static IReadOnlyList<FieldQuantity> Offered(IEnumerable<FieldArrayInfo> volume, IEnumerable<FieldArrayInfo> boundary)
    {
        var list = new List<FieldQuantity>();
        void Add(FieldArrayInfo a, bool onBoundary)
        {
            if (a.Components is not (1 or 3)) return;
            if (a.Components == 3 || a.IsComplex) list.Add(new(a, onBoundary, FieldMode.Peak));
            if (a.IsComplex) list.Add(new(a, onBoundary, FieldMode.Instantaneous));
            if (a.Components == 1 && !a.IsComplex) list.Add(new(a, onBoundary, FieldMode.Value));
        }
        foreach (var a in volume) Add(a, false);
        // A boundary array the volume also holds (E, B) adds nothing a slice or a solid's face does not
        // show; one only the boundary holds (J_s, Q_s) is what the conductor surfaces are for.
        var inVolume = volume.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var a in boundary) if (!inVolume.Contains(a.Name)) Add(a, true);
        return list;
    }

    /// <summary>The value this quantity reads from <paramref name="ch"/> at phase <paramref name="phase"/>
    /// (radians) — the same arithmetic as the shader, in double, for the tooltip and the range.</summary>
    public double Evaluate(ReadOnlySpan<double> ch, double phase = 0)
    {
        int c = Array.Components;
        bool cx = Array.IsComplex;
        double i0 = cx ? ch[c] : 0, i1 = cx && c == 3 ? ch[c + 1] : 0, i2 = cx && c == 3 ? ch[c + 2] : 0;
        double Im(int k) => k == 0 ? i0 : k == 1 ? i1 : i2;
        if (IsVector)
        {
            if (Mode == FieldMode.Instantaneous)
            {
                double co = Math.Cos(phase), si = Math.Sin(phase), s = 0;
                for (int k = 0; k < 3; k++) { double v = ch[k] * co - Im(k) * si; s += v * v; }
                return Math.Sqrt(s);
            }
            double m = 0;
            for (int k = 0; k < 3; k++) m += ch[k] * ch[k] + Im(k) * Im(k);
            return Math.Sqrt(m);
        }
        return Mode switch
        {
            FieldMode.Value => ch[0],
            FieldMode.Instantaneous => ch[0] * Math.Cos(phase) - Im(0) * Math.Sin(phase),
            _ => Math.Sqrt(ch[0] * ch[0] + Im(0) * Im(0)),
        };
    }

    /// <summary>The value that bounds this quantity over a cycle: the peak magnitude (what the range is
    /// taken from, so an animation does not rescale).</summary>
    public double Envelope(ReadOnlySpan<double> ch)
        => Mode == FieldMode.Instantaneous ? (this with { Mode = FieldMode.Peak }).Evaluate(ch) : Math.Abs(Evaluate(ch));
}

/// <summary>Linear or dB, and the range (R-em3d29-3c).</summary>
public sealed record FieldColorScale(bool Db, double Lo, double Hi, double Percentile, bool Signed, string Unit)
{
    /// <summary>The dB span below the top of the range. dB is 20·log10 of an amplitude.</summary>
    public const double DefaultDbSpan = 40;

    /// <summary>
    /// The automatic range of <paramref name="q"/> over the values of <paramref name="surfaces"/>: the top at
    /// the <paramref name="percentile"/>-th percentile of the envelope, the bottom at 0 (linear), the top
    /// less <paramref name="dbSpan"/> (dB), or minus the top (a signed quantity, on a symmetric range).
    /// </summary>
    public static FieldColorScale Auto(FieldQuantity q, IEnumerable<FieldSurface> surfaces, bool db, double percentile,
                                       double dbSpan = DefaultDbSpan)
    {
        var env = new List<double>();
        foreach (var s in surfaces)
            for (int v = 0; v < s.VertexCount; v++)
            {
                double e = q.Envelope(s.Values.AsSpan(v * s.Channels, s.Channels));
                if (double.IsFinite(e)) env.Add(e);
            }
        double top = PercentileOf(env, percentile);
        string unit = FieldNames.Unit(q.Array.Name);
        if (q.Signed) return new(false, -top, top, percentile, true, unit);
        if (!db) return new(false, 0, top, percentile, false, unit);
        double hi = 20 * Math.Log10(Math.Max(top, 1e-300));
        return new(true, hi - dbSpan, hi, percentile, false, unit);
    }

    /// <summary>The <paramref name="p"/>-th percentile (0..100) of <paramref name="values"/>, nearest rank;
    /// 1 when there are none.</summary>
    public static double PercentileOf(List<double> values, double p)
    {
        if (values.Count == 0) return 1;
        values.Sort();
        int i = (int)Math.Ceiling(Math.Clamp(p, 0, 100) / 100 * values.Count) - 1;
        double v = values[Math.Clamp(i, 0, values.Count - 1)];
        return v > 0 ? v : values[^1] > 0 ? values[^1] : 1;
    }

    /// <summary>Where <paramref name="value"/> falls in the range, 0..1, clamped (the end colour beyond).</summary>
    public double Position(double value)
    {
        double v = Db ? 20 * Math.Log10(Math.Max(Math.Abs(value), 1e-300)) : value;
        return Hi > Lo ? Math.Clamp((v - Lo) / (Hi - Lo), 0, 1) : 0;
    }

    /// <summary>The legend's range line: "0 – 2,430 V/m, 99th percentile" / "27.7 – 67.7 dB (re 1 V/m)".</summary>
    public string Describe()
    {
        string pct = Percentile >= 100 ? "maximum" : $"{Percentile.ToString("0.#", CultureInfo.InvariantCulture)}th percentile";
        string u = Unit.Length > 0 ? " " + Unit : "";
        return Db
            ? $"{G(Lo)} to {G(Hi)} dB{(Unit.Length > 0 ? $" re 1 {Unit}" : "")}, top at the {pct}"
            : $"{G(Lo)} to {G(Hi)}{u}, top at the {pct}";
    }

    internal static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}

/// <summary>A field vertex on the GPU: position (scene-local metres), the value's real and imaginary parts.
/// 36 bytes. A scalar uses the first component of each.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FieldVertex
{
    public const int Stride = 36;
    public float X, Y, Z, R0, R1, R2, I0, I1, I2;
}

/// <summary>
/// The field geometry a frame draws: the slice and the surfaces, one vertex buffer, uploaded once per
/// <see cref="Version"/>. A phase step changes a uniform, never this.
/// </summary>
public sealed class Scene3DFieldGeometry(FieldVertex[] vertices, long version)
{
    public FieldVertex[] Vertices { get; } = vertices;
    public long Version { get; } = version;
    public long Bytes => (long)Vertices.Length * FieldVertex.Stride;
    public static readonly Scene3DFieldGeometry None = new([], 0);

    /// <summary>
    /// Packs <paramref name="surfaces"/> for quantity <paramref name="q"/>. Each surface's vertices move by
    /// <paramref name="nudges"/>' vector (scene-local metres): the slice lies ON the clip plane, and is moved
    /// a hair to the kept side so the plane's own discard does not eat it.
    /// </summary>
    public static FieldVertex[] Pack(FieldQuantity q, IReadOnlyList<FieldSurface> surfaces, IReadOnlyList<Vector3> nudges)
    {
        int total = surfaces.Sum(s => s.VertexCount);
        var v = new FieldVertex[total];
        int at = 0, c = q.Array.Components;
        bool cx = q.Array.IsComplex;
        for (int si = 0; si < surfaces.Count; si++)
        {
            var s = surfaces[si];
            var nudge = si < nudges.Count ? nudges[si] : Vector3.Zero;
            for (int i = 0; i < s.VertexCount; i++, at++)
            {
                ref var o = ref v[at];
                o.X = (float)s.Xyz[3 * i] + nudge.X; o.Y = (float)s.Xyz[3 * i + 1] + nudge.Y; o.Z = (float)s.Xyz[3 * i + 2] + nudge.Z;
                var ch = s.Values.AsSpan(i * s.Channels, s.Channels);
                o.R0 = (float)ch[0];
                if (c == 3) { o.R1 = (float)ch[1]; o.R2 = (float)ch[2]; }
                if (cx)
                {
                    o.I0 = (float)ch[c];
                    if (c == 3) { o.I1 = (float)ch[c + 1]; o.I2 = (float)ch[c + 2]; }
                }
            }
        }
        return v;
    }
}

/// <summary>
/// The field uniform block (scene.wgsl <c>F</c>): cos φ, sin φ, range lo, range hi; mode, dB, stop count,
/// pad; sixteen colour-map stops (t, r, g, b). 288 bytes. Written per frame; an animation changes φ only.
/// </summary>
public static class FieldUniforms
{
    public const int Floats = 8 + 4 * MaxStops;
    public const int Bytes = Floats * 4;
    public const int MaxStops = 16;

    public static void Write(Span<float> u, FieldQuantity q, FieldColorScale scale, ColorMap3D map, double phase)
    {
        u.Clear();
        u[0] = (float)Math.Cos(phase); u[1] = (float)Math.Sin(phase);
        u[2] = (float)scale.Lo; u[3] = (float)scale.Hi;
        u[4] = q.ShaderMode; u[5] = scale.Db ? 1 : 0;
        var stops = map.Stops;
        int n = Math.Min(stops.Count, MaxStops);
        u[6] = n;
        for (int i = 0; i < n; i++)
        {
            var (t, r, g, b) = stops[i];
            u[8 + 4 * i] = t; u[9 + 4 * i] = r / 255f; u[10 + 4 * i] = g / 255f; u[11 + 4 * i] = b / 255f;
        }
    }
}
