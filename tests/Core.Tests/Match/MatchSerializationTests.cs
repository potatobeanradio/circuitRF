using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §7: the design blob, and the sequential rebuild that is what "everything I set is still
/// there" actually means.
/// </summary>
public class MatchSerializationTests
{
    private const double F1 = 3.3e9, F2 = 5.0e9;

    private static MatchDesign WithTwoTransformsAndAQAdjust()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var set = MatchSolutionSearch.Search(d, includeQAdjust: true);
        var chosen = set.Solutions.First(s => s.QAdjust > 0 && s.Transforms.Count >= 2);

        d.QAdjust = chosen.QAdjust;
        d.LinkTransforms = true;
        d.AppliedSolutions = [chosen.Fingerprint];
        d.Transforms =
        [
            chosen.Transforms[0] with { Form = TransformForm.Pi, Locked = false },
            chosen.Transforms[1] with { Form = TransformForm.T, Locked = true },
        ];
        d.BasisFingerprint = MatchSynthesis.Synthesize(d).BasisFingerprint;
        return d;
    }

    [Fact]
    public void EncodedPayload_IsOneUnpaddedBareToken()
    {
        string payload = MatchEmbedding.Encode(MatchAbcdOracle.GoldenDesign());
        Assert.DoesNotContain('=', payload);
        Assert.DoesNotContain(' ', payload);
        Assert.DoesNotContain('"', payload);
        Assert.DoesNotContain('\n', payload);
    }

    [Fact]
    public void BlobRoundTrip_ReSynthesisesAnIdenticalLadder()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.Encode(d), out var back));

        var a = MatchSynthesis.Synthesize(d);
        var b = MatchSynthesis.Synthesize(back!);
        Assert.Equal(a.BasisFingerprint, b.BasisFingerprint);
        Assert.Equal(a.G, b.G);
        Assert.Equal(a.Network!.Elements.Select(e => (e.Name, e.Value)),
                     b.Network!.Elements.Select(e => (e.Name, e.Value)));
    }

    [Fact]
    public void TryDecode_AcceptsRawJsonAndAPaddedPayload()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.Write(d), out var fromJson));
        Assert.Equal(d.Order, fromJson!.Order);

        string padded = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(MatchEmbedding.Write(d)));
        Assert.True(MatchEmbedding.TryDecode(padded, out var fromPadded));
        Assert.Equal(d.Order, fromPadded!.Order);
    }

    [Fact]
    public void TryDecode_ReturnsFalseRatherThanThrowing()
    {
        Assert.False(MatchEmbedding.TryDecode(null, out _));
        Assert.False(MatchEmbedding.TryDecode("   ", out _));
        Assert.False(MatchEmbedding.TryDecode("not base64 and not json!!", out _));
        Assert.False(MatchEmbedding.TryDecode("{ \"Order\": ", out _));
    }

    [Fact]
    public void SessionRoundTrip_RestoresEveryChoiceTheUserMade()
    {
        var d = WithTwoTransformsAndAQAdjust();
        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.Encode(d), out var back));

        // Everything the user set, verbatim.
        Assert.Equal(d.QAdjust, back!.QAdjust);
        Assert.True(back.LinkTransforms);
        Assert.Equal(d.AppliedSolutions, back.AppliedSolutions);
        Assert.Equal(TransformForm.Pi, back.Transforms[0].Form);
        Assert.Equal(TransformForm.T, back.Transforms[1].Form);
        Assert.False(back.Transforms[0].Locked);
        Assert.True(back.Transforms[1].Locked);
        Assert.Equal(d.Transforms.Select(t => t.N), back.Transforms.Select(t => t.N));

        // ... and the rebuild it drives is identical, element for element.
        var a = MatchRebuild.Rebuild(d);
        var b = MatchRebuild.Rebuild(back);
        Assert.False(a.FingerprintMismatch);
        Assert.Empty(a.Dropped);
        Assert.Equal(2, a.Applied.Count);
        Assert.Equal(a.Applied.Select(x => x.Record.N), b.Applied.Select(x => x.Record.N));
        Assert.Equal(a.Network!.Elements.Select(e => (e.Name, e.Value)),
                     b.Network!.Elements.Select(e => (e.Name, e.Value)));
        Assert.Equal(a.Network.R1, b.Network.R1);

        foreach (double f in MatchAbcdOracle.Band(F1, F2))
        {
            var sa = MatchAbcdOracle.S(a.Network, f);
            var sb = MatchAbcdOracle.S(b.Network, f);
            Assert.Equal(sa.S11, sb.S11);
            Assert.Equal(sa.S21, sb.S21);
        }
    }

    [Fact]
    public void ADetuneElementIsProducedAtTheAnalysisEndWhenQIsAdjusted()
    {
        // A deliberately inflated analysis-end Q, well above the termination's own 3.1345. The
        // MINIMUM Q-adjust the search finds for the golden problem is essentially the true Q (the
        // design already completes without one), so this is set explicitly rather than taken from
        // the search - otherwise the test would be measuring a split that correctly does not happen.
        var d = MatchAbcdOracle.GoldenDesign();
        d.QAdjust = 6.0;

        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(6.0, r.QAnalysis);
        Assert.Equal(3.1345, r.QAnalysisActual, 1e-4);

        var split = MatchSynthesis.WithEndSplits(r.Network!, r, d);
        var detune = Assert.Single(split.Elements, e => e.IsDetune);
        Assert.Equal("CDetune", detune.Name);

        // The analysis end is never rescaled by a transform, so its own 10 pF comes back out of the
        // split exactly, and the arm total - and therefore the response - is untouched.
        var kept = split.Elements.Single(e => e.AbsorbedEnd == 2);
        Assert.Equal(10e-12, kept.Value, 10e-12 * 1e-12);
        foreach (double f in MatchAbcdOracle.Band(F1, F2))
            Assert.True((MatchAbcdOracle.S(r.Network!, f).S21 - MatchAbcdOracle.S(split, f).S21).Magnitude < 1e-12);
    }

    [Fact]
    public void ABasisChange_IsFlaggedAndNothingSilentlyRePoints()
    {
        var d = WithTwoTransformsAndAQAdjust();
        string honest = d.BasisFingerprint!;
        d.BasisFingerprint = "0000000000000000";

        var r = MatchRebuild.Rebuild(d);
        Assert.True(r.FingerprintMismatch);
        Assert.Contains(r.Notes, s => s.Contains("fingerprint", StringComparison.Ordinal));

        // Flagged, but not discarded: the pairs still resolve BY NAME and the values are what the
        // honest fingerprint would have given.
        Assert.Empty(r.Dropped);
        d.BasisFingerprint = honest;
        var ok = MatchRebuild.Rebuild(d);
        Assert.False(ok.FingerprintMismatch);
        Assert.Equal(ok.Network!.Elements.Select(e => (e.Name, e.Value)),
                     r.Network!.Elements.Select(e => (e.Name, e.Value)));
    }

    [Fact]
    public void ATransformWhoseElementsAreGone_IsDroppedAndNamed()
    {
        // This is the failure the reference implementation's positional index CANNOT report: at
        // order 2 the ladder has four elements, so a transform naming L3/L4 would silently land on
        // whatever sat at those positions instead.
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 2;
        d.Transforms = [new TransformRecord("L3", "L4", TransformForm.Pi, 2.0, false)];

        var r = MatchRebuild.Rebuild(d);
        Assert.Empty(r.Applied);
        var dropped = Assert.Single(r.Dropped);
        Assert.Equal("L3", dropped.ElementA);
        Assert.Contains(r.Notes, s => s.Contains("L3/L4", StringComparison.Ordinal));

        // The basis is untouched, so the response is the basis response.
        var basis = MatchSynthesis.Synthesize(d);
        foreach (double f in MatchAbcdOracle.Band(F1, F2))
            Assert.True((MatchAbcdOracle.S(basis.Network!, f).S21
                         - MatchAbcdOracle.S(r.Network!, f).S21).Magnitude < 1e-12);
    }

    [Fact]
    public void AStoredNOutsideItsRange_IsClampedAndSaidSo()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Transforms = [new TransformRecord("L1", "L2", TransformForm.Pi, 1e6, false)];

        var r = MatchRebuild.Rebuild(d);
        var applied = Assert.Single(r.Applied);
        Assert.True(applied.Clamped);
        Assert.Equal(applied.Range.Max, applied.Record.N, 1e-12);
        Assert.Contains(r.Notes, s => s.Contains("outside the range", StringComparison.Ordinal));
    }
}
