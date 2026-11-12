// The closed-form cascade of a chain: per stage, the cumulative value at that stage's output, referred to the chain's
// input — and, in an array, to ONE element's input.
//
//   Gain                 G_k = Π g_i                                     (signal gain; a Combiner's g is N/L)
//   Noise (Friis)        Te_k = Σ Te_i / Gn_(i−1),  F = 1 + Te/T0        (noise gain Gn: a Combiner's is 1/L)
//   Intercept            1/IIP3_k = Σ G_(i−1) / IIP3_i ,  OIP3 = IIP3·G  (in-phase IM3 addition, the worst case)
//   Compression          1/IP1dB_k = Σ G_(i−1) / IP1dB_i ,  OP1dB = IP1dB + G − 1 dB   (an approximation, as for IP3)
//   MDS                  10·log10(k·(Ts + Te)·B) + 30 − 10·log10(A)       (A: the array gain, N for equal paths)
//   SNR                  Pin − MDS ;  sensitivity = MDS + SNR_req ;  SFDR = (2/3)(IIP3 − MDS)
//
// The array: N branches whose signals add in phase and whose noise is uncorrelated, so the SNR rises by N while the
// array's noise temperature is one branch's Friis sum. The sources are in docs/design/chain-budget.md.

using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>The cumulative budget at one stage's output.</summary>
public sealed record ChainStageResult(
    string Name, string Part, StageBudget Stage, double FrequencyHz,
    double GainDb, double NfDb, double TeK, double Iip3Dbm, double Oip3Dbm, double Ip1dbDbm, double Op1dbDbm,
    double PoutDbm, double NoiseOutDbm, double SnrDb, double ArrayGainDb, double NoiseSharePct);

/// <summary>The whole chain's numbers.</summary>
public sealed record ChainTotals(
    double GainDb, double NfDb, double TeK, double Iip3Dbm, double Oip3Dbm, double Ip1dbDbm, double Op1dbDbm,
    double PoutDbm, double ThermalFloorDbm, double NoiseOutDbm, double MdsDbm, double SensitivityDbm, double SnrDb,
    double SfdrDb, double ArrayGainDb, double OutputFrequencyHz);

public sealed record ChainBudgetResult(
    IReadOnlyList<ChainStageResult> Stages, ChainTotals? Totals, IReadOnlyList<string> Refusals, IReadOnlyList<string> Notes)
{
    /// <summary>The design as it was budgeted (<see cref="ChainDesign.Resolve"/>): every expression and table its number
    /// at its stage's frequency. The levels, the diagram and the benches read this one. Null when refused.</summary>
    public ChainDesign? Design { get; init; }
}

public static class ChainBudget
{
    /// <summary>Boltzmann's constant, exactly (the noise conventions in simulations.md).</summary>
    public const double K = 1.380649e-23;

    /// <summary>The temperature a noise figure is referred to.</summary>
    public const double T0 = 290;

    private static double Lin(double db) => Math.Pow(10, db / 10);
    private static double Db(double x) => 10 * Math.Log10(x);

    /// <summary>The budget, or the refusals that stop it. Never throws.</summary>
    public static ChainBudgetResult Evaluate(ChainDesign d)
    {
        // Expressions and tables first, each read at its stage's frequency; then the resolved design's own refusals.
        var unread = new List<string>();
        d = d.Resolve(unread);
        if (unread.Count > 0) return new([], null, unread, []);
        var refusals = d.Refusals();
        if (refusals.Count > 0) return new([], null, refusals, []);

        var notes = new List<string>();
        var rows = new List<ChainStageResult>();
        var inputHz = d.StageInputFrequencies();
        var budgets = d.Stages.Select((s, i) => ChainParts.Find(s.Part)!.Budget(s, new ChainStageContext(inputHz[i], d.BaseDirectory))).ToList();
        foreach (var b in budgets) notes.AddRange(b.Notes);
        notes.AddRange(BlockersOutsideFiles(d));

        double gs = 1, gn = 1, te = 0, inv3 = 0, inv1 = 0, f = d.FrequencyHz;
        double a = 1;
        var shares = new double[budgets.Count];
        for (int i = 0; i < budgets.Count; i++)
        {
            var b = budgets[i];
            shares[i] = T0 * (Lin(b.NfDb) - 1) / gn;
            te += shares[i];
            if (!double.IsPositiveInfinity(b.Iip3Dbm)) inv3 += gs / Lin(b.Iip3Dbm);
            if (!double.IsPositiveInfinity(b.Ip1dbDbm)) inv1 += gs / Lin(b.Ip1dbDbm);
            gs *= Lin(b.GainDb);
            gn *= Lin(b.NoiseGainDb);
            a *= Lin(b.GainDb - b.NoiseGainDb);   // the array gain: signal over noise gain, N for equal paths
            if (d.Stages[i].Part == ChainParts.Mixer)
                f = Math.Abs(f - ChainValue.Or(d.Stages[i].Get("Flo"), 0));

            double g = Db(gs);
            double iip3 = inv3 > 0 ? -Db(inv3) : double.PositiveInfinity;
            double ip1 = inv1 > 0 ? -Db(inv1) : double.PositiveInfinity;
            double mds = Db(K * (d.TemperatureK + te) * d.BandwidthHz) + 30 - Db(a);
            rows.Add(new ChainStageResult(
                d.Stages[i].Name, d.Stages[i].Part, b, f,
                g, Db(1 + te / T0), te, iip3, iip3 + g, ip1, ip1 + g - 1,
                d.InputPowerDbm + g, Db(K * (d.TemperatureK + te) * d.BandwidthHz * gn) + 30,
                d.InputPowerDbm - mds, Db(a), 0));
        }

        // Each stage's share of F − 1 (which is what an S-parameter or harmonic-balance noise run's NFpct reports).
        for (int i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { NoiseSharePct = te > 0 ? 100 * shares[i] / te : 0 };

        if (budgets.Any(b => b.ArrayN > 1)
            && d.Stages.Any(s => s.Part == ChainParts.Combiner && ChainValue.Or(s.Get("Steer"), 0) != 0)
            && !d.Stages.TakeWhile(s => s.Part != ChainParts.Combiner).Any(s => s.Part == ChainParts.PhaseShifter))
            notes.Add("The combiner's Steer is not 0 but no PhaseShifter precedes it: the branches arrive out of phase, " +
                      "and the budget's coherent sum assumes they do not.");

        var last = rows[^1];
        double mdsT = Db(K * (d.TemperatureK + te) * d.BandwidthHz) + 30 - Db(a);
        var totals = new ChainTotals(
            last.GainDb, last.NfDb, te, last.Iip3Dbm, last.Oip3Dbm, last.Ip1dbDbm, last.Op1dbDbm,
            last.PoutDbm, Db(K * d.TemperatureK * d.BandwidthHz) + 30, last.NoiseOutDbm, mdsT,
            mdsT + d.RequiredSnrDb, d.InputPowerDbm - mdsT,
            double.IsPositiveInfinity(last.Iip3Dbm) ? double.PositiveInfinity : 2.0 / 3.0 * (last.Iip3Dbm - mdsT),
            Db(a), f);
        notes.AddRange(ChainCapacity.Notes(d, rows, totals));
        return new(rows, totals, [], notes) { Design = d };
    }

    /// <summary>Each blocker that reaches an SnP stage outside its file's range: carried at the file's edge value, as the
    /// block clamps it in a simulation.</summary>
    private static IEnumerable<string> BlockersOutsideFiles(ChainDesign d)
    {
        if (!d.Stages.Any(s => s.Part == ChainParts.Snp)) yield break;
        for (int k = 0; k < d.Blockers.Count; k++)
        {
            double f = d.FrequencyHz + d.Blockers[k].OffsetHz;
            foreach (var s in d.Stages)
            {
                if (s.Part == ChainParts.Snp && ChainSnp.GainDbAt(s, d.BaseDirectory, f, out bool outside) is not null && outside)
                    yield return $"{s.Name}: blocker {k + 1} reaches it at {ChainSnp.Hz(f)}, outside its file's range; its gain " +
                                 "there is taken at the file's nearest frequency, as the SnP block takes it in a simulation.";
                if (s.Part == ChainParts.Mixer) f = Math.Abs(f - ChainValue.Or(s.Get("Flo"), 0));
            }
        }
    }

    /// <summary>
    /// The budget as a DataSet, as every run in circuitRF returns one: per-stage cubes over a <c>stage</c> axis labelled
    /// with the stage names, and the chain's totals as scalars. An infinite intercept is written as NaN, which is how a
    /// cube says "none".
    /// </summary>
    public static DataSet ToDataSet(ChainBudgetResult r)
    {
        var ds = new DataSet();
        if (r.Totals is not { } t) return ds;
        var stage = new Axis("stage", [.. Enumerable.Range(0, r.Stages.Count).Select(i => (double)i)], "",
                             [.. r.Stages.Select(s => s.Name)]);
        static double Fin(double v) => double.IsFinite(v) ? v : double.NaN;
        void Per(string name, string unit, Func<ChainStageResult, double> pick)
            => ds.Add(name, new DataCube([stage], [.. r.Stages.Select(s => Fin(pick(s)))]) { Unit = unit });
        void One(string name, string unit, double v) => ds.Add(name, new DataCube([], [Fin(v)]) { Unit = unit });

        Per("G_dB", "dB", s => s.GainDb);
        Per("NF_dB", "dB", s => s.NfDb);
        Per("Te_K", "K", s => s.TeK);
        Per("IIP3_dBm", "dBm", s => s.Iip3Dbm);
        Per("OIP3_dBm", "dBm", s => s.Oip3Dbm);
        Per("IP1dB_dBm", "dBm", s => s.Ip1dbDbm);
        Per("OP1dB_dBm", "dBm", s => s.Op1dbDbm);
        Per("Pout_dBm", "dBm", s => s.PoutDbm);
        Per("Nout_dBm", "dBm", s => s.NoiseOutDbm);
        Per("SNR_dB", "dB", s => s.SnrDb);
        Per("NFpct", "%", s => s.NoiseSharePct);
        Per("Freq_Hz", "Hz", s => s.FrequencyHz);
        Per("StageGain_dB", "dB", s => s.Stage.GainDb);
        Per("StageNF_dB", "dB", s => s.Stage.NfDb);
        Per("StageIIP3_dBm", "dBm", s => s.Stage.Iip3Dbm);
        if (r.Design is { } d)
        {
            // Each Amp's amplifier stages (NaN where it states none) and each stage's DC power.
            ds.Add("Nstg", new DataCube([stage], [.. d.Stages.Select(s => ChainParts.StageCount(s) is { } n ? n : double.NaN)]));
            ds.Add("Pdc_W", new DataCube([stage], [.. d.Stages.Select(ChainParts.StagePdcW)]) { Unit = "W" });
        }

        One("Gain_dB", "dB", t.GainDb);
        One("NFtot_dB", "dB", t.NfDb);
        One("Tetot_K", "K", t.TeK);
        One("IIP3tot_dBm", "dBm", t.Iip3Dbm);
        One("OIP3tot_dBm", "dBm", t.Oip3Dbm);
        One("IP1dBtot_dBm", "dBm", t.Ip1dbDbm);
        One("OP1dBtot_dBm", "dBm", t.Op1dbDbm);
        One("Pouttot_dBm", "dBm", t.PoutDbm);
        One("ThermalFloor_dBm", "dBm", t.ThermalFloorDbm);
        One("NoiseOut_dBm", "dBm", t.NoiseOutDbm);
        One("MDS_dBm", "dBm", t.MdsDbm);
        One("Sensitivity_dBm", "dBm", t.SensitivityDbm);
        One("SNRtot_dB", "dB", t.SnrDb);
        One("SFDR_dB", "dB", t.SfdrDb);
        One("ArrayGain_dB", "dB", t.ArrayGainDb);
        One("Fout_Hz", "Hz", t.OutputFrequencyHz);
        ChainCapacity.AddTo(ds, r, stage);
        return ds;
    }
}
