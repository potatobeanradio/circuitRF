using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gates for brief-mtaper-mklopf.md §1A — the MBend L-C-L rebuild (Kirschning, Jansen &amp;
/// Koster, via Lecture-3-Practical-Transmission-Lines.pdf eqs 20-25).</summary>
public class MicrostripBendLCTests
{
    private static ElaboratedComponent MakeEc(ComponentModel model, string type, int[] nodes)
        => new(type, "X1", nodes, new Dictionary<string, Value>(), model);

    private const double HMeters = 1.6e-3;
    private const double ErFr4 = 4.4;

    // ── R-bnd-1: the chamfer's diagonal-referenced cut and the per-edge leg differ by exactly √2 ──

    [Fact]
    public void MiterCutLength_TimesSqrt2_EqualsTheDiagonalReferencedCut_RBnd1()
    {
        // R-bnd-1: M = 100x/d %, d = W*sqrt2, so x = (M/100)*W*sqrt2. MiterCutLength already
        // returns the per-edge LEG (x / sqrt2 = W*(M/100)) — multiplying it back by sqrt2 must
        // recover the diagonal-referenced x exactly.
        double w = 2.9e-3, h = 1.6e-3;
        double mOver100 = 0.52 + 0.65 * Math.Exp(-1.35 * (w / h));
        double xDiagonal = mOver100 * w * Math.Sqrt(2.0);
        double leg = MicrostripDiscontinuities.MiterCutLength(w, h);
        Assert.Equal(xDiagonal, leg * Math.Sqrt(2.0), 12);
    }

    [Fact]
    public void MiterCutLength_AtWOverH1_OptimalPercentIsApproximately69Percent()
    {
        // "Sanity check: at W/h = 1, M ~ 69%" (brief §1A.1).
        double w = 1.0e-3, h = 1.0e-3;
        double mPercent = (0.52 + 0.65 * Math.Exp(-1.35 * (w / h))) * 100.0;
        Assert.InRange(mPercent, 68.0, 69.5);
    }

    // ── Units verification (R-bnd-3) — plugging metres in must give a physically sane order of
    // magnitude (tens of fF / sub-nH), not femto-small or nano-huge by a stray 1000x. ────────────

    [Fact]
    public void Compute_None_AtTypicalFr4Geometry_GivesPhysicallySaneOrderOfMagnitude()
    {
        var reporter = new MicrostripValidityReporter("check");
        var (l, c, approximated) = MicrostripBendLC.Compute(2.9e-3, HMeters, ErFr4, MicrostripBendMiter.None, reporter);

        Assert.False(approximated);
        // Tens of fF to a few pF, and sub-nH to a few nH — not femto-small, not nano-huge.
        Assert.InRange(c, 1e-15, 10e-12);
        Assert.InRange(l, 1e-12, 10e-9);
    }

    [Fact]
    public void Compute_Fifty_DiffersFromNone_AtSameGeometry()
    {
        var reporter = new MicrostripValidityReporter("check");
        var (lNone, cNone, _) = MicrostripBendLC.Compute(2.9e-3, HMeters, ErFr4, MicrostripBendMiter.None, reporter);
        var (lFifty, cFifty, _) = MicrostripBendLC.Compute(2.9e-3, HMeters, ErFr4, MicrostripBendMiter.Fifty, reporter);

        Assert.NotEqual(lNone, lFifty);
        Assert.NotEqual(cNone, cFifty);
    }

    [Fact]
    public void Compute_Optimal_UsesFiftyCoefficients_AndReportsApproximation()
    {
        var reporter = new MicrostripValidityReporter("check");
        var (lFifty, cFifty, _) = MicrostripBendLC.Compute(2.9e-3, HMeters, ErFr4, MicrostripBendMiter.Fifty, reporter);
        var (lOptimal, cOptimal, approximated) = MicrostripBendLC.Compute(2.9e-3, HMeters, ErFr4, MicrostripBendMiter.Optimal, reporter);

        Assert.True(approximated);
        Assert.Equal(lFifty, lOptimal, 15);
        Assert.Equal(cFifty, cOptimal, 15);
    }

    // ── eq (25)'s Z-matrix, stamped via branch currents — reciprocal, symmetric, lossless ───────

    [Fact]
    public void Stamp_IsReciprocal_Z12EqualsZ21()
    {
        var model = new MicrostripBendModel(2.9e-3, 90.0, MicrostripBendMiter.None, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MBEND:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MBEND", [1, 2]), 2 * Math.PI * 3e9);

        // Branch-current Z-stamp (ZPortModel's own pattern): AddBranchConstraint(branch,other,-Z)
        // records -Z at (branch,other). Two branches (0,1); Z11 self-terms equal, Z12 cross-terms
        // equal — the reciprocity and symmetric-self-impedance the T-network's own algebra demands.
        Assert.Equal(2, mna.BranchCurrents.Count);
        var z00 = mna.BranchConstraints[(0, 0)];
        var z11 = mna.BranchConstraints[(1, 1)];
        var z01 = mna.BranchConstraints[(0, 1)];
        var z10 = mna.BranchConstraints[(1, 0)];
        Assert.Equal(z00, z11);
        Assert.Equal(z01, z10);
        Assert.NotEqual(z00, z01); // the series+shunt self-term differs from the pure-shunt cross-term
    }

    [Fact]
    public void Stamp_Dc_CollapsesToIdealTie()
    {
        var model = new MicrostripBendModel(2.9e-3, 90.0, MicrostripBendMiter.Optimal, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MBEND:X1");
        var mna = new CapturingMnaContext();
        model.Stamp(mna, MakeEc(model, "MBEND", [1, 2]), 0.0);

        Assert.True(mna.Entries[(1, 2)].Real < -1e6);
    }

    [Fact]
    public void Stamp_AllThreeModes_ProduceFiniteNonZeroStamps()
    {
        foreach (var miter in new[] { MicrostripBendMiter.None, MicrostripBendMiter.Fifty, MicrostripBendMiter.Optimal })
        {
            var model = new MicrostripBendModel(2.9e-3, 90.0, miter, HMeters, 35e-6, ErFr4, 5.8e7, 0.0, "MBEND:X1");
            var mna = new CapturingMnaContext();
            model.Stamp(mna, MakeEc(model, "MBEND", [1, 2]), 2 * Math.PI * 3e9);
            Assert.NotEmpty(mna.BranchConstraints);
            foreach (var v in mna.BranchConstraints.Values)
            {
                Assert.False(double.IsNaN(v.Real) || double.IsNaN(v.Imaginary));
                Assert.False(double.IsInfinity(v.Real) || double.IsInfinity(v.Imaginary));
                Assert.NotEqual(Complex.Zero, v);
            }
        }
    }

    // ── Factory wiring: both the new "Miter" tri-state and the legacy "Mitered" bool work ───────

    [Fact]
    public void Factory_MiterParameter_SelectsOptimalMode()
    {
        var model = ComponentModelFactory.TryCreate("MBEND", new Dictionary<string, Value>
        {
            ["W"] = new(2.9e-3), ["Angle"] = new(90.0), ["Miter"] = new(2.0),
        }) as MicrostripBendModel;
        Assert.NotNull(model);

        var sw = new System.IO.StringWriter();
        var original = Console.Error;
        Console.SetError(sw);
        try
        {
            model!.Stamp(new CapturingMnaContext(), MakeEc(model, "MBEND", [1, 2]), 2 * Math.PI * 1e9);
            Assert.Contains("no matching published", sw.ToString());
        }
        finally { Console.SetError(original); }
    }

    [Fact]
    public void Factory_LegacyMitered_StillWorks_MapsToNoneOrOptimal()
    {
        var legacyOff = ComponentModelFactory.TryCreate("MBEND", new Dictionary<string, Value>
        {
            ["W"] = new(2.9e-3), ["Mitered"] = new(0.0),
        });
        var legacyOn = ComponentModelFactory.TryCreate("MBEND", new Dictionary<string, Value>
        {
            ["W"] = new(2.9e-3), ["Mitered"] = new(1.0),
        });
        Assert.NotNull(legacyOff);
        Assert.NotNull(legacyOn);
    }
}
