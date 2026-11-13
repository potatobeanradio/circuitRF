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
        // A link delivering more than the receiver takes is held at its ceiling, on the cascade's own input P1dB.
        d = ChainLink.Cap(d, rows, notes);

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
        ChainLink.AddTo(ds, r);
        return ds;
    }

    /// <summary>What a sweep reads at every point, by the name of its cube.</summary>
    public static readonly (string Name, string Unit, Func<ChainBudgetResult, double> Pick)[] SweepQuantities =
    [
        ("EIRP_dBm", "dBm", r => r.Design!.Link?.Budget(r.Design.FrequencyHz).EirpDbm ?? double.NaN),
        ("Prx_dBm", "dBm", r => r.Design!.Link?.Budget(r.Design.FrequencyHz).PrxDbm ?? r.Design.InputPowerDbm),
        ("Pin_dBm", "dBm", r => r.Design!.InputPowerDbm),
        ("Ceiling_dBm", "dBm", r => ChainCapacity.Read(r)!.CeilingDbm),
        ("MDS_dBm", "dBm", r => r.Totals!.MdsDbm),
        ("SNR_dB", "dB", r => r.Totals!.SnrDb),
        ("Margin_dB", "dB", r => ChainCapacity.Read(r)!.MarginDb),
        ("Rate_Gbps", "Gbit/s", r => ChainCapacity.Read(r)!.RateBps / 1e9),
        ("Pdc_W", "W", r => ChainLink.Power(r)!.TotalW),
        ("Ebit_pJ", "pJ", r => ChainLink.Power(r)!.EnergyPerBitJ * 1e12),
        ("Gain_dB", "dB", r => r.Totals!.GainDb),
        ("NFtot_dB", "dB", r => r.Totals!.NfDb),
        ("NFarray_dB", "dB", r => ChainCapacity.Read(r)!.NfArrayDb),
        ("IP1dBtot_dBm", "dBm", r => r.Totals!.Ip1dbDbm),
    ];

    /// <summary>
    /// One sweep of the design (<see cref="ChainSweep"/>): at each point a copy with the carrier, the transmit elements or
    /// the receive Combiner's N set, budgeted again, every expression and table read there. A cube a quantity over the
    /// sweep's axis (<c>freq</c> in Hz, <c>Mtx</c>, <c>Mrx</c>). A point the budget refuses is NaN, and one note gives
    /// how many and the first reason; a receive array of one has no Combiner. Nothing here is a bench: a sweep point is
    /// verified by transferring the design at that point.
    /// </summary>
    public static DataSet Sweep(ChainDesign d, ChainSweep s, List<string>? notes = null)
    {
        var values = s.Values();
        int cmb = d.Stages.FindIndex(st => st.Part == ChainParts.Combiner);
        var points = values.Select(v =>
        {
            var c = d.Clone();
            c.Sweeps.Clear();   // a point is one chain; one of a single element has no Combiner left to size
            switch (s.Of)
            {
                case ChainSweep.Frequency: c.FrequencyHz = v; break;
                case ChainSweep.TxElements: if (c.Link is not null) c.Link.Tx["Elements"] = ChainValue.Format(v); break;
                default:
                    if (cmb < 0) break;
                    if (v < 2) c.Stages.RemoveAt(cmb);
                    else c.Stages[cmb].Parameters["N"] = ChainValue.Format(v);
                    break;
            }
            return Evaluate(c);
        }).ToList();
        var refused = points.Where(p => p.Totals is null).ToList();
        if (refused.Count > 0)
            notes?.Add($"The {s.Of} sweep leaves out {refused.Count} of its {values.Length} points, which the budget refuses; the first: {refused[0].Refusals[0]}");
        int outside = s.Of == ChainSweep.Frequency ? points.Count(p => p.Design is { } pd && OutsideAFilter(pd)) : 0;
        if (outside > 0)
            notes?.Add($"At {outside} of the {s.Of} sweep's carriers a Filter's passband does not hold the stage's frequency: the budget " +
                       "takes its IL there, not its roll-off.");
        var axis = s.Of switch
        {
            ChainSweep.Frequency => new Axis("freq", values, "Hz"),
            ChainSweep.TxElements => new Axis("Mtx", values),
            _ => new Axis("Mrx", values),
        };
        var ds = new DataSet();
        foreach (var (name, unit, pick) in SweepQuantities)
            ds.Add(name, new DataCube([axis], [.. points.Select(p => p.Totals is null ? double.NaN : pick(p) is var v && double.IsFinite(v) ? v : double.NaN)]) { Unit = unit });
        return ds;
    }

    /// <summary>Every sweep of the design added to <paramref name="ds"/>, a group each, named as its sweep is.</summary>
    public static void AddSweeps(DataSet ds, ChainDesign d, List<string>? notes = null)
    {
        foreach (var s in d.Sweeps)
            foreach (var (name, cube) in Sweep(d, s, notes).CubesIn(DataSet.DefaultGroup))
                ds.AddToGroup(s.Of, name, cube);
    }

    /// <summary>True when a resolved design's Filter stage has its stage's frequency outside its passband.</summary>
    private static bool OutsideAFilter(ChainDesign d)
    {
        var hz = d.StageInputFrequencies();
        for (int i = 0; i < d.Stages.Count; i++)
        {
            var s = d.Stages[i];
            if (s.Part != ChainParts.Filter) continue;
            double f = hz[i];
            string form = s.Get("Form").Trim();
            bool inside = form.Equals("Lowpass", StringComparison.OrdinalIgnoreCase) ? f <= ChainValue.Or(s.Get("Fc"), double.PositiveInfinity)
                        : form.Equals("Highpass", StringComparison.OrdinalIgnoreCase) ? f >= ChainValue.Or(s.Get("Fc"), 0)
                        : f >= ChainValue.Or(s.Get("F1"), 0) && f <= ChainValue.Or(s.Get("F2"), double.PositiveInfinity);
            if (!inside) return true;
        }
        return false;
    }
}

/// <summary>
/// A sweep of a chain: <see cref="Of"/> the carrier frequency (<see cref="Points"/> carriers evenly from
/// <see cref="From"/> to <see cref="To"/>, Hz), or the transmit or receive array's elements (as many sizes spread evenly
/// in ratio, each rounded to a whole element, a size once).
/// </summary>
public sealed record ChainSweep(string Of, double From, double To, int Points)
{
    public const string Frequency = "Frequency", TxElements = "TxElements", RxElements = "RxElements";

    /// <summary>The points, in order.</summary>
    public double[] Values()
    {
        if (Points < 2) return [From];
        if (Of == Frequency) return [.. Enumerable.Range(0, Points).Select(i => From + i * (To - From) / (Points - 1))];
        return [.. Enumerable.Range(0, Points).Select(i => Math.Round(From * Math.Pow(To / From, i / (double)(Points - 1)))).Distinct()];
    }

    /// <summary>Why it cannot be run, or null.</summary>
    public string? Refusal(ChainDesign d) =>
        Of is not (Frequency or TxElements or RxElements) ? $"A sweep is of {Frequency}, {TxElements} or {RxElements}, not '{Of}'."
        : !(From > 0 && To > From) || !double.IsFinite(To) ? $"The {Of} sweep runs from a value above 0 to a larger one."
        : Points < 2 ? $"The {Of} sweep needs two points at least."
        : Of != Frequency && (From != Math.Round(From) || To != Math.Round(To)) ? $"The {Of} sweep runs over whole numbers of elements."
        : Of == TxElements && d.Link is null ? "The TxElements sweep sizes the transmitter, and the chain has no link."
        : Of == RxElements && d.Stages.All(s => s.Part != ChainParts.Combiner) ? "The RxElements sweep sizes the receive array, which is the chain's Combiner, and the chain has none."
        : null;
}
