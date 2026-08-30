using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CSparse.Storage;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// SP-P2 gate: <see cref="MnaSystem"/> records the stamp sequence on the first pass and writes
/// values straight into the CSC value array afterwards. The cached pass must produce a matrix that
/// is indistinguishable — structure AND values, entry by entry — from a FRESH MnaSystem stamped at
/// the same frequency, and it must notice the cases where the sequence legitimately changes
/// (ω = 0 for an ideal inductor, a regularization retry adding gmin stamps that were not recorded).
/// </summary>
public class MnaPatternCacheTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>
    /// The same two-phase visit order SParameterEngine.StampAll uses (non-mutual first so
    /// InductorModel.LastBranchIndex is set before MutualInductanceModel reads it), so the recorded
    /// call sequence is the real one.
    /// </summary>
    private static void StampAll(MnaSystem mna, ElaboratedNetlist nl, double omega)
    {
        mna.Reset();
        foreach (var ec in nl.Components)
        {
            if (ec.Model is MutualInductanceModel) continue;
            if (ec.Model.Kind == ModelKind.Nonlinear)
            {
                ec.StampLinearized(mna, omega, new PortVoltages(new double[ec.Model.PortCount]));
                continue;
            }
            ec.Stamp(mna, omega);
        }
        foreach (var ec in nl.Components)
            if (ec.Model is MutualInductanceModel)
                ec.Stamp(mna, omega);
    }

    private static void AssertSameMatrix(
        CompressedColumnStorage<Complex> expected,
        CompressedColumnStorage<Complex> actual,
        string what)
    {
        Assert.Equal(expected.RowCount,    actual.RowCount);
        Assert.Equal(expected.ColumnCount, actual.ColumnCount);
        Assert.Equal(expected.ColumnPointers, actual.ColumnPointers);

        int nnz = expected.ColumnPointers[expected.ColumnCount];
        Assert.Equal(nnz, actual.ColumnPointers[actual.ColumnCount]);
        for (int i = 0; i < nnz; i++)
        {
            Assert.Equal(expected.RowIndices[i], actual.RowIndices[i]);
            Assert.True(expected.Values[i] == actual.Values[i],
                $"{what}: entry {i} (row {expected.RowIndices[i]}) " +
                $"expected {expected.Values[i]} got {actual.Values[i]}");
        }
    }

    /// <summary>
    /// Stamp at ω₁, factorize, stamp at ω₂, factorize — then compare the ω₂ matrix against a fresh
    /// MnaSystem stamped only at ω₂. Bit-for-bit, not to a tolerance.
    /// </summary>
    private static void AssertCachedPassMatchesFresh(string cnl, double f1Hz, double f2Hz)
    {
        var nl = Elaborate(cnl);
        int n  = nl.Nodes.Count - 1;

        double w1 = 2 * Math.PI * f1Hz, w2 = 2 * Math.PI * f2Hz;

        var reused = new MnaSystem(n);
        StampAll(reused, nl, w1);
        reused.Factorize();                 // seals the pattern
        StampAll(reused, nl, w2);           // cached pass
        reused.Factorize();

        var fresh = new MnaSystem(n);
        StampAll(fresh, nl, w2);
        fresh.Factorize();

        Assert.Equal(fresh.Size, reused.Size);
        AssertSameMatrix(fresh.BuildCsc(), reused.BuildCsc(), "cached vs fresh");

        var bFresh  = fresh.BuildRhs();
        var bReused = reused.BuildRhs();
        Assert.Equal(bFresh.Length, bReused.Length);
        for (int i = 0; i < bFresh.Length; i++)
            Assert.True(bFresh[i] == bReused[i], $"RHS row {i}: {bFresh[i]} vs {bReused[i]}");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static string Hero1Cnl()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero1", "hero1.cnl");
            if (File.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("testdata/Hero1/hero1.cnl not found");
    }

    private const string MutualCnl = """
        Port:P1  n1 0  Num=1 Z=50 Ohm
        Port:P2  n2 0  Num=2 Z=50 Ohm
        Port:P3  n3 0  Num=3 Z=50 Ohm
        L:L1  n1 0  L=10 nH
        L:L2  n2 0  L=10 nH
        L:L3  n3 0  L=10 nH
        C:C1  n1 n2 C=1 pF
        Mutual:M12  M=3 nH  Inductor1="L1"  Inductor2="L2"
        Mutual:M23  M=3 nH  Inductor1="L2"  Inductor2="L3"
        Mutual:M13  M=-2 nH Inductor1="L1"  Inductor2="L3"
        """;

    private const string NonlinearCnl = """
        Port:P1  n1 0  Num=1  Z=50 Ohm
        Port:P2  n2 0  Num=2  Z=50 Ohm
        R:Rs     n1 n2 R=20 Ohm
        C:C1     n2 0  C=0.5 pF
        SDD:D1   n2 0  I[1]=_v1/75
        """;

    /// <summary>Reactive port reference impedance ⇒ SParameterEngine takes the legacy path.</summary>
    private const string ReactiveZ0Cnl = """
        Port:P1  n1 0  Num=1  Z=(0+50j) Ohm
        Port:P2  n2 0  Num=2  Z=(0+50j) Ohm
        L:L1     n1 n2 L=2 nH
        C:C1     n2 0  C=1 pF
        R:R1     n1 0  R=75 Ohm
        """;

    // ── T1: the cached pass reproduces a fresh assembly, entry for entry ───────

    [Fact]
    public void T1_Hero1_CachedPass_MatchesFreshAssembly()
    {
        var (lib, tb) = CnlReader.ReadFile(Hero1Cnl());
        var nl        = new Elaborator(lib).Elaborate(tb);
        int n         = nl.Nodes.Count - 1;

        double w1 = 2 * Math.PI * 1.0e9, w2 = 2 * Math.PI * 2.35e9;

        var reused = new MnaSystem(n);
        StampAll(reused, nl, w1);
        reused.Factorize();
        StampAll(reused, nl, w2);
        reused.Factorize();

        var fresh = new MnaSystem(n);
        StampAll(fresh, nl, w2);
        fresh.Factorize();

        AssertSameMatrix(fresh.BuildCsc(), reused.BuildCsc(), "Hero 1");
    }

    [Fact]
    public void T1b_MutualInductance_CachedPass_MatchesFreshAssembly()
        => AssertCachedPassMatchesFresh(MutualCnl, 1.0e9, 2.7e9);

    [Fact]
    public void T1c_NonlinearDevice_CachedPass_MatchesFreshAssembly()
        => AssertCachedPassMatchesFresh(NonlinearCnl, 1.0e9, 4.1e9);

    [Fact]
    public void T1d_ReactiveZ0Netlist_CachedPass_MatchesFreshAssembly()
        => AssertCachedPassMatchesFresh(ReactiveZ0Cnl, 1.0e9, 3.3e9);

    // ── T2: a sequence that legitimately changes (ω = 0 ideal inductor) ────────

    /// <summary>
    /// InductorModel skips its branch diagonal when jωL + R is exactly zero, so an ideal inductor's
    /// stamp sequence at DC differs from its sequence at any other frequency. Both orders of the
    /// grid must give exactly what a one-point-per-run solve gives.
    /// </summary>
    [Theory]
    [InlineData(new double[] { 0.0, 1.0e9, 2.0e9 })]
    [InlineData(new double[] { 1.0e9, 0.0, 2.0e9 })]
    [InlineData(new double[] { 1.0e9, 2.0e9, 0.0 })]
    public void T2_IdealInductorAcrossDc_SweptEqualsPointByPoint(double[] freqs)
    {
        const string cnl = """
            Port:P1  n1 0  Num=1 Z=50 Ohm
            Port:P2  n2 0  Num=2 Z=50 Ohm
            L:L1     n1 n2 L=5 nH
            R:R1     n2 0  R=50 Ohm
            """;

        var swept = SParameterEngine.Run(Elaborate(cnl), freqs);

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var one = SParameterEngine.Run(Elaborate(cnl), [freqs[fi]]);
            for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                Assert.True((Complex)swept["S"][fi, r, c] == (Complex)one["S"][0, r, c],
                    $"f={freqs[fi]:G6} S[{r},{c}]: swept {swept["S"][fi, r, c]} vs single {one["S"][0, r, c]}");
        }
    }

    // ── T3: every fixture — a swept run equals point-by-point runs, bit for bit ─

    public static TheoryData<string, string> SweptFixtures() => new()
    {
        { "mutual",    MutualCnl },
        { "nonlinear", NonlinearCnl },
        { "reactive",  ReactiveZ0Cnl },
    };

    [Theory]
    [MemberData(nameof(SweptFixtures))]
    public void T3_SweptRun_IsBitIdenticalToPointByPointRuns(string name, string cnl)
    {
        double[] freqs = [1.0e9, 1.5e9, 2.0e9, 2.5e9, 3.0e9];

        var swept = SParameterEngine.Run(Elaborate(cnl), freqs);
        int N     = swept["S"].Axes[1].Length;

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var one = SParameterEngine.Run(Elaborate(cnl), [freqs[fi]]);
            for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
                Assert.True((Complex)swept["S"][fi, r, c] == (Complex)one["S"][0, r, c],
                    $"{name} f={freqs[fi]:G6} S[{r},{c}]");
        }
    }

    [Fact]
    public void T3b_Hero1_SweptRun_IsBitIdenticalToPointByPointRuns()
    {
        var path  = Hero1Cnl();
        double[] freqs = [1.0e9, 1.45e9, 2.05e9, 2.9e9];

        static ElaboratedNetlist Load(string p)
        {
            var (lib, tb) = CnlReader.ReadFile(p);
            return new Elaborator(lib).Elaborate(tb);
        }

        var swept = SParameterEngine.Run(Load(path), freqs);
        int N     = swept["S"].Axes[1].Length;

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var one = SParameterEngine.Run(Load(path), [freqs[fi]]);
            for (int r = 0; r < N; r++)
            for (int c = 0; c < N; c++)
                Assert.True((Complex)swept["S"][fi, r, c] == (Complex)one["S"][0, r, c],
                    $"Hero1 f={freqs[fi]:G6} S[{r},{c}]");
        }
    }

    // ── T4: the regularization retry adds stamps that were never recorded ─────

    /// <summary>
    /// A floating node makes the first attempt singular; the IfNecessary retry re-stamps and adds a
    /// gmin admittance per node — calls the recorded sequence does not have. The pattern must
    /// rebuild rather than drop them, on the first frequency AND on every later one.
    /// </summary>
    [Fact]
    public void T4_RegularizationRetry_SweptEqualsPointByPoint_AndWarnsOnce()
    {
        const string cnl = """
            Port:P1  n1 0  Num=1 Z=50 Ohm
            Port:P2  n2 0  Num=2 Z=50 Ohm
            R:R1     n1 0  R=50 Ohm
            R:R2     n2 0  R=50 Ohm
            C:Cf     n3 n4 C=1 pF
            """;
        double[] freqs = [1.0e9, 2.0e9, 3.0e9];

        var nlSwept = Elaborate(cnl);
        var swept   = SParameterEngine.Run(nlSwept, freqs);

        Assert.Single(nlSwept.Warnings, w => w.Contains("regularization", StringComparison.OrdinalIgnoreCase));

        for (int fi = 0; fi < freqs.Length; fi++)
        {
            var nlOne = Elaborate(cnl);
            var one   = SParameterEngine.Run(nlOne, [freqs[fi]]);
            Assert.Single(nlOne.Warnings, w => w.Contains("regularization", StringComparison.OrdinalIgnoreCase));
            for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                Assert.True((Complex)swept["S"][fi, r, c] == (Complex)one["S"][0, r, c],
                    $"retry f={freqs[fi]:G6} S[{r},{c}]");
        }
    }

    // ── T5: a BuildCsc snapshot is the caller's, not a window onto the cache ──

    [Fact]
    public void T5_BuildCscSnapshot_SurvivesResetAndRestamp()
    {
        var nl = Elaborate(MutualCnl);
        int n  = nl.Nodes.Count - 1;

        var mna = new MnaSystem(n);
        StampAll(mna, nl, 2 * Math.PI * 1.0e9);
        mna.Factorize();

        var snapshot = mna.BuildCsc();
        var before   = (Complex[])snapshot.Values.Clone();

        StampAll(mna, nl, 2 * Math.PI * 2.0e9);   // Reset zeroes the LIVE value array
        mna.Factorize();

        Assert.Equal(before, snapshot.Values);
        Assert.NotEqual(before, mna.BuildCsc().Values);   // the matrix really did change
    }

    // ── T6: a mid-pass read seals the pattern; the pass must still come out right ─

    /// <summary>
    /// GetEntry/FindZeroRows make the CSC current. If more stamps arrive afterwards, they are extra
    /// calls the recorded sequence does not have, and the assembly must still end up correct.
    /// </summary>
    [Fact]
    public void T6_ReadMidPass_ThenMoreStamps_StillAssemblesCorrectly()
    {
        var mna = new MnaSystem(2);
        mna.AddAdmittance(1, 0, new Complex(0.02, 0));
        _ = mna.GetEntry(0, 0);                       // seals the pattern mid-pass
        mna.AddAdmittance(1, 2, new Complex(0.01, 0)); // extra calls ⇒ rebuild
        mna.AddAdmittance(2, 0, new Complex(0.04, 0));

        var expect = new MnaSystem(2);
        expect.AddAdmittance(1, 0, new Complex(0.02, 0));
        expect.AddAdmittance(1, 2, new Complex(0.01, 0));
        expect.AddAdmittance(2, 0, new Complex(0.04, 0));

        AssertSameMatrix(expect.BuildCsc(), mna.BuildCsc(), "mid-pass read");
        Assert.Equal(expect.GetEntry(0, 0), mna.GetEntry(0, 0));
        Assert.Equal(expect.GetEntry(0, 1), mna.GetEntry(0, 1));
        Assert.Equal(expect.GetEntry(1, 1), mna.GetEntry(1, 1));
    }

    // ── T7: a pass that stops SHORT of the recorded sequence ──────────────────

    [Fact]
    public void T7_ShorterPass_RebuildsPattern()
    {
        var mna = new MnaSystem(2);
        mna.AddAdmittance(1, 0, new Complex(0.02, 0));
        mna.AddAdmittance(2, 0, new Complex(0.04, 0));
        mna.Factorize();

        mna.Reset();
        mna.AddAdmittance(1, 0, new Complex(0.02, 0));   // node 2 never stamped this pass

        var zeroRows = mna.FindZeroRows();
        Assert.Single(zeroRows);
        Assert.Equal(1, zeroRows[0].Row);

        var expect = new MnaSystem(2);
        expect.AddAdmittance(1, 0, new Complex(0.02, 0));
        AssertSameMatrix(expect.BuildCsc(), mna.BuildCsc(), "short pass");
    }

    // ── T8: a pass that allocates more branches than the pattern recorded ─────

    [Fact]
    public void T8_ExtraBranch_RebuildsPattern()
    {
        var mna = new MnaSystem(2);
        int b0 = mna.AddBranch();
        mna.AddBranchCurrent(b0, 1, 0);
        mna.AddConstraint(b0, 1, Complex.One);
        _ = mna.BuildCsc();          // seal the pattern (this assembly is deliberately singular)

        mna.Reset();
        int c0 = mna.AddBranch();
        mna.AddBranchCurrent(c0, 1, 0);
        mna.AddConstraint(c0, 1, Complex.One);
        int c1 = mna.AddBranch();                       // one branch more than recorded
        mna.AddBranchCurrent(c1, 2, 0);
        mna.AddConstraint(c1, 2, Complex.One);

        var expect = new MnaSystem(2);
        int e0 = expect.AddBranch();
        expect.AddBranchCurrent(e0, 1, 0);
        expect.AddConstraint(e0, 1, Complex.One);
        int e1 = expect.AddBranch();
        expect.AddBranchCurrent(e1, 2, 0);
        expect.AddConstraint(e1, 2, Complex.One);

        Assert.Equal(expect.Size, mna.Size);
        AssertSameMatrix(expect.BuildCsc(), mna.BuildCsc(), "extra branch");
    }

    // ── T9b: the pattern is built ONCE across a sweep — the structural property ─

    [Fact]
    public void T9b_InvariantSequence_BuildsThePatternExactlyOnce()
    {
        var nl  = Elaborate(MutualCnl);
        var mna = new MnaSystem(nl.Nodes.Count - 1);

        for (int i = 0; i < 25; i++)
        {
            StampAll(mna, nl, 2 * Math.PI * (1.0e9 + i * 5e7));
            mna.Factorize();
        }
        Assert.Equal(1, mna.PatternBuilds);
    }

    /// <summary>An ideal inductor's ω = 0 pass really does diverge — the cache must notice.</summary>
    [Fact]
    public void T9c_IdealInductorAtDc_RebuildsThePattern()
    {
        const string cnl = """
            Port:P1  n1 0  Num=1 Z=50 Ohm
            L:L1     n1 n2 L=5 nH
            R:R1     n2 0  R=50 Ohm
            """;
        var nl  = Elaborate(cnl);
        var mna = new MnaSystem(nl.Nodes.Count - 1);

        StampAll(mna, nl, 2 * Math.PI * 1.0e9);
        mna.Factorize();
        Assert.Equal(1, mna.PatternBuilds);

        StampAll(mna, nl, 2 * Math.PI * 2.0e9);
        mna.Factorize();
        Assert.Equal(1, mna.PatternBuilds);          // same sequence — cache holds

        StampAll(mna, nl, 0.0);                      // ideal inductor skips its branch diagonal
        mna.Factorize();
        Assert.Equal(2, mna.PatternBuilds);          // rebuilt, once

        StampAll(mna, nl, 0.0);
        mna.Factorize();
        Assert.Equal(2, mna.PatternBuilds);          // the DC sequence is itself cacheable
    }

    // ── T9: duplicate cells still sum in call order ───────────────────────────

    /// <summary>
    /// Several stamps land in one cell. The pattern build merges them in CALL order — the order the
    /// old dictionary summed them in — so an order-sensitive floating-point sum is unchanged.
    /// </summary>
    [Fact]
    public void T9_DuplicateCells_SumInCallOrder()
    {
        double[] addends = [1e16, 1.0, -1e16, 1.0];     // sums to 1 in this order, 2 in reverse

        var mna = new MnaSystem(1);
        foreach (var a in addends) mna.AddAdmittance(1, 0, new Complex(a, 0));
        Assert.Equal(new Complex(((1e16 + 1.0) - 1e16) + 1.0, 0), mna.GetEntry(0, 0));

        // and the cached pass reproduces it
        mna.Factorize();
        mna.Reset();
        foreach (var a in addends) mna.AddAdmittance(1, 0, new Complex(a, 0));
        Assert.Equal(new Complex(((1e16 + 1.0) - 1e16) + 1.0, 0), mna.GetEntry(0, 0));
    }
}
