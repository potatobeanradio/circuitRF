using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using CircuitRF.WBond.Mom;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>The mutual inductance between two bond wires, lumped against distributed</b> (owner question,
/// 2026-08-18). The self inductance is what §6.6 compares; this is the off-diagonal, and it turns out
/// to behave completely differently.
///
/// <h3>Getting a mutual out of a MoM result at all</h3>
/// <para>The solve publishes a 2M × 2M <i>terminal</i>-basis <c>Z_port</c>, so there is no mutual in it
/// to read — it has to be transformed onto the array basis:
/// <see cref="WireMomSolver.PortImpedanceInArrayBasis"/>, which is <c>T Z_port Tᵀ</c> with T's rows
/// <c>+1</c> at each array's input terminal and <c>−1</c> at its output. Then
/// <c>M_ij = Im(Z_arr[i,j])/ω</c>. The zero row sum is what makes it work at low frequency: it
/// annihilates the common-mode <c>1/(jωC)</c> open circuit exactly instead of cancelling megohms
/// against a fraction of an ohm.</para>
///
/// <para><b><see cref="WireMomSolver.SeriesArmImpedance"/> would answer nothing.</b> It removes the
/// shunt by construction and is therefore provably equal to the lumped model (§6.2's identity gate), so
/// its mutual is the lumped mutual whatever the mesh. The comparison has to come out of the full
/// solve.</para>
/// </summary>
public class WBondMomMutualTests(ITestOutputHelper output)
{
    /// <summary>Two identical 100 mil bonds, <b>one per array</b>, so the mutual is a port quantity.</summary>
    private static WBondDesign TwoWires(double pitchMil)
    {
        long loopNm = WBondUnits.ToNm(30.0, WBondUnit.Mil);
        long diameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var design = new WBondDesign();
        for (int a = 0; a < 2; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, a * pitchMil, 4), Point3.Mils(100, a * pitchMil, 2),
                diameterNm, "Gold", loopHeightNm: loopNm));
            design.Arrays.Add(array);
        }
        return design;
    }

    /// <summary>The same <c>T Z Tᵀ</c>, applied to the LUMPED terminal admittance — the control.</summary>
    private static Complex[] LumpedArrayBasis(Mat<Complex> y)
    {
        int t = y.RowCount, m = t / 2;

        var flat = new Complex[t * t];
        for (int i = 0; i < t; i++) for (int j = 0; j < t; j++) flat[i * t + j] = y[i, j];

        var lu = ComplexLu.Factor(flat, t);
        var z = new Complex[t * t];
        var rhs = new Complex[t];
        for (int j = 0; j < t; j++)
        {
            Array.Clear(rhs);
            rhs[j] = Complex.One;
            var column = lu.Solve(rhs);
            for (int i = 0; i < t; i++) z[i * t + j] = column[i];
        }

        var arr = new Complex[m * m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                arr[i * m + j] = z[(2 * i) * t + 2 * j]     - z[(2 * i) * t + 2 * j + 1]
                               - z[(2 * i + 1) * t + 2 * j] + z[(2 * i + 1) * t + 2 * j + 1];
        return arr;
    }

    private static readonly double[] Grid = [0.01e9, 0.1e9, 0.5e9, 1e9, 2e9, 5e9, 10e9];

    [Theory]
    [InlineData(5.0)]
    [InlineData(10.0)]
    [InlineData(20.0)]
    public void TheMutualDivergesFarFasterThanTheSelf_AndTheLumpedModelUnderstatesCoupling(double pitchMil)
    {
        var design = TwoWires(pitchMil);
        var reduction = ImpedanceReduction.Create(design);
        var lumpedY = WBondTouchstoneExport.TerminalAdmittances(design, Grid);

        var solvers = new[] { 12, 24, 48 }
            .Select(n => WireMomSolver.Create(design, WireMomSettings.Default with { TargetSegmentsPerWire = n }))
            .ToArray();

        double resonanceGhz = CapacitanceReduction.SelfResonanceHz(
            reduction.InductanceOnlyReduction(), reduction.Capacitance!.TerminalShuntMatrix()) * 1e-9;

        output.WriteLine($"=== {pitchMil} mil pitch — lumped self-resonance {resonanceGhz:F2} GHz, N_s = {solvers[1].SegmentCount}");
        output.WriteLine("| f (GHz) | L11 lumped (pH) | L11 MoM (pH) | ΔL11 % | M12 lumped (pH) | M12 MoM (pH) | ΔM12 % | ΔM/ΔL | k lumped | k MoM |");

        double worstMeshDrift = 0.0, ratioAt10G = 0.0;

        for (int fi = 0; fi < Grid.Length; fi++)
        {
            double omega = 2.0 * Math.PI * Grid[fi];

            var lumped = LumpedArrayBasis(lumpedY[fi]);
            var mom = solvers[1].PortImpedanceInArrayBasis(Grid[fi]);

            double l11L = lumped[0].Imaginary / omega * 1e12, m12L = lumped[1].Imaginary / omega * 1e12;
            double l11M = mom[0].Imaginary / omega * 1e12,    m12M = mom[1].Imaginary / omega * 1e12;

            double dL = 100.0 * (l11M - l11L) / l11L;
            double dM = 100.0 * (m12M - m12L) / m12L;

            output.WriteLine(
                $"| {Grid[fi] * 1e-9,7:0.##} | {l11L,15:F2} | {l11M,12:F2} | {dL,6:F3} | {m12L,15:F3} | " +
                $"{m12M,12:F3} | {dM,6:F3} | {(dL == 0.0 ? 0.0 : dM / dL),5:F1} | {m12L / l11L,8:F4} | {m12M / l11M,5:F4} |");

            // THE MUTUAL IS MESH-CONVERGED, so any difference below is the MODEL and not the mesh. It
            // converges far better than the capacitance does (WM-1 §9.7) because the current path is
            // subdivision-exact and the charge path is not.
            double coarse = solvers[0].PortImpedanceInArrayBasis(Grid[fi])[1].Imaginary / omega * 1e12;
            double fine   = solvers[2].PortImpedanceInArrayBasis(Grid[fi])[1].Imaginary / omega * 1e12;
            worstMeshDrift = Math.Max(worstMeshDrift, Math.Abs(fine - coarse) / Math.Abs(m12M));

            // (1) AT LOW FREQUENCY IT IS AN IDENTITY, exactly as the self inductance is: partial mutual
            // inductance is additive under subdivision, so a uniform current gives the same number.
            if (Grid[fi] <= 0.1e9)
                Assert.True(Math.Abs(dM) < 0.01,
                    $"M12 at {Grid[fi] * 1e-9} GHz differs by {dM:F4} % — that should be an identity.");

            // (2) THE LUMPED MODEL UNDERSTATES THE COUPLING at every frequency above the identity.
            // Relative, not absolute: at the two lowest points the two models agree to their last
            // digits, so an absolute epsilon on a ~1,000 pH quantity is a rounding coin-flip.
            Assert.True(m12M >= m12L * (1.0 - 1e-6),
                $"MoM mutual {m12M:F4} pH is BELOW lumped {m12L:F4} pH at {Grid[fi] * 1e-9} GHz.");

            // (3) THE HEADLINE. The mutual's error is an order of magnitude worse than the self's.
            if (Grid[fi] >= 1e9)
            {
                Assert.True(dM / dL > 10.0,
                    $"At {Grid[fi] * 1e-9} GHz the mutual error is only {dM / dL:F1}x the self error.");
                ratioAt10G = dM / dL;
            }
        }

        output.WriteLine($"worst 12↔48 segment drift in M12: {100 * worstMeshDrift:F4} %; ΔM/ΔL at 10 GHz: {ratioAt10G:F1}");

        Assert.True(worstMeshDrift < 1e-3,
            $"M12 moves {100 * worstMeshDrift:F4} % between 12 and 48 segments — this comparison is " +
            "measuring the mesh, not the models.");
    }

    /// <summary>
    /// The transform is only trustworthy well below self-resonance, and this says where — by running it
    /// on the <b>lumped</b> model, whose array impedance is independently known from
    /// <see cref="ImpedanceReduction.ArrayImpedance"/>. A control that needs no MoM at all.
    /// </summary>
    [Fact]
    public void TheArrayBasisTransform_IsExactAtLowFrequency_AndDegradesTowardResonance()
    {
        var design = TwoWires(10.0);
        var reduction = ImpedanceReduction.Create(design);
        double[] grid = [10e6, 1e9, 10e9];
        var lumpedY = WBondTouchstoneExport.TerminalAdmittances(design, grid);

        double[] expected = [1e-6, 1e-3, 1e-1];

        for (int fi = 0; fi < grid.Length; fi++)
        {
            var viaTransform = LumpedArrayBasis(lumpedY[fi]);
            var direct = reduction.ArrayImpedance(grid[fi]);

            double error = Math.Abs(viaTransform[1].Imaginary - direct[1].Imaginary)
                         / Math.Abs(direct[1].Imaginary);

            output.WriteLine($"{grid[fi] * 1e-9,7:0.###} GHz — transform vs ArrayImpedance: {error:E2}");
            Assert.True(error < expected[fi],
                $"The differential transform costs {error:E2} at {grid[fi] * 1e-9} GHz.");
        }
    }
}
