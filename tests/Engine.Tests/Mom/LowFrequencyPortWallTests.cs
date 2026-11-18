// LFP — the de-embedding error floor stops under-predicting, and the band edge it names follows the
// law of the term that binds.
//
// `docs/sonnet-briefs/brief-em-lf2-port-discontinuity-as-a-lumped-element.md` §"What must SHIP".
// PEEL derived `DeembedErrorFloor` on a UNIFORM LINE, where the DUT's own discretisation error is
// the same shape as the standards' and cancels, so the standards' `ConsistencyResidual` was the
// whole of it. On the shipped MMIC spiral it does not cancel, and the published floor read 1.106e-2
// at 320 MHz against a realised error of about 0.5 — 40× low, on a row that is 9× wrong, and
// therefore a per-point guard that stayed silent on exactly the rows it exists for.
//
// EVERY NUMBER BELOW IS MEASURED AND NONE OF IT IS SOLVED HERE. The floor is a pure function of
// three scalars already on `LineErrorBox`, so the DECISIONS — which term binds, what the floor
// then reads, which threshold it crosses, which band edge the remedy sentence can offer — are
// arithmetic and belong in the routine tier at zero cost. The spiral's own literals come from
// `src/Engine/Mom/RESOLVED.md` §LFP §4; `LowFrequencyPortWallPhysicsTests` is what re-solves them.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class LowFrequencyPortWallTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>
    /// A box with the stated <c>ConsistencyResidual</c> and <c>PeelAmplification</c> and nothing
    /// else pinned. <c>PeelAmplification</c> is <c>|a₁₁|/|a₂₁|²</c>, so a₁₁ = 1 and a₂₁ = 1/√amp
    /// gives it exactly — the two scalars the floor is made of, with no solve behind them.
    /// </summary>
    private static LineErrorBox Box(double residual, double amplification) =>
        new(Complex.One, Complex.One, new Complex(1.0 / Math.Sqrt(amplification), 0), residual, 0);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The floor itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The floor is the LARGER of the two shares, times the one amplifier.</b> The peel divides
    /// by a₂₁², so an absolute error anywhere in the raw s-parameters it consumes reaches the answer
    /// multiplied by <see cref="LineErrorBox.PeelAmplification"/> — whether it is the standards
    /// disagreeing with each other or the DUT's own raw solve being what it is.
    /// </summary>
    [Theory]
    // residual well above the constant: PEEL's own arithmetic, unchanged
    [InlineData(7.08e-08, 1.70e+05, false)]
    [InlineData(5.55e-04, 1.33e+01, false)]
    [InlineData(2.00e-08, 1.00e+06, false)]   // exactly equal — the residual still wins, no NaN edge
    // residual below it: the DUT's share binds, which is the case that reached a user
    [InlineData(1.065297e-09, 1.037790e+07, true)]
    [InlineData(0.0, 1.0e+06, true)]          // a perfectly consistent pair of standards is not a
                                              // perfect instrument, and used to publish a floor of 0
    public void TheFloorTakesTheLargerOfTheStandardsShareAndTheDuts(
        double residual, double amplification, bool dutBound)
    {
        var b = Box(residual, amplification);
        Assert.Equal(amplification, b.PeelAmplification, amplification * 1e-12);
        Assert.Equal(dutBound, b.FloorIsDutBound);

        // The amplifier is read off the box rather than off the InlineData, because Box() gets it
        // there through a square root: the claim under test is about the floor's two TERMS, not
        // about that round trip.
        Assert.Equal(Math.Max(residual, LineErrorBox.DutRawErrorFloor) * b.PeelAmplification,
                     b.DeembedErrorFloor);

        // and the half that must not move: where the standards' share is the larger, the floor is
        // the product PEEL shipped, to the last bit.
        if (!dutBound) Assert.Equal(residual * b.PeelAmplification, b.DeembedErrorFloor);
    }

    /// <summary>
    /// <b>The defect, and the fix, on the reported part's own measured numbers.</b> Four rows of the
    /// shipped <c>examples/PDK PCells</c> spiral whose σ_max(S) &gt; 1 proves they are not a network
    /// — so the realised error is not in dispute — against the floor that was published for them.
    ///
    /// <para><b>Both columns are asserted.</b> That the new floor fires is half the claim; that the
    /// OLD one did not is the other half, and it is what says this test is about a defect rather
    /// than about a threshold someone moved.</para>
    /// </summary>
    [Theory]
    //          f (Hz)   residual        amplification   realised |ΔS|
    [InlineData(0.16e9, 5.365022e-10, 3.746865e+07, 0.758)]
    [InlineData(0.32e9, 1.065297e-09, 1.037790e+07, 0.499)]
    [InlineData(0.48e9, 1.578607e-09, 4.727378e+06, 0.359)]
    [InlineData(0.64e9, 2.068867e-09, 2.683773e+06, 0.278)]
    public void TheSpiralsBadRows_UsedToReadUnderBudget_AndNowDoNot(
        double fHz, double residual, double amplification, double realised)
    {
        var b = Box(residual, amplification);
        double was = residual * amplification;

        _out.WriteLine($"{fHz / 1e9:F2} GHz: was {was:0.000e+00}, is {b.DeembedErrorFloor:0.000e+00}, " +
                       $"realised {realised:F3} — under-predicted by {realised / was:F0}×, now by " +
                       $"{realised / b.DeembedErrorFloor:F1}×");

        Assert.True(was < PlanarSolve.PeelErrorBudgetDS,
            $"the whole defect is that {fHz / 1e9:F2} GHz used to read {was:0.000e+00}, under the " +
            $"{PlanarSolve.PeelErrorBudgetDS} budget, while being wrong by {realised:F2}");
        Assert.True(b.DeembedErrorFloor >= PlanarSolve.PeelErrorBudgetDS,
            $"{fHz / 1e9:F2} GHz must now be named; floor is {b.DeembedErrorFloor:0.000e+00}");

        // A FLOOR, which means it may under-predict and must never over-claim. It does both here:
        // 0.99× of the realised error at the bottom row and 0.19× at the top of the four.
        Assert.InRange(b.DeembedErrorFloor / realised, 0.0, 1.05);
    }

    /// <summary>
    /// <b>The one row of that spiral the guard leaves alone is the one the run's own answer is right
    /// on.</b> 2.08 GHz publishes 3.48 nH against a Wheeler estimate of 2.48 nH and is passive;
    /// 8 GHz is where the standards' residual takes over again and the floor is PEEL's untouched.
    /// </summary>
    [Theory]
    [InlineData(2.08e9, 4.186218e-09, 2.568864e+05, true)]
    [InlineData(8.00e9, 1.145314e-07, 1.733549e+04, false)]
    public void TheSpiralsGoodRowsStaySilent(double fHz, double residual, double amplification,
                                             bool dutBound)
    {
        var b = Box(residual, amplification);
        _out.WriteLine($"{fHz / 1e9:F2} GHz: floor {b.DeembedErrorFloor:0.000e+00}, dut-bound {b.FloorIsDutBound}");
        Assert.Equal(dutBound, b.FloorIsDutBound);
        Assert.True(b.DeembedErrorFloor < PlanarSolve.PeelErrorBudgetDS);
        if (!dutBound) Assert.Equal(residual * b.PeelAmplification, b.DeembedErrorFloor);
    }

    /// <summary>
    /// <b>PEEL's own uniform-line fixtures keep the floors it recorded, because on them the
    /// standards' residual is the larger term.</b> The (residual, |a₂₁|²) pairs RESOLVED.md §PEEL §1
    /// tabulates against the floor beside them: three stacks, four mesh densities, three decades.
    /// The 6 % tolerance is that table's own three significant figures and the |a₁₁| it carries,
    /// which is near 1 and not 1.
    ///
    /// <para>This is the arithmetic behind why <c>PeelConditioningTests</c> re-solves those fixtures
    /// and comes back unchanged. <b>The one row that does NOT is carved out below</b>, because a
    /// fixture that only just stays on the old side of a new term is exactly the one a reader needs
    /// told about.</para>
    /// </summary>
    [Theory]
    [InlineData(7.08e-08, 5.90e-06, 1.20e-02)]   // board 0.6 mm coarse, 10 MHz
    [InlineData(9.63e-07, 5.70e-04, 1.69e-03)]   // ...100 MHz
    [InlineData(2.97e-07, 8.53e-07, 3.48e-01)]   // board 4× mesh, 10 MHz
    [InlineData(2.13e-06, 2.64e-05, 8.06e-02)]   // FR-4 1.6 mm coarse, 10 MHz
    [InlineData(5.55e-04, 7.54e-02, 7.03e-03)]   // FR-4 2× mesh, 1 GHz
    [InlineData(6.45e-08, 4.54e-04, 1.42e-04)]   // GaAs 0.1 mm coarse, 300 MHz
    public void EveryUniformLineFixturePeelMeasuredKeepsTheFloorItRecorded(
        double residual, double a21Squared, double recorded)
    {
        var b = Box(residual, 1.0 / a21Squared);
        Assert.False(b.FloorIsDutBound,
            $"a residual of {residual:0.00e+00} is above {LineErrorBox.DutRawErrorFloor:0.0e+00}, " +
            "so the standards' share must still be what sets this fixture's floor");
        Assert.Equal(residual * b.PeelAmplification, b.DeembedErrorFloor);
        Assert.Equal(recorded, b.DeembedErrorFloor, recorded * 0.06);
    }

    /// <summary>
    /// <b>The one PEEL fixture the new term reaches, and it reaches it by 10 %.</b> GaAs 0.1 mm at
    /// the coarse mesh and 10 MHz is the smallest residual in that whole table — 1.82e-8, just
    /// under <see cref="LineErrorBox.DutRawErrorFloor"/> — so its floor moves from 4.22e-2 to
    /// 4.63e-2.
    ///
    /// <para><b>It changes no verdict and no published s-parameter.</b> Both readings are over the
    /// 0.25 refusal threshold's tenth and under it, on the same side of both thresholds, which is
    /// why <c>PeelConditioningTests</c> re-solves this stack and comes back green. It is recorded
    /// here rather than left to be discovered, because "no uniform line moved at all" would have
    /// been the easy claim and it is not quite true.</para>
    /// </summary>
    [Fact]
    public void TheTightestUniformLineFixtureIsTheOneTheNewTermJustReaches()
    {
        var b = Box(1.82e-08, 1.0 / 4.32e-07);
        _out.WriteLine($"GaAs 0.1 mm coarse, 10 MHz: was {1.82e-08 * b.PeelAmplification:0.000e+00}, " +
                       $"is {b.DeembedErrorFloor:0.000e+00}");

        Assert.True(b.FloorIsDutBound);
        Assert.Equal(4.22e-2, 1.82e-08 * b.PeelAmplification, 4.22e-2 * 0.02);
        Assert.Equal(4.63e-2, b.DeembedErrorFloor, 4.63e-2 * 0.02);

        // the verdict, which is the thing that must not have moved
        Assert.True(1.82e-08 * b.PeelAmplification < PlanarSolve.PeelErrorBudgetDS);
        Assert.True(b.DeembedErrorFloor < PlanarSolve.PeelErrorBudgetDS);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The band edge the remedy sentence offers
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private static PlanarFrequencyPoint Point(double fHz, LineErrorBox box) =>
        new(fHz, new Mat<Complex>(1, 1), new Mat<Complex>(1, 1),
            [new PlanarPortCalibration(1, new LineCalibration.GammaResult(0, 0, 0, true),
                                       box, 50, 0)], 0, 0, 0);

    /// <summary>
    /// <b>The floor's two terms fall at different rates, so the band edge cannot be extrapolated
    /// without knowing which one is binding.</b> The standards' residual rises as ω against an
    /// |a₂₁|² that rises as ω², so that share falls as 1/f; the DUT's share is a constant over the
    /// same ω², so it falls as 1/f².
    ///
    /// <para><b>Getting it wrong is not cosmetic.</b> Reading the spiral's own 160 MHz row under the
    /// 1/f law names 2.4 GHz as the edge where the measured one is 663 MHz, and a user acting on it
    /// would throw away most of a band that answers.</para>
    /// </summary>
    [Fact]
    public void TheBandEdgeFollowsTheLawOfTheTermThatBinds()
    {
        // The spiral: five points, all DUT-bound, amplification measured at each.
        var spiral = new[]
        {
            Point(0.16e9, Box(5.365022e-10, 3.746865e+07)),
            Point(0.32e9, Box(1.065297e-09, 1.037790e+07)),
            Point(0.48e9, Box(1.578607e-09, 4.727378e+06)),
            Point(0.64e9, Box(2.068867e-09, 2.683773e+06)),
            Point(0.80e9, Box(2.527684e-09, 1.725147e+06)),
        };
        var p = PlanarSolve.MeasurePeelConditioning(spiral);
        Assert.NotNull(p);
        _out.WriteLine($"spiral: edge {p!.Value.BudgetMetAboveHz / 1e6:F1} MHz, " +
                       $"{p.Value.Unanswerable.Count} dropped, {p.Value.Flagged.Count} flagged");

        // Every one of the five reads back the SAME edge under the 1/f² law, to 7 %, which is what
        // says the law is the measurement's and not a curve fitted to it. Under 1/f they would span
        // 2.40 GHz down to 552 MHz — a factor of 4.3 — and the sentence would quote the largest.
        Assert.InRange(p.Value.BudgetMetAboveHz, 600e6, 700e6);

        // 0.16 GHz is over the refusal threshold and leaves the sweep; the other three bad rows are
        // named and published; 0.80 GHz, the first row whose σ_max is under 1, is neither.
        Assert.Equal([0.16e9], p.Value.Unanswerable);
        Assert.Equal([0.32e9, 0.48e9, 0.64e9], p.Value.Flagged);

        // And the OTHER branch still reads the 1/f law it always did: PEEL §7's reported board at
        // 95.64 MHz, whose floor of 0.241 is set by the standards' residual, named 461.3 MHz as its
        // edge and must still name it.
        var board = PlanarSolve.MeasurePeelConditioning([Point(95.64e6, Box(2.41e-07, 1.0e+06))]);
        Assert.NotNull(board);
        Assert.False(board!.Value.Unanswerable.Contains(95.64e6),
                     "0.241 is under the refusal threshold; PEEL published this point flagged");
        Assert.InRange(board.Value.BudgetMetAboveHz, 455e6, 465e6);
    }
}
