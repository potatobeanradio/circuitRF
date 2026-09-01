using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Tests.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// The shared ideal-S-block stamp (brief-sys-2) and its first two users, at the model level: the
/// S-matrix each set of parameters produces, the three rules the base class owns, and the shape of
/// what reaches the matrix.
///
/// <para>Every expected number here is computed FROM THE dB VALUES the user typed, in this file.
/// Reading a coefficient back out of the model and comparing it with itself would prove nothing;
/// what needs gating is the trip from "10 dB of loss" to a linear amplitude and from there into a
/// constraint row.</para>
///
/// <para>The end-to-end half — a swept solve returning exactly the S the parameters state — lives in
/// <c>tests/Engine.Tests/Devices/IdealSBlockSParamTests.cs</c>, because it needs an engine.</para>
/// </summary>
public class IdealSBlockModelTests
{
    private static double Amp(double db) => Math.Pow(10.0, -db / 20.0);

    // ── The attenuator's S ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(10.0)]
    [InlineData(40.0)]
    public void Attenuator_TransmitsTheStatedLoss_AndIsMatchedByDefault(double lossDb)
    {
        var s = new AttenuatorModel(lossDb, 50.0, 200.0).SAt(2 * Math.PI * 1e9);

        Assert.Equal(Amp(lossDb), s[0, 1].Real, 12);
        Assert.Equal(Amp(lossDb), s[1, 0].Real, 12);
        Assert.Equal(0.0, s[0, 1].Imaginary, 12);

        // "Matched" has to mean the entry is not there, not that it rounds to not-there: Stamp skips
        // an exactly-zero S entry, so a 1e-10 would put two phantom reflections into every solve.
        Assert.Equal(Complex.Zero, s[0, 0]);
        Assert.Equal(Complex.Zero, s[1, 1]);
    }

    [Fact]
    public void Attenuator_AtZeroLossAndDefaultReturnLoss_IsExactlyTheIdealThrough()
    {
        // S = [[0,1],[1,0]] — the matrix with no Z form at all, and the one a closed switch and a
        // lowpass at DC both degenerate to. It is a legitimate part to place, and SYS-4 turns it
        // into the standalone PIM generator.
        var s = new AttenuatorModel(0.0, 50.0, 200.0).SAt(0.0);
        Assert.Equal(Complex.Zero, s[0, 0]);
        Assert.Equal(Complex.Zero, s[1, 1]);
        Assert.Equal(Complex.One,  s[0, 1]);
        Assert.Equal(Complex.One,  s[1, 0]);
    }

    [Fact]
    public void Attenuator_AFiniteReturnLossIsStamped_AndAHugeOneIsNot()
    {
        var mismatched = new AttenuatorModel(10.0, 50.0, 20.0).SAt(0.0);
        Assert.Equal(Amp(20.0), mismatched[0, 0].Real, 12);
        Assert.Equal(Amp(20.0), mismatched[1, 1].Real, 12);

        // 149 is below the 150 dB "off" threshold and is therefore taken literally; 150 is not.
        Assert.NotEqual(Complex.Zero, new AttenuatorModel(10, 50, 149.0).SAt(0.0)[0, 0]);
        Assert.Equal(Complex.Zero,    new AttenuatorModel(10, 50, 150.0).SAt(0.0)[0, 0]);
    }

    [Fact]
    public void Attenuator_LossIsNeverSnapped_BecauseAttenuatingIsWhatThePartIsFor()
    {
        // A suppression that large means "absent". A LOSS that large means a 200 dB pad, and the
        // two must not share a threshold.
        var s = new AttenuatorModel(200.0, 50.0, 200.0).SAt(0.0);
        Assert.Equal(1e-10, s[0, 1].Real, 15);
        Assert.NotEqual(Complex.Zero, s[0, 1]);
    }

    [Fact]
    public void Attenuator_IsTwoPorts_Linear_AndNamesItsTerminalsByNumber()
    {
        var m = new AttenuatorModel(10, 50, 200);
        Assert.Equal(2, m.PortCount);
        Assert.Equal(ModelKind.Linear, m.Kind);
        Assert.Equal(["1", "2"], m.TerminalNames);
    }

    // ── The switch's S, state by state ────────────────────────────────────────

    [Fact]
    public void Spst_Closed_IsTheIdealThrough_AndOpenReflectiveIsTheIdealOpen()
    {
        var closed = new SwitchModel(1, 1, 0, 200, SwitchOffState.Reflective, 50, 200).SAt(0.0);
        Assert.Equal(Complex.Zero, closed[0, 0]);
        Assert.Equal(Complex.Zero, closed[1, 1]);
        Assert.Equal(Complex.One,  closed[0, 1]);
        Assert.Equal(Complex.One,  closed[1, 0]);

        // S = I — which has no Z matrix — and it reduces in the stamp to i_p = 0 at both ports.
        var open = new SwitchModel(1, 0, 0, 200, SwitchOffState.Reflective, 50, 200).SAt(0.0);
        Assert.Equal(Complex.One,  open[0, 0]);
        Assert.Equal(Complex.One,  open[1, 1]);
        Assert.Equal(Complex.Zero, open[0, 1]);
        Assert.Equal(Complex.Zero, open[1, 0]);
    }

    [Fact]
    public void Spst_OpenAbsorptive_IsTwoMatchedTerminationsThatCannotSeeEachOther()
    {
        var open = new SwitchModel(1, 0, 0, 200, SwitchOffState.Absorptive, 50, 200).SAt(0.0);
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            Assert.Equal(Complex.Zero, open[p, q]);
    }

    [Theory]
    [InlineData(0.5, 25.0, 15.0)]
    [InlineData(0.0, 60.0, 30.0)]
    public void Spst_EveryNonIdealityLandsWhereItIsStated(double il, double iso, double rl)
    {
        var closed = new SwitchModel(1, 1, il, iso, SwitchOffState.Reflective, 50, rl).SAt(0.0);
        Assert.Equal(Amp(il), closed[0, 1].Real, 12);
        Assert.Equal(Amp(il), closed[1, 0].Real, 12);
        Assert.Equal(Amp(rl), closed[0, 0].Real, 12);
        Assert.Equal(Amp(rl), closed[1, 1].Real, 12);

        // Open: the leakage is the isolation, and a REFLECTIVE open throw is an open circuit
        // regardless of what return loss the closed path was given.
        var open = new SwitchModel(1, 0, il, iso, SwitchOffState.Reflective, 50, rl).SAt(0.0);
        Assert.Equal(Amp(iso), open[0, 1].Real, 12);
        Assert.Equal(1.0,      open[0, 0].Real, 12);
        Assert.Equal(1.0,      open[1, 1].Real, 12);
    }

    /// <summary>
    /// The SPDT's whole 3×3, state by state and off-state by off-state, rebuilt here from the dB
    /// values rather than compared against the model's own arithmetic. Port 0 is the common port;
    /// ports 1 and 2 are the throws.
    /// </summary>
    [Theory]
    [InlineData(2, 0, 1.0, 25.0, 20.0, true)]
    [InlineData(2, 0, 1.0, 25.0, 20.0, false)]
    [InlineData(2, 1, 0.0, 200.0, 200.0, true)]
    [InlineData(2, 1, 0.4, 30.0, 18.0, true)]
    [InlineData(2, 1, 0.4, 30.0, 18.0, false)]
    [InlineData(2, 2, 0.4, 30.0, 18.0, true)]
    [InlineData(2, 2, 0.4, 30.0, 18.0, false)]
    [InlineData(2, 3, 0.4, 30.0, 18.0, true)]      // a throw the switch does not have closes nothing
    [InlineData(3, 2, 0.2, 35.0, 22.0, true)]      // and the rule does not stop at two throws
    public void SwitchD_TheWholeMatrix(int throws, int state, double il, double iso, double rl,
                                       bool reflective)
    {
        var off = reflective ? SwitchOffState.Reflective : SwitchOffState.Absorptive;
        var s   = new SwitchModel(throws, state, il, iso, off, 50, rl).SAt(0.0);

        int    n              = 1 + throws;
        double thru           = Amp(il);
        double leak           = iso >= 150 ? 0.0 : Amp(iso);
        double refl           = rl  >= 150 ? 0.0 : Amp(rl);
        double open           = reflective ? 1.0 : 0.0;
        bool   anythingClosed = state >= 1 && state <= throws;

        var t = new double[n];
        for (int p = 1; p < n; p++) t[p] = p == state ? thru : leak;

        Assert.Equal(anythingClosed ? refl : open, s[0, 0].Real, 12);
        for (int p = 1; p < n; p++)
        {
            Assert.Equal(p == state ? refl : open, s[p, p].Real, 12);
            Assert.Equal(t[p], s[0, p].Real, 12);
            Assert.Equal(t[p], s[p, 0].Real, 12);
            for (int q = p + 1; q < n; q++)
            {
                Assert.Equal(t[p] * t[q], s[p, q].Real, 12);
                Assert.Equal(t[p] * t[q], s[q, p].Real, 12);
            }
        }

        // and nothing anywhere in it is complex — this family is frequency-flat and real.
        for (int p = 0; p < n; p++)
        for (int q = 0; q < n; q++)
            Assert.Equal(0.0, s[p, q].Imaginary);
    }

    [Fact]
    public void SwitchD_Default_LeavesTheOpenThrowCompletelyDecoupled()
    {
        // The default SPDT: State 1, no loss, isolation and return loss both "off". Every leakage
        // term is EXACTLY zero, so the throw-to-throw product is too and nothing is stamped for it.
        var s = new SwitchModel(2, 1, 0, 200, SwitchOffState.Reflective, 50, 200).SAt(0.0);
        Assert.Equal(Complex.Zero, s[0, 2]);
        Assert.Equal(Complex.Zero, s[2, 0]);
        Assert.Equal(Complex.Zero, s[1, 2]);
        Assert.Equal(Complex.Zero, s[2, 1]);
        Assert.Equal(Complex.One,  s[2, 2]);   // an ideal open at the throw that is not made
    }

    [Fact]
    public void Switch_PortCountAndTerminalNamesFollowTheThrowCount()
    {
        var spst = new SwitchModel(1, 1, 0, 200, SwitchOffState.Reflective, 50, 200);
        Assert.Equal(2, spst.PortCount);
        Assert.Equal(["1", "2"], spst.TerminalNames);

        var spdt = new SwitchModel(2, 1, 0, 200, SwitchOffState.Reflective, 50, 200);
        Assert.Equal(3, spdt.PortCount);
        Assert.Equal(["com", "1", "2"], spdt.TerminalNames);

        // Below one throw is not a switch; it falls back to SPST rather than producing a 1-port.
        Assert.Equal(2, new SwitchModel(0, 1, 0, 200, SwitchOffState.Reflective, 50, 200).PortCount);
    }

    // ── Rule 1: S(−ω) = conj(S(ω)) ────────────────────────────────────────────

    /// <summary>A block with a genuinely complex S — the shape the quadrature coupler will have.</summary>
    private sealed class QuadratureProbe() : IdealSBlockModel([50.0, 50.0])
    {
        protected override void FillS(double omega, Complex[,] s)
        {
            Assert.True(omega >= 0, "FillS must never be asked for a negative omega");
            s[0, 1] = s[1, 0] = -Complex.ImaginaryOne / Math.Sqrt(2.0);
            s[0, 0] = new Complex(0.1, 0.2);
        }
    }

    [Fact]
    public void NegativeOmega_ConjugatesTheMatrix_AndTheSubclassNeverSeesIt()
    {
        var m = new QuadratureProbe();
        double w = 2 * Math.PI * 1e9;

        var pos = (Complex[,])m.SAt(+w).Clone();
        var neg = m.SAt(-w);

        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            Assert.Equal(Complex.Conjugate(pos[p, q]), neg[p, q]);

        Assert.NotEqual(pos[0, 1], neg[0, 1]);   // and it actually moved
    }

    [Fact]
    public void ARealSSeesNoDifference_WhichIsWhyTheRuleIsFreeForEveryBlockHere()
    {
        var m = new AttenuatorModel(6, 50, 20);
        var pos = (Complex[,])m.SAt(+1e9).Clone();
        var neg = m.SAt(-1e9);
        for (int p = 0; p < 2; p++)
        for (int q = 0; q < 2; q++)
            Assert.Equal(pos[p, q], neg[p, q]);
    }

    // ── Rule 2: Z0 > 0 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    public void ANonPositiveReferenceImpedanceFallsBackToFiftyOhms(double z0)
    {
        // √Z0 of a zero or negative number is a NaN or an imaginary, and either surfaces as a
        // non-convergence with nothing attached to it. MixerModel's constructor does the same.
        var m = new AttenuatorModel(10, z0, 200);
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));
    }

    // ── What actually reaches the matrix ──────────────────────────────────────

    private static ElaboratedComponent Comp(ComponentModel m, params int[] nodes)
        => new("Test", "X1", nodes, new Dictionary<string, Value>(), m);

    [Fact]
    public void TheStamp_IsTheWaveConstraintRow_WrittenOutCoefficientByCoefficient()
    {
        // Nets [1,0, 2,0]: both ports ground-referenced, which is what the schematic tile emits.
        const double z0 = 75.0, lossDb = 6.0, rlDb = 20.0;
        double rt = Math.Sqrt(z0), thru = Amp(lossDb), refl = Amp(rlDb);

        var mna = new CapturingMnaContext();
        var m   = new AttenuatorModel(lossDb, z0, rlDb);
        m.Stamp(mna, Comp(m, 1, 0, 2, 0), 2 * Math.PI * 1e9);

        Assert.Equal([(0, 1, 0), (1, 2, 0)], mna.BranchCurrents);

        // Row 0:  (v0 − Z0·i0)/√Z0 − S00·(v0 + Z0·i0)/√Z0 − S01·(v1 + Z0·i1)/√Z0 = 0
        Assert.Equal((1.0 - refl) / rt, mna.NodeConstraints[(0, 1)].Real, 12);
        Assert.Equal(-thru / rt,        mna.NodeConstraints[(0, 2)].Real, 12);
        Assert.Equal(-rt * (1.0 + refl), mna.BranchConstraints[(0, 0)].Real, 12);
        Assert.Equal(-thru * rt,         mna.BranchConstraints[(0, 1)].Real, 12);

        // Row 1, the mirror image of it.
        Assert.Equal((1.0 - refl) / rt, mna.NodeConstraints[(1, 2)].Real, 12);
        Assert.Equal(-thru / rt,        mna.NodeConstraints[(1, 1)].Real, 12);
        Assert.Equal(-rt * (1.0 + refl), mna.BranchConstraints[(1, 1)].Real, 12);
        Assert.Equal(-thru * rt,         mna.BranchConstraints[(1, 0)].Real, 12);

        // Nothing is written into a ground column.
        Assert.DoesNotContain(mna.NodeConstraints.Keys, k => k.Node == 0);
    }

    [Fact]
    public void AnIdealBlockStampsNoEntryForATermThatIsNotThere()
    {
        // The default SPDT. Its only off-diagonal entry is the closed path; the open throw's
        // leakage, the throw-to-throw product and both return losses are absent from the matrix
        // rather than present at 1e-10.
        var mna = new CapturingMnaContext();
        var m   = new SwitchModel(2, 1, 0, 200, SwitchOffState.Reflective, 50, 200);
        m.Stamp(mna, Comp(m, 1, 0, 2, 0, 3, 0), 0.0);

        // Branch-constraint entries: the three diagonal −√Z0 terms and S01 and S10. Five, and not
        // one more — the open throw's leakage, the throw-to-throw product and both return losses
        // are all EXACTLY zero and so are absent rather than present at 1e-10. The ideal open at
        // throw 2 is S22 = 1, which lands on the diagonal key that already exists and doubles it.
        Assert.Equal(5, mna.BranchConstraints.Count);
        Assert.Equal(-2.0 * Math.Sqrt(50.0), mna.BranchConstraints[(2, 2)].Real, 12);
        Assert.Equal(-1.0 * Math.Sqrt(50.0), mna.BranchConstraints[(0, 0)].Real, 12);
        Assert.True(mna.BranchConstraints.ContainsKey((0, 1)));
        Assert.True(mna.BranchConstraints.ContainsKey((1, 0)));
        Assert.False(mna.BranchConstraints.ContainsKey((0, 2)));
        Assert.False(mna.BranchConstraints.ContainsKey((2, 0)));
        Assert.False(mna.BranchConstraints.ContainsKey((1, 2)));
        Assert.False(mna.BranchConstraints.ContainsKey((2, 1)));
    }

    [Fact]
    public void EachPortStampsAgainstItsOwnMinusNet_NotAgainstGround()
    {
        // The 2N-net convention is per-PORT, not a shared reference: ZPortModel is the precedent and
        // the differential tiles depend on it. Nets [1,2, 3,4] — four distinct non-ground nodes.
        var mna = new CapturingMnaContext();
        var m   = new AttenuatorModel(0, 50, 200);       // ideal through: only S01/S10 are non-zero
        m.Stamp(mna, Comp(m, 1, 2, 3, 4), 0.0);

        Assert.Equal([(0, 1, 2), (1, 3, 4)], mna.BranchCurrents);

        double rt = Math.Sqrt(50.0);
        Assert.Equal(+1.0 / rt, mna.NodeConstraints[(0, 1)].Real, 12);
        Assert.Equal(-1.0 / rt, mna.NodeConstraints[(0, 2)].Real, 12);
        Assert.Equal(-1.0 / rt, mna.NodeConstraints[(0, 3)].Real, 12);   // −S01·v1+/√Z0
        Assert.Equal(+1.0 / rt, mna.NodeConstraints[(0, 4)].Real, 12);   // +S01·v1−/√Z0
    }
}
