// The Link bench's pieces that are not drawing (brief PCH-11; docs/design/propagation-channel.md §9.3): the Propagation Channel a
// chain's link becomes, the number of drops its margins are checked over, the signal it demodulates, and the two drops the
// envelope runs at.
//
//   the channel   PathLoss = the link's model, its distance, heights and registry parameters as the chain states them (the
//                 budget's own values, already read at the chain's frequency), f0 the chain's frequency, TxGain the transmit
//                 element's gain plus 20·log10(Mtx) (Mtx PAs in phase are Mtx² in EIRP, as the budget counts them), RxGain the
//                 receive element's, RxTant the chain's source temperature, the fading the margins cover (block fading: fD = 0,
//                 each drop one realization), Seed 1 and Drop the bench's VAR drop
//   the drops     ≥ 100/(1 − p): about a hundred drops below the margin at the availability p; at least 1,000, at most
//                 100,000 (said when the availability asks for more)
//   the picks     the drop sweep's own median and the drop at its (1 − p) quantile, by received power: each drop's channel
//                 is the one the run elaborates (the same parameters, instance path and draws), evaluated here once per drop,
//                 so the envelope runs read EVM and BER where the budget says the link is typical and where it is about to fail
//
// Nothing here is simulated: H(f0) of each drop is the channel's own closed form, the one its report writes as Gain_dB.

using System.Collections.Concurrent;
using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Design.Chain;

public static class ChainLinkBench
{
    /// <summary>The bench's Propagation Channel, whose report cubes the measurements read as <c>link:CH1:…</c>.</summary>
    public const string Channel = "CH1";

    public const int MinDrops = 1_000, MaxDrops = 100_000;

    /// <summary>The single carrier's roll-off when the link sends one.</summary>
    public const double ScRollOff = 0.2;

    /// <summary>The drops the margins are checked over at an availability of <paramref name="availabilityPct"/>: 100/(1 − p),
    /// held between <see cref="MinDrops"/> and <see cref="MaxDrops"/>.</summary>
    public static int Drops(double availabilityPct) => (int)Math.Clamp(Wanted(availabilityPct), MinDrops, MaxDrops);

    /// <summary>100/(1 − p), rounded up; to 1e-9 first, so 99.9 % is 100,000 and not 100,001.</summary>
    public static double Wanted(double availabilityPct)
    {
        double q = 1 - availabilityPct / 100;
        return q > 0 ? Math.Ceiling(Math.Round(100 / q, 9)) : double.PositiveInfinity;
    }

    private static string G(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The names the chain reads itself (ChainPropagation's), which are not handed to the channel by name.</summary>
    private static readonly HashSet<string> ChainNames = new(StringComparer.OrdinalIgnoreCase)
        { "Distance", "Attenuation", "RxAntennaGain", "Model", "TxHeight", "RxHeight", "Fading", "KFactor", "m", "Sigma", "Margin", "TargetBer", "Modulation" };

    /// <summary>
    /// The channel's parameters as a netlist line's tail, from a budgeted design (<paramref name="d"/> is the budget's, its link
    /// read at the chain's frequency). <paramref name="notes"/> says what the channel takes differently from the budget.
    /// </summary>
    public static string ChannelParameters(ChainDesign d, ChainLinkBudget lb, List<string> notes)
    {
        var link = d.Link!;
        var p = lb.Propagation!;
        var model = ChainPropagation.ModelOf(link)!;
        bool satellite = model.Name == "satellite";
        var w = new List<string> { $"PathLoss={model.Name}" };
        if (!satellite && ChainLink.TryMetres(link.GetPath("Distance"), out double dm)) w.Add($"Distance={G(dm)}");
        foreach (var h in new[] { "TxHeight", "RxHeight" })
            if (link.GetPath(h).Trim().Length > 0 && ChainLink.TryMetres(link.GetPath(h), out double hm)) w.Add($"{h}={G(hm)}");
        w.Add($"f0={G(d.FrequencyHz)}");
        w.Add($"TxGain={G(ChainValue.Or(link.GetTx("AntennaGain"), 0) + 20 * Math.Log10(lb.Mtx))}");
        w.Add($"RxGain={G(ChainValue.Or(link.GetPath("RxAntennaGain"), 0))}");
        w.Add($"RxTant={G(d.TemperatureK)}");
        var m = p.Margins;
        if (m.Fading != "none")
        {
            w.Add($"Fading={m.Fading}");
            w.Add("fD=0");
            if (m.Fading == "rician") w.Add($"KFactor={G(m.KFactorDb)}");
            if (m.Fading == "nakagami") w.Add($"NakagamiM={G(m.M)}");
        }
        bool ownsSigma = model.Parameters.Any(x => x.Name == "Sigma");
        foreach (var rp in ChainPropagation.RegistryParameters(model))
        {
            if (ChainNames.Contains(rp.Name) && !(rp.Name == "Sigma" && ownsSigma)) continue;
            if (!link.Path.TryGetValue(rp.Name, out var v) || v.Trim().Length == 0) continue;
            string t = v.Trim();
            w.Add($"{rp.Name}={(t.Any(char.IsWhiteSpace) ? $"\"{t}\"" : t)}");
        }
        w.Add("Seed=1");
        w.Add("Drop=drop");

        if (!ownsSigma && link.GetPath("Sigma").Trim().Length > 0 && Math.Abs(p.SigmaDb - SigmaOfModel(link, d.FrequencyHz)) > 1e-12)
            notes.Add($"the chain states Sigma = {link.GetPath("Sigma").Trim()} dB, which {model.Name} does not read: the channel draws the " +
                      "model's own shadowing, so the drops leave the stated shadowing out of the outage");
        if (p.LosProbability is not null && link.GetPath("Los").Trim().ToLowerInvariant() is "" or "auto")
            notes.Add("Los=auto: the channel draws the line-of-sight state per drop from the model's probability, where the budget " +
                      "takes the more probable state; the drop sweep's outage includes the draw");
        return string.Join("  ", w);
    }

    /// <summary>The budget's received power at the availability (its received power less the margins taken), dBm, and the
    /// outage it predicts there, 1 − p; null without a link whose propagation evaluates.</summary>
    public static (double ThresholdDbm, double Outage)? Tail(ChainDesign d)
    {
        if (d.Link?.Budget(d.FrequencyHz) is not { Propagation: { } p } lb) return null;
        return (lb.PrxDbm - p.Margins.AppliedDb, Math.Round(1 - p.Margins.AvailabilityPct / 100, 12));
    }

    /// <summary>Whether the link states rain (Rain=fixed or drawn), whose drop-by-drop term the bench then sets beside the budget's.</summary>
    public static bool ReportsRain(ChainDesign d)
        => d.Link?.GetPath("Rain").Trim().ToLowerInvariant() is "fixed" or "drawn";

    /// <summary>The model's own σ at <paramref name="fHz"/>: the link evaluated with no Sigma of its own.</summary>
    private static double SigmaOfModel(ChainLink link, double fHz)
    {
        var probe = link.Clone();
        probe.Path.Remove("Sigma");
        try { return ChainPropagation.Evaluate(probe, fHz).SigmaDb; }
        catch (RfCore.Propagation.PropagationRefusal) { return double.NaN; }
    }

    /// <summary>The signal the bench sends: its MOD_Source carrier, its noise bandwidth (the NR carrier's occupied bandwidth,
    /// or the single carrier's symbol rate) and its occupied bandwidth.</summary>
    public sealed record LinkSignal(string Carrier, double NoiseBandwidthHz, double OccupiedHz, string Name);

    /// <summary>
    /// The Demo bench's NR carrier at the link's modulation, or PCH-5's single carrier on a satellite link and for a modulation NR does
    /// not carry (BPSK, 8PSK): a root-raised-cosine carrier filling the chain's bandwidth, with a preamble and pilot blocks for the
    /// pilot equalizer.
    /// </summary>
    public static LinkSignal Signal(ChainDesign d)
    {
        var link = d.Link!;
        string mod = link.GetPath("Modulation").Trim().ToLowerInvariant() is { Length: > 0 } stated ? stated : ChainPropagation.ChainSignal;
        string upper = mod.ToUpperInvariant();
        bool nr = !link.IsSatellite && mod is "qpsk" or "16qam" or "64qam" or "256qam";
        if (nr)
        {
            var (mhz, bocc) = ChainBenches.Carrier(d.BandwidthHz);
            return new($"NR;BW={mhz}MHz;SCS=15kHz;Mod={upper};Off=0;P=0;Seed=1", bocc, bocc, $"NR {mhz} MHz {upper}");
        }
        double rs = d.BandwidthHz / (1 + ScRollOff);
        string msps = (rs / 1e6).ToString("0.######", CultureInfo.InvariantCulture);
        double rsHz = double.Parse(msps, CultureInfo.InvariantCulture) * 1e6;
        return new($"SC;Rs={msps}Msps;Mod={upper};RollOff={G(ScRollOff)};Preamble=32;PilotBlock=16/200;Data=PN9;Off=0;P=0",
                   rsHz, rsHz * (1 + ScRollOff), $"single carrier {msps} Msps {upper}");
    }

    /// <summary>The two drops the envelope runs at, and the channel gain 20·log10|H(f0)| at each, dB; <paramref name="Varies"/>
    /// false when every drop is the same channel (nothing drawn), and then both are drop 1.</summary>
    public sealed record Picks(long MedianDrop, long OutageDrop, double MedianGainDb, double OutageGainDb, bool Varies);

    /// <summary>The 0-based rank of the outage drop among <paramref name="drops"/>, ascending by received power: (1 − p)·N, rounded
    /// to 1e-9 before the floor, since 1 − 99.9/100 is a hair under 0.001 in binary and would take the rank below.</summary>
    public static int OutageRank(int drops, double availabilityPct)
        => Math.Clamp((int)Math.Floor(Math.Round((1 - availabilityPct / 100) * drops, 9)), 0, drops - 1);

    private static readonly ConcurrentDictionary<string, Picks> PickCache = new(StringComparer.Ordinal);

    /// <summary>
    /// The drop sweep's median drop and its drop at the (1 − p) quantile, by the channel's gain: the channel of
    /// <paramref name="channelParameters"/> elaborated once as the bench elaborates it, then built at each drop and evaluated
    /// at f0. Cached by its arguments, since every listing of a chain's benches builds the Link bench. Throws
    /// <see cref="ParameterRefusalException"/> where the channel refuses.
    /// </summary>
    public static Picks Pick(string channelParameters, int drops, double availabilityPct)
        => PickCache.GetOrAdd($"{channelParameters}|{drops}|{G(availabilityPct)}", _ =>
        {
            string cnl = $"drop = 1\nPropagationChannel:{Channel}  a  b  {channelParameters}\nR:RA  a  0  R=50 Ohm\nR:RB  b  0  R=50 Ohm\n";
            var (lib, tb) = new CnlReader().Read(cnl, "tb", null);
            using var nl = new Elaborator(lib).Elaborate(tb);
            var ec = nl.Components.First(c => c.Model is PropagationChannelModel);
            double f0 = ((PropagationChannelModel)ec.Model).ReferenceHz(1);
            var gain = new double[drops];
            Parallel.For(0, drops, i =>
            {
                var p = new Dictionary<string, Value>(ec.Parameters, StringComparer.Ordinal) { ["Drop"] = new Value(i + 1.0) };
                var ch = new PropagationChannelModel(p, 16.85) { InstancePath = Channel };
                gain[i] = 20 * Math.Log10(ch.H(f0).Magnitude);
            });
            var order = Enumerable.Range(0, drops).OrderBy(i => gain[i]).ThenBy(i => i).ToArray();
            if (gain.Max() - gain.Min() <= 1e-9) return new Picks(1, 1, gain[0], gain[0], false);
            int med = order[drops / 2], outage = order[OutageRank(drops, availabilityPct)];
            return new Picks(med + 1, outage + 1, gain[med], gain[outage], true);
        });
}
