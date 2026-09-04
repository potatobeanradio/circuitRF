using System.Numerics;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Reads a compact model's own operating-point variables back at a converged harmonic-balance
/// point, and puts each one on the analysis' own spectral axis.
///
/// <para><b>At a large-signal point an op-var is a waveform, not a number.</b> A transconductance
/// at 30 dBm of drive is a different quantity at every instant of the RF cycle: it swings between
/// pinch-off and full channel once per period, and no single number describes it. So the value is
/// captured PER TIME SAMPLE and then transformed exactly the way <c>V</c> and <c>INl</c> already
/// are — a spectrum on the harmonic or mixing axis, whose k=0 entry is the cycle average and whose
/// higher entries say how hard the quantity is being swung. Reporting one scalar instead would mean
/// publishing whichever sample the Newton loop happened to evaluate last, which looks right.</para>
///
/// <para><b>Why this is a second evaluation and not a hook in the Newton loop.</b> The Newton pass
/// does not know which iteration is its last, so capturing there would mean asking the provider for
/// op-vars on EVERY iteration and throwing all but one set away — the cost paid per iteration
/// rather than per point. One deliberate pass at the converged voltages is one round trip per
/// external device per HB point, and it is unambiguously at the answer.</para>
///
/// <para><b>Only external devices are asked, and only those that declare op-vars and were not
/// switched off on the instance.</b> A circuit with none does no work here and its DataSet gains no
/// cubes at all.</para>
/// </summary>
public static class HbOpVars
{
    /// <summary>
    /// The external devices in this netlist that will report operating-point variables. Empty is the
    /// ordinary case, and the callers below all return immediately on it — so a design with no
    /// compiled model pays nothing for this feature existing.
    /// </summary>
    private static List<(ElaboratedComponent Ec, ExternalDeviceModel Ed)> Reporting(ElaboratedNetlist netlist)
    {
        var found = new List<(ElaboratedComponent, ExternalDeviceModel)>();
        foreach (int nlIdx in netlist.NonlinearComponents)
        {
            var ec = netlist.Components[nlIdx];
            if (ec.Model is ExternalDeviceModel ed && ed.ReportsOperatingPoint)
                found.Add((ec, ed));
        }
        return found;
    }

    /// <summary>Whether any device in this netlist would report one. Cheap; no provider is contacted.</summary>
    public static bool Any(ElaboratedNetlist netlist) => Reporting(netlist).Count > 0;

    /// <summary>
    /// The shared core. <paramref name="nodeSample"/> hands back interface node <c>n</c>'s voltage
    /// at sample <c>t</c>; <paramref name="analyze"/> turns one real sample series into the spectrum
    /// this analysis publishes on. Everything tone-count-specific lives in those two delegates.
    /// </summary>
    private static Dictionary<string, Complex[]> Collect(
        ElaboratedNetlist          netlist,
        int[]                      interfaceNodes,
        int                        sampleCount,
        Func<int, int, double>     nodeSample,
        Func<double[], Complex[]>  analyze)
    {
        var result = new Dictionary<string, Complex[]>(StringComparer.Ordinal);

        foreach (var (ec, ed) in Reporting(netlist))
        {
            int portCount = ed.PortCount;

            // Same port→node mapping every other device pass uses: port p spans Nodes[2p] and
            // Nodes[2p+1]. An external device's ports are one per node against ground, so the minus
            // side is normally the reference — but it is read rather than assumed, because that
            // layout is the elaborator's and this code should not depend on it a second time.
            var plusIdx  = new int[portCount];
            var minusIdx = new int[portCount];
            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
                int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;
                plusIdx [p] = Array.IndexOf(interfaceNodes, np);
                minusIdx[p] = Array.IndexOf(interfaceNodes, nm);
            }

            var points = new double[sampleCount][];
            for (int t = 0; t < sampleCount; t++)
            {
                var v = new double[portCount];
                for (int p = 0; p < portCount; p++)
                {
                    double vp = plusIdx [p] >= 0 ? nodeSample(plusIdx [p], t) : 0.0;
                    double vm = minusIdx[p] >= 0 ? nodeSample(minusIdx[p], t) : 0.0;
                    v[p] = vp - vm;
                }
                points[t] = v;
            }

            ExternalOperatingPoint? op;
            try
            {
                op = ed.ReadOperatingPointOver(points);
            }
            catch (ExternalDeviceException)
            {
                // A read-back is a diagnostic, never the answer. A provider that will not report one
                // must not turn a converged large-signal point into a failed run.
                continue;
            }
            if (op is null || op.Names.Count == 0) continue;

            var series = new double[sampleCount];
            for (int j = 0; j < op.Names.Count; j++)
            {
                for (int t = 0; t < sampleCount; t++)
                    series[t] = t < op.Values.Count && j < op.Values[t].Length ? op.Values[t][j] : 0.0;

                result[$"{ec.InstancePath}.{op.Names[j]}"] = analyze(series);
            }
        }

        return result;
    }

    // ── Single tone ───────────────────────────────────────────────────────────

    /// <summary>
    /// Op-var spectra over one tone's harmonic axis, keyed
    /// <c>"&lt;InstancePath&gt;.&lt;opVarName&gt;"</c> → <c>Complex[K+1]</c>.
    /// </summary>
    public static Dictionary<string, Complex[]> CollectSingleTone(
        Complex[,] V, int N, int K, int gridN, ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        if (!Any(netlist)) return [];

        // The converged voltages on the time grid. Built here rather than borrowed from the port
        // pass, which may legitimately have skipped building them (it prefers the buffer the last
        // Newton pass filled), and an IFFT per interface node is small beside a provider round trip.
        var vTime = new double[N][];
        for (int n = 0; n < N; n++)
        {
            vTime[n] = new double[gridN];
            var Xn = new Complex[K + 1];
            for (int k = 0; k <= K; k++) Xn[k] = V[n, k];
            HbFft.Inverse(Xn, K, vTime[n]);
        }

        return Collect(netlist, interfaceNodes, gridN,
            (n, t) => vTime[n][t],
            series =>
            {
                HbFft.Forward(series, K, out var spec, out _);
                return spec;
            });
    }

    // ── Two tones ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Op-var spectra over the two-tone mixing lattice, keyed the same way →
    /// <c>Complex[MixCount]</c>.
    /// </summary>
    public static Dictionary<string, Complex[]> CollectTwoTone(
        Complex[,] V, MixingGrid grid, int N, int N1, int N2,
        ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        if (!Any(netlist)) return [];

        int M = grid.MixCount;

        var vTime = new double[N][,];
        var diamond = new Complex[M];
        for (int n = 0; n < N; n++)
        {
            for (int m = 0; m < M; m++)
                diamond[m] = m == 0 ? new Complex(V[n, 0].Real, 0) : V[n, m];
            vTime[n] = new double[N1, N2];
            HbFft2D.Inverse2D(grid, diamond, N1, N2, vTime[n]);
        }

        // The lattice's samples are a rectangle; the shared core walks a flat index, so the two
        // indices are recovered the same way on the way in and on the way out.
        var square = new double[N1, N2];
        return Collect(netlist, interfaceNodes, N1 * N2,
            (n, t) => vTime[n][t / N2, t % N2],
            series =>
            {
                for (int t = 0; t < series.Length; t++) square[t / N2, t % N2] = series[t];
                var spec  = HbNewton2D.ForwardConv2D(square, N1, N2);
                var ampl  = new Complex[M];
                for (int m = 0; m < M; m++)
                {
                    var (k1, k2) = grid.ToneOf(m);
                    ampl[m] = HbFft2D.SpecGet(spec, k1, k2);
                }
                return ampl;
            });
    }

    // ── Three or more tones ───────────────────────────────────────────────────

    /// <summary>
    /// Op-var spectra over a T-tone APFT lattice, keyed the same way → <c>Complex[MixCount]</c>.
    /// </summary>
    public static Dictionary<string, Complex[]> CollectMultiTone(
        Complex[,] V, HbApft apft, int N, ElaboratedNetlist netlist, int[] interfaceNodes)
    {
        if (!Any(netlist)) return [];

        int M = apft.MixCount;
        int S = apft.SampleCount;

        var vTime = new double[N][];
        var diamond = new Complex[M];
        for (int n = 0; n < N; n++)
        {
            for (int m = 0; m < M; m++)
                diamond[m] = m == 0 ? new Complex(V[n, 0].Real, 0) : V[n, m];
            vTime[n] = new double[S];
            apft.Synthesize(diamond, vTime[n]);
        }

        return Collect(netlist, interfaceNodes, S,
            (n, t) => vTime[n][t],
            series =>
            {
                var ampl = new Complex[M];
                apft.Analyze(series, ampl);
                return ampl;
            });
    }
}
