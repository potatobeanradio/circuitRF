using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using CircuitRF.Diagnostics;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Coinciding mixing products (issue 65). With tones 100 and 300 MHz at MaxMixOrder 5, distinct products land on one
/// frequency: <c>(3,0)</c> and <c>(0,1)</c> are both 300 MHz. Two things follow, handled here.
///
/// <para><b>A source drives only its own product</b> (<see cref="SourceDrivenProducts"/>). Each product's Norton source
/// was extracted at the product's frequency, so the 300 MHz source drove <c>(3,0)</c> as well as <c>(0,1)</c>: the devices
/// saw more tones than the circuit has, and the lattice and the FFT path disagreed by tens of percent on IM3. The
/// products themselves stay independent unknowns, as designed: closely spaced tones (2.00/2.01/2.02 GHz) are separate
/// sources, each on its own phase axis.</para>
///
/// <para><b>Tones that are low harmonics of one fundamental run as single-tone HB on it.</b> Such a set is one
/// phase-locked periodic signal, whose coinciding products add, which independent unknowns cannot say; single-tone HB on
/// the greatest common divisor is exact for it. Its spectra are read back onto the mixing indices the analysis asked for,
/// so every cube, measurement and display sees a multi-tone run's shape, and the run is always announced.</para>
/// </summary>
public sealed partial class HbEngine
{
    /// <summary>Two retained products within this of each other are one frequency (the lattice's own on-grid tolerance).</summary>
    private const double CoincidenceHz = 1.0;

    /// <summary>The multi-tone band ruler (mean of the tones) and default drive (first tone) the single-tone run of a
    /// commensurate set must use; set only while <see cref="RunCommensurate"/>'s run is in progress.</summary>
    private (double Fc, double DefaultDriveHz)? _commensurateRuler;

    /// <summary>The interface seeds commensurate runs hand back. Such a seed is over the single-tone run's harmonics, so a
    /// sweep may chain it into the next commensurate point, and a lattice point must not read it as mixing products even
    /// when its length happens to equal M. A sweep re-elaborates (and so makes a new engine) per point, hence static.</summary>
    private static readonly ConditionalWeakTable<Complex[,], object> CommensurateSeeds = new();

    private static bool IsCommensurateSeed(Complex[,] seed) => CommensurateSeeds.TryGetValue(seed, out _);

    /// <summary>
    /// Which retained products a source drives: the lowest-order product at each frequency, and no other (issue 65).
    /// Products are kept as independent unknowns even where their frequencies coincide (each tone owns its own phase
    /// axis, the treatment for sources that are not phase-locked), but a source at 300 MHz was extracted at every product
    /// landing there, so with tones 100 and 300 MHz it drove (3,0) as well as (0,1), and a bias source drove every
    /// product on 0 Hz as well as DC: the devices saw more tones than there were. Index order is ascending total order on
    /// both the grid and the lattice, so the first index at a frequency is its lowest-order product.
    /// </summary>
    internal static bool[] SourceDrivenProducts(int count, Func<int, double> hz)
    {
        var drives = new bool[count];
        var seen = new List<double>();
        for (int m = 0; m < count; m++)
        {
            double f = Math.Abs(hz(m));
            if (seen.Any(x => Math.Abs(x - f) <= CoincidenceHz)) continue;
            seen.Add(f);
            drives[m] = true;
        }
        return drives;
    }

    /// <summary>The most harmonics the common-fundamental run is allowed: beyond it the tones are related only through a
    /// fundamental far below them, which a single-tone run cannot afford, and the run is refused instead.</summary>
    internal const int MaxCommensurateHarmonic = 1000;

    /// <summary>
    /// The common fundamental when <paramref name="p"/>'s retained products coincide in frequency, with the harmonic
    /// the highest one reaches and the first coinciding pair; null for an ordinary multi-tone run.
    /// </summary>
    internal static (double F0, int K, MixingLattice Lattice, int A, int B)? Commensurate(HbAnalysisParams p, int maxProducts)
    {
        double[] f = p.ToneFreqsHz;
        if (f.Length < 2 || MixingLattice.CountFor(f.Length, p.MaxMixOrder) > maxProducts) return null;

        var lattice = new MixingLattice(f.Length, p.MaxMixOrder);
        var order = Enumerable.Range(0, lattice.MixCount)
                              .Select(m => (M: m, Hz: Math.Abs(lattice.FrequencyOf(m, f))))
                              .OrderBy(x => x.Hz).ToArray();
        // The pair the warning names: one off DC when there is one ((3,0) and (0,1) both 300 MHz says more than a
        // difference product landing on 0 Hz), else the first.
        int a = -1, b = -1;
        for (int i = 1; i < order.Length; i++)
            if (order[i].Hz - order[i - 1].Hz <= CoincidenceHz && (a < 0 || order[i].Hz > CoincidenceHz))
            {
                (a, b) = (order[i - 1].M, order[i].M);
                if (order[i].Hz > CoincidenceHz) break;
            }
        if (a < 0) return null;

        double f0 = f[0];
        for (int t = 1; t < f.Length; t++) f0 = Gcd(f0, f[t]);
        // Only tones that are low harmonics of one fundamental (each within the analysis's own MaxHarm of it: 100 and
        // 300 MHz, an LO at twice the RF) are one phase-locked periodic signal. Closely spaced tones coincide too
        // (2.00/2.01/2.02 GHz on a 10 MHz grid) but are independent sources, which the multi-tone solve models on
        // separate phase axes; they stay there.
        if (f0 < CoincidenceHz || f.Any(x => Math.Round(x / f0) > p.MaxHarmonic)) return null;
        int k = (int)Math.Round(order[^1].Hz / f0);
        return (f0, k, lattice, a, b);

        static double Gcd(double x, double y)
        {
            x = Math.Abs(x); y = Math.Abs(y);
            while (y > CoincidenceHz) (x, y) = (y, x % y);
            return x;
        }
    }

    private HbRunResult RunCommensurate(HbAnalysisParams p, (double F0, int K, MixingLattice Lattice, int A, int B) c,
                                        Complex[,]? warmStart = null)
    {
        var (f0, k, lattice, a, b) = c;
        double[] f = p.ToneFreqsHz;
        if (k > MaxCommensurateHarmonic)   // Commensurate() already guarantees f0 >= CoincidenceHz
            throw new InvalidOperationException(HbCommensurateDiagnostics.NoCommonFundamental(
                f, p.MaxMixOrder, lattice.Label(a), lattice.Label(b), lattice.FrequencyOf(a, f), f0, k,
                f.Max(x => (int)Math.Round(x / f0))).Render());

        bool dropped = p.HasSsSweep || p.Noise is not null;
        _netlist.AddWarningOnce($"hb-commensurate:{string.Join(",", f.Select(x => x.ToString("R", CultureInfo.InvariantCulture)))}:{p.MaxMixOrder}",
            HbCommensurateDiagnostics.RanOnCommonFundamental(
                f, p.MaxMixOrder, lattice.Label(a), lattice.Label(b), Math.Abs(lattice.FrequencyOf(a, f)), f0, k, dropped).Render());

        var single = p with { ToneFreqsHz = [f0], MaxHarmonic = k, GuardHarmonic = 0, SsFreqsHz = null, Noise = null };
        // The terminations and drives are the multi-tone run's: a source's Z[k] bands are measured from the mean of the
        // tones, and a P1Tone with no Freq drives at the first tone, exactly as on the lattice. Ruling the bands from
        // the common fundamental instead put the 300 MHz tone of 100/300 MHz in band 3 where the lattice has band 2,
        // so its source impedance jumped between 300 and 300.001 MHz.
        _commensurateRuler = (f.Average(), f[0]);
        HbRunResult run;
        // Continuation across a sweep: only the previous commensurate point's own seed, whose shape the single-tone
        // path checks against this point's harmonic count.
        try { run = Run(single, warmStart is not null && IsCommensurateSeed(warmStart) ? warmStart : null); }
        finally { _commensurateRuler = null; }
        if (run.InterfaceV is { } seed) CommensurateSeeds.AddOrUpdate(seed, seed);
        return new HbRunResult(OntoMixingIndices(run.DataSet, lattice, f, f0, k), converged: run.Converged,
                               interfaceV: run.InterfaceV, trace: run.Trace);
    }

    /// <summary>A single-tone result at <paramref name="f0"/> read onto <paramref name="lattice"/>'s mixing indices: every
    /// cube's <c>harmonic</c> axis becomes <c>mixIndex</c>, a product taking the harmonic its frequency is (conjugated for a
    /// negative-frequency representative, as the multi-tone path stores one); the tone and order cubes are the multi-tone
    /// run's, and the probes' fundamental power sums the carriers' products as the multi-tone path does.</summary>
    internal static DataSet OntoMixingIndices(DataSet single, MixingLattice lattice, double[] f, double f0, int k)
    {
        int M = lattice.MixCount;
        var hz = Enumerable.Range(0, M).Select(m => lattice.FrequencyOf(m, f)).ToArray();
        var mixAxis = new Axis("mixIndex", hz, "Hz", [.. Enumerable.Range(0, M).Select(lattice.Label)]);
        var harmonic = hz.Select(x => (int)Math.Round(Math.Abs(x) / f0)).ToArray();

        var ds = new DataSet();
        foreach (string group in single.Groups)
            foreach (var (name, cube) in single.CubesIn(group))
            {
                var mapped = name switch
                {
                    "ToneFreqs" => new DataCube([new Axis("tone", [.. Enumerable.Range(1, f.Length).Select(t => (double)t)], "")],
                                                (double[])f.Clone()),
                    _ => Remap(cube, mixAxis, harmonic, hz, k),
                };
                if (group.Length == 0) ds.Add(name, mapped); else ds.AddToGroup(group, name, mapped);
            }
        ds.Add("MetaMixOrder", new DataCube([new Axis("order", [1.0], "")], [lattice.MaxMixOrder]));

        if (ds.Cubes.TryGetValue("Pprobe_W", out var pk) && ds.Cubes.ContainsKey("Pprobe_fund_W"))
        {
            var carriers = CarrierProducts(lattice);
            int nb = pk.Axes[0].Length;
            var pf = new double[nb];
            for (int br = 0; br < nb; br++) foreach (int m in carriers) pf[br] += pk.RealValues[br * M + m];
            ds.Add("Pprobe_fund_W", new DataCube([pk.Axes[0]], pf));
        }
        return ds;
    }

    private static DataCube Remap(DataCube cube, Axis mixAxis, int[] harmonic, double[] hz, int k)
    {
        int ax = -1;
        for (int i = 0; i < cube.Axes.Count; i++) if (cube.Axes[i].Name == "harmonic") { ax = i; break; }
        if (ax < 0) return cube;

        int outer = 1, inner = 1, nh = cube.Axes[ax].Length, M = mixAxis.Length;
        for (int i = 0; i < ax; i++) outer *= cube.Axes[i].Length;
        for (int i = ax + 1; i < cube.Axes.Count; i++) inner *= cube.Axes[i].Length;
        var axes = cube.Axes.ToArray();
        axes[ax] = mixAxis;

        DataCube result;
        if (cube.DataKind == DataKind.Complex)
        {
            var src = cube.ComplexValues;
            var dst = new Complex[outer * M * inner];
            for (int o = 0; o < outer; o++)
                for (int m = 0; m < M; m++)
                {
                    int h = harmonic[m];
                    if (h >= nh) continue;
                    for (int i = 0; i < inner; i++)
                    {
                        var v = src[(o * nh + h) * inner + i];
                        dst[(o * M + m) * inner + i] = hz[m] < 0 ? Complex.Conjugate(v) : v;
                    }
                }
            result = new DataCube(axes, dst);
        }
        else
        {
            var src = cube.RealValues;
            var dst = new double[outer * M * inner];
            for (int o = 0; o < outer; o++)
                for (int m = 0; m < M; m++)
                {
                    int h = harmonic[m];
                    if (h >= nh) continue;
                    for (int i = 0; i < inner; i++) dst[(o * M + m) * inner + i] = src[(o * nh + h) * inner + i];
                }
            result = new DataCube(axes, dst);
        }
        result.Unit = cube.Unit;
        return result;
    }
}

/// <summary>Issue 65: a commensurate tone set's messages, as coded diagnostics.</summary>
public static class HbCommensurateDiagnostics
{
    private static string Mhz(double hz) => (hz / 1e6).ToString("0.######", CultureInfo.InvariantCulture);
    private static string Tones(double[] f) => string.Join(", ", f.Select(x => Mhz(x) + " MHz"));

    /// <summary>The run was made as single-tone HB on the tones' common fundamental.</summary>
    public static Diagnostic RanOnCommonFundamental(double[] tones, int order, string a, string b, double sharedHz, double f0,
                                                    int k, bool dropped) => Diagnostic.Create(
        "hb.tones.commensurate", DiagnosticSeverity.Warning,
        "HB: the tones {tones} are harmonics of one fundamental, {f0Mhz} MHz, so at MaxMixOrder {order} their products " +
        "coincide ({a} and {b} are both {sharedMhz} MHz) and add as one. It ran as single-tone harmonic balance on " +
        "{f0Mhz} MHz to harmonic {k}, which is exact for these tones, and its spectra are read onto the mixing indices: " +
        "products on one frequency read that frequency's one value.{dropped}",
        ("tones", Tones(tones)), ("order", order), ("a", a), ("b", b), ("sharedMhz", Mhz(sharedHz)), ("f0Mhz", Mhz(f0)),
        ("k", k), ("dropped", dropped ? " The small-signal sweep and noise were not run." : ""));

    /// <summary>The tones coincide but share no fundamental a single-tone run can reach.</summary>
    public static Diagnostic NoCommonFundamental(double[] tones, int order, string a, string b, double sharedHz, double f0,
                                                 int k, int highestTone) => Diagnostic.Create(
        "hb.tones.no-common-fundamental", DiagnosticSeverity.Error,
        "HB: the tones {tones} are harmonics of one fundamental, {f0Mhz} MHz, so at MaxMixOrder {order} their products " +
        "coincide ({a} and {b} are both {sharedMhz} MHz), and a single-tone run on it would need harmonic {k} (at most " +
        "{max}). Lower MaxMixOrder, or set MaxHarm below {highest} to solve the tones as independent on the mixing lattice.",
        ("tones", Tones(tones)), ("order", order), ("a", a), ("b", b), ("sharedMhz", Mhz(sharedHz)), ("f0Mhz", Mhz(f0)),
        ("k", k), ("max", HbEngine.MaxCommensurateHarmonic), ("highest", highestTone));
}
