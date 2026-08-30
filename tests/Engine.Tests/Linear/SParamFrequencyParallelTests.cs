using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// SP-P3 gate: an S-parameter sweep may solve contiguous chunks of the frequency grid at once, on
/// separately elaborated copies of the testbench.
///
/// <para><b>The gate is bit-identity, not a tolerance.</b> Only who computes a point moves; the
/// arithmetic at each point is untouched, and each chunk writes its own slice of the output array.
/// So a parallel run must produce exactly the doubles a serial one produces — every S entry, the Z0
/// cube, and the warnings list in the same order — for every fixture, at a degree that divides the
/// grid unevenly. Nothing here is timed; a speed-up is measured with a scratch harness, and a
/// timing assertion in the suite would only measure the machine.</para>
/// </summary>
public class SParamFrequencyParallelTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>Wave path, purely linear.</summary>
    private const string LadderCnl = """
        Port:P1  n1 0  Num=1 Z=50 Ohm
        Port:P2  n3 0  Num=2 Z=50 Ohm
        L:L1     n1 n2 L=2.2 nH
        C:C1     n2 0  C=1.4 pF
        L:L2     n2 n3 L=3.3 nH
        C:C2     n3 0  C=0.9 pF
        R:R1     n3 0  R=180 Ohm
        """;

    /// <summary>Three coupled inductors — every model that writes a branch index during Stamp.</summary>
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

    /// <summary>A nonlinear device: every worker solves its OWN DC operating point (fact 4).</summary>
    private const string NonlinearCnl = """
        Port:P1  n1 0  Num=1  Z=50 Ohm
        Port:P2  n2 0  Num=2  Z=50 Ohm
        R:Rs     n1 n2 R=20 Ohm
        C:C1     n2 0  C=0.5 pF
        SDD:D1   n2 0  I[1]=_v1/75
        """;

    /// <summary>A nonlinear device with a real DC bias, so the operating point is not the trivial one.</summary>
    private const string BiasedNonlinearCnl = """
        Port:P1  n1 0  Num=1  Z=50 Ohm
        Port:P2  n2 0  Num=2  Z=50 Ohm
        Vdc:Vb   nb 0  V=0.8 V
        R:Rb     nb n2 R=1 kOhm
        R:Rs     n1 n2 R=20 Ohm
        C:C1     n2 0  C=0.5 pF
        SDD:D1   n2 0  I[1]=1e-14*(exp(_v1/0.026)-1)
        """;

    /// <summary>Reactive port reference impedance ⇒ the LEGACY path (0 V port branches, Y→S).</summary>
    private const string ReactiveZ0Cnl = """
        Port:P1  n1 0  Num=1  Z=(0+50j) Ohm
        Port:P2  n2 0  Num=2  Z=(0+50j) Ohm
        L:L1     n1 n2 L=2 nH
        C:C1     n2 0  C=1 pF
        R:R1     n1 0  R=75 Ohm
        """;

    /// <summary>A floating node: every point takes the IfNecessary regularization retry, and the
    /// warning must still be reported exactly once however many chunks hit it.</summary>
    private const string FloatingNodeCnl = """
        Port:P1  n1 0  Num=1 Z=50 Ohm
        Port:P2  n2 0  Num=2 Z=50 Ohm
        R:R1     n1 0  R=50 Ohm
        R:R2     n2 0  R=50 Ohm
        C:Cf     n3 n4 C=1 pF
        """;

    /// <summary>P1Tone acting as an s-param port — its minted __drv node is tied off per netlist.</summary>
    private const string P1ToneCnl = """
        P1Tone:PORT1  n1 0  Num=1 Z=50 Ohm  Pavl=0 dBm
        Port:P2       n2 0  Num=2 Z=50 Ohm
        L:L1          n1 n2 L=1.8 nH
        C:C1          n2 0  C=1.2 pF
        """;

    public static TheoryData<string, string> Fixtures() => new()
    {
        { "ladder",    LadderCnl },
        { "mutual",    MutualCnl },
        { "nonlinear", NonlinearCnl },
        { "biased",    BiasedNonlinearCnl },
        { "reactive",  ReactiveZ0Cnl },
        { "floating",  FloatingNodeCnl },
        { "p1tone",    P1ToneCnl },
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (Library Lib, TestBench Tb) Read(string cnl) => new CnlReader().Read(cnl);

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    /// <summary>301 points: not divisible by 3, so the chunks are 101/100/100 and the split is
    /// exercised on an uneven remainder rather than a tidy one.</summary>
    private static double[] Grid(int n = 301, double f0 = 0.5e9, double f1 = 6.0e9)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 + (f1 - f0) * i / (n - 1);
        return f;
    }

    private static void AssertCubesBitIdentical(DataSet expected, DataSet actual, string what)
    {
        var se = expected["S"];
        var sa = actual["S"];
        Assert.Equal(se.Axes.Count, sa.Axes.Count);
        for (int a = 0; a < se.Axes.Count; a++)
            Assert.Equal(se.Axes[a].Length, sa.Axes[a].Length);

        int nf = se.Axes[0].Length, n = se.Axes[1].Length;
        for (int fi = 0; fi < nf; fi++)
        for (int r = 0; r < n; r++)
        for (int c = 0; c < n; c++)
            Assert.True((Complex)se[fi, r, c] == (Complex)sa[fi, r, c],
                $"{what}: S[{fi},{r},{c}] serial {se[fi, r, c]} vs parallel {sa[fi, r, c]}");

        var z0e = expected["Z0"];
        var z0a = actual["Z0"];
        Assert.Equal(z0e.Axes[0].Length, z0a.Axes[0].Length);
        for (int i = 0; i < z0e.Axes[0].Length; i++)
            Assert.True((Complex)z0e[i] == (Complex)z0a[i], $"{what}: Z0[{i}]");
    }

    // ── T1: the parallel path is bit-identical to the serial one ──────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void T1_ParallelRun_IsBitIdenticalToSerial(string name, string cnl)
    {
        var freqs = Grid();

        var serialNl = Elaborate(cnl);
        var serial   = SParameterEngine.Run(serialNl, freqs);

        var (lib, tb) = Read(cnl);
        using var parNl = new Elaborator(lib).Elaborate(tb);
        var parallel    = SParameterEngine.Run(parNl, lib, tb, null, freqs,
                              settings: null, control: null, maxDegreeOfParallelism: 3);

        Assert.Equal(3, SParameterEngine.PlanDegree(parNl, freqs.Length, 3));
        AssertCubesBitIdentical(serial, parallel, name);

        // Same warnings, same order — the chunks' own findings are folded back by key, first
        // occurrence winning, so the report does not depend on how the grid was divided.
        Assert.Equal(serialNl.Warnings, parNl.Warnings);
        Assert.Equal(serialNl.Notes,    parNl.Notes);
    }

    /// <summary>Two, three, five and eight workers over the same grid: every degree lands on the
    /// same doubles, so the answer does not depend on where the chunk boundaries fell.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void T1b_EveryDegree_GivesTheSameDoubles(int degree)
    {
        var freqs  = Grid();
        var serial = SParameterEngine.Run(Elaborate(MutualCnl), freqs);

        var (lib, tb) = Read(MutualCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);
        var parallel  = SParameterEngine.Run(nl, lib, tb, null, freqs,
                            settings: null, control: null, maxDegreeOfParallelism: degree);

        AssertCubesBitIdentical(serial, parallel, $"degree {degree}");
    }

    [Fact]
    public void T1c_Hero1_ParallelRun_IsBitIdenticalToSerial()
    {
        var path  = Hero1Cnl();
        var freqs = Grid(301, 1.0e9, 3.0e9);

        var (lib, tb) = CnlReader.ReadFile(path);
        var serial    = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), freqs);

        var (lib2, tb2) = CnlReader.ReadFile(path);
        using var nl    = new Elaborator(lib2).Elaborate(tb2);
        var parallel    = SParameterEngine.Run(nl, lib2, tb2, Path.GetDirectoryName(path), freqs,
                              settings: null, control: null, maxDegreeOfParallelism: 3);

        AssertCubesBitIdentical(serial, parallel, "Hero 1");
    }

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

    // ── T2: the regularization retry, which every chunk hits, still warns ONCE ─

    /// <summary>
    /// A floating node makes every point's first factorization singular, so every chunk takes the
    /// IfNecessary retry and every chunk's own netlist records the regularization warning. The merge
    /// is by KEY, so the caller sees exactly one — the same one a serial run reports.
    /// </summary>
    [Fact]
    public void T2_RegularizationRetry_WarnsExactlyOnce_HoweverManyChunksHitIt()
    {
        var freqs = Grid();

        var (lib, tb) = Read(FloatingNodeCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);
        SParameterEngine.Run(nl, lib, tb, null, freqs,
            settings: null, control: null, maxDegreeOfParallelism: 4);

        Assert.Single(nl.Warnings,
            w => w.Contains("regularization", StringComparison.OrdinalIgnoreCase));
    }

    // ── T3: every worker reaches the same DC operating point (fact 4) ──────────

    /// <summary>
    /// A nonlinear netlist solves its operating point once PER NETLIST, so a parallel run solves it
    /// once per worker and the chunks would disagree if the Newton were not deterministic. Asserted
    /// rather than assumed: two independent elaborations of the same testbench must reach the same
    /// node voltages to the last bit, which is what makes T1's "biased" row bit-identical.
    /// </summary>
    [Fact]
    public void T3_IndependentElaborations_ReachTheSameDcOperatingPoint()
    {
        var (lib, tb) = Read(BiasedNonlinearCnl);

        var a = NonlinearDcEngine.Run(new Elaborator(lib).Elaborate(tb));
        var b = NonlinearDcEngine.Run(new Elaborator(lib).Elaborate(tb));

        Assert.True(a.Converged);
        Assert.True(b.Converged);
        Assert.Equal(a.NodeVoltages.Length, b.NodeVoltages.Length);
        for (int i = 0; i < a.NodeVoltages.Length; i++)
            Assert.True(a.NodeVoltages[i] == b.NodeVoltages[i],
                $"node {i}: {a.NodeVoltages[i]:R} vs {b.NodeVoltages[i]:R}");
    }

    // ── T4: progress counts every point exactly once, and never overruns ───────

    [Fact]
    public void T4_Progress_CountsEveryPointExactlyOnce()
    {
        var freqs = Grid();
        long overrun = 0;
        var control = new RunControl
        {
            Total               = freqs.Length,
            MinReportIntervalMs = 0,   // observe every tick, not one in forty milliseconds
            Progress            = new InlineProgress(p =>
                                      { if (p.Completed > freqs.Length) Interlocked.Increment(ref overrun); }),
        };

        var (lib, tb) = Read(LadderCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);
        SParameterEngine.Run(nl, lib, tb, null, freqs, settings: null, control: control,
            maxDegreeOfParallelism: 4);

        Assert.Equal(freqs.Length, control.Completed);
        Assert.Equal(0, Interlocked.Read(ref overrun));   // never counted a point twice
    }

    // ── T5: cancellation stops every chunk, with the serial path's exception ───

    /// <summary>
    /// Cancelled after a fixed number of ticks — not after a wall-clock delay, which on a fast
    /// machine races the sweep and would sometimes measure a completed run instead of a cancelled
    /// one. The run must surface <see cref="OperationCanceledException"/>, the same exception the
    /// serial path throws out of <c>RunControl.Tick</c>, rather than the
    /// <c>AggregateException</c> a bare <c>Parallel.For</c> would wrap it in; and every chunk must
    /// stop, which is what the settled counter shows.
    /// </summary>
    [Fact]
    public void T5_Cancellation_ThrowsTheSerialException_AndStopsEveryChunk()
    {
        var freqs = Grid(2001, 0.5e9, 6.0e9);
        const int cancelAfter = 40;

        using var cts = new CancellationTokenSource();
        var control = new RunControl
        {
            Token               = cts.Token,
            Total               = freqs.Length,
            MinReportIntervalMs = 0,      // every tick is observed, so the Nth one really is the Nth
            Progress            = new InlineProgress(p =>
                                      { if (p.Completed >= cancelAfter) cts.Cancel(); }),
        };

        var (lib, tb) = Read(LadderCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);

        var ex = Record.Exception(() => SParameterEngine.Run(
            nl, lib, tb, null, freqs, settings: null, control: control, maxDegreeOfParallelism: 4));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(control.Completed < freqs.Length,
            $"cancelled run still finished {control.Completed} of {freqs.Length}");

        // Nothing keeps running behind the throw: the counter is settled the moment Run returns.
        long settled = control.Completed;
        Thread.Sleep(50);
        Assert.Equal(settled, control.Completed);
    }

    /// <summary>A cancelled run throws the same exception TYPE the serial path throws — pinned
    /// against the serial path itself rather than against a remembered name.</summary>
    [Fact]
    public void T5b_SerialAndParallel_ThrowTheSameCancellationType()
    {
        var freqs = Grid(512);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var serialEx = Record.Exception(() => SParameterEngine.Run(
            Elaborate(LadderCnl), freqs, null, new RunControl { Token = cts.Token }));

        var (lib, tb) = Read(LadderCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);
        var parallelEx = Record.Exception(() => SParameterEngine.Run(
            nl, lib, tb, null, freqs,
            settings: null, control: new RunControl { Token = cts.Token },
            maxDegreeOfParallelism: 4));

        Assert.NotNull(serialEx);
        Assert.NotNull(parallelEx);
        Assert.Equal(serialEx!.GetType(), parallelEx!.GetType());
    }

    // ── T6: the degree formula and its floor ──────────────────────────────────

    [Fact]
    public void T6_ShortSweeps_StaySerial()
    {
        var nl = Elaborate(LadderCnl);

        // Under one worker's worth of points: nothing to divide.
        Assert.Equal(1, SParameterEngine.PlanDegree(nl, 1, 0));
        Assert.Equal(1, SParameterEngine.PlanDegree(nl, SParameterEngine.MinPointsPerWorker - 1, 0));
        Assert.Equal(1, SParameterEngine.PlanDegree(nl, SParameterEngine.MinPointsPerWorker, 8));

        // Two workers' worth is the first grid that splits.
        Assert.Equal(2, SParameterEngine.PlanDegree(nl, 2 * SParameterEngine.MinPointsPerWorker, 8));

        // An explicit 1 pins the serial path however long the grid is.
        Assert.Equal(1, SParameterEngine.PlanDegree(nl, 20_001, 1));

        // The cap binds when it is below what the grid would allow.
        Assert.Equal(3, SParameterEngine.PlanDegree(nl, 20_001, 3));

        // Automatic never exceeds the machine.
        Assert.True(SParameterEngine.PlanDegree(nl, 1_000_000, 0) <= Environment.ProcessorCount);
    }

    /// <summary>A short grid runs, and gives the serial answer, through the parallel overload —
    /// the floor is a routing decision, not a refusal.</summary>
    [Fact]
    public void T6b_AShortGridStillRuns_ThroughTheParallelOverload()
    {
        double[] freqs = [1.0e9, 2.0e9, 3.0e9];

        var serial    = SParameterEngine.Run(Elaborate(LadderCnl), freqs);
        var (lib, tb) = Read(LadderCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);
        var viaOverload = SParameterEngine.Run(nl, lib, tb, null, freqs);

        AssertCubesBitIdentical(serial, viaOverload, "short grid");
    }

    /// <summary>The settings knob is consulted when the call site names no degree of its own.</summary>
    [Fact]
    public void T6c_SettingsMaxParallelism_PinsTheSerialPath()
    {
        var freqs     = Grid();
        var settings  = new AnalysisSettings { MaxParallelism = 1 };
        var (lib, tb) = Read(LadderCnl);
        using var nl  = new Elaborator(lib).Elaborate(tb);

        var pinned = SParameterEngine.Run(nl, lib, tb, null, freqs, settings);
        var serial = SParameterEngine.Run(Elaborate(LadderCnl), freqs, settings);

        AssertCubesBitIdentical(serial, pinned, "MaxParallelism=1");
    }

    // ── T7: the netlists that must stay serial ────────────────────────────────

    /// <summary>
    /// An external device's instance is a slot in a WORKER PROCESS, one per kit rather than one per
    /// thread. Elaborating T copies would ask that process for T times the instances and then
    /// serialize on its channel anyway, so such a netlist is refused the split — asserted through
    /// <see cref="SParameterEngine.PlanDegree"/> rather than by timing anything.
    /// </summary>
    [Fact]
    public void T7_AnExternalDeviceNetlist_StaysSerial()
    {
        var nl = Elaborate(LadderCnl);
        Assert.True(SParameterEngine.PlanDegree(nl, 20_001, 8) > 1);   // control: it would split

        nl.AddComponent(new ElaboratedComponent(
            "External", "X1", [1, 0],
            new Dictionary<string, Value>(),
            new ExternalDeviceModel(new FakeExternalInstance(), "fake", "X1")));

        Assert.Equal(1, SParameterEngine.PlanDegree(nl, 20_001, 8));
    }

    /// <summary>
    /// An SDD with a control-current reference stays serial in this revision. Nothing about it is
    /// unsafe — <c>ResolveSParamControlBranches</c> simply runs per netlist and its test surface is
    /// small — so it keeps the path it has always run on.
    /// </summary>
    [Fact]
    public void T7b_AnSddWithControlRefs_StaysSerial()
    {
        const string cnl = """
            Port:P1   n1 0  Num=1 Z=50 Ohm
            Port:P2   n2 0  Num=2 Z=50 Ohm
            R:Rs      n1 n2 R=20 Ohm
            IProbe:IP n2 n3
            R:Rl      n3 0  R=50 Ohm
            SDD:S1    n2 0  C[1]="IP"  I[1]=_c1*10
            """;

        var nl = Elaborate(cnl);
        Assert.Contains(nl.Components, c => c.Model is SddModel { ControlRefs.Length: > 0 });
        Assert.Equal(1, SParameterEngine.PlanDegree(nl, 20_001, 8));
    }

    /// <summary>A control-free SDD is an ordinary nonlinear device and splits like any other.</summary>
    [Fact]
    public void T7c_AControlFreeSdd_Splits()
        => Assert.Equal(4, SParameterEngine.PlanDegree(Elaborate(NonlinearCnl), 20_001, 4));

    /// <summary>
    /// A progress sink that runs ON THE REPORTING THREAD. <see cref="Progress{T}"/> posts to the
    /// thread pool, so a test that acts on an observation races the run it is observing — which for
    /// a cancellation test means sometimes measuring a completed run instead of a cancelled one.
    /// </summary>
    private sealed class InlineProgress(Action<RunProgress> onReport) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => onReport(value);
    }

    /// <summary>The stand-in for a provider's live instance. It is never evaluated — the fallback is
    /// decided from the model's TYPE, before any solve — so its numbers only have to exist.</summary>
    private sealed class FakeExternalInstance : IExternalDeviceInstance
    {
        public ExternalDeviceDescriptor Descriptor { get; } = new(
            TypeId:            "fake",
            DisplayName:       "fake",
            ExternalPinCount:  2,
            InternalNodeCount: 0,
            Parameters:        [],
            Nodes:             [new ExternalNodeDescriptor(0, true), new ExternalNodeDescriptor(1, true)]);

        public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> nodeVoltages)
            => new(new double[2], new double[2], new double[2, 2], new double[2, 2]);

        public void Dispose() { }
    }
}
