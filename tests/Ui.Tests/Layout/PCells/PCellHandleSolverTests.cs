using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// M2 of brief-pcell-parameter-handles.md — the solver, tested against SYNTHETIC generators rather
/// than the built-ins.
///
/// <para>That is deliberate and it is the only way to cover what matters. The paths this has to
/// survive are non-linear, integer-quantized, internally-clamped and dead-derivative geometry, and
/// no shipping component is all four. A synthetic generator states its relationship in one line, so
/// a failure names the behaviour rather than the cell.</para>
/// </summary>
public class PCellHandleSolverTests
{
    private const string Gid = "SYNTH";

    /// <summary>A generator whose grip sits at (f(value), 0) — one line per relationship under
    /// test. The handle is anchored at the origin along +X, so the projection IS f(value).</summary>
    private static Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> Synth(
        Func<double, double> f, string parameter = "L")
        => p =>
        {
            long x = (long)Math.Round(f(p.Real(parameter)));
            return new PCellResult(
                [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = -10, X2 = x, Y2 = 10 }],
                [],
                Handles: [new PCellHandle(parameter, 0, 0, x, 0, AxisDeg: 0)]);
        };

    private static PCellHandle HandleOf(
        Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> gen,
        IReadOnlyDictionary<string, PCellValue> p) => gen(p).Handles![0];

    private static Dictionary<string, PCellValue> Params(params (string Name, PCellValue Value)[] kv)
    {
        var d = new Dictionary<string, PCellValue>(StringComparer.Ordinal);
        foreach (var (n, v) in kv) d[n] = v;
        return d;
    }

    // ── Probe ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_LinearGenerator_MeasuresTheSensitivityWithNoUnitInTheDeclaration()
    {
        // 1 unit of value = 1000 DBU of travel, a relationship the handle never states.
        var gen = Synth(v => v * 1000.0);
        var p = Params(("L", PCellValue.Real(2.0)));

        Assert.True(PCellHandleSolver.MeasureSensitivity(gen, p, HandleOf(gen, p), 0, out double vpp, out var why));
        Assert.Equal(PCellHandleRejection.None, why);
        Assert.Equal(1.0 / 1000.0, vpp, 9);
    }

    [Fact]
    public void Probe_FromZero_UsesTheAbsoluteFallback_AndStillMeasures()
    {
        // The relative probe is useless at zero; the absolute fallback plus geometric growth is what
        // rescues it, and it must do so without knowing the parameter's unit.
        var gen = Synth(v => v * 1000.0);
        var p = Params(("L", PCellValue.Real(0.0)));

        Assert.True(PCellHandleSolver.MeasureSensitivity(gen, p, HandleOf(gen, p), 0, out double vpp, out _));
        Assert.Equal(1.0 / 1000.0, vpp, 9);
    }

    [Fact]
    public void Probe_VeryCoarseParameter_GrowsTheStepUntilTheGripMoves()
    {
        // One value unit = one DBU. The first relative probe (1e-3 of 2.0) moves the grip by
        // 0.002 DBU, i.e. nothing at integer resolution; only geometric growth finds it.
        var gen = Synth(v => v);
        var p = Params(("L", PCellValue.Real(2.0)));

        Assert.True(PCellHandleSolver.MeasureSensitivity(gen, p, HandleOf(gen, p), 0, out double vpp, out _));
        Assert.Equal(1.0, vpp, 6);
    }

    [Fact]
    public void Probe_DeadDerivative_IsRejectedAsUnmeasurable_NotSilentlyZero()
    {
        var gen = Synth(_ => 5000.0);   // the grip never moves, whatever the parameter says
        var p = Params(("L", PCellValue.Real(2.0)));

        Assert.False(PCellHandleSolver.MeasureSensitivity(gen, p, HandleOf(gen, p), 0, out _, out var why));
        Assert.Equal(PCellHandleRejection.Unmeasurable, why);
    }

    [Fact]
    public void Probe_GeneratorThatThrows_IsReported_NeverPropagated()
    {
        var gen = Synth(v => v * 1000.0);
        var p = Params(("L", PCellValue.Real(2.0)));
        var handle = HandleOf(gen, p);
        Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> boom =
            _ => throw new InvalidOperationException("the script raised");

        Assert.False(PCellHandleSolver.MeasureSensitivity(boom, p, handle, 0, out _, out var why));
        Assert.Equal(PCellHandleRejection.GeneratorFailed, why);
    }

    // ── Validation (design §8) ───────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_UndeclaredParameter_IsRejectedByName()
    {
        var handle = new PCellHandle("NotAParameter", 0, 0, 100, 0, 0);
        var why = PCellHandleSolver.Validate(handle, Params(("L", PCellValue.Real(1.0))));

        Assert.Equal(PCellHandleRejection.UnknownParameter, why);
        Assert.Contains("NotAParameter", PCellHandleSolver.Explain(why, Gid, handle));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("flag")]
    public void Validate_NonNumericParameter_IsRejected(string kind)
    {
        var value = kind == "text" ? PCellValue.Text("nch") : PCellValue.Bool(true);
        var handle = new PCellHandle("Model", 0, 0, 100, 0, 0);

        Assert.Equal(PCellHandleRejection.NotNumeric,
            PCellHandleSolver.Validate(handle, Params(("Model", value))));
    }

    [Fact]
    public void Validate_AngularKind_IsAccepted_LikeLinear()
    {
        // This asserted the OPPOSITE while Angular was declared-but-unimplemented. Both kinds are
        // live now, so UnsupportedKind is unreachable from Validate — the member and its message stay
        // for the WIRE path, where an unrecognised kind genuinely can still arrive.
        var handle = new PCellHandle("A", 0, 0, 100, 0, 0, PCellHandleKind.Angular);

        Assert.Equal(PCellHandleRejection.None,
            PCellHandleSolver.Validate(handle, Params(("A", PCellValue.Real(1.0)))));
    }

    [Theory]
    [InlineData(PCellHandleRejection.UnknownParameter)]
    [InlineData(PCellHandleRejection.NotNumeric)]
    [InlineData(PCellHandleRejection.UnsupportedKind)]
    [InlineData(PCellHandleRejection.Unmeasurable)]
    [InlineData(PCellHandleRejection.GeneratorFailed)]
    public void EveryRejection_ExplainsItself_NamingTheGeneratorAndTheParameter(PCellHandleRejection why)
    {
        var text = PCellHandleSolver.Explain(why, Gid, new PCellHandle("W", 0, 0, 1, 0, 0));

        Assert.NotEqual("", text);
        Assert.Contains(Gid, text);
        Assert.Contains("W", text);
    }

    // ── Solve ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Solve_LinearGenerator_LandsExactlyOnTheTarget()
    {
        var gen = Synth(v => v * 1000.0);
        var p = Params(("L", PCellValue.Real(2.0)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        var solved = PCellHandleSolver.Solve(gen, p, handle, 0, targetProjection: 7000, vpp);

        Assert.True(solved.Ok);
        Assert.True(solved.Converged);
        Assert.Equal(7.0, solved.Value.AsReal(), 6);
        Assert.Equal(7000, solved.AchievedProjection, 0);
    }

    [Fact]
    public void Solve_QuadraticGenerator_ConvergesWithinTheIterationCap()
    {
        // Gate 8. A single linear extrapolation from the measured slope lands far off here; only the
        // regenerate-and-correct loop reaches the target, which is the whole reason R-pch-3 exists.
        var gen = Synth(v => v * v * 100.0);
        var p = Params(("L", PCellValue.Real(3.0)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        var solved = PCellHandleSolver.Solve(gen, p, handle, 0, targetProjection: 2500, vpp);

        Assert.True(solved.Ok);
        Assert.True(solved.Converged);
        Assert.Equal(5.0, solved.Value.AsReal(), 3);   // 5^2 * 100 = 2500
    }

    [Fact]
    public void Solve_IntegerParameter_CommitsAnInt_NotAWholeReal()
    {
        // Gate 13, and B0's rule: the kind belongs to the cell that declared it. A flipped kind
        // changes PCellValue.ToString(), which IS the content hash naming the generated cell.
        var gen = Synth(v => v * 500.0, "Fingers");
        var p = Params(("Fingers", PCellValue.Int(4)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        var solved = PCellHandleSolver.Solve(gen, p, handle, 0, targetProjection: 3500, vpp);

        Assert.True(solved.Ok);
        Assert.Equal(PCellValueKind.Int, solved.Value.Kind);
        Assert.Equal(7, solved.Value.AsInt());
    }

    [Fact]
    public void Solve_IntegerParameter_OffLattice_TakesTheReachableValue_AndSaysItDidNotConverge()
    {
        var gen = Synth(v => v * 500.0, "Fingers");
        var p = Params(("Fingers", PCellValue.Int(4)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        // 3750 sits between 7 fingers (3500) and 8 (4000) — unreachable by construction.
        var solved = PCellHandleSolver.Solve(gen, p, handle, 0, targetProjection: 3750, vpp);

        Assert.True(solved.Ok);              // not converging is a normal outcome, never an error
        Assert.False(solved.Converged);
        Assert.Equal(PCellValueKind.Int, solved.Value.Kind);
        Assert.InRange(solved.Value.AsInt(), 7, 8);
    }

    [Fact]
    public void Solve_GeneratorThatClampsInternally_ReportsWhereItActuallyLanded()
    {
        // The generator refuses to go past 5.0 and says nothing about it — the case a declared
        // Min/Max cannot cover and R-pch-3 has to.
        var gen = Synth(v => Math.Min(v, 5.0) * 1000.0);
        var p = Params(("L", PCellValue.Real(2.0)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        var solved = PCellHandleSolver.Solve(gen, p, handle, 0, targetProjection: 9000, vpp);

        Assert.True(solved.Ok);
        Assert.False(solved.Converged);
        Assert.Equal(5000, solved.AchievedProjection, 0);   // the grip is drawn where it really is
    }

    [Fact]
    public void Solve_DeclaredMax_StopsTheGripAtTheBound()
    {
        var gen = Synth(v => v * 1000.0);
        var p = Params(("L", PCellValue.Real(2.0)));
        var declared = HandleOf(gen, p) with { Max = 6.0 };
        PCellHandleSolver.MeasureSensitivity(gen, p, declared, 0, out double vpp, out _);

        var solved = PCellHandleSolver.Solve(gen, p, declared, 0, targetProjection: 20000, vpp);

        Assert.True(solved.Ok);
        Assert.Equal(6.0, solved.Value.AsReal(), 6);
    }

    [Fact]
    public void Solve_MultipleHandlesOnOneParameter_ResolvesByNameWhenTheSlotMoves()
    {
        // A centred width declares two grips for one parameter. The solver must still find "the same
        // handle" after a regeneration that reorders or resizes the list.
        Func<IReadOnlyDictionary<string, PCellValue>, PCellResult> gen = p =>
        {
            long w = (long)Math.Round(p.Real("W") * 1000.0);
            return new PCellResult([], [], Handles:
            [
                new PCellHandle("L", 0, 0, 500, 0, 0),
                new PCellHandle("W", 0, 0, 0, w / 2, 90),
                new PCellHandle("W", 0, 0, 0, -w / 2, 270),
            ]);
        };
        var p = Params(("L", PCellValue.Real(1.0)), ("W", PCellValue.Real(2.0)));
        var top = gen(p).Handles![1];

        Assert.True(PCellHandleSolver.MeasureSensitivity(gen, p, top, 1, out double vpp, out _));
        var solved = PCellHandleSolver.Solve(gen, p, top, 1, targetProjection: 2500, vpp);

        Assert.True(solved.Ok);
        Assert.Equal(5.0, solved.Value.AsReal(), 6);   // half-width 2500 DBU ⇒ W = 5.0
    }

    // ── R-pch-11 determinism ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Solve_Twice_FromTheSameStart_CommitsABitIdenticalValue()
    {
        // Gate 9's headline. The committed value feeds PCellValue.ToString(), which IS the content
        // hash naming the generated cell — a value differing in its last digit mints a second cell
        // folder for one design intent.
        var gen = Synth(v => Math.Sqrt(Math.Max(v, 0.0)) * 3000.0);
        var p = Params(("L", PCellValue.Real(2.0)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        var a = PCellHandleSolver.Solve(gen, p, handle, 0, 4321, vpp);
        var b = PCellHandleSolver.Solve(gen, p, handle, 0, 4321, vpp);

        Assert.Equal(a.Value.AsReal().ToString("R"), b.Value.AsReal().ToString("R"));
        Assert.Equal(a.Value.ToString(), b.Value.ToString());
    }

    [Fact]
    public void SolvedValue_IsOnTheSignificantDigitLattice()
    {
        var gen = Synth(v => Math.Sqrt(Math.Max(v, 0.0)) * 3000.0);
        var p = Params(("L", PCellValue.Real(2.0)));
        var handle = HandleOf(gen, p);
        PCellHandleSolver.MeasureSensitivity(gen, p, handle, 0, out double vpp, out _);

        var solved = PCellHandleSolver.Solve(gen, p, handle, 0, 4321, vpp);
        double v = solved.Value.AsReal();

        Assert.Equal(v, PCellHandleSolver.RoundSignificant(v, PCellHandleSolver.SignificantDigits));
    }

    [Theory]
    [InlineData(1.23456789012345, 1.23456789012)]
    [InlineData(0.000123456789012345, 0.000123456789012)]
    [InlineData(0.0, 0.0)]
    public void RoundSignificant_KeepsTwelveDigits_AtAnyMagnitude(double input, double expected)
        => Assert.Equal(expected, PCellHandleSolver.RoundSignificant(input, 12), 15);

    [Fact]
    public void RoundSignificant_ExtremeMagnitude_DoesNotOverflowToNonsense()
    {
        Assert.Equal(1e-320, PCellHandleSolver.RoundSignificant(1e-320, 12));

        double big = PCellHandleSolver.RoundSignificant(1e300, 12);
        Assert.True(double.IsFinite(big), "an extreme magnitude must not scale itself into infinity");
        Assert.True(Math.Abs(big - 1e300) / 1e300 < 1e-11);
    }

    // ── Projection ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Project_Linear_IsSignedAlongTheDeclaredAxis()
    {
        var alongX = new PCellHandle("L", 0, 0, 100, 0, AxisDeg: 0);

        Assert.Equal(100, alongX.Project(100, 0), 6);
        Assert.Equal(-40, alongX.Project(-40, 0), 6);
        Assert.Equal(0, alongX.Project(0, 999), 6);      // perpendicular travel changes nothing
    }

    [Fact]
    public void Project_Linear_At90Degrees_MeasuresY()
    {
        var alongY = new PCellHandle("W", 0, 0, 0, 50, AxisDeg: 90);

        Assert.Equal(50, alongY.ProjectedPosition, 6);
        Assert.Equal(-50, alongY.Project(0, -50), 6);
    }

    [Fact]
    public void Project_Angular_IsNormalisedToHalfTurns()
    {
        var swing = new PCellHandle("A", 0, 0, 100, 0, AxisDeg: 0, Kind: PCellHandleKind.Angular);

        Assert.Equal(90, swing.Project(0, 100), 6);
        Assert.Equal(-90, swing.Project(0, -100), 6);
        Assert.Equal(180, swing.Project(-100, 0), 6);    // never +180 one call and -180 the next
    }
}
