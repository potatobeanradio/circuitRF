using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gates for the MTee/MCross gap-fill (Gupta, Garg &amp; Chadha 1981, §6.2.6/§6.2.7 —
/// docs/sonnet-briefs/extract.pdf). Both models are lossless LC networks (no resistive element
/// anywhere in their equivalent circuit), so their stamped Y-matrix must be reciprocal AND purely
/// imaginary — both are strong, independent checks against a transcription/algebra error, not
/// merely "does it run."</summary>
public class MicrostripJunctionModelTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, string type, int[] nodes)
        => new(type, "X1", nodes, new Dictionary<string, Value>(), model);

    private const double ErAlumina = 9.9;
    private const double HMeters = 0.635e-3; // 25 mil alumina, the source's own illustrative substrate
    private const double TMeters = 5e-6;

    // ── MTee ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MTee_Stamp_IsReciprocal()
    {
        var model = new MicrostripTeeModel(1.0e-3, 1.0e-3, 0.6e-3, HMeters, TMeters, ErAlumina,
            5.8e7, 0.0, "MTEE:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MTEE", [1, 2, 3]), 2 * Math.PI * 3e9);

        Assert.Equal(mna.Entries[(1, 2)], mna.Entries[(2, 1)]);
        Assert.Equal(mna.Entries[(1, 3)], mna.Entries[(3, 1)]);
        Assert.Equal(mna.Entries[(2, 3)], mna.Entries[(3, 2)]);
        // The two main-line self-terms are equal (both through legs use the same L1).
        Assert.Equal(mna.Entries[(1, 1)], mna.Entries[(2, 2)]);
    }

    [Fact]
    public void MTee_Stamp_IsPurelyImaginary_LosslessNetwork()
    {
        var model = new MicrostripTeeModel(1.0e-3, 1.0e-3, 0.6e-3, HMeters, TMeters, ErAlumina,
            5.8e7, 0.0, "MTEE:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MTEE", [1, 2, 3]), 2 * Math.PI * 3e9);

        foreach (var (key, y) in mna.Entries)
            Assert.True(Math.Abs(y.Real) < 1e-9, $"entry {key} has non-negligible real part {y.Real:G6} — should be lossless");
    }

    [Fact]
    public void MTee_Dc_CollapsesToIdealJunction()
    {
        var model = new MicrostripTeeModel(1.0e-3, 1.0e-3, 0.6e-3, HMeters, TMeters, ErAlumina,
            5.8e7, 0.0, "MTEE:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MTEE", [1, 2, 3]), 0.0);

        // DC: ideal tie between all three ports — a large negative off-diagonal, large positive diagonal.
        Assert.True(mna.Entries[(1, 2)].Real < -1e6);
        Assert.True(mna.Entries[(1, 3)].Real < -1e6);
    }

    [Fact]
    public void MTee_UnequalThroughWidths_DoesNotThrow_UsesMean()
    {
        var model = new MicrostripTeeModel(1.2e-3, 0.8e-3, 0.6e-3, HMeters, TMeters, ErAlumina,
            5.8e7, 0.0, "MTEE:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MTEE", [1, 2, 3]), 2 * Math.PI * 3e9);
        Assert.NotEmpty(mna.Entries);
    }

    // ── MCross ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MCross_Stamp_IsReciprocal()
    {
        var model = new MicrostripCrossModel(1.0e-3, 0.8e-3, 1.0e-3, 0.8e-3, HMeters, TMeters,
            ErAlumina, 5.8e7, 0.0, "MCROSS:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MCROSS", [1, 2, 3, 4]), 2 * Math.PI * 3e9);

        Assert.Equal(mna.Entries[(1, 2)], mna.Entries[(2, 1)]);
        Assert.Equal(mna.Entries[(1, 3)], mna.Entries[(3, 1)]);
        Assert.Equal(mna.Entries[(1, 4)], mna.Entries[(4, 1)]);
        Assert.Equal(mna.Entries[(2, 3)], mna.Entries[(3, 2)]);
        Assert.Equal(mna.Entries[(2, 4)], mna.Entries[(4, 2)]);
        Assert.Equal(mna.Entries[(3, 4)], mna.Entries[(4, 3)]);
        // Symmetric widths: the two through-line self-terms match, and both stub-coupling terms
        // from arm1 match those from arm3 (the through arms are electrically equivalent).
        Assert.Equal(mna.Entries[(1, 1)], mna.Entries[(3, 3)]);
        Assert.Equal(mna.Entries[(1, 2)], mna.Entries[(3, 2)]);
        Assert.Equal(mna.Entries[(1, 4)], mna.Entries[(3, 4)]);
    }

    [Fact]
    public void MCross_Stamp_IsPurelyImaginary_LosslessNetwork()
    {
        var model = new MicrostripCrossModel(1.0e-3, 0.8e-3, 1.0e-3, 0.8e-3, HMeters, TMeters,
            ErAlumina, 5.8e7, 0.0, "MCROSS:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MCROSS", [1, 2, 3, 4]), 2 * Math.PI * 3e9);

        foreach (var (key, y) in mna.Entries)
            Assert.True(Math.Abs(y.Real) < 1e-9, $"entry {key} has non-negligible real part {y.Real:G6} — should be lossless");
    }

    [Fact]
    public void MCross_Arm2AndArm4_HaveDifferentSelfAndCouplingTerms_ConfirmingTheAsymmetricTopology()
    {
        // arm2 (up) carries the extra L3 hop; arm4 (down) connects directly — Fig 6.3(e)'s own
        // confirmed asymmetry (see the class doc comment). If this ever collapsed to Y22==Y44 the
        // asymmetric-topology reading would have been silently lost.
        var model = new MicrostripCrossModel(1.0e-3, 0.8e-3, 1.0e-3, 0.8e-3, HMeters, TMeters,
            ErAlumina, 5.8e7, 0.0, "MCROSS:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MCROSS", [1, 2, 3, 4]), 2 * Math.PI * 3e9);

        Assert.NotEqual(mna.Entries[(2, 2)], mna.Entries[(4, 4)]);
        Assert.NotEqual(mna.Entries[(1, 2)], mna.Entries[(1, 4)]);
    }

    [Fact]
    public void MCross_Dc_CollapsesToIdealJunction()
    {
        var model = new MicrostripCrossModel(1.0e-3, 0.8e-3, 1.0e-3, 0.8e-3, HMeters, TMeters,
            ErAlumina, 5.8e7, 0.0, "MCROSS:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MCROSS", [1, 2, 3, 4]), 0.0);

        Assert.True(mna.Entries[(1, 2)].Real < -1e6);
        Assert.True(mna.Entries[(1, 4)].Real < -1e6);
    }

    [Fact]
    public void MCross_AsymmetricOpposingArms_ReportsApproximation_StillStamps()
    {
        var model = new MicrostripCrossModel(1.0e-3, 0.8e-3, 1.4e-3, 0.5e-3, HMeters, TMeters,
            ErAlumina, 5.8e7, 0.0, "MCROSS:X1");
        Assert.True(model.UsesOpposingMeanApproximation(out double d13, out double d24));
        Assert.True(d13 > 0.0);
        Assert.True(d24 > 0.0);

        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MCROSS", [1, 2, 3, 4]), 2 * Math.PI * 3e9);
        Assert.NotEmpty(mna.Entries);
    }
}
