using System.Diagnostics.CodeAnalysis;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// brief-hb-p4-sdd-grid-evaluate.md §5 — the grid evaluator's gate.
///
/// <para>Its whole claim is that walking the compiled register program ONCE per time grid, with
/// structure-of-arrays operands, produces exactly what walking it once per sample through 136-byte
/// duals produces. "Exactly" here is <see cref="BitConverter.DoubleToInt64Bits"/> equality, not a
/// tolerance: the instruction stream is unchanged and IEEE arithmetic is deterministic per element,
/// so any difference at all is a real difference in what was computed — a fused multiply-add, a
/// gradient lane written where the scalar path leaves +0.0, a vector transcendental that rounds
/// differently — and each of those would move an HB answer.</para>
/// </summary>
public sealed class CompiledSddGridTests
{
    private static readonly Dictionary<string, double> HeroParams = new(StringComparer.Ordinal)
    {
        ["Sv"] = -0.837, ["Sc"] = 0.71, ["TV0"] = 4.268, ["TC"] = 1.507,
        ["th"] = 0.001, ["a"] = 0.176, ["g"] = 0.089, ["lam"] = 0.0012, ["B"] = 1130.0,
    };

    // The drain equation every hero SDD in testdata/ shares (Hero 2, 3, 3B, 4, 5 alike), and the
    // ×10 second-stage variant Hero 3B adds.
    private const string HeroI2 =
        "(B*TC*tanh(_v2*a*(tanh(g*(TV0 - _v1 + _v2*th + Sc*ln(exp(-(Sv - _v1)/Sc) + 1))) + 1))" +
        " * ln(exp(-(2*TV0 - 2*_v1 + 2*_v2*th + 2*Sc*ln(exp(-(Sv - _v1)/Sc) + 1))/TC) + 1)" +
        " * (_v2*lam + 1)) / 2";

    private static readonly (string Name, string Expr, IReadOnlyDictionary<string, double> Params)[] Corpus =
    [
        ("hero-gate",        "_v1/50", new Dictionary<string, double>()),
        ("hero-gate-80",     "_v1/80", new Dictionary<string, double>()),
        ("hero-drain",       HeroI2, HeroParams),
        ("hero-drain-10x",   "10*(" + HeroI2 + ")", HeroParams),
        ("bare-name",        "_v1", new Dictionary<string, double>()),
        ("bare-param",       "P", new Dictionary<string, double> { ["P"] = 2.5 }),
        ("pow",              "_v1^2 + _v2^0.5", new Dictionary<string, double>()),
        ("pow-func",         "pow(_v1, 3) + pow(_v2, _v1)", new Dictionary<string, double>()),
        ("every-fn-1",       "exp(_v1)+ln(abs(_v2)+1)+sqrt(abs(_v1)+1)+tanh(_v2)+sin(_v1)+cos(_v2)+abs(_v1-_v2)", new Dictionary<string, double>()),
        ("every-fn-2",       "sinh(_v1)+cosh(_v2)+tan(_v1*0.1)+log10(abs(_v2)+1)+min(_v1,_v2)+max(_v1,_v2)+sign(_v1)", new Dictionary<string, double>()),
        ("every-fn-3",       "atan(_v1)+atan2(_v1,_v2)+asin(_v1/100)+acos(_v2/100)", new Dictionary<string, double>()),
        ("expcap-clamp",     "exp(_v1*1000)", new Dictionary<string, double>()),
        ("logfloor-log",     "log(_v1)", new Dictionary<string, double>()),
        ("logfloor-sqrt",    "sqrt(_v1)", new Dictionary<string, double>()),
        ("with-param",       "P*_v1 + Q*_v2*_v2", new Dictionary<string, double> { ["P"] = 3.5, ["Q"] = -2.1 }),
        ("unary-plus-minus", "-(-_v1) + +(_v2)", new Dictionary<string, double>()),
        ("deep-nest",        "(((_v1+_v2)*(_v1-_v2))/(_v1*_v1+1))^2", new Dictionary<string, double>()),
        // A charge equation's shape: nothing structurally different, but it is the one that feeds Dc.
        ("charge",           "1e-12*_v1 + 2e-13*_v1*_v2", new Dictionary<string, double>()),
    ];

    // Not only powers of two: the vector loop has a scalar tail and a grid of 7 or 33 is what
    // exercises it. S=1 is the degenerate case a chunked parallel split can hand a worker.
    public static IEnumerable<object[]> CorpusAndGrids()
    {
        foreach (var c in Corpus)
            foreach (int s in new[] { 1, 7, 32, 33, 1024 })
                yield return [c.Name, c.Expr, c.Params, s];
    }

    /// <summary>
    /// A grid spanning the saturating region on both axes — Vgs from −6 to +1 drives the softplus
    /// clamp and the log floor, Vds from 0 to 100 drives the tanh into saturation — so the clamped
    /// branches are compared, not just the smooth interior.
    /// </summary>
    private static (double[] Port0, double[] Port1) Grid(int s)
    {
        var v1 = new double[s];
        var v2 = new double[s];
        for (int t = 0; t < s; t++)
        {
            double u = s == 1 ? 0.5 : (double)t / (s - 1);
            v1[t] = -6.0 + 7.0 * u;
            v2[t] = 100.0 * (0.5 - 0.5 * Math.Cos(2.0 * Math.PI * u));
        }
        return (v1, v2);
    }

    [Theory]
    [MemberData(nameof(CorpusAndGrids))]
    public void Grid_MatchesScalar_BitForBit(
        string name, string expr, IReadOnlyDictionary<string, double> parameters, int s)
    {
        var ast = Parser.Parse(expr);
        var compiled = CompiledSddExpr.Compile(ast, parameters, 2, [], name);
        Assert.True(compiled.SupportsGrid, $"{name}: no register program");

        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);

        var value = new double[s];
        var grad = new double[2 * s];
        var scratch = compiled.CreateScratch(s);
        var warn = new GridDomainWarnings();
        compiled.EvalDualGrid(portV, [], s, 0, s, value, grad, scratch, name, ref warn);

        for (int t = 0; t < s; t++)
        {
            (double refVal, double[] refGrad) = compiled.EvalDual([v1[t], v2[t]], [], name);
            AssertBitEqual(refVal, value[t], $"{name} S={s} t={t} value");
            for (int k = 0; k < 2; k++)
                AssertBitEqual(refGrad[k], grad[k * s + t], $"{name} S={s} t={t} grad[{k}]");
        }
    }

    /// <summary>
    /// Chunking must not change a bit either — M3 splits the grid across workers, and each worker
    /// runs the same program over its own slice into its own registers.
    /// </summary>
    [Theory]
    [InlineData(33, 7)]
    [InlineData(1024, 100)]
    public void ChunkedGrid_EqualsWholeGrid_BitForBit(int s, int chunk)
    {
        const string name = "hero-drain";
        var compiled = CompiledSddExpr.Compile(Parser.Parse(HeroI2), HeroParams, 2, [], name);
        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);

        var whole = new double[s];
        var wholeG = new double[2 * s];
        var w0 = new GridDomainWarnings();
        compiled.EvalDualGrid(portV, [], s, 0, s, whole, wholeG, compiled.CreateScratch(s), name, ref w0);

        var piece = new double[s];
        var pieceG = new double[2 * s];
        var scratch = compiled.CreateScratch(chunk);
        for (int t0 = 0; t0 < s; t0 += chunk)
        {
            int count = Math.Min(chunk, s - t0);
            var w1 = new GridDomainWarnings();
            compiled.EvalDualGrid(portV, [], s, t0, count, piece, pieceG, scratch, name, ref w1);
        }

        for (int t = 0; t < s; t++)
        {
            AssertBitEqual(whole[t], piece[t], $"value t={t}");
            for (int k = 0; k < 2; k++)
                AssertBitEqual(wholeG[k * s + t], pieceG[k * s + t], $"grad[{k}] t={t}");
        }
    }

    /// <summary>
    /// §5.5 — control-current seeds are per sample, and their gradient lands in lanes n..n+C−1
    /// exactly where the scalar path puts it.
    /// </summary>
    [Fact]
    public void ControlCurrentSeeds_PerSample_MatchScalarBitForBit()
    {
        const string name = "ctrl";
        const int s = 37;
        var ast = Parser.Parse("_v1 + _v2*0.5 + _c1*3.0 - _c1*_v1 + _c2*_c2");
        var parameters = new Dictionary<string, double>();
        var compiled = CompiledSddExpr.Compile(ast, parameters, 2, [1, 2], name);
        Assert.Equal(4, compiled.GradWidth);

        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);
        var ctrlV = new double[2 * s];
        for (int t = 0; t < s; t++)
        {
            ctrlV[t] = 0.01 * (t - s / 2.0);
            ctrlV[s + t] = -0.002 * t;
        }

        var value = new double[s];
        var grad = new double[4 * s];
        var warn = new GridDomainWarnings();
        compiled.EvalDualGrid(portV, ctrlV, s, 0, s, value, grad, compiled.CreateScratch(s), name, ref warn);

        for (int t = 0; t < s; t++)
        {
            (double refVal, double[] refGrad) = compiled.EvalDual(
                [v1[t], v2[t]], [(1, ctrlV[t]), (2, ctrlV[s + t])], name);
            AssertBitEqual(refVal, value[t], $"t={t} value");
            for (int k = 0; k < 4; k++)
                AssertBitEqual(refGrad[k], grad[k * s + t], $"t={t} grad[{k}]");
        }
    }

    /// <summary>
    /// §5.2 — the scalar path warns once per evaluation, so a grid that clamps at several samples
    /// would print that many identical lines. One line per grid, naming the model, is the contract.
    /// </summary>
    [Fact]
    public void DomainWarning_IsEmittedOncePerGrid_NamingTheModel()
    {
        const string name = "M_warn";
        const int s = 32;
        var compiled = CompiledSddExpr.Compile(Parser.Parse("log(_v1)"), new Dictionary<string, double>(), 1, [], name);

        // Three of the thirty-two samples are out of log's domain.
        var portV = new double[s];
        for (int t = 0; t < s; t++) portV[t] = t + 1.0;
        portV[3] = -1.5; portV[10] = -0.25; portV[31] = 0.0;

        var value = new double[s];
        var grad = new double[s];
        var warn = new GridDomainWarnings();

        var original = Console.Error;
        var sink = new StringWriter();
        try
        {
            Console.SetError(sink);
            compiled.EvalDualGrid(portV, [], s, 0, s, value, grad, compiled.CreateScratch(s), name, ref warn);
            Assert.Equal("", sink.ToString());   // nothing printed DURING the grid
            warn.Emit(name);
        }
        finally { Console.SetError(original); }

        var lines = sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains(name, lines[0]);
        Assert.Contains("log(", lines[0]);
        Assert.Contains("out of domain", lines[0]);

        // And the clamped values themselves still match the scalar path exactly.
        for (int t = 0; t < s; t++)
        {
            (double refVal, double[] refGrad) = compiled.EvalDual([portV[t]], [], name);
            AssertBitEqual(refVal, value[t], $"t={t} value");
            AssertBitEqual(refGrad[0], grad[t], $"t={t} grad");
        }
    }

    /// <summary>§5.3 — a conditional equation has no single instruction sequence for a grid, so it
    /// declines rather than guessing a branch.</summary>
    [Fact]
    public void ConditionalEquation_DeclinesTheGrid()
    {
        const string name = "M_if";
        var compiled = CompiledSddExpr.Compile(
            Parser.Parse("if(_v1>0, _v1*2, _v1/2)"), new Dictionary<string, double>(), 1, [], name);

        Assert.False(compiled.SupportsGrid);
        var scratch = new GridScratch();
        var value = new double[4];
        var grad = new double[4];
        var warn = new GridDomainWarnings();
        Assert.Throws<NotSupportedException>(() =>
        {
            var w = new GridDomainWarnings();
            compiled.EvalDualGrid(new double[4], [], 4, 0, 4, value, grad, scratch, name, ref w);
        });
        _ = warn;

        // …and the DEVICE carrying it stays on the scalar path, because a device is asked for all
        // its equations at once.
        var sdd = new SddModel("M_if", 1,
            currentAst: [Parser.Parse("if(_v1>0, _v1*2, _v1/2)")],
            chargeAst: [null],
            parameters: new Dictionary<string, double>());
        Assert.False(sdd.PrefersGridEvaluate);

        var plain = new SddModel("M_plain", 1,
            currentAst: [Parser.Parse("_v1/50")],
            chargeAst: [null],
            parameters: new Dictionary<string, double>());
        Assert.True(plain.PrefersGridEvaluate);
    }

    /// <summary>§5.4 — the register file is the caller's and is reused, so a converged solve pays
    /// for it once. The scalar path allocated six arrays per SAMPLE.</summary>
    [Fact]
    [SuppressMessage("Reliability", "CA2000", Justification = "no disposables here")]
    public void SecondGridCall_AllocatesNothing()
    {
        const string name = "hero-drain";
        const int s = 128;
        var compiled = CompiledSddExpr.Compile(Parser.Parse(HeroI2), HeroParams, 2, [], name);
        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);
        var value = new double[s];
        var grad = new double[2 * s];
        var scratch = compiled.CreateScratch(s);

        var warn = new GridDomainWarnings();
        compiled.EvalDualGrid(portV, [], s, 0, s, value, grad, scratch, name, ref warn);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        compiled.EvalDualGrid(portV, [], s, 0, s, value, grad, scratch, name, ref warn);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0, $"second EvalDualGrid allocated {delta} bytes");
    }

    /// <summary>
    /// The device-level door: SddModel.EvaluateGrid must fill a GridResult with exactly what the
    /// per-sample Evaluate returns, including the charge block and the Jacobians.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    public void SddModel_EvaluateGrid_MatchesPerSampleEvaluate_BitForBit(int s)
    {
        var sdd = new SddModel("M1", 2,
            currentAst: [Parser.Parse("_v1/50"), Parser.Parse(HeroI2)],
            chargeAst: [Parser.Parse("1.2e-12*_v1"), Parser.Parse("0.4e-12*(_v1-_v2)")],
            parameters: HeroParams);
        Assert.True(sdd.PrefersGridEvaluate);

        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);

        var into = new GridResult();
        sdd.EvaluateGrid(portV, [], s, into);

        for (int t = 0; t < s; t++)
        {
            var r = sdd.Evaluate(new PortVoltages([v1[t], v2[t]]));
            for (int p = 0; p < 2; p++)
            {
                AssertBitEqual(r.I[p], into.I[into.PortBase(p) + t], $"t={t} I[{p}]");
                AssertBitEqual(r.Q[p], into.Q[into.PortBase(p) + t], $"t={t} Q[{p}]");
                for (int q = 0; q < 2; q++)
                {
                    AssertBitEqual(r.Dg[p, q], into.Dg[into.JacBase(p, q) + t], $"t={t} Dg[{p},{q}]");
                    AssertBitEqual(r.Dc[p, q], into.Dc[into.JacBase(p, q) + t], $"t={t} Dc[{p},{q}]");
                }
            }
        }
    }

    /// <summary>
    /// §5.9 at the model level, where the claim is exact: a warm <see cref="GridResult"/> makes a
    /// whole grid's device evaluation allocation-FREE, whatever the grid size. The scalar path
    /// allocates six arrays per sample, so its cost is linear in S and the grid path's is zero — the
    /// engine-level test measures the same thing diluted by the HB pass's own buffers.
    /// </summary>
    [Theory]
    [InlineData(32)]
    [InlineData(1024)]
    public void SddModel_EvaluateGrid_AllocatesNothingOnAWarmResult(int s)
    {
        var sdd = new SddModel("M1", 2,
            currentAst: [Parser.Parse("_v1/50"), Parser.Parse(HeroI2)],
            chargeAst: [null, Parser.Parse("0.4e-12*(_v1-_v2)")],
            parameters: HeroParams);

        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);

        int saved = SddModel.GridParallelThreshold;
        try
        {
            // Serial: the parallel split's Parallel.For and its per-chunk state are their own
            // (bounded, per-call) allocation, and are M3's business rather than this claim's.
            SddModel.GridParallelThreshold = int.MaxValue;
            var into = new GridResult();
            sdd.EvaluateGrid(portV, [], s, into);   // warm

            long before = GC.GetAllocatedBytesForCurrentThread();
            sdd.EvaluateGrid(portV, [], s, into);
            long grid = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < s; t++) sdd.Evaluate(new PortVoltages([v1[t], v2[t]]));
            long scalar = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(grid == 0, $"grid evaluation of {s} samples allocated {grid} bytes");
            Assert.True(scalar > 0, "the scalar reference allocated nothing — nothing was measured");
        }
        finally { SddModel.GridParallelThreshold = saved; }
    }

    /// <summary>M3 — the parallel split is a performance decision, never a numerical one. Forced to
    /// both sides of its threshold, the same grid must give the same bits.</summary>
    [Fact]
    public void ParallelGrid_EqualsSerialGrid_BitForBit()
    {
        const int s = 1024;
        var sdd = new SddModel("M1", 2,
            currentAst: [Parser.Parse("_v1/50"), Parser.Parse(HeroI2)],
            chargeAst: [null, Parser.Parse("0.4e-12*(_v1-_v2)")],
            parameters: HeroParams);

        var (v1, v2) = Grid(s);
        var portV = new double[2 * s];
        v1.CopyTo(portV, 0);
        v2.CopyTo(portV, s);

        int saved = SddModel.GridParallelThreshold;
        var serial = new GridResult();
        var parallel = new GridResult();
        try
        {
            SddModel.GridParallelThreshold = int.MaxValue;
            sdd.EvaluateGrid(portV, [], s, serial);
            SddModel.GridParallelThreshold = 1;
            sdd.EvaluateGrid(portV, [], s, parallel);
        }
        finally { SddModel.GridParallelThreshold = saved; }

        for (int k = 0; k < 2 * s; k++)
        {
            AssertBitEqual(serial.I[k], parallel.I[k], $"I[{k}]");
            AssertBitEqual(serial.Q[k], parallel.Q[k], $"Q[{k}]");
        }
        for (int k = 0; k < 4 * s; k++)
        {
            AssertBitEqual(serial.Dg[k], parallel.Dg[k], $"Dg[{k}]");
            AssertBitEqual(serial.Dc[k], parallel.Dc[k], $"Dc[{k}]");
        }
    }

    /// <summary>A parallel grid must not lose a warning, and must not print one per chunk.</summary>
    [Fact]
    public void ParallelGrid_EmitsEachWarningExactlyOnce()
    {
        const int s = 512;
        var sdd = new SddModel("M_warn", 1,
            currentAst: [Parser.Parse("log(_v1)")],
            chargeAst: [null],
            parameters: new Dictionary<string, double>());

        var portV = new double[s];
        for (int t = 0; t < s; t++) portV[t] = t + 1.0;
        portV[5] = -2.0; portV[300] = -3.0; portV[511] = -4.0;   // spread across chunks

        int saved = SddModel.GridParallelThreshold;
        var original = Console.Error;
        var sink = new StringWriter();
        try
        {
            Console.SetError(sink);
            SddModel.GridParallelThreshold = 1;
            sdd.EvaluateGrid(portV, [], s, new GridResult());
        }
        finally { SddModel.GridParallelThreshold = saved; Console.SetError(original); }

        var lines = sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("M_warn", lines[0]);
    }

    private static void AssertBitEqual(double expected, double actual, string what)
    {
        if (BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual)) return;
        if (double.IsNaN(expected) && double.IsNaN(actual)) return;
        Assert.Fail($"{what}: scalar {expected:R} (0x{BitConverter.DoubleToInt64Bits(expected):X16}) " +
                    $"vs grid {actual:R} (0x{BitConverter.DoubleToInt64Bits(actual):X16})");
    }
}
