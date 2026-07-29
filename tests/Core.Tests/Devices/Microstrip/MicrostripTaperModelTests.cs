using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gates for brief-mtaper-mklopf.md §1 — MTaper (a linear-taper cascade of MLIN sections)
/// and the shared <see cref="MicrostripCascadeSectioning"/>/<see cref="MicrostripAbcd"/>
/// infrastructure it and (later) MKlopf both use.</summary>
public class MicrostripTaperModelTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, string type, int[] nodes)
        => new(type, "X1", nodes, new Dictionary<string, Value>(), model);

    private const double HMeters = 1.6e-3;
    private const double ErFr4 = 4.4;

    // ── MicrostripCascadeSectioning (R-tap-1) ───────────────────────────────────────────────────

    [Fact]
    public void ElectricalSectionCount_RisesWithFrequency()
    {
        double n1 = MicrostripCascadeSectioning.ElectricalSectionCount(0.02, 1e9, 3.0);
        double n2 = MicrostripCascadeSectioning.ElectricalSectionCount(0.02, 50e9, 3.0);
        Assert.True(n2 > n1);
    }

    [Fact]
    public void ElectricalSectionCount_AtDc_ReturnsOne()
    {
        Assert.Equal(1, MicrostripCascadeSectioning.ElectricalSectionCount(0.02, 0.0, 3.0));
    }

    [Fact]
    public void GeometricSectionCount_UniformWidth_ReturnsOne()
    {
        Assert.Equal(1, MicrostripCascadeSectioning.GeometricSectionCount(_ => 1.0e-3));
    }

    [Fact]
    public void GeometricSectionCount_LinearTaper_NeedsFiftyOrMore()
    {
        // A linear profile needs N=50 to keep every section's ΔW at the 2% ceiling (uniform step
        // size for a straight-line profile) — R-tap-1's own stated example. The search doubles
        // (1,2,4,...) rather than scanning every N, so it conservatively lands on the next power of
        // two at or above 50 (64) — never fewer sections than the criterion actually requires.
        double w1 = 1.0e-3, w2 = 3.0e-3;
        int n = MicrostripCascadeSectioning.GeometricSectionCount(t => w1 + (w2 - w1) * t);
        Assert.InRange(n, 50, 64);
    }

    [Fact]
    public void GeometricSectionCount_SteeperMidpointProfile_NeedsMoreThanLinear()
    {
        // A profile that changes width much faster near the middle than a straight line needs MORE
        // sections there to hold the same 2% local step — the direct test that this criterion is
        // genuinely profile-shape-sensitive, not hard-coded to "50."
        double w1 = 1.0e-3, w2 = 3.0e-3;
        double SteepMid(double t) => w1 + (w2 - w1) * (0.5 - 0.5 * Math.Cos(Math.PI * Math.Pow(t, 0.2)));
        int nLinear = MicrostripCascadeSectioning.GeometricSectionCount(t => w1 + (w2 - w1) * t);
        int nSteep = MicrostripCascadeSectioning.GeometricSectionCount(SteepMid);
        Assert.True(nSteep > nLinear);
    }

    // ── MicrostripAbcd ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Abcd_Identity_CascadeIsNoOp()
    {
        var section = MicrostripAbcd.UniformSection(new Complex(50, 0), new Complex(0.01, 0.5));
        var cascaded = MicrostripAbcd.Identity.Cascade(section);
        Assert.Equal(section.A, cascaded.A);
        Assert.Equal(section.D, cascaded.D);
    }

    [Fact]
    public void Abcd_TwoIdenticalHalfSections_MatchOneFullSection()
    {
        var z0 = new Complex(50, 0);
        var full = MicrostripAbcd.UniformSection(z0, new Complex(0.02, 1.0));
        var half = MicrostripAbcd.UniformSection(z0, new Complex(0.01, 0.5));
        var cascaded = half.Cascade(half);

        Assert.Equal(full.A.Real, cascaded.A.Real, 9);
        Assert.Equal(full.A.Imaginary, cascaded.A.Imaginary, 9);
        Assert.Equal(full.B.Real, cascaded.B.Real, 6);
        Assert.Equal(full.C.Real, cascaded.C.Real, 6);
    }

    [Fact]
    public void Abcd_UniformSection_ToZ_IsReciprocal()
    {
        var section = MicrostripAbcd.UniformSection(new Complex(50, 0), new Complex(0.01, 0.7));
        var (z11, z12, z21, z22) = section.ToZ();
        Assert.Equal(z12.Real, z21.Real, 9);
        Assert.Equal(z12.Imaginary, z21.Imaginary, 9);
        Assert.Equal(z11.Real, z22.Real, 9); // uniform line is symmetric port1<->port2
    }

    // ── MTaper end-to-end ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UniformTaper_W1EqualsW2_MatchesPlainMlinOfTheSameWidth()
    {
        // W1==W2 degenerates to a plain uniform line — the taper's own cascade must reproduce
        // MicrostripLineModel's stamp closely (both use the identical per-section physics; the
        // taper's N sections vs. MLIN's own single equivalent stamp should agree to a tight
        // tolerance since the underlying physics per unit length is identical).
        double w = 2.0e-3, l = 8e-3, freqHz = 3e9;
        var taper = new MicrostripTaperModel(w, w, l, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MTAPER:X1");
        var mnaT = new CapturingMnaContext();
        taper.Stamp(mnaT, MakeEc(taper, "MTAPER", [1, 2]), 2 * Math.PI * freqHz);

        var mlin = new MicrostripLineModel(w, l, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MLIN:X2");
        var mnaM = new CapturingMnaContext();
        mlin.Stamp(mnaM, MakeEc(mlin, "MLIN", [1, 2]), 2 * Math.PI * freqHz);

        // MLIN stamps Y directly (AddBlockAdmittance); MTaper stamps Z via branches. Compare by
        // converting MTaper's own Z back to Y algebraically: Y11 = Z22/det, Y12 = -Z12/det, etc.
        var z11 = mnaT.BranchConstraints[(0, 0)] * -1.0;
        var z12 = mnaT.BranchConstraints[(0, 1)] * -1.0;
        var z21 = mnaT.BranchConstraints[(1, 0)] * -1.0;
        var z22 = mnaT.BranchConstraints[(1, 1)] * -1.0;
        var det = z11 * z22 - z12 * z21;
        var y11 = z22 / det;
        var y12 = -z12 / det;

        Assert.Equal(mnaM.Entries[(1, 1)].Real, y11.Real, 2);
        Assert.Equal(mnaM.Entries[(1, 1)].Imaginary, y11.Imaginary, 2);
        Assert.Equal(mnaM.Entries[(1, 2)].Real, y12.Real, 2);
        Assert.Equal(mnaM.Entries[(1, 2)].Imaginary, y12.Imaginary, 2);
    }

    [Fact]
    public void Taper_ReportsSectionCountUsed()
    {
        var taper = new MicrostripTaperModel(1.0e-3, 3.0e-3, 10e-3, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MTAPER:X1");
        Assert.Equal(0, taper.LastSectionCount);
        taper.Stamp(new CapturingMnaContext(), MakeEc(taper, "MTAPER", [1, 2]), 2 * Math.PI * 1e9);
        Assert.True(taper.LastSectionCount >= 50); // the geometric floor for a linear taper
    }

    [Fact]
    public void Taper_HigherFrequency_UsesMoreOrEqualSections()
    {
        var taper1 = new MicrostripTaperModel(1.0e-3, 1.2e-3, 30e-3, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MTAPER:X1");
        taper1.Stamp(new CapturingMnaContext(), MakeEc(taper1, "MTAPER", [1, 2]), 2 * Math.PI * 1e9);
        int nLow = taper1.LastSectionCount;

        var taper2 = new MicrostripTaperModel(1.0e-3, 1.2e-3, 30e-3, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MTAPER:X2");
        taper2.Stamp(new CapturingMnaContext(), MakeEc(taper2, "MTAPER", [1, 2]), 2 * Math.PI * 60e9);
        int nHigh = taper2.LastSectionCount;

        Assert.True(nHigh >= nLow);
    }

    [Fact]
    public void Taper_SectionCountOverride_IsHonoured()
    {
        var taper = new MicrostripTaperModel(1.0e-3, 3.0e-3, 10e-3, HMeters, 35e-6, ErFr4, 5.8e7, 0.0,
            "MTAPER:X1", sectionCountOverride: 7);
        taper.Stamp(new CapturingMnaContext(), MakeEc(taper, "MTAPER", [1, 2]), 2 * Math.PI * 1e9);
        Assert.Equal(7, taper.LastSectionCount);
    }

    [Fact]
    public void Taper_Dc_CollapsesToIdealTie()
    {
        var taper = new MicrostripTaperModel(1.0e-3, 3.0e-3, 10e-3, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MTAPER:X1");
        var mna = new CapturingMnaContext();
        taper.Stamp(mna, MakeEc(taper, "MTAPER", [1, 2]), 0.0);
        Assert.True(mna.Entries[(1, 2)].Real < -1e6);
    }

    [Fact]
    public void Factory_CreatesMTaper()
    {
        var model = ComponentModelFactory.TryCreate("MTAPER", new Dictionary<string, Value>
        {
            ["W1"] = new(1.0e-3), ["W2"] = new(3.0e-3), ["L"] = new(10e-3),
        });
        Assert.NotNull(model);
        Assert.IsType<MicrostripTaperModel>(model);
        Assert.Equal(2, model!.PortCount);
    }

    [Fact]
    public void Factory_IsPrimitive_MTaper()
    {
        Assert.True(ComponentModelFactory.IsPrimitive("MTAPER"));
    }
}
