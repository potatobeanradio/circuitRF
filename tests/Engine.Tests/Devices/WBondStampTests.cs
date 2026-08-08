using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// Oracle tiers 2, 3, 4 and 7 of brief-wbond-wbb §4 — the wBond stamp, through the real engines.
///
/// <para>WB-A's tests check the physics and WB-B's <c>ImpedanceReductionTests</c> check the
/// reduction. <b>These check that the reduction actually reaches the matrix</b>, which is a separate
/// claim: a stamp with a transposed index, a dropped off-diagonal or a sign error produces a network
/// that solves perfectly and is wrong.</para>
/// </summary>
public class WBondStampTests : IDisposable
{
    private readonly List<string> _temporaryFiles = [];

    public void Dispose()
    {
        foreach (string path in _temporaryFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Writes a .wBond file holding <paramref name="arrays"/> arrays of parallel wires.</summary>
    private string WriteDesign(int arrays, int wiresPerArray, double arraySpacingMil,
                               double pitchMil = 6.0, double lengthMil = 100.0, double heightMil = 20.0,
                               bool groundPlane = true, string material = "Gold")
    {
        var design = new WBondDesign();
        design.GroundPlane.Enabled = groundPlane;

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}" };
            for (int w = 0; w < wiresPerArray; w++)
            {
                double y = a * arraySpacingMil + w * pitchMil;
                array.Wires.Add(new Wire
                {
                    Points = { Point3.Mils(0, y, heightMil), Point3.Mils(lengthMil, y, heightMil) },
                    DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil),
                    Material = material,
                });
            }
            design.Arrays.Add(array);
        }

        string path = Path.Combine(Path.GetTempPath(), $"wbond-test-{Guid.NewGuid():N}.wBond");
        WBondIo.WriteFile(path, design);
        _temporaryFiles.Add(path);
        return path;
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    // ---------------------------------------------------------------- tier 2: the series element

    /// <summary>
    /// TIER 2 — at DC a wBond array is a plain series resistance, so it forms an ordinary divider.
    ///
    /// <para>The simplest possible end-to-end statement that the stamp is wired the right way round:
    /// current enters the input pin and leaves the output pin, and the constraint row carries
    /// <c>Z_arr</c> and not its reciprocal. The expected value comes from the reduction directly, so
    /// this tests the <i>stamp</i> rather than re-deriving the physics.</para>
    /// </summary>
    [Fact]
    public void Tier2_AtDc_AWBondArrayIsASeriesResistance()
    {
        string wbond = WriteDesign(arrays: 1, wiresPerArray: 4, arraySpacingMil: 0.0);

        // The array's own DC resistance, straight from the reduction.
        var design = WBondIo.ReadFile(wbond);
        double rArray = ImpedanceReduction.Create(design, parallel: false).ArrayImpedance(0.0)[0].Real;

        Assert.True(rArray > 0.0, "A gold bond-wire array must have a positive DC resistance.");

        const double rLoad = 1.0;   // comparable to the wires' milliohms, so the divider is measurable
        string cnl = $@"
Vdc:VS    in 0   Vdc=1
wBond:WB1 in mid 0   File=""{wbond}""
R:RL      mid 0  R={N(rLoad)} Ohm
";
        var result = NonlinearDcEngine.Run(Elaborate(cnl));
        Assert.True(result.Converged, $"DC did not converge (residual {result.FinalResidual:G3}).");

        var netlist = Elaborate(cnl);
        int node = netlist.Nodes.GetOrAssign("mid");
        double vMid = node == 0 ? 0.0 : result.NodeVoltages[node - 1];

        double expected = 1.0 * rLoad / (rArray + rLoad);
        Assert.Equal(expected, vMid, expected * 1e-6);
    }

    /// <summary>
    /// TIER 2 — more wires in an array lowers its resistance, and the array is <b>not</b> simply
    /// N times better than one wire, because the reduction accounts for the shared current.
    /// </summary>
    [Fact]
    public void Tier2_MoreWiresInAnArray_LowerItsSeriesResistance()
    {
        double Previous = double.MaxValue;

        foreach (int wires in new[] { 1, 2, 4, 8 })
        {
            var design = WBondIo.ReadFile(WriteDesign(arrays: 1, wiresPerArray: wires, arraySpacingMil: 0.0));
            double r = ImpedanceReduction.Create(design, parallel: false).ArrayImpedance(0.0)[0].Real;

            Assert.True(r < Previous, $"Adding wires must lower the array resistance; {wires} wires gave {r:E3}.");
            Previous = r;
        }
    }

    // ---------------------------------------------------------------- tier 3: through the matrix

    /// <summary>
    /// TIER 3 — <b>the mutual coupling actually reaches the matrix.</b>
    ///
    /// <para>Two arrays, one driven and one terminated. If the stamp dropped the off-diagonal
    /// <c>Z_arr[k,j]</c> terms the second array would see nothing at all; with them, the driven
    /// array induces a measurable voltage across the second. This is the assertion a stamp with a
    /// missing off-diagonal fails and every other test in this file passes.</para>
    /// </summary>
    [Fact]
    public void Tier3_MutualCouplingBetweenArrays_ReachesTheMatrix()
    {
        // Two arrays close together, so the inter-array mutual is significant.
        string near = WriteDesign(arrays: 2, wiresPerArray: 3, arraySpacingMil: 30.0);
        string far = WriteDesign(arrays: 2, wiresPerArray: 3, arraySpacingMil: 4000.0);

        double nearCoupling = InducedS21(near);
        double farCoupling = InducedS21(far);

        Assert.True(nearCoupling > 0.0,
            "A nearby second array must be coupled to the driven one; the off-diagonal Z_arr terms " +
            $"are not reaching the matrix (|S21| = {nearCoupling:E3}).");

        Assert.True(nearCoupling > farCoupling * 5.0,
            $"Coupling must fall off with separation: near {nearCoupling:E3}, far {farCoupling:E3}.");
    }

    /// <summary>|S21| between array 1's input and array 2's output, both ends terminated.</summary>
    private static double InducedS21(string wbondPath)
    {
        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 0 p2 0 0   File=""{wbondPath}""
";
        var ds = SParameterEngine.Run(Elaborate(cnl), [1e10]);
        var s = ds["S"];
        return Complex.Abs((Complex)s[0, 1, 0]);
    }

    /// <summary>
    /// TIER 3 — the stamped network is <b>reciprocal</b>: S = Sᵀ. A transposed index in the
    /// off-diagonal loop breaks this and nothing else would catch it.
    /// </summary>
    [Theory]
    [InlineData(1e9)]
    [InlineData(1e10)]
    [InlineData(4e10)]
    public void Tier3_TheStampedNetworkIsReciprocal(double frequency)
    {
        string wbond = WriteDesign(arrays: 2, wiresPerArray: 3, arraySpacingMil: 30.0);

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 0 p2 0 0   File=""{wbond}""
";
        var ds = SParameterEngine.Run(Elaborate(cnl), [frequency]);
        var s = ds["S"];

        var s21 = (Complex)s[0, 1, 0];
        var s12 = (Complex)s[0, 0, 1];

        Assert.Equal(s21.Real, s12.Real, Math.Abs(s21.Real) * 1e-9 + 1e-15);
        Assert.Equal(s21.Imaginary, s12.Imaginary, Math.Abs(s21.Imaginary) * 1e-9 + 1e-15);
    }

    /// <summary>
    /// TIER 4 — the stamped network is <b>passive</b>: no |S| entry exceeds 1 anywhere in band. A
    /// sign error on the reactance yields a network that appears to generate power.
    /// </summary>
    [Fact]
    public void Tier4_TheStampedNetworkIsPassive()
    {
        string wbond = WriteDesign(arrays: 2, wiresPerArray: 3, arraySpacingMil: 30.0);

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 0 p2 0 0   File=""{wbond}""
";
        double[] frequencies = [1e8, 1e9, 5e9, 1e10, 2e10, 4e10];
        var ds = SParameterEngine.Run(Elaborate(cnl), frequencies);
        var s = ds["S"];

        for (int f = 0; f < frequencies.Length; f++)
        {
            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    double magnitude = Complex.Abs((Complex)s[f, i, j]);
                    Assert.True(magnitude <= 1.0 + 1e-9,
                        $"|S[{i},{j}]| = {magnitude:F6} at {frequencies[f]:E1} Hz — the network is generating power.");
                }
            }
        }
    }

    /// <summary>
    /// TIER 4 — a bond wire is a series inductance, so |S21| must <b>fall</b> with frequency once
    /// ωL dominates the 50 Ω system. Guards against a stamp that lost the jω.
    /// </summary>
    [Fact]
    public void Tier4_InsertionLossRisesWithFrequency()
    {
        string wbond = WriteDesign(arrays: 1, wiresPerArray: 2, arraySpacingMil: 0.0);

        string cnl = $@"
Term:T1   p1 0   Num=1 Z=50
Term:T2   p2 0   Num=2 Z=50
wBond:WB1 p1 p2 0   File=""{wbond}""
";
        double[] frequencies = [1e8, 1e9, 1e10, 4e10];
        var ds = SParameterEngine.Run(Elaborate(cnl), frequencies);
        var s = ds["S"];

        double previous = double.MaxValue;
        for (int f = 0; f < frequencies.Length; f++)
        {
            double magnitude = Complex.Abs((Complex)s[f, 1, 0]);
            Assert.True(magnitude < previous,
                $"|S21| must fall as the series inductance takes over; at {frequencies[f]:E1} Hz it was " +
                $"{magnitude:F6}, up from {previous:F6}.");
            previous = magnitude;
        }

        Assert.True(previous < 0.9,
            $"At 40 GHz a bond wire should be well into insertion loss; |S21| = {previous:F4}.");
    }

    // ---------------------------------------------------------------- tier 7: the refusal

    /// <summary>
    /// TIER 7 / R-wbb-4 — a wBond whose ground plane is disabled has no declared return path and is
    /// <b>refused</b>, naming the instance and both remedies.
    ///
    /// <para>Reporting an inductance against a return path that does not exist would be wrong in the
    /// <b>optimistic</b> direction, which is the worst kind — so this is a refusal, not a warning.</para>
    /// </summary>
    [Fact]
    public void Tier7_GroundPlaneDisabledWithNoDeclaredReturn_IsRefusedByName()
    {
        string wbond = WriteDesign(arrays: 1, wiresPerArray: 2, arraySpacingMil: 0.0, groundPlane: false);

        string cnl = $@"
Vdc:VS    in 0   Vdc=1
wBond:WB1 in mid 0   File=""{wbond}""
R:RL      mid 0  R=1 Ohm
";
        var ex = Assert.Throws<InvalidOperationException>(() => NonlinearDcEngine.Run(Elaborate(cnl)));

        Assert.Contains("WB1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ground plane", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("return", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("optimistically low", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same design with the plane enabled solves — so the refusal is about the configuration.</summary>
    [Fact]
    public void Tier7_TheSameDesignWithThePlaneEnabled_Solves()
    {
        string wbond = WriteDesign(arrays: 1, wiresPerArray: 2, arraySpacingMil: 0.0, groundPlane: true);

        string cnl = $@"
Vdc:VS    in 0   Vdc=1
wBond:WB1 in mid 0   File=""{wbond}""
R:RL      mid 0  R=1 Ohm
";
        var result = NonlinearDcEngine.Run(Elaborate(cnl));
        Assert.True(result.Converged, $"DC did not converge (residual {result.FinalResidual:G3}).");
    }

    /// <summary>A missing design file is reported by path, not as a null-reference somewhere downstream.</summary>
    [Fact]
    public void MissingDesignFile_IsReportedByPath()
    {
        string missing = Path.Combine(Path.GetTempPath(), "definitely-not-here.wBond");
        string cnl = $@"
Vdc:VS    in 0   Vdc=1
wBond:WB1 in mid 0   File=""{missing}""
R:RL      mid 0  R=1 Ohm
";
        var ex = Assert.Throws<FileNotFoundException>(() => Elaborate(cnl));
        Assert.Contains("definitely-not-here.wBond", ex.Message, StringComparison.Ordinal);
    }
}
