// brief-pch-9 — picking the beam (docs/design/propagation-channel.md §6).
//
// CANDIDATES (R-pch9-1). Per steered array end a codebook of directions: by default a (u, v) grid at one beamwidth — the DFT spacing
// Δu = λ/(Cols·dx), Δv = λ/(Rows·dy), the angle between one beam's peak and its first null — within ScanLimit of boresight, or the rows of
// Codebook=. Each entry is weighed through the array's own steer() (ArraySteering.Weigh: the elaboration's FeedWeights with θ0, φ0 set),
// PhaseBits and AttenStep included, so a candidate is exactly a beam the hardware can make. A pin end, or an array end that is not steered,
// is one fixed entry.
//
// SCORE (R-pch9-2). Note eq. (2), the wideband effective channel through both beams at the drop's channel at TimeOffset,
//
//     H(f) = Σ_r s_r·e^(−j2πf·τ_r) · g_rx(Ω_A,r)ᵀ · M_r · g_tx(Ω_D,r),     g(Ω) = E_c(Ω)·AF(Ω)/√N
//
// with s_r the ray's amplitude as the channel stamps it (large scale, ray gain, its fading and Doppler phase at TimeOffset), E_c the end's
// centre-element realized-gain field (the channel's own reference field), AF the array factor of the entry's weights by ArrayPattern.FactorOnce
// over the rays' directions in the end's frame, and 1/√N the beamformer's split. A pin end's g is its own field. The metric is the mean of
// |H|² over N_f frequency samples across the signal's band (one, at the carrier, outside an envelope run): what the linear pattern model says
// reaches feed from feed. Per pair it costs K·N_f after the factors, which are K·N per entry.
//
// SEARCH. exhaustive scores every pair; alternating (the default past 10⁶ pairs) fixes one end and takes the other's best, then the reverse,
// until neither moves, from the beams nearest each of the strongest rays (StartPairs distinct starts), keeping the best it saw.
//
// DISTINCT RANKS (R-pch9-4). The scored pairs sorted by metric; a pair within one beamwidth at BOTH ends of a higher-ranked distinct pair is not
// distinct (max(|Δu|/Δu_bw, |Δv|/Δv_bw) ≤ 1 at each end, so a grid neighbour, diagonal ones included, is the same path), so rank 2 is the next
// path, not the beam beside rank 1.
//
// BOUND (R-pch9-6). Phase-only weights on every element, unconstrained by the codebook: the dominant eigenvector of the spatial covariance at one
// end with the other end fixed, Σ_f q_f*·q_fᵀ, projected onto unit-modulus phases, alternated between the ends from the best pair until the
// metric stops moving. The element positions' steering vectors carry the same centre-element pattern as the score, so the codebook's loss
// against the bound is a loss of the same model.
//
// APPLIED IN MEMORY (R-pch9-5). The chosen entry's beamformer weights are taken into the array's own (PhasedArrayFeedModel.TakeWeights) before
// anything is stamped: the netlist is not elaborated again and the schematic is not written. A run that calls Apply twice selects once.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Propagation;

namespace CircuitRF.Engine.AntennaArray;

/// <summary>One beam of an end: its 1-based index, direction (degrees, the end's own frame) and direction cosines.</summary>
public sealed record BeamEntry(int Index, double ThetaDeg, double PhiDeg, double U, double V, string Label);

/// <summary>One scored pair: 0-based entries of each end, the metric (dB), and its rank among distinct pairs (null: not distinct).</summary>
public sealed record BeamPair(int Tx, int Rx, double MetricDb, int? Rank);

/// <summary>One ray of the drop at TimeOffset as the two ends see it.</summary>
public sealed record BeamRay(double TxThetaDeg, double TxPhiDeg, double RxThetaDeg, double RxPhiDeg, double PowerDb);

/// <summary>brief-pch-9 R-pch9-6 — what a selection scored and chose.</summary>
public sealed class BeamTable
{
    public required string Channel { get; init; }
    public required BeamSelectMode Mode { get; init; }
    public required string Search { get; init; }
    public required IReadOnlyList<BeamEntry> TxBeams { get; init; }
    public required IReadOnlyList<BeamEntry> RxBeams { get; init; }
    /// <summary>Which ends the selection set (a pin or an array that keeps its weights is not set).</summary>
    public required bool TxSteered { get; init; }
    public required bool RxSteered { get; init; }
    /// <summary>The instance each end is (null for a pin).</summary>
    public string? TxArray { get; init; }
    public string? RxArray { get; init; }
    /// <summary>Every scored or visited pair, strongest first.</summary>
    public required IReadOnlyList<BeamPair> Pairs { get; init; }
    public required BeamPair Chosen { get; init; }
    public required string Why { get; init; }
    public required double BoundDb { get; init; }
    public double CodebookLossDb => BoundDb - Chosen.MetricDb;
    public required long PairsScored { get; init; }
    public required long PairsTotal { get; init; }
    public required double ReferenceHz { get; init; }
    public required double TimeS { get; init; }
    public required int FrequencySamples { get; init; }
    public required double BandwidthHz { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>The drop's rays as each end sees them: departure in the transmit end's frame, arrival in the receive end's (degrees; NaN at
    /// a pin), and power relative to the strongest (dB).</summary>
    public required IReadOnlyList<BeamRay> Rays { get; init; }

    /// <summary>The chosen entries' beamformers (null at an end not steered), for a later run on new beamformers to take again.</summary>
    internal PhasedArrayFeedModel? TxChosenFeed { get; init; }
    internal PhasedArrayFeedModel? RxChosenFeed { get; init; }

    /// <summary>The pair's label: <c>tx 3 (12°, 90°) · rx 7 (8°, −90°)</c>.</summary>
    public string Label(BeamPair p) => $"tx {TxBeams[p.Tx].Label} · rx {RxBeams[p.Rx].Label}";
}

/// <summary>brief-pch-9 — codebooks, scoring, the search, the bound, and the in-memory application.</summary>
public static class BeamSelection
{
    /// <summary>The alternating search's distinct starting pairs, each the beams nearest one of the strongest rays.</summary>
    public const int StartPairs = 8;

    /// <summary>The pair count past which BeamSearch's default is alternating.</summary>
    public const long ExhaustiveLimit = 1_000_000;

    [ThreadStatic] private static long _pairsScored;
    private static long _selections;
    /// <summary>Pairs scored on this thread (eq. 2 evaluated through one pair of beams).</summary>
    public static long PairsScored => _pairsScored;
    /// <summary>Searches run in this process (a selection taken again, from the drag or the kept ones, is not one).</summary>
    public static long Selections => Interlocked.Read(ref _selections);

    /// <summary>
    /// Selects the beam of every channel in <paramref name="nl"/> with an array end, at the reference frequency of <paramref name="hz"/>,
    /// and takes the chosen weights into the arrays before the run stamps anything. <paramref name="bandwidthHz"/> is the signal's band
    /// (0: one sample at the carrier). Refusals are thrown as <see cref="ParameterRefusalException"/> with the sentence.
    /// </summary>
    /// <para><paramref name="setCarrier"/>: the run's carrier is <paramref name="hz"/>, set on every channel first (as the run then sets it).</para>
    public static void Apply(ElaboratedNetlist nl, double hz, double bandwidthHz = 0, bool setCarrier = false)
    {
        if (!nl.Components.Any(c => c.Model is PropagationChannelModel { HasInstanceEnd: true })) return;
        if (setCarrier) foreach (var c in nl.Components) if (c.Model is ICarrierContext cc) cc.SetCarrier(hz);
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);   // array → the channel that set it
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not PropagationChannelModel ch || !ch.HasInstanceEnd) continue;
            if (Select(ch, ec.InstancePath, hz, bandwidthHz, nl, taken, ec.Parameters) is { } table)
                foreach (string note in table.Notes) nl.AddNoteOnce($"beam:{ec.InstancePath}:{note.GetHashCode()}", note);
        }
    }

    /// <summary>
    /// The selection of one channel at <paramref name="hz"/>, applied; null when nothing is selected (BeamSelect=manual, or unstated with no
    /// steerable array end). Called again at the same reference frequency and time it returns the table it made and changes nothing.
    /// </summary>
    /// <para><paramref name="parameters"/>: the channel's resolved parameters, which with its ends' make the key of the selections kept
    /// across netlists (null: none kept, none looked up).</para>
    public static BeamTable? Select(PropagationChannelModel ch, string path, double hz, double bandwidthHz = 0, ElaboratedNetlist? nl = null,
                                    Dictionary<string, string>? taken = null, IReadOnlyDictionary<string, Value>? parameters = null)
    {
        double f = ch.ReferenceHz(hz);
        if (!(f > 0)) return null;
        lock (ch)
        {
            // selected once per run: the same frequency, time and BEAMFORMERS (a re-weighed netlist shares the channel but has new ones,
            // which must take the selection again)
            var txArray = ch.TxEnd?.Model as PhasedArrayModel;
            var rxArray = ch.RxEnd?.Model as PhasedArrayModel;
            object? txFeed = txArray?.Feed, rxFeed = rxArray?.Feed;
            if (_applied.TryGetValue(ch, out var at) && at.Hz == f && at.TimeS == ch.FrozenTimeS && ch.BeamTable is BeamTable kept)
            {
                if (ReferenceEquals(at.TxFeed, txFeed) && ReferenceEquals(at.RxFeed, rxFeed)) return kept;
                // a re-weighed netlist (a beam drag, a tuned VAR): new beamformers on the same channel. When each steered end's chosen beam
                // weighs exactly as it did, nothing a candidate depends on moved (θ0 and φ0 are replaced in every candidate), so the choice
                // stands and is taken again — one weighing per end, not the whole search at every drag step
                if (Same(txArray, kept.TxSteered, kept.TxBeams, kept.Chosen.Tx, at.TxChosen, f)
                    && Same(rxArray, kept.RxSteered, kept.RxBeams, kept.Chosen.Rx, at.RxChosen, f))
                {
                    if (kept.TxSteered) txArray!.Feed!.TakeWeights(at.TxChosen!);
                    if (kept.RxSteered) rxArray!.Feed!.TakeWeights(at.RxChosen!);
                    _applied.AddOrUpdate(ch, at with { TxFeed = txFeed, RxFeed = rxFeed });
                    Taken(kept, ch, path, taken);
                    Reused++;
                    return kept;
                }
            }
            if (ch.BeamSelect == BeamSelectMode.Manual) return null;
            // kept across netlists: an envelope run's segments and a sweep's points elaborate the same channel and arrays again
            string? key = parameters is null ? null : KeyOf(ch, path, f, bandwidthHz, parameters);
            if (key is null) return BuildAndKeep(null);
            // an envelope run's segments start together, on netlists of their own: the first searches, the others wait for it and take its choice
            if (_building.Count > 1024) _building.Clear();
            lock (_building.GetOrAdd(key, _ => new object())) return TakeOrBuild();

            BeamTable? TakeOrBuild()
            {
                if (Lookup(key) is { } hit
                    && Same(txArray, hit.Table.TxSteered, hit.Table.TxBeams, hit.Table.Chosen.Tx, hit.TxChosen, f)
                    && Same(rxArray, hit.Table.RxSteered, hit.Table.RxBeams, hit.Table.Chosen.Rx, hit.RxChosen, f))
                {
                    if (hit.Table.TxSteered) txArray!.Feed!.TakeWeights(hit.TxChosen!);
                    if (hit.Table.RxSteered) rxArray!.Feed!.TakeWeights(hit.RxChosen!);
                    ch.BeamTable = hit.Table;
                    _applied.AddOrUpdate(ch, new Applied(f, ch.FrozenTimeS, txFeed, rxFeed, hit.TxChosen, hit.RxChosen));
                    Taken(hit.Table, ch, path, taken);
                    Interlocked.Increment(ref _cacheHits);
                    return hit.Table;
                }
                return BuildAndKeep(key);
            }

            BeamTable? BuildAndKeep(string? keepAs)
            {
                var table = Build(ch, path, f, bandwidthHz, nl, taken ?? [], apply: true, _control.Value);
                ch.BeamTable = table;
                _applied.AddOrUpdate(ch, new Applied(f, ch.FrozenTimeS, txFeed, rxFeed, table?.TxChosenFeed, table?.RxChosenFeed));
                if (keepAs is not null && table is not null) Remember(keepAs, new Kept(table, table.TxChosenFeed, table.RxChosenFeed));
                return table;
            }
        }
    }

    private sealed record Applied(double Hz, double TimeS, object? TxFeed, object? RxFeed, PhasedArrayFeedModel? TxChosen, PhasedArrayFeedModel? RxChosen);

    /// <summary>True when an end is not steered, or its chosen entry weighs now exactly as <paramref name="chosen"/> did.</summary>
    private static bool Same(PhasedArrayModel? array, bool steered, IReadOnlyList<BeamEntry> beams, int entry, PhasedArrayFeedModel? chosen, double f)
    {
        if (!steered) return true;
        if (array?.Feed is not { } feed || array.Steering is not { Refusal: null } steering || chosen is null || chosen.Elements != feed.Elements) return false;
        var now = steering.Weigh(beams[entry].ThetaDeg, beams[entry].PhiDeg);
        for (int e = 0; e < now.Elements; e++)
            if (now.Quantised(e, f) != chosen.Quantised(e, f) || now.Requested(e, f) != chosen.Requested(e, f)) return false;
        return true;
    }

    private static void Taken(BeamTable t, PropagationChannelModel ch, string path, Dictionary<string, string>? taken)
    {
        if (taken is null) return;
        if (t.TxSteered && ch.TxInstance is { } tx) taken[tx] = path;
        if (t.RxSteered && ch.RxInstance is { } rx) taken[rx] = path;
    }

    // ── selections kept across netlists ────────────────────────────────────────────────────────────────────────

    private sealed record Kept(BeamTable Table, PhasedArrayFeedModel? TxChosen, PhasedArrayFeedModel? RxChosen);
    private static readonly LinkedList<(string Key, Kept Entry)> _kept = new();

    /// <summary>Selections kept across netlists, the oldest dropped first: a table holds every pair scored, up to 10⁶.</summary>
    public const int KeptSelections = 8;

    private static long _cacheHits;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _building = new(StringComparer.Ordinal);
    /// <summary>Selections taken from the kept ones in this process (an envelope segment's, a sweep point's).</summary>
    public static long CacheHits => Interlocked.Read(ref _cacheHits);

    /// <summary>Forgets every kept selection (a test's clean start).</summary>
    public static void ForgetKept() { lock (_kept) _kept.Clear(); }

    private static Kept? Lookup(string key)
    {
        lock (_kept)
            for (var n = _kept.First; n is not null; n = n.Next)
                if (n.Value.Key == key) { _kept.Remove(n); _kept.AddLast(n); return n.Value.Entry; }
        return null;
    }

    private static void Remember(string key, Kept entry)
    {
        lock (_kept)
        {
            for (var n = _kept.First; n is not null; n = n.Next) if (n.Value.Key == key) { _kept.Remove(n); break; }
            _kept.AddLast((key, entry));
            while (_kept.Count > KeptSelections) _kept.RemoveFirst();
        }
    }

    /// <summary>
    /// The key of a selection: everything the candidates, the rays and the scores are made of that the beamformers' own weights are not — the
    /// channel's instance (its draws' streams), resolved parameters and the files they name (with their write times), each end's instance,
    /// resolved parameters, record and elaborated weights, the reference frequency, TimeOffset and the band. Whether the CHOSEN entries still
    /// weigh as they did (the field, Amp, PhaseBits) is checked on every hit, by weighing the chosen entries again (<see cref="Same"/>), so a key that misses it costs a search,
    /// never a wrong beam.
    /// </summary>
    private static string KeyOf(PropagationChannelModel ch, string path, double f, double bandwidthHz, IReadOnlyDictionary<string, Value> parameters)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        void Params(IReadOnlyDictionary<string, Value> p)
        {
            foreach (var (k, v) in p.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                sb.Append(k).Append('=').Append(v.Kind switch
                {
                    Core.Expressions.ValueKind.Real => v.AsReal().ToString("R", inv),
                    Core.Expressions.ValueKind.Complex => v.AsComplex().ToString("R", inv),
                    Core.Expressions.ValueKind.Bool => v.AsBool() ? "1" : "0",
                    _ => v.AsString(),
                });
                // a pattern is path#port: the file's time is the path's, so a record re-solved in place re-scores
                if (v.Kind == Core.Expressions.ValueKind.String && v.AsString().Trim('"') is { Length: > 0 } text
                    && (k is "TxPattern" or "RxPattern" && text.LastIndexOf('#') is > 0 and var hash ? text[..hash] : text) is var file && File.Exists(file))
                    sb.Append('@').Append(File.GetLastWriteTimeUtc(file).Ticks);
                sb.Append(';');
            }
        }
        sb.Append(path).Append('|').Append(f.ToString("R", inv)).Append('|').Append(ch.FrozenTimeS.ToString("R", inv)).Append('|')
          .Append(bandwidthHz.ToString("R", inv)).Append('|');
        Params(parameters);
        foreach (var ec in new[] { ch.TxEnd, ch.RxEnd })
        {
            sb.Append("|end:");
            if (ec is null) continue;
            sb.Append(ec.InstancePath).Append(':');
            Params(ec.Parameters);
            if (ec.Model is not PhasedArrayModel pa) continue;
            if (File.Exists(pa.RecordPath)) sb.Append("record@").Append(File.GetLastWriteTimeUtc(pa.RecordPath).Ticks);
            // the weights as elaborated: an end that keeps them (not a steer(), or taken by another channel) scores the other end through them
            if (pa.Feed is { } feed) sb.Append("|w:").Append(feed.WeightsKey());
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Selections that took a previous choice again on new beamformers (<see cref="Select"/>), on this thread.</summary>
    [ThreadStatic] public static long Reused;

    // ── progress and cancellation: the run's control, held for the code that runs it ────────────────────────────

    private static readonly AsyncLocal<CircuitRF.Engine.RunControl?> _control = new();

    /// <summary>
    /// Makes <paramref name="control"/> the one a selection started inside the returned scope reports to and stops on: its stage counter
    /// shows the codebooks weighed and the pairs scored (a 64 × 64 array's default codebooks are minutes in a Debug build), and Stop is
    /// answered between entries. Ambient, because the selection starts deep inside every engine and none of them is handed the run's
    /// control the same way; it flows into the tasks a run starts.
    /// </summary>
    public static IDisposable Use(CircuitRF.Engine.RunControl? control)
    {
        var was = _control.Value;
        _control.Value = control;
        return new Restore(() => _control.Value = was);
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropagationChannelModel, Applied> _applied = new();

    /// <summary>
    /// R-pch9-7 — the table the Phased Array tool shows: scored as the run would score it (BeamSelect=manual as auto, an end that cannot be
    /// steered kept as it is), with NOTHING taken into the arrays and nothing kept on the channel. Null when no end can be steered.
    /// </summary>
    /// <para><paramref name="control"/>: progress (its stage counter) and cancellation, which throws <see cref="OperationCanceledException"/>.</para>
    public static BeamTable? Score(PropagationChannelModel ch, string path, double hz, CircuitRF.Engine.RunControl? control = null)
    {
        double f = ch.ReferenceHz(hz);
        if (!(f > 0)) return null;
        lock (ch) return Build(ch, path, f, 0, null, [], apply: false, control);
    }

    // ── the ends ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class End
    {
        public required string Name;                    // "transmit" / "receive"
        public string? Instance;
        public PhasedArrayModel? Array;
        public bool Steered;
        public bool Unscorable;                         // an EM Field Data block or an Elements-mode array: no beam to score through
        public List<BeamEntry> Entries = [];
        public Dictionary<int, PhasedArrayFeedModel> Kept = [];   // the few entries weighed again (the chosen, the best): see Feed

        /// <summary>The beamformer of entry <paramref name="i"/>: a fixed array's own; a steered one weighed through its steer() (kept, so the
        /// chosen and the best entries are weighed once more each, never every entry held at once — 2,501 entries of a 64 × 64 array held
        /// 1.8 GB).</summary>
        public PhasedArrayFeedModel Feed(int i, bool keep)
        {
            if (!Steered) return Array!.Feed!;
            if (Kept.TryGetValue(i, out var kept)) return kept;
            // scoring takes the closed form where the field is the bare call; anything kept is applied, and is weighed exactly
            if (!keep && Array!.Steering!.Fast is { } fast) return fast(Entries[i].ThetaDeg, Entries[i].PhiDeg);
            var feed = Array!.Steering!.Weigh(Entries[i].ThetaDeg, Entries[i].PhiDeg);
            if (keep) Kept[i] = feed;
            return feed;
        }
        public double BwU = double.PositiveInfinity, BwV = double.PositiveInfinity;
    }

    private static BeamTable? Build(PropagationChannelModel ch, string path, double f, double bandwidthHz, ElaboratedNetlist? nl,
                                    Dictionary<string, string> taken, bool apply, CircuitRF.Engine.RunControl? control)
    {
        string? stageWas = control?.Stage;
        try { return BuildCore(ch, path, f, bandwidthHz, nl, taken, apply, control); }
        finally { if (control is not null && stageWas is not null) control.BeginStage(stageWas); }
    }

    private static BeamTable? BuildCore(PropagationChannelModel ch, string path, double f, double bandwidthHz, ElaboratedNetlist? nl,
                                        Dictionary<string, string> taken, bool apply, CircuitRF.Engine.RunControl? control)
    {
        bool stated = ch.BeamSelectStated && ch.BeamSelect != BeamSelectMode.Manual && apply;
        var notes = new List<string>();
        string who = $"Propagation Channel '{path}'";
        End MakeEnd(bool tx)
        {
            var end = new End { Name = tx ? "transmit" : "receive", Instance = tx ? ch.TxInstance : ch.RxInstance };
            var ec = tx ? ch.TxEnd : ch.RxEnd;
            if (end.Instance is null) return end;                                     // a pin
            if (ec?.Model is not PhasedArrayModel pa || pa.Feed is null || pa.Steering is null)
            {
                string what = ec?.Model is PhasedArrayModel ? $"'{end.Instance}' is in Elements mode, with no beamformer to weigh"
                                                             : $"'{end.Instance}' is an EM Field Data block, with no beamformer to weigh";
                if (stated)
                    throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamEndNotFeed(path, Word(ch.BeamSelect), end.Instance,
                        ec?.Model is PhasedArrayModel ? "a Phased Antenna Array in Elements mode" : "an EM Field Data block"));
                end.Unscorable = true;
                notes.Add($"{who}: {what}, so its {end.Name} end is not selected.");
                return end;
            }
            end.Array = pa;
            if (pa.Steering.Refusal is { } why)
            {
                if (stated) throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamCannotSteer(path, Word(ch.BeamSelect), end.Instance, why));
                notes.Add($"{who}: '{end.Instance}' keeps its own weights. {why}");
                return end;
            }
            if (taken.TryGetValue(end.Instance, out var other))
            {
                notes.Add($"{who}: '{end.Instance}''s beam was selected by '{other}', which ends on it too, so this channel scores through that beam.");
                return end;
            }
            end.Steered = true;
            return end;
        }
        var txEnd = MakeEnd(true);
        var rxEnd = MakeEnd(false);
        if (txEnd.Unscorable || rxEnd.Unscorable) return NoSelection();
        if (!txEnd.Steered && !rxEnd.Steered) return NoSelection();
        BeamTable? NoSelection()
        {
            // unstated, with nothing steerable, BeamSelect is simply manual and there is nothing to say
            if (nl is not null && ch.BeamSelectStated) foreach (string n in notes) nl.AddNoteOnce($"beam:{path}:{n.GetHashCode()}", n);
            return null;
        }

        // ── the codebooks ──────────────────────────────────────────────────────────────────────────────────────
        double lambda = PropagationMath.C / f;
        foreach (var (end, key) in new[] { (txEnd, "tx"), (rxEnd, "rx") })
        {
            if (end.Array is { } pa)
            {
                var lat = pa.Lattice;
                end.BwU = lat.Cols > 1 ? lambda / (lat.Cols * lat.Dx) : 2;
                end.BwV = lat.Rows > 1 ? lambda / (lat.Rows * lat.Dy) : 2;
            }
            if (!end.Steered)
            {
                string label = end.Array is null ? "pin" : "own weights";
                (double t, double p) = end.Array?.Steering?.Authored is { } a ? (a.Theta * 180 / Math.PI, a.Phi * 180 / Math.PI) : (double.NaN, double.NaN);
                end.Entries.Add(Entry(1, t, p, label));
                continue;
            }
            var dirs = ch.Codebook is { } rows ? rows.Where(r => r.End is null || r.End == key).Select(r => (r.ThetaDeg, r.PhiDeg)).ToList()
                                               : Grid(end.BwU, end.BwV, ch.ScanLimitDeg);
            if (dirs.Count == 0)
                throw new ParameterRefusalException(PropagationChannelDiagnostics.CodebookNoEnd(path, ch.CodebookPath ?? "", end.Name, end.Instance ?? ""));
            for (int i = 0; i < dirs.Count; i++) end.Entries.Add(Entry(i + 1, dirs[i].Item1, dirs[i].Item2, null));
        }

        // BeamSelect=index: a named entry, or a direction added as an entry of its own
        int? pickTx = null, pickRx = null;
        if (ch.BeamSelect == BeamSelectMode.Index)
        {
            int? Pick(End end, BeamChoice? choice, string keyName)
            {
                if (!end.Steered) return choice is null ? 0 : throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamIndexOnFixedEnd(
                    path, keyName, choice.ToString(), end.Name, end.Array is null ? "is a pin" : "keeps its own weights"));
                if (choice is null)
                    throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamIndexNeeded(path, keyName, end.Entries.Count, end.Instance ?? ""));
                if (choice.Index is { } k)
                    return k <= end.Entries.Count ? k - 1
                        : throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamIndexBeyond(path, keyName, k, end.Name, end.Entries.Count));
                end.Entries.Add(Entry(end.Entries.Count + 1, choice.ThetaDeg, choice.PhiDeg, $"{keyName} ({choice.ThetaDeg:0.##}°, {choice.PhiDeg:0.##}°)"));
                return end.Entries.Count - 1;
            }
            pickTx = Pick(txEnd, ch.TxBeam, "TxBeam");
            pickRx = Pick(rxEnd, ch.RxBeam, "RxBeam");
        }

        // ── the rays at TimeOffset, and each end's field per entry per ray ─────────────────────────────────────
        var r = ch.At(f);
        var ends = ch.DescribeRays(f);
        int K = ends.Count;
        double amp = Math.Pow(10, -r.ExcessDb / 20) * PropagationMath.C / (4 * Math.PI * f * r.DistanceM);
        double scale = f / r.ReferenceHz, tt = ch.FrozenTimeS;
        var s = new Complex[K];
        for (int i = 0; i < K; i++)
        {
            var ray = r.Rays[i];
            var fade = i < r.Fades.Count ? r.Fades[i] : null;
            s[i] = amp * ray.Gain * (fade is null || fade.Sinusoids == 0 ? Complex.One : fade.Gain(tt))
                   * Complex.FromPolarCoordinates(1, 2 * Math.PI * ray.DopplerHz * scale * tt + Math.PI * r.DriftHzps * scale * tt * tt);
        }
        int nf = bandwidthHz > 0 ? Math.Clamp((int)Math.Ceiling(4 * bandwidthHz * (K == 0 ? 0 : r.MaxExcessDelayS)) + 1, 8, 64) : 1;
        var freqs = new double[nf];
        for (int i = 0; i < nf; i++) freqs[i] = nf == 1 ? f : f - 0.5 * bandwidthHz + bandwidthHz * i / (nf - 1);
        var e = new Complex[nf * K];                     // s_r·e^(−j2πf_i τ_r)
        for (int i = 0; i < nf; i++)
            for (int k = 0; k < K; k++)
                e[i * K + k] = s[k] * Complex.FromPolarCoordinates(1, -2 * Math.PI * freqs[i] * r.Rays[k].ExcessDelayS);

        // g[entry][ray] = E_c·AF/√N (θ, φ) for each end; M_r·g_tx for the transmit end
        (Complex T, Complex P)[][] Fields(End end, bool tx)
        {
            var refs = ends.Select(x => tx ? x.Tx : x.Rx).ToArray();
            var g = new (Complex, Complex)[end.Entries.Count][];
            if (end.Array is null)
            {
                g[0] = refs;
                return g;
            }
            var pa = end.Array;
            var dirs = ArrayDirections.Of([.. ends.Select(x => tx ? x.TxTheta : x.RxTheta)], [.. ends.Select(x => tx ? x.TxPhi : x.RxPhi)]);
            double norm = 1 / Math.Sqrt(pa.Lattice.Count);
            var tables = ArrayPattern.TransientTables(pa.Lattice, dirs, f);   // the rays' directions are every entry's: built once
            for (int b = 0; b < end.Entries.Count; b++)
            {
                var feed = end.Feed(b, keep: false);   // each entry weighed through the array's own steer(), then let go
                var w = new Complex[pa.Lattice.Count];
                for (int n = 0; n < w.Length; n++) w[n] = feed.Quantised(n, f);
                var af = ArrayPattern.FactorOnce(tables, pa.Lattice, w, K);
                var row = new (Complex, Complex)[K];
                for (int k = 0; k < K; k++) row[k] = (refs[k].Theta * af[k] * norm, refs[k].Phi * af[k] * norm);
                g[b] = row;
                if (end.Steered) control?.TickStage();   // and Stop, between entries
            }
            return g;
        }
        control?.BeginStage($"Beam of {path}: weighing the codebooks", (txEnd.Steered ? txEnd.Entries.Count : 0) + (rxEnd.Steered ? rxEnd.Entries.Count : 0),
                            "beam(s)");
        var gTx = Fields(txEnd, true);
        var gRx = Fields(rxEnd, false);
        var mTx = new (Complex T, Complex P)[gTx.Length][];
        for (int b = 0; b < gTx.Length; b++)
        {
            mTx[b] = new (Complex, Complex)[K];
            for (int k = 0; k < K; k++) mTx[b][k] = ends[k].M.Apply(gTx[b][k].T, gTx[b][k].P);
        }

        long scored = 0;
        var metric = new Dictionary<(int, int), double>();
        double Score(int a, int b)
        {
            if (metric.TryGetValue((a, b), out double m)) return m;
            var c = new Complex[K];
            var ga = mTx[a];
            var gb = gRx[b];
            for (int k = 0; k < K; k++) c[k] = gb[k].T * ga[k].T + gb[k].P * ga[k].P;
            double sum = 0;
            for (int i = 0; i < nf; i++)
            {
                Complex h = 0;
                int o = i * K;
                for (int k = 0; k < K; k++) h += e[o + k] * c[k];
                sum += h.Real * h.Real + h.Imaginary * h.Imaginary;
            }
            m = sum / nf;
            metric[(a, b)] = m;
            scored++;
            return m;
        }

        // ── the search ─────────────────────────────────────────────────────────────────────────────────────────
        int A = txEnd.Entries.Count, B = rxEnd.Entries.Count;
        long total = (long)A * B;
        bool exhaustive = ch.BeamSearch == BeamSearchMode.Exhaustive || ch.BeamSearch == BeamSearchMode.Default && total <= ExhaustiveLimit
                          || A == 1 || B == 1;
        if (exhaustive)
        {
            control?.BeginStage($"Beam of {path}: scoring every pair", A, "transmit beam(s)");
            for (int a = 0; a < A; a++)
            {
                for (int b = 0; b < B; b++) Score(a, b);
                control?.TickStage();
            }
        }
        else
        {
            control?.BeginStage($"Beam of {path}: alternating search", StartPairs, "start(s)");
            // from the beams nearest each of the strongest rays, up to StartPairs distinct starting pairs (a cluster's rays share one)
            var starts = new HashSet<(int, int)>();
            foreach (int k0 in Enumerable.Range(0, K).OrderByDescending(k => s[k].Magnitude))
            {
                if (starts.Count >= StartPairs) break;
                int a = Nearest(txEnd, ends[k0].TxTheta, ends[k0].TxPhi), b = Nearest(rxEnd, ends[k0].RxTheta, ends[k0].RxPhi);
                if (!starts.Add((a, b))) continue;
                for (int it = 0; it < 50; it++)
                {
                    int a2 = Enumerable.Range(0, A).MaxBy(x => Score(x, b));
                    int b2 = Enumerable.Range(0, B).MaxBy(y => Score(a2, y));
                    if (a2 == a && b2 == b) break;
                    a = a2; b = b2;
                    control?.ThrowIfCancellationRequested();
                }
                control?.TickStage();
            }
        }
        if (pickTx is int pt && pickRx is int pr) Score(pt, pr);
        _pairsScored += scored;
        Interlocked.Increment(ref _selections);

        // ── distinct ranks ─────────────────────────────────────────────────────────────────────────────────────
        static double Db(double x) => x > 0 ? 10 * Math.Log10(x) : double.NegativeInfinity;
        var sorted = metric.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2).ToList();
        var distinct = new List<(int A, int B)>();
        var pairs = new List<BeamPair>(sorted.Count);
        bool Near(End end, int i, int j)
        {
            if (end.Entries.Count == 1 || i == j) return true;
            var x = end.Entries[i];
            var y = end.Entries[j];
            return Math.Max(Math.Abs(x.U - y.U) / end.BwU, Math.Abs(x.V - y.V) / end.BwV) <= 1 + 1e-9;
        }
        foreach (var ((a, b), m) in sorted)
        {
            bool same = distinct.Any(d => Near(txEnd, a, d.A) && Near(rxEnd, b, d.B));
            int? rank = null;
            if (!same) { distinct.Add((a, b)); rank = distinct.Count; }
            pairs.Add(new BeamPair(a, b, Db(m), rank));
        }

        // ── the choice ─────────────────────────────────────────────────────────────────────────────────────────
        BeamPair chosen;
        string why;
        switch (ch.BeamSelect)
        {
            case BeamSelectMode.Index:
                chosen = pairs.First(p => p.Tx == pickTx && p.Rx == pickRx);
                why = "BeamSelect=index names it";
                break;
            case BeamSelectMode.Rank:
                chosen = pairs.FirstOrDefault(p => p.Rank == ch.BeamRank)
                         ?? throw new ParameterRefusalException(PropagationChannelDiagnostics.BeamRankBeyond(path, ch.BeamRank,
                                exhaustive ? "codebook" : "alternating search", distinct.Count));
                why = $"BeamSelect=rank: the {Ordinal(ch.BeamRank)} strongest distinct pair";
                break;
            case BeamSelectMode.Manual:
                chosen = pairs[0];
                why = "BeamSelect=manual: the arrays keep their own weights; this is the strongest pair";
                break;
            default:
                chosen = pairs[0];
                why = ch.BeamSelectStated ? "BeamSelect=auto: the strongest pair" : "the strongest pair (BeamSelect unstated, so auto)";
                break;
        }

        // ── the bound ──────────────────────────────────────────────────────────────────────────────────────────
        control?.BeginStage($"Beam of {path}: the phase-only bound");
        double bound = Math.Max(Bound(txEnd, rxEnd, ends, e, nf, K, f, chosen, gTx, gRx, sorted[0].Key, control), sorted[0].Value);

        // ── applied, in memory ─────────────────────────────────────────────────────────────────────────────────
        foreach (var (end, idx) in new[] { (txEnd, chosen.Tx), (rxEnd, chosen.Rx) })
        {
            if (!end.Steered || !apply) continue;
            end.Array!.Feed!.TakeWeights(end.Feed(idx, keep: true));
            taken[end.Instance!] = path;
        }

        string search = exhaustive ? "exhaustive" : "alternating";
        var table = new BeamTable
        {
            Channel = path, Mode = ch.BeamSelect, Search = search,
            TxBeams = txEnd.Entries, RxBeams = rxEnd.Entries, TxSteered = txEnd.Steered, RxSteered = rxEnd.Steered,
            TxArray = txEnd.Array is null ? null : ch.TxInstance, RxArray = rxEnd.Array is null ? null : ch.RxInstance,
            Pairs = pairs, Chosen = chosen, Why = why, BoundDb = Db(bound), PairsScored = scored, PairsTotal = total,
            ReferenceHz = f, TimeS = tt, FrequencySamples = nf, BandwidthHz = nf > 1 ? bandwidthHz : 0, Notes = notes,
            Rays = RaysOf(ends, s),
            TxChosenFeed = txEnd.Steered ? txEnd.Feed(chosen.Tx, keep: true) : null,
            RxChosenFeed = rxEnd.Steered ? rxEnd.Feed(chosen.Rx, keep: true) : null,
        };
        var inv = CultureInfo.InvariantCulture;
        notes.Add(string.Format(inv, "{0}: beam pair {1} — {2}, {3:0.00} dB through the linear pattern model ({4} of {5} pairs scored, {6}); the " +
                                     "unconstrained phase-only bound is {7:0.00} dB, so the codebook costs {8:0.00} dB. The selection used the linear " +
                                     "pattern model (array factor and the centre element's pattern); the run that follows is the full circuit.",
                                who, table.Label(chosen), why, chosen.MetricDb, scored, total, search, table.BoundDb, table.CodebookLossDb));
        return table;
    }

    // ── the bound: phase-only, alternating dominant eigenvectors ───────────────────────────────────────────────

    private static double Bound(End txEnd, End rxEnd, IReadOnlyList<PropagationChannelModel.RayEnds> ends, Complex[] e, int nf, int K, double f,
                                BeamPair chosen, (Complex T, Complex P)[][] gTx, (Complex T, Complex P)[][] gRx, (int A, int B) best,
                                CircuitRF.Engine.RunControl? control)
    {
        // κ_r = E_rx,cᵀ·M_r·E_tx,c (the centre elements' coupling through the ray), steering vectors per element per ray
        var kappa = new Complex[K];
        for (int k = 0; k < K; k++)
        {
            var (mt, mp) = ends[k].M.Apply(ends[k].Tx.Theta, ends[k].Tx.Phi);
            kappa[k] = ends[k].Rx.Theta * mt + ends[k].Rx.Phi * mp;
        }
        Complex[][] Steering(End end, bool tx)
        {
            if (end.Array is not { } pa) return [.. Enumerable.Range(0, K).Select(_ => new[] { Complex.One })];
            double kw = 2 * Math.PI * f / PropagationMath.C;
            var a = new Complex[K][];
            for (int k = 0; k < K; k++)
            {
                double th = tx ? ends[k].TxTheta : ends[k].RxTheta, ph = tx ? ends[k].TxPhi : ends[k].RxPhi;
                double ux = Math.Sin(th) * Math.Cos(ph), uy = Math.Sin(th) * Math.Sin(ph);
                var row = new Complex[pa.Lattice.Count];
                for (int n = 0; n < row.Length; n++)
                {
                    if (!pa.Lattice.IsOn(n)) continue;
                    var (x, y) = pa.Lattice.Position(n);
                    row[n] = Complex.FromPolarCoordinates(1, kw * (ux * x + uy * y));
                }
                a[k] = row;
            }
            return a;
        }
        var aTx = Steering(txEnd, true);
        var aRx = Steering(rxEnd, false);
        int nt = aTx[0].Length, nr = aRx[0].Length;
        Complex[] Start(End end, int entry, int count)
        {
            var w = new Complex[count];
            if (end.Array is not { } pa) { w[0] = 1; return w; }
            var feed = end.Feed(entry, keep: true);
            // a steered end starts phase-only (the bound's own constraint); an end that is not steered is never updated, so it keeps its
            // weights as they are, taper and all — taking its amplitudes off overstated the bound and the codebook's cost
            for (int n = 0; n < count; n++)
            {
                var q = feed.Quantised(n, f);
                w[n] = !end.Steered ? q : q == Complex.Zero ? Complex.Zero : Complex.FromPolarCoordinates(1, q.Phase);
            }
            return w;
        }
        var wt = Start(txEnd, best.A, nt);
        var wr = Start(rxEnd, best.B, nr);
        double nT = 1 / Math.Sqrt(nt), nR = 1 / Math.Sqrt(nr);
        double Metric()
        {
            var at = new Complex[K]; var ar = new Complex[K];
            for (int k = 0; k < K; k++)
            {
                Complex x = 0, y = 0;
                for (int n = 0; n < nt; n++) x += aTx[k][n] * wt[n];
                for (int n = 0; n < nr; n++) y += aRx[k][n] * wr[n];
                at[k] = x * nT; ar[k] = y * nR;
            }
            double sum = 0;
            for (int i = 0; i < nf; i++)
            {
                Complex h = 0;
                for (int k = 0; k < K; k++) h += e[i * K + k] * kappa[k] * at[k] * ar[k];
                sum += h.Real * h.Real + h.Imaginary * h.Imaginary;
            }
            return sum / nf;
        }
        // one end's update with the other fixed: w ← phase(Σ_i conj(q_i)·(q_iᵀ·w)), q_i = Σ_r e_ir·κ_r·α_r·a(r), α_r the other end's gain
        void Update(Complex[] w, Complex[][] a, Complex[] other, Complex[][] aOther, double nOther, int count)
        {
            if (count == 1) return;
            var alpha = new Complex[K];
            for (int k = 0; k < K; k++)
            {
                Complex x = 0;
                for (int n = 0; n < other.Length; n++) x += aOther[k][n] * other[n];
                alpha[k] = x * nOther;
            }
            var next = new Complex[count];
            var q = new Complex[count];
            for (int i = 0; i < nf; i++)
            {
                Array.Clear(q);
                for (int k = 0; k < K; k++)
                {
                    Complex c = e[i * K + k] * kappa[k] * alpha[k];
                    if (c == Complex.Zero) continue;
                    var ak = a[k];
                    for (int n = 0; n < count; n++) q[n] += c * ak[n];
                }
                Complex dot = 0;
                for (int n = 0; n < count; n++) dot += q[n] * w[n];
                for (int n = 0; n < count; n++) next[n] += Complex.Conjugate(q[n]) * dot;
            }
            for (int n = 0; n < count; n++) if (w[n] != Complex.Zero || next[n] != Complex.Zero) w[n] = next[n] == Complex.Zero ? w[n] : Complex.FromPolarCoordinates(1, next[n].Phase);
        }
        double last = Metric();
        for (int it = 0; it < 40; it++)
        {
            control?.ThrowIfCancellationRequested();
            if (txEnd.Steered) Update(wt, aTx, wr, aRx, nR, nt);
            if (rxEnd.Steered) Update(wr, aRx, wt, aTx, nT, nr);
            double now = Metric();
            if (Math.Abs(now - last) <= 1e-9 * Math.Max(now, 1e-300)) { last = Math.Max(now, last); break; }
            last = Math.Max(now, last);
        }
        return last;
    }

    private static List<BeamRay> RaysOf(IReadOnlyList<PropagationChannelModel.RayEnds> ends, Complex[] s)
    {
        double top = s.Select(x => x.Magnitude * x.Magnitude).DefaultIfEmpty(0).Max();
        static double Deg(double rad) => rad * 180 / Math.PI;
        return [.. ends.Select((x, k) => new BeamRay(Deg(x.TxTheta), Deg(x.TxPhi), Deg(x.RxTheta), Deg(x.RxPhi),
                                                     top > 0 ? 10 * Math.Log10(s[k].Magnitude * s[k].Magnitude / top) : double.NegativeInfinity))];
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The default codebook: (u, v) on a grid of the beamwidths within ScanLimit, sorted by v then u.</summary>
    internal static List<(double ThetaDeg, double PhiDeg)> Grid(double du, double dv, double scanLimitDeg)
    {
        double lim = Math.Sin(scanLimitDeg * Math.PI / 180);
        int iu = (int)Math.Floor(lim / du + 1e-9), iv = (int)Math.Floor(lim / dv + 1e-9);
        var list = new List<(double, double)>();
        for (int j = -iv; j <= iv; j++)
            for (int i = -iu; i <= iu; i++)
            {
                double u = i * du, v = j * dv, rho = Math.Sqrt(u * u + v * v);
                if (rho > lim + 1e-12) continue;
                double theta = Math.Asin(Math.Min(1, rho)) * 180 / Math.PI;
                double phi = rho == 0 ? 0 : Math.Atan2(v, u) * 180 / Math.PI;
                list.Add((theta, phi));
            }
        return list;
    }

    private static BeamEntry Entry(int index, double thetaDeg, double phiDeg, string? label)
    {
        double th = thetaDeg * Math.PI / 180, ph = phiDeg * Math.PI / 180;
        double u = double.IsNaN(th) ? 0 : Math.Sin(th) * Math.Cos(ph), v = double.IsNaN(th) ? 0 : Math.Sin(th) * Math.Sin(ph);
        string at = double.IsNaN(thetaDeg) ? "" : string.Format(CultureInfo.InvariantCulture, " ({0:0.##}°, {1:0.##}°)", thetaDeg, phiDeg);
        return new BeamEntry(index, thetaDeg, phiDeg, u, v, label is null ? $"#{index}{at}" : label + (label.Contains('(') ? "" : at));
    }

    private static int Nearest(End end, double thetaRad, double phiRad)
    {
        if (end.Entries.Count == 1 || double.IsNaN(thetaRad)) return 0;
        double u = Math.Sin(thetaRad) * Math.Cos(phiRad), v = Math.Sin(thetaRad) * Math.Sin(phiRad);
        return Enumerable.Range(0, end.Entries.Count).MinBy(i => Math.Pow((end.Entries[i].U - u) / end.BwU, 2) + Math.Pow((end.Entries[i].V - v) / end.BwV, 2));
    }

    private static string Word(BeamSelectMode m) => m.ToString().ToLowerInvariant();

    private static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ when n % 100 is >= 11 and <= 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };

    /// <summary>R-pch9-6 — the table as explain prints it: the choice, the bound, and the strongest <paramref name="rows"/> pairs.</summary>
    public static IReadOnlyList<string> Lines(BeamTable t, int rows = 10)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            string.Format(inv, "beam: {0} — {1}, {2:0.00} dB; bound (phase-only, unconstrained) {3:0.00} dB, codebook loss {4:0.00} dB",
                          t.Label(t.Chosen), t.Why, t.Chosen.MetricDb, t.BoundDb, t.CodebookLossDb),
            string.Format(inv, "  codebooks: transmit {0} beam(s){1}, receive {2} beam(s){3}; {4} of {5} pairs scored ({6}), {7} frequency sample(s){8}",
                          t.TxBeams.Count, t.TxSteered ? "" : " (fixed)", t.RxBeams.Count, t.RxSteered ? "" : " (fixed)", t.PairsScored, t.PairsTotal, t.Search,
                          t.FrequencySamples, t.BandwidthHz > 0 ? string.Format(inv, " across {0:G4} Hz", t.BandwidthHz) : " at the carrier"),
            "  the selection uses the linear pattern model (array factor and the centre element's pattern); the run is the full circuit",
        };
        foreach (var p in t.Pairs.Where(p => p.Rank is not null).Take(rows))
            lines.Add(string.Format(inv, "  rank {0}: {1}, {2:0.00} dB{3}", p.Rank, t.Label(p), p.MetricDb, ReferenceEquals(p, t.Chosen) ? "  ← chosen" : ""));
        if (t.Chosen.Rank is null) lines.Add(string.Format(inv, "  chosen: {0}, {1:0.00} dB (not a distinct pair)", t.Label(t.Chosen), t.Chosen.MetricDb));
        return lines;
    }
}
