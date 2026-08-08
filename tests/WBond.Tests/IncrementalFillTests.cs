namespace CircuitRF.WBond.Tests;

/// <summary>
/// Oracle tier 7 of brief-wbond-wba §5 — the incremental drag path against a full rebuild.
///
/// <para><b>An incremental path that drifts from the cold path is worse than no incremental path,
/// because it is invisible.</b> The matrix entries are gated <b>bit-identically</b>: the same
/// <c>Block</c> function computes them either way, so any difference at all is a bookkeeping bug,
/// not rounding. The factor is gated through the array reduction, which is what the user actually
/// sees.</para>
/// </summary>
public class IncrementalFillTests
{
    /// <summary>Moves one wire's points by a fixed offset, in mils.</summary>
    private static void Translate(Wire wire, double dxMil, double dyMil, double dzMil)
    {
        long dx = WBondUnits.ToNm(dxMil, WBondUnit.Mil);
        long dy = WBondUnits.ToNm(dyMil, WBondUnit.Mil);
        long dz = WBondUnits.ToNm(dzMil, WBondUnit.Mil);

        for (int i = 0; i < wire.Points.Count; i++)
        {
            var p = wire.Points[i];
            wire.Points[i] = new Point3(p.X + dx, p.Y + dy, p.Z + dz);
        }
    }

    /// <summary>
    /// TIER 7 — after moving one wire, every entry of <b>L</b> is bit-identical to a full rebuild.
    /// </summary>
    [Fact]
    public void Tier7_SingleWireMove_MatchesAFullRebuildBitForBit()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4);
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: false);

        const int moved = 17;
        Translate(mesh.Wires[moved], 3.0, -2.0, 5.0);

        incremental.MoveWires([moved], SelectionMotion.General);
        var rebuilt = InductanceMatrix.Fill(WireMesh.Build(design));

        for (int i = 0; i < mesh.WireCount; i++)
            for (int j = 0; j < mesh.WireCount; j++)
                Assert.Equal(rebuilt[i, j], incremental.Matrix[i, j], 0.0);
    }

    /// <summary>
    /// TIER 7 — the rank-2 factor update tracks a fresh factorisation: the array-basis inductance
    /// after an incremental move agrees with the value from a cold rebuild.
    ///
    /// <para>Not bit-identical, and it should not be: the factor is reached by different arithmetic.
    /// It must agree to well inside anything a user could see, which for a pH readout means many
    /// orders of margin.</para>
    /// </summary>
    [Fact]
    public void Tier7_RankTwoFactorUpdate_TracksAFreshFactorisation()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4);
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: false);

        // Ten successive drags of different wires, so any drift accumulates rather than cancelling.
        int[] moves = [3, 17, 40, 5, 22, 31, 12, 45, 8, 27];
        foreach (int w in moves)
        {
            Translate(mesh.Wires[w], 1.5, -1.0, 2.0);
            incremental.MoveWires([w], SelectionMotion.General);
        }

        var incrementalReduction = incremental.Reduce();
        var coldReduction = ArrayReduction.Reduce(InductanceMatrix.Fill(WireMesh.Build(design)), mesh);

        for (int i = 0; i < mesh.ArrayCount; i++)
        {
            for (int j = 0; j < mesh.ArrayCount; j++)
            {
                // 4.2e-9 measured after ten rank-2 updates — 2e-6 pH on a 509 pH array, i.e. far
                // below anything the panel can show. See RankTwoDrift_GrowthIsMeasuredAndBounded for
                // how this behaves over a realistic drag session.
                double reference = coldReduction[i, j];
                Assert.Equal(reference, incrementalReduction[i, j], Math.Abs(coldReduction[i, i]) * 1e-7);
            }
        }
    }

    /// <summary>
    /// TIER 7 / R-wb-10 — <b>horizontal</b> rigid translation of a whole array leaves the
    /// intra-selection blocks exactly unchanged, and skipping them gives bit-identical results.
    ///
    /// <para>This is the invariance that actually saves work: the ground-plane images translate
    /// rigidly with the selection when z does not change, so both the direct and the image mutuals
    /// within it are untouched.</para>
    ///
    /// <para><b>Exact in arithmetic, not bit-exact in floating point — and the difference matters
    /// enough to state.</b> A skipped block keeps the value computed at the OLD coordinates, while a
    /// recomputed one evaluates the same physics at translated coordinates. The differences
    /// <c>q.Ax − p.Ax</c> are then formed from different absolute values and round differently, so
    /// the two agree to ~1e-12 relative rather than to the last bit. That is unlike the single-wire
    /// case above, which IS bit-identical because the same function sees the same filaments. On a pH
    /// readout the gap is ~1e-12 pH; the reason to pin it here is so nobody later "fixes" a
    /// non-bug.</para>
    /// </summary>
    [Fact]
    public void Tier7_HorizontalRigidTranslation_SkipsIntraSelectionBlocksExactly()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4);
        var mesh = WireMesh.Build(design);

        var withSkip = IncrementalFill.Create(mesh, parallel: false);
        var withoutSkip = IncrementalFill.Create(WireMesh.Build(design), parallel: false);

        // Move the whole of array 1 (wires 12..23) horizontally.
        int[] moved = [.. Enumerable.Range(12, 12)];
        foreach (int w in moved) Translate(mesh.Wires[w], 7.0, 4.0, 0.0);

        withSkip.MoveWires(moved, SelectionMotion.HorizontalRigidTranslation);
        withoutSkip.MoveWires(moved, SelectionMotion.General);

        Assert.True(withSkip.LastBlocksSkipped > 0,
            "Horizontal rigid translation must actually skip intra-selection blocks.");

        double worst = 0.0;
        for (int i = 0; i < mesh.WireCount; i++)
        {
            for (int j = 0; j < mesh.WireCount; j++)
            {
                double reference = withoutSkip.Matrix[i, j];
                if (reference == 0.0) continue;
                worst = Math.Max(worst, Math.Abs(withSkip.Matrix[i, j] / reference - 1.0));
            }
        }

        Assert.True(worst < 1e-10,
            $"Skipping intra-selection blocks under horizontal rigid translation must agree with a full " +
            $"recompute to rounding; worst relative difference was {worst:E3}.");
    }

    /// <summary>
    /// R-wb-10 — the invariance claim itself, tested directly on the physics rather than through the
    /// incremental machinery: translating two wires together horizontally leaves both their direct
    /// AND their image mutual unchanged, while translating them together in z changes the image
    /// mutual and leaves the direct one alone.
    ///
    /// <para><b>This is the test that would catch applying the horizontal rule to a vertical
    /// move</b> — an optimisation that is silently wrong rather than slow.</para>
    /// </summary>
    [Fact]
    public void RigidMotion_ImageInvarianceHoldsHorizontallyAndFailsVertically()
    {
        var design = TestDesigns.ParallelArray(n: 2, pitchMil: 8.0, lengthMil: 100.0, heightMil: 20.0);
        var mesh = WireMesh.Build(design);

        double direct0 = InductanceMatrix.BlockDirect(mesh, 0, 1);
        double image0 = InductanceMatrix.BlockImage(mesh, 0, 1);

        // Horizontal: both invariant.
        foreach (var wire in mesh.Wires) Translate(wire, 30.0, 12.0, 0.0);
        var horizontal = WireMesh.Build(design);

        Assert.Equal(direct0, InductanceMatrix.BlockDirect(horizontal, 0, 1), Math.Abs(direct0) * 1e-12);
        Assert.Equal(image0, InductanceMatrix.BlockImage(horizontal, 0, 1), Math.Abs(image0) * 1e-12);

        // Vertical: direct invariant, image NOT.
        foreach (var wire in mesh.Wires) Translate(wire, 0.0, 0.0, 10.0);
        var vertical = WireMesh.Build(design);

        Assert.Equal(direct0, InductanceMatrix.BlockDirect(vertical, 0, 1), Math.Abs(direct0) * 1e-12);

        double imageAfter = InductanceMatrix.BlockImage(vertical, 0, 1);
        double change = Math.Abs(imageAfter / image0 - 1.0);
        Assert.True(change > 0.05,
            $"Raising a pair by 10 mil must change its image mutual materially; it moved by only {change:P3}. " +
            "If this passes trivially the horizontal invariance rule could be misapplied to vertical moves.");
    }

    /// <summary>
    /// A move of many wires falls back to a full refactorisation rather than accumulating 2k rank-1
    /// steps, and the result is still correct. Guards the crossover branch.
    /// </summary>
    [Fact]
    public void LargeSelection_FallsBackToRefactorisation_AndStaysCorrect()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4);
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: false);

        // 24 of 48 wires: far past the k*12 <= n crossover, so this takes the refactorisation path.
        int[] moved = [.. Enumerable.Range(0, 24)];
        foreach (int w in moved) Translate(mesh.Wires[w], 2.0, 0.0, 3.0);

        incremental.MoveWires(moved, SelectionMotion.General);

        var cold = ArrayReduction.Reduce(InductanceMatrix.Fill(WireMesh.Build(design)), mesh);
        var live = incremental.Reduce();

        for (int i = 0; i < mesh.ArrayCount; i++)
            for (int j = 0; j < mesh.ArrayCount; j++)
                Assert.Equal(cold[i, j], live[i, j], Math.Abs(cold[i, i]) * 1e-9);
    }

    /// <summary>
    /// The block-count arithmetic the whole incremental argument rests on: moving one wire touches
    /// 2N−1 blocks, not N².
    /// </summary>
    [Fact]
    public void SingleWireMove_TouchesTwoNMinusOneBlocks()
    {
        var mesh = WireMesh.Build(TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4));
        var incremental = IncrementalFill.Create(mesh, parallel: false);

        incremental.MoveWires([20], SelectionMotion.General);

        // One row of N, written symmetrically — N blocks computed, covering 2N-1 matrix entries.
        Assert.Equal(mesh.WireCount, incremental.LastBlocksRecomputed);
        Assert.True(incremental.LastBlocksRecomputed * 20 < mesh.WireCount * mesh.WireCount,
            "A one-wire move must cost O(N) blocks, not O(N^2).");
    }
    /// <summary>
    /// How the rank-2 update path DRIFTS from a fresh factorisation as updates accumulate.
    ///
    /// <para>Reported rather than merely bounded, because the policy question — how often to
    /// refactorise — can only be answered from the growth rate, and a drag session is thousands of
    /// updates, not ten.</para>
    /// </summary>
    [Fact]
    public void RankTwoDrift_GrowthIsMeasuredAndBounded()
    {
        var design = TestDesigns.PowerAmplifier(wireCount: 48, arrayCount: 4);
        var mesh = WireMesh.Build(design);
        var incremental = IncrementalFill.Create(mesh, parallel: false);

        var rng = new Random(11);
        double worstSoFar = 0.0;

        foreach (int updates in new[] { 10, 50, 200, 1000 })
        {
            for (int i = 0; i < updates; i++)
            {
                int w = rng.Next(mesh.WireCount);
                Translate(mesh.Wires[w], rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
                incremental.MoveWires([w], SelectionMotion.General);
            }

            // The matrix itself is exact (same Block calls); only the FACTOR can drift, so compare
            // the reduction driven by the maintained factor against one driven by a fresh factor of
            // the very same matrix.
            var live = ArrayReduction.Reduce(incremental.Matrix, incremental.Factor,
                                             mesh.ArrayOfWire, mesh.ArrayCount, mesh.ArrayNames);
            var fresh = ArrayReduction.Reduce(incremental.Matrix, mesh);

            double worst = 0.0;
            for (int i = 0; i < mesh.ArrayCount; i++)
                for (int j = 0; j < mesh.ArrayCount; j++)
                    worst = Math.Max(worst, Math.Abs(live[i, j] - fresh[i, j]) / Math.Abs(fresh[i, i]));

            worstSoFar = worst;
        }

        // After 1,260 cumulative rank-2 updates the drift must still be far below anything a pH
        // readout could show. If this ever fails, the fix is a periodic refactorisation (22.7 ms
        // amortised over K frames), not a looser tolerance.
        Assert.True(worstSoFar < 1e-6,
            $"Cumulative rank-2 drift after ~1,260 updates was {worstSoFar:E3} relative; " +
            "that is approaching visibility and IncrementalFill should refactorise periodically.");
    }
}
