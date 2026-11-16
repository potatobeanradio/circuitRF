// ================================================================
//  EnvelopeDropReuse.cs  —  a sweep over a Propagation Channel's drops solves a transmit side once (brief PCH-10 R-pch10-4,
//  design note §8.2)
//
//  The channel is explicit and one way in the envelope: nothing on the transmit side reads the receive side, and the receive
//  side reads only the transmit waves of steps already solved. So when the transmit side does not change from one drop to the
//  next, its waves are the same, and the receive side alone, driven by them, is the joint run's receive side.
//
//  The sides. A side ("island") is PropagationChannelGraph's: nets joined through anything but a channel, a DC supply's net an
//  AC ground. The receive side is every island a channel receives on and every island downstream of one; the transmit side is
//  every other island (a relay, which receives on one channel and transmits on another, is receive side and is solved). A
//  receive side with a nonlinear part refuses the reuse (it would agree only to the solve's tolerance). A part
//  on a transmit island is transmit side; a supply is the side of every part its nets reach, and a supply both sides reach
//  refuses the reuse (its current would be half one run's and half another's).
//
//  The key. The transmit parts' types, paths, nodes and resolved values, the transmit end each channel presents (TxZ,
//  TxPattern, its pin's node), each transmit array's weights after the drop's beam selection (a beam that changes with the drop
//  is a transmit side of its own: solved once per distinct beam, cached by its weights), and the run's own parameters (Stop,
//  Step, Seed, Noise …). Two drops with one key have one transmit side.
//
//  A drop whose key was solved runs the receive side alone: every transmit part removed but the MOD_Sources (the run's signal
//  and time base), every transmit net held by 1 Ω to ground, each channel driven by the recording of the same segment (the run
//  takes the recording drop's segmentation, so it steps where that run stepped) and its harmonic-balance start given the
//  recording's first sample. Its record then takes the transmit side's signals (node voltages, IProbes, supplies, drives) back
//  from the recording drop's record, by name, so every measurement reads what the joint run would have.
// ================================================================

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Engine.Envelope;

public static class EnvelopeDropReuse
{
    /// <summary>What one drop's run records or replays (EnvelopeRunner.Execute's hook).</summary>
    internal sealed class DropRun
    {
        /// <summary>Record every channel's transmit waves, per segment, into <see cref="Recorded"/>, and keep the stitched record.</summary>
        public bool Record;
        /// <summary>Per segment, the recordings this run replays.</summary>
        public IReadOnlyDictionary<string, ChannelRecording>?[]? Drives;
        /// <summary>The replayed run's record with the transmit side's signals taken back from the recording run's.</summary>
        public Func<EnvelopeRunResult, EnvelopeRunResult>? Merge;
        public IReadOnlyDictionary<string, ChannelRecording>?[]? Recorded;
        public EnvelopeRunResult? Merged;
    }

    /// <summary>How one netlist splits: the transmit parts to remove (paths), the transmit MOD_Sources kept (paths), the nets to hold, the
    /// transmit side's node names, supplies, IProbes and wires (for the merge), and which source rows are transmit side.</summary>
    /// <para><c>DrivenChannels</c>: the channels whose transmit end is on the transmit side, which a reusing drop replays; a relay's
    /// channel transmits from the receive side and is solved live.</para>
    public sealed record Partition(IReadOnlySet<string> Remove, IReadOnlySet<string> KeepSources, IReadOnlySet<int> Hold,
                                   IReadOnlySet<string> TxNodeNames, IReadOnlySet<string> TxParts, IReadOnlySet<int> TxSourceRows,
                                   IReadOnlySet<string> DrivenChannels);

    /// <summary>The recordings a reusing drop replays: each segment's, of <see cref="Partition.DrivenChannels"/> only.</summary>
    internal static IReadOnlyDictionary<string, ChannelRecording>?[] Driven(IReadOnlyDictionary<string, ChannelRecording>?[] recordings, Partition part)
        => [.. recordings.Select(r => r is null ? null
               : (IReadOnlyDictionary<string, ChannelRecording>)r.Where(kv => part.DrivenChannels.Contains(kv.Key))
                                                                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal))];

    /// <summary>The counters of the last reusing sweep on this thread (test-facing; the run also says them in a note): transmit sides
    /// solved, drops that replayed one, and distinct transmit array weights among the solved ones.</summary>
    [ThreadStatic] public static (int TransmitSolves, int Reused, int Beams) LastCounters;

    /// <summary>The split of <paramref name="nl"/>, or null and why when a drop cannot reuse a transmit side.</summary>
    public static Partition? Plan(ElaboratedNetlist nl, out string why)
    {
        why = "";
        var chans = nl.Components.Where(c => c.Model is PropagationChannelModel).ToList();
        if (chans.Count == 0) { why = "no Propagation Channel"; return null; }
        if (PropagationChannelGraph.Cycles(nl).Count > 0) { why = "its channels form a cycle"; return null; }
        var island = PropagationChannelGraph.Islands(nl);
        int Isl(int node) => node > 0 && node < island.Length ? island[node] : -1;
        var edges = chans.Select(c => { var (tx, rx) = PropagationChannelGraph.EndNodes(c); return (Tx: Isl(tx), Rx: Isl(rx)); }).ToList();
        var receive = new HashSet<int>(edges.Where(e => e.Rx >= 0).Select(e => e.Rx));
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var e in edges) if (e.Tx >= 0 && e.Rx >= 0 && receive.Contains(e.Tx) && receive.Add(e.Rx)) grew = true;
        }
        var transmit = new HashSet<int>(island.Where(i => i >= 0 && !receive.Contains(i)));
        if (transmit.Count == 0) { why = "every side receives"; return null; }

        bool OnTx(ElaboratedComponent c) => c.Nodes.Append(c.ReferenceNode).Any(n => transmit.Contains(Isl(n)));
        bool OnRx(ElaboratedComponent c) => c.Nodes.Append(c.ReferenceNode).Any(n => receive.Contains(Isl(n)));
        var tx = new HashSet<ElaboratedComponent>(ReferenceEqualityComparer.Instance);
        foreach (var c in nl.Components)
            if (c.Model is not PropagationChannelModel && OnTx(c)) tx.Add(c);
        // a part on no island (a supply, a part between two supply nets): the side of every part its nets reach
        foreach (var c in nl.Components)
        {
            if (c.Model is PropagationChannelModel || tx.Contains(c) || OnRx(c)) continue;
            var nets = c.Nodes.Where(n => n > 0).ToHashSet();
            if (nets.Count == 0) continue;
            var reach = nl.Components.Where(o => !ReferenceEquals(o, c) && o.Model is not PropagationChannelModel && o.Nodes.Any(nets.Contains)).ToList();
            bool t = reach.Any(OnTx), r = reach.Any(OnRx);
            if (t && r) { why = $"'{c.InstancePath}' feeds both sides, so its current would be half one run's"; return null; }
            if (t) tx.Add(c);
        }
        var hold = new HashSet<int>();
        for (int n = 1; n < Math.Min(island.Length, nl.Nodes.Count); n++) if (transmit.Contains(island[n])) hold.Add(n);
        foreach (var c in tx) foreach (int n in c.Nodes) if (n > 0 && Isl(n) < 0) hold.Add(n);
        // a nonlinear receive side refuses: the joint step's Newton and predictor acceptance are judged over every unknown, so the
        // transmit waves the recording drop kept carry its own receive side's iterations. Measured on a 50-drop sweep with an LNA
        // (IP3 −30 dBm) after the channel: V to 2e-5 and I to 2e-4 of their peaks, AM-PM and Delay of a CW not at all. A linear
        // receive side converges in the step's first iteration and leaves nothing behind, which is what makes the reuse exact.
        if (nl.Components.FirstOrDefault(c => c.Model is not PropagationChannelModel && !tx.Contains(c) && c.Model.Kind == CircuitRF.Core.ModelKind.Nonlinear) is { } nonlinear)
        {
            why = $"the receive side is nonlinear ('{nonlinear.InstancePath}'), and the joint solve's steps are judged over both sides";
            return null;
        }
        var sources = nl.Components.Where(c => c.Model is ModSourceModel).ToList();
        var keep = tx.Where(c => c.Model is ModSourceModel).Select(c => c.InstancePath).ToHashSet(StringComparer.Ordinal);
        if (tx.FirstOrDefault(c => c.Model is not ModSourceModel && IsTone(c.Model)) is { } lo)
        {
            // a tone on the transmit side (an upconverter's LO) is what made the joint run two-fundamental; pruned, the replay
            // would find one fundamental, or another lattice, and fail - so the drop is solved jointly instead
            why = $"'{lo.InstancePath}' is a tone on the transmit side";
            return null;
        }
        var driven = chans.Where((c, i) => edges[i].Tx >= 0 && transmit.Contains(edges[i].Tx)).Select(c => c.InstancePath).ToHashSet(StringComparer.Ordinal);
        return new Partition(
            tx.Where(c => c.Model is not ModSourceModel).Select(c => c.InstancePath).ToHashSet(StringComparer.Ordinal), keep, hold,
            hold.Select(n => nl.Nodes.NameOf(n)).ToHashSet(StringComparer.Ordinal), tx.Select(c => c.InstancePath).ToHashSet(StringComparer.Ordinal),
            Enumerable.Range(0, sources.Count).Where(i => keep.Contains(sources[i].InstancePath)).ToHashSet(), driven);
    }

    private static bool IsTone(CircuitRF.Core.ComponentModel m) => m is P1ToneModel or PnToneModel or ToneSourceModelBase;

    /// <summary>The transmit side's key (see the file header). The drop's beam must already be in its arrays.</summary>
    public static string Key(ElaboratedNetlist nl, Partition part, EnvelopeParams p)
    {
        var sb = new StringBuilder();
        sb.Append((p with { ChannelDrive = null, RecordChannelWaves = false }).ToString()).Append('\n');
        foreach (var c in nl.Components.OrderBy(c => c.InstancePath, StringComparer.Ordinal))
        {
            if (part.TxParts.Contains(c.InstancePath))
            {
                sb.Append(c.ComponentType).Append(' ').Append(c.InstancePath).Append(" m=").Append(c.Multiplicity.ToString("R", CultureInfo.InvariantCulture)).Append(" [");
                foreach (int k in c.Nodes) sb.Append(k).Append(',');
                sb.Append("] ");
                foreach (var (k, v) in c.Parameters.OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append(k).Append('=').Append(Text(v)).Append(';');
                if (c.Model is PhasedArrayFeedModel feed) sb.Append(" w=").Append(feed.WeightsKey());
                sb.Append('\n');
            }
            else if (c.Model is PropagationChannelModel ch)
            {
                // the transmit end a channel presents is the transmit side's load
                sb.Append("channel ").Append(c.InstancePath).Append(" pin=").Append(ch.TxPin >= 0 && ch.TxPin < c.Nodes.Length ? c.Nodes[ch.TxPin] : -1);
                foreach (string k in new[] { "TxZ", "TxPattern", "Tx" })
                    if (c.Parameters.TryGetValue(k, out var v)) sb.Append(' ').Append(k).Append('=').Append(Text(v));
                sb.Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The weights of the transmit side's arrays (after the beam selection), for counting distinct beams; empty with none.</summary>
    public static string Weights(ElaboratedNetlist nl, Partition part)
        => string.Join("|", nl.Components.Where(c => part.TxParts.Contains(c.InstancePath) && c.Model is PhasedArrayFeedModel)
                                         .OrderBy(c => c.InstancePath, StringComparer.Ordinal).Select(c => ((PhasedArrayFeedModel)c.Model).WeightsKey()));

    /// <summary><paramref name="nl"/> made the receive side alone: the transmit parts removed (MOD_Sources kept), the transmit nets held.</summary>
    public static void Prune(ElaboratedNetlist nl, Partition part)
    {
        var holds = part.Hold.OrderBy(n => n).Select(n => new ElaboratedComponent("R", $"__dropreuse_hold_{n}", [n, 0],
                        new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(1.0) }, new ResistorModel()) { NoiseOff = true }).ToList();
        nl.Prune(c => part.Remove.Contains(c.InstancePath), holds);
    }

    /// <summary>The replayed run's record with the transmit side's signals — its node voltages, IProbes, supplies, wires and source
    /// drives — taken from the recording run's, by name.</summary>
    public static EnvelopeRunResult Merge(EnvelopeRunResult joint, EnvelopeRunResult driven, Partition part)
    {
        int nt = driven.Time.Length;
        if (joint.Time.Length != nt)
            throw new InvalidOperationException($"envelope drop reuse: the replayed run has {nt} samples and its recording {joint.Time.Length}.");
        T[][] Pick<T>(string[] names, string[] jointNames, string[] drivenNames, T[][] jointRows, T[][] drivenRows, Func<string, bool> tx)
        {
            var dIndex = drivenNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
            var map = names.Select((n, i) => !tx(n) && dIndex.TryGetValue(n, out int d) ? (Joint: false, I: d) : (Joint: true, I: i)).ToArray();
            var rows = new T[nt][];
            for (int t = 0; t < nt; t++)
            {
                rows[t] = new T[map.Length];
                for (int i = 0; i < map.Length; i++) rows[t][i] = map[i].Joint ? jointRows[t][map[i].I] : drivenRows[t][map[i].I];
            }
            return rows;
        }
        bool TxNode(string n) => part.TxNodeNames.Contains(n);
        bool TxPart(string n) => part.TxParts.Contains(n);
        var drive = new Complex[joint.Drive.Length][];
        for (int r = 0; r < drive.Length; r++) drive[r] = part.TxSourceRows.Contains(r) || r >= driven.Drive.Length ? joint.Drive[r] : driven.Drive[r];
        var supplyIndex = driven.SupplyNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        var supplyV = joint.SupplyNames.Select((n, i) => !TxPart(n) && supplyIndex.TryGetValue(n, out int d) ? driven.SupplyVoltage[d] : joint.SupplyVoltage[i]).ToArray();
        var merged = new EnvelopeRunResult
        {
            Time = driven.Time, ProbeNames = joint.ProbeNames,
            Probe = Pick(joint.ProbeNames, joint.ProbeNames, driven.ProbeNames, joint.Probe, driven.Probe, TxNode),
            SupplyNames = joint.SupplyNames,
            SupplyCurrent = Pick(joint.SupplyNames, joint.SupplyNames, driven.SupplyNames, joint.SupplyCurrent, driven.SupplyCurrent, TxPart),
            SupplyVoltage = supplyV,
            IProbeNames = joint.IProbeNames,
            IProbeCurrent = Pick(joint.IProbeNames, joint.IProbeNames, driven.IProbeNames, joint.IProbeCurrent, driven.IProbeCurrent, TxPart),
            IProbeVoltage = Pick(joint.IProbeNames, joint.IProbeNames, driven.IProbeNames, joint.IProbeVoltage, driven.IProbeVoltage, TxPart),
            WBondPaths = joint.WBondPaths,
            WBondCurrent = joint.WBondPaths.Length == 0 ? driven.WBondCurrent
                           : Pick(joint.WBondPaths, joint.WBondPaths, driven.WBondPaths, joint.WBondCurrent, driven.WBondCurrent, TxPart),
            Drive = drive, DriveRe = joint.DriveRe, F0Hz = driven.F0Hz, StepS = driven.StepS, MaxHarm = driven.MaxHarm,
            SignalRateHz = driven.SignalRateHz, Sources = driven.Sources, WallSeconds = driven.WallSeconds, MeanNewton = driven.MeanNewton,
            JacobianBuilds = driven.JacobianBuilds, WorstTangentError = driven.WorstTangentError, SnpFits = driven.SnpFits,
            CarrierMix = driven.CarrierMix, CarrierOffsetHz = driven.CarrierOffsetHz, CarrierLabels = driven.CarrierLabels,
            ToneFreqsHz = driven.ToneFreqsHz, MixLabels = driven.MixLabels, MixFreqHz = driven.MixFreqHz, MixTones = driven.MixTones,
            ChannelLatencyS = driven.ChannelLatencyS, ChannelResponse = driven.ChannelResponse,
        };
        merged.Notes.AddRange(driven.Notes);
        return merged;
    }

    private static string Text(Value v) => v.Kind switch
    {
        ValueKind.Real => "r" + v.AsReal().ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Complex => "c" + v.AsComplex().Real.ToString("R", CultureInfo.InvariantCulture) + "," + v.AsComplex().Imaginary.ToString("R", CultureInfo.InvariantCulture),
        _ => "s" + v,
    };
}
