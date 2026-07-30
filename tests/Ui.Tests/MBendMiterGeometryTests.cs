using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-3.md §3 (R-L5h-6): "Use the calculators as an independent
/// oracle, not the eye." Both calculators the brief names by URL
/// (everythingrf.com/rf-calculators/microstrip-mitred-bend-calculator and
/// calctown.com/calculators/microstrip-optimal-mitre-bend) returned HTTP 403 Forbidden to every fetch
/// attempted during this brief — a real, reported constraint, not a silently-skipped gate. Two OTHER
/// independent, non-GPL microstrip mitred-bend calculators were reachable instead
/// (rfwireless-world.com/calculators/Microstrip-Mitred-Bend-Calculator.html and
/// calculatorultra.com/en/tool/microstrip-mitred-bend-calculator.html) and BOTH publish the identical
/// worked example below, which is what this file cross-checks our own math against — satisfying
/// R-L5h-6's actual intent (an independent numeric oracle, not the eye) even though it isn't literally
/// the two brief-named URLs.
///
/// <b>D and X (the two quantities <c>MBendPCell</c>/<c>MicrostripDiscontinuities</c> actually
/// compute) matched the fetched calculator's own worked example EXACTLY.</b> The brief's own §3.1
/// table also defines a third quantity, <b>A = D − X ("the remaining diagonal")</b> — this table's
/// own worked example DISPROVES that specific formula: calculatorultra.com states its own formula
/// verbatim as <c>A = (X − D/2)·√2</c>, which is NOT algebraically equal to <c>D − X</c> in general
/// (confirmed: for the worked example, D−X = 7.112 mil, but the calculator's own A = (X−D/2)·√2 =
/// 9.626 mil — the two numbers disagree, and the calculator's own value is what actually appears on
/// the page). This is reported here because R-L5h-6 asks for it explicitly, but it does not affect
/// this codebase's own correctness: <b>neither <c>MBendPCell</c> nor <c>MicrostripDiscontinuities</c>
/// ever compute or use "A" for anything</b> — the only quantity our own generator consumes is the
/// PER-EDGE LEG length (<c>MiterCutLength</c> returns <c>X/√2</c> directly), which is verified below
/// against the SAME fetched worked example (leg = W·M, cross-checked via X/√2).
///
/// <b>Calculator-oracle table</b> (D, X computed via <c>D = W√2</c>, <c>X = D·(0.52 +
/// 0.65·e^(−1.35·W/h))</c> — the exact formula both fetched calculators publish and this codebase's
/// own <see cref="MicrostripDiscontinuities.MiterCutLength"/> implements as <c>leg = X/√2 = W·M</c>):
///
/// | W | h | W/h | D | M (%) | X | leg = X/√2 | Source |
/// |---|---|---|---|---|---|---|---|
/// | 19.685 mil | 25 mil | 0.7874 | 27.8388 | 74.45% | 20.7266 | 14.6560 | fetched worked example (rfwireless-world.com + calculatorultra.com, identical) |
/// | 100 | 100 | 1.0000 | 141.4214 | 68.85% | 97.3695 | 68.8506 | R-L5h-5's own worked expectation ("≈69% at W/h=1") |
/// | 25 | 100 | 0.2500 | 35.3553 | 98.38% | 34.7829 | 24.5952 | Douville-James's own lower validity bound, W/h=0.25 |
/// | 2.9 mm | 1.6 mm | 1.8125 | 4.1012 mm | 57.63% | 2.3634 mm | 1.6712 mm | MBend's own default W on the PCB starter technology's own H |
///
/// (leg values are in the SAME unit as W/D/X in that row.)
/// </summary>
public sealed class MBendMiterGeometryTests
{
    private const double Sqrt2 = 1.4142135623730951;

    // ── The formula itself, cross-checked against the fetched worked example ────────────────────

    [Fact]
    public void MiterCutLength_MatchesTheFetchedCalculatorWorkedExample_19685milOver25mil()
    {
        // rfwireless-world.com AND calculatorultra.com both publish this exact worked example:
        // W=19.685 mil, h=25 mil -> D=27.838, X=20.726 (mil). Converted to metres for our API.
        double wMeters = 19.685e-3 * 0.0254; // mil -> metres
        double hMeters = 25e-3 * 0.0254;

        double leg = MicrostripDiscontinuities.MiterCutLength(wMeters, hMeters);

        double expectedDMil = 27.838, expectedXMil = 20.726;
        double expectedLegMil = expectedXMil / Sqrt2; // our own leg = X/√2 (never D-X, never a diagonal)
        double legMil = leg / 0.0254 * 1000;

        Assert.Equal(expectedLegMil, legMil, precision: 1);

        // D = W√2 independently, and X = leg*√2, both matching the fetched page's own numbers.
        double dMil = wMeters / 0.0254 * 1000 * Sqrt2;
        Assert.Equal(expectedDMil, dMil, precision: 2);
        Assert.Equal(expectedXMil, legMil * Sqrt2, precision: 1);
    }

    [Theory]
    // (W meters, h meters, expected D, expected X, expected leg — all in the SAME unit as W)
    [InlineData(0.100, 0.100, 0.1414213562, 0.0973694763, 0.0688506169)]   // W/h=1, R-L5h-5's own "~69%" case
    [InlineData(0.025, 0.100, 0.0353553391, 0.0347828931, 0.0245952196)]   // W/h=0.25, Douville-James's lower bound
    [InlineData(0.0029, 0.0016, 0.0041012193, 0.0023633949, 0.0016711725)] // MBend default W on Pcb2Layer's own H
    public void MiterCutLength_MatchesTheVerifiedFormula_AcrossThreeMoreWorkedPairs(
        double wMeters, double hMeters, double expectedD, double expectedX, double expectedLeg)
    {
        double leg = MicrostripDiscontinuities.MiterCutLength(wMeters, hMeters);
        Assert.Equal(expectedLeg, leg, precision: 8);

        double d = wMeters * Sqrt2;
        Assert.Equal(expectedD, d, precision: 8);
        Assert.Equal(expectedX, leg * Sqrt2, precision: 8);
    }

    [Fact]
    public void AtWOverHEqualsOne_TheRemovedLength_Is69PercentOfD_NotThe31PercentTheInvertedReadingWouldGive()
    {
        // R-L5h-5's own literal gate: "at W/h=1 the removed length is ~69% of D, NOT 31%."
        double wMeters = 0.001, hMeters = 0.001; // W/h = 1
        double leg = MicrostripDiscontinuities.MiterCutLength(wMeters, hMeters);
        double d = wMeters * Sqrt2;
        double x = leg * Sqrt2;
        double removedFractionOfD = x / d;

        Assert.True(removedFractionOfD is > 0.68 and < 0.70, $"expected ~69%, got {removedFractionOfD:P1}");
        Assert.False(removedFractionOfD is > 0.30 and < 0.32, "must not be the inverted (kept-not-removed) 31% reading");
    }

    // ── The generator-level fix: three modes, three distinct outlines, verified sizes ───────────

    [Fact]
    public void OptimalMiter_RemovesLegLength_MatchingTheFormula_ForTheDefaultMBend()
    {
        double wMeters = 0.0029; // MBend's own default W
        var result = MBendPCell.Generate(
            new Dictionary<string, double> { ["W"] = wMeters, ["Angle"] = 90.0, ["Miter"] = 2.0 },
            null, PCellLayerSelection.Default);

        // No technology resolved -> the W/h->infinity asymptote (leg = 0.52*W), per MBendPCell's own
        // documented fallback for "geometry is still generatable" with nothing resolved.
        double expectedLegMeters = MicrostripDiscontinuities.MiterCutLengthAsymptotic(wMeters);
        long expectedLegDbu = (long)Math.Round(expectedLegMeters * 1_000_000_000); // metres -> nm (DbuPerMicron=1000)

        var xy = ((PolygonShape)result.Shapes[0]).Xy;
        Assert.Equal(7, xy.Length / 2); // one sharp corner replaced by two cut vertices

        // The two NEW vertices (not present in the unmitered 6-vertex L) must each sit exactly
        // expectedLegDbu away from the sharp corner, along one arm's own outer edge.
        var none = MBendPCell.Generate(
            new Dictionary<string, double> { ["W"] = wMeters, ["Angle"] = 90.0, ["Miter"] = 0.0 },
            null, PCellLayerSelection.Default);
        var noneXy = ((PolygonShape)none.Shapes[0]).Xy;
        long sharpCornerX = noneXy[0], sharpCornerY = noneXy[1]; // (cornerX+halfW, -halfW), the outer corner

        // cut1 = sharpCorner - (leg, 0); cut2 = sharpCorner + (0, leg) for this 90° CCW example.
        Assert.Contains(System.Linq.Enumerable.Range(0, xy.Length / 2), i =>
            xy[i * 2] == sharpCornerX - expectedLegDbu && xy[i * 2 + 1] == sharpCornerY);
        Assert.Contains(System.Linq.Enumerable.Range(0, xy.Length / 2), i =>
            xy[i * 2] == sharpCornerX && xy[i * 2 + 1] == sharpCornerY + expectedLegDbu);
    }
}
