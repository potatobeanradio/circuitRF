using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using NumFlat;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MCLINN, N coupled microstrip lines from the cross-section's own C and L: two strips are MCLIN's pair, three equal
/// strips are a reciprocal, mirror-symmetric, passive 6-port with their even, odd and middle modes, and the plan section
/// is the element wired by hand.
/// </summary>
public class MicrostripMultiLineTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Ports(int n) => string.Join("\n",
        Enumerable.Range(1, n).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm"));

    private static Complex[,] SAt(string cnl, double freqHz, int n)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static double Worst(Complex[,] a, Complex[,] b, Func<int, int> p)
    {
        double worst = 0;
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                worst = Math.Max(worst, (a[i, j] - b[p(i), p(j)]).Magnitude);
        return worst;
    }

    /// <summary>
    /// Two strips 1.1 mm wide on 20 mil RO4350B at 1 GHz: the field solve's even and odd modes are MCLIN's
    /// Kirschning-Jansen ones within 2 %, and a 45 mm pair (about a quarter wave) has MCLIN's S-parameters within 0.02.
    /// MCLINN's pins are the near ends first (p1, p3), then the far ends (p2, p4), in MCLIN's nets.
    /// </summary>
    [Theory]
    [InlineData(0.2e-3)]
    [InlineData(0.5e-3)]
    public void TwoStrips_AreMclinsPair(double s)
    {
        const double f = 1e9;
        var kj = new MicrostripCoupledLineModel(1.1e-3, s, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "t").ModesAt(f);
        var modes = new MicrostripMultiLineModel([1.1e-3, 1.1e-3], [s], 1.0, Ro4350b, "t").Section.Modes;
        Assert.True(modes.TryIdentifyEvenOdd(out int e, out int o));
        output.WriteLine($"S = {s * 1e3} mm: Ze {modes.Z0(e):F2} / {kj.Ze:F2}, Zo {modes.Z0(o):F2} / {kj.Zo:F2}, "
                       + $"eeff,e {modes.Eeff[e]:F4} / {kj.EeffE:F4}, eeff,o {modes.Eeff[o]:F4} / {kj.EeffO:F4}");
        Assert.InRange(modes.Z0(e) / kj.Ze, 0.98, 1.02);
        Assert.InRange(modes.Z0(o) / kj.Zo, 0.98, 1.02);
        Assert.InRange(modes.Eeff[e] / kj.EeffE, 0.98, 1.02);
        Assert.InRange(modes.Eeff[o] / kj.EeffO, 0.98, 1.02);

        string geometry = $"W=1.1e-3 S={F(s)} L=45e-3 {Sub}";
        var mclin = SAt($"{Ports(4)}\nMCLIN:K1 p1 p2 p3 p4 {geometry}", f, 4);
        var mclinn = SAt($"{Ports(4)}\nMCLINN:K1 p1 p3 p2 p4 N=2 {geometry}", f, 4);
        double worst = Worst(mclinn, mclin, i => i);
        output.WriteLine($"  |S31| {mclin[2, 0].Magnitude:F4} / {mclinn[2, 0].Magnitude:F4}, worst |dS| {worst:G3}");
        Assert.True(worst < 0.02, $"worst |dS| {worst:G3}");
    }

    /// <summary>
    /// Three equal strips: reciprocal (S = Sᵀ to the last bits, as the modal sum assembles it), the same seen from either
    /// end, mirror-symmetric about the middle strip to the solver's grid (which is not itself mirror-exact: about 1e-3 of
    /// a mode's pattern), and passive; of the three modes one is odd
    /// about the middle (the outer strips opposite, the middle one still) and two even, the c mode with every strip one
    /// way and the middle mode with the middle strip against the outer two.
    /// </summary>
    [Fact]
    public void ThreeEqualStrips_AreReciprocalSymmetricAndPassive_WithEvenOddAndMiddleModes()
    {
        var model = new MicrostripMultiLineModel([1.1e-3, 1.1e-3, 1.1e-3], [0.3e-3, 0.3e-3], 1.0, Ro4350b, "t");
        var tv = model.Section.Modes.Tv;
        int odd = 0, even = 0, middle = 0;
        for (int m = 0; m < 3; m++)
        {
            double a = tv[0, m], b = tv[1, m], c = tv[2, m];
            double big = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
            output.WriteLine($"mode {m}: ({a / big:F4}, {b / big:F4}, {c / big:F4}), eeff {model.Section.Modes.Eeff[m]:F4}, "
                           + $"Z0 {model.Section.Modes.Z0(m):F2}");
            if (Math.Abs(a + c) < 5e-3 * big && Math.Abs(b) < 5e-3 * big) odd++;
            else if (Math.Abs(a - c) < 5e-3 * big && a * b > 0) even++;
            else if (Math.Abs(a - c) < 5e-3 * big && a * b < 0) middle++;
        }
        Assert.Equal((1, 1, 1), (odd, even, middle));

        var s = SAt($"{Ports(6)}\nMCLINN:K1 p1 p2 p3 p4 p5 p6 N=3 W=1.1e-3 S=0.3e-3 L=30e-3 {Sub}", 2e9, 6);
        double reciprocity = Transposed(s);
        double ends = Worst(s, s, i => (i + 3) % 6);
        double mirror = Worst(s, s, i => i < 3 ? 2 - i : 8 - i);
        var mat = new Mat<Complex>(6, 6);
        for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) mat[i, j] = s[i, j];
        double sigmaMax = mat.Svd().S.Max();
        output.WriteLine($"reciprocity {reciprocity:G3}, end to end {ends:G3}, mirror {mirror:G3}, largest singular value {sigmaMax:F6}");
        Assert.True(reciprocity < 1e-12, $"S - Sᵀ {reciprocity:G3}");
        Assert.True(ends < 1e-12, $"end to end {ends:G3}");
        Assert.True(mirror < 1e-3, $"mirror {mirror:G3}");
        Assert.True(sigmaMax <= 1.0 + 1e-12, $"largest singular value {sigmaMax}");
        Assert.True(sigmaMax < 1.0, "a lossy line absorbs some of every excitation");
    }

    private static double Transposed(Complex[,] s)
    {
        double worst = 0;
        for (int i = 0; i < s.GetLength(0); i++)
            for (int j = 0; j < s.GetLength(1); j++)
                worst = Math.Max(worst, (s[i, j] - s[j, i]).Magnitude);
        return worst;
    }

    /// <summary>
    /// A plan of a three-strip section of unequal widths and gaps, the first strip's far end run on by an MLIN through an
    /// internal node, stamped by <see cref="MicrostripPlanModel"/>, is MCLINN and MLIN wired by hand to 1e-9: the
    /// section's nodes (strip by strip) reach the element's pins (near ends, then far ends) in the documented order.
    /// </summary>
    [Theory]
    [InlineData(0.8e9)]
    [InlineData(2.4e9)]
    [InlineData(5.0e9)]
    public void TheSection_IsTheElementWiredByHand(double f)
    {
        double[] w = [1.0e-3, 1.4e-3, 0.8e-3], gaps = [0.25e-3, 0.4e-3];
        var plan = new PassivePlan(["1", "2", "3", "4", "5", "6"],
        [
            new PlanCoupledStrips([1, 7, 2, 3, 4, 5], 0.0, 0.0, 0.0, w, gaps, 20e-3),
            new PlanLine([7, 6], 20e-3, 0.0, 0.0, 1.1e-3, 5e-3),
        ], 7);
        var model = new MicrostripPlanModel(plan, Ro4350b, "t");
        var y = new DenseMna(6);
        model.Stamp(y, new ElaboratedComponent("t", "t", [1, 2, 3, 4, 5, 6], new Dictionary<string, Value>(), model),
                    2 * Math.PI * f);
        var sMat = RFNetwork.YToS(y.Y, new Complex(50.0, 0.0));
        var section = new Complex[6, 6];
        for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) section[i, j] = sMat[i, j];

        var byHand = SAt($"""
            {Ports(6)}
            MCLINN:K1 p1 p2 p4 i7 p3 p5 N=3 W[1]={F(w[0])} W[2]={F(w[1])} W[3]={F(w[2])} S[1]={F(gaps[0])} S[2]={F(gaps[1])} L=20e-3 {Sub}
            MLIN:L1 i7 p6 W=1.1e-3 L=5e-3 {Sub}
            """, f, 6);
        double worst = Worst(section, byHand, i => i);
        Assert.True(worst < 1e-9, $"worst |dS| {worst:G3}");
    }

    /// <summary>A dense nodal admittance on nodes 1..N, ground dropped.</summary>
    private sealed class DenseMna(int n) : IMnaContext
    {
        public Mat<Complex> Y { get; } = new(n, n);

        public void AddAdmittance(int a, int b, Complex y)
        {
            AddBlockAdmittance(a, a, y);
            AddBlockAdmittance(b, b, y);
            AddBlockAdmittance(a, b, -y);
            AddBlockAdmittance(b, a, -y);
        }

        public void AddBlockAdmittance(int r, int c, Complex y)
        {
            if (r > 0 && c > 0) Y[r - 1, c - 1] += y;
        }

        public int AddBranch() => throw new NotSupportedException();
        public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo) => throw new NotSupportedException();
        public void AddConstraint(int branch, int node, Complex coeff) => throw new NotSupportedException();
        public void AddNodeBranchCoupling(int node, int branch, Complex coeff) => throw new NotSupportedException();
        public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) => throw new NotSupportedException();
        public void AddCurrentInjection(int node, Complex j) => throw new NotSupportedException();
        public void AddSourceValue(int branch, Complex value) => throw new NotSupportedException();
    }
}
