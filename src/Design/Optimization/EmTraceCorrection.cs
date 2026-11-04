// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-7 D7 — the Tuning panel's optional "Circuit + EM correction" traces. One EM solve at values p gives, for every
// result cube, the offset EM(p) − circuit(p), sample by sample (D2's offset, applied to traces rather than to goals);
// adding it to the live circuit-model result at the values the sliders now hold is an ESTIMATE of what EM would say
// there — exact at p, and as good as the offset is constant away from it. Cubes whose shapes or kinds differ between the
// two runs are passed through uncorrected rather than guessed.

using System.Numerics;
using RfCore.Data;

namespace CircuitRF.Design.Optimization;

public sealed class EmTraceCorrection
{
    private readonly Dictionary<(string Group, string Cube), DataCube> _offsets = [];

    private EmTraceCorrection() { }

    /// <summary>How many cubes carry an offset.</summary>
    public int Count => _offsets.Count;

    /// <summary>The offsets EM − circuit for every cube both results hold with the same shape and kind; null when none.</summary>
    public static EmTraceCorrection? Of(DataSet em, DataSet circuit)
    {
        var c = new EmTraceCorrection();
        foreach (string group in em.Groups)
        {
            if (!circuit.ContainsGroup(group)) continue;
            var theirs = circuit.CubesIn(group);
            foreach (var (name, e) in em.CubesIn(group))
                if (theirs.TryGetValue(name, out var k) && SameShape(e, k) && Difference(e, k) is { } d)
                    c._offsets[(group, name)] = d;
        }
        return c._offsets.Count == 0 ? null : c;
    }

    /// <summary><paramref name="live"/> with each offset added; every other cube as it was. A new set — the live one is
    /// not changed.</summary>
    public DataSet Apply(DataSet live)
    {
        var ds = new DataSet();
        foreach (string group in live.Groups)
            foreach (var (name, cube) in live.CubesIn(group))
                ds.AddToGroup(group, name,
                    _offsets.TryGetValue((group, name), out var d) && SameShape(cube, d) && Sum(cube, d) is { } s ? s : cube);
        return ds;
    }

    private static bool SameShape(DataCube a, DataCube b) =>
        a.DataKind == b.DataKind && a.Axes.Count == b.Axes.Count &&
        a.Axes.Zip(b.Axes).All(p => p.First.Name == p.Second.Name && p.First.Length == p.Second.Length);

    private static DataCube? Difference(DataCube a, DataCube b) => Combine(a, b, -1);

    private static DataCube? Sum(DataCube a, DataCube b) => Combine(a, b, +1);

    private static DataCube? Combine(DataCube a, DataCube b, int sign)
    {
        var axes = a.Axes.ToArray();
        DataCube result;
        if (a.DataKind == DataKind.Complex)
        {
            Complex[] x = a.ComplexValues, y = b.ComplexValues;
            if (x.Length != y.Length) return null;
            result = new DataCube(axes, [.. x.Select((v, i) => v + sign * y[i])]);
        }
        else
        {
            double[] x = a.RealValues, y = b.RealValues;
            if (x.Length != y.Length) return null;
            result = new DataCube(axes, [.. x.Select((v, i) => v + sign * y[i])]);
        }
        result.Unit = a.Unit;
        return result;
    }
}
