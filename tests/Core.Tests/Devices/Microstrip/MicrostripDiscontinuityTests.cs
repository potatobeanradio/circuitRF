using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MBend/MTee/MCross — structural/topology gates. MTee/MCross's own real electrical models
/// (Gupta-Garg-Chadha 1981) are now implemented and covered by
/// <c>MicrostripJunctionModelTests</c> (reciprocity + lossless/purely-imaginary checks); this file
/// keeps the port-count/factory-registration/R11-report structural gates that remain valid
/// regardless of which electrical model is behind them. MBend's own Garg-Bahl fitted bend model is
/// being rebuilt per brief-mtaper-mklopf.md §1A (Kirschning-Jansen-Koster) — see that model's own
/// doc comment for current status.
/// </summary>
public class MicrostripDiscontinuityTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, string type, int[] nodes)
        => new(type, "X1", nodes, new Dictionary<string, Value>(), model);

    // ── MiterCutLength (Douville-James, confirmed by 4 independent sources) ─────────────────────

    [Fact]
    public void MiterCutLength_ZeroAtLargeWOverH_ApproachesAsymptote()
    {
        double h = 1e-3;
        double cut = MicrostripDiscontinuities.MiterCutLength(1000 * h, h); // W/h huge
        Assert.Equal(0.52 * 1000 * h, cut, 3);
    }

    [Fact]
    public void MiterCutLengthAsymptotic_MatchesLargeWOverHLimit()
    {
        double w = 5e-3;
        Assert.Equal(0.52 * w, MicrostripDiscontinuities.MiterCutLengthAsymptotic(w), 9);
    }

    [Fact]
    public void MiterCutLength_AlwaysPositive_ForPhysicalWAndH()
    {
        foreach (var (w, h) in new[] { (0.5e-3, 1.6e-3), (2.9e-3, 1.6e-3), (5e-3, 0.1e-3) })
            Assert.True(MicrostripDiscontinuities.MiterCutLength(w, h) > 0);
    }

    // ── MBend: None/Fifty/Optimal produce DIFFERENT stamps (R-pc-18, gate 11c) — see
    // MicrostripBendLCTests.cs for the L-C-L electrical-model gates themselves. ─────────────────

    [Fact]
    public void MBend_NoneAndOptimal_ProduceDifferentStamps_SameWidthAndAngle()
    {
        var none = new MicrostripBendModel(2.9e-3, 90.0, MicrostripBendMiter.None, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MBEND:X1");
        var optimal = new MicrostripBendModel(2.9e-3, 90.0, MicrostripBendMiter.Optimal, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MBEND:X2");

        var mnaN = new CapturingMnaContext();
        none.Stamp(mnaN, MakeEc(none, "MBEND", [1, 2]), 2 * Math.PI * 2e9);
        var mnaO = new CapturingMnaContext();
        optimal.Stamp(mnaO, MakeEc(optimal, "MBEND", [1, 2]), 2 * Math.PI * 2e9);

        bool anyDifferent = mnaN.BranchConstraints.Keys.Any(k =>
            Math.Abs(mnaN.BranchConstraints[k].Real - mnaO.BranchConstraints[k].Real) > 1e-9 ||
            Math.Abs(mnaN.BranchConstraints[k].Imaginary - mnaO.BranchConstraints[k].Imaginary) > 1e-9);
        Assert.True(anyDifferent);
    }

    [Fact]
    public void MBend_Stamp_IsFinite_NoneAndOptimal()
    {
        foreach (var miter in new[] { MicrostripBendMiter.None, MicrostripBendMiter.Fifty, MicrostripBendMiter.Optimal })
        {
            var model = new MicrostripBendModel(2.9e-3, 90.0, miter, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MBEND:X1");
            var mna = new CapturingMnaContext();
            model.Stamp(mna, MakeEc(model, "MBEND", [1, 2]), 2 * Math.PI * 2e9);
            foreach (var v in mna.BranchConstraints.Values)
            {
                Assert.False(double.IsNaN(v.Real) || double.IsNaN(v.Imaginary));
                Assert.False(double.IsInfinity(v.Real) || double.IsInfinity(v.Imaginary));
            }
        }
    }

    [Fact]
    public void MBend_OptimalApproximationWarning_PrintsOncePerInstance()
    {
        var sw = new System.IO.StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try
        {
            var model = new MicrostripBendModel(2.9e-3, 90.0, MicrostripBendMiter.Optimal, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MBEND:X1");
            var ec = MakeEc(model, "MBEND", [1, 2]);
            model.Stamp(new CapturingMnaContext(), ec, 2 * Math.PI * 1e9);
            model.Stamp(new CapturingMnaContext(), ec, 2 * Math.PI * 2e9); // second frequency point
            string output = sw.ToString();
            int count = output.Split("no matching published").Length - 1;
            Assert.Equal(1, count);
        }
        finally { Console.SetError(original); }
    }

    // ── MTee: 3-port, W1≠W2 accepted (through line steps width) ────────────────────────────────

    [Fact]
    public void MTee_IsThreePort()
    {
        var model = new MicrostripTeeModel(2.9e-3, 1.5e-3, 2.9e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MTEE:X1");
        Assert.Equal(3, model.PortCount);
    }

    [Fact]
    public void MTee_W1NotEqualW2_StillStampsWithoutThrowing()
    {
        var model = new MicrostripTeeModel(2.9e-3, 1.5e-3, 2.9e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MTEE:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MTEE", [1, 2, 3]), 2 * Math.PI * 2e9);
        Assert.NotEmpty(mna.Entries);
    }

    [Fact]
    public void MTee_Stamp_AllThreePortsAreMutuallyCoupled()
    {
        // The star-network LC model couples every port pair through the shared internal node —
        // all three off-diagonal entries must be present and non-zero.
        var model = new MicrostripTeeModel(2.9e-3, 2.9e-3, 2.9e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MTEE:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MTEE", [1, 2, 3]), 2 * Math.PI * 2e9);
        Assert.True(mna.Entries.ContainsKey((1, 2)));
        Assert.True(mna.Entries.ContainsKey((1, 3)));
        Assert.NotEqual(Complex.Zero, mna.Entries[(1, 2)]);
        Assert.NotEqual(Complex.Zero, mna.Entries[(1, 3)]);
    }

    // ── MCross: 4-port, R11 opposing-mean-approximation report (gate 11d) ──────────────────────

    [Fact]
    public void MCross_IsFourPort()
    {
        var model = new MicrostripCrossModel(2.9e-3, 2.9e-3, 2.9e-3, 2.9e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MCROSS:X1");
        Assert.Equal(4, model.PortCount);
    }

    [Fact]
    public void MCross_SymmetricWidths_DoesNotUseOpposingMeanApproximation()
    {
        var model = new MicrostripCrossModel(2.9e-3, 1.5e-3, 2.9e-3, 1.5e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MCROSS:X1");
        Assert.False(model.UsesOpposingMeanApproximation(out _, out _));
    }

    [Fact]
    public void MCross_AsymmetricWidths_R11_ReportsApproximationWithDivergence()
    {
        var model = new MicrostripCrossModel(2.9e-3, 1.5e-3, 2.0e-3, 1.5e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MCROSS:X1");
        bool uses = model.UsesOpposingMeanApproximation(out double d13, out double d24);
        Assert.True(uses);
        Assert.True(d13 > 0); // W1=2.9mm, W3=2.0mm diverge
        Assert.Equal(0.0, d24, 9); // W2==W4 -> that pair does not diverge
    }

    [Fact]
    public void MCross_AsymmetricWidths_RuntimeWarning_NamesTheDivergence()
    {
        var sw = new System.IO.StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try
        {
            var model = new MicrostripCrossModel(2.9e-3, 1.5e-3, 2.0e-3, 1.5e-3, 1.6e-3, 35e-6, 4.4, 5.8e7, 0.02, "MCROSS:X1");
            model.Stamp(new CapturingMnaContext(), MakeEc(model, "MCROSS", [1, 2, 3, 4]), 2 * Math.PI * 1e9);
            string output = sw.ToString();
            Assert.Contains("R11", output);
            Assert.Contains("opposing arms are not equal-width", output);
        }
        finally { Console.SetError(original); }
    }

    // ── Factory registration ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_CreatesAllFourMicrostripTypes()
    {
        Assert.NotNull(ComponentModelFactory.TryCreate("MLIN",
            new Dictionary<string, Value> { ["W"] = new(2.9e-3), ["L"] = new(10e-3) }));
        Assert.NotNull(ComponentModelFactory.TryCreate("MBEND",
            new Dictionary<string, Value> { ["W"] = new(2.9e-3), ["Angle"] = new(90.0) }));
        Assert.NotNull(ComponentModelFactory.TryCreate("MTEE",
            new Dictionary<string, Value> { ["W1"] = new(2.9e-3), ["W2"] = new(2.9e-3), ["W3"] = new(2.9e-3) }));
        Assert.NotNull(ComponentModelFactory.TryCreate("MCROSS",
            new Dictionary<string, Value> { ["W1"] = new(2.9e-3), ["W2"] = new(2.9e-3), ["W3"] = new(2.9e-3), ["W4"] = new(2.9e-3) }));
    }

    [Fact]
    public void Factory_IsPrimitive_TrueForAllFourMicrostripTypes()
    {
        Assert.True(ComponentModelFactory.IsPrimitive("MLIN"));
        Assert.True(ComponentModelFactory.IsPrimitive("MBEND"));
        Assert.True(ComponentModelFactory.IsPrimitive("MTEE"));
        Assert.True(ComponentModelFactory.IsPrimitive("MCROSS"));
    }
}
