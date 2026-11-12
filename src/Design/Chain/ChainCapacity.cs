// What a budget reads as a receiver of data: its input-referred noise, SNR and margin, its Shannon capacity, the NF each
// stage leaves the rest of the chain, and which stage sets its compression.
//
//   NF array  10·log10(F/A), the per-element noise factor F over the array gain A, in linear terms
//               F/A = F_A/M + (F_B − 1)/G_A                equal paths (A = M)
//               F/A = F_A/A + (F_B − 1)/G_A                with a PathIL taper
//             F_A the front end's to the combiner's output, F_B the stages after it, G_A the gain to that output with
//             the array gain included
//   SNR       Pin − MDS ;   margin = SNR − required SNR    the MDS is the noise at one element's input, array gain in
//   rate      S · B · log2(1 + 10^(SNR/10))                Shannon, over S streams; B stated, or FBW · f
//   NFallow   10·log10(1 + F_k·(10^(X/10) − 1)·Gn_k)       after stage k: the largest NF the rest may have for the whole
//                                                          chain's NF to stay within X dB of the NF up to k (F_k and Gn_k
//                                                          the cascade's noise factor and noise gain through k)
//   IP1dBpct  (G_(i−1)/IP1dB_i) / Σ                         stage i's share of the cascade's 1/IP1dB
//   ceiling   IP1dB − RxBackoffDb                          the largest input the receiver takes with that back-off
//
// The combiner convention is the matched one: N in-phase element signals into an N:1 combiner come out N times one
// element's power (10·log10 N), their uncorrelated noise once. A voltage-summing convention counts 20·log10 N of signal
// and 10·log10 N of noise instead; the SNR, and so every readout here, is the same in both, and only the absolute level
// after the combiner differs, by 10·log10 N.

using System.Globalization;
using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>The receiver readouts of one budget.</summary>
/// <param name="NfAllowedDb">After each stage, the largest NF the stages after it may have together, dB; NaN after the
/// last.</param>
/// <param name="Ip1dbSharePct">Each stage's share of the cascade's 1/IP1dB, %; 0 for a linear stage.</param>
public sealed record ChainReadout(double BandwidthHz, double NfArrayDb, double SnrDb, double MarginDb,
                                  int Streams, double RateBps, double CeilingDbm,
                                  IReadOnlyList<double> NfAllowedDb, IReadOnlyList<double> Ip1dbSharePct);

public static class ChainCapacity
{
    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);
    private static string D(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The readouts; null when the budget was refused.</summary>
    public static ChainReadout? Read(ChainBudgetResult r)
    {
        if (r.Totals is not { } t || r.Design is not { } d) return null;
        double rate = d.Streams * d.BandwidthHz * Math.Log2(1 + Lin(t.SnrDb));
        double x = Lin(d.NfAllowanceDb) - 1, gn = 1;
        var allow = new double[r.Stages.Count];
        for (int i = 0; i < r.Stages.Count; i++)
        {
            gn *= Lin(r.Stages[i].Stage.NoiseGainDb);
            // After the last stage there is nothing left to allow an NF to: none, the table's NaN.
            allow[i] = i == r.Stages.Count - 1 ? double.NaN : Db(1 + Lin(r.Stages[i].NfDb) * x * gn);
        }
        return new ChainReadout(d.BandwidthHz, t.NfDb - t.ArrayGainDb, t.SnrDb, t.SnrDb - d.RequiredSnrDb, d.Streams,
                                rate, t.Ip1dbDbm - d.RxBackoffDb, allow, Shares(r.Stages));
    }

    /// <summary>Each stage's share of the cascade's 1/IP1dB, %: G_(i−1)/IP1dB_i over their sum.</summary>
    private static double[] Shares(IReadOnlyList<ChainStageResult> rows)
    {
        var term = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            term[i] = double.IsFinite(rows[i].Stage.Ip1dbDbm) ? Lin((i > 0 ? rows[i - 1].GainDb : 0) - rows[i].Stage.Ip1dbDbm) : 0;
        double sum = term.Sum();
        return [.. term.Select(v => sum > 0 ? 100 * v / sum : 0)];
    }

    /// <summary>
    /// What the budget says about the receiver's range: in an array, that a stage after the combiner sets its compression
    /// (the elements' signals reach it in phase); and an input above the ceiling, IP1dB − RxBackoffDb.
    /// </summary>
    public static IEnumerable<string> Notes(ChainDesign d, IReadOnlyList<ChainStageResult> rows, ChainTotals t)
    {
        var share = Shares(rows);
        int cmb = d.Stages.FindIndex(s => s.Part == ChainParts.Combiner);
        int top = share.Length == 0 || share.Max() <= 0 ? -1 : Array.IndexOf(share, share.Max());
        if (cmb >= 0 && top > cmb)
            yield return $"{rows[top].Name} sets the receiver's compression ({D(share[top])} % of it): it comes after the combiner, " +
                         $"where the {rows[cmb].Stage.ArrayN} elements' signals arrive in phase, so the array's gain drives it before any " +
                         "element's own stage.";
        double ceiling = t.Ip1dbDbm - d.RxBackoffDb;
        if (d.InputPowerDbm > ceiling)
            yield return $"The input, {D(d.InputPowerDbm)} dBm, is above the receiver's ceiling of {D(ceiling)} dBm, its input P1dB " +
                         $"less the {D(d.RxBackoffDb)} dB back-off.";
    }

    /// <summary>The readouts added to a budget's DataSet: two cubes over the stage axis, the rest scalars.</summary>
    public static void AddTo(DataSet ds, ChainBudgetResult r, Axis stage)
    {
        if (Read(r) is not { } o) return;
        static double Fin(double v) => double.IsFinite(v) ? v : double.NaN;
        ds.Add("NFallow_dB", new DataCube([stage], [.. o.NfAllowedDb.Select(Fin)]) { Unit = "dB" });
        ds.Add("IP1dBpct", new DataCube([stage], [.. o.Ip1dbSharePct]) { Unit = "%" });
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [Fin(v)]) { Unit = unit });
        One("BW_Hz", "Hz", o.BandwidthHz);
        One("NFarray_dB", "dB", o.NfArrayDb);
        One("Margin_dB", "dB", o.MarginDb);
        One("Streams", "", o.Streams);
        One("Rate_bps", "bit/s", o.RateBps);
        One("Rate_Gbps", "Gbit/s", o.RateBps / 1e9);
        One("Ceiling_dBm", "dBm", o.CeilingDbm);
    }

    /// <summary>A rate as a person reads it: "71.3 Gb/s", "12.4 Mb/s".</summary>
    public static string Rate(double bps)
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return bps >= 1e9 ? $"{N(bps / 1e9)} Gb/s" : bps >= 1e6 ? $"{N(bps / 1e6)} Mb/s" : bps >= 1e3 ? $"{N(bps / 1e3)} kb/s" : $"{N(bps)} b/s";
    }
}
