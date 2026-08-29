namespace CircuitRF.Core.Matching;

/// <summary>One transform as it was actually applied, after name resolution and clamping.</summary>
/// <param name="Record">The stored record, with <see cref="TransformRecord.N"/> updated to what ran.</param>
/// <param name="Range">The range recomputed at that point in the sequence.</param>
/// <param name="Clamped">True when the stored N had to be brought inside the range.</param>
public sealed record AppliedTransform(TransformRecord Record, TransformRange Range, bool Clamped);

/// <summary>The outcome of a sequential rebuild.</summary>
public sealed class MatchRebuildResult
{
    /// <summary>The basis synthesis. Its refusal, if any, is this result's refusal.</summary>
    public required MatchSynthesisResult Basis { get; init; }

    /// <summary>The finished network, with the §4.5/§4.6 end splits applied. Null on a refusal.</summary>
    public MatchNetwork? Network { get; init; }

    /// <summary>The transforms that ran, in order.</summary>
    public IReadOnlyList<AppliedTransform> Applied { get; init; } = [];

    /// <summary>Records whose element pair no longer resolves. Named in the Designer's banner.</summary>
    public IReadOnlyList<TransformRecord> Dropped { get; init; } = [];

    /// <summary>
    /// True when the design's stored <see cref="MatchDesign.BasisFingerprint"/> disagrees with the
    /// basis just synthesised — the synthesis changed underneath a stored design.
    /// </summary>
    public bool FingerprintMismatch { get; init; }

    /// <summary>The product of N^2 the applied transforms reached.</summary>
    public double Achieved { get; init; } = 1.0;

    /// <summary>The product of N^2 they had to reach.</summary>
    public double Required { get; init; } = 1.0;

    /// <summary>
    /// True when <see cref="Achieved"/> is on target to <see cref="MatchLinkage.RatioTolerance"/> —
    /// see that constant for why it is 1e-6 and not a floating-point equality test.
    /// </summary>
    public bool OnTarget => Math.Abs(Achieved / Required - 1.0) <= MatchLinkage.RatioTolerance;

    /// <summary>Anything the caller should be told but that is not a refusal.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// One entry per end carrying a DC block (match.md §22), applied or not. Empty for a design with
    /// no block, which is every design written before rev 6.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="Notes"/> on purpose.</b> Notes are things that went wrong-ish — a
    /// dropped transform, a clamped N, a stale fingerprint — and a component reports them as warnings
    /// through <c>IReportsWarnings</c>. A block is a design input working as asked, and what it has to
    /// say is a line of numbers the Designer's status strip renders in the user's own units.
    /// </remarks>
    public IReadOnlyList<DcBlockNote> DcBlocks { get; init; } = [];

    /// <summary>Non-null when the design could not be synthesised at all.</summary>
    public MatchRefusal? Refusal => Basis.Refusal;
}

/// <summary>
/// match.md §7.3's load path: <b>a sequential rebuild, not a snapshot restore</b>. The ladder is
/// derived from the design's inputs and the stored transforms are re-applied one at a time, each
/// against the network state the previous one left.
/// </summary>
public static class MatchRebuild
{
    /// <summary>Synthesises the basis and re-applies the design's stored transforms.</summary>
    public static MatchRebuildResult Rebuild(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var basis = MatchSynthesis.Synthesize(design);
        if (!basis.Ok) return new MatchRebuildResult { Basis = basis };

        var notes = new List<string>(basis.Notes);
        bool mismatch = !string.IsNullOrEmpty(design.BasisFingerprint)
                        && !string.Equals(design.BasisFingerprint, basis.BasisFingerprint, StringComparison.Ordinal);
        if (mismatch)
            notes.Add(
                "The basis ladder no longer matches the fingerprint stored with this design: the " +
                "synthesis has changed underneath it. The stored transforms were re-applied by name " +
                "and nothing was discarded, but the element values will differ from last time.");

        var seq = ApplySequence(basis, design.Transforms, design.AllowNegativeComponents);
        notes.AddRange(seq.Notes);

        var network = MatchSynthesis.WithEndSplits(seq.Network, basis, design);

        // ── The DC block, LAST (match.md §22.2) ──────────────────────────────
        //
        // After the transforms and after the end splits, because the block attaches to whichever
        // shunt inductor sits at the end node once both have run — a Norton pi on the first pair
        // replaces L1 by a product that is still a shunt inductor there, and WithEndSplits can insert
        // a Fano or detune element beside it. Resolving by NODE rather than by name is what makes
        // that work with nothing to keep in step, and it is why the synthesis, the solution search
        // and both fingerprints never see the block at all.
        network = MatchDcBlock.Apply(network, design, design.Omega0, out var blocks);

        return new MatchRebuildResult
        {
            Basis = basis,
            Network = network,
            DcBlocks = blocks,
            Applied = seq.Applied,
            Dropped = seq.Dropped,
            FingerprintMismatch = mismatch,
            Achieved = seq.Achieved,
            Required = basis.RequiredTransformRatio,
            Notes = notes,
        };
    }

    /// <summary>The result of walking a transform list against a basis.</summary>
    /// <param name="Network">The network after every applicable transform.</param>
    /// <param name="Applied">What ran.</param>
    /// <param name="Dropped">What could not be resolved.</param>
    /// <param name="Achieved">The product of N^2.</param>
    /// <param name="Notes">Why anything was dropped or clamped.</param>
    /// <param name="GuardFired">True when an absolute value guard acted anywhere.</param>
    public sealed record SequenceResult(
        MatchNetwork Network,
        IReadOnlyList<AppliedTransform> Applied,
        IReadOnlyList<TransformRecord> Dropped,
        double Achieved,
        IReadOnlyList<string> Notes,
        bool GuardFired);

    /// <summary>
    /// Applies a transform list to a basis, resolving each pair <b>by name</b> against the current
    /// state and recomputing its range there.
    /// </summary>
    /// <remarks>
    /// <b>Name-keying is the point.</b> Positional indices round-trip correctly only while the basis
    /// ladder comes out byte-identical forever; since it is derived, any future change to the
    /// synthesis would re-point every transform at different elements and produce a different network
    /// with no error anywhere. Names cost nothing and make the failure detectable.
    /// </remarks>
    public static SequenceResult ApplySequence(
        MatchSynthesisResult basis, IReadOnlyList<TransformRecord> records, bool allowNegative)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(records);

        var net = basis.Network!.Clone();
        var applied = new List<AppliedTransform>();
        var dropped = new List<TransformRecord>();
        var notes = new List<string>();
        double achieved = 1.0;
        bool guard = false;
        int ordinal = 0;

        foreach (var rec in records)
        {
            ordinal++;
            var pair = NortonTransform.Discover(net).FirstOrDefault(
                p => (p.NameA == rec.ElementA && p.NameB == rec.ElementB)
                     || (p.NameA == rec.ElementB && p.NameB == rec.ElementA));
            if (pair is null)
            {
                dropped.Add(rec);
                notes.Add(
                    $"The transform on {rec.ElementA}/{rec.ElementB} was dropped: that pair no longer " +
                    "exists in the ladder. The remaining transforms were re-linked.");
                continue;
            }

            // A pair Apply cannot realise is DROPPED WITH A NOTE, never thrown out of here. Discover
            // and Apply have to agree about which pairs can be made adjacent, and when they have not
            // the honest report is the one a rebuild already makes for a pair that has vanished —
            // the Designer shows the note, keeps the rest of the sequence, and stays open. An
            // escaping exception is an application crash from a menu click (owner-reported,
            // 2026-08-20); the disagreement itself is fixed at its source in NortonTransform.Discover.
            TransformApplication app;
            try
            {
                app = NortonTransform.Apply(
                    net, pair, rec.N, rec.Form, basis.AnalysisIsTerm1, allowNegative, ordinal);
            }
            catch (InvalidOperationException e)
            {
                dropped.Add(rec);
                notes.Add(
                    $"The transform on {rec.ElementA}/{rec.ElementB} was dropped: it cannot be " +
                    $"applied to the ladder as it now stands. {e.Message}");
                continue;
            }

            if (app.Clamped)
                notes.Add(
                    $"N on {rec.ElementA}/{rec.ElementB} was {rec.N:0.#####}, outside the range " +
                    $"[{app.Range.Min:0.#####}, {app.Range.Max:0.#####}] it has here; " +
                    $"{app.NUsed:0.#####} was used.");
            guard |= app.GuardFired;
            net = app.Network;
            achieved *= app.NUsed * app.NUsed;
            applied.Add(new AppliedTransform(rec with { N = app.NUsed }, app.Range, app.Clamped));
        }

        return new SequenceResult(net, applied, dropped, achieved, notes, guard);
    }
}
