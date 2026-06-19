using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// Gate tests for numpy-style bracket indexing in measurement expressions.
/// Verifies that HB1.V[:, "Vout", 1] evaluates identically to HB1.V("Vout", 1, All),
/// and covers scalars, ranges, integer pins, full expressions, error cases, and regressions.
/// </summary>
public class BracketIndexTests
{
    // ── Test fixture ─────────────────────────────────────────────────────────
    //
    // HB1:  V[sweep(3), node(2: "Vout","Vin"), harmonic(4)]   Complex
    //       I[sweep(3), branch(2: "Iout","Iin"), harmonic(4)] Complex
    //
    // DC1:  V[node(2: "Vout","Vin")]                          Complex
    //       I[branch(2: "Iout","Iin")]                        Complex

    private static (MeasurementContext ctx, Scope scope) BuildFixture()
    {
        var sweepAxis    = new Axis("sweep",   [1.0, 2.0, 3.0]);
        var nodeAxis     = new Axis("node",    [0.0, 1.0],    labels: ["Vout", "Vin"]);
        var harmAxis     = new Axis("harmonic",[0.0, 1.0, 2.0, 3.0]);
        var branchAxis   = new Axis("branch",  [0.0, 1.0],    labels: ["Iout", "Iin"]);

        // V cube: shape [3, 2, 4] — known values for sweep=0, node="Vout"(0), harm=1
        var hbV = BuildComplex3([3, 2, 4]);
        hbV[0, 0, 1] = new Complex(10.0, 2.0);  // sweep=0, Vout, harm=1
        hbV[1, 0, 1] = new Complex(11.0, 3.0);  // sweep=1, Vout, harm=1
        hbV[2, 0, 1] = new Complex(12.0, 4.0);  // sweep=2, Vout, harm=1
        var hbVCube = new DataCube([sweepAxis, nodeAxis, harmAxis], Flatten(hbV, [3,2,4]));

        // I cube: shape [3, 2, 4]
        var hbI = BuildComplex3([3, 2, 4]);
        hbI[0, 0, 1] = new Complex(1.0, 0.1);
        hbI[1, 0, 1] = new Complex(2.0, 0.2);
        hbI[2, 0, 1] = new Complex(3.0, 0.3);
        var hbICube = new DataCube([sweepAxis, branchAxis, harmAxis], Flatten(hbI, [3,2,4]));

        var hbDs = new DataSet();
        hbDs.Add("V", hbVCube);
        hbDs.Add("I", hbICube);

        // DC1: V[node(2)], I[branch(2)]
        var dcNodeAxis   = new Axis("node",   [0.0, 1.0], labels: ["Vout", "Vin"]);
        var dcBranchAxis = new Axis("branch", [0.0, 1.0], labels: ["Iout", "Iin"]);
        var dcVCube = new DataCube([dcNodeAxis],   [new Complex(5.0, 0.0), new Complex(0.5, 0.0)]);
        var dcICube = new DataCube([dcBranchAxis], [new Complex(0.1, 0.0), new Complex(0.2, 0.0)]);
        var dcDs = new DataSet();
        dcDs.Add("V", dcVCube);
        dcDs.Add("I", dcICube);

        var results = new Dictionary<string, DataSet>
        {
            ["HB1"] = hbDs,
            ["DC1"] = dcDs,
        };
        var ctx   = new MeasurementContext(results);
        var scope = new Scope("test");
        return (ctx, scope);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Complex[,,] BuildComplex3(int[] shape)
        => new Complex[shape[0], shape[1], shape[2]];

    private static Complex[] Flatten(Complex[,,] a, int[] shape)
    {
        var flat = new Complex[shape[0] * shape[1] * shape[2]];
        for (int i = 0; i < shape[0]; i++)
        for (int j = 0; j < shape[1]; j++)
        for (int k = 0; k < shape[2]; k++)
            flat[i * shape[1] * shape[2] + j * shape[2] + k] = a[i, j, k];
        return flat;
    }

    private static Value Eval(string expr, MeasurementContext ctx, Scope scope)
        => new Evaluator(ctx).Eval(expr, scope);

    // ── Test 1: Bracket ≡ accessor (parity) ──────────────────────────────────

    [Fact]
    public void Bracket_Accessor_Parity_V()
    {
        var (ctx, scope) = BuildFixture();
        var byBracket   = Eval("HB1.V[:, \"Vout\", 1]", ctx, scope);
        var byAccessor  = Eval("HB1.V(\"Vout\", 1, All)", ctx, scope);

        Assert.Equal(ValueKind.Cube, byBracket.Kind);
        Assert.Equal(ValueKind.Cube, byAccessor.Kind);
        var bc = byBracket.AsCube();
        var ac = byAccessor.AsCube();
        Assert.Equal(ac.Rank, bc.Rank);
        Assert.Equal(ac.Axes[0].Length, bc.Axes[0].Length);
        for (int i = 0; i < ac.Axes[0].Length; i++)
            Assert.Equal((Complex)ac[i], (Complex)bc[i]);
    }

    [Fact]
    public void Bracket_Accessor_Parity_I()
    {
        var (ctx, scope) = BuildFixture();
        var byBracket   = Eval("HB1.I[:, \"Iout\", 1]", ctx, scope);
        var byAccessor  = Eval("HB1.I(\"Iout\", 1, All)", ctx, scope);

        Assert.Equal(ValueKind.Cube, byBracket.Kind);
        Assert.Equal(ValueKind.Cube, byAccessor.Kind);
        var bc = byBracket.AsCube();
        var ac = byAccessor.AsCube();
        Assert.Equal(ac.Rank, bc.Rank);
        for (int i = 0; i < ac.Axes[0].Length; i++)
            Assert.Equal((Complex)ac[i], (Complex)bc[i]);
    }

    // ── Test 2: Bracket scalar ────────────────────────────────────────────────

    [Fact]
    public void Bracket_Scalar_LabelPin_I()
    {
        var (ctx, scope) = BuildFixture();
        // DC1.I["Iout"] → single complex scalar
        var v = Eval("DC1.I[\"Iout\"]", ctx, scope);
        Assert.Equal(ValueKind.Complex, v.Kind);
        Assert.Equal(new Complex(0.1, 0.0), v.AsComplex());
    }

    [Fact]
    public void Bracket_Scalar_IntPin()
    {
        var (ctx, scope) = BuildFixture();
        // DC1.I[0] → same as DC1.I["Iout"]
        var v = Eval("DC1.I[0]", ctx, scope);
        Assert.Equal(ValueKind.Complex, v.Kind);
        Assert.Equal(new Complex(0.1, 0.0), v.AsComplex());
    }

    // ── Test 3: Bracket range ─────────────────────────────────────────────────

    [Fact]
    public void Bracket_Range()
    {
        var (ctx, scope) = BuildFixture();
        // HB1.V[:, "Vout", 1:3] → keep sweep, fix node, range of harmonics → rank-2 [sweep(3), harmonic(2)]
        var v = Eval("HB1.V[:, \"Vout\", 1:3]", ctx, scope);
        Assert.Equal(ValueKind.Cube, v.Kind);
        var cube = v.AsCube();
        Assert.Equal(2, cube.Rank);
        Assert.Equal(3, cube.Axes[0].Length);  // sweep
        Assert.Equal(2, cube.Axes[1].Length);  // harmonics 1..2
    }

    // ── Test 4: Bracket integer pin ───────────────────────────────────────────

    [Fact]
    public void Bracket_Index_AllIntPin_Scalar()
    {
        var (ctx, scope) = BuildFixture();
        // HB1.V[0, 0, 1] → all axes pinned → scalar
        var v = Eval("HB1.V[0, 0, 1]", ctx, scope);
        Assert.True(v.Kind is ValueKind.Real or ValueKind.Complex,
            $"Expected scalar, got {v.Kind}");
    }

    // ── Test 5: Full expression (the motivating use-case) ────────────────────

    [Fact]
    public void Bracket_FullExpression_ParsesAndEvaluates()
    {
        var (ctx, scope) = BuildFixture();
        // The original failing case: pasted from trace card, now should work
        var v = Eval("real(0.5 * HB1.V[:, \"Vout\", 1] * conj(HB1.I[:, \"Iout\", 1]))", ctx, scope);
        Assert.Equal(ValueKind.Cube, v.Kind);
        var cube = v.AsCube();
        Assert.Equal(DataKind.Real, cube.DataKind);
        Assert.Equal(3, cube.Axes[0].Length);  // over sweep axis
    }

    // ── Test 6: Token count mismatch ──────────────────────────────────────────

    [Fact]
    public void Bracket_TokenCountMismatch_Throws()
    {
        var (ctx, scope) = BuildFixture();
        // HB1.V has rank 3; give only 2 tokens → clear error with axis list
        var ex = Assert.Throws<ExpressionException>(
            () => Eval("HB1.V[\"Vout\", 1]", ctx, scope));
        Assert.Contains("3 axis", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 7: Unknown label ─────────────────────────────────────────────────

    [Fact]
    public void Bracket_UnknownLabel_Throws()
    {
        var (ctx, scope) = BuildFixture();
        var ex = Assert.Throws<ExpressionException>(
            () => Eval("HB1.V[:, \"nope\", 1]", ctx, scope));
        Assert.Contains("Available", ex.Message);
    }

    // ── Test 8: Tilde parse error ─────────────────────────────────────────────

    [Fact]
    public void Bracket_Tilde_ParseError()
    {
        var (ctx, scope) = BuildFixture();
        var ex = Assert.Throws<ParseException>(
            () => Eval("HB1.V[~, :]", ctx, scope));
        Assert.Contains("curve family", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 9: No regression on existing expressions ────────────────────────

    [Fact]
    public void NoRegression_ExistingExpressions()
    {
        var (ctx, scope) = BuildFixture();
        var ev = new Evaluator(ctx);

        // Existing accessor form still works
        var v1 = ev.Eval("HB1.V(\"Vout\", 1, All)", scope, null);
        Assert.Equal(ValueKind.Cube, v1.Kind);

        // Plain arithmetic
        var v2 = ev.Eval("2 + 3 * 4", scope, null);
        Assert.Equal(14.0, v2.AsReal(), 1e-12);

        // Complex constant
        var v3 = ev.Eval("j", scope, null);
        Assert.Equal(ValueKind.Complex, v3.Kind);
        Assert.Equal(Complex.ImaginaryOne, v3.AsComplex());

        // Conditional
        var v4 = ev.Eval("if(1 == 1, 42, 0)", scope, null);
        Assert.Equal(42.0, v4.AsReal(), 1e-12);
    }
}
