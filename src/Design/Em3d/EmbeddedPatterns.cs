// brief-paa-2 R-paa2-3 / R-paa2-4 — the embedded element patterns of any result, as the array engine reads them (note §2.1, §2.2).
//
// THE TERMINATED EMBEDDED PATTERN of port k is the far field with a unit incident wave at port k and every other port in Z0. It is
// formed through PortFarFieldSet.Weights from the waves of exactly that circuit — a = e_k, b = S·e_k, the k-th column of S — which is
// FieldCosimulation's own path, so there is no second formula: on a terminated (3D) record the weights are √Z0·(1 + S_kk) on port k
// alone and the result is the stored pattern per incident wave; on a planar record (every other gap SHORTED in the stored pattern)
// they are the gaps' voltages under that circuit, each port's calibration box included.
//
// REFERRED TO ITS OWN ELEMENT. A solver writes every port's pattern with the phase of the run's origin, so the pattern taken from port c
// at r_c is multiplied by exp(−j·k·û·r_c) before the array factor uses it. That is done once per (record, port, frequency, position) and
// kept with the record's own arrays (PortFarFieldSet.Referred), which are themselves kept per file.
//
// The elements lie in the layout's plane, so û·r_c is formed from x and y alone.

using System.Numerics;
using CircuitRF.Engine.AntennaArray;

namespace CircuitRF.Design.Em3d;

/// <summary>R-paa2-3 / R-paa2-4 — a result's ports as array elements.</summary>
public static class EmbeddedPatterns
{
    private static long _referrals;

    /// <summary>Patterns referred to a position in this process: a second request for the same one must not add to it.</summary>
    public static long Referrals => Interlocked.Read(ref _referrals);

    /// <summary>The terminated embedded pattern of each 0-based port in <paramref name="ports"/> at <paramref name="hz"/>, per unit incident
    /// wave, with the run's origin.</summary>
    public static IReadOnlyList<ElementPattern> Of(PortFarFieldSet far, IReadOnlyList<int> ports, double hz)
    {
        int i = FrequencyIndex(far, hz);
        return [.. ports.Select(k => Terminated(far, i, k))];
    }

    /// <summary>Port <paramref name="port"/>'s (0-based) terminated embedded pattern, referred to its own element at <paramref name="at"/>
    /// (metres, the record's frame): kept with the record's arrays.</summary>
    public static ElementPattern Referred(PortFarFieldSet far, int port, double hz, (double X, double Y) at)
    {
        int i = FrequencyIndex(far, hz);
        return far.Referred.GetOrAdd((i, port, at.X, at.Y), _ =>
        {
            Interlocked.Increment(ref _referrals);
            var e = Terminated(far, i, port);
            double k = 2 * Math.PI * far.FrequenciesHz[i] / Engine.Mom.EmConstants.C0;
            var grid = far.Grid;
            var et = new Complex[grid.DirectionCount];
            var ep = new Complex[grid.DirectionCount];
            for (int t = 0; t < grid.ThetaDeg.Count; t++)
            {
                double st = Math.Sin(grid.ThetaDeg[t] * Math.PI / 180);
                for (int p = 0; p < grid.PhiDeg.Count; p++)
                {
                    var (sp, cp) = Math.SinCos(grid.PhiDeg[p] * Math.PI / 180);
                    var (s, c) = Math.SinCos(-k * st * (cp * at.X + sp * at.Y));
                    var shift = new Complex(c, s);
                    int d = grid.IndexOf(t, p);
                    et[d] = e.ETheta[d] * shift;
                    ep[d] = e.EPhi[d] * shift;
                }
            }
            return new ElementPattern(grid, et, ep);
        });
    }

    /// <summary>R-paa2-4 — "port k driven, every other port in Z0", through the weights a fields analysis uses.</summary>
    private static ElementPattern Terminated(PortFarFieldSet far, int i, int port)
    {
        int n = far.PortCount;
        if (port < 0 || port >= n) throw new ArgumentOutOfRangeException(nameof(port), port, $"'{far.Name}' has {n} ports.");
        var a = new Complex[n];
        var b = new Complex[n];
        a[port] = 1;
        for (int m = 0; m < n; m++) b[m] = far.S[i][m, port];
        var weights = far.Weights(i, a, b, out _);
        var et = new Complex[far.Grid.DirectionCount];
        var ep = new Complex[far.Grid.DirectionCount];
        for (int m = 0; m < n; m++)
            if (weights[m] != Complex.Zero) far.AddWeighted(i, m, weights[m], et, ep);
        return new ElementPattern(far.Grid, et, ep);
    }

    private static int FrequencyIndex(PortFarFieldSet far, double hz)
        => far.IndexOf(hz) is var i and >= 0
            ? i
            : throw new ArgumentException($"'{far.Name}' holds no far field at {Layout.MaterialDispersion.Hz(hz)}: its patterns are at " +
                                          $"{string.Join(", ", far.FrequenciesHz.Select(Layout.MaterialDispersion.Hz))}.");
}
