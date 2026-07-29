using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gates for brief-mtaper-mklopf.md §2-3 — MKlopf's electrical model, entry-route
/// resolution, and the Offset=0/continuity checks (§3.4).</summary>
public class MicrostripKlopfModelTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, string type, int[] nodes)
        => new(type, "X1", nodes, new Dictionary<string, Value>(), model);

    private const double HMeters = 1.6e-3;
    private const double ErFr4 = 4.4;

    // ── R-klp-2: constructor-level guard ────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_GammaMaxAtOrAboveBound_Throws()
    {
        double bound = Math.Abs((100.0 - 50.0) / (100.0 + 50.0));
        Assert.Throws<ArgumentException>(() =>
            new MicrostripKlopfModel(50.0, 100.0, 10e-3, bound, 0.0, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1"));
    }

    [Fact]
    public void Constructor_ValidGammaMax_DoesNotThrow()
    {
        _ = new MicrostripKlopfModel(50.0, 100.0, 10e-3, 0.05, 0.0, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
    }

    // ── Basic stamping sanity ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Stamp_IsReciprocal()
    {
        var model = new MicrostripKlopfModel(50.0, 100.0, 10e-3, 0.05, 0.0, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);

        Assert.Equal(2, mna.BranchCurrents.Count);
        var z12 = mna.BranchConstraints[(0, 1)];
        var z21 = mna.BranchConstraints[(1, 0)];
        Assert.Equal(z12.Real, z21.Real, 9);
        Assert.Equal(z12.Imaginary, z21.Imaginary, 9);
    }

    [Fact]
    public void Stamp_Dc_CollapsesToIdealTie()
    {
        var model = new MicrostripKlopfModel(50.0, 100.0, 10e-3, 0.05, 0.0, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MKLOPF", [1, 2]), 0.0);
        Assert.True(mna.Entries[(1, 2)].Real < -1e6);
    }

    [Fact]
    public void Stamp_ReportsSectionCountAndArcLength()
    {
        var model = new MicrostripKlopfModel(50.0, 100.0, 10e-3, 0.05, 0.0, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
        model.Stamp(new CapturingMnaContext(), MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        Assert.True(model.LastSectionCount > 0);
        Assert.Equal(10e-3, model.LastTotalArcLengthMeters, 6); // Offset=0 -> arc == axial
    }

    // ── §3.4 check 1: Offset=0 reproduces the straight-taper profile exactly ───────────────────

    [Fact]
    public void Offset_Zero_TotalArcLength_EqualsAxialLength()
    {
        var model = new MicrostripKlopfModel(50.0, 100.0, 12e-3, 0.05, 0.0, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
        model.Stamp(new CapturingMnaContext(), MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 5e9);
        Assert.Equal(12e-3, model.LastTotalArcLengthMeters, 9);
        Assert.Equal(double.PositiveInfinity, model.LastMinRadiusOfCurvatureMeters);
    }

    // ── §3.4 check 2: continuity as Offset -> 0 (no discontinuity in the design) ────────────────

    [Fact]
    public void Offset_SmallValues_ArcLengthConvergesSmoothlyToAxial()
    {
        double l = 12e-3;
        double arcAtTiny = MicrostripOffsetCenterline.TotalArcLength(l, 1e-6);
        double arcAtZero = MicrostripOffsetCenterline.TotalArcLength(l, 0.0);
        double arcAtSmall = MicrostripOffsetCenterline.TotalArcLength(l, 1e-3);

        Assert.True(Math.Abs(arcAtTiny - arcAtZero) < Math.Abs(arcAtSmall - arcAtZero));
    }

    // ── §3.4 check that R-klp-10's warning correctly stays silent on the brief's own worked
    // example, and correctly fires on a short/sharp one ─────────────────────────────────────────

    // brief-mklopf-performance-and-messages.md R-mk-7/8: MicrostripKlopfModel no longer writes to
    // Console.Error at all -- its curvature/section-count warnings are queued on its own reporter
    // and exposed via IReportsWarnings.DrainWarnings() for the engine to route into
    // ElaboratedNetlist.Warnings (see MicrostripPerformanceAndMessagesTests.cs for the dedicated
    // gate tests on that routing). These two tests were rewritten from a Console.Error capture to
    // read the drain directly.

    [Fact]
    public void Offset_BriefsOwnWorkedExample_DoesNotWarn()
    {
        // ~50 Ohm on 1.6mm FR-4 (W~3mm per the brief's own figure), L=3in, Offset=1in. A
        // genuine (non-degenerate) taper is needed for GammaMax's own guard, so Z1/Z2 differ
        // slightly rather than being identical -- the curvature check itself does not care
        // about the impedance step, only about the geometry and the local synthesized width.
        var model = new MicrostripKlopfModel(48.0, 52.0, 76.2e-3, 0.02, 25.4e-3,
            HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
        model.Stamp(new CapturingMnaContext(), MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        var warnings = ((IReportsWarnings)model).DrainWarnings();
        Assert.DoesNotContain(warnings, w => w.Message.Contains("R-klp-10"));
    }

    [Fact]
    public void Offset_ShortSharpTaper_Warns()
    {
        var model = new MicrostripKlopfModel(50.0, 100.0, 3e-3, 0.05, 2e-3,
            HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MKLOPF:X1");
        model.Stamp(new CapturingMnaContext(), MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        var warnings = ((IReportsWarnings)model).DrainWarnings();
        Assert.Contains(warnings, w => w.Message.Contains("R-klp-10"));
    }

    // ── Factory entry-route resolution (R-klp-3/3a) ─────────────────────────────────────────────

    [Fact]
    public void Factory_Z1Z2Entry_UsesGivenImpedancesDirectly()
    {
        var model = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["Z1"] = new(50.0), ["Z2"] = new(100.0), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
        }) as MicrostripKlopfModel;
        Assert.NotNull(model);
        var mna = new CapturingMnaContext();
        model!.Stamp(mna, MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        Assert.NotEmpty(mna.BranchConstraints);
    }

    [Fact]
    public void Factory_W1W2Entry_DerivesImpedances_DifferentAcrossTechnology()
    {
        // Entering W1/W2 lets impedance follow the substrate -- a different H must give a
        // different design (gate 4c's "after a technology change they give different designs").
        var thin = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["W1"] = new(2.9e-3), ["W2"] = new(1.0e-3), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
            ["H"] = new(1.6e-3), ["Er"] = new(ErFr4),
        }) as MicrostripKlopfModel;
        var thick = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["W1"] = new(2.9e-3), ["W2"] = new(1.0e-3), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
            ["H"] = new(3.2e-3), ["Er"] = new(ErFr4),
        }) as MicrostripKlopfModel;
        Assert.NotNull(thin);
        Assert.NotNull(thick);

        var mnaThin = new CapturingMnaContext();
        thin!.Stamp(mnaThin, MakeEc(thin, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        var mnaThick = new CapturingMnaContext();
        thick!.Stamp(mnaThick, MakeEc(thick, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);

        Assert.NotEqual(mnaThin.BranchConstraints[(0, 0)], mnaThick.BranchConstraints[(0, 0)]);
    }

    [Fact]
    public void Factory_Z1Z2Entry_UnaffectedByTechnologyChange()
    {
        // The Z-entry route fixes the impedances; a substrate change must NOT change the design
        // (only the synthesized widths change, not the electrical result) -- gate 4c's other half.
        var onThinH = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["Z1"] = new(50.0), ["Z2"] = new(100.0), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
            ["H"] = new(1.6e-3), ["Er"] = new(ErFr4),
        }) as MicrostripKlopfModel;
        var onThickH = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["Z1"] = new(50.0), ["Z2"] = new(100.0), ["L"] = new(10e-3), ["GammaMax"] = new(0.05),
            ["H"] = new(3.2e-3), ["Er"] = new(ErFr4),
        }) as MicrostripKlopfModel;
        Assert.NotNull(onThinH);
        Assert.NotNull(onThickH);

        var mnaThin = new CapturingMnaContext();
        onThinH!.Stamp(mnaThin, MakeEc(onThinH, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        var mnaThick = new CapturingMnaContext();
        onThickH!.Stamp(mnaThick, MakeEc(onThickH, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);

        // Same Z1/Z2/L/GammaMax/frequency -> the Klopfenstein Z-PROFILE (and hence each section's
        // static Z0) is identical regardless of H; only the dispersion correction (itself a
        // function of the synthesized width, which DOES depend on H for a fixed Z0) differs
        // slightly between the two substrates -- so the two stamps are close, not bit-identical.
        double thinImag = mnaThin.BranchConstraints[(0, 0)].Imaginary;
        double thickImag = mnaThick.BranchConstraints[(0, 0)].Imaginary;
        double fractionalDiff = Math.Abs(thinImag - thickImag) / Math.Abs(thinImag);
        Assert.InRange(fractionalDiff, 0.0, 0.05);
    }

    [Fact]
    public void Factory_LEntry_UsedDirectly()
    {
        var model = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["Z1"] = new(50.0), ["Z2"] = new(100.0), ["L"] = new(15e-3), ["GammaMax"] = new(0.05),
        }) as MicrostripKlopfModel;
        Assert.NotNull(model);
        model!.Stamp(new CapturingMnaContext(), MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        Assert.Equal(15e-3, model.LastTotalArcLengthMeters, 9);
    }

    [Fact]
    public void Factory_F3dbEntry_DerivesLength()
    {
        var model = ComponentModelFactory.TryCreate("MKLOPF", new Dictionary<string, Value>
        {
            ["Z1"] = new(50.0), ["Z2"] = new(100.0), ["F3db"] = new(10e9), ["GammaMax"] = new(0.05),
        }) as MicrostripKlopfModel;
        Assert.NotNull(model);
        model!.Stamp(new CapturingMnaContext(), MakeEc(model, "MKLOPF", [1, 2]), 2 * Math.PI * 3e9);
        Assert.True(model.LastTotalArcLengthMeters > 0);
    }

    [Fact]
    public void Factory_IsPrimitive_MKlopf()
    {
        Assert.True(ComponentModelFactory.IsPrimitive("MKLOPF"));
    }
}
