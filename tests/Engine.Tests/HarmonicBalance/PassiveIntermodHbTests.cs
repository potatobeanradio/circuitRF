using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// The gate that matters (brief-sys-4): two carriers at the stated power through a block with a
/// stated passive intermod produce a third-order product at the stated LEVEL, in dBm, computed end
/// to end through the real harmonic-balance solver and compared against the number the user typed.
///
/// <para>Nothing here reads a coefficient back out of the model. The expectation is the dBm on the
/// netlist line; the trip from that number, through the intercept arithmetic, into a soft-limit
/// scale, into a nonlinear current, through a Newton solve and back out as a voltage at a load is
/// exactly what is being gated.</para>
///
/// <para>The attenuator, the circulator and the in-phase coupler have a REAL S-matrix. The 90° hybrid
/// is the one whose S is complex: its quadrature half rides in an <c>H[2]</c> weighting bucket, which
/// every multi-tone solver carries since <c>MultiToneWeightedTermsTests</c> (where it is held to the
/// single-tone HB); before that a two-tone run dropped it and saw four open circuits.</para>
/// </summary>
public class PassiveIntermodHbTests(ITestOutputHelper output)
{
    private const string Analysis =
        "analysis HB1 type=hb NumFreqs=2 Tone[1]=1.99e9 Tone[2]=2.01e9 MaxMixOrder=7 MaxHarm=3 Tol=1e-12";

    private static string Source(double pcDbm) =>
        $"PnTone:Ps  n1 0  Freq[1]=1.99 GHz Pavl[1]={pcDbm} Phase[1]=0 "
      + $"Freq[2]=2.01 GHz Pavl[2]={pcDbm} Phase[2]=0 Z=50";

    private static DataSet Run(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        Assert.Single(nl.NonlinearComponents);          // the block, and nothing else in the circuit
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        Assert.True(p.IsMultiTone, "a passive-intermod gate must resolve to a two-tone HB");
        var ds = (DataSet)new HbEngine(nl, tb).Run(p);
        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    /// <summary>
    /// Power into a 50 Ω load, in dBm, from the voltage there — every node in these netlists is
    /// linear-only, so the cube carries no nonlinear terminal current at one, and |V|²/2R on the
    /// peak-phasor convention is what the load receives.
    /// </summary>
    private static double PDbm(DataSet ds, string net, int k1, int k2)
    {
        double v = TwoToneMeasurements.Tone(ds, 0, net, k1, k2).Magnitude;
        double w = v * v / (2.0 * 50.0);
        return w > 0 ? 10.0 * Math.Log10(w) + 30.0 : double.NegativeInfinity;
    }

    /// <summary>The two third-order products of a two-tone pair, which must come out equal.</summary>
    private static (double Lower, double Upper) Im3(DataSet ds, string net)
        => (PDbm(ds, net, 2, -1), PDbm(ds, net, -1, 2));

    // ── The attenuator: the level the user typed comes back out ───────────────

    private static string Pad(double pcDbm, double pimDbm, double pimPcDbm, double lossDb = 0.01) => $@"
{Source(pcDbm)}
Atten:A1  n1 0 n2 0  Loss={lossDb} Z0=50 RL=200 PIM={pimDbm} PIMPc={pimPcDbm}
R:Rl      n2 0  R=50
{Analysis}
";

    [Theory]
    [InlineData(43.0, -110.0,  0.01)]    // the datasheet shape, on the standalone generator
    [InlineData(43.0, -110.0,  3.0)]
    [InlineData(30.0, -100.0,  1.0)]
    [InlineData(20.0,  -80.0,  1.0)]
    [InlineData(10.0,  -80.0,  1.0)]
    public void APadAtItsStatedCarrierPower_ProducesItsStatedProductLevel(
        double pcDbm, double pimDbm, double lossDb)
    {
        var ds = Run(Pad(pcDbm, pimDbm, pcDbm, lossDb));
        var (lower, upper) = Im3(ds, "n2");

        output.WriteLine($"{pcDbm} dBm/carrier through a {lossDb} dB pad, stated PIM {pimDbm} dBm "
                       + $"({pimDbm - pcDbm:F0} dBc) → 2f1−f2 {lower:F4} dBm, 2f2−f1 {upper:F4} dBm");

        Assert.Equal(pimDbm, lower, 1);          // 0.1 dB, as the brief asks
        Assert.Equal(pimDbm, upper, 1);
        Assert.Equal(lower,  upper, 3);          // a symmetric pair of tones makes a symmetric pair
    }

    [Fact]
    public void TheProductRidesTheThirdPower_TenDbOfCarrierIsThirtyDbOfProduct()
    {
        // The 3:1 slope, measured over 10 dB from the power the specification was written at. It is
        // not an assumption about the model: it is what separates a genuine third-order
        // nonlinearity from a coefficient tuned to hit one point.
        const double pim = -110.0, pc = 43.0;

        foreach (double drop in new[] { 0.0, 3.0, 6.0, 10.0 })
        {
            var ds = Run(Pad(pc - drop, pim, pc, 3.0));
            double got = PDbm(ds, "n2", 2, -1);
            output.WriteLine($"carrier {pc - drop:F0} dBm → product {got:F4} dBm "
                           + $"(3:1 from {pim} predicts {pim - 3 * drop:F4})");
            Assert.Equal(pim - 3 * drop, got, 1);
        }
    }

    [Fact]
    public void ThePadsOwnLoss_IsCarriedByTheArithmetic_NotLeftToTheUser()
    {
        // The stated level is an ABSOLUTE level at the output port, so a 30 dB pad has to distort
        // far harder than a 0.01 dB one to put the same product there. brief-sys-4's one-line
        // conversion assumes a unity-gain box and would be 30 dB out here; the |Λ| factor in
        // PimOverlay.Calibrate is what makes the number the user typed the number they measure.
        foreach (double loss in new[] { 0.01, 1.0, 3.0, 10.0, 30.0 })
        {
            var ds = Run(Pad(43.0, -110.0, 43.0, loss));
            double got = PDbm(ds, "n2", 2, -1);
            output.WriteLine($"{loss} dB pad → product {got:F4} dBm against a stated −110");
            Assert.Equal(-110.0, got, 1);
        }
    }

    [Fact]
    public void ANearlyLosslessPadStopsBeingExact_WhenTheProductStopsBeingPassive()
    {
        // MEASURED, and gated so it cannot quietly get worse. The limiter is applied to the wave
        // INCIDENT on each port, which a memoryless model can only reconstruct from the port
        // voltages through T = (I + S)⁻¹ — and for a pad approaching an ideal through, T diverges
        // (it is the same degeneracy that makes a matched 0 dB pad have no Y at all). What T
        // amplifies is the block's OWN product fed back into its own argument, so the error scales
        // as |T| × the product-to-carrier amplitude ratio, and it is invisible at any level a
        // passive part is actually specified at.
        //
        // The three rows below are the same 0.01 dB pad (|T| ≈ 435) at three product levels.
        foreach (var (pc, pim, tolerance) in new[]
                 { (43.0, -110.0, 0.01), (20.0, -80.0, 1.0), (10.0, -80.0, 3.0) })
        {
            var ds = Run(Pad(pc, pim, pc, 0.01));
            double got = PDbm(ds, "n2", 2, -1);
            output.WriteLine($"0.01 dB pad, {pim - pc:F0} dBc: stated {pim}, measured {got:F4} "
                           + $"(error {got - pim:+0.0000;-0.0000})");
            Assert.True(Math.Abs(got - pim) < tolerance,
                $"{pim - pc:F0} dBc on a 0.01 dB pad is now {got - pim:F4} dB out. If this has "
              + $"IMPROVED, tighten the row; if it has worsened, the incident-wave reconstruction "
              + $"has lost conditioning. Giving the pad 1 dB of loss removes the effect entirely.");
        }

        // One decibel of loss, which is still an electrically negligible pad, is exact at all three.
        foreach (var (pc, pim) in new[] { (43.0, -110.0), (20.0, -80.0), (10.0, -80.0) })
        {
            double got = PDbm(Run(Pad(pc, pim, pc, 1.0)), "n2", 2, -1);
            output.WriteLine($"1 dB pad,    {pim - pc:F0} dBc: stated {pim}, measured {got:F4}");
            Assert.Equal(pim, got, 1);
        }
    }

    [Fact]
    public void TheLevelStaysExactFarBelowAnythingAPartClaims()
    {
        // Measured, because the first guess was wrong. A product 200 dB below the carrier looked at
        // first like a double-precision floor — it read as exactly zero — and it was not: it was
        // the overlay's own OFF threshold, which sat at -150 dBm before this test existed and
        // switched off a level a good passive part can genuinely claim. There is no floor anywhere
        // near here: -160 dBm against two +43 dBm carriers is -203 dBc and comes back to within
        // a millionth of a decibel.
        foreach (double pim in new[] { -130.0, -145.0, -150.0, -160.0 })
        {
            double got = PDbm(Run(Pad(43.0, pim, 43.0, 3.0)), "n2", 2, -1);
            output.WriteLine($"{pim - 43.0:F0} dBc: stated {pim}, measured {got:F7}");
            Assert.Equal(pim, got, 4);
        }
    }

    // ── The circulator: the product routes like the signal ────────────────────

    private static string Circ(double pcDbm, double pimDbm, double isolationDb) => $@"
{Source(pcDbm)}
Circulator:C1  n1 0 n2 0 n3 0  Direction=CW IL=0 Isolation={isolationDb} RL=200 PIM={pimDbm} PIMPc={pcDbm}
R:R2  n2 0  R=50
R:R3  n3 0  R=50
{Analysis}
";

    [Fact]
    public void ACirculatorsProduct_LeavesTheForwardPort_AndIsAbsentFromTheIsolatedOne()
    {
        // brief-sys-4's routing gate. With the isolation ideal the reverse entry is not small, it is
        // ABSENT — so the product at port 3 is zero to machine precision rather than 200 dB down.
        var ds = Run(Circ(43.0, -110.0, 200.0));

        double p2 = PDbm(ds, "n2", 2, -1);
        double v3 = TwoToneMeasurements.Tone(ds, 0, "n3", 2, -1).Magnitude;
        double v2 = TwoToneMeasurements.Tone(ds, 0, "n2", 2, -1).Magnitude;

        output.WriteLine($"port 2 product {p2:F4} dBm; port 3 holds {v3:E3} V against {v2:E3} V "
                       + $"({20 * Math.Log10(v3 / v2):F1} dB down)");
        Assert.Equal(-110.0, p2, 1);

        // 1e-8 rather than zero because the residue at port 3 is DOUBLE-PRECISION NOISE ON THE
        // CARRIER, not a stamped leakage entry: ~1e-15 V beside the 44.6 V the +43 dBm carriers put
        // on the node is 2e-17 of it. Against the product itself that is still 180 dB of isolation,
        // where the model stamps no reverse entry at all.
        Assert.True(v3 < 1e-8 * v2, $"the isolated port holds {v3:E3} V of product");
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(35.0)]
    public void AFiniteIsolationPutsTheProductAtPortThree_ByExactlyThatIsolation(double isolationDb)
    {
        // "Isolated from port 3 by the same isolation the linear path has" — measured against the
        // linear path's own isolation in the same run, not against the number on the netlist line,
        // so a model that got BOTH wrong the same way still fails.
        var ds = Run(Circ(43.0, -110.0, isolationDb));

        double carrierIso = PDbm(ds, "n3", 1, 0) - PDbm(ds, "n2", 1, 0);
        double productIso = PDbm(ds, "n3", 2, -1) - PDbm(ds, "n2", 2, -1);

        output.WriteLine($"stated {isolationDb} dB: carrier is isolated by {-carrierIso:F6} dB, "
                       + $"product by {-productIso:F6} dB");
        Assert.Equal(-isolationDb, carrierIso, 6);
        Assert.Equal(carrierIso, productIso, 3);
    }

    // ── A four-port with a real S ─────────────────────────────────────────────

    [Fact]
    public void AnInPhaseCouplersProduct_ComesOutAtTheStatedLevel_AndSplitsLikeTheSignal()
    {
        // The 4-port case for the |Λ| calibration, and the one that shows the product is routed by
        // the block's own S: the coupled port sits Coupling dB below the through port for the
        // PRODUCT exactly as it does for the carrier, and the isolated port holds nothing.
        const double coupling = 10.0;
        string cnl = $@"
{Source(43.0)}
Coupler:K1  n1 0 n2 0 n3 0 n4 0  Coupling={coupling} Phase=0 deg Directivity=200 IL=0 RL=200 PIM=-110 PIMPc=43
R:R2  n2 0  R=50
R:R3  n3 0  R=50
R:R4  n4 0  R=50
{Analysis}
";
        var ds = Run(cnl);

        double thru = PDbm(ds, "n2", 2, -1);
        double cpl  = PDbm(ds, "n3", 2, -1);
        double v4   = TwoToneMeasurements.Tone(ds, 0, "n4", 2, -1).Magnitude;
        double v2   = TwoToneMeasurements.Tone(ds, 0, "n2", 2, -1).Magnitude;

        double carrierSplit = PDbm(ds, "n2", 1, 0) - PDbm(ds, "n3", 1, 0);
        output.WriteLine($"product: thru {thru:F4} dBm, cpl {cpl:F4} dBm (Δ {thru - cpl:F4} dB); "
                       + $"carrier Δ {carrierSplit:F4} dB; iso holds {v4:E3} V");

        Assert.Equal(-110.0, thru, 1);
        Assert.Equal(carrierSplit, thru - cpl, 3);
        // What the isolated port holds is the 43 dBm carrier's round-off, not product: 5.2e-16 V on macOS and 1.7e-15 V on
        // Linux arm64 against a ~1e-6 V product, the platforms' libm differing in the last bits of a ~45 V carrier. A bound
        // 120 dB under the product still says "nothing" and sits clear of that floor; 1e-9 sat on it.
        Assert.True(v4 < 1e-6 * v2, $"the isolated port holds {v4:E3} V of product");
    }

    // ── The quadrature hybrid: a complex S, carried by its H[2] bucket ─────────

    [Fact]
    public void AQuadratureHybridsProduct_ComesOutAtTheStatedLevel_InQuadratureLikeTheSignal()
    {
        // Half of this block's admittance — for the ideal hybrid, all of it — rides in an H[2](ω) = j·sign(ω)
        // weighting bucket. Until the multi-tone solvers carried buckets, this run delivered −276 dBm to the through
        // port and read the source node 6 dB high: the hybrid was four open circuits. Now the product comes out where
        // the specification puts it, split like the carrier and in the carrier's own quadrature.
        string cnl = $@"
{Source(43.0)}
Coupler:H1  n1 0 n2 0 n3 0 n4 0  Coupling=3.0103 Phase=90 deg Directivity=200 IL=0 RL=200 PIM=-110 PIMPc=43
R:R2  n2 0  R=50
R:R3  n3 0  R=50
R:R4  n4 0  R=50
{Analysis}
";
        var ds = Run(cnl);

        double thru = PDbm(ds, "n2", 2, -1);
        double cpl  = PDbm(ds, "n3", 2, -1);
        double carrierSplit = PDbm(ds, "n2", 1, 0) - PDbm(ds, "n3", 1, 0);
        double Quadrature(int k1, int k2) =>
            (TwoToneMeasurements.Tone(ds, 0, "n3", k1, k2) / TwoToneMeasurements.Tone(ds, 0, "n2", k1, k2)).Phase * 180 / Math.PI;
        output.WriteLine($"carrier: drive {PDbm(ds, "n1", 1, 0):F4} dBm, thru {PDbm(ds, "n2", 1, 0):F4} dBm, cpl/thru ∠{Quadrature(1, 0):F4}°; "
                       + $"product: thru {thru:F4} dBm, cpl {cpl:F4} dBm, cpl/thru ∠{Quadrature(2, -1):F4}°");

        Assert.Equal(-110.0, thru, 1);
        Assert.Equal(carrierSplit, thru - cpl, 3);
        Assert.Equal(-90.0, Quadrature(1, 0), 3);
        Assert.Equal(Quadrature(1, 0), Quadrature(2, -1), 3);
    }
}
