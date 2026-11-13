// The chain's levels node by node: at its input and after every stage, the signal, the noise floor in the bandwidth and
// the cumulative NF, the third-order products of two tones at the input power, each blocker carried through every
// stage's gain and every filter's own rejection at the blocker's frequency, a canceller's interference and its residual,
// the margin to compression and the SNR. All from the budget's own cascade (ChainBudget), so a node's numbers are the
// cascade's at that stage's output.
//
//   signal      Pin + G                                  (per tone of the two; per element before a combiner)
//   noise       10·log10(k·(Ts + Te)·B·Gn) + 30          (integrated over the bandwidth)
//   IM3         3·P − 2·OIP3                              (the two tones' third-order products, each)
//   blocker     L + Σ gᵢ(f_b)                             (a filter's |S21| at f_b, an SnP file's |S(Out,In)|, a mixer
//                                                          moving f_b to |f_b − Flo|)
//   margin      OP1dB − max(signal, blockers)            (before anything compresses, the whole chain's IP1dB − Pin)
//   SNR         signal − noise

using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>One node's levels: the chain's input (node 0) or a stage's output.</summary>
public sealed record ChainNodeLevel(string Node, double SignalDbm, double NoiseDbm, double NfDb, double Im3Dbm,
                                    IReadOnlyList<double> BlockerDbm, double InterferenceDbm, double MarginDb, double SnrDb);

public static class ChainLevels
{
    private static double Db(double x) => 10 * Math.Log10(x);

    /// <summary>The levels at the input and after every stage; empty when the budget is refused.</summary>
    public static IReadOnlyList<ChainNodeLevel> Evaluate(ChainDesign d, ChainBudgetResult r)
    {
        if (r.Totals is null) return [];
        d = r.Design ?? d;
        var nodes = new List<ChainNodeLevel>();
        int nb = d.Blockers.Count;
        var blk = d.Blockers.Select(b => b.LevelDbm).ToArray();
        var fb = d.Blockers.Select(b => d.FrequencyHz + b.OffsetHz).ToArray();
        double inNoise = Db(ChainBudget.K * d.TemperatureK * d.BandwidthHz) + 30;
        double interference = double.NaN;
        // Where nothing has compressed yet, the margin is the whole chain's, referred to its input.
        double inMargin = double.IsFinite(r.Totals.Ip1dbDbm) ? r.Totals.Ip1dbDbm - (nb > 0 ? Math.Max(d.InputPowerDbm, blk.Max()) : d.InputPowerDbm) : double.NaN;
        nodes.Add(new ChainNodeLevel("IN", d.InputPowerDbm, inNoise, 0, double.NaN, [.. blk], double.NaN, inMargin,
                                     d.InputPowerDbm - inNoise));
        for (int i = 0; i < r.Stages.Count; i++)
        {
            var st = d.Stages[i];
            var row = r.Stages[i];
            for (int k = 0; k < nb; k++) blk[k] += GainAt(st, row.Stage, ref fb[k], d.BaseDirectory);
            if (st.Part == ChainParts.Canceller)
            {
                // The interference is stated at the canceller's input; after it, only its residual goes on.
                var c = CancellerSpec.Read(st);
                nodes[^1] = nodes[^1] with { InterferenceDbm = c.PintDbm };
                interference = c.ResidualDbm;
            }
            else if (double.IsFinite(interference)) interference += row.Stage.GainDb;
            double strongest = nb > 0 ? Math.Max(row.PoutDbm, blk.Max()) : row.PoutDbm;
            nodes.Add(new ChainNodeLevel(
                st.Name, row.PoutDbm, row.NoiseOutDbm, row.NfDb,
                double.IsFinite(row.Oip3Dbm) ? 3 * row.PoutDbm - 2 * row.Oip3Dbm : double.NaN,
                [.. blk], interference,
                double.IsFinite(row.Op1dbDbm) ? row.Op1dbDbm - strongest : inMargin,
                row.PoutDbm - row.NoiseOutDbm));
        }
        return nodes;
    }

    /// <summary>
    /// A stage's gain at a frequency other than the chain's, dB, and the frequency after it. A filter's is its own
    /// response there (<see cref="FilterNetwork"/>, the model the Filter block simulates with), IL included; a mixer
    /// moves the frequency to |f − Flo|; an SnP stage's is its file's |S(Out,In)| there, at the file's nearest frequency
    /// outside its range (as the block clamps it); every other part is flat, so its gain is the budget's signal gain.
    /// </summary>
    /// <param name="baseDirectory">Where an SnP stage's relative <c>File</c> resolves from.</param>
    public static double GainAt(ChainStage s, StageBudget b, ref double fHz, string? baseDirectory = null)
    {
        double V(string key, double fallback) => ChainValue.Or(s.Get(key), fallback);
        switch (s.Part)
        {
            case ChainParts.Filter:
                var response = Enum.TryParse<FilterResponse>(s.Get("Response").Trim(), true, out var rsp) ? rsp : FilterResponse.Chebyshev;
                var form = Enum.TryParse<NetworkForm>(s.Get("Form").Trim(), true, out var fm) ? fm : NetworkForm.Bandpass;
                var net = FilterNetwork.Create(response, form, (int)Math.Round(V("Order", 3)), V("Fc", 1e9), V("F1", 0.9e9),
                                               V("F2", 1.1e9), V("Ripple", 0.1), V("Astop", 40), V("IL", 0));
                double s21 = net.At(2 * Math.PI * fHz).S21.Magnitude;
                return s21 > 0 ? 20 * Math.Log10(s21) : -300;
            case ChainParts.Mixer:
                fHz = Math.Abs(fHz - V("Flo", 0));
                return b.GainDb;
            case ChainParts.Snp:
                return ChainSnp.GainDbAt(s, baseDirectory, fHz, out _) is { } g ? g : b.GainDb;
            default:
                return b.GainDb;
        }
    }

    /// <summary>
    /// The levels as a DataSet: cubes over a <c>node</c> axis, 0 the input and k the output of stage k, labelled with
    /// "IN" and the stage names. A value that does not apply (an IM3 before any nonlinearity, a margin where nothing
    /// compresses, the interference before a canceller) is NaN.
    /// </summary>
    public static DataSet ToDataSet(IReadOnlyList<ChainNodeLevel> nodes)
    {
        var ds = new DataSet();
        if (nodes.Count == 0) return ds;
        var axis = new Axis("node", [.. Enumerable.Range(0, nodes.Count).Select(i => (double)i)], "", [.. nodes.Select(n => n.Node)]);
        static double Fin(double v) => double.IsFinite(v) ? v : double.NaN;
        void Per(string name, string unit, Func<ChainNodeLevel, double> pick)
            => ds.Add(name, new DataCube([axis], [.. nodes.Select(n => Fin(pick(n)))]) { Unit = unit });
        Per("Psig_dBm", "dBm", n => n.SignalDbm);
        Per("Noise_dBm", "dBm", n => n.NoiseDbm);
        Per("NF_dB", "dB", n => n.NfDb);
        Per("IM3_dBm", "dBm", n => n.Im3Dbm);
        for (int k = 0; k < nodes[0].BlockerDbm.Count; k++)
        {
            int kk = k;
            Per($"Blk{k + 1}_dBm", "dBm", n => n.BlockerDbm[kk]);
        }
        if (nodes.Any(n => double.IsFinite(n.InterferenceDbm))) Per("Int_dBm", "dBm", n => n.InterferenceDbm);
        Per("Margin_dB", "dB", n => n.MarginDb);
        Per("SNR_dB", "dB", n => n.SnrDb);
        return ds;
    }
}
